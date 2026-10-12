using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.Core.Viewport;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Rendering;
using VortexEditor.Controls;
using VortexEditor.Shell;
using VortexEditor.Viewport;

namespace VortexEditor.Panels
{
    /// <summary>
    /// The scene view: toolbar (tools, space, grid / snap / gizmos / colliders / FP layer, view options — shading,
    /// snap size, camera speed —, layouts, camera, PIP), the embedded engine view, split / quad secondary views, the
    /// camera-preview overlay and the asset drop target (drop a model / prefab / primitive where the pointer ray
    /// hits the scene, a material onto the object under the pointer).
    /// </summary>
    public partial class ViewportPanel : UserControl
    {
        private sealed class CameraChoice { public string Label; public GameEntity Entity; public bool FpPreview; public override string ToString() => Label; }

        private bool _syncing;
        private readonly List<SecondaryViewportView> _secondaries = new List<SecondaryViewportView>();
        private int _layout = 1;
        private DispatcherTimer _cameraPoll;
        private int _lastCameraCount = -1;
        private readonly CameraPreviewOverlay _pip;

        public EditorViewportSession Session => Engine.Session;
        public EngineViewport EngineView => Engine;
        /// <summary>1 single, 2 split vertical, 3 split horizontal, 4 quad.</summary>
        public int Layout => _layout;
        public CameraPreviewOverlay CameraPreview => _pip;
        public event Action<string, string> StatusChanged;

        public ViewportPanel()
        {
            InitializeComponent();
            var vs = EditorViewportService.Instance;
            vs.GridVisibilityChanged += (s, v) => Dispatcher.UIThread.Post(SyncToggles);
            vs.GizmosVisibilityChanged += (s, v) => Dispatcher.UIThread.Post(SyncToggles);
            vs.SnapToGridChanged += (s, v) => Dispatcher.UIThread.Post(SyncToggles);
            vs.CollisionVisibilityChanged += (s, v) => Dispatcher.UIThread.Post(SyncToggles);
            TransformGizmoService.Instance.ModeChanged += (s, m) => Dispatcher.UIThread.Post(SyncTools);
            PlayModeService.Instance.StateChanged += (s, st) => Dispatcher.UIThread.Post(SyncPlayState);
            PlayModeService.Instance.GameViewChanged += a => Dispatcher.UIThread.Post(SyncPlayState);
            Engine.Session.FlyModeChanged += fly => Dispatcher.UIThread.Post(() => { FlyBadge.IsVisible = fly; FlyText.Text = "FLY · " + EditorCameraController.Instance.MoveSpeed.ToString("0.#") + " m/s"; });
            Engine.Session.StatusUpdated += (st, res) => Dispatcher.UIThread.Post(() => StatusChanged?.Invoke(st, res));
            EditorSession.Instance.ProjectOpened += _ => Dispatcher.UIThread.Post(RefreshCameraList);
            SceneService.Instance.SceneLoaded += (s, sc) => Dispatcher.UIThread.Post(RefreshCameraList);
            SyncToggles(); SyncTools(); SyncPlayState();
            RefreshCameraList();
            _cameraPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _cameraPoll.Tick += (s, e) => { int n = CountCameras(); if (n != _lastCameraCount) RefreshCameraList(); };
            _cameraPoll.Start();

            // asset drops onto the 3D view (the native view forwards drags to the window; Avalonia routes them here)
            DragDrop.SetAllowDrop(MainCell, true);
            MainCell.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            MainCell.AddHandler(DragDrop.DropEvent, OnDrop);

            // camera preview (PIP) — a popup, so it is drawn above the native Metal view
            _pip = new CameraPreviewOverlay(MainCell);
            MainCell.Children.Add(_pip.Host);
            CameraPreviewService.Instance.PreviewRequested += (s, cam) => Dispatcher.UIThread.Post(() => { _pip.Show(cam); SyncPip(); });
            CameraPreviewService.Instance.PreviewClosed += (s, e) => Dispatcher.UIThread.Post(() => { _pip.Hide(); SyncPip(); });
        }

        // ------------------------------------------------------------ toggles
        private bool _fxHooked;
        private void SyncToggles()
        {
            if (!_fxHooked) { _fxHooked = true; EnvironmentPanel.PreviewChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(SyncToggles); }
            _syncing = true;
            var vs = EditorViewportService.Instance;
            FxToggle.IsChecked = EnvironmentPanel.PreviewPostEffects;
            GridToggle.IsChecked = vs.IsGridVisible;
            SnapToggle.IsChecked = vs.SnapToGrid;
            GizmoToggle.IsChecked = vs.AreGizmosVisible;
            ColliderToggle.IsChecked = vs.AreCollidersVisible;
            FpToggle.IsChecked = SceneRenderService.EditorViewmodelPreview != SceneRenderService.ViewmodelPreviewMode.Hidden;
            WireBadge.IsVisible = VortexAPI.IsWireframeMode;
            _syncing = false;
        }

