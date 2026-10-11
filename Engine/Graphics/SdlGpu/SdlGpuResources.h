#pragma once
#include "../Importers/TextureCompress.h"

// ============================================================================
// GPU resources for the SDL GPU backend (Metal on macOS, Vulkan on Linux).
//
// Same class names and public surface as the DX12 resource layer in
// Graphics/Resources (Mesh / Texture / Material / ResourceRegistry) so the
// VortexAPI, the importers and the components compile unchanged against either
// backend (Graphics/Backend.h selects one per platform).
// ============================================================================
#include "../../Common/CommonHeaders.h"
#include "../../Common/Id.h"
#include "../Geometry/IMeshGenerator.h"
#include "../Importers/ModelImporter.h"
#include "../Importers/TextureImporter.h"
#include "../Resources/MaterialProperties.h"
#include <SDL3/SDL.h>
#include <memory>
#include <string>
#include <unordered_map>
#include <vector>

namespace vortex::graphics
{
	enum class TextureFormat
	{
		RGBA8_UNORM,
		RGBA8_SRGB,
		BGRA8_UNORM,
		R8_UNORM,
		RG8_UNORM,
		RGBA16_FLOAT,
		RGBA32_FLOAT,
		BC1_UNORM,         // block compressed (#179): colour, 8 bytes per 4x4
		BC3_UNORM,         // colour + alpha, 16 bytes per 4x4
		BC4_UNORM,         // one channel, 8 bytes per 4x4
		BC5_UNORM,         // two channels (normal x / y), 16 bytes per 4x4
		D24_UNORM_S8_UINT,
		D32_FLOAT
	};

	struct TextureDesc
	{
		u32 width{ 0 };
		u32 height{ 0 };
		TextureFormat format{ TextureFormat::RGBA8_UNORM };
		bool generate_mips{ false };
		u32 mip_count{ 0 };        // > 1: data holds this many levels, tightly packed, level 0 first (block formats come this way)
		bool is_render_target{ false };
		bool is_depth_stencil{ false };
	};

	/// GPU-resident mesh: one vertex buffer (32-byte rigid or 52-byte skinned vertices) + optional 32-bit indices.
	class Mesh
	{
	public:
		Mesh() = default;
		~Mesh() { destroy(); }
		Mesh(const Mesh&) = delete;
		Mesh& operator=(const Mesh&) = delete;

		bool create(SDL_GPUDevice* device, const MeshData& data);
		bool create_from_vertices(SDL_GPUDevice* device, const void* vertices, u32 vertex_count, u32 vertex_stride,
			const u32* indices, u32 index_count);
		bool create_from_generator(SDL_GPUDevice* device, const IMeshGenerator& generator);
		void destroy();

		SDL_GPUBuffer* vertex_buffer() const { return m_vertex_buffer; }
		SDL_GPUBuffer* index_buffer() const { return m_index_buffer; }
		u32 vertex_stride() const { return m_vertex_stride; }
		u32 index_count() const { return m_index_count; }
		u32 vertex_count() const { return m_vertex_count; }
		bool has_indices() const { return m_index_count > 0; }
		bool is_valid() const { return m_vertex_buffer != nullptr; }

		bool is_skinned() const { return m_skinned; }
		void set_skinned(bool s) { m_skinned = s; }

		const std::string& name() const { return m_name; }
		void set_name(const std::string& name) { m_name = name; }

		void set_bounds(float minX, float minY, float minZ, float maxX, float maxY, float maxZ);
		void get_bounds(float& sizeX, float& sizeY, float& sizeZ) const;
		void get_bounds_center(float& centerX, float& centerY, float& centerZ) const;
		void get_min(float& x, float& y, float& z) const { x = m_bounds_min[0]; y = m_bounds_min[1]; z = m_bounds_min[2]; }
		void get_max(float& x, float& y, float& z) const { x = m_bounds_max[0]; y = m_bounds_max[1]; z = m_bounds_max[2]; }

	private:
		SDL_GPUDevice* m_device{ nullptr };
		SDL_GPUBuffer* m_vertex_buffer{ nullptr };
		SDL_GPUBuffer* m_index_buffer{ nullptr };
		u32 m_vertex_stride{ 0 };
		u32 m_vertex_count{ 0 };
		u32 m_index_count{ 0 };
		bool m_skinned{ false };
		std::string m_name;
		float m_bounds_min[3]{ 0, 0, 0 };
		float m_bounds_max[3]{ 1, 1, 1 };
	};

	/// Sampled 2D texture with a full mip chain generated on the GPU.
	class Texture
	{
	public:
		Texture() = default;
		~Texture() { destroy(); }
		Texture(const Texture&) = delete;
		Texture& operator=(const Texture&) = delete;

