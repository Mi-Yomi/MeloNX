#!/usr/bin/env python3
"""Validate the rebuilt driver's source identity and every packaged payload."""
import hashlib
import json
from pathlib import Path, PurePosixPath
import subprocess
import sys


def verify(folder, source_commit, expected_lock):
    folder = Path(folder)
    manifest = json.loads((folder / "build-manifest.json").read_text())
    lock = json.loads((folder / "source-lock.json").read_text())
    if lock != expected_lock:
        raise ValueError("Driver source lock differs from this checkout")
    if (manifest["schema"] != 1 or manifest["moltenvk_version"] != lock["version"]
            or manifest["moltenvk_source_commit"] != lock["source"]["revision"]
            or manifest["melonx_source_commit"] != source_commit
            or manifest["configuration_prefix_bytes"] != 152):
        raise ValueError("Driver source/ABI identity mismatch")
    variant = manifest["variant"]
    if variant not in ("patched", "baseline"):
        raise ValueError("Unknown driver variant")
    patches = [p["upstream_commit"] for p in lock["patches"]] if variant == "patched" else []
    if manifest["applied_upstream_commits"] != patches:
        raise ValueError("Driver backports differ from source lock")
    binary = manifest["binary"]
    if (binary["architecture"] != "arm64" or binary["platform"] != "iPhoneOS"
            or binary["install_name"] != "@rpath/libMoltenVK.dylib"):
        raise ValueError("Wrong driver architecture/platform/install name")
    files = manifest["files"]
    required = {"libMoltenVK.dylib", "libMoltenVK.dylib.dSYM.zip", "source-lock.json"}
    for repo in [lock["source"], *lock["dependencies"]]:
        required.add(f"source-repositories/{repo['name']}-{repo['revision']}.tar.gz")
        if not any(name.startswith(f"licenses/{repo['name']}/") for name in files):
            raise ValueError(f"Missing license: {repo['name']}")
    if not required <= files.keys():
        raise ValueError("Incomplete native artifact/source/symbol manifest")
    for name, digest in files.items():
        path = PurePosixPath(name)
        if path.is_absolute() or ".." in path.parts or "\\" in name or ":" in name:
            raise ValueError("Unsafe artifact manifest path")
        target = folder.joinpath(*path.parts)
        if not target.resolve().is_relative_to(folder.resolve()):
            raise ValueError("Artifact escapes output directory")
        if hashlib.sha256(target.read_bytes()).hexdigest() != digest:
            raise ValueError(f"Native artifact checksum mismatch: {name}")
    driver = folder / "libMoltenVK.dylib"
    if driver.stat().st_size != binary["bytes"] or files[driver.name] != binary["sha256"]:
        raise ValueError("Native binary size/hash mismatch")
    return manifest


def main():
    root = Path(__file__).resolve().parents[2]
    source = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    lock = json.loads((root / "distribution/ios/moltenvk-backports/source-lock.json").read_text())
    manifest = verify(sys.argv[1], source, lock)
    print(f"Verified MoltenVK {manifest['moltenvk_version']} {manifest['variant']} for {source}")


if __name__ == "__main__":
    main()
