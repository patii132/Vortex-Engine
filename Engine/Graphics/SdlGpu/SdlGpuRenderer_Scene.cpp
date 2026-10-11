#include "SdlGpuRenderer.h"
#include "../../Common/Platform.h"
#include <algorithm>
#include <cmath>
#include <cstring>

namespace vortex::graphics::sdlgpu
{
	namespace
	{
		struct FrustumPlanes { float p[6][4]; };

		// Six normalized frustum planes from a row-major view-projection (Gribb-Hartmann); inside when a*x+b*y+c*z+d >= 0.
		FrustumPlanes extract_frustum(const DirectX::XMFLOAT4X4& m)
		{
			FrustumPlanes f = { {
				{ m._14 + m._11, m._24 + m._21, m._34 + m._31, m._44 + m._41 },
				{ m._14 - m._11, m._24 - m._21, m._34 - m._31, m._44 - m._41 },
				{ m._14 + m._12, m._24 + m._22, m._34 + m._32, m._44 + m._42 },
				{ m._14 - m._12, m._24 - m._22, m._34 - m._32, m._44 - m._42 },
				{ m._13,         m._23,         m._33,         m._43         },
				{ m._14 - m._13, m._24 - m._23, m._34 - m._33, m._44 - m._43 },
			} };
			for (int i = 0; i < 6; ++i)
			{
				float a = f.p[i][0], b = f.p[i][1], c = f.p[i][2];
				float len = sqrtf(a * a + b * b + c * c);
				if (len > 1e-6f) { f.p[i][0] /= len; f.p[i][1] /= len; f.p[i][2] /= len; f.p[i][3] /= len; }
			}
			return f;
		}

		bool sphere_in_frustum(const FrustumPlanes& f, float cx, float cy, float cz, float r)
		{
			for (int i = 0; i < 6; ++i)
				if (f.p[i][0] * cx + f.p[i][1] * cy + f.p[i][2] * cz + f.p[i][3] < -r) return false;
			return true;
		}

		// The XMMatrixLookAtLH basis (forward = at - eye, right = up x forward, up = forward x right).
		void camera_basis(const DirectX::XMFLOAT3& eye, const DirectX::XMFLOAT3& at, const DirectX::XMFLOAT3& up_hint,
			DirectX::XMFLOAT3& right, DirectX::XMFLOAT3& up, DirectX::XMFLOAT3& forward)
		{
			using namespace DirectX;
			XMVECTOR f = XMVector3Normalize(XMVectorSubtract(XMLoadFloat3(&at), XMLoadFloat3(&eye)));
			XMVECTOR r = XMVector3Normalize(XMVector3Cross(XMLoadFloat3(&up_hint), f));
			XMVECTOR u = XMVector3Cross(f, r);
			XMStoreFloat3(&forward, f); XMStoreFloat3(&right, r); XMStoreFloat3(&up, u);
		}
	}

	// ---------------------------------------------------------------------------------------------
	// Queue
	// ---------------------------------------------------------------------------------------------
	namespace
	{
		// A world matrix with a negative determinant (an odd number of negative scale axes) flips the triangle
		// winding; the run picks a counter-clockwise front-face pipeline for it (#334).
		// The instance stream carries the world matrix (64 bytes). An affine matrix never uses its fourth column (0, 0, 0, 1),
		// so the per-instance tint (#331) rides there as (r-1, g-1, b-1, a): an untinted instance stays an exact affine
		// matrix (older custom vertex shaders keep working), and the standard vertex shaders take the two apart.
		inline void pack_instance(float* dst, const RenderItem& item)
		{
			memcpy(dst, &item.world_matrix, 64);
			dst[3] = item.color.x - 1.0f; dst[7] = item.color.y - 1.0f; dst[11] = item.color.z - 1.0f; dst[15] = item.color.w;
		}

		inline RenderItem with_winding(RenderItem item)
		{
			const DirectX::XMFLOAT4X4& m = item.world_matrix;
			const float det = m._11 * (m._22 * m._33 - m._23 * m._32) - m._12 * (m._21 * m._33 - m._23 * m._31) + m._13 * (m._21 * m._32 - m._22 * m._31);
			item.mirrored = det < 0.0f ? 1u : 0u;
			return item;
		}
	}

	void SdlGpuRenderer::submit_render_item(const RenderItem& item)
	{
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		(m_submit_target == 1 ? m_static_submit : m_submit_queue).push_back(with_winding(item));
	}

	void SdlGpuRenderer::submit_gizmo_item(const RenderItem& item)
	{
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		if (m_gizmo_submit.size() < MAX_GIZMO_ITEMS) m_gizmo_submit.push_back(with_winding(item));
	}

	void SdlGpuRenderer::submit_gizmo_wire_item(const RenderItem& item)
	{
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		if (m_gizmo_submit.size() + m_gizmo_wire_submit.size() < MAX_GIZMO_ITEMS) m_gizmo_wire_submit.push_back(with_winding(item));
	}

	void SdlGpuRenderer::submit_mesh_instances(id::id_type mesh, id::id_type material, const float* world_matrices, u32 count, u32 layer, const float* colors)
	{
		if (!world_matrices || count == 0) return;
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		auto& q = m_submit_target == 1 ? m_static_submit : m_submit_queue;
		q.reserve(q.size() + count);
		for (u32 i = 0; i < count; ++i)
		{
			RenderItem item;
			item.mesh_id = mesh; item.material_id = material; item.layer = layer;
			memcpy(&item.world_matrix, world_matrices + (size_t)i * 16, sizeof(DirectX::XMFLOAT4X4));
			if (colors) item.color = { colors[(size_t)i * 4], colors[(size_t)i * 4 + 1], colors[(size_t)i * 4 + 2], colors[(size_t)i * 4 + 3] };
			q.push_back(with_winding(item));
		}
	}

	void SdlGpuRenderer::submit_skinned_item(id::id_type mesh, id::id_type material, const float* world_matrix,
		const float* bone_matrices, u32 bone_count, u32 layer, const float* color)
	{
		if (!world_matrix || !bone_matrices || bone_count == 0) return;
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		RenderItem item;
		item.mesh_id = mesh; item.material_id = material; item.layer = layer;
		if (color) item.color = { color[0], color[1], color[2], color[3] };
		memcpy(&item.world_matrix, world_matrix, sizeof(DirectX::XMFLOAT4X4));
		const u32 offset = (u32)(m_bone_submit.size() / 16);
		if (offset + bone_count > MAX_BONE_MATRICES) { m_submit_queue.push_back(with_winding(item)); return; }   // palette full -> bind pose
		item.bone_offset = offset;
		item.bone_count = bone_count;
		m_bone_submit.insert(m_bone_submit.end(), bone_matrices, bone_matrices + (size_t)bone_count * 16);
		m_submit_queue.push_back(with_winding(item));
	}

	void SdlGpuRenderer::clear_render_queue()
	{
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		m_submit_queue.clear();
		m_bone_submit.clear();
		m_static_submit.clear(); m_static_render.clear(); m_static_pending = false; m_submit_target = 0;
	}

	// #364 A: everything submitted between these two calls is RETAINED across frames (the static scene); an empty pass
	// clears the retained set. Per-frame submits outside the pass are the dynamic entities, merged in at the swap.
	void SdlGpuRenderer::begin_static_scene()
	{
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		m_static_submit.clear();
		m_submit_target = 1;
	}

	void SdlGpuRenderer::end_static_scene()
	{
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		m_submit_target = 0;
		m_static_pending = true;
	}

	void SdlGpuRenderer::swap_render_queue()
	{
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		// Gizmos are re-submitted every frame by the editor: swap unconditionally (empty = no gizmo).
		m_gizmo_render.swap(m_gizmo_submit); m_gizmo_submit.clear();
		m_gizmo_wire_render.swap(m_gizmo_wire_submit); m_gizmo_wire_submit.clear();
		bool staticChanged = false;
		if (m_static_pending) { m_static_render.swap(m_static_submit); m_static_submit.clear(); m_static_pending = false; staticChanged = true; }
		if (m_submit_queue.empty() && !staticChanged) return;   // nothing new -> keep last frame's scene (camera-only frames are free)
		// the frame = the retained static set + this frame's dynamic submits (#364 A)
		m_render_queue.clear();
		m_render_queue.reserve(m_static_render.size() + m_submit_queue.size());
		m_render_queue.insert(m_render_queue.end(), m_static_render.begin(), m_static_render.end());
		m_render_queue.insert(m_render_queue.end(), m_submit_queue.begin(), m_submit_queue.end());
		m_submit_queue.clear();
		m_queue_dirty = true;
		m_bone_render.swap(m_bone_submit);
		m_bone_submit.clear();
		m_bone_upload_pending = true;
	}

