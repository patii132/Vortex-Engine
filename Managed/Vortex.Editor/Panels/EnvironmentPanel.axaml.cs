using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.DllWrapper;
using VortexEditor.Controls;

namespace VortexEditor.Panels
{
    /// <summary>
    /// Scene environment (port of the WPF EnvironmentView — fog #27 + post-FX #28/#29): edits the active scene's
    /// SceneSettings, pushes every change to the renderer the same frame and marks the scene dirty so ⌘S saves it
    /// into the .vscene. Same sections, ranges and labels as the Windows editor; numeric boxes commit on Enter / focus
    /// loss; "Preview post effects in viewport" shows the game-camera effects in the build view while tuning.
    /// </summary>
    public partial class EnvironmentPanel : UserControl
    {
        private bool _loading;
        private ProjectData _hooked;
        private readonly List<Action> _refreshers = new List<Action>();

        private static Scene ActiveScene => ProjectData.Current?.ActiveScene;
        private static SceneSettings Settings => ActiveScene?.Settings;

        // ---- "Preview post effects in viewport" (editor-only, never saved) — shared with the viewport's view options
        private static bool _previewFx;
        private static event Action PreviewChanged;
        public static bool PreviewPostEffects => _previewFx;
        public static void SetPreviewPostEffects(bool on)
        {
            _previewFx = on;
            try { VortexAPI.SetPostMainView(on); } catch { }
            PreviewChanged?.Invoke();
        }

        public EnvironmentPanel()
        {
            InitializeComponent();
            Build();
            EditorSession.Instance.ProjectOpened += _ => Dispatcher.UIThread.Post(Refresh);
            EditorSession.Instance.ProjectClosed += () => Dispatcher.UIThread.Post(Refresh);
            AttachedToVisualTree += (s, e) => Refresh();
            PreviewChanged += () => Dispatcher.UIThread.Post(() => { _loading = true; PreviewFx.IsChecked = _previewFx; _loading = false; });
        }

