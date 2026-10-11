#pragma once

// ============================================================================
// SdlGpuRenderer — the SDL GPU render backend (Metal on macOS, Vulkan on Linux).
//
// Same public surface as DX12Renderer (Graphics/DX12) so VortexAPI and the
// runtime compile unchanged against graphics::Renderer (see Graphics/Backend.h).
// Frame flow:
//   swap queues -> CPU sort/cull/pack -> upload instance + bone data (copy pass)
//   -> scene pass into an offscreen BGRA8 target at render scale (skybox, grid,
//      opaque runs, sorted transparents; particles of the world layer; a second
//      pass with cleared depth for the first-person viewmodel layer + its
//      particles; always-on-top gizmos)
//   -> blit / post-FX pass into the present target at window resolution
//   -> 2D overlay pass -> optional capture -> blit to the swapchain.
// ============================================================================
#include "../../Common/CommonHeaders.h"
#include "../../Common/Id.h"
#include "SdlGpuResources.h"
#include "SdlGpuOverlay.h"
#include "SdlGpuParticles.h"
#include "SdlGpuDecals.h"
#include "../Decals/Decals.h"
#include "SdlGpuVolumetrics.h"
#include "../Volumetrics/Volumetrics.h"
#include <SDL3/SDL.h>
#include <chrono>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

namespace vortex::graphics::sdlgpu
{
	constexpr u32 MAX_RENDER_OBJECTS = 262144;
	constexpr u32 MAX_DRAW_RUNS = 8192;
	constexpr u32 MAX_RENDER_TARGETS = 8;
	constexpr u32 MAX_BONE_MATRICES = 65536;
	constexpr u32 NO_BONES = 0xFFFFFFFFu;
	constexpr u32 MAX_GIZMO_ITEMS = 512;

	struct RendererDesc
	{
		void* native_window{ nullptr };      // NSView* (editor-embedded) or SDL_Window* (native_is_sdl_window)
		bool native_is_sdl_window{ false };
		u32 width{ 0 };
		u32 height{ 0 };
	};

	struct RenderItem
	{
		id::id_type mesh_id{ id::invalid_id };
		id::id_type material_id{ id::invalid_id };
		DirectX::XMFLOAT4X4 world_matrix;
		DirectX::XMFLOAT4 color{ 1.0f, 1.0f, 1.0f, 1.0f };   // per-instance tint (#331), multiplied with the material's base colour
		u32 bone_offset{ NO_BONES };
		u32 bone_count{ 0 };
		u32 layer{ 0 };
		u32 mirrored{ 0 };   // det(world) < 0: the instance is wound the other way (#334), set at submit
	};

	struct ViewportCamera
	{
		DirectX::XMFLOAT3 position{ 0.0f, 10.0f, 0.0f };
		DirectX::XMFLOAT3 target{ 0.0f, 0.0f, 0.0f };
		DirectX::XMFLOAT3 up{ 0.0f, 0.0f, -1.0f };
		float fov_degrees{ 60.0f };
		float near_clip{ 0.1f };
		float far_clip{ 1000.0f };
		bool orthographic{ true };
		float ortho_size{ 20.0f };
	};

	// Post-processing parameters — same fields and semantics as DX12PostFxChain::Params.
	class PostFx
	{
	public:
		struct Params
		{
			bool vignette{ false };
			float vig_intensity{ 0.8f }, vig_smoothness{ 0.5f }, vig_roundness{ 1.0f };
			float vig_r{ 0.0f }, vig_g{ 0.0f }, vig_b{ 0.0f };
			bool grain{ false };
			float grain_intensity{ 0.35f }, grain_size{ 1.6f };
			bool ca{ false };
			float ca_strength{ 0.35f }, ca_falloff{ 1.2f };
			bool grade{ false };
			float exposure{ 0.0f }, contrast{ 1.0f }, saturation{ 1.0f }, temperature{ 0.0f }, tint{ 0.0f };
			bool bloom{ false };
			float bloom_threshold{ 0.75f }, bloom_knee{ 0.5f }, bloom_intensity{ 0.7f }, bloom_scatter{ 0.65f };
			bool debug_invert{ false };
		};
		Params& params() { return m_params; }
		const Params& params() const { return m_params; }
		bool bloom_requested() const { return m_params.bloom; }
		bool active() const { return m_params.vignette || m_params.grain || m_params.ca || m_params.grade || m_params.debug_invert || m_params.bloom; }
		void set_main_view_enabled(bool enabled) { m_main_view = enabled; }
		bool main_view_enabled() const { return m_main_view; }
	private:
		Params m_params;
		bool m_main_view{ false };
	};

	// Offscreen color (+ optional depth) target with optional CPU readback staging.
	struct GpuTarget
	{
		SDL_GPUTexture* color{ nullptr };
		SDL_GPUTexture* depth{ nullptr };
		u32 width{ 0 };
		u32 height{ 0 };
		SDL_GPUTransferBuffer* readback{ nullptr };
		u32 readback_size{ 0 };
		void* readback_mapped{ nullptr };
	};

	class SdlGpuRenderer
	{
	public:
		static SdlGpuRenderer& instance();

		bool initialize(const RendererDesc& desc);
		void shutdown();
		void resize(u32 width, u32 height);
		void render_frame();
		void swap_render_queue();
		void on_scene_switch();
		void request_capture(const char* path);

		// Standalone game window (a second SDL window/swapchain; native_window may be null to create one).
		bool create_game_window(void* native_window, u32 width, u32 height);
		void render_game_window();
		void resize_game_window(u32 width, u32 height);
		void destroy_game_window();
		bool is_game_window_active() const { return m_game.window != nullptr; }

		bool is_initialized() const { return m_initialized; }

		// Render queue
		void submit_render_item(const RenderItem& item);
		void submit_gizmo_item(const RenderItem& item);
		void submit_gizmo_wire_item(const RenderItem& item);
		// Decals (#120): the scene submit clears and refills the list (DecalApi.cpp, like the lights); the world pass draws
		// it between the opaque and the transparent meshes.
		void clear_decals() { m_decal_list.clear(); }
		void add_decal(const decals::Decal& d) { if (m_decal_list.size() < decals::MAX_DECALS) m_decal_list.push_back(d); }
		const std::vector<decals::Decal>& decal_list() const { return m_decal_list; }
		// Volumetric fog (#119): persistent like the analytic fog (scene settings / Atmosphere.SetVolumetricFog).
		void set_volumetric_fog(const volumetrics::Params& p) { m_volumetrics.set_params(p); }
		const volumetrics::Params& volumetric_fog() const { return m_volumetrics.params(); }
		// colors (#331): count x (r, g, b, a) per instance, or nullptr for untinted
		void submit_mesh_instances(id::id_type mesh, id::id_type material, const float* world_matrices, u32 count, u32 layer = 0, const float* colors = nullptr);
		void submit_skinned_item(id::id_type mesh, id::id_type material, const float* world_matrix,
			const float* bone_matrices, u32 bone_count, u32 layer = 0, const float* color = nullptr);
		void clear_render_queue();
		void begin_static_scene();
		void end_static_scene();

