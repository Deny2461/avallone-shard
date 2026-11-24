"""Errands build 1, stage 3: the cousin and contact tables (errands_people.py) - the one pass, the two audits run
against wrong tables, fingerprints and pins, and the family view's completeness state (design v5 3.4).
Run: python3 test_errands_people.py"""
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import errands_people as ep  # noqa: E402
import memory  # noqa: E402
import people_store  # noqa: E402

TOWNS = ("Britain", "Minoc", "Vesper", "Yew")
ROLES = ("baker", "blacksmith", "innkeeper", "weaver")


def pid(n):
    return f"{n:08d}-0000-4000-8000-000000000000"


class Held:
    """The shape of townroster.Held that the pass reads."""
    def __init__(self, people, hash_="h" * 64):
        self.hash, self.by_id = hash_, {}
        for p in people:
            self.by_id[p["id"]] = (p["map"], p["town"], {"id": p["id"], "name": p["name"], "role": p["role"],
                                                         "sex": p["sex"], "kind": p.get("kind", "resident"), "home": None})

    def where(self, i):
        return self.by_id.get(i)


def world(n_per_town=12, maps=("Felucca",)):
    people, n = [], 0
    for m in maps:
        for t in TOWNS:
            for i in range(n_per_town):
                n += 1
                people.append({"id": pid(n), "map": m, "town": t, "name": f"P{chr(65 + n % 26)}{chr(97 + i % 26)}",
                               "role": ROLES[i % len(ROLES)], "sex": "man" if n % 2 else "woman", "age": 20 + (n * 7) % 50})
    return people


