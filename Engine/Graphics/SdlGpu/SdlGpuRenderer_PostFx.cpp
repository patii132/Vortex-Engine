#include "SdlGpuRenderer.h"
#include "SdlGpuResources.h"
#include "../../Common/Platform.h"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <string>
#include <unordered_map>

// Screen-space effects on the SDL GPU (Metal) backend, mirroring the DX12 backend:
//   SSAO  — half-res depth prepass of the rigid opaque geometry (camera VP, depth-only shadow pipelines),
//           AO pass, 4-tap blur; the blurred texture reaches PSMain at fragment slot 9 and darkens the ambient
//           term only (ssao.metal / standard.metal).
//   Bloom — prefilter + 13-tap downsample chain + additive tent upsample (bloom.metal); mip 0 is composited
//           by the post-FX uber pass (postfx.metal flag 32).

namespace vortex::graphics::sdlgpu
{
	namespace
	{
		void log(const std::string& msg) { platform::debug_output(("[sdlgpu] " + msg + "\n").c_str()); }

		struct SsaoCB
		{
			DirectX::XMFLOAT4X4 inv_proj;
			float texel[2];
			float radius, intensity, bias, proj_scale;
			float pad[2];
			// screen-space GI (#257 step 1)
			DirectX::XMFLOAT4X4 proj, view_to_prev_clip;
			float gi_strength, scene_exposure, prev_valid, gi_radius;
			// the bounce from the sun's shadow map (#257 step 2) — appended @240
			DirectX::XMFLOAT4X4 view_to_rsm, rsm_to_view;
			float rsm_tile[4], rsm_sun_view[4], rsm_flux[4];
		};
		static_assert(sizeof(SsaoCB) == 416, "SsaoCB must byte-match ssao.metal");

		struct BloomCB
		{
			float src_texel[2];
			float threshold, knee, sample_scale, weight;
			float pad[2];
		};
		static_assert(sizeof(BloomCB) == 32, "BloomCB must byte-match bloom.metal");