        private void SyncTools()
        {
            _syncing = true;
            switch (TransformGizmoService.Instance.CurrentMode)
            {
                case TransformGizmoService.GizmoMode.Translate: ToolMove.IsChecked = true; break;
                case TransformGizmoService.GizmoMode.Rotate: ToolRotate.IsChecked = true; break;
                case TransformGizmoService.GizmoMode.Scale: ToolScale.IsChecked = true; break;
            }
            bool local = TransformGizmoService.Instance.CurrentSpace == TransformGizmoService.GizmoSpace.Local;
            SpaceToggle.IsChecked = local;
            SpaceText.Text = local ? "Local" : "World";
            _syncing = false;
        }

        private void SyncPlayState()
        {
            var pms = PlayModeService.Instance;
            bool playing = pms.IsPlaying;
            PlayBadge.IsVisible = playing && !pms.IsExternalWindow;
            ExternalBadge.IsVisible = playing && pms.IsExternalWindow;
            Toolbar.IsVisible = !playing || pms.IsExternalWindow;
            GameViewBadge.IsVisible = pms.IsGameView && !playing;
        }

        private void OnToolMove(object s, RoutedEventArgs e) { if (!_syncing) TransformGizmoService.Instance.SetTranslateMode(); }
        private void OnToolRotate(object s, RoutedEventArgs e) { if (!_syncing) TransformGizmoService.Instance.SetRotateMode(); }
        private void OnToolScale(object s, RoutedEventArgs e) { if (!_syncing) TransformGizmoService.Instance.SetScaleMode(); }
        private void OnSpaceToggle(object s, RoutedEventArgs e) { if (!_syncing) { TransformGizmoService.Instance.ToggleSpace(); SyncTools(); } }
        private void OnGridToggle(object s, RoutedEventArgs e) { if (!_syncing) EditorViewportService.Instance.IsGridVisible = GridToggle.IsChecked == true; }
        private void OnSnapToggle(object s, RoutedEventArgs e) { if (!_syncing) EditorCommands.SetSnap(SnapToggle.IsChecked == true); }
        private void OnGizmoToggle(object s, RoutedEventArgs e) { if (!_syncing) EditorViewportService.Instance.AreGizmosVisible = GizmoToggle.IsChecked == true; }
        private void OnTerrainTools(object s, RoutedEventArgs e) { try { VortexEditor.Shell.EditorWindows.TerrainTools(); } catch (Exception ex) { EditorCommands.Fail("Terrain Tools", ex); } }
        private void OnFxToggle(object s, RoutedEventArgs e) { if (!_syncing) EnvironmentPanel.SetPreviewPostEffects(FxToggle.IsChecked == true); }
        private void OnColliderToggle(object s, RoutedEventArgs e) { if (!_syncing) { EditorViewportService.Instance.AreCollidersVisible = ColliderToggle.IsChecked == true; SceneRenderService.RuntimeDirty = true; } }

        private void OnColliderPressed(object s, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
            var m = new MenuFlyout();
            var all = new MenuItem { Header = "Show ALL colliders (not only the selection)", ToggleType = MenuItemToggleType.CheckBox, IsChecked = EditorViewportService.Instance.ShowAllColliders };
            all.Click += (a, b) => EditorCommands.ToggleAllColliders();
            m.Items.Add(all);
            m.ShowAt(ColliderToggle);
            e.Handled = true;
        }

        private void OnSnapPressed(object s, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
            var m = new MenuFlyout();
            foreach (var mi in SnapItems()) m.Items.Add(mi);
            m.ShowAt(SnapToggle);
            e.Handled = true;
        }

        private IEnumerable<MenuItem> SnapItems()
        {
            foreach (var v in new[] { 0.1f, 0.25f, 0.5f, 1f, 2f, 5f })
            {
                var vv = v;
                var mi = new MenuItem { Header = "Snap " + v.ToString(System.Globalization.CultureInfo.InvariantCulture) + " m", ToggleType = MenuItemToggleType.Radio, IsChecked = Math.Abs(EditorViewportService.Instance.GridSpacing - v) < 0.0001f };
                mi.Click += (a, b) => { EditorCommands.SetSnapSize(vv); if (!EditorViewportService.Instance.SnapToGrid) EditorCommands.SetSnap(true); };
                yield return mi;
            }
        }

