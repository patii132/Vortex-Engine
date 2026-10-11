#include "../ApiCommon.h"
#include "../../Engine/Common/VerboseLog.h"   // VORTEX_VLOG — chatty per-op logs gated by VORTEX_VERBOSE_LOG=1

// native_window: an HWND on Windows, an NSView* (editor-embedded viewport) or SDL_Window* on macOS.
EDITOR_INTERFACE bool InitializeRenderViewport(void* native_window, unsigned int width, unsigned int height)
{
	runtime::systems::render::viewport_desc desc{};
	desc.native_window = native_window;
	desc.width = width;
	desc.height = height;
	return runtime::systems::render::initialize(desc);
}

EDITOR_INTERFACE void ResizeRenderViewport(unsigned int width, unsigned int height)
{
	runtime::systems::render::resize(width, height);
}

EDITOR_INTERFACE void RenderFrame()
{
	runtime::systems::render::render_frame();
}

// Swap the render queue WITHOUT presenting to the main swapchain. Used by offscreen
// thumbnail/preview rendering so it can pick up its submitted item without flashing the
// editor viewport (calling RenderFrame for this caused the asset-browser white-flash).
EDITOR_INTERFACE void SwapRenderQueue()
{
	graphics::Renderer::instance().swap_render_queue();
}

// Scene-transition hook: GPU idle + drop overlay cache + clear stale queue BEFORE the managed layer
// frees the old scene's meshes (prevents in-flight use-after-free + stale-overlay carryover).
EDITOR_INTERFACE void OnSceneSwitch()
{
	graphics::Renderer::instance().on_scene_switch();
}

// Reliable frame verification: write the NEXT presented back buffer to a 32-bit BMP (GDI window capture
// reads a stale FLIP_DISCARD redirection surface and cannot be trusted).
EDITOR_INTERFACE void CaptureFrame(const char* path)
{
	graphics::Renderer::instance().request_capture(path);
}

// ---- Standalone game window: a SECOND DX12 swapchain on its own HWND (shares device/queue). The
// editor keeps its own swapchain; RenderGameWindow renders the current scene through the current
// camera into the game window. This is the real "exe window" play mode. ----
EDITOR_INTERFACE bool CreateGameWindow(void* native_window, unsigned int width, unsigned int height)
{
#if VORTEX_HAS_DX12
	return graphics::Renderer::instance().create_game_window((HWND)native_window, width, height);
#else
	return graphics::Renderer::instance().create_game_window(native_window, width, height);
#endif
}

EDITOR_INTERFACE void RenderGameWindow()
{
	graphics::Renderer::instance().render_game_window();
}

EDITOR_INTERFACE void ResizeGameWindow(unsigned int width, unsigned int height)
{
	graphics::Renderer::instance().resize_game_window(width, height);
}

EDITOR_INTERFACE void DestroyGameWindow()
{
	graphics::Renderer::instance().destroy_game_window();
}

EDITOR_INTERFACE bool IsGameWindowActive()
{
	return graphics::Renderer::instance().is_game_window_active();
}

// ---- 2D UI overlay (generic; driven by the game's Vortex.UI scripting API) ----
EDITOR_INTERFACE void UIBegin(float w, float h)
{
	graphics::Renderer::instance().ui_begin(w, h);
}
EDITOR_INTERFACE void UIRect(float x, float y, float w, float h, float r, float g, float b, float a, float radius)
{
	graphics::Renderer::instance().ui_rect(x, y, w, h, r, g, b, a, radius);
}
EDITOR_INTERFACE void UIText(float x, float y, float w, float h, const wchar_t* text,
	float size, float r, float g, float b, float a, int align, int weight)
{
	graphics::Renderer::instance().ui_text(x, y, w, h, text, size, r, g, b, a, align, weight);
}
EDITOR_INTERFACE void UILine(float x1, float y1, float x2, float y2, float r, float g, float b, float a, float thickness)
{
	graphics::Renderer::instance().ui_line(x1, y1, x2, y2, r, g, b, a, thickness);
}
// 5th UI primitive: a textured quad (PNG/JPG via WIC), tinted by (r,g,b,a). path = absolute or project file.
EDITOR_INTERFACE void UIImage(float x, float y, float w, float h, const wchar_t* path, float r, float g, float b, float a)
{
	graphics::Renderer::instance().ui_image(x, y, w, h, path, r, g, b, a);
}
EDITOR_INTERFACE void UIPushClip(float x, float y, float w, float h)
{
	graphics::Renderer::instance().ui_push_clip(x, y, w, h);
}
EDITOR_INTERFACE void UIPopClip()
{
	graphics::Renderer::instance().ui_pop_clip();
}

