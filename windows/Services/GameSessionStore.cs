using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using MCHarbor.Core.Models;

namespace MCHarbor.Windows.Services;

internal sealed record RunningGameSession(string Id, string RunDirectory, int Pid, long Started, string StopFile);

internal static class GameSessionStore
{
    internal static string CanonicalDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            var current = Path.GetPathRoot(full)!;
            foreach (var part in full[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target) current = target.FullName;
            }
            return Path.TrimEndingDirectorySeparator(current);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Path.TrimEndingDirectorySeparator(full); }
    }

    // A PID is only usable together with its start time and a known instance directory.
    internal static IReadOnlyList<RunningGameSession> Read(string dataDirectory, IEnumerable<GameInstance> instances)
    {
        var root = Path.Combine(dataDirectory, "launch-control");
        if (!Directory.Exists(root)) return [];
        var known = instances.GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => CanonicalDirectory(item.RunDirectory)).ToArray(), StringComparer.Ordinal);
        var sessions = new List<RunningGameSession>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.running"))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length is 0 or > 16384 || info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                        || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "D", out _)) continue;
                    using var document = JsonDocument.Parse(File.ReadAllBytes(file));
                    var item = document.RootElement;
                    var id = item.GetProperty("id").GetString();
                    var directory = item.GetProperty("runDirectory").GetString();
                    if (id is null || directory is null || !known.TryGetValue(id, out var paths)
                        || !paths.Contains(CanonicalDirectory(directory), StringComparer.OrdinalIgnoreCase)) continue;
                    var session = new RunningGameSession(id, CanonicalDirectory(directory), item.GetProperty("pid").GetInt32(),
                        item.GetProperty("started").GetInt64(), Path.ChangeExtension(file, ".stop"));
                    using var process = OpenVerifiedProcess(session);
                    if (process is not null) sessions.Add(session);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                    or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return sessions;
    }

    internal static Process? OpenVerifiedProcess(RunningGameSession session)
    {
        if (session.Pid <= 1 || session.Started <= 0) return null;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(session.Pid);
            if (!process.HasExited && new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() == session.Started)
                return process;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException) { }
        process?.Dispose();
        return null;
    }
}
