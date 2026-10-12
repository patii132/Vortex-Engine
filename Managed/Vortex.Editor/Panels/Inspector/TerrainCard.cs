using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Editor.Core.Services.Terrain;
using Editor.Core.Terrain;
using VortexEditor.Shell;
using static VortexEditor.Panels.Inspector.PropertyRows;

namespace VortexEditor.Panels.Inspector
{
    /// <summary>Inspector card of the Terrain component (#124): size / resolution / LOD / collision, the four texture layers
    /// (a .vmat + its tiling each), and the sculpt / paint tools whose brush works in the viewport while the terrain is
    /// selected (Shift inverts raise / lower, Ctrl smooths; every stroke is one undo step and saves the data file).</summary>
    internal static class TerrainCard
    {
        private static readonly string[] MaterialPatterns = { "*.vmat" };
        private static readonly string[] ResolutionLabels = { "33", "65", "129", "257", "513", "1025", "2049" };
        private static readonly int[] Resolutions = { 33, 65, 129, 257, 513, 1025, 2049 };
        private static readonly string[] ToolLabels = { "Select", "Raise", "Lower", "Smooth", "Flatten", "Paint" };
        private static readonly string[] LayerLabels = { "Layer 1", "Layer 2", "Layer 3", "Layer 4" };

        [ModuleInitializer]
        internal static void Register()
        {
            ComponentEditors.Custom[typeof(Editor.ECS.Components.Rendering.Terrain)] = (c, e) => Rows((Editor.ECS.Components.Rendering.Terrain)c, e);
        }

