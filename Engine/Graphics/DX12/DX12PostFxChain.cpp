#include "DX12PostFxChain.h"
#include "DX12ShaderCompiler.h"

namespace vortex::graphics::dx12
{
	namespace
	{
		// d3d12.dll is loaded dynamically engine-wide (DX12Core) — resolve the serializer the same way
		// every other pipeline does instead of linking d3d12.lib.
		using PFN_D3D12SerializeRootSignature = HRESULT(WINAPI*)(const D3D12_ROOT_SIGNATURE_DESC*, D3D_ROOT_SIGNATURE_VERSION, ID3DBlob**, ID3DBlob**);
		PFN_D3D12SerializeRootSignature get_serialize_root_signature()
		{
			static auto fn = reinterpret_cast<PFN_D3D12SerializeRootSignature>(
				GetProcAddress(LoadLibraryW(L"d3d12.dll"), "D3D12SerializeRootSignature"));
			return fn;
		}
	}

	bool DX12PostFxChain::initialize(ID3D12Device* device, DXGI_FORMAT rtv_format, DX12CommandQueue* queue)
	{
		if (!device) return false;
		m_format = rtv_format;
		m_queue = queue;
		m_device = device;

		auto vs = DX12ShaderCompiler::load_shader("postfx", "vs", "VSMain", "vs_5_0");
		auto ps = DX12ShaderCompiler::load_shader("postfx", "ps", "PSMain", "ps_5_0");
		if (!vs || !ps) return false;

		// Root signature: t0 = source color (SRV table), b0 = the pass's parameter CB (root CBV),
		// t1 = second texture table (bloom composite; every pass binds SOMETHING valid there),
		// s0 = static linear-clamp sampler — the upscale pass's layout plus one CBV + one table.
		D3D12_DESCRIPTOR_RANGE srv_range{};
		srv_range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
		srv_range.NumDescriptors = 1;
		srv_range.BaseShaderRegister = 0;

		D3D12_DESCRIPTOR_RANGE srv_range1{};
		srv_range1.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
		srv_range1.NumDescriptors = 1;
		srv_range1.BaseShaderRegister = 1;

		D3D12_ROOT_PARAMETER params[3]{};
		params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
		params[0].DescriptorTable.NumDescriptorRanges = 1;
		params[0].DescriptorTable.pDescriptorRanges = &srv_range;
		params[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
		params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
		params[1].Descriptor.ShaderRegister = 0;
		params[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
		params[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
		params[2].DescriptorTable.NumDescriptorRanges = 1;
		params[2].DescriptorTable.pDescriptorRanges = &srv_range1;
		params[2].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		D3D12_STATIC_SAMPLER_DESC sampler{};
		sampler.Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
		sampler.AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
		sampler.AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
		sampler.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
		sampler.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		D3D12_ROOT_SIGNATURE_DESC rs{};
		rs.NumParameters = 3;
		rs.pParameters = params;
		rs.NumStaticSamplers = 1;
		rs.pStaticSamplers = &sampler;

		auto serialize = get_serialize_root_signature();
		if (!serialize) return false;
		ComPtr<ID3DBlob> blob, err;
		if (FAILED(serialize(&rs, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &err))) return false;
		if (FAILED(device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(),
			IID_PPV_ARGS(&m_root_signature)))) return false;

		D3D12_RASTERIZER_DESC rast{};
		rast.FillMode = D3D12_FILL_MODE_SOLID;
		rast.CullMode = D3D12_CULL_MODE_NONE;
		rast.DepthClipEnable = TRUE;

		D3D12_BLEND_DESC blend{};
		blend.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;

		D3D12_GRAPHICS_PIPELINE_STATE_DESC pso{};
		pso.pRootSignature = m_root_signature.Get();
		pso.VS = { vs->GetBufferPointer(), vs->GetBufferSize() };
		pso.PS = { ps->GetBufferPointer(), ps->GetBufferSize() };
		pso.BlendState = blend;
		pso.SampleMask = UINT_MAX;
		pso.RasterizerState = rast;
		pso.DepthStencilState.DepthEnable = FALSE;      // fullscreen pass — no depth
		pso.DepthStencilState.StencilEnable = FALSE;
		pso.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
		pso.NumRenderTargets = 1;
		pso.RTVFormats[0] = rtv_format;
		pso.DSVFormat = DXGI_FORMAT_UNKNOWN;
		pso.SampleDesc.Count = 1;
		if (FAILED(device->CreateGraphicsPipelineState(&pso, IID_PPV_ARGS(&m_pso)))) return false;

		// Bloom PSOs (#30) — same root signature, bloom.hlsl entry points, R11G11B10_FLOAT targets.
		// OPTIONAL like the chain itself: if the shader is missing the three PSOs stay null, active()
		// never reports bloom and everything else keeps working.
		auto bvs = DX12ShaderCompiler::load_shader("bloom", "vs", "VSMain", "vs_5_0");
		auto bpre = DX12ShaderCompiler::load_shader("bloom", "ps_pre", "PSPrefilter", "ps_5_0");
		auto bdown = DX12ShaderCompiler::load_shader("bloom", "ps_down", "PSDownsample", "ps_5_0");
		auto bup = DX12ShaderCompiler::load_shader("bloom", "ps_up", "PSUpsample", "ps_5_0");
		if (bvs && bpre && bdown && bup)
		{
			D3D12_GRAPHICS_PIPELINE_STATE_DESC bp = pso;   // clone the uber-pass desc
			bp.VS = { bvs->GetBufferPointer(), bvs->GetBufferSize() };
			bp.RTVFormats[0] = m_bloom_format;
			bp.PS = { bpre->GetBufferPointer(), bpre->GetBufferSize() };
			if (FAILED(device->CreateGraphicsPipelineState(&bp, IID_PPV_ARGS(&m_pso_bloom_pre))))
				m_pso_bloom_pre.Reset();
			bp.PS = { bdown->GetBufferPointer(), bdown->GetBufferSize() };
			if (FAILED(device->CreateGraphicsPipelineState(&bp, IID_PPV_ARGS(&m_pso_bloom_down))))
				m_pso_bloom_down.Reset();
			// Upsample accumulates INTO the finer mip: additive ONE/ONE blend over its downsample content.
			bp.PS = { bup->GetBufferPointer(), bup->GetBufferSize() };
			bp.BlendState.RenderTarget[0].BlendEnable = TRUE;
			bp.BlendState.RenderTarget[0].SrcBlend = D3D12_BLEND_ONE;
			bp.BlendState.RenderTarget[0].DestBlend = D3D12_BLEND_ONE;
			bp.BlendState.RenderTarget[0].BlendOp = D3D12_BLEND_OP_ADD;
			bp.BlendState.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND_ONE;
			bp.BlendState.RenderTarget[0].DestBlendAlpha = D3D12_BLEND_ONE;
			bp.BlendState.RenderTarget[0].BlendOpAlpha = D3D12_BLEND_OP_ADD;
			if (FAILED(device->CreateGraphicsPipelineState(&bp, IID_PPV_ARGS(&m_pso_bloom_up))))
				m_pso_bloom_up.Reset();
			if (!m_pso_bloom_pre || !m_pso_bloom_down)   // all three or none (bloom_requested gates on up)
				m_pso_bloom_up.Reset();
		}

		// Bloom descriptor heaps: RTVs for every mip of both slots, plus THE shared shader-visible
		// SRV heap (input RT + mips per slot) that lets one pass bind two textures at once.
		if (m_pso_bloom_up)
		{
			m_rtv_increment = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
			m_srv_increment = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);

			D3D12_DESCRIPTOR_HEAP_DESC rh{};
			rh.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
			rh.NumDescriptors = 2 * MAX_BLOOM_MIPS;
			if (FAILED(device->CreateDescriptorHeap(&rh, IID_PPV_ARGS(&m_bloom_rtv_heap))))
				m_pso_bloom_up.Reset();

			D3D12_DESCRIPTOR_HEAP_DESC sh{};
			sh.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
			sh.NumDescriptors = 2 * SHARED_SRVS_PER_SLOT;
			sh.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
			if (FAILED(device->CreateDescriptorHeap(&sh, IID_PPV_ARGS(&m_srv_heap_shared))))
				m_pso_bloom_up.Reset();
		}

		// One 256B CB slot per pass, persistently mapped — written once per record() like the light CB.
		D3D12_HEAP_PROPERTIES up{}; up.Type = D3D12_HEAP_TYPE_UPLOAD;
		D3D12_RESOURCE_DESC bd{};
		bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
		bd.Width = (UINT64)256 * CB_SLOTS;
		bd.Height = 1; bd.DepthOrArraySize = 1; bd.MipLevels = 1;
		bd.SampleDesc.Count = 1;
		bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
		if (FAILED(device->CreateCommittedResource(&up, D3D12_HEAP_FLAG_NONE, &bd,
			D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_cb)))) return false;
		D3D12_RANGE none{ 0, 0 };
		if (FAILED(m_cb->Map(0, &none, &m_cb_mapped))) return false;

		// VORTEX_POSTFX_TEST=1: boot with vignette AND the invert pass on — the headless verification
		// switch for the chain's multi-pass ping-pong (#28 AC). Two passes force input -> ping B ->
		// back buffer; a frame that is inverted AND bright-edged proves both ran in order.
		char test[8];
		DWORD tn = GetEnvironmentVariableA("VORTEX_POSTFX_TEST", test, sizeof(test));
		if (tn > 0 && tn < sizeof(test) && test[0] == '1')
		{
			m_params.vignette = true;
			m_params.debug_invert = true;
		}
		// VORTEX_GRADE_TEST=1: force a strong cold, desaturated grade (engine-path isolation for #31).
		DWORD gn = GetEnvironmentVariableA("VORTEX_GRADE_TEST", test, sizeof(test));
		if (gn > 0 && gn < sizeof(test) && test[0] == '1')
		{
			m_main_view = true;
			m_params.grade = true;
			m_params.exposure = -0.3f; m_params.contrast = 1.35f;
			m_params.saturation = 0.15f; m_params.temperature = -0.85f; m_params.tint = 0.0f;
		}
		// VORTEX_BLOOM_TEST=1: boot with an aggressive glow (engine-path isolation for #30 —
		// verifies the native mip chain + shared-heap composite with ANY scene, no settings needed).
		DWORD bn = GetEnvironmentVariableA("VORTEX_BLOOM_TEST", test, sizeof(test));
		if (bn > 0 && bn < sizeof(test) && test[0] == '1')
		{
			m_main_view = true;
			m_params.bloom = true;
			m_params.bloom_threshold = 0.45f; m_params.bloom_knee = 0.35f;
			m_params.bloom_intensity = 1.5f; m_params.bloom_scatter = 0.8f;
		}

		return true;
	}


