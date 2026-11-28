using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCHarbor.Core.Services;

public interface IBridgeClient
{
    Task<JsonObject> InvokeAsync(string command, IReadOnlyDictionary<string, object?>? arguments = null,
        Action<JsonObject>? onEvent = null, CancellationToken cancellationToken = default);
}

public sealed class NativeBridgeClient(string javaExecutable) : IBridgeClient
{
    public static string? FindLauncherJava()
    {
        var executable = OperatingSystem.IsWindows() ? "java.exe" : "java";
        var candidates = new List<string> { Path.Combine(HarborHost.ApplicationDirectory, "Runtime", "bin", executable) };
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } javaHome)
            candidates.Add(Path.Combine(javaHome, "bin", executable));
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries).Select(folder => Path.Combine(folder, executable)));
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<JsonObject> InvokeAsync(string command, IReadOnlyDictionary<string, object?>? arguments = null,
        Action<JsonObject>? onEvent = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var backend = Path.Combine(HarborHost.ApplicationDirectory, "Backend");
        foreach (var file in new[] { "Backend.jar", "HMCLCore.jar" })
            if (!File.Exists(Path.Combine(backend, file))) throw new FileNotFoundException("缺少后端文件：" + file);
        var fx = Path.Combine(backend, "javafx");
        if (!Directory.Exists(fx) || !Directory.EnumerateFiles(fx, "*.jar").Any())
            throw new FileNotFoundException("缺少对应平台的 JavaFX 依赖，请使用完整发行包。");
        var info = new ProcessStartInfo(javaExecutable)
        {
            WorkingDirectory = HarborHost.ApplicationDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in new[] { "-Xms32m", "-Xmx384m", "-Dfile.encoding=UTF-8", "-Djava.awt.headless=true",
            "-Dshenggan.headless=true", "-Dhmcl.dir=" + HarborHost.DataDirectory, "-cp",
            string.Join(Path.PathSeparator, Path.Combine(backend, "Backend.jar"), Path.Combine(backend, "HMCLCore.jar"), Path.Combine(fx, "*")),
            "net.morben.shenggan.NativeBridge" }) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException("无法启动 Java 后端。");
        var launched = 0;
        using var registration = cancellationToken.Register(() =>
        {
            if (Volatile.Read(ref launched) != 0) return;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        var request = new JsonObject { ["command"] = command, ["root"] = HarborHost.GameRoot,
            ["source"] = HarborHost.GetPreference("downloadSource", "auto"),
            ["threads"] = int.TryParse(HarborHost.GetPreference("downloadThreads", "8"), out var threads) ? Math.Clamp(threads, 2, 24) : 8 };
        if (arguments is not null) foreach (var (key, value) in arguments)
            request[key] = value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(value);
        JsonObject? result = null;
        string? error = null;
        try
        {
            await process.StandardInput.WriteLineAsync(request.ToJsonString());
            process.StandardInput.Close();
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length > 32_000_000) throw new IOException("后端事件超过大小限制。");
                JsonObject? item;
                try { item = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { continue; }
                if (item is null) continue;
                var kind = item["event"]?.GetValue<string>();
                if (kind == "launched") Volatile.Write(ref launched, 1);
                if (kind == "result") result = item["data"]?.DeepClone().AsObject();
                if (kind == "error") error = item["message"]?.GetValue<string>();
                try { onEvent?.Invoke(item); } catch (Exception callbackError) { System.Diagnostics.Trace.WriteLine(callbackError.GetType().Name); }
            }
            await process.WaitForExitAsync(); await stderr;
        }
        catch
        {
            if (Volatile.Read(ref launched) == 0)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
            }
            throw;
        }
        if (Volatile.Read(ref launched) == 0) cancellationToken.ThrowIfCancellationRequested();
        if (error is not null) throw new IOException(error);
        if (process.ExitCode != 0) throw new IOException($"Java 后端退出，代码 {process.ExitCode}。");
        return result ?? throw new IOException("Java 后端未返回结果。");
    }
}
