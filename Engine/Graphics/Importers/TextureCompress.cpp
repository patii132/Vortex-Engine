#include "TextureCompress.h"

#define STB_DXT_IMPLEMENTATION
#define STB_DXT_STATIC
#include "../../ThirdParty/stb_dxt.h"

#include <algorithm>
#include <cctype>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <future>
#include <thread>

namespace vortex::graphics
{
	namespace
	{
		bool g_enabled = []()
		{
			const char* e = std::getenv("VORTEX_TEXTURE_COMPRESSION");
			return !(e && e[0] == '0');
		}();
		u32 g_min_size = 256;
		std::string g_cache_dir;
		constexpr u32 kCacheVersion = 1;

		u64 fnv1a(const std::string& s)
		{
			u64 h = 1469598103934665603ull;
			for (unsigned char c : s) { h ^= c; h *= 1099511628211ull; }
			return h;
		}

		std::string lower(std::string s)
		{
			for (auto& c : s) c = (char)std::tolower((unsigned char)c);
			return s;
		}

		// the 4x4 block at (bx, by) of a level, edge texels repeated past the border
		inline void gather_block(const u8* px, u32 w, u32 h, u32 bx, u32 by, u32 channels, u8* out)
		{
			for (u32 y = 0; y < 4; ++y)
			{
				u32 sy = by * 4 + y; if (sy >= h) sy = h - 1;
				for (u32 x = 0; x < 4; ++x)
				{
					u32 sx = bx * 4 + x; if (sx >= w) sx = w - 1;
					const u8* s = px + ((size_t)sy * w + sx) * 4;
					u8* d = out + ((size_t)y * 4 + x) * channels;
					for (u32 c = 0; c < channels; ++c) d[c] = s[c];
				}
			}
		}

		void encode_level(const u8* rgba, u32 w, u32 h, BlockFormat f, u8* dst)
		{
			const u32 bw = (w + 3) / 4, bh = (h + 3) / 4;
			const u32 bb = TextureCompress::block_bytes(f);
			auto rows = [&](u32 y0, u32 y1)
			{
				u8 block[16 * 4];
				for (u32 by = y0; by < y1; ++by)
					for (u32 bx = 0; bx < bw; ++bx)
					{
						u8* d = dst + ((size_t)by * bw + bx) * bb;
						switch (f)
						{
						case BlockFormat::BC1: gather_block(rgba, w, h, bx, by, 4, block); stb_compress_dxt_block(d, block, 0, STB_DXT_NORMAL); break;
						case BlockFormat::BC3: gather_block(rgba, w, h, bx, by, 4, block); stb_compress_dxt_block(d, block, 1, STB_DXT_NORMAL); break;
						case BlockFormat::BC4: gather_block(rgba, w, h, bx, by, 1, block); stb_compress_bc4_block(d, block); break;
						case BlockFormat::BC5: gather_block(rgba, w, h, bx, by, 2, block); stb_compress_bc5_block(d, block); break;
						}
					}
			};
			const u32 lanes = (std::max)(1u, (std::min)(16u, std::thread::hardware_concurrency()));
			if (bh < 16 || lanes == 1) { rows(0, bh); return; }
			std::vector<std::future<void>> jobs;
			const u32 per = (bh + lanes - 1) / lanes;
			for (u32 l = 0; l < lanes; ++l)
			{
				const u32 y0 = l * per, y1 = (std::min)(bh, y0 + per);
				if (y0 >= y1) break;
				jobs.push_back(std::async(std::launch::async, rows, y0, y1));
			}
			for (auto& j : jobs) j.get();
		}
	}

	u64 CompressedTexture::level_size(u32 mip) const { return TextureCompress::level_size(format, level_width(mip), level_height(mip)); }

