using System;
using System.Collections.Generic;
using Editor.Core.Data;
using Editor.Core.Input;
using Editor.Core.Services;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.Scripting;
using Editor.UI.Vui;

namespace Editor.Core.Viewport
{
    /// <summary>
    /// The editor's main 3D viewport, independent of any UI framework: owns the native render surface, the
    /// per-frame tick (camera → simulation → scene submit → render), selection picking, gizmo dragging, the
    /// in-viewport play mode (non-destructive snapshot/restore, physics registration, mouse-look, retained UI
    /// feed, debug freecam) and the keyboard shortcuts. The UI shell forwards neutral pointer/key events and
    /// calls <see cref="Tick"/> once per frame. (Port of the WPF GamePreviewView logic.)
    /// </summary>
    public sealed class EditorViewportSession
    {
        public static EditorViewportSession Main { get; private set; }

        private IViewportHost _host;
        private bool _initialized;
        private readonly EditorCameraController _camera = EditorCameraController.Instance;
        private Scene _currentScene;
        private object _submittedScene;
        private volatile bool _sceneDirty = true;
        private int _resubmitHoldFrames;
        private bool _gameViewMode;
        private bool _viewingThroughGameCamera;
        private DateTime _lastFrameTime = DateTime.Now;
        private DateTime _lastStatusUpdate = DateTime.MinValue;
        private uint _pixelW, _pixelH;

        // pointer state (logical viewport coordinates, from the shell's events)
        private double _pointerX, _pointerY;
        private bool _lmbDown, _rmbDown;

        // gizmo drag
        private bool _isDraggingGizmo;
        private GizmoAxis _activeGizmoAxis = GizmoAxis.None;
        private double _lastDragX, _lastDragY;
        private Vector3 _dragRawPos, _dragRawRot, _dragRawScale;

        // play-mode simulation (non-destructive)
        private bool _simActive;
        private readonly List<(GameEntity ent, Vector3 start)> _physicsEntities = new List<(GameEntity, Vector3)>();
        private readonly List<(GameEntity ent, Vector3 pos, Vector3 rot, Vector3 scale)> _transformSnapshot = new List<(GameEntity, Vector3, Vector3, Vector3)>();
        private readonly List<(GameEntity ent, float r, float g, float b, float a)> _colorSnapshot = new List<(GameEntity, float, float, float, float)>();
        private PlaySnapshot _componentSnapshot;   // lights a script changed (flicker, flashlight)
        private float _snapCamX, _snapCamY, _snapCamZ, _snapCamYaw, _snapCamPitch, _snapCamRoll;
        private bool _hasCamSnap;
        private Vector3 _extSnapPos, _extSnapRot;

        // game mouse-look
        private bool _mouseCaptured, _mouseJustCaptured, _userUnlockedCursor;
        private bool _cursorHidden;

        // editor-play UI feed
        private bool _playGridHidden, _savedGrid = true, _lmbPrevEd;
        private float _editorFovApplied = float.NaN;
        private float _uiMx, _uiMy; private bool _uiDown, _uiPressed;
        private float _wheelAccum;   // wheel notches since the last script tick (#319)
        private static readonly char[] _vuiCharsEmpty = new char[0];
        private static readonly int[] _navVks = { 0x25, 0x26, 0x27, 0x28, 0x09, 0x0D, 0x20 };
        private readonly bool[] _navPrev = new bool[7];
        private readonly int[] _uiKeys = new int[8];
        private int _uiKeyCount;

        // debug freecam (P) during editor play
        private readonly DebugFreeCamera _debugCam = new DebugFreeCamera();

        /// <summary>Modal preview dialogs that own the shared render queue (viewport pauses while &gt; 0).</summary>
        public static int ActivePreviewDialogs;
        /// <summary>Modeless previews that coexist with the viewport (scene re-submitted every frame while &gt; 0).</summary>
        public static int ActiveCoexistPreviews;

        public event Action<string, string> StatusUpdated;     // (status line, "W x H")
        /// <summary>Split/quad layouts active: secondary views render through readback targets on their own timers.</summary>
        public bool SecondaryViewsActive { get; set; }
        /// <summary>A retained-UI screen the UI editor wants drawn over the edit-mode viewport (null = none).</summary>
        public VuiCanvas PreviewCanvas { get; set; }
        public event Action<bool> FlyModeChanged;
        public event Action<bool> CursorCaptureChanged;

        public bool IsInitialized => _initialized;
        public bool IsPlaying => PlayModeService.Instance.IsPlaying;
        public bool IsFlyMode => _camera.IsFlyMode;
        public bool IsGameViewMode => _gameViewMode;
        public bool IsViewingThroughGameCamera => _viewingThroughGameCamera;
        public bool IsMouseCaptured => _mouseCaptured;
        public EditorCameraController Camera => _camera;
        public int CurrentFps => VortexAPI.CurrentFPS;

        public Scene CurrentScene
        {
            get => _currentScene;
            set
            {
                if (ReferenceEquals(_currentScene, value)) return;
                _currentScene = value;
                SceneRenderService.Instance.ClearAllRenderables();
                try { SceneRenderService.EvictModelsUnusedBy(value); } catch { }   // #358
                _sceneDirty = true;
            }
        }

        /// <summary>Force a scene re-submit on the next frames (after an external edit).</summary>
        public static void RequestResubmit()
        {
            var m = Main;
            if (m != null) { m._sceneDirty = true; m._resubmitHoldFrames = 30; }
        }