		// Camera + lights
		void set_camera(const DirectX::XMFLOAT3& position, const DirectX::XMFLOAT3& target, const DirectX::XMFLOAT3& up);
		void set_directional_light(const DirectX::XMFLOAT3& direction, const DirectX::XMFLOAT3& color);
		void set_ambient_strength(float strength);

		static constexpr u32 MAX_POINT_LIGHTS = 16;
		static constexpr u32 MAX_SPOT_LIGHTS = 8;
		static constexpr u32 MAX_SUBMITTED_LIGHTS = 1024;   // kept per frame; the most relevant fill the slots above (#335)

		struct PointLightData
		{
			DirectX::XMFLOAT3 position;
			float range;
			DirectX::XMFLOAT3 color;
			float intensity;
			u32 cast_shadows{ 0 };
			float shadow_strength{ 1.0f };
			float shadow_bias{ 0.0015f };
		};

		struct SpotLightData
		{
			DirectX::XMFLOAT3 position;
			float range;
			DirectX::XMFLOAT3 direction;
			float spot_angle;
			DirectX::XMFLOAT3 color;
			float intensity;
			float inner_spot_angle;
			u32 cast_shadows{ 0 };
			float shadow_strength{ 1.0f };
			float shadow_bias{ 0.0015f };
			u32 shadow_resolution{ 2048 };
		};

		void clear_lights();
		void set_ssao(bool enabled, float radius, float intensity)
		{
			m_ssao_enabled = enabled && intensity > 0.001f;
			m_ssao_radius = radius > 0.05f ? radius : 0.05f;
			m_ssao_intensity = intensity < 0.0f ? 0.0f : intensity;
		}
		void set_directional_light_full(const DirectX::XMFLOAT3& direction, const DirectX::XMFLOAT3& color, float intensity,
			bool cast_shadows = false, float shadow_strength = 1.0f, float shadow_bias = 0.0008f, float shadow_distance = 80.0f);
		void add_point_light(const PointLightData& light);
		void add_spot_light(const SpotLightData& light);
		void set_fog(const DirectX::XMFLOAT3& color, float density, float height_y, float height_falloff);
		void set_scene_exposure(float multiplier) { m_frame_constants.scene_exposure = multiplier > 0.0f ? multiplier : 0.5f; }
		void set_fog_sun_glow(float glow) { m_frame_constants.fog_sun_glow = glow > 0.0f ? glow : 0.0f; }
		void set_tonemapper(u32 mode) { m_frame_constants.tonemapper = mode > 1u ? 1u : mode; }

		PostFx& postfx() { return m_postfx; }
		float elapsed_seconds() const
		{
			return std::chrono::duration<float>(std::chrono::steady_clock::now() - m_time_origin).count();
		}

		void set_wireframe_mode(bool enabled) { m_wireframe_mode = enabled; }
		bool is_wireframe_mode() const { return m_wireframe_mode; }
		void set_vsync(bool enabled) { if (m_vsync_enabled != enabled) { m_vsync_enabled = enabled; m_vsync_dirty = true; } }
		bool is_vsync_enabled() const { return m_vsync_enabled; }
		void set_render_scale(float s) { m_render_scale = s < 0.25f ? 0.25f : (s > 2.0f ? 2.0f : s); }
		float render_scale() const { return m_render_scale; }

		// DLSS / frame generation are NVIDIA + Windows only: the modes are stored (settings round-trip) but
		// render at the mode's scale through the plain bilinear upscale, exactly like a non-RTX Windows GPU.
		void set_dlss_mode(int mode)
		{
			m_dlss_mode = (mode < 0 || mode > 4) ? 0 : mode;
			switch (m_dlss_mode)
			{
			case 1: m_render_scale = 0.667f; break;
			case 2: m_render_scale = 0.580f; break;
			case 3: m_render_scale = 0.500f; break;
			case 4: m_render_scale = 0.333f; break;
			default: m_render_scale = 1.0f; break;
			}
		}
		int dlss_mode() const { return m_dlss_mode; }
		void set_fg_mode(int mode) { m_fg_mode = (mode < 0 || mode > 3) ? 0 : mode; }
		int fg_mode() const { return m_fg_mode; }
		int fg_presented_fps() const { return 0; }

		// Custom per-material shaders: a .metal file with VSMain/PSMain using the standard binding layout.
		void set_material_shader(u32 material_id, const std::string& shader_path);
		int reload_dirty_shaders();
		bool any_material_shader_dirty() const;
		// Compile a custom material shader without binding it (the editor's shader check): both stages and a pipeline
		// with the engine's vertex layout and bindings. False = errors holds the compiler output.
		bool validate_material_shader(const std::string& shader_path, std::string& errors);

		void set_grid_visible(bool visible) { m_grid_visible = visible; }
		bool is_grid_visible() const { return m_grid_visible; }
		void set_grid_settings(float spacing, float major_interval, float extent)
		{
			m_grid_spacing = spacing; m_grid_major_interval = major_interval; m_grid_extent = extent;
		}
		void set_gizmos_visible(bool visible) { m_gizmos_visible = visible; }
		bool are_gizmos_visible() const { return m_gizmos_visible; }

		enum class SkyboxMode : u32 { SolidColor = 0, Gradient = 1, Texture = 2 };
		void set_skybox_enabled(bool enabled) { m_skybox_enabled = enabled; }
		bool is_skybox_enabled() const { return m_skybox_enabled; }
		void set_skybox_mode(SkyboxMode mode) { m_skybox_mode = mode; }
		// Equirect texture sky (#326): the registry texture the fullscreen sky pass samples in SkyboxMode::Texture —
		// behind everything at the far plane, no depth write, centred on whichever camera renders the frame.
		// hdr: 0 = an 8-bit picture shown as it is; 1 = a float picture shown the classic way (gamma-encoded, clamped at white);
		// 2 = a float picture run through the scene exposure + the tonemapper like the lit surfaces
		void set_skybox_texture(id::id_type texture, float exposure, float rotation_radians, int hdr = 0)
		{
			m_sky_texture = texture; m_sky_exposure = exposure; m_sky_rotation = rotation_radians; m_sky_hdr = hdr;
		}
		SkyboxMode get_skybox_mode() const { return m_skybox_mode; }
		void set_skybox_colors(const DirectX::XMFLOAT3& sky, const DirectX::XMFLOAT3& horizon, const DirectX::XMFLOAT3& ground)
		{
			m_sky_color = sky; m_horizon_color = horizon; m_ground_color = ground;
		}
		void set_skybox_solid_color(const DirectX::XMFLOAT3& color) { m_sky_color = m_horizon_color = m_ground_color = color; }
		void set_sky_light(const float* sh27, bool on) { if (sh27) memcpy(m_sky_sh, sh27, sizeof(m_sky_sh)); m_sky_sh_on = on && sh27 != nullptr; }
		void set_environment_map(id::id_type texture, int mips) { m_env_map = texture; m_env_mips = mips; }
		void set_skybox_sun(const DirectX::XMFLOAT3& direction, const DirectX::XMFLOAT3& color, float intensity)
		{
			m_sun_direction = direction; m_sun_color = color; m_sun_intensity = intensity;
		}

