"""Read public reset announcements; never infer a reset or redeem an account credit.

Only two fixed, anonymous HTTPS GET endpoints are used. No local account,
quota, identity or conversation data is read or sent. A schedule is actionable
only while the publisher's generatedAt metadata is fresh, not its query time.
"""
from __future__ import annotations

import copy
from dataclasses import dataclass
from datetime import datetime
from email.utils import parsedate_to_datetime
import http.client
import json
import math
import os
import re
import socket
import ssl
import sys
import threading
import time
from urllib.parse import urlsplit
import urllib.request


HOST = "didcodexreset.com"
STATUS_PATH = "/openapi/v1/status?lang=zh-CN&timeZone=Asia%2FShanghai"
META_PATH = "/openapi/v1/records/latest?kind=reset_completed"
INTERVAL_SECONDS = 180
MIN_REFRESH_SECONDS = 60
SOURCE_MAX_AGE_SECONDS = 30 * 60
CLOCK_SKEW_SECONDS = 120
TIMEOUT_SECONDS = 8
MAX_RESPONSE_BYTES = 512 * 1024
RESET_TYPES = {"global", "banked", "global_and_banked"}


class RadarError(Exception):
    """Only fixed reason codes, never response bodies or headers, reach the UI."""
    def __init__(self, code: str, retry_after: float = 0):
        super().__init__(code)
        self.retry_after = retry_after


def _timestamp(value) -> int | None:
    if not isinstance(value, str) or len(value) > 64:
        return None
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            return None
        stamp = parsed.timestamp() * 1000
        return int(stamp) if math.isfinite(stamp) and stamp > 0 else None
    except (ValueError, OverflowError, OSError):
        return None


def _number(value) -> float | None:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        return None
    return float(value)


def _retry_after(headers: dict, payload=None, now_ms=None) -> float:
    now_ms = time.time() * 1000 if now_ms is None else now_ms
    values = []
    value = headers.get("retry-after")
    if isinstance(value, str):
        try:
            seconds = float(value)
            if math.isfinite(seconds) and seconds >= 0:
                values.append(seconds)
        except ValueError:
            try:
                stamp = parsedate_to_datetime(value).timestamp() * 1000
                values.append(max(0, (stamp - now_ms) / 1000))
            except (ValueError, TypeError, OverflowError):
                pass
    seconds = _number(payload.get("retryAfter")) if isinstance(payload, dict) else None
    if seconds is not None and seconds >= 0:
        values.append(seconds)
    return max([MIN_REFRESH_SECONDS] + values) if values else 3600


def _budget_wait(headers: dict, now_ms: float) -> float:
    """Only exhausted budgets defer a request; reset is earliest slot recovery."""
    try:
        exhausted = int(headers.get("x-ratelimit-remaining", "1")) <= 0
    except (ValueError, TypeError):
        exhausted = False
    if not exhausted:
        return 0
    reset = _timestamp(headers.get("x-ratelimit-reset"))
    return max(MIN_REFRESH_SECONDS, (reset - now_ms) / 1000) if reset else 3600


def _https_proxy() -> str | None:
    """Honor HTTPS proxy/bypass settings, including the Windows system proxy.

    urllib's Windows discovery ignores the registry whenever ANY *_proxy
    environment variable exists, including unrelated applications' variables.
    Merge settings by scheme so those variables cannot force this host direct.
    """
    environment = urllib.request.getproxies_environment()
    if sys.platform == "win32":
        proxies = urllib.request.getproxies_registry()
        proxies.update(environment)
        bypass = (urllib.request.proxy_bypass_environment(HOST, environment)
                  if "no" in environment else urllib.request.proxy_bypass_registry(HOST))
    else:
        proxies = urllib.request.getproxies()
        bypass = urllib.request.proxy_bypass(HOST)
    # An explicitly empty HTTPS_PROXY disables the inherited system proxy.
    https_keys = [key for key in os.environ if key.lower() == "https_proxy"]
    if https_keys and "https" not in environment:
        return None
    return None if bypass else proxies.get("https")


