using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Animation;
using Editor.ECS.Components.Audio;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using Editor.ECS.Components.Scripting;
using Editor.Scripting;
using VortexEditor.Controls;
using VortexEditor.Shell;
using static VortexEditor.Panels.Inspector.PropertyRows;
using Transform = Editor.ECS.Components.Transform;

namespace VortexEditor.Panels.Inspector
{
    /// <summary>Per-component property editors. Each returns the rows for one component card.</summary>
    public static class ComponentEditors
    {
        internal static readonly string[] ModelPatterns = { "*.fbx", "*.obj", "*.gltf", "*.glb", "*.dae", "*.3ds", "*.blend", "*.vmesh" };
        internal static readonly string[] TexturePatterns = { "*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp", "*.hdr", "*.dds" };
        internal static readonly string[] AudioPatterns = { "*.wav", "*.mp3", "*.ogg", "*.flac", "*.vsndc" };
        internal static readonly string[] MaterialPatterns = { "*.vmat" };
        internal static readonly string[] AnimPatterns = { "*.vanim" };
        internal static readonly string[] PrefabPatterns = { "*.ventity" };

        public static (string icon, string brush) Style(Component c)
        {
            switch (c)
            {
                case Transform _: return ("Move", "VxTealBrush");
                case MeshRenderer _: return ("Cube", "VxAccentBrush");
                case Camera cam: return ("Camera", cam.CameraType == CameraType.MainCamera || cam.IsMainCamera ? "VxPurpleBrush" : "VxAccentBrush");
                case Light _: return ("Light", "VxYellowBrush");
                case Skybox _: return ("World", "VxTealBrush");
                case Script _: return ("Script", "VxOrangeBrush");
                case Collider _: return ("Collider", "VxGreenBrush");
                case Rigidbody _: return ("Sphere", "VxGreenBrush");
                case PhysicsJoint _: return ("Link", "VxGreenBrush");
                case Ragdoll _: return ("Bone", "VxGreenBrush");
                case Editor.ECS.Components.AI.NavAgent _: return ("Crosshair", "VxTealBrush");
                case Editor.ECS.Components.AI.BehaviorTreeAgent _: return ("Flow", "VxTealBrush");
                case Editor.ECS.Components.AI.AIPerception _: return ("Eye", "VxTealBrush");
                case Editor.ECS.Components.AI.PatrolPath _: return ("Link", "VxTealBrush");
                case Editor.ECS.Components.Rendering.ParticleSystem _: return ("Sparkle", "VxOrangeBrush");
                case Editor.ECS.Components.Rendering.Decal _: return ("Image", "VxPinkBrush");
                case Editor.ECS.Components.Rendering.Terrain _: return ("World", "VxGreenBrush");
                case Editor.ECS.Components.Rendering.Foliage _: return ("Layers", "VxGreenBrush");
                case Editor.ECS.Components.Rendering.Water _: return ("World", "VxTealBrush");
                case AudioSource _: case AudioListener _: case ReverbZone _: return ("Audio", "VxGreenBrush");
                case Animator _: case BoneAttachment _: case TwoBoneIk _: case HandPose _: case LookAtIk _: case FootIk _: return ("Bone", "VxPinkBrush");
                default: return ("Gear", "VxTextSecondaryBrush");
            }
        }

        /// <summary>Custom inspector cards registered from other files (by exact component type) — checked before the
        /// built-in cards, so a component's card can live next to the feature that owns it:
        /// <c>[ModuleInitializer] static void Register() =&gt; ComponentEditors.Custom[typeof(LookAtIk)] = (c, e) =&gt; Rows((LookAtIk)c);</c></summary>
        public static readonly Dictionary<Type, Func<Component, GameEntity, IEnumerable<Control>>> Custom = new Dictionary<Type, Func<Component, GameEntity, IEnumerable<Control>>>();

        public static IEnumerable<Control> Build(Component c, GameEntity entity)
        {
            if (c != null && Custom.TryGetValue(c.GetType(), out var custom)) return custom(c, entity);
            entity = entity ?? c?.Entity;
            switch (c)
            {
                case Transform t: return TransformRows(t);
                case MeshRenderer m: return MeshRendererRows(m, entity);
                case Camera cam: return CameraRows(cam, entity);
                case Light l: return LightRows(l);
                case Skybox s: return SkyboxRows(s);
                case Script sc: return ScriptCard.Rows(sc, entity);
                case Collider col: return ColliderRows(col, entity);
                case Rigidbody rb: return RigidbodyRows(rb);
                case AudioSource a: return AudioSourceRows(a);
                case AudioListener _: return new[] { Hint("Receives 3D audio from this entity's position. One listener per scene (usually on the player camera).") };
                case ReverbZone rz: return ReverbRows(rz);
                case Animator an: return AnimationCards.AnimatorRows(an, entity);
                case BoneAttachment ba: return AnimationCards.BoneAttachmentRows(ba, entity);
                case TwoBoneIk ik: return AnimationCards.TwoBoneIkRows(ik, entity);
                case HandPose hp: return HandPoseRows(hp);
                default: return GenericRows(c);
            }
        }