        private static IEnumerable<Control> Rows(Editor.ECS.Components.Rendering.Terrain t, Editor.ECS.GameEntity entity)
        {
            yield return Row("Size", FloatBox(() => t.Size, v => { t.Size = v; Touch(); }, 1, 1f, 16384f, "0.#"), "Edge length in metres (square, from the entity's position along +X / +Z; the entity's scale is ignored)");
            yield return Row("Resolution", Choice(() => Array.IndexOf(Resolutions, t.Resolution) < 0 ? 2 : Array.IndexOf(Resolutions, t.Resolution),
                i => { t.Resolution = Resolutions[Math.Max(0, Math.Min(Resolutions.Length - 1, i))]; Touch(); }, ResolutionLabels),
                "Height samples per side; changing it resamples the sculpting. 129 on 128 m = one sample per metre");
            yield return Row("LOD distance", FloatBox(() => t.LodDistance, v => { t.LodDistance = v; Touch(); }, 4, 4f, null, "0"), "Metres at which chunks drop to half the vertices (a quarter at twice that)");
            yield return Row("Collision", Bool(() => t.Collision, v => { t.Collision = v; Touch(); }), "Jolt height field + the character's collision world, and navmesh ground");
            yield return Row("Navigation", Bool(() => t.Navigation, v => { t.Navigation = v; Touch(); }), "Count as navmesh ground (with Collision) — off for a far backdrop terrain");
            yield return Row("Data file", DataFileRow(t, entity), "The .vterrain with the heights and the splat map; saved after every stroke and with the scene");

            yield return Section("Layers");
            for (int i = 0; i < 4; i++)
            {
                int li = i;
                yield return Row(LayerLabels[li], AssetPath(() => t.LayerMaterial(li), v => { t.SetLayerMaterial(li, string.IsNullOrEmpty(v) ? null : v.Replace('\\', '/')); Touch(); }, "Material", MaterialPatterns,
                    () => AssetPickerDialog.Pick("Material", MaterialPatterns, TerrainService.ResolvePath(t.LayerMaterial(li)))),
                    "A .vmat: its albedo, normal and roughness maps tile across the terrain where this layer is painted (empty = a flat default colour)");
                yield return Row("  tiling", FloatBox(() => t.LayerTile(li), v => { t.SetLayerTile(li, v); Touch(); }, 0.5, 0.1f, 1000f, "0.##"), "Metres per texture repeat");
            }

            yield return Section("Heightmap");
            yield return RowTop("Preview", PreviewRow(entity), "The heights as a shaded relief (north-west light); updates after every stroke");
            yield return Row("Import range", ImportRangeRow(), "Lowest and highest height (m) a heightmap picture maps to: black → low, white → high");
            var import = Ghost("Import heightmap…", () => _ = ImportHeightmap(entity), "Read a PNG (8 / 16 bit grey or colour) or a RAW 16-bit square into this terrain, resampled to its resolution. One undo step");
            var export = Ghost("Export heightmap…", () => _ = ExportHeightmap(entity), "Write the heights as a 16-bit grey PNG or RAW 16 (lowest = black, highest = white) for World Machine, Gaea, Blender, Photoshop…");
            yield return Row("", Actions(import, export));
            yield return Section("Generate");
            yield return Row("Shape", Choice(() => _genPreset, v => _genPreset = v, "Flat", "Hills", "Mountains", "Dunes", "Island", "Canyon"), "A landscape from noise as a starting point to sculpt on");
            yield return Row("Amplitude", FloatBox(() => _genAmplitude, v => _genAmplitude = Math.Max(0f, v), 1, 0f, 4000f, "0.#"), "Height of the features in metres");
            yield return Row("Wavelength", FloatBox(() => _genWavelength, v => _genWavelength = Math.Max(1f, v), 4, 1f, 10000f, "0.#"), "Width of the features in metres (dunes: ridge spacing; canyon: its width)");
            yield return Row("Base height", FloatBox(() => _genBase, v => _genBase = v, 1, null, null, "0.#"), "Where flat ground sits (m)");
            yield return Row("Seed", IntBox(() => _genSeed, v => _genSeed = v, 0, 999999), "Another seed, another landscape of the same kind");
            var gen = Ghost("Generate", () => Generate(entity, t), "Replace every height with the shape above (the splat map stays). One undo step");
            yield return Row("", Actions(gen));

            yield return Section("Sculpt & paint");
            yield return Row("Tool", Choice(() => (int)TerrainToolService.Tool, v => { TerrainToolService.SetTool((TerrainTool)v); Refresh(); }, ToolLabels),
                "Raise / Lower / Smooth / Flatten the heights or Paint a layer by dragging in the viewport. Shift inverts raise and lower, Ctrl smooths. Select = the normal gizmo");
            yield return Row("Radius", SliderRow(() => TerrainToolService.Radius, v => TerrainToolService.Radius = v, 0.5, 64, "0.0 m"), "Brush radius in metres");
            yield return Row("Strength", SliderRow(() => TerrainToolService.Strength, v => TerrainToolService.Strength = v, 0.02, 1, "0.00"), "How much one stroke step changes");
            yield return Row("Hardness", SliderRow(() => TerrainToolService.Hardness, v => TerrainToolService.Hardness = v, 0, 0.95, "0.00"), "The flat part of the brush: 0 a smooth dome, 0.9 almost a stamp");
            yield return Row("Paint layer", Choice(() => TerrainToolService.PaintLayer, v => TerrainToolService.PaintLayer = v, LayerLabels), "The layer the Paint tool paints");
            yield return Row("Flatten to", FlattenRow(), "Flatten pulls the ground to the height where the stroke starts, or to this fixed world height");

            var flat = Ghost("Level everything", () =>
            {
                Editor.Core.Terrain.TerrainData data; float cell;
                if (!TerrainService.TryGetData(entity, out data, out cell)) return;
                var before = (float[])data.Heights.Clone();
                Array.Clear(data.Heights, 0, data.Heights.Length);
                var all = new Editor.Core.Terrain.SampleRect { X0 = 0, Z0 = 0, X1 = data.Resolution - 1, Z1 = data.Resolution - 1 };
                TerrainService.MarkDirty(entity, all, true, false);
                try
                {
                    Editor.Core.UndoRedo.UndoRedoManager.Instance.Execute(new Editor.Core.UndoRedo.Commands.ActionCommand("Level terrain",
                        () => { Array.Clear(data.Heights, 0, data.Heights.Length); TerrainService.MarkDirty(entity, all, true, false); TerrainService.Save(entity); Touch(); },
                        () => { Array.Copy(before, data.Heights, before.Length); TerrainService.MarkDirty(entity, all, true, false); TerrainService.Save(entity); Touch(); }), false);
                }
                catch { }
                TerrainService.Save(entity);
                Touch();
            }, "Set every height to 0 (one undo step)");
            var save = Ghost("Save now", () => { TerrainService.Save(entity); Refresh(); }, "Write the heights and the splat map to the data file");
            yield return Row("", Actions(flat, save));
            yield return Hint("Drag on the terrain in the viewport with a tool chosen above. From scripts: Terrain.Height(x, z), Terrain.Deform(hit.Point, 1.2f, 0.35f) for a crater, Terrain.Paint(...).");
        }

