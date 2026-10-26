import copy
import json
from pathlib import Path
import threading
import time
import unittest
from unittest.mock import patch
from urllib.parse import unquote, urlencode, urlsplit, parse_qs

import latency


def catalog():
    return {"proxies": {
        "🧠 AI服务": {"type": "Selector", "now": "🧠 AI自动", "all": ["🧠 AI自动"]},
        "🧠 AI自动": {"type": "URLTest", "now": "US AI", "all": ["US AI", "JP"]},
        "US AI": {"type": "Vless"},
        "JP": {"type": "Vless"},
        "苏菲家宽": {"type": "Selector", "now": "JP", "all": ["JP"]},
        "苏菲家宽官网地址：example.invalid": {"type": "Selector", "now": "JP"},
    }}


class DiscoveryTests(unittest.TestCase):
    def discover(self, active, base=None, pipe_names=None):
        def read(path, encoding):
            value = {"clash-verge.yaml": active, "config.yaml": base}.get(path.name)
            if value is None:
                raise FileNotFoundError(path.name)
            return value
        with patch.object(Path, "read_text", autospec=True, side_effect=read):
            return latency.discover_controllers(Path("test-config"), pipe_names=pipe_names or [])

    def test_service_pipe_matches_configured_installation(self):
        suffix = "a" * 64
        pipe = "\\\\.\\pipe\\verge-mihomo-sidecar-release-" + suffix
        result = self.discover("external-controller: ''\nexternal-controller-pipe: " + pipe + "\nsecret: 'private value'\n",
            pipe_names=["verge-mihomo-production-" + suffix, "verge-mihomo-production-" + "b" * 64])
        self.assertTrue(result[0].endpoint.endswith("production-" + suffix))
        self.assertEqual(result[0].secret, "private value")
        self.assertNotIn("private value", repr(result))
        self.assertEqual(len(result), 2)

    def test_active_disable_does_not_resurrect_base_tcp(self):
        with self.assertRaisesRegex(latency.LatencyError, "controller_missing"):
            self.discover("external-controller:\nsecret: secret\n", "external-controller: 127.0.0.1:9097\n")

    def test_fallback_to_base_only_when_active_missing(self):
        result = self.discover(None, 'external-controller: "127.0.0.1:9097"\nsecret: "abc"\n')
        self.assertEqual(result[0].endpoint, "http://127.0.0.1:9097")
        self.assertEqual(result[0].secret, "abc")

    def test_only_local_controller_destinations(self):
        for endpoint in ("192.168.1.2:9090", "evil.example:9090", "http://127.0.0.1@evil.example:9090",
                         "https://127.0.0.1:9090", "127.0.0.1:9090/path", "127.0.0.1:9090?x=y"):
            self.assertIsNone(latency._loopback_endpoint(endpoint), endpoint)
        self.assertEqual(latency._loopback_endpoint("[::1]:9090"), "http://[::1]:9090")
        self.assertEqual(latency._loopback_endpoint("0.0.0.0:9090"), "http://127.0.0.1:9090")

    def test_newline_secret_cannot_enter_http_headers(self):
        self.assertIsNone(latency._scalar('secret: "bad\\r\\nInjected: yes"\n', "secret"))
        with self.assertRaisesRegex(latency.LatencyError, "invalid_config"):
            latency.controller_json(latency.Controller("http://127.0.0.1:1", "x\r\ny"), "/proxies")


