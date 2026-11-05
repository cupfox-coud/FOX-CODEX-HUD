import json
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import patch

from metrics import SessionTail
from quota import QuotaPoller, normalize_log_limits


def window(balance, reset):
    return {'window_minutes': 10080, 'remaining_percent': balance, 'resets_at': reset}


class RealtimeQuotaTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.now = datetime(2026, 11, 4, 12).timestamp()
        self.reset = self.now + 5 * 86400
        with patch('quota.threading.Thread'):
            self.poller = QuotaPoller(self.root, history_file=self.root / 'history.json')

    def accept(self, balance, at, reset=None, observed=None, source='local_event'):
        with patch('quota.time.time', return_value=at):
            return self.poller.accept([window(balance, reset or self.reset)], at if observed is None else observed, source)

    def snapshot(self, at):
        with patch('quota.time.time', return_value=at):
            return self.poller.snapshot()

    def test_live_debit_updates_all_fields_without_waiting_for_poll(self):
        self.accept(60, self.now)
        before = self.snapshot(self.now)
        self.accept(59, self.now + .1)
        after = self.snapshot(self.now + .1)
        self.assertEqual(after['windows'][0]['remaining_percent'], 59)
        self.assertAlmostEqual(after['runway']['today']['remaining_percent'], before['runway']['today']['remaining_percent'] - 1)
        self.assertGreater(after['runway']['rate_pct_per_day'], before['runway']['rate_pct_per_day'])
        self.assertEqual(after['updated_at_ms'], (self.now + .1) * 1000)

    def test_five_percent_consumed_is_not_a_twenty_percent_daily_projection(self):
        # The reported bug: 5 points in 5.8 hours becomes ~20.7/day.
        reset = self.now + (168 - 5.8) * 3600
        self.accept(95, self.now, reset)
        value = self.snapshot(self.now)['runway']
        self.assertEqual(value['consumed_since_reset_percent'], 5)
        self.assertAlmostEqual(value['rate_pct_per_day'], 5 / 5.8 * 24)
        self.accept(100, self.now + 1, self.now + 1 + 7 * 86400)
        self.assertEqual(self.snapshot(self.now + 1)['runway']['consumed_since_reset_percent'], 0)

    def test_partial_refill_and_midnight_do_not_mix_consumption_scopes(self):
        self.accept(60, self.now)
        self.accept(80, self.now + 1, source='account_api')
        self.assertEqual(self.snapshot(self.now + 1)['runway']['consumed_since_reset_percent'], 0)
        self.accept(75, self.now + 2)
        self.assertEqual(self.snapshot(self.now + 2)['runway']['consumed_since_reset_percent'], 5)
        midnight = datetime(2026, 11, 5).timestamp()
        self.accept(75, midnight)
        result = self.snapshot(midnight)['runway']
        self.assertEqual(result['consumed_since_reset_percent'], 5)
        self.assertEqual(result['today']['spent_percent'], 0)

    def test_new_cycle_replaces_negative_daily_budget_and_old_pace_immediately(self):
        self.accept(60, self.now)
        self.accept(40, self.now + 1)
        self.assertLess(self.snapshot(self.now + 1)['runway']['today']['remaining_percent'], 0)
        self.accept(100, self.now + 2, self.now + 2 + 7 * 86400)
        result = self.snapshot(self.now + 2)
        self.assertGreater(result['runway']['today']['remaining_percent'], 0)
        self.assertEqual(result['runway']['today']['spent_percent'], 0)
        self.assertEqual(result['runway']['rate_pct_per_day'], 0)
        self.assertEqual(result['windows'][0]['resets_at'], self.now + 2 + 7 * 86400)

    def test_new_log_timestamp_with_cached_balance_does_not_fabricate_refill(self):
        self.accept(95, self.now, source='account_api')
        self.accept(94, self.now + 1, source='account_api')
        before = self.snapshot(self.now + 1)
        self.assertFalse(self.accept(95, self.now + 2))
        after = self.snapshot(self.now + 2)
        self.assertEqual(after['windows'][0]['remaining_percent'], 94)
        self.assertEqual(after['runway']['consumed_since_reset_percent'], 6)
        self.assertEqual(after['runway']['today'], before['runway']['today'])
        self.assertEqual(after['updated_at_ms'], before['updated_at_ms'])
        self.assertTrue(self.poller._wake.is_set())
        self.assertTrue(self.accept(93, self.now + 3))

    def test_same_cycle_log_refill_requires_independent_api_confirmation(self):
        self.accept(60, self.now)
        self.assertFalse(self.accept(100, self.now + 1))
        self.assertTrue(self.accept(100, self.now + 2, source='account_api'))
        value = self.snapshot(self.now + 2)['runway']
        self.assertEqual(value['consumed_since_reset_percent'], 0)
        self.assertEqual(value['today']['spent_percent'], 0)

    def test_account_one_point_correction_cannot_clear_the_consumption_ledger(self):
        self.accept(95, self.now, source='account_api')
        self.accept(94, self.now + 1, source='account_api')
        before = self.snapshot(self.now + 1)['runway']['today']
        self.accept(95, self.now + 2, source='account_api')
        value = self.snapshot(self.now + 2)['runway']
        self.assertEqual(value['basis'], 'cycle')
        self.assertEqual(value['consumed_since_reset_percent'], 5)
        self.assertEqual(value['today'], before)
        self.assertIsNone(self.poller.forecast.refill_anchor)

    def test_cached_previous_cycle_with_a_new_log_timestamp_cannot_roll_back_reset(self):
        new_reset = self.now + 7 * 86400
        self.accept(100, self.now, new_reset)
        self.assertFalse(self.accept(20, self.now + 1, self.reset))
        self.assertEqual(self.snapshot(self.now + 1)['windows'][0]['resets_at'], new_reset)

    def test_confirmed_last_point_to_full_balance_still_resets_consumption(self):
        self.accept(99, self.now)
        self.accept(100, self.now + 1, source='account_api')
        value = self.snapshot(self.now + 1)['runway']
        self.assertEqual(value['basis'], 'after_refill')
        self.assertEqual(value['consumed_since_reset_percent'], 0)
        self.assertEqual(value['today']['spent_percent'], 0)

    def test_late_api_and_stale_replayed_logs_cannot_roll_back_newer_cycle(self):
        self.accept(100, self.now, self.now + 7 * 86400)
        self.assertFalse(self.accept(12, self.now + 1, observed=self.now - 1, source='account_api'))
        self.assertFalse(self.accept(12, self.now + 1, observed=self.now - 35))
        self.assertFalse(self.accept(12, self.now + 1, observed=self.now + 50))
        self.assertEqual(self.snapshot(self.now + 1)['windows'][0]['remaining_percent'], 100)

    def test_midnight_recalculates_today_without_claiming_an_api_refresh(self):
        before_midnight = datetime(2026, 11, 4, 23, 59, 59).timestamp()
        self.accept(60, before_midnight)
        before = self.snapshot(before_midnight)
        after = self.snapshot(before_midnight + 2)
        self.assertGreater(after['runway']['today']['remaining_percent'], before['runway']['today']['remaining_percent'])
        self.assertEqual(after['updated_at_ms'], before['updated_at_ms'])
        self.assertNotEqual(after['runway']['today']['day_start_ms'], before['runway']['today']['day_start_ms'])

    def test_idle_clock_recalculates_pace_but_never_replenishes_today(self):
        self.accept(60, self.now)
        before = self.snapshot(self.now)
        after = self.snapshot(self.now + 120)
        self.assertLess(after['runway']['rate_pct_per_day'], before['runway']['rate_pct_per_day'])
        self.assertEqual(after['runway']['today'], before['runway']['today'])
        self.assertEqual(after['updated_at_ms'], before['updated_at_ms'])

    def test_exhausted_window_is_not_invented_as_one_hundred_percent(self):
        reset = self.now + 2
        self.accept(2, self.now, reset)
        result = self.snapshot(self.now + 3)
        self.assertEqual(result['runway']['state'], 'unavailable')
        self.assertEqual(result['windows'][0]['remaining_percent'], 2)

    def test_incremental_log_metadata_is_live_only_and_normalized(self):
        path = self.root / 'session.jsonl'
        raw = {'limit_id': 'codex', 'primary': {'used_percent': 2, 'window_minutes': 10080, 'resets_at': self.reset},
               'credits': {'private': 'must not be retained'}}
        row = {'timestamp': datetime.fromtimestamp(self.now, timezone.utc).isoformat(),
               'type': 'event_msg', 'payload': {'type': 'token_count', 'rate_limits': raw}}
        path.write_text(json.dumps(row) + '\n', encoding='utf-8')
        tail = SessionTail(str(path)); tail.update()
        self.assertIsNone(tail.live_quota)
        with path.open('a', encoding='utf-8') as stream:
            stream.write(json.dumps(row) + '\n')
        tail.update()
        self.assertEqual(tail.live_quota, ([window(98, self.reset)], self.now * 1000))
        self.assertNotIn('credits', repr(tail.live_quota))
        tail.reset(); tail.update()
        self.assertIsNone(tail.live_quota)
        self.assertEqual(normalize_log_limits(dict(raw, limit_id='other')), [])
        self.assertEqual(normalize_log_limits(None), [])


if __name__ == '__main__':
    unittest.main()