        // ------------------------------------------------------------------------------------------ lifecycle

        /// <summary>Create the native render surface on the shell's window handle (NSView*/HWND) and start.</summary>
        public bool Initialize(IntPtr nativeHandle, uint pixelWidth, uint pixelHeight, IViewportHost host)
        {
            if (_initialized) return true;
            _host = host;
            _pixelW = Math.Max(1u, pixelWidth);
            _pixelH = Math.Max(1u, pixelHeight);
            if (!VortexAPI.InitRenderViewport(nativeHandle, _pixelW, _pixelH))
            {
                ConsoleService.Instance.LogError("Viewport: the renderer could not be initialised on this window.");
                return false;
            }
            _initialized = true;
            Main = this;
            SceneRenderService.Instance.Initialize();
            _camera.Reset();
            EditorViewportService.Instance.IsGridVisible = true;
            _lastFrameTime = DateTime.Now;

            PlayModeService.Instance.StateChanged += OnPlayModeStateChanged;
            PlayModeService.Instance.GameViewChanged += OnGameViewChanged;
            SelectionService.Instance.FocusRequested += OnFocusRequested;
            SelectionService.Instance.SelectionChanged += OnSelectionChanged;
            SelectionService.Instance.TransformChanged += OnTransformChanged;
            return true;
        }

        public void Shutdown()
        {
            if (!_initialized) return;
            PlayModeService.Instance.StateChanged -= OnPlayModeStateChanged;
            PlayModeService.Instance.GameViewChanged -= OnGameViewChanged;
            SelectionService.Instance.FocusRequested -= OnFocusRequested;
            SelectionService.Instance.SelectionChanged -= OnSelectionChanged;
            SelectionService.Instance.TransformChanged -= OnTransformChanged;
            if (_simActive) EndPlaySimulation();
            ReleaseGameMouse();
            _initialized = false;
            if (ReferenceEquals(Main, this)) Main = null;
            try { VortexAPI.ShutdownRender(); } catch { }
        }

        public void Resize(uint pixelWidth, uint pixelHeight)
        {
            if (!_initialized) return;
            pixelWidth = Math.Max(1u, pixelWidth); pixelHeight = Math.Max(1u, pixelHeight);
            if (pixelWidth == _pixelW && pixelHeight == _pixelH) return;
            _pixelW = pixelWidth; _pixelH = pixelHeight;
            VortexAPI.ResizeRender(_pixelW, _pixelH);
        }

        private void OnSelectionChanged(object s, SelectionEventArgs e) => _sceneDirty = true;
        private void OnTransformChanged(object s, TransformChangedEventArgs e) => _sceneDirty = true;

        // ------------------------------------------------------------------------------------------ per frame

