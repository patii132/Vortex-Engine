#pragma once

#include "../../Common/CommonHeaders.h"
#include "../../Common/Id.h"
#include "DX12Core.h"
#include "DX12CommandQueue.h"
#include "DX12Swapchain.h"
#include "DX12Pipeline.h"
#include "DX12Pipeline3D.h"
#include "DX12GridPipeline.h"
#include "DX12SkyboxPipeline.h"
#include "DX12Particles.h"       // the particle / VFX pass (#117)
#include "DX12Decals.h"          // the projected decal pass (#120)
#include "DX12Volumetrics.h"     // volumetric fog (#119)
#include "../Volumetrics/Volumetrics.h"
#include "DX12FrameRing.h"
#include "../Decals/Decals.h"
#include "DX12UpscalePipeline.h"
#include "DX12PostFxChain.h"
#include "DX12MotionVectorPipeline.h"
#include "DX12DepthBuffer.h"
#include "DX12RenderTarget.h"
#include "DX12Streamline.h"   // DLSS SR + Frame Generation (inline set_fg_mode/fg_presented_fps below)
#include "DX12Geometry.h"
#include "CullWorkerPool.h"
#include "UIOverlay.h"
#include "../Resources/Mesh.h"
#include <d3d12.h>
#include <wrl/client.h>
#include <vector>
#include <chrono>
#include <mutex>
#include <string>
#include <unordered_map>


