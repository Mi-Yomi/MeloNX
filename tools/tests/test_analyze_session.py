"""Synthetic diagnostic records only. Run: python -m unittest discover -s tools/tests -v."""

import csv
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


SPEC = importlib.util.spec_from_file_location("analyze_session", Path(__file__).parents[1] / "analyze_session.py")
analyzer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(analyzer)


class SessionAnalysisTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.session = {"source_commit": "a" * 40, "time_utc": "2026-09-05T06:00:00Z",
            "schema_version": 5, "sample_interval_seconds": 2, "resolution_scale": 0.5}

    def memory(self, records, name="memory.jsonl", tail=""):
        path = self.root / name
        path.write_text("".join(json.dumps(record) + "\n" for record in records) + tail, encoding="utf-8")
        return path

    def sample(self, seconds, **metrics):
        return dict(event="sample", elapsed_seconds=seconds, **metrics)

    def core(self, records):
        path = self.root / "core.log"
        path.write_text("\n".join(records) + "\n", encoding="utf-8")
        return path

    def line(self, second, payload, prefix="GPU memory owners v1", elapsed=None, pid=100):
        return f"2026-09-05 11:00:{second:06.3f} MeloNX[{pid}:42] 00:00:{(second if elapsed is None else elapsed):06.3f} |I| GPU.MainThread Gpu AdvanceSequence: {prefix}: {payload}"

    def analyze(self, records=(), core_records=None, **options):
        return analyzer.analyze(self.session, [self.memory(records)], self.core(core_records) if core_records else None, **options)

    def test_rotation_deduplicates_only_equal_records_and_tolerates_truncated_tail(self):
        first = self.memory([self.sample(0, phys_footprint_bytes=10), self.sample(2, phys_footprint_bytes=20)])
        second = self.memory([self.sample(2, phys_footprint_bytes=20), self.sample(4, phys_footprint_bytes=40)], "rotated.jsonl", '{"event":')
        report = analyzer.analyze(self.session, [second, first])
        self.assertEqual(3, report["summary"]["memory_samples"])
        self.assertEqual(1, report["quality"]["duplicate_memory_records"])
        self.assertTrue(report["quality"]["invalid_memory_lines"][0]["final_line"])
        self.assertEqual([0, 2, 4], [r["session_seconds"] for r in report["records"]])

    def test_invalid_middle_record_is_visible_not_a_truncated_tail(self):
        path = self.memory([], tail='{"event":"sample","elapsed_seconds":0}\nnot json\n{"event":"sample","elapsed_seconds":2}\n')
        report = analyzer.analyze(self.session, [path])
        self.assertFalse(report["quality"]["invalid_memory_lines"][0]["final_line"])
        self.assertEqual(2, report["summary"]["memory_samples"])

    def test_missing_metrics_and_unavailable_jit_are_unknown(self):
        report = self.analyze([self.sample(0, jit_cache_available=False, jit_cache_used_bytes=0)])
        self.assertIsNone(report["summary"]["peak_footprint_bytes"])
        self.assertIsNone(report["summary"]["jit_used_high_water_bytes"])
        self.assertIsNone(report["summary"]["presented_fps"]["weighted_mean"])

    def test_real_zero_is_valid_and_nan_is_not(self):
        report = self.analyze([self.sample(0, phys_footprint_bytes=0, os_proc_available_memory_bytes=float("nan"))])
        self.assertEqual(0, report["summary"]["peak_footprint_bytes"]["value"])
        self.assertIsNone(report["summary"]["min_headroom_bytes"])

    def test_wall_clock_handles_startup_timer_reset(self):
        core = [self.line(0.831, "resolution_scale=0.5", "Runtime graphics settings", elapsed=29.881),
            self.line(0.837, "buffers_issued=10", elapsed=0.003),
            self.line(10.837, "buffers_issued=110", elapsed=10.003)]
        report = self.analyze(core_records=core)
        self.assertEqual(1, report["quality"]["core_timer_resets"])
        self.assertEqual(18000, report["clock"]["utc_offset_seconds"])
        self.assertAlmostEqual(0.837, report["records"][1]["session_seconds"])
        self.assertAlmostEqual(10, report["intervals"][0]["rates"]["buffers_issued"]["per_second"])

    def test_truncated_core_does_not_guess_clock_alignment(self):
        report = self.analyze(core_records=[self.line(20, "buffers_issued=10"), self.line(30, "buffers_issued=20")])
        self.assertEqual("unavailable", report["clock"]["method"])
        self.assertIsNone(report["records"][0]["session_seconds"])
        self.assertEqual("core_wall_relative", report["intervals"][0]["clock"])
        self.assertEqual("unknown", report["intervals"][0]["phase_start"])

    def test_explicit_core_offset_aligns_truncated_log(self):
        report = self.analyze(core_records=[self.line(20, "buffers_issued=10")], offset="+05:00")
        self.assertEqual(20, report["records"][0]["session_seconds"])
        self.assertEqual("explicit_offset", report["clock"]["method"])

    def test_different_session_sha_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "different session"):
            self.analyze([dict(self.session, source_commit="b" * 40, event="session_start")])

    def test_multiple_core_processes_require_selection(self):
        records = [self.line(0, "buffers_issued=10", pid=100), self.line(10, "buffers_issued=20", pid=200)]
        with self.assertRaisesRegex(ValueError, "multiple process IDs"):
            self.analyze(core_records=records)
        report = self.analyze(core_records=records, pid=200, offset="+05:00")
        self.assertEqual(1, len(report["records"]))
        self.assertEqual(200, report["records"][0]["pid"])

    def test_cumulative_creation_is_rate_not_resident_and_normalizes_aligned_frames(self):
        report = self.analyze(core_records=[self.line(0, "buffers_issued=100, buffers_mapped=10, presentation=[presented=10, queued=3]"),
            self.line(10, "buffers_issued=300, buffers_mapped=8, presentation=[presented=60, queued=1]")])
        interval = report["intervals"][0]
        self.assertEqual(5, interval["presented_fps"])
        self.assertEqual(20, interval["rates"]["buffers_issued"]["per_second"])
        self.assertEqual(4, interval["rates"]["buffers_issued"]["per_frame"])
        self.assertNotIn("buffers_mapped", interval["rates"])
        self.assertNotIn("presentation.queued", interval["rates"])

    def test_counter_reset_is_unknown_not_negative_throughput(self):
        report = self.analyze(core_records=[self.line(0, "buffers_issued=100, presentation=[presented=60]"),
            self.line(10, "buffers_issued=10, presentation=[presented=20]")])
        rate = report["intervals"][0]["rates"]["buffers_issued"]
        self.assertEqual("counter_reset", rate["status"])
        self.assertIsNone(rate["per_second"])
        self.assertIsNone(report["intervals"][0]["presented_fps"])

    def test_missing_counter_endpoint_does_not_skip_to_older_sample(self):
        report = self.analyze(core_records=[self.line(0, "buffers_issued=10"), self.line(10, "buffers_mapped=20"), self.line(20, "buffers_issued=40")])
        self.assertTrue(all(r["rates"]["buffers_issued"]["per_second"] is None for r in report["intervals"]))

    def test_actual_interval_fps_not_target_and_no_fake_frame_percentiles(self):
        core = [self.line(0, "total_presented=0, target_fps=120, interval_presented=0", "Presentation telemetry"),
            self.line(12, "total_presented=60, target_fps=120, interval_presented=60", "Presentation telemetry")]
        report = self.analyze(core_records=core)
        self.assertEqual(5, report["summary"]["presented_fps"]["weighted_mean"])
        self.assertIsNone(report["summary"]["presented_fps"]["p95_frame_time_ms"])

    def test_presentation_alias_series_not_combined_twice(self):
        core = [self.line(0, "presentation=[presented=0]"), self.line(10, "presentation=[presented=100]"),
            self.line(0, "total_presented=0", "Presentation telemetry"), self.line(10, "total_presented=100", "Presentation telemetry")]
        report = self.analyze(core_records=core)
        self.assertEqual(1, report["summary"]["presented_fps"]["interval_count"])
        self.assertEqual(10, report["summary"]["presented_fps"]["weighted_mean"])

    def test_no_cross_stream_per_frame_or_post_core_extrapolation(self):
        core = [self.line(0, "texture_owner_logical_bytes=100, managed_allocated_bytes=0", "Native memory owners v1"),
            self.line(10, "texture_owner_logical_bytes=200, managed_allocated_bytes=100", "Native memory owners v1")]
        report = self.analyze([self.sample(2, phys_footprint_bytes=1000), self.sample(60, phys_footprint_bytes=2000)], core)
        self.assertEqual([0, 10], report["coverage_seconds"]["native"])
        self.assertEqual([2, 60], report["coverage_seconds"]["memory"])
        self.assertEqual(10, report["intervals"][0]["end_seconds"])
        self.assertIsNone(report["intervals"][0]["rates"]["managed_allocated_bytes"]["per_frame"])

    def test_memory_gaps_and_out_of_order_are_reported(self):
        report = self.analyze([self.sample(4), self.sample(0), self.sample(2), self.sample(20)])
        self.assertEqual(1, report["quality"]["out_of_order_memory_records"])
        self.assertEqual(16, report["quality"]["gaps"][0]["seconds"])

    def test_manual_markers_control_slopes_not_fixed_scene_seconds(self):
        report = self.analyze([self.sample(0, phys_footprint_bytes=10), self.sample(2, phys_footprint_bytes=30),
            self.sample(4, phys_footprint_bytes=100), self.sample(6, phys_footprint_bytes=120)], phases=[("therapy", 4)])
        slopes = {r["phase"]: r["slope_per_second"] for r in report["phase_slopes"]}
        self.assertEqual({"unmarked": 10, "therapy": 10}, slopes)
        self.assertEqual("unmarked", report["records"][0]["phase"])

    def test_explicit_markers_and_stop_events_preserved(self):
        report = self.analyze([dict(event="scene_marker", elapsed_seconds=4, phase="franklin"),
            dict(event="stop_requested", elapsed_seconds=8), self.sample(10, core_active=False)])
        self.assertEqual(["franklin", "stop_requested"], [m["name"] for m in report["phases"]])
        self.assertEqual("stop_requested", report["records"][-1]["phase"])

    def test_main_returned_and_actual_post_stop_samples_have_separate_slope(self):
        records = [self.sample(2, phys_footprint_bytes=1000),
            dict(event="main_returned", elapsed_seconds=10, phys_footprint_bytes=800),
            dict(event="post_stop_sample", elapsed_seconds=12, phys_footprint_bytes=600),
            dict(event="post_stop_60s", elapsed_seconds=70, phys_footprint_bytes=200)]
        report = self.analyze(records)
        slope = next(r for r in report["phase_slopes"] if r["phase"] == "main_returned")
        self.assertEqual(3, slope["samples"])
        self.assertEqual(70, slope["end_seconds"])
        self.assertLess(slope["slope_per_second"], 0)
        self.assertEqual(1, report["summary"]["post_stop_samples"])
        self.assertEqual(1, report["summary"]["memory_samples"])

    def test_repeated_phase_names_do_not_merge_different_visits(self):
        report = self.analyze([self.sample(t, phys_footprint_bytes=value) for t, value in ((0, 0), (2, 20), (4, 100), (6, 80))],
            phases=[("therapy", 0), ("therapy", 4)])
        self.assertEqual([0, 4], [r["phase_marker_seconds"] for r in report["phase_slopes"]])
        self.assertEqual([10, -10], [r["slope_per_second"] for r in report["phase_slopes"]])

    def test_gc_duration_and_unknown_duration_counts_are_separate(self):
        report = self.analyze(core_records=[self.line(0, "managed_gc=False, managed_gc_duration_us=0", "Renderer memory trim"),
            self.line(10, "managed_gc=True, managed_gc_duration_us=245719", "Renderer memory trim"),
            self.line(20, "managed_gc=True", "Renderer memory trim")])
        self.assertEqual(2, report["summary"]["forced_gc"]["observed_events"])
        self.assertEqual(1, report["summary"]["forced_gc"]["events_with_duration"])
        self.assertEqual(245.719, report["summary"]["forced_gc"]["max_pause_ms"])

    def test_event_driven_trim_interval_is_not_a_missing_periodic_sample(self):
        report = self.analyze(core_records=[self.line(t, "managed_gc=False", "Renderer memory trim") for t in (0, 1, 2, 50)])
        self.assertEqual([], report["quality"]["gaps"])

    def test_numeric_timing_is_cpu_and_unknown_gpu_remains_unknown(self):
        report = self.analyze(core_records=[self.line(0, "PresentCpu_calls=2, PresentCpu_us=100, gpu_work_us=unknown", "CPU timing v1"),
            self.line(10, "PresentCpu_calls=5, PresentCpu_us=300, gpu_work_us=unknown", "CPU timing v1")])
        self.assertEqual(20, report["intervals"][0]["rates"]["PresentCpu_us"]["per_second"])
        self.assertIsNone(report["records"][0]["metrics"]["gpu_work_us"])

    def test_unknown_fields_preserved_without_guessing_counter_semantics(self):
        report = self.analyze(core_records=[self.line(0, "surprise_count=2"), self.line(10, "surprise_count=5")])
        self.assertEqual("unclassified", report["metric_schema"]["gpu.surprise_count"])
        self.assertEqual([], report["intervals"])
        report = self.analyze(core_records=[self.line(0, "surprise_count=2"), self.line(10, "surprise_count=5")], extra_counters=["gpu.surprise_count"])
        self.assertEqual(0.3, report["intervals"][0]["rates"]["surprise_count"]["per_second"])

    def test_texture_census_preserves_distinct_bins_views_and_conversion_counters(self):
        def payload(count):
            return (f"scope=gal_issued_lifetimes,created={count * 3},logical_bytes=100; "
                f"bin=[guest=BC1,host_gal=BC1,role=Storage,created={count},logical_bytes=100]; "
                f"bin=[guest=BC1,host_gal=BC1,role=View,created={count * 2},logical_bytes=0]; "
                f"conversion=[fallback=AstcDecode,calls={count},source_bytes={count * 100},output_bytes={count * 400},cpu_ms={count * 2}]; census_cpu_ms=0.1")
        report = self.analyze(core_records=[self.line(0, payload(1), "Texture format census v1"), self.line(10, payload(3), "Texture format census v1")])
        bins = [r for r in report["intervals"] if r["stream"] == "texture_census_bin"]
        self.assertEqual(2, len(bins))
        self.assertEqual({"Storage": 0.2, "View": 0.4}, {r["dimensions"]["role"]: r["rates"]["created"]["per_second"] for r in bins})
        conversion = next(r for r in report["intervals"] if r["stream"] == "texture_conversion")
        self.assertEqual(80, conversion["rates"]["output_bytes"]["per_second"])
        self.assertEqual("cumulative_counter", report["metric_schema"]["texture_conversion.output_bytes"])
        self.assertNotIn("logical_bytes", bins[0]["rates"])
        self.assertEqual(2, len([r for r in report["phase_slopes"] if r["stream"] == "texture_census_bin"]))

    def test_scratch_purpose_counters_do_not_cross_categories(self):
        core = [self.line(t, f"type=byte,purpose={purpose},leased_bytes={value},created_bytes={value},rents=1", "Scratch purpose v1")
            for t, purpose, value in ((0, "Decode", 100), (0, "Upload", 200), (10, "Decode", 150), (10, "Upload", 400))]
        report = self.analyze(core_records=core)
        self.assertEqual({"Decode": 5, "Upload": 20},
            {r["dimensions"]["purpose"]: r["rates"]["created_bytes"]["per_second"] for r in report["intervals"]})
        self.assertTrue(all("leased_bytes" not in r["rates"] for r in report["intervals"]))

    def test_duplicate_core_telemetry_not_counted_as_extra_event(self):
        line = self.line(0, "managed_gc=True, managed_gc_duration_us=100", "Renderer memory trim")
        report = self.analyze(core_records=[line, line])
        self.assertEqual(1, report["quality"]["duplicate_core_records"])
        self.assertEqual(1, report["summary"]["forced_gc"]["observed_events"])

    def test_csv_json_markdown_include_provenance_and_unknowns(self):
        report = self.analyze([self.sample(0, phys_footprint_bytes=10), self.sample(2, phys_footprint_bytes=20)])
        out = self.root / "report"
        analyzer.write_reports(report, out)
        self.assertIsNone(json.loads((out / "analysis.json").read_text())["summary"]["min_headroom_bytes"])
        with (out / "samples.csv").open(newline="", encoding="utf-8") as stream:
            row = next(csv.DictReader(stream))
        self.assertEqual("a" * 40, row["source_commit"])
        self.assertEqual("0.5", row["resolution_scale"])
        self.assertIn("settings", row)
        self.assertEqual("unknown", row["version"])
        self.assertIn("Minimum observed headroom: unknown", (out / "analysis.md").read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
