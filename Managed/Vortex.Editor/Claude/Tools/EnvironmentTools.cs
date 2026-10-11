using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;
using ModelContextProtocol.Server;

namespace VortexEditor.Claude.Tools
{
    /// <summary>The scene's air and sky for the MCP server (the Open World template is built through these): the Skybox
    /// (gradient / HDR texture, exposure, ambient, the environment colours), the sun aligned to an HDRI's brightest pixel,
    /// and the per-scene environment settings (fog and height fog, SSAO, bloom, volumetrics, grading, vignette).</summary>
    [McpServerToolType, DisplayName("Environment")]
    public static class EnvironmentTools
    {
        private static Scene Scene() => ProjectData.Current?.ActiveScene ?? throw new ToolError("No scene is open.");

        [McpServerTool(Name = "set_sky", Idempotent = true)]
        [Description("Sets the scene's sky: a gradient (top / horizon / bottom colours) or an equirectangular HDR / image texture, its exposure and ambient " +
                     "strength. With a texture the gradient colours stay the ENVIRONMENT (what water and glossy surfaces reflect) — give them the picture's " +
                     "zenith / horizon / ground colours. align_sun turns the scene's directional light to the brightest pixel of the HDR (its sun). " +
                     "Creates a Sky entity with a Skybox component when the scene has none. Colours are linear rgb 0..1. One undo step.")]
        public static object SetSky(
            [Description("\"gradient\", \"texture\" or \"solid\" (default: keep)")] string type = null,
            [Description("Project-relative equirect .hdr / .png / .jpg (implies type texture)")] string texture = null,
            [Description("Exposure multiplier (default: keep; 0.7–1.0 for a midday HDR)")] float exposure = -1f,
            [Description("Ambient light strength (default: keep; ~1.2 for a desert day)")] float ambient = -1f,
            [Description("Zenith colour [r, g, b]")] float[] top_color = null,
            [Description("Horizon colour [r, g, b]")] float[] horizon_color = null,
            [Description("Ground / bottom colour [r, g, b]")] float[] bottom_color = null,
            [Description("Turn the directional light to the HDR texture's sun (default false)")] bool align_sun = false,
            [Description("Sun intensity when aligning (default: keep)")] float sun_intensity = -1f,
            [Description("Only report")] bool dry_run = false)
        {
            var scene = Scene();
            Skybox sky = null; GameEntity skyEntity = null;
            foreach (var e in SceneModel.All(scene)) { var s = e.GetComponent<Skybox>(); if (s != null) { sky = s; skyEntity = e; break; } }
            if (dry_run) return new { sky = sky != null ? skyEntity.Name : "(would create)", type, texture, exposure, ambient, align_sun };
            if (sky == null)
            {
                skyEntity = new GameEntity(scene, "Sky");
                sky = new Skybox(skyEntity) { IsEnabled = true, SkyboxType = SkyboxType.Gradient };
                skyEntity.AddComponent(sky);
                scene.AddEntity(skyEntity);
            }
            if (!string.IsNullOrEmpty(texture))
            {
                string full = Path.IsPathRooted(texture) ? texture : Path.Combine(ProjectData.Current.Path, texture);
                if (!File.Exists(full)) throw new ToolError("texture not found: " + texture);
                sky.TexturePath = texture.Replace('\\', '/');
                sky.SkyboxType = SkyboxType.Texture;
            }
            if (!string.IsNullOrEmpty(type))
            {
                switch (type.ToLowerInvariant())
                {
                    case "gradient": sky.SkyboxType = SkyboxType.Gradient; break;
                    case "texture": sky.SkyboxType = SkyboxType.Texture; break;
                    case "solid": sky.SkyboxType = SkyboxType.SolidColor; break;
                    default: throw new ToolError("type must be gradient, texture or solid.");
                }
            }
            if (exposure >= 0f) sky.Exposure = exposure;
            if (ambient >= 0f) sky.AmbientIntensity = ambient;
            if (top_color != null && top_color.Length >= 3) { sky.TopColorR = top_color[0]; sky.TopColorG = top_color[1]; sky.TopColorB = top_color[2]; }
            if (horizon_color != null && horizon_color.Length >= 3) { sky.HorizonColorR = horizon_color[0]; sky.HorizonColorG = horizon_color[1]; sky.HorizonColorB = horizon_color[2]; }
            if (bottom_color != null && bottom_color.Length >= 3) { sky.BottomColorR = bottom_color[0]; sky.BottomColorG = bottom_color[1]; sky.BottomColorB = bottom_color[2]; }
            sky.IsEnabled = true;

            object sun = null;
            if (align_sun)
            {
                if (sky.SkyboxType != SkyboxType.Texture || string.IsNullOrEmpty(sky.TexturePath)) throw new ToolError("align_sun needs a texture sky.");
                string full = Path.IsPathRooted(sky.TexturePath) ? sky.TexturePath : Path.Combine(ProjectData.Current.Path, sky.TexturePath);
                Vector3 toSun; float azimuth, elevation;
                if (!HdrSun.Find(full, out toSun, out azimuth, out elevation)) throw new ToolError("could not read the sun from " + sky.TexturePath + " (Radiance .hdr only).");
                GameEntity sunEntity = null; Light light = null;
                foreach (var e in SceneModel.All(scene)) { var l = e.GetComponent<Light>(); if (l != null && l.LightType == LightType.Directional) { light = l; sunEntity = e; break; } }
                if (light == null)
                {
                    sunEntity = new GameEntity(scene, "Sun");
                    light = new Light(sunEntity, LightType.Directional) { Intensity = 4f, ShadowType = ShadowType.Soft };
                    sunEntity.AddComponent(light);
                    scene.AddEntity(sunEntity);
                }
                float yaw, pitch;
                TransformMath.LookAngles(new Vector3(0f, 0f, 0f), new Vector3(-toSun.X, -toSun.Y, -toSun.Z), out yaw, out pitch);
                if (sunEntity.Transform != null) sunEntity.Transform.LocalRotation = new Vector3(pitch, yaw, 0f);
                if (sun_intensity >= 0f) light.Intensity = sun_intensity;
                sun = new { entity = sunEntity.Name, azimuth_deg = Math.Round(azimuth, 1), elevation_deg = Math.Round(elevation, 1), rotation = new[] { pitch, yaw, 0f }, intensity = light.Intensity };
            }
            ToolContext.UndoLabel = "sky";
            Editor.Core.Viewport.EditorViewportSession.RequestResubmit();
            return new { entity = skyEntity.Name, type = sky.SkyboxType.ToString(), texture = sky.TexturePath, exposure = sky.Exposure, ambient = sky.AmbientIntensity,
                         top = new[] { sky.TopColorR, sky.TopColorG, sky.TopColorB }, horizon = new[] { sky.HorizonColorR, sky.HorizonColorG, sky.HorizonColorB }, bottom = new[] { sky.BottomColorR, sky.BottomColorG, sky.BottomColorB }, sun };
        }

