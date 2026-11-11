"""the owner's step 2 play test (2026-11-10): one weather per day for every NPC (weather.py), and a varied aside to the companion
(party.ANGLES) instead of "looks as though she has walked many a mile" every time. Both are switched: off = the prompt
is unchanged, byte for byte. Run: python3 -m unittest test_weather"""
import json
import unittest
from unittest import mock

import test_acquaint as ta
from test_acquaint import ELS, NPC  # noqa: F401

import acquaint
import brain
import cards
import exchange
import party
import weather


class Weather(unittest.TestCase):
    def test_one_answer_per_day_for_everyone_and_stable(self):
        self.assertEqual(weather.today("2026-11-10"), weather.today("2026-11-10"))
        self.assertEqual(weather.today("2026-11-10"), "dry and mild - neither hot nor cold - with a few clouds")      # pinned: a restart must not reroll it
        seen = {weather.today(f"2026-{m:02d}-{d:02d}") for m in range(1, 13) for d in range(1, 29)}
        self.assertEqual(seen, {t for _, t in weather.KINDS})                                 # every kind turns up over a year
        dry = sum("rain" not in weather.today(f"2026-{m:02d}-{d:02d}") for m in range(1, 13) for d in range(1, 29))
        self.assertGreater(dry, 336 * 0.7)                                                    # mostly dry

    def test_the_line_states_a_fact_and_says_not_to_raise_it(self):
        s = weather.line("2026-11-10")
        self.assertEqual(s, "The weather today, the same for everyone here: dry and mild - neither hot nor cold - with a "
                            "few clouds. Do not bring the weather, the sky, the sun or the heat or cold up yourself; if it does "
                            "come up, it is exactly this.")
        self.assertEqual(weather.line("2026-11-10", False), "")
        self.assertEqual(weather.line("", True), "")
        self.assertEqual(weather.line(None, True), "")
        with mock.patch.object(weather, "today", side_effect=RuntimeError("boom")):
            self.assertEqual(weather.line("2026-11-10"), "")                                  # never raises


class Paragraphs(unittest.TestCase):
    PARTY = {"companions": [{"name": "Elspeth Vale", "title": "the field healer", "by_title": False}], "pets": []}
    ASK = {"speaker": {"kind": "townsperson", "name": "Osric", "title": "the tanner", "first": "Osric"},
           "addressee": {"kind": "companion", "name": "Elspeth Vale", "title": "the field healer", "first": "Elspeth"},
           "band": "", "last": False}

    def test_no_angle_is_stage_a1_byte_for_byte(self):
        base = party.block(self.PARTY, "companion", "KNOWN.")
        self.assertEqual(party.block(self.PARTY, "companion", "KNOWN.", ""), base)
        new = party.block(self.PARTY, "companion", "KNOWN.", party.ANGLES[1])
        add = " Make it a remark on their trade or calling. Not about how far they have travelled or how tired they look - every stranger says that."
        self.assertEqual(new, base.replace("(first name only).", "(first name only)." + add))
        for turn in ("quiet", "pet"):                                # the angle belongs to a companion turn only
            self.assertEqual(party.block(self.PARTY, turn, "", party.ANGLES[0]), party.block(self.PARTY, turn, ""))

    def test_exchange_rules_without_weather_are_unchanged(self):
        base = exchange.rules(self.ASK, "KNOWN.")
        self.assertEqual(exchange.rules(self.ASK, "KNOWN.", (), ""), base)
        new = exchange.rules(self.ASK, "KNOWN.", (), "WEATHER.")
        self.assertEqual(new, base.replace("KNOWN. ", "KNOWN. WEATHER. "))


class H(unittest.TestCase):
    ELSPETH, TANNER = ta.Handlers.ELSPETH, ta.Handlers.TANNER
    post, exchange, respond, party = ta.Handlers.post, ta.Handlers.exchange, ta.Handlers.respond, ta.Handlers.party
    setUp, tearDown = ta.Handlers.setUp, ta.Handlers.tearDown
    K = {"epoch": 7, "mpt": 30.0, "other_id": ELS}

    def wx(self):
        return weather.line(acquaint.central_today(cards.CENTRAL))

    def test_defaults_are_off_and_add_nothing(self):
        self.assertIs(brain.DEFAULTS["shared_weather"], False)
        out, prompt, _ = self.exchange(self.K)
        self.assertNotIn("The weather today", prompt)
        out, prompt, _ = self.respond(self.party())
        self.assertNotIn("The weather today", prompt)
        self.assertNotIn("Make it ", prompt)
        self.assertNotIn("how far they have travelled", prompt)

    def test_switched_on_every_prompt_carries_the_same_weather(self):
        with mock.patch.dict(brain.CFG, {"shared_weather": True}):
            out, a, _ = self.exchange(self.K)
            out, b, _ = self.exchange({"epoch": 7, "mpt": 30.0, "other_id": NPC}, speaker_is_npc=False)   # the companion's line
            out, c, _ = self.respond(self.party())                              # an ordinary reply, companion turn
            out, d, _ = self.respond(self.party(), companion_share=0.0)         # an ordinary reply, quiet turn
            body = {"request_id": "t", "npc_id": NPC, "subject_id": ta.CHAR, "priority": "Conversation", "persona": "PERSONA",
                    "utterance": "Good day.", "budget_ms": 8000, "history": []}
            self.sent.clear()
            with mock.patch.object(brain.urllib.request, "urlopen", self._fake()):
                self.post("/v1/npc/respond", body)                              # no company at all
            e = json.dumps(self.sent[-1]["messages"])
        want = json.dumps(self.wx())[1:-1]
        for prompt in (a, b, c, d, e):
            self.assertEqual(prompt.count(want), 1)

    def _fake(self):
        class R:
            def __init__(s, b): s.b = b
            def __enter__(s): return s
            def __exit__(s, *a): return False
            def read(s, *a): return s.b

        def fake(rq, timeout=None):
            self.sent.append(json.loads(rq.data))
            return R(json.dumps({"message": {"content": json.dumps({"say": "Good day to you.", "action": "none"})},
                                 "prompt_eval_count": 1, "eval_count": 1}).encode())
        return fake

    def test_the_aside_gets_an_angle_only_with_the_topics_setting_on(self):
        with mock.patch.dict(brain.CFG, {"acquaintance_topics": True}), \
                mock.patch.object(brain.rumour_speech.RNG, "choice", lambda seq: seq[2]):
            out, prompt, _ = self.respond(self.party())
            self.assertIn("Make it a friendly question about how their day goes. Not about how far they have travelled", prompt)
            out, prompt, _ = self.respond(self.party(), companion_share=0.0)
            self.assertNotIn("Make it ", prompt)


if __name__ == "__main__":
    unittest.main()
