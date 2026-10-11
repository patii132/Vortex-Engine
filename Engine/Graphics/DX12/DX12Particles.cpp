#include "DX12Particles.h"
#include "DX12ShaderCompiler.h"
#include "../Resources/ResourceRegistry.h"
#include "../Resources/Texture.h"
#include <algorithm>
#include <cstring>
#include <string>

namespace vortex::graphics::dx12
{
	namespace
	{
		void log(const std::string& s) { OutputDebugStringA(("[particles] " + s + "\n").c_str()); }

		// byte-matched to PFrame / PBatch / SnapCB in particles.hlsl (and particles.metal)
		struct PFrame
		{
			DirectX::XMFLOAT4X4 view_projection;
			float cam_right[4], cam_up[4], cam_forward[4], eye[4];
			float depth_params[4];
			float fog[4], fog2[4];
			float sun_dir[4], sun_color[4];
			u32 counts[4];
		};
		static_assert(sizeof(PFrame) == 224, "PFrame must byte-match particles.hlsl");

		struct PBatch
		{
			u32 base, mode, tiles_x, tiles_y;
			u32 frame_blend, lit, blend, has_texture;
			float soft_inv, emissive, pad0, pad1;
		};
		static_assert(sizeof(PBatch) == 48, "PBatch must byte-match particles.hlsl");

		struct SnapCB { float depth_params[4]; float src_size[2]; float dst_size[2]; };
		static_assert(sizeof(SnapCB) == 32, "SnapCB must byte-match particles.hlsl");

		constexpr u32 LIGHT_BYTES = 1024;   // point (16 x 32) + spot (8 x 64) lights lead the renderer's light buffer
		constexpr u32 MODE_RIBBON = 16;

		// root parameters (particles.hlsl registers)
		enum : UINT { RP_FRAME = 0, RP_BATCH, RP_LIGHTS, RP_INSTANCES, RP_TEXTURE, RP_DEPTH, RP_SNAP, RP_COUNT };

		using PFN_D3D12SerializeRootSignature = HRESULT(WINAPI*)(const D3D12_ROOT_SIGNATURE_DESC*, D3D_ROOT_SIGNATURE_VERSION, ID3DBlob**, ID3DBlob**);
		PFN_D3D12SerializeRootSignature get_serialize_root_signature()
		{
			static auto fn = reinterpret_cast<PFN_D3D12SerializeRootSignature>(
				GetProcAddress(LoadLibraryW(L"d3d12.dll"), "D3D12SerializeRootSignature"));
			return fn;
		}

		void barrier(ID3D12GraphicsCommandList* cmd, ID3D12Resource* res, D3D12_RESOURCE_STATES from, D3D12_RESOURCE_STATES to)
		{
			if (!res || from == to) return;
			D3D12_RESOURCE_BARRIER b{};
			b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
			b.Transition.pResource = res;
			b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
			b.Transition.StateBefore = from;
			b.Transition.StateAfter = to;
			cmd->ResourceBarrier(1, &b);
		}

		void fill_view(particles::DepthView& dv, const DX12Particles::View& view)
		{
			dv.eye[0] = view.eye.x; dv.eye[1] = view.eye.y; dv.eye[2] = view.eye.z;
			dv.right[0] = view.right.x; dv.right[1] = view.right.y; dv.right[2] = view.right.z;
			dv.up[0] = view.up.x; dv.up[1] = view.up.y; dv.up[2] = view.up.z;
			dv.forward[0] = view.forward.x; dv.forward[1] = view.forward.y; dv.forward[2] = view.forward.z;
			dv.tan_half_x = view.tan_half_x; dv.tan_half_y = view.tan_half_y;
			dv.near_clip = view.near_clip; dv.far_clip = view.far_clip; dv.ortho = view.ortho;
		}
	}

	// ---------------------------------------------------------------------------------------------------------
	// setup
	// ---------------------------------------------------------------------------------------------------------
	bool DX12Particles::initialize(ID3D12Device* device, DX12FrameRing* ring, DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format)
	{
		shutdown();
		m_device = device;
		m_ring = ring;
		if (!device || !ring) return false;
		m_vs_particle = DX12ShaderCompiler::load_shader("particles", "vs", "ParticleVS", "vs_5_0");
		m_vs_ribbon = DX12ShaderCompiler::load_shader("particles", "vs_ribbon", "RibbonVS", "vs_5_0");
		m_ps_particle = DX12ShaderCompiler::load_shader("particles", "ps", "ParticlePS", "ps_5_0");
		m_vs_snap = DX12ShaderCompiler::load_shader("particles", "vs_snap", "SnapVS", "vs_5_0");
		m_ps_snap = DX12ShaderCompiler::load_shader("particles", "ps_snap", "SnapPS", "ps_5_0");
		if (!m_vs_particle || !m_vs_ribbon || !m_ps_particle || !m_vs_snap || !m_ps_snap)
		{
			log("particles.hlsl did not compile — no VFX on this backend");
			shutdown();
			return false;
		}
		if (!create_root_signature() || !create_pipelines(rtv_format, dsv_format)) { shutdown(); return false; }
		m_ready = true;
		log("ready (billboards, ribbons, soft particles, depth collision)");
		return true;
	}

