import hashlib
from pathlib import Path
import subprocess
import tempfile
import unittest

from source_lock import apply_patches, git, verify, verify_patched_source


class SourcePatchLockTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.source = Path(self.temporary.name) / "source"
        self.source.mkdir()
        self.patches = self.source.parent / "patches"
        self.patches.mkdir()
        git(self.source, "init", "--quiet")
        git(self.source, "config", "core.autocrlf", "false")
        self.content = self.source / "content.txt"
        self.content.write_bytes(b"baseline\n")
        self.extra = self.source / "extra.txt"
        self.extra.write_bytes(b"unmodified\n")
        pregen = self.source / "Templates/spirv-tools/build.zip"
        pregen.parent.mkdir(parents=True)
        pregen.write_bytes(b"pinned header fixture")
        git(self.source, "add", ".")
        git(self.source, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "commit", "--quiet", "-m", "baseline")
        self.content.write_bytes(b"first patch\n")
        self.first = self.write_patch("first.patch", git(self.source, "diff", "HEAD") + "\n")
        # The second patch requires the first patch's output; reversal order matters.
        git(self.source, "add", "content.txt")
        self.content.write_bytes(b"second patch\n")
        self.second = self.write_patch("second.patch", git(self.source, "diff") + "\n")
        git(self.source, "reset", "--quiet", "HEAD", "--", "content.txt")
        self.content.write_bytes(b"baseline\n")
        self.stack = [self.first, self.second]
        self.lock = {"source": {"revision": git(self.source, "rev-parse", "HEAD")},
                     "patches": self.stack, "dependencies": [],
                     "pregen_spirv_tools_headers_sha256": hashlib.sha256(pregen.read_bytes()).hexdigest()}

    def write_patch(self, name, content):
        path = self.patches / name
        path.write_bytes(content.encode())
        return {"file": name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}

    def test_dependent_patches_apply_reverse_and_verify_in_order(self):
        apply_patches(self.source, self.stack, self.patches)
        self.assertEqual(self.content.read_text(), "second patch\n")
        verify_patched_source(self.source, self.stack, self.patches)
        self.assertEqual(self.content.read_text(), "second patch\n")
        apply_patches(self.source, self.stack, self.patches, reverse=True)
        self.assertFalse(git(self.source, "diff", "HEAD", "--"))

    def test_whole_tree_validation_detects_extra_edits_and_restores_patches(self):
        apply_patches(self.source, self.stack, self.patches)
        self.extra.write_bytes(b"unapproved extra edit\n")
        with self.assertRaisesRegex(ValueError, "Source differs"):
            verify_patched_source(self.source, self.stack, self.patches)
        self.assertEqual(self.content.read_text(), "second patch\n")
        self.assertEqual(self.extra.read_text(), "unapproved extra edit\n")

    def test_failed_later_patch_rolls_back_completed_prefix(self):
        # Applying first twice must fail; the completed first application is undone.
        with self.assertRaises(subprocess.CalledProcessError):
            apply_patches(self.source, [self.first, self.first], self.patches)
        self.assertFalse(git(self.source, "diff", "HEAD", "--"))

    def test_locked_patch_hash_rejects_tampering(self):
        verify(self.source, self.lock, self.patches)
        (self.patches / self.second["file"]).write_bytes(b"replacement patch\n")
        with self.assertRaisesRegex(ValueError, "second.patch"):
            verify(self.source, self.lock, self.patches)


if __name__ == "__main__":
    unittest.main()
