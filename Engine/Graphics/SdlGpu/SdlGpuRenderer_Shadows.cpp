#include "SdlGpuRenderer.h"
#include "SdlGpuResources.h"
#include "../../Common/Platform.h"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <cstdio>
#include <string>
#include <unordered_map>

// Shadow maps on the SDL GPU (Metal) backend — the same design as the DX12 backend:
//   * spot atlas  : up to MAX_SHADOW_SPOTS shadow-casting spots, 2x2 tiles of SHADOW_TILE_SIZE² (texture 6)
//   * CSM atlas   : CSM_CASCADES snapped ortho crops of the camera frustum along the sun (texture 7)
//   * point atlas : up to MAX_SHADOW_POINTS lights x 6 perspective faces, 4 tiles per row (texture 8)
// Each tile is a depth-only sub-pass (viewport + scissor) drawing the casters that intersect the light frustum,
// instanced through a private caster buffer. The light buffer tails (shadow_vp / cascade_vp / point_face_vp
// + params) and the per-spot shadow_slot tell PSMain where to sample; the light buffer layout is byte-matched
// to standard.hlsl, so the shader-side rules are identical on both platforms.

namespace vortex::graphics::sdlgpu
{
	namespace
	{
		void log(const std::string& msg) { platform::debug_output(("[sdlgpu] " + msg + "\n").c_str()); }

		struct ShadowFrustum { float p[6][4]; };

