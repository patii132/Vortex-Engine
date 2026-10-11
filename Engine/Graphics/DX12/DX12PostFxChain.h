#pragma once

#include "../../Common/CommonHeaders.h"
#include "DX12RenderTarget.h"
#include "DX12CommandQueue.h"
#include <d3d12.h>
#include <wrl/client.h>

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	// Post-processing pass chain (#28) + horror pack 1 (#29).
	//
	// An ordered set of fullscreen passes that runs at OUTPUT resolution, after the upscale/DLSS
	// composite (editor viewport) or the 3D scene (game window), and before the UI overlay. When the
	// chain is active the renderer redirects what used to hit the back buffer into acquire_input()'s
	// offscreen RT; record() then ping-pongs through the enabled passes and the LAST pass writes the
	// back buffer. Zero enabled passes = the renderer takes its untouched original path (no extra
	// copies, byte-identical output — the #28 regression guarantee).
	//
	// v1 registers two passes sharing one uber-PSO (postfx.hlsl, feature bits per pass in its CB):
	//   pass 0  "composite"  — vignette + film grain + chromatic aberration in one pass (#29)
	//   pass 1  "invert"     — trivial debug pass proving the multi-pass ping-pong (#28 AC), off in production
	// Bloom (#30) prepends a mip sub-chain (bloom.hlsl: soft-knee bright-pass -> progressive 13-tap
	// downsamples -> additive tent upsamples over up to MAX_BLOOM_MIPS half-res levels) and the uber
	// pass composites the result via flag 32 — sampling scene (t0) AND bloom (t1) from the chain's
	// shared shader-visible SRV heap, the piece single-SRV-per-RT binding couldn't do before.
	//
	// Traps honored (render-scale work): RTs are EXPLICIT R8G8B8A8_UNORM (class default is BGRA and
	// would mismatch the back buffer PSO format), every pass sets its own full-output viewport, and
	// all RT state round-trips happen through DX12RenderTarget's tracked transitions.
	class DX12PostFxChain
	{
	public:
		// Per-effect parameters, CPU-side. Written by the editor/scripts through the VortexAPI
		// setters (UI thread), read by record() (render thread) — same benign float-race contract as
		// the fog/light setters (a torn frame of a slider drag is invisible).
		struct Params
		{
			bool vignette{ false };
			float vig_intensity{ 0.8f };     // 0..~1.5: how far the darkening reaches in
			float vig_smoothness{ 0.5f };    // 0.01..1: falloff curve hardness
			float vig_roundness{ 1.0f };     // 1 = circular on any aspect, 0 = follows screen shape
			float vig_r{ 0.0f }, vig_g{ 0.0f }, vig_b{ 0.0f };
			bool grain{ false };
			float grain_intensity{ 0.35f };  // 0..1
			float grain_size{ 1.6f };        // cell size in output pixels
			bool ca{ false };
			float ca_strength{ 0.35f };      // percent of half-screen at the edge
			float ca_falloff{ 1.2f };        // radial power (higher = clean center, smeared edges)
			// Color grading (#31)
			bool grade{ false };
			bool fxaa{ false };              // FXAA 3.11 (console variant) on the LDR picture — the cheap anti-aliasing until TAA
			float exposure{ 0.0f };          // EV stops (2^EV); 0 = neutral
			float contrast{ 1.0f };          // 1 = neutral
			float saturation{ 1.0f };        // 1 = neutral, 0 = greyscale
			float temperature{ 0.0f };       // -1 cool .. +1 warm
			float tint{ 0.0f };              // -1 green .. +1 magenta
			// Bloom (#30) — SDR v1: thresholds the post-tonemap R8G8B8A8 scene (HDR intermediate
			// is the documented follow-up). intensity 0 keeps the whole chain bypassed (bit-exact).
			bool bloom{ false };
			float bloom_threshold{ 0.75f };  // brightness where glow starts (0..~1.5 useful in SDR)
			float bloom_knee{ 0.5f };        // soft-knee width below the threshold
			float bloom_intensity{ 0.7f };   // composite strength
			float bloom_scatter{ 0.65f };    // per-mip additive weight (how far the glow spreads)
			bool debug_invert{ false };      // #28 chain-test pass — never shipped on
		};

		bool initialize(ID3D12Device* device, DXGI_FORMAT rtv_format, DX12CommandQueue* queue);
		void shutdown();
		bool is_initialized() const { return m_pso != nullptr; }

		Params& params() { return m_params; }

		// True when at least one pass would run this frame — the renderer's redirect gate.
		bool active() const
		{
			return m_pso && (m_params.vignette || m_params.grain || m_params.ca
				|| m_params.grade || m_params.fxaa || m_params.debug_invert || bloom_requested());
		}

		// Bloom runs only when its PSOs built (shader present) AND intensity is meaningful —
		// intensity 0 must be a bit-exact passthrough (#30 AC), so it doesn't even redirect.
		bool bloom_requested() const
		{
			return m_pso_bloom_up && m_params.bloom && m_params.bloom_intensity > 0.0001f;
		}

		// MAIN-VIEW gate: post-FX is a GAME-camera look, not an editor tool — the editor's freecam
		// build viewport must stay clean. Default OFF; the GameHost player turns it on at boot (the
		// shipped game IS the main view there), and the editor's Environment panel can opt in for a
		// preview. The play-mode game window ignores this (it is always game rendering).
		void set_main_view_enabled(bool enabled) { m_main_view = enabled; }
		bool main_view_enabled() const { return m_main_view; }

		// The RT the scene/composite renders into while the chain is active, ensured to w x h and left
		// in RENDER_TARGET state. `slot` 0 = editor viewport, 1 = game window (independent sizes, so
		// each gets its own ping-pong pair — sharing one would re-create it every frame while both
		// views render). Returns nullptr on creation failure (caller falls back to the direct path).
		DX12RenderTarget* acquire_input(ID3D12Device* device, u32 w, u32 h, int slot);

		// Record the enabled passes: input(slot) -> [ping] -> out_rtv at w x h. Sets its own
		// viewport/scissor per pass and leaves every chain RT back in RENDER_TARGET state.
		void record(ID3D12GraphicsCommandList* cmd, int slot, D3D12_CPU_DESCRIPTOR_HANDLE out_rtv,
			u32 w, u32 h, float time_seconds);

	private:
		// Byte-matched to postfx.hlsl's PostFx cbuffer — keep both in sync.
		struct PassCB
		{
			float texel[2]; float time; u32 flags;
			float vignette[4];         // intensity, smoothness, roundness, unused
			float vignette_color[4];   // rgb + unused
			float grain_ca[4];         // grain intensity, grain size, ca strength, ca falloff
			float grade1[4];           // exposure, contrast, saturation, temperature
			float grade2[4];           // tint, reserved, reserved, reserved
			float bloom[4];            // composite intensity, reserved, reserved, reserved
		};
		static_assert(sizeof(PassCB) == 112, "PassCB must byte-match postfx.hlsl");

		// Byte-matched to bloom.hlsl's BloomCB — keep both in sync.
		struct BloomCB
		{
			float src_texel[2];        // 1 / source-level size
			float threshold; float knee;
			float sample_scale; float weight;
			float pad[2];
		};
		static_assert(sizeof(BloomCB) == 32, "BloomCB must byte-match bloom.hlsl");

		static constexpr u32 MAX_PASSES = 2;
		static constexpr u32 MAX_BLOOM_MIPS = 6;
		// 256B CB slots: 0 uber, 1 invert, 2 prefilter, 3..7 downsample, 8..12 upsample.
		static constexpr u32 CB_SLOTS = 16;
		static constexpr u32 CB_SLOT_PREFILTER = 2;
		static constexpr u32 CB_SLOT_DOWN = 3;                       // + (mip-1)
		static constexpr u32 CB_SLOT_UP = CB_SLOT_DOWN + (MAX_BLOOM_MIPS - 1);   // + (mip-1)
		// Shared shader-visible SRV heap layout, per slot: [0] chain input RT, [1..] bloom mips.
		static constexpr u32 SHARED_SRVS_PER_SLOT = 1 + MAX_BLOOM_MIPS;

		// A bloom mip level: color-only R11G11B10_FLOAT (float headroom while mips accumulate;
		// deliberately NOT a DX12RenderTarget, which would drag a depth buffer + readback staging
		// allocation along for every level of the chain).
		struct BloomMip
		{
			ComPtr<ID3D12Resource> tex;
			u32 w{ 0 }, h{ 0 };
			D3D12_RESOURCE_STATES state{ D3D12_RESOURCE_STATE_RENDER_TARGET };
		};

		bool ensure_rt(ID3D12Device* device, int slot, int ping, u32 w, u32 h);
		bool ensure_bloom_chain(int slot, u32 w, u32 h);
		void record_bloom(ID3D12GraphicsCommandList* cmd, int slot, u32 w, u32 h);
		void mip_transition(ID3D12GraphicsCommandList* cmd, BloomMip& mip, D3D12_RESOURCE_STATES to);
		D3D12_CPU_DESCRIPTOR_HANDLE bloom_rtv(int slot, u32 mip) const;
		D3D12_CPU_DESCRIPTOR_HANDLE shared_srv_cpu(u32 index) const;
		D3D12_GPU_DESCRIPTOR_HANDLE shared_srv_gpu(u32 index) const;
		void write_shared_srvs(int slot);

		ComPtr<ID3D12RootSignature> m_root_signature;
		ComPtr<ID3D12PipelineState> m_pso;
		ComPtr<ID3D12PipelineState> m_pso_bloom_pre;   // bright-pass + first downsample
		ComPtr<ID3D12PipelineState> m_pso_bloom_down;  // 13-tap downsample
		ComPtr<ID3D12PipelineState> m_pso_bloom_up;    // tent upsample, additive (ONE/ONE)
		ComPtr<ID3D12Resource> m_cb;          // CB_SLOTS x 256B upload heap, persistently mapped
		void* m_cb_mapped{ nullptr };
		DX12RenderTarget m_rt[2][2];          // [slot][ping] — ping B only exists once 2+ passes ran
		BloomMip m_bloom_mip[2][MAX_BLOOM_MIPS];   // [slot][level], level 0 = half res
		u32 m_bloom_mips[2]{ 0, 0 };
		ComPtr<ID3D12DescriptorHeap> m_bloom_rtv_heap;   // 2 x MAX_BLOOM_MIPS RTVs
		// THE shared shader-visible SRV heap (the #30 two-texture-composite enabler): the uber
		// pass binds scene color (t0) AND bloom (t1) from here — per-RT single-slot heaps can't
		// do that (one CBV_SRV_UAV heap bindable at a time, and shader-visible heaps are illegal
		// CopyDescriptors sources). SRVs are (re)written in place whenever an RT is (re)created.
		ComPtr<ID3D12DescriptorHeap> m_srv_heap_shared;
		u32 m_rtv_increment{ 0 }, m_srv_increment{ 0 };
		bool m_shared_dirty[2]{ true, true }; // shared-heap SRVs need a rewrite (RT/mips recreated)
		DX12CommandQueue* m_queue{ nullptr }; // for the GPU-idle before re-creating an in-flight RT
		ID3D12Device* m_device{ nullptr };    // borrowed (DX12Core owns it) — for lazy ping-B creation
		DXGI_FORMAT m_format{ DXGI_FORMAT_R8G8B8A8_UNORM };
		DXGI_FORMAT m_bloom_format{ DXGI_FORMAT_R11G11B10_FLOAT };
		Params m_params;
		bool m_main_view{ false };            // editor viewport clean by default; player enables at boot
	};
}
