#include "SdlGpuResources.h"
#include "../../Common/VerboseLog.h"
#include "../Geometry/MeshGeneratorFactory.h"
#include "../Geometry/MeshDecimator.h"
#include "../Importers/MeshSerializer.h"
#include <algorithm>
#include <filesystem>
#include <future>
#include <thread>
#include <iterator>
#include <cfloat>
#include <cmath>
#include <cstring>

namespace vortex::graphics
{
	namespace
	{
		// One-shot upload of CPU bytes into a freshly created GPU buffer (own command buffer; submitted in order
		// with every later frame, so the data is visible to the next draw that uses it).
		SDL_GPUBuffer* upload_buffer(SDL_GPUDevice* device, SDL_GPUBufferUsageFlags usage, const void* data, u32 size)
		{
			SDL_GPUBufferCreateInfo bci{};
			bci.usage = usage;
			bci.size = size;
			SDL_GPUBuffer* buffer = SDL_CreateGPUBuffer(device, &bci);
			if (!buffer) return nullptr;

			SDL_GPUTransferBufferCreateInfo tci{};
			tci.usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD;
			tci.size = size;
			SDL_GPUTransferBuffer* transfer = SDL_CreateGPUTransferBuffer(device, &tci);
			if (!transfer) { SDL_ReleaseGPUBuffer(device, buffer); return nullptr; }

			void* mapped = SDL_MapGPUTransferBuffer(device, transfer, false);
			if (!mapped) { SDL_ReleaseGPUTransferBuffer(device, transfer); SDL_ReleaseGPUBuffer(device, buffer); return nullptr; }
			std::memcpy(mapped, data, size);
			SDL_UnmapGPUTransferBuffer(device, transfer);

			SDL_GPUCommandBuffer* cmd = SDL_AcquireGPUCommandBuffer(device);
			SDL_GPUCopyPass* copy = SDL_BeginGPUCopyPass(cmd);
			SDL_GPUTransferBufferLocation src{ transfer, 0 };
			SDL_GPUBufferRegion dst{ buffer, 0, size };
			SDL_UploadToGPUBuffer(copy, &src, &dst, false);
			SDL_EndGPUCopyPass(copy);
			SDL_SubmitGPUCommandBuffer(cmd);
			SDL_ReleaseGPUTransferBuffer(device, transfer);   // deferred by SDL until the copy has run
			return buffer;
		}
	}

	// ---------------------------------------------------------------------------------------------
	// Mesh
	// ---------------------------------------------------------------------------------------------
	bool Mesh::create(SDL_GPUDevice* device, const MeshData& data)
	{
		if (data.vertices.empty()) return false;
		return create_from_vertices(device, data.vertices.data(), static_cast<u32>(data.vertices.size()),
			sizeof(VertexPosNormalUV), data.indices.empty() ? nullptr : data.indices.data(),
			static_cast<u32>(data.indices.size()));
	}

	bool Mesh::create_from_generator(SDL_GPUDevice* device, const IMeshGenerator& generator)
	{
		MeshData data = generator.generate();
		m_name = generator.type_name();
		return create(device, data);
	}

	bool Mesh::create_from_vertices(SDL_GPUDevice* device, const void* vertices, u32 vertex_count, u32 vertex_stride,
		const u32* indices, u32 index_count)
	{
		if (!device || !vertices || vertex_count == 0 || vertex_stride == 0) return false;
		const std::string keep_name = m_name;
		destroy();
		m_name = keep_name;
		m_device = device;

		m_vertex_buffer = upload_buffer(device, SDL_GPU_BUFFERUSAGE_VERTEX, vertices, vertex_count * vertex_stride);
		if (!m_vertex_buffer) return false;
		m_vertex_stride = vertex_stride;
		m_vertex_count = vertex_count;

		if (indices && index_count > 0)
		{
			m_index_buffer = upload_buffer(device, SDL_GPU_BUFFERUSAGE_INDEX, indices, index_count * sizeof(u32));
			if (!m_index_buffer) { destroy(); return false; }
			m_index_count = index_count;
		}
		return true;
	}

