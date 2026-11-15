"""Step 2e D1 - a made-up story handed to a companion exchange by typed id only (design §8.6, §9.7; Codex R1-7).
Run: python3 -m unittest test_handoff"""
import os
import random
import tempfile
import unittest

import exchange
import madeup
import memory
import rumours

CFG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "rumours.json")
PT = 50000


class HandoffTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.led.reconcile(1, 0, 0)
        self.r = rumours.Rumours(self.led, CFG)
        self.m = madeup.MadeUp(self.led, self.r)
        rows = self.m.town_rows(PT, "Britain")
        self.story = next(s for s, _a, _h in rows)

    def tearDown(self):
        self.led.db.close()
        self.led.rdb.close()
        self.dir.cleanup()

    def ask(self, **over):
        return {"id": self.story["id"], "source": "made_up", "pt": PT, "town": "Britain", **over}

    def test_a_live_made_up_story_opens_with_its_stored_words(self):
        text = exchange.made_up_opening(self.ask(), self.m)
        core = " ".join(self.story["text"].split()).rstrip(".!? ").lower()
        self.assertIn(core[:30], text.lower())

    def test_everything_else_is_refused(self):
        bad = [self.ask(source="deed"), self.ask(source=None), self.ask(id="mu-ffffffff-abcd-1"),     # forged
               self.ask(id="1-3"), self.ask(id="mo-" + self.story["key"]),                           # real deed / one-off
               self.ask(pt=self.story["expires_pt"]), self.ask(pt=True), self.ask(town="")]          # stale / malformed
        for raw in bad:
            with self.assertRaises(exchange.Refused, msg=raw):
                exchange.made_up_opening(raw, self.m)
        with self.assertRaises(exchange.Refused):
            exchange.made_up_opening(self.ask(), None)                                              # flow off

    def test_a_pinned_one_off_line_is_refused(self):
        line = self.m.one_off(PT, "Britain", random.Random(1))
        self.assertIsNotNone(line)
        self.assertTrue(self.m.pin_one_off(line, PT))
        with self.assertRaises(exchange.Refused):
            exchange.made_up_opening(self.ask(id=line["id"]), self.m)


if __name__ == "__main__":
    unittest.main()