		void set_projection(float fov_degrees, float aspect, float near_clip, float far_clip);
		void set_field_of_view(float fov_degrees) { if (fov_degrees >= 30.0f && fov_degrees <= 120.0f) m_fov_degrees = fov_degrees; }
		float field_of_view() const { return m_fov_degrees; }
		// Near / far planes of the live view (#327): the main view, SSAO, grid and sky read these instead of a
		// fixed 0.1 / 1000 m. Driven by the game's main Camera component in play mode.
		void set_clip_planes(float near_clip, float far_clip)
		{
			if (near_clip > 0.0001f && far_clip > near_clip) { m_near_clip = near_clip; m_far_clip = far_clip; }
		}
		float near_clip() const { return m_near_clip; }
		float far_clip() const { return m_far_clip; }
		void set_viewmodel_fov(float fov_degrees) { if (fov_degrees >= 10.0f && fov_degrees <= 120.0f) m_viewmodel_fov = fov_degrees; }
		void set_render_distance(float d) { m_render_distance = d >= 0.0f ? d : 0.0f; }
		float render_distance() const { return m_render_distance; }
		void set_lod(bool enabled, float mid, float farD)
		{
			m_lod_enabled = enabled;
			m_lod_mid = mid > 0.0f ? mid : 0.0f;
			m_lod_far = farD > m_lod_mid ? farD : m_lod_mid;
		}
		void set_geometric_lod(bool enabled, float mid, float farD)
		{
			m_geo_lod_enabled = enabled;
			m_lod_mid = mid > 0.0f ? mid : 0.0f;
			m_lod_far = farD > m_lod_mid ? farD : m_lod_mid;
		}
		void set_multithreading(bool enabled) { m_mt_enabled = enabled; }
		bool is_multithreading() const { return m_mt_enabled; }
		void set_multithreading_force(bool f) { m_mt_force = f; }
		bool mt_active() const { return m_mt_active; }

		void render_camera_gizmo(const DirectX::XMFLOAT3&, const DirectX::XMFLOAT3&, const DirectX::XMFLOAT3&, const DirectX::XMFLOAT3&,
			float, float, float, float, float, float, const DirectX::XMFLOAT4&) {}

		// 2D UI overlay (immediate-mode command list drawn over the finished frame; see SdlGpuOverlay).
		void ui_begin(float w, float h) { m_overlay.begin(w, h); }
		void ui_rect(float x, float y, float w, float h, float r, float g, float b, float a, float radius) { m_overlay.add_rect(x, y, w, h, r, g, b, a, radius); }
		void ui_text(float x, float y, float w, float h, const wchar_t* s, float size, float r, float g, float b, float a, int align, int weight);
		void ui_text_utf8(float x, float y, float w, float h, const char* s, float size, float r, float g, float b, float a, int align, int weight) { m_overlay.add_text(x, y, w, h, s ? s : "", size, r, g, b, a, align, weight); }
		void ui_line(float x1, float y1, float x2, float y2, float r, float g, float b, float a, float thick) { m_overlay.add_line(x1, y1, x2, y2, r, g, b, a, thick); }
		void ui_image(float x, float y, float w, float h, const wchar_t* path, float r, float g, float b, float a);
		void ui_image_utf8(float x, float y, float w, float h, const char* path, float r, float g, float b, float a) { m_overlay.add_image(x, y, w, h, path ? path : "", r, g, b, a); }
		void ui_push_clip(float x, float y, float w, float h) { m_overlay.push_clip(x, y, w, h); }
		void ui_pop_clip() { m_overlay.pop_clip(); }

		// Performance statistics
		int get_current_fps() const { return m_current_fps; }
		int get_draw_call_count() const { return m_draw_call_count; }
		// draws of the shadow atlases / the SSAO prepass — part of get_draw_call_count (#363)
		int get_shadow_draw_count() const { return m_shadow_draw_count; }
		int get_post_draw_count() const { return m_post_draw_count; }
		int get_vertex_count() const { return m_vertex_count; }
		int get_instances_tested() const { return m_instances_tested; }
		int get_instances_drawn() const { return m_instances_drawn; }

		// Secondary render targets (thumbnails, extra viewports) with CPU readback (BGRA8, row pitch = width*4).
		u32 create_render_target(u32 width, u32 height);
		void destroy_render_target(u32 target_id);
		bool resize_render_target(u32 target_id, u32 width, u32 height);
		void render_to_target(u32 target_id, const ViewportCamera& camera, bool render_grid = false, bool render_gizmos = false);
		bool prepare_render_target_readback(u32 target_id);
		const void* read_render_target_pixels(u32 target_id, u32& out_width, u32& out_height, u32& out_row_pitch);
		void release_render_target_pixels(u32 target_id);
		bool has_render_target(u32 target_id) const { return m_render_targets.find(target_id) != m_render_targets.end(); }

		// Particles (VFX, Graphics/Particles): the scene views draw world 0; ParticleSetNextTargetWorld picks the
		// world of the next render_to_target (SdlGpuParticles.h).
		SdlGpuParticles& particles() { return m_particles; }

		// GPU info (the DX12 backend exposes these through DX12Core)
		u32 gpu_vendor_id() const { return 0; }
		const std::string& gpu_name() const { return m_gpu_name; }
		bool dlss_capable() const { return false; }
		SDL_GPUDevice* device() const { return m_device; }
		// Explicit shader source directory (checked first by find_shader_dir); set before initialize().
		void set_shader_directory(const std::string& dir) { m_shader_dir_override = dir; }

		// Shadow maps — spot atlas (2x2 tiles), directional cascades (2x2 tiles), point cube faces (4x3 tiles).
		// Same tile layout, light-buffer tails and sampling rules as the DX12 backend (SdlGpuRenderer_Shadows.cpp).
		bool ensure_shadow_resources();
		// The comparison sampler + the 1x1 depth texture that stand in for a missing shadow atlas. Created once
		// with the device (not with the atlases), because the shadow slots must hold a legal comparison-sampled
		// depth binding on every frame, including before/without ensure_shadow_resources().
		bool create_shadow_fallback();
		void destroy_shadow_resources();
		void record_shadow_passes(SDL_GPUCommandBuffer* cmd);
		static constexpr u32 MAX_SHADOW_SPOTS = 4;
		static constexpr u32 SHADOW_TILE_SIZE = 2048;
		static constexpr u32 CSM_CASCADES = 3;
		static constexpr u32 MAX_SHADOW_POINTS = 2;
		static constexpr u32 POINT_SHADOW_TILE = 1024;
		static constexpr u32 POINT_ATLAS_COLS = 4;
		static constexpr u32 MAX_SHADOW_INSTANCES = 8192;

	private:
		SdlGpuRenderer() = default;
		~SdlGpuRenderer() = default;
		SdlGpuRenderer(const SdlGpuRenderer&) = delete;
		SdlGpuRenderer& operator=(const SdlGpuRenderer&) = delete;

