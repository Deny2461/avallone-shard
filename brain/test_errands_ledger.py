"""Errands build 1, stage 2: errand_done in the ledger (memory.py) - a receipt for every errand event, looked up first,
judged once, final both ways (design v5 3.8; Codex "Errands-Design R4" ERR-R4-1's twelve conditions and R5's receipt
checks). Run: python3 test_errands_ledger.py"""
import copy
import json
import os
import sqlite3
import sys
import tempfile
import threading
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import errands_text  # noqa: E402
import memory  # noqa: E402

S, R, C = "11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222", "c:7"


def done(epoch, seq, **change):
    ev = {"event_id": f"{epoch}-{seq}", "epoch": epoch, "seq": seq, "kind": "errand_done", "schema": 1, "npc_id": S,
          "char_id": C, "at_utc": "2026-11-21T14:00:00Z",
          "facts": {"v": 1, "errand": 3, "kind": "word", "route": "cousin", "sender_id": S, "sender_map": "Felucca",
                    "sender_town": "Minoc", "receiver_id": R, "receiver_map": "Felucca", "receiver_town": "Vesper",
                    "played": 86400, "memory": 4000, "day": "2026-11-21", "head_start": False}}
    facts = change.pop("facts", None)
    ev.update(change)
    if facts is not None:
        ev["facts"] = dict(ev["facts"], **facts) if isinstance(facts, dict) else facts
    return ev


def other(epoch, seq):
    return {"event_id": f"{epoch}-{seq}", "epoch": epoch, "seq": seq, "kind": "joined", "schema": 1, "npc_id": S,
            "char_id": C, "at_utc": "2026-11-21T14:00:00Z", "facts": {}}


class Base(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.dir.name, "m.db")
        self.led = memory.Ledger(self.path)
        self.led.reconcile(8, 7, 0, None)

    def tearDown(self):
        self.dir.cleanup()

    def rows(self, sql, *args):
        return [tuple(r) for r in self.led.db.execute(sql, args).fetchall()]

    def receipt(self, eid):
        r = self.rows("SELECT disposition, reason, fingerprint FROM errand_receipt WHERE event_id = ?", eid)
        return r[0] if r else None