class Base(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        people_store.ensure_tables(self.led)
        self.people = world()
        self.store(self.people)
        self.held = Held(self.people)

    def tearDown(self):
        self.dir.cleanup()

    def store(self, people, ties=()):
        for p in people:
            rec = {"id": p["id"], "map": p["map"], "town": p["town"], "status": p.get("status", "resolved"),
                   "age": p["age"], "sex": p["sex"], "role": p["role"]}
            self.led.db.execute("INSERT OR REPLACE INTO people_person (person_id, map, town, status, record, built_utc) "
                                "VALUES (?, ?, ?, ?, ?, 'x')", (p["id"], p["map"], p["town"], rec["status"], json.dumps(rec)))
        for a, rel, b in ties:
            self.led.db.execute("INSERT INTO people_tie (a, rel, b) VALUES (?, ?, ?)", (a, rel, b))

    def pass_(self, **kw):
        return ep.run_pass(self.led, self.held, kw.pop("seed", 7), **kw)

    def pins(self, out, **more):
        return dict({"errand_cousin_pin": out["cousin"]["fingerprint"], "errand_contact_pin": out["contact"]["fingerprint"],
                     "errand_tables": True}, **more)


class Pass(Base):
    def test_pairs_keep_every_rule(self):
        out = self.pass_(target_pairs=10)
        rows = ep.cousin_rows(self.led.db)
        self.assertEqual(len(rows), 10)
        self.assertEqual(out["cousin"]["pairs"], 10)
        recs = {p["id"]: p for p in self.people}
        seen = set()
        for a, b, m, ta, tb, rv in rows:
            self.assertLess(a, b)
            self.assertNotEqual(ta, tb)
            self.assertEqual((recs[a]["town"], recs[b]["town"], recs[a]["map"], recs[b]["map"]), (ta, tb, m, m))
            self.assertLessEqual(abs(recs[a]["age"] - recs[b]["age"]), ep.AGE_GAP)
            self.assertFalse({a, b} & seen)
            seen |= {a, b}
        self.assertEqual(ep.audit_cousins(self.led.db), [])

    def test_the_draw_is_the_same_for_a_seed_and_differs_between_seeds(self):
        people, real = ep._people(self.led.db), set()
        one = ep.draw_cousins(people, real, self.held, 7, 10)[0]
        self.assertEqual(one, ep.draw_cousins(people, real, self.held, 7, 10)[0])
        self.assertNotEqual(one, ep.draw_cousins(people, real, self.held, 8, 10)[0])

    def test_places_follow_town_size(self):
        big = [dict(p, id=pid(1000 + i), town="Britain") for i, p in enumerate(world(36)[:36])]
        self.store(big)
        self.held = Held(self.people + big)
        self.pass_(target_pairs=12)
        per = {}
        for a, b, m, ta, tb, rv in ep.cousin_rows(self.led.db):
            per[ta] = per.get(ta, 0) + 1
            per[tb] = per.get(tb, 0) + 1
        self.assertGreater(per["Britain"], max(per.get(t, 0) for t in TOWNS[1:]))

    def test_who_is_left_out_of_the_draw(self):
        a, b, c, d, e = (self.people[i] for i in (0, 1, 2, 3, 4))
        self.store([dict(b, status="unresolved"), dict(c, age=0)], ties=[(a["id"], "sibling", self.people[5]["id"]),
                                                                           (self.people[5]["id"], "sibling", a["id"]),
                                                                           (d["id"], "parent", "o-77")])
        self.held = Held([p for p in self.people if p["id"] != e["id"]] + [dict(self.people[6], kind="placed")]
                         + [dict(self.people[7], town="Yew")])
        self.pass_(target_pairs=100)
        used = {x for row in ep.cousin_rows(self.led.db) for x in row[:2]}
        for left_out in (a, self.people[5], b, c, e, self.people[6], self.people[7]):
            self.assertNotIn(left_out["id"], used)
        self.assertIn(d["id"], used | {d["id"]})             # an off-stage parent is not a real relative: d stays eligible
        self.assertEqual(ep.audit_cousins(self.led.db), [])

    def test_the_target_is_never_met_by_relaxing_a_rule(self):
        old = [dict(p, age=20 if p["town"] == "Britain" else 90) for p in self.people]
        self.store(old)
        out = self.pass_(target_pairs=100)
        for a, b, m, ta, tb, rv in ep.cousin_rows(self.led.db):
            self.assertNotIn("Britain", (ta, tb))
        self.assertLess(out["cousin"]["pairs"], 24)
        self.assertGreater(out["cousin"]["shortfall"]["Felucca/Britain"], 0)

    def test_cousins_never_cross_maps(self):
        other = [dict(p, id=pid(500 + i), map="Trammel") for i, p in enumerate(world(3))]
        self.store(other)
        self.held = Held(self.people + other)
        self.pass_()
        for a, b, m, ta, tb, rv in ep.cousin_rows(self.led.db):
            self.assertEqual(ep._people(self.led.db)[a]["map"], ep._people(self.led.db)[b]["map"])

    def test_contacts_are_directed_same_trade_other_town(self):
        self.pass_()
        rows = ep.contact_rows(self.led.db)
        self.assertEqual(len(rows), len(self.people))
        role = {p["id"]: (p["role"], p["town"]) for p in self.people}
        for sender, contact, m, trade, ts, tc, rv in rows:
            self.assertEqual((role[sender][0], role[contact][0]), (trade, trade))
            self.assertNotEqual(role[sender][1], role[contact][1])
        self.assertEqual(ep.contact_of(self.led, rows[0][0])["contact"], rows[0][1])
        self.assertEqual(ep.audit_contacts(self.led.db), [])

    def test_a_sender_with_nobody_of_the_trade_elsewhere_gets_none(self):
        lone = dict(self.people[0], id=pid(900), role="glassblower")
        self.store([lone])
        self.held = Held(self.people + [lone, dict(self.people[1], id=pid(901), role="glassblower", town=lone["town"])])
        out = self.pass_()
        self.assertIsNone(ep.contact_of(self.led, lone["id"]))
        self.assertEqual(out["contact"]["without"], 1)       # 901 has no stored record, so it is neither sender nor contact

    def test_the_pass_is_run_once_only_and_writes_all_or_nothing(self):
        out = self.pass_()
        before = (ep.cousin_rows(self.led.db), ep.contact_rows(self.led.db))
        with self.assertRaises(RuntimeError):
            self.pass_(seed=99)
        self.assertEqual(before, (ep.cousin_rows(self.led.db), ep.contact_rows(self.led.db)))
        self.assertEqual(out["cousin"]["fingerprint"], ep.fingerprint(before[0]))
        self.assertEqual(ep.provenance(self.led.db, "cousin")["seed"], "7")
        with self.assertRaises(RuntimeError):
            ep.run_pass(self.led, None, 7)

    def test_the_five_stored_tables_are_not_changed_by_the_pass(self):
        q = "SELECT * FROM {}"
        tables = ("people_town", "people_person", "people_offstage", "people_tie", "people_household")
        before = [[tuple(r) for r in self.led.db.execute(q.format(t)).fetchall()] for t in tables]
        self.pass_()
        self.assertEqual(before, [[tuple(r) for r in self.led.db.execute(q.format(t)).fetchall()] for t in tables])


class Audits(Base):
    def setUp(self):
        super().setUp()
        self.out = self.pass_(target_pairs=6)
        self.rows = ep.cousin_rows(self.led.db)

    def one(self, sql, *args):
        self.led.db.execute(sql, args)

    def test_cousin_audit_against_wrong_tables(self):
        a, b, m, ta, tb, rv = self.rows[0]
        a2, b2 = self.rows[1][0], self.rows[1][1]
        free = next(p["id"] for p in self.people if p["id"] not in {x for r in self.rows for x in r[:2]})
        wrong = {
            "unknown rules version": lambda: self.one("UPDATE people_cousin SET rules_version = 9 WHERE a = ?", a),
            "the same person": lambda: self.one("UPDATE people_cousin SET b = a WHERE a = ?", a),
            "not in the fixed order": lambda: self.one("UPDATE people_cousin SET a = ?, b = ? WHERE a = ?", b, a, a),
            "more than one pair": lambda: self.one("INSERT INTO people_cousin VALUES (?, ?, ?, ?, ?, 1)",
                                                   min(a, free), max(a, free), m, ta, "Zed"),
            "not a resolved real person": lambda: self.one("DELETE FROM people_person WHERE person_id = ?", b),
            "not one map": lambda: self.one("UPDATE people_cousin SET map = 'Trammel' WHERE a = ?", a),
            "ages": lambda: self.store([dict(next(p for p in self.people if p["id"] == a), age=109),
                                        dict(next(p for p in self.people if p["id"] == b), age=20)]),
            "home towns": lambda: self.one("UPDATE people_cousin SET town_b = town_a WHERE a = ?", a),
            "has a real relative": lambda: self.one("INSERT INTO people_tie VALUES (?, 'sibling', ?)", a, a2),
        }
        for fault, damage in wrong.items():
            self.led.db.execute("SAVEPOINT t")
            damage()
            found = ep.audit_cousins(self.led.db)
            self.assertTrue(any(fault in f for f in found), (fault, found))
            self.led.db.execute("ROLLBACK TO t")
            self.led.db.execute("RELEASE t")
            self.assertEqual(ep.audit_cousins(self.led.db), [], fault)
        self.assertTrue(b2)

    def test_contact_audit_against_wrong_tables(self):
        sender, contact, m, trade, ts, tc, rv = ep.contact_rows(self.led.db)[0]
        wrong = {
            "unknown rules version": "UPDATE people_contact SET rules_version = 9 WHERE sender = ?",
            "the same person": "UPDATE people_contact SET contact = sender WHERE sender = ?",
            "not one map": "UPDATE people_contact SET map = 'Trammel' WHERE sender = ?",
            "creation facts": "UPDATE people_contact SET town_contact = town_sender WHERE sender = ?",
        }
        for fault, sql in wrong.items():
            self.led.db.execute("SAVEPOINT t")
            self.one(sql, sender)
            self.assertTrue(any(fault in f for f in ep.audit_contacts(self.led.db)), fault)
            self.led.db.execute("ROLLBACK TO t")
            self.led.db.execute("RELEASE t")
        self.led.db.execute("SAVEPOINT t")
        self.one("DELETE FROM people_person WHERE person_id = ?", contact)
        self.assertTrue(any("not a resolved real person" in f for f in ep.audit_contacts(self.led.db)))
        self.led.db.execute("ROLLBACK TO t")
        self.led.db.execute("RELEASE t")
        # a contact may serve many senders, and A -> B does not need B -> A: neither is a fault
        self.assertEqual(ep.audit_contacts(self.led.db), [])


class State(Base):
    def test_no_pin_means_off_and_todays_output(self):
        st = ep.state(self.led, {})
        self.assertEqual((st["cousin"]["state"], st["contact"]["state"], ep.completeness(st)), ("off", "off", "off"))
        self.assertFalse(ep.usable(st, "cousin"))

    def test_pinned_and_sound_is_available_and_whole(self):
        st = ep.state(self.led, self.pins(self.pass_()))
        self.assertEqual((st["cousin"]["state"], st["contact"]["state"], ep.completeness(st)),
                         ("available", "available", "whole"))
        self.assertTrue(ep.usable(st, "cousin") and ep.usable(st, "contact"))

    def test_a_pin_with_the_switch_off_or_after_a_restart_is_unknown_never_off(self):
        out = self.pass_()
        st = ep.state(self.led, self.pins(out, errand_tables=False))
        self.assertEqual((ep.completeness(st), ep.usable(st, "cousin")), ("unknown", False))
        again = memory.Ledger(self.led.path)                       # a new process: the state comes from the settings
        self.assertEqual(ep.completeness(ep.state(again, self.pins(out, errand_tables=False))), "unknown")
        self.assertEqual(ep.completeness(ep.state(again, self.pins(out))), "whole")

    def test_absent_part_built_changed_or_failing_is_unavailable_and_unknown(self):
        pin = {"errand_cousin_pin": "f" * 64, "errand_contact_pin": "f" * 64, "errand_tables": True}
        st = ep.state(self.led, pin)
        self.assertEqual((st["cousin"]["state"], st["cousin"]["reason"], ep.completeness(st)),
                         ("unavailable", "table absent", "unknown"))
        out = self.pass_()
        self.assertIn("not the pinned one", ep.state(self.led, pin)["cousin"]["reason"])
        good = self.pins(out)
        a = ep.cousin_rows(self.led.db)[0][0]
        self.led.db.execute("DELETE FROM people_cousin WHERE a = ?", (a,))
        st = ep.state(self.led, good)
        self.assertEqual((st["cousin"]["state"], ep.completeness(st), st["contact"]["state"]),
                         ("unavailable", "unknown", "available"))
        self.assertIn("part built", st["cousin"]["reason"])
        self.led.db.execute("DELETE FROM people_extra WHERE name = 'contact'")
        self.assertIn("part built", ep.state(self.led, good)["contact"]["reason"])

    def test_a_pinned_table_that_fails_its_audit_is_unavailable(self):
        out = self.pass_()
        a, b = ep.cousin_rows(self.led.db)[0][:2]
        self.led.db.execute("INSERT INTO people_tie VALUES (?, 'sibling', ?)", (a, b))
        st = ep.state(self.led, self.pins(out))
        self.assertEqual(st["cousin"]["state"], "unavailable")
        self.assertIn("audit", st["cousin"]["reason"])
        self.assertEqual(ep.completeness(st), "unknown")

    def test_state_never_writes(self):
        out = self.pass_()
        self.led.db.execute("DELETE FROM people_cousin")
        before = self.led.db.total_changes
        ep.state(self.led, self.pins(out))
        ep.state(self.led, {})
        self.assertEqual(self.led.db.total_changes, before)
        self.assertEqual(ep.cousin_rows(self.led.db), [])           # never drawn again by itself

    def test_cousin_of_reads_either_end(self):
        self.assertIsNone(ep.cousin_of(self.led, pid(1)))           # before the pass: no table, no cousin
        self.pass_()
        a, b = ep.cousin_rows(self.led.db)[0][:2]
        self.assertEqual((ep.cousin_of(self.led, a), ep.cousin_of(self.led, b)), (b, a))
        self.assertIsNone(ep.cousin_of(self.led, "nobody"))


if __name__ == "__main__":
    unittest.main()
