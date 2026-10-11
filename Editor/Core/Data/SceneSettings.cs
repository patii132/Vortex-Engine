using System.Runtime.Serialization;

namespace Editor.Core.Data
{
    /// <summary>
    /// Per-scene environment settings (fog #27 + post-FX #28/#29), serialized into the .vscene and
    /// applied whenever the scene becomes the live one (open in editor, play, shipped game). Scripts
    /// (Vortex.Atmosphere / Vortex.PostFx) can still override at runtime — leaving play mode re-applies
    /// these authored values. Old .vscene files simply have no settings block and get the defaults
    /// (everything off = the untouched render path).
    /// </summary>
    [DataContract(Name = "SceneSettings", Namespace = "")]
    public class SceneSettings
    {
        // ---- Fog (#27) ----
        [DataMember(Name = "fogOn", Order = 0)] public bool FogEnabled { get; set; }
        [DataMember(Name = "fogDensity", Order = 1)] public float FogDensity { get; set; } = 0.08f;
        [DataMember(Name = "fogHeightY", Order = 2)] public float FogHeightY { get; set; }
        [DataMember(Name = "fogHeightFalloff", Order = 3)] public float FogHeightFalloff { get; set; }
        [DataMember(Name = "fogR", Order = 4)] public float FogR { get; set; } = 0.02f;
        [DataMember(Name = "fogG", Order = 5)] public float FogG { get; set; } = 0.025f;
        [DataMember(Name = "fogB", Order = 6)] public float FogB { get; set; } = 0.035f;

        // ---- Vignette (#29) ----
        [DataMember(Name = "vigOn", Order = 10)] public bool VignetteEnabled { get; set; }
        [DataMember(Name = "vigIntensity", Order = 11)] public float VignetteIntensity { get; set; } = 0.8f;
        [DataMember(Name = "vigSmoothness", Order = 12)] public float VignetteSmoothness { get; set; } = 0.5f;
        [DataMember(Name = "vigRoundness", Order = 13)] public float VignetteRoundness { get; set; } = 1.0f;
        [DataMember(Name = "vigR", Order = 14)] public float VignetteR { get; set; }
        [DataMember(Name = "vigG", Order = 15)] public float VignetteG { get; set; }
        [DataMember(Name = "vigB", Order = 16)] public float VignetteB { get; set; }

        // ---- Film grain (#29) ----
        [DataMember(Name = "grainOn", Order = 20)] public bool GrainEnabled { get; set; }
        [DataMember(Name = "grainIntensity", Order = 21)] public float GrainIntensity { get; set; } = 0.35f;
        [DataMember(Name = "grainSize", Order = 22)] public float GrainSize { get; set; } = 1.6f;

        // ---- Chromatic aberration (#29) ----
        [DataMember(Name = "caOn", Order = 30)] public bool CaEnabled { get; set; }
        [DataMember(Name = "caStrength", Order = 31)] public float CaStrength { get; set; } = 0.35f;
        [DataMember(Name = "caFalloff", Order = 32)] public float CaFalloff { get; set; } = 1.2f;

        // ---- Color grading (#31) ----
        [DataMember(Name = "gradeOn", Order = 40)] public bool GradeEnabled { get; set; }
        [DataMember(Name = "exposure", Order = 41)] public float Exposure { get; set; }
        [DataMember(Name = "contrast", Order = 42)] public float Contrast { get; set; } = 1.0f;
        [DataMember(Name = "saturation", Order = 43)] public float Saturation { get; set; } = 1.0f;
        [DataMember(Name = "temperature", Order = 44)] public float Temperature { get; set; }
        [DataMember(Name = "tint", Order = 45)] public float Tint { get; set; }

