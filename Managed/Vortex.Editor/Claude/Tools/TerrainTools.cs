using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Editor.Core.Data;
using Editor.Core.Foliage;
using Editor.Core.Services;
using Editor.Core.Services.Foliage;
using Editor.Core.Services.Terrain;
using Editor.Core.Terrain;
using Editor.ECS;
using Editor.ECS.Components.Rendering;
using ModelContextProtocol.Server;
using Vector3 = System.Numerics.Vector3;

namespace VortexEditor.Claude.Tools
{
    /// <summary>
    /// Terrain and foliage (#124 / #125): create heightfield terrains, sculpt and paint them, read their heights, and paint
    /// vegetation layers — the world-building half of the v3.4 World milestone, so Claude can raise a landscape and grow a
    /// forest from the chat. Every changing call saves the data assets it touched.
    /// </summary>
    [McpServerToolType, DisplayName("Terrain")]
    public static class TerrainTools
    {
        // ================================================================== terrain

        [McpServerTool(Name = "create_terrain", Destructive = false)]
        [Description("Creates a heightfield terrain entity: a square of size metres from position along +X / +Z with resolution height samples per side " +
                     "(33, 65, 129, 257, 513, 1025 or 2049), flat at height 0 until sculpted. layers = up to four .vmat paths that texture it (world-space tiling in metres). " +
                     "Sculpt it with sculpt_terrain, paint layers with paint_terrain_layer. One undo step.")]
        public static object CreateTerrain(
            [Description("World position of the terrain's corner [x, y, z] (the terrain extends along +X and +Z)")] float[] position,
            [Description("Edge length in metres (default 128)")] float size = 128f,
            [Description("Height samples per side (default 129 = 1 m cells on 128 m)")] int resolution = 129,
            [Description("Up to four .vmat material paths for layers 1–4")] string[] layers = null,
            [Description("Metres per texture repeat per layer (default 4 each)")] float[] tiling = null,
            [Description("Entity name (default Terrain)")] string name = null,
            [Description("Only report what would be created")] bool dry_run = false)
        {
            var scene = RequireScene();
            var pos = SceneModel.Vec(position, "position") ?? throw new ToolError("position [x, y, z] is required.");
            if (size < 1f || size > 16384f) throw new ToolError("size must be 1–16384 metres.");
            int res = Terrain.SnapResolution(resolution);
            if (dry_run) return new { would_create = name ?? "Terrain", size, resolution = res, cell_size = size / (res - 1) };
            var e = new GameEntity(scene, name ?? "Terrain");
            var t = new Terrain(e) { Size = size, Resolution = res };
            for (int i = 0; i < 4; i++)
            {
                if (layers != null && i < layers.Length && !string.IsNullOrWhiteSpace(layers[i])) t.SetLayerMaterial(i, layers[i].Replace('\\', '/'));
                if (tiling != null && i < tiling.Length && tiling[i] > 0f) t.SetLayerTile(i, tiling[i]);
            }
            e.AddComponent(t);
            if (e.Transform != null) e.Transform.LocalPosition = new Editor.ECS.Vector3(pos.X, pos.Y, pos.Z);
            scene.AddEntity(e);
            TerrainService.Save(e);
            ToolContext.UndoLabel = "terrain " + e.Name;
            SelectionService.Instance.SelectedEntity = e;
            return Info(e, t);
        }