		ShadowFrustum extract_shadow_frustum(const DirectX::XMFLOAT4X4& m)
		{
			ShadowFrustum f = { {
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

		bool sphere_in_shadow_frustum(const ShadowFrustum& f, float cx, float cy, float cz, float r)
		{
			for (int i = 0; i < 6; ++i)
				if (f.p[i][0] * cx + f.p[i][1] * cy + f.p[i][2] * cz + f.p[i][3] < -r) return false;
			return true;
		}

		bool build_spot_vp(const SdlGpuRenderer::SpotLightData& s, DirectX::XMFLOAT4X4& out)
		{
			using namespace DirectX;
			XMVECTOR dir = XMLoadFloat3(&s.direction);
			if (XMVectorGetX(XMVector3LengthSq(dir)) < 1e-6f) return false;
			dir = XMVector3Normalize(dir);
			XMVECTOR pos = XMLoadFloat3(&s.position);
			XMVECTOR up = fabsf(XMVectorGetY(dir)) > 0.99f ? XMVectorSet(0, 0, 1, 0) : XMVectorSet(0, 1, 0, 0);
			float fovY = XMConvertToRadians(s.spot_angle < 1.0f ? 1.0f : (s.spot_angle > 175.0f ? 175.0f : s.spot_angle));
			float farZ = s.range > 0.1f ? s.range : 0.1f;
			XMMATRIX view = XMMatrixLookToLH(pos, dir, up);
			XMMATRIX proj = XMMatrixPerspectiveFovLH(fovY, 1.0f, 0.05f, farZ);
			XMStoreFloat4x4(&out, view * proj);
			return true;
		}

		SDL_GPUTexture* create_depth_atlas(SDL_GPUDevice* dev, u32 w, u32 h, SDL_GPUTextureFormat fmt)
		{
			SDL_GPUTextureCreateInfo ti{};
			ti.type = SDL_GPU_TEXTURETYPE_2D;
			ti.format = fmt;
			ti.usage = SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER;
			ti.width = w; ti.height = h;
			ti.layer_count_or_depth = 1;
			ti.num_levels = 1;
			ti.sample_count = SDL_GPU_SAMPLECOUNT_1;
			return SDL_CreateGPUTexture(dev, &ti);
		}
	}

	bool SdlGpuRenderer::create_shadow_fallback()
	{
		if (!m_device) return false;
		if (m_sampler_shadow && m_shadow_dummy) return true;

		if (!m_sampler_shadow)
		{
			SDL_GPUSamplerCreateInfo sci{};
			sci.min_filter = SDL_GPU_FILTER_LINEAR;
			sci.mag_filter = SDL_GPU_FILTER_LINEAR;
			sci.mipmap_mode = SDL_GPU_SAMPLERMIPMAPMODE_NEAREST;
			sci.address_mode_u = sci.address_mode_v = sci.address_mode_w = SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE;
			sci.enable_compare = true;
			sci.compare_op = SDL_GPU_COMPAREOP_LESS_OR_EQUAL;
			m_sampler_shadow = SDL_CreateGPUSampler(m_device, &sci);
			if (!m_sampler_shadow) { log(std::string("shadow sampler creation failed: ") + SDL_GetError()); return false; }
		}

		if (!m_shadow_dummy)
		{
			m_shadow_dummy = create_depth_atlas(m_device, 1, 1, m_depth_format);
			if (!m_shadow_dummy) { log(std::string("shadow fallback texture creation failed: ") + SDL_GetError()); return false; }
			// Clear it to the far plane ("nothing occludes"). The shaders only sample a shadow slot when a
			// light actually asks for shadows, so this is belt and braces — but an image that was never
			// written to is undefined to sample, and a cleared depth target is not.
			if (SDL_GPUCommandBuffer* cmd = SDL_AcquireGPUCommandBuffer(m_device))
			{
				SDL_GPUDepthStencilTargetInfo dsi{};
				dsi.texture = m_shadow_dummy;
				dsi.clear_depth = 1.0f;
				dsi.load_op = SDL_GPU_LOADOP_CLEAR;
				dsi.store_op = SDL_GPU_STOREOP_STORE;
				dsi.stencil_load_op = SDL_GPU_LOADOP_DONT_CARE;
				dsi.stencil_store_op = SDL_GPU_STOREOP_DONT_CARE;
				if (SDL_GPURenderPass* pass = SDL_BeginGPURenderPass(cmd, nullptr, 0, &dsi))
					SDL_EndGPURenderPass(pass);
				SDL_SubmitGPUCommandBuffer(cmd);
			}
		}
		return true;
	}

	bool SdlGpuRenderer::ensure_shadow_resources()
	{
		if (m_shadows_ready) return true;
		if (!m_device || !m_vs_standard) return false;
		if (platform::env_flag("VORTEX_NO_SHADOWS")) return false;

		m_vs_shadow = create_shader("standard", "ShadowVS", SDL_GPU_SHADERSTAGE_VERTEX, 0, 0, 1);
		m_vs_shadow_skinned_layout = create_shader("standard", "ShadowVSSkinnedLayout", SDL_GPU_SHADERSTAGE_VERTEX, 0, 0, 1);
		m_fs_shadow = create_shader("standard", "ShadowPS", SDL_GPU_SHADERSTAGE_FRAGMENT, 0, 0, 0);
		if (!m_vs_shadow || !m_vs_shadow_skinned_layout || !m_fs_shadow) { destroy_shadow_resources(); return false; }
		// cut-out casters (#329): the albedo alpha decides what casts; optional — an older shader set keeps solid shadows
		m_vs_shadow_cut = create_shader("standard", "ShadowVSCut", SDL_GPU_SHADERSTAGE_VERTEX, 0, 0, 1);
		m_vs_shadow_cut_skinned_layout = create_shader("standard", "ShadowVSCutSkinnedLayout", SDL_GPU_SHADERSTAGE_VERTEX, 0, 0, 1);
		m_fs_shadow_cut = create_shader("standard", "ShadowPSCut", SDL_GPU_SHADERSTAGE_FRAGMENT, 1, 0, 2);

		auto make_pipeline = [&](SDL_GPUShader* vs, SDL_GPUShader* fs, u32 stride, bool skinned_layout) -> SDL_GPUGraphicsPipeline*
		{
			SDL_GPUVertexBufferDescription buffers[2] = {
				{ 0, stride, SDL_GPU_VERTEXINPUTRATE_VERTEX, 0 },
				{ 1, 64, SDL_GPU_VERTEXINPUTRATE_INSTANCE, 0 },
			};
			std::vector<SDL_GPUVertexAttribute> attrs = {
				{ 0, 0, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3, 0 },
				{ 1, 0, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3, 12 },
				{ 2, 0, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, 24 },
			};
			u32 loc = 3;
			if (skinned_layout)
			{
				attrs.push_back({ loc++, 0, SDL_GPU_VERTEXELEMENTFORMAT_UBYTE4, 32 });
				attrs.push_back({ loc++, 0, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4, 36 });
			}
			for (u32 r = 0; r < 4; ++r) attrs.push_back({ loc++, 1, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4, r * 16 });
			SDL_GPUGraphicsPipelineCreateInfo pci{};
			pci.vertex_shader = vs;
			pci.fragment_shader = fs;
			pci.vertex_input_state.vertex_buffer_descriptions = buffers;
			pci.vertex_input_state.num_vertex_buffers = 2;
			pci.vertex_input_state.vertex_attributes = attrs.data();
			pci.vertex_input_state.num_vertex_attributes = (Uint32)attrs.size();
			pci.primitive_type = SDL_GPU_PRIMITIVETYPE_TRIANGLELIST;
			pci.rasterizer_state.fill_mode = SDL_GPU_FILLMODE_FILL;
			pci.rasterizer_state.cull_mode = SDL_GPU_CULLMODE_NONE;   // thin walls/floors cast from both sides
			pci.rasterizer_state.front_face = SDL_GPU_FRONTFACE_CLOCKWISE;
			pci.rasterizer_state.enable_depth_clip = true;
			pci.multisample_state.sample_count = SDL_GPU_SAMPLECOUNT_1;
			pci.depth_stencil_state.enable_depth_test = true;
			pci.depth_stencil_state.enable_depth_write = true;
			pci.depth_stencil_state.compare_op = SDL_GPU_COMPAREOP_LESS;
			pci.target_info.num_color_targets = 0;
			pci.target_info.depth_stencil_format = m_depth_format;
			pci.target_info.has_depth_stencil_target = true;
			SDL_GPUGraphicsPipeline* p = SDL_CreateGPUGraphicsPipeline(m_device, &pci);
			if (!p) log(std::string("shadow pipeline failed: ") + SDL_GetError());
			return p;
		};
		m_pipeline_shadow = make_pipeline(m_vs_shadow, m_fs_shadow, 32, false);
		m_pipeline_shadow_52 = make_pipeline(m_vs_shadow_skinned_layout, m_fs_shadow, 52, true);
		if (!m_pipeline_shadow) { destroy_shadow_resources(); return false; }
		if (m_vs_shadow_cut && m_fs_shadow_cut) m_pipeline_shadow_cut = make_pipeline(m_vs_shadow_cut, m_fs_shadow_cut, 32, false);
		if (m_vs_shadow_cut_skinned_layout && m_fs_shadow_cut) m_pipeline_shadow_cut_52 = make_pipeline(m_vs_shadow_cut_skinned_layout, m_fs_shadow_cut, 52, true);

		if (!create_shadow_fallback()) { destroy_shadow_resources(); return false; }

		const u32 atlas = 2 * SHADOW_TILE_SIZE;
		m_shadow_atlas = create_depth_atlas(m_device, atlas, atlas, m_depth_format);
		m_csm_atlas = create_depth_atlas(m_device, atlas, atlas, m_depth_format);
		const u32 point_rows = (MAX_SHADOW_POINTS * 6 + POINT_ATLAS_COLS - 1) / POINT_ATLAS_COLS;
		m_point_atlas = create_depth_atlas(m_device, POINT_ATLAS_COLS * POINT_SHADOW_TILE, point_rows * POINT_SHADOW_TILE, m_depth_format);
		if (!m_shadow_atlas || !m_csm_atlas || !m_point_atlas) { log(std::string("shadow atlas creation failed: ") + SDL_GetError()); destroy_shadow_resources(); return false; }

		SDL_GPUBufferCreateInfo bi{};
		bi.usage = SDL_GPU_BUFFERUSAGE_VERTEX;
		bi.size = MAX_SHADOW_INSTANCES * 64;
		m_shadow_instance_buffer = SDL_CreateGPUBuffer(m_device, &bi);
		SDL_GPUTransferBufferCreateInfo tbi{};
		tbi.usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD;
		tbi.size = MAX_SHADOW_INSTANCES * 64;
		m_shadow_instance_transfer = SDL_CreateGPUTransferBuffer(m_device, &tbi);
		if (!m_shadow_instance_buffer || !m_shadow_instance_transfer) { destroy_shadow_resources(); return false; }

		m_shadows_ready = true;
		log("shadow maps ready (spot 2x2 / cascades 2x2 / point 4x3 tiles)");
		return true;
	}

	void SdlGpuRenderer::destroy_shadow_resources()
	{
		if (!m_device) return;
		auto rel_tex = [&](SDL_GPUTexture*& t) { if (t) { SDL_ReleaseGPUTexture(m_device, t); t = nullptr; } };
		rel_tex(m_shadow_atlas); rel_tex(m_csm_atlas); rel_tex(m_point_atlas);
		if (m_pipeline_shadow) { SDL_ReleaseGPUGraphicsPipeline(m_device, m_pipeline_shadow); m_pipeline_shadow = nullptr; }
		if (m_pipeline_shadow_52) { SDL_ReleaseGPUGraphicsPipeline(m_device, m_pipeline_shadow_52); m_pipeline_shadow_52 = nullptr; }
		if (m_pipeline_shadow_cut) { SDL_ReleaseGPUGraphicsPipeline(m_device, m_pipeline_shadow_cut); m_pipeline_shadow_cut = nullptr; }
		if (m_pipeline_shadow_cut_52) { SDL_ReleaseGPUGraphicsPipeline(m_device, m_pipeline_shadow_cut_52); m_pipeline_shadow_cut_52 = nullptr; }
		auto rel_sh = [&](SDL_GPUShader*& s) { if (s) { SDL_ReleaseGPUShader(m_device, s); s = nullptr; } };
		rel_sh(m_vs_shadow); rel_sh(m_vs_shadow_skinned_layout); rel_sh(m_fs_shadow);
		rel_sh(m_vs_shadow_cut); rel_sh(m_vs_shadow_cut_skinned_layout); rel_sh(m_fs_shadow_cut);
		if (m_shadow_instance_buffer) { SDL_ReleaseGPUBuffer(m_device, m_shadow_instance_buffer); m_shadow_instance_buffer = nullptr; }
		if (m_shadow_instance_transfer) { SDL_ReleaseGPUTransferBuffer(m_device, m_shadow_instance_transfer); m_shadow_instance_transfer = nullptr; }
		m_shadows_ready = false;
		m_shadow_spot_count = m_csm_count = m_shadow_point_count = 0;
	}

	// CPU side: select the shadowed lights, build their view-projections, pack the casters per tile and fill the
	// light buffer tails. Runs after prepare_scene (same submit order) and before the light buffer is pushed.
	void SdlGpuRenderer::prepare_shadow_pass(const FrameView& view)
	{
		using namespace DirectX;
		(void)view;
		m_shadow_spot_count = 0; m_csm_count = 0; m_shadow_point_count = 0;
		m_shadow_segs.clear(); m_shadow_tiles_spot.clear(); m_shadow_tiles_csm.clear(); m_shadow_tiles_point.clear();
		m_shadow_staging.clear();
		XMFLOAT4X4 ident; XMStoreFloat4x4(&ident, XMMatrixIdentity());
		auto& L = m_light_data;
		for (auto& m : L.shadow_vp) m = ident;
		for (auto& m : L.cascade_vp) m = ident;
		for (auto& m : L.point_face_vp) m = ident;
		L.point_shadows[0] = L.point_shadows[1] = { -1.0f, 0.0f, 0.0f, 0.0f };
		L.dir_shadow_params = { m_dir_shadow_strength, m_dir_shadow_bias, 0.0f, m_dir_shadow_softness };
		L.cascade_splits = { 0.0f, 0.0f, 0.0f, m_dir_shadow_distance };
		fill_sky_light(L);
		for (u32 i = 0; i < MAX_SPOT_LIGHTS; ++i) { L.spot_lights[i].shadow_slot = -1.0f; L.spot_lights[i].shadow_strength = 0.0f; L.spot_lights[i].shadow_bias = 0.0f; }
		if (!m_shadows_ready) return;

		// ---- spot tiles ----
		const bool no_spot = platform::env_flag("VORTEX_NO_SPOT_SHADOWS");
		for (size_t i = 0; i < m_spot_lights.size() && i < MAX_SPOT_LIGHTS && m_shadow_spot_count < MAX_SHADOW_SPOTS && !no_spot; ++i)
		{
			const SpotLightData& s = m_spot_lights[i];
			if (!s.cast_shadows || s.shadow_strength <= 0.0f) continue;
			XMFLOAT4X4 vp;
			if (!build_spot_vp(s, vp)) continue;
			ShadowSpot& slot = m_shadow_spots[m_shadow_spot_count];
			slot.spot_index = (int)i; slot.vp = vp;
			L.shadow_vp[m_shadow_spot_count] = vp;
			L.spot_lights[i].shadow_slot = (float)m_shadow_spot_count;
			L.spot_lights[i].shadow_strength = s.shadow_strength < 0.0f ? 0.0f : (s.shadow_strength > 1.0f ? 1.0f : s.shadow_strength);
			L.spot_lights[i].shadow_bias = s.shadow_bias;
			m_shadow_spot_count++;
		}

		// ---- directional cascades ----
		if (!platform::env_flag("VORTEX_NO_DIR_SHADOWS") && m_dir_cast_shadows && m_dir_shadow_strength > 0.0f && m_directional_intensity > 0.001f)
		{
			XMVECTOR lightDir = XMLoadFloat3(&m_light_direction);
			if (XMVectorGetX(XMVector3LengthSq(lightDir)) > 1e-6f)
			{
				lightDir = XMVector3Normalize(lightDir);
				XMVECTOR lightUp = fabsf(XMVectorGetY(lightDir)) > 0.99f ? XMVectorSet(0, 0, 1, 0) : XMVectorSet(0, 1, 0, 0);
				XMMATRIX lightView = XMMatrixLookToLH(XMVectorZero(), lightDir, lightUp);
				const float nearZ = 0.1f, farZ = m_dir_shadow_distance, lambda = 0.6f;
				float splitNear = nearZ;
				XMVECTOR eye = XMLoadFloat3(&m_camera_position);
				XMVECTOR fwd = XMVectorSubtract(XMLoadFloat3(&m_camera_target), eye);
				if (XMVectorGetX(XMVector3LengthSq(fwd)) > 1e-6f)
				{
					fwd = XMVector3Normalize(fwd);
					XMVECTOR camUp = XMLoadFloat3(&m_camera_up);
					XMVECTOR right = XMVector3Normalize(XMVector3Cross(camUp, fwd));
					XMVECTOR upv = XMVector3Cross(fwd, right);
					const float aspect = m_aspect_ratio > 0.01f ? m_aspect_ratio : 1.0f;
					const float tanHalfY = tanf(XMConvertToRadians(m_fov_degrees) * 0.5f);
					const float tanHalfX = tanHalfY * aspect;
					for (u32 c = 0; c < CSM_CASCADES; ++c)
					{
						const float f = (float)(c + 1) / (float)CSM_CASCADES;
						const float logSplit = nearZ * powf(farZ / nearZ, f);
						const float uniSplit = nearZ + (farZ - nearZ) * f;
						const float splitFar = uniSplit + lambda * (logSplit - uniSplit);
						XMVECTOR corners[8]; int k = 0;
						for (int d = 0; d < 2; ++d)
						{
							const float dist = d == 0 ? splitNear : splitFar;
							XMVECTOR center = XMVectorAdd(eye, XMVectorScale(fwd, dist));
							XMVECTOR ex = XMVectorScale(right, dist * tanHalfX);
							XMVECTOR ey = XMVectorScale(upv, dist * tanHalfY);
							corners[k++] = XMVectorAdd(XMVectorAdd(center, ex), ey);
							corners[k++] = XMVectorSubtract(XMVectorAdd(center, ey), ex);
							corners[k++] = XMVectorAdd(XMVectorSubtract(center, ey), ex);
							corners[k++] = XMVectorSubtract(XMVectorSubtract(center, ex), ey);
						}
						XMVECTOR centroid = XMVectorZero();
						for (int i = 0; i < 8; ++i) centroid = XMVectorAdd(centroid, corners[i]);
						centroid = XMVectorScale(centroid, 1.0f / 8.0f);
						float radius = 0.0f;
						for (int i = 0; i < 8; ++i) radius = (std::max)(radius, XMVectorGetX(XMVector3Length(XMVectorSubtract(corners[i], centroid))));
						radius = ceilf(radius * 16.0f) / 16.0f;
						XMVECTOR cLS = XMVector3TransformCoord(centroid, lightView);
						const float worldPerTexel = (2.0f * radius) / (float)SHADOW_TILE_SIZE;
						float cx = floorf(XMVectorGetX(cLS) / worldPerTexel) * worldPerTexel;
						float cy = floorf(XMVectorGetY(cLS) / worldPerTexel) * worldPerTexel;
						const float cz = XMVectorGetZ(cLS);
						XMMATRIX proj = XMMatrixOrthographicOffCenterLH(cx - radius, cx + radius, cy - radius, cy + radius, cz - radius - m_dir_shadow_distance, cz + radius);
						XMStoreFloat4x4(&m_csm_vp[c], lightView * proj);
						m_csm_splits[c] = splitFar;
						m_csm_radius[c] = radius;
						splitNear = splitFar;
					}
					m_csm_count = CSM_CASCADES;
					for (u32 c = 0; c < CSM_CASCADES; ++c) L.cascade_vp[c] = m_csm_vp[c];
					L.cascade_splits = { m_csm_splits[0], m_csm_splits[1], m_csm_splits[2], m_dir_shadow_distance };
					L.dir_shadow_params = { m_dir_shadow_strength, m_dir_shadow_bias, (float)m_csm_count, m_dir_shadow_softness };
				}
			}
		}

		// ---- point cube faces ----
		if (!platform::env_flag("VORTEX_NO_POINT_SHADOWS"))
		{
			static const XMVECTORF32 kDirs[6] = { { { { 1, 0, 0, 0 } } }, { { { -1, 0, 0, 0 } } }, { { { 0, 1, 0, 0 } } }, { { { 0, -1, 0, 0 } } }, { { { 0, 0, 1, 0 } } }, { { { 0, 0, -1, 0 } } } };
			static const XMVECTORF32 kUps[6] = { { { { 0, 1, 0, 0 } } }, { { { 0, 1, 0, 0 } } }, { { { 0, 0, -1, 0 } } }, { { { 0, 0, 1, 0 } } }, { { { 0, 1, 0, 0 } } }, { { { 0, 1, 0, 0 } } } };
			for (size_t i = 0; i < m_point_lights.size() && i < MAX_POINT_LIGHTS && m_shadow_point_count < MAX_SHADOW_POINTS; ++i)
			{
				const PointLightData& pl = m_point_lights[i];
				if (!pl.cast_shadows || pl.shadow_strength <= 0.0f) continue;
				ShadowPoint& spn = m_shadow_points[m_shadow_point_count];
				spn.light_index = (int)i;
				spn.strength = pl.shadow_strength < 0.0f ? 0.0f : (pl.shadow_strength > 1.0f ? 1.0f : pl.shadow_strength);
				spn.bias = pl.shadow_bias > 0.0f ? pl.shadow_bias : 0.0015f;
				XMVECTOR pos = XMLoadFloat3(&pl.position);
				const float farZ = pl.range > 0.1f ? pl.range : 0.1f;
				XMMATRIX proj = XMMatrixPerspectiveFovLH(XM_PIDIV2, 1.0f, 0.05f, farZ);
				for (u32 f = 0; f < 6; ++f)
				{
					XMMATRIX viewm = XMMatrixLookToLH(pos, kDirs[f], kUps[f]);
					XMStoreFloat4x4(&spn.face_vp[f], viewm * proj);
					L.point_face_vp[m_shadow_point_count * 6 + f] = spn.face_vp[f];
				}
				L.point_shadows[m_shadow_point_count] = { (float)spn.light_index, spn.strength, spn.bias, 0.0f };
				m_shadow_point_count++;
			}
		}

		if (m_shadow_spot_count == 0 && m_csm_count == 0 && m_shadow_point_count == 0) return;

		// ---- gather the casters ONCE per frame (#363) ----
		// The per-tile pack used to scan the whole render queue — a map lookup, a matrix transform and a sphere test per
		// item, for up to 19 tiles (4 spots, 3 cascades, 2 x 6 point faces): 19 x N per frame, single-threaded. Now the
		// filter and the world-space bounding sphere are computed once; each tile runs the sphere test only.
		auto& reg = ResourceRegistry::instance();
		if (reg.mesh_generation() != m_shadow_bounds_generation) { m_shadow_bounds.clear(); m_shadow_bounds_generation = reg.mesh_generation(); }
		auto& all = m_shadow_casters; all.clear();
		for (const auto& item : m_render_queue)
		{
			if (item.bone_offset != NO_BONES || item.layer != 0) continue;
			Material* cmat = reg.get_material(item.material_id);
			if (cmat && (cmat->blend_mode() == 1 || cmat->blend_mode() == 2)) continue;
			Mesh* mp = reg.get_mesh(item.mesh_id);
			if (!mp || !mp->is_valid()) continue;
			XMFLOAT4 bd;
			auto bit = m_shadow_bounds.find(item.mesh_id);
			if (bit == m_shadow_bounds.end())
			{
				float mnx = 0, mny = 0, mnz = 0, mxx = 1, mxy = 1, mxz = 1;
				mp->get_min(mnx, mny, mnz); mp->get_max(mxx, mxy, mxz);
				float dx = mxx - mnx, dy = mxy - mny, dz = mxz - mnz;
				bd = XMFLOAT4((mnx + mxx) * 0.5f, (mny + mxy) * 0.5f, (mnz + mxz) * 0.5f, 0.5f * sqrtf(dx * dx + dy * dy + dz * dz));
				m_shadow_bounds.emplace(item.mesh_id, bd);
			}
			else bd = bit->second;
			const XMFLOAT4X4& W = item.world_matrix;
			XMVECTOR wc = XMVector3TransformCoord(XMVectorSet(bd.x, bd.y, bd.z, 1.f), XMLoadFloat4x4(&W));
			float sx = sqrtf(W._11 * W._11 + W._12 * W._12 + W._13 * W._13);
			float sy = sqrtf(W._21 * W._21 + W._22 * W._22 + W._23 * W._23);
			float sz = sqrtf(W._31 * W._31 + W._32 * W._32 + W._33 * W._33);
			float ms = sx > sy ? (sx > sz ? sx : sz) : (sy > sz ? sy : sz);
			// an alpha-tested material casts the shape of its texture (#329) when the cut-out shadow pipeline exists
			const bool cut = cmat && cmat->blend_mode() == 3 && cmat->properties().alpha_cutoff > 0.0f && m_pipeline_shadow_cut;
			all.push_back({ item.mesh_id, &item.world_matrix, mp, cmat, cut, XMVectorGetX(wc), XMVectorGetY(wc), XMVectorGetZ(wc), bd.w * ms + 0.05f });
		}

		// ---- pack casters per tile (own culling per light frustum; the scene pack is keyed to the camera) ----
		u32 vb_used = 0, dropped = 0;
		auto pack = [&](const XMFLOAT4X4& vp, u32 x, u32 y, u32 size, std::vector<ShadowTile>& tiles)
		{
			const ShadowFrustum fr = extract_shadow_frustum(vp);
			auto& casters = m_shadow_tile_casters; casters.clear();
			for (const auto& c : all)
				if (sphere_in_shadow_frustum(fr, c.cx, c.cy, c.cz, c.r)) casters.push_back(c);
			// The tiles share one instance buffer. A tile over budget keeps its LARGEST casters (the shadows one sees from
			// afar) instead of the first in queue order, and the drop is reported below.
			const u32 room = vb_used < MAX_SHADOW_INSTANCES ? MAX_SHADOW_INSTANCES - vb_used : 0;
			if (casters.size() > room)
			{
				std::nth_element(casters.begin(), casters.begin() + room, casters.end(), [](const ShadowCaster& a, const ShadowCaster& b) { return a.r > b.r; });
				dropped += (u32)(casters.size() - room);
				casters.resize(room);
			}
			ShadowTile tile{ x, y, size, vp, (u32)m_shadow_segs.size(), (u32)m_shadow_segs.size() };
			if (!casters.empty())
			{
				// solid casters first, grouped by mesh; cut-out casters after them, grouped by mesh AND material (#329)
				std::sort(casters.begin(), casters.end(), [](const ShadowCaster& a, const ShadowCaster& b)
				{
					if (a.cut != b.cut) return !a.cut;
					if (a.mesh != b.mesh) return a.mesh < b.mesh;
					return a.cut && a.mat < b.mat;
				});
				m_shadow_staging.resize((size_t)(vb_used + casters.size()) * 16);
				for (size_t i = 0; i < casters.size(); ++i)
					memcpy(m_shadow_staging.data() + (size_t)(vb_used + i) * 16, casters[i].world, 64);
				size_t i = 0;
				while (i < casters.size())
				{
					size_t j = i + 1;
					while (j < casters.size() && casters[j].mesh == casters[i].mesh && casters[j].cut == casters[i].cut
						&& (!casters[i].cut || casters[j].mat == casters[i].mat)) ++j;
					m_shadow_segs.push_back({ casters[i].meshp, vb_used + (u32)i, (u32)(j - i), casters[i].cut ? casters[i].mat : nullptr });
					i = j;
				}
				vb_used += (u32)casters.size();
				tile.seg_end = (u32)m_shadow_segs.size();
			}
			tiles.push_back(tile);
		};

		// cascades first: the sun's shadows must never lose casters to the spot tiles packed before them (#363)
		for (u32 c = 0; c < m_csm_count; ++c)
			pack(m_csm_vp[c], (c & 1) * SHADOW_TILE_SIZE, ((c >> 1) & 1) * SHADOW_TILE_SIZE, SHADOW_TILE_SIZE, m_shadow_tiles_csm);
		for (u32 t = 0; t < m_shadow_spot_count; ++t)
			pack(m_shadow_spots[t].vp, (t & 1) * SHADOW_TILE_SIZE, ((t >> 1) & 1) * SHADOW_TILE_SIZE, SHADOW_TILE_SIZE, m_shadow_tiles_spot);
		for (u32 p = 0; p < m_shadow_point_count; ++p)
			for (u32 f = 0; f < 6; ++f)
			{
				const u32 tile = p * 6 + f;
				pack(m_shadow_points[p].face_vp[f], (tile % POINT_ATLAS_COLS) * POINT_SHADOW_TILE, (tile / POINT_ATLAS_COLS) * POINT_SHADOW_TILE, POINT_SHADOW_TILE, m_shadow_tiles_point);
			}
		if (dropped > 0)
		{
			const auto now = std::chrono::steady_clock::now();
			if (now - m_shadow_drop_logged > std::chrono::seconds(5))
			{
				m_shadow_drop_logged = now;
				log("shadows: " + std::to_string(dropped) + " caster(s) dropped this frame — the shared shadow instance budget of "
					+ std::to_string(MAX_SHADOW_INSTANCES) + " is full (cascades packed first, largest casters kept) (#363)");
			}
		}
	}

	// GPU side: upload the packed casters, then one depth-only render pass per atlas with a viewport/scissor
	// per tile. Runs before the scene pass of the same command buffer, so the atlases are ready for PSMain.
	void SdlGpuRenderer::record_shadow_passes(SDL_GPUCommandBuffer* cmd)
	{
		if (!m_shadows_ready) return;
		if (m_shadow_tiles_spot.empty() && m_shadow_tiles_csm.empty() && m_shadow_tiles_point.empty()) return;

		if (!m_shadow_staging.empty())
		{
			const u32 bytes = (u32)(m_shadow_staging.size() * sizeof(float));
			void* mapped = SDL_MapGPUTransferBuffer(m_device, m_shadow_instance_transfer, true);
			if (mapped)
			{
				memcpy(mapped, m_shadow_staging.data(), bytes);
				SDL_UnmapGPUTransferBuffer(m_device, m_shadow_instance_transfer);
				SDL_GPUCopyPass* copy = SDL_BeginGPUCopyPass(cmd);
				SDL_GPUTransferBufferLocation src{ m_shadow_instance_transfer, 0 };
				SDL_GPUBufferRegion dst{ m_shadow_instance_buffer, 0, bytes };
				SDL_UploadToGPUBuffer(copy, &src, &dst, true);
				SDL_EndGPUCopyPass(copy);
			}
		}

		auto render_atlas = [&](SDL_GPUTexture* atlas, const std::vector<ShadowTile>& tiles)
		{
			if (!atlas || tiles.empty()) return;
			SDL_GPUDepthStencilTargetInfo depth{};
			depth.texture = atlas;
			depth.clear_depth = 1.0f;
			depth.load_op = SDL_GPU_LOADOP_CLEAR;
			depth.store_op = SDL_GPU_STOREOP_STORE;
			depth.stencil_load_op = SDL_GPU_LOADOP_DONT_CARE;
			depth.stencil_store_op = SDL_GPU_STOREOP_DONT_CARE;
			depth.cycle = false;
			SDL_GPURenderPass* pass = SDL_BeginGPURenderPass(cmd, nullptr, 0, &depth);
			if (!pass) return;
			for (const ShadowTile& tile : tiles)
			{
				if (tile.seg_begin == tile.seg_end) continue;   // empty tile stays cleared -> fully lit
				SDL_GPUViewport vp{ (float)tile.x, (float)tile.y, (float)tile.size, (float)tile.size, 0.0f, 1.0f };
				SDL_SetGPUViewport(pass, &vp);
				SDL_Rect sc{ (int)tile.x, (int)tile.y, (int)tile.size, (int)tile.size };
				SDL_SetGPUScissor(pass, &sc);
				PerFrameConstants fc = m_frame_constants;
				fc.view_projection = tile.vp;
				SDL_PushGPUVertexUniformData(cmd, 0, &fc, sizeof(fc));
				SDL_PushGPUFragmentUniformData(cmd, 0, &fc, sizeof(fc));
				SDL_GPUGraphicsPipeline* bound = nullptr;
				for (u32 s = tile.seg_begin; s < tile.seg_end; ++s)
				{
					const ShadowDrawSeg& seg = m_shadow_segs[s];
					Mesh* mesh = seg.mesh;
					if (!mesh || !mesh->is_valid()) continue;
					const bool cut = seg.cut_mat != nullptr;
					SDL_GPUGraphicsPipeline* want = mesh->vertex_stride() == 52
						? (cut ? m_pipeline_shadow_cut_52 : m_pipeline_shadow_52)
						: (mesh->vertex_stride() == 32 ? (cut ? m_pipeline_shadow_cut : m_pipeline_shadow) : nullptr);
					if (!want) continue;
					if (want != bound) { SDL_BindGPUGraphicsPipeline(pass, want); bound = want; }
					if (cut)
					{
						// the material's albedo + cutoff (#329): PerObject at fragment uniform slot 1, albedo at sampler 0
						PerObjectConstants obj{};
						const auto& props = seg.cut_mat->properties();
						obj.base_color = props.base_color; obj.uv_tiling = props.uv_tiling; obj.alpha_cutoff = props.alpha_cutoff;
						Texture* at = seg.cut_mat->albedo_texture();
						Texture* white = ResourceRegistry::instance().white_texture();
						const bool has = at && at->is_valid();
						obj.has_albedo_texture = has ? 1u : 0u;
						SDL_GPUTextureSamplerBinding tb{ has ? at->texture() : (white ? white->texture() : nullptr), m_sampler_linear_wrap };
						SDL_PushGPUFragmentUniformData(cmd, 1, &obj, sizeof(obj));
						if (tb.texture) SDL_BindGPUFragmentSamplers(pass, 0, &tb, 1);
					}
					SDL_GPUBufferBinding vbs[2] = { { mesh->vertex_buffer(), 0 }, { m_shadow_instance_buffer, seg.instance_base * 64 } };
					SDL_BindGPUVertexBuffers(pass, 0, vbs, 2);
					if (mesh->has_indices())
					{
						SDL_GPUBufferBinding ib{ mesh->index_buffer(), 0 };
						SDL_BindGPUIndexBuffer(pass, &ib, SDL_GPU_INDEXELEMENTSIZE_32BIT);
						SDL_DrawGPUIndexedPrimitives(pass, mesh->index_count(), seg.instance_count, 0, 0, 0);
					}
					else SDL_DrawGPUPrimitives(pass, mesh->vertex_count(), seg.instance_count, 0, 0);
					++m_draw_call_count; ++m_shadow_draw_count;
				}
			}
			SDL_EndGPURenderPass(pass);
		};
		render_atlas(m_shadow_atlas, m_shadow_tiles_spot);
		render_atlas(m_csm_atlas, m_shadow_tiles_csm);
		render_atlas(m_point_atlas, m_shadow_tiles_point);
	}
}
