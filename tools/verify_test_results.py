"""Fail closed on missing regressions; report actual TRX outcomes, not just counters."""
import argparse
from collections import Counter
import json
from pathlib import Path
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def read_trx(path):
    root = ET.parse(path).getroot()
    summary = root.find("t:ResultSummary", NS)
    if summary is None or summary.attrib.get("outcome") not in ("Completed", "Passed"):
        raise ValueError("Test run did not complete successfully")
    for info in summary.findall(".//t:RunInfo", NS):
        if info.attrib.get("outcome") in ("Error", "Failed", "Aborted", "Timeout"):
            raise ValueError("Test run reports an infrastructure error")
    definitions = {}
    for test in root.findall(".//t:TestDefinitions/t:UnitTest", NS):
        method = test.find("t:TestMethod", NS)
        definitions[test.attrib["id"]] = method.attrib["className"]
    results = {}
    for test in root.findall(".//t:Results/t:UnitTestResult", NS):
        key = definitions[test.attrib["testId"]] + "." + test.attrib["testName"]
        if key in results:
            raise ValueError(f"Duplicate result: {key}")
        results[key] = test.attrib["outcome"]
    counters = root.find(".//t:ResultSummary/t:Counters", NS)
    if counters is None or not results:
        raise ValueError("Missing results/counters")
    if len(results) != int(counters.attrib["total"]):
        raise ValueError("TRX total does not match result records")
    for field in ("failed", "error", "timeout", "aborted"):
        if int(counters.attrib.get(field, 0)):
            raise ValueError(f"TRX has {field}: {counters.attrib[field]}")
    return results


def verify(results, required, case_groups=None):
    missing = set(required) - set(results)
    if missing:
        raise ValueError("Required tests missing: " + ", ".join(sorted(missing)))
    # NUnit Random data intentionally changes between runs. Preserve the method
    # and complete case count, rather than pinning yesterday's random numbers.
    for method, count in (case_groups or {}).items():
        cases = [name for name in results if name.startswith(method + "(")]
        if len(cases) != count:
            raise ValueError(f"Incomplete randomized cases: {method}: {len(cases)} != {count}")
    for name, outcome in results.items():
        allowed = required.get(name, ["Passed"])
        if outcome not in allowed:
            raise ValueError(f"Unexpected outcome: {name}: {outcome}; expected {allowed}")
    return {"outcomes": dict(Counter(results.values())), "total": len(results),
            "not_run": sorted(name for name, outcome in results.items() if outcome == "NotExecuted")}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--required", type=Path, required=True)
    parser.add_argument("--targeted", type=Path, required=True)
    parser.add_argument("--memory", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    required = json.loads(args.required.read_text())
    summaries = {suite: verify(read_trx(getattr(args, suite)), required[suite], required.get(suite + "_case_groups"))
                 for suite in ("targeted", "memory")}
    args.out.write_text(json.dumps(summaries, indent=2) + "\n")
    print(json.dumps(summaries, indent=2))


if __name__ == "__main__":
    main()
