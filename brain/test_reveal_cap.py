"""Codex R7-1 (2026-11-06): the final reply cap (REPLY_MAX_CHARS = 300, added with the shop hint) must not cut a secret
reveal, which has its own budget (reveal_max_chars, default 480). The shard marks a secret revealed once the answer is
spoken (BaseCompanion.cs -> AiSecrets.MarkRevealed), so a cut reveal would be lost for good. Ordinary replies keep 300."""
import json
import os
import sys
import tempfile
import threading
import unittest
import urllib.request
from http.server import ThreadingHTTPServer
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import brain  # noqa: E402
import memory  # noqa: E402

OPENER = urllib.request.build_opener()
NELL = {"name": "Nell", "role": "provisioner", "town": "Britain", "trade": "vendor"}
SECRET = ("I have carried this story through every town we have visited, and each time I meant to tell it I found some "
          "reason to stay quiet instead. My brother did not drown in the river that spring as everyone in the village "
          "believes. He walked away. The last line holds the key: he lives in Yew under another name, and I keep his ring.")


class RevealCap(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        brain.LEDGER = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        brain.LEDGER.reconcile(1, 0, 0)
        self.logs = []
        self.srv = ThreadingHTTPServer(("127.0.0.1", 0), brain.Handler)
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()

    def tearDown(self):
        self.srv.shutdown()
        self.srv.server_close()
        brain.LEDGER.db.close()
        self.dir.cleanup()

    def ask(self, utterance, reply, reveal=False):
        class R:
            def __init__(s, b): s.b = b
            def __enter__(s): return s
            def __exit__(s, *a): return False
            def read(s, *a): return s.b

        def fake(req, timeout=None):
            return R(json.dumps({"message": {"content": json.dumps({"say": reply, "action": "none"})},
                                 "prompt_eval_count": 1, "eval_count": 1}).encode())
        body = {"request_id": "t", "npc_id": "33333333-3333-4333-8333-333333333333",
                "subject_id": "44444444-4444-4444-8444-444444444444",
                "priority": "Companion" if reveal else "Conversation",
                "persona_key": "elspeth" if reveal else "townsfolk", "persona_vars": None if reveal else NELL,
                "utterance": utterance, "budget_ms": 8000,
                "history": [{"speaker": "player", "text": "hi"}, {"speaker": "npc", "text": "Well met."}]}
        if reveal:
            body["reveal_secret_id"] = "Fake-Tier-1"
        with mock.patch.object(brain.urllib.request, "urlopen", fake), \
                mock.patch.object(brain, "log", lambda **k: self.logs.append(k)), \
                mock.patch.object(brain.SECRETS, "refresh", lambda: False), \
                mock.patch.object(brain.SECRETS, "body", lambda *a: "A synthetic test secret."):
            r = OPENER.open(urllib.request.Request(f"http://127.0.0.1:{self.srv.server_port}/v1/npc/respond",
                                                   data=json.dumps(body).encode(),
                                                   headers={"Content-Type": "application/json"}), timeout=10)
            return json.load(r)

    def test_long_reveal_kept_whole(self):
        self.assertTrue(brain.REPLY_MAX_CHARS < len(SECRET) <= brain.CFG["reveal_max_chars"], len(SECRET))
        out = self.ask("Please go on.", SECRET, reveal=True)
        self.assertTrue(out["ok"])
        self.assertEqual(out["say"], SECRET)
        self.assertIn("he lives in Yew", out["say"])

    def test_short_reveal_unchanged(self):
        out = self.ask("Please go on.", "I once left a friend waiting at the gate.", reveal=True)
        self.assertEqual(out["say"], "I once left a friend waiting at the gate.")

    def test_ordinary_reply_still_capped(self):
        long = ("Grain, salted meats, dried fruit and ale " * 8)[:235].rstrip() + "."
        out = self.ask("what do you sell?", long)
        self.assertLessEqual(len(out["say"]), brain.REPLY_MAX_CHARS)


if __name__ == "__main__":
    unittest.main()
