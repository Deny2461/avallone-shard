"""5c-2 2c NPC mood. Run: python3 test_mood.py
Design: wiki Projects/Avallone-Memory-Affinity-Design.md section V1.5 + M7 (design v1.1, Codex 2c design review)."""
import os
import random
import tempfile
import unittest
from datetime import datetime
from zoneinfo import ZoneInfo

import memory
import mood

NPC, NPC2 = "11111111-1111-1111-1111-111111111111", "44444444-4444-4444-4444-444444444444"
A, B, C = ("22222222-2222-2222-2222-22222222222%d" % i for i in (1, 2, 3))
H = 3600
NOW = datetime(2026, 11, 3, 12, 0, tzinfo=ZoneInfo("America/Chicago")).timestamp()
TODAY = "2026-11-03"


class Rig(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.led.reconcile(1, 0, 0)
        self.epoch, self.seq = 1, 0

    def tearDown(self):
        self.led.db.close()
        self.led.rdb.close()
        self.dir.cleanup()

    def put(self, kind, facts, npc=NPC, char=A, ok=True):
        self.seq += 1
        eid = f"{self.epoch}-{self.seq}"
        acked, rej = self.led.ingest([{"event_id": eid, "epoch": self.epoch, "seq": self.seq, "npc_id": npc,
                                       "char_id": char, "kind": kind, "facts": facts, "at_utc": "2026-11-03T18:00:00Z",
                                       "schema": memory.SCHEMA_VERSION}])
        self.assertEqual(rej == [], ok, (kind, facts))
        return self.seq

    def gift(self, pt, char=A, day=TODAY, **kw):
        f = {"day": day, "mpt": 0, "kind": "gold", "value": "fair", **kw}
        if pt is not None:
            f["pt"] = pt
        return self.put("gift_given", f, char=char)

    def visit(self, char, day=TODAY):
        return self.put("visited", {"day": day, "mpt": 0}, char=char)

    def req(self, pt=10 * H, hour=12, npc=NPC, persona="townsfolk", epoch=None):
        return {"npc_id": npc, "persona_key": persona,
                "standing": {"epoch": self.epoch if epoch is None else epoch, "pt": pt, "hour": hour}}

    def mood(self, u=0.0, **kw):
        return mood.compute(self.req(**kw), self.led, {"sources": {"unease": u}}, now=NOW)


class Ledger(Rig):
    def test_gift_pt_optional_on_read_and_checked(self):
        self.gift(None)                                   # 2b row, no pt: still accepted
        self.gift(5 * H, char=B)
        self.put("gift_given", {"day": TODAY, "mpt": 99 * H, "kind": "gold", "value": "fair", "pt": H}, char=C,
                 ok=False)                                # memory clock ahead of the played clock
        self.put("gift_given", {"day": TODAY, "mpt": 0, "kind": "gold", "value": "fair", "pt": 1.5}, char=C, ok=False)
        self.put("gift_given", {"day": TODAY, "mpt": 0, "kind": "gold", "value": "fair", "pt": -1}, char=C, ok=False)
        self.put("gift_given", {"day": TODAY, "mpt": 0, "kind": "gold", "value": "fair", "item": "x"}, char=C,
                 ok=False)

    def test_npc_rows_gate_and_scope(self):
        self.gift(H)
        self.visit(B)
        self.put("visited", {"day": TODAY}, npc=NPC2, char=C)
        self.assertEqual(len(self.led.npc_rows(NPC, 1)), 2)
        self.assertEqual(len(self.led.npc_rows(NPC, 1, kinds=("visited",), day=TODAY)), 1)
        self.assertEqual(self.led.npc_rows(NPC, 1, kinds=("visited",), day="2026-11-02"), [])
        self.assertIsNone(self.led.npc_rows(NPC, 2))       # not reconciled for this epoch
        self.assertIsNone(self.led.npc_rows(NPC, True))    # a bool is not an epoch

    def test_index_used(self):
        plan = " ".join(str(tuple(r)) for r in self.led.rdb.execute(
            "EXPLAIN QUERY PLAN SELECT 1 FROM events WHERE npc_id = ? AND kind IN (?, ?) AND status = 'live'",
            (NPC, "visited", "gift_given")).fetchall())
        self.assertIn("events_npc_kind", plan)


class Lifecycle(Rig):
    """Codex 2c design review F1 acceptance: deletion, restore, older-epoch survivors, unreconciled epochs."""

    def test_deleted_donor_drops_out_and_restore_brings_back(self):
        self.gift(10 * H, char=A)
        self.visit(A)
        self.visit(B)
        before = self.mood()
        self.assertEqual(before["evidence"]["gifts_counted"], 1)
        self.assertEqual(before["evidence"]["visitors_today"], 2)
        cut = self.seq
        self.put("character_deleted", {}, npc="world", char=A)
        after = self.mood()
        self.assertEqual(after["evidence"]["gifts_counted"], 0)
        self.assertEqual(after["evidence"]["visitors_today"], 1)
        self.assertEqual(after["cheer_parts"]["gift"], 0)
        # world restore to before the deletion: the gift and A's visit count again
        self.assertEqual(self.led.reconcile(2, 1, cut)[0], "ok")
        self.epoch = 2
        back = self.mood()
        self.assertEqual(back["evidence"]["gifts_counted"], 1)
        self.assertEqual(back["evidence"]["visitors_today"], 2)
        self.assertEqual((back["cheer"], back["energy"]), (before["cheer"], before["energy"]))

    def test_restore_before_gift_keeps_older_epoch_visit(self):
        self.visit(B)
        cut = self.seq
        self.gift(10 * H, char=A)
        self.assertEqual(self.led.reconcile(2, 1, cut)[0], "ok")
        self.epoch = 2
        m = self.mood()
        self.assertEqual(m["evidence"]["gifts_counted"], 0)     # rolled back with the world
        self.assertEqual(m["evidence"]["visitors_today"], 1)    # epoch-1 survivor still counts (not epoch == current)

    def test_unreconciled_epoch_gives_no_mood(self):
        self.gift(10 * H)
        self.assertIsNone(mood.compute(self.req(epoch=2), self.led, {"sources": {}}, now=NOW))

    def test_same_inputs_same_mood_any_listener_or_order(self):
        self.gift(10 * H, char=B)
        m1 = self.mood()
        self.visit(C)
        self.visit(C)                                   # a second row for the same character/day counts once
        m2 = self.mood()
        self.assertEqual(m2["evidence"]["visitors_today"], 1)
        self.assertEqual(m1["cheer"], m2["cheer"])
        r = self.req()
        r["subject_id"] = A                             # who is asking never matters
        self.assertEqual(mood.compute(r, self.led, {"sources": {"unease": 0}}, now=NOW)["cheer"], m2["cheer"])


class Shape(Rig):
    def test_day_draw_stable_and_date_bound_not_epoch(self):
        self.assertEqual(mood.day_draw(NPC, TODAY), mood.day_draw(NPC, TODAY))
        days = {mood.day_draw(NPC, f"2026-11-{d:02d}") for d in range(1, 29)}
        self.assertGreater(len(days), 3)
        m1 = self.mood()
        self.led.reconcile(2, 1, self.seq)
        self.epoch = 2
        self.assertEqual(self.mood()["day_type"], m1["day_type"])   # a restart never rerolls the day

    def test_distribution_one_in_six(self):
        """the owner 2c-2: ~1 day in 6 sour/weary, ~1 in 6 bright - on the daily draw, 300 NPCs x 120 days."""
        rng = random.Random(3)
        kinds = {}
        n = 0
        for i in range(300):
            npc = "%032x" % rng.getrandbits(128)
            for d in range(120):
                k = mood.day_draw(npc, f"d{d}")[0]
                kinds[k] = kinds.get(k, 0) + 1
                n += 1
        off = sum(kinds.get(k, 0) for k in ("weary", "glum", "short-tempered")) / n
        bright = sum(kinds.get(k, 0) for k in ("cheerful", "lively")) / n
        self.assertAlmostEqual(off, 1 / 6, delta=0.02)
        self.assertAlmostEqual(bright, 1 / 6, delta=0.02)
        for k in ("weary", "glum", "short-tempered", "cheerful", "lively"):
            self.assertGreater(kinds.get(k, 0), 0, k)   # sim 2026-11-03: lively used never to happen

    def test_band_map(self):
        self.assertEqual(mood.band_of(-50, 20)[0], "short-tempered")
        self.assertEqual(mood.band_of(-50, 0)[0], "glum")
        self.assertEqual(mood.band_of(0, -50)[0], "weary")
        self.assertEqual(mood.band_of(-50, -50)[0], "glum")       # first match wins
        self.assertEqual(mood.band_of(50, 50)[0], "cheerful")
        self.assertEqual(mood.band_of(0, 50)[0], "lively")
        self.assertEqual(mood.band_of(29, -29), ("content", "", None))
        self.assertEqual(mood.band_of(0, -35)[1], "a little")
        self.assertEqual(mood.band_of(0, -50)[1], "")
        self.assertEqual(mood.band_of(0, -75)[1], "very")

    def test_events_capped(self):
        for i in range(40):
            self.gift(10 * H, char="%036d" % i)
        m = self.mood()
        self.assertLessEqual(m["cheer_parts"]["gift"], 25.0)      # saturates
        for i in range(40):
            self.visit("%036d" % (100 + i))
        m = self.mood(u=1.0, hour=2)
        self.assertGreaterEqual(sum(v for k, v in m["energy_parts"].items() if k not in ("rest", "day")), -40.0)


class Gifts(Rig):
    def test_fade_over_four_played_hours(self):
        self.gift(10 * H)
        self.assertGreater(self.mood(pt=10 * H)["cheer_parts"]["gift"], 9)
        mid = self.mood(pt=12 * H)["cheer_parts"]["gift"]
        self.assertTrue(3 < mid < 7, mid)
        self.assertEqual(self.mood(pt=14 * H)["cheer_parts"]["gift"], 0)

    def test_future_and_legacy_give_nothing(self):
        self.gift(20 * H)                 # sampled after "now" (a restore / clock mismatch): never counts as fresh
        self.gift(None, char=B)           # 2b row without pt: warmth only, no mood
        m = self.mood(pt=10 * H)
        self.assertEqual(m["cheer_parts"]["gift"], 0)
        self.assertEqual(m["evidence"]["gift_rows"], 2)

    def test_second_row_same_day_counts_once(self):
        """Accepted rollback round trip (2b F4) can leave two real rows for one pair/day: never counted twice."""
        self.gift(10 * H)
        one = self.mood()["cheer_parts"]["gift"]
        self.gift(10 * H)
        self.assertEqual(self.mood()["cheer_parts"]["gift"], one)

    def test_one_donor_cannot_pin_cheerful_on_an_ordinary_day(self):
        self.gift(10 * H)
        m = self.mood()
        self.assertLessEqual(m["cheer_parts"]["gift"], 12)


class Causes(unittest.TestCase):
    def m(self, band, cheer_parts, energy_parts, helped=False):
        axis = {"glum": "cheer", "short-tempered": "cheer", "cheerful": "cheer", "weary": "energy",
                "lively": "energy"}.get(band)
        out = {"band": band, "strength": "", "cheer_parts": {"rest": 0, "day": 0, **cheer_parts},
               "energy_parts": {"rest": 0, "day": 0, **energy_parts}}
        out["cause"], out["gift_helped"] = mood.cause(out, axis)
        return out

    def test_only_a_real_mover_is_a_cause(self):
        self.assertEqual(self.m("weary", {}, {"long_day": -12, "late_hour": -5})["cause"], "long_day")
        self.assertEqual(self.m("weary", {}, {"long_day": -4, "late_hour": 0})["cause"], "none")   # the day's draw
        self.assertEqual(self.m("glum", {"town_news": -9, "gift": 0}, {})["cause"], "town_news")
        self.assertEqual(self.m("cheerful", {"gift": 10, "town_news": 0}, {})["cause"], "gift")
        self.assertEqual(self.m("content", {"gift": 10}, {})["cause"], "none")

    def test_gift_never_explains_weariness_but_can_help(self):
        m = self.m("weary", {"gift": 10, "town_news": 0}, {"long_day": -12})
        self.assertEqual(m["cause"], "long_day")
        self.assertTrue(m["gift_helped"])
        line = mood.cause_clause(m, random.Random(1))
        self.assertIn(line, mood.cfg()["clauses"]["gift_helped"])

    def test_no_cause_no_explanation(self):
        self.assertEqual(mood.cause_clause(self.m("glum", {"town_news": 0}, {}), random.Random(1)), "")

    def test_town_news_line_is_general(self):
        for line in mood.cfg()["clauses"]["town_news"]:
            self.assertNotRegex(line.lower(), r"dock|murder|dragon|gate|road|\bby\b")

    def test_clause_before_a_closing_question(self):
        self.assertEqual(mood.add_clause("Weary today, Tamsin. What can I get for you?", "It's been a long day."),
                         "Weary today, Tamsin. It's been a long day. What can I get for you?")
        self.assertEqual(mood.add_clause("Weary today.", "It's been a long day."), "Weary today. It's been a long day.")
        self.assertEqual(mood.add_clause("What?", "It's late."), "What? It's late.")
        self.assertEqual(mood.add_clause("Fine.", ""), "Fine.")
        self.assertEqual(mood.add_clause("The day has been long. What can I find for you?", "It's been a long day."),
                         "The day has been long. What can I find for you?")     # trial row: no echo
        self.assertEqual(mood.add_clause("Weary. What'll it be?", "Folk are uneasy with the news going round."),
                         "Weary. Folk are uneasy with the news going round. What'll it be?")

    def test_who_was_it(self):
        mood.note_cause(NPC, A, "gift", now=100.0)
        self.assertTrue(mood.asks_who(NPC, A, "who was it?", now=150.0))
        self.assertTrue(mood.asks_who(NPC, A, "was it me?", now=150.0))
        self.assertFalse(mood.asks_who(NPC, B, "who was it?", now=150.0))      # someone else
        self.assertFalse(mood.asks_who(NPC, A, "who was it?", now=100.0 + 601))  # long ago
        self.assertFalse(mood.asks_who(NPC, A, "what do you sell?", now=150.0))
        mood.note_cause(NPC2, A, "late_hour", now=100.0)
        self.assertFalse(mood.asks_who(NPC2, A, "who was it?", now=150.0))      # no person behind "it's late"

    def test_asks_how(self):
        for s in ("how are you?", "How's business", "are you well?", "you look tired", "how fare you"):
            self.assertTrue(mood.asks_how(s), s)
        for s in ("what do you sell?", "hello", "how much is the bread?", "where is the bank?"):
            self.assertFalse(mood.asks_how(s), s)


class Excuses(unittest.TestCase):
    """Codex 2c F2: the M3b trial rows are regression cases."""
    TRIAL = {
        160: "Say 'Mirabel buy' if you want to see what's left on the tray. I'm too tired for the extra work of "
             "reaching for the basket just yet.",
        0: "Speak 'Mirabel buy' if you wish to see what's for the table, Tamsin. I'm in no mood for a long list today.",
        13: "Say 'Mirabel buy' if you want to see what's on the counter. I'm too tired for a long list today.",
        133: "The fire is too low and the stew is too thick, Tamsin. My patience is thinner than the broth.",
    }

    def test_trial_rows_caught_and_cut_keeps_service(self):
        self.assertIsNotNone(mood.excuse("I have no energy left for haggling today."))
        for i, s in self.TRIAL.items():
            self.assertIsNotNone(mood.excuse(s), i)
        for i in (160, 0, 13):
            cut = mood.strip_excuses(self.TRIAL[i])
            self.assertIn("buy", cut)
            self.assertIsNone(mood.excuse(cut))

    def test_ordinary_lines_pass(self):
        for s in ("Too right, traveller.", "It's too early for ale.", "Say 'Mirabel buy' to see the tray.",
                  "Mm. Long day. What'll it be?", "I've no time for brawlers in my house.",
                  "I've no patience for cheats."):
            self.assertIsNone(mood.excuse(s), s)


class Risky(unittest.TestCase):
    """the owner 2026-11-03 (M9 answer 1, M10): scenery stays; invented illness / shortage / delay / other-people claims go.
    The four rows are the integrated trial's own; the scenery and tone rows are from the same run (must pass)."""
    TRIAL = {
        "illness": "The weather is fine enough, Tamsin. Just keep your voice down, I've a headache brewing.",
        "shortage": "The sun is high and the crates are light! I'm in fine spirits to see a familiar face.",
        "delay": "[huffs] You're blocking my light, Tamsin. Speak quickly, I've a mountain of grain to sort.",
        "people": "My son is poorly, so forgive me. What can I get you?",
    }

    def test_trial_claims_caught_and_cut(self):
        for kind, s in self.TRIAL.items():
            f = mood.flagged(s)
            self.assertIsNotNone(f, kind)
            cut = mood.strip_excuses(s)
            self.assertTrue(cut, kind)
            self.assertIsNone(mood.excuse(cut), kind)
        self.assertEqual(mood.flagged(self.TRIAL["shortage"])[0], "shortage")

    def test_scenery_and_tone_pass(self):
        for s in ("The hearth is warm and the tea is steeping well, Tamsin.", "The sun is high and the crates are full!",
                  "The hearth is warm and the ale is cold, Tamsin.", "The fire is low and the day is long.",
                  "I'm busy enough. What do you want from my stall?", "State your business and make it quick.",
                  "You're making my head ache with questions, traveller.", "I've been on my feet since dawn.",
                  "The sun is bright and the wagons are heavy with good stock!"):
            self.assertIsNone(mood.flagged(s), s)


class Placement(unittest.TestCase):
    def test_place_default_head_and_turn(self):
        c = dict(mood.cfg())
        try:
            self.assertEqual(mood.place(), c.get("place", "head") if c.get("place") == "turn" else "head")
            mood._state["cfg"] = {**c, "place": "turn"}
            self.assertEqual(mood.place(), "turn")
            mood._state["cfg"] = {**c, "place": "bogus"}
            self.assertEqual(mood.place(), "head")
        finally:
            mood._state["cfg"] = c


class VoiceStyle(unittest.TestCase):
    def test_style_hint_only_when_on_and_never_a_reason(self):
        c = dict(mood.cfg())
        m = {"band": "weary", "strength": "a little"}
        try:
            mood._state["cfg"] = {**c, "voice_style": False}
            self.assertNotIn("worn out", mood.voice(m))
            mood._state["cfg"] = {**c, "voice_style": True}
            v = mood.voice(m)
            self.assertIn("a little weary", v)
            self.assertIn("worn out", v)
            self.assertIn("never give a reason", v)
            for band in c["bands"]["order"]:
                self.assertTrue(c["style"].get(band), band)
        finally:
            mood._state["cfg"] = c


class Actions(unittest.TestCase):
    def setUp(self):
        mood._conv.clear()
        mood._npc_act.clear()
        self.m = {"band": "weary"}

    def test_compose_keeps_one_action(self):
        self.assertEqual(mood.compose("[stifles a yawn]", "[sighs] Mm. Yes?"), "[stifles a yawn] Mm. Yes?")
        self.assertEqual(mood.compose("", "[sighs] Mm."), "[sighs] Mm.")

    def test_once_per_conversation_and_npc_gap(self):
        always = random.Random(0)
        always.random = lambda: 0.0
        a = mood.pick_action(self.m, NPC, A, rng=always, now=0.0)
        self.assertTrue(a)
        mood.note_turn(NPC, A, True, now=0.0)
        self.assertEqual(mood.pick_action(self.m, NPC, A, rng=always, now=60.0), "")    # same conversation
        self.assertEqual(mood.pick_action(self.m, NPC, B, rng=always, now=60.0), "")    # NPC gap, anyone
        self.assertTrue(mood.pick_action(self.m, NPC2, B, rng=always, now=60.0))        # another NPC
        self.assertTrue(mood.pick_action(self.m, NPC, B, rng=always, now=400.0))        # gap over

    def test_mirabel_uses_her_teapot_and_never_serves(self):
        always = random.Random(0)
        always.random = lambda: 0.0
        for band in mood.cfg()["bands"]["order"]:
            mood._conv.clear()
            mood._npc_act.clear()
            a = mood.pick_action({"band": band}, NPC, A, "mirabel", rng=always, now=0.0)
            self.assertIn("tea", a)
        for lines in mood.cfg()["personas"]["mirabel"]["habit"].values():
            for line in lines:
                self.assertNotRegex(line, r"\b(you|your|them|their|traveller|for (him|her))\b")

    def test_greet_percent(self):
        self.assertEqual(mood.greet_percent(None), 25)
        self.assertEqual(mood.greet_percent({"band": "glum"}), 0)
        self.assertEqual(mood.greet_percent({"band": "cheerful"}), 40)


class Positive(Rig):
    def test_weary_npc_on_a_quiet_day_reads_weary(self):
        """Positive control: find an NPC whose draw is weary today; with no events it really comes out weary."""
        for i in range(2000):
            npc = "%036d" % i
            if mood.day_draw(npc, TODAY)[0] == "weary":
                break
        m = mood.compute(self.req(npc=npc), self.led, {"sources": {"unease": 0}}, now=NOW)
        self.assertEqual(m["band"], "weary")
        self.assertIn("weary", mood.voice(m))
        self.assertEqual(m["cause"], "none")


if __name__ == "__main__":
    unittest.main()
