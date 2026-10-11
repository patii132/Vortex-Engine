#include "DX12Decals.h"
#include "DX12ShaderCompiler.h"
#include "../Resources/ResourceRegistry.h"
#include "../Resources/Material.h"
#include "../Resources/Texture.h"
#include <algorithm>
#include <cstring>
#include <string>

namespace vortex::graphics::dx12
{
	namespace
	{
		void log(const std::string& s) { OutputDebugStringA(("[decals] " + s + "\n").c_str()); }

		// byte-matched to DFrame / DBatch in decals.hlsl (and decals.metal / decals_common.glsl)
		struct DFrame
		{
			DirectX::XMFLOAT4X4 view_projection;
			DirectX::XMFLOAT4X4 inv_view_projection;
			float eye[4];
			float screen[4];
			float depth_params[4];
			float fog[4], fog2[4];
			float sun_dir[4], sun_color[4];
			u32 counts[4];
		};
		static_assert(sizeof(DFrame) == 256, "DFrame must byte-match decals.hlsl");

		struct DBatch
		{
			u32 base, blend, has_texture, pad;
			float base_color[4];
		};
		static_assert(sizeof(DBatch) == 32, "DBatch must byte-match decals.hlsl");

		constexpr u32 LIGHT_BYTES = 1024;
		enum : UINT { RP_FRAME = 0, RP_BATCH, RP_LIGHTS, RP_INSTANCES, RP_TEXTURE, RP_DEPTH, RP_COUNT };

		using PFN_D3D12SerializeRootSignature = HRESULT(WINAPI*)(const D3D12_ROOT_SIGNATURE_DESC*, D3D_ROOT_SIGNATURE_VERSION, ID3DBlob**, ID3DBlob**);
		PFN_D3D12SerializeRootSignature get_serialize_root_signature()
		{
			static auto fn = reinterpret_cast<PFN_D3D12SerializeRootSignature>(
				GetProcAddress(LoadLibraryW(L"d3d12.dll"), "D3D12SerializeRootSignature"));
			return fn;
		}
	}

	bool DX12Decals::initialize(ID3D12Device* device, DX12FrameRing* ring, DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format)
	{
		shutdown();
		m_device = device;
		m_ring = ring;
		if (!device || !ring) return false;
		m_vs = DX12ShaderCompiler::load_shader("decals", "vs", "DecalVS", "vs_5_0");
		m_ps = DX12ShaderCompiler::load_shader("decals", "ps", "DecalPS", "ps_5_0");
		if (!m_vs || !m_ps) { log("decals.hlsl did not compile — no decals on this backend"); shutdown(); return false; }
		if (!create_root_signature() || !create_pipelines(rtv_format, dsv_format)) { shutdown(); return false; }
		m_ready = true;
		log("ready (lit / multiply / additive box projection)");
		return true;
	}

	void DX12Decals::shutdown()
	{
		for (auto& p : m_pso) p.Reset();
		m_root_signature.Reset();
		m_vs.Reset(); m_ps.Reset();
		m_instances.clear(); m_batches.clear();
		m_ready = false;
		m_device = nullptr;
		m_ring = nullptr;
	}

	bool DX12Decals::create_root_signature()
	{
		auto serialize = get_serialize_root_signature();
		if (!serialize) return false;

		D3D12_DESCRIPTOR_RANGE tex_range{};
		tex_range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
		tex_range.NumDescriptors = 1;
		tex_range.BaseShaderRegister = 0;   // t0: the decal texture
		D3D12_DESCRIPTOR_RANGE depth_range = tex_range;
		depth_range.BaseShaderRegister = 1; // t1: the scene depth copy

		D3D12_ROOT_PARAMETER params[RP_COUNT]{};
		params[RP_FRAME].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
		params[RP_FRAME].Descriptor.ShaderRegister = 0;
		params[RP_FRAME].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
		params[RP_BATCH].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
		params[RP_BATCH].Descriptor.ShaderRegister = 1;
		params[RP_BATCH].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
		params[RP_LIGHTS].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
		params[RP_LIGHTS].Descriptor.ShaderRegister = 2;
		params[RP_LIGHTS].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
		params[RP_INSTANCES].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
		params[RP_INSTANCES].Descriptor.ShaderRegister = 2;
		params[RP_INSTANCES].ShaderVisibility = D3D12_SHADER_VISIBILITY_VERTEX;
		params[RP_TEXTURE].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
		params[RP_TEXTURE].DescriptorTable.NumDescriptorRanges = 1;
		params[RP_TEXTURE].DescriptorTable.pDescriptorRanges = &tex_range;
		params[RP_TEXTURE].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
		params[RP_DEPTH].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
		params[RP_DEPTH].DescriptorTable.NumDescriptorRanges = 1;
		params[RP_DEPTH].DescriptorTable.pDescriptorRanges = &depth_range;
		params[RP_DEPTH].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		D3D12_STATIC_SAMPLER_DESC sampler{};
		sampler.Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;   // s0: the decal texture, clamped (no repeat across the box)
		sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
		sampler.MaxLOD = D3D12_FLOAT32_MAX;
		sampler.ShaderRegister = 0;
		sampler.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		D3D12_ROOT_SIGNATURE_DESC desc{};
		desc.NumParameters = RP_COUNT;
		desc.pParameters = params;
		desc.NumStaticSamplers = 1;
		desc.pStaticSamplers = &sampler;
		desc.Flags = D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;

		ComPtr<ID3DBlob> signature, error;
		if (FAILED(serialize(&desc, D3D_ROOT_SIGNATURE_VERSION_1, &signature, &error)))
		{
			if (error) log(std::string("root signature: ") + static_cast<const char*>(error->GetBufferPointer()));
			return false;
		}
		return SUCCEEDED(m_device->CreateRootSignature(0, signature->GetBufferPointer(), signature->GetBufferSize(), IID_PPV_ARGS(&m_root_signature)));
	}