EDITOR_INTERFACE void ShutdownRenderViewport()
{
	runtime::systems::render::shutdown();
}

// Where the backend loads its shader sources from (Shaders/msl on macOS). Managed hosts whose executable does
// not sit next to a Shaders folder (dotnet apphost, .app bundles) set this before the renderer initializes.
// The DX12 backend resolves its .hlsl relative to the exe on its own; the call is a no-op there.
EDITOR_INTERFACE void SetShaderDirectory(const char* utf8_path)
{
#if VORTEX_HAS_SDLGPU
	graphics::Renderer::instance().set_shader_directory(utf8_path ? utf8_path : "");
#else
	(void)utf8_path;
#endif
}

// UTF-8 variants of the overlay text/image calls for the .NET host on macOS/Linux, where wchar_t is
// 32-bit and the wide P/Invoke marshalling (UTF-16) would not match. Same semantics as UIText / UIImage.
EDITOR_INTERFACE void UITextUtf8(float x, float y, float w, float h, const char* text_utf8,
	float size, float r, float g, float b, float a, int align, int weight)
{
	graphics::Renderer::instance().ui_text_utf8(x, y, w, h, text_utf8, size, r, g, b, a, align, weight);
}
EDITOR_INTERFACE void UIImageUtf8(float x, float y, float w, float h, const char* path_utf8, float r, float g, float b, float a)
{
	graphics::Renderer::instance().ui_image_utf8(x, y, w, h, path_utf8, r, g, b, a);
}

// Primitive mesh creation
EDITOR_INTERFACE id::id_type CreatePrimitiveCube(float size)
{
	return graphics::ResourceRegistry::instance().create_primitive_cube(size);
}

EDITOR_INTERFACE id::id_type CreatePrimitiveSphere(float radius)
{
	return graphics::ResourceRegistry::instance().create_primitive_sphere(radius);
}

EDITOR_INTERFACE id::id_type CreateInvertedSphere(float radius)
{
	return graphics::ResourceRegistry::instance().create_inverted_sphere(radius);
}

EDITOR_INTERFACE id::id_type CreatePrimitivePlane(float width, float height)
{
	return graphics::ResourceRegistry::instance().create_primitive_plane(width, height);
}

EDITOR_INTERFACE id::id_type CreatePrimitiveCylinder(float radius, float height)
{
	return graphics::ResourceRegistry::instance().create_primitive_cylinder(radius, height);
}

EDITOR_INTERFACE id::id_type CreatePrimitiveCone(float radius, float height)
{
	return graphics::ResourceRegistry::instance().create_primitive_cone(radius, height);
}

EDITOR_INTERFACE void DestroyMesh(id::id_type mesh_id)
{
	graphics::ResourceRegistry::instance().destroy_mesh(mesh_id);
}

EDITOR_INTERFACE void DestroyTexture(id::id_type texture_id)
{
	graphics::ResourceRegistry::instance().destroy_texture(texture_id);
}

// Live resource counts (meshes incl. LOD meshes, materials, textures) so a leak shows up in engine_stats (#357 #358).
EDITOR_INTERFACE void GetResourceCounts(int* meshes, int* materials, int* textures)
{
	auto& reg = graphics::ResourceRegistry::instance();
	if (meshes) *meshes = (int)reg.mesh_count();
	if (materials) *materials = (int)reg.material_count();
	if (textures) *textures = (int)reg.texture_count();
}

// Mesh bounds query
EDITOR_INTERFACE bool QueryMeshBounds(id::id_type mesh_id, float* sizeX, float* sizeY, float* sizeZ)
{
	auto* mesh = graphics::ResourceRegistry::instance().get_mesh(mesh_id);
	if (!mesh || !sizeX || !sizeY || !sizeZ)
	{
		if (sizeX) *sizeX = 1.0f;
		if (sizeY) *sizeY = 1.0f;
		if (sizeZ) *sizeZ = 1.0f;
		return false;
	}
	
	mesh->get_bounds(*sizeX, *sizeY, *sizeZ);
	return true;
}

