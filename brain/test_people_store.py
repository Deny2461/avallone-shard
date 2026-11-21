"""Townsfolk step, Stage 2 - people_store.py (design §6.1, §6.5, §6.6, §8.1). Run: python3 -m unittest test_people_store"""
import hashlib
import json
import backgrounds
import re
import os
import tempfile
import unittest
from unittest import mock

import memory
import people
import people_store as store
import townroster


def pid(i):
    return f"{i:08d}-3333-4333-8333-333333333333"


def roster_doc(towns):
    content = json.dumps({"towns": towns}, separators=(",", ":"))
    return {"epoch": 1, "rev": 1, "content": content, "hash": hashlib.sha256(content.encode()).hexdigest()}


def folk(n, start=0):
    roles = ["tailor", "blacksmith", "baker", "healer", "innkeeper", "mage", "cobbler", "weaver", "banker", "fisher"]
    m, w = people.data()["names"]["man"], people.data()["names"]["woman"]
    return [{"id": pid(i), "name": (m if i % 2 else w)[(i * 7) % 900], "role": roles[i % 10], "sex": "man" if i % 2 else "woman",
             "kind": "resident", "home": {"x": 1400 + i, "y": 1600}} for i in range(start, start + n)]



def session(led):
    """Repair B3: a real memory reconciliation on the test ledger (an empty one reconciles as fresh); its epoch."""
    if not led.reconciled(1):
        assert led.reconcile(1, 0, 0)[0] in ("ok", "fresh")
    return 1

class Store(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        import backgrounds
        backgrounds.ensure_table(self.led)
        store.ensure_tables(self.led)
        self.roster = townroster.Roster(self.led)
        self.towns = [{"map": "Felucca", "town": "Britain", "people": folk(60)},
                      {"map": "Felucca", "town": "Trinsic", "people": folk(20, 100)}]
        ok, err = self.roster.accept(roster_doc(self.towns))
        self.assertTrue(ok, err)
        self.held = self.roster.held()

    def tearDown(self):
        self.dir.cleanup()

    def count(self, table):
        return self.led.db.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0]

    def test_a_town_is_built_once(self):
        self.assertEqual(store.build_town(self.led, self.held, "Felucca", "Britain", epoch=session(self.led)), "built")
        n = (self.count("people_person"), self.count("people_offstage"), self.count("people_tie"), self.count("npc_backgrounds"))
        self.assertEqual(n[0], 60); self.assertEqual(n[3], 60); self.assertGreater(n[1], 100)
        h = self.count("people_household"); self.assertGreater(h, 0)
        self.assertEqual(store.build_town(self.led, self.held, "Felucca", "Britain", epoch=session(self.led)), "already")
        self.assertEqual(self.count("people_household"), h)
        self.assertEqual(n, (self.count("people_person"), self.count("people_offstage"), self.count("people_tie"), self.count("npc_backgrounds")))
        self.assertEqual(store.build_all(self.led, self.held, epoch=session(self.led)), {"already": 1, "built": 1})
        self.assertEqual(self.count("people_town"), 2)

    def test_a_failed_audit_writes_nothing_not_even_backgrounds(self):
        real = people.audit
        people.audit = lambda res: [("made-up-violation", "x")]
        try:
            self.assertEqual(store.build_town(self.led, self.held, "Felucca", "Britain", epoch=session(self.led)), "audit-failed")
        finally:
            people.audit = real
        for t in ("people_person", "people_offstage", "people_tie", "people_household", "people_town", "npc_backgrounds"):
            self.assertEqual(self.count(t), 0, t)

    def test_a_person_with_a_row_is_never_built_again(self):
        store.build_town(self.led, self.held, "Felucca", "Britain", epoch=session(self.led))
        before = self.led.db.execute("SELECT record FROM people_person WHERE person_id = ?", (pid(3),)).fetchone()[0]
        moved = [{"map": "Felucca", "town": "Britain", "people": [p for p in self.towns[0]["people"] if p["id"] != pid(3)]},
                 {"map": "Felucca", "town": "Trinsic", "people": self.towns[1]["people"] + [p for p in self.towns[0]["people"] if p["id"] == pid(3)]}]
        self.roster.accept(roster_doc(moved))
        self.assertEqual(store.build_town(self.led, self.roster.held(), "Felucca", "Trinsic", epoch=session(self.led)), "built")
        after = self.led.db.execute("SELECT record, town FROM people_person WHERE person_id = ?", (pid(3),)).fetchone()
        self.assertEqual(before, after[0]); self.assertEqual(after[1], "Britain")
        self.assertEqual(self.count("people_person"), 80)

    def test_an_existing_background_is_kept(self):
        import backgrounds
        mine = backgrounds.assign(self.led, pid(5), "Britain", "mage", "")
        store.build_town(self.led, self.held, "Felucca", "Britain", epoch=session(self.led))
        v = store.view(self.led, pid(5))
        self.assertEqual({k: v["record"]["bg"][k] for k in ("home", "life", "event")}, {k: mine[k] for k in ("home", "life", "event")})