        // ---- Bloom (#30) ----
        [DataMember(Name = "bloomOn", Order = 50)] public bool BloomEnabled { get; set; }
        [DataMember(Name = "bloomThreshold", Order = 51)] public float BloomThreshold { get; set; } = 0.75f;
        [DataMember(Name = "bloomKnee", Order = 52)] public float BloomKnee { get; set; } = 0.5f;
        [DataMember(Name = "bloomIntensity", Order = 53)] public float BloomIntensity { get; set; } = 0.7f;
        [DataMember(Name = "bloomScatter", Order = 54)] public float BloomScatter { get; set; } = 0.65f;

        // ---- SSAO (#32) ----
        [DataMember(Name = "aoOn", Order = 60)] public bool AoEnabled { get; set; }
        [DataMember(Name = "aoRadius", Order = 61)] public float AoRadius { get; set; } = 0.6f;
        [DataMember(Name = "aoIntensity", Order = 62)] public float AoIntensity { get; set; } = 1.0f;
        // ---- Volumetric fog (#119): ray-marched in-scattering of the sun and the lights, over the fog's height profile ----
        [DataMember(Name = "volOn", Order = 70)] public bool VolumetricEnabled { get; set; }
        [DataMember(Name = "volDensity", Order = 71)] public float VolumetricDensity { get; set; } = 0.03f;
        [DataMember(Name = "volAnisotropy", Order = 72)] public float VolumetricAnisotropy { get; set; } = 0.55f;
        [DataMember(Name = "volDistance", Order = 73)] public float VolumetricDistance { get; set; } = 60f;
        [DataMember(Name = "volNoise", Order = 74)] public float VolumetricNoise { get; set; } = 0.5f;
        [DataMember(Name = "volNoiseScale", Order = 75)] public float VolumetricNoiseScale { get; set; } = 6f;
        [DataMember(Name = "volNoiseSpeed", Order = 76)] public float VolumetricNoiseSpeed { get; set; } = 0.35f;
        [DataMember(Name = "volSun", Order = 77)] public float VolumetricSun { get; set; } = 1f;
        [DataMember(Name = "volLights", Order = 78)] public float VolumetricLights { get; set; } = 1f;
        [DataMember(Name = "volAmbient", Order = 79)] public float VolumetricAmbient { get; set; } = 0.35f;
        [DataMember(Name = "volSteps", Order = 80)] public int VolumetricSteps { get; set; } = 24;
        [DataMember(Name = "volShadows", Order = 81)] public bool VolumetricShadows { get; set; } = true;
        // ---- Exposure + aerial perspective ----
        /// <summary>Scene exposure in stops BEFORE the tonemapper (the camera's aperture): 0 = the classic look, +1 doubles the
        /// light the ACES curve receives. Unlike the grading exposure it brightens without clipping — a sunlit desert wants +1.5…+2.5.</summary>
        [DataMember(Name = "sceneExposure", Order = 82)] public float SceneExposure { get; set; }
        /// <summary>The sun's forward-scatter glow in the fog when looking towards it (0 = the flat fog colour; 0.3–0.6 a desert
        /// noon, 1–2 a low sun).</summary>
        [DataMember(Name = "fogSunGlow", Order = 83)] public float FogSunGlow { get; set; }
        /// <summary>0 = the ACES fit (the classic look; bright saturated colours drift towards white), 1 = Khronos PBR Neutral
        /// (hue-preserving — a sunlit desert stays orange, a red car red).</summary>
        [DataMember(Name = "tonemapper", Order = 84)] public int Tonemapper { get; set; }
        /// <summary>FXAA on the final picture (nine taps, no history): smooth edges on fronds, grass and ripples. Off = the picture as rendered.</summary>
        [DataMember(Name = "aaOn", Order = 85)] public bool AntiAliasEnabled { get; set; }
        // ---- Ground bounce: the sun's light bounced off the ground into the shadows (a warm lower hemisphere in the sky light) ----
        [DataMember(Name = "bounceStrength", Order = 86)] public float BounceStrength { get; set; }
        [DataMember(Name = "bounceR", Order = 87)] public float BounceR { get; set; } = 0.55f;
        [DataMember(Name = "bounceG", Order = 88)] public float BounceG { get; set; } = 0.40f;
        [DataMember(Name = "bounceB", Order = 89)] public float BounceB { get; set; } = 0.26f;