	void DX12Particles::shutdown()
	{
		for (auto& p : m_billboard) p.Reset();
		for (auto& p : m_ribbon) p.Reset();
		m_snap.Reset();
		m_root_signature.Reset();
		m_vs_particle.Reset(); m_vs_ribbon.Reset(); m_ps_particle.Reset(); m_vs_snap.Reset(); m_ps_snap.Reset();
		for (auto& c : m_depth_copies) { c.texture.Reset(); c.w = c.h = 0; c.state = D3D12_RESOURCE_STATE_COPY_DEST; }   // the SRV slots stay reserved
		for (auto& c : m_color_copies) { c.texture.Reset(); c.w = c.h = 0; c.format = DXGI_FORMAT_UNKNOWN; c.state = D3D12_RESOURCE_STATE_COPY_DEST; }
		m_snap_texture.Reset();
		m_snap_rtv_heap.Reset();
		m_snap_w = m_snap_h = 0;
		for (auto& s : m_snaps) s = Snap{};
		m_instances = DX12FrameRing::Upload{};
		m_ribbon_vbv = {}; m_ribbon_ibv = {};
		m_layer_batches[0] = m_layer_batches[1] = 0;
		m_list.clear();
		m_ready = false;
		m_device = nullptr;
		m_ring = nullptr;
	}

