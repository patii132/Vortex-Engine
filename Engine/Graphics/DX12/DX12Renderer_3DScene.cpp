#include "DX12Renderer_Internal.h"

namespace vortex::graphics::dx12
{
	namespace
	{
		// The instance stream carries the world matrix (64 bytes). An affine matrix never uses its fourth column (0, 0, 0, 1),
		// so the per-instance tint (#331) rides there as (r-1, g-1, b-1, a): an untinted instance stays an exact affine
		// matrix (older custom vertex shaders keep working), and the standard vertex shaders take the two apart.
		inline void pack_instance(float* dst, const RenderItem& item)
		{
			memcpy(dst, &item.world_matrix, 64);
			dst[3] = item.color.x - 1.0f; dst[7] = item.color.y - 1.0f; dst[11] = item.color.z - 1.0f; dst[15] = item.color.w;
		}
	}

	namespace {
		struct FrustumPlanes { float p[6][4]; };

		// Extract the 6 normalized frustum planes from a row-major view-projection matrix (Gribb-Hartmann).
		// Plane form (a,b,c,d): a point is inside that plane when a*x+b*y+c*z+d >= 0.
		FrustumPlanes extract_frustum(const DirectX::XMFLOAT4X4& m)
		{
			FrustumPlanes f = {
				{
					{ m._14 + m._11, m._24 + m._21, m._34 + m._31, m._44 + m._41 }, // left
					{ m._14 - m._11, m._24 - m._21, m._34 - m._31, m._44 - m._41 }, // right
					{ m._14 + m._12, m._24 + m._22, m._34 + m._32, m._44 + m._42 }, // bottom
					{ m._14 - m._12, m._24 - m._22, m._34 - m._32, m._44 - m._42 }, // top
					{ m._13,         m._23,         m._33,         m._43         }, // near
					{ m._14 - m._13, m._24 - m._23, m._34 - m._33, m._44 - m._43 }, // far
				}
			};
			for (int i = 0; i < 6; ++i)
			{
				float a = f.p[i][0], b = f.p[i][1], c = f.p[i][2];
				float len = sqrtf(a * a + b * b + c * c);
				if (len > 1e-6f) { f.p[i][0] /= len; f.p[i][1] /= len; f.p[i][2] /= len; f.p[i][3] /= len; }
			}
			return f;
		}

		// True if a world-space bounding sphere is at least partially inside the frustum.
		bool sphere_in_frustum(const FrustumPlanes& f, float cx, float cy, float cz, float r)
		{
			for (int i = 0; i < 6; ++i)
			{
				float dist = f.p[i][0] * cx + f.p[i][1] * cy + f.p[i][2] * cz + f.p[i][3];
				if (dist < -r) return false; // fully outside this plane -> not visible
			}
			return true;
		}
	}


	void DX12Renderer::render_3d_scene()
	{
		auto rtv = m_active_rtv;
		auto dsv = m_active_dsv;
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, &dsv);
	auto* pso = m_wireframe_mode ? m_pipeline_3d.wireframe_pso() : m_pipeline_3d.pipeline_state();
	auto* double_sided_pso = m_pipeline_3d.double_sided_pso();
	m_command_list->SetPipelineState(pso);
		bind_scene_pass(m_per_frame_cb->GetGPUVirtualAddress());

		// Particles (#117): gather + upload the scene world for this view; each layer draws right after its meshes —
		// world particles against the world depth, viewmodel particles against the viewmodel's own (cleared) depth.
		const float fx_aspect = m_active_height ? (float)m_active_width / (float)m_active_height : 1.0f;
		const DX12Particles::View pview = particle_view(m_frame_constants.view_projection, m_viewmodel_view_projection,
			m_camera_position, m_camera_target, m_camera_up, m_fov_degrees, fx_aspect, m_near_clip, m_far_clip, false, 0.0f);
		const bool fx = m_particles.prepare(pview, 0);
		const bool fx0 = fx && m_particles.has_layer(0);
		const bool fx1 = fx && m_particles.has_layer(1);
		// Decals (#120): drawn between the opaque and the transparent meshes of the world pass against a copy of its depth.
		const bool fx_decals = m_decal_pass.ready() && !m_decal_list.empty();
		const bool fx_fog = m_volumetrics.active();   // volumetric fog (#119), over the world's meshes before the particles
		DX12Decals::View dview{};
		if (fx_decals || fx_fog)
		{
			using namespace DirectX;
			dview.view_projection = m_frame_constants.view_projection;
			XMStoreFloat4x4(&dview.inv_view_projection, XMMatrixInverse(nullptr, XMLoadFloat4x4(&m_frame_constants.view_projection)));
			dview.eye = m_camera_position; dview.near_clip = m_near_clip; dview.far_clip = m_far_clip; dview.ortho = false;
		}
		const DX12Particles::Environment penv = (fx || fx_decals || fx_fog) ? particle_environment(m_frame_constants) : DX12Particles::Environment{};
		const D3D12_GPU_VIRTUAL_ADDRESS fx_lights = m_light_cb ? m_light_cb->GetGPUVirtualAddress() : 0;
		auto fx_layer = [&](u32 layer)
		{
			m_particles.draw_layer(m_command_list.Get(), rtv, dsv, m_active_depth, m_active_width, m_active_height, layer, pview, penv, fx_lights);
		};
		// the collision snapshot of the world depth (the main view only; a no-op unless an emitter collides)
		auto fx_capture = [&]() { m_particles.capture_depth(m_command_list.Get(), m_active_depth, m_active_width, m_active_height, pview, rtv, dsv); };
		auto fx_volumetrics = [&]()
		{
			if (!fx_fog) return;
			const D3D12_GPU_DESCRIPTOR_HANDLE depth_srv = m_particles.scene_depth_srv(m_command_list.Get(), m_active_depth);
			if (depth_srv.ptr == 0) return;
			DX12Volumetrics::View vv{};
			vv.inv_view_projection = dview.inv_view_projection; vv.eye = m_camera_position; vv.near_clip = m_near_clip; vv.far_clip = m_far_clip; vv.ortho = false;
			m_volumetrics.draw(m_command_list.Get(), rtv, dsv, depth_srv, m_active_width, m_active_height, vv, penv, fx_lights,
				m_shadow_srv_gpu, m_csm_srv_gpu, m_point_srv_gpu, m_frame_constants.shadow_map_texel);
			bind_scene_pass(m_per_frame_cb->GetGPUVirtualAddress());   // the fog pass left its own roots bound
		};
		auto fx_only = [&]()
		{
			fx_volumetrics();
			if (fx0) fx_layer(0);
			fx_capture();
			if (fx1) { m_command_list->ClearDepthStencilView(dsv, D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0, 0, nullptr); fx_layer(1); }
		};

