"""Errands build 1, stage 5: the picker and the current proof (errands.py; design v5 3.1, 3.3, 3.5, 3.7). Two real towns
from the real builder, the real pass, no model. Run: python3 test_errands_pick.py"""
import copy
import json
import os
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import backgrounds  # noqa: E402
import errands  # noqa: E402
import errands_people as ep  # noqa: E402
import errands_text as et  # noqa: E402
import memory  # noqa: E402
import people_store as store  # noqa: E402
import townroster  # noqa: E402
from test_people_store import folk, roster_doc, session  # noqa: E402

LISTS = et.load()


class Base(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.dir = tempfile.TemporaryDirectory()
        cls.led = memory.Ledger(os.path.join(cls.dir.name, "m.db"))
        backgrounds.ensure_table(cls.led)
        store.ensure_tables(cls.led)
        cls.towns = [{"map": "Felucca", "town": "Britain", "people": folk(80)},
                     {"map": "Felucca", "town": "Vesper", "people": folk(80, start=200)}]
        cls.roster = townroster.Roster(cls.led)
        cls.roster.accept(roster_doc(cls.towns))
        cls.held = cls.roster.held()
        for town in ("Britain", "Vesper"):
            assert store.build_town(cls.led, cls.held, "Felucca", town, epoch=session(cls.led)) == "built"
        out = ep.run_pass(cls.led, cls.held, 5, target_pairs=20)
        cls.pins = {"errand_cousin_pin": out["cousin"]["fingerprint"],
                    "errand_contact_pin": out["contact"]["fingerprint"], "errand_tables": True}
        cls.tables = ep.state(cls.led, cls.pins)
        assert ep.usable(cls.tables, "cousin") and ep.usable(cls.tables, "contact"), cls.tables
        cls.views = {p: store.view(cls.led, p) for p in cls.held.by_id}

    @classmethod
    def tearDownClass(cls):
        cls.dir.cleanup()

    def world(self, held="same", tables=None, **kw):
        return errands.World(self.led, self.held if held == "same" else held, tables or self.tables, LISTS,
                             neighbours=kw.pop("neighbours", lambda a, b: {a, b} == {"Britain", "Vesper"}), **kw)

    def held_with(self, change):
        """A new town list: `change(person, town)` may alter a person dict or return None to drop them."""
        towns = copy.deepcopy(self.towns)
        for t in towns:
            t["people"] = [p for p in (change(p, t) for p in t["people"]) if p is not None]
        r = townroster.Roster(None)
        ok, err = r.accept(roster_doc(towns))
        assert ok, err
        return r.held()

    def sender_with(self, kind, route):
        w = self.world()
        for pid in self.held.by_id:
            hit = next((c for c in errands.candidates(w, pid) if c[0] == kind and c[1] == route), None)
            if hit:
                return pid, hit
        self.fail(f"the sample towns hold nobody who could ask {kind} by {route}")

    def req(self, step, sender, c, **more):
        return dict({"step": step, "kind": c[0], "route": c[1], "sender": sender, "receiver": c[2], "about": c[3],
                     "line_key": more.pop("line_key", "m01" if c[0] == "word" else None), "seed": "t1"}, **more)


class Picker(Base):
    def test_each_route_is_found_and_obeys_its_rule(self):
        for kind, route in sorted(et.KIND_ROUTES):
            sender, c = self.sender_with(kind, route)
            s, r = self.held.where(sender), self.held.where(c[2])
            self.assertNotEqual(sender, c[2])
            self.assertEqual(r[2]["kind"], "resident")
            if route == "kin":
                self.assertEqual((s[0], s[1]), (r[0], r[1]))
            else:
                self.assertEqual(s[0], r[0])
                self.assertNotEqual(s[1], r[1])
            if route == "contact":
                self.assertEqual(s[2]["role"].lower(), r[2]["role"].lower())
            if route == "standin":
                self.assertIsNotNone(c[3])
                self.assertEqual(errands._trade_key(r[2]["role"]), "innkeeper")
                innkeepers = sorted(p["id"] for p in self.held.people(r[0], r[1]) if p["role"] == "innkeeper")
                self.assertEqual(c[2], innkeepers[0])

    def test_a_round_trip_is_never_offered_through_a_stand_in(self):
        for pid in self.held.by_id:
            for kind, route, _, _ in errands.candidates(self.world(), pid):
                self.assertIn((kind, route), et.KIND_ROUTES)
                self.assertFalse(kind == "ask_after" and route == "standin")

    def test_introduction_guards(self):
        sender, c = self.sender_with("introduce", "contact")
        w = self.world()
        self.assertIn(c, errands.candidates(w, sender))
        self.assertNotIn(c, errands.candidates(w, sender, intro_offered=True))
        self.assertNotIn(c, errands.candidates(w, sender, intro_blocked=[c[2]]))
        self.assertIn(c, errands.candidates(w, sender, intro_blocked=["someone-else"]))

    def test_tables_switched_off_or_unavailable_give_no_cousin_and_no_contact(self):
        off = ep.state(self.led, dict(self.pins, errand_tables=False))
        wrong = ep.state(self.led, dict(self.pins, errand_cousin_pin="0" * 64, errand_contact_pin="0" * 64))
        for tables in (off, wrong, ep.state(self.led, {})):
            for pid in list(self.held.by_id)[:80]:
                for kind, route, _, _ in errands.candidates(self.world(tables=tables), pid):
                    self.assertIn(route, ("kin", "standin"))

    def test_no_usable_town_list_no_candidates(self):
        sender, _ = self.sender_with("word", "kin")
        self.assertEqual(errands.candidates(self.world(held=None), sender), [])
        self.assertIsNone(errands.pick(self.world(held=None), sender, "t1"))

    def test_the_pick_is_fixed_by_its_seed_and_its_line_fits(self):
        sender, _ = self.sender_with("word", "cousin")
        w = self.world()
        picks = [errands.pick(w, sender, f"t{i}") for i in range(40)]
        self.assertEqual(picks[3], errands.pick(w, sender, "t3"))
        self.assertGreater(len({json.dumps(p, sort_keys=True) for p in picks}), 3)
        for p in picks:
            self.assertEqual(p["line_key"] is not None, p["kind"] == "word")
            proof = errands.prove(w, dict(p, step="ask", sender=sender, seed="x"))
            self.assertIsNotNone(proof, p)

    def test_a_placed_or_named_person_neither_asks_nor_receives(self):
        sender, c = self.sender_with("word", "kin")
        for who in (sender, c[2]):
            held = self.held_with(lambda p, t: dict(p, kind="placed") if p["id"] == who else p)
            self.assertNotIn(c, errands.candidates(self.world(held=held), sender))
            self.assertIsNone(errands.prove(self.world(held=held), self.req("ask", sender, c)))


class Proof(Base):
    def test_ask_lines_name_the_current_person_trade_and_town(self):
        for kind, route in sorted(et.KIND_ROUTES):
            sender, c = self.sender_with(kind, route)
            p = errands.prove(self.world(), self.req("ask", sender, c))
            self.assertIsNotNone(p, (kind, route))
            r = self.held.where(c[2])
            for part in (r[2]["name"], et.a_trade(r[2]["role"])):
                self.assertIn(part, p["ask_line"], (kind, route))
            self.assertLessEqual(len(p["ask_line"]), 300)
            self.assertTrue(p["yes_line"] and p["no_line"])
            self.assertEqual((p["receiver_map"], p["receiver_town"]), (r[0], r[1]))
            self.assertEqual(p["band"], {"kin": "same", "contact": "none"}.get(route, "near"))
            if route == "standin":
                off = next(o for rel, k, o in self.views[sender]["kin"] if k == "off" and o["id"] == c[3])
                self.assertIn(off["name"], p["ask_line"])
                self.assertIn(off["place_town"], p["ask_line"])

    def test_every_step_gives_the_same_digest_and_only_its_own_sentences(self):
        sender, c = self.sender_with("ask_after", "cousin")
        w = self.world()
        ask, acc, dlv = (errands.prove(w, self.req(s, sender, c)) for s in ("ask", "accept", "deliver"))
        self.assertEqual(len({ask["digest"], acc["digest"], dlv["digest"]}), 1)
        self.assertEqual(ask["data_gen"], errands.data_generation(self.tables, LISTS))
        self.assertNotIn("ask_line", acc)
        self.assertIn("yes_line", acc)
        self.assertEqual(set(dlv["answer"]), {"key", "hash", "digest"})
        self.assertIn(self.held.where(c[2])[1], dlv["line"])            # the answer is heard: trade and town named
        ret = errands.prove(w, self.req("return", sender, c, answer=dlv["answer"]))
        self.assertEqual((ret["digest"], ret["answer_changed"]), (ask["digest"], False))
        self.assertIn(self.held.where(c[2])[2]["name"], ret["line"])

    def test_a_far_band_when_the_towns_do_not_neighbour(self):
        sender, c = self.sender_with("word", "cousin")
        p = errands.prove(self.world(neighbours=lambda a, b: False), self.req("ask", sender, c))
        self.assertEqual(p["band"], "far")

    def test_a_changed_trade_keeps_the_digest_and_turns_the_answer_into_thanks(self):
        sender, c = self.sender_with("ask_after", "cousin")
        dlv = errands.prove(self.world(), self.req("deliver", sender, c))
        moved = self.held_with(lambda p, t: dict(p, role="glassblower") if p["id"] == c[2] else p)
        ret = errands.prove(self.world(held=moved), self.req("return", sender, c, answer=dlv["answer"]))
        self.assertEqual((ret["digest"], ret["answer_changed"]), (dlv["digest"], True))
        self.assertIn(ret["line"], [et.fill(r["text"], {}) for r in LISTS["thanks"]])
        self.assertNotIn("glassblower", ret["line"])
        self.assertNotIn(self.held.where(c[2])[2]["role"], ret["line"])

    def test_an_edited_answer_text_under_the_same_key_is_seen(self):
        sender, c = self.sender_with("ask_after", "kin")
        dlv = errands.prove(self.world(), self.req("deliver", sender, c))
        lists = copy.deepcopy(LISTS)
        next(a for a in lists["answer"] if a["key"] == dlv["answer"]["key"])["said"] += " indeed"
        w = errands.World(self.led, self.held, self.tables, lists)
        ret = errands.prove(w, self.req("return", sender, c, answer=dlv["answer"]))
        self.assertTrue(ret["answer_changed"])

    def test_a_malformed_or_foreign_answer_is_a_refusal_not_a_thanks(self):
        sender, c = self.sender_with("ask_after", "kin")
        good = errands.prove(self.world(), self.req("deliver", sender, c))["answer"]
        for bad in (None, {}, dict(good, key="m01"), dict(good, key="a99"), dict(good, hash="abc"),
                    dict(good, digest=None), dict(good, digest=12)):
            self.assertIsNone(errands.prove(self.world(), self.req("return", sender, c, answer=bad)), bad)
        word_sender, wc = self.sender_with("word", "kin")
        self.assertIsNone(errands.prove(self.world(), self.req("return", word_sender, wc, answer=good)))

    def test_a_changed_name_changes_the_digest(self):
        sender, c = self.sender_with("word", "cousin")
        before = errands.prove(self.world(), self.req("accept", sender, c))["digest"]
        renamed = self.held_with(lambda p, t: dict(p, name="Zebedee") if p["id"] == c[2] else p)
        self.assertNotEqual(before, errands.prove(self.world(held=renamed), self.req("accept", sender, c))["digest"])

    def test_refusals(self):
        sender, kin = self.sender_with("word", "kin")
        _, cousin = self.sender_with("word", "cousin")
        csender = self.sender_with("word", "cousin")[0]
        ssender, stand = self.sender_with("word", "standin")
        w = self.world()
        gone = self.held_with(lambda p, t: None if p["id"] == kin[2] else p)
        cases = [
            (w, self.req("offer", sender, kin)),
            (w, self.req("ask", sender, kin, line_key="m99")),
            (w, self.req("ask", sender, kin, line_key=None)),
            (w, self.req("ask", csender, cousin, line_key="m11")),                 # a close-family line for a cousin
            (w, self.req("ask", sender, ("ask_after", "kin", kin[2], None), line_key="m01")),
            (w, self.req("ask", sender, ("ask_after", "standin", stand[2], stand[3]))),
            (w, self.req("ask", sender, ("word", "kin", sender, None))),
            (w, self.req("ask", sender, ("word", "cousin", kin[2], None))),          # not this sender's cousin
            (w, self.req("ask", csender, ("word", "kin", cousin[2], None))),         # a cousin is not a stored tie
            (w, self.req("ask", ssender, ("word", "standin", kin[2], stand[3]))),    # not the town's stand-in
            (w, self.req("ask", ssender, ("word", "standin", stand[2], "o-nobody"))),
            (self.world(held=gone), self.req("ask", sender, kin)),
            (self.world(held=None), self.req("ask", sender, kin)),
            (self.world(tables=ep.state(self.led, dict(self.pins, errand_tables=False))), self.req("ask", csender, cousin)),
        ]
        for world, req in cases:
            self.assertIsNone(errands.prove(world, req), req)
        self.assertIsNone(errands.prove(w, self.req("ask", sender, kin), final_check=lambda text: False))
        self.assertIsNotNone(errands.prove(w, self.req("ask", sender, kin), final_check=lambda text: True))

    def test_stand_in_only_while_the_sender_is_at_home_and_the_name_is_free(self):
        sender, c = self.sender_with("word", "standin")
        off = next(o for rel, k, o in self.views[sender]["kin"] if k == "off" and o["id"] == c[3])
        self.assertIsNone(errands.prove(self.world(avoid=[off["name"]]), self.req("ask", sender, c)))
        shares = self.held_with(lambda p, t: dict(p, name=off["name"]) if p["id"] == c[2] else p)
        self.assertIsNone(errands.prove(self.world(held=shares), self.req("ask", sender, c)))
        p = errands.prove(self.world(), self.req("deliver", sender, c))
        self.assertIn("pass", p["line"])                                # a stand-in says only that they will pass it on
        self.assertNotIn("answer", p)
        dead = dict(off, alive=False)
        with mock.patch.object(store, "view", return_value=dict(self.views[sender], kin=[("parent", "off", dead)])):
            self.assertIsNone(errands.prove(self.world(), self.req("ask", sender, c)))

    def test_a_contact_no_longer_of_the_trade_is_simply_not_used(self):
        sender, c = self.sender_with("introduce", "contact")
        changed = self.held_with(lambda p, t: dict(p, role="glassblower") if p["id"] == c[2] else p)
        self.assertIsNone(errands.prove(self.world(held=changed), self.req("deliver", sender, c)))
        self.assertNotIn(c, errands.candidates(self.world(held=changed), sender))
        self.assertIsNotNone(errands.prove(self.world(), self.req("deliver", sender, c)))


class Readiness(Base):
    def test_may_ask_needs_two_days_and_regard_not_cold(self):
        ok = {"familiarity": 2, "warmth": 0.0, "trust": 0.0}
        self.assertTrue(errands.may_ask(ok))
        for bad in (None, {}, dict(ok, familiarity=1), dict(ok, warmth=-90.0), dict(ok, trust=-90.0)):
            self.assertFalse(errands.may_ask(bad), bad)

    def test_readiness_and_the_data_generation(self):
        r = errands.readiness(self.tables, LISTS, self.held.hash, self.held.hash)
        self.assertEqual((r["errands"], r["boot"], r["stamp_ok"]), (1, errands.BOOT_ID, True))
        self.assertFalse(errands.readiness(self.tables, LISTS, self.held.hash, "none")["stamp_ok"])
        self.assertFalse(errands.readiness(self.tables, LISTS, None, None)["stamp_ok"])
        gen = r["data_gen"]
        with mock.patch.object(errands, "BOOT_ID", "another-start"):
            self.assertNotEqual(gen, errands.data_generation(self.tables, LISTS))
        self.assertNotEqual(gen, errands.data_generation(self.tables, dict(LISTS, lists_version=LISTS["lists_version"] + 1)))
        other = copy.deepcopy(self.tables)
        other["contact"]["fingerprint"] = "0" * 64
        self.assertNotEqual(gen, errands.data_generation(other, LISTS))


if __name__ == "__main__":
    unittest.main()