        [McpServerTool(Name = "sculpt_terrain", Destructive = false)]
        [Description("Sculpts a terrain with a brush at a world point: op raise / lower / smooth / flatten, radius in metres, amount = metres (raise / lower), " +
                     "0..1 strength (smooth) or the target height (flatten). Several strokes in one call: pass points as [[x, z], ...] (the first point's values apply to all). " +
                     "The chunks, the collision and the data file update. One undo step.")]
        public static object SculptTerrain(
            [Description("The terrain entity (id, name or path); empty = the terrain under the point")] string terrain,
            [Description("Brush centre [x, z] (world)")] float[] point,
            [Description("raise, lower, smooth or flatten")] string op,
            [Description("Brush radius in metres")] float radius,
            [Description("raise / lower: metres at the centre; smooth: 0..1; flatten: the target world height")] float amount,
            [Description("0 soft dome .. 0.9 flat stamp (default 0.2)")] float hardness = 0.2f,
            [Description("More brush centres [[x, z], ...] with the same settings")] float[][] points = null,
            [Description("Only report")] bool dry_run = false)
        {
            if (point == null || point.Length != 2) throw new ToolError("point is [x, z].");
            if (radius <= 0f) throw new ToolError("radius must be positive.");
            string o = (op ?? "").Trim().ToLowerInvariant();
            if (o != "raise" && o != "lower" && o != "smooth" && o != "flatten") throw new ToolError("op is raise, lower, smooth or flatten.");
            var centres = new List<(float x, float z)> { (point[0], point[1]) };
            if (points != null) foreach (var p in points) if (p != null && p.Length == 2) centres.Add((p[0], p[1]));
            var e = ResolveTerrain(terrain, centres[0].x, centres[0].z);
            TerrainData data; float cell;
            if (!TerrainService.TryGetData(e, out data, out cell)) throw new ToolError("The terrain has no data.");
            if (dry_run) return new { terrain = SceneModel.ShortId(e), op = o, strokes = centres.Count, radius, amount };
            var before = (float[])data.Heights.Clone();
            var rect = SampleRect.Empty;
            foreach (var c in centres)
            {
                var l = TerrainService.WorldToLocal(e, new Vector3(c.x, 0f, c.z));
                float cx = l.X / cell, cz = l.Z / cell, r = Math.Max(0.5f, radius / cell);
                switch (o)
                {
                    case "raise": rect = rect.Union(data.Raise(cx, cz, r, amount, hardness)); break;
                    case "lower": rect = rect.Union(data.Raise(cx, cz, r, -amount, hardness)); break;
                    case "smooth": rect = rect.Union(data.Smooth(cx, cz, r, Math.Max(0f, Math.Min(1f, amount)), hardness)); break;
                    default:
                    {
                        var lt = TerrainService.WorldToLocal(e, new Vector3(c.x, amount, c.z));
                        rect = rect.Union(data.Flatten(cx, cz, r, lt.Y, 1f, hardness));
                        break;
                    }
                }
            }
            TerrainService.MarkDirty(e, rect, true, false);
            TerrainService.Save(e);
            var after = data.CopyHeights(rect);
            var beforeRect = CropHeights(before, data.Resolution, rect);
            var redoRect = rect;
            Editor.Core.UndoRedo.UndoRedoManager.Instance.Execute(new Editor.Core.UndoRedo.Commands.ActionCommand("Terrain " + o,
                () => { data.RestoreHeights(redoRect, after); TerrainService.MarkDirty(e, redoRect, true, false); TerrainService.Save(e); },
                () => { data.RestoreHeights(redoRect, beforeRect); TerrainService.MarkDirty(e, redoRect, true, false); TerrainService.Save(e); }), false);
            ToolContext.UndoLabel = o + " terrain";
            Editor.Core.Viewport.EditorViewportSession.RequestResubmit();
            return new { terrain = SceneModel.ShortId(e), op = o, strokes = centres.Count, changed_samples = rect.Width * rect.Height, min_height = data.MinHeight, max_height = data.MaxHeight };
        }

        [McpServerTool(Name = "generate_terrain", Destructive = false)]
        [Description("Fills a terrain's heights from noise as a starting point: preset flat, hills, mountains, dunes, island or canyon, sized by amplitude (m) and " +
                     "wavelength (m, the feature width / ridge spacing), on a base height, repeatable by seed. Replaces every height (the splat map stays); the chunks, " +
                     "the collision and the data file update. One undo step.")]
        public static object GenerateTerrain(
            [Description("The terrain entity (id, name or path)")] string terrain,
            [Description("flat, hills, mountains, dunes, island or canyon (default hills)")] string preset = "hills",
            [Description("Height of the features in metres (default 40)")] float amplitude = 40f,
            [Description("Width of the features in metres (default 120)")] float wavelength = 120f,
            [Description("Height of flat ground (default 0)")] float base_height = 0f,
            [Description("Noise seed (default 7)")] int seed = 7,
            [Description("Only report")] bool dry_run = false)
        {
            var e = ResolveTerrain(terrain, 0f, 0f);
            var t = e.GetComponent<Terrain>();
            var shape = TerrainGenerator.ParsePreset(preset);
            if (dry_run) return new { terrain = SceneModel.ShortId(e), preset = shape.ToString(), amplitude, wavelength, base_height, seed };
            ReplaceAllHeights(e, "Generate terrain (" + shape + ")", d => TerrainGenerator.Generate(d, t.Size, shape, amplitude, wavelength, base_height, seed));
            ToolContext.UndoLabel = "generate terrain " + e.Name;
            return Info(e, t);
        }

        [McpServerTool(Name = "import_heightmap", Destructive = false)]
        [Description("Reads a heightmap picture into a terrain: a PNG (8 / 16 bit grey or colour) or a RAW 16-bit square, resampled to the terrain's resolution, " +
                     "black → low_height, white → high_height (metres). Replaces every height; the chunks, the collision and the data file update. One undo step.")]
        public static object ImportHeightmap(
            [Description("The terrain entity (id, name or path)")] string terrain,
            [Description("Picture path (absolute, or relative to the project)")] string path,
            [Description("Height of black in metres (default 0)")] float low_height = 0f,
            [Description("Height of white in metres (default 100)")] float high_height = 100f,
            [Description("Flip the picture top-to-bottom (its first row at +Z instead of the terrain's first row)")] bool flip = false,
            [Description("Only report")] bool dry_run = false)
        {
            var e = ResolveTerrain(terrain, 0f, 0f);
            var t = e.GetComponent<Terrain>();
            string abs = TerrainService.ResolvePath(path) ?? throw new ToolError("path is required.");
            if (!System.IO.File.Exists(abs)) throw new ToolError("No such file: " + abs);
            HeightmapImage img;
            try { img = HeightmapIO.Load(abs); }
            catch (Exception ex) { throw new ToolError("Could not read the heightmap: " + ex.Message); }
            float low = Math.Min(low_height, high_height), high = Math.Max(low_height, high_height);
            if (high - low < 0.001f) high = low + 1f;
            if (dry_run) return new { terrain = SceneModel.ShortId(e), picture = new { img.Width, img.Height, bits = img.BitDepth, img.Note }, low, high };
            ReplaceAllHeights(e, "Import heightmap", d => HeightmapIO.Apply(d, img, low, high, flip));
            ToolContext.UndoLabel = "import heightmap " + e.Name;
            return new { terrain = SceneModel.ShortId(e), picture = new { img.Width, img.Height, bits = img.BitDepth, img.Note }, low, high, info = Info(e, t) };
        }

