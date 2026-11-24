"""Errands build 1, stage 4: the one composed family view (design v5 3.4) - the stored ties plus the cousin table, fed to
ordinary talk and to the companion exchange through brain.people_block; its completeness state off / whole / unknown.
Real builder, real block(), no model. Run: python3 test_errands_family.py"""
import json
import os
import re
import sys
import tempfile
import unittest
from unittest import mock

_d = tempfile.TemporaryDirectory()
with open(os.path.join(_d.name, "c.json"), "w") as f:
    json.dump({"memory_db": os.path.join(_d.name, "x.db"), "secrets_dir": _d.name, "quiet_windows": [],
               "prompt_layout": "v2", "reflection_mode": "off", "lore_dir": _d.name}, f)
os.environ["AVALLONE_BRAIN_CONFIG"] = os.path.join(_d.name, "c.json")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import backgrounds  # noqa: E402
import brain  # noqa: E402
import errands_people as ep  # noqa: E402
import memory  # noqa: E402
import people_store as store  # noqa: E402
import townroster  # noqa: E402
from test_people_store import folk, roster_doc, session  # noqa: E402

KIN_HERE = re.compile(r"You have no kin living here|All your kin here")
COUSIN_LINE = re.compile(r"^- (Your cousin|You have a cousin)", re.M)     # a FAMILY line; one stored past says "cousin" too