		auto& reg = ResourceRegistry::instance();
		size_t objectCount = (std::min)(m_render_queue.size(), static_cast<size_t>(MAX_RENDER_OBJECTS));
		if (objectCount == 0) { fx_only(); return; }

		if (m_worker_count == 0)
		{
			unsigned hc = std::thread::hardware_concurrency(); if (hc == 0) hc = 4;
			m_worker_count = (hc > 9) ? 8u : (hc - 1u); if (m_worker_count < 1) m_worker_count = 1;
		}

		bool preCullRan = false;   // the pre-cull mutates the queue -> forces a layout rebuild this frame
		// PRE-CULL for huge instanced scenes: when far more instances are SUBMITTED than can render, the O(n log n)
		// sort below over ALL of them was the bottleneck (millions of items). Drop frustum-invisible instances FIRST
		// (parallel), so the sort + run-build only touch what's actually on screen. Only kicks in over the cap, so
		// normal scenes are untouched.
		if (m_render_queue.size() > static_cast<size_t>(MAX_RENDER_OBJECTS))
		{
			using namespace DirectX;
			preCullRan = true;
			FrustumPlanes preFr = extract_frustum(m_frame_constants.view_projection);
			const size_t N = m_render_queue.size();
			std::vector<unsigned char> vis(N, 0);
			auto cullRange = [&](size_t a, size_t b)
			{
				std::unordered_map<id::id_type, XMFLOAT4> cache;   // mesh_id -> (local center xyz, radius in w)
				cache.reserve(64);
				for (size_t k = a; k < b; ++k)
				{
					const auto& it = m_render_queue[k];
					// Viewmodel items (#175) render with their OWN projection — the main frustum is wrong
					// for them and they must never be erased here.
					if (it.layer != 0) { vis[k] = 1; continue; }
					XMFLOAT4 bd;
					auto cit = cache.find(it.mesh_id);
					if (cit == cache.end())
					{
						Mesh* mp = reg.get_mesh(it.mesh_id);
						float mnx = 0, mny = 0, mnz = 0, mxx = 1, mxy = 1, mxz = 1;
						if (mp && mp->is_valid()) { mp->get_min(mnx, mny, mnz); mp->get_max(mxx, mxy, mxz); }
						float dx = mxx - mnx, dy = mxy - mny, dz = mxz - mnz;
						bd = XMFLOAT4((mnx + mxx) * 0.5f, (mny + mxy) * 0.5f, (mnz + mxz) * 0.5f, 0.5f * sqrtf(dx * dx + dy * dy + dz * dz));
						cache.emplace(it.mesh_id, bd);
					}
					else bd = cit->second;
					const XMFLOAT4X4& W = it.world_matrix;
					XMVECTOR wc = XMVector3TransformCoord(XMVectorSet(bd.x, bd.y, bd.z, 1.f), XMLoadFloat4x4(&W));
					float sx = sqrtf(W._11 * W._11 + W._12 * W._12 + W._13 * W._13);
					float sy = sqrtf(W._21 * W._21 + W._22 * W._22 + W._23 * W._23);
					float sz = sqrtf(W._31 * W._31 + W._32 * W._32 + W._33 * W._33);
					float ms = sx > sy ? (sx > sz ? sx : sz) : (sy > sz ? sy : sz);
					if (sphere_in_frustum(preFr, XMVectorGetX(wc), XMVectorGetY(wc), XMVectorGetZ(wc), bd.w * ms + 0.05f))
						vis[k] = 1;
				}
			};
			if (m_mt_enabled && m_worker_count > 1)
				m_cull_pool.run(m_worker_count, N, cullRange);
			else cullRange(0, N);
			size_t w = 0;
			for (size_t k = 0; k < N; ++k) if (vis[k]) { if (w != k) m_render_queue[w] = m_render_queue[k]; ++w; }
			m_render_queue.resize(w);
			objectCount = (std::min)(m_render_queue.size(), static_cast<size_t>(MAX_RENDER_OBJECTS));
			if (objectCount == 0) { fx_only(); return; }
		}

		// Rebuild the sorted (material,mesh) layout + run table ONLY when the submit queue changed (or the pre-cull
		// mutated it). A static map with a moving camera reuses the cached layout — the per-frame cull+pack still
		// runs (frustum-dependent), but the O(n log n) sort + O(n) run-build are skipped.
		bool needRebuild = m_queue_dirty || preCullRan || m_draw_runs.empty();

		// Sort by (material, mesh, distance): identical (mesh+material) objects become ADJACENT so each run
		// draws as ONE instanced call (fewer draw calls); distance is the tiebreaker for partial early-Z within a run.
		if (needRebuild)
		{
			const DirectX::XMFLOAT3 eye = m_camera_position;
			std::sort(m_render_queue.begin(), m_render_queue.end(),
				[&eye](const RenderItem& a, const RenderItem& b)
				{
					// Layer is the PRIMARY key (#175): viewmodel runs cluster at the TAIL so the record
					// loop can draw [0, vmStart) as the world pass and [vmStart, N) after the depth clear.
					if (a.layer != b.layer) return a.layer < b.layer;
					if (a.material_id != b.material_id) return a.material_id < b.material_id;
					if (a.mesh_id != b.mesh_id) return a.mesh_id < b.mesh_id;
					if (a.mirrored != b.mirrored) return a.mirrored < b.mirrored;
					float ax = a.world_matrix._41 - eye.x, ay = a.world_matrix._42 - eye.y, az = a.world_matrix._43 - eye.z;
					float bx = b.world_matrix._41 - eye.x, by = b.world_matrix._42 - eye.y, bz = b.world_matrix._43 - eye.z;
					return (ax * ax + ay * ay + az * az) < (bx * bx + by * by + bz * bz);
				});
		}

		// Frustum culling: build the 6 view planes once; objects fully outside are skipped (no draw, no
		// vertex work). The biggest 2026-standard CPU win — don't render what the camera can't see.
		FrustumPlanes frustum = extract_frustum(m_frame_constants.view_projection);

