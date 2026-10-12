#include "SdlGpuParticles.h"
#include "SdlGpuShaderFormat.h"
#include "SdlGpuResources.h"
#include "../../Common/Platform.h"
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <fstream>

namespace vortex::graphics::sdlgpu
{
	namespace
	{
		void log(const std::string& s) { platform::debug_output(("[particles] " + s + "\n").c_str()); }

		// byte-matched to PFrame / PBatch / SnapCB in particles.metal
		struct PFrame
		{
			DirectX::XMFLOAT4X4 view_projection;
			float cam_right[4], cam_up[4], cam_forward[4], eye[4];
			float depth_params[4];
			float fog[4], fog2[4];
			float sun_dir[4], sun_color[4];
			u32 counts[4];
		};
		static_assert(sizeof(PFrame) == 224, "PFrame must byte-match particles.metal");

		struct PBatch
		{
			u32 base, mode, tiles_x, tiles_y;
			u32 frame_blend, lit, blend, has_texture;
			float soft_inv, emissive, pad0, pad1;
		};
		static_assert(sizeof(PBatch) == 48, "PBatch must byte-match particles.metal");

		struct SnapCB { float depth_params[4]; float src_size[2]; float dst_size[2]; };
		static_assert(sizeof(SnapCB) == 32, "SnapCB must byte-match particles.metal");

		constexpr u32 LIGHT_BYTES = 1024;   // point (16 x 32) + spot (8 x 64) lights of LightBufferData
		constexpr u32 MODE_RIBBON = 16;

		u32 grow(u32 needed, u32 current)
		{
			if (needed <= current) return current;
			u32 n = std::max(64u * 1024u, current);
			while (n < needed && n < 0x40000000u) n *= 2;
			return std::max(n, needed);
		}
	}

