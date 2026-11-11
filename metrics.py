"""Read-only local measurements with optional official account-quota lookup."""
from __future__ import annotations

import argparse
import collections
from contextlib import closing
import datetime as dt
import hashlib
import json
import math
import os
from pathlib import Path
import sqlite3
import sys
import time
import unicodedata
from typing import Any

# In isolated mode, trust only this installed source directory for sibling modules.
if __name__ == "__main__":
    sys.path.insert(0, str(Path(__file__).resolve().parent))

from speed import LiveSpeed, GlobalSpeed, interval as speed_interval


def timestamp_ms(value: str | None) -> float | None:
    if not isinstance(value, str) or not value:
        return None
    try:
        return dt.datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp() * 1000
    except (TypeError, ValueError):
        return None


def integer(value: Any) -> int:
    try:
        return max(0, min(10**12, int(value)))
    except (TypeError, ValueError, OverflowError):
        return 0


def cache_percent(usage: dict) -> float | None:
    if "input_tokens" not in usage or "cached_input_tokens" not in usage:
        return None
    if usage["input_tokens"] is None or usage["cached_input_tokens"] is None:
        return None
    total = integer(usage.get("input_tokens"))
    cached = integer(usage.get("cached_input_tokens"))
    if not total or cached > total:
        return None
    return 100 * cached / total


class ContextUsage:
    """Last reported request input, not cumulative billing or a compression threshold.

    Keeps only a numeric sample and the latest explicit compaction event. The
    window and model always travel with that sample, including zero-delta rows.
    """
    def __init__(self):
        self.sample = None
        self.compacted_at = None

    @staticmethod
    def number(value):
        return value if type(value) in (int, float) and 0 <= value <= 2**53 and math.isfinite(value) else None

    def feed(self, event, model):
        at = timestamp_ms(event.get("timestamp"))
        if at is None:
            return
        payload = event.get("payload") or {}
        if not isinstance(payload, dict):
            return
        if event.get("type") == "compacted" or (
                event.get("type") == "event_msg" and payload.get("type") == "context_compacted"):
            self.compacted_at = max(self.compacted_at or 0, at)
            return
        if event.get("type") != "event_msg" or payload.get("type") != "token_count":
            return
        info = payload.get("info")
        if not isinstance(info, dict) or not isinstance(info.get("last_token_usage"), dict):
            return
        if self.sample and at < self.sample["sample_at_ms"]:
            return
        used = self.number(info["last_token_usage"].get("input_tokens"))
        window = self.number(info.get("model_context_window"))
        self.sample = {"input_tokens": used, "window_tokens": window if window else None,
                       "sample_at_ms": at, "sample_model": model,
                       "method": "last-request-input"}

    def snapshot(self, model):
        result = dict(self.sample or {})
        used, window = result.get("input_tokens"), result.get("window_tokens")
        result.update({"percent_used": used / window * 100 if used is not None and window else None,
                       "stale_model": bool(self.sample and self.sample["sample_model"] != model),
                       "compacted_at_ms": self.compacted_at,
                       "pending_compaction": bool(self.compacted_at and
                           self.compacted_at > result.get("sample_at_ms", 0))})
        return result


