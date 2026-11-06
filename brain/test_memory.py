"""Phase 4a1 ledger tests - each failure case the design names. Run: python3 test_memory.py"""
import os
import tempfile
import unittest

import memory

NPC, CHAR = "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222"


def ev(epoch, seq, kind="fight_survived", **facts):
    return {"event_id": f"{epoch}-{seq}", "epoch": epoch, "seq": seq, "npc_id": NPC, "char_id": CHAR,
            "kind": kind, "facts": facts, "at_utc": "2026-10-02T15:00:00Z", "schema": memory.SCHEMA_VERSION}


class LedgerTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))

    def tearDown(self):
        self.led.db.close()
        self.dir.cleanup()

    def live(self):
        return {r["event_id"] for r in self.led.db.execute("SELECT event_id FROM events WHERE status='live'")}

    def test_first_start_is_fresh(self):
        self.assertEqual(self.led.reconcile(1, 0, 0)[0], "fresh")

    def test_duplicate_delivery_is_harmless(self):
        self.led.reconcile(1, 0, 0)
        a1, _ = self.led.ingest([ev(1, 1), ev(1, 2)])
        a2, _ = self.led.ingest([ev(1, 2), ev(1, 1)])   # lost ack -> replay
        self.assertEqual(sorted(a1), sorted(a2))
        self.assertEqual(self.live(), {"1-1", "1-2"})

    def test_crash_before_save_rolls_back_unsaved_events(self):
        self.led.reconcile(1, 0, 0)
        self.led.ingest([ev(1, s) for s in range(1, 6)])
        # world saved at (1, 3); crash; next boot epoch 2 loads (1, 3)
        verdict, rolled, _ = self.led.reconcile(2, 1, 3)
        self.assertEqual((verdict, rolled), ("ok", 2))
        self.assertEqual(self.live(), {"1-1", "1-2", "1-3"})

    def test_clean_restart_rolls_back_nothing(self):
        self.led.reconcile(1, 0, 0)
        self.led.ingest([ev(1, 1), ev(1, 2)])
        self.assertEqual(self.led.reconcile(2, 1, 2)[:2], ("ok", 0))

    def test_restore_older_world_across_epochs(self):
        self.led.reconcile(1, 0, 0)
        self.led.ingest([ev(1, s) for s in range(1, 5)])
        self.led.reconcile(2, 1, 4)
        self.led.ingest([ev(2, s) for s in range(1, 4)])
        # restore a backup saved at (1, 2): epoch 3 boots
        verdict, rolled, _ = self.led.reconcile(3, 1, 2)
        self.assertEqual((verdict, rolled), ("ok", 5))
        self.assertEqual(self.live(), {"1-1", "1-2"})

    def test_late_event_from_cut_branch_is_stored_rolled_back(self):
        self.led.reconcile(1, 0, 0)
        self.led.ingest([ev(1, 1)])
        self.led.reconcile(2, 1, 1)
        self.led.ingest([ev(1, 7)])   # from the dead branch, arriving late
        self.assertEqual(self.live(), {"1-1"})

    def test_outbox_replay_after_brain_outage(self):
        self.led.reconcile(1, 0, 0)
        self.led.ingest([ev(1, 1)])
        # brain down for 2..4; world saved at (1, 4) with 2..4 still in the outbox
        verdict, _, _ = self.led.reconcile(2, 1, 4, outbox_min_seq=2)
        self.assertEqual(verdict, "ok")
        self.led.ingest([ev(1, 2), ev(1, 3), ev(1, 4)])
        self.assertEqual(self.live(), {"1-1", "1-2", "1-3", "1-4"})

    def test_db_behind_world_is_conflict(self):
        self.led.reconcile(1, 0, 0)
        self.led.ingest([ev(1, 1)])
        verdict, _, reason = self.led.reconcile(2, 1, 5)   # world has 5, db has 1, outbox empty
        self.assertEqual(verdict, "conflict")
        self.assertIn("missing events 2..5", reason)

    def test_epoch_counter_going_backwards_is_conflict(self):
        self.led.reconcile(1, 0, 0)
        self.led.ingest([ev(3, 1)])
        self.assertEqual(self.led.reconcile(2, 1, 0)[0], "conflict")

    def test_invalid_events_rejected_not_stored(self):
        self.led.reconcile(1, 0, 0)
        bad = [ev(1, 1, kind="ignore_previous_instructions"), dict(ev(1, 2), event_id="1-9"),
               dict(ev(1, 3), schema=99), {"nonsense": True}]
        acked, rejected = self.led.ingest(bad)
        self.assertEqual(acked, [])
        self.assertEqual(len(rejected), 4)
        self.assertEqual(self.live(), set())

    def test_untrusted_text_is_cleaned(self):
        self.led.reconcile(1, 0, 0)
        self.led.ingest([ev(1, 1, kind="conversation", player_said="hi‮​\x00there" + "x" * 2000)])
        said = self.led.recent(NPC, CHAR)[0]["facts"]["player_said"]
        self.assertTrue(said.startswith("hithere"))
        self.assertEqual(len(said), memory.MAX_TEXT_CHARS)

    def test_tombstone_hides_character(self):
        self.led.reconcile(1, 0, 0)
        self.led.ingest([ev(1, 1), ev(1, 2, kind="character_deleted")])
        self.assertEqual(self.led.recent(NPC, CHAR), [])
        self.assertEqual(len(self.live()), 2)   # kept for audit

    def test_backup_is_readable(self):
        self.led.reconcile(1, 0, 0)
        self.led.ingest([ev(1, 1)])
        dest = os.path.join(self.dir.name, "copy.db")
        self.led.backup(dest)
        copy = memory.Ledger(dest)
        self.assertEqual(copy.status()["live"], 1)
        copy.db.close()