namespace vortex::graphics { class Material; }   // ShadowCaster keeps the material of a cut-out caster (#329)

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	// Maximum number of object INSTANCES that can be rendered per frame (render-queue + instance-buffer cap).
	// High so GPU instancing can draw large crowds — e.g. thousands of copies of a multi-submesh model.
	constexpr u32 MAX_RENDER_OBJECTS = 262144;

	// Maximum distinct (mesh, material) DRAW RUNS per frame. This sizes the per-object constant buffer, which
	// holds ONE slot per run (not per instance), so it stays tiny even with a huge instance cap.
	constexpr u32 MAX_DRAW_RUNS = 8192;

	// Maximum number of secondary render targets
	constexpr u32 MAX_RENDER_TARGETS = 8;

	// Bone-palette capacity (float4x4 matrices) for GPU skinning. 65536 x 64 B = 4 MB persistently-mapped
	// UPLOAD buffer, split into TWO halves that alternate per upload: the CPU always writes the half the
	// in-flight frames are NOT reading (uploads happen after the frame-start fence wait, which guarantees
	// the 2-frames-back user of the same half has finished) — no torn palettes on animated characters.
	constexpr u32 MAX_BONE_MATRICES = 65536;
	constexpr u32 MAX_BONE_MATRICES_PER_FRAME = MAX_BONE_MATRICES / 2;   // per-half capacity (~250 chars x 128 bones)

	// RenderItem.bone_offset value that marks a RIGID (non-skinned) item.
	constexpr u32 NO_BONES = 0xFFFFFFFFu;

	// Editor transform gizmos render in a dedicated always-on-top pass. They reuse the TAIL slots of the per-object
	// CB + instance VB (the editor scene never fills 8192 draw-runs / 262144 instances), so no extra buffers are
	// needed. This caps how many gizmo meshes draw per frame (move=6, scale=7, rotate=3*segments).
	constexpr u32 MAX_GIZMO_ITEMS = 512;

	struct RendererDesc
	{
		HWND hwnd{ nullptr };
		u32 width{ 0 };
		u32 height{ 0 };
	};

	struct RenderItem
	{
		id::id_type mesh_id{ id::invalid_id };
		id::id_type material_id{ id::invalid_id };
		DirectX::XMFLOAT4X4 world_matrix;
		DirectX::XMFLOAT4 color{ 1.0f, 1.0f, 1.0f, 1.0f };   // per-instance tint (#331), multiplied with the material's base colour
		// GPU skinning: offset (in matrices) into the frame's bone-palette buffer + palette size.
		// NO_BONES = rigid item (the default; every existing submit path is untouched).
		u32 bone_offset{ NO_BONES };
		u32 bone_count{ 0 };
		// Render layer (#175): 0 = world, 1 = first-person VIEWMODEL — drawn in a second pass after a
		// depth clear with its own projection, excluded from shadow casting + SSAO + frustum cull.
		u32 layer{ 0 };
		u32 mirrored{ 0 };   // det(world) < 0: the instance is wound the other way (#334), set at submit
	};
	
	/// <summary>
	/// Camera parameters for secondary viewports.
	/// </summary>
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

	class DX12Renderer
	{
	public:
		static DX12Renderer& instance();

		bool initialize(const RendererDesc& desc);
		void shutdown();
		void resize(u32 width, u32 height);
		void render_frame();
		// Swap the submit/render queues (thread-safe) WITHOUT presenting. Lets offscreen
		// thumbnail/preview renders pick up a freshly-submitted item without flashing the main
		// swapchain (render_frame both swaps AND presents, which caused the asset-browser white-flash).
		void swap_render_queue();

		// Scene-transition hook: make the GPU idle BEFORE the managed layer frees the old scene's meshes
		// (DeleteMesh has no flush -> in-flight use-after-free) and drop the UI overlay's cached wrapped
		// back-buffer bitmaps + the stale render queue so nothing re-renders freed lobby geometry.
		void on_scene_switch();

		// Capture the NEXT presented frame to a 32-bit BMP at 'path'. Reliable verification: GDI window
		// capture (BitBlt/PrintWindow) reads a FLIP_DISCARD swapchain's stale redirection surface and
		// cannot be trusted — this copies the actual back buffer the GPU produced.
		void request_capture(const char* path);

		// Standalone game window: a SECOND swapchain on its own HWND that shares this renderer's device
		// and command queue (the editor viewport keeps its own swapchain). render_game_window() renders
		// the current scene through the current camera into the game window and presents it. This is the
		// "real exe window" play mode — NOT the offscreen render-target/readback path.
		bool create_game_window(HWND hwnd, u32 width, u32 height);
		void render_game_window();
		void resize_game_window(u32 width, u32 height);
		void destroy_game_window();
		bool is_game_window_active() const { return m_game_window_active; }

		bool is_initialized() const { return m_initialized; }

		// Render queue
		void submit_render_item(const RenderItem& item);
		// Submit an editor GIZMO mesh — goes into the separate gizmo queue and renders always-on-top (depth-disabled)
		// in render_gizmos() after the scene. Used by the editor's move/rotate/scale gizmos + selection outline.
		void submit_gizmo_item(const RenderItem& item);
		// Same always-on-top pass, but rasterized as WIREFRAME (fine triangle net) — one draw renders a whole
		// audio range sphere / reverb-zone shape as thin lines instead of hundreds of scaled-cube edge segments.
		void submit_gizmo_wire_item(const RenderItem& item);
		// Decals (#120): the scene submit clears and refills the list (DecalApi.cpp, like the lights); the world pass draws
		// it between the opaque and the transparent meshes.
		void clear_decals() { m_decal_list.clear(); }
		void add_decal(const decals::Decal& d) { if (m_decal_list.size() < decals::MAX_DECALS) m_decal_list.push_back(d); }
		const std::vector<decals::Decal>& decal_list() const { return m_decal_list; }
		// Volumetric fog (#119): persistent like the analytic fog (scene settings / Atmosphere.SetVolumetricFog).
		void set_volumetric_fog(const volumetrics::Params& p) { m_volumetrics.set_params(p); }
		const volumetrics::Params& volumetric_fog() const { return m_volumetrics.params(); }
		// Submit `count` instances of the SAME mesh+material in ONE call (world_matrices = count * 16 floats,
		// row-major 4x4 each). Avoids one P/Invoke per instance — the path for spawning large crowds.
		// colors (#331): count x (r, g, b, a) per instance, or nullptr for untinted
		void submit_mesh_instances(id::id_type mesh, id::id_type material, const float* world_matrices, u32 count, u32 layer = 0, const float* colors = nullptr);
		// Submit a SKINNED mesh: world matrix + this frame's bone palette (bone_count row-major 4x4s,
		// each = inverseBind * boneWorld — computed by the managed AnimationService). The palette is staged
		// CPU-side and uploaded to the GPU bone buffer on the next queue swap. Skinned items must be
		// re-submitted every frame their pose changes (they are inherently dynamic).
		void submit_skinned_item(id::id_type mesh, id::id_type material, const float* world_matrix,
			const float* bone_matrices, u32 bone_count, u32 layer = 0, const float* color = nullptr);
		void clear_render_queue();
		void begin_static_scene();
		void end_static_scene();

		// Camera
		void set_camera(const DirectX::XMFLOAT3& position, const DirectX::XMFLOAT3& target, const DirectX::XMFLOAT3& up);


	// Light settings
		void set_directional_light(const DirectX::XMFLOAT3& direction, const DirectX::XMFLOAT3& color);
		void set_ambient_strength(float strength);
		
		// Multi-light system
		static constexpr u32 MAX_POINT_LIGHTS = 16;
		static constexpr u32 MAX_SPOT_LIGHTS = 8;
		static constexpr u32 MAX_SUBMITTED_LIGHTS = 1024;   // kept per frame; the most relevant fill the slots above (#335)
		
		struct PointLightData
		{
			DirectX::XMFLOAT3 position;
			float range;
			DirectX::XMFLOAT3 color;
			float intensity;
			// Point shadows (#25): the first MAX_SHADOW_POINTS submitted point lights with
			// cast_shadows get a 6-face cube block in the point shadow atlas. CPU-side only;
			// the 32-byte GPUPointLight ABI is untouched (shadow data travels in the b2 tail).
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
			// Spot shadows (#23): the FIRST submitted spot with cast_shadows != 0 becomes this frame's
			// shadow-casting light (ONE shadow map in v1 — the flashlight). CPU-side only; the 64-byte
			// GPUSpotLight ABI is untouched.
			u32 cast_shadows{ 0 };
			float shadow_strength{ 1.0f };   // 0..1 — how dark the occluded area gets
			float shadow_bias{ 0.0015f };    // receiver-side depth-comparison bias
			u32 shadow_resolution{ 2048 };   // shadow-map size in texels
		};
		
		void clear_lights();
		// SSAO (#32): scene-look state like fog — shows in every view (editor + game). radius in
		// world units, intensity 0..~2; enabled=false or intensity 0 = the pass never records.
		void set_ssao(bool enabled, float radius, float intensity)
		{
			m_ssao_enabled = enabled && intensity > 0.001f;
			m_ssao_radius = radius > 0.05f ? radius : 0.05f;
			m_ssao_intensity = intensity < 0.0f ? 0.0f : intensity;
		}
		// #24: the directional light can now cast cascaded shadows. Defaults keep every existing
		// caller shadow-free; shadow_distance = the far end of the last cascade (world units).
		void set_directional_light_full(const DirectX::XMFLOAT3& direction, const DirectX::XMFLOAT3& color, float intensity,
			bool cast_shadows = false, float shadow_strength = 1.0f, float shadow_bias = 0.0008f,
			float shadow_distance = 80.0f);
		void add_point_light(const PointLightData& light);
		void add_spot_light(const SpotLightData& light);

		// Fog (Welle A #27): scene-wide exp2 distance/height fog, written straight into the persistent
		// PerFrameConstants fields (density <= 0 disables). Colors are linear 0..1.
		void set_fog(const DirectX::XMFLOAT3& color, float density, float height_y, float height_falloff);

		// Post-FX chain (#28/#29): VortexAPI setters write params(); when active() the frame's
		// composite is redirected through the chain (editor viewport AND game window paths).
		DX12PostFxChain& postfx() { return m_postfx; }
		float elapsed_seconds() const
		{
			return std::chrono::duration<float>(std::chrono::steady_clock::now() - m_time_origin).count();
		}

		// Rendering mode
		void set_wireframe_mode(bool enabled) { m_wireframe_mode = enabled; }
		bool is_wireframe_mode() const { return m_wireframe_mode; }

		// VSync control
		void set_vsync(bool enabled) { m_vsync_enabled = enabled; }
		bool is_vsync_enabled() const { return m_vsync_enabled; }

		// Render-scale (3D rendered into a scaled offscreen RT, then upscaled to the back buffer). 1.0 = native.
		// Stored here now (settings UI wires to it); the scaled-RT + upscale pass reads m_render_scale.
		void set_render_scale(float s) { m_render_scale = s < 0.25f ? 0.25f : (s > 2.0f ? 2.0f : s); }
		float render_scale() const { return m_render_scale; }

		// DLSS mode: 0=off, 1=Quality, 2=Balanced, 3=Performance, 4=UltraPerformance. A non-off mode drives the
		// render-scale (the 3D renders at the mode's fraction; DLSS upscales to native in the upscale slot). Off
		// resets render-scale to native. Only has visible effect on DLSS-capable GPUs (else bilinear upscale).
		void set_dlss_mode(int mode)
		{
			m_dlss_mode = (mode < 0 || mode > 4) ? 0 : mode;
			switch (m_dlss_mode)
			{
				case 1: m_render_scale = 0.667f; break; // Quality
				case 2: m_render_scale = 0.580f; break; // Balanced
				case 3: m_render_scale = 0.500f; break; // Performance
				case 4: m_render_scale = 0.333f; break; // Ultra Performance
				default: m_render_scale = 1.0f;  break; // Off -> native
			}
		}
		int dlss_mode() const { return m_dlss_mode; }

		// DLSS Frame Generation (separate from SR above). mode: 0=off, 1=x2, 2=x3, 3=x4 (= N AI frames inserted at
		// Present per real frame). Forces the scaled-RT path on (even at scale 1.0) so depth + motion vectors exist
		// for DLSS-G. Reflex is enabled/disabled with it inside set_frame_gen. Only effective on FG-capable GPUs.
		void set_fg_mode(int mode)
		{
			m_fg_mode = (mode < 0 || mode > 3) ? 0 : mode;
			DX12Streamline::instance().set_frame_gen(m_fg_mode, m_swapchain.width(), m_swapchain.height());
		}
		int fg_mode() const { return m_fg_mode; }
		// Smoothed presented-FPS RATE (real + AI-generated frames/sec), accumulated once per frame in render_frame.
		// 0 when FG is off. This is the "Shown FPS" — the engine's own get_current_fps counts only REAL frames.
		int fg_presented_fps() const { return m_presented_fps; }

		// Per-material custom shaders: bind a .hlsl (VSMain/PSMain) to a material -> the 3D pass uses a per-material
		// PSO instead of the built-in PBR one. hlsl_path empty = clear (revert to built-in). A compile failure keeps
		// the material on the built-in PSO, so a bad custom shader never black-screens.
		void set_material_shader(u32 material_id, const std::wstring& hlsl_path);
		// Hot-reload: recompile any custom material shader whose .hlsl changed on disk (GPU-idle first; keep the last
		// good PSO on failure). Cheap when nothing changed. Call on window focus / before a material-preview render.
		// Returns how many shaders were recompiled this call (0 = nothing changed).
		int reload_dirty_shaders();
		// Cheap (no-compile) check: does any assigned .hlsl differ on disk from what we last compiled? Used so the
		// hot-reload overlay only appears on a REAL change (never a spurious "already up to date").
		bool any_material_shader_dirty() const;
		// Compile a custom material shader without binding it (the editor's shader check): both stages and a PSO with
		// the engine's input layout / root signature. False = errors holds the compiler output.
		bool validate_material_shader(const std::wstring& hlsl_path, std::string& errors);

		// Grid rendering
		void set_grid_visible(bool visible) { m_grid_visible = visible; }
		bool is_grid_visible() const { return m_grid_visible; }
		void set_grid_settings(float spacing, float major_interval, float extent);

		// Gizmos
		void set_gizmos_visible(bool visible) { m_gizmos_visible = visible; }
		bool are_gizmos_visible() const { return m_gizmos_visible; }

		// Skybox modes
		enum class SkyboxMode : u32
		{
			SolidColor = 0,
			Gradient = 1,
			Texture = 2
		};

		// Skybox
		void set_skybox_enabled(bool enabled) { m_skybox_enabled = enabled; }
		bool is_skybox_enabled() const { return m_skybox_enabled; }
		void set_skybox_mode(SkyboxMode mode);
		// Equirect texture sky (#326): the registry texture the fullscreen sky pass samples in SkyboxMode::Texture.
		void set_environment_map(id::id_type texture, int mips) { m_env_map = texture; m_env_mips = mips; }
		D3D12_GPU_DESCRIPTOR_HANDLE env_map_srv() const;
		void set_skybox_texture(id::id_type texture, float exposure, float rotation_radians)
		{
			m_sky_texture = texture; m_sky_exposure = exposure; m_sky_rotation = rotation_radians;
		}
		SkyboxMode get_skybox_mode() const { return m_skybox_mode; }
		void set_skybox_colors(
			const DirectX::XMFLOAT3& sky_color,
			const DirectX::XMFLOAT3& horizon_color,
			const DirectX::XMFLOAT3& ground_color);
		void set_skybox_solid_color(const DirectX::XMFLOAT3& color);
		void set_sky_light(const float* sh27, bool on) { if (sh27) memcpy(m_sky_sh, sh27, sizeof(m_sky_sh)); m_sky_sh_on = on && sh27 != nullptr; }
		void set_skybox_sun(const DirectX::XMFLOAT3& direction, const DirectX::XMFLOAT3& color, float intensity);

		// Camera projection settings
		void set_projection(float fov_degrees, float aspect, float near_clip, float far_clip);
		// Vertical FOV (degrees) used by the live view camera (editor play + standalone). Settable from the game.
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
		// Viewmodel FOV (#175): the first-person layer's own projection (world FOV distortion never
		// touches the arms/weapon). Wider clamp than the world FOV — ADS zooms want < 30.
		void set_viewmodel_fov(float fov_degrees) { if (fov_degrees >= 10.0f && fov_degrees <= 120.0f) m_viewmodel_fov = fov_degrees; }
		// Particles (#117): the VFX pass. The scene view draws world 0; ParticleSetNextTargetWorld names the world an
		// offscreen render draws (one-shot).
		DX12Particles& particles() { return m_particles; }
		// Generic render-distance cull (world units; 0 = disabled). Set from the game's graphics settings.
		void set_render_distance(float d) { m_render_distance = d >= 0.0f ? d : 0.0f; }
		float render_distance() const { return m_render_distance; }

		// Density LOD: thin out DISTANT instances (keep 1/2 beyond mid, 1/4 beyond far) — the standard crowd/
		// foliage LOD. Deterministic per instance index so there is no per-frame flicker. mid/far are distances.
		void set_lod(bool enabled, float mid, float farD)
		{
			m_lod_enabled = enabled;
			m_lod_mid = mid > 0.0f ? mid : 0.0f;
			m_lod_far = farD > m_lod_mid ? farD : m_lod_mid;
		}

		// GEOMETRIC LOD: distant instances draw a decimated low-poly mesh (the whole crowd stays visible, no
		// thinning/holes). Shares the mid/far distance thresholds. Use INSTEAD of density LOD.
		void set_geometric_lod(bool enabled, float mid, float farD)
		{
			m_geo_lod_enabled = enabled;
			m_lod_mid = mid > 0.0f ? mid : 0.0f;
			m_lod_far = farD > m_lod_mid ? farD : m_lod_mid;
		}

		// Multithreaded culling+packing: parallelize the per-instance frustum/distance test + instance-buffer
		// pack across worker threads (the CPU bottleneck when one mesh is rendered thousands of times). The
		// draw recording stays single-threaded. Auto-gates on instance count; force ignores the threshold.
		void set_multithreading(bool enabled) { m_mt_enabled = enabled; }
		bool is_multithreading() const { return m_mt_enabled; }
		void set_multithreading_force(bool f) { m_mt_force = f; }
		bool mt_active() const { return m_mt_active; }


		// Camera gizmo rendering
		void render_camera_gizmo(
			const DirectX::XMFLOAT3& position,
			const DirectX::XMFLOAT3& forward,
			const DirectX::XMFLOAT3& right,
			const DirectX::XMFLOAT3& up,
			float near_width, float near_height,
			float far_width, float far_height,
			float near_dist, float far_dist,
			const DirectX::XMFLOAT4& color);

		// 2D UI overlay (generic Direct2D/DirectWrite layer drawn over the 3D; driven by the Vortex.UI
		// script API — the game records panels/text/lines each frame, the engine draws them before present).
		void ui_begin(float w, float h) { m_ui_overlay.begin(w, h); }
		void ui_rect(float x, float y, float w, float h, float r, float g, float b, float a, float radius) { m_ui_overlay.add_rect(x, y, w, h, r, g, b, a, radius); }
		void ui_text(float x, float y, float w, float h, const wchar_t* s, float size, float r, float g, float b, float a, int align, int weight) { m_ui_overlay.add_text(x, y, w, h, s, size, r, g, b, a, align, weight); }
		void ui_line(float x1, float y1, float x2, float y2, float r, float g, float b, float a, float thick) { m_ui_overlay.add_line(x1, y1, x2, y2, r, g, b, a, thick); }
		void ui_image(float x, float y, float w, float h, const wchar_t* path, float r, float g, float b, float a) { m_ui_overlay.add_image(x, y, w, h, path, r, g, b, a); }
		// UTF-8 variants (the exports UITextUtf8 / UIImageUtf8 the .NET hosts call; same as the SDL GPU backend).
		// The overlay copies the text, so the converted temporary only has to live for the call.
		void ui_text_utf8(float x, float y, float w, float h, const char* s, float size, float r, float g, float b, float a, int align, int weight) { ui_text(x, y, w, h, utf8_to_wide(s).c_str(), size, r, g, b, a, align, weight); }
		void ui_image_utf8(float x, float y, float w, float h, const char* path, float r, float g, float b, float a) { ui_image(x, y, w, h, utf8_to_wide(path).c_str(), r, g, b, a); }
		static std::wstring utf8_to_wide(const char* s)
		{
			if (!s || !*s) return std::wstring();
			int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
			if (n <= 1) return std::wstring();
			std::wstring w((size_t)n - 1, L'\0');
			MultiByteToWideChar(CP_UTF8, 0, s, -1, &w[0], n);
			return w;
		}
		void ui_push_clip(float x, float y, float w, float h) { m_ui_overlay.push_clip(x, y, w, h); }
		void ui_pop_clip() { m_ui_overlay.pop_clip(); }

		// Performance statistics
		int get_current_fps() const { return m_current_fps; }
		int get_draw_call_count() const { return m_draw_call_count; }
		// draws of the shadow atlases / the SSAO prepass — part of get_draw_call_count (#363)
		int get_shadow_draw_count() const { return m_shadow_draw_count; }
		int get_post_draw_count() const { return m_post_draw_count; }
		int get_vertex_count() const { return m_vertex_count; }
		// Cull stats (per frame): instances examined vs actually drawn — proves cull effectiveness on big maps.
		int get_instances_tested() const { return m_instances_tested; }
		int get_instances_drawn() const { return m_instances_drawn; }

		// ============== Multi-Viewport Rendering ==============
		
		/// <summary>
		/// Create a secondary render target for offscreen rendering.
		/// Returns a unique ID for the render target.
		/// </summary>
		u32 create_render_target(u32 width, u32 height);
		
		/// <summary>
		/// Destroy a render target by ID.
		/// </summary>
		void destroy_render_target(u32 target_id);
		
		/// <summary>
		/// Resize a render target.
		/// </summary>
		bool resize_render_target(u32 target_id, u32 width, u32 height);
		
		/// <summary>
		/// Render the scene to a secondary render target with a specific camera.
		/// </summary>
		void render_to_target(u32 target_id, const ViewportCamera& camera, bool render_grid = false, bool render_gizmos = false);
		
		/// <summary>
		/// Copy render target to staging buffer for CPU readback.
		/// Must be called after render_to_target and before read_render_target_pixels.
		/// </summary>
		bool prepare_render_target_readback(u32 target_id);
		
		/// <summary>
		/// Read pixel data from a render target (after prepare_render_target_readback).
		/// Returns pointer to RGBA8 pixel data, or nullptr if not available.
		/// </summary>
		const void* read_render_target_pixels(u32 target_id, u32& out_width, u32& out_height, u32& out_row_pitch);
		
		/// <summary>
		/// Release the mapped pixel data from read_render_target_pixels.
		/// </summary>
		void release_render_target_pixels(u32 target_id);
		
		/// <summary>
		/// Check if a render target exists.
		/// </summary>
		bool has_render_target(u32 target_id) const;

	private:
		DX12Renderer() = default;
		~DX12Renderer() = default;
		DX12Renderer(const DX12Renderer&) = delete;
		DX12Renderer& operator=(const DX12Renderer&) = delete;

		bool create_command_allocators();
		bool create_command_list();
		bool create_constant_buffers();
		bool create_grid_resources();
		bool create_skybox_resources();
		void update_per_frame_constants();
		// Keep the MAX_POINT_LIGHTS / MAX_SPOT_LIGHTS most relevant lights for this camera (#335); a no-op below the caps.
		void prioritize_lights();
		u32 m_light_cap_log{ 0 };
		void wait_for_previous_frame();

		void render_3d_scene();
		void render_gizmos();   // dedicated always-on-top pass for editor gizmos (depth-disabled PSO)
		// Spot-light shadows (#23): depth-only pass from the shadow spot's view into m_shadow_map.
		// prepare_* runs on the CPU before the per-frame CB upload (selects the shadow spot, builds the
		// light VP, fills the PerFrameConstants shadow fields); render_* records the GPU pass right after
		// the command-list reset (both the scaled and direct paths then sample the same map at t7).
		void prepare_shadow_pass();
		void render_shadow_pass();
		bool ensure_shadow_map(u32 size);   // lazily (re)creates the R32_TYPELESS map + DSV; SRV lives in a
											// RESERVED ResourceRegistry heap slot (the scene pass binds that heap)
		bool ensure_csm_map(u32 size);      // #24: same recipe for the directional cascade atlas (t8)
		void prepare_csm_cascades();        // #24: splits + snapped ortho crops (called by prepare_shadow_pass)
		bool ensure_point_shadow_map();     // #25: the 6-face point shadow atlas (t9)
		void prepare_point_shadows();       // #25: select lights + build the 6 face VPs each
		bool ensure_ssao_targets(int slot, u32 w, u32 h);   // #32: per-view AO depth + AO/blur RTs
		void record_ssao(int slot, u32 view_w, u32 view_h); // #32: prepass -> AO -> blur (before the color pass)
		bool capture_backbuffer_to_bmp(const char* path);
		void render_fallback_triangle();
		void render_grid();
		void render_skybox();

		DX12CommandQueue m_command_queue;
		DX12Swapchain m_swapchain;

		// Active render target for the current pass — set to the main swapchain for the editor frame, or
		// to the game window's swapchain in render_game_window(). The render_* helpers target these so the
		// same recording path serves both windows.
		D3D12_CPU_DESCRIPTOR_HANDLE m_active_rtv{};
		D3D12_CPU_DESCRIPTOR_HANDLE m_active_dsv{};
		ID3D12Resource* m_active_depth{ nullptr };   // the depth resource behind m_active_dsv (the particle pass copies it)
		ID3D12Resource* m_active_color{ nullptr };   // the colour resource behind m_active_rtv (copied for the water, #200)
		D3D12_GPU_DESCRIPTOR_HANDLE m_scene_color_srv{};   // t12 / t13 for the transparent pass: the opaque colour + depth
		D3D12_GPU_DESCRIPTOR_HANDLE m_scene_depth_srv{};   // so far (water refraction / SSR, #200); null until copied
		u32 m_active_width{ 0 };
		u32 m_active_height{ 0 };

		// Standalone game window (second swapchain + depth + allocator; shares device/queue)
		DX12Swapchain m_game_swapchain;
		DX12DepthBuffer m_game_depth;
		ComPtr<ID3D12CommandAllocator> m_game_cmd_allocator;
		bool m_game_window_active{ false };
		DX12Pipeline m_pipeline;           // Simple 2D pipeline (fallback)
		DX12Pipeline3D m_pipeline_3d;      // Full 3D pipeline
		// Custom per-material shaders: material_id -> its compiled PSO + source .hlsl + last-seen mtime (hot-reload).
		struct CustomShader { std::wstring path; unsigned long long mtime{ 0 }; };
		std::unordered_map<u32, CustomShader> m_custom_shaders;
		// Shared PSO cache keyed by .hlsl PATH (not material id): many materials + repeated preview rebuilds reuse ONE
		// compiled PSO, so an assigned shader recompiles only when its file's mtime changes (not every orbit frame).
		// Also drives the hot-reload dirty check so the overlay only appears on a REAL change.
		// One compiled PSO per variant (blend 0/1/2 × double-sided × mirrored = 12), each built the first time a draw
		// asks for it (#333, #334); the mtime of the source it was built from decides whether it is stale.
		// ... each once more for the skinned input layout (#332): 24
		static constexpr u32 PSO_VARIANTS = 24;
		struct CachedPso { ComPtr<ID3D12PipelineState> pso[PSO_VARIANTS]; unsigned long long mtime[PSO_VARIANTS]{}; };
		std::unordered_map<std::wstring, CachedPso> m_pso_cache;
		ComPtr<ID3D12PipelineState> get_or_compile_pso(const std::wstring& hlsl_path);
		ComPtr<ID3D12PipelineState> get_or_compile_pso(const std::wstring& hlsl_path, u32 blend_mode, bool double_sided, bool mirrored, bool skinned = false);
		ID3D12PipelineState* custom_pso(u32 material_id, u32 blend_mode, bool double_sided, bool mirrored, bool skinned = false);
		DX12GridPipeline m_grid_pipeline;  // Grid rendering pipeline
		DX12SkyboxPipeline m_skybox_pipeline; // Skybox rendering pipeline
		DX12Particles m_particles;            // Particle / VFX pass (#117): billboards, ribbons, soft particles, depth collision
		DX12FrameRing m_fx_ring;              // per-frame upload ring shared by the effect passes
		DX12Decals m_decal_pass;              // projected decals (#120)
		DX12Volumetrics m_volumetrics;        // volumetric fog (#119)
		std::vector<decals::Decal> m_decal_list;   // the frame's decals (refilled on every scene submit, like the lights)
		DX12UpscalePipeline m_upscale;        // Fullscreen upscale (render-scale composite + the DLSS slot)
		DX12PostFxChain m_postfx;             // Post-processing chain (#28/#29) between composite and UI overlay
		std::chrono::steady_clock::time_point m_time_origin{ std::chrono::steady_clock::now() };
		DX12RenderTarget m_scaled_rt;         // Offscreen color+depth the 3D renders into when render-scale < 1
		DX12MotionVectorPipeline m_mvec_pipeline; // RG16F velocity pass (DLSS input)
		DX12RenderTarget m_mvec_rt;           // RG16F motion vectors (render res) — DLSS input
		DX12RenderTarget m_dlss_output;       // Full-res DLSS upscaled output (UAV); blitted to the back buffer
		DirectX::XMFLOAT4X4 m_prev_view_projection{}; // previous frame VP (motion vectors + DLSS clipToPrevClip)
		DirectX::XMFLOAT4X4 m_viewmodel_view_projection{}; // view * viewmodel projection (#175); the viewmodel particle layer reads it
		int m_dlss_mode{ 0 };                 // 0=off, 1..4 quality modes
		int m_fg_mode{ 0 };                   // DLSS Frame Generation: 0=off, 1=x2, 2=x3, 3=x4
		DX12DepthBuffer m_depth_buffer;
		DX12Geometry m_geometry;           // Fallback triangle
		UIOverlay m_ui_overlay;            // Direct2D/DirectWrite 2D UI over the 3D


		// Grid mesh
		ComPtr<ID3D12Resource> m_grid_vertex_buffer;
		D3D12_VERTEX_BUFFER_VIEW m_grid_vbv{};
		u32 m_grid_vertex_count{ 0 };

		// Grid constants
		ComPtr<ID3D12Resource> m_grid_cb;
		void* m_grid_cb_mapped{ nullptr };

		struct alignas(256) GridConstants
		{
			DirectX::XMFLOAT4X4 view_projection;
			DirectX::XMFLOAT4X4 inverse_view_projection;
			DirectX::XMFLOAT3 camera_position;
			float grid_spacing;
			float grid_extent;
			float major_line_interval;
			float padding[2];
		};

		ComPtr<ID3D12CommandAllocator> m_command_allocators[DX12Swapchain::MaxBufferCount];
		ComPtr<ID3D12GraphicsCommandList> m_command_list;
		UINT64 m_frame_fence_values[DX12Swapchain::MaxBufferCount]{};

		// Per-frame constants (camera, lighting)
		ComPtr<ID3D12Resource> m_per_frame_cb;
		void* m_per_frame_cb_mapped{ nullptr };
		// #175: 256B PerFrameConstants clone with view x viewmodel-projection — bound as b0 only around
		// the viewmodel draws (the shadow pass's per-tile clone pattern; m_per_frame_cb stays untouched).
		ComPtr<ID3D12Resource> m_viewmodel_cb;
		void* m_viewmodel_cb_mapped{ nullptr };
		float m_viewmodel_fov{ 54.0f };
		// first-person weapons sit 2-5 cm in front of the eye when aiming: the viewmodel pass needs a close near plane
		static constexpr float VIEWMODEL_NEAR = 0.01f;
		static constexpr float VIEWMODEL_FAR = 200.0f;

	struct alignas(256) PerFrameConstants
		{
			DirectX::XMFLOAT4X4 view_projection;
			DirectX::XMFLOAT3 camera_position;
			float padding0;
			DirectX::XMFLOAT3 light_direction;
			float directional_intensity;
			DirectX::XMFLOAT3 light_color;
			float ambient_strength;
			// Point lights (16 max)
			u32 point_light_count;
			u32 spot_light_count;
			u32 padding1[2];
			// Fog (Welle A #27) — APPENDED so all prior offsets stay put; ABI-coupled byte-for-byte
			// to standard.hlsl's PerFrame cbuffer (FogColor @128). density 0 = fog off (the default).
			DirectX::XMFLOAT3 fog_color;
			float fog_density;
			float fog_height_y;        // height-fog reference height (world Y)
			float fog_height_falloff;  // 0 = pure distance fog; >0 = ground mist below fog_height_y
			u32 fog_mode;              // reserved (0 today); density gates the effect
			float fog_padding;
			// Spot-light shadows (#23) — APPENDED after fog (@160), byte-matched to standard.hlsl.
			// Per-spot shadow data lives in GPUSpotLight (strength/bias/slot) and the light buffer's
			// ShadowVP[4] tail; b0 only carries the atlas texel size (all tiles are the same size).
			float shadow_map_texel;    // 1 / tile size (ready for a PCF kernel later)
			u32 shadow_padding[3];
			// SSAO (#32) — APPENDED @176. The standard PS samples t10 only when enabled > 0.5;
			// the UV comes from SV_POSITION / (AO texture dims * 2), so no screen size travels here.
			float ssao_enabled;
			float ssao_padding[3];
			// Sky gradient for specular reflections (#metal-env) — APPENDED @192, byte-matched to standard.hlsl.
			// w of env_sky = 1 when a gradient sky is active (0 = the shader's neutral fallback environment).
			DirectX::XMFLOAT4 env_sky;
			DirectX::XMFLOAT4 env_horizon;
			DirectX::XMFLOAT4 env_ground;
			// water refraction / screen-space reflections (#200): x = near, y = far clip of this view — @240, the struct
			// stays exactly 256 bytes (the shadow passes clone it in fixed 256-byte slots)
			DirectX::XMFLOAT4 camera_clip;
		};
		
		// Separate light buffer for GPU
		struct alignas(16) GPUPointLight
		{
			DirectX::XMFLOAT3 position;
			float range;
			DirectX::XMFLOAT3 color;
			float intensity;
		};
		
		struct alignas(16) GPUSpotLight
		{
			DirectX::XMFLOAT3 position;
			float range;
			DirectX::XMFLOAT3 direction;
			float spot_angle;
			DirectX::XMFLOAT3 color;
			float intensity;
			float inner_spot_angle;
			// Spot shadows (#23): packed into the former padding — the 64-byte ABI is unchanged.
			// shadow_slot: -1 = no shadow; 0..3 = tile index into the shadow ATLAS (up to 4
			// shadow-casting spots per frame, e.g. the flashlight AND an authored scene spot).
			float shadow_strength;
			float shadow_bias;
			float shadow_slot;
		};
		PerFrameConstants m_frame_constants;
		// The 3D pass's root signature + per-frame roots (b0 = frame_cb, b2 = lights, the shadow / CSM / point-shadow /
		// SSAO tables): bound at the start of render_3d_scene and again after a particle pass left its own state.
		void bind_scene_pass(D3D12_GPU_VIRTUAL_ADDRESS frame_cb);
		// Particles (#117): what the VFX pass needs to know about a view / the scene look.
		DX12Particles::View particle_view(const DirectX::XMFLOAT4X4& view_projection, const DirectX::XMFLOAT4X4& viewmodel_projection,
			const DirectX::XMFLOAT3& eye, const DirectX::XMFLOAT3& at, const DirectX::XMFLOAT3& up, float fov_degrees, float aspect,
			float near_clip, float far_clip, bool ortho, float ortho_size) const;
		DX12Particles::Environment particle_environment(const PerFrameConstants& f) const;

		// Per-object constants - matches shader cbuffer PerObject
		ComPtr<ID3D12Resource> m_per_object_cb;
		void* m_per_object_cb_mapped{ nullptr };
		ComPtr<ID3D12Resource> m_instance_vb;          // per-instance world matrices (GPU instancing)
		void* m_instance_vb_mapped{ nullptr };
		// Bone palettes for GPU skinning: staged CPU-side per submit batch (m_bone_submit), swapped with the
		// render queue, then uploaded into the ALTERNATE half of the persistently-mapped buffer AFTER the
		// frame fence wait (see MAX_BONE_MATRICES_PER_FRAME) — root SRV param 8 points into the active half.
		ComPtr<ID3D12Resource> m_bone_vb;
		void* m_bone_vb_mapped{ nullptr };
		std::vector<float> m_bone_submit;   // guarded by m_queue_mutex (like m_submit_queue)
		std::vector<float> m_bone_render;
		bool m_bone_upload_pending{ false };   // set by swap_render_queue; consumed by upload_staged_bone_palettes
		u32 m_bone_active_half{ 0 };           // which buffer half the CURRENT render queue's offsets point into
		// Upload staged palettes into the inactive half + flip. Callers must guarantee that half is not
		// referenced by any still-executing frame (after the frame fence wait, or after a full flush).
		void upload_staged_bone_palettes();
		D3D12_GPU_VIRTUAL_ADDRESS bone_palette_base_va() const
		{
			return m_bone_vb->GetGPUVirtualAddress() + (UINT64)m_bone_active_half * MAX_BONE_MATRICES_PER_FRAME * 64;
		}

		struct alignas(256) PerObjectConstants
		{
		DirectX::XMFLOAT4X4 world;           // 64 bytes
		DirectX::XMFLOAT4 base_color;        // 16 bytes
		float metallic;                       // 4 bytes
		float roughness;                      // 4 bytes
		float ao;                             // 4 bytes
		float normal_strength;                // 4 bytes
		UINT has_albedo_texture;              // 4 bytes
		UINT has_normal_texture;              // 4 bytes
		UINT has_metallic_texture;            // 4 bytes
		UINT has_roughness_texture;           // 4 bytes
	UINT has_ao_texture;                  // 4 bytes
		UINT use_directx_normals;             // 4 bytes
		// Mirrors IsUnlit/EmissiveStrength in the standard.hlsl cbuffer (same 8 bytes that used to be
		// unnamed padding — the shader fields existed but were never fed, so unlit shading never engaged).
		UINT is_unlit;                        // 4 bytes
		float emissive_strength;              // 4 bytes
		// UV tiling (texture repeat scale) — mirrors standard.hlsl's UVTiling at the SAME byte offset (128).
		DirectX::XMFLOAT2 uv_tiling;          // 8 bytes @128
		UINT has_height_texture;              // 4 bytes @136 (parallax/displacement height map bound)
		float height_scale;                   // 4 bytes @140 (parallax depth) -> row 8 full (128-143)
		float alpha_cutoff;                   // 4 bytes @144 (AlphaTest cutoff, #329; 0 = off)
		float _pad0, _pad1, _pad2;            // row 9 (144-159)
		};  // Total: 160 bytes, aligned to 256
		
		// Light constant buffer
		ComPtr<ID3D12Resource> m_light_cb;
		void* m_light_cb_mapped{ nullptr };
		// Sky light (image-based lighting, step 1): SH9 irradiance per channel, appended to the light buffer @2304
		float m_sky_sh[27]{};
		bool m_sky_sh_on{ false };

		// Render queue - double buffered for thread safety
		std::vector<RenderItem> m_render_queue;
		std::vector<RenderItem> m_submit_queue;
		// Static / dynamic split (#364 A): the static set is submitted once (begin_static_scene .. end_static_scene) and
		// kept; per-frame submits (the moving entities) go to m_submit_queue as before. swap_render_queue merges both.
		std::vector<RenderItem> m_static_submit, m_static_render;
		bool m_static_pending{ false };
		int m_submit_target{ 0 };   // 0 = dynamic (per frame), 1 = static (retained)
		// Separate double-buffered queue for editor gizmos so the optimized scene path never touches them; they draw
		// in render_gizmos() after the scene with the depth-disabled gizmo PSO (always on top).
		std::vector<RenderItem> m_gizmo_render;
		std::vector<RenderItem> m_gizmo_submit;
		// Wireframe gizmo queue: same pass, drawn with the wire (fill-mode WIREFRAME) gizmo PSO after the
		// solid items. Shares the tail CB/VB slots — solid + wire together are capped at MAX_GIZMO_ITEMS.
		std::vector<RenderItem> m_gizmo_wire_render;
		std::vector<RenderItem> m_gizmo_wire_submit;
		std::mutex m_queue_mutex;
		
		// Batching structures for instanced rendering
		struct RenderBatch
		{
			id::id_type mesh_id;
			id::id_type material_id;
			u32 start_index;
			u32 instance_count;
		};
		std::vector<RenderBatch> m_batches;

		// One contiguous (mesh,material) run in the sorted render queue. Bounds are precomputed once
		// (single-threaded) so the per-instance cull+pack can run on any worker thread. vbBase reserves a
		// worst-case slab (= item count) in the instance VB so visible instances pack into a disjoint region.
		struct DrawRun
		{
			size_t start; u32 count;
			bool mirrored{ false };   // every instance of the run has a negative-determinant world matrix (#334)
			id::id_type mesh; id::id_type mat;
			Mesh* meshp;
			bool defaultBounds;
			float lcx, lcy, lcz, localR;
			u32 vbBase;
			u32 visible;
			// Geometric LOD (filled at run-build from the mesh's LodChain; lodLevels==1 = no geo-LOD).
			u32 lodLevels{ 1 };
			id::id_type lodMesh[4]{ id::invalid_id, id::invalid_id, id::invalid_id, id::invalid_id };
			float lodT1sq{ 0 }, lodT2sq{ 0 }, lodT3sq{ 0 }; // squared distance thresholds -> LOD1/2/3
			u32 lodCount[4]{ 0, 0, 0, 0 };                  // visible instances per LOD (single-threaded pack)
			// GPU skinning: skinned items form 1-item runs (each needs its own bone palette bound at
			// root param 8). Skinned runs skip geometric LOD (no decimated skin streams exist).
			bool skinned{ false };
			u32 boneOffset{ 0 };
			u32 boneCount{ 0 };
			// #175: runs are layer-homogeneous (layer is the primary sort key + a run-break condition);
			// viewmodel runs cluster at the tail and draw in the second (depth-cleared, own-proj) pass.
			u32 layer{ 0 };
		};
		std::vector<DrawRun> m_draw_runs;     // per-frame scratch (reused)
		std::vector<u32> m_item_run;          // run index per render-queue item (for flat parallel cull)
		std::vector<unsigned char> m_item_lod; // geometric-LOD level per item (0xFF = culled), for the 2-pass MT cull
		bool m_queue_dirty{ true };           // submit queue changed -> re-sort + rebuild runs; else reuse the cached layout
		bool m_mt_enabled{ true };            // master enable for multithreaded cull+pack
		bool m_mt_force{ false };             // ignore the instance-count threshold (testing/forced)
		bool m_mt_active{ false };            // whether MT was used on the last frame (telemetry)
		int  m_mt_threshold{ 2048 };          // min instances before parallelizing
		u32  m_worker_count{ 0 };             // lazily computed = clamp(hw_concurrency-1, 1, 8)
		CullWorkerPool m_cull_pool;           // persistent workers (NEVER spawn threads per frame — debugger death)

		// Camera
		DirectX::XMFLOAT3 m_camera_position{ 0.0f, 3.0f, -8.0f };
		DirectX::XMFLOAT3 m_camera_target{ 0.0f, 0.0f, 0.0f };
		DirectX::XMFLOAT3 m_camera_up{ 0.0f, 1.0f, 0.0f };
		
		// Projection parameters
		float m_fov_degrees{ 60.0f };
		float m_aspect_ratio{ 16.0f / 9.0f };
		float m_near_clip{ 0.1f };
		float m_far_clip{ 1000.0f };
		float m_render_distance{ 0.0f };   // generic distance cull (0 = disabled)
		bool  m_lod_enabled{ false };      // density LOD: thin distant instances
		bool  m_geo_lod_enabled{ true };   // geometric LOD on by default (#360): distant instances use a decimated mesh
		float m_lod_mid{ 40.0f };          // density: keep 1/2 beyond this (metres) | geometric: -> LOD1 beyond this × the mesh radius
		float m_lod_far{ 120.0f };         // density: keep 1/4 beyond this (metres) | geometric: -> LOD2 beyond this × the mesh radius

	// Lighting - balanced for PBR rendering
		DirectX::XMFLOAT3 m_light_direction{ -0.5f, -0.7f, 0.5f };
		DirectX::XMFLOAT3 m_light_color{ 1.0f, 0.98f, 0.95f };
		float m_ambient_strength{ 0.4f };
		float m_directional_intensity{ 3.0f };
		
		// Multi-light storage
		std::vector<PointLightData> m_point_lights;
		std::vector<SpotLightData> m_spot_lights;

		// Rendering settings - Unity-style gray-brown background (like screenshot)
		FLOAT m_clear_color[4]{ 0.18f, 0.18f, 0.20f, 1.0f };
		bool m_wireframe_mode{ false };
		bool m_grid_visible{ true };
		bool m_gizmos_visible{ true };
		bool m_skybox_enabled{ false };
		SkyboxMode m_skybox_mode{ SkyboxMode::Gradient };
		id::id_type m_sky_texture{ id::invalid_id };
		float m_sky_exposure{ 1.0f };
		float m_sky_rotation{ 0.0f };
		// last gradient colours (the skybox pipeline keeps its own copy) -> PerFrame env for metal reflections
		DirectX::XMFLOAT3 m_env_sky{ 0.3f, 0.5f, 0.85f };
		DirectX::XMFLOAT3 m_env_horizon{ 0.7f, 0.8f, 0.9f };
		DirectX::XMFLOAT3 m_env_ground{ 0.25f, 0.25f, 0.28f };
		// the sky as a reflection (IBL step 2): a registry equirect with mips, bound at t11 while valid
		id::id_type m_env_map{ id::invalid_id };
		int m_env_mips{ 0 };
		float m_grid_spacing{ 1.0f };
		float m_grid_major_interval{ 10.0f };
		float m_grid_extent{ 200.0f };
		bool m_vsync_enabled{ false };
		float m_render_scale{ 1.0f };   // 1.0 = render at native res; <1 = scaled offscreen RT + upscale (perf)
		bool m_initialized{ false };

		// Deferred back-buffer capture (set by request_capture, serviced in render_frame before present)
		bool m_capture_requested{ false };
		std::string m_capture_path;

		// Skybox constant buffer
		ComPtr<ID3D12Resource> m_skybox_cb;
		void* m_skybox_cb_mapped{ nullptr };

		// Performance statistics
		int m_current_fps{ 0 };
		int m_draw_call_count{ 0 };
		int m_shadow_draw_count{ 0 }, m_post_draw_count{ 0 };
		int m_vertex_count{ 0 };
		int m_instances_tested{ 0 };
		int m_instances_drawn{ 0 };
		int m_frame_count{ 0 };
		int m_presented_accum{ 0 };  // DLSS-G presented frames accumulated since the last rate update
		int m_presented_fps{ 0 };    // smoothed presented-FPS rate (real + AI frames); 0 when FG off
		std::chrono::high_resolution_clock::time_point m_last_fps_time;
		
		// Secondary render targets for multi-viewport rendering
		std::unordered_map<u32, std::unique_ptr<DX12RenderTarget>> m_render_targets;
		u32 m_next_render_target_id{ 1 };
		
		// Helper for rendering to a specific target
		void render_scene_to_target(DX12RenderTarget* target, const ViewportCamera& camera, bool render_grid, bool render_gizmos = false);

		// Render-scale: (re)create m_scaled_rt at w x h (R8G8B8A8) when the scale/window size changes; idles the
		// GPU first since the RT may be in flight. Returns false if creation failed (caller falls back to direct).
		bool ensure_scaled_rt(u32 width, u32 height);

		// DLSS targets (lazily (re)created): m_mvec_rt at render res (RG16F), m_dlss_output at display res (R8G8B8A8 + UAV).
		bool ensure_mvec_rt(u32 width, u32 height);
		bool ensure_dlss_output(u32 width, u32 height);

		// ---- Spot-light shadow ATLAS (#23) ----
		// ONE 2N x 2N depth atlas holds up to FOUR N x N tiles — up to 4 shadow-casting spots per frame
		// (the flashlight AND authored scene spots), each rendered as its own depth-only sub-pass into
		// its tile viewport. Per-spot VPs travel in the light buffer's ShadowVP[4] tail (b2).
		static constexpr u32 MAX_SHADOW_SPOTS = 4;
		static constexpr u32 SHADOW_TILE_SIZE = 2048;       // per-tile resolution (atlas = 2x2 tiles)
		static constexpr u32 MAX_SHADOW_INSTANCES = 8192;   // packed shadow casters per frame (all tiles)
		ComPtr<ID3D12Resource> m_shadow_map;                // R32_TYPELESS: D32_FLOAT DSV + R32_FLOAT SRV
		ComPtr<ID3D12DescriptorHeap> m_shadow_dsv_heap;
		D3D12_RESOURCE_STATES m_shadow_map_state{ D3D12_RESOURCE_STATE_DEPTH_WRITE };
		D3D12_CPU_DESCRIPTOR_HANDLE m_shadow_srv_cpu{};     // RESERVED slot in ResourceRegistry's shared SRV heap
		D3D12_GPU_DESCRIPTOR_HANDLE m_shadow_srv_gpu{};     // (reserved once; view recreated in place on resize)
		u32 m_shadow_map_size{ 0 };                         // atlas edge length (2 * SHADOW_TILE_SIZE)
		ComPtr<ID3D12Resource> m_shadow_pass_cb;            // MAX_SHADOW_SPOTS x 256B PerFrame clones (light VPs, b0 swap)
		void* m_shadow_pass_cb_mapped{ nullptr };
		ComPtr<ID3D12Resource> m_shadow_instance_vb;        // world matrices of this frame's shadow casters
		void* m_shadow_instance_vb_mapped{ nullptr };
		// Casters gathered ONCE per frame (#363): every tile tests precomputed world-space spheres instead of scanning
		// the queue with a map lookup and a matrix transform per item (up to 19 tiles x N). m_shadow_bounds (mesh ->
		// local centre + radius) is kept across frames and dropped when a mesh was destroyed.
		struct ShadowCaster { id::id_type mesh; const DirectX::XMFLOAT4X4* world; Material* mat; bool cut; float cx, cy, cz, r; };
		// cut-out casters (#329): one 256-byte PerObject slot per cut segment this frame
		static constexpr u32 MAX_SHADOW_CUT_SEGS = 256;
		ComPtr<ID3D12Resource> m_shadow_cut_cb;
		void* m_shadow_cut_cb_mapped{ nullptr };
		std::vector<ShadowCaster> m_shadow_casters, m_shadow_tile_casters;
		std::unordered_map<id::id_type, DirectX::XMFLOAT4> m_shadow_bounds;
		u32 m_shadow_bounds_generation{ 0 };
		std::chrono::steady_clock::time_point m_shadow_drop_logged{};
		// This frame's shadow spots (filled by prepare_shadow_pass, consumed by render_shadow_pass +
		// the GPUSpotLight copy in update_per_frame_constants).
		struct ShadowSpot { int spot_index; DirectX::XMFLOAT4X4 vp; };
		ShadowSpot m_shadow_spots[MAX_SHADOW_SPOTS]{};
		u32 m_shadow_spot_count{ 0 };

		// ---- Directional cascaded shadow maps (#24) ----
		// A SECOND 2x2 atlas (t8, own resource + DSV + reserved SRV slot + state round-trip) holds
		// CSM_CASCADES snapped ortho crops of the camera frustum along the sun direction. The cascade
		// VPs + split distances travel in the light buffer's grown tail (@1280); the depth passes are
		// extra tiles in render_shadow_pass, sharing the caster instance VB with the spot tiles.
		static constexpr u32 CSM_CASCADES = 3;
		ComPtr<ID3D12Resource> m_csm_map;                   // R32_TYPELESS: D32_FLOAT DSV + R32_FLOAT SRV
		ComPtr<ID3D12DescriptorHeap> m_csm_dsv_heap;
		D3D12_RESOURCE_STATES m_csm_map_state{ D3D12_RESOURCE_STATE_DEPTH_WRITE };
		D3D12_CPU_DESCRIPTOR_HANDLE m_csm_srv_cpu{};
		D3D12_GPU_DESCRIPTOR_HANDLE m_csm_srv_gpu{};
		u32 m_csm_map_size{ 0 };
		DirectX::XMFLOAT4X4 m_csm_vp[CSM_CASCADES]{};
		float m_csm_splits[CSM_CASCADES]{};                 // far view-distance of each cascade
		u32 m_csm_count{ 0 };                               // 0 = no directional shadows this frame
		// Directional shadow authoring state (set_directional_light_full).
		bool m_dir_cast_shadows{ false };
		float m_dir_shadow_strength{ 1.0f };
		float m_dir_shadow_bias{ 0.0008f };
		float m_dir_shadow_distance{ 80.0f };

		// ---- Point light cube shadows (#25) ----
		// A THIRD atlas (t9): up to MAX_SHADOW_POINTS point lights x 6 perspective faces of
		// POINT_SHADOW_TILE² each, laid out 4 tiles per row. Face VPs + per-light shadow params
		// travel in the b2 tail after the CSM block; GPUPointLight's 32-byte ABI stays untouched
		// (the shader scans the tiny PointShadow list for its light index).
		static constexpr u32 MAX_SHADOW_POINTS = 2;
		static constexpr u32 POINT_SHADOW_TILE = 1024;
		static constexpr u32 POINT_ATLAS_COLS = 4;      // 12 tiles -> 4096 x 3072

		// ---- SSAO (#32) ----
		// Fully self-contained: a dedicated HALF-RES depth prepass (own R32_TYPELESS target, camera
		// VP straight from b0) feeds an Alchemy-style AO pass + box blur; the blurred result lands
		// in a RESERVED registry SRV slot (t10) and the standard PS multiplies it into the ambient
		// term only. No main-depth plumbing, no PSO depth-func changes — identical on every path.
		// Two view slots like the postfx chain: 0 = main window/viewport, 1 = editor game window.
		static constexpr u32 SSAO_MAX_INSTANCES = 16384;
		struct SsaoView
		{
			ComPtr<ID3D12Resource> depth;               // R32_TYPELESS, half view res
			ComPtr<ID3D12Resource> ao;                  // R8_UNORM raw AO
			ComPtr<ID3D12Resource> blur;                // R8_UNORM blurred AO (sampled at t10)
			ComPtr<ID3D12DescriptorHeap> dsv_heap;      // 1 DSV
			ComPtr<ID3D12DescriptorHeap> rtv_heap;      // 2 RTVs (ao, blur)
			ComPtr<ID3D12DescriptorHeap> srv_heap;      // 2 shader-visible SRVs (depth, ao) for the gen/blur passes
			D3D12_RESOURCE_STATES depth_state{ D3D12_RESOURCE_STATE_DEPTH_WRITE };
			D3D12_RESOURCE_STATES ao_state{ D3D12_RESOURCE_STATE_RENDER_TARGET };
			D3D12_RESOURCE_STATES blur_state{ D3D12_RESOURCE_STATE_RENDER_TARGET };
			u32 w{ 0 }, h{ 0 };
		};
		SsaoView m_ssao_view[2];
		ComPtr<ID3D12RootSignature> m_ssao_root_sig;    // t0 table + 24 root constants + s0 point/clamp
		ComPtr<ID3D12PipelineState> m_ssao_pso;         // ssao.hlsl PSMain (AO generation)
		ComPtr<ID3D12PipelineState> m_ssao_blur_pso;    // ssao.hlsl PSBlur
		ComPtr<ID3D12Resource> m_ssao_instance_vb;      // prepass caster world matrices
		void* m_ssao_instance_vb_mapped{ nullptr };
		D3D12_CPU_DESCRIPTOR_HANDLE m_ssao_reserved_cpu[2]{};  // registry slots the scene pass samples (t10)
		D3D12_GPU_DESCRIPTOR_HANDLE m_ssao_reserved_gpu[2]{};
		D3D12_GPU_DESCRIPTOR_HANDLE m_ssao_current_srv{};      // what render_3d_scene binds this pass
		bool m_ssao_enabled{ false };
		float m_ssao_radius{ 0.6f };
		float m_ssao_intensity{ 1.0f };
		ComPtr<ID3D12Resource> m_point_shadow_map;
		ComPtr<ID3D12DescriptorHeap> m_point_shadow_dsv_heap;
		D3D12_RESOURCE_STATES m_point_shadow_state{ D3D12_RESOURCE_STATE_DEPTH_WRITE };
		D3D12_CPU_DESCRIPTOR_HANDLE m_point_srv_cpu{};
		D3D12_GPU_DESCRIPTOR_HANDLE m_point_srv_gpu{};
		bool m_point_shadow_ready{ false };
		struct ShadowPoint { int light_index; float strength; float bias; DirectX::XMFLOAT4X4 face_vp[6]; };
		ShadowPoint m_shadow_points[MAX_SHADOW_POINTS]{};
		u32 m_shadow_point_count{ 0 };

		// Create SRV descriptor heap for textures
		bool create_srv_heap();
		
		// SRV Descriptor Heap for textures
		ComPtr<ID3D12DescriptorHeap> m_srv_heap;
		UINT m_srv_descriptor_size{ 0 };
		static constexpr UINT MAX_SRV_DESCRIPTORS = 1024;
	};
}
