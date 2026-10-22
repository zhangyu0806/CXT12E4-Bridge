#!/usr/bin/env python3
"""Package a verified cross-build and its exact source for Windows installation.

Only explicit installer/source paths and tracked integration/manual files are
included. No runtime settings, caches, credentials, firmware or SDK downloads.
The package does not claim Windows-native testing and the installer rebuilds
and runs native self-tests before changing the user's installed Bridge.
"""

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import zipfile


ROOT = Path(__file__).resolve().parents[2]


def sha256(content):
    return hashlib.sha256(content).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build-dir", type=Path, default=ROOT / "outputs" / "windows-cross-windowed")
    parser.add_argument("--output", type=Path, default=ROOT / "outputs" / "CXT12E4-Windows-cross.zip")
    args = parser.parse_args()
    build = args.build_dir.resolve()
    cross_manifest = json.loads((build / "cross-build-manifest.json").read_text(encoding="utf-8"))
    binary = (build / "Cxt12E4Bridge.exe").read_bytes()
    if sha256(binary) != cross_manifest["executableSha256"] or binary[:2] != b"MZ":
        raise RuntimeError("Build executable is missing, changed, or not a Windows PE")
    files = {}

    def include(relative, expected=None):
        source = ROOT / relative
        if source.is_symlink() or not source.is_file():
            raise RuntimeError("Expected a regular source file: " + relative)
        content = source.read_bytes()
        if expected is not None and sha256(content) != expected:
            raise RuntimeError("Source changed since cross-compilation; rebuild first: " + relative)
        files[relative] = content

    # Include only source files recorded by the compiler, checking their exact bytes.
    for relative, expected in cross_manifest["sources"].items():
        path = Path(relative)
        if path.parts[:2] != ("src", "Cxt1024Bridge") or len(path.parts) != 3 or path.suffix != ".cs":
            raise RuntimeError("Unexpected build source path: " + relative)
        include(relative, expected)
    actual_sources = {p.relative_to(ROOT).as_posix() for p in (ROOT / "src" / "Cxt1024Bridge").glob("*.cs")}
    if actual_sources != set(cross_manifest["sources"]):
        raise RuntimeError("Source inventory changed since build; rebuild before packaging")
    for relative in (
        "build.ps1", "README.md", "README-CXT12E4.md", "CONTRIBUTING.md",
        "LICENSE", "THIRD_PARTY_NOTICES.md", "firmware/cxt12e4_chatgpt_bridge/LICENSE",
        "install/windows-home/Install-Cxt12E4Bridge.ps1",
        "install/windows-home/Uninstall-Cxt12E4Bridge.ps1",
        "install/windows-home/Verify-Cxt12E4Bridge.ps1",
        "install/windows-home/README.md",
        "install/windows-home/BUILDING-CROSS.md",
        "install/windows-home/build-cross.py",
        "install/windows-home/test-portable.py",
        "install/windows-home/package-cross.py",
        "install/wsl-home/Install-Cxt12E4Integrations.sh",
        "install/wsl-home/README.md",
    ):
        include(relative)
    tracked = subprocess.check_output(["git", "ls-files", "-z", "--", "integrations", "docs/cxt12e4-manual"], cwd=str(ROOT)).decode("utf-8").split("\0")
    for relative in tracked:
        if relative:
            include(relative)
    if "docs/cxt12e4-manual/index.html" not in files:
        raise RuntimeError("Tracked Windows manual is missing")
    files["outputs/Cxt12E4Bridge.exe"] = binary
    files["outputs/Cxt1024Bridge.exe"] = binary
    files["outputs/cross-build-manifest.json"] = (json.dumps(cross_manifest, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    files["START-HERE.md"] = """# CXT12E4 Windows Bridge

此包包含 Xbox 分层控制、12E4 配置、Windows 安装器和全部相关源码。
内含 EXE 在 macOS 交叉编译，尚未在 Windows 原生环境运行；它不是实机验收结论。

解压全部文件到固定目录，打开该目录的 PowerShell，执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\\install\\windows-home\\Install-Cxt12E4Bridge.ps1
```

安装器会先用 Windows 自带 .NET Framework 编译器重新构建并运行完整自测。
自测失败会停止；通过后才备份和更新本机 Bridge。不需要管理员权限，不刷键盘固件。

只检查构建和文件准备、不改现有 Bridge：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\\install\\windows-home\\Install-Cxt12E4Bridge.ps1 -PrepareOnly
```

功能说明见 `docs/cxt12e4-manual/index.html`；安装说明见 `install/windows-home/README.md`。
配置入口为 Bridge 托盘菜单中的“分层按键设置”。Xbox 需先在 Windows 蓝牙设置里配对。
Windows 原生窗口、输入、XInput、蓝牙及手柄手感仍需在目标电脑实测。
""".encode("utf-8")
    manifest = {
        "formatVersion": 1,
        "createdAtUtc": datetime.now(timezone.utc).isoformat(),
        "sourceCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=str(ROOT), text=True).strip(),
        "sourceIdentity": "Per-file hashes include current working-tree changes; sourceCommit is the base commit only",
        "validation": {
            "crossCompilation": "passed",
            "windowsNativeSelfTest": "not run by this packager",
            "windowsPhysicalAcceptance": "not performed",
            "installation": "Installer rebuilds and runs native --self-test before modifying the installed Bridge",
        },
        "files": {relative: sha256(content) for relative, content in sorted(files.items())},
    }
    files["package-manifest.json"] = (json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(prefix=".cxt-package-", suffix=".zip", dir=str(output.parent), delete=False) as temporary:
        temporary_path = Path(temporary.name)
    try:
        with zipfile.ZipFile(temporary_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for relative, content in sorted(files.items()):
                info = zipfile.ZipInfo(relative, date_time=(2020, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o100644 << 16
                archive.writestr(info, content)
        with zipfile.ZipFile(temporary_path) as archive:
            if archive.testzip() is not None:
                raise RuntimeError("ZIP verification failed")
            for relative, expected in manifest["files"].items():
                if sha256(archive.read(relative)) != expected:
                    raise RuntimeError("ZIP member hash mismatch: " + relative)
        temporary_path.replace(output)
    finally:
        temporary_path.unlink(missing_ok=True)
    print("Windows installer package: " + str(output))
    print("Files: %s; bytes: %s; SHA-256: %s" % (len(files), output.stat().st_size, sha256(output.read_bytes())))
    print("Cross-compiled only; target Windows installation performs the native self-test.")


if __name__ == "__main__":
    try:
        main()
    except (OSError, RuntimeError, KeyError, ValueError, subprocess.CalledProcessError, zipfile.BadZipFile) as error:
        print("Packaging failed: " + str(error), file=sys.stderr)
        sys.exit(1)
