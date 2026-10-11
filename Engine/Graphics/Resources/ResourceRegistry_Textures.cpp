#include "../../Common/VerboseLog.h"
#include "ResourceRegistry_Internal.h"
#include <iterator>

namespace vortex::graphics
{
	id::id_type ResourceRegistry::create_texture(const TextureDesc& desc, const void* data)
	{
		if (!m_device) return id::invalid_id;

		auto texture = std::make_unique<Texture>();
		if (!texture->create(m_device, desc, data))
		{
			return id::invalid_id;
		}

		id::id_type id = m_next_texture_id++;
		
		assign_srv_to_texture(texture.get());
		
		m_textures[id] = std::move(texture);
		return id;
	}


	id::id_type ResourceRegistry::create_solid_color_texture(u32 color, const std::string& name)
	{
		TextureDesc desc;
		desc.width = 1;
		desc.height = 1;
		desc.format = TextureFormat::RGBA8_UNORM;
		return create_texture(desc, &color);
	}


	Texture* ResourceRegistry::get_texture(id::id_type id)
	{
		auto it = m_textures.find(id);
		return it != m_textures.end() ? it->second.get() : nullptr;
	}


	void ResourceRegistry::destroy_texture(id::id_type id)
	{
		auto it = m_textures.find(id);
		if (it == m_textures.end()) return;
		// Materials keep raw Texture pointers: unbind the dying texture everywhere first (#358).
		rebind_texture(it->second.get(), nullptr);
		// its SRV slot goes back to the free list — the heap has a fixed size and textures now come and go
		const auto cpu = it->second->srv();
		if (cpu.ptr != 0 && m_srv_heap && m_srv_descriptor_size > 0)
			m_free_srv_slots.push_back((UINT)((cpu.ptr - m_srv_heap->GetCPUDescriptorHandleForHeapStart().ptr) / m_srv_descriptor_size));
		m_textures.erase(it);
		for (auto c = m_texture_path_cache.begin(); c != m_texture_path_cache.end();)
			c = (c->second == id) ? m_texture_path_cache.erase(c) : std::next(c);
	}

	void ResourceRegistry::rebind_texture(Texture* from, Texture* to)
	{
		if (!from) return;
		for (auto& [mid, mat] : m_materials)
		{
			if (mat->albedo_texture() == from) mat->set_albedo_texture(to);
			if (mat->normal_texture() == from) mat->set_normal_texture(to);
			if (mat->metallic_texture() == from) mat->set_metallic_texture(to);
			if (mat->roughness_texture() == from) mat->set_roughness_texture(to);
			if (mat->ao_texture() == from) mat->set_ao_texture(to);
			if (mat->height_texture() == from) mat->set_height_texture(to);
		}
	}

	void ResourceRegistry::retire_stale_textures(const std::string& path, id::id_type keep)
	{
		if (path.empty()) return;
		const std::string prefix = path + "|";
		std::vector<id::id_type> stale;
		for (const auto& [key, id] : m_texture_path_cache)
			if (id != keep && key.compare(0, prefix.size(), prefix) == 0) stale.push_back(id);
		Texture* fresh = get_texture(keep);
		for (id::id_type id : stale)
		{
			rebind_texture(get_texture(id), fresh);
			destroy_texture(id);   // drops its path-cache entries too
		}
	}


	std::vector<id::id_type> ResourceRegistry::get_all_texture_ids() const
	{
		std::vector<id::id_type> ids;
		ids.reserve(m_textures.size());
		for (const auto& [id, _] : m_textures)
		{
			ids.push_back(id);
		}
		return ids;
	}


	id::id_type ResourceRegistry::create_texture_compressed(const CompressedTexture& ct, const std::string& label)
	{
		if (!m_device || ct.data.empty() || ct.mip_count == 0) return id::invalid_id;
		TextureDesc desc;
		desc.width = ct.width; desc.height = ct.height;
		desc.mip_count = ct.mip_count;
		switch (ct.format)
		{
		case BlockFormat::BC1: desc.format = TextureFormat::BC1_UNORM; break;
		case BlockFormat::BC3: desc.format = TextureFormat::BC3_UNORM; break;
		case BlockFormat::BC4: desc.format = TextureFormat::BC4_UNORM; break;
		default: desc.format = TextureFormat::BC5_UNORM; break;
		}
		VORTEX_VLOG(("texture " + label + ": " + std::to_string(ct.width) + "x" + std::to_string(ct.height) + " " + TextureCompress::format_name(ct.format)
			+ " " + std::to_string(ct.data.size() / 1024) + " KB with " + std::to_string(ct.mip_count) + " mips (RGBA8 would be "
			+ std::to_string((size_t)ct.width * ct.height * 4 * 4 / 3 / 1024) + " KB)\n").c_str());
		return create_texture(desc, ct.data.data());
	}