	bool DX12Particles::create_root_signature()
	{
		auto serialize = get_serialize_root_signature();
		if (!serialize) return false;

		D3D12_DESCRIPTOR_RANGE tex_range{};
		tex_range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
		tex_range.NumDescriptors = 1;
		tex_range.BaseShaderRegister = 0;   // t0: the particle texture
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
		params[RP_LIGHTS].ShaderVisibility = D3D12_SHADER_VISIBILITY_VERTEX;
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
		params[RP_SNAP].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
		params[RP_SNAP].Descriptor.ShaderRegister = 3;
		params[RP_SNAP].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		D3D12_STATIC_SAMPLER_DESC samplers[2]{};
		samplers[0].Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;   // s0: the particle texture (wrap for tiled trails)
		samplers[0].AddressU = samplers[0].AddressV = samplers[0].AddressW = D3D12_TEXTURE_ADDRESS_MODE_WRAP;
		samplers[0].MaxLOD = D3D12_FLOAT32_MAX;
		samplers[0].ShaderRegister = 0;
		samplers[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
		samplers[1].Filter = D3D12_FILTER_MIN_MAG_MIP_POINT;    // s1: the depth copy
		samplers[1].AddressU = samplers[1].AddressV = samplers[1].AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
		samplers[1].MaxLOD = D3D12_FLOAT32_MAX;
		samplers[1].ShaderRegister = 1;
		samplers[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		D3D12_ROOT_SIGNATURE_DESC desc{};
		desc.NumParameters = RP_COUNT;
		desc.pParameters = params;
		desc.NumStaticSamplers = 2;
		desc.pStaticSamplers = samplers;
		desc.Flags = D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;

		ComPtr<ID3DBlob> signature, error;
		if (FAILED(serialize(&desc, D3D_ROOT_SIGNATURE_VERSION_1, &signature, &error)))
		{
			if (error) log(std::string("root signature: ") + static_cast<const char*>(error->GetBufferPointer()));
			return false;
		}
		return SUCCEEDED(m_device->CreateRootSignature(0, signature->GetBufferPointer(), signature->GetBufferSize(), IID_PPV_ARGS(&m_root_signature)));
	}

	bool DX12Particles::create_pipelines(DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format)
	{
		const D3D12_INPUT_ELEMENT_DESC ribbon_layout[3] = {
			{ "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0,  D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
			{ "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT,    0, 12, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
			{ "COLOR",    0, DXGI_FORMAT_R8G8B8A8_UNORM,  0, 20, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
		};
		static_assert(sizeof(particles::RibbonVertex) == 24, "ribbon input layout");

		for (u32 b = 0; b < 3; ++b)
		{
			D3D12_GRAPHICS_PIPELINE_STATE_DESC d{};
			d.pRootSignature = m_root_signature.Get();
			d.PS = { m_ps_particle->GetBufferPointer(), m_ps_particle->GetBufferSize() };
			d.SampleMask = UINT_MAX;
			d.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID;
			d.RasterizerState.CullMode = D3D12_CULL_MODE_NONE;
			d.RasterizerState.DepthClipEnable = TRUE;
			d.DepthStencilState.DepthEnable = TRUE;                       // the scene depth occludes particles ...
			d.DepthStencilState.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ZERO;   // ... which never write it
			d.DepthStencilState.DepthFunc = D3D12_COMPARISON_FUNC_LESS_EQUAL;
			auto& rt = d.BlendState.RenderTarget[0];
			rt.BlendEnable = TRUE;
			rt.BlendOp = D3D12_BLEND_OP_ADD;
			rt.BlendOpAlpha = D3D12_BLEND_OP_ADD;
			rt.SrcBlendAlpha = D3D12_BLEND_ONE;
			rt.DestBlendAlpha = D3D12_BLEND_INV_SRC_ALPHA;
			rt.RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
			switch (b)
			{
			case particles::BLEND_ADDITIVE:
				rt.SrcBlend = D3D12_BLEND_SRC_ALPHA; rt.DestBlend = D3D12_BLEND_ONE;
				rt.SrcBlendAlpha = D3D12_BLEND_ZERO; rt.DestBlendAlpha = D3D12_BLEND_ONE;
				break;
			case particles::BLEND_PREMULTIPLIED:
				rt.SrcBlend = D3D12_BLEND_ONE; rt.DestBlend = D3D12_BLEND_INV_SRC_ALPHA;
				break;
			default:
				rt.SrcBlend = D3D12_BLEND_SRC_ALPHA; rt.DestBlend = D3D12_BLEND_INV_SRC_ALPHA;
				break;
			}
			d.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
			d.NumRenderTargets = 1;
			d.RTVFormats[0] = rtv_format;
			d.DSVFormat = dsv_format;
			d.SampleDesc.Count = 1;

			d.VS = { m_vs_particle->GetBufferPointer(), m_vs_particle->GetBufferSize() };
			if (FAILED(m_device->CreateGraphicsPipelineState(&d, IID_PPV_ARGS(&m_billboard[b])))) { log("billboard PSO failed"); return false; }
			d.VS = { m_vs_ribbon->GetBufferPointer(), m_vs_ribbon->GetBufferSize() };
			d.InputLayout = { ribbon_layout, 3 };
			if (FAILED(m_device->CreateGraphicsPipelineState(&d, IID_PPV_ARGS(&m_ribbon[b])))) { log("ribbon PSO failed"); return false; }
		}
		{
			// collision snapshot: a fullscreen triangle into a small R32_FLOAT target, no depth, no blending
			D3D12_GRAPHICS_PIPELINE_STATE_DESC d{};
			d.pRootSignature = m_root_signature.Get();
			d.VS = { m_vs_snap->GetBufferPointer(), m_vs_snap->GetBufferSize() };
			d.PS = { m_ps_snap->GetBufferPointer(), m_ps_snap->GetBufferSize() };
			d.SampleMask = UINT_MAX;
			d.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID;
			d.RasterizerState.CullMode = D3D12_CULL_MODE_NONE;
			d.RasterizerState.DepthClipEnable = TRUE;
			d.BlendState.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
			d.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
			d.NumRenderTargets = 1;
			d.RTVFormats[0] = DXGI_FORMAT_R32_FLOAT;
			d.SampleDesc.Count = 1;
			if (FAILED(m_device->CreateGraphicsPipelineState(&d, IID_PPV_ARGS(&m_snap))))
			{
				log("collision snapshot PSO failed (depth collision off)");
				m_snap.Reset();
			}
		}
		return true;
	}

	// A copy of the pass's depth buffer the particle shader can sample (the bound DSV itself cannot be). One texture
	// per target size, kept across frames; the depth comes back in DEPTH_WRITE.
	DX12Particles::DepthCopy* DX12Particles::copy_depth(ID3D12GraphicsCommandList* cmd, ID3D12Resource* depth)
	{
		if (!cmd || !depth || !m_ring) return nullptr;
		const D3D12_RESOURCE_DESC dd = depth->GetDesc();
		const u32 w = (u32)dd.Width, h = (u32)dd.Height;
		if (w == 0 || h == 0 || dd.SampleDesc.Count != 1) return nullptr;
		DepthCopy* e = nullptr;
		for (auto& c : m_depth_copies) if (c.texture && c.w == w && c.h == h) { e = &c; break; }
		if (!e)
		{
			for (auto& c : m_depth_copies) if (!c.texture) { e = &c; break; }
			if (!e) { e = &m_depth_copies[0]; for (auto& c : m_depth_copies) if (c.last_used < e->last_used) e = &c; }
			if (e->texture) { m_ring->retire(e->texture); e->texture.Reset(); }
			if (!e->slot)
			{
				if (!ResourceRegistry::instance().reserve_srv_slot(e->cpu, e->gpu)) { log("no SRV slot for the depth copy"); return nullptr; }
				e->slot = true;
			}
			D3D12_HEAP_PROPERTIES hp{};
			hp.Type = D3D12_HEAP_TYPE_DEFAULT;
			D3D12_RESOURCE_DESC rd{};
			rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
			rd.Width = w; rd.Height = h;
			rd.DepthOrArraySize = 1; rd.MipLevels = 1;
			rd.Format = DXGI_FORMAT_R32_TYPELESS;   // copy-compatible with D32_FLOAT and R32_TYPELESS depth buffers
			rd.SampleDesc.Count = 1;
			rd.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
			if (FAILED(m_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&e->texture))))
			{
				log("depth copy allocation failed (" + std::to_string(w) + "x" + std::to_string(h) + ")");
				return nullptr;
			}
			e->w = w; e->h = h;
			e->state = D3D12_RESOURCE_STATE_COPY_DEST;
			D3D12_SHADER_RESOURCE_VIEW_DESC sd{};
			sd.Format = DXGI_FORMAT_R32_FLOAT;
			sd.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
			sd.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
			sd.Texture2D.MipLevels = 1;
			m_device->CreateShaderResourceView(e->texture.Get(), &sd, e->cpu);
		}
		e->last_used = m_frame;
		barrier(cmd, depth, D3D12_RESOURCE_STATE_DEPTH_WRITE, D3D12_RESOURCE_STATE_COPY_SOURCE);
		barrier(cmd, e->texture.Get(), e->state, D3D12_RESOURCE_STATE_COPY_DEST);
		cmd->CopyResource(e->texture.Get(), depth);
		barrier(cmd, e->texture.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
		e->state = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
		barrier(cmd, depth, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_DEPTH_WRITE);
		return e;
	}

	D3D12_GPU_DESCRIPTOR_HANDLE DX12Particles::scene_depth_srv(ID3D12GraphicsCommandList* cmd, ID3D12Resource* depth)
	{
		DepthCopy* dc = m_device ? copy_depth(cmd, depth) : nullptr;
		return dc ? dc->gpu : D3D12_GPU_DESCRIPTOR_HANDLE{};
	}

	// The colour target so far, copied for the transparent pass (#200): same size + format as the source (typeless
	// sources get the matching typed SRV), one entry per size/format, the SRV slot reserved once like the depth copies.
	DX12Particles::DepthCopy* DX12Particles::copy_color(ID3D12GraphicsCommandList* cmd, ID3D12Resource* color)
	{
		if (!cmd || !color || !m_ring) return nullptr;
		const D3D12_RESOURCE_DESC dd = color->GetDesc();
		const u32 w = (u32)dd.Width, h = (u32)dd.Height;
		if (w == 0 || h == 0 || dd.SampleDesc.Count != 1) return nullptr;
		DXGI_FORMAT srv_format = dd.Format;
		switch (dd.Format)
		{
		case DXGI_FORMAT_R8G8B8A8_TYPELESS: srv_format = DXGI_FORMAT_R8G8B8A8_UNORM; break;
		case DXGI_FORMAT_B8G8R8A8_TYPELESS: srv_format = DXGI_FORMAT_B8G8R8A8_UNORM; break;
		case DXGI_FORMAT_R16G16B16A16_TYPELESS: srv_format = DXGI_FORMAT_R16G16B16A16_FLOAT; break;
		case DXGI_FORMAT_R10G10B10A2_TYPELESS: srv_format = DXGI_FORMAT_R10G10B10A2_UNORM; break;
		case DXGI_FORMAT_R32G32B32A32_TYPELESS: srv_format = DXGI_FORMAT_R32G32B32A32_FLOAT; break;
		case DXGI_FORMAT_R11G11B10_FLOAT: case DXGI_FORMAT_R16G16B16A16_FLOAT: case DXGI_FORMAT_R8G8B8A8_UNORM: case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
		case DXGI_FORMAT_B8G8R8A8_UNORM: case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: case DXGI_FORMAT_R10G10B10A2_UNORM: case DXGI_FORMAT_R32G32B32A32_FLOAT: break;
		default: return nullptr;   // not a colour format we know how to view
		}
		DepthCopy* e = nullptr;
		for (auto& c : m_color_copies) if (c.texture && c.w == w && c.h == h && c.format == dd.Format) { e = &c; break; }
		if (!e)
		{
			for (auto& c : m_color_copies) if (!c.texture) { e = &c; break; }
			if (!e) { e = &m_color_copies[0]; for (auto& c : m_color_copies) if (c.last_used < e->last_used) e = &c; }
			if (e->texture) { m_ring->retire(e->texture); e->texture.Reset(); }
			if (!e->slot)
			{
				if (!ResourceRegistry::instance().reserve_srv_slot(e->cpu, e->gpu)) { log("no SRV slot for the colour copy"); return nullptr; }
				e->slot = true;
			}
			D3D12_HEAP_PROPERTIES hp{};
			hp.Type = D3D12_HEAP_TYPE_DEFAULT;
			D3D12_RESOURCE_DESC rd{};
			rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
			rd.Width = w; rd.Height = h;
			rd.DepthOrArraySize = 1; rd.MipLevels = 1;
			rd.Format = dd.Format;
			rd.SampleDesc.Count = 1;
			rd.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
			if (FAILED(m_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&e->texture))))
			{
				log("colour copy allocation failed (" + std::to_string(w) + "x" + std::to_string(h) + ")");
				return nullptr;
			}
			e->w = w; e->h = h; e->format = dd.Format;
			e->state = D3D12_RESOURCE_STATE_COPY_DEST;
			D3D12_SHADER_RESOURCE_VIEW_DESC sd{};
			sd.Format = srv_format;
			sd.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
			sd.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
			sd.Texture2D.MipLevels = 1;
			m_device->CreateShaderResourceView(e->texture.Get(), &sd, e->cpu);
		}
		e->last_used = m_frame;
		barrier(cmd, color, D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_COPY_SOURCE);
		barrier(cmd, e->texture.Get(), e->state, D3D12_RESOURCE_STATE_COPY_DEST);
		cmd->CopyResource(e->texture.Get(), color);
		barrier(cmd, e->texture.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
		e->state = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
		barrier(cmd, color, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_RENDER_TARGET);
		return e;
	}

	D3D12_GPU_DESCRIPTOR_HANDLE DX12Particles::scene_color_srv(ID3D12GraphicsCommandList* cmd, ID3D12Resource* color)
	{
		DepthCopy* cc = m_device ? copy_color(cmd, color) : nullptr;
		return cc ? cc->gpu : D3D12_GPU_DESCRIPTOR_HANDLE{};
	}

	// ---------------------------------------------------------------------------------------------------------
	// per view
	// ---------------------------------------------------------------------------------------------------------
	bool DX12Particles::prepare(const View& view, u32 world)
	{
		m_layer_batches[0] = m_layer_batches[1] = 0;
		m_instances = DX12FrameRing::Upload{};
		m_ribbon_vbv = {}; m_ribbon_ibv = {};
		if (!m_ready) return false;
		particles::ViewInfo vi{};
		std::memcpy(vi.view_proj, &view.view_projection, sizeof(vi.view_proj));
		vi.eye[0] = view.eye.x; vi.eye[1] = view.eye.y; vi.eye[2] = view.eye.z;
		vi.right[0] = view.right.x; vi.right[1] = view.right.y; vi.right[2] = view.right.z;
		vi.up[0] = view.up.x; vi.up[1] = view.up.y; vi.up[2] = view.up.z;
		vi.forward[0] = view.forward.x; vi.forward[1] = view.forward.y; vi.forward[2] = view.forward.z;
		vi.ortho = view.ortho;
		if (!particles::gather(world, vi, m_list)) return false;
		for (const auto& b : m_list.batches) if (b.layer < 2) ++m_layer_batches[b.layer];

		const u64 ib = (u64)m_list.instances.size() * sizeof(particles::GpuParticle);
		const u64 vb = (u64)m_list.ribbon_vertices.size() * sizeof(particles::RibbonVertex);
		const u64 xb = (u64)m_list.ribbon_indices.size() * sizeof(u32);
		auto fail = [&]() { m_layer_batches[0] = m_layer_batches[1] = 0; m_instances = DX12FrameRing::Upload{}; m_ribbon_vbv = {}; m_ribbon_ibv = {}; return false; };
		if (ib)
		{
			m_instances = m_ring->alloc(ib);
			if (!m_instances.cpu) return fail();
			std::memcpy(m_instances.cpu, m_list.instances.data(), ib);
		}
		if (vb)
		{
			DX12FrameRing::Upload v = m_ring->alloc(vb);
			if (!v.cpu) return fail();
			std::memcpy(v.cpu, m_list.ribbon_vertices.data(), vb);
			m_ribbon_vbv.BufferLocation = v.gpu;
			m_ribbon_vbv.SizeInBytes = (UINT)vb;
			m_ribbon_vbv.StrideInBytes = sizeof(particles::RibbonVertex);
		}
		if (xb)
		{
			DX12FrameRing::Upload x = m_ring->alloc(xb);
			if (!x.cpu) return fail();
			std::memcpy(x.cpu, m_list.ribbon_indices.data(), xb);
			m_ribbon_ibv.BufferLocation = x.gpu;
			m_ribbon_ibv.SizeInBytes = (UINT)xb;
			m_ribbon_ibv.Format = DXGI_FORMAT_R32_UINT;
		}
		return m_layer_batches[0] + m_layer_batches[1] > 0;
	}

	void DX12Particles::draw_layer(ID3D12GraphicsCommandList* cmd, D3D12_CPU_DESCRIPTOR_HANDLE rtv, D3D12_CPU_DESCRIPTOR_HANDLE dsv,
		ID3D12Resource* depth, u32 w, u32 h, u32 layer, const View& view, const Environment& env, D3D12_GPU_VIRTUAL_ADDRESS lights)
	{
		if (!m_ready || !cmd || !has_layer(layer) || !depth || w == 0 || h == 0) return;
		DepthCopy* dc = copy_depth(cmd, depth);
		if (!dc) return;   // the shader loads the scene depth for every pixel

		PFrame f{};
		f.view_projection = layer == 0 ? view.view_projection : view.viewmodel_projection;
		f.cam_right[0] = view.right.x; f.cam_right[1] = view.right.y; f.cam_right[2] = view.right.z;
		f.cam_up[0] = view.up.x; f.cam_up[1] = view.up.y; f.cam_up[2] = view.up.z;
		f.cam_forward[0] = view.forward.x; f.cam_forward[1] = view.forward.y; f.cam_forward[2] = view.forward.z;
		f.eye[0] = view.eye.x; f.eye[1] = view.eye.y; f.eye[2] = view.eye.z; f.eye[3] = 1.0f;
		f.depth_params[0] = layer == 0 ? view.near_clip : view.vm_near_clip;
		f.depth_params[1] = layer == 0 ? view.far_clip : view.vm_far_clip;
		f.depth_params[2] = (layer == 0 && view.ortho) ? 1.0f : 0.0f;
		f.fog[0] = env.fog_color.x; f.fog[1] = env.fog_color.y; f.fog[2] = env.fog_color.z; f.fog[3] = env.fog_density;
		f.fog2[0] = env.fog_height_y; f.fog2[1] = env.fog_height_falloff;
		f.sun_dir[0] = env.sun_direction.x; f.sun_dir[1] = env.sun_direction.y; f.sun_dir[2] = env.sun_direction.z; f.sun_dir[3] = env.sun_intensity;
		f.sun_color[0] = env.sun_color.x; f.sun_color[1] = env.sun_color.y; f.sun_color[2] = env.sun_color.z; f.sun_color[3] = env.ambient;
		f.counts[0] = lights ? env.point_lights : 0; f.counts[1] = lights ? env.spot_lights : 0;
		DX12FrameRing::Upload fcb = m_ring->alloc(sizeof(PFrame));
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
		cmd->SetGraphicsRootDescriptorTable(RP_DEPTH, dc->gpu);

		Texture* white = reg.get_texture(reg.default_white_texture());
		const D3D12_GPU_DESCRIPTOR_HANDLE white_srv = (white && white->is_valid() && white->srv_gpu().ptr != 0) ? white->srv_gpu() : dc->gpu;
		ID3D12PipelineState* bound = nullptr;
		for (const auto& b : m_list.batches)
		{
			if (b.layer != layer || b.count == 0) continue;
			const bool ribbon = b.kind == 1;
			if (ribbon ? (m_ribbon_vbv.BufferLocation == 0 || m_ribbon_ibv.BufferLocation == 0) : m_instances.gpu == 0) continue;
			ID3D12PipelineState* p = (ribbon ? m_ribbon : m_billboard)[(std::min)(b.blend, 2u)].Get();
			if (!p) continue;
			if (p != bound)
			{
				cmd->SetPipelineState(p);
				bound = p;
				if (ribbon)
				{
					cmd->IASetVertexBuffers(0, 1, &m_ribbon_vbv);
					cmd->IASetIndexBuffer(&m_ribbon_ibv);
				}
				else cmd->SetGraphicsRootShaderResourceView(RP_INSTANCES, m_instances.gpu);
			}
			Texture* t = b.texture != particles::NO_TEXTURE ? reg.get_texture((id::id_type)b.texture) : nullptr;
			const bool has_tex = t && t->is_valid() && t->srv_gpu().ptr != 0;
			PBatch pb{};
			pb.base = ribbon ? 0u : b.first;
			pb.mode = ribbon ? MODE_RIBBON : b.render_mode;
			pb.tiles_x = (std::max)(1u, b.tiles_x); pb.tiles_y = (std::max)(1u, b.tiles_y);
			pb.frame_blend = b.frame_blend; pb.lit = b.lit; pb.blend = b.blend; pb.has_texture = has_tex ? 1u : 0u;
			pb.soft_inv = b.soft_distance > 1e-4f ? 1.0f / b.soft_distance : 0.0f;
			pb.emissive = b.emissive;
			DX12FrameRing::Upload bcb = m_ring->alloc(sizeof(PBatch));
			if (!bcb.cpu) break;
			std::memcpy(bcb.cpu, &pb, sizeof(pb));
			cmd->SetGraphicsRootConstantBufferView(RP_BATCH, bcb.gpu);
			cmd->SetGraphicsRootDescriptorTable(RP_TEXTURE, has_tex ? t->srv_gpu() : white_srv);
			if (ribbon) cmd->DrawIndexedInstanced(b.count, 1, b.first, 0, 0);
			else cmd->DrawInstanced(6, b.count, 0, 0);
		}
	}

	// ---------------------------------------------------------------------------------------------------------
	// collision snapshot
	// ---------------------------------------------------------------------------------------------------------
	bool DX12Particles::ensure_snap_target(u32 w, u32 h)
	{
		if (m_snap_texture && m_snap_w == w && m_snap_h == h) return true;
		if (m_snap_texture) { m_ring->retire(m_snap_texture); m_snap_texture.Reset(); }
		if (!m_snap_rtv_heap)
		{
			D3D12_DESCRIPTOR_HEAP_DESC hd{};
			hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
			hd.NumDescriptors = 1;
			if (FAILED(m_device->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&m_snap_rtv_heap)))) return false;
		}
		D3D12_HEAP_PROPERTIES hp{};
		hp.Type = D3D12_HEAP_TYPE_DEFAULT;
		D3D12_RESOURCE_DESC rd{};
		rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
		rd.Width = w; rd.Height = h;
		rd.DepthOrArraySize = 1; rd.MipLevels = 1;
		rd.Format = DXGI_FORMAT_R32_FLOAT;
		rd.SampleDesc.Count = 1;
		rd.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
		rd.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
		D3D12_CLEAR_VALUE cv{};
		cv.Format = DXGI_FORMAT_R32_FLOAT;
		if (FAILED(m_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_RENDER_TARGET, &cv, IID_PPV_ARGS(&m_snap_texture))))
		{
			log("snapshot texture failed");
			m_snap_w = m_snap_h = 0;
			return false;
		}
		m_snap_state = D3D12_RESOURCE_STATE_RENDER_TARGET;
		D3D12_RENDER_TARGET_VIEW_DESC rv{};
		rv.Format = DXGI_FORMAT_R32_FLOAT;
		rv.ViewDimension = D3D12_RTV_DIMENSION_TEXTURE2D;
		m_device->CreateRenderTargetView(m_snap_texture.Get(), &rv, m_snap_rtv_heap->GetCPUDescriptorHandleForHeapStart());
		m_snap_w = w; m_snap_h = h;
		return true;
	}

