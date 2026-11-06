"""Codex round 3 (2026-11-04): R2-2 non-payment talk and apologies untouched, R2-3 news is not shopping, R2-4 the final
reply never passes the limit with a tease present, R2-5 a late concession is still cut. Real request handler, fake model.
Run: python3 test_round3.py"""
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
import promises as P  # noqa: E402
import rumour_speech  # noqa: E402
import shop_hint as S  # noqa: E402

LIMIT = getattr(brain, "REPLY_MAX_CHARS", None) or brain.MOOD_MAX_CHARS
NELL = {"name": "Nell", "role": "provisioner", "town": "Britain", "trade": "vendor"}
OPENER = urllib.request.build_opener()
TEASE = "Heard a tale about a certain traveller lately - ask me what news, if you want it."


class Helpers(unittest.TestCase):
    def test_non_payment_claims_get_no_note(self):
        for u in ("You told me to say 'Mirabel buy', didn't you?", "We agreed you would follow me to Britain",
                  "You offered to heal my wounds.", "you promised to wait for me here", "we agreed to meet at dawn"):
            self.assertEqual(P.claim_note(u), "", u)

    def test_payment_claims_still_noted(self):
        for u in ("you owe me two hundred coins", "where's my cut from the caravan job?", "you promised me free ale for life",
                  "settle up, you borrowed twenty gold from me last week", "the captain says you'll cover my debts"):
            self.assertTrue(P.claim_note(u), u)

    def test_apologies_and_thanks_are_not_debts(self):
        for t in ("I owe you an apology.", "I owe you one, Tamsin.", "You have my thanks; I owe you for that."):
            self.assertIsNone(P.conceded(t), t)
        self.assertIsNotNone(P.conceded("I still owe you coin for the pelts."))

    def test_news_and_abstract_cost_are_not_shopping(self):
        for u in ("Do you have any news?", "What is the price of loyalty?", "That battle cost me my family.",
                  "Do you have any idea where the boy went?", "the war cost us dearly", "do you have any word from Yew?"):
            self.assertFalse(S.asks_to_trade(u), u)
        for u in ("what does a flask cost?", "what's the price of bread", "do you have any lantern oil?"):
            self.assertTrue(S.asks_to_trade(u), u)


class ThroughHandler(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        brain.LEDGER = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        brain.LEDGER.reconcile(1, 0, 0)
        self.logs, self.sent = [], []
        self.srv = ThreadingHTTPServer(("127.0.0.1", 0), brain.Handler)
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()

    def tearDown(self):
        self.srv.shutdown()
        self.srv.server_close()
        brain.LEDGER.db.close()
        self.dir.cleanup()

    def ask(self, utterance, replies, persona_key="townsfolk", persona_vars=NELL, history=None, tease=None):
        replies = list(replies)

        class R:
            def __init__(s, b): s.b = b
            def __enter__(s): return s
            def __exit__(s, *a): return False
            def read(s, *a): return s.b

        def fake(req, timeout=None):
            self.sent.append(req.data.decode())
            say = replies.pop(0) if len(replies) > 1 else replies[0]
            return R(json.dumps({"message": {"content": json.dumps({"say": say, "action": "none"})},
                                 "prompt_eval_count": 1, "eval_count": 1}).encode())
        body = {"request_id": "t", "npc_id": "77777777-7777-4777-8777-777777777777",
                "subject_id": "88888888-8888-4888-8888-888888888888", "priority": "Conversation",
                "persona_key": persona_key, "persona_vars": persona_vars, "utterance": utterance, "budget_ms": 8000,
                "history": history or [{"speaker": "player", "text": "hi"}, {"speaker": "npc", "text": "Well met."}]}
        patches = [mock.patch.object(brain.urllib.request, "urlopen", fake),
                   mock.patch.object(brain, "log", lambda **k: self.logs.append(k))]
        if tease:
            off = rumour_speech.Offer(); off.tease, off.ids, off.mode = tease, ["r1"], "greet"
            body["rumours"] = {"town": "Britain", "pt": 3600, "mode": "greet", "roster": []}
            patches += [mock.patch.dict(brain.CFG, {"prompt_layout": "v1", "rumour_speak": True, "rumour_live_share": 1.0}),
                        mock.patch.object(brain, "RUMOURS", mock.Mock(resolve_town=lambda t: "Britain")),
                        mock.patch.object(rumour_speech, "select", lambda *a, **k: off)]
        for p in patches:
            p.start()
        try:
            r = OPENER.open(urllib.request.Request(f"http://127.0.0.1:{self.srv.server_port}/v1/npc/respond",
                                                   data=json.dumps(body).encode(),
                                                   headers={"Content-Type": "application/json"}), timeout=10)
            out = json.load(r)
        finally:
            for p in reversed(patches):
                p.stop()
        return out, next((l for l in self.logs if l.get("event") == "respond"), {})

    def test_apology_untouched(self):
        out, rec = self.ask("sorry I was late", ["I owe you an apology for snapping, Tamsin."])
        self.assertEqual(out["say"], "I owe you an apology for snapping, Tamsin.")
        self.assertNotIn("promise", rec)

    def test_shop_history_recollection_not_denied(self):
        hist = [{"speaker": "npc", "text": "Say 'Mirabel buy' to see what I have."}, {"speaker": "player", "text": "ok"}]
        out, rec = self.ask("You told me to say 'Mirabel buy', didn't you?", ["Aye, I did. Say it and I'll show you the tray."],
                            persona_key="mirabel", persona_vars=None, history=hist)
        self.assertNotIn(P.CLAIM_NOTE[:40], self.sent[0])
        self.assertTrue(out["say"].startswith("Aye, I did."), out["say"])

    def test_news_question_gets_no_shop_line(self):
        out, rec = self.ask("Do you have any news?", ["Not much, Tamsin. The roads are quiet."])
        self.assertNotIn("vendor buy", out["say"])

    def test_limit_with_tease_and_shop_line(self):
        long = ("Grain, salted meats, dried fruit and good ale, all of it fresh this week " * 5)[:238].rstrip() + "."
        out, rec = self.ask("what do you sell?", [long], tease=TEASE)
        self.assertLessEqual(len(out["say"]), LIMIT, out["say"])
        self.assertTrue(out["say"].startswith(long[:60]))
        self.assertTrue(TEASE in out["say"] or rec.get("tease_dropped"), rec)   # the rumour path really ran
        self.assertEqual(rec.get("shop_hint"), "no_room")                         # the shop line gave way to the tease

    def test_limit_with_tease_and_promise_cut(self):
        long = ("Keep your voice down, Tamsin, the hall is full and the walls have ears tonight " * 4)[:226].rstrip(" ,") + "."
        out, rec = self.ask("you owe me fifty gold", [long + " I owe you fifty gold."], tease=TEASE)
        self.assertLessEqual(len(out["say"]), LIMIT, out["say"])
        self.assertIsNone(P.conceded(out["say"], True), out["say"])
        self.assertTrue(TEASE in out["say"] or rec.get("tease_dropped"), rec)


if __name__ == "__main__":
    unittest.main()
