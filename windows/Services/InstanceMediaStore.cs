using System.Text;

namespace MCHarbor.Windows.Services;

public sealed record InstanceMediaFile(string Path, string Name, DateTime Modified);

internal static class InstanceMediaStore
{
    internal const int MaximumLogBytes = 500_000;
    internal static string DirectoryFor(string runDirectory, string category) => Path.Combine(runDirectory,
        category is "screenshots" or "logs" ? category : throw new ArgumentOutOfRangeException(nameof(category)));

    internal static IReadOnlyList<InstanceMediaFile> Read(string runDirectory, string category)
    {
        var directory = DirectoryFor(runDirectory, category);
        if (!Directory.Exists(directory) || File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) return [];
        return Directory.EnumerateFiles(directory).Where(path => IsSafeFile(runDirectory, category, path))
            .Where(path => category == "screenshots" ? Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg"
                : path.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .Take(5000).Select(path => new InstanceMediaFile(path, Path.GetFileName(path), File.GetLastWriteTimeUtc(path)))
            .OrderByDescending(item => item.Modified).Take(500).ToArray();
    }

    internal static bool IsSafeFile(string runDirectory, string category, string file)
    {
        try
        {
            var directory = Path.GetFullPath(DirectoryFor(runDirectory, category));
            var path = Path.GetFullPath(file);
            return Path.GetDirectoryName(path)!.Equals(directory, StringComparison.OrdinalIgnoreCase)
                && !File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)
                && (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    internal static string ReadLog(string runDirectory, string file)
    {
        if (!IsSafeFile(runDirectory, "logs", file) || Path.GetExtension(file).ToLowerInvariant() is not (".log" or ".txt"))
            throw new IOException("请选择实例目录中的普通日志文件。");
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var truncated = input.Length > MaximumLogBytes;
        if (truncated) input.Seek(-MaximumLogBytes, SeekOrigin.End);
        var buffer = new byte[MaximumLogBytes]; var count = 0;
        while (count < buffer.Length)
        {
            var read = input.Read(buffer, count, buffer.Length - count);
            if (read == 0) break;
            count += read;
        }
        var text = Encoding.UTF8.GetString(buffer, 0, count);
        if (truncated && text.IndexOf('\n') is var newline && newline >= 0) text = text[(newline + 1)..];
        return (truncated ? "（显示日志末尾，完整文件保留在磁盘）\n" : "") + text;
    }
}
