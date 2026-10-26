from datetime import datetime, timezone
import json
from pathlib import Path
import ssl
import sys
import threading
import time
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import reset_radar as radar


NOW = 1792404000000


def iso(stamp):
    return datetime.fromtimestamp(stamp / 1000, timezone.utc).isoformat().replace("+00:00", "Z")


def scheduled(**changes):
    record = {"id": "future-reset", "kind": "reset_scheduled", "resetType": "global",
              "scheduleState": "pending", "schedulePrecision": "datetime",
              "scheduleBasis": "explicit", "scheduleTimingSource": "explicit",
              "scheduleWindow": {"startAt": iso(NOW + 600000), "endAt": iso(NOW + 600000)},
              "confidence": .92, "scope": {"plans": ["pro"], "windows": ["weekly"]},
              "detailUrl": "https://didcodexreset.com/zh/history/1.html?event=future-reset"}
    record.update(changes)
    return record


def completed(**changes):
    record = {"id": "previous-reset", "kind": "reset_completed", "resetType": "global",
              "announcedAt": iso(NOW - 600000), "completedAt": None, "effectiveAt": None,
              "scope": {"plans": ["unknown"], "windows": ["unknown"]},
              "detailUrl": "https://didcodexreset.com/zh.html"}
    record.update(changes)
    return record


def status(schedule=None, **changes):
    data = {"currentTime": iso(NOW), "latestGlobal": completed(), "latestBanked": None,
            "nextScheduled": schedule}
    data.update(changes)
    return {"ok": True, "data": data}


def meta(stamp=NOW - 300000):
    return {"ok": True, "meta": {"generatedAt": iso(stamp)}, "data": completed()}