		// ---- constant layouts (byte-matched to the .metal structs AND the DX12 cbuffers) ----
		struct PerFrameConstants
		{
			DirectX::XMFLOAT4X4 view_projection;
			DirectX::XMFLOAT3 camera_position; float padding0;
			DirectX::XMFLOAT3 light_direction; float directional_intensity;
			DirectX::XMFLOAT3 light_color; float ambient_strength;
			u32 point_light_count; u32 spot_light_count; u32 padding1[2];
			DirectX::XMFLOAT3 fog_color; float fog_density;
			float fog_height_y; float fog_height_falloff; u32 fog_mode; float scene_exposure;   // pre-tonemap multiplier (0.5 = classic)
			float shadow_map_texel; u32 tonemapper; u32 shadow_padding[2];   // tonemapper: 0 = ACES fit, 1 = PBR Neutral
			float ssao_enabled; float ssao_padding[2]; float fog_sun_glow;   // [0..1] = AO UV scale; the sun's glow in the fog
			// scene sky gradient for specular reflections (w = 1 when a gradient sky is active)
			DirectX::XMFLOAT4 env_sky; DirectX::XMFLOAT4 env_horizon; DirectX::XMFLOAT4 env_ground;
			// water refraction / screen-space reflections (#200): x = near, y = far clip of this view — @240
			DirectX::XMFLOAT4 camera_clip;
		};
		static_assert(sizeof(PerFrameConstants) == 256, "PerFrameConstants must byte-match standard.metal (prefix) / water.metal");

		struct PerObjectConstants
		{
			DirectX::XMFLOAT4X4 world;
			DirectX::XMFLOAT4 base_color;
			float metallic, roughness, ao, normal_strength;
			u32 has_albedo_texture, has_normal_texture, has_metallic_texture, has_roughness_texture;
			u32 has_ao_texture, use_directx_normals, is_unlit; float emissive_strength;
			DirectX::XMFLOAT2 uv_tiling; u32 has_height_texture; float height_scale;
			float alpha_cutoff; float _pad0, _pad1, _pad2;   // @144: AlphaTest cutoff (#329), 0 = off
		};
		static_assert(sizeof(PerObjectConstants) == 160, "PerObjectConstants must byte-match standard.metal");

		struct GPUPointLight { DirectX::XMFLOAT3 position; float range; DirectX::XMFLOAT3 color; float intensity; };
		struct GPUSpotLight
		{
			DirectX::XMFLOAT3 position; float range;
			DirectX::XMFLOAT3 direction; float spot_angle;
			DirectX::XMFLOAT3 color; float intensity;
			float inner_spot_angle; float shadow_strength; float shadow_bias; float shadow_slot;
		};
		struct LightBufferData
		{
			GPUPointLight point_lights[MAX_POINT_LIGHTS];
			GPUSpotLight spot_lights[MAX_SPOT_LIGHTS];
			DirectX::XMFLOAT4X4 shadow_vp[4];
			DirectX::XMFLOAT4X4 cascade_vp[3];
			DirectX::XMFLOAT4 cascade_splits;
			DirectX::XMFLOAT4 dir_shadow_params;
			DirectX::XMFLOAT4 point_shadows[2];
			DirectX::XMFLOAT4X4 point_face_vp[12];
			// sky light (IBL step 1) @2304: SH9 irradiance per channel, rgb = coefficient, w of [0] = on
			DirectX::XMFLOAT4 sky_sh[9];
		};
		static_assert(sizeof(LightBufferData) == 2448, "LightBufferData must byte-match standard.metal");
		// the sky light's SH9 into the light buffer's tail (rgb = coefficient k, w of [0] = on)
		void fill_sky_light(LightBufferData& L) const
		{
			for (int k = 0; k < 9; ++k) L.sky_sh[k] = { m_sky_sh[k * 3], m_sky_sh[k * 3 + 1], m_sky_sh[k * 3 + 2], (k == 0 && m_sky_sh_on) ? 1.0f : 0.0f };
		}

		struct SkinParams { u32 bone_base; u32 padding[3]; };
		struct GridConstants
		{
			DirectX::XMFLOAT4X4 view_projection;
			DirectX::XMFLOAT4X4 inverse_view_projection;
			DirectX::XMFLOAT3 camera_position; float spacing;
			float extent; float major; float pad[2];
		};
		struct SkyboxConstants
		{
			DirectX::XMFLOAT4X4 inverse_view_projection;
			DirectX::XMFLOAT3 camera_position; float padding0;
			DirectX::XMFLOAT3 sky_color; float padding1;
			DirectX::XMFLOAT3 horizon_color; float padding2;
			DirectX::XMFLOAT3 ground_color; float padding3;
			DirectX::XMFLOAT3 sun_direction; float sun_intensity;
			DirectX::XMFLOAT3 sun_color; float tonemapper;   // the HDR path's curve: 0 = ACES fit, 1 = PBR Neutral
			DirectX::XMFLOAT4 params;   // x: 1 = sample the equirect texture (#326), y: exposure, z: yaw offset (radians)
		};
		struct PostFxCB
		{
			float texel[2]; float time; u32 flags;
			float vignette[4]; float vignette_color[4]; float grain_ca[4];
			float grade1[4]; float grade2[4]; float bloom[4];
		};
		static_assert(sizeof(PostFxCB) == 112, "PostFxCB must byte-match postfx.metal");

