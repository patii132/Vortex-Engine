using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Foliage;
using Editor.Core.Services;
using Editor.Core.Services.AI;
using Editor.Core.Services.Foliage;
using Editor.Core.Services.Terrain;
using Editor.Core.Terrain;
using Editor.Core.Viewport;
using Editor.ECS;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;
using Editor.ECS.Components.Scripting;
using NVec3 = System.Numerics.Vector3;
using FoliageComponent = Editor.ECS.Components.Rendering.Foliage;
using TerrainComponent = Editor.ECS.Components.Rendering.Terrain;
using WaterComponent = Editor.ECS.Components.Rendering.Water;

namespace VortexEditor.Shell
{
    /// <summary>
    /// A template-authoring helper, not a check: with <c>VORTEX_TEMPLATE_SETUP=1</c> it builds the OPEN WORLD template's
    /// first biome — a desert oasis the way the reference looks: a 512 m dune field under a real desert HDRI (the sun of the
    /// scene is read from the picture), two turquoise lakes in a basin, date palms along the shores, trees and reeds on the
    /// damp ground, desert shrubs, quiver trees and succulents on the gravel plains, boulders, a rocky escarpment along the
    /// north, haze. Everything is engine data: a Terrain (#124) sculpted by code, two Water surfaces (#200), one Foliage
    /// entity (#125) whose instances are scattered by biome rules (height above the water, slope, distance to the oasis,
    /// noise), the scene settings. Idempotent: an existing "Oasis" scene is replaced. Run against a copy of the project with
    /// <c>--scene=Range</c>; the scene, the .vterrain and the .vfoliage are saved into the project.
    /// </summary>
    internal static class OasisSetupSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("oasis setup", Run);

        [ModuleInitializer]
        internal static void RegisterCapture() => SmokeRegistry.Add("oasis capture", Capture);

        // ---- the world ----
        const float Size = 512f;                 // metres, centred on the origin
        const int Resolution = 513;              // 1 m cells
        const float WaterLevel = -6f;            // the lakes' surface (the plain is ~0)
        const float FarSize = 4096f;             // the far dunes: to the horizon, 8 m cells
        const int FarResolution = 513;
        static readonly NVec3 LakeA = new NVec3(-30f, 0f, 20f);    // the west lake (bigger)
        static readonly NVec3 LakeB = new NVec3(70f, 0f, -40f);    // the east lake
        const float LakeAR = 78f, LakeBR = 64f;  // basin radii
        const float LakeADepth = 11f, LakeBDepth = 10f;

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            if (Environment.GetEnvironmentVariable("VORTEX_TEMPLATE_SETUP") != "1") { log.Log("oasis setup: authoring helper — skipped (set VORTEX_TEMPLATE_SETUP=1)"); return true; }
            var project = ProjectData.Current;
            if (project == null) { log.LogError("oasis setup: no project"); return false; }
            if (!Editor.DllWrapper.VortexAPI.TerrainApiAvailable) { log.LogError("oasis setup: the engine library has no terrain exports"); return false; }

            // ---- the Tactical player rig (weapons, HUD, controller) becomes a prefab the oasis instantiates ----
            string rigPrefab = null;
            // the rig comes from the Range scene (the Tactical template's), never from a previous Oasis: taking the player of
            // whichever scene was active re-saved an oasis player — with its Footprints script and its weather children — as
            // the prefab, and every further run added one more of each (19 of everything after 19 runs, 2026-10-12)
            Scene range = null;
            foreach (var sc in project.Scenes) if (sc != null && string.Equals(sc.Name, "Range", StringComparison.OrdinalIgnoreCase)) { range = sc; break; }
            if (range == null) range = project.ActiveScene;
            if (range != null && !range.IsLoaded) { try { range.Load(); } catch { } }
            var rangePlayer = range == null ? null : TemplateSetupSmoke.Find(range, e => e.Name == "Player" || e.Tag == "Player");
            if (rangePlayer != null)
            {
                try
                {
                    var prefabDir = Path.Combine(project.Path, "Assets", "Prefabs");
                    Directory.CreateDirectory(prefabDir);
                    SceneService.Instance.SaveEntityAsPrefab(rangePlayer, Path.Combine(prefabDir, "TacticalPlayer.ventity"));
                    rigPrefab = "Assets/Prefabs/TacticalPlayer.ventity";
                }
                catch (Exception ex) { log.LogWarning("oasis setup: could not save the player rig as a prefab — " + ex.Message); }
            }

            // ---- the scene (a previous Oasis goes first) ----
            Scene old = null;
            foreach (var s in project.Scenes) if (s != null && s.Name == "Oasis") old = s;
            if (old != null)
            {
                Scene other = null;
                foreach (var s in project.Scenes) if (s != null && s != old) { other = s; break; }
                if (project.ActiveScene == old && other != null) EditorSession.Instance.ActivateScene(other);
                try { old.DeactivateEntities(); old.ReleaseEngineScene(); } catch { }
                project.RemoveScene(old);
                try { if (File.Exists(old.FilePath)) File.Delete(old.FilePath); } catch { }
                log.Log("oasis setup: replaced the previous Oasis scene");
            }
            // the data files of a previous run go too: the foliage service would load and ADD to them
            foreach (var rel in new[] { "Assets/Terrain/Oasis_Terrain.vterrain", "Assets/Terrain/Oasis_Far.vterrain", "Assets/Foliage/Oasis_Vegetation.vfoliage" })
            {
                try { var f = Path.Combine(project.Path, rel); if (File.Exists(f)) File.Delete(f); } catch { }
            }
            var scene = new Scene(project, "Oasis");
            project.AddScene(scene);
            EditorSession.Instance.ActivateScene(scene);
            await SmokeRegistry.Settle(300);

            // ---- the sun: where the HDRI has it (goegap: azimuth 218.8°, elevation 46.3° — tools/hdri-analyze.py) ----
            var toSun = new Vector3(0.432f, 0.723f, 0.539f);
            float sunYaw, sunPitch;
            TransformMath.LookAngles(new Vector3(0f, 0f, 0f), new Vector3(-toSun.X, -toSun.Y, -toSun.Z), out sunYaw, out sunPitch);
            var sun = scene.CreateEntity("Sun");
            // the late afternoon of the syferfontein pure sky (18.2° up, azimuth 216° — tools/hdri-analyze.py): long palm shadows,
            // the sun's glow in the haze; the goegap noon stays the "noon" weather preset
            sun.Transform.LocalRotation = new Vector3(18.19f, -144.05f, 0f);
            sun.AddComponentDirect(new Light(sun, LightType.Directional) { Intensity = 8f, ColorR = 1f, ColorG = 0.84f, ColorB = 0.62f, ShadowType = ShadowType.Soft });   // at 18° a flat floor gets only 0.31 of the sun — the ambient has to stay well under that or the shade is as bright as the sun (it was, at 5.5 / 0.9). A late sun, but not orange: the sand must stay golden, not pink

            // ---- the sky: the desert HDRI; the gradient colours are its environment (reflections, the lakes' sky) ----
            var sky = scene.CreateEntity("Sky");
            sky.AddComponentDirect(new Skybox(sky)
            {
                IsEnabled = true, SkyboxType = SkyboxType.Texture, TexturePath = "Assets/Skies/syferfontein_18d_clear_puresky_2k.hdr", Exposure = 0.4f, AmbientIntensity = 0.55f,
                TopColorR = 0.25f, TopColorG = 0.42f, TopColorB = 0.85f,
                HorizonColorR = 0.95f, HorizonColorG = 0.88f, HorizonColorB = 0.75f,
                BottomColorR = 0.60f, BottomColorG = 0.50f, BottomColorB = 0.35f
            });

            // ---- the terrain ----
            var ter = scene.CreateEntity("Oasis Terrain");
            ter.Transform.LocalPosition = new Vector3(-Size * 0.5f, 0f, -Size * 0.5f);
            var tc = new TerrainComponent(ter)
            {
                Size = Size, Resolution = Resolution, LodDistance = 140f, Collision = true,
                Layer0Material = "Assets/Materials/aerial_sand.vmat", Layer0Tile = 8f,
                Layer1Material = "Assets/Materials/damp_sand.vmat", Layer1Tile = 4f,
                Layer2Material = "Assets/Materials/rock_face_03.vmat", Layer2Tile = 9f,
                Layer3Material = "Assets/Materials/grass_ground.vmat", Layer3Tile = 3f,
                DataPath = "Assets/Terrain/Oasis_Terrain.vterrain"
            };
            ter.AddComponentDirect(tc);
            TerrainData data; float cell;
            if (!TerrainService.TryGetData(ter, out data, out cell)) { log.LogError("oasis setup: no terrain data"); return false; }
            int res = data.Resolution;
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    data.Heights[z * res + x] = Ground(-Size * 0.5f + x * cell, -Size * 0.5f + z * cell);
            data.MarkChanged();
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                {
                    float wx = -Size * 0.5f + x * cell, wz = -Size * 0.5f + z * cell;
                    float h = data.Heights[z * res + x];
                    float ny = data.Normal(x, z, cell).Y;
                    Splat(data.Splat, (z * res + x) * 4, wx, wz, h, ny);
                }
            TerrainService.MarkDirty(ter, new SampleRect { X0 = 0, Z0 = 0, X1 = res - 1, Z1 = res - 1 }, true, true);
            TerrainService.Save(ter);
            log.Log("oasis setup: terrain " + Size + " m, " + res + " samples, heights " + data.MinHeight.ToString("0.0") + " .. " + data.MaxHeight.ToString("0.0") + " m");