        [McpServerTool(Name = "set_environment", Idempotent = true)]
        [Description("Sets the scene's environment settings (saved with the scene): exp² distance fog with optional height fog, SSAO, bloom, volumetric fog, " +
                     "colour grading, vignette, the scene exposure (stops before the tonemapper) and the fog's sun glow. Every argument is optional — only the ones given change. Fog density 0.002 ≈ a 500 m desert haze, 0.02 a wood, " +
                     "0.08 a dungeon; height fog thins above fog_height_y by exp(-falloff·Δy). One undo step.")]
        public static object SetEnvironment(
            [Description("Fog on / off")] bool? fog = null,
            [Description("Fog density (exp², per metre)")] float fog_density = -1f,
            [Description("Fog colour [r, g, b]")] float[] fog_color = null,
            [Description("Height (m) up to which the fog is uniform")] float? fog_height_y = null,
            [Description("Height falloff per metre above it (0 = no height fog)")] float fog_height_falloff = -1f,
            [Description("Aerial perspective: the sun's glow in the fog when looking towards it (0 = off, 0.3–0.6 a desert noon, 1–2 a low sun)")] float? fog_sun_glow = null,
            [Description("Anti-aliasing (FXAA) on / off")] bool? anti_alias = null,
            [Description("Screen-space global illumination on / off (one bounce of the lit world; needs SSAO on)")] bool? gi = null,
            [Description("GI strength (1 = a physical diffuse bounce)")] float gi_strength = -1f,
            [Description("GI trace radius in metres")] float gi_radius = -1f,
            [Description("Ground bounce strength: the sun's light bounced off the ground into the shadows (0 = off, a desert 0.4–0.7)")] float? bounce_strength = null,
            [Description("Ground bounce colour [r, g, b] — the ground's albedo (sand 0.55, 0.40, 0.26)")] float[] bounce_color = null,
            [Description("Tonemapper: 'aces' (classic filmic, bright saturated colours drift towards white) or 'neutral' (Khronos PBR Neutral, hue-preserving — a sunlit desert stays orange)")] string tonemapper = null,
            [Description("Scene exposure in stops BEFORE the tonemapper — the camera's aperture (0 = classic; +1.5…+2.5 a sunlit desert, −1 a night). Brightens without clipping, unlike the grading exposure")] float? scene_exposure = null,
            [Description("SSAO on / off")] bool? ao = null,
            [Description("SSAO radius (m)")] float ao_radius = -1f,
            [Description("SSAO intensity")] float ao_intensity = -1f,
            [Description("Bloom on / off")] bool? bloom = null,
            [Description("Bloom threshold")] float bloom_threshold = -1f,
            [Description("Bloom intensity")] float bloom_intensity = -1f,
            [Description("Volumetric fog on / off")] bool? volumetric = null,
            [Description("Volumetric density")] float volumetric_density = -1f,
            [Description("Volumetric sun strength (god rays)")] float volumetric_sun = -1f,
            [Description("Colour grading on / off")] bool? grading = null,
            [Description("Grading exposure (stops, 0 = neutral)")] float? exposure = null,
            [Description("Grading contrast (1 = neutral)")] float contrast = -1f,
            [Description("Grading saturation (1 = neutral)")] float saturation = -1f,
            [Description("Grading temperature (-1 cool .. 1 warm)")] float? temperature = null,
            [Description("Vignette on / off")] bool? vignette = null,
            [Description("Vignette intensity")] float vignette_intensity = -1f,
            [Description("Only report")] bool dry_run = false)
        {
            var scene = Scene();
            var st = scene.Settings;
            if (dry_run) return Info(st);
            if (fog.HasValue) st.FogEnabled = fog.Value;
            if (fog_density >= 0f) st.FogDensity = fog_density;
            if (fog_color != null && fog_color.Length >= 3) { st.FogR = fog_color[0]; st.FogG = fog_color[1]; st.FogB = fog_color[2]; }
            if (fog_height_y.HasValue) st.FogHeightY = fog_height_y.Value;
            if (fog_height_falloff >= 0f) st.FogHeightFalloff = fog_height_falloff;
            if (fog_sun_glow.HasValue) st.FogSunGlow = fog_sun_glow.Value;
            if (scene_exposure.HasValue) st.SceneExposure = scene_exposure.Value;
            if (!string.IsNullOrEmpty(tonemapper)) st.Tonemapper = tonemapper.Trim().ToLowerInvariant().StartsWith("neutral") ? 1 : 0;
            if (anti_alias.HasValue) st.AntiAliasEnabled = anti_alias.Value;
            if (gi.HasValue) st.GiEnabled = gi.Value;
            if (gi_strength >= 0f) st.GiStrength = gi_strength;
            if (gi_radius >= 0f) st.GiRadius = gi_radius;
            if (bounce_strength.HasValue) st.BounceStrength = bounce_strength.Value;
            if (bounce_color != null && bounce_color.Length >= 3) { st.BounceR = bounce_color[0]; st.BounceG = bounce_color[1]; st.BounceB = bounce_color[2]; }
            if (ao.HasValue) st.AoEnabled = ao.Value;
            if (ao_radius >= 0f) st.AoRadius = ao_radius;
            if (ao_intensity >= 0f) st.AoIntensity = ao_intensity;
            if (bloom.HasValue) st.BloomEnabled = bloom.Value;
            if (bloom_threshold >= 0f) st.BloomThreshold = bloom_threshold;
            if (bloom_intensity >= 0f) st.BloomIntensity = bloom_intensity;
            if (volumetric.HasValue) st.VolumetricEnabled = volumetric.Value;
            if (volumetric_density >= 0f) st.VolumetricDensity = volumetric_density;
            if (volumetric_sun >= 0f) st.VolumetricSun = volumetric_sun;
            if (grading.HasValue) st.GradeEnabled = grading.Value;
            if (exposure.HasValue) st.Exposure = exposure.Value;
            if (contrast >= 0f) st.Contrast = contrast;
            if (saturation >= 0f) st.Saturation = saturation;
            if (temperature.HasValue) st.Temperature = temperature.Value;
            if (vignette.HasValue) st.VignetteEnabled = vignette.Value;
            if (vignette_intensity >= 0f) st.VignetteIntensity = vignette_intensity;
            st.Apply();
            scene.IsDirty = true;
            ToolContext.UndoLabel = "environment";
            Editor.Core.Viewport.EditorViewportSession.RequestResubmit();
            return Info(st);
        }