		bool create(SDL_GPUDevice* device, const TextureDesc& desc, const void* data = nullptr);
		bool create_from_color(SDL_GPUDevice* device, u32 color);
		void destroy();
		static bool is_block_format(TextureFormat f) { return f == TextureFormat::BC1_UNORM || f == TextureFormat::BC3_UNORM || f == TextureFormat::BC4_UNORM || f == TextureFormat::BC5_UNORM; }
		static u64 level_bytes(TextureFormat f, u32 w, u32 h);
		// whether the device samples BCn (every desktop GPU; a mobile part would not)
		static bool supports_block_formats(SDL_GPUDevice* device);

		SDL_GPUTexture* texture() const { return m_texture; }
		u32 width() const { return m_width; }
		u32 height() const { return m_height; }
		u32 mip_levels() const { return m_mip_levels; }
		TextureFormat format() const { return m_format; }
		bool is_valid() const { return m_texture != nullptr; }

		static SDL_GPUTextureFormat to_sdl_format(TextureFormat format);
		static u32 bytes_per_pixel(TextureFormat format);

	private:
		SDL_GPUDevice* m_device{ nullptr };
		SDL_GPUTexture* m_texture{ nullptr };
		u32 m_width{ 0 };
		u32 m_height{ 0 };
		u32 m_mip_levels{ 1 };
		TextureFormat m_format{ TextureFormat::RGBA8_UNORM };
	};

	/// PBR material: CPU-side properties (pushed as uniforms per draw run by the renderer) + texture slots.
	class Material
	{
	public:
		Material() = default;
		~Material() = default;

		bool create(SDL_GPUDevice* device) { m_valid = device != nullptr; return m_valid; }
		void destroy();

		void set_base_color(const DirectX::XMFLOAT4& color) { m_properties.base_color = color; }
		void set_metallic(float value) { m_properties.metallic = value; }
		void set_roughness(float value) { m_properties.roughness = value; }
		void set_ao(float value) { m_properties.ao = value; }
		void set_normal_strength(float value) { m_properties.normal_strength = value; }
		void set_use_directx_normals(bool use_directx) { m_properties.use_directx_normals = use_directx ? 1 : 0; }
		void set_unlit(bool is_unlit) { m_properties.is_unlit = is_unlit ? 1 : 0; }
		void set_emissive_strength(float strength) { m_properties.emissive_strength = strength; }
		void set_uv_tiling(float u, float v) { m_properties.uv_tiling = { u, v }; }
		void set_height_scale(float value) { m_properties.height_scale = value; }
		// AlphaTest (#329): fragments whose alpha falls below the cutoff are discarded (0 = off).
		void set_alpha_cutoff(float cutoff) { m_properties.alpha_cutoff = cutoff < 0.0f ? 0.0f : cutoff; }
		float alpha_cutoff() const { return m_properties.alpha_cutoff; }
		// TwoSided: drawn without back-face culling (cut-outs, foliage, cloth) — a pipeline choice, not CB data.
		void set_double_sided(bool two_sided) { m_double_sided = two_sided; }
		bool double_sided() const { return m_double_sided; }
		void set_blend_mode(u32 mode) { m_blend_mode = (mode <= 3) ? mode : 0; }   // 3 = alpha test (#329)
		u32 blend_mode() const { return m_blend_mode; }

		void set_albedo_texture(Texture* texture) { m_albedo_texture = texture; m_properties.has_albedo_texture = (texture && texture->is_valid()) ? 1 : 0; }
		// 2 = a two-channel (BC5) map: the shader rebuilds z
		void set_normal_texture(Texture* texture) { m_normal_texture = texture; m_properties.has_normal_texture = (texture && texture->is_valid()) ? (texture->format() == TextureFormat::BC5_UNORM ? 2u : 1u) : 0u; }
		void set_metallic_texture(Texture* texture) { m_metallic_texture = texture; m_properties.has_metallic_texture = (texture && texture->is_valid()) ? 1 + m_metallic_channel : 0; }
		void set_roughness_texture(Texture* texture) { m_roughness_texture = texture; m_properties.has_roughness_texture = (texture && texture->is_valid()) ? 1 + m_roughness_channel : 0; }
		void set_ao_texture(Texture* texture) { m_ao_texture = texture; m_properties.has_ao_texture = (texture && texture->is_valid()) ? 1 + m_ao_channel : 0; }
		// Channel a packed map is read from (0 R, 1 G, 2 B, 3 A), stored as has_*_texture = 1 + channel (0 = no
		// texture) so the shader picks it: glTF / ORM maps keep roughness in G, metallic in B, occlusion in R.
		void set_texture_channels(u32 metallic, u32 roughness, u32 ao)
		{
			m_metallic_channel = metallic & 3u; m_roughness_channel = roughness & 3u; m_ao_channel = ao & 3u;
			if (m_properties.has_metallic_texture) m_properties.has_metallic_texture = 1 + m_metallic_channel;
			if (m_properties.has_roughness_texture) m_properties.has_roughness_texture = 1 + m_roughness_channel;
			if (m_properties.has_ao_texture) m_properties.has_ao_texture = 1 + m_ao_channel;
		}
		u32 metallic_channel() const { return m_metallic_channel; }
		u32 roughness_channel() const { return m_roughness_channel; }
		u32 ao_channel() const { return m_ao_channel; }
		void set_height_texture(Texture* texture) { m_height_texture = texture; }

