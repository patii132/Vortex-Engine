#include "SdlGpuDecals.h"
#include "SdlGpuShaderFormat.h"
#include "SdlGpuResources.h"
#include "../../Common/Platform.h"
#include <algorithm>
#include <cstring>
#include <fstream>

namespace vortex::graphics::sdlgpu
{
	namespace
	{
		void log(const std::string& s) { platform::debug_output(("[decals] " + s + "\n").c_str()); }

		// byte-matched to DFrame / DBatch in decals.metal / decals_common.glsl
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
		static_assert(sizeof(DFrame) == 256, "DFrame must byte-match decals.metal");

		struct DBatch
		{
			u32 base, blend, has_texture, pad;
			float base_color[4];
		};
		static_assert(sizeof(DBatch) == 32, "DBatch must byte-match decals.metal");

		constexpr u32 LIGHT_BYTES = 1024;
	}

	SDL_GPUShader* SdlGpuDecals::create_shader(const char* entry, SDL_GPUShaderStage stage, u32 samplers, u32 storage, u32 uniforms)
	{
		const std::string file = shaderfmt::module_file("decals", entry);
		std::vector<unsigned char> code;
		{
			std::ifstream f(m_shader_dir + "/" + file, std::ios::binary);
			if (!f) { log("missing shader " + file); return nullptr; }
			code.assign((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
			if (code.empty()) { log("empty shader " + file); return nullptr; }
			if (shaderfmt::is_text) code.push_back('\0');
		}
		SDL_GPUShaderCreateInfo ci{};
		ci.code = code.data();
		ci.code_size = code.size();
		ci.entrypoint = shaderfmt::entrypoint(entry);
		ci.format = shaderfmt::format;
		ci.stage = stage;
		ci.num_samplers = samplers;
		ci.num_storage_buffers = storage;
		ci.num_uniform_buffers = uniforms;
		SDL_GPUShader* sh = SDL_CreateGPUShader(m_device, &ci);
		if (!sh) log(std::string("shader '") + entry + "' failed: " + SDL_GetError());
		return sh;
	}

	SDL_GPUGraphicsPipeline* SdlGpuDecals::create_pipeline(u32 blend)
	{
		SDL_GPUColorTargetDescription color{};
		color.format = m_color_format;
		color.blend_state.enable_blend = true;
		color.blend_state.color_blend_op = SDL_GPU_BLENDOP_ADD;
		color.blend_state.alpha_blend_op = SDL_GPU_BLENDOP_ADD;
		color.blend_state.src_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ZERO;
		color.blend_state.dst_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
		color.blend_state.enable_color_write_mask = true;
		color.blend_state.color_write_mask = SDL_GPU_COLORCOMPONENT_R | SDL_GPU_COLORCOMPONENT_G | SDL_GPU_COLORCOMPONENT_B;
		switch (blend)
		{
		case decals::BLEND_MULTIPLY:
			color.blend_state.src_color_blendfactor = SDL_GPU_BLENDFACTOR_ZERO;
			color.blend_state.dst_color_blendfactor = SDL_GPU_BLENDFACTOR_SRC_COLOR;   // dst * src
			break;
		case decals::BLEND_ADDITIVE:
			color.blend_state.src_color_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
			color.blend_state.dst_color_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
			break;
		default:
			color.blend_state.src_color_blendfactor = SDL_GPU_BLENDFACTOR_SRC_ALPHA;
			color.blend_state.dst_color_blendfactor = SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA;
			break;
		}
		SDL_GPUGraphicsPipelineCreateInfo pci{};
		pci.vertex_shader = m_vs;
		pci.fragment_shader = m_fs;
		pci.primitive_type = SDL_GPU_PRIMITIVETYPE_TRIANGLELIST;
		pci.rasterizer_state.fill_mode = SDL_GPU_FILLMODE_FILL;
		pci.rasterizer_state.cull_mode = SDL_GPU_CULLMODE_FRONT;   // the box's back faces: visible from inside the box too
		pci.rasterizer_state.front_face = SDL_GPU_FRONTFACE_CLOCKWISE;
		pci.rasterizer_state.enable_depth_clip = true;
		pci.multisample_state.sample_count = SDL_GPU_SAMPLECOUNT_1;
		pci.target_info.color_target_descriptions = &color;
		pci.target_info.num_color_targets = 1;
		pci.target_info.has_depth_stencil_target = false;   // the covered pixel is tested against the box in the shader
		SDL_GPUGraphicsPipeline* p = SDL_CreateGPUGraphicsPipeline(m_device, &pci);
		if (!p) log(std::string("pipeline failed: ") + SDL_GetError());
		return p;
	}

	bool SdlGpuDecals::initialize(SDL_GPUDevice* device, const std::string& shader_dir, SDL_GPUTextureFormat color_format, SDL_GPUSampler* linear_clamp)
	{
		shutdown();
		m_device = device;
		m_shader_dir = shader_dir;
		m_color_format = color_format;
		m_linear_clamp = linear_clamp;
		m_vs = create_shader("DecalVS", SDL_GPU_SHADERSTAGE_VERTEX, 0, 1, 2);
		m_fs = create_shader("DecalPS", SDL_GPU_SHADERSTAGE_FRAGMENT, 2, 0, 3);
		if (!m_vs || !m_fs) { shutdown(); return false; }
		for (u32 b = 0; b < 3; ++b)
		{
			m_pipeline[b] = create_pipeline(b);
			if (!m_pipeline[b]) { shutdown(); return false; }
		}
		SDL_GPUSamplerCreateInfo sci{};
		sci.min_filter = sci.mag_filter = SDL_GPU_FILTER_NEAREST;
		sci.mipmap_mode = SDL_GPU_SAMPLERMIPMAPMODE_NEAREST;
		sci.address_mode_u = sci.address_mode_v = sci.address_mode_w = SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE;
		m_point_clamp = SDL_CreateGPUSampler(m_device, &sci);
		if (!m_point_clamp) { shutdown(); return false; }
		m_ready = true;
		log("ready (lit / multiply / additive box projection)");
		return true;
	}

	void SdlGpuDecals::shutdown()
	{
		if (m_device)
		{
			for (auto& p : m_pipeline) if (p) { SDL_ReleaseGPUGraphicsPipeline(m_device, p); p = nullptr; }
			if (m_vs) { SDL_ReleaseGPUShader(m_device, m_vs); m_vs = nullptr; }
			if (m_fs) { SDL_ReleaseGPUShader(m_device, m_fs); m_fs = nullptr; }
			if (m_point_clamp) { SDL_ReleaseGPUSampler(m_device, m_point_clamp); m_point_clamp = nullptr; }
			if (m_instances) SDL_ReleaseGPUBuffer(m_device, m_instances);
			if (m_upload) SDL_ReleaseGPUTransferBuffer(m_device, m_upload);
		}
		m_instances = nullptr;
		m_upload = nullptr;
		m_instance_cap = 0;
		m_gpu.clear(); m_batches.clear();
		m_ready = false;
		m_device = nullptr;
	}

	bool SdlGpuDecals::prepare(SDL_GPUCommandBuffer* cmd, const std::vector<decals::Decal>& list)
	{
		if (!m_ready || !cmd) return false;
		if (!decals::build(list, m_gpu, m_batches)) return false;
		const u32 bytes = (u32)(m_gpu.size() * sizeof(decals::GpuDecal));
		if (bytes > m_instance_cap)
		{
			if (m_instances) SDL_ReleaseGPUBuffer(m_device, m_instances);   // released once the GPU is done with it
			if (m_upload) SDL_ReleaseGPUTransferBuffer(m_device, m_upload);
			u32 cap = (std::max)(64u * 1024u, m_instance_cap);
			while (cap < bytes) cap *= 2;
			SDL_GPUBufferCreateInfo bci{};
			bci.usage = SDL_GPU_BUFFERUSAGE_GRAPHICS_STORAGE_READ;
			bci.size = cap;
			m_instances = SDL_CreateGPUBuffer(m_device, &bci);
			SDL_GPUTransferBufferCreateInfo tci{};
			tci.usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD;
			tci.size = cap;
			m_upload = SDL_CreateGPUTransferBuffer(m_device, &tci);
			if (!m_instances || !m_upload) { m_instance_cap = 0; log(std::string("buffer allocation failed: ") + SDL_GetError()); return false; }
			m_instance_cap = cap;
		}
		void* mapped = SDL_MapGPUTransferBuffer(m_device, m_upload, true);
		if (!mapped) return false;
		std::memcpy(mapped, m_gpu.data(), bytes);
		SDL_UnmapGPUTransferBuffer(m_device, m_upload);
		SDL_GPUCopyPass* copy = SDL_BeginGPUCopyPass(cmd);
		SDL_GPUTransferBufferLocation s{ m_upload, 0 };
		SDL_GPUBufferRegion d{ m_instances, 0, bytes };
		SDL_UploadToGPUBuffer(copy, &s, &d, true);
		SDL_EndGPUCopyPass(copy);
		return true;
	}

	void SdlGpuDecals::draw(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* color, SDL_GPUTexture* depth, u32 w, u32 h,
		const View& view, const SdlGpuParticles::Environment& env)
	{
		if (!m_ready || !cmd || !color || !depth || m_batches.empty() || w == 0 || h == 0) return;
		SDL_GPUColorTargetInfo ct{};
		ct.texture = color;
		ct.load_op = SDL_GPU_LOADOP_LOAD;
		ct.store_op = SDL_GPU_STOREOP_STORE;
		SDL_GPURenderPass* pass = SDL_BeginGPURenderPass(cmd, &ct, 1, nullptr);
		if (!pass) return;
		SDL_GPUViewport vp{ 0, 0, (float)w, (float)h, 0.0f, 1.0f };
		SDL_SetGPUViewport(pass, &vp);

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
		f.counts[0] = env.lights ? env.point_lights : 0; f.counts[1] = env.lights ? env.spot_lights : 0; f.counts[2] = env.tonemapper;
		static const u8 no_lights[LIGHT_BYTES] = {};
		SDL_PushGPUVertexUniformData(cmd, 0, &f, sizeof(f));
		SDL_PushGPUFragmentUniformData(cmd, 0, &f, sizeof(f));
		SDL_PushGPUFragmentUniformData(cmd, 2, env.lights ? env.lights : no_lights, LIGHT_BYTES);

		auto& reg = ResourceRegistry::instance();
		Texture* white = reg.white_texture();
		SDL_GPUTexture* white_tex = white ? white->texture() : nullptr;
		SDL_GPUBuffer* sb[1] = { m_instances };
		SDL_GPUGraphicsPipeline* bound = nullptr;
		for (const auto& b : m_batches)
		{
			SDL_GPUGraphicsPipeline* p = m_pipeline[(std::min)(b.blend, 2u)];
			if (p != bound)
			{
				SDL_BindGPUGraphicsPipeline(pass, p);
				SDL_BindGPUVertexStorageBuffers(pass, 0, sb, 1);
				bound = p;
			}
			Material* mat = b.material != id::invalid_id ? reg.get_material(b.material) : nullptr;
			Texture* t = mat ? mat->albedo_texture() : nullptr;
			const bool has_tex = t && t->is_valid() && t->texture();
			DBatch pb{};
			pb.base = b.first;
			pb.blend = b.blend;
			pb.has_texture = has_tex ? 1u : 0u;
			if (mat) { const auto& bc = mat->properties().base_color; pb.base_color[0] = bc.x; pb.base_color[1] = bc.y; pb.base_color[2] = bc.z; pb.base_color[3] = bc.w; }
			else { pb.base_color[0] = pb.base_color[1] = pb.base_color[2] = pb.base_color[3] = 1.0f; }
			SDL_PushGPUVertexUniformData(cmd, 1, &pb, sizeof(pb));
			SDL_PushGPUFragmentUniformData(cmd, 1, &pb, sizeof(pb));
			SDL_GPUTextureSamplerBinding tb[2] = {
				{ has_tex ? t->texture() : (white_tex ? white_tex : depth), m_linear_clamp },
				{ depth, m_point_clamp },
			};
			SDL_BindGPUFragmentSamplers(pass, 0, tb, 2);
			SDL_DrawGPUPrimitives(pass, 36, b.count, 0, 0);
		}
		SDL_EndGPURenderPass(pass);
	}
}
