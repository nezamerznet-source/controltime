#!/usr/bin/env python3
"""Build, test and package Family Time. Python standard library only."""
from __future__ import annotations
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / "artifacts"
APP = ARTIFACTS / "app"
NATIVE = ARTIFACTS / "native"
VERSION = ET.parse(ROOT / "Directory.Build.props").findtext(".//Version")


def run(command: list[str], cwd: Path = ROOT) -> None:
    print("Running:", " ".join(command), flush=True)
    subprocess.run(command, cwd=cwd, check=True)


def tool(name: str, variable: str) -> str:
    value = os.environ.get(variable) or shutil.which(name)
    if name == "makensis" and not value and os.name == "nt":
        candidate = Path(os.environ.get("ProgramFiles(x86)", "C:/Program Files (x86)")) / "NSIS/makensis.exe"
        if candidate.is_file(): value = str(candidate)
    if not value: raise RuntimeError(f"Install {name}, or set {variable} to its executable.")
    return value


def sha(path: Path) -> str:
    with path.open("rb") as stream: return hashlib.file_digest(stream, "sha256").hexdigest()


def archive(destination: Path, files: list[Path], base: Path, prefix: str = "") -> None:
    with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as target:
        for file in sorted(files):
            if file.is_symlink(): raise RuntimeError(f"Refusing to package symlink: {file}")
            target.write(file, prefix + file.relative_to(base).as_posix())


