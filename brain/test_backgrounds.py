"""Step 2e - NPC backgrounds and gossip habits (design §3, §8.5, §9.3-9.5, A3). Run: python3 -m unittest test_backgrounds"""
import collections
import os
import tempfile
import threading
import unittest

import backgrounds
import decency
import memory
import nature


def npc(i):
    return f"{i:08d}-1111-4111-8111-111111111111"


class BackgroundTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.led.reconcile(1, 0, 0)
        backgrounds.ensure_table(self.led)
        self.c = backgrounds.cfg()

    def tearDown(self):
        self.led.db.close()
        self.led.rdb.close()
        self.dir.cleanup()

    def tags(self, bg):
        by = self.c["_by_id"]
        return set(by[bg["home"]]["tags"]) | set(by[bg["life"]]["tags"]) | set(by[bg["event"]]["tags"])


    def test_assigned_once_and_never_changed(self):
        first = backgrounds.assign(self.led, npc(1), "Britain", "provisioner", "vendor")
        again = backgrounds.assign(self.led, npc(1), "Yew", "healer", "healer")   # moved, new trade: the past stays
        self.assertEqual(first, again)
        self.assertEqual(backgrounds.get(self.led, npc(1))["event"], first["event"])

    def test_inspection_never_assigns(self):
        self.assertIsNone(backgrounds.get(self.led, npc(2)))
        self.assertIsNone(backgrounds.get(self.led, npc(2)))
        n = self.led.rdb.execute("SELECT COUNT(*) FROM npc_backgrounds").fetchone()[0]
        self.assertEqual(n, 0)



    def test_question_detector(self):
        for q in ("Where are you from?", "where were you born", "What did you do before this?", "tell me about yourself",
                  "How long have you been here?"):
            self.assertTrue(backgrounds.asked(q), q)
        for q in ("Any news?", "How are you?", "Where is the bank?", "What do you sell?"):
            self.assertFalse(backgrounds.asked(q), q)

    def test_unasked_shares_the_flourish_slot_and_never_runs_twice(self):
        n = npc(9000)
        self.assertFalse(nature.background_in_prompt(n, "p1", "t1", slot_taken=True, now=0))
        first = nature.background_in_prompt(n, "p2", "t1", slot_taken=False, now=0)
        self.assertEqual(first, nature.background_in_prompt(n, "p2", "t1", slot_taken=False, now=1))   # a retry
        shown = [nature.background_in_prompt(npc(9100 + i), "p", f"t{j}", slot_taken=False, now=j * 1000)
                 for i in range(60) for j in range(20)]
        rate = sum(shown) / len(shown)
        self.assertGreater(rate, 0.05)
        self.assertLess(rate, 0.25)
        for i in range(60):
            run = [nature.background_in_prompt(npc(9300 + i), "p", f"t{j}", slot_taken=False, now=j) for j in range(30)]
            self.assertFalse(any(a and b for a, b in zip(run, run[1:])))


class GossipHabitTests(unittest.TestCase):
    def test_shares_match_the_configured_numbers(self):
        got = collections.Counter((nature.of(npc(i)) or {}).get("gossip") for i in range(6000))
        self.assertAlmostEqual(got["gossip"] / 6000, 0.25, delta=0.03)
        self.assertAlmostEqual(got["cautious"] / 6000, 0.20, delta=0.03)
        self.assertAlmostEqual(got["sceptic"] / 6000, 0.1667, delta=0.03)

    def test_the_lines_of_doubt_are_clean(self):
        import brain
        for line in brain.SCEPTIC_LINES:
            self.assertIsNone(decency.check(line), line)
            self.assertLess(len(line), 40)


if __name__ == "__main__":
    unittest.main()
