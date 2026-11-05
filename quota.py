"""Read account quota through the authenticated local Codex app-server.

No credentials are opened, no inference is started, and only normalized quota
windows are retained. The official client handles its own authentication.
"""
from __future__ import annotations

import json
import math
import os
from pathlib import Path
import queue
import shutil
import subprocess
import threading
import time


class QuotaError(Exception):
    pass


def normalize_limits(result: dict) -> list[dict]:
    buckets = result.get("rateLimitsByLimitId")
    if isinstance(buckets, dict) and buckets:
        bucket = buckets.get("codex")
    else:
        bucket = result.get("rateLimits")
    if not isinstance(bucket, dict) or bucket.get("limitId") not in (None, "codex"):
        return []
    windows = {}
    for name in ("primary", "secondary"):
        raw = bucket.get(name)
        if not isinstance(raw, dict):
            continue
        used, duration, reset = (raw.get(key) for key in ("usedPercent", "windowDurationMins", "resetsAt"))
        if any(isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value)
               for value in (used, duration, reset)):
            continue
        if duration <= 0 or reset <= 0:
            continue
        minutes = int(duration)
        if minutes <= 0:
            continue
        windows[minutes] = {"window_minutes": minutes, "remaining_percent": max(0, min(100, 100 - used)),
                            "resets_at": reset}
    return [windows[key] for key in sorted(windows)]


def normalize_log_limits(raw) -> list[dict]:
    """Only quota metadata from new local token_count events; never retain credits/account data."""
    if not isinstance(raw, dict) or raw.get('limit_id') not in (None, 'codex'):
        return []
    bucket = {'limitId': 'codex'}
    for name in ('primary', 'secondary'):
        item = raw.get(name)
        if isinstance(item, dict):
            bucket[name] = {new: item.get(old) for old, new in (
                ('used_percent', 'usedPercent'), ('window_minutes', 'windowDurationMins'), ('resets_at', 'resetsAt'))}
    return normalize_limits({'rateLimits': bucket})


def find_codex() -> str | None:
    # Desktop updates change the versioned bin directory. Re-resolve every poll.
    desktop = Path(os.environ.get("LOCALAPPDATA", "")) / "OpenAI" / "Codex" / "bin"
    try:
        candidates = sorted(desktop.glob("*/codex.exe"), key=lambda path: path.stat().st_mtime, reverse=True)
        if candidates:
            return str(candidates[0])
    except OSError:
        pass
    return shutil.which("codex.exe") or shutil.which("codex")