        /// <summary>One editor frame: camera → (play: input, engine step, scripts, UI) → submit → render.</summary>
        public void Tick()
        {
            if (!_initialized) return;
            if (ActivePreviewDialogs > 0 && PlayModeService.Instance.State != PlayState.Playing) return;

            bool playing = PlayModeService.Instance.State == PlayState.Playing;
            var now = DateTime.Now;
            float dt = (float)(now - _lastFrameTime).TotalSeconds;
            _lastFrameTime = now;
            if (dt > 0.25f) dt = 0.25f;

            if (playing) { /* the game drives the view (applied after the scripts ran) */ }
            else if (_gameViewMode) { ApplyMainCameraView(); NotifyFly(false); }
            else if (!CameraService.Instance.IsGameCameraActive) { _camera.Update(dt); NotifyFly(_camera.IsFlyMode); }
            else
            {
                var active = CameraService.Instance.ActiveCamera;
                if (active.IsValid) VortexAPI.ApplyEngineCameraToRenderer(active);
                NotifyFly(false);
            }

            if (!playing)
                AudioPreviewService.Instance.Tick(_camera.PositionX, _camera.PositionY, _camera.PositionZ, _camera.Yaw, _camera.Pitch);

            double uw = _host != null ? _host.Width : 0, uh = _host != null ? _host.Height : 0;
            if (playing)
            {
                bool external = PlayModeService.Instance.IsExternalWindow;
                if (!external)
                {
                    UpdateGameMouseLook();
                    _debugCam.PreScripts(_currentScene, !PlayModeService.Instance.IsReleaseMode, _mouseCaptured || (_host != null && _host.IsWindowActive));
                }
                if (uw > 1 && uh > 1)
                {
                    if (!_playGridHidden) { _savedGrid = VortexAPI.IsGridVisible; VortexAPI.ShowGrid(false); _playGridHidden = true; }
                    FeedEditorUI((float)uw, (float)uh);
                    VortexAPI.UIBegin((float)uw, (float)uh);
                }

                VortexAPI.StepEngineRuntime(dt);
                ReadbackPhysics();
                // this tick's wheel notches, 0 on a tick without any (#319): the wheel event used to set ScrollDelta and
                // nothing reset it, so a zoom or weapon-switch script kept scrolling after one notch
                Vortex.Input.ScrollDelta = _wheelAccum; _wheelAccum = 0f;
                ScriptRuntime.Instance.Update(dt);
                GameRuntime.ProcessPendingSceneSwitch();
                AudioPlaybackService.Instance.Tick();
                if (external) PlayCameraHelper.ApplyPose(_extSnapPos, _extSnapRot);
                else if (!_debugCam.ApplyView(dt)) ApplyMainCameraView();

                if (!external && uw > 1 && uh > 1 && !PlayModeService.Instance.IsReleaseMode) _debugCam.DrawHint((float)uw, (float)uh);

                if (uw > 1 && uh > 1 && VuiStack.Instance.HasActiveScreens)
                {
                    var vin = new VuiInput { Mx = _uiMx, My = _uiMy, Down = _uiDown, Pressed = _uiPressed, Wheel = 0, Chars = _vuiCharsEmpty, CharCount = 0, KeyEvents = _uiKeys, KeyCount = _uiKeyCount };
                    VuiStack.Instance.TickAll((float)uw, (float)uh, vin);
                    var acts = VuiStack.Instance.ConsumeFiredActions();
                    if (acts != null) ScriptRuntime.Instance.InvokeUiActions(acts);
                }
            }
            else
            {
                if (_editorFovApplied != RaycastService.EditorFovYDegrees)
                {
                    VortexAPI.SetViewFOV(RaycastService.EditorFovYDegrees);
                    VortexAPI.SetViewClipPlanes(RaycastService.EditorNearClip, RaycastService.EditorFarClip);   // the game may have set its own (#327)
                    _editorFovApplied = RaycastService.EditorFovYDegrees;
                }
                if (_playGridHidden) { VortexAPI.ShowGrid(_savedGrid); _playGridHidden = false; }
                // the grid lies on y = 0: seen from underneath it is a lattice across the sky (a world built below 0, like the
                // oasis whose floor is at −5 m) — it stays off while the camera is under it and comes back when the camera rises
                bool gridWanted = EditorViewportService.Instance.IsGridVisible && _camera != null && _camera.PositionY >= -0.01f;
                if (gridWanted != VortexAPI.IsGridVisible) VortexAPI.ShowGrid(gridWanted);
                // UI editor preview: lay out + draw the screen through the engine's retained-UI renderer.
                var preview = PreviewCanvas;
                if (preview != null && uw > 1 && uh > 1)
                {
                    try
                    {
                        VortexAPI.UIBegin((float)uw, (float)uh);
                        preview.Layout((float)uw, (float)uh);
                        preview.Render();
                    }
                    catch { }
                }
            }

            var activeScene = ProjectData.Current?.ActiveScene;
            if (activeScene != null && !ReferenceEquals(activeScene, _currentScene)) CurrentScene = activeScene;

            var sceneToRender = _currentScene ?? ProjectData.Current?.ActiveScene;
            if (sceneToRender != null)
            {
                if (_resubmitHoldFrames > 0) _resubmitHoldFrames--;
                bool stressDirty = StressTestService.Dirty;
                bool dynamicDirty = playing && (SceneRenderService.RuntimeDirty || Editor.Core.Animation.AnimationService.Instance.HasActiveAnimators);
                bool needSubmit = IsPlaying || _sceneDirty || dynamicDirty || ActivePreviewDialogs > 0 || ActiveCoexistPreviews > 0
                                  || _resubmitHoldFrames > 0 || stressDirty || !ReferenceEquals(sceneToRender, _submittedScene);
                if (needSubmit)
                {
                    SceneRenderService.Instance.SubmitScene(sceneToRender);
                    if (StressTestService.Active) { StressTestService.Submit(); StressTestService.ClearDirty(); }
                    if (WorldService.HasItems) { WorldService.Submit(); WorldService.ClearDirty(); }
                    _submittedScene = sceneToRender;
                    _sceneDirty = false;
                    SceneRenderService.RuntimeDirty = false;
                }
                if (!playing) SceneRenderService.Instance.SubmitOverlays(sceneToRender);
            }

            VortexAPI.RenderOnce();

            if ((now - _lastStatusUpdate).TotalMilliseconds >= 500)
            {
                _lastStatusUpdate = now;
                PublishStatus();
            }
        }

        private bool _flyShown;
        private void NotifyFly(bool fly)
        {
            if (fly == _flyShown) return;
            _flyShown = fly;
            FlyModeChanged?.Invoke(fly);
        }

        private void PublishStatus()
        {
            var pms = PlayModeService.Instance;
            string status = $"FPS {VortexAPI.CurrentFPS}   ·   Draw Calls {VortexAPI.DrawCalls}   ·   Vertices {VortexAPI.VertexCount:N0}";
            if (pms.IsPlaying && pms.IsExternalWindow)
            {
                float t = VortexAPI.GameTime();
                status += $"   ·   ▶ {((int)t) / 60:00}:{((int)t) % 60:00}   ·   Game runs in its own window";
            }
            else if (pms.IsPlaying)
            {
                float t = VortexAPI.GameTime();
                status += $"   ·   ▶ {((int)t) / 60:00}:{((int)t) % 60:00}";
                status += _mouseCaptured ? "   ·   Esc releases the mouse" : "   ·   Click to capture the mouse";
            }
            else if (_gameViewMode)
                status += "   ·   Press Play to start";
            string res = _host != null ? $"{(int)_host.Width} × {(int)_host.Height}" + (_host.Scaling > 1.01 ? $"  @{_host.Scaling:0.#}x" : "") : "";
            StatusUpdated?.Invoke(status, res);
        }

        // ------------------------------------------------------------------------------------------ pointer input

