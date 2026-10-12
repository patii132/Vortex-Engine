#include "Texture.h"
#include "../DX12/DX12Core.h"
#include "../../Common/VerboseLog.h"
#include <wrl/client.h>
#include <cstring>
#include <vector>

namespace vortex::graphics
{
	using Microsoft::WRL::ComPtr;
	DXGI_FORMAT Texture::to_dxgi_format(TextureFormat format)
	{
		switch (format)
		{
		case TextureFormat::RGBA8_UNORM: return DXGI_FORMAT_R8G8B8A8_UNORM;
		case TextureFormat::RGBA8_SRGB: return DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
		case TextureFormat::BGRA8_UNORM: return DXGI_FORMAT_B8G8R8A8_UNORM;
		case TextureFormat::R8_UNORM: return DXGI_FORMAT_R8_UNORM;
		case TextureFormat::RG8_UNORM: return DXGI_FORMAT_R8G8_UNORM;
		case TextureFormat::RGBA16_FLOAT: return DXGI_FORMAT_R16G16B16A16_FLOAT;
		case TextureFormat::RGBA32_FLOAT: return DXGI_FORMAT_R32G32B32A32_FLOAT;
		case TextureFormat::BC1_UNORM: return DXGI_FORMAT_BC1_UNORM;
		case TextureFormat::BC3_UNORM: return DXGI_FORMAT_BC3_UNORM;
		case TextureFormat::BC4_UNORM: return DXGI_FORMAT_BC4_UNORM;
		case TextureFormat::BC5_UNORM: return DXGI_FORMAT_BC5_UNORM;
		case TextureFormat::D24_UNORM_S8_UINT: return DXGI_FORMAT_D24_UNORM_S8_UINT;
		case TextureFormat::D32_FLOAT: return DXGI_FORMAT_D32_FLOAT;
		default: return DXGI_FORMAT_R8G8B8A8_UNORM;
		}
	}

	u64 Texture::level_bytes(TextureFormat f, u32 w, u32 h)
	{
		if (is_block_format(f)) return (u64)((w + 3) / 4) * (u64)((h + 3) / 4) * ((f == TextureFormat::BC1_UNORM || f == TextureFormat::BC4_UNORM) ? 8u : 16u);
		u32 bpp = 4;
		switch (f) { case TextureFormat::R8_UNORM: bpp = 1; break; case TextureFormat::RG8_UNORM: bpp = 2; break; case TextureFormat::RGBA16_FLOAT: bpp = 8; break; case TextureFormat::RGBA32_FLOAT: bpp = 16; break; default: break; }
		return (u64)w * h * bpp;
	}

