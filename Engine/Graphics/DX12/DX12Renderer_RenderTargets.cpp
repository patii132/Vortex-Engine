#include "DX12Renderer_Internal.h"
#include "../../Common/VerboseLog.h"

namespace vortex::graphics::dx12
{
	u32 DX12Renderer::create_render_target(u32 width, u32 height)
	{
		if (!m_initialized || width == 0 || height == 0) return 0;
		if (m_render_targets.size() >= MAX_RENDER_TARGETS) return 0;
		
		auto target = std::make_unique<DX12RenderTarget>();
		if (!target->initialize(DX12Core::instance().device(), width, height))
		{
			return 0;
		}
		
		u32 id = m_next_render_target_id++;
		m_render_targets[id] = std::move(target);
		
		VORTEX_VLOG(("Created render target ID " + std::to_string(id) + 
			" (" + std::to_string(width) + "x" + std::to_string(height) + ")\n").c_str());
		
		return id;
	}
	

	void DX12Renderer::destroy_render_target(u32 target_id)
	{
		auto it = m_render_targets.find(target_id);
		if (it != m_render_targets.end())
		{
			m_command_queue.flush(); // Ensure GPU is done with this target
			m_render_targets.erase(it);
			VORTEX_VLOG(("Destroyed render target ID " + std::to_string(target_id) + "\n").c_str());
		}
	}
	