		// Distance cull: skip instances whose world center is beyond the render-distance setting (0 = off).
		const DirectX::XMFLOAT3 eye = m_camera_position;
		const bool useDist = m_render_distance > 0.0f;
		const float rd2 = m_render_distance * m_render_distance;
		const bool useLod = m_lod_enabled && m_lod_mid > 0.0f;
		const float lodMid2 = m_lod_mid * m_lod_mid;
		const float lodFar2 = m_lod_far * m_lod_far;

		// Build (mesh,material) runs over the sorted queue, precomputing each run's mesh bounds + a worst-case
		// instance-VB slab (vbBase = item count). Then CULL+PACK visible instances in PARALLEL across the flat
		// item list (atomic append into each run's slab — lock-free, and order-independent because instancing
		// ignores instance order), and finally record the draws single-threaded. This parallelizes the
		// per-instance frustum/distance test + memcpy — the CPU cost when a mesh is rendered thousands of times.
		using namespace DirectX;
		if (needRebuild)
		{
		m_draw_runs.clear();
		m_item_run.resize(objectCount);
		{
			u32 vbBase = 0; size_t i = 0;
			while (i < objectCount)
			{
				const auto idMesh = m_render_queue[i].mesh_id;
				const auto idMat = m_render_queue[i].material_id;
				const u32 idLayer = m_render_queue[i].layer;
				const bool skinnedRun = m_render_queue[i].bone_offset != NO_BONES;
				const u32 idMirrored = m_render_queue[i].mirrored;
				size_t j = i;
				if (skinnedRun)
					j = i + 1;   // each skinned item is its OWN run (needs its own bone palette bound)
				else
					// Layer in the break condition (#175): a world item and a viewmodel item sharing
					// mesh+material must never merge into one run (correct by construction, not just by sort).
					while (j < objectCount && m_render_queue[j].mesh_id == idMesh && m_render_queue[j].material_id == idMat
						&& m_render_queue[j].bone_offset == NO_BONES && m_render_queue[j].layer == idLayer
						&& m_render_queue[j].mirrored == idMirrored) ++j;
				u32 cnt = (u32)(j - i);
				Mesh* meshp = reg.get_mesh(idMesh);
				float minx = 0, miny = 0, minz = 0, maxx = 1, maxy = 1, maxz = 1;
				if (meshp && meshp->is_valid()) { meshp->get_min(minx, miny, minz); meshp->get_max(maxx, maxy, maxz); }
				DrawRun run{};
				run.layer = idLayer;
				run.mirrored = idMirrored != 0;
				run.start = i; run.count = cnt; run.mesh = idMesh; run.mat = idMat; run.meshp = meshp;
				run.defaultBounds = (minx == 0.f && miny == 0.f && minz == 0.f && maxx == 1.f && maxy == 1.f && maxz == 1.f);
				run.lcx = (minx + maxx) * 0.5f; run.lcy = (miny + maxy) * 0.5f; run.lcz = (minz + maxz) * 0.5f;
				float bdx = maxx - minx, bdy = maxy - miny, bdz = maxz - minz;
				run.localR = 0.5f * sqrtf(bdx * bdx + bdy * bdy + bdz * bdz);
				run.vbBase = vbBase; run.visible = 0;
				if (skinnedRun)
				{
					run.skinned = true;
					run.boneOffset = m_render_queue[i].bone_offset;
					run.boneCount = m_render_queue[i].bone_count;
					run.localR *= 2.0f;   // animated poses leave the bind-pose bounds — inflate the cull sphere
				}
				if (m_geo_lod_enabled && !skinnedRun)
				{
					const auto* chain = reg.get_lod_chain(idMesh);
					if (chain && chain->lod_count > 1)
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
				u32 ri = (u32)m_draw_runs.size();
				m_draw_runs.push_back(run);
				for (size_t k = i; k < j; ++k) m_item_run[k] = ri;
				vbBase += cnt;
				i = j;
			}
		}
		m_queue_dirty = false;
		}
		const size_t runN = m_draw_runs.size();
		m_instances_tested += (int)objectCount;

		// Per-run atomic pack counters (visible instances appended into each run's reserved slab).
		std::unique_ptr<std::atomic<u32>[]> counters(new std::atomic<u32>[runN ? runN : 1]);
		for (size_t r = 0; r < runN; ++r) counters[r].store(0, std::memory_order_relaxed);

		auto cullPack = [&](size_t a, size_t b)
		{
			for (size_t k = a; k < b; ++k)
			{
				const u32 ri = m_item_run[k];
				const DrawRun& run = m_draw_runs[ri];
				const auto& item = m_render_queue[k];
				bool visible = true;
					int instLod = 0;   // geometric-LOD level for this instance (0 = full)
				// Viewmodel runs (#175) bypass frustum/distance culling — their projection differs.
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
					if (visible && (useDist || useLod))
					{
						float ddx = cx - eye.x, ddy = cy - eye.y, ddz = cz - eye.z;
						float d2 = ddx * ddx + ddy * ddy + ddz * ddz;
						if (useDist && d2 > rd2) visible = false;
						else if (useLod && !m_geo_lod_enabled)
						{
							// Density LOD: keep every instance up close, 1/2 beyond mid, 1/4 beyond far. Selection
							// by the stable item index k -> deterministic, no per-frame flicker.
							if (d2 > lodFar2) { if ((k & 3) != 0) visible = false; }
							else if (d2 > lodMid2) { if ((k & 1) != 0) visible = false; }
						}
					}
				}
				if (visible && m_instance_vb_mapped)
				{
					u32 slot = counters[ri].fetch_add(1, std::memory_order_relaxed);
					if (run.vbBase + slot < MAX_RENDER_OBJECTS)
						pack_instance((float*)((u8*)m_instance_vb_mapped + (size_t)(run.vbBase + slot) * 64), item);
					// Geometric LOD: bucket this instance by distance. Single-threaded when geo-LOD is on (see
					// m_mt_active), so the slab is distance-ordered and lodCount segments are contiguous.
					if (run.lodLevels > 1)
					{
						const XMFLOAT4X4& Wl = item.world_matrix;
						float lx = Wl._41 - eye.x, ly = Wl._42 - eye.y, lz = Wl._43 - eye.z;
						float ld2 = lx * lx + ly * ly + lz * lz;
						instLod = (ld2 > run.lodT3sq) ? 3 : (ld2 > run.lodT2sq) ? 2 : (ld2 > run.lodT1sq) ? 1 : 0;
						if (instLod >= (int)run.lodLevels) instLod = (int)run.lodLevels - 1;
						m_draw_runs[ri].lodCount[instLod]++;
					}
				}
			}
		};

