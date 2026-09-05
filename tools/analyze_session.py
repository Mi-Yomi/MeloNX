#!/usr/bin/env python3
"""Analyze MeloNX diagnostics without dependencies or guessed scene boundaries.

Missing values are JSON null / CSV "unknown". Logical, virtual and OS accounting
are independent series, never summed. Frame percentiles need individual samples;
the periodic presentation counters cannot provide them. Reports contain settings
and local evidence metadata: review them before publishing.

Example: python tools/analyze_session.py --session session.json --memory memory.jsonl
  --core-log core.log --core-utc-offset +05:00 --phase therapy=420 --out analysis
"""

import argparse
import csv
import datetime as dt
import hashlib
import json
import math
from pathlib import Path
import re
import statistics
import sys


MIB = 1024 * 1024
CORE_LINE = re.compile(
    r"^(?P<wall>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d(?:\.\d+)?) "
    r"MeloNX\[(?P<pid>\d+):\d+\].*?"
    r"(?P<elapsed>\d+:\d\d:\d\d\.\d+) \|[A-Z]\| (?P<body>.*)$"
)
PREFIXES = {
    "GPU memory owners": "gpu",
    "Native memory owners": "native",
    "Guest memory owners": "guest",
    "Presentation telemetry": "presentation",
    "Renderer memory trim": "trim",
    "Runtime graphics settings": "runtime_settings",
    "First Vulkan presentation": "first_presentation",
    "Vulkan swapchain created": "swapchain",
    "Scratch memory owners": "scratch",
    "Scratch byte pool": "scratch_pool",
    "Scratch purpose": "scratch_purpose",
    "Texture format census": "texture_census",
    "Render timing telemetry": "render_timing",
    "CPU timing": "cpu_timing",
}
# The schema is deliberately explicit: a handle ID, ring position, gauge, interval
# delta and cumulative counter are not interchangeable. Unknown fields are kept.
COUNTERS = {
    "gpu": {
        "buffers_issued", "buffer_map_misses", "background_buffer_copies",
        "texture_normal_evictions", "texture_normal_evicted_mib",
        "texture_normal_readback_evictions", "texture_normal_clean_bypasses",
        "presentation.enqueued", "presentation.presented", "presentation.dropped",
        "buffers_created", "buffers_reused", "buffers_deleted",
        "buffer_upload_bytes", "buffer_readback_bytes",
        "buffer_evictions", "buffer_recreations_after_eviction",
    },
    "presentation": {"total_enqueued", "total_presented", "total_dropped"},
    "native": {"managed_allocated_bytes_total", "managed_allocated_bytes", "gc_gen0_count", "gc_gen1_count", "gc_gen2_count",
        "gen0_collections", "gen1_collections", "gen2_collections"},
    "scratch": {"created_count", "reused_count", "discarded_count"},
    "texture_census": {"created", "released", "invalid_astc"},
    "texture_census_bin": {"created", "released"},
    "texture_conversion": {"calls", "failed", "source_bytes", "output_bytes", "cpu_ms"},
}
COUNTERS["cpu_timing"] = {stage + suffix for stage in (
    "GuestFifoProcess", "GalQueueBackpressure", "GalInvokeWait", "GalFrameWait", "ShaderModuleCompile",
    "GalExternalInterruptWait", "CommandBufferSubmit", "FenceWait", "PresentCpu", "SwapchainAcquire", "QueuePresent", "DiagnosticSnapshot",
) for suffix in ("_calls", "_us")}
for _stream in ("scratch_pool", "scratch_purpose"):
    COUNTERS[_stream] = {"rents", "reuses", "created_arrays", "created_bytes", "discarded_arrays", "discarded_bytes"}
FRAME_COUNTERS = {"gpu": "presentation.presented", "presentation": "total_presented"}
GAUGES = {
    "gpu": {"buffers_mapped", "buffers_in_flight", "queue_pending", "deferred_actions", "physical_memories",
        "presentation.queued", "presentation.frames_available"},
    "native": {"host_import_count", "allocator_blocks"},
    "guest": {"managed_pt_leaf_arrays", "private_process_blocks"},
    "scratch_pool": {"retained_arrays"}, "scratch_purpose": {"retained_arrays"},
    "texture_census": {"keys", "max_keys", "live_storage", "live_views"},
    "texture_census_bin": {"live_storage", "live_views"},
    "presentation": {"frames_available", "max_queued", "target_fps"},
}
SETTINGS = (
    "resolution_scale", "docked_mode", "backend_threading_requested", "backend_threading_auto_effective",
    "ios_vulkan_command_buffers", "ios_buffer_cache_limit_mib", "ios_texture_cache_limit_mib",
    "selected_jit_cache", "effective_guest_memory_gib", "expand_ram_requested", "texture_recompression",
    "scaling_filter", "anti_aliasing", "async_shader_compilation", "shader_cache_enabled", "vsync_requested",
)


