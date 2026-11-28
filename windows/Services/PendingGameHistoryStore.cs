using System.Text.Json;
using MCHarbor.Core.Models;

namespace MCHarbor.Windows.Services;

internal sealed record PendingGameHistory(Guid Session, string Instance, string RunDirectory, string Version, string Loader, string[] Peers, string StopFile);

internal static class PendingGameHistoryStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string FilePath(string data, Guid session) => Path.Combine(data, "launch-control", session.ToString("D") + ".history");

    internal static void Save(string data, Guid session, GameInstance instance, string[] peers, string stopFile)
    {
        var path = FilePath(data, session); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var record = new PendingGameHistory(session, instance.Id, GameSessionStore.CanonicalDirectory(instance.RunDirectory), instance.Version, instance.Loader, peers.Take(64).ToArray(), stopFile);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(record, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal static IReadOnlyList<PendingGameHistory> Read(string data, IEnumerable<GameInstance> instances)
    {
        var root = Path.Combine(data, "launch-control"); if (!Directory.Exists(root)) return [];
        var known = instances.ToArray(); var result = new List<PendingGameHistory>();
        foreach (var file in Directory.EnumerateFiles(root, "*.history").Take(5000))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length is 0 or > 16384 || info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "D", out var session)) continue;
                var pending = JsonSerializer.Deserialize<PendingGameHistory>(File.ReadAllBytes(file), Json);
                if (pending is null || pending.Session != session || pending.RunDirectory is null
                    || pending.Version is null || pending.Loader is null || pending.Peers is null || pending.Peers.Length > 64
                    || pending.Version.Length > 256 || pending.Loader.Length > 256 || pending.Peers.Any(peer => peer is null || peer.Length > 128 || peer.Any(char.IsControl))) continue;
                if (!known.Any(instance => instance.Id == pending.Instance && GameSessionStore.CanonicalDirectory(instance.RunDirectory)
                    .Equals(GameSessionStore.CanonicalDirectory(pending.RunDirectory), StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(pending);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        }
        return result;
    }

    internal static void Remove(string data, Guid session) => File.Delete(FilePath(data, session));
}
