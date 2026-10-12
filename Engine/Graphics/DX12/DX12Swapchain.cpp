#include "DX12Swapchain.h"
#include "../../Common/Platform.h"
#include <cstdio>

namespace vortex::graphics::dx12
{
	bool DX12Swapchain::initialize(IDXGIFactory4* factory, ID3D12CommandQueue* queue,
								   ID3D12Device* device, const SwapchainDesc& desc)
	{
		if (!factory || !queue || !device || !desc.hwnd) return false;
		if (desc.width == 0 || desc.height == 0) return false;

		m_device = device;
		m_width = desc.width;
		m_height = desc.height;
		m_buffer_count = desc.buffer_count;
		m_format = desc.format;

		DXGI_SWAP_CHAIN_DESC1 scDesc{};
		scDesc.BufferCount = m_buffer_count;
		scDesc.Width = m_width;
		scDesc.Height = m_height;
		scDesc.Format = m_format;
		scDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
		scDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
		scDesc.SampleDesc.Count = 1;

		// Uncap past monitor refresh: a windowed flip-model swapchain stays pinned to the compositor
		// refresh unless created (and presented) with ALLOW_TEARING. Query support once.
		{
			BOOL tearing = FALSE;
			ComPtr<IDXGIFactory5> factory5;
			if (SUCCEEDED(factory->QueryInterface(IID_PPV_ARGS(&factory5))))
				factory5->CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING, &tearing, sizeof(tearing));
			m_allow_tearing = (tearing == TRUE);
			if (m_allow_tearing) scDesc.Flags |= DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING;
		}

		ComPtr<IDXGISwapChain1> swapchain1;
		if (FAILED(factory->CreateSwapChainForHwnd(queue, desc.hwnd, &scDesc, nullptr, nullptr, &swapchain1)))
			return false;

		if (FAILED(swapchain1.As(&m_swapchain)))
			return false;

		factory->MakeWindowAssociation(desc.hwnd, DXGI_MWA_NO_ALT_ENTER);

		if (!create_rtv_heap(device)) return false;
		if (!create_back_buffer_rtvs(device)) return false;

		m_current_index = m_swapchain->GetCurrentBackBufferIndex();
		return true;
	}

	void DX12Swapchain::shutdown()
	{
		release_back_buffers();
		m_rtv_heap.Reset();
		m_swapchain.Reset();
		m_device = nullptr;
	}

	bool DX12Swapchain::resize(u32 width, u32 height)
	{
		if (width == 0 || height == 0) return false;
		if (!m_swapchain || !m_device) return false;

		release_back_buffers();

		// ResizeBuffers MUST carry the same ALLOW_TEARING flag the swapchain was created with,
		// otherwise it returns DXGI_ERROR_INVALID_CALL.
		UINT rb_flags = m_allow_tearing ? DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING : 0;
		if (FAILED(m_swapchain->ResizeBuffers(m_buffer_count, width, height, m_format, rb_flags)))
			return false;

		m_width = width;
		m_height = height;

		if (!create_back_buffer_rtvs(m_device)) return false;

		m_current_index = m_swapchain->GetCurrentBackBufferIndex();
		return true;
	}

	void DX12Swapchain::present(bool vsync)
	{
		// ALLOW_TEARING present flag is illegal with a non-zero sync interval (returns
		// DXGI_ERROR_INVALID_CALL), so gate it strictly on !vsync.
		UINT sync = vsync ? 1 : 0;
		UINT flags = (!vsync && m_allow_tearing) ? DXGI_PRESENT_ALLOW_TEARING : 0;
		HRESULT hr = m_swapchain->Present(sync, flags);
		if (hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET)
		{
			// the GPU driver reset or crashed (a TDR, an overheating card, a driver update under a running editor): say so
			// once with the reason, so the crash report names the cause instead of a later access violation
			static bool reported = false;
			if (!reported)
			{
				reported = true;
				HRESULT reason = hr;
				ComPtr<ID3D12Device> dev;
				if (SUCCEEDED(m_swapchain->GetDevice(IID_PPV_ARGS(&dev))) && dev) reason = dev->GetDeviceRemovedReason();
				char msg[160];
				snprintf(msg, sizeof(msg), "[dx12] THE GPU DEVICE WAS LOST (Present 0x%08X, reason 0x%08X) - the driver reset; restart the editor\n", (unsigned)hr, (unsigned)reason);
				platform::debug_output(msg);
			}
		}
		m_current_index = m_swapchain->GetCurrentBackBufferIndex();
	}

	ID3D12Resource* DX12Swapchain::current_back_buffer() const
	{
		return m_back_buffers[m_current_index].Get();
	}

	D3D12_CPU_DESCRIPTOR_HANDLE DX12Swapchain::current_rtv() const
	{
		D3D12_CPU_DESCRIPTOR_HANDLE handle = m_rtv_heap->GetCPUDescriptorHandleForHeapStart();
		handle.ptr += m_current_index * m_rtv_descriptor_size;
		return handle;
	}

	bool DX12Swapchain::create_rtv_heap(ID3D12Device* device)
	{
		D3D12_DESCRIPTOR_HEAP_DESC desc{};
		desc.NumDescriptors = m_buffer_count;
		desc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
		desc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_NONE;

		if (FAILED(device->CreateDescriptorHeap(&desc, IID_PPV_ARGS(&m_rtv_heap))))
			return false;

		m_rtv_descriptor_size = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
		return true;
	}

	bool DX12Swapchain::create_back_buffer_rtvs(ID3D12Device* device)
	{
		D3D12_CPU_DESCRIPTOR_HANDLE rtvHandle = m_rtv_heap->GetCPUDescriptorHandleForHeapStart();

		for (u32 i = 0; i < m_buffer_count; ++i)
		{
			if (FAILED(m_swapchain->GetBuffer(i, IID_PPV_ARGS(&m_back_buffers[i]))))
				return false;

			device->CreateRenderTargetView(m_back_buffers[i].Get(), nullptr, rtvHandle);
			rtvHandle.ptr += m_rtv_descriptor_size;
		}
		return true;
	}

	void DX12Swapchain::release_back_buffers()
	{
		for (u32 i = 0; i < MaxBufferCount; ++i)
		{
			m_back_buffers[i].Reset();
		}
	}
}
