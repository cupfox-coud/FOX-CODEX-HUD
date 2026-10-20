import json
from pathlib import Path
import queue
import unittest
from unittest.mock import patch

from quota import QuotaError, normalize_limits, read_limits


def window(used=41, duration=10080, reset=1800000000):
    return {"usedPercent": used, "windowDurationMins": duration, "resetsAt": reset}


class LimitsTests(unittest.TestCase):
    def test_primary_weekly_does_not_invent_a_five_hour_bucket(self):
        self.assertEqual(normalize_limits({"rateLimits": {"primary": window(), "secondary": None}}),
            [{"window_minutes": 10080, "remaining_percent": 59, "resets_at": 1800000000}])

    def test_map_takes_precedence_and_sort_uses_duration(self):
        result = {"rateLimits": {"primary": window(90)}, "rateLimitsByLimitId": {
            "codex": {"primary": window(41), "secondary": window(28, 300)},
            "codex_other": {"primary": window(99)}}}
        self.assertEqual([w["remaining_percent"] for w in normalize_limits(result)], [72, 59])

    def test_missing_codex_bucket_is_not_another_models_balance(self):
        self.assertEqual(normalize_limits({"rateLimitsByLimitId": {"codex_other": {"primary": window()}},
                                           "rateLimits": {"primary": window()}}), [])

    def test_missing_or_invalid_values_are_unavailable(self):
        for value in (None, {}, {"usedPercent": None}, window(True), window(float("nan")),
                      window(duration=0), window(duration=0.5), window(reset=-1)):
            with self.subTest(value=value):
                self.assertEqual(normalize_limits({"rateLimits": {"primary": value}}), [])

    def test_percentage_is_clamped_and_credits_are_not_persisted(self):
        self.assertEqual(normalize_limits({"rateLimits": {"primary": window(-3), "secondary": window(110, 300),
                "credits": {"private_field": "redacted"}}}), [
            {"window_minutes": 300, "remaining_percent": 0, "resets_at": 1800000000},
            {"window_minutes": 10080, "remaining_percent": 100, "resets_at": 1800000000}])


class FakeServer:
    def __init__(self, fail=False, silent=False):
        self.responses = queue.Queue()
        self.stdout = self
        self.stdin = self
        self.methods = []
        self.terminated = False
        self.fail = fail
        self.silent = silent

    def write(self, line):
        value = json.loads(line)
        self.methods.append(value["method"])
        if self.silent:
            return
        if value["method"] == "initialize":
            self.responses.put(json.dumps({"id": 0, "result": {}}))
        elif value["method"] == "account/rateLimits/read":
            response = {"id": 1, "error": {"code": -1, "message": "private server detail"}} if self.fail else {
                "id": 1, "result": {"rateLimits": {"primary": window()}}}
            self.responses.put(json.dumps(response))

    def flush(self):
        pass

    def __iter__(self):
        while True:
            value = self.responses.get()
            if value is None:
                return
            yield value

    def close(self):
        self.responses.put(None)

    def poll(self):
        return 0 if self.terminated else None

    def terminate(self):
        self.terminated = True

    def wait(self, timeout=None):
        return 0


class ProtocolTests(unittest.TestCase):
    def test_only_initialize_and_quota_read_are_sent(self):
        process = FakeServer()
        with patch("quota.subprocess.Popen", return_value=process) as popen:
            self.assertEqual(read_limits(Path("fixture-home"), executable="codex.exe")[0]["remaining_percent"], 59)
        self.assertEqual(process.methods, ["initialize", "initialized", "account/rateLimits/read"])
        self.assertEqual(popen.call_args.kwargs["env"]["CODEX_HOME"], "fixture-home")
        self.assertTrue(process.terminated)

    def test_errors_are_sanitized_and_server_is_stopped(self):
        process = FakeServer(fail=True)
        with patch("quota.subprocess.Popen", return_value=process), self.assertRaisesRegex(QuotaError, "^request_failed$"):
            read_limits(Path("fixture-home"), executable="codex.exe")
        self.assertTrue(process.terminated)

    def test_timeout_stops_server(self):
        process = FakeServer(silent=True)
        with patch("quota.subprocess.Popen", return_value=process), self.assertRaisesRegex(QuotaError, "^timeout$"):
            read_limits(Path("fixture-home"), executable="codex.exe", timeout=0.02)
        self.assertTrue(process.terminated)

    def test_missing_client_does_not_try_to_log_in(self):
        with patch("quota.find_codex", return_value=None), self.assertRaisesRegex(QuotaError, "^client_missing$"):
            read_limits(Path("fixture-home"))


if __name__ == "__main__":
    unittest.main()