class NormalizationTests(unittest.TestCase):
    def normalize(self, schedule=None, metadata=None, **changes):
        return radar.normalize_status(status(schedule, **changes), meta() if metadata is None else metadata, NOW)

    def test_no_schedule_does_not_predict_from_history(self):
        result = self.normalize()
        self.assertEqual(result["state"], "ok")
        self.assertIsNone(result["upcoming"])
        self.assertEqual(result["latest_events"][0]["time_basis"], "announced")
        self.assertEqual(result["latest_events"][0]["at_ms"], NOW - 600000)

    def test_explicit_future_point_and_safe_event_link(self):
        result = self.normalize(scheduled())["upcoming"]
        self.assertEqual(result["precision"], "exact")
        self.assertEqual(result["start_ms"], NOW + 600000)
        self.assertEqual(result["scope"], {"plans": ["pro"], "windows": ["weekly"]})
        self.assertEqual(result["confidence"], .92)
        bad = self.normalize(scheduled(detailUrl="https://didcodexreset.com@evil.invalid/path"))
        self.assertEqual(bad["upcoming"]["url"], "https://didcodexreset.com/zh.html")

    def test_date_and_open_window_keep_explicit_boundaries(self):
        for start in (NOW + 100000, NOW - 100000):
            result = self.normalize(scheduled(schedulePrecision="date", scheduleWindow={
                "startAt": iso(start), "endAt": iso(NOW + 86400000)}))["upcoming"]
            self.assertEqual(result["precision"], "date")
            self.assertEqual(result["start_ms"], start)
            self.assertEqual(result["end_ms"], NOW + 86400000)

    def test_expired_schedule_never_turns_into_completion(self):
        for offset in (0, -1000):
            result = self.normalize(scheduled(scheduleWindow={
                "startAt": iso(NOW + offset), "endAt": iso(NOW + offset)}))
            self.assertIsNone(result["upcoming"])
            self.assertEqual(result["reason"], "schedule_elapsed_unconfirmed")
            self.assertEqual([e["id"] for e in result["latest_events"]], ["previous-reset"])

    def test_schedule_expiration_is_applied_between_polls(self):
        snapshot = self.normalize(scheduled())
        aged = radar.age_snapshot(snapshot, NOW + 600000)
        self.assertIsNone(aged["upcoming"])
        self.assertIsNotNone(snapshot["upcoming"])
        self.assertEqual(aged["reason"], "schedule_elapsed_unconfirmed")

    def test_inferred_and_fallback_points_keep_the_api_estimate(self):
        for changes in ({"scheduleBasis": "contextual_inference"}, {"scheduleBasis": None},
                        {"scheduleTimingSource": "fallback"}):
            result = self.normalize(scheduled(**changes))["upcoming"]
            self.assertEqual(result["precision"], "estimated")
            self.assertEqual(result["start_ms"], NOW + 600000)
        result = self.normalize(scheduled(schedulePrecision="date", scheduleBasis="contextual_inference",
            scheduleWindow={"startAt": iso(NOW), "endAt": iso(NOW + 86400000)}))["upcoming"]
        self.assertEqual(result["precision"], "date")
        self.assertEqual(result["basis"], "contextual_inference")

    def test_deadline_keeps_its_timestamp_and_uses_the_window_end(self):
        result = self.normalize(scheduled(scheduleConstraint="deadline"))["upcoming"]
        self.assertEqual(result["precision"], "deadline")
        self.assertEqual(result["end_ms"], NOW + 600000)
        result = self.normalize(scheduled(scheduleConstraint="deadline", scheduleWindow={
            "startAt": iso(NOW - 60000), "endAt": iso(NOW + 600000)}))["upcoming"]
        self.assertEqual(result["precision"], "deadline")
        self.assertEqual(result["start_ms"], NOW - 60000)
        self.assertEqual(result["end_ms"], NOW + 600000)

    def test_elapsed_prediction_remains_visible_without_claiming_completion(self):
        for changes in ({"scheduleConstraint": "deadline"}, {"scheduleBasis": "contextual_inference"}):
            result = self.normalize(scheduled(**changes, scheduleWindow={
                "startAt": iso(NOW - 1000), "endAt": iso(NOW - 1000)}))
            self.assertIsNotNone(result["upcoming"])
            self.assertEqual(result["reason"], "schedule_elapsed_unconfirmed")
            self.assertEqual([event["id"] for event in result["latest_events"]], ["previous-reset"])
        snapshot = self.normalize(scheduled(scheduleConstraint="deadline"))
        aged = radar.age_snapshot(snapshot, NOW + 600000)
        self.assertEqual(aged["upcoming"]["precision"], "deadline")
        self.assertEqual(aged["reason"], "schedule_elapsed_unconfirmed")
        snapshot["state"] = "unavailable"
        self.assertIsNone(radar.age_snapshot(snapshot, NOW)["upcoming"])

    def test_other_plan_or_five_hour_only_is_not_a_pro_weekly_hint(self):
        for scope in ({"plans": ["plus"], "windows": ["weekly"]},
                      {"plans": ["pro"], "windows": ["five_hour"]}):
            result = self.normalize(scheduled(scope=scope))
            self.assertIsNone(result["upcoming"])
            self.assertEqual(result["reason"], "schedule_other_scope")
        self.assertIsNotNone(self.normalize(scheduled(scope=None))["upcoming"])

    def test_missing_stale_or_future_source_blocks_countdown(self):
        for metadata, expected in (({}, "source_time_missing"), (meta(NOW - 1800001), "source_stale"),
                                   (meta(NOW + 120001), "source_clock_invalid")):
            result = self.normalize(scheduled(), metadata=metadata)
            self.assertIsNone(result["upcoming"])
            self.assertEqual(result["reason"], expected)
            self.assertNotEqual(result["state"], "ok")
        result = self.normalize(scheduled(), metadata={"ok": True, "meta": {"generatedAt": None}})
        self.assertIsNone(result["source_updated_at_ms"])  # currentTime must never replace generatedAt.

    def test_source_becomes_stale_without_another_http_call(self):
        result = radar.age_snapshot(self.normalize(scheduled()), NOW + 1800001)
        self.assertEqual(result["state"], "stale")
        self.assertIsNone(result["upcoming"])

    def test_invalid_or_nonpending_schedule_is_not_revived(self):
        for changes in ({"scheduleWindow": None}, {"scheduleWindow": {"startAt": "bad", "endAt": "bad"}},
                        {"scheduleWindow": {"startAt": iso(NOW + 2000), "endAt": iso(NOW + 1000)}},
                        {"scheduleState": "fulfilled"}, {"scheduleState": "elapsed"},
                        {"scheduleState": "unknown"}, {"completedAt": iso(NOW - 1000)}):
            self.assertIsNone(self.normalize(scheduled(**changes))["upcoming"])
        # An effectiveAt without the publisher's scheduleWindow is not a schedule.
        self.assertIsNone(self.normalize(scheduled(scheduleWindow=None, effectiveAt=iso(NOW + 10000)))["upcoming"])

    def test_banked_and_global_are_distinct_combined_is_deduplicated(self):
        banked = completed(id="banked", resetType="banked")
        events = self.normalize(latestBanked=banked)["latest_events"]
        self.assertEqual({e["reset_type"] for e in events}, {"global", "banked"})
        combined = completed(resetType="global_and_banked")
        events = self.normalize(latestGlobal=combined, latestBanked=combined)["latest_events"]
        self.assertEqual(len(events), 1)
        self.assertEqual(events[0]["reset_type"], "global_and_banked")

    def test_completion_time_basis_and_invalid_timestamps(self):
        record = completed(completedAt=iso(NOW - 2000), effectiveAt=iso(NOW - 3000))
        event = self.normalize(latestGlobal=record)["latest_events"][0]
        self.assertEqual((event["at_ms"], event["time_basis"]), (NOW - 2000, "completed"))
        self.assertEqual(self.normalize(latestGlobal=completed(announcedAt=None))["latest_events"], [])
        self.assertIsNone(radar._timestamp("2026-10-19T12:00:00"))
        self.assertEqual(self.normalize(latestGlobal=completed(completedAt=iso(NOW + 300000)))["latest_events"], [])

    def test_failed_source_has_no_actionable_countdown(self):
        result = self.normalize(scheduled())
        for state in ("limited", "stale", "unavailable"):
            result["state"] = state
            self.assertIsNone(radar.age_snapshot(result, NOW)["upcoming"])