def package() -> None:
    for file in [APP / "FamilyTime.exe", APP / "FamilyTime.dll", NATIVE / "FamilyTime.NativeHost.exe"]:
        if not file.is_file(): raise RuntimeError(f"Publish output missing: {file}")
    for path in [APP / "FamilyTime.runtimeconfig.json", NATIVE / "FamilyTime.NativeHost.runtimeconfig.json"]:
        frameworks = json.loads(path.read_text("utf-8"))["runtimeOptions"]["includedFrameworks"]
        if any(item["version"] != "10.0.12" for item in frameworks): raise RuntimeError("Mismatched bundled framework versions")
    # WindowsDesktop replaces these NETCore forwarding facades with its own implementations.
    desktop_facades = {"System.Drawing.dll", "Microsoft.VisualBasic.dll", "WindowsBase.dll"}
    for file in NATIVE.rglob("*"):
        if not file.is_file() or file.suffix.lower() == ".pdb": continue
        relative = file.relative_to(NATIVE)
        target = APP / relative
        if file.name.startswith("FamilyTime.NativeHost."):
            target.parent.mkdir(parents=True, exist_ok=True); shutil.copy2(file, target)
        elif not target.exists(): raise RuntimeError(f"Shared runtime dependency missing from Windows bundle: {relative}")
        elif file.name not in desktop_facades and sha(file) != sha(target): raise RuntimeError(f"Windows and native host must use identical shared dependencies: {relative}")

    for debug_symbols in APP.rglob("*.pdb"): debug_symbols.unlink()
    manifest = json.loads((ROOT / "extension/manifest.json").read_text("utf-8"))
    digest = hashlib.sha256(base64.b64decode(manifest["key"])).hexdigest()[:32]
    extension_id = "".join(chr(ord("a") + int(char, 16)) for char in digest)
    if extension_id not in (ROOT / "src/FamilyTime.Core/ExtensionIdentity.cs").read_text("utf-8"):
        raise RuntimeError("Extension public key and native host allowed origin do not match")

    for directory in ["extension", "docs"]:
        target = APP / directory
        if target.exists(): shutil.rmtree(target)
        shutil.copytree(ROOT / directory, target, ignore=shutil.ignore_patterns("tests", "package.json", "identity.json", "specification.md"))
    for name in ["branding.json", "README.md", "CHANGELOG.md", "LICENSE-NOT-SELECTED.md", "THIRD-PARTY-NOTICES.md"]:
        shutil.copy2(ROOT / name, APP / name)

    licenses = APP / "licenses"; licenses.mkdir(exist_ok=True)
    nuget = Path(os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages")))
    for package_name in ["microsoft.netcore.app.runtime.win-x64", "microsoft.windowsdesktop.app.runtime.win-x64"]:
        package_path = nuget / package_name / "10.0.12"
        license_files = [p for p in package_path.iterdir() if p.is_file() and ("license" in p.name.lower() or "third-party" in p.name.lower())]
        if not license_files: raise RuntimeError(f"Runtime license missing in {package_path}")
        for file in license_files: shutil.copy2(file, licenses / (package_name + "-" + file.name))
    nsis_license = ROOT / "packaging/NSIS-COPYRIGHT.txt"
    if nsis_license.exists(): shutil.copy2(nsis_license, licenses / nsis_license.name)

    delete_list = ARTIFACTS / "uninstall-files.nsh"
    # Delete only files owned by this build, never arbitrary contents of the chosen directory.
    def quoted_relative(file: Path) -> str:
        return str(file.relative_to(APP)).replace("/", "\\").replace("$", "$$").replace('"', '$\\"')
    lines = ['Delete "$INSTDIR\\' + quoted_relative(p) + '"' for p in APP.rglob("*") if p.is_file()]
    folders = sorted((p for p in APP.rglob("*") if p.is_dir()), key=lambda p: len(p.parts), reverse=True)
    lines += ['RMDir "$INSTDIR\\' + quoted_relative(p) + '"' for p in folders]
    delete_list.write_text("\n".join(lines) + "\n", encoding="utf-8")
    installer = ARTIFACTS / f"FamilyTime-{VERSION}-Setup.exe"
    makensis = tool("makensis", "FAMILYTIME_MAKENSIS")
    flag = "/" if os.name == "nt" else "-"
    run([makensis, flag + "V2", flag + "DPAYLOAD=" + str(APP), flag + "DOUTPUT=" + str(installer),
         flag + "DDELETE_LIST=" + str(delete_list), flag + "DAPP_VERSION=" + VERSION, str(ROOT / "packaging/installer.nsi")])

    extension_zip = ARTIFACTS / f"FamilyTime-{VERSION}-Extension.zip"
    archive(extension_zip, [p for p in (APP / "extension").rglob("*") if p.is_file()], APP / "extension")
    source_files: list[Path] = []
    for directory in ["src", "tests", "extension", "packaging", "docs", ".github"]:
        source_files += [p for p in (ROOT / directory).rglob("*") if p.is_file() and not any(x in {"bin", "obj", "__pycache__"} for x in p.relative_to(ROOT).parts)]
    source_files += [p for p in ROOT.iterdir() if p.is_file() and (p.suffix in {".md", ".json", ".props", ".slnx"} or p.name in {".gitignore", ".gitattributes", ".editorconfig"})]
    source_zip = ARTIFACTS / f"FamilyTime-{VERSION}-Source.zip"
    archive(source_zip, source_files, ROOT, "family-time/")
    checksums = ARTIFACTS / "SHA256SUMS.txt"
    checksums.write_text("".join(f"{sha(p)}  {p.name}\n" for p in [installer, extension_zip, source_zip]), encoding="utf-8")
    print("\nCreated:")
    for file in [installer, extension_zip, source_zip, checksums]: print(f"  {file} ({file.stat().st_size:,} bytes)")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package-only", action="store_true", help="Package previously published app/native outputs")
    args = parser.parse_args()
    ARTIFACTS.mkdir(exist_ok=True)
    if not args.package_only:
        dotnet = tool("dotnet", "FAMILYTIME_DOTNET"); node = tool("node", "FAMILYTIME_NODE")
        run([dotnet, "run", "--project", "tests/FamilyTime.Tests/FamilyTime.Tests.csproj", "-c", "Release"])
        run([node, "--test"] + [str(p) for p in sorted((ROOT / "extension/tests").glob("*.test.js"))])
        for project, target in [("FamilyTime.Windows", APP), ("FamilyTime.NativeHost", NATIVE)]:
            if target.exists(): shutil.rmtree(target)
            run([dotnet, "publish", f"src/{project}/{project}.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "true",
                 "-p:DebugType=None", "-p:DebugSymbols=false", "-o", str(target)])
    package()


if __name__ == "__main__":
    try: main()
    except (RuntimeError, subprocess.CalledProcessError) as error:
        print(str(error), file=sys.stderr); sys.exit(1)
