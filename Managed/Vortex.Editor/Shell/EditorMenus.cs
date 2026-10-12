using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.ECS;
using Editor.ECS.Components.Animation;
using Editor.ECS.Components.Audio;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell
{
    /// <summary>
    /// The editor's menu bar (the macOS global menu; an in-window bar elsewhere): File, Edit, View, GameObject,
    /// Component, Assets, Window, Tools, Help — parity with the WPF HeaderBar plus the macOS conventions (⌘ shortcuts).
    /// Plain-key gestures (W/E/R/F/G/⌫/F2) are display-only on macOS (AppKit never fires them); the main window's key
    /// handler runs them. The Window menu opens every editor through <see cref="EditorWindows"/>.
    /// </summary>
    internal static class EditorMenus
    {
        /// <summary>One entry of Window ▸ editors: a global window, an asset editor (asks for the asset) or an entity
        /// editor (uses the selection).</summary>
        public sealed class EditorEntry
        {
            public string Title, Gesture, AssetKind;
            public string[] Patterns;
            public Action Open;
            public Action<string> OpenAsset;
            public Action<GameEntity> OpenEntity;
            public Func<Task> OpenAsync;
            public bool IsAsset => OpenAsset != null;
            public bool IsEntity => OpenEntity != null;
        }

        public static readonly string[] Models = { "*.glb", "*.gltf", "*.fbx", "*.obj", "*.dae", "*.3ds", "*.blend" };
        public static readonly string[] Textures = { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tga", "*.dds", "*.hdr", "*.exr", "*.gif", "*.webp" };

        public static readonly IReadOnlyList<EditorEntry> Editors = new List<EditorEntry>
        {
            new EditorEntry { Title = "History…", Gesture = "Cmd+Shift+H", Open = () => EditorWindows.History() },
            new EditorEntry { Title = "Source Control…", Open = () => EditorCommands.GitWindow() },
            new EditorEntry { Title = "Audio Mixer…", Open = () => EditorWindows.AudioMixer() },
            new EditorEntry { Title = "Sound Studio…", Open = () => Audio.SoundStudioWindow.Open() },
            null,
            new EditorEntry { Title = "Material Editor…", AssetKind = "Material", Patterns = new[] { "*.vmat" }, OpenAsset = EditorWindows.MaterialEditor },
            new EditorEntry { Title = "Texture Editor…", AssetKind = "Texture", Patterns = Textures, OpenAsset = EditorWindows.TextureEditor },
            new EditorEntry { Title = "Model Editor…", AssetKind = "Model", Patterns = Models, OpenAsset = EditorWindows.ModelEditor },
            new EditorEntry { Title = "Mesh Editor…", AssetKind = "Model", Patterns = Models, OpenAsset = EditorWindows.MeshEditor },
            new EditorEntry { Title = "Asset Viewer…", AssetKind = "Asset", Patterns = Models.Concat(Textures).Concat(new[] { "*.vmat", "*.ventity" }).ToArray(), OpenAsset = EditorWindows.AssetViewer },
            new EditorEntry { Title = "Prefab Editor…", AssetKind = "Prefab", Patterns = new[] { "*.ventity" }, OpenAsset = EditorWindows.PrefabEditor },
            new EditorEntry { Title = "Sound Container Editor…", AssetKind = "Sound Container", Patterns = new[] { "*" + Editor.Core.Audio.SoundContainer.FileExtension }, OpenAsset = EditorWindows.SoundContainerEditor },
            new EditorEntry { Title = "UI Editor…", AssetKind = "UI Screen", Patterns = new[] { "*.vui" }, OpenAsset = EditorWindows.UiEditor },
            new EditorEntry { Title = "Animation Editor…", AssetKind = "Animation Clip", Patterns = new[] { "*.vanim" }, OpenAsset = EditorWindows.AnimationEditor },
            null,
            new EditorEntry { Title = "Socket Editor (selection)", OpenEntity = EditorWindows.SocketEditor },
            new EditorEntry { Title = "Collision Editor (selection)", OpenEntity = EditorWindows.CollisionEditor },
            null,
            new EditorEntry { Title = "Stress Test…", AssetKind = "Model", Patterns = Models, OpenAsset = EditorWindows.StressTest },
            new EditorEntry { Title = "Asset Tags…", AssetKind = "Asset", Patterns = Models.Concat(Textures).Concat(new[] { "*.vmat", "*.ventity", "*.wav", "*.mp3", "*.ogg", "*.vsndc", "*.vanim", "*.vui", "*.cs" }).ToArray(), OpenAsset = EditorWindows.AssetTags },
            new EditorEntry { Title = "Import Assets…", OpenAsync = EditorCommands.ImportAssetsDialog },
            new EditorEntry { Title = "Color Picker…", OpenAsync = PickColorToClipboard },
        };

        /// <summary>Run an editor entry the way the menu does: asset editors ask for the asset (project picker,
        /// the OS file picker when the project has none of that type), entity editors use the selection.</summary>
        public static async Task Run(EditorEntry e)
        {
            try
            {
                if (e.Open != null) { e.Open(); return; }
                if (e.OpenAsync != null) { await e.OpenAsync(); return; }
                if (e.IsEntity)
                {
                    var sel = SelectionService.Instance.SelectedEntity;
                    if (sel == null) { EditorCommands.Toast("Select an entity first"); return; }
                    e.OpenEntity(sel);
                    return;
                }
                if (e.IsAsset)
                {
                    // model tools (Model / Mesh Editor, Stress Test, Asset Viewer) take the selected object's model directly
                    string path = SelectedModel(e.Patterns) ?? await PickAsset(e.AssetKind, e.Patterns);
                    if (!string.IsNullOrEmpty(path)) e.OpenAsset(path);
                }
            }
            catch (Exception ex) { EditorCommands.Fail(e.Title.TrimEnd('…'), ex); }
        }

        /// <summary>The model file of the selected entity (its own MeshRenderer, or the first model part of a container)
        /// when it matches <paramref name="patterns"/>; null otherwise.</summary>
        public static string SelectedModel(string[] patterns)
        {
            var sel = SelectionService.Instance.SelectedEntity;
            if (sel == null || patterns == null || !patterns.Any(p => Array.IndexOf(Models, p) >= 0)) return null;
            string mesh = sel.GetComponent<MeshRenderer>()?.MeshPath;
            if (string.IsNullOrEmpty(mesh) && sel.Children != null)
                mesh = sel.Children.Select(c => c.GetComponent<MeshRenderer>()?.MeshPath).FirstOrDefault(m => !string.IsNullOrEmpty(m));
            if (string.IsNullOrEmpty(mesh) || mesh.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) return null;
            int hash = mesh.IndexOf('#'); if (hash >= 0) mesh = mesh.Substring(0, hash);
            string full = Path.IsPathRooted(mesh) ? mesh : Path.Combine(ProjectData.Current?.Path ?? "", mesh);
            return File.Exists(full) && VortexEditor.Panels.Inspector.PropertyRows.Matches(full, patterns) ? full : null;
        }

        /// <summary>Asset chooser for the Window-menu editors: the searchable project picker, or the OS file
        /// picker when the project has no file of that type. Returns a full path (null = cancelled).</summary>
        public static async Task<string> PickAsset(string kind, string[] patterns)
        {
            var root = ProjectData.Current?.Path;
            if (root != null && FindAssets(patterns).Any())
            {
                var rel = await AssetPickerDialog.Pick(kind, patterns);
                return string.IsNullOrEmpty(rel) ? null : Path.GetFullPath(Path.Combine(root, rel));
            }
            var top = EditorCommands.ActiveWindow(); if (top == null) return null;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open " + kind,
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(kind) { Patterns = patterns } }
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }

        /// <summary>Project files matching <paramref name="patterns"/> (skips hidden folders, build output, caches).</summary>
        public static IEnumerable<string> FindAssets(string[] patterns)
        {
            var root = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) yield break;
            IEnumerable<string> all;
            try { all = Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*", SearchOption.AllDirectories); }
            catch { yield break; }
            foreach (var f in all)
            {
                if (f.Contains(Path.DirectorySeparatorChar + ".") || f.Contains("/obj/") || f.Contains("/bin/")) continue;
                if (VortexEditor.Panels.Inspector.PropertyRows.Matches(f, patterns)) yield return f;
            }
        }

        private static async Task PickColorToClipboard()
        {
            var c = await EditorWindows.PickColor(Colors.White, "Color Picker");
            if (c == null) return;
            string hex = "#" + c.Value.R.ToString("X2") + c.Value.G.ToString("X2") + c.Value.B.ToString("X2");
            try { var cb = EditorCommands.ActiveWindow()?.Clipboard; if (cb != null) await cb.SetTextAsync(hex); } catch { }
            EditorCommands.Toast("Color " + hex + " copied to the clipboard");
        }

        // ================================================================== menu construction

        private sealed class Dyn { public NativeMenuItem Item; public Func<bool> Checked; public Func<bool> Enabled; public Func<string> Header; }
        private static readonly List<Dyn> _dynamic = new List<Dyn>();
        private static DispatcherTimer _timer;

        public static bool IsMac => OperatingSystem.IsMacOS();
        private static string G(string g) => string.IsNullOrEmpty(g) ? null : (IsMac ? g : g.Replace("Cmd", "Ctrl"));

        public static NativeMenu Build(MainWindow w)
        {
            var menu = new NativeMenu();
            menu.Items.Add(Sub("File",
                Item("New Project…", null, EditorCommands.NewProject),
                Item("Open Project…", "Cmd+O", EditorCommands.OpenProject),
                Sep(),
                Item("New Scene", "Cmd+N", EditorCommands.NewScene),
                Item("Open Scene…", "Cmd+Shift+O", () => _ = EditorCommands.OpenScene()),
                Sep(),
                Item("Save", "Cmd+S", EditorCommands.SaveProject),
                Item("Save All", "Cmd+Shift+S", EditorCommands.SaveAll),
                Sep(),
                Item("Project Settings…", "Cmd+OemComma", EditorCommands.ProjectSettings),
                Item("Show Project in Finder", null, EditorCommands.RevealProject),
                Item("Close Project", null, () => _ = EditorCommands.CloseProject()),
                Sep(),
                Item("Build Settings…", null, EditorCommands.Build),
                Item("Build…", "Cmd+B", EditorCommands.Build),
                Item("Build and Run", "Cmd+R", EditorCommands.BuildAndRun),
                IsMac ? null : Sep(),
                IsMac ? null : Item("Exit", "Alt+F4", () => _ = EditorCommands.Exit())));

            var undo = Item("Undo", "Cmd+Z", () => EditorCommands.EditCommand(EditorCommands.EditAction.Undo));
            Track(undo, header: () => UndoRedoManager.Instance.CanUndo ? "Undo " + UndoRedoManager.Instance.UndoName : "Undo");
            var redo = Item("Redo", "Cmd+Shift+Z", () => EditorCommands.EditCommand(EditorCommands.EditAction.Redo));
            Track(redo, header: () => UndoRedoManager.Instance.CanRedo ? "Redo " + UndoRedoManager.Instance.RedoName : "Redo");
            menu.Items.Add(Sub("Edit",
                undo, redo,
                Item("History…", null, EditorCommands.History),
                Sep(),
                Item("Cut", "Cmd+X", () => EditorCommands.EditCommand(EditorCommands.EditAction.Cut)),
                Item("Copy", "Cmd+C", () => EditorCommands.EditCommand(EditorCommands.EditAction.Copy)),
                Item("Paste", "Cmd+V", () => EditorCommands.EditCommand(EditorCommands.EditAction.Paste)),
                Item("Duplicate", "Cmd+D", () => EditorCommands.EditCommand(EditorCommands.EditAction.Duplicate)),
                Item("Delete", "Cmd+Back", () => EditorCommands.EditCommand(EditorCommands.EditAction.Delete)),
                Item("Rename", "F2", () => EditorCommands.EditCommand(EditorCommands.EditAction.Rename)),
                Sep(),
                Item("Select All", "Cmd+A", () => EditorCommands.EditCommand(EditorCommands.EditAction.SelectAll)),
                Item("Find…", "Cmd+F", EditorCommands.Find)));

            menu.Items.Add(Sub("View",
                Check("Grid", "G", EditorCommands.ToggleGrid, () => EditorViewportService.Instance.IsGridVisible),
                Check("Snap to Grid", null, EditorCommands.ToggleSnap, () => EditorViewportService.Instance.SnapToGrid),
                Sub("Snap Size", new[] { 0.1f, 0.25f, 0.5f, 1f, 2f, 5f }.Select(v => (NativeMenuItemBase)Radio(v.ToString(System.Globalization.CultureInfo.InvariantCulture) + " m", () => EditorCommands.SetSnapSize(v), () => Math.Abs(EditorViewportService.Instance.GridSpacing - v) < 0.0001f)).ToArray()),
                Check("Gizmos", null, EditorCommands.ToggleGizmos, () => EditorViewportService.Instance.AreGizmosVisible),
                Check("Colliders", null, EditorCommands.ToggleColliders, () => EditorViewportService.Instance.AreCollidersVisible),
                Check("All Colliders (not only the selection)", null, EditorCommands.ToggleAllColliders, () => EditorViewportService.Instance.ShowAllColliders),
                Check("Physics Debug (play)", null, EditorCommands.TogglePhysicsDebug, () => Editor.Core.Services.Physics.PhysicsService.ShowPhysicsDebug),
                Sep(),
                Radio("Move Tool", () => EditorCommands.MoveTool(), () => TransformGizmoService.Instance.CurrentMode == TransformGizmoService.GizmoMode.Translate, "W"),
                Radio("Rotate Tool", () => EditorCommands.RotateTool(), () => TransformGizmoService.Instance.CurrentMode == TransformGizmoService.GizmoMode.Rotate, "E"),
                Radio("Scale Tool", () => EditorCommands.ScaleTool(), () => TransformGizmoService.Instance.CurrentMode == TransformGizmoService.GizmoMode.Scale, "R"),
                Check("Local Space", "X", EditorCommands.ToggleGizmoSpace, () => TransformGizmoService.Instance.CurrentSpace == TransformGizmoService.GizmoSpace.Local),
                Sep(),
                Item("Frame Selection", "F", EditorCommands.FocusSelected),
                Item("Reset Camera", "Home", EditorCommands.ResetCamera),
                Sub("Viewport Layout",
                    Radio("Single", () => EditorCommands.SetLayout(1), () => w.ViewportPanel.Layout == 1),
                    Radio("Split Vertical", () => EditorCommands.SetLayout(2), () => w.ViewportPanel.Layout == 2),
                    Radio("Split Horizontal", () => EditorCommands.SetLayout(3), () => w.ViewportPanel.Layout == 3),
                    Radio("Quad", () => EditorCommands.SetLayout(4), () => w.ViewportPanel.Layout == 4)),
                Sep(),
                Check("Release Mode (hide play banner)", null, EditorCommands.ToggleReleaseMode, () => PlayModeService.Instance.IsReleaseMode)));

            menu.Items.Add(Sub("GameObject",
                Item("Create Empty", "Cmd+Shift+N", () => EditorCommands.CreateEmpty()),
                Item("Create Empty Child", null, () => EditorCommands.CreateChild()),
                Item("Player", null, () => EditorCommands.CreatePlayer()),
                Item("Folder", null, () => EditorCommands.CreateFolder()),
                Sub("3D Object",
                    Item("Cube", null, () => EditorCommands.CreatePrimitive(PrimitiveType.Cube)),
                    Item("Sphere", null, () => EditorCommands.CreatePrimitive(PrimitiveType.Sphere)),
                    Item("Capsule", null, () => EditorCommands.CreatePrimitive(PrimitiveType.Capsule)),
                    Item("Cylinder", null, () => EditorCommands.CreatePrimitive(PrimitiveType.Cylinder)),
                    Item("Plane", null, () => EditorCommands.CreatePrimitive(PrimitiveType.Plane)),
                    Item("Quad", null, () => EditorCommands.CreatePrimitive(PrimitiveType.Quad))),
                Sub("Light",
                    Item("Directional Light", null, () => EditorCommands.CreateLight(LightType.Directional)),
                    Item("Point Light", null, () => EditorCommands.CreateLight(LightType.Point)),
                    Item("Spot Light", null, () => EditorCommands.CreateLight(LightType.Spot)),
                    Sep(),
                    Item("Skybox", null, () => EditorCommands.CreateSkybox())),
                Item("Camera", null, () => EditorCommands.CreateCamera()),
                Item("Decal", null, () => EditorCommands.CreateDecal()),
                Item("Terrain", null, () => EditorCommands.CreateTerrain()),
                Item("Terrain from Heightmap…", null, () => _ = EditorCommands.CreateTerrainFromHeightmap()),
                Item("Foliage", null, () => EditorCommands.CreateFoliage()),
                Item("Water", null, () => EditorCommands.CreateWater()),
                Sub("Audio",
                    Item("Audio Source", null, () => EditorCommands.CreateAudioSource()),
                    Item("Reverb Zone", null, () => EditorCommands.CreateReverbZone())),
                Sub("UI",
                    Item("Canvas", null, () => EditorCommands.CreateUI("Canvas")),
                    Item("Text", null, () => EditorCommands.CreateUI("Text")),
                    Item("Image", null, () => EditorCommands.CreateUI("Image")),
                    Item("Button", null, () => EditorCommands.CreateUI("Button"))),
                Sep(),
                Item("Instantiate Prefab…", null, () => _ = EditorCommands.InstantiatePrefab()),
                Item("Create Prefab from Selection…", null, () => _ = EditorCommands.CreatePrefabFromSelection()),
                Sub("Prefab",
                    Item("Open Prefab", null, () => EditorCommands.OpenPrefab()),
                    Item("Select Prefab Asset", null, () => EditorCommands.SelectPrefabAsset()),
                    Sep(),
                    Item("Apply Overrides…", null, () => _ = EditorCommands.ApplyToPrefab()),
                    Item("Revert Overrides…", null, () => _ = EditorCommands.RevertToPrefab()),
                    Sep(),
                    Item("Unpack Prefab", null, () => EditorCommands.UnpackPrefab()),
                    Item("Unpack Completely", null, () => EditorCommands.UnpackPrefab(completely: true))),
                Sep(),
                Item("Toggle Active", null, () => EditorCommands.ToggleActive()),
                Item("Hide / Show in Scene View", null, () => EditorCommands.ToggleHiddenInEditor()),
                Item("Look Through Camera", null, () => EditorCommands.LookThroughCamera()),
                Item("Camera Preview", null, () => EditorCommands.ShowCameraPreview()),
                Sep(),
                Item("Socket Editor…", null, () => { var e = SelectionService.Instance.SelectedEntity; if (e != null) EditorWindows.SocketEditor(e); else EditorCommands.Toast("Select an entity first"); }),
                Item("Collision Editor…", null, () => { var e = SelectionService.Instance.SelectedEntity; if (e != null) EditorWindows.CollisionEditor(e); else EditorCommands.Toast("Select an entity first"); })));

            menu.Items.Add(Sub("Component",
                Sub("Rendering",
                    Item("Mesh Renderer", null, () => EditorCommands.AddComponentToSelection(e => new MeshRenderer(e), "Mesh Renderer")),
                    Item("Camera", null, () => EditorCommands.AddComponentToSelection(e => new Camera(e), "Camera")),
                    Item("Skybox", null, () => EditorCommands.AddComponentToSelection(e => new Skybox(e), "Skybox")),
                    Item("Decal", null, () => EditorCommands.AddComponentToSelection(e => new Editor.ECS.Components.Rendering.Decal(e), "Decal")),
                    Item("Terrain", null, () => EditorCommands.AddComponentToSelection(e => new Editor.ECS.Components.Rendering.Terrain(e), "Terrain")),
                    Item("Foliage", null, () => EditorCommands.AddComponentToSelection(e => new Editor.ECS.Components.Rendering.Foliage(e), "Foliage")),
                    Item("Water", null, () => EditorCommands.AddComponentToSelection(e => new Editor.ECS.Components.Rendering.Water(e), "Water")),
                    Item("Particle System", null, () => EditorCommands.AddComponentToSelection(e => new Editor.ECS.Components.Rendering.ParticleSystem(e) { PreviewInEditor = true }, "Particle System"))),
                Sub("Light",
                    Item("Directional Light", null, () => EditorCommands.AddComponentToSelection(e => new Light(e) { LightType = LightType.Directional }, "Light")),
                    Item("Point Light", null, () => EditorCommands.AddComponentToSelection(e => new Light(e) { LightType = LightType.Point }, "Light")),
                    Item("Spot Light", null, () => EditorCommands.AddComponentToSelection(e => new Light(e) { LightType = LightType.Spot }, "Light"))),
                Sub("Physics",
                    Item("Rigidbody", null, () => EditorCommands.AddComponentToSelection(e => new Rigidbody(e), "Rigidbody")),
                    Item("Box Collider", null, () => EditorCommands.AddComponentToSelection(e => new BoxCollider(e), "Box Collider")),
                    Item("Sphere Collider", null, () => EditorCommands.AddComponentToSelection(e => new SphereCollider(e), "Sphere Collider")),
                    Item("Capsule Collider", null, () => EditorCommands.AddComponentToSelection(e => new CapsuleCollider(e), "Capsule Collider")),
                    Item("Mesh Collider", null, () => EditorCommands.AddComponentToSelection(e => new MeshCollider(e), "Mesh Collider")),
                    Item("Ragdoll", null, () => EditorCommands.AddComponentToSelection(e => new Ragdoll(e), "Ragdoll")),
                    Sep(),
                    Item("Hinge Joint", null, () => EditorCommands.AddComponentToSelection(e => new HingeJoint(e), "Hinge Joint")),
                    Item("Ball Joint", null, () => EditorCommands.AddComponentToSelection(e => new BallJoint(e), "Ball Joint")),
                    Item("Slider Joint", null, () => EditorCommands.AddComponentToSelection(e => new SliderJoint(e), "Slider Joint")),
                    Item("Fixed Joint", null, () => EditorCommands.AddComponentToSelection(e => new FixedJoint(e), "Fixed Joint")),
                    Item("Distance Joint", null, () => EditorCommands.AddComponentToSelection(e => new DistanceJoint(e), "Distance Joint")),
                    Sep(),
                    Check("Physics Debug Draw (play)", null, EditorCommands.TogglePhysicsDebug, () => Editor.Core.Services.Physics.PhysicsService.ShowPhysicsDebug),
                    Item("Collision Editor…", null, () => { var e = SelectionService.Instance.SelectedEntity; if (e != null) EditorWindows.CollisionEditor(e); else EditorCommands.Toast("Select an entity first"); })),
                Sub("Audio",
                    Item("Audio Source", null, () => EditorCommands.AddComponentToSelection(e => new AudioSource(e), "Audio Source")),
                    Item("Audio Listener", null, () => EditorCommands.AddComponentToSelection(e => new AudioListener(e), "Audio Listener")),
                    Item("Reverb Zone", null, () => EditorCommands.AddComponentToSelection(e => new ReverbZone(e), "Reverb Zone"))),
                Sub("Animation",
                    Item("Animator", null, () => EditorCommands.AddComponentToSelection(e => new Animator(e), "Animator")),
                    Item("Bone Attachment", null, () => EditorCommands.AddComponentToSelection(e => new BoneAttachment(e), "Bone Attachment")),
                    Item("Two-Bone IK", null, () => EditorCommands.AddComponentToSelection(e => new TwoBoneIk(e), "Two-Bone IK")),
                    Item("Hand Pose", null, () => EditorCommands.AddComponentToSelection(e => new HandPose(e), "Hand Pose")),
                    Item("Look-At IK", null, () => EditorCommands.AddComponentToSelection(e => new LookAtIk(e), "Look-At IK")),
                    Item("Foot IK", null, () => EditorCommands.AddComponentToSelection(e => new FootIk(e), "Foot IK"))),
                Sub("AI",
                    Item("Nav Agent", null, () => EditorCommands.AddComponentToSelection(e => new Editor.ECS.Components.AI.NavAgent(e), "Nav Agent")),
                    Item("AI Perception", null, () => EditorCommands.AddComponentToSelection(e => new Editor.ECS.Components.AI.AIPerception(e), "AI Perception")),
                    Item("Patrol Path", null, () => EditorCommands.AddComponentToSelection(e => new Editor.ECS.Components.AI.PatrolPath(e), "Patrol Path")),
                    Item("Behavior Tree", null, () => EditorCommands.AddComponentToSelection(e => new Editor.ECS.Components.AI.BehaviorTreeAgent(e), "Behavior Tree")),
                    Sep(),
                    Item("Navigation (Bake NavMesh)…", null, EditorWindows.Navigation)),
                Sub("Script",
                    Item("New Script…", null, EditorCommands.AddNewScript),
                    Item("Existing Script…", null, () => _ = AddExistingScript()))));

            menu.Items.Add(Sub("Assets",
                Item("Import Asset…", "Cmd+I", () => _ = EditorCommands.ImportAsset()),
                Item("Export Selected Asset…", null, EditorCommands.ExportAsset),
                Sep(),
                Sub("Create",
                    Item("Material", null, EditorCommands.CreateMaterial),
                    Item("Shader", null, EditorCommands.CreateShader),
                    Item("Script", null, EditorCommands.CreateScript),
                    Item("Prefab (empty)", null, () => EditorCommands.CreateAsset("prefab")),
                    Item("UI Screen", null, () => EditorCommands.CreateAsset("ui")),
                    Item("Animation Clip", null, () => EditorCommands.CreateAsset("anim")),
                    Item("Sound Container", null, () => EditorCommands.CreateAsset("sound"))),
                Item("Create Material", null, EditorCommands.CreateMaterial),
                Item("Create Shader", null, EditorCommands.CreateShader),
                Item("Create Script", null, EditorCommands.CreateScript),
                Sep(),
                Item("Refresh", null, EditorCommands.RefreshAssets),
                Sep(),
                Item("Reload Material Shaders", null, EditorCommands.ReloadShaders),
                Item("Open Scripts Project in IDE", null, EditorCommands.OpenScriptsProject),
                Sep(),
                Sub("Asset Library",
                    Item("Show Library", null, () => EditorCommands.ShowLibrary()),
                    Item("Asset Store", null, () => EditorCommands.ShowStore()),
                    Item("Sound Studio…", null, () => Audio.SoundStudioWindow.Open()),
                    Sep(),
                    Item("Add Files to Library…", null, () => _ = Panels.AssetBrowser.LibraryView.Current?.PickFilesToLibrary()),
                    Item("Index Existing Projects…", null, () => _ = Library.LibraryIndexDialog.Run()),
                    Item("Import Library Bundle…", null, () => _ = Panels.AssetBrowser.LibraryView.Current?.ImportBundle()),
                    Sep(),
                    Item("Tag Manager…", null, () => _ = Library.LibraryTagManager.Run()),
                    Item("Maintenance…", null, Library.LibraryMaintenanceWindow.Open),
                    Item("Library Settings…", null, () => _ = Library.LibrarySettingsDialog.Run()),
                    Sep(),
                    Item("Backfill Content Hashes (Project)", null, () => _ = Panels.AssetBrowser.LibraryView.Current?.BackfillProjectHashes()))));

            var window = new List<NativeMenuItemBase>
            {
                Check("Scene Hierarchy", "Cmd+D1", () => w.TogglePanel(MainWindow.PanelHierarchy), () => w.IsPanelVisible(MainWindow.PanelHierarchy)),
                Check("File System", "Cmd+D2", () => w.TogglePanel(MainWindow.PanelFiles), () => w.IsPanelVisible(MainWindow.PanelFiles)),
                Check("Inspector", "Cmd+D3", () => w.TogglePanel(MainWindow.PanelInspector), () => w.IsPanelVisible(MainWindow.PanelInspector)),
                Check("Project", "Cmd+D4", () => w.TogglePanel(MainWindow.PanelProject), () => w.IsPanelVisible(MainWindow.PanelProject)),
                Check("Console", "Cmd+D5", () => w.TogglePanel(MainWindow.PanelConsole), () => w.IsPanelVisible(MainWindow.PanelConsole)),
                Check("Environment", "Cmd+D6", () => w.TogglePanel(MainWindow.PanelEnvironment), () => w.IsPanelVisible(MainWindow.PanelEnvironment)),
                Check("Library", "Cmd+D7", () => w.TogglePanel(MainWindow.PanelLibrary), () => w.IsPanelVisible(MainWindow.PanelLibrary)),
                Check("Asset Store", "Cmd+D8", () => w.TogglePanel(MainWindow.PanelStore), () => w.IsPanelVisible(MainWindow.PanelStore)),
                Check("Claude Sidebar", "Cmd+D9", () => w.TogglePanel(MainWindow.PanelClaude), () => w.IsPanelVisible(MainWindow.PanelClaude)),
                Check("Terminal", "Ctrl+OemTilde", w.ToggleTerminal, () => w.IsPanelVisible(MainWindow.PanelTerminal)),
                Sep(),
            };
            foreach (var e in Editors)
            {
                if (e == null) { window.Add(Sep()); continue; }
                var entry = e;
                window.Add(Item(entry.Title, entry.Gesture, () => _ = Run(entry)));
            }
            window.Add(Sep());
            window.Add(Item("Reset Layout (show all panels, hide Claude)", null, EditorCommands.ResetLayout));
            menu.Items.Add(Sub("Window", window.ToArray()));

            menu.Items.Add(Sub("Tools",
                Item("Play", "Cmd+P", EditorCommands.TogglePlay),
                Item("Pause / Resume", "Cmd+Shift+P", EditorCommands.Pause),
                Item("Stop", null, EditorCommands.Stop),
                Item("Play in Standalone Player", null, EditorCommands.PlayInNewWindow),
                Sep(),
                Item("Build Settings…", null, EditorCommands.Build),
                Sub("Editors", Editors.Where(x => x != null).Select(x => (NativeMenuItemBase)Item(x.Title, null, () => _ = Run(x))).ToArray()),
                Item("Source Control…", null, EditorCommands.GitWindow),
                Item("Audio Mixer…", null, EditorCommands.AudioMixer),
                Sub("Claude",
                    Item("Claude Sidebar", null, () => w.ShowPanel(MainWindow.PanelClaude)),
                    Item("Open Claude Code in a Terminal Window", null, () => _ = Claude.ClaudePanel.Current?.OpenInClaudeCodeAsync()),
                    Item("Connect Claude Code / Desktop…", null, () => _ = Claude.ClaudeConnectDialog.Run()),
                    Item("Operations…", null, () => Claude.ClaudeOperationsWindow.Open()),
                    Check("MCP Server", null, () => _ = Claude.McpHost.SetEnabledAsync(!Claude.McpHost.IsRunning), () => Claude.McpHost.IsRunning)),
                Item("Navigation (Bake NavMesh)…", null, EditorWindows.Navigation),
                Item("Stress Test…", null, () => _ = Run(Editors.First(x => x != null && x.Title.StartsWith("Stress")))),
                Item("History…", null, EditorCommands.History),
                Sep(),
                Item("Reload Material Shaders", null, EditorCommands.ReloadShaders),
                Item("Clear Console", null, () => ConsoleService.Instance.Clear()),
                Item("Show Project in Finder", null, EditorCommands.RevealProject)));

            menu.Items.Add(Sub("Help",
                Item("Documentation", null, EditorCommands.Documentation),
                Item("Scripting API Reference", null, EditorCommands.ApiReference),
                Item("Keyboard Shortcuts", null, () => _ = EditorCommands.KeyboardShortcuts()),
                Item("Release Notes", null, EditorCommands.ReleaseNotes),
                Sep(),
                Item("Check for Updates…", null, () => _ = EditorCommands.CheckForUpdates()),
                Item("About Vortex Engine", null, EditorCommands.About)));

            menu.NeedsUpdate += (s, e) => Refresh();
            foreach (var top in menu.Items.OfType<NativeMenuItem>()) if (top.Menu != null) top.Menu.NeedsUpdate += (s, e) => Refresh();
            if (_timer == null)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _timer.Tick += (s, e) => Refresh();
                _timer.Start();
            }
            Refresh();
            return menu;
        }

        private static async Task AddExistingScript()
        {
            var sel = SelectionService.Instance.SelectedEntity; if (sel == null) { EditorCommands.Toast("Select an entity first"); return; }
            var rel = await AssetPickerDialog.Pick("Script", new[] { "*.cs" });
            if (string.IsNullOrEmpty(rel)) return;
            EditorCommands.AddComponentToSelection(e => new Editor.ECS.Components.Scripting.Script(e, rel), Path.GetFileNameWithoutExtension(rel));
        }

        /// <summary>Sync the dynamic items (check marks, undo/redo names).</summary>
        public static void Refresh()
        {
            foreach (var d in _dynamic)
            {
                try
                {
                    if (d.Checked != null) { bool c = d.Checked(); if (d.Item.IsChecked != c) d.Item.IsChecked = c; }
                    if (d.Enabled != null) { bool en = d.Enabled(); if (d.Item.IsEnabled != en) d.Item.IsEnabled = en; }
                    if (d.Header != null) { string h = d.Header(); if (d.Item.Header != h) d.Item.Header = h; }
                }
                catch { }
            }
        }

        // ------------------------------------------------------------------ item helpers
        public static NativeMenuItem Sub(string header, params NativeMenuItemBase[] items)
        {
            var mi = new NativeMenuItem(header) { Menu = new NativeMenu() };
            foreach (var i in items) if (i != null) mi.Menu.Items.Add(i);
            return mi;
        }

        public static NativeMenuItem Item(string header, string gesture, Action action)
        {
            var mi = new NativeMenuItem(header);
            var g = G(gesture);
            if (!string.IsNullOrEmpty(g)) { try { mi.Gesture = KeyGesture.Parse(g); } catch { } }
            mi.Click += (s, e) => { try { action(); } catch (Exception ex) { EditorCommands.Fail(header, ex); } };
            return mi;
        }

        private static NativeMenuItem Check(string header, string gesture, Action toggle, Func<bool> isChecked)
        {
            var mi = Item(header, gesture, toggle);
            mi.ToggleType = NativeMenuItemToggleType.CheckBox;
            Track(mi, isChecked);
            return mi;
        }

        private static NativeMenuItem Radio(string header, Action select, Func<bool> isChecked, string gesture = null)
        {
            var mi = Item(header, gesture, select);
            mi.ToggleType = NativeMenuItemToggleType.Radio;
            Track(mi, isChecked);
            return mi;
        }

        private static void Track(NativeMenuItem mi, Func<bool> isChecked = null, Func<bool> enabled = null, Func<string> header = null)
            => _dynamic.Add(new Dyn { Item = mi, Checked = isChecked, Enabled = enabled, Header = header });

        public static NativeMenuItemSeparator Sep() => new NativeMenuItemSeparator();
    }
}