	bool DX12Decals::create_pipelines(DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format)
	{
		for (u32 b = 0; b < 3; ++b)
		{
			D3D12_GRAPHICS_PIPELINE_STATE_DESC d{};
			d.pRootSignature = m_root_signature.Get();
			d.VS = { m_vs->GetBufferPointer(), m_vs->GetBufferSize() };
			d.PS = { m_ps->GetBufferPointer(), m_ps->GetBufferSize() };
			d.SampleMask = UINT_MAX;
			d.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID;
			d.RasterizerState.CullMode = D3D12_CULL_MODE_FRONT;   // the box's back faces: visible from inside the box too
			d.RasterizerState.DepthClipEnable = TRUE;
			d.DepthStencilState.DepthEnable = FALSE;                // the covered pixel is tested against the box in the shader
			d.DepthStencilState.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ZERO;
			auto& rt = d.BlendState.RenderTarget[0];
			rt.BlendEnable = TRUE;
			rt.BlendOp = D3D12_BLEND_OP_ADD;
			rt.BlendOpAlpha = D3D12_BLEND_OP_ADD;
			rt.SrcBlendAlpha = D3D12_BLEND_ZERO;
			rt.DestBlendAlpha = D3D12_BLEND_ONE;
			rt.RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_RED | D3D12_COLOR_WRITE_ENABLE_GREEN | D3D12_COLOR_WRITE_ENABLE_BLUE;
			switch (b)
			{
			case decals::BLEND_MULTIPLY: rt.SrcBlend = D3D12_BLEND_ZERO; rt.DestBlend = D3D12_BLEND_SRC_COLOR; break;   // dst * src
			case decals::BLEND_ADDITIVE: rt.SrcBlend = D3D12_BLEND_ONE; rt.DestBlend = D3D12_BLEND_ONE; break;
			default: rt.SrcBlend = D3D12_BLEND_SRC_ALPHA; rt.DestBlend = D3D12_BLEND_INV_SRC_ALPHA; break;
			}
			d.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
			d.NumRenderTargets = 1;
			d.RTVFormats[0] = rtv_format;
			d.DSVFormat = dsv_format;   // the scene's depth stays bound (untouched)
			d.SampleDesc.Count = 1;
			if (FAILED(m_device->CreateGraphicsPipelineState(&d, IID_PPV_ARGS(&m_pso[b])))) { log("decal PSO failed"); return false; }
		}
		return true;
	}

