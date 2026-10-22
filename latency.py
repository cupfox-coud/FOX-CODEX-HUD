"""Actively measure two selected Clash/Mihomo routes without changing routing.

The controller is restricted to local named pipes or loopback HTTP. Secrets are
read from the local running config into memory only; errors never expose bodies,
headers, config text, or credentials. Each GET /delay performs a fresh HTTPS
probe through the selected route, independently of the VPN application's UI.
"""
from __future__ import annotations

import copy
from dataclasses import dataclass, field
import http.client
import io
import ipaddress
import json
import math
import os
from pathlib import Path
import re
import threading
import time
import unicodedata
from urllib.parse import parse_qs, quote, urlencode, urlsplit


TEST_URL = "https://www.gstatic.com/generate_204"
TIMEOUT_MS = 5000
POLL_INTERVAL_SECONDS = 10
MAX_RESPONSE_BYTES = 2 * 1024 * 1024
TARGETS = (("ai", "AI节点"), ("general", "苏菲家宽"))


class LatencyError(Exception):
    """Only fixed, safe reason codes should reach the UI or persisted snapshot."""


@dataclass(frozen=True)
class Controller:
    endpoint: str
    secret: str = field(default="", repr=False)


def _scalar(text: str, key: str) -> str | None:
    """Read the few top-level scalar keys needed, without importing a YAML stack."""
    match = re.search(r"^" + re.escape(key) + r":[ \t]*([^\r\n]*)", text, re.MULTILINE)
    if not match:
        return None
    value = match.group(1).strip()
    if value.startswith('"'):
        try:
            value, _ = json.JSONDecoder().raw_decode(value)
        except ValueError:
            return None
    elif value.startswith("'"):
        match = re.match(r"'((?:[^']|'')*)'", value)
        if not match:
            return None
        value = match.group(1).replace("''", "'")
    else:
        value = re.split(r"\s+#", value, maxsplit=1)[0].strip()
    if not isinstance(value, str) or any(c in value for c in ("\r", "\n", "\x00")):
        return None
    return value


def _loopback_endpoint(value: str) -> str | None:
    try:
        parsed = urlsplit(value if "://" in value else "http://" + value)
        if (parsed.scheme != "http" or parsed.username or parsed.password or
                parsed.path not in ("", "/") or parsed.query or parsed.fragment or not parsed.port):
            return None
        host = parsed.hostname
        if host == "localhost" or host == "0.0.0.0":
            host = "127.0.0.1"
        elif host == "::":
            host = "::1"
        if not host or not ipaddress.ip_address(host).is_loopback:
            return None
        return "http://" + ("[" + host + "]" if ":" in host else host) + ":" + str(parsed.port)
    except ValueError:
        return None


def discover_controllers(config_dir: Path | None = None, pipe_names: list[str] | None = None) -> list[Controller]:
    directory = config_dir or Path(os.environ.get("APPDATA", "")) / "io.github.clash-verge-rev.clash-verge-rev"
    text = None
    for filename in ("clash-verge.yaml", "config.yaml"):
        try:
            text = (directory / filename).read_text(encoding="utf-8-sig")
            break  # An explicitly disabled controller in the active config stays disabled.
        except OSError:
            continue
    if text is None:
        raise LatencyError("config_missing")
    secret = _scalar(text, "secret") or ""
    pipe = _scalar(text, "external-controller-pipe") or ""
    endpoints = []
    prefix = "\\\\.\\pipe\\"
    if pipe.startswith(prefix) and len(pipe) > len(prefix) and "\\" not in pipe[len(prefix):]:
        if pipe_names is None:
            try:
                pipe_names = os.listdir(prefix) if os.name == "nt" else []
            except OSError:
                pipe_names = []
        basename = pipe[len(prefix):]
        if basename in pipe_names:
            endpoints.append(pipe)
        # Verge's service uses a production pipe while its saved config can name
        # the sidecar pipe. Match the exact installation suffix, never another instance.
        suffix = re.search(r"-([0-9a-f]{32,})$", basename, re.IGNORECASE)
        if suffix and basename.startswith("verge-mihomo-"):
            matches = sorted(name for name in pipe_names if name.startswith("verge-mihomo-") and
                             name.endswith(suffix.group(0)) and "\\" not in name and "/" not in name)
            endpoints.extend(prefix + name for name in matches)
        endpoints.append(pipe)
    tcp = _loopback_endpoint(_scalar(text, "external-controller") or "")
    if tcp:
        endpoints.append(tcp)
    if not endpoints:
        raise LatencyError("controller_missing")
    return [Controller(endpoint, secret) for endpoint in dict.fromkeys(endpoints)]


