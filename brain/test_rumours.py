"""Phase 5a rumour network tests. Run: python3 test_rumours.py"""
import os
import tempfile
import unittest

import memory
import rumours

CFG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "rumours.json")
WREN, OTHER = "22222222-2222-2222-2222-222222222222", "33333333-3333-3333-3333-333333333333"
H = 3600


def _letters(i):
    """0 -> "a", 25 -> "z", 26 -> "ba": test subjects must be plain words (no digits)."""
    out = ""
    while True:
        out = "abcdefghijklmnopqrstuvwxyz"[i % 26] + out
        i //= 26
        if not i:
            return out
# Destard's entrance is near Trinsic's road; Britain's sewer door is in Britain.
NEAR_BRITAIN = {"map": "Trammel", "x": 1492, "y": 1641}
NEAR_YEW = {"map": "Trammel", "x": 560, "y": 1000}


class RumourTests(unittest.TestCase):
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

    def deed(self, typ, pt, char=WREN, who="Racer X", subject="dragon", place="Destard", where=None, **extra):
        self.seq += 1
        facts = {"type": typ, "subject": subject, "count": 1, "who": who, "descriptor": "a tamer", "place": place,
                 "pt": pt, **(where or NEAR_BRITAIN), **extra}
        acked, rej = self.led.ingest([{
            "event_id": f"1-{self.seq}", "epoch": 1, "seq": self.seq, "npc_id": rumours.WORLD_ID, "char_id": char,
            "kind": "deed", "facts": facts, "at_utc": "2026-10-28T16:00:00Z", "schema": memory.SCHEMA_VERSION}])
        self.assertEqual(rej, [])
        return f"1-{self.seq}"

    def towns(self, pt, rid=None):
        return {r["town"] for r in self.r.view(pt) if rid is None or r["id"] == rid}

    def test_starts_local_then_spreads_on_played_time(self):
        rid = self.deed("kill_notable", 10 * H)
        self.assertEqual(self.towns(10 * H, rid), {"Britain"})
        later = self.towns(12 * H, rid)
        self.assertIn("Trinsic", later)          # a neighbour within ~1-1.25 played hours
        self.assertNotIn("Zento", self.towns(10 * H + 600, rid))
        self.assertEqual(self.towns(10 * H - 1, rid), set())  # clock rewound (world restore) -> not yet happened

    def test_done_test_same_evening(self):
        rid = self.deed("kill_notable", 0)
        self.assertGreaterEqual(len(self.towns(3 * H, rid)), 5)   # a play evening reaches several towns

    def test_deterministic(self):
        rid = self.deed("kill_notable", 0)
        a = [(r["town"], r["text"], r["arrived_pt"]) for r in self.r.view(6 * H) if r["id"] == rid]
        b = [(r["town"], r["text"], r["arrived_pt"]) for r in self.r.view(6 * H) if r["id"] == rid]
        self.assertEqual(a, b)

    def test_farming_is_one_growing_rumour(self):
        for i in range(20):
            self.deed("kill_notable", i * 300)
        rows = [r for r in self.r.view(2 * H) if r["town"] == "Britain"]
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["events"], 20)
        self.assertIn("dragons", rows[0]["text"])

    def test_rolled_back_deed_unmakes_its_rumour(self):
        self.deed("kill_notable", 0)
        self.led.ingest([])
        # World restored to a save that held nothing: the reconciliation rolls the deed back.
        verdict, rolled, _ = self.led.reconcile(2, 1, 0)
        self.assertEqual(rolled, 1)
        self.assertEqual(self.r.view(5 * H), [])

    def test_restore_after_spread_and_garbling(self):
        # Codex 2026-10-28 finding 1 (b)+(c): a rumour that already hopped and was garbled must vanish after a restore.
        self.deed("kill_notable", 0)                       # saved world holds this one (1-1)
        rid = self.deed("provoked", 0, subject="lich", other="drake")  # after the save (1-2); a funny kind - only those garble
        spread = [r for r in self.r.view(8 * H) if r["id"] == rid]
        self.assertGreater(len(spread), 5)
        self.assertTrue(any(r["garbles"] for r in spread))
        self.r.tick(8 * H)                                  # arrivals logged
        _, rolled, _ = self.led.reconcile(2, 1, 1)          # world restored to the save holding 1-1 only
        self.assertEqual(rolled, 1)
        for pt in (0, 8 * H, 20 * H):
            self.assertFalse([r for r in self.r.view(pt) if r["id"] == rid])
        self.assertFalse([r for r in self.r.tick(20 * H) if r["id"] == rid])
        self.assertTrue([r for r in self.r.view(8 * H) if r["id"] == "1-1"])

    def test_never_another_players_name(self):
        self.deed("kill_notable", 0, char=WREN, who="Racer X")
        self.deed("died_pathetic", 0, char=OTHER, who="Bob", subject="chicken", where=NEAR_YEW, place="Yew")
        for pt in range(0, 30 * H, 1800):
            for r in self.r.view(pt):
                if r["char"] == WREN:
                    self.assertNotIn("Bob", r["text"])
                else:
                    self.assertNotIn("Racer X", r["text"])

    def test_garbling_is_mild_and_bounded(self):
        seen = set()
        for i in range(60):
            self.deed("provoked", i * 30 * H, subject="beast " + _letters(i), other="drake")
        for i in range(60):
            for r in self.r.view(i * 30 * H + 10 * H):
                self.assertLessEqual(len(r["garbles"]), 2)
                seen.update(r["garbles"])
                if not r["garbles"]:
                    self.assertTrue(r["text"].startswith("Racer X"), r["text"])
        self.assertTrue(seen <= set(rumours.DISTORTIONS))
        self.assertGreater(len(seen), 1)      # garbling does happen

    # ---- rumour step, Part B (the owner, chat, 2026-11-11): serious stories and a player's own death never change ----
    SERIOUS = ("murder", "died_notable", "kill_notable", "tame_notable", "pacified", "fished_bottle", "died_pathetic")

    def test_serious_kinds_render_the_same_at_every_hop(self):
        for n, typ in enumerate(self.SERIOUS):
            towns, hops = set(), 0
            for i in range(6):
                t0 = (n * 6 + i) * 60 * H
                rid = self.deed(typ, t0, subject="beast " + _letters(i), other="drake")
                rows = [r for dt in (0, 2 * H, 5 * H, 8 * H, 11 * H) for r in self.r.view(t0 + dt) if r["id"] == rid]
                self.assertEqual(len({r["text"] for r in rows}), 1, (typ, sorted({r["text"] for r in rows})))
                self.assertFalse([r for r in rows if r["garbles"]], typ)
                towns |= {r["town"] for r in rows}
                hops = max([hops] + [r["hops"] for r in rows])
            self.assertGreater(len(towns), 6, typ)      # they did travel ...
            self.assertGreater(hops, 2, typ)            # ... several hops out

    def test_funny_kinds_still_change(self):
        for i in range(40):
            self.deed("drunk", i * 30 * H)
        self.assertTrue(any(r["garbles"] for i in range(40) for r in self.r.view(i * 30 * H + 7 * H)))

    def _bad_file(self, change):
        import json as _json
        with open(CFG, encoding="utf-8") as fh:
            cfg = _json.load(fh)
        change(cfg["deeds"])
        path = os.path.join(self.dir.name, "bad.json")
        with open(path, "w", encoding="utf-8") as fh:
            _json.dump(cfg, fh)
        return path

    def test_load_refuses_a_serious_kind_with_a_distort_list(self):
        for typ in self.SERIOUS:
            path = self._bad_file(lambda d, t=typ: d[t].update(distort=["place_drift"]))
            with self.assertRaises(ValueError, msg=typ):
                rumours.Rumours(self.led, path)

    def test_both_death_kinds_stay_unchanged_even_if_flagged_funny(self):
        for typ in rumours.PLAYER_DEATHS:
            path = self._bad_file(lambda d, t=typ: d[t].update(funny=True, distort=["place_drift"]))
            with self.assertRaises(ValueError, msg=typ):
                rumours.Rumours(self.led, path)
        # and behind the load check: the code itself gives a death no garbles even if a list got through
        self.r.cfg["deeds"]["died_pathetic"]["distort"] = ["place_drift"]
        rid = self.deed("died_pathetic", 0, subject="chicken")
        self.assertFalse([r for r in self.r.view(10 * H) if r["id"] == rid and r["garbles"]])

    def test_a_bad_file_is_refused_on_a_reload_too(self):
        import json as _json, shutil
        path = os.path.join(self.dir.name, "live.json")
        shutil.copy(CFG, path)
        r = rumours.Rumours(self.led, path)
        with open(path, encoding="utf-8") as fh:
            cfg = _json.load(fh)
        cfg["deeds"]["murder"]["distort"] = ["count_up"]
        with open(path, "w", encoding="utf-8") as fh:
            _json.dump(cfg, fh)
        os.utime(path, (1, 1))                                  # a different mtime, whatever the clock's grain
        with self.assertRaises(ValueError):
            r._load()
        self.assertEqual(r.cfg["deeds"]["murder"]["distort"], [])   # the last good settings are still the ones in use
        with self.assertRaises(ValueError):                     # and it is refused again, not adopted on the second look
            r._load()

    def test_the_shipped_file_has_no_serious_distort_list(self):
        for name, d in self.r.cfg["deeds"].items():
            if d.get("distort"):
                self.assertTrue(d.get("funny") is True and name not in rumours.PLAYER_DEATHS, name)
        for typ in self.SERIOUS:
            self.assertEqual(self.r.cfg["deeds"][typ]["distort"], [], typ)

    def test_origin_hop_is_never_garbled(self):
        rid = self.deed("kill_notable", 0)
        here = [r for r in self.r.view(0) if r["id"] == rid]
        self.assertEqual(here[0]["garbles"], [])
        self.assertEqual(here[0]["text"], "Racer X slew a dragon near Destard")

    def test_fades(self):
        rid = self.deed("drunk", 0, subject="", place="Britain")
        self.assertTrue(self.towns(1 * H, rid))
        self.assertEqual(self.towns(9 * H, rid), set())   # drunk lives 8 played hours

    def test_one_live_joke_per_type_per_town(self):
        self.deed("died_pathetic", 0, subject="chicken", place="Britain")
        self.deed("died_pathetic", 600, subject="rat", place="Britain")
        rows = [r for r in self.r.view(H) if r["town"] == "Britain" and r["type"] == "died_pathetic"]
        self.assertEqual(len(rows), 1)
        self.assertIn("rat", rows[0]["text"])   # the freshest

    def test_town_cap(self):
        for i in range(9):
            self.deed("kill_notable", i, subject="beast " + _letters(i))
        self.assertEqual(len([r for r in self.r.view(H) if r["town"] == "Britain"]), 5)

    def test_gm_drop(self):
        rid = self.deed("kill_notable", 0)
        self.r.drop(rid, "2026-10-28T16:00:00Z")
        self.assertEqual(self.towns(5 * H, rid), set())

    def test_planted_starts_in_named_town(self):
        rid = self.deed("gm_planted", 0, subject="", text="The bell under Vesper rang twice.", town="Vesper")
        self.assertEqual(self.towns(0, rid), {"Vesper"})
        self.assertEqual([r["text"] for r in self.r.view(0)], ["The bell under Vesper rang twice."])

    def test_disabled_type_is_silent(self):
        self.r.cfg["deeds"]["drunk"]["enabled"] = False
        self.deed("drunk", 0, subject="")
        self.assertEqual(self.r.view(H), [])

    def test_epithets(self):
        for i in range(3):
            self.deed("kill_notable", i * 10 * H)
        self.assertEqual(self.r.epithets(40 * H).get(WREN), ["Dragonbane"])
        self.deed("died_pathetic", 0, subject="chicken")
        self.deed("died_pathetic", 20 * H, subject="chicken")
        self.assertIn("Chicken-Fodder", self.r.epithets(40 * H)[WREN])

    def test_funny_epithet_fades_heroic_stays(self):
        for i in range(3):
            self.deed("kill_notable", i * H)
        self.deed("died_pathetic", 0, subject="chicken")
        self.deed("died_pathetic", H, subject="chicken")
        self.assertIn("Chicken-Fodder", self.r.epithets(10 * H)[WREN])
        self.assertEqual(self.r.epithets(60 * H)[WREN], ["Dragonbane"])  # past the 50 h window
        self.deed("died_pathetic", 59 * H, subject="chicken")  # one fresh death does not earn it back
        self.assertEqual(self.r.epithets(60 * H)[WREN], ["Dragonbane"])

    # ---- Codex code review 2026-10-28 ----
    def ingest_raw(self, facts):
        self.seq += 1
        return self.led.ingest([{
            "event_id": f"1-{self.seq}", "epoch": 1, "seq": self.seq, "npc_id": rumours.WORLD_ID, "char_id": WREN,
            "kind": "deed", "facts": facts, "at_utc": "2026-10-28T16:00:00Z", "schema": memory.SCHEMA_VERSION}])

    def test_deed_schema_rejects_bad_facts(self):
        good = {"type": "kill_notable", "subject": "dragon", "count": 1, "who": "Racer X", "descriptor": "a tamer",
                "place": "Destard", "map": "Trammel", "x": 1, "y": 1, "pt": 0}
        self.assertEqual(self.ingest_raw(dict(good))[1], [])
        for bad in ({"pet": "Lightning"}, {"subject": "dragon{who}"}, {"subject": "drag0n"}, {"who": "Racer\nX"},
                    {"text": "free text"}, {"map": "TerMur"}, {"type": "gear_refused"}, {"count": "3"},
                    {"by_pet": "yes"}, {"place": "a" * 41}):
            acked, rej = self.ingest_raw({**good, **bad})
            self.assertEqual((acked, len(rej)), ([], 1), bad)
        self.assertEqual(self.ingest_raw({**good, "type": "gm_planted", "subject": "", "text": "Bells.",
                                          "town": "Vesper"})[1], [])

    def test_ilshenar_place_never_drifts_into_a_town(self):
        for i in range(40):
            self.deed("kill_notable", i * 30 * H, subject="beast " + _letters(i), place="Ilshenar dungeon",
                      where={"map": "Ilshenar", "x": 500, "y": 500})
        for i in range(40):
            for r in self.r.view(i * 30 * H + 20 * H):
                self.assertNotIn("somewhere around", r["text"])

    def test_horizon_bounds_reads_but_heroic_nickname_keeps_old_deeds(self):
        horizon = self.r.horizon()
        for i in range(3):
            self.deed("kill_notable", i * H)
        late = horizon + 10 * H
        self.assertEqual(self.r._deeds(late), [])                 # outside the horizon: not even read
        self.assertEqual(self.r.epithets(late).get(WREN), ["Dragonbane"])  # but still counted, by SQL

    def test_tick_never_marks_unshown_arrivals(self):
        for i in range(30):
            self.deed("kill_notable", 0, subject="beast " + _letters(i))
        # At pt 0 only Britain has heard, and a town holds 5: five arrivals, shown 3 per tick.
        self.assertEqual(len(self.r.tick(0, limit=3)), 3)
        self.assertEqual(len(self.r.tick(0, limit=3)), 2)        # the 2 not shown last time were not marked logged
        self.assertEqual(self.r.tick(0, limit=3), [])

    def test_retention_prunes_old_logs(self):
        rid = self.deed("kill_notable", 0)
        self.r.tick(0)
        self.r.drop(rid, "2026-10-28T16:00:00Z")
        self.r.tick(self.r.horizon() + H)
        db = self.led.db
        self.assertEqual(db.execute("SELECT COUNT(*) FROM rumour_arrivals").fetchone()[0], 0)
        self.assertEqual(db.execute("SELECT COUNT(*) FROM rumour_drops").fetchone()[0], 0)

    def test_tick_logs_each_arrival_once(self):
        self.deed("kill_notable", 0)
        first = self.r.tick(0)
        self.assertEqual([a["town"] for a in first], ["Britain"])
        self.assertEqual(self.r.tick(0), [])
        self.assertTrue(len(self.r.tick(3 * H)) >= 3)

    def test_dungeon_deed_starts_at_nearest_town_to_entrance(self):
        # The shard sends the dungeon ENTRANCE as x,y; the rumour starts in exactly one town, the nearest.
        rid = self.deed("kill_notable", 0, where={"map": "Trammel", "x": 1176, "y": 2635})
        self.assertEqual(len(self.towns(0, rid)), 1)

    def test_felucca_names_ocllo(self):
        rid = self.deed("drunk", 0, subject="", place="Haven", where={"map": "Felucca", "x": 3650, "y": 2519})
        texts = [r["text"] for r in self.r.view(0) if r["id"] == rid]
        self.assertEqual(texts, ["Racer X was roaring drunk near Ocllo"])

    def test_no_model_needed(self):
        import brain  # noqa: F401  # importing must not open the live db (main() does that)
        self.assertIsNone(brain.LEDGER)


if __name__ == "__main__":
    unittest.main()