class Measurements:
    """One thread's samples, with generation timing separated from tool execution."""

    def __init__(self, model: str | None = None):
        self.model = model
        self.turn_id: str | None = None
        self.last: dict | None = None
        self.model_start: float | None = None
        self.request_boundary: float | None = None
        self.pending_calls: set[str] = set()
        self.stage = "idle"
        # A tail can begin halfway through historical output. Only lifecycle events
        # establish whether a turn is active; tool/token records cannot prove it.
        self.turn_active: bool | None = None
        self.output_total = 0
        self.reasoning_total = 0
        self.turn_output = 0
        self.turn_generation_ms = 0.0
        self.sample_count = 0
        self.recent_response_ids: collections.deque[str] = collections.deque(maxlen=256)
        self.last_usage_fingerprint: tuple | None = None
        self.legacy_total: tuple | None = None
        self.expected_legacy: tuple | None = None
        self.last_usage_at: float = 0
        self.event_at: float = 0
        self.turn_started_at: float | None = None
        self.response_durations: collections.deque[dict] = collections.deque(maxlen=20)
        self.speed_samples: collections.deque[dict] = collections.deque(maxlen=256)
        self.live_speed = LiveSpeed()
        self.file_created_at = None
        self.agent_path = "/root"
        self.session_id = None
        self.subagent_start_ordinal = None
        # A separate replay can retain response timing across a truncated tail,
        # without adding historical usage to the live token counters.
        self.response_history: Measurements | None = None
        self.context = ContextUsage()

    def feed(self, event: dict) -> None:
        if self.response_history is not None:
            self.response_history.feed(event)
        kind = event.get("type")
        p = event.get("payload") or {}
        if not isinstance(p, dict):
            return
        at = timestamp_ms(event.get("timestamp"))
        event_type = p.get("type")
        if kind == "session_meta":
            if self.file_created_at is None:
                self.file_created_at = at
                self.agent_path = p.get("agent_path") or "/root"
                self.session_id = p.get("id") if isinstance(p.get("id"), str) else None
                ordinal = p.get("subagent_history_start_ordinal")
                if type(ordinal) is int and 0 <= ordinal <= 2**63 - 1:
                    self.subagent_start_ordinal = ordinal
            return
        if self.file_created_at is not None and at is not None and at < self.file_created_at - 2000:
            return  # Copied pre-fork history cannot become fresh speed or activity.
        ordinal = event.get("ordinal")
        if (self.subagent_start_ordinal is not None and type(ordinal) is int and
                0 <= ordinal < self.subagent_start_ordinal):
            return  # Paginated sub-agent context may carry fresh timestamps.
        if at is not None:
            self.event_at = max(self.event_at, at)

        self.context.feed(event, self.model)

        if kind == "turn_context":
            self.model = p.get("model") or self.model
            return
        if kind == "event_msg" and event_type in ("task_started", "turn_started"):
            if self.turn_active and p.get("turn_id") and self.turn_id == p.get("turn_id"):
                return  # Repeated start notifications must not shorten a task.
            self.turn_id = p.get("turn_id")
            self.turn_active = True
            self.turn_started_at = at
            self.request_boundary = at
            self.model_start = None
            self.pending_calls.clear()
            self.stage = "generating"
            self.turn_output = 0
            self.turn_generation_ms = 0
            self.sample_count = 0
            return
        if kind == "event_msg" and event_type in (
                "task_complete", "task_completed", "turn_complete", "turn_completed", "task_aborted", "turn_aborted"):
            if p.get("turn_id") and self.turn_id and p["turn_id"] != self.turn_id:
                return  # A delayed completion from an older turn cannot end the current one.
            self.turn_active = False
            self.stage = "idle"
            self.pending_calls.clear()
            self.request_boundary = None
            return

        if kind == "event_msg" and event_type == "user_message":
            if self.model_start is None and self.turn_active is not False:
                self.request_boundary = at
                self.stage = "generating"
            return

        if kind == "event_msg" and event_type == "item_completed":
            item = p.get("item") or {}
            if not isinstance(item, dict):
                return
            model_item = str(item.get("type", "")).replace("_", "").lower()
            if model_item in ("reasoning", "agentmessage", "assistantmessage"):
                start = p.get("started_at_ms")
                if isinstance(start, (int, float)) and at is not None and 0 < start <= at:
                    # Item timings can arrive late; never attach an old item to the next response.
                    if start >= self.last_usage_at:
                        self.model_start = min(self.model_start or start, start)
                        if self.turn_active is not False:
                            self.stage = "generating"
            return

        if kind == "response_item":
            call_id = p.get("call_id")
            if event_type in ("function_call", "custom_tool_call") and call_id:
                if self.turn_active is not False:
                    self.pending_calls.add(call_id)
                    self.stage = "tools"
            elif event_type in ("function_call_output", "custom_tool_call_output"):
                self.pending_calls.discard(call_id)
                if not self.pending_calls and self.turn_active is not False:
                    self.request_boundary = at
                    self.stage = "generating"
            elif event_type == "message" and p.get("role") in ("user", "developer", "system"):
                if self.model_start is None and self.turn_active is not False:
                    self.request_boundary = at
                    self.stage = "generating"
            elif event_type == "agent_message" and p.get("recipient") == self.agent_path:
                if self.model_start is None:
                    self.request_boundary = at
            elif event_type == "reasoning" or (event_type == "message" and p.get("role") == "assistant"):
                if self.turn_active is not False:
                    self.model_start = self.model_start or at
                    self.stage = "generating"
            return

        if kind == "token_usage_record":
            response_id = p.get("response_id")
            if response_id and response_id in self.recent_response_ids:
                return
            if response_id:
                self.recent_response_ids.append(response_id)
            usage = p.get("usage")
            if isinstance(usage, dict):
                self._sample(usage, at, "response", p.get("thread_token_usage"), p.get("turn_token_usage"), response_id)
                self.expected_legacy = self._fingerprint(usage)
            return

        if kind == "event_msg" and event_type == "token_count":
            info = p.get("info") or {}
            if not isinstance(info, dict):
                return
            usage = info.get("last_token_usage")
            total = info.get("total_token_usage") or {}
            if isinstance(usage, dict) and at is not None:
                fp = self._fingerprint(usage)
                # The two streams have DIFFERENT cumulative totals after resume/compaction.
                # Compare legacy totals to legacy totals, never to billing-record totals.
                cumulative = self._fingerprint(total) if isinstance(total, dict) and total else None
                repeated_total = cumulative is not None and cumulative == self.legacy_total
                self.legacy_total = cumulative
                record_duplicate = fp == self.expected_legacy
                if record_duplicate:
                    self.expected_legacy = None
                if not repeated_total and not record_duplicate:
                    self.expected_legacy = None
                    self._sample(usage, at, "legacy", total, None)

    @staticmethod
    def _fingerprint(usage: dict) -> tuple:
        return tuple(integer(usage.get(k)) for k in (
            "input_tokens", "cached_input_tokens", "output_tokens", "reasoning_output_tokens"))

    def _sample(self, usage: dict, at: float | None, source: str,
                total: dict | None, turn: dict | None, response_id: str | None = None) -> None:
        if at is None:
            return
        output = integer(usage.get("output_tokens"))
        stream_duration = at - self.model_start if self.model_start is not None else None
        response_start = self.request_boundary
        response_ms = at - response_start if response_start is not None else None
        valid = speed_interval(response_start, at)
        duration = valid[1] - valid[0] if valid else None
        basis = "request" if valid else "unmeasured"
        if self.turn_active is not False and output and response_ms is not None and math.isfinite(response_ms) and response_ms > 0:
            self.response_durations.append({"duration_ms": response_ms, "completed_at_ms": at})
        rate = output * 1000 / duration if duration is not None and output else None
        if valid and output:
            self.speed_samples.append({
                "id": response_id or str(at) + ":" + str(self._fingerprint(usage)),
                "global_id": ("response:" + str(response_id)) if response_id else (
                    "legacy:" + self.session_id + ":" + str(at) + ":" + str(self._fingerprint(usage))
                    if self.session_id else None),
                "start_ms": valid[0], "end_ms": valid[1], "output_tokens": output,
                "input_tokens": integer(usage.get("input_tokens")),
                "cached_input_tokens": integer(usage.get("cached_input_tokens")),
                "cache_valid": cache_percent(usage) is not None, "model": self.model})
        self.last = {
            "rate": rate, "duration_ms": duration if duration is not None and duration > 0 else None,
            "basis": basis, "stream_duration_ms": stream_duration, "request_start_ms": response_start, "input_tokens": integer(usage.get("input_tokens")),
            "cached_input_tokens": integer(usage.get("cached_input_tokens")),
            "output_tokens": output, "reasoning_output_tokens": integer(usage.get("reasoning_output_tokens")),
            "cache_percent": cache_percent(usage), "measured_at_ms": at, "model": self.model,
            "source": source, "turn_id": self.turn_id,
        }
        if isinstance(total, dict):
            self.output_total = integer(total.get("output_tokens"))
            self.reasoning_total = integer(total.get("reasoning_output_tokens"))
        else:
            self.output_total += output
        if isinstance(turn, dict):
            self.turn_output = integer(turn.get("output_tokens"))
        else:
            self.turn_output += output
        if rate is not None:
            self.turn_generation_ms += duration
            self.sample_count += 1
        self.last_usage_fingerprint = self._fingerprint(usage)
        self.last_usage_at = at
        self.model_start = None
        self.request_boundary = at
        if self.turn_active is not False:
            self.stage = "tools"

    def snapshot(self, now_ms=None) -> dict:
        durations = self.response_history.response_durations if self.response_history is not None else self.response_durations
        context = self.context
        if self.response_history and not context.sample:
            context = self.response_history.context
        return {"model": self.model, "stage": self.stage, "last": self.last,
                "context": context.snapshot(self.model),
                "live_speed": self.live_speed.compute(self, time.time() * 1000 if now_ms is None else now_ms),
                "response_duration": {
                    "mean_ms": sum(row["duration_ms"] for row in durations) / len(durations) if durations else None,
                    "count": len(durations), "limit": 20,
                    "last_ms": durations[-1]["duration_ms"] if durations else None,
                    "samples": [dict(row) for row in durations]},
                "turn_active": self.turn_active,
                "turn_id": self.turn_id, "turn_output_tokens": self.turn_output,
                "sample_count": self.sample_count, "output_total": self.output_total,
                "turn_started_at_ms": self.turn_started_at,
                "last_event_at_ms": self.event_at}


