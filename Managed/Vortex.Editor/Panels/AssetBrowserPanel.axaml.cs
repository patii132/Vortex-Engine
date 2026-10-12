using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.Editors.WorldEditor.Components.FileExplorer.Models;
using Editor.Editors.WorldEditor.Components.FileExplorer.Services;
using Editor.ECS;
using VortexEditor.Controls;
using VortexEditor.Panels.AssetBrowser;
using VortexEditor.Services;
using VortexEditor.Shell;
using Actions = VortexEditor.Services.AssetActions;
using CoreAssetActions = Editor.Core.Assets.AssetActions;

namespace VortexEditor.Panels
{
    /// <summary>
    /// The project's Asset Browser (bottom "Project" tab) — port of the Windows editor's AssetBrowserView:
    /// an Explorer tab that browses folders (synced with the file tree) plus project-wide type tabs (Meshes with the
    /// built-in primitives, Models, Textures, Materials, Scripts, Audio, Prefabs, Scenes); real thumbnails
    /// (ThumbnailService renders / decodes, waveforms for audio), grid or list view with a tile-size slider, search,
    /// tag filter and sorting; Finder-style selection (click, ⌘-click, ⇧-click, rubber band) and keys; the Windows
    /// double-click convention (plain = default action, ⇧ = the asset's editor, ⌘/Ctrl = large preview); drag to the
    /// viewport / hierarchy / inspector ("vortex/asset"), drag onto folders to move (undoable, .vmeta sidecars follow),
    /// Finder drops to import; and the full context menu.
    /// </summary>
    public partial class AssetBrowserPanel : UserControl
    {
        public static readonly StyledProperty<double> TileSizeProperty = AvaloniaProperty.Register<AssetBrowserPanel, double>(nameof(TileSize), 96);
        public static readonly StyledProperty<double> TileWidthProperty = AvaloniaProperty.Register<AssetBrowserPanel, double>(nameof(TileWidth), 110);
        public static readonly StyledProperty<double> IconSizeProperty = AvaloniaProperty.Register<AssetBrowserPanel, double>(nameof(IconSize), 34);

        /// <summary>Edge length of a grid tile's preview square (the tile-size slider).</summary>
        public double TileSize { get => GetValue(TileSizeProperty); set => SetValue(TileSizeProperty, value); }
        public double TileWidth { get => GetValue(TileWidthProperty); set => SetValue(TileWidthProperty, value); }
        public double IconSize { get => GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }

        /// <summary>The live browser (static callers: "locate asset" from the inspector / hierarchy).</summary>
        public static AssetBrowserPanel Current { get; private set; }

        public static readonly string[] TabNames = { "Explorer", "Meshes", "Models", "Textures", "Materials", "Scripts", "Audio", "Prefabs", "Scenes" };

        private readonly ObservableCollection<AssetTile> _tiles = new ObservableCollection<AssetTile>();
        private readonly BrowserSettings _settings;
        private readonly List<string> _back = new List<string>(), _forward = new List<string>();
        private string _tab = "Explorer";
        private string _folder;
        private string _search = "";
        private string _tagFilter;
        private bool _listMode;
        private bool _restoringSelection;
        private string _pendingRename;
        private List<string> _pendingSelect;
        private AssetTile _renaming;
        private DispatcherTimer _refreshTimer, _searchTimer, _dbTimer, _saveTimer;
        private List<string> _importBatch;

        // pointer state: drag arming, press-on-selection, rubber band
        private AssetTile _pressTile, _selectOnRelease;
        private Point _pressPos;
        private bool _dragArmed, _dragging, _marquee;
        private Point _marqueeStart;
        private List<AssetTile> _marqueeBase;
        private AssetTile _dropHighlight;

        public AssetBrowserPanel()
        {
            InitializeComponent();
            Current = this;
            // a narrow centre column (side panels open): the search field gives way before the buttons next to it are cut
            SizeChanged += (s, e) =>
            {
                SearchBox.Width = Math.Clamp(e.NewSize.Width - 300, 110, 180);
                CreateLabel.IsVisible = ImportLabel.IsVisible = e.NewSize.Width >= 420;   // icons only when it is tight
            };
            _settings = BrowserSettings.Load();
            Items.ItemsSource = _tiles;
            _listMode = string.Equals(_settings.ViewMode, "List", StringComparison.OrdinalIgnoreCase);
            ApplyViewMode(_listMode, save: false);
            SizeSlider.Value = Math.Clamp(_settings.TileSize, SizeSlider.Minimum, SizeSlider.Maximum);
            ApplyTileSize(SizeSlider.Value, save: false);
            UpdateSortLabel();
            UpdateTagButton();

            AssetNavigation.Changed += OnNavigated;
            AssetWatcher.Changed += OnAssetsChanged;
            AssetTagEditorDialog.TagsChanged += path => Dispatcher.UIThread.Post(() => OnTagsChanged(path));
            FileExplorerService.Instance.FolderContentsChanged += (s, e) => Dispatcher.UIThread.Post(() => ScheduleRefresh());

            Items.AddHandler(PointerPressedEvent, OnItemsPointerPressed, RoutingStrategies.Tunnel);
            Items.AddHandler(PointerMovedEvent, OnItemsPointerMoved, RoutingStrategies.Tunnel);
            Items.AddHandler(PointerReleasedEvent, OnItemsPointerReleased, RoutingStrategies.Tunnel);
            Items.AddHandler(PointerCaptureLostEvent, (s, e) => { if (_marquee) EndMarquee(); });
            AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Tunnel);
            SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
            // On macOS ⌘D / ⌘⌫ / ⌘A / ⌘C arrive as menu commands (AppKit consumes a menu key equivalent before the
            // window sees the key): while the item list has focus they act on the selected assets, not the scene.
            EditorCommands.RegisterEditHandler(Items, OnEditCommand);

            DragDrop.SetAllowDrop(Items, true);
            Items.AddHandler(DragDrop.DragOverEvent, OnItemsDragOver);
            Items.AddHandler(DragDrop.DragLeaveEvent, (s, e) => SetDropHighlight(null));
            Items.AddHandler(DragDrop.DropEvent, OnItemsDrop);
            UpdateNavButtons();
        }

        // ================================================================ state
        public static string ProjectRoot => ProjectData.Current?.Path;
        private static string AssetsRoot => ProjectRoot == null ? null : Path.Combine(ProjectRoot, "Assets");

        /// <summary>Folder shown by the Explorer tab (absolute).</summary>
        public string CurrentFolder => _folder;
        public string CurrentTab => _tab;
        public IReadOnlyList<AssetTile> Tiles => _tiles;
        public IReadOnlyList<AssetTile> SelectedAssets => SelectedTiles.ToList();
        public bool IsListMode => _listMode;
        private bool IsFiltering => !string.IsNullOrEmpty(_search) || _tagFilter != null;

        private IEnumerable<AssetTile> SelectedTiles => (Items.SelectedItems ?? Array.Empty<object>()).OfType<AssetTile>();

        // ================================================================ project / navigation
        /// <summary>Project opened / closed: start watching Assets and show it.</summary>
        public void Reload()
        {
            Actions.StopAudition();
            CancelRename();
            _back.Clear(); _forward.Clear();
            string root = ProjectRoot;
            if (root == null)
            {
                AssetWatcher.Stop();
                AssetNavigation.Reset();
                _folder = null;
                RefreshNow(resetScroll: true);
                UpdateNavButtons();
                return;
            }
            AssetNavigation.EnsureExplorer();
            AssetWatcher.Start(root);
            UpdateTagButton();
            string assets = AssetsRoot;
            SetTab("Explorer", refresh: false);
            _folder = null;
            NavigateInternal(Directory.Exists(assets) ? assets : root, recordHistory: false);
        }

        /// <summary>Show a folder in the Explorer tab (switches to it).</summary>
        public void Navigate(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            if (_tab != "Explorer") SetTab("Explorer", refresh: false);
            NavigateInternal(folder, recordHistory: true, force: true);
        }

