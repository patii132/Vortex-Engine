using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.Core.Services.Terrain;
using Editor.ECS;
using VortexEditor.Panels.Inspector;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Window ▸ Terrain Tools (and the viewport's Terrain button): the terrain editor as its own palette — every terrain of the
    /// open scene in a list, a Create / From Heightmap pair for new ones, and below it the full Terrain inspector of the chosen
    /// terrain: size, resolution, layers, the heightmap preview with import / export, the noise generator, and the sculpt / paint
    /// brush that works in the viewport while the window is open. The same rows the Inspector shows (TerrainCard), so the two
    /// never disagree; the palette just keeps them in reach while the hierarchy and inspector are used for other things.
    /// </summary>
    public sealed class TerrainToolsWindow : Window
    {
        private static TerrainToolsWindow _open;
        public static void Open()
        {
            if (_open != null) { _open.Activate(); return; }
            _open = new TerrainToolsWindow();
            _open.Closed += (s, e) => _open = null;
            EditorWindows.Show(_open);
        }
        public static TerrainToolsWindow Current => _open;

        private readonly ComboBox _terrains = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly StackPanel _rows = new StackPanel { Spacing = 2 };
        private readonly TextBlock _empty = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 10, 0, 0) };
        private readonly List<GameEntity> _list = new List<GameEntity>();
        private GameEntity _shown;
        private bool _syncing;
        private readonly DispatcherTimer _refresh;

        private TerrainToolsWindow()
        {
            Title = "Terrain Tools";
            Width = 420; Height = 820; MinWidth = 360; MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;

            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(12, 12, 12, 6) };
            var create = new Button { Content = "＋ Terrain", Margin = new Thickness(6, 0, 0, 0) };
            ToolTip.SetTip(create, "A flat 64 m terrain at the origin — sculpt it, generate a landscape or import a heightmap below");
            create.Click += (s, e) => { try { var ent = EditorCommands.CreateTerrain(); Rebuild(ent); } catch (Exception ex) { EditorCommands.Fail("Create terrain", ex); } };
            var import = new Button { Content = "From Heightmap…", Margin = new Thickness(6, 0, 0, 0) };
            ToolTip.SetTip(import, "A terrain shaped by a PNG (8 / 16 bit) or RAW 16 picture");
            import.Click += async (s, e) => { await EditorCommands.CreateTerrainFromHeightmap(); Rebuild(SelectionService.Instance.SelectedEntity); };
            Grid.SetColumn(_terrains, 0); Grid.SetColumn(create, 1); Grid.SetColumn(import, 2);
            top.Children.Add(_terrains); top.Children.Add(create); top.Children.Add(import);
            _terrains.SelectionChanged += (s, e) =>
            {
                if (_syncing) return;
                int i = _terrains.SelectedIndex;
                if (i >= 0 && i < _list.Count) { SelectionService.Instance.SelectedEntity = _list[i]; Show(_list[i]); }
            };

            var scroll = new ScrollViewer { Content = _rows, Padding = new Thickness(12, 0, 12, 12), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            var root = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            var hint = new TextBlock { Text = "Pick a tool under Sculpt & paint and drag on the terrain in the viewport. Shift inverts raise / lower, Ctrl smooths; every stroke is one undo step.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 11, Margin = new Thickness(12, 0, 12, 6) };
            DockPanel.SetDock(hint, Dock.Top);
            root.Children.Add(top); root.Children.Add(hint); root.Children.Add(scroll);
            Content = root;

            SelectionService.Instance.SelectionChanged += OnSelectionChanged;
            EditorSession.Instance.ProjectOpened += OnProjectChanged;
            EditorSession.Instance.ProjectClosed += OnProjectClosed;
            Closed += (s, e) =>
            {
                SelectionService.Instance.SelectionChanged -= OnSelectionChanged;
                EditorSession.Instance.ProjectOpened -= OnProjectChanged;
                EditorSession.Instance.ProjectClosed -= OnProjectClosed;
                _refresh.Stop();
                try { if (TerrainToolService.Tool != TerrainTool.None) TerrainToolService.SetTool(TerrainTool.None); } catch { }
            };
            // the rows' value boxes follow outside changes (strokes, the MCP tools) like the inspector's do
            _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (s, e) => { try { PropertyRows.RefreshAll(); } catch { } });
            _refresh.Start();
            Rebuild(SelectionService.Instance.SelectedEntity);
        }

        private void OnProjectChanged(ProjectData p) => Dispatcher.UIThread.Post(() => Rebuild(null));
        private void OnProjectClosed() => Dispatcher.UIThread.Post(() => Rebuild(null));
        private void OnSelectionChanged(object s, SelectionEventArgs e)
        {
            var sel = SelectionService.Instance.SelectedEntity;
            if (sel != null && sel.GetComponent<Editor.ECS.Components.Rendering.Terrain>() != null && !ReferenceEquals(sel, _shown)) Dispatcher.UIThread.Post(() => Rebuild(sel));
        }

        /// <summary>List the scene's terrains and show one: the given entity when it is a terrain, else the shown one, else the first.</summary>
        private void Rebuild(GameEntity prefer)
        {
            _list.Clear();
            var scene = ProjectData.Current?.ActiveScene;
            if (scene != null)
                foreach (var e in Panels.AssetBrowser.WorldThumbs.Walk(scene))
                    if (e.GetComponent<Editor.ECS.Components.Rendering.Terrain>() != null) _list.Add(e);
            _syncing = true;
            _terrains.Items.Clear();
            foreach (var e in _list) _terrains.Items.Add(e.Name);
            _syncing = false;
            GameEntity show = prefer != null && _list.Contains(prefer) ? prefer : (_shown != null && _list.Contains(_shown) ? _shown : _list.FirstOrDefault());
            Show(show);
        }

        private void Show(GameEntity e)
        {
            _shown = e;
            _syncing = true;
            _terrains.SelectedIndex = e != null ? _list.IndexOf(e) : -1;
            _syncing = false;
            _rows.Children.Clear();
            var t = e?.GetComponent<Editor.ECS.Components.Rendering.Terrain>();
            if (e == null || t == null)
            {
                _empty.Text = ProjectData.Current?.ActiveScene == null ? "Open a project first." : "No terrain in this scene yet — ＋ Terrain makes a flat one to sculpt, From Heightmap… shapes one from a picture.";
                _rows.Children.Add(_empty);
                return;
            }
            try
            {
                foreach (var row in TerrainCard.RowsFor(t, e)) _rows.Children.Add(row);
            }
            catch (Exception ex) { _rows.Children.Add(new TextBlock { Text = "Could not build the terrain rows: " + ex.Message, TextWrapping = TextWrapping.Wrap }); }
        }
    }
}
