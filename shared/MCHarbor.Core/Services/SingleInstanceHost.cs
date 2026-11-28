using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCHarbor.Core.Services;

public sealed class SingleInstanceHost : IAsyncDisposable
{
    private readonly FileStream _lease;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<Guid> _received = [];
    private readonly Queue<Guid> _receiptOrder = new();
    private readonly Queue<string?> _pending = new();
    private readonly object _gate = new();
    private readonly Task _server;
    private Action<string?>? _activate;
    private readonly string _path;
    private static string SocketPath => Path.Combine(HarborHost.Paths.Runtime, "instance-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(HarborHost.DataDirectory)))[..16] + ".sock");

    private SingleInstanceHost(FileStream lease)
    {
        _lease = lease; _path = SocketPath; File.Delete(_path);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_path));
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _listener.Listen(8); _server = ServeAsync();
    }
    public static SingleInstanceHost? Acquire()
    {
        if (!OperatingSystem.IsLinux()) return null;
        FileStream lease;
        try { lease = new FileStream(SocketPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
        try { return new SingleInstanceHost(lease); } catch { lease.Dispose(); throw; }
    }
    public void SetActivationHandler(Action<string?> activate)
    {
        lock (_gate) { _activate = activate; while (_pending.TryDequeue(out var id)) activate(id); }
    }
    public static async Task ForwardAsync(string? instance, CancellationToken cancellation)
    {
        var request = JsonSerializer.Serialize(new { request = Guid.NewGuid(), instance });
        for (var attempt = 0; ; attempt++)
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), cancellation);
                await SendAllAsync(socket, Encoding.UTF8.GetBytes(request + "\n"), cancellation);
                var answer = new byte[32]; var total = 0;
                while (total < answer.Length)
                {
                    var count = await socket.ReceiveAsync(answer.AsMemory(total), cancellation); if (count == 0) break;
                    total += count; if (Array.IndexOf(answer, (byte)'\n', 0, total) >= 0) break;
                }
                if (Encoding.UTF8.GetString(answer, 0, total).Trim() != "accepted") throw new IOException("已运行窗口未接收启动请求。");
                return;
            }
            catch (SocketException) when (attempt < 20) { await Task.Delay(100, cancellation); }
        }
    }
    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptAsync(_stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(4));
                var data = new byte[4096]; var total = 0;
                while (total < data.Length)
                {
                    var count = await client.ReceiveAsync(data.AsMemory(total), timeout.Token); if (count == 0) break;
                    total += count; if (Array.IndexOf(data, (byte)'\n', 0, total) >= 0) break;
                }
                if (total == data.Length) continue;
                var json = JsonNode.Parse(Encoding.UTF8.GetString(data, 0, total))!.AsObject();
                var request = Guid.Parse(json["request"]!.GetValue<string>()); var instance = json["instance"]?.GetValue<string>();
                if (instance?.Length > 512 || instance?.Any(char.IsControl) == true) continue;
                lock (_gate)
                {
                    if (_received.Add(request))
                    {
                        _receiptOrder.Enqueue(request); if (_receiptOrder.Count > 4096) _received.Remove(_receiptOrder.Dequeue());
                        if (_activate is null) _pending.Enqueue(instance); else _activate(instance);
                    }
                }
                await SendAllAsync(client, Encoding.UTF8.GetBytes("accepted\n"), timeout.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or SocketException or JsonException or FormatException or OperationCanceledException) { }
        }
    }
    private static async Task SendAllAsync(Socket socket, ReadOnlyMemory<byte> payload, CancellationToken cancellation)
    {
        while (payload.Length > 0)
        {
            var sent = await socket.SendAsync(payload, cancellation); if (sent == 0) throw new IOException("单实例连接已关闭。");
            payload = payload[sent..];
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Dispose();
        try { await _server; } catch (ObjectDisposedException) { }
        File.Delete(_path); _lease.Dispose(); _stop.Dispose();
    }
}
