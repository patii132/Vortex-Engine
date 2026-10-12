using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Serialization;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using Editor.ECS;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;
using Editor.Editors.WorldEditor.Components.SceneHierarchy;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Every user-facing action of the editor in one place, so the menu bar, toolbar, context menus and keyboard
    /// shortcuts all run the same code path against the shared core.
    /// <para>macOS: the native menu consumes ⌘ key equivalents BEFORE the focused control sees them, so every edit
    /// command first routes to a focused text field (⌘C in a text box copies text, not entities) and then to a panel
    /// that registered its own edit handler (<see cref="RegisterEditHandler"/>), before acting on the scene.</para>
    /// </summary>
    public static class EditorCommands
    {
        public static MainWindow Window { get; set; }
        private static EditorSession Session => EditorSession.Instance;
        private static SceneHierarchyViewModel Vm => Session.Hierarchy;
        private static Scene ActiveScene => ProjectData.Current?.ActiveScene;
        private static GameEntity Selected => SelectionService.Instance.SelectedEntity;

        // ============================================================ focus-aware edit routing

        public enum EditAction { Undo, Redo, Cut, Copy, Paste, Delete, Duplicate, SelectAll, Rename }

        /// <summary>Menu / keyboard entry point for the Edit commands: a focused text field gets the text operation,
        /// a panel with its own handler (<see cref="RegisterEditHandler"/>) gets the command, otherwise the scene
        /// command runs. (Context menus and code call the scene commands directly.)</summary>
        /// <summary>Diagnostics for the smoke run: the last edit command and where it went.</summary>
        internal static string LastEditRoute;

        public static void EditCommand(EditAction c)
        {
            var f = FocusedElement();
            LastEditRoute = c + " focus=" + (f?.GetType().Name ?? "none");
            if (System.Environment.GetEnvironmentVariable("VORTEX_TRACE_EDIT") == "1")
                Console.WriteLine("EDITCMD " + c + " from " + string.Join(" <- ", new StackTrace(1, false).GetFrames().Take(6).Select(fr => fr.GetMethod()?.DeclaringType?.Name + "." + fr.GetMethod()?.Name)));
            if (RouteEdit(c)) { LastEditRoute += " -> routed"; return; }
            LastEditRoute += " -> scene";
            switch (c)
            {
                case EditAction.Undo: Undo(); break;
                case EditAction.Redo: Redo(); break;
                case EditAction.Cut: Cut(); break;
                case EditAction.Copy: Copy(); break;
                case EditAction.Paste: Paste(); break;
                case EditAction.Delete: Delete(); break;
                case EditAction.Duplicate: Duplicate(); break;
                case EditAction.SelectAll: SelectAll(); break;
                case EditAction.Rename: Rename(); break;
            }
        }

        private static readonly List<(WeakReference<Control> scope, Func<EditAction, bool> handler)> _editHandlers = new List<(WeakReference<Control>, Func<EditAction, bool>)>();

        /// <summary>A panel that owns its own Cut/Copy/Paste/Duplicate/Delete/Select All/Rename (e.g. the Asset
        /// Browser for files) registers here: while keyboard focus is inside <paramref name="scope"/>, the menu
        /// commands and shortcuts call <paramref name="handler"/> first; returning true consumes the command.</summary>
        public static void RegisterEditHandler(Control scope, Func<EditAction, bool> handler)
        {
            if (scope == null || handler == null) return;
            _editHandlers.RemoveAll(h => !h.scope.TryGetTarget(out var c) || ReferenceEquals(c, scope));
            _editHandlers.Add((new WeakReference<Control>(scope), handler));
        }

        /// <summary>The window that has keyboard focus (the menu bar acts on it), else the main window.</summary>
        public static Window ActiveWindow()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
                foreach (var w in d.Windows) if (w.IsActive) return w;
            return Window;
        }

        public static IInputElement FocusedElement()
        {
            try { return ActiveWindow()?.FocusManager?.GetFocusedElement(); } catch { return null; }
        }

        /// <summary>The text field that has keyboard focus (typing), or null.</summary>
        public static TextBox FocusedTextBox()
        {
            var f = FocusedElement();
            if (f is TextBox tb) return tb;
            return (f as Visual)?.FindAncestorOfType<TextBox>();
        }

        /// <summary>True when the keyboard is in a terminal (Claude Code in the sidebar): typing and Ctrl keys go to the program.</summary>
        public static bool TerminalFocused()
        {
            var f = FocusedElement() as Visual;
            return f is VortexEditor.Controls.TerminalView || f?.FindAncestorOfType<VortexEditor.Controls.TerminalView>() != null;
        }

        /// <summary>True when keyboard focus is inside <paramref name="scope"/>.</summary>
        public static bool FocusWithin(Visual scope)
        {
            var f = FocusedElement() as Visual;
            while (f != null) { if (ReferenceEquals(f, scope)) return true; f = f.GetVisualParent(); }
            return false;
        }

        private static bool RouteEdit(EditAction c)
        {
            var tb = FocusedTextBox();
            if (tb != null) { TextEdit(tb, c); return true; }
            var f = FocusedElement() as Visual;
            if (f != null)
            {
                foreach (var (scope, handler) in _editHandlers.ToArray())
                {
                    if (!scope.TryGetTarget(out var s) || !s.IsEffectivelyVisible) continue;
                    if (FocusWithin(s)) { try { if (handler(c)) return true; } catch (Exception ex) { Fail("Edit", ex); return true; } }
                }
            }
            return false;
        }

        private static void TextEdit(TextBox tb, EditAction c)
        {
            try
            {
                switch (c)
                {
                    case EditAction.Undo: if (tb.CanUndo) tb.Undo(); break;
                    case EditAction.Redo: if (tb.CanRedo) tb.Redo(); break;
                    case EditAction.Cut: tb.Cut(); break;
                    case EditAction.Copy: tb.Copy(); break;
                    case EditAction.Paste: tb.Paste(); break;
                    case EditAction.SelectAll: tb.SelectAll(); break;
                    case EditAction.Delete: DeleteToLineStart(tb); break;   // ⌘⌫ in a text field
                }
            }
            catch { }
        }

        private static void DeleteToLineStart(TextBox tb)
        {
            string text = tb.Text ?? "";
            int a = Math.Min(tb.SelectionStart, tb.SelectionEnd), b = Math.Max(tb.SelectionStart, tb.SelectionEnd);
            if (b <= a)
            {
                b = Math.Min(text.Length, tb.CaretIndex);
                a = b > 0 ? text.LastIndexOf('\n', b - 1) + 1 : 0;
            }
            if (b <= a) return;
            tb.Text = text.Remove(a, b - a);
            tb.CaretIndex = a;
        }

        // ============================================================ project
        public static void NewProject() => Window?.ShowProjectHub(createTab: true);
        public static void OpenProject() => Window?.ShowProjectHub(createTab: false);
        public static void SaveProject() { if (!Session.HasProject) return; try { Session.SaveProject(); } catch (Exception ex) { Fail("Save failed", ex); } }
        public static void SaveAll() { if (!Session.HasProject) return; try { Session.SaveAll(); } catch (Exception ex) { Fail("Save failed", ex); } }
        public static async Task CloseProject()
        {
            if (!Session.HasProject) return;
            if (await Dialogs.Confirm("Close project?", "Unsaved changes will be lost.", "Close", "Cancel", destructive: true))
            {
                Session.CloseProject();
                Window?.ShowProjectHub(createTab: false);
            }
        }
        public static void ProjectSettings() => Window?.OpenProjectSettings();
        public static async Task Exit()
        {
            if (Session.HasProject && !await Dialogs.Confirm("Quit Vortex Editor?", "Unsaved changes will be lost.", "Quit", "Cancel", destructive: true)) return;
            Window?.CloseConfirmed();
        }
        public static void RevealProject() { var p = ProjectData.Current?.Path; if (p != null) RevealInFinder(p); }

        // ============================================================ scenes
        public static void NewScene()
        {
            if (!Session.HasProject) { Toast("Open a project first"); return; }
            Vm.CreateSceneCommand.Execute(null);
            var sc = Vm.SelectedScene;
            if (sc != null) { Toast("Created " + sc.Name + " — right-click it to activate"); Window?.Hierarchy?.Reveal(sc); }
        }

        /// <summary>Load a .vscene file into the project and activate it (Hierarchy ▸ Load Scene… in the WPF editor).</summary>
        public static async Task OpenScene()
        {
            var project = ProjectData.Current; var top = ActiveWindow();
            if (project == null || top == null) { Toast("Open a project first"); return; }
            string dir = Path.Combine(project.Path, "Assets", "Scenes");
            IStorageFolder start = null;
            try { if (Directory.Exists(dir)) start = await top.StorageProvider.TryGetFolderFromPathAsync(dir); } catch { }
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Scene",
                AllowMultiple = false,
                SuggestedStartLocation = start,
                FileTypeFilter = new[] { new FilePickerFileType("Vortex Scene") { Patterns = new[] { "*.vscene" } } }
            });
            string path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (!string.IsNullOrEmpty(path)) LoadSceneFile(path);
        }

        public static Scene LoadSceneFile(string path)
        {
            var project = ProjectData.Current; if (project == null || string.IsNullOrEmpty(path)) return null;
            try
            {
                string full = Path.GetFullPath(path);
                var existing = project.Scenes.FirstOrDefault(s => s != null && !string.IsNullOrEmpty(s.FilePath) && string.Equals(Path.GetFullPath(s.FilePath), full, StringComparison.OrdinalIgnoreCase));
                if (existing != null) { Session.ActivateScene(existing); return existing; }
                var scene = SceneService.Instance.LoadScene(full);
                if (scene == null) { Toast("Could not load " + Path.GetFileName(path)); return null; }
                scene.Project = project;
                project.Scenes.Add(scene);
                Session.ActivateScene(scene);
                Toast("Scene loaded: " + scene.Name);
                return scene;
            }
            catch (Exception ex) { Fail("Load scene", ex); return null; }
        }

        public static void SaveScene(Scene scene)
        {
            scene = scene ?? ActiveScene; if (scene == null) return;
            try { SceneService.Instance.SaveScene(scene); Toast("Scene saved: " + scene.Name); }
            catch (Exception ex) { Fail("Save scene", ex); }
        }

        // ============================================================ build
        public static void Build() => Window?.OpenBuildDialog(runAfter: false);
        public static void BuildAndRun() => Window?.OpenBuildDialog(runAfter: true);

        // ============================================================ edit
        public static void Undo()
        {
            UndoRedoManager.Instance.Undo();
            AfterSceneEdit();
        }

        public static void Redo()
        {
            UndoRedoManager.Instance.Redo();
            AfterSceneEdit();
        }

        public static void Cut()
        {
            var list = TopLevelOnly(SelectedEntities()); if (list.Count == 0) return;
            EntityClipboardService.Instance.Cut(list);
            Toast(list.Count == 1 ? "Cut " + list[0].Name + " — paste to move it" : "Cut " + list.Count + " entities — paste to move them");
        }

        public static void Copy()
        {
            var list = TopLevelOnly(SelectedEntities()); if (list.Count == 0) return;
            EntityClipboardService.Instance.Copy(list);
            Toast(list.Count == 1 ? "Copied " + list[0].Name : "Copied " + list.Count + " entities");
        }

        /// <summary>Paste the entity clipboard into the active scene (top level). Returns the pasted entities.</summary>
        public static List<GameEntity> Paste()
        {
            var scene = ActiveScene;
            if (scene == null || !EntityClipboardService.Instance.HasContent) return new List<GameEntity>();
            // Top level on purpose: PasteEntitiesCommand's "paste under a parent" path adds through a nested undo
            // command that the undo manager drops while it is executing, so the child paste would silently vanish.
            var pasted = EntityClipboardService.Instance.Paste(scene, null);
            if (pasted != null && pasted.Count > 0) SelectMany(pasted);
            AfterSceneEdit();
            return pasted ?? new List<GameEntity>();
        }

        public static void Delete()
        {
            DeleteEntities(SelectedEntities());
        }

        public static void DeleteEntities(IList<GameEntity> entities)
        {
            var list = TopLevelOnly(entities); if (list.Count == 0) return;
            UndoRedoManager.Instance.Execute(new DeleteEntitiesCommand(list));
            Vm.ClearSelection();
            SelectionService.Instance.ClearSelection();
            AfterSceneEdit();
        }

        /// <summary>Duplicate the selection next to the originals (same parent, right after each source) — one undo step.</summary>
        public static List<GameEntity> Duplicate()
        {
            return DuplicateEntities(SelectedEntities());
        }

        public static List<GameEntity> DuplicateEntities(IList<GameEntity> entities)
        {
            var list = TopLevelOnly(entities); if (list.Count == 0) return new List<GameEntity>();
            var cmd = new DuplicateEntitiesCommand(list);
            UndoRedoManager.Instance.Execute(cmd);
            var copies = cmd.Copies.ToList();
            if (copies.Count > 0) SelectMany(copies);
            AfterSceneEdit();
            return copies;
        }

        public static void SelectAll()
        {
            SyncHierarchySelection();
            if (Vm.SelectedScene == null) Vm.SelectedScene = ActiveScene;
            Vm.SelectAllCommand.Execute(null);
            Window?.Hierarchy?.SelectEntities(Vm.SelectedEntities.ToList(), reveal: false);
        }

        public static void Rename()
        {
            var e = Selected; if (e == null) return;
            Window?.Hierarchy?.BeginRename(e);
        }

        public static void Find() => Window?.FocusHierarchySearch();

        /// <summary>Undoable entity rename.</summary>
        public static void RenameEntity(GameEntity e, string newName)
        {
            if (e == null || string.IsNullOrWhiteSpace(newName)) return;
            string old = e.Name, n = newName.Trim();
            if (old == n) return;
            UndoRedoManager.Instance.ExecuteAction("Rename " + old + " → " + n, () => e.Name = n, () => e.Name = old);
        }

        /// <summary>Undoable scene rename.</summary>
        public static void RenameScene(Scene s, string newName)
        {
            if (s == null || string.IsNullOrWhiteSpace(newName)) return;
            string old = s.Name, n = newName.Trim();
            if (old == n) return;
            UndoRedoManager.Instance.ExecuteAction("Rename Scene " + old + " → " + n, () => { s.Name = n; s.IsDirty = true; }, () => { s.Name = old; s.IsDirty = true; });
        }

        /// <summary>The entities the edit commands act on: the hierarchy multi-selection (kept in step with the
        /// primary selection that the viewport / inspector use).</summary>
        public static List<GameEntity> SelectedEntities()
        {
            SyncHierarchySelection();
            var list = Vm.SelectedEntities?.Where(e => e != null).Distinct().ToList() ?? new List<GameEntity>();
            if (list.Count == 0 && Selected != null) list.Add(Selected);
            return list;
        }

        /// <summary>Keep the hierarchy view model's multi-selection consistent with the primary selection: a click
        /// in the viewport replaces it, a cleared selection clears it (so ⌘⌫ never deletes a stale selection).</summary>
        public static void SyncHierarchySelection()
        {
            var vm = Vm; var sel = Selected;
            if (sel == null)
            {
                if (vm.SelectedEntities != null && vm.SelectedEntities.Count > 0) vm.ClearSelection();
                return;
            }
            if (sel.Scene != null && !ReferenceEquals(vm.SelectedScene, sel.Scene)) vm.SelectedScene = sel.Scene;
            if (vm.SelectedEntities == null || !vm.SelectedEntities.Contains(sel)) vm.SetSelection(sel);
            else if (!ReferenceEquals(vm.SelectedEntity, sel)) vm.SelectedEntity = sel;
        }

        /// <summary>Select several entities (hierarchy multi-selection + primary selection = the last one).</summary>
        public static void SelectMany(IList<GameEntity> entities)
        {
            if (entities == null || entities.Count == 0) return;
            var vm = Vm;
            vm.SetSelection(entities[0]);
            for (int i = 1; i < entities.Count; i++) vm.AddToSelection(entities[i]);
            SelectionService.Instance.Select(entities[entities.Count - 1]);
            Window?.Hierarchy?.SelectEntities(entities);
        }

        /// <summary>Drop entities whose ancestor is also in the list (operating on the ancestor covers them).</summary>
        public static List<GameEntity> TopLevelOnly(IEnumerable<GameEntity> entities)
        {
            var set = new HashSet<GameEntity>(entities.Where(e => e != null));
            return set.Where(e => { for (var p = e.Parent; p != null; p = p.Parent) if (set.Contains(p)) return false; return true; }).ToList();
        }

        internal static void AfterSceneEdit()
        {
            SceneRenderService.RuntimeDirty = true;
            Editor.Core.Viewport.EditorViewportSession.RequestResubmit();
            Window?.Inspector?.Refresh();
        }

        // ============================================================ view
        public static void ToggleGrid() => EditorViewportService.Instance.ToggleGrid();
        public static void ToggleSnap() => SetSnap(!EditorViewportService.Instance.SnapToGrid);
        public static void SetSnap(bool on) { EditorViewportService.Instance.SnapToGrid = on; TransformGizmoService.Instance.SnapEnabled = on; }
        public static void SetSnapSize(float size) { if (size > 0) { EditorViewportService.Instance.GridSpacing = size; TransformGizmoService.Instance.SnapTranslate = size; } }
        public static void ToggleGizmos() => EditorViewportService.Instance.ToggleGizmos();
        public static void ToggleColliders() { EditorViewportService.Instance.ToggleColliders(); SceneRenderService.RuntimeDirty = true; }
        public static void ToggleAllColliders() { EditorViewportService.Instance.ShowAllColliders = !EditorViewportService.Instance.ShowAllColliders; SceneRenderService.RuntimeDirty = true; }
        /// <summary>Physics v2 (#106): draw the live Jolt shapes (cyan wire lines) over the play view.</summary>
        public static void TogglePhysicsDebug()
            => Editor.Core.Services.Physics.PhysicsService.ShowPhysicsDebug = !Editor.Core.Services.Physics.PhysicsService.ShowPhysicsDebug;
        /// <summary>#106: switch one physics debug layer; switching one on turns the debug draw on as well.</summary>
        public static void TogglePhysicsDebugLayer(Editor.Core.Services.Physics.PhysicsService.DebugLayer layer)
        {
            var svc = Editor.Core.Services.Physics.PhysicsService.DebugLayers;
            svc ^= layer;
            Editor.Core.Services.Physics.PhysicsService.DebugLayers = svc;
            if ((svc & layer) != 0) Editor.Core.Services.Physics.PhysicsService.ShowPhysicsDebug = true;
        }
        public static void FocusSelected() => Editor.Core.Viewport.EditorViewportSession.Main?.FocusOnSelected();
        public static void ResetCamera() => Editor.Core.Viewport.EditorViewportSession.Main?.Camera.Reset();
        public static void ToggleReleaseMode() => PlayModeService.Instance.IsReleaseMode = !PlayModeService.Instance.IsReleaseMode;
        public static void MoveTool() => TransformGizmoService.Instance.SetTranslateMode();
        public static void RotateTool() => TransformGizmoService.Instance.SetRotateMode();
        public static void ScaleTool() => TransformGizmoService.Instance.SetScaleMode();
        public static void ToggleGizmoSpace() => TransformGizmoService.Instance.ToggleSpace();
        public static void SetLayout(int layout) => Window?.ViewportPanel?.SetLayout(layout);

        // ============================================================ assets
        public static async Task ImportAsset()
        {
            var top = ActiveWindow();
            if (top == null || !Session.HasProject) { Toast("Open a project first"); return; }
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Asset",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("All supported") { Patterns = new[] { "*.fbx", "*.obj", "*.gltf", "*.glb", "*.dae", "*.3ds", "*.blend", "*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp", "*.hdr", "*.wav", "*.mp3", "*.ogg", "*.flac" } },
                    new FilePickerFileType("3D models") { Patterns = new[] { "*.fbx", "*.obj", "*.gltf", "*.glb", "*.dae", "*.3ds", "*.blend" } },
                    new FilePickerFileType("Textures") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp", "*.hdr" } },
                    new FilePickerFileType("Audio") { Patterns = new[] { "*.wav", "*.mp3", "*.ogg", "*.flac" } },
                }
            });
            foreach (var f in files)
            {
                string path = f.TryGetLocalPath();
                if (string.IsNullOrEmpty(path)) continue;
                Window?.AssetBrowser?.ImportFile(path);
            }
        }

        /// <summary>Window ▸ Import Assets…: the batch import dialog for files picked from disk.</summary>
        public static async Task ImportAssetsDialog()
        {
            var top = ActiveWindow();
            if (top == null || !Session.HasProject) { Toast("Open a project first"); return; }
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import Assets", AllowMultiple = true });
            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).ToArray();
            if (paths.Length == 0) return;
            var imported = await EditorWindows.ImportAssets(paths, null);   // null = each file type's default folder
            if (imported != null && imported.Length > 0) { Window?.AssetBrowser?.Refresh(); Toast("Imported " + imported.Length + " asset(s)"); }
        }

        public static void CreateMaterial() => Window?.AssetBrowser?.CreateMaterial("Standard");
        public static void CreateShader() => Window?.AssetBrowser?.CreateShader("Standard");
        public static void CreateScript()
        {
            if (!Session.HasProject) { Toast("Open a project first"); return; }
            try
            {
                var path = ScriptingService.CreateScript("NewBehaviour");
                Window?.AssetBrowser?.Refresh();
                OpenInIde(path);
                Toast("Script created: " + Path.GetFileName(path));
            }
            catch (Exception ex) { Fail("Create script", ex); }
        }

        /// <summary>Create a new asset through the shared core (UI screen, animation clip, sound container, empty
        /// prefab) in its default folder with a unique name, then reveal it in the Asset Browser.</summary>
        public static string CreateAsset(string kind)
        {
            var root = ProjectData.Current?.Path; if (root == null) { Toast("Open a project first"); return null; }
            try
            {
                string path;
                switch (kind)
                {
                    case "ui": path = Editor.Core.Assets.AssetActions.CreateUiScreen(UniquePath(Path.Combine(root, "Assets", "UI"), "NewScreen", ".vui")); break;
                    case "anim": path = Editor.Core.Assets.AssetActions.CreateAnimationClip(UniquePath(Path.Combine(root, "Assets", "Animations"), "NewClip", ".vanim")); break;
                    case "sound": path = Editor.Core.Assets.AssetActions.CreateSoundContainer(); break;
                    case "prefab": path = Editor.Core.Assets.AssetActions.CreateEmptyPrefab(); break;
                    default: return null;
                }
                Window?.AssetBrowser?.Refresh();
                try { Window?.AssetBrowser?.SelectPath(path); } catch { }
                Toast("Created " + Path.GetFileName(path));
                return path;
            }
            catch (Exception ex) { Fail("Create asset", ex); return null; }
        }

        public static string UniquePath(string dir, string name, string ext)
        {
            string p = Path.Combine(dir, name + ext); int n = 1;
            while (File.Exists(p)) p = Path.Combine(dir, name + (++n) + ext);
            return p;
        }

        public static void OpenScriptsProject()
        {
            if (!Session.HasProject) { Toast("Open a project first"); return; }
            try { ScriptingService.EnsureScriptsProject(); } catch { }
            OpenInIde(null);
        }
        public static void ExportAsset() => Window?.AssetBrowser?.ExportSelected();
        public static void RefreshAssets() { try { Editor.Core.Assets.AssetDatabase.Instance.Refresh(); } catch { } Window?.AssetBrowser?.Refresh(); Window?.FileTree?.Reload(); }
        public static void ReloadShaders()
        {
            try
            {
                int n = Editor.DllWrapper.VortexAPI.ReloadMaterialShaders();
                Toast(n == 0 ? "No shader changes" : n == 1 ? "1 shader reloaded" : n + " shaders reloaded");
            }
            catch (Exception ex) { Fail("Reload shaders", ex); }
        }

        /// <summary>Open a script (or the scripts project) in the user's IDE: VS Code, Rider, Visual Studio (Windows),
        /// else the OS default handler. Works when started from Finder (no shell PATH).</summary>
        public static void OpenInIde(string filePath) => OpenInIde(filePath, 0);

        /// <summary>Open a file in the IDE at a line (VS Code jumps to it; others open the file).</summary>
        public static void OpenInIde(string filePath, int line)
        {
            string root = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(root)) return;
            try
            {
                if (OperatingSystem.IsWindows()) { ScriptingService.OpenInVisualStudio(filePath); return; }
                try { ScriptingService.EnsureScriptsProject(); } catch { }
                string target = filePath ?? root;
                string pref = EditorPreferences.Current.ScriptIde ?? "";
                if (pref != "rider")
                {
                    foreach (var exe in new[] { "/usr/local/bin/code", "/opt/homebrew/bin/code", "/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications/Visual Studio Code.app/Contents/Resources/app/bin/code"), "/usr/bin/code" })
                    {
                        if (!File.Exists(exe)) continue;
                        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
                        psi.ArgumentList.Add(root);
                        if (filePath != null) { psi.ArgumentList.Add("--goto"); psi.ArgumentList.Add(line > 0 ? filePath + ":" + line : filePath); }
                        if (Process.Start(psi) != null) return;
                    }
                }
                foreach (var app in pref == "rider" ? new[] { "Rider", "Visual Studio Code" } : new[] { "Visual Studio Code", "Rider", "Visual Studio" })
                {
                    if (!Directory.Exists("/Applications/" + app + ".app") && !Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", app + ".app"))) continue;
                    var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                    psi.ArgumentList.Add("-a"); psi.ArgumentList.Add(app); psi.ArgumentList.Add(target);
                    if (Process.Start(psi) != null) return;
                }
                var fallback = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open") { UseShellExecute = false };
                fallback.ArgumentList.Add(target);
                Process.Start(fallback);
            }
            catch (Exception ex) { Fail("Could not open the IDE", ex); }
        }

        // ============================================================ game objects
        private static Scene TargetScene(Scene scene) => scene ?? ActiveScene;
        private static void PrepareVm(Scene scene) { var s = TargetScene(scene); if (s != null && !ReferenceEquals(Vm.SelectedScene, s)) Vm.SelectedScene = s; }

        public static GameEntity CreateEmpty(Scene scene = null) => RunCreate(() => TargetScene(scene)?.CreateEntity("New Entity"));
        public static GameEntity CreatePrimitive(PrimitiveType type, Scene scene = null) => RunCreate(() => TargetScene(scene)?.CreatePrimitive(type));
        public static GameEntity CreateLight(LightType type, Scene scene = null) => RunCreate(() => TargetScene(scene)?.CreateLight(type));
        public static GameEntity CreateCamera(Scene scene = null) => RunCreate(() => TargetScene(scene)?.CreateCamera());
        public static GameEntity CreateSkybox(Scene scene = null) => RunCreate(() => TargetScene(scene)?.CreateSkybox());
        public static GameEntity CreatePlayer(Scene scene = null) => RunVm(scene, Vm.CreatePlayerCommand);
        public static GameEntity CreateFolder(Scene scene = null) => RunVm(scene, Vm.CreateFolderCommand);
        public static GameEntity CreateAudioSource(Scene scene = null) => RunVm(scene, Vm.CreateAudioSourceCommand);
        public static GameEntity CreateReverbZone(Scene scene = null) => RunVm(scene, Vm.CreateReverbZoneCommand);
        public static GameEntity CreateDecal(Scene scene = null) => RunVm(scene, Vm.CreateDecalCommand);
        public static GameEntity CreateTerrain(Scene scene = null) => RunVm(scene, Vm.CreateTerrainCommand);

        /// <summary>Create ▸ Terrain from Heightmap…: a terrain shaped by a picture (PNG 8 / 16 bit or RAW 16) — size and the
        /// height range come from one prompt ("512, 0, 60" = 512 m across, black at 0 m, white at 60 m); the resolution follows
        /// the picture (one sample per pixel up to 2049). The terrain's corner lands so the picture is centred in front of the camera.</summary>
        public static async Task CreateTerrainFromHeightmap()
        {
            var top = ActiveWindow();
            var scene = ActiveScene;
            if (top == null || scene == null) { Toast("Open a project first"); return; }
            try
            {
                var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Terrain from heightmap",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType("Heightmap (PNG, RAW 16)") { Patterns = new[] { "*.png", "*.raw", "*.r16" } } }
                });
                string path = files != null && files.Count > 0 ? files[0].TryGetLocalPath() : null;
                if (string.IsNullOrEmpty(path)) return;
                var img = Editor.Core.Terrain.HeightmapIO.Load(path);
                int res = Terrain.SnapResolution(Math.Max(img.Width, img.Height));
                string answer = await Dialogs.Prompt("Terrain from heightmap", Path.GetFileName(path) + " (" + img.Width + " × " + img.Height + ", " + img.BitDepth + " bit) → " + res + " samples per side.\nSize in metres, lowest height, highest height:", (res - 1).ToString() + ", 0, 60", "Create", top);
                if (string.IsNullOrWhiteSpace(answer)) return;
                var parts = answer.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                float size = res - 1, low = 0f, high = 60f;
                if (parts.Length > 0 && !PropertyRowsTryParse(parts[0], out size)) { Toast("Size: a number of metres"); return; }
                if (parts.Length > 1) PropertyRowsTryParse(parts[1], out low);
                if (parts.Length > 2) PropertyRowsTryParse(parts[2], out high);
                size = Math.Max(1f, Math.Min(16384f, size));
                if (high - low < 0.001f) high = low + 1f;
                // centred ahead of the editor camera, on the ground height 0
                var cam = Editor.Core.Viewport.EditorViewportSession.Main?.Camera;
                float cx = 0f, cz = 0f;
                if (cam != null)
                {
                    double yr = cam.Yaw * Math.PI / 180.0;
                    cx = cam.PositionX + (float)Math.Sin(yr) * size * 0.5f; cz = cam.PositionZ + (float)Math.Cos(yr) * size * 0.5f;
                }
                var e = new GameEntity(scene, Path.GetFileNameWithoutExtension(path));
                var t = new Terrain(e) { Size = size, Resolution = res };
                e.AddComponent(t);
                if (e.Transform != null) e.Transform.LocalPosition = new Editor.ECS.Vector3(cx - size * 0.5f, 0f, cz - size * 0.5f);
                scene.AddEntity(e);
                Editor.Core.Terrain.TerrainData data; float cell;
                if (Editor.Core.Services.Terrain.TerrainService.TryGetData(e, out data, out cell))
                {
                    Editor.Core.Terrain.HeightmapIO.Apply(data, img, low, high);
                    Editor.Core.Services.Terrain.TerrainService.MarkDirty(e, new Editor.Core.Terrain.SampleRect { X0 = 0, Z0 = 0, X1 = res - 1, Z1 = res - 1 }, true, false);
                    Editor.Core.Services.Terrain.TerrainService.Save(e);
                }
                SelectionService.Instance.SelectedEntity = e;
                SelectionService.Instance.RequestFocus(e);
                Editor.Core.Viewport.EditorViewportSession.RequestResubmit();
                Toast("Terrain \"" + e.Name + "\" from " + Path.GetFileName(path) + ": " + size.ToString("0") + " m, " + res + "² samples, " + low.ToString("0.#") + " … " + high.ToString("0.#") + " m");
            }
            catch (Exception ex) { Fail("Terrain from heightmap", ex); }
        }

        private static bool PropertyRowsTryParse(string s, out float v) => float.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
        public static GameEntity CreateFoliage(Scene scene = null) => RunVm(scene, Vm.CreateFoliageCommand);
        public static GameEntity CreateWater(Scene scene = null) => RunVm(scene, Vm.CreateWaterCommand);
        public static GameEntity CreateUI(string kind, Scene scene = null)
        {
            switch (kind)
            {
                case "Canvas": return RunVm(scene, Vm.CreateUICanvasCommand);
                case "Text": return RunVm(scene, Vm.CreateUITextCommand);
                case "Image": return RunVm(scene, Vm.CreateUIImageCommand);
                default: return RunVm(scene, Vm.CreateUIButtonCommand);
            }
        }

        /// <summary>New empty child under <paramref name="parent"/> (default: the selection).</summary>
        public static GameEntity CreateChild(GameEntity parent = null)
        {
            parent = parent ?? Selected;
            if (parent == null) { Toast("Select an entity first"); return null; }
            return RunCreate(() =>
            {
                PrepareVm(parent.Scene);
                Vm.SelectedEntity = parent;
                Vm.CreateChildEntityCommand.Execute(null);
                return Vm.SelectedEntity;
            });
        }

        private static GameEntity RunVm(Scene scene, System.Windows.Input.ICommand cmd)
            => RunCreate(() => { PrepareVm(scene); cmd.Execute(null); return Vm.SelectedEntity; });

        private static GameEntity RunCreate(Func<GameEntity> make)
        {
            if (!Session.HasProject) { Toast("Open a project first"); return null; }
            try
            {
                var e = make();
                if (e != null) Focus(e);
                return e;
            }
            catch (Exception ex) { Fail("Action failed", ex); return null; }
        }

        private static void Focus(GameEntity e)
        {
            if (e == null) return;
            Vm.SetSelection(e);
            SelectionService.Instance.Select(e);
            SelectionService.Instance.RequestFocus(e);
            AfterSceneEdit();
        }

        /// <summary>Hierarchy / GameObject ▸ Instantiate Prefab…: pick a .ventity and add a linked instance (the active
        /// scene through PrefabWorkflow.PlaceInScene; another scene / under a parent through PrefabService).</summary>
        public static async Task InstantiatePrefab(Scene scene = null, GameEntity parent = null)
        {
            var sc = TargetScene(scene); if (sc == null) { Toast("Open a project first"); return; }
            string rel = await AssetPickerDialog.Pick("Prefab", new[] { "*.ventity", "*.vprefab" });
            if (string.IsNullOrEmpty(rel)) return;
            try
            {
                string full = Path.Combine(ProjectData.Current.Path, rel);
                GameEntity ent = ReferenceEquals(sc, ActiveScene) && (parent == null || ReferenceEquals(parent.Scene, sc))
                    ? Prefab.PrefabWorkflow.PlaceInScene(full, parent)
                    : PrefabService.Instance.InstantiatePrefab(full, sc, parent);
                if (ent != null) Focus(ent); else Toast("Could not instantiate the prefab (empty or unreadable)");
            }
            catch (Exception ex) { Fail("Instantiate prefab", ex); }
        }

        /// <summary>Save as Prefab / Create Prefab from Selection: one entity goes through the interactive prefab
        /// workflow (name prompt, replace confirmation, workflow help); several are saved under their own names after
        /// one confirmation. Each source becomes a linked instance. <paramref name="ask"/> false = no dialogs (smoke / API).</summary>
        public static async Task<List<string>> CreatePrefabFromSelection(IList<GameEntity> entities = null, bool ask = true)
        {
            var result = new List<string>();
            var list = TopLevelOnly(entities ?? SelectedEntities());
            if (list.Count == 0 || ProjectData.Current == null) { Toast("Select an entity first"); return result; }
            if (ask && list.Count == 1)
            {
                var p = await Prefab.PrefabWorkflow.SaveAsPrefabInteractive(list[0], ActiveWindow());
                if (p != null) result.Add(p);
                return result;
            }
            if (ask && !await Dialogs.Confirm("Create " + list.Count + " prefabs?", "Each selected entity is saved to Assets/Prefabs under its own name and becomes a linked instance. Existing prefabs with the same names are replaced.", "Create", "Cancel"))
                return result;
            foreach (var e in list)
            {
                try { var path = Prefab.PrefabWorkflow.SaveAsPrefab(e); if (path != null) result.Add(path); }
                catch (Exception ex) { Fail("Save prefab", ex); }
            }
            if (result.Count > 0) Toast(result.Count == 1 ? "Prefab saved — '" + Path.GetFileNameWithoutExtension(result[0]) + "' is now a linked instance" : result.Count + " prefabs saved");
            return result;
        }

        private static GameEntity PrefabRoot(GameEntity e)
        {
            var root = Prefab.PrefabWorkflow.FindInstanceRoot(e ?? Selected);
            if (root == null) Toast("Select a prefab instance first");
            return root;
        }

        /// <summary>Apply Overrides: push the instance's changes into its prefab (asks first, updates every instance).</summary>
        public static async Task ApplyToPrefab(GameEntity e = null)
        {
            e = e ?? Selected; if (PrefabRoot(e) == null) return;
            try { await Prefab.PrefabWorkflow.ApplyInteractive(e, ActiveWindow()); AfterSceneEdit(); }
            catch (Exception ex) { Fail("Apply to prefab", ex); }
        }

        /// <summary>Revert Overrides: reload the instance from its prefab (keeps its transform).</summary>
        public static async Task RevertToPrefab(GameEntity e = null)
        {
            e = e ?? Selected; if (PrefabRoot(e) == null) return;
            try { var r = await Prefab.PrefabWorkflow.RevertInteractive(e, ActiveWindow()); if (r != null) Focus(r); }
            catch (Exception ex) { Fail("Revert instance", ex); }
        }

        /// <summary>Unpack Prefab (completely = nested instances too): the entity becomes a plain one (undoable).</summary>
        public static void UnpackPrefab(GameEntity e = null, bool completely = false)
        {
            e = e ?? Selected; var root = PrefabRoot(e); if (root == null) return;
            Prefab.PrefabWorkflow.Unpack(e, completely);
            Toast((completely ? "Unpacked completely: " : "Unpacked: ") + root.Name);
        }

        public static void OpenPrefab(GameEntity e = null) { var root = PrefabRoot(e); if (root != null) Prefab.PrefabWorkflow.OpenInEditor(root.PrefabPath); }
        public static void SelectPrefabAsset(GameEntity e = null) { var root = PrefabRoot(e); if (root != null) Prefab.PrefabWorkflow.SelectAsset(root.PrefabPath); }

        public static void ToggleActive(GameEntity e = null)
        {
            e = e ?? Selected; if (e == null) return;
            bool old = e.IsActive;
            UndoRedoManager.Instance.ExecuteAction((old ? "Deactivate " : "Activate ") + e.Name, () => e.IsActive = !old, () => e.IsActive = old);
            AfterSceneEdit();
        }

        /// <summary>Eye toggle: hide/show in the editor viewport only (session state, never saved, inert in play).</summary>
        public static void ToggleHiddenInEditor(GameEntity e = null)
        {
            e = e ?? Selected; if (e == null) return;
            e.IsHiddenInEditor = !e.IsHiddenInEditor;
            AfterSceneEdit();
        }

        public static void LookThroughCamera(GameEntity e = null)
        {
            e = e ?? Selected;
            if (e?.GetComponent<Camera>() == null) { Toast("Select a camera first"); return; }
            Editor.Core.Viewport.EditorViewportSession.Main?.ViewThroughCamera(e);
        }

        public static void ShowCameraPreview(GameEntity e = null)
        {
            e = e ?? Selected;
            if (e?.GetComponent<Camera>() == null) { Toast("Select a camera first"); return; }
            CameraPreviewService.Instance.ShowPreview(e);
        }

        // ============================================================ components
        public static void AddComponent(Editor.ECS.Component component)
        {
            var e = Selected; if (e == null || component == null) return;
            e.AddComponent(component);
            AfterSceneEdit();
        }

        /// <summary>Add a component (made per entity) to every selected entity.</summary>
        public static void AddComponentToSelection(Func<GameEntity, Editor.ECS.Component> make, string what = null)
        {
            var list = SelectedEntities();
            if (list.Count == 0) { Toast("Select an entity first"); return; }
            try
            {
                foreach (var e in list) e.AddComponent(make(e));
                if (what != null) Toast(what + " added" + (list.Count > 1 ? " to " + list.Count + " entities" : ""));
            }
            catch (Exception ex) { Fail("Add component", ex); }
            AfterSceneEdit();
        }

        public static void AddNewScript()
        {
            var e = Selected; if (e == null) { Toast("Select an entity first"); return; }
            try
            {
                var p = ScriptingService.CreateScript("NewBehaviour");
                e.AddComponent(new Editor.ECS.Components.Scripting.Script(e, ScriptingService.MakeRelative(ProjectData.Current?.Path ?? "", p)));
                OpenInIde(p);
                AfterSceneEdit();
            }
            catch (Exception ex) { Fail("Create script", ex); }
        }

        // ============================================================ play
        public static void Play() => Session.Play();
        public static void Pause()
        {
            var pms = PlayModeService.Instance;
            if (pms.State == PlayState.Playing) pms.Pause(); else if (pms.State == PlayState.Paused) pms.Resume();
        }
        public static void Stop() => Session.Stop();
        public static void TogglePlay() { if (!Session.HasProject) { Toast("Open a project first"); return; } Session.TogglePlay(); }
        public static void PlayInNewWindow() => Window?.LaunchStandalonePlayer();

        // ============================================================ windows / help
        public static void AudioMixer() => EditorWindows.AudioMixer();
        public static void GitWindow() { if (Session.HasProject) EditorWindows.Git(); else Toast("Open a project first"); }
        public static void History() => EditorWindows.History();
        public static void ResetLayout() => Window?.ResetLayout();
        public static void Documentation() => OpenUrl("https://engine.vortexstudio.dev/docs/");
        public static void ApiReference() => OpenUrl("https://engine.vortexstudio.dev/docs/#/api-index");
        public static void About() => Window?.OpenAbout();
        public static void ReleaseNotes() => OpenUrl("https://github.com/" + Editor.Core.EngineInfo.RepoOwner + "/" + Editor.Core.EngineInfo.RepoName + "/releases");

        /// <summary>Help ▸ Check for Updates: an installed Windows build downloads and installs the new version
        /// (UpdateWindow); elsewhere the release page is opened for the download.</summary>
        public static async Task CheckForUpdates()
        {
            if (OperatingSystem.IsWindows() && Editor.Core.Services.Update.UpdateService.IsInstalledBuild())
            {
                var info = await Editor.Core.Services.Update.UpdateService.CheckAsync();
                if (info != null && info.Bump != Editor.Core.Services.Update.BumpType.None) { UpdateWindow.Show(info, installNow: false); return; }
            }
            var r = await LatestRelease();
            if (r.tag == null) { if (await Dialogs.Confirm("Update check failed", "Could not reach GitHub. Open the release page instead?", "Open", "Cancel")) ReleaseNotes(); return; }
            if (IsNewer(r.tag, Editor.Core.EngineInfo.VersionString))
            {
                if (await Dialogs.Confirm("Update available: " + r.tag, "You are running Vortex " + Editor.Core.EngineInfo.VersionString + ". Open the release page to download the new version?", "Open Release", "Later"))
                    OpenUrl(r.url ?? "https://github.com/" + Editor.Core.EngineInfo.RepoOwner + "/" + Editor.Core.EngineInfo.RepoName + "/releases");
            }
            else await Dialogs.Alert("You're up to date", "Vortex " + Editor.Core.EngineInfo.VersionString + " is the latest version (" + r.tag + ").");
        }

        internal static async Task<(string tag, string url)> LatestRelease()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("VortexEditor/" + Editor.Core.EngineInfo.VersionString);
                var json = await http.GetStringAsync("https://api.github.com/repos/" + Editor.Core.EngineInfo.RepoOwner + "/" + Editor.Core.EngineInfo.RepoName + "/releases/latest");
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                string tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                string url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;
                return (tag, url);
            }
            catch { return (null, null); }
        }

        internal static bool IsNewer(string tag, string current)
        {
            Version Parse(string s) { s = (s ?? "").TrimStart('v', 'V'); int dash = s.IndexOfAny(new[] { '-', '+', ' ' }); if (dash > 0) s = s.Substring(0, dash); return Version.TryParse(s, out var v) ? v : null; }
            var a = Parse(tag); var b = Parse(current);
            return a != null && b != null && a > b;
        }

        public static Task KeyboardShortcuts()
        {
            string m = Keys.Cmd;
            string sh = Keys.Shift;
            var sb = new System.Text.StringBuilder();
            void Head(string h) { if (sb.Length > 0) sb.Append('\n'); sb.Append(h).Append('\n'); }
            void K(string keys, string what) => sb.Append("  ").Append(keys.PadRight(22)).Append(what).Append('\n');
            Head("PROJECT & SCENES");
            K(m + "S", "Save scene + project");
            K(sh + m + "S", "Save all scenes");
            K(m + "O", "Open project…");
            K(sh + m + "O", "Open scene…");
            K(m + "N", "New scene");
            K(m + ",", "Project settings");
            K(m + "B  /  " + m + "R", "Build…  /  Build and run");
            Head("EDIT");
            K(m + "Z  /  " + sh + m + "Z", "Undo / Redo   (also " + m + "Y)");
            K(m + "X  " + m + "C  " + m + "V", "Cut / Copy / Paste entities");
            K("", "(in a text field: the text)");
            K(m + "D", "Duplicate");
            K(Keys.Delete + "  /  " + m + Keys.Delete, "Delete");
            K(m + "A", "Select all");
            K(m + "F", "Find entity (hierarchy search)");
            K("F2  /  ↩", "Rename (hierarchy)");
            K("Esc", "Clear selection");
            Head("SCENE VIEW");
            K("W  E  R", "Move / Rotate / Scale tool");
            K("X", "Local / World space");
            K("F", "Frame the selection");
            K("G", "Grid on / off");
            K("Home", "Reset the camera");
            K("Right mouse + WASD", "Fly  (Q / E down / up, Shift = faster)");
            K("Mouse wheel", "Dolly in / out");
            Head("PLAY");
            K(m + "P", "Play / Stop");
            K(sh + m + "P", "Pause / Resume");
            Head("WINDOWS");
            K(m + "1 … " + m + "6", "Hierarchy, Files, Inspector,");
            K("", "Project, Console, Environment");
            K(sh + m + "H", "History (undo list)");
            K(sh + m + "N", "Create empty entity");
            return Dialogs.ShowText("Keyboard Shortcuts", sb.ToString().TrimEnd(), 600, 600);
        }

        public static void OpenUrl(string url)
        {
            try
            {
                if (OperatingSystem.IsMacOS()) { var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false }; psi.ArgumentList.Add(url); Process.Start(psi); }
                else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

        public static void RevealInFinder(string path)
        {
            try
            {
                if (OperatingSystem.IsMacOS())
                {
                    var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                    if (File.Exists(path)) psi.ArgumentList.Add("-R");
                    psi.ArgumentList.Add(path);
                    Process.Start(psi);
                }
                else if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", File.Exists(path) ? "/select,\"" + path + "\"" : "\"" + path + "\"");
                else Process.Start("xdg-open", "\"" + (File.Exists(path) ? Path.GetDirectoryName(path) : path) + "\"");
            }
            catch { }
        }

        // ============================================================ helpers
        public static void Toast(string message) => Window?.ShowToast(message);

        // ============================================================ asset library / store tabs

        /// <summary>Bring the Library tab to the front, optionally searching for <paramref name="search"/>.</summary>
        public static void ShowLibrary(string search = null)
        {
            Window?.ShowPanel(MainWindow.PanelLibrary);
            if (search != null) Panels.LibraryPanel.Current?.SetSearch(search);
        }

        /// <summary>Bring the Asset Store tab to the front, optionally on one source ("polyhaven", "sketchfab",
        /// "soundstudio" …).</summary>
        public static void ShowStore(string providerId = null)
        {
            // pick the source first: showing the tab would otherwise open the first source (and search it) on the way
            if (providerId != null) Panels.StorePanel.Current?.View.SelectProvider(providerId);
            Window?.ShowPanel(MainWindow.PanelStore);
        }

        /// <summary>Show a project file in the Project tab (selected in its folder).</summary>
        public static void RevealInProject(string fullPath)
        {
            Window?.ShowPanel(MainWindow.PanelProject);
            Panels.AssetBrowserPanel.Current?.SetTab("Explorer");
            Panels.AssetBrowserPanel.Current?.Reveal(fullPath);
        }
        public static void Fail(string what, Exception ex)
        {
            ConsoleService.Instance.LogError(what + ": " + ex.Message);
            Toast(what + ": " + ex.Message);
        }
    }

    /// <summary>Several commands as ONE undo step, each built right before it runs — so commands that capture state
    /// in their constructor (old parent / index, e.g. MoveEntityCommand) see the state left by the previous one.
    /// Undo runs in reverse; redo replays the executed commands.</summary>
    internal sealed class SequenceCommand : UndoableCommandBase
    {
        private readonly string _name;
        private readonly List<Func<IUndoableCommand>> _factories;
        private readonly List<IUndoableCommand> _done = new List<IUndoableCommand>();
        private bool _executed;

        public SequenceCommand(string name, IEnumerable<Func<IUndoableCommand>> factories) { _name = name; _factories = factories.ToList(); }
        public override string Name => _name;
        public int Count => _done.Count;

        public override void Execute()
        {
            if (_executed) { foreach (var c in _done) c.Redo(); return; }
            _executed = true;
            foreach (var f in _factories)
            {
                var c = f();
                if (c == null) continue;
                c.Execute();
                _done.Add(c);
            }
        }

        public override void Undo() { for (int i = _done.Count - 1; i >= 0; i--) _done[i].Undo(); }
        public override void Redo() { foreach (var c in _done) c.Redo(); }
    }

    /// <summary>Duplicate entities as siblings right after their sources (deep copy, fresh ids) — one undo step.
    /// Redo re-creates the copies.</summary>
    internal sealed class DuplicateEntitiesCommand : UndoableCommandBase
    {
        private readonly List<GameEntity> _sources;
        private readonly List<GameEntity> _copies = new List<GameEntity>();
        public IReadOnlyList<GameEntity> Copies => _copies;

        public DuplicateEntitiesCommand(IEnumerable<GameEntity> sources) { _sources = sources.Where(s => s != null).ToList(); }

        public override string Name => _sources.Count == 1 ? "Duplicate " + _sources[0].Name : "Duplicate " + _sources.Count + " Entities";

        public override void Execute()
        {
            _copies.Clear();
            foreach (var src in _sources)
            {
                var copy = DataSerializer.FromBinary<GameEntity>(DataSerializer.ToBinary(src));
                if (copy == null) continue;
                copy.RegenerateIds();
                SetScene(copy, src.Scene);
                copy.Parent = src.Parent;
                var list = src.Parent != null ? src.Parent.Children : src.Scene?.Entities;
                if (list == null) continue;
                int idx = list.IndexOf(src);
                if (idx >= 0 && idx + 1 <= list.Count) list.Insert(idx + 1, copy); else list.Add(copy);
                try { copy.SyncEngineStateRecursive(src.Parent?.ActiveInHierarchy ?? (src.Scene?.IsActive ?? true)); } catch { }
                if (src.Scene != null) src.Scene.IsDirty = true;
                _copies.Add(copy);
            }
        }

        public override void Undo()
        {
            for (int i = _copies.Count - 1; i >= 0; i--)
            {
                var c = _copies[i];
                try { c.SyncEngineStateRecursive(false); } catch { }
                if (c.Parent != null) c.Parent.Children.Remove(c); else c.Scene?.Entities.Remove(c);
                try { SceneRenderService.Instance.RemoveEntityTree(c); } catch { }   // #358
                if (c.Scene != null) c.Scene.IsDirty = true;
            }
        }

        private static void SetScene(GameEntity e, Scene s)
        {
            e.Scene = s;
            if (e.Children != null) foreach (var c in e.Children) { c.Parent = e; SetScene(c, s); }
        }
    }
}
