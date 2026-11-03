"""Estimate weekly runway from the account's quota window and reset time.

The cycle average assumes the weekly balance began at 100%. A detected increase
within a cycle switches to observations made after that increase. No token rate,
session log, message text, account identity or path is used in the estimate.
"""
from __future__ import annotations

import json
import math
import os
from datetime import datetime, time, timedelta
from pathlib import Path

WINDOW_MINUTES = 10080
LOOKBACK_SECONDS = 7 * 86400
MIN_SPAN_SECONDS = 6 * 3600
MIN_SPENT_PERCENT = 2.0
PUBLISH_INTERVAL = 10 * 60
RESET_TOLERANCE = 120
MAX_SAMPLES = 400
MAX_HISTORY_BYTES = 128 * 1024
METHOD = 'weekly_cycle_v3'
BASELINE_PER_DAY = 100.0 / 7.0


def _day_bounds(now):
    """System-local calendar day (including DST); the installed machine uses China time."""
    day = datetime.fromtimestamp(now).date()
    return (datetime.combine(day, time()).timestamp(),
            datetime.combine(day + timedelta(days=1), time()).timestamp())


def _number(value):
    return (isinstance(value, (int, float)) and not isinstance(value, bool)
            and math.isfinite(value))


def _window(value, now):
    if not isinstance(value, dict) or not _number(now):
        return None
    duration, remaining, reset = (value.get(k) for k in ('window_minutes', 'remaining_percent', 'resets_at'))
    if not all(_number(n) for n in (duration, remaining, reset)):
        return None
    if (duration != WINDOW_MINUTES or not 0 <= remaining <= 100 or reset <= now or
            reset > now + WINDOW_MINUTES * 60 + RESET_TOLERANCE):
        return None
    return float(remaining), float(reset)


def _round_hours(hours):
    if hours < 0.25:
        return 0.0  # Display as "less than 15 minutes", not a fabricated 15 minutes.
    if hours >= 48:
        step = 2.0
    elif hours >= 3:
        step = 1.0
    else:
        step = 0.25
    return max(step, round(hours / step) * step)


