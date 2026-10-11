#include "DX12Pipeline3D.h"
#include "DX12ShaderCompiler.h"   // shaders now live in Engine/Shaders/standard.hlsl

namespace vortex::graphics::dx12
{
	namespace
	{
		using PFN_D3D12SerializeRootSignature = HRESULT(WINAPI*)(const D3D12_ROOT_SIGNATURE_DESC*, D3D_ROOT_SIGNATURE_VERSION, ID3DBlob**, ID3DBlob**);

		PFN_D3D12SerializeRootSignature get_serialize_root_signature()
		{
			static auto fn = reinterpret_cast<PFN_D3D12SerializeRootSignature>(
				GetProcAddress(LoadLibraryW(L"d3d12.dll"), "D3D12SerializeRootSignature"));
			return fn;
		}

	}

	bool DX12Pipeline3D::initialize(ID3D12Device* device, DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format)
	{
		if (!device) return false;
		m_rtv_format = rtv_format; m_dsv_format = dsv_format;   // remembered for custom material PSOs
		if (!compile_shaders()) return false;
		if (!create_root_signature(device)) return false;
		if (!create_pso(device, rtv_format, dsv_format)) return false;
		return true;
	}

	ComPtr<ID3D12PipelineState> DX12Pipeline3D::create_custom_pso(ID3D12Device* device, const std::wstring& hlsl_path,
		u32 blend_mode, bool double_sided, bool mirrored, bool skinned)
	{
		if (!device || !m_root_signature || hlsl_path.empty()) return nullptr;
		if (skinned && !m_skinned_vs_blob) return nullptr;

		// Compile VSMain/PSMain from the project's .hlsl. nullptr on failure -> caller keeps the built-in PSO.
		// A skinned mesh (#332) keeps the engine's skinning VS in front of the file's PSMain (skinned.hlsl's PS_IN
		// is semantically identical to standard.hlsl's, which the template copies).
		ComPtr<ID3DBlob> vs = skinned ? m_skinned_vs_blob : DX12ShaderCompiler::compile_from_file(hlsl_path, "VSMain", "vs_5_0");
		ComPtr<ID3DBlob> ps = DX12ShaderCompiler::compile_from_file(hlsl_path, "PSMain", "ps_5_0");
		if (!vs || !ps) return nullptr;

		// Same input layout + render state as the built-in PBR PSO — only the shader stages differ (binding-compatible).
		D3D12_INPUT_ELEMENT_DESC rigid_layout[] = {
			{ "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
			{ "NORMAL", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 12, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
			{ "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 24, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
			{ "INSTANCEWORLD", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 0,  D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
			{ "INSTANCEWORLD", 1, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 16, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
			{ "INSTANCEWORLD", 2, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 32, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
			{ "INSTANCEWORLD", 3, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 48, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 }
		};
		D3D12_INPUT_ELEMENT_DESC skinned_layout[] = {
			{ "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
			{ "NORMAL", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 12, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
			{ "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 24, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
			{ "BLENDINDICES", 0, DXGI_FORMAT_R8G8B8A8_UINT, 0, 32, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
			{ "BLENDWEIGHT", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 36, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
			{ "INSTANCEWORLD", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 0,  D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
			{ "INSTANCEWORLD", 1, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 16, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
			{ "INSTANCEWORLD", 2, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 32, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
			{ "INSTANCEWORLD", 3, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 48, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 }
		};

		D3D12_RASTERIZER_DESC rasterizer{};
		rasterizer.FillMode = D3D12_FILL_MODE_SOLID;
		rasterizer.CullMode = double_sided ? D3D12_CULL_MODE_NONE : D3D12_CULL_MODE_BACK;
		rasterizer.FrontCounterClockwise = (mirrored && !double_sided) ? TRUE : FALSE;   // #334
		rasterizer.DepthClipEnable = TRUE;

		// A custom shader keeps the material's blend mode (#333): alpha / additive blend with depth write off, like
		// the built-in transparent PSOs.
		const u32 b = blend_mode == 1 ? 1u : (blend_mode == 2 ? 2u : 0u);
		D3D12_BLEND_DESC blend{};
		blend.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
		if (b != 0)
		{
			blend.RenderTarget[0].BlendEnable = TRUE;
			blend.RenderTarget[0].SrcBlend = D3D12_BLEND_SRC_ALPHA;
			blend.RenderTarget[0].DestBlend = b == 1 ? D3D12_BLEND_INV_SRC_ALPHA : D3D12_BLEND_ONE;
			blend.RenderTarget[0].BlendOp = D3D12_BLEND_OP_ADD;
			blend.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND_ONE;
			blend.RenderTarget[0].DestBlendAlpha = b == 1 ? D3D12_BLEND_INV_SRC_ALPHA : D3D12_BLEND_ONE;
			blend.RenderTarget[0].BlendOpAlpha = D3D12_BLEND_OP_ADD;
		}

		D3D12_DEPTH_STENCIL_DESC depth_stencil{};
		depth_stencil.DepthEnable = TRUE;
		depth_stencil.DepthWriteMask = b == 0 ? D3D12_DEPTH_WRITE_MASK_ALL : D3D12_DEPTH_WRITE_MASK_ZERO;
		depth_stencil.DepthFunc = b == 0 ? D3D12_COMPARISON_FUNC_LESS : D3D12_COMPARISON_FUNC_LESS_EQUAL;

		D3D12_GRAPHICS_PIPELINE_STATE_DESC pso_desc{};
		pso_desc.pRootSignature = m_root_signature.Get();
		pso_desc.VS = { vs->GetBufferPointer(), vs->GetBufferSize() };
		pso_desc.PS = { ps->GetBufferPointer(), ps->GetBufferSize() };
		pso_desc.BlendState = blend;
		pso_desc.SampleMask = UINT_MAX;
		pso_desc.RasterizerState = rasterizer;
		pso_desc.DepthStencilState = depth_stencil;
		pso_desc.InputLayout = skinned ? D3D12_INPUT_LAYOUT_DESC{ skinned_layout, _countof(skinned_layout) }
		                               : D3D12_INPUT_LAYOUT_DESC{ rigid_layout, _countof(rigid_layout) };
		pso_desc.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
		pso_desc.NumRenderTargets = 1;
		pso_desc.RTVFormats[0] = m_rtv_format;
		pso_desc.DSVFormat = m_dsv_format;
		pso_desc.SampleDesc.Count = 1;

		ComPtr<ID3D12PipelineState> pso;
		if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&pso)))) return nullptr;
		return pso;
	}

	void DX12Pipeline3D::shutdown()
	{
		m_alpha_pso.Reset();
		m_alpha_ds_pso.Reset();
		m_additive_pso.Reset();
		m_additive_ds_pso.Reset();
		m_mirrored_pso.Reset();
		m_alpha_m_pso.Reset();
		m_additive_m_pso.Reset();
		m_skinned_m_pso.Reset();
		m_skinned_pso.Reset();
		m_shadow_cut_pso.Reset();
		m_shadow_cut_ps_blob.Reset();
		m_wireframe_pso.Reset();
		m_pipeline_state.Reset();
		m_root_signature.Reset();
		m_vs_blob.Reset();
		m_ps_blob.Reset();
		m_skinned_vs_blob.Reset();
	}

	bool DX12Pipeline3D::compile_shaders()
	{
		// Engine/Shaders/standard.hlsl (VSMain/PSMain), via the shared compiler. This is the FATAL pass — a load
		// failure aborts renderer init (DX12Renderer.cpp) rather than degrading, so this must always resolve.
		m_vs_blob = DX12ShaderCompiler::load_shader("standard", "vs", "VSMain", "vs_5_0");
		m_ps_blob = DX12ShaderCompiler::load_shader("standard", "ps", "PSMain", "ps_5_0");
		// Skinned VS (skinned.hlsl) is OPTIONAL: a missing/broken file only disables GPU skinning (skinned
		// meshes then draw through the rigid PSO in bind pose) — it never aborts renderer init.
		m_skinned_vs_blob = DX12ShaderCompiler::load_shader("skinned", "vs", "VSMain", "vs_5_0");
		if (!m_skinned_vs_blob) OutputDebugStringA("DX12Pipeline3D: skinned.hlsl unavailable — GPU skinning disabled\n");
		m_shadow_cut_ps_blob = DX12ShaderCompiler::load_shader("standard", "ps_shadowcut", "ShadowCutPS", "ps_5_0");
		if (!m_shadow_cut_ps_blob) OutputDebugStringA("DX12Pipeline3D: ShadowCutPS unavailable — alpha-tested casters cast solid shadows\n");
		return m_vs_blob != nullptr && m_ps_blob != nullptr;
	}

	bool DX12Pipeline3D::create_root_signature(ID3D12Device* device)
	{
		auto serialize = get_serialize_root_signature();
		if (!serialize) return false;

		// Params 0-7 are the long-standing ABI (every existing SetGraphicsRoot* call keeps its index).
		// Param 8 is the bone-palette root SRV (t5, vertex-only) for GPU skinning — a root descriptor (raw GPU VA),
		// so it costs no descriptor-heap slot and rigid draws simply never bind it.
		// Param 9 is the HEIGHT/displacement map descriptor table (t6, pixel) for parallax mapping — additive, so
		// every existing SetGraphicsRoot* index is unchanged; a material with no height map simply never binds it.
		// Param 10 is the SHADOW MAP descriptor table (t7, pixel) for spot-light shadows — additive again. Once
		// standard.hlsl references t7 this param MUST be bound in every pass that uses the standard PS (scene,
		// gizmo, offscreen previews); the renderer eager-creates the shadow map so a valid descriptor always exists.
		// Param 11 is the CSM cascade atlas table (t8, pixel) for directional shadows (#24), param 12
		// the point-light face atlas (t9, #25) — additive like 9/10, so every existing
		// SetGraphicsRoot* index stays put; the renderer eager-creates both atlases so a valid
		// descriptor always exists wherever the standard PS runs.
		// Param 13 is the SSAO texture table (t10, #32) — the blurred half-res AO the standard PS
		// multiplies into the ambient term only (sampled with the s2 linear-clamp sampler).
		D3D12_ROOT_PARAMETER params[17] = {};

		params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
		params[0].Descriptor.ShaderRegister = 0;
		params[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;

		params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
		params[1].Descriptor.ShaderRegister = 1;
		params[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;

		params[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
		params[2].Descriptor.ShaderRegister = 2;
		params[2].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		static D3D12_DESCRIPTOR_RANGE srv_ranges[13] = {};
		for (int i = 0; i < 5; i++)
		{
			srv_ranges[i].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
			srv_ranges[i].NumDescriptors = 1;
			srv_ranges[i].BaseShaderRegister = i;
			srv_ranges[i].RegisterSpace = 0;
			srv_ranges[i].OffsetInDescriptorsFromTableStart = 0;

			params[3 + i].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
			params[3 + i].DescriptorTable.NumDescriptorRanges = 1;
			params[3 + i].DescriptorTable.pDescriptorRanges = &srv_ranges[i];
			params[3 + i].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
		}

		params[8].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;   // root SRV: StructuredBuffer<float4> BoneRows (t5)
		params[8].Descriptor.ShaderRegister = 5;
		params[8].ShaderVisibility = D3D12_SHADER_VISIBILITY_VERTEX;

		// Height/displacement map descriptor table at t6 (pixel), for parallax mapping.
		srv_ranges[5].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
		srv_ranges[5].NumDescriptors = 1;
		srv_ranges[5].BaseShaderRegister = 6;
		srv_ranges[5].RegisterSpace = 0;
		srv_ranges[5].OffsetInDescriptorsFromTableStart = 0;
		params[9].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
		params[9].DescriptorTable.NumDescriptorRanges = 1;
		params[9].DescriptorTable.pDescriptorRanges = &srv_ranges[5];
		params[9].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		// Shadow map descriptor table at t7 (pixel) — the spot-light shadow depth (R32_FLOAT SRV over D32).
		srv_ranges[6].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
		srv_ranges[6].NumDescriptors = 1;
		srv_ranges[6].BaseShaderRegister = 7;
		srv_ranges[6].RegisterSpace = 0;
		srv_ranges[6].OffsetInDescriptorsFromTableStart = 0;
		params[10].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
		params[10].DescriptorTable.NumDescriptorRanges = 1;
		params[10].DescriptorTable.pDescriptorRanges = &srv_ranges[6];
		params[10].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		// CSM cascade atlas descriptor table at t8 (pixel) — directional shadows (#24).
		srv_ranges[7].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
		srv_ranges[7].NumDescriptors = 1;
		srv_ranges[7].BaseShaderRegister = 8;
		srv_ranges[7].RegisterSpace = 0;
		srv_ranges[7].OffsetInDescriptorsFromTableStart = 0;
		params[11].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
		params[11].DescriptorTable.NumDescriptorRanges = 1;
		params[11].DescriptorTable.pDescriptorRanges = &srv_ranges[7];
		params[11].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		// Point-light face atlas descriptor table at t9 (pixel) — point cube shadows (#25).
		srv_ranges[8].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
		srv_ranges[8].NumDescriptors = 1;
		srv_ranges[8].BaseShaderRegister = 9;
		srv_ranges[8].RegisterSpace = 0;
		srv_ranges[8].OffsetInDescriptorsFromTableStart = 0;
		params[12].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
		params[12].DescriptorTable.NumDescriptorRanges = 1;
		params[12].DescriptorTable.pDescriptorRanges = &srv_ranges[8];
		params[12].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		// SSAO texture descriptor table at t10 (pixel) — #32.
		srv_ranges[9].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
		srv_ranges[9].NumDescriptors = 1;
		srv_ranges[9].BaseShaderRegister = 10;
		srv_ranges[9].RegisterSpace = 0;
		srv_ranges[9].OffsetInDescriptorsFromTableStart = 0;
		params[13].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
		params[13].DescriptorTable.NumDescriptorRanges = 1;
		params[13].DescriptorTable.pDescriptorRanges = &srv_ranges[9];
		params[13].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
		// Environment map descriptor table at t11 (pixel) — the sky as a reflection (IBL step 2).
		srv_ranges[10].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
		srv_ranges[10].NumDescriptors = 1;
		srv_ranges[10].BaseShaderRegister = 11;
		srv_ranges[10].RegisterSpace = 0;
		srv_ranges[10].OffsetInDescriptorsFromTableStart = 0;
		params[14].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
		params[14].DescriptorTable.NumDescriptorRanges = 1;
		params[14].DescriptorTable.pDescriptorRanges = &srv_ranges[10];
		params[14].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
		// Scene colour (t12) + depth (t13) copies for the transparent pass — water refraction / screen-space reflections (#200).
		for (int k = 0; k < 2; ++k)
		{
			srv_ranges[11 + k].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
			srv_ranges[11 + k].NumDescriptors = 1;
			srv_ranges[11 + k].BaseShaderRegister = 12 + k;
			srv_ranges[11 + k].RegisterSpace = 0;
			srv_ranges[11 + k].OffsetInDescriptorsFromTableStart = 0;
			params[15 + k].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
			params[15 + k].DescriptorTable.NumDescriptorRanges = 1;
			params[15 + k].DescriptorTable.pDescriptorRanges = &srv_ranges[11 + k];
			params[15 + k].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
		}

		D3D12_STATIC_SAMPLER_DESC samplers[3] = {};
		samplers[0].Filter = D3D12_FILTER_ANISOTROPIC;
		samplers[0].AddressU = D3D12_TEXTURE_ADDRESS_MODE_WRAP;
		samplers[0].AddressV = D3D12_TEXTURE_ADDRESS_MODE_WRAP;
		samplers[0].AddressW = D3D12_TEXTURE_ADDRESS_MODE_WRAP;
		samplers[0].MipLODBias = 0.0f;
		samplers[0].MaxAnisotropy = 16;
		samplers[0].ComparisonFunc = D3D12_COMPARISON_FUNC_NEVER;
		samplers[0].BorderColor = D3D12_STATIC_BORDER_COLOR_OPAQUE_WHITE;
		samplers[0].MinLOD = 0.0f;
		samplers[0].MaxLOD = D3D12_FLOAT32_MAX;
		samplers[0].ShaderRegister = 0;
		samplers[0].RegisterSpace = 0;
		samplers[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		// s1: comparison sampler for shadow-map SampleCmpLevelZero. BORDER + opaque-white border means
		// samples OUTSIDE the shadow map read "fully lit" — the spot cone gate in the shader masks the
		// rest, so the shadow frustum edge never shows as a dark rectangle. Static sampler = zero root cost.
		samplers[1].Filter = D3D12_FILTER_COMPARISON_MIN_MAG_LINEAR_MIP_POINT;
		samplers[1].AddressU = D3D12_TEXTURE_ADDRESS_MODE_BORDER;
		samplers[1].AddressV = D3D12_TEXTURE_ADDRESS_MODE_BORDER;
		samplers[1].AddressW = D3D12_TEXTURE_ADDRESS_MODE_BORDER;
		samplers[1].MipLODBias = 0.0f;
		samplers[1].MaxAnisotropy = 1;
		samplers[1].ComparisonFunc = D3D12_COMPARISON_FUNC_LESS_EQUAL;
		samplers[1].BorderColor = D3D12_STATIC_BORDER_COLOR_OPAQUE_WHITE;
		samplers[1].MinLOD = 0.0f;
		samplers[1].MaxLOD = D3D12_FLOAT32_MAX;
		samplers[1].ShaderRegister = 1;
		samplers[1].RegisterSpace = 0;
		samplers[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		// s2: linear/clamp for screen-space textures (#32 SSAO) — s0 is ANISOTROPIC/WRAP, which
		// would wrap the AO texture at the screen edges.
		samplers[2].Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
		samplers[2].AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
		samplers[2].AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
		samplers[2].AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
		samplers[2].MaxAnisotropy = 1;
		samplers[2].ComparisonFunc = D3D12_COMPARISON_FUNC_NEVER;
		samplers[2].BorderColor = D3D12_STATIC_BORDER_COLOR_OPAQUE_WHITE;
		samplers[2].MinLOD = 0.0f;
		samplers[2].MaxLOD = D3D12_FLOAT32_MAX;
		samplers[2].ShaderRegister = 2;
		samplers[2].RegisterSpace = 0;
		samplers[2].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		D3D12_ROOT_SIGNATURE_DESC desc{};
		desc.NumParameters = 17;
		desc.pParameters = params;
		desc.NumStaticSamplers = 3;
		desc.pStaticSamplers = samplers;
		desc.Flags = D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;

		ComPtr<ID3DBlob> signature;
		ComPtr<ID3DBlob> error;

		if (FAILED(serialize(&desc, D3D_ROOT_SIGNATURE_VERSION_1, &signature, &error)))
		{
			if (error) OutputDebugStringA(static_cast<const char*>(error->GetBufferPointer()));
			return false;
		}

		return SUCCEEDED(device->CreateRootSignature(0, signature->GetBufferPointer(),
			signature->GetBufferSize(), IID_PPV_ARGS(&m_root_signature)));
	}

	bool DX12Pipeline3D::create_pso(ID3D12Device* device, DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format)
	{
		D3D12_INPUT_ELEMENT_DESC input_layout[] = {
			{ "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
			{ "NORMAL", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 12, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
			{ "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 24, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
			// Per-instance world matrix (slot 1, advances once per instance) — GPU instancing.
			{ "INSTANCEWORLD", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 0,  D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
			{ "INSTANCEWORLD", 1, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 16, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
			{ "INSTANCEWORLD", 2, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 32, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
			{ "INSTANCEWORLD", 3, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 48, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 }
		};

		D3D12_RASTERIZER_DESC rasterizer{};
		rasterizer.FillMode = D3D12_FILL_MODE_SOLID;
		rasterizer.CullMode = D3D12_CULL_MODE_BACK;
		rasterizer.FrontCounterClockwise = FALSE;
		rasterizer.DepthBias = 0;
		rasterizer.DepthBiasClamp = 0.0f;
		rasterizer.SlopeScaledDepthBias = 0.0f;
		rasterizer.DepthClipEnable = TRUE;
		rasterizer.MultisampleEnable = FALSE;
		rasterizer.AntialiasedLineEnable = FALSE;
		rasterizer.ForcedSampleCount = 0;
		rasterizer.ConservativeRaster = D3D12_CONSERVATIVE_RASTERIZATION_MODE_OFF;

		D3D12_BLEND_DESC blend{};
		blend.AlphaToCoverageEnable = FALSE;
		blend.IndependentBlendEnable = FALSE;
		blend.RenderTarget[0].BlendEnable = FALSE;
		blend.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;

		D3D12_DEPTH_STENCIL_DESC depth_stencil{};
		depth_stencil.DepthEnable = TRUE;
		depth_stencil.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ALL;
		depth_stencil.DepthFunc = D3D12_COMPARISON_FUNC_LESS;
		depth_stencil.StencilEnable = FALSE;

		D3D12_GRAPHICS_PIPELINE_STATE_DESC pso_desc{};
		pso_desc.pRootSignature = m_root_signature.Get();
		pso_desc.VS = { m_vs_blob->GetBufferPointer(), m_vs_blob->GetBufferSize() };
		pso_desc.PS = { m_ps_blob->GetBufferPointer(), m_ps_blob->GetBufferSize() };
		pso_desc.BlendState = blend;
		pso_desc.SampleMask = UINT_MAX;
		pso_desc.RasterizerState = rasterizer;
		pso_desc.DepthStencilState = depth_stencil;
		pso_desc.InputLayout = { input_layout, _countof(input_layout) };
		pso_desc.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
		pso_desc.NumRenderTargets = 1;
		pso_desc.RTVFormats[0] = rtv_format;
		pso_desc.DSVFormat = dsv_format;
		pso_desc.SampleDesc.Count = 1;

		if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_pipeline_state))))
		{
			return false;
		}

		// Mirrored twin (#334): an instance whose world matrix has a negative determinant is wound the other way.
		rasterizer.FrontCounterClockwise = TRUE;
		pso_desc.RasterizerState = rasterizer;
		if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_mirrored_pso))))
			OutputDebugStringA("DX12Pipeline3D: mirrored PSO creation failed — mirrored meshes render inside-out\n");
		rasterizer.FrontCounterClockwise = FALSE;

		rasterizer.FillMode = D3D12_FILL_MODE_WIREFRAME;
		rasterizer.CullMode = D3D12_CULL_MODE_NONE;
		pso_desc.RasterizerState = rasterizer;
		if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_wireframe_pso))))
		{
			return false;
		}

		// Double-sided PSO (no backface culling, for skybox/unlit materials)
		rasterizer.FillMode = D3D12_FILL_MODE_SOLID;
		rasterizer.CullMode = D3D12_CULL_MODE_NONE;
		pso_desc.RasterizerState = rasterizer;
		if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_double_sided_pso))))
		{
			return false;
		}

		// TRANSPARENT PSOs (#33): blending ON, depth test kept (LESS_EQUAL) but depth WRITE off — a
		// transparent surface must not occlude what's drawn behind it later in the sorted pass. Four
		// variants: {alpha, additive} x {cull back, double-sided}; the renderer picks per material at
		// draw time. Optional like skinned/shadow: a creation failure only keeps those materials opaque.
		{
			D3D12_DEPTH_STENCIL_DESC transp_ds{};
			transp_ds.DepthEnable = TRUE;
			transp_ds.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ZERO;
			transp_ds.DepthFunc = D3D12_COMPARISON_FUNC_LESS_EQUAL;
			transp_ds.StencilEnable = FALSE;
			pso_desc.DepthStencilState = transp_ds;

			D3D12_BLEND_DESC transp_blend = blend;
			transp_blend.RenderTarget[0].BlendEnable = TRUE;
			transp_blend.RenderTarget[0].SrcBlend = D3D12_BLEND_SRC_ALPHA;
			transp_blend.RenderTarget[0].DestBlend = D3D12_BLEND_INV_SRC_ALPHA;
			transp_blend.RenderTarget[0].BlendOp = D3D12_BLEND_OP_ADD;
			transp_blend.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND_ONE;
			transp_blend.RenderTarget[0].DestBlendAlpha = D3D12_BLEND_INV_SRC_ALPHA;
			transp_blend.RenderTarget[0].BlendOpAlpha = D3D12_BLEND_OP_ADD;
			pso_desc.BlendState = transp_blend;

			rasterizer.CullMode = D3D12_CULL_MODE_BACK;
			pso_desc.RasterizerState = rasterizer;
			if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_alpha_pso))))
				OutputDebugStringA("DX12Pipeline3D: alpha PSO creation failed — alpha materials render opaque\n");
			rasterizer.FrontCounterClockwise = TRUE;   // mirrored twin (#334)
			pso_desc.RasterizerState = rasterizer;
			if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_alpha_m_pso))))
				OutputDebugStringA("DX12Pipeline3D: mirrored alpha PSO creation failed\n");
			rasterizer.FrontCounterClockwise = FALSE;
			rasterizer.CullMode = D3D12_CULL_MODE_NONE;
			pso_desc.RasterizerState = rasterizer;
			if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_alpha_ds_pso))))
				OutputDebugStringA("DX12Pipeline3D: alpha DS PSO creation failed\n");

			// Additive: SrcAlpha/One — alpha still scales the contribution, black adds nothing.
			transp_blend.RenderTarget[0].DestBlend = D3D12_BLEND_ONE;
			transp_blend.RenderTarget[0].DestBlendAlpha = D3D12_BLEND_ONE;
			pso_desc.BlendState = transp_blend;
			rasterizer.CullMode = D3D12_CULL_MODE_BACK;
			pso_desc.RasterizerState = rasterizer;
			if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_additive_pso))))
				OutputDebugStringA("DX12Pipeline3D: additive PSO creation failed — additive materials render opaque\n");
			rasterizer.FrontCounterClockwise = TRUE;   // mirrored twin (#334)
			pso_desc.RasterizerState = rasterizer;
			if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_additive_m_pso))))
				OutputDebugStringA("DX12Pipeline3D: mirrored additive PSO creation failed\n");
			rasterizer.FrontCounterClockwise = FALSE;
			rasterizer.CullMode = D3D12_CULL_MODE_NONE;
			pso_desc.RasterizerState = rasterizer;
			if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_additive_ds_pso))))
				OutputDebugStringA("DX12Pipeline3D: additive DS PSO creation failed\n");

			// Restore the opaque defaults the following PSO blocks (gizmo/skinned/shadow) inherit.
			pso_desc.BlendState = blend;
			pso_desc.DepthStencilState = depth_stencil;
			// rasterizer stays SOLID + CULL_NONE — exactly what the gizmo block below expects.
		}

		// Gizmo PSO: no backface culling (already set above) + depth test/write DISABLED so editor transform gizmos
		// always render ON TOP of scene geometry and are never occluded. Same shaders/root sig/input layout, so it
		// stays binding-compatible with the standard PerFrame/PerObject/instance-VB setup.
		{
			D3D12_DEPTH_STENCIL_DESC gizmo_ds{};
			gizmo_ds.DepthEnable = FALSE;
			gizmo_ds.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ZERO;
			gizmo_ds.DepthFunc = D3D12_COMPARISON_FUNC_ALWAYS;
			gizmo_ds.StencilEnable = FALSE;
			pso_desc.DepthStencilState = gizmo_ds;   // rasterizer already SOLID + CULL_NONE from double-sided above
			if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_gizmo_pso))))
			{
				return false;
			}

			// Gizmo WIRE PSO: rasterized as wireframe — a whole audio range sphere or reverb-zone shape draws
			// as one fine triangle net (thin lines) instead of hundreds of scaled-cube edge segments. Unlike the
			// solid gizmo PSO this one KEEPS the depth TEST (write still off): a volume shape's far half hides
			// naturally behind scene geometry, which halves the on-screen line density and reads as 3D instead
			// of an always-on-top line salad. Handles/icons stay on the depth-disabled solid PSO.
			rasterizer.FillMode = D3D12_FILL_MODE_WIREFRAME;
			pso_desc.RasterizerState = rasterizer;
			D3D12_DEPTH_STENCIL_DESC wire_ds{};
			wire_ds.DepthEnable = TRUE;
			wire_ds.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ZERO;
			wire_ds.DepthFunc = D3D12_COMPARISON_FUNC_LESS_EQUAL;
			wire_ds.StencilEnable = FALSE;
			pso_desc.DepthStencilState = wire_ds;
			if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_gizmo_wire_pso))))
			{
				return false;
			}
			rasterizer.FillMode = D3D12_FILL_MODE_SOLID;   // restore for the skinned PSO below
			pso_desc.RasterizerState = rasterizer;
		}

		// Skinned PSO (optional — only when skinned.hlsl compiled): the rigid input layout + BLENDINDICES/
		// BLENDWEIGHT at offsets 32/36 on slot 0 (the 52-byte SkinnedVertexPosNormalUV), INSTANCEWORLD slot 1
		// kept. Same root signature; the VS reads the bone palette from the root SRV at param 8. Uses the
		// standard PS blob — pixel shading of a skinned character is identical to a rigid mesh.
		if (m_skinned_vs_blob)
		{
			D3D12_INPUT_ELEMENT_DESC skinned_layout[] = {
				{ "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
				{ "NORMAL", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 12, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
				{ "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 24, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
				{ "BLENDINDICES", 0, DXGI_FORMAT_R8G8B8A8_UINT, 0, 32, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
				{ "BLENDWEIGHT", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 36, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
				{ "INSTANCEWORLD", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 0,  D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
				{ "INSTANCEWORLD", 1, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 16, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
				{ "INSTANCEWORLD", 2, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 32, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
				{ "INSTANCEWORLD", 3, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 48, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 }
			};

			D3D12_RASTERIZER_DESC skinned_raster{};
			skinned_raster.FillMode = D3D12_FILL_MODE_SOLID;
			skinned_raster.CullMode = D3D12_CULL_MODE_BACK;
			skinned_raster.DepthClipEnable = TRUE;

			D3D12_DEPTH_STENCIL_DESC skinned_ds{};
			skinned_ds.DepthEnable = TRUE;
			skinned_ds.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ALL;
			skinned_ds.DepthFunc = D3D12_COMPARISON_FUNC_LESS;

			pso_desc.VS = { m_skinned_vs_blob->GetBufferPointer(), m_skinned_vs_blob->GetBufferSize() };
			pso_desc.PS = { m_ps_blob->GetBufferPointer(), m_ps_blob->GetBufferSize() };
			pso_desc.RasterizerState = skinned_raster;
			pso_desc.DepthStencilState = skinned_ds;
			pso_desc.InputLayout = { skinned_layout, _countof(skinned_layout) };
			if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_skinned_pso))))
				OutputDebugStringA("DX12Pipeline3D: skinned PSO creation failed — GPU skinning disabled\n");
			skinned_raster.FrontCounterClockwise = TRUE;   // mirrored twin (#334)
			pso_desc.RasterizerState = skinned_raster;
			if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_skinned_m_pso))))
				OutputDebugStringA("DX12Pipeline3D: mirrored skinned PSO creation failed\n");
			skinned_raster.FrontCounterClockwise = FALSE;
		}

		// SHADOW PSO: depth-only pass that renders the scene from the spot light's point of view into the
		// shadow map. Reuses the standard VS blob (the shadow pass binds a second 256-byte PerFrame CB region
		// whose view_projection is the LIGHT's VP at root param 0 — no extra .hlsl file, nothing new to ship)
		// with NO pixel shader and NO render target. Depth bias fights shadow acne on the receiver side;
		// slope-scaled handles glancing surfaces (bunker walls lit along their length by the flashlight).
		{
			D3D12_RASTERIZER_DESC shadow_raster{};
			shadow_raster.FillMode = D3D12_FILL_MODE_SOLID;
			shadow_raster.CullMode = D3D12_CULL_MODE_NONE;   // thin walls/floors cast from both sides, mirrored meshes (#334) need no twin (as on Metal)
			shadow_raster.FrontCounterClockwise = FALSE;
			shadow_raster.DepthBias = 100;
			shadow_raster.DepthBiasClamp = 0.0f;
			shadow_raster.SlopeScaledDepthBias = 1.5f;
			shadow_raster.DepthClipEnable = TRUE;

			D3D12_DEPTH_STENCIL_DESC shadow_ds{};
			shadow_ds.DepthEnable = TRUE;
			shadow_ds.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ALL;
			shadow_ds.DepthFunc = D3D12_COMPARISON_FUNC_LESS;
			shadow_ds.StencilEnable = FALSE;

			pso_desc.VS = { m_vs_blob->GetBufferPointer(), m_vs_blob->GetBufferSize() };
			pso_desc.PS = { nullptr, 0 };
			pso_desc.RasterizerState = shadow_raster;
			pso_desc.DepthStencilState = shadow_ds;
			pso_desc.InputLayout = { input_layout, _countof(input_layout) };   // rigid layout -> same instance-VB flow
			pso_desc.NumRenderTargets = 0;
			pso_desc.RTVFormats[0] = DXGI_FORMAT_UNKNOWN;
			pso_desc.DSVFormat = DXGI_FORMAT_D32_FLOAT;
			if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_shadow_pso))))
				OutputDebugStringA("DX12Pipeline3D: shadow PSO creation failed — spot shadows disabled\n");

			// cut-out casters (#329): the same depth-only pass with a pixel shader that clips by the albedo alpha
			if (m_shadow_cut_ps_blob)
			{
				pso_desc.PS = { m_shadow_cut_ps_blob->GetBufferPointer(), m_shadow_cut_ps_blob->GetBufferSize() };
				if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_shadow_cut_pso))))
					OutputDebugStringA("DX12Pipeline3D: cut-out shadow PSO creation failed — alpha-tested casters cast solid shadows\n");
				pso_desc.PS = { nullptr, 0 };
			}

			// Z-PREPASS PSO (#32 SSAO): the same depth-only pass WITHOUT the shadow depth biases
			// (those exist to fight shadow acne; in a camera prepass they would shift the AO depth
			// away from the real scene depth). Renders the half-res AO depth from the camera VP.
			shadow_raster.DepthBias = 0;
			shadow_raster.SlopeScaledDepthBias = 0.0f;
			pso_desc.RasterizerState = shadow_raster;
			if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_zprepass_pso))))
				OutputDebugStringA("DX12Pipeline3D: z-prepass PSO creation failed — SSAO disabled\n");
		}

		return true;
	}
}