class Receipts(Base):
    def test_a_good_completion_is_stored_accepted_and_read(self):
        self.assertEqual(self.led.ingest([done(8, 1)]), (["8-1"], []))
        self.assertEqual(self.receipt("8-1")[:2], ("accepted", None))
        self.assertEqual(self.receipt("8-1")[2], errands_text.event_fingerprint(done(8, 1)))
        got = self.led.errands_done(char_id=C)
        self.assertEqual([(g["event_id"], g["receiver_town"], g["head_start"]) for g in got], [("8-1", "Vesper", False)])
        self.assertEqual(self.led.errand_set_aside(["8-1"]), [])

    def test_never_searchable_and_never_in_ordinary_retrieval(self):
        self.led.ingest([done(8, 1)])
        self.assertEqual(self.rows("SELECT COUNT(*) FROM events_fts"), [(0,)])
        self.assertIn("errand_done", memory.PRIVATE_KINDS)
        self.assertIn("'errand_done'", memory.NOT_PRIVATE)

    def test_identical_resend_adds_nothing_and_replays(self):
        self.led.ingest([done(8, 1)])
        before = self.rows("SELECT * FROM events") + self.rows("SELECT * FROM errand_receipt")
        for _ in range(3):
            self.assertEqual(self.led.ingest([copy.deepcopy(done(8, 1))]), (["8-1"], []))
        self.assertEqual(before, self.rows("SELECT * FROM events") + self.rows("SELECT * FROM errand_receipt"))

    def test_facts_that_fail_the_check_are_stored_set_aside_and_acknowledged(self):
        cases = {"8-1": done(8, 1, facts={"v": 2}), "8-2": done(8, 2, facts={"route": "standin", "kind": "ask_after"}),
                 "8-3": done(8, 3, facts={"memory": 4000.5}), "8-4": done(8, 4, facts={"extra": 1}),
                 "8-5": done(8, 5, schema=2), "8-6": done(8, 6, npc_id=R), "8-7": done(8, 7, facts="not an object"),
                 "8-8": done(8, 8, facts={"head_start": True}), "8-9": done(8, 9, facts={"played": True}),
                 "8-10": done(8, 10, facts={"day": "today"}), "8-11": done(8, 11, facts={"receiver_id": S})}
        del cases["8-1"]["facts"]["sender_town"]
        acked, rejected = self.led.ingest(list(cases.values()))
        self.assertEqual((acked, rejected), (list(cases), []))
        self.assertEqual(self.led.errand_set_aside(acked), list(cases))
        for eid in cases:
            self.assertEqual(self.receipt(eid)[0], "set_aside", eid)
            self.assertTrue(self.receipt(eid)[1].startswith("errand:"), eid)
        self.assertEqual(self.led.errands_done(), [])
        self.assertEqual(self.rows("SELECT facts FROM events WHERE event_id = '8-7'"), [("{}",)])
        self.assertEqual(self.rows("SELECT COUNT(*) FROM rejected"), [(0,)])       # set aside is not rejected
        self.assertEqual(self.led.ingest([other(8, 12)]), (["8-12"], []))           # the queue is not held up

    def test_a_missing_field_fingerprints_but_is_not_valid(self):
        ev = done(8, 1)
        del ev["at_utc"]
        self.assertEqual(self.led.ingest([ev]), (["8-1"], []))
        self.assertEqual(self.receipt("8-1")[0], "set_aside")

    def test_set_aside_resend_reports_set_aside_again_after_a_lost_reply(self):
        bad = done(8, 1, facts={"v": 2})
        self.led.ingest([bad])
        acked, rejected = self.led.ingest([bad])
        self.assertEqual((acked, rejected, self.led.errand_set_aside(acked)), (["8-1"], [], ["8-1"]))

    def test_first_disposition_is_final_when_the_check_later_changes_either_way(self):
        good, bad = done(8, 1), done(8, 2, facts={"v": 2})
        self.led.ingest([good, bad])
        with mock.patch.object(memory, "check_errand", side_effect=ValueError("errand: stricter now")):
            self.assertEqual(self.led.ingest([good]), (["8-1"], []))
        with mock.patch.object(memory, "check_errand", return_value=None):
            self.assertEqual(self.led.ingest([bad]), (["8-2"], []))
        self.assertEqual((self.receipt("8-1")[0], self.receipt("8-2")[0]), ("accepted", "set_aside"))
        self.assertEqual([g["event_id"] for g in self.led.errands_done()], ["8-1"])

    def test_the_receipt_is_looked_up_before_anything_is_judged(self):
        self.led.ingest([done(8, 1)])
        with mock.patch.object(memory, "check_errand", side_effect=AssertionError("judged again")):
            self.assertEqual(self.led.ingest([done(8, 1)]), (["8-1"], []))

    def test_different_contents_under_a_stored_identity_are_refused_and_change_nothing(self):
        self.led.ingest([done(8, 1)])
        before = self.rows("SELECT * FROM events") + self.rows("SELECT * FROM errand_receipt")
        for changed in (done(8, 1, facts={"receiver_town": "Yew"}), done(8, 1, char_id="c:8"), done(8, 1, npc_id=R),
                        done(8, 1, at_utc="2026-11-21T14:00:01Z"), done(8, 1, schema=2), done(8, 1, facts={"v": 2})):
            self.assertEqual(self.led.ingest([changed]), ([], ["8-1"]))
        self.assertEqual(before, self.rows("SELECT * FROM events") + self.rows("SELECT * FROM errand_receipt"))
        self.assertEqual(self.rows("SELECT COUNT(*) FROM rejected"), [(6,)])

    def test_cleaning_never_turns_an_identical_resend_into_a_conflict(self):
        long, ctrl = "x" * 2000, "a​b\u0007c"
        many = {f"k{i:02d}": i for i in range(30)}
        for seq, facts in ((1, {"note": long}), (2, {"note": ctrl}), (3, many), (4, {"n": "y" * 5000}), (5, [1, 2])):
            ev = done(8, seq, facts=facts)
            ev["facts"] = facts
            self.assertEqual(self.led.ingest([ev]), ([f"8-{seq}"], []), seq)
            self.assertEqual(self.led.ingest([copy.deepcopy(ev)]), ([f"8-{seq}"], []), seq)
            self.assertEqual(self.receipt(f"8-{seq}")[0], "set_aside")

    def test_originals_that_clean_alike_still_conflict(self):
        a, b = done(8, 1), done(8, 1)
        a["facts"], b["facts"] = {"note": "x" * 700}, {"note": "x" * 701}
        self.assertEqual(memory.clean_facts(a["facts"]), memory.clean_facts(b["facts"]))
        self.led.ingest([a])
        self.assertEqual(self.led.ingest([b]), ([], ["8-1"]))

    def test_an_unsound_identity_is_refused_as_every_kind_is(self):
        self.assertEqual(self.led.ingest([done(8, 1, event_id="8-2")]), ([], ["8-2"]))
        self.assertEqual(self.receipt("8-2"), None)
        self.assertEqual(self.rows("SELECT COUNT(*) FROM events"), [(0,)])

    def test_event_and_receipt_are_written_together_or_not_at_all(self):
        real = self.led._rolled_back_by_history
        calls = []

        def boom(epoch, seq):
            calls.append(seq)
            if seq == 2:
                raise sqlite3.OperationalError("disk full")
            return real(epoch, seq)
        with mock.patch.object(self.led, "_rolled_back_by_history", side_effect=boom):
            with self.assertRaises(sqlite3.OperationalError):
                self.led.ingest([done(8, 1), done(8, 2)])
        self.assertEqual(calls, [1, 2])
        self.assertEqual(self.rows("SELECT COUNT(*) FROM events") + self.rows("SELECT COUNT(*) FROM errand_receipt"),
                         [(0,), (0,)])
        self.assertEqual(self.led.ingest([done(8, 1), done(8, 2)]), (["8-1", "8-2"], []))

    def test_damage_is_a_conflict_and_is_never_mended_from_one_side(self):
        self.led.ingest([done(8, 1), done(8, 2)])
        self.led.db.execute("DELETE FROM errand_receipt WHERE event_id = '8-1'")
        self.led.db.execute("DELETE FROM events WHERE event_id = '8-2'")
        self.assertEqual(self.led.ingest([done(8, 1), done(8, 2)]), ([], ["8-1", "8-2"]))
        self.assertEqual(self.receipt("8-1"), None)                                    # no receipt made up
        self.assertEqual(self.rows("SELECT COUNT(*) FROM events WHERE event_id = '8-2'"), [(0,)])   # no event made up
        self.assertEqual(self.led.errands_done(), [])

    def test_two_identical_requests_at_once_give_one_event_and_one_receipt(self):
        results = []
        threads = [threading.Thread(target=lambda: results.append(self.led.ingest([done(8, 1)]))) for _ in range(8)]
        [t.start() for t in threads]
        [t.join() for t in threads]
        self.assertEqual(results, [(["8-1"], [])] * 8)
        self.assertEqual(self.rows("SELECT COUNT(*) FROM events") + self.rows("SELECT COUNT(*) FROM errand_receipt"),
                         [(1,), (1,)])

    def test_two_conflicting_requests_at_once_keep_the_winner(self):
        a, b = done(8, 1), done(8, 1, facts={"receiver_town": "Yew"})
        results = []
        threads = [threading.Thread(target=lambda e=e: results.append(self.led.ingest([e]))) for e in (a, b, a, b)]
        [t.start() for t in threads]
        [t.join() for t in threads]
        self.assertEqual(sorted(results), [([], ["8-1"]), ([], ["8-1"]), (["8-1"], []), (["8-1"], [])])
        self.assertEqual(self.rows("SELECT COUNT(*) FROM errand_receipt"), [(1,)])


