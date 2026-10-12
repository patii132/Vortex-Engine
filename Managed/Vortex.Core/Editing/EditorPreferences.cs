using System;
using System.IO;
using System.Text.Json;
using Editor.Core.Services;

namespace Editor.Core.Editing
{
    /// <summary>Per-user editor preferences (editor-prefs.json in the Vortex app-data folder).</summary>
    public sealed class EditorPreferences
    {
        private static EditorPreferences _current;
        public static EditorPreferences Current => _current ?? (_current = Load());

        /// <summary>Skip the project hub and reopen the last project when the editor starts (off by default).</summary>
        public bool OpenLastProjectOnStart { get; set; }
        /// <summary>"System", "Dark" or "Light".</summary>
        public string Theme { get; set; } = "System";
        /// <summary>Preferred IDE for scripts: "" (auto), "code", "rider".</summary>
        public string ScriptIde { get; set; } = "";
        /// <summary>Run the Claude MCP server (Claude Code / Claude Desktop operate the editor over http://127.0.0.1:port/mcp).
        /// Off until the user turns it on (Tools ▸ Claude ▸ Connect Claude…).</summary>
        public bool McpServerEnabled { get; set; }
        /// <summary>Port of the MCP server. A fixed default keeps committed .mcp.json files valid across sessions.</summary>
        public int McpPort { get; set; } = 7420;
        /// <summary>The scene's post effects (colour grade, bloom, FXAA, vignette, grain, aberration) in the editor's own
        /// viewport — while building the world AND while playing in it. On by default so the viewport shows the picture the
        /// game will; off saves a few milliseconds per frame on a slow machine. The standalone player and the game window
        /// always apply them. Toggled from the viewport toolbar (FX) and the Environment panel.</summary>
        public bool PostEffectsInViewport { get; set; } = true;

        private static string FilePath => Path.Combine(EditorPaths.VortexAppData, "editor-prefs.json");

        public static EditorPreferences Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var p = JsonSerializer.Deserialize<EditorPreferences>(File.ReadAllText(FilePath));
                    if (p != null) return p;
                }
            }
            catch { }
            return new EditorPreferences();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(EditorPaths.VortexAppData);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
