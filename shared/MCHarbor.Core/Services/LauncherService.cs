using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using MCHarbor.Core.Models;

namespace MCHarbor.Core.Services;

public sealed class LauncherService(AccountRepository accounts, IBridgeClient? backend = null)
{
    private readonly ConcurrentDictionary<string, string> _running = new(PathComparer);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _directoryGates = new(PathComparer);
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public event Action<JsonObject>? BackendEvent;
    public event Action? RunningChanged;
    public bool AnyRunning => !_running.IsEmpty;
    public bool IsRunning(GameInstance instance) => _running.ContainsKey(InstanceFiles.CanonicalDirectory(instance.RunDirectory));
    public IBridgeClient Backend => backend ?? new NativeBridgeClient(NativeBridgeClient.FindLauncherJava()
        ?? throw new IOException("未找到启动器 Java 21。请使用完整发行包，或设置 JAVA_HOME。"));

    public IReadOnlyList<GameInstance> Scan() => InstanceScanner.Scan(HarborHost.GameRoot);
    public static Dictionary<string, object?> Arguments(GameInstance instance) => new()
    {
        ["id"] = instance.Id, ["name"] = instance.Id, ["version"] = instance.Version,
        ["loader"] = instance.Loader, ["runDirectory"] = instance.RunDirectory,
        ["root"] = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(instance.Directory)))
    };
    public Task<JsonObject> InvokeAsync(string command, IReadOnlyDictionary<string, object?>? arguments = null, CancellationToken cancellation = default) =>
        Backend.InvokeAsync(command, arguments, HandleEvent, cancellation);

    private void HandleEvent(JsonObject message)
    {
        BackendEvent?.Invoke(message);
    }

    public async Task<JsonObject> LoginAsync(string kind, Dictionary<string, object?> args, CancellationToken cancellation)
    {
        var command = kind switch { "offline" => "offlineAccount", "microsoft" => "login", "external" => "externalLogin", _ => throw new ArgumentException("账号类型无效。") };
        var result = await InvokeAsync(command, args, cancellation);
        if (result["metadata"] is JsonObject) await accounts.SaveAsync(result, cancellation);
        return result;
    }

    public async Task<JsonObject> AccountOperationAsync(StoredAccount account, string command, Dictionary<string, object?>? args = null, CancellationToken cancellation = default)
    {
        var payload = await accounts.LoadPayloadAsync(account, cancellation) ?? throw new IOException("登录凭据不可用，请重新登录：" + account.Name);
        args ??= [];
        args["account"] = payload; args["accountType"] = account.Kind;
        var updates = new List<JsonObject>();
        var result = await Backend.InvokeAsync(command, args, item =>
        {
            if (item["event"]?.GetValue<string>() == "accountUpdated" && item["data"] is JsonObject updated) updates.Add(updated.DeepClone().AsObject());
            HandleEvent(item);
        }, cancellation);
        foreach (var update in updates) await accounts.SaveAsync(update, cancellation);
        return result;
    }

    public async Task<JsonObject> LaunchAsync(GameInstance instance, string? server, CancellationToken cancellation)
    {
        var run = InstanceFiles.CanonicalDirectory(instance.RunDirectory);
        var gate = _directoryGates.GetOrAdd(run, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation);
        var stop = Path.Combine(HarborHost.DataDirectory, "launch-control", Guid.NewGuid() + ".stop");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stop)!);
            if (!_running.TryAdd(run, stop)) throw new IOException("使用此运行目录的游戏已经启动。");
        }
        finally { gate.Release(); }
        NotifyRunningChanged();
        var session = Guid.NewGuid();
        try
        {
            var args = Arguments(instance);
            args["memory"] = InstancePreferences.Number(instance, "memory", PreferenceNumber("memory", 3072));
            args["jvmArguments"] = InstancePreferences.Get(instance, "jvmArguments");
            args["fullscreen"] = InstancePreferences.Get(instance, "fullscreen") == "true";
            args["width"] = InstancePreferences.Number(instance, "width", 1280);
            args["height"] = InstancePreferences.Number(instance, "height", 800);
            args["completeDependencies"] = HarborHost.GetPreference("completeDependencies") == "true";
            args["stopFile"] = stop; args["historySessionID"] = session.ToString().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(server)) args["serverAddress"] = MinecraftAddress.Parse(server);
            var java = InstancePreferences.Get(instance, "java", HarborHost.GetPreference("gameJava"));
            if (!string.IsNullOrWhiteSpace(java)) { args["gameJava"] = java; args["javaExplicit"] = true; }
            var selected = accounts.ReadAll().FirstOrDefault(account => account.Id == HarborHost.GetPreference("selectedAccount"))
                ?? throw new IOException("请先添加并选择游戏账号。");
            args["account"] = await accounts.LoadPayloadAsync(selected, cancellation) ?? throw new IOException("账号需要重新登录。");
            args["accountType"] = selected.Kind;
            var updates = new List<JsonObject>();
            var result = await Backend.InvokeAsync("launch", args, item =>
            {
                if (item["event"]?.GetValue<string>() == "accountUpdated" && item["data"] is JsonObject updated) updates.Add(updated.DeepClone().AsObject());
                HandleEvent(item);
            }, cancellation);
            foreach (var update in updates) await accounts.SaveAsync(update, CancellationToken.None);
            PlayHistoryStore.Complete(instance, session, []);
            return result;
        }
        finally
        {
            _running.TryRemove(run, out _);
            try { File.Delete(stop); } catch (IOException) { }
            NotifyRunningChanged();
        }
    }

    public void RequestTermination(GameInstance instance)
    {
        if (!_running.TryGetValue(InstanceFiles.CanonicalDirectory(instance.RunDirectory), out var stop)) throw new IOException("游戏没有运行。");
        AtomicFile.Write(stop, []);
    }

    private void NotifyRunningChanged()
    {
        if (RunningChanged is null) return;
        foreach (Action listener in RunningChanged.GetInvocationList())
            try { listener(); } catch (Exception error) { System.Diagnostics.Trace.WriteLine(error.GetType().Name); }
    }

    public async Task<T> WriteInstanceAsync<T>(GameInstance instance, Func<Task<T>> operation, CancellationToken cancellation = default)
    {
        var run = InstanceFiles.CanonicalDirectory(instance.RunDirectory);
        var gate = _directoryGates.GetOrAdd(run, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation);
        try
        {
            if (IsRunning(instance)) throw new IOException("游戏正在运行，请保存并退出游戏后重试。");
            return await operation();
        }
        finally { gate.Release(); }
    }

    public static int PreferenceNumber(string key, int fallback) => int.TryParse(HarborHost.GetPreference(key), out var value) ? value : fallback;

    public async Task<int> ImportAccountsAsync(string source, CancellationToken cancellation)
    {
        var result = await InvokeAsync("migrate", new Dictionary<string, object?> { ["migrationRoots"] = new[] { source } }, cancellation);
        var rows = result["accounts"] as JsonArray ?? [];
        foreach (var row in rows.OfType<JsonObject>()) await accounts.SaveAsync(row, cancellation);
        return rows.Count;
    }
}
