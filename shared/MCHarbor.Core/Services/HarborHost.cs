using System.Text.Json;

namespace MCHarbor.Core.Services;

public sealed record HarborPaths(string Application, string Config, string Data, string Cache, string State, string Runtime, bool Portable)
{
    public static HarborPaths Resolve(string? application = null, Func<string, string?>? environment = null, string? home = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        application = Path.GetFullPath(application ?? AppContext.BaseDirectory);
        var appImage = environment("APPIMAGE");
        var portableBase = !string.IsNullOrWhiteSpace(appImage) && Path.IsPathFullyQualified(appImage)
            ? Path.GetDirectoryName(appImage)! : application;
        var explicitRoot = environment("SHENGGAN_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            if (!Path.IsPathFullyQualified(explicitRoot)) throw new ArgumentException("SHENGGAN_DATA_DIR 必须是绝对路径。");
            return InRoot(application, Path.GetFullPath(explicitRoot), false);
        }
        if (File.Exists(Path.Combine(portableBase, "MCHarbor.portable")))
            return InRoot(application, Path.Combine(portableBase, "MCHarborData"), true);
        string Xdg(string key, string fallback)
        {
            var value = environment(key);
            return Path.Combine(!string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) ? value : Path.Combine(home, fallback), "mcharbor");
        }
        return new(application, Xdg("XDG_CONFIG_HOME", ".config"), Xdg("XDG_DATA_HOME", ".local/share"),
            Xdg("XDG_CACHE_HOME", ".cache"), Xdg("XDG_STATE_HOME", ".local/state"),
            Xdg("XDG_RUNTIME_DIR", ".cache/run"), false);
    }

    private static HarborPaths InRoot(string app, string root, bool portable) =>
        new(app, root, root, Path.Combine(root, "cache"), Path.Combine(root, "state"), Path.Combine(root, "run"), portable);

    public void EnsureCreated()
    {
        foreach (var directory in new[] { Config, Data, Cache, State, Runtime })
        {
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("应用数据目录不能是符号链接：" + directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}

public static class HarborHost
{
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _preferences;
    private static Func<string, string, string>? _read;
    private static Action<string, string>? _write;
    public static HarborPaths Paths { get; private set; } = HarborPaths.Resolve();
    public static string PlatformName { get; private set; } = OperatingSystem.IsWindows() ? "Windows" : "Linux";
    public static string Version { get; private set; } = "1.7.1";
    public static string ApplicationDirectory => Paths.Application;
    public static string DataDirectory => Paths.Data;
    public static string HmclJar => Path.Combine(ApplicationDirectory, "Backend", "HMCLCore.jar");
    public static string GameRoot => GetPreference("gameRoot", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft"));

    public static void Configure(HarborPaths paths, string platform = "Linux", string version = "1.7.1",
        Func<string, string, string>? readPreference = null, Action<string, string>? writePreference = null)
    {
        lock (Gate)
        {
            Paths = paths; PlatformName = platform; Version = version;
            _preferences = null; _read = readPreference; _write = writePreference;
        }
    }

    public static string GetPreference(string key, string fallback = "")
    {
        lock (Gate)
        {
            if (_read is not null) return _read(key, fallback);
            if (_preferences is null)
            {
                var path = Path.Combine(Paths.Config, "preferences.json");
                _preferences = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(path)) ?? [] : [];
            }
            return _preferences.GetValueOrDefault(key, fallback);
        }
    }

    public static void SetPreference(string key, string value)
        => SetPreferences(new Dictionary<string, string> { [key] = value });

    public static void SetPreferences(IReadOnlyDictionary<string, string> values)
    {
        lock (Gate)
        {
            if (_write is not null) { foreach (var (key, value) in values) _write(key, value); return; }
            _ = GetPreference("");
            var updated = new Dictionary<string, string>(_preferences!);
            foreach (var (key, value) in values) updated[key] = value;
            AtomicFile.Write(Path.Combine(Paths.Config, "preferences.json"), JsonSerializer.SerializeToUtf8Bytes(updated));
            _preferences = updated;
        }
    }
}

public static class AtomicFile
{
    public static void Write(string destination, byte[] bytes)
    {
        destination = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("目标文件是符号链接，未覆盖。");
        var temporary = Path.Combine(parent, ".harbor-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.Write(bytes); stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