	void DX12Decals::draw(ID3D12GraphicsCommandList* cmd, D3D12_CPU_DESCRIPTOR_HANDLE rtv, D3D12_CPU_DESCRIPTOR_HANDLE dsv,
		D3D12_GPU_DESCRIPTOR_HANDLE depth_srv, u32 w, u32 h, const View& view, const DX12Particles::Environment& env,
		D3D12_GPU_VIRTUAL_ADDRESS lights, const std::vector<decals::Decal>& list)
	{
		if (!m_ready || !cmd || !m_ring || depth_srv.ptr == 0 || w == 0 || h == 0) return;
		if (!decals::build(list, m_instances, m_batches)) return;

		const u64 ib = (u64)m_instances.size() * sizeof(decals::GpuDecal);
		DX12FrameRing::Upload inst = m_ring->alloc(ib);
		if (!inst.cpu) return;
		std::memcpy(inst.cpu, m_instances.data(), ib);

		DFrame f{};
		f.view_projection = view.view_projection;
		f.inv_view_projection = view.inv_view_projection;
		f.eye[0] = view.eye.x; f.eye[1] = view.eye.y; f.eye[2] = view.eye.z; f.eye[3] = 1.0f;
		f.screen[0] = (float)w; f.screen[1] = (float)h; f.screen[2] = 1.0f / (float)w; f.screen[3] = 1.0f / (float)h;
		f.depth_params[0] = view.near_clip; f.depth_params[1] = view.far_clip; f.depth_params[2] = view.ortho ? 1.0f : 0.0f;
		f.fog[0] = env.fog_color.x; f.fog[1] = env.fog_color.y; f.fog[2] = env.fog_color.z; f.fog[3] = env.fog_density;
		f.fog2[0] = env.fog_height_y; f.fog2[1] = env.fog_height_falloff; f.fog2[2] = env.scene_exposure; f.fog2[3] = env.fog_sun_glow;
		f.sun_dir[0] = env.sun_direction.x; f.sun_dir[1] = env.sun_direction.y; f.sun_dir[2] = env.sun_direction.z; f.sun_dir[3] = env.sun_intensity;
		f.sun_color[0] = env.sun_color.x; f.sun_color[1] = env.sun_color.y; f.sun_color[2] = env.sun_color.z; f.sun_color[3] = env.ambient;
		f.counts[0] = lights ? env.point_lights : 0; f.counts[1] = lights ? env.spot_lights : 0; f.counts[2] = env.tonemapper;
		DX12FrameRing::Upload fcb = m_ring->alloc(sizeof(DFrame));
		if (!fcb.cpu) return;
		std::memcpy(fcb.cpu, &f, sizeof(f));
		D3D12_GPU_VIRTUAL_ADDRESS lights_va = lights;
		if (!lights_va)
		{
			DX12FrameRing::Upload z = m_ring->alloc(LIGHT_BYTES);
			if (!z.cpu) return;
			std::memset(z.cpu, 0, LIGHT_BYTES);
			lights_va = z.gpu;
		}

		auto& reg = ResourceRegistry::instance();
		cmd->SetGraphicsRootSignature(m_root_signature.Get());
		if (auto* heap = reg.srv_heap()) { ID3D12DescriptorHeap* heaps[] = { heap }; cmd->SetDescriptorHeaps(1, heaps); }
		cmd->OMSetRenderTargets(1, &rtv, FALSE, &dsv);
		D3D12_VIEWPORT vp{ 0.0f, 0.0f, (float)w, (float)h, 0.0f, 1.0f };
		D3D12_RECT sc{ 0, 0, (LONG)w, (LONG)h };
		cmd->RSSetViewports(1, &vp);
		cmd->RSSetScissorRects(1, &sc);
		cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		cmd->SetGraphicsRootConstantBufferView(RP_FRAME, fcb.gpu);
		cmd->SetGraphicsRootConstantBufferView(RP_LIGHTS, lights_va);
		cmd->SetGraphicsRootShaderResourceView(RP_INSTANCES, inst.gpu);
		cmd->SetGraphicsRootDescriptorTable(RP_DEPTH, depth_srv);

		Texture* white = reg.get_texture(reg.default_white_texture());
		const D3D12_GPU_DESCRIPTOR_HANDLE white_srv = (white && white->is_valid() && white->srv_gpu().ptr != 0) ? white->srv_gpu() : depth_srv;
		ID3D12PipelineState* bound = nullptr;
		for (const auto& b : m_batches)
		{
			ID3D12PipelineState* p = m_pso[(std::min)(b.blend, 2u)].Get();
			if (!p) continue;
			if (p != bound) { cmd->SetPipelineState(p); bound = p; }
			Material* mat = b.material != id::invalid_id ? reg.get_material(b.material) : nullptr;
			Texture* t = mat ? mat->albedo_texture() : nullptr;
			const bool has_tex = t && t->is_valid() && t->srv_gpu().ptr != 0;
			DBatch pb{};
			pb.base = b.first;
			pb.blend = b.blend;
			pb.has_texture = has_tex ? 1u : 0u;
			if (mat) { const auto& bc = mat->properties().base_color; pb.base_color[0] = bc.x; pb.base_color[1] = bc.y; pb.base_color[2] = bc.z; pb.base_color[3] = bc.w; }
			else { pb.base_color[0] = pb.base_color[1] = pb.base_color[2] = pb.base_color[3] = 1.0f; }
			DX12FrameRing::Upload bcb = m_ring->alloc(sizeof(DBatch));
			if (!bcb.cpu) break;
			std::memcpy(bcb.cpu, &pb, sizeof(pb));
			cmd->SetGraphicsRootConstantBufferView(RP_BATCH, bcb.gpu);
			cmd->SetGraphicsRootDescriptorTable(RP_TEXTURE, has_tex ? t->srv_gpu() : white_srv);
			cmd->DrawInstanced(36, b.count, 0, 0);
		}
	}
}