		// Parallelize only when there's enough per-instance work to amortize the threads (force ignores it).
		// Geometric LOD needs the per-run slab in distance order (so LOD segments are contiguous) — that requires
		// the single-threaded ordered pack. MT gives no FPS here anyway (GPU-bound), so just disable it for LOD.
		m_mt_active = m_mt_enabled && runN > 0 && m_worker_count > 1 && (m_mt_force || objectCount >= (size_t)m_mt_threshold);
		if (m_geo_lod_enabled)
		{
			// 2-PASS MULTITHREADED cull for geometric LOD: parallel count -> compact prefix-sum offsets ->
			// parallel pack. Uses ALL worker threads (the single-threaded ordered pack was the FPS bottleneck at
			// high instance counts) and packs ONLY visible instances compactly (no reserved gaps for culled
			// copies), so the instance VB holds far more on screen.
			if (m_item_lod.size() < objectCount) m_item_lod.resize(objectCount);
			const size_t c4n = (runN ? runN : 1) * 4;
			std::unique_ptr<std::atomic<u32>[]> c4(new std::atomic<u32>[c4n]);
			for (size_t z = 0; z < c4n; ++z) c4[z].store(0, std::memory_order_relaxed);

			auto passA = [&](size_t a, size_t b)
			{
				for (size_t k = a; k < b; ++k)
				{
					const u32 ri = m_item_run[k];
					const DrawRun& run = m_draw_runs[ri];
					const auto& item = m_render_queue[k];
					bool visible = true; int lod = 0;
					if (!run.defaultBounds && run.layer == 0)   // #175: viewmodel bypasses the main frustum
					{
						const XMFLOAT4X4& W = item.world_matrix;
						XMVECTOR wc = XMVector3TransformCoord(XMVectorSet(run.lcx, run.lcy, run.lcz, 1.f), XMLoadFloat4x4(&W));
						float cx = XMVectorGetX(wc), cy = XMVectorGetY(wc), cz = XMVectorGetZ(wc);
						float sx = sqrtf(W._11 * W._11 + W._12 * W._12 + W._13 * W._13);
						float sy = sqrtf(W._21 * W._21 + W._22 * W._22 + W._23 * W._23);
						float sz = sqrtf(W._31 * W._31 + W._32 * W._32 + W._33 * W._33);
						float maxScale = sx > sy ? (sx > sz ? sx : sz) : (sy > sz ? sy : sz);
						visible = sphere_in_frustum(frustum, cx, cy, cz, run.localR * maxScale + 0.05f);
						if (visible)
						{
							float dx = cx - eye.x, dy = cy - eye.y, dz = cz - eye.z;
							float d2 = dx * dx + dy * dy + dz * dz;
							if (useDist && d2 > rd2) visible = false;
							else if (run.lodLevels > 1)
							{
								lod = (d2 > run.lodT3sq) ? 3 : (d2 > run.lodT2sq) ? 2 : (d2 > run.lodT1sq) ? 1 : 0;
								if (lod >= (int)run.lodLevels) lod = (int)run.lodLevels - 1;
							}
						}
					}
					if (visible) { m_item_lod[k] = (unsigned char)lod; c4[ri * 4 + lod].fetch_add(1, std::memory_order_relaxed); }
					else m_item_lod[k] = 0xFF;
				}
			};
			if (m_mt_active)
				m_cull_pool.run(m_worker_count, objectCount, passA);
			else passA(0, objectCount);

			// Compact prefix sums: each run's base = the running VISIBLE total (not the submitted count); c4[run,LOD]
			// becomes the absolute pack pointer. Clamp to the VB cap so a huge view can't overrun the buffer.
			u32 globalBase = 0;
			for (size_t r = 0; r < runN; ++r)
			{
				m_draw_runs[r].vbBase = globalBase;
				u32 localBase = 0;
				for (int L = 0; L < 4; ++L)
				{
					u32 cnt = c4[r * 4 + L].load(std::memory_order_relaxed);
					u32 segStart = globalBase + localBase;
					if (segStart >= MAX_RENDER_OBJECTS) cnt = 0;
					else if (segStart + cnt > MAX_RENDER_OBJECTS) cnt = MAX_RENDER_OBJECTS - segStart;
					m_draw_runs[r].lodCount[L] = cnt;
					c4[r * 4 + L].store(segStart, std::memory_order_relaxed);
					localBase += cnt;
				}
				m_draw_runs[r].visible = localBase;
				globalBase += localBase;
			}

			auto passB = [&](size_t a, size_t b)
			{
				for (size_t k = a; k < b; ++k)
				{
					unsigned char lod = m_item_lod[k];
					if (lod == 0xFF) continue;
					const u32 ri = m_item_run[k];
					u32 dst = c4[ri * 4 + lod].fetch_add(1, std::memory_order_relaxed);
					if (dst < MAX_RENDER_OBJECTS && m_instance_vb_mapped)
						pack_instance((float*)((u8*)m_instance_vb_mapped + (size_t)dst * 64), m_render_queue[k]);
				}
			};
			if (m_mt_active)
				m_cull_pool.run(m_worker_count, objectCount, passB);
			else passB(0, objectCount);
		}
		else if (m_mt_active)
		{
			m_cull_pool.run(m_worker_count, objectCount, cullPack);
		}
		else
		{
			cullPack(0, objectCount);
		}

		if (!m_geo_lod_enabled)
			for (size_t r = 0; r < runN; ++r) m_draw_runs[r].visible = counters[r].load(std::memory_order_relaxed);

