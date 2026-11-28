using System.Security.Cryptography;
using System.Text.Json;
using MCHarbor.Core.Models;

namespace MCHarbor.Core.Services;

public sealed record StagedResource(string Path, string Name, string Sha256);
public sealed record PendingInstall(Guid Id, GameInstance Instance, string Kind, StagedResource[] Files, string Title);

public sealed class ResourceInstallQueue(LauncherService launcher)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string Index => Path.Combine(HarborHost.DataDirectory, "pending-installs.json");
    public PendingInstall[] Read() => File.Exists(Index) ? JsonSerializer.Deserialize<PendingInstall[]>(File.ReadAllBytes(Index)) ?? [] : [];
    private void Save(PendingInstall[] rows) => AtomicFile.Write(Index, JsonSerializer.SerializeToUtf8Bytes(rows));

    public async Task<PendingInstall> EnqueueAsync(GameInstance instance, string kind, IEnumerable<string> files, string title, CancellationToken cancellation)
    {
        if (kind is not ("mods" or "resourcepacks" or "shaderpacks" or "saves")) throw new IOException("待安装资源类型无效。");
        var staged = new List<StagedResource>();
        foreach (var file in files)
        {
            var path = InstanceFiles.Child(Path.Combine(HarborHost.DataDirectory, "staging"), file);
            await using var input = File.OpenRead(path);
            staged.Add(new(path, Path.GetFileName(path), Convert.ToHexString(await SHA256.HashDataAsync(input, cancellation))));
        }
        if (staged.Count == 0 || kind == "saves" && staged.Count != 1) throw new IOException("待安装资源清单为空或无效。");
        var row = new PendingInstall(Guid.NewGuid(), instance, kind, staged.ToArray(), title);
        await _gate.WaitAsync(cancellation);
        try { Save(Read().Append(row).ToArray()); }
        finally { _gate.Release(); }
        return row;
    }

    public async Task<bool> CommitAsync(Guid id, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var pending = Read().FirstOrDefault(row => row.Id == id) ?? throw new IOException("安装任务不存在。");
            if (launcher.IsRunning(pending.Instance)) return false;
            var versionRoot = Path.GetDirectoryName(pending.Instance.Directory)!;
            var library = Path.GetDirectoryName(versionRoot)!;
            var current = InstanceScanner.Scan(library).FirstOrDefault(game => game.Directory == pending.Instance.Directory);
            if (current is null || current.RunDirectory != pending.Instance.RunDirectory) throw new IOException("实例已移动或运行目录已改变，请取消任务后重新选择实例。");
            foreach (var file in pending.Files)
            {
                InstanceFiles.Child(Path.Combine(HarborHost.DataDirectory, "staging"), file.Path);
                await using var input = File.OpenRead(file.Path);
                if (!Convert.ToHexString(await SHA256.HashDataAsync(input, cancellation)).Equals(file.Sha256, StringComparison.Ordinal))
                    throw new IOException("待安装资源的暂存文件校验失败。");
            }
            await launcher.WriteInstanceAsync(pending.Instance, async () =>
            {
                if (pending.Kind == "saves")
                {
                    await launcher.InvokeAsync("installWorld", new Dictionary<string, object?> { ["file"] = pending.Files[0].Path,
                        ["runDirectory"] = pending.Instance.RunDirectory, ["name"] = pending.Title }, cancellation);
                    return true;
                }
                var root = Path.Combine(pending.Instance.RunDirectory, pending.Kind);
                InstanceFiles.Child(pending.Instance.RunDirectory, root); Directory.CreateDirectory(root);
                var temporary = new List<(string Stage, string Target, string Hash)>(); var committed = new List<(string Path, string Hash)>();
                try
                {
                    foreach (var file in pending.Files)
                    {
                        var target = InstanceFiles.Child(root, Path.Combine(root, file.Name), true);
                        if (File.Exists(target))
                        {
                            await using var previous = File.OpenRead(target);
                            if (Convert.ToHexString(await SHA256.HashDataAsync(previous, cancellation)) == file.Sha256) continue;
                            throw new IOException("目标已有不同内容的同名资源，未覆盖：" + file.Name);
                        }
                        var stage = Path.Combine(root, ".harbor-" + Guid.NewGuid().ToString("N") + ".partial");
                        temporary.Add((stage, target, file.Sha256));
                        await using var input = File.OpenRead(file.Path);
                        await using var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                        await input.CopyToAsync(output, cancellation); await output.FlushAsync(cancellation);
                    }
                    foreach (var (stage, target, hash) in temporary) { cancellation.ThrowIfCancellationRequested(); File.Move(stage, target, overwrite: false); committed.Add((target, hash)); }
                }
                catch
                {
                    foreach (var (path, hash) in committed)
                    {
                        using var check = File.OpenRead(path); var unchanged = Convert.ToHexString(SHA256.HashData(check)) == hash; check.Close();
                        if (unchanged) File.Delete(path);
                    }
                    throw;
                }
                finally { foreach (var (stage, _, _) in temporary) File.Delete(stage); }
                return true;
            }, cancellation);
            Save(Read().Where(row => row.Id != id).ToArray());
            foreach (var file in pending.Files) File.Delete(file.Path);
            return true;
        }
        finally { _gate.Release(); }
    }
    public async Task CancelAsync(Guid id, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try { Save(Read().Where(row => row.Id != id).ToArray()); }
        finally { _gate.Release(); }
    }
}
