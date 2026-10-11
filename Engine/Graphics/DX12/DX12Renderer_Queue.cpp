#include "DX12Renderer_Internal.h"

namespace vortex::graphics::dx12
{
	namespace
	{
		// A world matrix with a negative determinant (an odd number of negative scale axes) flips the triangle
		// winding; the run picks a counter-clockwise front-face PSO for it (#334).
		inline RenderItem with_winding(RenderItem item)
		{
			const DirectX::XMFLOAT4X4& m = item.world_matrix;
			const float det = m._11 * (m._22 * m._33 - m._23 * m._32) - m._12 * (m._21 * m._33 - m._23 * m._31) + m._13 * (m._21 * m._32 - m._22 * m._31);
			item.mirrored = det < 0.0f ? 1u : 0u;
			return item;
		}
	}

		bool DX12Renderer::create_game_window(HWND hwnd, u32 width, u32 height)
	{
		if (!m_initialized || !hwnd || width == 0 || height == 0) return false;
		if (m_game_window_active) destroy_game_window();

		auto& core = DX12Core::instance();
		SwapchainDesc sc_desc{};
		sc_desc.hwnd = hwnd;
		sc_desc.width = width;
		sc_desc.height = height;
		sc_desc.buffer_count = 2;
		if (!m_game_swapchain.initialize(core.factory(), m_command_queue.queue(), core.device(), sc_desc))
			return false;
		if (!m_game_depth.initialize(core.device(), width, height, DXGI_FORMAT_D32_FLOAT))
		{
			m_game_swapchain.shutdown();
			return false;
		}
		if (FAILED(core.device()->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,
				IID_PPV_ARGS(&m_game_cmd_allocator))))
		{
			m_game_depth.shutdown();
			m_game_swapchain.shutdown();
			return false;
		}
		m_game_window_active = true;
		return true;
	}


	void DX12Renderer::render_game_window()
	{
		if (!m_initialized || !m_game_window_active) return;

		// The command list + queue are shared with the editor frame; finish all prior GPU work before
		// reusing them. Simple full sync — perf is not the priority for the play window.
		m_command_queue.flush();
		upload_staged_bone_palettes();   // GPU idle after the flush — safe to write either buffer half

		// Target the game window's own swapchain + depth at its own size, using the CURRENT camera
		// (the caller sets the game's main camera before calling this).
		m_active_rtv = m_game_swapchain.current_rtv();
		m_active_color = m_game_swapchain.current_back_buffer();
		m_active_dsv = m_game_depth.dsv();
		m_active_width = m_game_swapchain.width();
		m_active_height = m_game_swapchain.height();

		update_per_frame_constants();

		m_game_cmd_allocator->Reset();
		auto* pso = m_render_queue.empty() ? m_pipeline.pipeline_state() :
			(m_wireframe_mode ? m_pipeline_3d.wireframe_pso() : m_pipeline_3d.pipeline_state());
		m_command_list->Reset(m_game_cmd_allocator.Get(), pso);

		// Post-FX (#28): this path has no upscale composite, so the 3D itself is redirected into the
		// chain's input RT (game-window slot 1); the chain's last pass then writes the back buffer.
		// Chain off (or RT creation failed) = the original direct render, untouched.
		DX12RenderTarget* pfx_in = m_postfx.active()
			? m_postfx.acquire_input(DX12Core::instance().device(), m_active_width, m_active_height, 1) : nullptr;
		if (pfx_in)
		{
			pfx_in->transition_to_render_target(m_command_list.Get());
			m_active_rtv = pfx_in->rtv();   // scene color -> chain input; depth stays the game window's
			m_active_color = pfx_in->resource();
		}

		// Spot-light shadow pass (#23) — THIS is the shipped game's render path (the flashlight!).
		// Recorded first, exactly like render_frame; the window viewport is (re)set below anyway.
		render_shadow_pass();

		// SSAO (#32) for the play window (view slot 1) — before the color pass, like render_frame.
		record_ssao(1, m_active_width, m_active_height);

		D3D12_RESOURCE_BARRIER barrier{};
		barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
		barrier.Transition.pResource = m_game_swapchain.current_back_buffer();
		barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
		barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
		barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
		m_command_list->ResourceBarrier(1, &barrier);

		D3D12_VIEWPORT vp{}; vp.Width = (float)m_active_width; vp.Height = (float)m_active_height; vp.MaxDepth = 1.0f;
		D3D12_RECT sc{}; sc.right = (LONG)m_active_width; sc.bottom = (LONG)m_active_height;
		m_command_list->RSSetViewports(1, &vp);
		m_command_list->RSSetScissorRects(1, &sc);
		m_command_list->ClearRenderTargetView(m_active_rtv, m_clear_color, 0, nullptr);
		m_command_list->ClearDepthStencilView(m_active_dsv, D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0, 0, nullptr);
		m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		m_command_list->OMSetRenderTargets(1, &m_active_rtv, FALSE, &m_active_dsv);

		if (m_skybox_enabled) render_skybox();
		if (m_grid_visible) render_grid();
		if (!m_render_queue.empty()) render_3d_scene();
		render_gizmos();   // script Debug.Draw shapes (#42) — wire gizmos show in the play window too

		if (pfx_in)
			m_postfx.record(m_command_list.Get(), 1, m_game_swapchain.current_rtv(),
				m_active_width, m_active_height, elapsed_seconds());

		barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
		barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
		m_command_list->ResourceBarrier(1, &barrier);
		m_command_list->Close();

		m_command_queue.execute_command_list(m_command_list.Get());
		m_command_queue.flush(); // wait before present (safe)

		// 2D UI overlay (Direct2D over the same back buffer) — drawn after the 3D, before present.
		m_ui_overlay.render(m_game_swapchain.current_back_buffer());

		m_game_swapchain.present(false);
	}


	void DX12Renderer::resize_game_window(u32 width, u32 height)
	{
		if (!m_game_window_active || width == 0 || height == 0) return;
		m_command_queue.flush();
		m_ui_overlay.invalidate_targets();   // drop cached bitmaps aliasing the old game back buffers
		m_game_swapchain.resize(width, height);
		m_game_depth.resize(DX12Core::instance().device(), width, height);
	}


	void DX12Renderer::destroy_game_window()
	{
		if (!m_game_window_active) return;
		m_command_queue.flush();
		m_ui_overlay.invalidate_targets();   // wrapped game back buffers are about to be freed
		m_game_window_active = false;
		m_game_depth.shutdown();
		m_game_swapchain.shutdown();
		m_game_cmd_allocator.Reset();
	}

	// --- Frustum culling helpers (don't render what the camera can't see) ---

	void DX12Renderer::submit_render_item(const RenderItem& item)
	{
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		(m_submit_target == 1 ? m_static_submit : m_submit_queue).push_back(with_winding(item));
	}


	void DX12Renderer::submit_gizmo_item(const RenderItem& item)
	{
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		if (m_gizmo_submit.size() < MAX_GIZMO_ITEMS) m_gizmo_submit.push_back(with_winding(item));
	}


	void DX12Renderer::submit_gizmo_wire_item(const RenderItem& item)
	{
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		// Solid + wire share the tail CB/VB slot range — cap their SUM, not each list.
		if (m_gizmo_submit.size() + m_gizmo_wire_submit.size() < MAX_GIZMO_ITEMS)
			m_gizmo_wire_submit.push_back(with_winding(item));
	}


	void DX12Renderer::submit_mesh_instances(id::id_type mesh, id::id_type material, const float* world_matrices, u32 count, u32 layer, const float* colors)
	{
		if (!world_matrices || count == 0) return;
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		auto& q = m_submit_target == 1 ? m_static_submit : m_submit_queue;
		q.reserve(q.size() + count);
		for (u32 i = 0; i < count; ++i)
		{
			RenderItem item;
			item.mesh_id = mesh;
			item.material_id = material;
			item.layer = layer;
			memcpy(&item.world_matrix, world_matrices + (size_t)i * 16, sizeof(DirectX::XMFLOAT4X4));
			if (colors) item.color = { colors[(size_t)i * 4], colors[(size_t)i * 4 + 1], colors[(size_t)i * 4 + 2], colors[(size_t)i * 4 + 3] };
			q.push_back(with_winding(item));
		}
	}
	

	void DX12Renderer::submit_skinned_item(id::id_type mesh, id::id_type material, const float* world_matrix,
		const float* bone_matrices, u32 bone_count, u32 layer, const float* color)
	{
		if (!world_matrix || !bone_matrices || bone_count == 0) return;
		std::lock_guard<std::mutex> lock(m_queue_mutex);

		RenderItem item;
		item.mesh_id = mesh;
		item.material_id = material;
		item.layer = layer;
		if (color) item.color = { color[0], color[1], color[2], color[3] };
		memcpy(&item.world_matrix, world_matrix, sizeof(DirectX::XMFLOAT4X4));

		// Stage the palette; the offset is in MATRICES (root SRV binds at the active half's VA + offset * 64).
		u32 offset = (u32)(m_bone_submit.size() / 16);
		if (offset + bone_count > MAX_BONE_MATRICES_PER_FRAME)
		{
			// Palette half full — submit as rigid (bind pose) instead of overrunning.
			m_submit_queue.push_back(with_winding(item));
			return;
		}
		item.bone_offset = offset;
		item.bone_count = bone_count;
		m_bone_submit.insert(m_bone_submit.end(), bone_matrices, bone_matrices + (size_t)bone_count * 16);
		m_submit_queue.push_back(with_winding(item));
	}


	void DX12Renderer::clear_render_queue()
	{
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		m_submit_queue.clear();
		m_bone_submit.clear();
		m_static_submit.clear(); m_static_render.clear(); m_static_pending = false; m_submit_target = 0;
	}

	// #364 A: everything submitted between these two calls is RETAINED across frames (the static scene); an empty pass
	// clears the retained set. Per-frame submits outside the pass are the dynamic entities, merged in at the swap.
	void DX12Renderer::begin_static_scene()
	{
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		m_static_submit.clear();
		m_submit_target = 1;
	}

	void DX12Renderer::end_static_scene()
	{
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		m_submit_target = 0;
		m_static_pending = true;
	}


	void DX12Renderer::set_camera(const DirectX::XMFLOAT3& pos, const DirectX::XMFLOAT3& target, const DirectX::XMFLOAT3& up)
	{
		m_camera_position = pos; m_camera_target = target; m_camera_up = up;
	}


	void DX12Renderer::set_projection(float fov_degrees, float aspect, float near_clip, float far_clip)
	{
		m_fov_degrees = fov_degrees;
		m_aspect_ratio = aspect;
		m_near_clip = near_clip;
		m_far_clip = far_clip;
	}


	void DX12Renderer::render_camera_gizmo(
		const DirectX::XMFLOAT3& position,
		const DirectX::XMFLOAT3& forward,
		const DirectX::XMFLOAT3& right,
		const DirectX::XMFLOAT3& up,
		float near_width, float near_height,
		float far_width, float far_height,
		float near_dist, float far_dist,
		const DirectX::XMFLOAT4& color)
	{
		// This renders a wireframe camera frustum gizmo
		// For now, we'll add the gizmo as lines to a line buffer that gets rendered
		// This is a placeholder - full implementation would use a line rendering system
		
		using namespace DirectX;
		
		// Calculate frustum corners in world space
		XMVECTOR pos = XMLoadFloat3(&position);
		XMVECTOR fwd = XMLoadFloat3(&forward);
		XMVECTOR rgt = XMLoadFloat3(&right);
		XMVECTOR upv = XMLoadFloat3(&up);
		
		// Near plane corners
		XMVECTOR near_center = pos + fwd * near_dist;
		XMVECTOR near_tl = near_center - rgt * near_width + upv * near_height;
		XMVECTOR near_tr = near_center + rgt * near_width + upv * near_height;
		XMVECTOR near_bl = near_center - rgt * near_width - upv * near_height;
		XMVECTOR near_br = near_center + rgt * near_width - upv * near_height;
		
		// Far plane corners
		XMVECTOR far_center = pos + fwd * far_dist;
		XMVECTOR far_tl = far_center - rgt * far_width + upv * far_height;
		XMVECTOR far_tr = far_center + rgt * far_width + upv * far_height;
		XMVECTOR far_bl = far_center - rgt * far_width - upv * far_height;
		XMVECTOR far_br = far_center + rgt * far_width - upv * far_height;
		
		// TODO: Add these lines to a line buffer for rendering
		// For now, this is a stub that would be connected to a line rendering system
		// The lines would be:
		// - Near plane rectangle (4 lines)
		// - Far plane rectangle (4 lines)
		// - Connecting lines from camera to far corners (4 lines)
		// - Camera body (small box at position)
	}


}