class ProxyTransportTests(unittest.TestCase):
    def setUp(self):
        platform = patch.object(radar.sys, "platform", "win32")
        platform.start()
        self.addCleanup(platform.stop)
        for name, value in (
                ("getproxies_registry", {"https": "http://127.0.0.1:7897"}),
                ("proxy_bypass_registry", False)):
            mocked = patch.object(radar.urllib.request, name, return_value=value, create=True)
            setattr(self, name, mocked.start())
            self.addCleanup(mocked.stop)
        environment = patch.dict(radar.os.environ, {key: value for key, value in radar.os.environ.items()
            if not key.lower().endswith("_proxy")}, clear=True)
        environment.start()
        self.addCleanup(environment.stop)

    def test_unrelated_proxy_variable_cannot_disable_windows_proxy(self):
        with patch.dict(radar.os.environ, {"GHOST_PROXY": "unrelated"}), patch.object(
                radar.http.client, "HTTPSConnection") as factory:
            connection = radar._https_connection(8)
        self.assertEqual(factory.call_args.args, ("127.0.0.1", 7897))
        self.assertEqual(factory.call_args.kwargs["timeout"], 8)
        context = factory.call_args.kwargs["context"]
        self.assertTrue(context.check_hostname)
        self.assertEqual(context.verify_mode, ssl.CERT_REQUIRED)
        connection.set_tunnel.assert_called_once_with(radar.HOST, 443)

    def test_explicit_https_proxy_overrides_system_proxy(self):
        with patch.dict(radar.os.environ, {"HTTPS_PROXY": "http://127.0.0.1:8888"}), patch.object(
                radar.http.client, "HTTPSConnection") as factory:
            radar._https_connection(8)
        self.assertEqual(factory.call_args.args, ("127.0.0.1", 8888))

    def test_empty_https_proxy_explicitly_disables_system_proxy(self):
        with patch.dict(radar.os.environ, {"HTTPS_PROXY": ""}), patch.object(
                radar.http.client, "HTTPSConnection") as factory:
            connection = radar._https_connection(8)
        self.assertEqual(factory.call_args.args, (radar.HOST,))
        connection.set_tunnel.assert_not_called()

    def test_no_proxy_rule_keeps_matching_destination_direct(self):
        with patch.dict(radar.os.environ, {"GHOST_PROXY": "unrelated", "NO_PROXY": radar.HOST}), patch.object(
                radar.http.client, "HTTPSConnection") as factory:
            connection = radar._https_connection(8)
        self.assertEqual(factory.call_args.args, (radar.HOST,))
        connection.set_tunnel.assert_not_called()

    def test_system_bypass_still_applies_with_unrelated_environment(self):
        self.proxy_bypass_registry.return_value = True
        with patch.dict(radar.os.environ, {"GHOST_PROXY": "unrelated"}):
            self.assertIsNone(radar._https_proxy())

    def test_no_system_proxy_keeps_direct_connection(self):
        self.getproxies_registry.return_value = {}
        with patch.object(radar.http.client, "HTTPSConnection") as factory:
            connection = radar._https_connection(8)
        self.assertEqual(factory.call_args.args, (radar.HOST,))
        connection.set_tunnel.assert_not_called()

    def test_unsupported_proxies_fail_without_leaking_values_or_bypassing(self):
        for address in ("socks5://127.0.0.1:7897", "https://127.0.0.1:7897",
                        "http://private-user:private-password@127.0.0.1:7897",
                        "http://127.0.0.1:0", "http://127.0.0.1:bad", "http://127.0.0.1:7897/private",
                        "http://127.0.0.1:7897?secret=value", "http://127.0.0.1:7897\n"):
            with self.subTest(address=address), patch.dict(radar.os.environ, {"HTTPS_PROXY": address}), patch.object(
                    radar.http.client, "HTTPSConnection") as factory:
                with self.assertRaises(radar.RadarError) as caught:
                    radar.request_json(radar.STATUS_PATH)
                self.assertEqual(str(caught.exception), "proxy_unsupported")
                factory.assert_not_called()

    def test_proxy_timeout_is_sanitized_and_connection_is_closed(self):
        with patch.object(radar.http.client, "HTTPSConnection") as factory:
            factory.return_value.request.side_effect = TimeoutError("private proxy detail")
            with self.assertRaises(radar.RadarError) as caught:
                radar.request_json(radar.STATUS_PATH)
            self.assertEqual(str(caught.exception), "request_timeout")
            factory.return_value.close.assert_called_once()

    def test_non_windows_discovery_does_not_read_windows_settings(self):
        with patch.object(radar.sys, "platform", "linux"), patch.object(
                radar.urllib.request, "getproxies", return_value={"https": "http://127.0.0.1:9999"}), patch.object(
                radar.urllib.request, "proxy_bypass", return_value=False):
            self.assertEqual(radar._https_proxy(), "http://127.0.0.1:9999")
        self.getproxies_registry.assert_not_called()


