using System;
using System.IO;
using System.Runtime.InteropServices;
using Editor.Utilities;

namespace Editor.DllWrapper
{
    /// <summary>Terrain interop (#124) — mirrors VortexAPI/Api/TerrainApi.cpp: raw-data meshes and textures for the chunk
    /// meshes, the splat map and the layer atlases, an image decoder for the atlas composer, Jolt's height field body, and
    /// the terrain material / brush gizmo glue.</summary>
    public static partial class VortexAPI
    {
        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long CreateMeshFromData(float[] vertices, int vertexCount, uint[] indices, int indexCount,
            float[] boundsMin, float[] boundsMax, [MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long CreateTexturePixels(int width, int height, int srgb, int mips, byte[] rgba);
        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long CreateTexturePixelsEx(int width, int height, int srgb, int mips, int kind, byte[] rgba);
        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetTextureCompression(int enabled, int minSize);
        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetTextureCacheDir(string dir);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int DecodeImageMemory(byte[] data, int length, out int width, out int height, byte[] outRgba, int cap);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern uint PhysicsCreateHeightFieldBody(ulong entityId, float[] heights, int sampleCount, float[] pos, float[] quat,
            float cellSize, float friction, float restitution, int layer);

        private static bool _terrainApiMissing;
        private static bool _terrainShaderWarned;

        /// <summary>False with an engine library that predates the terrain exports.</summary>
        public static bool TerrainApiAvailable => !_terrainApiMissing;

        /// <summary>A render mesh from raw data: 8 floats per vertex (position, normal, uv), a triangle list and the local
        /// bounds. <see cref="ID.INVALID_ID"/> on failure.</summary>
        public static long CreateTerrainMesh(float[] vertices, int vertexCount, uint[] indices, int indexCount, float[] boundsMin, float[] boundsMax, string name)
        {
            if (vertices == null || indices == null || vertexCount < 3 || indexCount < 3) return ID.INVALID_ID;
            try { return CreateMeshFromData(vertices, vertexCount, indices, indexCount, boundsMin, boundsMax, name ?? "TerrainChunk"); }
            catch (EntryPointNotFoundException) { _terrainApiMissing = true; return ID.INVALID_ID; }
            catch { return ID.INVALID_ID; }
        }

        /// <summary>An RGBA8 texture from raw pixels (row-major from the top). <see cref="ID.INVALID_ID"/> on failure.</summary>
        /// <summary>Compression kinds for <see cref="CreateTextureFromPixels"/> (#179): 0 = RGBA8 as it is, 1 = colour (BC1), 2 = colour +
        /// alpha (BC3), 3 = normal map (BC5, the shaders rebuild z), 4 = one channel (BC4).</summary>
        public const int TextureKindNone = 0, TextureKindColor = 1, TextureKindColorAlpha = 2, TextureKindNormal = 3, TextureKindGray = 4;

        /// <summary>Texture compression policy: off keeps every picture RGBA8; minSize = the smallest side that gets compressed.</summary>
        public static void ConfigureTextureCompression(bool enabled, int minSize = 256)
        {
            try { SetTextureCompression(enabled ? 1 : 0, minSize); } catch { }
        }

        /// <summary>The folder compressed textures are cached in between runs (the editor passes &lt;appdata&gt;/VortexEngine/texcache).</summary>
        public static void SetTextureCacheDirectory(string dir)
        {
            try { SetTextureCacheDir(dir ?? ""); } catch { }
        }

        public static long CreateTextureFromPixels(int width, int height, byte[] rgba, bool srgb = false, bool mips = true, int kind = 0)
        {
            if (rgba == null || width <= 0 || height <= 0 || rgba.Length < width * height * 4) return ID.INVALID_ID;
            try { return kind != 0 ? CreateTexturePixelsEx(width, height, srgb ? 1 : 0, mips ? 1 : 0, kind, rgba) : CreateTexturePixels(width, height, srgb ? 1 : 0, mips ? 1 : 0, rgba); }
            catch (EntryPointNotFoundException) { _terrainApiMissing = true; return ID.INVALID_ID; }
            catch { return ID.INVALID_ID; }
        }

        /// <summary>Decode an encoded image (PNG / JPG / TGA / BMP bytes) to RGBA8; null when it cannot be decoded.</summary>
        public static byte[] DecodeImage(byte[] encoded, out int width, out int height)
        {
            width = height = 0;
            if (encoded == null || encoded.Length == 0) return null;
            try
            {
                var buf = new byte[Math.Min(64 * 1024 * 1024, Math.Max(4 * 1024 * 1024, encoded.Length * 8))];
                int needed = DecodeImageMemory(encoded, encoded.Length, out width, out height, buf, buf.Length);
                if (needed <= 0) return null;
                if (needed > buf.Length)
                {
                    buf = new byte[needed];
                    needed = DecodeImageMemory(encoded, encoded.Length, out width, out height, buf, buf.Length);
                    if (needed <= 0 || needed > buf.Length) return null;
                }
                if (buf.Length == needed) return buf;
                var exact = new byte[needed];
                Buffer.BlockCopy(buf, 0, exact, 0, needed);
                return exact;
            }
            catch (EntryPointNotFoundException) { _terrainApiMissing = true; return null; }
            catch { return null; }
        }

        /// <summary>A static Jolt height-field body (0 = failed / no physics).</summary>
        public static uint CreateHeightFieldBody(ulong entityId, float[] heights, int sampleCount, float[] pos, float[] quat, float cellSize,
            float friction, float restitution, int layer)
        {
            if (heights == null || sampleCount < 2 || heights.Length < sampleCount * sampleCount) return 0;
            try { return PhysicsCreateHeightFieldBody(entityId, heights, sampleCount, pos, quat, cellSize, friction, restitution, layer); }
            catch (EntryPointNotFoundException) { _terrainApiMissing = true; return 0; }
            catch { return 0; }
        }

        // ---------------------------------------------------------------- terrain material

        /// <summary>The terrain shader file for this backend inside the engine's shader directory (null when missing).</summary>
        public static string TerrainShaderPath() => BuiltinMaterialShaderPath("terrain");

        /// <summary>The foliage (wind) shader file for this backend (null when missing).</summary>
        public static string FoliageShaderPath() => BuiltinMaterialShaderPath("foliage");

        /// <summary>A built-in material shader (terrain, foliage, …) of this backend inside the engine's shader directory:
        /// .hlsl flat on Windows, msl/&lt;name&gt;.metal on macOS, glsl/&lt;name&gt;.glsl beside the spirv set on Linux.</summary>
        public static string BuiltinMaterialShaderPath(string baseName)
        {
            try
            {
                string dir = Editor.Core.Native.NativeLoader.ShaderDirectory;
                if (string.IsNullOrEmpty(dir)) return null;
                string ext = Editor.Core.Native.NativeLoader.MaterialShaderExtension;
                string direct = Path.Combine(dir, baseName + ext);
                if (File.Exists(direct)) return direct;
                string glsl = Path.GetFullPath(Path.Combine(dir, "..", "glsl", baseName + ".glsl"));
                if (File.Exists(glsl)) return glsl;
                string hlsl = Path.Combine(dir, baseName + ".hlsl");
                return File.Exists(hlsl) ? hlsl : null;
            }
            catch { return null; }
        }

        /// <summary>A plain lit material of one colour (primitive foliage types, placeholders). <see cref="ID.INVALID_ID"/> on failure.</summary>
        public static long CreateColorMaterial(float r, float g, float b, float roughness = 0.8f)
        {
            long mat;
            try { mat = CreateMaterial(); } catch { return ID.INVALID_ID; }
            if (mat == ID.INVALID_ID) return mat;
            try { SetMaterialColor(mat, r, g, b, 1f); SetMaterialMetallic(mat, 0f); SetMaterialRoughness(mat, Math.Max(0.04f, Math.Min(1f, roughness))); SetMaterialAO(mat, 1f); } catch { }
            return mat;
        }

        /// <summary>A material bound to the terrain shader (<see cref="ID.INVALID_ID"/> when none could be made).</summary>
        public static long CreateTerrainMaterial()
        {
            long mat;
            try { mat = CreateMaterial(); } catch { return ID.INVALID_ID; }
            if (mat == ID.INVALID_ID) return mat;
            try
            {
                SetMaterialColor(mat, 0.25f, 0.25f, 0.25f, 0.25f);   // tiles per metre until the layers are set
                SetMaterialMetallic(mat, 0f);
                SetMaterialRoughness(mat, 0.9f);
                SetMaterialAO(mat, 1f);
                string shader = TerrainShaderPath();
                if (!string.IsNullOrEmpty(shader)) SetMaterialShader((int)mat, shader);
                else if (!_terrainShaderWarned)
                {
                    _terrainShaderWarned = true;
                    try { Editor.Core.Services.ConsoleService.Instance.LogWarning("Terrain: the terrain shader (terrain" + Editor.Core.Native.NativeLoader.MaterialShaderExtension + ") is missing from the engine's shader directory — terrains render with the standard PBR shader"); } catch { }
                }
            }
            catch { }
            return mat;
        }

        /// <summary>The terrain shader's per-material inputs: the four layers' tiling (metres per repeat), the fallback
        /// roughness, the normal-map strength and whether the normal maps use the DirectX green convention.</summary>
        public static void SetTerrainMaterialParams(long mat, float tile0, float tile1, float tile2, float tile3, float roughness, float normalStrength, bool directXNormals)
        {
            if (mat == ID.INVALID_ID) return;
            try
            {
                SetMaterialColor(mat, 1f / Math.Max(0.01f, tile0), 1f / Math.Max(0.01f, tile1), 1f / Math.Max(0.01f, tile2), 1f / Math.Max(0.01f, tile3));
                SetMaterialRoughness(mat, Math.Max(0.04f, Math.Min(1f, roughness)));
                SetMaterialNormalStrengthValue(mat, normalStrength);
                SetMaterialUseDirectXNormals(mat, directXNormals);
                SetMaterialTiling(mat, 1f, 1f);
            }
            catch { }
        }

        /// <summary>Bind the atlases and the splat map to the terrain material's slots (albedo / normal / roughness atlases,
        /// the splat map in the metallic slot). <see cref="ID.INVALID_ID"/> leaves a slot empty.</summary>
        public static void SetTerrainMaterialTextures(long mat, long albedoAtlas, long normalAtlas, long roughnessAtlas, long splat)
        {
            if (mat == ID.INVALID_ID) return;
            try
            {
                if (albedoAtlas != ID.INVALID_ID) SetMaterialAlbedoTexture(mat, albedoAtlas);
                if (normalAtlas != ID.INVALID_ID) SetMaterialNormalMap(mat, normalAtlas);
                if (roughnessAtlas != ID.INVALID_ID) SetMaterialRoughnessMap(mat, roughnessAtlas);
                if (splat != ID.INVALID_ID) SetMaterialMetallicMap(mat, splat);
                SetMaterialTextureChannels(mat, 1, 1, 1);   // every map reads its red channel (the splat map is sampled whole)
            }
            catch { }
        }

        // ---------------------------------------------------------------- brush gizmo

        private static long _terrainBrushSculpt = ID.INVALID_ID, _terrainBrushPaint = ID.INVALID_ID, _terrainBrushSmooth = ID.INVALID_ID;

        /// <summary>The terrain brush as an always-on-top wire disc: <paramref name="kind"/> 0 sculpt (green), 1 paint (cyan),
        /// 2 smooth / flatten (amber).</summary>
        public static void RenderTerrainBrush(float x, float y, float z, float radius, int kind)
        {
            if (!_gizmosInitialized) InitializeGizmos();
            if (_gizmoSphere == ID.INVALID_ID || radius <= 0f) return;
            long mat;
            if (kind == 1) { if (_terrainBrushPaint == ID.INVALID_ID) _terrainBrushPaint = MakeUnlitMaterial(0.2f, 0.85f, 0.95f); mat = _terrainBrushPaint; }
            else if (kind == 2) { if (_terrainBrushSmooth == ID.INVALID_ID) _terrainBrushSmooth = MakeUnlitMaterial(0.95f, 0.7f, 0.2f); mat = _terrainBrushSmooth; }
            else { if (_terrainBrushSculpt == ID.INVALID_ID) _terrainBrushSculpt = MakeUnlitMaterial(0.35f, 0.95f, 0.35f); mat = _terrainBrushSculpt; }
            if (mat == ID.INVALID_ID) return;
            // the unit sphere (diameter 1) squashed flat: a disc net of the brush radius, a little above the surface
            float d = radius * 2f;
            var world = new float[16] { d, 0, 0, 0, 0, Math.Max(0.02f, radius * 0.02f), 0, 0, 0, 0, d, 0, x, y + 0.03f, z, 1 };
            SubmitGizmoWireForRendering(_gizmoSphere, mat, world);
            // a thin post at the centre so the brush is visible edge-on
            var post = new float[16] { 0.03f, 0, 0, 0, 0, radius * 0.25f, 0, 0, 0, 0, 0.03f, 0, x, y + radius * 0.125f, z, 1 };
            SubmitGizmoWireForRendering(_gizmoCube, mat, post);
        }
    }
}
