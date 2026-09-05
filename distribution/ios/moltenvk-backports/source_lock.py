#!/usr/bin/env python3
"""Fetch/verify exact official sources; archive source and original notices."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess


def require(condition, reason):
    if not condition:
        raise ValueError(reason)


def git(path, *args):
    return subprocess.check_output(["git", "-C", str(path), *args], text=True).strip()


def verify(source, lock, patch_dir):
    require(git(source, "rev-parse", "HEAD") == lock["source"]["revision"], "MoltenVK HEAD mismatch")
    pregen = source / "Templates/spirv-tools/build.zip"
    require(hashlib.sha256(pregen.read_bytes()).hexdigest() == lock["pregen_spirv_tools_headers_sha256"], "Pre-generated SPIRV headers mismatch")
    for patch in lock["patches"]:
        require(hashlib.sha256((patch_dir / patch["file"]).read_bytes()).hexdigest() == patch["sha256"], patch["file"])
    for dependency in lock["dependencies"]:
        revision_file = source / "ExternalRevisions" / (dependency["name"] + "_repo_revision")
        require(revision_file.read_text().strip() == dependency["revision"], str(revision_file))


def apply_patches(source, patches, patch_dir, reverse=False):
    completed = []
    try:
        for patch in reversed(patches) if reverse else patches:
            arguments = ["git", "-C", str(source), "apply"]
            if reverse:
                arguments.append("--reverse")
            path = str((patch_dir / patch["file"]).resolve())
            subprocess.run([*arguments, "--check", path], check=True)
            subprocess.run([*arguments, path], check=True)
            completed.append(path)
    except subprocess.CalledProcessError:
        # A failed later patch must not strand a half-applied patch stack.
        for path in reversed(completed):
            arguments = ["git", "-C", str(source), "apply"]
            if not reverse:
                arguments.append("--reverse")
            subprocess.run([*arguments, path], check=True)
        raise


def verify_patched_source(source, patches, patch_dir):
    apply_patches(source, patches, patch_dir, reverse=True)
    try:
        require(not git(source, "diff", "HEAD", "--"), "Source differs from baseline plus locked patches")
    finally:
        apply_patches(source, patches, patch_dir)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("--fetch", action="store_true")
    parser.add_argument("--archive", type=Path)
    parser.add_argument("--patches", choices=("apply", "reverse", "verify"))
    args = parser.parse_args()
    patch_dir = Path(__file__).resolve().parent
    lock = json.loads((patch_dir / "source-lock.json").read_text())
    source = args.source.resolve()
    verify(source, lock, patch_dir)
    if args.patches:
        if args.patches == "verify":
            verify_patched_source(source, lock["patches"], patch_dir)
        else:
            apply_patches(source, lock["patches"], patch_dir, reverse=args.patches == "reverse")
        return
    # Parent repositories must exist before their nested dependencies are fetched.
    dependencies = sorted(lock["dependencies"], key=lambda item: item["path"].count("/"))
    for dependency in dependencies:
        path = source / dependency["path"]
        if args.fetch and not (path / ".git").exists():
            path.mkdir(parents=True, exist_ok=True)
            subprocess.run(["git", "init", str(path)], check=True)
            git(path, "remote", "add", "origin", dependency["url"])
            git(path, "fetch", "--depth=1", "origin", dependency["revision"])
            git(path, "checkout", "--detach", "FETCH_HEAD")
        require(git(path, "rev-parse", "HEAD") == dependency["revision"], dependency["name"])
        require(not git(path, "diff", "HEAD", "--"), "Modified dependency: " + dependency["name"])
        print(dependency["name"], dependency["revision"], flush=True)

    if args.archive:
        args.archive.mkdir(parents=True, exist_ok=True)
        for repo, path in [(lock["source"], source)] + [(dep, source / dep["path"]) for dep in dependencies]:
            archive = (args.archive / (repo["name"] + "-" + repo["revision"] + ".tar.gz")).resolve()
            git(path, "archive", "--format=tar.gz", "--output=" + str(archive), repo["revision"])
            for entry in git(path, "ls-files").splitlines():
                name = Path(entry).name.upper()
                if name.startswith(("LICENSE", "COPYING", "NOTICE")):
                    target = args.archive.parent / "licenses" / repo["name"] / entry
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes((path / entry).read_bytes())


if __name__ == "__main__":
    main()
