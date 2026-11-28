using System.Diagnostics;
using MCHarbor.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MCHarbor.Windows;

public sealed partial class MainWindow
{
    public sealed record ScreenshotRow(InstanceMediaFile File)
    {
        public string Name => File.Name;
    }
    private int _mediaRequest;

    private async void PrepareInstanceMedia()
    {
        var sequence = ++_mediaRequest;
        var category = _resourceCategory;
        if (_activeModsInstance is not { } instance || category is not ("screenshots" or "logs")) return;
        InstanceScreenshots.Visibility = VisibleWhen(category == "screenshots");
        InstanceLogPane.Visibility = VisibleWhen(category == "logs");
        InstanceLogText.Text = "";
        try
        {
            var files = await Task.Run(() => InstanceMediaStore.Read(instance.RunDirectory, category));
            if (sequence != _mediaRequest || _activeModsInstance != instance || _resourceCategory != category) return;
            InstanceMediaEmpty.Visibility = VisibleWhen(category == "screenshots" && files.Count == 0);
            if (category == "logs" && files.Count == 0) InstanceLogText.Text = "暂无日志";
            if (category == "screenshots")
            {
                InstanceScreenshots.ItemsSource = files.Select(file => new ScreenshotRow(file)).ToArray();
                DispatcherQueue.TryEnqueue(ResizeScreenshotGrid);
            }
            else
            {
                InstanceLogFiles.ItemsSource = files;
                InstanceLogFiles.SelectedItem = files.FirstOrDefault();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (sequence == _mediaRequest) ShowStatus(error.Message, InfoBarSeverity.Warning);
        }
    }

    private async void InstanceLog_SelectionChanged(object sender, SelectionChangedEventArgs e) => await LoadInstanceLogAsync();

    private async Task LoadInstanceLogAsync()
    {
        if (_activeModsInstance is not { } instance || InstanceLogFiles.SelectedItem is not InstanceMediaFile file) return;
        try
        {
            var text = await Task.Run(() => InstanceMediaStore.ReadLog(instance.RunDirectory, file.Path));
            if (_activeModsInstance == instance && Equals(InstanceLogFiles.SelectedItem, file)) InstanceLogText.Text = text;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ShowStatus(error.Message, InfoBarSeverity.Warning); }
    }

    private async void InstanceScreenshot_Click(object sender, ItemClickEventArgs e)
    {
        if (_activeModsInstance is not { } instance || e.ClickedItem is not ScreenshotRow row
            || !InstanceMediaStore.IsSafeFile(instance.RunDirectory, "screenshots", row.File.Path)) return;
        try { await global::Windows.System.Launcher.LaunchFileAsync(await global::Windows.Storage.StorageFile.GetFileFromPathAsync(row.File.Path)); }
        catch (Exception error) { ShowStatus(error.Message, InfoBarSeverity.Warning); }
    }

    private void OpenInstanceMediaDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (_activeModsInstance is not { } instance || _resourceCategory is not ("screenshots" or "logs")) return;
        var directory = InstanceMediaStore.DirectoryFor(instance.RunDirectory, _resourceCategory);
        if (Directory.Exists(directory)) Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private void ScreenshotGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => ResizeScreenshotGrid();

    private void ResizeScreenshotGrid()
    {
        if (InstanceScreenshots.ItemsPanelRoot is not ItemsWrapGrid panel || InstanceScreenshots.ActualWidth < 1) return;
        var (columns, width) = MacLayout.Cards(Math.Max(1, InstanceScreenshots.ActualWidth - 15), 210, double.MaxValue, 15);
        panel.ItemWidth = width + 15; panel.MaximumRowsOrColumns = columns;
    }

    private void ScreenshotThumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image { DataContext: ScreenshotRow row } image || _activeModsInstance is not { } instance
            || !InstanceMediaStore.IsSafeFile(instance.RunDirectory, "screenshots", row.File.Path)) return;
        var pixels = (int)Math.Clamp(Math.Ceiling(Math.Max(210, image.ActualWidth) * (RootShell.XamlRoot?.RasterizationScale ?? 1)), 210, 1000);
        image.Source = new BitmapImage { DecodePixelWidth = pixels, UriSource = new Uri(row.File.Path) };
    }

    private void ScreenshotThumbnail_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image) image.Source = null;
    }

    private void CopyInstanceLog_Click(object sender, RoutedEventArgs e)
    {
        var data = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(InstanceLogText.Text);
        global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
    }

    private async void RefreshInstanceLog_Click(object sender, RoutedEventArgs e) => await LoadInstanceLogAsync();
}