def reverse_small_lines(stream, end: int, chunk_bytes: int = 64 * 1024,
                        line_limit: int = 256 * 1024, include_gaps: bool = False):
    """Read prefix lines newest-first with bounded memory, skipping giant media rows."""
    remainder = b""
    position = end
    while position:
        size = min(position, chunk_bytes)
        position -= size
        stream.seek(position)
        parts = stream.read(size).split(b"\n")
        if len(parts) == 1:
            if remainder is not None:
                remainder = parts[0] + remainder
                if len(remainder) > line_limit:
                    remainder = None
            continue
        if remainder is not None and len(parts[-1]) + len(remainder) <= line_limit:
            yield parts[-1] + remainder
        elif include_gaps:
            yield None
        for line in reversed(parts[1:-1]):
            if len(line) <= line_limit:
                yield line
            elif include_gaps:
                yield None
        remainder = parts[0]
    if remainder and len(remainder) <= line_limit:
        yield remainder
    elif include_gaps and (remainder is None or remainder):
        yield None


class SessionTail:
    def __init__(self, path: str, model: str | None = None, history_bytes: int = 8 * 1024 * 1024):
        self.path = Path(path)
        self.measurements = Measurements(model)
        self.offset = 0
        self.partial = b""
        self.history_bytes = history_bytes
        self.initialized = False
        self.identity: tuple | None = None
        self.anchor = b""
        self.source_state = "waiting"
        self.file_size = 0
        self.file_mtime_ms: float | None = None
        self.mtime_ns: int | None = None
        self.live_quota = None

    def reset(self) -> None:
        self.measurements = Measurements(self.measurements.model)
        self.offset = 0
        self.partial = b""
        self.initialized = False
        self.anchor = b""
        self.live_quota = None

    def restore_lifecycle(self, stream, end: int) -> None:
        """Recover explicit turn state before the sample tail, without replaying usage."""
        starts = ("task_started", "turn_started")
        finishes = ("task_complete", "task_completed", "turn_complete", "turn_completed",
                    "task_aborted", "turn_aborted")
        endings = {}
        for line in reverse_small_lines(stream, end):
            if b'"event_msg"' not in line:
                continue
            try:
                record = json.loads(line)
            except (ValueError, UnicodeDecodeError):
                continue
            if not isinstance(record, dict) or record.get("type") != "event_msg":
                continue
            payload = record.get("payload")
            if not isinstance(payload, dict):
                continue
            kind = payload.get("type")
            turn_id = payload.get("turn_id")
            if turn_id is not None and not isinstance(turn_id, str):
                continue
            if kind in finishes:
                endings.setdefault(turn_id, record)
            elif kind in starts:
                self.measurements.feed(record)
                # A late completion for an older turn must not end this turn.
                ending = next((e for key, e in endings.items() if not key or not turn_id or key == turn_id), None)
                if ending:
                    self.measurements.feed(ending)
                break
        else:
            if endings:
                self.measurements.feed(next(iter(endings.values())))
        # The skipped prefix establishes lifecycle only. It cannot establish a
        # response timing boundary or contribute historical token counts.
        self.measurements.request_boundary = None
        self.measurements.model_start = None

    def restore_response_history(self, stream, end: int) -> None:
        """Restore up to 20 timed responses using small metadata rows only.

        Read more history if the first batch has missing timings or duplicates.
        Usage replay is isolated from the live token/speed counters.
        """
        records = []
        usage_rows = 0
        history = None
        def replay():
            result = Measurements(self.measurements.model)
            result.file_created_at = self.measurements.file_created_at
            result.agent_path = self.measurements.agent_path
            result.session_id = self.measurements.session_id
            result.subagent_start_ordinal = self.measurements.subagent_start_ordinal
            for row in reversed(records):
                if row is None:
                    # An oversized row may be a tool result or user message.
                    # Do not bridge an unknown timing boundary with a guess.
                    result.request_boundary = None
                    result.model_start = None
                    result.pending_calls.clear()
                else:
                    result.feed(row)
            return result

        # Tool results commonly carry multi-MB images. Preserve their timing
        # boundaries while bounding each decoded row, then discard all bodies.
        for line in reverse_small_lines(stream, end, line_limit=16 * 1024 * 1024, include_gaps=True):
            if line is None:
                records.append(None)
                continue
            try:
                record = json.loads(line)
            except (ValueError, UnicodeDecodeError):
                continue
            if not isinstance(record, dict):
                continue
            kind, payload = record.get("type"), record.get("payload")
            if kind not in ("event_msg", "response_item", "token_usage_record", "turn_context", "compacted") or not isinstance(payload, dict):
                continue
            event_type = payload.get("type")
            if kind == "event_msg" and event_type not in (
                    "task_started", "turn_started", "task_complete", "task_completed", "turn_complete",
                    "turn_completed", "task_aborted", "turn_aborted", "item_completed", "token_count", "context_compacted"):
                continue
            if kind == "response_item" and event_type not in (
                    "function_call", "custom_tool_call", "function_call_output", "custom_tool_call_output", "message"):
                continue
            # Retain timing metadata, never message bodies or tool output.
            slim = {k: payload[k] for k in ("type", "turn_id", "call_id", "role", "response_id", "usage",
                    "thread_token_usage", "turn_token_usage", "info", "started_at_ms", "model") if k in payload}
            if isinstance(payload.get("item"), dict):
                slim["item"] = {"type": payload["item"].get("type")}
            records.append({"type": kind, "timestamp": record.get("timestamp"), "payload": slim,
                            "ordinal": record.get("ordinal")})
            if kind == "token_usage_record" or event_type == "token_count":
                usage_rows += 1
            else:
                continue
            if usage_rows % 64 == 0:
                history = replay()
                if len(history.response_durations) == 20:
                    break
        else:
            history = replay()
        self.measurements.response_history = history or Measurements()

    def update(self) -> None:
        live_read = self.initialized
        try:
            stat = self.path.stat()
            size = stat.st_size
            identity = (stat.st_dev, stat.st_ino)
            if size < self.offset or (self.identity is not None and identity != self.identity):
                self.reset()
            self.identity = identity
            self.file_size = size
            self.file_mtime_ms = stat.st_mtime * 1000
            self.source_state = "ok"
            unchanged = self.mtime_ns == stat.st_mtime_ns
            self.mtime_ns = stat.st_mtime_ns
            if self.initialized and size == self.offset and unchanged:
                return
            if self.initialized and size == self.offset and not unchanged:
                self.reset()  # Same-length rewrites can leave the last 64 payload bytes unchanged.
            # A chat left unwatched for days can have hundreds of MB of new logs.
            # Jump to recent history rather than publishing old samples while replaying it.
            if self.initialized and size - self.offset > 16 * 1024 * 1024:
                self.reset()
            with self.path.open("rb") as f:
                if self.initialized and self.anchor:
                    f.seek(max(0, self.offset - len(self.anchor)))
                    if f.read(len(self.anchor)) != self.anchor:
                        self.reset()  # Same file was rewritten and grew before the next poll.
                if not self.initialized:
                    live_read = False
                    # Forks can prepend copied records whose timestamps predate this
                    # file's creation. Recover that guard even when starting at its tail.
                    f.seek(0)
                    try:
                        metadata = json.loads(f.readline(256 * 1024))
                        if isinstance(metadata, dict) and metadata.get("type") == "session_meta":
                            self.measurements.feed(metadata)
                    except (ValueError, UnicodeDecodeError):
                        pass
                    self.offset = max(0, size - self.history_bytes)
                    f.seek(self.offset)
                    if self.offset:
                        f.readline()  # discard an incomplete first JSON line
                        self.offset = f.tell()
                        self.restore_lifecycle(f, self.offset)
                        self.restore_response_history(f, self.offset)
                    self.initialized = True
                f.seek(self.offset)
                data = f.read(16 * 1024 * 1024)
                self.offset = f.tell()
                f.seek(max(0, self.offset - 64))
                self.anchor = f.read(min(64, self.offset))
                self.source_state = "ok" if self.offset >= size else "catching_up"
        except OSError:
            self.source_state = "unavailable"
            return
        if not data:
            return
        lines = (self.partial + data).split(b"\n")
        self.partial = lines.pop()
        for line in lines:
            if not line.strip():
                continue
            try:
                event = json.loads(line)
            except (ValueError, UnicodeDecodeError):
                continue
            if isinstance(event, dict):
                self.measurements.feed(event)
                payload = event.get('payload')
                if (live_read and event.get('type') == 'event_msg' and isinstance(payload, dict)
                        and payload.get('type') == 'token_count'):
                    from quota import normalize_log_limits
                    windows = normalize_log_limits(payload.get('rate_limits'))
                    at = timestamp_ms(event.get('timestamp'))
                    if windows and at and (not self.live_quota or at > self.live_quota[1]):
                        self.live_quota = (windows, at)

    def source(self) -> dict:
        return {"path": str(self.path), "state": self.source_state, "size": self.file_size,
                "offset": self.offset, "modified_at_ms": self.file_mtime_ms}