	// ---------------------------------------------------------------------------------------------
	// Camera / lights
	// ---------------------------------------------------------------------------------------------
	void SdlGpuRenderer::set_camera(const DirectX::XMFLOAT3& pos, const DirectX::XMFLOAT3& target, const DirectX::XMFLOAT3& up)
	{
		m_camera_position = pos; m_camera_target = target; m_camera_up = up;
	}

	void SdlGpuRenderer::set_projection(float fov_degrees, float aspect, float near_clip, float far_clip)
	{
		m_fov_degrees = fov_degrees; m_aspect_ratio = aspect; m_near_clip = near_clip; m_far_clip = far_clip;
	}

	void SdlGpuRenderer::set_directional_light(const DirectX::XMFLOAT3& dir, const DirectX::XMFLOAT3& col) { m_light_direction = dir; m_light_color = col; }
	void SdlGpuRenderer::set_ambient_strength(float s) { m_ambient_strength = s; }
	void SdlGpuRenderer::clear_lights() { m_point_lights.clear(); m_spot_lights.clear(); }

	void SdlGpuRenderer::set_directional_light_full(const DirectX::XMFLOAT3& direction, const DirectX::XMFLOAT3& color, float intensity,
		bool cast_shadows, float shadow_strength, float shadow_bias, float shadow_distance)
	{
		m_light_direction = direction; m_light_color = color; m_directional_intensity = intensity;
		m_dir_cast_shadows = cast_shadows;
		m_dir_shadow_strength = shadow_strength < 0.0f ? 0.0f : (shadow_strength > 1.0f ? 1.0f : shadow_strength);
		m_dir_shadow_bias = shadow_bias > 0.0f ? shadow_bias : 0.0008f;
		m_dir_shadow_distance = shadow_distance > 5.0f ? shadow_distance : 5.0f;
	}

	namespace
	{
		// Lights beyond the shader's slots are chosen by relevance to the camera, not by submission order (#335):
		// intensity weighted by how deep the camera sits inside the light's range sphere and, outside it, by how
		// close that sphere is. The brightest nearby lights win; far dim ones drop out.
		template<class L>
		void keep_most_relevant(std::vector<L>& lights, size_t cap, const DirectX::XMFLOAT3& eye)
		{
			if (lights.size() <= cap) return;
			std::vector<std::pair<float, size_t>> order;
			order.reserve(lights.size());
			for (size_t i = 0; i < lights.size(); ++i)
			{
				const L& l = lights[i];
				const float dx = l.position.x - eye.x, dy = l.position.y - eye.y, dz = l.position.z - eye.z;
				const float d = sqrtf(dx * dx + dy * dy + dz * dz);
				float inside = l.range > 0.0f ? 1.0f - d / l.range : 1.0f;
				if (inside < 0.0f) inside = 0.0f;
				float nearness = l.range > 0.0f ? l.range / (d + 1e-3f) : 1.0f;
				if (nearness > 1.0f) nearness = 1.0f;
				order.push_back({ l.intensity * (inside + nearness), i });
			}
			std::stable_sort(order.begin(), order.end(), [](const std::pair<float, size_t>& a, const std::pair<float, size_t>& b) { return a.first > b.first; });
			std::vector<L> kept;
			kept.reserve(cap);
			for (size_t i = 0; i < cap; ++i) kept.push_back(lights[order[i].second]);
			lights.swap(kept);
		}
	}

	// every submitted light is kept here; prioritize_lights() picks the ones that fit the shader slots (#335)
	void SdlGpuRenderer::add_point_light(const PointLightData& light) { if (m_point_lights.size() < MAX_SUBMITTED_LIGHTS) m_point_lights.push_back(light); }
	void SdlGpuRenderer::add_spot_light(const SpotLightData& light) { if (m_spot_lights.size() < MAX_SUBMITTED_LIGHTS) m_spot_lights.push_back(light); }

	void SdlGpuRenderer::prioritize_lights()
	{
		const size_t points = m_point_lights.size(), spots = m_spot_lights.size();
		if (points <= MAX_POINT_LIGHTS && spots <= MAX_SPOT_LIGHTS) return;
		keep_most_relevant(m_point_lights, MAX_POINT_LIGHTS, m_camera_position);
		keep_most_relevant(m_spot_lights, MAX_SPOT_LIGHTS, m_camera_position);
		if (m_light_cap_log++ % 600 == 0)
			platform::debug_output(("[sdlgpu] lights: " + std::to_string(points) + " point / " + std::to_string(spots) + " spot submitted, "
				+ std::to_string(MAX_POINT_LIGHTS) + " / " + std::to_string(MAX_SPOT_LIGHTS) + " fit — the brightest near the camera are used (#335)\n").c_str());
	}

	void SdlGpuRenderer::set_fog(const DirectX::XMFLOAT3& color, float density, float height_y, float height_falloff)
	{
		m_frame_constants.fog_color = color;
		m_frame_constants.fog_density = density > 0.0f ? density : 0.0f;
		m_frame_constants.fog_height_y = height_y;
		m_frame_constants.fog_height_falloff = height_falloff > 0.0f ? height_falloff : 0.0f;
		m_frame_constants.fog_mode = 0;
	}

	void SdlGpuRenderer::fill_light_buffer()
	{
		using namespace DirectX;
		prioritize_lights();
		LightBufferData& L = m_light_data;
		memset(&L, 0, sizeof(L));
		for (size_t i = 0; i < m_point_lights.size() && i < MAX_POINT_LIGHTS; ++i)
		{
			L.point_lights[i].position = m_point_lights[i].position;
			L.point_lights[i].range = m_point_lights[i].range;
			L.point_lights[i].color = m_point_lights[i].color;
			L.point_lights[i].intensity = m_point_lights[i].intensity;
		}
		for (size_t i = 0; i < m_spot_lights.size() && i < MAX_SPOT_LIGHTS; ++i)
		{
			GPUSpotLight& s = L.spot_lights[i];
			s.position = m_spot_lights[i].position; s.range = m_spot_lights[i].range;
			XMFLOAT3 dir = m_spot_lights[i].direction;
			float len = sqrtf(dir.x * dir.x + dir.y * dir.y + dir.z * dir.z);
			if (len > 0.0001f) { dir.x /= len; dir.y /= len; dir.z /= len; }
			s.direction = dir;
			s.spot_angle = m_spot_lights[i].spot_angle;
			s.color = m_spot_lights[i].color;
			s.intensity = m_spot_lights[i].intensity;
			s.inner_spot_angle = m_spot_lights[i].inner_spot_angle;
			s.shadow_slot = -1.0f;   // prepare_shadow_pass assigns the atlas tiles after the view is known
		}
		XMFLOAT4X4 ident; XMStoreFloat4x4(&ident, XMMatrixIdentity());
		for (auto& m : L.shadow_vp) m = ident;
		for (auto& m : L.cascade_vp) m = ident;
		for (auto& m : L.point_face_vp) m = ident;
		L.point_shadows[0] = L.point_shadows[1] = { -1.0f, 0.0f, 0.0f, 0.0f };
		L.dir_shadow_params = { m_dir_shadow_strength, m_dir_shadow_bias, 0.0f, m_dir_shadow_softness };
		L.cascade_splits = { 0.0f, 0.0f, 0.0f, m_dir_shadow_distance };
		fill_sky_light(L);
	}