EDITOR_INTERFACE bool QueryMeshBoundsCenter(id::id_type mesh_id, float* centerX, float* centerY, float* centerZ)
{
	auto* mesh = graphics::ResourceRegistry::instance().get_mesh(mesh_id);
	if (!mesh || !centerX || !centerY || !centerZ)
	{
		if (centerX) *centerX = 0.0f;
		if (centerY) *centerY = 0.0f;
		if (centerZ) *centerZ = 0.0f;
		return false;
	}
	
	mesh->get_bounds_center(*centerX, *centerY, *centerZ);
	return true;
}

// Material creation
EDITOR_INTERFACE id::id_type CreateMaterial()
{
	return graphics::ResourceRegistry::instance().create_material();
}

EDITOR_INTERFACE void SetMaterialColor(id::id_type material_id, float r, float g, float b, float a)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_base_color({ r, g, b, a });
}

EDITOR_INTERFACE void SetMaterialTexture(id::id_type material_id, id::id_type texture_id)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	auto* tex = graphics::ResourceRegistry::instance().get_texture(texture_id);
	if (mat && tex)
		mat->set_albedo_texture(tex);
	else
		VORTEX_VLOG("SetMaterialTexture: mat or tex is null\n");
}

EDITOR_INTERFACE bool MaterialHasTexture(id::id_type material_id)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	return mat && mat->albedo_texture() != nullptr;
}

EDITOR_INTERFACE void DestroyMaterial(id::id_type material_id)
{
	// Also drop any per-material custom-shader binding so throwaway preview materials (rebuilt every orbit frame in
	// the Material Editor) don't accumulate stale entries. The shared PSO cache keeps the compiled shader alive.
#if VORTEX_HAS_DX12
	graphics::Renderer::instance().set_material_shader((uint32_t)material_id, L"");
#else
	graphics::Renderer::instance().set_material_shader((uint32_t)material_id, std::string());
#endif
	graphics::ResourceRegistry::instance().destroy_material(material_id);
}

// PBR Material texture setters
EDITOR_INTERFACE void SetMaterialNormalTexture(id::id_type material_id, id::id_type texture_id)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	auto* tex = graphics::ResourceRegistry::instance().get_texture(texture_id);
	if (mat && tex) mat->set_normal_texture(tex);
}

EDITOR_INTERFACE void SetMaterialMetallicTexture(id::id_type material_id, id::id_type texture_id)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	auto* tex = graphics::ResourceRegistry::instance().get_texture(texture_id);
	if (mat && tex) mat->set_metallic_texture(tex);
}

EDITOR_INTERFACE void SetMaterialRoughnessTexture(id::id_type material_id, id::id_type texture_id)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	auto* tex = graphics::ResourceRegistry::instance().get_texture(texture_id);
	if (mat && tex) mat->set_roughness_texture(tex);
}

EDITOR_INTERFACE void SetMaterialAOTexture(id::id_type material_id, id::id_type texture_id)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	auto* tex = graphics::ResourceRegistry::instance().get_texture(texture_id);
	if (mat && tex) mat->set_ao_texture(tex);
}

// Channel each packed map is read from (0 R, 1 G, 2 B, 3 A): glTF metallicRoughness and ORM/ARM maps store
// roughness in G, metallic in B and occlusion in R. Applies to maps bound before or after this call.
EDITOR_INTERFACE void SetMaterialTextureChannels(id::id_type material_id, int metallic, int roughness, int ao)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_texture_channels((u32)(metallic < 0 ? 0 : metallic), (u32)(roughness < 0 ? 0 : roughness), (u32)(ao < 0 ? 0 : ao));
}

EDITOR_INTERFACE void SetMaterialMetallic(id::id_type material_id, float value)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_metallic(value);
}

EDITOR_INTERFACE void SetMaterialRoughness(id::id_type material_id, float value)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_roughness(value);
}

EDITOR_INTERFACE void SetMaterialNormalStrength(id::id_type material_id, float value)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_normal_strength(value);
}