        private void Build()
        {
            Sections.Children.Add(Section("Exposure", "The camera's aperture: scene exposure in stops BEFORE the tonemapper. +1 doubles the light the ACES curve receives — brightens a sunlit world without the clipping of the grading exposure. 0 = the classic look.", null, null,
                Slider("Stops", "−3 … +4 stops; a sunlit desert wants +1.5 … +2.5, a night scene −1", -3, 4, s => s.SceneExposure, (s, v) => s.SceneExposure = v),
                Choice("Tonemapper", "ACES: the classic filmic curve — bright saturated colours drift towards white. PBR Neutral (Khronos): keeps hue and saturation, a sunlit desert stays orange.", new[] { "ACES", "PBR Neutral" }, s => s.Tonemapper, (s, v) => s.Tonemapper = v)));
            Sections.Children.Add(Section("Anti-aliasing", "FXAA on the final picture: smooth edges on fronds, grass and ripples (nine taps, no history, ~0.3 ms). Temporal AA comes with the AAA rendering milestone.", s => s.AntiAliasEnabled, (s, v) => s.AntiAliasEnabled = v));
            Sections.Children.Add(Section("Global Illumination", "Dynamic GI, step 1 (screen space): one bounce of the lit world traced against the depth with the previous frame's light — sunlit sand warms the palm trunks, a lit wall lights the floor beside it. Needs SSAO on; ~0.4 ms at half resolution. The world-space probe cache and ray tracing follow.", s => s.GiEnabled, (s, v) => s.GiEnabled = v,
                Slider("Strength", "1 = a physical diffuse bounce", 0, 3, s => s.GiStrength, (s, v) => s.GiStrength = v),
                Slider("Radius (m)", "How far a bounce ray travels", 0.5, 10, s => s.GiRadius, (s, v) => s.GiRadius = v)));
            Sections.Children.Add(Section("Ground Bounce", "The sun's light bounced off the ground into the shadows: a warm lower hemisphere added to the sky light, scaled by the sun's strength and height. 0 = off; a desert noon wants 0.4–0.7.", null, null,
                Slider("Strength", null, 0, 1.5, s => s.BounceStrength, (s, v) => s.BounceStrength = v),
                ColorRow("Ground", s => (s.BounceR, s.BounceG, s.BounceB), (s, c) => { s.BounceR = c.r; s.BounceG = c.g; s.BounceB = c.b; })));
            Sections.Children.Add(Section("Fog", "Height fog: distance haze plus an optional ground mist below Height Y.", s => s.FogEnabled, (s, v) => s.FogEnabled = v,
                Slider("Density", null, 0, 0.5, s => s.FogDensity, (s, v) => s.FogDensity = v),
                Slider("Height Y", null, -20, 20, s => s.FogHeightY, (s, v) => s.FogHeightY = v),
                Slider("Ground Mist", "0 = uniform distance fog; higher = mist hugging the ground below Height Y", 0, 2, s => s.FogHeightFalloff, (s, v) => s.FogHeightFalloff = v),
                Slider("Sun Glow", "Aerial perspective: the sun's glow scattered in the haze when looking towards it (0 = off, 0.3–0.6 a desert noon, 1–2 a low sun)", 0, 3, s => s.FogSunGlow, (s, v) => s.FogSunGlow = v),
                ColorRow("Color", s => (s.FogR, s.FogG, s.FogB), (s, c) => { s.FogR = c.r; s.FogG = c.g; s.FogB = c.b; })));
            Sections.Children.Add(Section("Volumetric Fog", "Ray-marched fog (#119): light scattered towards the camera from the fog colour, the sun and every point / spot light — visible cones and shafts. Uses the Fog height and colour above; works with Fog off.", s => s.VolumetricEnabled, (s, v) => s.VolumetricEnabled = v,
                Slider("Density", "Scattering per metre — 0.01 a haze, 0.03 fog, 0.1 a thick cellar", 0, 0.25, s => s.VolumetricDensity, (s, v) => s.VolumetricDensity = v),
                Slider("Anisotropy", "0 = light spreads evenly; towards 1 = bright halos when looking into a light", 0, 0.95, s => s.VolumetricAnisotropy, (s, v) => s.VolumetricAnisotropy = v),
                Slider("Distance (m)", "How far the march goes", 5, 200, s => s.VolumetricDistance, (s, v) => s.VolumetricDistance = v),
                Slider("Noise", "Wind-blown patchiness (0 = uniform)", 0, 1, s => s.VolumetricNoise, (s, v) => s.VolumetricNoise = v),
                Slider("Noise Scale (m)", null, 0.5, 30, s => s.VolumetricNoiseScale, (s, v) => s.VolumetricNoiseScale = v),
                Slider("Wind", "Noise drift in cells per second", 0, 3, s => s.VolumetricNoiseSpeed, (s, v) => s.VolumetricNoiseSpeed = v),
                Slider("Sun Shafts", "Directional light contribution (through its cascaded shadows)", 0, 3, s => s.VolumetricSun, (s, v) => s.VolumetricSun = v),
                Slider("Lights", "Point / spot light contribution (through their shadow maps)", 0, 3, s => s.VolumetricLights, (s, v) => s.VolumetricLights = v),
                Slider("Ambient", "Fog-colour glow without a light", 0, 2, s => s.VolumetricAmbient, (s, v) => s.VolumetricAmbient = v),
                Slider("Steps", "Ray-march quality (more = smoother, slower)", 8, 48, s => s.VolumetricSteps, (s, v) => s.VolumetricSteps = (int)Math.Round(v)),
                Slider("Shadows", "1 = light shafts stop at walls (shadow maps), 0 = cheaper, unshadowed", 0, 1, s => s.VolumetricShadows ? 1f : 0f, (s, v) => s.VolumetricShadows = v >= 0.5f)));
            Sections.Children.Add(Section("Vignette", null, s => s.VignetteEnabled, (s, v) => s.VignetteEnabled = v,
                Slider("Intensity", null, 0, 1.5, s => s.VignetteIntensity, (s, v) => s.VignetteIntensity = v),
                Slider("Smoothness", null, 0.01, 1, s => s.VignetteSmoothness, (s, v) => s.VignetteSmoothness = v),
                Slider("Roundness", "1 = circular on any aspect ratio, 0 = follows the screen shape", 0, 1, s => s.VignetteRoundness, (s, v) => s.VignetteRoundness = v),
                ColorRow("Color", s => (s.VignetteR, s.VignetteG, s.VignetteB), (s, c) => { s.VignetteR = c.r; s.VignetteG = c.g; s.VignetteB = c.b; })));
            Sections.Children.Add(Section("Film Grain", null, s => s.GrainEnabled, (s, v) => s.GrainEnabled = v,
                Slider("Intensity", null, 0, 1, s => s.GrainIntensity, (s, v) => s.GrainIntensity = v),
                Slider("Size (px)", null, 1, 4, s => s.GrainSize, (s, v) => s.GrainSize = v)));
            Sections.Children.Add(Section("Ambient Occlusion", "Darkens crevices and contact areas (indirect light only). Shows in the editor viewport like fog.", s => s.AoEnabled, (s, v) => s.AoEnabled = v,
                Slider("Radius", "Sample radius in world units — how far a surface looks for nearby occluders", 0.1, 2, s => s.AoRadius, (s, v) => s.AoRadius = v),
                Slider("Intensity", null, 0, 2, s => s.AoIntensity, (s, v) => s.AoIntensity = v)));
            Sections.Children.Add(Section("Bloom", null, s => s.BloomEnabled, (s, v) => s.BloomEnabled = v,
                Slider("Threshold", "Brightness where the glow starts — lower makes more of the scene bloom", 0, 1.5, s => s.BloomThreshold, (s, v) => s.BloomThreshold = v),
                Slider("Soft Knee", "Softens the threshold cutoff — avoids shimmer on barely-bright highlights", 0, 1, s => s.BloomKnee, (s, v) => s.BloomKnee = v),
                Slider("Intensity", "Glow strength in the final image; 0 = off (bit-exact passthrough)", 0, 3, s => s.BloomIntensity, (s, v) => s.BloomIntensity = v),
                Slider("Scatter", "How far the glow spreads — higher bleeds wider around bright spots", 0, 1, s => s.BloomScatter, (s, v) => s.BloomScatter = v)));
            Sections.Children.Add(Section("Color Grading", null, s => s.GradeEnabled, (s, v) => s.GradeEnabled = v,
                Slider("Exposure", "EV stops (2^EV)", -3, 3, s => s.Exposure, (s, v) => s.Exposure = v),
                Slider("Contrast", null, 0, 2, s => s.Contrast, (s, v) => s.Contrast = v),
                Slider("Saturation", null, 0, 2, s => s.Saturation, (s, v) => s.Saturation = v),
                Slider("Temperature", "-1 cool / blue .. +1 warm / orange", -1, 1, s => s.Temperature, (s, v) => s.Temperature = v),
                Slider("Tint", "-1 green .. +1 magenta", -1, 1, s => s.Tint, (s, v) => s.Tint = v)));
            Sections.Children.Add(Section("Chromatic Aberration", null, s => s.CaEnabled, (s, v) => s.CaEnabled = v,
                Slider("Strength", null, 0, 2, s => s.CaStrength, (s, v) => s.CaStrength = v),
                Slider("Falloff", "Radial power — higher keeps the center clean and smears only the edges", 0.5, 3, s => s.CaFalloff, (s, v) => s.CaFalloff = v)));
        }