	// `entry` of the "particles" shader set: an entrypoint inside particles.metal on Metal, or its own
	// particles.<Entry>.spv module on Vulkan (SdlGpuShaderFormat.h).
	SDL_GPUShader* SdlGpuParticles::create_shader(const char* entry, SDL_GPUShaderStage stage, u32 samplers, u32 storage, u32 uniforms)
	{
		const std::string file = shaderfmt::module_file("particles", entry);
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

	SDL_GPUGraphicsPipeline* SdlGpuParticles::create_pipeline(SDL_GPUShader* vs, SDL_GPUShader* fs, bool ribbon, u32 blend)
	{
		SDL_GPUColorTargetDescription color{};
		color.format = m_color_format;
		color.blend_state.enable_blend = true;
		color.blend_state.color_blend_op = SDL_GPU_BLENDOP_ADD;
		color.blend_state.alpha_blend_op = SDL_GPU_BLENDOP_ADD;
		color.blend_state.src_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
		color.blend_state.dst_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA;
		switch (blend)
		{
		case particles::BLEND_ADDITIVE:
			color.blend_state.src_color_blendfactor = SDL_GPU_BLENDFACTOR_SRC_ALPHA;
			color.blend_state.dst_color_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
			color.blend_state.src_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ZERO;
			color.blend_state.dst_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
			break;
		case particles::BLEND_PREMULTIPLIED:
			color.blend_state.src_color_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
			color.blend_state.dst_color_blendfactor = SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA;
			break;
		default:
			color.blend_state.src_color_blendfactor = SDL_GPU_BLENDFACTOR_SRC_ALPHA;
			color.blend_state.dst_color_blendfactor = SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA;
			break;
		}
		SDL_GPUVertexBufferDescription vb{ 0, sizeof(particles::RibbonVertex), SDL_GPU_VERTEXINPUTRATE_VERTEX, 0 };
		SDL_GPUVertexAttribute attrs[3] = {
			{ 0, 0, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3, 0 },
			{ 1, 0, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, 12 },
			{ 2, 0, SDL_GPU_VERTEXELEMENTFORMAT_UBYTE4_NORM, 20 },
		};
		SDL_GPUGraphicsPipelineCreateInfo pci{};
		pci.vertex_shader = vs;
		pci.fragment_shader = fs;
		if (ribbon)
		{
			pci.vertex_input_state.vertex_buffer_descriptions = &vb;
			pci.vertex_input_state.num_vertex_buffers = 1;
			pci.vertex_input_state.vertex_attributes = attrs;
			pci.vertex_input_state.num_vertex_attributes = 3;
		}
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
		if (!p) log(std::string("pipeline failed: ") + SDL_GetError());
		return p;
	}

	bool SdlGpuParticles::initialize(SDL_GPUDevice* device, const std::string& shader_dir, SDL_GPUTextureFormat color_format,
		SDL_GPUSampler* linear_wrap, SDL_GPUSampler* linear_clamp)
	{
		shutdown();
		m_device = device;
		m_color_format = color_format;
		m_linear_wrap = linear_wrap;
		m_linear_clamp = linear_clamp;
		m_shader_dir = shader_dir;

		m_vs_particle = create_shader("ParticleVS", SDL_GPU_SHADERSTAGE_VERTEX, 0, 1, 3);
		m_vs_ribbon = create_shader("RibbonVS", SDL_GPU_SHADERSTAGE_VERTEX, 0, 0, 3);
		m_fs_particle = create_shader("ParticlePS", SDL_GPU_SHADERSTAGE_FRAGMENT, 2, 0, 2);
		m_vs_snap = create_shader("SnapVS", SDL_GPU_SHADERSTAGE_VERTEX, 0, 0, 0);
		m_fs_snap = create_shader("SnapPS", SDL_GPU_SHADERSTAGE_FRAGMENT, 1, 0, 1);
		if (!m_vs_particle || !m_vs_ribbon || !m_fs_particle || !m_vs_snap || !m_fs_snap) { shutdown(); return false; }
		for (u32 b = 0; b < 3; ++b)
		{
			m_billboard[b] = create_pipeline(m_vs_particle, m_fs_particle, false, b);
			m_ribbon[b] = create_pipeline(m_vs_ribbon, m_fs_particle, true, b);
			if (!m_billboard[b] || !m_ribbon[b]) { shutdown(); return false; }
		}
		{
			SDL_GPUColorTargetDescription color{};
			color.format = SDL_GPU_TEXTUREFORMAT_R32_FLOAT;
			SDL_GPUGraphicsPipelineCreateInfo pci{};
			pci.vertex_shader = m_vs_snap;
			pci.fragment_shader = m_fs_snap;
			pci.primitive_type = SDL_GPU_PRIMITIVETYPE_TRIANGLELIST;
			pci.rasterizer_state.fill_mode = SDL_GPU_FILLMODE_FILL;
			pci.rasterizer_state.cull_mode = SDL_GPU_CULLMODE_NONE;
			pci.rasterizer_state.front_face = SDL_GPU_FRONTFACE_CLOCKWISE;
			pci.multisample_state.sample_count = SDL_GPU_SAMPLECOUNT_1;
			pci.target_info.color_target_descriptions = &color;
			pci.target_info.num_color_targets = 1;
			m_snap = SDL_CreateGPUGraphicsPipeline(m_device, &pci);
			if (!m_snap) log(std::string("collision snapshot pipeline failed (depth collision off): ") + SDL_GetError());
		}
		SDL_GPUSamplerCreateInfo sci{};
		sci.min_filter = sci.mag_filter = SDL_GPU_FILTER_NEAREST;
		sci.mipmap_mode = SDL_GPU_SAMPLERMIPMAPMODE_NEAREST;
		sci.address_mode_u = sci.address_mode_v = sci.address_mode_w = SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE;
		m_point_clamp = SDL_CreateGPUSampler(m_device, &sci);
		if (!m_point_clamp) { shutdown(); return false; }
		m_ready = true;
		log("ready (billboards, ribbons, soft particles, depth collision)");
		return true;
	}

	void SdlGpuParticles::shutdown()
	{
		if (m_device)
		{
			for (auto& p : m_billboard) if (p) { SDL_ReleaseGPUGraphicsPipeline(m_device, p); p = nullptr; }
			for (auto& p : m_ribbon) if (p) { SDL_ReleaseGPUGraphicsPipeline(m_device, p); p = nullptr; }
			if (m_snap) { SDL_ReleaseGPUGraphicsPipeline(m_device, m_snap); m_snap = nullptr; }
			SDL_GPUShader** shaders[] = { &m_vs_particle, &m_vs_ribbon, &m_fs_particle, &m_vs_snap, &m_fs_snap };
			for (auto** s : shaders) if (*s) { SDL_ReleaseGPUShader(m_device, *s); *s = nullptr; }
			if (m_point_clamp) { SDL_ReleaseGPUSampler(m_device, m_point_clamp); m_point_clamp = nullptr; }
			if (m_instances) SDL_ReleaseGPUBuffer(m_device, m_instances);
			if (m_vertices) SDL_ReleaseGPUBuffer(m_device, m_vertices);
			if (m_indices) SDL_ReleaseGPUBuffer(m_device, m_indices);
			if (m_upload) SDL_ReleaseGPUTransferBuffer(m_device, m_upload);
			for (auto& s : m_snaps) if (s.buffer) SDL_ReleaseGPUTransferBuffer(m_device, s.buffer);
			if (m_snap_texture) SDL_ReleaseGPUTexture(m_device, m_snap_texture);
		}
		m_instances = m_vertices = m_indices = nullptr;
		m_upload = nullptr;
		m_instance_cap = m_vertex_cap = m_index_cap = m_upload_cap = 0;
		for (auto& s : m_snaps) s = Snap{};
		m_snap_texture = nullptr;
		m_snap_w = m_snap_h = 0;
		m_ready = false;
		m_layer_batches[0] = m_layer_batches[1] = 0;
		m_list.clear();
	}

	bool SdlGpuParticles::ensure_buffers(u32 instance_bytes, u32 vertex_bytes, u32 index_bytes)
	{
		auto remake = [&](SDL_GPUBuffer*& buf, u32& cap, u32 needed, SDL_GPUBufferUsageFlags usage) -> bool
		{
			if (needed == 0 || needed <= cap) return true;
			if (buf) SDL_ReleaseGPUBuffer(m_device, buf);   // released once the GPU is done with it
			cap = grow(needed, cap);
			SDL_GPUBufferCreateInfo bci{};
			bci.usage = usage;
			bci.size = cap;
			buf = SDL_CreateGPUBuffer(m_device, &bci);
			if (!buf) { cap = 0; log(std::string("buffer allocation failed: ") + SDL_GetError()); return false; }
			return true;
		};
		if (!remake(m_instances, m_instance_cap, instance_bytes, SDL_GPU_BUFFERUSAGE_GRAPHICS_STORAGE_READ)) return false;
		if (!remake(m_vertices, m_vertex_cap, vertex_bytes, SDL_GPU_BUFFERUSAGE_VERTEX)) return false;
		if (!remake(m_indices, m_index_cap, index_bytes, SDL_GPU_BUFFERUSAGE_INDEX)) return false;
		const u32 total = instance_bytes + vertex_bytes + index_bytes;
		if (total > m_upload_cap)
		{
			if (m_upload) SDL_ReleaseGPUTransferBuffer(m_device, m_upload);
			m_upload_cap = grow(total, m_upload_cap);
			SDL_GPUTransferBufferCreateInfo tci{};
			tci.usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD;
			tci.size = m_upload_cap;
			m_upload = SDL_CreateGPUTransferBuffer(m_device, &tci);
			if (!m_upload) { m_upload_cap = 0; log(std::string("upload buffer allocation failed: ") + SDL_GetError()); return false; }
		}
		return true;
	}

	bool SdlGpuParticles::prepare(SDL_GPUCommandBuffer* cmd, const View& view, u32 world)
	{
		m_layer_batches[0] = m_layer_batches[1] = 0;
		if (!m_ready || !cmd) return false;
		particles::ViewInfo vi{};
		std::memcpy(vi.view_proj, &view.view_projection, sizeof(vi.view_proj));
		vi.eye[0] = view.eye.x; vi.eye[1] = view.eye.y; vi.eye[2] = view.eye.z;
		vi.right[0] = view.right.x; vi.right[1] = view.right.y; vi.right[2] = view.right.z;
		vi.up[0] = view.up.x; vi.up[1] = view.up.y; vi.up[2] = view.up.z;
		vi.forward[0] = view.forward.x; vi.forward[1] = view.forward.y; vi.forward[2] = view.forward.z;
		vi.ortho = view.ortho;
		if (!particles::gather(world, vi, m_list)) return false;
		for (const auto& b : m_list.batches) if (b.layer < 2) ++m_layer_batches[b.layer];

		const u32 ib = (u32)(m_list.instances.size() * sizeof(particles::GpuParticle));
		const u32 vb = (u32)(m_list.ribbon_vertices.size() * sizeof(particles::RibbonVertex));
		const u32 xb = (u32)(m_list.ribbon_indices.size() * sizeof(u32));
		if (!ensure_buffers(ib, vb, xb)) { m_layer_batches[0] = m_layer_batches[1] = 0; return false; }
		u8* mapped = static_cast<u8*>(SDL_MapGPUTransferBuffer(m_device, m_upload, true));
		if (!mapped) { m_layer_batches[0] = m_layer_batches[1] = 0; return false; }
		if (ib) std::memcpy(mapped, m_list.instances.data(), ib);
		if (vb) std::memcpy(mapped + ib, m_list.ribbon_vertices.data(), vb);
		if (xb) std::memcpy(mapped + ib + vb, m_list.ribbon_indices.data(), xb);
		SDL_UnmapGPUTransferBuffer(m_device, m_upload);
		SDL_GPUCopyPass* copy = SDL_BeginGPUCopyPass(cmd);
		if (ib) { SDL_GPUTransferBufferLocation s{ m_upload, 0 }; SDL_GPUBufferRegion d{ m_instances, 0, ib }; SDL_UploadToGPUBuffer(copy, &s, &d, true); }
		if (vb) { SDL_GPUTransferBufferLocation s{ m_upload, ib }; SDL_GPUBufferRegion d{ m_vertices, 0, vb }; SDL_UploadToGPUBuffer(copy, &s, &d, true); }
		if (xb) { SDL_GPUTransferBufferLocation s{ m_upload, ib + vb }; SDL_GPUBufferRegion d{ m_indices, 0, xb }; SDL_UploadToGPUBuffer(copy, &s, &d, true); }
		SDL_EndGPUCopyPass(copy);
		return m_layer_batches[0] + m_layer_batches[1] > 0;
	}

	void SdlGpuParticles::draw_layer(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* color, SDL_GPUTexture* depth, u32 w, u32 h, u32 layer,
		const View& view, const Environment& env)
	{
		if (!m_ready || !has_layer(layer) || !color || !depth) return;
		SDL_GPUColorTargetInfo ct{};
		ct.texture = color;
		ct.load_op = SDL_GPU_LOADOP_LOAD;
		ct.store_op = SDL_GPU_STOREOP_STORE;
		SDL_GPURenderPass* pass = SDL_BeginGPURenderPass(cmd, &ct, 1, nullptr);
		if (!pass) return;
		SDL_GPUViewport vp{ 0, 0, (float)w, (float)h, 0.0f, 1.0f };
		SDL_SetGPUViewport(pass, &vp);

		PFrame f{};
		f.view_projection = layer == 0 ? view.view_projection : view.viewmodel_projection;
		f.cam_right[0] = view.right.x; f.cam_right[1] = view.right.y; f.cam_right[2] = view.right.z;
		f.cam_up[0] = view.up.x; f.cam_up[1] = view.up.y; f.cam_up[2] = view.up.z;
		f.cam_forward[0] = view.forward.x; f.cam_forward[1] = view.forward.y; f.cam_forward[2] = view.forward.z;
		f.eye[0] = view.eye.x; f.eye[1] = view.eye.y; f.eye[2] = view.eye.z; f.eye[3] = 1.0f;
		f.depth_params[0] = layer == 0 ? view.near_clip : view.vm_near_clip; f.depth_params[1] = layer == 0 ? view.far_clip : view.vm_far_clip;
		f.depth_params[2] = (layer == 0 && view.ortho) ? 1.0f : 0.0f;
		f.fog[0] = env.fog_color.x; f.fog[1] = env.fog_color.y; f.fog[2] = env.fog_color.z; f.fog[3] = env.fog_density;
		f.fog2[0] = env.fog_height_y; f.fog2[1] = env.fog_height_falloff; f.fog2[2] = env.scene_exposure; f.fog2[3] = env.fog_sun_glow;
		f.sun_dir[0] = env.sun_direction.x; f.sun_dir[1] = env.sun_direction.y; f.sun_dir[2] = env.sun_direction.z; f.sun_dir[3] = env.sun_intensity;
		f.sun_color[0] = env.sun_color.x; f.sun_color[1] = env.sun_color.y; f.sun_color[2] = env.sun_color.z; f.sun_color[3] = env.ambient;
		f.counts[0] = env.lights ? env.point_lights : 0; f.counts[1] = env.lights ? env.spot_lights : 0; f.counts[2] = env.tonemapper;
		static const u8 no_lights[LIGHT_BYTES] = {};
		SDL_PushGPUVertexUniformData(cmd, 0, &f, sizeof(f));
		SDL_PushGPUFragmentUniformData(cmd, 0, &f, sizeof(f));
		SDL_PushGPUVertexUniformData(cmd, 2, env.lights ? env.lights : no_lights, LIGHT_BYTES);

		auto& reg = ResourceRegistry::instance();
		Texture* white = reg.white_texture();
		SDL_GPUTexture* white_tex = white ? white->texture() : nullptr;
		SDL_GPUGraphicsPipeline* bound = nullptr;
		for (const auto& b : m_list.batches)
		{
			if (b.layer != layer || b.count == 0) continue;
			const bool ribbon = b.kind == 1;
			SDL_GPUGraphicsPipeline* p = ribbon ? m_ribbon[std::min(b.blend, 2u)] : m_billboard[std::min(b.blend, 2u)];
			if (p != bound)
			{
				SDL_BindGPUGraphicsPipeline(pass, p);
				bound = p;
				if (ribbon)
				{
					SDL_GPUBufferBinding vbind{ m_vertices, 0 };
					SDL_BindGPUVertexBuffers(pass, 0, &vbind, 1);
					SDL_GPUBufferBinding ibind{ m_indices, 0 };
					SDL_BindGPUIndexBuffer(pass, &ibind, SDL_GPU_INDEXELEMENTSIZE_32BIT);
				}
				else
				{
					SDL_GPUBuffer* sb[1] = { m_instances };
					SDL_BindGPUVertexStorageBuffers(pass, 0, sb, 1);
				}
			}
			Texture* t = b.texture != particles::NO_TEXTURE ? reg.get_texture((id::id_type)b.texture) : nullptr;
			const bool has_tex = t && t->is_valid();
			PBatch pb{};
			pb.base = ribbon ? 0u : b.first;
			pb.mode = ribbon ? MODE_RIBBON : b.render_mode;
			pb.tiles_x = std::max(1u, b.tiles_x); pb.tiles_y = std::max(1u, b.tiles_y);
			pb.frame_blend = b.frame_blend; pb.lit = b.lit; pb.blend = b.blend; pb.has_texture = has_tex ? 1u : 0u;
			pb.soft_inv = b.soft_distance > 1e-4f ? 1.0f / b.soft_distance : 0.0f;
			pb.emissive = b.emissive;
			SDL_PushGPUVertexUniformData(cmd, 1, &pb, sizeof(pb));
			SDL_PushGPUFragmentUniformData(cmd, 1, &pb, sizeof(pb));
			SDL_GPUTextureSamplerBinding tb[2] = {
				{ has_tex ? t->texture() : white_tex, m_linear_wrap },
				{ depth, m_point_clamp },
			};
			SDL_BindGPUFragmentSamplers(pass, 0, tb, 2);
			if (ribbon) SDL_DrawGPUIndexedPrimitives(pass, b.count, 1, b.first, 0, 0);
			else SDL_DrawGPUPrimitives(pass, 6, b.count, 0, 0);
		}
		SDL_EndGPURenderPass(pass);
	}

	void SdlGpuParticles::capture_depth(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* depth, u32 w, u32 h, const View& view)
	{
		if (!m_ready || !m_snap || !cmd || !depth || w < 16 || h < 16) return;
		if (m_last_capture_frame == m_frame) return;   // once per frame (the first main surface)
		if (!particles::wants_depth_snapshot()) return;
		u32 sw = std::max(16u, std::min(640u, w / 4));
		u32 sh = std::max(9u, (u32)((u64)h * sw / w));
		if (!m_snap_texture || m_snap_w != sw || m_snap_h != sh)
		{
			if (m_snap_texture) SDL_ReleaseGPUTexture(m_device, m_snap_texture);
			SDL_GPUTextureCreateInfo tci{};
			tci.type = SDL_GPU_TEXTURETYPE_2D;
			tci.format = SDL_GPU_TEXTUREFORMAT_R32_FLOAT;
			tci.usage = SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER;
			tci.width = sw; tci.height = sh; tci.layer_count_or_depth = 1; tci.num_levels = 1;
			tci.sample_count = SDL_GPU_SAMPLECOUNT_1;
			m_snap_texture = SDL_CreateGPUTexture(m_device, &tci);
			m_snap_w = sw; m_snap_h = sh;
			if (!m_snap_texture) { log(std::string("snapshot texture failed: ") + SDL_GetError()); m_snap_w = m_snap_h = 0; return; }
		}
		Snap& slot = m_snaps[m_snap_next];
		const u32 bytes = sw * sh * 4;
		if (!slot.buffer || slot.bytes < bytes)
		{
			if (slot.buffer) SDL_ReleaseGPUTransferBuffer(m_device, slot.buffer);
			SDL_GPUTransferBufferCreateInfo tci{};
			tci.usage = SDL_GPU_TRANSFERBUFFERUSAGE_DOWNLOAD;
			tci.size = bytes;
			slot.buffer = SDL_CreateGPUTransferBuffer(m_device, &tci);
			slot.bytes = slot.buffer ? bytes : 0;
			if (!slot.buffer) return;
		}

		SDL_GPUColorTargetInfo ct{};
		ct.texture = m_snap_texture;
		ct.load_op = SDL_GPU_LOADOP_DONT_CARE;
		ct.store_op = SDL_GPU_STOREOP_STORE;
		SDL_GPURenderPass* pass = SDL_BeginGPURenderPass(cmd, &ct, 1, nullptr);
		if (!pass) return;
		SDL_BindGPUGraphicsPipeline(pass, m_snap);
		SDL_GPUViewport vp{ 0, 0, (float)sw, (float)sh, 0.0f, 1.0f };
		SDL_SetGPUViewport(pass, &vp);
		SnapCB cb{ { view.near_clip, view.far_clip, view.ortho ? 1.0f : 0.0f, 0.0f }, { (float)w, (float)h }, { (float)sw, (float)sh } };
		SDL_PushGPUFragmentUniformData(cmd, 0, &cb, sizeof(cb));
		SDL_GPUTextureSamplerBinding tb{ depth, m_point_clamp };
		SDL_BindGPUFragmentSamplers(pass, 0, &tb, 1);
		SDL_DrawGPUPrimitives(pass, 3, 1, 0, 0);
		SDL_EndGPURenderPass(pass);

		SDL_GPUCopyPass* copy = SDL_BeginGPUCopyPass(cmd);
		SDL_GPUTextureRegion region{};
		region.texture = m_snap_texture; region.w = sw; region.h = sh; region.d = 1;
		SDL_GPUTextureTransferInfo dst{ slot.buffer, 0, sw, sh };
		SDL_DownloadFromGPUTexture(copy, &region, &dst);
		SDL_EndGPUCopyPass(copy);

		slot.w = sw; slot.h = sh; slot.frame = m_frame; slot.pending = true;
		particles::DepthView& dv = slot.view;
		dv.eye[0] = view.eye.x; dv.eye[1] = view.eye.y; dv.eye[2] = view.eye.z;
		dv.right[0] = view.right.x; dv.right[1] = view.right.y; dv.right[2] = view.right.z;
		dv.up[0] = view.up.x; dv.up[1] = view.up.y; dv.up[2] = view.up.z;
		dv.forward[0] = view.forward.x; dv.forward[1] = view.forward.y; dv.forward[2] = view.forward.z;
		dv.tan_half_x = view.tan_half_x; dv.tan_half_y = view.tan_half_y;
		dv.near_clip = view.near_clip; dv.far_clip = view.far_clip; dv.ortho = view.ortho;
		m_snap_next = (m_snap_next + 1) % SNAP_SLOTS;
		m_last_capture_frame = m_frame;
	}

	void SdlGpuParticles::begin_frame()
	{
		++m_frame;
		if (!m_ready) return;
		// newest capture that the GPU has certainly finished (SDL keeps <= 3 frames in flight)
		Snap* best = nullptr;
		for (auto& s : m_snaps)
			if (s.pending && s.frame + 3 <= m_frame && (!best || s.frame > best->frame)) best = &s;
		if (!best) return;
		const u32 floats = best->w * best->h;
		const float* src = static_cast<const float*>(SDL_MapGPUTransferBuffer(m_device, best->buffer, false));
		if (src)
		{
			m_snap_scratch.assign(src, src + floats);
			SDL_UnmapGPUTransferBuffer(m_device, best->buffer);
			particles::submit_depth_snapshot(m_snap_scratch.data(), best->w, best->h, best->view);
		}
		const u64 used = best->frame;
		for (auto& s : m_snaps) if (s.pending && s.frame <= used) s.pending = false;
	}
}
