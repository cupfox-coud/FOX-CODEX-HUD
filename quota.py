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
    def __init__(self, codex_home: Path, interval: float = 60):
        self.codex_home = codex_home
        self.interval = interval
        self._lock = threading.Lock()
        self._wake = threading.Event()
        self._state = {"state": "loading", "windows": [], "updated_at_ms": None}
        threading.Thread(target=self._run, name="account-quota", daemon=True).start()

    def snapshot(self) -> dict:
        with self._lock:
            return dict(self._state)

    def refresh(self):
        self._wake.set()

    def _run(self):
        while True:
            self._wake.clear()
            try:
                windows = read_limits(self.codex_home)
                state = {"state": "ok", "windows": windows, "updated_at_ms": time.time() * 1000}
                with self._lock:
                    self._state = state
            except Exception as error:
                reason = str(error) if isinstance(error, QuotaError) else "request_failed"
                with self._lock:
                    self._state = dict(self._state, state="unavailable", reason=reason)
            self._wake.wait(self.interval)
            # A tray refresh can wake the poller, but cannot create a request storm.
            self._wake.clear()
            time.sleep(1)