class QuotaForecast:
    def __init__(self, codex_home: Path, history_file: Path | None = None):
        self.codex_home = Path(codex_home)
        self.history_file = Path(history_file) if history_file else None
        self.samples = []
        self.reset = None
        self.published = None
        self.published_at = 0.0
        self.refill_anchor = None
        self.daily_budget = None
        self._load()

    def _load(self):
        try:
            if not self.history_file or self.history_file.stat().st_size > MAX_HISTORY_BYTES:
                return
            data = json.loads(self.history_file.read_text(encoding='utf-8'))
            if not isinstance(data, dict) or data.get('version') != 1 or not _number(data.get('reset')):
                return
            points = data.get('samples')
            if not isinstance(points, list):
                return
            good = []
            for point in points[-MAX_SAMPLES:]:
                if (isinstance(point, list) and len(point) == 2 and all(_number(x) for x in point)
                        and 0 <= point[1] <= 100):
                    good.append((float(point[0]), float(point[1])))
            self.samples = sorted(good)
            self.reset = float(data['reset'])
            daily = data.get('daily_budget')
            keys = ('day_start', 'day_end', 'reset', 'anchor_at', 'balance', 'budget', 'spent')
            if (isinstance(daily, dict) and all(_number(daily.get(k)) for k in keys)
                    and 20 * 3600 <= daily['day_end'] - daily['day_start'] <= 27 * 3600
                    and daily['day_start'] <= daily['anchor_at'] < min(daily['day_end'], daily['reset'])
                    and abs(daily['reset'] - self.reset) <= RESET_TOLERANCE
                    and all(0 <= daily[k] <= 100 for k in ('balance', 'budget', 'spent'))):
                expected = daily['balance'] * (min(daily['day_end'], daily['reset']) - daily['anchor_at']) / (daily['reset'] - daily['anchor_at'])
                if abs(daily['budget'] - expected) < 0.000001:
                    self.daily_budget = {k: float(daily[k]) for k in keys}
            anchor = data.get('refill_anchor')
            if (isinstance(anchor, list) and len(anchor) == 2 and all(_number(x) for x in anchor)
                    and 0 <= anchor[1] <= 100
                    and self.reset - LOOKBACK_SECONDS - RESET_TOLERANCE <= anchor[0] < self.reset):
                self.refill_anchor = (float(anchor[0]), float(anchor[1]))
            # Preserve a known discontinuity even when importing an old cache.
            for i in range(1, len(self.samples)):
                if (self.samples[i][1] > self.samples[i - 1][1] + 0.5 and
                        self.reset - LOOKBACK_SECONDS - RESET_TOLERANCE <= self.samples[i][0] < self.reset and
                        (not self.refill_anchor or self.samples[i][0] > self.refill_anchor[0])):
                    self.refill_anchor = self.samples[i]
            published = data.get('published')
            if (isinstance(published, dict) and published.get('state') in ('estimated', 'through_reset')
                    and published.get('method') == METHOD
                    and _number(data.get('published_at'))
                    and all(_number(published.get(k)) for k in
                            ('reset_hours', 'runway_fraction', 'rate_pct_per_day', 'gap_hours', 'span_hours'))
                    and 0 <= published['reset_hours'] <= 168
                    and 0 <= published['runway_fraction'] <= 1
                    and 0 <= published['gap_hours'] <= 168
                    and 6 <= published['span_hours'] <= 168
                    and 0 < published['rate_pct_per_day'] <= 400
                    and published.get('basis') in ('cycle', 'after_refill')
                    and (published.get('basis') == 'after_refill') == bool(self.refill_anchor)
                    and (published['state'] != 'estimated' or
                         (_number(published.get('hours')) and 0 <= published['hours'] <= 168))):
                # Forecasts from the old six-hour method must be recalculated.
                self.published = published
                self.published_at = float(data['published_at'])
        except (OSError, ValueError, TypeError, OverflowError):
            return

    def _save(self):
        if not self.history_file or self.reset is None:
            return
        payload = {'version': 1, 'reset': self.reset, 'samples': self.samples[-MAX_SAMPLES:],
                   'published': self.published, 'published_at': self.published_at,
                   'refill_anchor': self.refill_anchor, 'daily_budget': self.daily_budget}
        try:
            self.history_file.parent.mkdir(parents=True, exist_ok=True)
            temporary = self.history_file.with_name(self.history_file.name + '.new')
            temporary.write_text(json.dumps(payload, separators=(',', ':')), encoding='utf-8')
            os.replace(temporary, self.history_file)
        except (OSError, ValueError, TypeError):
            pass  # The live quota display still works when the optional cache is unavailable.

    def _today_budget(self, remaining, reset, now):
        try:
            start, end = _day_bounds(now)
        except (OSError, ValueError, OverflowError):
            return {'state': 'unavailable'}
        daily = self.daily_budget
        if (not daily or daily['day_start'] != start or daily['day_end'] != end
                or abs(daily['reset'] - reset) > RESET_TOLERANCE
                or daily['anchor_at'] > now + 5
                or (self.refill_anchor and self.refill_anchor[0] > daily['anchor_at'])):
            earliest = max(start, reset - LOOKBACK_SECONDS,
                           self.refill_anchor[0] if self.refill_anchor else start)
            # Reuse today's real observations on upgrade. Never invent a midnight
            # balance or describe the unobserved morning as measured usage.
            points = [(at, value) for at, value in self.samples if earliest <= at <= now]
            anchor_at, balance = min(points, default=(now, remaining))
            budget = balance * (min(end, reset) - anchor_at) / (reset - anchor_at)
            daily = {'day_start': start, 'day_end': end, 'reset': reset,
                     'anchor_at': anchor_at, 'balance': balance,
                     'budget': max(0.0, min(balance, budget)), 'spent': 0.0}
        # A frozen allocation is debited by real balance loss. Passing time alone
        # never tops it up; tiny upward corrections cannot replenish the allowance.
        spent = max(daily['spent'], daily['balance'] - remaining)
        daily = dict(daily, spent=spent)
        self.daily_budget = daily
        # Keep the signed deficit: zero means exactly on budget, not every overrun.
        available = min(remaining, daily['budget'] - spent)
        if abs(available) < 1e-9:
            available = 0.0
        return {'state': 'over_budget' if available < 0 else 'spent' if available == 0 else 'ok',
                'remaining_percent': available, 'allocated_percent': daily['budget'],
                'spent_percent': spent, 'anchor_at_ms': daily['anchor_at'] * 1000,
                'day_start_ms': start * 1000, 'day_end_ms': end * 1000,
                'valid_until_ms': min(end, reset) * 1000}

    def observe(self, window, now):
        current = _window(window, now)
        if current is None:
            return {'state': 'unavailable'}
        remaining, reset = current
        changed_cycle = self.reset is None or abs(reset - self.reset) > RESET_TOLERANCE
        if changed_cycle:
            self.samples, self.published, self.published_at = [], None, 0.0
            self.refill_anchor = None
            self.daily_budget = None
        self.reset = reset
        if self.refill_anchor and self.refill_anchor[0] > now + 5:
            # A backwards clock jump must not mix pre- and post-adjustment data.
            self.refill_anchor = (now, remaining)
            self.samples, self.published, self.published_at = [], None, 0.0
        self.samples = [(at, value) for at, value in self.samples
                        if now - LOOKBACK_SECONDS <= at <= now + 5 and 0 <= value <= 100]
        if self.samples and remaining > self.samples[-1][1] + 0.5:
            self.samples, self.published, self.published_at = [], None, 0.0
            # A refill or account change breaks the full-cycle assumption.
            self.refill_anchor = (now, remaining)
            self.daily_budget = None

        if not self.samples or now - self.samples[-1][0] >= 300 or abs(remaining - self.samples[-1][1]) >= 0.5:
            self.samples.append((now, remaining))
            self.samples = self.samples[-MAX_SAMPLES:]
            changed = True
        else:
            changed = False

        previous_daily = self.daily_budget
        today = self._today_budget(remaining, reset, now)
        daily_changed = previous_daily != self.daily_budget

        reset_hours = (reset - now) / 3600.0
        # Near the instant of reset, a per-day allowance grows without bound.
        allowance = round(remaining * 24.0 / reset_hours, 1) if reset_hours >= 0.25 else None
        common = {'method': METHOD, 'reset_hours': _round_hours(reset_hours),
                  'baseline_pct_per_day': round(BASELINE_PER_DAY, 1),
                  'allowance_pct_per_day': allowance, 'calculated_at_ms': now * 1000}
        if remaining == 0:
            self.published, self.published_at = dict(common, state='exhausted'), now
            self._save()
            return dict(self.published, today=today)
        previous_remaining = self.published.get('remaining_at_publish') if self.published else None
        substantial_change = _number(previous_remaining) and previous_remaining - remaining >= 5
        if (self.published and self.published.get('state') in ('estimated', 'through_reset') and
                0 <= now - self.published_at < PUBLISH_INTERVAL and not substantial_change):
            if changed or daily_changed:
                self._save()
            return dict(self.published, today=today)

        if self.refill_anchor:
            basis = 'after_refill'
            span = max(0.0, now - self.refill_anchor[0])
            spent = max(0.0, self.refill_anchor[1] - remaining)
        else:
            basis = 'cycle'
            span = max(0.0, WINDOW_MINUTES * 60.0 - (reset - now))
            spent = max(0.0, 100.0 - remaining)
        if span < MIN_SPAN_SECONDS or spent < MIN_SPENT_PERCENT:
            result = dict(common, state='observing', basis=basis)
        else:
            rate = spent * 86400.0 / span
            safe_rate = remaining * 24.0 / reset_hours
            old = self.published if isinstance(self.published, dict) else {}
            old_rate = old.get('rate_pct_per_day')
            # Hold the pace, not just the text, so the rate, timeline and gap
            # always describe the same estimate. Do not hide a risk transition.
            if (not substantial_change and old.get('method') == METHOD and old.get('basis') == basis
                    and _number(old_rate) and old_rate > 0
                    and (old_rate > safe_rate) == (rate > safe_rate)
                    and abs(rate - old_rate) < max(0.2, old_rate * 0.05)):
                rate = old_rate
            rate = max(0.01, rate)  # Keep the comparison precise at the reset boundary.
            hours = remaining * 24.0 / rate
            rounded_reset = _round_hours(reset_hours)
            rounded_hours = _round_hours(hours)
            gap = max(0.0, rounded_reset - rounded_hours) if hours < reset_hours else 0.0
            result = dict(common, state='through_reset' if hours >= reset_hours else 'estimated',
                          basis=basis, span_hours=min(168.0, span / 3600.0),
                          remaining_at_publish=remaining, runway_fraction=min(1.0, hours / reset_hours),
                          gap_hours=gap, gap_under_15_minutes=0 <= reset_hours - hours < 0.25,
                          rate_pct_per_day=rate)
            if result['state'] == 'estimated':
                result['hours'] = rounded_hours
        self.published, self.published_at = result, now
        self._save()
        return dict(result, today=today)