class TailCache:
    """Cache by thread, but rebind whenever its active rollout path changes."""

    def __init__(self, maximum: int = 8):
        self.tails: collections.OrderedDict[str, SessionTail] = collections.OrderedDict()
        self.maximum = maximum

    def update(self, row: dict, force: bool = False) -> SessionTail | None:
        path = row.get("rollout_path")
        if not path:
            return None
        key = row["id"]
        tail = self.tails.get(key)
        if force or tail is None or os.path.normcase(str(tail.path)) != os.path.normcase(str(Path(path))):
            tail = SessionTail(path, row.get("model"))
            self.tails[key] = tail
        self.tails.move_to_end(key)
        tail.update()
        while len(self.tails) > self.maximum:
            self.tails.popitem(last=False)
        return tail


class GlobalCollector:
    """Hot logs only; bounded readers and staggered initial replay keep RAM/IO low."""
    def __init__(self, maximum=64):
        self.maximum = maximum
        self.tails = {}
        self.speed = GlobalSpeed()

    def snapshot(self, rows, now_ms):
        candidates = sorted((r for r in rows if r.get('rollout_path') and
                             now_ms - r.get('updated_at_ms', 0) <= 300_000),
                            key=lambda r: (-r.get('updated_at_ms', 0), r['id']))
        wanted = {r['id']: r for r in candidates[:self.maximum]}
        self.tails = {aid: tail for aid, tail in self.tails.items() if aid in wanted}
        started = 0
        agents = []
        complete = len(candidates) <= self.maximum
        for aid, row in wanted.items():
            tail = self.tails.get(aid)
            if tail is None or os.path.normcase(str(tail.path)) != os.path.normcase(row['rollout_path']):
                if started >= 2:
                    complete = False
                    continue
                tail = SessionTail(row['rollout_path'], row.get('model'), history_bytes=256 * 1024)
                self.tails[aid] = tail
                started += 1
            tail.update()
            if tail.source_state == 'ok':
                agents.append((aid, tail.measurements))
            else:
                complete = False
        return self.speed.compute(agents, now_ms, complete)