def _https_connection(timeout: float):
    proxy = _https_proxy()
    context = ssl.create_default_context()
    if not proxy:
        return http.client.HTTPSConnection(HOST, timeout=timeout, context=context)
    try:
        parsed = urlsplit(proxy if "://" in proxy else "http://" + proxy)
        port = parsed.port if parsed.port is not None else 80
        if (parsed.scheme != "http" or not parsed.hostname or parsed.username is not None or
                parsed.password is not None or parsed.path not in ("", "/") or parsed.query or
                parsed.fragment or not 1 <= port <= 65535 or any(ord(char) < 33 for char in proxy)):
            raise ValueError("Unsupported proxy")
    except (TypeError, ValueError):
        raise RadarError("proxy_unsupported") from None
    connection = http.client.HTTPSConnection(parsed.hostname, port, timeout=timeout, context=context)
    # CONNECT carries no account data; TLS still authenticates the fixed host.
    connection.set_tunnel(HOST, 443)
    return connection


def request_json(path: str, timeout: float = TIMEOUT_SECONDS) -> tuple[dict, dict]:
    """Fixed host and GET paths, verified TLS, system proxy, no redirects or credentials."""
    if path not in (STATUS_PATH, META_PATH):
        raise RadarError("invalid_endpoint")
    connection = None
    try:
        connection = _https_connection(timeout)
        connection.request("GET", path, headers={
            "Accept": "application/json", "User-Agent": "CodexTokenHud-PublicResetRadar/1.0",
            "Accept-Encoding": "identity", "Connection": "close"})
        response = connection.getresponse()
        headers = {key.lower(): value for key, value in response.getheaders()
                   if key.lower() in ("retry-after", "x-ratelimit-remaining", "x-ratelimit-reset")}
        raw = response.read(MAX_RESPONSE_BYTES + 1)
        if len(raw) > MAX_RESPONSE_BYTES:
            if response.status == 429:
                raise RadarError("rate_limited", _retry_after(headers))
            raise RadarError("response_too_large")
        try:
            payload = json.loads(raw)
        except (ValueError, UnicodeError):
            payload = None
        if response.status == 429:
            raise RadarError("rate_limited", _retry_after(headers, payload))
        if response.status != 200:
            raise RadarError("source_unavailable" if response.status >= 500 else "request_rejected")
        if not isinstance(payload, dict) or payload.get("ok") is not True:
            raise RadarError("invalid_response")
        return payload, headers
    except RadarError:
        raise
    except (TimeoutError, socket.timeout):
        raise RadarError("request_timeout") from None
    except (OSError, http.client.HTTPException):
        raise RadarError("request_failed") from None
    finally:
        if connection is not None:
            connection.close()


def _empty(state="loading", reason=None) -> dict:
    return {"state": state, "fetched_at_ms": None, "source_updated_at_ms": None,
            "upcoming": None, "latest_events": [], "reason": reason, "next_check_at_ms": None}


def _identity(record, kind: str) -> bool:
    return (isinstance(record, dict) and record.get("kind") == kind and
            record.get("resetType") in RESET_TYPES and isinstance(record.get("id"), str) and
            re.fullmatch(r"[A-Za-z0-9_.:-]{1,128}", record["id"]) is not None)


def _url(record: dict) -> str:
    value = record.get("detailUrl")
    if isinstance(value, str) and len(value) <= 2048 and not any(ord(c) < 32 for c in value):
        try:
            parsed = urlsplit(value)
            if (parsed.scheme == "https" and parsed.hostname == HOST and
                    parsed.port in (None, 443) and not parsed.username and not parsed.password):
                return value
        except ValueError:
            pass
    return "https://didcodexreset.com/zh.html"