	void DX12PostFxChain::shutdown()
	{
		for (int s = 0; s < 2; ++s)
			for (int p = 0; p < 2; ++p)
				m_rt[s][p].shutdown();
		for (int s = 0; s < 2; ++s)
		{
			for (u32 i = 0; i < MAX_BLOOM_MIPS; ++i) m_bloom_mip[s][i].tex.Reset();
			m_bloom_mips[s] = 0;
			m_shared_dirty[s] = true;
		}
		m_bloom_rtv_heap.Reset();
		m_srv_heap_shared.Reset();
		if (m_cb && m_cb_mapped) { m_cb->Unmap(0, nullptr); m_cb_mapped = nullptr; }
		m_cb.Reset();
		m_pso_bloom_pre.Reset();
		m_pso_bloom_down.Reset();
		m_pso_bloom_up.Reset();
		m_pso.Reset();
		m_root_signature.Reset();
		m_queue = nullptr;
		m_device = nullptr;
	}


	bool DX12PostFxChain::ensure_rt(ID3D12Device* device, int slot, int ping, u32 w, u32 h)
	{
		DX12RenderTarget& rt = m_rt[slot][ping];
		if (rt.is_initialized() && rt.width() == w && rt.height() == h) return true;
		// Size change / first use: the old RT may be referenced by an in-flight frame — idle the GPU
		// first (rare: window/scale changes only, same one-off stall as ensure_scaled_rt).
		if (rt.is_initialized() && m_queue) m_queue->flush();
		if (ping == 0) m_shared_dirty[slot] = true;   // input RT SRV in the shared heap goes stale
		if (!rt.is_initialized())
			return rt.initialize(device, w, h, m_format);   // color-only default depth is unused
		return rt.resize(device, w, h);
	}


