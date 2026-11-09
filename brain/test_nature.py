"""Step B (2026-11-08): NPC natures (the owner, chat; prickly about 1 in 6; nature tilts mood).
Run: python3 test_nature.py"""
import json
import unittest
import uuid

import mood
import nature

IDS = [str(uuid.UUID(int=k)) for k in range(1, 6001)]


class Nature(unittest.TestCase):
    def test_stable(self):
        for i in IDS[:50]:
            self.assertEqual(nature.of(i), nature.of(i))

    def test_prickly_about_one_in_six(self):
        share = sum(nature.of(i)["group"] == "prickly" for i in IDS) / len(IDS)
        self.assertAlmostEqual(share, 1 / 6, delta=0.02)

    def test_varied(self):   # most NPCs in a town of 20 differ in all three parts
        town = [nature.of(i) for i in IDS[:20]]
        self.assertGreaterEqual(len({(p["key"], p["habit"], p["subject"]) for p in town}), 19)
        self.assertGreaterEqual(len({p["key"] for p in town}), 14)

    def test_removing_an_entry_moves_only_its_npcs(self):   # rendezvous hashing
        before = {i: nature.of(i)["key"] for i in IDS[:2000]}
        c = nature.cfg()
        gone = c["personalities"][0]["key"]
        saved = list(c["personalities"])
        try:
            c["personalities"] = saved[1:]
            moved = [i for i in before if nature.of(i)["key"] != before[i]]
        finally:
            c["personalities"] = saved
        self.assertTrue(moved)
        self.assertTrue(all(before[i] == gone for i in moved))

    def test_prompt_block(self):
        pr = next(nature.of(i) for i in IDS if nature.of(i)["group"] == "prickly")
        b = nature.prompt_block(pr)
        self.assertIn("Your nature: you are", b)
        self.assertIn("never threaten", b)
        self.assertIn(pr["jab"], b)
        warm = next(nature.of(i) for i in IDS if nature.of(i)["group"] == "warm")
        self.assertNotIn("rude", nature.prompt_block(warm))   # control: only prickly natures get the jab line
        self.assertNotIn("{", b + nature.prompt_block(warm))

    def test_tilt(self):
        sun = next(i for i in IDS if nature.of(i)["lean"] == "sun")
        cloud = next(i for i in IDS if nature.of(i)["lean"] == "cloud")
        swing = next(i for i in IDS if nature.of(i)["lean"] == "swing")
        self.assertEqual(nature.tilt(sun)[:2], (10.0, 0.0))
        self.assertEqual(nature.tilt(cloud)[:2], (-10.0, 0.0))
        self.assertGreater(nature.tilt(swing)[2]["share_x"], 1.0)
        plain = mood.rest(cloud)                     # control: not templated -> no tilt
        tilted = mood.rest(cloud, templated=True)
        self.assertAlmostEqual(tilted[0], plain[0] - 10.0, places=1)

    def test_swing_means_more_off_and_bright_days(self):
        def share(sw):
            days = [mood.day_draw("npc-x", f"d{d}", sw)[0] for d in range(3000)]
            return sum(k != "ordinary" for k in days) / len(days)
        self.assertGreater(share({"jitter_x": 1.5, "share_x": 1.6}), share(None) + 0.1)

    def test_named_npcs_untouched(self):   # Mirabel: no persona_vars -> no nature
        self.assertIsNone(mood.nature_of({"npc_id": IDS[0], "persona_key": "mirabel"}))
        self.assertIsNotNone(mood.nature_of({"npc_id": IDS[0], "persona_key": "townsfolk", "persona_vars": {"name": "Gus"}}))

    def test_off_switch(self):
        c = nature.cfg()
        try:
            c["enabled"] = False
            self.assertIsNone(nature.of(IDS[0]))
            self.assertEqual(nature.prompt_block(nature.of(IDS[0])), "")
            self.assertEqual(nature.tilt(IDS[0])[:2], (0.0, 0.0))
        finally:
            c["enabled"] = True

    def test_subjects_are_small_talk_not_world_claims(self):   # Codex Step B r1 finding 4 (the owner's exception: small flavour)
        subs = " ".join(nature.cfg()["subjects"]).lower()
        for w in ("bandit", "festival", "repaired", "debt", "lost and keep looking"):
            self.assertNotIn(w, subs, w)
        p = nature.of(IDS[0])
        self.assertIn("never a danger", nature.prompt_block(p))
        self.assertIn("not the reason", nature.subject_angle(IDS[0]))

    def test_file_parses_and_weights_sum(self):
        d = json.load(open(nature.CONFIG))
        self.assertAlmostEqual(sum(p["weight"] for p in d["personalities"]), 1.0, delta=0.001)
        self.assertEqual(len({p["key"] for p in d["personalities"]}), len(d["personalities"]))


if __name__ == "__main__":
    unittest.main(verbosity=1)
