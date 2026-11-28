"""Apply upstream 1.7.2's bounded display buffer to the pinned HMCL process class."""
from pathlib import Path
import tarfile


def prepare_managed_process_source(repository: Path, directory: Path) -> Path:
    member = "HMCL/HMCLCore/src/main/java/org/jackhuang/hmcl/util/platform/ManagedProcess.java"
    with tarfile.open(repository / "upstream/HMCL-3.16.4-source.tar.gz") as archive:
        stream = archive.extractfile(member)
        if stream is None:
            raise SystemExit("Pinned HMCL process source is missing.")
        text = stream.read().decode("utf-8")
    declaration = "private final List<String> lines = new ArrayList<>();"
    append = "lines.add(line);"
    if text.count(declaration) != 1 or text.count(append) != 1:
        raise SystemExit("Pinned HMCL ManagedProcess source changed; inspect before patching.")
    text = text.replace(declaration, "private final ArrayDeque<String> lines = new ArrayDeque<>();")
    text = text.replace(append, "if (lines.size() == 256) lines.removeFirst();\n            lines.addLast(line.length() <= 4096 ? line : line.substring(0, 4096));")
    directory.mkdir(parents=True, exist_ok=True)
    destination = directory / "ManagedProcess.java"
    destination.write_text(text, encoding="utf-8")
    return destination
