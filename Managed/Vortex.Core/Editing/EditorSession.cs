using System;
using System.IO;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.DllWrapper;
using Editor.Editors.WorldEditor.Components.SceneHierarchy;
using Editor.Scripting;

namespace Editor.Core.Editing
{
    /// <summary>
    /// The editor's project lifecycle, shared by every editor shell: open / create / save / close a project,
    /// scene activation, play-mode control and the on-activate hot reload of shaders and scripts. Shells only
    /// bind UI to it. (Port of the WPF MainWindow / HeaderBar plumbing.)
    /// </summary>
    public sealed class EditorSession
    {
        public static EditorSession Instance { get; } = new EditorSession();

        private bool _engineUp;

        public SceneHierarchyViewModel Hierarchy { get; } = new SceneHierarchyViewModel();
        public ProjectData Project => ProjectData.Current;
        public bool HasProject => ProjectData.Current != null;

        public event Action<ProjectData> ProjectOpened;
        public event Action ProjectClosed;
        public event Action<string> Toast;
        public event Action<string> Error;

        /// <summary>Bring up the native runtime once (before any viewport or resource call).</summary>
        public void EnsureEngine()
        {
            if (_engineUp) return;
            _engineUp = true;
            VortexAPI.InitEngineRuntime();
            var shaders = Editor.Core.Native.NativeLoader.ShaderDirectory;
            if (!string.IsNullOrEmpty(shaders)) VortexAPI.SetShaderDirectory(shaders);
            ConsoleService.Instance.GreetOnce();
        }

        public void ShutdownEngine()
        {
            if (!_engineUp) return;
            _engineUp = false;
            try { if (PlayModeService.Instance.IsPlaying) Stop(); } catch { }
            try { PhysicsNative.Shutdown(); } catch { }
            try { VortexAPI.ShutdownEngineRuntime(); } catch { }
        }

        // ------------------------------------------------------------------ project lifecycle

        public bool OpenProject(string directory)
        {
            try
            {
                var project = ProjectService.Instance.LoadProjectFromPath(Path.GetFullPath(directory));
                if (project == null) { Error?.Invoke("No project found at " + directory); return false; }
                return OpenProject(project);
            }
            catch (Exception ex) { Error?.Invoke("Could not open the project: " + ex.Message); return false; }
        }

        public bool OpenProject(ProjectData project)
        {
            if (project == null) return false;
            EnsureEngine();
            if (ProjectData.Current != null && !ReferenceEquals(ProjectData.Current, project)) CloseProject(keepLastProject: true);
            ProjectData.Current = project;
            try { Editor.Core.Assets.AssetTagService.Instance.Initialize(project.Path); } catch { }
            try { Editor.Core.Assets.AssetDatabase.Instance.Initialize(project.Path); } catch { }
            EditorStateService.Instance.SetLastProject(project.Id, project.Path);
            try { _ = Editor.Core.Services.Git.GitService.Instance.EnsureRepoAsync(project.Path); } catch { }
            try { AudioMixerConfig.Load(project.Path).Apply(); } catch { }
            Hierarchy.SetProject(project);          // loads + activates the start scene
            var scene = project.ActiveScene;
            if (scene != null)
            {
                try { GameRuntime.MountSceneAssets(scene); } catch { }
                try { SceneRenderService.Instance.PreloadSceneAssets(scene); } catch { }
                try { EditorViewState.Restore(scene); } catch { }   // the camera where the scene was saved from
            }
            ConsoleService.Instance.LogSystem("Project opened: " + project.Name);
            ProjectOpened?.Invoke(project);
            return true;
        }

        public ProjectData CreateProject(string name, string path, ProjectTemplate template)
        {
            var project = (template != null && !template.IsEmpty && !string.IsNullOrEmpty(template.ProjectDir))
                ? ProjectService.Instance.CreateProjectFromTemplate(name, path, template.ProjectDir)
                : ProjectService.Instance.CreateProject(name, path);
            return project;
        }

        public void SaveProject()
        {
            var project = ProjectData.Current;
            if (project == null) return;
            if (project.ActiveScene != null) SceneService.Instance.SaveScene(project.ActiveScene);
            ProjectService.Instance.SaveProject(project);
            Toast?.Invoke("Project saved");
        }

        public void SaveAll()
        {
            var project = ProjectData.Current;
            if (project == null) return;
            SceneService.Instance.SaveAllScenes(project);
            ProjectService.Instance.SaveProject(project);
            Toast?.Invoke("All scenes saved");
        }

        public void CloseProject(bool keepLastProject = false)
        {
            if (PlayModeService.Instance.IsPlaying) Stop();
            var project = ProjectData.Current;
            try { SelectionService.Instance.ClearSelection(); } catch { }
            try { SceneRenderService.Instance.ClearAllRenderables(); } catch { }
            try { SceneRenderService.EvictModelsUnusedBy(null); } catch { }   // every model of the closed project (#358)
            project?.Unload();
            ProjectData.Current = null;
            Hierarchy.SetProject(null);
            if (!keepLastProject) EditorStateService.Instance.ClearLastProject();
            ProjectClosed?.Invoke();
        }

        public void ActivateScene(Scene scene)
        {
            if (scene == null) return;
            Hierarchy.ActivateScene(scene);
            try { GameRuntime.MountSceneAssets(scene); } catch { }
            try { SceneRenderService.Instance.PreloadSceneAssets(scene); } catch { }
            try { EditorViewState.Restore(scene); } catch { }   // the camera where the scene was saved from
            SceneRenderService.RuntimeDirty = true;
        }

        /// <summary>Last project from the previous run, if it still exists.</summary>
        public string LastProjectPath => EditorStateService.Instance.IsLastProjectValid() ? EditorStateService.Instance.LastProjectPath : null;

        // ------------------------------------------------------------------ play mode

        public void Play()
        {
            var pms = PlayModeService.Instance;
            if (pms.State == PlayState.Paused) { pms.Resume(); return; }
            if (pms.IsPlaying) return;
            var mainCam = CameraService.Instance.GetMainCamera();
            if (mainCam.IsValid) CameraService.Instance.SetActiveCamera(mainCam);
            pms.Play();
        }

        public void Pause() => PlayModeService.Instance.Pause();

        public void Stop()
        {
            CameraService.Instance.SwitchToEditorCamera();
            PlayModeService.Instance.Stop();
            PlayModeService.Instance.IsExternalWindow = false;
        }

        public void TogglePlay()
        {
            if (PlayModeService.Instance.IsPlaying) Stop(); else Play();
        }

        // ------------------------------------------------------------------ hot reload (window activated)

        public void OnWindowActivated()
        {
            try
            {
                if (ProjectData.Current == null) return;
                if (VortexAPI.AnyMaterialShaderDirty())
                {
                    int n = VortexAPI.ReloadMaterialShaders();
                    if (n > 0) Toast?.Invoke(n == 1 ? "1 shader hot-reloaded" : n + " shaders hot-reloaded");
                }
                var sr = ScriptRuntime.Instance;
                var pms = PlayModeService.Instance;
                if (pms.State == PlayState.Playing && !pms.IsExternalWindow && sr.ScriptsChanged())
                {
                    sr.ReloadScripts();
                    if (sr.LastReloadOutcome == ScriptRuntime.ReloadOutcome.Reloaded) Toast?.Invoke("Scripts hot-reloaded — " + sr.LastReloadSummary);
                    else if (sr.LastReloadOutcome == ScriptRuntime.ReloadOutcome.CompileError) Toast?.Invoke("Hot-reload failed: " + sr.LastReloadError);
                }
            }
            catch { }
        }
    }
}