		// Fill the per-run object constants + bind the material's texture tables. Shared by the opaque
		// loop and the sorted transparent pass (#33) — identical material state, different PSO/order.
		auto apply_material = [&](Material* mat, PerObjectConstants& obj)
		{
			obj.base_color = { 0.85f, 0.85f, 0.88f, 1.0f };
			obj.metallic = 0.7f; obj.roughness = 0.35f; obj.ao = 1.0f; obj.normal_strength = 1.0f;
			obj.use_directx_normals = 1;
			obj.uv_tiling = { 1.0f, 1.0f };
			if (!mat) return;
			const auto& props = mat->properties();
			obj.base_color = props.base_color; obj.metallic = props.metallic; obj.roughness = props.roughness;
			obj.ao = props.ao; obj.normal_strength = props.normal_strength; obj.use_directx_normals = props.use_directx_normals;
			obj.is_unlit = props.is_unlit; obj.emissive_strength = props.emissive_strength; // feed the PS's unlit path (was zeroed padding)
			obj.uv_tiling = props.uv_tiling;   // texture repeat scale -> the PS multiplies UVs by this
			auto* tex = mat->albedo_texture();
			if (tex && tex->is_valid() && tex->srv_gpu().ptr != 0) { obj.has_albedo_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(3, tex->srv_gpu()); }
			auto* normal = mat->normal_texture();
			if (normal && normal->is_valid() && normal->srv_gpu().ptr != 0) { obj.has_normal_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(4, normal->srv_gpu()); }
			auto* metallic_tex = mat->metallic_texture();
			if (metallic_tex && metallic_tex->is_valid() && metallic_tex->srv_gpu().ptr != 0) { obj.has_metallic_texture = props.has_metallic_texture ? props.has_metallic_texture : 1; m_command_list->SetGraphicsRootDescriptorTable(5, metallic_tex->srv_gpu()); }
			auto* roughness_tex = mat->roughness_texture();
			if (roughness_tex && roughness_tex->is_valid() && roughness_tex->srv_gpu().ptr != 0) { obj.has_roughness_texture = props.has_roughness_texture ? props.has_roughness_texture : 1; m_command_list->SetGraphicsRootDescriptorTable(6, roughness_tex->srv_gpu()); }
			auto* ao_tex = mat->ao_texture();
			if (ao_tex && ao_tex->is_valid() && ao_tex->srv_gpu().ptr != 0) { obj.has_ao_texture = props.has_ao_texture ? props.has_ao_texture : 1; m_command_list->SetGraphicsRootDescriptorTable(7, ao_tex->srv_gpu()); }
			auto* height_tex = mat->height_texture();
			if (height_tex && height_tex->is_valid() && height_tex->srv_gpu().ptr != 0) { obj.has_height_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(9, height_tex->srv_gpu()); }
			obj.height_scale = props.height_scale;   // parallax depth (root param 9 = height map at t6)
			obj.alpha_cutoff = props.alpha_cutoff;   // AlphaTest (#329)
		};

