using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Foliage;
using Editor.Core.Services;
using Editor.Core.Services.Foliage;
using Editor.Core.Services.Terrain;
using Editor.ECS;
using Editor.ECS.Components.Rendering;
using NVec3 = System.Numerics.Vector3;
using FoliageComponent = Editor.ECS.Components.Rendering.Foliage;

namespace VortexEditor.Shell
{
    /// <summary>
    /// "oasis jungle" (authoring helper, <c>VORTEX_TEMPLATE_SETUP=1</c>): the overgrown corner of the oasis — a disc of the east
    /// bank where the date palms stand three and a half metres apart instead of six and a half, with small palms, quiver
    /// trees and shrubs as understorey, ferns on every square metre, dead fronds and litter everywhere, and lianas hanging
    /// from half of the crowns (tools/palm-gen.py --liana). Adds to the existing Oasis_Vegetation.vfoliage (the groves,
    /// lakeshore and desert stay as they are) and is repeatable: its own instances are removed first.
    /// </summary>
    internal static class OasisJungleSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("oasis jungle", Run);

        // the zone: the grove the scene opens on, on the east bank
        internal const float CentreX = 118f, CentreZ = -95f, Radius = 72f, Edge = 18f;
        const float WaterLevel = -6f;

        private static float Zone(float x, float z)
        {
            float d = (float)Math.Sqrt((x - CentreX) * (x - CentreX) + (z - CentreZ) * (z - CentreZ));
            if (d >= Radius) return 0f;
            if (d <= Radius - Edge) return 1f;
            float t = (Radius - d) / Edge;
            return t * t * (3f - 2f * t);
        }

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            if (Environment.GetEnvironmentVariable("VORTEX_TEMPLATE_SETUP") != "1") { log.Log("oasis jungle: authoring helper — skipped (set VORTEX_TEMPLATE_SETUP=1)"); return true; }
            var scene = ProjectData.Current?.ActiveScene;
            var ter = scene == null ? null : TemplateSetupSmoke.Find(scene, e => e.Name == "Oasis Terrain");
            var fol = scene == null ? null : TemplateSetupSmoke.Find(scene, e => e.Name == "Oasis Vegetation");
            var fc = fol?.GetComponent<FoliageComponent>();
            if (ter == null || fc == null) { log.LogError("oasis jungle: the scene needs the Oasis Terrain and the Oasis Vegetation layer"); return false; }
            FoliageData data;
            if (!FoliageService.TryGetData(fol, out data)) { log.LogError("oasis jungle: no foliage data"); return false; }

            // ---- the types the jungle adds (once) ----
            FoliageType Need(string name, Func<FoliageType> make)
            {
                foreach (var t in fc.Types) if (t.Name == name) return t;
                var nt = make(); fc.Types.Add(nt); return nt;
            }
            var lianaA = Need("Liana", () => Type("Liana", "Assets/Models/Palms/liana_a/liana_a.gltf", 0.8f, 1.2f, 0.6f, 180f, 0f, 0.25f, 9f, 50f, 0f));
            var lianaB = Need("Liana short", () => Type("Liana short", "Assets/Models/Palms/liana_b/liana_b.gltf", 0.8f, 1.2f, 0.6f, 180f, 0f, 0.25f, 7f, 50f, 0f));
            var fernC = Need("Jungle fern", () => Type("Jungle fern", "Assets/Models/Palms/fern_c/fern_c.gltf", 0.9f, 1.5f, 1.1f, 160f, 60f, 0.12f, 1.3f, 50f, 0.03f, align: true));

            // ---- a repeatable pass: everything inside the zone of the layers the jungle fills goes first (the base scene's
            // grove there included — the jungle re-plants the whole disc denser), the liana layers entirely ----
            int removed = 0;
            var filled = new HashSet<string> { "Liana", "Liana short", "Jungle fern", "Palm straight", "Palm bend", "Palm dual", "Palm dual_bend", "Palm v2", "Palm trio",
                "Quiver tree 1", "Quiver tree 2", "Rooibos", "Didelta", "Searsia burchellii", "Searsia lucida", "Palm litter", "Palm litter pile", "Dry leaf", "Grass" };
            foreach (var l in data.Layers)
            {
                if (l == null || !filled.Contains(l.TypeName)) continue;
                int before = l.Instances.Count;
                if (l.TypeName.StartsWith("Liana")) l.Instances.Clear();
                else l.Instances.RemoveAll(inst => Zone(inst.Position.X, inst.Position.Z) > 0f);
                removed += before - l.Instances.Count;
                l.Touch();
            }
            log.Log("oasis jungle: " + removed + " earlier instances inside the zone removed");
            // the floor types draw closer and thin sooner everywhere: a jungle floor at 160 m is thousands of invisible fronds
            foreach (var t in fc.Types)
            {
                if (t.Name == "Searsia burchellii" || t.Name == "Searsia lucida" || t.Name == "Jungle fern") { t.CullDistance = 100f; t.ThinDistance = 28f; }
                else if (t.Name == "Palm litter" || t.Name == "Palm litter pile" || t.Name == "Dry leaf") { t.CullDistance = 70f; t.ThinDistance = 22f; }
                else if (t.Name == "Rooibos") { t.CullDistance = 80f; t.ThinDistance = 30f; }
                else if (t.Name == "Grass") { t.CullDistance = 70f; t.ThinDistance = 22f; }
            }

