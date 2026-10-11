#include "DX12Renderer_Internal.h"

namespace vortex::graphics::dx12
{
	bool DX12Renderer::create_skybox_resources()
	{
		auto dev = DX12Core::instance().device();

		D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_UPLOAD;
		D3D12_RESOURCE_DESC rd{}; rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
		rd.Width = 256; rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
		rd.SampleDesc.Count = 1; rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;

		if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_skybox_cb))))
			return false;
		
		D3D12_RANGE r{0,0};
		if (FAILED(m_skybox_cb->Map(0, &r, &m_skybox_cb_mapped))) return false;

		return true;
	}


	void DX12Renderer::render_skybox()
	{
		if (!m_skybox_pipeline.pipeline_state()) return;

		auto rtv = m_active_rtv;
		auto dsv = m_active_dsv;
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, &dsv);
		
		m_command_list->SetPipelineState(m_skybox_pipeline.pipeline_state());
		m_command_list->SetGraphicsRootSignature(m_skybox_pipeline.root_signature());

		using namespace DirectX;
		XMVECTOR eye = XMLoadFloat3(&m_camera_position);
		XMVECTOR at = XMLoadFloat3(&m_camera_target);
		XMVECTOR up = XMLoadFloat3(&m_camera_up);
		XMMATRIX view = XMMatrixLookAtLH(eye, at, up);
		float aspect = (float)m_active_width / (float)m_active_height;
		XMMATRIX proj = XMMatrixPerspectiveFovLH(XMConvertToRadians(m_fov_degrees), aspect, m_near_clip, m_far_clip); // MUST match the scene projection (update_per_frame_constants) or grid/sky misalign vs objects
		XMMATRIX vp = view * proj;

		// the equirect map (#326) — or the white texture, so t0 always holds a valid descriptor
		{
			auto& reg = ResourceRegistry::instance();
			Texture* sky = (m_skybox_mode == SkyboxMode::Texture && m_sky_texture != id::invalid_id) ? reg.get_texture(m_sky_texture) : nullptr;
			m_skybox_pipeline.set_texture_params(sky ? 1.0f : 0.0f, m_sky_exposure, m_sky_rotation,
				(sky && m_sky_hdr == 2) ? (m_frame_constants.scene_exposure > 0.0f ? m_frame_constants.scene_exposure : 0.5f) : ((sky && m_sky_hdr == 1) ? -1.0f : 0.0f),
				(float)m_frame_constants.tonemapper);
			if (!sky) sky = reg.get_texture(reg.default_white_texture());
			if (auto* heap = reg.srv_heap()) { ID3D12DescriptorHeap* heaps[] = { heap }; m_command_list->SetDescriptorHeaps(1, heaps); }
			if (sky && sky->srv_gpu().ptr != 0) m_command_list->SetGraphicsRootDescriptorTable(1, sky->srv_gpu());
		}

		// Update skybox constants with inverse VP matrix
		auto constants_ptr = reinterpret_cast<u8*>(m_skybox_cb_mapped);
		if (constants_ptr)
		{
			// Copy pipeline constants first
			memcpy(constants_ptr, m_skybox_pipeline.get_constants(), m_skybox_pipeline.get_constants_size());
			
			// Update inverse view projection and camera position
			XMMATRIX inv_vp = XMMatrixInverse(nullptr, vp);
			XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(constants_ptr), inv_vp);
			
			// Camera position is at offset 64 (after the 4x4 matrix)
			*reinterpret_cast<XMFLOAT3*>(constants_ptr + 64) = m_camera_position;
		}

		m_command_list->SetGraphicsRootConstantBufferView(0, m_skybox_cb->GetGPUVirtualAddress());
		m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		m_command_list->DrawInstanced(3, 1, 0, 0);
	}


	void DX12Renderer::set_skybox_colors(
		const DirectX::XMFLOAT3& sky_color,
		const DirectX::XMFLOAT3& horizon_color,
		const DirectX::XMFLOAT3& ground_color)
	{
		m_skybox_pipeline.set_colors(sky_color, horizon_color, ground_color);
		m_env_sky = sky_color; m_env_horizon = horizon_color; m_env_ground = ground_color;
	}


	void DX12Renderer::set_skybox_solid_color(const DirectX::XMFLOAT3& color)
	{
		// For solid color, set all three colors to the same value
		m_skybox_pipeline.set_colors(color, color, color);
		m_env_sky = m_env_horizon = m_env_ground = color;
	}


	void DX12Renderer::set_skybox_mode(SkyboxMode mode)
	{
		m_skybox_mode = mode;
	}


	void DX12Renderer::set_skybox_sun(const DirectX::XMFLOAT3& direction, const DirectX::XMFLOAT3& color, float intensity)
	{
		m_skybox_pipeline.set_sun(direction, color, intensity);
	}

	// The sky as a reflection (IBL step 2): the bound equirect's SRV, or a null handle while none is set / loaded.
	D3D12_GPU_DESCRIPTOR_HANDLE DX12Renderer::env_map_srv() const
	{
		if (m_env_map == id::invalid_id) return {};
		Texture* tex = ResourceRegistry::instance().get_texture(m_env_map);
		if (!tex || !tex->is_valid()) return {};
		return tex->srv_gpu();
	}
}
