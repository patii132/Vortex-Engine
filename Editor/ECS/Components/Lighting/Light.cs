using System.Runtime.Serialization;

namespace Editor.ECS.Components.Lighting
{
    /// <summary>
    /// Licht-Typen
    /// </summary>
    public enum LightType
    {
        Directional,
        Point,
        Spot,
        Area
    }

    /// <summary>
    /// Schatten-Typen
    /// </summary>
    public enum ShadowType
    {
        None,
        Hard,
        Soft
    }

    /// <summary>
    /// Licht-Komponente f�r Beleuchtung in der Szene.
    /// </summary>
    [DataContract(Name = "Light", Namespace = "")]
    public class Light : Component
    {
        private LightType _lightType = LightType.Directional;
        private ShadowType _shadowType = ShadowType.Soft;
        private float _intensity = 2.5f;
        private float _range = 10f;
        private float _spotAngle = 30f;
        private float _innerSpotAngle = 21f;
        private float _colorR = 1f;
        private float _colorG = 0.956f;
        private float _colorB = 0.839f;
        private float _shadowStrength = 1f;
        private float _shadowBias = 0.05f;
        private float _shadowNormalBias = 0.4f;
        private int _shadowResolution = 2048;
        private int _cullingMask = -1;
        private float _shadowDistance = DefaultShadowDistance;

        /// <summary>The sun's shadow reach before this setting existed (and the renderer's own default).</summary>
        public const float DefaultShadowDistance = 80f;
        public const float MinShadowDistance = 5f, MaxShadowDistance = 2000f;

        public override string DisplayName => $"{_lightType} Light";
        public override string IconCode => "\uE793";
        public override string IconColor => "#FFD700";

        /// <summary>
        /// Typ des Lichts
        /// </summary>
        [DataMember(Name = "lightType", Order = 10)]
        public LightType LightType
        {
            get => _lightType;
            set
            {
                if (SetProperty(ref _lightType, value, nameof(LightType)))
                    OnPropertyChanged(nameof(DisplayName));
            }
        }

        /// <summary>
        /// Schatten-Typ
        /// </summary>
        [DataMember(Name = "shadowType", Order = 11)]
        public ShadowType ShadowType
        {
            get => _shadowType;
            set => SetProperty(ref _shadowType, value, nameof(ShadowType));
        }

        /// <summary>
        /// Lichtintensit�t
        /// </summary>
        [DataMember(Name = "intensity", Order = 12)]
        public float Intensity
        {
            get => _intensity;
            set => SetProperty(ref _intensity, value, nameof(Intensity));
        }

        /// <summary>
        /// Reichweite (f�r Point und Spot)
        /// </summary>
        [DataMember(Name = "range", Order = 13)]
        public float Range
        {
            get => _range;
            set => SetProperty(ref _range, value, nameof(Range));
        }

        /// <summary>
        /// Spot-Winkel in Grad (f�r Spot)
        /// </summary>
        [DataMember(Name = "spotAngle", Order = 14)]
        public float SpotAngle
        {
            get => _spotAngle;
            set => SetProperty(ref _spotAngle, value, nameof(SpotAngle));
        }

        /// <summary>
        /// Innerer Spot-Winkel in Grad (f�r Spot)
        /// </summary>
        [DataMember(Name = "innerSpotAngle", Order = 15)]
        public float InnerSpotAngle
        {
            get => _innerSpotAngle;
            set => SetProperty(ref _innerSpotAngle, value, nameof(InnerSpotAngle));
        }

        /// <summary>
        /// Lichtfarbe Rot
        /// </summary>
        [DataMember(Name = "colorR", Order = 16)]
        public float ColorR
        {
            get => _colorR;
            set => SetProperty(ref _colorR, value, nameof(ColorR));
        }

        /// <summary>
        /// Lichtfarbe Gr�n
        /// </summary>
        [DataMember(Name = "colorG", Order = 17)]
        public float ColorG
        {
            get => _colorG;
            set => SetProperty(ref _colorG, value, nameof(ColorG));
        }

        /// <summary>
        /// Lichtfarbe Blau
        /// </summary>
        [DataMember(Name = "colorB", Order = 18)]
        public float ColorB
        {
            get => _colorB;
            set => SetProperty(ref _colorB, value, nameof(ColorB));
        }

        /// <summary>
        /// Schattenst�rke (0-1)
        /// </summary>
        [DataMember(Name = "shadowStrength", Order = 19)]
        public float ShadowStrength
        {
            get => _shadowStrength;
            set => SetProperty(ref _shadowStrength, value, nameof(ShadowStrength));
        }

        /// <summary>
        /// Schatten-Bias
        /// </summary>
        [DataMember(Name = "shadowBias", Order = 20)]
        public float ShadowBias
        {
            get => _shadowBias;
            set => SetProperty(ref _shadowBias, value, nameof(ShadowBias));
        }

        /// <summary>
        /// Schatten-Normal-Bias
        /// </summary>
        [DataMember(Name = "shadowNormalBias", Order = 21)]
        public float ShadowNormalBias
        {
            get => _shadowNormalBias;
            set => SetProperty(ref _shadowNormalBias, value, nameof(ShadowNormalBias));
        }

        /// <summary>
        /// Schatten-Aufl�sung
        /// </summary>
        [DataMember(Name = "shadowResolution", Order = 22)]
        public int ShadowResolution
        {
            get => _shadowResolution;
            set => SetProperty(ref _shadowResolution, value, nameof(ShadowResolution));
        }

        /// <summary>
        /// Culling-Maske
        /// </summary>
        [DataMember(Name = "cullingMask", Order = 23)]
        public int CullingMask
        {
            get => _cullingMask;
            set => SetProperty(ref _cullingMask, value, nameof(CullingMask));
        }

        /// <summary>
        /// Directional only: how far from the camera the sun's cascaded shadows reach (world units). The three
        /// cascades share the same shadow-map resolution, so a longer reach makes near shadows softer -
        /// open worlds (driving, flying) want 300-1000, interiors and horror scenes the default 80.
        /// </summary>
        [DataMember(Name = "shadowDistance", Order = 24)]
        public float ShadowDistance
        {
            get => _shadowDistance;
            set => SetProperty(ref _shadowDistance, ClampShadowDistance(value), nameof(ShadowDistance));
        }

        public static float ClampShadowDistance(float v) =>
            float.IsNaN(v) ? DefaultShadowDistance : (v < MinShadowDistance ? MinShadowDistance : (v > MaxShadowDistance ? MaxShadowDistance : v));

        // The serializer skips constructors and field initializers: scenes saved before "shadowDistance" existed
        // would load 0 (the renderer clamps that to 5 m) instead of the 80 m they always had.
        [OnDeserializing]
        private void OnDeserializingMethod(StreamingContext context)
        {
            _shadowDistance = DefaultShadowDistance;
        }

        // NOTE: the light's enabled flag is the base Component.IsEnabled ("isEnabled", Order 1). Light used to
        // re-declare it as a second "isEnabled" data member, which the JSON serializer rejects as a duplicate in
        // the type hierarchy - prefabs (.ventity) with a Light could neither be saved nor loaded.

        public Light() : base() { }
        public Light(GameEntity entity) : base(entity) { }
        public Light(GameEntity entity, LightType type) : base(entity)
        {
            LightType = type;
            
            // Standard-Einstellungen je nach Typ
            switch (type)
            {
                case LightType.Point:
                    Range = 10f;
                    break;
                case LightType.Spot:
                    Range = 10f;
                    SpotAngle = 30f;
                    break;
            }
        }
    }
}