            var types = new Dictionary<string, FoliageType>();
            foreach (var t in fc.Types) types[t.Name] = t;
            FoliageType Of(string n) => types.TryGetValue(n, out var t) ? t : null;

            int seed = 4242, total = 0;
            float z0 = CentreZ - Radius, z1 = CentreZ + Radius, x0 = CentreX - Radius, x1 = CentreX + Radius;

            // a scatter inside the zone: prob(x, z, heightAboveWater, normalY) × zone falloff
            int Scatter(FoliageType type, int candidates, float minScale, float maxScale, float spacing, float palmSpacing, Func<float, float, float, float, float> prob)
            {
                if (type == null) return 0;
                var rng = new Random(seed++);
                var layer = data.Layer(type.Name, true);
                float slopeCos = (float)Math.Cos(type.MaxSlope * Math.PI / 180.0);
                int placed = 0;
                for (int c = 0; c < candidates; c++)
                {
                    float x = x0 + (float)rng.NextDouble() * (x1 - x0), z = z0 + (float)rng.NextDouble() * (z1 - z0);
                    float zf = Zone(x, z);
                    if (zf <= 0f) continue;
                    float h; NVec3 n;
                    if (!TerrainService.TryHeight(ter, x, z, out h) || !TerrainService.TryNormal(ter, x, z, out n)) continue;
                    if (n.Y < slopeCos) continue;
                    float hw = h - WaterLevel;
                    if (hw < 0.3f) continue;   // never in the water
                    float p = prob(x, z, hw, n.Y) * zf;
                    if (p <= 0f || rng.NextDouble() >= p) continue;
                    var pos = new NVec3(x, h, z);
                    if (spacing > 0f && layer.AnyWithin(pos, spacing)) continue;
                    if (palmSpacing > 0f && TooCloseToAnyPalm(data, pos, palmSpacing)) continue;
                    float scale = minScale + (float)rng.NextDouble() * (maxScale - minScale);
                    float yaw = (float)(rng.NextDouble() * 360.0), tilt = (float)(rng.NextDouble() * type.MaxTilt), tiltDir = (float)(rng.NextDouble() * 360.0);
                    var rot = FoliageData.Orientation(n, type.AlignToNormal, yaw, tilt, tiltDir);
                    pos.Y -= type.Sink * scale;
                    layer.Add(new FoliageInstance(pos, rot, scale));
                    placed++;
                }
                total += placed;
                log.Log("oasis jungle: " + type.Name + " +" + placed);
                return placed;
            }

            // ---- the canopy: date palms close, every kind, tall and small — with holes: a closed roof of crowns made a cellar
            // of the floor (measured: the floor stayed black at twice the sky light); the sun has to reach it in shafts and
            // dapples, so a clearing noise leaves a third of the disc with gaps between the crowns ----
            Func<float, float, float> holes = (x, z) => { float g = OasisSetupSmoke.Fbm(x * 0.05f + 11f, z * 0.05f + 3f, 3); return g < 0.42f ? 0.15f : (g < 0.5f ? 0.45f : 1f); };
            Func<float, float, float, float, float> canopy = (x, z, hw, ny) => hw > 16f || ny < 0.82f ? 0f : 0.9f * holes(x, z);
            Scatter(Of("Palm straight"), 2600, 0.85f, 1.2f, 4.4f, 4.4f, canopy);
            Scatter(Of("Palm bend"), 2600, 0.8f, 1.15f, 4.4f, 4.4f, canopy);
            Scatter(Of("Palm dual"), 2200, 0.9f, 1.3f, 4.4f, 4.4f, canopy);
            Scatter(Of("Palm dual_bend"), 1800, 0.6f, 0.9f, 4.0f, 4.0f, canopy);     // the smaller date palms under the tall ones
            Scatter(Of("Palm v2"), 1800, 0.55f, 0.85f, 4.0f, 4.0f, canopy);
            Scatter(Of("Palm trio"), 1600, 0.28f, 0.42f, 3.8f, 3.4f, canopy);         // the Yughues palm at 5–8 m: a second storey
            // ---- the understorey: quiver trees, shrubs ----
            Scatter(Of("Quiver tree 1"), 900, 1.0f, 1.6f, 4f, 2.2f, (x, z, hw, ny) => hw > 14f || ny < 0.85f ? 0f : 0.45f);
            Scatter(Of("Quiver tree 2"), 900, 1.0f, 1.6f, 4f, 2.2f, (x, z, hw, ny) => hw > 14f || ny < 0.85f ? 0f : 0.4f);
            Scatter(Of("Rooibos"), 1400, 1.0f, 1.7f, 2.2f, 0.9f, (x, z, hw, ny) => ny < 0.8f ? 0f : 0.5f);     // 10k-triangle bushes: a few hundred, not thousands
            Scatter(Of("Didelta"), 900, 0.8f, 1.3f, 4.5f, 1.2f, (x, z, hw, ny) => ny < 0.8f ? 0f : 0.45f);
            // ---- the floor: ferns everywhere, litter everywhere ----
            Func<float, float, float, float, float> floor = (x, z, hw, ny) => ny < 0.78f ? 0f : 0.95f;
            Scatter(Of("Searsia burchellii"), 7000, 0.9f, 1.5f, 1.5f, 0.6f, floor);    // ferns 1.5 m apart (1.0 cost 7 M triangles)
            Scatter(Of("Searsia lucida"), 7000, 0.8f, 1.4f, 1.5f, 0.6f, floor);
            Scatter(fernC, 6000, 0.9f, 1.6f, 1.6f, 0.6f, floor);
            Scatter(Of("Palm litter"), 9000, 0.8f, 1.3f, 0.9f, 0f, floor);
            Scatter(Of("Palm litter pile"), 5000, 0.8f, 1.2f, 1.8f, 0f, floor);
            Scatter(Of("Dry leaf"), 5000, 1.0f, 1.6f, 1.0f, 0f, floor);
            Scatter(Of("Grass"), 4000, 1.2f, 2.2f, 0.9f, 0f, (x, z, hw, ny) => ny < 0.8f ? 0f : 0.5f);