class MeasurementTests(unittest.TestCase):
    def measure(self, values=None, data=None):
        values = values or {"🧠 AI服务": 252, "苏菲家宽": 134}
        requests = []
        def request(controller, path, timeout):
            requests.append((path, timeout))
            if path == "/proxies":
                return copy.deepcopy(data if data is not None else catalog())
            parsed = urlsplit(path)
            self.assertEqual(parse_qs(parsed.query), {"url": [latency.TEST_URL], "timeout": ["5000"]})
            name = unquote(parsed.path[len("/proxies/"):-len("/delay")])
            value = values[name]
            if isinstance(value, Exception):
                raise value
            return {"delay": value}
        result = latency.measure_once(controllers=[latency.Controller("http://127.0.0.1:9090", "secret-never-in-result")], request=request)
        return result, requests

    def test_active_probe_follows_selected_chain_and_encodes_chinese(self):
        result, requests = self.measure()
        ai, general = result["nodes"]
        self.assertEqual(result["state"], "ok")
        self.assertEqual(ai["chain"], ["🧠 AI服务", "🧠 AI自动", "US AI"])
        self.assertEqual(ai["selected"], "US AI")
        self.assertEqual(general["proxy"], "苏菲家宽")
        self.assertEqual([ai["delay_ms"], general["delay_ms"]], [252, 134])
        self.assertEqual(len(requests), 3)
        self.assertTrue(all(path == "/proxies" or "%" in path for path, _ in requests))
        self.assertNotIn("secret-never-in-result", json.dumps(result))

    def test_timeout_is_not_zero_and_other_route_can_succeed(self):
        result, _ = self.measure({"🧠 AI服务": latency.LatencyError("timeout"), "苏菲家宽": 201})
        self.assertEqual(result["state"], "partial")
        self.assertEqual(result["nodes"][0]["state"], "timeout")
        self.assertIsNone(result["nodes"][0]["delay_ms"])
        self.assertIsNotNone(result["nodes"][0]["measured_at_ms"])
        self.assertEqual(result["nodes"][1]["delay_ms"], 201)

    def test_retired_ai_group_is_ignored_and_browsing_route_is_retained(self):
        data = catalog()
        data["proxies"]["AI节点"] = {"type": "Selector", "now": "JP"}
        result, requests = self.measure(data=data)
        self.assertEqual([(n["id"], n["label"]) for n in result["nodes"]],
                         [("ai", "AI服务"), ("general", "苏菲家宽")])
        requested_names = [unquote(urlsplit(path).path) for path, _ in requests]
        self.assertNotIn("/proxies/AI节点/delay", requested_names)
        self.assertIn("/proxies/苏菲家宽/delay", requested_names)

    def test_missing_or_zero_or_nonfinite_delay_is_unavailable(self):
        for value in (0, -1, None, True, "142", float("nan"), float("inf")):
            result, _ = self.measure({"🧠 AI服务": value, "苏菲家宽": 100})
            self.assertEqual(result["nodes"][0]["state"], "unavailable", repr(value))
            self.assertIsNone(result["nodes"][0]["delay_ms"])

    def test_ambiguous_name_is_not_guessed(self):
        data = catalog()
        data["proxies"]["🤖 AI服务"] = {"type": "Selector", "now": "JP"}
        result, requests = self.measure(data=data)
        self.assertEqual(result["nodes"][0]["reason"], "proxy_ambiguous")
        self.assertEqual(len(requests), 2)

    def test_missing_proxy_and_selection_cycle_fail_safely(self):
        data = catalog()
        data["proxies"]["🧠 AI自动"]["now"] = "🧠 AI服务"
        del data["proxies"]["苏菲家宽"]
        result, requests = self.measure(data=data)
        self.assertEqual(result["state"], "unavailable")
        self.assertEqual([node["reason"] for node in result["nodes"]], ["selection_cycle", "proxy_missing"])
        self.assertEqual(len(requests), 1)

    def test_controller_failure_preserves_useful_permission_reason(self):
        candidates = [latency.Controller("local-active"), latency.Controller("local-old")]
        def request(controller, path, timeout):
            raise latency.LatencyError("controller_forbidden" if controller.endpoint == "local-active" else "controller_unavailable")
        result = latency.measure_once(controllers=candidates, request=request)
        self.assertEqual(result["state"], "unavailable")
        self.assertTrue(all(node["reason"] == "controller_forbidden" for node in result["nodes"]))

    def test_controller_catalog_timeout_is_not_misdiagnosed_as_node_timeout(self):
        requested = []
        def request(controller, path, timeout):
            requested.append(path)
            raise latency.LatencyError("timeout")
        result = latency.measure_once(controllers=[latency.Controller("local")], request=request)
        self.assertEqual(requested, ["/proxies"])
        self.assertEqual(result["state"], "unavailable")
        for node in result["nodes"]:
            self.assertEqual(node["state"], "unavailable")
            self.assertEqual(node["reason"], "controller_timeout")
            self.assertIsNone(node["delay_ms"])
            self.assertIsNone(node["proxy"])

    def test_unexpected_error_body_or_credentials_are_not_returned(self):
        result, _ = self.measure({"🧠 AI服务": ValueError("secret authentication body"), "苏菲家宽": 100})
        self.assertEqual(result["nodes"][0]["reason"], "request_failed")
        self.assertNotIn("secret authentication body", json.dumps(result))

    def test_two_route_probes_run_concurrently(self):
        barrier = threading.Barrier(2)
        def request(controller, path, timeout):
            if path == "/proxies":
                return catalog()
            barrier.wait(timeout=1)
            return {"delay": 100}
        result = latency.measure_once(controllers=[latency.Controller("local")], request=request)
        self.assertEqual(result["state"], "ok")


