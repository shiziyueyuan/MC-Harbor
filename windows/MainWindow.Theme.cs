using System.Text.Json;
using MCHarbor.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using Windows.UI;

namespace MCHarbor.Windows;

public sealed partial class MainWindow
{
    private HarborThemeSettings _themeSettings = new();
    private Rgb _themeColor = Rgb.Parse("#C3E3D4");
    private bool _buildingTheme;
    private readonly Dictionary<string, Ellipse> _paletteSwatches = [];
    private Border? _themePreview;
    private Border? _inversePreview;
    private Border? _accentPreview;
    private TextBlock? _buttonPreview;
    private TextBlock? _accentTextPreview;
    private static readonly (string Key, string Label)[] ThemeAreas =
    [
        ("buttonFill", "主要按钮背景"), ("buttonText", "按钮上的文字与图标"), ("controls", "选中状态与控件"),
        ("icons", "界面图标"), ("accentText", "强调文字"), ("primaryText", "主要文字"),
        ("secondaryText", "辅助文字"), ("surfaces", "玻璃卡片底色"), ("borders", "卡片与按钮边框"), ("effects", "欢迎动画与流体配色")
    ];

    private static Color ToColor(Rgb value, byte alpha = 255) => Color.FromArgb(alpha, value.R, value.G, value.B);
    private Color ThemeColorFor(string area, Color fallback) => ToColor(_themeSettings.Resolve(area, new(fallback.R, fallback.G, fallback.B), _themeColor), fallback.A);

