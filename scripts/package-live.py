"""Package a previously built mod, never the proprietary game assemblies."""
import argparse
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("output", type=Path)
args = parser.parse_args()
files = {
    "CoiCoop/CoiCoop.dll": root / "src/CoiCoop/bin/Release/net48/CoiCoop.dll",
    "README.ru.md": root / "docs/LIVE_HOST_QUICKSTART_RU.md",
    "IMPLEMENTATION.md": root / "docs/LIVE_HOST_IMPLEMENTATION.md",
}
for name in ("manifest.json", "config.json"):
    files["CoiCoop/" + name] = root / "src/CoiCoop" / name
for name in ("Host.bat", "Join.bat", "play-coop.ps1", "live-common.ps1"):
    files["scripts/" + name] = root / "scripts" / name
for path in files.values():
    if not path.is_file():
        raise SystemExit(f"Missing build/package input: {path}")
args.output.parent.mkdir(parents=True, exist_ok=True)
with ZipFile(args.output, "w", ZIP_DEFLATED) as archive:
    for name, path in files.items():
        data = path.read_bytes()
        if name.endswith(".bat"):
            data = data.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
        archive.writestr(name, data)
with ZipFile(args.output) as archive:
    assert archive.testzip() is None
    assert set(archive.namelist()) == set(files)
print(args.output.resolve())