def read_limits(codex_home: Path, timeout: float = 25, executable: str | None = None) -> list[dict]:
    executable = executable or find_codex()
    if not executable:
        raise QuotaError("client_missing")
    environment = os.environ.copy()
    environment["CODEX_HOME"] = str(codex_home)
    process = subprocess.Popen([executable, "app-server", "--listen", "stdio://"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        text=True, encoding="utf-8", env=environment,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    messages = queue.Queue(maxsize=128)

    def receive():
        try:
            for line in process.stdout:
                try:
                    value = json.loads(line)
                    if isinstance(value, dict) and value.get("id") in (0, 1):
                        messages.put_nowait(value)
                except (ValueError, queue.Full):
                    pass
        finally:
            try:
                messages.put_nowait(None)
            except queue.Full:
                pass

    reader = threading.Thread(target=receive, daemon=True)
    reader.start()
    deadline = time.monotonic() + timeout

    def send(value):
        process.stdin.write(json.dumps(value) + "\n")
        process.stdin.flush()

    def response(identifier):
        while True:
            try:
                value = messages.get(timeout=max(0.001, deadline - time.monotonic()))
            except queue.Empty:
                raise QuotaError("timeout") from None
            if value is None:
                raise QuotaError("request_failed")
            if value.get("id") == identifier:
                if "error" in value:
                    raise QuotaError("request_failed")
                return value.get("result") or {}

    try:
        send({"method": "initialize", "id": 0, "params": {"clientInfo": {
            "name": "codex_token_hud", "title": "Codex Token HUD", "version": "1.3.0-beta.8.corner.15"}}})
        response(0)
        send({"method": "initialized", "params": {}})
        send({"method": "account/rateLimits/read", "id": 1})
        windows = normalize_limits(response(1))
        if not windows:
            raise QuotaError("not_available")
        return windows
    finally:
        # Closing stdio also lets the server exit if the collector is terminated.
        try:
            process.stdin.close()
        except OSError:
            pass
        if process.poll() is None:
            process.terminate()
        try:
            process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
        reader.join(timeout=1)
        process.stdout.close()


class QuotaPoller:
    """Keep network latency entirely outside the metrics collector's fast loop."""
    def __init__(self, codex_home: Path, interval: float = 30, history_file: Path | None = None):
        from quota_forecast import QuotaForecast
        self.codex_home = codex_home
        self.interval = interval
        self.forecast = QuotaForecast(codex_home, history_file)
        self._lock = threading.Lock()
        self._wake = threading.Event()
        self._stop = threading.Event()
        self._observed_at = 0.0
        self._calculated_at = 0.0
        self._refill_probe_at = 0.0
        self._state = {"state": "loading", "windows": [], "updated_at_ms": None}
        self._thread = threading.Thread(target=self._run, name="account-quota", daemon=True)
        self._thread.start()

    def snapshot(self) -> dict:
        with self._lock:
            now = time.time()
            # Clock-dependent advice also rolls over at midnight with no log activity.
            # Keep the source timestamp unchanged: a recalculation is not a fresh API read.
            age = now * 1000 - (self._state.get('updated_at_ms') or 0)
            if 0 <= age <= 180000 and (now - self._calculated_at >= 1 or now < self._calculated_at):
                self._calculate(now)
            return dict(self._state)

    def _calculate(self, now):
        weekly = next((w for w in self._state['windows'] if w['window_minutes'] == 10080), None)
        self._state = dict(self._state, runway=self.forecast.observe(weekly, now))
        self._calculated_at = now

    def accept(self, windows, observed_at, source='local_event'):
        now = time.time()
        if not windows or not isinstance(observed_at, (int, float)) or not math.isfinite(observed_at):
            return False
        # No history replay, future clock, or late API response may roll back a newer balance.
        if observed_at < now - 30 or observed_at > now + 1:
            return False
        with self._lock:
            if observed_at <= self._observed_at:
                return False
            if source == 'local_event':
                previous = {w['window_minutes']: w for w in self._state['windows']}
                for candidate in windows:
                    old = previous.get(candidate['window_minutes'])
                    if not old:
                        continue
                    # A freshly appended token event can carry quota cached at
                    # request start. Its log timestamp is not a balance timestamp.
                    # Never undo a newer debit/cycle on that evidence; ask the
                    # independent account endpoint to confirm a same-cycle refill.
                    reset_change = candidate['resets_at'] - old['resets_at']
                    higher = candidate['remaining_percent'] > old['remaining_percent'] + 1e-9
                    if reset_change < -120 or (abs(reset_change) <= 120 and higher):
                        if now - self._refill_probe_at >= 10:
                            self._refill_probe_at = now
                            self._wake.set()
                        return False
            self._observed_at = observed_at
            self._state = {'state': 'ok', 'windows': windows, 'source': source,
                           'updated_at_ms': observed_at * 1000}
            self._calculate(now)
        return True

    def close(self):
        self._stop.set()
        self._wake.set()
        self._thread.join(timeout=1)

    def refresh(self):
        self._wake.set()

    def _run(self):
        failures = 0
        while not self._stop.is_set():
            self._wake.clear()
            started = time.time()
            try:
                windows = read_limits(self.codex_home)
                self.accept(windows, started, 'account_api')
                failures = 0
            except Exception as error:
                failures += 1
                reason = str(error) if isinstance(error, QuotaError) else "request_failed"
                with self._lock:
                    if self._observed_at <= started and not (self._state.get('source') == 'local_event' and
                            0 <= time.time() - self._observed_at < 30):
                        self._state = dict(self._state, state="unavailable", reason=reason)
            delay = min(60, 5 * 2 ** min(failures - 1, 4)) if failures else self.interval
            with self._lock:
                deadlines = [w['resets_at'] for w in self._state['windows']]
            if deadlines:
                until = min(deadlines) - time.time()
                delay = min(delay, max(5, until + .25))
            self._wake.wait(delay)
            # Manual refresh is coalesced; even repeated requests cannot flood the API.
            self._stop.wait(max(0, 1 - (time.time() - started)))