class ActivitySelector:
    """Follow the newest started user task, not competing background writes."""
    def __init__(self):
        self.selected_id = None

    def choose(self, rows, tails, now_ms):
        eligible = [r for r in rows if r.get("model") != "codex-auto-review" and not r.get("is_subagent")]
        candidates = eligible[:8]
        previous = next((r for r in eligible if r.get("id") == self.selected_id), None)
        if previous is not None and previous not in candidates:
            candidates.append(previous)
        started = []
        for row in candidates:
            tail = tails.update(row)
            start = tail.measurements.turn_started_at if tail else None
            if isinstance(start, (int, float)) and not isinstance(start, bool) and math.isfinite(start) and 0 < start <= now_ms + 30000:
                started.append((row, start))
        if started:
            newest_start = max(start for _, start in started)
            matches = [row for row, start in started if start == newest_start]
        else:
            newest_activity = max((r["updated_at_ms"] for r in candidates), default=None)
            matches = [r for r in candidates if r["updated_at_ms"] == newest_activity]
        if len(matches) > 1:
            previous_match = next((r for r in matches if r["id"] == self.selected_id), None)
            if previous_match is not None:
                matches = [previous_match]
            else:
                newest_activity = max(r["updated_at_ms"] for r in matches)
                matches = [r for r in matches if r["updated_at_ms"] == newest_activity]
        self.selected_id = matches[0]["id"] if len(matches) == 1 else None
        return matches


