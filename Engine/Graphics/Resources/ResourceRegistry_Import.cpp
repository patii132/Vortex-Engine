#include "../../Common/VerboseLog.h"
#include "ResourceRegistry_Internal.h"
#include <filesystem>
#include <future>
#include <thread>
#include <algorithm>

namespace vortex::graphics
{
	id::id_type ResourceRegistry::import_model(const std::string& filepath)
	{
		if (!m_device) 
		{
			VORTEX_VLOG("ResourceRegistry::import_model - Device not initialized!\n");
			return id::invalid_id;
		}

		VORTEX_VLOG(("ResourceRegistry: Importing model: " + filepath + "\n").c_str());

		ImportedModelData data = ModelImporter::import_from_file(filepath);
		if (!data.is_valid())
		{
			VORTEX_VLOG("ResourceRegistry: ModelImporter returned invalid data!\n");
			return id::invalid_id;
		}

		VORTEX_VLOG(("ResourceRegistry: Model has " + std::to_string(data.submeshes.size()) + " submeshes\n").c_str());

		MeshData combined_data;
		u32 index_offset = 0;

		for (const auto& submesh : data.submeshes)
		{
			for (const auto& vertex : submesh.vertices)
			{
				combined_data.vertices.push_back(vertex);
			}
			for (auto idx : submesh.indices)
			{
				combined_data.indices.push_back(idx + index_offset);
			}
			index_offset += static_cast<u32>(submesh.vertices.size());
		}

		return create_mesh(combined_data, data.name);
	}


	namespace
	{
		// "<path>|<mtime>|<size>" for a texture file on disk (an edited file gets a new key), empty otherwise.
		std::string texture_cache_key(const std::string& path)
		{
			if (path.empty()) return std::string();
			std::error_code ec;
			const std::filesystem::path p(path);
			const auto size = std::filesystem::file_size(p, ec);
			if (ec) return std::string();
			const auto time = std::filesystem::last_write_time(p, ec);
			if (ec) return std::string();
			return path + "|" + std::to_string((long long)time.time_since_epoch().count()) + "|" + std::to_string((unsigned long long)size);
		}
	}

	id::id_type ResourceRegistry::import_texture(const std::string& filepath, const std::string& name)
	{
		if (!m_device) return id::invalid_id;
		// Path cache: an unchanged file is uploaded once and shared. Model imports, previews and thumbnails import
		// the same maps over and over and textures are never freed, so every re-import used to cost VRAM.
		const std::string key = texture_cache_key(filepath);
		if (!key.empty())
		{
			auto it = m_texture_path_cache.find(key);
			if (it != m_texture_path_cache.end() && get_texture(it->second)) return it->second;
		}

		// the compressed-texture cache (#179): a known file skips the decode and the encode
		if (TextureCompress::enabled() && !TextureCompress::cache_dir().empty())
		{
			const std::string ck = TextureCompress::cache_key(filepath, CompressKind::Auto);
			CompressedTexture ct;
			if (!ck.empty() && TextureCompress::load_cached(ck, ct))
			{
				const id::id_type cid = create_texture_compressed(ct, filepath + " (cache)");
				if (cid != id::invalid_id) { if (!key.empty()) { m_texture_path_cache[key] = cid; retire_stale_textures(filepath, cid); } return cid; }
			}
		}
		ImageData image_data = TextureImporter::import_from_file(filepath);
		if (!image_data.is_valid())
		{
			VORTEX_VLOG(("Failed to load texture: " + filepath + "\n").c_str());
			return id::invalid_id;
		}
		const id::id_type id = create_texture_from_image(image_data, filepath, &filepath);
		if (!key.empty() && id != id::invalid_id) { m_texture_path_cache[key] = id; retire_stale_textures(filepath, id); }
		return id;
	}