        // ------------------------------------------------------------------ weather

        /// <summary>A weather preset: the air, the sun, the sky's exposure, the wind and which dust effects run. The Open World
        /// template's Weather.cs script blends the same presets at runtime; this applies them in the editor.</summary>
        public sealed class WeatherPreset
        {
            public string Name;
            public float FogDensity, FogHeightY, FogFalloff; public float[] FogColor;
            public float SunIntensity; public float[] SunColor;
            public float SkyExposure, Ambient, Wind;
            public float SceneExposure, FogSunGlow;   // stops before the tonemapper; the sun's glow in the fog
            public bool Haze, Storm;
            public string Sky; public float SunPitch, SunYaw;   // the HDRI of the preset and where its sun is (tools/hdri-analyze.py)
            public float[] SkyTop, SkyHorizon, SkyBottom;          // a gradient sky instead (a sandstorm has no blue sky)
        }

        public static readonly WeatherPreset[] WeatherPresets =
        {
            new WeatherPreset { Name = "clear",     FogDensity = 0.00028f, FogHeightY = -4f, FogFalloff = 0.012f, FogColor = new[] { 0.70f, 0.62f, 0.52f }, SunIntensity = 8f, SunColor = new[] { 1f, 0.90f, 0.72f },  SkyExposure = 0.5f, Ambient = 0.45f, Wind = 0.5f, SceneExposure = 1.7f, FogSunGlow = 0.2f, Sky = "Assets/Skies/syferfontein_18d_clear_puresky_2k.hdr", SunPitch = 18.19f, SunYaw = -144.05f },
            new WeatherPreset { Name = "noon",      FogDensity = 0.0011f, FogHeightY = -4f, FogFalloff = 0.012f, FogColor = new[] { 0.40f, 0.43f, 0.50f }, SunIntensity = 5.5f, SunColor = new[] { 1f, 0.93f, 0.82f },  SkyExposure = 1.0f, Ambient = 1.0f, Wind = 0.5f, SceneExposure = 1.7f, FogSunGlow = 0.45f, Sky = "Assets/Skies/goegap_2k.hdr", SunPitch = 46.32f, SunYaw = -141.24f },
            new WeatherPreset { Name = "haze",      FogDensity = 0.0032f, FogHeightY = 2f,  FogFalloff = 0.02f,  FogColor = new[] { 0.46f, 0.44f, 0.42f }, SunIntensity = 4.6f, SunColor = new[] { 1f, 0.88f, 0.72f },  SkyExposure = 0.9f, Ambient = 0.95f,  Wind = 1.1f, SceneExposure = 1.5f, FogSunGlow = 0.8f, Haze = true, Sky = "Assets/Skies/goegap_2k.hdr", SunPitch = 46.32f, SunYaw = -141.24f },
            new WeatherPreset { Name = "sandstorm", FogDensity = 0.012f,  FogHeightY = 12f, FogFalloff = 0.012f, FogColor = new[] { 0.42f, 0.32f, 0.20f }, SunIntensity = 2.4f, SunColor = new[] { 1f, 0.74f, 0.48f },  SkyExposure = 0.6f,  Ambient = 1.0f,  Wind = 2.6f, SceneExposure = 1.2f, FogSunGlow = 0.6f, Haze = true, Storm = true, SunPitch = 46.32f, SunYaw = -141.24f, SkyTop = new[] { 0.62f, 0.50f, 0.36f }, SkyHorizon = new[] { 0.80f, 0.64f, 0.44f }, SkyBottom = new[] { 0.66f, 0.50f, 0.34f } },
            new WeatherPreset { Name = "dusk",      FogDensity = 0.0022f, FogHeightY = -2f, FogFalloff = 0.025f, FogColor = new[] { 0.34f, 0.26f, 0.22f }, SunIntensity = 3.6f, SunColor = new[] { 1f, 0.64f, 0.38f },  SkyExposure = 1.0f,  Ambient = 0.9f,  Wind = 0.4f, SceneExposure = 1.3f, FogSunGlow = 1.6f, Sky = "Assets/Skies/rogland_sunset_2k.hdr", SunPitch = 12.57f, SunYaw = -144.05f },
        };