class _PipeStream(io.RawIOBase):
    """Windows overlapped pipe I/O with one deadline and cancellable operations."""
    def __init__(self, name: str, deadline: float):
        if os.name != "nt":
            raise LatencyError("controller_unavailable")
        import ctypes
        from ctypes import wintypes as w
        self.c, self.w = ctypes, w
        self.deadline = deadline
        self.kernel = k = ctypes.WinDLL("kernel32", use_last_error=True)

        class Overlapped(ctypes.Structure):
            _fields_ = [("Internal", ctypes.c_size_t), ("InternalHigh", ctypes.c_size_t),
                        ("Offset", w.DWORD), ("OffsetHigh", w.DWORD), ("hEvent", w.HANDLE)]

        self.Overlapped = Overlapped
        k.CreateFileW.argtypes = [w.LPCWSTR, w.DWORD, w.DWORD, ctypes.c_void_p, w.DWORD, w.DWORD, w.HANDLE]
        k.CreateFileW.restype = w.HANDLE
        k.CreateEventW.argtypes = [ctypes.c_void_p, w.BOOL, w.BOOL, w.LPCWSTR]
        k.CreateEventW.restype = w.HANDLE
        k.CloseHandle.argtypes = [w.HANDLE]
        k.WaitNamedPipeW.argtypes = [w.LPCWSTR, w.DWORD]
        k.WaitForSingleObject.argtypes = [w.HANDLE, w.DWORD]
        k.ReadFile.argtypes = [w.HANDLE, ctypes.c_void_p, w.DWORD, ctypes.POINTER(w.DWORD), ctypes.POINTER(Overlapped)]
        k.WriteFile.argtypes = k.ReadFile.argtypes
        k.GetOverlappedResult.argtypes = [w.HANDLE, ctypes.POINTER(Overlapped), ctypes.POINTER(w.DWORD), w.BOOL]
        k.CancelIoEx.argtypes = [w.HANDLE, ctypes.POINTER(Overlapped)]
        self.handle = None
        while time.monotonic() < deadline:
            handle = k.CreateFileW(name, 0xC0000000, 0, None, 3, 0x40000000, None)
            if handle != ctypes.c_void_p(-1).value:
                self.handle = handle
                return
            code = ctypes.get_last_error()
            if code != 231:  # ERROR_PIPE_BUSY
                raise LatencyError("controller_forbidden" if code == 5 else "controller_unavailable")
            k.WaitNamedPipeW(name, min(100, max(1, int((deadline - time.monotonic()) * 1000))))
        raise LatencyError("timeout")

    def readable(self):
        return True

    def _io(self, buffer, write: bool) -> int:
        c, w, k = self.c, self.w, self.kernel
        remaining = self.deadline - time.monotonic()
        if remaining <= 0:
            raise LatencyError("timeout")
        event = k.CreateEventW(None, True, False, None)
        if not event:
            raise LatencyError("controller_unavailable")
        overlapped = self.Overlapped()
        overlapped.hEvent = event
        count = w.DWORD()
        try:
            success = (k.WriteFile if write else k.ReadFile)(self.handle, buffer, len(buffer), c.byref(count), c.byref(overlapped))
            if not success:
                code = c.get_last_error()
                if code == 109 and not write:  # ERROR_BROKEN_PIPE means EOF.
                    return 0
                if code != 997:  # ERROR_IO_PENDING
                    raise LatencyError("controller_unavailable")
                result = k.WaitForSingleObject(event, max(1, int(remaining * 1000)))
                if result != 0:
                    k.CancelIoEx(self.handle, c.byref(overlapped))
                    # Complete cancellation before releasing the referenced buffers.
                    k.GetOverlappedResult(self.handle, c.byref(overlapped), c.byref(count), True)
                    raise LatencyError("timeout")
                if not k.GetOverlappedResult(self.handle, c.byref(overlapped), c.byref(count), False):
                    if c.get_last_error() == 109 and not write:
                        return 0
                    raise LatencyError("controller_unavailable")
            return count.value
        finally:
            k.CloseHandle(event)

    def sendall(self, value: bytes):
        offset = 0
        while offset < len(value):
            chunk = self.c.create_string_buffer(value[offset:], len(value) - offset)
            count = self._io(chunk, True)
            if not count:
                raise LatencyError("controller_unavailable")
            offset += count

    def readinto(self, target):
        buffer = self.c.create_string_buffer(min(len(target), 65536))
        count = self._io(buffer, False)
        target[:count] = buffer.raw[:count]
        return count

    def makefile(self, mode):
        return io.BufferedReader(self)

    def close(self):
        if getattr(self, "handle", None):
            self.kernel.CloseHandle(self.handle)
            self.handle = None
        super().close()