        internal static void Dirty() { SceneRenderService.RuntimeDirty = true; }

        /// <summary>True when the entity lives in a scene; false for an isolated prefab template (the Prefab Editor).</summary>
        public static bool IsSceneEntity(GameEntity e) => e != null && Shell.Prefab.PrefabWorkflow.IsInScene(e);

        /// <summary>Add a component the way the entity's context needs: undoable in a scene, DIRECT on an isolated prefab
        /// template — its edits must not land in the main scene's undo history.</summary>
        public static void AddComponent(GameEntity e, Component c)
        {
            if (e == null || c == null) return;
            c.Entity = e;
            if (IsSceneEntity(e)) e.AddComponent(c); else e.AddComponentDirect(c);
        }

        /// <summary>Remove counterpart of <see cref="AddComponent"/> (Transform is never removed).</summary>
        public static void RemoveComponent(GameEntity e, Component c)
        {
            if (e == null || c == null || c is Transform) return;
            if (IsSceneEntity(e)) e.RemoveComponent(c); else e.Components.Remove(c);
        }

        /// <summary>Rebuild the inspector cards after the current event (type switches change titles and rows).</summary>
        internal static void RefreshInspectorSoon(Control origin = null)
        {
            // the inspector that hosts the card (the main one, or the Prefab Editor's isolated one)
            var host = origin != null ? Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<InspectorPanel>(origin) : null;
            Dispatcher.UIThread.Post(() => { if (host != null) host.Refresh(); else EditorCommands.Window?.Inspector?.Refresh(); });
        }

        // ---------------------------------------------------------------- transform
        private static IEnumerable<Control> TransformRows(Transform t)
        {
            yield return Row("Position", Vector3(() => t.LocalPosition, v => t.LocalPosition = v));
            yield return Row("Rotation", Vector3(() => t.LocalRotation, v => t.LocalRotation = v, 1));
            yield return Row("Scale", Vector3(() => t.LocalScale, v => t.LocalScale = v, 0.1, 0.001f));
            var reset = new Button { Content = "Reset", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left };
            ToolTip.SetTip(reset, "Position 0, rotation 0, scale 1");
            reset.Click += (s, e) => { t.Reset(); RefreshAll(); Dirty(); };
            yield return Row("", reset);
        }

        // ---------------------------------------------------------------- mesh renderer
        private static readonly string[] Primitives = { "None", "Cube", "Sphere", "Plane", "Cylinder", "Cone", "Capsule", "Quad" };
        private static IEnumerable<Control> MeshRendererRows(MeshRenderer m, GameEntity entity)
        {
            void MeshChanged() { try { if (entity != null) SceneRenderService.Instance.OnMeshChanged(entity.Id); } catch { } Dirty(); }
            var combo = new ComboBox { MinHeight = 22, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var p in Primitives) combo.Items.Add(p);
            combo.Items.Add("Custom mesh…");
            bool syncing = false;
            void Sync()
            {
                syncing = true;
                string mp = m.MeshPath ?? "";
                if (string.IsNullOrEmpty(mp)) combo.SelectedIndex = 0;
                else if (mp.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) { int i = Array.FindIndex(Primitives, p => string.Equals(p, mp.Substring(10), StringComparison.OrdinalIgnoreCase)); combo.SelectedIndex = i < 0 ? Primitives.Length : i; }
                else combo.SelectedIndex = Primitives.Length;
                syncing = false;
            }
            Sync();
            combo.SelectionChanged += async (s, e) =>
            {
                if (syncing) return;
                int i = combo.SelectedIndex;
                if (i == 0) { if (!string.IsNullOrEmpty(m.MeshPath)) { m.MeshPath = ""; MeshChanged(); } }
                else if (i > 0 && i < Primitives.Length) { string np = "Primitive:" + Primitives[i]; if (m.MeshPath != np) { m.MeshPath = np; MeshChanged(); } }
                else if (i == Primitives.Length && (string.IsNullOrEmpty(m.MeshPath) || IsPrimitive(m.MeshPath)))
                {
                    var p = await AssetPickerDialog.Pick("Models", ModelPatterns);
                    if (!string.IsNullOrEmpty(p)) { m.MeshPath = p; MeshChanged(); }
                    Sync();
                    RefreshAll();
                }
            };
            Refreshers[combo] = Sync;
            yield return Row("Mesh", combo);
            yield return Row("Mesh file", AssetPath(() => IsPrimitive(m.MeshPath) ? "" : m.MeshPath, v => { m.MeshPath = v ?? ""; MeshChanged(); Sync(); }, "Model", ModelPatterns, () => AssetPickerDialog.Pick("Models", ModelPatterns)));
            yield return Row("Material", AssetPath(() => IsMaterialPlaceholder(m.MaterialPath) ? "" : m.MaterialPath, v =>
            {
                string note = null;
                // a store material on a floor/wall primitive: a copy tiled for the object's size (see MaterialFit)
                m.MaterialPath = string.IsNullOrEmpty(v) ? null : VortexEditor.Services.MaterialFit.ForRenderer(m.Entity, m, v, out note);
                if (note != null) EditorCommands.Toast(note);
                Dirty();
            }, "Material", MaterialPatterns, () => AssetPickerDialog.Pick("Materials", MaterialPatterns)), "Drop a .vmat — empty = the engine default material. Store materials (they know their real size) are tiled for the object's size.");
            var edit = new Button { Content = "Edit Material…", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left };
            edit.Click += (s, e) =>
            {
                if (string.IsNullOrEmpty(m.MaterialPath) || IsMaterialPlaceholder(m.MaterialPath)) return;
                string full = ProjectRelativeToAbsolute(m.MaterialPath);
                if (File.Exists(full)) EditorCommands.Window?.OpenMaterialEditor(full);
                else EditorCommands.Toast("Material file not found: " + m.MaterialPath);
            };
            Refreshers[edit] = () => edit.IsEnabled = !string.IsNullOrEmpty(m.MaterialPath) && !IsMaterialPlaceholder(m.MaterialPath);
            Refreshers[edit]();
            yield return Row("", edit);
            yield return Row("Color", Color(() => (m.ColorR, m.ColorG, m.ColorB), (r, g, b) => { m.ColorR = r; m.ColorG = g; m.ColorB = b; Dirty(); }));
            yield return Row("Opacity", SliderRow(() => m.ColorA, v => { m.ColorA = v; Dirty(); }, 0, 1));
            yield return Row("Metallic", SliderRow(() => m.Metallic, v => { m.Metallic = v; Dirty(); }, 0, 1));
            yield return Row("Roughness", SliderRow(() => m.Roughness, v => { m.Roughness = v; Dirty(); }, 0, 1));
            yield return Row("Normal strength", SliderRow(() => m.NormalStrength, v => { m.NormalStrength = v; Dirty(); }, 0, 2));
            var shadows = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            shadows.Children.Add(Check("Cast", () => m.CastShadows, v => { m.CastShadows = v; Dirty(); }));
            shadows.Children.Add(Check("Receive", () => m.ReceiveShadows, v => { m.ReceiveShadows = v; Dirty(); }));
            yield return Row("Shadows", shadows);
            yield return Row("Render layer", Choice(() => m.RenderLayer, v => { m.RenderLayer = v; Dirty(); }, "World", "First-Person (viewmodel)", "Third-Person only"),
                "World: every camera, casts shadows.\nFirst-Person: drawn after the world with its own FOV and cleared depth — never clips through walls, no shadows (arms / weapons).\nThird-Person only: what OTHERS see — hidden for the local player while playing.");
        }
        private static bool IsPrimitive(string p) => !string.IsNullOrEmpty(p) && p.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase);
        private static bool IsMaterialPlaceholder(string p) => !string.IsNullOrEmpty(p) && p.StartsWith("Material:", StringComparison.OrdinalIgnoreCase);