def numeric(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def iso_time(value):
    if not isinstance(value, str):
        return None
    try:
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
        return parsed if parsed.tzinfo else None
    except ValueError:
        return None


def scalar(value):
    value = value.strip().rstrip(".")
    if value.lower() in ("true", "false"):
        return value.lower() == "true"
    if value.lower() in ("null", "unknown", "unavailable"):
        return None
    try:
        return int(value)
    except ValueError:
        try:
            result = float(value)
            return result if math.isfinite(result) else None
        except ValueError:
            return value


def key_values(payload):
    """Flatten bracketed named fields, retaining their namespace (views != storage)."""
    result = {}
    for match in re.finditer(r"([A-Za-z_][\w.]*)=(\[[^\]]*\]|[^,]+)", payload):
        key, value = match.groups()
        if value.startswith("[") and "=" in value:
            result.update({key + "." + k: v for k, v in key_values(value[1:-1]).items()})
        else:
            result[key] = scalar(value)
    return result


def census_records(payload):
    """Repeated lifetime bins are separate series, never overwrite or sum aliases."""
    result = []
    dimension_keys = ("guest", "host_gal", "fallback", "target", "guest_width", "guest_height",
        "host_width", "host_height", "depth", "layers", "levels", "samples", "role", "key")
    for match in re.finditer(r";\s*(bin|conversion)=\[([^\]]*)\]", payload):
        fields = key_values(match[2])
        keys = dimension_keys if match[1] == "bin" else ("fallback",)
        dimensions = {k: fields[k] for k in keys if k in fields}
        result.append({"stream": "texture_census_bin" if match[1] == "bin" else "texture_conversion",
            "dimensions": dimensions, "metrics": fields})
    header = re.sub(r";\s*(bin|conversion)=\[[^\]]*\]", "", payload).replace(";", ",")
    fields = key_values(header)
    for row in result:
        row["dimensions"]["pid"] = fields.get("pid")
    return [{"stream": "texture_census", "dimensions": {"pid": fields.get("pid")}, "metrics": fields}] + result


def read_json(path):
    with Path(path).open(encoding="utf-8-sig") as stream:
        return json.load(stream, parse_constant=lambda value: None)


def read_memory(paths, session, quality):
    records, seen = [], set()
    start = iso_time(session.get("time_utc"))
    for segment, path in enumerate(paths):
        content = Path(path).read_text(encoding="utf-8-sig").splitlines()
        nonempty = [i for i, line in enumerate(content) if line.strip()]
        previous_time = None
        for index in nonempty:
            line = content[index]
            try:
                data = json.loads(line, parse_constant=lambda value: None)
                if not isinstance(data, dict):
                    raise ValueError("record is not an object")
            except (ValueError, json.JSONDecodeError) as error:
                quality["invalid_memory_lines"].append({"segment": segment, "line": index + 1,
                    "final_line": index == nonempty[-1], "error": str(error)})
                continue
            if data.get("event") == "session_start":
                for key in ("source_commit", "time_utc"):
                    if key in data and key in session and data[key] != session[key]:
                        raise ValueError("Memory segments contain a different session: " + key)
            if data.get("source_commit") and session.get("source_commit") and data["source_commit"] != session["source_commit"]:
                raise ValueError("Memory record source_commit differs from session")
            identity = json.dumps(data, sort_keys=True, separators=(",", ":"))
            if identity in seen:
                quality["duplicate_memory_records"] += 1
                continue
            seen.add(identity)
            wall = iso_time(data.get("time_utc"))
            elapsed = data.get("elapsed_seconds")
            time = (wall - start).total_seconds() if wall and start else elapsed if numeric(elapsed) else None
            if numeric(time) and numeric(previous_time) and time < previous_time:
                quality["out_of_order_memory_records"] += 1
            if numeric(time):
                previous_time = time
            # JIT's zero placeholder when its source is unavailable is not a measurement.
            metrics = {k: v for k, v in data.items() if k not in {"time_utc", "elapsed_seconds", "event"}}
            if data.get("jit_cache_available") is False:
                for key in metrics:
                    if key.startswith("jit_cache_") and key != "jit_cache_available":
                        metrics[key] = None
            if data.get("task_vm_limit_bytes_remaining_available") is False:
                metrics["task_vm_limit_bytes_remaining"] = None
            records.append({"stream": "memory", "event": data.get("event"), "segment": segment,
                "line": index + 1, "raw_time": data.get("time_utc"), "raw_elapsed_seconds": elapsed,
                "session_seconds": time, "metrics": metrics})
    return sorted(records, key=lambda r: r["session_seconds"] if numeric(r["session_seconds"]) else -math.inf)


def parse_offset(value):
    match = re.fullmatch(r"([+-])(\d\d):(\d\d)", value)
    if not match or int(match[2]) > 14 or int(match[3]) > 59:
        raise ValueError("UTC offset must be +/-HH:MM (at most 14 hours)")
    seconds = (int(match[2]) * 60 + int(match[3])) * 60
    if seconds > 14 * 3600:
        raise ValueError("UTC offset exceeds 14 hours")
    return seconds * (1 if match[1] == "+" else -1)


def read_core(path, session, quality, offset=None, pid=None):
    if path is None:
        return [], {"method": "unavailable", "core_interval_seconds": None}
    lines = Path(path).read_text(encoding="utf-8-sig", errors="replace").splitlines()
    parsed = [(i + 1, CORE_LINE.match(line)) for i, line in enumerate(lines)]
    parsed = [(i, m) for i, m in parsed if m]
    pids = sorted({int(m["pid"]) for _, m in parsed})
    if pid is None and len(pids) > 1:
        raise ValueError("Core log has multiple process IDs; select --core-pid")
    parsed = [(i, m) for i, m in parsed if pid is None or int(m["pid"]) == pid]
    if not parsed:
        return [], {"method": "unavailable", "core_pids": pids, "core_interval_seconds": None}
    start = iso_time(session.get("time_utc"))
    first_wall = dt.datetime.fromisoformat(parsed[0][1]["wall"])
    method = "explicit_offset" if offset is not None else "unavailable"
    offset_seconds = parse_offset(offset) if offset is not None else None
    # Infer only when the first log wall clock is within 5 s of the session start
    # after a timezone adjustment. A truncated core log requires explicit offset.
    if offset_seconds is None and start:
        difference = (first_wall - start.astimezone(dt.timezone.utc).replace(tzinfo=None)).total_seconds()
        candidate = round(difference / 900) * 900
        if abs(difference - candidate) <= 5 and abs(candidate) <= 14 * 3600:
            offset_seconds, method = candidate, "inferred_from_session_start_verify_offset"
    records, seen, covered = [], set(), []
    last_elapsed = None
    for line, match in parsed:
        wall = dt.datetime.fromisoformat(match["wall"])
        time = ((wall.replace(tzinfo=dt.timezone.utc) - dt.timedelta(seconds=offset_seconds)) - start).total_seconds() if start and offset_seconds is not None else None
        covered.append(time)
        hours, minutes, seconds = match["elapsed"].split(":")
        elapsed = int(hours) * 3600 + int(minutes) * 60 + float(seconds)
        if last_elapsed is not None and elapsed < last_elapsed:
            quality["core_timer_resets"] += 1
        last_elapsed = elapsed
        # Guest log content is data, never a telemetry/scene instruction.
        body = match["body"]
        if "Guest Log:" in body or " Guest " in body and "Guest memory owners" not in body:
            continue
        for prefix, stream in PREFIXES.items():
            found = re.search(r": " + re.escape(prefix) + r"(?: v\d+)?: (.*)$", body)
            if found:
                identity = (match["wall"], match["pid"], stream, found[1])
                if identity in seen:
                    quality["duplicate_core_records"] += 1
                    break
                seen.add(identity)
                record = {"stream": stream, "event": "telemetry", "line": line,
                    "pid": int(match["pid"]), "raw_time": match["wall"], "raw_elapsed_seconds": elapsed,
                    "session_seconds": time, "core_wall_seconds": (wall - first_wall).total_seconds(),
                    "metrics": key_values(found[1])}
                if stream == "texture_census":
                    records.extend(dict(record, **part) for part in census_records(found[1]))
                else:
                    keys = ("type", "purpose") if stream == "scratch_purpose" else ("pid",) if stream == "guest" else ()
                    record["dimensions"] = {key: record["metrics"].get(key) for key in keys}
                    records.append(record)
                break
    valid_times = [t for t in covered if numeric(t)]
    return records, {"method": method, "utc_offset_seconds": offset_seconds, "core_pids": pids,
        "selected_core_pid": pid if pid is not None else pids[0],
        "raw_first_time": parsed[0][1]["wall"], "raw_last_time": parsed[-1][1]["wall"],
        "core_interval_seconds": [min(valid_times), max(valid_times)] if valid_times else None,
        "note": "Last core log line is coverage end, not proof of process termination. Raw timers may reset."}


def metric_kind(stream, key, extra_counters=()):
    if key in COUNTERS.get(stream, set()) or stream + "." + key in extra_counters:
        return "cumulative_counter"
    if stream == "memory" or key in GAUGES.get(stream, set()) or key.endswith(("_bytes_per_second", "_bytes", "_mib", "_owners")):
        return "gauge"
    if key.startswith("interval_") or key.endswith("_delta"):
        return "interval_delta"
    if key.endswith(("_duration_us", "_duration_ms")):
        return "event_duration"
    return "unclassified"


def phase_at(time, phases):
    if not numeric(time):
        return "unknown"
    applicable = [marker for marker in phases if marker["session_seconds"] <= time]
    return applicable[-1]["name"] if applicable else "unmarked"


def phase_start_at(time, phases):
    applicable = [marker["session_seconds"] for marker in phases if numeric(time) and marker["session_seconds"] <= time]
    return applicable[-1] if applicable else None


def time_of(record):
    value = record.get("session_seconds")
    return value if numeric(value) else record.get("core_wall_seconds")


def intervals(records, phases, quality, extra_counters=(), expected_interval=None):
    groups = {}
    for record in records:
        if numeric(time_of(record)):
            # Guest process counters are independent even within one host process.
            dimensions = dict(record.get("dimensions", {}))
            if record["stream"] == "guest":
                dimensions["guest_pid"] = record["metrics"].get("pid")
            key = (record["stream"], json.dumps(dimensions, sort_keys=True))
            groups.setdefault(key, []).append(record)
    output = []
    for (stream, dimensions), group in groups.items():
        group.sort(key=time_of)
        deltas = [time_of(b) - time_of(a) for a, b in zip(group, group[1:]) if time_of(b) > time_of(a)]
        cadence = expected_interval if stream == "memory" and numeric(expected_interval) else statistics.median(deltas) if deltas else None
        for previous, current in zip(group, group[1:]):
            duration = time_of(current) - time_of(previous)
            if duration <= 0:
                continue
            periodic = stream in ("memory", "gpu", "guest", "native", "presentation", "cpu_timing", "scratch", "scratch_pool", "scratch_purpose")
            gap = periodic and cadence is not None and duration > max(cadence * 2.5, cadence + 2)
            if gap:
                quality["gaps"].append({"stream": stream, "start": time_of(previous), "end": time_of(current), "seconds": duration})
            prior, now = previous["metrics"], current["metrics"]
            frame_key = FRAME_COUNTERS.get(stream)
            frame_delta = now[frame_key] - prior[frame_key] if frame_key and numeric(now.get(frame_key)) and numeric(prior.get(frame_key)) else None
            frame_delta = frame_delta if numeric(frame_delta) and frame_delta >= 0 else None
            rates = {}
            for key in sorted(set(prior) | set(now)):
                if metric_kind(stream, key, extra_counters) != "cumulative_counter":
                    continue
                delta = now[key] - prior[key] if numeric(now.get(key)) and numeric(prior.get(key)) else None
                reset = numeric(delta) and delta < 0
                if reset:
                    quality["counter_resets"].append({"stream": stream, "metric": key, "at": time_of(current)})
                    delta = None
                rates[key] = {"delta": delta, "per_second": delta / duration if delta is not None else None,
                    "per_frame": delta / frame_delta if delta is not None and frame_delta and frame_delta > 0 else None,
                    "status": "counter_reset" if reset else "unknown" if delta is None else "spans_gap" if gap else "measured_interval"}
            if rates:
                output.append({"stream": stream, "start_seconds": time_of(previous), "end_seconds": time_of(current),
                    "dimensions": json.loads(dimensions),
                    "clock": "session" if numeric(current.get("session_seconds")) else "core_wall_relative",
                    "raw_start_time": previous["raw_time"], "raw_end_time": current["raw_time"],
                    "duration_seconds": duration, "spans_gap": gap, "frame_delta": frame_delta,
                    "presented_fps": frame_delta / duration if frame_delta is not None else None,
                    "phase_start": phase_at(previous.get("session_seconds"), phases),
                    "phase_end": phase_at(current.get("session_seconds"), phases), "rates": rates})
    return output


def extrema(records, key, maximum=True):
    candidates = [r for r in records if numeric(r["metrics"].get(key))]
    if not candidates:
        return None
    record = (max if maximum else min)(candidates, key=lambda r: r["metrics"][key])
    return {"value": record["metrics"][key], "session_seconds": record["session_seconds"], "raw_time": record["raw_time"]}


def slopes(records, phases):
    results = []
    grouped = {}
    for record in records:
        if not numeric(record.get("session_seconds")):
            continue
        phase = phase_at(record["session_seconds"], phases)
        for key, value in record["metrics"].items():
            if numeric(value) and key.endswith(("_bytes", "_mib")) and metric_kind(record["stream"], key) == "gauge":
                dimensions = json.dumps(record.get("dimensions", {}), sort_keys=True)
                phase_start = phase_start_at(record["session_seconds"], phases)
                grouped.setdefault((phase, phase_start, record["stream"], key, dimensions), {})[record["session_seconds"]] = value
    for (phase, phase_start, stream, key, dimensions), points in grouped.items():
        times, values = list(points), list(points.values())
        if len(points) < 2:
            slope = None
        else:
            mean_t, mean_v = statistics.mean(times), statistics.mean(values)
            denominator = sum((t - mean_t) ** 2 for t in times)
            slope = sum((t - mean_t) * (v - mean_v) for t, v in points.items()) / denominator if denominator else None
        ordered_times = sorted(times)
        results.append({"phase": phase, "phase_marker_seconds": phase_start, "stream": stream, "metric": key, "dimensions": json.loads(dimensions), "samples": len(points),
            "start_seconds": min(times), "end_seconds": max(times), "slope_per_second": slope,
            "largest_sample_gap_seconds": max((b - a for a, b in zip(ordered_times, ordered_times[1:])), default=None),
            "method": "OLS over observed samples only; not a leak attribution"})
    return results


def analyze(session, memory_paths, core_path=None, phases=(), offset=None, pid=None, extra_counters=()):
    quality = {"invalid_memory_lines": [], "duplicate_memory_records": 0, "duplicate_core_records": 0,
        "out_of_order_memory_records": 0, "core_timer_resets": 0, "counter_resets": [], "gaps": []}
    memory = read_memory(memory_paths, session, quality)
    core, clock = read_core(core_path, session, quality, offset, pid)
    markers = [{"name": name, "session_seconds": time, "source": "manual_cli"} for name, time in phases]
    for record in memory:
        if record["event"] in ("scene_marker", "phase_marker") and numeric(record["session_seconds"]):
            name = record["metrics"].get("phase") or record["metrics"].get("scene")
            if isinstance(name, str):
                markers.append({"name": name, "session_seconds": record["session_seconds"], "source": "explicit_memory_marker"})
        if record["event"] in ("stop_requested", "core_returned", "main_returned") and numeric(record["session_seconds"]):
            markers.append({"name": record["event"], "session_seconds": record["session_seconds"], "source": "runtime_event"})
    markers.sort(key=lambda marker: marker["session_seconds"])
    all_records = memory + core
    for record in all_records:
        record["phase"] = phase_at(record["session_seconds"], markers)
    # Session metadata isn't a sampled memory record and must not distort cadence.
    sampled = [r for r in memory if r["event"] == "sample"]
    periodic_memory = [r for r in memory if r["event"] in ("sample", "post_stop_sample")]
    measured_memory = [r for r in memory if r["event"] != "session_start"]
    interval_rows = intervals(periodic_memory + core, markers, quality, extra_counters, session.get("sample_interval_seconds"))
    present = [r for r in interval_rows if r["stream"] == "presentation" and numeric(r["presented_fps"])]
    if not present:
        present = [r for r in interval_rows if r["stream"] == "gpu" and numeric(r["presented_fps"])]
    gc_records = [r for r in core if r["stream"] == "trim" and r["metrics"].get("managed_gc") is True]
    gc_durations = [r["metrics"]["managed_gc_duration_us"] / 1000 for r in gc_records if numeric(r["metrics"].get("managed_gc_duration_us"))]
    coverage = {}
    for stream in sorted({r["stream"] for r in all_records}):
        times = [time_of(r) for r in all_records if r["stream"] == stream and numeric(time_of(r))]
        coverage[stream] = [min(times), max(times)] if times else None
    schema = {}
    for record in all_records:
        for key, value in record["metrics"].items():
            if numeric(value):
                schema[record["stream"] + "." + key] = metric_kind(record["stream"], key, extra_counters)
    return {
        "schema_version": 1,
        "provenance": {"source_commit": session.get("source_commit"), "version": session.get("app_version", session.get("version")),
            "session_time_utc": session.get("time_utc"), "diagnostics_schema": session.get("schema_version"),
            "settings": {k: session.get(k) for k in SETTINGS}, "runtime_settings": [r["metrics"] for r in core if r["stream"] == "runtime_settings"],
            "resolution_observations": [r for r in core if r["stream"] in ("first_presentation", "swapchain")],
            "inputs": [{"kind": kind, "sha256": hashlib.sha256(Path(path).read_bytes()).hexdigest()} for kind, path in
                [("memory_segment", p) for p in memory_paths] + ([("core_log", core_path)] if core_path else [])]},
        "clock": clock, "coverage_seconds": coverage, "quality": quality, "metric_schema": schema, "phases": markers,
        "summary": {"memory_records": len(memory), "memory_samples": len(sampled),
            "post_stop_samples": sum(r["event"] == "post_stop_sample" for r in memory),
            "peak_footprint_bytes": extrema(memory, "phys_footprint_bytes"),
            "min_headroom_bytes": extrema(memory, "os_proc_available_memory_bytes", False),
            "jit_used_high_water_bytes": extrema(memory, "jit_cache_used_bytes"),
            "jit_address_high_water_bytes": extrema(memory, "jit_cache_address_high_water_bytes"),
            "thermal_states_observed": sorted({r["metrics"]["thermal_state_raw"] for r in memory if numeric(r["metrics"].get("thermal_state_raw"))}),
            "presented_fps": {"source": present[0]["stream"] if present else None,
                "interval_count": len(present), "weighted_mean": sum(r["frame_delta"] for r in present) / sum(r["duration_seconds"] for r in present) if present else None,
                "min_interval": min((r["presented_fps"] for r in present), default=None),
                "max_interval": max((r["presented_fps"] for r in present), default=None),
                "p95_frame_time_ms": None, "p99_frame_time_ms": None},
            "forced_gc": {"observed_events": len(gc_records), "events_with_duration": len(gc_durations),
                "max_pause_ms": max(gc_durations, default=None), "total_pause_ms": sum(gc_durations) if gc_durations else None}},
        "limitations": ["Missing metrics are unknown. No additive total of overlapping owners is calculated.",
            "buffers_issued is cumulative, not simultaneously live allocations; texture views do not add payload.",
            "No interpolation, carry-forward, extrapolation or inferred scene boundaries; no frame-time percentiles from interval counters.",
            "Per-frame resource rates use a frame counter in the SAME snapshot; otherwise unknown.",
            "A low final headroom is consistent with a memory limit; this report cannot prove Jetsam without matching system evidence.",
            "Core coverage end is not core termination. OS post-stop observations remain separate from renderer coverage."],
        "records": all_records, "intervals": interval_rows, "phase_slopes": slopes(measured_memory + core, markers),
    }


def printable(value):
    if value is None:
        return "unknown"
    return json.dumps(value, ensure_ascii=False, sort_keys=True) if isinstance(value, (dict, list)) else str(value)


def write_reports(report, out):
    out = Path(out)
    out.mkdir(parents=True, exist_ok=True)
    (out / "analysis.json").write_text(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    provenance = report["provenance"]
    context = {"source_commit": provenance["source_commit"], "version": provenance["version"],
        "resolution_scale": provenance["settings"]["resolution_scale"], "settings": provenance["settings"]}
    tables = {
        "samples.csv": [dict(context, stream=r["stream"], start_seconds=r["session_seconds"], end_seconds=r["session_seconds"],
            raw_time=r["raw_time"], raw_elapsed_seconds=r["raw_elapsed_seconds"], phase=r["phase"], event=r["event"], dimensions=r.get("dimensions", {}),
            **r["metrics"]) for r in report["records"]],
        "intervals.csv": [dict(context, **{k: v for k, v in r.items() if k != "rates"}, metric=key, **rate)
            for r in report["intervals"] for key, rate in r["rates"].items()],
        "phase-slopes.csv": [dict(context, **r) for r in report["phase_slopes"]],
    }
    for filename, rows in tables.items():
        columns = list(context) + sorted({k for r in rows for k in r} - set(context))
        with (out / filename).open("w", encoding="utf-8", newline="") as stream:
            writer = csv.DictWriter(stream, fieldnames=columns)
            writer.writeheader()
            for row in rows:
                writer.writerow({k: printable(row.get(k)) for k in columns})
    summary = report["summary"]
    def measured(item):
        return "unknown" if item is None else f"{item['value'] / MIB:.2f} MiB at {item['session_seconds']} s"
    settings = printable(provenance["settings"]).replace("`", "'")
    lines = ["# MeloNX session analysis", "", f"Source commit: `{provenance['source_commit'] or 'unknown'}`; version: `{provenance['version'] or 'unknown'}`.",
        f"Resolution scale: {printable(context['resolution_scale'])}. Settings: `{settings}`.",
        f"Observed coverage (seconds): `{printable(report['coverage_seconds'])}`.",
        f"Clock mapping: `{report['clock']['method']}`. Scene markers: `{printable(report['phases'])}`.", "",
        "All values below apply to that SHA, settings and observed coverage only.", "",
        f"- Peak observed footprint: {measured(summary['peak_footprint_bytes'])}.",
        f"- Minimum observed headroom: {measured(summary['min_headroom_bytes'])}.",
        f"- JIT used high-water: {measured(summary['jit_used_high_water_bytes'])}.",
        f"- Actual presentation counter intervals: `{printable(summary['presented_fps'])}`.",
        f"- Forced GC: `{printable(summary['forced_gc'])}`.",
        f"- Thermal states observed: {printable(summary['thermal_states_observed'])}.",
        f"- Input quality: `{printable(report['quality'])}`.", "",
        "Resource rates (per second/per frame), owner gauges and phase slopes are in CSV/JSON. Unknown schema fields are retained without guessed counter semantics.", ""]
    lines += ["- " + note for note in report["limitations"]]
    (out / "analysis.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--session", required=True, type=Path)
    parser.add_argument("--memory", required=True, nargs="+", type=Path)
    parser.add_argument("--core-log", type=Path)
    parser.add_argument("--core-utc-offset", help="Explicit local core wall-clock offset, e.g. +05:00; negative: --core-utc-offset=-04:00")
    parser.add_argument("--core-pid", type=int)
    parser.add_argument("--phase", action="append", default=[], metavar="NAME=SECONDS", help="Manual session elapsed marker, e.g. train=400, therapy=500, franklin=600")
    parser.add_argument("--counter", action="append", default=[], metavar="STREAM.METRIC", help="Explicit cumulative semantics for a new telemetry field; never declare a gauge")
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args(argv)
    try:
        phases = []
        for marker in args.phase:
            name, value = marker.rsplit("=", 1)
            seconds = float(value)
            if not name or not numeric(seconds) or seconds < 0:
                raise ValueError("Invalid phase marker: " + marker)
            phases.append((name, seconds))
        report = analyze(read_json(args.session), args.memory, args.core_log, phases, args.core_utc_offset, args.core_pid, args.counter)
        write_reports(report, args.out)
    except (OSError, ValueError) as error:
        parser.exit(2, "Analysis failed: " + str(error) + "\n")
    print(f"Analyzed {report['summary']['memory_samples']} memory samples; source {report['provenance']['source_commit'] or 'unknown'}; reports: {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