class ThreadRepository:
    def __init__(self, codex_home: Path):
        self.codex_home = codex_home.expanduser().resolve()
        self.database: Path | None = None
        self.state = "missing"
        self.columns: set[str] = set()
        self.rows: dict[str, dict] = {}

    def _connect(self):
        if self.database is None:
            raise sqlite3.OperationalError("No compatible database")
        return sqlite3.connect(self.database.as_uri() + "?mode=ro", uri=True, timeout=0.5)

    def _select(self) -> str:
        title = "COALESCE(NULLIF(name, ''), title)" if "name" in self.columns else "title"
        optional = [c if c in self.columns else f"NULL AS {c}" for c in ("model", "updated_at", "archived")]
        return f"SELECT id, {title} AS title, rollout_path, {', '.join(optional)} FROM threads"

    def _discover(self) -> None:
        # Codex can migrate to a new state_N.sqlite. Inspect only its thread schema.
        candidates = []
        for path in self.codex_home.glob("state_*.sqlite"):
            suffix = path.stem.removeprefix("state_")
            if suffix.isdigit():
                candidates.append((int(suffix), path))
        self.database = None
        self.columns = set()
        self.state = "unsupported" if candidates else "missing"
        # Never fall back to a retained old DB after an incompatible newer migration.
        for _, path in sorted(candidates, reverse=True)[:1]:
            try:
                with closing(sqlite3.connect(path.as_uri() + "?mode=ro", uri=True, timeout=0.5)) as conn:
                    columns = {r[1] for r in conn.execute("PRAGMA table_info(threads)")}
                if {"id", "title", "rollout_path"} <= columns:
                    self.database, self.columns, self.state = path, columns, "ok"
                    return
            except (OSError, sqlite3.Error):
                self.state = "unavailable"

    def refresh(self) -> None:
        self._discover()
        try:
            with closing(self._connect()) as conn:
                conn.row_factory = sqlite3.Row
                rows = conn.execute(self._select() + " ORDER BY updated_at DESC LIMIT 512").fetchall()
            self.rows = {row["id"]: dict(row) for row in rows}
        except (OSError, sqlite3.Error):
            self.rows = {}
            if self.database is not None:
                self.state = "unavailable"

    def title_matches(self, title: str) -> list[dict]:
        # Exact matching across the entire DB also covers old chats and duplicate titles.
        try:
            with closing(self._connect()) as conn:
                conn.row_factory = sqlite3.Row
                title_column = "COALESCE(NULLIF(name, ''), title)" if "name" in self.columns else "title"
                rows = conn.execute(
                    self._select() + f" WHERE {title_column} = ? ORDER BY updated_at DESC", (title,)).fetchall()
            return [dict(row) for row in rows]
        except (OSError, sqlite3.Error):
            return [r for r in self.rows.values() if r["title"] == title]


def read_selection(path: Path) -> dict:
    try:
        value = json.loads(path.read_text("utf-8-sig"))
        return value if isinstance(value, dict) else {}
    except (OSError, ValueError):
        return {}


def atomic_json(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, allow_nan=False), encoding="utf-8")
    for attempt in range(4):
        try:
            temporary.replace(path)
            return
        except PermissionError:
            time.sleep(0.015 * (attempt + 1))