	// ---- Bloom chain plumbing (#30) ----

	D3D12_CPU_DESCRIPTOR_HANDLE DX12PostFxChain::bloom_rtv(int slot, u32 mip) const
	{
		auto h = m_bloom_rtv_heap->GetCPUDescriptorHandleForHeapStart();
		h.ptr += (SIZE_T)((u32)slot * MAX_BLOOM_MIPS + mip) * m_rtv_increment;
		return h;
	}

	D3D12_CPU_DESCRIPTOR_HANDLE DX12PostFxChain::shared_srv_cpu(u32 index) const
	{
		auto h = m_srv_heap_shared->GetCPUDescriptorHandleForHeapStart();
		h.ptr += (SIZE_T)index * m_srv_increment;
		return h;
	}

	D3D12_GPU_DESCRIPTOR_HANDLE DX12PostFxChain::shared_srv_gpu(u32 index) const
	{
		auto h = m_srv_heap_shared->GetGPUDescriptorHandleForHeapStart();
		h.ptr += (UINT64)index * m_srv_increment;
		return h;
	}

	void DX12PostFxChain::mip_transition(ID3D12GraphicsCommandList* cmd, BloomMip& mip, D3D12_RESOURCE_STATES to)
	{
		if (mip.state == to || !mip.tex) return;
		D3D12_RESOURCE_BARRIER b{};
		b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
		b.Transition.pResource = mip.tex.Get();
		b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
		b.Transition.StateBefore = mip.state;
		b.Transition.StateAfter = to;
		cmd->ResourceBarrier(1, &b);
		mip.state = to;
	}