        private void OnFpToggle(object s, RoutedEventArgs e)
        {
            if (_syncing) return;
            var m = new MenuFlyout();
            foreach (var mode in new[] { SceneRenderService.ViewmodelPreviewMode.Hidden, SceneRenderService.ViewmodelPreviewMode.AsWorld, SceneRenderService.ViewmodelPreviewMode.GameView })
            {
                var mm = mode;
                var mi = new MenuItem { Header = mode == SceneRenderService.ViewmodelPreviewMode.Hidden ? "Hide first-person layer" : mode == SceneRenderService.ViewmodelPreviewMode.AsWorld ? "Show in the world (placement)" : "Game view (as the player sees it)", ToggleType = MenuItemToggleType.Radio, IsChecked = SceneRenderService.EditorViewmodelPreview == mode };
                mi.Click += (a, b) => { SceneRenderService.EditorViewmodelPreview = mm; SceneRenderService.RuntimeDirty = true; EditorViewportSession.RequestResubmit(); SyncToggles(); };
                m.Items.Add(mi);
            }
            m.ShowAt(FpToggle);
            SyncToggles();
        }

        /// <summary>The "⋯" menu: shading (shaded / wireframe), snap size, camera speed, camera actions, debug draws.</summary>
        private void OnViewOptions(object s, RoutedEventArgs e)
        {
            var m = new MenuFlyout();
            var shaded = new MenuItem { Header = "Shaded", ToggleType = MenuItemToggleType.Radio, IsChecked = !VortexAPI.IsWireframeMode };
            shaded.Click += (a, b) => SetWireframe(false);
            var wire = new MenuItem { Header = "Wireframe", ToggleType = MenuItemToggleType.Radio, IsChecked = VortexAPI.IsWireframeMode };
            wire.Click += (a, b) => SetWireframe(true);
            m.Items.Add(shaded); m.Items.Add(wire);
            m.Items.Add(new Separator());
            var snap = new MenuItem { Header = "Snap Size" };
            foreach (var mi in SnapItems()) snap.Items.Add(mi);
            m.Items.Add(snap);
            var speed = new MenuItem { Header = "Camera Speed" };
            foreach (var v in new[] { 1f, 2.5f, 5f, 10f, 20f, 40f })
            {
                var vv = v;
                var mi = new MenuItem { Header = v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " m/s" + (v == 5f ? " (default)" : ""), ToggleType = MenuItemToggleType.Radio, IsChecked = Math.Abs(EditorCameraController.Instance.MoveSpeed - v) < 0.01f };
                mi.Click += (a, b) => SetCameraSpeed(vv);
                speed.Items.Add(mi);
            }
            m.Items.Add(speed);
            m.Items.Add(new Separator());
            var frame = new MenuItem { Header = "Frame Selection", InputGesture = new KeyGesture(Key.F) }; frame.Click += (a, b) => EditorCommands.FocusSelected();
            var reset = new MenuItem { Header = "Reset Camera", InputGesture = new KeyGesture(Key.Home) }; reset.Click += (a, b) => EditorCommands.ResetCamera();
            m.Items.Add(frame); m.Items.Add(reset);
            m.Items.Add(new Separator());
            var phys = new MenuItem { Header = "Physics Debug (play)", ToggleType = MenuItemToggleType.CheckBox, IsChecked = Editor.Core.Services.Physics.PhysicsService.ShowPhysicsDebug };
            phys.Click += (a, b) => EditorCommands.TogglePhysicsDebug();
            // #106: the debug layers — bodies (static grey / kinematic blue / dynamic cyan / sleeping dim), joints, characters, contacts
            var layers = new MenuItem { Header = "Physics Debug Layers" };
            foreach (var (name, flag) in new[] { ("Bodies", Editor.Core.Services.Physics.PhysicsService.DebugLayer.Bodies), ("Joints", Editor.Core.Services.Physics.PhysicsService.DebugLayer.Joints), ("Characters", Editor.Core.Services.Physics.PhysicsService.DebugLayer.Characters), ("Contacts", Editor.Core.Services.Physics.PhysicsService.DebugLayer.Contacts) })
            {
                var item = new MenuItem { Header = name, ToggleType = MenuItemToggleType.CheckBox, IsChecked = (Editor.Core.Services.Physics.PhysicsService.DebugLayers & flag) != 0 };
                var f = flag;
                item.Click += (a, b) => EditorCommands.TogglePhysicsDebugLayer(f);
                layers.Items.Add(item);
            }
            var all = new MenuItem { Header = "Show All Colliders", ToggleType = MenuItemToggleType.CheckBox, IsChecked = EditorViewportService.Instance.ShowAllColliders };
            all.Click += (a, b) => EditorCommands.ToggleAllColliders();
            var fx = new MenuItem { Header = "Post Effects in Viewport (FX)", ToggleType = MenuItemToggleType.CheckBox, IsChecked = EnvironmentPanel.PreviewPostEffects };
            fx.Click += (a, b) => EnvironmentPanel.SetPreviewPostEffects(!EnvironmentPanel.PreviewPostEffects);
            m.Items.Add(phys); m.Items.Add(layers); m.Items.Add(all); m.Items.Add(fx);
            m.ShowAt(ViewOptionsButton);
        }

