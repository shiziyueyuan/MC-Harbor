using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MCHarbor.Core.Models;
using MCHarbor.Windows.Services;

if (args.Contains("--fixture-child")) { await Task.Delay(TimeSpan.FromSeconds(40)); return; }
if (args.Length == 5 && args[0] == "--verify-receipt")
{
    var instance = new GameInstance("isolated", "1.20.1", "vanilla", args[2], args[2], null, DateTimeOffset.UtcNow);
    var sessions = GameSessionStore.Read(args[1], [instance]);
    if (sessions.Count != int.Parse(args[4]) || (sessions.Count > 0 && sessions[0].Pid != int.Parse(args[3])))
        throw new InvalidOperationException("The Java supervisor receipt was not verified by the Windows production reader.");
    Console.WriteLine("Verified Java supervisor state using the Windows production receipt reader.");
    return;
}
var checks = new Dictionary<string, bool>();
void Check(bool passed, string name)
{
    checks[name] = passed;
    Console.WriteLine((passed ? "PASS " : "FAIL ") + name);
    if (!passed) throw new InvalidOperationException(name);
}
var root = Path.Combine(Path.GetTempPath(), "harbor-session-check-" + Guid.NewGuid().ToString("N"));
var run = Path.Combine(root, "instance"); Directory.CreateDirectory(run);
var control = Path.Combine(root, "launch-control"); Directory.CreateDirectory(control);
var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test host.");
var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
start.ArgumentList.Add("--fixture-child");
using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the disposable fixture.");
try
{
    var instance = new GameInstance("Fixture", "1.20.1", "vanilla", run, run, null, DateTimeOffset.UtcNow);
    var marker = Path.Combine(control, Guid.NewGuid().ToString("D") + ".running");
    var started = new DateTimeOffset(child.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
    void Receipt(string id, string directory, int pid, long timestamp) => File.WriteAllText(marker,
        JsonSerializer.Serialize(new { id, runDirectory = directory, pid, started = timestamp }));
    Receipt(instance.Id, run, child.Id, started);
    Check(GameSessionStore.Read(root, [instance]).Count == 1, "recovers a live owned process and known instance");
    var history = Guid.NewGuid();
    PendingGameHistoryStore.Save(root, history, instance, ["fixture-peer"], Path.ChangeExtension(marker, ".stop"));
    Check(PendingGameHistoryStore.Read(root, [instance]).Single() is { Session: var savedSession, Peers: ["fixture-peer"] } && savedSession == history,
        "history context survives closing and reopening the launcher");
    Check(PendingGameHistoryStore.Read(root, [instance with { RunDirectory = Path.Combine(root, "wrong") }]).Count == 0,
        "history recovery refuses contexts from a different instance directory");
    PendingGameHistoryStore.Remove(root, history);
    Check(PendingGameHistoryStore.Read(root, [instance]).Count == 0, "completed history contexts are removed after import");
    var recovered = GameSessionStore.Read(root, [instance])[0];
    Check(recovered.StopFile == Path.ChangeExtension(marker, ".stop"), "control marker belongs to the verified receipt");
    using (var verified = GameSessionStore.OpenVerifiedProcess(recovered))
        Check(verified?.Id == child.Id, "process revalidation uses its actual start time");
    Receipt(instance.Id, run, child.Id, started - 1);
    Check(GameSessionStore.Read(root, [instance]).Count == 0, "rejects stale PID reuse");
    Receipt("Unknown", run, child.Id, started);
    Check(GameSessionStore.Read(root, [instance]).Count == 0, "ignores receipts from another library");
    Receipt(instance.Id, Path.Combine(root, "unrelated"), child.Id, started);
    Check(GameSessionStore.Read(root, [instance]).Count == 0, "rejects mismatched instance directories");
    Receipt(instance.Id, run, 1, started);
    Check(GameSessionStore.Read(root, [instance]).Count == 0, "rejects reserved process identifiers");
    File.WriteAllText(marker, new string('x', 20000));
    Check(GameSessionStore.Read(root, [instance]).Count == 0, "ignores oversized receipts without allocating their contents");
    File.WriteAllText(marker, "{\"id\":false}");
    Check(GameSessionStore.Read(root, [instance]).Count == 0, "ignores malformed receipt field types");
    Receipt(instance.Id, run, child.Id, started);
    File.Move(marker, Path.Combine(control, "invalid.running"));
    Check(GameSessionStore.Read(root, [instance]).Count == 0, "rejects unrecognized control filenames");

    var logs = Path.Combine(run, "logs"); var screenshots = Path.Combine(run, "screenshots");
    Directory.CreateDirectory(logs); Directory.CreateDirectory(screenshots);
    var log = Path.Combine(logs, "latest.log");
    File.WriteAllText(log, string.Concat(Enumerable.Repeat("fixture-line\n", 60000)) + "FINAL-LINE\n");
    var preview = InstanceMediaStore.ReadLog(run, log);
    Check(preview.EndsWith("FINAL-LINE\n") && preview.Length <= InstanceMediaStore.MaximumLogBytes + 100, "large logs retain a bounded tail and their final lines");
    var outside = Path.Combine(root, "outside.log"); File.WriteAllText(outside, "unrelated");
    Check(!InstanceMediaStore.IsSafeFile(run, "logs", outside), "media actions cannot open files outside the instance folder");
    Check(new FileInfo(log).Length > InstanceMediaStore.MaximumLogBytes, "bounded previews preserve the complete source log");
    File.WriteAllBytes(Path.Combine(screenshots, "fixture.png"), [137, 80, 78, 71]);
    File.WriteAllText(Path.Combine(screenshots, "private.json"), "fixture");
    Check(InstanceMediaStore.Read(run, "screenshots").Select(item => item.Name).SequenceEqual(["fixture.png"]), "screenshots exclude unrelated file types");
    Check(InstanceMediaStore.Read(run, "logs").Count == 1, "Mac-equivalent game log files are discoverable");
    var sharedRoot = Path.Combine(root, "library");
    var versionDirectory = Path.Combine(sharedRoot, "versions", "Fixture");
    Directory.CreateDirectory(versionDirectory);
    Directory.CreateDirectory(Path.Combine(sharedRoot, "assets"));
    Directory.CreateDirectory(Path.Combine(sharedRoot, "mods"));
    File.WriteAllBytes(Path.Combine(versionDirectory, "fixture.json"), new byte[17]);
    File.WriteAllBytes(Path.Combine(sharedRoot, "assets", "cache.bin"), new byte[101]);
    File.WriteAllBytes(Path.Combine(sharedRoot, "mods", "fixture.jar"), new byte[31]);
    var sharedGame = instance with { Directory = versionDirectory, RunDirectory = sharedRoot };
    var footprint = await InstanceFootprintReader.MeasureAsync(sharedGame, CancellationToken.None);
    Check(footprint.Shared && footprint.Files == 2 && footprint.Logical == 48 && footprint.Unreadable == 0,
        "shared-instance space includes the instance and mods while excluding common assets");
    Check(!footprint.AllocationKnown || footprint.Allocated >= 0, "allocated-size query returns a valid value or explicitly marks it unavailable");

    Check(!Rgb.TryParse("#1234567", out _) && !Rgb.TryParse("#GGGGGG", out _), "custom colors reject malformed input");
    Check(Math.Abs(Rgb.Contrast(new(0, 0, 0), new(255, 255, 255)) - 21) < 0.00001, "contrast follows the sRGB luminance ratio");
    var protectedInk = Rgb.Protect(new(220, 220, 220), new(245, 245, 245), true);
    Check(Rgb.Contrast(protectedInk, new(245, 245, 245)) >= 4.5, "contrast protection keeps pale-theme text readable");
    var theme = new HarborThemeSettings { InverseMode = "rgb" };
    Check(theme.Inverse(new(10, 20, 30)) == new Rgb(245, 235, 225), "RGB inverse preserves the source palette semantics");
    theme.Roles["primaryText"] = "custom"; theme.Custom["primaryText"] = "invalid";
    Check(theme.Resolve("primaryText", new(15, 15, 15), new(100, 100, 100)) == new Rgb(15, 15, 15), "invalid saved custom colors fall back safely");
    foreach (var width in new[] { 1040d, 1280d, 1440d })
    {
        var content = width - MacLayout.SidebarWidth - 2 * MacLayout.PageInset;
        var (columns, itemWidth) = MacLayout.Cards(content, 300, 480, 14);
        Check(itemWidth <= 480 && columns * itemWidth + (columns - 1) * 14 <= content + 0.01,
            $"instance cards fit the {width:0}px Mac reference viewport");
        var detail = content - MacLayout.AccountListWidth - 22;
        Check(detail > 350, $"account columns preserve readable preview width at {width:0}px");
    }
    child.Kill(entireProcessTree: true); await child.WaitForExitAsync();
    Check(GameSessionStore.OpenVerifiedProcess(recovered) is null, "expired receipts cannot target a later process");
    Console.WriteLine($"{checks.Count} Windows session, media, theme and layout checks passed.");
}
finally
{
    if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
    var prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(root).StartsWith("harbor-session-check-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe fixture cleanup path.");
    Directory.Delete(root, recursive: true);
}
