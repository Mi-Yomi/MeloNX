#!/usr/bin/env python3
"""Run actual Metal-backed control/candidate imports in bounded separate processes."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

HOST_IMPORT_COMMIT = "c33ebc7e1ea491529477c795479ab414b335ab57"


def validate_report(report, control):
    expected = "expected_rejection" if control else "passed"
    if report.get("status") != expected:
        raise ValueError(f"Native {'control' if control else 'candidate'}: {report}")
    if report.get("host_create_result") != (-8 if control else 0):
        raise ValueError("Unexpected vkCreateBuffer result")
    if report.get("platform") != "macOS arm64" or not report.get("metal_device"):
        raise ValueError("Native test did not report a Metal device")
    if not control:
        if (report.get("bytes_each_direction", 0) <= 0 or report.get("binding_offset", 0) <= 0 or
                report.get("fence_submissions") != 2 or
                report.get("cpu_allocation_survived_native_destruction") is not True):
            raise ValueError("Incomplete imported-memory roundtrip/ownership test")


def run_probe(executable, dylib, expectation, directory, timeout=60):
    report_path = directory / "report.json"
    # Never accidentally accept a report from a previous attempt.
    if report_path.exists():
        raise ValueError(f"Native probe report already exists: {report_path}")
    with (directory / "stdout.log").open("wb") as stdout, (directory / "stderr.log").open("wb") as stderr:
        try:
            completed = subprocess.run([str(executable), str(dylib), expectation, str(report_path)],
                                       stdout=stdout, stderr=stderr, timeout=timeout, check=False)
            code = completed.returncode
        except subprocess.TimeoutExpired:
            code = "timeout"
    report = json.loads(report_path.read_text()) if report_path.exists() else {"status": "no_report"}
    return {"exit_code": code, "report": report}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output_root", type=Path)
    parser.add_argument("source_lock", type=Path)
    args = parser.parse_args()
    out = args.output_root.resolve()
    root = out / "host-import-regression"
    lock = json.loads(args.source_lock.read_text())
    commits = [patch["upstream_commit"] for patch in lock["patches"]]
    if not commits or commits[-1] != HOST_IMPORT_COMMIT:
        raise ValueError("The final locked patch must be the host import validation fix")
    result = {"schema": 1, "status": "failed", "platform": "macOS arm64",
              "source_commit": lock["source"]["revision"]}
    failure = None
    # Always preserve both reports, including explicit unavailable/failure results.
    for name, expectation, patches in (("control", "expect-rejected", commits[:-1]),
                                       ("candidate", "expect-roundtrip", commits)):
        directory = root / name
        dylib = directory / "libMoltenVK.dylib"
        entry = {"dylib": dylib.relative_to(out).as_posix(),
                 "sha256": hashlib.sha256(dylib.read_bytes()).hexdigest(),
                 "applied_upstream_commits": patches}
        entry.update(run_probe(root / "host_import_probe", dylib, expectation, directory))
        result[name] = entry
        try:
            if entry["exit_code"] != 0:
                raise ValueError(f"{name} exited {entry['exit_code']}: {entry['report']}")
            validate_report(entry["report"], name == "control")
        except ValueError as error:
            failure = str(error) if failure is None else failure + "; " + str(error)
    if failure:
        result["reason"] = failure
    else:
        if result["control"]["sha256"] == result["candidate"]["sha256"]:
            failure = "Control and candidate dylibs are identical"
            result["reason"] = failure
        else:
            result["status"] = "passed"
    (root / "result.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result, indent=2), flush=True)
    if failure:
        raise SystemExit("Native host import release gate failed: " + failure)


if __name__ == "__main__":
    main()
