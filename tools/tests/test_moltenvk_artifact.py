import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("mvk_artifact", ROOT / "distribution/ios/verify-moltenvk-artifact.py")
mvk = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mvk)


class MoltenVKArtifactTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name)
        self.lock = json.loads((ROOT / "distribution/ios/moltenvk-backports/source-lock.json").read_text())
        payloads = {"libMoltenVK.dylib": b"fixture binary", "libMoltenVK.dylib.dSYM.zip": b"fixture symbols",
                    "source-lock.json": json.dumps(self.lock).encode()}
        commits = [p["upstream_commit"] for p in self.lock["patches"]]
        result = dict(schema=1, status="passed", platform="macOS arm64", source_commit=self.lock["source"]["revision"])
        for name in ("control", "candidate"):
            control = name == "control"
            dylib = f"host-import-regression/{name}/libMoltenVK.dylib"
            payloads[dylib] = f"fixture {name} dylib".encode()
            report = dict(schema=1, status="expected_rejection" if control else "passed",
                platform="macOS arm64", metal_device="fixture Metal device", host_create_result=-8 if control else 0)
            if not control:
                report.update(bytes_each_direction=16384, binding_offset=16384, fence_submissions=2,
                    cpu_allocation_survived_native_destruction=True)
            result[name] = dict(dylib=dylib, sha256=hashlib.sha256(payloads[dylib]).hexdigest(), exit_code=0,
                applied_upstream_commits=[c for c in commits if c != mvk.HOST_IMPORT_FIX] if control else commits,
                report=report)
        payloads["host-import-regression/result.json"] = json.dumps(result).encode()
        for repo in [self.lock["source"], *self.lock["dependencies"]]:
            payloads[f"source-repositories/{repo['name']}-{repo['revision']}.tar.gz"] = b"fixture source"
            payloads[f"licenses/{repo['name']}/LICENSE"] = b"fixture license"
        files = {}
        for name, value in payloads.items():
            path = self.folder / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(value)
            files[name] = hashlib.sha256(value).hexdigest()
        self.manifest = dict(schema=1, variant="patched", moltenvk_version="1.4.0",
            moltenvk_source_commit=self.lock["source"]["revision"], melonx_source_commit="app-sha",
            configuration_prefix_bytes=152, applied_upstream_commits=[p["upstream_commit"] for p in self.lock["patches"]],
            binary=dict(architecture="arm64", platform="iPhoneOS", install_name="@rpath/libMoltenVK.dylib",
                        bytes=len(payloads["libMoltenVK.dylib"]), sha256=files["libMoltenVK.dylib"]), files=files)
        self.manifest["host_import_regression"] = dict(status="passed", report="host-import-regression/result.json")

    def verify(self):
        (self.folder / "build-manifest.json").write_text(json.dumps(self.manifest))
        return mvk.verify(self.folder, "app-sha", self.lock)

    def test_complete_source_locked_artifact_is_accepted(self):
        self.assertEqual(self.verify()["variant"], "patched")

    def test_corrupted_driver_is_rejected(self):
        (self.folder / "libMoltenVK.dylib").write_bytes(b"wrong")
        with self.assertRaisesRegex(ValueError, "checksum"):
            self.verify()

    def test_incomplete_sources_or_license_are_rejected(self):
        original = copy.deepcopy(self.manifest)
        for prefix in ("source-repositories/", "licenses/"):
            with self.subTest(prefix=prefix):
                self.manifest = copy.deepcopy(original)
                key = next(k for k in self.manifest["files"] if k.startswith(prefix))
                del self.manifest["files"][key]
                with self.assertRaises(ValueError):
                    self.verify()

    def test_wrong_commit_abi_platform_or_patch_is_rejected(self):
        original = copy.deepcopy(self.manifest)
        for key, value in (("melonx_source_commit", "stale"), ("configuration_prefix_bytes", 144),
                           ("applied_upstream_commits", []), ("variant", "unknown")):
            with self.subTest(key=key):
                self.manifest = copy.deepcopy(original)
                self.manifest[key] = value
                with self.assertRaises(ValueError):
                    self.verify()
        self.manifest = copy.deepcopy(original)
        self.manifest["binary"]["platform"] = "iPhoneSimulator"
        with self.assertRaises(ValueError):
            self.verify()

    def test_parent_escape_is_rejected(self):
        self.manifest["files"]["../outside"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "Unsafe"):
            self.verify()

    def test_missing_or_skipped_native_import_evidence_is_rejected(self):
        for evidence in ({}, {"status": "unavailable"}, {"status": "passed", "report": "stale.json"}):
            with self.subTest(evidence=evidence):
                self.manifest["host_import_regression"] = evidence
                with self.assertRaisesRegex(ValueError, "host-import"):
                    self.verify()

    def test_native_execution_and_source_claims_must_match_hashed_artifacts(self):
        path = self.folder / "host-import-regression/result.json"
        original = json.loads(path.read_text())
        mutations = (
            lambda r: r.update(source_commit="wrong"),
            lambda r: r["control"].update(exit_code=77),
            lambda r: r["control"]["report"].update(host_create_result=0),
            lambda r: r["candidate"].update(applied_upstream_commits=[]),
            lambda r: r["candidate"].update(sha256="0" * 64),
            lambda r: r["candidate"]["report"].update(fence_submissions=1),
            lambda r: r["candidate"]["report"].update(binding_offset=0),
            lambda r: r["candidate"]["report"].update(bytes_each_direction=0),
            lambda r: r["candidate"]["report"].update(cpu_allocation_survived_native_destruction=False),
            lambda r: r["candidate"]["report"].update(metal_device=""),
        )
        for index, mutate in enumerate(mutations):
            with self.subTest(index=index):
                result = copy.deepcopy(original)
                mutate(result)
                path.write_text(json.dumps(result))
                self.manifest["files"]["host-import-regression/result.json"] = hashlib.sha256(path.read_bytes()).hexdigest()
                with self.assertRaises(ValueError):
                    self.verify()


if __name__ == "__main__":
    unittest.main()