        public void SetWireframe(bool on)
        {
            VortexAPI.SetWireframe(on);
            SceneRenderService.RuntimeDirty = true;
            EditorViewportSession.RequestResubmit();
            SyncToggles();
        }

        public void SetCameraSpeed(float metersPerSecond)
        {
            EditorCameraController.Instance.MoveSpeed = Math.Max(0.1f, metersPerSecond);
            EditorCommands.Toast("Camera speed " + EditorCameraController.Instance.MoveSpeed.ToString("0.#") + " m/s");
        }

        // ------------------------------------------------------------ cameras
        private int CountCameras()
        {
            var scene = ProjectData.Current?.ActiveScene; int n = 0;
            if (scene?.Entities != null) foreach (var e in scene.Entities) n += Count(e);
            return n;
        }
        private static int Count(GameEntity e) { int n = e.GetComponent<Camera>() != null ? 1 : 0; if (e.Children != null) foreach (var c in e.Children) n += Count(c); return n; }

        public void RefreshCameraList()
        {
            _syncing = true;
            var items = new List<CameraChoice> { new CameraChoice { Label = "Free Camera" } };
            var scene = ProjectData.Current?.ActiveScene;
            if (scene?.Entities != null) foreach (var e in scene.Entities) AddCameras(e, items);
            items.Add(new CameraChoice { Label = "FP Preview (in-game)", FpPreview = true });
            CameraSelector.ItemsSource = items;
            CameraSelector.SelectedIndex = 0;
            _lastCameraCount = CountCameras();
            _syncing = false;
        }
        private static void AddCameras(GameEntity e, List<CameraChoice> items)
        {
            var cam = e.GetComponent<Camera>();
            if (cam != null) items.Add(new CameraChoice { Label = (cam.IsMainCamera ? "★ " : "") + e.Name, Entity = e });
            if (e.Children != null) foreach (var c in e.Children) AddCameras(c, items);
        }
        private void OnCameraSelected(object s, SelectionChangedEventArgs e)
        {
            if (_syncing || !(CameraSelector.SelectedItem is CameraChoice ch)) return;
            if (ch.FpPreview)
            {
                Session.ViewThroughCamera(null);
                SceneRenderService.EditorViewmodelPreview = SceneRenderService.ViewmodelPreviewMode.GameView;
                PlayCameraHelper.ApplyMainCamera(ProjectData.Current?.ActiveScene);
                SceneRenderService.RuntimeDirty = true;
                SyncToggles();
                return;
            }
            Session.ViewThroughCamera(ch.Entity);
        }
        private void OnPipToggle(object s, RoutedEventArgs e)
        {
            if (PipToggle.IsChecked != true) { CameraPreviewService.Instance.ClosePreview(); return; }
            var cam = CameraSelector.SelectedItem is CameraChoice ch && ch.Entity != null ? ch.Entity : SelectedOrFirstCamera();
            if (cam == null) { EditorCommands.Toast("No camera in the scene"); PipToggle.IsChecked = false; return; }
            CameraPreviewService.Instance.ShowPreview(cam);
        }
        private void SyncPip() { _syncing = true; PipToggle.IsChecked = _pip.IsVisible; _syncing = false; }

        private GameEntity SelectedOrFirstCamera()
        {
            var sel = SelectionService.Instance.SelectedEntity;
            if (sel?.GetComponent<Camera>() != null) return sel;
            var scene = ProjectData.Current?.ActiveScene; if (scene?.Entities == null) return null;
            foreach (var e in scene.Entities) { var r = Find(e); if (r != null) return r; }
            return null;
        }
        private static GameEntity Find(GameEntity e) { if (e.GetComponent<Camera>() != null) return e; if (e.Children != null) foreach (var c in e.Children) { var r = Find(c); if (r != null) return r; } return null; }