        [McpServerTool(Name = "export_heightmap", ReadOnly = true)]
        [Description("Writes a terrain's heights as a heightmap file: a 16-bit grey PNG (.png) or RAW 16-bit (.raw / .r16), lowest height = black, highest = white. " +
                     "For World Machine, Gaea, Blender, Photoshop… Returns the height range the picture spans.")]
        public static object ExportHeightmap(
            [Description("The terrain entity (id, name or path)")] string terrain,
            [Description("Output path (.png, .raw or .r16; absolute, or relative to the project)")] string path)
        {
            var e = ResolveTerrain(terrain, 0f, 0f);
            TerrainData data; float cell;
            if (!TerrainService.TryGetData(e, out data, out cell)) throw new ToolError("The terrain has no data.");
            string abs = TerrainService.ResolvePath(path) ?? throw new ToolError("path is required.");
            string ext = System.IO.Path.GetExtension(abs).ToLowerInvariant();
            if (ext != ".png" && ext != ".raw" && ext != ".r16") throw new ToolError("path must end in .png, .raw or .r16.");
            float low, high;
            var bytes = ext == ".png" ? HeightmapIO.ToPng16(data, out low, out high) : HeightmapIO.ToRaw16(data, out low, out high);
            var dir = System.IO.Path.GetDirectoryName(abs);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllBytes(abs, bytes);
            return new { terrain = SceneModel.ShortId(e), file = abs, resolution = data.Resolution, black_height = low, white_height = high, bytes = bytes.Length };
        }

        /// <summary>Replace every height of a terrain as one undo step: rebuild the chunks, the collision and the data file.</summary>
        private static void ReplaceAllHeights(GameEntity e, string label, Action<TerrainData> fill)
        {
            TerrainData data; float cell;
            if (!TerrainService.TryGetData(e, out data, out cell)) throw new ToolError("The terrain has no data.");
            var before = (float[])data.Heights.Clone();
            fill(data);
            var after = (float[])data.Heights.Clone();
            var all = new SampleRect { X0 = 0, Z0 = 0, X1 = data.Resolution - 1, Z1 = data.Resolution - 1 };
            void Put(float[] h) { Array.Copy(h, data.Heights, h.Length); data.MarkChanged(); TerrainService.MarkDirty(e, all, true, false); TerrainService.Save(e); Editor.Core.Viewport.EditorViewportSession.RequestResubmit(); }
            Editor.Core.UndoRedo.UndoRedoManager.Instance.Execute(new Editor.Core.UndoRedo.Commands.ActionCommand(label, () => Put(after), () => Put(before)), false);
            Put(after);
        }

