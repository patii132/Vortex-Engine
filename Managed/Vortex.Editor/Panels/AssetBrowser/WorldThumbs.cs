using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Foliage;
using Editor.Core.Terrain;
using Editor.ECS;

namespace VortexEditor.Panels.AssetBrowser
{
    /// <summary>
    /// Previews for the world data files in the Asset Browser (#124 / #125): a <c>.vterrain</c> tile shows its heights as a
    /// shaded relief, a <c>.vfoliage</c> tile a top-down map of its painted instances (one colour per type) — and the tooltip
    /// says what is inside (samples and height range; instances and types) and which scene entity uses the file. Read on a
    /// worker from the file itself; nothing is cached on disk (the files are small and change with every stroke).
    /// </summary>
    public static class WorldThumbs
    {
        private static readonly Dictionary<string, (Bitmap bmp, string tip, DateTime stamp)> Cache = new Dictionary<string, (Bitmap, string, DateTime)>(StringComparer.OrdinalIgnoreCase);

        public static bool Handles(string path)
        {
            string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            return ext == ".vterrain" || ext == ".vfoliage";
        }

        /// <summary>Thumbnail + tooltip for the file; <paramref name="ready"/> runs on the UI thread (at once from the cache).</summary>
        public static void Request(string fullPath, int size, Action<Bitmap, string> ready)
        {
            if (!Handles(fullPath) || ready == null) return;
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(fullPath); } catch { stamp = DateTime.MinValue; }
            string key = fullPath + "|" + size;
            lock (Cache) if (Cache.TryGetValue(key, out var hit) && hit.stamp == stamp) { ready(hit.bmp, hit.tip); return; }
            Task.Run(() =>
            {
                Bitmap bmp = null; string tip = null;
                try
                {
                    string ext = Path.GetExtension(fullPath).ToLowerInvariant();
                    if (ext == ".vterrain") Terrain(fullPath, size, out bmp, out tip);
                    else Foliage(fullPath, size, out bmp, out tip);
                }
                catch (Exception ex) { tip = "could not read: " + ex.Message; }
                lock (Cache) Cache[key] = (bmp, tip, stamp);
                Dispatcher.UIThread.Post(() => ready(bmp, tip));
            });
        }

        public static void Forget(string fullPath)
        {
            lock (Cache)
            {
                var drop = Cache.Keys.Where(k => k.StartsWith(fullPath + "|", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var k in drop) Cache.Remove(k);
            }
        }

        // ---------------------------------------------------------------- terrain

        private static void Terrain(string path, int size, out Bitmap bmp, out string tip)
        {
            var data = TerrainData.Load(path);
            if (data == null) { bmp = null; tip = "not a terrain data file"; return; }
            var user = UserOf(path);
            float cell = 1f;
            var t = user?.GetComponent<Editor.ECS.Components.Rendering.Terrain>();
            if (t != null && t.Resolution > 1) cell = t.Size / (t.Resolution - 1);
            int stride;
            var px = HeightmapIO.ShadedRelief(data, size, cell, out stride);
            bmp = ToBitmap(px, size, size, stride);
            tip = "Terrain heightfield · " + HeightmapIO.Describe(data) +
                  (t != null ? "\n" + t.Size.ToString("0") + " m × " + t.Size.ToString("0") + " m, " + cell.ToString("0.##") + " m cells" : "") +
                  "\n" + (user != null ? "used by \"" + user.Name + "\" — double-click selects it" : "no entity in the open scene uses it — add a Terrain and pick it as the data file") +
                  "\nImport / export a heightmap (PNG 16 bit, RAW 16) in the Terrain inspector";
        }

        // ---------------------------------------------------------------- foliage

        private static readonly uint[] Palette =
        {
            0xFF3FA34D, 0xFF8BC34A, 0xFF2E7D32, 0xFFC0CA33, 0xFF9E9D24, 0xFF4CAF50, 0xFF689F38, 0xFF00897B, 0xFFA1887F, 0xFF8D6E63,
            0xFF795548, 0xFF6D4C41, 0xFFBDBDBD, 0xFF9E9E9E, 0xFF78909C, 0xFFFFB74D, 0xFFFF8A65, 0xFFD4E157, 0xFFAED581, 0xFF80CBC4,
        };

        private static void Foliage(string path, int size, out Bitmap bmp, out string tip)
        {
            var data = FoliageData.Load(path);
            if (data == null) { bmp = null; tip = "not a foliage data file"; return; }
            int total = 0, typeCount = 0;
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            var names = new List<string>();
            foreach (var layer in data.Layers)
            {
                if (layer == null) continue;
                typeCount++;
                names.Add(layer.TypeName + " " + layer.Count);
                foreach (var inst in layer.Instances)
                {
                    total++;
                    if (inst.Position.X < minX) minX = inst.Position.X; if (inst.Position.X > maxX) maxX = inst.Position.X;
                    if (inst.Position.Z < minZ) minZ = inst.Position.Z; if (inst.Position.Z > maxZ) maxZ = inst.Position.Z;
                }
            }
            var user = UserOf(path);
            // the map covers the terrain under the plants when there is one (the painted area sits where it sits on the ground)
            Editor.ECS.Components.Rendering.Terrain ground = null; GameEntity groundEntity = null;
            try
            {
                var scene = ProjectData.Current?.ActiveScene;
                if (scene != null)
                    foreach (var e in Walk(scene))
                    {
                        var tc = e.GetComponent<Editor.ECS.Components.Rendering.Terrain>();
                        if (tc != null && (ground == null || tc.Size < ground.Size) && tc.Navigation) { ground = tc; groundEntity = e; }
                    }
            }
            catch { }
            if (ground != null && groundEntity?.Transform != null)
            {
                var p = groundEntity.Transform.LocalPosition;
                minX = p.X; minZ = p.Z; maxX = p.X + ground.Size; maxZ = p.Z + ground.Size;
            }
            if (total == 0 || maxX - minX < 1f || maxZ - minZ < 1f) { minX = -64; maxX = 64; minZ = -64; maxZ = 64; }
            int stride = size * 4;
            var px = new byte[size * stride];
            for (int i = 0; i < size * size; i++) { px[i * 4] = 0x8A; px[i * 4 + 1] = 0xB0; px[i * 4 + 2] = 0xC4; px[i * 4 + 3] = 255; }   // sand
            float sx = (size - 1) / (maxX - minX), sz = (size - 1) / (maxZ - minZ);
            int li = 0;
            foreach (var layer in data.Layers)
            {
                if (layer == null) continue;
                uint c = Palette[li++ % Palette.Length];
                byte r = (byte)(c >> 16), g = (byte)(c >> 8), b = (byte)c;
                int dot = size >= 192 ? 2 : 1;
                foreach (var inst in layer.Instances)
                {
                    int x = (int)((inst.Position.X - minX) * sx), y = size - 1 - (int)((inst.Position.Z - minZ) * sz);
                    for (int dy = 0; dy < dot; dy++)
                        for (int dx = 0; dx < dot; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= size || yy >= size) continue;
                            int o = yy * stride + xx * 4;
                            px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = 255;
                        }
                }
            }
            bmp = ToBitmap(px, size, size, stride);
            names.Sort((a, b2) => Count(b2).CompareTo(Count(a)));
            tip = "Foliage instances · " + total.ToString("#,0") + " plants, rocks and props of " + typeCount + " types" +
                  "\n" + string.Join(", ", names.Take(8)) + (names.Count > 8 ? ", …" : "") +
                  "\n" + (user != null ? "used by \"" + user.Name + "\" — double-click selects it" : "no entity in the open scene uses it — add a Foliage layer and pick it as the data file") +
                  "\nPaint and erase with the brush in the Foliage inspector";
        }

