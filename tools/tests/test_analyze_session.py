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

    def test_nslog_census_continuations_restore_split_fields_and_conversion(self):
        payload = ("pid=109,created=3,logical_bytes=100; bin=[guest=BC1,host_gal=BC1,role=Storage,created=3,logical_bytes=100]; "
            "conversion=[fallback=Native,calls=2,source_bytes=200,output_bytes=100,cpu_ms=1]; census_cpu_ms=0.1")
        split = payload.index("logical_bytes=100]") + 7
        report = self.analyze(core_records=[self.line(0, payload[:split], "Texture format census v1"),
            "2026-09-05 11:00:00.000 MeloNX[100:42] " + payload[split:]])
        self.assertEqual(1, report["quality"]["core_continuations_joined"])
        self.assertEqual([], report["quality"]["incomplete_census_records"])
        self.assertEqual(2, next(r for r in report["records"] if r["stream"] == "texture_conversion")["metrics"]["calls"])
        self.assertEqual(100, next(r for r in report["records"] if r["stream"] == "texture_census_bin")["metrics"]["logical_bytes"])

    def test_census_continuation_rejects_distant_wall_or_other_pid_and_thread(self):
        first = self.line(0, "created=3; bin=[created=90,logical_", "Texture format census v1")
        for suffix in ("2026-09-05 11:00:01.000 MeloNX[100:42] ", "2026-09-05 11:00:00.000 MeloNX[101:42] ",
                       "2026-09-05 11:00:00.000 MeloNX[100:43] "):
            report = self.analyze(core_records=[first, suffix + "bytes=200]; census_cpu_ms=0.1"])
            self.assertEqual(0, report["quality"]["core_continuations_joined"])
            self.assertEqual(1, len(report["quality"]["incomplete_census_records"]))
            self.assertEqual(3, report["records"][0]["metrics"]["created"])

    def test_census_continuation_allows_millisecond_rollover_during_write(self):
        report = self.analyze(core_records=[self.line(0, "created=3; bin=[created=3,logical_", "Texture format census v1"),
            "2026-09-05 11:00:00.001 MeloNX[100:42] bytes=100]; census_cpu_ms=0.1"])
        self.assertEqual(1, report["quality"]["core_continuations_joined"])
        self.assertEqual([], report["quality"]["incomplete_census_records"])

    def test_interleaved_full_message_does_not_steal_other_thread_census(self):
        report = self.analyze(core_records=[self.line(0, "created=3; bin=[created=3,logical_", "Texture format census v1"),
            self.line(0, "buffers_issued=99").replace("[100:42]", "[100:43]"),
            "2026-09-05 11:00:00.000 MeloNX[100:42] bytes=100]; census_cpu_ms=0.1"])
        self.assertEqual(1, report["quality"]["core_continuations_joined"])
        self.assertEqual(100, next(r for r in report["records"] if r["stream"] == "texture_census_bin")["metrics"]["logical_bytes"])

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

    def test_inactive_regular_samples_are_post_stop_without_counting_events_as_samples(self):
        report = self.analyze([self.sample(0, core_active=True), self.sample(2),
            dict(event="main_returned", elapsed_seconds=3, core_active=False),
            self.sample(4, core_active=False), dict(event="post_stop_sample", elapsed_seconds=6)])
        self.assertEqual(2, report["summary"]["memory_samples"])
        self.assertEqual(2, report["summary"]["post_stop_samples"])
        self.assertEqual("stop_or_core_inactive_observed", report["forensic_evidence"]["assessment"])

    def test_low_active_tail_is_pressure_evidence_but_never_proves_system_termination(self):
        report = self.analyze([self.sample(0, core_active=True, os_proc_available_memory_bytes=100 * analyzer.MIB),
            self.sample(2, core_active=True, os_proc_available_memory_bytes=14 * analyzer.MIB)])
        evidence = report["forensic_evidence"]
        self.assertEqual("memory_limit_pressure_at_active_recording_end", evidence["assessment"])
        self.assertEqual("unconfirmed_no_matching_system_crash_evidence", evidence["system_termination"])
        self.assertEqual(2, evidence["last_memory_sample"]["session_seconds"])

    def test_recovery_unknown_headroom_stale_sample_and_stop_do_not_inherit_old_pressure(self):
        low = self.sample(0, core_active=True, os_proc_available_memory_bytes=0)
        for tail, core, expected in (
            ([self.sample(2, core_active=True, os_proc_available_memory_bytes=500 * analyzer.MIB)], None, "recording_end_cause_unknown"),
            ([self.sample(2, core_active=True)], None, "recording_end_cause_unknown"),
            ([], [self.line(0, "buffers_issued=1"), self.line(20, "buffers_issued=2")], "last_memory_observation_precedes_core_coverage"),
            ([dict(event="stop_requested", elapsed_seconds=1)], None, "stop_or_core_inactive_observed"),
        ):
            with self.subTest(expected=expected, tail=tail):
                report = self.analyze([low] + tail, core_records=core)
                self.assertEqual(expected, report["forensic_evidence"]["assessment"])

    def test_missing_core_activity_is_not_assumed_active_at_low_headroom(self):
        report = self.analyze([self.sample(2, os_proc_available_memory_bytes=0)])
        self.assertEqual("low_headroom_observed_core_activity_unknown", report["forensic_evidence"]["assessment"])

    def test_presentation_plateau_requires_prior_frames_and_excludes_stop_and_recovery(self):
        core = [self.line(t, f"total_presented={frames}", "Presentation telemetry")
            for t, frames in ((0, 0), (10, 0), (20, 10), (30, 10), (40, 10), (50, 20))]
        report = self.analyze(core_records=core)
        periods = report["forensic_evidence"]["presentation"]["zero_progress_periods"]
        self.assertEqual(1, len(periods))
        self.assertEqual((20, 40, 20), tuple(periods[0][key] for key in ("start_seconds", "end_seconds", "duration_seconds")))
        report = self.analyze([dict(event="stop_requested", elapsed_seconds=25)], core_records=core)
        self.assertIsNone(report["forensic_evidence"]["presentation"]["longest_zero_progress_seconds"])

    def test_sparse_counter_plateau_does_not_certify_unobserved_stall(self):
        report = self.analyze(core_records=[self.line(t, "total_presented=10", "Presentation telemetry") for t in (0, 50)])
        self.assertIsNone(report["forensic_evidence"]["presentation"]["longest_zero_progress_seconds"])

    def test_nested_backend_metrics_and_quoted_guest_name_preserve_namespaces(self):
        report = self.analyze(core_records=[self.line(0, "queue_pending=0, backend=[query_copies_recorded=10, counter=[retired=5]], tail=9"),
            self.line(10, 'stall=1, pid=42, uid=5, host_name="worker, [buffer]=a", waiting_sync=True', "Guest stall thread v1")])
        self.assertEqual(5, report["records"][0]["metrics"]["backend.counter.retired"])
        self.assertEqual(9, report["records"][0]["metrics"]["tail"])
        self.assertEqual("worker, [buffer]=a", report["records"][1]["metrics"]["host_name"])
        self.assertEqual(1, report["forensic_evidence"]["diagnostic_event_counts"]["guest_stall_thread"])

    def test_census_record_and_diagnostic_summary_limits_are_explicit(self):
        payload = "created=100; " + "; ".join(f"bin=[key={index},created=1]" for index in range(100)) + "; census_cpu_ms=1"
        report = self.analyze(core_records=[self.line(0, payload, "Texture format census v1")] +
            [self.line(10, f"stall=1,pid=42,uid={index},waiting_sync=True", "Guest stall thread v1") for index in range(100)])
        self.assertEqual(35, report["quality"]["omitted_census_records"])
        self.assertEqual(65, len([r for r in report["records"] if r["stream"] == "texture_census_bin"]))
        evidence = report["forensic_evidence"]
        self.assertEqual(100, evidence["diagnostic_event_counts"]["guest_stall_thread"])
        self.assertEqual(32, len(evidence["diagnostic_tail"]["guest_stall_thread"]))
        out = self.root / "bounded-report"
        analyzer.write_reports(report, out)
        self.assertNotIn("texture_census_bin", (out / "forensic-summary.json").read_text(encoding="utf-8"))

    def forensic_packet(self, seconds, core=None, **native):
        return {"event": "memory_forensic_snapshot", "schema_version": 1, "native": {
            "event": "sample", "time_utc": f"2026-09-05T06:00:{int(seconds):02d}Z", "elapsed_precise_seconds": seconds,
            "source_commit": self.session["source_commit"], "session_time_utc": self.session["time_utc"],
            "core_active": True, "forensic_core_snapshot_status": 100,
            "forensic_core_snapshot_elapsed_precise_seconds": seconds + 0.01,
            "forensic_core_snapshot_duration_ms": 0.5, **native}, "core": core}

    def test_forensic_cached_rates_use_owner_capture_time_and_do_not_repeat_cached_samples(self):
        packets = []
        for seconds, capture, frames in ((1, 500, 10), (2, 500, 10), (3, 2500, 20)):
            payload = {"schema_version": 1, "monotonic_ms": seconds * 1000,
                "producer": {"observed": True, "captured_at_monotonic_ms": capture, "age_ms": seconds * 1000 - capture,
                    "publish_failures": 0, "data": {"presentation": f"presented={frames}, enqueued={frames}",
                        "scratch_purposes": [{"purpose": "Mirror", "created_bytes": frames * 100, "leased_bytes": 40}]}}}
            packets.append(self.forensic_packet(seconds, payload))
        report = self.analyze(forensic_paths=[self.memory(packets, "forensics.jsonl")])
        owners = [r for r in report["records"] if r["stream"] == "forensic_producer"]
        self.assertEqual(2, len(owners))
        interval = next(r for r in report["intervals"] if r["stream"] == "forensic_producer")
        self.assertEqual("core_monotonic", interval["clock"])
        self.assertEqual((0.5, 2.5, 5), (interval["start_seconds"], interval["end_seconds"], interval["presented_fps"]))
        self.assertEqual("unknown", interval["phase_end"])
        self.assertAlmostEqual(0.51, owners[0]["estimated_capture_session_seconds"])
        self.assertEqual(2, report["quality"]["reused_cached_forensic_snapshots"])
        scratch = next(r for r in report["intervals"] if r["stream"] == "forensic_scratch_purpose")
        self.assertEqual(500, scratch["rates"]["created_bytes"]["per_second"])
        self.assertIsNone(scratch["rates"]["created_bytes"]["per_frame"])

    def test_inner_buffer_cache_timestamp_not_outer_publication_controls_rates(self):
        packets = []
        for seconds, sampled, count in ((1, 400, 1), (2, 400, 1), (3, 2400, 5)):
            cache = {"sampled_at_monotonic_ms": sampled, "cached_logical_bytes": 400,
                "cumulative": {"created": {"count": count, "logical_bytes": count * 100,
                    "by_kind": {"physical": {"size_bucket_counts": [count, 0, 0, 0], "logical_bytes": count * 100}}}},
                "recent_events": [{"logical_bytes": 100, "lifetime_id": count}]}
            payload = {"schema_version": 1, "monotonic_ms": seconds * 1000,
                "producer": {"observed": True, "captured_at_monotonic_ms": seconds * 1000 - 100,
                    "data": {"physical_memories": [{"pid": 109, "buffer_cache": cache}]}}}
            packets.append(self.forensic_packet(seconds, payload))
        report = self.analyze(forensic_paths=[self.memory(packets, "forensics.jsonl")])
        interval = next(r for r in report["intervals"] if r["stream"] == "forensic_buffer_cache")
        self.assertEqual(2, interval["rates"]["cumulative.created.count"]["per_second"])
        self.assertEqual(200, interval["rates"]["cumulative.created.logical_bytes"]["per_second"])
        self.assertEqual(2, interval["rates"]["cumulative.created.by_kind.physical.size_bucket_counts.0"]["per_second"])
        self.assertNotIn("cached_logical_bytes", interval["rates"])
        self.assertNotIn("recent_events", interval["rates"])

    def test_forensic_rotation_duplicate_and_truncated_tail_are_reported(self):
        packet = self.forensic_packet(1, {"schema_version": 1, "monotonic_ms": 1000, "managed": {"allocated_bytes_total": 100}})
        one = self.memory([packet], "forensic-previous.jsonl")
        two = self.memory([packet, self.forensic_packet(2)], "forensic.jsonl", '{"event":')
        report = self.analyze(forensic_paths=[two, one])
        self.assertEqual(2, report["forensic_evidence"]["forensic_packets"]["observed"])
        self.assertEqual(1, report["quality"]["duplicate_forensic_records"])
        self.assertFalse(report["quality"]["invalid_forensic_records"][0]["newline_terminated"])
        self.assertEqual(1, len([r for r in report["records"] if r["stream"] == "forensic_managed"]))

    def test_forensic_other_source_session_and_wall_anchor_are_rejected(self):
        for changed in ({"source_commit": "b" * 40}, {"session_time_utc": "2026-09-04T06:00:00Z"}, {"elapsed_precise_seconds": 40}):
            with self.subTest(changed=changed), self.assertRaisesRegex(ValueError, "Forensic"):
                self.analyze(forensic_paths=[self.memory([self.forensic_packet(1, **changed)], "forensic.jsonl")])

    def test_unavailable_failed_and_unobserved_core_are_unknown_not_zero(self):
        packets = [self.forensic_packet(index, {"schema_version": 1, "monotonic_ms": index * 1000,
            "producer": {"observed": False}}, forensic_core_snapshot_status=status)
            for index, status in enumerate((-1, -2, -3, -4, -5, 100), 1)]
        report = self.analyze(forensic_paths=[self.memory(packets, "forensic.jsonl")])
        status = report["forensic_evidence"]["forensic_packets"]["status_counts"]
        self.assertEqual({"available": 1, "unavailable_or_busy": 2, "failed_or_invalid": 3}, status)
        self.assertFalse(any(r["stream"] == "forensic_producer" for r in report["records"]))
        self.assertIsNone(report["summary"]["presented_fps"]["weighted_mean"])

    def test_forensic_native_unavailable_placeholders_and_missing_cached_timestamp_stay_unknown(self):
        payload = {"schema_version": 1, "monotonic_ms": 1000, "renderer": {"base": {"backend": {
            "observed": True, "captured_at_monotonic_ms": 900,
            "data": {"buffers": {"status": "not_sampled", "cumulative": {"created": {"count": 0}}}}}}}}
        packet = self.forensic_packet(1, payload, jit_cache_available=False, jit_cache_used_bytes=0,
            task_vm_limit_bytes_remaining_available=False, task_vm_limit_bytes_remaining=0)
        report = self.analyze(forensic_paths=[self.memory([packet], "forensic.jsonl")])
        native = next(r for r in report["records"] if r["stream"] == "forensic_native")
        self.assertIsNone(native["metrics"]["jit_cache_used_bytes"])
        self.assertIsNone(native["metrics"]["task_vm_limit_bytes_remaining"])
        self.assertFalse(any(r["stream"] == "forensic_buffer_lifecycle" for r in report["records"]))
        self.assertTrue(any(r["reason"] == "missing_capture_time" for r in report["quality"]["invalid_forensic_records"]))

    def test_rotated_breadcrumbs_order_phases_by_precise_phase_time(self):
        base = {**self.forensic_packet(2)["native"], "event": "memory_forensic_phase", "schema_version": 1}
        before = dict(base, phase="before_core_snapshot", phase_elapsed_precise_seconds=2.01)
        complete = dict(base, phase="sample_complete", phase_elapsed_precise_seconds=2.02)
        report = self.analyze(breadcrumb_paths=[self.memory([complete], "breadcrumbs.jsonl"), self.memory([before], "breadcrumbs-previous.jsonl")])
        self.assertTrue(report["forensic_evidence"]["breadcrumbs"]["last_sample_complete_recorded"])

    def test_csv_exports_forensic_runtime_phase_and_scene_phase_without_collision(self):
        payload = {"schema_version": 1, "monotonic_ms": 1000,
            "renderer": {"base": {"trim_stage": {"phase": "complete", "sequence": 1}}}}
        breadcrumb = {**self.forensic_packet(1)["native"], "schema_version": 1,
            "event": "memory_forensic_phase", "phase": "sample_complete"}
        report = self.analyze(phases=[("manual_scene", 0)],
            forensic_paths=[self.memory([self.forensic_packet(1, payload)], "forensic.jsonl")],
            breadcrumb_paths=[self.memory([breadcrumb], "breadcrumbs.jsonl")])
        out = self.root / "phase-collision-report"
        analyzer.write_reports(report, out)
        with (out / "samples.csv").open(encoding="utf-8", newline="") as source:
            rows = list(csv.DictReader(source))
        runtime = next(row for row in rows if row["stream"] == "forensic_trim_stage")
        self.assertEqual("unknown", runtime["phase"])
        self.assertEqual("complete", runtime["metric.phase"])
        breadcrumb_row = next(row for row in rows if row["stream"] == "forensic_breadcrumb")
        self.assertEqual("manual_scene", breadcrumb_row["phase"])
        self.assertEqual("sample_complete", breadcrumb_row["metric.phase"])
        self.assertEqual("a" * 40, breadcrumb_row["source_commit"])
        self.assertEqual("a" * 40, breadcrumb_row["metric.source_commit"])

    def test_structured_managed_failure_takes_precedence_over_headroom_assessment(self):
        for terminating, expected in ((True, "terminating_managed_failure_observed"), (False, "managed_failure_observed")):
            with self.subTest(terminating=terminating):
                report = self.analyze([self.sample(1, core_active=True, os_proc_available_memory_bytes=0),
                    dict(event="managed_crash", elapsed_seconds=2, exception_type="InvalidMemoryRegionException",
                        exception_message="invalid region", exception_stack="at VirtualMemoryEvent", is_terminating=terminating)])
                evidence = report["forensic_evidence"]
                self.assertEqual(expected, evidence["assessment"])
                self.assertEqual(1, evidence["managed_failure_count"])
                self.assertEqual("at VirtualMemoryEvent", evidence["managed_failures_tail"][0]["metrics"]["exception_stack"])
                self.assertEqual("unconfirmed_no_matching_system_crash_evidence", evidence["system_termination"])
        report = self.analyze([dict(event="managed_crash_entry", elapsed_seconds=2)])
        self.assertEqual(0, report["forensic_evidence"]["managed_failure_count"])

    def test_breadcrumb_latest_kernel_sample_survives_missing_complete_packet_without_cause_claim(self):
        breadcrumb = {**self.forensic_packet(4)["native"], "event": "memory_forensic_phase", "schema_version": 1,
            "phase": "before_core_snapshot", "phase_elapsed_precise_seconds": 4.05,
            "os_proc_available_memory_bytes": 14 * analyzer.MIB}
        report = self.analyze([self.sample(2, core_active=True, os_proc_available_memory_bytes=100 * analyzer.MIB)],
            breadcrumb_paths=[self.memory([breadcrumb], "breadcrumbs.jsonl")])
        evidence = report["forensic_evidence"]
        self.assertEqual("memory_limit_pressure_at_active_recording_end", evidence["assessment"])
        self.assertEqual("forensic_breadcrumb", evidence["last_memory_sample"]["stream"])
        self.assertFalse(evidence["breadcrumbs"]["last_sample_complete_recorded"])
        self.assertIn("not the cause", evidence["breadcrumbs"]["note"])

    def test_precise_adaptive_samples_and_cumulative_compression_have_real_rates(self):
        records = [self.sample(0, elapsed_precise_seconds=0.1, task_vm_compressed_lifetime_bytes=100,
            task_vm_compressed_bytes=50, effective_sample_interval_seconds=2),
            self.sample(1, elapsed_precise_seconds=1.2, task_vm_compressed_lifetime_bytes=210,
                task_vm_compressed_bytes=60, effective_sample_interval_seconds=1)]
        report = self.analyze(records)
        self.assertAlmostEqual(100, report["intervals"][0]["rates"]["task_vm_compressed_lifetime_bytes"]["per_second"])
        self.assertNotIn("task_vm_compressed_bytes", report["intervals"][0]["rates"])
        self.assertNotIn("task_vm_compressed_lifetime_bytes", {r["metric"] for r in report["phase_slopes"]})

    def test_forensic_oversized_record_arrays_and_conflicting_cache_are_visible(self):
        payload = {"schema_version": 1, "monotonic_ms": 1000,
            "producer": {"observed": True, "captured_at_monotonic_ms": 500,
                "data": {"sequence": 1, "scratch_purposes": [{"purpose": str(index)} for index in range(100)]}}}
        first = self.forensic_packet(1, payload)
        second = json.loads(json.dumps(first))
        second["native"]["elapsed_precise_seconds"] = 2
        second["core"]["monotonic_ms"] = 2000
        second["core"]["producer"]["data"]["sequence"] = 2
        path = self.memory([first, second], "forensic.jsonl", '"' + "a" * analyzer.MAX_CORE_MESSAGE_CHARS + '"\n')
        report = self.analyze(forensic_paths=[path])
        self.assertGreaterEqual(report["quality"]["bounded_forensic_values"], 72)
        self.assertEqual(1, report["quality"]["conflicting_cached_forensic_snapshots"])
        self.assertEqual("record_size_limit", report["quality"]["invalid_forensic_records"][-1]["reason"])


if __name__ == "__main__":
    unittest.main()