	bool TextureCompress::enabled() { return g_enabled; }
	void TextureCompress::set_enabled(bool on) { g_enabled = on; }
	u32 TextureCompress::min_size() { return g_min_size; }
	void TextureCompress::set_min_size(u32 size) { g_min_size = size < 4 ? 4 : size; }
	void TextureCompress::set_cache_dir(const std::string& dir) { g_cache_dir = dir; }
	const std::string& TextureCompress::cache_dir() { return g_cache_dir; }

	u64 TextureCompress::level_size(BlockFormat f, u32 w, u32 h)
	{
		return (u64)((w + 3) / 4) * (u64)((h + 3) / 4) * block_bytes(f);
	}

	bool TextureCompress::eligible(u32 width, u32 height)
	{
		return g_enabled && width >= g_min_size && height >= g_min_size && (width % 4) == 0 && (height % 4) == 0;
	}

	CompressKind TextureCompress::kind_for(const std::string& label, const u8* rgba, u32 width, u32 height)
	{
		const std::string l = lower(label);
		// the file name decides a normal map (Poly Haven: _nor_gl / _nor_dx, Blender: _normal, the usual _n suffix)
		if (l.find("_nor") != std::string::npos || l.find("normal") != std::string::npos || l.find("_nrm") != std::string::npos
			|| l.find("_n.") != std::string::npos || l.find("_n_") != std::string::npos)
			return CompressKind::Normal;
		if (rgba)
		{
			const size_t n = (size_t)width * height;
			for (size_t i = 0; i < n; i += 7)   // every 7th texel is plenty to notice a cut-out
				if (rgba[i * 4 + 3] < 250) return CompressKind::ColorAlpha;
		}
		return CompressKind::Color;
	}

	BlockFormat TextureCompress::format_for(CompressKind kind)
	{
		switch (kind)
		{
		case CompressKind::ColorAlpha: return BlockFormat::BC3;
		case CompressKind::Normal: return BlockFormat::BC5;
		case CompressKind::Gray: return BlockFormat::BC4;
		default: return BlockFormat::BC1;
		}
	}

	const char* TextureCompress::format_name(BlockFormat f)
	{
		switch (f) { case BlockFormat::BC1: return "BC1"; case BlockFormat::BC3: return "BC3"; case BlockFormat::BC4: return "BC4"; default: return "BC5"; }
	}

	bool TextureCompress::compress_rgba8(const u8* rgba, u32 width, u32 height, CompressKind kind, bool mips, CompressedTexture& out)
	{
		if (!rgba || width == 0 || height == 0 || kind == CompressKind::None) return false;
		if (kind == CompressKind::Auto) kind = kind_for("", rgba, width, height);
		out.format = format_for(kind);
		out.width = width; out.height = height;
		out.mip_count = 1;
		if (mips) { u32 dim = (std::max)(width, height); while (dim > 1) { dim >>= 1; ++out.mip_count; } }

		// the chain: a 2x2 box filter per level (floor sizes, as the GPU subresources)
		std::vector<std::vector<u8>> levels(out.mip_count);
		const u8* src = rgba;
		u32 sw = width, sh = height;
		u64 total = 0;
		out.level_offset.assign(out.mip_count, 0);
		for (u32 m = 0; m < out.mip_count; ++m)
		{
			if (m > 0)
			{
				const u32 dw = sw > 1 ? sw >> 1 : 1, dh = sh > 1 ? sh >> 1 : 1;
				std::vector<u8> dst((size_t)dw * dh * 4);
				for (u32 y = 0; y < dh; ++y)
				{
					const u32 sy0 = y * 2, sy1 = (sy0 + 1 < sh) ? sy0 + 1 : sy0;
					for (u32 x = 0; x < dw; ++x)
					{
						const u32 sx0 = x * 2, sx1 = (sx0 + 1 < sw) ? sx0 + 1 : sx0;
						const u8* p00 = src + ((size_t)sy0 * sw + sx0) * 4;
						const u8* p01 = src + ((size_t)sy0 * sw + sx1) * 4;
						const u8* p10 = src + ((size_t)sy1 * sw + sx0) * 4;
						const u8* p11 = src + ((size_t)sy1 * sw + sx1) * 4;
						u8* o = dst.data() + ((size_t)y * dw + x) * 4;
						for (int c = 0; c < 4; ++c) o[c] = (u8)((p00[c] + p01[c] + p10[c] + p11[c] + 2) >> 2);
					}
				}
				levels[m] = std::move(dst);
				src = levels[m].data(); sw = dw; sh = dh;
			}
			out.level_offset[m] = total;
			total += level_size(out.format, sw, sh);
		}
		out.data.resize(total);
		// encode (level 0 straight from the caller's pixels)
		sw = width; sh = height;
		for (u32 m = 0; m < out.mip_count; ++m)
		{
			const u8* px = m == 0 ? rgba : levels[m].data();
			encode_level(px, sw, sh, out.format, out.data.data() + out.level_offset[m]);
			sw = sw > 1 ? sw >> 1 : 1; sh = sh > 1 ? sh >> 1 : 1;
		}
		return true;
	}

