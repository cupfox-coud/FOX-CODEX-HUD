import datetime as dt
import math
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from metrics import Measurements


def event(second, kind, payload):
    return {"timestamp": dt.datetime.fromtimestamp(1700000000 + second, dt.timezone.utc).isoformat(),
            "type": kind, "payload": payload}


def sample(second=1, used=72000, window=100000):
    return event(second, "event_msg", {"type": "token_count", "info": {
        "model_context_window": window,
        "last_token_usage": {"input_tokens": used, "cached_input_tokens": 30000,
                             "output_tokens": 1000, "total_tokens": 73000},
        "total_token_usage": {"input_tokens": 600000, "output_tokens": 80000}}})


class ContextUsageTests(unittest.TestCase):
    def test_last_input_includes_cache_once_not_cumulative_or_output(self):
        m = Measurements("model-a")
        m.feed(sample())
        c = m.snapshot()["context"]
        self.assertEqual((c["input_tokens"], c["window_tokens"], c["percent_used"]), (72000, 100000, 72))

    def test_same_cumulative_totals_still_update_context(self):
        m = Measurements("model-a")
        m.feed(sample())
        m.feed(sample(2, 88000))
        self.assertEqual(m.snapshot()["context"]["percent_used"], 88)

    def test_missing_window_does_not_reuse_old_denominator(self):
        m = Measurements("model-a")
        m.feed(sample())
        m.feed(sample(2, 80000, None))
        self.assertIsNone(m.snapshot()["context"]["percent_used"])

    def test_invalid_fields_are_unknown_not_zero(self):
        for used, window in [(None, 100000), (-1, 100000), (True, 100000),
                             (72000, 0), (72000, -1), (math.inf, 100000), (72000, math.nan), (10**1000, 100000)]:
            with self.subTest(used=used, window=window):
                m = Measurements("model-a"); m.feed(sample(1, used, window))
                self.assertIsNone(m.snapshot()["context"]["percent_used"])

    def test_model_switch_does_not_relabel_old_sample(self):
        m = Measurements("model-a"); m.feed(sample())
        m.feed(event(2, "turn_context", {"model": "model-b"}))
        c = m.snapshot()["context"]
        self.assertTrue(c["stale_model"])
        self.assertEqual(c["sample_model"], "model-a")
        m.feed(sample(3, 30000, 200000))
        self.assertFalse(m.snapshot()["context"]["stale_model"])
        self.assertEqual(m.snapshot()["context"]["percent_used"], 15)

    def test_decrease_is_not_proof_of_compaction(self):
        m = Measurements("model-a"); m.feed(sample()); m.feed(sample(2, 20000))
        self.assertIsNone(m.snapshot()["context"]["compacted_at_ms"])
        self.assertEqual(m.snapshot()["context"]["percent_used"], 20)

    def test_explicit_compaction_waits_for_new_sample_and_retains_marker(self):
        m = Measurements("model-a"); m.feed(sample())
        m.feed(event(2, "compacted", {"message": "private summary must not be retained"}))
        self.assertTrue(m.snapshot()["context"]["pending_compaction"])
        m.feed(sample(3, 20000))
        c = m.snapshot()["context"]
        self.assertFalse(c["pending_compaction"])
        self.assertEqual(c["compacted_at_ms"], 1700000002000)
        self.assertNotIn("private", str(c))

    def test_older_sample_and_inherited_fork_history_cannot_replace_current(self):
        m = Measurements("model-a")
        m.feed(event(10, "session_meta", {"id": "child"}))
        m.feed(sample(5, 99000)); self.assertIsNone(m.snapshot()["context"]["percent_used"])
        m.feed(sample(12, 20000)); m.feed(sample(11, 90000))
        self.assertEqual(m.snapshot()["context"]["percent_used"], 20)

    def test_billing_record_does_not_invent_context_window(self):
        m = Measurements("model-a")
        m.feed(event(1, "token_usage_record", {"response_id": "r", "usage": {"input_tokens": 80000}}))
        self.assertIsNone(m.snapshot()["context"]["percent_used"])

    def test_overflow_is_visible_and_zero_is_valid_when_explicitly_reported(self):
        m = Measurements("model-a"); m.feed(sample(1, 120000))
        self.assertEqual(m.snapshot()["context"]["percent_used"], 120)
        m.feed(sample(2, 0)); self.assertEqual(m.snapshot()["context"]["percent_used"], 0)

    def test_sessions_have_independent_context(self):
        a, b = Measurements("a"), Measurements("b")
        a.feed(sample(1, 90000)); b.feed(sample(1, 10000))
        self.assertEqual(a.snapshot()["context"]["percent_used"], 90)
        self.assertEqual(b.snapshot()["context"]["percent_used"], 10)