		// A view = camera matrices + the lighting snapshot the passes read.
		struct FrameView
		{
			PerFrameConstants frame{};
			PerFrameConstants viewmodel{};
			DirectX::XMFLOAT4X4 view_projection;
			DirectX::XMFLOAT4X4 inverse_view_projection;
			DirectX::XMFLOAT3 eye;
			bool has_viewmodel{ false };
			// camera basis + projection parameters (particles: billboards, soft depth, collision snapshot)
			DirectX::XMFLOAT3 right{ 1, 0, 0 }, up{ 0, 1, 0 }, forward{ 0, 0, 1 };
			float near_clip{ 0.1f }, far_clip{ 1000.0f };
			bool ortho{ false };
			float tan_half_x{ 1.0f }, tan_half_y{ 1.0f };
		};
		void prepare_shadow_pass(const FrameView& view);   // SdlGpuRenderer_Shadows.cpp
		// Screen-space effects (SdlGpuRenderer_PostFx.cpp)
		static constexpr u32 SSAO_MAX_INSTANCES = 4096;
		static constexpr u32 BLOOM_MIPS = 5;
		struct SsaoSet { u32 w, h; SDL_GPUTexture* depth; SDL_GPUTexture* ao; SDL_GPUTexture* blur; };
		struct BloomMip { u32 w, h; SDL_GPUTexture* tex; };
		struct BloomChain { u32 w, h; std::vector<BloomMip> mips; };
		bool ensure_postfx_resources();
		void destroy_postfx_resources();
		SDL_GPUGraphicsPipeline* create_post_pipeline(SDL_GPUShader* vs, SDL_GPUShader* fs, SDL_GPUTextureFormat format, bool additive);
		SsaoSet* acquire_ssao_set(u32 w, u32 h);
		BloomChain* acquire_bloom_chain(u32 w, u32 h);
		void record_ssao(SDL_GPUCommandBuffer* cmd, const FrameView& view, u32 target_w, u32 target_h);
		void record_bloom(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* scene, u32 w, u32 h);
		bool m_post_ready{ false };
		SDL_GPUShader* m_vs_ssao{ nullptr }; SDL_GPUShader* m_fs_ssao{ nullptr }; SDL_GPUShader* m_fs_ssao_blur{ nullptr };
		SDL_GPUShader* m_vs_bloom{ nullptr }; SDL_GPUShader* m_fs_bloom_prefilter{ nullptr }; SDL_GPUShader* m_fs_bloom_down{ nullptr }; SDL_GPUShader* m_fs_bloom_up{ nullptr };
		SDL_GPUGraphicsPipeline* m_pipeline_ssao{ nullptr }; SDL_GPUGraphicsPipeline* m_pipeline_ssao_blur{ nullptr };
		SDL_GPUGraphicsPipeline* m_pipeline_bloom_prefilter{ nullptr }; SDL_GPUGraphicsPipeline* m_pipeline_bloom_down{ nullptr }; SDL_GPUGraphicsPipeline* m_pipeline_bloom_up{ nullptr };
		SDL_GPUSampler* m_sampler_point_clamp{ nullptr };
		SDL_GPUBuffer* m_ssao_instance_buffer{ nullptr }; SDL_GPUTransferBuffer* m_ssao_instance_transfer{ nullptr };
		std::vector<SsaoSet> m_ssao_sets; SDL_GPUTexture* m_ssao_current{ nullptr };
		std::vector<BloomChain> m_bloom_chains; SDL_GPUTexture* m_bloom_result{ nullptr };

		// One contiguous (layer, material, mesh) run in the sorted render queue.
		struct DrawRun
		{
			size_t start; u32 count;
			id::id_type mesh; id::id_type mat;
			Mesh* meshp;
			bool defaultBounds;
			float lcx, lcy, lcz, localR;
			u32 vbBase; u32 visible;
			u32 lodLevels{ 1 };
			id::id_type lodMesh[4]{ id::invalid_id, id::invalid_id, id::invalid_id, id::invalid_id };
			float lodT1sq{ 0 }, lodT2sq{ 0 }, lodT3sq{ 0 };
			u32 lodCount[4]{ 0, 0, 0, 0 };
			bool skinned{ false };
			u32 boneOffset{ 0 }; u32 boneCount{ 0 };
			u32 layer{ 0 };
			bool mirrored{ false };   // every instance of the run has a negative-determinant world matrix (#334)
		};

		// Pipeline variants keyed by the mesh vertex stride (32 rigid / 52 skinned-layout) — SDL GPU bakes
		// the vertex pitch into the pipeline, so both strides get their own set.
		struct PipelineSet
		{
			SDL_GPUGraphicsPipeline* opaque{ nullptr };
			SDL_GPUGraphicsPipeline* wireframe{ nullptr };
			SDL_GPUGraphicsPipeline* double_sided{ nullptr };
			SDL_GPUGraphicsPipeline* gizmo{ nullptr };
			SDL_GPUGraphicsPipeline* gizmo_wire{ nullptr };
			SDL_GPUGraphicsPipeline* alpha{ nullptr };
			SDL_GPUGraphicsPipeline* alpha_ds{ nullptr };
			SDL_GPUGraphicsPipeline* additive{ nullptr };
			SDL_GPUGraphicsPipeline* additive_ds{ nullptr };
			// counter-clockwise front faces for mirrored instances (#334); the double-sided variants cull nothing
			SDL_GPUGraphicsPipeline* opaque_m{ nullptr };
			SDL_GPUGraphicsPipeline* alpha_m{ nullptr };
			SDL_GPUGraphicsPipeline* additive_m{ nullptr };
			SDL_GPUGraphicsPipeline* transparent(u32 blend_mode, bool ds, bool mirrored = false) const
			{
				if (blend_mode == 1) return ds ? alpha_ds : (mirrored && alpha_m ? alpha_m : alpha);
				if (blend_mode == 2) return ds ? additive_ds : (mirrored && additive_m ? additive_m : additive);
				return nullptr;
			}
		};

		struct WindowSurface
		{
			SDL_Window* window{ nullptr };
			bool owns_window{ false };
			GpuTarget scene;     // render-scale target (BGRA8 + D32)
			GpuTarget present;   // window-resolution composite + overlay target (BGRA8)
			GpuTarget postfx;    // post-FX input when the chain is active
			u32 last_w{ 0 }, last_h{ 0 };
		};

		struct CustomShader { std::string path; unsigned long long mtime{ 0 }; };
		// One compiled pipeline per variant (blend 0/1/2 × double-sided × mirrored = 12), each built the first time a
		// draw asks for it; the mtime of the source it was built from decides whether it is stale.
		// blend 0/1/2 x double-sided x mirrored (#333, #334) = 12, each once more for the skinned layout (#332)
		static constexpr u32 PIPELINE_VARIANTS = 24;
		struct CachedPipeline { SDL_GPUGraphicsPipeline* pipeline[PIPELINE_VARIANTS]{}; unsigned long long mtime[PIPELINE_VARIANTS]{}; };