        [McpServerTool(Name = "paint_terrain_layer", Destructive = false)]
        [Description("Paints one of the terrain's four texture layers (1–4) with a brush at world points: strength 0..1 moves the splat weight onto that layer. One undo step.")]
        public static object PaintTerrainLayer(
            [Description("The terrain entity (id, name or path); empty = the terrain under the point")] string terrain,
            [Description("Brush centre [x, z] (world)")] float[] point,
            [Description("The layer to paint, 1–4")] int layer,
            [Description("Brush radius in metres")] float radius,
            [Description("0..1 (default 1)")] float strength = 1f,
            [Description("0 soft .. 0.9 hard (default 0.3)")] float hardness = 0.3f,
            [Description("More brush centres [[x, z], ...]")] float[][] points = null)
        {
            if (point == null || point.Length != 2) throw new ToolError("point is [x, z].");
            if (layer < 1 || layer > 4) throw new ToolError("layer is 1–4.");
            if (radius <= 0f) throw new ToolError("radius must be positive.");
            var centres = new List<(float x, float z)> { (point[0], point[1]) };
            if (points != null) foreach (var p in points) if (p != null && p.Length == 2) centres.Add((p[0], p[1]));
            var e = ResolveTerrain(terrain, centres[0].x, centres[0].z);
            TerrainData data; float cell;
            if (!TerrainService.TryGetData(e, out data, out cell)) throw new ToolError("The terrain has no data.");
            var before = (byte[])data.Splat.Clone();
            var rect = SampleRect.Empty;
            foreach (var c in centres)
            {
                var l = TerrainService.WorldToLocal(e, new Vector3(c.x, 0f, c.z));
                rect = rect.Union(data.Paint(l.X / cell, l.Z / cell, Math.Max(0.5f, radius / cell), layer - 1, Math.Max(0f, Math.Min(1f, strength)), hardness));
            }
            TerrainService.MarkDirty(e, rect, false, true);
            TerrainService.Save(e);
            var after = data.CopySplat(rect);
            var beforeRect = CropSplat(before, data.Resolution, rect);
            var r2 = rect;
            Editor.Core.UndoRedo.UndoRedoManager.Instance.Execute(new Editor.Core.UndoRedo.Commands.ActionCommand("Paint terrain layer",
                () => { data.RestoreSplat(r2, after); TerrainService.MarkDirty(e, r2, false, true); TerrainService.Save(e); },
                () => { data.RestoreSplat(r2, beforeRect); TerrainService.MarkDirty(e, r2, false, true); TerrainService.Save(e); }), false);
            ToolContext.UndoLabel = "paint terrain layer " + layer;
            Editor.Core.Viewport.EditorViewportSession.RequestResubmit();
            return new { terrain = SceneModel.ShortId(e), layer, strokes = centres.Count, changed_samples = rect.Width * rect.Height };
        }

        [McpServerTool(Name = "set_terrain_layers", Destructive = false)]
        [Description("Sets a terrain's texture layers: up to four .vmat paths and their tiling in metres (null entries keep the current layer). One undo step.")]
        public static object SetTerrainLayers(
            [Description("The terrain entity (id, name or path)")] string terrain,
            [Description("Four .vmat paths (null keeps a layer)")] string[] layers,
            [Description("Metres per repeat per layer (0 keeps)")] float[] tiling = null)
        {
            var e = SceneModel.Resolve(terrain) ?? throw new ToolError("No entity '" + terrain + "'.");
            var t = e.GetComponent<Terrain>() ?? throw new ToolError("'" + e.Name + "' has no Terrain component.");
            for (int i = 0; i < 4; i++)
            {
                if (layers != null && i < layers.Length && layers[i] != null) t.SetLayerMaterial(i, layers[i].Length == 0 ? null : layers[i].Replace('\\', '/'));
                if (tiling != null && i < tiling.Length && tiling[i] > 0f) t.SetLayerTile(i, tiling[i]);
            }
            ToolContext.UndoLabel = "terrain layers of " + e.Name;
            Editor.Core.Viewport.EditorViewportSession.RequestResubmit();
            return Info(e, t);
        }

        [McpServerTool(Name = "terrain_height", ReadOnly = true, Idempotent = true)]
        [Description("The terrain surface height (world Y) and normal under world points [[x, z], ...]; null for points off every terrain.")]
        public static object TerrainHeight([Description("World points [[x, z], ...]")] float[][] points)
        {
            if (points == null || points.Length == 0) throw new ToolError("points is [[x, z], ...].");
            var list = new List<object>();
            foreach (var p in points)
            {
                if (p == null || p.Length != 2) { list.Add(null); continue; }
                var e = TerrainService.FindAt(p[0], p[1]);
                float y; Vector3 n;
                if (e != null && TerrainService.TryHeight(e, p[0], p[1], out y))
                {
                    TerrainService.TryNormal(e, p[0], p[1], out n);
                    list.Add(new { x = p[0], z = p[1], y, normal = new[] { n.X, n.Y, n.Z }, terrain = SceneModel.ShortId(e) });
                }
                else list.Add(null);
            }
            return list;
        }

        [McpServerTool(Name = "terrain_info", ReadOnly = true, Idempotent = true)]
        [Description("The terrains of the scene: size, resolution, height range, layers, data file.")]
        public static object TerrainInfo()
        {
            var scene = RequireScene();
            var list = new List<object>();
            foreach (var e in SceneModel.All(scene))
            {
                var t = e.GetComponent<Terrain>();
                if (t != null) list.Add(Info(e, t));
            }
            return list;
        }

        // ================================================================== foliage

