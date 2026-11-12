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


class RealWeather(unittest.TestCase):
    """Rumour step C3 (2026-11-11): the line follows what the game shows falling where the NPC stands."""
    WET_DAY = next(f"2026-{m:02d}-{d:02d}" for m in range(1, 13) for d in range(1, 29)
                   if "rain" in weather.today(f"2026-{m:02d}-{d:02d}"))
    FOG_DAY = next(f"2026-{m:02d}-{d:02d}" for m in range(1, 13) for d in range(1, 29)
                   if "fog" in weather.today(f"2026-{m:02d}-{d:02d}"))
    DRY_DAY = "2026-11-10"

    def test_setting_off_is_the_old_sentence_whatever_the_shard_says(self):
        for day in (self.WET_DAY, self.FOG_DAY, self.DRY_DAY):
            for falling in (None, "dry", "rain", "snow", "hail", 7):
                self.assertEqual(weather.line(day, True, falling, False), weather.line(day))
                self.assertEqual(weather.line(day, True, falling), weather.line(day))        # real defaults to off

    def test_rain_and_snow_replace_the_days_description(self):
        for day in (self.WET_DAY, self.DRY_DAY):
            r, s = weather.line(day, True, "rain", True), weather.line(day, True, "snow", True)
            self.assertTrue(r.startswith("The weather right now, the same for everyone here: rain is falling, and it is cool. "))
            self.assertTrue(s.startswith("The weather right now, the same for everyone here: snow is falling, and it is cold. "))
            self.assertNotIn(weather.today(day), r + s)
            self.assertIn("Do not bring the weather", r)                                     # still a fact, not a subject

    def test_dry_never_gives_a_wet_kind(self):
        dry = [t for _, t in weather.DRY_KINDS]
        self.assertEqual(len(dry), 5)
        self.assertFalse([t for t in dry if "rain" in t or "fog" in t or "damp" in t])
        for day in (self.WET_DAY, self.FOG_DAY):
            text, now = weather.describe(day, "dry", True)
            self.assertIn(text, dry)
            self.assertFalse(now)
            self.assertEqual(weather.describe(day, "dry", True), weather.describe(day, "dry", True))   # stable all day
        for m in range(1, 13):
            for d in range(1, 29):
                self.assertIn(weather.describe(f"2026-{m:02d}-{d:02d}", "dry", True)[0], dry)

    def test_dry_on_a_dry_day_is_the_same_description_as_before(self):
        self.assertEqual(weather.line(self.DRY_DAY, True, "dry", True), weather.line(self.DRY_DAY))

    def test_unknown_is_the_days_description_as_before(self):
        for day in (self.WET_DAY, self.FOG_DAY, self.DRY_DAY):
            for falling in (None, "", "hail", 3, ["rain"]):
                self.assertEqual(weather.line(day, True, falling, True), weather.line(day))

    def test_the_four_answers_stay_apart(self):
        got = {w: weather.describe(self.WET_DAY, weather.word(w), True) for w in (None, "dry", "rain", "snow")}
        self.assertEqual(len(set(got.values())), 4)

    def test_the_companions_sentence_only_while_something_falls(self):
        self.assertIn("rain is falling", weather.falling_line(True, "rain", True))
        self.assertIn("snow is falling", weather.falling_line(True, "snow", True))
        for args in ((True, "dry", True), (True, None, True), (True, "rain", False), (False, "rain", True), (True, "hail", True)):
            self.assertEqual(weather.falling_line(*args), "")
        with mock.patch.object(weather, "word", side_effect=RuntimeError("boom")):
            self.assertEqual(weather.falling_line(True, "rain", True), "")                   # never raises


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

    # ---- rumour step C3: the handlers pass the shard's word through, under the real_weather setting ----
    def _plain(self, falling=None, **over):
        body = {"request_id": "t", "npc_id": NPC, "subject_id": ta.CHAR, "priority": "Conversation", "persona": "PERSONA",
                "utterance": "Good day.", "budget_ms": 8000, "history": []}
        if falling is not None:
            body["falling"] = falling
        body.update(over)
        self.sent.clear()
        with mock.patch.object(brain.urllib.request, "urlopen", self._fake()):
            self.post("/v1/npc/respond", body)
        return json.dumps(self.sent[-1]["messages"])

    def test_real_weather_default_is_off(self):
        self.assertIs(brain.DEFAULTS["real_weather"], False)
        with mock.patch.dict(brain.CFG, {"shared_weather": True}):
            for falling in (None, "rain", "snow", "dry"):
                p = self._plain(falling)
                self.assertEqual(p.count(json.dumps(self.wx())[1:-1]), 1)
                self.assertNotIn("is falling", p)

    def test_real_weather_on_rain_reaches_an_ordinary_reply_and_an_exchange(self):
        with mock.patch.dict(brain.CFG, {"shared_weather": True, "real_weather": True}):
            self.assertIn("The weather right now, the same for everyone here: rain is falling", self._plain("rain"))
            self.assertIn("The weather right now, the same for everyone here: snow is falling", self._plain("snow"))
            p = self._plain(None)                                                  # the shard did not say: as before
            self.assertEqual(p.count(json.dumps(self.wx())[1:-1]), 1)
            self.assertNotIn("is falling", self._plain("hail"))                    # not one of the three words: unknown
        with mock.patch.dict(brain.CFG, {"shared_weather": False, "real_weather": True}):
            self.assertNotIn("weather", self._plain("rain").lower())               # needs the shared-weather setting too

    def _chat(self, falling):
        def fake(url, payload, timeout_s):
            self.sent.append(json.loads(payload))
            return {"message": {"content": json.dumps({"say": "And to you."})}, "prompt_eval_count": 1, "eval_count": 1}
        x = {"id": "x1", "step": 0, "speaker": dict(self.ELSPETH), "addressee": dict(self.TANNER), "trust_band": "warming",
             "transcript": [{"who": "Osric", "text": "Good day, Elspeth."}], "roster": []}
        body = {"npc_id": ELS, "persona_key": "elspeth", "request_id": "t", "subject_id": ta.CHAR, "priority": "Background",
                "budget_ms": 6000, "exchange": x}
        if falling is not None:
            body["falling"] = falling
        with mock.patch.object(brain, "_post_cancellable", fake):
            self.post("/v1/npc/exchange", body)
        return json.dumps(self.sent[-1]["messages"])

    def test_real_weather_reaches_a_chat_between_npc_and_companion(self):
        with mock.patch.dict(brain.CFG, {"shared_weather": True, "real_weather": True}):
            self.assertIn("The weather right now, the same for everyone here: snow is falling", self._chat("snow"))
            self.assertEqual(self._chat(None).count(json.dumps(self.wx())[1:-1]), 1)
        with mock.patch.dict(brain.CFG, {"shared_weather": True}):
            self.assertNotIn("is falling", self._chat("snow"))                      # real_weather off: as before

    def test_a_companions_own_reply_hears_of_rain_only_while_it_falls(self):
        with mock.patch.dict(brain.CFG, {"shared_weather": True, "real_weather": True, "prompt_layout": "v2"}):
            wet = self._plain("rain", priority="Companion")
            self.assertEqual(wet.count("Right now, where you stand, rain is falling, and it is cool."), 1)
            for falling in ("dry", None):
                p = self._plain(falling, priority="Companion")
                self.assertNotIn("is falling", p)
                self.assertNotIn("The weather today", p)                             # her own reply never got the day line
        with mock.patch.dict(brain.CFG, {"shared_weather": True, "prompt_layout": "v2"}):
            self.assertNotIn("is falling", self._plain("rain", priority="Companion"))   # real_weather off: as before

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
