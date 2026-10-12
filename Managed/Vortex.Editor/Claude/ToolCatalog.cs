using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace VortexEditor.Claude
{
    /// <summary>A tool that drives the undo stack itself (undo, redo): it runs without the per-call undo group.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ManagesUndoAttribute : Attribute { }

    /// <summary>A changing tool whose effects are not all undoable (it writes files outside the undo stack): it gets no
    /// generic dry_run.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class NoDryRunAttribute : Attribute { }

    /// <summary>A tool that reaches beyond the editor (internet sources, generation services) — the MCP openWorldHint.
    /// Everything else works on the open project only.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class OpenWorldAttribute : Attribute { }

    /// <summary>One Vortex tool: name, description, JSON input schema, behaviour hints and the bound C# method.</summary>
    public sealed class ToolDef
    {
        public string Name { get; init; }
        public string Title { get; init; }
        public string Description { get; init; }
        /// <summary>The tool set it belongs to ("Scenes", "Viewport" …), from the declaring class.</summary>
        public string Category { get; init; }
        public JsonElement InputSchema { get; init; }
        public bool ReadOnly { get; init; }
        public bool Destructive { get; init; }
        public bool Idempotent { get; init; }
        public bool OpenWorld { get; init; }
        public bool ManagesUndo { get; init; }
        /// <summary>dry_run is handled by the host (run, describe, roll back) — the tool has no dry_run of its own.</summary>
        public bool GenericDryRun { get; init; }
        internal AIFunction Function { get; init; }
    }

    /// <summary>
    /// Every Vortex tool, defined once and registered twice (#84): static methods in classes marked
    /// <c>[McpServerToolType]</c>, each with <c>[McpServerTool]</c> + <c>[Description]</c>. The MCP server lists and
    /// calls them through <see cref="ToolHost"/>, and the embedded Claude panel hands the very same definitions to the
    /// Messages API — one implementation, one behaviour, one undo model.
    /// </summary>
    public static class ToolCatalog
    {
        private static readonly object Lock = new object();
        private static List<ToolDef> _all;
        private static Dictionary<string, ToolDef> _byName;

        public static IReadOnlyList<ToolDef> All { get { Ensure(); return _all; } }

        public static ToolDef Find(string name)
        {
            Ensure();
            return name != null && _byName.TryGetValue(name, out var d) ? d : null;
        }

        private static void Ensure()
        {
            if (_all != null) return;
            lock (Lock)
            {
                if (_all != null) return;
                var list = Discover();
                _byName = list.ToDictionary(t => t.Name, StringComparer.Ordinal);
                _all = list;
            }
        }

        private static List<ToolDef> Discover()
        {
            var list = new List<ToolDef>();
            var options = new AIFunctionFactoryOptions
            {
                SerializerOptions = ToolJson.Options,
                // keep the C# return value as it is: ToolHost turns it into text / JSON / images
                MarshalResult = (result, type, ct) => new ValueTask<object>(result),
            };
            foreach (var type in typeof(ToolCatalog).Assembly.GetTypes())
            {
                if (type.GetCustomAttribute<McpServerToolTypeAttribute>() == null) continue;
                string category = type.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? type.Name.Replace("Tools", "");
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    var attr = m.GetCustomAttribute<McpServerToolAttribute>();
                    if (attr == null) continue;
                    try
                    {
                    string name = attr.Name ?? Snake(m.Name);
                    string desc = m.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
                    var fn = AIFunctionFactory.Create(m, (object)null, new AIFunctionFactoryOptions
                    {
                        Name = name,
                        Description = desc,
                        SerializerOptions = options.SerializerOptions,
                        MarshalResult = options.MarshalResult,
                    });
                    bool manages = m.GetCustomAttribute<ManagesUndoAttribute>() != null;
                    bool openWorld = m.GetCustomAttribute<OpenWorldAttribute>() != null;
                    bool generic = !attr.ReadOnly && !manages && !openWorld && m.GetCustomAttribute<NoDryRunAttribute>() == null
                                   && !m.GetParameters().Any(p => p.Name == "dry_run");
                    list.Add(new ToolDef
                    {
                        Name = name,
                        Title = attr.Title ?? Humanize(name),
                        Description = desc,
                        Category = category,
                        InputSchema = generic ? WithDryRun(fn.JsonSchema) : fn.JsonSchema,
                        GenericDryRun = generic,
                        ReadOnly = attr.ReadOnly,
                        Destructive = attr.Destructive,
                        Idempotent = attr.Idempotent,
                        // the SDK attribute defaults OpenWorld to true; ours is explicit
                        OpenWorld = openWorld,
                        ManagesUndo = manages,
                        Function = fn,
                    });
                    }
                    catch (Exception ex)
                    {
                        // a tool whose schema cannot be built (a NaN default once crashed the editor at start) is left out, not fatal
                        try { Editor.Core.Services.ConsoleService.Instance.LogError("MCP: the tool '" + m.Name + "' was skipped — " + ex.Message); } catch { }
                        System.Diagnostics.Debug.WriteLine("[ToolCatalog] " + m.Name + ": " + ex);
                    }
                }
            }
            return list.OrderBy(t => t.Category, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
        }

        /// <summary>The schema plus the host-provided dry_run flag.</summary>
        private static JsonElement WithDryRun(JsonElement schema)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(schema.GetRawText()) as System.Text.Json.Nodes.JsonObject;
            if (node == null) return schema;
            if (!(node["properties"] is System.Text.Json.Nodes.JsonObject props)) node["properties"] = props = new System.Text.Json.Nodes.JsonObject();
            props["dry_run"] = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Only report what this call would create, remove, change and write — nothing is changed.",
                ["default"] = false,
            };
            return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
        }

        /// <summary>CreateEntity → create_entity.</summary>
        internal static string Snake(string pascal)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < pascal.Length; i++)
            {
                char c = pascal[i];
                if (char.IsUpper(c) && i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>create_entity → "Create entity".</summary>
        internal static string Humanize(string snake)
        {
            string s = snake.Replace('_', ' ');
            return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }
}
