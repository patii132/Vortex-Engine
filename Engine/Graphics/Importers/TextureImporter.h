#pragma once

#include "../../Common/CommonHeaders.h"
#include <string>
#include <vector>

namespace vortex::graphics
{
	enum class ImageFormat
	{
		R8,
		RG8,
		RGB8,
		RGBA8,
		RGBA16F,   // a Radiance .hdr: four half floats per texel (linear radiance, not clamped)
		Unknown
	};

	struct ImageData
	{
		std::vector<u8> pixels;
		u32 width{ 0 };
		u32 height{ 0 };
		u32 channels{ 0 };
		ImageFormat format{ ImageFormat::Unknown };

		bool is_valid() const { return !pixels.empty() && width > 0 && height > 0; }
		void clear() { pixels.clear(); width = 0; height = 0; channels = 0; }
	};

	/// <summary>
	/// Imports textures from various image formats.
	/// Uses stb_image for PNG, JPG, TGA, BMP support.
	/// </summary>
	class TextureImporter
	{
	public:
		TextureImporter() = default;
		~TextureImporter() = default;

		/// <summary>
		/// Import an image from file.
		/// Note: flip_vertically defaults to false because Assimp already flips UVs with aiProcess_FlipUVs
		/// </summary>
		static ImageData import_from_file(const std::string& filepath, bool flip_vertically = false);

		/// <summary>
		/// Import an image from an in-memory buffer (for packed/encrypted asset paks loaded into RAM).
		/// </summary>
		static ImageData import_from_memory(const u8* data, u64 length, bool flip_vertically = false);

		/// <summary>
		/// Check if a file format is supported.
		/// </summary>
		static bool is_format_supported(const std::string& extension);

	private:
		static ImageFormat determine_format(u32 channels);
	};
}