        private bool _terrainStroke;   // #124: the left button is sculpting / painting the selected terrain
        private bool _foliageStroke;   // #125: the left button is painting / erasing the selected foliage

        /// <param name="button">0 = left, 1 = right, 2 = middle</param>
        public void OnPointerDown(int button, double x, double y, bool alt, bool ctrl, bool shift)
        {
            _pointerX = x; _pointerY = y;
            if (button == 0) _lmbDown = true; else if (button == 1) _rmbDown = true;

            if (IsPlaying)
            {
                _host?.FocusViewport();
                _userUnlockedCursor = false;
                return;
            }
            _host?.FocusViewport();

            if (button == 1 && !_viewingThroughGameCamera) SetCursorHidden(true);
            if (!_viewingThroughGameCamera) _camera.OnMouseDown(button == 1, new PointD(x, y));

            if (button == 0 && !alt && !_rmbDown && !_viewingThroughGameCamera)
            {
                // #124: with a terrain tool active and a terrain selected, the left button sculpts / paints under the cursor
                if (Editor.Core.Services.Terrain.TerrainToolService.Active && _host != null && _host.Width > 0 && _host.Height > 0)
                {
                    float tnx = (float)(x / _host.Width), tny = (float)(y / _host.Height), taspect = (float)(_host.Width / _host.Height);
                    if (Editor.Core.Services.Terrain.TerrainToolService.TryBeginStroke(tnx, tny, taspect, shift, ctrl)) { _terrainStroke = true; return; }
                }
                if (Editor.Core.Services.Foliage.FoliageToolService.Active && _host != null && _host.Width > 0 && _host.Height > 0)
                {
                    float fnx = (float)(x / _host.Width), fny = (float)(y / _host.Height), faspect = (float)(_host.Width / _host.Height);
                    if (Editor.Core.Services.Foliage.FoliageToolService.TryBeginStroke(fnx, fny, faspect, shift, ctrl)) { _foliageStroke = true; return; }
                }
                var selected = SelectionService.Instance.SelectedEntity;
                if (selected != null && VortexAPI.AreGizmosVisible && selected.Transform != null && _host != null && _host.Width > 0 && _host.Height > 0)
                {
                    var transform = selected.Transform;
                    float nx = (float)(x / _host.Width), ny = (float)(y / _host.Height);
                    float aspect = (float)(_host.Width / _host.Height);
                    var gizmoPos = new Vector3f(transform.LocalPosition.X, transform.LocalPosition.Y, transform.LocalPosition.Z);
                    float gizmoScale = RaycastService.ComputeGizmoScale(gizmoPos);
                    var axis = RaycastService.Instance.PickGizmoAxis(nx, ny, gizmoPos, aspect, gizmoScale);
                    if (axis != GizmoAxis.None)
                    {
                        _isDraggingGizmo = true;
                        _activeGizmoAxis = axis;
                        _lastDragX = x; _lastDragY = y;
                        _dragRawPos = transform.LocalPosition;
                        _dragRawRot = transform.LocalRotation;
                        _dragRawScale = transform.LocalScale;
                        VortexAPI.IsDraggingGizmo = true;
                        VortexAPI.DraggingAxis = axis;
                        return;
                    }
                }
                HandleEntityPicking(x, y, ctrl, shift);
            }
        }

        public void OnPointerUp(int button, double x, double y)
        {
            _pointerX = x; _pointerY = y;
            if (button == 0) _lmbDown = false; else if (button == 1) _rmbDown = false;
            if (IsPlaying) return;
            if (button == 1) SetCursorHidden(false);
            if (!_viewingThroughGameCamera) _camera.OnMouseUp(button == 1);
            if (_terrainStroke && button == 0)
            {
                _terrainStroke = false;
                Editor.Core.Services.Terrain.TerrainToolService.EndStroke();
            }
            if (_foliageStroke && button == 0)
            {
                _foliageStroke = false;
                Editor.Core.Services.Foliage.FoliageToolService.EndStroke();
            }
            if (_isDraggingGizmo && button == 0)
            {
                _isDraggingGizmo = false;
                _activeGizmoAxis = GizmoAxis.None;
                VortexAPI.IsDraggingGizmo = false;
                VortexAPI.DraggingAxis = GizmoAxis.None;
            }
        }