		// ---- init helpers (SdlGpuRenderer.cpp) ----
		bool create_device();
		SDL_Window* create_or_wrap_window(void* native, bool is_sdl_window, u32 w, u32 h);
		bool claim_window(SDL_Window* window);
		void attach_metal_view_to_host();
		// X11: give the window's input masks back to the toolkit that owns this window (see the .cpp).
		void release_host_input(SDL_Window* window);
		// Keeps SDL's surface the size of the toolkit's host view/window (macOS: the Metal layer's
		// frame; Linux: SDL's idea of the wrapped X11 window's size, which drives the swapchain).
		void sync_host_surface();
		void apply_swapchain_params(SDL_Window* window);
		bool load_shaders();
		// Creates the shader `entry` of the shader set `base` ("standard", "grid", ...). The backend's shader
		// format decides whether that is an entrypoint inside one MSL source or its own SPIR-V module
		// (SdlGpuShaderFormat.h); the raw code is cached in m_shader_blobs either way.
		SDL_GPUShader* create_shader(const std::string& base, const char* entry, SDL_GPUShaderStage stage,
			u32 samplers, u32 storage_buffers, u32 uniform_buffers);
		// Shader code as SDL consumes it, read once per file: NUL-terminated MSL text or a SPIR-V binary.
		const std::vector<unsigned char>* shader_blob(const std::string& file);
		// The primitive behind both create_shader() and the custom-material path.
		SDL_GPUShader* create_shader_from_code(const std::vector<unsigned char>& code, const char* entry,
			const std::string& origin, SDL_GPUShaderStage stage, u32 samplers, u32 storage_buffers, u32 uniform_buffers);
		// A project's own material shader: reads the .metal text, or compiles the .glsl to SPIR-V with glslc.
		bool load_material_shader(const std::string& path, SDL_GPUShaderStage stage, std::vector<unsigned char>& out);
		bool create_pipelines();
		bool create_pipeline_set(PipelineSet& set, u32 stride, bool skinned_layout);
		SDL_GPUGraphicsPipeline* create_scene_pipeline(SDL_GPUShader* vs, SDL_GPUShader* fs, u32 stride, bool skinned_layout,
			SDL_GPUFillMode fill, SDL_GPUCullMode cull, bool depth_test, bool depth_write, SDL_GPUCompareOp depth_op, u32 blend_mode,
			bool front_ccw = false);
		SDL_GPUGraphicsPipeline* create_fullscreen_pipeline(SDL_GPUShader* vs, SDL_GPUShader* fs, SDL_GPUTextureFormat color_format,
			bool depth, bool blend);
		bool create_dynamic_buffers();
		bool ensure_target(GpuTarget& target, u32 w, u32 h, bool with_depth, bool readback);
		void release_target(GpuTarget& target);
		void release_pipeline_set(PipelineSet& set);
		void wait_idle();
		unsigned long long file_mtime(const std::string& path) const;
		SDL_GPUGraphicsPipeline* get_or_compile_pipeline(const std::string& path);
		// The custom pipeline variant a draw needs: blend mode (#333) × double-sided × mirrored (#334), compiled on demand.
		SDL_GPUGraphicsPipeline* get_or_compile_pipeline(const std::string& path, u32 blend_mode, bool double_sided, bool mirrored, bool skinned = false);
		SDL_GPUGraphicsPipeline* custom_pipeline(u32 material_id, u32 blend_mode, bool double_sided, bool mirrored, bool skinned = false);

		// ---- frame helpers (SdlGpuRenderer.cpp) ----
		void render_surface(WindowSurface& surface, int slot);
		void composite_and_present(SDL_GPUCommandBuffer* cmd, WindowSurface& surface, int slot, SDL_GPUTexture* swapchain, u32 w, u32 h);
		void fullscreen_blit(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* src, SDL_GPUTexture* dst, u32 w, u32 h,
			SDL_GPUGraphicsPipeline* pipeline, const PostFxCB* postfx, SDL_GPUTexture* second = nullptr);
		bool capture_target_to_bmp(GpuTarget& target, const char* path);
		bool download_target(GpuTarget& target);

		// ---- scene helpers (SdlGpuRenderer_Scene.cpp) ----
		FrameView build_main_view(u32 width, u32 height);
		FrameView build_camera_view(const ViewportCamera& camera, u32 width, u32 height);
		void fill_light_buffer();
		// Keep the MAX_POINT_LIGHTS / MAX_SPOT_LIGHTS most relevant lights for this camera (#335); a no-op below the caps.
		void prioritize_lights();
		u32 m_light_cap_log{ 0 };
		void upload_staged_bone_palettes();
		void prepare_scene(const FrameView& view);   // sort + cull + pack (CPU) into the staging vectors
		void upload_dynamic(SDL_GPUCommandBuffer* cmd);
		void record_scene(SDL_GPUCommandBuffer* cmd, GpuTarget& target, const FrameView& view, bool draw_skybox, bool draw_grid, bool draw_gizmos,
			int particle_world = -1, bool particle_depth_capture = false);
		SdlGpuParticles::View particle_view(const FrameView& view) const;
		SdlGpuParticles::Environment particle_environment(const FrameView& view) const;
		void draw_skybox(SDL_GPURenderPass* pass, SDL_GPUCommandBuffer* cmd, const FrameView& view);
		// sky gradient -> PerFrame env colours (specular reflections of metals, the water's sky); w = 0 falls back to the
		// neutral env. A texture sky carries the gradient colours as its environment too (the managed side sets them to
		// the HDR's analysed zenith / horizon / ground, so a lake under an HDRI reflects that sky).
		void fill_environment(PerFrameConstants& f) const
		{
			const float on = (m_skybox_enabled && (m_skybox_mode == SkyboxMode::Gradient || m_skybox_mode == SkyboxMode::Texture)) ? 1.0f : 0.0f;
			const bool map = on > 0.5f && m_env_map != id::invalid_id && m_env_mips > 0;
			f.env_sky = { m_sky_color.x, m_sky_color.y, m_sky_color.z, map ? 2.0f : on };   // 2 = a reflection map is bound at slot 10
			f.env_horizon = { m_horizon_color.x, m_horizon_color.y, m_horizon_color.z, map ? (float)m_env_mips : 0.0f };
			f.env_ground = { m_ground_color.x, m_ground_color.y, m_ground_color.z, 0.0f };
		}
		void draw_grid(SDL_GPURenderPass* pass, SDL_GPUCommandBuffer* cmd, const FrameView& view);
		void record_runs(SDL_GPURenderPass* pass, SDL_GPUCommandBuffer* cmd, size_t run_begin, size_t run_end, const FrameView& view, u32 phase = 3u);   // phase bits: 1 opaque, 2 transparent
		void draw_gizmos(SDL_GPURenderPass* pass, SDL_GPUCommandBuffer* cmd);
		void bind_material(SDL_GPURenderPass* pass, SDL_GPUCommandBuffer* cmd, Material* mat, PerObjectConstants& obj, bool gizmo);
		void push_frame_uniforms(SDL_GPUCommandBuffer* cmd, const PerFrameConstants& frame);
		void draw_mesh(SDL_GPURenderPass* pass, Mesh* mesh, u32 instance_base, u32 instance_count);
		const PipelineSet& pipelines_for(const Mesh* mesh) const { return (mesh && mesh->vertex_stride() == 52) ? m_pipelines_skinned_stride : m_pipelines; }

		// ---- state ----
		bool m_initialized{ false };
		bool m_sdl_video_inited_here{ false };
		SDL_GPUDevice* m_device{ nullptr };
		std::string m_gpu_name{ "Metal GPU" };
		SDL_GPUTextureFormat m_scene_format{ SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM };
		SDL_GPUTextureFormat m_present_format{ SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM };
		SDL_GPUTextureFormat m_depth_format{ SDL_GPU_TEXTUREFORMAT_D32_FLOAT };
		WindowSurface m_main;
		WindowSurface m_game;
		u32 m_width{ 0 }, m_height{ 0 };
		// Editor-embedded main surface: the toolkit's native view/window the engine renders into — an NSView
		// on macOS, an X11 Window id (XID) on Linux. The remaining members are the macOS re-parenting dance:
		// SDL's Metal view moved into the host view, and the
		// toolkit's responder chain that SDL replaces while wrapping the view (restored in claim_window).
		void* m_host_view{ nullptr };
		void* m_host_metal_view{ nullptr };
		void* m_host_content_view{ nullptr };
		void* m_host_content_prev_responder{ nullptr };
		void* m_host_window_prev_responder{ nullptr };
		// X11 only: Xlib loaded on demand to hand the embedded window's input masks back to the toolkit.
		SDL_SharedObject* m_x11_lib{ nullptr };
		SDL_FunctionPointer m_x11_select_input{ nullptr };
		SDL_FunctionPointer m_x11_flush{ nullptr };
		SDL_SharedObject* m_xi2_lib{ nullptr };
		SDL_FunctionPointer m_xi2_select_events{ nullptr };
		std::string m_shader_dir;
		std::string m_shader_dir_override;
		std::unordered_map<std::string, std::vector<unsigned char>> m_shader_blobs;