	void ResourceRegistry::prefetch_textures(const std::vector<std::string>& paths)
	{
		if (!m_device) return;
		std::vector<std::pair<std::string, std::string>> todo;   // path, cache key
		for (const auto& p : paths)
		{
			if (p.empty()) continue;
			std::string key = texture_cache_key(p);
			if (key.empty()) continue;
			auto hit = m_texture_path_cache.find(key);
			if (hit != m_texture_path_cache.end() && get_texture(hit->second)) continue;
			if (TextureCompress::enabled() && !TextureCompress::cache_dir().empty())
			{
				CompressedTexture ct;
				const std::string ck = TextureCompress::cache_key(p, CompressKind::Auto);
				if (!ck.empty() && TextureCompress::load_cached(ck, ct))
				{
					const id::id_type cid = create_texture_compressed(ct, p + " (cache)");
					if (cid != id::invalid_id) { m_texture_path_cache[key] = cid; retire_stale_textures(p, cid); continue; }
				}
			}
			bool dup = false;
			for (const auto& t : todo) if (t.second == key) { dup = true; break; }
			if (!dup) todo.emplace_back(p, std::move(key));
		}
		if (todo.size() < 2) return;   // nothing to overlap
		// Waves of at most `lanes` decodes: a 4K map is 64 MB of RGBA, so a big model must not decode all at once.
		const size_t lanes = (std::max)(size_t(2), (std::min)(size_t(8), size_t(std::thread::hardware_concurrency())));
		for (size_t start = 0; start < todo.size(); start += lanes)
		{
			const size_t end = (std::min)(todo.size(), start + lanes);
			std::vector<std::future<ImageData>> jobs;
			for (size_t i = start; i < end; ++i)
				jobs.push_back(std::async(std::launch::async, [path = todo[i].first] { return TextureImporter::import_from_file(path); }));
			for (size_t i = start; i < end; ++i)
			{
				ImageData image = jobs[i - start].get();
				if (!image.is_valid()) continue;
				const id::id_type id = create_texture_from_image(image, todo[i].first, &todo[i].first);
				if (id != id::invalid_id) { m_texture_path_cache[todo[i].second] = id; retire_stale_textures(todo[i].first, id); }
			}
		}
	}

	id::id_type ResourceRegistry::import_texture_from_memory(const u8* data, u64 length, const std::string& name)
	{
		if (!m_device) return id::invalid_id;

		ImageData image_data = TextureImporter::import_from_memory(data, length);
		if (!image_data.is_valid())
		{
			VORTEX_VLOG("Failed to load texture from memory\n");
			return id::invalid_id;
		}
		return create_texture_from_image(image_data, name.empty() ? std::string("memtex") : name);
	}


	ResourceRegistry::MultiMaterialImportResult ResourceRegistry::import_model_with_materials(const std::string& filepath)
	{
		MultiMaterialImportResult result;
		result.success = false;

		if (!m_device)
		{
			VORTEX_VLOG("ResourceRegistry not initialized\n");
			return result;
		}

		VORTEX_VLOG(("=== Multi-Material Import: " + filepath + " ===\n").c_str());

		// Import model - ModelImporter now handles texture assignment
		ImportedModelData model_data = ModelImporter::import_from_file(filepath);
		if (!model_data.is_valid())
		{
			VORTEX_VLOG("Import failed - no valid data\n");
			return result;
		}
		return build_model_result(model_data);
	}


	ResourceRegistry::MultiMaterialImportResult ResourceRegistry::import_model_with_materials_cached(
		const std::string& filepath, const std::string& cache_dir)
	{
		MultiMaterialImportResult result;
		result.success = false;
		if (!m_device)
		{
			VORTEX_VLOG("ResourceRegistry not initialized\n");
			return result;
		}

		VORTEX_VLOG(("=== Multi-Material Import (cached): " + filepath + " ===\n").c_str());
		ImportedModelData model_data = ModelImporter::import_from_file(filepath);
		if (!model_data.is_valid())
		{
			VORTEX_VLOG("Import failed - no valid data\n");
			return result;
		}
		// the render-side import cache (#364 C): one .vmesh per submesh, so the next start skips Assimp
		const u32 cached = cache_dir.empty() ? 0 : MeshSerializer::save_submeshes_to_dir(model_data, cache_dir);
		result = build_model_result(model_data);
		result.cached = cached;
		return result;
	}


	ResourceRegistry::MultiMaterialImportResult ResourceRegistry::import_model_from_cache(const std::string& cache_dir)
	{
		MultiMaterialImportResult result;
		result.success = false;
		if (!m_device) return result;
		ImportedModelData model_data;
		if (!MeshSerializer::load_submeshes_from_dir(cache_dir, model_data))
		{
			VORTEX_VLOG(("Model cache unusable: " + cache_dir + "\n").c_str());
			return result;
		}
		VORTEX_VLOG(("=== Multi-Material Import (from cache): " + cache_dir + " ===\n").c_str());
		return build_model_result(model_data);
	}


