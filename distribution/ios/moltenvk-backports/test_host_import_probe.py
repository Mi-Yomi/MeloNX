import copy
from pathlib import Path
import sys
import tempfile
import unittest

from run_host_import_probe import run_probe, validate_report


class HostImportProbeTests(unittest.TestCase):
    def setUp(self):
        self.candidate = {"status": "passed", "host_create_result": 0,
                          "platform": "macOS arm64", "metal_device": "fixture",
                          "bytes_each_direction": 16384, "binding_offset": 16384,
                          "fence_submissions": 2, "cpu_allocation_survived_native_destruction": True}

    def test_accepts_reproduced_control_and_complete_roundtrip(self):
        control = {**self.candidate, "status": "expected_rejection", "host_create_result": -8}
        validate_report(control, True)
        validate_report(self.candidate, False)

    def test_unavailable_or_incomplete_gpu_validation_is_not_success(self):
        for key, value in (("status", "unavailable"), ("host_create_result", -8),
                           ("metal_device", ""), ("bytes_each_direction", 0), ("binding_offset", 0),
                           ("fence_submissions", 1), ("cpu_allocation_survived_native_destruction", False)):
            with self.subTest(key=key):
                report = copy.copy(self.candidate)
                report[key] = value
                with self.assertRaises(ValueError):
                    validate_report(report, False)

    def test_actual_process_exit_and_missing_report_are_recorded(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            fake_probe = directory / "probe.py"
            fake_probe.write_text("import sys\nsys.exit(77)\n")
            result = run_probe(Path(sys.executable), fake_probe, "expect-roundtrip", directory)
            self.assertEqual(result, {"exit_code": 77, "report": {"status": "no_report"}})

    def test_actual_process_timeout_is_bounded_and_stale_report_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            fake_probe = directory / "probe.py"
            fake_probe.write_text("import time\ntime.sleep(30)\n")
            result = run_probe(Path(sys.executable), fake_probe, "expect-roundtrip", directory, timeout=0.1)
            self.assertEqual(result["exit_code"], "timeout")
            (directory / "report.json").write_text('{"status":"passed"}')
            with self.assertRaises(ValueError):
                run_probe(Path(sys.executable), fake_probe, "expect-roundtrip", directory, timeout=0.1)


if __name__ == "__main__":
    unittest.main()