	bool Texture::create(ID3D12Device* device, const TextureDesc& desc, const void* data)
	{
		if (!device || desc.width == 0 || desc.height == 0) return false;

		destroy();

		m_width = desc.width;
		m_height = desc.height;
		m_format = desc.format;

		DXGI_FORMAT dxgi_format = to_dxgi_format(desc.format);

		u32 bytes_per_pixel = 4;   // (the upload reads the subresource row sizes; kept for the plain path's bookkeeping)
		switch (desc.format)
		{
		case TextureFormat::R8_UNORM: bytes_per_pixel = 1; break;
		case TextureFormat::RG8_UNORM: bytes_per_pixel = 2; break;
		case TextureFormat::RGBA16_FLOAT: bytes_per_pixel = 8; break;
		case TextureFormat::RGBA32_FLOAT: bytes_per_pixel = 16; break;
		default: break;
		}

		// CPU box-filter mipgen only supports 4-byte UNORM data
		const bool can_mip =
			desc.format == TextureFormat::RGBA8_UNORM ||
			desc.format == TextureFormat::RGBA8_SRGB ||
			desc.format == TextureFormat::BGRA8_UNORM;

		u32 mip_count = 1;
		const bool provided = desc.mip_count > 1 && data != nullptr;   // the caller built the chain (block formats, #179)
		if (provided) mip_count = desc.mip_count;
		else if (desc.generate_mips && data && can_mip && !desc.is_render_target && !desc.is_depth_stencil)
		{
			u32 dim = desc.width > desc.height ? desc.width : desc.height;
			while (dim > 1) { dim >>= 1; ++mip_count; }
		}
		m_mip_levels = mip_count;

		D3D12_RESOURCE_DESC res_desc{};
		res_desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
		res_desc.Width = desc.width;
		res_desc.Height = desc.height;
		res_desc.DepthOrArraySize = 1;
		res_desc.MipLevels = (UINT16)mip_count;
		res_desc.Format = dxgi_format;
		res_desc.SampleDesc.Count = 1;
		res_desc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;

		D3D12_RESOURCE_FLAGS flags = D3D12_RESOURCE_FLAG_NONE;
		if (desc.is_render_target)
			flags |= D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
		if (desc.is_depth_stencil)
			flags |= D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL;
		res_desc.Flags = flags;

		D3D12_HEAP_PROPERTIES heap_props{};
		heap_props.Type = D3D12_HEAP_TYPE_DEFAULT;

		D3D12_RESOURCE_STATES initial_state = D3D12_RESOURCE_STATE_COPY_DEST;
		if (desc.is_depth_stencil)
			initial_state = D3D12_RESOURCE_STATE_DEPTH_WRITE;
		else if (desc.is_render_target)
			initial_state = D3D12_RESOURCE_STATE_RENDER_TARGET;

		if (FAILED(device->CreateCommittedResource(&heap_props, D3D12_HEAP_FLAG_NONE,
			&res_desc, initial_state, nullptr, IID_PPV_ARGS(&m_resource))))
		{
			return false;
		}

		if (data && !desc.is_depth_stencil && !desc.is_render_target)
		{
			// Build the CPU mip chain (2x2 box filter; floor semantics so level sizes
			// match D3D12's subresource footprints exactly, incl. non-power-of-two)
			std::vector<std::vector<u8>> mip_pixels;
			std::vector<const u8*> level_data(mip_count);
			std::vector<u32> level_w(mip_count), level_h(mip_count);
			level_data[0] = static_cast<const u8*>(data);
			level_w[0] = desc.width;
			level_h[0] = desc.height;
			if (provided)
			{
				u64 off = 0; u32 lw = desc.width, lh = desc.height;
				for (u32 m = 0; m < mip_count; ++m)
				{
					level_data[m] = static_cast<const u8*>(data) + off; level_w[m] = lw; level_h[m] = lh;
					off += level_bytes(desc.format, lw, lh);
					lw = lw > 1 ? lw >> 1 : 1; lh = lh > 1 ? lh >> 1 : 1;
				}
			}
			for (u32 m = 1; m < mip_count && !provided; ++m)
			{
				const u32 sw = level_w[m - 1], sh = level_h[m - 1];
				const u32 dw = sw > 1 ? sw >> 1 : 1;
				const u32 dh = sh > 1 ? sh >> 1 : 1;
				std::vector<u8> dst_pixels((size_t)dw * dh * 4);
				const u8* src_px = level_data[m - 1];
				for (u32 y = 0; y < dh; ++y)
				{
					const u32 sy0 = y * 2;
					const u32 sy1 = (sy0 + 1 < sh) ? sy0 + 1 : sy0;
					for (u32 x = 0; x < dw; ++x)
					{
						const u32 sx0 = x * 2;
						const u32 sx1 = (sx0 + 1 < sw) ? sx0 + 1 : sx0;
						const u8* p00 = src_px + ((size_t)sy0 * sw + sx0) * 4;
						const u8* p01 = src_px + ((size_t)sy0 * sw + sx1) * 4;
						const u8* p10 = src_px + ((size_t)sy1 * sw + sx0) * 4;
						const u8* p11 = src_px + ((size_t)sy1 * sw + sx1) * 4;
						u8* out = dst_pixels.data() + ((size_t)y * dw + x) * 4;
						out[0] = (u8)((p00[0] + p01[0] + p10[0] + p11[0] + 2) >> 2);
						out[1] = (u8)((p00[1] + p01[1] + p10[1] + p11[1] + 2) >> 2);
						out[2] = (u8)((p00[2] + p01[2] + p10[2] + p11[2] + 2) >> 2);
						out[3] = (u8)((p00[3] + p01[3] + p10[3] + p11[3] + 2) >> 2);
					}
				}
				level_w[m] = dw;
				level_h[m] = dh;
				mip_pixels.push_back(std::move(dst_pixels));
				level_data[m] = mip_pixels.back().data();
			}

			std::vector<D3D12_PLACED_SUBRESOURCE_FOOTPRINT> footprints(mip_count);
			std::vector<UINT> num_rows(mip_count);
			std::vector<UINT64> row_sizes(mip_count);
			UINT64 upload_size = 0;
			device->GetCopyableFootprints(&res_desc, 0, mip_count, 0,
				footprints.data(), num_rows.data(), row_sizes.data(), &upload_size);

			D3D12_HEAP_PROPERTIES upload_heap{};
			upload_heap.Type = D3D12_HEAP_TYPE_UPLOAD;

			D3D12_RESOURCE_DESC upload_desc{};
			upload_desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
			upload_desc.Width = upload_size;
			upload_desc.Height = 1;
			upload_desc.DepthOrArraySize = 1;
			upload_desc.MipLevels = 1;
			upload_desc.Format = DXGI_FORMAT_UNKNOWN;
			upload_desc.SampleDesc.Count = 1;
			upload_desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;

			if (FAILED(device->CreateCommittedResource(&upload_heap, D3D12_HEAP_FLAG_NONE,
				&upload_desc, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_upload_buffer))))
			{
				return false;
			}

			void* mapped = nullptr;
			D3D12_RANGE range{ 0, 0 };
			if (SUCCEEDED(m_upload_buffer->Map(0, &range, &mapped)))
			{
				for (u32 m = 0; m < mip_count; ++m)
				{
					const size_t src_row_pitch = (size_t)row_sizes[m];   // bytes per row of the subresource (a block row for BCn)
					u8* dst = static_cast<u8*>(mapped) + footprints[m].Offset;
					const u8* src = level_data[m];
					for (UINT row = 0; row < num_rows[m]; ++row)
					{
						std::memcpy(dst + (size_t)row * footprints[m].Footprint.RowPitch,
						            src + (size_t)row * src_row_pitch,
						            src_row_pitch);
					}
				}
				m_upload_buffer->Unmap(0, nullptr);

				// Note: The actual GPU copy is deferred - caller must execute copy commands
				// For now, we'll do an immediate copy using the DX12Core command queue
				VORTEX_VLOG("Texture data uploaded to staging buffer\n");

				// Create a temporary command list to copy texture data
				ComPtr<ID3D12CommandAllocator> cmd_alloc;
				ComPtr<ID3D12GraphicsCommandList> cmd_list;
				ComPtr<ID3D12CommandQueue> cmd_queue;
				ComPtr<ID3D12Fence> fence;

				if (SUCCEEDED(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&cmd_alloc))) &&
					SUCCEEDED(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, cmd_alloc.Get(), nullptr, IID_PPV_ARGS(&cmd_list))))
				{
					D3D12_COMMAND_QUEUE_DESC queue_desc{};
					queue_desc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
					
					if (SUCCEEDED(device->CreateCommandQueue(&queue_desc, IID_PPV_ARGS(&cmd_queue))) &&
						SUCCEEDED(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence))))
					{
						// Copy every mip from the upload buffer to the GPU texture
						for (u32 m = 0; m < mip_count; ++m)
						{
							D3D12_TEXTURE_COPY_LOCATION dst_loc{};
							dst_loc.pResource = m_resource.Get();
							dst_loc.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
							dst_loc.SubresourceIndex = m;

							D3D12_TEXTURE_COPY_LOCATION src_loc{};
							src_loc.pResource = m_upload_buffer.Get();
							src_loc.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
							src_loc.PlacedFootprint = footprints[m];

							cmd_list->CopyTextureRegion(&dst_loc, 0, 0, 0, &src_loc, nullptr);
						}

						// Transition texture to shader resource state
						D3D12_RESOURCE_BARRIER barrier{};
						barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
						barrier.Transition.pResource = m_resource.Get();
						barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
						barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
						barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
						cmd_list->ResourceBarrier(1, &barrier);

						cmd_list->Close();

						// Execute and wait
						ID3D12CommandList* lists[] = { cmd_list.Get() };
						cmd_queue->ExecuteCommandLists(1, lists);
						cmd_queue->Signal(fence.Get(), 1);

						HANDLE event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
						fence->SetEventOnCompletion(1, event);
						WaitForSingleObject(event, INFINITE);
						CloseHandle(event);

						// Upload is fully synchronous — the staging buffer is dead weight after this
						m_upload_buffer.Reset();

						VORTEX_VLOG("Texture GPU copy completed\n");
					}
				}
			}
		}

		return true;
	}

	bool Texture::create_from_color(ID3D12Device* device, u32 color)
	{
		TextureDesc desc{};
		desc.width = 1;
		desc.height = 1;
		desc.format = TextureFormat::RGBA8_UNORM;

		return create(device, desc, &color);
	}

	void Texture::destroy()
	{
		m_resource.Reset();
		m_upload_buffer.Reset();
		m_srv = {};
		m_srv_gpu = {};
		m_width = 0;
		m_height = 0;
		m_mip_levels = 1;
	}

	void Texture::set_srv_handles(D3D12_CPU_DESCRIPTOR_HANDLE cpu, D3D12_GPU_DESCRIPTOR_HANDLE gpu)
	{
		m_srv = cpu;
		m_srv_gpu = gpu;
	}
}