        // ---------------------------------------------------------------- heightmap + generator

        private static int _genPreset = 1;
        private static float _genAmplitude = 40f, _genWavelength = 120f, _genBase = 0f;
        private static int _genSeed = 7;
        private static float _importLow = 0f, _importHigh = 100f;

        private static Control PreviewRow(Editor.ECS.GameEntity entity)
        {
            var img = new Image { Width = 160, Height = 160, Stretch = Avalonia.Media.Stretch.Uniform, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
            var info = new TextBlock { FontSize = 11, Opacity = 0.75, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            var panel = new StackPanel();
            panel.Children.Add(img); panel.Children.Add(info);
            int lastVersion = -1;
            void Update()
            {
                TerrainData data; float cell;
                if (!TerrainService.TryGetData(entity, out data, out cell)) { info.Text = "no terrain data"; return; }
                // a cheap fingerprint of the heights (64 samples + the range): redraw only when something moved
                int v = data.Heights.Length ^ (int)(data.MinHeight * 97f) ^ (int)(data.MaxHeight * 131f);
                for (int i = 0; i < 64; i++) v = v * 31 + (int)(data.Heights[(int)((long)i * (data.Heights.Length - 1) / 63)] * 100f);
                if (v == lastVersion) return;
                lastVersion = v;
                int stride; var px = HeightmapIO.ShadedRelief(data, 160, cell, out stride);
                var wb = new WriteableBitmap(new PixelSize(160, 160), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                using (var fb = wb.Lock())
                    for (int y = 0; y < 160; y++) System.Runtime.InteropServices.Marshal.Copy(px, y * stride, IntPtr.Add(fb.Address, y * fb.RowBytes), stride);
                img.Source = wb;
                info.Text = HeightmapIO.Describe(data);
            }
            Update();
            Refreshers[panel] = Update;
            return panel;
        }

        private static Control ImportRangeRow()
        {
            var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(FloatBox(() => _importLow, v => _importLow = v, 1, null, null, "0.#"));
            panel.Children.Add(new TextBlock { Text = "…", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            panel.Children.Add(FloatBox(() => _importHigh, v => _importHigh = v, 1, null, null, "0.#"));
            panel.Children.Add(new TextBlock { Text = "m", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Opacity = 0.7 });
            return panel;
        }

        /// <summary>Replace every height (one undo step), rebuild, save, refresh — the import and the generator share it.</summary>
        internal static void ReplaceHeights(Editor.ECS.GameEntity entity, string label, Action<TerrainData> fill)
        {
            TerrainData data; float cell;
            if (!TerrainService.TryGetData(entity, out data, out cell)) { EditorCommands.Toast("The terrain has no data"); return; }
            var before = (float[])data.Heights.Clone();
            fill(data);
            var after = (float[])data.Heights.Clone();
            var all = new SampleRect { X0 = 0, Z0 = 0, X1 = data.Resolution - 1, Z1 = data.Resolution - 1 };
            void Put(float[] h) { Array.Copy(h, data.Heights, h.Length); data.MarkChanged(); TerrainService.MarkDirty(entity, all, true, false); TerrainService.Save(entity); Touch(); Refresh(); }
            try
            {
                Editor.Core.UndoRedo.UndoRedoManager.Instance.Execute(new Editor.Core.UndoRedo.Commands.ActionCommand(label, () => Put(after), () => Put(before)), false);
            }
            catch { }
            Put(after);
        }

        private static void Generate(Editor.ECS.GameEntity entity, Editor.ECS.Components.Rendering.Terrain t)
        {
            var preset = (TerrainPreset)_genPreset;
            ReplaceHeights(entity, "Generate terrain (" + preset + ")", d => TerrainGenerator.Generate(d, t.Size, preset, _genAmplitude, _genWavelength, _genBase, _genSeed));
            EditorCommands.Toast(preset + " terrain generated — sculpt from here");
        }

        private static async Task ImportHeightmap(Editor.ECS.GameEntity entity)
        {
            try
            {
                var top = EditorCommands.ActiveWindow();
                if (top == null) return;
                var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Import heightmap",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType("Heightmap (PNG, RAW 16)") { Patterns = new[] { "*.png", "*.raw", "*.r16" } } }
                });
                string path = files != null && files.Count > 0 ? files[0].TryGetLocalPath() : null;
                if (string.IsNullOrEmpty(path)) return;
                var img = HeightmapIO.Load(path);
                float low = Math.Min(_importLow, _importHigh), high = Math.Max(_importLow, _importHigh);
                if (high - low < 0.001f) high = low + 1f;
                ReplaceHeights(entity, "Import heightmap", d => HeightmapIO.Apply(d, img, low, high));
                EditorCommands.Toast("Heightmap " + Path.GetFileName(path) + " (" + img.Width + "×" + img.Height + ", " + img.BitDepth + " bit) imported: " + low.ToString("0.#") + " … " + high.ToString("0.#") + " m");
            }
            catch (Exception ex) { EditorCommands.Fail("Import heightmap", ex); }
        }

        private static async Task ExportHeightmap(Editor.ECS.GameEntity entity)
        {
            try
            {
                TerrainData data; float cell;
                if (!TerrainService.TryGetData(entity, out data, out cell)) { EditorCommands.Toast("The terrain has no data"); return; }
                var top = EditorCommands.ActiveWindow();
                if (top == null) return;
                var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export heightmap",
                    SuggestedFileName = (entity?.Name ?? "Terrain").Replace(' ', '_') + "_heightmap.png",
                    DefaultExtension = "png",
                    FileTypeChoices = new[] { new FilePickerFileType("PNG, 16-bit grey") { Patterns = new[] { "*.png" } }, new FilePickerFileType("RAW 16-bit") { Patterns = new[] { "*.raw", "*.r16" } } }
                });
                string path = file?.TryGetLocalPath();
                if (string.IsNullOrEmpty(path)) return;
                float low, high;
                byte[] bytes = Path.GetExtension(path).ToLowerInvariant() == ".png" ? HeightmapIO.ToPng16(data, out low, out high) : HeightmapIO.ToRaw16(data, out low, out high);
                File.WriteAllBytes(path, bytes);
                EditorCommands.Toast("Heightmap written: " + Path.GetFileName(path) + " (" + data.Resolution + "², black = " + low.ToString("0.#") + " m, white = " + high.ToString("0.#") + " m)");
            }
            catch (Exception ex) { EditorCommands.Fail("Export heightmap", ex); }
        }

        private static Control DataFileRow(Editor.ECS.Components.Rendering.Terrain t, Editor.ECS.GameEntity entity)
        {
            var tb = new TextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
            Func<string> text = () => string.IsNullOrEmpty(t.DataPath) ? "(saved with the first stroke: " + TerrainService.DefaultDataPath(entity) + ")" : t.DataPath + (TerrainService.HasUnsavedChanges(entity) ? "  •" : "");
            tb.Text = text();
            Refreshers[tb] = () => tb.Text = text();
            return tb;
        }

        private static Control FlattenRow()
        {
            var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(FloatBox(() => TerrainToolService.FlattenHeight, v => TerrainToolService.FlattenHeight = v, 0.5, null, null, "0.##"));
            panel.Children.Add(Check("fixed", () => TerrainToolService.FlattenFixed, v => TerrainToolService.FlattenFixed = v, "Use the height above instead of the stroke's start"));
            return panel;
        }

        private static void Touch()
        {
            ComponentEditors.Dirty();
            try { Editor.Core.Viewport.EditorViewportSession.RequestResubmit(); } catch { }
        }

        private static void Refresh()
        {
            foreach (var kv in Refreshers) { try { kv.Value(); } catch { } }
            try { Editor.Core.Viewport.EditorViewportSession.RequestResubmit(); } catch { }
        }
    }
}