            // ---- the far dunes: the same ground continued to the horizon at 8 m cells, a dune sea that grows with the distance
            // and a mountain range along the edge; it runs under the oasis terrain (1.2 m lower) so only the rim shows ----
            var far = scene.CreateEntity("Far Dunes");
            far.Transform.LocalPosition = new Vector3(-FarSize * 0.5f, 0f, -FarSize * 0.5f);
            var ftc = new TerrainComponent(far)
            {
                Size = FarSize, Resolution = FarResolution, LodDistance = 900f, Collision = true, Navigation = false,
                Layer0Material = "Assets/Materials/aerial_sand.vmat", Layer0Tile = 8f,
                Layer1Material = "Assets/Materials/damp_sand.vmat", Layer1Tile = 4f,
                Layer2Material = "Assets/Materials/rock_face_03.vmat", Layer2Tile = 9f,
                Layer3Material = "Assets/Materials/grass_ground.vmat", Layer3Tile = 3f,
                DataPath = "Assets/Terrain/Oasis_Far.vterrain"
            };
            far.AddComponentDirect(ftc);
            TerrainData fdata2; float fcell;
            if (!TerrainService.TryGetData(far, out fdata2, out fcell)) { log.LogError("oasis setup: no far terrain data"); return false; }
            int fres = fdata2.Resolution;
            for (int z = 0; z < fres; z++)
                for (int x = 0; x < fres; x++)
                    fdata2.Heights[z * fres + x] = FarGround(-FarSize * 0.5f + x * fcell, -FarSize * 0.5f + z * fcell);
            fdata2.MarkChanged();
            for (int z = 0; z < fres; z++)
                for (int x = 0; x < fres; x++)
                {
                    float wx = -FarSize * 0.5f + x * fcell, wz = -FarSize * 0.5f + z * fcell;
                    Splat(fdata2.Splat, (z * fres + x) * 4, wx, wz, fdata2.Heights[z * fres + x], fdata2.Normal(x, z, fcell).Y);
                }
            TerrainService.MarkDirty(far, new SampleRect { X0 = 0, Z0 = 0, X1 = fres - 1, Z1 = fres - 1 }, true, true);
            TerrainService.Save(far);
            log.Log("oasis setup: far dunes " + FarSize + " m, " + fres + " samples, heights " + fdata2.MinHeight.ToString("0.0") + " .. " + fdata2.MaxHeight.ToString("0.0") + " m");

            // ---- the lakes ----
            Lake(scene, "Oasis Lake West", LakeA, 170f);
            Lake(scene, "Oasis Lake East", LakeB, 140f);

            // ---- the player: the Tactical rig (weapons, HUD) on the sand strip between the lakes; the engine's walker if there is none ----
            float startY; if (!TerrainService.TryHeight(ter, 24f, -10f, out startY)) startY = 0f;
            GameEntity rig = null;
            if (rigPrefab != null)
            {
                try { rig = PrefabService.Instance.InstantiatePrefab(rigPrefab, scene, null, false); }
                catch (Exception ex) { log.LogWarning("oasis setup: could not instantiate the player rig — " + ex.Message); }
            }
            if (rig != null)
            {
                rig.Name = "Player";
                rig.PrefabPath = null;   // its own entity now, not a linked instance
                // whatever the prefab brought along from an earlier oasis goes first: one Footprints script, no weather children
                foreach (var sc in rig.GetComponents<Script>())
                    if (sc != null && (sc.ScriptPath ?? "").EndsWith("Footprints.cs", StringComparison.OrdinalIgnoreCase)) rig.RemoveComponent(sc);
                foreach (var ch in new List<GameEntity>(rig.Children))
                    if (ch != null && (ch.Name == "Weather Haze" || ch.Name == "Weather Sand")) { rig.RemoveChild(ch); scene.RemoveEntity(ch); }
                rig.AddComponentDirect(new Script(rig, "Assets/Scripts/Player/Footprints.cs"));   // boot prints in the sand
                // the rig's entity sits at EYE height (CoDMovement: feet = Position.Y - 1.7): 1.2 m put the feet half a metre
                // under the sand, and with the far dunes' sheet 1.2 m below the oasis the capsule was sandwiched between two
                // terrains and pushed through both ("man fällt durch den Boden", 2026-10-11)
                rig.Transform.LocalPosition = new Vector3(24f, startY + 1.9f, -10f);
                rig.Transform.LocalRotation = new Vector3(0f, -60f, 0f);
                SetFarClip(rig, 6000f);   // the far dunes and the mountains are 2 km out
                log.Log("oasis setup: the Tactical player rig stands on the strip between the lakes");
                // the weather's dust effects travel with the player: a haze and a sandstorm emitter, off until a preset asks
                foreach (var w in new[] { ("Weather Haze", "Assets/VFX/Dust_Haze.vfx"), ("Weather Sand", "Assets/VFX/Sandstorm.vfx") })
                {
                    var d = scene.CreateEntity(w.Item1);
                    d.Transform.LocalPosition = new Vector3(0f, 2f, 0f);
                    d.AddComponentDirect(new ParticleSystem(d) { VfxPath = w.Item2, PlayOnStart = false, PreviewInEditor = false });
                    d.SetParent(rig);
                }
            }
            else
            {
                var camera = scene.CreateEntity("Main Camera");
                camera.Transform.LocalPosition = new Vector3(24f, startY + 1.7f, -10f);
                camera.Transform.LocalRotation = new Vector3(0f, -60f, 0f);
                camera.AddComponentDirect(new Camera(camera) { IsMainCamera = true, FarClip = 6000f });
                var playerScript = ScriptingService.EnsurePlayerController(project.Path);
                if (!string.IsNullOrEmpty(playerScript)) camera.AddComponentDirect(new Script(camera, playerScript));
            }

            // ---- the vegetation and the rocks ----
            var fol = scene.CreateEntity("Oasis Vegetation");
            var fc = new FoliageComponent(fol) { Wind = 1f, DataPath = "Assets/Foliage/Oasis_Vegetation.vfoliage" };
            var types = Types();
            foreach (var t in types) fc.Types.Add(t);
            fol.AddComponentDirect(fc);
            FoliageData fdata;
            if (!FoliageService.TryGetData(fol, out fdata)) { log.LogError("oasis setup: no foliage data"); return false; }
            int placed = Scatter(fdata, types, ter);
            FoliageService.MarkDirty(fol);
            FoliageService.Save(fol);
            log.Log("oasis setup: " + placed + " foliage instances in " + types.Count + " types");

            // ---- the weather: a script that blends the presets (F6 / F7) and the wind loop it plays ----
            var weather = scene.CreateEntity("Weather");
            weather.AddComponentDirect(new Editor.ECS.Components.Audio.AudioSource(weather) { AudioClipPath = "Assets/Audio/wind_desert_loop.wav", Loop = true, PlayOnAwake = false, Volume = 0f, SpatialBlend = 0f });
            weather.AddComponentDirect(new Script(weather, "Assets/Scripts/World/Weather.cs"));

            // ---- the infected: a director that spawns them on the navmesh around the player (Z calls a wave) ----
            var zombies = scene.CreateEntity("Zombies");
            zombies.AddComponentDirect(new Script(zombies, "Assets/Scripts/AI/ZombieDirector.cs"));

            // ---- the air: warm haze that thins with height, a touch of bloom and AO, no vignette ----
            var st = scene.Settings;
            st.Tonemapper = 1;         // PBR Neutral: the sand stays orange, the sky blue — the ACES fit washed both towards white
            st.AntiAliasEnabled = true;   // FXAA: the fronds and the ripples stop flickering
            st.BounceStrength = 1.2f; st.BounceR = 0.62f; st.BounceG = 0.40f; st.BounceB = 0.20f;   // the sand's warm bounce into the shadows: the reference's shade is orange-brown; at 1.0 the sky's blue won and the grove floor turned grey
            st.SceneExposure = 1.7f;   // stops before the tonemapper: the low sun needs more aperture than noon; 2.2 pushed the sunlit sand into the tonemapper's shoulder (pale pink), 2.6 clipped the crowns
            st.FogEnabled = true; st.FogDensity = 0.00028f;   // at 0.0010 the air swallowed 63 % of a dune 1 km out — the reference's dunes are saturated to the horizon st.FogHeightY = -4f; st.FogHeightFalloff = 0.012f;
            st.FogR = 0.70f; st.FogG = 0.62f; st.FogB = 0.52f; st.FogSunGlow = 0.2f;   // the reference's distance is peach, not blue-white   // the reference's far dunes fade to a neutral, slightly cool white — a warm haze turned them salmon
            st.GradeEnabled = true; st.Exposure = 0f; st.Contrast = 1.06f; st.Saturation = 1.05f;   // a light touch: the grade only reaches the viewport once SetPostMainView is on (the oasis capture turns it on) — at 1.22 the sky went navy and the sand neon st.Temperature = 0.0f; st.Tint = 0.0f;   // the reference is NOT saturated: the sand golden, the palms olive, the shadows warm
            st.AoEnabled = true; st.AoRadius = 0.8f; st.AoIntensity = 0.9f;
            st.GiEnabled = true; st.GiStrength = 0.6f; st.GiRadius = 3.0f;   // screen-space GI: the sand's light on the trunks and the undergrowth
            st.BloomEnabled = true; st.BloomThreshold = 0.95f; st.BloomIntensity = 0.18f; st.BloomScatter = 0.6f;   // 0.3 spread the sun into a white sheet
            // sun shafts through the palms: a thin volumetric haze marched 160 m, strongly forward-scattering
            st.VolumetricEnabled = true; st.VolumetricDensity = 0.0018f; st.VolumetricAnisotropy = 0.55f; st.VolumetricDistance = 120f;   // g 0.75 + sun 1.6 whited out a third of the frame around the sun (Henyey-Greenstein peaks 28x isotropic at g 0.75); the reference has soft shafts, not a sheet
            st.VolumetricNoise = 0.25f; st.VolumetricNoiseScale = 12f; st.VolumetricNoiseSpeed = 0.2f; st.VolumetricSun = 0.3f;   // 0.6 whited out 12–16 % of a frame that looks towards the sun (the in-scatter sheet, measured 2026-10-12); 0.3 keeps the shafts and the sky
            st.VolumetricLights = 0f; st.VolumetricAmbient = 0.04f; st.VolumetricSteps = 20; st.VolumetricShadows = true;
            st.Apply();

