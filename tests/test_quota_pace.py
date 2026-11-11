import unittest
from quota_forecast import QuotaPace


def window(value, reset=1800604800):
    return dict(window_minutes=10080, remaining_percent=value, resets_at=reset)


class PaceTests(unittest.TestCase):
    start = 1800000000

    def test_real_debits_define_rate_not_tokens_or_cycle_average(self):
        p = QuotaPace()
        for seconds, value in ((0, 60), (30, 60), (60, 59), (120, 58), (180, 58), (240, 58), (300, 57)):
            p.observe(window(value), self.start + seconds)
        self.assertEqual(p.snapshot(self.start + 300)['rate_pct_per_hour'], 36)

    def test_time_alone_cannot_create_new_source_observations(self):
        p = QuotaPace(); p.observe(window(60), self.start)
        self.assertEqual(p.snapshot(self.start + 120)['state'], 'warming')
        p.observe(window(59), self.start + 120)
        a, b = p.snapshot(self.start + 121), p.snapshot(self.start + 130)
        self.assertEqual(a['rate_pct_per_hour'], b['rate_pct_per_hour'])
        self.assertEqual(a['sample_at_ms'], b['sample_at_ms'])

    def test_constant_confirmed_balance_is_zero_after_enough_observation(self):
        p = QuotaPace(); p.observe(window(60), self.start); p.observe(window(60), self.start + 60)
        self.assertEqual(p.snapshot(self.start + 60)['rate_pct_per_hour'], 0)

    def test_stale_missing_and_out_of_order_are_not_zero(self):
        p = QuotaPace(); self.assertIsNone(p.snapshot(self.start)['rate_pct_per_hour'])
        p.observe(window(60), self.start); p.observe(window(59), self.start + 60)
        p.observe(window(1), self.start + 30)
        self.assertEqual(p.snapshot(self.start + 60)['rate_pct_per_hour'], 60)
        self.assertIsNone(p.snapshot(self.start + 241)['rate_pct_per_hour'])

    def test_reset_refill_or_unobserved_gap_restarts_sampling(self):
        for second, value, reset in ((90, 100, 1800604800), (90, 59, 1800604500), (300, 58, 1800604800)):
            with self.subTest(case=(second, value, reset)):
                p = QuotaPace(); p.observe(window(60), self.start); p.observe(window(59), self.start + 60)
                p.observe(window(value, reset), self.start + second)
                self.assertEqual(p.snapshot(self.start + second)['state'], 'warming')

    def test_small_correction_cannot_charge_the_same_loss_twice(self):
        p = QuotaPace()
        for second, value in ((0, 60), (60, 59), (90, 60), (120, 59)):
            p.observe(window(value), self.start + second)
        self.assertEqual(p.snapshot(self.start + 120)['rate_pct_per_hour'], 30)

    def test_window_boundary_interpolates_only_a_measured_interval(self):
        p = QuotaPace()
        for second, value in ((0, 60), (120, 58), (240, 56), (360, 54)):
            p.observe(window(value), self.start + second)
        self.assertEqual(p.snapshot(self.start + 360)['rate_pct_per_hour'], 60)
        self.assertEqual(p.snapshot(self.start + 360)['span_seconds'], 300)

    def test_history_seed_and_memory_remain_bounded(self):
        points = [(self.start+i, 60-i/10000) for i in range(1000)]
        p = QuotaPace(points, 1800604800)
        self.assertLessEqual(len(p.points), 400)
        self.assertAlmostEqual(p.snapshot(self.start+999)['rate_pct_per_hour'], .36)

    def test_missing_weekly_window_invalidates_old_rate(self):
        p = QuotaPace(); p.observe(window(60), self.start); p.observe(window(59), self.start+60)
        p.observe(None, self.start+90)
        self.assertIsNone(p.snapshot(self.start+90)['rate_pct_per_hour'])