	SdlGpuRenderer::FrameView SdlGpuRenderer::build_main_view(u32 width, u32 height)
	{
		using namespace DirectX;
		FrameView v{};
		XMVECTOR eye = XMLoadFloat3(&m_camera_position);
		XMVECTOR at = XMLoadFloat3(&m_camera_target);
		XMVECTOR up = XMLoadFloat3(&m_camera_up);
		XMMATRIX view = XMMatrixLookAtLH(eye, at, up);
		const float aspect = (float)width / (float)(height ? height : 1);
		XMMATRIX proj = XMMatrixPerspectiveFovLH(XMConvertToRadians(m_fov_degrees), aspect, m_near_clip, m_far_clip);
		XMMATRIX vp = view * proj;

		v.frame = m_frame_constants;   // keeps the persistent fog fields
		XMStoreFloat4x4(&v.frame.view_projection, vp);
		v.view_projection = v.frame.view_projection;
		XMStoreFloat4x4(&v.inverse_view_projection, XMMatrixInverse(nullptr, vp));
		v.frame.camera_position = m_camera_position;
		v.frame.light_direction = m_light_direction;
		v.frame.directional_intensity = m_directional_intensity;
		v.frame.light_color = m_light_color;
		v.frame.ambient_strength = m_ambient_strength;
		v.frame.point_light_count = (u32)(std::min)(m_point_lights.size(), (size_t)MAX_POINT_LIGHTS);
		v.frame.spot_light_count = (u32)(std::min)(m_spot_lights.size(), (size_t)MAX_SPOT_LIGHTS);
		v.frame.ssao_enabled = 0.0f;
		v.frame.gi_strength = 0.0f;
		fill_environment(v.frame);
		v.frame.shadow_map_texel = 1.0f / (float)SHADOW_TILE_SIZE;
		v.eye = m_camera_position;

		v.viewmodel = v.frame;
		XMMATRIX vm_proj = XMMatrixPerspectiveFovLH(XMConvertToRadians(m_viewmodel_fov), aspect, VIEWMODEL_NEAR, VIEWMODEL_FAR);
		XMStoreFloat4x4(&v.viewmodel.view_projection, view * vm_proj);
		camera_basis(m_camera_position, m_camera_target, m_camera_up, v.right, v.up, v.forward);
		v.near_clip = m_near_clip; v.far_clip = m_far_clip; v.ortho = false;
		v.frame.camera_clip = { v.near_clip, v.far_clip, 0.0f, 0.0f };
		v.tan_half_y = tanf(XMConvertToRadians(m_fov_degrees) * 0.5f);
		v.tan_half_x = v.tan_half_y * aspect;
		fill_light_buffer();
		return v;
	}

	SdlGpuRenderer::FrameView SdlGpuRenderer::build_camera_view(const ViewportCamera& camera, u32 width, u32 height)
	{
		using namespace DirectX;
		FrameView v{};
		XMVECTOR eye = XMLoadFloat3(&camera.position);
		XMVECTOR at = XMLoadFloat3(&camera.target);
		XMVECTOR up = XMLoadFloat3(&camera.up);
		XMMATRIX view = XMMatrixLookAtLH(eye, at, up);
		const float aspect = (float)width / (float)(height ? height : 1);
		XMMATRIX proj = camera.orthographic
			? XMMatrixOrthographicLH(camera.ortho_size * aspect, camera.ortho_size, camera.near_clip, camera.far_clip)
			: XMMatrixPerspectiveFovLH(XMConvertToRadians(camera.fov_degrees), aspect, camera.near_clip, camera.far_clip);
		XMMATRIX vp = view * proj;

		v.frame = m_frame_constants;
		XMStoreFloat4x4(&v.frame.view_projection, vp);
		v.view_projection = v.frame.view_projection;
		XMStoreFloat4x4(&v.inverse_view_projection, XMMatrixInverse(nullptr, vp));
		v.frame.camera_position = camera.position;
		v.frame.light_direction = m_light_direction;
		v.frame.directional_intensity = m_directional_intensity;
		v.frame.light_color = m_light_color;
		v.frame.ambient_strength = m_ambient_strength;
		v.frame.point_light_count = (u32)(std::min)(m_point_lights.size(), (size_t)MAX_POINT_LIGHTS);
		v.frame.spot_light_count = (u32)(std::min)(m_spot_lights.size(), (size_t)MAX_SPOT_LIGHTS);
		v.frame.ssao_enabled = 0.0f;
		v.frame.gi_strength = 0.0f;
		fill_environment(v.frame);
		v.frame.shadow_map_texel = 1.0f / (float)SHADOW_TILE_SIZE;
		v.eye = camera.position;
		v.viewmodel = v.frame;
		camera_basis(camera.position, camera.target, camera.up, v.right, v.up, v.forward);
		v.near_clip = camera.near_clip; v.far_clip = camera.far_clip; v.ortho = camera.orthographic;
		v.frame.camera_clip = { v.near_clip, v.far_clip, 0.0f, 0.0f };
		if (camera.orthographic) { v.tan_half_y = camera.ortho_size * 0.5f; v.tan_half_x = v.tan_half_y * aspect; }
		else { v.tan_half_y = tanf(XMConvertToRadians(camera.fov_degrees) * 0.5f); v.tan_half_x = v.tan_half_y * aspect; }
		fill_light_buffer();
		return v;
	}

