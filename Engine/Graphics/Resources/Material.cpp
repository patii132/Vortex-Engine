#include "Material.h"
#include <cstring>

namespace vortex::graphics
{
	bool Material::create(ID3D12Device* device)
	{
		if (!device) return false;

		destroy();

		const UINT buffer_size = (sizeof(MaterialProperties) + 255) & ~255;

		D3D12_HEAP_PROPERTIES heap_props{};
		heap_props.Type = D3D12_HEAP_TYPE_UPLOAD;

		D3D12_RESOURCE_DESC res_desc{};
		res_desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
		res_desc.Width = buffer_size;
		res_desc.Height = 1;
		res_desc.DepthOrArraySize = 1;
		res_desc.MipLevels = 1;
		res_desc.Format = DXGI_FORMAT_UNKNOWN;
		res_desc.SampleDesc.Count = 1;
		res_desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;

		if (FAILED(device->CreateCommittedResource(&heap_props, D3D12_HEAP_FLAG_NONE,
			&res_desc, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_constant_buffer))))
		{
			return false;
		}

		D3D12_RANGE range{ 0, 0 };
		if (FAILED(m_constant_buffer->Map(0, &range, &m_mapped_data)))
		{
			m_constant_buffer.Reset();
			return false;
		}

		update_gpu_data();
		return true;
	}

	void Material::destroy()
	{
		if (m_constant_buffer && m_mapped_data)
		{
			m_constant_buffer->Unmap(0, nullptr);
			m_mapped_data = nullptr;
		}
		m_constant_buffer.Reset();
		m_albedo_texture = nullptr;
		m_normal_texture = nullptr;
		m_metallic_texture = nullptr;
		m_roughness_texture = nullptr;
		m_ao_texture = nullptr;
	}

	void Material::set_base_color(const DirectX::XMFLOAT4& color)
	{
		m_properties.base_color = color;
		update_gpu_data();
	}

	void Material::set_metallic(float value)
	{
		m_properties.metallic = value;
		update_gpu_data();
	}

	void Material::set_roughness(float value)
	{
		m_properties.roughness = value;
		update_gpu_data();
	}

	void Material::set_ao(float value)
	{
		m_properties.ao = value;
		update_gpu_data();
	}

	void Material::set_normal_strength(float value)
	{
		m_properties.normal_strength = value;
		update_gpu_data();
	}

	void Material::set_use_directx_normals(bool use_directx)
	{
		m_properties.use_directx_normals = use_directx ? 1 : 0;
		update_gpu_data();
	}

	void Material::set_unlit(bool is_unlit)
	{
		m_properties.is_unlit = is_unlit ? 1 : 0;
		update_gpu_data();
	}

	void Material::set_emissive_strength(float strength)
	{
		m_properties.emissive_strength = strength;
		update_gpu_data();
	}

	void Material::set_uv_tiling(float u, float v)
	{
		m_properties.uv_tiling = { u, v };
		update_gpu_data();
	}

	void Material::set_albedo_texture(Texture* texture)
	{
		m_albedo_texture = texture;
		m_properties.has_albedo_texture = (texture && texture->is_valid()) ? 1 : 0;
		update_gpu_data();
	}

	void Material::set_normal_texture(Texture* texture)
	{
		m_normal_texture = texture;
		m_properties.has_normal_texture = (texture && texture->is_valid()) ? (texture->format() == TextureFormat::BC5_UNORM ? 2u : 1u) : 0u;   // 2 = two-channel map, the shader rebuilds z
		update_gpu_data();
	}

	void Material::set_metallic_texture(Texture* texture)
	{
		m_metallic_texture = texture;
		m_properties.has_metallic_texture = (texture && texture->is_valid()) ? 1 + m_metallic_channel : 0;
		update_gpu_data();
	}

	void Material::set_roughness_texture(Texture* texture)
	{
		m_roughness_texture = texture;
		m_properties.has_roughness_texture = (texture && texture->is_valid()) ? 1 + m_roughness_channel : 0;
		update_gpu_data();
	}

	void Material::set_ao_texture(Texture* texture)
	{
		m_ao_texture = texture;
		m_properties.has_ao_texture = (texture && texture->is_valid()) ? 1 + m_ao_channel : 0;
		update_gpu_data();
	}

	void Material::set_texture_channels(u32 metallic, u32 roughness, u32 ao)
	{
		m_metallic_channel = metallic & 3u; m_roughness_channel = roughness & 3u; m_ao_channel = ao & 3u;
		if (m_properties.has_metallic_texture) m_properties.has_metallic_texture = 1 + m_metallic_channel;
		if (m_properties.has_roughness_texture) m_properties.has_roughness_texture = 1 + m_roughness_channel;
		if (m_properties.has_ao_texture) m_properties.has_ao_texture = 1 + m_ao_channel;
		update_gpu_data();
	}

	void Material::set_height_texture(Texture* texture)
	{
		m_height_texture = texture;
		update_gpu_data();
	}

	void Material::set_height_scale(float value)
	{
		m_properties.height_scale = value;
		update_gpu_data();
	}

	void Material::set_alpha_cutoff(float cutoff)
	{
		m_properties.alpha_cutoff = cutoff < 0.0f ? 0.0f : cutoff;
		update_gpu_data();
	}

	void Material::update_gpu_data()
	{
		if (m_mapped_data)
		{
			std::memcpy(m_mapped_data, &m_properties, sizeof(MaterialProperties));
		}
	}
}