	std::string TextureCompress::cache_key(const std::string& path, CompressKind kind)
	{
		std::error_code ec;
		const auto size = std::filesystem::file_size(path, ec);
		if (ec) return std::string();
		const auto mtime = std::filesystem::last_write_time(path, ec).time_since_epoch().count();
		char buf[64];
		snprintf(buf, sizeof(buf), "|%llu|%lld|%u|v%u", (unsigned long long)size, (long long)mtime, (unsigned)kind, kCacheVersion);
		snprintf(buf, sizeof(buf), "%016llx", (unsigned long long)fnv1a(path + buf));
		return std::string(buf);
	}

	bool TextureCompress::load_cached(const std::string& key, CompressedTexture& out)
	{
		if (g_cache_dir.empty() || key.empty()) return false;
		const std::string file = g_cache_dir + "/" + key + ".vtex";
		FILE* f = fopen(file.c_str(), "rb");
		if (!f) return false;
		struct Header { char magic[4]; u32 version, format, width, height, mips; u64 size; } h{};
		bool ok = fread(&h, sizeof(h), 1, f) == 1 && memcmp(h.magic, "VTX1", 4) == 0 && h.version == kCacheVersion
			&& h.format <= 3 && h.width > 0 && h.height > 0 && h.mips > 0 && h.mips < 32 && h.size > 0 && h.size < (1ull << 31);
		if (ok)
		{
			out.format = (BlockFormat)h.format; out.width = h.width; out.height = h.height; out.mip_count = h.mips;
			out.data.resize((size_t)h.size);
			ok = fread(out.data.data(), 1, (size_t)h.size, f) == (size_t)h.size;
			out.level_offset.assign(h.mips, 0);
			u64 off = 0;
			for (u32 m = 0; m < h.mips; ++m) { out.level_offset[m] = off; off += out.level_size(m); }
			if (off != h.size) ok = false;
		}
		fclose(f);
		return ok;
	}

	void TextureCompress::save_cached(const std::string& key, const CompressedTexture& t)
	{
		if (g_cache_dir.empty() || key.empty() || t.data.empty()) return;
		std::error_code ec;
		std::filesystem::create_directories(g_cache_dir, ec);
		const std::string file = g_cache_dir + "/" + key + ".vtex";
		const std::string tmp = file + ".tmp";
		FILE* f = fopen(tmp.c_str(), "wb");
		if (!f) return;
		struct Header { char magic[4]; u32 version, format, width, height, mips; u64 size; } h{};
		memcpy(h.magic, "VTX1", 4); h.version = kCacheVersion; h.format = (u32)t.format; h.width = t.width; h.height = t.height;
		h.mips = t.mip_count; h.size = (u64)t.data.size();
		const bool ok = fwrite(&h, sizeof(h), 1, f) == 1 && fwrite(t.data.data(), 1, t.data.size(), f) == t.data.size();
		fclose(f);
		if (ok) std::filesystem::rename(tmp, file, ec); else std::filesystem::remove(tmp, ec);
	}
}
