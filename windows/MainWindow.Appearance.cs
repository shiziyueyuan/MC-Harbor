using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using MCHarbor.Windows.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace MCHarbor.Windows;

public sealed partial class MainWindow
{
    private const int DefaultGlassTransparency = 40;
    private const int DefaultGlassShadow = 24;
    private const int DefaultGlassBlur = 18;
    private readonly AccessibilitySettings _accessibility = new();
    private readonly UISettings _uiSettings = new();
    private WallpaperEntry[] _wallpapers = [];
    private volatile MediaPlayer? _wallpaperPlayer;
    private MediaPlayer? _voicePlayer;
    private MediaPlayer? _interfacePlayer;
    private InMemoryRandomAccessStream? _interfaceAudioStream;
    private InterfaceSoundbank? _interfaceSoundbank;
    private CanvasDevice? _wallpaperCanvasDevice;
    private CanvasRenderTarget? _wallpaperFrameBitmap;
    private CanvasBitmap? _wallpaperStillBitmap;
    private volatile bool _wallpaperFrameReady;
    private long _wallpaperRequest;
    private DependencyObject? _lastHoverSoundTarget;
    private DateTimeOffset _lastFocusSound = DateTimeOffset.MinValue;
    private bool _appearanceReady;
    private bool _appearanceWindowLoaded;
    private bool _glassMotionActive;
    private bool _glassLayerActive;
    private volatile bool _playingWallpaper;
    private bool _welcomePlayed;
    private bool _colorEventsAttached;
    private bool _contrastEventsAttached;
    private int _wallpaperFrameQueued;
    private long _lastWallpaperFrameTimestamp;
    private static string WallpaperDirectory => Path.Combine(AppPaths.DataDirectory, "wallpapers");
    private static readonly WallpaperLibraryStore WallpaperLibrary = new(WallpaperDirectory);
    private bool _wallpaperLibraryOpen;
    private Controls.WallpaperLibraryDialog? _wallpaperLibraryDialog;