def _scope(record: dict) -> dict:
    raw = record.get("scope")
    raw = raw if isinstance(raw, dict) else {}
    result = {}
    for key in ("plans", "windows"):
        values = raw.get(key)
        result[key] = list(dict.fromkeys(value.lower() for value in values[:16]
            if isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9_ -]{1,32}", value))) if isinstance(values, list) else []
    return result


def _relevant(scope: dict) -> bool:
    # This HUD's reminder is for Pro weekly quota. Unspecified scope remains a
    # public signal; a clearly different plan/window must not suggest eligibility.
    plans, windows = set(scope["plans"]), set(scope["windows"])
    generic = {"unknown", "all", "all_plans", "all_windows"}
    if plans and not plans.intersection(generic | {"pro", "pro_5x", "pro_20x", "pro5x", "pro20x"}):
        return False
    if windows and not windows.intersection(generic | {"weekly", "week", "seven_day"}):
        return False
    return True


def _completion(record, now_ms: float) -> dict | None:
    if not _identity(record, "reset_completed"):
        return None
    stamp, basis = None, None
    for key, label in (("completedAt", "completed"), ("effectiveAt", "effective"), ("announcedAt", "announced")):
        stamp = _timestamp(record.get(key))
        if stamp is not None:
            basis = label
            break
    if stamp is None or stamp > now_ms + CLOCK_SKEW_SECONDS * 1000:
        return None
    return {"id": record["id"], "kind": "reset_completed", "reset_type": record["resetType"],
            "at_ms": stamp, "time_basis": basis, "url": _url(record), "scope": _scope(record)}


def _schedule(record, now_ms: float) -> tuple[dict | None, str | None]:
    if record is None:
        return None, None
    if not _identity(record, "reset_scheduled"):
        return None, "invalid_schedule"
    if record.get("scheduleState") != "pending" or _timestamp(record.get("completedAt")):
        return None, "schedule_unconfirmed"
    scope = _scope(record)
    if not _relevant(scope):
        return None, "schedule_other_scope"
    window = record.get("scheduleWindow")
    if not isinstance(window, dict):
        return None, "schedule_time_unknown"
    start, end = _timestamp(window.get("startAt")), _timestamp(window.get("endAt"))
    if start is None or end is None or end < start:
        return None, "invalid_schedule"
    declared = record.get("schedulePrecision")
    if declared == "date" and start < end:
        precision = "date"
    elif declared == "datetime" and start == end:
        precision = "exact"
    elif start < end and declared == "datetime":
        precision = "window"
    else:
        precision = "unknown"
    basis = record.get("scheduleBasis")
    basis = basis if basis in ("explicit", "contextual_inference") else None
    timing = record.get("scheduleTimingSource")
    timing = timing if timing in ("explicit", "fallback") else None
    constraint = "deadline" if record.get("scheduleConstraint") == "deadline" else None
    # Keep the publisher's prediction visible, with its actual timing semantics.
    if constraint == "deadline" and declared == "datetime":
        precision = "deadline"
    elif precision == "exact" and (basis != "explicit" or timing == "fallback"):
        precision = "estimated"
    elapsed = end <= now_ms
    if elapsed and precision not in ("deadline", "estimated"):
        return None, "schedule_elapsed_unconfirmed"
    confidence = _number(record.get("confidence"))
    if confidence is not None and not 0 <= confidence <= 1:
        confidence = None
    return {"id": record["id"], "kind": "reset_scheduled", "reset_type": record["resetType"],
            "start_ms": start, "end_ms": end, "precision": precision, "url": _url(record),
            "basis": basis, "confidence": confidence, "constraint": constraint,
            "timing_source": timing, "scope": scope}, "schedule_elapsed_unconfirmed" if elapsed else None


def age_snapshot(snapshot: dict, now_ms: float | None = None) -> dict:
    """Remove stale or elapsed countdowns even between network polling rounds."""
    now_ms = time.time() * 1000 if now_ms is None else now_ms
    result = copy.deepcopy(snapshot)
    if result["state"] == "ok":
        source = result.get("source_updated_at_ms")
        if source is None:
            result.update(state="unavailable", reason="source_time_missing", upcoming=None)
        elif source > now_ms + CLOCK_SKEW_SECONDS * 1000:
            result.update(state="unavailable", reason="source_clock_invalid", upcoming=None)
        elif now_ms - source > SOURCE_MAX_AGE_SECONDS * 1000:
            result.update(state="stale", reason="source_stale", upcoming=None)
        elif result.get("upcoming") and result["upcoming"]["end_ms"] <= now_ms:
            result["reason"] = "schedule_elapsed_unconfirmed"
            if result["upcoming"].get("precision") not in ("deadline", "estimated"):
                result["upcoming"] = None
    if result["state"] != "ok":
        result["upcoming"] = None
    return result


def normalize_status(status: dict, metadata: dict, now_ms: float | None = None) -> dict:
    now_ms = time.time() * 1000 if now_ms is None else now_ms
    result = _empty("ok")
    data = status.get("data") if isinstance(status, dict) else None
    if (not isinstance(status, dict) or status.get("ok") is not True or not isinstance(data, dict) or
            not all(key in data for key in ("latestGlobal", "latestBanked", "nextScheduled"))):
        return _empty("unavailable", "invalid_response")
    meta = metadata.get("meta") if isinstance(metadata, dict) and metadata.get("ok") is True else None
    source = _timestamp(meta.get("generatedAt")) if isinstance(meta, dict) else None
    result.update(fetched_at_ms=int(now_ms), source_updated_at_ms=source)
    events = {}
    for key in ("latestGlobal", "latestBanked"):
        event = _completion(data.get(key), now_ms)
        if event and (event["id"] not in events or event["reset_type"] == "global_and_banked"):
            events[event["id"]] = event
    result["latest_events"] = sorted(events.values(), key=lambda item: item["at_ms"], reverse=True)
    result["upcoming"], result["reason"] = _schedule(data["nextScheduled"], now_ms)
    return age_snapshot(result, now_ms)


@dataclass
class _Round:
    snapshot: dict
    not_before_ms: float = 0


def _fetch_round(request=None) -> _Round:
    request = request or request_json
    try:
        status, headers = request(STATUS_PATH)
        wait = _budget_wait(headers, time.time() * 1000)
        if wait:
            # A second request would exceed the shared anonymous IP allowance.
            raise RadarError("rate_limited", wait)
        metadata, headers = request(META_PATH)
        now_ms = time.time() * 1000
        wait = _budget_wait(headers, now_ms)
        result = normalize_status(status, metadata, now_ms)
        result["next_check_at_ms"] = int(now_ms + max(INTERVAL_SECONDS, wait) * 1000)
        return _Round(result, now_ms + wait * 1000 if wait else 0)
    except RadarError as error:
        now_ms = time.time() * 1000
        wait = max(MIN_REFRESH_SECONDS, error.retry_after) if str(error) == "rate_limited" else 0
        result = _empty("limited" if wait else "unavailable", str(error))
        result["next_check_at_ms"] = int(now_ms + max(INTERVAL_SECONDS, wait) * 1000)
        return _Round(result, now_ms + wait * 1000 if wait else 0)
    except Exception:
        result = _empty("unavailable", "request_failed")
        result["next_check_at_ms"] = int((time.time() + INTERVAL_SECONDS) * 1000)
        return _Round(result)


def fetch_once(request=None) -> dict:
    return _fetch_round(request).snapshot


class ResetRadarPoller:
    """Nonblocking snapshots; 40 anonymous requests/hour at the default cadence."""
    def __init__(self, interval: float = INTERVAL_SECONDS, *, sampler=None):
        value = _number(interval)
        self.interval = max(INTERVAL_SECONDS, value) if value is not None else INTERVAL_SECONDS
        self._sample = sampler or _fetch_round
        self._lock = threading.Lock()
        self._wake = threading.Event()
        self._stop = threading.Event()
        self._state = _empty()
        self._thread = threading.Thread(target=self._run, name="public-reset-radar", daemon=True)
        self._thread.start()

    def snapshot(self) -> dict:
        with self._lock:
            return age_snapshot(self._state)

    def refresh(self):
        self._wake.set()

    def close(self):
        self._stop.set()
        self._wake.set()

    def _run(self):
        due, blocked_until, last_started = 0.0, 0.0, float("-inf")
        while not self._stop.is_set():
            remaining = max(due, blocked_until) - time.monotonic()
            if remaining > 0:
                requested = self._wake.wait(remaining)
                self._wake.clear()
                if self._stop.is_set():
                    return
                if requested:
                    due = max(last_started + MIN_REFRESH_SECONDS, blocked_until)
                    with self._lock:
                        self._state["next_check_at_ms"] = int((time.time() + max(0, due - time.monotonic())) * 1000)
                continue
            last_started = time.monotonic()
            try:
                fetched = self._sample()
                if isinstance(fetched, dict):
                    fetched = _Round(fetched)
                result = copy.deepcopy(fetched.snapshot)
            except Exception:
                fetched = _Round(_empty("unavailable", "request_failed"))
                result = fetched.snapshot
            if self._stop.is_set():
                return
            now, now_ms = time.monotonic(), time.time() * 1000
            blocked_until = now + max(0, (fetched.not_before_ms - now_ms) / 1000)
            due = max(last_started + self.interval, now + 1, blocked_until)
            result["next_check_at_ms"] = int(now_ms + max(0, due - now) * 1000)
            with self._lock:
                if result.get("fetched_at_ms") is None:
                    # Keep historical context but never reuse a failed round's
                    # schedule as a fresh countdown or a new completion signal.
                    for key in ("fetched_at_ms", "source_updated_at_ms", "latest_events"):
                        result[key] = copy.deepcopy(self._state.get(key))
                self._state = result


if __name__ == "__main__":
    print(json.dumps(fetch_once(), ensure_ascii=False, indent=2))
