"""5c-2 2b relationship dials. Run: python3 test_dials.py
Design: wiki Projects/Avallone-Memory-Affinity-Design.md 'Step 2b - relationship dials: DESIGN v1' + B12 (Codex 2b review)."""
import os
import tempfile
import unittest

import dials
import memory
import rumours

CFG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "rumours.json")
NPC, NPC2 = "11111111-1111-1111-1111-111111111111", "44444444-4444-4444-4444-444444444444"
WREN, OTHER = "22222222-2222-2222-2222-222222222222", "33333333-3333-3333-3333-333333333333"
H = 3600
NEAR_BRITAIN = {"map": "Trammel", "x": 1492, "y": 1641}


class Rig(unittest.TestCase):
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

    def put(self, kind, facts, npc=NPC, char=WREN):
        self.seq += 1
        acked, rej = self.led.ingest([{"event_id": f"1-{self.seq}", "epoch": 1, "seq": self.seq, "npc_id": npc,
                                       "char_id": char, "kind": kind, "facts": facts, "at_utc": "2026-11-01T16:00:00Z",
                                       "schema": memory.SCHEMA_VERSION}])
        self.assertEqual(rej, [], (kind, facts))
        return f"1-{self.seq}"

    def deed(self, typ, pt, char=WREN, **extra):
        return self.put("deed", {"type": typ, "subject": "dragon", "count": 1, "who": "Racer X", "descriptor": "a tamer",
                                 "place": "Destard", "pt": pt, **NEAR_BRITAIN, **extra}, npc=rumours.WORLD_ID, char=char)

    def req(self, karma=0, fame=0, hour=12, moon="WaxingGibbous", town="Britain", pt=10 * H, mpt=10 * H, npc=NPC, char=WREN,
            persona="townsfolk", role="provisioner", trade="vendor", epoch=1):
        return {"npc_id": npc, "subject_id": char, "persona_key": persona,
                "persona_vars": {"role": role, "trade": trade},
                "standing": {"karma": karma, "fame": fame, "hour": hour, "moon": moon, "town": town, "pt": pt, "mpt": mpt,
                             "epoch": epoch}}


class Traits(unittest.TestCase):
    def test_stable_per_npc_and_different_between_npcs(self):
        a, b = dials.traits(NPC, town="Britain"), dials.traits(NPC, town="Britain")
        self.assertEqual(a, b)
        self.assertNotEqual(dials.traits(NPC2, town="Britain")["trust_T"], a["trust_T"])
        lo, hi = dials.cfg()["trust"]["T"]
        self.assertTrue(lo <= a["trust_T"] <= hi)

    def test_inverted_share_is_higher_in_shady_places(self):
        ids = [f"{i:08d}-0000-4000-8000-000000000000" for i in range(400)]
        town = sum(dials.traits(i, town="Britain")["trust_lean"] < 0 for i in ids) / len(ids)
        den = sum(dials.traits(i, town="Buccaneer's Den")["trust_lean"] < 0 for i in ids) / len(ids)
        self.assertLess(town, 0.2)
        self.assertGreater(den, 0.55)

    def test_persona_overrides(self):
        self.assertEqual(dials.traits(NPC, persona_key="mirabel")["warmth_rest"], 10.0)
        self.assertFalse(dials.traits(NPC, persona_key="mirabel")["superstitious"])