	bool DX12Renderer::resize_render_target(u32 target_id, u32 width, u32 height)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end()) return false;
		
		m_command_queue.flush(); // Ensure GPU is done
		return it->second->resize(DX12Core::instance().device(), width, height);
	}
	

	bool DX12Renderer::has_render_target(u32 target_id) const
	{
		return m_render_targets.find(target_id) != m_render_targets.end();
	}
	

	void DX12Renderer::render_to_target(u32 target_id, const ViewportCamera& camera, bool render_grid, bool render_gizmos)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end()) return;

		render_scene_to_target(it->second.get(), camera, render_grid, render_gizmos);
	}


	void DX12Renderer::render_scene_to_target(DX12RenderTarget* target, const ViewportCamera& camera, bool render_grid, bool render_gizmos)
	{
		using namespace DirectX;
		
		if (!target || !target->is_initialized()) return;
		
		// Get current back buffer index for command allocator
		u32 idx = m_swapchain.current_back_buffer_index();
		
		// Ensure previous work is complete before rendering to secondary target
		m_command_queue.flush();
		upload_staged_bone_palettes();   // GPU idle — safe; previews then render the freshly-swapped pose
		m_command_allocators[idx]->Reset();
		
		auto* pso = m_render_queue.empty() ? m_pipeline.pipeline_state() :
			(m_wireframe_mode ? m_pipeline_3d.wireframe_pso() : m_pipeline_3d.pipeline_state());
		m_command_list->Reset(m_command_allocators[idx].Get(), pso);
		
		// Transition render target to render target state
		target->transition_to_render_target(m_command_list.Get());
		
		// Setup viewport and scissor
		D3D12_VIEWPORT vp{};
		vp.Width = (float)target->width();
		vp.Height = (float)target->height();
		vp.MaxDepth = 1.0f;
		
		D3D12_RECT sc{};
		sc.right = target->width();
		sc.bottom = target->height();
		
		m_command_list->RSSetViewports(1, &vp);
		m_command_list->RSSetScissorRects(1, &sc);
		
		// Clear render target and depth - skybox will fill background
		FLOAT clear_color[4] = { 0.0f, 0.0f, 0.0f, 1.0f }; // Black - skybox renders over this
		auto rtv = target->rtv();
		auto dsv = target->dsv();
		m_command_list->ClearRenderTargetView(rtv, clear_color, 0, nullptr);
		m_command_list->ClearDepthStencilView(dsv, D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0, 0, nullptr);
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, &dsv);
		m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		
		// Build view/projection matrix for this camera
		XMVECTOR eye = XMLoadFloat3(&camera.position);
		XMVECTOR at = XMLoadFloat3(&camera.target);
		XMVECTOR up = XMLoadFloat3(&camera.up);
		XMMATRIX view = XMMatrixLookAtLH(eye, at, up);
		
		float aspect = (float)target->width() / (float)target->height();
		XMMATRIX proj;
		if (camera.orthographic)
		{
			proj = XMMatrixOrthographicLH(camera.ortho_size * aspect, camera.ortho_size, 
				camera.near_clip, camera.far_clip);
		}
		else
		{
			proj = XMMatrixPerspectiveFovLH(XMConvertToRadians(camera.fov_degrees), aspect, 
				camera.near_clip, camera.far_clip);
		}
		
		XMMATRIX vp_matrix = view * proj;
		
		// Update per-frame constants with this camera's view/proj
		PerFrameConstants frame_constants = m_frame_constants;
		XMStoreFloat4x4(&frame_constants.view_projection, vp_matrix);
		frame_constants.camera_position = camera.position;

		// Pull in the CURRENT lighting state. Offscreen previews (AssetPreviewRenderer) set ambient + directional
		// + point lights via the API right before this call, but m_frame_constants is only refreshed by the main
		// loop — so without this the preview rendered with stale (dark) lighting and NO point lights. Copy them
		// here AND upload the point lights to the light CB so they actually illuminate the preview.
		frame_constants.light_direction = m_light_direction;
		frame_constants.directional_intensity = m_directional_intensity;
		frame_constants.light_color = m_light_color;
		frame_constants.ambient_strength = m_ambient_strength;
		frame_constants.point_light_count = static_cast<u32>((std::min)(m_point_lights.size(), static_cast<size_t>(MAX_POINT_LIGHTS)));
		frame_constants.spot_light_count = static_cast<u32>((std::min)(m_spot_lights.size(), static_cast<size_t>(MAX_SPOT_LIGHTS)));
		frame_constants.ssao_enabled = 0.0f;   // #32: previews never sample the main view's AO
		{
			const float env_on = (m_skybox_enabled && (m_skybox_mode == SkyboxMode::Gradient || m_skybox_mode == SkyboxMode::Texture)) ? 1.0f : 0.0f;
			const bool env_map = env_on > 0.5f && m_env_map != id::invalid_id && m_env_mips > 0;
			frame_constants.env_sky = DirectX::XMFLOAT4(m_env_sky.x, m_env_sky.y, m_env_sky.z, env_map ? 2.0f : env_on);   // 2 = a reflection map is bound at t11
			frame_constants.env_horizon = DirectX::XMFLOAT4(m_env_horizon.x, m_env_horizon.y, m_env_horizon.z, env_map ? (float)m_env_mips : 0.0f);
		}
		frame_constants.env_ground = DirectX::XMFLOAT4(m_env_ground.x, m_env_ground.y, m_env_ground.z, 0.0f);
		frame_constants.camera_clip = DirectX::XMFLOAT4(m_near_clip, m_far_clip, 0.0f, 0.0f);
		if (m_light_cb_mapped)
		{
			u8* lptr = static_cast<u8*>(m_light_cb_mapped);
			for (size_t i = 0; i < m_point_lights.size() && i < MAX_POINT_LIGHTS; ++i)
			{
				GPUPointLight gl{};
				gl.position = m_point_lights[i].position; gl.range = m_point_lights[i].range;
				gl.color = m_point_lights[i].color; gl.intensity = m_point_lights[i].intensity;
				memcpy(lptr + i * sizeof(GPUPointLight), &gl, sizeof(GPUPointLight));
			}
			// Spot lights too (same layout as update_light_buffer: after the point block, direction
			// normalized). spot_light_count was already written to b0 above, but the SPOT DATA region
			// held stale bytes from the main loop — previews/secondary viewports lit spots wrongly.
			u8* sptr = lptr + MAX_POINT_LIGHTS * sizeof(GPUPointLight);
			for (size_t i = 0; i < m_spot_lights.size() && i < MAX_SPOT_LIGHTS; ++i)
			{
				GPUSpotLight gs{};
				gs.position = m_spot_lights[i].position; gs.range = m_spot_lights[i].range;
				DirectX::XMFLOAT3 dir = m_spot_lights[i].direction;
				float len = sqrtf(dir.x * dir.x + dir.y * dir.y + dir.z * dir.z);
				if (len > 0.0001f) { dir.x /= len; dir.y /= len; dir.z /= len; }
				gs.direction = dir;
				gs.spot_angle = m_spot_lights[i].spot_angle;
				gs.color = m_spot_lights[i].color;
				gs.intensity = m_spot_lights[i].intensity;
				gs.inner_spot_angle = m_spot_lights[i].inner_spot_angle;
				memcpy(sptr + i * sizeof(GPUSpotLight), &gs, sizeof(GPUSpotLight));
			}
			// CSM (#24) + point shadows (#25): the shared light CB's tail still holds the MAIN
			// camera's shadow data — a preview object could land inside a scene shadow region and
			// render mysteriously dark. Disarm both for offscreen renders (the main loop rewrites
			// the tail next frame).
			{
				u8* tail = lptr + MAX_POINT_LIGHTS * sizeof(GPUPointLight) + MAX_SPOT_LIGHTS * sizeof(GPUSpotLight)
					+ (size_t)MAX_SHADOW_SPOTS * 64 + (size_t)CSM_CASCADES * 64 + 16;
				float off[4] = { 0.0f, 0.0f, 0.0f, 0.0f };
				memcpy(tail, off, 16);   // DirShadowParams: cascade count 0
				float noEntry[4] = { -1.0f, 0.0f, 0.0f, 0.0f };
				for (u32 p = 0; p < MAX_SHADOW_POINTS; ++p)
					memcpy(tail + 16 + (size_t)p * 16, noEntry, 16);   // PointShadows: index -1
			}
		}

		if (m_per_frame_cb_mapped)
			memcpy(m_per_frame_cb_mapped, &frame_constants, sizeof(frame_constants));
		
		// ========== RENDER SKYBOX FIRST (appears behind everything) ==========
		if (m_skybox_pipeline.pipeline_state())
		{
		m_command_list->SetPipelineState(m_skybox_pipeline.pipeline_state());
		m_command_list->SetGraphicsRootSignature(m_skybox_pipeline.root_signature());
			
		// Update skybox constants with THIS camera's inverse VP matrix
		if (m_skybox_cb_mapped)
		{
		auto skybox_ptr = reinterpret_cast<u8*>(m_skybox_cb_mapped);
		memcpy(skybox_ptr, m_skybox_pipeline.get_constants(), m_skybox_pipeline.get_constants_size());
				
		XMMATRIX inv_vp = XMMatrixInverse(nullptr, vp_matrix);
		XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(skybox_ptr), inv_vp);
		}
			
		m_command_list->SetGraphicsRootConstantBufferView(0, m_skybox_cb->GetGPUVirtualAddress());
		m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		m_command_list->DrawInstanced(3, 1, 0, 0);
			
		// Reset render target state after skybox
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, &dsv);
		}
		
		// Render grid if requested
		if (render_grid && m_grid_pipeline.pipeline_state() && m_grid_vertex_count > 0)
		{
			// Update grid constants
			if (m_grid_cb_mapped)
			{
				GridConstants grid_cb{};
				XMStoreFloat4x4(&grid_cb.view_projection, vp_matrix);
				
				XMMATRIX inv_vp = XMMatrixInverse(nullptr, vp_matrix);
				XMStoreFloat4x4(&grid_cb.inverse_view_projection, inv_vp);
				
				grid_cb.camera_position = camera.position;
				grid_cb.grid_spacing = m_grid_spacing;
				grid_cb.grid_extent = m_grid_extent;
				grid_cb.major_line_interval = m_grid_major_interval;
				
				memcpy(m_grid_cb_mapped, &grid_cb, sizeof(grid_cb));
			}
			
			m_command_list->SetPipelineState(m_grid_pipeline.pipeline_state());
			m_command_list->SetGraphicsRootSignature(m_grid_pipeline.root_signature());
			m_command_list->SetGraphicsRootConstantBufferView(0, m_grid_cb->GetGPUVirtualAddress());
			m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_LINELIST);
			m_command_list->IASetVertexBuffers(0, 1, &m_grid_vbv);
			m_command_list->DrawInstanced(m_grid_vertex_count, 1, 0, 0);
		}
		
		// Render 3D objects
		if (!m_render_queue.empty())
		{
			// Custom per-material shaders (assigned .hlsl) must show in previews too — so the Material Editor sphere
			// and the Asset Browser material thumbnail match the scene. Bind the default PBR PSO up front, then swap
			// to a material's custom PSO per object (guarded so we only call SetPipelineState when it actually changes).
			ID3D12PipelineState* default_pso = m_wireframe_mode ? m_pipeline_3d.wireframe_pso() : m_pipeline_3d.pipeline_state();
			m_command_list->SetPipelineState(default_pso);
			ID3D12PipelineState* cur_pso = default_pso;
			m_command_list->SetGraphicsRootSignature(m_pipeline_3d.root_signature());
			m_command_list->SetGraphicsRootConstantBufferView(0, m_per_frame_cb->GetGPUVirtualAddress());
			m_command_list->SetGraphicsRootConstantBufferView(2, m_light_cb->GetGPUVirtualAddress()); // point/spot lights
				// MUST set the SRV heap before any texture descriptor table is bound in the loop below — without
				// it, SetGraphicsRootDescriptorTable removes the device (the crash when a textured model's
				// thumbnail rendered, e.g. opening the Models folder).
				{ auto* _sh = ResourceRegistry::instance().srv_heap(); if (_sh) { ID3D12DescriptorHeap* _hh[] = { _sh }; m_command_list->SetDescriptorHeaps(1, _hh); } }
				// standard.hlsl references the t7 shadow map -> the table must be bound in the PREVIEW path
				// too (same unbound-table rule as the scene pass; the map always exists via eager init).
				if (m_shadow_srv_gpu.ptr != 0)
					m_command_list->SetGraphicsRootDescriptorTable(10, m_shadow_srv_gpu);
				if (m_csm_srv_gpu.ptr != 0)
					m_command_list->SetGraphicsRootDescriptorTable(11, m_csm_srv_gpu);   // t8 (#24)
				if (m_point_srv_gpu.ptr != 0)
					m_command_list->SetGraphicsRootDescriptorTable(12, m_point_srv_gpu); // t9 (#25)
				if (m_ssao_current_srv.ptr != 0)
					m_command_list->SetGraphicsRootDescriptorTable(13, m_ssao_current_srv); // t10 (#32)
				{ auto env = env_map_srv(); if (env.ptr != 0) m_command_list->SetGraphicsRootDescriptorTable(14, env); } // t11 (IBL step 2)
				{
					// t12 / t13 (#200): no scene copies in a preview — the white fallback makes the water blend plainly
					auto& rg = ResourceRegistry::instance(); Texture* white = rg.get_texture(rg.default_white_texture());
					if (white && white->is_valid() && white->srv_gpu().ptr != 0) { m_command_list->SetGraphicsRootDescriptorTable(15, white->srv_gpu()); m_command_list->SetGraphicsRootDescriptorTable(16, white->srv_gpu()); }
				}
				m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
			
			auto& reg = ResourceRegistry::instance();
			size_t objectCount = (std::min)(m_render_queue.size(), static_cast<size_t>(MAX_RENDER_OBJECTS));

			// Per-object CB slots: the buffer holds MAX_DRAW_RUNS (8192) 256-byte slots and its TAIL is
			// shared with the gizmo pass (MAX_GIZMO_ITEMS) — but this loop used to index it with the RAW
			// queue index (up to MAX_RENDER_OBJECTS = 262144), writing far past the mapped allocation in
			// big scenes. Use a compacted draw-slot counter (skipped items consume nothing) clamped below
			// the gizmo tail; overflow items share the last slot (wrong constants beat heap corruption).
			const u32 maxPaneSlot = MAX_DRAW_RUNS - MAX_GIZMO_ITEMS - 1;
			u32 drawSlot = 0;

			for (size_t i = 0; i < objectCount; ++i)
			{
				const auto& item = m_render_queue[i];
				// #175: the first-person viewmodel layer is "only YOUR camera" — a secondary viewport
				// (camera pane / PIP) is by definition another camera, so never draw layer!=0 items here.
				// Without this the FP arms/weapon render interleaved as world geometry with the pane
				// camera's projection whenever they are in the queue (play mode, FP preview).
				if (item.layer != 0) continue;
				auto* mesh = reg.get_mesh(item.mesh_id);
				if (!mesh) continue;
				
				PerObjectConstants obj_cb{};
				obj_cb.world = item.world_matrix;
				obj_cb.base_color = { 0.8f, 0.8f, 0.8f, 1.0f };
				obj_cb.metallic = 0.0f; obj_cb.roughness = 0.5f; obj_cb.ao = 1.0f; obj_cb.normal_strength = 1.0f;
				obj_cb.uv_tiling = { 1.0f, 1.0f };   // default so a no-material object isn't left with a 0,0 tiling

				// Full PBR material + textures (matches render_3d_scene) so previews show the REAL material, not a
				// flat base color.
				auto* mat = reg.get_material(item.material_id);
				// A compiled custom per-material shader overrides the built-in PSO for THIS object (mirrors
				// render_3d_scene). Objects without one fall back to the default PBR PSO bound above.
				// Skinned items switch to the skinned PSO + bind their bone palette so PREVIEWS (thumbnails,
				// Keyframe Editor, Prefab Editor) show the posed character, not the bind pose.
				{
					ID3D12PipelineState* want_pso = default_pso;
					const bool skinned_item = item.bone_offset != NO_BONES && m_pipeline_3d.skinned_pso() && m_bone_vb;
					if (skinned_item)
					{
						want_pso = m_pipeline_3d.skinned_pso();
						m_command_list->SetGraphicsRootShaderResourceView(8,
							bone_palette_base_va() + (UINT64)item.bone_offset * 64);
					}
					else
					{
						const u32 bm = mat ? mat->blend_mode() : 0u;
						const bool blended = bm == 1 || bm == 2;
						const bool ds = mat && (mat->properties().is_unlit || mat->double_sided());
						const bool mirrored = item.mirrored != 0;
						ID3D12PipelineState* custom = custom_pso((u32)item.material_id, blended ? bm : 0u, ds, mirrored);
						if (custom) want_pso = custom;
						else if (blended)
						{
							// Transparent material (#33) in a preview/thumbnail: at least render it BLENDED so
							// the Material Editor sphere shows the see-through look (no sorting needed — the
							// preview draws a handful of items and the checker/sky behind them is opaque).
							auto* tpso = m_pipeline_3d.transparent_pso(bm, ds, mirrored);
							if (tpso) want_pso = tpso;
						}
						else if (ds) want_pso = m_pipeline_3d.double_sided_pso();
						else if (mirrored) want_pso = m_pipeline_3d.pipeline_state(true);   // #334
					}
					if (want_pso != cur_pso) { m_command_list->SetPipelineState(want_pso); cur_pso = want_pso; }
				}
				if (mat)
				{
					const auto& props = mat->properties();
					obj_cb.base_color = props.base_color;
					obj_cb.metallic = props.metallic; obj_cb.roughness = props.roughness;
					obj_cb.ao = props.ao; obj_cb.normal_strength = props.normal_strength;
					obj_cb.use_directx_normals = props.use_directx_normals;
					auto* tex = mat->albedo_texture();
					if (tex && tex->is_valid() && tex->srv_gpu().ptr != 0) { obj_cb.has_albedo_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(3, tex->srv_gpu()); }
					auto* normal = mat->normal_texture();
					if (normal && normal->is_valid() && normal->srv_gpu().ptr != 0) { obj_cb.has_normal_texture = normal->format() == TextureFormat::BC5_UNORM ? 2u : 1u; m_command_list->SetGraphicsRootDescriptorTable(4, normal->srv_gpu()); }
					auto* metallic_tex = mat->metallic_texture();
					if (metallic_tex && metallic_tex->is_valid() && metallic_tex->srv_gpu().ptr != 0) { obj_cb.has_metallic_texture = props.has_metallic_texture ? props.has_metallic_texture : 1; m_command_list->SetGraphicsRootDescriptorTable(5, metallic_tex->srv_gpu()); }
					auto* roughness_tex = mat->roughness_texture();
					if (roughness_tex && roughness_tex->is_valid() && roughness_tex->srv_gpu().ptr != 0) { obj_cb.has_roughness_texture = props.has_roughness_texture ? props.has_roughness_texture : 1; m_command_list->SetGraphicsRootDescriptorTable(6, roughness_tex->srv_gpu()); }
					auto* ao_tex = mat->ao_texture();
					if (ao_tex && ao_tex->is_valid() && ao_tex->srv_gpu().ptr != 0) { obj_cb.has_ao_texture = props.has_ao_texture ? props.has_ao_texture : 1; m_command_list->SetGraphicsRootDescriptorTable(7, ao_tex->srv_gpu()); }
					// UV tiling + height/parallax — so previews/thumbnails/game-window match the scene viewport.
					obj_cb.uv_tiling = props.uv_tiling;
					obj_cb.height_scale = props.height_scale;
					auto* height_tex = mat->height_texture();
					if (height_tex && height_tex->is_valid() && height_tex->srv_gpu().ptr != 0) { obj_cb.has_height_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(9, height_tex->srv_gpu()); }
				}
				
				const u32 slot = (drawSlot < maxPaneSlot) ? drawSlot : maxPaneSlot;
				if (drawSlot < maxPaneSlot) ++drawSlot;

				void* dest = static_cast<u8*>(m_per_object_cb_mapped) + (size_t)slot * 256;
				memcpy(dest, &obj_cb, sizeof(obj_cb));

				D3D12_GPU_VIRTUAL_ADDRESS obj_cb_addr = m_per_object_cb->GetGPUVirtualAddress() + (UINT64)slot * 256;
				m_command_list->SetGraphicsRootConstantBufferView(1, obj_cb_addr);

				// The shared 3D PSO now REQUIRES per-instance world data on slot 1 (instancing). The editor
				// preview draws one instance per object, so stage this world matrix + bind the instance VB.
				// (Same compacted slot — the instance VB is larger, but one indexing scheme is one bug fewer.)
				if (m_instance_vb_mapped)
					memcpy(static_cast<u8*>(m_instance_vb_mapped) + (size_t)slot * 64, &item.world_matrix, 64);
				D3D12_VERTEX_BUFFER_VIEW vbs2[2];
				vbs2[0] = mesh->vertex_buffer_view();
				vbs2[1].BufferLocation = m_instance_vb->GetGPUVirtualAddress() + (UINT64)slot * 64;
				vbs2[1].SizeInBytes = 64;
				vbs2[1].StrideInBytes = 64;
				m_command_list->IASetVertexBuffers(0, 2, vbs2);
				m_command_list->IASetIndexBuffer(&mesh->index_buffer_view());
				m_command_list->DrawIndexedInstanced(mesh->index_count(), 1, 0, 0, 0);
			}
		}

		// Editor gizmo pass INTO the offscreen target (e.g. the Collision Editor's green collider wireframe).
		// render_gizmos() reads m_active_rtv/m_active_dsv + the per-frame CB (which still holds THIS camera's VP
		// until it's restored below), so retarget the actives to this RT/DSV first. Must be recorded BEFORE Close().
		// The gizmo PSO is depth-disabled (always-on-top), so the wireframe draws over the mesh, exactly aligned.
		// Gate on BOTH queues: collider nets are WIRE-only items (SubmitGizmoWireItem), so a solid-queue-only
		// check silently skipped the pass and the Collision Editor preview never showed its wireframe.
		// Particles (#117): an offscreen target draws the world ParticleSetNextTargetWorld named for this render (one-shot;
		// thumbnails and previews stay particle-free unless they ask). World layer only — previews have no viewmodel.
		{
			const int fx_world = m_particles.consume_target_world();
			if (fx_world >= 0 && m_particles.ready())
			{
				XMFLOAT4X4 fx_vp; XMStoreFloat4x4(&fx_vp, vp_matrix);
				const DX12Particles::View pv = particle_view(fx_vp, fx_vp, camera.position, camera.target, camera.up, camera.fov_degrees, aspect,
					camera.near_clip, camera.far_clip, camera.orthographic, camera.ortho_size);
				if (m_particles.prepare(pv, (u32)fx_world) && m_particles.has_layer(0))
					m_particles.draw_layer(m_command_list.Get(), rtv, dsv, target->depth_resource(), target->width(), target->height(), 0, pv,
						particle_environment(frame_constants), m_light_cb ? m_light_cb->GetGPUVirtualAddress() : 0);
			}
		}
		if (render_gizmos && (!m_gizmo_render.empty() || !m_gizmo_wire_render.empty()))
		{
			m_active_rtv = rtv; m_active_dsv = dsv; m_active_color = target->resource();
			m_active_width = target->width(); m_active_height = target->height();
			this->render_gizmos();   // this-> so the bool param doesn't shadow the member function
		}

		// Execute commands
		m_command_list->Close();
		m_command_queue.execute_command_list(m_command_list.Get());
		m_command_queue.signal_and_wait();

		// Restore main camera constants
		if (m_per_frame_cb_mapped)
			memcpy(m_per_frame_cb_mapped, &m_frame_constants, sizeof(m_frame_constants));
	}
	

	bool DX12Renderer::prepare_render_target_readback(u32 target_id)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end()) return false;
		
		auto* target = it->second.get();
		
		// Ensure GPU is idle before readback
		m_command_queue.flush();
		
		u32 idx = m_swapchain.current_back_buffer_index();
		m_command_allocators[idx]->Reset();
		m_command_list->Reset(m_command_allocators[idx].Get(), nullptr);
		
		target->copy_to_staging(m_command_list.Get());
		
		m_command_list->Close();
		m_command_queue.execute_command_list(m_command_list.Get());
		m_command_queue.signal_and_wait();
		
		return true;
	}
	

	const void* DX12Renderer::read_render_target_pixels(u32 target_id, u32& out_width, u32& out_height, u32& out_row_pitch)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end())
		{
			out_width = out_height = out_row_pitch = 0;
			return nullptr;
		}
		
		auto* target = it->second.get();
		out_width = target->width();
		out_height = target->height();
		out_row_pitch = target->staging_row_pitch();
		
		return target->map_staging_buffer();
	}
	

	void DX12Renderer::release_render_target_pixels(u32 target_id)
	{
		auto it = m_render_targets.find(target_id);
		if (it != m_render_targets.end())
		{
			it->second->unmap_staging_buffer();
		}
	}

}