        public static string ProjectRelativeToAbsolute(string p)
        {
            if (string.IsNullOrEmpty(p) || Path.IsPathRooted(p)) return p;
            var root = ProjectData.Current?.Path; return string.IsNullOrEmpty(root) ? p : Path.Combine(root, p.Replace('\\', '/'));
        }

        // ---------------------------------------------------------------- camera
        private static IEnumerable<Control> CameraRows(Camera c, GameEntity entity)
        {
            var rows = new List<Control>();
            void Changed() { try { if (entity != null) SceneRenderService.Instance.OnCameraChanged(entity.Id); } catch { } Dirty(); }
            var warn = Warning("Only one Main Camera allowed per scene! Make the other camera a Game camera first.");
            warn.IsVisible = false;
            bool editorOnly = c.CameraType == CameraType.EditorCamera;
            var labels = editorOnly ? new[] { "Game camera", "Main camera (player view)", "Editor only" } : new[] { "Game camera", "Main camera (player view)" };
            Control typeChoice = null;
            rows.Add(Row("Camera type", typeChoice = Choice(() => c.CameraType == CameraType.EditorCamera ? 2 : c.CameraType == CameraType.MainCamera || c.IsMainCamera ? 1 : 0, v =>
            {
                var type = v == 1 ? CameraType.MainCamera : v == 2 ? CameraType.EditorCamera : CameraType.GameCamera;
                if (type == CameraType.MainCamera)
                {
                    var other = FindOtherMainCamera(entity, c);
                    if (other != null) { warn.IsVisible = true; RefreshAll(); return; }   // revert: one main camera per scene
                }
                warn.IsVisible = false;
                c.CameraType = type;
                c.IsMainCamera = type == CameraType.MainCamera;
                Changed();
                RefreshInspectorSoon(typeChoice);   // header icon colour + the "no main camera" warning
            }, labels), "The Main camera is the player's view in play mode and in the built game"));
            rows.Add(warn);
            if (!c.IsMainCamera && c.CameraType != CameraType.MainCamera && !SceneHasMainCamera(entity))
                rows.Add(Warning("No main camera in this scene: Play will have nothing to render through. Make one camera the main camera."));
            rows.Add(Section("Projection"));
            Action updateVis = null;
            rows.Add(Row("Projection", Enum<CameraProjection>(() => c.Projection, v => { c.Projection = v; Changed(); updateVis?.Invoke(); })));
            var fov = Row("Field of view", SliderRow(() => c.FieldOfView, v => { c.FieldOfView = Math.Max(1f, Math.Min(179f, v)); Changed(); }, 10, 150, "0.#", 1f, 179f), "Vertical field of view in degrees");
            var ortho = Row("Ortho size", FloatBox(() => c.OrthographicSize, v => { c.OrthographicSize = Math.Max(0.1f, v); Changed(); }, 0.5, 0.1f), "Half the visible height in world units");
            updateVis = () => { bool persp = c.Projection == CameraProjection.Perspective; fov.IsVisible = persp; ortho.IsVisible = !persp; };
            updateVis();
            rows.Add(fov); rows.Add(ortho);
            rows.Add(Section("Clipping planes"));
            rows.Add(Row("Near", FloatBox(() => c.NearClip, v => { c.NearClip = Math.Max(0.001f, v); if (c.FarClip < c.NearClip + 0.1f) c.FarClip = c.NearClip + 0.1f; Changed(); RefreshAll(); }, 0.01, 0.001f)));
            rows.Add(Row("Far", FloatBox(() => c.FarClip, v => { c.FarClip = Math.Max(c.NearClip + 0.1f, v); Changed(); }, 10, 0.1f)));
            rows.Add(Section("Rendering"));
            rows.Add(Row("Clear", Enum<CameraClearFlags>(() => c.ClearFlags, v => { c.ClearFlags = v; Changed(); })));
            rows.Add(Row("Background", Color(() => (c.BackgroundR, c.BackgroundG, c.BackgroundB), (r, g, b) => { c.BackgroundR = r; c.BackgroundG = g; c.BackgroundB = b; Changed(); })));
            rows.Add(Row("Render order", IntBox(() => c.Depth, v => { c.Depth = v; Changed(); }, -100, 100), "Lower values render first (0 = first)"));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var look = new Button { Content = "Look Through", Classes = { "ghost" } };
            ToolTip.SetTip(look, "Move the scene view to this camera");
            look.Click += (s, e) => Editor.Core.Viewport.EditorViewportSession.Main?.ViewThroughCamera(entity);
            var prev = new Button { Content = "Preview Camera View", Classes = { "ghost" } };
            ToolTip.SetTip(prev, "Picture-in-picture preview of what this camera sees");
            prev.Click += (s, e) => CameraPreviewService.Instance.TogglePreview(entity);
            row.Children.Add(look); row.Children.Add(prev);
            rows.Add(Row("", row));
            return rows;
        }
        private static bool SceneHasMainCamera(GameEntity e)
        {
            var scene = e?.Scene ?? ProjectData.Current?.ActiveScene;
            return scene != null && PlayCameraHelper.FindMainCamera(scene) != null;
        }
        private static Camera FindOtherMainCamera(GameEntity self, Camera cam)
        {
            var scene = self?.Scene ?? ProjectData.Current?.ActiveScene;
            foreach (var e in Shell.Animation.AnimUtil.AllEntities(scene))
            {
                var c = e.GetComponent<Camera>();
                if (c != null && !ReferenceEquals(c, cam) && (c.CameraType == CameraType.MainCamera || c.IsMainCamera)) return c;
            }
            return null;
        }