        // ------------------------------------------------------------ layout (secondary camera views)
        private void OnLayoutClick(object s, RoutedEventArgs e)
        {
            if (!(s is RadioButton rb) || !int.TryParse(rb.Tag as string, out int layout)) return;
            SetLayout(layout);
        }

        public void SetLayout(int layout)
        {
            _layout = Math.Max(1, Math.Min(4, layout));
            bool two = _layout >= 2, quad = _layout == 4;
            bool vertical = _layout == 2 || quad, horizontal = _layout == 3 || quad;
            ViewGrid.ColumnDefinitions[1].Width = vertical ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            ViewGrid.RowDefinitions[1].Height = horizontal ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Cell2.IsVisible = vertical; Cell3.IsVisible = horizontal; Cell4.IsVisible = quad;
            Grid.SetColumnSpan(MainCell, vertical ? 1 : 2);
            Grid.SetRowSpan(MainCell, horizontal ? 1 : 2);
            Grid.SetColumnSpan(Cell3, quad ? 1 : 2);
            EnsureSecondary(Secondary1, 0, vertical);
            EnsureSecondary(Secondary2, 1, horizontal);
            EnsureSecondary(Secondary3, 2, quad);
            // Secondary panes render through readback while the main view keeps its swapchain: cheap when idle.
            Engine.Session.SecondaryViewsActive = two;
            _syncing = true;
            LayoutSingle.IsChecked = _layout == 1; LayoutSplitV.IsChecked = _layout == 2; LayoutSplitH.IsChecked = _layout == 3; LayoutQuad.IsChecked = _layout == 4;
            _syncing = false;
            _pip.Reposition();
        }

        private void EnsureSecondary(ContentControl host, int index, bool wanted)
        {
            var existing = host.Content as SecondaryViewportView;
            if (wanted && existing == null) { var v = new SecondaryViewportView(index); host.Content = v; _secondaries.Add(v); }
            else if (!wanted && existing != null) { existing.Shutdown(); host.Content = null; _secondaries.Remove(existing); }
        }

        // ------------------------------------------------------------ drop target

        private void OnDragOver(object s, DragEventArgs e)
        {
            e.DragEffects = DragDropEffects.None;
            if (PlayModeService.Instance.IsPlaying || ProjectData.Current?.ActiveScene == null) return;
            string path = Inspector.PropertyRows.DroppedPath(e, "vortex/asset");
            if (path == null) return;
            string full = ResolveAssetPath(path);
            string ext = Ext(full);
            if (ext == ".vmat" || ext == ".cs")
            {
                var p = e.GetPosition(Engine);
                e.DragEffects = Session.PickEntityAt(p.X, p.Y) != null ? DragDropEffects.Link : DragDropEffects.None;
            }
            else if (CanPlace(full) || ext == ".vscene") e.DragEffects = DragDropEffects.Copy;
        }

        private async void OnDrop(object s, DragEventArgs e)
        {
            string path = Inspector.PropertyRows.DroppedPath(e, "vortex/asset");
            if (path == null) return;
            e.Handled = true;
            await DropAssetAt(path, e.GetPosition(Engine));
        }

        /// <summary>The viewport drop handler (also the smoke / API entry point): <paramref name="pathOrPrimitive"/> is
        /// the "vortex/asset" payload (project-relative or absolute path, or "Primitive:Cube"), <paramref name="p"/> the
        /// drop point in viewport coordinates. Models / prefabs / primitives are placed where the pointer ray hits the
        /// scene (AssetActions.AddToScene); a material goes onto the object under the pointer; a script is attached to
        /// it; a scene file is loaded; a file from outside the project is imported first.</summary>
        public async Task<GameEntity> DropAssetAt(string pathOrPrimitive, Point p)
        {
            if (ProjectData.Current?.ActiveScene == null || string.IsNullOrEmpty(pathOrPrimitive)) return null;
            string full = ResolveAssetPath(pathOrPrimitive);
            string ext = Ext(full);
            try
            {
                if (ext == ".vmat" || ext == ".cs")
                {
                    var target = Session.PickEntityAt(p.X, p.Y);
                    if (target == null) { EditorCommands.Toast("Drop it onto an object"); return null; }
                    if (ext == ".vmat")
                    {
                        if (ApplyMaterial(target, full)) EditorCommands.Toast("Material '" + Path.GetFileNameWithoutExtension(full) + "' → " + target.Name);
                        else EditorCommands.Toast(target.Name + " has no mesh to take a material");
                    }
                    else
                    {
                        string rel = ScriptingService.MakeRelative(ProjectData.Current.Path, full);
                        target.AddComponent(new Editor.ECS.Components.Scripting.Script(target, rel));
                        SelectionService.Instance.Select(target);
                        EditorCommands.Window?.Inspector?.Refresh();
                        EditorCommands.Toast("Script " + Path.GetFileNameWithoutExtension(full) + " → " + target.Name);
                    }
                    return target;
                }
                if (ext == ".vscene") { EditorCommands.LoadSceneFile(full); return null; }
                if (!full.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase) && !IsInsideProject(full))
                {
                    var imported = await ImportExternal(full);
                    if (string.IsNullOrEmpty(imported)) return null;
                    full = imported;
                }
                if (!CanPlace(full)) { EditorCommands.Toast("Can't place " + Path.GetFileName(full) + " in the scene"); return null; }
                var hit = DropPoint(p.X, p.Y);
                var created = VortexEditor.Services.AssetActions.AddToScene(full, hit, null);
                LastDropPoint = hit;
                if (created == null) { EditorCommands.Toast("Can't add " + Path.GetFileName(full) + " to the scene"); return null; }
                SceneRenderService.RuntimeDirty = true;
                EditorViewportSession.RequestResubmit();
                return created;
            }
            catch (Exception ex) { EditorCommands.Fail("Drop", ex); return null; }
        }

