"""Townsfolk step, Stage 2 - people.py (design §6.2a N1-N11, §6.3). Run: python3 -m unittest test_people"""
import copy
import hashlib
import json
import unittest
from unittest import mock

import backgrounds
import nature
import people

PERS = [(p["key"], p.get("weight", 0)) for p in nature.cfg()["personalities"]]


def pid(i):
    return f"{i:08d}-2222-4222-8222-222222222222"


def town(n=60, name="Britain", start=0):
    """n made-up residents with the brain's own nature picks and the backgrounds its own chooser would give."""
    roles = ["tailor", "blacksmith", "baker", "healer", "innkeeper", "mage", "cobbler", "weaver guildmaster", "banker", "fisher"]
    names_m, names_w = people.data()["names"]["man"], people.data()["names"]["woman"]
    ps, bgs, held, cat = [], {}, {}, backgrounds.cfg()
    for i in range(start, start + n):
        sex = "man" if i % 2 else "woman"
        ps.append({"id": pid(i), "name": (names_m if sex == "man" else names_w)[(i * 7) % 900], "role": roles[i % len(roles)],
                   "sex": sex, "kind": "resident", "home": None})
        h, life, e, _ = backgrounds._choose(cat, pid(i), name, roles[i % len(roles)], "", held)
        bgs[pid(i)] = {"home": h["id"], "life": life["id"], "event": e["id"]}
        held[e["id"]] = held.get(e["id"], 0) + 1
    nat = {p["id"]: nature.of(p["id"]) for p in ps}
    return ps, bgs, nat


def build(n=60, **kw):
    ps, bgs, nat = town(n)
    return people.build_town("Felucca", "Britain", ps, bgs, nat, PERS, ["Britain", "Trinsic", "Yew"], **kw)


class Profile(unittest.TestCase):

    def test_untagged_ids_fix_nothing(self):
        p = people.profile(["L03", "E005", "personality:cheerful", "habit:lists", "subject:your cat"])
        self.assertEqual({k: p[k] for k in people.BLANK}, people.BLANK)



class BornHereYears(unittest.TestCase):
    """Decision 8 (the owner, chat, 2026-11-17): born here, years in town come from the person's own sentences."""

    def years(self, ids, age=40):
        r = {"prof": people.profile(ids), "age": age, "id": pid(1)}
        people.set_years(r, None)
        return r["years_town"], r["years_town_kind"]


    def test_away_years_without_absence_or_malformed_is_unresolved(self):
        tags = people.data()["tags"]
        for bad in ({"home": "here", "away_years": 1}, {"home": "here", "absence": True, "away_years": "one"},
                    {"home": "here", "absence": True, "away_years": -1}, {"home": "here", "years_town_open": "yes"}):
            with mock.patch.dict(tags, {"H03": bad}):
                with self.assertRaises(people.Unresolved, msg=bad):
                    people.profile(["H03"])


class Build(unittest.TestCase):
    def test_a_town_passes_its_audit(self):
        res = build(80)
        self.assertEqual(people.audit(res), [])
        s = people.summary(res)
        self.assertGreater(s["in_a_real_unit"], 10); self.assertGreater(s["offstage_people"], 150)
        self.assertTrue(all(r["age"] is None or 18 <= r["age"] <= 80 for r in res["records"].values()))

    def test_same_input_same_town_whatever_the_order(self):
        ps, bgs, nat = town(50)
        a = people.build_town("Felucca", "Britain", ps, bgs, nat, PERS, ["Britain", "Yew"])
        b = people.build_town("Felucca", "Britain", list(reversed(ps)), bgs, nat, PERS, ["Yew", "Britain"][::-1])
        self.assertEqual(json.dumps(a, sort_keys=True, default=str), json.dumps(b, sort_keys=True, default=str))

    def test_another_town_name_gives_other_units(self):
        ps, bgs, nat = town(50)
        a = people.build_town("Felucca", "Britain", ps, bgs, nat, PERS, ["Yew"])
        b = people.build_town("Felucca", "Trinsic", ps, bgs, nat, PERS, ["Yew"])
        self.assertNotEqual([u["id"] for u in a["units"]], [u["id"] for u in b["units"]])


    def test_the_dead_have_no_age_and_children_get_children_s_lines(self):
        res = build(80)
        fates = {f["id"]: f for f in people.data()["lists"]["fates"]}
        dead = [o for o in res["offstage"].values() if not o["alive"]]
        kids = [o for o in res["offstage"].values() if o["alive"] and o["age"] < 14]
        self.assertTrue(dead and kids)
        self.assertTrue(all(o["age"] is None and fates[o["fate"]]["class"].startswith("dead") for o in dead))
        self.assertTrue(all(fates[o["fate"]]["class"] == "child" and o["place"] == "here" for o in kids))



