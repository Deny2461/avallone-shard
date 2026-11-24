"""Errands build 1, stage 6: what a completed errand is worth in regard (dials.py; design v5 3.10) - warmth for sender
and receiver sized like a modest gift, the introduction's head start only from a flagged event, personal trust and the
town's goodwill, each bounded and windowed. Real ledger, no model. Run: python3 test_errands_standing.py"""
import os
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dials  # noqa: E402
import memory  # noqa: E402
from test_errands_ledger import C, R, S, done  # noqa: E402

OTHER = "33333333-3333-4333-8333-333333333333"
HOUR = 3600


def errand(seq, n, memory_s=0, played=0, **facts):
    return done(8, seq, facts=dict({"errand": n, "memory": memory_s, "played": played}, **facts))


class Base(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.led.reconcile(8, 7, 0, None)
        self.cfg = dials.cfg()
        self.e, self.g = self.cfg["errands"], self.cfg["warmth"]["gift"]
        self.one = self.g["base"] * self.e["warmth_mult"]

    def tearDown(self):
        self.dir.cleanup()

    def req(self, npc, town="Minoc", pt=0, mpt=0, map_="Felucca"):
        st = {"karma": 0, "fame": 0, "hour": 12, "town": town, "pt": pt, "mpt": mpt, "epoch": 8}
        if map_:
            st["map"] = map_
        return {"npc_id": npc, "subject_id": C, "persona_vars": {"role": "baker"}, "standing": st}


class Warmth(Base):
    def test_nothing_recorded_changes_nothing(self):
        self.assertEqual(dials.errand_warmth(self.led, S, C, 0), 0.0)
        self.assertEqual(dials.errand_trust(self.led, S, C, "Minoc", 0, "Felucca"), (0.0, 0.0))
        d = dials.compute(self.req(S), self.led, None)
        self.assertNotIn("errands", d["sources"])

    def test_sender_and_receiver_both_feel_it_and_a_bystander_does_not(self):
        self.led.ingest([errand(1, 1)])
        self.assertAlmostEqual(dials.errand_warmth(self.led, S, C, 0), self.one)
        self.assertAlmostEqual(dials.errand_warmth(self.led, R, C, 0), self.one)
        self.assertEqual(dials.errand_warmth(self.led, OTHER, C, 0), 0.0)
        self.assertEqual(dials.errand_warmth(self.led, S, "c:someone-else", 0), 0.0)
        base = dials.warmth(self.led, OTHER, C, 0, 5.0)
        with_it = dials.warmth(self.led, S, C, 0, 5.0)
        self.assertAlmostEqual(with_it[1] - base[1], self.one)

    def test_it_is_a_modest_gift_and_shrinks_with_repeats_and_fades(self):
        self.assertLess(self.one, self.g["base"] * self.g["liked"])
        self.led.ingest([errand(1, 1), errand(2, 2), errand(3, 3)])
        self.assertAlmostEqual(dials.errand_warmth(self.led, S, C, 0), self.one * (1 + 1 / 2 + 1 / 3))
        half = self.cfg["warmth"]["half_life_mh"] * HOUR
        self.assertAlmostEqual(dials.errand_warmth(self.led, S, C, half), self.one * (1 + 1 / 2 + 1 / 3) / 2)

    def test_once_per_errand_id(self):
        self.led.ingest([errand(1, 1), errand(2, 1)])                 # the same errand id recorded twice
        self.assertAlmostEqual(dials.errand_warmth(self.led, S, C, 0), self.one)

    def test_the_head_start_comes_only_from_a_flagged_event_and_only_to_the_receiver(self):
        intro = dict(kind="introduce", route="contact")
        self.led.ingest([errand(1, 1, **intro)])
        self.assertAlmostEqual(dials.errand_warmth(self.led, R, C, 0), self.one)          # first ever, but no flag
        self.led.ingest([errand(2, 2, memory_s=400 * HOUR, head_start=True, **intro)])
        now = 400 * HOUR
        fade = 0.5 ** (400 / self.cfg["warmth"]["half_life_mh"])
        self.assertAlmostEqual(dials.errand_warmth(self.led, R, C, now), self.one * fade + self.one + self.e["head_start"])
        self.assertAlmostEqual(dials.errand_warmth(self.led, S, C, now), self.one * fade + self.one)

    def test_set_aside_rolled_back_and_deleted_give_nothing(self):
        self.led.ingest([errand(1, 1), done(8, 2, facts={"v": 2, "errand": 2})])
        self.assertAlmostEqual(dials.errand_warmth(self.led, S, C, 0), self.one)           # the set-aside one is left out
        self.led.reconcile(9, 8, 0, None)
        self.assertEqual(dials.errand_warmth(self.led, S, C, 0), 0.0)

    def test_explained_for_the_admin_view(self):
        self.led.ingest([errand(1, 1)])
        items = []
        dials.warmth(self.led, S, C, 0, 5.0, items)
        self.assertEqual([i["kind"] for i in items], ["errand"])


class Trust(Base):
    def test_personal_trust_is_per_errand_and_capped(self):
        each, cap = self.e["trust_each"], self.e["trust_cap"]
        for n in range(1, 6):
            self.led.ingest([errand(n, n, sender_town="Elsewhere", receiver_town="Elsewhere")])
            own, town = dials.errand_trust(self.led, S, C, "Minoc", 0, "Felucca")
            self.assertEqual((own, town), (min(cap, each * n), 0.0))

    def test_the_towns_goodwill_counts_either_end_once_per_errand_and_is_capped(self):
        each, cap = self.e["town_each"], self.e["town_cap"]
        self.led.ingest([errand(1, 1)])                               # Felucca Minoc -> Felucca Vesper
        self.assertEqual(dials.errand_trust(self.led, OTHER, C, "Minoc", 0, "Felucca"), (0.0, each))
        self.assertEqual(dials.errand_trust(self.led, OTHER, C, "vesper", 0, "felucca"), (0.0, each))
        self.assertEqual(dials.errand_trust(self.led, OTHER, C, "Yew", 0, "Felucca"), (0.0, 0.0))
        self.assertEqual(dials.errand_trust(self.led, OTHER, C, "", 0, "Felucca"), (0.0, 0.0))
        self.led.ingest([errand(2, 1)] + [errand(n, n, receiver_town="Minoc") for n in range(3, 10)])
        self.assertEqual(dials.errand_trust(self.led, OTHER, C, "Minoc", 0, "Felucca")[1], cap)

    def test_a_town_is_its_map_and_its_name_together(self):
        """Codex Code R1 C3: an errand in Felucca's Minoc earns nothing with a townsperson of Trammel's Minoc."""
        each = self.e["town_each"]
        self.led.ingest([errand(1, 1)])                               # Felucca Minoc -> Felucca Vesper
        self.assertEqual(dials.errand_trust(self.led, OTHER, C, "Minoc", 0, "Trammel"), (0.0, 0.0))
        self.assertEqual(dials.errand_trust(self.led, OTHER, C, "Vesper", 0, "Trammel"), (0.0, 0.0))
        self.assertEqual(dials.errand_trust(self.led, OTHER, C, "Minoc", 0, "Felucca"), (0.0, each))
        self.assertEqual(dials.errand_trust(self.led, OTHER, C, "Minoc", 0), (0.0, 0.0))          # no map known: nothing
        self.assertEqual(dials.errand_trust(self.led, OTHER, C, "Minoc", 0, ""), (0.0, 0.0))
        # personal trust never depended on the town: the sender feels it whatever map is given, or none
        self.assertEqual(dials.errand_trust(self.led, S, C, "Minoc", 0, "Trammel"), (self.e["trust_each"], 0.0))
        self.assertEqual(dials.errand_trust(self.led, S, C, "Minoc", 0)[0], self.e["trust_each"])
        wrong = dials.compute(self.req(OTHER, map_="Trammel"), self.led, None)
        right = dials.compute(self.req(OTHER, map_="Felucca"), self.led, None)
        none = dials.compute(self.req(OTHER, map_=None), self.led, None)
        self.assertNotIn("errands", wrong["sources"])
        self.assertNotIn("errands", none["sources"])
        self.assertEqual(right["sources"]["errands"], {"own": 0.0, "town": each})
        self.assertAlmostEqual(right["trust"] - wrong["trust"], each)

    def test_both_ends_in_one_town_count_once(self):
        self.led.ingest([errand(1, 1, receiver_town="Minoc")])        # sender and receiver both in Felucca Minoc
        self.assertEqual(dials.errand_trust(self.led, OTHER, C, "Minoc", 0, "Felucca")[1], self.e["town_each"])

    def test_only_the_last_seven_played_days_count(self):
        week = self.e["window_played_h"] * HOUR
        self.led.ingest([errand(1, 1, played=1000)])
        self.assertEqual(dials.errand_trust(self.led, S, C, "Minoc", 1000 + week, "Felucca"),
                         (self.e["trust_each"], self.e["town_each"]))
        self.assertEqual(dials.errand_trust(self.led, S, C, "Minoc", 1001 + week, "Felucca"), (0.0, 0.0))
        self.assertEqual(dials.errand_trust(self.led, S, C, "Minoc", 999, "Felucca"), (0.0, 0.0))     # a clock that went back

    def test_the_dial_moves_by_exactly_the_two_terms_and_says_so(self):
        before = dials.compute(self.req(S), self.led, None)
        self.led.ingest([errand(1, 1)])
        after = dials.compute(self.req(S), self.led, None)
        self.assertEqual(after["sources"]["errands"], {"own": self.e["trust_each"], "town": self.e["town_each"]})
        self.assertAlmostEqual(after["trust"] - before["trust"], self.e["trust_each"] + self.e["town_each"])
        self.assertAlmostEqual(after["warmth"] - before["warmth"], self.one)
        self.assertEqual(after["respect"], before["respect"])
        self.assertEqual(after["familiarity"], before["familiarity"])             # a head start adds no visit days
        bystander = dials.compute(self.req(OTHER, town="Vesper"), self.led, None)
        self.assertEqual(bystander["sources"]["errands"], {"own": 0.0, "town": self.e["town_each"]})

    def test_what_one_townsperson_can_feel_is_bounded(self):
        self.led.ingest([errand(n, n) for n in range(1, 40)])
        before = dials.compute(self.req(OTHER, town="Yew"), self.led, None)["trust"]
        after = dials.compute(self.req(S), self.led, None)["trust"]
        self.assertLessEqual(after - before, self.e["trust_cap"] + self.e["town_cap"] + 60)   # leans differ per NPC
        self.assertEqual(dials.errand_trust(self.led, S, C, "Minoc", 0, "Felucca"), (self.e["trust_cap"], self.e["town_cap"]))

    def test_no_errands_section_no_effect(self):
        self.led.ingest([errand(1, 1)])
        bare = {k: v for k, v in self.cfg.items() if k != "errands"}
        with mock.patch.object(dials, "cfg", return_value=bare):
            self.assertEqual(dials.errand_warmth(self.led, S, C, 0), 0.0)
            self.assertEqual(dials.errand_trust(self.led, S, C, "Minoc", 0, "Felucca"), (0.0, 0.0))

    def test_a_ledger_without_the_reader_is_left_alone(self):
        class Old:
            pass
        self.assertEqual(dials.errand_rows(Old(), S, C), [])
        self.assertEqual(dials.errand_trust(Old(), S, C, "Minoc", 0, "Felucca"), (0.0, 0.0))


if __name__ == "__main__":
    unittest.main()