	id::id_type ResourceRegistry::create_texture_from_image(ImageData& image_data, const std::string& label, const std::string* cache_path)
	{
		VORTEX_VLOG(("Loaded texture: " + label + " (" +
			std::to_string(image_data.width) + "x" + std::to_string(image_data.height) + ")\n").c_str());

		std::vector<u8> rgba_pixels;
		const u8* pixel_data = image_data.pixels.data();
		
		if (image_data.format == ImageFormat::RGB8 || image_data.channels == 3)
		{
			rgba_pixels.resize(image_data.width * image_data.height * 4);
			const u8* src = image_data.pixels.data();
			for (size_t i = 0; i < image_data.width * image_data.height; i++)
			{
				rgba_pixels[i * 4 + 0] = src[i * 3 + 0];
				rgba_pixels[i * 4 + 1] = src[i * 3 + 1];
				rgba_pixels[i * 4 + 2] = src[i * 3 + 2];
				rgba_pixels[i * 4 + 3] = 255;
			}
			pixel_data = rgba_pixels.data();
			image_data.format = ImageFormat::RGBA8;
			image_data.channels = 4;
		}

		TextureDesc desc;
		desc.width = image_data.width;
		desc.height = image_data.height;
		desc.generate_mips = true;

		switch (image_data.format)
		{
		case ImageFormat::R8:
			desc.format = TextureFormat::R8_UNORM;
			break;
		case ImageFormat::RG8:
			desc.format = TextureFormat::RG8_UNORM;
			break;
		case ImageFormat::RGBA16F:
			desc.format = TextureFormat::RGBA16_FLOAT;   // an HDR sky: sampled at mip 0 by the sky pass, tonemapped there
			desc.generate_mips = false;
			break;
		case ImageFormat::RGBA8:
		default:
			desc.format = TextureFormat::RGBA8_UNORM;
			break;
		}

		// block compression (#179): a big RGBA8 picture goes to the GPU as BC1 / BC3 / BC5 with its mip chain — a quarter
		// to an eighth of the memory and bandwidth; cached on disk by file when the path is known
		if (desc.format == TextureFormat::RGBA8_UNORM && TextureCompress::eligible(image_data.width, image_data.height) && true)
		{
			CompressedTexture ct;
			const CompressKind kind = TextureCompress::kind_for(label, pixel_data, image_data.width, image_data.height);
			if (TextureCompress::compress_rgba8(pixel_data, image_data.width, image_data.height, kind, true, ct))
			{
				if (cache_path) TextureCompress::save_cached(TextureCompress::cache_key(*cache_path, CompressKind::Auto), ct);
				const id::id_type cid = create_texture_compressed(ct, label);
				if (cid != id::invalid_id) return cid;
			}
		}

		return create_texture(desc, pixel_data);
	}


	bool ResourceRegistry::create_srv_heap()
	{
		if (!m_device) return false;

		D3D12_DESCRIPTOR_HEAP_DESC heap_desc{};
		heap_desc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
		heap_desc.NumDescriptors = MAX_SRV_DESCRIPTORS;
		heap_desc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;

		if (FAILED(m_device->CreateDescriptorHeap(&heap_desc, IID_PPV_ARGS(&m_srv_heap))))
		{
			VORTEX_VLOG("Failed to create SRV heap\n");
			return false;
		}

		m_srv_descriptor_size = m_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
		m_next_srv_index = 0;

		VORTEX_VLOG("SRV heap created\n");
		return true;
	}


	bool ResourceRegistry::reserve_srv_slot(D3D12_CPU_DESCRIPTOR_HANDLE& out_cpu, D3D12_GPU_DESCRIPTOR_HANDLE& out_gpu)
	{
		if (!m_srv_heap || !m_device) return false;
		if (m_next_srv_index >= MAX_SRV_DESCRIPTORS)
		{
			VORTEX_VLOG("SRV heap full — cannot reserve external slot\n");
			return false;
		}
		out_cpu = m_srv_heap->GetCPUDescriptorHandleForHeapStart();
		out_cpu.ptr += (SIZE_T)m_next_srv_index * m_srv_descriptor_size;
		out_gpu = m_srv_heap->GetGPUDescriptorHandleForHeapStart();
		out_gpu.ptr += (UINT64)m_next_srv_index * m_srv_descriptor_size;
		m_next_srv_index++;
		return true;
	}


	void ResourceRegistry::assign_srv_to_texture(Texture* texture)
	{
		if (!texture || !m_srv_heap || !m_device) return;
		UINT slot;
		if (!m_free_srv_slots.empty() && (m_free_srv_slots.size() > 8 || m_next_srv_index >= MAX_SRV_DESCRIPTORS))
		{
			slot = m_free_srv_slots.front();
			m_free_srv_slots.pop_front();
		}
		else
		{
			if (m_next_srv_index >= MAX_SRV_DESCRIPTORS)
			{
				VORTEX_VLOG("SRV heap full\n");
				return;
			}
			slot = m_next_srv_index++;
		}

		D3D12_CPU_DESCRIPTOR_HANDLE cpu_handle = m_srv_heap->GetCPUDescriptorHandleForHeapStart();
		cpu_handle.ptr += (SIZE_T)slot * m_srv_descriptor_size;

		D3D12_GPU_DESCRIPTOR_HANDLE gpu_handle = m_srv_heap->GetGPUDescriptorHandleForHeapStart();
		gpu_handle.ptr += (UINT64)slot * m_srv_descriptor_size;

		D3D12_SHADER_RESOURCE_VIEW_DESC srv_desc{};
		srv_desc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
		srv_desc.Format = Texture::to_dxgi_format(texture->format());
		srv_desc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
		srv_desc.Texture2D.MipLevels = texture->mip_levels();
		srv_desc.Texture2D.MostDetailedMip = 0;

		m_device->CreateShaderResourceView(texture->resource(), &srv_desc, cpu_handle);
		texture->set_srv_handles(cpu_handle, gpu_handle);
	}
}