        public void OnPointerMove(double x, double y)
        {
            double px = _pointerX, py = _pointerY;
            _pointerX = x; _pointerY = y;
            if (IsPlaying)
            {
                // Captured mouse-look: the delta comes from the pointer's offset to the centre before re-centering.
                if (_mouseCaptured && _host != null) _lookAccumX += x - _host.Width * 0.5; 
                if (_mouseCaptured && _host != null) _lookAccumY += y - _host.Height * 0.5;
                return;
            }
            if (_terrainStroke && _lmbDown)
            {
                if (_host != null && _host.Width > 0 && _host.Height > 0)
                    Editor.Core.Services.Terrain.TerrainToolService.ContinueStroke((float)(x / _host.Width), (float)(y / _host.Height), (float)(_host.Width / _host.Height), false, false);
                return;
            }
            if (_foliageStroke && _lmbDown)
            {
                if (_host != null && _host.Width > 0 && _host.Height > 0)
                    Editor.Core.Services.Foliage.FoliageToolService.ContinueStroke((float)(x / _host.Width), (float)(y / _host.Height), (float)(_host.Width / _host.Height), false, false);
                return;
            }
            if (_isDraggingGizmo && _lmbDown)
            {
                var selected = SelectionService.Instance.SelectedEntity;
                if (selected != null && selected.Transform != null && _host != null && _host.Width > 0 && _host.Height > 0)
                {
                    float nx = (float)(x / _host.Width), ny = (float)(y / _host.Height);
                    float lx = (float)(_lastDragX / _host.Width), ly = (float)(_lastDragY / _host.Height);
                    var transform = selected.Transform;
                    var entityPos = new Vector3f(transform.LocalPosition.X, transform.LocalPosition.Y, transform.LocalPosition.Z);
                    var delta = RaycastService.Instance.CalculateAxisDragDelta(nx, ny, lx, ly, entityPos, _activeGizmoAxis);
                    bool snap = EditorViewportService.Instance.SnapToGrid;
                    switch (TransformGizmoService.Instance.CurrentMode)
                    {
                        case TransformGizmoService.GizmoMode.Translate:
                        {
                            _dragRawPos = new Vector3(_dragRawPos.X + delta.X, _dragRawPos.Y + delta.Y, _dragRawPos.Z + delta.Z);
                            var np = _dragRawPos;
                            if (snap) np = new Vector3(EditorViewportService.Instance.SnapValue(np.X), EditorViewportService.Instance.SnapValue(np.Y), EditorViewportService.Instance.SnapValue(np.Z));
                            transform.LocalPosition = np;
                            break;
                        }
                        case TransformGizmoService.GizmoMode.Rotate:
                        {
                            float rotDelta = (delta.X + delta.Y + delta.Z) * 50.0f;
                            var raw = _dragRawRot;
                            switch (_activeGizmoAxis)
                            {
                                case GizmoAxis.X: raw = new Vector3(raw.X + rotDelta, raw.Y, raw.Z); break;
                                case GizmoAxis.Y: raw = new Vector3(raw.X, raw.Y + rotDelta, raw.Z); break;
                                case GizmoAxis.Z: raw = new Vector3(raw.X, raw.Y, raw.Z + rotDelta); break;
                            }
                            _dragRawRot = raw;
                            transform.LocalRotation = snap ? new Vector3(SnapTo(raw.X, 15f), SnapTo(raw.Y, 15f), SnapTo(raw.Z, 15f)) : raw;
                            break;
                        }
                        case TransformGizmoService.GizmoMode.Scale:
                        {
                            float scaleDelta = (delta.X + delta.Y + delta.Z) * 0.5f;
                            var raw = _dragRawScale;
                            switch (_activeGizmoAxis)
                            {
                                case GizmoAxis.X: raw = new Vector3(Math.Max(0.01f, raw.X + scaleDelta), raw.Y, raw.Z); break;
                                case GizmoAxis.Y: raw = new Vector3(raw.X, Math.Max(0.01f, raw.Y + scaleDelta), raw.Z); break;
                                case GizmoAxis.Z: raw = new Vector3(raw.X, raw.Y, Math.Max(0.01f, raw.Z + scaleDelta)); break;
                            }
                            _dragRawScale = raw;
                            transform.LocalScale = snap
                                ? new Vector3(Math.Max(0.01f, SnapTo(raw.X, 0.25f)), Math.Max(0.01f, SnapTo(raw.Y, 0.25f)), Math.Max(0.01f, SnapTo(raw.Z, 0.25f)))
                                : raw;
                            break;
                        }
                    }
                    SelectionService.Instance.NotifyTransformChanged();
                    _lastDragX = x; _lastDragY = y;
                }
            }
            else
            {
                if (Editor.Core.Services.Terrain.TerrainToolService.Active && _host != null && _host.Width > 0 && _host.Height > 0)
                    Editor.Core.Services.Terrain.TerrainToolService.UpdateHover((float)(x / _host.Width), (float)(y / _host.Height), (float)(_host.Width / _host.Height));
                if (Editor.Core.Services.Foliage.FoliageToolService.Active && _host != null && _host.Width > 0 && _host.Height > 0)
                    Editor.Core.Services.Foliage.FoliageToolService.UpdateHover((float)(x / _host.Width), (float)(y / _host.Height), (float)(_host.Width / _host.Height));
                UpdateGizmoHover(x, y);
                if (!_viewingThroughGameCamera) _camera.OnMouseMove(new PointD(x, y));
            }
        }

        public void OnPointerWheel(int delta)
        {
            if (IsPlaying) { _wheelAccum += delta / 120f; return; }
            _wheelAccum = 0f;
            if (!_viewingThroughGameCamera) _camera.OnMouseWheel(delta);
        }

        private static float SnapTo(float value, float increment) => increment <= 0f ? value : (float)Math.Round(value / increment) * increment;

        private void UpdateGizmoHover(double x, double y)
        {
            if (_host == null || _host.Width <= 0 || _host.Height <= 0) { VortexAPI.HoveredAxis = GizmoAxis.None; return; }
            var selected = SelectionService.Instance.SelectedEntity;
            if (selected != null && selected.Transform != null && VortexAPI.AreGizmosVisible)
            {
                float nx = (float)(x / _host.Width), ny = (float)(y / _host.Height);
                float aspect = (float)(_host.Width / _host.Height);
                var t = selected.Transform;
                var gizmoPos = new Vector3f(t.LocalPosition.X, t.LocalPosition.Y, t.LocalPosition.Z);
                float gizmoScale = RaycastService.ComputeGizmoScale(gizmoPos);
                VortexAPI.HoveredAxis = RaycastService.Instance.PickGizmoAxis(nx, ny, gizmoPos, aspect, gizmoScale);
                VortexAPI.IsDraggingGizmo = _isDraggingGizmo;
                VortexAPI.DraggingAxis = _activeGizmoAxis;
            }
            else
            {
                VortexAPI.HoveredAxis = GizmoAxis.None;
                VortexAPI.IsDraggingGizmo = false;
                VortexAPI.DraggingAxis = GizmoAxis.None;
            }
        }

