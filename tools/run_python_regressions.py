"""Run all Python regressions and reject silent loss of required test coverage."""
import json
import argparse
from pathlib import Path
import unittest


def test_ids(suite):
    for item in suite:
        if isinstance(item, unittest.TestSuite):
            yield from test_ids(item)
        else:
            yield item.id()


def main():
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser()
    parser.add_argument("--required", default=str(root / "distribution/ios/v11-required-tests.json"))
    args = parser.parse_args()
    suite = unittest.defaultTestLoader.discover(str(root / "tools/tests"))
    native_tests = root / "distribution/ios/moltenvk-backports"
    suite.addTests(unittest.TestLoader().discover(str(native_tests), top_level_dir=str(native_tests)))
    discovered = set(test_ids(suite))
    required = set(json.loads(Path(args.required).read_text())["python"])
    missing = required - discovered
    if not required or missing:
        raise SystemExit("Required Python tests missing: " + ", ".join(sorted(missing)))
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    if not result.wasSuccessful() or result.skipped or result.testsRun != len(discovered):
        raise SystemExit("Python regression gate failed, skipped or incomplete")


if __name__ == "__main__":
    main()
