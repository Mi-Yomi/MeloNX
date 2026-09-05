import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("verify_test_results", Path(__file__).resolve().parents[1] / "verify_test_results.py")
verify_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verify_module)


class ReleaseGateTests(unittest.TestCase):
    def test_missing_suite_cannot_pass_with_one_success(self):
        with self.assertRaises(ValueError):
            verify_module.verify({"A": "Passed"}, {"A": ["Passed"], "B": ["Passed"]})

    def test_only_explicit_historical_not_run_is_allowed(self):
        required = {"A": ["Passed"], "B": ["Passed", "NotExecuted"]}
        result = verify_module.verify({"A": "Passed", "B": "NotExecuted"}, required)
        self.assertEqual(result["not_run"], ["B"])
        with self.assertRaises(ValueError):
            verify_module.verify({"A": "NotExecuted", "B": "Passed"}, required)

    def test_new_failures_or_skips_cannot_hide_outside_required_set(self):
        for outcome in ("Failed", "NotExecuted", "Skipped", "Inconclusive"):
            with self.assertRaises(ValueError):
                verify_module.verify({"A": "Passed", "new": outcome}, {"A": ["Passed"]})

    def test_random_values_may_change_but_all_cases_must_execute(self):
        cases = {"A.Random(42)": "Passed", "A.Random(197)": "Passed"}
        self.assertEqual(verify_module.verify(cases, {}, {"A.Random": 2})["total"], 2)
        with self.assertRaises(ValueError):
            verify_module.verify({"A.Random(42)": "Passed"}, {}, {"A.Random": 2})
        with self.assertRaises(ValueError):
            verify_module.verify({"A.Other(42)": "Passed", "A.Other(197)": "Passed"}, {}, {"A.Random": 2})


if __name__ == "__main__":
    unittest.main()