        /// <summary>Where the last placement drop landed (smoke checks).</summary>
        public Editor.ECS.Vector3? LastDropPoint { get; private set; }

        /// <summary>World point under a viewport point: the nearest object hit, else the ground plane (y = 0), else
        /// 8 m in front of the camera.</summary>
        public Editor.ECS.Vector3 DropPoint(double x, double y)
        {
            double w = Engine.Bounds.Width, h = Engine.Bounds.Height;
            var cam = EditorCameraController.Instance;
            if (w < 2 || h < 2)
                return new Editor.ECS.Vector3(cam.PositionX, cam.PositionY, cam.PositionZ + 8);
            var ray = RaycastService.Instance.ScreenToRayWithAspect((float)(x / w), (float)(y / h), (float)(w / h), 1f);
            var scene = ProjectData.Current?.ActiveScene;
            var hits = scene != null ? RaycastService.Instance.RaycastAll(ray, scene) : null;
            if (hits != null && hits.Count > 0)
            {
                var pt = hits[0].Point;
                return new Editor.ECS.Vector3(pt.X, pt.Y, pt.Z);
            }
            if (ray.Direction.Y < -1e-4f)
            {
                float t = -ray.Origin.Y / ray.Direction.Y;
                if (t > 0 && t < 500) { var g = ray.Origin + ray.Direction * t; return new Editor.ECS.Vector3(g.X, 0f, g.Z); }
            }
            var f = ray.Origin + ray.Direction * 8f;
            return new Editor.ECS.Vector3(f.X, f.Y, f.Z);
        }