		// Record the draws single-threaded: one DrawIndexedInstanced per run with visible instances.
		// Transparent runs (#33) are deferred: their instances are already culled+packed in the slab,
		// but they draw AFTER every opaque, sorted back-to-front. Wireframe mode ignores transparency
		// (debug view shows everything), skinned + custom-shader materials stay opaque in v1.
		// #175: the whole record (opaque loop + transparent tail) is a lambda over a RUN RANGE — pass 1
		// draws the world runs, pass 2 the viewmodel tail after a depth clear + b0 swap. cbSlot is
		// SHARED across both passes (per-run CB slots must not alias); transparentRuns is per-pass
		// (world transparents must not re-draw over the cleared depth).
		u32 cbSlot = 0;
		auto record_pass = [&](size_t rBegin, size_t rEnd, bool world)
		{
		std::vector<u32> transparentRuns;
		for (size_t r = rBegin; r < rEnd; ++r)
		{
			const DrawRun& run = m_draw_runs[r];
			m_instances_drawn += run.visible;
			Mesh* mesh = run.meshp;
			if (!mesh || !mesh->is_valid() || run.visible == 0) continue;

			auto* mat = reg.get_material(run.mat);
			// alpha blend / additive draw in the sorted transparent pass — custom shaders included (#333); alpha test
			// (3) stays here and discards in the shader (#329)
			const u32 blend = mat ? mat->blend_mode() : 0u;
			if (!m_wireframe_mode && !run.skinned && (blend == 1 || blend == 2) && m_pipeline_3d.transparent_pso(blend, false))
			{
				transparentRuns.push_back((u32)r);
				continue;
			}

			// Material + PSO + textures: set ONCE for the whole run (shared by all its instances).
			// Skinned runs use the skinned PSO + bind their bone palette (root SRV param 8). Custom material
			// shaders don't apply to skinned meshes in v1 (they'd need a skinned input-layout variant).
			// A compiled custom per-material shader overrides the built-in PSO; else unlit -> double-sided, else PBR.
			auto* skinned_pso = m_pipeline_3d.skinned_pso(run.mirrored);
			// TwoSided and unlit materials cull nothing; everything else culls back faces, and a mirrored run (#334)
			// takes the counter-clockwise twin so its front faces survive
			const bool two_sided = mat && (mat->properties().is_unlit || mat->double_sided());
			if (run.skinned && skinned_pso && m_bone_vb)
			{
				// a custom material shader applies to skinned meshes too (#332): engine skinning VS + its PSMain
				ID3D12PipelineState* custom = custom_pso((u32)run.mat, 0, two_sided, run.mirrored, true);
				m_command_list->SetPipelineState(custom ? custom : skinned_pso);
				m_command_list->SetGraphicsRootShaderResourceView(8,
					bone_palette_base_va() + (UINT64)run.boneOffset * 64);
			}
			else
			{
				ID3D12PipelineState* custom = custom_pso((u32)run.mat, 0, two_sided, run.mirrored);
				if (custom) m_command_list->SetPipelineState(custom);
				else if (two_sided) m_command_list->SetPipelineState(double_sided_pso);
				else m_command_list->SetPipelineState(m_wireframe_mode ? pso : m_pipeline_3d.pipeline_state(run.mirrored));
			}
			PerObjectConstants obj{};
			apply_material(mat, obj);
			// One CB slot per run; clamp to the CB capacity (runs beyond reuse the last slot — only matters
			// with thousands of DISTINCT materials, which instancing makes rare).
			u32 slot = (cbSlot < MAX_DRAW_RUNS) ? cbSlot : (MAX_DRAW_RUNS - 1);
			if (m_per_object_cb_mapped) memcpy((u8*)m_per_object_cb_mapped + (size_t)slot * 256, &obj, sizeof(obj));
			m_command_list->SetGraphicsRootConstantBufferView(1, m_per_object_cb->GetGPUVirtualAddress() + (size_t)slot * 256);
			if (cbSlot < MAX_DRAW_RUNS) ++cbSlot;

			// Bind mesh vertices (slot 0) + this run's per-instance world matrices (slot 1); draw all at once.
			if (run.lodLevels > 1)
			{
				// Geometric LOD: the slab is distance-ordered, so each LOD's instances are a contiguous segment.
				// Draw each LOD's segment with its own (decimated) mesh — whole crowd visible, far ones low-poly.
				u32 segStart = 0;
				for (u32 L = 0; L < run.lodLevels; ++L)
				{
					u32 c = run.lodCount[L];
					if (c == 0) continue;
					Mesh* lm = (L == 0) ? mesh : reg.get_mesh(run.lodMesh[L]);
					if (!lm || !lm->is_valid()) lm = mesh;
					D3D12_VERTEX_BUFFER_VIEW lv[2];
					lv[0] = lm->vertex_buffer_view();
					lv[1].BufferLocation = m_instance_vb->GetGPUVirtualAddress() + (UINT64)(run.vbBase + segStart) * 64;
					lv[1].SizeInBytes = c * 64;
					lv[1].StrideInBytes = 64;
					m_command_list->IASetVertexBuffers(0, 2, lv);
					if (lm->has_indices())
					{
						m_command_list->IASetIndexBuffer(&lm->index_buffer_view());
						m_command_list->DrawIndexedInstanced(lm->index_count(), c, 0, 0, 0);
						m_vertex_count += lm->index_count() * c;
					}
					else
					{
						m_command_list->DrawInstanced(lm->vertex_count(), c, 0, 0);
						m_vertex_count += lm->vertex_count() * c;
					}
					++m_draw_call_count;
					segStart += c;
				}
			}
			else
			{
			// Bind mesh vertices (slot 0) + this run's per-instance world matrices (slot 1); draw all at once.
			D3D12_VERTEX_BUFFER_VIEW vbs[2];
			vbs[0] = mesh->vertex_buffer_view();
			vbs[1].BufferLocation = m_instance_vb->GetGPUVirtualAddress() + (UINT64)run.vbBase * 64;
			vbs[1].SizeInBytes = run.visible * 64;
			vbs[1].StrideInBytes = 64;
			m_command_list->IASetVertexBuffers(0, 2, vbs);
			if (mesh->has_indices())
			{
				m_command_list->IASetIndexBuffer(&mesh->index_buffer_view());
				m_command_list->DrawIndexedInstanced(mesh->index_count(), run.visible, 0, 0, 0);
				m_vertex_count += mesh->index_count() * run.visible;
			}
			else
			{
				m_command_list->DrawInstanced(mesh->vertex_count(), run.visible, 0, 0);
				m_vertex_count += mesh->vertex_count() * run.visible;
			}
			++m_draw_call_count;
			}
		}

		// Decals (#120) go between the opaque and the transparent meshes of the world pass: back faces of their boxes,
		// no depth test, the covered position reconstructed from a copy of the depth so far.
		if (world && fx_decals)
		{
			const D3D12_GPU_DESCRIPTOR_HANDLE depth_srv = m_particles.scene_depth_srv(m_command_list.Get(), m_active_depth);
			if (depth_srv.ptr != 0)
				m_decal_pass.draw(m_command_list.Get(), rtv, dsv, depth_srv, m_active_width, m_active_height, dview, penv, fx_lights, m_decal_list);
			bind_scene_pass(m_per_frame_cb->GetGPUVirtualAddress());   // the decal pass left its own roots bound
		}
		// ---- Sorted transparent pass (#33): every deferred run's packed instances, back-to-front ----
		// The instances already sit culled+packed in the instance-VB slab (upload heap = CPU-readable);
		// each item's view depth comes from the world translation at bytes 48..59 of its 64B matrix.
		// Blend order matters, so items draw ONE BY ONE with depth write off — correct layering beats
		// instancing here, and horror scenes hold dozens of transparents, not thousands (capped anyway).
		if (!transparentRuns.empty() && m_instance_vb_mapped)
		{
			struct TDraw { u32 run; u32 slot; float d2; };
			std::vector<TDraw> titems;
			constexpr size_t MAX_TRANSPARENT_DRAWS = 4096;
			for (u32 tr : transparentRuns)
			{
				const DrawRun& run = m_draw_runs[tr];
				for (u32 s2 = 0; s2 < run.visible && titems.size() < MAX_TRANSPARENT_DRAWS; ++s2)
				{
					const float* t = reinterpret_cast<const float*>(
						(u8*)m_instance_vb_mapped + (size_t)(run.vbBase + s2) * 64 + 48);
					float dx = t[0] - eye.x, dy = t[1] - eye.y, dz = t[2] - eye.z;
					titems.push_back({ tr, s2, dx * dx + dy * dy + dz * dz });
				}
			}
			std::sort(titems.begin(), titems.end(),
				[](const TDraw& a, const TDraw& b) { return a.d2 > b.d2; });   // farthest first

			u32 lastRun = 0xFFFFFFFF;
			Mesh* tmesh = nullptr;
			for (const TDraw& td : titems)
			{
				const DrawRun& run = m_draw_runs[td.run];
				if (td.run != lastRun)
				{
					lastRun = td.run;
					tmesh = run.meshp;   // LOD0 for every distance — transparents are few and LOD pops read badly through glass
					if (!tmesh || !tmesh->is_valid()) { tmesh = nullptr; continue; }
					auto* mat = reg.get_material(run.mat);
					const u32 bm = mat ? mat->blend_mode() : 1u;
					const bool ds = mat && (mat->properties().is_unlit || mat->double_sided());   // mirror the opaque rule
					ID3D12PipelineState* tpso = custom_pso((u32)run.mat, bm, ds, run.mirrored);   // a custom shader keeps its blend mode (#333)
					if (!tpso) tpso = m_pipeline_3d.transparent_pso(bm, ds, run.mirrored);
					if (!tpso) { tmesh = nullptr; continue; }
					m_command_list->SetPipelineState(tpso);
					PerObjectConstants obj{};
					apply_material(mat, obj);
					u32 slot = (cbSlot < MAX_DRAW_RUNS) ? cbSlot : (MAX_DRAW_RUNS - 1);
					if (m_per_object_cb_mapped) memcpy((u8*)m_per_object_cb_mapped + (size_t)slot * 256, &obj, sizeof(obj));
					m_command_list->SetGraphicsRootConstantBufferView(1, m_per_object_cb->GetGPUVirtualAddress() + (size_t)slot * 256);
					if (cbSlot < MAX_DRAW_RUNS) ++cbSlot;
					if (tmesh->has_indices())
						m_command_list->IASetIndexBuffer(&tmesh->index_buffer_view());
				}
				if (!tmesh) continue;
				D3D12_VERTEX_BUFFER_VIEW tv[2];
				tv[0] = tmesh->vertex_buffer_view();
				tv[1].BufferLocation = m_instance_vb->GetGPUVirtualAddress() + (UINT64)(run.vbBase + td.slot) * 64;
				tv[1].SizeInBytes = 64;
				tv[1].StrideInBytes = 64;
				m_command_list->IASetVertexBuffers(0, 2, tv);
				if (tmesh->has_indices())
				{
					m_command_list->DrawIndexedInstanced(tmesh->index_count(), 1, 0, 0, 0);
					m_vertex_count += tmesh->index_count();
				}
				else
				{
					m_command_list->DrawInstanced(tmesh->vertex_count(), 1, 0, 0);
					m_vertex_count += tmesh->vertex_count();
				}
				++m_draw_call_count;
			}
		}
		};