class RetrievalGoldenTests(unittest.TestCase):
    """Phase 4a2 golden set: paraphrased questions must bring back the right memory (design v2 #5)."""

    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.led.reconcile(1, 0, 0)
        evs, seq = [], 0

        def add(kind, at, **facts):
            nonlocal seq
            seq += 1
            e = ev(1, seq, kind, **facts)
            e["at_utc"] = at
            evs.append(e)
            return e["event_id"]

        self.joined = add("joined", "2026-09-20T10:00:00Z", text="You agreed to travel with Racer X.")
        self.orc = add("fight_survived", "2026-09-21T10:00:00Z", foes="Karthurg (orc)", owner_hits_pct=40)
        self.storm = add("conversation", "2026-09-22T10:00:00Z", player_said="that storm on the road to Minoc was awful",
                         she_said="I have never been so wet in my life.")
        self.died = add("owner_died", "2026-09-23T10:00:00Z", text="Racer X was killed in front of you.")
        for i in range(12):   # routine fights must not crowd out the orc
            add("fight_survived", f"2026-09-24T1{i % 10}:0{i % 6}:00Z", foes="a mongbat")
        self.chat_recent = add("conversation", "2026-09-25T10:00:00Z", player_said="nice day", she_said="It is.")
        self.led.ingest(evs)
        self.now = memory.datetime(2026, 9, 25, 12, 0, tzinfo=memory.timezone.utc)

    def tearDown(self):
        self.led.db.close()
        self.dir.cleanup()

    def ids(self, words, skip=0):
        return [e for e, _ in self.led.retrieve(NPC, CHAR, words, skip_conversations=skip, now=self.now)[1]]

    def test_that_orc(self):
        self.assertIn(self.orc, self.ids("remember that orc we fought?"))

    def test_when_i_died_paraphrase(self):
        self.assertIn(self.died, self.ids("do you remember when I died"))

    def test_the_storm(self):
        self.assertIn(self.storm, self.ids("what did I say about the storm"))

    def test_place_name(self):
        self.assertIn(self.storm, self.ids("how was Minoc"))

    def test_big_event_always_pinned(self):
        self.assertIn(self.died, self.ids("hello"))

    def test_history_lines_not_repeated(self):
        self.assertNotIn(self.chat_recent, self.ids("nice day", skip=1))

    def test_budget_and_order(self):
        lines, picked = self.led.retrieve(NPC, CHAR, "orc storm Minoc died fight", budget_chars=900, now=self.now)
        self.assertLessEqual(sum(len(x) for x in lines), 900)
        self.assertLessEqual(len(picked), 6)
        self.assertTrue(lines[0].startswith(("5 days ago", "4 days ago", "3 days ago")))

    def stable(self, words):
        lines, picked = self.led.retrieve(NPC, CHAR, words, now=self.now, stable_first=True, matched_budget=450)
        return [e for e, r in picked if r != "matched"], lines, picked

    def test_layout_v2_stable_memories_do_not_move_with_the_line(self):
        # The newest fight is "recent"; a line that also matches it must not pull it out of the stable set.
        e = ev(1, 18, "fight_survived", foes="a wyvern")
        e["at_utc"] = "2026-09-25T11:00:00Z"
        self.led.ingest([e])
        a, _, _ = self.stable("hello")
        b, _, _ = self.stable("remember that wyvern?")
        self.assertIn("1-18", a)
        c, _, _ = self.stable("what did I say about the storm")
        self.assertEqual(a, b)
        self.assertEqual(a, c)
        self.assertIn(self.died, a)

    def test_layout_v2_matched_lines_have_their_own_budget(self):
        _, lines, picked = self.stable("orc storm Minoc died fight mongbat")
        matched = [ln for ln, (_, r) in zip(lines, picked) if r == "matched"]
        self.assertTrue(matched)
        self.assertLessEqual(sum(len(x) for x in matched), 450)
        self.assertLessEqual(sum(len(ln) for ln, (_, r) in zip(lines, picked) if r != "matched"), 900)

    def test_restart_history(self):
        h = self.led.history(NPC, CHAR, pairs=2)
        self.assertEqual([x["speaker"] for x in h], ["player", "npc", "player", "npc"])
        self.assertEqual(h[-1]["text"], "It is.")

    def test_fts_injection_is_harmless(self):
        self.ids('" OR * NEAR( ) AND"')   # must not raise

    def test_remap_moves_events_and_records_it(self):
        n = self.led.remap_npc(NPC, "33333333-3333-3333-3333-333333333333", "test")
        self.assertEqual(n, 17)
        self.assertEqual(self.led.retrieve(NPC, CHAR, "orc", now=self.now)[0], [])
        self.assertEqual(self.led.db.execute("SELECT rows FROM npc_remaps").fetchone()[0], 17)

    def test_turning_point_remembered_until_undone(self):
        self.led.ingest([dict(ev(1, 90, "turning_point", turning_point="tp-x", text="FAKE turning memory"),
                              at_utc="2026-09-25T11:00:00Z")])
        self.assertIn("1-90", self.ids("hello"))
        self.led.ingest([dict(ev(1, 91, "turning_point_revoked", turning_point="tp-x"), at_utc="2026-09-25T11:30:00Z")])
        got = self.ids("turning memory hello")
        self.assertNotIn("1-90", got)
        self.assertNotIn("1-91", got)

    def test_prompt_block_says_claims_are_claims(self):
        lines, _ = self.led.retrieve(NPC, CHAR, "storm", now=self.now)
        block = memory.prompt_block(lines)
        self.assertIn("only their word", block)
        self.assertIn("their words, not proven fact", block)


