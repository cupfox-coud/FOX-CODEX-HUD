import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from metrics import VisibleConversationResolver


class VisibleIdentityTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(); self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.visible = 'This is the visible conversation selected by the user. Its unique context should follow this actual window, not recent background output.'
        self.other = 'An archived conversation has the same title, but different actual content and a different context. It cannot be chosen by how recently a file changed.'
        self.rows = []
        for name, value in (('a', self.visible), ('b', self.other)):
            p = self.root/(name+'.jsonl')
            p.write_text(json.dumps({'type':'event_msg','payload':{'type':'agent_message','message':value}})+'\n', encoding='utf-8')
            self.rows.append({'id':name, 'title':'Same title', 'rollout_path':str(p)})

    def hashes(self, text):
        n = VisibleConversationResolver.normalized(text)
        return [hashlib.sha256(n[:32].encode()).hexdigest(), hashlib.sha256(n[-32:].encode()).hexdigest()]

    def test_short_visible_paragraph_can_identify_a_chat(self):
        text = 'This short paragraph identifies the viewed chat.'
        Path(self.rows[0]['rollout_path']).write_text(json.dumps({'type':'event_msg','payload':{'type':'agent_message','message':text}})+'\n', encoding='utf-8')
        self.assertEqual(VisibleConversationResolver().choose(self.rows, self.hashes(text))['id'], 'a')

    def test_visible_content_resolves_duplicates_and_tracks_switches(self):
        r = VisibleConversationResolver()
        self.assertEqual(r.choose(self.rows, self.hashes(self.visible))['id'], 'a')
        self.assertEqual(r.choose(self.rows, self.hashes(self.other))['id'], 'b')

    def test_shared_history_never_becomes_false_identity(self):
        Path(self.rows[1]['rollout_path']).write_bytes(Path(self.rows[0]['rollout_path']).read_bytes())
        self.assertIsNone(VisibleConversationResolver().choose(self.rows, self.hashes(self.visible)))

    def test_mixed_visible_chats_and_unavailable_evidence_are_ambiguous(self):
        for hashes in (None, [], ['x'*64], self.hashes(self.visible)+self.hashes(self.other)):
            self.assertIsNone(VisibleConversationResolver().choose(self.rows, hashes))

    def test_wrong_role_and_private_reasoning_are_not_visible_evidence(self):
        for role, channel in (('tool', None), ('assistant', 'analysis')):
            Path(self.rows[0]['rollout_path']).write_text(json.dumps({'type':'response_item','payload':{'role':role,'channel':channel,'content':[{'text':self.visible}]}})+'\n', encoding='utf-8')
            self.assertIsNone(VisibleConversationResolver().choose(self.rows, self.hashes(self.visible)))

    def test_cache_contains_only_identifiers_and_fingerprints(self):
        r = VisibleConversationResolver(); r.choose(self.rows, self.hashes(self.visible))
        self.assertNotIn(self.visible, str(r.__dict__))
        self.assertNotIn(self.rows[0]['rollout_path'], str(r.__dict__))
