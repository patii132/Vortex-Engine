using System;
using System.Collections.Generic;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Rendering;

namespace Editor.Core.Services
{
    /// <summary>
    /// Manages rendering of scene entities in the viewport.
    /// Acts as bridge between Editor entities and Engine rendering.
    /// </summary>
    public class SceneRenderService : IDisposable
    {
        private static SceneRenderService _instance;
        public static SceneRenderService Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new SceneRenderService();
                return _instance;
            }
        }

        /// <summary>
        /// Set when a script/runtime change invalidated the baked render queue (Transform.SyncToEngine
        /// during play). The submit-once GameHost loop re-submits the scene and clears it — without this,
        /// script-moved entities render frozen in shipped games (editor play re-submits every frame anyway).
        /// </summary>
        public static bool RuntimeDirty;
        /// <summary>The STATIC scene changed in a way a transform version cannot see (#364 A): an entity was created,
        /// destroyed, (de)activated, re-parented, its renderer enabled/disabled, its layer or colour changed. The next
        /// play-mode submit re-sends the retained static set. Set it together with <see cref="RuntimeDirty"/> at
        /// structural sites; per-frame movement of dynamic entities must NOT set it.</summary>
        public static bool StaticDirty = true;

        /// <summary>How the EDIT-mode viewport presents non-world render layers (#175: 1 = FP viewmodel,
        /// 2 = third-person only). Play mode ignores this — while playing the layers always behave like
        /// the shipped game (1 = FP overlay pass, 2 = skipped for the local player).</summary>
        public enum ViewmodelPreviewMode
        {
            /// <summary>Default build view: FP meshes are NOT rendered (they belong to the game's FP pass),
            /// third-person-only meshes render as normal world geometry.</summary>
            Hidden,
            /// <summary>Placement mode (viewport toolbar toggle): FP meshes render as plain, depth-tested
            /// world geometry so they can be positioned; no FP overlay pass runs.</summary>
            AsWorld,
            /// <summary>"FP Preview (In-Game)" view mode: submit exactly like play mode — layer 1 goes to
            /// the native FP overlay pass (own FOV, cleared depth), layer 2 is skipped. The viewport shows
            /// the frame the game would render.</summary>
            GameView,
        }

        /// <summary>Edit-mode presentation of the FP/3P layers; set by the viewport (View dropdown +
        /// toolbar toggle). Static so the GameHost path (which never touches it) keeps the default.</summary>
        public static ViewmodelPreviewMode EditorViewmodelPreview = ViewmodelPreviewMode.Hidden;

        /// <summary>Set while the in-game DEBUG FREECAM is active (editor play / GameHost debug builds):
        /// the render view has detached from the player and is looking AT them, so render like another
        /// camera — third-person body (layer 2) visible, first-person viewmodel (layer 1) hidden.
        /// Cleared the instant the freecam exits. Never set in a shipped Release.</summary>
        public static bool DebugThirdPersonView;

        /// <summary>"We are the game, not the editor build view": editor play (viewport/game window)
        /// sets IsPlaying via PlayModeService.Play(); the standalone player additionally always sets
        /// NativeGameHostRunning before RunGameHost — belt and braces so a shipped game can NEVER lose
        /// its viewmodel to the edit-mode layer presentation.</summary>
        private static bool IsPlayLike =>
            PlayModeService.Instance.IsPlaying || PlayModeService.Instance.NativeGameHostRunning;

        /// <summary>Whether an entity's SUBTREE is part of the edit-viewport render (eye toggle +
        /// activeSelf cascade). The pick path (RaycastService) prunes recursion with this so an
        /// invisible entity never swallows clicks or gizmo drags — pick == render.</summary>
        public static bool IsEditorSubtreeVisible(GameEntity entity)
        {
            if (entity == null || !entity.IsActive) return false;
            if (entity.IsHiddenInEditor && !IsPlayLike) return false;
            return true;
        }

        /// <summary>Whether an entity's OWN mesh is drawn — and therefore pickable — under the current
        /// layer presentation. Mirrors SubmitEntity's layer branch exactly: layer-1 meshes are hidden in
        /// the default build view, and in FP Preview / play they draw with the FP overlay projection
        /// (clicks would misalign), so they are only pickable in the AsWorld placement mode. Non-mesh
        /// entities stay pickable (icons/gizmos represent them).</summary>
        public static bool IsEditorMeshPickable(GameEntity entity)
        {
            var mr = entity?.GetComponent<MeshRenderer>();
            if (mr == null) return true;
            int layer = mr.RenderLayer;
            if (layer <= 0 || layer > 2) return true;
            if (IsPlayLike || EditorViewmodelPreview == ViewmodelPreviewMode.GameView)
                return false;                                        // 2 = not drawn; 1 = FP projection, misaligned
            if (EditorViewmodelPreview == ViewmodelPreviewMode.Hidden)
                return layer != 1;                                   // FP meshes aren't drawn -> not pickable
            return true;                                             // AsWorld: drawn as world geometry
        }

        /// <summary>
        /// Asset-pipeline diagnostics, opt-in via VORTEX_VERBOSE_LOG=1. With a VS debugger attached EVERY
        /// Debug.WriteLine is a ~0.5-2ms cross-process round-trip — this service used to log per asset
        /// during scene load (250+ writes for a real level), which alone added seconds of F5-only stall
        /// while the same build loaded instantly standalone.
        /// </summary>
        private static readonly bool _verboseLog =
            Environment.GetEnvironmentVariable("VORTEX_VERBOSE_LOG") == "1";
        private static void Log(string msg)
        {
            if (_verboseLog) System.Diagnostics.Debug.WriteLine(msg);
        }

        private readonly Dictionary<Guid, long> _entityMeshes = new Dictionary<Guid, long>();
        private static int _meshDbg; // diagnostic: log first few mesh creations
        private static int _submitN, _ssDbg; // diagnostic: count submits per SubmitScene
        private static DateTime _slowSubmitLogAt;   // rate limit for the slow-submit console line
        /// <summary>How long the last SubmitScene took and how many meshes it sent — the player's FPS log shows them.</summary>
        public static double LastSubmitMs;
        public static int LastSubmitMeshes;
        private readonly Dictionary<Guid, long> _entityMaterials = new Dictionary<Guid, long>();

        // ---- shared plain materials (#364 D) --------------------------------------------------------------------
        // A primitive / plain-colour entity used to get its OWN native material, so 8k identical cubes were 8k material
        // ids — and the renderer only merges consecutive items with the same mesh AND material into one instanced run,
        // so they never instanced. Now one native material per (colour, metallic, roughness) is shared by every entity
        // with that look. Copy-on-write: a shared material is never mutated in place — a recoloured entity switches to
        // the shared material of its new colour, so recolouring one cube cannot recolour the others.
        internal struct MaterialKey : IEquatable<MaterialKey>
        {
            public float R, G, B, A, Metallic, Roughness;
            public long Texture;
            public bool Equals(MaterialKey o) => R == o.R && G == o.G && B == o.B && A == o.A && Metallic == o.Metallic && Roughness == o.Roughness && Texture == o.Texture;
            public override bool Equals(object obj) => obj is MaterialKey k && Equals(k);
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = R.GetHashCode(); h = h * 31 + G.GetHashCode(); h = h * 31 + B.GetHashCode(); h = h * 31 + A.GetHashCode();
                    h = h * 31 + Metallic.GetHashCode(); h = h * 31 + Roughness.GetHashCode(); h = h * 31 + Texture.GetHashCode();
                    return h;
                }
            }
        }
        private static readonly Dictionary<MaterialKey, long> _sharedPlainMaterials = new Dictionary<MaterialKey, long>();
        // The plain materials' ids: their colour is NOT in the material (white) but in the per-instance tint (#331), so
        // every plain primitive of one metallic/roughness shares one material whatever its colour.
        private static readonly HashSet<long> _plainMaterialIds = new HashSet<long>();
        // Every SHARED material id (plain materials above + the per-mesh-path import materials): never DeleteMaterial'd
        // on behalf of one entity. (ClearAllRenderables used to delete a model's shared import material once per
        // entity that used it — a double free of the same kind the shared meshes had.)
        private static readonly HashSet<long> _sharedMaterialIds = new HashSet<long>();
        private readonly Dictionary<Guid, MaterialKey> _entityMaterialKey = new Dictionary<Guid, MaterialKey>();

        /// <summary>The shared native material for a plain look, created on first use.</summary>
        private static long GetOrCreatePlainMaterial(MaterialKey key)
        {
            long id;
            if (_sharedPlainMaterials.TryGetValue(key, out id) && id >= 0) return id;
            id = VortexAPI.CreateNewMaterial();
            if (id < 0) return id;
            VortexAPI.SetMaterialBaseColor(id, key.R, key.G, key.B, key.A);
            // Push PBR scalars too — otherwise the engine keeps its material defaults and a freshly created primitive
            // renders far too dark (metallic surface, one weak light).
            VortexAPI.SetMaterialMetallicValue(id, key.Metallic);
            VortexAPI.SetMaterialRoughnessValue(id, key.Roughness);
            _sharedPlainMaterials[key] = id;
            _sharedMaterialIds.Add(id);
            _plainMaterialIds.Add(id);
            return id;
        }

        /// <summary>The instance tint (#331). A plain material is white, so the MeshRenderer colour IS the instance's colour.
        /// A textured, imported or .vmat material has its own base colour; the MeshRenderer colour tints it on top, where
        /// the component's default grey (0.7) means "untinted" — otherwise every placed model would darken by 30 %.</summary>
        private static void TintFor(MeshRenderer mr, long materialId, out float r, out float g, out float b, out float a)
        {
            r = mr.ColorR; g = mr.ColorG; b = mr.ColorB; a = mr.ColorA;
            if (_plainMaterialIds.Contains(materialId)) return;
            if (Math.Abs(r - 0.7f) < 0.002f && Math.Abs(g - 0.7f) < 0.002f && Math.Abs(b - 0.7f) < 0.002f) { r = 1f; g = 1f; b = 1f; }
        }
        
        // Track mesh paths to detect changes
        private readonly Dictionary<Guid, string> _entityMeshPaths = new Dictionary<Guid, string>();

        // ---- scene-submit performance (8–10k entities; see the render-performance tracking issue) -------------------
        // The player re-submits the WHOLE scene every frame something moves, so every per-entity cost here is paid
        // thousands of times per frame. These keep that loop allocation-free and cheap.
        // One GPU mesh per primitive kind ("Primitive:Cube") for the whole scene — never one per entity.
        private static readonly Dictionary<string, long> _primitiveMeshCache = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        // Every SHARED mesh id (model submeshes + primitives): never DeleteMesh'd on behalf of one entity.
        private static readonly HashSet<long> _sharedMeshIds = new HashSet<long>();
        // A model path's "path#submeshN" keys + mesh ids, resolved once — the per-frame loop no longer builds up to
        // 64 strings and misses up to 64 dictionary lookups per entity. Cleared whenever the submesh cache changes.
        private sealed class SubmeshSet { public string[] Keys; public long[] MeshIds; }
        private static readonly Dictionary<string, SubmeshSet> _resolvedSubmeshes = new Dictionary<string, SubmeshSet>();
        private static readonly Dictionary<string, bool> _isModelPath = new Dictionary<string, bool>();
        // Zero-allocation world matrices: one result buffer per entity submit (the renderer memcpy's it during the
        // call) and scratch buffers per hierarchy depth — instead of a new float[16] per entity per frame
        // (≈40 MB/s of garbage at 8k entities, i.e. GC stalls).
        private readonly float[] _worldTmp = new float[16];
        private float[][] _localPool = new float[8][], _worldPool = new float[8][];
        // Rigid submits of one SubmitScene pass grouped by (mesh, material, layer): one P/Invoke per group instead of
        // one per entity. The renderer sorts its queue and merges same-mesh items into instanced draw runs anyway.
        private readonly InstanceBatcher _batcher = new InstanceBatcher();
        private bool _batching;

        /// <summary>Groups rigid mesh submits by (mesh, material, layer) and sends each group in ONE call with all its
        /// world matrices. Allocation-free after warm-up: batches and matrix buffers are pooled across frames.</summary>
        internal sealed class InstanceBatcher
        {
            private struct Key : IEquatable<Key>
            {
                public long Mesh, Material; public int Layer;
                public bool Equals(Key o) => Mesh == o.Mesh && Material == o.Material && Layer == o.Layer;
                public override bool Equals(object obj) => obj is Key k && Equals(k);
                public override int GetHashCode() { unchecked { return (int)Mesh * 397 ^ (int)(Mesh >> 32) ^ (int)Material * 31 ^ (int)(Material >> 32) ^ Layer; } }
            }
            private sealed class Batch { public Key Key; public float[] Data = new float[16 * 16]; public float[] Tint = new float[16 * 4]; public int Count; }

            private readonly Dictionary<Key, Batch> _open = new Dictionary<Key, Batch>();
            private readonly List<Batch> _order = new List<Batch>();   // first-seen order (deterministic)
            private readonly Stack<Batch> _pool = new Stack<Batch>();

            /// <summary>Groups in the current pass.</summary>
            public int BatchCount => _order.Count;
            /// <summary>Instances added in the current pass.</summary>
            public int InstanceCount { get; private set; }

            /// <summary>Queue one instance; <paramref name="world"/> (row-major 4x4, 16 floats) and its tint are copied now.</summary>
            public void Add(long mesh, long material, int layer, float[] world, float r, float g, float bl, float a)
            {
                var key = new Key { Mesh = mesh, Material = material, Layer = layer };
                Batch b;
                if (!_open.TryGetValue(key, out b))
                {
                    b = _pool.Count > 0 ? _pool.Pop() : new Batch();
                    b.Key = key; b.Count = 0;
                    _open[key] = b;
                    _order.Add(b);
                }
                int need = (b.Count + 1) * 16;
                if (need > b.Data.Length) Array.Resize(ref b.Data, Math.Max(need, b.Data.Length * 2));
                if ((b.Count + 1) * 4 > b.Tint.Length) Array.Resize(ref b.Tint, Math.Max((b.Count + 1) * 4, b.Tint.Length * 2));
                Array.Copy(world, 0, b.Data, b.Count * 16, 16);
                int t = b.Count * 4;
                b.Tint[t] = r; b.Tint[t + 1] = g; b.Tint[t + 2] = bl; b.Tint[t + 3] = a;
                b.Count++;
                InstanceCount++;
            }

            /// <summary>Send every group as (mesh, material, layer, matrices, tints, count), then reset for the next pass.</summary>
            public void Flush(Action<long, long, int, float[], float[], int> submit)
            {
                for (int i = 0; i < _order.Count; i++)
                {
                    var b = _order[i];
                    if (b.Count > 0) submit(b.Key.Mesh, b.Key.Material, b.Key.Layer, b.Data, b.Tint, b.Count);
                    b.Count = 0;
                    _pool.Push(b);
                }
                _order.Clear();
                _open.Clear();
                InstanceCount = 0;
            }
        }
        
        // Material color cache for dirty checking
        private readonly Dictionary<Guid, (float r, float g, float b, float a)> _entityMaterialColors = 
            new Dictionary<Guid, (float r, float g, float b, float a)>();

        // STATIC cache: Map mesh paths to their imported material IDs
        // This survives entity serialization/deserialization
        private static readonly Dictionary<string, long> _meshPathToMaterialId = new Dictionary<string, long>();

        // FAST cache: resolved .vmat MaterialPath -> engine material id. The submit-once render loop calls
        // GetOrCreateMaterial for EVERY entity EVERY frame whenever any dynamic entity (e.g. the first-person
        // viewmodel) is moving. Without this, each call did a per-entity filesystem AssetVfs.Exists + Path.Combine
        // — with ~360 objects that was ~60us x 360 = the CPU bottleneck capping the scene at ~30 FPS. The material
        // id is stable at runtime (live edits mutate the native material in place, keeping the same id), so caching
        // by path is safe. Cleared on scene (pre)load.
        private static readonly Dictionary<string, long> _vmatPathCache = new Dictionary<string, long>();


        /// <summary>Built-in primitives ("Primitive:Cube", ...) share one mesh path across every entity that uses them, so
        /// they must never get a path-wide material — each entity keeps its own.</summary>
        public static bool IsPrimitivePath(string meshPath)
            => !string.IsNullOrEmpty(meshPath) && meshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Register a material for a mesh path (called during import)
        /// </summary>
        public static void RegisterMaterialForMeshPath(string meshPath, long materialId)
        {
            if (!string.IsNullOrEmpty(meshPath) && materialId >= 0)
            {
                _meshPathToMaterialId[meshPath] = materialId;
                _sharedMaterialIds.Add(materialId);   // shared by every entity of that model: never deleted per entity
                Log($"[SceneRenderService] Registered material {materialId} for mesh path: {meshPath}");
            }
        }

        /// <summary>
        /// Register a mesh ID for a submesh path (called during import to avoid re-import)
        /// </summary>
        public static void RegisterMeshIdForPath(string meshPath, long meshId)
        {
            if (!string.IsNullOrEmpty(meshPath) && meshId >= 0)
            {
                _submeshMeshCache[meshPath] = meshId;
                _sharedMeshIds.Add(meshId);
                _resolvedSubmeshes.Clear();   // per-path submesh sets are re-resolved on the next submit
                Log($"[SceneRenderService] Registered mesh {meshId} for path: {meshPath}");
            }
        }

        /// <summary>A model file changed on disk or is being re-imported (#339): forget every cached mesh, material and
        /// bounds entry for it (the bare path and its "path#submeshN" keys, relative or absolute), free the old meshes,
        /// and make the entities that used them re-resolve on their next submit. The path caches were keyed by the
        /// path string alone, so an overwritten .fbx/.glb kept showing the previous mesh until the file was renamed.</summary>
        public static void InvalidateModel(string modelPath)
        {
            if (string.IsNullOrEmpty(modelPath)) return;
            string target = NormalizeModelPath(modelPath);
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in _submeshMeshCache.Keys) if (SameModel(k, target)) keys.Add(k);
            foreach (var k in _meshPathToMaterialId.Keys) if (SameModel(k, target)) keys.Add(k);
            foreach (var k in _meshBoundsCache.Keys) if (SameModel(k, target)) keys.Add(k);
            if (keys.Count == 0) return;
            var ids = new HashSet<long>();
            foreach (var k in keys)
            {
                long id;
                if (_submeshMeshCache.TryGetValue(k, out id) && id >= 0) ids.Add(id);
                _submeshMeshCache.Remove(k);
                _meshPathToMaterialId.Remove(k);
                _meshBoundsCache.Remove(k);
                _isModelPath.Remove(k);
            }
            _resolvedSubmeshes.Clear();
            foreach (var id in ids)
            {
                _sharedMeshIds.Remove(id);
                try { VortexAPI.DeleteMesh(id); } catch { }
            }
            var inst = Instance;
            if (inst != null) inst.ForgetEntityMeshes(ids);
            RuntimeDirty = true;
        }

        /// <summary>Entities whose mesh id was just freed re-create it on their next submit.</summary>
        private void ForgetEntityMeshes(HashSet<long> ids)
        {
            var gone = new List<Guid>();
            foreach (var kv in _entityMeshes) if (ids.Contains(kv.Value)) gone.Add(kv.Key);
            foreach (var g in gone) { _entityMeshes.Remove(g); _entityMeshPaths.Remove(g); }
        }

        private void ForgetEntityMaterials(HashSet<long> ids)
        {
            var gone = new List<Guid>();
            foreach (var kv in _entityMaterials) if (ids.Contains(kv.Value)) gone.Add(kv.Key);
            foreach (var g in gone) { _entityMaterials.Remove(g); _entityMaterialKey.Remove(g); _entityMaterialColors.Remove(g); }
        }

        /// <summary>Free a model completely: its meshes (InvalidateModel) AND its import materials. For a model no entity
        /// references any more — a scene switch (#358); a live re-import keeps the materials (InvalidateModel).</summary>
        private static void EvictModel(string modelPath)
        {
            // whatever still holds this model's mesh ids must reload: the foliage runtimes cache them per type (#125)
            try { Editor.Core.Services.Foliage.FoliageService.Clear(); } catch { }
            string target = NormalizeModelPath(modelPath);
            var mats = new HashSet<long>();
            foreach (var kv in _meshPathToMaterialId) if (SameModel(kv.Key, target) && kv.Value >= 0) mats.Add(kv.Value);
            InvalidateModel(modelPath);
            foreach (var id in mats)
            {
                _sharedMaterialIds.Remove(id);
                try { VortexAPI.DeleteMaterial(id); } catch { }
            }
            var inst = Instance;
            if (inst != null && mats.Count > 0) inst.ForgetEntityMaterials(mats);
        }

        /// <summary>Scene switch (#358): the meshes, LOD chains and import materials of every model the new scene does not
        /// reference are freed (null = everything). They used to stay for the whole session so a reload never re-imported;
        /// with the import cache (#364 C) a reload is a .vmesh read, so holding every model ever opened only cost memory.
        /// Textures stay cached by path (shared between models and scenes).</summary>
        public static void EvictModelsUnusedBy(Data.Scene scene)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (scene?.Entities != null) CollectModelPaths(scene.Entities, used);
            var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in _submeshMeshCache.Keys) models.Add(NormalizeModelPath(k));
            foreach (var k in _meshPathToMaterialId.Keys) models.Add(NormalizeModelPath(k));
            int n = 0;
            foreach (var m in models)
            {
                if (string.IsNullOrEmpty(m) || used.Contains(m)) continue;
                EvictModel(m); n++;
            }
            if (n > 0) Log("[SceneRenderService] evicted " + n + " model(s) the scene does not use");
        }

        private static void CollectModelPaths(IEnumerable<GameEntity> entities, HashSet<string> into)
        {
            foreach (var e in entities)
            {
                if (e == null) continue;
                var mr = e.GetComponent<MeshRenderer>();
                if (mr != null && !string.IsNullOrEmpty(mr.MeshPath) && !IsPrimitivePath(mr.MeshPath)) into.Add(NormalizeModelPath(mr.MeshPath));
                // painted vegetation (#125) references its models through the Foliage component's types, not a MeshRenderer:
                // a scene switch evicted them and the foliage runtimes kept drawing dead mesh ids (nothing on screen)
                var fol = e.GetComponent<Editor.ECS.Components.Rendering.Foliage>();
                if (fol != null && fol.Types != null)
                    foreach (var t in fol.Types)
                        if (t != null && !string.IsNullOrEmpty(t.MeshPath)) into.Add(NormalizeModelPath(t.MeshPath));
                if (e.Children != null) CollectModelPaths(e.Children, into);
            }
        }

        /// <summary>Load a model through the render cache (once per path and session) and return its submesh mesh and
        /// material ids in submesh order, or null when it is not a loadable model file. Placement reads bounds, names
        /// and materials from here instead of importing the file per placement — those imports created meshes, LOD
        /// chains, materials and texture uploads nothing ever drew or freed (#357).</summary>
        public static VortexAPI.SubmeshImportData[] LoadModelSubmeshes(string meshPath)
        {
            if (string.IsNullOrEmpty(meshPath)) return null;
            string ext = null;
            try { ext = System.IO.Path.GetExtension(meshPath)?.ToLowerInvariant(); } catch { }
            if (!IsModelFileExtension(ext)) return null;
            var inst = Instance;
            if (inst == null) return null;
            long first;
            try { first = inst.LoadMeshFromFile(meshPath); } catch { return null; }
            if (first < 0) return null;
            var list = new List<VortexAPI.SubmeshImportData>();
            for (int i = 0; i < 4096; i++)
            {
                string key = meshPath + "#submesh" + i;
                long meshId;
                if (!_submeshMeshCache.TryGetValue(key, out meshId) || meshId < 0) break;
                list.Add(new VortexAPI.SubmeshImportData { MeshId = meshId, MaterialId = GetMaterialForMeshPath(key), TextureId = -1 });
            }
            if (list.Count == 0) list.Add(new VortexAPI.SubmeshImportData { MeshId = first, MaterialId = GetMaterialForMeshPath(meshPath), TextureId = -1 });
            return list.ToArray();
        }

        /// <summary>A cache key or model path without its "#submeshN" suffix, as an absolute path (relative keys are
        /// resolved against the open project), so relative and absolute spellings of one file compare equal.</summary>
        private static string NormalizeModelPath(string p)
        {
            string s = p;
            int h = s.LastIndexOf('#');
            if (h > 0 && s.Length > h + 7 && s.Substring(h + 1, 7) == "submesh") s = s.Substring(0, h);
            try
            {
                var proj = Data.ProjectData.Current != null ? Data.ProjectData.Current.Path : null;
                if (!System.IO.Path.IsPathRooted(s) && !string.IsNullOrEmpty(proj)) s = System.IO.Path.Combine(proj, s);
                return System.IO.Path.GetFullPath(s);
            }
            catch { return s; }
        }

        private static bool SameModel(string key, string targetFull)
            => string.Equals(NormalizeModelPath(key), targetFull, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Get the material ID for a mesh path (if one was imported)
        /// </summary>
        public static long GetMaterialForMeshPath(string meshPath)
        {
            if (!string.IsNullOrEmpty(meshPath) && _meshPathToMaterialId.TryGetValue(meshPath, out long materialId))
            {
                return materialId;
            }
            return -1;
        }

        /// <summary>Apply <paramref name="apply"/> to EVERY live scene material of a model's submesh, matched by the
        /// ACTUAL registered mesh-path key resolved to an absolute file — so a material edit reaches the object no
        /// matter how it stored its model path (project-relative, absolute, or a prefab that stored just the file
        /// name). This is what makes an edited material's colour update in the live viewport / placed instances, where
        /// a single fixed-key lookup missed prefab-placed models. Returns how many live materials were updated.</summary>
        public static int ApplyToLiveMaterialsForModel(string modelAbsPath, int submeshIndex, Action<long> apply)
        {
            if (string.IsNullOrEmpty(modelAbsPath) || apply == null) return 0;
            string modelFull;
            try { modelFull = System.IO.Path.GetFullPath(modelAbsPath); } catch { modelFull = modelAbsPath; }
            var proj = Data.ProjectData.Current?.Path;
            int n = 0;
            // Snapshot the keys: apply() only mutates native materials, not the dictionary, but be defensive.
            foreach (var kv in new List<KeyValuePair<string, long>>(_meshPathToMaterialId))
            {
                var key = kv.Key;
                int hash = key.LastIndexOf('#');
                if (hash <= 0 || !(key.Length > hash + 7 && key.Substring(hash + 1, 7) == "submesh")) continue;
                if (!int.TryParse(key.Substring(hash + 8), out int idx) || idx != submeshIndex) continue;

                var basePath = key.Substring(0, hash);
                bool match = false;
                try
                {
                    var baseAbs = System.IO.Path.IsPathRooted(basePath) || string.IsNullOrEmpty(proj)
                        ? basePath : System.IO.Path.Combine(proj, basePath);
                    match = string.Equals(System.IO.Path.GetFullPath(baseAbs), modelFull, StringComparison.OrdinalIgnoreCase);
                }
                catch { }
                // Match on the RESOLVED absolute path only — NEVER a bare filename fallback, which would apply this
                // model's edit to a DIFFERENT model that merely shares a file name (e.g. two washer.glb) and corrupt it.

                if (match && kv.Value >= 0) { try { apply(kv.Value); n++; } catch { } }
            }
            return n;
        }

        private bool _isInitialized;

        private SceneRenderService() { }

        public void Initialize()
        {
            if (_isInitialized) return;
            _isInitialized = true;
        }

        public void Shutdown()
        {
            ClearAllRenderables();
            _isInitialized = false;
        }

        /// <summary>
        /// Preloads all textures and materials for entities in a scene.
        /// Should be called when a scene is activated.
        /// </summary>
        public void PreloadSceneAssets(Data.Scene scene)
        {
            if (scene == null) return;

            _vmatPathCache.Clear();   // fresh material resolution for the (re)loaded scene
            Decals.DecalService.ClearMaterialCache();
            try { EvictModelsUnusedBy(scene); } catch { }   // the previous scene's models go (#358)
            Log($"[SceneRenderService] Preloading assets for scene: {scene.Name}");
            var projectPath = Data.ProjectData.Current?.Path ?? "";

            PreloadEntitiesRecursive(scene.Entities, projectPath);
        }

        private void PreloadEntitiesRecursive(IEnumerable<GameEntity> entities, string projectPath)
        {
            foreach (var entity in entities)
            {
                var meshRenderer = entity.GetComponent<MeshRenderer>();
                if (meshRenderer != null && !string.IsNullOrEmpty(meshRenderer.TexturePath))
                {
                    PreloadTextureForEntity(entity.Id, meshRenderer, projectPath);
                }

                // Recursively preload children
                if (entity.Children != null && entity.Children.Count > 0)
                {
                    PreloadEntitiesRecursive(entity.Children, projectPath);
                }
            }
        }

        private void PreloadTextureForEntity(Guid entityId, MeshRenderer meshRenderer, string projectPath)
        {
            string meshPath = meshRenderer.MeshPath;
            
            // Check if we already have a material cached for this mesh path
            if (_meshPathToMaterialId.ContainsKey(meshPath))
            {
                return; // Already loaded
            }

            string texturePath = meshRenderer.TexturePath;
            if (string.IsNullOrEmpty(texturePath))
            {
                return;
            }

            // Build full path
            string fullTexturePath = texturePath;
            if (!System.IO.Path.IsPathRooted(texturePath))
            {
                fullTexturePath = System.IO.Path.Combine(projectPath, texturePath);
            }

            if (!AssetVfs.Exists(fullTexturePath))
            {
                Log($"[SceneRenderService] Texture not found: {fullTexturePath}");
                return;
            }

            try
            {
                // Import texture
                long textureId = ImportTexturePath(fullTexturePath);
                if (textureId >= 0)
                {
                    // Create material with texture
                    long materialId = VortexAPI.CreateNewMaterial();
                    if (materialId >= 0)
                    {
                        // white: the MeshRenderer colour tints this instance alone (#331), not the shared material
                        VortexAPI.SetMaterialBaseColor(materialId, 1.0f, 1.0f, 1.0f, 1.0f);
                        VortexAPI.SetMaterialAlbedoTexture(materialId, textureId);

                        // Cache the material (per model path; a primitive's texture belongs to this entity only)
                        if (!IsPrimitivePath(meshPath)) RegisterMaterialForMeshPath(meshPath, materialId);
                        _entityMaterials[entityId] = materialId;

                        Log($"[SceneRenderService] Preloaded texture for {meshPath}: {fullTexturePath}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[SceneRenderService] Error preloading texture: {ex.Message}");
            }
        }

        /// <summary>
        /// Submit an entity for rendering this frame.
        /// </summary>
        public void SubmitEntity(GameEntity entity)
        {
            if (entity == null || !entity.IsActive) return;

            var meshRenderer = entity.GetComponent<MeshRenderer>();
            if (meshRenderer == null || !meshRenderer.IsEnabled) return;

            var transform = entity.GetComponent<Transform>();
            if (transform == null) return;

            // Skip if MeshPath is empty (don't log every frame)
            if (string.IsNullOrEmpty(meshRenderer.MeshPath))
            {
                return;
            }

            // Get or create mesh (with dirty checking)
            long meshId = GetOrCreateMesh(entity.Id, meshRenderer);
            if (meshId < 0)
            {
                return;
            }

            // Build world matrix from transform (including parent transforms) — into a reused buffer, no allocation
            float[] worldMatrix = _worldTmp;
            BuildWorldMatrixInto(entity, worldMatrix, 0);

            // Skinned characters: an entity with an Animator + a skinned model renders through the GPU
            // skinning path — the AnimationService supplies the bone palette (animated pose while playing,
            // bind pose in edit mode). Rigid submeshes of the same model still go through the normal path.
            // The Animator may sit on an ANCESTOR: multi-submesh models import as a parent container with
            // '#submeshN' child entities, and the user drops the Animator on the container.
            float[] bonePalette = null; int boneCount = 0;
            var animatorOwner = entity;
            var animator = entity.GetComponent<Editor.ECS.Components.Animation.Animator>();
            while (animator == null && animatorOwner.Parent != null)
            {
                animatorOwner = animatorOwner.Parent;
                animator = animatorOwner.GetComponent<Editor.ECS.Components.Animation.Animator>();
            }
            if (animator != null && animator.IsEnabled)
                Core.Animation.AnimationService.Instance.TryGetPalette(animatorOwner, meshRenderer.MeshPath, out bonePalette, out boneCount);

            // Render layer (#175): 0 = world, 1 = first-person viewmodel (second pass, own FOV, depth
            // cleared, casts no shadows), 2 = third-person only (hidden for the local player in play).
            // Travels with every submit variant below. While playing (editor play, game window, GameHost)
            // the layers always behave like the shipped game; in edit mode the viewport's preview mode
            // decides how FP/3P meshes are presented (see ViewmodelPreviewMode).
            int layer = meshRenderer.RenderLayer;
            if (layer < 0 || layer > 2) layer = 0;   // unknown layers = world geometry, in editor AND play
            if (layer != 0)
            {
                if (DebugThirdPersonView)
                {
                    // In-game debug freecam: you are now an EXTERNAL camera looking AT the player, so render
                    // like OTHER cameras see them — show the third-person body (layer 2) as world geometry,
                    // hide the local first-person viewmodel (layer 1). Takes priority over the play-like FP
                    // behaviour so flying out shows the character cleanly instead of nothing + floating arms.
                    // DebugShowViewmodel inverts that: the freecam inspects the FIRST-PERSON arms + weapon
                    // (grip / finger placement) as world geometry and hides the body instead.
                    if (layer == (DebugShowViewmodel ? 2 : 1)) return;
                    layer = 0;
                }
                else if (IsPlayLike || EditorViewmodelPreview == ViewmodelPreviewMode.GameView)
                {
                    if (layer == 2) return;            // third-person only: the local player never sees it
                }
                else if (EditorViewmodelPreview == ViewmodelPreviewMode.Hidden)
                {
                    if (layer == 1) return;            // FP meshes live in the game's FP pass, not the build view
                    layer = 0;                          // 3P body: normal world object while editing
                }
                else // AsWorld — placement mode: draw FP/3P meshes as plain world geometry
                {
                    layer = 0;
                }
            }

            // Imported models (no explicit .vmat) are multi-submesh with per-submesh colored materials —
            // submit EVERY submesh, not just the first, so e.g. a Kenney tree shows trunk + leaves.
            if (string.IsNullOrEmpty(meshRenderer.MaterialPath) && IsModelPath(meshRenderer.MeshPath))
            {
                var set = ResolveSubmeshes(meshRenderer.MeshPath);   // resolved once per path, not per frame
                if (set != null)
                {
                    for (int i = 0; i < set.MeshIds.Length; i++)
                    {
                        long subMesh = set.MeshIds[i];
                        long subMat = GetMaterialForMeshPath(set.Keys[i]);
                        float tr, tg, tb, ta;
                        TintFor(meshRenderer, subMat, out tr, out tg, out tb, out ta);   // per-instance tint (#331)
                        if (bonePalette != null && Core.Animation.AnimationService.Instance.IsMeshSkinned(subMesh))
                            VortexAPI.SubmitSkinnedMeshTinted(subMesh, subMat, worldMatrix, bonePalette, boneCount, layer, tr, tg, tb, ta);
                        else
                            SubmitRigid(subMesh, subMat, worldMatrix, layer, tr, tg, tb, ta);
                        _submitN++;
                    }
                    return;
                }
            }

            // Primitive / single mesh / explicitly-assigned .vmat:
            long materialId = GetOrCreateMaterial(entity.Id, meshRenderer);
            float r, g, b, a;
            TintFor(meshRenderer, materialId, out r, out g, out b, out a);   // per-instance tint (#331)
            if (bonePalette != null && Core.Animation.AnimationService.Instance.IsMeshSkinned(meshId))
                VortexAPI.SubmitSkinnedMeshTinted(meshId, materialId, worldMatrix, bonePalette, boneCount, layer, r, g, b, a);
            else
                SubmitRigid(meshId, materialId, worldMatrix, layer, r, g, b, a);
            _submitN++;
        }

        /// <summary>A rigid (non-skinned) submit: batched per (mesh, material, layer) inside SubmitScene, immediate
        /// when SubmitEntity is called on its own. The tint rides with the instance (#331).</summary>
        private void SubmitRigid(long meshId, long materialId, float[] world, int layer, float r, float g, float b, float a)
        {
            if (_batching) _batcher.Add(meshId, materialId, layer, world, r, g, b, a);
            else VortexAPI.SubmitMeshForRenderingTinted(meshId, materialId, world, r, g, b, a, layer);
        }

        private static void FlushBatch(long mesh, long material, int layer, float[] matrices, float[] tints, int count)
            => VortexAPI.SubmitMeshInstancedTinted(mesh, material, matrices, tints, count, layer);

        /// <summary>Is this an imported model path (.fbx/.glb/…)? Cached per path — the extension check allocated
        /// two strings per entity per frame.</summary>
        private static bool IsModelPath(string meshPath)
        {
            bool m;
            if (_isModelPath.TryGetValue(meshPath, out m)) return m;
            var ext = System.IO.Path.GetExtension(meshPath);
            m = IsModelFileExtension(ext != null ? ext.ToLowerInvariant() : null);
            _isModelPath[meshPath] = m;
            return m;
        }

        /// <summary>The loaded submeshes of a model path (null when none are loaded yet) — resolved once and reused
        /// every frame; <see cref="_resolvedSubmeshes"/> is cleared whenever the submesh cache changes.</summary>
        private static SubmeshSet ResolveSubmeshes(string meshPath)
        {
            SubmeshSet set;
            if (_resolvedSubmeshes.TryGetValue(meshPath, out set)) return set;
            var keys = new List<string>(); var ids = new List<long>();
            for (int n = 0; n < 64; n++)
            {
                string sub = meshPath + "#submesh" + n;
                long id;
                if (_submeshMeshCache.TryGetValue(sub, out id) && id >= 0) { keys.Add(sub); ids.Add(id); }
                else if (n > 0) break;
            }
            set = keys.Count > 0 ? new SubmeshSet { Keys = keys.ToArray(), MeshIds = ids.ToArray() } : null;
            _resolvedSubmeshes[meshPath] = set;   // null is cached too ("not loaded yet"); an import clears it
            return set;
        }






        /// <summary>
        /// Submit all entities in a scene for rendering.
        /// </summary>
        // ---- static / dynamic split (#364 A) ---------------------------------------------------------------------
        // While playing, the scene is re-submitted every frame something moves. The renderer now keeps a RETAINED
        // static set (BeginStaticSubmit .. EndStaticSubmit) and merges the per-frame submits into it, so the managed
        // side only walks the entities that actually move every frame: those with an Animator, Rigidbody, NavAgent,
        // Ragdoll or ParticleSystem on them or an ancestor. The static set is re-sent only when a static transform
        // changed (Transform.StaticVersion), something structural happened (StaticDirty) or the scene changed.
        // VORTEX_STATIC_SUBMIT=0 switches back to the full submit; an older native library does so by itself.
        private static bool _splitEnabled = Environment.GetEnvironmentVariable("VORTEX_STATIC_SUBMIT") != "0";
        private readonly List<GameEntity> _dynamicEntities = new List<GameEntity>();
        private Data.Scene _splitScene;
        private int _staticVersionSent = -1;
        private bool _splitWasActive;
        /// <summary>Static submits of the last structural pass (the FPS log and tests).</summary>
        public static int LastStaticMeshes;

        private static readonly HashSet<string> DynamicComponentNames = new HashSet<string>(StringComparer.Ordinal)
            { "Animator", "Rigidbody", "NavAgent", "Ragdoll", "ParticleSystem" };

        /// <summary>Does this entity itself move every frame (a per-frame component on it)?</summary>
        internal static bool IsDynamicSelf(GameEntity e)
        {
            var comps = e.Components;
            if (comps == null) return false;
            for (int i = 0; i < comps.Count; i++)
            {
                var c = comps[i];
                if (c != null && DynamicComponentNames.Contains(c.GetType().Name)) return true;
            }
            return false;
        }

        /// <summary>Walk a subtree the way SubmitEntityRecursive does: dynamic entities (self or ancestor) go to
        /// <paramref name="dynamicOut"/> and are re-submitted every frame, static ones to <paramref name="submitStatic"/>.
        /// Inactive subtrees are skipped (a later SetActive raises StaticDirty, which re-classifies).</summary>
        internal static void Classify(GameEntity entity, bool parentDynamic, List<GameEntity> dynamicOut, Action<GameEntity> submitStatic)
        {
            if (entity == null) return;
            if (entity.IsHiddenInEditor && !IsPlayLike) return;
            if (!entity.IsActive) return;
            bool dyn = parentDynamic || IsDynamicSelf(entity);
            entity.RenderDynamic = dyn;
            if (dyn) dynamicOut.Add(entity); else submitStatic(entity);
            if (entity.Children != null)
                foreach (var child in entity.Children) Classify(child, dyn, dynamicOut, submitStatic);
        }

        private void SubmitSceneSplit(Data.Scene scene)
        {
            bool structural = StaticDirty || _staticVersionSent != Transform.StaticVersion || !ReferenceEquals(scene, _splitScene);
            if (structural)
            {
                if (!VortexAPI.BeginStaticSubmit()) { _splitEnabled = false; SubmitSceneFull(scene); return; }   // older native library
                _dynamicEntities.Clear();
                _submitN = 0;
                _batching = true;
                try { foreach (var entity in scene.Entities) Classify(entity, false, _dynamicEntities, SubmitEntity); }
                finally { _batching = false; _batcher.Flush(FlushBatch); }
                VortexAPI.EndStaticSubmit();
                LastStaticMeshes = _submitN;
                _staticVersionSent = Transform.StaticVersion;
                StaticDirty = false;
                _splitScene = scene;
                _splitWasActive = true;
            }
            // the dynamic entities, every frame (skinned meshes carry their bone palettes here)
            _submitN = 0;
            _batching = true;
            try { for (int i = 0; i < _dynamicEntities.Count; i++) SubmitEntity(_dynamicEntities[i]); }
            finally { _batching = false; _batcher.Flush(FlushBatch); }
        }

        /// <summary>The whole scene into the per-frame queue (edit mode, or the split switched off).</summary>
        private void SubmitSceneFull(Data.Scene scene)
        {
            if (_splitWasActive)
            {
                // leaving the split: an empty static pass drops the renderer's retained set, or it would draw twice
                if (VortexAPI.BeginStaticSubmit()) VortexAPI.EndStaticSubmit();
                _splitWasActive = false;
                _splitScene = null;
            }
            _submitN = 0;
            _batching = true;
            try
            {
                foreach (var entity in scene.Entities)
                {
                    SubmitEntityRecursive(entity);
                }
            }
            finally
            {
                _batching = false;
                _batcher.Flush(FlushBatch);   // one P/Invoke per (mesh, material, layer) group
            }
        }

        public void SubmitScene(Data.Scene scene)
        {
            if (scene == null || scene.Entities == null) return;

            // Clear and submit all lights first
            SubmitSceneLights(scene);
            Decals.DecalService.Submit(scene, IsPlayLike);   // #120: the scene's Decal components + spawned decals, like the lights
            Editor.Core.Services.Terrain.TerrainService.Submit(scene, IsPlayLike);   // #124: the terrains' LOD chunks (built / rebuilt here)
            Editor.Core.Services.Foliage.FoliageService.Submit(scene, IsPlayLike);   // #125: painted foliage through the instancing path
            Editor.Core.Services.Water.WaterService.Submit(scene, IsPlayLike);   // #200: water surfaces into the transparent pass

            var swSubmit = System.Diagnostics.Stopwatch.StartNew();
            if (_splitEnabled && IsPlayLike) SubmitSceneSplit(scene);
            else SubmitSceneFull(scene);
            swSubmit.Stop();
            LastSubmitMs = swSubmit.Elapsed.TotalMilliseconds; LastSubmitMeshes = _submitN;
            // a slow submit is either the first one (it imports every model) or a scene too big for the per-frame
            // re-submit (#364 A) — say so, at most once every 10 s
            if (swSubmit.ElapsedMilliseconds > 500 && (DateTime.UtcNow - _slowSubmitLogAt).TotalSeconds > 10)
            {
                _slowSubmitLogAt = DateTime.UtcNow;
                try { ConsoleService.Instance.LogSystem("Scene submit '" + scene.Name + "': " + _submitN + " meshes in " + swSubmit.ElapsedMilliseconds + " ms"); } catch { }
            }
            if (scene.Name != "Lobby" && _ssDbg < 12) { _ssDbg++; try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vortex_submit.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " SubmitScene '" + scene.Name + "' topEnts=" + System.Linq.Enumerable.Count(scene.Entities) + " submitted=" + _submitN + "\r\n"); } catch { } }

        }

        /// <summary>One entity's collider as a wireframe net (green solid / amber trigger) into the gizmo
        /// queue — shared by the selected-entity overlay and the "show all colliders" walk (#49).</summary>
        private void SubmitColliderGizmoFor(GameEntity entity)
        {
            var transform = entity != null ? entity.Transform : null;
            if (transform == null) return;
            var col = entity.GetComponent<Editor.ECS.Components.Physics.Collider>();
            if (col == null || !col.IsEnabled) return;

            var pos = transform.LocalPosition;
            var rot = transform.LocalRotation;
            float sx = transform.LocalScale.X, sy = transform.LocalScale.Y, sz = transform.LocalScale.Z;
            float ccx = pos.X + col.Center.X * sx, ccy = pos.Y + col.Center.Y * sy, ccz = pos.Z + col.Center.Z * sz;
            bool trig = col.IsTrigger; // amber net for a trigger, green for a solid — visible toggle feedback
            if (col is Editor.ECS.Components.Physics.BoxCollider bc)
                VortexAPI.RenderColliderBox(ccx, ccy, ccz, Math.Abs(bc.Size.X * 0.5f * sx), Math.Abs(bc.Size.Y * 0.5f * sy), Math.Abs(bc.Size.Z * 0.5f * sz), rot.Y, trig);
            else if (col is Editor.ECS.Components.Physics.SphereCollider spc)
                VortexAPI.RenderColliderSphere(ccx, ccy, ccz, spc.Radius * Math.Max(Math.Abs(sx), Math.Max(Math.Abs(sy), Math.Abs(sz))), trig);
            else if (col is Editor.ECS.Components.Physics.CapsuleCollider cpc)
            {
                float cr = cpc.Radius * Math.Max(Math.Abs(sx), Math.Abs(sz));
                VortexAPI.RenderColliderCapsule(ccx, ccy, ccz, cr, Math.Max(0f, cpc.Height * 0.5f * Math.Abs(sy) - cr), trig);
            }
            else // Mesh / base collider: draw the ACTUAL render mesh as a green net (the collision mesh IS the
                 // render mesh), so a round object shows a round net — not a box. Falls back to a bounds net box.
            {
                if (!RenderMeshColliderWireframe(entity, trig))
                {
                    var b = CalculateCombinedBounds(entity);
                    VortexAPI.RenderColliderBox(pos.X + b.CenterOffset.X, pos.Y + b.CenterOffset.Y, pos.Z + b.CenterOffset.Z, b.Size.X * 0.5f, b.Size.Y * 0.5f, b.Size.Z * 0.5f, rot.Y, trig);
                }
            }
        }

        private void SubmitColliderGizmosRecursive(GameEntity entity, GameEntity skip)
        {
            if (entity == null || !entity.IsActive || entity.IsHiddenInEditor) return;
            if (!ReferenceEquals(entity, skip)) SubmitColliderGizmoFor(entity);
            if (entity.Children != null)
                foreach (var c in entity.Children) SubmitColliderGizmosRecursive(c, skip);
        }

        /// <summary>Editor overlays: camera/light icons + the selected entity's outline (or camera frustum) + the
        /// transform gizmo. These all go into the always-on-top GIZMO queue, and this is called EVERY frame in edit
        /// mode (decoupled from SubmitScene's static-reuse) so the gizmo instantly reflects the current tool mode /
        /// drag / hover / selection + camera — without it, switching tools or releasing a drag wouldn't update on a
        /// static scene.</summary>
        public void SubmitOverlays(Data.Scene scene)
        {
            if (scene == null || scene.Entities == null) return;
            if (HideEditorOverlays) return;

            var selected = SelectionService.Instance.SelectedEntity;
            RenderAllCameraIcons(scene, selected);
            RenderAllLightIcons(scene, selected);
            RenderAllAudioIcons(scene, selected);

            // #49: "Show ALL colliders" — every entity's collision shape at a glance (trigger zones
            // amber, solids green) while level-building. The selected entity's collider still draws
            // through the block below, so skip it here to avoid a double net.
            if (EditorViewportService.Instance.AreCollidersVisible && EditorViewportService.Instance.ShowAllColliders)
                foreach (var entity in scene.Entities)
                    SubmitColliderGizmosRecursive(entity, selected);

            if (selected == null) return;
            var transform = selected.Transform;
            if (transform == null) return;

            var pos = transform.LocalPosition;
            var rot = transform.LocalRotation;

            var camera = selected.GetComponent<Camera>();
            if (camera != null)
            {
                // Selected camera: show its FOV frustum instead of a box outline.
                VortexAPI.RenderCameraGizmo(
                    pos.X, pos.Y, pos.Z,
                    rot.X, rot.Y, rot.Z,
                    camera.FieldOfView, 16f / 9f,
                    camera.CameraType == CameraType.MainCamera);
            }
            else
            {
                var bounds = CalculateCombinedBounds(selected);
                VortexAPI.RenderSelectionOutline(
                    pos.X + bounds.CenterOffset.X,
                    pos.Y + bounds.CenterOffset.Y,
                    pos.Z + bounds.CenterOffset.Z,
                    bounds.Size.X, bounds.Size.Y, bounds.Size.Z,
                    rot.X, rot.Y, rot.Z);
            }

            // Green collider wireframe for the selected entity, so you SEE its collision shape where it sits.
            // Gated by the "Show Collision" viewport toggle (EditorViewportService.AreCollidersVisible).
            if (EditorViewportService.Instance.AreCollidersVisible)
                SubmitColliderGizmoFor(selected);

            // Audio gizmos (issue #18): the selected AudioSource's min/max distance spheres and a
            // selected ReverbZone's boundary + falloff shell. Values are read fresh every frame,
            // so inspector edits and entity drags update the shapes live.
            var audioSrc = selected.GetComponent<ECS.Components.Audio.AudioSource>();
            if (audioSrc != null && audioSrc.IsEnabled)
                VortexAPI.RenderAudioRangeSpheres(pos.X, pos.Y, pos.Z, audioSrc.MinDistance, audioSrc.MaxDistance);
            var reverbZone = selected.GetComponent<ECS.Components.Audio.ReverbZone>();
            if (reverbZone != null && reverbZone.IsEnabled)
                // Max(0.01, extent) — NOT Abs — mirrors the runtime test (ZoneWeight), so the drawn box is
                // exactly the audible one even for hand-edited negative extents.
                VortexAPI.RenderReverbZoneGizmo(pos.X, pos.Y, pos.Z, reverbZone.Shape, reverbZone.Radius,
                    Math.Max(0.01f, reverbZone.BoxExtents.X), Math.Max(0.01f, reverbZone.BoxExtents.Y), Math.Max(0.01f, reverbZone.BoxExtents.Z),
                    reverbZone.Falloff);
            var decal = selected.GetComponent<ECS.Components.Rendering.Decal>();
            if (decal != null && decal.IsEnabled)
                VortexAPI.RenderDecalGizmo(Decals.DecalService.BoxWorld(selected, decal));   // the projection box (#120)
            Editor.Core.Services.Terrain.TerrainToolService.SubmitGizmo();   // the terrain brush under the cursor (#124)
            Editor.Core.Services.Foliage.FoliageToolService.SubmitGizmo();   // the foliage brush (#125)

            if (VortexAPI.AreGizmosVisible)
            {
                // Constant on-screen size (Blender/Unreal feel) — the identical scale is used by the picker in
                // GamePreviewView (RaycastService.ComputeGizmoScale), so the clickable boxes sit on the drawn arrows.
                float gizmoScale = RaycastService.ComputeGizmoScale(new Vector3f(pos.X, pos.Y, pos.Z));
                VortexAPI.RenderGizmo(pos.X, pos.Y, pos.Z, transform.LocalScale.Y, gizmoScale);
            }
        }

        /// <summary>Speaker icons at every AudioSource and a head icon at every AudioListener —
        /// camera-facing billboards, drawn regardless of selection (like camera icons).</summary>
        private void RenderAllAudioIcons(Data.Scene scene, GameEntity selected)
        {
            if (!VortexAPI.AreGizmosVisible) return;
            var cam = EditorCameraController.Instance;
            foreach (var entity in scene.Entities)
                RenderAudioIconRecursive(entity, selected, cam.PositionX, cam.PositionY, cam.PositionZ);
        }

        private void RenderAudioIconRecursive(GameEntity entity, GameEntity selected, float camX, float camY, float camZ)
        {
            if (entity == null || !entity.IsActive || entity.IsHiddenInEditor) return;   // eye toggle hides the icon too
            var transform = entity.Transform;
            if (transform != null)
            {
                var pos = transform.LocalPosition;
                if (entity.GetComponent<ECS.Components.Audio.AudioSource>() != null)
                    VortexAPI.RenderAudioSourceIcon(pos.X, pos.Y, pos.Z, camX, camY, camZ, entity == selected);
                if (entity.GetComponent<ECS.Components.Audio.AudioListener>() != null)
                {
                    // Listeners usually sit on a camera entity — float the head above the camera icon.
                    float lift = entity.GetComponent<Camera>() != null ? 0.45f : 0f;
                    VortexAPI.RenderAudioListenerIcon(pos.X, pos.Y + lift, pos.Z, camX, camY, camZ);
                }
            }

            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                    RenderAudioIconRecursive(child, selected, camX, camY, camZ);
            }
        }

        /// <summary>Draw a Mesh Collider as a green wireframe net over the entity's ACTUAL render mesh (the collision
        /// mesh is the render mesh), at the same world transform the mesh renders with. Mirrors the submesh resolution
        /// in RenderMesh so multi-submesh imports net every part. Returns false (caller falls back to a bounds box)
        /// when the entity has no usable render mesh.</summary>
        private bool RenderMeshColliderWireframe(GameEntity entity, bool isTrigger = false)
        {
            var meshRenderer = entity.GetComponent<MeshRenderer>();
            if (meshRenderer == null || !meshRenderer.IsEnabled || string.IsNullOrEmpty(meshRenderer.MeshPath))
                return false;

            long meshId = GetOrCreateMesh(entity.Id, meshRenderer);
            if (meshId < 0) return false;

            float[] worldMatrix = BuildWorldMatrixWithParent(entity);

            // Multi-submesh imported model: net every cached submesh (same path the renderer submits).
            var ext = System.IO.Path.GetExtension(meshRenderer.MeshPath)?.ToLowerInvariant();
            if (string.IsNullOrEmpty(meshRenderer.MaterialPath) && IsModelFileExtension(ext))
            {
                bool any = false;
                for (int n = 0; n < 64; n++)
                {
                    string sub = meshRenderer.MeshPath + "#submesh" + n;
                    if (_submeshMeshCache.TryGetValue(sub, out long subMesh) && subMesh >= 0)
                    {
                        VortexAPI.RenderColliderMeshWire(subMesh, worldMatrix, isTrigger);
                        any = true;
                    }
                    else if (n > 0) break;
                }
                if (any) return true;
            }

            VortexAPI.RenderColliderMeshWire(meshId, worldMatrix, isTrigger);
            return true;
        }

        /// <summary>
        /// Represents bounds with size and center offset
        /// </summary>
        private struct EntityBounds
        {
            public ECS.Vector3 Size;
            public ECS.Vector3 CenterOffset;
        }

        /// <summary>
        /// Calculate combined bounds for an entity, including all children with MeshRenderers.
        /// </summary>
        private EntityBounds CalculateCombinedBounds(GameEntity entity)
        {
            var bounds = new EntityBounds
            {
                Size = entity.Transform?.LocalScale ?? ECS.Vector3.One,
                CenterOffset = ECS.Vector3.Zero
            };

            // If entity has no children, just use its own scale
            if (entity.Children == null || entity.Children.Count == 0)
            {
                return bounds;
            }

            // Check if any children have mesh renderers
            bool hasChildMeshes = false;
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

            foreach (var child in entity.Children)
            {
                var meshRenderer = child.GetComponent<MeshRenderer>();
                if (meshRenderer != null && !string.IsNullOrEmpty(meshRenderer.MeshPath))
                {
                    hasChildMeshes = true;
                    var childPos = child.Transform?.LocalPosition ?? ECS.Vector3.Zero;
                    var childScale = child.Transform?.LocalScale ?? ECS.Vector3.One;

                    // Get mesh bounds and center from cache
                    var meshBoundsInfo = GetMeshBoundsAndCenter(meshRenderer.MeshPath);
                    
                    // Apply mesh center offset and scale
                    float centerX = meshBoundsInfo.Center.X * childScale.X;
                    float centerY = meshBoundsInfo.Center.Y * childScale.Y;
                    float centerZ = meshBoundsInfo.Center.Z * childScale.Z;
                    
                    // Calculate world-space bounds for this child
                    float halfX = (meshBoundsInfo.Size.X * childScale.X) * 0.5f;
                    float halfY = (meshBoundsInfo.Size.Y * childScale.Y) * 0.5f;
                    float halfZ = (meshBoundsInfo.Size.Z * childScale.Z) * 0.5f;

                    // Add child position + mesh center offset
                    float worldCenterX = childPos.X + centerX;
                    float worldCenterY = childPos.Y + centerY;
                    float worldCenterZ = childPos.Z + centerZ;

                    minX = Math.Min(minX, worldCenterX - halfX);
                    minY = Math.Min(minY, worldCenterY - halfY);
                    minZ = Math.Min(minZ, worldCenterZ - halfZ);
                    maxX = Math.Max(maxX, worldCenterX + halfX);
                    maxY = Math.Max(maxY, worldCenterY + halfY);
                    maxZ = Math.Max(maxZ, worldCenterZ + halfZ);
                }
            }

            if (hasChildMeshes)
            {
                bounds.Size = new ECS.Vector3(maxX - minX, maxY - minY, maxZ - minZ);
                bounds.CenterOffset = new ECS.Vector3(
                    (minX + maxX) * 0.5f,
                    (minY + maxY) * 0.5f,
                    (minZ + maxZ) * 0.5f
                );
            }


            return bounds;
        }

        /// <summary>World-space AABB for viewport PICKING — the real hitbox that should match what's drawn.
        /// Uses the actual mesh bounds (cached from the engine) transformed by the entity's full world matrix
        /// (so it's correct for children of scaled/moved parents), instead of the old "LocalScale is the size"
        /// guess that made imported models almost unclickable. Non-mesh entities (cameras/lights/empties) get a
        /// small clickable box at their world position. Returns false only if the entity has no transform.</summary>
        public bool TryGetWorldPickBounds(GameEntity entity, out Vector3f center, out Vector3f halfExtents)
        {
            center = new Vector3f(0, 0, 0);
            halfExtents = new Vector3f(0.25f, 0.25f, 0.25f);
            var t = entity?.Transform;
            if (t == null) return false;

            // World transform (walks the parent chain). Translation is the last row; scale is each basis row length.
            float[] wm;
            try { wm = BuildWorldMatrixWithParent(entity); }
            catch { wm = BuildWorldMatrix(t); }
            float wpx = wm[12], wpy = wm[13], wpz = wm[14];
            float sx = (float)Math.Sqrt(wm[0] * wm[0] + wm[1] * wm[1] + wm[2] * wm[2]);
            float sy = (float)Math.Sqrt(wm[4] * wm[4] + wm[5] * wm[5] + wm[6] * wm[6]);
            float sz = (float)Math.Sqrt(wm[8] * wm[8] + wm[9] * wm[9] + wm[10] * wm[10]);

            var mr = entity.GetComponent<MeshRenderer>();
            if (mr != null && mr.IsEnabled && !string.IsNullOrEmpty(mr.MeshPath))
            {
                var mb = GetMeshBoundsAndCenter(mr.MeshPath);
                // World AABB of the ROTATED local box. The old version applied centre + extents on world axes
                // (scale only), so any rotated non-uniform object (a wall turned 90°) had its hitbox crossways
                // to the drawn mesh — clicks on the visible object missed and empty air hit. Row-vector matrix:
                // local axis i is row i (rows already include scale), so the centre offset transforms with the
                // full 3x3, and the tight world half-extent per axis is the |R·S| absolute-column sum.
                float cx = mb.Center.X * wm[0] + mb.Center.Y * wm[4] + mb.Center.Z * wm[8];
                float cy = mb.Center.X * wm[1] + mb.Center.Y * wm[5] + mb.Center.Z * wm[9];
                float cz = mb.Center.X * wm[2] + mb.Center.Y * wm[6] + mb.Center.Z * wm[10];
                center = new Vector3f(wpx + cx, wpy + cy, wpz + cz);
                float hx = Math.Abs(mb.Size.X) * 0.5f, hy = Math.Abs(mb.Size.Y) * 0.5f, hz = Math.Abs(mb.Size.Z) * 0.5f;
                halfExtents = new Vector3f(
                    Math.Max(Math.Abs(wm[0]) * hx + Math.Abs(wm[4]) * hy + Math.Abs(wm[8]) * hz, 0.1f),
                    Math.Max(Math.Abs(wm[1]) * hx + Math.Abs(wm[5]) * hy + Math.Abs(wm[9]) * hz, 0.1f),
                    Math.Max(Math.Abs(wm[2]) * hx + Math.Abs(wm[6]) * hy + Math.Abs(wm[10]) * hz, 0.1f));
                return true;
            }

            // No mesh: a modest box at the world pivot so lights/cameras/empties stay clickable.
            center = new Vector3f(wpx, wpy, wpz);
            halfExtents = new Vector3f(
                Math.Max(Math.Abs(sx) * 0.5f, 0.35f),
                Math.Max(Math.Abs(sy) * 0.5f, 0.35f),
                Math.Max(Math.Abs(sz) * 0.5f, 0.35f));
            return true;
        }

        // Cache for mesh bounds (size in local space)
        private static readonly Dictionary<string, ECS.Vector3> _meshBoundsCache = new Dictionary<string, ECS.Vector3>();
        private static readonly Dictionary<string, ECS.Vector3> _meshBoundsCenterCache = new Dictionary<string, ECS.Vector3>();

        /// <summary>
        /// Mesh bounds information (size and center)
        /// </summary>
        private struct MeshBoundsInfo
        {
            public ECS.Vector3 Size;
            public ECS.Vector3 Center;
        }

        /// <summary>While true, no editor overlays (selection outline, transform gizmo, light / camera / audio icons,
        /// collider nets) are submitted — a clean frame of the scene, e.g. for a viewport capture Claude looks at.</summary>
        public static bool HideEditorOverlays { get; set; }

        /// <summary>Size and center of a mesh's local bounding box (primitives: their unit box). False when the mesh
        /// cannot be loaded — size is then 1×1×1.</summary>
        public bool TryGetMeshBounds(string meshPath, out ECS.Vector3 size, out ECS.Vector3 center)
        {
            var info = GetMeshBoundsAndCenter(meshPath);
            size = info.Size;
            center = info.Center;
            return !string.IsNullOrEmpty(meshPath) && _meshBoundsCache.ContainsKey(meshPath);
        }

        /// <summary>
        /// Get bounds and center for a mesh path
        /// </summary>
        private MeshBoundsInfo GetMeshBoundsAndCenter(string meshPath)
        {
            var info = new MeshBoundsInfo
            {
                Size = ECS.Vector3.One,
                Center = ECS.Vector3.Zero
            };

            if (string.IsNullOrEmpty(meshPath))
                return info;

            // Check caches
            if (_meshBoundsCache.TryGetValue(meshPath, out var cachedSize))
            {
                info.Size = cachedSize;
                if (_meshBoundsCenterCache.TryGetValue(meshPath, out var cachedCenter))
                {
                    info.Center = cachedCenter;
                }
                return info;
            }

            // Try to get from engine
            float sizeX = 1f, sizeY = 1f, sizeZ = 1f;
            float centerX = 0f, centerY = 0f, centerZ = 0f;
            
            long meshId = -1;
            if (_submeshMeshCache.TryGetValue(meshPath, out meshId) && meshId >= 0)
            {
                // Get size
                if (VortexAPI.GetMeshBounds(meshId, out sizeX, out sizeY, out sizeZ))
                {
                    info.Size = new ECS.Vector3(sizeX, sizeY, sizeZ);
                    _meshBoundsCache[meshPath] = info.Size;
                }
                
                // Get center
                if (VortexAPI.GetMeshBoundsCenter(meshId, out centerX, out centerY, out centerZ))
                {
                    info.Center = new ECS.Vector3(centerX, centerY, centerZ);
                    _meshBoundsCenterCache[meshPath] = info.Center;
                }
            }
            else
            {
                // Try to find mesh in entity cache
                meshId = GetOrLoadMeshForBounds(meshPath);
                if (meshId >= 0)
                {
                    if (VortexAPI.GetMeshBounds(meshId, out sizeX, out sizeY, out sizeZ))
                    {
                        info.Size = new ECS.Vector3(sizeX, sizeY, sizeZ);
                        _meshBoundsCache[meshPath] = info.Size;
                    }
                    if (VortexAPI.GetMeshBoundsCenter(meshId, out centerX, out centerY, out centerZ))
                    {
                        info.Center = new ECS.Vector3(centerX, centerY, centerZ);
                        _meshBoundsCenterCache[meshPath] = info.Center;
                    }
                }
            }

            return info;
        }

        /// <summary>
        /// Get or calculate bounds for a mesh path
        /// </summary>
        private ECS.Vector3 GetMeshBounds(string meshPath)
        {
            if (string.IsNullOrEmpty(meshPath))
                return ECS.Vector3.One;

            if (_meshBoundsCache.TryGetValue(meshPath, out var cached))
                return cached;

            // Try to get bounds from engine
            float sizeX = 1f, sizeY = 1f, sizeZ = 1f;
            
            // Check if mesh is loaded and get its bounds
            if (_submeshMeshCache.TryGetValue(meshPath, out long meshId) && meshId >= 0)
            {
                if (VortexAPI.GetMeshBounds(meshId, out sizeX, out sizeY, out sizeZ))
                {
                    var bounds = new ECS.Vector3(sizeX, sizeY, sizeZ);
                    _meshBoundsCache[meshPath] = bounds;
                    return bounds;
                }
            }
            
            // If not found, try to load the mesh to get bounds
            // This happens during the first frame after scene load
            long loadedMeshId = GetOrLoadMeshForBounds(meshPath);
            if (loadedMeshId >= 0)
            {
                if (VortexAPI.GetMeshBounds(loadedMeshId, out sizeX, out sizeY, out sizeZ))
                {
                    var bounds = new ECS.Vector3(sizeX, sizeY, sizeZ);
                    _meshBoundsCache[meshPath] = bounds;
                    return bounds;
                }
            }

            // Return default bounds
            return new ECS.Vector3(1f, 1f, 1f);
        }

        /// <summary>
        /// Try to load a mesh just to get its bounds (without caching the mesh itself)
        /// </summary>
        private long GetOrLoadMeshForBounds(string meshPath)
        {
            try
            {
                // Shared caches first — O(1), where the scan below is O(entities) per bounds query
                long sharedId;
                if (_submeshMeshCache.TryGetValue(meshPath, out sharedId) && sharedId >= 0) return sharedId;
                if (_primitiveMeshCache.TryGetValue(meshPath, out sharedId) && sharedId >= 0) return sharedId;

                // Check the entity mesh cache first
                foreach (var kvp in _entityMeshes)
                {
                    if (_entityMeshPaths.TryGetValue(kvp.Key, out var path) && path == meshPath)
                    {
                        return kvp.Value;
                    }
                }
                
                // Return -1, bounds will be calculated later when mesh is loaded
                return -1;
            }
            catch
            {
                return -1;
            }
        }
        
        /// <summary>
        /// Render camera icons for all cameras in the scene (simplified icon for non-selected).
        /// </summary>
        private void RenderAllCameraIcons(Data.Scene scene, GameEntity selected)
        {
            if (!VortexAPI.AreGizmosVisible) return;
            
            foreach (var entity in scene.Entities)
            {
                RenderCameraIconRecursive(entity, selected);
            }
        }
        
        private void RenderCameraIconRecursive(GameEntity entity, GameEntity selected)
        {
            if (entity == null || !entity.IsActive || entity.IsHiddenInEditor) return;   // eye toggle hides the icon too
            // Skip the selected entity (it gets the full frustum gizmo)
            if (entity != selected)
            {
                var camera = entity.GetComponent<Camera>();
                if (camera != null)
                {
                    var pos = entity.Transform.LocalPosition;
                    var rot = entity.Transform.LocalRotation;
                    
                    // Render simple camera icon (just the body, no frustum)
                    VortexAPI.RenderCameraIcon(
                        pos.X, pos.Y, pos.Z,
                        rot.X, rot.Y, rot.Z,
                        camera.CameraType == CameraType.MainCamera);
                }
            }
            
            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                {
                    RenderCameraIconRecursive(child, selected);
                }
            }
        }

        private void SubmitEntityRecursive(GameEntity entity)
        {
            if (entity == null) return;

            // Editor eye toggle: a hidden entity takes its whole subtree out of the render.
            // Edit-mode only — during play the runtime activeSelf (SetActive) is in charge.
            if (entity.IsHiddenInEditor && !IsPlayLike) return;

            // activeSelf cascades: an inactive parent hides its children too (ActiveInHierarchy),
            // matching SubmitEntityLightsRecursive / SubmitColliderGizmosRecursive.
            if (!entity.IsActive) return;

            SubmitEntity(entity);

            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                {
                    SubmitEntityRecursive(child);
                }
            }
        }

        private long GetOrCreateMesh(Guid entityId, MeshRenderer renderer)
        {
            if (string.IsNullOrEmpty(renderer.MeshPath)) return -1;


            // Check if mesh path changed (dirty check)
            bool needsRecreate = false;
            if (_entityMeshPaths.TryGetValue(entityId, out string cachedPath))
            {
                if (cachedPath != renderer.MeshPath)
                {
                    // Path changed, need to recreate
                    if (_entityMeshes.TryGetValue(entityId, out long oldMesh))
                    {
                        if (!_sharedMeshIds.Contains(oldMesh)) VortexAPI.DeleteMesh(oldMesh);   // shared: other entities use it
                        _entityMeshes.Remove(entityId);
                    }
                    needsRecreate = true;
                }
            }
            else
            {
                needsRecreate = true;
            }

            // Check if we already have a valid mesh for this entity
            if (!needsRecreate && _entityMeshes.TryGetValue(entityId, out long existingMesh))
            {
                return existingMesh;
            }

            // Create new mesh based on path
            long meshId = CreateMeshFromPath(renderer.MeshPath);
            if (meshId >= 0)
            {
                _entityMeshes[entityId] = meshId;
                _entityMeshPaths[entityId] = renderer.MeshPath;
            }
            if (_meshDbg < 16) { _meshDbg++; try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vortex_mesh.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " path='" + renderer.MeshPath + "' id=" + meshId + " projPath='" + (Data.ProjectData.Current != null ? Data.ProjectData.Current.Path : "?") + "'\r\n"); } catch { } }

            return meshId;
        }

        /// <summary>The scene-wide shared mesh of a primitive kind ("Primitive:Sphere") for instancers such as the foliage (#125);
        /// -1 for anything else.</summary>
        public static long SharedPrimitiveMesh(string meshPath)
        {
            if (string.IsNullOrEmpty(meshPath) || !meshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) return -1;
            long shared;
            if (_primitiveMeshCache.TryGetValue(meshPath, out shared) && shared >= 0) return shared;
            long created = CreatePrimitiveMesh(meshPath.Substring("Primitive:".Length));
            if (created >= 0) { _primitiveMeshCache[meshPath] = created; _sharedMeshIds.Add(created); }
            return created;
        }

        private long CreateMeshFromPath(string meshPath)
        {
            if (meshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
            {
                // One shared GPU mesh per primitive kind for the whole scene: 8k cubes = 1 mesh, not 8k uploads.
                // Materials stay per entity, so colours are still individual. Shared ids are never deleted per entity.
                long shared;
                if (_primitiveMeshCache.TryGetValue(meshPath, out shared) && shared >= 0) return shared;
                long created = CreatePrimitiveMesh(meshPath.Substring("Primitive:".Length));
                if (created >= 0) { _primitiveMeshCache[meshPath] = created; _sharedMeshIds.Add(created); }
                return created;
            }

            // Load mesh from external file
            return LoadMeshFromFile(meshPath);
        }

        /// <summary>The engine mesh for a primitive kind ("cube", "sphere", …); -1 for an unknown kind.</summary>
        private static long CreatePrimitiveMesh(string primitiveType)
        {
            switch (primitiveType.ToLower())
            {
                case "cube":
                    return VortexAPI.CreateCubeMesh(1.0f);
                case "sphere":
                    return VortexAPI.CreateSphereMesh(0.5f);
                case "plane":
                    return VortexAPI.CreatePlaneMesh(1.0f, 1.0f);
                case "cylinder":
                    return VortexAPI.CreateCylinderMesh(0.5f, 1.0f);
                case "capsule":
                    // Capsule approximated with cylinder for now
                    return VortexAPI.CreateCylinderMesh(0.5f, 1.0f);
                case "cone":
                    // Cone approximated with cylinder for now
                    return VortexAPI.CreateCylinderMesh(0.5f, 1.0f);
                case "torus":
                    // Torus approximated with sphere for now
                    return VortexAPI.CreateSphereMesh(0.5f);
                case "quad":
                    return VortexAPI.CreatePlaneMesh(1.0f, 1.0f);
                default:
                    return -1;
            }
        }

        // Cache for submesh mesh IDs (keyed by submesh path like "path#submesh0")
        private static readonly Dictionary<string, long> _submeshMeshCache = new Dictionary<string, long>();

        private long LoadMeshFromFile(string meshPath)
        {
            if (string.IsNullOrEmpty(meshPath))
                return -1;

            try
            {
                // Check if this is a submesh path (format: "path#submeshN")
                string actualPath = meshPath;
                int submeshIndex = -1;
                
                int hashIndex = meshPath.LastIndexOf('#');
                if (hashIndex > 0 && meshPath.Length > hashIndex + 7 && meshPath.Substring(hashIndex + 1, 7) == "submesh")
                {
                    actualPath = meshPath.Substring(0, hashIndex);
                    if (int.TryParse(meshPath.Substring(hashIndex + 8), out int idx))
                    {
                        submeshIndex = idx;
                    }
                }

                // Check submesh cache first (most common path for already-imported models)
                if (_submeshMeshCache.TryGetValue(meshPath, out long cachedMeshId))
                {
                    Log($"[SceneRenderService] Using cached mesh {cachedMeshId} for {meshPath}");
                    return cachedMeshId;
                }

                // Get full path
                var projectPath = Data.ProjectData.Current?.Path;
                string fullPath = actualPath;
                
                // If it's a relative path, combine with project path
                if (!System.IO.Path.IsPathRooted(actualPath) && !string.IsNullOrEmpty(projectPath))
                {
                    fullPath = System.IO.Path.Combine(projectPath, actualPath);
                }

                if (!AssetVfs.Exists(fullPath))
                {
                    return -1;
                }

                var extension = System.IO.Path.GetExtension(fullPath)?.ToLowerInvariant();

                // Shipped game: the bytes live in the in-RAM pak, not on disk.
                byte[] vfsBytes = null;
                bool fromVfs = AssetVfs.IsMounted && AssetVfs.TryGetBytes(fullPath, out vfsBytes);

                // Check if it's a .vmesh file (binary format - fast load)
                if (extension == ".vmesh")
                {
                    if (fromVfs)
                    {
                        // Shipped game: the native .vmesh loader is disk-path only (no bytes overload). Spill the
                        // packed bytes to a per-run temp file (mirrors AudioPlaybackService's container handling) so
                        // packed .vmesh meshes still load — otherwise a Release build renders them as missing geometry.
                        try
                        {
                            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VortexVMesh",
                                (uint)fullPath.ToLowerInvariant().GetHashCode() + "_" + vfsBytes.Length + ".vmesh");
                            if (!System.IO.File.Exists(tmp))
                            {
                                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(tmp));
                                System.IO.File.WriteAllBytes(tmp, vfsBytes);
                            }
                            return VortexAPI.LoadVMeshFromFile(tmp);
                        }
                        catch { return -1; }
                    }
                    return VortexAPI.LoadVMeshFromFile(fullPath);
                }

                // For model files (FBX, OBJ, etc.) - use multi-material import
                if (IsModelFileExtension(extension))
                {
                    if (!fromVfs && !VortexAPI.IsAssimpAvailable())
                    {
                        return -1;
                    }

                    // the render-side import cache (#364 C): after the first import the engine keeps one .vmesh per
                    // submesh plus the material records under <project>/.ve/cache/models; later starts load the model
                    // from there without Assimp. A shipped game (pak) never caches.
                    string cacheDir = fromVfs ? null : ModelImportCache.Dir(projectPath, fullPath);
                    int cachedCount = ModelImportCache.Count(cacheDir);
                    VortexAPI.SubmeshImportData[] submeshes = null;
                    bool fromCache = false;
                    if (cachedCount > 0)
                    {
                        var swCache = System.Diagnostics.Stopwatch.StartNew();
                        var cached = VortexAPI.ImportModelFromCacheDir(cacheDir);
                        swCache.Stop();
                        if (cached != null && cached.Length == cachedCount)
                        {
                            submeshes = cached;
                            fromCache = true;
                            try { ConsoleService.Instance.LogSystem("Model cache " + System.IO.Path.GetFileName(actualPath) + ": " + cachedCount + " submeshes in " + swCache.ElapsedMilliseconds + " ms (no Assimp)"); } catch { }
                        }
                        else { try { System.IO.Directory.Delete(cacheDir, true); } catch { } }   // stale or broken: re-import below
                    }

                    string writeDir = null;
                    int cachedWritten = 0;
                    if (submeshes == null)
                    {
                        // Import with materials (this creates all submeshes at once) — from RAM if packed, else disk.
                        Log($"[SceneRenderService] Importing model with materials: {fullPath} (vfs={fromVfs})");
                        var virtualDir = (System.IO.Path.GetDirectoryName(actualPath) ?? "").Replace('\\', '/');
                        var swImport = System.Diagnostics.Stopwatch.StartNew();
                        // the import itself writes the cache (meshes + material records); a skinned model writes
                        // nothing (no bone weights in .vmesh) and keeps going through Assimp
                        writeDir = cacheDir;
                        submeshes = fromVfs
                            ? VortexAPI.ImportModelFromBytes(vfsBytes, extension.TrimStart('.'), virtualDir, ModelImportSettings.LoadLeftHanded(fullPath))
                            : VortexAPI.ImportModelWithMaterialsFromFile(fullPath, writeDir, out cachedWritten);
                        swImport.Stop();
                        // make the load cost visible: this runs on every start for a model that could not be cached (#364 C)
                        if (swImport.ElapsedMilliseconds > 50)
                            try { ConsoleService.Instance.LogSystem("Model import " + System.IO.Path.GetFileName(actualPath) + ": " + (submeshes != null ? submeshes.Length : 0) + " submeshes in " + swImport.ElapsedMilliseconds + " ms (Assimp)"); } catch { }
                    }
                    if (submeshes != null && submeshes.Length > 0)
                    {
                        // Cache all submeshes for future use
                        for (int i = 0; i < submeshes.Length; i++)
                        {
                            string subPath = $"{actualPath}#submesh{i}";
                            _submeshMeshCache[subPath] = submeshes[i].MeshId;
                            _sharedMeshIds.Add(submeshes[i].MeshId);
                            
                            // Also register materials
                            if (submeshes[i].MaterialId >= 0)
                            {
                                RegisterMaterialForMeshPath(subPath, submeshes[i].MaterialId);
                            }
                        }
                        
                        // Also cache the base path with first mesh
                        _submeshMeshCache[actualPath] = submeshes[0].MeshId;
                        _sharedMeshIds.Add(submeshes[0].MeshId);
                        _resolvedSubmeshes.Clear();   // the per-path submesh sets pick the new entries up next submit

                        // finish the import cache for the next start (#364 C): the manifest is written last and only
                        // when the engine wrote every submesh file and the material records; anything else (skinned
                        // model, failed write, old engine) leaves no cache behind
                        if (!fromCache && writeDir != null)
                        {
                            bool complete = cachedWritten == submeshes.Length && System.IO.File.Exists(ModelImportCache.Materials(writeDir));
                            for (int i = 0; i < submeshes.Length && complete; i++)
                                if (!System.IO.File.Exists(ModelImportCache.SubmeshFile(writeDir, i))) complete = false;
                            try
                            {
                                if (complete)
                                {
                                    ModelImportCache.WriteManifest(writeDir, submeshes.Length);
                                    try { ConsoleService.Instance.LogSystem("Model cache written: " + System.IO.Path.GetFileName(actualPath) + " (" + submeshes.Length + " submeshes) — the next start skips Assimp"); } catch { }
                                }
                                else if (System.IO.Directory.Exists(writeDir)) System.IO.Directory.Delete(writeDir, true);
                            }
                            catch { }
                        }
                        
                        // Return requested submesh or first mesh
                        if (submeshIndex >= 0 && submeshIndex < submeshes.Length)
                        {
                            return submeshes[submeshIndex].MeshId;
                        }
                        return submeshes[0].MeshId;
                    }
                    return -1;
                }

                return -1;
            }
            catch (Exception ex)
            {
                Log($"[SceneRenderService] Error loading mesh: {ex.Message}");
                return -1;
            }
        }

        private static bool IsModelFileExtension(string extension)
        {
            return extension switch
            {
                ".fbx" or ".obj" or ".gltf" or ".glb" or ".dae" or ".3ds" or ".blend" => true,
                _ => false
            };
        }

        // ---- render-side model import cache (#364 C) -------------------------------------------------------------
        // A model that is not a .vmesh goes through Assimp on EVERY start. During the first import the engine writes
        // its submeshes as .vmesh files plus their material records (materials.vmc: colour, PBR factors, texture
        // paths, channels) under <project>/.ve/cache/models/<sha1(path|mtime|size)>/; the manifest comes last, from
        // here, so a half-written cache is never used. Later starts load the model from there through the same
        // material setup as the import, without Assimp. Never for skinned models (the .vmesh format carries no bone
        // weights) and never in a shipped game. An overwritten model gets a new key by itself.
        internal static class ModelImportCache
        {
            public static string Dir(string projectPath, string absModelPath)
            {
                if (string.IsNullOrEmpty(projectPath) || string.IsNullOrEmpty(absModelPath)) return null;
                try
                {
                    var fi = new System.IO.FileInfo(absModelPath);
                    if (!fi.Exists) return null;
                    // "|v2": glTF node transforms are baked since #338 — caches written before are ignored
                    string key = absModelPath.ToLowerInvariant() + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length + "|v2|" + ModelImportSettings.SidecarSignature(absModelPath);
                    using (var sha = System.Security.Cryptography.SHA1.Create())
                    {
                        var h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key));
                        var sb = new System.Text.StringBuilder(40);
                        foreach (var b in h) sb.Append(b.ToString("x2"));
                        return System.IO.Path.Combine(projectPath, ".ve", "cache", "models", sb.ToString());
                    }
                }
                catch { return null; }
            }

            public static string Manifest(string dir) => System.IO.Path.Combine(dir, "manifest.txt");
            public static string SubmeshFile(string dir, int i) => System.IO.Path.Combine(dir, "submesh_" + i + ".vmesh");
            public static string Materials(string dir) => System.IO.Path.Combine(dir, "materials.vmc");

            /// <summary>The cached submesh count, or -1 when <paramref name="dir"/> holds no complete cache.</summary>
            public static int Count(string dir)
            {
                try
                {
                    if (dir == null || !System.IO.File.Exists(Manifest(dir))) return -1;
                    var lines = System.IO.File.ReadAllLines(Manifest(dir));
                    int n;
                    if (lines.Length < 2 || lines[0] != "vortex-model-cache 1" || !int.TryParse(lines[1], out n) || n <= 0) return -1;
                    if (!System.IO.File.Exists(Materials(dir))) return -1;
                    for (int i = 0; i < n; i++) if (!System.IO.File.Exists(SubmeshFile(dir, i))) return -1;
                    return n;
                }
                catch { return -1; }
            }

            /// <summary>Write the manifest last (atomically), so a half-written cache is never used.</summary>
            public static void WriteManifest(string dir, int count)
            {
                System.IO.Directory.CreateDirectory(dir);
                string tmp = Manifest(dir) + ".tmp";
                System.IO.File.WriteAllText(tmp, "vortex-model-cache 1\n" + count + "\n");
                if (System.IO.File.Exists(Manifest(dir))) System.IO.File.Delete(Manifest(dir));
                System.IO.File.Move(tmp, Manifest(dir));
            }
        }

        /// <summary>Load a texture by path — from the in-RAM asset pak (shipped game) or from disk (editor).</summary>
        private static long ImportTexturePath(string fullPath)
        {
            if (AssetVfs.IsMounted && AssetVfs.TryGetBytes(fullPath, out var bytes))
                return VortexAPI.ImportTextureFromBytes(bytes);
            return VortexAPI.ImportTextureFromFile(fullPath);
        }

        private long GetOrCreateMaterial(Guid entityId, MeshRenderer renderer)
        {
            // Highest precedence: an explicitly assigned .vmat material. Build it FULLY (all PBR
            // scalars + every texture map, not just base color) via MaterialService, which owns the
            // engine material and shares one instance across every entity referencing the same file.
            // Deliberately NOT stored in _entityMaterials — those get DeleteMaterial'd per entity on
            // cleanup, which would free a shared material out from under other meshes.
            if (!string.IsNullOrEmpty(renderer.MaterialPath))
            {
                // Fast path: already resolved this .vmat -> skip the per-frame filesystem check + rebuild.
                if (_vmatPathCache.TryGetValue(renderer.MaterialPath, out long fastMat) && fastMat >= 0)
                    return fastMat;

                string vmatPath = renderer.MaterialPath;
                if (!System.IO.Path.IsPathRooted(vmatPath))
                {
                    var projectPath = Data.ProjectData.Current?.Path;
                    if (!string.IsNullOrEmpty(projectPath))
                        vmatPath = System.IO.Path.Combine(projectPath, vmatPath);
                }

                if (AssetVfs.Exists(vmatPath))
                {
                    long vmatMaterial = MaterialService.Instance.GetOrBuildVortexMaterial(vmatPath);
                    if (vmatMaterial >= 0)
                    {
                        _vmatPathCache[renderer.MaterialPath] = vmatMaterial;   // cache for every subsequent frame
                        return vmatMaterial;
                    }
                }
            }

            // First, check if we have a cached material for this mesh path. Never for primitives: "Primitive:Cube"
            // is shared by every box in the scene, so a material registered under it would recolour all of them.
            string meshPath = renderer.MeshPath;
            bool primitive = IsPrimitivePath(meshPath);
            long cachedMaterial = primitive ? -1 : GetMaterialForMeshPath(meshPath);
            
            if (cachedMaterial >= 0)
            {
                // Use the cached material with textures
                if (!_entityMaterials.ContainsKey(entityId))
                {
                    _entityMaterials[entityId] = cachedMaterial;
                }
                return cachedMaterial;
            }

            // Fallback: Check if the renderer has an imported material directly. Not when a .vmat is assigned: then
            // MaterialHandle is the resource manager's handle for that file (LoadMaterialResource), NOT a graphics
            // material id — an unresolvable .vmat used to send that number to the renderer (a random other material).
            if (renderer.HasImportedMaterial && string.IsNullOrEmpty(renderer.MaterialPath))
            {
                if (!_entityMaterials.ContainsKey(entityId))
                {
                    _entityMaterials[entityId] = renderer.MaterialHandle;
                }
                // Register in cache for future lookups
                if (!string.IsNullOrEmpty(meshPath) && !primitive)
                {
                    RegisterMaterialForMeshPath(meshPath, renderer.MaterialHandle);
                }
                return renderer.MaterialHandle;
            }

            // Check if renderer has a texture path but no cached material (e.g., after restart)
            if (!string.IsNullOrEmpty(renderer.TexturePath) && !_entityMaterials.ContainsKey(entityId))
            {
                var projectPath = Data.ProjectData.Current?.Path;
                string fullTexturePath = renderer.TexturePath;
                
                if (!System.IO.Path.IsPathRooted(renderer.TexturePath) && !string.IsNullOrEmpty(projectPath))
                {
                    fullTexturePath = System.IO.Path.Combine(projectPath, renderer.TexturePath);
                }

                if (AssetVfs.Exists(fullTexturePath))
                {
                    try
                    {
                        // Import texture and create material
                        long textureId = ImportTexturePath(fullTexturePath);
                        if (textureId >= 0)
                        {
                            long newMaterialId = VortexAPI.CreateNewMaterial();
                            if (newMaterialId >= 0)
                            {
                                // white: the base colour now TINTS the albedo texture (#330), 0.9 would darken every
                                // textured model by 10 %
                                VortexAPI.SetMaterialBaseColor(newMaterialId, 1.0f, 1.0f, 1.0f, 1.0f);
                                VortexAPI.SetMaterialMetallicValue(newMaterialId, renderer.Metallic);
                                VortexAPI.SetMaterialRoughnessValue(newMaterialId, renderer.Roughness);
                                VortexAPI.SetMaterialAlbedoTexture(newMaterialId, textureId);
                                _entityMaterials[entityId] = newMaterialId;
                                
                                // Register in cache for future lookups (models only — see IsPrimitivePath)
                                if (!string.IsNullOrEmpty(meshPath) && !primitive)
                                {
                                    RegisterMaterialForMeshPath(meshPath, newMaterialId);
                                }
                                
                                Log($"[SceneRenderService] Created material with texture for {meshPath}: {fullTexturePath}");
                                return newMaterialId;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[SceneRenderService] Error loading texture: {ex.Message}");
                    }
                }
            }

            // Plain look (primitives, single meshes without a material): a SHARED native material per look (#364 D).
            // The material is WHITE — the colour rides with the instance (#331), so every plain primitive of one
            // metallic/roughness shares one material whatever its colour (8k cubes in 8k colours = one draw run).
            var key = new MaterialKey
            {
                R = 1f, G = 1f, B = 1f, A = 1f,
                Metallic = renderer.Metallic, Roughness = renderer.Roughness, Texture = -1,
            };
            long existingMaterial;
            if (_entityMaterials.TryGetValue(entityId, out existingMaterial) && existingMaterial >= 0)
            {
                MaterialKey oldKey;
                if (_entityMaterialKey.TryGetValue(entityId, out oldKey) && oldKey.Equals(key)) return existingMaterial;
                if (!_sharedMaterialIds.Contains(existingMaterial))
                {
                    // this entity's own (textured) material stays white — the colour is the instance tint (#331)
                    _entityMaterialKey[entityId] = key;
                    _entityMaterialColors[entityId] = (renderer.ColorR, renderer.ColorG, renderer.ColorB, renderer.ColorA);
                    return existingMaterial;
                }
                // a shared material: the look changed, so switch to the shared material of the NEW look (copy-on-write)
            }
            long shared = GetOrCreatePlainMaterial(key);
            if (shared >= 0)
            {
                _entityMaterials[entityId] = shared;
                _entityMaterialKey[entityId] = key;
                _entityMaterialColors[entityId] = (renderer.ColorR, renderer.ColorG, renderer.ColorB, renderer.ColorA);
            }
            return shared;
        }

        private float[] BuildWorldMatrix(Transform transform)
        {
            var pos = transform.LocalPosition;
            var rot = transform.LocalRotation; // Rotation in degrees (Euler angles)
            var scale = transform.LocalScale;

            // Convert degrees to radians
            float radX = rot.X * (float)(Math.PI / 180.0);
            float radY = rot.Y * (float)(Math.PI / 180.0);
            float radZ = rot.Z * (float)(Math.PI / 180.0);

            // Pre-calculate sin and cos
            float cosX = (float)Math.Cos(radX), sinX = (float)Math.Sin(radX);
            float cosY = (float)Math.Cos(radY), sinY = (float)Math.Sin(radY);
            float cosZ = (float)Math.Cos(radZ), sinZ = (float)Math.Sin(radZ);

            // Build rotation matrix (ZXY order for Unity-like behavior)
            // R = Rz * Rx * Ry
            float r00 = cosZ * cosY + sinZ * sinX * sinY;
            float r01 = sinZ * cosX;
            float r02 = -cosZ * sinY + sinZ * sinX * cosY;

            float r10 = -sinZ * cosY + cosZ * sinX * sinY;
            float r11 = cosZ * cosX;
            float r12 = sinZ * sinY + cosZ * sinX * cosY;

            float r20 = cosX * sinY;
            float r21 = -sinX;
            float r22 = cosX * cosY;

            // Combine with scale: S * R (scale first, then rotate)
            // Final matrix: Scale * Rotation * Translation (in column-major terms)
            // For row-major DirectX: Transpose the rotation part
            return new float[]
            {
                scale.X * r00, scale.X * r01, scale.X * r02, 0,
                scale.Y * r10, scale.Y * r11, scale.Y * r12, 0,
                scale.Z * r20, scale.Z * r21, scale.Z * r22, 0,
                pos.X,         pos.Y,         pos.Z,         1
            };
        }

        /// <summary>
        /// Build world matrix including parent transformations.
        /// This allows child entities to inherit transforms from their parent.
        /// </summary>
        private float[] BuildWorldMatrixWithParent(GameEntity entity)
        {
            if (entity == null || entity.Transform == null)
                return BuildIdentityMatrix();

            // Get local matrix
            float[] localMatrix = BuildWorldMatrix(entity.Transform);

            // If no parent, return local matrix
            if (entity.Parent == null || entity.Parent.Transform == null)
                return localMatrix;

            // Get parent world matrix (recursive)
            float[] parentMatrix = BuildWorldMatrixWithParent(entity.Parent);

            // Multiply: local * parent (row-major order)
            return MultiplyMatrices(localMatrix, parentMatrix);
        }

        // ---- allocation-free world matrices (the renderer copies the 16 floats during the submit call) -----------

        /// <summary>World matrix of <paramref name="entity"/> (local × parent chain) into <paramref name="dst"/>.
        /// <paramref name="depth"/> selects the scratch buffers so the recursion never aliases a buffer in use.</summary>
        private void BuildWorldMatrixInto(GameEntity entity, float[] dst, int depth)
        {
            if (entity == null || entity.Transform == null) { IdentityInto(dst); return; }
            if (depth >= _localPool.Length) { Array.Resize(ref _localPool, depth * 2); Array.Resize(ref _worldPool, depth * 2); }
            var local = _localPool[depth] ?? (_localPool[depth] = new float[16]);
            LocalMatrixInto(entity.Transform, local);
            if (entity.Parent == null || entity.Parent.Transform == null) { Array.Copy(local, dst, 16); return; }
            var parentWorld = _worldPool[depth] ?? (_worldPool[depth] = new float[16]);
            BuildWorldMatrixInto(entity.Parent, parentWorld, depth + 1);
            MultiplyInto(local, parentWorld, dst);
        }

        internal static void IdentityInto(float[] m)
        {
            Array.Clear(m, 0, 16);
            m[0] = m[5] = m[10] = m[15] = 1f;
        }

        /// <summary>Same math as <see cref="BuildWorldMatrix"/> (S·R(ZXY)·T, row-major), written into <paramref name="m"/>.</summary>
        internal static void LocalMatrixInto(Transform transform, float[] m)
        {
            var pos = transform.LocalPosition;
            var rot = transform.LocalRotation;
            var scale = transform.LocalScale;
            float radX = rot.X * (float)(Math.PI / 180.0), radY = rot.Y * (float)(Math.PI / 180.0), radZ = rot.Z * (float)(Math.PI / 180.0);
            float cosX = (float)Math.Cos(radX), sinX = (float)Math.Sin(radX);
            float cosY = (float)Math.Cos(radY), sinY = (float)Math.Sin(radY);
            float cosZ = (float)Math.Cos(radZ), sinZ = (float)Math.Sin(radZ);
            float r00 = cosZ * cosY + sinZ * sinX * sinY, r01 = sinZ * cosX, r02 = -cosZ * sinY + sinZ * sinX * cosY;
            float r10 = -sinZ * cosY + cosZ * sinX * sinY, r11 = cosZ * cosX, r12 = sinZ * sinY + cosZ * sinX * cosY;
            float r20 = cosX * sinY, r21 = -sinX, r22 = cosX * cosY;
            m[0] = scale.X * r00; m[1] = scale.X * r01; m[2] = scale.X * r02; m[3] = 0;
            m[4] = scale.Y * r10; m[5] = scale.Y * r11; m[6] = scale.Y * r12; m[7] = 0;
            m[8] = scale.Z * r20; m[9] = scale.Z * r21; m[10] = scale.Z * r22; m[11] = 0;
            m[12] = pos.X; m[13] = pos.Y; m[14] = pos.Z; m[15] = 1;
        }

        /// <summary>dst = a × b (row-major); <paramref name="dst"/> must not be <paramref name="a"/> or <paramref name="b"/>.</summary>
        internal static void MultiplyInto(float[] a, float[] b, float[] dst)
        {
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 4; col++)
                    dst[row * 4 + col] = a[row * 4 + 0] * b[col] + a[row * 4 + 1] * b[4 + col] + a[row * 4 + 2] * b[8 + col] + a[row * 4 + 3] * b[12 + col];
        }

        private float[] BuildIdentityMatrix()
        {
            return new float[]
            {
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, 0,
                0, 0, 0, 1
            };
        }

        private float[] MultiplyMatrices(float[] a, float[] b)
        {
            float[] result = new float[16];
            
            for (int row = 0; row < 4; row++)
            {
                for (int col = 0; col < 4; col++)
                {
                    result[row * 4 + col] = 
                        a[row * 4 + 0] * b[0 * 4 + col] +
                        a[row * 4 + 1] * b[1 * 4 + col] +
                        a[row * 4 + 2] * b[2 * 4 + col] +
                        a[row * 4 + 3] * b[3 * 4 + col];
                }
            }
            
            return result;
        }

        /// <summary>
        /// Set an entity's base color at runtime (used by scripts — e.g. change color when a trigger is touched).
        /// Updates the C# MeshRenderer color and pushes it to the engine material immediately so it shows this
        /// frame, even under submit-once (the material is referenced by id, so changing its color is live).
        /// </summary>
        public void SetEntityColor(GameEntity entity, float r, float g, float b, float a = 1f)
        {
            if (entity == null) return;
            var mr = entity.GetComponent<MeshRenderer>();
            if (mr != null) { mr.ColorR = r; mr.ColorG = g; mr.ColorB = b; mr.ColorA = a; }
            // The colour is a per-instance tint (#331): it rides with the next submit of THIS entity and touches no
            // material — a shared .vmat / import / plain material is never recoloured for the other instances.
            _entityMaterialColors[entity.Id] = (r, g, b, a);
            RuntimeDirty = true;
            StaticDirty = true;   // a static entity's tint lives in the retained static set
        }

        /// <summary>
        /// Notify that an entity's mesh has changed.
        /// </summary>
        public void OnMeshChanged(Guid entityId)
        {
            // Remove old mesh so it gets recreated (a shared model/primitive mesh stays — other entities use it)
            if (_entityMeshes.TryGetValue(entityId, out long meshId))
            {
                if (!_sharedMeshIds.Contains(meshId)) VortexAPI.DeleteMesh(meshId);
                _entityMeshes.Remove(entityId);
            }
        }

        /// <summary>
        /// Notify that an entity's camera properties have changed.
        /// </summary>
        public void OnCameraChanged(Guid entityId)
        {
            // Fire event so viewport can update camera view if previewing this camera
            CameraPropertiesChanged?.Invoke(this, entityId);
        }

        /// <summary>
        /// Event fired when camera properties are modified.
        /// </summary>
        public event EventHandler<Guid> CameraPropertiesChanged;

        /// <summary>An entity left the scene (deleted, destroyed at runtime, undone): free what the renderer created for
        /// it and its children. Shared meshes and materials (models, primitives, plain looks) stay for the others (#358).</summary>
        public void RemoveEntityTree(GameEntity e)
        {
            if (e == null) return;
            try { RemoveEntity(e.Id); } catch { }
            if (e.Children != null) for (int i = 0; i < e.Children.Count; i++) RemoveEntityTree(e.Children[i]);
        }

        /// <summary>
        /// Remove an entity from the render system.
        /// </summary>
        public void RemoveEntity(Guid entityId)
        {
            if (_entityMeshes.TryGetValue(entityId, out long meshId))
            {
                if (!_sharedMeshIds.Contains(meshId)) VortexAPI.DeleteMesh(meshId);   // shared: other entities use it
                _entityMeshes.Remove(entityId);
            }

            if (_entityMaterials.TryGetValue(entityId, out long materialId))
            {
                if (!_sharedMaterialIds.Contains(materialId)) VortexAPI.DeleteMaterial(materialId);   // shared: others use it
                _entityMaterials.Remove(entityId);
            }

            _entityMeshPaths.Remove(entityId);
            _entityMaterialColors.Remove(entityId);
            _entityMaterialKey.Remove(entityId);
            
            // Also remove camera if exists
            RemoveEntityCamera(entityId);
        }

        /// <summary>
        /// Clear all renderables.
        /// </summary>
        public void ClearAllRenderables()
        {
            // CRITICAL: imported MODEL meshes are SHARED + owned by _submeshMeshCache (loaded ONCE from a file,
            // reused by every entity that references the same path — incl. all Ctrl+D duplicates — and kept
            // resident across scene reloads so re-loading never re-imports). _entityMeshes maps MANY entities to
            // the SAME shared mesh id, so deleting per-entry called DeleteMesh up to 50x ON ONE id (double-free
            // -> native registry corruption + leaks that made repeated loads exponentially slower, and a 4-min
            // startup with 50 copies). Here: delete ONLY non-shared (e.g. primitive) meshes, each id at most once,
            // and NEVER delete a shared cached model mesh — it stays loaded for reuse.
            var sharedMeshIds = new HashSet<long>(_submeshMeshCache.Values);
            sharedMeshIds.UnionWith(_sharedMeshIds);   // + shared primitives (also kept resident for reuse)
            var alreadyDeleted = new HashSet<long>();
            foreach (var meshId in _entityMeshes.Values)
            {
                if (meshId < 0 || sharedMeshIds.Contains(meshId)) continue;  // shared model mesh -> keep resident
                if (!alreadyDeleted.Add(meshId)) continue;                   // delete each unique id only once
                VortexAPI.DeleteMesh(meshId);
            }
            _entityMeshes.Clear();

            // delete only this entity's own materials, each id once; shared ones (plain looks, import materials) stay
            // resident for reuse — deleting them per entity was a double free
            var deletedMaterials = new HashSet<long>();
            foreach (var materialId in _entityMaterials.Values)
            {
                if (materialId < 0 || _sharedMaterialIds.Contains(materialId) || !deletedMaterials.Add(materialId)) continue;
                VortexAPI.DeleteMaterial(materialId);
            }
            _entityMaterials.Clear();
            _entityMaterialKey.Clear();

            _entityMeshPaths.Clear();
            _entityMaterialColors.Clear();
            
            // Clear cameras
            foreach (var handle in _entityCameras.Values)
            {
                VortexAPI.DestroyEngineCamera(handle);
            }
            _entityCameras.Clear();
        }

        #region Camera Management

        private readonly Dictionary<Guid, CameraHandle> _entityCameras = new Dictionary<Guid, CameraHandle>();
        private CameraHandle _previewCamera = CameraHandle.Invalid;
        private bool _isPreviewingCamera;

        /// <summary>
        /// Create or update an engine camera for an entity.
        /// </summary>
        public CameraHandle GetOrCreateEntityCamera(Guid entityId, Camera cameraComponent, Transform transform)
        {
            if (_entityCameras.TryGetValue(entityId, out var existingHandle))
            {
                // Update existing camera
                UpdateEngineCamera(existingHandle, cameraComponent, transform);
                return existingHandle;
            }

            // Create new engine camera
            var desc = new CameraDescriptor
            {
                Position = new float[] { transform.LocalPosition.X, transform.LocalPosition.Y, transform.LocalPosition.Z },
                Rotation = EulerToQuaternion(transform.LocalRotation.X, transform.LocalRotation.Y, transform.LocalRotation.Z),
                Projection = (byte)cameraComponent.Projection,
                FieldOfView = cameraComponent.FieldOfView,
                OrthographicSize = cameraComponent.OrthographicSize,
                NearClip = cameraComponent.NearClip,
                FarClip = cameraComponent.FarClip,
                AspectRatio = 16f / 9f,
                ClearFlags = (byte)cameraComponent.ClearFlags,
                BackgroundColor = new float[] { cameraComponent.BackgroundR, cameraComponent.BackgroundG, cameraComponent.BackgroundB, 1f },
                Depth = cameraComponent.Depth,
                CullingMask = cameraComponent.CullingMask,
                CameraType = (byte)cameraComponent.CameraType,
                IsEnabled = true
            };

            var handle = VortexAPI.CreateEngineCamera(desc);
            if (handle.IsValid)
            {
                _entityCameras[entityId] = handle;
            }
            return handle;
        }

        /// <summary>
        /// Update an existing engine camera with new properties.
        /// </summary>
        private void UpdateEngineCamera(CameraHandle handle, Camera camera, Transform transform)
        {
            if (!handle.IsValid) return;

            VortexAPI.SetEngineCameraPosition(handle, 
                transform.LocalPosition.X, transform.LocalPosition.Y, transform.LocalPosition.Z);
            
            var quat = EulerToQuaternion(transform.LocalRotation.X, transform.LocalRotation.Y, transform.LocalRotation.Z);
            VortexAPI.SetEngineCameraRotation(handle, quat[0], quat[1], quat[2], quat[3]);
            
            VortexAPI.SetEngineCameraFOV(handle, camera.FieldOfView);
            VortexAPI.SetEngineCameraClipPlanes(handle, camera.NearClip, camera.FarClip);
            VortexAPI.SetEngineCameraProjection(handle, (CameraProjectionType)camera.Projection);
            VortexAPI.SetEngineCameraType(handle, (CameraTypeEnum)camera.CameraType);
            VortexAPI.SetEngineCameraBackgroundColor(handle, 
                camera.BackgroundR, camera.BackgroundG, camera.BackgroundB, 1f);
            VortexAPI.SetEngineCameraDepth(handle, camera.Depth);
        }

        /// <summary>
        /// Remove an entity's camera.
        /// </summary>
        public void RemoveEntityCamera(Guid entityId)
        {
            if (_entityCameras.TryGetValue(entityId, out var handle))
            {
                VortexAPI.DestroyEngineCamera(handle);
                _entityCameras.Remove(entityId);
            }
        }

        /// <summary>
        /// Get the engine camera handle for an entity.
        /// </summary>
        public CameraHandle GetEntityCamera(Guid entityId)
        {
            return _entityCameras.TryGetValue(entityId, out var handle) ? handle : CameraHandle.Invalid;
        }

        /// <summary>
        /// Render camera gizmo for a selected camera entity.
        /// </summary>
        public void RenderCameraGizmo(Guid entityId, bool isMainCamera)
        {
            if (!_entityCameras.TryGetValue(entityId, out var handle)) return;
            
            // Main camera = purple, other cameras = blue
            if (isMainCamera)
            {
                VortexAPI.RenderEngineCameraGizmo(handle, 0.608f, 0.349f, 0.714f); // Purple #9B59B6
            }
            else
            {
                VortexAPI.RenderEngineCameraGizmo(handle, 0.337f, 0.612f, 0.839f); // Blue #569CD6
            }
        }

        /// <summary>
        /// Start previewing a camera's view in the viewport.
        /// </summary>
        public void StartCameraPreview(CameraHandle camera)
        {
            _previewCamera = camera;
            _isPreviewingCamera = true;
        }

        /// <summary>
        /// Stop camera preview and return to editor camera.
        /// </summary>
        public void StopCameraPreview()
        {
            _previewCamera = CameraHandle.Invalid;
            _isPreviewingCamera = false;
        }

        /// <summary>
        /// Check if currently previewing a camera.
        /// </summary>
        public bool IsPreviewingCamera => _isPreviewingCamera;

        /// <summary>
        /// Get the camera being previewed.
        /// </summary>
        public CameraHandle PreviewCamera => _previewCamera;

        /// <summary>
        /// Apply the preview camera to the renderer (call during render loop).
        /// </summary>
        public void ApplyPreviewCameraIfActive()
        {
            if (_isPreviewingCamera && _previewCamera.IsValid)
            {
                VortexAPI.ApplyEngineCameraToRenderer(_previewCamera);
            }
        }

        /// <summary>
        /// Convert Euler angles (degrees) to quaternion.
        /// </summary>
        private float[] EulerToQuaternion(float pitch, float yaw, float roll)
        {
            // Convert to radians
            double p = pitch * Math.PI / 180.0 * 0.5;
            double y = yaw * Math.PI / 180.0 * 0.5;
            double r = roll * Math.PI / 180.0 * 0.5;

            double sinP = Math.Sin(p), cosP = Math.Cos(p);
            double sinY = Math.Sin(y), cosY = Math.Cos(y);
            double sinR = Math.Sin(r), cosR = Math.Cos(r);

            return new float[]
            {
                (float)(cosR * sinP * cosY + sinR * cosP * sinY), // X
                (float)(cosR * cosP * sinY - sinR * sinP * cosY), // Y
                (float)(sinR * cosP * cosY - cosR * sinP * sinY), // Z
                (float)(cosR * cosP * cosY + sinR * sinP * sinY)  // W
            };
        }

        #endregion

        #region Light Management

        private bool _hasSceneLights = false;
        private bool _hasDirectionalLight = false;
        // the sun as last submitted (the sky light's ground bounce reads it): colour × intensity and the direction's y
        private float _sunR = 1f, _sunG = 1f, _sunB = 1f, _sunIntensity = 0f, _sunDirY = -1f;
        /// <summary>With <see cref="DebugThirdPersonView"/>: render the first-person viewmodel (layer 1) as world
        /// geometry and hide the third-person body — for inspecting the FP arms + weapon from outside
        /// (VM_DBGCAM_FP=1 in the standalone player).</summary>
        public static bool DebugShowViewmodel;

        private bool _hasSkybox = false;

        /// <summary>Ambient strength a game SCRIPT set via Vortex.Lighting.SetAmbient during play. While set,
        /// scene re-submits must NOT stomp it with the editor defaults below — the shipped-game loop re-submits
        /// after every scripted light change, so the default (0.35) used to win the tug-of-war every time and a
        /// pitch-black horror scene snapped back to bright. Cleared by ScriptRuntime on play begin/end.</summary>
        public static float? ScriptAmbientOverride;

        /// <summary>A sky set from a script (#349) — wins over the scene's Skybox component until cleared (play end).
        /// Either an equirect texture (project-relative path, exposure, yaw in degrees) or a gradient.</summary>
        public sealed class SkyOverride
        {
            public string TexturePath;
            public float Exposure = 1f;
            public float RotationDeg;
            public float[] Gradient;   // top rgb, horizon rgb, bottom rgb — when no texture
        }
        public static SkyOverride ScriptSky;

        /// <summary>
        /// Submit all lights in the scene to the renderer.
        /// </summary>
        private void SubmitSceneLights(Data.Scene scene)
        {
            // Clear previous frame's lights
            VortexAPI.ClearAllLights();

            _hasSceneLights = false;
            _hasDirectionalLight = false;
            _hasSkybox = false;

            // Collect and submit all lights and skybox
            foreach (var entity in scene.Entities)
            {
                SubmitEntityLightsRecursive(entity);
            }

            // If no lights in scene, use default directional light
            if (!_hasSceneLights)
            {
                // Default sun light (like Unity/Unreal default scene)
                VortexAPI.SetDirectionalLightParams(
                    -0.5f, -0.7f, 0.5f,  // Direction
                    1.0f, 0.98f, 0.95f,   // Strong intensity for PBR
                    3.0f);
            }
            else if (!_hasDirectionalLight)
            {
                // The scene HAS lights but no directional one — force the sun OFF. The directional light is
                // PERSISTENT renderer state (ClearAllLights only clears the point/spot lists), so a default
                // sun set by an earlier lightless submit (project boot, scene switch) would otherwise burn
                // forever and drown out point/spot-only horror scenes — the flashlight looked broken because
                // a full-strength sun was still lighting the bunker.
                VortexAPI.SetDirectionalLightParams(0f, -1f, 0f, 1f, 1f, 1f, 0f);
            }
            
            // If no skybox, disable skybox rendering and set default ambient — unless a game script owns
            // the ambient right now (horror scenes crush it to ~0.01; the default would flood the dark).
            if (!_hasSkybox)
            {
                if (ScriptSky != null) { ApplyScriptSky(ScriptSky); SubmitSkyLight(ScriptSky); }   // a scripted sky needs no Skybox component (#349)
                else { VortexAPI.EnableSkybox(false); VortexAPI.SetSkyLight(null, false); PushEnvironmentMap(null); }
                VortexAPI.SetAmbientLightStrength(ScriptAmbientOverride ?? 0.35f);  // 0.35 matches the engine header default
            }
        }

        // ---- the sky light (IBL step 1): the Skybox component's picture or gradient, or a script's sky, as SH9 irradiance ----
        private string _skyLightKey;

        private void SubmitSkyLight(Skybox skybox)
        {
            SkyLight.Coefficients c = null;
            float e = skybox.Exposure;
            try
            {
                if ((skybox.SkyboxType == SkyboxType.Texture || skybox.SkyboxType == SkyboxType.Cubemap) && !string.IsNullOrEmpty(skybox.TexturePath))
                {
                    var projectPath = Data.ProjectData.Current?.Path ?? "";
                    var full = System.IO.Path.IsPathRooted(skybox.TexturePath) ? skybox.TexturePath : System.IO.Path.Combine(projectPath, skybox.TexturePath);
                    c = SkyLight.FromTexture(full, e, 0f);
                }
                if (c == null && skybox.SkyboxType == SkyboxType.SolidColor)
                    c = SkyLight.FromGradient(skybox.TopColorR * e, skybox.TopColorG * e, skybox.TopColorB * e, skybox.TopColorR * e, skybox.TopColorG * e, skybox.TopColorB * e, skybox.TopColorR * e, skybox.TopColorG * e, skybox.TopColorB * e);
                if (c == null)
                    c = SkyLight.FromGradient(skybox.TopColorR * e, skybox.TopColorG * e, skybox.TopColorB * e, skybox.HorizonColorR * e, skybox.HorizonColorG * e, skybox.HorizonColorB * e, skybox.BottomColorR * e, skybox.BottomColorG * e, skybox.BottomColorB * e);
            }
            catch { c = null; }
            // the ground bounce: the sun's light off the ground into the shadows (scene settings)
            try
            {
                var st = Data.ProjectData.Current?.ActiveScene?.Settings;
                if (st != null && st.BounceStrength > 0f)
                    c = SkyLight.WithGroundBounce(c, st.BounceStrength, st.BounceR, st.BounceG, st.BounceB, _sunR, _sunG, _sunB, _sunIntensity, _sunDirY);
            }
            catch { }
            PushSkyLight(c);
            SkyLight.ReflectionMap m = null;
            try
            {
                if ((skybox.SkyboxType == SkyboxType.Texture || skybox.SkyboxType == SkyboxType.Cubemap) && !string.IsNullOrEmpty(skybox.TexturePath))
                {
                    var projectPath = Data.ProjectData.Current?.Path ?? "";
                    var full = System.IO.Path.IsPathRooted(skybox.TexturePath) ? skybox.TexturePath : System.IO.Path.Combine(projectPath, skybox.TexturePath);
                    m = SkyLight.ReflectionFromTexture(full, e);
                }
                if (m == null && skybox.SkyboxType == SkyboxType.SolidColor)
                    m = SkyLight.ReflectionFromGradient(skybox.TopColorR * e, skybox.TopColorG * e, skybox.TopColorB * e, skybox.TopColorR * e, skybox.TopColorG * e, skybox.TopColorB * e, skybox.TopColorR * e, skybox.TopColorG * e, skybox.TopColorB * e);
                if (m == null)
                    m = SkyLight.ReflectionFromGradient(skybox.TopColorR * e, skybox.TopColorG * e, skybox.TopColorB * e, skybox.HorizonColorR * e, skybox.HorizonColorG * e, skybox.HorizonColorB * e, skybox.BottomColorR * e, skybox.BottomColorG * e, skybox.BottomColorB * e);
            }
            catch { m = null; }
            PushEnvironmentMap(m);
        }

        private void SubmitSkyLight(SkyOverride s)
        {
            SkyLight.Coefficients c = null;
            try
            {
                if (!string.IsNullOrEmpty(s.TexturePath))
                {
                    var projectPath = Data.ProjectData.Current?.Path ?? "";
                    var full = System.IO.Path.IsPathRooted(s.TexturePath) ? s.TexturePath : System.IO.Path.Combine(projectPath, s.TexturePath);
                    c = SkyLight.FromTexture(full, s.Exposure, s.RotationDeg);
                }
                var g = s.Gradient;
                if (c == null && g != null && g.Length >= 9)
                    c = SkyLight.FromGradient(g[0], g[1], g[2], g[3], g[4], g[5], g[6], g[7], g[8]);
            }
            catch { c = null; }
            // the ground bounce: the sun's light off the ground into the shadows (scene settings)
            try
            {
                var st = Data.ProjectData.Current?.ActiveScene?.Settings;
                if (st != null && st.BounceStrength > 0f)
                    c = SkyLight.WithGroundBounce(c, st.BounceStrength, st.BounceR, st.BounceG, st.BounceB, _sunR, _sunG, _sunB, _sunIntensity, _sunDirY);
            }
            catch { }
            PushSkyLight(c);
            SkyLight.ReflectionMap m = null;
            try
            {
                if (!string.IsNullOrEmpty(s.TexturePath))
                {
                    var projectPath = Data.ProjectData.Current?.Path ?? "";
                    var full = System.IO.Path.IsPathRooted(s.TexturePath) ? s.TexturePath : System.IO.Path.Combine(projectPath, s.TexturePath);
                    m = SkyLight.ReflectionFromTexture(full, s.Exposure);
                }
                var g = s.Gradient;
                if (m == null && g != null && g.Length >= 9) m = SkyLight.ReflectionFromGradient(g[0], g[1], g[2], g[3], g[4], g[5], g[6], g[7], g[8]);
            }
            catch { m = null; }
            PushEnvironmentMap(m);
        }

        private void PushSkyLight(SkyLight.Coefficients c)
        {
            string key = c != null ? c.Key : "";
            if (key == _skyLightKey) return;   // the renderer keeps the last coefficients; only changes go over the boundary
            _skyLightKey = key;
            VortexAPI.SetSkyLight(c != null ? c.Sh : null, c != null);
        }

        // ---- the sky as a reflection (IBL step 2): the picture or gradient as a mipped equirect texture the shaders sample by roughness ----
        private readonly Dictionary<string, long> _envTextures = new Dictionary<string, long>();
        private string _envKey;

        private void PushEnvironmentMap(SkyLight.ReflectionMap m)
        {
            string key = m != null ? m.Key : "";
            if (key == _envKey) return;
            _envKey = key;
            if (m == null) { VortexAPI.ApplyEnvironmentMap(-1, 0); return; }
            long id;
            if (!_envTextures.TryGetValue(key, out id) || id < 0)
            {
                try { id = VortexAPI.CreateTextureFromPixels(m.Width, m.Height, m.Rgba, false, true); } catch { id = -1; }
                _envTextures[key] = id;
            }
            int mips = 1 + (int)Math.Floor(Math.Log(Math.Max(m.Width, m.Height), 2.0));
            VortexAPI.ApplyEnvironmentMap(id, id >= 0 ? mips : 0);
        }

        private void SubmitEntityLightsRecursive(GameEntity entity)
        {
            if (entity == null || !entity.IsActive) return;
            // Eye toggle: a hidden subtree takes its light/skybox contribution out too (edit mode only —
            // the session flag must stay inert during play/GameHost re-submits).
            if (entity.IsHiddenInEditor && !IsPlayLike) return;

            // Check for Skybox component
            var skybox = entity.GetComponent<Skybox>();
            if (skybox != null && skybox.IsEnabled)
            {
                _hasSkybox = true;
                
                // Enable skybox rendering
                VortexAPI.EnableSkybox(true);
                
                // a script's sky (#349) wins over the component while it is set
                if (ScriptSky != null) ApplyScriptSky(ScriptSky);
                else switch (skybox.SkyboxType)
                {
                    case SkyboxType.SolidColor:
                        VortexAPI.SetSkyboxRenderMode(VortexAPI.SkyboxMode.SolidColor);
                        // Apply exposure to solid color
                        float exp = skybox.Exposure;
                        VortexAPI.SetSkyboxColor(
                            skybox.TopColorR * exp, 
                            skybox.TopColorG * exp, 
                            skybox.TopColorB * exp);
                        break;
                        
                    case SkyboxType.Gradient:
                        VortexAPI.SetSkyboxRenderMode(VortexAPI.SkyboxMode.Gradient);
                        // Set skybox colors (apply exposure)
                        float gradExp = skybox.Exposure;
                        VortexAPI.SetSkyboxGradient(
                            skybox.TopColorR * gradExp, skybox.TopColorG * gradExp, skybox.TopColorB * gradExp,
                            skybox.HorizonColorR * gradExp, skybox.HorizonColorG * gradExp, skybox.HorizonColorB * gradExp,
                            skybox.BottomColorR * gradExp, skybox.BottomColorG * gradExp, skybox.BottomColorB * gradExp);
                        break;
                        
                    case SkyboxType.Cubemap:
                    case SkyboxType.Texture:
                        // an equirect texture is sampled by the renderer's fullscreen sky pass (#326): behind
                        // everything, centred on whichever camera renders the frame, no depth write, no fog.
                        // The gradient colours stay the sky's ENVIRONMENT (specular reflections, the water's sky):
                        // set them to the HDR's zenith / horizon / ground so reflections match the picture.
                        float texExp = skybox.Exposure;
                        VortexAPI.SetSkyboxGradient(
                            skybox.TopColorR * texExp, skybox.TopColorG * texExp, skybox.TopColorB * texExp,
                            skybox.HorizonColorR * texExp, skybox.HorizonColorG * texExp, skybox.HorizonColorB * texExp,
                            skybox.BottomColorR * texExp, skybox.BottomColorG * texExp, skybox.BottomColorB * texExp);
                        if (!string.IsNullOrEmpty(skybox.TexturePath) && SubmitSkyboxWithTexture(skybox))
                        {
                            // the sky pass is on, in Texture mode
                        }
                        else if (!string.IsNullOrEmpty(skybox.SkyboxMeshPath))
                        {
                            VortexAPI.EnableSkybox(false);   // a custom sky mesh replaces the built-in pass
                            SubmitSkyboxMesh(skybox);
                        }
                        else
                        {
                            // No texture set - fall back to gradient
                            VortexAPI.EnableSkybox(true);
                            VortexAPI.SetSkyboxRenderMode(VortexAPI.SkyboxMode.Gradient);
                        }
                        break;
                }
                
                // the sky as a LIGHT (IBL step 1): SH9 irradiance baked off the picture or the gradient — blue from above,
                // the ground's bounce from below — replaces the shaders' fixed hemisphere ambient
                if (ScriptSky != null) SubmitSkyLight(ScriptSky);
                else SubmitSkyLight(skybox);

                // Also set ambient light based on skybox colors — unless a game script owns the ambient.
                var (ambientR, ambientG, ambientB) = skybox.GetAmbientColor();
                float ambientBrightness = Math.Max(Math.Max(ambientR, ambientG), ambientB);
                ambientBrightness = Math.Max(ambientBrightness, 0.2f);
                VortexAPI.SetAmbientLightStrength(ScriptAmbientOverride ?? ambientBrightness);
            }

            var light = entity.GetComponent<ECS.Components.Lighting.Light>();
            if (light != null)
            {
                // A PRESENT light suppresses the default sun even while disabled — otherwise an
                // all-lights-off scene (horror: flashlight toggled off) snaps back to full daylight.
                _hasSceneLights = true;
            }
            if (light != null && light.IsEnabled)
            {
                var transform = entity.Transform;
                if (transform != null)
                {
                    // WORLD transform (parent chain included) — a flashlight is naturally a CHILD of the
                    // player camera, and lights used to submit LocalPosition/LocalRotation: a parented spot
                    // rendered at its local offset near the origin with an unrotated cone. Meshes already
                    // accumulate the chain; lights now match. Local forward is +Z (the old Euler math was
                    // exactly Ry·Rx·(0,0,1)), so the world forward is the matrix's third basis row.
                    float[] wm;
                    try { wm = BuildWorldMatrixWithParent(entity); }
                    catch { wm = BuildWorldMatrix(transform); }
                    float px = wm[12], py = wm[13], pz = wm[14];
                    float dirX = wm[8], dirY = wm[9], dirZ = wm[10];
                    float dl = (float)Math.Sqrt(dirX * dirX + dirY * dirY + dirZ * dirZ);
                    if (dl > 1e-6f) { dirX /= dl; dirY /= dl; dirZ /= dl; }
                    else { dirX = 0f; dirY = 0f; dirZ = 1f; }

                    switch (light.LightType)
                    {
                        case ECS.Components.Lighting.LightType.Directional:
                            _hasDirectionalLight = true;
                            _sunR = light.ColorR; _sunG = light.ColorG; _sunB = light.ColorB; _sunIntensity = light.Intensity; _sunDirY = dirY;
                            // CSM (#24): ShadowType != None turns on the sun's cascade pass. Bias mapping:
                            // the component's legacy 0.05 default scales by 0.016 to land on the tuned
                            // CSM NDC bias (0.0008); user tweaks stay proportional (same idea as spots).
                            VortexAPI.SetDirectionalLightParams(
                                dirX, dirY, dirZ,
                                light.ColorR, light.ColorG, light.ColorB,
                                light.Intensity,
                                light.ShadowType != ECS.Components.Lighting.ShadowType.None,
                                light.ShadowStrength,
                                light.ShadowBias * 0.016f,
                                light.ShadowDistance);
                            // Soft = a 16-tap Poisson disk of 4 shadow texels (a penumbra), Hard = the 3x3 box
                            VortexAPI.SetDirectionalShadowSoftnessTexels(light.ShadowType == ECS.Components.Lighting.ShadowType.Soft ? 10.0f : 1.0f);   // the cap of the contact-hardening filter: the radius grows with the blocker distance, so a wide cap keeps contact shadows sharp
                            break;

                        case ECS.Components.Lighting.LightType.Point:
                            // Point cube shadows (#25): same ShadowType gate + legacy bias mapping
                            // as spots (0.05 default x 0.03 = the tuned 0.0015 NDC bias).
                            VortexAPI.SubmitPointLight(
                                px, py, pz,
                                light.ColorR, light.ColorG, light.ColorB,
                                light.Intensity, light.Range,
                                light.ShadowType != ECS.Components.Lighting.ShadowType.None,
                                light.ShadowStrength,
                                light.ShadowBias * 0.03f);
                            break;

                        case ECS.Components.Lighting.LightType.Spot:
                            // Spot shadows (#23): ShadowType != None requests this spot as the frame's
                            // shadow caster (renderer takes the FIRST such spot; Soft = Hard in v1).
                            // Bias mapping: the component's ShadowBias serialized with a 0.05 default long
                            // before shadows existed — scale by 0.03 so that legacy default lands exactly
                            // on the tuned NDC-depth bias (0.0015) and user tweaks stay proportional.
                            VortexAPI.SubmitSpotLight(
                                px, py, pz,
                                dirX, dirY, dirZ,
                                light.ColorR, light.ColorG, light.ColorB,
                                light.Intensity, light.Range,
                                light.SpotAngle, light.InnerSpotAngle,
                                light.ShadowType != ECS.Components.Lighting.ShadowType.None,
                                light.ShadowStrength,
                                light.ShadowBias * 0.03f,
                                light.ShadowResolution);
                            break;
                    }
                }
            }

            // Process children
            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                {
                    SubmitEntityLightsRecursive(child);
                }
            }
        }

        // Cache for built-in skybox sphere
        private long _skyboxTextureId = -1;
        private string _cachedSkyboxTexturePath = null;

        /// <summary>
        /// Submit a skybox with texture on a built-in inverted sphere.
        /// This is the simplest and most reliable way to render a textured skybox.
        /// </summary>
        /// <summary>Texture sky (#326): hand the equirect map to the renderer's fullscreen sky pass, which draws it
        /// behind all geometry at the far plane without a depth write, centred on the camera that renders the frame
        /// and outside the fog — no 1000 m sphere mesh around the editor camera any more. False when the texture
        /// cannot be loaded (the caller falls back to the gradient).</summary>
        private bool SubmitSkyboxWithTexture(Skybox skybox) => SubmitSkyTexture(skybox.TexturePath, skybox.Exposure, 0f);

        /// <summary>Texture sky (#326): hand an equirect map to the renderer's fullscreen sky pass, which draws it
        /// behind all geometry at the far plane without a depth write, centred on the camera that renders the frame
        /// and outside the fog. <paramref name="rotationDeg"/> turns it around the up axis. False when the texture
        /// cannot be loaded (the caller falls back to the gradient).</summary>
        private bool SubmitSkyTexture(string texturePath, float exposure, float rotationDeg)
        {
            var projectPath = Data.ProjectData.Current?.Path ?? "";
            var fullTexturePath = System.IO.Path.IsPathRooted(texturePath)
                ? texturePath
                : System.IO.Path.Combine(projectPath, texturePath);

            if (_cachedSkyboxTexturePath != fullTexturePath)
            {
                // a new path: load it once; a missing file is remembered too, so the fallback does not retry every frame
                _cachedSkyboxTexturePath = fullTexturePath;
                _skyboxTextureId = -1;
                if (!AssetVfs.Exists(fullTexturePath))
                    Log($"[SceneRenderService] Skybox texture not found: {fullTexturePath}");
                else
                {
                    Log($"[SceneRenderService] Loading skybox texture: {fullTexturePath}");
                    _skyboxTextureId = ImportTexturePath(fullTexturePath);
                    if (_skyboxTextureId < 0) Log("[SceneRenderService] Failed to load skybox texture");
                }
            }
            if (_skyboxTextureId < 0) return false;

            VortexAPI.EnableSkybox(true);
            VortexAPI.SetSkyboxRenderMode(VortexAPI.SkyboxMode.Texture);
            // a Radiance .hdr keeps its range (RGBA16F): where the scene uses the exposure pipeline (a scene exposure or the
            // neutral tonemapper) the sky pass exposes + tonemaps it like the lit surfaces; other scenes keep the classic look
            int hdrMode = 0;
            if (fullTexturePath.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase))
            {
                var st = Data.ProjectData.Current?.ActiveScene?.Settings;
                hdrMode = st != null && (st.SceneExposure != 0f || st.Tonemapper != 0) ? 2 : 1;
            }
            VortexAPI.ApplySkyboxTexture(_skyboxTextureId, exposure, rotationDeg, hdrMode);
            return true;
        }

        /// <summary>The sky a script asked for (#349): its texture through the sky pass, or its gradient.</summary>
        private void ApplyScriptSky(SkyOverride s)
        {
            if (!string.IsNullOrEmpty(s.TexturePath) && SubmitSkyTexture(s.TexturePath, s.Exposure, s.RotationDeg)) return;
            VortexAPI.EnableSkybox(true);
            VortexAPI.SetSkyboxRenderMode(VortexAPI.SkyboxMode.Gradient);
            var g = s.Gradient;
            if (g != null && g.Length >= 9)
                VortexAPI.SetSkyboxGradient(g[0] * s.Exposure, g[1] * s.Exposure, g[2] * s.Exposure, g[3] * s.Exposure, g[4] * s.Exposure, g[5] * s.Exposure, g[6] * s.Exposure, g[7] * s.Exposure, g[8] * s.Exposure);
        }

        // Cache for skybox mesh
        private readonly Dictionary<string, (long meshId, long materialId, long textureId)> _skyboxMeshCache = 
            new Dictionary<string, (long, long, long)>();



        /// <summary>
        /// Submit a skybox mesh for rendering.
        /// </summary>
        private void SubmitSkyboxMesh(Skybox skybox)
        {
            var meshPath = skybox.SkyboxMeshPath;
            var texturePath = skybox.TexturePath;
            
            if (string.IsNullOrEmpty(meshPath))
            {
                Log("[SceneRenderService] Skybox mesh path is empty");
                return;
            }
            
            // Get full path
            var projectPath = Data.ProjectData.Current?.Path ?? "";
            var fullMeshPath = System.IO.Path.IsPathRooted(meshPath) 
                ? meshPath 
                : System.IO.Path.Combine(projectPath, meshPath);

            Log($"[SceneRenderService] Skybox mesh path: {fullMeshPath}");
            Log($"[SceneRenderService] Skybox texture path: {texturePath}");

            // Check if file exists
            if (!AssetVfs.Exists(fullMeshPath))
            {
                Log($"[SceneRenderService] Skybox mesh file not found: {fullMeshPath}");
                return;
            }

            // Create cache key that includes both mesh and texture
            var cacheKey = $"{fullMeshPath}|{texturePath ?? ""}";

            // Check if we have a cached mesh
            if (!_skyboxMeshCache.TryGetValue(cacheKey, out var cached))
            {
                Log($"[SceneRenderService] Importing skybox mesh...");
                
                // Import the mesh
                var submeshData = VortexAPI.ImportModelWithMaterialsFromFile(fullMeshPath);
                if (submeshData != null && submeshData.Length > 0)
                {
                    long meshId = submeshData[0].MeshId;
                    long materialId = submeshData[0].MaterialId;
                    long textureId = -1;

                    Log($"[SceneRenderService] Skybox mesh imported: meshId={meshId}, materialId={materialId}");

                    // If we have a texture, load it and apply to material
                    if (!string.IsNullOrEmpty(texturePath))
                    {
                        var fullTexturePath = System.IO.Path.IsPathRooted(texturePath)
                            ? texturePath
                            : System.IO.Path.Combine(projectPath, texturePath);

                        Log($"[SceneRenderService] Loading skybox texture: {fullTexturePath}");

                        if (AssetVfs.Exists(fullTexturePath))
                        {
                            textureId = ImportTexturePath(fullTexturePath);
                            if (textureId >= 0)
                            {
                                VortexAPI.SetMaterialAlbedoTexture(materialId, textureId);
                                Log($"[SceneRenderService] Skybox texture loaded: textureId={textureId}");
                            }
                            else
                            {
                                Log($"[SceneRenderService] Failed to load skybox texture");
                            }
                        }
                        else
                        {
                            Log($"[SceneRenderService] Skybox texture file not found: {fullTexturePath}");
                        }
                    }

                    cached = (meshId, materialId, textureId);
                    _skyboxMeshCache[cacheKey] = cached;
                    
                    Log($"[SceneRenderService] Skybox cached: mesh={meshId}, material={materialId}, texture={textureId}");
                }
                else
                {
                    Log($"[SceneRenderService] Failed to import skybox mesh: {meshPath}");
                    return;
                }
            }

            // centred on the camera that renders the frame — the game camera while playing, the editor's otherwise —
            // and sized to stay inside that camera's far plane (#326)
            SkyMeshPlacement(out float camX, out float camY, out float camZ, out float scale);
            
            // Create world matrix with translation to camera position
            float[] worldMatrix = new float[16]
            {
                scale, 0, 0, 0,
                0, scale, 0, 0,
                0, 0, scale, 0,
                camX, camY, camZ, 1
            };

            // Submit skybox mesh - it will be rendered before other objects
            VortexAPI.SubmitMeshForRendering(cached.meshId, cached.materialId, worldMatrix);
        }

        /// <summary>Where a camera-following sky mesh goes this frame: the render camera's world position and a radius
        /// inside its far plane (#326) — the game camera while playing, the editor camera otherwise.</summary>
        private void SkyMeshPlacement(out float x, out float y, out float z, out float scale)
        {
            float far = RaycastService.EditorFarClip;
            if (IsPlayLike && PlayCameraHelper.TryGetMainCameraWorld(Data.ProjectData.Current?.ActiveScene, out var pos, out _, out var cam))
            {
                x = pos.X; y = pos.Y; z = pos.Z;
                if (cam != null && cam.FarClip > 1f) far = cam.FarClip;
            }
            else
            {
                var c = EditorCameraController.Instance;
                x = c?.PositionX ?? 0f; y = c?.PositionY ?? 0f; z = c?.PositionZ ?? 0f;
            }
            scale = far * 0.9f;
        }

        /// <summary>
        /// Clear cached skybox mesh and texture (call when skybox properties change).
        /// </summary>
        public void ClearSkyboxMeshCache()
        {
            _skyboxMeshCache.Clear();
            _cachedSkyboxTexturePath = null; // Force reload of texture
            _skyboxTextureId = -1;
            Log("[SceneRenderService] Skybox cache cleared");
        }

        /// <summary>
        /// Render light icons for all lights in the scene.
        /// </summary>
        private void RenderAllLightIcons(Data.Scene scene, GameEntity selected)
        {
            if (!VortexAPI.AreGizmosVisible) return;

            foreach (var entity in scene.Entities)
            {
                RenderLightIconRecursive(entity, selected);
            }
        }

        private void RenderLightIconRecursive(GameEntity entity, GameEntity selected)
        {
            if (entity == null || !entity.IsActive || entity.IsHiddenInEditor) return;   // eye toggle hides the icon too
            var light = entity.GetComponent<ECS.Components.Lighting.Light>();
            if (light != null)
            {
                // World position so the icon/outline sits where the light actually shines (parented lights).
                ECS.Vector3 pos;
                try { var wm = BuildWorldMatrixWithParent(entity); pos = new ECS.Vector3(wm[12], wm[13], wm[14]); }
                catch { pos = entity.Transform?.LocalPosition ?? ECS.Vector3.Zero; }
                var rot = entity.Transform?.LocalRotation ?? ECS.Vector3.Zero;
                
                // Light icon color based on type
                float iconR, iconG, iconB;
                switch (light.LightType)
                {
                    case ECS.Components.Lighting.LightType.Directional:
                        iconR = 1.0f; iconG = 0.95f; iconB = 0.5f; // Yellow-gold
                        break;
                    case ECS.Components.Lighting.LightType.Point:
                        iconR = 0.5f; iconG = 0.8f; iconB = 1.0f; // Light blue
                        break;
                    case ECS.Components.Lighting.LightType.Spot:
                        iconR = 0.5f; iconG = 1.0f; iconB = 0.5f; // Light green
                        break;
                    default:
                        iconR = 1.0f; iconG = 1.0f; iconB = 1.0f;
                        break;
                }

                // TODO: Render actual light icon using VortexAPI.RenderLightIcon when available
                // For now, we render a simple selection outline for the selected light
                if (entity == selected)
                {
                    VortexAPI.RenderSelectionOutline(
                        pos.X, pos.Y, pos.Z,
                        0.5f, 0.5f, 0.5f,
                        rot.X, rot.Y, rot.Z);
                }
            }

            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                {
                    RenderLightIconRecursive(child, selected);
                }
            }
        }

        #endregion

        public void Dispose()
        {
            Shutdown();
        }
    }
}
