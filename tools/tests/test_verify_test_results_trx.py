"""TRX gate tests use synthetic adapter output, never user diagnostics."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location(
    "verify_test_results_trx_subject", Path(__file__).resolve().parents[1] / "verify_test_results.py"
)
subject = importlib.util.module_from_spec(spec)
spec.loader.exec_module(subject)

NS = subject.NS["t"]


def element(name, **attributes):
    return ET.Element(f"{{{NS}}}{name}", attributes)


def trx(outcomes=("Passed",), summary_outcome="Completed", counter_overrides=None):
    root = element("TestRun")
    definitions, results = element("TestDefinitions"), element("Results")
    root.extend((definitions, results))
    for index, outcome in enumerate(outcomes):
        test_id = str(index)
        test = element("UnitTest", id=test_id)
        test.append(element("TestMethod", className="Synthetic.Suite"))
        definitions.append(test)
        results.append(element("UnitTestResult", testId=test_id, testName=f"Test{index}", outcome=outcome))
    summary = element("ResultSummary", outcome=summary_outcome)
    counters = dict(total=str(len(outcomes)), passed=str(outcomes.count("Passed")),
                    failed="0", error="0", timeout="0", aborted="0", notExecuted="0")
    counters.update(counter_overrides or {})
    summary.append(element("Counters", **counters))
    root.append(summary)
    return root


class TrxReleaseGateTests(unittest.TestCase):
    def read(self, document):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "synthetic.trx"
            ET.ElementTree(document).write(path, encoding="utf-8", xml_declaration=True)
            return subject.read_trx(path)

    def test_reports_historical_not_executed_from_records_despite_zero_summary_counter(self):
        results = self.read(trx(("Passed", "NotExecuted")))
        verified = subject.verify(results, {
            "Synthetic.Suite.Test0": ["Passed"],
            "Synthetic.Suite.Test1": ["Passed", "NotExecuted"],
        })
        self.assertEqual(verified["outcomes"], {"Passed": 1, "NotExecuted": 1})
        self.assertEqual(verified["not_run"], ["Synthetic.Suite.Test1"])

    def test_missing_or_empty_result_records_fail(self):
        for empty in (False, True):
            with self.subTest(empty=empty):
                root = trx(()) if empty else trx()
                if not empty:
                    root.remove(root.find("t:Results", subject.NS))
                with self.assertRaises(ValueError):
                    self.read(root)

    def test_missing_counters_fail(self):
        root = trx()
        summary = root.find("t:ResultSummary", subject.NS)
        summary.remove(summary.find("t:Counters", subject.NS))
        with self.assertRaises(ValueError):
            self.read(root)

    def test_total_mismatch_fails(self):
        with self.assertRaises(ValueError):
            self.read(trx(counter_overrides={"total": "2"}))

    def test_failure_counters_fail_even_if_result_records_pass(self):
        for field in ("failed", "error", "timeout", "aborted"):
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.read(trx(counter_overrides={field: "1"}))

    def test_duplicate_result_fails(self):
        root = trx(counter_overrides={"total": "2"})
        results = root.find("t:Results", subject.NS)
        results.append(element("UnitTestResult", testId="0", testName="Test0", outcome="Passed"))
        with self.assertRaises(ValueError):
            self.read(root)

    def test_result_without_definition_fails(self):
        root = trx()
        root.find("t:TestDefinitions", subject.NS).clear()
        with self.assertRaises((ValueError, KeyError)):
            self.read(root)

    def test_nonpassing_records_cannot_hide_behind_success_counters(self):
        for outcome in ("Failed", "Error", "Timeout", "Aborted", "Inconclusive", "NotExecuted"):
            with self.subTest(outcome=outcome), self.assertRaises(ValueError):
                subject.verify(self.read(trx((outcome,))), {"Synthetic.Suite.Test0": ["Passed"]})

    def test_aborted_summary_cannot_pass_with_completed_test_records(self):
        for outcome in ("Aborted", "Error", "Failed", "InProgress"):
            with self.subTest(outcome=outcome), self.assertRaises(ValueError):
                self.read(trx(summary_outcome=outcome))

    def test_run_level_error_cannot_hide_behind_passing_records(self):
        root = trx()
        infos = element("RunInfos")
        info = element("RunInfo", outcome="Error")
        text = element("Text")
        text.text = "Synthetic adapter process failed after returning results."
        info.append(text)
        infos.append(info)
        root.find("t:ResultSummary", subject.NS).append(infos)
        with self.assertRaises(ValueError):
            self.read(root)


if __name__ == "__main__":
    unittest.main()