        /// <summary>Entity under a viewport point (same ray pick as click selection); null over empty space.</summary>
        public GameEntity PickEntityAt(double x, double y)
        {
            if (_host == null || _host.Width <= 0 || _host.Height <= 0) return null;
            var scene = _currentScene ?? ProjectData.Current?.ActiveScene;
            if (scene == null) return null;
            return RaycastService.Instance.PickEntity((float)(x / _host.Width), (float)(y / _host.Height), scene, (float)(_host.Width / _host.Height));
        }

        private void HandleEntityPicking(double x, double y, bool ctrl, bool shift)
        {
            var picked = PickEntityAt(x, y);
            if (picked != null) SelectionService.Instance.Select(picked);
            else if (!ctrl && !shift) SelectionService.Instance.ClearSelection();
        }

        // ------------------------------------------------------------------------------------------ keyboard

        /// <summary>Key pressed while the viewport has focus. Returns true when the viewport consumed it.</summary>
        public bool OnKeyDown(int vk, bool ctrl, bool alt, bool shift)
        {
            if (vk == 0x1B && !IsPlaying)   // Esc: deselect + drop focus
            {
                SelectionService.Instance.ClearSelection();
                _host?.ClearFocus();
                SetCursorHidden(false);
                return true;
            }
            if (IsPlaying) return false;
            if (!_viewingThroughGameCamera) _camera.OnKeyDown(vk, ctrl || alt);
            if (ctrl || alt) return false;
            bool tools = !_viewingThroughGameCamera && !_camera.IsFlyMode;
            switch (vk)
            {
                case 0x57: if (tools) { TransformGizmoService.Instance.SetTranslateMode(); return true; } break;   // W
                case 0x45: if (tools) { TransformGizmoService.Instance.SetRotateMode(); return true; } break;      // E
                case 0x52: if (tools) { TransformGizmoService.Instance.SetScaleMode(); return true; } break;       // R
                case 0x47: EditorViewportService.Instance.IsGridVisible = !EditorViewportService.Instance.IsGridVisible; return true; // G
                case 0x46: if (tools) { FocusOnSelected(); return true; } break;                                   // F
                case 0x24: if (!_viewingThroughGameCamera) { _camera.Reset(); return true; } break;               // Home
                case 0x58: if (tools) { TransformGizmoService.Instance.ToggleSpace(); return true; } break;        // X
            }
            return false;
        }

        public void OnKeyUp(int vk)
        {
            if (IsPlaying) return;
            if (!_viewingThroughGameCamera) _camera.OnKeyUp(vk);
        }

        public void FocusOnSelected()
        {
            var e = SelectionService.Instance.SelectedEntity;
            if (e?.Transform != null) { var p = e.Transform.LocalPosition; _camera.FocusOn(p.X, p.Y, p.Z, 8.0f); }
        }

        public void FocusOn(float x, float y, float z, float distance = 5f) => _camera.FocusOn(x, y, z, distance);

        private void OnFocusRequested(object sender, SelectionEventArgs e)
        {
            if (_viewingThroughGameCamera) return;
            var t = e.SelectedEntity?.Transform;
            if (t != null) { var p = t.LocalPosition; _camera.FocusOn(p.X, p.Y, p.Z, 8.0f); }
        }

        // ------------------------------------------------------------------------------------------ camera choice

        /// <summary>Look through a scene camera entity (null = back to the free editor camera).</summary>
        public void ViewThroughCamera(GameEntity cameraEntity)
        {
            if (cameraEntity == null)
            {
                _viewingThroughGameCamera = false;
                CameraService.Instance.SwitchToEditorCamera();
                SetCursorHidden(false);
                return;
            }
            var cam = cameraEntity.GetComponent<Editor.ECS.Components.Rendering.Camera>();
            if (cam == null) return;
            CameraService.Instance.SwitchToGameCamera(cameraEntity.Id);
            _viewingThroughGameCamera = true;
            _sceneDirty = true;
        }

        // ------------------------------------------------------------------------------------------ play mode

        private void OnGameViewChanged(bool active)
        {
            _gameViewMode = active;
            PublishStatus();
        }

        private void OnPlayModeStateChanged(object sender, PlayState state)
        {
            if (state == PlayState.Playing)
            {
                BeginPlaySimulation();
                AudioPlaybackService.Instance.ResumeAll();
                VortexAPI.ShowGrid(false);
                VortexAPI.ShowGizmos(false);
                if (PlayModeService.Instance.IsExternalWindow)
                {
                    var t = PlayCameraHelper.FindMainCamera(_currentScene ?? ProjectData.Current?.ActiveScene);
                    if (t != null) { _extSnapPos = t.LocalPosition; _extSnapRot = t.LocalRotation; }
                }
                else _userUnlockedCursor = false;
                PublishStatus();
                return;
            }
            if (state == PlayState.Paused)
            {
                AudioPlaybackService.Instance.PauseAll();
                ReleaseGameMouse();
                PublishStatus();
                return;
            }
            _debugCam.Reset();
            ReleaseGameMouse();
            EndPlaySimulation();
            _editorFovApplied = float.NaN;
            VortexAPI.ShowGrid(EditorViewportService.Instance.IsGridVisible);
            VortexAPI.ShowGizmos(EditorViewportService.Instance.AreGizmosVisible);
            PublishStatus();
        }

