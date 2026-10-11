#pragma once

// Block compression for textures (#179): BC1 for colour, BC3 for colour + alpha, BC5 for normal maps (x / y, the shader
// rebuilds z), BC4 for single-channel maps — with the whole mip chain built on the CPU and encoded in parallel, and a
// disk cache so a 4K map decodes and encodes once per machine. A 4K RGBA8 map with mips is 85 MB of VRAM and bandwidth;
// the same map is 11 MB as BC1 / BC4, 22 MB as BC3 / BC5, and samples faster. Backend-independent: the registries map
// BlockFormat to their TextureFormat and upload the packed chain.
#include "../../Common/CommonHeaders.h"
#include <string>
#include <vector>

namespace vortex::graphics
{
	enum class BlockFormat : u32 { BC1 = 0, BC3 = 1, BC4 = 2, BC5 = 3 };

	// What a picture is used for — decides the block format. Auto = Normal by file name (…_nor…, …normal…, …_n.…),
	// else ColorAlpha when any alpha byte is below 250, else Color.
	enum class CompressKind : u32 { None = 0, Color = 1, ColorAlpha = 2, Normal = 3, Gray = 4, Auto = 5 };

	struct CompressedTexture
	{
		BlockFormat format{ BlockFormat::BC1 };
		u32 width{ 0 }, height{ 0 }, mip_count{ 0 };
		std::vector<u8> data;              // every level, tightly packed, level 0 first
		std::vector<u64> level_offset;     // mip_count entries into data
		u64 level_size(u32 mip) const;
		u32 level_width(u32 mip) const { u32 w = width >> mip; return w ? w : 1; }
		u32 level_height(u32 mip) const { u32 h = height >> mip; return h ? h : 1; }
	};

	class TextureCompress
	{
	public:
		// Policy: on by default (VORTEX_TEXTURE_COMPRESSION=0 turns it off), pictures below min_size on either side stay
		// RGBA8 (icons, LUTs, tiny masks). Both sides must be multiples of 4.
		static bool enabled();
		static void set_enabled(bool on);
		static u32 min_size();
		static void set_min_size(u32 size);
		static void set_cache_dir(const std::string& dir);
		static const std::string& cache_dir();

		static u32 block_bytes(BlockFormat f) { return (f == BlockFormat::BC1 || f == BlockFormat::BC4) ? 8u : 16u; }
		static u64 level_size(BlockFormat f, u32 w, u32 h);
		static bool eligible(u32 width, u32 height);
		static CompressKind kind_for(const std::string& label, const u8* rgba, u32 width, u32 height);
		static BlockFormat format_for(CompressKind kind);

		// RGBA8 (4 bytes per pixel, rows from the top) -> the packed BC chain (mips: the full chain down to 1x1).
		static bool compress_rgba8(const u8* rgba, u32 width, u32 height, CompressKind kind, bool mips, CompressedTexture& out);

		// The disk cache: key = the file's path + size + modification time + kind + the encoder version.
		static std::string cache_key(const std::string& path, CompressKind kind);
		static bool load_cached(const std::string& key, CompressedTexture& out);
		static void save_cached(const std::string& key, const CompressedTexture& t);

		static const char* format_name(BlockFormat f);
	};
}
