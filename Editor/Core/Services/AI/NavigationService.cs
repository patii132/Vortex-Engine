using System;
using System.Collections.Generic;
using System.IO;
using Editor.Core.Data;
using Editor.Core.Services.Physics;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.AI;
using Editor.ECS.Components.Animation;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using Editor.Utilities;
using SysVec = System.Numerics.Vector3;
using SysQuat = System.Numerics.Quaternion;
using SysMat = System.Numerics.Matrix4x4;

namespace Editor.Core.Services.AI
{
    /// <summary>Result of a path query / an agent's current path.</summary>
    public enum NavPathStatus
    {
        /// <summary>No navmesh, or start / end are not on (near) it.</summary>
        Invalid = 0,
        /// <summary>The path reaches the destination.</summary>
        Complete = 1,
        /// <summary>The destination is unreachable: the path leads to the closest reachable point.</summary>
        Partial = 2,
        /// <summary>An agent's path is still being computed (SetDestination returned immediately).</summary>
        Pending = 3
    }

    /// <summary>Which scene geometry a bake reads (stored with the navmesh in <see cref="NavBakeSettings.SourceFlags"/>).</summary>
    [Flags]
    public enum NavGeometrySource
    {
        None = 0,
        /// <summary>Enabled, solid colliders of entities that don't move (no dynamic / kinematic Rigidbody, no joint).</summary>
        Colliders = 1,
        /// <summary>Render meshes of entities marked Static that have no collider.</summary>
        StaticRenderMeshes = 2,
        /// <summary>Render meshes of every non-moving entity that has no collider.</summary>
        AllRenderMeshes = 4,
        Default = Colliders | StaticRenderMeshes
    }

    /// <summary>World-space bake input gathered from a scene (see <see cref="NavigationService.CollectGeometry"/>).</summary>
    public sealed class NavBakeGeometry
    {
        public float[] Verts = new float[0];
        public int[] Tris = new int[0];
        public byte[] TriAreas;
        public float[] Boxes = new float[0];
        public byte[] BoxAreas;
        public int TriangleCount, BoxCount, EntityCount;
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>Outcome of a bake.</summary>
    public sealed class NavBakeResult
    {
        public bool Success;
        /// <summary>Native error code (&lt; 0) when the bake failed.</summary>
        public int Error;
        public string Message = "";
        public byte[] Data;
        public NavBakeStats Stats;
        /// <summary>Where the .vnav was written (null when not saved).</summary>
        public string Path;
    }

    /// <summary>
    /// AI &amp; Navigation (GitHub #109 / #110): the scene's navmesh and the agents that walk on it.
    ///
    /// <b>Bake</b> (editor, Window ▸ Navigation): <see cref="CollectGeometry"/> flattens the scene's static geometry into
    /// world space — box / sphere / capsule colliders and primitive meshes as SOLID oriented boxes, mesh colliders and
    /// model render meshes as triangles (the same model-triangle export the mesh colliders use) — skipping moving
    /// entities (dynamic / kinematic Rigidbody, joints), characters (Animator, NavAgent, tag "Player") and everything
    /// tagged <see cref="IgnoreTag"/>. <see cref="Bake"/> runs Recast (tiled, multi-threaded; safe on a worker thread)
    /// and the result is saved as <c>&lt;Scene&gt;.vnav</c> next to the <c>.vscene</c> (it ships inside Assets.vpak).
    ///
    /// <b>Runtime</b> (play / standalone): <see cref="Begin"/> loads the scene's .vnav and registers every enabled
    /// <see cref="NavAgent"/> with the native crowd (DetourCrowd: path corridors, steering, local avoidance),
    /// <see cref="Tick"/> updates the crowd and writes each agent's pose back to its entity (collide-and-slide against the
    /// colliders through <see cref="CollisionService.MoveCharacter"/> when <see cref="NavAgent.ResolveCollisions"/> is
    /// on), <see cref="End"/> drops the agents (the navmesh stays loaded for the editor overlay). Scripts drive agents
    /// through <c>Vortex.Navigation</c>. The lifecycle is owned by <see cref="AiRuntime"/>.
    ///
    /// Without a Recast-enabled engine (<see cref="NavigationNative.Available"/> false) every call is a harmless no-op.
    /// </summary>
    public static class NavigationService
    {
        public const string NavMeshExtension = ".vnav";
        /// <summary>Entities with this tag (and their children) are left out of the bake (doors a script moves, clutter).</summary>
        public const string IgnoreTag = "NavIgnore";

        public static bool Available => NavigationNative.Available;
        public static string UnavailableReason => NavigationNative.Reason;

        // ================================================================================================ settings

        /// <summary>The engine's default bake settings with the editor's default geometry sources.</summary>
        public static NavBakeSettings DefaultSettings()
        {
            var s = new NavBakeSettings
            {
                CellSize = 0.2f, CellHeight = 0.1f, AgentHeight = 1.8f, AgentRadius = 0.4f, AgentMaxClimb = 0.4f, AgentMaxSlope = 45f,
                RegionMinSize = 8f, RegionMergeSize = 20f, EdgeMaxLen = 12f, EdgeMaxError = 1.3f, DetailSampleDist = 6f, DetailSampleMaxError = 1f,
                VertsPerPoly = 6, TileSize = 48, Partition = 0, FilterFlags = 7,
                BoundsMinX = 1f, BoundsMinY = 1f, BoundsMinZ = 1f, BoundsMaxX = -1f, BoundsMaxY = -1f, BoundsMaxZ = -1f
            };
            if (Available) { try { VortexAPI.NavGetDefaultSettings(out s); } catch { } }
            s.SourceFlags = (int)NavGeometrySource.Default;
            return s;
        }

        /// <summary>Settings to show for a scene: those its navmesh was baked with, else the defaults.</summary>
        public static NavBakeSettings SettingsFor(Scene scene)
        {
            if (scene != null && ReferenceEquals(scene, LoadedScene) && TryGetInfo(out var s, out _))
            {
                if (s.SourceFlags == 0) s.SourceFlags = (int)NavGeometrySource.Default;
                return s;
            }
            var data = scene != null ? ReadNavMeshFile(scene) : null;
            if (data != null && data.Length >= 8 + 23 * 4)
            {
                // The .vnav header starts with magic, version, then the settings block (NavigationFormat.h).
                try
                {
                    var h = System.Runtime.InteropServices.GCHandle.Alloc(data, System.Runtime.InteropServices.GCHandleType.Pinned);
                    try
                    {
                        if (BitConverter.ToUInt32(data, 0) == 0x56414E56u)
                        {
                            var p = new IntPtr(h.AddrOfPinnedObject().ToInt64() + 8);
                            var s2 = (NavBakeSettings)System.Runtime.InteropServices.Marshal.PtrToStructure(p, typeof(NavBakeSettings));
                            if (s2.SourceFlags == 0) s2.SourceFlags = (int)NavGeometrySource.Default;
                            if (s2.CellSize > 0f) return s2;
                        }
                    }
                    finally { h.Free(); }
                }
                catch { }
            }
            return DefaultSettings();
        }

        // ================================================================================================ files

        /// <summary><c>Assets/Scenes/Level.vscene</c> -> <c>Assets/Scenes/Level.vnav</c> (null without a scene path).</summary>
        public static string NavMeshPathFor(Scene scene)
        {
            string p = scene?.FilePath;
            return string.IsNullOrEmpty(p) ? null : Path.ChangeExtension(p, NavMeshExtension);
        }

        public static bool HasNavMeshFile(Scene scene)
        {
            var p = NavMeshPathFor(scene);
            return p != null && AssetVfs.Exists(p);
        }