        private void NavigateInternal(string folder, bool recordHistory, bool force = false)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            folder = AssetTile.Normalize(folder);
            bool changed = !AssetFileOps.PathsEqual(folder, _folder);
            if (changed && recordHistory && _folder != null) { _back.Add(_folder); _forward.Clear(); }
            _folder = folder;
            AssetNavigation.NavigateTo(folder);
            if (changed || force) { CancelRename(); if (IsFiltering && _tab == "Explorer" && changed) ClearFilters(refresh: false); RefreshNow(resetScroll: changed); }
            UpdateNavButtons();
        }

        /// <summary>The tree (or other code) navigated.</summary>
        private void OnNavigated(string path)
        {
            if (AssetFileOps.PathsEqual(path, _folder)) return;
            if (_folder != null) { _back.Add(_folder); _forward.Clear(); }
            _folder = path;
            CancelRename();
            if (_tab == "Explorer") { if (IsFiltering) ClearFilters(refresh: false); RefreshNow(resetScroll: true); }
            else UpdateBreadcrumb();
            UpdateNavButtons();
        }

        public void GoBack()
        {
            if (_back.Count == 0) return;
            string target = _back[_back.Count - 1]; _back.RemoveAt(_back.Count - 1);
            if (_folder != null) _forward.Add(_folder);
            if (_tab != "Explorer") SetTab("Explorer", refresh: false);
            NavigateInternal(target, recordHistory: false, force: true);
        }

        public void GoForward()
        {
            if (_forward.Count == 0) return;
            string target = _forward[_forward.Count - 1]; _forward.RemoveAt(_forward.Count - 1);
            if (_folder != null) _back.Add(_folder);
            if (_tab != "Explorer") SetTab("Explorer", refresh: false);
            NavigateInternal(target, recordHistory: false, force: true);
        }

        /// <summary>Parent folder (never above the project root).</summary>
        public void NavigateUp()
        {
            string root = ProjectRoot;
            if (_folder == null || root == null || AssetFileOps.PathsEqual(_folder, root)) return;
            string parent = Path.GetDirectoryName(_folder);
            if (parent != null && AssetFileOps.IsWithin(parent, root)) Navigate(parent);
        }

        private void UpdateNavButtons()
        {
            BackButton.IsEnabled = _back.Count > 0;
            ForwardButton.IsEnabled = _forward.Count > 0;
        }

        /// <summary>Navigate to a file's folder and select it (WPF SelectFileInExplorer).</summary>
        public void Reveal(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            if (Directory.Exists(fullPath)) { Navigate(fullPath); return; }
            string dir = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            _pendingSelect = new List<string> { fullPath };
            if (IsFiltering) ClearFilters(refresh: false);
            if (_tab != "Explorer") SetTab("Explorer", refresh: false);
            if (AssetFileOps.PathsEqual(dir, _folder)) RefreshNow(resetScroll: false); else Navigate(dir);
        }

        /// <summary>Static entry point (the Windows editor's AssetBrowserView.SelectFileInExplorer).</summary>
        public static void SelectFileInExplorer(string fullPath) => Current?.Reveal(fullPath);

        /// <summary>Select the tile of <paramref name="path"/> (revealing its folder when it isn't listed).</summary>
        public void SelectPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var t = _tiles.FirstOrDefault(x => AssetFileOps.PathsEqual(x.FullPath, path));
            if (t != null) { SelectOnly(t); Items.ScrollIntoView(t); return; }
            Reveal(path);
        }

        // ================================================================ tabs / filters / view
        public void SetTab(string tab) => SetTab(tab, refresh: true);

        private void SetTab(string tab, bool refresh)
        {
            if (Array.IndexOf(TabNames, tab) < 0) tab = "Explorer";
            _tab = tab;
            foreach (var child in Tabs.Children) if (child is RadioButton rb && (rb.Tag as string) == tab) rb.IsChecked = true;
            Actions.StopAudition();   // leaving (or re-entering) a tab silences the preview (WPF)
            CancelRename();
            if (refresh) RefreshNow(resetScroll: true);
        }

        private void OnTabClick(object sender, RoutedEventArgs e) => SetTab((sender as RadioButton)?.Tag as string ?? "Explorer");

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            if (_searchTimer == null)
            {
                _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
                _searchTimer.Tick += (s, a) => { _searchTimer.Stop(); _search = SearchBox.Text?.Trim() ?? ""; RefreshNow(resetScroll: true); };
            }
            _searchTimer.Stop(); _searchTimer.Start();
        }

        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { SearchBox.Text = ""; _search = ""; RefreshNow(true); e.Handled = true; }
            else if (e.Key == Key.Down || e.Key == Key.Enter)
            {
                _searchTimer?.Stop(); _search = SearchBox.Text?.Trim() ?? ""; RefreshNow(true);
                var first = _tiles.FirstOrDefault(t => !t.IsParentLink);
                if (first != null) { SelectOnly(first); FocusTile(first); }
                e.Handled = true;
            }
        }

        /// <summary>Clear search text + tag filter.</summary>
        public void ClearFilters(bool refresh = true)
        {
            _searchTimer?.Stop();
            _search = "";
            if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = "";
            _tagFilter = null;
            UpdateTagButton();
            if (refresh) RefreshNow(true);
        }

        /// <summary>Filter by name (the search box) — searches the current folder and its sub-folders.</summary>
        public void SetSearch(string text) { SearchBox.Text = text ?? ""; _searchTimer?.Stop(); _search = (text ?? "").Trim(); RefreshNow(true); }

        /// <summary>Every tag known to the project (tag service incl. the predefined ones + tags stored in .vmeta files).</summary>
        public static List<string> AllTags()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var t in AssetTagService.Instance.AllTags) set.Add(t); } catch { }
            try { foreach (var m in AssetDatabase.Instance.GetAllAssets()) if (m?.Tags != null) foreach (var t in m.Tags) if (!string.IsNullOrWhiteSpace(t)) set.Add(t); } catch { }
            return set.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void OnTagFilterClick(object sender, RoutedEventArgs e)
        {
            var m = new MenuFlyout();
            m.Items.Add(Check("All tags", _tagFilter == null, () => SetTagFilter(null)));
            var tags = AllTags();
            if (tags.Count > 0) m.Items.Add(new Separator());
            foreach (var t in tags) { var tag = t; m.Items.Add(Check(tag, string.Equals(tag, _tagFilter, StringComparison.OrdinalIgnoreCase), () => SetTagFilter(tag))); }
            m.ShowAt(TagButton);
        }

        private void UpdateTagButton()
        {
            TagLabel.Text = _tagFilter ?? "";
            TagLabel.IsVisible = _tagFilter != null;   // icon only until a tag filter is active (keeps the type tabs visible)
            if (_tagFilter != null) TagIcon.Foreground = Brush("VxAccentBrush"); else TagIcon.ClearValue(ForegroundProperty);
            ToolTip.SetTip(TagButton, _tagFilter != null ? "Showing assets tagged “" + _tagFilter + "” — click to change" : "Show only assets with a tag");
        }

        /// <summary>An asset's tags were edited: update its tooltip, re-filter when a tag filter is active.</summary>
        private void OnTagsChanged(string fullPath)
        {
            var t = _tiles.FirstOrDefault(x => AssetFileOps.PathsEqual(x.FullPath, fullPath));
            if (t != null && t.IsFileSystemItem && !t.IsFolder) t.ToolTip = BaseTip(t);
            if (_tagFilter != null) RefreshNow(resetScroll: false);
        }

        /// <summary>Show only assets carrying <paramref name="tag"/> (null = all).</summary>
        public void SetTagFilter(string tag)
        {
            _tagFilter = string.IsNullOrWhiteSpace(tag) ? null : tag;
            UpdateTagButton();
            RefreshNow(true);
        }

        private void OnSortClick(object sender, RoutedEventArgs e)
        {
            var m = new MenuFlyout();
            foreach (var (key, label) in new[] { ("Name", "Name"), ("Type", "Type"), ("Modified", "Date Modified"), ("Size", "Size") })
            {
                var k = key;
                m.Items.Add(Check(label, _settings.SortBy == k, () => SetSort(k, _settings.SortDescending)));
            }
            m.Items.Add(new Separator());
            m.Items.Add(Check("Ascending", !_settings.SortDescending, () => SetSort(_settings.SortBy, false)));
            m.Items.Add(Check("Descending", _settings.SortDescending, () => SetSort(_settings.SortBy, true)));
            m.ShowAt(SortButton);
        }

        private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
        {
            string key = (sender as Control)?.Tag as string ?? "Name";
            SetSort(key, _settings.SortBy == key ? !_settings.SortDescending : false);
        }

        public void SetSort(string by, bool descending)
        {
            _settings.SortBy = by; _settings.SortDescending = descending;
            _settings.Save();
            UpdateSortLabel();
            RefreshNow(resetScroll: false);
        }

        private void UpdateSortLabel()
        {
            SortLabel.Text = _settings.SortBy == "Modified" ? "Date" : _settings.SortBy;
            SortArrow.RenderTransform = _settings.SortDescending ? new RotateTransform(180) : null;
        }

        private void OnViewModeClick(object sender, RoutedEventArgs e) => ApplyViewMode(((sender as Control)?.Tag as string) == "List", save: true);

        /// <summary>Grid (tiles) or list (rows with type / size / date columns).</summary>
        public void ApplyViewMode(bool list, bool save = true)
        {
            _listMode = list;
            GridModeButton.IsChecked = !list; ListModeButton.IsChecked = list;
            Items.ItemTemplate = (IDataTemplate)Resources[list ? "ListRowTemplate" : "GridTileTemplate"];
            Items.ItemsPanel = (ITemplate<Panel>)Resources[list ? "ListItemsPanel" : "GridItemsPanel"];
            Items.Classes.Set("list", list);
            ListHeader.IsVisible = list;
            SizeSlider.IsEnabled = !list;
            // rebuild the containers with the new template (keeps the selection)
            var sel = SelectedTiles.ToList();
            Items.ItemsSource = null;
            Items.ItemsSource = _tiles;
            RestoreSelection(sel.Select(t => t.FullPath));
            if (save) { _settings.ViewMode = list ? "List" : "Grid"; _settings.Save(); }
        }

        private void OnSizeChanged(object sender, RangeBaseValueChangedEventArgs e) => ApplyTileSize(e.NewValue, save: true);

        private void ApplyTileSize(double size, bool save)
        {
            size = Math.Round(size);
            TileSize = size;
            TileWidth = size + 14;
            IconSize = Math.Round(size * 0.36);
            if (!save) return;
            _settings.TileSize = size;
            if (_saveTimer == null)
            {
                _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _saveTimer.Tick += (s, a) => { _saveTimer.Stop(); _settings.Save(); };
            }
            _saveTimer.Stop(); _saveTimer.Start();
        }

        // ================================================================ listing
        /// <summary>Re-list the current view, keeping unchanged tiles (thumbnails, selection, scroll).</summary>
        public void Refresh() => RefreshNow(resetScroll: false);

        private void ScheduleRefresh()
        {
            if (_refreshTimer == null)
            {
                _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
                _refreshTimer.Tick += (s, e) => { _refreshTimer.Stop(); RefreshNow(resetScroll: false); };
            }
            _refreshTimer.Stop(); _refreshTimer.Start();
        }

        private void RefreshNow(bool resetScroll)
        {
            _refreshTimer?.Stop();
            // A file change (watcher, another window saving) while a tile is being renamed would replace that tile and
            // take the inline editor + its focus with it: wait until the rename is committed or cancelled.
            if (!resetScroll && _tiles.Any(t => t.IsRenaming)) { ScheduleRefresh(); return; }
            bool hadFocus = Items.IsKeyboardFocusWithin && !(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox);
            string root = ProjectRoot;
            List<AssetTile> list;
            if (root == null) list = new List<AssetTile>();
            else
            {
                try { list = BuildListing(root); }
                catch (Exception ex) { ConsoleService.Instance.LogWarning("Asset browser: " + ex.Message); list = new List<AssetTile>(); }
                SortTiles(list);
            }
            UpdateBreadcrumb();
            SetTiles(list, resetScroll);
            UpdateEmptyState(list);
            // navigating away removes the focused tile: keep the keyboard in the list (Backspace / arrows keep working)
            if (hadFocus && !Items.IsKeyboardFocusWithin)
                Dispatcher.UIThread.Post(() =>
                {
                    if (_renaming != null || Items.IsKeyboardFocusWithin) return;
                    var sel = Items.SelectedItem;
                    if (sel != null && Items.ContainerFromItem(sel) is Control c) c.Focus(); else Items.Focus();
                }, DispatcherPriority.Background);
        }

        private List<AssetTile> BuildListing(string root)
        {
            var list = new List<AssetTile>();
            HashSet<string> tagged = _tagFilter != null ? TaggedPaths(_tagFilter) : null;
            bool Matches(string name, string fullPath) =>
                (string.IsNullOrEmpty(_search) || name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0)
                && (tagged == null || tagged.Contains(AssetTile.Normalize(fullPath)));

            if (_tab == "Explorer")
            {
                if (_folder == null || !Directory.Exists(_folder) || !AssetFileOps.IsWithin(_folder, root))
                {
                    string fallback = ExistingAncestor(_folder, root) ?? (Directory.Exists(AssetsRoot) ? AssetsRoot : root);
                    _folder = AssetTile.Normalize(fallback);
                    AssetNavigation.NavigateTo(_folder);
                }
                if (!IsFiltering)
                {
                    // ".." — up one level (not above the project root); doubles as a move-up drop target
                    string parent = Path.GetDirectoryName(_folder);
                    if (!AssetFileOps.PathsEqual(_folder, root) && parent != null && AssetFileOps.IsWithin(parent, root))
                        list.Add(ParentTile(parent));
                    foreach (var d in SafeDirs(_folder)) list.Add(FolderTile(d, null));
                    foreach (var f in SafeFiles(_folder)) list.Add(FileTile(f, fullName: true, null));
                }
                else
                {
                    // search / tag filter: the current folder and every sub-folder (Finder-style), flat results
                    foreach (var (path, isDir) in Walk(_folder))
                    {
                        string name = Path.GetFileName(path);
                        if (isDir) { if (tagged == null && Matches(name, path)) list.Add(FolderTile(path, _folder)); }
                        else if (Matches(name, path)) list.Add(FileTile(path, fullName: true, _folder));
                    }
                }
                return list;
            }

            string assets = AssetsRoot;
            IEnumerable<string> files = Directory.Exists(assets) ? Walk(assets).Where(x => !x.isDir).Select(x => x.path) : Enumerable.Empty<string>();
            Func<string, bool> filter;
            switch (_tab)
            {
                case "Meshes":
                    int order = 0;
                    foreach (var prim in AssetKinds.Primitives)
                        if (Matches(prim, "Primitive:" + prim) && tagged == null) list.Add(PrimitiveTile(prim, order++));
                    filter = f => AssetKinds.Is(Ext(f), AssetKinds.ModelExt);
                    break;
                case "Models": filter = f => AssetKinds.Is(Ext(f), AssetKinds.ModelExt); break;
                case "Textures":
                    for (int i = 0; i < BuiltInSwatches.Textures.Length; i++)
                    {
                        var b = BuiltInSwatches.Textures[i];
                        if (tagged == null && Matches(b.name, b.path)) list.Add(BuiltInTile(b.path, b.name, b.label, AssetKind.BuiltInTexture, i));
                    }
                    filter = f => AssetKinds.Is(Ext(f), AssetKinds.TextureExt);
                    break;
                case "Materials":
                    for (int i = 0; i < BuiltInSwatches.Materials.Length; i++)
                    {
                        var b = BuiltInSwatches.Materials[i];
                        if (tagged == null && Matches(b.name, b.path)) list.Add(BuiltInTile(b.path, b.name, b.label, AssetKind.BuiltInMaterial, i));
                    }
                    filter = f => Ext(f) == ".vmat";
                    break;
                case "Scripts":
                    // the scripting service is the single source of truth for scripts (Assets/Scripts, no API stub)
                    files = SafeScripts(root);
                    filter = f => true;
                    break;
                case "Audio": filter = f => AssetKinds.Is(Ext(f), AssetKinds.AudioExt) || Ext(f) == ".vsndc"; break;
                case "Prefabs": filter = f => Ext(f) == ".ventity" || Ext(f) == ".vprefab"; break;
                case "Scenes": filter = f => Ext(f) == ".vscene"; break;
                default: filter = f => false; break;
            }
            foreach (var f in files)
            {
                if (!filter(f)) continue;
                if (Matches(Path.GetFileName(f), f)) list.Add(FileTile(f, fullName: false, null));
            }
            return list;
        }

        private static string Ext(string path) => Path.GetExtension(path).ToLowerInvariant();

        private static IEnumerable<string> SafeScripts(string root)
        {
            List<string> rel;
            try { rel = ScriptingService.EnumerateScripts(); } catch { rel = new List<string>(); }
            return rel.Select(r => Path.Combine(root, r.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static string ExistingAncestor(string folder, string root)
        {
            for (string d = folder; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                if (Directory.Exists(d) && AssetFileOps.IsWithin(d, root)) return d;
            return null;
        }

        /// <summary>Visible sub-folders (no hidden / build / IDE folders — same rule as the Windows file tree).</summary>
        internal static IEnumerable<string> SafeDirs(string folder)
        {
            DirectoryInfo[] dirs;
            try { dirs = new DirectoryInfo(folder).GetDirectories(); } catch { yield break; }
            foreach (var d in dirs) if (!FileSystemItem.IsIgnoredDir(d)) yield return d.FullName;
        }

        /// <summary>Visible files (no hidden files, no .vmeta/.meta sidecars — they're engine-internal).</summary>
        internal static IEnumerable<string> SafeFiles(string folder)
        {
            FileInfo[] files;
            try { files = new DirectoryInfo(folder).GetFiles(); } catch { yield break; }
            foreach (var f in files)
            {
                if ((f.Attributes & FileAttributes.Hidden) != 0 || f.Name.StartsWith(".")) continue;
                if (f.Name.EndsWith(AssetDatabase.MetaFileExtension, StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                yield return f.FullName;
            }
        }

        /// <summary>Everything under <paramref name="folder"/> (recursive, skipping hidden/ignored folders).</summary>
        internal static IEnumerable<(string path, bool isDir)> Walk(string folder)
        {
            var stack = new Stack<string>();
            stack.Push(folder);
            int guard = 0;
            while (stack.Count > 0 && guard++ < 20000)
            {
                string dir = stack.Pop();
                foreach (var d in SafeDirs(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) { yield return (d, true); stack.Push(d); }
                foreach (var f in SafeFiles(dir)) yield return (f, false);
            }
        }

        private static IBrush Brush(string key)
        {
            var app = Application.Current;
            if (app != null && app.TryFindResource(key, app.ActualThemeVariant, out var b) && b is IBrush brush) return brush;
            return Brushes.Gray;
        }

        private static AssetTile ParentTile(string parent) => new AssetTile
        {
            Name = "..", FullPath = AssetTile.Normalize(parent), RelPath = AssetFileOps.ToRelative(parent), Kind = AssetKind.ParentFolder,
            TypeName = AssetKinds.TypeName(AssetKind.ParentFolder, null), Icon = AssetKinds.Icon(AssetKind.ParentFolder),
            IconBrush = Brush(AssetKinds.BrushKey(AssetKind.ParentFolder)), ToolTip = "Go up to " + Path.GetFileName(parent.TrimEnd('/', '\\'))
        };

        private static AssetTile FolderTile(string dir, string searchRoot)
        {
            var t = new AssetTile
            {
                Name = Path.GetFileName(dir.TrimEnd('/', '\\')), FullPath = AssetTile.Normalize(dir), RelPath = AssetFileOps.ToRelative(dir),
                Kind = AssetKind.Folder, TypeName = "Folder", Icon = "FolderFill", IconBrush = Brush(AssetKinds.BrushKey(AssetKind.Folder))
            };
            try { t.Modified = Directory.GetLastWriteTime(dir); } catch { }
            if (searchRoot != null) t.Location = LocationOf(dir, searchRoot);
            t.ToolTip = t.RelPath;
            return t;
        }

        private static AssetTile FileTile(string path, bool fullName, string searchRoot)
        {
            var fi = new FileInfo(path);
            string ext = fi.Extension.ToLowerInvariant();
            var kind = AssetKinds.Classify(path, false);
            var t = new AssetTile
            {
                Name = fullName ? fi.Name : Path.GetFileNameWithoutExtension(fi.Name),
                FullPath = AssetTile.Normalize(fi.FullName), RelPath = AssetFileOps.ToRelative(fi.FullName), Kind = kind, Extension = ext,
                TypeName = AssetKinds.TypeName(kind, ext), Icon = AssetKinds.Icon(kind, path), IconBrush = Brush(AssetKinds.BrushKey(kind)),
                Badge = AssetKinds.Badge(kind, ext)
            };
            try { t.Size = fi.Length; t.Modified = fi.LastWriteTime; } catch { }
            if (searchRoot != null) t.Location = LocationOf(Path.GetDirectoryName(path), searchRoot);
            t.ToolTip = BaseTip(t);
            return t;
        }

        private static string LocationOf(string dir, string searchRoot)
        {
            if (AssetFileOps.PathsEqual(dir, searchRoot)) return "";
            string rel = Path.GetRelativePath(searchRoot, dir).Replace('\\', '/');
            return rel.StartsWith("..") ? AssetFileOps.ToRelative(dir) : rel;
        }

        private static string BaseTip(AssetTile t)
        {
            string head = Path.GetFileName(t.FullPath);
            string info = t.TypeName + (t.Size >= 0 ? " · " + t.SizeText : "") + (t.Modified != default ? " · " + t.ModifiedText : "");
            string hint = AssetKinds.IsPlaceable(t.Kind) ? "\nDouble-click: add to scene \u00b7 " + VortexEditor.Shell.Keys.Shift + " editor \u00b7 " + VortexEditor.Shell.Keys.Cmd + " preview" : "";
            string tags = "";
            try { var list = AssetTagEditorDialog.GetTags(t.FullPath); if (list.Count > 0) tags = "\nTags: " + string.Join(", ", list); } catch { }
            return head + "\n" + info + "\n" + t.RelPath + tags + hint;
        }

        private static AssetTile PrimitiveTile(string prim, int order) => new AssetTile
        {
            Name = prim, FullPath = "Primitive:" + prim, RelPath = "Primitive:" + prim, Kind = AssetKind.Primitive, TypeName = "Primitive",
            Icon = AssetKinds.Icon(AssetKind.Primitive, prim), IconBrush = Brush(AssetKinds.BrushKey(AssetKind.Primitive)), Order = order,
            ToolTip = "Built-in " + prim.ToLowerInvariant() + "\nDouble-click or drag into the scene to add it"
        };

        private static AssetTile BuiltInTile(string path, string name, string label, AssetKind kind, int order) => new AssetTile
        {
            Name = name, FullPath = path, RelPath = path, Kind = kind, TypeName = label, Icon = AssetKinds.Icon(kind),
            IconBrush = Brush(AssetKinds.BrushKey(kind)), Order = order, ToolTip = "Built-in " + label.ToLowerInvariant() + " (read-only)"
        };

        /// <summary>Folders first (".." on top, built-ins before files), then the chosen key.</summary>
        private void SortTiles(List<AssetTile> list)
        {
            int Rank(AssetTile t) => t.IsParentLink ? 0 : t.Kind == AssetKind.Primitive || t.Kind == AssetKind.BuiltInMaterial || t.Kind == AssetKind.BuiltInTexture ? 1 : t.IsFolder ? 2 : 3;
            string by = _settings.SortBy; bool desc = _settings.SortDescending;
            list.Sort((a, b) =>
            {
                int r = Rank(a).CompareTo(Rank(b));
                if (r != 0) return r;
                if (Rank(a) == 1) return a.Order.CompareTo(b.Order);   // built-ins keep their declared order
                int c;
                switch (by)
                {
                    case "Type": c = string.Compare(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase); if (c == 0) c = NaturalCompare(a.Name, b.Name); break;
                    case "Modified": c = a.Modified.CompareTo(b.Modified); if (c == 0) c = NaturalCompare(a.Name, b.Name); break;
                    case "Size": c = a.Size.CompareTo(b.Size); if (c == 0) c = NaturalCompare(a.Name, b.Name); break;
                    default: c = NaturalCompare(a.Name, b.Name); if (c == 0) c = string.Compare(a.Location, b.Location, StringComparison.OrdinalIgnoreCase); break;
                }
                return desc ? -c : c;
            });
        }

        /// <summary>Finder-like ordering: "Tile 2" before "Tile 10", case-insensitive.</summary>
        internal static int NaturalCompare(string a, string b)
        {
            a ??= ""; b ??= "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a.Substring(si, i - si).TrimStart('0'), nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                    int cmp = string.CompareOrdinal(na, nb);
                    if (cmp != 0) return cmp;
                }
                else
                {
                    int cmp = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                    if (cmp != 0) return cmp;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }

        /// <summary>Paths (normalized, absolute) of every asset carrying <paramref name="tag"/> (tag service + .vmeta tags).</summary>
        private static HashSet<string> TaggedPaths(string tag)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string root = ProjectRoot;
            if (root == null) return set;
            try
            {
                // sidecars written on Windows store "Assets\Textures\x.png": normalise the separators first
                string Full(string rel) => AssetTile.Normalize(Path.Combine(root, rel.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
                var db = AssetDatabase.Instance;
                foreach (var guid in AssetTagService.Instance.FindAssetsByTag(tag))
                {
                    string rel = db.GetAsset(guid)?.RelativePath;
                    if (!string.IsNullOrEmpty(rel)) set.Add(Full(rel));
                }
                foreach (var meta in db.GetAllAssets())
                    if (meta?.Tags != null && meta.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrEmpty(meta.RelativePath))
                        set.Add(Full(meta.RelativePath));
            }
            catch { }
            return set;
        }

        /// <summary>Apply a fresh listing: unchanged tiles are kept (thumbnail, selection, container), so a refresh
        /// after a file change doesn't flicker; a navigation replaces everything and scrolls to the top.</summary>
        private void SetTiles(List<AssetTile> fresh, bool reset)
        {
            var selectedPaths = SelectedTiles.Select(t => t.FullPath).ToList();
            if (reset)
            {
                _tiles.Clear();
                foreach (var t in fresh) _tiles.Add(t);
                selectedPaths.Clear();
                if (_tiles.Count > 0) Dispatcher.UIThread.Post(() => { if (_tiles.Count > 0) Items.ScrollIntoView(_tiles[0]); }, DispatcherPriority.Background);
            }
            else
            {
                var old = new Dictionary<string, AssetTile>();
                foreach (var t in _tiles) old[t.Key] = t;
                for (int i = 0; i < fresh.Count; i++)
                    if (old.TryGetValue(fresh[i].Key, out var o) && o.SameContent(fresh[i]) && !o.IsRenaming) fresh[i] = o;
                for (int i = 0; i < fresh.Count; i++)
                {
                    if (i < _tiles.Count && ReferenceEquals(_tiles[i], fresh[i])) continue;
                    int j = -1;
                    for (int k = i + 1; k < _tiles.Count; k++) if (ReferenceEquals(_tiles[k], fresh[i])) { j = k; break; }
                    if (j >= 0) _tiles.Move(j, i); else _tiles.Insert(i, fresh[i]);
                }
                while (_tiles.Count > fresh.Count) _tiles.RemoveAt(_tiles.Count - 1);
            }

            if (_pendingSelect != null && _pendingSelect.Count > 0) { selectedPaths = _pendingSelect; _pendingSelect = null; }
            RestoreSelection(selectedPaths, scroll: true);
            // the auditioned clip is no longer the (single) selection — e.g. the browser moved to another folder
            var now = SelectedTiles.Take(2).ToList();
            if (!(now.Count == 1 && (now[0].Kind == AssetKind.AudioClip || now[0].Kind == AssetKind.SoundContainer))) Actions.StopAudition();
            if (_pendingRename != null)
            {
                var t = _tiles.FirstOrDefault(x => AssetFileOps.PathsEqual(x.FullPath, _pendingRename));
                _pendingRename = null;
                if (t != null) BeginRename(t);
            }
        }

        private void RestoreSelection(IEnumerable<string> paths, bool scroll = false)
        {
            var want = new HashSet<string>(paths.Where(p => p != null).Select(AssetTile.Normalize), StringComparer.OrdinalIgnoreCase);
            _restoringSelection = true;
            try
            {
                var sel = Items.Selection;
                sel.BeginBatchUpdate();
                try
                {
                    sel.Clear();
                    AssetTile last = null;
                    for (int i = 0; i < _tiles.Count; i++)
                        if (want.Contains(AssetTile.Normalize(_tiles[i].FullPath))) { sel.Select(i); last = _tiles[i]; }
                    if (scroll && last != null) Dispatcher.UIThread.Post(() => Items.ScrollIntoView(last), DispatcherPriority.Background);
                }
                finally { sel.EndBatchUpdate(); }
            }
            finally { _restoringSelection = false; }
        }

        private void UpdateEmptyState(List<AssetTile> list)
        {
            bool empty = list.Count == 0 || list.All(t => t.IsParentLink);
            EmptyState.IsVisible = empty;
            if (!empty) return;
            EmptyIcon.Icon = "Folder";
            if (ProjectRoot == null) { EmptyHint.Text = "No project open"; EmptySubHint.Text = "Open or create a project to browse its assets."; return; }
            if (IsFiltering)
            {
                EmptyIcon.Icon = "Search";
                EmptyHint.Text = !string.IsNullOrEmpty(_search) ? "No assets match “" + _search + "”" : "No assets tagged “" + _tagFilter + "”";
                EmptySubHint.Text = _tab == "Explorer" ? "Searched " + Path.GetFileName(_folder) + " and its sub-folders." : "Searched every " + _tab.ToLowerInvariant() + " asset in the project.";
                return;
            }
            switch (_tab)
            {
                case "Explorer": EmptyHint.Text = "This folder is empty"; EmptySubHint.Text = "Drop files here from Finder, or use Create / Import."; break;
                case "Models": case "Meshes": EmptyHint.Text = "No models imported yet"; EmptySubHint.Text = "Drag & drop models here or use Import."; break;
                default: EmptyHint.Text = "No " + _tab.ToLowerInvariant() + " in this project yet"; EmptySubHint.Text = "Use Create or Import to add some."; break;
            }
        }

        // ================================================================ breadcrumb
        private void UpdateBreadcrumb()
        {
            Breadcrumb.Children.Clear();
            string root = ProjectRoot;
            if (root == null) { Breadcrumb.Children.Add(new TextBlock { Text = "No project", Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) }); return; }
            var segs = new List<(string label, string path)> { (ProjectData.Current?.Name ?? Path.GetFileName(root.TrimEnd('/', '\\')), root) };
            string folder = _folder ?? root;
            if (AssetFileOps.IsWithin(folder, root) && !AssetFileOps.PathsEqual(folder, root))
            {
                string acc = root;
                foreach (var part in Path.GetRelativePath(root, folder).Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    acc = Path.Combine(acc, part);
                    segs.Add((part, acc));
                }
            }
            bool explorer = _tab == "Explorer";
            for (int i = 0; i < segs.Count; i++)
            {
                bool last = i == segs.Count - 1;
                string target = segs[i].path;
                var b = new Button { Content = segs[i].label, Classes = { "ghost", "crumb" }, Tag = target, FontWeight = last && explorer ? FontWeight.SemiBold : FontWeight.Normal };
                if (!(last && explorer)) b.Foreground = Brush("VxTextSecondaryBrush");
                ToolTip.SetTip(b, AssetFileOps.ToRelative(target) + "\nDrop items here to move them into this folder");
                b.Click += (s, e) => Navigate(target);
                DragDrop.SetAllowDrop(b, true);
                b.AddHandler(DragDrop.DragOverEvent, (s, e) =>
                {
                    var mv = AssetDragData.MovePaths(e.Data);
                    bool ok = mv != null && AssetFileOps.CanMoveInto(mv, target);
                    e.DragEffects = ok ? DragDropEffects.Move : DragDropEffects.None;
                    b.Classes.Set("droptarget", ok);
                    e.Handled = true;
                });
                b.AddHandler(DragDrop.DragLeaveEvent, (s, e) => b.Classes.Set("droptarget", false));
                b.AddHandler(DragDrop.DropEvent, (s, e) =>
                {
                    b.Classes.Set("droptarget", false);
                    var mv = AssetDragData.MovePaths(e.Data);
                    if (mv != null) MoveInto(mv, target);
                    e.Handled = true;
                });
                Breadcrumb.Children.Add(b);
                if (!last || !explorer) Breadcrumb.Children.Add(new VxIcon { Icon = "ChevronRight", Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("VxTextTertiaryBrush"), Margin = new Thickness(1, 0) });
            }
            if (!explorer)
                Breadcrumb.Children.Add(new TextBlock { Text = _tab == "Meshes" ? "Meshes (primitives + models)" : "All " + _tab, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0) });
            if (IsFiltering)
                Breadcrumb.Children.Add(new TextBlock { Text = "· search results", Classes = { "tertiary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) });
            Dispatcher.UIThread.Post(() => BreadcrumbScroll.ScrollToEnd(), DispatcherPriority.Background);
        }

        // ================================================================ thumbnails
        private double RenderScale => TopLevel.GetTopLevel(this)?.RenderScaling ?? 2.0;

        /// <summary>Pixel size of thumbnail requests: crisp on Retina for big tiles, cheap for small tiles / rows.</summary>
        private int ThumbPixelSize => _listMode ? 128 : (TileSize * RenderScale > 150 ? 256 : 128);

        /// <summary>A tile scrolled into (or near) view: load its thumbnail (only visible tiles cost anything).</summary>
        private void OnTileViewportChanged(object sender, EffectiveViewportChangedEventArgs e)
        {
            if (!(sender is Control c) || !(c.DataContext is AssetTile t)) return;
            var vp = e.EffectiveViewport;
            if (vp.Width <= 0 || vp.Height <= 0) return;
            if (!vp.Inflate(240).Intersects(new Rect(c.Bounds.Size))) return;
            EnsureThumbnail(t);
        }

        internal void EnsureThumbnail(AssetTile t, bool force = false)
        {
            if (t == null) return;
            int size = ThumbPixelSize;
            if (!force && t.RequestedSize >= size) return;
            t.RequestedSize = size;
            switch (t.Kind)
            {
                case AssetKind.AudioClip:
                    WaveformThumbs.Request(t.FullPath, (bmp, tip) =>
                    {
                        if (bmp != null) t.Thumbnail = bmp;
                        if (tip != null) t.ToolTip = Path.GetFileName(t.FullPath) + "\n" + tip + "\n" + t.RelPath + "\nClick to audition · drag onto an Audio Source";
                    });
                    return;
                case AssetKind.SoundContainer:
                    t.ToolTip = Path.GetFileName(t.FullPath) + "\n" + WaveformThumbs.ContainerTip(t.FullPath) + "\n" + t.RelPath;
                    return;
                case AssetKind.BuiltInMaterial:
                case AssetKind.BuiltInTexture:
                    t.Thumbnail = BuiltInSwatches.For(t.FullPath);
                    return;
                case AssetKind.Terrain:
                case AssetKind.Foliage:
                    // the world data files (#124 / #125): a shaded relief of the heights, a map of the painted instances
                    WorldThumbs.Request(t.FullPath, Math.Max(96, size), (bmp, tip) =>
                    {
                        if (bmp != null) t.Thumbnail = bmp;
                        if (tip != null) t.ToolTip = Path.GetFileName(t.FullPath) + "\n" + tip + "\n" + t.RelPath;
                    });
                    return;
            }
            if (ThumbnailService.KindOf(t.FullPath) == ThumbnailService.Kind.None) return;
            var hit = ThumbnailService.TryGet(t.FullPath, size);
            if (hit != null) { t.Thumbnail = hit; return; }
            // a bigger render already in memory serves a small tile / list row as well (no second render)
            var bigger = size < 256 ? ThumbnailService.TryGet(t.FullPath, 256) : null;
            if (bigger != null) { t.Thumbnail = bigger; return; }
            ThumbnailService.Request(t.FullPath, size, bmp => { if (bmp != null) t.Thumbnail = bmp; });
        }

        // ================================================================ file-system changes
        private void OnAssetsChanged(AssetChanges ch)
        {
            // (changed files were invalidated in ThumbnailService -> OnThumbnailInvalidated re-requests visible tiles)
            if (_tab != "Explorer" || IsFiltering || _folder == null || ch.Touches(_folder)) ScheduleRefresh();
            if (ch.Structural) ScheduleDatabaseRefresh();
        }

        /// <summary>Keep asset GUIDs / tags current after files appeared or vanished (writes missing .vmeta sidecars).</summary>
        private void ScheduleDatabaseRefresh()
        {
            if (_dbTimer == null)
            {
                _dbTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                _dbTimer.Tick += (s, e) => { _dbTimer.Stop(); try { AssetDatabase.Instance.Refresh(); } catch { } };
            }
            _dbTimer.Stop(); _dbTimer.Start();
        }

        private static void RefreshDatabase() { try { AssetDatabase.Instance.Refresh(); } catch { } }

        // ================================================================ toolbar
        private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();
        private void OnForwardClick(object sender, RoutedEventArgs e) => GoForward();
        private void OnRefreshClick(object sender, RoutedEventArgs e) { RefreshDatabase(); RefreshNow(false); }
        private async void OnImportClick(object sender, RoutedEventArgs e) => await ImportViaPicker();
        private void OnCreateClick(object sender, RoutedEventArgs e)
        {
            var m = new MenuFlyout();
            foreach (var item in CreateItems()) m.Items.Add(item);
            m.ShowAt(CreateButton);
        }

        // ================================================================ selection helpers
        private AssetTile TileFromSource(object source)
        {
            for (var v = source as Visual; v != null && !ReferenceEquals(v, Items); v = v.GetVisualParent())
                if (v is ListBoxItem lbi) return lbi.DataContext as AssetTile;
            return null;
        }

        private static bool IsInside<T>(object source, Visual stop) where T : class
        {
            for (var v = source as Visual; v != null && !ReferenceEquals(v, stop); v = v.GetVisualParent())
                if (v is T) return true;
            return false;
        }

        public void SelectOnly(AssetTile t)
        {
            int i = _tiles.IndexOf(t);
            if (i < 0) return;
            var sel = Items.Selection;
            sel.BeginBatchUpdate();
            try { sel.Clear(); sel.Select(i); } finally { sel.EndBatchUpdate(); }
        }

        public void SelectAll()
        {
            var sel = Items.Selection;
            sel.BeginBatchUpdate();
            try { sel.Clear(); for (int i = 0; i < _tiles.Count; i++) if (!_tiles[i].IsParentLink) sel.Select(i); } finally { sel.EndBatchUpdate(); }
        }

        private void FocusTile(AssetTile t)
        {
            if (t == null) return;
            Items.ScrollIntoView(t);
            Dispatcher.UIThread.Post(() => (Items.ContainerFromItem(t) as Control)?.Focus(), DispatcherPriority.Background);
        }

        /// <summary>The tile keyboard commands act on: the focused tile when selected, else the first selected.</summary>
        private AssetTile PrimaryTile()
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
            var ft = focused?.DataContext as AssetTile;
            if (ft != null && Items.SelectedItems != null && Items.SelectedItems.Contains(ft)) return ft;
            return Items.SelectedItem as AssetTile;
        }

        private void OnItemsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_restoringSelection || _marquee) return;
            // Audio audition (WPF): selecting a clip plays it, selecting anything else stops it.
            var sel = SelectedTiles.Take(2).ToList();
            if (sel.Count == 1 && (sel[0].Kind == AssetKind.AudioClip || sel[0].Kind == AssetKind.SoundContainer)) Actions.Audition(sel[0].FullPath);
            else Actions.StopAudition();
        }

        // ================================================================ pointer: drag, press-on-selection, rubber band
        private void OnItemsPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (IsInside<TextBox>(e.Source, Items)) return;
            var point = e.GetCurrentPoint(Items);
            _selectOnRelease = null; _dragArmed = false; _pressTile = null;
            if (!point.Properties.IsLeftButtonPressed) return;   // right button: the list selects, ContextRequested opens the menu
            if (_renaming != null) CommitRename();
            var tile = TileFromSource(e.Source);
            bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (tile == null)
            {
                if (IsInside<ScrollBar>(e.Source, Items)) return;
                StartMarquee(e, point.Position, additive: cmd || shift);
                e.Handled = true;
                return;
            }
            // only a genuine single press arms a drag — never the 2nd press of a double-click (it would swallow the
            // double-click) and never a modified press (⌘/⇧ are selection / open-in-editor gestures, not placement)
            if (e.ClickCount >= 2 || cmd || shift) return;
            _pressTile = tile; _pressPos = point.Position; _dragArmed = true;
            if (Items.SelectedItems != null && Items.SelectedItems.Contains(tile) && Items.SelectedItems.Count > 1)
            {
                // keep the multi-selection so it can be dragged as a whole; a click without a drag selects just this tile
                _selectOnRelease = tile;
                e.Handled = true;
                (Items.ContainerFromItem(tile) as Control)?.Focus();
            }
        }

        private async void OnItemsPointerMoved(object sender, PointerEventArgs e)
        {
            var point = e.GetCurrentPoint(Items);
            if (_marquee) { UpdateMarquee(point.Position); return; }
            if (!_dragArmed || _dragging || _pressTile == null) return;
            if (!point.Properties.IsLeftButtonPressed) { _dragArmed = false; return; }
            var d = point.Position - _pressPos;
            if (Math.Abs(d.X) < 5 && Math.Abs(d.Y) < 5) return;
            var press = _pressTile;
            _dragArmed = false; _selectOnRelease = null;
            if (press.IsParentLink) return;
            var dragged = new List<AssetTile> { press };
            if (Items.SelectedItems != null && Items.SelectedItems.Contains(press))
                dragged.AddRange(SelectedTiles.Where(t => !ReferenceEquals(t, press) && !t.IsParentLink));
            _dragging = true;
            try { await DragDrop.DoDragDrop(e, AssetDragData.Create(dragged), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
            catch { }
            finally { _dragging = false; _pressTile = null; SetDropHighlight(null); }
        }

        private void OnItemsPointerReleased(object sender, PointerReleasedEventArgs e)
        {
            if (_marquee) { EndMarquee(); e.Handled = true; return; }
            if (_selectOnRelease != null && !_dragging) SelectOnly(_selectOnRelease);
            _selectOnRelease = null; _dragArmed = false; _pressTile = null;
        }

        private void StartMarquee(PointerPressedEventArgs e, Point p, bool additive)
        {
            _marquee = true;
            _marqueeStart = p;
            _marqueeBase = additive ? SelectedTiles.ToList() : new List<AssetTile>();
            if (!additive) Items.Selection.Clear();
            e.Pointer.Capture(Items);
            Canvas.SetLeft(Marquee, p.X); Canvas.SetTop(Marquee, p.Y);
            Marquee.Width = 0; Marquee.Height = 0;
            Marquee.IsVisible = true;
            Items.Focus();
            Actions.StopAudition();
        }

        private void UpdateMarquee(Point p)
        {
            var rect = new Rect(Math.Min(_marqueeStart.X, p.X), Math.Min(_marqueeStart.Y, p.Y), Math.Abs(p.X - _marqueeStart.X), Math.Abs(p.Y - _marqueeStart.Y));
            Canvas.SetLeft(Marquee, rect.X); Canvas.SetTop(Marquee, rect.Y);
            Marquee.Width = rect.Width; Marquee.Height = rect.Height;
            var hits = new HashSet<AssetTile>(_marqueeBase);
            foreach (var c in Items.GetRealizedContainers())
            {
                if (!(c.DataContext is AssetTile t) || t.IsParentLink) continue;
                var tl = c.TranslatePoint(new Point(0, 0), Items);
                if (tl.HasValue && rect.Intersects(new Rect(tl.Value, c.Bounds.Size))) hits.Add(t);
            }
            var sel = Items.Selection;
            sel.BeginBatchUpdate();
            try { sel.Clear(); for (int i = 0; i < _tiles.Count; i++) if (hits.Contains(_tiles[i])) sel.Select(i); }
            finally { sel.EndBatchUpdate(); }
        }

        private void EndMarquee()
        {
            if (!_marquee) return;
            _marquee = false;
            Marquee.IsVisible = false;
            _marqueeBase = null;
        }

        // ================================================================ open (double-click convention)
        private async void OnItemsDoubleTapped(object sender, TappedEventArgs e)
        {
            if (IsInside<TextBox>(e.Source, Items)) return;
            var tile = TileFromSource(e.Source);
            if (tile == null) return;
            e.Handled = true;
            await OpenTile(tile, e.KeyModifiers);
        }

        /// <summary>The Windows double-click convention: plain = default action, ⇧ = the asset's editor,
        /// ⌘ or Ctrl = large preview. Folders navigate, ".." goes up.</summary>
        public async Task OpenTile(AssetTile tile, KeyModifiers mods)
        {
            if (tile == null) return;
            if (tile.IsParentLink) { Navigate(tile.FullPath); return; }
            if (tile.Kind == AssetKind.Folder) { Navigate(tile.FullPath); return; }
            if (tile.Kind == AssetKind.BuiltInMaterial || tile.Kind == AssetKind.BuiltInTexture)
            { EditorCommands.Toast(tile.Name + " is a built-in " + (tile.Kind == AssetKind.BuiltInMaterial ? "material" : "texture") + " (read-only)"); return; }
            bool shift = mods.HasFlag(KeyModifiers.Shift);
            bool cmd = mods.HasFlag(KeyModifiers.Meta) || mods.HasFlag(KeyModifiers.Control);
            if (tile.Kind == AssetKind.Terrain || tile.Kind == AssetKind.Foliage) { SelectWorldDataUser(tile); return; }
            bool handled = false;
            try
            {
                if (shift) handled = EditorWindows.OpenEditorFor(tile.FullPath);
                else if (cmd) handled = EditorWindows.OpenLargePreview(tile.FullPath);
            }
            catch (Exception ex) { EditorCommands.Fail("Open", ex); handled = true; }
            if (!handled) await Actions.OpenDefault(tile.FullPath);
            // WPF: opening an asset from a type tab reveals its folder in the file tree
            if (_tab != "Explorer" && tile.IsFileSystemItem)
            {
                string dir = Path.GetDirectoryName(tile.FullPath);
                if (Directory.Exists(dir) && !AssetFileOps.PathsEqual(dir, _folder)) { _folder = AssetTile.Normalize(dir); AssetNavigation.NavigateTo(_folder); UpdateBreadcrumb(); }
            }
        }

        // ================================================================ keyboard
        private async void OnPanelKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Source is TextBox) return;   // search box + inline rename handle their own keys
            bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (cmd && e.Key == Key.OemOpenBrackets) { GoBack(); e.Handled = true; return; }
            if (cmd && e.Key == Key.OemCloseBrackets) { GoForward(); e.Handled = true; return; }
            if (!Items.IsKeyboardFocusWithin) return;
            var primary = PrimaryTile();
            switch (e.Key)
            {
                case Key.Delete:
                    e.Handled = true; await DeleteSelectedAsync(); break;
                case Key.Back:
                    e.Handled = true;
                    if (cmd) await DeleteSelectedAsync();      // ⌘⌫ = move to Trash (Finder)
                    else if (_tab == "Explorer") NavigateUp();   // ⌫ = up one level
                    break;
                case Key.F2:
                    e.Handled = true; if (primary != null) BeginRename(primary); break;
                case Key.Enter:
                    e.Handled = true;
                    if (primary == null) break;
                    if (cmd || primary.IsFolder || primary.IsVirtual) await OpenTile(primary, KeyModifiers.None);   // Enter on a folder opens it
                    else BeginRename(primary);                                                                      // Enter on a file renames (Finder)
                    break;
                case Key.D:
                    if (cmd) { e.Handled = true; DuplicateSelected(); }
                    break;
                case Key.O:
                    if (cmd && primary != null) { e.Handled = true; await OpenTile(primary, shift ? KeyModifiers.Shift : KeyModifiers.None); }
                    break;
                case Key.Down:
                    if (cmd && primary != null) { e.Handled = true; await OpenTile(primary, KeyModifiers.None); }
                    break;
                case Key.Up:
                    if (cmd) { e.Handled = true; NavigateUp(); }
                    break;
                case Key.A:
                    if (cmd) { e.Handled = true; SelectAll(); }
                    break;
                case Key.Space:
                    e.Handled = true;
                    if (primary == null) break;
                    if (primary.Kind == AssetKind.AudioClip || primary.Kind == AssetKind.SoundContainer) Actions.Audition(primary.FullPath);
                    else EditorWindows.OpenLargePreview(primary.FullPath);   // Quick Look
                    break;
                case Key.Escape:
                    e.Handled = true; Items.Selection.Clear(); Actions.StopAudition(); break;
            }
        }

        /// <summary>Edit-menu command while the item list has focus (see <see cref="EditorCommands.RegisterEditHandler"/>).
        /// Undo/Redo stay global: file operations are on the editor's undo stack.</summary>
        private bool OnEditCommand(EditorCommands.EditAction a)
        {
            var primary = PrimaryTile();
            switch (a)
            {
                case EditorCommands.EditAction.Duplicate: DuplicateSelected(); return true;
                case EditorCommands.EditAction.Delete: _ = DeleteSelectedAsync(); return true;
                case EditorCommands.EditAction.SelectAll: SelectAll(); return true;
                case EditorCommands.EditAction.Rename: if (primary != null) BeginRename(primary); return true;
                case EditorCommands.EditAction.Copy:
                {
                    var rel = SelectedTiles.Where(t => t.IsFileSystemItem).Select(t => t.RelPath).ToList();
                    if (rel.Count > 0) _ = CopyText(string.Join("\n", rel));   // project-relative paths, for scripts
                    return true;
                }
                default: return false;
            }
        }

        /// <summary>True while the keyboard focus is in the browser's item list (menu commands route here then).</summary>
        public bool HasItemFocus => Items.IsKeyboardFocusWithin;

        // ================================================================ rename (inline)
        public void BeginRename(AssetTile t)
        {
            if (t == null || !t.IsFileSystemItem) return;
            if (_renaming != null && !ReferenceEquals(_renaming, t)) CommitRename();
            SelectOnly(t);
            t.EditName = Path.GetFileName(t.FullPath);
            t.IsRenaming = true;
            _renaming = t;
            Items.ScrollIntoView(t);
            Dispatcher.UIThread.Post(() => FocusRenameBox(t), DispatcherPriority.Background);
        }

        /// <summary>Start renaming the selected asset (F2 / menu).</summary>
        public void RenameSelected() => BeginRename(PrimaryTile());

        private void FocusRenameBox(AssetTile t)
        {
            if (!t.IsRenaming) return;
            var box = (Items.ContainerFromItem(t) as Control)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.IsVisible);
            if (box == null) return;
            box.Focus();
            string text = box.Text ?? "";
            int stem = t.IsFolder ? text.Length : Path.GetFileNameWithoutExtension(text).Length;
            box.SelectionStart = 0;
            box.SelectionEnd = Math.Max(0, Math.Min(stem, text.Length));
        }

        private void OnRenameKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { var t = _renaming; CommitRename(); e.Handled = true; if (t != null) FocusAfterRename(); }
            else if (e.Key == Key.Escape) { CancelRename(); e.Handled = true; FocusAfterRename(); }
        }

        private void FocusAfterRename() => Dispatcher.UIThread.Post(() =>
        {
            var t = Items.SelectedItem as AssetTile;
            if (t != null) (Items.ContainerFromItem(t) as Control)?.Focus(); else Items.Focus();
        }, DispatcherPriority.Background);

        private void OnRenameLostFocus(object sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.DataContext is AssetTile t && ReferenceEquals(t, _renaming)) CommitRename();
        }

        private void CommitRename()
        {
            var t = _renaming;
            if (t == null) return;
            _renaming = null;
            t.IsRenaming = false;
            string newName = t.EditName?.Trim();
            if (string.IsNullOrEmpty(newName) || newName == Path.GetFileName(t.FullPath)) return;
            try
            {
                string np = AssetFileOps.Rename(t.FullPath, newName);
                RefreshDatabase();
                _pendingSelect = new List<string> { np };
                RefreshNow(resetScroll: false);
            }
            catch (Exception ex) { EditorCommands.Toast(ex.Message); }
        }

        private void CancelRename()
        {
            var t = _renaming;
            _renaming = null;
            if (t != null) t.IsRenaming = false;
        }

        // ================================================================ delete / duplicate / move / reimport
        /// <summary>Delete the selected assets after a confirmation (moved to the Trash; undoable).</summary>
        public async Task DeleteSelectedAsync(bool confirm = true)
        {
            var sel = SelectedTiles.Where(t => t.IsFileSystemItem).ToList();
            if (sel.Count == 0) return;
            if (confirm)
            {
                string title = sel.Count == 1 ? "Delete “" + Path.GetFileName(sel[0].FullPath) + "”?" : "Delete " + sel.Count + " items?";
                string msg = (SystemTrash.IsSupported ? "The items are moved to the Trash." : "The items are deleted.") + " Undo (" + VortexEditor.Shell.Keys.Chord("Z") + ") restores them.";
                if (sel.Any(t => t.Kind == AssetKind.Prefab || t.IsFolder)) msg += " Scene instances of deleted prefabs are removed too.";
                if (!await Dialogs.Confirm(title, msg, "Delete", "Cancel", destructive: true)) return;
            }
            DeletePaths(sel.Select(t => t.FullPath).ToList());
        }

        /// <summary>Delete without asking (smoke tests, programmatic use). Returns the deleted paths.</summary>
        public List<string> DeletePaths(List<string> paths)
        {
            CancelRename();
            Actions.StopAudition();
            var prefabRelated = paths.Where(p => Directory.Exists(p) || p.EndsWith(PrefabService.PrefabExtension, StringComparison.OrdinalIgnoreCase))
                                     .Select(p => (path: p, dir: Directory.Exists(p))).ToList();
            List<string> deleted;
            try { deleted = AssetFileOps.Delete(paths); }
            catch (Exception ex) { EditorCommands.Fail("Delete", ex); return new List<string>(); }
            int removed = 0;
            foreach (var (p, dir) in prefabRelated)
                if (deleted.Any(d => AssetFileOps.PathsEqual(d, p)))
                    try { removed += PrefabService.Instance.OnPrefabDeleted(p, dir); } catch { }
            if (removed > 0) EditorCommands.Toast("Removed " + removed + " prefab instance" + (removed == 1 ? "" : "s") + " (source deleted)");
            RefreshDatabase();
            RefreshNow(resetScroll: false);
            return deleted;
        }

        /// <summary>Duplicate the selected assets next to themselves (⌘D; undoable).</summary>
        public List<string> DuplicateSelected()
        {
            var sel = SelectedTiles.Where(t => t.IsFileSystemItem).Select(t => t.FullPath).ToList();
            if (sel.Count == 0) return new List<string>();
            List<string> created;
            try { created = AssetFileOps.Duplicate(sel); }
            catch (Exception ex) { EditorCommands.Fail("Duplicate", ex); return new List<string>(); }
            RefreshDatabase();
            _pendingSelect = created;
            RefreshNow(resetScroll: false);
            if (created.Count > 0) EditorCommands.Toast(created.Count == 1 ? "Duplicated as " + Path.GetFileName(created[0]) : "Duplicated " + created.Count + " items");
            return created;
        }

        /// <summary>Move items into a folder (undoable; .vmeta sidecars follow).</summary>
        public List<string> MoveInto(IEnumerable<string> sources, string destDir)
        {
            List<string> moved;
            try { moved = AssetFileOps.Move(sources, destDir); }
            catch (Exception ex) { EditorCommands.Fail("Move", ex); return new List<string>(); }
            if (moved.Count > 0)
            {
                EditorCommands.Toast("Moved " + (moved.Count == 1 ? Path.GetFileName(moved[0]) : moved.Count + " items") + " to " + Path.GetFileName(destDir.TrimEnd('/', '\\')));
                RefreshDatabase();
            }
            RefreshNow(resetScroll: false);
            return moved;
        }

        /// <summary>Re-read assets from disk: fresh thumbnails + metadata, rebuilt materials, reloaded prefab instances.</summary>
        public void Reimport(IEnumerable<string> paths)
        {
            var files = new List<string>();
            foreach (var p in paths)
            {
                if (Directory.Exists(p)) files.AddRange(Walk(p).Where(x => !x.isDir).Select(x => x.path).Take(5000));
                else if (File.Exists(p)) files.Add(p);
            }
            foreach (var f in files)
            {
                ThumbnailService.Invalidate(f);   // memory + disk cache -> the preview is rendered again
                string ext = Ext(f);
                try
                {
                    if (ext == ".vmat") MaterialService.Instance.InvalidateVortexMaterial(f);
                    else if (ext == ".ventity" || ext == ".vprefab") PrefabService.Instance.ReloadInstancesFromPrefab(f);
                    else if (AssetKinds.Is(ext, AssetKinds.ModelExt))
                    {
                        string matDir = Path.Combine(Path.GetDirectoryName(f), "materials");
                        if (Directory.Exists(matDir)) foreach (var v in Directory.GetFiles(matDir, "*.vmat")) MaterialService.Instance.InvalidateVortexMaterial(v);
                    }
                }
                catch { }
            }
            RefreshDatabase();
            SceneRenderService.RuntimeDirty = true;
            Editor.Core.Viewport.EditorViewportSession.RequestResubmit();
            RefreshNow(resetScroll: false);
            EditorCommands.Toast("Reimported " + files.Count + (files.Count == 1 ? " asset" : " assets"));
        }

        /// <summary>A thumbnail was invalidated (asset changed, reimport, material edit): tiles of that asset that are
        /// on screen load it again; off-screen ones reload when they scroll into view.</summary>
        /// <summary>A .vterrain / .vfoliage opens as its entity: select and frame the Terrain / Foliage that uses it (the sculpt and
        /// paint tools live in its inspector); a file no entity uses says how to put it into the scene.</summary>
        private void SelectWorldDataUser(AssetTile tile)
        {
            var user = WorldThumbs.UserOf(tile.FullPath);
            bool terrain = tile.Kind == AssetKind.Terrain;
            if (user == null)
            {
                EditorCommands.Toast("No " + (terrain ? "Terrain" : "Foliage") + " in the open scene uses " + tile.Name + " — Create ▸ " + (terrain ? "Terrain" : "Foliage") + ", then pick it as the data file");
                return;
            }
            try
            {
                SelectionService.Instance.SelectedEntity = user;
                SelectionService.Instance.RequestFocus(user);
                EditorCommands.Toast((terrain ? "Terrain" : "Foliage") + " \"" + user.Name + "\" selected — " + (terrain ? "sculpt, paint, import and export in the Inspector" : "paint and erase with the brush in the Inspector"));
            }
            catch (Exception ex) { EditorCommands.Fail("Select", ex); }
        }

        private void OnThumbnailInvalidated(string fullPath)
        {
            if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnThumbnailInvalidated(fullPath)); return; }
            if (WorldThumbs.Handles(fullPath)) WorldThumbs.Forget(fullPath);
            foreach (var t in _tiles)
            {
                if (!AssetFileOps.PathsEqual(t.FullPath, fullPath)) continue;
                t.RequestedSize = 0;
                var c = Items.ContainerFromItem(t) as Control;
                if (c != null && c.IsEffectivelyVisible) EnsureThumbnail(t, force: true);
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            ThumbnailService.Invalidated -= OnThumbnailInvalidated;
            ThumbnailService.Invalidated += OnThumbnailInvalidated;
            if (_wasDetached)
            {
                // hidden behind another bottom tab meanwhile: invalidations were missed, so every tile checks the
                // thumbnail cache again when it is laid out (unchanged previews are memory hits; stale ones re-render)
                _wasDetached = false;
                foreach (var t in _tiles) t.RequestedSize = 0;
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            ThumbnailService.Invalidated -= OnThumbnailInvalidated;
            _wasDetached = true;
            base.OnDetachedFromVisualTree(e);
        }
        private bool _wasDetached;

        // ================================================================ drop targets
        /// <summary>Where a drop on <paramref name="tile"/> lands: a folder tile / "..", else the listed folder.</summary>
        private string DropFolder(AssetTile tile, bool forImport)
        {
            if (tile != null && tile.IsFolder) return tile.FullPath;
            if (_tab == "Explorer" && _folder != null && !IsFiltering) return _folder;
            return forImport ? ImportTargetFolder() : null;
        }

        private void SetDropHighlight(AssetTile t)
        {
            if (ReferenceEquals(_dropHighlight, t)) return;
            if (_dropHighlight != null) _dropHighlight.IsDropTarget = false;
            _dropHighlight = t;
            if (t != null) t.IsDropTarget = true;
        }

        private void OnItemsDragOver(object sender, DragEventArgs e)
        {
            var tile = TileFromSource(e.Source);
            var moves = AssetDragData.MovePaths(e.Data);
            e.DragEffects = DragDropEffects.None;
            if (moves != null)
            {
                string dest = DropFolder(tile, forImport: false);
                bool ok = dest != null && AssetFileOps.CanMoveInto(moves, dest);
                if (ok) e.DragEffects = DragDropEffects.Move;
                SetDropHighlight(ok && tile != null && tile.IsFolder ? tile : null);
            }
            else if (e.Data.Contains(DataFormats.Files) && ProjectRoot != null)
            {
                e.DragEffects = DragDropEffects.Copy;
                SetDropHighlight(tile != null && tile.IsFolder ? tile : null);
            }
            else SetDropHighlight(null);
            e.Handled = true;
        }

        private async void OnItemsDrop(object sender, DragEventArgs e)
        {
            var tile = TileFromSource(e.Source);
            SetDropHighlight(null);
            var moves = AssetDragData.MovePaths(e.Data);
            if (moves != null)
            {
                string dest = DropFolder(tile, forImport: false);
                if (dest != null) MoveInto(moves, dest);
                e.Handled = true;
                return;
            }
            var files = AssetDragData.OsFiles(e.Data);
            if (files != null)
            {
                e.Handled = true;
                await ImportFiles(files, DropFolder(tile, forImport: true));
            }
        }

        // ================================================================ import / export
        /// <summary>Where imports land: the folder being browsed (inside Assets), else the type tab's default folder.</summary>
        public string ImportTargetFolder()
        {
            string assets = AssetsRoot;
            if (assets == null) return null;
            if (_tab == "Explorer" && _folder != null && AssetFileOps.IsWithin(_folder, assets) && Directory.Exists(_folder)) return _folder;
            string sub;
            switch (_tab)
            {
                case "Meshes": case "Models": sub = "Models"; break;
                case "Textures": sub = "Textures"; break;
                case "Materials": sub = "Materials"; break;
                case "Audio": sub = "Audio"; break;
                case "Scripts": sub = "Scripts"; break;
                case "Prefabs": sub = "Prefabs"; break;
                case "Scenes": sub = "Scenes"; break;
                default: sub = ""; break;
            }
            string dir = sub.Length == 0 ? assets : Path.Combine(assets, sub);
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        public async Task ImportViaPicker()
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null || ProjectRoot == null) return;
            string target = ImportTargetFolder();
            IStorageFolder start = null;
            try { start = await top.StorageProvider.TryGetFolderFromPathAsync(target); } catch { }
            var types = new List<FilePickerFileType>
            {
                new FilePickerFileType("All supported") { Patterns = AssetKinds.ModelExt.Concat(AssetKinds.TextureExt).Concat(AssetKinds.AudioExt).Concat(new[] { ".vmat", ".cs", ".ttf", ".otf" }).Select(x => "*" + x).ToArray() },
                new FilePickerFileType("3D models") { Patterns = AssetKinds.ModelExt.Select(x => "*" + x).ToArray() },
                new FilePickerFileType("Textures") { Patterns = AssetKinds.TextureExt.Select(x => "*" + x).ToArray() },
                new FilePickerFileType("Audio") { Patterns = AssetKinds.AudioExt.Select(x => "*" + x).ToArray() },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } },
            };
            // the picker opens on the type the browser is showing (WPF: filter per tab)
            if (_tab == "Meshes" || _tab == "Models") types.Insert(0, types[1]);
            else if (_tab == "Textures") types.Insert(0, types[2]);
            else if (_tab == "Audio") types.Insert(0, types[3]);
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Assets", AllowMultiple = true, SuggestedStartLocation = start, FileTypeFilter = types.Distinct().ToList()
            });
            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).ToArray();
            if (paths.Length > 0) await ImportFiles(paths, target);
        }

        /// <summary>Import files into <paramref name="targetFolder"/> through the shared import dialog, then show them.</summary>
        public async Task ImportFiles(string[] files, string targetFolder)
        {
            if (files == null || files.Length == 0 || ProjectRoot == null) return;
            targetFolder ??= ImportTargetFolder();
            string[] imported = null;
            try { imported = await EditorWindows.ImportAssets(files, targetFolder); }
            catch (Exception ex) { EditorCommands.Fail("Import", ex); }
            RefreshDatabase();
            if (imported != null && imported.Length > 0)
            {
                _pendingSelect = imported.ToList();
                string dir = Path.GetDirectoryName(imported[0]);
                if (_tab == "Explorer" && Directory.Exists(dir) && !AssetFileOps.PathsEqual(dir, _folder)) { Navigate(dir); return; }
            }
            RefreshNow(resetScroll: false);
        }

        /// <summary>Menu "Import Asset…" hands files over one by one: batch them into one import.</summary>
        public void ImportFile(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath)) return;
            if (_importBatch == null)
            {
                _importBatch = new List<string>();
                Dispatcher.UIThread.Post(async () =>
                {
                    var batch = _importBatch.ToArray();
                    _importBatch = null;
                    await ImportFiles(batch, ImportTargetFolder());
                });
            }
            _importBatch.Add(sourcePath);
        }

        public void ExportSelected()
        {
            var t = PrimaryTile();
            if (t == null || !t.IsFileSystemItem || t.IsFolder || !File.Exists(t.FullPath)) { EditorCommands.Toast("Select an asset to export"); return; }
            _ = ExportAsync(t.FullPath);
        }

        private async Task ExportAsync(string path)
        {
            var top = TopLevel.GetTopLevel(this); if (top == null) return;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export Asset", SuggestedFileName = Path.GetFileName(path) });
            var dest = file?.TryGetLocalPath(); if (string.IsNullOrEmpty(dest)) return;
            try { File.Copy(path, dest, true); EditorCommands.Toast("Exported to " + dest); } catch (Exception ex) { EditorCommands.Fail("Export", ex); }
        }

        // ================================================================ create
        /// <summary>Where new assets are created: the folder being browsed when it is inside Assets, else a default.</summary>
        private string CreateFolderFor(string defaultRel)
        {
            string assets = AssetsRoot;
            if (_tab == "Explorer" && _folder != null && assets != null && AssetFileOps.IsWithin(_folder, assets) && Directory.Exists(_folder)) return _folder;
            string dir = defaultRel.Length == 0 ? assets : Path.Combine(ProjectRoot, defaultRel);
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        private IEnumerable<Control> CreateItems()
        {
            yield return Mi("New Folder", () => CreateFolder(), "FolderFill");
            yield return Mi("New Script…", () => _ = CreateScriptAsync(), "Script");
            var mat = new MenuItem { Header = "New Material", Icon = Icon("Material") };
            foreach (var t in new[] { "Standard", "Unlit", "Transparent" }) { var tt = t; mat.Items.Add(Mi(t + " Material…", () => _ = CreateMaterialAsync(tt))); }
            yield return mat;
            var sh = new MenuItem { Header = "New Shader", Icon = Icon("Sparkle") };
            foreach (var t in new[] { "Standard", "Unlit", "Transparent" }) { var tt = t; sh.Items.Add(Mi(t + " Shader…", () => _ = CreateShaderAsync(tt))); }
            yield return sh;
            yield return Mi("New Prefab", () => CreatePrefab(), "Prefab");
            yield return Mi("New Scene…", () => _ = CreateSceneAsync(), "Scene");
            yield return Mi("New UI Screen…", () => _ = CreateUiScreenAsync(), "LayoutSingle");
            yield return Mi("New Animation Clip…", () => _ = CreateAnimationClipAsync(), "Play");
            yield return Mi("New Sound Container", () => CreateSoundContainer(), "Layers");
            yield return Mi("New Visual Effect", () => CreateVfx(), "Sparkle");
            yield return Mi("New Behavior Tree", () => CreateBehaviorTree(), "Flow");
        }

        /// <summary>New folder in the browsed folder (undoable), then rename it inline.</summary>
        public string CreateFolder(string parent = null, bool rename = true)
        {
            parent ??= _tab == "Explorer" && _folder != null ? _folder : CreateFolderFor("");
            string path = AssetFileOps.CreateFolder(parent);
            if (path == null) return null;
            if (_tab != "Explorer") SetTab("Explorer", refresh: false);
            if (!AssetFileOps.PathsEqual(parent, _folder)) { _folder = null; NavigateInternal(parent, recordHistory: true); }
            if (rename) _pendingRename = path; else _pendingSelect = new List<string> { path };
            RefreshNow(resetScroll: false);
            return path;
        }

        private void Created(string path, string toast)
        {
            if (string.IsNullOrEmpty(path)) return;
            RefreshDatabase();
            EditorCommands.Toast(toast);
            Reveal(path);
        }

        private async Task<string> AskName(string what, string defaultName)
        {
            var name = await Dialogs.Prompt("New " + what, "Name", defaultName, "Create");
            if (string.IsNullOrWhiteSpace(name)) return null;
            string err = AssetFileOps.ValidateName(name);
            if (err != null) { EditorCommands.Toast(err); return null; }
            return name.Trim();
        }

        private static string WithExt(string dir, string name, string ext)
            => AssetFileOps.UniqueChild(dir, name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? name : name + ext, false);

        /// <summary>New C# behaviour (the scripting template) — in the browsed folder when that is inside Assets/Scripts
        /// (scripts compile from there), else Assets/Scripts — then opened in the code editor. <paramref name="name"/>
        /// null asks for it. Returns the new file.</summary>
        public async Task<string> CreateScriptAsync(string name = null, bool openInEditor = true)
        {
            if (ProjectRoot == null) return null;
            name ??= await AskName("Script", "NewBehaviour");
            if (name == null) return null;
            try
            {
                string path = ScriptingService.CreateScript(Path.GetFileNameWithoutExtension(name));
                string scripts = ScriptingService.ScriptsDir;
                if (_tab == "Explorer" && _folder != null && scripts != null && AssetFileOps.IsWithin(_folder, scripts) && !AssetFileOps.PathsEqual(_folder, scripts))
                {
                    string dest = AssetFileOps.UniqueChild(_folder, Path.GetFileName(path), false);
                    if (Path.GetFileName(dest) == Path.GetFileName(path)) { File.Move(path, dest); path = dest; }
                }
                Created(path, "Script created: " + Path.GetFileName(path));
                if (openInEditor) EditorCommands.OpenInIde(path);
                return path;
            }
            catch (Exception ex) { EditorCommands.Fail("Create script", ex); return null; }
        }

        public Task<string> CreateMaterialAsync(string type, string name = null) => CreateNamed(type + " Material", "New" + type + "Material", ".vmat", "Assets/Materials", p => CoreAssetActions.CreateMaterial(p, type), null, name);
        public void CreateMaterial(string type) => _ = CreateMaterialAsync(type);
        public Task<string> CreateShaderAsync(string type, string name = null, bool openInEditor = true) => CreateNamed(type + " Shader", type == "Unlit" ? "NewUnlitShader" : "NewShader", Editor.Core.Native.NativeLoader.MaterialShaderExtension, "Assets/Shaders",
            p => CoreAssetActions.CreateShader(p, type), p => { if (openInEditor) EditorCommands.OpenInIde(p); }, name);
        public void CreateShader(string type) => _ = CreateShaderAsync(type);
        public Task<string> CreateUiScreenAsync(string name = null, bool open = true) => CreateNamed("UI Screen", "NewScreen", ".vui", "Assets/UI", p => CoreAssetActions.CreateUiScreen(p), p => { if (open) EditorWindows.UiEditor(p); }, name);
        public Task<string> CreateAnimationClipAsync(string name = null, bool open = true) => CreateNamed("Animation Clip", "NewClip", ".vanim", "Assets/Animations", p => CoreAssetActions.CreateAnimationClip(p), p => { if (open) EditorWindows.AnimationEditor(p); }, name);

        private async Task<string> CreateNamed(string what, string defaultName, string ext, string defaultRel, Func<string, string> create, Action<string> after, string name)
        {
            if (ProjectRoot == null) return null;
            name ??= await AskName(what, defaultName);
            if (name == null) return null;
            try
            {
                string path = create(WithExt(CreateFolderFor(defaultRel), name, ext));
                Created(path, what + " created: " + Path.GetFileName(path));
                after?.Invoke(path);
                return path;
            }
            catch (Exception ex) { EditorCommands.Fail("Create " + what.ToLowerInvariant(), ex); return null; }
        }

        /// <summary>New empty prefab in Assets/Prefabs (like the Windows editor), revealed + selected.</summary>
        public string CreatePrefab()
        {
            try { var p = CoreAssetActions.CreateEmptyPrefab(); Created(p, "Prefab created"); return p; }
            catch (Exception ex) { EditorCommands.Fail("Create prefab", ex); return null; }
        }

        /// <summary>New sound container in the browsed folder (else Assets/Audio), opened in its editor.</summary>
        /// <summary>New .vbt (a Selector root with an idle sequence) in the browsed folder or Assets/AI, opened in the Behavior Tree editor.</summary>
        public string CreateBehaviorTree(bool open = true)
        {
            if (ProjectRoot == null) return null;
            try
            {
                string folder = CreateFolderFor("Assets/AI");
                string path = BehaviorTreeEditorWindow.CreateNew(folder, "NewBehaviorTree");
                Created(path, "Behavior tree created");
                if (open) EditorWindows.BehaviorTreeEditor(path);
                return path;
            }
            catch (Exception ex) { EditorCommands.Fail("Create behavior tree", ex); return null; }
        }

        /// <summary>New .vfx (one soft emitter) in the browsed folder or Assets/VFX, opened in the VFX editor.</summary>
        public string CreateVfx(bool open = true)
        {
            if (ProjectRoot == null) return null;
            try
            {
                string folder = CreateFolderFor("Assets/VFX");
                string path = VfxEditorWindow.CreateNew(folder, "NewEffect");
                Created(path, "Visual effect created");
                if (open) EditorWindows.VfxEditor(path);
                return path;
            }
            catch (Exception ex) { EditorCommands.Fail("Create visual effect", ex); return null; }
        }

        public string CreateSoundContainer(bool open = true)
        {
            if (ProjectRoot == null) return null;
            try
            {
                string path = WithExt(CreateFolderFor("Assets/Audio"), "NewSoundContainer", Editor.Core.Audio.SoundContainer.FileExtension);
                new Editor.Core.Audio.SoundContainer().Save(path);
                Created(path, "Sound container created");
                if (open) EditorWindows.SoundContainerEditor(path);
                return path;
            }
            catch (Exception ex) { EditorCommands.Fail("Create sound container", ex); return null; }
        }

        /// <summary>New scene with the default content (camera/player, light, ground): added to the project and saved to
        /// Assets/Scenes (scene paths in the project file are relative to it); <paramref name="open"/> also makes it the
        /// active scene (a double-click on the new tile opens it otherwise).</summary>
        public async Task<string> CreateSceneAsync(string name = null, bool open = false)
        {
            var project = ProjectData.Current; if (project == null) return null;
            name ??= await AskName("Scene", "New Scene");
            if (name == null) return null;
            try
            {
                string dir = Path.Combine(project.Path, "Assets", "Scenes");
                Directory.CreateDirectory(dir);
                string unique = Path.GetFileNameWithoutExtension(AssetFileOps.UniqueChild(dir, name + Scene.FileExtension, false));
                for (int i = 2; project.Scenes.Any(s => string.Equals(s?.Name, unique, StringComparison.OrdinalIgnoreCase)) && i < 1000; i++) unique = name + " " + i;
                var scene = SceneService.Instance.CreateDefaultScene(project, unique);
                if (scene == null) return null;
                project.AddScene(scene);
                SceneService.Instance.SaveScene(scene);
                if (open) EditorSession.Instance.ActivateScene(scene);
                EditorCommands.Toast("Scene created: " + scene.Name + (open ? "" : " — double-click it to open"));
                RefreshDatabase();
                if (File.Exists(scene.FilePath)) Reveal(scene.FilePath); else RefreshNow(false);
                return scene.FilePath;
            }
            catch (Exception ex) { EditorCommands.Fail("Create scene", ex); return null; }
        }

        // ================================================================ context menu
        private void OnItemsContextRequested(object sender, ContextRequestedEventArgs e)
        {
            if (IsInside<TextBox>(e.Source, Items)) return;
            var tile = TileFromSource(e.Source);
            if (tile != null && (Items.SelectedItems == null || !Items.SelectedItems.Contains(tile))) SelectOnly(tile);
            var sel = tile == null ? new List<AssetTile>() : SelectedTiles.ToList();
            if (tile != null && sel.Count == 0) sel.Add(tile);
            var menu = BuildContextMenu(tile, sel);
            if (menu == null) return;
            e.Handled = true;
            Control anchor = (tile != null ? Items.ContainerFromItem(tile) as Control : null) ?? Items;
            menu.ShowAt(anchor, showAtPointer: e.TryGetPosition(anchor, out _));
        }

        internal MenuFlyout BuildContextMenu(AssetTile tile, List<AssetTile> sel)
        {
            if (tile != null && tile.IsParentLink) return null;   // ".." has no actions (WPF)
            var m = new MenuFlyout();
            var cmdKey = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            var fs = sel.Where(t => t.IsFileSystemItem).ToList();
            bool multi = sel.Count > 1;

            if (_tab == "Audio") { m.Items.Add(Mi("New Sound Container", () => CreateSoundContainer(), "Layers")); if (tile != null) m.Items.Add(new Separator()); }

            if (tile != null && !multi)
            {
                string p = tile.FullPath;
                switch (tile.Kind)
                {
                    case AssetKind.Folder:
                        m.Items.Add(Mi("Open Folder", () => Navigate(p), "FolderFill"));
                        break;
                    case AssetKind.Prefab:
                        m.Items.Add(Mi("Add to Scene (Instance)", () => AddToScene(p), "Plus"));
                        m.Items.Add(Mi("Open Prefab (Edit)", () => EditorWindows.PrefabEditor(p), "Prefab", gestureText: VortexEditor.Shell.Keys.Shift + " double-click"));
                        m.Items.Add(Mi("Large Preview", () => EditorWindows.OpenLargePreview(p), "Eye", gestureText: VortexEditor.Shell.Keys.Cmd + " double-click"));
                        break;
                    case AssetKind.Model:
                        m.Items.Add(Mi("Add to Scene", () => AddToScene(p), "Plus"));
                        m.Items.Add(Mi("Open in Model Editor", () => EditorWindows.ModelEditor(p), "Cube", gestureText: VortexEditor.Shell.Keys.Shift + " double-click"));
                        m.Items.Add(Mi("Mesh Editor", () => EditorWindows.MeshEditor(p), "Grid"));
                        m.Items.Add(Mi("Large Preview", () => EditorWindows.OpenLargePreview(p), "Eye", gestureText: VortexEditor.Shell.Keys.Cmd + " double-click"));
                        m.Items.Add(new Separator());
                        m.Items.Add(Mi("Create Prefab from Model", () => _ = CreatePrefabFromModelAsync(p), "Prefab"));
                        m.Items.Add(Mi("Extract Animations…", () => _ = ExtractAnimationsAsync(p), "Play"));
                        m.Items.Add(Mi("Stress Test…", () => EditorWindows.StressTest(p), "Hammer"));
                        break;
                    case AssetKind.Primitive:
                        m.Items.Add(Mi("Add to Scene", () => AddToScene(p), "Plus"));
                        m.Items.Add(Mi("Large Preview", () => EditorWindows.OpenLargePreview(p), "Eye", gestureText: VortexEditor.Shell.Keys.Cmd + " double-click"));
                        break;
                    case AssetKind.Scene:
                        m.Items.Add(Mi("Open Scene", () => _ = Actions.OpenDefault(p), "Scene"));
                        break;
                    case AssetKind.Script:
                        m.Items.Add(Mi("Open in Code Editor", () => EditorCommands.OpenInIde(p), "Script"));
                        m.Items.Add(Mi("Assign to Selected Entity", () => AssignScript(p), "Link", enabled: SelectionService.Instance.SelectedEntity != null));
                        break;
                    case AssetKind.Material:
                        m.Items.Add(Mi("Open in Material Editor", () => EditorWindows.MaterialEditor(p), "Material"));
                        m.Items.Add(Mi("Large Preview", () => EditorWindows.OpenLargePreview(p), "Eye", gestureText: VortexEditor.Shell.Keys.Cmd + " double-click"));
                        m.Items.Add(Mi("Assign to Selected Entity", () => AssignMaterial(p), "Link", enabled: SelectionService.Instance.SelectedEntity != null));
                        break;
                    case AssetKind.Texture:
                        m.Items.Add(Mi("Open in Texture Editor", () => EditorWindows.TextureEditor(p), "Image"));
                        m.Items.Add(Mi("Large Preview", () => EditorWindows.OpenLargePreview(p), "Eye", gestureText: VortexEditor.Shell.Keys.Cmd + " double-click"));
                        break;
                    case AssetKind.AudioClip:
                        m.Items.Add(Mi("Play", () => Actions.Audition(p), "Play"));
                        m.Items.Add(Mi("Stop", Actions.StopAudition, "Stop"));
                        {
                            // a Sound Studio take: reopen its recipe (#83)
                            Editor.Core.Assets.AssetMetadata meta = null;
                            try { meta = Editor.Core.Serialization.DataSerializer.LoadFromJson<Editor.Core.Assets.AssetMetadata>(p + Editor.Core.Assets.AssetDatabase.MetaFileExtension); } catch { }
                            var recipe = Editor.Core.Audio.SoundStudio.SoundRecipe.FromJson(meta?.Recipe);
                            if (recipe != null) m.Items.Add(Mi("Open in Sound Studio", () => VortexEditor.Shell.Audio.SoundStudioWindow.Open(recipe, meta.ContentHash), "Sparkle"));
                        }
                        break;
                    case AssetKind.SoundContainer:
                        m.Items.Add(Mi("Open Sound Container Editor", () => EditorWindows.SoundContainerEditor(p), "Layers"));
                        m.Items.Add(Mi("Play (random clip)", () => Actions.Audition(p), "Play"));
                        m.Items.Add(Mi("Stop", Actions.StopAudition, "Stop"));
                        break;
                    case AssetKind.AnimationClip:
                        m.Items.Add(Mi("Open in Animation Editor", () => EditorWindows.AnimationEditor(p), "Play"));
                        break;
                    case AssetKind.Terrain:
                        m.Items.Add(Mi("Select Terrain in Scene", () => SelectWorldDataUser(tile), "World", gestureText: "double-click"));
                        break;
                    case AssetKind.Foliage:
                        m.Items.Add(Mi("Select Foliage in Scene", () => SelectWorldDataUser(tile), "Layers", gestureText: "double-click"));
                        break;
                    case AssetKind.UiScreen:
                        m.Items.Add(Mi("Open in UI Editor", () => EditorWindows.UiEditor(p), "LayoutSingle"));
                        break;
                    case AssetKind.Vfx:
                        m.Items.Add(Mi("Add to Scene", () => AddToScene(p), "Plus"));
                        m.Items.Add(Mi("Open in VFX Editor", () => EditorWindows.VfxEditor(p), "Sparkle", gestureText: VortexEditor.Shell.Keys.Shift + " double-click"));
                        break;
                    case AssetKind.BehaviorTree:
                        m.Items.Add(Mi("Open in Behavior Tree Editor", () => EditorWindows.BehaviorTreeEditor(p), "Flow"));
                        break;
                    case AssetKind.Shader:
                        m.Items.Add(Mi("Open in Code Editor", () => EditorCommands.OpenInIde(p), "Sparkle"));
                        break;
                    case AssetKind.BuiltInMaterial:
                    case AssetKind.BuiltInTexture:
                        m.Items.Add(new MenuItem { Header = "Built-in (read-only)", IsEnabled = false });
                        break;
                    default:
                        m.Items.Add(Mi("Open", () => _ = Actions.OpenDefault(p), "File"));
                        break;
                }
                if (tile.IsFileSystemItem && !tile.IsFolder) m.Items.Add(Mi("Open With Default App", () => AssetFileOps.OpenWithDefaultApp(p)));
            }
            else if (multi && sel.All(t => AssetKinds.IsPlaceable(t.Kind)))
            {
                var paths = sel.Select(t => t.FullPath).ToList();
                m.Items.Add(Mi("Add " + paths.Count + " to Scene", () => { foreach (var x in paths) AddToScene(x); }, "Plus"));
            }

            if (fs.Count > 0)
            {
                if (m.Items.Count > 0) m.Items.Add(new Separator());
                var paths = fs.Select(t => t.FullPath).ToList();
                if (!multi) m.Items.Add(Mi("Rename", () => BeginRename(fs[0]), "Tag", new KeyGesture(Key.F2)));
                m.Items.Add(Mi("Duplicate", () => { RestoreSelection(paths); DuplicateSelected(); }, "Layers", new KeyGesture(Key.D, cmdKey)));
                m.Items.Add(Mi(multi ? "Delete " + fs.Count + " Items…" : "Delete…", () => { RestoreSelection(paths); _ = DeleteSelectedAsync(); }, "Trash", new KeyGesture(Key.Back, cmdKey)));
                m.Items.Add(new Separator());
                m.Items.Add(Mi(OperatingSystem.IsMacOS() ? "Reveal in Finder" : "Show in Explorer", () => AssetFileOps.Reveal(paths[0]), "Folder"));
                m.Items.Add(Mi("Copy Path", () => _ = CopyText(string.Join("\n", fs.Select(t => t.RelPath))), "Link"));
                m.Items.Add(Mi("Copy Full Path", () => _ = CopyText(string.Join("\n", paths))));
                if (!multi && !fs[0].IsFolder) m.Items.Add(Mi("Edit Tags…", () => EditorWindows.AssetTags(paths[0]), "Tag"));
                m.Items.Add(Mi("Reimport", () => Reimport(paths), "Refresh"));
            }

            if (m.Items.Count > 0) m.Items.Add(new Separator());
            foreach (var item in CreateItems()) m.Items.Add(item);
            m.Items.Add(new Separator());
            m.Items.Add(Mi("Import…", () => _ = ImportViaPicker(), "Import"));
            if (_tab == "Explorer" && _folder != null)
                m.Items.Add(Mi(OperatingSystem.IsMacOS() ? "Open Folder in Finder" : "Open Folder in Explorer", () => AssetFileOps.OpenWithDefaultApp(_folder), "Folder"));
            m.Items.Add(Mi("Refresh", () => { RefreshDatabase(); RefreshNow(false); }, "Refresh"));
            return m;
        }

        private static Control Icon(string name) => new VxIcon { Icon = name, Width = 14, Height = 14 };

        private static MenuItem Mi(string header, Action action, string icon = null, KeyGesture gesture = null, bool enabled = true, string gestureText = null)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            if (icon != null) mi.Icon = Icon(icon);
            if (gesture != null) mi.InputGesture = gesture;
            if (gestureText != null) mi.Header = new DockPanel { Children = { new TextBlock { Text = gestureText, Classes = { "tertiary", "small" }, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right }, new TextBlock { Text = header } } };
            mi.Click += (s, e) => { try { action(); } catch (Exception ex) { EditorCommands.Fail(header.TrimEnd('…'), ex); } };
            return mi;
        }

        private static MenuItem Check(string header, bool on, Action action)
        {
            var mi = new MenuItem { Header = header, Icon = on ? Icon("Check") : null };
            mi.Click += (s, e) => action();
            return mi;
        }

        private async Task CopyText(string text)
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb == null) return;
            await cb.SetTextAsync(text);
            EditorCommands.Toast("Copied " + (text.Contains('\n') ? "paths" : text));
        }

        // ================================================================ asset actions
        private static void AddToScene(string path)
        {
            var e = Actions.AddToScene(path);
            if (e != null && !Actions.IsPrefab(path)) EditorCommands.Toast("Added " + e.Name + " to the scene");   // prefabs toast in the prefab workflow
        }

        private async Task CreatePrefabFromModelAsync(string modelPath)
        {
            try
            {
                string prefab = PrefabService.Instance.CreatePrefabFromModel(modelPath, Path.GetFileNameWithoutExtension(modelPath));
                if (string.IsNullOrEmpty(prefab)) { await Dialogs.Alert("Create Prefab", "Could not create a prefab from this model."); return; }
                RefreshDatabase();
                Reveal(prefab);
                await Dialogs.Alert("Created prefab", AssetFileOps.ToRelative(prefab) + "\n\nDrag it into the scene (or double-click it) to place LINKED instances — no throwaway mesh needed. "
                    + "Add scripts / colliders to the prefab and apply, and every instance updates.");
            }
            catch (Exception ex) { EditorCommands.Fail("Create prefab", ex); }
        }

        private async Task ExtractAnimationsAsync(string modelPath)
        {
            try
            {
                var written = Editor.Core.Animation.AnimationService.ExtractClipsFromModel(modelPath);
                RefreshDatabase();
                RefreshNow(false);
                if (written == null || written.Count == 0) { await Dialogs.Alert("Extract Animations", "This model has no embedded animation clips to extract."); return; }
                await Dialogs.Alert("Extracted " + written.Count + " animation clip" + (written.Count == 1 ? "" : "s"),
                    string.Join("\n", written.Select(w => "• " + Path.GetFileName(w))) + "\n\nDouble-click a .vanim to edit it in the Animation Editor, or assign it to any model's Animator.");
            }
            catch (Exception ex) { EditorCommands.Fail("Extract animations", ex); }
        }

        private static void AssignScript(string scriptPath)
        {
            var ent = SelectionService.Instance.SelectedEntity;
            if (ent == null) { EditorCommands.Toast("Select an entity in the scene first"); return; }
            ent.AddComponent(new Editor.ECS.Components.Scripting.Script(ent, ScriptingService.MakeRelative(ProjectRoot, scriptPath)));
            SelectionService.Instance.Select(ent);
            EditorCommands.Window?.Inspector?.Refresh();
            EditorCommands.Toast("Assigned " + Path.GetFileName(scriptPath) + " to " + ent.Name);
        }

        /// <summary>Assign a .vmat to the selected entity; a model container gets it on every submesh part.</summary>
        private static void AssignMaterial(string vmatPath)
        {
            var ent = SelectionService.Instance.SelectedEntity;
            if (ent == null) { EditorCommands.Toast("Select an entity in the scene first"); return; }
            string rel = AssetFileOps.ToRelative(vmatPath);
            var mr = ent.GetComponent<Editor.ECS.Components.Rendering.MeshRenderer>();
            int n = 0;
            string fitNote = null;
            if (mr != null && !IsMultiSubmeshBase(mr.MeshPath)) { mr.MaterialPath = VortexEditor.Services.MaterialFit.ForRenderer(ent, mr, rel, out fitNote); n = 1; }
            else
                foreach (var child in ent.Children)
                {
                    var cr = child.GetComponent<Editor.ECS.Components.Rendering.MeshRenderer>();
                    if (cr != null && (cr.MeshPath ?? "").IndexOf("#submesh", StringComparison.OrdinalIgnoreCase) >= 0) { cr.MaterialPath = rel; n++; }
                }
            SceneRenderService.RuntimeDirty = true;
            EditorCommands.Window?.Inspector?.Refresh();
            if (fitNote != null) { Current?.Refresh(); EditorCommands.Toast(fitNote); return; }
            EditorCommands.Toast(n > 0 ? "Assigned " + Path.GetFileNameWithoutExtension(vmatPath) + " to " + ent.Name + (n > 1 ? " (" + n + " parts)" : "") : ent.Name + " has no mesh to assign a material to");
        }

        private static bool IsMultiSubmeshBase(string meshPath)
        {
            if (string.IsNullOrEmpty(meshPath) || meshPath.IndexOf('#') >= 0 || meshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) return false;
            if (!AssetKinds.Is(Ext(meshPath), AssetKinds.ModelExt)) return false;
            string full = Path.IsPathRooted(meshPath) ? meshPath : Path.Combine(ProjectRoot ?? "", meshPath);
            try { return File.Exists(full) && Editor.DllWrapper.VortexAPI.GetSubmeshCount(full) > 1; } catch { return false; }
        }
    }
}
