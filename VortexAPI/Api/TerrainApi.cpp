#include "../ApiCommon.h"
#include "../../Engine/Graphics/Importers/TextureImporter.h"
#include <cstring>
#include <string>
#include <vector>

namespace phys = vortex::physics;

// ============================================================================================
// Terrain interop (#124) — the small extern "C" surface the heightfield terrain needs and nothing else had:
//   * CreateMeshFromData       a render mesh from raw vertices / indices (the terrain chunks are built on the CPU in
//                              Editor/Core/Terrain/TerrainMeshBuilder.cs — sculpting rebuilds a chunk by creating a new
//                              mesh and retiring the old one; neither backend can update a vertex buffer in place)
//   * CreateTexturePixels      an RGBA8 texture from raw pixels (the splat map, the layer atlases)
//   * DecodeImageMemory        an image file's pixels for the atlas composer (stb_image, like every import)
//   * PhysicsCreateHeightFieldBody   Jolt's HeightFieldShape for the terrain collider
// Internal ABI: changed in lockstep with Editor/DllWrapper/VortexAPI.Terrain.cs.
// ============================================================================================

// vertices = 8 floats each (position xyz, normal xyz, uv) — the VertexPosNormalUV layout every mesh uses; indices =
// triangle list. bounds_min / bounds_max (3 floats each, local space) give the mesh its culling bounds — a mesh made
// from raw data would otherwise keep the default unit box and be culled wrongly. id::invalid_id on failure.
EDITOR_INTERFACE id::id_type CreateMeshFromData(const float* vertices, int32_t vertex_count, const uint32_t* indices,
	int32_t index_count, const float* bounds_min, const float* bounds_max, const char* name)
{
	if (!vertices || vertex_count < 3 || !indices || index_count < 3) return id::invalid_id;
	static_assert(sizeof(graphics::VertexPosNormalUV) == 32, "VertexPosNormalUV must be 8 floats");
	graphics::MeshData data;
	data.vertices.resize((size_t)vertex_count);
	std::memcpy(data.vertices.data(), vertices, (size_t)vertex_count * sizeof(graphics::VertexPosNormalUV));
	data.indices.assign(indices, indices + index_count);
	auto& registry = graphics::ResourceRegistry::instance();
	const id::id_type id = registry.create_mesh(data, name ? std::string(name) : std::string("TerrainChunk"));
	if (id != id::invalid_id && bounds_min && bounds_max)
	{
		if (graphics::Mesh* mesh = registry.get_mesh(id))
			mesh->set_bounds(bounds_min[0], bounds_min[1], bounds_min[2], bounds_max[0], bounds_max[1], bounds_max[2]);
	}
	return id;
}

// width x height RGBA8 pixels, row-major from the top. srgb != 0 stores them as sRGB (the engine's shaders convert
// albedo themselves, so splat maps and atlases pass 0); mips != 0 builds the mip chain. id::invalid_id on failure.
EDITOR_INTERFACE id::id_type CreateTexturePixels(int32_t width, int32_t height, int32_t srgb, int32_t mips, const unsigned char* rgba)
{
	if (width <= 0 || height <= 0 || !rgba) return id::invalid_id;
	graphics::TextureDesc desc;
	desc.width = (u32)width;
	desc.height = (u32)height;
	desc.format = srgb != 0 ? graphics::TextureFormat::RGBA8_SRGB : graphics::TextureFormat::RGBA8_UNORM;
	desc.generate_mips = mips != 0;
	return graphics::ResourceRegistry::instance().create_texture(desc, rgba);
}

// Decode an encoded image (PNG / JPG / TGA / BMP bytes — a file read by the caller, so pak-only textures work too)
// into RGBA8. Writes width / height and up to `cap` bytes of pixels; returns the number of pixel bytes the image
// needs (width * height * 4). A result larger than `cap` means nothing was written — call again with a larger
// buffer. 0 = the image could not be decoded.
EDITOR_INTERFACE int32_t DecodeImageMemory(const unsigned char* data, int32_t length, int32_t* out_width, int32_t* out_height,
	unsigned char* out_rgba, int32_t cap)
{
	if (!data || length <= 0) return 0;
	graphics::ImageData image = graphics::TextureImporter::import_from_memory(reinterpret_cast<const u8*>(data), (u64)length);
	if (!image.is_valid() || image.format == graphics::ImageFormat::RGBA16F) return 0;   // 8-bit pictures only here
	const int32_t w = (int32_t)image.width, h = (int32_t)image.height;
	const int64_t needed64 = (int64_t)w * (int64_t)h * 4;
	if (needed64 <= 0 || needed64 > 0x7fffffff) return 0;
	const int32_t needed = (int32_t)needed64;
	if (out_width) *out_width = w;
	if (out_height) *out_height = h;
	if (!out_rgba || cap < needed) return needed;

	const u32 channels = image.channels != 0 ? image.channels : 4;
	const u8* src = image.pixels.data();
	const size_t count = (size_t)w * (size_t)h;
	if (channels == 4)
	{
		std::memcpy(out_rgba, src, (size_t)needed);
	}
	else
	{
		for (size_t i = 0; i < count; ++i)
		{
			unsigned char* o = out_rgba + i * 4;
			const u8* s = src + i * channels;
			if (channels == 1)      { o[0] = o[1] = o[2] = s[0]; o[3] = 255; }
			else if (channels == 2) { o[0] = o[1] = o[2] = s[0]; o[3] = s[1]; }
			else                    { o[0] = s[0]; o[1] = s[1]; o[2] = s[2]; o[3] = 255; }
		}
	}
	return needed;
}

// A static Jolt HeightFieldShape body for a terrain: heights = sample_count x sample_count metres (row z, column x,
// the terrain's local corner at the body origin, +X / +Z across, one sample every cell_size metres). pos / quat place
// the terrain's local origin. Returns the body id (0 = failed / no physics in this build).
EDITOR_INTERFACE uint32_t PhysicsCreateHeightFieldBody(uint64_t entityId, const float* heights, int32_t sampleCount,
	const float* pos, const float* quat, float cellSize, float friction, float restitution, int32_t layer)
{
	return phys::create_heightfield_body(entityId, heights, sampleCount, pos, quat, cellSize, friction, restitution, layer);
}