class Shape(unittest.TestCase):
    def test_line_value_stranger_below_the_line_distrust_only_from_negative_karma(self):
        self.assertEqual(dials.line_value(0, 3000, 2000, 1, 2500), 0.0)        # a new character: a stranger, not a suspect
        self.assertEqual(dials.line_value(3000, 3000, 2000, 1, 2500), 0.0)
        vals = [dials.line_value(k, 3000, 2000, 1, 2500) for k in range(-15000, 15001, 1000)]
        self.assertEqual(vals, sorted(vals))
        self.assertAlmostEqual(dials.line_value(5000, 3000, 2000, 1, 2500), 76.159, places=2)
        self.assertAlmostEqual(dials.line_value(-2500, 3000, 2000, 1, 2500), -76.159, places=2)
        self.assertAlmostEqual(dials.line_value(-2500, 3000, 2000, -1, 2500), 76.159, places=2)   # inverted
        self.assertEqual(dials.line_value(-9000, 500, 1500, 1), 0.0)            # respect: no Wneg, never below 0
        self.assertLess(dials.line_value(9000, 500, 1500, -1), -90)             # the cynic sneers at the famous

    def test_ambience_night_and_moon_only_for_the_superstitious(self):
        self.assertEqual(dials.ambience(12, 1.0, False, 0.5, 0.2), 0.0)       # not superstitious: the moons do nothing
        self.assertEqual(dials.ambience(23, 1.0, False, 0.5, 0.2), -5.0)      # night, for everyone
        self.assertEqual(dials.ambience(12, 0.45, True, 0.5, 0.2), 0.0)       # below their line: nothing
        self.assertAlmostEqual(dials.ambience(12, 0.7, True, 0.5, 0.2), -11.4, places=1)   # past it: spooked
        self.assertGreaterEqual(dials.ambience(23, 1.0, True, 0.4, 0.1), -20.0)

    def test_two_moons_multiply(self):
        # the owner, chat, 2026-11-02: the slow real moon x an 18 h moon. A real new moon (2000-01-06 18:14 UTC) is dark.
        m = dials.cfg()["ambience"]["moons"]
        new_real = m["real_new_epoch"]
        real, fast, glow = dials.moon_glow(new_real)
        self.assertAlmostEqual(real, 0.0, places=6)
        self.assertAlmostEqual(glow, 0.0, places=6)
        full_real = new_real + m["real_days"] * 86400 / 2
        real, fast, glow = dials.moon_glow(full_real)
        self.assertAlmostEqual(real, 1.0, places=6)
        self.assertAlmostEqual(glow, fast, places=6)
        # the fast moon goes round once every 18 h: full 9 h after its own new moon
        self.assertAlmostEqual(dials.moon_glow(m["fast_new_epoch"] + 9 * 3600)[1], 1.0, places=6)
        self.assertAlmostEqual(dials.moon_glow(m["fast_new_epoch"] + 18 * 3600)[1], 0.0, places=6)


    def test_bands_and_cue(self):
        d = {"familiarity": 0, "warmth": 5, "trust": -30, "respect": 70}
        line = dials.cue(d)
        self.assertIn("never changes prices", line)
        self.assertIn("stranger", line)
        self.assertIn("wary", line)
        self.assertIn("say little", line)
        self.assertIn("high regard", line)
        self.assertIn("give normal service", dials.band(-90, "warmth"))   # the lowest band is never "hate"


class Gossip(unittest.TestCase):
    def test_wary_keeps_gossip_cool_volunteers_nothing(self):
        d = lambda t, w: {"trust": t, "warmth": w, "familiarity": 5}   # knows them (stranger rule: class GossipHold)
        self.assertIsNotNone(dials.holds_gossip(d(-30, 10), "news"))     # wary: not even when asked
        self.assertIsNotNone(dials.holds_gossip(d(-30, 10), "greet"))
        self.assertIsNotNone(dials.holds_gossip(d(0, -30), "greet"))     # cool: nothing volunteered
        self.assertIsNone(dials.holds_gossip(d(0, -30), "news"))         # ...but asked, it answers
        self.assertIsNone(dials.holds_gossip(d(0, 10), "greet"))


