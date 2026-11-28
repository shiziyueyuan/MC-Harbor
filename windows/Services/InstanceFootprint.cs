using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MCHarbor.Core.Models;

namespace MCHarbor.Windows.Services;

internal sealed record InstanceFootprint(long Logical, long Allocated, int Files, int Unreadable, bool Shared, bool AllocationKnown);

internal static class InstanceFootprintReader
{
    internal static Task<InstanceFootprint> MeasureAsync(GameInstance game, CancellationToken cancellation) => Task.Run(() =>
    {
        var instance = GameSessionStore.CanonicalDirectory(game.Directory);
        var run = GameSessionStore.CanonicalDirectory(game.RunDirectory);
        var roots = new List<string> { instance };
        var shared = instance.StartsWith(run + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (shared)
        {
            var exclude = new HashSet<string>(["versions", "assets", "libraries", "runtime", "runtimes", "java"], StringComparer.OrdinalIgnoreCase);
            roots.AddRange(Directory.EnumerateFileSystemEntries(run).Where(path => !exclude.Contains(Path.GetFileName(path))));
        }
        else if (!run.Equals(instance, StringComparison.OrdinalIgnoreCase) && !run.StartsWith(instance + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) roots.Add(run);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(roots);
        long logical = 0, allocated = 0; int files = 0, unreadable = 0; var allocationKnown = true;
        while (pending.TryPop(out var path))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    foreach (var child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
                    continue;
                }
                using var handle = CreateFile(path, 0, 7, 0, 3, 0x00200000, 0);
                if (handle.IsInvalid) { unreadable++; continue; }
                var identity = GetIdentity(handle, 18, out var id, (uint)Marshal.SizeOf<FileIdentity>())
                    ? $"{id.Volume:X16}:{id.Low:X16}:{id.High:X16}" : Path.GetFullPath(path);
                if (!seen.Add(identity)) continue;
                if (GetStandard(handle, 1, out var info, (uint)Marshal.SizeOf<FileStandardInfo>()))
                {
                    if (info.Directory != 0) continue;
                    logical += info.Logical; allocated += info.Allocated;
                }
                else { logical += new FileInfo(path).Length; allocationKnown = false; }
                files++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { unreadable++; }
        }
        return new InstanceFootprint(logical, allocated, files, unreadable, shared, allocationKnown);
    }, cancellation);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo { public long Allocated, Logical; public uint Links; public byte Deleted, Directory; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity { public ulong Volume, Low, High; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStandard(SafeFileHandle handle, int kind, out FileStandardInfo result, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdentity(SafeFileHandle handle, int kind, out FileIdentity result, uint size);
}