    private void InitializeMacThemeSettings()
    {
        _buildingTheme = true;
        try
        {
            if (Rgb.TryParse(AppPaths.GetPreference("themeHex"), out var color)) _themeColor = color;
            try { _themeSettings = JsonSerializer.Deserialize<HarborThemeSettings>(AppPaths.GetPreference("themeCustomization", "{}"), HarborThemeSettings.Json) ?? new(); }
            catch (JsonException) { _themeSettings = new(); }
            _themeSettings.Roles ??= []; _themeSettings.Custom ??= [];
            var modes = new[] { ("深色", "#303030"), ("浅色", "#E8E8E8"), ("通透", "#809B9B"), ("主题", _themeColor.Hex) };
            foreach (var (label, hex) in modes)
            {
                var content = new StackPanel { Spacing = 12, MinWidth = 64 };
                content.Children.Add(new Border { Height = 62, CornerRadius = new(14), Background = new SolidColorBrush(ToColor(Rgb.Parse(hex))) });
                content.Children.Add(new TextBlock { Text = label, FontSize = 12 });
                ThemeMode.Items.Add(new RadioButton { Content = content, Style = (Style)RootShell.Resources["HarborAppearanceChoice"], HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
            ResizeAppearanceChoices();
            foreach (var hex in new[] { "#C3E3D4", "#C4DDD9", "#D8E2BE", "#D4D9D1", "#E8D9BD", "#E3D1DE" })
            {
                var swatch = new Ellipse { Width = 30, Height = 30, Fill = new SolidColorBrush(ToColor(Rgb.Parse(hex))) };
                _paletteSwatches[hex] = swatch;
                var button = new Button { Content = swatch, Width = 36, Height = 36, MinWidth = 0, MinHeight = 0,
                    Padding = new(3), CornerRadius = new(18), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new(0) };
                AutomationProperties.SetName(button, hex);
                button.Click += (_, _) => { _themeColor = Rgb.Parse(hex); SaveSetting("themeHex", hex); ApplyTheme(); UpdateThemePreviews(); };
                ThemePalette.Children.Add(button);
            }
            foreach (var (key, label) in ThemeAreas)
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new() { Width = new(130) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
                var roles = new[] { "system", "theme", "inverse", "custom" };
                var select = new ComboBox { Tag = key, ItemsSource = new[] { "默认", "主题色", "反色", "自定义" }, SelectedIndex = Math.Max(0, Array.IndexOf(roles, _themeSettings.Roles.GetValueOrDefault(key, "system"))), HorizontalAlignment = HorizontalAlignment.Stretch };
                var customColor = Rgb.TryParse(_themeSettings.Custom.GetValueOrDefault(key), out var saved) ? saved : _themeColor;
                var value = new Button { Content = new Border { Width = 30, Height = 22, CornerRadius = new(6), Background = new SolidColorBrush(ToColor(customColor)) },
                    Padding = new(2), MinWidth = 0, MinHeight = 0, Visibility = select.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed };
                AutomationProperties.SetName(select, label); AutomationProperties.SetName(value, label + "自定义颜色");
                select.SelectionChanged += (_, _) => { value.Visibility = select.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed; if (_buildingTheme) return; _themeSettings.Roles[key] = roles[Math.Max(0, select.SelectedIndex)]; SaveThemeSettings(); };
                value.Click += async (_, _) =>
                {
                    var current = Rgb.TryParse(_themeSettings.Custom.GetValueOrDefault(key), out var chosen) ? chosen : _themeColor;
                    var picker = new ColorPicker { Color = ToColor(current), IsAlphaEnabled = false };
                    if (await FriendDialog(label, picker, "应用").ShowAsync() != ContentDialogResult.Primary) return;
                    var parsed = new Rgb(picker.Color.R, picker.Color.G, picker.Color.B);
                    _themeSettings.Custom[key] = parsed.Hex;
                    ((Border)value.Content).Background = new SolidColorBrush(ToColor(parsed));
                    SaveThemeSettings();
                };
                Grid.SetColumn(select, 1); Grid.SetColumn(value, 2); row.Children.Add(select); row.Children.Add(value); ThemeScopeRows.Children.Add(row);
            }
            var inverse = new ComboBox { Header = "反色方式", ItemsSource = new[] { "自动黑白对比色", "RGB 反色", "色相互补色", "自定义反色" }, SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "contrast", "rgb", "complementary", "custom" }, _themeSettings.InverseMode)) };
            ThemeScopeRows.Children.Add(inverse);
            var inverseHex = new TextBox { Header = "自定义反色", Text = _themeSettings.InverseHex, MaxLength = 7, MaxWidth = 200, HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = inverse.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed };
            inverse.SelectionChanged += (_, _) => { _themeSettings.InverseMode = new[] { "contrast", "rgb", "complementary", "custom" }[Math.Max(0, inverse.SelectedIndex)]; inverseHex.Visibility = inverse.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed; SaveThemeSettings(); };
            inverseHex.LostFocus += (_, _) => { if (Rgb.TryParse(inverseHex.Text, out var custom)) { _themeSettings.InverseHex = custom.Hex; SaveThemeSettings(); } else inverseHex.Text = _themeSettings.InverseHex; };
            ThemeScopeRows.Children.Add(inverseHex);
            AddThemeSlider("反色亮度", -0.5, 0.5, _themeSettings.InverseBrightness, value => _themeSettings.InverseBrightness = value);
            AddThemeSlider("反色饱和度", 0, 2, _themeSettings.InverseSaturation, value => _themeSettings.InverseSaturation = value);
            AddThemeSlider("卡片染色强度", 0, 0.7, _themeSettings.SurfaceStrength, value => _themeSettings.SurfaceStrength = value);
            AddThemeSlider("边框强度", 0, 1, _themeSettings.BorderStrength, value => _themeSettings.BorderStrength = value);
            var protect = new ToggleSwitch { IsOn = _themeSettings.ProtectContrast };
            protect.Toggled += (_, _) => { _themeSettings.ProtectContrast = protect.IsOn; SaveThemeSettings(); };
            ThemeScopeRows.Children.Add(MacSwitchRow("保护文字对比度", protect));
            AddThemeRoleActions();
            AddThemePreviewExplanation();
            UpdateThemePreviews();
            InitializeCollectionDisplayOptions();
            LoadContributors();
        }
        finally { _buildingTheme = false; }
    }