        private Control Section(string title, string tip, Func<SceneSettings, bool> get, Action<SceneSettings, bool> set, params Control[] rows)
        {
            var card = new Border { Classes = { "card" }, Padding = new Thickness(10, 6, 10, 8) };
            var stack = new StackPanel { Spacing = 2 };
            var header = new DockPanel { Height = 26 };
            var body = new StackPanel { Spacing = 2 };
            var sw = new ToggleSwitch { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, OnContent = "", OffContent = "" };
            DockPanel.SetDock(sw, Dock.Right);
            sw.IsCheckedChanged += (s, e) => { if (_loading || set == null) return; var st = Settings; if (st == null) return; set(st, sw.IsChecked == true); body.Opacity = sw.IsChecked == true ? 1 : 0.55; Apply(); };
            if (set != null) header.Children.Add(sw);   // a section without a switch (always on)
            var t = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            if (tip != null) ToolTip.SetTip(t, tip);
            header.Children.Add(t);
            stack.Children.Add(header);
            foreach (var r in rows) body.Children.Add(r);
            stack.Children.Add(body);
            card.Child = stack;
            _refreshers.Add(() => { var st = Settings; if (st == null || get == null) return; sw.IsChecked = get(st); body.Opacity = get(st) ? 1 : 0.55; });
            return card;
        }