        private double _lookAccumX, _lookAccumY;

        private void UpdateGameMouseLook()
        {
            bool authorityWantsCapture = VuiStack.Instance.WantsCursorCapture(ScriptRuntime.Instance.CursorLocked);
            if (!authorityWantsCapture) _userUnlockedCursor = false;
            else if (HostInput.IsKeyDown(0x1B)) _userUnlockedCursor = true;

            bool wantCapture = authorityWantsCapture && !_userUnlockedCursor && (_host == null || _host.IsWindowActive);
            if (wantCapture && !_mouseCaptured) CaptureGameMouse();
            else if (!wantCapture && _mouseCaptured) ReleaseGameMouse();

            float dx = 0f, dy = 0f;
            if (_mouseCaptured && _host != null)
            {
                if (_mouseJustCaptured) { _mouseJustCaptured = false; _lookAccumX = _lookAccumY = 0; }
                else
                {
                    dx = (float)(_lookAccumX * _host.Scaling);
                    dy = (float)(_lookAccumY * _host.Scaling);
                    if (dx > 200f) dx = 200f; else if (dx < -200f) dx = -200f;
                    if (dy > 200f) dy = 200f; else if (dy < -200f) dy = -200f;
                }
                _lookAccumX = _lookAccumY = 0;
                if (dx != 0f || dy != 0f) _host.WarpCursorToCenter();
            }
            Vortex.Input.MouseDeltaX = dx;
            Vortex.Input.MouseDeltaY = dy;
        }

        private void CaptureGameMouse()
        {
            if (_mouseCaptured || _host == null) return;
            _mouseCaptured = true;
            _mouseJustCaptured = true;
            _lookAccumX = _lookAccumY = 0;
            SetCursorHidden(true);
            _host.WarpCursorToCenter();
            CursorCaptureChanged?.Invoke(true);
        }

        private void ReleaseGameMouse()
        {
            if (!_mouseCaptured) return;
            _mouseCaptured = false;
            SetCursorHidden(false);
            Vortex.Input.MouseDeltaX = 0f;
            Vortex.Input.MouseDeltaY = 0f;
            CursorCaptureChanged?.Invoke(false);
        }

        private void SetCursorHidden(bool hidden)
        {
            if (_cursorHidden == hidden) return;
            _cursorHidden = hidden;
            _host?.SetCursorHidden(hidden);
        }

        private void FeedEditorUI(float renderW, float renderH)
        {
            float mx = 0f, my = 0f;
            if (_host != null && _host.Width > 1)
            {
                mx = (float)(_pointerX * (renderW / _host.Width));
                my = (float)(_pointerY * (renderH / _host.Height));
            }
            bool down = _lmbDown;
            bool pressed = down && !_lmbPrevEd;
            _lmbPrevEd = down;
            _uiMx = mx; _uiMy = my; _uiDown = down; _uiPressed = pressed;

            _uiKeyCount = 0;
            for (int i = 0; i < _navVks.Length; i++)
            {
                bool held = HostInput.IsKeyDown(_navVks[i]);
                if (held && !_navPrev[i] && _uiKeyCount < _uiKeys.Length) _uiKeys[_uiKeyCount++] = _navVks[i];
                _navPrev[i] = held;
            }
            ScriptRuntime.Instance.SetUIFrame(renderW, renderH, mx, my, down, pressed);
        }

        private void BeginPlaySimulation()
        {
            if (_simActive) return;
            _simActive = true;
            _physicsEntities.Clear();
            SnapshotSceneForPlay();

            VortexAPI.ClearAllRigidbodies();
            VortexAPI.ClearAllColliders();
            VortexAPI.ResetGameClock();

            var scene = _currentScene ?? ProjectData.Current?.ActiveScene;
            // Physics v2 (#100): with a Jolt-enabled engine the rigid bodies are built by PhysicsService inside
            // ScriptRuntime.Begin (Collider + Rigidbody -> simulated body, poses written back each step). The old
            // toy AABB rigidbodies of the native runtime only remain as the fallback for a stub (no-Jolt) build.
            if (!PhysicsNative.Available && scene?.Entities != null)
                foreach (var e in scene.Entities) RegisterPhysicsRecursive(e);

            EnsurePlayerControllerOnMainCamera(scene);
            AudioPreviewService.Instance.Stop();
            AudioPlaybackService.Instance.BeginPlay(scene);
            ScriptRuntime.Instance.Begin(scene);
        }

        private void EnsurePlayerControllerOnMainCamera(Scene scene)
        {
            var camEnt = FindMainCameraEntity(scene);
            if (camEnt == null) return;
            if (camEnt.GetComponent<Editor.ECS.Components.Scripting.Script>() != null) return;
            var root = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(root)) return;
            var rel = ScriptingService.EnsurePlayerController(root);
            if (!string.IsNullOrEmpty(rel))
                camEnt.AddComponentDirect(new Editor.ECS.Components.Scripting.Script(camEnt, rel));
        }