class RequestTests(unittest.TestCase):
    def test_exactly_two_fixed_public_gets_and_source_metadata(self):
        paths = []
        def request(path):
            paths.append(path)
            return (status(scheduled()) if path == radar.STATUS_PATH else meta()), {}
        with patch.object(radar.time, "time", return_value=NOW / 1000):
            result = radar.fetch_once(request)
        self.assertEqual(paths, [radar.STATUS_PATH, radar.META_PATH])
        self.assertEqual(result["source_updated_at_ms"], NOW - 300000)
        self.assertEqual(result["state"], "ok")

    def test_429_stops_requests_and_obeys_retry_after(self):
        paths = []
        def request(path):
            paths.append(path)
            raise radar.RadarError("rate_limited", 3600)
        with patch.object(radar.time, "time", return_value=NOW / 1000):
            result = radar.fetch_once(request)
        self.assertEqual(paths, [radar.STATUS_PATH])
        self.assertEqual(result["state"], "limited")
        self.assertEqual(result["next_check_at_ms"], NOW + 3600000)
        self.assertIsNone(result["upcoming"])

    def test_exhausted_budget_skips_metadata_and_waits_for_slot(self):
        paths = []
        def request(path):
            paths.append(path)
            return status(scheduled()), {"x-ratelimit-remaining": "0", "x-ratelimit-reset": iso(NOW + 1200000)}
        with patch.object(radar.time, "time", return_value=NOW / 1000):
            result = radar.fetch_once(request)
        self.assertEqual(len(paths), 1)
        self.assertEqual(result["state"], "limited")
        self.assertEqual(result["next_check_at_ms"], NOW + 1200000)

    def test_last_allowed_metadata_request_sets_hard_refresh_floor(self):
        def request(path):
            if path == radar.STATUS_PATH:
                return status(scheduled()), {"x-ratelimit-remaining": "1"}
            return meta(), {"x-ratelimit-remaining": "0", "x-ratelimit-reset": iso(NOW + 1200000)}
        with patch.object(radar.time, "time", return_value=NOW / 1000):
            fetched = radar._fetch_round(request)
        self.assertEqual(fetched.snapshot["state"], "ok")
        self.assertEqual(fetched.not_before_ms, NOW + 1200000)

    def test_retry_after_seconds_http_date_and_body(self):
        self.assertEqual(radar._retry_after({"retry-after": "120"}, {"retryAfter": 180}, NOW), 180)
        self.assertEqual(radar._retry_after({"retry-after": "Mon, 19 Oct 2026 10:02:00 GMT"}, now_ms=NOW), 120)
        self.assertEqual(radar._retry_after({"retry-after": "NaN"}, now_ms=NOW), 3600)

    def test_request_rejects_arbitrary_endpoints_without_connecting(self):
        with patch.object(radar.http.client, "HTTPSConnection") as connection:
            for path in ("https://evil.invalid", "/openapi/v1/status?token=private", "/account"):
                with self.assertRaisesRegex(radar.RadarError, "invalid_endpoint"):
                    radar.request_json(path)
            connection.assert_not_called()

    def test_transport_429_is_safe_and_redirects_are_not_followed(self):
        class Response:
            status = 429
            def getheaders(self):
                return [("Retry-After", "420"), ("Set-Cookie", "private-cookie")]
            def read(self, size):
                return json.dumps({"ok": False, "detail": "untrusted secret", "retryAfter": 300}).encode()
        response = Response()
        with patch.object(radar.http.client, "HTTPSConnection") as connection:
            connection.return_value.getresponse.return_value = response
            with self.assertRaises(radar.RadarError) as caught:
                radar.request_json(radar.STATUS_PATH)
            self.assertEqual(str(caught.exception), "rate_limited")
            self.assertEqual(caught.exception.retry_after, 420)
            response.status = 302
            with self.assertRaisesRegex(radar.RadarError, "request_rejected"):
                radar.request_json(radar.STATUS_PATH)
            self.assertEqual(connection.return_value.request.call_count, 2)
            self.assertEqual(connection.return_value.request.call_args.args[:2], ("GET", radar.STATUS_PATH))


