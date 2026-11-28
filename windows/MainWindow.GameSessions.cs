using System.Diagnostics;
using System.Text.Json.Nodes;
using MCHarbor.Core.Models;
using MCHarbor.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MCHarbor.Windows;

public sealed partial class MainWindow
{
    private sealed class ActiveGameSession(GameInstance instance, string stopFile)
    {
        internal GameInstance Instance { get; } = instance;
        internal string StopFile { get; } = stopFile;
        internal Process? Process { get; set; }
        internal RunningGameSession? Receipt { get; set; }
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Dictionary<string, ActiveGameSession> _gameSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _backendLog = new();
    private bool HasRunningGames => _gameSessions.Count > 0;
    private bool IsRunningInstance(GameInstance? instance) => instance is not null && _gameSessions.ContainsKey(GameDirectoryKey(instance.RunDirectory));

    private void RestoreRunningGames()
    {
        ReconcileCompletedGameHistory();
        foreach (var receipt in GameSessionStore.Read(AppPaths.DataDirectory, _instances))
        {
            var key = GameDirectoryKey(receipt.RunDirectory);
            if (_gameSessions.ContainsKey(key)) continue;
            var instance = _instances.FirstOrDefault(item => item.Id == receipt.Id
                && GameDirectoryKey(item.RunDirectory).Equals(key, StringComparison.OrdinalIgnoreCase));
            if (instance is null || GameSessionStore.OpenVerifiedProcess(receipt) is not { } process) continue;
            var session = new ActiveGameSession(instance, receipt.StopFile) { Process = process, Receipt = receipt };
            _gameSessions.Add(key, session);
            _ = WatchRecoveredGameAsync(session);
        }
        UpdateGameSessionUi();
    }

    private async Task WatchRecoveredGameAsync(ActiveGameSession session)
    {
        try
        {
            await PauseLanguageForGameAsync();
            await session.Process!.WaitForExitAsync();
            var receiptFile = Path.ChangeExtension(session.StopFile, ".running");
            for (var attempt = 0; attempt < 30 && File.Exists(receiptFile); attempt++) await Task.Delay(100);
            ReconcileCompletedGameHistory();
            if (!_closing) ShowStatus($"{session.Instance.Id} 已退出。", InfoBarSeverity.Informational);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (!_closing) StartupDiagnostics.Write("恢复游戏状态失败", error);
        }
        finally { CompleteGameSession(session); }
    }

    private void CompleteGameSession(ActiveGameSession session)
    {
        _gameSessions.Remove(GameDirectoryKey(session.Instance.RunDirectory));
        session.Process?.Dispose();
        session.Finished.TrySetResult();
        if (_closing) return;
        UpdateGameSessionUi();
        if (!HasRunningGames) ResumeLanguageAfterGame();
    }

    private void UpdateGameSessionUi()
    {
        ApplyTheme();
        UpdateWallpaperPlayback();
        UpdateFeaturedLaunch();
    }

    private void ReconcileCompletedGameHistory()
    {
        try
        {
            foreach (var pending in PendingGameHistoryStore.Read(AppPaths.DataDirectory, _instances))
            {
                var receipt = Path.Combine(AppPaths.DataDirectory, "social", "receipts", pending.Session.ToString("D").ToUpperInvariant() + ".json");
                if (!File.Exists(receipt)) continue;
                var info = new FileInfo(receipt);
                if (info.Length > 16384 || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(receipt));
                if (!document.RootElement.TryGetProperty("finished", out var finished) || finished.ValueKind != System.Text.Json.JsonValueKind.True) continue;
                var instance = _instances.First(item => item.Id == pending.Instance && GameDirectoryKey(item.RunDirectory).Equals(GameDirectoryKey(pending.RunDirectory), StringComparison.OrdinalIgnoreCase));
                PlayHistoryStore.Complete(instance with { Version = pending.Version, Loader = pending.Loader }, pending.Session, pending.Peers);
                if (!File.Exists(receipt)) PendingGameHistoryStore.Remove(AppPaths.DataDirectory, pending.Session);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { StartupDiagnostics.Write("恢复游玩记录失败", error); }
    }

    private void RecordBackendMessage(string message)
    {
        _backendLog.Enqueue(message.Length > 2000 ? message[..2000] : message);
        while (_backendLog.Count > 400) _backendLog.Dequeue();
        MaintenanceLog.Text = string.Join(Environment.NewLine, _backendLog.TakeLast(15));
        MaintenanceLog.Visibility = MaintenanceLog.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PostLaunch_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_appearanceReady || PostLaunchMode.SelectedItem is not ComboBoxItem { Tag: string mode }) return;
        AppPaths.SetPreference("postLaunch", mode);
    }

    private void ApplyPostLaunchBehavior()
    {
        switch (AppPaths.GetPreference("postLaunch", "keep"))
        {
            case "hide": AppWindow.Hide(); UpdateWallpaperPlayback(); break;
            case "quit": _ = CloseApplicationAsync(); break;
        }
    }

    private void InstanceMenu_Opening(object sender, object args)
    {
        if (sender is not MenuFlyout menu) return;
        foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
        {
            var instance = MenuInstance(item);
            if (item.Text == "关闭游戏") item.Visibility = VisibleWhen(IsRunningInstance(instance));
            else if (item.Text is "收藏" or "取消收藏" or "收藏/取消收藏")
                item.Text = instance is not null && ReadFavoriteInstances().Contains(instance.Directory) ? "取消收藏" : "收藏";
        }
    }

    private void AttachLaunchedProcess(ActiveGameSession session, JsonObject message)
    {
        var receipt = GameSessionStore.Read(AppPaths.DataDirectory, [session.Instance])
            .FirstOrDefault(item => item.StopFile.Equals(session.StopFile, StringComparison.OrdinalIgnoreCase));
        if (receipt is null || GameSessionStore.OpenVerifiedProcess(receipt) is not { } process)
        {
            ShowStatus("游戏已启动，未能恢复其控制凭据。请在游戏内正常退出。", InfoBarSeverity.Warning);
            return;
        }
        session.Process = process;
        session.Receipt = receipt;
        UpdateGameSessionUi();
        ShowStatus("Minecraft 正在运行。", InfoBarSeverity.Success);
        ApplyPostLaunchBehavior();
    }
}