		const MaterialProperties& properties() const { return m_properties; }
		bool is_unlit() const { return m_properties.is_unlit != 0; }
		bool uses_directx_normals() const { return m_properties.use_directx_normals != 0; }
		void update_gpu_data() {}   // properties are pushed per draw by the renderer; nothing to sync

		Texture* albedo_texture() const { return m_albedo_texture; }
		Texture* normal_texture() const { return m_normal_texture; }
		Texture* metallic_texture() const { return m_metallic_texture; }
		Texture* roughness_texture() const { return m_roughness_texture; }
		Texture* ao_texture() const { return m_ao_texture; }
		Texture* height_texture() const { return m_height_texture; }

		bool is_valid() const { return m_valid; }
		const std::string& name() const { return m_name; }
		void set_name(const std::string& name) { m_name = name; }

	private:
		MaterialProperties m_properties;
		u32 m_blend_mode{ 0 };   // 0 opaque, 1 alpha blend, 2 additive, 3 alpha test — not part of the CB
		bool m_double_sided{ false };   // TwoSided (#329): drawn without back-face culling
		bool m_valid{ false };
		std::string m_name{ "New Material" };
		Texture* m_albedo_texture{ nullptr };
		Texture* m_normal_texture{ nullptr };
		Texture* m_metallic_texture{ nullptr };
		Texture* m_roughness_texture{ nullptr };
		Texture* m_ao_texture{ nullptr };
		Texture* m_height_texture{ nullptr };
		u32 m_metallic_channel{ 0 }, m_roughness_channel{ 0 }, m_ao_channel{ 0 };
	};

	/// Central registry for all GPU resources — lifetime + lookup by id. Mirrors the DX12 registry's surface.
	class ResourceRegistry
	{
	public:
		static ResourceRegistry& instance();

		void initialize(SDL_GPUDevice* device);
		void shutdown();
		SDL_GPUDevice* device() const { return m_device; }

		// Mesh management
		id::id_type create_mesh(const MeshData& data, const std::string& name = "");
		id::id_type create_mesh_from_generator(const IMeshGenerator& generator);
		id::id_type create_primitive_cube(float size = 1.0f);
		id::id_type create_primitive_sphere(float radius = 0.5f, u32 slices = 32, u32 stacks = 16);
		id::id_type create_inverted_sphere(float radius = 0.5f, u32 slices = 32, u32 stacks = 16);
		id::id_type create_primitive_plane(float width = 1.0f, float depth = 1.0f);
		id::id_type create_primitive_cylinder(float radius = 0.5f, float height = 1.0f, u32 slices = 32);
		id::id_type create_primitive_cone(float radius = 0.5f, float height = 1.0f, u32 slices = 32);
		Mesh* get_mesh(id::id_type id);
		void destroy_mesh(id::id_type id);
		// Bumped by every destroy_mesh that removed a mesh: the renderer compares it each frame and drops / rebuilds
		// cached draw runs (raw Mesh pointers) that referenced it instead of touching freed memory.
		u32 mesh_generation() const { return m_mesh_generation; }
		std::vector<id::id_type> get_all_mesh_ids() const;

		struct LodChain
		{
			id::id_type lods[4]{ id::invalid_id, id::invalid_id, id::invalid_id, id::invalid_id };
			u32 lod_count{ 1 };
			float radius{ 1.0f };
		};
		const LodChain* get_lod_chain(id::id_type base_mesh_id) const;

		// Texture management
		id::id_type create_texture(const TextureDesc& desc, const void* data = nullptr);
		id::id_type create_solid_color_texture(u32 color, const std::string& name = "");
		Texture* get_texture(id::id_type id);
		void destroy_texture(id::id_type id);
		std::vector<id::id_type> get_all_texture_ids() const;
		// Live counts for engine_stats / the resource smoke: a leak shows as a number that only grows (#357 #358).
		size_t mesh_count() const { return m_meshes.size(); }
		size_t texture_count() const { return m_textures.size(); }
		size_t material_count() const { return m_materials.size(); }

		// Material management
		id::id_type create_material(const std::string& name = "");
		Material* get_material(id::id_type id);
		void destroy_material(id::id_type id);
		std::vector<id::id_type> get_all_material_ids() const;

