using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MCHarbor.Core.Services;

public interface ISecretStore : IDisposable
{
    bool CanPersist { get; }
    Task<string?> ReadAsync(string key, CancellationToken cancellation = default);
    Task WriteAsync(string key, string value, CancellationToken cancellation = default);
    Task DeleteAsync(string key, CancellationToken cancellation = default);
}

public sealed class SessionSecretStore : ISecretStore
{
    private readonly Dictionary<string, byte[]> _values = [];
    private readonly object _gate = new();
    public bool CanPersist => false;
    public Task<string?> ReadAsync(string key, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(_values.TryGetValue(key, out var value) ? Encoding.UTF8.GetString(value) : null);
    }
    public Task WriteAsync(string key, string value, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_values.Remove(key, out var previous)) CryptographicOperations.ZeroMemory(previous);
            _values[key] = Encoding.UTF8.GetBytes(value);
        }
        return Task.CompletedTask;
    }
    public Task DeleteAsync(string key, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (_gate) if (_values.Remove(key, out var previous)) CryptographicOperations.ZeroMemory(previous);
        return Task.CompletedTask;
    }
    public void Dispose()
    {
        lock (_gate) { foreach (var item in _values.Values) CryptographicOperations.ZeroMemory(item); _values.Clear(); }
    }
}

/// <summary>Uses libsecret's Secret Service client; secret values only travel through redirected pipes.</summary>
public sealed class SecretServiceStore : ISecretStore
{
    private readonly SessionSecretStore _session = new();
    private readonly string? _tool;
    public bool CanPersist => _tool is not null && !UseSessionOnly;
    public bool UseSessionOnly { get; set; }
    public bool RememberThisSession { get; set; } = true;
    public void ClearSessionCache() => _session.Dispose();
    public SecretServiceStore()
    {
        if (OperatingSystem.IsLinux()) _tool = Executables.Find("secret-tool");
        UseSessionOnly = HarborHost.GetPreference("sessionOnly") == "true";
        RememberThisSession = HarborHost.GetPreference("rememberCredentials", "true") == "true";
    }
    private static string Lookup(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public async Task<string?> ReadAsync(string key, CancellationToken cancellation = default)
    {
        if ((RememberThisSession || !CanPersist) && await _session.ReadAsync(key, cancellation) is { } cached) return cached;
        if (!CanPersist) return null;
        var result = await CommandRunner.RunAsync(_tool!, ["lookup", "application", "mc-harbor", "key", Lookup(key)], null, cancellation);
        if (result.ExitCode == 1 && string.IsNullOrEmpty(result.Output)) return null;
        if (result.ExitCode != 0) throw new IOException("无法读取系统钥匙环，请解锁钥匙环或选择本次会话登录。");
        var value = result.Output.TrimEnd('\r', '\n');
        if (RememberThisSession) await _session.WriteAsync(key, value, cancellation);
        return value;
    }
    public async Task WriteAsync(string key, string value, CancellationToken cancellation = default)
    {
        if (CanPersist)
        {
            var result = await CommandRunner.RunAsync(_tool!, ["store", "--label=MC Harbor", "application", "mc-harbor", "key", Lookup(key)], value, cancellation);
            if (result.ExitCode != 0) throw new IOException("系统钥匙环未保存凭据，请确认授权或选择本次会话登录。");
        }
        await _session.WriteAsync(key, value, cancellation);
    }
    public async Task DeleteAsync(string key, CancellationToken cancellation = default)
    {
        if (CanPersist)
        {
            var result = await CommandRunner.RunAsync(_tool!, ["clear", "application", "mc-harbor", "key", Lookup(key)], null, cancellation);
            if (result.ExitCode is not (0 or 1)) throw new IOException("无法移除系统钥匙环中的凭据。");
        }
        await _session.DeleteAsync(key, cancellation);
    }
    public void Dispose() => _session.Dispose();
}

public static class Executables
{
    public static string? Find(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
}

public sealed record CommandResult(int ExitCode, string Output, string Error);

public static class CommandRunner
{
    public static async Task<CommandResult> RunAsync(string executable, IEnumerable<string> arguments, string? input = null,
        CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("无法启动辅助程序。");
        async Task<string> DrainAsync(StreamReader reader)
        {
            var output = new StringBuilder(); var buffer = new char[4096]; int received;
            while ((received = await reader.ReadAsync(buffer.AsMemory(), limit.Token)) > 0)
            {
                if (output.Length + received > 16_000_000) throw new IOException("辅助程序输出超过限制。");
                output.Append(buffer, 0, received);
            }
            return output.ToString();
        }
        var stdout = DrainAsync(process.StandardOutput); var stderr = DrainAsync(process.StandardError);
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), limit.Token);
            process.StandardInput.Close();
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(limit.Token));
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); } catch (Exception) { }
            throw;
        }
    }
}