EDITOR_INTERFACE void SetMaterialAO(id::id_type material_id, float value)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_ao(value);
}

EDITOR_INTERFACE void SetMaterialUseDirectXNormals(id::id_type material_id, bool use_directx)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_use_directx_normals(use_directx);
}

EDITOR_INTERFACE void SetMaterialUnlit(id::id_type material_id, bool is_unlit)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_unlit(is_unlit);
}

EDITOR_INTERFACE void SetMaterialEmissiveStrength(id::id_type material_id, float strength)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_emissive_strength(strength);
}

EDITOR_INTERFACE void SetMaterialUVTiling(id::id_type material_id, float u, float v)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_uv_tiling(u, v);
}

// Blend mode (#33): 0 = opaque, 1 = alpha blend, 2 = additive. Non-opaque materials draw in the
// sorted back-to-front transparent pass with depth write off (the .vmat BlendMode finally renders).
EDITOR_INTERFACE void SetMaterialBlendMode(id::id_type material_id, int mode)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_blend_mode(mode < 0 ? 0u : (u32)mode);
}

// AlphaTest (#329): fragments whose alpha falls below the cutoff are discarded in the main pass (0 = off).
EDITOR_INTERFACE void SetMaterialAlphaCutoff(id::id_type material_id, float cutoff)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_alpha_cutoff(cutoff);
}

// TwoSided: the material draws without back-face culling (cut-outs, foliage, cloth).
EDITOR_INTERFACE void SetMaterialDoubleSided(id::id_type material_id, bool two_sided)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_double_sided(two_sided);
}

EDITOR_INTERFACE void SetMaterialHeightTexture(id::id_type material_id, id::id_type texture_id)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	auto* tex = graphics::ResourceRegistry::instance().get_texture(texture_id);
	if (mat && tex) mat->set_height_texture(tex);
}

EDITOR_INTERFACE void SetMaterialHeightScale(id::id_type material_id, float scale)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_height_scale(scale);
}

// Render item submission
EDITOR_INTERFACE void SubmitRenderItem(id::id_type mesh_id, id::id_type material_id, float* world_matrix)
{
	graphics::backend::RenderItem item{};
	item.mesh_id = mesh_id;
	item.material_id = material_id;
	if (world_matrix)
	{
		memcpy(&item.world_matrix, world_matrix, sizeof(DirectX::XMFLOAT4X4));
	}
	else
	{
		DirectX::XMStoreFloat4x4(&item.world_matrix, DirectX::XMMatrixIdentity());
	}
	graphics::Renderer::instance().submit_render_item(item);
}

// Submit an editor GIZMO mesh — rendered ALWAYS ON TOP (depth-disabled) in a dedicated pass after the scene, so
// transform handles + the selection outline are never hidden behind geometry.
EDITOR_INTERFACE void SubmitGizmoItem(id::id_type mesh_id, id::id_type material_id, float* world_matrix)
{
	graphics::backend::RenderItem item{};
	item.mesh_id = mesh_id;
	item.material_id = material_id;
	if (world_matrix) memcpy(&item.world_matrix, world_matrix, sizeof(DirectX::XMFLOAT4X4));
	else DirectX::XMStoreFloat4x4(&item.world_matrix, DirectX::XMMatrixIdentity());
	graphics::Renderer::instance().submit_gizmo_item(item);
}

// Same always-on-top gizmo pass, but rasterized as WIREFRAME: one call draws a whole shape (audio range
// sphere, reverb-zone box) as a fine triangle net instead of hundreds of scaled-cube edge segments.
EDITOR_INTERFACE void SubmitGizmoWireItem(id::id_type mesh_id, id::id_type material_id, float* world_matrix)
{
	graphics::backend::RenderItem item{};
	item.mesh_id = mesh_id;
	item.material_id = material_id;
	if (world_matrix) memcpy(&item.world_matrix, world_matrix, sizeof(DirectX::XMFLOAT4X4));
	else DirectX::XMStoreFloat4x4(&item.world_matrix, DirectX::XMMatrixIdentity());
	graphics::Renderer::instance().submit_gizmo_wire_item(item);
}

