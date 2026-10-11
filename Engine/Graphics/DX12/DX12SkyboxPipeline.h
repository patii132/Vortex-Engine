#pragma once

#include "../../Common/CommonHeaders.h"
#include <d3d12.h>
#include <wrl/client.h>
#include <DirectXMath.h>

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	/// <summary>
	/// Pipeline for rendering a procedural gradient skybox.
	/// Renders a full-screen quad with a gradient from sky to horizon to ground.
	/// </summary>
	class DX12SkyboxPipeline
	{
	public:
		bool initialize(ID3D12Device* device, DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format);
		void shutdown();

		ID3D12PipelineState* pipeline_state() const { return m_pipeline_state.Get(); }
		ID3D12RootSignature* root_signature() const { return m_root_signature.Get(); }

		// Set skybox colors (linear RGB, 0-1)
		void set_colors(
			const DirectX::XMFLOAT3& sky_color,
			const DirectX::XMFLOAT3& horizon_color,
			const DirectX::XMFLOAT3& ground_color);
		
		// Set sun parameters
		void set_sun(const DirectX::XMFLOAT3& direction, const DirectX::XMFLOAT3& color, float intensity);

		// Equirect texture sky (#326): mode 1 samples the texture bound at root parameter 1 (t0).
		void set_texture_params(float mode, float exposure, float rotation_radians, float tonemap_exposure = 0.0f, float tonemapper = 0.0f)
		{
			m_constants.tonemapper = tonemapper;
			// w > 0: an HDR sky — the shader applies this scene exposure + ACES + gamma like the lit surfaces
			m_constants.params = { mode, exposure, rotation_radians, tonemap_exposure };
		}
		
		// Get constant buffer data
		const void* get_constants() const { return &m_constants; }
		size_t get_constants_size() const { return sizeof(m_constants); }

	private:
		bool compile_shaders();
		bool create_root_signature(ID3D12Device* device);
		bool create_pso(ID3D12Device* device, DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format);

		ComPtr<ID3D12PipelineState> m_pipeline_state;
		ComPtr<ID3D12RootSignature> m_root_signature;
		ComPtr<ID3DBlob> m_vs_blob;
		ComPtr<ID3DBlob> m_ps_blob;

		struct alignas(256) SkyboxConstants
		{
			DirectX::XMFLOAT4X4 inverse_view_projection;
			DirectX::XMFLOAT3 camera_position;
			float padding0;
			DirectX::XMFLOAT3 sky_color;
			float padding1;
			DirectX::XMFLOAT3 horizon_color;
			float padding2;
			DirectX::XMFLOAT3 ground_color;
			float padding3;
			DirectX::XMFLOAT3 sun_direction;
			float sun_intensity;
			DirectX::XMFLOAT3 sun_color;
			float tonemapper;   // the HDR path's curve: 0 = ACES fit, 1 = PBR Neutral
			DirectX::XMFLOAT4 params;   // x: 1 = sample the equirect texture at t0 (#326), y: exposure, z: yaw offset (radians)
		};

		SkyboxConstants m_constants{};
	};
}