        // ---------------------------------------------------------------- light
        private static IEnumerable<Control> LightRows(Light l)
        {
            var rows = new List<Control>();
            void D() => Dirty();
            Action updateVis = null;
            Control typeBox = null;
            rows.Add(Row("Type", typeBox = Enum<LightType>(() => l.LightType, v => { l.LightType = v; D(); updateVis?.Invoke(); RefreshInspectorSoon(typeBox); })));
            rows.Add(Row("Color", Color(() => (l.ColorR, l.ColorG, l.ColorB), (r, g, b) => { l.ColorR = r; l.ColorG = g; l.ColorB = b; D(); })));
            rows.Add(Row("Intensity", SliderRow(() => l.Intensity, v => { l.Intensity = v; D(); }, 0, 10, "0.#", 0f, null)));
            var range = Row("Range", SliderRow(() => l.Range, v => { l.Range = v; D(); }, 0.1, 100, "0.#", 0.1f, null), "Distance the light reaches (m)");
            var spot = Row("Spot angle", SliderRow(() => l.SpotAngle, v => { l.SpotAngle = v; if (l.InnerSpotAngle > v) l.InnerSpotAngle = v; D(); RefreshAll(); }, 1, 179, "0"), "Outer cone angle (degrees)");
            var inner = Row("Inner angle", SliderRow(() => l.InnerSpotAngle, v => { l.InnerSpotAngle = Math.Min(v, l.SpotAngle); D(); RefreshAll(); }, 0, 178, "0"), "Full-intensity inner cone (degrees)");
            rows.Add(range); rows.Add(spot); rows.Add(inner);
            rows.Add(Section("Shadows"));
            rows.Add(Row("Shadow type", Enum<ShadowType>(() => l.ShadowType, v => { l.ShadowType = v; D(); updateVis?.Invoke(); })));
            var strength = Row("Strength", SliderRow(() => l.ShadowStrength, v => { l.ShadowStrength = v; D(); }, 0, 1));
            var bias = Row("Bias", SliderRow(() => l.ShadowBias, v => { l.ShadowBias = v; D(); }, 0, 0.2, "0.###", 0f, null), "Higher = less shadow acne, but shadows detach from objects (peter-panning)");
            var nbias = Row("Normal bias", SliderRow(() => l.ShadowNormalBias, v => { l.ShadowNormalBias = v; D(); }, 0, 3, "0.##", 0f, null), "Offset along the surface normal — fixes acne on slopes");
            var res = Row("Resolution", Choice(() => ResIndex(l.ShadowResolution), v => { l.ShadowResolution = new[] { 512, 1024, 2048, 4096 }[v]; D(); }, "512", "1024", "2048", "4096"));
            var dist = Row("Distance", SliderRow(() => l.ShadowDistance, v => { l.ShadowDistance = v; D(); }, 5, 2000, "0", 5f, null), "How far from the camera the sun's shadows reach (m) — higher reaches further but softens near shadows");
            rows.Add(strength); rows.Add(bias); rows.Add(nbias); rows.Add(res); rows.Add(dist);
            updateVis = () =>
            {
                bool dir = l.LightType == LightType.Directional, isSpot = l.LightType == LightType.Spot, sh = l.ShadowType != ShadowType.None;
                range.IsVisible = !dir; spot.IsVisible = isSpot; inner.IsVisible = isSpot;
                strength.IsVisible = sh; bias.IsVisible = sh; nbias.IsVisible = sh; res.IsVisible = sh; dist.IsVisible = sh && dir;
            };
            updateVis();
            return rows;
        }
        private static int ResIndex(int r) => r >= 4096 ? 3 : r >= 2048 ? 2 : r >= 1024 ? 1 : 0;