class Unease(Rig):
    def test_only_stories_that_reached_the_town_count_and_not_the_log(self):
        self.deed("murder", 10 * H)
        live = dials.Exposure(self.r).live_in("Britain", 10 * H)
        self.assertEqual(len(live), 1)                                        # local at once
        self.assertEqual(self.led.rdb.execute("SELECT COUNT(*) FROM rumour_arrivals").fetchone()[0], 0)  # no log tick
        self.assertEqual(dials.Exposure(self.r).live_in("Zento", 10 * H + 600), [])   # not arrived yet
        self.assertGreater(dials.unease(live, 10 * H), 0.5)
        self.assertEqual(dials.Exposure(self.r).live_in("Britain", 10 * H - 1), [])  # clock rewound: not happened yet

    def test_story_lifetime_withdrawal_and_deletion_end_the_effect(self):
        rid = self.deed("died_notable", 10 * H)                              # lifetime 12 h
        self.assertTrue(dials.Exposure(self.r).live_in("Britain", 11 * H))
        self.assertEqual(dials.Exposure(self.r).live_in("Britain", 22 * H + 1), [])
        self.r.drop(rid, "2026-11-01T17:00:00Z")
        self.assertEqual(dials.Exposure(self.r).live_in("Britain", 11 * H), [])
        self.deed("died_notable", 10 * H, char=OTHER)
        self.assertTrue(dials.Exposure(self.r).live_in("Britain", 11 * H))
        self.put("character_deleted", {}, npc=rumours.WORLD_ID, char=OTHER)
        self.assertEqual(dials.Exposure(self.r).live_in("Britain", 11 * H), [])

    def test_repeats_by_one_character_cannot_hold_a_town_on_edge(self):
        live = [({"type": "died_notable", "char": WREN, "facts": {}}, 10 * H + i * 600, 22 * H) for i in range(20)]
        u = dials.unease(live, 10 * H + 20 * 600)
        self.assertLess(u, 0.3 * 1.75 + 1e-9)                                 # 1 + 0.5 + 0.25, never more
        many = [({"type": "died_notable", "char": f"c{i}", "facts": {}}, 10 * H, 22 * H) for i in range(20)]
        self.assertEqual(dials.unease(many, 10 * H), 1.0)                      # clamped
        good = live[:1] + [({"type": "kill_notable", "char": OTHER, "facts": {}}, 10 * H, 34 * H)]
        self.assertEqual(dials.unease(good, 10 * H), 0.0)                      # good news settles it

    def test_gm_weight_typed_and_bounded(self):
        mk = lambda v: [({"type": "gm_planted", "char": WREN, "facts": {"unease": v}}, 10 * H, 34 * H)]
        self.assertEqual(dials.unease(mk(0.4), 10 * H), 0.4)
        self.assertEqual(dials.unease(mk(7), 10 * H), 1.0)
        for bad in ("lots", None, float("nan"), True):
            self.assertEqual(dials.unease(mk(bad), 10 * H), 0.0)