    private void InitializeAppearance()
    {
        InitializeMacThemeSettings();
        ThemeMode.SelectedIndex = AppPaths.GetPreference("appearance", AppPaths.GetPreference("theme", "dark")) switch { "light" => 1, "ultraClear" => 2, "theme" => 3, _ => 0 };
        GlassEnabled.IsOn = PreferenceEnabled("glass", true);
        GlassTransparency.Value = ReadSettingNumber("glassTransparency", DefaultGlassTransparency, 0, 100);
        GlassBlur.Value = ReadSettingNumber("glassBlur", DefaultGlassBlur, 0, 40);
        WallpaperSoftness.Value = ReadSettingNumber("wallpaperSoftness", 0, 0, 24);
        WindowTransparency.Value = ReadSettingNumber("windowTransparency", 86, 0, 100);
        DownloadThreads.Value = ReadSettingNumber("downloadThreads", 8, 4, 24);
        PostLaunchMode.SelectedIndex = AppPaths.GetPreference("postLaunch", "keep") switch { "hide" => 1, "quit" => 2, _ => 0 };
        ReduceMotion.IsOn = PreferenceEnabled("reduceMotion");
        PerformanceMode.IsOn = PreferenceEnabled("performanceMode", false);
        WelcomeVoice.SelectedIndex = (int)ReadSettingNumber("welcomeVoice", 1, 0, 4);
        WelcomeVolume.Value = ReadSettingNumber("welcomeVolume", 65, 0, 100);
        InterfaceSounds.IsOn = PreferenceEnabled("interfaceSounds", true);
        InterfaceVolume.Value = ReadSettingNumber("interfaceVolume", 48, 0, 100);
        GameMemory.SelectedIndex = Math.Clamp((int)Math.Round(ReadSettingNumber("gameMemory", 3072, 2048, 6144) / 1024) - 2, 0, 4);
        GlobalGameJava.Text = AppPaths.GetPreference("gameJava");
        TranslationProvider.SelectedIndex = (int)ReadSettingNumber("translationProvider", 0, 0, 3);
        TranslationQwenEndpoint.Text = AppPaths.GetPreference("translationQwenEndpoint", OnlineTranslationClient.QwenAddress(null).AbsoluteUri);
        UpdateTranslationSettings();
        CompleteDependencies.IsOn = PreferenceEnabled("completeDependencies");
        DownloadSource.SelectedIndex = AppPaths.GetPreference("downloadSource") switch { "official" => 1, "mirror" => 2, _ => 0 };
        DataDirectoryText.Text = AppPaths.DataDirectory;
        WindowsVersionText.Text = $"MC Harbor for Windows {AppPaths.Version} · {WindowsPlatform.ClientArchitecture.ToUpperInvariant()}";

        _appearanceReady = true;
        ApplyTheme();
        // Some Windows 10 desktops do not expose the notification source to unpackaged apps.
        // Read the settings at startup and on activation even if subscribing is unavailable.
        try
        {
            _accessibility.HighContrastChanged += OnHighContrastChanged;
            _contrastEventsAttached = true;
        }
        catch (System.Runtime.InteropServices.COMException) { }
        try
        {
            _uiSettings.ColorValuesChanged += OnColorsChanged;
            _colorEventsAttached = true;
        }
        catch (System.Runtime.InteropServices.COMException) { }
        Activated += OnAppearanceActivated;
        AppWindow.Changed += OnAppearanceWindowChanged;
        Closed += OnAppearanceClosed;
        RootShell.AddHandler(UIElement.PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnGlassPointerMoved), true);
        RootShell.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnGlassPointerPressed), true);
        RootShell.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(OnGlassKeyDown), true);
        RootShell.PointerExited += OnGlassPointerExited;
    }

    private async Task InitializeWallpaperAsync()
    {
        if (_welcomePlayed) return;
        _welcomePlayed = true;
        await RefreshWallpaperLibraryAsync();
        PlayInterfaceSound("startup");
        if (!PerformanceMode.IsOn) PlayWelcome();
    }

    private static bool PreferenceEnabled(string name, bool fallback = false) =>
        AppPaths.GetPreference(name, fallback ? "true" : "false") == "true";

    private static double ReadSettingNumber(string name, double fallback, double minimum, double maximum) =>
        double.TryParse(AppPaths.GetPreference(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private void ThemeMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_appearanceReady) return;
        SaveSetting("appearance", ThemeMode.SelectedIndex switch { 1 => "light", 2 => "ultraClear", 3 => "theme", _ => "dark" });
        ApplyTheme();
    }

    private void AppearanceToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_appearanceReady || sender is not ToggleSwitch toggle) return;
        var key = toggle.Name switch
        {
            nameof(GlassEnabled) => "glass",
            nameof(ReduceMotion) => "reduceMotion", nameof(PerformanceMode) => "performanceMode",
            nameof(InterfaceSounds) => "interfaceSounds",
            nameof(CompleteDependencies) => "completeDependencies", _ => ""
        };
        if (key.Length == 0) return;
        SaveSetting(key, toggle.IsOn ? "true" : "false");
        if (key == "interfaceSounds" && !toggle.IsOn)
        {
            _interfacePlayer?.Pause();
            _interfacePlayer?.Dispose();
            _interfaceAudioStream?.Dispose();
            _interfacePlayer = null;
            _interfaceAudioStream = null;
        }
        ApplyTheme();
        UpdateWallpaperPlayback();
    }

    private void GlassMaterial_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_appearanceReady || sender is not Slider slider) return;
        var key = slider.Name switch
        {
            nameof(GlassTransparency) => "glassTransparency",
            nameof(GlassBlur) => "glassBlur",
            _ => ""
        };
        if (key.Length == 0) return;
        SaveSetting(key, ((int)e.NewValue).ToString(CultureInfo.InvariantCulture));
        ApplyTheme();
    }

    private void WallpaperTuning_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_appearanceReady) return;
        SaveSetting("wallpaperSoftness",
            ((int)e.NewValue).ToString(CultureInfo.InvariantCulture));
        QueueBackdropRefresh();
    }

    private void WelcomeVoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_appearanceReady) SaveSetting("welcomeVoice", Math.Max(0, WelcomeVoice.SelectedIndex).ToString(CultureInfo.InvariantCulture));
    }

    private void WelcomeVolume_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_appearanceReady) return;
        SaveSetting("welcomeVolume", ((int)e.NewValue).ToString(CultureInfo.InvariantCulture));
        if (_voicePlayer is not null) _voicePlayer.Volume = e.NewValue / 100;
    }

    private void InterfaceVolume_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_appearanceReady) return;
        SaveSetting("interfaceVolume", ((int)e.NewValue).ToString(CultureInfo.InvariantCulture));
        if (_interfacePlayer is not null) _interfacePlayer.Volume = e.NewValue / 100;
    }

    private void DownloadSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_appearanceReady) SaveSetting("downloadSource", DownloadSource.SelectedIndex switch { 1 => "official", 2 => "mirror", _ => "auto" });
    }

    private void GameMemory_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_appearanceReady && GameMemory.SelectedIndex >= 0)
            SaveSetting("gameMemory", ((GameMemory.SelectedIndex + 2) * 1024).ToString(CultureInfo.InvariantCulture));
    }

    private void SaveSetting(string key, string value)
    {
        try { AppPaths.SetPreference(key, value); }
        catch (Exception error) { ShowStatus("设置无法保存：" + error.Message, InfoBarSeverity.Error); }
    }

    private void OnHighContrastChanged(AccessibilitySettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { ApplyTheme(); UpdateWallpaperPlayback(); });

    private void OnColorsChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { ApplyTheme(); UpdateWallpaperPlayback(); });

    private void OnAppearanceActivated(object sender, WindowActivatedEventArgs args)
    {
        ApplyTheme();
        UpdateWallpaperPlayback();
        UpdateAccountSkinOptions();
    }

    private bool AllowsAmbientAnimation() => !ReduceMotion.IsOn && _uiSettings.AnimationsEnabled
        && !_accessibility.HighContrast && !PerformanceMode.IsOn && AppWindow.IsVisible
        && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    private void ApplyTheme()
    {
        var systemBackground = _uiSettings.GetColorValue(UIColorType.Background);
        var dark = ThemeMode.SelectedIndex is 0 or 2;
        RootShell.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        var highContrast = _accessibility.HighContrast;
        _chromeHighContrast = highContrast;
        _glassLayerActive = _uiSettings.AdvancedEffectsEnabled && !highContrast;
        GlassTransparency.IsEnabled = !GlassEnabled.IsOn;
        _glassMotionActive = _glassLayerActive && _chromeActive && AllowsAmbientAnimation();
        ApplyHarborPalette(dark, highContrast, systemBackground);
        UpdateAccountSkinOptions();
        var closeColor = highContrast ? _uiSettings.GetColorValue(UIColorType.Foreground) : Color.FromArgb(255, 225, 105, 93);
        var minimizeColor = highContrast ? closeColor : Color.FromArgb(255, 244, 197, 54);
        var maximizeColor = highContrast ? closeColor : Color.FromArgb(255, 101, 190, 54);
        var glyphColor = new SolidColorBrush(highContrast ? systemBackground : Color.FromArgb(255, 55, 52, 44));
        ChromeCloseDot.Background = new SolidColorBrush(closeColor);
        ChromeMinimizeDot.Background = new SolidColorBrush(minimizeColor);
        ChromeMaximizeDot.Background = new SolidColorBrush(maximizeColor);
        ChromeCloseGlyph.Foreground = glyphColor;
        ChromeMinimizeGlyph.Foreground = glyphColor;
        ChromeMaximizeGlyph.Foreground = glyphColor;
        UpdateChromeGlyphs();
    }

    private void OnAppearanceWindowChanged(AppWindow sender, AppWindowChangedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() => { UpdateWallpaperPlayback(); UpdateAccountSkinOptions(); });

    private void UpdateWallpaperPlayback()
    {
        if (_wallpaperPlayer is null) return;
        var animate = _chromeActive && !_wallpaperLibraryOpen && AllowsAmbientAnimation();
        if (_playingWallpaper == animate) return;
        _playingWallpaper = animate;
        if (animate) _wallpaperPlayer.Play();
        else _wallpaperPlayer.Pause();
    }

    private async Task RefreshWallpaperLibraryAsync(bool applyWallpaper = true)
    {
        try
        {
            _wallpapers = WallpaperLibrary.Read();
            var selected = _wallpapers.FirstOrDefault(item => item.FileName == AppPaths.GetPreference("wallpaper"));
            if (applyWallpaper) await ApplyWallpaperAsync(selected);
        }
        catch (Exception error) { ShowStatus("壁纸库无法读取：" + error.Message, InfoBarSeverity.Warning); }
    }

    private async Task ApplyWallpaperAsync(WallpaperEntry? entry)
    {
        StopWallpaper();
        var request = _wallpaperRequest;
        if (entry is null || entry.FileName.Length == 0) return;
        try
        {
            var path = WallpaperLibrary.FilePath(entry);
            var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            if (request != _wallpaperRequest) return;
            if (entry.Video)
            {
                var player = new MediaPlayer
                {
                    IsMuted = true,
                    IsLoopingEnabled = true,
                    IsVideoFrameServerEnabled = true
                };
                _wallpaperPlayer = player;
                player.MediaFailed += (_, _) => DispatcherQueue.TryEnqueue(() =>
                {
                    if (_wallpaperPlayer != player) return;
                    StopWallpaper();
                    ShowStatus("无法解码这段视频。请尝试 MP4 / H.264。", InfoBarSeverity.Warning);
                });
                player.VideoFrameAvailable += WallpaperVideoFrameAvailable;
                player.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(() => WallpaperVideoOpened(player));
                player.PlaybackSession.NaturalVideoSizeChanged += (_, _) =>
                    DispatcherQueue.TryEnqueue(() => InitializeWallpaperFrameSurface(player));
                player.Source = MediaSource.CreateFromStorageFile(file);
                UpdateWallpaperPlayback();
            }
            else
            {
                var image = await LoadWallpaperCanvasBitmapAsync(file);
                if (request != _wallpaperRequest)
                {
                    image.Dispose();
                    return;
                }
                _wallpaperStillBitmap = image;
                QueueBackdropRefresh();
            }
        }
        catch (Exception error)
        {
            if (request != _wallpaperRequest) return;
            StopWallpaper();
            ShowStatus("壁纸无法显示：" + error.Message, InfoBarSeverity.Warning);
        }
    }

    private void StopWallpaper()
    {
        _wallpaperRequest++;
        var player = _wallpaperPlayer;
        _wallpaperPlayer = null;
        _playingWallpaper = false;
        if (player is not null)
        {
            player.VideoFrameAvailable -= WallpaperVideoFrameAvailable;
            player.Dispose();
        }
        _wallpaperFrameBitmap?.Dispose();
        _wallpaperFrameBitmap = null;
        _wallpaperFrameReady = false;
        _wallpaperStillBitmap?.Dispose();
        _wallpaperStillBitmap = null;
        QueueBackdropRefresh();
    }

    private async Task<CanvasBitmap> LoadWallpaperCanvasBitmapAsync(global::Windows.Storage.StorageFile file)
    {
        _wallpaperCanvasDevice ??= CanvasDevice.GetSharedDevice();
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var scale = Math.Min(1d, 1920d / Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale))
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        return CanvasBitmap.CreateFromSoftwareBitmap(_wallpaperCanvasDevice, bitmap);
    }

    private void WallpaperVideoOpened(MediaPlayer player)
    {
        InitializeWallpaperFrameSurface(player);
        if (!ReferenceEquals(_wallpaperPlayer, player) || _playingWallpaper || _wallpaperFrameReady) return;
        try { player.StepForwardOneFrame(); }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    private void InitializeWallpaperFrameSurface(MediaPlayer player)
    {
        if (!ReferenceEquals(_wallpaperPlayer, player)) return;
        var naturalWidth = (int)player.PlaybackSession.NaturalVideoWidth;
        var naturalHeight = (int)player.PlaybackSession.NaturalVideoHeight;
        if (naturalWidth <= 0 || naturalHeight <= 0) return;

        var resize = Math.Min(1d, Math.Min(1920d / naturalWidth, 1080d / naturalHeight));
        var width = Math.Max(1, (int)Math.Round(naturalWidth * resize));
        var height = Math.Max(1, (int)Math.Round(naturalHeight * resize));
        try
        {
            if (_wallpaperFrameBitmap?.SizeInPixels.Width == width
                && _wallpaperFrameBitmap.SizeInPixels.Height == height) return;
            _wallpaperCanvasDevice ??= CanvasDevice.GetSharedDevice();
            _wallpaperFrameBitmap?.Dispose();
            _wallpaperFrameBitmap = new CanvasRenderTarget(_wallpaperCanvasDevice, width, height, 96);
            _wallpaperFrameReady = false;
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException
            or System.Runtime.InteropServices.COMException)
        {
            StopWallpaper();
            ShowStatus("视频壁纸渲染器无法初始化：" + error.Message, InfoBarSeverity.Warning);
        }
    }

    private void WallpaperVideoFrameAvailable(MediaPlayer sender, object args)
    {
        if (!ReferenceEquals(_wallpaperPlayer, sender) || (!_playingWallpaper && _wallpaperFrameReady)) return;
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref _lastWallpaperFrameTimestamp);
        if (now - previous < Stopwatch.Frequency / 24
            || Interlocked.Exchange(ref _wallpaperFrameQueued, 1) != 0) return;
        Interlocked.Exchange(ref _lastWallpaperFrameTimestamp, now);

        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                if (!ReferenceEquals(_wallpaperPlayer, sender) || (!_playingWallpaper && _wallpaperFrameReady)) return;
                if (_wallpaperFrameBitmap is null)
                    InitializeWallpaperFrameSurface(sender);
                if (_wallpaperFrameBitmap is null) return;

                sender.CopyFrameToVideoSurface(_wallpaperFrameBitmap,
                    new Rect(0, 0, _wallpaperFrameBitmap.SizeInPixels.Width, _wallpaperFrameBitmap.SizeInPixels.Height));
                _wallpaperFrameReady = true;
                DrawBackdrop();
            }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException
                or System.Runtime.InteropServices.COMException)
            {
                if (ReferenceEquals(_wallpaperPlayer, sender))
                {
                    StopWallpaper();
                    ShowStatus("视频壁纸帧处理失败：" + error.Message, InfoBarSeverity.Warning);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _wallpaperFrameQueued, 0);
            }
        }))
        {
            Interlocked.Exchange(ref _wallpaperFrameQueued, 0);
        }
    }

    private void PreviewWelcome_Click(object sender, RoutedEventArgs e) => PlayWelcome();

    private void PlayWelcome()
    {
        var name = WelcomeVoice.SelectedIndex switch
        {
            1 => "welcome.mp3", 2 => "welcome-female2.mp3",
            3 => "welcome-male.mp3", 4 => "welcome-male2.mp3", _ => ""
        };
        if (name.Length == 0) return;
        try
        {
            var path = Path.Combine(AppPaths.ApplicationDirectory, "Assets", "Voice", name);
            if (!File.Exists(path)) return;
            _voicePlayer ??= new MediaPlayer();
            _voicePlayer.Volume = WelcomeVolume.Value / 100;
            _voicePlayer.Source = MediaSource.CreateFromUri(new Uri(path));
            _voicePlayer.Play();
        }
        catch (Exception error) { ShowStatus("无法播放欢迎语音：" + error.Message, InfoBarSeverity.Warning); }
    }

    private async void OpenApplicationData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var folder = await global::Windows.Storage.StorageFolder.GetFolderFromPathAsync(AppPaths.DataDirectory);
            await global::Windows.System.Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception error) { ShowStatus(error.Message, InfoBarSeverity.Error); }
    }

    private async void RunDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ShowStatus("正在检查版本下载源…", InfoBarSeverity.Informational);
            var result = await RequireBridge().InvokeAsync("diagnostics");
            ShowStatus(result["report"]?.GetValue<string>() ?? "诊断完成。", InfoBarSeverity.Informational);
        }
        catch (Exception error) { ShowStatus(error.Message, InfoBarSeverity.Error); }
    }

    private void OnAppearanceClosed(object sender, WindowEventArgs args)
    {
        _appearanceWindowLoaded = false;
        if (_contrastEventsAttached) _accessibility.HighContrastChanged -= OnHighContrastChanged;
        if (_colorEventsAttached) _uiSettings.ColorValuesChanged -= OnColorsChanged;
        Activated -= OnAppearanceActivated;
        Activated -= WindowChrome_Activated;
        AppWindow.Changed -= OnAppearanceWindowChanged;
        AppWindow.Changed -= WindowChrome_Changed;
        _windowFrame?.Dispose();
        RootShell.Tag = null;
        CancelAccountSkinPreview();
        StopWallpaper();
        ReleaseBackdrop();
        _voicePlayer?.Dispose();
        _interfacePlayer?.Dispose();
        _interfaceAudioStream?.Dispose();
        _interfacePlayer = null;
        _interfaceAudioStream = null;
    }

    private void OnGlassPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        var target = FindInteractiveSoundTarget(args.OriginalSource as DependencyObject);
        if (!ReferenceEquals(target, _lastHoverSoundTarget))
        {
            _lastHoverSoundTarget = target;
            var now = DateTimeOffset.UtcNow;
            if (target is not null && _appearanceWindowLoaded && now - _lastFocusSound > TimeSpan.FromMilliseconds(90))
            {
                _lastFocusSound = now;
                PlayInterfaceSound("focus");
            }
        }
    }

    private void OnGlassPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        if (_appearanceWindowLoaded && FindInteractiveSoundTarget(args.OriginalSource as DependencyObject) is { } target
            && target is UIElement element && !SidebarItems.Children.Contains(element))
            PlayInterfaceSound("select");
    }

    private void OnGlassPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args) =>
        _lastHoverSoundTarget = null;

    private void OnGlassKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (!_appearanceWindowLoaded || args.Key is not (global::Windows.System.VirtualKey.Enter or global::Windows.System.VirtualKey.Space)) return;
        if (FindInteractiveSoundTarget(args.OriginalSource as DependencyObject) is { } target
            && target is UIElement element && !SidebarItems.Children.Contains(element))
            PlayInterfaceSound("select");
    }

    private static DependencyObject? FindInteractiveSoundTarget(DependencyObject? source)
    {
        for (var current = source; current is not null; current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
            if (current is ButtonBase or ComboBox or Slider or NumberBox or ListViewItem or GridViewItem)
                return current;
        return null;
    }

    private async void PlayInterfaceSound(string cue)
    {
        if (!_appearanceReady || !_appearanceWindowLoaded || !InterfaceSounds.IsOn || InterfaceVolume.Value <= 0) return;
        _interfaceSoundbank ??= InterfaceSoundbank.TryLoad(Path.Combine(AppPaths.ApplicationDirectory, "Assets", "Audio", "Interface.soundbank"));
        var bytes = _interfaceSoundbank?.GetCue(cue);
        if (bytes is null) return;

        try
        {
            _interfacePlayer?.Pause();
            _interfacePlayer?.Dispose();
            _interfaceAudioStream?.Dispose();
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var player = new MediaPlayer { Volume = InterfaceVolume.Value / 100 };
            _interfaceAudioStream = stream;
            _interfacePlayer = player;
            player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() => ReleaseInterfacePlayer(player, stream));
            player.MediaFailed += (_, _) => DispatcherQueue.TryEnqueue(() => ReleaseInterfacePlayer(player, stream));
            player.Source = MediaSource.CreateFromStream(stream, "audio/mpeg");
            player.Play();
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            _interfacePlayer?.Dispose();
            _interfaceAudioStream?.Dispose();
            _interfacePlayer = null;
            _interfaceAudioStream = null;
        }
    }

    private void ReleaseInterfacePlayer(MediaPlayer player, InMemoryRandomAccessStream stream)
    {
        if (!ReferenceEquals(_interfacePlayer, player)) return;
        player.Dispose();
        stream.Dispose();
        _interfacePlayer = null;
        _interfaceAudioStream = null;
    }
}