	// ---------------------------------------------------------------------------------------------
	// CPU side: sort, build runs, cull + pack instances (main-thread; MT cull is a follow-up here)
	// ---------------------------------------------------------------------------------------------
	void SdlGpuRenderer::prepare_scene(const FrameView& view)
	{
		using namespace DirectX;
		auto& reg = ResourceRegistry::instance();
		m_instance_staging.clear();
		m_instance_count = 0;

		// A mesh was destroyed since the draw runs were built (preview renders, asset reloads, runtime Destroy):
		// the cached runs hold raw Mesh pointers and the kept queue may still name the dead mesh — drop those items
		// and rebuild, instead of drawing through freed memory.
		if (const u32 gen = reg.mesh_generation(); gen != m_seen_mesh_generation)
		{
			m_seen_mesh_generation = gen;
			m_render_queue.erase(std::remove_if(m_render_queue.begin(), m_render_queue.end(), [&reg](const RenderItem& it)
			{
				Mesh* m = reg.get_mesh(it.mesh_id);
				return m == nullptr || !m->is_valid();
			}), m_render_queue.end());
			m_draw_runs.clear();
			m_queue_dirty = true;
		}

		size_t objectCount = (std::min)(m_render_queue.size(), (size_t)MAX_RENDER_OBJECTS);
		if (m_render_queue.size() > (size_t)MAX_RENDER_OBJECTS) m_render_queue.resize(MAX_RENDER_OBJECTS);
		if (objectCount == 0) m_draw_runs.clear();
		m_mt_active = false;

		const bool needRebuild = m_queue_dirty || (m_draw_runs.empty() && objectCount > 0);
		if (needRebuild && objectCount > 0)
		{
			const XMFLOAT3 eye = view.eye;
			std::sort(m_render_queue.begin(), m_render_queue.end(), [&eye](const RenderItem& a, const RenderItem& b)
			{
				if (a.layer != b.layer) return a.layer < b.layer;
				if (a.material_id != b.material_id) return a.material_id < b.material_id;
				if (a.mesh_id != b.mesh_id) return a.mesh_id < b.mesh_id;
				if (a.mirrored != b.mirrored) return a.mirrored < b.mirrored;
				float ax = a.world_matrix._41 - eye.x, ay = a.world_matrix._42 - eye.y, az = a.world_matrix._43 - eye.z;
				float bx = b.world_matrix._41 - eye.x, by = b.world_matrix._42 - eye.y, bz = b.world_matrix._43 - eye.z;
				return (ax * ax + ay * ay + az * az) < (bx * bx + by * by + bz * bz);
			});

			m_draw_runs.clear();
			m_item_run.resize(objectCount);
			size_t i = 0;
			while (i < objectCount)
			{
				const auto idMesh = m_render_queue[i].mesh_id;
				const auto idMat = m_render_queue[i].material_id;
				const u32 idLayer = m_render_queue[i].layer;
				const bool skinnedRun = m_render_queue[i].bone_offset != NO_BONES;
				const u32 idMirrored = m_render_queue[i].mirrored;
				size_t j = i;
				if (skinnedRun) j = i + 1;
				else while (j < objectCount && m_render_queue[j].mesh_id == idMesh && m_render_queue[j].material_id == idMat
					&& m_render_queue[j].bone_offset == NO_BONES && m_render_queue[j].layer == idLayer
					&& m_render_queue[j].mirrored == idMirrored) ++j;
				Mesh* meshp = reg.get_mesh(idMesh);
				float minx = 0, miny = 0, minz = 0, maxx = 1, maxy = 1, maxz = 1;
				if (meshp && meshp->is_valid()) { meshp->get_min(minx, miny, minz); meshp->get_max(maxx, maxy, maxz); }
				DrawRun run{};
				run.layer = idLayer;
				run.mirrored = idMirrored != 0;
				run.start = i; run.count = (u32)(j - i); run.mesh = idMesh; run.mat = idMat; run.meshp = meshp;
				run.defaultBounds = (minx == 0.f && miny == 0.f && minz == 0.f && maxx == 1.f && maxy == 1.f && maxz == 1.f);
				run.lcx = (minx + maxx) * 0.5f; run.lcy = (miny + maxy) * 0.5f; run.lcz = (minz + maxz) * 0.5f;
				float bdx = maxx - minx, bdy = maxy - miny, bdz = maxz - minz;
				run.localR = 0.5f * sqrtf(bdx * bdx + bdy * bdy + bdz * bdz);
				if (skinnedRun)
				{
					run.skinned = true;
					run.boneOffset = m_render_queue[i].bone_offset;
					run.boneCount = m_render_queue[i].bone_count;
					run.localR *= 2.0f;
				}
				if (m_geo_lod_enabled && !skinnedRun)
				{
					if (const auto* chain = reg.get_lod_chain(idMesh); chain && chain->lod_count > 1)
					{
						run.lodLevels = chain->lod_count;
						for (u32 L = 0; L < chain->lod_count && L < 4; ++L) run.lodMesh[L] = chain->lods[L];
						// thresholds scale with the mesh's radius (#360): a crate thins out at 40 m, a building at 800 m
						const float radius = run.localR > 0.5f ? run.localR : 0.5f;
						const float t1 = m_lod_mid * radius, t2 = m_lod_far * radius, t3 = m_lod_far * 1.8f * radius;
						run.lodT1sq = t1 * t1;
						run.lodT2sq = t2 * t2;
						run.lodT3sq = t3 * t3;
					}
				}
				const u32 ri = (u32)m_draw_runs.size();
				m_draw_runs.push_back(run);
				for (size_t k = i; k < j; ++k) m_item_run[k] = ri;
				i = j;
			}
			m_queue_dirty = false;
		}

		const size_t runN = m_draw_runs.size();
		m_instances_tested += (int)objectCount;
		const FrustumPlanes frustum = extract_frustum(view.view_projection);
		const XMFLOAT3 eye = view.eye;
		const bool useDist = m_render_distance > 0.0f;
		const float rd2 = m_render_distance * m_render_distance;
		const bool useLod = m_lod_enabled && m_lod_mid > 0.0f && !m_geo_lod_enabled;
		const float lodMid2 = m_lod_mid * m_lod_mid, lodFar2 = m_lod_far * m_lod_far;

		// Pass A: visibility + LOD bucket per item (stored in m_item_lod; 0xFF = culled).
		if (m_item_lod.size() < objectCount) m_item_lod.resize(objectCount);
		std::vector<u32> counts(runN * 4, 0);
		for (size_t k = 0; k < objectCount; ++k)
		{
			const u32 ri = m_item_run[k];
			const DrawRun& run = m_draw_runs[ri];
			const RenderItem& item = m_render_queue[k];
			bool visible = true; int lod = 0;
			if (!run.defaultBounds && run.layer == 0)
			{
				const XMFLOAT4X4& W = item.world_matrix;
				XMVECTOR wc = XMVector3TransformCoord(XMVectorSet(run.lcx, run.lcy, run.lcz, 1.f), XMLoadFloat4x4(&W));
				float cx = XMVectorGetX(wc), cy = XMVectorGetY(wc), cz = XMVectorGetZ(wc);
				float sx = sqrtf(W._11 * W._11 + W._12 * W._12 + W._13 * W._13);
				float sy = sqrtf(W._21 * W._21 + W._22 * W._22 + W._23 * W._23);
				float sz = sqrtf(W._31 * W._31 + W._32 * W._32 + W._33 * W._33);
				float maxScale = sx > sy ? (sx > sz ? sx : sz) : (sy > sz ? sy : sz);
				visible = sphere_in_frustum(frustum, cx, cy, cz, run.localR * maxScale + 0.05f);
				if (visible && (useDist || useLod || run.lodLevels > 1))
				{
					float dx = cx - eye.x, dy = cy - eye.y, dz = cz - eye.z;
					float d2 = dx * dx + dy * dy + dz * dz;
					if (useDist && d2 > rd2) visible = false;
					else if (useLod)
					{
						if (d2 > lodFar2) { if ((k & 3) != 0) visible = false; }
						else if (d2 > lodMid2) { if ((k & 1) != 0) visible = false; }
					}
					else if (run.lodLevels > 1)
					{
						lod = (d2 > run.lodT3sq) ? 3 : (d2 > run.lodT2sq) ? 2 : (d2 > run.lodT1sq) ? 1 : 0;
						if (lod >= (int)run.lodLevels) lod = (int)run.lodLevels - 1;
					}
				}
			}
			if (visible) { m_item_lod[k] = (unsigned char)lod; counts[(size_t)ri * 4 + lod]++; }
			else m_item_lod[k] = 0xFF;
		}

		// Compact prefix sums: each run gets a contiguous slab, LOD segments contiguous inside it.
		u32 globalBase = 0;
		std::vector<u32> cursor(runN * 4, 0);
		for (size_t r = 0; r < runN; ++r)
		{
			m_draw_runs[r].vbBase = globalBase;
			u32 localBase = 0;
			for (int L = 0; L < 4; ++L)
			{
				u32 cnt = counts[r * 4 + L];
				const u32 segStart = globalBase + localBase;
				if (segStart >= MAX_RENDER_OBJECTS) cnt = 0;
				else if (segStart + cnt > MAX_RENDER_OBJECTS) cnt = MAX_RENDER_OBJECTS - segStart;
				m_draw_runs[r].lodCount[L] = cnt;
				cursor[r * 4 + L] = segStart;
				localBase += cnt;
			}
			m_draw_runs[r].visible = localBase;
			globalBase += localBase;
		}

		// Pass B: pack the visible world matrices into the staging vector at their slab positions.
		m_instance_staging.resize((size_t)globalBase * 16);
		for (size_t k = 0; k < objectCount; ++k)
		{
			const unsigned char lod = m_item_lod[k];
			if (lod == 0xFF) continue;
			const u32 ri = m_item_run[k];
			const u32 dst = cursor[(size_t)ri * 4 + lod]++;
			if (dst < globalBase) pack_instance(m_instance_staging.data() + (size_t)dst * 16, m_render_queue[k]);
		}

		// Gizmo items ride in the same buffer after the scene slabs (solid list first, then wire).
		m_gizmo_instance_base = globalBase;
		u32 gizmoCount = 0;
		auto append_gizmos = [&](const std::vector<RenderItem>& list)
		{
			for (const RenderItem& item : list)
			{
				if (gizmoCount >= MAX_GIZMO_ITEMS || m_gizmo_instance_base + gizmoCount >= MAX_RENDER_OBJECTS) return;
				m_instance_staging.insert(m_instance_staging.end(), &item.world_matrix._11, &item.world_matrix._11 + 16);
				++gizmoCount;
			}
		};
		append_gizmos(m_gizmo_render);
		append_gizmos(m_gizmo_wire_render);
		m_instance_count = globalBase + gizmoCount;
	}