        // ---------------------------------------------------------------- skybox
        private static IEnumerable<Control> SkyboxRows(Skybox s)
        {
            var rows = new List<Control>();
            void D() => Dirty();
            void Reload() { try { SceneRenderService.Instance.ClearSkyboxMeshCache(); } catch { } D(); }
            Action updateVis = null;
            // Skybox hides Component.IsEnabled with its own flag (the renderer reads that one) — edit it here too
            rows.Add(Row("Visible", Bool(() => s.IsEnabled, v => { s.IsEnabled = v; D(); }), "Draw this sky (the renderer's own switch)"));
            rows.Add(Row("Type", Enum<SkyboxType>(() => s.SkyboxType, v => { s.SkyboxType = v; Reload(); updateVis?.Invoke(); }, new[] { "Solid Color", "Gradient", "Cubemap", "Texture (HDR)" })));
            rows.Add(Row("Ambient", SliderRow(() => s.AmbientIntensity, v => { s.AmbientIntensity = v; D(); }, 0, 2, "0.##", 0f, 2f), "Ambient light intensity"));
            rows.Add(Row("Exposure", SliderRow(() => s.Exposure, v => { s.Exposure = v; D(); }, 0.1, 4, "0.#", 0.1f, 4f)));
            var sky = Row("Sky color", Color(() => (s.TopColorR, s.TopColorG, s.TopColorB), (r, g, b) => { s.TopColorR = r; s.TopColorG = g; s.TopColorB = b; D(); }));
            var horizon = Row("Horizon color", Color(() => (s.HorizonColorR, s.HorizonColorG, s.HorizonColorB), (r, g, b) => { s.HorizonColorR = r; s.HorizonColorG = g; s.HorizonColorB = b; D(); }));
            var ground = Row("Ground color", Color(() => (s.BottomColorR, s.BottomColorG, s.BottomColorB), (r, g, b) => { s.BottomColorR = r; s.BottomColorG = g; s.BottomColorB = b; D(); }));
            var cube = Row("Cubemap", AssetPath(() => s.CubemapPath, v => { s.CubemapPath = v ?? ""; Reload(); }, "Texture", TexturePatterns, () => AssetPickerDialog.Pick("Textures", TexturePatterns)));
            var tex = Row("Texture", AssetPath(() => s.TexturePath, v => { s.TexturePath = v ?? ""; Reload(); }, "Texture", TexturePatterns, () => AssetPickerDialog.Pick("Textures", TexturePatterns)), "HDR or equirectangular panorama (required)");
            var mesh = Row("Mesh (opt.)", AssetPath(() => s.SkyboxMeshPath, v => { s.SkyboxMeshPath = v ?? ""; Reload(); }, "Model", ModelPatterns, () => AssetPickerDialog.Pick("Models", ModelPatterns)), "Optional custom sky mesh — empty uses the built-in sphere");
            var info = Hint("Select a texture (PNG, JPG, HDR) for the sky. A built-in sphere is used automatically.");
            rows.Add(sky); rows.Add(horizon); rows.Add(ground); rows.Add(cube); rows.Add(tex); rows.Add(mesh); rows.Add(info);
            updateVis = () =>
            {
                var t = s.SkyboxType;
                ((TextBlock)sky.Children[0]).Text = t == SkyboxType.SolidColor ? "Color" : "Sky color";
                sky.IsVisible = t == SkyboxType.SolidColor || t == SkyboxType.Gradient;
                horizon.IsVisible = ground.IsVisible = t == SkyboxType.Gradient;
                cube.IsVisible = t == SkyboxType.Cubemap;
                tex.IsVisible = info.IsVisible = t == SkyboxType.Texture;
                mesh.IsVisible = t == SkyboxType.Cubemap || t == SkyboxType.Texture;
            };
            updateVis();
            return rows;
        }

