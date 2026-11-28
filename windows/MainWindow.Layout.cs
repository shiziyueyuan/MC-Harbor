using MCHarbor.Core.Models;
using MCHarbor.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MCHarbor.Windows;

public sealed partial class MainWindow
{
    private string _settingsSection = "appearance";
    private string _resourceCategory = "mods";
    private InstanceResource[] _instanceResources = [];

    private static Visibility VisibleWhen(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void CardGrid_Loaded(object sender, RoutedEventArgs e) => ResizeCardGrid((GridView)sender);

    private void CardGrid_SizeChanged(object sender, SizeChangedEventArgs e) => ResizeCardGrid((GridView)sender);

    private void ResizeCardGrid(GridView view)
    {
        if (view.ActualWidth <= 1 || view.ItemsPanelRoot is not ItemsWrapGrid panel) return;
        var library = view == InstanceList;
        var gap = library ? 14 : 20;
        var minimum = library ? 300 : 250;
        var width = Math.Max(1, view.ActualWidth - gap);
        var (columns, itemWidth) = MacLayout.Cards(width, minimum, library ? 480 : double.MaxValue, gap);
        panel.MaximumRowsOrColumns = columns;
        panel.ItemWidth = itemWidth + gap;
        if (library) LibraryGridHeader.Width = width;
    }

    private void UpdatePageSelections()
    {
        UpdateTabSelection(DownloadCategories, _downloadCategory);
        UpdateTabSelection(VersionFilters, _versionFilter);
        UpdateTabSelection(SettingsSections, _settingsSection);
        UpdateTabSelection(WorkspaceTabs, _lastNavigationTag is "localization" or "instance-settings"
            ? _lastNavigationTag : _resourceCategory);
    }

    private void UpdateTabSelection(StackPanel group, string? current)
    {
        var selectedFill = _chromeHighContrast
            ? _uiSettings.UIElementColor(global::Windows.UI.ViewManagement.UIElementType.Highlight)
            : _darkAppearance ? global::Windows.UI.Color.FromArgb(34, 255, 255, 255)
            : global::Windows.UI.Color.FromArgb(24, 22, 22, 22);
        foreach (var button in group.Children.OfType<Button>())
        {
            var selected = Equals(button.Tag, current);
            button.Background = new SolidColorBrush(selected ? selectedFill : Microsoft.UI.Colors.Transparent);
            button.BorderBrush = new SolidColorBrush(selected
                ? (_chromeHighContrast ? selectedFill : _glassBorder.Color) : Microsoft.UI.Colors.Transparent);
            if (_chromeHighContrast && selected)
                button.Foreground = new SolidColorBrush(_uiSettings.UIElementColor(global::Windows.UI.ViewManagement.UIElementType.HighlightText));
            else button.ClearValue(Control.ForegroundProperty);
            AutomationProperties.SetItemStatus(button, selected ? "当前分类" : "");
        }
    }

    private void SettingsSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section })
        {
            SettingsSearch.Text = "";
            SelectSettingsSection(section);
        }
    }

    private void SelectSettingsSection(string section, FrameworkElement? target = null)
    {
        _settingsSection = section;
        AppearanceSettings.Visibility = VisibleWhen(section == "appearance");
        WelcomeSettings.Visibility = VisibleWhen(section == "welcome");
        RuntimeSettings.Visibility = VisibleWhen(section == "runtime");
        MaintenanceSettings.Visibility = VisibleWhen(section == "maintenance");
        UpdateTabSelection(SettingsSections, section);
        if (target is null) SettingsScroll.ChangeView(null, 0, null, disableAnimation: true);
        else DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 }));
        QueueGlassCardsRefresh();
    }

    private (string Label, string Terms, string Section, FrameworkElement Target)[] SettingsIndex() =>
    [
        ("外观 · 主题色", "主题色 调色盘 配色", "appearance", SettingsPalette),
        ("外观 · 主题范围与反色", "反色 主要文字 辅助文字 按钮背景 图标 边框 对比度", "appearance", SettingsThemeScopes),
        ("外观 · 模组夹信息显示", "模组 图标 加载器 版本 收藏时间 简介 作者 信息量", "appearance", SettingsCollectionDisplay),
        ("外观 · 外观模式", "主题 深色 浅色 通透", "appearance", SettingsGlass),
        ("外观 · 背景", "壁纸库 图片 视频 MP4 预览 重命名 毛玻璃 玻璃通透 窗口透明 壁纸柔化 后景材质", "appearance", SettingsWallpaper),
        ("外观 · 动态与声音", "独家优化 减少动态效果 音效 声音 音量 性能", "appearance", SettingsSounds),
        ("欢迎动画 · 启动播报", "欢迎 语音 播报 静音 音量 试听", "welcome", SettingsVoice),
        ("运行 · 翻译与账号授权", "翻译 API 密钥 Qwen 阿里 B站 MyMemory Google 凭据", "runtime", SettingsTranslation),
        ("运行 · 游戏", "目录 路径 Minecraft Java 内存", "runtime", SettingsPaths),
        ("运行 · 启动器", "游戏启动后 保持可见 隐藏 退出 后台会话", "runtime", SettingsLauncher),
        ("运行 · 前置补全", "模组 依赖 前置补全", "runtime", SettingsDependencies),
        ("运行 · 下载", "下载 镜像 并发数", "runtime", SettingsGameOptions),
        ("维护 · 数据目录与诊断", "诊断 数据 存储", "maintenance", SettingsData),
        ("维护 · 开发者与平台分工", "开发者 维护者 作者 分工 GitHub", "maintenance", DeveloperRows),
        ("维护 · 贡献者", "贡献者 反馈 致谢", "maintenance", ContributorRows),
        ("维护 · 关于", "关于 版本 GPL 许可", "maintenance", SettingsAbout)
    ];

    private void SettingsSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            sender.ItemsSource = FindSettings(sender.Text).Select(item => item.Label).ToArray();
    }

    private void SettingsSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e)
    {
        var matches = e.ChosenSuggestion is string chosen ? SettingsIndex().Where(item => item.Label == chosen)
            : FindSettings(e.QueryText);
        var match = matches.FirstOrDefault();
        if (match.Target is null)
        {
            if (!string.IsNullOrWhiteSpace(e.QueryText)) ShowStatus("没有匹配的设置。", InfoBarSeverity.Informational);
            return;
        }
        CloseStatus();
        sender.Text = "";
        SelectSettingsSection(match.Section, match.Target);
    }

    private IEnumerable<(string Label, string Terms, string Section, FrameworkElement Target)> FindSettings(string query)
    {
        query = query.Trim();
        return query.Length == 0 ? [] : SettingsIndex().Where(item =>
            item.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Terms.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateWorkspaceHeader()
    {
        var show = IsInstancePage(_lastNavigationTag) && _activeModsInstance is not null;
        var wasVisible = WorkspaceOverlay.Visibility == Visibility.Visible;
        WorkspaceOverlay.Visibility = VisibleWhen(show);
        MainNavigationSurface.IsHitTestVisible = !show;
        MainNavigationSurface.Opacity = show ? 0.92 : 1;
        UpdateWorkspaceBackdrop(show);
        if (show && !wasVisible) DispatcherQueue.TryEnqueue(() => WorkspaceCloseButton.Focus(FocusState.Programmatic));
        WorkspaceIcon.Visibility = WorkspaceTabs.Visibility = WorkspaceTopActions.Visibility = VisibleWhen(show);
        if (!show || _activeModsInstance is not { } instance) return;
        WorkspaceTitle.Text = instance.FriendlyName;
        WorkspaceSubtitle.Text = $"{instance.Version} · {instance.Loader}";
        WorkspaceIcon.Source = new BitmapImage(new Uri(instance.Artwork));
        InstanceSettingsPath.Text = instance.RunDirectory;
        LoadInstanceOptions(instance);
        WorkspaceLaunchButton.Tag = WorkspaceOpenDirectory.Tag = WorkspaceRepair.Tag = WorkspaceExport.Tag =
            WorkspaceRelocate.Tag = WorkspaceJava.Tag = WorkspaceChangeIcon.Tag = WorkspaceShortcut.Tag = instance.Id;
        UpdateFeaturedLaunch();
    }

    private void WorkspaceTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tab } || _activeModsInstance is null) return;
        if (tab is "localization" or "instance-settings") NavigateTo(tab);
        else
        {
            _resourceCategory = tab;
            ResourceQuery.Text = "";
            if (_lastNavigationTag == "collections") UpdateResourceRows();
            else NavigateTo("collections");
        }
    }

    private void CloseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        _activeModsInstance = null;
        _instanceSpaceCancellation?.Cancel();
        NavigateTo("games");
    }

    private void WorkspaceBackdrop_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) =>
        CloseWorkspace_Click(sender, new RoutedEventArgs());

    private void ResourceQuery_Changed(object sender, TextChangedEventArgs e)
    {
        if (ResourceCount is not null) UpdateResourceRows();
    }

    private void UpdateResourceRows()
    {
        var media = _resourceCategory is "screenshots" or "logs";
        InstanceMediaPage.Visibility = VisibleWhen(media && IsInstancePage(_lastNavigationTag));
        if (_lastNavigationTag == "collections")
            InstanceResourcesCard.Visibility = ResourcesList.Visibility = VisibleWhen(!media);
        if (media)
        {
            PrepareInstanceMedia();
            UpdateTabSelection(WorkspaceTabs, _resourceCategory);
            return;
        }
        var query = ResourceQuery.Text.Trim();
        var items = _instanceResources.Where(item => item.Kind == _resourceCategory
            && (item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(item.Path).Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        ResourcesList.ItemsSource = items;
        ResourceCount.Text = items.Length == 0 ? "没有匹配的项目" : $"{items.Length} 个项目";
        ResourceImportButton.Content = _resourceCategory switch
        {
            "resourcepacks" => "导入资源包",
            "shaderpacks" => "导入光影",
            "saves" => "导入地图",
            _ => "导入模组"
        };
        CollectWorkspaceMods.Visibility = VisibleWhen(_resourceCategory == "mods");
        BackupWorkspaceWorlds.Visibility = VisibleWhen(_resourceCategory == "saves");
        UpdateTabSelection(WorkspaceTabs, _resourceCategory);
    }

    private void ApplyMacPageLayout(string tag)
    {
        var settings = tag == "settings";
        var library = tag == "games" || IsInstancePage(tag);
        var inset = settings ? MacLayout.SettingsInset : MacLayout.PageInset;
        PageContent.Padding = new Thickness(inset, library ? 14 : settings ? 24 : 28, inset, 28);
        PageHeader.Margin = new Thickness(0, 0, 0, settings ? 20 : tag is "friends" or "downloads" ? 22 : 24);
        if (!IsInstancePage(tag)) InstanceMediaPage.Visibility = Visibility.Collapsed;
    }

    private async void RefreshWorkspaceResources_Click(object sender, RoutedEventArgs e) => await LoadResourcesAsync();

    private void BrowseWorkspaceResources_Click(object sender, RoutedEventArgs e)
    {
        var category = _resourceCategory switch
        {
            "resourcepacks" => "resourcepack",
            "shaderpacks" => "shader",
            "saves" => "world",
            _ => "mod"
        };
        OpenDownloads(category, _activeModsInstance);
    }
}