            // ---- the navmesh: the whole oasis walkable for the zombies (the terrain is ground, the trunks and rocks obstacles) ----
            if (NavigationService.Available)
            {
                var t0 = DateTime.UtcNow;
                var bake = NavigationService.BakeScene(scene, NavigationService.SettingsFor(scene), save: true, load: true);
                if (bake.Success) log.Log("oasis setup: navmesh " + bake.Stats.PolyCount + " polygons in " + (DateTime.UtcNow - t0).TotalSeconds.ToString("0") + " s");
                else log.LogWarning("oasis setup: navmesh bake failed — " + bake.Message);
            }
            else log.LogWarning("oasis setup: no Recast in this build — the zombies have no navmesh");

            SceneService.Instance.SaveScene(scene);
            ProjectService.Instance.SaveProject(project);
            scene.IsDirty = false;
            log.Log("oasis setup: scene saved — " + scene.FilePath);

            await SmokeRegistry.Settle(2500);   // the models load
            return await Capture();
        }

        // ------------------------------------------------------------------ the ground

        /// <summary>The ground height (m) at a world position: the dune field, the two basins, the escarpment.</summary>
        internal static float Ground(float x, float z)
        {
            float prox = Proximity(x, z);
            // the basins: a flat-bottomed bowl each, their rims wobbling with noise
            float h = -LakeADepth * Bowl(Dist(x, z, LakeA) * (1f + 0.10f * Fbm(x * 0.02f + 5f, z * 0.02f + 1f, 3)), LakeAR)
                      - LakeBDepth * Bowl(Dist(x, z, LakeB) * (1f + 0.10f * Fbm(x * 0.02f + 9f, z * 0.02f + 4f, 3)), LakeBR);
            // the oasis floor undulates a little; the dunes rise beyond it
            float duneMask = Smooth((prox - 0.95f) / 0.9f);
            h += 0.5f * Fbm(x * 0.03f, z * 0.03f, 3) + 1.4f * Fbm(x * 0.008f + 2f, z * 0.008f + 2f, 3);
            h += duneMask * Dunes(x, z);
            // the escarpment along the north: a 20 m step with a broken edge, gravel on top
            float edge = -175f + 14f * Fbm(x * 0.006f + 1f, 0.3f, 3) + 4f * Fbm(x * 0.03f, 0.7f, 2);
            float cliff = Smooth((edge - z) / 22f);
            h += cliff * (19f + 3f * Fbm(x * 0.02f + 3f, z * 0.02f + 3f, 3)) + cliff * 2.5f * Fbm(x * 0.07f, z * 0.07f, 4);
            return h;
        }

        /// <summary>The far terrain's ground: the oasis ground continued (1.2 m lower under the oasis terrain so only the rim shows),
        /// a dune sea whose swell grows with the distance, and a broken mountain range 1.4–2 km out all around.</summary>
        internal static float FarGround(float x, float z)
        {
            float inside = 1f - Smooth((Math.Max(Math.Abs(x), Math.Abs(z)) - Size * 0.5f + 30f) / 36f);   // 1 under the oasis terrain
            float h = Ground(x, z) - 1.2f * inside;
            float r = (float)Math.Sqrt(x * x + z * z);
            float sea = Smooth((r - 300f) / 500f);
            h += sea * (22f * Fbm(x * 0.0018f + 11f, z * 0.0018f + 5f, 4) + 14f * Fbm(x * 0.0007f + 2f, z * 0.0007f + 9f, 3));
            float ridge = Smooth((r - 1250f) / 400f);   // all around: the sky picture's own hills never show above the horizon
            float m = 1f - Math.Abs(Fbm(x * 0.0011f + 4f, z * 0.0011f + 1f, 5));          // ridged: sharp crests
            h += ridge * (120f + 320f * m * m + 40f * Fbm(x * 0.006f, z * 0.006f, 4));
            return h;
        }

        /// <summary>Ridged dunes running SW–NE (wind from the north-west): a gentle windward side, a steep slip face, bent by
        /// noise; broad undulations and ripples on top.</summary>
        private static float Dunes(float x, float z)
        {
            float u = (x + z) * 0.7071f, v = (x - z) * 0.7071f;
            float warp = 28f * Fbm(v * 0.011f + 4f, u * 0.006f + 8f, 3);
            float phase = (u + warp) / 96f;
            float f = phase - (float)Math.Floor(phase);
            f = 1f - f;   // the long windward slopes face the sun (it stands in the north-east), the slip faces drop away from it into shadow — side-lit crests instead of a flat sheet
            // a long windward slope (72 % of the wavelength) and a short steep slip face: the crest reads as an edge, not a hump
            float crest = f < 0.72f ? Smooth(f / 0.72f) : 1f - Smooth((f - 0.72f) / 0.28f);
            float amp = 15f + 9f * Fbm(x * 0.004f + 7f, z * 0.004f + 3f, 3);
            float big = 16f * Fbm(x * 0.0045f + 3.3f, z * 0.0045f + 7.1f, 4);
            float detail = 0.7f * Fbm(x * 0.05f, z * 0.05f, 3);
            return crest * amp + big + detail;
        }

        /// <summary>Splat weights: sand; damp sand along the water line; grass on the oasis floor; rock on slopes, the
        /// escarpment and the gravel plains.</summary>
        private static void Splat(byte[] s, int o, float x, float z, float h, float ny)
        {
            float hw = h - WaterLevel, prox = Proximity(x, z);
            float damp = Clamp01(1f - Math.Abs(hw - 0.2f) / 1.3f);
            float grassN = Clamp01(1.2f * (Fbm(x * 0.03f + 11f, z * 0.03f + 2f, 3) + 0.25f));
            float grass = Smooth((hw - 0.25f) / 1.2f) * (1f - Smooth((hw - 4.5f) / 3f)) * (1f - Smooth((prox - 0.9f) / 0.25f)) * grassN;
            float rock = Clamp01((0.80f - ny) / 0.12f);
            float gravelN = Fbm(x * 0.012f + 9f, z * 0.012f + 9f, 3);
            if (z < -150f) rock = Math.Max(rock, Clamp01((gravelN + 0.15f) * 1.5f) * Smooth((-150f - z) / 40f));      // the escarpment's top
            rock = Math.Max(rock, Clamp01((gravelN - 0.3f) * 3f) * Smooth((prox - 1.1f) / 0.5f));                        // gravel plains
            float sand = Clamp01(1f - damp - grass - rock);
            float sum = sand + damp + rock + grass; if (sum < 1e-4f) { sand = 1f; sum = 1f; }
            s[o] = (byte)Math.Round(255f * sand / sum); s[o + 1] = (byte)Math.Round(255f * damp / sum);
            s[o + 2] = (byte)Math.Round(255f * rock / sum); s[o + 3] = (byte)Math.Round(255f * grass / sum);
        }

        private static bool TooCloseToAnyPalm(FoliageData data, NVec3 pos, float spacing)
        {
            foreach (var pl in data.Layers) if (pl.TypeName.StartsWith("Palm") && pl.AnyWithin(pos, spacing)) return true;
            return false;
        }

        /// <summary>0 at a lake's centre, 1 at the oasis' edge, larger outside.</summary>
        internal static float Proximity(float x, float z) => Math.Min(Dist(x, z, LakeA) / (LakeAR * 1.25f), Dist(x, z, LakeB) / (LakeBR * 1.25f));

        private static float Bowl(float d, float r) { float t = Clamp01(d / r); return 1f - t * t * t; }
        private static float Dist(float x, float z, NVec3 c) { float dx = x - c.X, dz = z - c.Z; return (float)Math.Sqrt(dx * dx + dz * dz); }
        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        private static float Smooth(float t) { t = Clamp01(t); return t * t * (3f - 2f * t); }

        // value noise, -1 .. 1
        private static float Hash(int x, int z)
        {
            unchecked
            {
                int h = x * 374761393 + z * 668265263;
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0x7fffffff) / (float)0x7fffffff;
            }
        }
        private static float Noise(float x, float z)
        {
            int xi = (int)Math.Floor(x), zi = (int)Math.Floor(z);
            float fx = x - xi, fz = z - zi;
            fx = fx * fx * (3f - 2f * fx); fz = fz * fz * (3f - 2f * fz);
            float a = Hash(xi, zi), b = Hash(xi + 1, zi), c = Hash(xi, zi + 1), d = Hash(xi + 1, zi + 1);
            return (a + (b - a) * fx) + ((c + (d - c) * fx) - (a + (b - a) * fx)) * fz;
        }
        internal static float Fbm(float x, float z, int octaves)
        {
            float sum = 0f, amp = 0.5f, norm = 0f;
            for (int i = 0; i < octaves; i++) { sum += amp * Noise(x, z); norm += amp; x = x * 2.03f + 17.7f; z = z * 1.97f + 31.3f; amp *= 0.5f; }
            return (sum / norm) * 2f - 1f;
        }

        // ------------------------------------------------------------------ the water

        private static void Lake(Scene scene, string name, NVec3 centre, float size)
        {
            var e = scene.CreateEntity(name);
            e.Transform.LocalPosition = new Vector3(centre.X, WaterLevel, centre.Z);
            e.AddComponentDirect(new WaterComponent(e)
            {
                Size = size, CellSize = 1f,
                // the body colours are albedos (lit by sky + sun, then exposed): the reference's dark green needs 0.03–0.15
                DeepR = 0.004f, DeepG = 0.022f, DeepB = 0.016f, ShallowR = 0.025f, ShallowG = 0.09f, ShallowB = 0.06f,
                // dark green water that mirrors the palms and the sky; gentle ripples (a strong ripple normal painted the lake in white sky stripes)
                Absorption = 2.5f, Reflection = 0.7f, Roughness = 0.06f, WaveScale = 9f, WaveSpeed = 0.5f, WaveHeight = 0.035f, FoamWidth = 0.45f, DefaultDepth = 3f
            });
        }

        // ------------------------------------------------------------------ the plants and the rocks

        private sealed class Spec
        {
            public FoliageType Type; public int Candidates; public float X0, Z0, X1, Z1;
            public Func<float, float, float, float, float> Prob;   // (x, z, height above water, normal.y) -> 0..1
        }

        private static FoliageType T(string name, string mesh, float minScale, float maxScale, float spacing, float cull, float thin, float windStrength, float windHeight, int collision, float colRadius, float colHeight, bool align, float maxSlope, float sink)
        {
            var t = FoliageComponent.DefaultType(name, mesh, false);
            t.MinScale = minScale; t.MaxScale = maxScale; t.MinSpacing = spacing; t.CullDistance = cull; t.ThinDistance = thin;
            t.WindStrength = windStrength; t.WindHeight = windHeight; t.WindSpeed = 0.8f; t.Collision = collision; t.CollisionRadius = colRadius; t.CollisionHeight = colHeight;
            t.AlignToNormal = align; t.MaxSlope = maxSlope; t.Sink = sink; t.Cutout = true; t.MaxTilt = align ? 0f : 3f;
            return t;
        }

        private static List<FoliageType> Types()
        {
            var l = new List<FoliageType>();
            // the palms (Yughues' palm pack, 18 – 21 m at scale 1): the skyline of the oasis
            // date palms (tools/palm-gen.py: a leaning trunk, 36–44 pinnate fronds in three rings, a skirt of dead fronds — the
            // silhouette of the reference); real size, so the scale stays near 1. The two Yughues palms keep the mix varied.
            l.Add(T("Palm straight", "Assets/Models/Palms/date_palm_a/date_palm_a.gltf", 0.8f, 1.15f, 4.5f, 650f, 0f, 0.45f, 10f, 1, 0.35f, 9f, false, 32f, 0.15f));
            l.Add(T("Palm bend", "Assets/Models/Palms/date_palm_b/date_palm_b.gltf", 0.75f, 1.1f, 4.5f, 650f, 0f, 0.45f, 12f, 1, 0.35f, 12f, false, 32f, 0.15f));
            l.Add(T("Palm dual", "Assets/Models/Palms/date_palm_c/date_palm_c.gltf", 0.85f, 1.25f, 4.5f, 650f, 0f, 0.45f, 8f, 1, 0.35f, 7f, false, 32f, 0.15f));
            l.Add(T("Palm dual_bend", "Assets/Models/Palms/date_palm_a/date_palm_a.gltf", 0.7f, 1.0f, 4.5f, 650f, 0f, 0.45f, 10f, 1, 0.35f, 9f, false, 32f, 0.15f));
            l.Add(T("Palm trio", "Assets/Models/Palms/palm_trio/palm_trio.gltf", 0.42f, 0.7f, 4.5f, 650f, 0f, 0.45f, 13f, 1, 0.35f, 12f, false, 32f, 0.15f));
            l.Add(T("Palm v2", "Assets/Models/Palms/date_palm_b/date_palm_b.gltf", 0.65f, 0.95f, 4.5f, 650f, 0f, 0.45f, 12f, 1, 0.3f, 11f, false, 32f, 0.15f));
            // trees on the damp ground
            l.Add(T("Island tree 1", "Assets/Models/Oasis/island_tree_01/island_tree_01_lod.gltf", 0.9f, 1.4f, 6f, 320f, 0f, 0.15f, 5f, 1, 0.3f, 5f, false, 35f, 0.1f));
            l.Add(T("Island tree 2", "Assets/Models/Oasis/island_tree_02/island_tree_02_lod.gltf", 0.9f, 1.4f, 6f, 320f, 0f, 0.15f, 4f, 1, 0.3f, 4f, false, 35f, 0.1f));
            l.Add(T("Small tree", "Assets/Models/Oasis/tree_small_02/tree_small_02_lod.gltf", 0.9f, 1.3f, 6f, 320f, 0f, 0.15f, 4.5f, 1, 0.25f, 4.5f, false, 35f, 0.1f));
            // shrubs
            l.Add(T("Searsia burchellii", "Assets/Models/Palms/fern_a/fern_a.gltf", 0.8f, 1.3f, 1.6f, 160f, 60f, 0.12f, 1.2f, 0, 0f, 0f, true, 50f, 0.03f));   // fern clumps (tools/palm-gen.py --fern): the reference's undergrowth
            l.Add(T("Searsia lucida", "Assets/Models/Palms/fern_b/fern_b.gltf", 0.7f, 1.2f, 1.8f, 160f, 60f, 0.12f, 1.2f, 0, 0f, 0f, true, 50f, 0.03f));
            l.Add(T("Rooibos", "Assets/Models/Oasis/wild_rooibos_bush/wild_rooibos_bush_lod.gltf", 0.9f, 1.6f, 2f, 90f, 40f, 0.06f, 0.6f, 0, 0f, 0f, true, 50f, 0.04f));
            l.Add(T("Didelta", "Assets/Models/Oasis/didelta_spinosa/didelta_spinosa_lod.gltf", 0.7f, 1.2f, 5f, 140f, 0f, 0.07f, 2f, 0, 0f, 0f, false, 45f, 0.1f));
            // the desert
            l.Add(T("Quiver tree 1", "Assets/Models/Oasis/quiver_tree_01/quiver_tree_01_lod.gltf", 1.3f, 2.2f, 6f, 400f, 0f, 0.05f, 4f, 1, 0.25f, 4f, false, 40f, 0.06f));
            l.Add(T("Quiver tree 2", "Assets/Models/Oasis/quiver_tree_02/quiver_tree_02_lod.gltf", 1.4f, 2.4f, 6f, 400f, 0f, 0.05f, 3f, 1, 0.2f, 3f, false, 40f, 0.06f));
            l.Add(T("Succulent", "Assets/Models/Oasis/cheiridopsis_succulent/cheiridopsis_succulent_lod.gltf", 1f, 1.8f, 1.2f, 110f, 40f, 0f, 0.3f, 0, 0f, 0f, true, 50f, 0.05f));
            l.Add(T("Dry leaf", "Assets/Models/Oasis/dry_quiver_leaf/dry_quiver_leaf_lod.gltf", 1f, 1.6f, 1.2f, 100f, 40f, 0f, 0.4f, 0, 0f, 0f, true, 50f, 0.0f));
            l.Add(T("Dead trunk", "Assets/Models/Oasis/dead_quiver_trunk/dead_quiver_trunk_lod.gltf", 1f, 1.8f, 5f, 260f, 0f, 0f, 2f, 1, 0.2f, 2f, false, 45f, 0.12f));
            // grass and reeds at the water
            l.Add(T("Grass", "Assets/Models/Oasis/grass_medium_02/grass_medium_02_lod.gltf", 1.2f, 2.2f, 0.7f, 90f, 30f, 0.05f, 0.5f, 0, 0f, 0f, true, 55f, 0.04f));
            l.Add(T("Bermuda grass", "Assets/Models/Oasis/grass_bermuda_01/grass_bermuda_01_lod.gltf", 1.8f, 3.2f, 0.55f, 90f, 30f, 0.03f, 0.3f, 0, 0f, 0f, true, 55f, 0.02f));
            // the rocks
            l.Add(T("Boulder 2", "Assets/Models/Rocks/namaqualand_boulder_02/namaqualand_boulder_02_lod.gltf", 0.8f, 1.8f, 5f, 380f, 0f, 0f, 1f, 2, 0f, 0f, true, 60f, 0.2f));
            l.Add(T("Boulder 3", "Assets/Models/Rocks/namaqualand_boulder_03/namaqualand_boulder_03_lod.gltf", 0.8f, 1.8f, 5f, 380f, 0f, 0f, 1f, 2, 0f, 0f, true, 60f, 0.25f));
            l.Add(T("Boulder 4", "Assets/Models/Rocks/namaqualand_boulder_04/namaqualand_boulder_04_lod.gltf", 0.8f, 1.8f, 5f, 380f, 0f, 0f, 1f, 2, 0f, 0f, true, 60f, 0.3f));
            l.Add(T("Cliff", "Assets/Models/Rocks/namaqualand_cliff_01/namaqualand_cliff_01_lod.gltf", 1.6f, 2.8f, 11f, 700f, 0f, 0f, 1f, 2, 0f, 0f, true, 70f, 1.2f));
            l.Add(T("Stones", "Assets/Models/Rocks/namaqualand_rocks_01/namaqualand_rocks_01_lod.gltf", 1.5f, 4f, 1.3f, 90f, 30f, 0f, 1f, 0, 0f, 0f, true, 60f, 0.02f));
            l.Add(T("Pebbles", "Assets/Models/Rocks/namaqualand_stones_01/namaqualand_stones_01_lod.gltf", 1.2f, 2.5f, 1.3f, 80f, 30f, 0f, 1f, 0, 0f, 0f, true, 60f, 0.01f));
            l.Add(T("Flat rocks", "Assets/Models/Rocks/sand_rocks_small_01/sand_rocks_small_01_lod.gltf", 0.8f, 1.6f, 9f, 260f, 0f, 0f, 1f, 0, 0f, 0f, true, 50f, 0.25f));
            // the litter of the groves: dead fronds on the sand (tools/palm-gen.py --litter)
            l.Add(T("Palm litter", "Assets/Models/Palms/palm_litter_a/palm_litter_a.gltf", 0.7f, 1.2f, 1.5f, 110f, 45f, 0f, 0.3f, 0, 0f, 0f, true, 50f, 0.0f));
            l.Add(T("Palm litter pile", "Assets/Models/Palms/palm_litter_b/palm_litter_b.gltf", 0.7f, 1.1f, 3f, 110f, 45f, 0f, 0.3f, 0, 0f, 0f, true, 50f, 0.0f));
            l.Add(T("Rock formation", "Assets/Models/Rocks/coast_rocks_02/coast_rocks_02_lod.gltf", 0.3f, 0.45f, 40f, 1200f, 0f, 0f, 1f, 2, 0f, 0f, false, 80f, 1.5f));
            foreach (var t in l) t.Cutout = !(t.Name.StartsWith("Boulder") || t.Name == "Cliff" || t.Name == "Stones" || t.Name == "Pebbles" || t.Name == "Flat rocks" || t.Name == "Rock formation" || t.Name == "Dead trunk");   // rocks and bare wood are opaque
            return l;
        }

        /// <summary>The biome rules: where every type grows. Height above the water (hw) and the oasis proximity (prox: 0 lake
        /// centre, 1 oasis edge) carry most of it, noise breaks the rings into groves and clearings.</summary>
        private static int Scatter(FoliageData data, List<FoliageType> types, GameEntity ter)
        {
            var specs = new List<Spec>();
            float ox0 = -150f, oz0 = -140f, ox1 = 170f, oz1 = 130f;   // the oasis rectangle
            Func<float, float, float> grove = (x, z) => Clamp01(0.40f + 0.8f * Fbm(x * 0.025f + 3f, z * 0.025f + 5f, 3));   // groves and real clearings: the reference's sunlit sand between the trunks
            // palms: a dense ring from the water line to ~10 m up, thinning outwards; palm groves beyond the ring
            for (int i = 0; i < 6; i++)
            {
                int k = i;
                specs.Add(new Spec { Type = types[k], Candidates = k == 5 ? 2000 : 1500, X0 = ox0, Z0 = oz0, X1 = ox1, Z1 = oz1, Prob = (x, z, hw, ny) =>
                {
                    if (hw < 0.35f || hw > 12f || ny < 0.85f) return 0f;
                    float g = grove(x, z);                                        // groves and clearings
                    float ring = hw < 4f ? 0.85f : 0.85f * (1f - (hw - 4f) / 8f);   // densest just above the water (the reference's thick groves)
                    float strip = Smooth((60f - Math.Abs(x - 20f) * 0.6f - Math.Abs(z + 10f)) / 25f);   // the open sand between the lakes
                    return ring * Smooth((g - 0.15f) / 0.5f) * (1f - 0.85f * strip) * (Proximity(x, z) < 1.2f ? 1f : 0.15f);
                } });
            }
            // trees and shrubs on the damp ground
            for (int i = 6; i < 9; i++)
            {
                int k = i;   // the photoscanned trees keep their leaves only above ~10 % of their triangles: a few, close to the water
                specs.Add(new Spec { Type = types[k], Candidates = 0, X0 = ox0, Z0 = oz0, X1 = ox1, Z1 = oz1, Prob = (x, z, hw, ny) =>
                    hw < 0.8f || hw > 14f || ny < 0.85f || Proximity(x, z) > 1.2f ? 0f : 0.45f * grove(x + 50f, z - 30f) });
            }
            // the undergrowth of the groves: shrubs thick under the palms, thinning into the desert
            specs.Add(new Spec { Type = types[9], Candidates = 5000, X0 = -256f, Z0 = -256f, X1 = 256f, Z1 = 256f, Prob = (x, z, hw, ny) => hw < 0.5f || ny < 0.8f ? 0f : (Proximity(x, z) < 1.3f ? 0.75f : 0.07f) });
            specs.Add(new Spec { Type = types[10], Candidates = 6000, X0 = -256f, Z0 = -256f, X1 = 256f, Z1 = 256f, Prob = (x, z, hw, ny) => hw < 0.5f || ny < 0.8f ? 0f : (Proximity(x, z) < 1.3f ? 0.75f : 0.06f) });
            specs.Add(new Spec { Type = types[11], Candidates = 2600, X0 = -256f, Z0 = -256f, X1 = 256f, Z1 = 256f, Prob = (x, z, hw, ny) => hw < 0.4f || ny < 0.75f ? 0f : (Proximity(x, z) < 1.4f ? 0.7f : 0.12f) * grove(x * 1.5f, z * 1.5f) });
            specs.Add(new Spec { Type = types[12], Candidates = 1100, X0 = -256f, Z0 = -256f, X1 = 256f, Z1 = 256f, Prob = (x, z, hw, ny) => hw < 0.6f || ny < 0.8f ? 0f : (Proximity(x, z) < 1.3f ? 0.55f : 0.08f) });
            // dead fronds and leaves on the grove floor, stones and pebbles along the water line (the reference's shore)
            specs.Add(new Spec { Type = types[16], Candidates = 2500, X0 = ox0, Z0 = oz0, X1 = ox1, Z1 = oz1, Prob = (x, z, hw, ny) => hw < 0.4f || hw > 12f || ny < 0.8f || Proximity(x, z) > 1.2f ? 0f : 0.6f * grove(x, z) });
            specs.Add(new Spec { Type = types[24], Candidates = 3000, X0 = ox0, Z0 = oz0, X1 = ox1, Z1 = oz1, Prob = (x, z, hw, ny) => hw < 0.1f || hw > 1.4f ? 0f : 0.7f });
            specs.Add(new Spec { Type = types[25], Candidates = 4000, X0 = ox0, Z0 = oz0, X1 = ox1, Z1 = oz1, Prob = (x, z, hw, ny) => hw < 0.05f || hw > 1.2f ? 0f : 0.8f });
            specs.Add(new Spec { Type = types[26], Candidates = 700, X0 = ox0, Z0 = oz0, X1 = ox1, Z1 = oz1, Prob = (x, z, hw, ny) => hw < 0.2f || hw > 2.2f ? 0f : 0.5f });
            // the desert plain: quiver trees, succulents, dead wood (not on the dune slip faces, not on the escarpment's face)
            specs.Add(new Spec { Type = types[13], Candidates = 900, X0 = -256f, Z0 = -256f, X1 = 256f, Z1 = 256f, Prob = (x, z, hw, ny) => hw < 1f || ny < 0.88f ? 0f : (Proximity(x, z) > 1.0f && Proximity(x, z) < 2.6f ? 0.3f : 0.03f) });
            specs.Add(new Spec { Type = types[14], Candidates = 900, X0 = -256f, Z0 = -256f, X1 = 256f, Z1 = 256f, Prob = (x, z, hw, ny) => hw < 1f || ny < 0.88f ? 0f : (Proximity(x, z) > 1.0f && Proximity(x, z) < 2.6f ? 0.3f : 0.03f) });
            specs.Add(new Spec { Type = types[15], Candidates = 4000, X0 = -256f, Z0 = -256f, X1 = 256f, Z1 = 256f, Prob = (x, z, hw, ny) => hw < 0.4f || ny < 0.8f ? 0f : 0.35f * grove(x * 2f + 9f, z * 2f) });
            specs.Add(new Spec { Type = types[16], Candidates = 3000, X0 = -256f, Z0 = -256f, X1 = 256f, Z1 = 256f, Prob = (x, z, hw, ny) => hw < 0.4f || ny < 0.8f ? 0f : 0.3f });
            specs.Add(new Spec { Type = types[17], Candidates = 500, X0 = -256f, Z0 = -256f, X1 = 256f, Z1 = 256f, Prob = (x, z, hw, ny) => hw < 0.5f || ny < 0.85f ? 0f : (Proximity(x, z) < 2.2f ? 0.25f : 0.05f) });
            // grass at the water
            specs.Add(new Spec { Type = types[18], Candidates = 7000, X0 = ox0, Z0 = oz0, X1 = ox1, Z1 = oz1, Prob = (x, z, hw, ny) => hw < 0.2f || hw > 6.5f || ny < 0.8f || Proximity(x, z) > 1.15f ? 0f : 0.9f * (0.4f + 0.6f * grove(x - 20f, z + 40f)) });
            specs.Add(new Spec { Type = types[19], Candidates = 6000, X0 = ox0, Z0 = oz0, X1 = ox1, Z1 = oz1, Prob = (x, z, hw, ny) => hw < 0.12f || hw > 2.8f || ny < 0.8f ? 0f : 0.95f });
            // rocks: boulders on the gravel and below the escarpment, cliffs along its edge, stones everywhere, flat rocks on the plains, three formations on the dunes
            for (int i = 20; i < 23; i++)
            {
                int k = i;
                specs.Add(new Spec { Type = types[k], Candidates = 1200, X0 = -256f, Z0 = -256f, X1 = 256f, Z1 = 256f, Prob = (x, z, hw, ny) =>
                    hw < 0.5f ? 0f : (z < -120f && z > -215f ? 0.3f : (Proximity(x, z) > 1.1f ? 0.07f : 0.02f)) });
            }
            specs.Add(new Spec { Type = types[23], Candidates = 700, X0 = -256f, Z0 = -230f, X1 = 256f, Z1 = -140f, Prob = (x, z, hw, ny) => z < -215f || z > -160f ? 0f : 0.4f });
            specs.Add(new Spec { Type = types[24], Candidates = 9000, X0 = -256f, Z0 = -256f, X1 = 256f, Z1 = 256f, Prob = (x, z, hw, ny) => hw < 0.3f ? 0f : 0.3f });
            // dead fronds under the palms, a few blown out into the sand
            specs.Add(new Spec { Type = types[28], Candidates = 6000, X0 = ox0, Z0 = oz0, X1 = ox1, Z1 = oz1, Prob = (x, z, hw, ny) => hw < 0.35f || ny < 0.8f ? 0f : (Proximity(x, z) < 1.25f ? 0.6f * grove(x, z) : 0.04f) });
            specs.Add(new Spec { Type = types[29], Candidates = 1500, X0 = ox0, Z0 = oz0, X1 = ox1, Z1 = oz1, Prob = (x, z, hw, ny) => hw < 0.35f || ny < 0.8f || Proximity(x, z) > 1.2f ? 0f : 0.35f * grove(x, z) });
            specs.Add(new Spec { Type = types[25], Candidates = 9000, X0 = -256f, Z0 = -256f, X1 = 256f, Z1 = 256f, Prob = (x, z, hw, ny) => hw < 0.3f ? 0f : 0.3f });
            specs.Add(new Spec { Type = types[26], Candidates = 500, X0 = -256f, Z0 = -256f, X1 = 256f, Z1 = 256f, Prob = (x, z, hw, ny) => hw < 0.8f || ny < 0.9f ? 0f : (Proximity(x, z) > 1.2f ? 0.25f : 0.05f) });
            specs.Add(new Spec { Type = types[27], Candidates = 60, X0 = -230f, Z0 = -120f, X1 = 230f, Z1 = 230f, Prob = (x, z, hw, ny) => hw < 6f || Proximity(x, z) < 1.6f ? 0f : 0.5f });

            int total = 0, seed = 9001;
            foreach (var s in specs)
            {
                var rng = new Random(seed++);
                var layer = data.Layer(s.Type.Name, true);
                float slopeCos = (float)Math.Cos(s.Type.MaxSlope * Math.PI / 180.0);
                int placed = 0;
                for (int c = 0; c < s.Candidates; c++)
                {
                    float x = s.X0 + (float)rng.NextDouble() * (s.X1 - s.X0), z = s.Z0 + (float)rng.NextDouble() * (s.Z1 - s.Z0);
                    float h; NVec3 n;
                    if (!TerrainService.TryHeight(ter, x, z, out h) || !TerrainService.TryNormal(ter, x, z, out n)) continue;
                    if (n.Y < slopeCos) continue;
                    float p = s.Prob(x, z, h - WaterLevel, n.Y);
                    if (p <= 0f || rng.NextDouble() >= p) continue;
                    var pos = new NVec3(x, h, z);
                    if (s.Type.MinSpacing > 0f && layer.AnyWithin(pos, s.Type.MinSpacing)) continue;
                    if (s.Type.Name.StartsWith("Palm") && TooCloseToAnyPalm(data, pos, 6.5f)) continue;   // across the palm layers: the reference's groves leave sunlit floor between the trunks
                    float scale = s.Type.MinScale + (float)rng.NextDouble() * (s.Type.MaxScale - s.Type.MinScale);
                    float yaw = (float)(rng.NextDouble() * 360.0), tilt = (float)(rng.NextDouble() * s.Type.MaxTilt), tiltDir = (float)(rng.NextDouble() * 360.0);
                    var rot = FoliageData.Orientation(n, s.Type.AlignToNormal, yaw, tilt, tiltDir);
                    pos.Y -= s.Type.Sink * scale;
                    layer.Add(new FoliageInstance(pos, rot, scale));
                    placed++;
                }
                total += placed;
            }
            return total;
        }

        // ------------------------------------------------------------------ the pictures

        private static async Task<bool> Capture()
        {
            var log = ConsoleService.Instance;
            if (Environment.GetEnvironmentVariable("VORTEX_TEMPLATE_SETUP") != "1") { log.Log("oasis capture: authoring helper — skipped"); return true; }
            var scene = ProjectData.Current?.ActiveScene;
            var ter = scene == null ? null : TemplateSetupSmoke.Find(scene, e => e.Name == "Oasis Terrain");
            if (ter == null) { log.Log("oasis capture: no oasis in this scene — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            Editor.DllWrapper.VortexAPI.SetPostMainView(true);   // the pictures are the game's: the grade, bloom and FXAA apply in the viewport as they do in play (off, the editor's build view stays clean)
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw0 = cam.Yaw, pitch0 = cam.Pitch;
            bool gridWas = EditorViewportService.Instance.IsGridVisible, gizmosWere = EditorViewportService.Instance.AreGizmosVisible;
            bool playing = false;
            var win = EditorCommands.Window;
            double winW = win != null ? win.Width : 0, winH = win != null ? win.Height : 0;
            if (win != null)
            {
                try
                {
                    var screen = win.Screens?.ScreenFromWindow(win) ?? win.Screens?.Primary;
                    double sw = screen != null ? screen.WorkingArea.Width / screen.Scaling : 1600, sh = screen != null ? screen.WorkingArea.Height / screen.Scaling : 1000;
                    win.Position = new Avalonia.PixelPoint(0, 0);
                    win.Width = Math.Max(winW, Math.Min(sw, 2400)); win.Height = Math.Max(winH, Math.Min(sh, 1500));
                }
                catch { }
            }
            try
            {
                SelectionService.Instance.ClearSelection();
                var footprintStart = At(ter, -16f, -60f, 0f);
                if (gridWas) EditorViewportService.Instance.ToggleGrid();   // no editor grid across the sky in the pictures
                EditorViewportService.Instance.AreGizmosVisible = false;    // no light / particle icons either
                // a fresh editor loads the 28 models after its first frames: wait until the vegetation draws
                Look(cam, At(ter, -235f, 125f, 48f), new Vector3(45f, WaterLevel, -55f));   // from the west, the sun to the left: the dunes side-lit (from the north-east it stood behind the camera and flattened everything)
                int waited = 0;
                for (; waited < 80 && FoliageService.LastInstancesDrawn == 0; waited++) { EditorViewportSession.RequestResubmit(); await Task.Delay(400); }
                if (FoliageService.LastInstancesDrawn == 0)
                {
                    // the built-in checks before this one (scene switch, export, play + stop) can leave the vegetation's
                    // runtimes without their models: rebuild them from scratch and wait once more
                    var fol = TemplateSetupSmoke.Find(scene, e => e.GetComponent<FoliageComponent>() != null);
                    log.LogWarning("oasis capture: no vegetation drawn after " + waited + " waits — foliage entity " + (fol != null ? fol.Name + " active=" + fol.IsActive + " hidden=" + fol.IsHiddenInEditor + " enabled=" + (fol.GetComponent<FoliageComponent>()?.IsEnabled) : "MISSING") + "; rebuilding the runtimes");
                    FoliageService.Clear();
                    for (waited = 0; waited < 40 && FoliageService.LastInstancesDrawn == 0; waited++) { EditorViewportSession.RequestResubmit(); await Task.Delay(400); }
                }
                log.Log("oasis capture: " + FoliageService.LastInstancesDrawn + " vegetation instances drawn before the pictures (" + FoliageService.LastDrawCalls + " draws, " + waited + " waits)");
                await SmokeRegistry.Settle(1500);
                // the reference's aerial view: high over the dunes south-east of the oasis, the lakes in the middle distance, the
                // dune sea and the mountains behind
                Look(cam, At(ter, 330f, 300f, 150f), new Vector3(-10f, WaterLevel, -30f));
                await SmokeRegistry.Settle(2500);
                await CameraSkySmoke.Sample("oasis_aerial.bmp", 0.5, 0.5);
                // the reference view: from the dunes east of the lakes, the sun behind the camera, both lakes and the palms ahead
                Look(cam, At(ter, -235f, 125f, 48f), new Vector3(45f, WaterLevel, -55f));   // from the west, the sun to the left: the dunes side-lit (from the north-east it stood behind the camera and flattened everything)
                await SmokeRegistry.Settle(1500);
                await CameraSkySmoke.Sample("oasis_wide.bmp", 0.5, 0.5);
                // at the west lake's south shore, eye height, across the water to the palms on the far bank
                Look(cam, At(ter, -24f, -58f, 1.7f), new Vector3(-34f, WaterLevel + 0.3f, 40f));
                await SmokeRegistry.Settle(800);
                await CameraSkySmoke.Sample("oasis_shore.bmp", 0.5, 0.5);
                // the grove: eye height on the grove floor west of the big lake, looking INTO the low sun through the crowns —
                // long shadows towards the camera, sun shafts, the ferns lit from behind (the reference's grove pictures)
                {
                    var sunE = TemplateSetupSmoke.Find(scene, e => e.Name == "Sun");
                    var toSunDir = new Vector3(0.43f, 0.0f, 0.55f);
                    if (sunE != null)
                    {
                        float yd = sunE.Transform.LocalRotation.Y * (float)Math.PI / 180f, pd = sunE.Transform.LocalRotation.X * (float)Math.PI / 180f;
                        // the light travels along the entity's forward; towards the sun is the opposite, flattened
                        var fwd = new Vector3((float)(Math.Sin(yd) * Math.Cos(pd)), (float)(-Math.Sin(pd)), (float)(Math.Cos(yd) * Math.Cos(pd)));
                        toSunDir = new Vector3(-fwd.X, 0f, -fwd.Z);
                    }
                    float tl = (float)Math.Sqrt(toSunDir.X * toSunDir.X + toSunDir.Z * toSunDir.Z); if (tl < 1e-3f) tl = 1f;
                    toSunDir = new Vector3(toSunDir.X / tl, 0f, toSunDir.Z / tl);
                    // the densest spot of the palm belt: the palm with the most palms within 20 m
                    float gx = -96f, gz = 6f;
                    {
                        var fol3 = TemplateSetupSmoke.Find(scene, e => e.GetComponent<FoliageComponent>() != null);
                        FoliageData fd3;
                        if (fol3 != null && FoliageService.TryGetData(fol3, out fd3))
                        {
                            var pts = new List<Vector3>();
                            foreach (var ly in fd3.Layers) if (ly.TypeName.StartsWith("Palm")) for (int k = 0; k < ly.Count; k++) pts.Add(new Vector3(ly.Instances[k].Position.X, ly.Instances[k].Position.Y, ly.Instances[k].Position.Z));
                            int best = -1, bestN = -1;
                            for (int k = 0; k < pts.Count; k += 5)
                            {
                                int cnt = 0;
                                for (int q = 0; q < pts.Count; q++) { float ddx = pts[q].X - pts[k].X, ddz = pts[q].Z - pts[k].Z; if (ddx * ddx + ddz * ddz < 400f) cnt++; }
                                if (cnt > bestN) { bestN = cnt; best = k; }
                            }
                            if (best >= 0) { gx = pts[best].X; gz = pts[best].Z; log.Log("oasis grove: densest spot (" + gx.ToString("0") + ", " + gz.ToString("0") + "), " + bestN + " palms within 20 m"); }
                        }
                    }
                    // look 50° off the sun: the light rakes across the frame, the trunks throw their shadows towards the camera's side
                    const float rot = 28f * (float)Math.PI / 180f;   // the sun in the frame's upper corner: shafts, backlit fronds, the shadows raking towards the camera
                    var viewDir = new Vector3(toSunDir.X * (float)Math.Cos(rot) - toSunDir.Z * (float)Math.Sin(rot), 0f, toSunDir.X * (float)Math.Sin(rot) + toSunDir.Z * (float)Math.Cos(rot));
                    var eye = At(ter, gx - viewDir.X * 3f + viewDir.Z * 2.5f, gz - viewDir.Z * 3f - viewDir.X * 2.5f, 1.6f);
                    Look(cam, eye, new Vector3(eye.X + viewDir.X * 30f, eye.Y + 6.5f, eye.Z + viewDir.Z * 30f));
                    await SmokeRegistry.Settle(1200);
                    await CameraSkySmoke.Sample("oasis_grove.bmp", 0.5, 0.5);
                    // the same frame without the volumetric haze, then without the fog's sun glow and the bloom: which of them whites out the sun's side?
                    {
                        var stg = scene.Settings;
                        bool vol = stg.VolumetricEnabled; float glow = stg.FogSunGlow, bloom = stg.BloomIntensity;
                        stg.VolumetricEnabled = false; stg.Apply(); await SmokeRegistry.Settle(700); await CameraSkySmoke.Sample("oasis_grove_novol.bmp", 0.5, 0.5);
                        stg.FogSunGlow = 0f; stg.BloomIntensity = 0f; stg.Apply(); await SmokeRegistry.Settle(700); await CameraSkySmoke.Sample("oasis_grove_noglow.bmp", 0.5, 0.5);
                        stg.VolumetricEnabled = vol; stg.FogSunGlow = glow; stg.BloomIntensity = bloom; stg.Apply(); await SmokeRegistry.Settle(400);
                        // the grade: the same frame nearly grey — proves the post colour grade reaches the picture (it seemed not to on 2026-10-11)
                        float sat = stg.Saturation; stg.Saturation = 0.3f; stg.Apply(); await SmokeRegistry.Settle(500); await CameraSkySmoke.Sample("oasis_grove_desat.bmp", 0.5, 0.5);
                        stg.Saturation = sat; stg.Apply(); await SmokeRegistry.Settle(300);
                    }
                }
                // close-ups: the nearest bush and the nearest palm, the sun behind the camera
                CloseUp(cam, ter, "Searsia burchellii", 4.5f, 1.4f);
                await SmokeRegistry.Settle(800);
                await CameraSkySmoke.Sample("oasis_bush.bmp", 0.5, 0.5);
                CloseUp(cam, ter, "Palm straight", 14f, 6f);
                await SmokeRegistry.Settle(800);
                await CameraSkySmoke.Sample("oasis_palm.bmp", 0.5, 0.5);
                // a palm base from 3 m: the trunk must stand IN the sand (trunks floated on 2026-10-11) — and the numbers behind it
                {
                    var fol2 = TemplateSetupSmoke.Find(scene, e => e.GetComponent<FoliageComponent>() != null);
                    FoliageData fd2;
                    if (fol2 != null && FoliageService.TryGetData(fol2, out fd2))
                    {
                        var layer = fd2.Layer("Palm straight", false);
                        if (layer != null && layer.Count > 0)
                        {
                            var inst = layer.Instances[layer.Count / 2];
                            float gh; TerrainService.TryHeight(ter, inst.Position.X, inst.Position.Z, out gh);
                            log.Log("oasis base: instance y " + inst.Position.Y.ToString("0.00") + ", terrain " + gh.ToString("0.00") + " at (" + inst.Position.X.ToString("0.0") + ", " + inst.Position.Z.ToString("0.0") + "), scale " + inst.Scale.ToString("0.00"));
                            // the engine-side mesh: does the imported palm start at y 0 as the file says?
                            var subs = SceneRenderService.LoadModelSubmeshes("Assets/Models/Palms/date_palm_a/date_palm_a.gltf");
                            if (subs != null)
                                for (int si = 0; si < subs.Length; si++)
                                {
                                    float sx, sy, sz, cx, cy, cz;
                                    Editor.DllWrapper.VortexAPI.GetMeshBounds(subs[si].MeshId, out sx, out sy, out sz);
                                    Editor.DllWrapper.VortexAPI.GetMeshBoundsCenter(subs[si].MeshId, out cx, out cy, out cz);
                                    log.Log("oasis base: palm submesh " + si + " mesh " + subs[si].MeshId + " size (" + sx.ToString("0.00") + ", " + sy.ToString("0.00") + ", " + sz.ToString("0.00") + ") centre (" + cx.ToString("0.00") + ", " + cy.ToString("0.00") + ", " + cz.ToString("0.00") + ") bottom y " + (cy - sy * 0.5f).ToString("0.00"));
                                }
                            var target = new Vector3(inst.Position.X, inst.Position.Y + 0.5f, inst.Position.Z);
                            Look(cam, At(ter, inst.Position.X + 2.2f, inst.Position.Z + 2.2f, 0.7f), target);
                            await SmokeRegistry.Settle(800);
                            await CameraSkySmoke.Sample("oasis_base.bmp", 0.5, 0.5);
                            // the same view without the far dunes, then without the oasis terrain: which sheet is the visible floor?
                            var farT = TemplateSetupSmoke.Find(scene, e => e.Name == "Far Dunes");
                            if (farT != null) { farT.IsActive = false; EditorViewportSession.RequestResubmit(); await SmokeRegistry.Settle(800); await CameraSkySmoke.Sample("oasis_base_nofar.bmp", 0.5, 0.5); farT.IsActive = true; }
                            ter.IsActive = false; EditorViewportSession.RequestResubmit(); await SmokeRegistry.Settle(800); await CameraSkySmoke.Sample("oasis_base_nooasis.bmp", 0.5, 0.5); ter.IsActive = true;
                            EditorViewportSession.RequestResubmit(); await SmokeRegistry.Settle(600);
                            log.Log("oasis base: chunk submits last frame " + TerrainService.LastChunkSubmits);
                        }
                    }
                }
                // across the east lake towards the escarpment
                Look(cam, At(ter, 40f, 30f, 1.8f), new Vector3(90f, WaterLevel + 2f, -120f));
                await SmokeRegistry.Settle(800);
                await CameraSkySmoke.Sample("oasis_lake_east.bmp", 0.5, 0.5);
                // from a dune crest in the south-west
                Look(cam, At(ter, -195f, 175f, 5f), new Vector3(10f, -2f, -10f));
                await SmokeRegistry.Settle(800);
                await CameraSkySmoke.Sample("oasis_dunes.bmp", 0.5, 0.5);
                // the map from above
                Look(cam, new Vector3(0f, 430f, 1f), new Vector3(0f, 0f, 0f));
                await SmokeRegistry.Settle(800);
                await CameraSkySmoke.Sample("oasis_top.bmp", 0.5, 0.5);
                // the weather: the sandstorm and the dusk presets from the shore, then clear again
                var storm = VortexEditor.Claude.Tools.EnvironmentTools.FindWeather("sandstorm");
                var dusk = VortexEditor.Claude.Tools.EnvironmentTools.FindWeather("dusk");
                var clear = VortexEditor.Claude.Tools.EnvironmentTools.FindWeather("clear");
                if (storm != null && clear != null && dusk != null)
                {
                    VortexEditor.Claude.Tools.EnvironmentTools.ApplyWeather(scene, storm, 1f);
                    Look(cam, At(ter, 30f, -4f, 1.8f), new Vector3(95f, WaterLevel + 2f, -60f));   // beside the player: the sand sheets run past
                    await SmokeRegistry.Settle(3500);
                    await CameraSkySmoke.Sample("oasis_sandstorm.bmp", 0.5, 0.5);
                    VortexEditor.Claude.Tools.EnvironmentTools.ApplyWeather(scene, dusk, 1f);
                    Look(cam, At(ter, -235f, 125f, 48f), new Vector3(45f, WaterLevel, -55f));   // from the west, the sun to the left: the dunes side-lit (from the north-east it stood behind the camera and flattened everything)
                    await SmokeRegistry.Settle(1200);
                    await CameraSkySmoke.Sample("oasis_dusk.bmp", 0.5, 0.5);
                    VortexEditor.Claude.Tools.EnvironmentTools.ApplyWeather(scene, clear, 1f);
                    EditorViewportSession.RequestResubmit();
                }
                // footprints: a few strides stamped along the shore the way Footprints.cs stamps them, for a look at the decals
                {
                    var p0 = footprintStart;
                    float dirX = 0.6f, dirZ = 0.8f;
                    for (int i = 0; i < 8; i++)
                    {
                        bool left = (i & 1) == 0; float side = left ? -0.17f : 0.17f;
                        float fx = p0.X + dirX * i * 0.78f + (-dirZ) * side, fz = p0.Z + dirZ * i * 0.78f + dirX * side;
                        float fy; if (!TerrainService.TryHeight(ter, fx, fz, out fy)) continue;
                        NVec3 n; if (!TerrainService.TryNormal(ter, fx, fz, out n)) n = NVec3.UnitY;
                        float yaw = (float)(Math.Atan2(dirX, dirZ) * 180.0 / Math.PI);
                        Editor.Core.Services.Decals.DecalService.Spawn(left ? "Assets/Materials/Decals/Footprint_Sand_L.vmat" : "Assets/Materials/Decals/Footprint_Sand_R.vmat",
                            new NVec3(fx, fy, fz), n, new NVec3(0.165f, 1.5f, 0.33f), 0f, yaw, 0.78f, 0.7f, 0.6f, 0.9f, 1, 0.5f, 70f, 1);
                    }
                    log.Log("oasis capture: " + Editor.Core.Services.Decals.DecalService.SpawnedCount + " footprints stamped for the picture");
                    Look(cam, At(ter, -17.2f, -63.5f, 1.2f), new Vector3(p0.X + dirX * 2.5f, p0.Y, p0.Z + dirZ * 2.5f));
                    await SmokeRegistry.Settle(3000);
                    await CameraSkySmoke.Sample("oasis_footprints.bmp", 0.5, 0.5);
                    Editor.Core.Services.Decals.DecalService.Clear();
                }
                // one of the infected up close, in the editor: a prefab instance on the shore, removed again after the picture
                GameEntity shown = null;
                try { shown = PrefabService.Instance.InstantiatePrefab("Assets/Prefabs/Zombie.ventity", scene, null, false); } catch { }
                if (shown != null)
                {
                    var spot = At(ter, -10f, -52f, 0f);
                    shown.Transform.LocalPosition = spot;
                    shown.Transform.LocalRotation = new Vector3(0f, 200f, 0f);
                    EditorViewportSession.RequestResubmit();
                    await SmokeRegistry.Settle(1500);
                    Look(cam, At(ter, spot.X + 1.2f, spot.Z - 4.2f, 1.6f), new Vector3(spot.X, spot.Y + 1.1f, spot.Z));   // eye height over the ground there
                    await SmokeRegistry.Settle(800);
                    await CameraSkySmoke.Sample("oasis_infected.bmp", 0.5, 0.5);
                    EditorCommands.DeleteEntities(new List<GameEntity> { shown });
                    EditorViewportSession.RequestResubmit();
                }
                // the infected in play: the director spawns its first wave a moment after Start
                var player = TemplateSetupSmoke.Find(scene, e => e.Name == "Player" || e.Tag == "Player");
                if (player != null && NavigationService.Available)
                {
                    EditorCommands.Play(); playing = true;
                    await SmokeRegistry.Settle(6500);    // the first wave spawns ahead of the player ~1.5 s in and sprints at them
                    GameEntity nearest = null; float best = float.MaxValue;
                    var pp = TransformMath.WorldPosition(player);
                    foreach (var e in scene.Entities)
                    {
                        if (e == null || !e.Name.StartsWith("Zombie", StringComparison.Ordinal) || e.GetComponent<Script>() == null) continue;
                        var zp = TransformMath.WorldPosition(e); float d = TemplateSetupSmoke.Dist(zp, pp);
                        if (d < best) { best = d; nearest = e; }
                    }
                    int count = 0; foreach (var e in scene.Entities) if (e != null && e.Name.StartsWith("Zombie", StringComparison.Ordinal) && e.GetComponent<Script>() != null) count++;
                    {
                        // the player must stand on the sand, not fall through it (reported 2026-10-11)
                        float gy; bool onTer = TerrainService.TryHeight(ter, pp.X, pp.Z, out gy);
                        float dyp = onTer ? pp.Y - gy : float.NaN;
                        if (!onTer || dyp < -0.5f || dyp > 6f) log.LogError("oasis play: the player is " + (onTer ? dyp.ToString("0.00") + " m above the ground" : "off the terrain") + " after 6.5 s of play (y " + pp.Y.ToString("0.00") + ") — fell through?");
                        else log.Log("oasis play: the player stands " + dyp.ToString("0.00") + " m above the ground after 6.5 s of play (y " + pp.Y.ToString("0.00") + ")");
                    }
                    // the game camera renders in play: the infected come into the player's view on their own
                    await CameraSkySmoke.Sample("oasis_zombie.bmp", 0.5, 0.5);
                    log.Log("oasis capture: " + count + " zombies in play, nearest " + (nearest != null ? best.ToString("0") + " m from the player" : "none"));
                }
                log.Log("oasis capture: wide / shore / east lake / dunes / top captured; " + FoliageService.LastInstancesDrawn + " instances in the last frame, " + FoliageService.LastDrawCalls + " draws, " + Editor.DllWrapper.VortexAPI.CurrentFPS + " fps in the editor viewport");
                // ---- the performance probe: the frame rate of the wide view with one feature at a time switched off ----
                await PerfProbe(scene, ter, cam, log);
                return true;
            }
            finally
            {
                if (playing) { EditorCommands.Stop(); await SmokeRegistry.Settle(600); }
                if (win != null && winW > 0 && winH > 0) { try { win.Width = winW; win.Height = winH; } catch { } }
                if (gridWas && !EditorViewportService.Instance.IsGridVisible) EditorViewportService.Instance.ToggleGrid();
                EditorViewportService.Instance.AreGizmosVisible = gizmosWere;
                cam.SetPositionAndRotation(px, py, pz, yaw0, pitch0);
                EditorViewportSession.RequestResubmit();
            }
        }

        /// <summary>Camera at <paramref name="distance"/> from the first instance of a foliage type (towards the sun, so the sun is
        /// behind the camera), looking at it <paramref name="height"/> above its base.</summary>
        private static void CloseUp(EditorCameraController cam, GameEntity ter, string typeName, float distance, float height)
        {
            var scene = ProjectData.Current?.ActiveScene;
            var fol = scene == null ? null : TemplateSetupSmoke.Find(scene, e => e.Name == "Oasis Vegetation");
            FoliageData data;
            if (fol == null || !FoliageService.TryGetData(fol, out data)) return;
            var layer = data.Layer(typeName, false);
            if (layer == null || layer.Count == 0) return;
            var inst = layer.Instances[layer.Count / 2];
            var toSun = new Vector3(0.626f, 0f, 0.780f);
            var target = new Vector3(inst.Position.X, inst.Position.Y + height, inst.Position.Z);
            var from = new Vector3(target.X + toSun.X * distance, 0f, target.Z + toSun.Z * distance);
            float gy; if (!TerrainService.TryHeight(ter, from.X, from.Z, out gy)) gy = target.Y;
            from.Y = Math.Max(gy + 1.6f, target.Y - height * 0.3f);
            Look(cam, from, target);
        }

        /// <summary>Every camera under <paramref name="e"/> sees at least <paramref name="far"/> metres.</summary>
        private static void SetFarClip(GameEntity e, float far)
        {
            if (e == null) return;
            var cam = e.GetComponent<Camera>();
            if (cam != null && cam.FarClip < far) cam.FarClip = far;
            if (e.Children != null) foreach (var c in e.Children) SetFarClip(c, far);
        }

        /// <summary>Where the milliseconds go: the frame rate of the wide view, then with one feature at a time switched off
        /// (volumetric fog, FXAA, soft → hard → no sun shadows, the vegetation, the far dunes, the water, SSAO + bloom), everything
        /// restored afterwards. The renderer's fps counter updates once a second, so every reading waits two.</summary>
        private static async Task PerfProbe(Scene scene, GameEntity ter, EditorCameraController cam, ConsoleService log)
        {
            var st = scene.Settings;
            var sunE = TemplateSetupSmoke.Find(scene, e => e.GetComponent<Light>() != null && e.GetComponent<Light>().LightType == LightType.Directional);
            var sun = sunE?.GetComponent<Light>();
            var fol = TemplateSetupSmoke.Find(scene, e => e.GetComponent<FoliageComponent>() != null);
            var far = TemplateSetupSmoke.Find(scene, e => e.Name == "Far Dunes");
            var lakes = new List<GameEntity>();
            foreach (var e in scene.Entities) if (e != null && e.GetComponent<WaterComponent>() != null) lakes.Add(e);
            Look(cam, At(ter, -235f, 125f, 48f), new Vector3(45f, WaterLevel, -55f));   // from the west, the sun to the left: the dunes side-lit (from the north-east it stood behind the camera and flattened everything)
            var sb = new System.Text.StringBuilder();
            async Task<int> Read(string what)
            {
                EditorViewportSession.RequestResubmit();
                await SmokeRegistry.Settle(2300);
                int fps = Editor.DllWrapper.VortexAPI.CurrentFPS;
                sb.Append(what).Append(' ').Append(fps).Append(" fps | ");
                return fps;
            }
            await Read("baseline");
            bool vol = st.VolumetricEnabled; st.VolumetricEnabled = false; st.Apply(); await Read("no volumetrics"); st.VolumetricEnabled = vol; st.Apply();
            bool aa = st.AntiAliasEnabled; st.AntiAliasEnabled = false; st.Apply(); await Read("no FXAA"); st.AntiAliasEnabled = aa; st.Apply();
            if (sun != null)
            {
                var shadow = sun.ShadowType;
                sun.ShadowType = ShadowType.Hard; await Read("hard sun shadows");
                sun.ShadowType = ShadowType.None; await Read("no sun shadows");
                sun.ShadowType = shadow;
            }
            if (fol != null) { fol.IsActive = false; await Read("no vegetation"); fol.IsActive = true; }
            if (far != null) { far.IsActive = false; await Read("no far dunes"); far.IsActive = true; }
            foreach (var l in lakes) l.IsActive = false; await Read("no water"); await Read("no water (2nd read)"); foreach (var l in lakes) l.IsActive = true;
            bool gi = st.GiEnabled; st.GiEnabled = false; st.Apply(); await Read("no GI"); st.GiEnabled = gi; st.Apply();
            bool ao = st.AoEnabled, bloom = st.BloomEnabled; st.AoEnabled = false; st.BloomEnabled = false; st.Apply(); await Read("no SSAO + bloom"); st.AoEnabled = ao; st.BloomEnabled = bloom; st.Apply();
            EditorViewportSession.RequestResubmit();
            await SmokeRegistry.Settle(800);
            log.Log("oasis perf: " + sb.ToString());
        }

        private static Vector3 At(GameEntity ter, float x, float z, float above)
        {
            float y; if (!TerrainService.TryHeight(ter, x, z, out y)) y = 0f;
            return new Vector3(x, y + above, z);
        }

        private static void Look(EditorCameraController cam, Vector3 from, Vector3 to)
        {
            float yaw, pitch;
            TransformMath.LookAngles(from, to, out yaw, out pitch);
            cam.SetPositionAndRotation(from.X, from.Y, from.Z, yaw, pitch);
            EditorViewportSession.RequestResubmit();
        }
    }
}