        [McpServerTool(Name = "create_foliage_layer", Destructive = false)]
        [Description("Creates a Foliage entity (a layer of painted vegetation / props) with optional types: each type = a model path plus whether it is a tree " +
                     "(upright, colliding, far view distance) or a plant (aligned to the ground, dense, thinned with distance). Paint with paint_foliage. One undo step.")]
        public static object CreateFoliageLayer(
            [Description("Entity name (default Foliage)")] string name = null,
            [Description("Types to add: [{name, model, tree}] — model = .glb/.gltf/.fbx/.vmesh or Primitive:Sphere, tree = true for trees")] FoliageTypeSpec[] types = null,
            [Description("Only report")] bool dry_run = false)
        {
            var scene = RequireScene();
            if (dry_run) return new { would_create = name ?? "Foliage", types = types?.Length ?? 0 };
            var e = new GameEntity(scene, name ?? "Foliage");
            var f = new Foliage(e);
            if (types != null)
                foreach (var ts in types)
                {
                    if (ts == null || string.IsNullOrWhiteSpace(ts.model)) continue;
                    var t = Foliage.DefaultType(string.IsNullOrWhiteSpace(ts.name) ? System.IO.Path.GetFileNameWithoutExtension(ts.model) : ts.name, ts.model.Replace('\\', '/'), !ts.tree);
                    Apply(t, ts);
                    f.Types.Add(t);
                }
            e.AddComponent(f);
            scene.AddEntity(e);
            ToolContext.UndoLabel = "foliage layer " + e.Name;
            SelectionService.Instance.SelectedEntity = e;
            return FoliageInfo(e, f);
        }

        [McpServerTool(Name = "add_foliage_type", Destructive = false)]
        [Description("Adds (or updates, by name) a type on a Foliage entity: the model, tree / plant preset, and optional overrides (density per m², min spacing m, " +
                     "scale range, cull distance m, collision radius / height, wind strength). One undo step.")]
        public static object AddFoliageType(
            [Description("The Foliage entity (id, name or path)")] string foliage,
            [Description("The type: {name, model, tree, density, spacing, min_scale, max_scale, cull_distance, collision_radius, collision_height, wind}")] FoliageTypeSpec type)
        {
            var e = SceneModel.Resolve(foliage) ?? throw new ToolError("No entity '" + foliage + "'.");
            var f = e.GetComponent<Foliage>() ?? throw new ToolError("'" + e.Name + "' has no Foliage component.");
            if (type == null || string.IsNullOrWhiteSpace(type.model)) throw new ToolError("type.model is required.");
            string tname = string.IsNullOrWhiteSpace(type.name) ? System.IO.Path.GetFileNameWithoutExtension(type.model) : type.name;
            int idx = f.IndexOfType(tname);
            FoliageType t;
            if (idx >= 0) { t = f.Types[idx]; t.MeshPath = type.model.Replace('\\', '/'); }
            else { t = Foliage.DefaultType(tname, type.model.Replace('\\', '/'), !type.tree); f.Types.Add(t); }
            Apply(t, type);
            f.Touch();
            ToolContext.UndoLabel = "foliage type " + tname;
            Editor.Core.Viewport.EditorViewportSession.RequestResubmit();
            return FoliageInfo(e, f);
        }

        [McpServerTool(Name = "paint_foliage", Destructive = false)]
        [Description("Paints instances of a Foliage type inside discs: centre [x, z] and radius in metres, strength 0..1 × the type's density. " +
                     "Instances land on terrains and mesh tops, keep the type's spacing and slope limits. Deterministic for a seed. Several discs: points [[x, z], ...]. One undo step.")]
        public static object PaintFoliage(
            [Description("The Foliage entity (id, name or path)")] string foliage,
            [Description("The type name (empty = every type by weight)")] string type,
            [Description("Disc centre [x, z] (world)")] float[] point,
            [Description("Disc radius in metres")] float radius,
            [Description("0..1 (default 1)")] float strength = 1f,
            [Description("Random seed (default 1)")] int seed = 1,
            [Description("More disc centres [[x, z], ...]")] float[][] points = null,
            [Description("Only report the candidate count")] bool dry_run = false)
        {
            var e = SceneModel.Resolve(foliage) ?? throw new ToolError("No entity '" + foliage + "'.");
            var f = e.GetComponent<Foliage>() ?? throw new ToolError("'" + e.Name + "' has no Foliage component.");
            if (f.Types.Count == 0) throw new ToolError("The layer has no types — add_foliage_type first.");
            if (point == null || point.Length != 2) throw new ToolError("point is [x, z].");
            if (radius <= 0f || radius > 2000f) throw new ToolError("radius must be 0–2000 metres.");
            int typeIndex = -1;
            if (!string.IsNullOrWhiteSpace(type)) { typeIndex = f.IndexOfType(type); if (typeIndex < 0) throw new ToolError("No type '" + type + "' on '" + e.Name + "'."); }
            var centres = new List<(float x, float z)> { (point[0], point[1]) };
            if (points != null) foreach (var p in points) if (p != null && p.Length == 2) centres.Add((p[0], p[1]));
            if (dry_run)
            {
                float area = (float)Math.PI * radius * radius * centres.Count;
                int cand = 0;
                for (int i = 0; i < f.Types.Count; i++) if (typeIndex < 0 || i == typeIndex) cand += (int)Math.Round(area * f.Types[i].Density * strength);
                return new { foliage = SceneModel.ShortId(e), discs = centres.Count, candidates = cand };
            }
            FoliageData data;
            FoliageService.TryGetData(e, out data);
            var before = Snapshot(data);
            int placed = 0, s = seed;
            foreach (var c in centres)
            {
                var centre = new Vector3(c.x, 0f, c.z);
                if (typeIndex >= 0) placed += FoliageService.Paint(e, typeIndex, centre, radius, strength, s++);
                else
                {
                    float total = f.Types.Sum(t => Math.Max(0f, t.Weight));
                    for (int i = 0; i < f.Types.Count; i++)
                    {
                        float share = total > 0f ? Math.Max(0f, f.Types[i].Weight) / total : 1f / f.Types.Count;
                        if (share > 0f) placed += FoliageService.Paint(e, i, centre, radius, strength * share * f.Types.Count, s++);
                    }
                }
            }
            FoliageService.Save(e);
            PushUndo(e, "Paint foliage", before, Snapshot(data));
            ToolContext.UndoLabel = "paint " + placed + " foliage";
            Editor.Core.Viewport.EditorViewportSession.RequestResubmit();
            return new { foliage = SceneModel.ShortId(e), placed, total = FoliageService.InstanceCount(e) };
        }

