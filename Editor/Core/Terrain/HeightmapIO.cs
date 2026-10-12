using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Editor.Core.Terrain
{
    /// <summary>
    /// A heightmap picture in memory: width × height samples normalised to 0..1 (row-major, row 0 at the top of the picture).
    /// </summary>
    public sealed class HeightmapImage
    {
        public int Width, Height;
        /// <summary>Height samples 0..1, index y * Width + x.</summary>
        public float[] Samples;
        /// <summary>16 for a 16-bit source (65536 steps), 8 for an 8-bit picture, 32 for float data.</summary>
        public int BitDepth;
        public string Note;

        public float At(int x, int y)
        {
            if (x < 0) x = 0; else if (x >= Width) x = Width - 1;
            if (y < 0) y = 0; else if (y >= Height) y = Height - 1;
            return Samples[y * Width + x];
        }

        /// <summary>Bilinear sample at picture coordinates (0..Width-1, 0..Height-1).</summary>
        public float Sample(float x, float y)
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            float fx = x - x0, fy = y - y0;
            float a = At(x0, y0), b = At(x0 + 1, y0), c = At(x0, y0 + 1), d = At(x0 + 1, y0 + 1);
            return (a + (b - a) * fx) + ((c + (d - c) * fx) - (a + (b - a) * fx)) * fy;
        }
    }

    /// <summary>
    /// Heightmaps in and out of a <see cref="TerrainData"/> (#124): import a picture — PNG 8 / 16 bit grey or colour (the
    /// luminance), or a RAW 16-bit little-endian square (World Machine / Gaea / Unity "R16") — stretched between a low and a
    /// high metre value; export the heights as a 16-bit grey PNG or RAW 16 for other tools; and a small shaded picture of the
    /// heights for thumbnails and the inspector. Self-contained: the PNG reader / writer here (deflate via the runtime)
    /// keeps 16-bit precision, which the UI image decoders drop.
    /// </summary>
    public static class HeightmapIO
    {
        public static readonly string[] Extensions = { ".png", ".raw", ".r16" };

        // ================================================================== import

        /// <summary>Load a heightmap picture. RAW needs a square 16-bit file (its side is the square root of the sample count).</summary>
        public static HeightmapImage Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) throw new FileNotFoundException("No such heightmap", path);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            var bytes = File.ReadAllBytes(path);
            if (ext == ".png") return DecodePng(bytes);
            if (ext == ".raw" || ext == ".r16") return DecodeRaw16(bytes);
            throw new NotSupportedException("Heightmaps are PNG (8 / 16 bit) or RAW 16-bit files");
        }

        /// <summary>Write the picture's heights into the terrain: the picture is resampled to the terrain's resolution,
        /// 0 → <paramref name="lowMetres"/>, 1 → <paramref name="highMetres"/>. Returns the touched rectangle (everything).</summary>
        public static SampleRect Apply(TerrainData data, HeightmapImage img, float lowMetres, float highMetres, bool flipY = false)
        {
            if (data == null || img == null) throw new ArgumentNullException();
            int n = data.Resolution;
            float sx = (img.Width - 1) / (float)(n - 1), sy = (img.Height - 1) / (float)(n - 1);
            for (int z = 0; z < n; z++)
            {
                float py = (flipY ? (n - 1 - z) : z) * sy;
                for (int x = 0; x < n; x++)
                {
                    float v = img.Sample(x * sx, py);
                    data.Heights[z * n + x] = lowMetres + (highMetres - lowMetres) * v;
                }
            }
            data.MarkChanged();
            return new SampleRect { X0 = 0, Z0 = 0, X1 = n - 1, Z1 = n - 1 };
        }

        // ================================================================== export

        /// <summary>The terrain's heights as a 16-bit grey PNG (lowest height = 0, highest = 65535; the range is reported).</summary>
        public static byte[] ToPng16(TerrainData data, out float low, out float high)
        {
            int n = data.Resolution;
            low = data.MinHeight; high = data.MaxHeight;
            float span = high - low; if (span < 1e-6f) span = 1f;
            var raw = new byte[n * (1 + n * 2)];
            int o = 0;
            for (int z = 0; z < n; z++)
            {
                raw[o++] = 0;   // filter: none
                for (int x = 0; x < n; x++)
                {
                    int v = (int)Math.Round((data.Heights[z * n + x] - low) / span * 65535f);
                    if (v < 0) v = 0; else if (v > 65535) v = 65535;
                    raw[o++] = (byte)(v >> 8); raw[o++] = (byte)v;
                }
            }
            return EncodePng(n, n, 16, raw);
        }

        /// <summary>The terrain's heights as RAW 16-bit little-endian (resolution² samples, lowest = 0, highest = 65535).</summary>
        public static byte[] ToRaw16(TerrainData data, out float low, out float high)
        {
            int n = data.Resolution;
            low = data.MinHeight; high = data.MaxHeight;
            float span = high - low; if (span < 1e-6f) span = 1f;
            var raw = new byte[n * n * 2];
            for (int i = 0; i < n * n; i++)
            {
                int v = (int)Math.Round((data.Heights[i] - low) / span * 65535f);
                if (v < 0) v = 0; else if (v > 65535) v = 65535;
                raw[i * 2] = (byte)v; raw[i * 2 + 1] = (byte)(v >> 8);
            }
            return raw;
        }

        // ================================================================== pictures of the heights

        /// <summary>A shaded relief of the terrain as BGRA pixels (size × size): height as brightness with a light from the
        /// north-west, the splat's dominant layer tinting nothing — a neutral grey map that reads at thumbnail size.</summary>
        public static byte[] ShadedRelief(TerrainData data, int size, float cellSize, out int stride)
        {
            stride = size * 4;
            var px = new byte[size * stride];
            int n = data.Resolution;
            float low = data.MinHeight, high = data.MaxHeight, span = Math.Max(1e-3f, high - low);
            float scale = (n - 1) / (float)Math.Max(1, size - 1);
            float step = Math.Max(1f, scale);   // samples per pixel, for the slope
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float sx = x * scale, sz = y * scale;
                    float h = data.SampleHeight(sx, sz);
                    float hx = data.SampleHeight(Math.Min(n - 1, sx + step), sz) - data.SampleHeight(Math.Max(0, sx - step), sz);
                    float hz = data.SampleHeight(sx, Math.Min(n - 1, sz + step)) - data.SampleHeight(sx, Math.Max(0, sz - step));
                    float run = 2f * step * Math.Max(1e-3f, cellSize);
                    // normal of the slope, lit from the north-west and above
                    float nx = -hx / run, nz = -hz / run, ny = 1f;
                    float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz); nx /= len; ny /= len; nz /= len;
                    float light = Math.Max(0f, nx * -0.5f + ny * 0.7f + nz * -0.5f);
                    float tone = 0.25f + 0.55f * ((h - low) / span) ;
                    float v = tone * (0.45f + 0.75f * light);
                    byte b = (byte)Math.Max(0, Math.Min(255, (int)(v * 255f)));
                    int o = y * stride + x * 4;
                    px[o] = (byte)(b * 0.92f); px[o + 1] = (byte)(b * 0.97f); px[o + 2] = b; px[o + 3] = 255;
                }
            }
            return px;
        }

        /// <summary>One line of facts for tooltips: "513 × 513 · heights −6.2 … 31.4 m".</summary>
        public static string Describe(TerrainData data)
        {
            if (data == null) return "no terrain data";
            return data.Resolution + " × " + data.Resolution + " samples · heights " + data.MinHeight.ToString("0.#") + " … " + data.MaxHeight.ToString("0.#") + " m";
        }

        // ================================================================== PNG

        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        /// <summary>A PNG reader for heightmaps: colour types 0 (grey), 2 (RGB), 4 (grey+alpha), 6 (RGBA), bit depths 8 and 16
        /// (1/2/4-bit grey too), non-interlaced. Colour pictures use the luminance.</summary>
        public static HeightmapImage DecodePng(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 33) throw new InvalidDataException("not a PNG");
            for (int i = 0; i < 8; i++) if (bytes[i] != PngSignature[i]) throw new InvalidDataException("not a PNG");
            int pos = 8, width = 0, height = 0, depth = 0, colour = 0, interlace = 0;
            var idat = new MemoryStream();
            while (pos + 8 <= bytes.Length)
            {
                int len = ReadBE(bytes, pos); string type = Encoding.ASCII.GetString(bytes, pos + 4, 4);
                int dataAt = pos + 8;
                if (dataAt + len > bytes.Length) throw new InvalidDataException("truncated PNG");
                if (type == "IHDR")
                {
                    width = ReadBE(bytes, dataAt); height = ReadBE(bytes, dataAt + 4);
                    depth = bytes[dataAt + 8]; colour = bytes[dataAt + 9]; interlace = bytes[dataAt + 12];
                }
                else if (type == "IDAT") idat.Write(bytes, dataAt, len);
                else if (type == "IEND") break;
                pos = dataAt + len + 4;
            }
            if (width <= 0 || height <= 0) throw new InvalidDataException("PNG without a header");
            if (interlace != 0) throw new NotSupportedException("interlaced PNGs are not supported — save the heightmap without interlacing");
            if (colour == 3) throw new NotSupportedException("palette PNGs are not heightmaps — save as grey or RGB");
            int channels = colour == 0 ? 1 : colour == 2 ? 3 : colour == 4 ? 2 : colour == 6 ? 4 : 0;
            if (channels == 0) throw new NotSupportedException("unknown PNG colour type " + colour);
            if (depth != 8 && depth != 16 && !(colour == 0 && (depth == 1 || depth == 2 || depth == 4))) throw new NotSupportedException("PNG bit depth " + depth);
            int bitsPerPixel = channels * depth;
            int stride = (width * bitsPerPixel + 7) / 8;
            int bpp = Math.Max(1, bitsPerPixel / 8);   // filter byte distance
            var raw = Inflate(idat.ToArray(), (stride + 1) * height);
            var img = new HeightmapImage { Width = width, Height = height, Samples = new float[width * height], BitDepth = depth, Note = channels == 1 ? "grey" : "colour (luminance)" };
            var prev = new byte[stride]; var line = new byte[stride];
            float max = (1 << depth) - 1;
            for (int y = 0; y < height; y++)
            {
                int o = y * (stride + 1);
                int filter = raw[o];
                Array.Copy(raw, o + 1, line, 0, stride);
                Unfilter(filter, line, prev, bpp);
                for (int x = 0; x < width; x++)
                {
                    float v;
                    if (depth == 16)
                    {
                        int i = x * channels * 2;
                        if (channels >= 3) v = (0.2126f * ((line[i] << 8) | line[i + 1]) + 0.7152f * ((line[i + 2] << 8) | line[i + 3]) + 0.0722f * ((line[i + 4] << 8) | line[i + 5])) / 65535f;
                        else v = ((line[i] << 8) | line[i + 1]) / 65535f;
                    }
                    else if (depth == 8)
                    {
                        int i = x * channels;
                        if (channels >= 3) v = (0.2126f * line[i] + 0.7152f * line[i + 1] + 0.0722f * line[i + 2]) / 255f;
                        else v = line[i] / 255f;
                    }
                    else
                    {
                        int bit = x * depth; int b = line[bit >> 3]; int shift = 8 - depth - (bit & 7);
                        v = ((b >> shift) & ((1 << depth) - 1)) / max;
                    }
                    img.Samples[y * width + x] = v;
                }
                var t = prev; prev = line; line = t;
            }
            return img;
        }

        private static void Unfilter(int filter, byte[] line, byte[] prev, int bpp)
        {
            int n = line.Length;
            switch (filter)
            {
                case 0: break;
                case 1: for (int i = bpp; i < n; i++) line[i] = (byte)(line[i] + line[i - bpp]); break;
                case 2: for (int i = 0; i < n; i++) line[i] = (byte)(line[i] + prev[i]); break;
                case 3: for (int i = 0; i < n; i++) line[i] = (byte)(line[i] + (((i >= bpp ? line[i - bpp] : 0) + prev[i]) >> 1)); break;
                case 4:
                    for (int i = 0; i < n; i++)
                    {
                        int a = i >= bpp ? line[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        line[i] = (byte)(line[i] + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c));
                    }
                    break;
                default: throw new InvalidDataException("PNG filter " + filter);
            }
        }

        private static byte[] Inflate(byte[] zlib, int expected)
        {
            using (var ms = new MemoryStream(zlib))
            using (var z = new ZLibStream(ms, CompressionMode.Decompress))
            {
                var outp = new byte[expected];
                int got = 0;
                while (got < expected) { int r = z.Read(outp, got, expected - got); if (r <= 0) break; got += r; }
                if (got < expected) throw new InvalidDataException("PNG image data is short (" + got + " of " + expected + " bytes)");
                return outp;
            }
        }

        /// <summary>Encode filtered scanlines (each row prefixed by its filter byte) as a grey PNG of the given bit depth.</summary>
        public static byte[] EncodePng(int width, int height, int depth, byte[] filteredRows)
        {
            using (var ms = new MemoryStream())
            {
                ms.Write(PngSignature, 0, 8);
                var ihdr = new byte[13];
                WriteBE(ihdr, 0, width); WriteBE(ihdr, 4, height);
                ihdr[8] = (byte)depth; ihdr[9] = 0; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
                Chunk(ms, "IHDR", ihdr);
                byte[] z;
                using (var zm = new MemoryStream())
                {
                    using (var zs = new ZLibStream(zm, CompressionLevel.Optimal, true)) zs.Write(filteredRows, 0, filteredRows.Length);
                    z = zm.ToArray();
                }
                Chunk(ms, "IDAT", z);
                Chunk(ms, "IEND", Array.Empty<byte>());
                return ms.ToArray();
            }
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; WriteBE(len, 0, data.Length); s.Write(len, 0, 4);
            var t = Encoding.ASCII.GetBytes(type); s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Crc32(t, 0, 4, 0xFFFFFFFFu); crc = Crc32(data, 0, data.Length, crc) ^ 0xFFFFFFFFu;
            var c = new byte[4]; WriteBE(c, 0, (int)crc); s.Write(c, 0, 4);
        }

        private static uint[] _crcTable;
        private static uint Crc32(byte[] buf, int off, int len, uint crc)
        {
            if (_crcTable == null)
            {
                var t = new uint[256];
                for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
                _crcTable = t;
            }
            for (int i = 0; i < len; i++) crc = _crcTable[(crc ^ buf[off + i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static int ReadBE(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        private static void WriteBE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        // ================================================================== RAW 16

        private static HeightmapImage DecodeRaw16(byte[] bytes)
        {
            int count = bytes.Length / 2;
            int side = (int)Math.Round(Math.Sqrt(count));
            if (side < 2 || side * side != count) throw new InvalidDataException("a RAW heightmap must be a square of 16-bit samples (" + bytes.Length + " bytes is not)");
            var img = new HeightmapImage { Width = side, Height = side, Samples = new float[count], BitDepth = 16, Note = "raw 16-bit" };
            for (int i = 0; i < count; i++) img.Samples[i] = (bytes[i * 2] | (bytes[i * 2 + 1] << 8)) / 65535f;
            return img;
        }
    }
}
