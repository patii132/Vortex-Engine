#include "DX12Renderer_Internal.h"
#include <algorithm>
#include <unordered_map>

// Spot-light shadow ATLAS (#23 — "the flashlight", and every other authored spot).
//
// Up to MAX_SHADOW_SPOTS (4) shadow-casting spots per frame share ONE depth atlas (2x2 tiles of
// SHADOW_TILE_SIZE²). Each shadow spot renders a depth-only sub-pass into its tile viewport; the
// pixel shader picks the tile via the per-spot shadow_slot packed into GPUSpotLight and the per-tile
// view-projections in the light buffer's ShadowVP[4] tail (b2 @1024 — the buffer was already 1280
// bytes wide, the tail fits exactly).
//
//  prepare_shadow_pass()  CPU side, inside update_per_frame_constants BEFORE the CB uploads:
//                         collects the first 4 submitted spots with cast_shadows (submit order =
//                         scene order — deterministic and user-controllable), builds their VPs and
//                         primes the per-tile b0 clones for the depth passes.
//  render_shadow_pass()   GPU side, right after the command-list reset: one atlas clear, then per
//                         shadow spot a cone-culled caster pack + instanced depth-only draws into
//                         its tile. The atlas then transitions to PSR for every scene pass (t7).
//
// State machine: the atlas has its own DEPTH_WRITE <-> PIXEL_SHADER_RESOURCE round-trip per frame,
// deliberately separate from m_scaled_rt's depth (which Frame Generation leaves in PSR across frames).

namespace vortex::graphics::dx12
{
	namespace
	{
		// Local copies of the frustum helpers — the originals live in DX12Renderer_3DScene.cpp's
		// anonymous namespace (same Gribb/Hartmann extraction for the row-major, mul(vec,mat) VP).
		struct ShadowFrustum { float p[6][4]; };

		ShadowFrustum extract_shadow_frustum(const DirectX::XMFLOAT4X4& m)
		{
			ShadowFrustum f = {
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

		bool sphere_in_shadow_frustum(const ShadowFrustum& f, float cx, float cy, float cz, float r)
		{
			for (int i = 0; i < 6; ++i)
			{
				float dist = f.p[i][0] * cx + f.p[i][1] * cy + f.p[i][2] * cz + f.p[i][3];
				if (dist < -r) return false;
			}
			return true;
		}

		// Spot light view-projection: FOV = the FULL outer cone angle (SpotAngle travels end-to-end in
		// degrees; the shader gates at cos(angle/2), so the full angle IS the perspective FOV). Far
		// plane = range (attenuation + the dist<range gate zero the light beyond it anyway).
		bool build_spot_vp(const DX12Renderer::SpotLightData& s, DirectX::XMFLOAT4X4& out)
		{
			using namespace DirectX;
			XMVECTOR dir = XMVector3Normalize(XMLoadFloat3(&s.direction));
			if (XMVectorGetX(XMVector3LengthSq(dir)) < 0.5f) return false;   // zero direction
			XMVECTOR pos = XMLoadFloat3(&s.position);
			XMVECTOR up = fabsf(XMVectorGetY(dir)) > 0.99f ? XMVectorSet(0, 0, 1, 0) : XMVectorSet(0, 1, 0, 0);
			float fovY = XMConvertToRadians(s.spot_angle < 1.0f ? 1.0f : (s.spot_angle > 175.0f ? 175.0f : s.spot_angle));
			float farZ = s.range > 0.1f ? s.range : 0.1f;
			XMMATRIX view = XMMatrixLookToLH(pos, dir, up);
			XMMATRIX proj = XMMatrixPerspectiveFovLH(fovY, 1.0f, 0.05f, farZ);
			XMStoreFloat4x4(&out, view * proj);   // same row-major/no-transpose treatment as view_projection
			return true;
		}
	}


	bool DX12Renderer::ensure_shadow_map(u32 size)
	{
		if (size < 512) size = 512; else if (size > 16384) size = 16384;
		if (m_shadow_map && m_shadow_map_size == size) return true;

		auto* dev = DX12Core::instance().device();
		if (!dev) return false;

		// The map may be referenced by an in-flight frame — idle the GPU before recreating.
		if (m_shadow_map) m_command_queue.flush();

		// One-time: reserve the SRV slot in ResourceRegistry's SHARED shader-visible heap. The scene pass
		// binds that heap, and only one CBV_SRV_UAV heap can be bound — a private per-resource heap could
		// never be sampled mid-scene-pass. The slot survives recreation (view recreated in place below).
		if (m_shadow_srv_cpu.ptr == 0)
		{
			if (!ResourceRegistry::instance().reserve_srv_slot(m_shadow_srv_cpu, m_shadow_srv_gpu))
				return false;
		}

		// R32_TYPELESS so the same resource carries a D32_FLOAT DSV (render) and an R32_FLOAT SRV (sample)
		// — the same sampleable-depth recipe DX12RenderTarget uses for the DLSS depth input.
		D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_DEFAULT;
		D3D12_RESOURCE_DESC rd{};
		rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
		rd.Width = size; rd.Height = size;
		rd.DepthOrArraySize = 1; rd.MipLevels = 1;
		rd.Format = DXGI_FORMAT_R32_TYPELESS;
		rd.SampleDesc.Count = 1;
		rd.Flags = D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL;

		D3D12_CLEAR_VALUE cv{};
		cv.Format = DXGI_FORMAT_D32_FLOAT;
		cv.DepthStencil.Depth = 1.0f;

		m_shadow_map.Reset();
		if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd,
			D3D12_RESOURCE_STATE_DEPTH_WRITE, &cv, IID_PPV_ARGS(&m_shadow_map))))
		{
			OutputDebugStringA("[shadows] shadow atlas creation failed\n");
			return false;
		}
		m_shadow_map_state = D3D12_RESOURCE_STATE_DEPTH_WRITE;
		m_shadow_map_size = size;

