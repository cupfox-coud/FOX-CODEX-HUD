"""Local Codex live throughput, inspired by zoefix/agent-speed METRICS 3 and 6.

Measured output is spread over its request interval. In-flight output is an
explicit estimate from recent measured responses, never added to token totals.
See docs/SPEED.md and licenses/agent-speed-MIT.txt for attribution and scope.
"""
from __future__ import annotations

import math
import collections

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


class GlobalSpeed:
    """Bounded Codex throughput, independent of the selected conversation."""
    def __init__(self):
        self.samples = collections.OrderedDict()
        self._key = None
        self._frame = None

    def compute(self, agents, now_ms, complete=True):
        key = (int(now_ms // 500), complete, tuple(sorted(
            (aid, m.event_at, m.last_usage_at, m.model or '', m.stage, m.turn_active,
             m.request_boundary, len(m.speed_samples),
             m.response_history.last_usage_at if m.response_history else 0) for aid, m in agents)))
        if key == self._key:
            return self._frame
        for aid, m in sorted(agents):
            for row in list((m.response_history or m).speed_samples) + list(m.speed_samples):
                if now_ms - RETENTION_MS <= row['end_ms'] <= now_ms:
                    identity = row.get('global_id') or (aid, row['id'])
                    if identity not in self.samples:
                        self.samples[identity] = dict(row, agent=aid)
        self.samples = collections.OrderedDict((k, r) for k, r in self.samples.items()
                                               if r['end_ms'] >= now_ms - RETENTION_MS)
        while len(self.samples) > 16384:
            self.samples.popitem(last=False)
        samples = sorted(self.samples.values(), key=lambda r: r['end_ms'])
        by_agent = collections.defaultdict(list)
        by_model = collections.defaultdict(list)
        for row in samples:
            by_agent[(row['agent'], row['model'])].append(row)
            by_model[row['model']].append(row)
        window_samples = [r for r in samples if r['end_ms'] >= now_ms - 60_000]
        estimates = []
        generating = tools = warming = 0
        for aid, m in agents:
            fresh = m.turn_active is True and 0 <= now_ms - m.event_at <= STALE_MS
            if not fresh:
                continue
            tools += m.stage == 'tools'
            if m.stage != 'generating':
                continue
            generating += 1
            own = by_agent[(aid, m.model)]
            reference = [r for r in own if r['end_ms'] >= now_ms - RECENT_MS][-3:]
            if not reference:
                reference = by_model[m.model][-10:] or samples[-10:]
            boundary = m.request_boundary
            if not reference or not number(boundary) or boundary > now_ms:
                warming += 1
                continue
            rate = sum(r['output_tokens'] for r in reference) * 1000 / sum(
                r['end_ms'] - r['start_ms'] for r in reference)
            local = list((m.response_history or m).speed_samples) + list(m.speed_samples)
            boundary = max(boundary, max((r['end_ms'] for r in local), default=0))
            estimates.append((boundary, rate))

        def rates(begin, end):
            measured = sum(r['output_tokens'] * overlap(begin, end, r['start_ms'], r['end_ms']) /
                           (r['end_ms'] - r['start_ms']) for r in window_samples if r['end_ms'] >= begin)
            estimated = sum(rate * overlap(begin, end, start, now_ms) / 1000 for start, rate in estimates)
            return measured, estimated

        measured, estimated = rates(now_ms - WINDOW_MS, now_ms)
        total = measured + estimated
        state = 'estimating' if estimated > 0 else 'measured' if measured > 0 else (
            'warming' if generating else 'tools' if tools else 'idle')
        history = []
        last = int(now_ms // 1000) - (now_ms % 1000 == 0)
        for second in range(last - 59, last + 1):
            begin, end = second * 1000, min(now_ms, (second + 1) * 1000)
            actual, predicted = rates(begin, end)
            history.append(dict(at_ms=begin, measured=actual * 1000 / (end-begin),
                                estimated=predicted * 1000 / (end-begin)))
        recent = samples[-10:]
        cache = [r for r in recent if r.get('cache_valid') and r['input_tokens'] > 0]
        inputs = sum(r['input_tokens'] for r in cache)
        avg = sum(r['output_tokens'] for r in recent) * 1000 / sum(
            r['end_ms'] - r['start_ms'] for r in recent) if recent else None
        frame = dict(version=1, scope='global', window_ms=WINDOW_MS, state=state, updated_at_ms=now_ms,
                     average_30s=sum(rates(now_ms - 30_000, now_ms)) / 30,
                     rate=total * 1000 / WINDOW_MS if state != 'warming' else None,
                     measured_rate=measured * 1000 / WINDOW_MS, estimated_rate=estimated * 1000 / WINDOW_MS,
                     estimated_share=estimated / total if total else 0,
                     generating=generating, tools=tools, warming=warming, tracked=len(agents), complete=complete,
                     recent_rate=avg, steady_rate=avg, last_response_rate=avg,
                     last_response_at_ms=max((r['end_ms'] for r in samples), default=0),
                     cache_percent=sum(r['cached_input_tokens'] for r in cache)*100/inputs if inputs else None,
                     confirmed_turn_output=sum(r['output_tokens'] for r in samples),
                     peak_rate=max((sum(p['measured'] + p['estimated'] for p in history[i-2:i+1])/3
                                    for i in range(2, len(history))), default=0),
                     history=history, history_unit='tok/s')
        self._key, self._frame = key, frame
        return frame
