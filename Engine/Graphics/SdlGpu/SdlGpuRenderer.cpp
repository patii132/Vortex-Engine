#include "SdlGpuRenderer.h"
#include "SdlGpuShaderFormat.h"
#include "../../Common/Platform.h"
#include "../../Common/VerboseLog.h"
#if VORTEX_PLATFORM_APPLE
#include <objc/message.h>
#include <objc/runtime.h>
#endif
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>

namespace vortex::graphics::sdlgpu
{
	namespace
	{
		void log(const std::string& s) { platform::debug_output(("[sdlgpu] " + s + "\n").c_str()); }

		bool read_text_file(const std::string& path, std::string& out)
		{
			std::ifstream f(path, std::ios::binary);
			if (!f) return false;
			out.assign((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
			return true;
		}

		// Shader code exactly as SDL_CreateGPUShader wants it: MSL is a NUL-terminated string, SPIR-V a blob.
		bool read_shader_file(const std::string& path, std::vector<unsigned char>& out)
		{
			std::ifstream f(path, std::ios::binary);
			if (!f) return false;
			out.assign((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
			if (out.empty()) return false;
			if (shaderfmt::is_text) out.push_back('\0');
			return true;
		}

		// The shaders directory: next to the executable (shipped), the CMake build tree, or the source tree.
		// The leaf folder and the file that proves it is the right one depend on the backend's shader format
		// (Shaders/msl/standard.metal on Metal, Shaders/spirv/standard.PSMain.spv on Vulkan).
		std::string find_shader_dir(const std::string& override_dir)
		{
			const std::string env = platform::env_string("VORTEX_SHADER_DIR");
			const std::string leaf = std::string("Shaders/") + shaderfmt::directory;
			std::vector<std::string> candidates;
			if (!override_dir.empty()) candidates.push_back(override_dir);
			if (!env.empty()) candidates.push_back(env);
			const std::string exe = platform::executable_directory();
			candidates.push_back(exe + leaf);
			candidates.push_back(exe + "../" + leaf);
			candidates.push_back(exe + "../Resources/" + leaf);   // inside a .app bundle
			candidates.push_back(exe + "../../Engine/" + leaf);
			candidates.push_back(exe + "../../../Engine/" + leaf);
			candidates.push_back("Engine/" + leaf);
			for (const std::string& c : candidates)
			{
				std::error_code ec;
				if (std::filesystem::is_regular_file(std::filesystem::path(c) / shaderfmt::probe, ec))
					return std::filesystem::absolute(c, ec).string();
			}
			return {};
		}
	}

	SdlGpuRenderer& SdlGpuRenderer::instance()
	{
		static SdlGpuRenderer inst;
		return inst;
	}

	// ---------------------------------------------------------------------------------------------
	// Initialization
	// ---------------------------------------------------------------------------------------------
	bool SdlGpuRenderer::initialize(const RendererDesc& desc)
	{
		if (m_initialized) return true;
		if (!desc.native_window) { log("initialize: no native window"); return false; }

		if (!SDL_WasInit(SDL_INIT_VIDEO))
		{
#if VORTEX_PLATFORM_LINUX
			// Embedding into a toolkit window means wrapping its X11 window, which only the x11 video driver
			// can do — on a Wayland session SDL would otherwise pick its wayland driver and ignore the handle.
			// A standalone window (the player) keeps SDL's own choice, so it runs natively on Wayland.
			if (desc.native_window && !desc.native_is_sdl_window && !platform::env_string("SDL_VIDEO_DRIVER").size())
				SDL_SetHint(SDL_HINT_VIDEO_DRIVER, "x11");
#endif
			if (!SDL_Init(SDL_INIT_VIDEO)) { log(std::string("SDL_Init failed: ") + SDL_GetError()); return false; }
			m_sdl_video_inited_here = true;
		}

		m_width = desc.width; m_height = desc.height;
		if (!create_device()) return false;

		m_main.window = create_or_wrap_window(desc.native_window, desc.native_is_sdl_window, desc.width, desc.height);
		m_main.owns_window = !desc.native_is_sdl_window;
		if (!m_main.window || !claim_window(m_main.window)) { shutdown(); return false; }
		m_present_format = SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM;   // present targets are ours; the swapchain blit converts

		m_shader_dir = find_shader_dir(m_shader_dir_override);
		if (m_shader_dir.empty())
		{
			log(std::string("shader directory not found (Engine/Shaders/") + shaderfmt::directory + ")");
			shutdown(); return false;
		}
		log("shaders: " + m_shader_dir);
		if (!load_shaders() || !create_pipelines() || !create_dynamic_buffers()) { shutdown(); return false; }

		SDL_GPUSamplerCreateInfo sci{};
		sci.min_filter = sci.mag_filter = SDL_GPU_FILTER_LINEAR;
		sci.mipmap_mode = SDL_GPU_SAMPLERMIPMAPMODE_LINEAR;
		sci.address_mode_u = sci.address_mode_v = sci.address_mode_w = SDL_GPU_SAMPLERADDRESSMODE_REPEAT;
		sci.enable_anisotropy = true; sci.max_anisotropy = 16.0f;
		sci.max_lod = 1000.0f;
		m_sampler_linear_wrap = SDL_CreateGPUSampler(m_device, &sci);
		sci.address_mode_u = sci.address_mode_v = sci.address_mode_w = SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE;
		sci.enable_anisotropy = false; sci.max_anisotropy = 1.0f;
		m_sampler_linear_clamp = SDL_CreateGPUSampler(m_device, &sci);
		if (!m_sampler_linear_wrap || !m_sampler_linear_clamp) { log("sampler creation failed"); shutdown(); return false; }
		// The shadow slots are comparison-sampled depth textures in every scene pipeline, so their sampler and
		// a stand-in depth texture must exist before the first frame, with or without shadow atlases.
		if (!create_shadow_fallback()) { shutdown(); return false; }

		ResourceRegistry::instance().initialize(m_device);
		if (!m_overlay.initialize(m_device, m_shader_dir, m_present_format))
			log("UI overlay unavailable (continuing without it)");
		if (!m_particles.initialize(m_device, m_shader_dir, m_scene_format, m_sampler_linear_wrap, m_sampler_linear_clamp))
			log("particles unavailable (continuing without them)");
		if (!m_decals.initialize(m_device, m_shader_dir, m_scene_format, m_sampler_linear_clamp))
			log("decals unavailable (continuing without them)");
		if (!m_volumetrics.initialize(m_device, m_shader_dir, m_scene_format, m_sampler_linear_clamp))
			log("volumetric fog unavailable (continuing without it)");

		m_initialized = true;
		log("initialized (" + m_gpu_name + ")");
		return true;
	}

	bool SdlGpuRenderer::create_device()
	{
		const bool debug = platform::env_flag("VORTEX_GPU_DEBUG");
		m_device = SDL_CreateGPUDevice(shaderfmt::format, debug, nullptr);
		if (!m_device) { log(std::string("SDL_CreateGPUDevice failed: ") + SDL_GetError()); return false; }
		const char* driver = SDL_GetGPUDeviceDriver(m_device);
		m_gpu_name = driver ? driver : "gpu";
#ifdef SDL_PROP_GPU_DEVICE_NAME_STRING
		const SDL_PropertiesID props = SDL_GetGPUDeviceProperties(m_device);
		if (props)
		{
			const char* name = SDL_GetStringProperty(props, SDL_PROP_GPU_DEVICE_NAME_STRING, nullptr);
			if (name && *name) m_gpu_name = std::string(name) + " (" + m_gpu_name + ")";
		}
#endif
		return true;
	}

#if VORTEX_PLATFORM_APPLE
	// Minimal Objective-C runtime bridge for the editor-embedded viewport (this is a .cpp file; the few AppKit
	// calls below are made through objc_msgSend so the engine core stays free of Objective-C++).
	namespace objc
	{
		using id_t = void*;
		struct Rect { double x, y, w, h; };
		inline SEL sel(const char* s) { return sel_registerName(s); }
		inline id_t msg_id(id_t o, const char* s) { return reinterpret_cast<id_t (*)(id_t, SEL)>(objc_msgSend)(o, sel(s)); }
		inline void msg_void(id_t o, const char* s) { reinterpret_cast<void (*)(id_t, SEL)>(objc_msgSend)(o, sel(s)); }
		inline void msg_void_id(id_t o, const char* s, id_t a) { reinterpret_cast<void (*)(id_t, SEL, id_t)>(objc_msgSend)(o, sel(s), a); }
		inline void msg_void_ulong(id_t o, const char* s, unsigned long a) { reinterpret_cast<void (*)(id_t, SEL, unsigned long)>(objc_msgSend)(o, sel(s), a); }
		inline unsigned long msg_ulong(id_t o, const char* s) { return reinterpret_cast<unsigned long (*)(id_t, SEL)>(objc_msgSend)(o, sel(s)); }
		inline id_t msg_id_ulong(id_t o, const char* s, unsigned long a) { return reinterpret_cast<id_t (*)(id_t, SEL, unsigned long)>(objc_msgSend)(o, sel(s), a); }
		inline bool is_kind_of(id_t o, Class c) { return reinterpret_cast<BOOL (*)(id_t, SEL, Class)>(objc_msgSend)(o, sel("isKindOfClass:"), c) != NO; }
		inline Rect msg_rect(id_t o, const char* s)
		{
#if defined(__x86_64__)
			return reinterpret_cast<Rect (*)(id_t, SEL)>(objc_msgSend_stret)(o, sel(s));   // 32-byte struct: hidden return pointer
#else
			return reinterpret_cast<Rect (*)(id_t, SEL)>(objc_msgSend)(o, sel(s));         // arm64: homogeneous float aggregate in v0-v3
#endif
		}
		inline void msg_set_rect(id_t o, const char* s, Rect r) { reinterpret_cast<void (*)(id_t, SEL, Rect)>(objc_msgSend)(o, sel(s), r); }

		// SDL's Metal view class (SDL 3.4 renamed it with the SDL3_ prefix).
		inline Class metal_view_class()
		{
			Class c = objc_getClass("SDL3_cocoametalview");
			return c ? c : objc_getClass("SDL_cocoametalview");
		}

		// The toolkit's host NSView is a plain NSView, which refuses the first click on a non-key window (AppKit
		// only activates the window then). A 3D viewport should act on that click like any canvas does, so the
		// host view is re-classed at runtime into a subclass that accepts first-mouse events.
		BOOL host_accepts_first_mouse(id_t, SEL, id_t) { return YES; }
		void make_host_accept_first_mouse(id_t host)
		{
			Class sub = objc_getClass("VortexViewportHostView");
			if (!sub)
			{
				Class base = object_getClass(reinterpret_cast<::id>(host));
				sub = objc_allocateClassPair(base, "VortexViewportHostView", 0);
				if (!sub) return;
				class_addMethod(sub, sel("acceptsFirstMouse:"), reinterpret_cast<IMP>(host_accepts_first_mouse), "B@:@");
				objc_registerClassPair(sub);
			}
			object_setClass(reinterpret_cast<::id>(host), sub);
		}
	}
#endif

	SDL_Window* SdlGpuRenderer::create_or_wrap_window(void* native, bool is_sdl_window, u32 w, u32 h)
	{
		if (is_sdl_window) return static_cast<SDL_Window*>(native);
		// Editor-embedded viewport: wrap the host's NSView so SDL attaches its Metal layer to it.
		SDL_PropertiesID props = SDL_CreateProperties();
#if VORTEX_PLATFORM_LINUX
		// Editor-embedded viewport: wrap the toolkit's X11 window so SDL puts its Vulkan surface on it.
		// Avalonia's NativeControlHost hands out an X11 Window id (XID) on its X11 backend; SDL must therefore
		// be on its x11 video driver, which initialize() pins before SDL_Init for exactly this case.
		m_host_view = native;
		SDL_SetNumberProperty(props, SDL_PROP_WINDOW_CREATE_X11_WINDOW_NUMBER, (Sint64)(uintptr_t)native);
#elif VORTEX_PLATFORM_APPLE
		// UI toolkits (Avalonia, WPF-like shells) keep their NSWindow non-opaque so they can offer window
		// transparency. SDL mirrors [NSWindow isOpaque] into SDL_WINDOW_TRANSPARENT and the GPU API refuses to
		// claim a transparent window - the editor viewport is always opaque, so mark the host window opaque first.
		{
			objc::id_t ns_window = objc::msg_id(native, "window");
			if (!ns_window) { log("viewport: the host view is not inside a window yet"); SDL_DestroyProperties(props); return nullptr; }
			reinterpret_cast<void (*)(objc::id_t, SEL, BOOL)>(objc_msgSend)(ns_window, objc::sel("setOpaque:"), YES);
			// SDL is handed the NSWindow, not the host view: given a view, SDL 3.x installs it as the window's
			// content view ([NSWindow setContentView:]), which throws the toolkit's own view - the whole editor UI -
			// out of the window. With the window, SDL keeps the existing content view and only adds its Metal layer
			// to it; claim_window() then moves that layer into the host view (the viewport panel) and restores the
			// responder chain SDL rewires to its own event listener.
			m_host_view = native;
			m_host_metal_view = nullptr;
			m_host_content_view = objc::msg_id(ns_window, "contentView");
			m_host_content_prev_responder = m_host_content_view ? objc::msg_id(m_host_content_view, "nextResponder") : nullptr;
			m_host_window_prev_responder = objc::msg_id(ns_window, "nextResponder");
			SDL_SetPointerProperty(props, SDL_PROP_WINDOW_CREATE_COCOA_WINDOW_POINTER, ns_window);
		}
#else
		(void)native;
#endif
		SDL_SetBooleanProperty(props, SDL_PROP_WINDOW_CREATE_HIGH_PIXEL_DENSITY_BOOLEAN, true);
		SDL_SetNumberProperty(props, SDL_PROP_WINDOW_CREATE_WIDTH_NUMBER, (Sint64)(w ? w : 1));
		SDL_SetNumberProperty(props, SDL_PROP_WINDOW_CREATE_HEIGHT_NUMBER, (Sint64)(h ? h : 1));
		SDL_Window* window = SDL_CreateWindowWithProperties(props);
		SDL_DestroyProperties(props);
		if (!window) log(std::string("SDL_CreateWindowWithProperties failed: ") + SDL_GetError());
		return window;
	}

	bool SdlGpuRenderer::claim_window(SDL_Window* window)
	{
		if (!SDL_ClaimWindowForGPUDevice(m_device, window))
		{
			log(std::string("SDL_ClaimWindowForGPUDevice failed: ") + SDL_GetError());
			return false;
		}
		apply_swapchain_params(window);
		if (window == m_main.window && m_host_view)
		{
			attach_metal_view_to_host();
			release_host_input(window);
		}
		return true;
	}

	// Editor-embedded viewport only, X11. SDL selects the full input mask on the window it wraps
	// (ButtonPress, KeyPress, PointerMotion, ...). ButtonPress in particular can be selected by only ONE
	// client per window, so from that moment the button and key events over the viewport belong to SDL's
	// display connection — and the editor never pumps SDL's event loop, so they are simply dropped: clicking
	// in the viewport did nothing, and right-drag never started the fly camera.
	//
	// The toolkit owns input on this window, so SDL's connection gives the input masks back and keeps only
	// what it needs to track the surface. X11 then delivers those events to the next window up that has them
	// selected — the toolkit's own window. This is the X11 twin of restoring the responder chain on macOS
	// (see attach_metal_view_to_host).
	//
	// Xlib is loaded on demand: the engine links only SDL, and a build on a machine without X11 must keep
	// working (a standalone player on Wayland never comes through here).
	void SdlGpuRenderer::release_host_input(SDL_Window* window)
	{
#if VORTEX_PLATFORM_LINUX
		const SDL_PropertiesID props = SDL_GetWindowProperties(window);
		if (!props) return;
		void* display = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_X11_DISPLAY_POINTER, nullptr);
		const Sint64 xwindow = SDL_GetNumberProperty(props, SDL_PROP_WINDOW_X11_WINDOW_NUMBER, 0);
		if (!display || !xwindow) return;   // not the X11 backend (e.g. a native Wayland window)

		// Resolved once and kept: this is re-applied whenever the surface is re-sized, because SDL re-selects
		// its own mask on some window operations and the toolkit must not lose the input again.
		if (!m_x11_select_input)
		{
			SDL_SharedObject* xlib = SDL_LoadObject("libX11.so.6");
			if (!xlib) xlib = SDL_LoadObject("libX11.so");
			if (!xlib) { log("viewport: libX11 not loadable — input stays with SDL"); return; }
			m_x11_select_input = SDL_LoadFunction(xlib, "XSelectInput");
			m_x11_flush = SDL_LoadFunction(xlib, "XFlush");
			if (!m_x11_select_input || !m_x11_flush)
			{
				log("viewport: XSelectInput unavailable — input stays with SDL");
				SDL_UnloadObject(xlib);
				m_x11_select_input = m_x11_flush = nullptr;
				return;
			}
			m_x11_lib = xlib;

			// libXi is optional: without it only the core mask is handed back, which is enough for a toolkit
			// that still uses core pointer events.
			SDL_SharedObject* xi = SDL_LoadObject("libXi.so.6");
			if (!xi) xi = SDL_LoadObject("libXi.so");
			if (xi)
			{
				m_xi2_select_events = SDL_LoadFunction(xi, "XISelectEvents");
				if (m_xi2_select_events) m_xi2_lib = xi;
				else SDL_UnloadObject(xi);
			}
			log(std::string("viewport: input returned to the host toolkit (core mask")
				+ (m_xi2_select_events ? " + XInput2)" : " only — libXi missing)"));
		}

		using select_input_fn = int (*)(void*, unsigned long, long);
		using flush_fn = int (*)(void*);
		// Keep only what SDL needs to notice the surface changing; hand back every core input mask.
		constexpr long ExposureMask = 1L << 15;
		constexpr long StructureNotifyMask = 1L << 17;
		reinterpret_cast<select_input_fn>(m_x11_select_input)(display, (unsigned long)xwindow,
			ExposureMask | StructureNotifyMask);

		// The core mask is only half of it. Modern toolkits — Avalonia among them — take pointer input through
		// XInput2, and SDL does too, so the pointer events live in a SEPARATE selection that XSelectInput does
		// not touch: with only the core mask handed back, the toolkit still saw nothing over the viewport.
		// Clearing SDL's XI2 selection on its surface lets the server walk up to the toolkit's own window.
		if (m_xi2_select_events)
		{
			struct XIEventMask { int deviceid; int mask_len; unsigned char* mask; };
			using xi_select_fn = int (*)(void*, unsigned long, XIEventMask*, int);
			unsigned char empty[4] = { 0, 0, 0, 0 };
			constexpr int XIAllDevices = 0, XIAllMasterDevices = 1;
			XIEventMask clear[2] = {
				{ XIAllDevices,       (int)sizeof(empty), empty },
				{ XIAllMasterDevices, (int)sizeof(empty), empty },
			};
			reinterpret_cast<xi_select_fn>(m_xi2_select_events)(display, (unsigned long)xwindow, clear, 2);
		}
		reinterpret_cast<flush_fn>(m_x11_flush)(display);
#else
		(void)window;
#endif
	}

	// Editor-embedded viewport only. SDL (3.x) always parents the Metal layer it creates for a wrapped view to the
	// NSWindow's *content view*, sized to the whole window - in the editor that covers every panel with the 3D
	// view. Move the layer into the host view the editor gave us (the viewport panel's own NSView) and keep it
	// sized to that view; the swapchain follows the layer's drawable size. SDL also makes itself the next
	// responder of the host view and the window to read mouse events; the editor toolkit owns input on this
	// window (SDL never pumps it), so the original responder chain is restored - clicks, drags and wheel events
	// over the viewport reach the toolkit again.
	void SdlGpuRenderer::attach_metal_view_to_host()
	{
#if VORTEX_PLATFORM_APPLE
		if (!m_host_view) return;
		objc::id_t host = m_host_view;
		objc::id_t window = objc::msg_id(host, "window");
		objc::id_t content = window ? objc::msg_id(window, "contentView") : nullptr;
		objc::id_t metal = nullptr;
		if (Class cls = objc::metal_view_class(); cls && content)
		{
			objc::id_t subviews = objc::msg_id(content, "subviews");
			const unsigned long n = subviews ? objc::msg_ulong(subviews, "count") : 0;
			for (unsigned long i = 0; i < n && !metal; ++i)
			{
				objc::id_t v = objc::msg_id_ulong(subviews, "objectAtIndex:", i);
				if (v && objc::is_kind_of(v, cls)) metal = v;
			}
		}
		if (metal)
		{
			const objc::Rect b = objc::msg_rect(host, "bounds");
			objc::msg_void(metal, "removeFromSuperview");
			objc::msg_void_ulong(metal, "setAutoresizingMask:", 0);   // sized explicitly by sync_host_surface()
			objc::msg_set_rect(metal, "setFrame:", objc::Rect{ 0.0, 0.0, b.w, b.h });
			objc::msg_void_id(host, "addSubview:", metal);
			objc::msg_void(metal, "updateDrawableSize");
			objc::make_host_accept_first_mouse(host);
			m_host_metal_view = metal;
			log("viewport: Metal layer attached to the host view (" + std::to_string((int)b.w) + "x" + std::to_string((int)b.h) + " pt)");
		}
		else log("viewport: SDL Metal view not found under the window's content view - the 3D view may cover the whole window");
		// SDL made its event listener the next responder of the content view and of the window; the toolkit owns
		// input on this window (SDL never pumps it), so put the original chain back.
		if (m_host_content_view) objc::msg_void_id(m_host_content_view, "setNextResponder:", m_host_content_prev_responder);
		if (window) objc::msg_void_id(window, "setNextResponder:", m_host_window_prev_responder);
#endif
	}

	// Keep the Metal layer exactly the size of the host view (the viewport panel). Called every frame and on
	// resize: the toolkit re-frames the host view during layout, and SDL only refreshes the drawable size on
	// whole-window size events, so a panel-only resize (splitter drag) would otherwise stretch the old drawable.
	void SdlGpuRenderer::sync_host_surface()
	{
#if VORTEX_PLATFORM_LINUX
		// The toolkit resizes the embedded X11 window itself, but SDL only learns a window's new size from the
		// event loop — which the editor never pumps (it owns input on this window). Tell SDL the size the
		// editor just reported, so the next acquired swapchain texture matches the panel.
		if (m_host_view && m_main.window && m_width && m_height)
		{
			int w = 0, h = 0;
			SDL_GetWindowSize(m_main.window, &w, &h);
			if ((u32)w != m_width || (u32)h != m_height)
			{
				SDL_SetWindowSize(m_main.window, (int)m_width, (int)m_height);
				release_host_input(m_main.window);   // SDL re-selects its mask on some window operations
			}
		}
#elif VORTEX_PLATFORM_APPLE
		if (!m_host_view || !m_host_metal_view) return;
		const objc::Rect b = objc::msg_rect(m_host_view, "bounds");
		const objc::Rect f = objc::msg_rect(m_host_metal_view, "frame");
		if (f.x != 0.0 || f.y != 0.0 || f.w != b.w || f.h != b.h)
		{
			objc::msg_set_rect(m_host_metal_view, "setFrame:", objc::Rect{ 0.0, 0.0, b.w, b.h });
			objc::msg_void(m_host_metal_view, "updateDrawableSize");
		}
#endif
	}

	void SdlGpuRenderer::apply_swapchain_params(SDL_Window* window)
	{
		SDL_GPUPresentMode mode = SDL_GPU_PRESENTMODE_VSYNC;
		if (!m_vsync_enabled)
		{
			if (SDL_WindowSupportsGPUPresentMode(m_device, window, SDL_GPU_PRESENTMODE_IMMEDIATE)) mode = SDL_GPU_PRESENTMODE_IMMEDIATE;
			else if (SDL_WindowSupportsGPUPresentMode(m_device, window, SDL_GPU_PRESENTMODE_MAILBOX)) mode = SDL_GPU_PRESENTMODE_MAILBOX;
		}
		SDL_SetGPUSwapchainParameters(m_device, window, SDL_GPU_SWAPCHAINCOMPOSITION_SDR, mode);
	}

	const std::vector<unsigned char>* SdlGpuRenderer::shader_blob(const std::string& file)
	{
		auto it = m_shader_blobs.find(file);
		if (it != m_shader_blobs.end()) return &it->second;
		std::vector<unsigned char> code;
		if (!read_shader_file(m_shader_dir + "/" + file, code)) return nullptr;
		return &m_shader_blobs.emplace(file, std::move(code)).first->second;
	}

	SDL_GPUShader* SdlGpuRenderer::create_shader_from_code(const std::vector<unsigned char>& code, const char* entry,
		const std::string& origin, SDL_GPUShaderStage stage, u32 samplers, u32 storage_buffers, u32 uniform_buffers)
	{
		SDL_GPUShaderCreateInfo ci{};
		ci.code = code.data();
		ci.code_size = code.size();
		ci.entrypoint = shaderfmt::entrypoint(entry);
		ci.format = shaderfmt::format;
		ci.stage = stage;
		ci.num_samplers = samplers;
		ci.num_storage_buffers = storage_buffers;
		ci.num_uniform_buffers = uniform_buffers;
		SDL_GPUShader* sh = SDL_CreateGPUShader(m_device, &ci);
		if (!sh) log(std::string("shader '") + entry + "' (" + origin + ") failed: " + SDL_GetError());
		return sh;
	}

	SDL_GPUShader* SdlGpuRenderer::create_shader(const std::string& base, const char* entry, SDL_GPUShaderStage stage,
		u32 samplers, u32 storage_buffers, u32 uniform_buffers)
	{
		const std::string file = shaderfmt::module_file(base, entry);
		const std::vector<unsigned char>* code = shader_blob(file);
		if (!code) { log("missing shader " + file); return nullptr; }
		return create_shader_from_code(*code, entry, file, stage, samplers, storage_buffers, uniform_buffers);
	}

	bool SdlGpuRenderer::load_shaders()
	{
		m_vs_standard = create_shader("standard", "VSMain", SDL_GPU_SHADERSTAGE_VERTEX, 0, 0, 1);
		m_vs_skinned = create_shader("standard", "VSSkinned", SDL_GPU_SHADERSTAGE_VERTEX, 0, 1, 2);
		m_fs_standard = create_shader("standard", "PSMain", SDL_GPU_SHADERSTAGE_FRAGMENT, 13, 0, 3);
		m_vs_grid = create_shader("grid", "GridVS", SDL_GPU_SHADERSTAGE_VERTEX, 0, 0, 1);
		m_fs_grid = create_shader("grid", "GridPS", SDL_GPU_SHADERSTAGE_FRAGMENT, 0, 0, 1);
		m_vs_sky = create_shader("skybox", "SkyVS", SDL_GPU_SHADERSTAGE_VERTEX, 0, 0, 1);
		m_fs_sky = create_shader("skybox", "SkyPS", SDL_GPU_SHADERSTAGE_FRAGMENT, 1, 0, 1);   // sampler 0: the equirect sky (#326)
		m_vs_blit = create_shader("postfx", "BlitVS", SDL_GPU_SHADERSTAGE_VERTEX, 0, 0, 0);
		m_fs_blit = create_shader("postfx", "BlitPS", SDL_GPU_SHADERSTAGE_FRAGMENT, 1, 0, 0);
		m_fs_postfx = create_shader("postfx", "PostFxPS", SDL_GPU_SHADERSTAGE_FRAGMENT, 2, 0, 1);
		return m_vs_standard && m_fs_standard && m_vs_grid && m_fs_grid && m_vs_sky && m_fs_sky && m_vs_blit && m_fs_blit && m_fs_postfx;
	}

	SDL_GPUGraphicsPipeline* SdlGpuRenderer::create_scene_pipeline(SDL_GPUShader* vs, SDL_GPUShader* fs, u32 stride, bool skinned_layout,
		SDL_GPUFillMode fill, SDL_GPUCullMode cull, bool depth_test, bool depth_write, SDL_GPUCompareOp depth_op, u32 blend_mode,
		bool front_ccw)
	{
		SDL_GPUVertexBufferDescription buffers[2] = {
			{ 0, stride, SDL_GPU_VERTEXINPUTRATE_VERTEX, 0 },
			{ 1, 64, SDL_GPU_VERTEXINPUTRATE_INSTANCE, 0 },
		};
		std::vector<SDL_GPUVertexAttribute> attrs = {
			{ 0, 0, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3, 0 },
			{ 1, 0, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3, 12 },
			{ 2, 0, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, 24 },
		};
		u32 loc = 3;
		if (skinned_layout)
		{
			attrs.push_back({ loc++, 0, SDL_GPU_VERTEXELEMENTFORMAT_UBYTE4, 32 });
			attrs.push_back({ loc++, 0, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4, 36 });
		}
		for (u32 r = 0; r < 4; ++r) attrs.push_back({ loc++, 1, SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4, r * 16 });

		SDL_GPUColorTargetDescription color{};
		color.format = m_scene_format;
		if (blend_mode == 1 || blend_mode == 2)
		{
			color.blend_state.enable_blend = true;
			color.blend_state.src_color_blendfactor = SDL_GPU_BLENDFACTOR_SRC_ALPHA;
			color.blend_state.dst_color_blendfactor = blend_mode == 1 ? SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA : SDL_GPU_BLENDFACTOR_ONE;
			color.blend_state.color_blend_op = SDL_GPU_BLENDOP_ADD;
			color.blend_state.src_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
			color.blend_state.dst_alpha_blendfactor = blend_mode == 1 ? SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA : SDL_GPU_BLENDFACTOR_ONE;
			color.blend_state.alpha_blend_op = SDL_GPU_BLENDOP_ADD;
		}

		SDL_GPUGraphicsPipelineCreateInfo pci{};
		pci.vertex_shader = vs;
		pci.fragment_shader = fs;
		pci.vertex_input_state.vertex_buffer_descriptions = buffers;
		pci.vertex_input_state.num_vertex_buffers = 2;
		pci.vertex_input_state.vertex_attributes = attrs.data();
		pci.vertex_input_state.num_vertex_attributes = (Uint32)attrs.size();
		pci.primitive_type = SDL_GPU_PRIMITIVETYPE_TRIANGLELIST;
		pci.rasterizer_state.fill_mode = fill;
		pci.rasterizer_state.cull_mode = cull;
		// D3D convention (the mesh generators wind clockwise); mirrored instances get the counter-clockwise twin (#334)
		pci.rasterizer_state.front_face = front_ccw ? SDL_GPU_FRONTFACE_COUNTER_CLOCKWISE : SDL_GPU_FRONTFACE_CLOCKWISE;
		pci.rasterizer_state.enable_depth_clip = true;
		pci.multisample_state.sample_count = SDL_GPU_SAMPLECOUNT_1;
		pci.depth_stencil_state.enable_depth_test = depth_test;
		pci.depth_stencil_state.enable_depth_write = depth_write;
		pci.depth_stencil_state.compare_op = depth_op;
		pci.target_info.color_target_descriptions = &color;
		pci.target_info.num_color_targets = 1;
		pci.target_info.depth_stencil_format = m_depth_format;
		pci.target_info.has_depth_stencil_target = true;
		SDL_GPUGraphicsPipeline* p = SDL_CreateGPUGraphicsPipeline(m_device, &pci);
		if (!p) log(std::string("scene pipeline failed: ") + SDL_GetError());
		return p;
	}

	SDL_GPUGraphicsPipeline* SdlGpuRenderer::create_fullscreen_pipeline(SDL_GPUShader* vs, SDL_GPUShader* fs, SDL_GPUTextureFormat color_format,
		bool depth, bool blend)
	{
		SDL_GPUColorTargetDescription color{};
		color.format = color_format;
		if (blend)
		{
			color.blend_state.enable_blend = true;
			color.blend_state.src_color_blendfactor = SDL_GPU_BLENDFACTOR_SRC_ALPHA;
			color.blend_state.dst_color_blendfactor = SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA;
			color.blend_state.color_blend_op = SDL_GPU_BLENDOP_ADD;
			color.blend_state.src_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
			color.blend_state.dst_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ZERO;
			color.blend_state.alpha_blend_op = SDL_GPU_BLENDOP_ADD;
		}
		SDL_GPUGraphicsPipelineCreateInfo pci{};
		pci.vertex_shader = vs;
		pci.fragment_shader = fs;
		pci.primitive_type = SDL_GPU_PRIMITIVETYPE_TRIANGLELIST;
		pci.rasterizer_state.fill_mode = SDL_GPU_FILLMODE_FILL;
		pci.rasterizer_state.cull_mode = SDL_GPU_CULLMODE_NONE;
		pci.rasterizer_state.front_face = SDL_GPU_FRONTFACE_CLOCKWISE;
		pci.rasterizer_state.enable_depth_clip = !depth ? true : false;   // grid/skybox reach the far plane
		pci.multisample_state.sample_count = SDL_GPU_SAMPLECOUNT_1;
		pci.depth_stencil_state.enable_depth_test = depth;
		pci.depth_stencil_state.enable_depth_write = false;
		pci.depth_stencil_state.compare_op = SDL_GPU_COMPAREOP_LESS_OR_EQUAL;
		pci.target_info.color_target_descriptions = &color;
		pci.target_info.num_color_targets = 1;
		pci.target_info.depth_stencil_format = m_depth_format;
		pci.target_info.has_depth_stencil_target = depth;
		SDL_GPUGraphicsPipeline* p = SDL_CreateGPUGraphicsPipeline(m_device, &pci);
		if (!p) log(std::string("fullscreen pipeline failed: ") + SDL_GetError());
		return p;
	}

	bool SdlGpuRenderer::create_pipeline_set(PipelineSet& set, u32 stride, bool skinned_layout)
	{
		(void)skinned_layout;   // the rigid shading pipelines read pos/normal/uv only; the stride skips the skin data
		set.opaque = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_BACK, true, true, SDL_GPU_COMPAREOP_LESS, 0);
		set.wireframe = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_LINE, SDL_GPU_CULLMODE_NONE, true, true, SDL_GPU_COMPAREOP_LESS, 0);
		set.double_sided = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_NONE, true, true, SDL_GPU_COMPAREOP_LESS, 0);
		set.gizmo = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_NONE, false, false, SDL_GPU_COMPAREOP_ALWAYS, 0);
		set.gizmo_wire = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_LINE, SDL_GPU_CULLMODE_NONE, true, false, SDL_GPU_COMPAREOP_LESS_OR_EQUAL, 0);
		set.alpha = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_BACK, true, false, SDL_GPU_COMPAREOP_LESS_OR_EQUAL, 1);
		set.alpha_ds = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_NONE, true, false, SDL_GPU_COMPAREOP_LESS_OR_EQUAL, 1);
		set.additive = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_BACK, true, false, SDL_GPU_COMPAREOP_LESS_OR_EQUAL, 2);
		set.additive_ds = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_NONE, true, false, SDL_GPU_COMPAREOP_LESS_OR_EQUAL, 2);
		// mirrored twins (#334): same state, counter-clockwise front faces
		set.opaque_m = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_BACK, true, true, SDL_GPU_COMPAREOP_LESS, 0, true);
		set.alpha_m = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_BACK, true, false, SDL_GPU_COMPAREOP_LESS_OR_EQUAL, 1, true);
		set.additive_m = create_scene_pipeline(m_vs_standard, m_fs_standard, stride, false, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_BACK, true, false, SDL_GPU_COMPAREOP_LESS_OR_EQUAL, 2, true);
		return set.opaque && set.wireframe && set.double_sided && set.gizmo && set.gizmo_wire && set.alpha && set.alpha_ds && set.additive && set.additive_ds
			&& set.opaque_m && set.alpha_m && set.additive_m;
	}

	bool SdlGpuRenderer::create_pipelines()
	{
		if (!create_pipeline_set(m_pipelines, 32, false)) return false;
		if (!create_pipeline_set(m_pipelines_skinned_stride, 52, false)) return false;
		m_pipeline_skinned = create_scene_pipeline(m_vs_skinned, m_fs_standard, 52, true, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_BACK, true, true, SDL_GPU_COMPAREOP_LESS, 0);
		m_pipeline_skinned_m = create_scene_pipeline(m_vs_skinned, m_fs_standard, 52, true, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_BACK, true, true, SDL_GPU_COMPAREOP_LESS, 0, true);
		m_pipeline_grid = create_fullscreen_pipeline(m_vs_grid, m_fs_grid, m_scene_format, true, true);
		m_pipeline_skybox = create_fullscreen_pipeline(m_vs_sky, m_fs_sky, m_scene_format, true, false);
		if (!ensure_shadow_resources()) log("shadow maps unavailable on this device - rendering without shadows");
		if (!ensure_postfx_resources()) log("SSAO/bloom unavailable on this device");
		m_pipeline_blit = create_fullscreen_pipeline(m_vs_blit, m_fs_blit, m_present_format, false, false);
		m_pipeline_postfx = create_fullscreen_pipeline(m_vs_blit, m_fs_postfx, m_present_format, false, false);
		if (!m_pipeline_skinned) log("skinned pipeline unavailable — skinned meshes render in bind pose");
		return m_pipeline_grid && m_pipeline_skybox && m_pipeline_blit && m_pipeline_postfx;
	}

	bool SdlGpuRenderer::create_dynamic_buffers()
	{
		SDL_GPUBufferCreateInfo bci{};
		bci.usage = SDL_GPU_BUFFERUSAGE_VERTEX;
		bci.size = MAX_RENDER_OBJECTS * 64;
		m_instance_buffer = SDL_CreateGPUBuffer(m_device, &bci);
		SDL_GPUTransferBufferCreateInfo tci{};
		tci.usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD;
		tci.size = MAX_RENDER_OBJECTS * 64;
		m_instance_transfer = SDL_CreateGPUTransferBuffer(m_device, &tci);

		bci.usage = SDL_GPU_BUFFERUSAGE_GRAPHICS_STORAGE_READ;
		bci.size = MAX_BONE_MATRICES * 64;
		m_bone_buffer = SDL_CreateGPUBuffer(m_device, &bci);
		tci.size = MAX_BONE_MATRICES * 64;
		m_bone_transfer = SDL_CreateGPUTransferBuffer(m_device, &tci);
		if (!m_instance_buffer || !m_instance_transfer || !m_bone_buffer || !m_bone_transfer)
		{
			log(std::string("dynamic buffer creation failed: ") + SDL_GetError());
			return false;
		}
		m_instance_staging.reserve(4096 * 16);
		return true;
	}

	bool SdlGpuRenderer::ensure_target(GpuTarget& target, u32 w, u32 h, bool with_depth, bool readback)
	{
		if (w < 1) w = 1;
		if (h < 1) h = 1;
		if (target.color && target.width == w && target.height == h && (!with_depth || target.depth) && (!readback || target.readback))
			return true;
		wait_idle();
		release_target(target);

		SDL_GPUTextureCreateInfo tci{};
		tci.type = SDL_GPU_TEXTURETYPE_2D;
		tci.format = m_scene_format;
		tci.usage = SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER;
		tci.width = w; tci.height = h;
		tci.layer_count_or_depth = 1;
		tci.num_levels = 1;
		tci.sample_count = SDL_GPU_SAMPLECOUNT_1;
		target.color = SDL_CreateGPUTexture(m_device, &tci);
		if (with_depth)
		{
			tci.format = m_depth_format;
			// sampled by the particle passes (depth test + soft particles) and the collision snapshot
			tci.usage = SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER;
			target.depth = SDL_CreateGPUTexture(m_device, &tci);
		}
		if (readback)
		{
			SDL_GPUTransferBufferCreateInfo rci{};
			rci.usage = SDL_GPU_TRANSFERBUFFERUSAGE_DOWNLOAD;
			rci.size = w * h * 4;
			target.readback = SDL_CreateGPUTransferBuffer(m_device, &rci);
			target.readback_size = w * h * 4;
		}
		target.width = w; target.height = h;
		if (!target.color || (with_depth && !target.depth) || (readback && !target.readback))
		{
			log(std::string("render target creation failed: ") + SDL_GetError());
			release_target(target);
			return false;
		}
		return true;
	}

	void SdlGpuRenderer::release_target(GpuTarget& target)
	{
		if (m_device)
		{
			if (target.readback_mapped) { SDL_UnmapGPUTransferBuffer(m_device, target.readback); target.readback_mapped = nullptr; }
			if (target.color) SDL_ReleaseGPUTexture(m_device, target.color);
			if (target.depth) SDL_ReleaseGPUTexture(m_device, target.depth);
			if (target.readback) SDL_ReleaseGPUTransferBuffer(m_device, target.readback);
		}
		target = GpuTarget{};
	}

	void SdlGpuRenderer::release_pipeline_set(PipelineSet& set)
	{
		SDL_GPUGraphicsPipeline** all[] = { &set.opaque, &set.wireframe, &set.double_sided, &set.gizmo, &set.gizmo_wire,
			&set.alpha, &set.alpha_ds, &set.additive, &set.additive_ds, &set.opaque_m, &set.alpha_m, &set.additive_m };
		for (auto** p : all) { if (*p) SDL_ReleaseGPUGraphicsPipeline(m_device, *p); *p = nullptr; }
	}

	void SdlGpuRenderer::wait_idle()
	{
		if (m_device) SDL_WaitForGPUIdle(m_device);
	}

	void SdlGpuRenderer::shutdown()
	{
		if (!m_device) return;
		wait_idle();
		destroy_postfx_resources();
		destroy_shadow_resources();
		m_overlay.shutdown();
		m_particles.shutdown();
		m_decals.shutdown();
		m_volumetrics.shutdown();
		::vortex::particles::on_renderer_shutdown();   // the engine texture ids the emitters hold die with the registry
		destroy_game_window();
		for (auto& [id, target] : m_render_targets) release_target(*target);
		m_render_targets.clear();
		ResourceRegistry::instance().shutdown();
		{
			std::lock_guard<std::mutex> lock(m_queue_mutex);
			m_render_queue.clear(); m_submit_queue.clear();
		m_static_submit.clear(); m_static_render.clear(); m_static_pending = false; m_submit_target = 0;
			m_gizmo_render.clear(); m_gizmo_submit.clear(); m_gizmo_wire_render.clear(); m_gizmo_wire_submit.clear();
		}
		m_custom_shaders.clear();
		for (auto& [path, cp] : m_pipeline_cache)
			for (auto* p : cp.pipeline) if (p) SDL_ReleaseGPUGraphicsPipeline(m_device, p);
		m_pipeline_cache.clear();
		release_pipeline_set(m_pipelines);
		release_pipeline_set(m_pipelines_skinned_stride);
		SDL_GPUGraphicsPipeline** singles[] = { &m_pipeline_skinned, &m_pipeline_skinned_m, &m_pipeline_grid, &m_pipeline_skybox, &m_pipeline_blit, &m_pipeline_postfx };
		for (auto** p : singles) { if (*p) SDL_ReleaseGPUGraphicsPipeline(m_device, *p); *p = nullptr; }
		SDL_GPUShader** shaders[] = { &m_vs_standard, &m_vs_skinned, &m_fs_standard, &m_vs_grid, &m_fs_grid, &m_vs_sky, &m_fs_sky, &m_vs_blit, &m_fs_blit, &m_fs_postfx };
		for (auto** s : shaders) { if (*s) SDL_ReleaseGPUShader(m_device, *s); *s = nullptr; }
		if (m_sampler_linear_wrap) SDL_ReleaseGPUSampler(m_device, m_sampler_linear_wrap);
		if (m_sampler_linear_clamp) SDL_ReleaseGPUSampler(m_device, m_sampler_linear_clamp);
		m_sampler_linear_wrap = m_sampler_linear_clamp = nullptr;
		if (m_instance_buffer) SDL_ReleaseGPUBuffer(m_device, m_instance_buffer);
		if (m_instance_transfer) SDL_ReleaseGPUTransferBuffer(m_device, m_instance_transfer);
		if (m_bone_buffer) SDL_ReleaseGPUBuffer(m_device, m_bone_buffer);
		if (m_bone_transfer) SDL_ReleaseGPUTransferBuffer(m_device, m_bone_transfer);
		m_instance_buffer = nullptr; m_instance_transfer = nullptr; m_bone_buffer = nullptr; m_bone_transfer = nullptr;
		release_target(m_main.scene); release_target(m_main.present); release_target(m_main.postfx);
		if (m_main.window)
		{
			SDL_ReleaseWindowFromGPUDevice(m_device, m_main.window);   // removes the Metal view from the host view
			if (m_main.owns_window) SDL_DestroyWindow(m_main.window);
			m_main.window = nullptr;
		}
		m_host_view = nullptr; m_host_metal_view = nullptr; m_host_content_view = nullptr;
		m_host_content_prev_responder = nullptr; m_host_window_prev_responder = nullptr;
		SDL_DestroyGPUDevice(m_device);
		m_device = nullptr;
		if (m_xi2_lib) { SDL_UnloadObject(m_xi2_lib); m_xi2_lib = nullptr; }
		if (m_x11_lib) { SDL_UnloadObject(m_x11_lib); m_x11_lib = nullptr; }
		m_x11_select_input = m_x11_flush = m_xi2_select_events = nullptr;
		if (m_shadow_dummy) { SDL_ReleaseGPUTexture(m_device, m_shadow_dummy); m_shadow_dummy = nullptr; }
		if (m_scene_color_copy) { SDL_ReleaseGPUTexture(m_device, m_scene_color_copy); m_scene_color_copy = nullptr; }
		if (m_scene_depth_copy) { SDL_ReleaseGPUTexture(m_device, m_scene_depth_copy); m_scene_depth_copy = nullptr; }
		m_scene_copy_valid = false;
		if (m_sampler_shadow) { SDL_ReleaseGPUSampler(m_device, m_sampler_shadow); m_sampler_shadow = nullptr; }
		m_shader_blobs.clear();
		m_initialized = false;
		if (m_sdl_video_inited_here) { SDL_QuitSubSystem(SDL_INIT_VIDEO); m_sdl_video_inited_here = false; }
	}

	void SdlGpuRenderer::resize(u32 w, u32 h)
	{
		if (!m_initialized || w == 0 || h == 0) return;
		m_width = w; m_height = h;   // the swapchain follows the host view / window; targets are re-sized on the next frame
		sync_host_surface();
	}

	// ---------------------------------------------------------------------------------------------
	// Frames
	// ---------------------------------------------------------------------------------------------
	void SdlGpuRenderer::render_frame()
	{
		if (!m_initialized) return;
		// Particles: hand finished collision readbacks to the simulation, then let the frame driver (the managed
		// ParticleService callback, or the automatic world-0 update) step it before anything is gathered.
		m_particles.begin_frame();
		::vortex::particles::begin_frame();
		swap_render_queue();

		m_frame_count++;
		auto now = std::chrono::steady_clock::now();
		auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(now - m_last_fps_time).count();
		if (elapsed >= 500)
		{
			m_current_fps = static_cast<int>((m_frame_count * 1000) / elapsed);
			m_frame_count = 0;
			m_last_fps_time = now;
		}
		m_draw_call_count = 0; m_vertex_count = 0; m_instances_tested = 0; m_instances_drawn = 0;
		m_shadow_draw_count = 0; m_post_draw_count = 0;

		if (m_vsync_dirty) { m_vsync_dirty = false; apply_swapchain_params(m_main.window); if (m_game.window) apply_swapchain_params(m_game.window); }
		sync_host_surface();
		render_surface(m_main, 0);
	}

	void SdlGpuRenderer::render_surface(WindowSurface& surface, int slot)
	{
		SDL_GPUCommandBuffer* cmd = SDL_AcquireGPUCommandBuffer(m_device);
		if (!cmd) { log(std::string("AcquireGPUCommandBuffer failed: ") + SDL_GetError()); return; }
		SDL_GPUTexture* swapchain = nullptr;
		u32 w = 0, h = 0;
		if (!SDL_WaitAndAcquireGPUSwapchainTexture(cmd, surface.window, &swapchain, &w, &h))
		{
			log(std::string("swapchain acquire failed: ") + SDL_GetError());
			SDL_SubmitGPUCommandBuffer(cmd);
			return;
		}
		if (!swapchain || w == 0 || h == 0) { SDL_SubmitGPUCommandBuffer(cmd); return; }   // minimized / occluded
		if (w != surface.last_w || h != surface.last_h)
		{
			surface.last_w = w; surface.last_h = h;
			log(std::string(slot == 0 ? "main" : "game") + " surface: swapchain " + std::to_string(w) + "x" + std::to_string(h) + " px");
		}

		const bool post_on = m_postfx.active() && (slot == 1 || m_postfx.main_view_enabled());
		u32 rw = (u32)((float)w * m_render_scale + 0.5f), rh = (u32)((float)h * m_render_scale + 0.5f);
		if (rw < 1) rw = 1; if (rh < 1) rh = 1;
		if (!ensure_target(surface.scene, rw, rh, true, false) || !ensure_target(surface.present, w, h, false, true))
		{
			SDL_SubmitGPUCommandBuffer(cmd);
			return;
		}

		FrameView view = build_main_view(rw, rh);
		if (slot == 0) m_frame_constants = view.frame;
		record_scene(cmd, surface.scene, view, m_skybox_enabled, m_grid_visible, true, 0, true);

		// Composite the scene into the present target (render-scale upscale), through the post chain if active.
		if (post_on && ensure_target(surface.postfx, w, h, false, false))
		{
			fullscreen_blit(cmd, surface.scene.color, surface.postfx.color, w, h, m_pipeline_blit, nullptr);
			PostFxCB cb{};
			const auto& p = m_postfx.params();
			cb.texel[0] = 1.0f / (float)w; cb.texel[1] = 1.0f / (float)h;
			cb.time = elapsed_seconds();
			cb.flags = (p.vignette ? 1u : 0u) | (p.grain ? 2u : 0u) | (p.ca ? 4u : 0u) | (p.debug_invert ? 8u : 0u) | (p.grade ? 16u : 0u);
			cb.vignette[0] = p.vig_intensity; cb.vignette[1] = p.vig_smoothness; cb.vignette[2] = p.vig_roundness;
			cb.vignette_color[0] = p.vig_r; cb.vignette_color[1] = p.vig_g; cb.vignette_color[2] = p.vig_b;
			cb.grain_ca[0] = p.grain_intensity; cb.grain_ca[1] = p.grain_size; cb.grain_ca[2] = p.ca_strength; cb.grain_ca[3] = p.ca_falloff;
			cb.grade1[0] = p.exposure; cb.grade1[1] = p.contrast; cb.grade1[2] = p.saturation; cb.grade1[3] = p.temperature;
			cb.grade2[0] = p.tint;
			cb.bloom[0] = p.bloom_intensity;
			if (p.bloom && m_post_ready)
			{
				record_bloom(cmd, surface.scene.color, rw, rh);
				if (m_bloom_result) cb.flags |= 32u;
			}
			fullscreen_blit(cmd, surface.postfx.color, surface.present.color, w, h, m_pipeline_postfx, &cb, m_bloom_result);
		}
		else
		{
			fullscreen_blit(cmd, surface.scene.color, surface.present.color, w, h, m_pipeline_blit, nullptr);
		}

		// 2D overlay on top of the composite, then hand the present target to the swapchain.
		m_overlay.render(cmd, surface.present.color, w, h);
		composite_and_present(cmd, surface, slot, swapchain, w, h);
	}

	void SdlGpuRenderer::fullscreen_blit(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* src, SDL_GPUTexture* dst, u32 w, u32 h,
		SDL_GPUGraphicsPipeline* pipeline, const PostFxCB* postfx, SDL_GPUTexture* second)
	{
		SDL_GPUColorTargetInfo color{};
		color.texture = dst;
		color.load_op = SDL_GPU_LOADOP_DONT_CARE;
		color.store_op = SDL_GPU_STOREOP_STORE;
		SDL_GPURenderPass* pass = SDL_BeginGPURenderPass(cmd, &color, 1, nullptr);
		if (!pass) return;
		SDL_BindGPUGraphicsPipeline(pass, pipeline);
		SDL_GPUViewport vp{ 0, 0, (float)w, (float)h, 0.0f, 1.0f };
		SDL_SetGPUViewport(pass, &vp);
		SDL_GPUTextureSamplerBinding bindings[2] = { { src, m_sampler_linear_clamp }, { second ? second : src, m_sampler_linear_clamp } };
		SDL_BindGPUFragmentSamplers(pass, 0, bindings, postfx ? 2 : 1);
		if (postfx) SDL_PushGPUFragmentUniformData(cmd, 0, postfx, sizeof(PostFxCB));
		SDL_DrawGPUPrimitives(pass, 3, 1, 0, 0);
		SDL_EndGPURenderPass(pass);
	}

	void SdlGpuRenderer::composite_and_present(SDL_GPUCommandBuffer* cmd, WindowSurface& surface, int slot, SDL_GPUTexture* swapchain, u32 w, u32 h)
	{
		SDL_GPUBlitInfo blit{};
		blit.source.texture = surface.present.color;
		blit.source.w = w; blit.source.h = h;
		blit.destination.texture = swapchain;
		blit.destination.w = w; blit.destination.h = h;
		blit.load_op = SDL_GPU_LOADOP_DONT_CARE;
		blit.filter = SDL_GPU_FILTER_NEAREST;
		SDL_BlitGPUTexture(cmd, &blit);

		if (slot == 0 && m_capture_requested)
		{
			m_capture_requested = false;
			// Download the finished present target (3D + overlay) as part of this submission, then write it out.
			SDL_GPUCopyPass* copy = SDL_BeginGPUCopyPass(cmd);
			SDL_GPUTextureRegion region{}; region.texture = surface.present.color; region.w = w; region.h = h; region.d = 1;
			SDL_GPUTextureTransferInfo dst{ surface.present.readback, 0, w, h };
			SDL_DownloadFromGPUTexture(copy, &region, &dst);
			SDL_EndGPUCopyPass(copy);
			SDL_GPUFence* fence = SDL_SubmitGPUCommandBufferAndAcquireFence(cmd);
			if (fence)
			{
				SDL_WaitForGPUFences(m_device, true, &fence, 1);
				SDL_ReleaseGPUFence(m_device, fence);
				capture_target_to_bmp(surface.present, m_capture_path.c_str());
			}
			return;
		}
		SDL_SubmitGPUCommandBuffer(cmd);
	}

	bool SdlGpuRenderer::capture_target_to_bmp(GpuTarget& target, const char* path)
	{
		if (!target.readback || !path) return false;
		void* mapped = SDL_MapGPUTransferBuffer(m_device, target.readback, false);
		if (!mapped) return false;
		const u32 width = target.width, height = target.height;
		FILE* f = nullptr;
		bool ok = false;
		if (fopen_s(&f, path, "wb") == 0 && f)
		{
			const u32 img_size = width * height * 4;
			const u32 file_size = 54 + img_size;
			unsigned char fh[14] = { 0 };
			fh[0] = 'B'; fh[1] = 'M';
			memcpy(&fh[2], &file_size, 4);
			const u32 offset = 54; memcpy(&fh[10], &offset, 4);
			fwrite(fh, 1, 14, f);
			unsigned char ih[40] = { 0 };
			const u32 hdr = 40; memcpy(&ih[0], &hdr, 4);
			const int w = (int)width, hneg = -(int)height;
			memcpy(&ih[4], &w, 4); memcpy(&ih[8], &hneg, 4);
			const unsigned short planes = 1, bpp = 32;
			memcpy(&ih[12], &planes, 2); memcpy(&ih[14], &bpp, 2);
			memcpy(&ih[20], &img_size, 4);
			fwrite(ih, 1, 40, f);
			// present targets are BGRA8: rows go out as-is (BMP is BGRA top-down with a negative height)
			fwrite(mapped, 1, img_size, f);
			fclose(f);
			ok = true;
		}
		SDL_UnmapGPUTransferBuffer(m_device, target.readback);
		return ok;
	}

	void SdlGpuRenderer::request_capture(const char* path)
	{
		if (!path) return;
		m_capture_path = path;
		m_capture_requested = true;
	}

	void SdlGpuRenderer::on_scene_switch()
	{
		if (!m_initialized) return;
		wait_idle();
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		m_render_queue.clear(); m_submit_queue.clear();
		m_static_submit.clear(); m_static_render.clear(); m_static_pending = false; m_submit_target = 0;
		m_bone_submit.clear(); m_bone_render.clear();
		m_bone_upload_pending = false;
		m_queue_dirty = true;
	}

	// ---------------------------------------------------------------------------------------------
	// Standalone game window
	// ---------------------------------------------------------------------------------------------
	bool SdlGpuRenderer::create_game_window(void* native_window, u32 width, u32 height)
	{
		if (!m_initialized || width == 0 || height == 0) return false;
		if (m_game.window) destroy_game_window();
		if (native_window)
		{
			m_game.window = create_or_wrap_window(native_window, false, width, height);
			m_game.owns_window = true;
		}
		else
		{
			m_game.window = SDL_CreateWindow("Vortex — Game", (int)width, (int)height, SDL_WINDOW_RESIZABLE | SDL_WINDOW_HIGH_PIXEL_DENSITY);
			m_game.owns_window = true;
		}
		if (!m_game.window || !claim_window(m_game.window))
		{
			if (m_game.window) { SDL_DestroyWindow(m_game.window); m_game.window = nullptr; }
			return false;
		}
		return true;
	}

	void SdlGpuRenderer::render_game_window()
	{
		if (!m_initialized || !m_game.window) return;
		render_surface(m_game, 1);
	}

	void SdlGpuRenderer::resize_game_window(u32 width, u32 height)
	{
		if (!m_game.window || width == 0 || height == 0) return;
		if (m_game.owns_window) SDL_SetWindowSize(m_game.window, (int)width, (int)height);
	}

	void SdlGpuRenderer::destroy_game_window()
	{
		if (!m_game.window) return;
		wait_idle();
		release_target(m_game.scene); release_target(m_game.present); release_target(m_game.postfx);
		SDL_ReleaseWindowFromGPUDevice(m_device, m_game.window);
		if (m_game.owns_window) SDL_DestroyWindow(m_game.window);
		m_game.window = nullptr;
	}

	// ---------------------------------------------------------------------------------------------
	// Custom material shaders — VSMain/PSMain with the standard bindings.
	//   Metal: a .metal file, compiled from source by the driver at load time.
	//   Vulkan: a .glsl file, compiled to SPIR-V by glslc at load time (the twin of the DX12 backend's
	//           run-time DXC compile, so "edit the shader, alt-tab back, it is in" still holds). One file
	//           serves both stages: it is compiled twice, with VORTEX_VERTEX_STAGE / VORTEX_FRAGMENT_STAGE
	//           defined, and `main` guarded on them. Shaders/glsl-include sits next to the compiled shaders,
	//           so a material shader can #include "standard_common.glsl" and reuse the engine's blocks.
	// ---------------------------------------------------------------------------------------------
	bool SdlGpuRenderer::load_material_shader(const std::string& path, SDL_GPUShaderStage stage,
		std::vector<unsigned char>& out)
	{
		const char* ext = shaderfmt::material_suffix;
		const size_t n = std::strlen(ext);
		if (path.size() <= n || path.compare(path.size() - n, n, ext) != 0) return false;

		if (!shaderfmt::per_entrypoint)                       // Metal: hand the source to the driver
			return read_shader_file(path, out);

		// Vulkan: compile with glslc. The paths come from the project's own material assets; a quote in one
		// would break the shell quoting below, so those are refused rather than escaped.
		if (path.find('\'') != std::string::npos) { log("material shader path contains a quote: " + path); return false; }
		const bool vertex = (stage == SDL_GPU_SHADERSTAGE_VERTEX);
		std::string glslc = platform::env_string("VORTEX_GLSLC");
		if (glslc.empty()) glslc = "glslc";

		std::error_code ec;
		const std::filesystem::path spv =
			std::filesystem::temp_directory_path(ec) /
			("vortex_" + std::to_string(file_mtime(path)) + (vertex ? ".vert.spv" : ".frag.spv"));
		if (ec) { log("no temp directory for shader compilation"); return false; }

		const std::string include_dir = m_shader_dir + "/../glsl-include";
		const std::string cmd = "'" + glslc + "' --target-env=vulkan1.0"
			+ (vertex ? " -fshader-stage=vert -DVORTEX_VERTEX_STAGE" : " -fshader-stage=frag -DVORTEX_FRAGMENT_STAGE")
			+ " -I '" + include_dir + "' -o '" + spv.string() + "' '" + path + "'";
		if (std::system(cmd.c_str()) != 0)
		{
			log("glslc failed for '" + path + "' (set VORTEX_GLSLC if it is not on PATH)");
			std::filesystem::remove(spv, ec);
			return false;
		}
		const bool ok = read_shader_file(spv.string(), out);
		std::filesystem::remove(spv, ec);
		if (!ok) log("could not read the compiled shader for '" + path + "'");
		return ok;
	}

	unsigned long long SdlGpuRenderer::file_mtime(const std::string& path) const
	{
		std::error_code ec;
		auto t = std::filesystem::last_write_time(path, ec);
		if (ec) return 0ull;
		return (unsigned long long)t.time_since_epoch().count();
	}

	namespace
	{
		// blend (0 opaque / 1 alpha / 2 additive) × double-sided × mirrored -> 0..11
		u32 pipeline_variant(u32 blend_mode, bool double_sided, bool mirrored, bool skinned)
		{
			const u32 b = blend_mode == 1 ? 1u : (blend_mode == 2 ? 2u : 0u);
			return b + (double_sided ? 3u : 0u) + (mirrored ? 6u : 0u) + (skinned ? 12u : 0u);
		}
	}

	SDL_GPUGraphicsPipeline* SdlGpuRenderer::get_or_compile_pipeline(const std::string& path)
	{
		return get_or_compile_pipeline(path, 0, false, false, false);
	}

	SDL_GPUGraphicsPipeline* SdlGpuRenderer::get_or_compile_pipeline(const std::string& path, u32 blend_mode, bool double_sided, bool mirrored, bool skinned)
	{
		if (path.empty()) return nullptr;
		if (skinned && !m_vs_skinned) return nullptr;
		const u32 variant = pipeline_variant(blend_mode, double_sided, mirrored, skinned);
		const unsigned long long mt = file_mtime(path);
		auto it = m_pipeline_cache.find(path);
		if (it != m_pipeline_cache.end() && it->second.pipeline[variant] && it->second.mtime[variant] == mt) return it->second.pipeline[variant];

		// A custom shader keeps the material's blend mode (#333): the alpha / additive variants blend with depth write
		// off like the built-in transparent pipelines; a mirrored instance gets counter-clockwise front faces (#334).
		SDL_GPUGraphicsPipeline* pipeline = nullptr;
		std::vector<unsigned char> vs_code, fs_code;
		// A skinned mesh (#332) runs the ENGINE's skinning vertex shader in front of the material's PSMain: the
		// template's VSOut is the standard one, so the fragment shader reads the same interpolants either way.
		if ((skinned || load_material_shader(path, SDL_GPU_SHADERSTAGE_VERTEX, vs_code)) &&
			load_material_shader(path, SDL_GPU_SHADERSTAGE_FRAGMENT, fs_code))
		{
			// two vertex uniform buffers: PerFrame (0) and PerObject (1) — custom vertex shaders may read the material (#125)
			SDL_GPUShader* vs = skinned ? m_vs_skinned : create_shader_from_code(vs_code, "VSMain", path, SDL_GPU_SHADERSTAGE_VERTEX, 0, 0, 2);
			SDL_GPUShader* fs = create_shader_from_code(fs_code, "PSMain", path, SDL_GPU_SHADERSTAGE_FRAGMENT, 13, 0, 3);
			if (vs && fs)
			{
				wait_idle();
				const u32 b = blend_mode == 1 ? 1u : (blend_mode == 2 ? 2u : 0u);
				pipeline = create_scene_pipeline(vs, fs, skinned ? 52u : 32u, skinned, SDL_GPU_FILLMODE_FILL,
					double_sided ? SDL_GPU_CULLMODE_NONE : SDL_GPU_CULLMODE_BACK, true, b == 0,
					b == 0 ? SDL_GPU_COMPAREOP_LESS : SDL_GPU_COMPAREOP_LESS_OR_EQUAL, b, mirrored && !double_sided);
			}
			if (vs && !skinned) SDL_ReleaseGPUShader(m_device, vs);
			if (fs) SDL_ReleaseGPUShader(m_device, fs);
		}
		else if (it == m_pipeline_cache.end())
		{
			log("custom material shader '" + path + "' is not a " + shaderfmt::material_suffix
				+ " file (or it failed to compile) — the built-in PBR shader stays active on this backend");
		}
		auto& e = m_pipeline_cache[path];
		e.mtime[variant] = mt;   // a failed compile keeps the last-good variant and is not retried until the file changes
		if (pipeline)
		{
			if (e.pipeline[variant]) SDL_ReleaseGPUGraphicsPipeline(m_device, e.pipeline[variant]);
			e.pipeline[variant] = pipeline;
		}
		return e.pipeline[variant];
	}

	SDL_GPUGraphicsPipeline* SdlGpuRenderer::custom_pipeline(u32 material_id, u32 blend_mode, bool double_sided, bool mirrored, bool skinned)
	{
		auto it = m_custom_shaders.find(material_id);
		if (it == m_custom_shaders.end() || it->second.path.empty()) return nullptr;
		return get_or_compile_pipeline(it->second.path, blend_mode, double_sided, mirrored, skinned);
	}

	void SdlGpuRenderer::set_material_shader(u32 material_id, const std::string& shader_path)
	{
		if (shader_path.empty()) { m_custom_shaders.erase(material_id); return; }
		auto& e = m_custom_shaders[material_id];
		e.path = shader_path;
		get_or_compile_pipeline(shader_path);   // the opaque variant now, so a broken file is reported at assignment
		e.mtime = file_mtime(shader_path);
	}

	int SdlGpuRenderer::reload_dirty_shaders()
	{
		int changed = 0;
		for (auto& kv : m_pipeline_cache)
		{
			const unsigned long long mt = file_mtime(kv.first);
			if (mt == 0ull) continue;
			// every variant that was built from an older file is rebuilt (a variant nobody asked for yet stays empty)
			for (u32 v = 0; v < PIPELINE_VARIANTS; ++v)
			{
				if (!kv.second.pipeline[v] || kv.second.mtime[v] == mt) continue;
				SDL_GPUGraphicsPipeline* before = kv.second.pipeline[v];
				if (get_or_compile_pipeline(kv.first, v % 3, (v / 3) % 2 != 0, (v / 6) % 2 != 0, v >= 12) != before || kv.second.mtime[v] == mt) ++changed;
			}
		}
		if (changed == 0) return 0;
		for (auto& kv : m_custom_shaders) kv.second.mtime = file_mtime(kv.second.path);
		return changed;
	}

	namespace
	{
		// SDL reports a shader compiler's output (e.g. Metal's "program_source:12:5: error: …") through its log, not
		// SDL_GetError: validate_material_shader listens to the log while it compiles.
		struct SdlLogCapture
		{
			std::string text;
			SDL_LogOutputFunction prev_fn = nullptr;
			void* prev_ud = nullptr;
		};

		void SDLCALL capture_sdl_log(void* userdata, int category, SDL_LogPriority priority, const char* message)
		{
			auto* c = static_cast<SdlLogCapture*>(userdata);
			if (message && priority >= SDL_LOG_PRIORITY_WARN) { c->text += message; c->text += "\n"; }
			if (c->prev_fn) c->prev_fn(c->prev_ud, category, priority, message);
		}

		std::string stage_error(const SdlLogCapture& cap)
		{
			const char* e = SDL_GetError();
			std::string s = (e && *e) ? std::string(e) : std::string();
			if (!cap.text.empty()) s += (s.empty() ? "" : "\n") + cap.text;
			return s.empty() ? std::string("compile failed (no compiler output)") : s;
		}
	}

	bool SdlGpuRenderer::validate_material_shader(const std::string& path, std::string& errors)
	{
		errors.clear();
		if (!m_initialized) { errors = "the renderer is not running"; return false; }
		const char* ext = shaderfmt::material_suffix;
		const size_t n = std::strlen(ext);
		if (path.size() <= n || path.compare(path.size() - n, n, ext) != 0)
		{
			errors = std::string("this backend compiles ") + ext + " material shaders";
			return false;
		}
		std::vector<unsigned char> vs_code, fs_code;
		if (!load_material_shader(path, SDL_GPU_SHADERSTAGE_VERTEX, vs_code) || !load_material_shader(path, SDL_GPU_SHADERSTAGE_FRAGMENT, fs_code))
		{
			errors = shaderfmt::per_entrypoint ? "glslc could not compile the shader (run glslc on it for the details; VORTEX_GLSLC selects the compiler)"
											   : "the shader file could not be read";
			return false;
		}
		SdlLogCapture cap;
		SDL_GetLogOutputFunction(&cap.prev_fn, &cap.prev_ud);
		SDL_SetLogOutputFunction(capture_sdl_log, &cap);
		SDL_ClearError();
		SDL_GPUShader* vs = create_shader_from_code(vs_code, "VSMain", path, SDL_GPU_SHADERSTAGE_VERTEX, 0, 0, 2);
		if (!vs) errors += "VSMain: " + stage_error(cap) + "\n";
		std::string vs_text = cap.text;
		cap.text.clear();
		SDL_ClearError();
		SDL_GPUShader* fs = create_shader_from_code(fs_code, "PSMain", path, SDL_GPU_SHADERSTAGE_FRAGMENT, 13, 0, 3);
		// Metal compiles the whole source for each stage: report the same diagnostics once
		if (!fs) errors += (!vs && cap.text == vs_text) ? std::string("PSMain: the same errors\n") : "PSMain: " + stage_error(cap) + "\n";
		cap.text.clear();
		bool ok = vs && fs;
		if (ok)
		{
			SDL_ClearError();
			SDL_GPUGraphicsPipeline* p = create_scene_pipeline(vs, fs, 32, false, SDL_GPU_FILLMODE_FILL, SDL_GPU_CULLMODE_BACK, true, true, SDL_GPU_COMPAREOP_LESS, 0);
			if (p) SDL_ReleaseGPUGraphicsPipeline(m_device, p);
			else
			{
				ok = false;
				errors = "Both stages compile, but no pipeline could be built with them (" + stage_error(cap)
					+ "): they do not match the engine's vertex layout or bindings (start from the Standard shader template).";
			}
		}
		SDL_SetLogOutputFunction(cap.prev_fn, cap.prev_ud);
		if (vs) SDL_ReleaseGPUShader(m_device, vs);
		if (fs) SDL_ReleaseGPUShader(m_device, fs);
		return ok;
	}

	bool SdlGpuRenderer::any_material_shader_dirty() const
	{
		for (auto& kv : m_pipeline_cache)
		{
			const unsigned long long mt = file_mtime(kv.first);
			if (mt == 0ull) continue;
			for (u32 v = 0; v < PIPELINE_VARIANTS; ++v)
				if (kv.second.pipeline[v] && kv.second.mtime[v] != mt) return true;
		}
		return false;
	}

	// ---------------------------------------------------------------------------------------------
	// Overlay wide-string entry points (wchar_t is UTF-32 on macOS/Linux; the API's UTF-8 twins skip this)
	// ---------------------------------------------------------------------------------------------
	namespace
	{
		std::string wide_to_utf8(const wchar_t* s)
		{
			std::string out;
			if (!s) return out;
			for (; *s; ++s)
			{
				u32 cp = (u32)*s;
				if (cp < 0x80) out.push_back((char)cp);
				else if (cp < 0x800) { out.push_back((char)(0xC0 | (cp >> 6))); out.push_back((char)(0x80 | (cp & 0x3F))); }
				else if (cp < 0x10000) { out.push_back((char)(0xE0 | (cp >> 12))); out.push_back((char)(0x80 | ((cp >> 6) & 0x3F))); out.push_back((char)(0x80 | (cp & 0x3F))); }
				else { out.push_back((char)(0xF0 | (cp >> 18))); out.push_back((char)(0x80 | ((cp >> 12) & 0x3F))); out.push_back((char)(0x80 | ((cp >> 6) & 0x3F))); out.push_back((char)(0x80 | (cp & 0x3F))); }
			}
			return out;
		}
	}

	void SdlGpuRenderer::ui_text(float x, float y, float w, float h, const wchar_t* s, float size, float r, float g, float b, float a, int align, int weight)
	{
		m_overlay.add_text(x, y, w, h, wide_to_utf8(s), size, r, g, b, a, align, weight);
	}

	void SdlGpuRenderer::ui_image(float x, float y, float w, float h, const wchar_t* path, float r, float g, float b, float a)
	{
		m_overlay.add_image(x, y, w, h, wide_to_utf8(path), r, g, b, a);
	}

	// ---------------------------------------------------------------------------------------------
	// Secondary render targets
	// ---------------------------------------------------------------------------------------------
	u32 SdlGpuRenderer::create_render_target(u32 width, u32 height)
	{
		if (!m_initialized || width == 0 || height == 0) return 0;
		if (m_render_targets.size() >= MAX_RENDER_TARGETS) return 0;
		auto target = std::make_unique<GpuTarget>();
		if (!ensure_target(*target, width, height, true, true)) return 0;
		const u32 id = m_next_render_target_id++;
		m_render_targets[id] = std::move(target);
		return id;
	}

	void SdlGpuRenderer::destroy_render_target(u32 target_id)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end()) return;
		wait_idle();
		release_target(*it->second);
		m_render_targets.erase(it);
	}

	bool SdlGpuRenderer::resize_render_target(u32 target_id, u32 width, u32 height)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end()) return false;
		return ensure_target(*it->second, width, height, true, true);
	}

	void SdlGpuRenderer::render_to_target(u32 target_id, const ViewportCamera& camera, bool render_grid, bool render_gizmos)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end() || !m_initialized) return;
		GpuTarget& target = *it->second;
		if (target.readback_mapped) { SDL_UnmapGPUTransferBuffer(m_device, target.readback); target.readback_mapped = nullptr; }

		SDL_GPUCommandBuffer* cmd = SDL_AcquireGPUCommandBuffer(m_device);
		if (!cmd) return;
		FrameView view = build_camera_view(camera, target.width, target.height);
		record_scene(cmd, target, view, true, render_grid, render_gizmos, m_particles.consume_target_world(), false);
		SDL_SubmitGPUCommandBuffer(cmd);
	}

	bool SdlGpuRenderer::download_target(GpuTarget& target)
	{
		if (!target.color || !target.readback) return false;
		if (target.readback_mapped) { SDL_UnmapGPUTransferBuffer(m_device, target.readback); target.readback_mapped = nullptr; }
		SDL_GPUCommandBuffer* cmd = SDL_AcquireGPUCommandBuffer(m_device);
		if (!cmd) return false;
		SDL_GPUCopyPass* copy = SDL_BeginGPUCopyPass(cmd);
		SDL_GPUTextureRegion region{}; region.texture = target.color; region.w = target.width; region.h = target.height; region.d = 1;
		SDL_GPUTextureTransferInfo dst{ target.readback, 0, target.width, target.height };
		SDL_DownloadFromGPUTexture(copy, &region, &dst);
		SDL_EndGPUCopyPass(copy);
		SDL_GPUFence* fence = SDL_SubmitGPUCommandBufferAndAcquireFence(cmd);
		if (!fence) return false;
		SDL_WaitForGPUFences(m_device, true, &fence, 1);
		SDL_ReleaseGPUFence(m_device, fence);
		return true;
	}

	bool SdlGpuRenderer::prepare_render_target_readback(u32 target_id)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end()) return false;
		return download_target(*it->second);
	}

	const void* SdlGpuRenderer::read_render_target_pixels(u32 target_id, u32& out_width, u32& out_height, u32& out_row_pitch)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end()) { out_width = out_height = out_row_pitch = 0; return nullptr; }
		GpuTarget& target = *it->second;
		out_width = target.width; out_height = target.height; out_row_pitch = target.width * 4;
		if (!target.readback_mapped) target.readback_mapped = SDL_MapGPUTransferBuffer(m_device, target.readback, false);
		return target.readback_mapped;
	}

	void SdlGpuRenderer::release_render_target_pixels(u32 target_id)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end()) return;
		GpuTarget& target = *it->second;
		if (target.readback_mapped) { SDL_UnmapGPUTransferBuffer(m_device, target.readback); target.readback_mapped = nullptr; }
	}
}