		// Runs are layer-sorted, so the viewmodel set is a contiguous TAIL. vmStart = its first run.
		size_t vmStart = runN;
		for (size_t r = 0; r < runN; ++r) if (m_draw_runs[r].layer != 0) { vmStart = r; break; }

		record_pass(0, vmStart, true);
		fx_volumetrics();   // volumetric fog over the finished world (#119), before the particles
		// World-layer particles after the opaque + transparent meshes, then the collision snapshot of the world depth
		// (before the viewmodel pass clears it).
		if (fx0) fx_layer(0);
		fx_capture();
		if (vmStart < runN || fx1)
		{
			// Viewmodel pass (#175): clear depth so the first-person weapon/arms NEVER clip world
			// geometry, swap b0 to the viewmodel projection (own FOV, identical lighting fields —
			// lighting is world-space, so only the projection changes), draw the tail runs, then
			// restore b0 for everything recorded after (wire gizmos, preview restores, later passes).
			// Viewmodel particles (a muzzle flash on the weapon, #117) need that cleared depth too, even
			// without viewmodel meshes.
			m_command_list->ClearDepthStencilView(m_active_dsv, D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0, 0, nullptr);
			bind_scene_pass(m_viewmodel_cb ? m_viewmodel_cb->GetGPUVirtualAddress() : m_per_frame_cb->GetGPUVirtualAddress());   // the particle pass left its own roots
			record_pass(vmStart, runN, false);
			if (fx1) { fx_layer(1); bind_scene_pass(m_per_frame_cb->GetGPUVirtualAddress()); }
			else m_command_list->SetGraphicsRootConstantBufferView(0, m_per_frame_cb->GetGPUVirtualAddress());
		}
		}


	// Dedicated ALWAYS-ON-TOP pass for editor gizmos: draws the gizmo queue with the depth-DISABLED gizmo PSO so the
	// move/rotate/scale handles + the selection outline render over scene geometry (never occluded). Reuses the TAIL
	// slots of the per-object CB + instance VB (the editor scene never fills 8192 runs / 262144 instances), so no
	// extra GPU buffers are needed. One draw per gizmo mesh (few per frame) — no instancing/culling.
	void DX12Renderer::bind_scene_pass(D3D12_GPU_VIRTUAL_ADDRESS frame_cb)
	{
		m_command_list->SetGraphicsRootSignature(m_pipeline_3d.root_signature());
		m_command_list->SetGraphicsRootConstantBufferView(0, frame_cb);
		m_command_list->SetGraphicsRootConstantBufferView(2, m_light_cb->GetGPUVirtualAddress());
		if (auto* srv_heap = ResourceRegistry::instance().srv_heap()) { ID3D12DescriptorHeap* heaps[] = { srv_heap }; m_command_list->SetDescriptorHeaps(1, heaps); }
		if (m_shadow_srv_gpu.ptr != 0)
			m_command_list->SetGraphicsRootDescriptorTable(10, m_shadow_srv_gpu);
		if (m_csm_srv_gpu.ptr != 0)
			m_command_list->SetGraphicsRootDescriptorTable(11, m_csm_srv_gpu);   // t8 (#24)
		if (m_point_srv_gpu.ptr != 0)
			m_command_list->SetGraphicsRootDescriptorTable(12, m_point_srv_gpu); // t9 (#25)
		if (m_ssao_current_srv.ptr != 0)
			m_command_list->SetGraphicsRootDescriptorTable(13, m_ssao_current_srv); // t10 (#32)
		{ auto env = env_map_srv(); if (env.ptr != 0) m_command_list->SetGraphicsRootDescriptorTable(14, env); } // t11 (IBL step 2)
	}

	DX12Particles::View DX12Renderer::particle_view(const DirectX::XMFLOAT4X4& view_projection, const DirectX::XMFLOAT4X4& viewmodel_projection,
		const DirectX::XMFLOAT3& eye, const DirectX::XMFLOAT3& at, const DirectX::XMFLOAT3& up_hint, float fov_degrees, float aspect,
		float near_clip, float far_clip, bool ortho, float ortho_size) const
	{
		using namespace DirectX;
		DX12Particles::View v{};
		v.view_projection = view_projection;
		v.viewmodel_projection = viewmodel_projection;
		v.eye = eye;
		// the XMMatrixLookAtLH basis (forward = at - eye, right = up x forward, up = forward x right)
		XMVECTOR f = XMVector3Normalize(XMVectorSubtract(XMLoadFloat3(&at), XMLoadFloat3(&eye)));
		XMVECTOR r = XMVector3Normalize(XMVector3Cross(XMLoadFloat3(&up_hint), f));
		XMVECTOR u = XMVector3Cross(f, r);
		XMStoreFloat3(&v.forward, f); XMStoreFloat3(&v.right, r); XMStoreFloat3(&v.up, u);
		v.near_clip = near_clip; v.far_clip = far_clip; v.ortho = ortho;
		v.vm_near_clip = VIEWMODEL_NEAR; v.vm_far_clip = VIEWMODEL_FAR;
		if (ortho) { v.tan_half_y = ortho_size * 0.5f; v.tan_half_x = v.tan_half_y * aspect; }
		else { v.tan_half_y = tanf(XMConvertToRadians(fov_degrees) * 0.5f); v.tan_half_x = v.tan_half_y * aspect; }
		return v;
	}

	DX12Particles::Environment DX12Renderer::particle_environment(const PerFrameConstants& f) const
	{
		DX12Particles::Environment e{};
		e.fog_color = f.fog_color; e.fog_density = f.fog_density;
		e.fog_height_y = f.fog_height_y; e.fog_height_falloff = f.fog_height_falloff;
		e.sun_direction = f.light_direction; e.sun_intensity = f.directional_intensity;
		e.sun_color = f.light_color; e.ambient = f.ambient_strength;
		e.point_lights = (std::min)(f.point_light_count, (u32)MAX_POINT_LIGHTS);
		e.spot_lights = (std::min)(f.spot_light_count, (u32)MAX_SPOT_LIGHTS);
		return e;
	}

	void DX12Renderer::render_gizmos()
	{
		if (m_gizmo_render.empty() && m_gizmo_wire_render.empty()) return;
		auto* gpso = m_pipeline_3d.gizmo_pso();
		if (!gpso) return;

		auto rtv = m_active_rtv; auto dsv = m_active_dsv;
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, &dsv);
		m_command_list->SetPipelineState(gpso);
		m_command_list->SetGraphicsRootSignature(m_pipeline_3d.root_signature());
		m_command_list->SetGraphicsRootConstantBufferView(0, m_per_frame_cb->GetGPUVirtualAddress());
		m_command_list->SetGraphicsRootConstantBufferView(2, m_light_cb->GetGPUVirtualAddress());
		{ auto* sh = ResourceRegistry::instance().srv_heap(); if (sh) { ID3D12DescriptorHeap* hh[] = { sh }; m_command_list->SetDescriptorHeaps(1, hh); } }
		// Gizmos draw with the standard PS too -> the t7 shadow table must be bound here as well.
		if (m_shadow_srv_gpu.ptr != 0)
			m_command_list->SetGraphicsRootDescriptorTable(10, m_shadow_srv_gpu);
		if (m_csm_srv_gpu.ptr != 0)
			m_command_list->SetGraphicsRootDescriptorTable(11, m_csm_srv_gpu);   // t8 (#24)
		if (m_point_srv_gpu.ptr != 0)
			m_command_list->SetGraphicsRootDescriptorTable(12, m_point_srv_gpu); // t9 (#25)
		if (m_ssao_current_srv.ptr != 0)
			m_command_list->SetGraphicsRootDescriptorTable(13, m_ssao_current_srv); // t10 (#32)
		{ auto env = env_map_srv(); if (env.ptr != 0) m_command_list->SetGraphicsRootDescriptorTable(14, env); } // t11 (IBL step 2)
		m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);

		auto& reg = ResourceRegistry::instance();
		const u32 cbBase = MAX_DRAW_RUNS - MAX_GIZMO_ITEMS;         // tail CB slots (never used by the scene)
		const u32 vbBase = MAX_RENDER_OBJECTS - MAX_GIZMO_ITEMS;    // tail instance-VB slots

		// Draw one gizmo list; `slot` indexes the shared tail CB/VB range and keeps advancing across lists so
		// solid + wire items never alias a slot (their SUM is capped at MAX_GIZMO_ITEMS at submit time).
		u32 slot = 0;
		auto draw_list = [&](const std::vector<RenderItem>& list)
		{
			for (size_t i = 0; i < list.size() && slot < MAX_GIZMO_ITEMS; ++i)
			{
				const RenderItem& item = list[i];
				Mesh* mesh = reg.get_mesh(item.mesh_id);
				if (!mesh || !mesh->is_valid()) continue;

				PerObjectConstants obj{};
				obj.world = item.world_matrix;
				obj.base_color = { 0.9f, 0.9f, 0.95f, 1.0f };
				obj.metallic = 0.0f; obj.roughness = 1.0f; obj.ao = 1.0f; obj.normal_strength = 1.0f;
				obj.use_directx_normals = 1;
				obj.emissive_strength = 1.0f;
				auto* mat = reg.get_material(item.material_id);
				if (mat)
				{
					const auto& props = mat->properties();
					obj.base_color = props.base_color;
					obj.is_unlit = props.is_unlit;                    // unlit gizmo materials = constant bright color,
					obj.emissive_strength = props.emissive_strength;  // readable from every angle / in shadow
				}

				u32 cbSlot = cbBase + slot;
				u32 vbSlot = vbBase + slot;
				++slot;
				if (m_per_object_cb_mapped) memcpy((u8*)m_per_object_cb_mapped + (size_t)cbSlot * 256, &obj, sizeof(obj));
				m_command_list->SetGraphicsRootConstantBufferView(1, m_per_object_cb->GetGPUVirtualAddress() + (size_t)cbSlot * 256);
				if (m_instance_vb_mapped) memcpy((u8*)m_instance_vb_mapped + (size_t)vbSlot * 64, &item.world_matrix, 64);

				D3D12_VERTEX_BUFFER_VIEW vbs[2];
				vbs[0] = mesh->vertex_buffer_view();
				vbs[1].BufferLocation = m_instance_vb->GetGPUVirtualAddress() + (UINT64)vbSlot * 64;
				vbs[1].SizeInBytes = 64;
				vbs[1].StrideInBytes = 64;
				m_command_list->IASetVertexBuffers(0, 2, vbs);
				if (mesh->has_indices())
				{
					m_command_list->IASetIndexBuffer(&mesh->index_buffer_view());
					m_command_list->DrawIndexedInstanced(mesh->index_count(), 1, 0, 0, 0);
				}
				else
				{
					m_command_list->DrawInstanced(mesh->vertex_count(), 1, 0, 0);
				}
				++m_draw_call_count;
			}
		};

		draw_list(m_gizmo_render);

		// Wireframe gizmos (audio range spheres, reverb zones): same pass, wire PSO.
		if (!m_gizmo_wire_render.empty())
		{
			auto* wpso = m_pipeline_3d.gizmo_wire_pso();
			if (wpso)
			{
				m_command_list->SetPipelineState(wpso);
				draw_list(m_gizmo_wire_render);
			}
		}
	}


		void DX12Renderer::render_fallback_triangle()
	{
		if (m_grid_visible) return;
		auto rtv = m_active_rtv;
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, nullptr);
		m_command_list->SetPipelineState(m_pipeline.pipeline_state());
		m_command_list->SetGraphicsRootSignature(m_pipeline.root_signature());
		m_command_list->IASetVertexBuffers(0, 1, &m_geometry.vertex_buffer_view());
		m_command_list->DrawInstanced(m_geometry.vertex_count(), 1, 0, 0);
	}


}