	void SdlGpuRenderer::upload_dynamic(SDL_GPUCommandBuffer* cmd)
	{
		const bool upload_instances = m_instance_count > 0;
		const bool upload_bones = m_bone_upload_pending && !m_bone_render.empty();
		if (!upload_instances && !upload_bones) return;
		SDL_GPUCopyPass* copy = SDL_BeginGPUCopyPass(cmd);
		if (upload_instances)
		{
			const u32 bytes = m_instance_count * 64;
			void* mapped = SDL_MapGPUTransferBuffer(m_device, m_instance_transfer, true);
			if (mapped)
			{
				memcpy(mapped, m_instance_staging.data(), bytes);
				SDL_UnmapGPUTransferBuffer(m_device, m_instance_transfer);
				SDL_GPUTransferBufferLocation src{ m_instance_transfer, 0 };
				SDL_GPUBufferRegion dst{ m_instance_buffer, 0, bytes };
				SDL_UploadToGPUBuffer(copy, &src, &dst, true);
			}
		}
		if (upload_bones)
		{
			m_bone_upload_pending = false;
			const size_t floats = (std::min)(m_bone_render.size(), (size_t)MAX_BONE_MATRICES * 16);
			const u32 bytes = (u32)(floats * sizeof(float));
			void* mapped = SDL_MapGPUTransferBuffer(m_device, m_bone_transfer, true);
			if (mapped)
			{
				memcpy(mapped, m_bone_render.data(), bytes);
				SDL_UnmapGPUTransferBuffer(m_device, m_bone_transfer);
				SDL_GPUTransferBufferLocation src{ m_bone_transfer, 0 };
				SDL_GPUBufferRegion dst{ m_bone_buffer, 0, bytes };
				SDL_UploadToGPUBuffer(copy, &src, &dst, true);
			}
		}
		SDL_EndGPUCopyPass(copy);
	}

	void SdlGpuRenderer::upload_staged_bone_palettes() {}

	// ---------------------------------------------------------------------------------------------
	// GPU side: the scene passes
	// ---------------------------------------------------------------------------------------------
	void SdlGpuRenderer::push_frame_uniforms(SDL_GPUCommandBuffer* cmd, const PerFrameConstants& frame)
	{
		SDL_PushGPUVertexUniformData(cmd, 0, &frame, sizeof(PerFrameConstants));
		SDL_PushGPUFragmentUniformData(cmd, 0, &frame, sizeof(PerFrameConstants));
		SDL_PushGPUFragmentUniformData(cmd, 2, &m_light_data, sizeof(LightBufferData));
	}

	SdlGpuParticles::View SdlGpuRenderer::particle_view(const FrameView& view) const
	{
		SdlGpuParticles::View p{};
		p.view_projection = view.view_projection;
		p.viewmodel_projection = view.viewmodel.view_projection;
		p.eye = view.eye; p.right = view.right; p.up = view.up; p.forward = view.forward;
		p.near_clip = view.near_clip; p.far_clip = view.far_clip; p.ortho = view.ortho;
		p.vm_near_clip = VIEWMODEL_NEAR; p.vm_far_clip = VIEWMODEL_FAR;
		p.tan_half_x = view.tan_half_x; p.tan_half_y = view.tan_half_y;
		return p;
	}

	SdlGpuParticles::Environment SdlGpuRenderer::particle_environment(const FrameView& view) const
	{
		SdlGpuParticles::Environment e{};
		const PerFrameConstants& f = view.frame;
		e.fog_color = f.fog_color; e.fog_density = f.fog_density;
		e.fog_height_y = f.fog_height_y; e.fog_height_falloff = f.fog_height_falloff;
		e.scene_exposure = f.scene_exposure > 0.0f ? f.scene_exposure : 0.5f; e.fog_sun_glow = f.fog_sun_glow; e.tonemapper = f.tonemapper;
		e.sun_direction = f.light_direction; e.sun_intensity = f.directional_intensity;
		e.sun_color = f.light_color; e.ambient = f.ambient_strength;
		e.point_lights = f.point_light_count; e.spot_lights = f.spot_light_count;
		static_assert(sizeof(GPUPointLight) * MAX_POINT_LIGHTS + sizeof(GPUSpotLight) * MAX_SPOT_LIGHTS == 1024, "particle light block");
		e.lights = &m_light_data;   // point + spot lights lead the struct (the 1024 bytes particles.metal reads)
		return e;
	}