class RicherFacts(unittest.TestCase):
    """2026-10-06: fight detail and the session summary, all shard-measured."""

    def test_old_fight_event_renders_as_before(self):
        self.assertEqual(memory.render("fight_survived", {"foes": "a troll", "owner_hits_pct": 80}),
                         "You survived a fight together against a troll.")

    def test_fight_detail(self):
        line = memory.render("fight_survived", {"foes": "an ettin", "owner_low_pct": 18, "her_low_pct": 40,
                                                "times_healed": 1, "started": "foe", "broke_off": True})
        for part in ("nearly killed them", "It came at them first", "bandaged them once", "hurt yourself",
                     "foe still standing"):
            self.assertIn(part, line)

    def test_wrong_types_are_ignored(self):
        line = memory.render("fight_survived", {"foes": "a rat", "owner_low_pct": "10", "times_healed": True,
                                                "started": "player", "broke_off": "yes"})
        self.assertEqual(line, "You survived a fight together against a rat.")

    def test_gear_kinds(self):
        for k in ("gear_given", "gear_returned", "gear_refused"):
            self.assertIn(k, memory.KINDS)
            self.assertIn(k, memory.IMPORTANCE)
        self.assertEqual(memory.render("gear_given", {"item": "the longsword", "worn": "carrying"}),
                         "They gave you the longsword, and you have been carrying it since.")
        self.assertEqual(memory.render("gear_given", {"item": "the cloak", "worn": "???"}),
                         "They gave you the cloak, and you have been wearing it since.")
        self.assertEqual(memory.render("gear_returned", {"item": "the cloak"}),
                         "They asked for the cloak back, and you gave it to them.")
        self.assertEqual(memory.render("gear_refused", {"item": "the plate gloves", "reason": "plate is too heavy for you"}),
                         "They offered you the plate gloves and you turned it down, because plate is too heavy for you.")
        self.assertEqual(memory.render("gear_refused", {"item": "the bow"}), "They offered you the bow and you turned it down.")

    def test_session_summary(self):
        line = memory.render("session_summary", {"minutes_together": 47, "fights": 3, "close_calls": 1,
                                                 "times_healed": 2, "owner_deaths": 1, "time_of_day": "evening",
                                                 "places": "Moonglow, Britain"})
        for part in ("about 47 minutes, in the evening", "3 fights, 1 of them close", "bandaged them twice",
                     "They died once", "by way of Moonglow, Britain"):
            self.assertIn(part, line)
        self.assertIn("session_summary", memory.KINDS)
        self.assertNotIn("midnight", memory.render("session_summary", {"minutes_together": 5, "time_of_day": "midnight"}))


if __name__ == "__main__":
    unittest.main(verbosity=1)
