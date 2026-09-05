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
# Producer census is at most 64 keys plus overflow. Bounds also apply to
# malformed/imported logs; an omitted record is reported, never treated as zero.
MAX_CORE_MESSAGE_CHARS = 128 * 1024
MAX_CENSUS_BINS = 65
MAX_CONVERSIONS = 32
MAX_FORENSIC_EVENTS = 32
CORE_LINE = re.compile(
    r"^(?P<wall>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d(?:\.\d+)?) "
    r"MeloNX\[(?P<pid>\d+):(?P<tid>\d+)\].*?"
    r"(?P<elapsed>\d+:\d\d:\d\d\.\d+) \|(?P<level>[A-Z])\| (?P<body>.*)$"
)
CORE_CONTINUATION = re.compile(
    r"^(?P<wall>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d(?:\.\d+)?) "
    r"MeloNX\[(?P<pid>\d+):(?P<tid>\d+)\] (?P<payload>.*)$"
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
    "GPU frame stall": "frame_stall",
    "Guest stall threads": "guest_stall_snapshot",
    "Guest stall thread": "guest_stall_thread",
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
        "commands_completed", "idle_services", "backend.query_copies_recorded", "backend.idle_submissions",
    },
    "presentation": {"total_enqueued", "total_presented", "total_dropped"},
    "native": {"managed_allocated_bytes_total", "managed_allocated_bytes", "gc_gen0_count", "gc_gen1_count", "gc_gen2_count",
        "gen0_collections", "gen1_collections", "gen2_collections"},
    "scratch": {"created_count", "reused_count", "discarded_count"},
    "texture_census": {"created", "released", "invalid_astc"},
    "texture_census_bin": {"created", "released"},
    "texture_conversion": {"calls", "failed", "source_bytes", "output_bytes", "cpu_ms"},
}
COUNTERS["gpu"].update("backend.counter_" + str(index) + suffix for index in range(3) for suffix in ("_retired", "_timeouts"))
COUNTERS["memory"] = {"task_vm_decompressions_count", "task_vm_compressed_lifetime_bytes",
    "forensic_unavailable_packets", "forensic_snapshot_failures", "forensic_write_failures",
    "forensic_breadcrumb_sync_count", "forensic_breadcrumb_sync_duration_ms", "forensic_packet_sync_count", "forensic_packet_sync_duration_ms"}
COUNTERS["forensic_native"] = COUNTERS["memory"]
COUNTERS["forensic_managed"] = {"allocated_bytes_total", "gen0_collections", "gen1_collections", "gen2_collections"}
COUNTERS["forensic_renderer"] = {"commands_completed", "idle_services", "background_buffer_copies"}
COUNTERS["forensic_pressure"] = {"reports", "accepted", "processed"}
COUNTERS["forensic_buffer_cache"] = {"creation_lookup_hits", "creation_lookup_misses", "events_total", "events_contention_dropped", "events_sampled_total"}
COUNTERS["forensic_buffer_lifecycle"] = {"events_total", "events_contention_dropped", "events_sampled_total"}
COUNTERS["forensic_cache_status"] = {"publish_failures"}
COUNTERS["forensic_producer"] = {"presentation.enqueued", "presentation.presented", "presentation.dropped"}
COUNTERS["forensic_backend"] = {"progress.query_copies_recorded", "progress.idle_submissions"} | {
    "progress.counter_" + str(index) + suffix for index in range(3) for suffix in ("_retired", "_timeouts")}
COUNTERS["forensic_backend"].add("buffer_diagnostic_publish_failures")
COUNTERS["forensic_guest"] = {"buffer_diagnostic_publish_failures"}
COUNTERS["cpu_timing"] = {stage + suffix for stage in (
    "GuestFifoProcess", "GalQueueBackpressure", "GalInvokeWait", "GalFrameWait", "ShaderModuleCompile",
    "GalExternalInterruptWait", "CommandBufferSubmit", "FenceWait", "PresentCpu", "SwapchainAcquire", "QueuePresent", "DiagnosticSnapshot",
) for suffix in ("_calls", "_us")}
for _stream in ("scratch_pool", "scratch_purpose"):
    COUNTERS[_stream] = {"rents", "reuses", "created_arrays", "created_bytes", "discarded_arrays", "discarded_bytes"}
COUNTERS["forensic_scratch_purpose"] = COUNTERS["scratch_purpose"]
FRAME_COUNTERS = {"gpu": "presentation.presented", "presentation": "total_presented", "forensic_producer": "presentation.presented"}
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
    if value.startswith('"') and value.endswith('"'):
        # Names may contain commas, '=' or bracket characters. They remain data.
        return value[1:-1]
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