class Block(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.dir = tempfile.TemporaryDirectory()
        cls.led = memory.Ledger(os.path.join(cls.dir.name, "m.db"))
        import backgrounds
        backgrounds.ensure_table(cls.led)
        store.ensure_tables(cls.led)
        cls.roster = townroster.Roster(cls.led)
        cls.roster.accept(roster_doc([{"map": "Felucca", "town": "Britain", "people": folk(120)}]))
        cls.held = cls.roster.held()
        assert store.build_town(cls.led, cls.held, "Felucca", "Britain", epoch=session(cls.led)) == "built"
        cls.views = {pid(i): store.view(cls.led, pid(i)) for i in range(120)}

    @classmethod
    def tearDownClass(cls):
        cls.dir.cleanup()

    def find(self, want):
        for i, v in self.views.items():
            if want(v):
                return i, v
        self.fail("the sample town holds no such person")

    def test_nothing_stored_nothing_said_and_control_says_nothing(self):
        self.assertEqual(store.block(None, "Britain"), "")
        _, v = self.find(lambda v: True)
        self.assertEqual(store.block(v, "Britain", arrangement="off"), "")

    def test_the_block_states_the_stored_age_and_only_given_numbers(self):
        i, v = self.find(lambda v: v["record"]["years_town_kind"] == "number" and v["record"]["years_trade"] is not None)
        b = store.block(v, "Britain", where=self.held.where)
        r = v["record"]
        self.assertIn(f"You are {r['age']} years old.", b)
        self.assertIn(f"lived here in Britain for {r['years_town']} years", b)
        self.assertIn(f"worked as {store._a(r['role'])} for {r['years_trade']} years", b)
        self.assertIn(store.RULES, b); self.assertIn(store.NOW_AND_THEN, b); self.assertNotIn(store.HOLD_BACK, b)

    def test_a_real_relative_is_named_from_the_roster_or_not_at_all(self):
        i, v = self.find(lambda v: any(kind == "real" and rel == "spouse" for rel, kind, _ in v["kin"]))
        other = next(o for rel, kind, o in v["kin"] if kind == "real" and rel == "spouse")
        name = self.held.where(other["id"])[2]["name"]
        with_roster = store.block(v, "Britain", where=self.held.where)
        self.assertIn(f"is {name}, the ", with_roster); self.assertIn("here in Britain", with_roster)
        without = store.block(v, "Britain", where=None)
        self.assertNotIn(name + ", the", without); self.assertIn("who is a townsperson", without)

    def test_where_a_same_town_relative_works_is_said_only_when_asked_for_and_computed_from_the_roster(self):
        import town_knowledge
        i, v = self.find(lambda v: any(kind == "real" for _, kind, _ in v["kin"]))
        other = next(o for _, kind, o in v["kin"] if kind == "real")
        me, them = self.held.where(i)[2], self.held.where(other["id"])[2]
        way = town_knowledge.how(them["home"][0] - me["home"][0], them["home"][1] - me["home"][1])
        he = "He" if them["sex"] == "man" else "She"
        plain = store.block(v, "Britain", where=self.held.where)
        self.assertNotIn(" works ", plain)                                         # off by default: the text is unchanged
        self.assertEqual(plain, store.block(v, "Britain", where=self.held.where, works=False))
        on = store.block(v, "Britain", where=self.held.where, works=True)
        self.assertIn(f"is {them['name']}, the {them['role']} here in Britain; {he.lower()} is {other['age']}. {he} works {way}", on)
        self.assertEqual(len(on.splitlines()), len(plain.splitlines()))            # a clause on the same line, nothing else added
        snap = {}
        store.block(v, "Britain", where=self.held.where, works=True, snap=snap)
        self.assertEqual(next(k for k in snap["kin"] if k.get("id") == other["id"])["works"], way)
        # standing in another town, or with no usable roster: no place is given for anyone
        moved = lambda x: ("Felucca", "Trinsic", self.held.where(x)[2]) if x == i else self.held.where(x)
        self.assertNotIn(" works ", store.block(v, "Trinsic", where=moved, works=True))
        self.assertNotIn(" works ", store.block(v, "Britain", where=None, works=True))
        # a relative with no home on the roster: named as before, no place
        nohome = lambda x: (self.held.where(x)[0], self.held.where(x)[1], dict(self.held.where(x)[2], home=None)) if x == other["id"] else self.held.where(x)
        b = store.block(v, "Britain", where=nohome, works=True)
        self.assertIn(f"is {them['name']}, the ", b); self.assertNotIn(f"{he} works", b)

    def kin_sets(self, v):
        """(names in this town, names elsewhere or dead) worked out from the stored view, not from the block's text."""
        near, away = [], []
        for rel, kind, o in v["kin"]:
            if kind == "real":
                near.append(self.held.where(o["id"])[2]["name"])
            elif o["alive"] and (o["place"] == "here" or (o["place"] == "town" and o.get("place_town") == "Britain")):
                near.append(o["name"])
            else:
                away.append(o["name"])
        return near, away

    def test_the_kin_in_town_line_gathers_exactly_the_relatives_placed_here_and_is_off_by_default(self):
        def mixed(v):
            near, away = self.kin_sets(v)
            return len(near) >= 2 and away and not set(near) & set(away)
        i, v = self.find(mixed)
        near, away = self.kin_sets(v)
        plain = store.block(v, "Britain", where=self.held.where)
        self.assertNotIn("kin", plain)
        self.assertEqual(plain, store.block(v, "Britain", where=self.held.where, kin_here=False))
        on = store.block(v, "Britain", where=self.held.where, kin_here=True)
        line = [l for l in on.splitlines() if l.startswith("- All your kin here in Britain")]
        self.assertEqual(len(line), 1)
        self.assertEqual(len(on.splitlines()), len(plain.splitlines()) + 1)       # one line more, nothing else changed
        self.assertEqual([l for l in on.splitlines() if l != line[0]], plain.splitlines())
        for name in near:
            self.assertRegex(line[0], rf"your \w+ {name}\b")
        for name in away:
            self.assertNotIn(name, line[0])
        self.assertEqual(line[0].split(": ", 1)[1].count("your "), len(near))       # nobody else is listed
        snap = {}
        store.block(v, "Britain", where=self.held.where, kin_here=True, snap=snap)
        self.assertEqual(len(snap["kin_here"]), len(near))

    def test_the_kin_in_town_line_says_none_when_none_and_nothing_when_it_cannot_be_sure(self):
        i, v = self.find(lambda v: not self.kin_sets(v)[0])
        self.assertIn("- You have no kin living here in Britain.", store.block(v, "Britain", where=self.held.where, kin_here=True))
        j, w = self.find(lambda v: any(kind == "real" for _, kind, _ in v["kin"]))
        other = next(o for _, kind, o in w["kin"] if kind == "real")
        # where the person stands is not known (no roster, or it disagrees with the game): nothing is said about "here"
        for b in (store.block(w, "Trinsic", where=self.held.where, kin_here=True), store.block(w, "Britain", where=None, kin_here=True)):
            self.assertNotIn("kin here", b); self.assertNotIn("no kin living", b)
        # a real relative who cannot be named this turn: the list would be short by one, so it is not written at all
        gone = lambda x: None if x == other["id"] else self.held.where(x)
        b = store.block(w, "Britain", where=gone, kin_here=True)
        self.assertIn("who is a townsperson", b); self.assertNotIn("kin here", b); self.assertNotIn("no kin living", b)

    def test_the_kin_in_town_line_follows_a_person_who_has_moved(self):
        """Reading batch 2026-11-19: a moved resident, asked about family "here", named a sister in the old town."""
        moved_to = lambda me, also=(): (lambda x: ("Felucca", "Trinsic", self.held.where(x)[2])
                                        if self.held.where(x) and (x == me or x in also) else self.held.where(x))
        for i, v in self.views.items():
            if v["record"]["status"] != "resolved":
                continue
            b = store.block(v, "Trinsic", where=moved_to(i), kin_here=True)
            there = [o["name"] for _, kind, o in v["kin"] if kind == "off" and o["alive"] and o["place"] == "town" and o.get("place_town") == "Trinsic"]
            line = [l for l in b.splitlines() if "kin here in" in l or "no kin living here in" in l]
            self.assertEqual(len(line), 1, (i, b))
            self.assertNotIn("Britain", line[0])                       # the town they stand in, never the old one
            if there:
                self.assertTrue(line[0].startswith("- All your kin here in Trinsic: "), line[0])
                self.assertEqual(line[0].split(": ", 1)[1].count("your "), len(there))
            else:
                self.assertEqual(line[0], "- You have no kin living here in Trinsic.")
        j, w = self.find(lambda v: any(kind == "real" for _, kind, _ in v["kin"]))
        mates = [o["id"] for _, kind, o in w["kin"] if kind == "real"]
        b = store.block(w, "Trinsic", where=moved_to(j, also=mates), kin_here=True)     # the family moved together
        line = [l for l in b.splitlines() if l.startswith("- All your kin here in Trinsic: ")][0]
        for m in mates:
            self.assertIn(self.held.where(m)[2]["name"], line)

    def test_the_dead_have_no_age_in_the_block(self):
        i, v = self.find(lambda v: any(kind == "off" and not o["alive"] for _, kind, o in v["kin"]))
        b = store.block(v, "Britain", where=self.held.where)
        dead = next(o for _, kind, o in v["kin"] if kind == "off" and not o["alive"])
        line = next(x for x in b.splitlines() if dead["name"] in x)
        self.assertIn("is dead:", line); self.assertIn("You do not know how old", line)
        self.assertFalse(any(ch.isdigit() for ch in line), line)

    def test_a_clashing_name_is_left_out_for_that_turn(self):
        i, v = self.find(lambda v: any(kind == "off" and o["alive"] for _, kind, o in v["kin"]))
        o = next(o for _, kind, o in v["kin"] if kind == "off" and o["alive"])
        self.assertIn(o["name"], store.block(v, "Britain", where=self.held.where))
        self.assertNotIn(o["name"], store.block(v, "Britain", where=self.held.where, avoid_names=[o["name"].upper()]))

    def test_standing_in_another_town_never_says_here(self):
        i, v = self.find(lambda v: v["record"]["years_town_kind"] == "number")
        me = v["record"]["id"]
        b = store.block(v, "Trinsic", where=lambda x: ("Felucca", "Trinsic", self.held.where(x)[2]) if x == me else self.held.where(x))
        self.assertIn("You lived in Britain for", b); self.assertNotIn("lived here in", b); self.assertNotIn("You live in ", b)

    def test_arrangement_b_adds_the_hold_back_line_only_when_told(self):
        _, v = self.find(lambda v: v["record"]["status"] == "resolved")
        self.assertNotIn(store.HOLD_BACK, store.block(v, "Britain", arrangement="B", held_back=False))
        self.assertIn(store.HOLD_BACK, store.block(v, "Britain", arrangement="B", held_back=True))
        self.assertNotIn(store.HOLD_BACK, store.block(v, "Britain", arrangement="A", held_back=True))
        share = sum(store.hold_back(pid(1), "c", t, 0.5) for t in range(400)) / 400.0
        self.assertTrue(0.4 < share < 0.6, share)

    def test_an_unresolved_person_gets_the_turn_aside_line_only(self):
        _, v = self.find(lambda v: True)
        u = {"record": dict(v["record"], status="unresolved"), "kin": []}
        self.assertEqual(store.block(u, "Britain"), store.UNRESOLVED)


class CodeR1Builds(unittest.TestCase):
    """Codex Code R1 (2026-11-19) - F1 a past already told in another town; F4 one town failing; F5 who has no record."""
    setUp, tearDown, count = Store.setUp, Store.tearDown, Store.count

    def test_f1_a_past_told_in_another_town_is_never_rewritten_as_born_here(self):
        mover = pid(103)                                       # on the roster in Trinsic ...
        self.assertIsNotNone(backgrounds.assign(self.led, mover, "Britain", "healer", ""))   # ... with a past given, the real way, in Britain
        row = self.led.db.execute("SELECT * FROM npc_backgrounds WHERE npc_id = ?", (mover,)).fetchone()
        logs = []
        self.assertEqual(store.build_town(self.led, self.held, "Felucca", "Trinsic", epoch=session(self.led),
                                          log=lambda **f: logs.append(f)), "built")
        self.assertIsNone(store.view(self.led, mover))         # no record, so nothing can say "born here in Trinsic"
        self.assertEqual(self.count("people_person"), 19)      # everyone else in the town has theirs
        self.assertEqual(self.led.db.execute("SELECT COUNT(*) FROM people_tie WHERE a = ? OR b = ?", (mover, mover)).fetchone()[0], 0)
        self.assertEqual(self.led.db.execute("SELECT COUNT(*) FROM people_household WHERE record LIKE ?", (f"%{mover}%",)).fetchone()[0], 0)
        self.assertEqual(row, self.led.db.execute("SELECT * FROM npc_backgrounds WHERE npc_id = ?", (mover,)).fetchone())
        summary = json.loads(self.led.db.execute("SELECT summary FROM people_town WHERE town = 'Trinsic'").fetchone()[0])
        self.assertEqual(summary["past_elsewhere"], [mover]); self.assertEqual(summary["residents"], 19)
        built = [f for f in logs if f.get("outcome") == "built"][0]
        self.assertEqual((built["past_elsewhere"], built["past_elsewhere_ids"]), (1, [mover]))
        self.assertIsInstance(built["ms"], int)
        self.assertIn(mover, store.coverage(self.led, self.held)["without"])     # and they are counted as without one
        # the earlier way of talking still tells the past it always told
        bg = backgrounds.assign(self.led, mover, "Trinsic", "healer", "")
        self.assertEqual(bg["town"], "Britain")

    def test_f1_a_town_name_that_differs_only_in_capitals_is_the_same_town(self):
        self.assertIsNotNone(backgrounds.assign(self.led, pid(104), "trinsic", "innkeeper", ""))
        self.assertEqual(store.build_town(self.led, self.held, "Felucca", "Trinsic", epoch=session(self.led)), "built")
        self.assertIsNotNone(store.view(self.led, pid(104)))
        self.assertEqual(self.count("people_person"), 20)

    def test_f4_one_town_failing_does_not_stop_the_others_and_a_second_pass_finishes(self):
        real = store.build_town

        def flaky(ledger, held, map_, town, **kw):
            if town == "Britain":
                raise RuntimeError("made-up failure")
            return real(ledger, held, map_, town, **kw)
        logs = []
        with mock.patch.object(store, "build_town", flaky):
            out = store.build_all(self.led, self.held, epoch=session(self.led), log=lambda **f: logs.append(f))
        self.assertEqual(out, {"error": 1, "built": 1})
        self.assertEqual(self.count("people_town"), 1); self.assertEqual(self.count("people_person"), 20)
        self.assertTrue(any(f.get("outcome") == "error" and f.get("town") == "Britain" for f in logs))
        for k in ("error", "audit-failed", "catalogue-changed", "not-reconciled"):
            self.assertIn(k, store.RETRY)
        for k in ("built", "already", "empty"):
            self.assertNotIn(k, store.RETRY)
        self.assertEqual(store.build_all(self.led, self.held, epoch=session(self.led)), {"built": 1, "already": 1})
        self.assertEqual(self.count("people_person"), 80)

    def test_f5_a_resident_who_arrives_after_the_town_was_built_is_counted_as_without_a_record(self):
        ep = session(self.led)
        self.assertEqual(store.build_all(self.led, self.held, epoch=ep), {"built": 2})
        self.assertEqual(store.coverage(self.led, self.held), {"residents": 80, "with_record": 80, "without": [], "towns": {},
                                                               "unresolved": []})
        new, passing = folk(1, 500)[0], dict(folk(1, 501)[0], kind=[k for k in townroster.KINDS if k != "resident"][0])
        towns = [dict(self.towns[0], people=self.towns[0]["people"] + [new, passing]), self.towns[1]]
        ok, err = self.roster.accept(roster_doc(towns))
        self.assertTrue(ok, err)
        held, logs = self.roster.held(), []
        before = self.led.db.execute("SELECT person_id, record FROM people_person ORDER BY person_id").fetchall()
        self.assertEqual(store.build_all(self.led, held, epoch=ep, log=lambda **f: logs.append(f)), {"already": 2})
        self.assertEqual(before, self.led.db.execute("SELECT person_id, record FROM people_person ORDER BY person_id").fetchall())
        self.assertIsNone(store.view(self.led, new["id"]))
        c = store.coverage(self.led, held)                     # 'already' for every town is not 'everyone has a record'
        self.assertEqual(c, {"residents": 81, "with_record": 80, "without": [new["id"]], "towns": {"Felucca/Britain": 1},
                             "unresolved": []})
        said = [f for f in logs if f.get("op") == "coverage"]
        self.assertEqual(len(said), 1)
        self.assertEqual((said[0]["residents"], said[0]["with_record"], said[0]["without"], said[0]["first"]), (81, 80, 1, [new["id"]]))

    def test_glf1_the_coverage_line_names_its_session_and_roster_and_counts_stored_rows_that_did_not_resolve(self):
        """Codex GoLive R1 F1: a go-live check must be able to tell WHICH work a line describes, and a row existing is
        not a record that resolved - on a pass that built nothing itself, too."""
        ep, logs = session(self.led), []
        self.assertEqual(store.build_all(self.led, self.held, epoch=ep, log=lambda **f: logs.append(f)), {"built": 2})
        said = [f for f in logs if f.get("op") == "coverage"]
        self.assertEqual(len(said), 1)
        self.assertEqual((said[0]["epoch"], said[0]["roster"]), (ep, self.held.hash[:12]))
        self.assertEqual((said[0]["unresolved"], said[0]["unresolved_first"]), (0, []))
        some = [r[0] for r in self.led.db.execute("SELECT person_id FROM people_person ORDER BY person_id LIMIT 2")]
        with self.led.lock:
            self.led.db.executemany("UPDATE people_person SET status = 'unresolved' WHERE person_id = ?", [(i,) for i in some])
        logs.clear()
        self.assertEqual(store.build_all(self.led, self.held, epoch=ep, log=lambda **f: logs.append(f)), {"already": 2})
        said = [f for f in logs if f.get("op") == "coverage"]
        self.assertEqual((said[0]["with_record"], said[0]["without"]), (80, 0))     # the rows exist ...
        self.assertEqual((said[0]["unresolved"], said[0]["unresolved_first"]), (2, some))     # ... and two did not resolve
        self.assertEqual(store.coverage(self.led, self.held)["unresolved"], some)


class CodeR1Lines(unittest.TestCase):
    """Codex Code R1 (2026-11-19) - F6 a changed trade and a moved resident's years; F7 the kin-in-town line."""
    setUpClass = classmethod(Block.setUpClass.__func__)
    tearDownClass = classmethod(Block.tearDownClass.__func__)
    find = Block.find

    def moved(self, me, to=("Felucca", "Trinsic")):
        def where(x):
            at = self.held.where(x)
            return (to[0], to[1], at[2]) if at and x == me else at
        return where


    def test_a_moved_resident_is_told_the_years_in_the_new_town_are_not_known(self):
        i, v = self.find(lambda v: v["record"]["status"] == "resolved" and v["record"]["years_town_kind"] == "number")
        line = "- Not known to you: how many years you have lived in Trinsic."
        self.assertNotIn("Not known to you: how many years you have lived in", store.block(v, "Britain", where=self.held.where))
        b = store.block(v, "Trinsic", where=self.moved(i)).splitlines()
        old = f"- You lived in Britain for {v['record']['years_town']} years."
        self.assertEqual(b.index(line), b.index(old) + 1)      # straight after the old town's years
        snap = {}
        store.block(v, "Trinsic", where=self.moved(i), snap=snap)
        self.assertEqual(snap["years_now"], {"n": None, "town": "Trinsic"})
        # where they stand is not known (no roster, or it disagrees with the game): nothing is said about a new town
        for where, town in ((None, "Trinsic"), (self.held.where, "Trinsic")):
            self.assertNotIn("lived in Trinsic", store.block(v, town, where=where))

    def test_f7_the_kin_line_keeps_a_real_relative_when_only_the_capitals_of_the_town_name_change(self):
        i, v = self.find(lambda v: any(kind == "real" for _, kind, _ in v["kin"]))

        def lower(x):
            at = self.held.where(x)
            return at and (at[0], "britain", at[2])
        b = store.block(v, "britain", where=lower, kin_here=True)
        line = [l for l in b.splitlines() if l.startswith("- All your kin here in Britain: ")]
        self.assertEqual(len(line), 1, b)
        real = [self.held.where(o["id"])[2]["name"] for _, kind, o in v["kin"] if kind == "real"]
        for name in real:
            self.assertIn(f"here in britain", b)
            self.assertRegex(line[0], rf"your \w+ {name}\b")
        self.assertEqual(line[0], [l for l in store.block(v, "Britain", where=self.held.where, kin_here=True).splitlines()
                                   if l.startswith("- All your kin here in Britain: ")][0])

    def test_f7_the_line_is_plain_and_lists_by_place_not_by_wording(self):
        for i, v in self.views.items():
            if v["record"]["status"] != "resolved":
                continue
            snap = {}
            b = store.block(v, "Britain", where=self.held.where, kin_here=True, snap=snap)
            self.assertNotIn("to be sure of it", b)
            want = []
            for rel, kind, o in v["kin"]:
                if kind == "real" or (o["alive"] and (o["place"] == "here" or (o["place"] == "town" and (o.get("place_town") or "").lower() == "britain"))):
                    want.append(o["id"])
            self.assertEqual(sorted(snap["kin_here"]), sorted(want), i)
            self.assertEqual("- You have no kin living here in Britain." in b.splitlines(), not want, i)


class MissingFacts(unittest.TestCase):
    """Repair A2 (Codex Stage 2 Method R1 F12, F8): a missing fact is one plain line for that fact only."""
    setUpClass = classmethod(Block.setUpClass.__func__)
    tearDownClass = classmethod(Block.tearDownClass.__func__)
    find = Block.find
    BANNED = ("kept count", "never counted", "give no number", "you give no", "do not mention it")

    def variant(self, **change):
        _, v = self.find(lambda v: v["record"]["status"] == "resolved")
        return dict(v, record=dict(v["record"], **change))

    def blk(self, v, town="Britain", **kw):
        kw.setdefault("where", self.held.where)
        return store.block(v, town, **kw)

    def test_no_invented_history_and_no_blanket_order_anywhere(self):
        for i, v in self.views.items():
            for town in ("Britain", "Trinsic"):
                b = store.block(v, town, where=self.held.where).lower()
                for phrase in self.BANNED:
                    self.assertNotIn(phrase, b, (i, town))
        for kind in ("unknown", "born"):
            b = self.blk(self.variant(years_town=None, years_town_kind=kind, years_trade=None, habit="years"), "Britain").lower()
            for phrase in self.BANNED:
                self.assertNotIn(phrase, b, kind)

    def test_unknown_years_in_town_is_one_line_and_the_trade_number_stays(self):
        v = self.variant(years_town=None, years_town_kind="unknown", years_trade=9)
        b = self.blk(v)
        r = v["record"]
        self.assertIn("- Not known to you: how many years you have lived in Britain.", b)
        self.assertIn(f"- You have worked as {store._a(r['role'])} for 9 years.", b)
        self.assertEqual(sum("Not known to you" in x for x in b.splitlines()), 1)

    def test_unknown_years_at_the_trade_is_one_line_and_the_town_number_stays(self):
        v = self.variant(years_town=11, years_town_kind="number", years_trade=None)
        b = self.blk(v)
        r = v["record"]
        self.assertIn("lived here in Britain for 11 years", b)
        self.assertIn(f"- Not known to you: how many years you have worked as {store._a(r['role'])}.", b)
        self.assertEqual(sum("Not known to you" in x for x in b.splitlines()), 1)

    def test_born_here_states_the_birth_and_no_number_of_years(self):
        b = self.blk(self.variant(years_town=None, years_town_kind="born", years_trade=5), "Britain")
        self.assertIn("- You were born here in Britain.", b)
        self.assertIn("- Not known to you: how many years you have lived in Britain.", b)

    def test_the_unknown_line_names_the_stored_town_never_this_town(self):
        b = self.blk(self.variant(years_town=None, years_town_kind="unknown", years_trade=5), "Trinsic")
        self.assertIn("how many years you have lived in Britain.", b); self.assertNotIn("this town", b)

    def test_a_changed_role_says_nothing_about_trade_years(self):
        b = self.blk(self.variant(years_trade=None), "Britain", role_now="some other trade entirely")
        self.assertNotIn("worked as", b)

    def test_the_years_habit_line_only_when_both_numbers_are_held(self):
        line = "When you mention how long you have lived or worked here, use only the numbers above."
        self.assertIn(line, self.blk(self.variant(habit="years", years_town=3, years_trade=4, years_town_kind="number"), "Britain"))
        for t, k, y in ((None, "unknown", 4), (3, "number", None), (None, "born", 4), (None, "unknown", None)):
            b = self.blk(self.variant(habit="years", years_town=t, years_town_kind=k, years_trade=y), "Britain")
            self.assertNotIn("use only the numbers above", b, (t, k, y))

    def test_the_general_rule_against_filling_gaps_stays(self):
        b = self.blk(self.variant(years_town=None, years_town_kind="unknown", years_trade=None), "Britain")
        self.assertIn(store.RULES, b)


class BornYearsAndBands(unittest.TestCase):
    """Decision 8 (the owner, chat, 2026-11-17): a born-here number, said as their life; the owner's bands written by code beside the
    number - 0-3 new, 4-9 a while, 10 or more a long time - only where the person stands in their own town."""
    setUpClass = classmethod(Block.setUpClass.__func__)
    tearDownClass = classmethod(Block.tearDownClass.__func__)
    find = Block.find
    variant = MissingFacts.variant

    def moved(self, me, to=("Felucca", "Trinsic")):
        def where(x):
            at = self.held.where(x)
            return (to[0], to[1], at[2]) if at and x == me else at
        return where

    def born(self, away):
        _, v = self.find(lambda v: v["record"]["status"] == "resolved")
        age = v["record"]["age"]
        return self.variant(years_town=age - away, years_town_kind="born"), age

    def test_born_here_all_their_life(self):
        v, age = self.born(0)
        b = store.block(v, "Britain", where=self.held.where)
        self.assertIn(f"- You were born here in Britain and have lived here all your life, {age} years.", b)
        self.assertIn("- Put plainly, you have been here a long time.", b)
        self.assertNotIn("Not known to you: how many years you have lived", b)

    def test_born_here_with_time_away(self):
        for away, word in ((1, "1 year"), (3, "3 years")):
            v, age = self.born(away)
            b = store.block(v, "Britain", where=self.held.where)
            self.assertIn(f"- You were born here in Britain and have lived here for {age - away} years: all your life except {word} away.", b)

    def test_born_here_away_from_home_names_the_town_and_has_no_band(self):
        v, age = self.born(0)
        b = store.block(v, "Trinsic", where=self.moved(v["record"]["id"]))
        self.assertIn(f"- You were born in Britain and lived there for {age} years.", b)
        b2 = store.block(v, "Britain", where=None)
        self.assertIn(f"- You were born in Britain and have spent {age} years of your life there.", b2)
        for x in (b, b2):
            self.assertNotIn("Put plainly", x); self.assertNotIn("all your life", x)

    def test_the_band_boundaries(self):
        for n, words in ((0, "still new"), (1, "still new"), (3, "still new"), (4, "a while"), (9, "a while"),
                         (10, "a long time"), (60, "a long time")):
            self.assertIn(words, store.band_line(n), n)

    def test_a_newcomer_gets_the_band_at_home_only(self):
        v = self.variant(years_town=2, years_town_kind="number")
        self.assertIn("- You have lived here in Britain for 2 years.\n- Put plainly, you are still new here.",
                      store.block(v, "Britain", where=self.held.where))
        b = store.block(v, "Trinsic", where=self.moved(v["record"]["id"]))
        self.assertIn("You lived in Britain for 2 years.", b); self.assertNotIn("Put plainly", b)

    def test_no_number_no_band(self):
        for kind in ("born", "unknown"):
            b = store.block(self.variant(years_town=None, years_town_kind=kind), "Britain", where=self.held.where)
            self.assertNotIn("Put plainly", b, kind)


class Snapshot(unittest.TestCase):
    """Repair C1/D3 (Codex Method R1 F3): block() fills, from the same code that writes each line, a snapshot of what the
    turn's text states - the truth a reply is scored against. Passing it never changes the text."""
    setUpClass = classmethod(Block.setUpClass.__func__)
    tearDownClass = classmethod(Block.tearDownClass.__func__)
    find = Block.find

    def both(self, v, town="Britain", **kw):
        snap = {}
        kw.setdefault("where", self.held.where)
        with_snap = store.block(v, town, snap=snap, **kw)
        self.assertEqual(with_snap, store.block(v, town, **kw))          # the text is byte-identical
        return with_snap, snap

    def test_every_person_every_standing_text_unchanged_and_snapshot_true_to_it(self):
        seen = set()
        for i, v in self.views.items():
            for town, where in (("Britain", self.held.where), ("Britain", None), ("Trinsic", self.held.where)):
                b, s = self.both(v, town, where=where)
                if v["record"]["status"] != "resolved":
                    self.assertEqual(s, {"given": True, "status": "unresolved"}); seen.add("unresolved"); continue
                self.assertIn(f"You are {s['age']} years old.", b)
                yt = s["years_town"]
                if yt["n"] is not None:
                    self.assertIn(f"{yt['n']} years", b); seen.add("yt-" + yt["kind"])
                else:
                    self.assertIn("Not known to you: how many years you have lived in", b)
                if s["years_trade"]["stated"] and s["years_trade"]["n"] is not None:
                    self.assertIn(f"for {s['years_trade']['n']} years.", b)
                for k in s["kin"]:
                    if k["name"]:
                        self.assertIn(k["name"], b); seen.add("named")
                    if k["kind"] == "off" and k.get("age") is not None:
                        self.assertIn(f", {k['age']}", b)
                    if not k["alive"]:
                        self.assertIn("is dead", b); seen.add("dead")
                for m in s["household"]["with"]:
                    if m["name"]:
                        self.assertIn(m["name"], b)
                    seen.add("household")
                self.assertEqual(s["no_children"], "- You have no children." in b)
                self.assertEqual(s["no_siblings"], "- You have no brothers or sisters." in b)
                self.assertEqual(s["household"]["alone"], "- You live alone." in b)
                if s["background"]:
                    self.assertIn("- Your past: " + s["background"], b)
        self.assertTrue({"yt-number", "yt-born", "named", "dead", "household"} <= seen, seen)

    def test_an_unresolved_person(self):
        _, v = self.find(lambda v: v["record"]["status"] == "resolved")
        v = dict(v, record=dict(v["record"], status="unresolved"))
        b, s = self.both(v)
        self.assertEqual(b, store.UNRESOLVED); self.assertEqual(s, {"given": True, "status": "unresolved"})

    def test_a_withheld_name_is_withheld_in_the_snapshot_too(self):
        i, v = self.find(lambda v: any(k == "off" and o["alive"] for _, k, o in v["kin"]))
        o = next(o for _, k, o in v["kin"] if k == "off" and o["alive"])
        b, s = self.both(v, avoid_names=[o["name"]])
        e = next(k for k in s["kin"] if k["id"] == o["id"])
        self.assertIsNone(e["name"]); self.assertTrue(e["name_withheld"]); self.assertNotIn(o["name"], b)

    def test_a_real_relative_off_the_roster_has_no_name_in_the_snapshot(self):
        i, v = self.find(lambda v: any(k == "real" for _, k, _ in v["kin"]))
        b, s = self.both(v, where=lambda x: self.held.where(x) if x == v["record"]["id"] else None)
        self.assertTrue(all(k["name"] is None for k in s["kin"] if k["kind"] == "real"))

    def test_no_marriage_said_means_no_children_said(self):
        _, v = self.find(lambda v: v["record"]["status"] == "resolved" and not any(rel == "child" for rel, _, _ in v["kin"]))
        v = dict(v, record=dict(v["record"], marital="unplaced"))
        b, s = self.both(v)
        self.assertFalse(s["no_children"]); self.assertNotIn("no children", b); self.assertIsNone(s["marital"])

    def test_a_changed_role_states_no_trade_years(self):
        _, v = self.find(lambda v: v["record"]["status"] == "resolved")
        b, s = self.both(v, role_now="some other trade entirely")
        self.assertEqual(s["years_trade"], {"n": None, "stated": False, "role_changed": True})

    def test_nothing_given_off_or_no_record(self):
        _, v = self.find(lambda v: True)
        s = {}
        self.assertEqual(store.block(v, "Britain", arrangement="off", snap=s), ""); self.assertEqual(s, {"given": False})


class Households(unittest.TestCase):
    """Repair A3 (Codex Stage 2 Method R1 F9): who lives with whom is stored and stated, and never asserted for a
    member who has moved away or cannot be seen on this turn's roster."""
    setUpClass = classmethod(Block.setUpClass.__func__)
    tearDownClass = classmethod(Block.tearDownClass.__func__)
    find = Block.find

    def shared_line(self, b):
        return [x for x in b.splitlines() if x.startswith("- You share your home") or x == "- You live alone."]

    def real_mate(self):
        return self.find(lambda v: v["house"] and any(k == "real" for k, _ in v["house"]))

    def test_every_resident_has_a_stored_household_that_includes_them(self):
        for i, v in self.views.items():
            r = v["record"]
            if r["status"] != "resolved":
                continue
            row = self.led.db.execute("SELECT record FROM people_household WHERE hid = ?", (r["household"],)).fetchone()
            self.assertIsNotNone(row, i)
            self.assertIn(r["id"], json.loads(row[0])["members"])
            self.assertIsNotNone(v["house"])

    def test_a_real_housemate_on_this_turns_roster_is_named(self):
        i, v = self.real_mate()
        mate = next(o for k, o in v["house"] if k == "real")
        name = self.held.where(mate["id"])[2]["name"]
        line = self.shared_line(store.block(v, "Britain", where=self.held.where))
        self.assertEqual(len(line), 1); self.assertIn(name, line[0])

    def test_a_household_of_one_lives_alone(self):
        i, v = self.find(lambda v: v["house"] == [])
        self.assertEqual(self.shared_line(store.block(v, "Britain", where=self.held.where)), ["- You live alone."])

    def test_an_offstage_spouse_shares_the_home_and_without_a_roster_nothing_is_said(self):
        i, v = self.find(lambda v: v["house"] and any(k == "off" for k, _ in v["house"]))
        o = next(o for k, o in v["house"] if k == "off")
        line = self.shared_line(store.block(v, "Britain", where=self.held.where))
        self.assertEqual(len(line), 1); self.assertIn(o["name"], line[0])
        self.assertEqual(self.shared_line(store.block(v, "Britain", where=None)), [])     # A4: home not known this turn

    def test_a_member_who_moved_away_is_not_said_to_share_it(self):
        i, v = self.real_mate()
        mate = next(o for k, o in v["house"] if k == "real")
        def moved(pid_):
            at = self.held.where(pid_)
            return ("Felucca", "Trinsic", at[2]) if at and pid_ == mate["id"] else at
        b = store.block(v, "Britain", where=moved)
        name = self.held.where(mate["id"])[2]["name"]
        self.assertFalse(any(name in x for x in self.shared_line(b)), b)
        self.assertNotIn("- You live alone.", b)                  # never inferred from a move
        self.assertIn(f"{name}, the ", b); self.assertIn("in Trinsic", b)   # the kin line still says where they are

    def test_same_town_name_on_another_map_is_not_home(self):
        i, v = self.real_mate()
        mate = next(o for k, o in v["house"] if k == "real")
        def other_map(pid_):
            at = self.held.where(pid_)
            return ("Trammel", "Britain", at[2]) if at and pid_ == mate["id"] else at
        name = self.held.where(mate["id"])[2]["name"]
        self.assertFalse(any(name in x for x in self.shared_line(store.block(v, "Britain", where=other_map))))

    def test_without_a_roster_a_real_housemate_is_not_asserted(self):
        i, v = self.real_mate()
        b = store.block(v, "Britain", where=None)
        for x in self.shared_line(b):
            self.assertNotIn("live alone", x)
            for k, o in v["house"]:
                if k == "real":
                    self.assertNotIn(self.held.where(o["id"])[2]["name"], x)

    def test_standing_in_another_town_says_nothing_about_the_home(self):
        i, v = self.real_mate()
        self.assertEqual(self.shared_line(store.block(v, "Trinsic", where=self.held.where)), [])
        i, v = self.find(lambda v: v["house"] == [])
        self.assertEqual(self.shared_line(store.block(v, "Trinsic", where=self.held.where)), [])

    def test_a_town_built_before_a3_says_nothing_about_the_home(self):
        i, v = self.find(lambda v: v["house"] == [])
        old = dict(v, house=None)
        self.assertEqual(self.shared_line(store.block(old, "Britain", where=self.held.where)), [])

    def test_a_clashing_offstage_name_is_left_out_of_the_home_line_too(self):
        i, v = self.find(lambda v: v["house"] and any(k == "off" for k, _ in v["house"]))
        o = next(o for k, o in v["house"] if k == "off")
        line = self.shared_line(store.block(v, "Britain", where=self.held.where, avoid_names=[o["name"]]))
        self.assertEqual(len(line), 1); self.assertNotIn(o["name"], line[0])


class Places(unittest.TestCase):
    """Repair A4 (Codex Stage 2 Method R1 F12; design 6.1, 6.2b): place words are true wherever the person stands."""
    setUpClass = classmethod(Block.setUpClass.__func__)
    tearDownClass = classmethod(Block.tearDownClass.__func__)
    find = Block.find
    HERE = re.compile(r"\bhere\b|this (very )?town", re.IGNORECASE)

    def moved(self, me, to=("Felucca", "Trinsic"), also=()):
        def where(x):
            at = self.held.where(x)
            return (to[0], to[1], at[2]) if at and (x == me or x in also) else at
        return where

    def test_no_usable_roster_never_says_here_and_names_the_stored_town(self):
        for i, v in self.views.items():
            if v["record"]["status"] != "resolved":
                continue
            for where, town in ((None, "Britain"), (self.held.where, "Trinsic")):   # no roster; roster disagrees with the game
                b = store.block(v, town, where=where)
                self.assertIsNone(self.HERE.search(b), (i, b))
                self.assertNotIn("- You live in ", b); self.assertNotIn("share your home", b); self.assertNotIn("live alone", b)
                if v["record"]["years_town"] is not None:
                    self.assertIn(f"You have spent {v['record']['years_town']} years of your life in Britain." if v["record"]["years_town_kind"] == "number"
                                  else f"You were born in Britain and have spent {v['record']['years_town']} years of your life there.", b)
                    self.assertNotIn("Put plainly", b)

    def test_at_home_with_a_roster_the_words_are_unchanged(self):
        i, v = self.find(lambda v: v["record"]["years_town_kind"] == "number")
        b = store.block(v, "Britain", where=self.held.where)
        self.assertIn(f"You have lived here in Britain for {v['record']['years_town']} years.", b)

    def test_a_moved_person_speaks_of_the_old_town_by_name(self):
        i, v = self.find(lambda v: v["record"]["years_town_kind"] == "number" and any(k == "off" and o["alive"] and o["place"] == "far" for _, k, o in v["kin"]))
        b = store.block(v, "Trinsic", where=self.moved(v["record"]["id"]))
        self.assertIn(f"You lived in Britain for {v['record']['years_town']} years.", b)
        self.assertIn("lives far from Britain", b); self.assertNotIn("far from here", b)
        self.assertNotIn("lives with you", b); self.assertNotIn("- You live in ", b)

    def test_relatives_are_here_only_on_the_same_map_and_town(self):
        i, v = self.find(lambda v: any(k == "real" for _, k, _o in v["kin"]))
        me = v["record"]["id"]
        mate = next(o for _, k, o in v["kin"] if k == "real")
        name = self.held.where(mate["id"])[2]["name"]
        line = lambda b: next(x for x in b.splitlines() if f"is {name}, the " in x)
        self.assertIn("here in Britain", line(store.block(v, "Britain", where=self.held.where)))
        other_map = self.moved(None, ("Trammel", "Britain"), also={mate["id"]})
        self.assertNotIn("here", line(store.block(v, "Britain", where=other_map)))
        both = self.moved(me, ("Felucca", "Trinsic"), also={mate["id"]})            # moved together
        self.assertIn("here in Trinsic", line(store.block(v, "Trinsic", where=both)))
        self.assertIn("in Britain", line(store.block(v, "Trinsic", where=self.moved(me))))
        self.assertNotIn("here", line(store.block(v, "Trinsic", where=self.moved(me))))


    def bg(self, home):
        c = backgrounds.cfg()
        return {"town": "Britain", "home": home, "life": c["lives"][0]["id"], "event": c["events"][0]["id"]}


    def test_at_home_the_anchored_text_equals_todays_text(self):
        c = backgrounds.cfg()
        for h in c["homes"]:
            bg = dict(self.bg(h["id"]), life=c["lives"][hash(h["id"]) % len(c["lives"])]["id"])
            self.assertEqual(backgrounds.text(bg, "britain", here=True), backgrounds.text(bg, "Britain"), h["id"])


    def test_the_years_habit_is_dropped_whenever_its_numbers_are_not_tenure_here(self):
        i, v = self.find(lambda v: v["record"]["years_town"] is not None and v["record"]["years_trade"] is not None)
        me, role = v["record"]["id"], v["record"]["role"]
        p = {"key": "x", "habit": "years"}
        self.assertEqual(store.nature_drops(v, self.held.where, "Britain", role, p), [])
        self.assertIn("habit", store.nature_drops(v, None, "Britain", role, p))                      # no roster
        self.assertIn("habit", store.nature_drops(v, self.moved(me), "Trinsic", role, p))           # moved
        self.assertIn("habit", store.nature_drops(v, self.held.where, "Britain", "a new trade", p))  # role changed (A2 gap)
        b = store.block(dict(v, record=dict(v["record"], habit="years")), "Britain", where=self.held.where, role_now="a new trade")
        self.assertNotIn("use only the numbers above", b)


    def test_for_turn_removes_exactly_the_dropped_lines(self):
        import nature
        p = nature.natural(pid(1))
        p = dict(p, habit="years", habit_line="You mention how many years you have lived or worked here.")
        full = nature.prompt_block(p)
        self.assertIn("Your nature:", full); self.assertIn("A habit of speech:", full)
        self.assertNotIn("A habit of speech:", nature.prompt_block(store.for_turn(p, ["habit"])))
        no_line = nature.prompt_block(store.for_turn(p, ["personality"]))
        self.assertNotIn("Your nature:", no_line); self.assertIn("A habit of speech:", no_line)
        self.assertIs(store.for_turn(p, []), p)


class Names(unittest.TestCase):
    """Repair A6 (Codex Stage 2 Method R1 F15): name clashes use every word of the names that arrive; a real relative's
    name is always the roster's current one."""
    setUpClass = classmethod(Block.setUpClass.__func__)
    tearDownClass = classmethod(Block.tearDownClass.__func__)
    find = Block.find

    def test_name_words(self):
        self.assertEqual(store.name_words(["Elspeth Vale", " ", None, "O'Brien"]), {"elspeth vale", "elspeth", "vale", "o'brien"})

    def test_a_first_name_inside_a_full_name_is_a_clash(self):
        i, v = self.find(lambda v: any(kind == "off" and o["alive"] for _, kind, o in v["kin"]))
        o = next(o for _, kind, o in v["kin"] if kind == "off" and o["alive"])
        b = store.block(v, "Britain", where=self.held.where, avoid_names=[f"{o['name']} Vale, the Wanderer"])
        self.assertNotIn(o["name"], b)
        self.assertIn(o["name"], store.block(v, "Britain", where=self.held.where, avoid_names=["Someone Else"]))

    def test_a_renamed_real_relative_is_called_by_the_new_name(self):
        i, v = self.find(lambda v: any(kind == "real" for _, kind, _o in v["kin"]))
        other = next(o for _, kind, o in v["kin"] if kind == "real")
        old = self.held.where(other["id"])[2]["name"]

        def renamed(x):
            at = self.held.where(x)
            return (at[0], at[1], dict(at[2], name="Quillon")) if at and x == other["id"] else at
        b = store.block(v, "Britain", where=renamed)
        self.assertIn("is Quillon, the ", b); self.assertNotIn(f"is {old}, the ", b)


class TagsA7(unittest.TestCase):
    """Repair A7 (Codex Stage 2 Method R1 F15): travelling former lives are absences; the grandmother jab is figurative."""


    def jabbed(self, jab_index, group="prickly"):
        import nature
        return {"group": group, "jab": nature.cfg()["jabs"][jab_index]}

    def test_the_grandmother_jab_is_named_a_figure_of_speech(self):
        line = store.figurative_line(self.jabbed(7))
        self.assertIn("grandmother is only a figure of speech", line)
        self.assertIn("not about anyone in your own family", line)
        self.assertEqual(store.figurative_line(self.jabbed(6)), "")                  # a jab with no kin or number
        self.assertEqual(store.figurative_line(self.jabbed(7, group="quiet")), "")   # no jab in the prompt
        self.assertEqual(store.figurative_line(None), "")

    def test_the_line_sits_in_the_facts_before_the_rules(self):
        led_dir = tempfile.TemporaryDirectory()
        try:
            led = memory.Ledger(os.path.join(led_dir.name, "m.db"))
            import backgrounds as bgm
            bgm.ensure_table(led); store.ensure_tables(led)
            ro = townroster.Roster(led); ro.accept(roster_doc([{"map": "Felucca", "town": "Britain", "people": folk(20)}]))
            store.build_town(led, ro.held(), "Felucca", "Britain", epoch=session(led))
            v = next(store.view(led, pid(i)) for i in range(20) if store.view(led, pid(i))["record"]["status"] == "resolved")
            b = store.block(v, "Britain", where=ro.held().where, nature_now=self.jabbed(7))
            self.assertLess(b.index("figure of speech"), b.index(store.RULES))
            self.assertNotIn("figure of speech", store.block(v, "Britain", where=ro.held().where, nature_now=self.jabbed(6)))
        finally:
            led_dir.cleanup()


class SessionAndFreeze(unittest.TestCase):
    """Repair B3 (Codex Stage 2 Method R1 F13): no family record without a reconciled ledger for the shard session, and
    the catalogue bytes used are the bytes hashed and recorded."""

    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        import backgrounds as bgm
        bgm.ensure_table(self.led); store.ensure_tables(self.led)
        self.roster = townroster.Roster(self.led)
        self.roster.accept(roster_doc([{"map": "Felucca", "town": "Britain", "people": folk(30)}]))
        self.held = self.roster.held()

    def tearDown(self):
        self.dir.cleanup()

    def rows(self):
        return sum(self.led.db.execute(f"SELECT COUNT(*) FROM {t}").fetchone()[0]
                   for t in ("people_person", "people_offstage", "people_tie", "people_household", "people_town", "npc_backgrounds"))

    def test_no_session_no_build(self):
        self.assertEqual(store.build_town(self.led, self.held, "Felucca", "Britain"), "not-reconciled")
        self.assertEqual(self.rows(), 0)

    def test_a_ledger_never_reconciled_is_refused(self):
        self.assertEqual(store.build_town(self.led, self.held, "Felucca", "Britain", epoch=1), "not-reconciled")
        self.assertEqual(self.rows(), 0)

    def test_a_newer_session_reconciled_since_refuses_the_old_one(self):
        self.assertEqual(self.led.reconcile(1, 0, 0)[0], "fresh")
        self.assertIn(self.led.reconcile(2, 1, 0)[0], ("ok", "fresh"))          # the shard restarted
        self.assertEqual(store.build_town(self.led, self.held, "Felucca", "Britain", epoch=1), "not-reconciled")
        self.assertEqual(self.rows(), 0)
        self.assertEqual(store.build_town(self.led, self.held, "Felucca", "Britain", epoch=2), "built")

    def test_a_catalogue_in_memory_that_differs_from_disk_builds_nothing(self):
        session(self.led)
        with mock.patch.dict(people.data()["tags"], {"H01": {"minage": 30}}):
            self.assertEqual(store.build_town(self.led, self.held, "Felucca", "Britain", epoch=1), "catalogue-changed")
            self.assertEqual(store.build_all(self.led, self.held, epoch=1), {"catalogue-changed": 1})
        self.assertEqual(self.rows(), 0)

    def test_bytes_changing_during_the_build_roll_it_back(self):
        session(self.led)
        real = store._file_bytes
        calls = []

        def changing():
            calls.append(1)
            out = real()
            if len(calls) > 1:
                out = dict(out, **{"people_lists.json": out["people_lists.json"] + b" "})
            return out
        with mock.patch.object(store, "_file_bytes", changing):
            self.assertEqual(store.build_town(self.led, self.held, "Felucca", "Britain", epoch=1), "catalogue-changed")
        self.assertEqual(self.rows(), 0)

    def test_the_hashed_bytes_and_the_builder_are_recorded(self):
        session(self.led)
        f = store.freeze()
        self.assertEqual(store.build_town(self.led, self.held, "Felucca", "Britain", epoch=1), "built")
        h, summ = self.led.db.execute("SELECT catalog_hash, summary FROM people_town").fetchone()
        self.assertEqual(h, f["catalog_hash"]); self.assertEqual(h, store.catalog_hash())
        prov = json.loads(summ)["provenance"]
        self.assertEqual((prov["builder_code"], prov["shard_epoch"], prov["catalogue_files"]), (f["builder_code"], 1, f["files"]))
        self.assertEqual(sorted(prov["catalogue_files"]), sorted(store.FILES))


class Pins(unittest.TestCase):
    """Design 6.4 / Codex Stage 2 Method R1 F8: the STORED nature is the one every prompt path uses."""

    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        import backgrounds
        backgrounds.ensure_table(self.led)
        store.ensure_tables(self.led)
        r = townroster.Roster(self.led)
        r.accept(roster_doc([{"map": "Felucca", "town": "Britain", "people": folk(40)}]))
        store.build_town(self.led, r.held(), "Felucca", "Britain", epoch=session(self.led))
        store._pin_cache.clear()
        import nature
        self.nature = nature

    def tearDown(self):
        self.nature.set_pins(None)
        store._pin_cache.clear()
        self.dir.cleanup()

    def stored(self, i):
        return json.loads(self.led.db.execute("SELECT record FROM people_person WHERE person_id = ?", (pid(i),)).fetchone()[0])

    def restore(self, i, **change):
        r = self.stored(i); r.update(change)
        self.led.db.execute("UPDATE people_person SET record = ? WHERE person_id = ?", (json.dumps(r), pid(i)))
        store._pin_cache.clear()

    def test_without_a_pin_source_nothing_changes(self):
        self.assertEqual(self.nature.of(pid(1)), self.nature.natural(pid(1)))

    def test_the_stored_personality_is_the_one_that_talks(self):
        natural = self.nature.natural(pid(1))["key"]
        other = "bashful" if natural != "bashful" else "cheerful"
        self.restore(1, personality=other)                      # as a re-pin at the build would have stored it
        self.nature.set_pins(lambda npc: store.pins(self.led, npc))
        p = self.nature.of(pid(1))
        self.assertEqual(p["key"], other)
        line = next(x["line"] for x in self.nature.cfg()["personalities"] if x["key"] == other)
        self.assertIn(line, self.nature.prompt_block(p))
        self.assertEqual(self.nature.natural(pid(1))["key"], natural)          # the catalogue pick itself is untouched
        self.assertEqual(self.nature.of(pid(2))["key"], self.stored(2)["personality"])

    def test_a_lookup_before_the_build_does_not_hide_the_build(self):
        """Codex Method R2 F8: asked before the town is built (no record: natural nature), then built - the stored
        nature is used from the next turn, with no cache clearing in between."""
        d = tempfile.TemporaryDirectory()
        try:
            led = memory.Ledger(os.path.join(d.name, "m.db"))
            import backgrounds
            backgrounds.ensure_table(led)
            store.ensure_tables(led)
            r = townroster.Roster(led)
            r.accept(roster_doc([{"map": "Felucca", "town": "Britain", "people": folk(40)}]))
            self.assertIsNone(store.pins(led, pid(1)))                         # before the build: no record
            store.build_town(led, r.held(), "Felucca", "Britain", epoch=session(led))
            got = store.pins(led, pid(1))
            self.assertIsNotNone(got)
            rec = json.loads(led.db.execute("SELECT record FROM people_person WHERE person_id = ?", (pid(1),)).fetchone()[0])
            self.assertEqual(got["key"], rec["personality"])
        finally:
            d.cleanup()

    def test_a_person_with_no_record_keeps_the_natural_pick(self):
        self.nature.set_pins(lambda npc: store.pins(self.led, npc))
        stranger = "99999999-3333-4333-8333-333333333333"
        self.assertEqual(self.nature.of(stranger), self.nature.natural(stranger))

    def test_a_removed_key_falls_back_to_one_that_fits_and_stays_the_same(self):
        self.restore(1, personality="no-longer-in-the-catalogue", habit="gone-too", subject="a subject that was deleted")
        self.nature.set_pins(lambda npc: store.pins(self.led, npc))
        a = self.nature.of(pid(1)); store._pin_cache.clear(); b = self.nature.of(pid(1))
        self.assertEqual((a["key"], a["habit"], a["subject"]), (b["key"], b["habit"], b["subject"]))
        self.assertIn(a["key"], [x["key"] for x in self.nature.cfg()["personalities"]])
        self.assertIn(a["habit"], [x["key"] for x in self.nature.cfg()["habits"]])
        self.assertIn(a["subject"], self.nature.cfg()["subjects"])

    def test_a_homesick_replacement_is_never_given_to_someone_born_here(self):
        r = self.stored(1)
        self.restore(1, personality="no-longer-in-the-catalogue", home="here", bg=dict(r["bg"], home="H01"))
        self.nature.set_pins(lambda npc: store.pins(self.led, npc))
        self.assertNotEqual(self.nature.of(pid(1))["key"], "homesick")

    def test_the_years_habit_is_silent_when_no_number_is_held(self):
        self.restore(1, habit="years", years_town=None, years_trade=None)
        self.nature.set_pins(lambda npc: store.pins(self.led, npc))
        p = self.nature.of(pid(1))
        self.assertEqual((p["habit"], p["habit_line"]), ("", ""))
        self.assertNotIn("how many years", self.nature.prompt_block(p))
        self.restore(1, habit="years", years_town=None, years_trade=7)          # repair A2 / N11: EITHER number missing
        self.assertEqual(self.nature.of(pid(1))["habit"], "")
        self.restore(1, habit="years", years_town=12, years_trade=None)
        self.assertEqual(self.nature.of(pid(1))["habit"], "")
        self.restore(1, habit="years", years_town=12, years_trade=7)
        self.assertEqual(self.nature.of(pid(1))["habit"], "years")


if __name__ == "__main__":
    unittest.main()