class Compute(Rig):
    def test_trust_follows_karma_and_the_environment_is_capped(self):
        new = dials.compute(self.req(karma=0), self.led, self.r)
        self.assertEqual(new["sources"]["trust_base"], 0.0)                     # the owner: a stranger until past the NPC's line
        lo = dials.compute(self.req(karma=-15000), self.led, self.r)["trust"]
        hi = dials.compute(self.req(karma=15000), self.led, self.r)["trust"]
        lean = dials.traits(NPC, "townsfolk", "provisioner", "vendor", "Britain")["trust_lean"]
        self.assertTrue(hi > lo if lean > 0 else hi < lo)
        for _ in range(3):
            self.deed("murder", 10 * H, char=OTHER)
        m = dials.cfg()["ambience"]["moons"]
        d = dials.compute(self.req(karma=0, hour=23), self.led, self.r, now=m["real_new_epoch"] + m["real_days"] * 43200)
        self.assertGreaterEqual(d["sources"]["trust_env"], -20.0)
        self.assertTrue(-100 <= d["trust"] <= 100)

    def test_respect_counts_this_players_arrived_deeds_up_to_the_cap(self):
        for i in range(5):
            self.deed("kill_notable", 10 * H, subject=f"wyrm{'abcde'[i]}")
        d = dials.compute(self.req(fame=0), self.led, self.r)
        self.assertEqual(d["sources"]["deeds"], 15.0)
        self.assertEqual(dials.compute(self.req(fame=0, char=OTHER), self.led, self.r)["sources"]["deeds"], 0.0)

    def test_warmth_one_per_day_fades_and_ignores_pre_2b_rows(self):
        rest = dials.traits(NPC, "townsfolk", "provisioner", "vendor", "Britain")["warmth_rest"]
        self.put("visited", {"day": "2026-10-30"})                            # pre-2b: familiarity only
        self.put("visited", {"day": "2026-11-01", "mpt": 10 * H})
        self.put("visited", {"day": "2026-11-01", "mpt": 10 * H + 60})       # same day: once
        self.put("traded", {"day": "2026-11-01", "mpt": 10 * H})
        d = dials.compute(self.req(mpt=10 * H), self.led, self.r)
        self.assertEqual(d["familiarity"], 2)
        self.assertAlmostEqual(d["warmth"], max(-100, min(100, rest + 3)), places=3)
        later = dials.compute(self.req(mpt=10 * H + 24 * H), self.led, self.r)  # one half-life of memory time
        self.assertAlmostEqual(later["warmth"], max(-100, min(100, rest + 1.5)), places=3)
        self.assertEqual(dials.compute(self.req(char=OTHER, mpt=10 * H), self.led, self.r)["familiarity"], 0)

    def test_gifts_warm_less_each_recent_time_and_likes_reach_the_cue(self):
        rest = dials.traits(NPC, "townsfolk", "provisioner", "vendor", "Britain")["warmth_rest"]
        self.put("gift_given", {"day": "2026-11-01", "mpt": 10 * H, "kind": "liked", "value": "fair"})
        self.put("gift_given", {"day": "2026-11-02", "mpt": 11 * H, "kind": "gold", "value": "generous"})
        r = self.req(mpt=11 * H)
        r["standing"]["likes"] = "a fresh fish"
        d = dials.compute(r, self.led, self.r)
        first = 4 * 1.0 * 0.5 ** (1 / 24)                                        # liked, an hour faded
        second = 4 * 1.0 / 2                                                      # generous, but the 2nd in 72 h
        self.assertAlmostEqual(d["warmth"], max(-100, min(100, rest + first + second)), places=3)
        self.assertIn("If they ask what you like", dials.cue(d))
        self.assertIn("a fresh fish", dials.cue(d))

    def test_nothing_without_standing_or_for_a_deleted_character(self):
        r = self.req()
        del r["standing"]
        self.assertIsNone(dials.compute(r, self.led, self.r))
        self.put("character_deleted", {}, npc=rumours.WORLD_ID, char=WREN)
        self.assertIsNone(dials.compute(self.req(), self.led, self.r))

    def test_bad_inputs_are_clamped_not_trusted(self):
        r = self.req()
        r["standing"].update(karma="lots", fame=1e9, hour=99, moon="x" * 500, pt=-5)
        d = dials.compute(r, self.led, self.r)
        self.assertEqual(d["sources"]["karma"], 0)
        self.assertEqual(d["sources"]["fame"], 15000)
        self.assertEqual(d["sources"]["hour"], 23)