        private static readonly string[] Placeable = { ".glb", ".gltf", ".fbx", ".obj", ".dae", ".3ds", ".blend", ".vmesh", ".ventity" };
        private static string Ext(string full) => full != null && full.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase) ? "" : (Path.GetExtension(full ?? "") ?? "").ToLowerInvariant();
        public static bool CanPlace(string full) => !string.IsNullOrEmpty(full) && (full.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase) || Array.IndexOf(Placeable, Ext(full)) >= 0);

        /// <summary>"vortex/asset" payload → full path (primitives stay "Primitive:Name").</summary>
        public static string ResolveAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) return path;
            if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
            var root = ProjectData.Current?.Path;
            return root == null ? path : Path.GetFullPath(Path.Combine(root, path.Replace('\\', '/')));
        }

        public static bool IsInsideProject(string full)
        {
            var root = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(full)) return false;
            string r = Path.GetFullPath(root).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            return Path.GetFullPath(full).StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Import a file dropped from Finder (outside the project) through the import dialog; returns the
        /// imported file's path (null = cancelled / failed).</summary>
        public static async Task<string> ImportExternal(string file)
        {
            var imported = await EditorWindows.ImportAssets(new[] { file }, null);   // null = the default folder of the file type
            var first = imported?.FirstOrDefault(i => !string.IsNullOrEmpty(i));
            if (first == null) return null;
            EditorCommands.Window?.AssetBrowser?.Refresh();
            return ResolveAssetPath(first);
        }

        /// <summary>Unreal-style material assignment (WPF ViewportDropHandler.HandleMaterialDrop), undoable: a
        /// '#submeshN' part gets the material directly; a model container (or a multi-submesh base path) applies it to
        /// all its parts; returns false when no MeshRenderer changed.</summary>
        public static bool ApplyMaterial(GameEntity target, string vmatPath)
        {
            if (target == null || string.IsNullOrEmpty(vmatPath)) return false;
            string root = ProjectData.Current?.Path ?? "";
            string rel = vmatPath;
            if (!string.IsNullOrEmpty(root) && Path.IsPathRooted(rel) && rel.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring(root.Length).TrimStart('/', '\\');
            rel = rel.Replace('\\', '/');
            var renderers = new List<MeshRenderer>();
            var mr = target.GetComponent<MeshRenderer>();
            if (mr == null || IsMultiSubmeshBasePath(mr.MeshPath))
            {
                if (target.Children != null)
                    foreach (var child in target.Children)
                    {
                        var cr = child.GetComponent<MeshRenderer>();
                        if (cr != null && !string.IsNullOrEmpty(cr.MeshPath) && cr.MeshPath.IndexOf("#submesh", StringComparison.OrdinalIgnoreCase) >= 0) renderers.Add(cr);
                    }
            }
            else renderers.Add(mr);
            if (renderers.Count == 0) return false;
            // a store material on a floor/wall primitive: a copy tiled for the object's size instead of one stretched tile
            string fitNote = null;
            if (renderers.Count == 1 && ReferenceEquals(renderers[0], mr)) rel = VortexEditor.Services.MaterialFit.ForRenderer(target, mr, rel, out fitNote);
            if (fitNote != null) EditorCommands.Toast(fitNote);
            var old = renderers.Select(r => r.MaterialPath).ToList();
            UndoRedoManager.Instance.ExecuteAction("Assign Material " + Path.GetFileNameWithoutExtension(rel) + " → " + target.Name,
                () => { foreach (var r in renderers) r.MaterialPath = rel; Resubmit(target); },
                () => { for (int i = 0; i < renderers.Count; i++) renderers[i].MaterialPath = old[i]; Resubmit(target); });
            return true;
        }

        private static void Resubmit(GameEntity e)
        {
            if (e?.Scene != null) e.Scene.IsDirty = true;
            SceneRenderService.RuntimeDirty = true;
            EditorViewportSession.RequestResubmit();
        }

        private static bool IsMultiSubmeshBasePath(string meshPath)
        {
            if (string.IsNullOrEmpty(meshPath) || meshPath.IndexOf('#') >= 0 || meshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) return false;
            string ext = (Path.GetExtension(meshPath) ?? "").ToLowerInvariant();
            if (Array.IndexOf(new[] { ".fbx", ".obj", ".gltf", ".glb", ".dae", ".3ds", ".blend" }, ext) < 0) return false;
            string full = Path.IsPathRooted(meshPath) ? meshPath : Path.Combine(ProjectData.Current?.Path ?? "", meshPath);
            if (!File.Exists(full)) return false;
            try { return VortexAPI.GetSubmeshCount(full) > 1; } catch { return false; }
        }
    }

    /// <summary>
    /// Camera preview (picture in picture, WPF CameraPreviewOverlay): a small live view of a scene camera in the lower
    /// right corner of the scene view, rendered through an off-screen target at ~30 fps. Lives in a popup so it is
    /// drawn above the native 3D view. Opened from the viewport PIP button, the hierarchy (double-click a camera /
    /// context menu) or <see cref="CameraPreviewService"/>.
    /// </summary>
    public sealed class CameraPreviewOverlay
    {
        private readonly Control _anchor;
        private readonly Popup _popup;
        private readonly Image _image = new Image { Stretch = Stretch.UniformToFill };
        private readonly TextBlock _type = new TextBlock { FontSize = 10, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _name = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170 };
        private readonly TextBlock _details = new TextBlock { Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center };
        private readonly Border _frame;
        private readonly SecondaryViewSession _session = new SecondaryViewSession { RenderIntervalMs = 33 };
        private readonly DispatcherTimer _timer;
        private WriteableBitmap _bitmap;
        private GameEntity _camera;
        private const double W = 320, H = 180;

        public Control Host { get; }
        public bool IsVisible => _popup.IsOpen;
        public GameEntity Camera => _camera;
        /// <summary>Frames rendered since the preview opened (smoke checks).</summary>
        public int FramesRendered { get; private set; }
        public Border Frame => _frame;

        public CameraPreviewOverlay(Control anchor)
        {
            _anchor = anchor;
            var close = new Button { Classes = { "icon", "small" }, Content = new VxIcon { Icon = "Close", Width = 12, Height = 12 }, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(close, "Close preview");
            close.Click += (s, e) => CameraPreviewService.Instance.ClosePreview();
            var typeBadge = new Border { Classes = { "badge" }, Child = _type, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            var header = new DockPanel { Margin = new Thickness(8, 5, 4, 5) };
            DockPanel.SetDock(close, Dock.Right); DockPanel.SetDock(typeBadge, Dock.Left);
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            right.Children.Add(_details); right.Children.Add(close);
            DockPanel.SetDock(right, Dock.Right);
            header.Children.Add(right); header.Children.Add(typeBadge); header.Children.Add(_name);
            var imageHost = new Border { Width = W, Height = H, ClipToBounds = true, Background = (IBrush)Application.Current.FindResource("VxViewportBgBrush"), Child = _image };
            var stack = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            stack.Children.Add(header); stack.Children.Add(imageHost);
            _frame = new Border
            {
                Background = (IBrush)Application.Current.FindResource("VxPanelRaisedBrush"),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(10),
                ClipToBounds = true,
                Child = stack,
                Margin = new Thickness(4),
            };
            _popup = new Popup
            {
                PlacementTarget = anchor,
                Placement = PlacementMode.AnchorAndGravity,
                PlacementAnchor = PopupAnchor.BottomRight,
                PlacementGravity = PopupGravity.TopLeft,
                HorizontalOffset = -12,
                VerticalOffset = -12,
                IsLightDismissEnabled = false,
                Child = _frame,
            };
            Host = new Panel { IsHitTestVisible = false, Children = { _popup } };
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (s, e) => Tick();
            anchor.SizeChanged += (s, e) => Reposition();
        }

        public void Show(GameEntity cameraEntity)
        {
            var cam = cameraEntity?.GetComponent<Camera>();
            if (cam == null) { Hide(); return; }
            _camera = cameraEntity;
            bool main = cam.IsMainCamera;
            _type.Text = main ? "MAIN CAM" : "GAME CAM";
            _name.Text = cameraEntity.Name;
            _details.Text = "FOV " + cam.FieldOfView.ToString("0") + "°";
            _frame.BorderBrush = new SolidColorBrush(main ? Color.FromRgb(155, 89, 182) : Color.FromRgb(86, 156, 214));
            _session.SetCamera(cameraEntity, false);
            FramesRendered = 0;
            if (TopLevel.GetTopLevel(_anchor) != null) _popup.IsOpen = true;
            _timer.Start();
        }

        public void Hide()
        {
            _camera = null;
            _timer.Stop();
            _popup.IsOpen = false;
        }

        /// <summary>The entity is still part of its scene (not deleted): every link up to the top level holds.</summary>
        private static bool IsInScene(GameEntity e)
        {
            if (e?.Scene?.Entities == null) return false;
            var cur = e;
            while (cur.Parent != null) { if (cur.Parent.Children == null || !cur.Parent.Children.Contains(cur)) return false; cur = cur.Parent; }
            return e.Scene.Entities.Contains(cur);
        }

        /// <summary>Re-place the popup after the scene view moved or resized.</summary>
        public void Reposition()
        {
            if (!_popup.IsOpen) return;
            _popup.HorizontalOffset = _popup.HorizontalOffset == -12 ? -12.001 : -12;
        }

        private void Tick()
        {
            if (_camera == null) return;
            if (!IsInScene(_camera)) { CameraPreviewService.Instance.ClosePreview(); return; }
            double scale = TopLevel.GetTopLevel(_anchor)?.RenderScaling ?? 1.0;
            int w = (int)(W * scale), h = (int)(H * scale);
            if (!_session.RenderIfNeeded(w, h, out var pixels, out int pw, out int ph, out int pitch)) return;
            if (_bitmap == null || _bitmap.PixelSize.Width != pw || _bitmap.PixelSize.Height != ph)
            {
                _bitmap = new WriteableBitmap(new PixelSize(pw, ph), new Vector(96 * scale, 96 * scale), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                _image.Source = _bitmap;
            }
            using (var fb = _bitmap.Lock())
            {
                unsafe
                {
                    fixed (byte* src = pixels)
                    {
                        byte* dst = (byte*)fb.Address;
                        int rowBytes = Math.Min(pitch, fb.RowBytes);
                        for (int y = 0; y < ph; y++) Buffer.MemoryCopy(src + y * pitch, dst + y * fb.RowBytes, fb.RowBytes, rowBytes);
                    }
                }
            }
            _image.InvalidateVisual();
            FramesRendered++;
        }
    }
}