        /// <summary>The scene's baked navmesh bytes — the mounted pak in a shipped game, the loose file in the editor.</summary>
        public static byte[] ReadNavMeshFile(Scene scene)
        {
            var p = NavMeshPathFor(scene);
            if (p == null) return null;
            try
            {
                if (AssetVfs.IsMounted && AssetVfs.TryGetBytes(p, out var bytes) && bytes != null) return bytes;
                if (File.Exists(p)) return File.ReadAllBytes(p);
            }
            catch (Exception ex) { Log("[Navigation] could not read " + p + ": " + ex.Message, true); }
            return null;
        }

        /// <summary>Write a baked navmesh next to the scene (<c>.vnav</c>). False (with the reason logged) on failure.</summary>
        public static bool Save(Scene scene, byte[] data, out string path)
        {
            path = NavMeshPathFor(scene);
            if (path == null || data == null || data.Length == 0) return false;
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, data);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception ex)
            {
                Log("[Navigation] could not save " + path + ": " + ex.Message, true);
                return false;
            }
        }

        // ================================================================================================ loaded navmesh

        private static Scene _loadedScene;
        private static int _version;

        /// <summary>The scene whose navmesh is loaded (null = none, or one loaded from raw data).</summary>
        public static Scene LoadedScene => _loadedScene;
        /// <summary>Bumped on every load / unload (debug-mesh cache key).</summary>
        public static int Version => _version;

        public static bool IsLoaded
        {
            get
            {
                if (!Available) return false;
                try { return VortexAPI.NavIsLoaded() != 0; } catch { return false; }
            }
        }

        public static bool TryGetInfo(out NavBakeSettings settings, out NavBakeStats stats)
        {
            settings = default(NavBakeSettings); stats = default(NavBakeStats);
            if (!Available) return false;
            try { return VortexAPI.NavGetInfo(out settings, out stats) != 0; } catch { return false; }
        }

        /// <summary>Load navmesh bytes (a .vnav blob) as the current navmesh; every agent is re-registered on the next tick.</summary>
        public static bool LoadData(byte[] data, Scene owner)
        {
            if (!Available || data == null || data.Length == 0) return false;
            bool ok;
            try { ok = VortexAPI.NavLoad(data, data.Length) != 0; } catch { ok = false; }
            _version++;
            _loadedScene = ok ? owner : null;
            DropAgentHandles();
            return ok;
        }

        /// <summary>Load the scene's .vnav (VFS-aware). False when the scene has none or it is invalid.</summary>
        public static bool LoadForScene(Scene scene)
        {
            var data = ReadNavMeshFile(scene);
            if (data == null) return false;
            if (!LoadData(data, scene))
            {
                Log("[Navigation] '" + NavMeshPathFor(scene) + "' is not a valid navmesh for this engine version — rebake it (Window ▸ Navigation).", true);
                return false;
            }
            return true;
        }

        public static void Unload()
        {
            if (Available) { try { VortexAPI.NavUnload(); } catch { } }
            _loadedScene = null;
            _version++;
            DropAgentHandles();
        }

        // ================================================================================================ bake

        /// <summary>Flatten the scene's static geometry into world space (UI / game thread: reads the ECS).</summary>
        public static NavBakeGeometry CollectGeometry(Scene scene, int sourceFlags)
        {
            var g = new GeometryBuilder((NavGeometrySource)(sourceFlags == 0 ? (int)NavGeometrySource.Default : sourceFlags));
            if (scene?.Entities != null)
                foreach (var e in scene.Entities) g.Visit(e);
            return g.Finish();
        }