        [McpServerTool(Name = "erase_foliage", Destructive = true)]
        [Description("Removes the instances inside a disc (one type, or every type) — or every instance of the layer with clear_all. One undo step.")]
        public static object EraseFoliage(
            [Description("The Foliage entity (id, name or path)")] string foliage,
            [Description("Disc centre [x, z] (world); ignored with clear_all")] float[] point = null,
            [Description("Disc radius in metres")] float radius = 5f,
            [Description("The type name (empty = every type)")] string type = null,
            [Description("Remove every instance of the layer")] bool clear_all = false)
        {
            var e = SceneModel.Resolve(foliage) ?? throw new ToolError("No entity '" + foliage + "'.");
            var f = e.GetComponent<Foliage>() ?? throw new ToolError("'" + e.Name + "' has no Foliage component.");
            int typeIndex = -1;
            if (!string.IsNullOrWhiteSpace(type)) { typeIndex = f.IndexOfType(type); if (typeIndex < 0) throw new ToolError("No type '" + type + "'."); }
            FoliageData data;
            FoliageService.TryGetData(e, out data);
            var before = Snapshot(data);
            int removed;
            if (clear_all) removed = FoliageService.ClearInstances(e, typeIndex);
            else
            {
                if (point == null || point.Length != 2) throw new ToolError("point is [x, z] (or clear_all).");
                removed = FoliageService.Erase(e, typeIndex, new Vector3(point[0], 0f, point[1]), Math.Max(0.1f, radius));
            }
            FoliageService.Save(e);
            PushUndo(e, "Erase foliage", before, Snapshot(data));
            ToolContext.UndoLabel = "erase " + removed + " foliage";
            Editor.Core.Viewport.EditorViewportSession.RequestResubmit();
            return new { foliage = SceneModel.ShortId(e), removed, total = FoliageService.InstanceCount(e) };
        }

        [McpServerTool(Name = "foliage_info", ReadOnly = true, Idempotent = true)]
        [Description("The foliage layers of the scene: their types with models and settings, instance counts, wind, data files.")]
        public static object FoliageInfoAll()
        {
            var scene = RequireScene();
            var list = new List<object>();
            foreach (var e in SceneModel.All(scene))
            {
                var f = e.GetComponent<Foliage>();
                if (f != null) list.Add(FoliageInfo(e, f));
            }
            return list;
        }

        // ================================================================== water

        [McpServerTool(Name = "create_water", Destructive = false)]
        [Description("Creates a body of water (#200): a square surface of size metres centred on position at position's height, following the terrain " +
                     "underneath (it fades at the shore, darkens with depth, foams along the bank, reflects the sky). Colours are linear rgb. One undo step.")]
        public static object CreateWater(
            [Description("Centre [x, y, z] — y is the water level")] float[] position,
            [Description("Edge length in metres (default 64)")] float size = 64f,
            [Description("Deep colour [r, g, b] (default a dark teal)")] float[] deep_color = null,
            [Description("Shallow colour [r, g, b] (default a turquoise)")] float[] shallow_color = null,
            [Description("Depth (m) at which the deep colour is reached (default 3.5)")] float absorption = 3.5f,
            [Description("Ripple strength 0..4 (default 0.35)")] float wave_height = 0.35f,
            [Description("Foam band width (m) along the bank (default 0.9)")] float foam_width = 0.9f,
            [Description("Metres between surface vertices (default 1)")] float cell_size = 1f,
            [Description("Entity name (default Water)")] string name = null,
            [Description("Only report")] bool dry_run = false)
        {
            var scene = RequireScene();
            var pos = SceneModel.Vec(position, "position") ?? throw new ToolError("position [x, y, z] is required.");
            if (size < 1f || size > 8192f) throw new ToolError("size must be 1–8192 metres.");
            if (dry_run) return new { would_create = name ?? "Water", size, level = pos.Y };
            var e = new GameEntity(scene, name ?? "Water");
            var w = new Water(e) { Size = size, Absorption = absorption, WaveHeight = wave_height, FoamWidth = foam_width, CellSize = cell_size };
            if (deep_color != null && deep_color.Length >= 3) { w.DeepR = deep_color[0]; w.DeepG = deep_color[1]; w.DeepB = deep_color[2]; }
            if (shallow_color != null && shallow_color.Length >= 3) { w.ShallowR = shallow_color[0]; w.ShallowG = shallow_color[1]; w.ShallowB = shallow_color[2]; }
            e.AddComponent(w);
            if (e.Transform != null) e.Transform.LocalPosition = new Editor.ECS.Vector3(pos.X, pos.Y, pos.Z);
            scene.AddEntity(e);
            ToolContext.UndoLabel = "water " + e.Name;
            SelectionService.Instance.SelectedEntity = e;
            Editor.Core.Viewport.EditorViewportSession.RequestResubmit();
            return WaterInfoOf(e, w);
        }