        // ---------------------------------------------------------------- physics
        private static IEnumerable<Control> ColliderRows(Collider c, GameEntity entity)
        {
            yield return Row("Is trigger", Bool(() => c.IsTrigger, v => { c.IsTrigger = v; Dirty(); }), "Overlap only (OnTriggerEnter / Stay / Exit) — no solid blocking");
            yield return Row("Center", Vector3(() => c.Center, v => { c.Center = v; Dirty(); }, 0.05));
            switch (c)
            {
                case BoxCollider b:
                    yield return Row("Size", Vector3(() => b.Size, v => { b.Size = v; Dirty(); }, 0.05, 0.001f));
                    break;
                case SphereCollider s:
                    yield return Row("Radius", FloatBox(() => s.Radius, v => { s.Radius = v; Dirty(); }, 0.05, 0.001f));
                    break;
                case CapsuleCollider cc:
                    yield return Row("Radius", FloatBox(() => cc.Radius, v => { cc.Radius = v; Dirty(); }, 0.05, 0.001f));
                    yield return Row("Height", FloatBox(() => cc.Height, v => { cc.Height = v; Dirty(); }, 0.05, 0.001f), "Total height including the caps");
                    yield return Row("Direction", Choice(() => cc.Direction, v => { cc.Direction = v; Dirty(); }, "X axis", "Y axis", "Z axis"));
                    break;
                case MeshCollider mc:
                    yield return Row("Mesh", AssetPath(() => mc.MeshPath, v => { mc.MeshPath = v; Dirty(); }, "Mesh", ModelPatterns, () => AssetPickerDialog.Pick("Models", ModelPatterns)), "Empty = this entity's Mesh Renderer (exact rendered triangles)");
                    yield return Row("Convex", Bool(() => mc.Convex, v => { mc.Convex = v; Dirty(); }), "Convex hull (needed for dynamic rigid bodies; faster)");
                    break;
            }
            // mesh-shaped colliders are built from the entity's OWN Mesh Renderer — without one they collide with nothing
            bool meshShaped = c is MeshCollider || c.GetType() == typeof(Collider);
            var owner = c.Entity ?? entity;
            if (meshShaped && owner != null && owner.GetComponent<MeshRenderer>() == null && !(c is MeshCollider mc2 && !string.IsNullOrEmpty(mc2.MeshPath)))
                yield return Danger("No Mesh Renderer on this entity — this collider produces NO collision. Add it to the child that has the mesh, or use a Box Collider.");
            // physics material (nested object: null until the first edit — the defaults show, the object is created on write)
            yield return Section("Physics material");
            yield return Row("Friction", SliderRow(() => c.Material != null ? c.Material.Friction : 0.5f, v => { Mat(c).Friction = v; Dirty(); }, 0, 1, "0.##", 0f, 5f), "0 = ice … 1 = rubber (default 0.5) — rigid-body physics");
            yield return Row("Bounciness", SliderRow(() => c.Material != null ? c.Material.Bounciness : 0f, v => { Mat(c).Bounciness = v; Dirty(); }, 0, 1), "0 = no bounce … 1 = perfectly elastic");
            yield return Hint("Switch shapes, auto-fit, add a contact script and preview the collider in the Collision Editor.");
            var open = new Button { Content = "Open Collision Editor…", Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Left };
            open.Click += (s, e) => EditorWindows.CollisionEditor(owner);
            yield return Row("", open);
        }
        private static PhysicsMaterial Mat(Collider c) { if (c.Material == null) c.Material = new PhysicsMaterial(); return c.Material; }