class Malformed(unittest.TestCase):
    """Repair B2 (Codex Stage 2 Method R1 F11): an unknown, removed or malformed catalogue input makes THAT person
    unresolved - never a normal person with no constraints, never a failed town."""

    def built_with(self, i, **bg_change):
        ps, bgs, nat = town(40)
        bgs[pid(i)] = dict(bgs[pid(i)], **bg_change) if bg_change else None
        res = people.build_town("Felucca", "Britain", ps, bgs, nat, PERS, ["Britain", "Trinsic", "Yew"])
        self.assertEqual(people.audit(res), [])
        others = [r for r in res["records"].values() if r["id"] != pid(i)]
        self.assertTrue(all(r["status"] == "resolved" for r in others))       # nobody else is touched
        return res["records"][pid(i)]

    def test_codex_a_home_that_does_not_exist(self):
        r = self.built_with(3, home="H99")
        self.assertEqual((r["status"], r["reason"]), ("unresolved", "background-home-unknown:H99"))
        self.assertIsNone(r["age"])

    def test_unknown_life_and_event(self):
        self.assertEqual(self.built_with(3, life="L999")["reason"], "background-life-unknown:L999")
        self.assertEqual(self.built_with(3, event="E999")["reason"], "background-event-unknown:E999")

    def test_a_half_missing_background(self):
        self.assertEqual(self.built_with(3, life="")["reason"], "background-incomplete")
        self.assertEqual(self.built_with(3, event=None)["reason"], "background-incomplete")

    def test_an_id_of_the_wrong_kind(self):
        self.assertEqual(self.built_with(3, home="L01")["reason"], "background-home-unknown:L01")

    def test_no_background_at_all_is_a_normal_person(self):
        self.assertEqual(self.built_with(3)["status"], "resolved")             # documented: no background, no constraints

    def bad_tags(self, entry):
        return mock.patch.dict(people.data()["tags"], {"H01": entry})

    def test_malformed_tag_values_are_unresolved_not_defaults_or_crashes(self):
        for entry in ({"minage": "forty"}, {"minage": True}, {"minage": 4.5}, {"minage": -1}, {"siblings_exact": None},
                      {"parents": "alive"}, {"home": "nowhere"}, {"absence": "yes"}, {"unsupported": ""}, "not a dict",
                      {"figurative": "grandmother"}, {"made_up_tag": 1},
                      [], False, 0, "", None, 1, ["minage"]):           # Codex Method R2 F9: present but falsy is malformed too
            with self.bad_tags(entry):
                with self.assertRaises(people.Unresolved, msg=entry):
                    people.profile(["H01"])

    def test_a_malformed_tag_costs_one_person_not_the_town(self):
        ps, bgs, nat = town(40)
        victims = [i for i in range(40) if bgs[pid(i)]["home"] == "H01"] or [3]
        bgs[pid(victims[0])]["home"] = "H01"
        with self.bad_tags({"minage": "forty"}):
            res = people.build_town("Felucca", "Britain", ps, bgs, nat, PERS, ["Britain", "Trinsic", "Yew"])
        self.assertEqual(res["records"][pid(victims[0])]["status"], "unresolved")
        self.assertIn("malformed tag minage on H01", res["records"][pid(victims[0])]["reason"])
        self.assertGreater(sum(1 for r in res["records"].values() if r["status"] == "resolved"), 30)

    def test_a_removed_nature_key_is_unknown(self):
        with self.assertRaises(people.Unresolved):
            people.profile(["H01", "personality:no-longer-in-the-catalogue"])
        people.profile(["H01", "habit:", "subject:"])                            # "none" is a valid habit / subject