		// Import management
		id::id_type import_model(const std::string& filepath);
		id::id_type import_texture(const std::string& filepath, const std::string& name = "");
		// A block-compressed picture with its mip chain (#179) — from the compressor or its disk cache.
		id::id_type create_texture_compressed(const CompressedTexture& ct, const std::string& label);
		// Decode the not-yet-cached files among `paths` on worker threads (GPU upload stays on this thread) and
		// add them to the path cache, so the import_texture calls that follow are hits.
		void prefetch_textures(const std::vector<std::string>& paths);
		id::id_type import_texture_from_memory(const u8* data, u64 length, const std::string& name = "");
		bool export_mesh_to_vmesh(id::id_type mesh_id, const std::string& filepath);
		id::id_type load_vmesh(const std::string& filepath);

		struct SubmeshImportResult
		{
			id::id_type mesh_id{ id::invalid_id };
			id::id_type material_id{ id::invalid_id };
			id::id_type texture_id{ id::invalid_id };
			u32 material_index{ 0 };
			std::string name;
		};

		struct MultiMaterialImportResult
		{
			std::vector<SubmeshImportResult> submeshes;
			std::string model_name;
			bool success{ false };
			u32 cached{ 0 };   // .vmesh files written by import_model_with_materials_cached (0 = not cacheable)
		};

		MultiMaterialImportResult import_model_with_materials(const std::string& filepath);
		/// <summary>Like import_model_with_materials, but also writes one .vmesh per submesh into cache_dir (the
		/// render-side import cache, #364 C) so the next start loads the model without Assimp. Nothing is written for
		/// a skinned model; result.cached tells how many files were written.</summary>
		MultiMaterialImportResult import_model_with_materials_cached(const std::string& filepath, const std::string& cache_dir);
		/// <summary>Load a model from the folder import_model_with_materials_cached wrote: meshes from the .vmesh files,
		/// materials from the records — no Assimp. Not successful when the cache is missing, incomplete or stale.</summary>
		MultiMaterialImportResult import_model_from_cache(const std::string& cache_dir);
		MultiMaterialImportResult import_model_with_materials_from_memory(const u8* data, u64 length,
			const std::string& ext_hint, const std::string& virtual_dir, bool left_handed = false);

		// Default resources
		id::id_type default_cube_mesh() const { return m_default_cube; }
		id::id_type default_sphere_mesh() const { return m_default_sphere; }
		id::id_type default_plane_mesh() const { return m_default_plane; }
		id::id_type default_white_texture() const { return m_default_white_texture; }
		id::id_type default_material() const { return m_default_material; }
		Texture* white_texture() { return get_texture(m_default_white_texture); }

		bool is_initialized() const { return m_device != nullptr; }

	private:
		u32 m_mesh_generation{ 0 };
		ResourceRegistry() = default;

		id::id_type create_mesh_from_submesh(const SubMeshData& submesh, const std::string& name);
		void register_lod_chain(id::id_type base_mesh_id, const SubMeshData& submesh, const std::string& name);
		id::id_type create_texture_from_image(ImageData& image_data, const std::string& label, const std::string* cache_path = nullptr);
		MultiMaterialImportResult build_model_result(ImportedModelData& model_data);

		SDL_GPUDevice* m_device{ nullptr };
		std::unordered_map<id::id_type, LodChain> m_lod_chains;
		std::unordered_map<id::id_type, std::unique_ptr<Mesh>> m_meshes;
		std::unordered_map<id::id_type, std::unique_ptr<Texture>> m_textures;
		std::unordered_map<std::string, id::id_type> m_texture_path_cache;   // "<path>|<mtime>|<size>" -> texture
		// Every material slot showing `from` shows `to` afterwards (nullptr unbinds). Materials keep raw Texture pointers.
		void rebind_texture(Texture* from, Texture* to);
		// `path` was imported again under a new mtime/size as `keep`: the earlier uploads of that file are rebound to
		// `keep` and freed, so an edited or re-exported texture no longer leaves its predecessors in VRAM (#358).
		void retire_stale_textures(const std::string& path, id::id_type keep);
		std::unordered_map<id::id_type, std::unique_ptr<Material>> m_materials;

		id::id_type m_next_mesh_id{ 1 };
		id::id_type m_next_texture_id{ 1 };
		id::id_type m_next_material_id{ 1 };

		id::id_type m_default_cube{ id::invalid_id };
		id::id_type m_default_sphere{ id::invalid_id };
		id::id_type m_default_plane{ id::invalid_id };
		id::id_type m_default_cylinder{ id::invalid_id };
		id::id_type m_default_cone{ id::invalid_id };
		id::id_type m_default_white_texture{ id::invalid_id };
		id::id_type m_default_material{ id::invalid_id };
	};
}
