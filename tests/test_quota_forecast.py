import json
from datetime import datetime
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from quota import QuotaError, QuotaPoller
from quota_forecast import QuotaForecast, _round_hours


def weekly(remaining, reset):
    return {'window_minutes': 10080, 'remaining_percent': remaining, 'resets_at': reset}


class ForecastTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.now = 1800000000.0
        self.reset = self.now + 5 * 86400  # Two days elapsed, five days left.
        self.path = self.root / 'runtime/history.json'
        self.forecast = QuotaForecast(self.root / 'home', self.path)

    def test_seven_day_baseline_cycle_pace_allowance_and_runway(self):
        value = self.forecast.observe(weekly(60, self.reset), self.now)
        self.assertEqual(value['state'], 'estimated')
        self.assertEqual(value['basis'], 'cycle')
        self.assertEqual(value['baseline_pct_per_day'], 14.3)  # 100 / 7
        self.assertEqual(value['rate_pct_per_day'], 20)  # 40% used / 2 days
        self.assertEqual(value['allowance_pct_per_day'], 12)  # 60% / 5 days
        self.assertEqual(value['hours'], 72)  # 60% / 20% per day = 3 days
        self.assertEqual(value['gap_hours'], 48)
        self.assertAlmostEqual(value['runway_fraction'], 0.6)

    def test_slow_cycle_pace_can_last_until_reset_without_inventing_extra_quota(self):
        value = self.forecast.observe(weekly(80, self.reset), self.now)
        self.assertEqual(value['rate_pct_per_day'], 10)
        self.assertEqual(value['allowance_pct_per_day'], 16)
        self.assertEqual(value['state'], 'through_reset')
        self.assertNotIn('hours', value)
        self.assertEqual(value['runway_fraction'], 1)
        self.assertEqual(value['gap_hours'], 0)

    def test_cycle_calculation_across_balances_and_elapsed_days(self):
        for elapsed in (0.25, 1, 2, 4, 6):
            reset = self.now + (7 - elapsed) * 86400
            for remaining in (1, 10, 50, 70, 98):
                with self.subTest(elapsed=elapsed, remaining=remaining):
                    forecast = QuotaForecast(self.root / 'fixture')
                    value = forecast.observe(weekly(remaining, reset), self.now)
                    rate = (100 - remaining) / elapsed
                    safe = remaining / (7 - elapsed)
                    runway = 24 * remaining / rate
                    self.assertAlmostEqual(value['rate_pct_per_day'], rate)
                    self.assertAlmostEqual(value['allowance_pct_per_day'], round(safe, 1))
                    self.assertEqual(value['state'], 'estimated' if rate > safe else 'through_reset')
                    if rate > safe:
                        self.assertEqual(value['hours'], _round_hours(runway))
                        self.assertEqual(value['gap_hours'],
                            _round_hours((7 - elapsed) * 24) - _round_hours(runway))

    def test_estimate_uses_cycle_pace_not_a_temporary_recent_spike(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(json.dumps({'version': 1, 'reset': self.reset,
            'samples': [[self.now - 7200, 63], [self.now - 60, 60]],
            'published': None, 'published_at': 0}))
        forecast = QuotaForecast(self.root / 'home', self.path)
        value = forecast.observe(weekly(60, self.reset), self.now)
        self.assertEqual(value['rate_pct_per_day'], 20)
        self.assertEqual(value['hours'], 72)

    def test_each_balance_change_updates_forecast_without_ten_minute_freeze(self):
        first = self.forecast.observe(weekly(60, self.reset), self.now)
        second = self.forecast.observe(weekly(59, self.reset), self.now + 60)
        self.assertEqual(second['remaining_at_publish'], 59)
        self.assertEqual(second['calculated_at_ms'], (self.now + 60) * 1000)
        self.assertGreater(second['rate_pct_per_day'], first['rate_pct_per_day'])
        self.assertLess(second['hours'], first['hours'])
        urgent = self.forecast.observe(weekly(54, self.reset), self.now + 120)
        self.assertEqual(urgent['remaining_at_publish'], 54)
        self.assertGreater(urgent['rate_pct_per_day'], first['rate_pct_per_day'])
        self.assertLess(urgent['hours'], first['hours'])

    def test_small_changes_hold_one_coherent_pace_for_numbers_and_timeline(self):
        first = self.forecast.observe(weekly(60, self.reset), self.now)
        later = self.forecast.observe(weekly(60, self.reset), self.now + 601)
        self.assertLess(later['rate_pct_per_day'], first['rate_pct_per_day'])
        expected_hours = 60 * 24 / later['rate_pct_per_day']
        actual_reset_hours = (self.reset - self.now - 601) / 3600
        self.assertAlmostEqual(later['runway_fraction'], expected_hours / actual_reset_hours)
        self.assertEqual(later['hours'], _round_hours(expected_hours))
        self.assertEqual(later['gap_hours'], _round_hours(actual_reset_hours) - _round_hours(expected_hours))

    def test_smoothing_does_not_hide_a_transition_to_early_exhaustion(self):
        first = self.forecast.observe(weekly(71.5, self.reset), self.now)
        self.assertEqual(first['state'], 'through_reset')
        later = self.forecast.observe(weekly(71.2, self.reset), self.now + 601)
        self.assertEqual(later['state'], 'estimated')
        self.assertLess(later['runway_fraction'], 1)

    def test_rounding_cannot_create_a_false_warning_at_the_reset_boundary(self):
        # 14.2856%/day is just below the safe allowance; rounding it to
        # 14.29 before comparison would falsely claim early exhaustion.
        value = self.forecast.observe(weekly(71.4288, self.reset), self.now)
        self.assertEqual(value['state'], 'through_reset')
        self.assertLess(value['rate_pct_per_day'], 71.4288 / 5)

    def test_shortfall_matches_displayed_durations_and_does_not_fabricate_15_minutes(self):
        near = self.forecast.observe(weekly(71.3, self.reset), self.now)
        self.assertEqual(near['state'], 'estimated')
        self.assertEqual(near['reset_hours'], near['hours'])
        self.assertEqual(near['gap_hours'], 0)
        self.assertFalse(near['gap_under_15_minutes'])
        other = QuotaForecast(self.root / 'other-home')
        very_near = other.observe(weekly(71.42, self.reset), self.now)
        self.assertEqual(very_near['gap_hours'], 0)
        self.assertTrue(very_near['gap_under_15_minutes'])

    def test_early_cycle_and_minimal_consumption_remain_neutral(self):
        near_start = self.forecast.observe(weekly(90, self.now + 7 * 86400 - 3600), self.now)
        self.assertEqual(near_start['state'], 'estimated')
        self.assertTrue(near_start['provisional'])
        self.assertEqual(near_start['rate_pct_per_day'], 240)
        self.assertEqual(near_start['baseline_pct_per_day'], 14.3)
        self.assertIsNotNone(near_start['allowance_pct_per_day'])
        no_usage = QuotaForecast(self.root).observe(weekly(100, self.reset), self.now)
        self.assertEqual(no_usage['state'], 'observing')
        self.assertEqual(no_usage['rate_pct_per_day'], 0)

    def test_zero_balance_is_immediate_and_does_not_invent_a_pace(self):
        value = self.forecast.observe(weekly(0, self.reset), self.now)
        self.assertEqual(value['state'], 'exhausted')
        self.assertEqual(value['allowance_pct_per_day'], 0)
        self.assertNotIn('rate_pct_per_day', value)

    def test_reset_imminent_does_not_display_an_unbounded_daily_budget(self):
        value = self.forecast.observe(weekly(60, self.now + 120), self.now)
        self.assertIsNone(value['allowance_pct_per_day'])
        self.assertEqual(value['reset_hours'], 0)

    def test_refill_clears_cycle_average_then_uses_observed_post_refill_pace(self):
        self.assertEqual(self.forecast.observe(weekly(60, self.reset), self.now)['state'], 'estimated')
        refill = self.forecast.observe(weekly(80, self.reset), self.now + 60)
        self.assertEqual(refill['state'], 'observing')
        self.assertEqual(refill['basis'], 'after_refill')
        later = self.forecast.observe(weekly(76, self.reset), self.now + 60 + 6 * 3600)
        self.assertEqual(later['basis'], 'after_refill')
        self.assertEqual(later['rate_pct_per_day'], 16)  # 4 points / 0.25 day
        self.assertEqual(later['state'], 'through_reset')

    def test_refill_anchor_survives_restart_and_resets_with_next_cycle(self):
        self.forecast.observe(weekly(60, self.reset), self.now)
        self.forecast.observe(weekly(80, self.reset), self.now + 60)
        resumed = QuotaForecast(self.root / 'home', self.path)
        result = resumed.observe(weekly(76, self.reset), self.now + 60 + 6 * 3600)
        self.assertEqual(result['basis'], 'after_refill')
        switched = resumed.observe(weekly(100, self.now + 7 * 86400 + 8 * 3600), self.now + 8 * 3600)
        self.assertEqual(switched['basis'], 'cycle')
        self.assertEqual(switched['state'], 'observing')
        self.assertIsNone(resumed.refill_anchor)

    def test_tiny_balance_rounding_change_does_not_look_like_refill(self):
        self.forecast.observe(weekly(60, self.reset), self.now)
        result = self.forecast.observe(weekly(60.4, self.reset), self.now + 60)
        self.assertEqual(result['basis'], 'cycle')
        self.assertIsNone(self.forecast.refill_anchor)

    def test_old_six_hour_forecast_is_recalculated_immediately_on_upgrade(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(json.dumps({'version': 1, 'reset': self.reset,
            'samples': [[self.now - 60, 60]],
            'published': {'state': 'estimated', 'hours': 38, 'span_hours': 2,
                'reset_hours': 120, 'runway_fraction': .3, 'gap_hours': 84,
                'rate_pct_per_hour': 1.9}, 'published_at': self.now - 60}))
        upgraded = QuotaForecast(self.root / 'home', self.path)
        result = upgraded.observe(weekly(60, self.reset), self.now)
        self.assertEqual(result['method'], 'weekly_cycle_v4')
        self.assertEqual(result['hours'], 72)
        self.assertEqual(result['rate_pct_per_day'], 20)
        stored = json.loads(self.path.read_text())
        stored['published']['method'] = 'weekly_cycle_v2'
        stored['published']['hours'] = 40
        self.path.write_text(json.dumps(stored))
        resumed = QuotaForecast(self.root / 'home', self.path)
        self.assertEqual(resumed.observe(weekly(60, self.reset), self.now + 60)['hours'], 72)

    def test_restart_holds_a_valid_new_forecast_without_private_data(self):
        first = self.forecast.observe(weekly(60, self.reset), self.now)
        resumed = QuotaForecast(self.root / 'home', self.path)
        later = resumed.observe(weekly(60, self.reset), self.now + 60)
        self.assertEqual(later['today'], first['today'])
        self.assertEqual(later['calculated_at_ms'], (self.now + 60) * 1000)
        self.assertLess(later['rate_pct_per_day'], first['rate_pct_per_day'])
        stored = json.loads(self.path.read_text())
        self.assertEqual(set(stored), {'version', 'reset', 'samples', 'published', 'published_at', 'refill_anchor', 'daily_budget'})
        self.assertNotIn('thread_id', self.path.read_text())

    def test_corrupt_cache_does_not_prevent_valid_official_cycle_estimate(self):
        self.path.parent.mkdir(parents=True)
        self.path.write_text('{bad json')
        forecast = QuotaForecast(self.root / 'home', self.path)
        self.assertEqual(forecast.observe(weekly(60, self.reset), self.now)['hours'], 72)

    def test_adjustment_anchor_from_a_different_week_is_ignored(self):
        self.path.parent.mkdir(parents=True)
        self.path.write_text(json.dumps({'version': 1, 'reset': self.reset, 'samples': [],
            'refill_anchor': [self.now - 8 * 86400, 80], 'published': None, 'published_at': 0}))
        forecast = QuotaForecast(self.root / 'home', self.path)
        result = forecast.observe(weekly(60, self.reset), self.now)
        self.assertEqual(result['basis'], 'cycle')
        self.assertEqual(result['hours'], 72)

    def test_future_refill_anchor_after_clock_change_does_not_create_a_forecast(self):
        self.forecast.observe(weekly(60, self.reset), self.now)
        self.forecast.observe(weekly(80, self.reset), self.now + 60)
        result = self.forecast.observe(weekly(76, self.reset), self.now - 120)
        self.assertEqual(result['state'], 'observing')
        self.assertEqual(result['basis'], 'after_refill')

    def test_only_valid_weekly_values_and_times_are_accepted(self):
        for item in (None, {}, weekly(True, self.reset), weekly(-1, self.reset), weekly(101, self.reset),
                     weekly(float('nan'), self.reset), weekly(30, self.now - 1),
                     weekly(30, self.now + 8 * 86400),
                     {'window_minutes': 300, 'remaining_percent': 50, 'resets_at': self.reset}):
            with self.subTest(item=item):
                self.assertEqual(self.forecast.observe(item, self.now)['state'], 'unavailable')
        self.assertEqual(self.forecast.observe(weekly(40, self.reset), float('nan'))['state'], 'unavailable')

    def test_duration_never_rounds_tiny_forecast_up_to_15_minutes(self):
        self.assertEqual(_round_hours(0.01), 0)
        self.assertEqual(_round_hours(0.24), 0)
        self.assertEqual(_round_hours(0.25), 0.25)

    def test_quota_poller_keeps_last_estimate_on_failed_refresh(self):
        class StopPolling(BaseException):
            pass
        with patch('quota.threading.Thread'), patch('quota.time.time', return_value=self.now), \
             patch('quota.time.sleep'), patch('quota.read_limits', side_effect=[
                 [weekly(60, self.reset)], QuotaError('timeout'), StopPolling()]):
            poller = QuotaPoller(self.root / 'home', history_file=self.path)
            poller._wake.wait = lambda _: None
            with self.assertRaises(StopPolling):
                poller._run()
        snapshot = poller.snapshot()
        self.assertEqual(snapshot['state'], 'unavailable')
        self.assertEqual(snapshot['reason'], 'timeout')
        self.assertEqual(snapshot['runway']['hours'], 72)
        self.assertEqual(snapshot['runway']['rate_pct_per_day'], 20)
        self.assertEqual(snapshot['updated_at_ms'], self.now * 1000)


class TodayBudgetTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.path = self.root / 'history.json'
        self.start = datetime(2026, 10, 30).timestamp()
        self.reset = self.start + 5 * 86400
        self.forecast = QuotaForecast(self.root, self.path)

    def observe(self, remaining, hours=0, reset=None):
        return self.forecast.observe(weekly(remaining, reset or self.reset), self.start + hours * 3600)

    def test_day_allocation_debits_actual_consumption(self):
        first = self.observe(60)['today']
        self.assertEqual(first['allocated_percent'], 12)
        later = self.observe(56, 6)['today']
        self.assertEqual(later['remaining_percent'], 8)
        self.assertEqual(later['spent_percent'], 4)
        self.assertEqual(later['allocated_percent'], first['allocated_percent'])

    def test_no_refill_from_passing_time_or_held_weekly_forecast(self):
        first = self.observe(60)
        later = self.observe(59, 1 / 60)
        self.assertGreater(later['rate_pct_per_day'], first['rate_pct_per_day'])
        self.assertEqual(later['today']['remaining_percent'], 11)
        self.assertEqual(self.observe(59, 12)['today']['remaining_percent'], 11)

    def test_over_budget_is_negative_without_claiming_weekly_quota_is_exhausted(self):
        self.observe(60)
        later = self.observe(47, 8)
        self.assertEqual(later['today']['state'], 'over_budget')
        self.assertEqual(later['today']['remaining_percent'], -1)
        self.assertNotEqual(later['state'], 'exhausted')

    def test_exactly_spent_is_zero_but_further_usage_keeps_debiting(self):
        self.observe(60)
        exact = self.observe(48, 8)['today']
        self.assertEqual((exact['state'], exact['remaining_percent']), ('spent', 0))
        self.assertEqual(self.observe(46, 9)['today']['remaining_percent'], -2)
        self.assertEqual(self.observe(46, 12)['today']['remaining_percent'], -2)
        self.assertEqual(self.observe(45, 13)['today']['remaining_percent'], -3)

    def test_existing_zero_clamped_ledger_restores_the_real_deficit(self):
        self.observe(60)
        self.observe(47, 8)
        self.forecast = QuotaForecast(self.root, self.path)
        self.assertEqual(self.observe(47, 9)['today']['remaining_percent'], -1)
        self.assertEqual(self.observe(47.3, 10)['today']['remaining_percent'], -1)
        self.assertAlmostEqual(self.observe(46.9, 10 + 1 / 60)['today']['remaining_percent'], -1.1)

    def test_negative_recommendation_is_not_deducted_again_the_next_day(self):
        self.observe(60)
        self.assertEqual(self.observe(47, 8)['today']['remaining_percent'], -1)
        self.assertEqual(self.observe(47, 24)['today']['remaining_percent'], 11.75)

    def test_weekly_exhaustion_retains_the_daily_deficit(self):
        self.observe(60)
        exhausted = self.observe(0, 10)
        self.assertEqual(exhausted['state'], 'exhausted')
        self.assertEqual(exhausted['today']['remaining_percent'], -48)

    def test_sub_decimal_deficit_is_preserved_without_float_dust(self):
        self.observe(60)
        self.assertEqual(self.observe(47.99999999999999, 8)['today']['remaining_percent'], 0)
        self.assertAlmostEqual(self.observe(47.96, 9)['today']['remaining_percent'], -0.04)

    def test_midday_first_observation_only_allocates_remaining_part_of_day(self):
        today = self.observe(54, 12)['today']
        self.assertEqual(today['allocated_percent'], 6)
        self.assertEqual(today['spent_percent'], 0)
        self.assertEqual(today['anchor_at_ms'], (self.start + 12 * 3600) * 1000)

    def test_upgrade_reuses_real_observations_instead_of_giving_fresh_budget(self):
        self.path.write_text(json.dumps({'version': 1, 'reset': self.reset,
            'samples': [[self.start, 60], [self.start + 6 * 3600, 56]],
            'published': None, 'published_at': 0}))
        self.forecast = QuotaForecast(self.root, self.path)
        today = self.observe(56, 6)['today']
        self.assertEqual(today['allocated_percent'], 12)
        self.assertEqual(today['remaining_percent'], 8)

    def test_restart_and_trimmed_history_preserve_the_day_allocation(self):
        self.observe(60)
        self.observe(56, 6)
        stored = json.loads(self.path.read_text())
        stored['samples'] = stored['samples'][-1:]
        self.path.write_text(json.dumps(stored))
        self.forecast = QuotaForecast(self.root, self.path)
        today = self.observe(50, 12)['today']
        self.assertEqual(today['allocated_percent'], 12)
        self.assertEqual(today['remaining_percent'], 2)

    def test_next_calendar_day_allocates_from_new_balance(self):
        self.observe(60)
        old = self.observe(54, 23)['today']
        today = self.observe(54, 24)['today']
        self.assertEqual(old['remaining_percent'], 6)
        self.assertEqual(today['allocated_percent'], 13.5)
        self.assertEqual(today['spent_percent'], 0)
        self.assertEqual(today['day_start_ms'], old['day_end_ms'])

    def test_skipped_days_do_not_carry_an_expired_budget(self):
        self.observe(60)
        today = self.observe(50, 60)['today']
        self.assertEqual(today['remaining_percent'], 10)
        self.assertEqual(today['anchor_at_ms'], (self.start + 60 * 3600) * 1000)

    def test_refill_and_official_cycle_change_rebase_from_that_time(self):
        self.observe(60)
        refill = self.observe(80, 12)['today']
        self.assertAlmostEqual(refill['remaining_percent'], 80 / 9)
        self.assertEqual(refill['spent_percent'], 0)
        reset = self.start + 13 * 3600 + 7 * 86400
        new_cycle = self.observe(100, 13, reset)['today']
        self.assertAlmostEqual(new_cycle['remaining_percent'], 100 * 11 / 168)
        self.assertEqual(new_cycle['anchor_at_ms'], (self.start + 13 * 3600) * 1000)

    def test_tiny_upward_correction_does_not_replenish_today(self):
        self.observe(60)
        self.observe(56, 6)
        self.assertEqual(self.observe(56.4, 7)['today']['remaining_percent'], 8)
        self.assertEqual(self.observe(56.2, 8)['today']['remaining_percent'], 8)

    def test_small_real_debit_is_saved_even_without_new_history_sample(self):
        self.observe(60)
        self.observe(59.9, 1 / 60)
        self.forecast = QuotaForecast(self.root, self.path)
        self.assertAlmostEqual(self.observe(59.9, 2 / 60)['today']['remaining_percent'], 11.9)

    def test_corrupt_persisted_budget_is_rebuilt_from_observations(self):
        self.observe(60)
        self.observe(56, 6)
        stored = json.loads(self.path.read_text())
        stored['daily_budget']['budget'] = 90
        self.path.write_text(json.dumps(stored))
        self.forecast = QuotaForecast(self.root, self.path)
        self.assertEqual(self.observe(56, 6)['today']['remaining_percent'], 8)

    def test_partial_reset_day_never_allocates_more_than_remaining_quota(self):
        reset = self.start + 16 * 3600
        today = self.observe(10, 12, reset)['today']
        self.assertEqual(today['remaining_percent'], 10)
        self.assertEqual(today['valid_until_ms'], reset * 1000)

    def test_calendar_days_with_23_or_25_hours_use_actual_day_length(self):
        for hours in (23, 25):
            with self.subTest(hours=hours), patch('quota_forecast._day_bounds', return_value=(self.start, self.start + hours * 3600)):
                self.forecast = QuotaForecast(self.root)
                self.assertEqual(self.observe(60)['today']['remaining_percent'], hours / 2)

    def test_clock_rollback_does_not_reuse_a_future_anchor(self):
        self.observe(60, 12)
        today = self.observe(59, 10)['today']
        self.assertEqual(today['anchor_at_ms'], (self.start + 10 * 3600) * 1000)
        self.assertEqual(today['spent_percent'], 0)


if __name__ == '__main__':
    unittest.main()
