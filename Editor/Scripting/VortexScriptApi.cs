// The Vortex scripting API. Gameplay scripts (Assets/Scripts/*.cs) derive from VortexBehaviour and
// are compiled + run by the engine on Play (see ScriptRuntime). This API lives in the editor
// assembly so behaviours can actually affect the running game (move their entity, read input, etc.).
// The engine wires the host implementation at runtime; scripts only see the public surface below.
using System;
using System.Collections.Generic;

namespace Vortex
{
    public struct Vector3
    {
        public float X, Y, Z;
        public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static Vector3 Zero => new Vector3(0f, 0f, 0f);
        public static Vector3 One => new Vector3(1f, 1f, 1f);
        public static Vector3 Up => new Vector3(0f, 1f, 0f);
        public static Vector3 Forward => new Vector3(0f, 0f, 1f);

        /// <summary>Vector length.</summary>
        public float Length { get { return (float)Math.Sqrt(X * X + Y * Y + Z * Z); } }

        /// <summary>Unit-length copy (Zero stays Zero).</summary>
        public Vector3 Normalized
        {
            get
            {
                float l = Length;
                return l > 1e-8f ? new Vector3(X / l, Y / l, Z / l) : Zero;
            }
        }

        public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static Vector3 operator -(Vector3 a) { return new Vector3(-a.X, -a.Y, -a.Z); }
        public static Vector3 operator *(Vector3 a, float s) { return new Vector3(a.X * s, a.Y * s, a.Z * s); }
        public static Vector3 operator *(float s, Vector3 a) { return new Vector3(a.X * s, a.Y * s, a.Z * s); }
        public static float Dot(Vector3 a, Vector3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        public static Vector3 Cross(Vector3 a, Vector3 b) { return new Vector3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X); }
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) { return new Vector3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t); }
        public static float Distance(Vector3 a, Vector3 b) { return (a - b).Length; }
        public override string ToString() { return "(" + X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ", " + Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ", " + Z.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ")"; }
    }

    /// <summary>
    /// A rotation for gameplay math — the same convention the engine uses everywhere (Transform.Rotation, bone
    /// sockets, SetWorldPose): <see cref="FromEuler"/>/<see cref="ToEuler"/> take engine Euler degrees
    /// (X = pitch, Y = yaw, Z = roll, applied Z·X·Y). <c>a * b</c> rotates by <c>b</c> FIRST, then by <c>a</c>
    /// (so <c>parent * child</c> takes a child-local rotation to the parent's frame). <see cref="Rotate"/>
    /// applies the rotation to a vector. Built on the engine's own matrix math, so results match what renders.
    /// </summary>
    public struct Quaternion
    {
        public float X, Y, Z, W;
        public Quaternion(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public static Quaternion Identity { get { return new Quaternion(0f, 0f, 0f, 1f); } }

        private System.Numerics.Quaternion Sys { get { return new System.Numerics.Quaternion(X, Y, Z, W); } }
        private static Quaternion From(System.Numerics.Quaternion q) { q = System.Numerics.Quaternion.Normalize(q); return new Quaternion(q.X, q.Y, q.Z, q.W); }

        /// <summary>Rotation from engine Euler degrees (pitch X, yaw Y, roll Z).</summary>
        public static Quaternion FromEuler(Vector3 eulerDeg)
        {
            var m = Editor.Core.Animation.BoneSocketService.EulerZXY(new System.Numerics.Vector3(eulerDeg.X, eulerDeg.Y, eulerDeg.Z));
            return From(System.Numerics.Quaternion.CreateFromRotationMatrix(m));
        }
        public static Quaternion FromEuler(float pitchDeg, float yawDeg, float rollDeg) { return FromEuler(new Vector3(pitchDeg, yawDeg, rollDeg)); }

        /// <summary>Rotation of <paramref name="degrees"/> around an axis.</summary>
        public static Quaternion FromAxisAngle(Vector3 axis, float degrees)
        {
            var a = axis.Normalized;
            return From(System.Numerics.Quaternion.CreateFromAxisAngle(new System.Numerics.Vector3(a.X, a.Y, a.Z), degrees * (float)(Math.PI / 180.0)));
        }

        /// <summary>Engine Euler degrees (pitch X, yaw Y, roll Z) of this rotation — feed straight into Rotation / SetWorldPose.</summary>
        public Vector3 ToEuler()
        {
            var m = System.Numerics.Matrix4x4.CreateFromQuaternion(System.Numerics.Quaternion.Normalize(Sys));
            var e = Editor.Core.Animation.BoneSocketService.ToEulerZXY(m);
            return new Vector3(e.X, e.Y, e.Z);
        }

        /// <summary>Apply the rotation to a vector.</summary>
        public Vector3 Rotate(Vector3 v)
        {
            var r = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(v.X, v.Y, v.Z), System.Numerics.Quaternion.Normalize(Sys));
            return new Vector3(r.X, r.Y, r.Z);
        }

        /// <summary>The opposite rotation.</summary>
        public Quaternion Inverse { get { return From(System.Numerics.Quaternion.Inverse(Sys)); } }
        public Quaternion Normalized { get { return From(Sys); } }

        /// <summary>a * b = rotate by b first, then by a.</summary>
        public static Quaternion operator *(Quaternion a, Quaternion b)
        {
            // System.Numerics' product applies the LEFT operand first; swap so a*b reads like matrices (b, then a).
            return From(System.Numerics.Quaternion.Concatenate(b.Sys, a.Sys));
        }

        /// <summary>Spherical interpolation (t 0..1).</summary>
        public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
        {
            if (t <= 0f) return a; if (t >= 1f) return b;
            return From(System.Numerics.Quaternion.Slerp(a.Sys, b.Sys, t));
        }

        /// <summary>Angle between two rotations in degrees.</summary>
        public static float Angle(Quaternion a, Quaternion b)
        {
            // Relative rotation, then a numerically stable half-angle (acos of a dot near 1 loses precision).
            var d = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.Concatenate(System.Numerics.Quaternion.Inverse(a.Sys), b.Sys));
            double s = Math.Sqrt((double)d.X * d.X + (double)d.Y * d.Y + (double)d.Z * d.Z);
            return (float)(2.0 * Math.Atan2(s, Math.Abs(d.W)) * 180.0 / Math.PI);
        }

        /// <summary>A rotation whose +Z axis points along <paramref name="forward"/> with <paramref name="up"/> as the up hint.</summary>
        public static Quaternion LookRotation(Vector3 forward, Vector3 up)
        {
            Vector3 f = forward.Normalized; if (f.Length < 1e-6f) return Identity;
            Vector3 r = Vector3.Cross(up, f).Normalized;
            if (r.Length < 1e-6f) r = Vector3.Cross(new Vector3(0f, 0f, 1f), f).Normalized;
            Vector3 u = Vector3.Cross(f, r);
            // Row-vector basis matrix: rows = the rotated X, Y, Z axes.
            var m = new System.Numerics.Matrix4x4(r.X, r.Y, r.Z, 0f, u.X, u.Y, u.Z, 0f, f.X, f.Y, f.Z, 0f, 0f, 0f, 0f, 1f);
            return From(System.Numerics.Quaternion.CreateFromRotationMatrix(m));
        }

        public Vector3 Forward { get { return Rotate(new Vector3(0f, 0f, 1f)); } }
        public Vector3 Up { get { return Rotate(new Vector3(0f, 1f, 0f)); } }
        public Vector3 Right { get { return Rotate(new Vector3(1f, 0f, 0f)); } }
        public override string ToString() { return ToEuler().ToString(); }
    }

    /// <summary>A UI color (0..1 channels). Use Rgb/Rgba helpers for 0..255 values.</summary>
    public struct Color
    {
        public float R, G, B, A;
        public Color(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; }
        public static Color Rgb(int r, int g, int b) { return new Color(r / 255f, g / 255f, b / 255f, 1f); }
        public static Color Rgba(int r, int g, int b, int a) { return new Color(r / 255f, g / 255f, b / 255f, a / 255f); }
        public Color WithAlpha(float a) { return new Color(R, G, B, a); }
    }

    /// <summary>Implemented by the engine (ScriptRuntime); lets behaviours touch the live game.</summary>
    public interface IScriptHost
    {
        Vector3 GetPosition(long entityId);
        Vector3 GetScale(long entityId);
        void SetScale(long entityId, Vector3 scale);
        void SetPosition(long entityId, Vector3 position);
        Vector3 GetRotation(long entityId);
        void SetRotation(long entityId, Vector3 eulerDegrees);
        // WORLD-space pose write, parent-safe (converts to the entity's local frame; scale untouched) —
        // Set/GetPosition/Rotation are LOCAL values, which breaks for children of moved/rotated parents.
        void SetEntityWorldPose(long entityId, Vector3 position, Vector3 rotationEulerDeg);
        bool TryGetEntityWorldPose(long entityId, out Vector3 position, out Vector3 rotationEulerDeg);
        // Force a render layer (0 world / 1 FP viewmodel / 2 third-person only) onto an entity's
        // MeshRenderers, recursively over its subtree — e.g. a runtime-spawned weapon copy for the
        // 3P body (layer 2) vs the FP viewmodel copy (layer 1).
        void SetRenderLayer(long entityId, int layer);
        bool GetKey(string key);
        bool GetKeyDown(string key);
        bool GetKeyUp(string key);

        // Collide-and-slide a character capsule (feet, radius, height) against the scene's colliders.
        // selfId registers this character so other characters can't walk through it (0 = anonymous).
        Vector3 MoveCharacter(Vector3 feet, float radius, float height, Vector3 move, out bool grounded, long selfId);

        // Ray straight down from `origin` (up to maxDist) against the world colliders — returns the surface entity's
        // Tag (the material) or "" if nothing is below. The "what am I standing on?" query for footsteps.
        string GroundTag(Vector3 origin, float maxDist);

        // Like GroundTag but returns the surface's MATERIAL name (from the hit entity's MeshRenderer material) —
        // the scalable, tag-free way to drive surface-aware audio (map material -> sound once, works everywhere).
        string GroundMaterial(Vector3 origin, float maxDist);

        // Returns the FOOTSTEP SOUND assigned to the surface's material in the Material Editor (a project-relative
        // clip / .vsndc path), or "" — footsteps authored entirely in the editor, no per-material script dictionary.
        string GroundStepSound(Vector3 origin, float maxDist);

        // Rigid-body physics (#100, Jolt): act on an entity's simulated body (Collider + Rigidbody). Every call
        // returns false / zero when the entity has no dynamic body or the engine build has no physics.
        bool PhysicsAddForce(long entityId, Vector3 force);
        bool PhysicsAddForceAtPoint(long entityId, Vector3 force, Vector3 worldPoint);
        bool PhysicsAddImpulse(long entityId, Vector3 impulse);
        bool PhysicsAddImpulseAtPoint(long entityId, Vector3 impulse, Vector3 worldPoint);
        bool PhysicsAddTorque(long entityId, Vector3 torque);
        bool PhysicsSetVelocity(long entityId, Vector3 velocity);
        Vector3 PhysicsGetVelocity(long entityId);
        bool PhysicsSetAngularVelocity(long entityId, Vector3 velocity);
        Vector3 PhysicsGetAngularVelocity(long entityId);
        bool PhysicsSetKinematic(long entityId, bool kinematic);
        bool PhysicsWakeUp(long entityId);
        bool PhysicsIsSleeping(long entityId);
        void PhysicsSetGravity(Vector3 gravity);
        long[] PhysicsOverlapSphere(Vector3 center, float radius);
        bool PhysicsHasRigidbody(long entityId);
        float PhysicsGetMass(long entityId);

        // Request switching the active scene by name (deferred — applied by the runtime after this tick).
        void LoadScene(string name);

        // Mouse mode: locked = captured + hidden for mouse-look (gameplay); unlocked = free cursor for UI.
        bool GetCursorLocked();
        void SetCursorLocked(bool locked);

        // Quit the whole game (closes the player / stops play).
        void QuitGame();

        // Set the player camera's vertical field of view (degrees).
        void SetCameraFov(float fovDegrees);

        // Set the first-person viewmodel layer's own FOV (degrees, default 54) — #175.
        void SetViewmodelFov(float fovDegrees);

        // Set an entity's base color at runtime (e.g. change color when a trigger is touched).
        void SetEntityColor(long entityId, float r, float g, float b);
        void SetEntityMaterial(long entityId, string materialPath);
        string GetEntityMaterial(long entityId);

        // Skeletal animation: play a clip on an entity's Animator (clip = table name or .vanim path);
        // fade > 0 crossfades from the current pose. State machines are game logic — build them in scripts.
        bool PlayAnimation(long entityId, string clip, float fade);
        void StopAnimation(long entityId);
        void SetAnimationSpeed(long entityId, float speed);
        bool IsAnimationPlaying(long entityId, string clip);
        float GetAnimationTime(long entityId);

        // Bone-masked animation layers: base clip (full body) + override layers restricted to a bone
        // mask ("Spine1+") — aim a pistol while the legs keep walking. weight blends the layer.
        bool PlayLayeredAnimation(long entityId, string clip, int layer, string mask, float weight, float fade);
        void SetAnimationLayerWeight(long entityId, int layer, float weight);
        void StopLayeredAnimation(long entityId, int layer);

        // Runtime procedural bone control (#178): compose an additive LOCAL rotation delta onto one bone every
        // frame (aim-offset spine pitch, lean, recoil) so it carries all descendants — the gun stays in the hands
        // at any aim angle. (0,0,0) clears the bone; ClearBoneOverrides drops all of an entity's overrides.
        void SetBoneAdditiveRotation(long entityId, string bone, Vector3 eulerDeg);
        void SetBoneScaleOverride(long entityId, string bone, float scale);
        void SetBoneHidden(long entityId, string bone, bool hidden, bool includeDescendants);
        void ClearBoneOverrides(long entityId);

        // Runtime two-bone IK (#179): blend the TwoBoneIk chain(s) on an entity (0 = animation only,
        // 1 = full IK). tipBone selects one chain; null/empty hits every chain on the entity.
        void SetIkWeight(long entityId, string tipBone, float weight);
        // Script world-space IK targets: the chain (selected by tip bone) reaches a WORLD position (optionally
        // with an orientation) until cleared — the support hand on a mag well, a hand on a handle.
        void SetIkWorldTarget(long entityId, string tipBone, Vector3 worldPos, Vector3 worldRotEuler, bool hasRotation);
        void ClearIkWorldTarget(long entityId, string tipBone);
        void SetIkPoleAngle(long entityId, string tipBone, float degrees);

        // Rig-aware procedural layers (#147) — any skeleton, bones auto-detected: runtime finger poses (preset name or
        // explicit curls, blended in), look-at targets (world point or another entity) + weight, foot-IK weight.
        bool SetHandPose(long entityId, string side, string preset, float weight, float blendSeconds);
        bool SetHandPoseCurls(long entityId, string side, Vector3 index, Vector3 middle, Vector3 ring, Vector3 pinky, Vector3 thumb,
                              float spread, float weight, float blendSeconds);
        void ClearHandPose(long entityId, string side);
        void SetLookAtPoint(long entityId, Vector3 worldPoint);
        void SetLookAtEntity(long entityId, long targetEntityId);
        void ClearLookAtTarget(long entityId);
        void SetLookAtWeight(long entityId, float weight);
        void SetFootIkWeight(long entityId, float weight);

        // Camera/attachment feel primitives: spring-damper impulses + seeded noise channels composed
        // onto the game camera (transform untouched) and onto socket offsets (weapon kicks in the hand).
        void CameraFxKick(Vector3 rotationDegrees, Vector3 position);
        void CameraFxKickEntity(long entityId, Vector3 rotationDegrees, Vector3 position);
        void CameraFxSway(int slot, float positionAmplitude, float rotationAmplitudeDeg, float frequencyHz);
        void CameraFxSwayEntity(long entityId, int slot, float positionAmplitude, float rotationAmplitudeDeg, float frequencyHz);
        void CameraFxSpring(float stiffness, float damping);
        void CameraFxSeed(int seed);

        // Synced playback groups: N entities' clips frame-locked to one master clock (character
        // reload + weapon reload as one). Returns a group id; 0 = nothing started.
        int PlaySyncedAnimation(long[] entities, string[] clips, float speed, float fade);
        void PauseSyncedAnimation(int groupId, bool paused);
        void SetSyncedAnimationSpeed(int groupId, float speed);
        void StopSyncedAnimation(int groupId);

        // Bone sockets: attach an entity to a skeleton bone at runtime (weapon pickup), detach it
        // (keepWorldPosition true = stays where the hand left it), query a bone's world transform
        // (muzzle raycast origins, VFX spawn points), and list a skeleton's current attachments.
        bool AttachEntityToBone(long entityId, long targetId, string bone, Vector3 offsetPos, Vector3 offsetRotEuler);
        bool DetachEntityFromBone(long entityId, bool keepWorldPosition);
        bool TryGetBoneTransform(long targetId, string bone, out Vector3 position, out Vector3 rotationEuler);
        long[] GetAttachedEntities(long targetId);
        // Compose a bone-LOCAL offset (pos in the bone's local frame, meters; rot euler deg) onto a bone's WORLD
        // transform -> the attachment's world pos+rot. Scripts have no matrix math, so the engine does it; the
        // Socket Editor preview uses the SAME composition so what you author there is what the game shows.
        void ComposeBoneAttach(Vector3 bonePos, Vector3 boneEuler, Vector3 offsetPos, Vector3 offsetEuler,
                               out Vector3 worldPos, out Vector3 worldEuler);
        // Read/write a project asset as UTF-8 text (resolves against the project root in dev, the mounted pak in
        // release which is read-only). Enables data-driven scripts — e.g. a weapon reading its editor-authored
        // ".vsocket" hand placement. Read returns null if missing; Write returns false in release / on error.
        string AssetReadText(string projectRelativePath);
        bool AssetWriteText(string projectRelativePath, string text);

        // 2D UI overlay (immediate mode), coordinates in viewport pixels (top-left origin).
        void UIRect(float x, float y, float w, float h, float r, float g, float b, float a, float radius);
        void UIText(float x, float y, float w, float h, string text, float size, float r, float g, float b, float a, int align, int weight);
        void UILine(float x1, float y1, float x2, float y2, float r, float g, float b, float a, float thick);
        void UIImage(float x, float y, float w, float h, string path, float r, float g, float b, float a);
        float UIWidth();
        float UIHeight();
        float UIMouseX();
        float UIMouseY();
        bool UIMouseDown();
        bool UIMousePressed();
    }

    /// <summary>
    /// A collision/trigger contact passed to OnTriggerEnter/Stay/Exit and OnCollisionEnter. Identifies the OTHER
    /// entity involved (the one that entered your trigger, or the surface you hit).
    /// </summary>
    public struct TriggerHit
    {
        /// <summary>Script handle of the other entity (0 if it has no script).</summary>
        public long EntityId;
        /// <summary>Name of the other entity.</summary>
        public string Name;
        /// <summary>Tag of the other entity (e.g. "Player", "Enemy").</summary>
        public string Tag;
        public TriggerHit(long id, string name, string tag) { EntityId = id; Name = name ?? ""; Tag = tag ?? ""; }
    }

    /// <summary>Result of <see cref="Physics.Raycast(Vector3, Vector3, float, out RaycastHit, int)"/> —
    /// where the ray hit, the surface normal, and WHO was hit.</summary>
    public struct RaycastHit
    {
        /// <summary>World-space hit point.</summary>
        public Vector3 Point;
        /// <summary>Surface normal at the hit (unit length, faces the ray origin).</summary>
        public Vector3 Normal;
        /// <summary>Distance from the ray origin to the hit.</summary>
        public float Distance;
        /// <summary>Script handle of the hit entity (usable with Scene.NameOf/TagOf/GetBehaviour etc.).</summary>
        public long EntityId;
        /// <summary>Name of the hit entity.</summary>
        public string Name;
        /// <summary>Tag of the hit entity.</summary>
        public string Tag;
    }

    /// <summary>Coroutine yield instruction: pause the coroutine for the given seconds.
    /// <c>yield return new WaitForSeconds(2f);</c> — <c>yield return null</c> waits one frame.</summary>
    public sealed class WaitForSeconds
    {
        internal readonly float Seconds;
        public WaitForSeconds(float seconds) { Seconds = seconds > 0f ? seconds : 0f; }
    }

    /// <summary>Handle to a running coroutine (returned by <see cref="VortexBehaviour.StartCoroutine"/>).
    /// Pass it to StopCoroutine to cancel. <see cref="IsRunning"/> tells whether it finished.</summary>
    public sealed class Coroutine
    {
        internal bool Stopped;
        internal bool Done;
        /// <summary>True while the coroutine still has work pending.</summary>
        public bool IsRunning { get { return !Stopped && !Done; } }
    }

    /// <summary>
    /// Base class for all gameplay behaviours — like MonoBehaviour. Override Start (called once when
    /// play begins) and Update (called every tick). Move your entity via Position / Translate, read
    /// input via Input.GetKey, and timing via Time.DeltaTime. For collision zones, mark a Collider as
    /// a Trigger and override OnTriggerEnter/OnTriggerStay/OnTriggerExit (e.g. a no-fly zone, or "change
    /// color when touched"); for solid contacts override OnCollisionEnter.
    /// </summary>
    public abstract class VortexBehaviour
    {
        /// <summary>Engine id of the entity this behaviour is attached to (set by the runtime).</summary>
        public long EntityId { get; internal set; }

        /// <summary>The host the engine wires up so behaviours can affect the live game.</summary>
        internal static IScriptHost Host;

        /// <summary>LOCAL (parent-relative) position of this behaviour's entity (read/write). For an entity under a
        /// moved parent read <see cref="Scene.WorldPositionOf"/> / write <see cref="Scene.SetWorldPose"/> (#354).</summary>
        public Vector3 Position
        {
            get => Host != null ? Host.GetPosition(EntityId) : Vector3.Zero;
            set { Host?.SetPosition(EntityId, value); }
        }

        /// <summary>Move this behaviour's entity by a delta.</summary>
        public void Translate(float dx, float dy, float dz)
        {
            var p = Position; p.X += dx; p.Y += dy; p.Z += dz; Position = p;
        }

        /// <summary>Euler rotation in degrees (X = pitch, Y = yaw, Z = roll) — read/write.</summary>
        public Vector3 Rotation
        {
            get => Host != null ? Host.GetRotation(EntityId) : Vector3.Zero;
            set { Host?.SetRotation(EntityId, value); }
        }

        /// <summary>Rotate this behaviour's entity by a delta (degrees).</summary>
        public void Rotate(float dPitch, float dYaw, float dRoll)
        {
            var r = Rotation; r.X += dPitch; r.Y += dYaw; r.Z += dRoll; Rotation = r;
        }

        /// <summary>Local scale — read/write.</summary>
        public Vector3 Scale
        {
            get => Host != null ? Host.GetScale(EntityId) : Vector3.One;
            set { Host?.SetScale(EntityId, value); }
        }

        /// <summary>Set THIS entity's WORLD position + rotation in one call — correct even when it is a
        /// CHILD of a moved/rotated parent (Position/Rotation write LOCAL values). The viewmodel-follow
        /// primitive: <c>SetWorldPose(eyePos, new Vector3(pitch, yaw, 0));</c></summary>
        public void SetWorldPose(Vector3 position, Vector3 rotationEulerDeg)
            { Host?.SetEntityWorldPose(EntityId, position, rotationEulerDeg); }

        /// <summary>This entity's position in the world, through every parent. <see cref="Position"/> is relative to
        /// the parent — the same for a top-level entity, but not for a camera, flashlight or weapon under the player.</summary>
        public Vector3 WorldPosition
        {
            get { Vector3 p, r; return TryGetWorldPose(out p, out r) ? p : Position; }
        }

        /// <summary>Unit forward vector in the world, through every parent (where a child camera or flashlight looks).
        /// <see cref="Forward"/> uses this entity's own yaw + pitch only.</summary>
        public Vector3 WorldForward
        {
            get
            {
                Vector3 p, r;
                if (!TryGetWorldPose(out p, out r)) return Forward;
                double yaw = r.Y * Math.PI / 180.0, pitch = r.X * Math.PI / 180.0;   // roll does not move the forward axis
                return new Vector3(
                    (float)(Math.Sin(yaw) * Math.Cos(pitch)),
                    (float)(-Math.Sin(pitch)),
                    (float)(Math.Cos(yaw) * Math.Cos(pitch)));
            }
        }

        /// <summary>Unit forward vector from this entity's own yaw + pitch — the world forward for a top-level entity;
        /// under a rotated parent use <see cref="WorldForward"/>.</summary>
        public Vector3 Forward
        {
            get
            {
                var r = Rotation;
                double yaw = r.Y * Math.PI / 180.0, pitch = r.X * Math.PI / 180.0;
                return new Vector3(
                    (float)(Math.Sin(yaw) * Math.Cos(pitch)),
                    (float)(-Math.Sin(pitch)),
                    (float)(Math.Cos(yaw) * Math.Cos(pitch)));
            }
        }

        /// <summary>Unit right vector in world space (horizontal), derived from this entity's yaw.</summary>
        public Vector3 Right
        {
            get
            {
                double yaw = Rotation.Y * Math.PI / 180.0;
                return new Vector3((float)Math.Cos(yaw), 0f, (float)(-Math.Sin(yaw)));
            }
        }

        /// <summary>Set this entity's base color at runtime — e.g. flash a color when a trigger is touched.</summary>
        public void SetColor(float r, float g, float b) { Host?.SetEntityColor(EntityId, r, g, b); }

        /// <summary>This entity's AudioSource component as a script handle (Play/Stop/Pause/
        /// Resume, live Volume/Pitch), or null if the entity has none.</summary>
        public AudioSource GetAudioSource()
        {
            var entity = Editor.Scripting.ScriptRuntime.Instance.FindEntityByHandle(EntityId);
            var component = entity?.GetComponent<Editor.ECS.Components.Audio.AudioSource>();
            return component != null ? new AudioSource(component) : null;
        }

        /// <summary>This entity's Light component as a script handle — the runtime light-control API
        /// (flashlight toggle, dying-bulb flicker, color shifts). Null if the entity has no Light.
        /// Changes take effect next frame in editor play AND in shipped builds.</summary>
        public Light GetLight()
        {
            var entity = Editor.Scripting.ScriptRuntime.Instance.FindEntityByHandle(EntityId);
            var component = entity?.GetComponent<Editor.ECS.Components.Lighting.Light>();
            return component != null ? new Light(component) : null;
        }

        /// <summary>Play an animation clip on this entity's Animator. Pass a clip NAME from the Animator's
        /// clip table (e.g. "Walk") or a .vanim path. fade &gt; 0 crossfades from the current pose (seconds).
        /// Returns false when the entity has no Animator / the clip can't be found.</summary>
        public bool PlayAnimation(string clip, float fade = 0f) { return Host != null && Host.PlayAnimation(EntityId, clip, fade); }

        /// <summary>Freeze this entity's animation on the current pose.</summary>
        public void StopAnimation() { Host?.StopAnimation(EntityId); }

        /// <summary>Playback speed multiplier for this entity's animation (1 = authored speed).</summary>
        public void SetAnimationSpeed(float speed) { Host?.SetAnimationSpeed(EntityId, speed); }

        /// <summary>Is an animation playing? Pass a clip name to ask about that clip specifically.</summary>
        public bool IsAnimationPlaying(string clip = null) { return Host != null && Host.IsAnimationPlaying(EntityId, clip); }

        /// <summary>Current playback time (seconds) of this entity's animation.</summary>
        public float AnimationTime { get { return Host != null ? Host.GetAnimationTime(EntityId) : 0f; } }

        /// <summary>Play a clip on an override LAYER restricted to a bone mask — walk with the legs while
        /// the upper body aims: <c>PlayAnimationLayered("aim_pistol", 1, "Spine1+");</c>. mask = comma-
        /// separated bone names, '+' includes all children. weight blends the layer (0..1).</summary>
        public bool PlayAnimationLayered(string clip, int layer, string mask, float weight = 1f, float fade = 0f)
            { return Host != null && Host.PlayLayeredAnimation(EntityId, clip, layer, mask, weight, fade); }

        /// <summary>Blend an animation layer in or out (raise/lower the weapon smoothly).</summary>
        public void SetAnimationLayerWeight(int layer, float weight) { Host?.SetAnimationLayerWeight(EntityId, layer, weight); }

        /// <summary>Stop an animation layer — the base clip takes its bones back next frame.</summary>
        public void StopAnimationLayer(int layer) { Host?.StopLayeredAnimation(EntityId, layer); }

        /// <summary>Add a persistent ADDITIVE local rotation (Euler degrees) to one of THIS entity's animated
        /// bones every frame — the aim-offset / lean / recoil primitive. A pitch on a spine bone carries
        /// chest+arms+weapon as one so the gun stays locked in the hands at any aim angle:
        /// <c>SetBoneAdditiveRotation("mixamorig:Spine1", new Vector3(aimPitch, 0, 0));</c>. (0,0,0) clears
        /// that bone; <see cref="ClearBoneOverrides"/> clears them all.</summary>
        public void SetBoneAdditiveRotation(string bone, Vector3 eulerDeg) { Host?.SetBoneAdditiveRotation(EntityId, bone, eulerDeg); }
        public void SetBoneScaleOverride(string bone, float scale) { Host?.SetBoneScaleOverride(EntityId, bone, scale); }
        /// <summary>Hide/show a bone (see Animation.SetBoneHidden).</summary>
        public void SetBoneHidden(string bone, bool hidden, bool includeDescendants = true) { Animation.SetBoneHidden(EntityId, bone, hidden, includeDescendants); }

        /// <summary>Clear every runtime bone-rotation override on this entity (back to the pure clip pose).</summary>
        public void ClearBoneOverrides() { Host?.ClearBoneOverrides(EntityId); }

        /// <summary>Blend THIS entity's Two-Bone IK chain(s) (#179): 0 = animation only, 1 = full IK.
        /// tipBone selects one chain (null/empty = all): <c>SetIkWeight("mixamorig:LeftHand", 0f);</c></summary>
        public void SetIkWeight(string tipBone, float weight) { Host?.SetIkWeight(EntityId, tipBone, weight); }
        /// <summary>Drive this entity's IK chain to a world position (see Animation.SetIkTarget).</summary>
        public void SetIkTarget(string tipBone, Vector3 worldPosition) { Animation.SetIkTarget(EntityId, tipBone, worldPosition); }
        public void SetIkTarget(string tipBone, Vector3 worldPosition, Vector3 worldRotationEuler) { Animation.SetIkTarget(EntityId, tipBone, worldPosition, worldRotationEuler); }
        public void ClearIkTarget(string tipBone) { Animation.ClearIkTarget(EntityId, tipBone); }
        public void SetIkPoleAngle(string tipBone, float degrees) { Animation.SetIkPoleAngle(EntityId, tipBone, degrees); }

        /// <summary>Pose one of THIS character's hands from a preset (see <see cref="Animation.SetHandPose(long, string, string, float, float)"/>):
        /// <c>SetHandPose("Right", "Fist");</c></summary>
        public bool SetHandPose(string side, string preset, float weight = 1f, float blendSeconds = 0.15f)
            { return Animation.SetHandPose(EntityId, side, preset, weight, blendSeconds); }
        /// <summary>Back to the authored Hand Pose of that side (null = both hands).</summary>
        public void ClearHandPose(string side = null) { Animation.ClearHandPose(EntityId, side); }
        /// <summary>Turn THIS character's head toward a world point (see Animation.SetLookAtTarget).</summary>
        public void SetLookAtTarget(Vector3 worldPoint) { Animation.SetLookAtTarget(EntityId, worldPoint); }
        /// <summary>Turn THIS character's head toward another entity, tracked every frame (its head when it is a character).</summary>
        public void SetLookAtTarget(long targetEntity) { Animation.SetLookAtTarget(EntityId, targetEntity); }
        public void ClearLookAtTarget() { Animation.ClearLookAtTarget(EntityId); }
        public void SetLookAtWeight(float weight) { Animation.SetLookAtWeight(EntityId, weight); }
        /// <summary>Blend THIS character's foot IK (0 = off, 1 = feet planted on the ground).</summary>
        public void SetFootIkWeight(float weight) { Animation.SetFootIkWeight(EntityId, weight); }
        /// <summary>This entity's world position + rotation (through every parent).</summary>
        public bool TryGetWorldPose(out Vector3 position, out Vector3 rotationEulerDeg) { return Scene.TryGetWorldPose(EntityId, out position, out rotationEulerDeg); }

        /// <summary>Attach THIS entity to a bone of an animated entity — it follows the bone through every
        /// clip from now on (pistol into the hand: <c>AttachTo(character, "Hand_R");</c>). Offsets are in
        /// bone space. Returns false on unknown bone or attach cycle. Pass target 0 to use the nearest
        /// ancestor with an Animator.</summary>
        public bool AttachTo(long targetEntity, string bone) { return AttachTo(targetEntity, bone, Vector3.Zero, Vector3.Zero); }
        public bool AttachTo(long targetEntity, string bone, Vector3 offsetPos, Vector3 offsetRotEuler)
            { return Host != null && Host.AttachEntityToBone(EntityId, targetEntity, bone, offsetPos, offsetRotEuler); }

        /// <summary>Detach this entity from its bone. keepWorldPosition=true (default) leaves it exactly
        /// where the hand released it — no pop; false restores the local transform it had before the
        /// first AttachTo (holster-to-origin).</summary>
        public bool Detach(bool keepWorldPosition = true)
            { return Host != null && Host.DetachEntityFromBone(EntityId, keepWorldPosition); }

        // ---- Rigid-body physics (#100) on THIS entity (needs a Collider + Dynamic Rigidbody in the editor) ----

        /// <summary>Continuous push in N for this frame (see Physics.AddForce): <c>AddForce(new Vector3(0, 0, 40f));</c></summary>
        public void AddForce(Vector3 force) { Physics.AddForce(EntityId, force); }

        /// <summary>Instant kick in N·s (see Physics.AddImpulse): <c>AddImpulse(Vector3.Up * 30f);</c></summary>
        public void AddImpulse(Vector3 impulse) { Physics.AddImpulse(EntityId, impulse); }

        /// <summary>Spin this body (N·m).</summary>
        public void AddTorque(Vector3 torque) { Physics.AddTorque(EntityId, torque); }

        /// <summary>Velocity (m/s) of this entity's simulated body — read/write. Zero / ignored without a dynamic Rigidbody.</summary>
        public Vector3 Velocity
        {
            get => Physics.GetVelocity(EntityId);
            set { Physics.SetVelocity(EntityId, value); }
        }

        /// <summary>Angular velocity (rad/s) of this entity's simulated body — read/write.</summary>
        public Vector3 AngularVelocity
        {
            get => Physics.GetAngularVelocity(EntityId);
            set { Physics.SetAngularVelocity(EntityId, value); }
        }

        /// <summary>True when this entity is simulated as a moving physics body (Dynamic or Kinematic Rigidbody).</summary>
        public bool HasRigidbody { get { return Physics.HasRigidbody(EntityId); } }

        // ---- Coroutines + timers (#37) ----

        /// <summary>Start a coroutine on this behaviour. Inside, <c>yield return new WaitForSeconds(2f)</c>
        /// pauses for 2 seconds and <c>yield return null</c> waits one frame. Coroutines stop automatically
        /// when the behaviour's entity is destroyed or play ends. The classic horror sequence tool:
        /// <c>IEnumerator Scare() { light.Enabled = false; yield return new WaitForSeconds(1.5f); Spawn(); }</c></summary>
        public Coroutine StartCoroutine(System.Collections.IEnumerator routine)
            { return Editor.Scripting.ScriptRuntime.Instance.StartCoroutine(this, routine); }

        /// <summary>Cancel a running coroutine started on this behaviour.</summary>
        public void StopCoroutine(Coroutine routine) { if (routine != null) routine.Stopped = true; }

        /// <summary>Cancel every coroutine started on this behaviour.</summary>
        public void StopAllCoroutines() { Editor.Scripting.ScriptRuntime.Instance.StopAllCoroutines(this); }

        /// <summary>Run <paramref name="action"/> once after <paramref name="delay"/> seconds.</summary>
        public void Invoke(Action action, float delay)
            { Editor.Scripting.ScriptRuntime.Instance.ScheduleInvoke(this, action, delay, 0f); }

        /// <summary>Run <paramref name="action"/> after <paramref name="delay"/> seconds, then again every
        /// <paramref name="interval"/> seconds until <see cref="CancelInvokes"/> (or play ends).</summary>
        public void InvokeRepeating(Action action, float delay, float interval)
            { Editor.Scripting.ScriptRuntime.Instance.ScheduleInvoke(this, action, delay, interval > 0.001f ? interval : 0.001f); }

        /// <summary>Cancel every pending Invoke/InvokeRepeating on this behaviour.</summary>
        public void CancelInvokes() { Editor.Scripting.ScriptRuntime.Instance.CancelInvokes(this); }

        // ---- Entity messaging (#38) + hierarchy (#39) ----

        /// <summary>Send a message to the behaviour on another entity — its <see cref="OnMessage"/> is called
        /// this frame. Target entities via <see cref="Scene.Find"/>/<see cref="Scene.FindByTag"/> or a TriggerHit/
        /// RaycastHit EntityId. <c>SendMessage(door, "open");</c></summary>
        public void SendMessage(long targetEntity, string message, object arg = null)
            { Editor.Scripting.ScriptRuntime.Instance.SendEntityMessage(targetEntity, message, arg); }

        /// <summary>Script handle of this entity's parent (0 = none / root).</summary>
        public long GetParent() { return Scene.Parent(EntityId); }

        /// <summary>Script handles of this entity's direct children.</summary>
        public long[] GetChildren() { return Scene.Children(EntityId); }

        public virtual void Start() { }
        public virtual void Update(float dt) { }
        /// <summary>Runs AFTER every behaviour's Update() this frame (Unity-style). Use it for anything that
        /// must read the FINAL state the other scripts produced this tick — most importantly a first-person
        /// VIEWMODEL that follows the camera: positioning it here (not in Update) guarantees the camera script
        /// already moved this frame, so the weapon can't lag a frame behind and jitter (worst at uncapped FPS).</summary>
        public virtual void LateUpdate(float dt) { }
        public virtual void OnDestroy() { }

        /// <summary>Called when another behaviour <see cref="SendMessage"/>s this entity.</summary>
        public virtual void OnMessage(string message, object arg) { }

        /// <summary>Called when the playing clip crosses one of its EVENT markers (authored in the Keyframe
        /// Editor) — e.g. footstep sounds, attack hit frames. The marker's name is passed.</summary>
        public virtual void OnAnimationEvent(string name) { }

        /// <summary>Called once when another character first enters this entity's TRIGGER collider.</summary>
        public virtual void OnTriggerEnter(TriggerHit other) { }
        /// <summary>Called every tick while another character stays inside this entity's TRIGGER collider.</summary>
        public virtual void OnTriggerStay(TriggerHit other) { }
        /// <summary>Called once when another character leaves this entity's TRIGGER collider.</summary>
        public virtual void OnTriggerExit(TriggerHit other) { }
        /// <summary>Called once when a character first touches this entity's SOLID (non-trigger) collider.</summary>
        public virtual void OnCollisionEnter(TriggerHit other) { }
    }

    /// <summary>Log to the editor's Console panel (bottom, next to the Explorer) — shows while the game plays, with
    /// timestamps and Info/Warn/Error colours. Unity-style: pass anything, it's ToString()'d.</summary>
    public static class Debug
    {
        public static void Log(object message) { var s = Str(message); Push(0, s); Editor.Core.Services.ConsoleService.Instance.Log(s); }
        public static void LogWarning(object message) { var s = Str(message); Push(1, s); Editor.Core.Services.ConsoleService.Instance.LogWarning(s); }
        public static void LogError(object message) { var s = Str(message); Push(2, s); Editor.Core.Services.ConsoleService.Instance.LogError(s); }
        private static string Str(object m) => m?.ToString() ?? "null";

        // ---- On-screen dev console (#42): the last log lines drawn over the GAME view (editor play,
        // game window AND shipped builds). Toggle with F9 or ShowConsole(); errors auto-show it briefly. ----

        /// <summary>Show/hide the in-game console overlay (F9 toggles it too).</summary>
        public static void ShowConsole(bool show) { ConsoleVisible = show; }
        public static bool ConsoleVisible { get; set; }

        internal struct ConsoleLine { public int Level; public string Text; public DateTime At; }
        internal static readonly List<ConsoleLine> Lines = new List<ConsoleLine>();
        internal static void Push(int level, string text)
        {
            lock (Lines)
            {
                Lines.Add(new ConsoleLine { Level = level, Text = text ?? "", At = DateTime.UtcNow });
                if (Lines.Count > 200) Lines.RemoveRange(0, Lines.Count - 200);
            }
        }
        internal static void ClearLines() { lock (Lines) Lines.Clear(); }

        // ---- Debug draw (#42): wireframe shapes in the 3D view — the classic "see what the AI sees"
        // tools. duration 0 = this frame only; > 0 keeps the shape alive that many seconds. ----

        /// <summary>Draw a wire line from a to b (default green).</summary>
        public static void DrawLine(Vector3 a, Vector3 b, float r = 0.2f, float g = 1f, float bl = 0.3f, float duration = 0f)
            { Editor.Scripting.ScriptRuntime.Instance.AddDebugLine(a, b, r, g, bl, duration); }

        /// <summary>Draw a ray: origin + direction * length. Perfect together with Physics.Raycast.</summary>
        public static void DrawRay(Vector3 origin, Vector3 direction, float length, float r = 1f, float g = 0.9f, float bl = 0.2f, float duration = 0f)
        {
            var d = direction.Normalized;
            DrawLine(origin, new Vector3(origin.X + d.X * length, origin.Y + d.Y * length, origin.Z + d.Z * length), r, g, bl, duration);
        }

        /// <summary>Draw a wire sphere (trigger radii, hearing ranges, blast zones).</summary>
        public static void DrawSphere(Vector3 center, float radius, float r = 0.3f, float g = 0.6f, float bl = 1f, float duration = 0f)
            { Editor.Scripting.ScriptRuntime.Instance.AddDebugSphere(center, radius, r, g, bl, duration); }
    }

    /// <summary>Keyboard + mouse input. Key names match WPF keys, e.g. "W", "Space", "LeftShift".</summary>
    public static class Input
    {
        internal static IScriptHost Host;
        public static bool GetKey(string key) => Host != null && Host.GetKey(key);
        /// <summary>True for exactly the tick the key went down (E = interact, R = reload) — a tap shorter than one
        /// frame still counts in the editor. Key names as for <see cref="GetKey"/>.</summary>
        public static bool GetKeyDown(string key) => Host != null && Host.GetKeyDown(key);
        /// <summary>True for exactly the tick the key was released.</summary>
        public static bool GetKeyUp(string key) => Host != null && Host.GetKeyUp(key);

        /// <summary>Mouse movement since the last tick, in pixels (only non-zero while the game has
        /// captured the cursor — i.e. in play before ESC). Use it for mouse-look. Forced to 0 while a screen that
        /// opted into freezing gameplay (BlocksGameplay) is up, so mouse-look stops with movement.</summary>
        public static float MouseDeltaX { get { return (Editor.UI.Vui.VuiStack.Instance.GameplayInputBlocked || !WindowFocused) ? 0f : _mouseDeltaX; } internal set { _mouseDeltaX = value; } }
        public static float MouseDeltaY { get { return (Editor.UI.Vui.VuiStack.Instance.GameplayInputBlocked || !WindowFocused) ? 0f : _mouseDeltaY; } internal set { _mouseDeltaY = value; } }
        private static float _mouseDeltaX, _mouseDeltaY;

        /// <summary>Mouse-wheel movement since the last tick, in NOTCHES: +1 per notch up (away), -1 per notch down.
        /// 0 when not scrolling, unfocused, or a menu screen consumed the wheel. Use it for weapon switching etc.</summary>
        public static float ScrollDelta { get { return (Editor.UI.Vui.VuiStack.Instance.GameplayInputBlocked || !WindowFocused) ? 0f : _scrollDelta; } internal set { _scrollDelta = value; } }
        private static float _scrollDelta;

        // ---- Window focus: ALL input (keyboard, mouse, controller) is dead unless OUR window is the foreground
        // window. Works everywhere — in-editor play, the external game window, and an exported debug/release build
        // (they're all in this process) — so an unfocused/alt-tabbed game can't be driven by stray global input. ----
        /// <summary>True only while this app's window is the foreground window. Input is ignored otherwise.
        /// The host (native GameHost / editor shell) reports focus through Editor.Core.Input.HostInput.</summary>
        public static bool WindowFocused { get { return Editor.Core.Input.HostInput.IsWindowFocused(); } }

        // ---- Gamepad / controller (Windows.Gaming.Input incl. PlayStation, XInput fallback). Polled once per tick.
        // Sticks/triggers are normalized to -1..1 / 0..1 with dead zones; frozen to neutral while a gameplay-blocking
        // UI screen is up OR the window isn't focused, so the pad can't drive the player through a menu / in the bg. ----
        private static bool _padOn;
        private static float _lx, _ly, _rx, _ry, _lt, _rt;
        private static ushort _buttons, _prevButtons;
        private static bool Gated { get { return Editor.UI.Vui.VuiStack.Instance.GameplayInputBlocked || !WindowFocused; } }

        /// <summary>True while a controller is connected.</summary>
        public static bool GamepadConnected { get { return _padOn; } }
        /// <summary>Left stick, -1..1 (X right, Y up). 0 when gated by a blocking UI screen.</summary>
        public static float LeftStickX  { get { return Gated ? 0f : _lx; } }
        public static float LeftStickY  { get { return Gated ? 0f : _ly; } }
        /// <summary>Right stick, -1..1 (for look). 0 when gated.</summary>
        public static float RightStickX { get { return Gated ? 0f : _rx; } }
        public static float RightStickY { get { return Gated ? 0f : _ry; } }
        /// <summary>Triggers, 0..1.</summary>
        public static float LeftTrigger  { get { return Gated ? 0f : _lt; } }
        public static float RightTrigger { get { return Gated ? 0f : _rt; } }

        // ---- UNGATED reads for the retained UI (#44): menu navigation needs the pad exactly while a
        // BlocksGameplay screen gates the public getters above. Window focus still applies (PollGamepad
        // zeroes the raw fields while unfocused). Internal — game scripts keep the gated surface. ----
        internal static float UiLeftStickX { get { return _lx; } }
        internal static float UiLeftStickY { get { return _ly; } }
        internal static bool UiButtonDown(string name)
        { ushort m = MaskOf(name); return (_buttons & m) != 0 && (_prevButtons & m) == 0; }

        /// <summary>Is a controller button held? Names: A B X Y LB RB Back Start LeftStick RightStick
        /// DPadUp DPadDown DPadLeft DPadRight.</summary>
        public static bool GetGamepadButton(string name) { return !Gated && (_buttons & MaskOf(name)) != 0; }
        /// <summary>Was a controller button pressed THIS tick (edge)?</summary>
        public static bool GetGamepadButtonDown(string name)
        { ushort m = MaskOf(name); return !Gated && (_buttons & m) != 0 && (_prevButtons & m) == 0; }

        private static ushort MaskOf(string n)
        {
            if (string.IsNullOrEmpty(n)) return 0;
            switch (n.ToLowerInvariant())
            {
                case "dpadup": return 0x0001; case "dpaddown": return 0x0002;
                case "dpadleft": return 0x0004; case "dpadright": return 0x0008;
                case "start": return 0x0010; case "back": return 0x0020;
                case "leftstick": return 0x0040; case "rightstick": return 0x0080;
                case "lb": case "leftshoulder": return 0x0100; case "rb": case "rightshoulder": return 0x0200;
                case "a": return 0x1000; case "b": return 0x2000; case "x": return 0x4000; case "y": return 0x8000;
                default: return 0;
            }
        }

        /// <summary>Poll the first connected controller once per tick. Order: Windows.Gaming.Input.Gamepad
        /// (normalized — Xbox + any pad Windows maps as a gamepad, incl. DualSense on Win11) → RawGameController
        /// (a Sony DualSense/DualShock that wasn't mapped as a Gamepad, by vendor id) → XInput (last resort).
        /// Windows.Gaming.Input handles USB + Bluetooth + the DualSense HID internally, so a PS5 pad "just works"
        /// with no extra software.</summary>
        internal static void PollGamepad()
        {
            _prevButtons = _buttons;

            // No controller input while our window isn't focused.
            if (!WindowFocused) { _padOn = false; _buttons = 0; _lx = _ly = _rx = _ry = _lt = _rt = 0f; return; }

            // Shared core: the host supplies the controller snapshot (SDL3 gamepads on macOS/Linux).
            var pad = Editor.Core.Input.HostInput.PollGamepad();
            _padOn = pad.Connected;
            _buttons = pad.Buttons;
            _lx = pad.LeftX; _ly = pad.LeftY; _rx = pad.RightX; _ry = pad.RightY; _lt = pad.LeftTrigger; _rt = pad.RightTrigger;
            return;
        }

    }

    /// <summary>Frame timing.</summary>
    public static class Time
    {
        /// <summary>Seconds since the last tick (the runtime sets this each frame).</summary>
        public static float DeltaTime { get; internal set; }
    }

    /// <summary>
    /// Scene control. Generic engine API — the GAME decides WHEN/WHICH scene (e.g. lobby PLAY -&gt; "Match",
    /// death -&gt; "Lobby"). The switch is deferred to the end of the current tick so it's safe to call from
    /// inside a behaviour's Update. Scene names match the scenes authored in the project.
    /// </summary>
    public static class Scene
    {
        internal static IScriptHost Host;

        /// <summary>Fired right before a requested scene switch is applied — hook it to show a loading
        /// screen (draw UI / fade out). The actual switch happens at the end of the current tick.</summary>
        public static event Action<string> Loading;

        public static void Load(string name)
        {
            if (Host == null) return;
            try { Loading?.Invoke(name); } catch { }
            Host.LoadScene(name);
        }

        internal static void ResetHooks() { Loading = null; }

        // ---- Entity queries + hierarchy traversal (#39) ----
        // All queries return SCRIPT HANDLES (long, 0 = not found) — the same ids used by TriggerHit,
        // RaycastHit and behaviours' EntityId. Handles stay valid for the run (until scene switch/stop).

        /// <summary>Find an entity anywhere in the scene by exact name (first match; 0 = none).</summary>
        public static long Find(string name) { return Editor.Scripting.ScriptRuntime.Instance.FindEntityHandle(name); }

        /// <summary>Every entity carrying the given Tag (set in the Inspector).</summary>
        public static long[] FindByTag(string tag) { return Editor.Scripting.ScriptRuntime.Instance.FindEntityHandlesByTag(tag); }

        /// <summary>Parent of an entity (0 = root/none).</summary>
        public static long Parent(long entity) { return Editor.Scripting.ScriptRuntime.Instance.GetParentHandle(entity); }

        /// <summary>Direct children of an entity.</summary>
        public static long[] Children(long entity) { return Editor.Scripting.ScriptRuntime.Instance.GetChildHandles(entity); }

        /// <summary>Name of an entity ("" if the handle is stale).</summary>
        public static string NameOf(long entity) { return Editor.Scripting.ScriptRuntime.Instance.EntityNameOf(entity); }

        /// <summary>Tag of an entity ("" if none).</summary>
        public static string TagOf(long entity) { return Editor.Scripting.ScriptRuntime.Instance.EntityTagOf(entity); }

        /// <summary>LOCAL (parent-relative) position of any entity — the same value the inspector shows. For a parented
        /// entity (a door marker inside a group) use <see cref="WorldPositionOf"/> (#354).</summary>
        public static Vector3 PositionOf(long entity) { return Host != null ? Host.GetPosition(entity) : Vector3.Zero; }

        /// <summary>Set the LOCAL (parent-relative) position of any entity — see <see cref="SetWorldPositionOf"/> for
        /// a world-space target (#354).</summary>
        public static void SetPositionOf(long entity, Vector3 position) { Host?.SetPosition(entity, position); }

        /// <summary>WORLD position of any entity, through every parent — the one to teleport to, aim at or measure
        /// distances with (#354). Equals <see cref="PositionOf"/> for an unparented entity.</summary>
        public static Vector3 WorldPositionOf(long entity)
        {
            Vector3 p, r;
            return TryGetWorldPose(entity, out p, out r) ? p : PositionOf(entity);
        }

        /// <summary>Set the WORLD position of any entity, keeping its world rotation (#354) — a teleport that lands
        /// where you say even for an entity inside a moved group.</summary>
        public static void SetWorldPositionOf(long entity, Vector3 position)
        {
            Vector3 p, r;
            if (TryGetWorldPose(entity, out p, out r)) SetWorldPose(entity, position, r); else SetPositionOf(entity, position);
        }

        /// <summary>LOCAL rotation (Euler degrees) of any entity — pairs with <see cref="PositionOf"/>.</summary>
        public static Vector3 RotationOf(long entity) { return Host != null ? Host.GetRotation(entity) : Vector3.Zero; }

        /// <summary>Set the LOCAL rotation (Euler degrees) of any entity — pairs with <see cref="SetPositionOf"/>.</summary>
        public static void SetRotationOf(long entity, Vector3 rotationEulerDeg) { Host?.SetRotation(entity, rotationEulerDeg); }
        /// <summary>An entity's LOCAL scale.</summary>
        public static Vector3 ScaleOf(long entity) { return Host != null ? Host.GetScale(entity) : Vector3.One; }
        public static void SetScaleOf(long entity, Vector3 scale) { Host?.SetScale(entity, scale); }
        /// <summary>Tint another entity's mesh (0..1 per channel) — the same as this behaviour's SetColor, for any entity.</summary>
        public static void SetColorOf(long entity, float r, float g, float b) { Host?.SetEntityColor(entity, r, g, b); }
        /// <summary>Swap an entity's material at runtime (a project .vmat, e.g. a weapon camo); null or "" restores the
        /// model's own imported material. Takes effect on the next frame.</summary>
        public static void SetMaterialOf(long entity, string materialPath) { Host?.SetEntityMaterial(entity, materialPath); }
        /// <summary>The .vmat currently assigned to an entity's mesh ("" = the model's own material).</summary>
        public static string MaterialOf(long entity) { return Host != null ? Host.GetEntityMaterial(entity) : ""; }

        /// <summary>Set an entity's WORLD position + rotation in one call — correct even when the entity
        /// is a CHILD of a moved/rotated/scaled parent (the engine converts to the local frame; the
        /// entity's own scale is untouched). THE way to camera-lock a viewmodel that lives under the
        /// Player entity: <c>Scene.SetWorldPose(EntityId, eyePos, new Vector3(pitch, yaw, 0));</c>
        /// (Position/Rotation write LOCAL values — for parented entities use this instead.)</summary>
        public static void SetWorldPose(long entity, Vector3 position, Vector3 rotationEulerDeg)
            { Host?.SetEntityWorldPose(entity, position, rotationEulerDeg); }

        /// <summary>An entity's WORLD position + rotation (engine Euler degrees), through every parent — the
        /// counterpart of <see cref="SetWorldPose"/>. False when the entity is unknown.</summary>
        public static bool TryGetWorldPose(long entity, out Vector3 position, out Vector3 rotationEulerDeg)
        {
            position = Vector3.Zero; rotationEulerDeg = Vector3.Zero;
            return Host != null && Host.TryGetEntityWorldPose(entity, out position, out rotationEulerDeg);
        }

        /// <summary>Force a render layer onto an entity + all children (every MeshRenderer):
        /// 0 = World, 1 = First-Person viewmodel, 2 = Third-Person only. The weapon-system primitive —
        /// spawn one prefab copy for your hands and one for your body:
        /// <c>Scene.SetRenderLayer(fpGun, 1); Scene.SetRenderLayer(bodyGun, 2);</c></summary>
        public static void SetRenderLayer(long entity, int layer)
            { Host?.SetRenderLayer(entity, layer); }

        /// <summary>The behaviour instance running on an entity (null if none / wrong type) — lets scripts
        /// talk to each other directly: <c>Scene.GetBehaviour&lt;DoorController&gt;(door)?.Open();</c></summary>
        public static T GetBehaviour<T>(long entity) where T : VortexBehaviour
            { return Editor.Scripting.ScriptRuntime.Instance.BehaviourOf(entity) as T; }

        /// <summary>The Light component of ANY entity as a script handle (null if none) — drive scene
        /// lamps from a manager script, not just your own entity's light.</summary>
        public static Light GetLight(long entity)
        {
            var e = Editor.Scripting.ScriptRuntime.Instance.FindEntityByHandle(entity);
            var c = e != null ? e.GetComponent<Editor.ECS.Components.Lighting.Light>() : null;
            return c != null ? new Light(c) : null;
        }

        /// <summary>The AudioSource component of ANY entity as a script handle (null if none).</summary>
        public static AudioSource GetAudioSource(long entity)
        {
            var e = Editor.Scripting.ScriptRuntime.Instance.FindEntityByHandle(entity);
            var c = e != null ? e.GetComponent<Editor.ECS.Components.Audio.AudioSource>() : null;
            return c != null ? new AudioSource(c) : null;
        }

        /// <summary>Activate/deactivate an entity and its children (#51) — the staple horror mechanic
        /// (monster appears behind you, prop swaps between glances, a door unlocks). Deactivating stops
        /// rendering, REMOVES its colliders from the collision world and stops its audio sources;
        /// re-activating restores all of it. Behaviours keep their state. No allocation churn — far
        /// cheaper than Instantiate/Destroy for on/off mechanics.</summary>
        public static void SetActive(long entity, bool active) { Editor.Scripting.ScriptRuntime.Instance.SetEntityActive(entity, active); }

        /// <summary>The entity's OWN active flag (Unity's activeSelf) — true even while hidden because
        /// a parent is deactivated.</summary>
        public static bool IsActive(long entity) { return Editor.Scripting.ScriptRuntime.Instance.IsEntityActive(entity); }

        /// <summary>True only when this entity AND every ancestor are active (Unity's activeInHierarchy)
        /// — i.e. the entity actually renders/collides right now.</summary>
        public static bool IsActiveInHierarchy(long entity) { return Editor.Scripting.ScriptRuntime.Instance.IsEntityActiveInHierarchy(entity); }

        /// <summary>Enable/disable JUST the entity's MeshRenderer (#51) — the prop stays solid and
        /// audible, it only stops rendering (invisible wall, blinking pickup).</summary>
        public static void SetRendererEnabled(long entity, bool enabled) { Editor.Scripting.ScriptRuntime.Instance.SetRendererEnabled(entity, enabled); }

        /// <summary>Enable/disable the entity's colliders (#51), subtree-wide — a locked door opens for
        /// the player without hiding its mesh. Shapes leave/rejoin the collision world immediately.</summary>
        public static void SetColliderEnabled(long entity, bool enabled) { Editor.Scripting.ScriptRuntime.Instance.SetColliderEnabled(entity, enabled); }

        // ---- Runtime prefab instantiation (#36) ----

        /// <summary>Spawn a prefab (.ventity asset, project-relative path like "Assets/Prefabs/Monster.ventity")
        /// at a world position. Its Script components start immediately (Start() runs this frame), its colliders
        /// join the collision world, and it renders the same frame. Returns the new entity's handle (0 = failed).
        /// THE jump-scare primitive: <c>Scene.Instantiate("Assets/Prefabs/Monster.ventity", pos, yaw);</c></summary>
        public static long Instantiate(string prefabPath, Vector3 position, float yawDegrees = 0f)
            { return Editor.Scripting.ScriptRuntime.Instance.InstantiatePrefabAt(prefabPath, position, yawDegrees); }

        /// <summary>Remove an entity (spawned or authored) from the running game: behaviours get OnDestroy,
        /// rendering + colliders are removed. The scene ASSET is untouched — this is runtime-only.</summary>
        public static void Destroy(long entity) { Editor.Scripting.ScriptRuntime.Instance.DestroyEntity(entity); }
    }

    /// <summary>
    /// Typed event bus (#38) — decoupled game-wide messaging. Define an event type in your scripts,
    /// Subscribe in Start, Publish from anywhere: <c>Events.Publish(new MonsterSpotted { Where = pos });</c>
    /// Subscriptions are cleared automatically when play stops or the scene switches.
    /// For DIRECT entity-to-entity calls use <see cref="VortexBehaviour.SendMessage"/> instead.
    /// </summary>
    public static class Events
    {
        private static readonly Dictionary<Type, List<Delegate>> _subs = new Dictionary<Type, List<Delegate>>();

        /// <summary>Subscribe to every published event of type T.</summary>
        public static void Subscribe<T>(Action<T> handler)
        {
            if (handler == null) return;
            if (!_subs.TryGetValue(typeof(T), out var list)) { list = new List<Delegate>(); _subs[typeof(T)] = list; }
            list.Add(handler);
        }

        /// <summary>Remove a previously subscribed handler.</summary>
        public static void Unsubscribe<T>(Action<T> handler)
        {
            if (handler != null && _subs.TryGetValue(typeof(T), out var list)) list.Remove(handler);
        }

        /// <summary>Publish an event to every subscriber, immediately. A throwing handler is logged
        /// and skipped — one broken listener never breaks the others.</summary>
        public static void Publish<T>(T evt)
        {
            if (!_subs.TryGetValue(typeof(T), out var list) || list.Count == 0) return;
            var snapshot = list.ToArray();   // handlers may (un)subscribe while being invoked
            for (int i = 0; i < snapshot.Length; i++)
            {
                try { ((Action<T>)snapshot[i])(evt); }
                catch (Exception ex) { Debug.LogError("Events handler for " + typeof(T).Name + " threw: " + ex.Message); }
            }
        }

        /// <summary>Drop every subscription (the runtime calls this on play start/stop + scene switch).</summary>
        public static void Clear() { _subs.Clear(); }
    }

    /// <summary>
    /// Persistent save data (#40) — PlayerPrefs-style key/value storage plus save SLOTS, stored per game
    /// under %APPDATA%\VortexGames\&lt;project&gt;. Works identically in editor play and shipped builds.
    /// Values auto-flush to disk on scene switch and play end; call <see cref="Flush"/> after a checkpoint
    /// to be crash-safe. Slots: <c>Save.UseSlot(2)</c> switches the active file (slot 0 is the default).
    /// </summary>
    /// <summary>Project asset text I/O for data-driven scripts (e.g. a weapon reading its ".vsocket" hand
    /// placement authored in the Socket Editor). Paths are project-relative; dev reads/writes loose files, a
    /// shipped pak is read-only.</summary>
    public static class Assets
    {
        internal static IScriptHost Host;
        /// <summary>Read a project asset as text (null if missing).</summary>
        public static string ReadText(string projectRelativePath)
            { return Host != null ? Host.AssetReadText(projectRelativePath) : null; }
        /// <summary>Write text to a project asset (dev only; false in a shipped pak / on error).</summary>
        public static bool WriteText(string projectRelativePath, string text)
            { return Host != null && Host.AssetWriteText(projectRelativePath, text); }
    }

    public static class Save
    {
        private static Dictionary<string, string> _data;   // typed values as "i:", "f:", "s:" strings
        private static int _slot;
        private static bool _dirty;

        public static int CurrentSlot { get { return _slot; } }

        /// <summary>Switch the active save slot (loads that slot's file; creates it on first write).</summary>
        public static void UseSlot(int slot)
        {
            Flush();
            _slot = slot < 0 ? 0 : slot;
            _data = null;   // lazy-reload from the new slot's file
        }

        public static bool SlotExists(int slot) { return System.IO.File.Exists(SlotPath(slot)); }

        public static void DeleteSlot(int slot)
        {
            try { System.IO.File.Delete(SlotPath(slot)); } catch { }
            if (slot == _slot) { _data = new Dictionary<string, string>(); _dirty = false; }
        }

        public static void SetInt(string key, int value)       { Data()[key] = "i:" + value.ToString(System.Globalization.CultureInfo.InvariantCulture); _dirty = true; }
        public static void SetFloat(string key, float value)   { Data()[key] = "f:" + value.ToString("R", System.Globalization.CultureInfo.InvariantCulture); _dirty = true; }
        public static void SetString(string key, string value) { Data()[key] = "s:" + (value ?? ""); _dirty = true; }
        public static void SetBool(string key, bool value)     { SetInt(key, value ? 1 : 0); }

        public static int GetInt(string key, int def = 0)
        {
            return Data().TryGetValue(key, out var v) && v.StartsWith("i:")
                && int.TryParse(v.Substring(2), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i) ? i : def;
        }
        public static float GetFloat(string key, float def = 0f)
        {
            return Data().TryGetValue(key, out var v) && v.StartsWith("f:")
                && float.TryParse(v.Substring(2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : def;
        }
        public static string GetString(string key, string def = "")
        {
            return Data().TryGetValue(key, out var v) && v.StartsWith("s:") ? v.Substring(2) : def;
        }
        public static bool GetBool(string key, bool def = false) { return GetInt(key, def ? 1 : 0) != 0; }

        public static bool HasKey(string key) { return Data().ContainsKey(key); }
        public static void DeleteKey(string key) { if (Data().Remove(key)) _dirty = true; }
        public static void DeleteAll() { Data().Clear(); _dirty = true; }

        /// <summary>Write pending changes to disk now (checkpoint!). Auto-called on scene switch + play end.</summary>
        public static void Flush()
        {
            if (!_dirty || _data == null) return;
            try
            {
                var path = SlotPath(_slot);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                var sb = new System.Text.StringBuilder();
                foreach (var kv in _data)
                    sb.Append(Escape(kv.Key)).Append('\t').Append(Escape(kv.Value)).Append('\n');
                System.IO.File.WriteAllText(path, sb.ToString());
                _dirty = false;
            }
            catch (Exception ex) { Debug.LogError("Save.Flush failed: " + ex.Message); }
        }

        private static Dictionary<string, string> Data()
        {
            if (_data != null) return _data;
            _data = new Dictionary<string, string>();
            try
            {
                var path = SlotPath(_slot);
                if (System.IO.File.Exists(path))
                {
                    foreach (var line in System.IO.File.ReadAllLines(path))
                    {
                        int t = line.IndexOf('\t');
                        if (t > 0) _data[Unescape(line.Substring(0, t))] = Unescape(line.Substring(t + 1));
                    }
                }
            }
            catch { }
            return _data;
        }

        private static string SlotPath(int slot)
        {
            string game = "Game";
            try { game = Editor.Core.Data.ProjectData.Current?.Name ?? "Game"; } catch { }
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) game = game.Replace(c, '_');
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return System.IO.Path.Combine(root, "VortexGames", game, "save_slot" + slot + ".dat");
        }

        private static string Escape(string s)   { return s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n"); }
        private static string Unescape(string s) { return s.Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\"); }
    }

    /// <summary>Mouse mode. Locked = captured + hidden for mouse-look (gameplay). Unlocked = free cursor so
    /// the player can click the UI (lobby / ESC menu / shop). The game sets this; the engine enforces it.</summary>
    public static class Cursor
    {
        internal static IScriptHost Host;
        public static bool Locked
        {
            get { return Host != null && Host.GetCursorLocked(); }
            set { if (Host != null) Host.SetCursorLocked(value); }
        }
    }

    /// <summary>Application-level control for the game.</summary>
    public static class Application
    {
        internal static IScriptHost Host;
        /// <summary>Quit the game (closes the standalone player / stops play).</summary>
        public static void Quit() { if (Host != null) Host.QuitGame(); }
    }

    /// <summary>Player view camera control (the live game/play view).</summary>
    public static class Camera
    {
        internal static IScriptHost Host;
        /// <summary>Vertical field of view in degrees (clamped 30–120 by the engine).</summary>
        public static void SetFieldOfView(float fovDegrees) { if (Host != null) Host.SetCameraFov(fovDegrees); }

        /// <summary>True while the view is NOT the local first-person camera — the debug free camera (P), a spectator:
        /// the third-person body (render layer 2) is drawn and the viewmodel (layer 1) hidden. Weapon scripts start
        /// tracers and eject shells at the third-person gun's sockets then (#194).</summary>
        public static bool IsExternalView { get { return Editor.Core.Services.SceneRenderService.DebugThirdPersonView; } }

        /// <summary>FOV of the FIRST-PERSON layer (entities with the "First-Person (viewmodel)" flag on
        /// their Mesh Renderer). Default 54 — the world FOV never distorts the arms/weapon. Clamped
        /// 10–120 (values below 30 are the ADS-zoom range).</summary>
        public static void SetViewmodelFieldOfView(float fovDegrees) { if (Host != null) Host.SetViewmodelFov(fovDegrees); }
    }

    /// <summary>
    /// Collision + rigid-body physics. <see cref="MoveCharacter"/> resolves a character capsule (feet position,
    /// radius, height) against the scene's Collider components with collide-and-slide: the ground is solid, you
    /// can't walk through walls/props/models, and you can't clip through even up close. <see cref="Grounded"/> is
    /// true when the last move ended resting on a surface (use it to reset jumping/gravity). Add Colliders to your
    /// level objects in the editor; the character itself needs no collider — you pass its capsule to MoveCharacter
    /// each frame. Props with a Collider AND a Rigidbody (Dynamic) are simulated by the physics engine (Jolt): they
    /// fall, stack, roll, get pushed by the character and react to <see cref="AddForce"/> / <see cref="AddImpulse"/>.
    /// </summary>
    public static class Physics
    {
        internal static IScriptHost Host;
        private static bool _grounded;
        public static bool Grounded { get { return _grounded; } }

        /// <summary>Draw the physics world over the scene (#106): body wireframes colour-coded static / kinematic /
        /// dynamic / sleeping, joints, character capsules and this frame's contacts. Editor play and dev builds only —
        /// a shipped game draws nothing. <c>Physics.DebugDraw(true)</c> switches every layer on.</summary>
        public static void DebugDraw(bool on) { Editor.Core.Services.Physics.PhysicsService.SetDebugDraw(on); }
        /// <summary>The same with the layers chosen one by one.</summary>
        public static void DebugDraw(bool bodies, bool joints, bool characters, bool contacts)
        {
            var l = Editor.Core.Services.Physics.PhysicsService.DebugLayer.None;
            if (bodies) l |= Editor.Core.Services.Physics.PhysicsService.DebugLayer.Bodies;
            if (joints) l |= Editor.Core.Services.Physics.PhysicsService.DebugLayer.Joints;
            if (characters) l |= Editor.Core.Services.Physics.PhysicsService.DebugLayer.Characters;
            if (contacts) l |= Editor.Core.Services.Physics.PhysicsService.DebugLayer.Contacts;
            Editor.Core.Services.Physics.PhysicsService.SetDebugDraw(l != Editor.Core.Services.Physics.PhysicsService.DebugLayer.None, l);
        }

        /// <summary>Move a character capsule (feet = the entity/eye base, radius, total height) by <paramref name="move"/>
        /// and return the collision-resolved feet position. Call it each frame with your desired displacement
        /// (input + gravity). No collision world yet → returns feet+move unchanged.</summary>
        public static Vector3 MoveCharacter(Vector3 feet, float radius, float height, Vector3 move)
        {
            return MoveCharacter(feet, radius, height, move, 0);
        }

        /// <summary>As above, but <paramref name="characterId"/> (e.g. your entity's EntityId) registers this
        /// character so OTHER characters can't walk through it — for multiplayer / multiple actors.</summary>
        public static Vector3 MoveCharacter(Vector3 feet, float radius, float height, Vector3 move, long characterId)
        {
            if (Host == null) return new Vector3(feet.X + move.X, feet.Y + move.Y, feet.Z + move.Z);
            bool g; var r = Host.MoveCharacter(feet, radius, height, move, out g, characterId); _grounded = g; return r;
        }

        /// <summary>Stair-step + slope tuning (#48) for every MoveCharacter call. <paramref name="stepHeight"/> is
        /// the tallest ledge auto-climbed (16-20 cm stairs "just work" at the 0.35 default; 0 disables); the same
        /// distance is also used to SNAP down onto steps when walking downstairs (no bouncing). Surfaces steeper
        /// than <paramref name="maxSlopeDeg"/> act like walls — slide, no climb. Call once in Start(); resets to
        /// the defaults on every scene load.</summary>
        public static void SetCharacterOptions(float stepHeight = 0.35f, float maxSlopeDeg = 50f)
        {
            Editor.Core.Services.Physics.CollisionService.CharacterStepHeight = stepHeight < 0f ? 0f : stepHeight;
            Editor.Core.Services.Physics.CollisionService.CharacterMaxSlopeDeg = maxSlopeDeg;
        }

        /// <summary>Which controller resolves <see cref="MoveCharacter"/> (#187): <c>"managed"</c> (the default — the
        /// engine's own collide-and-slide) or <c>"jolt"</c> (Jolt's CharacterVirtual: smooth stairs and slopes from the
        /// physics engine, pushes dynamic props with the character's mass). The call contract is the same either way:
        /// feet, capsule, this frame's displacement in, resolved feet and <see cref="Grounded"/> out; the step height
        /// and slope limit from <see cref="SetCharacterOptions"/> apply to both. Call once in Start() — like the
        /// options it resets to "managed" on every scene load. Without a physics world (an engine build without
        /// Jolt) "jolt" silently keeps the managed controller.</summary>
        public static void SetCharacterController(string controller)
        {
            bool jolt = controller != null && (controller.Trim().ToLowerInvariant() == "jolt" || controller.Trim().ToLowerInvariant() == "v2");
            Editor.Core.Services.Physics.PhysicsService.JoltCharacters = jolt;
        }

        /// <summary>The controller <see cref="MoveCharacter"/> currently resolves with: "jolt" or "managed".</summary>
        public static string CharacterController
        {
            get { return Editor.Core.Services.Physics.PhysicsService.JoltCharacters ? "jolt" : "managed"; }
        }

        /// <summary>How hard the character shoves dynamic props (#105): the mass in kilograms Jolt gives the character
        /// controller (default 70). A heavier character pushes a chair or a crate further; a very light one is stopped by
        /// it. Applies to the "jolt" controller (the managed one pushes with the character's move). Call in Start();
        /// resets to the default on every scene load.</summary>
        public static void SetCharacterMass(float kilograms)
        {
            Editor.Core.Services.Physics.PhysicsService.CharacterMass = kilograms > 1f ? kilograms : 1f;
        }

        /// <summary>Is there room for a capsule of this <paramref name="height"/> at <paramref name="feet"/> (#105)? The
        /// stand-up ceiling check: before a crouched character rises, ask with the STANDING height — false means a
        /// ceiling, a table top or another character is in the way, so stay crouched this frame. Tests the level
        /// colliders, simulated props and other characters; <paramref name="characterId"/> excludes your own capsule
        /// (pass what you pass to <see cref="MoveCharacter(Vector3, float, float, Vector3, long)"/>). Works with both
        /// controllers. Without a collision world → true.</summary>
        public static bool CanStand(Vector3 feet, float radius, float height, long characterId = 0)
        {
            // a 2 cm lift keeps the floor the character stands on out of the test — only what is ABOVE counts
            var probe = new Vector3(feet.X, feet.Y + 0.02f, feet.Z);
            return !Editor.Core.Services.Physics.CollisionService.CapsuleOverlaps(
                new Editor.ECS.Vector3(probe.X, probe.Y, probe.Z), radius, height - 0.02f, characterId);
        }

        /// <summary>Ray straight DOWN from <paramref name="from"/> (up to <paramref name="maxDist"/>) against the world
        /// colliders — returns the <b>Tag</b> of the surface entity you're standing on (its material), or "" if
        /// nothing is below. This is the standard "what am I standing on?" query. Use it for material-based footsteps:
        /// tag your floors ("grass", "wood", "metal", …) and pick the step sound from the returned tag.</summary>
        public static string GroundTag(Vector3 from, float maxDist = 3f)
        {
            return Host != null ? (Host.GroundTag(from, maxDist) ?? "") : "";
        }

        /// <summary>Ray straight DOWN from <paramref name="from"/> against the world colliders — returns the
        /// <b>material</b> name of the surface you're standing on (the file name of that object's material, e.g.
        /// "grass" from grass.vmat), or "" if nothing is below. This is the SCALABLE alternative to
        /// <see cref="GroundTag"/> for surface-aware audio: map material→sound ONCE and every object that uses that
        /// material plays the right footstep automatically — in every scene, with no per-object tagging.</summary>
        public static string GroundMaterial(Vector3 from, float maxDist = 3f)
        {
            return Host != null ? (Host.GroundMaterial(from, maxDist) ?? "") : "";
        }

        /// <summary>Ray straight DOWN from <paramref name="from"/> — returns the <b>footstep sound</b> assigned to the
        /// surface's material in the Material Editor (a project-relative clip / .vsndc path), or "" if none. This is
        /// the EDITOR-FIRST footstep API: the sound lives on the material (assigned in the Material Editor), so a
        /// footstep script is just <c>Audio.PlayOneShot(Physics.GroundStepSound(pos), pos)</c> — adding a new surface
        /// never touches code, only the Material Editor.</summary>
        public static string GroundStepSound(Vector3 from, float maxDist = 3f)
        {
            return Host != null ? (Host.GroundStepSound(from, maxDist) ?? "") : "";
        }

        /// <summary>General raycast (#35) against the scene's SOLID colliders, any direction. Returns true
        /// and fills <paramref name="hit"/> with point/normal/distance/entity on the closest hit.
        /// <paramref name="layerMask"/> filters by entity Layer bit (default: everything). The horror
        /// workhorse: line-of-sight checks, interaction rays, "what am I looking at".
        /// <c>if (Physics.Raycast(eye, Forward, 3f, out var h) &amp;&amp; h.Tag == "Door") ...</c></summary>
        public static bool Raycast(Vector3 origin, Vector3 direction, float maxDist, out RaycastHit hit, int layerMask = ~0)
            => Raycast(origin, direction, maxDist, out hit, layerMask, 0);

        /// <summary>Raycast that passes THROUGH one entity (#341): <paramref name="ignoreEntity"/> — its handle, as
        /// RaycastHit.EntityId / a behaviour's EntityId — and everything under it never count as the hit. The car's
        /// ground probe ignores the car, the player's interaction ray ignores the player.
        /// <c>Physics.Raycast(wheel, Vector3.Down, 1f, out var h, ~0, EntityId)</c></summary>
        public static bool Raycast(Vector3 origin, Vector3 direction, float maxDist, out RaycastHit hit, int layerMask, long ignoreEntity)
        {
            hit = default(RaycastHit);
            var o = new Editor.ECS.Vector3(origin.X, origin.Y, origin.Z);
            var d = new Editor.ECS.Vector3(direction.X, direction.Y, direction.Z);
            float dl = (float)Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
            if (dl < 1e-6f || maxDist <= 0f) return false;
            var unit = new Editor.ECS.Vector3(d.X / dl, d.Y / dl, d.Z / dl);
            var ignored = ignoreEntity != 0 ? Editor.Scripting.ScriptRuntime.Instance.FindEntityByHandle(ignoreEntity) : null;
            float travelled = 0f;
            // the ignored entity is stepped over: the cast resumes just past its hit, a few times at most
            for (int attempt = 0; attempt < 4; attempt++)
            {
                Editor.ECS.Vector3 point, normal; Editor.ECS.GameEntity entity; float dist;
                float remaining = maxDist - travelled;
                if (remaining <= 0f) return false;
                // Jolt casts against the live world (static level, simulated props at their CURRENT pose, triggers
                // excluded). An explicit entity-layer mask keeps the managed cast (entity layers are not physics layers).
                bool got = Editor.Core.Services.Physics.PhysicsService.IsBuilt && layerMask == ~0
                    ? Editor.Core.Services.Physics.PhysicsService.Raycast(o, unit, remaining, out point, out normal, out entity, out dist)
                    : Editor.Core.Services.Physics.CollisionService.Raycast(o, unit, remaining, layerMask, out point, out normal, out entity, out dist);
                if (!got) return false;
                if (ignored != null && entity != null && IsSelfOrUnder(entity, ignored))
                {
                    const float step = 0.01f;
                    travelled += dist + step;
                    o = new Editor.ECS.Vector3(point.X + unit.X * step, point.Y + unit.Y * step, point.Z + unit.Z * step);
                    continue;
                }
                hit.Point = new Vector3(point.X, point.Y, point.Z);
                hit.Normal = new Vector3(normal.X, normal.Y, normal.Z);
                hit.Distance = travelled + dist;
                hit.Name = entity != null ? (entity.Name ?? "") : "";
                hit.Tag = entity != null ? (entity.Tag ?? "") : "";
                hit.EntityId = entity != null ? Editor.Scripting.ScriptRuntime.Instance.HandleForEntity(entity) : 0;
                return true;
            }
            return false;
        }

        private static bool IsSelfOrUnder(Editor.ECS.GameEntity e, Editor.ECS.GameEntity root)
        {
            for (var p = e; p != null; p = p.Parent) if (ReferenceEquals(p, root)) return true;
            return false;
        }

        /// <summary>Raycast without hit details — "is something within maxDist in that direction?".</summary>
        public static bool Raycast(Vector3 origin, Vector3 direction, float maxDist, int layerMask = ~0)
        {
            RaycastHit h;
            return Raycast(origin, direction, maxDist, out h, layerMask);
        }

        /// <summary>Re-bake an entity's colliders at its CURRENT transform. The collision world is built
        /// once per scene (static level geometry) — call this after a script MOVES a collider-carrying
        /// entity (sliding door, moving platform) so characters and raycasts see the new position. (An entity
        /// with a KINEMATIC Rigidbody does this automatically every physics step.)</summary>
        public static void RefreshCollider(long entity)
        {
            var e = Editor.Scripting.ScriptRuntime.Instance.FindEntityByHandle(entity);
            if (e == null) return;
            try
            {
                Editor.Core.Services.Physics.CollisionService.RemoveEntityShapes(e);
                Editor.Core.Services.Physics.CollisionService.AddEntityShapes(e);
                Editor.Core.Services.Physics.PhysicsService.RefreshEntity(e);
            }
            catch { }
        }

        // ---- Rigid-body physics (#100, Jolt) ----
        // Make a prop physical in the EDITOR: add a Collider (Box/Sphere/Capsule/Mesh) + a Rigidbody (Dynamic),
        // set its mass, and it falls, stacks, rolls and gets pushed by the player from the moment Play starts.
        // These calls let scripts act on that body. Without a physics-enabled engine build they are no-ops
        // (false / zero) — see Simulated.

        /// <summary>True when rigid-body physics is running (a Jolt-enabled engine build, in play). In the stub
        /// build props stay static and every force/impulse call below returns false.</summary>
        public static bool Simulated { get { return Editor.Core.Services.Physics.PhysicsService.IsBuilt; } }

        /// <summary>Push with a continuous force (N) for this frame — call every Update for a steady push (fans,
        /// thrusters, wind): <c>Physics.AddForce(crate, new Vector3(0, 0, 40f));</c>. Returns false when the entity
        /// has no dynamic Rigidbody.</summary>
        public static bool AddForce(long entity, Vector3 force) { return Host != null && Host.PhysicsAddForce(entity, force); }

        /// <summary>Force (N) applied at a world point — off-centre pushes make the body spin.</summary>
        public static bool AddForceAtPoint(long entity, Vector3 force, Vector3 worldPoint) { return Host != null && Host.PhysicsAddForceAtPoint(entity, force, worldPoint); }

        /// <summary>Instant kick (N·s) — bullets, explosions, a thrown bottle: <c>Physics.AddImpulse(hit.EntityId, Forward * 8f);</c>.
        /// An impulse of mass × Δv changes the velocity by Δv.</summary>
        public static bool AddImpulse(long entity, Vector3 impulse) { return Host != null && Host.PhysicsAddImpulse(entity, impulse); }

        /// <summary>Instant kick (N·s) at a world point — a shot at a barrel's rim tips it over:
        /// <c>Physics.AddImpulseAtPoint(hit.EntityId, dir * 6f, hit.Point);</c></summary>
        public static bool AddImpulseAtPoint(long entity, Vector3 impulse, Vector3 worldPoint) { return Host != null && Host.PhysicsAddImpulseAtPoint(entity, impulse, worldPoint); }

        /// <summary>Spin the body (N·m, accumulated until the next physics step).</summary>
        public static bool AddTorque(long entity, Vector3 torque) { return Host != null && Host.PhysicsAddTorque(entity, torque); }

        /// <summary>Set a dynamic body's velocity (m/s) directly — launching a projectile prop:
        /// <c>Physics.SetVelocity(grenade, Forward * 15f + Vector3.Up * 3f);</c></summary>
        public static bool SetVelocity(long entity, Vector3 velocity) { return Host != null && Host.PhysicsSetVelocity(entity, velocity); }

        /// <summary>Current velocity (m/s) of a simulated body — zero without one.</summary>
        public static Vector3 GetVelocity(long entity) { return Host != null ? Host.PhysicsGetVelocity(entity) : Vector3.Zero; }

        /// <summary>Set a dynamic body's angular velocity (rad/s).</summary>
        public static bool SetAngularVelocity(long entity, Vector3 velocity) { return Host != null && Host.PhysicsSetAngularVelocity(entity, velocity); }

        /// <summary>Current angular velocity (rad/s) — zero without a simulated body.</summary>
        public static Vector3 GetAngularVelocity(long entity) { return Host != null ? Host.PhysicsGetAngularVelocity(entity) : Vector3.Zero; }

        /// <summary>Switch a Rigidbody entity between kinematic (you move it — via Position/SetWorldPose or animation —
        /// and it pushes everything in its way) and dynamic (the simulation moves it). Picking up a prop:
        /// <c>Physics.SetKinematic(prop, true);</c> … drop it: <c>Physics.SetKinematic(prop, false);</c></summary>
        public static bool SetKinematic(long entity, bool kinematic) { return Host != null && Host.PhysicsSetKinematic(entity, kinematic); }

        /// <summary>Wake a resting body (bodies fall asleep when they stop moving; forces and impulses wake them too).</summary>
        public static bool WakeUp(long entity) { return Host != null && Host.PhysicsWakeUp(entity); }

        /// <summary>True when the body is asleep (at rest) — also true for entities without a simulated body.</summary>
        public static bool IsSleeping(long entity) { return Host == null || Host.PhysicsIsSleeping(entity); }

        /// <summary>Change the world gravity (m/s², default (0, -9.81, 0)) — a moon level, or zero-g. Resets to the
        /// default on every scene start.</summary>
        public static void SetGravity(Vector3 gravity) { if (Host != null) Host.PhysicsSetGravity(gravity); }

        /// <summary>Every entity whose physics body overlaps a sphere — explosions, "what's around me":
        /// <c>foreach (var id in Physics.OverlapSphere(Position, 4f)) Physics.AddImpulse(id, (Scene.WorldPositionOf(id) - Position).Normalized * 20f);</c>
        /// Triggers are not included. Empty without a physics-enabled build.</summary>
        public static long[] OverlapSphere(Vector3 center, float radius) { return Host != null ? (Host.PhysicsOverlapSphere(center, radius) ?? new long[0]) : new long[0]; }

        /// <summary>True when the entity is a moving physics body (a Dynamic or Kinematic Rigidbody).</summary>
        public static bool HasRigidbody(long entity) { return Host != null && Host.PhysicsHasRigidbody(entity); }

        /// <summary>Mass (kg) of the entity's simulated body — 0 without one.</summary>
        public static float GetMass(long entity) { return Host != null ? Host.PhysicsGetMass(entity) : 0f; }
    }

    /// <summary>
    /// Skeletal animation on OTHER entities (your own entity has PlayAnimation() directly on the
    /// behaviour). Clip = a name from the target's Animator clip table (e.g. "Walk") or a .vanim path.
    /// Animation state machines are game logic — build them in scripts with these calls.
    /// </summary>
    public static class Animation
    {
        internal static IScriptHost Host;

        /// <summary>Play a clip on an entity's Animator; fade &gt; 0 crossfades (seconds).</summary>
        public static bool Play(long entityId, string clip, float fade = 0f)
            { return Host != null && Host.PlayAnimation(entityId, clip, fade); }

        /// <summary>Freeze an entity's animation on its current pose.</summary>
        public static void Stop(long entityId) { if (Host != null) Host.StopAnimation(entityId); }

        /// <summary>Playback speed multiplier (1 = authored speed).</summary>
        public static void SetSpeed(long entityId, float speed) { if (Host != null) Host.SetAnimationSpeed(entityId, speed); }

        /// <summary>Is an animation playing on the entity? Pass a clip name to ask about that clip.</summary>
        public static bool IsPlaying(long entityId, string clip = null)
            { return Host != null && Host.IsAnimationPlaying(entityId, clip); }

        /// <summary>Current playback time in seconds.</summary>
        public static float Time(long entityId) { return Host != null ? Host.GetAnimationTime(entityId) : 0f; }

        // ---- bone-masked layers (#173) ----

        /// <summary>Play a clip on an override layer of another entity, restricted to a bone mask
        /// ("Spine1+" = spine and everything below it in the hierarchy).</summary>
        public static bool PlayLayered(long entityId, string clip, int layer, string mask, float weight = 1f, float fade = 0f)
            { return Host != null && Host.PlayLayeredAnimation(entityId, clip, layer, mask, weight, fade); }

        /// <summary>Blend a layer in/out at runtime (0..1).</summary>
        public static void SetLayerWeight(long entityId, int layer, float weight)
            { if (Host != null) Host.SetAnimationLayerWeight(entityId, layer, weight); }

        /// <summary>Stop an override layer — the base clip takes its bones back.</summary>
        public static void StopLayer(long entityId, int layer)
            { if (Host != null) Host.StopLayeredAnimation(entityId, layer); }

        // ---- runtime procedural bone control (#178) ----

        /// <summary>Add a persistent additive LOCAL rotation (Euler degrees) to a bone of an animated entity
        /// each frame — aim-offset / lean / recoil. The delta carries all descendant bones, so a spine pitch
        /// moves chest+arms+weapon as one and the gun stays in the hands. (0,0,0) clears that bone.</summary>
        public static void SetBoneAdditiveRotation(long entityId, string bone, Vector3 eulerDeg)
            { if (Host != null) Host.SetBoneAdditiveRotation(entityId, bone, eulerDeg); }

        /// <summary>Runtime per-bone SCALE (1 = normal, 0 = hide the bone + its descendants) — strip a first-person
        /// body down to arms+gun by hiding the legs and head so looking up/down never shows the player's own body.</summary>
        public static void SetBoneScaleOverride(long entityId, string bone, float scale)
            { if (Host != null) Host.SetBoneScaleOverride(entityId, bone, scale); }

        /// <summary>Hide (or show) a bone's vertices. includeDescendants = the whole limb below it, like a 0 scale
        /// override. false = ONLY that bone: the first-person arms trick — collapse Hips/Spine/Shoulders so the torso
        /// never shows, while the arm bones hanging off them keep rendering.</summary>
        public static void SetBoneHidden(long entityId, string bone, bool hidden, bool includeDescendants = true)
            { if (Host != null) Host.SetBoneHidden(entityId, bone, hidden, includeDescendants); }

        /// <summary>Clear every runtime bone-rotation override on an entity's animator.</summary>
        public static void ClearBoneOverrides(long entityId)
            { if (Host != null) Host.ClearBoneOverrides(entityId); }

        /// <summary>Blend a Two-Bone IK chain at runtime (#179): 0 = animation only, 1 = full IK.
        /// tipBone selects the chain (the TwoBoneIk component whose TipBone matches); null/empty hits
        /// every chain on the entity. Release the support hand during a reload:
        /// <c>Animation.SetIkWeight(chr, "mixamorig:LeftHand", 0f);</c> then back to 1 when done.</summary>
        public static void SetIkWeight(long entityId, string tipBone, float weight)
            { if (Host != null) Host.SetIkWeight(entityId, tipBone, weight); }

        /// <summary>Pull an IK chain (selected by its tip bone, e.g. "mixamorig:LeftHand") to a WORLD-space
        /// position every frame until <see cref="ClearIkTarget"/> — the support hand travelling to the mag
        /// well during a reload, a hand landing on a door handle. The entity's TwoBoneIk component still defines
        /// the limb and the weight; the animated wrist orientation is kept. Set it in LateUpdate (after the
        /// camera/rig moved) so the target is exact for this frame.</summary>
        public static void SetIkTarget(long entityId, string tipBone, Vector3 worldPosition)
            { if (Host != null) Host.SetIkWorldTarget(entityId, tipBone, worldPosition, Vector3.Zero, false); }

        /// <summary>Same, with the hand's WORLD orientation (engine Euler degrees) — the palm wraps the grip.</summary>
        public static void SetIkTarget(long entityId, string tipBone, Vector3 worldPosition, Vector3 worldRotationEuler)
            { if (Host != null) Host.SetIkWorldTarget(entityId, tipBone, worldPosition, worldRotationEuler, true); }

        /// <summary>Back to the component's own (bone-relative / auto-grip) target. null/empty = every chain.</summary>
        public static void ClearIkTarget(long entityId, string tipBone)
            { if (Host != null) Host.ClearIkWorldTarget(entityId, tipBone); }

        /// <summary>Move the target of an entity's TwoBoneIk chain(s) — the offset from the chain's TARGET bone, in that
        /// bone's space (position in metres, rotation in engine Euler degrees) — e.g. the support hand's spot on the
        /// foregrip of a different weapon after a weapon switch. Solved in the same frame as the animation, so the hand
        /// never lags the bone it follows. tipBone null/empty = every chain of the entity.</summary>
        public static void SetIkOffset(long entityId, string tipBone, Vector3 position, Vector3 rotationEulerDeg)
        {
            var e = Editor.Scripting.ScriptRuntime.Instance.FindEntityByHandle(entityId);
            if (e == null) return;
            foreach (var ik in e.GetComponents<Editor.ECS.Components.Animation.TwoBoneIk>())
            {
                if (!string.IsNullOrEmpty(tipBone) && !string.Equals(ik.TipBone, tipBone, System.StringComparison.OrdinalIgnoreCase)) continue;
                ik.TargetOffsetPosition = new Editor.ECS.Vector3(position.X, position.Y, position.Z);
                ik.TargetOffsetRotation = new Editor.ECS.Vector3(rotationEulerDeg.X, rotationEulerDeg.Y, rotationEulerDeg.Z);
            }
        }

        /// <summary>Swing an IK chain's elbow/knee around the root->target axis (degrees; 0 = the animation's natural
        /// bend plane) — steer a first-person elbow down and out of the view.</summary>
        public static void SetIkPoleAngle(long entityId, string tipBone, float degrees)
            { if (Host != null) Host.SetIkPoleAngle(entityId, tipBone, degrees); }

        // ---- hand poses / look-at / foot IK (#147) — work on ANY skeleton, the bones are detected ----

        /// <summary>Pose a hand from a preset, blended in over <paramref name="blendSeconds"/>. side = "Left", "Right" or
        /// "Both"; preset = "Open", "Relaxed", "Fist", "Trigger", "Grip" or "Point". Works on any character — the hand and
        /// finger bones are detected (Mixamo, Unreal, Rigify, Unity, Biped …); a Hand Pose component on that side supplies
        /// its rig settings. The authored pose comes back with <see cref="ClearHandPose"/>. False when the entity has no
        /// skeleton or the side/preset is unknown.
        /// <code>Animation.SetHandPose(bot, "Right", "Fist", 1f, 0.2f);</code></summary>
        public static bool SetHandPose(long entityId, string side, string preset, float weight = 1f, float blendSeconds = 0.15f)
            { return Host != null && Host.SetHandPose(entityId, side, preset, weight, blendSeconds); }

        /// <summary>Pose a hand with explicit curls — degrees per joint (knuckle, middle, tip); positive closes the hand.
        /// <code>Animation.SetHandPose(bot, "Left", new Vector3(20f, 30f, 10f), new Vector3(70f, 85f, 45f),
        ///     new Vector3(70f, 85f, 45f), new Vector3(70f, 85f, 45f), new Vector3(30f, 30f, 20f), 1f, 0.2f);</code></summary>
        public static bool SetHandPose(long entityId, string side, Vector3 index, Vector3 middle, Vector3 ring, Vector3 pinky, Vector3 thumb,
                                       float weight = 1f, float blendSeconds = 0.15f)
            { return Host != null && Host.SetHandPoseCurls(entityId, side, index, middle, ring, pinky, thumb, 0f, weight, blendSeconds); }

        /// <summary>Drop the script hand pose — the Hand Pose component's authored pose applies again. side null = both.
        /// <code>Animation.ClearHandPose(bot, "Right");</code></summary>
        public static void ClearHandPose(long entityId, string side = null)
            { if (Host != null) Host.ClearHandPose(entityId, side); }

        /// <summary>Turn a character's head (neck, upper spine) toward a WORLD point until cleared — within the Look-At IK
        /// component's limits, or default limits (70° yaw / 40° pitch) on a character without one.
        /// <code>Animation.SetLookAtTarget(bot, Scene.WorldPositionOf(player));</code></summary>
        public static void SetLookAtTarget(long entityId, Vector3 worldPoint)
            { if (Host != null) Host.SetLookAtPoint(entityId, worldPoint); }

        /// <summary>Turn a character's head toward ANOTHER entity, tracked every frame (its head bone when it is an
        /// animated character, else its origin + the component's target offset).
        /// <code>Animation.SetLookAtTarget(bot, Scene.Find("Player"));</code></summary>
        public static void SetLookAtTarget(long entityId, long targetEntity)
            { if (Host != null) Host.SetLookAtEntity(entityId, targetEntity); }

        /// <summary>Stop a script look target (the Look-At IK component's own target entity applies again).
        /// <code>Animation.ClearLookAtTarget(bot);</code></summary>
        public static void ClearLookAtTarget(long entityId)
            { if (Host != null) Host.ClearLookAtTarget(entityId); }

        /// <summary>Blend look-at in/out (0..1, smoothed by the component's smoothing time); a negative value returns to the
        /// component's own weight. <code>Animation.SetLookAtWeight(bot, 0f);</code></summary>
        public static void SetLookAtWeight(long entityId, float weight)
            { if (Host != null) Host.SetLookAtWeight(entityId, weight); }

        /// <summary>Blend foot IK (feet planted on uneven ground): 0 = animation only, 1 = full. Enables foot IK with detected
        /// legs on a character without a Foot IK component; a negative value returns to the component's own weight.
        /// <code>Animation.SetFootIkWeight(bot, 1f);</code></summary>
        public static void SetFootIkWeight(long entityId, float weight)
            { if (Host != null) Host.SetFootIkWeight(entityId, weight); }

        // ---- synced playback groups (#174) ----

        /// <summary>Start clips on several entities frame-locked to ONE clock — the reload pair:
        /// <c>Animation.PlaySynced(new[]{ chr, gun }, new[]{ "reload_char", "reload_weapon" });</c>
        /// Members share normalized time (drift-free by construction); pause/speed/stop hit the whole
        /// group atomically. Returns the group id (0 = failed).</summary>
        public static int PlaySynced(long[] entities, string[] clips, float speed = 1f, float fade = 0f)
            { return Host != null ? Host.PlaySyncedAnimation(entities, clips, speed, fade) : 0; }

        /// <summary>Pause or resume a synced group as one.</summary>
        public static void PauseSynced(int groupId, bool paused = true)
            { if (Host != null) Host.PauseSyncedAnimation(groupId, paused); }

        /// <summary>Change a synced group's speed (all members together).</summary>
        public static void SetSyncedSpeed(int groupId, float speed)
            { if (Host != null) Host.SetSyncedAnimationSpeed(groupId, speed); }

        /// <summary>Dissolve a synced group; members freeze on their current pose.</summary>
        public static void StopSynced(int groupId)
            { if (Host != null) Host.StopSyncedAnimation(groupId); }

        // ---- bone sockets (#170/#171) ----

        /// <summary>Attach an entity to a bone of an animated entity (offsets in bone space). It follows
        /// the bone through every clip until detached. False on unknown bone or attach cycle.</summary>
        public static bool Attach(long entityId, long targetEntity, string bone)
            { return Attach(entityId, targetEntity, bone, Vector3.Zero, Vector3.Zero); }
        public static bool Attach(long entityId, long targetEntity, string bone, Vector3 offsetPos, Vector3 offsetRotEuler)
            { return Host != null && Host.AttachEntityToBone(entityId, targetEntity, bone, offsetPos, offsetRotEuler); }

        /// <summary>Detach an entity from its bone. keepWorldPosition=true = no pop (stays where the hand
        /// left it); false restores its pre-attach local transform.</summary>
        public static bool Detach(long entityId, bool keepWorldPosition = true)
            { return Host != null && Host.DetachEntityFromBone(entityId, keepWorldPosition); }

        /// <summary>World transform of a bone THIS frame (animated pose while playing, else bind pose) —
        /// the muzzle-raycast / VFX-spawn query. Rotation is Euler degrees (engine ZXY order).</summary>
        public static bool TryGetBoneTransform(long targetEntity, string bone, out Vector3 position, out Vector3 rotationEuler)
        {
            position = default(Vector3); rotationEuler = default(Vector3);
            return Host != null && Host.TryGetBoneTransform(targetEntity, bone, out position, out rotationEuler);
        }

        /// <summary>World position of a bone (Vector3.Zero when the bone/skeleton can't resolve).</summary>
        public static Vector3 BonePosition(long targetEntity, string bone)
        {
            Vector3 p, r;
            return Host != null && Host.TryGetBoneTransform(targetEntity, bone, out p, out r) ? p : Vector3.Zero;
        }

        /// <summary>Script handles of every entity currently socketed to the target's bones.</summary>
        public static long[] GetAttachedEntities(long targetEntity)
            { return Host != null ? Host.GetAttachedEntities(targetEntity) : new long[0]; }

        /// <summary>Place a rigid attachment on a bone from a bone-LOCAL offset: given the bone's world transform
        /// (from TryGetBoneTransform) and a local offset (pos meters + rot euler in the bone's frame), returns the
        /// attachment's WORLD pos + euler. The offset ROTATES with the bone, so the weapon stays glued to the hand
        /// through every animation — unlike a world-space add, which drifts off when the hand turns. The Socket
        /// Editor authors the offset with this exact math, so editor placement == in-game placement.</summary>
        public static void ComposeBoneAttach(Vector3 bonePos, Vector3 boneEuler, Vector3 offsetPos, Vector3 offsetEuler,
                                             out Vector3 worldPos, out Vector3 worldEuler)
        {
            worldPos = bonePos; worldEuler = boneEuler;
            if (Host != null) Host.ComposeBoneAttach(bonePos, boneEuler, offsetPos, offsetEuler, out worldPos, out worldEuler);
        }
    }

    /// <summary>
    /// Procedural camera/weapon FEEL primitives (#176): recoil kicks, damage flinches, idle sway and
    /// breathing bob as additive offset channels with spring-damper recovery. Channels stack (a recoil
    /// impulse rides on top of the sway) and never touch entity transforms — the camera offset is
    /// composed at render time, attachment offsets inside the bone socket. What a "pistol recoil
    /// pattern" IS remains game code: scripts decide when and how hard to Kick.
    /// </summary>
    public static class CameraFX
    {
        internal static IScriptHost Host;

        /// <summary>Impulse on the camera: rotation in Euler degrees (X = pitch up), position in
        /// camera-relative meters (Z = forward). Spring-damper pulls it back to zero.
        /// Pistol: <c>CameraFX.Kick(new Vector3(-1.6f, 0, 0), new Vector3(0, 0, -0.03f));</c></summary>
        public static void Kick(Vector3 rotationDegrees, Vector3 position)
            { if (Host != null) Host.CameraFxKick(rotationDegrees, position); }

        /// <summary>Impulse on a socket-attached entity, in ITS local axes — the weapon kicks back in
        /// the hand while staying glued to the bone.</summary>
        public static void Kick(long entityId, Vector3 rotationDegrees, Vector3 position)
            { if (Host != null) Host.CameraFxKickEntity(entityId, rotationDegrees, position); }

        /// <summary>Continuous seeded noise on a camera channel (slot 0..3): breathing, idle sway,
        /// scare rumble. Amplitudes 0 stop the slot. <c>CameraFX.Sway(0, 0.004f, 0.35f, 0.4f);</c></summary>
        public static void Sway(int slot, float positionAmplitude, float rotationAmplitudeDeg, float frequencyHz)
            { if (Host != null) Host.CameraFxSway(slot, positionAmplitude, rotationAmplitudeDeg, frequencyHz); }

        /// <summary>Continuous noise on an attached entity's channel (flashlight/weapon sway).</summary>
        public static void Sway(long entityId, int slot, float positionAmplitude, float rotationAmplitudeDeg, float frequencyHz)
            { if (Host != null) Host.CameraFxSwayEntity(entityId, slot, positionAmplitude, rotationAmplitudeDeg, frequencyHz); }

        /// <summary>Stop a camera sway slot.</summary>
        public static void StopSway(int slot) { Sway(slot, 0f, 0f, 1f); }
        public static void StopSway(long entityId, int slot) { Sway(entityId, slot, 0f, 0f, 1f); }

        /// <summary>Recovery feel: (120, 22) = snappy pistol (default), (60, 10) = heavy shotgun wobble.</summary>
        public static void SetSpring(float stiffness, float damping)
            { if (Host != null) Host.CameraFxSpring(stiffness, damping); }

        /// <summary>Noise seed — a fixed seed makes sway replay-stable.</summary>
        public static void SetSeed(int seed) { if (Host != null) Host.CameraFxSeed(seed); }
    }

    /// <summary>
    /// Immediate-mode 2D UI drawn by the engine OVER the 3D (same swapchain — works over the live game).
    /// Call these from a behaviour's Update each frame; coordinates are viewport pixels (top-left origin).
    /// This is the generic engine UI; a game builds its own lobby/HUD with it (no UI code in the engine).
    /// </summary>
    public static class UI
    {
        internal static IScriptHost Host;

        /// <summary>Viewport size in pixels.</summary>
        public static float Width { get { return Host != null ? Host.UIWidth() : 0f; } }
        public static float Height { get { return Host != null ? Host.UIHeight() : 0f; } }
        /// <summary>Mouse position in viewport pixels (top-left origin).</summary>
        public static float MouseX { get { return Host != null ? Host.UIMouseX() : 0f; } }
        public static float MouseY { get { return Host != null ? Host.UIMouseY() : 0f; } }
        public static bool MouseDown { get { return Host != null && Host.UIMouseDown(); } }

        /// <summary>Filled rectangle (radius &gt; 0 = rounded).</summary>
        public static void Rect(float x, float y, float w, float h, Color c, float radius)
        {
            if (Host != null) Host.UIRect(x, y, w, h, c.R, c.G, c.B, c.A, radius);
        }
        public static void Rect(float x, float y, float w, float h, Color c) { Rect(x, y, w, h, c, 0f); }

        /// <summary>Text in a box. align: 0 left, 1 center, 2 right. weight: 400/600/700.</summary>
        public static void Text(string text, float x, float y, float w, float h, float size, Color c, int align, int weight)
        {
            if (Host != null) Host.UIText(x, y, w, h, text, size, c.R, c.G, c.B, c.A, align, weight);
        }
        public static void Text(string text, float x, float y, float w, float h, float size, Color c) { Text(text, x, y, w, h, size, c, 0, 600); }

        public static void Line(float x1, float y1, float x2, float y2, Color c, float thick)
        {
            if (Host != null) Host.UILine(x1, y1, x2, y2, c.R, c.G, c.B, c.A, thick);
        }

        /// <summary>Textured quad (PNG/JPG). path = absolute or project-relative. tint multiplies; tint.A = opacity.</summary>
        public static void Image(string path, float x, float y, float w, float h, Color tint)
        {
            if (Host != null) Host.UIImage(x, y, w, h, path, tint.R, tint.G, tint.B, tint.A);
        }
        public static void Image(string path, float x, float y, float w, float h) { Image(path, x, y, w, h, new Color(1f, 1f, 1f, 1f)); }

        /// <summary>True while the cursor is inside the box.</summary>
        public static bool Hover(float x, float y, float w, float h)
        {
            float mx = MouseX, my = MouseY;
            return mx >= x && mx <= x + w && my >= y && my <= y + h;
        }

        /// <summary>A clickable button (returns true on the click). Lightens on hover.</summary>
        public static bool Button(float x, float y, float w, float h, string label, Color bg, Color fg, float size, float radius)
        {
            bool hover = Hover(x, y, w, h);
            Color face = hover ? new Color(Clamp01(bg.R + 0.09f), Clamp01(bg.G + 0.09f), Clamp01(bg.B + 0.09f), bg.A) : bg;
            Rect(x, y, w, h, face, radius);
            Text(label, x, y, w, h, size, fg, 1, 700);
            return hover && Host != null && Host.UIMousePressed();
        }

        private static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }
    }

    /// <summary>A loaded retained-UI screen (.vui). Drive named slots + read events by stable id; gameplay logic
    /// stays in the script — the canvas is just a renderer/router.</summary>
    public sealed class VuiHandle
    {
        internal Editor.UI.Vui.VuiCanvas C;
        internal VuiHandle(Editor.UI.Vui.VuiCanvas c) { C = c; }
        public bool IsValid { get { return C != null; } }
        public void Show() { if (C != null) Editor.UI.Vui.VuiStack.Instance.Show(C); }
        public void Hide() { if (C != null) Editor.UI.Vui.VuiStack.Instance.Hide(C); }
        public void SetValue(string id, float v) { if (C != null) C.SetValue(id, v); }
        public void SetText(string id, string t) { if (C != null) C.SetText(id, t); }
        public void SetVisible(string id, bool v) { if (C != null) C.SetVisible(id, v); }
        public void SetColor(string id, Color c) { if (C != null) C.SetColor(id, c.R, c.G, c.B, c.A); }
        public void SetImage(string id, string asset) { if (C != null) C.SetImage(id, asset); }
        public void SetList(string id, System.Collections.Generic.IReadOnlyList<System.Collections.Generic.IReadOnlyDictionary<string, string>> rows) { if (C != null) C.SetList(id, rows); }
        public bool WasClicked(string id) { return C != null && C.WasClicked(id); }
        public float GetSlider(string id) { return C != null ? C.GetSlider(id) : 0f; }
        public bool GetToggle(string id) { return C != null && C.GetToggle(id); }
        public string GetText(string id) { return C != null ? C.GetText(id) : ""; }
        public int GetStep(string id) { return C != null ? C.GetStep(id) : 0; }
        public int GetCapturedKey(string id) { return C != null ? C.GetCapturedKey(id) : 0; }
    }

    /// <summary>Retained-mode 2D UI: load .vui screens, stack them, drive them by id. Sits beside the immediate-mode
    /// <see cref="UI"/> facade (both draw into the same frame). Gameplay stays in scripts.</summary>
    public static class Gui
    {
        public static VuiHandle Load(string name) { return new VuiHandle(Editor.UI.Vui.VuiStack.Instance.Load(name)); }
        public static VuiHandle Push(string name) { return new VuiHandle(Editor.UI.Vui.VuiStack.Instance.Push(name)); }
        public static void Pop() { Editor.UI.Vui.VuiStack.Instance.Pop(); }
        public static bool HasScreens { get { return Editor.UI.Vui.VuiStack.Instance.HasActiveScreens; } }

        /// <summary>One-call modal yes/no confirmation (#45) — no .vui authoring needed. Blocks input
        /// and gameplay, frees the cursor, and is gamepad-navigable like any screen:
        /// <c>Gui.Confirm("Quit?", "Unsaved progress will be lost.", delegate { App.Quit(); }, null);</c></summary>
        public static void Confirm(string title, string message, System.Action onYes, System.Action onNo)
            { Editor.UI.Vui.VuiDialogs.Confirm(title, message, onYes, onNo); }
    }

    /// <summary>Generic engine settings a script's options menu applies (the UI only surfaces values; the script
    /// reads the widgets + calls these). Resolution / render-scale / volume land with feature #3 (ESC menu).</summary>
    public static class Settings
    {
        public static void SetVSync(bool on) { Editor.DllWrapper.VortexAPI.SetGameHostVSync(on); }
        public static void ToggleFullscreen() { Editor.DllWrapper.VortexAPI.GameHostToggleFullscreen(); }
        public static bool IsFullscreen { get { return Editor.DllWrapper.VortexAPI.GameHostIsFullscreen(); } }

        /// <summary>Set fullscreen to a specific state (idempotent — toggles only if it differs).</summary>
        public static void SetFullscreen(bool on) { if (on != IsFullscreen) ToggleFullscreen(); }

        /// <summary>Field of view in degrees (the renderer-global projection FOV).</summary>
        public static void SetFieldOfView(float degrees) { Camera.SetFieldOfView(degrees); }

        /// <summary>Window resolution (windowed only); resizes the client area + swapchain.</summary>
        public static void SetResolution(int width, int height) { Editor.DllWrapper.VortexAPI.GameHostSetResolution(width, height); }

        /// <summary>Render scale 0.25..2.0 — the 3D scene renders into a scaled offscreen RT then upscales (perf).
        /// 1.0 = native. Stored in the renderer now; the scaled-RT upscale pass applies it.</summary>
        public static void SetRenderScale(float scale) { Editor.DllWrapper.VortexAPI.SetRenderScale(scale); }
        public static float RenderScale { get { return Editor.DllWrapper.VortexAPI.GetRenderScale(); } }

        /// <summary>DLSS quality: 0=Off, 1=Quality, 2=Balanced, 3=Performance, 4=Ultra Performance. A non-off mode
        /// renders the 3D at a lower resolution and AI-upscales it to native (big perf win on RTX). Only takes
        /// visible effect when <see cref="DlssSupported"/> is true; otherwise it falls back to a bilinear upscale.</summary>
        public static void SetDlssMode(int mode) { Editor.DllWrapper.VortexAPI.SetDlssMode(mode); }
        public static int DlssMode { get { return Editor.DllWrapper.VortexAPI.GetDlssMode(); } }

        /// <summary>DLSS Frame Generation: 0=Off, 1=x2, 2=x3, 3=x4 — the GPU inserts N AI-generated frames at Present
        /// per real frame (smoother motion). SEPARATE from <see cref="SetDlssMode"/> (super-resolution). Enables
        /// Reflex internally. Needs <see cref="DlssSupported"/>; best at LOW real framerates with VSync on.</summary>
        public static void SetFrameGenMode(int mode) { Editor.DllWrapper.VortexAPI.SetFrameGenMode(mode); }
        public static int FrameGenMode { get { return Editor.DllWrapper.VortexAPI.GetFrameGenMode(); } }
        /// <summary>Smoothed PRESENTED-FPS rate (real + AI-generated frames/sec) — the "Shown FPS" with Frame Gen on.
        /// Accumulated once per frame in the engine, so reading it from multiple places is safe. 0 when FG is off.</summary>
        public static int FrameGenPresentedFps { get { return Editor.DllWrapper.VortexAPI.FrameGenPresentedFps(); } }

        /// <summary>Current REAL (rendered) frames per second — the engine frame counter. With Frame Gen on this stays
        /// at the rendered rate (the generated frames show up in <see cref="FrameGenPresentedFps"/>, not here).</summary>
        public static int CurrentFps { get { return Editor.DllWrapper.VortexAPI.CurrentFPS; } }

        /// <summary>Master volume 0..1: the mixer's Master bus — the same as Audio.SetBusVolume("Master", v), so a
        /// shipped game keeps the player's choice across restarts. (It used to be stored and never applied.)</summary>
        public static float MasterVolume { get { return Audio.GetBusVolume("Master"); } }
        public static void SetMasterVolume(float v) { Audio.SetBusVolume("Master", v < 0f ? 0f : (v > 1f ? 1f : v)); }

        /// <summary>The selected GPU's name (e.g. "NVIDIA GeForce RTX 5070").</summary>
        public static string GpuName { get { return Editor.DllWrapper.VortexAPI.GpuName(); } }
        /// <summary>True only on an NVIDIA RTX GPU — gate DLSS options on this (render-scale is the universal fallback).</summary>
        public static bool DlssSupported { get { return Editor.DllWrapper.VortexAPI.GpuSupportsDlss(); } }
    }

    /// <summary>A script handle to one entity's Light component (get it via GetLight() in a VortexBehaviour).
    /// Property writes go straight to the managed component, which the renderer re-reads every submitted frame;
    /// each write also marks the scene runtime-dirty so the shipped game's submit-once loop re-submits — the
    /// same mechanism scripted Transforms use. This is the flashlight/flicker API (Welle A #26).</summary>
    public class Light
    {
        private readonly Editor.ECS.Components.Lighting.Light _c;
        internal Light(Editor.ECS.Components.Lighting.Light c) { _c = c; }

        private static void Dirty() { Editor.Core.Services.SceneRenderService.RuntimeDirty = true; }

        /// <summary>Switch the light on/off (the flashlight toggle). The default sun stays suppressed
        /// while the component exists, so toggling off really means darkness.</summary>
        public bool Enabled { get { return _c.IsEnabled; } set { _c.IsEnabled = value; Dirty(); } }
        /// <summary>Brightness. Modulate per Update() for flicker.</summary>
        public float Intensity { get { return _c.Intensity; } set { _c.Intensity = value; Dirty(); } }
        /// <summary>Reach in world units (point/spot).</summary>
        public float Range { get { return _c.Range; } set { _c.Range = value; Dirty(); } }
        /// <summary>Outer cone angle in degrees (spot).</summary>
        public float SpotAngle { get { return _c.SpotAngle; } set { _c.SpotAngle = value; Dirty(); } }
        /// <summary>Inner full-brightness cone angle in degrees (spot).</summary>
        public float InnerSpotAngle { get { return _c.InnerSpotAngle; } set { _c.InnerSpotAngle = value; Dirty(); } }
        /// <summary>Light color, each channel 0..1.</summary>
        public void SetColor(float r, float g, float b) { _c.ColorR = r; _c.ColorG = g; _c.ColorB = b; Dirty(); }

        /// <summary>Real shadow mapping for a SPOT light (#23 — the flashlight). The renderer draws the
        /// scene from this light's view each frame; the FIRST shadow-enabled spot wins (one shadow map).
        /// On by default for new lights: <c>GetLight().CastShadows = false;</c> opts out.</summary>
        public bool CastShadows
        {
            get { return _c.ShadowType != Editor.ECS.Components.Lighting.ShadowType.None; }
            set { _c.ShadowType = value ? Editor.ECS.Components.Lighting.ShadowType.Hard
                                        : Editor.ECS.Components.Lighting.ShadowType.None; Dirty(); }
        }
        /// <summary>How dark the shadowed area gets: 1 = pitch black (horror default), 0 = shadows off.</summary>
        public float ShadowStrength { get { return _c.ShadowStrength; } set { _c.ShadowStrength = value; Dirty(); } }

        /// <summary>Procedural flicker in [0..1] — multiply into Intensity each Update() for a dying
        /// bulb: <c>light.Intensity = 40f * Light.Flicker(t, 14f);</c>. Two detuned sines, cheap + loopless.</summary>
        public static float Flicker(float time, float speed = 12f)
        {
            float s = (float)System.Math.Sin(time * speed);
            float s2 = (float)System.Math.Sin(time * speed * 2.3f + 1.7f);
            float v = 0.5f + 0.35f * s + 0.15f * s2;
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }
    }

    /// <summary>Lighting/atmosphere control for game scripts — flicker, lightning, mood. With submit-once a static
    /// scene keeps whatever the script last set, so per-frame changes here drive a living, flickering environment.
    /// Per-entity control lives on GetLight() (Vortex.Light); this class is the GLOBAL ambient/sun/fog surface.</summary>
    public static class Lighting
    {
        /// <summary>Global ambient strength (0 = pitch black, 1 = flat-lit). Dip it for darkness/flicker.
        /// Registered as an OVERRIDE so scene re-submits don't stomp it back to the editor default.</summary>
        public static void SetAmbient(float strength)
        {
            Editor.Core.Services.SceneRenderService.ScriptAmbientOverride = strength;
            Editor.DllWrapper.VortexAPI.SetAmbientLightStrength(strength);
        }
        /// <summary>The sun/key directional light: direction (dx,dy,dz), color (r,g,b 0..1), and intensity.
        /// castShadows (#24) renders cascaded shadow maps for the sun — moonlight through a window throws
        /// real moving shadows; shadowDistance = how far from the camera shadows reach (world units).</summary>
        public static void SetDirectional(float dx, float dy, float dz, float r, float g, float b, float intensity,
                                          bool castShadows = false, float shadowStrength = 1f, float shadowDistance = 80f)
            { Editor.DllWrapper.VortexAPI.SetDirectionalLightParams(dx, dy, dz, r, g, b, intensity,
                  castShadows, shadowStrength, 0.0008f, shadowDistance); }
        public static void ClearLights() { Editor.DllWrapper.VortexAPI.ClearAllLights(); }
    }

    /// <summary>Scene atmosphere for game scripts (Welle A #27): exp2 distance fog with optional ground
    /// mist. The flashlight cone visibly "cuts" into the fog. Setting persists until changed — call once
    /// in Start(). <c>Atmosphere.SetFog(density: 0.14f, heightY: 1.2f, heightFalloff: 0.6f, r: 0.016f, g: 0.02f, b: 0.027f);</c></summary>
    public static class Atmosphere
    {
        /// <summary>Enable fog: <paramref name="density"/> &gt; 0 (try 0.05–0.2 for a cellar, 0.005–0.02 outdoors).
        /// With <paramref name="heightFalloff"/> = 0 it is uniform distance fog. With heightFalloff &gt; 0 it is HEIGHT
        /// fog (#328): the fog is uniform up to <paramref name="heightY"/> and thins out above it as
        /// exp(-heightFalloff · (y − heightY)) — 0.06 halves it every ~12 m, 0.3 every ~2 m — integrated along the view
        /// ray, so a camera high above the layer sees only the fog the ray actually crosses and a camera inside it sees
        /// the full density. The sky is never fogged. Colors are linear 0..1 (keep them DARK for horror).</summary>
        public static void SetFog(float density, float heightY = 0f, float heightFalloff = 0f,
                                  float r = 0.02f, float g = 0.025f, float b = 0.035f)
            { Editor.DllWrapper.VortexAPI.SetFog(r, g, b, density, heightY, heightFalloff); }

        /// <summary>Turn fog off.</summary>
        public static void ClearFog() { Editor.DllWrapper.VortexAPI.SetFog(0f, 0f, 0f, 0f, 0f, 0f); }

        /// <summary>Scene exposure in stops BEFORE the tonemapper — the camera's aperture. 0 = the scene's classic look, +1
        /// doubles the light the ACES curve receives (a sunlit desert: +1.5 … +2.5, a night: −1). Unlike the LDR grading
        /// exposure it brightens without clipping; the HDR sky follows it. Stays until changed (the scene settings
        /// re-apply on Stop).</summary>
        public static void SetExposure(float stops) { Editor.DllWrapper.VortexAPI.SetSceneExposureStops(stops); }

        /// <summary>Aerial perspective: the sun's forward-scatter glow in the fog when looking towards it — the haze brightens and
        /// warms around the sun. 0 = the flat fog colour, 0.3–0.6 a desert noon, 1–2 a low sun.</summary>
        public static void SetFogSunGlow(float glow) { Editor.DllWrapper.VortexAPI.SetFogSunGlowStrength(glow); }

        /// <summary>Volumetric fog (#119): the air itself scatters light towards the camera — the flashlight becomes a visible
        /// cone, point lights glow, the sun throws shafts, all stopped by the shadow maps. <paramref name="density"/> scales the
        /// scattering (the height profile and colour come from <see cref="SetFog"/>, which may stay at density 0),
        /// <paramref name="anisotropy"/> is the Henyey-Greenstein g (0 even, towards 1 bright halos looking into lights),
        /// <paramref name="noise"/> / <paramref name="noiseScale"/> / <paramref name="noiseSpeed"/> the wind-blown patchiness,
        /// <paramref name="sun"/> / <paramref name="lights"/> / <paramref name="ambient"/> the contributions, <paramref name="steps"/> the
        /// quality (8..48). Density is per metre (0.01 a haze, 0.03 fog, 0.1 a thick cellar). Scare ramps: call it again with a higher density. <c>Atmosphere.SetVolumetricFog(0.08f, anisotropy: 0.6f);</c></summary>
        public static void SetVolumetricFog(float density = 0.03f, float anisotropy = 0.55f, float maxDistance = 60f, float noise = 0.5f,
                                            float noiseScale = 6f, float noiseSpeed = 0.35f, float sun = 1f, float lights = 1f, float ambient = 0.35f,
                                            int steps = 24, bool shadows = true)
            { Editor.DllWrapper.VortexAPI.SetVolumetricFog(density > 0f, density, anisotropy, maxDistance, noise, noiseScale, noiseSpeed, sun, lights, ambient, steps, shadows); }
        /// <summary>Volumetric fog off (the analytic fog of <see cref="SetFog"/> stays).</summary>
        public static void ClearVolumetricFog() { Editor.DllWrapper.VortexAPI.SetVolumetricFog(false, 0f, 0.55f, 60f, 0f, 6f, 0f, 1f, 1f, 0.35f, 24, true); }
    }

    /// <summary>The sky from game scripts (#349): swap the equirect texture per area or time of day, or set a gradient.
    /// The script's sky wins over the scene's Skybox component until <see cref="Clear"/> or the end of play; ambient
    /// stays with <see cref="Lighting.SetAmbient"/>. The sky is drawn by the renderer's own pass — behind everything,
    /// around whichever camera renders, never fogged.</summary>
    public static class Sky
    {
        /// <summary>An equirect texture (project-relative, e.g. "Assets/Skies/dusk.hdr"). <paramref name="exposure"/>
        /// scales it, <paramref name="rotationDeg"/> turns it around the up axis so the sun in the image lines up with
        /// the directional light.</summary>
        public static void SetTexture(string path, float exposure = 1f, float rotationDeg = 0f)
        {
            Editor.Core.Services.SceneRenderService.ScriptSky = new Editor.Core.Services.SceneRenderService.SkyOverride { TexturePath = path, Exposure = exposure, RotationDeg = rotationDeg };
            Editor.Core.Services.SceneRenderService.RuntimeDirty = true;
        }

        /// <summary>A three-colour gradient sky (linear 0..1): top, horizon, bottom.</summary>
        public static void SetGradient(float topR, float topG, float topB, float horizonR, float horizonG, float horizonB,
                                       float bottomR, float bottomG, float bottomB, float exposure = 1f)
        {
            Editor.Core.Services.SceneRenderService.ScriptSky = new Editor.Core.Services.SceneRenderService.SkyOverride
                { Exposure = exposure, Gradient = new[] { topR, topG, topB, horizonR, horizonG, horizonB, bottomR, bottomG, bottomB } };
            Editor.Core.Services.SceneRenderService.RuntimeDirty = true;
        }

        /// <summary>Global ambient strength — the same as <see cref="Lighting.SetAmbient"/>, here for symmetry.</summary>
        public static void SetAmbient(float strength) => Lighting.SetAmbient(strength);

        /// <summary>Back to the scene's own Skybox component.</summary>
        public static void Clear()
        {
            Editor.Core.Services.SceneRenderService.ScriptSky = null;
            Editor.Core.Services.SceneRenderService.RuntimeDirty = true;
        }
    }

    /// <summary>Draw distance and level of detail from game scripts (#360). Geometric LOD is on by default: a mesh
    /// switches to its decimated copies beyond <c>mid</c> and <c>far</c> times its own radius (40 / 120 radii), so a
    /// crate thins out at 40 m and a building at 800 m. Persistent until changed; play end restores the defaults.</summary>
    public static class Rendering
    {
        /// <summary>Instances whose centre is farther than this (metres) are not drawn; 0 = no limit (the camera's
        /// far plane). Pair it with fog so nothing pops.</summary>
        public static void SetDrawDistance(float metres) => Editor.DllWrapper.VortexAPI.RenderDistance(metres);

        /// <summary>Geometric LOD on/off and its switch distances in multiples of each mesh's radius.</summary>
        public static void SetLod(bool enabled, float midRadii = 40f, float farRadii = 120f)
            => Editor.DllWrapper.VortexAPI.GeometricLod(enabled, midRadii, farRadii);
    }

    /// <summary>Screen post-effects for game scripts (#28/#29): vignette, animated film grain and
    /// chromatic aberration — the horror tension package. Settings persist until changed and apply the
    /// SAME frame, so per-frame ramps work: <c>PostFx.SetGrain(true, Mathf.Lerp(0.1f, 0.6f, panic), 1.6f);</c>
    /// All effects off = the effect pipeline is completely bypassed (zero GPU cost).</summary>
    public static class PostFx
    {
        /// <summary>Darkened screen edges ("claustrophobia dial"). intensity 0..~1.5 = reach inward,
        /// smoothness 0.01..1 = falloff hardness, roundness 1 = circular / 0 = follows the screen shape.
        /// Color is the edge tint (default black).</summary>
        public static void SetVignette(bool enabled, float intensity = 0.8f, float smoothness = 0.5f,
                                       float roundness = 1f, float r = 0f, float g = 0f, float b = 0f)
            { Editor.DllWrapper.VortexAPI.SetPostVignette(enabled, intensity, smoothness, roundness, r, g, b); }

        /// <summary>Animated film grain, luminance-weighted (shadows grain more). intensity 0..1,
        /// size = grain cell size in output pixels (1–3 is filmic).</summary>
        public static void SetGrain(bool enabled, float intensity = 0.35f, float size = 1.6f)
            { Editor.DllWrapper.VortexAPI.SetPostGrain(enabled, intensity, size); }

        /// <summary>Chromatic aberration: RGB fringing that grows towards the screen edges. strength in
        /// percent-of-half-screen (0.2–0.6 = unease, 2+ = heavy VHS), falloff = radial power.</summary>
        public static void SetChromaticAberration(bool enabled, float strength = 0.35f, float falloff = 1.2f)
            { Editor.DllWrapper.VortexAPI.SetPostChromaticAberration(enabled, strength, falloff); }

        /// <summary>Color grading (#31): exposure in EV stops, contrast (1 = neutral), saturation (1 =
        /// neutral, 0 = greyscale), temperature (-1 cool .. +1 warm), tint (-1 green .. +1 magenta).
        /// The horror mood dial — a cold, desaturated, low-exposure grade turns any scene grim.</summary>
        public static void SetColorGrade(bool enabled, float exposure = 0f, float contrast = 1f, float saturation = 1f,
                                         float temperature = 0f, float tint = 0f)
            { Editor.DllWrapper.VortexAPI.SetPostColorGrade(enabled, exposure, contrast, saturation, temperature, tint); }

        /// <summary>Bloom (#30): everything brighter than <paramref name="threshold"/> glows. knee
        /// softens the cutoff (no shimmer on grazing highlights), intensity is the glow strength
        /// (0 = off, bit-exact passthrough), scatter 0..1 spreads the glow wider. The horror
        /// staple for dying flashlight bulbs, exit signs and monster eyes:
        /// <c>PostFx.SetBloom(true, 0.7f, 0.5f, 1.2f, 0.7f);</c></summary>
        public static void SetBloom(bool enabled, float threshold = 0.75f, float knee = 0.5f,
                                    float intensity = 0.7f, float scatter = 0.65f)
            { Editor.DllWrapper.VortexAPI.SetPostBloom(enabled, threshold, knee, intensity, scatter); }

        /// <summary>SSAO (#32): screen-space ambient occlusion — corners, crevices and contact points
        /// get naturally darker without extra geometry. Darkens ONLY the ambient/indirect light (the
        /// flashlight beam stays untouched). radius in world units (~0.3-1.5), intensity 0..~2:
        /// <c>PostFx.SetAmbientOcclusion(true, 0.6f, 1.2f);</c></summary>
        public static void SetAmbientOcclusion(bool enabled, float radius = 0.6f, float intensity = 1.0f)
            { Editor.DllWrapper.VortexAPI.SetAmbientOcclusion(enabled, radius, intensity); }

        /// <summary>Everything off — back to the clean image (and the zero-cost render path).</summary>
        public static void ClearAll()
        {
            Editor.DllWrapper.VortexAPI.SetPostVignette(false, 0f, 0f, 0f, 0f, 0f, 0f);
            Editor.DllWrapper.VortexAPI.SetPostGrain(false, 0f, 0f);
            Editor.DllWrapper.VortexAPI.SetPostChromaticAberration(false, 0f, 0f);
            Editor.DllWrapper.VortexAPI.SetPostColorGrade(false, 0f, 1f, 1f, 0f, 0f);
            Editor.DllWrapper.VortexAPI.SetPostBloom(false, 0.75f, 0.5f, 0f, 0.65f);
            Editor.DllWrapper.VortexAPI.SetAmbientOcclusion(false, 0.6f, 0f);
            Editor.DllWrapper.VortexAPI.SetPostDebugInvert(false);
        }
    }

    /// <summary>Script-driven world geometry — assemble a level/backdrop from meshes without authoring a scene
    /// file. Add(meshPath, x,y,z, yawDeg, scale) places a model; placements persist until Clear(). Render-only
    /// (no collision yet) — perfect for greybox levels + the lobby's creepy motel backdrop.</summary>
    public static class World
    {
        public static void Add(string meshPath, float x, float y, float z, float yawDegrees, float scale)
            { Editor.Core.Services.WorldService.Add(Resolve(meshPath), x, y, z, yawDegrees, scale); }
        public static void Clear() { Editor.Core.Services.WorldService.Clear(); }

        private static string Resolve(string p)
        {
            try
            {
                if (System.IO.File.Exists(p)) return p;
                var proj = Editor.Core.Data.ProjectData.Current != null ? Editor.Core.Data.ProjectData.Current.Path : null;
                if (!string.IsNullOrEmpty(proj)) { var f = System.IO.Path.Combine(proj, p); if (System.IO.File.Exists(f)) return f; }
            }
            catch { }
            return p;
        }
    }

    /// <summary>
    /// Game audio for scripts. Clip paths are project-relative ("Assets/Audio/scream.wav")
    /// and resolve identically in editor play mode and shipped .vpak builds. One-shots use
    /// pooled voices that auto-reclaim — nothing to hold on to or free.
    /// <code>
    /// // Jump-scare stinger when the player trips a trigger:
    /// public class ScareTrigger : VortexBehaviour
    /// {
    ///     public override void OnTriggerEnter(TriggerHit hit)
    ///     {
    ///         if (hit.Tag != "Player") return;
    ///         Audio.PlayOneShot("Assets/Audio/stinger.wav", Position, 1f);   // 3D, at this entity
    ///         Audio.Music.CrossFade("Assets/Audio/chase.ogg", 2f);           // chase music sneaks in
    ///     }
    /// }
    /// </code>
    /// </summary>
    public static class Audio
    {
        /// <summary>Play a positional (3D) one-shot at a world position — no entity needed.
        /// Distance attenuation uses sensible defaults (min 1, max 500, logarithmic).</summary>
        public static void PlayOneShot(string clipPath, Vector3 position, float volume = 1f, float pitch = 1f)
            => Editor.Core.Services.AudioPlaybackService.Instance.PlayOneShot(clipPath, position.X, position.Y, position.Z, volume, pitch);

        /// <summary>Play a flat 2D one-shot (UI clicks, stingers) — no position, no attenuation.</summary>
        public static void PlayOneShot2D(string clipPath, float volume = 1f, float pitch = 1f)
            => Editor.Core.Services.AudioPlaybackService.Instance.PlayOneShot2D(clipPath, volume, pitch);

        /// <summary>Mixer bus volume by name ("Master", "Music", "SFX", "Ambience", "UI"),
        /// 0..1 — the settings-screen sliders call this. Applies in real time.</summary>
        public static void SetBusVolume(string busName, float volume)
        {
            var bus = Editor.DllWrapper.VortexAudio.BusIndexFromName(busName);
            if (bus >= 0)
            {
                Editor.DllWrapper.VortexAudio.SetBusVolume(bus, volume);
                // Shipped game: persist the player's choice so it survives a restart (#20). No-op in the editor.
                Editor.Core.Services.GameAudioSettings.Instance.Persist();
            }
        }

        public static float GetBusVolume(string busName)
        {
            var bus = Editor.DllWrapper.VortexAudio.BusIndexFromName(busName);
            return bus >= 0 ? Editor.DllWrapper.VortexAudio.GetBusVolume(bus) : 1f;
        }

        /// <summary>The music channel: one streamed, looping track at priority 0 (never stolen),
        /// with fade-in and crossfade. Fades are frame-ticked ramps for now (native envelopes
        /// arrive with the fade-envelope feature); the API shape is final.</summary>
        public static class Music
        {
            /// <summary>Start a track, fading in over fadeInSeconds (0 = immediate). A track
            /// that is already playing is faded out quickly and replaced.</summary>
            public static void Play(string clipPath, float fadeInSeconds = 0f)
                => Editor.Core.Services.AudioPlaybackService.Instance.MusicPlay(clipPath, fadeInSeconds);

            /// <summary>Fade the current track out while the new one fades in, overlapping.</summary>
            public static void CrossFade(string clipPath, float seconds)
                => Editor.Core.Services.AudioPlaybackService.Instance.MusicCrossFade(clipPath, seconds);

            public static void Stop(float fadeOutSeconds = 0f)
                => Editor.Core.Services.AudioPlaybackService.Instance.MusicStop(fadeOutSeconds);

            public static bool IsPlaying
                => Editor.Core.Services.AudioPlaybackService.Instance.MusicIsPlaying;

            /// <summary>Music channel volume (multiplies the per-track fades).</summary>
            public static float Volume
            {
                get => Editor.Core.Services.AudioPlaybackService.Instance.MusicVolume;
                set => Editor.Core.Services.AudioPlaybackService.Instance.MusicVolume = value;
            }
        }
    }

    /// <summary>
    /// Script-side handle to an entity's AudioSource component — get it via
    /// <see cref="VortexBehaviour.GetAudioSource"/>. Play/Stop/Pause/Resume control the
    /// component's voice; Volume/Pitch write through to the component, so inspector and
    /// script always agree.
    /// </summary>
    public sealed class AudioSource
    {
        private readonly Editor.ECS.Components.Audio.AudioSource _component;
        internal AudioSource(Editor.ECS.Components.Audio.AudioSource component) { _component = component; }

        /// <summary>(Re)start this source's clip from the beginning — works regardless of PlayOnAwake.</summary>
        public void Play() => Editor.Core.Services.AudioPlaybackService.Instance.ScriptPlay(_component);
        public void Stop() => Editor.Core.Services.AudioPlaybackService.Instance.ScriptStop(_component);
        public void Pause() => Editor.Core.Services.AudioPlaybackService.Instance.ScriptPause(_component);
        public void Resume() => Editor.Core.Services.AudioPlaybackService.Instance.ScriptResume(_component);
        public bool IsPlaying => Editor.Core.Services.AudioPlaybackService.Instance.ScriptIsPlaying(_component);

        /// <summary>(Re)start silent and glide to full volume over <paramref name="seconds"/> —
        /// ambience swells, creeping dread. Sample-accurate, no zipper noise.</summary>
        public void FadeIn(float seconds) => Editor.Core.Services.AudioPlaybackService.Instance.ScriptFadeIn(_component, seconds);

        /// <summary>Glide to silence over <paramref name="seconds"/>, then stop and free the voice.</summary>
        public void FadeOut(float seconds) => Editor.Core.Services.AudioPlaybackService.Instance.ScriptFadeOut(_component, seconds);

        /// <summary>Glide the fade envelope (0..1, on top of Volume) to a live target —
        /// duck a heartbeat under dialogue, swell a drone. Retargets smoothly mid-fade.</summary>
        public void FadeTo(float target, float seconds) => Editor.Core.Services.AudioPlaybackService.Instance.ScriptFadeTo(_component, target, seconds);

        /// <summary>Component enable toggle (#51): disabling stops playback immediately; the component
        /// keeps its clip/volume settings for the next Play() after re-enabling.</summary>
        public bool Enabled
        {
            get => _component.IsEnabled;
            set
            {
                _component.IsEnabled = value;
                if (!value) Editor.Core.Services.AudioPlaybackService.Instance.ScriptStop(_component);
            }
        }

        /// <summary>Live volume (0..1) — audible immediately while playing.</summary>
        public float Volume { get => _component.Volume; set => _component.Volume = value; }
        /// <summary>Live pitch — audible immediately while playing.</summary>
        public float Pitch { get => _component.Pitch; set => _component.Pitch = value; }
        public bool Loop { get => _component.Loop; set => _component.Loop = value; }
        /// <summary>Project-relative clip path; takes effect on the next Play().</summary>
        public string Clip { get => _component.AudioClipPath; set => _component.AudioClipPath = value; }
    }
}
