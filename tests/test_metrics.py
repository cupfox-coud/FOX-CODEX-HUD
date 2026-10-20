import datetime as dt
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import sqlite3
import subprocess
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from metrics import Measurements, SessionTail, TailCache, ThreadRepository, cache_percent, monitor, reverse_small_lines


def event(seconds, kind, payload):
    at = dt.datetime.fromtimestamp(1700000000 + seconds, dt.timezone.utc).isoformat()
    return {"timestamp": at, "type": kind, "payload": payload}


def usage(output=100, cached=750, input_tokens=1000):
    return {"input_tokens": input_tokens, "cached_input_tokens": cached,
            "output_tokens": output, "reasoning_output_tokens": 30}


class MeasurementsTest(unittest.TestCase):
    def test_response_mean_is_sum_divided_by_twenty_not_whole_task(self):
        m = Measurements()
        m.feed(event(0, 'event_msg', {'type': 'task_started', 'turn_id': 'long-task'}))
        elapsed = 0
        for i in range(1, 21):
            elapsed += i
            m.feed(event(elapsed, 'token_usage_record', {'response_id': str(i), 'usage': usage()}))
        m.feed(event(3180, 'event_msg', {'type': 'task_complete', 'turn_id': 'long-task'}))
        self.assertEqual(m.snapshot()['response_duration'],
                         {'mean_ms': 10500, 'count': 20, 'limit': 20, 'last_ms': 20000})
        self.assertNotIn('processing_duration', m.snapshot())

    def test_response_mean_excludes_tools_idle_and_unfinished_response(self):
        m = Measurements()
        rows = [event(0, 'event_msg', {'type': 'task_started', 'turn_id': 'a'}),
            event(4, 'response_item', {'type': 'function_call', 'call_id': 'tool'}),
            event(5, 'token_usage_record', {'response_id': 'r1', 'usage': usage()}),
            event(3605, 'response_item', {'type': 'function_call_output', 'call_id': 'tool'}),
            event(3615, 'token_usage_record', {'response_id': 'r2', 'usage': usage()}),
            event(3616, 'event_msg', {'type': 'task_complete', 'turn_id': 'a'}),
            event(7200, 'event_msg', {'type': 'task_started', 'turn_id': 'b'}),
            event(7215, 'token_usage_record', {'response_id': 'r3', 'usage': usage()}),
            event(7216, 'response_item', {'type': 'function_call', 'call_id': 'unfinished'})]
        for row in rows:
            m.feed(row)
        self.assertEqual(m.snapshot()['response_duration']['mean_ms'], 10000)
        self.assertEqual(m.snapshot()['response_duration']['count'], 3)

    def test_response_mean_slides_over_latest_twenty_and_skips_missing_times(self):
        m = Measurements()
        m.feed(event(0, 'token_usage_record', {'response_id': 'missing-start', 'usage': usage()}))
        self.assertEqual(m.snapshot()['response_duration']['count'], 0)
        for i in range(25):
            m.feed(event(i * 100 + 1, 'event_msg', {'type': 'task_started', 'turn_id': str(i)}))
            m.feed(event(i * 100 + i + 2, 'token_usage_record', {'response_id': str(i), 'usage': usage()}))
        self.assertEqual(m.snapshot()['response_duration']['count'], 20)
        self.assertEqual(m.snapshot()['response_duration']['mean_ms'], 15500)

    def test_response_average_recovers_prefix_and_cross_boundary_timing(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'large.jsonl'
            records = [event(0, 'event_msg', {'type': 'task_started', 'turn_id': 'same-task'})]
            for i in range(80):
                records += [event(i * 10 + 5, 'token_usage_record', {'response_id': str(i), 'usage': usage()})]
            records += [event(796, 'response_item', {'type': 'function_call', 'call_id': 'tool'}),
                event(3600, 'response_item', {'type': 'function_call_output', 'call_id': 'tool'}),
                event(3601, 'response_item', {'image': 'x' * 300000}),
                event(3615, 'token_usage_record', {'response_id': 'straddle', 'usage': usage()}),
                event(3616, 'event_msg', {'type': 'token_count', 'info': {
                    'last_token_usage': usage(), 'total_token_usage': {'output_tokens': 8100}}})]
            path.write_text(''.join(json.dumps(e) + '\n' for e in records), encoding='utf-8')
            full = Measurements()
            for record in records:
                full.feed(record)
            tail = SessionTail(str(path), history_bytes=1000)
            tail.update()
            self.assertEqual(tail.measurements.snapshot()['response_duration'], full.snapshot()['response_duration'])
            self.assertEqual(tail.measurements.snapshot()['response_duration']['mean_ms'], 10250)
            self.assertEqual(tail.measurements.output_total, 100)
            self.assertIsNone(tail.measurements.last['rate'])
            tail.update()
            tail.reset()
            tail.update()
            self.assertEqual(tail.measurements.snapshot()['response_duration'], full.snapshot()['response_duration'])
            with path.open('a', encoding='utf-8') as stream:
                stream.write(json.dumps(event(3620, 'token_usage_record', {'response_id': 'new', 'usage': usage()})) + '\n')
            tail.update()
            self.assertEqual(tail.measurements.snapshot()['response_duration']['mean_ms'], 10000)
            other = SessionTail(str(Path(temporary) / 'absent'))
            other.update()
            self.assertEqual(other.measurements.snapshot()['response_duration']['count'], 0)

    def test_response_average_deduplicates_record_and_legacy_notifications(self):
        m = Measurements()
        m.feed(event(0, 'event_msg', {'type': 'task_started'}))
        for i in range(1, 4):
            payload = {'response_id': str(i), 'usage': usage()}
            m.feed(event(i * 5, 'token_usage_record', payload))
            m.feed(event(i * 5 + 1, 'token_usage_record', payload))
            m.feed(event(i * 5 + 2, 'event_msg', {'type': 'token_count', 'info': {
                'last_token_usage': usage(), 'total_token_usage': {'output_tokens': i * 100}}}))
        self.assertEqual(m.snapshot()['response_duration']['count'], 3)
        self.assertEqual(m.snapshot()['response_duration']['mean_ms'], 5000)

    def test_response_history_preserves_large_tool_result_boundary(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'image-tool.jsonl'
            rows = [event(0, 'event_msg', {'type': 'task_started'}),
                event(1, 'response_item', {'type': 'function_call', 'call_id': 'image'}),
                event(2, 'token_usage_record', {'response_id': 'first', 'usage': usage()}),
                event(3600, 'response_item', {'type': 'function_call_output', 'call_id': 'image', 'output': 'x' * 300000}),
                event(3605, 'token_usage_record', {'response_id': 'second', 'usage': usage()}),
                event(3615, 'token_usage_record', {'response_id': 'third', 'usage': usage()})]
            path.write_text(''.join(json.dumps(row) + '\n' for row in rows), encoding='utf-8')
            tail = SessionTail(str(path), history_bytes=100)
            tail.update()
            self.assertEqual(tail.measurements.snapshot()['response_duration']['mean_ms'], 17000 / 3)
            self.assertEqual(tail.measurements.snapshot()['response_duration']['count'], 3)
            # Simulate a row beyond the bounded decoder size. The response
            # following the missing boundary must be omitted, never inflated.
            def bounded_reader(*args, **kwargs):
                if kwargs.get('include_gaps'):
                    kwargs['line_limit'] = 1024
                return reverse_small_lines(*args, **kwargs)
            with patch('metrics.reverse_small_lines', side_effect=bounded_reader):
                tail.reset()
                tail.update()
            self.assertEqual(tail.measurements.snapshot()['response_duration']['mean_ms'], 6000)
            self.assertEqual(tail.measurements.snapshot()['response_duration']['count'], 2)

    def test_reverse_reader_crosses_chunk_boundaries_and_skips_large_media(self):
        import io
        raw = b'first\n' + b'x' * 1000 + b'\n' + '中文'.encode() + b'\nlast\n'
        for chunk in (1, 7, 32, 128):
            with self.subTest(chunk=chunk):
                lines = list(reverse_small_lines(io.BytesIO(raw), len(raw), chunk, 64))
                self.assertEqual([line for line in lines if line], [b'last', '中文'.encode(), b'first'])

    def test_large_tail_recovers_explicit_lifecycle_without_replaying_old_usage(self):
        for ending in (None, 'task_complete', 'turn_aborted', 'old_turn_complete'):
            with self.subTest(ending=ending), tempfile.TemporaryDirectory() as temporary:
                path = Path(temporary) / 'large.jsonl'
                prefix = [event(0, 'event_msg', {'type': 'task_started', 'turn_id': 'current'}),
                          event(1, 'token_usage_record', {'response_id': 'old', 'usage': usage(900)})]
                if ending:
                    prefix.append(event(2, 'event_msg', {
                        'type': 'task_complete' if ending == 'old_turn_complete' else ending,
                        'turn_id': 'older' if ending == 'old_turn_complete' else 'current'}))
                prefix.append(event(3, 'response_item', {'type': 'message', 'image': 'x' * 300000}))
                recent = [event(4, 'token_usage_record', {'response_id': 'recent', 'usage': usage(100)}),
                          event(5, 'response_item', {'type': 'function_call', 'call_id': 'call'}),
                          event(6, 'token_usage_record', {'response_id': 'new', 'usage': usage(200)})]
                recent_bytes = ''.join(json.dumps(e) + '\n' for e in recent).encode()
                path.write_bytes(''.join(json.dumps(e) + '\n' for e in prefix).encode() + recent_bytes)
                tail = SessionTail(str(path), history_bytes=len(recent_bytes) + 50)
                tail.update()
                active = ending in (None, 'old_turn_complete')
                self.assertEqual(tail.measurements.turn_active, active)
                self.assertEqual(tail.measurements.turn_id, 'current')
                self.assertEqual(tail.measurements.stage, 'tools' if active else 'idle')
                self.assertEqual(tail.measurements.output_total, 300)
                self.assertEqual(tail.measurements.sample_count, 1)
                self.assertEqual(tail.measurements.last['rate'], 100)
                tail.update()
                self.assertEqual(tail.measurements.output_total, 300)
                with path.open('a', encoding='utf-8') as stream:
                    stream.write(json.dumps(event(7, 'event_msg', {'type': 'task_complete', 'turn_id': 'current'})) + '\n')
                tail.update()
                self.assertFalse(tail.measurements.turn_active)

    def test_large_tail_without_any_lifecycle_stays_unknown(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'unknown.jsonl'
            path.write_text(json.dumps(event(0, 'response_item', {'image': 'x' * 300000})) + '\n' +
                            json.dumps(event(2, 'token_usage_record', {'usage': usage()})) + '\n', encoding='utf-8')
            tail = SessionTail(str(path), history_bytes=1000)
            tail.update()
            self.assertIsNone(tail.measurements.turn_active)
            self.assertIsNone(tail.measurements.last['rate'])

    def test_truncated_tail_recovers_completion_and_accepts_new_start(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'completed.jsonl'
            prefix = [event(1, 'event_msg', {'type': 'task_complete', 'turn_id': 'old'}),
                      event(2, 'response_item', {'image': 'x' * 300000})]
            path.write_text(''.join(json.dumps(e) + '\n' for e in prefix) +
                            json.dumps(event(3, 'token_usage_record', {'usage': usage()})) + '\n', encoding='utf-8')
            tail = SessionTail(str(path), history_bytes=1000)
            tail.update()
            self.assertFalse(tail.measurements.turn_active)
            with path.open('a', encoding='utf-8') as stream:
                stream.write(json.dumps(event(4, 'event_msg', {'type': 'task_started', 'turn_id': 'new'})) + '\n')
            tail.update()
            self.assertTrue(tail.measurements.turn_active)
            self.assertEqual(tail.measurements.turn_id, 'new')
            # Forced rebuild and file replacement must not carry the old state.
            rebuilt = SessionTail(str(path), history_bytes=1000)
            rebuilt.update()
            self.assertTrue(rebuilt.measurements.turn_active)
            path.write_text(json.dumps(event(5, 'event_msg', {'type': 'task_complete'})) + '\n', encoding='utf-8')
            tail.update()
            self.assertFalse(tail.measurements.turn_active)
            self.assertIsNone(tail.measurements.last)

    def test_activity_requires_explicit_turn_lifecycle(self):
        m = Measurements()
        self.assertIsNone(m.snapshot()["turn_active"])
        m.feed(event(0, "response_item", {"type": "function_call", "call_id": "historical"}))
        m.feed(event(1, "response_item", {"type": "function_call_output", "call_id": "historical"}))
        m.feed(event(2, "token_usage_record", {"response_id": "old", "usage": usage()}))
        self.assertIsNone(m.snapshot()["turn_active"])
        m.feed(event(3, "event_msg", {"type": "task_started", "turn_id": "current"}))
        self.assertTrue(m.snapshot()["turn_active"])
        m.feed(event(4, "event_msg", {"type": "task_complete", "turn_id": "current"}))
        self.assertFalse(m.snapshot()["turn_active"])
        self.assertEqual(m.stage, "idle")

    def test_all_supported_completion_and_abort_events_end_activity(self):
        for name in ("task_complete", "task_completed", "turn_complete", "turn_completed", "task_aborted", "turn_aborted"):
            with self.subTest(name=name):
                m = Measurements()
                m.feed(event(0, "event_msg", {"type": "turn_started", "turn_id": "t"}))
                m.feed(event(1, "response_item", {"type": "function_call", "call_id": "c"}))
                m.feed(event(2, "event_msg", {"type": name, "turn_id": "t"}))
                self.assertFalse(m.turn_active)
                self.assertEqual(m.stage, "idle")
                self.assertFalse(m.pending_calls)

    def test_completion_without_start_confirms_idle_history(self):
        m = Measurements()
        m.feed(event(0, "event_msg", {"type": "task_complete"}))
        self.assertFalse(m.snapshot()["turn_active"])
        self.assertEqual(m.stage, "idle")

    def test_late_items_and_tool_records_cannot_reopen_completed_turn(self):
        m = Measurements()
        m.feed(event(0, "event_msg", {"type": "task_started", "turn_id": "t"}))
        m.feed(event(5, "event_msg", {"type": "task_complete", "turn_id": "t"}))
        m.feed(event(6, "event_msg", {"type": "item_completed", "item": {"type": "AgentMessage"},
                "started_at_ms": 1700000001000}))
        self.assertEqual(m.stage, "idle")
        for i, name in enumerate(("function_call", "custom_tool_call", "function_call_output", "custom_tool_call_output")):
            m.feed(event(7 + i, "response_item", {"type": name, "call_id": "late"}))
            self.assertEqual(m.stage, "idle")
            self.assertFalse(m.turn_active)
            self.assertFalse(m.pending_calls)
        # Late final token statistics are still useful, and retain the item timing.
        m.feed(event(11, "token_usage_record", {"response_id": "final", "usage": usage(100)}))
        self.assertEqual(m.last["rate"], 10)
        self.assertEqual(m.output_total, 100)
        self.assertFalse(m.turn_active)
        self.assertEqual(m.stage, "idle")

    def test_new_turn_can_start_after_completed_turn(self):
        m = Measurements()
        m.feed(event(0, "event_msg", {"type": "task_started", "turn_id": "old"}))
        m.feed(event(1, "event_msg", {"type": "task_complete", "turn_id": "old"}))
        m.feed(event(2, "event_msg", {"type": "turn_started", "turn_id": "new"}))
        m.feed(event(3, "response_item", {"type": "function_call", "call_id": "current"}))
        self.assertTrue(m.turn_active)
        self.assertEqual(m.stage, "tools")
        m.feed(event(4, "event_msg", {"type": "turn_aborted", "turn_id": "old"}))
        self.assertTrue(m.turn_active)
        self.assertEqual(m.stage, "tools")
        m.feed(event(5, "response_item", {"type": "function_call_output", "call_id": "current"}))
        self.assertEqual(m.stage, "generating")

    def test_tool_activity_is_newer_than_last_token_sample(self):
        m = Measurements()
        m.feed(event(0, "event_msg", {"type": "task_started"}))
        m.feed(event(5, "token_usage_record", {"response_id": "r", "usage": usage()}))
        m.feed(event(50, "response_item", {"type": "function_call", "call_id": "c"}))
        snapshot = m.snapshot()
        self.assertEqual(snapshot["last_event_at_ms"], 1700000050000)
        self.assertEqual(snapshot["last"]["measured_at_ms"], 1700000005000)
        self.assertTrue(snapshot["turn_active"])

    def test_model_stream_timing_excludes_prefill_and_tools(self):
        m = Measurements("example-model")
        m.feed(event(0, "event_msg", {"type": "task_started", "turn_id": "t"}))
        m.feed(event(5, "event_msg", {"type": "item_completed", "item": {"type": "Reasoning"},
                "started_at_ms": 1700000002000, "completed_at_ms": 1700000005000}))
        m.feed(event(6, "response_item", {"type": "custom_tool_call", "call_id": "c"}))
        m.feed(event(7, "token_usage_record", {"response_id": "r", "usage": usage(100)}))
        self.assertEqual(m.last["rate"], 20)
        self.assertEqual(m.last["basis"], "stream")
        m.feed(event(107, "response_item", {"type": "custom_tool_call_output", "call_id": "c"}))
        m.feed(event(110, "event_msg", {"type": "item_completed", "item": {"type": "AgentMessage"},
                "started_at_ms": 1700000108000, "completed_at_ms": 1700000110000}))
        m.feed(event(112, "token_usage_record", {"response_id": "r2", "usage": usage(80)}))
        self.assertEqual(m.last["rate"], 20)
        self.assertEqual(m.output_total, 180)
        self.assertEqual(m.last["cache_percent"], 75)

    def test_record_followed_by_token_count_does_not_double_count(self):
        m = Measurements()
        m.feed(event(0, "event_msg", {"type": "task_started"}))
        p = {"response_id": "r", "usage": usage(), "thread_token_usage": {"output_tokens": 100}}
        m.feed(event(5, "token_usage_record", p))
        m.feed(event(7, "event_msg", {"type": "token_count", "info": {
            "last_token_usage": usage(), "total_token_usage": {"output_tokens": 100},
            "model_context_window": 2000}}))
        self.assertEqual(m.sample_count, 1)
        self.assertEqual(m.last["rate"], 20)

    def test_equal_usage_in_distinct_responses_counts_twice(self):
        m = Measurements()
        m.feed(event(0, "event_msg", {"type": "task_started"}))
        m.feed(event(5, "token_usage_record", {"response_id": "r1", "usage": usage()}))
        m.feed(event(10, "token_usage_record", {"response_id": "r2", "usage": usage()}))
        self.assertEqual(m.output_total, 200)
        self.assertEqual(m.sample_count, 2)

    def test_duplicate_response_id_is_ignored(self):
        m = Measurements()
        m.feed(event(0, "event_msg", {"type": "task_started"}))
        p = {"response_id": "r", "usage": usage()}
        m.feed(event(5, "token_usage_record", p))
        m.feed(event(6, "token_usage_record", p))
        self.assertEqual(m.output_total, 100)

    def test_delayed_legacy_notification_with_different_totals_is_not_a_new_sample(self):
        m = Measurements()
        m.feed(event(0, "event_msg", {"type": "task_started"}))
        m.feed(event(5, "token_usage_record", {"response_id": "r", "usage": usage(),
                "thread_token_usage": {"output_tokens": 10000}}))
        m.feed(event(25, "event_msg", {"type": "token_count", "info": {
            "last_token_usage": usage(), "total_token_usage": {"output_tokens": 1000}}}))
        self.assertEqual(m.sample_count, 1)
        self.assertEqual(m.last["rate"], 20)

    def test_legacy_counter_reset_after_resume_keeps_updating(self):
        m = Measurements()
        m.feed(event(0, "event_msg", {"type": "task_started"}))
        m.feed(event(5, "token_usage_record", {"response_id": "r", "usage": usage(),
                "thread_token_usage": {"output_tokens": 10000}}))
        m.feed(event(6, "event_msg", {"type": "token_count", "info": {
            "last_token_usage": usage(), "total_token_usage": {"output_tokens": 1000}}}))
        m.feed(event(10, "event_msg", {"type": "task_started", "turn_id": "new"}))
        m.feed(event(15, "event_msg", {"type": "token_count", "info": {
            "last_token_usage": usage(200), "total_token_usage": {"output_tokens": 200}}}))
        self.assertEqual(m.last["rate"], 40)
        self.assertEqual(m.last["turn_id"], "new")

    def test_identical_legacy_usage_with_increased_total_is_a_new_response(self):
        m = Measurements()
        m.feed(event(0, "event_msg", {"type": "task_started"}))
        for seconds, output in ((3, 100), (6, 200)):
            m.feed(event(seconds, "event_msg", {"type": "token_count", "info": {
                "last_token_usage": usage(), "total_token_usage": {"output_tokens": output}}}))
        self.assertEqual(m.sample_count, 2)
        self.assertEqual(m.output_total, 200)

    def test_missing_timing_stays_unknown(self):
        m = Measurements()
        m.feed(event(5, "token_usage_record", {"response_id": "r", "usage": usage()}))
        self.assertIsNone(m.last["rate"])
        self.assertEqual(m.last["cache_percent"], 75)

    def test_cache_has_correct_denominator_and_missing_is_unknown(self):
        self.assertIsNone(cache_percent({"input_tokens": 1000, "cached_input_tokens": None}))
        self.assertEqual(cache_percent(usage(input_tokens=200, cached=150)), 75)
        self.assertEqual(cache_percent(usage(input_tokens=100, cached=0)), 0)
        self.assertIsNone(cache_percent(usage(input_tokens=0)))
        self.assertIsNone(cache_percent(usage(input_tokens=100, cached=110)))
        self.assertIsNone(cache_percent({"input_tokens": 100}))

    def test_tail_handles_split_utf8_and_partial_json_without_replaying(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "session.jsonl"
            start = (json.dumps(event(0, "event_msg", {"type": "task_started"})) + "\n").encode()
            end = (json.dumps(event(5, "token_usage_record", {"response_id": "r", "usage": usage(),
                                "ignored": "中文"}), ensure_ascii=False) + "\n").encode()
            split = end.index("中".encode()) + 1
            path.write_bytes(start + end[:split])
            tail = SessionTail(str(path))
            tail.update()
            self.assertIsNone(tail.measurements.last)
            with path.open("ab") as f:
                f.write(end[split:])
            tail.update()
            tail.update()
            self.assertEqual(tail.measurements.output_total, 100)
            self.assertEqual(tail.measurements.last["rate"], 20)

    def test_visible_chat_name_is_used_and_duplicate_names_are_not_guessed(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with sqlite3.connect(root / "state_5.sqlite") as c:
                c.execute("CREATE TABLE threads (id TEXT, title TEXT, name TEXT, rollout_path TEXT, model TEXT, updated_at INT, archived INT)")
                c.executemany("INSERT INTO threads VALUES (?,?,?,?,?,?,?)", [
                    ("a", "long first user message", "当前对话", "a.jsonl", "model-a", 1, 0),
                    ("b", "another first user message", "同名对话", "b.jsonl", "model-b", 2, 0),
                    ("c", "third first user message", "同名对话", "c.jsonl", "model-c", 3, 0),
                ])
            c.close()
            repo = ThreadRepository(root)
            repo.refresh()
            self.assertEqual(repo.title_matches("当前对话")[0]["id"], "a")
            self.assertEqual(len(repo.title_matches("同名对话")), 2)
            self.assertEqual(repo.title_matches("long first user message"), [])

    def test_database_version_discovery_and_legacy_optional_columns(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for version in (4, 6):
                with sqlite3.connect(root / f"state_{version}.sqlite") as c:
                    c.execute("CREATE TABLE threads (id TEXT, title TEXT, rollout_path TEXT)")
                    c.execute("INSERT INTO threads VALUES (?,?,?)", (str(version), "old layout", "fixture.jsonl"))
                c.close()
            repo = ThreadRepository(root); repo.refresh()
            self.assertEqual(repo.database.name, "state_6.sqlite")
            self.assertEqual(repo.title_matches("old layout")[0]["id"], "6")
            self.assertIsNone(repo.rows["6"]["model"])

    def test_database_migration_is_detected_while_repository_is_running(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            repo = ThreadRepository(root); repo.refresh()
            self.assertEqual(repo.state, "missing")
            for version in (5, 7):
                with sqlite3.connect(root / f"state_{version}.sqlite") as c:
                    c.execute("CREATE TABLE threads (id TEXT, title TEXT, rollout_path TEXT)")
                    c.execute("INSERT INTO threads VALUES (?,?,?)", (str(version), "chat", "fixture.jsonl"))
                c.close()
                repo.refresh()
                self.assertEqual(repo.database.name, f"state_{version}.sqlite")
                self.assertEqual(repo.state, "ok")

    def test_incompatible_newer_database_does_not_fall_back_to_stale_metadata(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with sqlite3.connect(root / "state_5.sqlite") as c:
                c.execute("CREATE TABLE threads (id TEXT, title TEXT, rollout_path TEXT)")
                c.execute("INSERT INTO threads VALUES ('t', 'chat', 'old.jsonl')")
            c.close()
            with sqlite3.connect(root / "state_7.sqlite") as c:
                c.execute("CREATE TABLE threads (incompatible TEXT)")
            c.close()
            repo = ThreadRepository(root); repo.refresh()
            self.assertEqual(repo.state, "unsupported")
            self.assertEqual(repo.title_matches("chat"), [])

    def test_missing_and_incompatible_database_clear_previous_rows_without_creating_files(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            db = root / "state_5.sqlite"
            with sqlite3.connect(db) as c:
                c.execute("CREATE TABLE threads (id TEXT, title TEXT, rollout_path TEXT)")
                c.execute("INSERT INTO threads VALUES ('t', 'chat', 'fixture.jsonl')")
            c.close()
            repo = ThreadRepository(root); repo.refresh()
            self.assertEqual(len(repo.rows), 1)
            db.unlink(); repo.refresh()
            self.assertEqual(repo.state, "missing")
            self.assertEqual(repo.title_matches("chat"), [])
            self.assertFalse(db.exists())
            with sqlite3.connect(root / "state_7.sqlite") as c:
                c.execute("CREATE TABLE threads (unrelated TEXT)")
            c.close()
            repo.refresh()
            self.assertEqual(repo.state, "unsupported")
            self.assertEqual(repo.rows, {})

    @staticmethod
    def write_log(path, response_id, output=100, padding=""):
        events = [event(0, "event_msg", {"type": "task_started"}),
                  event(5, "token_usage_record", {"response_id": response_id, "usage": usage(output), "padding": padding})]
        path.write_text("".join(json.dumps(e) + "\n" for e in events), encoding="utf-8")

    def test_active_rollout_path_change_for_same_thread_is_followed(self):
        with tempfile.TemporaryDirectory() as temporary:
            old, new = Path(temporary) / "old.jsonl", Path(temporary) / "new.jsonl"
            self.write_log(old, "old", 100)
            self.write_log(new, "new", 200)
            cache = TailCache()
            row = {"id": "same-thread", "rollout_path": str(old), "model": "m"}
            self.assertEqual(cache.update(row).measurements.last["rate"], 20)
            row["rollout_path"] = str(new)
            tail = cache.update(row)
            self.assertEqual(tail.path, new)
            self.assertEqual(tail.measurements.last["rate"], 40)

    def test_log_replaced_with_a_larger_file_resets_read_offset(self):
        with tempfile.TemporaryDirectory() as temporary:
            path, replacement = Path(temporary) / "log.jsonl", Path(temporary) / "replacement.jsonl"
            self.write_log(path, "old", 100)
            tail = SessionTail(str(path)); tail.update()
            self.write_log(replacement, "new", 200, "longer" * 100)
            replacement.replace(path)
            tail.update()
            self.assertEqual(tail.measurements.output_total, 200)
            self.assertEqual(tail.measurements.last["rate"], 40)

    def test_log_rewritten_in_place_and_regrown_resets_partial_data(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "log.jsonl"
            self.write_log(path, "old", 100)
            tail = SessionTail(str(path)); tail.update()
            self.write_log(path, "new", 200, "longer" * 100)
            tail.update()
            self.assertEqual(tail.measurements.output_total, 200)

    def test_missing_log_is_reported_unavailable(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "log.jsonl"
            self.write_log(path, "old", 100)
            tail = SessionTail(str(path)); tail.update()
            path.unlink(); tail.update()
            self.assertEqual(tail.source()["state"], "unavailable")

    def test_forced_refresh_reopens_even_the_same_path(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "log.jsonl"
            self.write_log(path, "r", 100)
            cache = TailCache()
            row = {"id": "t", "rollout_path": str(path)}
            first = cache.update(row)
            refreshed = cache.update(row, force=True)
            self.assertIsNot(first, refreshed)
            self.assertEqual(refreshed.measurements.last["rate"], 20)

    def test_running_collector_follows_resumed_path_and_publishes_append_promptly(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            runtime = root / "runtime"; runtime.mkdir()
            old, new = root / "old.jsonl", root / "new.jsonl"
            self.write_log(old, "old", 100)
            self.write_log(new, "new", 300)
            database = root / "state_5.sqlite"
            with sqlite3.connect(database) as c:
                c.execute("CREATE TABLE threads (id TEXT, title TEXT, name TEXT, rollout_path TEXT, model TEXT, updated_at INT, archived INT)")
                c.execute("INSERT INTO threads VALUES (?,?,?,?,?,?,?)", ("t", "first message", "current", str(old), "m", 1, 0))
            c.close()
            (runtime / "selection.json").write_text(json.dumps({"title": "current"}), encoding="utf-8")
            process = subprocess.Popen([sys.executable, str(Path(__file__).resolve().parents[1] / "metrics.py"),
                "--codex-home", str(root), "--runtime", str(runtime)], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                creationflags=subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0)
            def wait_for(predicate, limit=5):
                start = time.monotonic()
                while time.monotonic() - start < limit:
                    try:
                        payload = json.loads((runtime / "metrics.json").read_text("utf-8"))
                        if predicate(payload):
                            return time.monotonic() - start
                    except (OSError, ValueError, TypeError, KeyError):
                        pass
                    time.sleep(0.01)
                self.fail("running collector did not publish expected stats")
            try:
                wait_for(lambda p: p["metrics"]["last"]["output_tokens"] == 100)
                with sqlite3.connect(database) as c:
                    c.execute("UPDATE threads SET rollout_path=?", (str(new),))
                c.close()
                changed = wait_for(lambda p: p["source"]["path"] == str(new) and p["metrics"]["last"]["output_tokens"] == 300)
                self.assertLess(changed, 1.2)
                with new.open("a", encoding="utf-8") as f:
                    f.write(json.dumps(event(10, "token_usage_record", {"response_id": "append", "usage": usage(200)})) + "\n")
                updated = wait_for(lambda p: p["metrics"]["last"]["output_tokens"] == 200)
                self.assertLess(updated, 0.7)
            finally:
                process.terminate()
                process.communicate(timeout=3)


class ActivityBindingIntegrationTest(unittest.TestCase):
    def collect(self, rows, mode="activity", selected_id=None, title=""):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            runtime = root / "runtime"
            runtime.mkdir()
            (runtime / "selection.json").write_text(json.dumps({
                "binding_mode": mode, "thread_id": selected_id, "title": title}), encoding="utf-8")
            with sqlite3.connect(root / "state_5.sqlite") as connection:
                connection.execute("CREATE TABLE threads (id TEXT, title TEXT, rollout_path TEXT, model TEXT, updated_at INT)")
                for thread_id, thread_title, model, seconds in rows:
                    log = root / (thread_id + ".jsonl")
                    # Real internal session metadata may omit model; the catalog
                    # obtains the confirmed model from its read-only database.
                    records = [event(0, "session_meta", {"id": thread_id}),
                               event(seconds, "event_msg", {"type": "task_started"})]
                    log.write_text("".join(json.dumps(row) + "\n" for row in records), encoding="utf-8")
                    connection.execute("INSERT INTO threads VALUES (?,?,?,?,?)",
                                       (thread_id, thread_title, str(log), model, seconds))
            connection.close()
            return monitor(root, runtime, once=True)

    def test_newer_permission_review_does_not_steal_latest_user_activity(self):
        result = self.collect([("user", "Build the dashboard", "gpt-6-astra", 10),
                               ("internal", "Guardian review", "codex-auto-review", 20)])
        self.assertEqual(result["thread_id"], "user")
        self.assertEqual(result["binding"], "ok")
        self.assertEqual(result["metrics"]["model"], "gpt-6-astra")
        self.assertEqual([row["id"] for row in result["matches"]], ["user"])
        self.assertIn("internal", [row["id"] for row in result["threads"]])

    def test_permission_review_at_same_time_does_not_make_user_activity_ambiguous(self):
        result = self.collect([("user", "Task", "gpt-6-astra", 20),
                               ("internal", "Guardian review", "codex-auto-review", 20)])
        self.assertEqual(result["thread_id"], "user")
        self.assertEqual(result["binding"], "ok")

    def test_internal_only_activity_waits_instead_of_falling_back_to_internal(self):
        result = self.collect([("internal", "Guardian review", "codex-auto-review", 20)],
                              selected_id="internal", title="Guardian review")
        self.assertEqual(result["binding"], "unbound")
        self.assertIsNone(result["thread_id"])
        self.assertIsNone(result["metrics"])
        self.assertEqual(result["matches"], [])
        self.assertEqual(result["title"], "")

    def test_normal_review_titles_unknown_and_similarly_named_models_remain_eligible(self):
        for model in ("gpt-6-astra", "gpt-6.1-sol", None, "codex-auto-review-experiment"):
            with self.subTest(model=model):
                result = self.collect([("older", "Task", "gpt-6-astra", 10),
                                       ("review", "Guardian review", model, 20)])
                self.assertEqual(result["thread_id"], "review")
                self.assertEqual(result["binding"], "ok")

    def test_explicit_manual_and_title_binding_can_still_select_internal_record(self):
        rows = [("user", "Task", "gpt-6-astra", 30),
                ("internal", "Guardian review", "codex-auto-review", 20)]
        for mode in ("manual", "auto"):
            with self.subTest(mode=mode):
                result = self.collect(rows, mode=mode, selected_id="internal", title="Guardian review")
                self.assertEqual(result["thread_id"], "internal")
                self.assertEqual(result["metrics"]["model"], "codex-auto-review")

    def test_equal_user_activity_still_requires_a_choice_after_internal_is_filtered(self):
        result = self.collect([("one", "Task one", "gpt-6-astra", 20),
                               ("two", "Task two", "gpt-6.1-sol", 20),
                               ("internal", "Guardian review", "codex-auto-review", 30)])
        self.assertEqual(result["binding"], "ambiguous")
        self.assertIsNone(result["thread_id"])
        self.assertEqual({row["id"] for row in result["matches"]}, {"one", "two"})


class NetworkCollectorIntegrationTest(unittest.TestCase):
    def test_once_mode_and_default_do_not_launch_network_probe(self):
        with tempfile.TemporaryDirectory() as temporary, patch("latency.LatencyPoller") as factory, \
                patch("reset_radar.ResetRadarPoller") as radar_factory:
            root = Path(temporary)
            self.assertIsNone(monitor(root, root / "runtime", once=True)["network_latency"])
            self.assertIsNone(monitor(root, root / "runtime", once=True, network_latency=True)["network_latency"])
            self.assertIsNone(monitor(root, root / "runtime", once=True, reset_radar=True)["reset_radar"])
            factory.assert_not_called()
            radar_factory.assert_not_called()

    def test_snapshot_refresh_and_shutdown_do_not_block_the_collector(self):
        class StopMonitor(Exception):
            pass
        with tempfile.TemporaryDirectory() as temporary, patch("latency.LatencyPoller") as factory, \
                patch("reset_radar.ResetRadarPoller") as radar_factory:
            root = Path(temporary)
            runtime = root / "runtime"
            runtime.mkdir()
            (runtime / "selection.json").write_text(json.dumps({"refresh_id": 123}), encoding="utf-8")
            snapshot = {"state": "ok", "updated_at_ms": 1700000000000, "nodes": []}
            poller = factory.return_value
            poller.snapshot.return_value = snapshot
            radar_snapshot = {"state": "unavailable", "upcoming": None, "latest_events": []}
            radar = radar_factory.return_value
            radar.snapshot.return_value = radar_snapshot
            with patch("metrics.time.sleep", side_effect=[None, StopMonitor]):
                with self.assertRaises(StopMonitor):
                    monitor(root, runtime, network_latency=True, reset_radar=True)
            factory.assert_called_once_with(interval=30)
            self.assertEqual(poller.snapshot.call_count, 2)
            poller.refresh.assert_called_once_with()
            poller.close.assert_called_once_with()
            radar_factory.assert_called_once_with(interval=180)
            self.assertEqual(radar.snapshot.call_count, 2)
            radar.refresh.assert_called_once_with()
            radar.close.assert_called_once_with()
            published = json.loads((runtime / "metrics.json").read_text(encoding="utf-8"))
            self.assertEqual(published["network_latency"], snapshot)
            self.assertEqual(published["reset_radar"], radar_snapshot)
            self.assertIsNotNone(published["updated_at_ms"])

    def test_network_cli_switch_is_forwarded(self):
        import metrics
        for switch, network, radar in (("--network-latency", True, False), ("--reset-radar", False, True)):
            with self.subTest(switch=switch), patch("metrics.monitor", return_value=None) as run, patch.object(
                    sys, "argv", ["metrics.py", "--once", switch]):
                metrics.main()
                self.assertEqual(run.call_args.args[6:], (network, radar))
                self.assertTrue(run.call_args.args[3])


if __name__ == "__main__":
    unittest.main()
