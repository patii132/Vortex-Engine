#include "DX12Renderer_Internal.h"
#include "DX12ShaderCompiler.h"
#include <algorithm>
#include <unordered_map>

// SSAO (#32) — screen-space ambient occlusion, fully self-contained.
//
// Instead of making every path's main depth buffer sampleable (three different depth targets,
// LESS -> LESS_EQUAL flips on every PSO, skybox/grid depth interplay), the effect renders its OWN
// half-res depth prepass (camera VP straight from b0, rigid opaque casters only) into a dedicated
// R32_TYPELESS target, then:
//   AO pass    ssao.hlsl PSMain — reconstruct view-space position via InvProj, normal from
//              derivatives, 12-tap golden-angle spiral, Alchemy-style angle/distance weighting.
//   Blur pass  4-tap box into the blur RT, whose SRV lives in a RESERVED registry heap slot so
//              the scene pass can sample it mid-pass at t10 (root param 13).
// standard.hlsl multiplies the AO into the AMBIENT term only — direct light, fog and emissive
// stay untouched (the #32 acceptance criterion). Identical flow for the editor viewport, the
// standalone player and the editor's play window (slot 0/0/1).

namespace vortex::graphics::dx12
{
	namespace
	{
		struct SsaoFrustum { float p[6][4]; };