class KindCollisions(Base):
    """Codex Code R1 C1: an identity that belongs to an errand event is judged against its receipt whatever kind a later
    arrival claims - and a conflict has no side effect of any kind."""

    def state(self):
        return [self.rows(f"SELECT * FROM {t} ORDER BY 1") for t in ("events", "errand_receipt", "tombstones", "events_fts")]

    def test_replayed_as_every_other_kind_it_is_refused_and_changes_nothing(self):
        self.led.ingest([done(8, 1)])
        before = self.state()
        kinds = sorted(memory.KINDS - set(memory.ERRAND_KINDS)) + ["no_such_kind"]
        self.assertGreater(len(kinds), 20)
        for kind in kinds:
            ev = done(8, 1, kind=kind, facts={} if kind != "visited" else {"day": "2026-11-21"})
            ev["facts"] = {"day": "2026-11-21"} if kind in ("visited", "traded") else {}
            self.assertEqual(self.led.ingest([ev]), ([], ["8-1"]), kind)
            self.assertEqual(before, self.state(), kind)
        self.assertEqual(self.rows("SELECT COUNT(*) FROM rejected"), [(len(kinds),)])
        self.assertEqual([g["event_id"] for g in self.led.errands_done(char_id=C)], ["8-1"])

    def test_replayed_as_a_deletion_of_another_character_it_writes_no_tombstone(self):
        self.led.ingest([done(8, 1)])
        other = {"event_id": "8-1", "epoch": 8, "seq": 1, "kind": "character_deleted", "schema": 1, "npc_id": S,
                 "char_id": "c:someone-else", "at_utc": "2026-11-21T15:00:00Z", "facts": {}}
        self.assertEqual(self.led.ingest([other]), ([], ["8-1"]))
        self.assertEqual(self.rows("SELECT COUNT(*) FROM tombstones"), [(0,)])
        same_char = dict(other, char_id=C)
        self.assertEqual(self.led.ingest([same_char]), ([], ["8-1"]))
        self.assertEqual(self.rows("SELECT COUNT(*) FROM tombstones"), [(0,)])
        self.assertEqual(len(self.led.errands_done(char_id=C)), 1)                 # still there, still theirs

    def test_the_other_direction_an_ordinary_identity_claimed_as_a_completion(self):
        self.assertEqual(self.led.ingest([other(8, 1)]), (["8-1"], []))
        before = self.state()
        self.assertEqual(self.led.ingest([done(8, 1)]), ([], ["8-1"]))
        self.assertEqual(before, self.state())
        self.assertIsNone(self.receipt("8-1"))
        self.assertEqual(self.led.errands_done(), [])
        self.assertEqual(self.led.ingest([other(8, 1)]), (["8-1"], []))            # the ordinary event's own resend: as before

    def test_a_changed_header_under_the_same_event_id_is_refused(self):
        self.led.ingest([done(8, 1)])
        before = self.state()
        for bent in (dict(done(8, 1), epoch=9), dict(done(8, 1), seq=2), done(8, 1, char_id="c:8"), done(8, 1, npc_id=R),
                     done(8, 1, schema=2), done(8, 1, at_utc="2026-11-21T14:00:01Z"), done(8, 1, facts={"errand": 4})):
            bent["event_id"] = "8-1"
            self.assertEqual(self.led.ingest([bent]), ([], ["8-1"]))
            self.assertEqual(before, self.state())

    def test_an_identical_replay_is_acknowledged_and_nothing_is_added(self):
        self.led.ingest([done(8, 1), other(8, 2)])
        before = self.state()
        self.assertEqual(self.led.ingest([done(8, 1), other(8, 2), done(8, 1)]), (["8-1", "8-2", "8-1"], []))
        self.assertEqual(before, self.state())
        self.assertEqual(self.rows("SELECT COUNT(*) FROM rejected"), [(0,)])

    def test_a_stored_errand_event_without_its_receipt_is_a_conflict_for_any_kind(self):
        self.led.ingest([done(8, 1)])
        self.led.db.execute("DELETE FROM errand_receipt WHERE event_id = '8-1'")
        for ev in (done(8, 1), done(8, 1, kind="joined"), done(8, 1, kind="character_deleted")):
            self.assertEqual(self.led.ingest([ev]), ([], ["8-1"]))
        self.assertEqual(self.rows("SELECT COUNT(*) FROM tombstones"), [(0,)])
        self.assertIsNone(self.receipt("8-1"))