def controller_json(controller: Controller, path: str, timeout: float = 7) -> dict:
    if not path.startswith("/") or any(c in path for c in ("\r", "\n", "\x00")):
        raise LatencyError("invalid_request")
    # Restrict this reader to the two read-only routes used by the HUD. In
    # particular, callers cannot use the controller as a general API or ask it
    # to probe arbitrary URLs with the user's outbound routes.
    try:
        parsed_path = urlsplit(path)
        query = parse_qs(parsed_path.query, keep_blank_values=True)
        allowed = path == "/proxies"
        if re.fullmatch(r"/proxies/[^/]+/delay", parsed_path.path) and not parsed_path.fragment:
            allowed = (set(query) == {"url", "timeout"} and query.get("url") == [TEST_URL] and
                       len(query["timeout"]) == 1 and query["timeout"][0].isdigit() and
                       0 < int(query["timeout"][0]) <= TIMEOUT_MS)
        if not allowed:
            raise LatencyError("invalid_request")
    except ValueError:
        raise LatencyError("invalid_request") from None
    if any(c in controller.secret for c in ("\r", "\n", "\x00")):
        raise LatencyError("invalid_config")
    connection = response = None
    try:
        if controller.endpoint.startswith("\\\\.\\pipe\\"):
            connection = _PipeStream(controller.endpoint, time.monotonic() + timeout)
            request = "GET " + path + " HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\nAccept: application/json\r\n"
            if controller.secret:
                request += "Authorization: Bearer " + controller.secret + "\r\n"
            connection.sendall((request + "\r\n").encode("utf-8"))
            response = http.client.HTTPResponse(connection)
            response.begin()
        else:
            endpoint = _loopback_endpoint(controller.endpoint)
            if not endpoint:
                raise LatencyError("nonlocal_controller")
            parsed = urlsplit(endpoint)
            connection = http.client.HTTPConnection(parsed.hostname, parsed.port, timeout=timeout)
            headers = {"Accept": "application/json", "Connection": "close"}
            if controller.secret:
                headers["Authorization"] = "Bearer " + controller.secret
            connection.request("GET", path, headers=headers)
            response = connection.getresponse()
        if response.status != 200:
            reason = {401: "controller_unauthorized", 403: "controller_forbidden", 404: "proxy_missing",
                      504: "timeout"}.get(response.status, "request_failed")
            raise LatencyError(reason)
        payload = response.read(MAX_RESPONSE_BYTES + 1)
        if len(payload) > MAX_RESPONSE_BYTES:
            raise LatencyError("invalid_response")
        result = json.loads(payload)
        if not isinstance(result, dict):
            raise LatencyError("invalid_response")
        return result
    except LatencyError:
        raise
    except TimeoutError:
        raise LatencyError("timeout") from None
    except (OSError, ValueError, http.client.HTTPException):
        raise LatencyError("controller_unavailable") from None
    finally:
        if response is not None:
            response.close()
        if connection is not None:
            connection.close()


def _normalized_name(value: str) -> str:
    return "".join(c for c in value if unicodedata.category(c)[0] in ("L", "N"))


def resolve_target(proxies: dict, label: str) -> tuple[str, list[str]]:
    if label in proxies:
        name = label
    else:
        candidates = [name for name in proxies if isinstance(name, str) and _normalized_name(name) == label]
        if len(candidates) != 1:
            raise LatencyError("proxy_ambiguous" if candidates else "proxy_missing")
        name = candidates[0]
    chain, current = [], name
    for _ in range(16):
        if current in chain:
            raise LatencyError("selection_cycle")
        chain.append(current)
        item = proxies.get(current)
        if not isinstance(item, dict):
            raise LatencyError("selection_missing")
        selected = item.get("now")
        if not selected:
            if item.get("all"):
                raise LatencyError("selection_missing")
            return name, chain
        if not isinstance(selected, str):
            raise LatencyError("selection_missing")
        current = selected
    raise LatencyError("selection_cycle")


def _node(identifier: str, label: str, state: str = "loading", reason: str | None = None) -> dict:
    return {"id": identifier, "label": label, "proxy": None, "selected": None, "chain": [],
            "delay_ms": None, "state": state, "reason": reason, "measured_at_ms": None, "history": []}


