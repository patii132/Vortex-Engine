using System;

namespace Editor.Core.Terrain
{
    /// <summary>The landscape shapes <see cref="TerrainGenerator"/> can raise from noise.</summary>
    public enum TerrainPreset
    {
        /// <summary>Everything at the base height.</summary>
        Flat = 0,
        /// <summary>Soft rolling ground: two octaves of smooth noise.</summary>
        Hills = 1,
        /// <summary>Ridged peaks with valleys between them (ridged multifractal).</summary>
        Mountains = 2,
        /// <summary>Parallel dune ridges with gentle slip faces, a little drift noise.</summary>
        Dunes = 3,
        /// <summary>Hills that sink below the base height towards the edges (a coast all round).</summary>
        Island = 4,
        /// <summary>A flat floor with a canyon cut through it.</summary>
        Canyon = 5,
    }

    /// <summary>
    /// Procedural starting points for a terrain (#124): a few shapes from value noise — hills, mountains, dunes, an island, a
    /// canyon — scaled by an amplitude and a wavelength in metres, repeatable by seed. The result is a starting point to sculpt
    /// on, not a simulation; the Terrain inspector, the hierarchy's Create menu and the <c>generate_terrain</c> tool call it.
    /// </summary>
    public static class TerrainGenerator
    {
        /// <summary>Fill the heights with the preset. <paramref name="sizeMetres"/> is the terrain's edge; amplitude (m) and
        /// wavelength (m) size the features; the base height is where flat ground sits. Returns the touched rectangle.</summary>
        public static SampleRect Generate(TerrainData data, float sizeMetres, TerrainPreset preset, float amplitude, float wavelength, float baseHeight, int seed)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            int n = data.Resolution;
            float cell = sizeMetres / (n - 1);
            if (wavelength < 1f) wavelength = 1f;
            if (amplitude < 0f) amplitude = 0f;
            var rnd = new Random(seed);
            float ox = (float)rnd.NextDouble() * 1000f, oz = (float)rnd.NextDouble() * 1000f;
            float ox2 = (float)rnd.NextDouble() * 1000f, oz2 = (float)rnd.NextDouble() * 1000f;
            float duneAngle = (float)(rnd.NextDouble() * Math.PI);
            float ca = (float)Math.Cos(duneAngle), sa = (float)Math.Sin(duneAngle);
            for (int z = 0; z < n; z++)
            {
                for (int x = 0; x < n; x++)
                {
                    float wx = x * cell, wz = z * cell;
                    float h;
                    switch (preset)
                    {
                        case TerrainPreset.Hills:
                            h = Fbm(wx / wavelength + ox, wz / wavelength + oz, 3, 0.5f) * amplitude;
                            break;
                        case TerrainPreset.Mountains:
                            h = Ridged(wx / wavelength + ox, wz / wavelength + oz, 5) * amplitude;
                            break;
                        case TerrainPreset.Dunes:
                        {
                            // ridges across the wind: a sawtooth with a gentle windward side and a steeper slip face, bent by drift noise
                            float drift = (Fbm(wx / (wavelength * 2.5f) + ox2, wz / (wavelength * 2.5f) + oz2, 2, 0.5f) - 0.5f) * wavelength * 0.8f;
                            float along = (wx * ca + wz * sa + drift) / wavelength;
                            float f = along - (float)Math.Floor(along);
                            float ridge = f < 0.7f ? f / 0.7f : 1f - (f - 0.7f) / 0.3f;   // windward 70 %, slip face 30 %
                            ridge = ridge * ridge * (3f - 2f * ridge);
                            float bumps = Fbm(wx / (wavelength * 0.35f) + ox, wz / (wavelength * 0.35f) + oz, 2, 0.5f) - 0.5f;
                            h = ridge * amplitude + bumps * amplitude * 0.18f;
                            break;
                        }
                        case TerrainPreset.Island:
                        {
                            float cx = sizeMetres * 0.5f, cz = sizeMetres * 0.5f;
                            float d = (float)Math.Sqrt((wx - cx) * (wx - cx) + (wz - cz) * (wz - cz)) / (sizeMetres * 0.5f);
                            float shore = 1f - Smooth(0.55f, 1.0f, d);   // 1 inside, 0 at the edge
                            float hills = Fbm(wx / wavelength + ox, wz / wavelength + oz, 3, 0.5f);
                            h = (hills * shore - 0.35f * (1f - shore)) * amplitude;
                            break;
                        }
                        case TerrainPreset.Canyon:
                        {
                            float wander = (Fbm(wz / (wavelength * 2f) + ox2, oz2, 2, 0.5f) - 0.5f) * wavelength * 1.5f;
                            float across = Math.Abs(wx - sizeMetres * 0.5f - wander) / (wavelength * 0.5f);   // 0 in the middle of the cut
                            float cut = Smooth(0.5f, 1.0f, across);   // 0 inside the canyon, 1 on the plateau
                            float rough = (Fbm(wx / (wavelength * 0.3f) + ox, wz / (wavelength * 0.3f) + oz, 2, 0.5f) - 0.5f) * 0.12f;
                            h = (cut - 1f + rough) * amplitude;   // the plateau at the base height, the canyon floor amplitude below
                            break;
                        }
                        default:
                            h = 0f;
                            break;
                    }
                    data.Heights[z * n + x] = baseHeight + h;
                }
            }
            data.MarkChanged();
            return new SampleRect { X0 = 0, Z0 = 0, X1 = n - 1, Z1 = n - 1 };
        }