def key_values(payload, depth=0):
    """Bounded bracket/quote-aware fields; repeated census bins use another parser."""
    result = {}
    if depth >= 6:
        return result
    # A regex ending at the first ']' flattens nested backend/counter snapshots
    # into the wrong namespace. Split only outside brackets and quoted values.
    fields, start, brackets, quoted = [], 0, 0, False
    for index, character in enumerate(payload):
        if character == '"' and (index == 0 or payload[index - 1] != "\\"):
            quoted = not quoted
        elif not quoted:
            if character == "[":
                brackets += 1
            elif character == "]":
                brackets = max(0, brackets - 1)
            elif character == "," and brackets == 0:
                fields.append(payload[start:index])
                start = index + 1
                if len(fields) >= 256:
                    break
    else:
        fields.append(payload[start:])
    for field in fields:
        match = re.fullmatch(r"\s*([A-Za-z_][\w.]*)=(.*)\s*", field, re.DOTALL)
        if not match:
            continue
        key, value = match[1], match[2].strip().rstrip(".")
        if value.startswith("[") and value.endswith("]") and "=" in value:
            result.update({key + "." + k: v for k, v in key_values(value[1:-1], depth + 1).items()})
        else:
            result[key] = scalar(value)
    return result


def census_records(payload, quality=None):
    """Repeated lifetime bins are separate series, never overwrite or sum aliases."""
    result = []
    dimension_keys = ("guest", "host_gal", "fallback", "target", "guest_width", "guest_height",
        "host_width", "host_height", "depth", "layers", "levels", "samples", "role", "key")
    counts = {"bin": 0, "conversion": 0}
    for match in re.finditer(r";\s*(bin|conversion)=\[([^\]]*)\]", payload):
        counts[match[1]] += 1
        if counts[match[1]] > (MAX_CENSUS_BINS if match[1] == "bin" else MAX_CONVERSIONS):
            if quality is not None:
                quality["omitted_census_records"] += 1
            continue
        fields = key_values(match[2])
        keys = dimension_keys if match[1] == "bin" else ("fallback",)
        dimensions = {k: fields[k] for k in keys if k in fields}
        result.append({"stream": "texture_census_bin" if match[1] == "bin" else "texture_conversion",
            "dimensions": dimensions, "metrics": fields})
    # An incomplete trailing bin must not overwrite the valid global header.
    fields = key_values(payload.split(";", 1)[0])
    duration = re.search(r";\s*census_cpu_ms=([^;]+)$", payload)
    if duration:
        fields["census_cpu_ms"] = scalar(duration[1])
    for row in result:
        row["dimensions"]["pid"] = fields.get("pid")
    return [{"stream": "texture_census", "dimensions": {"pid": fields.get("pid")}, "metrics": fields}] + result


def read_json(path):
    with Path(path).open(encoding="utf-8-sig") as stream:
        return json.load(stream, parse_constant=lambda value: None)


def memory_metrics(data):
    metrics = {key: value for key, value in data.items() if key not in {"time_utc", "elapsed_seconds", "event"}}
    # Placeholders are identical in memory.jsonl and the forensic native copy.
    if data.get("jit_cache_available") is False:
        for key in metrics:
            if key.startswith("jit_cache_") and key != "jit_cache_available":
                metrics[key] = None
    if data.get("task_vm_limit_bytes_remaining_available") is False:
        metrics["task_vm_limit_bytes_remaining"] = None
    return metrics


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
            if data.get("session_time_utc") and data["session_time_utc"] != session.get("time_utc"):
                raise ValueError("Memory record belongs to a different session")
            identity = json.dumps(data, sort_keys=True, separators=(",", ":"))
            if identity in seen:
                quality["duplicate_memory_records"] += 1
                continue
            seen.add(identity)
            wall = iso_time(data.get("time_utc"))
            elapsed = data.get("elapsed_seconds")
            precise = data.get("elapsed_precise_seconds")
            time = precise if numeric(precise) else (wall - start).total_seconds() if wall and start else elapsed if numeric(elapsed) else None
            if numeric(time) and numeric(previous_time) and time < previous_time:
                quality["out_of_order_memory_records"] += 1
            if numeric(time):
                previous_time = time
            # JIT's zero placeholder when its source is unavailable is not a measurement.
            metrics = memory_metrics(data)
            records.append({"stream": "memory", "event": data.get("event"), "segment": segment,
                "line": index + 1, "raw_time": data.get("time_utc"), "raw_elapsed_seconds": elapsed,
                "session_seconds": time, "metrics": metrics})
    return sorted(records, key=lambda r: r["session_seconds"] if numeric(r["session_seconds"]) else -math.inf)


def bounded_json(value, budget, quality, depth=0):
    """Keep supplied scalar meanings; bound malformed JSON nesting and collections."""
    if budget[0] <= 0 or depth > 16:
        quality["bounded_forensic_values"] += 1
        return None
    budget[0] -= 1
    if isinstance(value, dict):
        if len(value) > 128:
            quality["bounded_forensic_values"] += len(value) - 128
        return {str(key)[:128]: bounded_json(item, budget, quality, depth + 1) for key, item in list(value.items())[:128]}
    if isinstance(value, list):
        if len(value) > 64:
            quality["bounded_forensic_values"] += len(value) - 64
        return [bounded_json(item, budget, quality, depth + 1) for item in value[:64]]
    if isinstance(value, str) and len(value) > 4096:
        quality["bounded_forensic_values"] += 1
        return value[:4096]
    return value