// Submit `count` instances of the SAME mesh+material in ONE call (world_matrices = count*16 floats, row-major
// 4x4 each). The renderer groups them into a single DrawIndexedInstanced — the path for spawning large crowds
// without one P/Invoke per instance.
EDITOR_INTERFACE void SubmitMeshInstances(id::id_type mesh_id, id::id_type material_id, const float* world_matrices, int count)
{
	if (!world_matrices || count <= 0) return;
	graphics::Renderer::instance().submit_mesh_instances(mesh_id, material_id, world_matrices, static_cast<u32>(count));
}

// #175: layer-aware variant (0 = world, 1 = first-person viewmodel). Additive export — the old
// signature stays for P/Invoke compatibility.
EDITOR_INTERFACE void SubmitMeshInstancesEx(id::id_type mesh_id, id::id_type material_id, const float* world_matrices, int count, int layer)
{
	if (!world_matrices || count <= 0) return;
	graphics::Renderer::instance().submit_mesh_instances(mesh_id, material_id, world_matrices,
		static_cast<u32>(count), layer > 0 ? 1u : 0u);
}

// #331: per-instance tint — colors = count x (r, g, b, a), multiplied with the material's base colour per instance, so
// instances of one shared material (a .vmat, an imported material) can differ in colour without a material each.
EDITOR_INTERFACE void SubmitMeshInstancesTinted(id::id_type mesh_id, id::id_type material_id, const float* world_matrices,
	const float* colors, int count, int layer)
{
	if (!world_matrices || count <= 0) return;
	graphics::Renderer::instance().submit_mesh_instances(mesh_id, material_id, world_matrices,
		static_cast<u32>(count), layer > 0 ? 1u : 0u, colors);
}

EDITOR_INTERFACE void SubmitRenderItemTinted(id::id_type mesh_id, id::id_type material_id, const float* world_matrix,
	float r, float g, float b, float a, int layer)
{
	if (!world_matrix) return;
	const float color[4] = { r, g, b, a };
	graphics::Renderer::instance().submit_mesh_instances(mesh_id, material_id, world_matrix, 1u, layer > 0 ? 1u : 0u, color);
}

// #364 A: static / dynamic split. Everything submitted between BeginStaticScene and EndStaticScene is RETAINED by
// the renderer across frames; per-frame submits (the moving entities) go to the normal queue and are merged in at the
// swap. An empty pass (Begin + End) clears the retained set.
EDITOR_INTERFACE void BeginStaticScene()
{
	graphics::Renderer::instance().begin_static_scene();
}

EDITOR_INTERFACE void EndStaticScene()
{
	graphics::Renderer::instance().end_static_scene();
}

// #175: the first-person layer's own field of view (degrees; world FOV never distorts the viewmodel).
EDITOR_INTERFACE void SetViewmodelFOV(float fov_degrees)
{
	graphics::Renderer::instance().set_viewmodel_fov(fov_degrees);
}

// Camera control
EDITOR_INTERFACE void SetCamera(float pos_x, float pos_y, float pos_z,
								float target_x, float target_y, float target_z,
								float up_x, float up_y, float up_z)
{
	graphics::Renderer::instance().set_camera(
		{ pos_x, pos_y, pos_z },
		{ target_x, target_y, target_z },
		{ up_x, up_y, up_z });
}

// Vertical FOV (degrees) of the live view camera — driven by the game's FOV setting.
EDITOR_INTERFACE void SetViewFieldOfView(float fov_degrees)
{
	graphics::Renderer::instance().set_field_of_view(fov_degrees);
}

EDITOR_INTERFACE float GetViewFieldOfView()
{
	return graphics::Renderer::instance().field_of_view();
}

// Near / far planes of the live view camera (#327) — the main Camera component's values in play mode.
EDITOR_INTERFACE void SetViewClipPlanes(float near_clip, float far_clip)
{
	graphics::Renderer::instance().set_clip_planes(near_clip, far_clip);
}

// Grid and Gizmo control
EDITOR_INTERFACE void SetGridVisible(bool visible)
{
	graphics::Renderer::instance().set_grid_visible(visible);
}

EDITOR_INTERFACE void SetGridSettings(float spacing, float major_line_interval, float extent)
{
	graphics::Renderer::instance().set_grid_settings(spacing, major_line_interval, extent);
}

