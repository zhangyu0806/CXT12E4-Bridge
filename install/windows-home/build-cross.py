#!/usr/bin/env python3
"""Compile the Windows Bridge on macOS/Linux; never run or install it.

Requires Python 3 and an official .NET SDK (8.0.416 by default). The downloaded
Microsoft reference-assembly package is pinned and SHA-256 verified. Build
products and the package cache stay under the ignored outputs directory.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.request
import zipfile


ROOT = Path(__file__).resolve().parents[2]
PACKAGE_URL = (
    "https://api.nuget.org/v3-flatcontainer/"
    "microsoft.netframework.referenceassemblies.net48/1.0.3/"
    "microsoft.netframework.referenceassemblies.net48.1.0.3.nupkg"
)
PACKAGE_SHA256 = "8a7e348538e7eb91351696911689f49e3d4f63f8bab517432bbe159b8b1104a2"
REFERENCES = (
    "mscorlib.dll", "System.dll", "System.Core.dll",
    "System.Web.Extensions.dll", "System.Windows.Forms.dll", "System.Drawing.dll",
    "UIAutomationClient.dll", "UIAutomationTypes.dll", "WindowsBase.dll",
)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest() if hasattr(hashlib, "file_digest") else hashlib.sha256(stream.read()).hexdigest()


def reference_assemblies(cache):
    cache.mkdir(parents=True, exist_ok=True)
    package = cache / "microsoft.netframework.referenceassemblies.net48.1.0.3.nupkg"
    if not package.is_file():
        print("Downloading pinned Microsoft .NET Framework 4.8 reference assemblies...", flush=True)
        with tempfile.NamedTemporaryFile(dir=str(cache), delete=False) as temporary:
            temporary_path = Path(temporary.name)
            try:
                with urllib.request.urlopen(PACKAGE_URL, timeout=60) as response:
                    shutil.copyfileobj(response, temporary)
                temporary.flush()
                if digest(temporary_path) != PACKAGE_SHA256:
                    raise RuntimeError("Reference package SHA-256 mismatch; refusing to compile")
                temporary_path.replace(package)
            finally:
                temporary_path.unlink(missing_ok=True)
    if digest(package) != PACKAGE_SHA256:
        raise RuntimeError("Cached reference package SHA-256 mismatch; remove the cache and retry")
    references = cache / "net48-references"
    references.mkdir(exist_ok=True)
    # Extract only named DLLs from the verified archive; no arbitrary zip paths.
    with zipfile.ZipFile(package) as archive:
        for name in REFERENCES:
            (references / name).write_bytes(archive.read("build/.NETFramework/v4.8/" + name))
    return references


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default=os.environ.get("DOTNET_HOST_PATH") or shutil.which("dotnet"), help="Path to the dotnet host; no global installation required")
    parser.add_argument("--sdk-version", default="8.0.416")
    parser.add_argument("--output-dir", type=Path, default=ROOT / "outputs" / "windows-cross")
    parser.add_argument("--cache-dir", type=Path, default=ROOT / "outputs" / ".windows-build-cache")
    parser.add_argument("--windowed", action="store_true", help="Build Windows GUI subsystem instead of a console executable")
    args = parser.parse_args()
    if not args.dotnet:
        parser.error("dotnet not found; install the official .NET SDK in a temporary directory and pass --dotnet")
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1", DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1")
    available = subprocess.check_output([args.dotnet, "--list-sdks"], text=True, env=env)
    sdk = None
    for line in available.splitlines():
        match = re.fullmatch(r"([^ ]+) \[(.+)\]", line.strip())
        if match and match.group(1) == args.sdk_version:
            sdk = Path(match.group(2)) / args.sdk_version
    if sdk is None:
        parser.error("Required SDK %s is not installed. Available SDKs:\n%s" % (args.sdk_version, available))
    compiler = sdk / "Roslyn" / "bincore" / "csc.dll"
    if not compiler.is_file():
        raise RuntimeError("Roslyn compiler is missing: " + str(compiler))
    references = reference_assemblies(args.cache_dir.resolve())
    sources = sorted((ROOT / "src" / "Cxt1024Bridge").glob("*.cs"))
    snapshots = {source.relative_to(ROOT).as_posix(): source.read_bytes() for source in sources}
    output = args.output_dir.resolve()
    output.mkdir(parents=True, exist_ok=True)
    executable = output / "Cxt12E4Bridge.exe"
    with tempfile.TemporaryDirectory(prefix="cxt-windows-build-", dir=str(output)) as temporary:
        temporary_executable = Path(temporary) / executable.name
        snapshot_directory = Path(temporary) / "src"
        snapshot_directory.mkdir()
        snapshot_sources = []
        for relative, content in snapshots.items():
            snapshot = snapshot_directory / Path(relative).name
            snapshot.write_bytes(content)
            snapshot_sources.append(snapshot)
        command = [args.dotnet, str(compiler), "/nologo", "/noconfig", "/nostdlib+", "/langversion:5", "/target:" + ("winexe" if args.windowed else "exe"), "/platform:anycpu", "/optimize+", "/warn:4", "/deterministic+", "/out:" + str(temporary_executable)]
        command.append("/pathmap:" + str(snapshot_directory) + "=/src/Cxt1024Bridge")
        command += ["/reference:" + str(references / name) for name in REFERENCES]
        command += [str(source) for source in snapshot_sources]
        subprocess.run(command, env=env, check=True, cwd=str(ROOT))
        temporary_executable.replace(executable)
    shutil.copyfile(executable, output / "Cxt1024Bridge.exe")
    manifest = {
        "purpose": "Cross-compilation only; Windows runtime and hardware behavior are not tested",
        "framework": ".NET Framework 4.8",
        "languageVersion": "C# 5",
        "sdkVersion": args.sdk_version,
        "compilerSha256": digest(compiler),
        "referencePackageUrl": PACKAGE_URL,
        "referencePackageSha256": PACKAGE_SHA256,
        "target": "winexe" if args.windowed else "exe",
        "executableSha256": digest(executable),
        "sources": {relative: hashlib.sha256(content).hexdigest() for relative, content in snapshots.items()},
    }
    (output / "cross-build-manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("Windows PE built: " + str(executable))
    print("SHA-256: " + manifest["executableSha256"])
    print("Not executed on this host. Run the native Windows --self-test before installation.")


if __name__ == "__main__":
    try:
        main()
    except subprocess.CalledProcessError as error:
        print("Build tool exited with code %s; see diagnostics above." % error.returncode, file=sys.stderr)
        sys.exit(1)
    except (OSError, RuntimeError, zipfile.BadZipFile) as error:
        print("Build failed: " + str(error), file=sys.stderr)
        sys.exit(1)