        /// <summary>Run the bake on prepared geometry. Thread-safe: call it from a worker thread and poll
        /// <see cref="BakeProgress"/>; the loaded navmesh is untouched (load the result with <see cref="LoadData"/>).</summary>
        public static NavBakeResult Bake(NavBakeGeometry geometry, NavBakeSettings settings)
        {
            var r = new NavBakeResult();
            if (!Available) { r.Error = -6; r.Message = "Navigation is unavailable: " + UnavailableReason; return r; }
            if (geometry == null || (geometry.TriangleCount == 0 && geometry.BoxCount == 0))
            {
                r.Error = -1; r.Message = "Nothing to bake: the scene has no static colliders or meshes."; return r;
            }
            int size;
            NavBakeStats stats;
            try
            {
                size = VortexAPI.NavBake(geometry.Verts, geometry.Verts.Length / 3, geometry.Tris, geometry.TriangleCount, geometry.TriAreas,
                    geometry.Boxes, geometry.BoxCount, geometry.BoxAreas, ref settings, out stats);
            }
            catch (Exception ex) { r.Error = -4; r.Message = "Bake failed: " + ex.Message; return r; }
            r.Stats = stats;
            if (size <= 0) { r.Error = size; r.Message = ErrorText(size); return r; }
            var data = new byte[size];
            int got = VortexAPI.NavGetBakeResult(data, size);
            try { VortexAPI.NavClearBakeResult(); } catch { }
            if (got != size) { r.Error = -4; r.Message = "Bake result could not be read back."; return r; }
            r.Data = data;
            r.Success = true;
            r.Message = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0} polygons in {1} tiles, {2:0.0} ms ({3} triangles + {4} solid boxes from {5} entities)",
                stats.PolyCount, stats.TileCount, stats.BakeMs, stats.InputTriangles, stats.InputBoxes, geometry.EntityCount);
            return r;
        }

        /// <summary>Gather, bake, save next to the scene and load — synchronously (the Navigation window does the same on a
        /// worker thread with progress).</summary>
        public static NavBakeResult BakeScene(Scene scene, NavBakeSettings settings, bool save = true, bool load = true)
        {
            var geo = CollectGeometry(scene, settings.SourceFlags);
            var r = Bake(geo, settings);
            if (!r.Success) return r;
            if (save && Save(scene, r.Data, out var path)) r.Path = path;
            if (load) LoadData(r.Data, scene);
            return r;
        }

        /// <summary>0..1 of the running bake.</summary>
        public static float BakeProgress
        {
            get { if (!Available) return 0f; try { return VortexAPI.NavGetBakeProgress(); } catch { return 0f; } }
        }

        public static void CancelBake() { if (Available) { try { VortexAPI.NavCancelBake(); } catch { } } }

        public static string ErrorText(int code)
        {
            switch (code)
            {
                case -1: return "Nothing to bake: the scene has no static colliders or meshes.";
                case -2: return "Invalid bake settings.";
                case -3: return "Bake cancelled.";
                case -5: return "Nothing walkable: no surface is flat enough / large enough for the agent (check Max Slope, Agent Radius, Min Region Size).";
                case -6: return "Navigation is unavailable in this engine build: " + UnavailableReason;
                case -7: return "The level needs more than 16384 tiles: raise the Tile Size or the Cell Size.";
                case -8: return "Another bake is still running.";
                default: return "Bake failed (see the Console / log).";
            }
        }

        // ================================================================================================ queries

        private static readonly float[] _qa = new float[3], _qb = new float[3], _qc = new float[3], _qd = new float[3], _qext = new float[3];
        private static float[] _pathBuf = new float[256 * 3];

        /// <summary>Straight path from <paramref name="from"/> to <paramref name="to"/> (corner points, start and end included,
        /// written to <paramref name="corners"/> when given).</summary>
        public static NavPathStatus CalculatePath(Vector3 from, Vector3 to, List<Vector3> corners)
        {
            corners?.Clear();
            if (!IsLoaded) return NavPathStatus.Invalid;
            Fill(_qa, from); Fill(_qb, to);
            int status;
            int n = VortexAPI.NavFindPath(_qa, _qb, null, _pathBuf, _pathBuf.Length / 3, out status);
            if (n <= 0) return NavPathStatus.Invalid;
            if (corners != null) for (int i = 0; i < n; i++) corners.Add(new Vector3(_pathBuf[i * 3], _pathBuf[i * 3 + 1], _pathBuf[i * 3 + 2]));
            return status == 1 ? NavPathStatus.Complete : status == 2 ? NavPathStatus.Partial : NavPathStatus.Invalid;
        }

        /// <summary>The closest navmesh point within <paramref name="maxDistance"/> of <paramref name="position"/>.</summary>
        public static bool SamplePosition(Vector3 position, float maxDistance, out Vector3 hit)
        {
            hit = position;
            if (!IsLoaded || maxDistance <= 0f) return false;
            Fill(_qa, position);
            _qext[0] = maxDistance; _qext[1] = Math.Max(maxDistance, 0.5f); _qext[2] = maxDistance;
            if (VortexAPI.NavClosestPoint(_qa, _qext, _qb) == 0) return false;
            var p = new Vector3(_qb[0], _qb[1], _qb[2]);
            if ((p - position).Magnitude > maxDistance + 1e-4f) return false;
            hit = p;
            return true;
        }

        /// <summary>Walk the navmesh surface from <paramref name="from"/> towards <paramref name="to"/>: true = a navmesh edge
        /// (wall / drop) blocks the way; <paramref name="hit"/> is where (or <paramref name="to"/> when clear).</summary>
        public static bool Raycast(Vector3 from, Vector3 to, out Vector3 hit, out Vector3 normal)
        {
            hit = to; normal = Vector3.Zero;
            if (!IsLoaded) return false;
            Fill(_qa, from); Fill(_qb, to);
            float t;
            bool blocked = VortexAPI.NavRaycast(_qa, _qb, null, _qc, _qd, out t) != 0;
            hit = new Vector3(_qc[0], _qc[1], _qc[2]);
            normal = new Vector3(_qd[0], _qd[1], _qd[2]);
            return blocked;
        }

        /// <summary>A random point anywhere on the navmesh.</summary>
        public static bool RandomPoint(out Vector3 point)
        {
            point = Vector3.Zero;
            if (!IsLoaded || VortexAPI.NavRandomPoint(_qa) == 0) return false;
            point = new Vector3(_qa[0], _qa[1], _qa[2]);
            return true;
        }

        /// <summary>A random point reachable from <paramref name="center"/> within <paramref name="radius"/> (along the surface).</summary>
        public static bool RandomPointAround(Vector3 center, float radius, out Vector3 point)
        {
            point = center;
            if (!IsLoaded || radius <= 0f) return false;
            Fill(_qa, center);
            if (VortexAPI.NavRandomPointAround(_qa, radius, null, _qb) == 0) return false;
            point = new Vector3(_qb[0], _qb[1], _qb[2]);
            return true;
        }

        // ================================================================================================ agents (runtime)

        private sealed class AgentRec
        {
            public GameEntity Entity;
            public NavAgent Agent;
            public uint Handle;
            public long SelfId;
            public bool HasColliders;
            public SysVec LastFeet;
            public bool HasLastFeet;
            public bool HasDestination;
            public SysVec Destination;
            public bool Arrived;
            public float SpeedOverride = -1f;
            public bool Stopped;
            public NavAgentState State;
            public bool StateValid;
            public volatile bool ParamsDirty = true;
            public float Yaw;
            public bool YawKnown;
            public bool Active = true;
        }

        private static readonly Dictionary<GameEntity, AgentRec> _agents = new Dictionary<GameEntity, AgentRec>();
        private static readonly List<AgentRec> _agentList = new List<AgentRec>();
        private static Scene _runScene;
        private static float _rescanTimer;
        private static bool _warnedNoNavMesh;
        private static readonly float[] _fa = new float[3], _fb = new float[3];

        /// <summary>True between <see cref="Begin"/> and <see cref="End"/>.</summary>
        public static bool IsRunning { get; private set; }
        /// <summary>The scene the runtime was started for.</summary>
        public static Scene RunScene => _runScene;
        /// <summary>Agents registered with the crowd right now.</summary>
        public static int AgentCount { get { int n = 0; foreach (var r in _agentList) if (r.Handle != 0) n++; return n; } }

        /// <summary>Play start: load the scene's navmesh and register its agents. Call after the collision / physics worlds
        /// were built (agents take their own colliders out of the static collision world).</summary>
        public static void Begin(Scene scene)
        {
            End();
            _runScene = scene;
            IsRunning = true;
            _rescanTimer = 0f;
            if (!Available)
            {
                if (HasAgents(scene)) Log("[Navigation] unavailable (" + UnavailableReason + ") — Nav Agents will not move.", true);
                return;
            }
            bool loaded = LoadForScene(scene);
            if (!loaded && IsLoaded && ReferenceEquals(_loadedScene, scene)) loaded = true;
            if (!loaded && HasAgents(scene) && !_warnedNoNavMesh)
            {
                _warnedNoNavMesh = true;
                Log("[Navigation] scene '" + scene?.Name + "' has Nav Agents but no baked navmesh (" + Path.GetFileName(NavMeshPathFor(scene) ?? "?")
                    + ") — bake it in Window ▸ Navigation; the agents stand still until then.", true);
            }
            Rescan();
            if (loaded)
            {
                TryGetInfo(out _, out var st);
                Log("[Navigation] navmesh loaded for '" + scene?.Name + "': " + st.PolyCount + " polygons, " + AgentCount + " agents");
            }
        }

        /// <summary>Play stop / scene switch: remove every agent from the crowd (the navmesh stays loaded).</summary>
        public static void End()
        {
            if (Available) { try { VortexAPI.NavCrowdClear(); } catch { } }
            foreach (var r in _agentList) if (r.Agent != null) r.Agent.PropertyChanged -= OnAgentChanged;
            _agents.Clear();
            _agentList.Clear();
            _runScene = null;
            IsRunning = false;
        }

        /// <summary>One frame: register new / re-activated agents, update the crowd, move the entities.</summary>
        public static void Tick(float dt)
        {
            if (!IsRunning || !Available) return;
            if (dt < 0f) dt = 0f;
            if (dt > 0.1f) dt = 0.1f;
            _rescanTimer -= dt;
            if (_rescanTimer <= 0f) { _rescanTimer = 0.5f; Rescan(); }
            if (!IsLoaded) return;

            // 1) sync entity -> crowd (spawns, teleports by scripts, parameter changes)
            for (int i = 0; i < _agentList.Count; i++)
            {
                var r = _agentList[i];
                bool live = r.Agent != null && r.Agent.IsEnabled && r.Entity != null && r.Entity.ActiveInHierarchy && InRunScene(r.Entity);
                if (!live)
                {
                    if (r.Handle != 0) { try { VortexAPI.NavAgentRemove(r.Handle); } catch { } r.Handle = 0; }
                    r.StateValid = false;
                    continue;
                }
                var feet = EntityFeet(r);
                if (r.Handle == 0)
                {
                    AddToCrowd(r, feet);
                    continue;
                }
                if (r.HasLastFeet && (feet - r.LastFeet).LengthSquared() > 0.05f * 0.05f)
                {
                    // Moved by a script / physics since our last write: teleport and keep the destination.
                    Fill(_fa, feet);
                    VortexAPI.NavAgentTeleport(r.Handle, _fa);
                    if (r.HasDestination) { Fill(_fb, r.Destination); VortexAPI.NavAgentSetTarget(r.Handle, _fb); }
                }
                if (r.ParamsDirty)
                {
                    var p = ParamsOf(r);
                    VortexAPI.NavAgentSetParams(r.Handle, ref p);
                    r.ParamsDirty = false;
                }
            }

            // 2) the crowd: path requests, corridor following, avoidance
            VortexAPI.NavCrowdUpdate(dt);

            // 3) crowd -> entities
            bool moved = false;
            for (int i = 0; i < _agentList.Count; i++)
            {
                var r = _agentList[i];
                if (r.Handle == 0) continue;
                if (VortexAPI.NavAgentGetState(r.Handle, out r.State) == 0) { r.StateValid = false; r.Handle = 0; continue; }
                r.StateValid = true;
                var nav = new SysVec(r.State.PosX, r.State.PosY, r.State.PosZ);
                var feet = nav;
                bool rootDriven = Animation.AnimationService.Instance.RootMotionDrives(r.Entity);
                if (rootDriven)
                {
                    // #113: the clip's root motion moves the entity (through collision); the crowd agent follows it, so the
                    // path corridor and the desired velocity (Navigation.Velocity — the script picks walk / run from it)
                    // stay valid, and the agent only turns the body toward the path.
                    var cur = EntityFeet(r);
                    if (!r.HasLastFeet || (cur - nav).LengthSquared() > 1e-6f) { Fill(_fa, cur); VortexAPI.NavAgentMovePosition(r.Handle, _fa); }
                    feet = cur;
                }
                else if (r.Agent.ResolveCollisions && CollisionService.IsBuilt && r.HasLastFeet)
                {
                    if (r.HasColliders) { try { CollisionService.RemoveEntityShapes(r.Entity); } catch { } }   // never collide with itself
                    var delta = nav - r.LastFeet;
                    var from = new Vector3(r.LastFeet.X, r.LastFeet.Y, r.LastFeet.Z);
                    var res = CollisionService.MoveCharacter(from, r.Agent.Radius, r.Agent.Height, new Vector3(delta.X, delta.Y, delta.Z), out _, r.SelfId);
                    var resolved = new SysVec(res.X, res.Y, res.Z);
                    if (Math.Abs(resolved.Y - nav.Y) > 1.0f) resolved = nav;   // off the navmesh vertically: trust the navmesh
                    float dx = resolved.X - nav.X, dz = resolved.Z - nav.Z;
                    if (dx * dx + dz * dz > 0.02f * 0.02f)
                    {
                        // A collider (prop, closed door, the player) pushed the capsule: move the crowd agent there too.
                        Fill(_fa, resolved);
                        VortexAPI.NavAgentMovePosition(r.Handle, _fa);
                    }
                    feet = resolved;
                }

                // Face the walking direction.
                float vx = r.State.VelX, vz = r.State.VelZ;
                float speed = (float)Math.Sqrt(vx * vx + vz * vz);
                if (r.Agent.UpdateRotation && speed > 0.1f && r.Agent.AngularSpeed > 0f)
                {
                    float target = (float)(Math.Atan2(vx, vz) * 180.0 / Math.PI);
                    if (!r.YawKnown) { r.Yaw = EntityYaw(r.Entity); r.YawKnown = true; }
                    float d = DeltaAngle(r.Yaw, target);
                    float step = r.Agent.AngularSpeed * dt;
                    r.Yaw = Math.Abs(d) <= step ? target : r.Yaw + Math.Sign(d) * step;
                    WritePose(r.Entity, feet + new SysVec(0f, r.Agent.BaseOffset, 0f), r.Yaw);
                }
                else WritePose(r.Entity, feet + new SysVec(0f, r.Agent.BaseOffset, 0f), null);
                if (r.HasColliders && PhysicsService.IsBuilt && (feet - r.LastFeet).LengthSquared() > 1e-6f) SyncStaticBodies(r.Entity);
                r.LastFeet = feet;
                r.HasLastFeet = true;
                moved = true;

                // Arrival: stopping distance reached (without auto-braking before DetourCrowd's own slow-down radius).
                if (r.HasDestination && r.State.MoveState == 2 && r.State.RemainingDistance >= 0f)
                {
                    float stop = Math.Max(r.Agent.StoppingDistance, 0.05f);
                    if (!r.Agent.AutoBraking) stop = Math.Max(stop, r.Agent.Radius * 2f);
                    if (r.State.RemainingDistance <= stop)
                    {
                        VortexAPI.NavAgentResetTarget(r.Handle);
                        r.HasDestination = false;
                        r.Arrived = true;
                    }
                }
            }
            if (moved) SceneRenderService.RuntimeDirty = true;
        }

        /// <summary>Send an agent to <paramref name="target"/> (asynchronous path request). False when the entity has no
        /// enabled NavAgent, there is no navmesh, or the target is nowhere near it.</summary>
        public static bool SetDestination(GameEntity e, Vector3 target)
        {
            var r = Rec(e, true);
            if (r == null) return false;
            r.Destination = ToSys(target);
            r.HasDestination = true;
            r.Arrived = false;
            if (r.Handle == 0 && IsLoaded && r.Entity.ActiveInHierarchy) AddToCrowd(r, EntityFeet(r));
            if (r.Handle == 0) return false;
            Fill(_fa, target);
            bool ok = VortexAPI.NavAgentSetTarget(r.Handle, _fa) != 0;
            if (!ok) r.HasDestination = false;
            if (ok) { VortexAPI.NavAgentGetState(r.Handle, out r.State); r.StateValid = true; }
            return ok;
        }

        /// <summary>Stop moving (the destination is cleared; the agent slows to a halt).</summary>
        public static void Stop(GameEntity e)
        {
            var r = Rec(e, false);
            if (r == null) return;
            r.HasDestination = false;
            if (r.Handle != 0) VortexAPI.NavAgentResetTarget(r.Handle);
        }

        /// <summary>Freeze / unfreeze an agent in place (keeps its destination; speed 0 while paused).</summary>
        public static void SetPaused(GameEntity e, bool paused)
        {
            var r = Rec(e, true);
            if (r == null || r.Stopped == paused) return;
            r.Stopped = paused;
            r.ParamsDirty = true;
        }

        public static bool IsPaused(GameEntity e) { var r = Rec(e, false); return r != null && r.Stopped; }

        /// <summary>True while the agent walks (has a destination and some speed).</summary>
        public static bool IsMoving(GameEntity e)
        {
            var r = Rec(e, false);
            if (r == null || !r.StateValid) return false;
            float v = r.State.VelX * r.State.VelX + r.State.VelZ * r.State.VelZ;
            return (r.HasDestination && r.State.MoveState != 3) || v > 0.01f;
        }

        public static bool HasDestination(GameEntity e) { var r = Rec(e, false); return r != null && r.HasDestination; }

        /// <summary>True once the agent reached its last destination (until the next SetDestination).</summary>
        public static bool HasArrived(GameEntity e) { var r = Rec(e, false); return r != null && r.Arrived; }

        public static NavPathStatus GetPathStatus(GameEntity e)
        {
            var r = Rec(e, false);
            if (r == null || r.Handle == 0 || !r.StateValid) return NavPathStatus.Invalid;
            switch (r.State.MoveState)
            {
                case 1: return NavPathStatus.Pending;
                case 2: return r.State.Partial != 0 ? NavPathStatus.Partial : NavPathStatus.Complete;
                case 0: return r.Arrived ? NavPathStatus.Complete : NavPathStatus.Invalid;
                default: return NavPathStatus.Invalid;
            }
        }

        /// <summary>Distance left along the path (0 after arrival, -1 without a path).</summary>
        public static float RemainingDistance(GameEntity e)
        {
            var r = Rec(e, false);
            if (r == null || !r.StateValid) return -1f;
            if (r.Arrived && !r.HasDestination) return 0f;
            return r.State.MoveState == 2 ? r.State.RemainingDistance : (r.State.MoveState == 1 ? float.PositiveInfinity : -1f);
        }

        public static Vector3 GetVelocity(GameEntity e)
        {
            var r = Rec(e, false);
            return r != null && r.StateValid ? new Vector3(r.State.VelX, r.State.VelY, r.State.VelZ) : Vector3.Zero;
        }

        public static Vector3 GetDestination(GameEntity e)
        {
            var r = Rec(e, false);
            return r != null && r.HasDestination ? new Vector3(r.Destination.X, r.Destination.Y, r.Destination.Z) : Vector3.Zero;
        }

        /// <summary>The next corner the agent walks towards (its own position without a path).</summary>
        public static Vector3 GetSteeringTarget(GameEntity e)
        {
            var r = Rec(e, false);
            return r != null && r.StateValid ? new Vector3(r.State.CornerX, r.State.CornerY, r.State.CornerZ) : Vector3.Zero;
        }

        /// <summary>Place an agent somewhere else (its path is cleared). False when the point is not on / near the navmesh.</summary>
        public static bool Warp(GameEntity e, Vector3 position)
        {
            var r = Rec(e, true);
            if (r == null) return false;
            var p = ToSys(position);
            WritePose(r.Entity, p + new SysVec(0f, r.Agent.BaseOffset, 0f), null);
            r.LastFeet = p; r.HasLastFeet = true;
            r.HasDestination = false;
            if (r.Handle == 0) { if (IsLoaded) AddToCrowd(r, p); return r.Handle != 0; }
            Fill(_fa, p);
            return VortexAPI.NavAgentTeleport(r.Handle, _fa) != 0;
        }

        /// <summary>Runtime speed (m/s) override; negative = back to the component's Speed. The component is not changed.</summary>
        public static void SetSpeedOverride(GameEntity e, float speed)
        {
            var r = Rec(e, true);
            if (r == null) return;
            r.SpeedOverride = speed;
            r.ParamsDirty = true;
        }

        public static float GetSpeed(GameEntity e)
        {
            var r = Rec(e, false);
            if (r == null) return 0f;
            return r.SpeedOverride >= 0f ? r.SpeedOverride : r.Agent.Speed;
        }

        /// <summary>The agent's remaining path corners (empty without a path).</summary>
        public static List<Vector3> GetAgentPath(GameEntity e)
        {
            var list = new List<Vector3>();
            var r = Rec(e, false);
            if (r == null || r.Handle == 0) return list;
            int n = VortexAPI.NavAgentGetCorners(r.Handle, _pathBuf, _pathBuf.Length / 3);
            for (int i = 0; i < n; i++) list.Add(new Vector3(_pathBuf[i * 3], _pathBuf[i * 3 + 1], _pathBuf[i * 3 + 2]));
            return list;
        }

        /// <summary>True when the entity is a registered (enabled) agent in the running game.</summary>
        public static bool IsAgent(GameEntity e) => Rec(e, false) != null;

        // ---- internals ----

        private static AgentRec Rec(GameEntity e, bool create)
        {
            if (e == null || !IsRunning) return null;
            if (_agents.TryGetValue(e, out var r)) return r.Agent != null && r.Agent.IsEnabled ? r : null;
            if (!create) return null;
            var a = e.GetComponent<NavAgent>();
            if (a == null || !a.IsEnabled) return null;
            return Register(e, a);
        }

        private static AgentRec Register(GameEntity e, NavAgent a)
        {
            var r = new AgentRec { Entity = e, Agent = a };
            try { r.SelfId = Editor.Scripting.ScriptRuntime.Instance.HandleForEntity(e); } catch { r.SelfId = 0; }
            if (r.SelfId == 0) r.SelfId = -1000000 - _agentList.Count;
            r.HasColliders = HasColliderInSubtree(e);
            if (a.ResolveCollisions && r.HasColliders && CollisionService.IsBuilt)
            {
                // A moving agent must not keep a static copy of its own collider in the character world.
                try { CollisionService.RemoveEntityShapes(e); } catch { }
            }
            a.PropertyChanged += OnAgentChanged;
            _agents[e] = r;
            _agentList.Add(r);
            if (IsLoaded && e.ActiveInHierarchy && a.IsEnabled) AddToCrowd(r, EntityFeet(r));
            return r;
        }

        private static void OnAgentChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            var a = sender as NavAgent;
            if (a?.Entity != null && _agents.TryGetValue(a.Entity, out var r)) r.ParamsDirty = true;
        }

        private static void AddToCrowd(AgentRec r, SysVec feet)
        {
            if (!IsLoaded) return;
            var p = ParamsOf(r);
            Fill(_fa, feet);
            r.Handle = VortexAPI.NavAgentAdd(_fa, ref p);
            r.ParamsDirty = false;
            if (r.Handle == 0) return;
            r.LastFeet = feet;
            r.HasLastFeet = true;
            if (r.HasDestination) { Fill(_fb, r.Destination); VortexAPI.NavAgentSetTarget(r.Handle, _fb); }
            VortexAPI.NavAgentGetState(r.Handle, out r.State);
            r.StateValid = true;
        }

        private static NavAgentParams ParamsOf(AgentRec r)
        {
            var a = r.Agent;
            float speed = r.SpeedOverride >= 0f ? r.SpeedOverride : a.Speed;
            float lowPriority = a.AvoidancePriority / 99f;   // 0 = most important .. 1 = gives way most
            return new NavAgentParams
            {
                Radius = a.Radius,
                Height = a.Height,
                MaxSpeed = r.Stopped ? 0f : speed,
                MaxAcceleration = r.Stopped ? 1000f : a.Acceleration,
                SeparationWeight = 0.5f + 3.5f * lowPriority,
                CollisionQueryRange = a.Radius * 12f,
                PathOptimizationRange = a.Radius * 30f,
                AvoidanceQuality = 3,
                UpdateFlags = 0
            };
        }

        /// <summary>Find every NavAgent of the run scene (runtime spawns included) and forget removed ones.</summary>
        private static void Rescan()
        {
            if (_runScene?.Entities == null) return;
            var seen = new HashSet<GameEntity>();
            foreach (var root in _runScene.Entities) Collect(root, seen);
            for (int i = _agentList.Count - 1; i >= 0; i--)
            {
                var r = _agentList[i];
                if (seen.Contains(r.Entity)) continue;
                if (r.Handle != 0) { try { VortexAPI.NavAgentRemove(r.Handle); } catch { } }
                if (r.Agent != null) r.Agent.PropertyChanged -= OnAgentChanged;
                _agents.Remove(r.Entity);
                _agentList.RemoveAt(i);
            }
        }

        private static void Collect(GameEntity e, HashSet<GameEntity> seen)
        {
            if (e == null) return;
            var a = e.GetComponent<NavAgent>();
            if (a != null)
            {
                seen.Add(e);
                if (!_agents.ContainsKey(e)) Register(e, a);
            }
            if (e.Children != null) foreach (var c in e.Children) Collect(c, seen);
        }

        private static bool HasAgents(Scene scene)
        {
            if (scene?.Entities == null) return false;
            bool Walk(GameEntity e)
            {
                if (e == null) return false;
                if (e.GetComponent<NavAgent>() != null) return true;
                if (e.Children != null) foreach (var c in e.Children) if (Walk(c)) return true;
                return false;
            }
            foreach (var e in scene.Entities) if (Walk(e)) return true;
            return false;
        }

        private static bool InRunScene(GameEntity e)
        {
            var top = e;
            while (top.Parent != null) top = top.Parent;
            return _runScene?.Entities != null && _runScene.Entities.Contains(top);
        }

        private static void DropAgentHandles()
        {
            foreach (var r in _agentList) { r.Handle = 0; r.StateValid = false; }
        }

        /// <summary>An agent whose collider became a STATIC physics body (no Rigidbody) would leave that body at its spawn
        /// point: teleport it along so raycasts / bullets hit the agent where it is. (A Kinematic Rigidbody is the better
        /// setup — PhysicsService drives those itself.)</summary>
        private static void SyncStaticBodies(GameEntity e)
        {
            if (e == null) return;
            try
            {
                if (PhysicsService.HasBody(e) && !PhysicsService.HasRigidbody(e)) PhysicsService.RefreshEntity(e);
            }
            catch { }
            if (e.Children != null) foreach (var c in e.Children) SyncStaticBodies(c);
        }

        private static bool HasColliderInSubtree(GameEntity e)
        {
            if (e == null) return false;
            foreach (var c in e.Components) if (c is Collider) return true;
            if (e.Children != null) foreach (var c in e.Children) if (HasColliderInSubtree(c)) return true;
            return false;
        }

        private static SysVec EntityFeet(AgentRec r)
        {
            var w = Animation.BoneSocketService.EntityWorld(r.Entity).Translation;
            return new SysVec(w.X, w.Y - r.Agent.BaseOffset, w.Z);
        }

        internal static float EntityYaw(GameEntity e)
        {
            var m = Animation.BoneSocketService.EntityWorld(e);
            var f = SysVec.TransformNormal(SysVec.UnitZ, m);
            if (f.X * f.X + f.Z * f.Z < 1e-8f) return 0f;
            return (float)(Math.Atan2(f.X, f.Z) * 180.0 / Math.PI);
        }

        private static float DeltaAngle(float from, float to)
        {
            float d = (to - from) % 360f;
            if (d > 180f) d -= 360f; else if (d < -180f) d += 360f;
            return d;
        }

        /// <summary>World position (+ optional world yaw, pitch / roll cleared) -> the entity's local transform.</summary>
        internal static void WritePose(GameEntity e, SysVec worldPos, float? yawDeg)
        {
            var t = e?.Transform;
            if (t == null) return;
            SysMat desired;
            if (yawDeg.HasValue) desired = Animation.BoneSocketService.EulerZXY(new SysVec(0f, yawDeg.Value, 0f));
            else desired = Animation.BoneSocketService.NormalizeBasis(Animation.BoneSocketService.EntityWorld(e));
            desired.M41 = worldPos.X; desired.M42 = worldPos.Y; desired.M43 = worldPos.Z; desired.M44 = 1f;
            var local = desired;
            var parentWorld = Animation.BoneSocketService.EntityWorld(e.Parent);
            if (SysMat.Invert(parentWorld, out var inv)) local = desired * inv;
            var lp = local.Translation;
            var cp = t.LocalPosition;
            if (Math.Abs(cp.X - lp.X) > 1e-5f || Math.Abs(cp.Y - lp.Y) > 1e-5f || Math.Abs(cp.Z - lp.Z) > 1e-5f)
                t.LocalPosition = new Vector3(lp.X, lp.Y, lp.Z);
            if (yawDeg.HasValue)
            {
                var euler = Animation.BoneSocketService.ToEulerZXY(Animation.BoneSocketService.NormalizeBasis(local));
                var cr = t.LocalRotation;
                if (Math.Abs(DeltaAngle(cr.X, euler.X)) > 1e-3f || Math.Abs(DeltaAngle(cr.Y, euler.Y)) > 1e-3f || Math.Abs(DeltaAngle(cr.Z, euler.Z)) > 1e-3f)
                    t.LocalRotation = new Vector3(euler.X, euler.Y, euler.Z);
            }
        }

        // ================================================================================================ debug drawing

        /// <summary>Draw the navmesh surface (a tinted wire net through the gizmo pass) — the Navigation window's toggle.</summary>
        public static bool ShowNavMesh;
        /// <summary>Draw the agents' remaining paths in play mode.</summary>
        public static bool ShowAgentPaths;

        private static long _debugMesh = ID.INVALID_ID;
        private static int _debugMeshVersion = -1;

        /// <summary>Submit the navmesh overlay (when <see cref="ShowNavMesh"/> is on) and, in play, the agents' paths
        /// (<see cref="ShowAgentPaths"/>). Call once per frame before the renderer draws (UI / game thread).</summary>
        public static void SubmitDebugDraw()
        {
            try
            {
                if (ShowNavMesh) SubmitNavMeshSurface();
                if (ShowAgentPaths && IsRunning) SubmitAgentPaths();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Navigation] debug draw: " + ex.Message); }
        }

        private static void SubmitNavMeshSurface()
        {
            if (!IsLoaded) return;
            if (_debugMeshVersion != _version || _debugMesh == ID.INVALID_ID)
            {
                if (_debugMesh != ID.INVALID_ID) { try { VortexAPI.DeleteMesh(_debugMesh); } catch { } }
                _debugMesh = VortexAPI.NavCreateDebugMesh(0.05f);
                _debugMeshVersion = _version;
            }
            if (_debugMesh == ID.INVALID_ID) return;
            long mat = VortexAPI.NavigationGizmoMaterial(0.15f, 0.85f, 0.75f);
            if (mat != ID.INVALID_ID) VortexAPI.SubmitGizmoWireForRendering(_debugMesh, mat, null);
        }

        private static void SubmitAgentPaths()
        {
            var seg = new List<float>();
            foreach (var r in _agentList)
            {
                if (r.Handle == 0 || !r.StateValid) continue;
                int n = VortexAPI.NavAgentGetCorners(r.Handle, _pathBuf, 32);
                float px = r.State.PosX, py = r.State.PosY + 0.1f, pz = r.State.PosZ;
                for (int i = 0; i < n; i++)
                {
                    float x = _pathBuf[i * 3], y = _pathBuf[i * 3 + 1] + 0.1f, z = _pathBuf[i * 3 + 2];
                    seg.Add(px); seg.Add(py); seg.Add(pz); seg.Add(x); seg.Add(y); seg.Add(z);
                    px = x; py = y; pz = z;
                }
            }
            if (seg.Count >= 6) VortexAPI.RenderNavigationLines(seg.ToArray(), seg.Count, 1f, 0.85f, 0.2f, 0.03f, 96);
        }

        /// <summary>Navmesh edges as line segments (6 floats each) for a viewport gizmo layer: flags bit0 = boundary edges,
        /// bit1 = internal polygon edges. Empty without a navmesh.</summary>
        public static float[] GetNavMeshDebugLines(int flags = 1)
        {
            if (!IsLoaded) return new float[0];
            int n = VortexAPI.NavGetDebugLines(null, 0, flags);
            if (n <= 0) return new float[0];
            var buf = new float[n];
            int got = VortexAPI.NavGetDebugLines(buf, n, flags);
            if (got != n) Array.Resize(ref buf, Math.Max(0, got));
            return buf;
        }

        /// <summary>The navmesh surface as world-space triangles (9 floats each).</summary>
        public static float[] GetNavMeshDebugTriangles()
        {
            if (!IsLoaded) return new float[0];
            int n = VortexAPI.NavGetDebugTriangles(null, 0);
            if (n <= 0) return new float[0];
            var buf = new float[n];
            int got = VortexAPI.NavGetDebugTriangles(buf, n);
            if (got != n) Array.Resize(ref buf, Math.Max(0, got));
            return buf;
        }

        // ================================================================================================ helpers

        private static void Fill(float[] a, Vector3 v) { a[0] = v.X; a[1] = v.Y; a[2] = v.Z; }
        private static void Fill(float[] a, SysVec v) { a[0] = v.X; a[1] = v.Y; a[2] = v.Z; }
        private static SysVec ToSys(Vector3 v) => new SysVec(v.X, v.Y, v.Z);

        internal static void Log(string msg, bool warning = false)
        {
            System.Diagnostics.Debug.WriteLine(msg);
            try { if (warning) ConsoleService.Instance.LogWarning(msg); else ConsoleService.Instance.Log(msg); } catch { }
        }

        // ================================================================================================ bake input

        /// <summary>Walks the entity tree and flattens static geometry into world space.</summary>
        private sealed class GeometryBuilder
        {
            private readonly NavGeometrySource _src;
            private readonly List<float> _verts = new List<float>(4096);
            private readonly List<int> _tris = new List<int>(4096);
            private readonly List<float> _boxes = new List<float>(256);
            private readonly HashSet<GameEntity> _contributors = new HashSet<GameEntity>();
            private readonly NavBakeGeometry _out = new NavBakeGeometry();
            private readonly Dictionary<string, float[]> _modelCache = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);

            public GeometryBuilder(NavGeometrySource src) { _src = src; }

            public void Visit(GameEntity e)
            {
                if (e == null || !e.IsActive) return;
                if (Excluded(e)) return;
                var terrain = e.GetComponent<Editor.ECS.Components.Rendering.Terrain>();
                if (terrain != null && terrain.IsEnabled && terrain.Collision && terrain.Navigation)
                {
                    // #124: the terrain's surface (every sample up to 257², coarser beyond) is level geometry
                    float cell;
                    var raw = Editor.Core.Services.Terrain.TerrainService.CollisionTriangles(e, out cell);
                    if (raw != null && raw.Length >= 9)
                    {
                        SysVec tpos; SysQuat trot;
                        Editor.Core.Services.Terrain.TerrainService.Pose(e, out tpos, out trot);
                        SysMat tworld = SysMat.CreateFromQuaternion(trot) * SysMat.CreateTranslation(tpos);
                        for (int i = 0; i + 8 < raw.Length; i += 9)
                            AddTriangle(tworld, new SysVec(raw[i], raw[i + 1], raw[i + 2]), new SysVec(raw[i + 3], raw[i + 4], raw[i + 5]), new SysVec(raw[i + 6], raw[i + 7], raw[i + 8]));
                        _contributors.Add(e);
                    }
                    if (e.Children != null) foreach (var c in e.Children) Visit(c);
                    return;
                }
                var waterComp = e.GetComponent<Editor.ECS.Components.Rendering.Water>();
                if (waterComp != null && waterComp.IsEnabled)
                {
                    // #200: the deep part of a lake blocks the navmesh — agents wade the shallows, not the lake bed
                    var deep = Editor.Core.Services.Water.WaterService.DeepBoxes(e);
                    foreach (var b in deep)
                        AddBox(SysMat.Identity, new SysVec(b.centre.X, b.centre.Y, b.centre.Z), new SysVec(b.half.X, b.half.Y, b.half.Z), SysQuat.Identity);
                    if (deep.Count > 0) _contributors.Add(e);
                }
                var foliageComp = e.GetComponent<Editor.ECS.Components.Rendering.Foliage>();
                if (foliageComp != null && foliageComp.IsEnabled)
                {
                    // #125: the colliding foliage (trunks) blocks the navmesh as boxes
                    var cols = Editor.Core.Services.Foliage.FoliageService.Collidables(e);
                    if (cols.Count > 0)
                    {
                        foreach (var c in cols)
                            AddBox(SysMat.Identity, new SysVec(c.Base.X, c.Base.Y + c.Height * 0.5f, c.Base.Z), new SysVec(c.Radius, c.Height * 0.5f, c.Radius), SysQuat.Identity);
                        _contributors.Add(e);
                    }
                    if (e.Children != null) foreach (var c in e.Children) Visit(c);
                    return;
                }
                bool hadCollider = false;
                if ((_src & NavGeometrySource.Colliders) != 0)
                {
                    SysMat world = default(SysMat);
                    bool haveWorld = false;
                    foreach (var c in e.Components)
                    {
                        if (!(c is Collider col) || !col.IsEnabled) continue;
                        if (col.IsTrigger) { hadCollider = true; continue; }   // triggers never block, but the entity is "collided"
                        if (!haveWorld) { world = Animation.BoneSocketService.EntityWorld(e); haveWorld = true; }
                        if (AddCollider(e, col, world)) { _contributors.Add(e); }
                        hadCollider = true;
                    }
                }
                if (!hadCollider)
                {
                    bool wantMesh = (_src & NavGeometrySource.AllRenderMeshes) != 0 || ((_src & NavGeometrySource.StaticRenderMeshes) != 0 && e.IsStatic);
                    var mr = wantMesh ? e.GetComponent<MeshRenderer>() : null;
                    if (mr != null && mr.IsEnabled && !string.IsNullOrEmpty(mr.MeshPath))
                    {
                        if (AddMesh(e, mr.MeshPath, SysVec.Zero, Animation.BoneSocketService.EntityWorld(e))) _contributors.Add(e);
                    }
                }
                if (e.Children != null) foreach (var c in e.Children) Visit(c);
            }

            /// <summary>Moving things and characters are not level geometry.</summary>
            private static bool Excluded(GameEntity e)
            {
                if (string.Equals(e.Tag, IgnoreTag, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(e.Tag, "Player", StringComparison.OrdinalIgnoreCase)) return true;
                foreach (var c in e.Components)
                {
                    if (c is NavAgent || c is Animator) return true;
                    if (c is Rigidbody rb && rb.IsEnabled && rb.BodyType != RigidbodyType.Static) return true;
                    if (c is PhysicsJoint j && j.IsEnabled) return true;
                }
                return false;
            }

            private bool AddCollider(GameEntity e, Collider col, SysMat world)
            {
                var center = new SysVec(col.Center.X, col.Center.Y, col.Center.Z);
                switch (col)
                {
                    case BoxCollider box:
                        AddBox(world, center, new SysVec(Math.Abs(box.Size.X) * 0.5f, Math.Abs(box.Size.Y) * 0.5f, Math.Abs(box.Size.Z) * 0.5f), SysQuat.Identity);
                        return true;
                    case SphereCollider sph:
                        {
                            // A sphere as its (rotated) bounding cube in the entity frame, radius scaled by the largest axis.
                            var sc = ScaleOf(world);
                            float r = Math.Abs(sph.Radius) * Math.Max(sc.X, Math.Max(sc.Y, sc.Z));
                            AddBoxWorldHalf(world, center, new SysVec(r, r, r));
                            return true;
                        }
                    case CapsuleCollider cap:
                        {
                            float r = Math.Abs(cap.Radius), h = Math.Max(Math.Abs(cap.Height), 2f * r) * 0.5f;
                            var half = cap.Direction == 0 ? new SysVec(h, r, r) : cap.Direction == 2 ? new SysVec(r, r, h) : new SysVec(r, h, r);
                            AddBox(world, center, half, SysQuat.Identity);
                            return true;
                        }
                    case MeshCollider mc:
                        {
                            string path = !string.IsNullOrEmpty(mc.MeshPath) ? mc.MeshPath : e.GetComponent<MeshRenderer>()?.MeshPath;
                            if (string.IsNullOrEmpty(path)) { Note(e, "Mesh Collider without a mesh — skipped"); return false; }
                            return AddMesh(e, path, center, world);
                        }
                    default:
                        {
                            // A plain Collider of type Mesh/Convex: the render mesh.
                            string path = e.GetComponent<MeshRenderer>()?.MeshPath;
                            return !string.IsNullOrEmpty(path) && AddMesh(e, path, center, world);
                        }
                }
            }

            private bool AddMesh(GameEntity e, string meshPath, SysVec localOffset, SysMat world)
            {
                if (meshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
                {
                    switch (meshPath.Substring("Primitive:".Length).ToLowerInvariant())
                    {
                        case "plane":
                        case "quad":
                            {
                                // One-sided unit quad in XZ at y = 0, walkable side up.
                                var a = new SysVec(-0.5f, 0f, -0.5f) + localOffset; var b = new SysVec(-0.5f, 0f, 0.5f) + localOffset;
                                var c = new SysVec(0.5f, 0f, 0.5f) + localOffset; var d = new SysVec(0.5f, 0f, -0.5f) + localOffset;
                                AddTriangle(world, a, b, c);
                                AddTriangle(world, a, c, d);
                                return true;
                            }
                        case "cube":
                        case "sphere":
                        case "cylinder":
                        case "capsule":
                        case "cone":
                        case "torus":
                            AddBox(world, localOffset, new SysVec(0.5f, 0.5f, 0.5f), SysQuat.Identity);
                            return true;
                        default:
                            Note(e, "unknown primitive '" + meshPath + "' — skipped");
                            return false;
                    }
                }
                var tris = ModelTriangles(meshPath);
                if (tris == null || tris.Length < 9) { Note(e, "no triangles for '" + meshPath + "' — skipped"); return false; }
                for (int i = 0; i + 8 < tris.Length; i += 9)
                {
                    AddTriangle(world,
                        new SysVec(tris[i], tris[i + 1], tris[i + 2]) + localOffset,
                        new SysVec(tris[i + 3], tris[i + 4], tris[i + 5]) + localOffset,
                        new SysVec(tris[i + 6], tris[i + 7], tris[i + 8]) + localOffset);
                }
                return true;
            }

            private void AddTriangle(SysMat world, SysVec a, SysVec b, SysVec c)
            {
                var wa = SysVec.Transform(a, world); var wb = SysVec.Transform(b, world); var wc = SysVec.Transform(c, world);
                bool mirrored = Det3(world) < 0f;   // a mirroring transform flips the winding: keep the walkable side up
                int i0 = _verts.Count / 3;
                Push(wa); Push(mirrored ? wc : wb); Push(mirrored ? wb : wc);
                _tris.Add(i0); _tris.Add(i0 + 1); _tris.Add(i0 + 2);
            }

            /// <summary>Oriented box: local centre / half extents (scaled by the entity) and a local rotation.</summary>
            private void AddBox(SysMat world, SysVec localCenter, SysVec localHalf, SysQuat localRot)
            {
                var sc = ScaleOf(world);
                var rot = RotationOf(world);
                var wc = SysVec.Transform(localCenter, world);
                var half = new SysVec(Math.Max(0.005f, localHalf.X * sc.X), Math.Max(0.005f, localHalf.Y * sc.Y), Math.Max(0.005f, localHalf.Z * sc.Z));
                var q = SysQuat.Normalize(SysQuat.Concatenate(localRot, rot));
                _boxes.Add(wc.X); _boxes.Add(wc.Y); _boxes.Add(wc.Z);
                _boxes.Add(half.X); _boxes.Add(half.Y); _boxes.Add(half.Z);
                _boxes.Add(q.X); _boxes.Add(q.Y); _boxes.Add(q.Z); _boxes.Add(q.W);
            }

            /// <summary>Box whose half extents are already in world units (spheres).</summary>
            private void AddBoxWorldHalf(SysMat world, SysVec localCenter, SysVec worldHalf)
            {
                var rot = RotationOf(world);
                var wc = SysVec.Transform(localCenter, world);
                _boxes.Add(wc.X); _boxes.Add(wc.Y); _boxes.Add(wc.Z);
                _boxes.Add(Math.Max(0.005f, worldHalf.X)); _boxes.Add(Math.Max(0.005f, worldHalf.Y)); _boxes.Add(Math.Max(0.005f, worldHalf.Z));
                _boxes.Add(rot.X); _boxes.Add(rot.Y); _boxes.Add(rot.Z); _boxes.Add(rot.W);
            }

            private void Push(SysVec v) { _verts.Add(v.X); _verts.Add(v.Y); _verts.Add(v.Z); }

            private float[] ModelTriangles(string meshPath)
            {
                if (_modelCache.TryGetValue(meshPath, out var cached)) return cached;
                float[] tris = null;
                try { tris = CollisionService.MeshTriangleProvider?.Invoke(meshPath); } catch { }
                if (tris == null)
                {
                    try
                    {
                        var actual = meshPath;
                        int h = actual.LastIndexOf('#'); if (h > 0) actual = actual.Substring(0, h);
                        var proj = ProjectData.Current?.Path;
                        var abs = Path.IsPathRooted(actual) ? actual : (proj != null ? Path.Combine(proj, actual) : actual);
                        var ext = Path.GetExtension(actual)?.TrimStart('.');
                        if (AssetVfs.IsMounted && AssetVfs.TryGetBytes(abs, out var bytes) && bytes != null) tris = VortexAPI.GetModelTrianglesFromMemory(bytes, ext);
                        else if (File.Exists(abs)) tris = VortexAPI.GetModelTriangles(abs);
                    }
                    catch { }
                }
                _modelCache[meshPath] = tris;
                return tris;
            }

            private void Note(GameEntity e, string text)
            {
                if (_out.Notes.Count < 50) _out.Notes.Add("'" + (e?.Name ?? "?") + "': " + text);
            }

            public NavBakeGeometry Finish()
            {
                _out.Verts = _verts.ToArray();
                _out.Tris = _tris.ToArray();
                _out.TriangleCount = _tris.Count / 3;
                _out.Boxes = _boxes.ToArray();
                _out.BoxCount = _boxes.Count / 10;
                _out.EntityCount = _contributors.Count;
                return _out;
            }

            private static SysVec ScaleOf(SysMat m)
                => new SysVec(new SysVec(m.M11, m.M12, m.M13).Length(), new SysVec(m.M21, m.M22, m.M23).Length(), new SysVec(m.M31, m.M32, m.M33).Length());

            /// <summary>The entity's world rotation. A mirroring scale is folded out (boxes are symmetric, so un-mirroring
            /// one axis keeps the shape and yields a proper rotation).</summary>
            private static SysQuat RotationOf(SysMat world)
            {
                var b = Animation.BoneSocketService.NormalizeBasis(world);
                if (Det3(b) < 0f) { b.M11 = -b.M11; b.M12 = -b.M12; b.M13 = -b.M13; }
                return SysQuat.Normalize(SysQuat.CreateFromRotationMatrix(b));
            }

            private static float Det3(SysMat m)
                => m.M11 * (m.M22 * m.M33 - m.M23 * m.M32) - m.M12 * (m.M21 * m.M33 - m.M23 * m.M31) + m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);
        }
    }
}