		SDL_GPUShader* m_vs_standard{ nullptr };
		SDL_GPUShader* m_vs_skinned{ nullptr };
		SDL_GPUShader* m_fs_standard{ nullptr };
		SDL_GPUShader* m_vs_grid{ nullptr };
		SDL_GPUShader* m_fs_grid{ nullptr };
		SDL_GPUShader* m_vs_sky{ nullptr };
		SDL_GPUShader* m_fs_sky{ nullptr };
		SDL_GPUShader* m_vs_blit{ nullptr };
		SDL_GPUShader* m_fs_blit{ nullptr };
		SDL_GPUShader* m_fs_postfx{ nullptr };
		PipelineSet m_pipelines;                 // stride 32
		PipelineSet m_pipelines_skinned_stride;  // stride 52, rigid shading (bind pose / no palette)
		SDL_GPUGraphicsPipeline* m_pipeline_skinned{ nullptr };
		SDL_GPUGraphicsPipeline* m_pipeline_skinned_m{ nullptr };   // mirrored skinned instances (#334)
		SDL_GPUGraphicsPipeline* m_pipeline_grid{ nullptr };
		SDL_GPUGraphicsPipeline* m_pipeline_skybox{ nullptr };
		SDL_GPUGraphicsPipeline* m_pipeline_blit{ nullptr };
		SDL_GPUGraphicsPipeline* m_pipeline_postfx{ nullptr };
		SDL_GPUSampler* m_sampler_linear_wrap{ nullptr };
		SDL_GPUSampler* m_sampler_linear_clamp{ nullptr };
		std::unordered_map<u32, CustomShader> m_custom_shaders;
		std::unordered_map<std::string, CachedPipeline> m_pipeline_cache;

		// dynamic GPU data
		SDL_GPUBuffer* m_instance_buffer{ nullptr };
		SDL_GPUTransferBuffer* m_instance_transfer{ nullptr };
		SDL_GPUBuffer* m_bone_buffer{ nullptr };
		SDL_GPUTransferBuffer* m_bone_transfer{ nullptr };
		std::vector<float> m_instance_staging;   // packed per-instance world matrices for this frame (16 floats each)
		u32 m_instance_count{ 0 };
		std::vector<float> m_bone_submit, m_bone_render;
		bool m_bone_upload_pending{ false };
		bool m_bone_dirty{ false };

		// queues
		std::vector<RenderItem> m_render_queue, m_submit_queue;
		// Static / dynamic split (#364 A): the static set is submitted once (begin_static_scene .. end_static_scene) and
		// kept; per-frame submits (the moving entities) go to m_submit_queue as before. swap_render_queue merges both.
		std::vector<RenderItem> m_static_submit, m_static_render;
		bool m_static_pending{ false };
		int m_submit_target{ 0 };   // 0 = dynamic (per frame), 1 = static (retained)
		std::vector<RenderItem> m_gizmo_render, m_gizmo_submit;
		std::vector<RenderItem> m_gizmo_wire_render, m_gizmo_wire_submit;
		std::mutex m_queue_mutex;
		std::vector<DrawRun> m_draw_runs;
		std::vector<u32> m_item_run;
		std::vector<unsigned char> m_item_lod;
		bool m_queue_dirty{ true };
		u32 m_seen_mesh_generation{ 0 };   // ResourceRegistry::mesh_generation() the cached draw runs were built against
		bool m_mt_enabled{ true }, m_mt_force{ false }, m_mt_active{ false };
		u32 m_gizmo_instance_base{ 0 };

		// camera / lights / look
		DirectX::XMFLOAT3 m_camera_position{ 0.0f, 5.0f, -10.0f };
		DirectX::XMFLOAT3 m_camera_target{ 0.0f, 0.0f, 0.0f };
		DirectX::XMFLOAT3 m_camera_up{ 0.0f, 1.0f, 0.0f };
		float m_fov_degrees{ 60.0f };
		float m_aspect_ratio{ 16.0f / 9.0f };
		float m_near_clip{ 0.1f };
		float m_far_clip{ 1000.0f };
		float m_viewmodel_fov{ 54.0f };
		// Viewmodel depth range: first-person weapons sit 2-5 cm in front of the eye when aiming (rear sight, optics),
		// so the layer-1 pass needs a much closer near plane than the world (its depth buffer is cleared separately).
		static constexpr float VIEWMODEL_NEAR = 0.01f;
		static constexpr float VIEWMODEL_FAR = 200.0f;
		DirectX::XMFLOAT3 m_light_direction{ 0.3f, -1.0f, 0.5f };
		DirectX::XMFLOAT3 m_light_color{ 1.0f, 0.98f, 0.95f };
		float m_directional_intensity{ 1.0f };
		float m_ambient_strength{ 0.3f };
		bool m_dir_cast_shadows{ false };
		float m_dir_shadow_strength{ 1.0f }, m_dir_shadow_bias{ 0.0008f }, m_dir_shadow_distance{ 80.0f };
		std::vector<PointLightData> m_point_lights;
		std::vector<SpotLightData> m_spot_lights;
		PerFrameConstants m_frame_constants{};
		LightBufferData m_light_data{};