def flatten_json(value, prefix=""):
    """Known structured snapshots; arrays of events remain illustrative data."""
    result = {}
    for key, item in value.items():
        path = prefix + key
        if isinstance(item, dict):
            result.update(flatten_json(item, path + "."))
        elif isinstance(item, list) and key == "size_bucket_counts":
            for index, count in enumerate(item[:4]):
                result[path + "." + str(index)] = count
        else:
            result[path] = item
    return result


def read_forensic_jsonl(paths, session, quality, breadcrumb=False):
    rows, seen = [], set()
    start = iso_time(session.get("time_utc"))
    event = "memory_forensic_phase" if breadcrumb else "memory_forensic_snapshot"
    kind = "breadcrumbs" if breadcrumb else "forensics"
    for segment, path in enumerate(paths):
        with Path(path).open(encoding="utf-8-sig") as source:
            line_number = 0
            while True:
                line = source.readline(MAX_CORE_MESSAGE_CHARS + 1)
                if not line:
                    break
                line_number += 1
                if len(line) > MAX_CORE_MESSAGE_CHARS:
                    while line and not line.endswith("\n"):
                        line = source.readline(MAX_CORE_MESSAGE_CHARS + 1)
                    quality["invalid_forensic_records"].append({"kind": kind, "segment": segment, "line": line_number, "reason": "record_size_limit"})
                    continue
                if not line.strip():
                    continue
                try:
                    data = json.loads(line, parse_constant=lambda value: None)
                    if not isinstance(data, dict) or data.get("event") != event or data.get("schema_version") != 1:
                        raise ValueError("unexpected forensic envelope schema/event")
                    native = data if breadcrumb else data.get("native")
                    if not isinstance(native, dict):
                        raise ValueError("missing native object")
                except (ValueError, RecursionError) as error:
                    quality["invalid_forensic_records"].append({"kind": kind, "segment": segment, "line": line_number,
                        "reason": str(error)[:160], "newline_terminated": line.endswith("\n")})
                    continue
                for item in (data, native):
                    if item.get("source_commit") and session.get("source_commit") and item["source_commit"] != session["source_commit"]:
                        raise ValueError("Forensic record source_commit differs from session")
                    if item.get("session_time_utc") and item["session_time_utc"] != session.get("time_utc"):
                        raise ValueError("Forensic record belongs to a different session")
                if not session.get("source_commit") or not (data.get("source_commit") or native.get("source_commit")):
                    quality["unverified_forensic_source_records"] += 1
                elapsed = native.get("elapsed_precise_seconds")
                wall = iso_time(native.get("time_utc"))
                if start and wall and numeric(elapsed):
                    # Legacy timestamps have second precision; tolerate rounding,
                    # not a different boot/session with the same counter values.
                    if abs((wall - start).total_seconds() - elapsed) > 2:
                        raise ValueError("Forensic wall/elapsed anchor differs from session")
                elif not (data.get("session_time_utc") == session.get("time_utc") and session.get("time_utc")):
                    quality["invalid_forensic_records"].append({"kind": kind, "segment": segment, "line": line_number, "reason": "unverifiable_session_anchor"})
                    continue
                digest = hashlib.sha256(json.dumps(data, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
                if digest in seen:
                    quality["duplicate_forensic_records"] += 1
                    continue
                seen.add(digest)
                data = bounded_json(data, [12000], quality)
                native = data if breadcrumb else data["native"]
                if not isinstance(native, dict):
                    quality["invalid_forensic_records"].append({"kind": kind, "segment": segment, "line": line_number, "reason": "native_payload_exceeded_bounds"})
                    continue
                time = elapsed if numeric(elapsed) else (wall - start).total_seconds() if wall and start else None
                record = {"stream": "forensic_breadcrumb" if breadcrumb else "forensic_native", "event": event,
                    "segment": segment, "line": line_number, "raw_time": native.get("time_utc"),
                    "raw_elapsed_seconds": native.get("elapsed_seconds"), "session_seconds": time,
                    "dimensions": {}, "metrics": memory_metrics(native)}
                if not breadcrumb:
                    record["event"] = native.get("event", event)
                    record["core_payload"] = data.get("core")
                rows.append(record)
    return sorted(rows, key=lambda row: (row["session_seconds"] if numeric(row["session_seconds"]) else -math.inf,
        row["metrics"]["phase_elapsed_precise_seconds"] if numeric(row["metrics"].get("phase_elapsed_precise_seconds")) else 0))


def forensic_core_records(packets, quality):
    output, cached_seen = [], {}
    for packet in packets:
        payload = packet.pop("core_payload", None)
        status = packet["metrics"].get("forensic_core_snapshot_status")
        packet["core_payload_accepted"] = False
        if not isinstance(payload, dict):
            if numeric(status) and status > 0:
                quality["invalid_forensic_records"].append({"kind": "core", "segment": packet["segment"], "line": packet["line"], "reason": "missing_core_payload_despite_positive_status"})
            continue
        if not numeric(status) or status <= 0 or payload.get("schema_version") != 1 or not numeric(payload.get("monotonic_ms")):
            quality["invalid_forensic_records"].append({"kind": "core", "segment": packet["segment"], "line": packet["line"], "reason": "unavailable_or_invalid_core_payload"})
            continue
        packet["core_payload_accepted"] = True
        now = payload["monotonic_ms"]
        anchor = packet["metrics"].get("forensic_core_snapshot_elapsed_precise_seconds")
        def emit(stream, values, dimensions=None, captured=None, cached=False):
            if not isinstance(values, dict):
                return
            if cached and not numeric(captured):
                quality["invalid_forensic_records"].append({"kind": stream, "line": packet["line"], "reason": "missing_capture_time"})
                return
            captured = now if captured is None else captured
            if not numeric(captured) or captured > now:
                quality["invalid_forensic_records"].append({"kind": stream, "line": packet["line"], "reason": "invalid_future_or_missing_capture_time"})
                return
            metrics = flatten_json(values)
            dims = dimensions or {}
            if cached:
                key = (stream, json.dumps(dims, sort_keys=True), captured)
                digest = hashlib.sha256(json.dumps(metrics, sort_keys=True).encode()).hexdigest()
                if key in cached_seen:
                    quality["reused_cached_forensic_snapshots"] += 1
                    if cached_seen[key] != digest:
                        quality["conflicting_cached_forensic_snapshots"] += 1
                    return
                cached_seen[key] = digest
            # Rates use exact C# monotonic deltas, never packet arrival cadence.
            # Swift/Core call boundaries are not the same instant: retain the
            # alignment estimate with its uncertainty instead of guessing phases.
            record = {key: packet.get(key) for key in ("line", "segment", "raw_time", "raw_elapsed_seconds")}
            record.update(stream=stream, event="forensic_snapshot", metrics=metrics, dimensions=dims,
                session_seconds=None, counter_seconds=captured / 1000, interval_clock="core_monotonic",
                packet_session_seconds=packet["session_seconds"], captured_at_monotonic_ms=captured,
                capture_age_at_packet_ms=now - captured,
                estimated_capture_session_seconds=anchor - (now - captured) / 1000 if numeric(anchor) else None,
                anchor_uncertainty_ms=packet["metrics"].get("forensic_core_snapshot_duration_ms"))
            output.append(record)
        for key in ("managed", "pressure"):
            emit("forensic_" + key, payload.get(key))
        renderer = payload.get("renderer")
        renderer = renderer if isinstance(renderer, dict) else {}
        emit("forensic_renderer", {key: value for key, value in renderer.items() if key not in ("base", "backend", "trim_stage")})
        base = renderer.get("base", renderer)
        base = base if isinstance(base, dict) else {}
        emit("forensic_trim_stage", base.get("trim_stage"))
        for name, cache in (("producer", payload.get("producer")), ("backend", base.get("backend"))):
            if not isinstance(cache, dict):
                continue
            emit("forensic_cache_status", {key: value for key, value in cache.items() if key != "data"}, {"owner": name})
            captured = cache.get("captured_at_monotonic_ms")
            data = cache.get("data")
            if cache.get("observed") is not True or not isinstance(data, dict) or not numeric(captured):
                continue
            skip = {"physical_memories", "scratch_purposes", "buffers"}
            metrics = {key: value for key, value in data.items() if key not in skip}
            for key in ("presentation", "progress"):
                if isinstance(metrics.get(key), str):
                    metrics[key] = key_values(metrics[key])
            emit("forensic_" + name, metrics, captured=captured, cached=True)
            if name == "producer":
                memories = data.get("physical_memories")
                for memory in (memories if isinstance(memories, list) else [])[:4]:
                    if not isinstance(memory, dict):
                        continue
                    dimensions = {"pid": memory.get("pid")}
                    guest = key_values(memory["guest_owners"]) if isinstance(memory.get("guest_owners"), str) else {}
                    guest["texture_cache_bytes"] = memory.get("texture_cache_bytes")
                    guest["buffer_diagnostic_publish_failures"] = memory.get("buffer_diagnostic_publish_failures")
                    emit("forensic_guest", guest, dimensions, captured, True)
                    buffer = memory.get("buffer_cache")
                    if isinstance(buffer, dict):
                        emit("forensic_buffer_cache", buffer, dimensions, buffer.get("sampled_at_monotonic_ms"), True)
                purposes = data.get("scratch_purposes")
                for purpose in (purposes if isinstance(purposes, list) else []):
                    if isinstance(purpose, dict):
                        emit("forensic_scratch_purpose", purpose, {"purpose": purpose.get("purpose")}, captured, True)
            elif isinstance(data.get("buffers"), dict):
                buffer = data["buffers"]
                emit("forensic_buffer_lifecycle", buffer, captured=buffer.get("sampled_at_monotonic_ms"), cached=True)
    return output


def parse_offset(value):
    match = re.fullmatch(r"([+-])(\d\d):(\d\d)", value)
    if not match or int(match[2]) > 14 or int(match[3]) > 59:
        raise ValueError("UTC offset must be +/-HH:MM (at most 14 hours)")
    seconds = (int(match[2]) * 60 + int(match[3])) * 60
    if seconds > 14 * 3600:
        raise ValueError("UTC offset exceeds 14 hours")
    return seconds * (1 if match[1] == "+" else -1)


def logical_core_lines(lines, quality):
    """Reassemble NSLog's split census message without joining other threads.

    Continuations repeat PID:TID but omit the core timer and log category. Their
    wall stamp may advance by a millisecond while NSLog emits the same message.
    A different full message on the same key closes an unfinished census. The
    explicit final census_cpu_ms field distinguishes a complete message from a
    file truncated during a write. Raw evidence stays unchanged on disk.
    """
    logical, pending = [], {}
    for index, line in enumerate(lines):
        match = CORE_LINE.match(line)
        if match:
            key = (match["pid"], match["tid"])
            pending.pop(key, None)
            if len(line) > MAX_CORE_MESSAGE_CHARS:
                quality["bounded_core_messages"] += 1
            logical.append([index + 1, line[:MAX_CORE_MESSAGE_CHARS]])
            if re.search(r": Texture format census(?: v\d+)?: ", match["body"]) and "; census_cpu_ms=" not in line:
                pending[key] = (len(logical) - 1, dt.datetime.fromisoformat(match["wall"]))
            continue
        continuation = CORE_CONTINUATION.match(line)
        if continuation:
            key = (continuation["pid"], continuation["tid"])
            target = pending.get(key)
            if target is not None:
                slot, started = target
                elapsed = (dt.datetime.fromisoformat(continuation["wall"]) - started).total_seconds()
                if not 0 <= elapsed <= 0.1:
                    pending.pop(key)
                    continue
                remaining = MAX_CORE_MESSAGE_CHARS - len(logical[slot][1])
                if len(continuation["payload"]) > remaining:
                    quality["bounded_core_messages"] += 1
                    pending.pop(key)
                logical[slot][1] += continuation["payload"][:remaining]
                quality["core_continuations_joined"] += 1
                if "; census_cpu_ms=" in continuation["payload"]:
                    pending.pop(key, None)
    return logical


def read_core(path, session, quality, offset=None, pid=None):
    if path is None:
        return [], {"method": "unavailable", "core_interval_seconds": None}
    lines = Path(path).read_text(encoding="utf-8-sig", errors="replace").splitlines()
    parsed = [(i, CORE_LINE.match(line)) for i, line in logical_core_lines(lines, quality)]
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
        if "Guest Log:" in body:
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
                    complete = "; census_cpu_ms=" in found[1]
                    record["message_complete"] = complete
                    if not complete:
                        quality["incomplete_census_records"].append({"line": line, "session_seconds": time})
                    records.extend(dict(record, **part) for part in census_records(found[1], quality))
                else:
                    keys = (("type", "purpose") if stream == "scratch_purpose" else ("pid",) if stream == "guest"
                        else ("pid", "uid") if stream == "guest_stall_thread" else ())
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
    if stream in ("forensic_buffer_cache", "forensic_buffer_lifecycle") and key.startswith("cumulative."):
        if key.endswith((".count", ".logical_bytes")) or re.search(r"\.size_bucket_counts\.[0-3]$", key):
            return "cumulative_counter"
    if stream in ("memory", "forensic_native", "forensic_breadcrumb") or key in GAUGES.get(stream, set()) or key.endswith(("_bytes_per_second", "_bytes", "_mib", "_owners")):
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
    if numeric(record.get("counter_seconds")):
        return record["counter_seconds"]
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
            periodic = stream in ("memory", "gpu", "guest", "native", "presentation", "cpu_timing", "scratch", "scratch_pool", "scratch_purpose", "forensic_native", "forensic_managed", "forensic_renderer")
            current_cadence = current["metrics"].get("effective_sample_interval_seconds", cadence)
            gap = periodic and numeric(current_cadence) and duration > max(current_cadence * 2.5, current_cadence + 2)
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
                    "clock": current.get("interval_clock", "session" if numeric(current.get("session_seconds")) else "core_wall_relative"),
                    "raw_start_time": previous["raw_time"], "raw_end_time": current["raw_time"],
                    "end_packet_session_seconds": current.get("packet_session_seconds"),
                    "duration_seconds": duration, "spans_gap": gap, "frame_delta": frame_delta,
                    "frame_counter_start": prior.get(frame_key), "frame_counter_end": now.get(frame_key),
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


STOP_EVENTS = {"stop_requested", "core_returned", "main_returned"}


def post_stop_sample(record):
    return record["event"] == "post_stop_sample" or (
        record["event"] == "sample" and record["metrics"].get("core_active") is False)


def evidence_reference(record, keys=()):
    if record is None:
        return None
    return {**{key: record.get(key) for key in ("stream", "event", "line", "segment", "session_seconds", "raw_time")},
        "metrics": {key: record["metrics"].get(key) for key in keys}}


def forensic_evidence(memory, core, interval_rows, clock, packets=(), breadcrumbs=()):
    """Describe the end of a recording, never invent the reason the process ended.

    No scene is inferred. A completed stop prevents the low-headroom observation
    from being described as an unexplained active end. A stale earlier minimum
    cannot replace the last observation. Counter plateaus are bounded intervals,
    not proof of a deadlock or of the producer/backend responsible for one.
    """
    stop_records = [r for r in memory if r["event"] in STOP_EVENTS]
    # Breadcrumbs can preserve the newest cheap kernel sample when the full
    # diagnostic packet was never written. Prefer the complete copy at equal time.
    periodic = [r for r in memory if r["event"] in ("sample", "post_stop_sample")]
    periodic += list(packets) + list(breadcrumbs)
    periodic.sort(key=lambda r: (r["session_seconds"] if numeric(r["session_seconds"]) else -math.inf,
        {"forensic_breadcrumb": 0, "memory": 1, "forensic_native": 2}.get(r["stream"], 0)))
    by_sample = {}
    for record in periodic:
        by_sample[record["session_seconds"]] = record
    periodic = list(by_sample.values())
    last = periodic[-1] if periodic else None
    stop_times = [r["session_seconds"] for r in stop_records if numeric(r["session_seconds"])]
    stop_times += [r["session_seconds"] for r in periodic if r["metrics"].get("core_active") is False and numeric(r["session_seconds"])]
    stop_at = min(stop_times) if stop_times else None
    memory_keys = ("core_active", "phys_footprint_bytes", "process_phys_footprint_peak_bytes",
        "os_proc_available_memory_bytes", "task_vm_limit_bytes_remaining", "task_vm_compressed_bytes",
        "task_vm_internal_bytes", "thermal_state_raw")
    headroom = last["metrics"].get("os_proc_available_memory_bytes") if last else None
    low = numeric(headroom) and 0 <= headroom <= 64 * MIB
    core_end = (clock.get("core_interval_seconds") or [None, None])[1]
    last_time = last.get("session_seconds") if last else None
    stale = numeric(core_end) and numeric(last_time) and core_end - last_time > 10
    if stop_records or any(post_stop_sample(r) or r["metrics"].get("core_active") is False for r in periodic):
        assessment = "stop_or_core_inactive_observed"
    elif last is None:
        assessment = "memory_observation_unavailable"
    elif low and last["metrics"].get("core_active") is True and not stale:
        assessment = "memory_limit_pressure_at_active_recording_end"
    elif stale:
        assessment = "last_memory_observation_precedes_core_coverage"
    elif low:
        assessment = "low_headroom_observed_core_activity_unknown"
    else:
        assessment = "recording_end_cause_unknown"

    source = next((stream for stream in ("presentation", "gpu", "forensic_producer")
        if any(r["stream"] == stream and numeric(r["presented_fps"]) for r in interval_rows)), "gpu")
    presentation = sorted((r for r in interval_rows if r["stream"] == source), key=lambda r: r["start_seconds"])
    plateaus, current = [], None
    for interval in presentation:
        # Producer reports every 10 s. A sparse log with only two points must
        # not silently establish its own 10-minute cadence and certify a stall.
        valid = (interval["frame_delta"] == 0 and numeric(interval["frame_counter_start"])
            and interval["frame_counter_start"] > 0 and not interval["spans_gap"]
            and interval["duration_seconds"] <= 30
            and not (numeric(stop_at) and (
                interval["clock"] == "session" and interval["end_seconds"] > stop_at
                or interval["clock"] == "core_wall_relative"
                or interval["clock"] == "core_monotonic" and (
                    not numeric(interval.get("end_packet_session_seconds")) or interval["end_packet_session_seconds"] > stop_at))))
        if not valid:
            current = None
            continue
        if current and current["clock"] == interval["clock"] and current["end_seconds"] == interval["start_seconds"]:
            current["end_seconds"] = interval["end_seconds"]
            current["duration_seconds"] += interval["duration_seconds"]
            current["intervals"] += 1
        else:
            current = {key: interval[key] for key in ("clock", "start_seconds", "end_seconds", "duration_seconds")}
            current.update(intervals=1, presented_counter=interval["frame_counter_start"])
            plateaus.append(current)
    plateaus = [r for r in plateaus if r["duration_seconds"] >= 10]
    diagnostic_streams = {"frame_stall", "guest_stall_snapshot", "guest_stall_thread"}
    diagnostics = [r for r in core if r["stream"] in diagnostic_streams]
    diagnostic_counts = {stream: sum(r["stream"] == stream for r in diagnostics) for stream in sorted(diagnostic_streams)}
    tail = {stream: [{**evidence_reference(r), "metrics": r["metrics"]} for r in diagnostics if r["stream"] == stream][-MAX_FORENSIC_EVENTS:]
        for stream in sorted(diagnostic_streams)}
    owner_last = {}
    for record in core:
        if record["stream"].startswith("forensic_"):
            key = (record["stream"], json.dumps(record.get("dimensions", {}), sort_keys=True))
            owner_last[key] = record
    owner_tail = []
    for record in list(owner_last.values())[:64]:
        metrics = {key: value[-8:] if key == "recent_events" and isinstance(value, list) else value for key, value in record["metrics"].items()}
        owner_tail.append({**evidence_reference(record), "dimensions": record.get("dimensions", {}), "metrics": metrics,
            **{key: record.get(key) for key in ("packet_session_seconds", "captured_at_monotonic_ms", "capture_age_at_packet_ms", "estimated_capture_session_seconds", "anchor_uncertainty_ms")}})
    last_breadcrumb = breadcrumbs[-1] if breadcrumbs else None
    packet_counts = {}
    for packet in packets:
        status = packet["metrics"].get("forensic_core_snapshot_status")
        state = ("available" if packet.get("core_payload_accepted") else "positive_status_missing_or_invalid_payload") if numeric(status) and status > 0 else "unavailable_or_busy" if status in (-1, -3) else "failed_or_invalid" if status in (-2, -4, -5) else "unknown"
        packet_counts[state] = packet_counts.get(state, 0) + 1
    return {"assessment": assessment, "system_termination": "unconfirmed_no_matching_system_crash_evidence",
        "policy": {"low_headroom_bytes": 64 * MIB, "memory_stale_after_seconds": 10,
            "plateau_min_seconds": 10, "plateau_max_single_interval_seconds": 30, "diagnostic_tail_per_stream": MAX_FORENSIC_EVENTS},
        "last_memory_sample": evidence_reference(last, memory_keys),
        "memory_tail": [evidence_reference(r, memory_keys) for r in periodic[-8:]],
        "stop_event_counts": {event: sum(r["event"] == event for r in stop_records) for event in sorted(STOP_EVENTS)},
        "stop_events_tail": [evidence_reference(r) for r in stop_records[-MAX_FORENSIC_EVENTS:]],
        "presentation": {"source": source if presentation else None, "zero_progress_periods": plateaus[-MAX_FORENSIC_EVENTS:],
            "zero_progress_periods_observed": len(plateaus),
            "longest_zero_progress_seconds": max((r["duration_seconds"] for r in plateaus), default=None),
            "last_interval": {key: presentation[-1][key] for key in ("clock", "start_seconds", "end_seconds", "presented_fps", "frame_delta", "spans_gap")} if presentation else None,
            "note": "Counter plateau is observed non-presentation, not an inferred deadlock or scene; unobserved time and post-stop intervals are excluded."},
        "diagnostic_event_counts": diagnostic_counts, "diagnostic_tail": tail,
        "forensic_packets": {"observed": len(packets), "status_counts": packet_counts,
            "last_native": evidence_reference(packets[-1], tuple(packets[-1]["metrics"])) if packets else None,
            "last_owner_snapshots": owner_tail,
            "note": "Cached owners retain their own capture times; repeated cached payloads never create new rates. Core monotonic rates are not assigned guessed scene phases."},
        "breadcrumbs": {"observed": len(breadcrumbs), "last": evidence_reference(last_breadcrumb, tuple(last_breadcrumb["metrics"])) if last_breadcrumb else None,
            "last_sample_complete_recorded": last_breadcrumb["metrics"].get("phase") == "sample_complete" if last_breadcrumb else None,
            "note": "An unfinished sampling phase locates the last persisted breadcrumb, not the cause of a crash or evidence that its following operation failed."},
        "note": "Low active-end headroom is consistent with a process memory limit. File end is not proof of termination; Jetsam requires matching system evidence."}


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


def analyze(session, memory_paths, core_path=None, phases=(), offset=None, pid=None, extra_counters=(), forensic_paths=(), breadcrumb_paths=()):
    quality = {"invalid_memory_lines": [], "duplicate_memory_records": 0, "duplicate_core_records": 0,
        "out_of_order_memory_records": 0, "core_timer_resets": 0, "counter_resets": [], "gaps": []}
    quality.update(core_continuations_joined=0, incomplete_census_records=[], bounded_core_messages=0, omitted_census_records=0)
    quality.update(invalid_forensic_records=[], duplicate_forensic_records=0, bounded_forensic_values=0,
        reused_cached_forensic_snapshots=0, conflicting_cached_forensic_snapshots=0, unverified_forensic_source_records=0)
    memory = read_memory(memory_paths, session, quality)
    core, clock = read_core(core_path, session, quality, offset, pid)
    packets = read_forensic_jsonl(forensic_paths, session, quality)
    breadcrumbs = read_forensic_jsonl(breadcrumb_paths, session, quality, breadcrumb=True)
    core += forensic_core_records(packets, quality)
    markers = [{"name": name, "session_seconds": time, "source": "manual_cli"} for name, time in phases]
    for record in memory:
        if record["event"] in ("scene_marker", "phase_marker") and numeric(record["session_seconds"]):
            name = record["metrics"].get("phase") or record["metrics"].get("scene")
            if isinstance(name, str):
                markers.append({"name": name, "session_seconds": record["session_seconds"], "source": "explicit_memory_marker"})
        if record["event"] in ("stop_requested", "core_returned", "main_returned") and numeric(record["session_seconds"]):
            markers.append({"name": record["event"], "session_seconds": record["session_seconds"], "source": "runtime_event"})
    markers.sort(key=lambda marker: marker["session_seconds"])
    all_records = memory + core + packets + breadcrumbs
    for record in all_records:
        record["phase"] = phase_at(record["session_seconds"], markers)
    # Session metadata isn't a sampled memory record and must not distort cadence.
    sampled = [r for r in memory if r["event"] == "sample" and not post_stop_sample(r)]
    periodic_memory = [r for r in memory if r["event"] in ("sample", "post_stop_sample")]
    measured_memory = [r for r in memory if r["event"] != "session_start"]
    interval_rows = intervals(periodic_memory + core + packets, markers, quality, extra_counters, session.get("sample_interval_seconds"))
    present = [r for r in interval_rows if r["stream"] == "presentation" and numeric(r["presented_fps"])]
    if not present:
        present = [r for r in interval_rows if r["stream"] == "gpu" and numeric(r["presented_fps"])]
    if not present:
        present = [r for r in interval_rows if r["stream"] == "forensic_producer" and numeric(r["presented_fps"])]
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
                [("memory_segment", p) for p in memory_paths] + ([("core_log", core_path)] if core_path else [])
                + [("forensic_segment", p) for p in forensic_paths] + [("breadcrumb_segment", p) for p in breadcrumb_paths]]},
        "clock": clock, "coverage_seconds": coverage, "quality": quality, "metric_schema": schema, "phases": markers,
        "forensic_evidence": forensic_evidence(memory or packets, core, interval_rows, clock, packets, breadcrumbs),
        "summary": {"memory_records": len(memory), "memory_samples": len(sampled),
            "post_stop_samples": sum(post_stop_sample(r) for r in memory),
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
        "records": all_records, "intervals": interval_rows, "phase_slopes": slopes(measured_memory + core + packets, markers),
    }