class Conduct(Rig):
    def test_hateful_language_cools_once_a_day_and_fades(self):
        # design B4: -20 start, once per NPC + character + day (the shard sends at most one; two rows still count once)
        d0 = dials.compute(self.req(), self.led, self.r)["warmth"]
        for _ in (1, 2):
            self.put("player_used_hateful_language", {"day": "2026-11-02", "mpt": 10 * H})
        d1 = dials.explain(self.req(), self.led, self.r)
        self.assertAlmostEqual(d1["warmth"], round(d0 - 20, 1), places=1)
        self.assertTrue(any(i["kind"] == "player_used_hateful_language" for i in d1["detail"]["warmth_items"]))
        later = dials.compute(self.req(mpt=34 * H), self.led, self.r)["warmth"]   # one half-life (24 memory h) later
        self.assertAlmostEqual(later, d0 - 10, places=0)

    def test_the_kind_takes_no_text(self):
        with self.assertRaises(ValueError):
            memory.check_named("player_used_hateful_language", {"day": "2026-11-02", "mpt": 5, "line": "x"})
        memory.check_named("player_used_hateful_language", {"day": "2026-11-02", "mpt": 5})

    def test_gm_plant_unease_is_checked(self):
        base = {"type": "gm_planted", "map": "Trammel", "town": "Britain", "text": "a ghost walks the docks"}
        memory.check_deed(dict(base, unease=0.5))
        for bad in (2, "high", True):
            with self.assertRaises(ValueError):
                memory.check_deed(dict(base, unease=bad))
        with self.assertRaises(ValueError):
            memory.check_deed({"type": "murder", "map": "Trammel", "unease": 0.5})


class Reconciled(Rig):
    """Codex 2b code review F1: dials only for the epoch that reconciled successfully, never during or after a conflict."""

    def test_only_the_reconciled_epoch_reads_the_ledger(self):
        self.assertIsNotNone(dials.compute(self.req(epoch=1), self.led, self.r))
        self.assertIsNone(dials.compute(self.req(epoch=2), self.led, self.r))      # reconcile for 2 not done yet
        r = self.req()
        del r["standing"]["epoch"]
        self.assertIsNone(dials.compute(r, self.led, self.r))                     # no epoch: nothing
        self.put("visited", {"day": "2026-11-02", "mpt": 10 * H})
        self.assertEqual(self.led.reconcile(1, 0, 0)[0], "conflict")             # epoch went backwards
        self.assertIsNone(dials.compute(self.req(epoch=1), self.led, self.r))      # the conflict is now the latest
        self.assertIsNone(dials.explain(self.req(epoch=1), self.led, self.r))

    def test_gifts_count_once_a_day_and_the_window_slides(self):
        base = dials.compute(self.req(), self.led, self.r)["warmth"]
        self.put("gift_given", {"day": "2026-11-02", "mpt": 10 * H, "kind": "liked", "value": "fair"})
        one = dials.compute(self.req(), self.led, self.r)["warmth"]
        self.put("gift_given", {"day": "2026-11-02", "mpt": 10 * H, "kind": "liked", "value": "fair"})
        self.assertEqual(dials.compute(self.req(), self.led, self.r)["warmth"], one)   # same day: once (Codex F4)
        self.assertAlmostEqual(one - base, 4.0, places=1)


class Moons(Rig):
    def test_superstitious_npc_is_spooked_only_when_both_moons_are_bright(self):
        m = dials.cfg()["ambience"]["moons"]
        both_full = m["real_new_epoch"] + m["real_days"] * 86400 / 2
        while abs(dials.moon_glow(both_full)[1] - 1.0) > 1e-3:   # walk to the fast moon's next full, inside the real full
            both_full += 60
        dark = m["real_new_epoch"] + 29.530588853 * 86400 * 10
        sup = next(f"npc-{i}" for i in range(500) if dials.traits(f"npc-{i}")["superstitious"])
        calm = next(f"npc-{i}" for i in range(500) if not dials.traits(f"npc-{i}")["superstitious"])
        for npc, spooked in ((sup, True), (calm, False)):
            bright = dials.compute(self.req(npc=npc), self.led, self.r, now=both_full)
            dim = dials.compute(self.req(npc=npc), self.led, self.r, now=dark)
            self.assertEqual(dim["sources"]["ambience"], 0.0)
            self.assertEqual(bright["sources"]["ambience"] < 0, spooked, npc)
            self.assertIn("glow", bright["sources"]["moon"])
        self.assertIn("spooked above", dials.compute(self.req(npc=sup), self.led, self.r, now=dark)["sources"]["moon"])