class StructureCatches(unittest.TestCase):
    """Repair B1 (Codex Stage 2 Method R1 F10): each test breaks exactly one structural contract of a good town and the
    REAL audit must name it. The first four are Codex's own reproductions."""

    def setUp(self):
        self.res = build(160)
        self.assertEqual(people.audit(self.res), [])
        self.recs, self.off = self.res["records"], self.res["offstage"]

    def codes(self):
        return {b[0] for b in people.audit(self.res)}

    def unit(self, kind):
        return next(u for u in self.res["units"] if u["kind"] == kind)

    def drop_tie(self, t):
        self.res["ties"] = [x for x in self.res["ties"] if tuple(x) != tuple(t)]

    def test_codex_one_direction_of_a_real_sibling_tie_removed(self):
        a, b = self.unit("U4")["members"]
        self.drop_tie((a, "sibling", b))
        self.assertIn("inverse-missing", self.codes())

    def test_codex_a_parents_child_tie_removed_child_keeps_parent(self):
        par, child = self.unit("U3")["members"]
        self.drop_tie((par, "child", child))
        self.assertIn("inverse-missing", self.codes())

    def test_codex_years_in_town_not_a_whole_number(self):
        r = next(r for r in self.recs.values() if r["status"] == "resolved" and r["years_town"] is not None)
        r["years_town"] = 0.5
        self.assertIn("type-years_town", self.codes())

    def test_codex_a_married_member_with_a_household_that_does_not_exist(self):
        m = self.unit("U1")["members"][0]
        self.recs[m]["household"] = "h-doesnotexist"
        self.assertTrue({"household-missing", "household-record-mismatch"} <= self.codes())

    def test_an_age_that_is_a_boolean(self):
        r = next(r for r in self.recs.values() if r["status"] == "resolved")
        r["age"] = True
        self.assertIn("type-age", self.codes())

    def test_real_siblings_with_different_parents(self):
        a, b = self.unit("U4")["members"]
        old = next(x for x in self.res["ties"] if x[0] == b and x[1] == "parent")
        stranger = next(o for o in self.off if o not in {x[2] for x in self.res["ties"] if x[0] in (a, b)})
        self.res["ties"] = [x if tuple(x) != tuple(old) else (b, "parent", stranger) for x in self.res["ties"]]
        self.assertIn("siblings-parents-differ", self.codes())

    def test_an_offstage_sibling_held_by_only_one_real_sibling(self):
        a, b, extra = next((u["members"][0], u["members"][1], x) for u in self.res["units"] if u["kind"] == "U4"
                           for x in self.res["ties"] if x[0] == u["members"][0] and x[1] == "sibling" and x[2] in self.off)
        self.drop_tie((b, "sibling", extra[2]))
        self.assertIn("siblings-siblings-differ", self.codes())

    def test_real_spouses_with_different_children(self):
        un = next(u for u in self.res["units"] if u["kind"] == "U1" and any(x[0] == u["members"][0] and x[1] == "child" for x in self.res["ties"]))
        a = un["members"][0]
        kid = next(x[2] for x in self.res["ties"] if x[0] == a and x[1] == "child")
        self.drop_tie((a, "child", kid))
        self.assertIn("spouses-children-differ", self.codes())

    def test_a_unit_member_pointing_at_another_unit(self):
        u1, u4 = self.unit("U1"), self.unit("U4")
        self.recs[u1["members"][0]]["unit"] = u4["id"]
        self.assertIn("unit-membership", self.codes())

    def test_a_home_line_the_household_may_not_have(self):
        born_only = next(d["id"] for d in people.data()["lists"]["homes"] if d["who"] == "born")
        hid, h = next((k, h) for k, h in self.res["households"].items()
                      if any(m in self.recs and self.recs[m]["status"] == "resolved" and self.recs[m]["prof"]["home"] == "elsewhere" for m in h["members"]))
        h["home_line"] = born_only
        for m in h["members"]:
            if m in self.recs:
                self.recs[m]["home_line"] = born_only
        self.assertIn("home-line-not-eligible", self.codes())

    def test_a_profile_that_is_not_what_the_catalogue_says(self):
        r = next(r for r in self.recs.values() if r["status"] == "resolved")
        r["prof"] = dict(r["prof"], minage=r["prof"]["minage"] - 1)
        self.assertIn("profile-source", self.codes())

    def test_the_n3_not_applicable_ties_are_counted(self):
        n = sum(1 for a, r_, b in self.res["ties"] if r_ in ("parent", "child") and b in self.off and not self.off[b]["alive"])
        self.assertGreater(n, 0)
        self.assertEqual(people.summary(self.res)["n3_not_applicable"], n)

    def test_a_tie_to_nobody(self):
        r = next(r for r in self.recs.values() if r["status"] == "resolved")
        self.res["ties"].append((r["id"], "sibling", "o-nobody"))
        self.assertIn("tie-to-nobody", self.codes())

    def test_an_offstage_spouse_here_left_out_of_the_home(self):
        a, b = next((x[0], x[2]) for x in self.res["ties"] if x[1] == "spouse" and x[2] in self.off and self.off[x[2]]["alive"] and self.off[x[2]]["place"] == "here")
        for h in self.res["households"].values():
            if b in h["members"]:
                h["members"].remove(b)
        self.assertIn("household-offstage-missing", self.codes())