class TransportTests(unittest.TestCase):
    def test_http_status_is_sanitized_and_connection_always_closed(self):
        for status, reason in ((401, "controller_unauthorized"), (403, "controller_forbidden"),
                               (404, "proxy_missing"), (504, "timeout"), (302, "request_failed")):
            with patch("latency.http.client.HTTPConnection") as factory:
                connection = factory.return_value
                response = connection.getresponse.return_value
                response.status = status
                response.read.return_value = b'{"error":"secret-body-not-for-UI"}'
                with self.assertRaisesRegex(latency.LatencyError, reason):
                    latency.controller_json(latency.Controller("http://127.0.0.1:9090"), "/proxies")
                response.read.assert_not_called()
                response.close.assert_called_once()
                connection.close.assert_called_once()
                connection.request.assert_called_once()
                self.assertEqual(connection.request.call_args.args[0], "GET")

    def test_tcp_request_does_not_use_environment_proxy_or_follow_redirect(self):
        with patch("latency.http.client.HTTPConnection") as factory:
            response = factory.return_value.getresponse.return_value
            response.status = 200
            response.read.return_value = b'{"delay":174}'
            path = "/proxies/x/delay?" + urlencode({"url": latency.TEST_URL, "timeout": 5000})
            self.assertEqual(latency.controller_json(latency.Controller("http://localhost:9090"), path), {"delay": 174})
            self.assertEqual(factory.call_args.args, ("127.0.0.1", 9090))
        with self.assertRaisesRegex(latency.LatencyError, "nonlocal_controller"):
            latency.controller_json(latency.Controller("http://outside.invalid:9090"), "/proxies")

    def test_request_path_allowlist_rejects_other_apis_and_probe_urls(self):
        invalid = ["/configs", "/connections", "/proxies?extra=1", "/proxies/x/delay",
                   "/proxies/x/delay?" + urlencode({"url": "https://outside.invalid/", "timeout": 5000}),
                   "/proxies/x/delay?" + urlencode({"url": latency.TEST_URL, "timeout": 6000}),
                   "/proxies/x/delay?" + urlencode({"url": latency.TEST_URL, "timeout": 5000}) + "&timeout=1"]
        with patch("latency.http.client.HTTPConnection") as factory:
            for path in invalid:
                with self.assertRaisesRegex(latency.LatencyError, "invalid_request"):
                    latency.controller_json(latency.Controller("http://127.0.0.1:9090"), path)
            factory.assert_not_called()


class PollerTests(unittest.TestCase):
    def fixture(self, delay=100, state="ok"):
        now = time.time() * 1000
        nodes = [dict(latency._node(*target), state=state, delay_ms=delay, measured_at_ms=now) for target in latency.TARGETS]
        return {"state": state, "updated_at_ms": now, "nodes": nodes, "test_url": latency.TEST_URL, "timeout_ms": 5000}

    def test_history_is_bounded_and_failure_replaces_previous_delay(self):
        with patch.object(threading.Thread, "start"):
            poller = latency.LatencyPoller()
        try:
            for _ in range(14):
                poller._publish(self.fixture())
            poller._publish(self.fixture(delay=None, state="timeout"))
            value = poller.snapshot()
            self.assertEqual(len(value["nodes"][0]["history"]), 12)
            self.assertIsNone(value["nodes"][0]["delay_ms"])
            self.assertEqual(value["nodes"][0]["history"][-1]["state"], "timeout")
            value["nodes"][0]["history"].clear()
            self.assertEqual(len(poller.snapshot()["nodes"][0]["history"]), 12)
        finally:
            poller.close()

    def test_snapshot_does_not_wait_for_network_and_refresh_is_coalesced(self):
        entered, release = threading.Event(), threading.Event()
        calls = []
        def sample():
            calls.append(time.monotonic())
            entered.set()
            release.wait(2)
            return self.fixture()
        poller = latency.LatencyPoller(sampler=sample)
        try:
            self.assertTrue(entered.wait(1))
            before = time.monotonic()
            self.assertEqual(poller.snapshot()["state"], "loading")
            self.assertLess(time.monotonic() - before, .2)
            for _ in range(30):
                poller.refresh()
            release.set()
            time.sleep(.15)
            self.assertEqual(len(calls), 1)
        finally:
            release.set()
            poller.close()


if __name__ == "__main__":
    unittest.main()