class VisibleConversationResolver:
    """Disambiguate identical titles with hashes of visible, accessible text.

    The bounded read is local and transient. Never persist chat bodies or treat
    recency, archive status or a background writer as proof of the viewed chat.
    """
    def __init__(self):
        self.key = None
        self.selected_id = None
        self.checked = 0.0

    @staticmethod
    def normalized(text):
        return ''.join(c.lower() for c in unicodedata.normalize('NFKC', text)
                       if c.isalnum() and ord(c) <= 65535)

    def choose(self, matches, fingerprints):
        if not isinstance(fingerprints, list) or not 1 <= len(fingerprints) <= 48 or len(matches) > 4:
            return None
        wanted = {h for h in fingerprints if isinstance(h, str) and len(h) == 64 and
                  all(c in '0123456789abcdef' for c in h)}
        if not wanted:
            return None
        key = (tuple(sorted(r['id'] for r in matches)), tuple(sorted(wanted)))
        if key == self.key and time.monotonic() - self.checked < 5:
            return next((r for r in matches if r['id'] == self.selected_id), None)
        scores = {}
        for row in matches:
            hits = set()
            try:
                with open(row['rollout_path'], 'rb') as stream:
                    size = stream.seek(0, 2)
                    start = max(0, size - 2 * 1024 * 1024)
                    stream.seek(start)
                    lines = stream.read().splitlines()
                if start:
                    lines = lines[1:]
                for line in reversed(lines):
                    if len(line) > 256 * 1024:
                        continue
                    try:
                        event = json.loads(line)
                    except (ValueError, UnicodeError):
                        continue
                    if not isinstance(event, dict) or not isinstance(event.get('payload'), dict):
                        continue
                    p = event['payload']
                    texts = []
                    if event.get('type') == 'response_item' and p.get('role') in ('user', 'assistant') and p.get('channel') in (None, 'commentary', 'final'):
                        content = p.get('content')
                        texts = [item.get('text') for item in content if isinstance(item, dict)] if isinstance(content, list) else []
                    elif event.get('type') == 'event_msg' and p.get('type') in ('user_message', 'agent_message'):
                        texts = [p.get('message')]
                    for text in texts:
                        if not isinstance(text, str):
                            continue
                        normalized = self.normalized(text[:16000])
                        for i in range(max(0, len(normalized) - 31)):
                            digest = hashlib.sha256(normalized[i:i + 32].encode()).hexdigest()
                            if digest in wanted:
                                hits.add(digest)
                    if hits == wanted:
                        break
            except OSError:
                pass
            scores[row['id']] = hits
        winners = [row for row in matches if scores[row['id']] - set().union(
            *(scores[r['id']] for r in matches if r['id'] != row['id']))]
        selected = winners[0] if len(winners) == 1 else None
        self.key, self.checked = key, time.monotonic()
        self.selected_id = selected['id'] if selected else None
        return selected


