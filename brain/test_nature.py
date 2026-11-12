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

    # ---- rumour step C4 (2026-11-11): manners always, flourishes only some turns and never two running ----
    def _with(self, habit):
        return next(i for i in IDS if nature.of(i)["habit"] == habit)

    def setUp(self):
        nature._turns.clear()

    def test_a_manner_is_in_every_prompt(self):
        for habit in ("pet_name", "slow", "no_names"):
            npc = self._with(habit)
            self.assertTrue(nature.of(npc)["habit_manner"])
            for t in range(40):
                self.assertTrue(nature.habit_in_prompt(npc, "p1", f"t{t}", now=t), habit)
            self.assertIn("A habit of speech:", nature.prompt_block(nature.of(npc), True))

    def test_only_three_habits_are_manners(self):
        self.assertEqual({h["key"] for h in nature.cfg()["habits"] if h.get("manner")}, {"pet_name", "slow", "no_names"})

    def test_a_flourish_is_never_in_two_turns_running(self):
        for habit in ("years", "sayings", "blessing"):
            npc = self._with(habit)
            shown = [nature.habit_in_prompt(npc, "p1", f"t{t}", now=t) for t in range(400)]
            self.assertTrue(any(shown), habit)
            self.assertFalse(any(a and b for a, b in zip(shown, shown[1:])), habit)

    def test_shares(self):
        def rate(habit):
            npc = self._with(habit)
            # every turn in a fresh conversation, so "not twice running" does not thin it: this measures the draw alone
            return sum(nature.habit_in_prompt(npc, f"p{t}", f"t{t}", now=t) for t in range(3000)) / 3000
        self.assertAlmostEqual(rate("sayings"), 0.5, delta=0.04)
        self.assertAlmostEqual(rate("years"), 0.25, delta=0.04)      # the overused one gets the smaller share

    def test_same_turn_asked_again_gets_the_same_answer(self):   # a retry never re-draws
        npc = self._with("years")
        for t in range(200):
            first = nature.habit_in_prompt(npc, "p1", f"t{t}", now=t)
            self.assertEqual([nature.habit_in_prompt(npc, "p1", f"t{t}", now=t) for _ in range(3)], [first] * 3)

    def test_a_long_silence_starts_a_new_conversation(self):
        npc = self._with("sayings")
        gap = nature.cfg()["flourishes"]["gap_s"]
        t = next(t for t in range(400) if nature.habit_in_prompt(npc, "p1", f"a{t}", now=t))   # shown at second t
        nxt = next(k for k in range(400) if nature._unit(npc, "flourish", "p1", f"b{k}") < 0.5)   # a draw that says yes
        self.assertFalse(nature.habit_in_prompt(npc, "p1", f"b{nxt}", now=t + 5))            # straight after: held back
        nature._turns.clear()
        self.assertTrue(nature.habit_in_prompt(npc, "p1", f"a{t}", now=t))
        self.assertTrue(nature.habit_in_prompt(npc, "p1", f"b{nxt}", now=t + gap + 1))       # after the silence: allowed

    def test_conversations_are_separate(self):
        npc = self._with("sayings")
        t = next(t for t in range(400) if nature.habit_in_prompt(npc, "p1", f"a{t}", now=t))
        nxt = next(k for k in range(400) if nature._unit(npc, "flourish", "p2", f"b{k}") < 0.5)
        self.assertTrue(nature.habit_in_prompt(npc, "p2", f"b{nxt}", now=t + 1))   # another player: not held back

    def test_inspection_never_advances_the_choice(self):
        npc = self._with("years")
        nature.habit_in_prompt(npc, "p1", "t1", now=1)
        before = dict(nature._turns)
        for _ in range(5):
            nature.of(npc), nature.label(npc), nature.prompt_block(nature.of(npc)), nature.subject_angle(npc)
            nature.tilt(npc)
        self.assertEqual(dict(nature._turns), before)

    def test_prompt_block_without_the_habit(self):
        npc = self._with("years")
        p = nature.of(npc)
        self.assertIn("how many years", nature.prompt_block(p))             # default: in, as before this step
        out = nature.prompt_block(p, False)
        self.assertNotIn("A habit of speech", out)
        self.assertNotIn("how many years", out)
        self.assertIn("Your nature: you are", out)                          # the rest of the nature is untouched

    def test_flourish_gate_off_is_the_old_behaviour(self):
        c = nature.cfg()
        npc = self._with("years")
        try:
            c["flourishes"]["enabled"] = False
            self.assertTrue(all(nature.habit_in_prompt(npc, "p1", f"t{t}", now=t) for t in range(100)))
            self.assertEqual(nature._turns, {})                             # and nothing is remembered
        finally:
            c["flourishes"]["enabled"] = True

    def test_memory_of_turns_is_bounded(self):
        npc = self._with("years")
        for t in range(nature.TURNS_MAX + 50):
            nature.habit_in_prompt(npc, f"p{t}", "t", now=t)
        self.assertEqual(len(nature._turns), nature.TURNS_MAX)

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
