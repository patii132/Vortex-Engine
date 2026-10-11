#pragma once

// ============================================================================
// DX12Particles — draws the particle module's output (Graphics/Particles) on the DirectX 12 backend (#117); the
// twin of SdlGpuParticles, fed by particles.hlsl.
//
// Per view: prepare() gathers the world's visible emitters / trails / beams (culled, alpha batches sorted
// back-to-front) into this frame's upload ring; draw_layer() renders one layer right after that layer's meshes
// with the pass's depth buffer bound for the depth test (no depth writes) and a copy of it sampled for the
// soft-particle fade. Layer 0 uses the world projection, layer 1 the first-person viewmodel projection against
// the viewmodel's own depth.
// Collision: capture_depth() writes a downsampled linear depth of the main view into a readback ring;
// begin_frame() hands the newest finished slot (>= 3 frames old, i.e. retired by the frame fence) to the
// simulation.
// GPU memory an in-flight frame may still read (grown rings, resized textures) is retired through a graveyard
// a few frames later; render_scene_to_target() runs against an idle GPU and simply shares the current ring.
// ============================================================================
#include "../../Common/CommonHeaders.h"
#include "../Particles/ParticleSystem.h"
#include "DX12FrameRing.h"
#include <d3d12.h>
#include <wrl/client.h>
#include <DirectXMath.h>
#include <utility>
#include <vector>

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	class DX12Particles
	{
	public:
		// What the renderer knows about one view.
		struct View
		{
			DirectX::XMFLOAT4X4 view_projection;         // layer 0
			DirectX::XMFLOAT4X4 viewmodel_projection;    // layer 1
			DirectX::XMFLOAT3 eye, right, up, forward;
			float near_clip{ 0.1f }, far_clip{ 1000.0f };
			float vm_near_clip{ 0.01f }, vm_far_clip{ 200.0f };   // layer 1 (viewmodel projection) depth range
			bool ortho{ false };
			float tan_half_x{ 1.0f }, tan_half_y{ 1.0f };   // perspective: tan(fov/2)*aspect, tan(fov/2); ortho: half extents
		};
		// Scene lighting / fog snapshot for lit + fogged particles. The point / spot lights themselves are the
		// renderer's light constant buffer (its first 1024 bytes), passed to draw_layer by GPU address.
		struct Environment
		{
			DirectX::XMFLOAT3 fog_color{ 0, 0, 0 }; float fog_density{ 0 };
			float fog_height_y{ 0 }, fog_height_falloff{ 0 };
			DirectX::XMFLOAT3 sun_direction{ 0.3f, -1.0f, 0.5f }; float sun_intensity{ 1 };
			DirectX::XMFLOAT3 sun_color{ 1, 1, 1 }; float ambient{ 0.3f };
			u32 point_lights{ 0 }, spot_lights{ 0 };
		};

		bool initialize(ID3D12Device* device, DX12FrameRing* ring, DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format);
		void shutdown();
		bool ready() const { return m_ready; }

		// Offscreen targets draw no particles unless the caller names a world for the NEXT render (one-shot).
		void set_next_target_world(int world) { m_next_target_world = world; }
		int consume_target_world() { int w = m_next_target_world; m_next_target_world = -1; return w; }

		// Once per renderer frame, right after the frame fence wait: rotates the upload ring, retires old GPU
		// memory and hands the newest finished collision snapshot to the simulation.
		void begin_frame();

		// Gather + upload for one view (before its scene pass). True when this view has particles to draw.
		bool prepare(const View& view, u32 world);
		bool has_layer(u32 layer) const { return layer < 2 && m_layer_batches[layer] > 0; }

		// Draw one layer into the target of the current pass: `depth` is the resource behind `dsv` (DEPTH_WRITE
		// on entry and exit; it is copied for the soft fade). `lights` is the renderer's light constant buffer.
		void draw_layer(ID3D12GraphicsCommandList* cmd, D3D12_CPU_DESCRIPTOR_HANDLE rtv, D3D12_CPU_DESCRIPTOR_HANDLE dsv,
			ID3D12Resource* depth, u32 w, u32 h, u32 layer, const View& view, const Environment& env,
			D3D12_GPU_VIRTUAL_ADDRESS lights);

		// Collision snapshot of the main view (after the world layer's depth is complete, before the viewmodel pass
		// clears it). Leaves `rtv` / `dsv` and the full-target viewport bound again.
		void capture_depth(ID3D12GraphicsCommandList* cmd, ID3D12Resource* depth, u32 w, u32 h, const View& view,
			D3D12_CPU_DESCRIPTOR_HANDLE rtv, D3D12_CPU_DESCRIPTOR_HANDLE dsv);

		// A copy of the pass's depth buffer as a shader resource for other effect passes (decals, #120); {} when unavailable.
		D3D12_GPU_DESCRIPTOR_HANDLE scene_depth_srv(ID3D12GraphicsCommandList* cmd, ID3D12Resource* depth);
		// A copy of the pass's COLOUR target so far (same format), sampled by the water for refraction / SSR (#200).
		// `color` must be in RENDER_TARGET state; it is returned to it.
		D3D12_GPU_DESCRIPTOR_HANDLE scene_color_srv(ID3D12GraphicsCommandList* cmd, ID3D12Resource* color);

	private:
		static constexpr u32 SNAP_SLOTS = 4;
		static constexpr u32 DEPTH_COPIES = 4;    // distinct target sizes per frame (main view + previews)

		struct DepthCopy
		{
			ComPtr<ID3D12Resource> texture;      // R32_TYPELESS copy of a pass's depth, sampled as R32_FLOAT
			u32 w{ 0 }, h{ 0 };
			u64 last_used{ 0 };
			D3D12_RESOURCE_STATES state{ D3D12_RESOURCE_STATE_COPY_DEST };
			D3D12_CPU_DESCRIPTOR_HANDLE cpu{};
			D3D12_GPU_DESCRIPTOR_HANDLE gpu{};
			bool slot{ false };                  // SRV slot reserved in the registry heap (never freed; reused in place)
			DXGI_FORMAT format{ DXGI_FORMAT_UNKNOWN };   // colour copies only: the source's format
		};
		struct Snap
		{
			ComPtr<ID3D12Resource> readback;
			u32 w{ 0 }, h{ 0 }, pitch{ 0 };
			u64 bytes{ 0 };
			u64 frame{ 0 };
			bool pending{ false };
			particles::DepthView view{};
		};

		bool create_root_signature();
		bool create_pipelines(DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format);
		DepthCopy* copy_depth(ID3D12GraphicsCommandList* cmd, ID3D12Resource* depth);
		DepthCopy* copy_color(ID3D12GraphicsCommandList* cmd, ID3D12Resource* color);
		bool ensure_snap_target(u32 w, u32 h);

		ID3D12Device* m_device{ nullptr };
		bool m_ready{ false };
		ComPtr<ID3D12RootSignature> m_root_signature;
		ComPtr<ID3DBlob> m_vs_particle, m_vs_ribbon, m_ps_particle, m_vs_snap, m_ps_snap;
		ComPtr<ID3D12PipelineState> m_billboard[3];   // by blend mode
		ComPtr<ID3D12PipelineState> m_ribbon[3];
		ComPtr<ID3D12PipelineState> m_snap;

		DX12FrameRing* m_ring{ nullptr };   // the renderer's per-frame upload ring (shared with the decal pass)

		particles::DrawList m_list;
		u32 m_layer_batches[2]{ 0, 0 };
		int m_next_target_world{ -1 };
		DX12FrameRing::Upload m_instances{};                        // this view's GpuParticle array (root SRV t2)
		D3D12_VERTEX_BUFFER_VIEW m_ribbon_vbv{};
		D3D12_INDEX_BUFFER_VIEW m_ribbon_ibv{};

		DepthCopy m_depth_copies[DEPTH_COPIES];
		DepthCopy m_color_copies[2];   // the colour target so far, per size/format (#200)

		// collision depth snapshot: a small R32_FLOAT target + a readback ring
		ComPtr<ID3D12Resource> m_snap_texture;
		ComPtr<ID3D12DescriptorHeap> m_snap_rtv_heap;
		D3D12_RESOURCE_STATES m_snap_state{ D3D12_RESOURCE_STATE_RENDER_TARGET };
		u32 m_snap_w{ 0 }, m_snap_h{ 0 };
		Snap m_snaps[SNAP_SLOTS];
		u32 m_snap_next{ 0 };
		u64 m_frame{ 0 }, m_last_capture_frame{ ~0ull };
		std::vector<float> m_snap_scratch;
	};
}