def monitor(codex_home: Path, runtime: Path, parent_pid: int | None = None, once: bool = False,
            dashboard_enabled: bool = False, account_quota: bool = False,
            network_latency: bool = False, reset_radar: bool = False) -> dict | None:
    from history import SessionCatalog
    catalog = SessionCatalog(codex_home)
    repository = catalog.repository
    if dashboard_enabled and not once:
        from dashboard import start_server
        start_server(codex_home, runtime, native_enabled=os.name == "nt")
    tails = TailCache(maximum=10)
    activity_selector = ActivitySelector()
    visible_resolver = VisibleConversationResolver()
    global_collector = GlobalCollector()
    quota = None
    if account_quota and not once:
        from quota import QuotaPoller
        quota = QuotaPoller(codex_home, history_file=runtime / "quota-usage-history.json")
    latency = None
    if network_latency and not once:
        from latency import LatencyPoller
        latency = LatencyPoller()
    radar = None
    if reset_radar and not once:
        from reset_radar import ResetRadarPoller
        previous_radar = read_selection(runtime / "metrics.json").get("reset_radar")
        radar = ResetRadarPoller(interval=180, resume=previous_radar) if isinstance(previous_radar, dict) else ResetRadarPoller(interval=180)
    matched_at = 0.0
    selection_key = None
    last_refresh_id = None
    matches: list[dict] = []
    last_published = 0.0
    last_payload = None
    refresh_result = None
    try:
        while True:
            if parent_pid and os.name == "nt":
                import ctypes
                kernel = ctypes.WinDLL("kernel32", use_last_error=True)
                kernel.OpenProcess.argtypes = (ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong)
                kernel.OpenProcess.restype = ctypes.c_void_p
                kernel.GetExitCodeProcess.argtypes = (ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong))
                kernel.CloseHandle.argtypes = (ctypes.c_void_p,)
                handle = kernel.OpenProcess(0x1000, False, parent_pid)
                if not handle:
                    return None
                code = ctypes.c_ulong()
                alive = kernel.GetExitCodeProcess(handle, ctypes.byref(code)) and code.value == 259
                kernel.CloseHandle(handle)
                if not alive:
                    return None
            elif parent_pid:
                try:
                    os.kill(parent_pid, 0)
                except ProcessLookupError:
                    return None
                except PermissionError:
                    pass
            selection = read_selection(runtime / "selection.json")
            title = selection.get("title", "")
            selected_id = selection.get("thread_id")
            refresh_id = selection.get("refresh_id")
            force = refresh_id != last_refresh_id
            if force and quota:
                quota.refresh()
            if force and latency:
                latency.refresh()
            if force and radar:
                radar.refresh()
            last_refresh_id = refresh_id
            catalog.refresh(force=force or once)
            key = (title, selected_id)
            if key != selection_key or force or time.monotonic() - matched_at >= 0.5 or once:
                matches = catalog.title_matches(title) if title else []
                selection_key = key
                matched_at = time.monotonic()
            selected = next((r for r in matches if r["id"] == selected_id), None)
            if selection.get("binding_mode") == "activity":
                # A different task can keep writing while this one is active.
                # Prefer the most recently started user task so those background
                # writes do not repeatedly steal the live panel.
                matches = activity_selector.choose(list(catalog.rows.values()), tails, time.time() * 1000)
                selected = matches[0] if len(matches) == 1 else None
                title = selected["title"] if selected else ""
            if selection.get("binding_mode") == "manual" and selected_id:
                selected = catalog.rows.get(selected_id)
            if selected is None and len(matches) == 1 and selection.get("binding_mode") != "manual":
                selected = matches[0]
            if selected is None and len(matches) > 1 and selection.get("binding_mode") == "follow":
                selected = visible_resolver.choose(matches, selection.get("view_fingerprints"))
            status = "ok" if selected else "ambiguous" if len(matches) > 1 else "unbound"
            tail = tails.update(selected, force) if selected else None
            if quota and tail and tail.live_quota:
                quota.accept(tail.live_quota[0], tail.live_quota[1] / 1000)
            metrics = tail.measurements.snapshot() if tail else None
            # Recent activity can still describe response timing when the open
            # chat is ambiguous. Keep it separate from the current-chat identity.
            activity_fallback = None
            if selected is None and selection.get("binding_mode") in ("auto", "follow"):
                recent = activity_selector.choose(list(catalog.rows.values()), tails, time.time() * 1000)
                if len(recent) == 1:
                    recent_tail = tails.update(recent[0], force)
                    if recent_tail:
                        activity_fallback = {"binding": "ok", "binding_mode": "activity",
                                             "title": recent[0]["title"], "thread_id": recent[0]["id"],
                                             "source": recent_tail.source(),
                                             "metrics": recent_tail.measurements.snapshot(),
                                             "updated_at_ms": time.time() * 1000}
            global_speed = global_collector.snapshot(list(catalog.rows.values()), time.time() * 1000)
            if force:
                refresh_result = {"request_id": refresh_id, "completed_at_ms": time.time() * 1000,
                                  "state": "ok" if tail and tail.source_state == "ok" else
                                  "unavailable" if tail else status,
                                  "sample_at_ms": tail.measurements.last_usage_at if tail else None}
            saved_range = read_selection(runtime / "range-result.json")
            range_result = saved_range if selected and saved_range.get("thread_id") == selected["id"] else None
            result = {"schema_version": 2, "collector_pid": os.getpid(),
                      "repository_state": repository.state,
                      "binding": status, "binding_mode": selection.get("binding_mode", "auto"),
                      "view_observed_at_ms": selection.get("view_observed_at_ms"),
                      "thread_id": selected["id"] if selected else None,
                      "title": title, "metrics": metrics, "source": tail.source() if tail else None,
                      "activity_fallback": activity_fallback,
                      "global_speed": global_speed,
                      "refresh": refresh_result, "range": range_result,
                      "account_quota": quota.snapshot() if quota else None,
                      "network_latency": latency.snapshot() if latency else None,
                      "reset_radar": radar.snapshot() if radar else None,
                      "known_titles": list(dict.fromkeys(r['title'] for r in catalog.rows.values()
                          if not r.get('is_subagent') and r.get('model') != 'codex-auto-review' and
                          isinstance(r.get('title'), str) and 0 < len(r['title']) <= 160))[:2048],
                      "matches": [{k: r.get(k) for k in ("id", "title", "model", "updated_at")} for r in matches],
                      "threads": [{"id": r["id"], "title": r["title"], "model": r["model"]} for r in list(catalog.rows.values())[:80]]}
            if once:
                result["updated_at_ms"] = time.time() * 1000
                return result
            if result != last_payload or time.monotonic() - last_published >= .5:
                last_payload = result.copy()
                result["updated_at_ms"] = time.time() * 1000
                atomic_json(runtime / "metrics.json", result)
                last_published = time.monotonic()
            time.sleep(0.1)
    finally:
        if quota:
            quota.close()
        if latency:
            latency.close()
        if radar:
            radar.close()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--codex-home", type=Path, default=Path(os.environ.get("CODEX_HOME") or Path.home() / ".codex"))
    parser.add_argument("--runtime", type=Path, default=Path(__file__).resolve().parent / "runtime")
    parser.add_argument("--parent-pid", type=int)
    parser.add_argument("--once", action="store_true")
    parser.add_argument("--dashboard", action="store_true", help="Start the local historical statistics panel")
    parser.add_argument("--account-quota", action="store_true", help="Read account limits via the authenticated local Codex client")
    parser.add_argument("--network-latency", action="store_true", help="Periodically probe the configured local proxy nodes")
    parser.add_argument("--reset-radar", action="store_true", help="Read public quota-reset announcements and schedules")
    args = parser.parse_args()
    result = monitor(args.codex_home, args.runtime, args.parent_pid, args.once, args.dashboard, args.account_quota,
                     args.network_latency, args.reset_radar)
    if result is not None:
        print(json.dumps(result, ensure_ascii=False, allow_nan=False))


if __name__ == "__main__":
    main()