def measure_once(config_dir: Path | None = None, *, controllers: list[Controller] | None = None,
                 request=controller_json) -> dict:
    result = {"state": "loading", "updated_at_ms": None, "interval_seconds": POLL_INTERVAL_SECONDS,
              "timeout_ms": TIMEOUT_MS, "test_url": TEST_URL,
              "nodes": [_node(*target) for target in TARGETS]}
    selected_controller = None
    reason = "controller_unavailable"
    try:
        candidates = controllers if controllers is not None else discover_controllers(config_dir)
        for candidate in candidates:
            try:
                catalog = request(candidate, "/proxies", timeout=2)
                proxies = catalog.get("proxies")
                if not isinstance(proxies, dict):
                    raise LatencyError("invalid_response")
                selected_controller = candidate
                break
            except LatencyError as error:
                # A denied live pipe is more useful diagnostically than a missing stale pipe.
                if reason not in ("controller_forbidden", "controller_unauthorized", "controller_timeout"):
                    # Catalog failures say nothing about the outbound route's
                    # latency: only a /delay failure may mark a node timed out.
                    reason = "controller_timeout" if str(error) == "timeout" else str(error)
    except LatencyError as error:
        reason = str(error)

    def measure(node: dict):
        try:
            if selected_controller is None:
                raise LatencyError(reason)
            name, chain = resolve_target(proxies, node["label"])
            node.update(proxy=name, selected=chain[-1], chain=chain)
            path = "/proxies/" + quote(name, safe="") + "/delay?" + urlencode({"url": TEST_URL, "timeout": TIMEOUT_MS})
            value = request(selected_controller, path, timeout=TIMEOUT_MS / 1000 + 2).get("delay")
            if (isinstance(value, bool) or not isinstance(value, (int, float)) or
                    not math.isfinite(value) or value <= 0):
                raise LatencyError("invalid_delay")
            node.update(state="ok", delay_ms=max(1, int(round(value))), reason=None)
        except LatencyError as error:
            node.update(state="timeout" if str(error) == "timeout" else "unavailable",
                        delay_ms=None, reason=str(error))
        except Exception:
            node.update(state="unavailable", delay_ms=None, reason="request_failed")
        node["measured_at_ms"] = time.time() * 1000

    # Both targets probe independently; no pool threads live beyond this round.
    workers = [threading.Thread(target=measure, args=(node,), daemon=True) for node in result["nodes"]]
    for worker in workers:
        worker.start()
    for worker in workers:
        worker.join()
    count = sum(node["state"] == "ok" for node in result["nodes"])
    result.update(state="ok" if count == len(TARGETS) else "partial" if count else "unavailable",
                  updated_at_ms=time.time() * 1000)
    return result


class LatencyPoller:
    """Non-blocking snapshots; two fresh route probes every ten seconds."""
    def __init__(self, interval: float = POLL_INTERVAL_SECONDS, config_dir: Path | None = None, *, sampler=None):
        self.interval = max(5.0, interval)
        self.config_dir = config_dir
        self._sample = sampler or (lambda: measure_once(self.config_dir))
        self._lock = threading.Lock()
        self._wake = threading.Event()
        self._stop = threading.Event()
        self._state = {"state": "loading", "updated_at_ms": None, "interval_seconds": self.interval,
                       "timeout_ms": TIMEOUT_MS, "test_url": TEST_URL,
                       "nodes": [_node(*target) for target in TARGETS]}
        self._thread = threading.Thread(target=self._run, name="proxy-latency", daemon=True)
        self._thread.start()

    def snapshot(self) -> dict:
        with self._lock:
            return copy.deepcopy(self._state)

    def refresh(self):
        self._wake.set()

    def close(self):
        self._stop.set()
        self._wake.set()

    def _publish(self, result: dict):
        with self._lock:
            prior = {node["id"]: node for node in self._state["nodes"]}
            for node in result["nodes"]:
                history = list(prior.get(node["id"], {}).get("history", []))
                history.append({"at_ms": node["measured_at_ms"], "delay_ms": node["delay_ms"], "state": node["state"]})
                node["history"] = history[-12:]
            result["interval_seconds"] = self.interval
            self._state = copy.deepcopy(result)

    def _run(self):
        next_due = 0.0
        last_started = float("-inf")
        while not self._stop.is_set():
            remaining = next_due - time.monotonic()
            if remaining > 0:
                self._wake.wait(remaining)
                self._wake.clear()
                if self._stop.is_set():
                    return
                # Coalesce refresh clicks and enforce at least five seconds.
                if self._stop.wait(max(0, last_started + 5 - time.monotonic())):
                    return
            last_started = time.monotonic()
            try:
                result = self._sample()
            except Exception:
                now = time.time() * 1000
                result = {"state": "unavailable", "updated_at_ms": now, "test_url": TEST_URL,
                          "timeout_ms": TIMEOUT_MS, "nodes": [_node(*target, state="unavailable", reason="request_failed") for target in TARGETS]}
                for node in result["nodes"]:
                    node["measured_at_ms"] = now
            if self._stop.is_set():
                return
            self._publish(result)
            next_due = max(last_started + self.interval, time.monotonic() + 1)


if __name__ == "__main__":
    # Safe one-shot diagnostic: no controller addresses, secrets, or other proxies.
    print(json.dumps(measure_once(), ensure_ascii=False, indent=2))