        public static WeatherPreset FindWeather(string name)
        {
            foreach (var p in WeatherPresets) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        /// <summary>Apply a weather preset to the open scene in the editor: fog, the directional light, the Skybox exposure
        /// and ambient, the foliage wind, and the "Weather Haze" / "Weather Sand" particle entities' preview.</summary>
        public static void ApplyWeather(Scene scene, WeatherPreset p, float intensity)
        {
            var st = scene.Settings;
            float k = Math.Max(0f, Math.Min(2f, intensity));
            st.FogEnabled = true; st.FogDensity = p.FogDensity * k; st.FogHeightY = p.FogHeightY; st.FogHeightFalloff = p.FogFalloff;
            st.FogR = p.FogColor[0]; st.FogG = p.FogColor[1]; st.FogB = p.FogColor[2];
            st.FogSunGlow = p.FogSunGlow; st.SceneExposure = p.SceneExposure;
            st.Apply();
            foreach (var e in SceneModel.All(scene))
            {
                var l = e.GetComponent<Light>();
                if (l != null && l.LightType == LightType.Directional)
                {
                    l.Intensity = p.SunIntensity; l.ColorR = p.SunColor[0]; l.ColorG = p.SunColor[1]; l.ColorB = p.SunColor[2];
                    if (e.Transform != null) e.Transform.LocalRotation = new Vector3(p.SunPitch, p.SunYaw, 0f);
                }
                var sky = e.GetComponent<Skybox>();
                if (sky != null)
                {
                    sky.Exposure = p.SkyExposure; sky.AmbientIntensity = p.Ambient;
                    if (!string.IsNullOrEmpty(p.Sky) && File.Exists(Path.Combine(ProjectData.Current.Path, p.Sky))) { sky.TexturePath = p.Sky; sky.SkyboxType = SkyboxType.Texture; }
                    else if (p.SkyTop != null)
                    {
                        sky.SkyboxType = SkyboxType.Gradient;
                        sky.TopColorR = p.SkyTop[0]; sky.TopColorG = p.SkyTop[1]; sky.TopColorB = p.SkyTop[2];
                        sky.HorizonColorR = p.SkyHorizon[0]; sky.HorizonColorG = p.SkyHorizon[1]; sky.HorizonColorB = p.SkyHorizon[2];
                        sky.BottomColorR = p.SkyBottom[0]; sky.BottomColorG = p.SkyBottom[1]; sky.BottomColorB = p.SkyBottom[2];
                    }
                }
                var f = e.GetComponent<Editor.ECS.Components.Rendering.Foliage>();
                if (f != null) { f.Wind = p.Wind * (0.5f + 0.5f * k); f.Touch(); }
                var ps = e.GetComponent<ParticleSystem>();
                if (ps != null && (e.Name == "Weather Haze" || e.Name == "Weather Sand"))
                {
                    bool on = e.Name == "Weather Haze" ? p.Haze : p.Storm;
                    ps.PreviewInEditor = on; ps.PlayOnStart = on;   // the preview plays what would play on start; Weather.cs takes over in play
                }
            }
            scene.IsDirty = true;
        }

        [McpServerTool(Name = "set_weather", Idempotent = true)]
        [Description("Applies a weather preset to the scene in the editor — clear, haze, sandstorm or dusk: fog and height fog, the sun's intensity and colour, " +
                     "the sky's exposure and ambient, the foliage wind, and the \"Weather Haze\" / \"Weather Sand\" dust effects (entities with a ParticleSystem " +
                     "under the player, created by the Open World template). intensity scales the fog (0.5 light .. 2 heavy). At runtime the template's " +
                     "Weather.cs blends the same presets (F6 / F7). One undo step.")]
        public static object SetWeather(
            [Description("clear | haze | sandstorm | dusk")] string preset,
            [Description("0.5 .. 2 (default 1)")] float intensity = 1f,
            [Description("Only report")] bool dry_run = false)
        {
            var scene = Scene();
            var p = FindWeather(preset) ?? throw new ToolError("preset must be clear, haze, sandstorm or dusk.");
            if (dry_run) return new { would_apply = p.Name, intensity };
            ApplyWeather(scene, p, intensity);
            ToolContext.UndoLabel = "weather " + p.Name;
            Editor.Core.Viewport.EditorViewportSession.RequestResubmit();
            return new { preset = p.Name, intensity, fog_density = p.FogDensity * intensity, sun = p.SunIntensity, wind = p.Wind, haze = p.Haze, storm = p.Storm };
        }

        [McpServerTool(Name = "environment_info", ReadOnly = true, Idempotent = true)]
        [Description("The scene's environment settings (fog, SSAO, bloom, volumetrics, grading, vignette) and its sky and directional light.")]
        public static object EnvironmentInfo()
        {
            var scene = Scene();
            object sky = null, sun = null;
            foreach (var e in SceneModel.All(scene))
            {
                var s = e.GetComponent<Skybox>();
                if (s != null && sky == null) sky = new { entity = e.Name, type = s.SkyboxType.ToString(), texture = s.TexturePath, exposure = s.Exposure, ambient = s.AmbientIntensity };
                var l = e.GetComponent<Light>();
                if (l != null && l.LightType == LightType.Directional && sun == null)
                    sun = new { entity = e.Name, rotation = e.Transform != null ? new[] { e.Transform.LocalRotation.X, e.Transform.LocalRotation.Y, e.Transform.LocalRotation.Z } : null, intensity = l.Intensity, color = new[] { l.ColorR, l.ColorG, l.ColorB }, shadows = l.ShadowType.ToString() };
            }
            return new { settings = Info(scene.Settings), sky, sun };
        }

        private static object Info(SceneSettings st) => new
        {
            exposure = new { scene_stops = st.SceneExposure, tonemapper = st.Tonemapper == 1 ? "neutral" : "aces", anti_alias = st.AntiAliasEnabled },
            bounce = new { strength = st.BounceStrength, color = new[] { st.BounceR, st.BounceG, st.BounceB } },
            gi = new { on = st.GiEnabled, strength = st.GiStrength, radius = st.GiRadius, mode = "screen-space" },
            fog = new { on = st.FogEnabled, density = st.FogDensity, color = new[] { st.FogR, st.FogG, st.FogB }, height_y = st.FogHeightY, height_falloff = st.FogHeightFalloff, sun_glow = st.FogSunGlow },
            ao = new { on = st.AoEnabled, radius = st.AoRadius, intensity = st.AoIntensity },
            bloom = new { on = st.BloomEnabled, threshold = st.BloomThreshold, intensity = st.BloomIntensity },
            volumetric = new { on = st.VolumetricEnabled, density = st.VolumetricDensity, sun = st.VolumetricSun },
            grading = new { on = st.GradeEnabled, exposure = st.Exposure, contrast = st.Contrast, saturation = st.Saturation, temperature = st.Temperature },
            vignette = new { on = st.VignetteEnabled, intensity = st.VignetteIntensity }
        };
    }

