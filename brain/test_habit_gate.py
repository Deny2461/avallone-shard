"""Rumour step C4 (2026-11-11): the reply handler really uses the habit gate. A flourish habit's sentence ("you mention
how many years...") is in the prompt the MODEL is sent only on some turns, never two running, and a retry inside one
turn is sent the same prompt. Ollama is never called. Run: python3 test_habit_gate.py"""
import json
import os
import sys
import tempfile
import threading
import unittest
import urllib.request
import uuid
from http.server import ThreadingHTTPServer
from unittest import mock

_d = tempfile.TemporaryDirectory()
with open(os.path.join(_d.name, "c.json"), "w") as f:
    json.dump({"memory_db": os.path.join(_d.name, "x.db"), "lore_dir": _d.name, "secrets_dir": _d.name,
               "quiet_windows": [], "reflection_mode": "off"}, f)
os.environ["AVALLONE_BRAIN_CONFIG"] = os.path.join(_d.name, "c.json")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import brain  # noqa: E402
import nature  # noqa: E402

IDS = [str(uuid.UUID(int=k)) for k in range(1, 4001)]
CHAR = "22222222-2222-4222-8222-222222222222"
YEARS = "how many years you have lived or worked here"


def npc_with(habit):
    return next(i for i in IDS if nature.of(i)["habit"] == habit)


class HabitGate(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.srv = ThreadingHTTPServer(("127.0.0.1", 0), brain.Handler)
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()

    def setUp(self):
        nature._turns.clear()

    def turn(self, npc, rid, fake):
        body = {"request_id": rid, "npc_id": npc, "subject_id": CHAR, "persona_key": "townsfolk",
                "persona_vars": {"name": "Osric", "role": "tanner", "town": "Britain", "trade": "vendor"},
                "utterance": "hello there", "budget_ms": 8000}
        req = urllib.request.Request(f"http://127.0.0.1:{self.srv.server_address[1]}/v1/npc/respond",
                                     json.dumps(body).encode(), {"Content-Type": "application/json"})
        with mock.patch.object(brain, "ollama_chat", fake):
            return json.loads(urllib.request.urlopen(req, timeout=20).read())

    def prompts(self, npc, turns):
        seen = []
        for t in range(turns):
            calls = []

            def fake(persona, *a, **k):
                calls.append(persona)
                return "Well met, friend.", "none"
            self.assertEqual(self.turn(npc, f"turn-{t}", fake).get("say"), "Well met, friend.")
            # the same line every turn also trips the brain own "say it differently" retry: every call of one turn
            # must have been sent the same prompt
            self.assertEqual(len({(YEARS in p, "A habit of speech" in p) for p in calls}), 1, t)
            seen.append(calls[0])
        return seen

    def test_the_years_sentence_is_sent_on_some_turns_and_never_two_running(self):
        seen = self.prompts(npc_with("years"), 40)
        has = [YEARS in p for p in seen]
        self.assertEqual(len(has), 40)
        self.assertTrue(any(has))
        self.assertLess(sum(has), 20)                                        # before this step: all 40
        self.assertFalse(any(a and b for a, b in zip(has, has[1:])))
        self.assertTrue(all("Your nature: you are" in p for p in seen))      # the rest of the nature is always there

    def test_a_manner_is_sent_every_turn(self):
        seen = self.prompts(npc_with("slow"), 12)
        self.assertTrue(all("You speak slowly" in p for p in seen))

    def test_a_retry_inside_one_turn_is_sent_the_same_prompt(self):
        npc = npc_with("years")
        for t in range(30):
            seen = []

            def fake(persona, *a, **k):
                seen.append(persona)
                if len(seen) == 1:
                    raise brain.GarbledReply("JSONDecodeError: Unterminated string")
                return "Well met, friend.", "none"
            self.turn(npc, f"turn-{t}", fake)
            self.assertGreaterEqual(len(seen), 2)
            self.assertEqual(len({YEARS in p for p in seen}), 1, t)

    def test_gate_off_sends_it_every_turn(self):
        c = nature.cfg()
        try:
            c["flourishes"]["enabled"] = False
            self.assertTrue(all(YEARS in p for p in self.prompts(npc_with("years"), 10)))
        finally:
            c["flourishes"]["enabled"] = True

    def test_a_hand_written_persona_gets_no_nature_and_no_gate(self):   # Mirabel, Bram, Elspeth: untouched
        seen = []

        def fake(persona, *a, **k):
            seen.append(persona)
            return "Well met.", "none"
        body = {"request_id": "m1", "npc_id": IDS[0], "subject_id": CHAR, "persona": "You are Nell, a baker.",
                "utterance": "hello", "budget_ms": 8000}
        req = urllib.request.Request(f"http://127.0.0.1:{self.srv.server_address[1]}/v1/npc/respond",
                                     json.dumps(body).encode(), {"Content-Type": "application/json"})
        with mock.patch.object(brain, "ollama_chat", fake):
            json.loads(urllib.request.urlopen(req, timeout=20).read())
        self.assertNotIn("A habit of speech", seen[0])
        self.assertEqual(dict(nature._turns), {})


if __name__ == "__main__":
    unittest.main()