		if (!m_shadow_dsv_heap)
		{
			D3D12_DESCRIPTOR_HEAP_DESC dh{};
			dh.Type = D3D12_DESCRIPTOR_HEAP_TYPE_DSV;
			dh.NumDescriptors = 1;
			if (FAILED(dev->CreateDescriptorHeap(&dh, IID_PPV_ARGS(&m_shadow_dsv_heap))))
				return false;
		}
		D3D12_DEPTH_STENCIL_VIEW_DESC dsv{};
		dsv.Format = DXGI_FORMAT_D32_FLOAT;   // typed view over the typeless resource
		dsv.ViewDimension = D3D12_DSV_DIMENSION_TEXTURE2D;
		dev->CreateDepthStencilView(m_shadow_map.Get(), &dsv, m_shadow_dsv_heap->GetCPUDescriptorHandleForHeapStart());

		D3D12_SHADER_RESOURCE_VIEW_DESC srv{};
		srv.Format = DXGI_FORMAT_R32_FLOAT;
		srv.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
		srv.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
		srv.Texture2D.MipLevels = 1;
		dev->CreateShaderResourceView(m_shadow_map.Get(), &srv, m_shadow_srv_cpu);

		// Lazily created support buffers (once): the per-tile light-VP b0 clones + the caster instance VB.
		if (!m_shadow_pass_cb)
		{
			D3D12_HEAP_PROPERTIES up{}; up.Type = D3D12_HEAP_TYPE_UPLOAD;
			D3D12_RESOURCE_DESC bd{}; bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
			bd.Height = 1; bd.DepthOrArraySize = 1; bd.MipLevels = 1; bd.SampleDesc.Count = 1;
			bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
			D3D12_RANGE none{ 0, 0 };

			// Spot tiles + cascade tiles (#24) + point face tiles (#25).
			bd.Width = (UINT64)256 * (MAX_SHADOW_SPOTS + CSM_CASCADES + MAX_SHADOW_POINTS * 6);
			if (FAILED(dev->CreateCommittedResource(&up, D3D12_HEAP_FLAG_NONE, &bd,
				D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_shadow_pass_cb)))) return false;
			if (FAILED(m_shadow_pass_cb->Map(0, &none, &m_shadow_pass_cb_mapped))) return false;