	ResourceRegistry::MultiMaterialImportResult ResourceRegistry::import_model_with_materials_from_memory(
		const u8* data, u64 length, const std::string& ext_hint, const std::string& virtual_dir, bool left_handed)
	{
		MultiMaterialImportResult result;
		result.success = false;
		if (!m_device)
		{
			VORTEX_VLOG("ResourceRegistry not initialized\n");
			return result;
		}

		VORTEX_VLOG(("=== Multi-Material Import (memory): ." + ext_hint + " ===\n").c_str());
		ImportedModelData model_data = ModelImporter::import_from_memory(data, length, ext_hint, virtual_dir, left_handed);
		if (!model_data.is_valid())
		{
			VORTEX_VLOG("Import from memory failed - no valid data\n");
			return result;
		}
		return build_model_result(model_data);
	}


	ResourceRegistry::MultiMaterialImportResult ResourceRegistry::build_model_result(ImportedModelData& model_data)
	{
		MultiMaterialImportResult result;
		result.success = false;

		result.model_name = model_data.name;
		VORTEX_VLOG(("Model: " + model_data.name + ", " +
			std::to_string(model_data.submeshes.size()) + " submeshes\n").c_str());

		// Decode every map the model references in parallel up front; the per-submesh binds below hit the cache.
		{
			std::vector<std::string> maps;
			for (const auto& sm : model_data.submeshes)
			{
				maps.push_back(sm.diffuse_texture);
				if (!sm.normal_is_bump) maps.push_back(sm.normal_texture);
				maps.push_back(sm.metallic_texture); maps.push_back(sm.roughness_texture); maps.push_back(sm.ao_texture);
			}
			prefetch_textures(maps);
		}

		for (size_t i = 0; i < model_data.submeshes.size(); i++)
		{
			const auto& submesh = model_data.submeshes[i];
			SubmeshImportResult sub_result;
			sub_result.material_index = submesh.material_index;
			sub_result.name = submesh.name.empty() ? ("Submesh_" + std::to_string(i)) : submesh.name;

			VORTEX_VLOG(("Processing: " + sub_result.name + "\n").c_str());

			// Create mesh
			sub_result.mesh_id = create_mesh_from_submesh(submesh, sub_result.name);
			if (sub_result.mesh_id == id::invalid_id)
			{
				continue;
			}

			// Create material
			sub_result.material_id = create_material(sub_result.name + "_material");
			if (sub_result.material_id == id::invalid_id)
			{
				continue;
			}

			auto* mat = get_material(sub_result.material_id);
			if (mat)
			{
				mat->set_base_color({ submesh.base_color[0], submesh.base_color[1], submesh.base_color[2], submesh.base_color[3] });
				mat->set_metallic(submesh.metallic);
				mat->set_roughness(submesh.roughness);
			}

			// Load texture - ModelImporter already assigned the correct path
			if (!submesh.diffuse_texture.empty())
			{
				VORTEX_VLOG(("  Texture: " + submesh.diffuse_texture + "\n").c_str());
				sub_result.texture_id = import_texture(submesh.diffuse_texture, sub_result.name + "_tex");
				if (sub_result.texture_id != id::invalid_id && mat)
				{
					auto* tex = get_texture(sub_result.texture_id);
					if (tex)
					{
						mat->set_albedo_texture(tex);
						VORTEX_VLOG("  Texture bound OK\n");
					}
				}
			}
			else
			{
				VORTEX_VLOG("  No texture assigned\n");
			}
			// Every other PBR map the material references. import_texture shares an unchanged file, so a packed
			// glTF/ORM map used for metallic, roughness AND occlusion is uploaded once; the channels tell the shader
			// which component to read. Bump maps posing as normal maps (OBJ map_Bump) stay unbound.
			if (mat)
			{
				auto load = [&](const std::string& path, const char* tag) -> Texture* {
					if (path.empty()) return nullptr;
					const id::id_type tid = import_texture(path, sub_result.name + tag);
					return tid != id::invalid_id ? get_texture(tid) : nullptr;
				};
				mat->set_texture_channels(submesh.metallic_channel, submesh.roughness_channel, submesh.ao_channel);
				if (!submesh.normal_is_bump)
					if (auto* t = load(submesh.normal_texture, "_normal")) { mat->set_normal_texture(t); mat->set_use_directx_normals(!submesh.normal_opengl); }
				if (auto* t = load(submesh.metallic_texture, "_metallic")) mat->set_metallic_texture(t);
				if (auto* t = load(submesh.roughness_texture, "_roughness")) mat->set_roughness_texture(t);
				if (auto* t = load(submesh.ao_texture, "_ao")) mat->set_ao_texture(t);
			}

			result.submeshes.push_back(sub_result);
		}

		result.success = !result.submeshes.empty();
		VORTEX_VLOG(("=== Import Complete: " + std::to_string(result.submeshes.size()) + " submeshes ===\n").c_str());

		return result;
	}


}