        [McpServerTool(Name = "water_info", ReadOnly = true, Idempotent = true)]
        [Description("The bodies of water in the scene: level, size, colours, and the surface height under optional world points [[x, z], ...].")]
        public static object WaterInfo([Description("World points [[x, z], ...] to probe (optional)")] float[][] points = null)
        {
            var scene = RequireScene();
            var list = new List<object>();
            foreach (var e in SceneModel.All(scene))
            {
                var w = e.GetComponent<Water>();
                if (w != null) list.Add(WaterInfoOf(e, w));
            }
            var probes = new List<object>();
            if (points != null)
                foreach (var p in points)
                {
                    if (p == null || p.Length != 2) { probes.Add(null); continue; }
                    float y;
                    probes.Add(Editor.Core.Services.Water.WaterService.TryHeight(p[0], p[1], out y) ? new { x = p[0], z = p[1], surface = (float?)y } : new { x = p[0], z = p[1], surface = (float?)null });
                }
            return new { water = list, probes };
        }

        private static object WaterInfoOf(GameEntity e, Water w)
        {
            var pos = e.Transform != null ? e.Transform.LocalPosition : new Editor.ECS.Vector3(0, 0, 0);
            return new
            {
                entity = SceneModel.ShortId(e), name = e.Name, centre = new[] { pos.X, pos.Y, pos.Z }, level = pos.Y, size = w.Size, cell_size = w.CellSize,
                deep_color = new[] { w.DeepR, w.DeepG, w.DeepB }, shallow_color = new[] { w.ShallowR, w.ShallowG, w.ShallowB }, absorption = w.Absorption,
                reflection = w.Reflection, roughness = w.Roughness, wave_scale = w.WaveScale, wave_speed = w.WaveSpeed, wave_height = w.WaveHeight, foam_width = w.FoamWidth,
                wet_vertices = Editor.Core.Services.Water.WaterService.WetVertices(e)
            };
        }

        /// <summary>A type as the tools take it.</summary>
        public sealed class FoliageTypeSpec
        {
            public string name { get; set; }
            public string model { get; set; }
            public bool tree { get; set; }
            public float? density { get; set; }
            public float? spacing { get; set; }
            public float? min_scale { get; set; }
            public float? max_scale { get; set; }
            public float? cull_distance { get; set; }
            public float? thin_distance { get; set; }
            public float? max_slope { get; set; }
            public bool? align_to_normal { get; set; }
            public int? collision { get; set; }
            public float? collision_radius { get; set; }
            public float? collision_height { get; set; }
            public float? wind { get; set; }
            public float? wind_height { get; set; }
            public bool? cutout { get; set; }
            public float? weight { get; set; }
        }

        // ================================================================== helpers

        private static Scene RequireScene() => ProjectData.Current?.ActiveScene ?? throw new ToolError("No scene is open.");

        private static GameEntity ResolveTerrain(string reference, float x, float z)
        {
            if (!string.IsNullOrWhiteSpace(reference))
            {
                var e = SceneModel.Resolve(reference) ?? throw new ToolError("No entity '" + reference + "'.");
                if (e.GetComponent<Terrain>() == null) throw new ToolError("'" + e.Name + "' has no Terrain component.");
                return e;
            }
            return TerrainService.FindAt(x, z) ?? throw new ToolError("No terrain under (" + x + ", " + z + ") — pass the terrain entity or create_terrain first.");
        }

        private static object Info(GameEntity e, Terrain t)
        {
            TerrainData data; float cell;
            bool has = TerrainService.TryGetData(e, out data, out cell);
            var pos = e.Transform != null ? e.Transform.LocalPosition : new Editor.ECS.Vector3(0, 0, 0);
            return new
            {
                entity = SceneModel.ShortId(e), name = e.Name, corner = new[] { pos.X, pos.Y, pos.Z }, size = t.Size, resolution = t.Resolution, cell_size = t.CellSize,
                min_height = has ? data.MinHeight : 0f, max_height = has ? data.MaxHeight : 0f, lod_distance = t.LodDistance, collision = t.Collision,
                layers = new[] { t.Layer0Material, t.Layer1Material, t.Layer2Material, t.Layer3Material },
                tiling = new[] { t.Layer0Tile, t.Layer1Tile, t.Layer2Tile, t.Layer3Tile }, data_file = t.DataPath
            };
        }

