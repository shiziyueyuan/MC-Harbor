using System.Globalization;
using System.Text.Json;
using MCHarbor.Core.Models;
using MCHarbor.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MCHarbor.Windows;

public sealed partial class MainWindow
{
    private sealed record InstanceOptions(int Memory = 0, int Width = 1280, int Height = 800, bool Fullscreen = false, string JvmArguments = "");
    private bool _loadingInstanceOptions;
    private CancellationTokenSource? _instanceSpaceCancellation;
    private static string InstanceOptionsKey(GameInstance instance) => "instanceOptions:" + instance.Directory.ToUpperInvariant();
    private static string InstanceNicknameKey(GameInstance instance) => "instanceNickname:" + instance.Directory.ToUpperInvariant();
    private static string InstanceIconKey(GameInstance instance) => "instanceIcon:" + instance.Directory.ToUpperInvariant();

    private static InstanceOptions ReadInstanceOptions(GameInstance instance)
    {
        try { return JsonSerializer.Deserialize<InstanceOptions>(AppPaths.GetPreference(InstanceOptionsKey(instance), "{}")) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private void LoadInstanceOptions(GameInstance instance)
    {
        _loadingInstanceOptions = true;
        try
        {
            var options = ReadInstanceOptions(instance);
            InstanceSettingsIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(instance.Artwork));
            InstanceRepairButton.Tag = InstanceExportButton.Tag = InstanceRevealButton.Tag = instance.Id;
            InstanceNickname.Text = AppPaths.GetPreference(InstanceNicknameKey(instance));
            InstanceMemory.SelectedIndex = options.Memory == 0 ? 0 : Math.Clamp(options.Memory / 1024 - 1, 1, 5);
            InstanceJavaPath.Text = AppPaths.GetPreference(JavaPreferenceKey(instance));
            InstanceFullscreen.IsOn = options.Fullscreen;
            InstanceWidth.Value = Math.Clamp(options.Width, 640, 7680);
            InstanceHeight.Value = Math.Clamp(options.Height, 480, 4320);
            InstanceJvmArguments.Text = options.JvmArguments ?? "";
        }
        finally { _loadingInstanceOptions = false; }
    }

    private void SaveInstanceOptions()
    {
        if (_loadingInstanceOptions || _activeModsInstance is not { } instance
            || !double.IsFinite(InstanceWidth.Value) || !double.IsFinite(InstanceHeight.Value)) return;
        var options = new InstanceOptions(InstanceMemory.SelectedIndex <= 0 ? 0 : (InstanceMemory.SelectedIndex + 1) * 1024,
            (int)Math.Clamp(InstanceWidth.Value, 640, 7680), (int)Math.Clamp(InstanceHeight.Value, 480, 4320),
            InstanceFullscreen.IsOn, InstanceJvmArguments.Text);
        SaveSetting(InstanceOptionsKey(instance), JsonSerializer.Serialize(options));
        SaveSetting(JavaPreferenceKey(instance), InstanceJavaPath.Text.Trim());
    }

    private void InstanceOptionSelection_Changed(object sender, SelectionChangedEventArgs e) => SaveInstanceOptions();
    private void InstanceOptionToggle_Changed(object sender, RoutedEventArgs e) => SaveInstanceOptions();
    private void InstanceOptionNumber_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e) => SaveInstanceOptions();
    private void InstanceOption_LostFocus(object sender, RoutedEventArgs e) => SaveInstanceOptions();

    private async void InstanceNickname_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loadingInstanceOptions || _activeModsInstance is not { } instance) return;
        SaveSetting(InstanceNicknameKey(instance), InstanceNickname.Text.Trim());
        await RefreshInstancesAsync();
    }

    private async void ResetInstanceIcon_Click(object sender, RoutedEventArgs e)
    {
        if (_activeModsInstance is not { } instance) return;
        AppPaths.SetPreferences((InstanceIconKey(instance), null));
        await RefreshInstancesAsync();
    }

    private async void MeasureInstanceSpace_Click(object sender, RoutedEventArgs e) => await MeasureInstanceSpaceAsync();

    private async Task MeasureInstanceSpaceAsync()
    {
        if (_activeModsInstance is not { } instance) return;
        _instanceSpaceCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(); _instanceSpaceCancellation = cancellation;
        InstanceSpaceText.Text = "正在计算…";
        try
        {
            var footprint = await InstanceFootprintReader.MeasureAsync(instance, cancellation.Token);
            if (_activeModsInstance != instance) return;
            static string Size(long bytes)
            {
                var units = new[] { "B", "KB", "MB", "GB", "TB" }; double value = bytes; var unit = 0;
                while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
                return value.ToString("N1", CultureInfo.CurrentCulture) + " " + units[unit];
            }
            InstanceSpaceText.Text = $"文件大小 {Size(footprint.Logical)} · 磁盘分配 {(footprint.AllocationKnown ? Size(footprint.Allocated) : "无法读取")} · {footprint.Files} 个文件"
                + (footprint.Shared ? "\n含此非隔离实例使用的共享游戏数据，不计其他版本与公共下载缓存。" : "")
                + (footprint.Unreadable > 0 ? $"\n{footprint.Unreadable} 项无法读取，统计未覆盖这些项目。" : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { InstanceSpaceText.Text = error.Message; }
        finally { if (ReferenceEquals(_instanceSpaceCancellation, cancellation)) _instanceSpaceCancellation = null; }
    }
}
