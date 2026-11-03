"""Local Codex live throughput, inspired by zoefix/agent-speed METRICS 3 and 6.

Measured output is spread over its request interval. In-flight output is an
explicit estimate from recent measured responses, never added to token totals.
See docs/SPEED.md and licenses/agent-speed-MIT.txt for attribution and scope.
"""
from __future__ import annotations

import math

WINDOW_MS = 3000
MIN_DURATION_MS = 500
MAX_DURATION_MS = 15 * 60_000
RECENT_MS = 10 * 60_000
RETENTION_MS = 30 * 60_000
STALE_MS = 5 * 60_000


def number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def overlap(a, b, c, d):
    return max(0, min(b, d) - max(a, c))


def interval(start, end):
    if not number(start) or not number(end) or end < start or end - start > MAX_DURATION_MS:
        return None
    return max(start, end - MAX_DURATION_MS) if end - start >= MIN_DURATION_MS else end - MIN_DURATION_MS, end


class LiveSpeed:
    """Per-conversation engine; explicit clocks make estimation and decay testable."""
    def __init__(self):
        self._key = None
        self._frame = None

    def compute(self, measurements, now_ms):
        history = measurements.response_history or measurements
        key = (int(now_ms // 500), measurements.event_at, measurements.last_usage_at,
               measurements.model, measurements.stage, measurements.turn_active,
               len(history.speed_samples), history.last_usage_at)
        if key == self._key:
            return self._frame
        samples = []
        seen = set()
        for row in list(history.speed_samples) + list(measurements.speed_samples):
            if row['id'] in seen or row['end_ms'] < now_ms - RETENTION_MS or row['end_ms'] > now_ms + 30_000:
                continue
            seen.add(row['id'])
            if row['model'] == measurements.model:
                samples.append(row)
        samples.sort(key=lambda row: row['end_ms'])
        recent = [row for row in samples if row['end_ms'] >= now_ms - RECENT_MS][-3:]
        reference = recent or samples[-10:]
        recent_speed = (sum(row['output_tokens'] for row in reference) * 1000 /
                        sum(row['end_ms'] - row['start_ms'] for row in reference)) if reference else None
        stable_reference = samples[-10:]
        stable_speed = (sum(row['output_tokens'] for row in stable_reference) * 1000 /
                        sum(row['end_ms'] - row['start_ms'] for row in stable_reference)) if stable_reference else None
        last_end = max((row['end_ms'] for row in samples), default=0)
        estimate_start = measurements.request_boundary
        # A log heartbeat is not proof of generation; require explicit active lifecycle.
        estimating = (measurements.turn_active is True and measurements.stage == 'generating' and
                      number(estimate_start) and estimate_start <= now_ms and
                      0 <= now_ms - measurements.event_at <= STALE_MS)
        estimate_start = max(estimate_start, last_end) if estimating else now_ms
        can_estimate = estimating and recent_speed is not None

        def rates(begin, end):
            measured = sum(row['output_tokens'] * overlap(begin, end, row['start_ms'], row['end_ms']) /
                           (row['end_ms'] - row['start_ms']) for row in samples)
            estimated = recent_speed * overlap(begin, end, estimate_start, now_ms) / 1000 if can_estimate else 0
            return measured, estimated

        measured, estimated = rates(now_ms - WINDOW_MS, now_ms)
        live = (measured + estimated) * 1000 / WINDOW_MS
        state = ('estimating' if estimated > 0 else 'measured' if measured > 0 else
                 'warming' if estimating and recent_speed is None else
                 'stale' if measurements.turn_active is True and now_ms - measurements.event_at > STALE_MS else
                 'tools' if measurements.turn_active is True and measurements.stage == 'tools' else
                 'idle' if measurements.turn_active is False else 'unknown')
        buckets = []
        last_second = int(now_ms // 1000)
        if now_ms == last_second * 1000:
            last_second -= 1  # The new second has no observations yet.
        for second in range(last_second - 59, last_second + 1):
            begin, end = second * 1000, min(now_ms, (second + 1) * 1000)
            actual, predicted = rates(begin, end)
            # These are speed points, including the unfinished current second.
            # Plotting raw partial token amounts creates a false dip every second.
            elapsed = end - begin
            buckets.append({'at_ms': begin, 'measured': actual * 1000 / elapsed,
                            'estimated': predicted * 1000 / elapsed})
        cache_rows = [row for row in stable_reference if row.get('cache_valid') and row['input_tokens'] > 0]
        input_tokens = sum(row['input_tokens'] for row in cache_rows)
        frame = {
            'version': 1, 'state': state, 'scope': 'conversation', 'window_ms': WINDOW_MS,
            'updated_at_ms': now_ms, 'rate': live if state not in ('unknown', 'warming', 'stale') else None,
            'measured_rate': measured * 1000 / WINDOW_MS,
            'estimated_rate': estimated * 1000 / WINDOW_MS,
            'estimated_share': estimated / (measured + estimated) if measured + estimated > 0 else 0,
            'recent_rate': recent_speed, 'reference_count': len(reference),
            'steady_rate': stable_speed, 'steady_reference_count': len(stable_reference),
            'peak_rate': max((row['output_tokens'] * 1000 / (row['end_ms'] - row['start_ms']) for row in samples), default=None),
            'last_response_at_ms': last_end,
            'last_response_rate': (samples[-1]['output_tokens'] * 1000 /
                                   (samples[-1]['end_ms'] - samples[-1]['start_ms'])) if samples else None,
            'confirmed_turn_output': measurements.turn_output,
            'cache_percent': sum(row['cached_input_tokens'] for row in cache_rows) * 100 / input_tokens if input_tokens else None,
            'history': buckets, 'history_unit': 'tok/s',
        }
        self._key, self._frame = key, frame
        return frame