class Rollback(Base):
    def test_restore_to_before_the_event_rolls_it_back_and_the_receipt_gives_nothing(self):
        self.led.ingest([other(8, 1), done(8, 2)])
        self.assertEqual(self.led.reconcile(9, 8, 1, None)[:2], ("ok", 1))
        self.assertEqual(self.rows("SELECT status FROM events WHERE event_id = '8-2'"), [("rolled_back",)])
        self.assertEqual(self.receipt("8-2")[0], "accepted")
        self.assertEqual(self.led.errands_done(), [])

    def test_restore_of_a_world_that_still_holds_it_pending_keeps_one_meaning(self):
        self.led.ingest([other(8, 1), done(8, 2)])
        self.assertEqual(self.led.reconcile(9, 8, 2, 2)[:2], ("ok", 0))      # the world still has 8-2 in its outbox
        self.assertEqual(self.led.ingest([done(8, 2)]), (["8-2"], []))
        self.assertEqual([g["event_id"] for g in self.led.errands_done()], ["8-2"])

    def test_a_set_aside_tail_keeps_the_start_up_count_whole(self):
        self.led.ingest([other(8, 1), done(8, 2, facts={"v": 2})])
        self.assertEqual(self.led.reconcile(9, 8, 2, None)[0], "ok")         # nothing pending; the brain holds 1..2

    def test_first_arrival_after_its_branch_was_cut_off_is_stored_rolled_back(self):
        self.led.ingest([other(8, 1)])
        self.led.reconcile(9, 8, 1, None)
        self.assertEqual(self.led.ingest([done(8, 2)]), (["8-2"], []))
        self.assertEqual(self.rows("SELECT status FROM events WHERE event_id = '8-2'"), [("rolled_back",)])
        self.assertEqual(self.receipt("8-2")[0], "accepted")
        self.assertEqual(self.led.errands_done(), [])
        self.assertEqual(self.led.ingest([done(8, 2)]), (["8-2"], []))       # replay does not revive it
        self.assertEqual(self.led.errands_done(), [])

    def test_a_deleted_characters_completions_are_gone_and_come_back_with_a_rollback(self):
        self.led.ingest([done(8, 1)])
        self.led.ingest([{"event_id": "8-2", "epoch": 8, "seq": 2, "kind": "character_deleted", "schema": 1,
                          "npc_id": S, "char_id": C, "at_utc": "2026-11-21T15:00:00Z", "facts": {}}])
        self.assertEqual(self.led.errands_done(), [])
        self.led.reconcile(9, 8, 1, None)
        self.assertEqual([g["event_id"] for g in self.led.errands_done()], ["8-1"])


class Schema(Base):
    def test_the_receipt_table_does_not_raise_the_database_version(self):
        self.assertEqual(self.rows("PRAGMA user_version"), [(memory.DB_VERSION,)])
        self.assertEqual(memory.DB_VERSION, 6)
        self.assertEqual(self.rows("SELECT sql FROM sqlite_master WHERE name = 'events'")[0][0].count("rolled_back"), 1)

    def test_reopening_keeps_receipts(self):
        self.led.ingest([done(8, 1)])
        again = memory.Ledger(self.path)
        self.assertEqual(again.ingest([done(8, 1)]), (["8-1"], []))
        self.assertEqual([g["event_id"] for g in again.errands_done()], ["8-1"])

    def test_readers_filter_by_character_and_sender(self):
        self.led.ingest([done(8, 1), done(8, 2, char_id="c:8"), done(8, 3, facts={"errand": 9})])
        self.assertEqual([g["event_id"] for g in self.led.errands_done(char_id=C)], ["8-1", "8-3"])
        self.assertEqual(len(self.led.errands_done(npc_id=S)), 3)
        self.assertEqual(self.led.errands_done(npc_id=R), [])


if __name__ == "__main__":
    unittest.main()
