using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Editor.ECS;

namespace VortexEditor.Shell
{
    /// <summary>
    /// One place to open every editor window — the Asset Browser, menus, inspector buttons and the hierarchy call
    /// these instead of constructing windows, so each window owns its <c>Open</c> and can evolve independently.
    /// Asset double-click convention (same as the Windows editor):
    /// plain = the asset's default action (add a model / prefab / primitive to the scene, open a scene, …),
    /// Shift = <see cref="OpenEditorFor"/> (the asset's editor), Ctrl/Cmd = <see cref="OpenLargePreview"/>.
    /// </summary>
    public static class EditorWindows
    {
        public static Window Owner => EditorCommands.Window;

        /// <summary>Show a tool window owned by the main window (stays on top of it, closes with it).</summary>
        public static void Show(Window w)
        {
            if (w == null) return;
            if (Owner != null) w.Show(Owner); else w.Show();
        }

        public static void ModelEditor(string fullPath) => ModelEditorWindow.Open(fullPath);
        public static void MeshEditor(string fullPath) => MeshEditorWindow.Open(fullPath);
        /// <summary>Large interactive preview: models, prefabs, materials, textures, primitives.</summary>
        public static void AssetViewer(string fullPath) => ModelViewerWindow.Open(fullPath);
        public static void TextureEditor(string fullPath) => TextureEditorWindow.Open(fullPath);
        public static void MaterialEditor(string fullPath) => MaterialEditorWindow.Open(fullPath);
        public static void PrefabEditor(string fullPath) => PrefabEditorWindow.Open(fullPath);
        public static void SoundContainerEditor(string fullPath) => SoundContainerEditorWindow.Open(fullPath);
        public static void UiEditor(string fullPath) => UiEditorWindow.Open(fullPath);
        public static void AnimationEditor(string fullPath) => AnimationEditorWindow.Open(fullPath);
        public static void VfxEditor(string fullPath) => VfxEditorWindow.Open(fullPath);
        public static void BehaviorTreeEditor(string fullPath) => BehaviorTreeEditorWindow.Open(fullPath);
        public static void Navigation() => NavigationWindow.Open();
        public static void SocketEditor(GameEntity e) => SocketEditorWindow.Open(e);
        public static void CollisionEditor(GameEntity e) => CollisionEditorWindow.Open(e);
        public static void AudioMixer() => AudioMixerWindow.Open();
        public static void Git() => GitWindow.Open();
        public static void TerrainTools() => TerrainToolsWindow.Open();
        public static void History() => HistoryWindow.Open();
        public static void StressTest(string modelFullPath) => StressTestWindow.Open(modelFullPath);
        public static void AssetTags(string fullPath) => AssetTagEditorDialog.Open(fullPath);
        public static Task<string[]> ImportAssets(string[] files, string targetFolder) => AssetImportDialog.Run(files, targetFolder);
        public static Task<Color?> PickColor(Color initial, string title = "Color") => ColorPickerDialog.Pick(initial, title);

        static readonly string[] Models = { ".glb", ".gltf", ".fbx", ".obj", ".dae", ".3ds", ".blend" };
        static readonly string[] Textures = { ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds", ".hdr", ".exr", ".gif", ".webp" };

        static bool Is(string ext, string[] set) => Array.IndexOf(set, ext) >= 0;

        /// <summary>Shift + double-click: open the dedicated editor for this asset type. False = no editor for it.</summary>
        public static bool OpenEditorFor(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            if (Is(ext, Models)) { ModelEditor(fullPath); return true; }
            if (Is(ext, Textures)) { TextureEditor(fullPath); return true; }
            switch (ext)
            {
                case ".vmat": MaterialEditor(fullPath); return true;
                case ".ventity": case ".vprefab": PrefabEditor(fullPath); return true;
                case ".vsndc": SoundContainerEditor(fullPath); return true;
                case ".vui": UiEditor(fullPath); return true;
                case ".vanim": AnimationEditor(fullPath); return true;
                case ".vfx": VfxEditor(fullPath); return true;
                case ".vbt": BehaviorTreeEditor(fullPath); return true;
            }
            return false;
        }

        /// <summary>Ctrl/Cmd + double-click: large interactive preview window. False = nothing to preview.</summary>
        public static bool OpenLargePreview(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            if (fullPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase) || Is(ext, Models) || Is(ext, Textures)
                || ext == ".vmat" || ext == ".ventity" || ext == ".vprefab") { AssetViewer(fullPath); return true; }
            if (ext == ".vfx") { VfxEditor(fullPath); return true; }   // the editor IS the large live preview
            if (ext == ".vbt") { BehaviorTreeEditor(fullPath); return true; }
            return false;
        }
    }

    /// <summary>Temporary window for editors that are being ported (replaced by the real window).</summary>
    internal sealed class PlaceholderWindow : Window
    {
        public PlaceholderWindow(string title, string what)
        {
            Title = title; Width = 460; Height = 180;
            Content = new TextBlock { Text = what, Margin = new Avalonia.Thickness(20), TextWrapping = TextWrapping.Wrap };
        }
    }
}