	void SdlGpuRenderer::record_scene(SDL_GPUCommandBuffer* cmd, GpuTarget& target, const FrameView& view_in,
		bool draw_skybox_pass, bool draw_grid_pass, bool draw_gizmo_pass, int particle_world, bool particle_depth_capture)
	{
		FrameView view = view_in;
		const bool ssao_on = m_ssao_enabled && m_post_ready && !draw_gizmo_pass ? true : (m_ssao_enabled && m_post_ready);
		view.frame.ssao_enabled = ssao_on ? 1.0f : 0.0f;
		view.frame.ssao_padding[0] = 1.0f / (float)(target.width ? target.width : 1);
		view.frame.ssao_padding[1] = 1.0f / (float)(target.height ? target.height : 1);
		view.viewmodel.ssao_enabled = 0.0f;   // the first-person layer has no AO (own projection)
		// screen-space GI (#257 step 1): only where the previous frame's copy exists at this size (the main view)
		view.frame.gi_strength = (ssao_on && m_gi_enabled && m_gi_prev_valid && m_gi_prev_w == target.width && m_gi_prev_h == target.height) ? m_gi_strength : 0.0f;
		view.viewmodel.gi_strength = 0.0f;
		prepare_scene(view);
		prepare_shadow_pass(view);
		upload_dynamic(cmd);
		record_shadow_passes(cmd);
		if (ssao_on) record_ssao(cmd, view, target.width, target.height); else m_ssao_current = nullptr;
		// Particles (VFX): gather + upload before the scene pass; each layer draws right after its meshes.
		const SdlGpuParticles::View pview = particle_view(view);
		const bool fx = particle_world >= 0 && m_particles.prepare(cmd, pview, (u32)particle_world);
		const bool fx0 = fx && m_particles.has_layer(0);
		const bool fx1 = fx && m_particles.has_layer(1);
		// Decals (#120): the scene view's list, built + uploaded before the pass, drawn between the opaque and the transparent meshes.
		const bool dx = particle_world == 0 && m_decals.ready() && m_decals.prepare(cmd, m_decal_list);
		const bool vf = particle_world == 0 && m_volumetrics.active();   // volumetric fog (#119)
		const SdlGpuParticles::Environment penv = (fx || dx || vf) ? particle_environment(view) : SdlGpuParticles::Environment{};

		SDL_GPUColorTargetInfo color{};
		color.texture = target.color;
		color.clear_color = { m_clear_color[0], m_clear_color[1], m_clear_color[2], m_clear_color[3] };
		color.load_op = SDL_GPU_LOADOP_CLEAR;
		color.store_op = SDL_GPU_STOREOP_STORE;
		SDL_GPUDepthStencilTargetInfo depth{};
		depth.texture = target.depth;
		depth.clear_depth = 1.0f;
		depth.load_op = SDL_GPU_LOADOP_CLEAR;
		depth.store_op = SDL_GPU_STOREOP_STORE;
		depth.stencil_load_op = SDL_GPU_LOADOP_DONT_CARE;
		depth.stencil_store_op = SDL_GPU_STOREOP_DONT_CARE;

		const size_t runN = m_draw_runs.size();
		size_t vmStart = runN;
		for (size_t r = 0; r < runN; ++r) if (m_draw_runs[r].layer != 0) { vmStart = r; break; }
		const bool has_viewmodel = vmStart < runN;
		const bool vm_pass = has_viewmodel || fx1;   // viewmodel particles need the cleared (viewmodel-only) depth too
		const bool gizmos = draw_gizmo_pass && (!m_gizmo_render.empty() || !m_gizmo_wire_render.empty());
		// Always-on-top gizmos go last; after a particle pass they need a pass of their own (depth LOAD).
		auto gizmo_pass = [&]()
		{
			SDL_GPUColorTargetInfo gc = color;
			gc.load_op = SDL_GPU_LOADOP_LOAD;
			SDL_GPUDepthStencilTargetInfo gd = depth;
			gd.load_op = SDL_GPU_LOADOP_LOAD;
			SDL_GPURenderPass* gp = SDL_BeginGPURenderPass(cmd, &gc, 1, &gd);
			if (!gp) return;
			SDL_GPUViewport gv{ 0, 0, (float)target.width, (float)target.height, 0.0f, 1.0f };
			SDL_SetGPUViewport(gp, &gv);
			push_frame_uniforms(cmd, view.frame);   // the particle pass left its own uniforms in the slots
			draw_gizmos(gp, cmd);
			SDL_EndGPURenderPass(gp);
		};

		SDL_GPURenderPass* pass = SDL_BeginGPURenderPass(cmd, &color, 1, &depth);
		if (!pass) return;
		SDL_GPUViewport vp{ 0, 0, (float)target.width, (float)target.height, 0.0f, 1.0f };
		SDL_SetGPUViewport(pass, &vp);
		if (draw_skybox_pass) draw_skybox(pass, cmd, view);
		if (draw_grid_pass) draw_grid(pass, cmd, view);
		// The skybox/grid pushed their own constants into uniform slot 0 — the scene shaders read PerFrame there.
		push_frame_uniforms(cmd, view.frame);
		{
			// opaque meshes, then the decal pass (it samples the depth, so it needs a pass of its own), then a copy of the
			// colour + depth so far for the water's refraction / screen-space reflections (#200), then the transparent
			// meshes with colour + depth loaded
			record_runs(pass, cmd, 0, vmStart, view, 1u);
			SDL_EndGPURenderPass(pass);
			if (dx)
			{
				SdlGpuDecals::View dv{};
				dv.view_projection = view.view_projection; dv.inv_view_projection = view.inverse_view_projection; dv.eye = view.eye;
				dv.near_clip = view.near_clip; dv.far_clip = view.far_clip; dv.ortho = view.ortho;
				m_decals.draw(cmd, target.color, target.depth, target.width, target.height, dv, penv);
			}
			m_scene_copy_valid = false;
			if (m_transparent_runs_seen || (m_gi_enabled && !draw_gizmo_pass)) copy_scene_for_transparents(cmd, target);   // the GI pass reads it next frame
			if (m_scene_copy_valid) { m_gi_prev_view_projection = view.view_projection; m_gi_prev_w = target.width; m_gi_prev_h = target.height; m_gi_prev_valid = true; }
			color.load_op = SDL_GPU_LOADOP_LOAD;
			depth.load_op = SDL_GPU_LOADOP_LOAD;
			pass = SDL_BeginGPURenderPass(cmd, &color, 1, &depth);
			if (!pass) return;
			SDL_SetGPUViewport(pass, &vp);
			push_frame_uniforms(cmd, view.frame);   // the decal pass left its own uniforms in the slots
			record_runs(pass, cmd, 0, vmStart, view, 2u);
		}
		if (!vm_pass && gizmos && !fx0) draw_gizmos(pass, cmd);
		SDL_EndGPURenderPass(pass);

		// Volumetric fog (#119): over the finished world, before the particles (they carry the analytic fog themselves).
		if (vf)
		{
			SdlGpuVolumetrics::View vv{};
			vv.inv_view_projection = view.inverse_view_projection; vv.eye = view.eye; vv.near_clip = view.near_clip; vv.far_clip = view.far_clip; vv.ortho = view.ortho;
			SdlGpuVolumetrics::Shadows sh{};
			sh.spot_atlas = m_shadow_atlas; sh.csm_atlas = m_csm_atlas; sh.point_atlas = m_point_atlas; sh.comparison = m_sampler_shadow; sh.map_texel = view.frame.shadow_map_texel;
			m_volumetrics.draw(cmd, target.color, target.depth, target.width, target.height, vv, penv, &m_light_data, sh);
		}
		// World-layer particles: after the opaque + transparent meshes, depth-tested against the world depth.
		if (fx0) m_particles.draw_layer(cmd, target.color, target.depth, target.width, target.height, 0, pview, penv);
		// Collision snapshot of the world depth (before the viewmodel pass clears it).
		if (particle_depth_capture && particle_world == 0) m_particles.capture_depth(cmd, target.depth, target.width, target.height, pview);
		if (!vm_pass && gizmos && fx0) gizmo_pass();

		if (vm_pass)
		{
			// First-person layer: own projection, cleared depth so the arms/weapon never clip the world.
			color.load_op = SDL_GPU_LOADOP_LOAD;
			depth.load_op = SDL_GPU_LOADOP_CLEAR;
			pass = SDL_BeginGPURenderPass(cmd, &color, 1, &depth);
			if (!pass) return;
			SDL_SetGPUViewport(pass, &vp);
			push_frame_uniforms(cmd, view.viewmodel);
			record_runs(pass, cmd, vmStart, runN, view);
			push_frame_uniforms(cmd, view.frame);
			if (gizmos && !fx1) draw_gizmos(pass, cmd);
			SDL_EndGPURenderPass(pass);
			// Viewmodel-layer particles (muzzle flash on the weapon): viewmodel projection, viewmodel depth only.
			if (fx1)
			{
				m_particles.draw_layer(cmd, target.color, target.depth, target.width, target.height, 1, pview, penv);
				if (gizmos) gizmo_pass();
			}
		}
	}

	void SdlGpuRenderer::draw_skybox(SDL_GPURenderPass* pass, SDL_GPUCommandBuffer* cmd, const FrameView& view)
	{
		if (!m_pipeline_skybox) return;
		SkyboxConstants c{};
		c.inverse_view_projection = view.inverse_view_projection;
		c.camera_position = view.eye;
		c.sky_color = m_sky_color; c.horizon_color = m_horizon_color; c.ground_color = m_ground_color;
		c.sun_direction = m_sun_direction; c.sun_intensity = m_sun_intensity; c.sun_color = m_sun_color;
		c.tonemapper = (float)m_frame_constants.tonemapper;
		c.haze = { m_frame_constants.fog_color.x, m_frame_constants.fog_color.y, m_frame_constants.fog_color.z, m_frame_constants.fog_density };
		c.glow_dir = { m_frame_constants.light_direction.x, m_frame_constants.light_direction.y, m_frame_constants.light_direction.z, m_frame_constants.fog_sun_glow };
		c.glow_color = { m_frame_constants.light_color.x, m_frame_constants.light_color.y, m_frame_constants.light_color.z, 0.0f };
		// the equirect map (#326) — or the white texture, so sampler slot 0 is always bound
		auto& reg = ResourceRegistry::instance();
		Texture* sky = (m_skybox_mode == SkyboxMode::Texture && m_sky_texture != id::invalid_id) ? reg.get_texture(m_sky_texture) : nullptr;
		c.params = { sky ? 1.0f : 0.0f, m_sky_exposure, m_sky_rotation,
			(sky && m_sky_hdr == 2) ? (m_frame_constants.scene_exposure > 0.0f ? m_frame_constants.scene_exposure : 0.5f) : ((sky && m_sky_hdr == 1) ? -1.0f : 0.0f) };   // w > 0: HDR sky tonemapped, w < 0: HDR sky the classic way
		if (!sky) sky = reg.white_texture();
		SDL_BindGPUGraphicsPipeline(pass, m_pipeline_skybox);
		SDL_PushGPUVertexUniformData(cmd, 0, &c, sizeof(c));
		SDL_PushGPUFragmentUniformData(cmd, 0, &c, sizeof(c));
		if (sky && sky->texture())
		{
			SDL_GPUTextureSamplerBinding tex{ sky->texture(), m_sampler_linear_wrap };
			SDL_BindGPUFragmentSamplers(pass, 0, &tex, 1);
		}
		SDL_DrawGPUPrimitives(pass, 3, 1, 0, 0);
		++m_draw_call_count;
	}

	void SdlGpuRenderer::draw_grid(SDL_GPURenderPass* pass, SDL_GPUCommandBuffer* cmd, const FrameView& view)
	{
		if (!m_pipeline_grid) return;
		GridConstants c{};
		c.view_projection = view.view_projection;
		c.inverse_view_projection = view.inverse_view_projection;
		c.camera_position = view.eye;
		c.spacing = m_grid_spacing; c.extent = m_grid_extent; c.major = m_grid_major_interval;
		SDL_BindGPUGraphicsPipeline(pass, m_pipeline_grid);
		SDL_PushGPUVertexUniformData(cmd, 0, &c, sizeof(c));
		SDL_PushGPUFragmentUniformData(cmd, 0, &c, sizeof(c));
		SDL_DrawGPUPrimitives(pass, 3, 1, 0, 0);
		++m_draw_call_count;
	}

