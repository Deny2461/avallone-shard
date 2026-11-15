"""Step 2e - canonical rumour groups in fixed played-time buckets (design v6 §12, Codex design R5/R6).
Run: python3 -m unittest test_buckets"""
import os
import random
import tempfile
import unittest

import memory
import rumours

CFG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "rumours.json")
WREN, OTHER = "22222222-2222-2222-2222-222222222222", "33333333-3333-3333-3333-333333333333"
H = 3600
B = rumours.BUCKET_S
NEAR_BRITAIN = {"map": "Trammel", "x": 1492, "y": 1641}
NEAR_YEW = {"map": "Trammel", "x": 560, "y": 1000}


class BucketTests(unittest.TestCase):
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

    def deeds(self, rows):
        """rows: (type, pt, char, subject, where). One ingest call."""
        evs = []
        for typ, pt, char, subject, where in rows:
            self.seq += 1
            facts = {"type": typ, "subject": subject, "count": 1, "who": "Racer X", "descriptor": "a tamer",
                     "place": "Destard", "pt": pt, **where}
            evs.append({"event_id": f"1-{self.seq}", "epoch": 1, "seq": self.seq, "npc_id": rumours.WORLD_ID,
                        "char_id": char, "kind": "deed", "facts": facts, "at_utc": "2026-11-13T16:00:00Z",
                        "schema": memory.SCHEMA_VERSION})
        for i in range(0, len(evs), 200):
            _acked, rej = self.led.ingest(evs[i:i + 200])
            self.assertEqual(rej, [])

    def groups(self, pt, lookback):
        return self.r._group(self.r._deeds(pt, lookback))

    @staticmethod
    def shape(groups, since):
        return [(g["id"], tuple(g["events"]), g["first_pt"], g["last_pt"], g["count"], g["origin"], g["type"])
                for g in groups if g["last_pt"] >= since]

    def test_r5_fixture_same_groups_whatever_the_read_start(self):
        # Codex R5: one notable kill every played hour, hours 0..200, never more than the 2 h gap apart.
        self.deeds([("kill_notable", h * H, WREN, "dragon", NEAR_BRITAIN) for h in range(201)])
        pt = 200 * H
        full = self.groups(pt, 10 ** 9)
        self.assertEqual([g["first_pt"] // H for g in full], [0, 24, 48, 72, 96, 120, 144, 168, 192])
        self.assertTrue(all(g["first_pt"] // B == g["last_pt"] // B for g in full))
        since = pt - 120 * H
        ref = self.shape(full, since)
        self.assertEqual(ref, self.shape(self.groups(pt, 168 * H), since))
        self.assertEqual(ref, self.shape(self.groups(pt, 197 * H), since))
        self.assertEqual(ref, self.shape(self.groups(pt + 1, 168 * H), since))
        self.assertEqual(ref[-1][0], "1-193")

    def test_deed_on_a_bucket_boundary_starts_a_new_group(self):
        self.deeds([("kill_notable", B - 600, WREN, "dragon", NEAR_BRITAIN), ("kill_notable", B, WREN, "dragon", NEAR_BRITAIN)])
        g = self.groups(B + 60, 168 * H)
        self.assertEqual([x["id"] for x in g], ["1-1", "1-2"])

    def test_natural_gap_and_zero_window_unchanged(self):
        self.deeds([("kill_notable", 1 * H, WREN, "dragon", NEAR_BRITAIN), ("kill_notable", 5 * H, WREN, "dragon", NEAR_BRITAIN),
                    ("gm_planted", 6 * H, WREN, "", NEAR_BRITAIN), ("gm_planted", 6 * H + 1, WREN, "", NEAR_BRITAIN)])
        ids = [g["id"] for g in self.groups(7 * H, 168 * H)]
        self.assertEqual(len(ids), 4)

    def test_gm_drop_keeps_its_story_out_across_reads_and_ticks(self):
        self.deeds([("kill_notable", h * H, WREN, "dragon", NEAR_BRITAIN) for h in range(201)])
        pt = 200 * H
        latest = self.groups(pt, 168 * H)[-1]["id"]
        self.r.drop(latest, "2026-11-13T16:00:00Z")
        for t in (pt, pt + 1, pt + 3600):
            self.r.tick(t)
            self.assertNotIn(latest, {row["id"] for row in self.r.view(t)})
            self.assertIn(latest, self.r._dropped())

    def test_drop_kept_for_the_computed_retention_then_collected(self):
        self.deeds([("kill_notable", 10 * H, WREN, "dragon", NEAR_BRITAIN)])
        self.r.drop("1-1", "2026-11-13T16:00:00Z")
        keep = self.r.drop_retention_s()
        self.assertGreater(keep, 168 * H)            # longer than the old horizon rule
        self.r.tick(10 * H + keep - 1)
        self.assertIn("1-1", self.r._dropped())
        self.r.tick(10 * H + keep + 1)
        self.assertNotIn("1-1", self.r._dropped())

    def test_arrivals_logged_once(self):
        self.deeds([("kill_notable", 0, WREN, "dragon", NEAR_BRITAIN)])
        first = self.r.tick(6 * H, limit=100)
        self.assertGreater(len(first), 0)
        self.assertEqual(self.r.tick(6 * H + 60, limit=100), [])

    def test_bucketed_reader_equals_full_scan_on_random_ledgers(self):
        keys = [(WREN, "dragon", NEAR_BRITAIN), (OTHER, "dragon", NEAR_BRITAIN), (WREN, "ettin", NEAR_YEW)]
        types = ["kill_notable", "pet_died", "drunk", "gm_planted", "tame_failstreak"]
        for trial in range(6):
            self.tearDown(); self.setUp()
            rnd, t, rows = random.Random(trial), 0, []
            for _ in range(500):
                t += rnd.choice([0, 60, 900, 3600, 5400, 7300, 21000, 30000])   # ties, short and long gaps
                char, subject, where = rnd.choice(keys)
                rows.append((rnd.choice(types), t, char, subject, where))
            self.deeds(rows)
            for pt in (t, t + 1, t + 37 * H):
                full = self.shape(self.groups(pt, 10 ** 9), pt - 150 * H)
                self.assertEqual(full, self.shape(self.groups(pt, 168 * H), pt - 150 * H), f"trial {trial} pt {pt}")
                self.assertEqual(full, self.shape(self.groups(pt, 197 * H), pt - 150 * H), f"trial {trial} pt {pt}")

    def test_config_too_short_a_horizon_is_refused(self):
        import json
        with open(CFG, encoding="utf-8") as fh:
            cfg = json.load(fh)
        cfg["horizon_h"] = 50     # below bucket 24 + travel 7.5 + lifetime 24
        bad = os.path.join(self.dir.name, "r.json")
        with open(bad, "w", encoding="utf-8") as fh:
            json.dump(cfg, fh)
        with self.assertRaises(ValueError):
            rumours.Rumours(self.led, bad)


if __name__ == "__main__":
    unittest.main()