	// (Re)create the mip pyramid for `slot` to match a w x h output: level 0 = half res, halving
	// down to >= 8 px or MAX_BLOOM_MIPS. Cheap no-op when sizes already match.
	bool DX12PostFxChain::ensure_bloom_chain(int slot, u32 w, u32 h)
	{
		if (!m_device || !m_bloom_rtv_heap || !m_srv_heap_shared) return false;

		u32 sizes_w[MAX_BLOOM_MIPS], sizes_h[MAX_BLOOM_MIPS];
		u32 count = 0;
		u32 mw = w / 2, mh = h / 2;
		while (count < MAX_BLOOM_MIPS && mw >= 8 && mh >= 8)
		{
			sizes_w[count] = mw; sizes_h[count] = mh; ++count;
			mw /= 2; mh /= 2;
		}
		if (count == 0) return false;   // window too small — bloom silently off

		bool match = (m_bloom_mips[slot] == count);
		for (u32 i = 0; match && i < count; ++i)
			match = m_bloom_mip[slot][i].tex && m_bloom_mip[slot][i].w == sizes_w[i]
				&& m_bloom_mip[slot][i].h == sizes_h[i];
		if (match) return true;

		// Old mips may be referenced by an in-flight frame — same one-off idle as ensure_rt.
		if (m_bloom_mips[slot] > 0 && m_queue) m_queue->flush();

		for (u32 i = 0; i < MAX_BLOOM_MIPS; ++i) m_bloom_mip[slot][i].tex.Reset();
		m_bloom_mips[slot] = 0;

		D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_DEFAULT;
		for (u32 i = 0; i < count; ++i)
		{
			BloomMip& m = m_bloom_mip[slot][i];
			D3D12_RESOURCE_DESC td{};
			td.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
			td.Width = sizes_w[i];
			td.Height = sizes_h[i];
			td.DepthOrArraySize = 1;
			td.MipLevels = 1;
			td.Format = m_bloom_format;
			td.SampleDesc.Count = 1;
			td.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
			if (FAILED(m_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &td,
				D3D12_RESOURCE_STATE_RENDER_TARGET, nullptr, IID_PPV_ARGS(&m.tex))))
			{
				for (u32 k = 0; k <= i; ++k) m_bloom_mip[slot][k].tex.Reset();
				return false;
			}
			m.w = sizes_w[i]; m.h = sizes_h[i];
			m.state = D3D12_RESOURCE_STATE_RENDER_TARGET;

			D3D12_RENDER_TARGET_VIEW_DESC rd{};
			rd.Format = m_bloom_format;
			rd.ViewDimension = D3D12_RTV_DIMENSION_TEXTURE2D;
			m_device->CreateRenderTargetView(m.tex.Get(), &rd, bloom_rtv(slot, i));
		}
		m_bloom_mips[slot] = count;
		m_shared_dirty[slot] = true;
		return true;
	}

	// Rewrite the slot's SRVs in the shared heap. Only called after (re)creation, which either
	// happens before first GPU use or behind a queue flush — never racing an in-flight read.
	void DX12PostFxChain::write_shared_srvs(int slot)
	{
		const u32 base = (u32)slot * SHARED_SRVS_PER_SLOT;

		D3D12_SHADER_RESOURCE_VIEW_DESC sd{};
		sd.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
		sd.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
		sd.Texture2D.MipLevels = 1;

		sd.Format = m_format;
		m_device->CreateShaderResourceView(m_rt[slot][0].resource(), &sd, shared_srv_cpu(base + 0));

		sd.Format = m_bloom_format;
		for (u32 i = 0; i < m_bloom_mips[slot]; ++i)
			m_device->CreateShaderResourceView(m_bloom_mip[slot][i].tex.Get(), &sd,
				shared_srv_cpu(base + 1 + i));

		m_shared_dirty[slot] = false;
	}

	// Record the bloom mip sub-chain: input(slot) -> prefilter -> down ... -> up (additive) ...,
	// leaving mip 0 in PIXEL_SHADER_RESOURCE for the uber pass's t1 and the input RT in PSR for
	// its t0. Caller has set root signature + topology; PSOs/viewports are per-stage here.
	void DX12PostFxChain::record_bloom(ID3D12GraphicsCommandList* cmd, int slot, u32 w, u32 h)
	{
		const u32 base = (u32)slot * SHARED_SRVS_PER_SLOT;
		const u32 n = m_bloom_mips[slot];
		if (n == 0 || !m_cb_mapped) return;

		DX12RenderTarget& in = m_rt[slot][0];
		in.transition_to_shader_resource(cmd);

		if (m_shared_dirty[slot]) write_shared_srvs(slot);

		ID3D12DescriptorHeap* heaps[] = { m_srv_heap_shared.Get() };
		cmd->SetDescriptorHeaps(1, heaps);

		auto set_target = [&](u32 mip)
		{
			BloomMip& m = m_bloom_mip[slot][mip];
			mip_transition(cmd, m, D3D12_RESOURCE_STATE_RENDER_TARGET);
			D3D12_VIEWPORT vp{}; vp.Width = (float)m.w; vp.Height = (float)m.h; vp.MaxDepth = 1.0f;
			D3D12_RECT sc{}; sc.right = (LONG)m.w; sc.bottom = (LONG)m.h;
			cmd->RSSetViewports(1, &vp);
			cmd->RSSetScissorRects(1, &sc);
			auto rtv = bloom_rtv(slot, mip);
			cmd->OMSetRenderTargets(1, &rtv, FALSE, nullptr);
		};
		auto write_cb = [&](u32 cb_slot, float sw, float sh, float scale, float weight)
		{
			BloomCB bc{};
			bc.src_texel[0] = 1.0f / sw;
			bc.src_texel[1] = 1.0f / sh;
			bc.threshold = m_params.bloom_threshold;
			bc.knee = m_params.bloom_knee;
			bc.sample_scale = scale;
			bc.weight = weight;
			memcpy((u8*)m_cb_mapped + (SIZE_T)cb_slot * 256, &bc, sizeof(bc));
			cmd->SetGraphicsRootConstantBufferView(1, m_cb->GetGPUVirtualAddress() + (UINT64)cb_slot * 256);
		};

		// Bright-pass prefilter: full-res scene -> mip 0.
		cmd->SetPipelineState(m_pso_bloom_pre.Get());
		set_target(0);
		cmd->SetGraphicsRootDescriptorTable(0, shared_srv_gpu(base + 0));
		cmd->SetGraphicsRootDescriptorTable(2, shared_srv_gpu(base + 0));   // t1 unused; keep valid
		write_cb(CB_SLOT_PREFILTER, (float)w, (float)h, 1.0f, 1.0f);
		cmd->DrawInstanced(3, 1, 0, 0);

		// Progressive downsamples: mip i-1 -> mip i.
		cmd->SetPipelineState(m_pso_bloom_down.Get());
		for (u32 i = 1; i < n; ++i)
		{
			BloomMip& src = m_bloom_mip[slot][i - 1];
			mip_transition(cmd, src, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
			set_target(i);
			cmd->SetGraphicsRootDescriptorTable(0, shared_srv_gpu(base + 1 + (i - 1)));
			cmd->SetGraphicsRootDescriptorTable(2, shared_srv_gpu(base + 1 + (i - 1)));
			write_cb(CB_SLOT_DOWN + (i - 1), (float)src.w, (float)src.h, 1.0f, 1.0f);
			cmd->DrawInstanced(3, 1, 0, 0);
		}

		// Additive tent upsamples: mip i -> mip i-1 (scatter-weighted accumulate).
		cmd->SetPipelineState(m_pso_bloom_up.Get());
		for (u32 i = n - 1; i >= 1; --i)
		{
			BloomMip& src = m_bloom_mip[slot][i];
			mip_transition(cmd, src, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
			set_target(i - 1);
			cmd->SetGraphicsRootDescriptorTable(0, shared_srv_gpu(base + 1 + i));
			cmd->SetGraphicsRootDescriptorTable(2, shared_srv_gpu(base + 1 + i));
			write_cb(CB_SLOT_UP + (i - 1), (float)src.w, (float)src.h, 1.0f, m_params.bloom_scatter);
			cmd->DrawInstanced(3, 1, 0, 0);
		}

		// The uber pass samples mip 0 at t1.
		mip_transition(cmd, m_bloom_mip[slot][0], D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
	}


	DX12RenderTarget* DX12PostFxChain::acquire_input(ID3D12Device* device, u32 w, u32 h, int slot)
	{
		if (!m_pso || !device || w == 0 || h == 0 || slot < 0 || slot > 1) return nullptr;
		if (!ensure_rt(device, slot, 0, w, h)) return nullptr;
		return &m_rt[slot][0];
	}


	void DX12PostFxChain::record(ID3D12GraphicsCommandList* cmd, int slot, D3D12_CPU_DESCRIPTOR_HANDLE out_rtv,
		u32 w, u32 h, float time_seconds)
	{
		if (!m_pso || !cmd || slot < 0 || slot > 1 || !m_rt[slot][0].is_initialized()) return;

		// Bloom sub-chain (#30) runs first when requested AND its mip pyramid fits the output.
		bool bloom_on = bloom_requested() && ensure_bloom_chain(slot, w, h);

		// The enabled pass list, in chain order. Each entry is a CB slot; all share the uber-PSO.
		u32 passes[MAX_PASSES]; u32 n = 0;
		if (m_params.vignette || m_params.grain || m_params.ca || m_params.grade || m_params.fxaa || bloom_on) passes[n++] = 0;
		if (m_params.debug_invert) passes[n++] = 1;
		if (n == 0) return;   // renderer gates on active(), but stay safe against a mid-frame toggle

		if (m_cb_mapped)
		{
			PassCB cb{};
			cb.texel[0] = 1.0f / (float)w;
			cb.texel[1] = 1.0f / (float)h;
			cb.time = time_seconds;
			cb.flags = (m_params.vignette ? 1u : 0u) | (m_params.grain ? 2u : 0u) | (m_params.ca ? 4u : 0u)
				| (m_params.grade ? 16u : 0u) | (bloom_on ? 32u : 0u) | (m_params.fxaa ? 64u : 0u);
			cb.vignette[0] = m_params.vig_intensity;
			cb.vignette[1] = m_params.vig_smoothness;
			cb.vignette[2] = m_params.vig_roundness;
			cb.vignette_color[0] = m_params.vig_r;
			cb.vignette_color[1] = m_params.vig_g;
			cb.vignette_color[2] = m_params.vig_b;
			cb.grain_ca[0] = m_params.grain_intensity;
			cb.grain_ca[1] = m_params.grain_size;
			cb.grain_ca[2] = m_params.ca_strength;
			cb.grain_ca[3] = m_params.ca_falloff;
			cb.grade1[0] = m_params.exposure;
			cb.grade1[1] = m_params.contrast;
			cb.grade1[2] = m_params.saturation;
			cb.grade1[3] = m_params.temperature;
			cb.grade2[0] = m_params.tint;
			cb.bloom[0] = m_params.bloom_intensity;
			memcpy(m_cb_mapped, &cb, sizeof(cb));

			PassCB tb{};
			tb.texel[0] = cb.texel[0]; tb.texel[1] = cb.texel[1];
			tb.time = time_seconds;
			tb.flags = 8u;   // invert — the chain-verification pass
			memcpy((u8*)m_cb_mapped + 256, &tb, sizeof(tb));
		}

		cmd->SetGraphicsRootSignature(m_root_signature.Get());
		cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);

		// The mip sub-chain records its own PSOs/viewports and leaves mip 0 + the input RT in PSR.
		if (bloom_on) record_bloom(cmd, slot, w, h);

		cmd->SetPipelineState(m_pso.Get());

		D3D12_VIEWPORT vp{}; vp.Width = (float)w; vp.Height = (float)h; vp.MaxDepth = 1.0f;
		D3D12_RECT sc{}; sc.right = (LONG)w; sc.bottom = (LONG)h;
		cmd->RSSetViewports(1, &vp);
		cmd->RSSetScissorRects(1, &sc);

		DX12RenderTarget* src = &m_rt[slot][0];
		for (u32 k = 0; k < n; ++k)
		{
			bool last = (k == n - 1);
			DX12RenderTarget* dst = nullptr;
			D3D12_CPU_DESCRIPTOR_HANDLE dst_rtv = out_rtv;
			if (!last)
			{
				// Intermediate pass -> the other ping RT (created on first 2-pass frame).
				if (!m_device || !ensure_rt(m_device, slot, 1, w, h)) last = true;   // fall back: write out now
				else
				{
					dst = &m_rt[slot][1];
					dst->transition_to_render_target(cmd);
					dst_rtv = dst->rtv();
				}
			}

			src->transition_to_shader_resource(cmd);
			cmd->OMSetRenderTargets(1, &dst_rtv, FALSE, nullptr);
			if (bloom_on && passes[k] == 0)
			{
				// Uber pass with bloom: BOTH textures (scene t0 + bloom mip 0 t1) come from the
				// shared heap — the two-texture composite the per-RT single-slot heaps can't bind.
				const u32 base = (u32)slot * SHARED_SRVS_PER_SLOT;
				ID3D12DescriptorHeap* heaps[] = { m_srv_heap_shared.Get() };
				cmd->SetDescriptorHeaps(1, heaps);
				cmd->SetGraphicsRootDescriptorTable(0, shared_srv_gpu(base + 0));
				cmd->SetGraphicsRootDescriptorTable(2, shared_srv_gpu(base + 1));
			}
			else
			{
				ID3D12DescriptorHeap* heaps[] = { src->srv_heap() };
				cmd->SetDescriptorHeaps(1, heaps);
				cmd->SetGraphicsRootDescriptorTable(0, src->srv_gpu());
				cmd->SetGraphicsRootDescriptorTable(2, src->srv_gpu());   // t1 never sampled; keep the table valid
			}
			cmd->SetGraphicsRootConstantBufferView(1, m_cb->GetGPUVirtualAddress() + (UINT64)passes[k] * 256);
			cmd->DrawInstanced(3, 1, 0, 0);
			src->transition_to_render_target(cmd);   // leave every chain RT ready for the next frame

			if (last) break;
			src = dst;
		}
	}
}