    private async void ThemeCustomColor_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ColorPicker { Color = ToColor(_themeColor), IsAlphaEnabled = false, IsColorSliderVisible = true };
        if (await FriendDialog("主题色", picker, "应用").ShowAsync() != ContentDialogResult.Primary) return;
        _themeColor = new(picker.Color.R, picker.Color.G, picker.Color.B);
        SaveSetting("themeHex", _themeColor.Hex); ApplyTheme(); UpdateThemePreviews();
    }

    private void ThemeMode_SizeChanged(object sender, SizeChangedEventArgs e) => ResizeAppearanceChoices();

    private void ResizeAppearanceChoices()
    {
        if (ThemeMode is null || ThemeMode.ActualWidth < 1) return;
        var width = Math.Max(1, (ThemeMode.ActualWidth - 36) / 4);
        var index = 0;
        foreach (var button in ThemeMode.Items.OfType<RadioButton>())
        {
            button.Width = width;
            button.Margin = new Thickness(0, 0, ++index < 4 ? 12 : 0, 0);
        }
    }

    private void AddThemeSlider(string title, double minimum, double maximum, double value, Action<double> assign)
    {
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : minimum, StepFrequency = 0.01 };
        slider.ValueChanged += (_, e) => { if (_buildingTheme) return; assign(e.NewValue); SaveThemeSettings(); };
        var row = new Grid { ColumnSpacing = 20, MinHeight = 26 };
        row.ColumnDefinitions.Add(new() { Width = new(90) }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new(42) });
        row.Children.Add(new TextBlock { Text = title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var number = new TextBlock { Text = (100 * slider.Value).ToString("0") + "%", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) => number.Text = (100 * e.NewValue).ToString("0") + "%";
        Grid.SetColumn(slider, 1); Grid.SetColumn(number, 2); row.Children.Add(slider); row.Children.Add(number);
        ThemeScopeRows.Children.Add(row);
    }

    private static Grid MacSwitchRow(string label, ToggleSwitch toggle)
    {
        var row = new Grid { ColumnSpacing = 20, MinHeight = 26 };
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = label, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        toggle.Header = null; toggle.OnContent = toggle.OffContent = ""; toggle.MinWidth = toggle.Width = 44;
        AutomationProperties.SetName(toggle, label); Grid.SetColumn(toggle, 1); row.Children.Add(toggle);
        return row;
    }

    private void AddThemeRoleActions()
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Grid.SetColumn(buttons, 2); row.Children.Add(swatches); row.Children.Add(buttons);
        Border Swatch(string title)
        {
            var swatch = new Border { Width = 42, Height = 30, CornerRadius = new(9) };
            var column = new StackPanel { Spacing = 5, Children = { swatch, new TextBlock { Text = title, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center } } };
            swatches.Children.Add(column);
            return swatch;
        }
        _themePreview = Swatch("主题色"); _inversePreview = Swatch("反色"); _accentPreview = Swatch("强调文字");
        foreach (var (label, all) in new[] { ("恢复默认范围", false), ("全界面跟随主题", true) })
        {
            var button = new Button { Content = label, Style = (Style)RootShell.Resources["HarborPill"] };
            button.Click += (_, _) =>
            {
                _themeSettings.Roles = all ? ThemeAreas.ToDictionary(item => item.Key, item => item.Key == "buttonText" ? "inverse" : "theme") : new HarborThemeSettings().Roles;
                _buildingTheme = true;
                foreach (var select in ThemeScopeRows.Children.OfType<Grid>().SelectMany(grid => grid.Children.OfType<ComboBox>()))
                    if (select.Tag is string key) select.SelectedIndex = Array.IndexOf(new[] { "system", "theme", "inverse", "custom" }, _themeSettings.Roles.GetValueOrDefault(key, "system"));
                _buildingTheme = false; SaveThemeSettings();
            };
            buttons.Children.Add(button);
        }
        ThemeScopeRows.Children.Insert(0, row);
    }

    private void AddThemePreviewExplanation()
    {
        ThemeScopeRows.Children.Add(new TextBlock
        {
            Text = "每类元素可独立使用默认色、主题色、反色或自定义色。自动保护会调整文字明暗；关闭后按原色显示。模组原图标与皮肤保持原图。",
            FontSize = 11, Opacity = 0.72, TextWrapping = TextWrapping.Wrap
        });
        _buttonPreview = new TextBlock { Text = "主题按钮预览", Padding = new Thickness(12), FontSize = 12 };
        _accentTextPreview = new TextBlock { Text = "强调文字预览", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var button = new Border { Child = _buttonPreview, CornerRadius = new(30) };
        ThemeScopeRows.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { button, _accentTextPreview } });
    }

    private void UpdateThemePreviews()
    {
        ThemeCustomColorSwatch.Background = new SolidColorBrush(ToColor(_themeColor));
        foreach (var (hex, circle) in _paletteSwatches)
        {
            circle.Stroke = new SolidColorBrush(Microsoft.UI.Colors.White);
            circle.StrokeThickness = hex.Equals(_themeColor.Hex, StringComparison.OrdinalIgnoreCase) ? 3 : 0;
        }
        var accent = _themeSettings.Resolve("accentText", _themeColor, _themeColor);
        if (_themePreview is not null) _themePreview.Background = new SolidColorBrush(ToColor(_themeColor));
        if (_inversePreview is not null) _inversePreview.Background = new SolidColorBrush(ToColor(_themeSettings.Inverse(_themeColor)));
        if (_accentPreview is not null) _accentPreview.Background = new SolidColorBrush(ToColor(accent));
        if (_accentTextPreview is not null) _accentTextPreview.Foreground = new SolidColorBrush(ToColor(accent));
        if (_buttonPreview?.Parent is Border button)
        {
            var fill = _themeSettings.Resolve("buttonFill", _themeColor, _themeColor);
            var ink = _themeSettings.Resolve("buttonText", new Rgb(20, 37, 38), _themeColor);
            button.Background = new SolidColorBrush(ToColor(fill));
            _buttonPreview.Foreground = new SolidColorBrush(ToColor(Rgb.Protect(ink, fill, _themeSettings.ProtectContrast)));
        }
    }

    private static readonly (string Key, string Label, bool Default)[] CollectionOptions =
    [
        ("icon", "模组图标", true), ("loader", "加载器", true), ("modVersion", "模组版本", true),
        ("gameVersion", "Minecraft 版本", false), ("collectedAt", "收藏时间", false), ("summary", "模组简介", false), ("authors", "作者", false)
    ];

    private void InitializeCollectionDisplayOptions()
    {
        CollectionDisplayOptions.Children.Clear();
        CollectionDisplayOptions.Children.Add(new TextBlock { Text = "模组名称始终显示。完整资料仍可从详情查看；旧收藏未记录的资料不会凭空补造。", FontSize = 11, Opacity = 0.72, TextWrapping = TextWrapping.Wrap });
        foreach (var (key, label, fallback) in CollectionOptions)
        {
            var toggle = new ToggleSwitch { IsOn = PreferenceEnabled("collectionDisplay." + key, fallback) };
            toggle.Toggled += (_, _) => { SaveSetting("collectionDisplay." + key, toggle.IsOn ? "true" : "false"); RefreshModFolders(); };
            CollectionDisplayOptions.Children.Add(MacSwitchRow(label, toggle));
        }
        foreach (var (label, defaults) in new[] { ("仅显示模组名称", false), ("恢复默认信息量", true) })
        {
            var button = new Button { Content = label, Style = (Style)RootShell.Resources["HarborPill"] };
            button.Click += (_, _) =>
            {
                AppPaths.SetPreferences(CollectionOptions.Select(item => ("collectionDisplay." + item.Key, (string?)(defaults && item.Default ? "true" : "false"))).ToArray());
                InitializeCollectionDisplayOptions(); RefreshModFolders();
            };
            CollectionDisplayOptions.Children.Add(button);
        }
    }

    private void ThemeScopeToggle_Click(object sender, RoutedEventArgs e)
    {
        ThemeScopeRows.Visibility = ThemeScopeRows.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        ThemeScopeChevron.Text = ThemeScopeRows.Visibility == Visibility.Visible ? "⌃" : "⌄";
        SettingsThemeScopes.InvalidateMeasure();
        AppearanceSettings.InvalidateMeasure();
        QueueGlassCardsRefresh();
    }
    private void SaveThemeSettings()
    {
        if (_buildingTheme) return;
        SaveSetting("themeCustomization", JsonSerializer.Serialize(_themeSettings, HarborThemeSettings.Json)); ApplyTheme(); UpdateThemePreviews();
    }

    private void ApplyThemeText(bool dark, bool highContrast)
    {
        var background = dark ? new Rgb(26, 26, 26) : new Rgb(240, 240, 240);
        var systemInk = dark ? new Rgb(240, 240, 240) : new Rgb(24, 24, 24);
        var primary = _themeSettings.Resolve("primaryText", systemInk, _themeColor);
        var secondary = _themeSettings.Resolve("secondaryText", dark ? new(190, 190, 190) : new(88, 88, 88), _themeColor);
        if (highContrast)
        {
            var ink = _uiSettings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Foreground);
            primary = secondary = new(ink.R, ink.G, ink.B);
        }
        else
        {
            primary = Rgb.Protect(primary, background, _themeSettings.ProtectContrast);
            secondary = Rgb.Protect(secondary, background, _themeSettings.ProtectContrast);
        }
        SetThemeBrush("TextFillColorPrimaryBrush", ToColor(primary));
        SetThemeBrush("TextFillColorSecondaryBrush", ToColor(secondary));
        PageTitle.Foreground = new SolidColorBrush(ToColor(primary));
        WorkspaceTitle.Foreground = new SolidColorBrush(ToColor(primary));
        var iconStyle = new Style(typeof(FontIcon));
        iconStyle.Setters.Add(new Setter(IconElement.ForegroundProperty, new SolidColorBrush(highContrast ? ToColor(primary) : ThemeColorFor("icons", ToColor(primary)))));
        RootShell.Resources[typeof(FontIcon)] = iconStyle;
        var controls = highContrast ? _uiSettings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Accent) : ThemeColorFor("controls", ToColor(_themeColor));
        SetThemeBrush("AccentFillColorDefaultBrush", controls);
        SetThemeBrush("AccentFillColorSecondaryBrush", controls);
        SetThemeBrush("AccentTextFillColorPrimaryBrush", highContrast ? ToColor(primary) : ThemeColorFor("accentText", controls));
    }

    private void SetThemeBrush(string key, Color color)
    {
        if (RootShell.Resources.TryGetValue(key, out var existing) && existing is SolidColorBrush brush) brush.Color = color;
        else RootShell.Resources[key] = new SolidColorBrush(color);
    }

    private void PreviewInterfaceSound_Click(object sender, RoutedEventArgs e) => PlayInterfaceSound("open");

    private void LoadContributors()
    {
        try
        {
            var directory = Path.Combine(AppPaths.ApplicationDirectory, "Assets", "Contributors");
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "contributors.json")));
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var row = new Grid { ColumnSpacing = 16 };
                row.ColumnDefinitions.Add(new() { Width = new(52) }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                var avatar = item.GetProperty("avatar").GetString() ?? "";
                if (Path.GetFileName(avatar) == avatar && File.Exists(Path.Combine(directory, avatar)))
                    row.Children.Add(new Border { Width = 52, Height = 52, CornerRadius = new(26), Child = new Image { Source = new BitmapImage(new Uri(Path.Combine(directory, avatar))) { DecodePixelWidth = 104 }, Stretch = Stretch.UniformToFill } });
                var content = new StackPanel { Spacing = 6 }; Grid.SetColumn(content, 1); row.Children.Add(content);
                content.Children.Add(new TextBlock { Text = item.GetProperty("name").GetString(), FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                foreach (var key in new[] { "role", "description" })
                    if (item.TryGetProperty(key, out var value) && !string.IsNullOrWhiteSpace(value.GetString())) content.Children.Add(new TextBlock { Text = value.GetString(), FontSize = 12, Opacity = 0.72, TextWrapping = TextWrapping.Wrap });
                if (item.TryGetProperty("url", out var url) && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var address) && address.Scheme == "https" && address.Host == "github.com")
                    content.Children.Add(new HyperlinkButton { Content = "GitHub 主页", NavigateUri = address, Padding = new(0) });
                if (item.TryGetProperty("contributions", out var contributions))
                    foreach (var value in contributions.EnumerateArray()) content.Children.Add(new TextBlock { Text = value.GetString(), FontSize = 12, Opacity = 0.72, TextWrapping = TextWrapping.Wrap });
                (item.TryGetProperty("category", out var category) && category.GetString() is "author" or "developer" ? DeveloperRows : ContributorRows).Children.Add(row);
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { StartupDiagnostics.Write("贡献者资料未能载入", error); }
    }

    private void WindowTransparency_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_appearanceReady) return;
        SaveSetting("windowTransparency", e.NewValue.ToString(System.Globalization.CultureInfo.InvariantCulture)); ApplyTheme();
    }
    private void DownloadThreads_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_appearanceReady && double.IsFinite(e.NewValue)) SaveSetting("downloadThreads", Math.Clamp((int)e.NewValue, 4, 24).ToString());
    }
    private void GlobalGameJava_LostFocus(object sender, RoutedEventArgs e) => SaveSetting("gameJava", GlobalGameJava.Text.Trim());
    private async void ResetWallpaper_Click(object sender, RoutedEventArgs e)
    {
        SaveSetting("wallpaper", ""); await RefreshWallpaperLibraryAsync();
    }
}
