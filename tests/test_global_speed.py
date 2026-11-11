import tempfile
import json
import unittest
from pathlib import Path
from metrics import Measurements, GlobalCollector
from speed import GlobalSpeed
from test_speed import BASE, event, start, response


class GlobalSpeedTests(unittest.TestCase):
    def pair(self):
        a, b = Measurements('model-a'), Measurements('model-b')
        start(a, 0); start(b, 0)
        response(a, 3, 120, 'response-a'); response(b, 3, 180, 'response-b')
        return [('a', a), ('b', b)]

    def test_concurrent_output_sums_on_one_wall_clock(self):
        agents = self.pair(); frame = GlobalSpeed().compute(agents, BASE+3000)
        self.assertEqual(frame['rate'], 100)
        self.assertEqual(frame['confirmed_turn_output'], 300)
        self.assertEqual(frame['tools'], 2)
        self.assertEqual(frame['average_30s'], 10)
        self.assertEqual(frame['history'][-1]['measured'], 100)

    def test_selection_order_does_not_reset_history_or_count_twice(self):
        agents = self.pair(); engine = GlobalSpeed()
        a = engine.compute(agents, BASE+3000)
        b = engine.compute(list(reversed(agents)), BASE+3000)
        self.assertEqual(a, b)
        self.assertEqual(engine.compute(agents, BASE+3500)['confirmed_turn_output'], 300)

    def test_one_finished_task_does_not_clear_other_output(self):
        agents = self.pair()
        agents[0][1].feed(event(3, 'event_msg', {'type':'task_complete'}))
        agents[1][1].feed(event(3, 'response_item', {'type':'function_call_output','call_id':'x'}))
        frame = GlobalSpeed().compute(agents, BASE+6000)
        self.assertEqual(frame['rate'], 60)
        self.assertEqual(frame['generating'], 1)
        self.assertEqual(frame['estimated_share'], 1)

    def test_all_paused_decays_to_real_zero_not_retained_last_rates(self):
        engine = GlobalSpeed(); agents = self.pair()
        self.assertAlmostEqual(engine.compute(agents, BASE+4000)['rate'], 200/3)
        frame = engine.compute(agents, BASE+6000)
        self.assertEqual(frame['rate'], 0)
        self.assertEqual(frame['tools'], 2)
        for _, m in agents: m.feed(event(6, 'event_msg', {'type':'task_complete'}))
        self.assertEqual(engine.compute(agents, BASE+6500)['state'], 'idle')

    def test_copied_response_is_counted_once_across_files_and_replay(self):
        a, b = Measurements('m'), Measurements('m')
        for m in (a,b): start(m,0); response(m,3,300,'same-response')
        a.response_history = b
        frame = GlobalSpeed().compute([('a',a),('b',b)], BASE+3000)
        self.assertEqual(frame['rate'],100)
        self.assertEqual(frame['confirmed_turn_output'],300)

    def test_new_task_uses_same_model_reference_without_zeroing(self):
        agents = self.pair(); c=Measurements('model-a'); start(c,3)
        frame = GlobalSpeed().compute(agents+[('c',c)], BASE+6000)
        self.assertEqual(frame['rate'],40)
        self.assertEqual(frame['generating'],1)

    def test_real_completion_replaces_estimate(self):
        agents=self.pair(); b=agents[1][1]; engine=GlobalSpeed()
        b.feed(event(3,'response_item',{'type':'function_call_output','call_id':'x'}))
        self.assertEqual(engine.compute(agents,BASE+6000)['estimated_rate'],60)
        response(b,6,300,'new-response')
        frame=engine.compute(agents,BASE+6000)
        self.assertEqual(frame['rate'],100)
        self.assertEqual(frame['estimated_share'],0)
        self.assertEqual(frame['confirmed_turn_output'],600)

    def test_unknown_baseline_and_stale_activity_are_distinct(self):
        m=Measurements('new'); start(m,0); engine=GlobalSpeed()
        self.assertIsNone(engine.compute([('a',m)],BASE+3000)['rate'])
        frame=engine.compute([('a',m)],BASE+301000)
        self.assertEqual(frame['rate'],0)
        self.assertEqual(frame['generating'],0)

    def test_paginated_subagent_inheritance_cannot_contribute_usage_or_phase(self):
        m=Measurements('m')
        m.feed(event(0,'session_meta',{'id':'child','subagent_history_start_ordinal':10}))
        m.feed(dict(event(1,'event_msg',{'type':'task_started'}),ordinal=1))
        m.feed(dict(event(2,'token_usage_record',{'response_id':'copied','usage':{'output_tokens':900}}),ordinal=2))
        self.assertEqual(m.output_total,0)
        self.assertIsNone(m.turn_active)
        m.feed(dict(event(3,'event_msg',{'type':'task_started'}),ordinal=10))
        self.assertTrue(m.turn_active)

    def test_samples_expire_and_tool_does_not_use_historical_speed(self):
        engine=GlobalSpeed(); agents=self.pair()
        engine.compute(agents,BASE+3000)
        frame=engine.compute([],BASE+1804000)
        self.assertEqual(frame['rate'],0)
        self.assertEqual(len(engine.samples),0)

    def test_collector_discovers_all_hot_tasks_including_subagents(self):
        with tempfile.TemporaryDirectory() as tmp:
            rows=[]
            for i in range(3):
                path=Path(tmp)/f'{i}.jsonl'
                events=[event(0,'session_meta',{'id':str(i)}),event(0,'event_msg',{'type':'task_started'}),
                        event(3,'token_usage_record',{'response_id':f'r{i}','usage':{'output_tokens':120}})]
                path.write_text('\n'.join(map(json.dumps,events))+'\n',encoding='utf-8')
                rows.append(dict(id=str(i),rollout_path=str(path),updated_at_ms=BASE+3000,is_subagent=i==2))
            collector=GlobalCollector()
            self.assertFalse(collector.snapshot(rows,BASE+3000)['complete'])
            frame=collector.snapshot(list(reversed(rows)),BASE+3000)
            self.assertTrue(frame['complete'])
            self.assertEqual(frame['rate'],120)
            self.assertEqual(frame['tracked'],3)
            limited=GlobalCollector(maximum=2).snapshot(rows,BASE+3000)
            self.assertFalse(limited['complete'])
            self.assertEqual(limited['tracked'],2)

    def test_disappeared_file_does_not_clear_healthy_peer(self):
        agents=self.pair(); engine=GlobalSpeed()
        engine.compute(agents,BASE+3000)
        frame=engine.compute([agents[1]],BASE+3500,complete=False)
        self.assertFalse(frame['complete'])
        self.assertGreater(frame['rate'],0)


if __name__ == '__main__': unittest.main()
