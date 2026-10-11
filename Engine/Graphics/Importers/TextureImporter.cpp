#include "TextureImporter.h"

#define STB_IMAGE_IMPLEMENTATION
#include "../../ThirdParty/stb_image.h"

#include <algorithm>

namespace vortex::graphics
{
	// A Radiance .hdr keeps its range: stb's 8-bit path would clamp the sun to 1 and gamma-encode the sky, so the sky pass
	// could never expose it like the lit world. Four half floats per texel (RGBA16F); stb gives linear floats.
	static u16 float_to_half(float f)
	{
		u32 x; memcpy(&x, &f, 4);
		const u32 sign = (x >> 16) & 0x8000u;
		const int exp = (int)((x >> 23) & 0xffu) - 127 + 15;
		u32 mant = x & 0x7fffffu;
		if (exp <= 0)
		{
			if (exp < -10) return (u16)sign;
			mant |= 0x800000u;
			const u32 shift = (u32)(14 - exp);
			return (u16)(sign | ((mant + (1u << (shift - 1))) >> shift));
		}
		if (exp >= 31) return (u16)(sign | 0x7c00u);   // overflow -> inf (the sun of a 16-bit sky is clamped by the shader)
		return (u16)(sign | ((u32)exp << 10) | ((mant + 0x1000u) >> 13));
	}

	static ImageData hdr_to_half(const float* f, int width, int height)
	{
		ImageData r;
		r.width = (u32)width; r.height = (u32)height; r.channels = 4; r.format = ImageFormat::RGBA16F;
		const size_t count = (size_t)width * (size_t)height * 4;
		r.pixels.resize(count * 2);
		u16* out = reinterpret_cast<u16*>(r.pixels.data());
		for (size_t i = 0; i < count; ++i)
		{
			float v = f[i];
			if (!(v == v)) v = 0.0f;                 // NaN
			if (v > 65000.0f) v = 65000.0f;          // keep it finite in half
			if ((i & 3) == 3) v = 1.0f;              // opaque alpha
			out[i] = float_to_half(v);
		}
		return r;
	}

	ImageData TextureImporter::import_from_file(const std::string& filepath, bool flip_vertically)
	{
	ImageData result;
	if (stbi_is_hdr(filepath.c_str()))
	{
		stbi_set_flip_vertically_on_load_thread(flip_vertically ? 1 : 0);
		int hw, hh, hc;
		float* hf = stbi_loadf(filepath.c_str(), &hw, &hh, &hc, 4);
		if (hf) { result = hdr_to_half(hf, hw, hh); stbi_image_free(hf); return result; }
	}

	// Don't flip here - Assimp already handles UV flipping with aiProcess_FlipUVs
	// Setting flip_vertically=false by default to avoid double-flip issues
	stbi_set_flip_vertically_on_load_thread(flip_vertically ? 1 : 0);   // per thread: models decode maps in parallel

	int width, height, channels;
	// Request 4 channels (RGBA) to ensure consistent format for GPU upload
	unsigned char* data = stbi_load(filepath.c_str(), &width, &height, &channels, 4);

		if (!data)
		{
		return result; // Return empty on error
		}

		result.width = static_cast<u32>(width);
		result.height = static_cast<u32>(height);
		result.channels = 4; // Always 4 channels since we requested RGBA
		result.format = ImageFormat::RGBA8;

		// Copy data into vector - always RGBA now
		size_t data_size = width * height * 4;
		result.pixels.resize(data_size);
		memcpy(result.pixels.data(), data, data_size);

		// Free stb_image data
		stbi_image_free(data);

		return result;
	}

	ImageData TextureImporter::import_from_memory(const u8* data, u64 length, bool flip_vertically)
	{
		ImageData result;
		if (!data || length == 0) return result;
		if (stbi_is_hdr_from_memory(data, static_cast<int>(length)))
		{
			stbi_set_flip_vertically_on_load_thread(flip_vertically ? 1 : 0);
			int hw, hh, hc;
			float* hf = stbi_loadf_from_memory(data, static_cast<int>(length), &hw, &hh, &hc, 4);
			if (hf) { result = hdr_to_half(hf, hw, hh); stbi_image_free(hf); return result; }
		}

		stbi_set_flip_vertically_on_load_thread(flip_vertically ? 1 : 0);   // per thread: models decode maps in parallel

		int width, height, channels;
		unsigned char* pixels = stbi_load_from_memory(data, static_cast<int>(length), &width, &height, &channels, 4);
		if (!pixels)
			return result;

		result.width = static_cast<u32>(width);
		result.height = static_cast<u32>(height);
		result.channels = 4;
		result.format = ImageFormat::RGBA8;

		size_t data_size = static_cast<size_t>(width) * height * 4;
		result.pixels.resize(data_size);
		memcpy(result.pixels.data(), pixels, data_size);

		stbi_image_free(pixels);
		return result;
	}

	bool TextureImporter::is_format_supported(const std::string& extension)
	{
		static const std::vector<std::string> supported = {
			".png", ".jpg", ".jpeg", ".tga", ".bmp", ".psd", ".hdr"
		};

		std::string ext_lower = extension;
		std::transform(ext_lower.begin(), ext_lower.end(), ext_lower.begin(), ::tolower);

		return std::find(supported.begin(), supported.end(), ext_lower) != supported.end();
	}

	ImageFormat TextureImporter::determine_format(u32 channels)
	{
		switch (channels)
		{
		case 1: return ImageFormat::R8;
		case 2: return ImageFormat::RG8;
		case 3: return ImageFormat::RGB8;
		case 4: return ImageFormat::RGBA8;
		default: return ImageFormat::Unknown;
		}
	}
}