	void DX12Particles::capture_depth(ID3D12GraphicsCommandList* cmd, ID3D12Resource* depth, u32 w, u32 h, const View& view,
		D3D12_CPU_DESCRIPTOR_HANDLE rtv, D3D12_CPU_DESCRIPTOR_HANDLE dsv)
	{
		if (!m_ready || !m_snap || !cmd || !depth || w < 16 || h < 16) return;
		if (m_last_capture_frame == m_frame) return;   // once per frame (the first main surface)
		if (!particles::wants_depth_snapshot()) return;
		DepthCopy* dc = copy_depth(cmd, depth);        // a fresh copy of the finished world depth
		if (!dc) return;
		const u32 sw = (std::max)(16u, (std::min)(640u, w / 4));
		const u32 sh = (std::max)(9u, (u32)((u64)h * sw / w));
		if (!ensure_snap_target(sw, sh)) return;

		Snap& slot = m_snaps[m_snap_next];
		const u32 pitch = (sw * 4 + D3D12_TEXTURE_DATA_PITCH_ALIGNMENT - 1) / D3D12_TEXTURE_DATA_PITCH_ALIGNMENT * D3D12_TEXTURE_DATA_PITCH_ALIGNMENT;
		const u64 bytes = (u64)pitch * sh;
		if (!slot.readback || slot.bytes < bytes)
		{
			if (slot.readback) { m_ring->retire(slot.readback); slot.readback.Reset(); }
			D3D12_HEAP_PROPERTIES hp{};
			hp.Type = D3D12_HEAP_TYPE_READBACK;
			D3D12_RESOURCE_DESC rd{};
			rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
			rd.Width = bytes;
			rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
			rd.SampleDesc.Count = 1;
			rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
			if (FAILED(m_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&slot.readback))))
			{
				slot.bytes = 0;
				return;
			}
			slot.bytes = bytes;
		}

		// linearise + downsample into the small target
		barrier(cmd, m_snap_texture.Get(), m_snap_state, D3D12_RESOURCE_STATE_RENDER_TARGET);
		m_snap_state = D3D12_RESOURCE_STATE_RENDER_TARGET;
		const D3D12_CPU_DESCRIPTOR_HANDLE snap_rtv = m_snap_rtv_heap->GetCPUDescriptorHandleForHeapStart();
		cmd->OMSetRenderTargets(1, &snap_rtv, FALSE, nullptr);
		D3D12_VIEWPORT vp{ 0.0f, 0.0f, (float)sw, (float)sh, 0.0f, 1.0f };
		D3D12_RECT sc{ 0, 0, (LONG)sw, (LONG)sh };
		cmd->RSSetViewports(1, &vp);
		cmd->RSSetScissorRects(1, &sc);
		cmd->SetPipelineState(m_snap.Get());
		cmd->SetGraphicsRootSignature(m_root_signature.Get());
		if (auto* heap = ResourceRegistry::instance().srv_heap()) { ID3D12DescriptorHeap* heaps[] = { heap }; cmd->SetDescriptorHeaps(1, heaps); }
		SnapCB cb{ { view.near_clip, view.far_clip, view.ortho ? 1.0f : 0.0f, 0.0f }, { (float)w, (float)h }, { (float)sw, (float)sh } };
		DX12FrameRing::Upload u = m_ring->alloc(sizeof(SnapCB));
		if (!u.cpu) return;
		std::memcpy(u.cpu, &cb, sizeof(cb));
		cmd->SetGraphicsRootConstantBufferView(RP_SNAP, u.gpu);
		cmd->SetGraphicsRootDescriptorTable(RP_DEPTH, dc->gpu);
		cmd->SetGraphicsRootDescriptorTable(RP_TEXTURE, dc->gpu);
		cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		cmd->DrawInstanced(3, 1, 0, 0);

		// read it back
		barrier(cmd, m_snap_texture.Get(), D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_COPY_SOURCE);
		m_snap_state = D3D12_RESOURCE_STATE_COPY_SOURCE;
		D3D12_TEXTURE_COPY_LOCATION dst{};
		dst.pResource = slot.readback.Get();
		dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
		dst.PlacedFootprint.Offset = 0;
		dst.PlacedFootprint.Footprint.Format = DXGI_FORMAT_R32_FLOAT;
		dst.PlacedFootprint.Footprint.Width = sw;
		dst.PlacedFootprint.Footprint.Height = sh;
		dst.PlacedFootprint.Footprint.Depth = 1;
		dst.PlacedFootprint.Footprint.RowPitch = pitch;
		D3D12_TEXTURE_COPY_LOCATION src{};
		src.pResource = m_snap_texture.Get();
		src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
		src.SubresourceIndex = 0;
		cmd->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);

		slot.w = sw; slot.h = sh; slot.pitch = pitch; slot.frame = m_frame; slot.pending = true;
		fill_view(slot.view, view);
		m_snap_next = (m_snap_next + 1) % SNAP_SLOTS;
		m_last_capture_frame = m_frame;

		// back to the caller's target
		cmd->OMSetRenderTargets(1, &rtv, FALSE, &dsv);
		D3D12_VIEWPORT rvp{ 0.0f, 0.0f, (float)w, (float)h, 0.0f, 1.0f };
		D3D12_RECT rsc{ 0, 0, (LONG)w, (LONG)h };
		cmd->RSSetViewports(1, &rvp);
		cmd->RSSetScissorRects(1, &rsc);
	}

	void DX12Particles::begin_frame()
	{
		++m_frame;   // the renderer rotated the shared upload ring already
		if (!m_ready) return;
		// newest capture the GPU has certainly finished (the renderer keeps < 3 frames in flight)
		Snap* best = nullptr;
		for (auto& s : m_snaps)
			if (s.pending && s.frame + 3 <= m_frame && (!best || s.frame > best->frame)) best = &s;
		if (!best) return;
		D3D12_RANGE read{ 0, (SIZE_T)best->bytes };
		void* p = nullptr;
		if (best->readback && SUCCEEDED(best->readback->Map(0, &read, &p)) && p)
		{
			m_snap_scratch.resize((size_t)best->w * best->h);
			for (u32 y = 0; y < best->h; ++y)
				std::memcpy(&m_snap_scratch[(size_t)y * best->w], static_cast<const u8*>(p) + (size_t)y * best->pitch, (size_t)best->w * sizeof(float));
			D3D12_RANGE none{ 0, 0 };
			best->readback->Unmap(0, &none);
			particles::submit_depth_snapshot(m_snap_scratch.data(), best->w, best->h, best->view);
		}
		const u64 used = best->frame;
		for (auto& s : m_snaps) if (s.pending && s.frame <= used) s.pending = false;
	}
}