	void Mesh::destroy()
	{
		if (m_device)
		{
			if (m_vertex_buffer) SDL_ReleaseGPUBuffer(m_device, m_vertex_buffer);
			if (m_index_buffer) SDL_ReleaseGPUBuffer(m_device, m_index_buffer);
		}
		m_vertex_buffer = nullptr;
		m_index_buffer = nullptr;
		m_vertex_stride = 0;
		m_vertex_count = 0;
		m_index_count = 0;
		m_skinned = false;
		m_name.clear();
		m_bounds_min[0] = m_bounds_min[1] = m_bounds_min[2] = 0;
		m_bounds_max[0] = m_bounds_max[1] = m_bounds_max[2] = 1;
	}

	void Mesh::set_bounds(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
	{
		m_bounds_min[0] = minX; m_bounds_min[1] = minY; m_bounds_min[2] = minZ;
		m_bounds_max[0] = maxX; m_bounds_max[1] = maxY; m_bounds_max[2] = maxZ;
	}

	void Mesh::get_bounds(float& sizeX, float& sizeY, float& sizeZ) const
	{
		sizeX = m_bounds_max[0] - m_bounds_min[0];
		sizeY = m_bounds_max[1] - m_bounds_min[1];
		sizeZ = m_bounds_max[2] - m_bounds_min[2];
		if (sizeX < 0.001f) sizeX = 1.0f;
		if (sizeY < 0.001f) sizeY = 1.0f;
		if (sizeZ < 0.001f) sizeZ = 1.0f;
	}

	void Mesh::get_bounds_center(float& centerX, float& centerY, float& centerZ) const
	{
		centerX = (m_bounds_min[0] + m_bounds_max[0]) * 0.5f;
		centerY = (m_bounds_min[1] + m_bounds_max[1]) * 0.5f;
		centerZ = (m_bounds_min[2] + m_bounds_max[2]) * 0.5f;
	}

	// ---------------------------------------------------------------------------------------------
	// Texture
	// ---------------------------------------------------------------------------------------------
	SDL_GPUTextureFormat Texture::to_sdl_format(TextureFormat format)
	{
		switch (format)
		{
		case TextureFormat::RGBA8_UNORM: return SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM;
		case TextureFormat::RGBA8_SRGB: return SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM_SRGB;
		case TextureFormat::BGRA8_UNORM: return SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM;
		case TextureFormat::R8_UNORM: return SDL_GPU_TEXTUREFORMAT_R8_UNORM;
		case TextureFormat::RG8_UNORM: return SDL_GPU_TEXTUREFORMAT_R8G8_UNORM;
		case TextureFormat::RGBA16_FLOAT: return SDL_GPU_TEXTUREFORMAT_R16G16B16A16_FLOAT;
		case TextureFormat::RGBA32_FLOAT: return SDL_GPU_TEXTUREFORMAT_R32G32B32A32_FLOAT;
		case TextureFormat::D24_UNORM_S8_UINT: return SDL_GPU_TEXTUREFORMAT_D24_UNORM_S8_UINT;
		case TextureFormat::D32_FLOAT: return SDL_GPU_TEXTUREFORMAT_D32_FLOAT;
		default: return SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM;
		}
	}

	u32 Texture::bytes_per_pixel(TextureFormat format)
	{
		switch (format)
		{
		case TextureFormat::R8_UNORM: return 1;
		case TextureFormat::RG8_UNORM: return 2;
		case TextureFormat::RGBA16_FLOAT: return 8;
		case TextureFormat::RGBA32_FLOAT: return 16;
		default: return 4;
		}
	}

	bool Texture::create(SDL_GPUDevice* device, const TextureDesc& desc, const void* data)
	{
		if (!device || desc.width == 0 || desc.height == 0) return false;
		destroy();
		m_device = device;
		m_width = desc.width;
		m_height = desc.height;
		m_format = desc.format;

		u32 mip_count = 1;
		if (desc.generate_mips && data && !desc.is_render_target && !desc.is_depth_stencil)
		{
			u32 dim = desc.width > desc.height ? desc.width : desc.height;
			while (dim > 1) { dim >>= 1; ++mip_count; }
		}
		m_mip_levels = mip_count;

		SDL_GPUTextureCreateInfo tci{};
		tci.type = SDL_GPU_TEXTURETYPE_2D;
		tci.format = to_sdl_format(desc.format);
		tci.usage = SDL_GPU_TEXTUREUSAGE_SAMPLER;
		if (desc.is_render_target) tci.usage |= SDL_GPU_TEXTUREUSAGE_COLOR_TARGET;
		if (desc.is_depth_stencil) tci.usage = SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET;
		tci.width = desc.width;
		tci.height = desc.height;
		tci.layer_count_or_depth = 1;
		tci.num_levels = mip_count;
		tci.sample_count = SDL_GPU_SAMPLECOUNT_1;
		m_texture = SDL_CreateGPUTexture(device, &tci);
		if (!m_texture)
		{
			VORTEX_VLOG((std::string("Texture: SDL_CreateGPUTexture failed: ") + SDL_GetError() + "\n").c_str());
			return false;
		}

		if (data && !desc.is_depth_stencil && !desc.is_render_target)
		{
			const u32 bpp = bytes_per_pixel(desc.format);
			const u32 size = desc.width * desc.height * bpp;

			SDL_GPUTransferBufferCreateInfo xci{};
			xci.usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD;
			xci.size = size;
			SDL_GPUTransferBuffer* transfer = SDL_CreateGPUTransferBuffer(device, &xci);
			if (!transfer) { destroy(); return false; }
			void* mapped = SDL_MapGPUTransferBuffer(device, transfer, false);
			if (!mapped) { SDL_ReleaseGPUTransferBuffer(device, transfer); destroy(); return false; }
			std::memcpy(mapped, data, size);
			SDL_UnmapGPUTransferBuffer(device, transfer);

			SDL_GPUCommandBuffer* cmd = SDL_AcquireGPUCommandBuffer(device);
			SDL_GPUCopyPass* copy = SDL_BeginGPUCopyPass(cmd);
			SDL_GPUTextureTransferInfo src{};
			src.transfer_buffer = transfer;
			src.offset = 0;
			src.pixels_per_row = desc.width;
			src.rows_per_layer = desc.height;
			SDL_GPUTextureRegion dst{};
			dst.texture = m_texture;
			dst.w = desc.width; dst.h = desc.height; dst.d = 1;
			SDL_UploadToGPUTexture(copy, &src, &dst, false);
			SDL_EndGPUCopyPass(copy);
			if (mip_count > 1) SDL_GenerateMipmapsForGPUTexture(cmd, m_texture);
			SDL_SubmitGPUCommandBuffer(cmd);
			SDL_ReleaseGPUTransferBuffer(device, transfer);
		}
		return true;
	}

	bool Texture::create_from_color(SDL_GPUDevice* device, u32 color)
	{
		TextureDesc desc{};
		desc.width = 1; desc.height = 1;
		desc.format = TextureFormat::RGBA8_UNORM;
		return create(device, desc, &color);
	}

	void Texture::destroy()
	{
		if (m_device && m_texture) SDL_ReleaseGPUTexture(m_device, m_texture);
		m_texture = nullptr;
		m_width = m_height = 0;
		m_mip_levels = 1;
	}

	// ---------------------------------------------------------------------------------------------
	// Material
	// ---------------------------------------------------------------------------------------------
	void Material::destroy()
	{
		m_valid = false;
		m_albedo_texture = m_normal_texture = m_metallic_texture = m_roughness_texture = m_ao_texture = m_height_texture = nullptr;
	}

	// ---------------------------------------------------------------------------------------------
	// ResourceRegistry
	// ---------------------------------------------------------------------------------------------
	ResourceRegistry& ResourceRegistry::instance()
	{
		static ResourceRegistry inst;
		return inst;
	}

	void ResourceRegistry::initialize(SDL_GPUDevice* device)
	{
		if (!device) return;
		m_device = device;

		m_default_cube = create_primitive_cube(1.0f);
		m_default_sphere = create_primitive_sphere(0.5f);
		m_default_plane = create_primitive_plane(10.0f, 10.0f);
		m_default_cylinder = create_primitive_cylinder(0.5f, 1.0f);
		m_default_cone = create_primitive_cone(0.5f, 1.0f);

		m_default_white_texture = create_solid_color_texture(0xFFFFFFFF, "White");
		m_default_material = create_material("Default");
	}

	void ResourceRegistry::shutdown()
	{
		m_materials.clear();
		m_meshes.clear();
		m_textures.clear();
		m_lod_chains.clear();
		m_default_cube = m_default_sphere = m_default_plane = m_default_cylinder = m_default_cone = id::invalid_id;
		m_default_white_texture = m_default_material = id::invalid_id;
		m_device = nullptr;
	}

	id::id_type ResourceRegistry::create_mesh(const MeshData& data, const std::string& name)
	{
		if (!m_device) return id::invalid_id;
		auto mesh = std::make_unique<Mesh>();
		if (!mesh->create(m_device, data)) return id::invalid_id;
		mesh->set_name(name);
		id::id_type id = m_next_mesh_id++;
		m_meshes[id] = std::move(mesh);
		return id;
	}

	id::id_type ResourceRegistry::create_mesh_from_generator(const IMeshGenerator& generator)
	{
		if (!m_device) return id::invalid_id;
		auto mesh = std::make_unique<Mesh>();
		if (!mesh->create_from_generator(m_device, generator)) return id::invalid_id;
		id::id_type id = m_next_mesh_id++;
		m_meshes[id] = std::move(mesh);
		return id;
	}

	id::id_type ResourceRegistry::create_primitive_cube(float size)
	{
		auto generator = MeshGeneratorFactory::create_cube(size);
		return create_mesh_from_generator(*generator);
	}

	id::id_type ResourceRegistry::create_primitive_sphere(float radius, u32 slices, u32 stacks)
	{
		auto generator = MeshGeneratorFactory::create_sphere(radius, slices, stacks);
		return create_mesh_from_generator(*generator);
	}

	id::id_type ResourceRegistry::create_inverted_sphere(float radius, u32 slices, u32 stacks)
	{
		auto generator = MeshGeneratorFactory::create_sphere(radius, slices, stacks);
		std::vector<VertexPosNormalUV> vertices;
		std::vector<u32> indices;
		generator->generate(vertices, indices);
		for (auto& v : vertices) { v.normal.x = -v.normal.x; v.normal.y = -v.normal.y; v.normal.z = -v.normal.z; }
		for (size_t i = 0; i + 2 < indices.size(); i += 3) std::swap(indices[i + 1], indices[i + 2]);
		MeshData mesh_data;
		mesh_data.vertices = std::move(vertices);
		mesh_data.indices = std::move(indices);
		return create_mesh(mesh_data, "InvertedSphere");
	}

	id::id_type ResourceRegistry::create_primitive_plane(float width, float depth)
	{
		auto generator = MeshGeneratorFactory::create_plane(width, depth);
		return create_mesh_from_generator(*generator);
	}

	id::id_type ResourceRegistry::create_primitive_cylinder(float radius, float height, u32 slices)
	{
		auto generator = MeshGeneratorFactory::create_cylinder(radius, height, slices);
		return create_mesh_from_generator(*generator);
	}

	id::id_type ResourceRegistry::create_primitive_cone(float radius, float height, u32 slices)
	{
		auto generator = MeshGeneratorFactory::create_cone(radius, height, slices);
		return create_mesh_from_generator(*generator);
	}

	Mesh* ResourceRegistry::get_mesh(id::id_type id)
	{
		auto it = m_meshes.find(id);
		return it != m_meshes.end() ? it->second.get() : nullptr;
	}

	void ResourceRegistry::destroy_mesh(id::id_type id)
	{
		if (m_meshes.erase(id) > 0) ++m_mesh_generation;
		// the decimated LOD meshes belong to their base mesh and go with it (#358)
		auto chain = m_lod_chains.find(id);
		if (chain != m_lod_chains.end())
		{
			for (u32 i = 1; i < chain->second.lod_count && i < 4; ++i)
				if (chain->second.lods[i] != id::invalid_id) m_meshes.erase(chain->second.lods[i]);
			m_lod_chains.erase(chain);
		}
	}

	std::vector<id::id_type> ResourceRegistry::get_all_mesh_ids() const
	{
		std::vector<id::id_type> ids;
		ids.reserve(m_meshes.size());
		for (const auto& [id, _] : m_meshes) ids.push_back(id);
		return ids;
	}

	const ResourceRegistry::LodChain* ResourceRegistry::get_lod_chain(id::id_type base_mesh_id) const
	{
		auto it = m_lod_chains.find(base_mesh_id);
		return it != m_lod_chains.end() ? &it->second : nullptr;
	}

	id::id_type ResourceRegistry::create_texture(const TextureDesc& desc, const void* data)
	{
		if (!m_device) return id::invalid_id;
		auto texture = std::make_unique<Texture>();
		if (!texture->create(m_device, desc, data)) return id::invalid_id;
		id::id_type id = m_next_texture_id++;
		m_textures[id] = std::move(texture);
		return id;
	}

	id::id_type ResourceRegistry::create_solid_color_texture(u32 color, const std::string&)
	{
		TextureDesc desc;
		desc.width = 1; desc.height = 1;
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
		for (const auto& [id, _] : m_textures) ids.push_back(id);
		return ids;
	}

	id::id_type ResourceRegistry::create_material(const std::string& name)
	{
		if (!m_device) return id::invalid_id;
		auto material = std::make_unique<Material>();
		material->create(m_device);
		if (!name.empty()) material->set_name(name);
		id::id_type id = m_next_material_id++;
		m_materials[id] = std::move(material);
		return id;
	}

	Material* ResourceRegistry::get_material(id::id_type id)
	{
		auto it = m_materials.find(id);
		return it != m_materials.end() ? it->second.get() : nullptr;
	}

	void ResourceRegistry::destroy_material(id::id_type id) { m_materials.erase(id); }

	std::vector<id::id_type> ResourceRegistry::get_all_material_ids() const
	{
		std::vector<id::id_type> ids;
		ids.reserve(m_materials.size());
		for (const auto& [id, _] : m_materials) ids.push_back(id);
		return ids;
	}

	// ---- import ----------------------------------------------------------------------------------
	id::id_type ResourceRegistry::import_model(const std::string& filepath)
	{
		if (!m_device) return id::invalid_id;
		ImportedModelData data = ModelImporter::import_from_file(filepath);
		if (!data.is_valid()) return id::invalid_id;

		MeshData combined_data;
		u32 index_offset = 0;
		for (const auto& submesh : data.submeshes)
		{
			for (const auto& vertex : submesh.vertices) combined_data.vertices.push_back(vertex);
			for (auto idx : submesh.indices) combined_data.indices.push_back(idx + index_offset);
			index_offset += static_cast<u32>(submesh.vertices.size());
		}
		return create_mesh(combined_data, data.name);
	}

	namespace
	{
		// "<path>|<mtime>|<size>" for a texture file on disk (an edited file gets a new key), empty otherwise.
		std::string texture_cache_key(const std::string& path)
		{
			if (path.empty()) return std::string();
			std::error_code ec;
			const std::filesystem::path p(path);
			const auto size = std::filesystem::file_size(p, ec);
			if (ec) return std::string();
			const auto time = std::filesystem::last_write_time(p, ec);
			if (ec) return std::string();
			return path + "|" + std::to_string((long long)time.time_since_epoch().count()) + "|" + std::to_string((unsigned long long)size);
		}
	}

	id::id_type ResourceRegistry::import_texture(const std::string& filepath, const std::string&)
	{
		if (!m_device) return id::invalid_id;
		// Path cache: an unchanged file is uploaded once and shared. Model imports, previews and thumbnails import
		// the same maps over and over and textures are never freed, so every re-import used to cost VRAM.
		const std::string key = texture_cache_key(filepath);
		if (!key.empty())
		{
			auto it = m_texture_path_cache.find(key);
			if (it != m_texture_path_cache.end() && get_texture(it->second)) return it->second;
		}
		ImageData image_data = TextureImporter::import_from_file(filepath);
		if (!image_data.is_valid())
		{
			VORTEX_VLOG(("Failed to load texture: " + filepath + "\n").c_str());
			return id::invalid_id;
		}
		const id::id_type id = create_texture_from_image(image_data, filepath);
		if (!key.empty() && id != id::invalid_id) { m_texture_path_cache[key] = id; retire_stale_textures(filepath, id); }
		return id;
	}

	void ResourceRegistry::prefetch_textures(const std::vector<std::string>& paths)
	{
		if (!m_device) return;
		std::vector<std::pair<std::string, std::string>> todo;   // path, cache key
		for (const auto& p : paths)
		{
			if (p.empty()) continue;
			std::string key = texture_cache_key(p);
			if (key.empty()) continue;
			auto hit = m_texture_path_cache.find(key);
			if (hit != m_texture_path_cache.end() && get_texture(hit->second)) continue;
			bool dup = false;
			for (const auto& t : todo) if (t.second == key) { dup = true; break; }
			if (!dup) todo.emplace_back(p, std::move(key));
		}
		if (todo.size() < 2) return;   // nothing to overlap
		// Waves of at most `lanes` decodes: a 4K map is 64 MB of RGBA, so a big model must not decode all at once.
		const size_t lanes = (std::max)(size_t(2), (std::min)(size_t(8), size_t(std::thread::hardware_concurrency())));
		for (size_t start = 0; start < todo.size(); start += lanes)
		{
			const size_t end = (std::min)(todo.size(), start + lanes);
			std::vector<std::future<ImageData>> jobs;
			for (size_t i = start; i < end; ++i)
				jobs.push_back(std::async(std::launch::async, [path = todo[i].first] { return TextureImporter::import_from_file(path); }));
			for (size_t i = start; i < end; ++i)
			{
				ImageData image = jobs[i - start].get();
				if (!image.is_valid()) continue;
				const id::id_type id = create_texture_from_image(image, todo[i].first);
				if (id != id::invalid_id) { m_texture_path_cache[todo[i].second] = id; retire_stale_textures(todo[i].first, id); }
			}
		}
	}

	id::id_type ResourceRegistry::import_texture_from_memory(const u8* data, u64 length, const std::string& name)
	{
		if (!m_device) return id::invalid_id;
		ImageData image_data = TextureImporter::import_from_memory(data, length);
		if (!image_data.is_valid()) return id::invalid_id;
		return create_texture_from_image(image_data, name.empty() ? std::string("memtex") : name);
	}

	id::id_type ResourceRegistry::create_texture_from_image(ImageData& image_data, const std::string& label)
	{
		VORTEX_VLOG(("Loaded texture: " + label + " (" + std::to_string(image_data.width) + "x" +
			std::to_string(image_data.height) + ")\n").c_str());

		std::vector<u8> rgba_pixels;
		const u8* pixel_data = image_data.pixels.data();
		if (image_data.format == ImageFormat::RGB8 || image_data.channels == 3)
		{
			rgba_pixels.resize((size_t)image_data.width * image_data.height * 4);
			const u8* src = image_data.pixels.data();
			for (size_t i = 0; i < (size_t)image_data.width * image_data.height; i++)
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
		case ImageFormat::R8: desc.format = TextureFormat::R8_UNORM; break;
		case ImageFormat::RG8: desc.format = TextureFormat::RG8_UNORM; break;
		case ImageFormat::RGBA16F: desc.format = TextureFormat::RGBA16_FLOAT; desc.generate_mips = false; break;   // an HDR sky: mip 0 only
		default: desc.format = TextureFormat::RGBA8_UNORM; break;
		}
		return create_texture(desc, pixel_data);
	}

	ResourceRegistry::MultiMaterialImportResult ResourceRegistry::import_model_with_materials(const std::string& filepath)
	{
		MultiMaterialImportResult result;
		if (!m_device) return result;
		ImportedModelData model_data = ModelImporter::import_from_file(filepath);
		if (!model_data.is_valid()) return result;
		return build_model_result(model_data);
	}

	ResourceRegistry::MultiMaterialImportResult ResourceRegistry::import_model_with_materials_cached(
		const std::string& filepath, const std::string& cache_dir)
	{
		MultiMaterialImportResult result;
		if (!m_device) return result;
		ImportedModelData model_data = ModelImporter::import_from_file(filepath);
		if (!model_data.is_valid()) return result;
		const u32 cached = cache_dir.empty() ? 0 : MeshSerializer::save_submeshes_to_dir(model_data, cache_dir);
		result = build_model_result(model_data);
		result.cached = cached;
		return result;
	}

	ResourceRegistry::MultiMaterialImportResult ResourceRegistry::import_model_from_cache(const std::string& cache_dir)
	{
		MultiMaterialImportResult result;
		if (!m_device) return result;
		ImportedModelData model_data;
		if (!MeshSerializer::load_submeshes_from_dir(cache_dir, model_data)) return result;
		return build_model_result(model_data);
	}

	ResourceRegistry::MultiMaterialImportResult ResourceRegistry::import_model_with_materials_from_memory(
		const u8* data, u64 length, const std::string& ext_hint, const std::string& virtual_dir, bool left_handed)
	{
		MultiMaterialImportResult result;
		if (!m_device) return result;
		ImportedModelData model_data = ModelImporter::import_from_memory(data, length, ext_hint, virtual_dir, left_handed);
		if (!model_data.is_valid()) return result;
		return build_model_result(model_data);
	}

	ResourceRegistry::MultiMaterialImportResult ResourceRegistry::build_model_result(ImportedModelData& model_data)
	{
		MultiMaterialImportResult result;
		result.model_name = model_data.name;
		// Decode every map the model references in parallel up front; the per-submesh binds below hit the cache.
		{
			std::vector<std::string> maps;
			for (const auto& sm : model_data.submeshes)
			{
				maps.push_back(sm.diffuse_texture);
				if (!sm.normal_is_bump) maps.push_back(sm.normal_texture);
				maps.push_back(sm.metallic_texture); maps.push_back(sm.roughness_texture); maps.push_back(sm.ao_texture);
			}
			prefetch_textures(maps);
		}
		for (size_t i = 0; i < model_data.submeshes.size(); i++)
		{
			const auto& submesh = model_data.submeshes[i];
			SubmeshImportResult sub_result;
			sub_result.material_index = submesh.material_index;
			sub_result.name = submesh.name.empty() ? ("Submesh_" + std::to_string(i)) : submesh.name;

			sub_result.mesh_id = create_mesh_from_submesh(submesh, sub_result.name);
			if (sub_result.mesh_id == id::invalid_id) continue;
			sub_result.material_id = create_material(sub_result.name + "_material");
			if (sub_result.material_id == id::invalid_id) continue;

			auto* mat = get_material(sub_result.material_id);
			if (mat)
			{
				mat->set_base_color({ submesh.base_color[0], submesh.base_color[1], submesh.base_color[2], submesh.base_color[3] });
				mat->set_metallic(submesh.metallic);
				mat->set_roughness(submesh.roughness);
			}
			if (!submesh.diffuse_texture.empty())
			{
				sub_result.texture_id = import_texture(submesh.diffuse_texture, sub_result.name + "_tex");
				if (sub_result.texture_id != id::invalid_id && mat)
					if (auto* tex = get_texture(sub_result.texture_id)) mat->set_albedo_texture(tex);
			}
			// Every other PBR map the material references. import_texture shares an unchanged file, so a packed
			// glTF/ORM map used for metallic, roughness AND occlusion is uploaded once; the channels tell the shader
			// which component to read. Bump maps posing as normal maps (OBJ map_Bump) stay unbound.
			if (mat)
			{
				auto load = [&](const std::string& path, const char* tag) -> Texture* {
					if (path.empty()) return nullptr;
					const id::id_type tid = import_texture(path, sub_result.name + tag);
					return tid != id::invalid_id ? get_texture(tid) : nullptr;
				};
				mat->set_texture_channels(submesh.metallic_channel, submesh.roughness_channel, submesh.ao_channel);
				if (!submesh.normal_is_bump)
					if (auto* t = load(submesh.normal_texture, "_normal")) { mat->set_normal_texture(t); mat->set_use_directx_normals(!submesh.normal_opengl); }
				if (auto* t = load(submesh.metallic_texture, "_metallic")) mat->set_metallic_texture(t);
				if (auto* t = load(submesh.roughness_texture, "_roughness")) mat->set_roughness_texture(t);
				if (auto* t = load(submesh.ao_texture, "_ao")) mat->set_ao_texture(t);
			}
			result.submeshes.push_back(sub_result);
		}
		result.success = !result.submeshes.empty();
		return result;
	}

	id::id_type ResourceRegistry::create_mesh_from_submesh(const SubMeshData& submesh, const std::string& name)
	{
		if (submesh.vertices.empty()) return id::invalid_id;

		id::id_type mesh_id = id::invalid_id;
		const bool skinned = submesh.has_skin() && submesh.skin.size() == submesh.vertices.size();
		if (skinned)
		{
			std::vector<SkinnedVertexPosNormalUV> verts(submesh.vertices.size());
			for (size_t i = 0; i < submesh.vertices.size(); ++i)
			{
				const auto& v = submesh.vertices[i];
				const auto& s = submesh.skin[i];
				auto& d = verts[i];
				d.position = v.position; d.normal = v.normal; d.uv = v.uv;
				memcpy(d.bone_indices, s.bone_indices, 4);
				memcpy(d.bone_weights, s.bone_weights, 4 * sizeof(float));
			}
			auto mesh = std::make_unique<Mesh>();
			if (!mesh->create_from_vertices(m_device, verts.data(), (u32)verts.size(), sizeof(SkinnedVertexPosNormalUV),
				submesh.indices.empty() ? nullptr : submesh.indices.data(), (u32)submesh.indices.size()))
				return id::invalid_id;
			mesh->set_name(name);
			mesh->set_skinned(true);
			mesh_id = m_next_mesh_id++;
			m_meshes[mesh_id] = std::move(mesh);
		}
		else
		{
			MeshData mesh_data;
			mesh_data.vertices = submesh.vertices;
			mesh_data.indices = submesh.indices;
			mesh_id = create_mesh(mesh_data, name);
		}

		if (mesh_id != id::invalid_id)
		{
			if (auto* mesh = get_mesh(mesh_id))
			{
				float minX = FLT_MAX, minY = FLT_MAX, minZ = FLT_MAX, maxX = -FLT_MAX, maxY = -FLT_MAX, maxZ = -FLT_MAX;
				for (const auto& vertex : submesh.vertices)
				{
					minX = std::min(minX, vertex.position.x); minY = std::min(minY, vertex.position.y); minZ = std::min(minZ, vertex.position.z);
					maxX = std::max(maxX, vertex.position.x); maxY = std::max(maxY, vertex.position.y); maxZ = std::max(maxZ, vertex.position.z);
				}
				mesh->set_bounds(minX, minY, minZ, maxX, maxY, maxZ);
			}
			if (!skinned) register_lod_chain(mesh_id, submesh, name);
		}
		return mesh_id;
	}

	void ResourceRegistry::register_lod_chain(id::id_type base_mesh_id, const SubMeshData& submesh, const std::string& name)
	{
		if (base_mesh_id == id::invalid_id) return;
		if (submesh.indices.size() < 900 || submesh.vertices.size() < 300) return;

		DirectX::XMFLOAT3 bmin{ FLT_MAX, FLT_MAX, FLT_MAX }, bmax{ -FLT_MAX, -FLT_MAX, -FLT_MAX };
		for (const auto& v : submesh.vertices)
		{
			bmin.x = std::min(bmin.x, v.position.x); bmin.y = std::min(bmin.y, v.position.y); bmin.z = std::min(bmin.z, v.position.z);
			bmax.x = std::max(bmax.x, v.position.x); bmax.y = std::max(bmax.y, v.position.y); bmax.z = std::max(bmax.z, v.position.z);
		}
		const float dx = bmax.x - bmin.x, dy = bmax.y - bmin.y, dz = bmax.z - bmin.z;

		LodChain chain;
		chain.lods[0] = base_mesh_id;
		chain.lod_count = 1;
		chain.radius = 0.5f * std::sqrt(dx * dx + dy * dy + dz * dz);
		if (chain.radius < 0.0001f) chain.radius = 1.0f;

		const unsigned gridRes[3] = { 24u, 12u, 6u };
		double prevIdx = static_cast<double>(submesh.indices.size());
		for (int L = 0; L < 3 && chain.lod_count < 4; ++L)
		{
			MeshData dec = decimate_vertex_cluster(submesh.vertices, submesh.indices, bmin, bmax, gridRes[L]);
			if (!dec.is_valid() || dec.indices.size() < 3 || static_cast<double>(dec.indices.size()) > prevIdx * 0.7) continue;
			id::id_type lodId = create_mesh(dec, name + "_LOD" + std::to_string(L + 1));
			if (lodId == id::invalid_id) continue;
			if (Mesh* lm = get_mesh(lodId)) lm->set_bounds(bmin.x, bmin.y, bmin.z, bmax.x, bmax.y, bmax.z);
			chain.lods[chain.lod_count++] = lodId;
			prevIdx = static_cast<double>(dec.indices.size());
		}
		if (chain.lod_count > 1) m_lod_chains[base_mesh_id] = chain;
	}

	bool ResourceRegistry::export_mesh_to_vmesh(id::id_type, const std::string&) { return false; }

	id::id_type ResourceRegistry::load_vmesh(const std::string& filepath)
	{
		if (!m_device) return id::invalid_id;
		ImportedModelData model_data = MeshSerializer::load_from_file(filepath);
		if (!model_data.is_valid()) return id::invalid_id;
		return create_mesh_from_submesh(model_data.submeshes[0], model_data.name);
	}
}