		SsaoFrustum extract_ssao_frustum(const DirectX::XMFLOAT4X4& m)
		{
			SsaoFrustum f = {
				{
					{ m._14 + m._11, m._24 + m._21, m._34 + m._31, m._44 + m._41 },
					{ m._14 - m._11, m._24 - m._21, m._34 - m._31, m._44 - m._41 },
					{ m._14 + m._12, m._24 + m._22, m._34 + m._32, m._44 + m._42 },
					{ m._14 - m._12, m._24 - m._22, m._34 - m._32, m._44 - m._42 },
					{ m._13,         m._23,         m._33,         m._43         },
					{ m._14 - m._13, m._24 - m._23, m._34 - m._33, m._44 - m._43 },
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

		bool sphere_in_ssao_frustum(const SsaoFrustum& f, float cx, float cy, float cz, float r)
		{
			for (int i = 0; i < 6; ++i)
			{
				float dist = f.p[i][0] * cx + f.p[i][1] * cy + f.p[i][2] * cz + f.p[i][3];
				if (dist < -r) return false;
			}
			return true;
		}

		// Byte-matched to ssao.hlsl's SsaoCB (a CBV at b0 since the bounce; it was 60 root constants).
		struct SsaoCB
		{
			DirectX::XMFLOAT4X4 inv_proj;
			float texel[2];
			float radius;
			float intensity;
			float bias;
			float proj_scale;
			float pad[2];
			// screen-space GI (#257 step 1)
			DirectX::XMFLOAT4X4 proj, view_to_prev_clip;
			float gi_strength, scene_exposure, prev_valid, gi_radius;
			// the bounce from the sun's shadow map (#257 step 2) — appended @240
			DirectX::XMFLOAT4X4 view_to_rsm, rsm_to_view;
			float rsm_tile[4], rsm_sun_view[4], rsm_flux[4];
		};
		static_assert(sizeof(SsaoCB) == 416, "SsaoCB must byte-match ssao.hlsl (a CBV at b0)");

		using PFN_D3D12SerializeRootSignature = HRESULT(WINAPI*)(const D3D12_ROOT_SIGNATURE_DESC*, D3D_ROOT_SIGNATURE_VERSION, ID3DBlob**, ID3DBlob**);
		PFN_D3D12SerializeRootSignature get_serialize_root_signature_ssao()
		{
			static auto fn = reinterpret_cast<PFN_D3D12SerializeRootSignature>(
				GetProcAddress(LoadLibraryW(L"d3d12.dll"), "D3D12SerializeRootSignature"));
			return fn;
		}
	}


	// Create (once) the SSAO root signature + PSOs + instance VB, and (per size change) the slot's
	// depth/AO/blur targets. The blur SRV is (re)written IN PLACE into the reserved registry slot,
	// so the scene pass's t10 table always points at something valid for this view.
	bool DX12Renderer::ensure_ssao_targets(int slot, u32 w, u32 h)
	{
		auto* dev = DX12Core::instance().device();
		if (!dev || slot < 0 || slot > 1) return false;

		u32 hw = w / 2, hh = h / 2;
		if (hw < 16) hw = 16;
		if (hh < 16) hh = 16;

		// ---- one-time pipeline objects ----
		if (!m_ssao_root_sig)
		{
			auto vs = DX12ShaderCompiler::load_shader("ssao", "vs", "VSMain", "vs_5_0");
			auto ps = DX12ShaderCompiler::load_shader("ssao", "ps", "PSMain", "ps_5_0");
			auto pb = DX12ShaderCompiler::load_shader("ssao", "ps_blur", "PSBlur", "ps_5_0");
			if (!vs || !ps || !pb) return false;

			D3D12_DESCRIPTOR_RANGE range{};
			range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
			range.NumDescriptors = 1;
			range.BaseShaderRegister = 0;
			D3D12_DESCRIPTOR_RANGE range_prev{};   // t1: the previous frame's colour (GI)
			range_prev.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
			range_prev.NumDescriptors = 1;
			range_prev.BaseShaderRegister = 1;

			D3D12_DESCRIPTOR_RANGE range_csm{};    // t2: the sun's cascade atlas (the bounce's emitters)
			range_csm.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
			range_csm.NumDescriptors = 1;
			range_csm.BaseShaderRegister = 2;

			D3D12_ROOT_PARAMETER params[4]{};
			params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
			params[0].DescriptorTable.NumDescriptorRanges = 1;
			params[0].DescriptorTable.pDescriptorRanges = &range;
			params[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
			params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;   // b0: 416 bytes outgrew the root-constant budget with the bounce
			params[1].Descriptor.ShaderRegister = 0;
			params[1].Descriptor.RegisterSpace = 0;
			params[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
			params[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
			params[2].DescriptorTable.NumDescriptorRanges = 1;
			params[2].DescriptorTable.pDescriptorRanges = &range_prev;
			params[2].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
			params[3].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
			params[3].DescriptorTable.NumDescriptorRanges = 1;
			params[3].DescriptorTable.pDescriptorRanges = &range_csm;
			params[3].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

			D3D12_STATIC_SAMPLER_DESC smp{};
			smp.Filter = D3D12_FILTER_MIN_MAG_MIP_POINT;   // depth must not be filtered
			smp.AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
			smp.AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
			smp.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
			smp.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

			D3D12_ROOT_SIGNATURE_DESC rs{};
			rs.NumParameters = 4;
			rs.pParameters = params;
			rs.NumStaticSamplers = 1;
			rs.pStaticSamplers = &smp;

			auto serialize = get_serialize_root_signature_ssao();
			if (!serialize) return false;
			ComPtr<ID3DBlob> blob, err;
			if (FAILED(serialize(&rs, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &err))) return false;
			if (FAILED(dev->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(),
				IID_PPV_ARGS(&m_ssao_root_sig)))) return false;

			D3D12_RASTERIZER_DESC rast{};
			rast.FillMode = D3D12_FILL_MODE_SOLID;
			rast.CullMode = D3D12_CULL_MODE_NONE;
			rast.DepthClipEnable = TRUE;

			D3D12_BLEND_DESC blend{};
			blend.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;

			D3D12_GRAPHICS_PIPELINE_STATE_DESC pso{};
			pso.pRootSignature = m_ssao_root_sig.Get();
			pso.VS = { vs->GetBufferPointer(), vs->GetBufferSize() };
			pso.PS = { ps->GetBufferPointer(), ps->GetBufferSize() };
			pso.BlendState = blend;
			pso.SampleMask = UINT_MAX;
			pso.RasterizerState = rast;
			pso.DepthStencilState.DepthEnable = FALSE;
			pso.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
			pso.NumRenderTargets = 1;
			pso.RTVFormats[0] = DXGI_FORMAT_R8G8B8A8_UNORM;   // rgb = GI, a = AO
			pso.DSVFormat = DXGI_FORMAT_UNKNOWN;
			pso.SampleDesc.Count = 1;
			if (FAILED(dev->CreateGraphicsPipelineState(&pso, IID_PPV_ARGS(&m_ssao_pso)))) return false;
			pso.PS = { pb->GetBufferPointer(), pb->GetBufferSize() };
			if (FAILED(dev->CreateGraphicsPipelineState(&pso, IID_PPV_ARGS(&m_ssao_blur_pso)))) return false;

			D3D12_HEAP_PROPERTIES up{}; up.Type = D3D12_HEAP_TYPE_UPLOAD;
			D3D12_RESOURCE_DESC bd{};
			bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
			bd.Width = (UINT64)64 * SSAO_MAX_INSTANCES;
			bd.Height = 1; bd.DepthOrArraySize = 1; bd.MipLevels = 1;
			bd.SampleDesc.Count = 1;
			bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
			if (FAILED(dev->CreateCommittedResource(&up, D3D12_HEAP_FLAG_NONE, &bd,
				D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_ssao_instance_vb)))) return false;
			D3D12_RANGE none{ 0, 0 };
			if (FAILED(m_ssao_instance_vb->Map(0, &none, &m_ssao_instance_vb_mapped))) return false;
		}

		SsaoView& v = m_ssao_view[slot];
		if (v.depth && v.w == hw && v.h == hh) return true;

		// One-time: reserve this slot's registry SRV handle (the scene pass binds the registry heap).
		if (m_ssao_reserved_cpu[slot].ptr == 0)
		{
			if (!ResourceRegistry::instance().reserve_srv_slot(m_ssao_reserved_cpu[slot], m_ssao_reserved_gpu[slot]))
				return false;
		}

		// Size change: the old targets may be referenced by an in-flight frame.
		if (v.depth) m_command_queue.flush();

		D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_DEFAULT;

		// Half-res AO depth (R32_TYPELESS: D32 DSV + R32F SRV — the shadow-atlas recipe).
		{
			D3D12_RESOURCE_DESC rd{};
			rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
			rd.Width = hw; rd.Height = hh;
			rd.DepthOrArraySize = 1; rd.MipLevels = 1;
			rd.Format = DXGI_FORMAT_R32_TYPELESS;
			rd.SampleDesc.Count = 1;
			rd.Flags = D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL;
			D3D12_CLEAR_VALUE cv{};
			cv.Format = DXGI_FORMAT_D32_FLOAT;
			cv.DepthStencil.Depth = 1.0f;
			v.depth.Reset();
			if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd,
				D3D12_RESOURCE_STATE_DEPTH_WRITE, &cv, IID_PPV_ARGS(&v.depth)))) return false;
			v.depth_state = D3D12_RESOURCE_STATE_DEPTH_WRITE;
		}

		// AO + GI color targets (RGBA8: rgb = screen-space GI, a = AO).
		{
			D3D12_RESOURCE_DESC rd{};
			rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
			rd.Width = hw; rd.Height = hh;
			rd.DepthOrArraySize = 1; rd.MipLevels = 1;
			rd.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
			rd.SampleDesc.Count = 1;
			rd.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
			v.ao.Reset(); v.blur.Reset();
			if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd,
				D3D12_RESOURCE_STATE_RENDER_TARGET, nullptr, IID_PPV_ARGS(&v.ao)))) return false;
			if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd,
				D3D12_RESOURCE_STATE_RENDER_TARGET, nullptr, IID_PPV_ARGS(&v.blur)))) return false;
			v.ao_state = D3D12_RESOURCE_STATE_RENDER_TARGET;
			v.blur_state = D3D12_RESOURCE_STATE_RENDER_TARGET;
		}

		// Descriptor heaps (created once, views rewritten in place on resize).
		if (!v.dsv_heap)
		{
			D3D12_DESCRIPTOR_HEAP_DESC dh{};
			dh.Type = D3D12_DESCRIPTOR_HEAP_TYPE_DSV; dh.NumDescriptors = 1;
			if (FAILED(dev->CreateDescriptorHeap(&dh, IID_PPV_ARGS(&v.dsv_heap)))) return false;
			dh.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV; dh.NumDescriptors = 2;
			if (FAILED(dev->CreateDescriptorHeap(&dh, IID_PPV_ARGS(&v.rtv_heap)))) return false;
			dh.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV; dh.NumDescriptors = 4;
			dh.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
			if (FAILED(dev->CreateDescriptorHeap(&dh, IID_PPV_ARGS(&v.srv_heap)))) return false;
			// the pass constants as a CBV (416 bytes outgrew the root-constant budget with the bounce): three 512-byte slots rotate
			D3D12_HEAP_PROPERTIES cup{}; cup.Type = D3D12_HEAP_TYPE_UPLOAD;
			D3D12_RESOURCE_DESC cbd{};
			cbd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER; cbd.Width = 3 * 512; cbd.Height = 1; cbd.DepthOrArraySize = 1; cbd.MipLevels = 1;
			cbd.SampleDesc.Count = 1; cbd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
			if (FAILED(dev->CreateCommittedResource(&cup, D3D12_HEAP_FLAG_NONE, &cbd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&v.cb)))) return false;
			D3D12_RANGE none{ 0, 0 }; void* mp = nullptr;
			if (FAILED(v.cb->Map(0, &none, &mp))) return false;
			v.cb_mapped = (u8*)mp;
		}
		const u32 rtv_inc = dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
		const u32 srv_inc = dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);

		D3D12_DEPTH_STENCIL_VIEW_DESC dsv{};
		dsv.Format = DXGI_FORMAT_D32_FLOAT;
		dsv.ViewDimension = D3D12_DSV_DIMENSION_TEXTURE2D;
		dev->CreateDepthStencilView(v.depth.Get(), &dsv, v.dsv_heap->GetCPUDescriptorHandleForHeapStart());

		auto rtv0 = v.rtv_heap->GetCPUDescriptorHandleForHeapStart();
		auto rtv1 = rtv0; rtv1.ptr += rtv_inc;
		dev->CreateRenderTargetView(v.ao.Get(), nullptr, rtv0);
		dev->CreateRenderTargetView(v.blur.Get(), nullptr, rtv1);

		// Pass-local SRVs (own shader-visible heap): [0] depth for the AO pass, [1] raw AO for the blur.
		D3D12_SHADER_RESOURCE_VIEW_DESC srv{};
		srv.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
		srv.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
		srv.Texture2D.MipLevels = 1;
		auto srv0 = v.srv_heap->GetCPUDescriptorHandleForHeapStart();
		auto srv1 = srv0; srv1.ptr += srv_inc;
		srv.Format = DXGI_FORMAT_R32_FLOAT;
		dev->CreateShaderResourceView(v.depth.Get(), &srv, srv0);
		srv.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
		dev->CreateShaderResourceView(v.ao.Get(), &srv, srv1);

		// The scene-pass-facing SRV (t10) in the RESERVED registry slot: the blurred result.
		dev->CreateShaderResourceView(v.blur.Get(), &srv, m_ssao_reserved_cpu[slot]);

		v.w = hw; v.h = hh;
		return true;
	}


	// Record the full SSAO chain for one view — called right after the shadow pass, BEFORE the 3D
	// color pass, on the already-recording command list. Leaves the blur RT in PSR for t10.
	void DX12Renderer::record_ssao(int slot, u32 view_w, u32 view_h)
	{
		using namespace DirectX;

		// Whatever happens below, the scene pass needs a valid t10 handle for this view.
		if (m_ssao_reserved_gpu[slot].ptr != 0) m_ssao_current_srv = m_ssao_reserved_gpu[slot];

		if (!m_ssao_enabled || !m_pipeline_3d.zprepass_pso() || view_w < 32 || view_h < 32) return;
		if (!ensure_ssao_targets(slot, view_w, view_h)) return;
		m_ssao_current_srv = m_ssao_reserved_gpu[slot];

		SsaoView& v = m_ssao_view[slot];
		auto& reg = ResourceRegistry::instance();

		// ---- cull + pack rigid opaque casters (camera frustum, single-threaded like shadows) ----
		struct Caster { id::id_type mesh; const XMFLOAT4X4* world; };
		std::vector<Caster> casters;
		std::unordered_map<id::id_type, XMFLOAT4> bounds;
		bounds.reserve(64);
		SsaoFrustum fr = extract_ssao_frustum(m_frame_constants.view_projection);
		for (const auto& item : m_render_queue)
		{
			if (item.bone_offset != NO_BONES) continue;   // v1: skinned meshes don't occlude in AO
			if (item.layer != 0) continue;   // #175: the viewmodel would rasterize at the WRONG projection here
			auto* cmat = reg.get_material(item.material_id);
			if (cmat && (cmat->blend_mode() == 1 || cmat->blend_mode() == 2)) continue;   // transparents neither
			XMFLOAT4 bd;
			auto bit = bounds.find(item.mesh_id);
			if (bit == bounds.end())
			{
				Mesh* mp = reg.get_mesh(item.mesh_id);
				float mnx = 0, mny = 0, mnz = 0, mxx = 1, mxy = 1, mxz = 1;
				if (mp && mp->is_valid()) { mp->get_min(mnx, mny, mnz); mp->get_max(mxx, mxy, mxz); }
				float dx = mxx - mnx, dy = mxy - mny, dz = mxz - mnz;
				bd = XMFLOAT4((mnx + mxx) * 0.5f, (mny + mxy) * 0.5f, (mnz + mxz) * 0.5f,
					0.5f * sqrtf(dx * dx + dy * dy + dz * dz));
				bounds.emplace(item.mesh_id, bd);
			}
			else bd = bit->second;
			const XMFLOAT4X4& W = item.world_matrix;
			XMVECTOR wc = XMVector3TransformCoord(XMVectorSet(bd.x, bd.y, bd.z, 1.f), XMLoadFloat4x4(&W));
			float sx = sqrtf(W._11 * W._11 + W._12 * W._12 + W._13 * W._13);
			float sy = sqrtf(W._21 * W._21 + W._22 * W._22 + W._23 * W._23);
			float sz = sqrtf(W._31 * W._31 + W._32 * W._32 + W._33 * W._33);
			float ms = sx > sy ? (sx > sz ? sx : sz) : (sy > sz ? sy : sz);
			if (sphere_in_ssao_frustum(fr, XMVectorGetX(wc), XMVectorGetY(wc), XMVectorGetZ(wc), bd.w * ms + 0.05f))
			{
				casters.push_back({ item.mesh_id, &item.world_matrix });
				if (casters.size() >= SSAO_MAX_INSTANCES) break;
			}
		}

		auto transition = [&](ID3D12Resource* res, D3D12_RESOURCE_STATES& state, D3D12_RESOURCE_STATES to)
		{
			if (state == to) return;
			D3D12_RESOURCE_BARRIER b{};
			b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
			b.Transition.pResource = res;
			b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
			b.Transition.StateBefore = state;
			b.Transition.StateAfter = to;
			m_command_list->ResourceBarrier(1, &b);
			state = to;
		};

		// ---- half-res depth prepass (camera VP straight from the already-uploaded b0) ----
		transition(v.depth.Get(), v.depth_state, D3D12_RESOURCE_STATE_DEPTH_WRITE);
		auto dsv = v.dsv_heap->GetCPUDescriptorHandleForHeapStart();
		m_command_list->ClearDepthStencilView(dsv, D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0, 0, nullptr);
		if (!casters.empty() && m_ssao_instance_vb_mapped)
		{
			m_command_list->OMSetRenderTargets(0, nullptr, FALSE, &dsv);
			m_command_list->SetPipelineState(m_pipeline_3d.zprepass_pso());
			m_command_list->SetGraphicsRootSignature(m_pipeline_3d.root_signature());
			m_command_list->SetGraphicsRootConstantBufferView(0, m_per_frame_cb->GetGPUVirtualAddress());
			m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
			D3D12_VIEWPORT vp{}; vp.Width = (float)v.w; vp.Height = (float)v.h; vp.MaxDepth = 1.0f;
			D3D12_RECT sc{}; sc.right = (LONG)v.w; sc.bottom = (LONG)v.h;
			m_command_list->RSSetViewports(1, &vp);
			m_command_list->RSSetScissorRects(1, &sc);

			std::sort(casters.begin(), casters.end(),
				[](const Caster& a, const Caster& b) { return a.mesh < b.mesh; });
			u8* vb = (u8*)m_ssao_instance_vb_mapped;
			for (size_t i = 0; i < casters.size(); ++i)
				memcpy(vb + i * 64, casters[i].world, 64);

			size_t i = 0;
			while (i < casters.size())
			{
				const id::id_type meshId = casters[i].mesh;
				size_t j = i + 1;
				while (j < casters.size() && casters[j].mesh == meshId) ++j;
				const u32 count = (u32)(j - i);
				Mesh* mesh = reg.get_mesh(meshId);
				if (mesh && mesh->is_valid())
				{
					D3D12_VERTEX_BUFFER_VIEW vbs[2];
					vbs[0] = mesh->vertex_buffer_view();
					vbs[1].BufferLocation = m_ssao_instance_vb->GetGPUVirtualAddress() + (UINT64)i * 64;
					vbs[1].SizeInBytes = count * 64;
					vbs[1].StrideInBytes = 64;
					m_command_list->IASetVertexBuffers(0, 2, vbs);
					if (mesh->has_indices())
					{
						m_command_list->IASetIndexBuffer(&mesh->index_buffer_view());
						m_command_list->DrawIndexedInstanced(mesh->index_count(), count, 0, 0, 0);
					}
					else
						m_command_list->DrawInstanced(mesh->vertex_count(), count, 0, 0);
					++m_draw_call_count; ++m_post_draw_count;
				}
				i = j;
			}
		}
		transition(v.depth.Get(), v.depth_state, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);

		// ---- AO + blur fullscreen passes ----
		// InvProj must invert the SAME projection update_per_frame_constants composed into the VP.
		const float aspect = (float)m_swapchain.width() / (float)(m_swapchain.height() ? m_swapchain.height() : 1);
		XMMATRIX proj = XMMatrixPerspectiveFovLH(XMConvertToRadians(m_fov_degrees), aspect, m_near_clip, m_far_clip);
		SsaoCB cb{};
		XMStoreFloat4x4(&cb.inv_proj, XMMatrixInverse(nullptr, proj));
		cb.texel[0] = 1.0f / (float)v.w;
		cb.texel[1] = 1.0f / (float)v.h;
		cb.radius = m_ssao_radius;
		cb.intensity = m_ssao_intensity;
		cb.bias = 0.015f;
		cb.proj_scale = 0.5f * XMVectorGetY(proj.r[1]) * (float)v.h;   // proj._22 * h/2
		// screen-space GI (#257 step 1): the previous frame's opaque colour copy at this view's size is the radiance source
		DX12Particles::DepthCopy* prev = (slot == 0 && m_gi_enabled) ? m_gi_prev_copy : nullptr;
		const bool gi_prev = prev && prev->texture && prev->w == m_gi_prev_w && prev->h == m_gi_prev_h && m_gi_prev_w == view_w && m_gi_prev_h == view_h;
		XMStoreFloat4x4(&cb.proj, proj);
		{
			XMMATRIX vp = XMLoadFloat4x4(&m_frame_constants.view_projection);
			XMMATRIX inv_vp = XMMatrixInverse(nullptr, vp);
			XMStoreFloat4x4(&cb.view_to_prev_clip, proj * inv_vp * XMLoadFloat4x4(&m_prev_view_projection));
		}
		cb.gi_strength = m_gi_enabled ? m_gi_strength : 0.0f;
		cb.scene_exposure = m_frame_constants.scene_exposure > 0.0f ? m_frame_constants.scene_exposure : 0.5f;
		cb.prev_valid = gi_prev ? 1.0f : 0.0f; cb.gi_radius = m_gi_radius;
		// the bounce from the sun's shadow map (#257 step 2): the far cascade (it holds every receiver and its surroundings) read
		// as a reflective shadow map — the ground's albedo x the sun as the emitters' radiance
		const bool rsm_on = slot == 0 && m_gi_enabled && m_csm_count > 0 && m_csm_map && m_directional_intensity > 0.001f && m_rsm_enabled;
		if (rsm_on)
		{
			const u32 c = m_csm_count - 1;
			XMMATRIX rsmVP = XMLoadFloat4x4(&m_csm_vp[c]);
			XMMATRIX vp = XMLoadFloat4x4(&m_frame_constants.view_projection);
			XMMATRIX inv_vp = XMMatrixInverse(nullptr, vp);
			XMStoreFloat4x4(&cb.view_to_rsm, proj * inv_vp * rsmVP);
			XMStoreFloat4x4(&cb.rsm_to_view, XMMatrixInverse(nullptr, rsmVP) * vp * XMMatrixInverse(nullptr, proj));
			cb.rsm_tile[0] = (c & 1) ? 0.5f : 0.0f; cb.rsm_tile[1] = (c & 2) ? 0.5f : 0.0f; cb.rsm_tile[2] = 0.5f; cb.rsm_tile[3] = 1.0f / (2.0f * (float)SHADOW_TILE_SIZE);
			XMVECTOR Lw = XMVector3Normalize(XMVectorNegate(XMLoadFloat3(&m_light_direction)));
			XMMATRIX V = XMMatrixLookAtLH(XMLoadFloat3(&m_camera_position), XMLoadFloat3(&m_camera_target), XMLoadFloat3(&m_camera_up));
			XMVECTOR Lv = XMVector3Normalize(XMVector3TransformNormal(Lw, V));
			const float radius_m = (std::max)(2.0f, m_gi_radius * 2.0f);
			const float cascade_r = m_csm_radius[c] > 0.5f ? m_csm_radius[c] : 0.5f;
			cb.rsm_sun_view[0] = XMVectorGetX(Lv); cb.rsm_sun_view[1] = XMVectorGetY(Lv); cb.rsm_sun_view[2] = XMVectorGetZ(Lv);
			cb.rsm_sun_view[3] = (std::min)(0.3f, radius_m / (2.0f * cascade_r));
			cb.rsm_flux[0] = m_gi_albedo.x * m_light_color.x * m_directional_intensity; cb.rsm_flux[1] = m_gi_albedo.y * m_light_color.y * m_directional_intensity;
			cb.rsm_flux[2] = m_gi_albedo.z * m_light_color.z * m_directional_intensity; cb.rsm_flux[3] = radius_m;
		}
		{
			// t2 of the AO pass: the sun's cascade atlas as a plain depth texture (the bounce's emitters)
			auto* dev3 = DX12Core::instance().device();
			const u32 inc3 = dev3->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
			auto srv3 = v.srv_heap->GetCPUDescriptorHandleForHeapStart(); srv3.ptr += 3 * inc3;
			D3D12_SHADER_RESOURCE_VIEW_DESC sd3{};
			sd3.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D; sd3.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING; sd3.Texture2D.MipLevels = 1;
			sd3.Format = DXGI_FORMAT_R32_FLOAT;
			if (rsm_on)
			{
				dev3->CreateShaderResourceView(m_csm_map.Get(), &sd3, srv3);
				if (m_csm_map_state != D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE)
				{
					D3D12_RESOURCE_BARRIER b{};
					b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION; b.Transition.pResource = m_csm_map.Get();
					b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES; b.Transition.StateBefore = m_csm_map_state; b.Transition.StateAfter = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
					m_command_list->ResourceBarrier(1, &b); m_csm_map_state = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
				}
			}
			else dev3->CreateShaderResourceView(v.depth.Get(), &sd3, srv3);   // a valid descriptor; the shader does not read it
		}
		{
			// t1 of the AO pass: the copy's SRV written into the pass-local heap (a shader-visible heap cannot be copied from)
			auto* dev2 = DX12Core::instance().device();
			const u32 inc2 = dev2->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
			auto srv2 = v.srv_heap->GetCPUDescriptorHandleForHeapStart(); srv2.ptr += 2 * inc2;
			D3D12_SHADER_RESOURCE_VIEW_DESC sd{};
			sd.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D; sd.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING; sd.Texture2D.MipLevels = 1;
			if (gi_prev)
			{
				sd.Format = prev->format;
				dev2->CreateShaderResourceView(prev->texture.Get(), &sd, srv2);
				if (prev->state != D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE)
				{
					D3D12_RESOURCE_BARRIER b{};
					b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION; b.Transition.pResource = prev->texture.Get();
					b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES; b.Transition.StateBefore = prev->state; b.Transition.StateAfter = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
					m_command_list->ResourceBarrier(1, &b); prev->state = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
				}
			}
			else
			{
				sd.Format = DXGI_FORMAT_R32_FLOAT;
				dev2->CreateShaderResourceView(v.depth.Get(), &sd, srv2);   // a valid descriptor; the shader does not read it
			}
		}

		m_command_list->SetGraphicsRootSignature(m_ssao_root_sig.Get());
		m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		ID3D12DescriptorHeap* heaps[] = { v.srv_heap.Get() };
		m_command_list->SetDescriptorHeaps(1, heaps);
		D3D12_VIEWPORT vp{}; vp.Width = (float)v.w; vp.Height = (float)v.h; vp.MaxDepth = 1.0f;
		D3D12_RECT sc{}; sc.right = (LONG)v.w; sc.bottom = (LONG)v.h;
		m_command_list->RSSetViewports(1, &vp);
		m_command_list->RSSetScissorRects(1, &sc);

		const u32 srv_inc = DX12Core::instance().device()->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
		auto srv_gpu0 = v.srv_heap->GetGPUDescriptorHandleForHeapStart();
		auto srv_gpu1 = srv_gpu0; srv_gpu1.ptr += srv_inc;
		auto srv_gpu2 = srv_gpu0; srv_gpu2.ptr += 2 * srv_inc;
		auto srv_gpu3 = srv_gpu0; srv_gpu3.ptr += 3 * srv_inc;
		v.cb_cursor = (v.cb_cursor + 1) % 3;
		memcpy(v.cb_mapped + (size_t)v.cb_cursor * 512, &cb, sizeof(cb));
		const D3D12_GPU_VIRTUAL_ADDRESS cb_va = v.cb->GetGPUVirtualAddress() + (UINT64)v.cb_cursor * 512;
		const u32 rtv_inc = DX12Core::instance().device()->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
		auto rtv0 = v.rtv_heap->GetCPUDescriptorHandleForHeapStart();
		auto rtv1 = rtv0; rtv1.ptr += rtv_inc;

		// AO generation: depth -> raw AO.
		transition(v.ao.Get(), v.ao_state, D3D12_RESOURCE_STATE_RENDER_TARGET);
		m_command_list->OMSetRenderTargets(1, &rtv0, FALSE, nullptr);
		m_command_list->SetPipelineState(m_ssao_pso.Get());
		m_command_list->SetGraphicsRootDescriptorTable(0, srv_gpu0);
		m_command_list->SetGraphicsRootDescriptorTable(2, srv_gpu2);
		m_command_list->SetGraphicsRootConstantBufferView(1, cb_va);
		m_command_list->SetGraphicsRootDescriptorTable(3, srv_gpu3);
		m_command_list->DrawInstanced(3, 1, 0, 0);

		// Blur: raw AO -> blurred AO (the texture the scene pass samples at t10).
		transition(v.ao.Get(), v.ao_state, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
		transition(v.blur.Get(), v.blur_state, D3D12_RESOURCE_STATE_RENDER_TARGET);
		m_command_list->OMSetRenderTargets(1, &rtv1, FALSE, nullptr);
		m_command_list->SetPipelineState(m_ssao_blur_pso.Get());
		m_command_list->SetGraphicsRootDescriptorTable(0, srv_gpu1);
		m_command_list->SetGraphicsRootDescriptorTable(2, srv_gpu2);
		m_command_list->SetGraphicsRootConstantBufferView(1, cb_va);
		m_command_list->SetGraphicsRootDescriptorTable(3, srv_gpu3);
		m_command_list->DrawInstanced(3, 1, 0, 0);

		transition(v.blur.Get(), v.blur_state, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
	}
}
