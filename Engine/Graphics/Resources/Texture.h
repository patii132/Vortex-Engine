#pragma once

#include "../../Common/CommonHeaders.h"
#include <d3d12.h>
#include <wrl/client.h>
#include <string>

namespace vortex::graphics
{
	using Microsoft::WRL::ComPtr;

	enum class TextureFormat
	{
		RGBA8_UNORM,
		RGBA8_SRGB,
		BGRA8_UNORM,
		R8_UNORM,
		RG8_UNORM,
		RGBA16_FLOAT,
		RGBA32_FLOAT,
		BC1_UNORM,         // block compressed (#179): colour, 8 bytes per 4x4
		BC3_UNORM,         // colour + alpha, 16 bytes per 4x4
		BC4_UNORM,         // one channel, 8 bytes per 4x4
		BC5_UNORM,         // two channels (normal x / y), 16 bytes per 4x4
		D24_UNORM_S8_UINT,
		D32_FLOAT
	};

	struct TextureDesc
	{
		u32 width{ 0 };
		u32 height{ 0 };
		TextureFormat format{ TextureFormat::RGBA8_UNORM };
		bool generate_mips{ false };
		u32 mip_count{ 0 };        // > 1: data holds this many levels, tightly packed, level 0 first (block formats come this way)
		bool is_render_target{ false };
		bool is_depth_stencil{ false };
	};

	class Texture
	{
	public:
		Texture() = default;
		~Texture() = default;

		bool create(ID3D12Device* device, const TextureDesc& desc, const void* data = nullptr);
		bool create_from_color(ID3D12Device* device, u32 color);
		void destroy();

		ID3D12Resource* resource() const { return m_resource.Get(); }
		D3D12_CPU_DESCRIPTOR_HANDLE srv() const { return m_srv; }
		D3D12_GPU_DESCRIPTOR_HANDLE srv_gpu() const { return m_srv_gpu; }

		u32 width() const { return m_width; }
		u32 height() const { return m_height; }
		u32 mip_levels() const { return m_mip_levels; }
		TextureFormat format() const { return m_format; }
		bool is_valid() const { return m_resource != nullptr; }

		void set_srv_handles(D3D12_CPU_DESCRIPTOR_HANDLE cpu, D3D12_GPU_DESCRIPTOR_HANDLE gpu);

		static DXGI_FORMAT to_dxgi_format(TextureFormat format);
		static bool is_block_format(TextureFormat f) { return f == TextureFormat::BC1_UNORM || f == TextureFormat::BC3_UNORM || f == TextureFormat::BC4_UNORM || f == TextureFormat::BC5_UNORM; }
		static u64 level_bytes(TextureFormat f, u32 w, u32 h);

	private:
		ComPtr<ID3D12Resource> m_resource;
		ComPtr<ID3D12Resource> m_upload_buffer;
		D3D12_CPU_DESCRIPTOR_HANDLE m_srv{};
		D3D12_GPU_DESCRIPTOR_HANDLE m_srv_gpu{};
		u32 m_width{ 0 };
		u32 m_height{ 0 };
		u32 m_mip_levels{ 1 };
		TextureFormat m_format{ TextureFormat::RGBA8_UNORM };
	};
}