		struct Frustum6 { float p[6][4]; };
		Frustum6 extract_frustum6(const DirectX::XMFLOAT4X4& m)
		{
			Frustum6 f = { {
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
		bool sphere_in_frustum6(const Frustum6& f, float cx, float cy, float cz, float r)
		{
			for (int i = 0; i < 6; ++i)
				if (f.p[i][0] * cx + f.p[i][1] * cy + f.p[i][2] * cz + f.p[i][3] < -r) return false;
			return true;
		}

		SDL_GPUTexture* make_texture(SDL_GPUDevice* dev, u32 w, u32 h, SDL_GPUTextureFormat fmt, SDL_GPUTextureUsageFlags usage)
		{
			SDL_GPUTextureCreateInfo ti{};
			ti.type = SDL_GPU_TEXTURETYPE_2D; ti.format = fmt; ti.usage = usage;
			ti.width = w; ti.height = h; ti.layer_count_or_depth = 1; ti.num_levels = 1; ti.sample_count = SDL_GPU_SAMPLECOUNT_1;
			return SDL_CreateGPUTexture(dev, &ti);
		}
	}

	SDL_GPUGraphicsPipeline* SdlGpuRenderer::create_post_pipeline(SDL_GPUShader* vs, SDL_GPUShader* fs, SDL_GPUTextureFormat format, bool additive)
	{
		SDL_GPUColorTargetDescription color{};
		color.format = format;
		if (additive)
		{
			color.blend_state.enable_blend = true;
			color.blend_state.src_color_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
			color.blend_state.dst_color_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
			color.blend_state.color_blend_op = SDL_GPU_BLENDOP_ADD;
			color.blend_state.src_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
			color.blend_state.dst_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ZERO;
			color.blend_state.alpha_blend_op = SDL_GPU_BLENDOP_ADD;
		}
		SDL_GPUGraphicsPipelineCreateInfo pci{};
		pci.vertex_shader = vs; pci.fragment_shader = fs;
		pci.primitive_type = SDL_GPU_PRIMITIVETYPE_TRIANGLELIST;
		pci.rasterizer_state.fill_mode = SDL_GPU_FILLMODE_FILL;
		pci.rasterizer_state.cull_mode = SDL_GPU_CULLMODE_NONE;
		pci.rasterizer_state.front_face = SDL_GPU_FRONTFACE_CLOCKWISE;
		pci.rasterizer_state.enable_depth_clip = true;
		pci.multisample_state.sample_count = SDL_GPU_SAMPLECOUNT_1;
		pci.target_info.color_target_descriptions = &color;
		pci.target_info.num_color_targets = 1;
		pci.target_info.has_depth_stencil_target = false;
		SDL_GPUGraphicsPipeline* p = SDL_CreateGPUGraphicsPipeline(m_device, &pci);
		if (!p) log(std::string("post pipeline failed: ") + SDL_GetError());
		return p;
	}

	bool SdlGpuRenderer::ensure_postfx_resources()
	{
		if (m_post_ready) return true;
		if (!m_device || !m_pipeline_shadow) return false;
		m_vs_ssao = create_shader("ssao", "SsaoVS", SDL_GPU_SHADERSTAGE_VERTEX, 0, 0, 0);
		m_fs_ssao = create_shader("ssao", "SsaoPS", SDL_GPU_SHADERSTAGE_FRAGMENT, 3, 0, 1);   // depth + the previous frame + the sun's shadow atlas (the bounce)   // depth + the previous frame's colour (GI)
		m_fs_ssao_blur = create_shader("ssao", "SsaoBlurPS", SDL_GPU_SHADERSTAGE_FRAGMENT, 1, 0, 1);
		m_vs_bloom = create_shader("bloom", "BloomVS", SDL_GPU_SHADERSTAGE_VERTEX, 0, 0, 0);
		m_fs_bloom_prefilter = create_shader("bloom", "BloomPrefilterPS", SDL_GPU_SHADERSTAGE_FRAGMENT, 1, 0, 1);
		m_fs_bloom_down = create_shader("bloom", "BloomDownsamplePS", SDL_GPU_SHADERSTAGE_FRAGMENT, 1, 0, 1);
		m_fs_bloom_up = create_shader("bloom", "BloomUpsamplePS", SDL_GPU_SHADERSTAGE_FRAGMENT, 1, 0, 1);
		if (!m_vs_ssao || !m_fs_ssao || !m_fs_ssao_blur || !m_vs_bloom || !m_fs_bloom_prefilter || !m_fs_bloom_down || !m_fs_bloom_up) { destroy_postfx_resources(); return false; }
		m_pipeline_ssao = create_post_pipeline(m_vs_ssao, m_fs_ssao, SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, false);   // rgb = GI, a = AO
		m_pipeline_ssao_blur = create_post_pipeline(m_vs_ssao, m_fs_ssao_blur, SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, false);
		m_pipeline_bloom_prefilter = create_post_pipeline(m_vs_bloom, m_fs_bloom_prefilter, m_scene_format, false);
		m_pipeline_bloom_down = create_post_pipeline(m_vs_bloom, m_fs_bloom_down, m_scene_format, false);
		m_pipeline_bloom_up = create_post_pipeline(m_vs_bloom, m_fs_bloom_up, m_scene_format, true);
		if (!m_pipeline_ssao || !m_pipeline_ssao_blur || !m_pipeline_bloom_prefilter || !m_pipeline_bloom_down || !m_pipeline_bloom_up) { destroy_postfx_resources(); return false; }
		SDL_GPUSamplerCreateInfo sci{};
		sci.min_filter = SDL_GPU_FILTER_NEAREST; sci.mag_filter = SDL_GPU_FILTER_NEAREST; sci.mipmap_mode = SDL_GPU_SAMPLERMIPMAPMODE_NEAREST;
		sci.address_mode_u = sci.address_mode_v = sci.address_mode_w = SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE;
		m_sampler_point_clamp = SDL_CreateGPUSampler(m_device, &sci);
		SDL_GPUBufferCreateInfo bci{}; bci.usage = SDL_GPU_BUFFERUSAGE_VERTEX; bci.size = SSAO_MAX_INSTANCES * 64;
		m_ssao_instance_buffer = SDL_CreateGPUBuffer(m_device, &bci);
		SDL_GPUTransferBufferCreateInfo tci{}; tci.usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD; tci.size = SSAO_MAX_INSTANCES * 64;
		m_ssao_instance_transfer = SDL_CreateGPUTransferBuffer(m_device, &tci);
		if (!m_sampler_point_clamp || !m_ssao_instance_buffer || !m_ssao_instance_transfer) { destroy_postfx_resources(); return false; }
		m_post_ready = true;
		log("post effects ready (SSAO + bloom chain)");
		return true;
	}

	void SdlGpuRenderer::destroy_postfx_resources()
	{
		if (!m_device) return;
		for (auto& s : m_ssao_sets) { if (s.depth) SDL_ReleaseGPUTexture(m_device, s.depth); if (s.ao) SDL_ReleaseGPUTexture(m_device, s.ao); if (s.blur) SDL_ReleaseGPUTexture(m_device, s.blur); }
		m_ssao_sets.clear();
		for (auto& c : m_bloom_chains) for (auto& m : c.mips) if (m.tex) SDL_ReleaseGPUTexture(m_device, m.tex);
		m_bloom_chains.clear();
		auto rel_p = [&](SDL_GPUGraphicsPipeline*& p) { if (p) { SDL_ReleaseGPUGraphicsPipeline(m_device, p); p = nullptr; } };
		rel_p(m_pipeline_ssao); rel_p(m_pipeline_ssao_blur); rel_p(m_pipeline_bloom_prefilter); rel_p(m_pipeline_bloom_down); rel_p(m_pipeline_bloom_up);
		auto rel_s = [&](SDL_GPUShader*& s) { if (s) { SDL_ReleaseGPUShader(m_device, s); s = nullptr; } };
		rel_s(m_vs_ssao); rel_s(m_fs_ssao); rel_s(m_fs_ssao_blur); rel_s(m_vs_bloom); rel_s(m_fs_bloom_prefilter); rel_s(m_fs_bloom_down); rel_s(m_fs_bloom_up);
		if (m_sampler_point_clamp) { SDL_ReleaseGPUSampler(m_device, m_sampler_point_clamp); m_sampler_point_clamp = nullptr; }
		if (m_ssao_instance_buffer) { SDL_ReleaseGPUBuffer(m_device, m_ssao_instance_buffer); m_ssao_instance_buffer = nullptr; }
		if (m_ssao_instance_transfer) { SDL_ReleaseGPUTransferBuffer(m_device, m_ssao_instance_transfer); m_ssao_instance_transfer = nullptr; }
		m_ssao_current = nullptr; m_bloom_result = nullptr;
		m_post_ready = false;
	}

	SdlGpuRenderer::SsaoSet* SdlGpuRenderer::acquire_ssao_set(u32 w, u32 h)
	{
		for (auto& s : m_ssao_sets) if (s.w == w && s.h == h) return &s;
		if (m_ssao_sets.size() >= 3)
		{
			wait_idle();
			auto& s = m_ssao_sets.front();
			if (s.depth) SDL_ReleaseGPUTexture(m_device, s.depth); if (s.ao) SDL_ReleaseGPUTexture(m_device, s.ao); if (s.blur) SDL_ReleaseGPUTexture(m_device, s.blur);
			m_ssao_sets.erase(m_ssao_sets.begin());
		}
		SsaoSet s{};
		s.w = w; s.h = h;
		s.depth = make_texture(m_device, w, h, m_depth_format, SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER);
		s.ao = make_texture(m_device, w, h, SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER);   // rgb = GI, a = AO
		s.blur = make_texture(m_device, w, h, SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER);
		if (!s.depth || !s.ao || !s.blur) { log(std::string("SSAO targets failed: ") + SDL_GetError()); if (s.depth) SDL_ReleaseGPUTexture(m_device, s.depth); if (s.ao) SDL_ReleaseGPUTexture(m_device, s.ao); if (s.blur) SDL_ReleaseGPUTexture(m_device, s.blur); return nullptr; }
		m_ssao_sets.push_back(s);
		return &m_ssao_sets.back();
	}

	// Records the SSAO chain for one view into the command buffer (before the scene pass). Sets m_ssao_current.
	void SdlGpuRenderer::record_ssao(SDL_GPUCommandBuffer* cmd, const FrameView& view, u32 target_w, u32 target_h)
	{
		using namespace DirectX;
		m_ssao_current = nullptr;
		if (!m_ssao_enabled || !m_post_ready || target_w < 32 || target_h < 32) return;
		if (platform::env_flag("VORTEX_NO_SSAO")) return;
		u32 hw = (std::max)(16u, target_w / 2), hh = (std::max)(16u, target_h / 2);
		SsaoSet* set = acquire_ssao_set(hw, hh);
		if (!set) return;

		// ---- pack rigid opaque casters (camera frustum) ----
		auto& reg = ResourceRegistry::instance();
		struct Caster { id::id_type mesh; const XMFLOAT4X4* world; Mesh* meshp; };
		std::vector<Caster> casters;
		std::unordered_map<id::id_type, XMFLOAT4> bounds;
		const Frustum6 fr = extract_frustum6(view.view_projection);
		for (const auto& item : m_render_queue)
		{
			if (item.bone_offset != NO_BONES || item.layer != 0) continue;
			Material* cmat = reg.get_material(item.material_id);
			if (cmat && (cmat->blend_mode() == 1 || cmat->blend_mode() == 2)) continue;
			Mesh* mp = reg.get_mesh(item.mesh_id);
			if (!mp || !mp->is_valid()) continue;
			XMFLOAT4 bd;
			auto bit = bounds.find(item.mesh_id);
			if (bit == bounds.end())
			{
				float mnx = 0, mny = 0, mnz = 0, mxx = 1, mxy = 1, mxz = 1;
				mp->get_min(mnx, mny, mnz); mp->get_max(mxx, mxy, mxz);
				float dx = mxx - mnx, dy = mxy - mny, dz = mxz - mnz;
				bd = XMFLOAT4((mnx + mxx) * 0.5f, (mny + mxy) * 0.5f, (mnz + mxz) * 0.5f, 0.5f * sqrtf(dx * dx + dy * dy + dz * dz));
				bounds.emplace(item.mesh_id, bd);
			}
			else bd = bit->second;
			const XMFLOAT4X4& W = item.world_matrix;
			XMVECTOR wc = XMVector3TransformCoord(XMVectorSet(bd.x, bd.y, bd.z, 1.f), XMLoadFloat4x4(&W));
			float sx = sqrtf(W._11 * W._11 + W._12 * W._12 + W._13 * W._13);
			float sy = sqrtf(W._21 * W._21 + W._22 * W._22 + W._23 * W._23);
			float sz = sqrtf(W._31 * W._31 + W._32 * W._32 + W._33 * W._33);
			float ms = sx > sy ? (sx > sz ? sx : sz) : (sy > sz ? sy : sz);
			if (sphere_in_frustum6(fr, XMVectorGetX(wc), XMVectorGetY(wc), XMVectorGetZ(wc), bd.w * ms + 0.05f))
			{
				casters.push_back({ item.mesh_id, &item.world_matrix, mp });
				if (casters.size() >= SSAO_MAX_INSTANCES) break;
			}
		}
		std::sort(casters.begin(), casters.end(), [](const Caster& a, const Caster& b) { return a.mesh < b.mesh; });
		if (!casters.empty())
		{
			void* mapped = SDL_MapGPUTransferBuffer(m_device, m_ssao_instance_transfer, true);
			if (!mapped) return;
			for (size_t i = 0; i < casters.size(); ++i) memcpy((u8*)mapped + i * 64, casters[i].world, 64);
			SDL_UnmapGPUTransferBuffer(m_device, m_ssao_instance_transfer);
			SDL_GPUCopyPass* copy = SDL_BeginGPUCopyPass(cmd);
			SDL_GPUTransferBufferLocation src{ m_ssao_instance_transfer, 0 };
			SDL_GPUBufferRegion dst{ m_ssao_instance_buffer, 0, (u32)(casters.size() * 64) };
			SDL_UploadToGPUBuffer(copy, &src, &dst, true);
			SDL_EndGPUCopyPass(copy);
		}

		// ---- half-res depth prepass ----
		{
			SDL_GPUDepthStencilTargetInfo depth{};
			depth.texture = set->depth; depth.clear_depth = 1.0f;
			depth.load_op = SDL_GPU_LOADOP_CLEAR; depth.store_op = SDL_GPU_STOREOP_STORE;
			depth.stencil_load_op = SDL_GPU_LOADOP_DONT_CARE; depth.stencil_store_op = SDL_GPU_STOREOP_DONT_CARE;
			SDL_GPURenderPass* pass = SDL_BeginGPURenderPass(cmd, nullptr, 0, &depth);
			if (!pass) return;
			SDL_GPUViewport vp{ 0, 0, (float)hw, (float)hh, 0.0f, 1.0f };
			SDL_SetGPUViewport(pass, &vp);
			SDL_PushGPUVertexUniformData(cmd, 0, &view.frame, sizeof(PerFrameConstants));
			SDL_GPUGraphicsPipeline* bound = nullptr;
			size_t i = 0;
			while (i < casters.size())
			{
				size_t j = i + 1;
				while (j < casters.size() && casters[j].mesh == casters[i].mesh) ++j;
				Mesh* mesh = casters[i].meshp;
				SDL_GPUGraphicsPipeline* want = mesh->vertex_stride() == 52 ? m_pipeline_shadow_52 : (mesh->vertex_stride() == 32 ? m_pipeline_shadow : nullptr);
				if (want)
				{
					if (want != bound) { SDL_BindGPUGraphicsPipeline(pass, want); bound = want; }
					SDL_GPUBufferBinding vbs[2] = { { mesh->vertex_buffer(), 0 }, { m_ssao_instance_buffer, (u32)i * 64 } };
					SDL_BindGPUVertexBuffers(pass, 0, vbs, 2);
					if (mesh->has_indices())
					{
						SDL_GPUBufferBinding ib{ mesh->index_buffer(), 0 };
						SDL_BindGPUIndexBuffer(pass, &ib, SDL_GPU_INDEXELEMENTSIZE_32BIT);
						SDL_DrawGPUIndexedPrimitives(pass, mesh->index_count(), (u32)(j - i), 0, 0, 0);
					}
					else SDL_DrawGPUPrimitives(pass, mesh->vertex_count(), (u32)(j - i), 0, 0);
					++m_draw_call_count; ++m_post_draw_count;
				}
				i = j;
			}
			SDL_EndGPURenderPass(pass);
		}

		// ---- AO + blur ----
		const float aspect = (float)target_w / (float)(target_h ? target_h : 1);
		XMMATRIX proj = XMMatrixPerspectiveFovLH(XMConvertToRadians(m_fov_degrees), aspect, m_near_clip, m_far_clip);
		SsaoCB cb{};
		XMStoreFloat4x4(&cb.inv_proj, XMMatrixInverse(nullptr, proj));
		cb.texel[0] = 1.0f / (float)hw; cb.texel[1] = 1.0f / (float)hh;
		cb.radius = m_ssao_radius; cb.intensity = m_ssao_intensity; cb.bias = 0.015f;
		cb.proj_scale = 0.5f * XMVectorGetY(proj.r[1]) * (float)hh;
		// screen-space GI (#257 step 1): the previous frame's opaque copy at this size is the radiance source
		const bool gi_prev = m_gi_enabled && m_gi_prev_valid && m_scene_color_copy && m_gi_prev_w == target_w && m_gi_prev_h == target_h;
		XMStoreFloat4x4(&cb.proj, proj);
		XMStoreFloat4x4(&cb.view_to_prev_clip, proj * XMLoadFloat4x4(&view.inverse_view_projection) * XMLoadFloat4x4(&m_gi_prev_view_projection));
		cb.gi_strength = m_gi_enabled ? m_gi_strength : 0.0f; cb.scene_exposure = view.frame.scene_exposure > 0.0f ? view.frame.scene_exposure : 0.5f;
		cb.prev_valid = gi_prev ? 1.0f : 0.0f; cb.gi_radius = m_gi_radius;
		// the bounce from the sun's shadow map (#257 step 2): the far cascade (it holds every receiver and its surroundings, at
		// ~7 cm per texel for a 150 m shadow distance) read as a reflective shadow map — the ground's albedo x the sun as the emitters' radiance
		const bool rsm_on = m_gi_enabled && m_csm_count > 0 && m_csm_atlas && m_directional_intensity > 0.001f && m_rsm_enabled;   // the scene's sun (m_sun_* is the gradient skybox's disc)
		if (rsm_on)
		{
			const u32 c = m_csm_count - 1;   // the last cascade: the only one whose crop holds every receiver out to the shadow distance (the near ones are frustum slices)
			XMMATRIX rsmVP = XMLoadFloat4x4(&m_csm_vp[c]);
			XMMATRIX invVP = XMLoadFloat4x4(&view.inverse_view_projection);
			XMMATRIX VP = XMLoadFloat4x4(&view.view_projection);
			XMStoreFloat4x4(&cb.view_to_rsm, proj * invVP * rsmVP);
			XMStoreFloat4x4(&cb.rsm_to_view, XMMatrixInverse(nullptr, rsmVP) * VP * XMMatrixInverse(nullptr, proj));
			cb.rsm_tile[0] = (c & 1) ? 0.5f : 0.0f; cb.rsm_tile[1] = (c & 2) ? 0.5f : 0.0f; cb.rsm_tile[2] = 0.5f; cb.rsm_tile[3] = 1.0f / (2.0f * (float)SHADOW_TILE_SIZE);
			XMVECTOR Lw = XMVector3Normalize(XMVectorNegate(XMLoadFloat3(&view.frame.light_direction)));
			XMMATRIX V = XMMatrixLookToLH(XMLoadFloat3(&view.eye), XMLoadFloat3(&view.forward), XMLoadFloat3(&view.up));
			XMVECTOR Lv = XMVector3Normalize(XMVector3TransformNormal(Lw, V));
			const float radius_m = (std::max)(2.0f, m_gi_radius * 2.0f);
			const float cascade_r = m_csm_radius[c] > 0.5f ? m_csm_radius[c] : 0.5f;
			cb.rsm_sun_view[0] = XMVectorGetX(Lv); cb.rsm_sun_view[1] = XMVectorGetY(Lv); cb.rsm_sun_view[2] = XMVectorGetZ(Lv);
			cb.rsm_sun_view[3] = (std::min)(0.3f, radius_m / (2.0f * cascade_r));
			cb.rsm_flux[0] = m_gi_albedo.x * m_light_color.x * m_directional_intensity; cb.rsm_flux[1] = m_gi_albedo.y * m_light_color.y * m_directional_intensity;
			cb.rsm_flux[2] = m_gi_albedo.z * m_light_color.z * m_directional_intensity; cb.rsm_flux[3] = radius_m;
			static bool logged = false;
			if (!logged) { logged = true; log("GI: the bounce from the sun's shadow map is on (cascade " + std::to_string(c) + ", " + std::to_string((int)(2.0f * cascade_r)) + " m across, gather " + std::to_string((int)radius_m) + " m)"); }
		}
		auto& regw = ResourceRegistry::instance(); Texture* whitep = regw.white_texture();
		SDL_GPUTexture* prev_tex = gi_prev ? m_scene_color_copy : (whitep ? whitep->texture() : nullptr);
		auto fullscreen = [&](SDL_GPUTexture* dst, SDL_GPUGraphicsPipeline* pipeline, SDL_GPUTexture* src, SDL_GPUSampler* smp)
		{
			SDL_GPUColorTargetInfo color{};
			color.texture = dst; color.load_op = SDL_GPU_LOADOP_DONT_CARE; color.store_op = SDL_GPU_STOREOP_STORE;
			SDL_GPURenderPass* pass = SDL_BeginGPURenderPass(cmd, &color, 1, nullptr);
			if (!pass) return;
			SDL_BindGPUGraphicsPipeline(pass, pipeline);
			SDL_GPUViewport vp{ 0, 0, (float)hw, (float)hh, 0.0f, 1.0f };
			SDL_SetGPUViewport(pass, &vp);
			SDL_GPUTextureSamplerBinding b[3] = { { src, smp }, { prev_tex ? prev_tex : src, m_sampler_linear_clamp }, { rsm_on ? m_csm_atlas : src, m_sampler_point_clamp } };
			SDL_BindGPUFragmentSamplers(pass, 0, b, pipeline == m_pipeline_ssao ? 3u : 1u);
			SDL_PushGPUFragmentUniformData(cmd, 0, &cb, sizeof(cb));
			SDL_DrawGPUPrimitives(pass, 3, 1, 0, 0);
			SDL_EndGPURenderPass(pass);
		};
		fullscreen(set->ao, m_pipeline_ssao, set->depth, m_sampler_point_clamp);
		fullscreen(set->blur, m_pipeline_ssao_blur, set->ao, m_sampler_point_clamp);
		m_ssao_current = set->blur;
	}

	SdlGpuRenderer::BloomChain* SdlGpuRenderer::acquire_bloom_chain(u32 w, u32 h)
	{
		for (auto& c : m_bloom_chains) if (c.w == w && c.h == h) return &c;
		if (m_bloom_chains.size() >= 2)
		{
			wait_idle();
			for (auto& m : m_bloom_chains.front().mips) if (m.tex) SDL_ReleaseGPUTexture(m_device, m.tex);
			m_bloom_chains.erase(m_bloom_chains.begin());
		}
		BloomChain c{};
		c.w = w; c.h = h;
		u32 mw = (std::max)(1u, w / 2), mh = (std::max)(1u, h / 2);
		for (u32 i = 0; i < BLOOM_MIPS && mw >= 8 && mh >= 8; ++i)
		{
			BloomMip m{ mw, mh, make_texture(m_device, mw, mh, m_scene_format, SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER) };
			if (!m.tex) break;
			c.mips.push_back(m);
			mw = (std::max)(1u, mw / 2); mh = (std::max)(1u, mh / 2);
		}
		if (c.mips.empty()) return nullptr;
		m_bloom_chains.push_back(c);
		return &m_bloom_chains.back();
	}

	// Records the bloom chain from the scene colour; sets m_bloom_result (mip 0, half res) for the uber pass.
	void SdlGpuRenderer::record_bloom(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* scene, u32 w, u32 h)
	{
		m_bloom_result = nullptr;
		if (!m_post_ready || !scene || w < 16 || h < 16) return;
		BloomChain* chain = acquire_bloom_chain(w, h);
		if (!chain) return;
		const auto& p = m_postfx.params();
		auto pass_fs = [&](SDL_GPUTexture* dst, u32 dw, u32 dh, SDL_GPUGraphicsPipeline* pipeline, SDL_GPUTexture* src, u32 sw, u32 sh, bool load, float scale, float weight)
		{
			SDL_GPUColorTargetInfo color{};
			color.texture = dst; color.load_op = load ? SDL_GPU_LOADOP_LOAD : SDL_GPU_LOADOP_DONT_CARE; color.store_op = SDL_GPU_STOREOP_STORE;
			SDL_GPURenderPass* rp = SDL_BeginGPURenderPass(cmd, &color, 1, nullptr);
			if (!rp) return;
			SDL_BindGPUGraphicsPipeline(rp, pipeline);
			SDL_GPUViewport vp{ 0, 0, (float)dw, (float)dh, 0.0f, 1.0f };
			SDL_SetGPUViewport(rp, &vp);
			SDL_GPUTextureSamplerBinding b{ src, m_sampler_linear_clamp };
			SDL_BindGPUFragmentSamplers(rp, 0, &b, 1);
			BloomCB cb{};
			cb.src_texel[0] = 1.0f / (float)sw; cb.src_texel[1] = 1.0f / (float)sh;
			cb.threshold = p.bloom_threshold; cb.knee = (std::max)(0.001f, p.bloom_knee);
			cb.sample_scale = scale; cb.weight = weight;
			SDL_PushGPUFragmentUniformData(cmd, 0, &cb, sizeof(cb));
			SDL_DrawGPUPrimitives(rp, 3, 1, 0, 0);
			SDL_EndGPURenderPass(rp);
		};
		auto& mips = chain->mips;
		pass_fs(mips[0].tex, mips[0].w, mips[0].h, m_pipeline_bloom_prefilter, scene, w, h, false, 1.0f, 1.0f);
		for (size_t i = 1; i < mips.size(); ++i)
			pass_fs(mips[i].tex, mips[i].w, mips[i].h, m_pipeline_bloom_down, mips[i - 1].tex, mips[i - 1].w, mips[i - 1].h, false, 1.0f, 1.0f);
		const float scatter = p.bloom_scatter < 0.0f ? 0.0f : (p.bloom_scatter > 1.0f ? 1.0f : p.bloom_scatter);
		for (size_t i = mips.size() - 1; i > 0; --i)
			pass_fs(mips[i - 1].tex, mips[i - 1].w, mips[i - 1].h, m_pipeline_bloom_up, mips[i].tex, mips[i].w, mips[i].h, true, 1.0f, scatter);
		m_bloom_result = mips[0].tex;
	}
}
