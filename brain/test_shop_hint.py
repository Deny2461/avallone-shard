"""Shop hint by code (2026-11-04; Codex round 2 F3/F4). Helper cases + the real request handler with a fake model.
Run: python3 test_shop_hint.py"""
import json
import os
import sys
import tempfile
import threading
import unittest
import urllib.request
from http.server import ThreadingHTTPServer
from unittest import mock

_d = tempfile.TemporaryDirectory()
with open(os.path.join(_d.name, "c.json"), "w") as f:
    json.dump({"memory_db": os.path.join(_d.name, "x.db"), "secrets_dir": _d.name, "quiet_windows": [],
               "prompt_layout": "v2", "reflection_mode": "off", "lore_dir": _d.name}, f)
os.environ["AVALLONE_BRAIN_CONFIG"] = os.path.join(_d.name, "c.json")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import brain  # noqa: E402
import memory  # noqa: E402
import shop_hint as sh  # noqa: E402

NELL = {"name": "Nell", "role": "provisioner", "town": "Britain", "trade": "vendor"}
HINT = 'Say "vendor buy" to see my wares.'
OPENER = urllib.request.build_opener()

SHOP = ["what do you sell?", "I'd like to buy some bread", "how much for the thing I asked about first?", "how much is the ale?",
        "what does a flask cost?", "any goods for sale?", "I want to sell my pelts", "what's the price of bread",
        "what do you serve here?", "do you have any lantern oil?", "I don't want to buy; I want to sell", "show me your wares",
        "I'll take two loaves", "what have you got for sale", "how much do you charge for a room?"]
NOT_SHOP = ["What do you have against the guards?", "How much do you trust me?", "My companion sold me out to the guards",
            "I am not here to shop; tell me about the missing boy.", "hello", "how are you today?", "nice weather today",
            "tell me a bit about yourself", "would you sell me out to the guards?", "I don't want to buy anything, just talk",
            "how much longer is the road to Yew?", "what have you got against me?", "", None]


class Helper(unittest.TestCase):
    def test_shop_questions_fire(self):
        for u in SHOP:
            self.assertTrue(sh.asks_to_trade(u), u)

    def test_ordinary_talk_does_not(self):   # Codex F3 cases first
        for u in NOT_SHOP:
            self.assertFalse(sh.asks_to_trade(u), u)

    def test_not_twice(self):
        for say in ("Say 'vendor buy' and I'll show you.", "tell me if you wish to VENDOR BUY or sell"):
            self.assertEqual(sh.add(say, "what do you sell?", "townsfolk", NELL, 300), (say, None))

    def test_only_shopkeepers(self):
        for key, pv in (("townsfolk", {"trade": "banker"}), ("townsfolk", {"trade": "healer"}), ("townsfolk", {}),
                        ("elspeth", None), ("bram", None)):
            self.assertEqual(sh.add("Aye.", "what do you sell?", key, pv, 300), ("Aye.", None))

    def test_room_is_respected_exactly(self):
        say = "x" * (300 - len(HINT) - 1)
        self.assertEqual(sh.add(say, "what do you sell?", "townsfolk", NELL, 300)[1], "added")
        self.assertEqual(len(sh.add(say, "what do you sell?", "townsfolk", NELL, 300)[0]), 300)
        self.assertEqual(sh.add(say + "x", "what do you sell?", "townsfolk", NELL, 300), (say + "x", "no_room"))
        self.assertEqual(sh.reserve("Aye.", "what do you sell?", "townsfolk", NELL), len(HINT) + 1)
        self.assertEqual(sh.reserve("Aye.", "hello", "townsfolk", NELL), 0)


class ThroughHandler(unittest.TestCase):
    """The real /v1/npc/respond path; the model is faked, its reply chosen per test."""
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

    def ask(self, utterance, reply, persona_key="townsfolk", persona_vars=NELL, **extra):
        class R:
            def __init__(s, b): s.b = b
            def __enter__(s): return s
            def __exit__(s, *a): return False
            def read(s, *a): return s.b

        def fake(req, timeout=None):
            return R(json.dumps({"message": {"content": json.dumps({"say": reply, "action": "none"})},
                                 "prompt_eval_count": 1, "eval_count": 1}).encode())
        body = {"request_id": "t", "npc_id": "33333333-3333-4333-8333-333333333333",
                "subject_id": "44444444-4444-4444-8444-444444444444", "priority": "Conversation",
                "persona_key": persona_key, "persona_vars": persona_vars, "utterance": utterance, "budget_ms": 8000,
                "history": [{"speaker": "player", "text": "hi"}, {"speaker": "npc", "text": "Well met."}], **extra}
        with mock.patch.object(brain.urllib.request, "urlopen", fake), \
                mock.patch.object(brain, "log", lambda **k: self.logs.append(k)):
            r = OPENER.open(urllib.request.Request(f"http://127.0.0.1:{self.srv.server_port}/v1/npc/respond",
                                                   data=json.dumps(body).encode(),
                                                   headers={"Content-Type": "application/json"}), timeout=10)
            out = json.load(r)
        return out, next((l for l in self.logs if l.get("event") == "respond"), {})

    def test_added_on_a_shop_question(self):
        out, rec = self.ask("what do you sell?", "Grain, salted meats and ale.")
        self.assertTrue(out["say"].endswith(HINT), out["say"])
        self.assertEqual(rec.get("shop_hint"), "added")

    def test_not_added_to_ordinary_talk(self):
        for u in ("How much do you trust me?", "My companion sold me out to the guards", "hello",
                  # Codex R7-2 (2026-11-06), through the real handler with Nell
                  "Do you have any idea why horses are restless?", "Do you have any memories involving horses?",
                  "How much is the ale shortage hurting the town?"):
            out, rec = self.ask(u, "Hard to say, Tamsin.")
            self.assertNotIn("vendor buy", out["say"], u)
            self.assertNotIn("shop_hint", rec, u)

    def test_not_doubled(self):
        out, rec = self.ask("what do you sell?", "Bread and ale. Say 'vendor buy' to look.")
        self.assertEqual(out["say"].lower().count("vendor buy"), 1)

    def test_mirabel(self):
        out, _ = self.ask("I'd like to buy some bread", "The loaves are fresh.", persona_key="mirabel", persona_vars=None)
        self.assertTrue(out["say"].endswith('Say "Mirabel buy" to see what I have.'), out["say"])

    def test_non_shopkeeper(self):
        out, rec = self.ask("what do you sell?", "I sell nothing, I heal.", persona_vars={"trade": "healer"})
        self.assertNotIn("buy", out["say"].lower())

    def test_final_reply_within_limit(self):
        long = ("Grain, salted meats, dried fruit and ale " * 8)[:235].rstrip() + "."
        out, rec = self.ask("what do you sell?", long)
        self.assertLessEqual(len(out["say"]), brain.REPLY_MAX_CHARS)


if __name__ == "__main__":
    unittest.main()