class Family(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.dir = tempfile.TemporaryDirectory()
        cls.led = memory.Ledger(os.path.join(cls.dir.name, "m.db"))
        backgrounds.ensure_table(cls.led)
        store.ensure_tables(cls.led)
        cls.roster = townroster.Roster(cls.led)
        doc = roster_doc([{"map": "Felucca", "town": "Britain", "people": folk(60)},
                          {"map": "Felucca", "town": "Vesper", "people": folk(60, start=200)}])
        cls.roster.accept(doc)
        cls.stamp = doc["hash"]
        cls.held = cls.roster.held()
        for town in ("Britain", "Vesper"):
            assert store.build_town(cls.led, cls.held, "Felucca", town, epoch=session(cls.led)) == "built"
        cls.out = ep.run_pass(cls.led, cls.held, 11, target_pairs=12)
        cls.pairs = [r[:2] for r in ep.cousin_rows(cls.led.db)]
        assert len(cls.pairs) >= 6, len(cls.pairs)
        cls.pins = {"errand_cousin_pin": cls.out["cousin"]["fingerprint"],
                    "errand_contact_pin": cls.out["contact"]["fingerprint"], "errand_tables": True}

    @classmethod
    def tearDownClass(cls):
        cls.dir.cleanup()

    def town(self, pid):
        return self.held.where(pid)[1]

    def text(self, v, pid, where=None, **kw):
        return store.block(v, self.town(pid), where=where or self.held.where, kin_here=True, **kw)

    # ---- people_store.block ----

    def test_off_is_exactly_todays_text_and_snapshot(self):
        for pid in list(self.held.by_id)[:60]:
            v = store.view(self.led, pid)
            a, b = {}, {}
            self.assertEqual(store.block(v, self.town(pid), where=self.held.where, kin_here=True, snap=a),
                             store.block(v, self.town(pid), where=self.held.where, kin_here=True, snap=b, completeness="off"))
            self.assertEqual(a, b)
            self.assertNotIn("completeness", a)
            self.assertFalse(COUSIN_LINE.search(store.block(v, self.town(pid), where=self.held.where, kin_here=True)))

    def test_whole_adds_one_line_after_the_siblings_and_changes_nothing_else(self):
        for a, b in self.pairs:
            v = store.view(self.led, a)
            before = self.text(v, a).split("\n")
            snap = {}
            after = self.text(store.with_cousin(self.led, v, b), a, completeness="whole", snap=snap).split("\n")
            person = self.held.where(b)[2]
            line = f"- Your cousin is {person['name']}, the {person['role']} in {self.town(b)}; "
            added = [x for x in after if x not in before]
            self.assertEqual(len(added), 1, added)
            self.assertTrue(added[0].startswith(line), added)
            self.assertEqual([x for x in before if x not in after], [])
            self.assertEqual(snap["completeness"], "whole")
            self.assertEqual(snap["kin"][-1]["word"], "cousin")
            # a cousin in ANOTHER town: "no kin living here" is still true and still said
            for fixed in ("- You have no brothers or sisters.", "- You have no children.", "- You live alone."):
                self.assertEqual(fixed in before, fixed in after)
            self.assertEqual(any(KIN_HERE.search(x) for x in before), any(KIN_HERE.search(x) for x in after))
            kin_lines = [x for x in after if x.startswith("- Your ") and not x.startswith("- Your past:")
                         and (" is " in x or ": " in x)]
            self.assertEqual(kin_lines[-1], added[0])           # after every other relative

    def test_a_cousin_standing_in_the_speakers_town_is_counted_in_all_your_kin_here(self):
        a, b = self.pairs[0]
        mine, theirs = self.held.where(a), self.held.where(b)

        def where(pid):
            return (mine[0], mine[1], theirs[2]) if pid == b else self.held.where(pid)
        out = self.text(store.with_cousin(self.led, store.view(self.led, a), b), a, where=where, completeness="whole")
        self.assertIn(f"your cousin {theirs[2]['name']}", out)
        self.assertIn(f"All your kin here in {mine[1]}", out)
        self.assertNotIn("You have no kin living here", out)

    def test_a_cousin_the_town_list_does_not_place_is_given_without_name_trade_or_place(self):
        a, b = self.pairs[0]
        person = self.held.where(b)[2]
        out = self.text(store.with_cousin(self.led, store.view(self.led, a), b), a, completeness="whole",
                        where=lambda pid: None if pid == b else self.held.where(pid))
        self.assertIn("- You have a cousin who is a townsperson; you do not speak of where they are just now.", out)
        self.assertNotIn(person["name"], out)
        self.assertFalse(KIN_HERE.search(out))                  # the existing rule drops the summary; it covers cousins too

    def test_unknown_leaves_out_every_clause_a_cousin_could_change(self):
        for pid in list(self.held.by_id)[:60]:
            v = store.view(self.led, pid)
            snap = {}
            out = self.text(v, pid, completeness="unknown", snap=snap)
            self.assertFalse(KIN_HERE.search(out), out)
            self.assertFalse(COUSIN_LINE.search(out))
            self.assertEqual(snap["completeness"], "unknown")
            self.assertNotIn("kin_here", snap)
            self.assertEqual(out.split("\n"), [x for x in self.text(v, pid).split("\n") if not KIN_HERE.search(x)])

    def test_with_cousin_refuses_what_it_cannot_stand_behind(self):
        a, b = self.pairs[0]
        v = store.view(self.led, a)
        self.assertIsNone(store.with_cousin(self.led, v, "nobody"))
        self.assertIsNone(store.with_cousin(self.led, None, b))
        self.assertEqual(len(store.with_cousin(self.led, v, b)["kin"]), len(v["kin"]) + 1)
        self.assertEqual(store.view(self.led, a)["kin"], v["kin"])               # the stored view is not changed
        self.assertEqual(store.with_cousin(self.led, v, b)["house"], v["house"])  # a cousin joins no household

    # ---- brain.people_block: the one way in for talk and for the exchange ----

    def block_through_brain(self, pid, cfg):
        req = {"roster_stamp": self.stamp, "character": "Traveller"}
        base = {"people": True, "always_know": "A", "kin_in_town": True}
        with mock.patch.object(brain, "LEDGER", self.led), mock.patch.object(brain, "ROSTER", self.roster), \
                mock.patch.dict(brain.CFG, dict(base, **cfg)), mock.patch.object(brain, "log", lambda **k: None):
            brain.errand_tables(refresh=True)
            try:
                return brain.people_block(req, pid, {"town": self.town(pid), "role": self.held.where(pid)[2]["role"]}, "c", 1)
            finally:
                brain._errand_tables["state"] = None

    def test_through_the_brain_off_whole_and_unknown(self):
        a, b = self.pairs[0]
        name = self.held.where(b)[2]["name"]
        off, info = self.block_through_brain(a, {})
        self.assertNotIn("people_family", info)
        self.assertFalse(COUSIN_LINE.search(off))
        whole, info = self.block_through_brain(a, self.pins)
        self.assertEqual(info["people_family"], "whole")
        self.assertIn(f"- Your cousin is {name}, the ", whole)
        unknown, info = self.block_through_brain(a, dict(self.pins, errand_tables=False))
        self.assertEqual(info["people_family"], "unknown")
        self.assertFalse(COUSIN_LINE.search(unknown))
        self.assertFalse(KIN_HERE.search(unknown))
        wrong, info = self.block_through_brain(a, dict(self.pins, errand_cousin_pin="0" * 64))
        self.assertEqual(info["people_family"], "unknown")
        self.assertFalse(KIN_HERE.search(wrong))

    def test_someone_with_no_cousin_in_a_whole_view_keeps_todays_text(self):
        paired = {x for p in self.pairs for x in p}
        pid = next(p for p in self.held.by_id if p not in paired)
        off, _ = self.block_through_brain(pid, {})
        whole, info = self.block_through_brain(pid, self.pins)
        self.assertEqual((off, info["people_family"]), (whole, "whole"))

    def test_no_ledger_or_a_failing_audit_is_unavailable_never_off(self):
        with mock.patch.object(brain, "LEDGER", None), mock.patch.dict(brain.CFG, self.pins), \
                mock.patch.object(brain, "log", lambda **k: None):
            st = brain.errand_tables(refresh=True)
            self.assertEqual((st["cousin"]["state"], ep.completeness(st)), ("unavailable", "unknown"))
        with mock.patch.object(brain, "LEDGER", None), mock.patch.dict(brain.CFG, {}), \
                mock.patch.object(brain, "log", lambda **k: None):
            self.assertEqual(ep.completeness(brain.errand_tables(refresh=True)), "off")
        brain._errand_tables["state"] = None

    def test_nobody_whose_stored_past_speaks_of_a_cousin_was_given_one(self):
        said = 0
        for pid in self.held.by_id:
            rec = store.view(self.led, pid)["record"]
            if ep._past_names_a_cousin(rec):
                said += 1
                self.assertIsNone(ep.cousin_of(self.led, pid))
        self.assertGreater(said, 0)                              # the sample towns do hold such a person
        self.assertTrue(ep._past_names_a_cousin({"bg": {"event": "no-such-id"}, "town": "Britain"}) in (True, False))
        self.assertFalse(ep._past_names_a_cousin({"bg": None}))

    def test_talk_and_exchange_both_come_through_the_one_function(self):
        with open(os.path.join(os.path.dirname(os.path.abspath(brain.__file__)), "brain.py"), encoding="utf-8") as fh:
            src = fh.read()
        self.assertEqual(src.count("people_block(req, npc,"), 3)                 # its definition and its two callers
        self.assertEqual(src.count("people_store.view("), 1)
        self.assertEqual(src.count("people_store.block("), 1)


if __name__ == "__main__":
    unittest.main()