        public static TerrainPreset ParsePreset(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return TerrainPreset.Hills;
            switch (s.Trim().ToLowerInvariant())
            {
                case "flat": return TerrainPreset.Flat;
                case "hills": case "hill": case "rolling": return TerrainPreset.Hills;
                case "mountains": case "mountain": case "peaks": return TerrainPreset.Mountains;
                case "dunes": case "dune": case "desert": return TerrainPreset.Dunes;
                case "island": return TerrainPreset.Island;
                case "canyon": case "valley": return TerrainPreset.Canyon;
            }
            throw new ArgumentException("Unknown terrain preset '" + s + "' — flat, hills, mountains, dunes, island or canyon");
        }

        // ---------------------------------------------------------------- noise

        private static float Smooth(float a, float b, float t)
        {
            t = (t - a) / (b - a); if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            return t * t * (3f - 2f * t);
        }

        private static float Hash(int x, int z)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393) + (uint)(z * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777216f;
            }
        }

        /// <summary>Smooth value noise 0..1.</summary>
        public static float Value(float x, float z)
        {
            int x0 = (int)Math.Floor(x), z0 = (int)Math.Floor(z);
            float fx = x - x0, fz = z - z0;
            fx = fx * fx * (3f - 2f * fx); fz = fz * fz * (3f - 2f * fz);
            float a = Hash(x0, z0), b = Hash(x0 + 1, z0), c = Hash(x0, z0 + 1), d = Hash(x0 + 1, z0 + 1);
            return (a + (b - a) * fx) + ((c + (d - c) * fx) - (a + (b - a) * fx)) * fz;
        }

        /// <summary>Fractal sum of value noise, normalised to 0..1.</summary>
        public static float Fbm(float x, float z, int octaves, float gain)
        {
            float sum = 0f, amp = 1f, norm = 0f, freq = 1f;
            for (int i = 0; i < octaves; i++)
            {
                sum += Value(x * freq + i * 17.3f, z * freq - i * 11.7f) * amp;
                norm += amp; amp *= gain; freq *= 2.1f;
            }
            return sum / norm;
        }

        /// <summary>Ridged multifractal 0..1: sharp crests, broad valleys.</summary>
        public static float Ridged(float x, float z, int octaves)
        {
            float sum = 0f, amp = 0.5f, freq = 1f, weight = 1f, norm = 0f;
            for (int i = 0; i < octaves; i++)
            {
                float v = Value(x * freq + i * 31.1f, z * freq + i * 7.9f) * 2f - 1f;
                v = 1f - Math.Abs(v);
                v *= v * weight;
                weight = Math.Max(0f, Math.Min(1f, v * 2f));
                sum += v * amp; norm += amp;
                amp *= 0.5f; freq *= 2.05f;
            }
            return sum / norm;
        }
    }
}