        private Control Slider(string label, string tip, double min, double max, Func<SceneSettings, float> get, Action<SceneSettings, float> set)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*,58"), Classes = { "proprow" } };
            var lbl = new TextBlock { Text = label, Classes = { "label", "small" } };
            if (tip != null) ToolTip.SetTip(lbl, tip);
            row.Children.Add(lbl);
            var slider = new Slider { Minimum = min, Maximum = max, IsSnapToTickEnabled = false, Margin = new Thickness(4, 0) };
            if (tip != null) ToolTip.SetTip(slider, tip);
            Grid.SetColumn(slider, 1);
            var box = new TextBox { Classes = { "number" }, MinHeight = 22, FontSize = 11 };
            Grid.SetColumn(box, 2);
            slider.ValueChanged += (s, e) => { if (_loading) return; var st = Settings; if (st == null) return; set(st, (float)slider.Value); box.Text = Fmt(slider.Value); Apply(); };
            box.LostFocus += (s, e) => Commit();
            box.KeyDown += (s, e) => { if (e.Key == Avalonia.Input.Key.Return) { Commit(); e.Handled = true; } };
            void Commit()
            {
                if (float.TryParse(box.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) slider.Value = Math.Max(min, Math.Min(max, v));
                box.Text = Fmt(slider.Value);
            }
            row.Children.Add(slider); row.Children.Add(box);
            _refreshers.Add(() => { var st = Settings; if (st == null) return; slider.Value = Math.Max(min, Math.Min(max, get(st))); box.Text = Fmt(get(st)); });
            return row;
        }

        private Control Choice(string label, string tip, string[] names, Func<SceneSettings, int> get, Action<SceneSettings, int> set)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*"), Classes = { "proprow" } };
            var lbl = new TextBlock { Text = label, Classes = { "label", "small" } };
            if (tip != null) ToolTip.SetTip(lbl, tip);
            row.Children.Add(lbl);
            var combo = new ComboBox { ItemsSource = names, MinHeight = 24, FontSize = 11, Margin = new Thickness(4, 0), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            if (tip != null) ToolTip.SetTip(combo, tip);
            Grid.SetColumn(combo, 1);
            combo.SelectionChanged += (s, e) => { if (_loading) return; var st = Settings; if (st == null || combo.SelectedIndex < 0) return; set(st, combo.SelectedIndex); Apply(); };
            row.Children.Add(combo);
            _refreshers.Add(() => { var st = Settings; if (st == null) return; int i = get(st); combo.SelectedIndex = i < 0 || i >= names.Length ? 0 : i; });
            return row;
        }

        private Control ColorRow(string label, Func<SceneSettings, (float, float, float)> get, Action<SceneSettings, (float r, float g, float b)> set)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*"), Classes = { "proprow" } };
            row.Children.Add(new TextBlock { Text = label, Classes = { "label", "small" } });
            var picker = new ColorPicker { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, Width = 90, Height = 22, IsAlphaEnabled = false, IsAlphaVisible = false };
            Grid.SetColumn(picker, 1);
            picker.ColorChanged += (s, e) => { if (_loading) return; var st = Settings; if (st == null) return; set(st, (e.NewColor.R / 255f, e.NewColor.G / 255f, e.NewColor.B / 255f)); Apply(); };
            row.Children.Add(picker);
            _refreshers.Add(() => { var st = Settings; if (st == null) return; var (r, g, b) = get(st); picker.Color = Color.FromRgb(B(r), B(g), B(b)); });
            return row;
        }

        private static byte B(float v) => (byte)Math.Max(0, Math.Min(255, (int)(v * 255f + 0.5f)));
        private static string Fmt(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        private void Apply()
        {
            var st = Settings; if (st == null) return;
            try { st.Apply(); } catch { }
            var scene = ActiveScene; if (scene != null) scene.IsDirty = true;
        }

        public void Refresh()
        {
            HookProject();
            _loading = true;
            try
            {
                bool has = Settings != null;
                Sections.IsEnabled = has;
                Sections.Opacity = has ? 1 : 0.5;
                NoScene.IsVisible = !has;
                PreviewFx.IsChecked = _previewFx;
                foreach (var r in _refreshers) r();
            }
            finally { _loading = false; }
        }

        private void HookProject()
        {
            var p = ProjectData.Current;
            if (ReferenceEquals(p, _hooked)) return;
            if (_hooked != null) _hooked.PropertyChanged -= OnProjectChanged;
            _hooked = p;
            if (p != null) p.PropertyChanged += OnProjectChanged;
        }

        private void OnProjectChanged(object s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ProjectData.ActiveScene)) Dispatcher.UIThread.Post(Refresh);
        }

        private void OnPreviewFx(object sender, RoutedEventArgs e) { if (!_loading) SetPreviewPostEffects(PreviewFx.IsChecked == true); }
    }
}
