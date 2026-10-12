#pragma once

// ============================================================================
// SdlGpuParticles — draws the particle module's output (Graphics/Particles) on the SDL GPU backend.
//
// Per view (record_scene): prepare() gathers the world's visible emitters / trails / beams (culled, alpha
// batches sorted back-to-front) and uploads them in one copy pass; draw_layer() then renders one layer in its
// own render pass right after that layer's meshes — colour LOAD, NO depth attachment: the scene depth is
// sampled for the depth test and the soft-particle fade (particles.metal). Layer 0 uses the world projection,
// layer 1 the first-person viewmodel projection against the viewmodel's own depth.
// Collision: capture_depth() writes a downsampled linear depth of the main view into a readback ring;
// begin_frame() hands the newest finished slot (>= 3 frames old, i.e. retired by the swapchain throttle)
// to the simulation.
// ============================================================================
#include "../../Common/CommonHeaders.h"
#include "../Particles/ParticleSystem.h"
#include <SDL3/SDL.h>
#include <string>

namespace vortex::graphics::sdlgpu
{
	class SdlGpuParticles
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
		// Scene lighting / fog snapshot for lit + fogged particles.
		struct Environment
		{
			DirectX::XMFLOAT3 fog_color{ 0, 0, 0 }; float fog_density{ 0 };
			float fog_height_y{ 0 }, fog_height_falloff{ 0 };
			float scene_exposure{ 0.5f }, fog_sun_glow{ 0 };   // the standard shader's pre-tonemap exposure and fog sun glow
			u32 tonemapper{ 0 };                                // 0 = ACES fit, 1 = PBR Neutral
			DirectX::XMFLOAT3 sun_direction{ 0.3f, -1.0f, 0.5f }; float sun_intensity{ 1 };
			DirectX::XMFLOAT3 sun_color{ 1, 1, 1 }; float ambient{ 0.3f };
			u32 point_lights{ 0 }, spot_lights{ 0 };
			const void* lights{ nullptr };   // the first 1024 bytes of LightBufferData (point + spot lights)
		};

		bool initialize(SDL_GPUDevice* device, const std::string& shader_dir, SDL_GPUTextureFormat color_format,
			SDL_GPUSampler* linear_wrap, SDL_GPUSampler* linear_clamp);
		void shutdown();
		bool ready() const { return m_ready; }

		// Offscreen targets draw no particles unless the caller names a world for the NEXT render (one-shot).
		void set_next_target_world(int world) { m_next_target_world = world; }
		int consume_target_world() { int w = m_next_target_world; m_next_target_world = -1; return w; }

		// Gather + upload (copy pass, before the scene pass). True when this view has particles to draw.
		bool prepare(SDL_GPUCommandBuffer* cmd, const View& view, u32 world);
		bool has_layer(u32 layer) const { return layer < 2 && m_layer_batches[layer] > 0; }
		void draw_layer(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* color, SDL_GPUTexture* depth, u32 w, u32 h, u32 layer,
			const View& view, const Environment& env);

		// Collision snapshot of the main view (call after the world layer's depth is complete).
		void capture_depth(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* depth, u32 w, u32 h, const View& view);
		void begin_frame();

	private:
		SDL_GPUShader* create_shader(const char* entry, SDL_GPUShaderStage stage, u32 samplers, u32 storage, u32 uniforms);
		SDL_GPUGraphicsPipeline* create_pipeline(SDL_GPUShader* vs, SDL_GPUShader* fs, bool ribbon, u32 blend);
		bool ensure_buffers(u32 instance_bytes, u32 vertex_bytes, u32 index_bytes);

		SDL_GPUDevice* m_device{ nullptr };
		std::string m_shader_dir;          // Engine/Shaders/<msl|spirv>, from the renderer
		bool m_ready{ false };
		SDL_GPUTextureFormat m_color_format{ SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM };
		SDL_GPUSampler* m_linear_wrap{ nullptr };
		SDL_GPUSampler* m_linear_clamp{ nullptr };
		SDL_GPUSampler* m_point_clamp{ nullptr };
		SDL_GPUShader* m_vs_particle{ nullptr };
		SDL_GPUShader* m_vs_ribbon{ nullptr };
		SDL_GPUShader* m_fs_particle{ nullptr };
		SDL_GPUShader* m_vs_snap{ nullptr };
		SDL_GPUShader* m_fs_snap{ nullptr };
		SDL_GPUGraphicsPipeline* m_billboard[3]{};   // by blend mode
		SDL_GPUGraphicsPipeline* m_ribbon[3]{};
		SDL_GPUGraphicsPipeline* m_snap{ nullptr };

		SDL_GPUBuffer* m_instances{ nullptr };      // storage (GpuParticle)
		SDL_GPUBuffer* m_vertices{ nullptr };       // ribbon vertices
		SDL_GPUBuffer* m_indices{ nullptr };        // ribbon indices
		SDL_GPUTransferBuffer* m_upload{ nullptr };
		u32 m_instance_cap{ 0 }, m_vertex_cap{ 0 }, m_index_cap{ 0 }, m_upload_cap{ 0 };

		particles::DrawList m_list;
		u32 m_layer_batches[2]{ 0, 0 };
		int m_next_target_world{ -1 };

		// collision depth readback ring
		static constexpr u32 SNAP_SLOTS = 4;
		struct Snap
		{
			SDL_GPUTransferBuffer* buffer{ nullptr };
			u32 w{ 0 }, h{ 0 }, bytes{ 0 };
			u64 frame{ 0 };
			bool pending{ false };
			particles::DepthView view{};
		};
		Snap m_snaps[SNAP_SLOTS];
		SDL_GPUTexture* m_snap_texture{ nullptr };
		u32 m_snap_w{ 0 }, m_snap_h{ 0 };
		u32 m_snap_next{ 0 };
		u64 m_frame{ 0 }, m_last_capture_frame{ ~0ull };
		std::vector<float> m_snap_scratch;
	};
}
