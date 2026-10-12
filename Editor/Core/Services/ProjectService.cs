using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Exceptions;
using Editor.Core.Serialization;
using Editor.Core.Validation;
using Editor.ECS;

namespace Editor.Core.Services
{
    /// <summary>
    /// Zentraler Service f�r alle Projektoperationen.
    /// Verwaltet das Laden, Speichern und die Registry aller Projekte.
    /// Szenen werden separat in .vscene Dateien gespeichert.
    /// </summary>
    public sealed class ProjectService
    {
        private static readonly Lazy<ProjectService> _instance = new Lazy<ProjectService>(() => new ProjectService());
        public static ProjectService Instance => _instance.Value;

        private readonly Dictionary<Guid, ProjectRef> _projectRegistry;
        private readonly string _appDataPath;
        private readonly string _registryFilePath;
        private readonly string _defaultProjectsPath;

        /// <summary>
        /// Konstanten f�r Projektstruktur
        /// </summary>
        public const string ManifestFileName = "project.vortex";
        public const string LegacyManifestPath = ".ve/project.json";
        public const string AssetsFolder = "Assets";
        public const string ScenesFolder = "Scenes";
        public const string PrefabsFolder = "Prefabs";

        private ProjectService()
        {
            _appDataPath = Path.Combine(Editor.Core.Services.EditorPaths.AppDataRoot(), "VortexEngine");
            _defaultProjectsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "VortexEngineProjects");
            _registryFilePath = Path.Combine(_appDataPath, "projects.json");

            EnsureDirectoriesExist();
            _projectRegistry = LoadProjectRegistry();
        }

        /// <summary>
        /// Gibt alle registrierten Projekte zur�ck
        /// </summary>
        public Dictionary<Guid, ProjectRef> GetAllProjects() => _projectRegistry;

        /// <summary>Forget a project in the launcher list (its files stay on disk).</summary>
        public void UnregisterProject(Guid id)
        {
            if (_projectRegistry.Remove(id)) SaveProjectRegistry();
        }

        /// <summary>
        /// L�dt ein Projekt anhand seiner Referenz
        /// </summary>
        /// <summary>Load a project directly from its folder (used by the standalone player, which ships inside the project).</summary>
        public ProjectData LoadProjectFromPath(string dir) => LoadProject(new ProjectRef(dir, "Game"));

        public ProjectData LoadProject(ProjectRef projectRef)
        {
            try
            {
                // Versuche neues Format zuerst (the shipped game has project.vortex only inside the RAM pak)
                string manifestPath = Path.Combine(projectRef.Path, ManifestFileName);
                if (AssetVfs.Exists(manifestPath))
                {
                    return LoadProjectFromManifest(projectRef.Path);
                }

                // Fallback auf Legacy-Format
                string legacyPath = Path.Combine(projectRef.Path, LegacyManifestPath);
                if (File.Exists(legacyPath))
                {
                    return LoadLegacyProject(projectRef, legacyPath);
                }

                throw new FileNotFoundException($"Project file not found in {projectRef.Path}");
            }
            catch (ProjectException)
            {
                throw;
            }
            catch (IOException ioEx)
            {
                throw new ProjectIOException(
                    projectRef?.Path ?? "unbekannt",
                    "Fehler beim �ffnen des Projekts.",
                    ioEx
                );
            }
            catch (UnauthorizedAccessException uaEx)
            {
                throw new ProjectIOException(
                    projectRef?.Path ?? "unbekannt",
                    "Keine Berechtigung zum Zugriff auf den Projektpfad.",
                    uaEx
                );
            }
            catch (Exception ex)
            {
                throw new ProjectException(
                    $"Unerwarteter Fehler beim �ffnen des Projekts: {ex.Message}",
                    ex
                );
            }
        }