        /// <summary>DataContractSerializer builds an UNINITIALIZED object (field initializers never
        /// run), so members missing from an older .vscene would deserialize as 0 — a scene saved
        /// before bloom existed must still open with the neutral bloom defaults, not zeros. Restore
        /// every non-zero default here; authored values then overwrite whichever members the file has.</summary>
        [OnDeserializing]
        private void RestoreDefaults(StreamingContext context)
        {
            FogDensity = 0.08f; FogR = 0.02f; FogG = 0.025f; FogB = 0.035f;
            BounceR = 0.55f; BounceG = 0.40f; BounceB = 0.26f;
            VignetteIntensity = 0.8f; VignetteSmoothness = 0.5f; VignetteRoundness = 1.0f;
            GrainIntensity = 0.35f; GrainSize = 1.6f;
            CaStrength = 0.35f; CaFalloff = 1.2f;
            Contrast = 1.0f; Saturation = 1.0f;
            BloomThreshold = 0.75f; BloomKnee = 0.5f; BloomIntensity = 0.7f; BloomScatter = 0.65f;
            AoRadius = 0.6f; AoIntensity = 1.0f;
            VolumetricDensity = 0.03f; VolumetricAnisotropy = 0.55f; VolumetricDistance = 60f; VolumetricNoise = 0.5f; VolumetricNoiseScale = 6f;
            VolumetricNoiseSpeed = 0.35f; VolumetricSun = 1f; VolumetricLights = 1f; VolumetricAmbient = 0.35f; VolumetricSteps = 24; VolumetricShadows = true;
        }

        /// <summary>Push every setting into the renderer (persistent frame state, same-frame visible).</summary>
        public void Apply()
        {
            DllWrapper.VortexAPI.SetFog(FogR, FogG, FogB, FogEnabled ? FogDensity : 0f,
                FogHeightY, FogHeightFalloff);
            DllWrapper.VortexAPI.SetSceneExposureStops(SceneExposure);
            DllWrapper.VortexAPI.SetTonemapperMode(Tonemapper);
            DllWrapper.VortexAPI.SetPostAntiAlias(AntiAliasEnabled);
            DllWrapper.VortexAPI.SetFogSunGlowStrength(FogEnabled ? FogSunGlow : 0f);
            DllWrapper.VortexAPI.SetPostVignette(VignetteEnabled, VignetteIntensity, VignetteSmoothness,
                VignetteRoundness, VignetteR, VignetteG, VignetteB);
            DllWrapper.VortexAPI.SetPostGrain(GrainEnabled, GrainIntensity, GrainSize);
            DllWrapper.VortexAPI.SetPostChromaticAberration(CaEnabled, CaStrength, CaFalloff);
            DllWrapper.VortexAPI.SetPostColorGrade(GradeEnabled, Exposure, Contrast, Saturation, Temperature, Tint);
            DllWrapper.VortexAPI.SetPostBloom(BloomEnabled, BloomThreshold, BloomKnee, BloomIntensity, BloomScatter);
            DllWrapper.VortexAPI.SetAmbientOcclusion(AoEnabled, AoRadius, AoIntensity);
            DllWrapper.VortexAPI.SetVolumetricFog(VolumetricEnabled, VolumetricDensity, VolumetricAnisotropy, VolumetricDistance, VolumetricNoise,
                VolumetricNoiseScale, VolumetricNoiseSpeed, VolumetricSun, VolumetricLights, VolumetricAmbient, VolumetricSteps, VolumetricShadows);
        }

        /// <summary>Renderer back to a clean image (used when a scene without settings becomes live).</summary>
        public static void ApplyDefaults() => new SceneSettings().Apply();
    }
}
