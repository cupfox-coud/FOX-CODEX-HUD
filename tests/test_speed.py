import datetime as dt
import math
import json
import tempfile
import unittest
from pathlib import Path

from metrics import Measurements, SessionTail
from speed import interval

BASE = 1700000000000

def event(second, kind, payload):
    return {'timestamp':dt.datetime.fromtimestamp((BASE + second * 1000) / 1000, dt.timezone.utc).isoformat(),
            'type':kind, 'payload':payload}

def start(m, second):
    m.feed(event(second, 'event_msg', {'type':'task_started', 'turn_id':str(second)}))

def response(m, end, output, key='a', cached=500, inputs=1000):
    m.feed(event(end, 'token_usage_record', {'response_id':key, 'usage':{
        'output_tokens':output, 'input_tokens':inputs, 'cached_input_tokens':cached}}))

def frame(m, second):
    return m.snapshot(BASE + second * 1000)['live_speed']

class LiveSpeedTests(unittest.TestCase):
    def test_request_timing_includes_first_output_wait(self):
        m=Measurements('m');start(m,0)
        m.feed(event(8,'response_item',{'type':'reasoning'}))
        response(m,10,200)
        self.assertEqual(m.last['rate'],20)
        self.assertEqual(m.last['duration_ms'],10000)
        self.assertEqual(m.last['stream_duration_ms'],2000)

    def test_minimum_duration_avoids_single_tick_spikes(self):
        m=Measurements('m');start(m,0);response(m,.01,100)
        self.assertEqual(m.last['rate'],200)
        self.assertEqual(m.last['duration_ms'],500)

    def test_invalid_timing_preserves_counts_without_speed(self):
        for seconds in (-1,901):
            m=Measurements('m');start(m,0);response(m,seconds,100)
            self.assertIsNone(m.last['rate'])
            self.assertEqual(m.output_total,100)
        for a,b in ((None,1),(True,1),(math.nan,1),(0,math.inf)):
            self.assertIsNone(interval(a,b))

    def test_completed_request_is_distributed_over_window_and_decays(self):
        m=Measurements('m');start(m,0);response(m,10,200)
        self.assertEqual(frame(m,10)['rate'],20)
        self.assertAlmostEqual(frame(m,11)['rate'],40/3)
        self.assertEqual(frame(m,13)['rate'],0)

    def test_recent_baseline_is_weighted_by_duration(self):
        m=Measurements('m');start(m,0);response(m,10,100,'a')
        m.feed(event(20,'response_item',{'type':'function_call_output','call_id':'tool'}))
        response(m,21,100,'b')
        self.assertAlmostEqual(frame(m,21)['recent_rate'],200/11)

    def test_display_average_uses_up_to_ten_real_responses_and_limits_spikes(self):
        m=Measurements('m');start(m,0)
        for i in range(1,10):
            response(m,i*10,300,str(i))
        before=frame(m,90)
        response(m,91,400,'spike')
        after=frame(m,91)
        self.assertEqual(before['steady_rate'],30)
        self.assertAlmostEqual(after['steady_rate'],3100/91)
        self.assertAlmostEqual(after['recent_rate'],1000/21)
        self.assertEqual(after['steady_reference_count'],10)
        self.assertEqual(after['confirmed_turn_output'],3100)

    def test_inflight_is_labeled_estimate_and_does_not_inflate_token_totals(self):
        m=Measurements('m');start(m,0);response(m,10,200)
        m.feed(event(20,'response_item',{'type':'function_call_output','call_id':'tool'}))
        view=frame(m,23)
        self.assertEqual(view['state'],'estimating')
        self.assertEqual(view['rate'],20)
        self.assertEqual(view['estimated_share'],1)
        self.assertEqual(view['confirmed_turn_output'],200)
        self.assertEqual(m.output_total,200)

    def test_completion_replaces_its_estimate_without_double_counting(self):
        m=Measurements('m');start(m,0);response(m,10,200)
        m.feed(event(20,'response_item',{'type':'function_call_output','call_id':'tool'}))
        self.assertEqual(frame(m,23)['rate'],20)
        response(m,24,200,'b')
        view=frame(m,24)
        self.assertEqual(view['estimated_share'],0)
        self.assertEqual(view['rate'],50)
        self.assertEqual(m.output_total,400)

    def test_no_baseline_means_no_fabricated_number(self):
        m=Measurements('m');start(m,0)
        self.assertIsNone(frame(m,3)['rate'])
        self.assertEqual(frame(m,3)['state'],'warming')

    def test_tool_execution_never_gets_an_estimate(self):
        m=Measurements('m');start(m,0);response(m,10,200)
        m.feed(event(20,'response_item',{'type':'function_call','call_id':'tool'}))
        self.assertEqual(frame(m,23)['rate'],0)
        self.assertEqual(frame(m,23)['state'],'tools')

    def test_model_change_and_old_reference_do_not_reuse_wrong_baseline(self):
        m=Measurements('old');start(m,0);response(m,10,200)
        m.feed(event(20,'turn_context',{'model':'new'}))
        m.feed(event(21,'response_item',{'type':'function_call_output','call_id':'tool'}))
        self.assertIsNone(frame(m,24)['rate'])
        m.feed(event(1900,'turn_context',{'model':'old'}))
        m.feed(event(1901,'response_item',{'type':'function_call_output','call_id':'tool'}))
        self.assertIsNone(frame(m,1904)['recent_rate'])
        self.assertIsNone(frame(m,1904)['steady_rate'])

    def test_stale_lifecycle_cannot_drive_continuous_motion(self):
        m=Measurements('m');start(m,0);response(m,10,200)
        m.feed(event(20,'response_item',{'type':'function_call_output','call_id':'tool'}))
        self.assertIsNone(frame(m,321)['rate'])
        self.assertEqual(frame(m,321)['state'],'stale')

    def test_cache_is_weighted_by_input_not_average_of_percentages(self):
        m=Measurements('m');start(m,0);response(m,1,100,'a',cached=900,inputs=1000)
        response(m,2,100,'b',cached=0,inputs=100)
        self.assertAlmostEqual(frame(m,2)['cache_percent'],90000/1100)

    def test_duplicate_records_and_copied_fork_history_do_not_make_peaks(self):
        m=Measurements('m')
        m.feed(event(100,'session_meta',{'id':'new'}))
        start(m,0);response(m,10,10000)
        self.assertEqual(m.output_total,0)
        start(m,100);response(m,110,200,'new')
        response(m,111,200,'new')
        self.assertEqual(m.output_total,200)
        self.assertEqual(frame(m,111)['peak_rate'],20)

    def test_histogram_contains_only_measured_and_qualified_estimated_buckets(self):
        m=Measurements('m');start(m,0);response(m,10,200)
        view=frame(m,10)
        self.assertEqual(len(view['history']),60)
        self.assertAlmostEqual(sum(p['measured'] for p in view['history']),200)
        self.assertTrue(all(p['estimated']==0 for p in view['history']))

    def test_cold_tail_replay_does_not_restore_copied_parent_speed(self):
        records = [event(100, 'session_meta', {'id':'fork', 'agent_path':'/root/child'}),
                   event(0, 'event_msg', {'type':'task_started'}),
                   event(10, 'token_usage_record', {'response_id':'parent', 'usage':{'output_tokens':10000}}),
                   event(99, 'response_item', {'type':'message', 'role':'user', 'content':'x'*2000}),
                   event(100, 'event_msg', {'type':'task_started'}),
                   event(105, 'token_usage_record', {'response_id':'child', 'usage':{'output_tokens':100}})]
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'fork.jsonl'
            path.write_text('\n'.join(json.dumps(r) for r in records)+'\n', encoding='utf-8')
            tail = SessionTail(str(path), 'm', history_bytes=400)
            tail.update()
            view = frame(tail.measurements, 106)
            self.assertEqual(view['peak_rate'], 20)
            self.assertEqual(view['reference_count'], 1)
            self.assertEqual(tail.measurements.agent_path, '/root/child')

    def test_historical_model_context_is_kept_separate_in_cold_replay(self):
        records = [event(0, 'turn_context', {'model':'other'}),
                   event(1, 'event_msg', {'type':'task_started'}),
                   event(10, 'token_usage_record', {'response_id':'other', 'usage':{'output_tokens':9000}}),
                   event(11, 'turn_context', {'model':'current'}),
                   event(12, 'response_item', {'type':'function_call_output'}),
                   event(22, 'token_usage_record', {'response_id':'current', 'usage':{'output_tokens':100}}),
                   event(23, 'response_item', {'type':'function_call_output', 'output':'x'*2000}),
                   event(24, 'response_item', {'type':'reasoning'})]
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'models.jsonl'
            path.write_text('\n'.join(json.dumps(r) for r in records)+'\n', encoding='utf-8')
            tail = SessionTail(str(path), 'current', history_bytes=200)
            tail.update()
            self.assertEqual(frame(tail.measurements, 25)['recent_rate'], 10)
            self.assertEqual(frame(tail.measurements, 25)['reference_count'], 1)

    def test_idle_is_zero_while_recent_actual_remains_available(self):
        m=Measurements('m');start(m,0);response(m,10,200)
        m.feed(event(11,'event_msg',{'type':'task_complete'}))
        view=frame(m,20)
        self.assertEqual(view['rate'],0)
        self.assertEqual(view['recent_rate'],20)
        self.assertEqual(view['state'],'idle')

    def test_steady_generation_has_no_partial_second_tip_dips(self):
        m=Measurements('m');start(m,0);response(m,10,200)
        m.feed(event(20,'response_item',{'type':'function_call_output','call_id':'tool'}))
        tips=[]
        for second in (24.001,24.5,25.0,25.001,25.5,26.0):
            view=frame(m,second)
            tips.append(view['history'][-1]['measured']+view['history'][-1]['estimated'])
            self.assertAlmostEqual(view['rate'],20)
        for tip in tips:
            self.assertAlmostEqual(tip,20)

    def test_exact_second_does_not_append_an_unobserved_zero_bucket(self):
        m=Measurements('m');start(m,0);response(m,10,200)
        m.feed(event(20,'response_item',{'type':'function_call_output'}))
        history=frame(m,25)['history']
        self.assertEqual(len(history),60)
        self.assertEqual(history[-1]['at_ms'],BASE+24000)

    def test_partial_measured_bucket_is_a_rate_without_inflating_totals(self):
        m=Measurements('m');start(m,0);response(m,10.25,205)
        view=frame(m,10.25)
        self.assertAlmostEqual(view['history'][-1]['measured'],20)
        self.assertEqual(view['confirmed_turn_output'],205)
        self.assertEqual(m.output_total,205)

    def test_real_tool_stop_still_draws_zero(self):
        m=Measurements('m');start(m,0);response(m,10,200)
        m.feed(event(20,'response_item',{'type':'function_call_output'}))
        m.feed(event(24.5,'response_item',{'type':'function_call','call_id':'next-tool'}))
        view=frame(m,25.25)
        self.assertEqual(view['rate'],0)
        self.assertEqual(view['history'][-1]['estimated'],0)

if __name__=='__main__':unittest.main()