def printable(value):
    if value is None:
        return "unknown"
    return json.dumps(value, ensure_ascii=False, sort_keys=True) if isinstance(value, (dict, list)) else str(value)


def write_reports(report, out):
    out = Path(out)
    out.mkdir(parents=True, exist_ok=True)
    (out / "analysis.json").write_text(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    (out / "forensic-summary.json").write_text(json.dumps({"provenance": report["provenance"],
        "quality": report["quality"], "forensic_evidence": report["forensic_evidence"]},
        ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")
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
        f"Recording-end evidence: `{report['forensic_evidence']['assessment']}`. System termination is unconfirmed without matching system evidence.",
        f"Observed longest presentation plateau: {printable(report['forensic_evidence']['presentation']['longest_zero_progress_seconds'])} seconds. No scene or deadlock cause is inferred.",
        f"Active/unknown-activity periodic samples: {summary['memory_samples']}; post-stop/inactive periodic samples: {summary['post_stop_samples']}.", "",
        "The bounded forensic-summary.json contains end-of-recording evidence and diagnostic tails, without texture census bins.", "",
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
    parser.add_argument("--forensics", nargs="+", default=[], type=Path, help="Optional memory-forensics JSONL current/previous segments")
    parser.add_argument("--breadcrumbs", nargs="+", default=[], type=Path, help="Optional memory-breadcrumbs JSONL current/previous segments")
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
        report = analyze(read_json(args.session), args.memory, args.core_log, phases, args.core_utc_offset, args.core_pid, args.counter, args.forensics, args.breadcrumbs)
        write_reports(report, args.out)
    except (OSError, ValueError) as error:
        parser.exit(2, "Analysis failed: " + str(error) + "\n")
    print(f"Analyzed {report['summary']['memory_samples']} memory samples; source {report['provenance']['source_commit'] or 'unknown'}; reports: {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
