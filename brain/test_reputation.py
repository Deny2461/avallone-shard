"""Step 2e - a town's reputation of a player (design §8.4, §9.6). Run: python3 -m unittest test_reputation"""
import os
import random
import tempfile
import unittest

import dials
import memory
import reputation
import rumours

CFG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "rumours.json")
WREN, OTHER = "22222222-2222-2222-2222-222222222222", "33333333-3333-3333-3333-333333333333"
NPC = "44444444-4444-4444-4444-444444444444"
H, DAY = 3600, 86400
W = rumours.REPUTATION_S
NEAR_BRITAIN = {"map": "Trammel", "x": 1492, "y": 1641}


class ReputationTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.led.reconcile(1, 0, 0)
        self.r = rumours.Rumours(self.led, CFG)
        self.seq = 0

    def tearDown(self):
        self.led.db.close()
        self.led.rdb.close()
        self.dir.cleanup()

    def deed(self, typ, pt, char=WREN, subject="dragon", **extra):
        self.seq += 1
        facts = {"type": typ, "subject": subject, "count": 1, "who": "Racer X", "descriptor": "a tamer",
                 "place": "Destard", "pt": pt, **NEAR_BRITAIN, **extra}
        _a, rej = self.led.ingest([{"event_id": f"1-{self.seq}", "epoch": 1, "seq": self.seq, "npc_id": rumours.WORLD_ID,
                                    "char_id": char, "kind": "deed", "facts": facts, "at_utc": "2026-11-13T16:00:00Z",
                                    "schema": memory.SCHEMA_VERSION}])
        self.assertEqual(rej, [])
        return f"1-{self.seq}"

    def rep(self, pt, town="Britain", char=WREN):
        return reputation.of(self.r, char, town, pt)

    def test_one_notable_deed_is_recognised_for_its_whole_window(self):
        self.deed("kill_notable", 0)
        for t in (0, 1, 24 * H, 5 * DAY, W - 1):                 # arrival, +1 s, after the speech lifetime, near expiry
            got = self.rep(t)
            self.assertEqual(got["label"], "renown", t)
            self.assertIn("slew a dragon", got["line"])
        self.assertIsNone(self.rep(W)["label"])                    # gone exactly at the window's end
        self.assertLess(self.rep(5 * DAY)["renown"], self.rep(1)["renown"])   # while the score fades

    def test_murder_is_dread_and_provoking_is_only_a_joke(self):
        self.deed("murder", 0)
        self.assertEqual(self.rep(H)["label"], "dread")
        self.tearDown(); self.setUp()
        self.deed("provoked", 0, subject="ogre", other="dragon")
        got = self.rep(H)
        self.assertEqual((got["label"], got["dread"]), ("jest", 0.0))

    def test_line_is_the_original_story_not_a_correction(self):
        sid = self.deed("tame_failstreak", 0, subject="cougar")
        _a, rej = self.led.ingest([{"event_id": "1-99", "epoch": 1, "seq": 99, "npc_id": rumours.WORLD_ID, "char_id": WREN,
                                    "kind": "rumour_corrected", "facts": {"story": sid, "pt": 10, "thing": "rabbit"},
                                    "at_utc": "2026-11-13T16:00:00Z", "schema": memory.SCHEMA_VERSION}])
        self.assertEqual(rej, [])
        line = self.rep(H)["line"]
        self.assertIn("cougar", line)
        self.assertNotIn("rabbit", line)

    def test_gm_drop_and_deleted_character_count_for_nothing(self):
        sid = self.deed("kill_notable", 0)
        self.r.drop(sid, "2026-11-13T16:00:00Z")
        self.assertIsNone(self.rep(H)["label"])

    def test_reach_follows_the_one_spread_function(self):
        sid = self.deed("tame_failstreak", 0)                     # weight 1: its own town and the next ones
        g = self.r._group(self.r._deeds(0))[-1]
        far = next(t for t in self.r.cfg["towns"] if t not in self.r._spread(g))
        self.assertEqual(self.rep(H)["label"], "jest")
        self.assertIsNone(self.rep(2 * DAY, town=far)["label"])
        self.assertEqual(sid, g["id"])

    def test_a_story_born_before_the_window_but_arriving_inside_it_counts(self):
        self.deed("kill_notable", 0)
        g = self.r._group(self.r._deeds(0))[-1]
        far, (arrive, _p) = max(self.r._spread(g).items(), key=lambda kv: kv[1][0])
        self.assertGreater(arrive, 0)
        self.assertEqual(self.rep(arrive + W - 1, town=far)["label"], "renown")   # first deed is > W old by now
        self.assertIsNone(self.rep(arrive + W, town=far)["label"])

    def test_repeats_damp_and_tallies_cap(self):
        for i in range(12):
            self.deed("kill_notable", i * 2 * DAY, subject="beast" + "abcdefghijkl"[i])
        got = self.rep(23 * DAY)
        self.assertLessEqual(got["renown"], reputation.CAP)

    def test_bounded_read_equals_full_history(self):
        types = ["kill_notable", "murder", "tame_failstreak", "drunk", "pacified", "died_notable", "provoked"]
        for trial in range(5):
            self.tearDown(); self.setUp()
            rnd, t = random.Random(trial), 0
            for _ in range(300):
                t += rnd.choice([60, 3600, 7300, 30000, DAY])
                self.deed(rnd.choice(types), t, char=rnd.choice([WREN, OTHER]), subject=rnd.choice(["dragon", "ettin"]),
                          other="ogre")
            for pt in (t, t + 1, t + 3 * DAY):
                bounded = {town: self.rep(pt, town=town) for town in ("Britain", "Yew", "Moonglow")}
                real = reputation.lookback_s
                reputation.lookback_s = lambda r: 10 ** 9
                try:
                    full = {town: self.rep(pt, town=town) for town in ("Britain", "Yew", "Moonglow")}
                finally:
                    reputation.lookback_s = real
                for town in bounded:
                    a, b = bounded[town], full[town]
                    self.assertEqual((a["label"], a["renown"], a["dread"], a["jest"], a["line"]),
                                     (b["label"], b["renown"], b["dread"], b["jest"], b["line"]), (trial, pt, town))

    # ---- the dials and the prompt line ----
    def standing(self, pt, town="Britain"):
        return {"npc_id": NPC, "subject_id": WREN, "standing": {"epoch": 1, "karma": 0, "fame": 0, "hour": 12,
                                                              "town": town, "pt": pt, "mpt": pt}}

    def test_dials_use_the_faded_score_and_the_line_goes_to_strangers_only(self):
        base = dials.compute(self.standing(H), self.led, self.r)
        self.deed("murder", 0)
        d = dials.compute(self.standing(H), self.led, self.r)
        self.assertLess(d["trust"], base["trust"])
        self.assertIn("reputation_line", d)
        self.assertIn("Folk in Britain have heard", dials.cue(d))           # a stranger
        known = dict(d, familiarity=10)
        self.assertNotIn("Folk in Britain have heard", dials.cue(known))     # an NPC that knows them uses its own memory


if __name__ == "__main__":
    unittest.main()