			bd.Width = (UINT64)64 * MAX_SHADOW_INSTANCES;
			if (FAILED(dev->CreateCommittedResource(&up, D3D12_HEAP_FLAG_NONE, &bd,
				D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_shadow_instance_vb)))) return false;
			if (FAILED(m_shadow_instance_vb->Map(0, &none, &m_shadow_instance_vb_mapped))) return false;

			// PerObject constants of the cut-out caster segments (#329)
			bd.Width = (UINT64)256 * MAX_SHADOW_CUT_SEGS;
			if (FAILED(dev->CreateCommittedResource(&up, D3D12_HEAP_FLAG_NONE, &bd,
				D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_shadow_cut_cb)))) return false;
			if (FAILED(m_shadow_cut_cb->Map(0, &none, &m_shadow_cut_cb_mapped))) return false;
		}

		OutputDebugStringA("[shadows] shadow atlas ready\n");
		return true;
	}


	// #24: the directional cascade atlas — the exact ensure_shadow_map recipe with its own resource,
	// DSV heap, reserved SRV slot (t8) and state tracking. 2x2 tiles; cascade c renders into tile c.
	bool DX12Renderer::ensure_csm_map(u32 size)
	{
		if (size < 512) size = 512; else if (size > 16384) size = 16384;
		if (m_csm_map && m_csm_map_size == size) return true;

		auto* dev = DX12Core::instance().device();
		if (!dev) return false;

		if (m_csm_map) m_command_queue.flush();

		if (m_csm_srv_cpu.ptr == 0)
		{
			if (!ResourceRegistry::instance().reserve_srv_slot(m_csm_srv_cpu, m_csm_srv_gpu))
				return false;
		}

		D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_DEFAULT;
		D3D12_RESOURCE_DESC rd{};
		rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
		rd.Width = size; rd.Height = size;
		rd.DepthOrArraySize = 1; rd.MipLevels = 1;
		rd.Format = DXGI_FORMAT_R32_TYPELESS;
		rd.SampleDesc.Count = 1;
		rd.Flags = D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL;

		D3D12_CLEAR_VALUE cv{};
		cv.Format = DXGI_FORMAT_D32_FLOAT;
		cv.DepthStencil.Depth = 1.0f;

		m_csm_map.Reset();
		if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd,
			D3D12_RESOURCE_STATE_DEPTH_WRITE, &cv, IID_PPV_ARGS(&m_csm_map))))
		{
			OutputDebugStringA("[shadows] CSM atlas creation failed\n");
			return false;
		}
		m_csm_map_state = D3D12_RESOURCE_STATE_DEPTH_WRITE;
		m_csm_map_size = size;

		if (!m_csm_dsv_heap)
		{
			D3D12_DESCRIPTOR_HEAP_DESC dh{};
			dh.Type = D3D12_DESCRIPTOR_HEAP_TYPE_DSV;
			dh.NumDescriptors = 1;
			if (FAILED(dev->CreateDescriptorHeap(&dh, IID_PPV_ARGS(&m_csm_dsv_heap))))
				return false;
		}
		D3D12_DEPTH_STENCIL_VIEW_DESC dsv{};
		dsv.Format = DXGI_FORMAT_D32_FLOAT;
		dsv.ViewDimension = D3D12_DSV_DIMENSION_TEXTURE2D;
		dev->CreateDepthStencilView(m_csm_map.Get(), &dsv, m_csm_dsv_heap->GetCPUDescriptorHandleForHeapStart());

		D3D12_SHADER_RESOURCE_VIEW_DESC srv{};
		srv.Format = DXGI_FORMAT_R32_FLOAT;
		srv.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
		srv.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
		srv.Texture2D.MipLevels = 1;
		dev->CreateShaderResourceView(m_csm_map.Get(), &srv, m_csm_srv_cpu);

		OutputDebugStringA("[shadows] CSM atlas ready\n");
		return true;
	}


	// #25: the point-light face atlas — 4x3 grid of POINT_SHADOW_TILE² tiles (2 lights x 6 faces).
	bool DX12Renderer::ensure_point_shadow_map()
	{
		if (m_point_shadow_map) return true;

		auto* dev = DX12Core::instance().device();
		if (!dev) return false;

		if (m_point_srv_cpu.ptr == 0)
		{
			if (!ResourceRegistry::instance().reserve_srv_slot(m_point_srv_cpu, m_point_srv_gpu))
				return false;
		}

		D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_DEFAULT;
		D3D12_RESOURCE_DESC rd{};
		rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
		rd.Width = (UINT64)POINT_ATLAS_COLS * POINT_SHADOW_TILE;                       // 4096
		rd.Height = ((MAX_SHADOW_POINTS * 6 + POINT_ATLAS_COLS - 1) / POINT_ATLAS_COLS) * POINT_SHADOW_TILE; // 3072
		rd.DepthOrArraySize = 1; rd.MipLevels = 1;
		rd.Format = DXGI_FORMAT_R32_TYPELESS;
		rd.SampleDesc.Count = 1;
		rd.Flags = D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL;

		D3D12_CLEAR_VALUE cv{};
		cv.Format = DXGI_FORMAT_D32_FLOAT;
		cv.DepthStencil.Depth = 1.0f;

		if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd,
			D3D12_RESOURCE_STATE_DEPTH_WRITE, &cv, IID_PPV_ARGS(&m_point_shadow_map))))
		{
			OutputDebugStringA("[shadows] point shadow atlas creation failed\n");
			return false;
		}
		m_point_shadow_state = D3D12_RESOURCE_STATE_DEPTH_WRITE;

		if (!m_point_shadow_dsv_heap)
		{
			D3D12_DESCRIPTOR_HEAP_DESC dh{};
			dh.Type = D3D12_DESCRIPTOR_HEAP_TYPE_DSV;
			dh.NumDescriptors = 1;
			if (FAILED(dev->CreateDescriptorHeap(&dh, IID_PPV_ARGS(&m_point_shadow_dsv_heap))))
				return false;
		}
		D3D12_DEPTH_STENCIL_VIEW_DESC dsv{};
		dsv.Format = DXGI_FORMAT_D32_FLOAT;
		dsv.ViewDimension = D3D12_DSV_DIMENSION_TEXTURE2D;
		dev->CreateDepthStencilView(m_point_shadow_map.Get(), &dsv, m_point_shadow_dsv_heap->GetCPUDescriptorHandleForHeapStart());

		D3D12_SHADER_RESOURCE_VIEW_DESC srv{};
		srv.Format = DXGI_FORMAT_R32_FLOAT;
		srv.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
		srv.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
		srv.Texture2D.MipLevels = 1;
		dev->CreateShaderResourceView(m_point_shadow_map.Get(), &srv, m_point_srv_cpu);

		m_point_shadow_ready = true;
		OutputDebugStringA("[shadows] point shadow atlas ready\n");
		return true;
	}


	// #25: select the first shadow-requesting point lights + build their 6 face VPs (LookToLH along
	// ±X/±Y/±Z with the standard cube-face up vectors, 90° perspective, far = light range).
	void DX12Renderer::prepare_point_shadows()
	{
		using namespace DirectX;

		m_shadow_point_count = 0;
		if (!m_pipeline_3d.shadow_pso() || !m_point_shadow_ready) return;

		{
			char buf[8];
			DWORD n = GetEnvironmentVariableA("VORTEX_NO_POINT_SHADOWS", buf, sizeof(buf));
			if (n > 0 && n < sizeof(buf) && buf[0] == '1') return;
		}

		static const XMVECTORF32 kDirs[6] = {
			{ { {  1,  0,  0, 0 } } }, { { { -1,  0,  0, 0 } } },
			{ { {  0,  1,  0, 0 } } }, { { {  0, -1,  0, 0 } } },
			{ { {  0,  0,  1, 0 } } }, { { {  0,  0, -1, 0 } } },
		};
		static const XMVECTORF32 kUps[6] = {
			{ { { 0, 1,  0, 0 } } }, { { { 0, 1, 0, 0 } } },
			{ { { 0, 0, -1, 0 } } }, { { { 0, 0, 1, 0 } } },
			{ { { 0, 1,  0, 0 } } }, { { { 0, 1, 0, 0 } } },
		};

		for (size_t i = 0; i < m_point_lights.size() && i < MAX_POINT_LIGHTS
			&& m_shadow_point_count < MAX_SHADOW_POINTS; ++i)
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
				XMMATRIX view = XMMatrixLookToLH(pos, kDirs[f], kUps[f]);
				XMStoreFloat4x4(&spn.face_vp[f], view * proj);
				if (m_shadow_pass_cb_mapped)
				{
					PerFrameConstants sc = m_frame_constants;
					sc.view_projection = spn.face_vp[f];
					memcpy((u8*)m_shadow_pass_cb_mapped
						+ (size_t)(MAX_SHADOW_SPOTS + CSM_CASCADES + m_shadow_point_count * 6 + f) * 256,
						&sc, sizeof(sc));
				}
			}
			m_shadow_point_count++;
		}
	}


	// #24: cascade splits + stable snapped ortho crops. Called from prepare_shadow_pass (so BOTH the
	// editor frame and the game-window path get it, before the b2 upload that carries the VPs).
	void DX12Renderer::prepare_csm_cascades()
	{
		using namespace DirectX;

		m_csm_count = 0;
		if (!m_pipeline_3d.shadow_pso() || !m_csm_map) return;
		if (!m_dir_cast_shadows || m_dir_shadow_strength <= 0.0f || m_directional_intensity <= 0.001f) return;

		// Kill switch for A/B verification captures (mirrors VORTEX_NO_SPOT_SHADOWS).
		{
			char buf[8];
			DWORD n = GetEnvironmentVariableA("VORTEX_NO_DIR_SHADOWS", buf, sizeof(buf));
			if (n > 0 && n < sizeof(buf) && buf[0] == '1') return;
		}

		XMVECTOR lightDir = XMLoadFloat3(&m_light_direction);
		if (XMVectorGetX(XMVector3LengthSq(lightDir)) < 1e-6f) return;
		lightDir = XMVector3Normalize(lightDir);
		XMVECTOR lightUp = fabsf(XMVectorGetY(lightDir)) > 0.99f ? XMVectorSet(0, 0, 1, 0) : XMVectorSet(0, 1, 0, 0);
		// Rotation-only light view (positioned at the origin — the ortho crop supplies the extents).
		XMMATRIX lightView = XMMatrixLookToLH(XMVectorZero(), lightDir, lightUp);

		// Practical split scheme over [near, shadow distance]: blend of logarithmic (resolution where
		// it counts, near the camera) and uniform (no absurdly thin far cascades). near/far MUST match
		// update_per_frame_constants' hardcoded projection (0.1 / 1000) for consistent world distances.
		const float nearZ = 0.1f;
		const float farZ = m_dir_shadow_distance;
		const float lambda = 0.6f;
		float splitNear = nearZ;

		// Camera basis for slicing the view frustum (same inputs update_per_frame_constants uses).
		XMVECTOR eye = XMLoadFloat3(&m_camera_position);
		XMVECTOR fwd = XMVector3Normalize(XMVectorSubtract(XMLoadFloat3(&m_camera_target), eye));
		if (XMVectorGetX(XMVector3LengthSq(fwd)) < 1e-6f) return;
		XMVECTOR camUp = XMLoadFloat3(&m_camera_up);
		XMVECTOR right = XMVector3Normalize(XMVector3Cross(camUp, fwd));
		XMVECTOR upv = XMVector3Cross(fwd, right);
		const float aspect = (float)m_swapchain.width() / (float)(m_swapchain.height() ? m_swapchain.height() : 1);
		const float tanHalfY = tanf(XMConvertToRadians(m_fov_degrees) * 0.5f);
		const float tanHalfX = tanHalfY * aspect;

		for (u32 c = 0; c < CSM_CASCADES; ++c)
		{
			const float f = (float)(c + 1) / (float)CSM_CASCADES;
			const float logSplit = nearZ * powf(farZ / nearZ, f);
			const float uniSplit = nearZ + (farZ - nearZ) * f;
			const float splitFar = uniSplit + lambda * (logSplit - uniSplit);

			// 8 world-space corners of this frustum slice.
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

			// Circumsphere (fixed radius per slice geometry): the ortho crop size then NEVER changes
			// as the camera rotates — combined with texel snapping below, this kills the shimmer.
			XMVECTOR centroid = XMVectorZero();
			for (int i = 0; i < 8; ++i) centroid = XMVectorAdd(centroid, corners[i]);
			centroid = XMVectorScale(centroid, 1.0f / 8.0f);
			float radius = 0.0f;
			for (int i = 0; i < 8; ++i)
				radius = (std::max)(radius, XMVectorGetX(XMVector3Length(XMVectorSubtract(corners[i], centroid))));
			radius = ceilf(radius * 16.0f) / 16.0f;   // quantize so FP noise can't wiggle the crop

			// Snap the crop center to whole shadow-map texels in light view space.
			XMVECTOR cLS = XMVector3TransformCoord(centroid, lightView);
			const float worldPerTexel = (2.0f * radius) / (float)SHADOW_TILE_SIZE;
			float cx = floorf(XMVectorGetX(cLS) / worldPerTexel) * worldPerTexel;
			float cy = floorf(XMVectorGetY(cLS) / worldPerTexel) * worldPerTexel;
			const float cz = XMVectorGetZ(cLS);

			// Ortho crop; near pulled back toward the light so casters ABOVE/BEHIND the slice
			// (a roof over the corridor, a tower off-screen) still land in the depth map.
			XMMATRIX proj = XMMatrixOrthographicOffCenterLH(
				cx - radius, cx + radius, cy - radius, cy + radius,
				cz - radius - m_dir_shadow_distance, cz + radius);
			XMStoreFloat4x4(&m_csm_vp[c], lightView * proj);
			m_csm_splits[c] = splitFar;
			m_csm_radius[c] = radius;

			// The depth pass's b0 clone for this cascade (slots after the spot tiles).
			if (m_shadow_pass_cb_mapped)
			{
				PerFrameConstants sc = m_frame_constants;
				sc.view_projection = m_csm_vp[c];
				memcpy((u8*)m_shadow_pass_cb_mapped + (size_t)(MAX_SHADOW_SPOTS + c) * 256, &sc, sizeof(sc));
			}
			splitNear = splitFar;
		}
		m_csm_count = CSM_CASCADES;
	}


	void DX12Renderer::prepare_shadow_pass()
	{
		using namespace DirectX;

		m_shadow_spot_count = 0;
		m_frame_constants.shadow_map_texel = 1.0f / (float)SHADOW_TILE_SIZE;

		// Directional cascades (#24) + point cube shadows (#25) — prepared BEFORE the spot
		// early-outs (own kill switches, own atlases): a missing spot atlas or
		// VORTEX_NO_SPOT_SHADOWS must not leave stale cascade/point state.
		prepare_csm_cascades();
		prepare_point_shadows();

		if (!m_pipeline_3d.shadow_pso() || !m_shadow_map) return;

		// Debug/support kill switch: VORTEX_NO_SPOT_SHADOWS=1 disables all spot shadows at runtime —
		// used for A/B verification captures and as an escape hatch if a scene misbehaves.
		{
			char buf[8];
			DWORD n = GetEnvironmentVariableA("VORTEX_NO_SPOT_SHADOWS", buf, sizeof(buf));
			if (n > 0 && n < sizeof(buf) && buf[0] == '1') return;
		}

		// Collect up to MAX_SHADOW_SPOTS shadow-requesting spots in SUBMIT (= scene) order. Both the
		// flashlight AND authored scene spots get a tile; extras beyond 4 simply render unshadowed.
		for (size_t i = 0; i < m_spot_lights.size() && i < MAX_SPOT_LIGHTS && m_shadow_spot_count < MAX_SHADOW_SPOTS; ++i)
		{
			const SpotLightData& s = m_spot_lights[i];
			if (!s.cast_shadows || s.shadow_strength <= 0.0f) continue;

			XMFLOAT4X4 vp;
			if (!build_spot_vp(s, vp)) continue;

			ShadowSpot& slot = m_shadow_spots[m_shadow_spot_count];
			slot.spot_index = (int)i;
			slot.vp = vp;

			// The depth pass's b0 for this tile: this frame's constants with the LIGHT's VP swapped in.
			// The standard VS only reads view_projection from b0, the rest is along for the ride.
			if (m_shadow_pass_cb_mapped)
			{
				PerFrameConstants sc = m_frame_constants;
				sc.view_projection = vp;
				memcpy((u8*)m_shadow_pass_cb_mapped + (size_t)m_shadow_spot_count * 256, &sc, sizeof(sc));
			}
			m_shadow_spot_count++;
		}
	}


	void DX12Renderer::render_shadow_pass()
	{
		using namespace DirectX;
		if ((m_shadow_spot_count == 0 && m_csm_count == 0 && m_shadow_point_count == 0)
			|| !m_shadow_instance_vb_mapped) return;

		auto& reg = ResourceRegistry::instance();

		m_command_list->SetPipelineState(m_pipeline_3d.shadow_pso());
		m_command_list->SetGraphicsRootSignature(m_pipeline_3d.root_signature());
		m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);

		// ---- gather the casters ONCE per frame (#363) ----
		// The per-tile pack used to scan the whole render queue — a map lookup, a matrix transform and a sphere test per
		// item, for up to 19 tiles (4 spots, 3 cascades, 2 x 6 point faces): 19 x N per frame, single-threaded. Now the
		// filter and the world-space bounding sphere are computed once; each tile runs the sphere test only.
		if (reg.mesh_generation() != m_shadow_bounds_generation) { m_shadow_bounds.clear(); m_shadow_bounds_generation = reg.mesh_generation(); }
		auto& all = m_shadow_casters; all.clear();
		for (const auto& item : m_render_queue)
		{
			if (item.bone_offset != NO_BONES) continue;   // v1: skinned meshes receive but don't cast
			if (item.layer != 0) continue;                // #175: the viewmodel receives but never casts
			// Transparent materials (#33) don't cast: an alpha-blended ghost/glass pane throwing a
			// fully SOLID shadow reads as a bug (real tinted-glass shadows need translucent maps — v2).
			{
				auto* cmat = reg.get_material(item.material_id);
				if (cmat && (cmat->blend_mode() == 1 || cmat->blend_mode() == 2)) continue;
			}
			XMFLOAT4 bd;
			auto bit = m_shadow_bounds.find(item.mesh_id);
			if (bit == m_shadow_bounds.end())
			{
				Mesh* mp = reg.get_mesh(item.mesh_id);
				float mnx = 0, mny = 0, mnz = 0, mxx = 1, mxy = 1, mxz = 1;
				if (mp && mp->is_valid()) { mp->get_min(mnx, mny, mnz); mp->get_max(mxx, mxy, mxz); }
				float dx = mxx - mnx, dy = mxy - mny, dz = mxz - mnz;
				bd = XMFLOAT4((mnx + mxx) * 0.5f, (mny + mxy) * 0.5f, (mnz + mxz) * 0.5f,
					0.5f * sqrtf(dx * dx + dy * dy + dz * dz));
				m_shadow_bounds.emplace(item.mesh_id, bd);
			}
			else bd = bit->second;
			const XMFLOAT4X4& W = item.world_matrix;
			XMVECTOR wc = XMVector3TransformCoord(XMVectorSet(bd.x, bd.y, bd.z, 1.f), XMLoadFloat4x4(&W));
			float sx = sqrtf(W._11 * W._11 + W._12 * W._12 + W._13 * W._13);
			float sy = sqrtf(W._21 * W._21 + W._22 * W._22 + W._23 * W._23);
			float sz = sqrtf(W._31 * W._31 + W._32 * W._32 + W._33 * W._33);
			float ms = sx > sy ? (sx > sz ? sx : sz) : (sy > sz ? sy : sz);
			// an alpha-tested material casts the shape of its texture (#329) when the cut-out shadow PSO exists
			Material* cmat = reg.get_material(item.material_id);
			const bool cut = cmat && cmat->blend_mode() == 3 && cmat->properties().alpha_cutoff > 0.0f && m_pipeline_3d.shadow_cut_pso() && m_shadow_cut_cb_mapped;
			all.push_back({ item.mesh_id, &item.world_matrix, cmat, cut, XMVectorGetX(wc), XMVectorGetY(wc), XMVectorGetZ(wc), bd.w * ms + 0.05f });
		}

		auto& casters = m_shadow_tile_casters;
		u32 vb_used = 0;   // running offset into the shared shadow instance VB (across ALL tiles, all atlases)
		u32 dropped = 0;
		u32 cut_slot = 0;  // PerObject slots used by cut-out segments this frame (#329)
		ID3D12PipelineState* bound_pso = m_pipeline_3d.shadow_pso();   // set at the top of the pass; cut segments switch and back

		// ---- cull one tile's casters (shared by spot, cascade and point tiles) ----
		// Own pack per tile: the scene's instance packing is keyed to the MAIN camera frustum and
		// runs later — the wall BEHIND the player must still cast into the light frustum.
		auto pack_casters = [&](const ShadowFrustum& fr)
		{
			casters.clear();
			for (const auto& c : all)
				if (sphere_in_shadow_frustum(fr, c.cx, c.cy, c.cz, c.r)) casters.push_back(c);
			// The tiles share one instance buffer. A tile over budget keeps its LARGEST casters (the shadows one sees from
			// afar) instead of the first in queue order, and the drop is reported once the frame is packed.
			const u32 room = vb_used < MAX_SHADOW_INSTANCES ? MAX_SHADOW_INSTANCES - vb_used : 0;
			if (casters.size() > room)
			{
				std::nth_element(casters.begin(), casters.begin() + room, casters.end(), [](const ShadowCaster& a, const ShadowCaster& b) { return a.r > b.r; });
				dropped += (u32)(casters.size() - room);
				casters.resize(room);
			}
		};

		// ---- record one tile: pack into the shared VB, tile viewport, b0 clone, instanced draws ----
		// Pixel-origin + size parameters so spot/cascade tiles (2048²) and point face tiles (1024²)
		// share the identical body.
		auto draw_tile = [&](u32 pxX, u32 pxY, u32 tileSize, u32 cbSlot)
		{
			if (casters.empty()) return;   // empty tile stays cleared -> fully lit, correct

			// solid casters first, grouped by mesh; cut-out casters after them, grouped by mesh AND material (#329)
			std::sort(casters.begin(), casters.end(), [](const ShadowCaster& a, const ShadowCaster& b)
			{
				if (a.cut != b.cut) return !a.cut;
				if (a.mesh != b.mesh) return a.mesh < b.mesh;
				return a.cut && a.mat < b.mat;
			});
			u8* vb = (u8*)m_shadow_instance_vb_mapped;
			for (size_t i = 0; i < casters.size(); ++i)
				memcpy(vb + (size_t)(vb_used + i) * 64, casters[i].world, 64);

			const LONG tx = (LONG)pxX;
			const LONG ty = (LONG)pxY;
			D3D12_VIEWPORT vp{}; vp.TopLeftX = (float)tx; vp.TopLeftY = (float)ty; vp.Width = (float)tileSize; vp.Height = (float)tileSize; vp.MaxDepth = 1.0f;
			D3D12_RECT sc{}; sc.left = tx; sc.top = ty; sc.right = tx + (LONG)tileSize; sc.bottom = ty + (LONG)tileSize;
			m_command_list->RSSetViewports(1, &vp);
			m_command_list->RSSetScissorRects(1, &sc);
			m_command_list->SetGraphicsRootConstantBufferView(0, m_shadow_pass_cb->GetGPUVirtualAddress() + (UINT64)cbSlot * 256);

			size_t i = 0;
			while (i < casters.size())
			{
				const id::id_type meshId = casters[i].mesh;
				size_t j = i + 1;
				while (j < casters.size() && casters[j].mesh == meshId && casters[j].cut == casters[i].cut
					&& (!casters[i].cut || casters[j].mat == casters[i].mat)) ++j;
				const u32 count = (u32)(j - i);

				Mesh* mesh = reg.get_mesh(meshId);
				if (mesh && mesh->is_valid())
				{
					// cut-out segment (#329): the cut PSO, the material's PerObject (cutoff, tiling, base alpha) at root
					// param 1 and its albedo at the texture table (root param 3, as the main pass binds it)
					ID3D12PipelineState* want = casters[i].cut ? m_pipeline_3d.shadow_cut_pso() : m_pipeline_3d.shadow_pso();
					if (want != bound_pso) { m_command_list->SetPipelineState(want); bound_pso = want; }
					if (casters[i].cut && cut_slot < MAX_SHADOW_CUT_SEGS)
					{
						PerObjectConstants obj{};
						const auto& props = casters[i].mat->properties();
						obj.base_color = props.base_color; obj.uv_tiling = props.uv_tiling; obj.alpha_cutoff = props.alpha_cutoff;
						Texture* at = casters[i].mat->albedo_texture();
						if (at && at->is_valid() && at->srv_gpu().ptr != 0) { obj.has_albedo_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(3, at->srv_gpu()); }
						memcpy((u8*)m_shadow_cut_cb_mapped + (size_t)cut_slot * 256, &obj, sizeof(obj));
						m_command_list->SetGraphicsRootConstantBufferView(1, m_shadow_cut_cb->GetGPUVirtualAddress() + (UINT64)cut_slot * 256);
						++cut_slot;
					}
					D3D12_VERTEX_BUFFER_VIEW vbs[2];
					vbs[0] = mesh->vertex_buffer_view();
					vbs[1].BufferLocation = m_shadow_instance_vb->GetGPUVirtualAddress() + (UINT64)(vb_used + i) * 64;
					vbs[1].SizeInBytes = count * 64;
					vbs[1].StrideInBytes = 64;
					m_command_list->IASetVertexBuffers(0, 2, vbs);
					if (mesh->has_indices())
					{
						m_command_list->IASetIndexBuffer(&mesh->index_buffer_view());
						m_command_list->DrawIndexedInstanced(mesh->index_count(), count, 0, 0, 0);
					}
					else
					{
						m_command_list->DrawInstanced(mesh->vertex_count(), count, 0, 0);
					}
					++m_draw_call_count; ++m_shadow_draw_count;
				}
				i = j;
			}
			vb_used += (u32)casters.size();
		};

		// ---- directional cascade atlas (#24): t8 — packed FIRST so the sun never loses casters to the spot tiles (#363) ----
		if (m_csm_count > 0 && m_csm_map)
		{
			if (m_csm_map_state != D3D12_RESOURCE_STATE_DEPTH_WRITE)
			{
				D3D12_RESOURCE_BARRIER b{};
				b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
				b.Transition.pResource = m_csm_map.Get();
				b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
				b.Transition.StateBefore = m_csm_map_state;
				b.Transition.StateAfter = D3D12_RESOURCE_STATE_DEPTH_WRITE;
				m_command_list->ResourceBarrier(1, &b);
				m_csm_map_state = D3D12_RESOURCE_STATE_DEPTH_WRITE;
			}

			auto cdsv = m_csm_dsv_heap->GetCPUDescriptorHandleForHeapStart();
			m_command_list->ClearDepthStencilView(cdsv, D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0, 0, nullptr);
			m_command_list->OMSetRenderTargets(0, nullptr, FALSE, &cdsv);

			for (u32 c = 0; c < m_csm_count; ++c)
			{
				pack_casters(extract_shadow_frustum(m_csm_vp[c]));
				draw_tile((c & 1) * SHADOW_TILE_SIZE, ((c >> 1) & 1) * SHADOW_TILE_SIZE,
					SHADOW_TILE_SIZE, MAX_SHADOW_SPOTS + c);
			}

			D3D12_RESOURCE_BARRIER b{};
			b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
			b.Transition.pResource = m_csm_map.Get();
			b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
			b.Transition.StateBefore = D3D12_RESOURCE_STATE_DEPTH_WRITE;
			b.Transition.StateAfter = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
			m_command_list->ResourceBarrier(1, &b);
			m_csm_map_state = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
		}

		// ---- spot atlas (#23): t7 ----
		if (m_shadow_spot_count > 0 && m_shadow_map)
		{
			if (m_shadow_map_state != D3D12_RESOURCE_STATE_DEPTH_WRITE)
			{
				D3D12_RESOURCE_BARRIER b{};
				b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
				b.Transition.pResource = m_shadow_map.Get();
				b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
				b.Transition.StateBefore = m_shadow_map_state;
				b.Transition.StateAfter = D3D12_RESOURCE_STATE_DEPTH_WRITE;
				m_command_list->ResourceBarrier(1, &b);
				m_shadow_map_state = D3D12_RESOURCE_STATE_DEPTH_WRITE;
			}

			auto dsv = m_shadow_dsv_heap->GetCPUDescriptorHandleForHeapStart();
			m_command_list->ClearDepthStencilView(dsv, D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0, 0, nullptr);   // whole atlas once
			m_command_list->OMSetRenderTargets(0, nullptr, FALSE, &dsv);   // depth-only: no RTV

			for (u32 t = 0; t < m_shadow_spot_count; ++t)
			{
				pack_casters(extract_shadow_frustum(m_shadow_spots[t].vp));
				draw_tile((t & 1) * SHADOW_TILE_SIZE, ((t >> 1) & 1) * SHADOW_TILE_SIZE, SHADOW_TILE_SIZE, t);
			}

			// Hand the atlas to the pixel shaders (t7) for every scene pass this frame.
			D3D12_RESOURCE_BARRIER b{};
			b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
			b.Transition.pResource = m_shadow_map.Get();
			b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
			b.Transition.StateBefore = D3D12_RESOURCE_STATE_DEPTH_WRITE;
			b.Transition.StateAfter = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
			m_command_list->ResourceBarrier(1, &b);
			m_shadow_map_state = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
		}

		// ---- point-light face atlas (#25): t9 — 6 perspective tiles per shadowed light ----
		if (m_shadow_point_count > 0 && m_point_shadow_map)
		{
			if (m_point_shadow_state != D3D12_RESOURCE_STATE_DEPTH_WRITE)
			{
				D3D12_RESOURCE_BARRIER b{};
				b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
				b.Transition.pResource = m_point_shadow_map.Get();
				b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
				b.Transition.StateBefore = m_point_shadow_state;
				b.Transition.StateAfter = D3D12_RESOURCE_STATE_DEPTH_WRITE;
				m_command_list->ResourceBarrier(1, &b);
				m_point_shadow_state = D3D12_RESOURCE_STATE_DEPTH_WRITE;
			}

			auto pdsv = m_point_shadow_dsv_heap->GetCPUDescriptorHandleForHeapStart();
			m_command_list->ClearDepthStencilView(pdsv, D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0, 0, nullptr);
			m_command_list->OMSetRenderTargets(0, nullptr, FALSE, &pdsv);

			for (u32 p = 0; p < m_shadow_point_count; ++p)
			{
				for (u32 f = 0; f < 6; ++f)
				{
					pack_casters(extract_shadow_frustum(m_shadow_points[p].face_vp[f]));
					const u32 tile = p * 6 + f;
					draw_tile((tile % POINT_ATLAS_COLS) * POINT_SHADOW_TILE,
						(tile / POINT_ATLAS_COLS) * POINT_SHADOW_TILE,
						POINT_SHADOW_TILE,
						MAX_SHADOW_SPOTS + CSM_CASCADES + tile);
				}
			}

			D3D12_RESOURCE_BARRIER b{};
			b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
			b.Transition.pResource = m_point_shadow_map.Get();
			b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
			b.Transition.StateBefore = D3D12_RESOURCE_STATE_DEPTH_WRITE;
			b.Transition.StateAfter = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
			m_command_list->ResourceBarrier(1, &b);
			m_point_shadow_state = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
		}
		if (dropped > 0)
		{
			const auto now = std::chrono::steady_clock::now();
			if (now - m_shadow_drop_logged > std::chrono::seconds(5))
			{
				m_shadow_drop_logged = now;
				OutputDebugStringA(("[dx12] shadows: " + std::to_string(dropped) + " caster(s) dropped this frame — the shared shadow instance budget of "
					+ std::to_string(MAX_SHADOW_INSTANCES) + " is full (cascades packed first, largest casters kept) (#363)\n").c_str());
			}
		}
	}
}
