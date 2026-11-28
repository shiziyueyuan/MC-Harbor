"""Verify detached supervision and the production Windows receipt reader with fake games."""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import uuid

ROOT = Path(__file__).resolve().parent.parent


def wait_for(predicate, message, timeout=15):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate(): return
        time.sleep(0.1)
    raise AssertionError(message)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--jdk", required=True, type=Path)
    parser.add_argument("--dotnet", default=shutil.which("dotnet"))
    args = parser.parse_args()
    if not args.dotnet: raise SystemExit("Pass the .NET 10 executable with --dotnet.")
    java = args.jdk / "bin/java.exe"
    compiler = args.jdk / "bin/javac.exe"
    reader = ROOT / "tests/windows-game-sessions/bin/Release/net10.0-windows/SessionChecks.dll"
    if not reader.is_file(): raise SystemExit("Build tests/windows-game-sessions in Release first.")
    completed_checks = []
    with tempfile.TemporaryDirectory(prefix="harbor-windows-lifecycle-", dir=ROOT / "build") as temporary:
        stage = Path(temporary); run = stage / "instance"; run.mkdir()
        control = stage / "launch-control"; marker = control / f"{uuid.uuid4()}.stop"; receipt = marker.with_suffix(".running")
        source = stage / "LifecycleFixture.java"
        source.write_text(r'''
package net.morben.shenggan;
import java.nio.file.*;
import java.util.List;
import org.jackhuang.hmcl.util.platform.ManagedProcess;
public final class LifecycleFixture {
    public static void main(String[] args) throws Exception {
        if (args.length == 1 && args[0].equals("--child")) { Thread.sleep(45000); return; }
        try (NativeGameControl owner = new NativeGameControl(Path.of(args[0]), Path.of(args[1]), args[2])) {
            owner.checkPendingStop();
            if (args.length > 3) throw new AssertionError("Duplicate directory owner was accepted");
            String java = Path.of(System.getProperty("java.home"), "bin", "java.exe").toString();
            Process child = new ProcessBuilder(java, "-cp", System.getProperty("java.class.path"), LifecycleFixture.class.getName(), "--child").start();
            try {
                ManagedProcess display = new ManagedProcess(child, List.of(java));
                for (int i = 0; i < 2048; i++) display.addLine(i + " " + "X".repeat(8192));
                var lines = display.getLines(null);
                if (lines.size() != 256 || !lines.get(0).startsWith("1792 ") ||
                    !lines.get(255).startsWith("2047 ") || lines.get(255).length() != 4096)
                    throw new AssertionError("Game display log is not bounded");
                owner.record("isolated", child);
                System.out.println(child.pid());
                System.out.flush();
                owner.waitFor(child);
            } finally {
                if (child.isAlive()) child.destroy();
                Files.writeString(Path.of(args[0]).resolve("completed"), "stopped");
            }
        }
    }
}
''', encoding="utf-8")
        classpath = os.pathsep.join(map(str, [ROOT / "build/Backend.jar", ROOT / "vendor/HMCLCore.jar", stage]))
        subprocess.run([str(compiler), "--release", "17", "-encoding", "UTF-8", "-cp", classpath, "-d", str(stage), str(source)], check=True)
        command = [str(java), "-Dfile.encoding=UTF-8", "-cp", classpath, "net.morben.shenggan.LifecycleFixture", str(stage), str(run), str(marker)]
        invoker = stage / "launcher-side.py"
        invoker.write_text('''import subprocess,sys
process = subprocess.Popen(sys.argv[1:], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    creationflags=subprocess.CREATE_NEW_PROCESS_GROUP | subprocess.CREATE_NO_WINDOW)
line = process.stdout.readline()
if not line.strip().isdigit(): raise RuntimeError(process.stderr.read().decode("utf-8", "replace"))
process.stdout.close(); process.stderr.close()
''', encoding="utf-8")
        try:
            subprocess.run([sys.executable, str(invoker), *command], check=True, timeout=20)
            wait_for(receipt.exists, "Supervisor did not record the game.")
            running = json.loads(receipt.read_text(encoding="utf-8"))
            verifier = [str(args.dotnet), str(reader), "--verify-receipt", str(stage), str(run), str(running["pid"])]
            subprocess.run([*verifier, "1"], check=True, timeout=15)
            completed_checks.extend(["game-survives-launcher-side-exit", "native-java-receipt-restores-in-windows", "display-log-bounded-to-256-lines-of-4096-characters"])
            duplicate = [*command[:-1], str(control / f"{uuid.uuid4()}.stop"), "duplicate"]
            result = subprocess.run(duplicate, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=15)
            error_text = result.stderr.decode("utf-8", "replace")
            if "已有实例" not in error_text:
                error_text = result.stderr.decode("gb18030", "replace")
            assert result.returncode != 0 and "已有实例" in error_text, f"Duplicate launch check failed: exit={result.returncode}, error={error_text}"
            completed_checks.append("duplicate-instance-directory-is-locked")
            marker.write_text("stop", encoding="utf-8")
            wait_for(lambda: (stage / "completed").exists() and not receipt.exists(), "Stop request did not release the game and receipt.")
            subprocess.run([*verifier, "0"], check=True, timeout=15)
            completed_checks.extend(["restored-game-can-be-stopped", "receipt-and-stop-marker-cleaned"])
        finally:
            if receipt.exists():
                marker.write_text("stop", encoding="utf-8")
                wait_for(lambda: not receipt.exists(), "Fixture cleanup did not finish.")
    evidence = ROOT / "evidence/windows-game-lifecycle.json"
    evidence.write_text(json.dumps({"passed": len(completed_checks), "failed": 0, "realGameOrUserDataUsed": False, "checks": completed_checks}, indent=2) + "\n", encoding="utf-8")
    for name in completed_checks: print("PASS " + name)


if __name__ == "__main__": main()