class PollerTests(unittest.TestCase):
    def test_snapshot_and_close_do_not_block_on_http(self):
        started, release = threading.Event(), threading.Event()
        def sample():
            started.set()
            release.wait(2)
            return radar._empty("unavailable", "request_failed")
        poller = radar.ResetRadarPoller(sampler=sample)
        try:
            self.assertTrue(started.wait(1))
            before = time.monotonic()
            self.assertEqual(poller.snapshot()["state"], "loading")
            poller.close()
            self.assertLess(time.monotonic() - before, .2)
        finally:
            release.set()
            poller.close()
            poller._thread.join(1)

    def test_refresh_coalesces_and_cannot_bypass_rate_limit(self):
        first, second = threading.Event(), threading.Event()
        times = []
        def sample():
            times.append(time.monotonic())
            if len(times) == 1:
                first.set()
                return radar._Round(radar._empty("limited", "rate_limited"), (time.time() + .18) * 1000)
            second.set()
            return radar._empty("ok")
        with patch.object(radar, "MIN_REFRESH_SECONDS", .05):
            poller = radar.ResetRadarPoller(sampler=sample)
            try:
                self.assertTrue(first.wait(1))
                for _ in range(10):
                    poller.refresh()
                self.assertFalse(second.wait(.1))
                self.assertTrue(second.wait(1))
                self.assertGreaterEqual(times[1] - times[0], .17)
                self.assertEqual(len(times), 2)
            finally:
                poller.close()
                poller._thread.join(1)

    def test_manual_refresh_has_minimum_start_interval(self):
        first, second = threading.Event(), threading.Event()
        times = []
        def sample():
            times.append(time.monotonic())
            (first if len(times) == 1 else second).set()
            return radar._empty("unavailable", "request_failed")
        with patch.object(radar, "MIN_REFRESH_SECONDS", .12):
            poller = radar.ResetRadarPoller(sampler=sample)
            try:
                self.assertTrue(first.wait(1))
                poller.refresh()
                self.assertFalse(second.wait(.06))
                self.assertTrue(second.wait(1))
                self.assertGreaterEqual(times[1] - times[0], .11)
            finally:
                poller.close()
                poller._thread.join(1)


if __name__ == "__main__":
    unittest.main()
