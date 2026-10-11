using System;
using System.Collections.Generic;
using System.IO;

namespace Editor.Core.Services
{
    /// <summary>
    /// The sky as a light (image-based lighting, first step): the irradiance of an equirectangular HDR — or of a
    /// three-colour gradient — projected onto 9 spherical-harmonic coefficients per colour channel, so every surface's
    /// ambient term knows where the sky is blue and where the ground bounces warm light. The coefficients are normalised so
    /// their average radiance equals the renderer's old hemisphere level (0.5): the Skybox's ambient strength keeps setting
    /// the brightness, the picture sets colour and direction. The sun is clamped out (it is the directional light's job).
    /// </summary>
    public static class SkyLight
    {
        /// <summary>SH9 per channel: 27 floats, coefficient-major (c0.rgb, c1.rgb, … c8.rgb).</summary>
        public sealed class Coefficients
        {
            public readonly float[] Sh = new float[27];
            public string Key;
        }

        private static readonly Dictionary<string, Coefficients> _cache = new Dictionary<string, Coefficients>();
        private const int Width = 256, Height = 128;

        /// <summary>The sky light of an equirect picture (.hdr read as float, anything else through 8-bit sRGB), cached by path.</summary>
        public static Coefficients FromTexture(string fullPath, float exposure, float rotationDeg)
        {
            string key = "tex|" + fullPath + "|" + (File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath).Ticks : 0) + "|" + rotationDeg.ToString("0.0");
            Coefficients c;
            if (!_cache.TryGetValue(key, out c))
            {
                float[] rgb; int w, h;
                if (!LoadEquirect(fullPath, out rgb, out w, out h)) return null;
                c = Project(rgb, w, h, rotationDeg);
                c.Key = key;
                _cache[key] = c;
            }
            return c;   // exposure is applied by the renderer's ambient strength path (the picture is normalised anyway)
        }

        /// <summary>The sky light of a gradient sky (top / horizon / bottom), cached by its colours.</summary>
        public static Coefficients FromGradient(float tr, float tg, float tb, float hr, float hg, float hb, float br, float bg, float bb)
        {
            string key = "grad|" + string.Join(",", new[] { tr, tg, tb, hr, hg, hb, br, bg, bb }.Select3());
            Coefficients c;
            if (_cache.TryGetValue(key, out c)) return c;
            int w = 64, h = 32;
            var rgb = new float[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                float v = (y + 0.5f) / h;               // 0 = zenith, 1 = nadir
                float dy = 1f - 2f * v;                 // direction.y
                float r, g, b;
                if (dy >= 0f) { float t = (float)Math.Pow(dy, 0.6); r = hr + (tr - hr) * t; g = hg + (tg - hg) * t; b = hb + (tb - hb) * t; }
                else { float t = (float)Math.Pow(-dy, 0.6); r = hr + (br - hr) * t; g = hg + (bg - hg) * t; b = hb + (bb - hb) * t; }
                for (int x = 0; x < w; x++) { int o = (y * w + x) * 3; rgb[o] = r; rgb[o + 1] = g; rgb[o + 2] = b; }
            }
            c = Project(rgb, w, h, 0f);
            c.Key = key;
            _cache[key] = c;
            return c;
        }

        private static string Select3(this float[] a) { var s = new string[a.Length]; for (int i = 0; i < a.Length; i++) s[i] = a[i].ToString("0.000", System.Globalization.CultureInfo.InvariantCulture); return string.Join(",", s); }

        // ---------------------------------------------------------------- projection