	void SdlGpuRenderer::bind_material(SDL_GPURenderPass* pass, SDL_GPUCommandBuffer* cmd, Material* mat, PerObjectConstants& obj, bool gizmo)
	{
		auto& reg = ResourceRegistry::instance();
		Texture* white = reg.white_texture();
		SDL_GPUTexture* white_tex = white ? white->texture() : nullptr;
		SDL_GPUTextureSamplerBinding bindings[13];
		for (auto& b : bindings) { b.texture = white_tex; b.sampler = m_sampler_linear_wrap; }
		bindings[9] = { m_ssao_current ? m_ssao_current : white_tex, m_sampler_linear_clamp };   // blurred SSAO (slot 9)
		{
			// the sky as a reflection (slot 10, IBL step 2): the equirect with mips, wrapped so the seam samples across
			Texture* env = (m_env_map != id::invalid_id) ? reg.get_texture(m_env_map) : nullptr;
			bindings[10] = { env && env->is_valid() && env->texture() ? env->texture() : white_tex, m_sampler_linear_wrap };
		}
		// the opaque colour + depth so far (slots 11 / 12, #200): water refraction + screen-space reflections; the white
		// texture (1 px wide = "no copy" for the shader) and the shadow dummy before the transparent pass made the copies
		bindings[11] = { (m_scene_copy_valid && m_scene_color_copy) ? m_scene_color_copy : white_tex, m_sampler_linear_clamp };
		bindings[12] = { (m_scene_copy_valid && m_scene_depth_copy) ? m_scene_depth_copy : (m_shadow_dummy ? m_shadow_dummy : white_tex), m_sampler_point_clamp };
		// Shadow atlases (slots 6..8) with the comparison sampler; the shaders only sample them when a light
		// asks. A missing atlas falls back to the 1x1 depth texture, NOT to white_tex: these slots are declared
		// as comparison-sampled depth textures, and a colour texture with a plain sampler is not a legal
		// binding for them under Vulkan.
		bindings[6] = { m_shadow_atlas ? m_shadow_atlas : m_shadow_dummy, m_sampler_shadow };
		bindings[7] = { m_csm_atlas ? m_csm_atlas : m_shadow_dummy, m_sampler_shadow };
		bindings[8] = { m_point_atlas ? m_point_atlas : m_shadow_dummy, m_sampler_shadow };

		if (gizmo)
		{
			obj.base_color = { 0.9f, 0.9f, 0.95f, 1.0f };
			obj.metallic = 0.0f; obj.roughness = 1.0f; obj.ao = 1.0f; obj.normal_strength = 1.0f;
			obj.use_directx_normals = 1; obj.emissive_strength = 1.0f; obj.uv_tiling = { 1.0f, 1.0f };
			if (mat)
			{
				const auto& props = mat->properties();
				obj.base_color = props.base_color;
				obj.is_unlit = props.is_unlit;
				obj.emissive_strength = props.emissive_strength;
			}
		}
		else
		{
			obj.base_color = { 0.85f, 0.85f, 0.88f, 1.0f };
			obj.metallic = 0.7f; obj.roughness = 0.35f; obj.ao = 1.0f; obj.normal_strength = 1.0f;
			obj.use_directx_normals = 1; obj.uv_tiling = { 1.0f, 1.0f };
			if (mat)
			{
				const auto& props = mat->properties();
				obj.base_color = props.base_color; obj.metallic = props.metallic; obj.roughness = props.roughness;
				obj.ao = props.ao; obj.normal_strength = props.normal_strength; obj.use_directx_normals = props.use_directx_normals;
				obj.is_unlit = props.is_unlit; obj.emissive_strength = props.emissive_strength;
				obj.uv_tiling = props.uv_tiling; obj.height_scale = props.height_scale;
				obj.alpha_cutoff = props.alpha_cutoff;   // AlphaTest (#329)
				// flag = 1 + the channel a packed map is read from (see Material::set_texture_channels)
				auto bind = [&](Texture* t, int slot, u32& flag, u32 packed)
				{
					if (t && t->is_valid()) { bindings[slot].texture = t->texture(); flag = packed ? packed : 1; }
				};
				bind(mat->albedo_texture(), 0, obj.has_albedo_texture, 1);
				bind(mat->normal_texture(), 1, obj.has_normal_texture, props.has_normal_texture);   // 2 = a two-channel (BC5) map, the shader rebuilds z
				bind(mat->metallic_texture(), 2, obj.has_metallic_texture, props.has_metallic_texture);
				bind(mat->roughness_texture(), 3, obj.has_roughness_texture, props.has_roughness_texture);
				bind(mat->ao_texture(), 4, obj.has_ao_texture, props.has_ao_texture);
				bind(mat->height_texture(), 5, obj.has_height_texture, 1);
			}
		}
		SDL_PushGPUFragmentUniformData(cmd, 1, &obj, sizeof(PerObjectConstants));
		// The per-object constants reach the VERTEX stage too (#125: the foliage shader bends by the material's wind
		// fields; the DX12 root signature exposes b1 to every stage). Built-in vertex shaders declare one uniform
		// buffer and never see slot 1; custom material vertex shaders are created with two (get_or_compile_pipeline).
		// Skinned draws push their SkinParams into vertex slot 1 after this call, so they stay unaffected.
		SDL_PushGPUVertexUniformData(cmd, 1, &obj, sizeof(PerObjectConstants));
		SDL_BindGPUFragmentSamplers(pass, 0, bindings, 13);
	}

	void SdlGpuRenderer::draw_mesh(SDL_GPURenderPass* pass, Mesh* mesh, u32 instance_base, u32 instance_count)
	{
		SDL_GPUBufferBinding vbs[2] = { { mesh->vertex_buffer(), 0 }, { m_instance_buffer, instance_base * 64 } };
		SDL_BindGPUVertexBuffers(pass, 0, vbs, 2);
		if (mesh->has_indices())
		{
			SDL_GPUBufferBinding ib{ mesh->index_buffer(), 0 };
			SDL_BindGPUIndexBuffer(pass, &ib, SDL_GPU_INDEXELEMENTSIZE_32BIT);
			SDL_DrawGPUIndexedPrimitives(pass, mesh->index_count(), instance_count, 0, 0, 0);
			m_vertex_count += (int)(mesh->index_count() * instance_count);
		}
		else
		{
			SDL_DrawGPUPrimitives(pass, mesh->vertex_count(), instance_count, 0, 0);
			m_vertex_count += (int)(mesh->vertex_count() * instance_count);
		}
		++m_draw_call_count;
	}