    /// <summary>Reads a Radiance RGBE (.hdr) picture and finds its sun — the brightest texel — as a world direction in the
    /// engine's sky mapping (u = atan2(x, z) / 2π + 0.5, v from the top = zenith).</summary>
    internal static class HdrSun
    {
        public static bool Find(string path, out Vector3 toSun, out float azimuthDeg, out float elevationDeg)
        {
            toSun = new Vector3(0f, 1f, 0f); azimuthDeg = 0f; elevationDeg = 90f;
            try
            {
                using (var fs = File.OpenRead(path))
                using (var br = new BinaryReader(fs))
                {
                    string line = ReadLine(br);
                    if (!line.StartsWith("#?")) return false;
                    int w = 0, h = 0;
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
                    double best = -1; int bx = 0, by = 0;
                    var row = new byte[w * 4];
                    for (int y = 0; y < h; y++)
                    {
                        if (!ReadScanline(br, row, w)) return false;
                        for (int x = 0; x < w; x++)
                        {
                            int e = row[x * 4 + 3];
                            if (e == 0) continue;
                            double f = Math.Pow(2.0, e - 136);
                            double lum = (row[x * 4] * 0.2126 + row[x * 4 + 1] * 0.7152 + row[x * 4 + 2] * 0.0722) * f;
                            if (lum > best) { best = lum; bx = x; by = y; }
                        }
                    }
                    azimuthDeg = 360f * (bx + 0.5f) / w;
                    elevationDeg = 90f - 180f * (by + 0.5f) / h;
                    double phi = (azimuthDeg / 360.0 - 0.5) * 2.0 * Math.PI, th = elevationDeg * Math.PI / 180.0;
                    toSun = new Vector3((float)(Math.Cos(th) * Math.Sin(phi)), (float)Math.Sin(th), (float)(Math.Cos(th) * Math.Cos(phi)));
                    return true;
                }
            }
            catch { return false; }
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

        // new-style RLE scanline (2 2 w_hi w_lo), else flat RGBE
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
                    if (count > 128)
                    {
                        count -= 128; byte v = br.ReadByte();
                        for (int i = 0; i < count && x < w; i++, x++) row[x * 4 + ch] = v;
                    }
                    else for (int i = 0; i < count && x < w; i++, x++) row[x * 4 + ch] = br.ReadByte();
                }
            }
            return true;
        }
    }
}