        /// <summary>Project a linear equirect (w × h × rgb) onto SH9 with the cosine-lobe weights (Ramamoorthi &amp; Hanrahan),
        /// the brightest texels clamped (the sun), the result normalised to an average radiance of 0.5.</summary>
        public static Coefficients Project(float[] rgb, int w, int h, float rotationDeg)
        {
            // downsample to a small working size first (the SH only needs the low frequencies)
            int dw = Math.Min(w, Width), dh = Math.Min(h, Height);
            var small = new float[dw * dh * 3];
            int bx = Math.Max(1, w / dw), by = Math.Max(1, h / dh);
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    float r = 0, g = 0, b = 0; int n = 0;
                    for (int yy = 0; yy < by; yy++)
                        for (int xx = 0; xx < bx; xx++)
                        {
                            int sx = Math.Min(w - 1, x * bx + xx), sy = Math.Min(h - 1, y * by + yy);
                            int o = (sy * w + sx) * 3; r += rgb[o]; g += rgb[o + 1]; b += rgb[o + 2]; n++;
                        }
                    int d = (y * dw + x) * 3; small[d] = r / n; small[d + 1] = g / n; small[d + 2] = b / n;
                }
            // the sun: clamp every texel to 6× the mean luminance (the directional light carries the sun)
            double mean = 0; int cnt = 0;
            for (int i = 0; i < small.Length; i += 3) { mean += 0.2126 * small[i] + 0.7152 * small[i + 1] + 0.0722 * small[i + 2]; cnt++; }
            mean = Math.Max(1e-6, mean / Math.Max(1, cnt));
            float clampLum = (float)(mean * 6.0);
            for (int i = 0; i < small.Length; i += 3)
            {
                float lum = 0.2126f * small[i] + 0.7152f * small[i + 1] + 0.0722f * small[i + 2];
                if (lum > clampLum) { float s = clampLum / lum; small[i] *= s; small[i + 1] *= s; small[i + 2] *= s; }
            }
            // project
            var sh = new double[27];
            double rot = rotationDeg * Math.PI / 180.0;
            double totalW = 0;
            for (int y = 0; y < dh; y++)
            {
                double theta = Math.PI * (y + 0.5) / dh;             // 0 = zenith
                double sinT = Math.Sin(theta), cosT = Math.Cos(theta);
                double dOmega = (2.0 * Math.PI / dw) * (Math.PI / dh) * sinT;
                for (int x = 0; x < dw; x++)
                {
                    // the engine's mapping: u = atan2(dir.x, dir.z) / 2π + 0.5 (+ rotation) → dir.x = sin(phi), dir.z = cos(phi)
                    double phi = ((x + 0.5) / dw - 0.5) * 2.0 * Math.PI - rot;
                    double dx = sinT * Math.Sin(phi), dy = cosT, dz = sinT * Math.Cos(phi);
                    int o = (y * dw + x) * 3;
                    double r = small[o], g = small[o + 1], b = small[o + 2];
                    double[] basis = Basis(dx, dy, dz);
                    for (int k = 0; k < 9; k++)
                    {
                        double wgt = basis[k] * dOmega;
                        sh[k * 3] += r * wgt; sh[k * 3 + 1] += g * wgt; sh[k * 3 + 2] += b * wgt;
                    }
                    totalW += dOmega;
                }
            }
            // normalise: the DC term's radiance (c0 · Y00 = average radiance) → 0.5
            double avg = (0.2126 * sh[0] + 0.7152 * sh[1] + 0.0722 * sh[2]) * 0.282095;
            double scale = avg > 1e-9 ? 0.5 / avg : 1.0;
            var c = new Coefficients();
            for (int i = 0; i < 27; i++) c.Sh[i] = (float)(sh[i] * scale);
            return c;
        }

        private static double[] Basis(double x, double y, double z)
        {
            return new[]
            {
                0.282095,
                0.488603 * y, 0.488603 * z, 0.488603 * x,
                1.092548 * x * y, 1.092548 * y * z, 0.315392 * (3.0 * z * z - 1.0), 1.092548 * x * z, 0.546274 * (x * x - y * y)
            };
        }

        // ---------------------------------------------------------------- the reflection map

        /// <summary>The sky as a reflection: a linear 8-bit RGBA equirect (width × width/2) of the picture or gradient,
        /// radiance soft-compressed into 0..1 (x / (1 + x) above 1 — the sun's glitter is the directional light's job), ready
        /// for a texture with a mip chain that the shaders sample by roughness. Cached by the same key as the irradiance.</summary>
        public sealed class ReflectionMap
        {
            public byte[] Rgba; public int Width, Height; public string Key;
        }

        private static readonly Dictionary<string, ReflectionMap> _maps = new Dictionary<string, ReflectionMap>();

        public static ReflectionMap ReflectionFromTexture(string fullPath, float exposure, int width = 512)
        {
            string key = "rtex|" + fullPath + "|" + (File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath).Ticks : 0) + "|" + exposure.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "|" + width;
            ReflectionMap m;
            if (_maps.TryGetValue(key, out m)) return m;
            float[] rgb; int w, h;
            if (!LoadEquirect(fullPath, out rgb, out w, out h)) return null;
            m = Encode(rgb, w, h, exposure, width); m.Key = key; _maps[key] = m;
            return m;
        }

        public static ReflectionMap ReflectionFromGradient(float tr, float tg, float tb, float hr, float hg, float hb, float br, float bg, float bb, int width = 128)
        {
            string key = "rgrad|" + new[] { tr, tg, tb, hr, hg, hb, br, bg, bb }.Select3() + "|" + width;
            ReflectionMap m;
            if (_maps.TryGetValue(key, out m)) return m;
            int w = width, h = width / 2;
            var rgb = new float[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                float dy = 1f - 2f * (y + 0.5f) / h; float r, g, b;
                if (dy >= 0f) { float t = (float)Math.Pow(dy, 0.6); r = hr + (tr - hr) * t; g = hg + (tg - hg) * t; b = hb + (tb - hb) * t; }
                else { float t = (float)Math.Pow(-dy, 0.6); r = hr + (br - hr) * t; g = hg + (bg - hg) * t; b = hb + (bb - hb) * t; }
                for (int x = 0; x < w; x++) { int o = (y * w + x) * 3; rgb[o] = r; rgb[o + 1] = g; rgb[o + 2] = b; }
            }
            m = Encode(rgb, w, h, 1f, width); m.Key = key; _maps[key] = m;
            return m;
        }

        private static ReflectionMap Encode(float[] rgb, int w, int h, float exposure, int width)
        {
            int dw = Math.Min(w, width), dh = Math.Max(1, dw / 2);
            var m = new ReflectionMap { Width = dw, Height = dh, Rgba = new byte[dw * dh * 4] };
            int bx = Math.Max(1, w / dw), by = Math.Max(1, h / dh);
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    float r = 0, g = 0, b = 0; int n = 0;
                    for (int yy = 0; yy < by; yy++)
                        for (int xx = 0; xx < bx; xx++)
                        {
                            int sx = Math.Min(w - 1, x * bx + xx), sy = Math.Min(h - 1, y * by + yy);
                            int o = (sy * w + sx) * 3; r += rgb[o]; g += rgb[o + 1]; b += rgb[o + 2]; n++;
                        }
                    r = r / n * exposure; g = g / n * exposure; b = b / n * exposure;
                    int d = (y * dw + x) * 4;
                    m.Rgba[d] = Compress(r); m.Rgba[d + 1] = Compress(g); m.Rgba[d + 2] = Compress(b); m.Rgba[d + 3] = 255;
                }
            return m;
        }

        // linear radiance -> 8 bits: straight below 1, x / (1 + x) rescaled above (the shaders read it back as linear-ish)
        private static byte Compress(float v)
        {
            if (v < 0f) v = 0f;
            float c = v <= 1f ? v * 0.75f : 0.75f + 0.25f * ((v - 1f) / (v));
            return (byte)Math.Round(Math.Min(1f, c) * 255f);
        }

        // ---------------------------------------------------------------- pictures

        /// <summary>A linear RGB equirect from a Radiance .hdr (RGBE, RLE) or, for other formats, from the 8-bit decode (sRGB → linear).</summary>
        public static bool LoadEquirect(string path, out float[] rgb, out int w, out int h)
        {
            rgb = null; w = h = 0;
            try
            {
                if (path.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase)) return LoadRgbe(path, out rgb, out w, out h);
                int pw, ph;
                byte[] px = Editor.DllWrapper.VortexAPI.DecodeImage(File.ReadAllBytes(path), out pw, out ph);
                if (px == null || pw <= 0 || ph <= 0) return false;
                w = pw; h = ph; rgb = new float[w * h * 3];
                for (int i = 0, o = 0; i < w * h; i++, o += 4)
                {
                    rgb[i * 3] = Srgb(px[o]); rgb[i * 3 + 1] = Srgb(px[o + 1]); rgb[i * 3 + 2] = Srgb(px[o + 2]);
                }
                return true;
            }
            catch { return false; }
        }

        private static float Srgb(byte v) { float f = v / 255f; return f <= 0.04045f ? f / 12.92f : (float)Math.Pow((f + 0.055f) / 1.055f, 2.4); }

        private static bool LoadRgbe(string path, out float[] rgb, out int w, out int h)
        {
            rgb = null; w = h = 0;
            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs))
            {
                string line = ReadLine(br);
                if (line == null || !line.StartsWith("#?")) return false;
                while (true)
                {
                    line = ReadLine(br);
                    if (line == null) return false;
                    if (line.StartsWith("-Y"))
                    {
                        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        h = int.Parse(parts[1]); w = int.Parse(parts[3]);
                        break;
                    }
                }
                rgb = new float[w * h * 3];
                var row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    if (!ReadScanline(br, row, w)) return false;
                    for (int x = 0; x < w; x++)
                    {
                        int e = row[x * 4 + 3];
                        int o = (y * w + x) * 3;
                        if (e == 0) { rgb[o] = rgb[o + 1] = rgb[o + 2] = 0f; continue; }
                        float f = (float)Math.Pow(2.0, e - 136);
                        rgb[o] = row[x * 4] * f; rgb[o + 1] = row[x * 4 + 1] * f; rgb[o + 2] = row[x * 4 + 2] * f;
                    }
                }
                return true;
            }
        }

        private static string ReadLine(BinaryReader br)
        {
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                if (br.BaseStream.Position >= br.BaseStream.Length) return sb.Length > 0 ? sb.ToString() : null;
                char c = (char)br.ReadByte();
                if (c == '\n') return sb.ToString();
                sb.Append(c);
            }
        }

        private static bool ReadScanline(BinaryReader br, byte[] row, int w)
        {
            byte a = br.ReadByte(), b = br.ReadByte(), c = br.ReadByte(), d = br.ReadByte();
            if (a != 2 || b != 2 || (c & 0x80) != 0 || ((c << 8) | d) != w)
            {
                row[0] = a; row[1] = b; row[2] = c; row[3] = d;
                for (int x = 1; x < w; x++) { row[x * 4] = br.ReadByte(); row[x * 4 + 1] = br.ReadByte(); row[x * 4 + 2] = br.ReadByte(); row[x * 4 + 3] = br.ReadByte(); }
                return true;
            }
            for (int ch = 0; ch < 4; ch++)
            {
                int x = 0;
                while (x < w)
                {
                    int count = br.ReadByte();
                    if (count > 128) { count -= 128; byte v = br.ReadByte(); for (int i = 0; i < count && x < w; i++, x++) row[x * 4 + ch] = v; }
                    else for (int i = 0; i < count && x < w; i++, x++) row[x * 4 + ch] = br.ReadByte();
                }
            }
            return true;
        }
    }
}
