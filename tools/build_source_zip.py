"""Build a source-only package from a fixed allowlist; never include local data."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile


def main():
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    version = json.loads((root / "package.json").read_text(encoding="utf-8-sig"))["version"]
    if not version or any(c not in "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ.-_" for c in version):
        raise ValueError("Invalid package version")
    output = args.output or root / "dist" / ("camera-bridge-" + version + "-source.zip")
    names = [
        ".gitignore", "README.md", "FRONTEND.md", "VALIDATION.md", "package.json", "server.js",
        "index.html", "bridge.config.example.json", "Setup Camera.cmd", "Setup-Camera.ps1", "Start Camera.cmd",
        "Stop Camera.cmd", "Start-Camera.ps1", "Stop-Camera.ps1",
        "Test-CameraEof.ps1",
        "tools/Test-OobEof.ps1", "tools/build_source_zip.py",
        "docs/images/triforce-ip-redirections.png",
        "docs/images/linked-cabinets.jpg", "docs/SOURCE.md", "docs/SETUP.md",
        "tools/Build-Portable.ps1", "portable/CameraBridge.cfg", "portable/app.manifest",
    ]
    if (root / "LICENSE").is_file():
        names.append("LICENSE")
    for pattern in ("lib/*.js", "lib/*.py", "test/*.js", "portable/*.cs", "portable/tests/*.cs", "portable/tests/*.ps1"):
        names.extend(p.relative_to(root).as_posix() for p in root.glob(pattern) if p.is_file())
    names = sorted(set(names))
    # Package only the declared source files. Local integration tools and data
    # outside this allowlist are excluded automatically.
    for name in names:
        source = root / name
        if not source.is_file() or source.is_symlink():
            raise ValueError("Missing source file or unsupported symlink: " + name)
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + ".tmp")
    manifest = []
    try:
        with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for name in names:
                data = (root / name).read_bytes()
                info = zipfile.ZipInfo("camera-bridge/" + name, date_time=(2000, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o100644 << 16
                archive.writestr(info, data, compresslevel=9)
                manifest.append({"path": name, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})
        with zipfile.ZipFile(temporary) as archive:
            if archive.testzip() is not None:
                raise ValueError("Archive integrity check failed")
        temporary.replace(output)
    finally:
        temporary.unlink(missing_ok=True)
    report = {
        "package": output.name,
        "version": version,
        "bytes": output.stat().st_size,
        "sha256": hashlib.sha256(output.read_bytes()).hexdigest(),
        "licenseIncluded": "LICENSE" in names,
        "files": manifest,
    }
    receipt = output.with_suffix(".manifest.json")
    receipt.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k != "files"} | {"fileCount": len(names), "output": str(output)}))


if __name__ == "__main__":
    main()