class AuditCatches(unittest.TestCase):
    """The audit must fail when a rule is broken: each test breaks one thing in a good town."""

    def setUp(self):
        self.res = build(80)
        self.assertEqual(people.audit(self.res), [])
        self.kin = {}
        for a, rel, b in self.res["ties"]:
            self.kin.setdefault(a, []).append((rel, b))

    def codes(self):
        return {b[0] for b in people.audit(self.res)}

    def first(self, want):
        for a, rels in self.kin.items():
            for rel, b in rels:
                if want(a, rel, b):
                    return a, b
        self.fail("the sample holds no such tie")

    def test_spouses_too_far_apart(self):
        a, b = self.first(lambda a, rel, b: rel == "spouse" and b in self.res["offstage"] and self.res["offstage"][b]["alive"])
        self.res["offstage"][b]["age"] = self.res["records"][a]["age"] + 20
        self.assertIn("N2", self.codes())

    def test_a_dead_person_with_an_age(self):
        o = next(o for o in self.res["offstage"].values() if not o["alive"])
        o["age"] = 50
        self.assertIn("N7-dead-with-age", self.codes())

    def test_a_parent_too_young(self):
        a, b = self.first(lambda a, rel, b: rel == "parent" and b in self.res["offstage"] and self.res["offstage"][b]["alive"])
        self.res["offstage"][b]["age"] = self.res["records"][a]["age"] + 10
        self.assertIn("N3", self.codes())

    def test_a_working_relative_in_a_fixed_trade_family(self):
        a, b = self.first(lambda a, rel, b: b in self.res["offstage"] and self.res["offstage"][b]["alive"] and self.res["offstage"][b]["_age"] >= 18)
        self.res["records"][a]["prof"]["family_trade"] = True
        self.res["offstage"][b]["fate"] = "F001"
        self.assertIn("profile-family-trade", self.codes())


    def test_a_wrong_years_in_town_number_on_someone_born_here(self):
        """Decision 8: born here, the number is the whole life less any stated time away - nothing else."""
        r = next(r for r in self.res["records"].values() if r["status"] == "resolved" and r["prof"]["home"] == "here"
                 and r["years_town"] is not None)
        r["years_town"] -= 1
        self.assertIn("N8", self.codes())

    def test_a_missing_number_where_the_sentences_fix_it(self):
        r = next(r for r in self.res["records"].values() if r["status"] == "resolved" and r["prof"]["home"] == "here"
                 and r["years_town"] is not None)
        r["years_town"] = None
        self.assertIn("N8-born-number", self.codes())

    def test_a_number_where_a_sentence_leaves_it_open(self):
        r = next(r for r in self.res["records"].values() if r["status"] == "resolved" and r["prof"]["home"] == "here"
                 and r["years_town"] is not None)
        r["prof"] = dict(r["prof"], years_open=True)
        self.assertIn("N8", self.codes())

    def test_a_real_tie_across_units(self):
        us = [u for u in self.res["units"]]
        a, b = us[0]["members"][0], us[1]["members"][0]
        self.res["ties"].append((a, "sibling", b))
        self.assertIn("real-tie-across-units", self.codes())


if __name__ == "__main__":
    unittest.main()
