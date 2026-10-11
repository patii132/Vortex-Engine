using System.Runtime.Serialization;

namespace Editor.ECS.Components.Rendering
{
    /// <summary>
    /// A heightfield terrain (#124): a square of <see cref="Size"/> metres starting at the entity's position and extending
    /// along its local +X / +Z, with <see cref="Resolution"/> height samples per side. The heights and the splat map (the
    /// weights of up to four texture layers) live in a <c>.vterrain</c> asset next to the scene's other assets
    /// (<see cref="DataPath"/>); the editor's Terrain tools sculpt and paint it in the viewport, scripts read it through
    /// <c>Terrain.Height</c> and deform it through <c>Terrain.Deform</c>. Renders as LOD chunks with the terrain
    /// material shader, collides as a Jolt height field + the managed collision world, feeds the navmesh bake.
    /// </summary>
    [DataContract(Name = "Terrain", Namespace = "")]
    public class Terrain : Component
    {
        public const int DefaultResolution = 129;

        private string _dataPath;
        private float _size = 128f;
        private int _resolution = DefaultResolution;
        private float _lodDistance = 48f;
        private bool _collision = true;
        private bool _navigation = true;
        private string _layer0Material, _layer1Material, _layer2Material, _layer3Material;
        private float _layer0Tile = 4f, _layer1Tile = 4f, _layer2Tile = 4f, _layer3Tile = 4f;

        public Terrain() : base() { }
        public Terrain(GameEntity entity) : base(entity) { }

        public override string DisplayName => "Terrain";
        public override string IconCode => "";
        public override string IconColor => "#7CB342";

        /// <summary>The <c>.vterrain</c> file with the heights and the splat map (project-relative). Empty until the terrain
        /// is first saved — the service then puts it under <c>Assets/Terrain/</c>, named after the entity.</summary>
        [DataMember(Name = "dataPath", Order = 10)]
        public string DataPath
        {
            get => _dataPath;
            set => SetProperty(ref _dataPath, value, nameof(DataPath));
        }

        /// <summary>Edge length in metres (the terrain is square; the entity's scale is ignored).</summary>
        [DataMember(Name = "size", Order = 11)]
        public float Size
        {
            get => _size;
            set => SetProperty(ref _size, value < 1f ? 1f : (value > 16384f ? 16384f : value), nameof(Size));
        }

        /// <summary>Height samples per side: 33, 65, 129, 257, 513, 1025 or 2049 (32 · 2ⁿ + 1, so the chunks divide evenly).
        /// Changing it resamples the existing heights.</summary>
        [DataMember(Name = "resolution", Order = 12)]
        public int Resolution
        {
            get => _resolution;
            set => SetProperty(ref _resolution, SnapResolution(value), nameof(Resolution));
        }

        /// <summary>Camera distance (m) at which a chunk drops to the second LOD; the third starts at twice that.</summary>
        [DataMember(Name = "lodDistance", Order = 13)]
        public float LodDistance
        {
            get => _lodDistance;
            set => SetProperty(ref _lodDistance, value < 4f ? 4f : value, nameof(LodDistance));
        }

        /// <summary>Collide (Jolt height field + the character's collision world) and count as navmesh ground.</summary>
        [DataMember(Name = "collision", Order = 14)]
        public bool Collision
        {
            get => _collision;
            set => SetProperty(ref _collision, value, nameof(Collision));
        }

        /// <summary>Count as navmesh ground (with Collision). Off for a far-distance terrain that only dresses the horizon — a
        /// 4 km backdrop would blow the navmesh's tile budget.</summary>
        [DataMember(Name = "navigation", Order = 15)]
        public bool Navigation
        {
            get => _navigation;
            set => SetProperty(ref _navigation, value, nameof(Navigation));
        }

        // ---- layers: a .vmat per layer (albedo / normal / roughness maps + base colour) and its tiling in metres ----

        [DataMember(Name = "layer0Material", Order = 20)]
        public string Layer0Material { get => _layer0Material; set => SetProperty(ref _layer0Material, value, nameof(Layer0Material)); }
        [DataMember(Name = "layer1Material", Order = 21)]
        public string Layer1Material { get => _layer1Material; set => SetProperty(ref _layer1Material, value, nameof(Layer1Material)); }
        [DataMember(Name = "layer2Material", Order = 22)]
        public string Layer2Material { get => _layer2Material; set => SetProperty(ref _layer2Material, value, nameof(Layer2Material)); }
        [DataMember(Name = "layer3Material", Order = 23)]
        public string Layer3Material { get => _layer3Material; set => SetProperty(ref _layer3Material, value, nameof(Layer3Material)); }

        /// <summary>Metres per texture repeat of layer 0 (world-space tiling, so neighbouring terrains match).</summary>
        [DataMember(Name = "layer0Tile", Order = 24)]
        public float Layer0Tile { get => _layer0Tile; set => SetProperty(ref _layer0Tile, ClampTile(value), nameof(Layer0Tile)); }
        [DataMember(Name = "layer1Tile", Order = 25)]
        public float Layer1Tile { get => _layer1Tile; set => SetProperty(ref _layer1Tile, ClampTile(value), nameof(Layer1Tile)); }
        [DataMember(Name = "layer2Tile", Order = 26)]
        public float Layer2Tile { get => _layer2Tile; set => SetProperty(ref _layer2Tile, ClampTile(value), nameof(Layer2Tile)); }
        [DataMember(Name = "layer3Tile", Order = 27)]
        public float Layer3Tile { get => _layer3Tile; set => SetProperty(ref _layer3Tile, ClampTile(value), nameof(Layer3Tile)); }

        public string LayerMaterial(int i) => i == 0 ? _layer0Material : i == 1 ? _layer1Material : i == 2 ? _layer2Material : _layer3Material;
        public float LayerTile(int i) => i == 0 ? _layer0Tile : i == 1 ? _layer1Tile : i == 2 ? _layer2Tile : _layer3Tile;
        public void SetLayerMaterial(int i, string path)
        {
            switch (i) { case 0: Layer0Material = path; break; case 1: Layer1Material = path; break; case 2: Layer2Material = path; break; default: Layer3Material = path; break; }
        }
        public void SetLayerTile(int i, float tile)
        {
            switch (i) { case 0: Layer0Tile = tile; break; case 1: Layer1Tile = tile; break; case 2: Layer2Tile = tile; break; default: Layer3Tile = tile; break; }
        }

        /// <summary>Metres between two height samples.</summary>
        public float CellSize => _size / (_resolution - 1);

        private static float ClampTile(float v) => v < 0.1f ? 0.1f : (v > 1000f ? 1000f : v);

        /// <summary>Nearest legal resolution (32 · 2ⁿ + 1 between 33 and 2049).</summary>
        public static int SnapResolution(int wanted)
        {
            int best = DefaultResolution, bestDiff = int.MaxValue;
            for (int cells = 32; cells <= 2048; cells *= 2)
            {
                int res = cells + 1;
                int diff = System.Math.Abs(res - wanted);
                if (diff < bestDiff) { bestDiff = diff; best = res; }
            }
            return best;
        }
    }
}