EDITOR_INTERFACE void SetGizmosVisible(bool visible)
{
	graphics::Renderer::instance().set_gizmos_visible(visible);
}

EDITOR_INTERFACE bool IsGridVisible()
{
	return graphics::Renderer::instance().is_grid_visible();
}

EDITOR_INTERFACE bool AreGizmosVisible()
{
	return graphics::Renderer::instance().are_gizmos_visible();
}

// Rendering mode
EDITOR_INTERFACE void SetWireframeMode(bool enabled)
{
	graphics::Renderer::instance().set_wireframe_mode(enabled);
}

EDITOR_INTERFACE bool IsWireframeMode()
{
	return graphics::Renderer::instance().is_wireframe_mode();
}

// VSync control
EDITOR_INTERFACE void SetVSync(bool enabled)
{
	graphics::Renderer::instance().set_vsync(enabled);
}

EDITOR_INTERFACE bool IsVSyncEnabled()
{
	return graphics::Renderer::instance().is_vsync_enabled();
}

// Gizmo mesh creation
EDITOR_INTERFACE id::id_type CreateGizmoArrow(float length, float radius)
{
	// Create arrow mesh (cylinder + cone)
	auto& reg = graphics::ResourceRegistry::instance();
	return reg.create_primitive_cone(radius * 2, length * 0.3f);
}

EDITOR_INTERFACE id::id_type CreateGizmoCylinder(float length, float radius)
{
	auto& reg = graphics::ResourceRegistry::instance();
	return reg.create_primitive_cylinder(radius, length);
}

// Performance statistics
EDITOR_INTERFACE int GetCurrentFPS()
{
	// Prefer RenderLoop FPS if running, otherwise use Renderer FPS
	auto& loop = runtime::RenderLoop::instance();
	if (loop.is_running())
		return loop.get_current_fps();
	return graphics::Renderer::instance().get_current_fps();
}

EDITOR_INTERFACE int GetDrawCallCount()
{
	return graphics::Renderer::instance().get_draw_call_count();
}

EDITOR_INTERFACE int GetVertexCount()
{
	return graphics::Renderer::instance().get_vertex_count();
}

// Shadow-atlas and SSAO-prepass draws of the last frame; both are included in GetDrawCallCount (#363).
EDITOR_INTERFACE int GetShadowDrawCallCount()
{
	return graphics::Renderer::instance().get_shadow_draw_count();
}

EDITOR_INTERFACE int GetPostDrawCallCount()
{
	return graphics::Renderer::instance().get_post_draw_count();
}

EDITOR_INTERFACE int GetInstancesTested()
{
	return graphics::Renderer::instance().get_instances_tested();
}

EDITOR_INTERFACE int GetInstancesDrawn()
{
	return graphics::Renderer::instance().get_instances_drawn();
}

// Generic render-distance cull (world units; 0 = disabled). Driven by the game's graphics settings.
EDITOR_INTERFACE void SetRenderDistance(float distance)
{
	graphics::Renderer::instance().set_render_distance(distance);
}

// Density LOD: thin distant instances (1/2 beyond mid, 1/4 beyond far world units). enabled=false disables.
EDITOR_INTERFACE void SetLOD(bool enabled, float mid, float farD)
{
	graphics::Renderer::instance().set_lod(enabled, mid, farD);
}

// Geometric LOD: distant instances draw a decimated low-poly mesh (whole crowd visible, no holes). mid/far = the
// distances at which LOD1/LOD2 kick in.
EDITOR_INTERFACE void SetGeometricLOD(bool enabled, float mid, float farD)
{
	graphics::Renderer::instance().set_geometric_lod(enabled, mid, farD);
}

// ---- Post-processing (#28 framework / #29 pack 1) ----
// Params are persistent renderer state (like fog): set once, applies to the editor viewport, the
// play-mode game window AND shipped games until changed. All effects off = the untouched render path.

EDITOR_INTERFACE void SetPostFxVignette(bool enabled, float intensity, float smoothness, float roundness,
	float r, float g, float b)
{
	auto& p = graphics::Renderer::instance().postfx().params();
	p.vignette = enabled;
	p.vig_intensity = intensity; p.vig_smoothness = smoothness; p.vig_roundness = roundness;
	p.vig_r = r; p.vig_g = g; p.vig_b = b;
}