        private static GameEntity FindMainCameraEntity(Scene scene)
        {
            if (scene?.Entities == null) return null;
            foreach (var e in scene.Entities) { var r = FindMainCamEntityRec(e); if (r != null) return r; }
            return null;
        }

        private static GameEntity FindMainCamEntityRec(GameEntity e)
        {
            if (e == null) return null;
            var cam = e.GetComponent<Editor.ECS.Components.Rendering.Camera>();
            if (cam != null && cam.IsMainCamera) return e;
            if (e.Children != null)
                foreach (var c in e.Children) { var r = FindMainCamEntityRec(c); if (r != null) return r; }
            return null;
        }

        private void RegisterPhysicsRecursive(GameEntity e)
        {
            if (e == null) return;
            var mr = e.GetComponent<Editor.ECS.Components.Rendering.MeshRenderer>();
            if (mr != null && e.Transform != null)
            {
                var s = e.Transform.LocalScale;
                var p = e.Transform.LocalPosition;
                float hx = Math.Max(0.05f, Math.Abs(s.X) * 0.5f);
                float hy = Math.Max(0.05f, Math.Abs(s.Y) * 0.5f);
                float hz = Math.Max(0.05f, Math.Abs(s.Z) * 0.5f);
                var rb = e.GetComponent<Editor.ECS.Components.Physics.Rigidbody>();
                if (rb != null && rb.BodyType == Editor.ECS.Components.Physics.RigidbodyType.Dynamic && Editor.Utilities.ID.IsValid(e.EntityId))
                {
                    _physicsEntities.Add((e, p));
                    VortexAPI.RegisterRigidbody(e.EntityId, rb.UseGravity, hx, hy, hz);
                }
                else VortexAPI.AddStaticBox(p.X, p.Y, p.Z, hx, hy, hz);
            }
            if (e.Children != null)
                foreach (var c in e.Children) RegisterPhysicsRecursive(c);
        }

        /// <summary>Legacy toy-physics readback (stub build only): mirrors the native AABB bodies' positions into the
        /// entities. With Jolt available <c>_physicsEntities</c> stays empty — PhysicsService writes the poses.</summary>
        private void ReadbackPhysics()
        {
            if (!_simActive || _physicsEntities.Count == 0) return;
            for (int i = 0; i < _physicsEntities.Count; i++)
            {
                var ent = _physicsEntities[i].ent;
                if (ent?.Transform == null || !Editor.Utilities.ID.IsValid(ent.EntityId)) continue;
                ent.Transform.SetLocalPositionFromEngine(VortexAPI.ReadEntityPosition(ent.EntityId));
            }
        }

        private void SnapshotSceneForPlay()
        {
            _snapCamX = _camera.PositionX; _snapCamY = _camera.PositionY; _snapCamZ = _camera.PositionZ;
            _snapCamYaw = _camera.Yaw; _snapCamPitch = _camera.Pitch; _snapCamRoll = _camera.Roll;
            _hasCamSnap = true;
            _transformSnapshot.Clear();
            _colorSnapshot.Clear();
            var scene = _currentScene ?? ProjectData.Current?.ActiveScene;
            _componentSnapshot = PlaySnapshot.Take(scene);
            if (scene?.Entities != null)
                foreach (var e in scene.Entities) SnapshotTransformRecursive(e);
        }

        private void SnapshotTransformRecursive(GameEntity e)
        {
            if (e == null) return;
            if (e.Transform != null)
                _transformSnapshot.Add((e, e.Transform.LocalPosition, e.Transform.LocalRotation, e.Transform.LocalScale));
            var mr = e.GetComponent<Editor.ECS.Components.Rendering.MeshRenderer>();
            if (mr != null) _colorSnapshot.Add((e, mr.ColorR, mr.ColorG, mr.ColorB, mr.ColorA));
            if (e.Children != null)
                foreach (var c in e.Children) SnapshotTransformRecursive(c);
        }

        private void EndPlaySimulation()
        {
            if (!_simActive) return;
            _simActive = false;
            AudioPlaybackService.Instance.EndPlay();
            ScriptRuntime.Instance.End();
            VortexAPI.ClearAllRigidbodies();
            _componentSnapshot?.Restore();   // after OnDestroy: lights scripts changed
            _componentSnapshot = null;
            foreach (var s in _transformSnapshot)
            {
                if (s.ent?.Transform == null) continue;
                s.ent.Transform.LocalPosition = s.pos;
                s.ent.Transform.LocalRotation = s.rot;
                s.ent.Transform.LocalScale = s.scale;
            }
            _transformSnapshot.Clear();
            _physicsEntities.Clear();
            foreach (var cs in _colorSnapshot) SceneRenderService.Instance.SetEntityColor(cs.ent, cs.r, cs.g, cs.b, cs.a);
            _colorSnapshot.Clear();
            if (_hasCamSnap)
            {
                _camera.SetFromEntityTransform(_snapCamX, _snapCamY, _snapCamZ, _snapCamPitch, _snapCamYaw, _snapCamRoll);
                _hasCamSnap = false;
            }
            _sceneDirty = true;
        }

        private void ApplyMainCameraView() => PlayCameraHelper.ApplyMainCamera(_currentScene ?? ProjectData.Current?.ActiveScene);
    }
}
