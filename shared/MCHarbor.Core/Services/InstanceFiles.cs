using System.Security.Cryptography;
using System.Text.Json;
using MCHarbor.Core.Models;

namespace MCHarbor.Core.Services;

public static class InstanceFiles
{
    public static string CanonicalDirectory(string directory)
    {
        var full = Path.GetFullPath(directory); var current = Path.GetPathRoot(full)!;
        foreach (var segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                current = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }
    public static string Child(string root, string path, bool direct = false)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); path = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison) || direct && !string.Equals(Path.GetDirectoryName(path), root, comparison))
            throw new IOException("文件不属于目标目录。");
        for (var current = path; current.Length > root.Length; current = Path.GetDirectoryName(current)!)
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("操作目标包含符号链接。");
        return path;
    }

    public static string ValidInstanceDirectory(GameInstance instance)
    {
        var versions = Path.Combine(HarborHost.GameRoot, "versions");
        var path = Child(versions, instance.Directory, direct: true);
        if (Path.GetFileName(path) != instance.Id || !Directory.Exists(path)) throw new IOException("实例目录无效。");
        foreach (var folder in Directory.EnumerateDirectories(versions).Where(folder => folder != path))
        {
            var descriptor = Path.Combine(folder, Path.GetFileName(folder) + ".json");
            if (!File.Exists(descriptor)) continue;
            using var document = JsonDocument.Parse(File.ReadAllBytes(descriptor));
            if (document.RootElement.TryGetProperty("inheritsFrom", out var parent) && parent.GetString() == instance.Id)
                throw new IOException("其他实例依赖此版本，请先处理依赖关系。");
        }
        return path;
    }

    public static void Toggle(GameInstance instance, string file)
    {
        file = Child(instance.RunDirectory, file);
        var parent = Path.GetFileName(Path.GetDirectoryName(file));
        if (parent is not ("mods" or "resourcepacks" or "shaderpacks") || !File.Exists(file)) throw new IOException("此资源不能启停。");
        var next = file.EndsWith(".disabled", StringComparison.Ordinal) ? file[..^9] : file + ".disabled";
        File.Move(file, next, overwrite: false);
    }

    public static async Task ImportAsync(GameInstance instance, string kind, string file, CancellationToken cancellation)
    {
        if (kind is not ("mods" or "resourcepacks" or "shaderpacks")) throw new IOException("请选择支持导入的资源分类。");
        var extension = Path.GetExtension(file).ToLowerInvariant();
        if (kind == "mods" ? extension != ".jar" : extension != ".zip") throw new IOException("资源文件类型不匹配。");
        var folder = Child(instance.RunDirectory, Path.Combine(instance.RunDirectory, kind)); Directory.CreateDirectory(folder);
        var target = Child(folder, Path.Combine(folder, Path.GetFileName(file)), true);
        var temporary = Path.Combine(folder, ".harbor-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            await using (var input = File.OpenRead(file))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await input.CopyToAsync(output, cancellation);
            File.Move(temporary, target, overwrite: false);
        }
        finally { File.Delete(temporary); }
    }

    public static async Task<(long Bytes, int Files)> MeasureAsync(string root, CancellationToken cancellation)
    {
        return await Task.Run(() =>
        {
            long bytes = 0; var files = 0;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            { cancellation.ThrowIfCancellationRequested(); bytes += new FileInfo(file).Length; files++; }
            return (bytes, files);
        }, cancellation);
    }
}