class Why(Rig):
    """[Named why] (design B8): the breakdown adds up to the same dials the model is given."""

    def test_breakdown_matches_compute_and_lists_each_warmth_row(self):
        self.put("visited", {"day": "2026-11-01", "mpt": 9 * H})
        self.put("gift_given", {"day": "2026-11-01", "mpt": 9 * H, "kind": "liked", "value": "fair"})
        plain = dials.compute(self.req(karma=500, fame=200), self.led, self.r)
        why = dials.explain(self.req(karma=500, fame=200), self.led, self.r)
        for k in ("warmth", "trust", "respect"):
            self.assertAlmostEqual(why[k], plain[k], places=1)
        items = why["detail"]["warmth_items"]
        self.assertEqual([i["kind"] for i in items], ["visited", "gift"])
        self.assertAlmostEqual(sum(i["now"] for i in items), plain["sources"]["warmth_events"], places=1)
        self.assertEqual(set(why["bands"]), {"familiarity", "warmth", "trust", "respect"})
        self.assertIn("trust_T", why["detail"]["traits"])
        self.assertNotIn("detail", plain)                     # the normal request path is unchanged

    def test_short_band_edges(self):
        self.assertEqual(dials.short_band(-61, "warmth"), "hostile")
        self.assertEqual(dials.short_band(0, "trust"), "as a stranger")
        self.assertEqual(dials.short_band(60, "respect"), "high regard")
        self.assertEqual(dials.short_band(0, "familiarity"), "stranger")
        self.assertEqual(dials.short_band(12, "familiarity"), "regular")



class GossipHold(unittest.TestCase):
    """the owner, chat, 2026-11-02: strangers volunteer nothing; asked-for news is unaffected by familiarity."""

    def d(self, familiarity, warmth=5.0, trust=0.0):
        return {"familiarity": familiarity, "warmth": warmth, "trust": trust}

    def test_strangers_volunteer_nothing_but_answer_when_asked(self):
        self.assertIn("does not know them", dials.holds_gossip(self.d(0), "greet"))
        self.assertIn("does not know them", dials.holds_gossip(self.d(2), "greet"))
        self.assertIsNone(dials.holds_gossip(self.d(3), "greet"))      # "by sight"
        self.assertIsNone(dials.holds_gossip(self.d(0), "news"))

    def test_stranger_pass_lets_a_greeting_hint_through(self):   # Step B (the owner, 2026-11-08)
        self.assertIsNone(dials.holds_gossip(self.d(0), "greet", stranger_pass=True))
        self.assertIn("wary", dials.holds_gossip(self.d(0, trust=-21), "greet", stranger_pass=True))   # wary still holds
        self.assertIn("cool", dials.holds_gossip(self.d(0, warmth=-21), "greet", stranger_pass=True))  # cool still holds

    def test_stranger_rate_is_one_sixth(self):
        import random
        import mood
        self.assertAlmostEqual(dials.greet_scale(self.d(2)), 0.1667)
        self.assertEqual(dials.greet_scale(self.d(3)), 1.0)
        self.assertEqual(dials.greet_scale(None), 1.0)
        m = {"band": "content"}
        base = mood.greet_percent(m)
        rng, n = random.Random(7), 60000
        known = sum(mood.greet_draw(m, rng) for _ in range(n)) / n * 100
        stranger = sum(mood.greet_draw(m, rng, scale=dials.greet_scale(self.d(1))) for _ in range(n)) / n * 100
        self.assertAlmostEqual(known, base, delta=0.6)            # positive control: the normal rate
        self.assertAlmostEqual(stranger, base / 6, delta=0.35)    # ~1/6 of it

    def test_wary_holds_everything_asked_or_not(self):
        self.assertIn("wary", dials.holds_gossip(self.d(10, trust=-21), "news"))
        self.assertIn("wary", dials.holds_gossip(self.d(10, trust=-21), "greet"))


if __name__ == "__main__":
    unittest.main()
