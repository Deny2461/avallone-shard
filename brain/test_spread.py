"""Step 2e - spread by deed strength (design A2 / §8.3). Run: python3 -m unittest test_spread"""
import json
import os
import tempfile
import unittest

import memory
import rumours

CFG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "rumours.json")
WREN = "22222222-2222-2222-2222-222222222222"
H = 3600
NEAR_BRITAIN = {"map": "Trammel", "x": 1492, "y": 1641}


class SpreadTests(unittest.TestCase):
    def setUp(self, cfg_path=CFG):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.led.reconcile(1, 0, 0)
        self.r = rumours.Rumours(self.led, cfg_path)
        self.seq = 0

    def tearDown(self):
        self.led.db.close()
        self.led.rdb.close()
        self.dir.cleanup()

    def group(self, typ):
        self.seq += 1
        facts = {"type": typ, "subject": "cougar", "count": 1, "who": "Racer X", "descriptor": "a tamer",
                 "place": "Destard", "pt": 0, **NEAR_BRITAIN}
        _a, rej = self.led.ingest([{"event_id": f"1-{self.seq}", "epoch": 1, "seq": self.seq, "npc_id": rumours.WORLD_ID,
                                    "char_id": WREN, "kind": "deed", "facts": facts, "at_utc": "2026-11-13T16:00:00Z",
                                    "schema": memory.SCHEMA_VERSION}])
        self.assertEqual(rej, [])
        return self.r._group(self.r._deeds(0))[-1]

    def neighbours(self, town):
        return {t for t, _h in self.r.cfg["_graph"][town]}

    def test_weights_in_config_are_what_the_design_says(self):
        w = {n: d["weight"] for n, d in self.r.cfg["deeds"].items()}
        self.assertEqual((w["kill_notable"], w["tame_notable"], w["tame_failstreak"]), (3, 2, 1))

    def test_weight_one_stays_in_its_town_and_the_next_ones(self):
        g = self.group("tame_failstreak")
        self.assertEqual(set(self.r._spread(g)), {"Britain"} | self.neighbours("Britain"))

    def test_weight_two_reaches_about_half_the_map(self):
        g = self.group("tame_notable")
        hops = self.r._hops("Britain")
        self.assertEqual(set(self.r._spread(g)), {t for t, d in hops.items() if d <= 2.5})
        self.assertLess(len(self.r._spread(g)), len(self.r.cfg["towns"]))

    def test_weight_three_reaches_everywhere_and_faster(self):
        g = self.group("kill_notable")
        fast = self.r._spread(g)
        self.assertEqual(set(fast), set(self.r.cfg["towns"]))
        self.r.cfg["weighted_spread"] = False
        slow = self.r._spread(g)
        far = max(slow, key=lambda t: slow[t][0])
        self.assertLess(fast[far][0], slow[far][0])
        self.assertAlmostEqual((fast[far][0] - g["first_pt"]) * 1.5, slow[far][0] - g["first_pt"], delta=2)

    def test_madeup_stories_carry_no_type_and_keep_todays_travel(self):
        sp = self.r._spread({"id": "mu-12345678-abcd-1", "origin": "Britain", "first_pt": 0})
        self.assertEqual(set(sp), set(self.r.cfg["towns"]))

    def test_switch_off_is_todays_behaviour(self):
        self.r.cfg["weighted_spread"] = False
        g = self.group("tame_failstreak")
        self.assertEqual(set(self.r._spread(g)), set(self.r.cfg["towns"]))

    def test_speech_and_town_unease_see_the_same_reach(self):
        import dials
        g = self.group("tame_failstreak")
        far = next(t for t in self.r.cfg["towns"] if t not in self.r._spread(g))
        late = 20 * H
        self.assertNotIn(far, {row["town"] for row in self.r.view(late) if row["id"] == g["id"]})
        self.assertEqual([x for x in dials.Exposure(self.r).live_in(far, late) if x[0]["id"] == g["id"]], [])


if __name__ == "__main__":
    unittest.main()