        private static object FoliageInfo(GameEntity e, Foliage f)
        {
            return new
            {
                entity = SceneModel.ShortId(e), name = e.Name, wind = f.Wind, view_distance_scale = f.ViewDistanceScale, data_file = f.DataPath,
                instances = FoliageService.InstanceCount(e),
                types = f.Types.Select((t, i) => new
                {
                    t.Name, model = t.MeshPath, instances = FoliageService.InstanceCount(e, i), density = t.Density, spacing = t.MinSpacing, scale = new[] { t.MinScale, t.MaxScale },
                    align_to_normal = t.AlignToNormal, max_slope = t.MaxSlope, cull_distance = t.CullDistance, thin_distance = t.ThinDistance,
                    collision = t.Collision, collision_radius = t.CollisionRadius, collision_height = t.CollisionHeight, wind = t.WindStrength, wind_height = t.WindHeight, cutout = t.Cutout, weight = t.Weight
                }).ToList()
            };
        }

        private static void Apply(FoliageType t, FoliageTypeSpec s)
        {
            if (s.density.HasValue) t.Density = Math.Max(0f, s.density.Value);
            if (s.spacing.HasValue) t.MinSpacing = Math.Max(0f, s.spacing.Value);
            if (s.min_scale.HasValue) t.MinScale = Math.Max(0.01f, s.min_scale.Value);
            if (s.max_scale.HasValue) t.MaxScale = Math.Max(0.01f, s.max_scale.Value);
            if (s.cull_distance.HasValue) t.CullDistance = Math.Max(1f, s.cull_distance.Value);
            if (s.thin_distance.HasValue) t.ThinDistance = Math.Max(0f, s.thin_distance.Value);
            if (s.max_slope.HasValue) t.MaxSlope = Math.Max(0f, Math.Min(89f, s.max_slope.Value));
            if (s.align_to_normal.HasValue) t.AlignToNormal = s.align_to_normal.Value;
            if (s.collision.HasValue) t.Collision = Math.Max(0, Math.Min(2, s.collision.Value));
            if (s.collision_radius.HasValue) t.CollisionRadius = Math.Max(0.01f, s.collision_radius.Value);
            if (s.collision_height.HasValue) t.CollisionHeight = Math.Max(0.05f, s.collision_height.Value);
            if (s.wind.HasValue) t.WindStrength = Math.Max(0f, s.wind.Value);
            if (s.wind_height.HasValue) t.WindHeight = Math.Max(0.05f, s.wind_height.Value);
            if (s.cutout.HasValue) t.Cutout = s.cutout.Value;
            if (s.weight.HasValue) t.Weight = Math.Max(0f, s.weight.Value);
        }

        private static Dictionary<string, List<FoliageInstance>> Snapshot(FoliageData data)
        {
            var d = new Dictionary<string, List<FoliageInstance>>(StringComparer.OrdinalIgnoreCase);
            if (data != null) foreach (var l in data.Layers) d[l.TypeName ?? ""] = l.Snapshot();
            return d;
        }

        private static void PushUndo(GameEntity e, string name, Dictionary<string, List<FoliageInstance>> before, Dictionary<string, List<FoliageInstance>> after)
        {
            Action Apply(Dictionary<string, List<FoliageInstance>> state) => () =>
            {
                FoliageData d;
                if (!FoliageService.TryGetData(e, out d)) return;
                foreach (var kv in state) d.Layer(kv.Key, true).Set(kv.Value);
                foreach (var l in d.Layers) if (!state.ContainsKey(l.TypeName ?? "")) l.Clear();
                FoliageService.MarkDirty(e);
                FoliageService.Save(e);
                Editor.Core.Viewport.EditorViewportSession.RequestResubmit();
            };
            try { Editor.Core.UndoRedo.UndoRedoManager.Instance.Execute(new Editor.Core.UndoRedo.Commands.ActionCommand(name, Apply(after), Apply(before)), false); } catch { }
        }

        private static float[] CropHeights(float[] full, int res, SampleRect r)
        {
            if (r.IsEmpty) return Array.Empty<float>();
            var o = new float[r.Width * r.Height];
            for (int z = r.Z0, k = 0; z <= r.Z1; z++)
                for (int x = r.X0; x <= r.X1; x++, k++) o[k] = full[z * res + x];
            return o;
        }

        private static byte[] CropSplat(byte[] full, int res, SampleRect r)
        {
            if (r.IsEmpty) return Array.Empty<byte>();
            var o = new byte[r.Width * r.Height * 4];
            for (int z = r.Z0, k = 0; z <= r.Z1; z++)
                for (int x = r.X0; x <= r.X1; x++, k += 4) Buffer.BlockCopy(full, (z * res + x) * 4, o, k, 4);
            return o;
        }
    }
}