        private static IEnumerable<Control> RigidbodyRows(Rigidbody rb)
        {
            yield return Hint("Simulated in play when the entity also has a Collider. Dynamic = falls / stacks / gets pushed, Kinematic = you move it and it pushes others, Static = never moves.");
            yield return Row("Body type", Enum<RigidbodyType>(() => rb.BodyType, v => rb.BodyType = v));
            yield return Row("Mass (kg)", FloatBox(() => rb.Mass, v => rb.Mass = v, 0.1, 0.001f));
            yield return Row("Drag", FloatBox(() => rb.Drag, v => rb.Drag = v, 0.05, 0), "Linear damping");
            yield return Row("Angular drag", FloatBox(() => rb.AngularDrag, v => rb.AngularDrag = v, 0.05, 0));
            yield return Row("Use gravity", Bool(() => rb.UseGravity, v => rb.UseGravity = v));
            yield return Row("Interpolation", Enum<RigidbodyInterpolation>(() => rb.Interpolation, v => rb.Interpolation = v));
            yield return Row("Collision", Enum<CollisionDetectionMode>(() => rb.CollisionDetection, v => rb.CollisionDetection = v), "Continuous detection keeps fast bodies from tunnelling through thin walls");
            var fp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            fp.Children.Add(Labeled("X", Bool(() => rb.FreezePositionX, v => rb.FreezePositionX = v))); fp.Children.Add(Labeled("Y", Bool(() => rb.FreezePositionY, v => rb.FreezePositionY = v))); fp.Children.Add(Labeled("Z", Bool(() => rb.FreezePositionZ, v => rb.FreezePositionZ = v)));
            yield return Row("Freeze position", fp);
            var fr = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            fr.Children.Add(Labeled("X", Bool(() => rb.FreezeRotationX, v => rb.FreezeRotationX = v))); fr.Children.Add(Labeled("Y", Bool(() => rb.FreezeRotationY, v => rb.FreezeRotationY = v))); fr.Children.Add(Labeled("Z", Bool(() => rb.FreezeRotationZ, v => rb.FreezeRotationZ = v)));
            yield return Row("Freeze rotation", fr);
        }
        private static Control Labeled(string l, Control c) { var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 }; sp.Children.Add(c); sp.Children.Add(new TextBlock { Text = l, Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center }); return sp; }

        // ---------------------------------------------------------------- audio
        private static IEnumerable<Control> AudioSourceRows(AudioSource a)
        {
            yield return Row("Clip", AssetPath(() => a.AudioClipPath, v => a.AudioClipPath = v, "Audio", AudioPatterns, () => AssetPickerDialog.Pick("Audio", AudioPatterns)), "Project-relative .wav/.mp3/.ogg/.flac or a .vsndc container — drop one here");
            // edit-mode preview with the current settings — no play mode needed
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var indicator = new TextBlock { Classes = { "small" }, Foreground = (IBrush)Application.Current.FindResource("VxGreenBrush"), VerticalAlignment = VerticalAlignment.Center };
            var spatial = new CheckBox { Content = new TextBlock { Text = "Listen from camera (3D)", Classes = { "small" } }, IsChecked = a.SpatialBlend > 0f, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(spatial, "Preview spatialized from the scene camera's position (else a plain 2D preview)");
            var play = new Button { Content = "Preview ▶", Classes = { "ghost" } };
            ToolTip.SetTip(play, "Play this source now with the current settings");
            play.Click += (s, e) =>
            {
                try { AudioPreviewService.Instance.Start(a, spatial.IsChecked == true); indicator.Text = AudioPreviewService.Instance.IsPreviewing(a) ? "playing…" : ""; }
                catch (Exception ex) { EditorCommands.Fail("Preview", ex); }
            };
            var stop = new Button { Content = "Stop ■", Classes = { "ghost" } };
            stop.Click += (s, e) => { try { AudioPreviewService.Instance.Stop(); } catch { } indicator.Text = ""; };
            row.Children.Add(play); row.Children.Add(stop); row.Children.Add(spatial); row.Children.Add(indicator);
            yield return Row("", row);
            yield return Row("Volume", SliderRow(() => a.Volume, v => a.Volume = v, 0, 1));
            yield return Row("Pitch", SliderRow(() => a.Pitch, v => a.Pitch = v, 0.25, 3, "0.##", 0.01f, 3f));
            yield return Row("Loop", Bool(() => a.Loop, v => a.Loop = v));
            yield return Row("Play on awake", Bool(() => a.PlayOnAwake, v => a.PlayOnAwake = v));
            yield return Row("Mute", Bool(() => a.Mute, v => a.Mute = v));
            yield return Row("Streaming", Bool(() => a.Streaming, v => a.Streaming = v), "Stream from disk (music / long ambience)");
            yield return Row("Output bus", Choice(() => a.OutputBus, v => a.OutputBus = v, Editor.DllWrapper.VortexAudio.BusNames));
            yield return Section("3D sound");
            yield return Row("Spatial blend", SliderRow(() => a.SpatialBlend, v => a.SpatialBlend = v, 0, 1), "0 = 2D, 1 = fully 3D");
            yield return Row("Min distance", SliderRow(() => a.MinDistance, v => a.MinDistance = v, 0.1, 50, "0.#", 0.01f, null));
            yield return Row("Max distance", SliderRow(() => a.MaxDistance, v => a.MaxDistance = v, 1, 1000, "0", 0.1f, null));
            yield return Row("Rolloff", Enum<AudioRolloffMode>(() => a.RolloffMode, v => a.RolloffMode = v));
            yield return Row("Priority", IntBox(() => a.Priority, v => a.Priority = v, 0, 256), "0 = highest");
            yield return Row("Stereo pan", SliderRow(() => a.StereoPan, v => a.StereoPan = v, -1, 1));
            yield return Row("Reverb mix", SliderRow(() => a.ReverbZoneMix, v => a.ReverbZoneMix = v, 0, 1));
            yield return Row("Doppler", SliderRow(() => a.DopplerLevel, v => a.DopplerLevel = v, 0, 5));
            yield return Row("Spread", SliderRow(() => a.Spread, v => a.Spread = v, 0, 360, "0"));
            yield return Section("Steam Audio");
            yield return Row("HRTF", Bool(() => a.EnableHrtf, v => a.EnableHrtf = v), "Binaural HRTF (needs the project's Steam Audio switch in the Audio Mixer)");
            yield return Row("Occlusion", Bool(() => a.EnableOcclusion, v => a.EnableOcclusion = v), "Ray-traced occlusion behind walls (needs HRTF and a 3D source)");
        }

        private static IEnumerable<Control> ReverbRows(ReverbZone r)
        {
            var box = Row("Box extents", Vector3(() => r.BoxExtents, v => { r.BoxExtents = v; Dirty(); }, 0.5, 0.1f), "Half extents of the box zone");
            var radius = Row("Radius", FloatBox(() => r.Radius, v => { r.Radius = v; Dirty(); }, 0.5, 0.1f));
            void Vis() { box.IsVisible = r.Shape == 1; radius.IsVisible = r.Shape != 1; }
            yield return Row("Shape", Choice(() => r.Shape, v => { r.Shape = v; Dirty(); Vis(); }, "Sphere", "Box"));
            Vis();
            yield return radius;
            yield return box;
            yield return Row("Falloff", SliderRow(() => r.Falloff, v => r.Falloff = v, 0, 20), "Blend distance outside the zone (m)");
            yield return Row("Decay time", SliderRow(() => r.DecayTime, v => r.DecayTime = v, 0.1, 20), "Seconds");
            yield return Row("Wet level", SliderRow(() => r.WetLevel, v => r.WetLevel = v, 0, 1));
            yield return Row("Pre-delay (ms)", SliderRow(() => r.PreDelayMs, v => r.PreDelayMs = v, 0, 200, "0"));
        }

        // Hand Pose: editor-authored finger grip (per-finger curl of the three joints) — live in the viewport.
        private static IEnumerable<Control> HandPoseRows(HandPose hp)
        {
            yield return Row("Hand", Enum<HandSide>(() => hp.Side, v => hp.Side = v));
            yield return Row("Weight", SliderRow(() => hp.Weight, v => hp.Weight = v, 0, 1));
            yield return Row("Index (j1 j2 j3 °)", Vector3(() => hp.Index, v => hp.Index = v, 1));
            yield return Row("Middle", Vector3(() => hp.Middle, v => hp.Middle = v, 1));
            yield return Row("Ring", Vector3(() => hp.Ring, v => hp.Ring = v, 1));
            yield return Row("Pinky", Vector3(() => hp.Pinky, v => hp.Pinky = v, 1));
            yield return Row("Thumb", Vector3(() => hp.Thumb, v => hp.Thumb = v, 1));
            yield return Row("Spread", SliderRow(() => hp.Spread, v => hp.Spread = v, -30, 30, "0"));
            yield return Row("Bone prefix", Text(() => hp.BonePrefix, v => hp.BonePrefix = v));
            yield return Row("Bone format", Text(() => hp.BoneFormat, v => hp.BoneFormat = v));
            yield return Row("Curl axis (0 X, 1 Y, 2 Z)", IntBox(() => hp.CurlAxis, v => hp.CurlAxis = v));
            yield return Row("Curl sign", FloatBox(() => hp.CurlSign, v => hp.CurlSign = v));
        }

        // ---------------------------------------------------------------- fallback: reflection
        private static IEnumerable<Control> GenericRows(Component c)
        {
            bool any = false;
            foreach (var p in c.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0 || p.GetSetMethod() == null) continue;
                if (p.Name == "Id" || p.Name == "Entity" || p.Name == "IsEnabled") continue;
                if (p.GetCustomAttributes(typeof(System.Runtime.Serialization.IgnoreDataMemberAttribute), true).Length > 0) continue;
                var pp = p;
                Control ed = null;
                var t = p.PropertyType;
                if (t == typeof(float)) ed = FloatBox(() => (float)pp.GetValue(c), v => pp.SetValue(c, v));
                else if (t == typeof(int)) ed = IntBox(() => (int)pp.GetValue(c), v => pp.SetValue(c, v));
                else if (t == typeof(bool)) ed = Bool(() => (bool)pp.GetValue(c), v => pp.SetValue(c, v));
                else if (t == typeof(string)) ed = Text(() => (string)pp.GetValue(c), v => pp.SetValue(c, v));
                else if (t == typeof(Editor.ECS.Vector3)) ed = Vector3(() => (Editor.ECS.Vector3)pp.GetValue(c), v => pp.SetValue(c, v));
                else if (t.IsEnum)
                {
                    var names = System.Enum.GetNames(t);
                    var values = System.Enum.GetValues(t);
                    ed = Choice(() => Array.IndexOf(values, pp.GetValue(c)), i => { if (i >= 0 && i < values.Length) pp.SetValue(c, values.GetValue(i)); }, names.Select(Pretty).ToArray());
                }
                if (ed != null) { any = true; yield return Row(Pretty(p.Name), ed); }
            }
            if (!any) yield return Hint("This component has no editable properties.");
        }
    }
}