        /// <summary>
        /// L�dt ein Projekt aus dem neuen Manifest-Format
        /// </summary>
        private ProjectData LoadProjectFromManifest(string projectPath)
        {
            var manifestPath = Path.Combine(projectPath, ManifestFileName);
            var manifest = DataSerializer.LoadFromJson<ProjectManifest>(manifestPath);

            // Editor-only compatibility gate: migrate an older-format project (with a backup) or warn on a project
            // saved by a NEWER engine, before it loads. The shipped game's pak is read-only + has no UI, so skip
            // when mounted. Up-to-date projects (the common case) pass through with no dialog.
            if (!AssetVfs.IsMounted) CompatibilityGate?.Invoke(projectPath, manifest, manifestPath);

            // Erstelle ProjectData aus Manifest
            var project = new ProjectData(manifest.Id, projectPath, manifest.Name)
            {
                LastModified = manifest.LastModified,
                Settings = manifest.Settings ?? new ProjectSettings(),
                ImagePath = manifest.ThumbnailPath,
                StartSceneId = manifest.StartSceneId // round-trip the boot scene so editor saves preserve it
            };

            // Initialize asset database for this project
            AssetDatabase.Instance.Initialize(projectPath);

            // Which scene boots first? The SHIPPED GAME (asset pak mounted) starts in the designated
            // StartScene (e.g. the lobby) regardless of what the developer had open. The EDITOR resumes the
            // last-open scene. Each falls back to the other id, then to the first scene below.
            bool playerBoot = AssetVfs.IsMounted;
            Guid? bootSceneId = playerBoot
                ? (manifest.StartSceneId ?? manifest.LastOpenSceneId)
                : (manifest.LastOpenSceneId ?? manifest.StartSceneId);

            // Lade Szenen
            var scenesPath = Path.Combine(projectPath, AssetsFolder, ScenesFolder);
            var loadedSceneIds = new HashSet<Guid>();
            foreach (var sceneRef in manifest.Scenes)
            {
                var sceneFilePath = Path.Combine(scenesPath, sceneRef.RelativePath);

                if (AssetVfs.IsMounted)
                {
                    // SHIPPED GAME: create a lazy stub from the manifest (id/name/path) and DON'T deserialize it.
                    // Each scene lives in its own Scenes/<name>.vpak, mounted + deserialized only when that scene is
                    // entered (boot scene at startup, others via Scene.Load) — a 100-scene game never loads them all.
                    var stub = new Scene { Project = project, FilePath = sceneFilePath, Id = sceneRef.Id, Name = sceneRef.Name };
                    project.Scenes.Add(stub);
                    loadedSceneIds.Add(sceneRef.Id);
                }
                else if (AssetVfs.Exists(sceneFilePath))
                {
                    try
                    {
                        var scene = DataSerializer.LoadFromBinary<Scene>(sceneFilePath);
                        scene.FilePath = sceneFilePath;
                        scene.Project = project;
                        scene.Load();
                        project.Scenes.Add(scene);
                        loadedSceneIds.Add(scene.Id);
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[LoadProject] scene '" + sceneRef.RelativePath + "' failed to load: " + ex.Message); }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[LoadProject] manifest scene file missing (disk recovery will try): " + sceneFilePath);
                }
            }

            // SELF-HEAL: an editor save that ran while a .vscene was renamed/missing DROPS that scene's manifest
            // entry, so the file silently vanishes from the project forever (this is the recurring "scene
            // disappeared" bug). In the EDITOR (disk, not the shipped pak) pull in any .vscene in Assets/Scenes the
            // manifest didn't reference — deduped by scene id — so a scene file present on disk is never lost; the
            // next save re-records it in the manifest. The exported game's packed manifest is authoritative (skip).
            if (!AssetVfs.IsMounted && Directory.Exists(scenesPath))
            {
                foreach (var file in Directory.GetFiles(scenesPath, "*.vscene"))
                {
                    try
                    {
                        var scene = DataSerializer.LoadFromBinary<Scene>(file);
                        if (scene == null || loadedSceneIds.Contains(scene.Id)) continue;   // already loaded via the manifest
                        scene.FilePath = file;
                        scene.Project = project;
                        scene.Load();
                        project.Scenes.Add(scene);
                        loadedSceneIds.Add(scene.Id);
                        System.Diagnostics.Debug.WriteLine("[LoadProject] recovered orphan scene '" + scene.Name + "' from " + Path.GetFileName(file));
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[LoadProject] orphan scene '" + file + "' failed to load: " + ex.Message); }
                }
            }

            // Setze aktive Szene (StartScene im Spiel, LastOpenScene im Editor) — across manifest + recovered scenes.
            if (bootSceneId.HasValue)
            {
                foreach (var sc in project.Scenes)
                    if (sc.Id == bootSceneId.Value) { project.ActiveScene = sc; break; }
            }

            // Falls keine aktive Szene, nehme die erste
            if (project.ActiveScene == null && project.Scenes.Count > 0)
            {
                project.ActiveScene = project.Scenes[0];
            }

            return project;
        }

        /// <summary>
        /// L�dt ein Projekt im Legacy-Format und migriert es
        /// </summary>
        private ProjectData LoadLegacyProject(ProjectRef projectRef, string legacyPath)
        {
            var project = DataSerializer.LoadFromJson<ProjectData>(legacyPath);

            if (project.Scenes == null)
            {
                project.Scenes = new ObservableCollection<Scene>();
            }

            // Initialize asset database
            AssetDatabase.Instance.Initialize(projectRef.Path);

            // Migriere zum neuen Format
            SaveProject(project);

            return project;
        }

        /// <summary>
        /// Erstellt ein neues Projekt
        /// </summary>
        public ProjectData CreateProject(string projectName, string projectPath)
        {
            try
            {
                // Erstelle Projektverzeichnis zuerst
                if (!Directory.Exists(projectPath))
                {
                    Directory.CreateDirectory(projectPath);
                }
                
                var project = new ProjectData(projectPath, projectName);

                // Erstelle Default-Szene
                var defaultScene = SceneService.Instance.CreateDefaultScene(project, "Main Scene");
                project.Scenes.Clear(); // Remove any default scenes from constructor
                project.Scenes.Add(defaultScene);
                project.ActiveScene = defaultScene;

                // Initialize asset database
                AssetDatabase.Instance.Initialize(projectPath);

                SaveProject(project);
                
                System.Diagnostics.Debug.WriteLine($"Project created successfully at: {projectPath}");
                return project;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating project: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// Create a new project by copying a shipped template's project folder, then re-stamping identity (fresh Id +
        /// the chosen name) so the result is an independent project. Falls back to an empty scaffold when the template
        /// is missing/unreadable, so "Create" never dead-ends.
        /// </summary>
        public ProjectData CreateProjectFromTemplate(string projectName, string projectPath, string templateProjectDir)
        {
            if (string.IsNullOrEmpty(templateProjectDir) || !Directory.Exists(templateProjectDir)
                || !File.Exists(Path.Combine(templateProjectDir, ManifestFileName)))
                return CreateProject(projectName, projectPath);   // no usable template -> empty project

            if (Directory.Exists(projectPath) && Directory.GetFileSystemEntries(projectPath).Length > 0)
                throw new DuplicateProjectPathException(projectPath, projectName);

            Directory.CreateDirectory(projectPath);

            // Copy the template's project files, skipping machine/build-local dirs so the new project is clean.
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".git", ".vs", "bin", "obj", "Library", "Build", "Temp", "Logs" };
            CopyDirectoryFiltered(templateProjectDir, projectPath, skip);

            // Load the copy, give it a fresh identity + the chosen name, then re-save (writes manifest + registers it).
            var project = LoadProjectFromPath(projectPath);
            project.Id = Guid.NewGuid();
            project.Name = projectName;
            AssetDatabase.Instance.Initialize(projectPath);
            SaveProject(project);
            System.Diagnostics.Debug.WriteLine($"Project created from template '{templateProjectDir}' at: {projectPath}");
            return project;
        }

        // Template-metadata files that live at the template repo root but must NOT land in a created project.
        private static readonly HashSet<string> _templateMetaFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "template.json", "preview.png", "README.md", ".gitmodules", ".gitattributes" };

        private static void CopyDirectoryFiltered(string sourceDir, string destDir, HashSet<string> skipDirNames)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                if (_templateMetaFiles.Contains(Path.GetFileName(file))) continue;
                try { File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), true); } catch { }
            }
            foreach (var sub in Directory.GetDirectories(sourceDir))
            {
                var name = Path.GetFileName(sub);
                if (skipDirNames.Contains(name)) continue;
                CopyDirectoryFiltered(sub, Path.Combine(destDir, name), skipDirNames);
            }
        }

        /// <summary>
        /// Speichert ein Projekt (Manifest + separate Szenen-Dateien)
        /// </summary>
        public void SaveProject(ProjectData project)
        {
            try
            {
                ProjectValidator.ValidateProject(project, _projectRegistry);

                // Projekt in Registry speichern
                _projectRegistry[project.Id] = new ProjectRef(project.Id, project.Path, project.Name)
                {
                    ImagePath = project.ImagePath
                };

                // Aktualisiere LastModified
                project.LastModified = DateTime.Now;

                // Projektdateien erstellen
                CreateProjectFiles(project);

                // Speichere Manifest
                SaveManifest(project);

                // Speichere alle Szenen separat
                SaveAllScenes(project);

                // Registry speichern
                SaveProjectRegistry();
            }
            catch (ProjectException)
            {
                throw;
            }
            catch (IOException ioEx)
            {
                throw new ProjectIOException(
                    project?.Path ?? "unbekannt",
                    "Fehler beim Speichern des Projekts.",
                    ioEx
                );
            }
            catch (UnauthorizedAccessException uaEx)
            {
                throw new ProjectIOException(
                    project?.Path ?? "unbekannt",
                    "Keine Berechtigung zum Zugriff auf den Projektpfad.",
                    uaEx
                );
            }
            catch (Exception ex)
            {
                throw new ProjectException(
                    $"Unerwarteter Fehler beim Speichern des Projekts: {ex.Message}",
                    ex
                );
            }
        }

        /// <summary>
        /// Speichert das Projekt-Manifest (ohne Szenen-Inhalte)
        /// </summary>
        private void SaveManifest(ProjectData project)
        {
            try
            {
                var manifest = new ProjectManifest(project.Name)
                {
                    Id = project.Id,
                    LastModified = project.LastModified,
                    ThumbnailPath = project.ImagePath,
                    LastOpenSceneId = project.ActiveScene?.Id,
                    StartSceneId = project.StartSceneId, // the game's boot scene (persisted choice; null ⇒ first)
                    // Stamp the version the project was saved with, so the migration gate can check it on open.
                    EngineVersion = Editor.Core.EngineInfo.VersionString,
                    FormatVersion = Editor.Core.EngineInfo.CurrentProjectFormatVersion,
                    Settings = project.Settings ?? new ProjectSettings(),
                };

                // Füge Szenen-Referenzen hinzu
                foreach (var scene in project.Scenes)
                {
                    // Reference the ACTUAL on-disk file (its real name), NOT a name-derived guess. SaveScene writes
                    // to scene.FilePath; if the file was renamed so its name no longer matches scene.Name, a
                    // name-derived path here would point at a missing .vscene and the scene would silently vanish
                    // on the next load (LoadProjectFromManifest skips refs whose file doesn't exist). Fall back to
                    // the name only for a brand-new scene that hasn't been saved yet (FilePath still null).
                    var relativePath = !string.IsNullOrEmpty(scene.FilePath)
                        ? Path.GetFileName(scene.FilePath)
                        : $"{SanitizeFileName(scene.Name)}.vscene";
                    manifest.Scenes.Add(new SceneReference(scene.Id, scene.Name, relativePath));

                    // Fallback only: if no start scene was ever chosen, default to the first scene.
                    if (manifest.StartSceneId == null)
                    {
                        manifest.StartSceneId = scene.Id;
                    }
                }

                var manifestPath = Path.Combine(project.Path, ManifestFileName);
                
                // Ensure directory exists
                var dir = Path.GetDirectoryName(manifestPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                
                DataSerializer.SaveAsJson(manifest, manifestPath);
                System.Diagnostics.Debug.WriteLine($"Saved manifest to: {manifestPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving manifest: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Speichert alle Szenen des Projekts
        /// </summary>
        public void SaveAllScenes(ProjectData project)
        {
            try
            {
                var scenesPath = Path.Combine(project.Path, AssetsFolder, ScenesFolder);

                if (!Directory.Exists(scenesPath))
                {
                    Directory.CreateDirectory(scenesPath);
                }

                foreach (var scene in project.Scenes)
                {
                    SaveScene(project, scene);
                }
                
                System.Diagnostics.Debug.WriteLine($"Saved {project.Scenes.Count} scenes to: {scenesPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving scenes: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Speichert eine einzelne Szene
        /// </summary>
        public void SaveScene(ProjectData project, Scene scene)
        {
            var scenesPath = Path.Combine(project.Path, AssetsFolder, ScenesFolder);

            if (!Directory.Exists(scenesPath))
            {
                Directory.CreateDirectory(scenesPath);
            }

            // Setze FilePath falls nicht gesetzt
            if (string.IsNullOrEmpty(scene.FilePath))
            {
                scene.FilePath = Path.Combine(scenesPath, $"{SanitizeFileName(scene.Name)}.vscene");
            }

            // the editor's view travels with the scene (see EditorViewState)
            try { if (ReferenceEquals(project.ActiveScene, scene)) EditorViewState.Capture(scene); } catch { }
            // Speichere Szene als Bin�r
            DataSerializer.SaveAsBinary(scene, scene.FilePath);
            scene.IsDirty = false;
        }

        /// <summary>
        /// Speichert eine Entity als Prefab
        /// </summary>
        public void SavePrefab(ProjectData project, GameEntity entity, string prefabName = null)
        {
            var prefabsPath = Path.Combine(project.Path, AssetsFolder, PrefabsFolder);

            if (!Directory.Exists(prefabsPath))
            {
                Directory.CreateDirectory(prefabsPath);
            }

            var fileName = SanitizeFileName(prefabName ?? entity.Name) + ".ventity";
            var filePath = Path.Combine(prefabsPath, fileName);

            DataSerializer.SaveAsBinary(entity, filePath);
        }

        /// <summary>
        /// L�dt ein Prefab
        /// </summary>
        public GameEntity LoadPrefab(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Prefab file not found: {filePath}");
            }

            return DataSerializer.LoadFromBinary<GameEntity>(filePath);
        }

        /// <summary>
        /// Entfernt ung�ltige Zeichen aus Dateinamen
        /// </summary>
        private string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            foreach (var c in invalidChars)
            {
                fileName = fileName.Replace(c, '_');
            }
            return fileName;
        }

        /// <summary>
        /// Pr�ft ob ein Projektpfad bereits existiert
        /// </summary>
        public bool ProjectPathExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string normalizedPath = NormalizePath(path);

            foreach (var project in _projectRegistry.Values)
            {
                if (string.Equals(NormalizePath(project.Path), normalizedPath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
            }

        private void CreateProjectFiles(ProjectData project)
            {
                // Projektverzeichnis erstellen
                if (!Directory.Exists(project.Path))
                {
                    Directory.CreateDirectory(project.Path);
                }

                // Standard-Projektordner erstellen
                CreateDefaultProjectFolders(project.Path);

                // .ve Verzeichnis erstellen (versteckt)
                string veDir = Path.Combine(project.Path, ".ve");
            if (!Directory.Exists(veDir))
            {
                var dirInfo = Directory.CreateDirectory(veDir);
                try
                {
                    dirInfo.Attributes |= FileAttributes.Hidden;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Could not set hidden attribute: {ex.Message}");
                }
            }

            // Icon speichern
            try
            {
                var iconPath = SaveIconFromResources("AppIcon", project.Path);
                if (!string.IsNullOrEmpty(iconPath))
                {
                    project.ImagePath = iconPath;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not save icon: {ex.Message}");
            }

            // The legacy .ve/project.json stored the ENTIRE project (every scene + entity + component) inline
            // as one JSON — a scalability disaster for real games. The project is now stored as project.vortex
            // (manifest only) + Assets/Scenes/<name>.vscene (per-scene, binary). Delete any stale legacy copy
            // so scene content never lives in that file again. (LoadProject still READS it once, to migrate
            // an old project to the new format on first save — by which point it's already in memory.)
            try
            {
                string legacyFile = Path.Combine(veDir, "project.json");
                if (File.Exists(legacyFile)) File.Delete(legacyFile);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not remove legacy project.json: {ex.Message}");
            }
        }

        private void EnsureDirectoriesExist()
        {
            if (!Directory.Exists(_appDataPath))
            {
                Directory.CreateDirectory(_appDataPath);
            }

            if (!Directory.Exists(_defaultProjectsPath))
            {
                Directory.CreateDirectory(_defaultProjectsPath);
            }
        }

        private Dictionary<Guid, ProjectRef> LoadProjectRegistry()
        {
            try
            {
                if (!File.Exists(_registryFilePath))
                {
                    return new Dictionary<Guid, ProjectRef>();
                }

                var json = File.ReadAllText(_registryFilePath);
                return DataSerializer.FromJson<Dictionary<Guid, ProjectRef>>(json) 
                       ?? new Dictionary<Guid, ProjectRef>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Fehler beim Laden der Projekt-Registry: {ex.Message}");
                return new Dictionary<Guid, ProjectRef>();
            }
        }

        private void SaveProjectRegistry()
        {
            var json = DataSerializer.ToJson(_projectRegistry);
            File.WriteAllText(_registryFilePath, json);
        }

        private string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path;
            }
        }

        /// <summary>
        /// Erstellt die Standard-Ordnerstruktur f�r ein neues Projekt.
        /// </summary>
        private void CreateDefaultProjectFolders(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                return;

            string[] defaultFolders = new[]
            {
                "Assets",
                "Assets/Materials",
                "Assets/Models",
                "Assets/Prefabs",
                "Assets/Scenes",
                "Assets/Scripts",
                "Assets/Textures",
                "Assets/Audio",
                "Assets/Shaders",
                "Assets/Fonts",
                "Assets/UI",
                "Packages",
                "ProjectSettings"
            };

            foreach (var folder in defaultFolders)
            {
                string fullPath = Path.Combine(projectPath, folder);
                if (!Directory.Exists(fullPath))
                {
                    Directory.CreateDirectory(fullPath);
                }
            }
        }

        /// <summary>Editor shells install their migration/compatibility gate here (the player skips it).</summary>
        public static Action<string, Data.ProjectManifest, string> CompatibilityGate;
        /// <summary>Editor shells install a PNG writer for the default project icon (resource key, output path).</summary>
        public static Func<string, string, bool> IconWriter;

        private string SaveIconFromResources(string resourceKey, string projectPath, string fileName = "icon.png")
        {
            try
            {
                string iconPath = Path.Combine(projectPath, ".ve", fileName);
                return (IconWriter != null && IconWriter(resourceKey, iconPath)) ? iconPath : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Fehler beim Speichern des Icons: {ex.Message}");
                return null;
            }
        }
    }
}