	void SdlGpuRenderer::record_runs(SDL_GPURenderPass* pass, SDL_GPUCommandBuffer* cmd, size_t run_begin, size_t run_end, const FrameView& view, u32 phase)
	{
		auto& reg = ResourceRegistry::instance();
		std::vector<u32> transparentRuns;

		for (size_t r = run_begin; r < run_end; ++r)
		{
			const DrawRun& run = m_draw_runs[r];
			if (phase & 1u) m_instances_drawn += (int)run.visible;
			Mesh* mesh = run.meshp;
			if (!mesh || !mesh->is_valid() || run.visible == 0) continue;
			Material* mat = reg.get_material(run.mat);
			const PipelineSet& set = pipelines_for(mesh);
			const bool has_custom = m_custom_shaders.find((u32)run.mat) != m_custom_shaders.end();

			// alpha blend / additive draw in the sorted transparent pass — custom shaders included (#333); alpha test
			// (3) stays here and discards in the shader (#329)
			const u32 blend = mat ? mat->blend_mode() : 0u;
			if (!m_wireframe_mode && !run.skinned && (blend == 1 || blend == 2) && set.transparent(blend, false))
			{
				transparentRuns.push_back((u32)r);
				continue;
			}
			if (!(phase & 1u)) continue;   // transparent phase: the opaque runs were drawn before the decal pass

			// TwoSided and unlit materials cull nothing; everything else culls back faces, and a mirrored run (#334)
			// takes the counter-clockwise twin so its front faces survive
			const bool two_sided = mat && (mat->properties().is_unlit || mat->double_sided());
			SDL_GPUGraphicsPipeline* pipeline = m_wireframe_mode ? set.wireframe : (run.mirrored && set.opaque_m ? set.opaque_m : set.opaque);
			bool skinned_draw = false;
			if (run.skinned && m_pipeline_skinned && mesh->vertex_stride() == 52)
			{
				pipeline = run.mirrored && m_pipeline_skinned_m ? m_pipeline_skinned_m : m_pipeline_skinned;
				// a custom material shader applies to skinned meshes too (#332): engine skinning VS + its PSMain
				SDL_GPUGraphicsPipeline* custom = has_custom ? custom_pipeline((u32)run.mat, 0, two_sided, run.mirrored, true) : nullptr;
				if (custom) pipeline = custom;
				skinned_draw = true;
			}
			else
			{
				SDL_GPUGraphicsPipeline* custom = (has_custom && mesh->vertex_stride() == 32) ? custom_pipeline((u32)run.mat, 0, two_sided, run.mirrored) : nullptr;
				if (custom) pipeline = custom;
				else if (two_sided) pipeline = set.double_sided;
			}

			SDL_BindGPUGraphicsPipeline(pass, pipeline);
			if (skinned_draw)
			{
				SDL_GPUBuffer* bones[1] = { m_bone_buffer };
				SDL_BindGPUVertexStorageBuffers(pass, 0, bones, 1);
			}
			PerObjectConstants obj{};
			bind_material(pass, cmd, mat, obj, false);
			if (skinned_draw)
			{
				// after bind_material: it pushes the per-object constants into vertex slot 1 (custom vertex shaders), the
				// skinning shader wants its SkinParams there
				SkinParams sp{ run.boneOffset, { 0, 0, 0 } };
				SDL_PushGPUVertexUniformData(cmd, 1, &sp, sizeof(sp));
			}

			if (run.lodLevels > 1)
			{
				u32 segStart = 0;
				for (u32 L = 0; L < run.lodLevels; ++L)
				{
					const u32 c = run.lodCount[L];
					if (c == 0) continue;
					Mesh* lm = (L == 0) ? mesh : reg.get_mesh(run.lodMesh[L]);
					if (!lm || !lm->is_valid() || lm->vertex_stride() != mesh->vertex_stride()) lm = mesh;
					draw_mesh(pass, lm, run.vbBase + segStart, c);
					segStart += c;
				}
			}
			else
			{
				draw_mesh(pass, mesh, run.vbBase, run.visible);
			}
		}

		m_transparent_runs_seen = (u32)transparentRuns.size();   // the opaque phase tells the pass whether to copy the scene for them (#200)
		if (!(phase & 2u)) return;   // opaque phase only
		// Sorted transparent pass: one draw per instance, farthest first, depth write off.
		if (transparentRuns.empty()) return;
		struct TDraw { u32 run; u32 slot; float d2; };
		std::vector<TDraw> titems;
		constexpr size_t MAX_TRANSPARENT_DRAWS = 4096;
		for (u32 tr : transparentRuns)
		{
			const DrawRun& run = m_draw_runs[tr];
			for (u32 s = 0; s < run.visible && titems.size() < MAX_TRANSPARENT_DRAWS; ++s)
			{
				const float* t = m_instance_staging.data() + (size_t)(run.vbBase + s) * 16 + 12;
				float dx = t[0] - view.eye.x, dy = t[1] - view.eye.y, dz = t[2] - view.eye.z;
				titems.push_back({ tr, s, dx * dx + dy * dy + dz * dz });
			}
		}
		std::sort(titems.begin(), titems.end(), [](const TDraw& a, const TDraw& b) { return a.d2 > b.d2; });

		u32 lastRun = 0xFFFFFFFFu;
		Mesh* tmesh = nullptr;
		for (const TDraw& td : titems)
		{
			const DrawRun& run = m_draw_runs[td.run];
			if (td.run != lastRun)
			{
				lastRun = td.run;
				tmesh = run.meshp;
				if (!tmesh || !tmesh->is_valid()) { tmesh = nullptr; continue; }
				Material* mat = reg.get_material(run.mat);
				const u32 bm = mat ? mat->blend_mode() : 1u;
				const bool ds = mat && (mat->properties().is_unlit || mat->double_sided());
				SDL_GPUGraphicsPipeline* tp = nullptr;
				if (tmesh->vertex_stride() == 32 && m_custom_shaders.find((u32)run.mat) != m_custom_shaders.end())
					tp = custom_pipeline((u32)run.mat, bm, ds, run.mirrored);   // a custom shader keeps its blend mode (#333)
				if (!tp) tp = pipelines_for(tmesh).transparent(bm, ds, run.mirrored);
				if (!tp) { tmesh = nullptr; continue; }
				SDL_BindGPUGraphicsPipeline(pass, tp);
				PerObjectConstants obj{};
				bind_material(pass, cmd, mat, obj, false);
			}
			if (!tmesh) continue;
			draw_mesh(pass, tmesh, run.vbBase + td.slot, 1);
		}
	}

	void SdlGpuRenderer::draw_gizmos(SDL_GPURenderPass* pass, SDL_GPUCommandBuffer* cmd)
	{
		auto& reg = ResourceRegistry::instance();
		u32 slot = 0;
		auto draw_list = [&](const std::vector<RenderItem>& list, bool wire)
		{
			for (size_t i = 0; i < list.size() && slot < MAX_GIZMO_ITEMS; ++i)
			{
				const RenderItem& item = list[i];
				const u32 instance = m_gizmo_instance_base + slot;
				++slot;
				if (instance >= m_instance_count) break;
				Mesh* mesh = reg.get_mesh(item.mesh_id);
				if (!mesh || !mesh->is_valid()) continue;
				const PipelineSet& set = pipelines_for(mesh);
				SDL_BindGPUGraphicsPipeline(pass, wire ? set.gizmo_wire : set.gizmo);
				PerObjectConstants obj{};
				bind_material(pass, cmd, reg.get_material(item.material_id), obj, true);
				draw_mesh(pass, mesh, instance, 1);
			}
		};
		draw_list(m_gizmo_render, false);
		draw_list(m_gizmo_wire_render, true);
	}

	// The opaque colour + depth so far (#200), copied once per world pass before the transparents so the water can refract
	// what lies behind it and march the depth for screen-space reflections. Same size + format as the scene target.
	void SdlGpuRenderer::copy_scene_for_transparents(SDL_GPUCommandBuffer* cmd, const GpuTarget& target)
	{
		m_scene_copy_valid = false;
		if (!cmd || !m_device || !target.color || !target.depth || target.width == 0 || target.height == 0) return;
		if (!m_scene_color_copy || !m_scene_depth_copy || m_scene_copy_w != target.width || m_scene_copy_h != target.height)
		{
			if (m_scene_color_copy) { SDL_ReleaseGPUTexture(m_device, m_scene_color_copy); m_scene_color_copy = nullptr; }
			if (m_scene_depth_copy) { SDL_ReleaseGPUTexture(m_device, m_scene_depth_copy); m_scene_depth_copy = nullptr; }
			SDL_GPUTextureCreateInfo ci{};
			ci.type = SDL_GPU_TEXTURETYPE_2D;
			ci.width = target.width; ci.height = target.height;
			ci.layer_count_or_depth = 1; ci.num_levels = 1;
			ci.sample_count = SDL_GPU_SAMPLECOUNT_1;
			ci.format = m_scene_format;
			ci.usage = SDL_GPU_TEXTUREUSAGE_SAMPLER;
			m_scene_color_copy = SDL_CreateGPUTexture(m_device, &ci);
			ci.format = m_depth_format;
			ci.usage = SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER;
			m_scene_depth_copy = SDL_CreateGPUTexture(m_device, &ci);
			m_scene_copy_w = target.width; m_scene_copy_h = target.height;
			if (!m_scene_color_copy || !m_scene_depth_copy) { platform::debug_output(("[sdlgpu] scene copies for the transparent pass failed: " + std::string(SDL_GetError()) + "\n").c_str()); return; }
		}
		SDL_GPUCopyPass* cp = SDL_BeginGPUCopyPass(cmd);
		if (!cp) return;
		SDL_GPUTextureLocation src{}, dst{};
		src.texture = target.color; dst.texture = m_scene_color_copy;
		SDL_CopyGPUTextureToTexture(cp, &src, &dst, target.width, target.height, 1, false);
		src.texture = target.depth; dst.texture = m_scene_depth_copy;
		SDL_CopyGPUTextureToTexture(cp, &src, &dst, target.width, target.height, 1, false);
		SDL_EndGPUCopyPass(cp);
		m_scene_copy_valid = true;
	}
}