        private static int Count(string nameAndCount)
        {
            int sp = nameAndCount.LastIndexOf(' ');
            return sp >= 0 && int.TryParse(nameAndCount.Substring(sp + 1), out int n) ? n : 0;
        }

        // ---------------------------------------------------------------- shared

        /// <summary>The entity of the open scene whose Terrain / Foliage component points at this data file (null = none).</summary>
        public static GameEntity UserOf(string fullPath)
        {
            try
            {
                var scene = ProjectData.Current?.ActiveScene;
                string root = ProjectData.Current?.Path;
                if (scene == null || string.IsNullOrEmpty(root)) return null;
                string want = Path.GetFullPath(fullPath);
                foreach (var e in Walk(scene))
                {
                    var t = e.GetComponent<Editor.ECS.Components.Rendering.Terrain>();
                    if (t != null && Same(root, t.DataPath, want)) return e;
                    var f = e.GetComponent<Editor.ECS.Components.Rendering.Foliage>();
                    if (f != null && Same(root, f.DataPath, want)) return e;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Every entity of the scene, parents before children.</summary>
        public static IEnumerable<GameEntity> Walk(Scene scene)
        {
            if (scene?.Entities == null) yield break;
            var stack = new Stack<GameEntity>();
            for (int i = scene.Entities.Count - 1; i >= 0; i--) if (scene.Entities[i] != null) stack.Push(scene.Entities[i]);
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                yield return e;
                if (e.Children != null) for (int i = e.Children.Count - 1; i >= 0; i--) if (e.Children[i] != null) stack.Push(e.Children[i]);
            }
        }

        private static bool Same(string root, string rel, string wantAbs)
        {
            if (string.IsNullOrWhiteSpace(rel)) return false;
            try { return string.Equals(Path.GetFullPath(Path.IsPathRooted(rel) ? rel : Path.Combine(root, rel.Replace('\\', '/'))), wantAbs, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static Bitmap ToBitmap(byte[] bgra, int w, int h, int stride)
        {
            var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using (var fb = wb.Lock())
                for (int y = 0; y < h; y++)
                    System.Runtime.InteropServices.Marshal.Copy(bgra, y * stride, IntPtr.Add(fb.Address, y * fb.RowBytes), stride);
            return wb;
        }
    }
}
