#pragma once
#include "../Importers/TextureCompress.h"

#include "../../Common/CommonHeaders.h"
#include "../../Common/Id.h"
#include "Mesh.h"
#include "Texture.h"
#include "Material.h"
#include "../Geometry/IMeshGenerator.h"
#include "../Importers/ModelImporter.h"
#include "../Importers/TextureImporter.h"
#include <d3d12.h>
#include <wrl/client.h>
#include <unordered_map>
#include <deque>
#include <memory>
#include <string>
#include <vector>

namespace vortex::graphics
{
	using Microsoft::WRL::ComPtr;
	/// <summary>
	/// Central registry for all GPU resources.
	/// Manages lifetime and provides access by ID.
	/// </summary>
	class ResourceRegistry
	{
	public:
		static ResourceRegistry& instance();

		void initialize(ID3D12Device* device);
		void shutdown();

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
		std::vector<id::id_type> get_all_mesh_ids() const;
		// Bumped by every destroy_mesh: renderers that cache raw Mesh pointers (draw runs) compare it each frame and
		// drop / rebuild what referenced a destroyed mesh instead of touching freed memory.
		u32 mesh_generation() const { return m_mesh_generation; }

		// Geometric LOD: each imported base mesh gets a chain of decimated lower-poly meshes for distant
		// rendering. lods[0] = the full-res base (unchanged id, so existing scenes keep working).
		struct LodChain
		{
			id::id_type lods[4]{ id::invalid_id, id::invalid_id, id::invalid_id, id::invalid_id };
			u32 lod_count{ 1 };
			float radius{ 1.0f };   // base mesh radius (local units) — for distance thresholds
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
		/// <summary>Import a texture from an in-memory buffer (packed asset pak loaded into RAM).</summary>
		id::id_type import_texture_from_memory(const u8* data, u64 length, const std::string& name = "");
		bool export_mesh_to_vmesh(id::id_type mesh_id, const std::string& filepath);
		id::id_type load_vmesh(const std::string& filepath);

		// Multi-material import result structure
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

		// Import model with separate meshes and materials per submesh
		MultiMaterialImportResult import_model_with_materials(const std::string& filepath);
		/// <summary>Like import_model_with_materials, but also writes one .vmesh per submesh into cache_dir (the
		/// render-side import cache, #364 C) so the next start loads the model without Assimp. Nothing is written for
		/// a skinned model; result.cached tells how many files were written.</summary>
		MultiMaterialImportResult import_model_with_materials_cached(const std::string& filepath, const std::string& cache_dir);
		/// <summary>Load a model from the folder import_model_with_materials_cached wrote: meshes from the .vmesh files,
		/// materials from the records — no Assimp. Not successful when the cache is missing, incomplete or stale.</summary>
		MultiMaterialImportResult import_model_from_cache(const std::string& cache_dir);
		/// <summary>Import a multi-material model from an in-memory buffer (packed asset pak loaded into RAM).</summary>
		MultiMaterialImportResult import_model_with_materials_from_memory(const u8* data, u64 length,
			const std::string& ext_hint, const std::string& virtual_dir, bool left_handed = false);

		// Default resources
		id::id_type default_cube_mesh() const { return m_default_cube; }
		id::id_type default_sphere_mesh() const { return m_default_sphere; }
		id::id_type default_plane_mesh() const { return m_default_plane; }
		id::id_type default_white_texture() const { return m_default_white_texture; }
		id::id_type default_material() const { return m_default_material; }

		bool is_initialized() const { return m_device != nullptr; }

		// Get SRV descriptor heap for rendering
		ID3D12DescriptorHeap* srv_heap() const { return m_srv_heap.Get(); }

		// Reserve ONE slot of this shared shader-visible heap for an EXTERNAL long-lived descriptor
		// (e.g. the renderer's shadow map at t7). The scene pass binds THIS heap, and D3D12 allows only
		// one CBV_SRV_UAV heap bound at a time — so any view sampled mid-scene-pass must live here, not
		// in a private per-resource heap. The bump allocator never frees: reserve once at init and
		// re-create the view IN PLACE (same handles) when the underlying resource is recreated/resized.
		bool reserve_srv_slot(D3D12_CPU_DESCRIPTOR_HANDLE& out_cpu, D3D12_GPU_DESCRIPTOR_HANDLE& out_gpu);

	private:
		u32 m_mesh_generation{ 0 };
		ResourceRegistry() = default;

		id::id_type create_mesh_from_submesh(const SubMeshData& submesh, const std::string& name);
		void register_lod_chain(id::id_type base_mesh_id, const SubMeshData& submesh, const std::string& name);
		std::unordered_map<id::id_type, LodChain> m_lod_chains;
		// Shared cores so the file-based and in-memory import paths build resources identically.
		id::id_type create_texture_from_image(ImageData& image_data, const std::string& label, const std::string* cache_path = nullptr);
		MultiMaterialImportResult build_model_result(ImportedModelData& model_data);

		ID3D12Device* m_device{ nullptr };

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

		// SRV Descriptor Heap for texture sampling
		ComPtr<ID3D12DescriptorHeap> m_srv_heap;
		UINT m_srv_descriptor_size{ 0 };
		UINT m_next_srv_index{ 0 };
		// SRV slots of destroyed textures. Reused oldest first and only once a few have piled up (or the heap is full), so
		// a frame still in flight never sees its descriptor overwritten right after the texture went away.
		std::deque<UINT> m_free_srv_slots;
		static constexpr UINT MAX_SRV_DESCRIPTORS = 1024;

		bool create_srv_heap();
		void assign_srv_to_texture(Texture* texture);
	};
}