EDITOR_INTERFACE void SetPostFxGrain(bool enabled, float intensity, float size)
{
	auto& p = graphics::Renderer::instance().postfx().params();
	p.grain = enabled;
	p.grain_intensity = intensity; p.grain_size = size;
}

EDITOR_INTERFACE void SetPostFxChromaticAberration(bool enabled, float strength, float falloff)
{
	auto& p = graphics::Renderer::instance().postfx().params();
	p.ca = enabled;
	p.ca_strength = strength; p.ca_falloff = falloff;
}

// Color grading (#31): exposure (EV stops), contrast, saturation, temperature (-1 cool..+1 warm),
// tint (-1 green..+1 magenta). Single pass, folded into the post uber-pass.
// Anti-aliasing: FXAA 3.11 (console variant) in the post pass on the LDR picture — nine taps, no history, the cheap
// edge smoothing until temporal AA (#141). Persistent renderer state like the other post effects.
EDITOR_INTERFACE void SetPostFxAntiAlias(bool enabled)
{
	graphics::Renderer::instance().postfx().params().fxaa = enabled;
}

EDITOR_INTERFACE void SetPostFxColorGrade(bool enabled, float exposure, float contrast, float saturation,
	float temperature, float tint)
{
	auto& p = graphics::Renderer::instance().postfx().params();
	p.grade = enabled;
	p.exposure = exposure; p.contrast = contrast; p.saturation = saturation;
	p.temperature = temperature; p.tint = tint;
}

// Bloom (#30): soft-knee bright-pass -> progressive mip down/up chain -> additive composite in the
// uber pass. threshold = brightness where glow starts (SDR 0..~1.5), knee = soft width below it,
// intensity = composite strength (0 = bit-exact passthrough, the chain never even redirects),
// scatter = per-mip accumulate weight (how far the glow spreads).
EDITOR_INTERFACE void SetPostFxBloom(bool enabled, float threshold, float knee, float intensity, float scatter)
{
	auto& p = graphics::Renderer::instance().postfx().params();
	p.bloom = enabled;
	p.bloom_threshold = threshold; p.bloom_knee = knee;
	p.bloom_intensity = intensity; p.bloom_scatter = scatter;
}

// Chain-verification pass (#28 AC): a trivial invert as a SECOND pass, proving the ping-pong. Debug only.
EDITOR_INTERFACE void SetPostFxDebugInvert(bool enabled)
{
	graphics::Renderer::instance().postfx().params().debug_invert = enabled;
}

// SSAO (#32): screen-space ambient occlusion — darkens ONLY the ambient/indirect term (crevices,
// contact areas). Like fog it is part of the scene render itself, so it shows in the editor
// viewport too. radius in world units (~0.3-1.5), intensity 0..~2.
EDITOR_INTERFACE void SetSSAO(bool enabled, float radius, float intensity)
{
	auto& r = graphics::Renderer::instance();
	r.set_ssao(enabled, radius, intensity);
}

// Main-view gate: post-FX in the PRIMARY swapchain. The GameHost player enables it at boot (there the
// main view IS the game); the editor leaves it off so the freecam build viewport stays clean (its
// Environment panel may toggle it for a preview). The play-mode game window always applies effects.
EDITOR_INTERFACE void SetPostFxMainView(bool enabled)
{
	graphics::Renderer::instance().postfx().set_main_view_enabled(enabled);
}

// Multithreaded per-instance cull+pack (auto-gates on instance count; the draw recording stays single-threaded).
EDITOR_INTERFACE void SetMultithreading(bool enabled)
{
	graphics::Renderer::instance().set_multithreading(enabled);
}
EDITOR_INTERFACE void SetMultithreadingForce(bool force)
{
	graphics::Renderer::instance().set_multithreading_force(force);
}
EDITOR_INTERFACE bool IsMultithreadingActive()
{
	return graphics::Renderer::instance().mt_active();
}

// ============== RENDER LOOP API ==============

// ---- Native GameHost: the standalone game runs in its OWN native Win32 window + DX12 swapchain + loop,
// all on one thread (no WPF HwndHost). Fixes the cross-thread Present-freeze and uncaps FPS. ----
