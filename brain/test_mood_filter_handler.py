"""Step B r1 (Codex finding 3, 2026-11-08): the mood filter through the REAL /v1/npc/respond handler, with mood on (a
reconciled ledger and a standing block) and a stand-in model that gives the same answer on every attempt. Healer answers,
denials and "poorly made" must come back whole with ONE model call; an NPC saying it is ill now, or an ache given as the
reason it can't serve, is still retried and cut (positive controls). Ollama is never called. Run: python3 test_mood_filter_handler.py"""
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
    json.dump({"memory_db": os.path.join(_d.name, "x.db"), "lore_dir": _d.name, "secrets_dir": _d.name,
               "quiet_windows": [], "reflection_mode": "off"}, f)
os.environ["AVALLONE_BRAIN_CONFIG"] = os.path.join(_d.name, "c.json")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import brain  # noqa: E402
import memory  # noqa: E402
import mood  # noqa: E402
import variety  # noqa: E402

NPC, CHAR = "33333333-3333-4333-8333-333333333333", "44444444-4444-4444-8444-444444444444"
KEEP = [("Can you heal me?", "If you are sick, I can heal you."),
        ("Are you ill?", "No, I am not ill."),
        ("Can you repair this?", "This sword is poorly made. I can repair it."),
        ("how are you?", "This knee of mine is acting a bit stiff today, but the herbs are drying well."),
        # Codex Step B r2 finding 1: reassuring patient advice and a remedy offer
        ("Can you heal me?", "If you are ill, I won't turn you away."),
        ("do you have anything for a fever?", "I have a fever remedy for you.")]
# (player line, model answer, the exact final answer) - Codex Step B r2 finding 2: assert what comes back, not just "changed"
CUT = [("can you sew this?", "I'm feeling poorly today. Come back tomorrow.", brain.MOOD_FALLBACK),
       ("can you sew this?", "My hands ache too much to sew today.", brain.MOOD_FALLBACK),
       ("how much is the blue cloth?", "I'm feeling poorly today. The blue cloth is three gold.", "The blue cloth is three gold.")]


class MoodFilterThroughTheHandler(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        brain.LEDGER = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        brain.LEDGER.reconcile(1, 0, 0)
        variety.reset_for_tests()
        self.srv = ThreadingHTTPServer(("127.0.0.1", 0), brain.Handler)
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()

    def tearDown(self):
        self.srv.shutdown()
        brain.LEDGER.db.close()
        self.dir.cleanup()

    def ask(self, utterance, answer):
        calls, logs = [], []

        def fake(*a, **k):
            calls.append(k.get("nudge"))
            return answer, "none"
        body = {"request_id": "mf", "npc_id": NPC, "subject_id": CHAR, "persona_key": "townsfolk",
                "persona_vars": {"name": "Wren", "role": "healer", "town": "Britain", "trade": "healer"},
                "utterance": utterance, "budget_ms": 8000,
                "standing": {"epoch": 1, "pt": 3600, "mpt": 3600, "hour": 12, "karma": 0, "fame": 0, "town": "Britain"}}
        with mock.patch.object(brain, "ollama_chat", fake), mock.patch.object(brain, "log", lambda **k: logs.append(k)):
            req = urllib.request.Request(f"http://127.0.0.1:{self.srv.server_port}/v1/npc/respond",
                                         json.dumps(body).encode(), {"Content-Type": "application/json"})
            r = json.loads(urllib.request.urlopen(req, timeout=20).read())
        resp = next(l for l in logs if l.get("event") == "respond")
        return r, calls, resp

    def test_mood_is_on_in_this_rig(self):   # else the tests below would pass for the wrong reason
        _, _, resp = self.ask("hello", "Good day to you.")
        self.assertIsNotNone(resp.get("mood"))

    def test_healer_answers_denials_and_flavour_come_back_whole(self):
        for utt, ans in KEEP:
            variety.reset_for_tests()
            r, calls, resp = self.ask(utt, ans)
            self.assertEqual(mood.LEAD_ACTION.sub("", r["say"]), ans, utt)   # a mood gesture may lead ("[nods] ")
            self.assertEqual(len(calls), 1, utt)                       # no second model call
            self.assertIsNone((resp.get("mood") or {}).get("excuse"), utt)

    def test_ill_now_or_ache_as_excuse_is_still_caught(self):   # positive controls, same rig
        for utt, ans, final in CUT:
            variety.reset_for_tests()
            r, calls, resp = self.ask(utt, ans)
            self.assertEqual(len(calls), 2, utt)                        # one retry
            self.assertEqual(mood.LEAD_ACTION.sub("", r["say"]), final, ans)   # no refusal left behind
            self.assertEqual((resp.get("mood") or {}).get("excuse_kind"), "illness", utt)


if __name__ == "__main__":
    unittest.main(verbosity=1)