		// shadows (see SdlGpuRenderer_Shadows.cpp)
		SDL_GPUTexture* m_shadow_atlas{ nullptr };        // spot tiles, 2*SHADOW_TILE_SIZE square, D32
		SDL_GPUTexture* m_csm_atlas{ nullptr };           // cascade tiles, 2*SHADOW_TILE_SIZE square
		SDL_GPUTexture* m_point_atlas{ nullptr };         // POINT_ATLAS_COLS x 3 tiles of POINT_SHADOW_TILE
		SDL_GPUSampler* m_sampler_shadow{ nullptr };      // comparison sampler (LESS_OR_EQUAL, clamp)
		// 1x1 D32 depth texture cleared to 1.0 ("nothing occludes"), bound to the shadow slots whenever an
		// atlas is missing (shadows off, or before ensure_shadow_resources() has run). The shadow slots are
		// declared as comparison-sampled depth textures (sampler2DShadow / depth2d), so a colour texture with
		// a non-comparison sampler is not a legal binding for them under Vulkan.
		SDL_GPUTexture* m_shadow_dummy{ nullptr };
		// the opaque colour + depth so far, copied before the transparent pass for the water's refraction / SSR (#200);
		// bound at fragment slots 11 / 12 while valid, the white texture / the shadow dummy otherwise
		SDL_GPUTexture* m_scene_color_copy{ nullptr };
		SDL_GPUTexture* m_scene_depth_copy{ nullptr };
		u32 m_scene_copy_w{ 0 }, m_scene_copy_h{ 0 };
		bool m_scene_copy_valid{ false };
		u32 m_transparent_runs_seen{ 0 };   // record_runs: how many transparent runs the last scan found
		void copy_scene_for_transparents(SDL_GPUCommandBuffer* cmd, const GpuTarget& target);
		SDL_GPUShader* m_vs_shadow{ nullptr };
		SDL_GPUShader* m_vs_shadow_skinned_layout{ nullptr };
		SDL_GPUShader* m_fs_shadow{ nullptr };
		SDL_GPUGraphicsPipeline* m_pipeline_shadow{ nullptr };      // stride 32
		SDL_GPUGraphicsPipeline* m_pipeline_shadow_52{ nullptr };   // stride 52 (skinned layout, rigid pose)
		// cut-out casters (#329): uv through to a fragment stage that discards by the albedo alpha; optional
		SDL_GPUShader* m_vs_shadow_cut{ nullptr };
		SDL_GPUShader* m_vs_shadow_cut_skinned_layout{ nullptr };
		SDL_GPUShader* m_fs_shadow_cut{ nullptr };
		SDL_GPUGraphicsPipeline* m_pipeline_shadow_cut{ nullptr };
		SDL_GPUGraphicsPipeline* m_pipeline_shadow_cut_52{ nullptr };
		SDL_GPUBuffer* m_shadow_instance_buffer{ nullptr };
		SDL_GPUTransferBuffer* m_shadow_instance_transfer{ nullptr };
		std::vector<float> m_shadow_staging;              // packed caster world matrices for this frame
		// Casters gathered ONCE per frame (#363): every tile tests precomputed world-space spheres instead of scanning
		// the queue with a map lookup and a matrix transform per item (up to 19 tiles x N). m_shadow_bounds (mesh ->
		// local centre + radius) is kept across frames and dropped when a mesh was destroyed.
		struct ShadowCaster { id::id_type mesh; const DirectX::XMFLOAT4X4* world; Mesh* meshp; Material* mat; bool cut; float cx, cy, cz, r; };
		std::vector<ShadowCaster> m_shadow_casters, m_shadow_tile_casters;
		std::unordered_map<id::id_type, DirectX::XMFLOAT4> m_shadow_bounds;
		u32 m_shadow_bounds_generation{ 0 };
		std::chrono::steady_clock::time_point m_shadow_drop_logged{};
		bool m_shadows_ready{ false };
		struct ShadowDrawSeg { Mesh* mesh; u32 instance_base; u32 instance_count; Material* cut_mat; };   // cut_mat: alpha-tested casters (#329)
		struct ShadowTile { u32 x, y, size; DirectX::XMFLOAT4X4 vp; u32 seg_begin, seg_end; };
		std::vector<ShadowDrawSeg> m_shadow_segs;
		std::vector<ShadowTile> m_shadow_tiles_spot, m_shadow_tiles_csm, m_shadow_tiles_point;
		struct ShadowSpot { int spot_index; DirectX::XMFLOAT4X4 vp; };
		ShadowSpot m_shadow_spots[MAX_SHADOW_SPOTS]{};
		u32 m_shadow_spot_count{ 0 };
		DirectX::XMFLOAT4X4 m_csm_vp[CSM_CASCADES]{};
		float m_csm_splits[CSM_CASCADES]{};
		u32 m_csm_count{ 0 };
		struct ShadowPoint { int light_index; float strength; float bias; DirectX::XMFLOAT4X4 face_vp[6]; };
		ShadowPoint m_shadow_points[MAX_SHADOW_POINTS]{};
		u32 m_shadow_point_count{ 0 };
		bool m_ssao_enabled{ false }; float m_ssao_radius{ 0.6f }; float m_ssao_intensity{ 1.0f };
		PostFx m_postfx;
		std::chrono::steady_clock::time_point m_time_origin{ std::chrono::steady_clock::now() };
		bool m_wireframe_mode{ false };
		bool m_vsync_enabled{ false }, m_vsync_dirty{ true };
		float m_render_scale{ 1.0f };
		int m_dlss_mode{ 0 }, m_fg_mode{ 0 };
		bool m_grid_visible{ true };
		float m_grid_spacing{ 1.0f }, m_grid_major_interval{ 10.0f }, m_grid_extent{ 100.0f };
		bool m_gizmos_visible{ true };
		bool m_skybox_enabled{ true };
		SkyboxMode m_skybox_mode{ SkyboxMode::Gradient };
		id::id_type m_sky_texture{ id::invalid_id };
		float m_sky_exposure{ 1.0f };
		int m_sky_hdr{ 0 };   // 0 = 8-bit sky, 1 = float sky shown the classic way, 2 = float sky through exposure + tonemapper
		float m_sky_sh[27]{};
		bool m_sky_sh_on{ false };
		// the sky as a reflection (IBL step 2): a registry equirect with mips, bound at fragment slot 10 while valid
		id::id_type m_env_map{ id::invalid_id };
		int m_env_mips{ 0 };
		float m_sky_rotation{ 0.0f };
		DirectX::XMFLOAT3 m_sky_color{ 0.3f, 0.5f, 0.85f };
		DirectX::XMFLOAT3 m_horizon_color{ 0.7f, 0.8f, 0.9f };
		DirectX::XMFLOAT3 m_ground_color{ 0.25f, 0.25f, 0.28f };
		DirectX::XMFLOAT3 m_sun_direction{ 0.3f, -1.0f, 0.5f };
		DirectX::XMFLOAT3 m_sun_color{ 1.0f, 0.95f, 0.85f };
		float m_sun_intensity{ 1.0f };
		float m_render_distance{ 0.0f };
		// geometric LOD is on by default (#360): the thresholds are multiples of each mesh's radius (40 / 120 radii)
		bool m_lod_enabled{ false }, m_geo_lod_enabled{ true };
		float m_lod_mid{ 40.0f }, m_lod_far{ 120.0f };
		float m_clear_color[4]{ 0.18f, 0.18f, 0.20f, 1.0f };

		// overlay + particles + capture + stats
		SdlGpuOverlay m_overlay;
		SdlGpuParticles m_particles;
		SdlGpuDecals m_decals;                    // the projected decal pass (#120)
		SdlGpuVolumetrics m_volumetrics;          // volumetric fog (#119)
		std::vector<decals::Decal> m_decal_list;  // the frame's decals (refilled on every scene submit, like the lights)
		bool m_capture_requested{ false };
		std::string m_capture_path;
		int m_current_fps{ 0 }, m_frame_count{ 0 };
		std::chrono::steady_clock::time_point m_last_fps_time{ std::chrono::steady_clock::now() };
		int m_draw_call_count{ 0 }, m_vertex_count{ 0 }, m_instances_tested{ 0 }, m_instances_drawn{ 0 };
		int m_shadow_draw_count{ 0 }, m_post_draw_count{ 0 };

		// secondary targets
		std::unordered_map<u32, std::unique_ptr<GpuTarget>> m_render_targets;
		u32 m_next_render_target_id{ 1 };
	};
}