            // ---- the lianas: hanging from half of the zone's palm crowns, scaled to the crown height ----
            {
                var rng = new Random(seed++);
                var la = data.Layer(lianaA.Name, true); var lb = data.Layer(lianaB.Name, true);
                int hung = 0;
                foreach (var layer in data.Layers)
                {
                    if (layer == null || !layer.TypeName.StartsWith("Palm") || layer.TypeName.StartsWith("Palm litter")) continue;
                    var pt = Of(layer.TypeName);
                    float modelHeight = layer.TypeName == "Palm bend" || layer.TypeName == "Palm v2" ? 17.5f : layer.TypeName == "Palm dual" ? 11.5f : layer.TypeName == "Palm trio" ? 19f : 14.5f;
                    foreach (var inst in layer.Instances)
                    {
                        float zf = Zone(inst.Position.X, inst.Position.Z);
                        if (zf <= 0f || rng.NextDouble() > 0.55 * zf) continue;
                        float crown = modelHeight * inst.Scale * 0.82f;            // where the fronds start
                        bool shortOne = crown < 8.5f || rng.NextDouble() < 0.35;
                        float model = shortOne ? 7f : 9f;
                        float scale = Math.Max(0.5f, Math.Min(2.2f, crown / model));
                        float ang = (float)(rng.NextDouble() * Math.PI * 2), off = 0.35f + (float)rng.NextDouble() * 0.5f;
                        var pos = new NVec3(inst.Position.X + (float)Math.Cos(ang) * off, inst.Position.Y, inst.Position.Z + (float)Math.Sin(ang) * off);
                        float h; if (TerrainService.TryHeight(ter, pos.X, pos.Z, out h)) pos.Y = h;
                        var rot = FoliageData.Orientation(new NVec3(0, 1, 0), false, (float)(rng.NextDouble() * 360.0), 0f, 0f);
                        (shortOne ? lb : la).Add(new FoliageInstance(pos, rot, scale));
                        hung++;
                    }
                }
                total += hung;
                log.Log("oasis jungle: " + hung + " lianas hung");
            }

            fc.Touch();
            FoliageService.Save(fol);
            scene.IsDirty = true;
            try { SceneService.Instance.SaveScene(scene); } catch (Exception ex) { log.LogWarning("oasis jungle: scene save — " + ex.Message); }
            log.Log("oasis jungle: " + total + " instances in the overgrown corner at (" + CentreX + ", " + CentreZ + ") r " + Radius + " m — saved");
            await Task.Delay(50);
            return true;
        }

        private static bool TooCloseToAnyPalm(FoliageData data, NVec3 pos, float spacing)
        {
            foreach (var l in data.Layers)
                if (l != null && l.TypeName.StartsWith("Palm") && !l.TypeName.StartsWith("Palm litter") && l.AnyWithin(pos, spacing)) return true;
            return false;
        }

        private static FoliageType Type(string name, string mesh, float minScale, float maxScale, float spacing, float cull, float thin, float windStrength, float windHeight, float maxSlope, float sink, bool align = false)
        {
            var t = FoliageComponent.DefaultType(name, mesh, false);
            t.MinScale = minScale; t.MaxScale = maxScale; t.MinSpacing = spacing; t.CullDistance = cull; t.ThinDistance = thin;
            t.WindStrength = windStrength; t.WindHeight = windHeight; t.WindSpeed = 0.6f; t.Collision = 0;
            t.AlignToNormal = align; t.MaxSlope = maxSlope; t.Sink = sink; t.Cutout = true; t.MaxTilt = align ? 0f : 2f;
            return t;
        }
    }
}
