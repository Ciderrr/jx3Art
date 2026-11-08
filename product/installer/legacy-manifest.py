"""Write the v1.0 release-file allowlist used by the installer migration."""
import hashlib
import sys
from pathlib import Path, PurePosixPath
from zipfile import ZipFile

V10_ZIP_SHA256 = "2afac0b3a7b854ec3de37c4cf626b0d33cb3844e373d2ca18c1bf4d70579eff6"
ROOT = "JX3Artist-1.0-win-x64"

archive, manifest = map(Path, sys.argv[1:])
with archive.open("rb") as source:
    if hashlib.file_digest(source, "sha256").hexdigest() != V10_ZIP_SHA256:
        raise SystemExit("Use the published v1.0 portable ZIP.")

rows = {}
with ZipFile(archive) as package:
    for entry in package.infolist():
        if entry.is_dir():
            continue
        path = PurePosixPath(entry.filename.replace("\\", "/"))
        if path.parts[0] != ROOT or len(path.parts) < 2 or ".." in path.parts:
            raise ValueError(f"Unexpected release path: {entry.filename}")
        relative = "\\".join(path.parts[1:])
        if relative.lower() in rows or any(char in relative for char in "\t\r\n"):
            raise ValueError(f"Invalid release path: {entry.filename}")
        with package.open(entry) as source:
            rows[relative.lower()] = f"{hashlib.file_digest(source, 'sha256').hexdigest()}\t{relative}"

if "jx3artist.exe" not in rows:
    raise ValueError("Release executable missing.")
manifest.write_text("\n".join(rows.values()) + "\n", encoding="utf-8")
