using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Editor.Core.Services.Terrain;
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
