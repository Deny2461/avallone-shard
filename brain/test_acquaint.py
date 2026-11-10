"""Stage A3 step 1 (2026-11-10): an NPC remembers WHO it has traded lines with (acquaint.py, memory.Ledger.acquaintances),
and nothing else may see those rows. Covers the design v2 and Codex design R1/R2: strict row shape and stored round trip;
isolation (retrieval in both prompt layouts, the full-text index, reflection inputs AND scheduling are identical with and
without talked_with rows); the reconciled-read gate (unavailable is not empty); fade, future clock, deletion and rollback
applied before grouping and the queue cap; the 400-row examined tail; wording buckets; and both prompt paths through the
real handlers with a faked model. Run: python3 test_acquaint.py"""
import json
import os
import sys
import tempfile
import threading
import unittest
import urllib.request
from datetime import datetime, timedelta, timezone
from http.server import ThreadingHTTPServer
from unittest import mock

_d = tempfile.TemporaryDirectory()
with open(os.path.join(_d.name, "c.json"), "w") as f:
    json.dump({"memory_db": os.path.join(_d.name, "x.db"), "secrets_dir": _d.name, "quiet_windows": [],
               "prompt_layout": "v2", "reflection_mode": "off", "lore_dir": _d.name}, f)
os.environ["AVALLONE_BRAIN_CONFIG"] = os.path.join(_d.name, "c.json")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import acquaint  # noqa: E402
import brain  # noqa: E402
import cards  # noqa: E402
import memory  # noqa: E402
import reflection  # noqa: F401,E402  (adds pending_reflections to the ledger)

NPC = "33333333-3333-4333-8333-333333333333"        # the one who remembers (a townsperson)
CHAR = "44444444-4444-4444-8444-444444444444"       # the owner
CHAR2 = "44444444-4444-4444-8444-444444444445"
ELS = "55555555-5555-4555-8555-555555555555"        # the companion
HOUR = 3600.0
FADE = 120 * HOUR


def oid(n):
    return f"66666666-6666-4666-8666-{n:012d}"


def talked(epoch, seq, other=ELS, day="2026-11-10", mpt=1000.0, npc=NPC, char=CHAR, lines=3, kind="companion"):
    return {"event_id": f"{epoch}-{seq}", "epoch": epoch, "seq": seq, "npc_id": npc, "char_id": char, "kind": "talked_with",
            "facts": {"day": day, "mpt": mpt, "other_kind": kind, "other_id": other, "lines": lines},
            "at_utc": "2026-11-10T15:00:00Z", "schema": memory.SCHEMA_VERSION}


def plain(epoch, seq, kind="fight_survived", npc=NPC, char=CHAR, **facts):
    return {"event_id": f"{epoch}-{seq}", "epoch": epoch, "seq": seq, "npc_id": npc, "char_id": char, "kind": kind,
            "facts": facts, "at_utc": "2026-11-10T15:00:00Z", "schema": memory.SCHEMA_VERSION}


class Base(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.led.reconcile(1, 0, 0)

    def tearDown(self):
        self.led.db.close()
        self.led.rdb.close()
        self.dir.cleanup()

    def read(self, npc=NPC, epoch=1, now=1000.0, fade=FADE, **kw):
        return self.led.acquaintances(npc, epoch, now, fade, **kw)


class RowShape(unittest.TestCase):
    GOOD = {"day": "2026-11-10", "mpt": 79956.5, "other_kind": "companion", "other_id": ELS, "lines": 3}

    def test_good_row_is_accepted_positive_control(self):
        acquaint.check_facts(dict(self.GOOD))
        for k in acquaint.OTHER_KINDS:
            acquaint.check_facts(dict(self.GOOD, other_kind=k))
        acquaint.check_facts(dict(self.GOOD, mpt=0, lines=1))
        acquaint.check_facts(dict(self.GOOD, lines=10))

    def test_every_other_shape_is_refused(self):
        bad = [
            None, [], "x", {},
            {k: v for k, v in self.GOOD.items() if k != "lines"},                    # a key missing
            dict(self.GOOD, name="Elspeth Vale"),                                    # a name is never stored
            dict(self.GOOD, text="what was said"), dict(self.GOOD, town="Britain"), dict(self.GOOD, topics=["road"]),
            dict(self.GOOD, day="10/11/2026"), dict(self.GOOD, day="2026-13-40"), dict(self.GOOD, day=20261110),
            dict(self.GOOD, mpt=-1), dict(self.GOOD, mpt=float("nan")), dict(self.GOOD, mpt=float("inf")),
            dict(self.GOOD, mpt=True), dict(self.GOOD, mpt="5"),
            dict(self.GOOD, other_kind="player"), dict(self.GOOD, other_kind=""),
            dict(self.GOOD, other_id="Elspeth Vale"), dict(self.GOOD, other_id="AAAAAAAA-5555-4555-8555-555555555555"), dict(self.GOOD, other_id=None),
            dict(self.GOOD, lines=0), dict(self.GOOD, lines=11), dict(self.GOOD, lines=True), dict(self.GOOD, lines=2.0),
            {"day": "2026-11-10", "mpt": 1.0, "lines": 3, "other": {"kind": "companion", "id": ELS}},   # the v1 nested shape
        ]
        for b in bad:
            with self.subTest(b=b), self.assertRaises(ValueError):
                acquaint.check_facts(b)

    def test_read_request_shapes(self):
        self.assertEqual(acquaint.parse_known({"epoch": 3, "mpt": 5, "other_id": ELS}), {"epoch": 3, "mpt": 5.0, "other_id": ELS})
        for b in (None, {}, {"epoch": 0, "mpt": 5, "other_id": ELS}, {"epoch": True, "mpt": 5, "other_id": ELS},
                  {"epoch": 3, "mpt": -1, "other_id": ELS}, {"epoch": 3, "mpt": float("nan"), "other_id": ELS},
                  {"epoch": 3, "mpt": 5, "other_id": "x"}, {"epoch": "3", "mpt": 5, "other_id": ELS}):
            self.assertIsNone(acquaint.parse_known(b), b)


class Ingest(Base):
    def test_stored_round_trip_and_never_searchable(self):
        acked, rejected = self.led.ingest([talked(1, 1, mpt=79956.5)])
        self.assertEqual((acked, rejected), (["1-1"], []))
        row = self.led.db.execute("SELECT kind, facts, status FROM events WHERE event_id = '1-1'").fetchone()
        self.assertEqual((row["kind"], row["status"]), ("talked_with", "live"))
        self.assertEqual(json.loads(row["facts"]),
                         {"day": "2026-11-10", "mpt": 79956.5, "other_kind": "companion", "other_id": ELS, "lines": 3})
        self.assertEqual(self.led.db.execute("SELECT COUNT(*) FROM events_fts WHERE event_id = '1-1'").fetchone()[0], 0)
        self.led.ingest([plain(1, 2, text="a wolf")])                       # control: an ordinary event IS searchable
        self.assertEqual(self.led.db.execute("SELECT COUNT(*) FROM events_fts WHERE event_id = '1-2'").fetchone()[0], 1)

    def test_bad_row_is_rejected_and_the_rest_of_the_batch_goes_on(self):
        bad = talked(1, 1)
        bad["facts"]["name"] = "Elspeth Vale"
        acked, rejected = self.led.ingest([bad, talked(1, 2)])
        self.assertEqual((acked, rejected), (["1-2"], ["1-1"]))
        self.assertEqual(self.led.db.execute("SELECT COUNT(*) FROM events").fetchone()[0], 1)

    def test_database_is_version_6_with_the_index(self):
        self.assertEqual(self.led.db.execute("PRAGMA user_version").fetchone()[0], 6)
        self.assertTrue(self.led.db.execute("SELECT 1 FROM sqlite_master WHERE name = 'events_talked'").fetchone())

    def test_a_version_5_database_is_migrated_in_place(self):
        path = os.path.join(self.dir.name, "old.db")
        old = memory.Ledger(path)
        old.reconcile(1, 0, 0)
        old.ingest([plain(1, 1, text="kept")])
        old.db.execute("DROP INDEX events_talked")
        old.db.execute("PRAGMA user_version = 5")
        old.db.close()
        old.rdb.close()
        new = memory.Ledger(path)
        self.assertEqual(new.db.execute("PRAGMA user_version").fetchone()[0], 6)
        self.assertEqual(new.db.execute("SELECT COUNT(*) FROM events").fetchone()[0], 1)
        self.assertEqual(new.acquaintances(NPC, 1, 0.0, FADE), {})
        new.db.close()
        new.rdb.close()


class Reader(Base):
    def test_unavailable_is_not_empty(self):
        fresh = memory.Ledger(os.path.join(self.dir.name, "f.db"))          # never reconciled
        self.assertIsNone(fresh.acquaintances(NPC, 1, 1000.0, FADE))
        fresh.db.close()
        fresh.rdb.close()
        self.assertEqual(self.read(), {})                                    # reconciled, nobody yet
        self.assertIsNone(self.read(epoch=2))                                # another shard start: not reconciled for it
        self.assertIsNone(self.read(epoch=True))
        self.assertIsNone(self.read(epoch="1"))

    def test_rows_of_a_discarded_future_are_not_read_before_this_start_reconciles(self):
        """Codex design R1-3: after a restore the newer rows are still 'live' until reconciliation runs."""
        self.led.ingest([talked(1, 1), talked(1, 2, other=oid(2))])
        self.assertEqual(set(self.read()), {ELS, oid(2)})
        self.assertIsNone(self.read(epoch=2))                                # the restored world's new start, before reconcile
        self.led.reconcile(2, 1, 1)                                          # world saved at (1, 1): 1-2 never happened
        self.assertEqual(set(self.read(epoch=2)), {ELS})
        self.assertIsNone(self.read(epoch=1))                                # the old epoch is no longer the answer

    def test_days_are_counted_not_rows(self):
        self.led.ingest([talked(1, 1, day="2026-11-08", mpt=100), talked(1, 2, day="2026-11-10", mpt=300),
                         talked(1, 3, day="2026-11-10", mpt=310),          # a second row the same day (after a restart)
                         talked(1, 4, day="2026-11-09", mpt=320)])         # out of calendar order: last_day is the latest DAY
        self.assertEqual(self.read(), {ELS: {"days": 3, "last_day": "2026-11-10"}})

    def test_only_this_rememberer(self):
        self.led.ingest([talked(1, 1), talked(1, 2, npc=ELS, other=NPC, kind="townsperson")])
        self.assertEqual(set(self.read()), {ELS})
        self.assertEqual(set(self.read(npc=ELS)), {NPC})
        self.assertEqual(self.read(npc=oid(99)), {})

    def test_fade_edge_and_the_clocks_future(self):
        self.led.ingest([talked(1, 1, other=oid(1), mpt=0.0), talked(1, 2, other=oid(2), mpt=1.0),
                         talked(1, 3, other=oid(3), mpt=FADE + 500)])
        self.assertEqual(set(self.read(now=FADE)), {oid(1), oid(2)})        # exactly the fade age is still remembered
        self.assertEqual(set(self.read(now=FADE + 0.5)), {oid(2)})          # a moment older is gone
        self.assertEqual(set(self.read(now=FADE + 600)), {oid(3)})          # 3 was in the clock's future until now

    def test_deleted_owner_is_hidden_and_a_rolled_back_deletion_brings_it_back(self):
        self.led.ingest([talked(1, 1, char=CHAR), talked(1, 2, other=oid(2), char=CHAR2)])
        self.led.ingest([plain(1, 3, kind="character_deleted", char=CHAR)])
        self.assertEqual(set(self.read()), {oid(2)})
        self.led.reconcile(2, 1, 2)                                          # restore to before the deletion
        self.assertEqual(set(self.read(epoch=2)), {ELS, oid(2)})

    def test_queue_keeps_the_eight_newest_speakers_grouped_before_the_cap(self):
        evs = [talked(1, 1, other=oid(0), mpt=1)]                            # the oldest: the ninth, falls off
        evs += [talked(1, 2 + i, other=oid(1 + i), mpt=10 + i) for i in range(7)]
        evs += [talked(1, 20 + i, other=oid(50), day=f"2026-10-{1 + i:02d}", mpt=100 + i) for i in range(25)]   # one chatty peer
        self.led.ingest(evs)
        got = self.read()
        self.assertEqual(set(got), {oid(50)} | {oid(1 + i) for i in range(7)})
        self.assertEqual(got[oid(50)]["days"], 25)                           # 25 rows did not crowd the other seven out
        self.assertEqual(list(got)[0], oid(50))                              # newest speaker first
        self.assertEqual(set(self.read(queue=2)), {oid(50), oid(7)})

    def test_tail_counts_examined_rows_including_skipped_ones(self):
        """Codex design R2: 400 is rows EXAMINED. A long deleted prefix hides whoever lies beyond it - and stays bounded."""
        self.led.ingest([talked(1, 1, other=oid(1), mpt=1)])                              # older, valid
        self.led.ingest([talked(1, 2 + i, other=oid(2), char=CHAR2, mpt=5) for i in range(150)])
        self.assertEqual(set(self.read()), {oid(1), oid(2)})
        self.assertEqual(set(self.read(tail=150)), {oid(2)})                              # 1-1 is the 151st row
        self.led.ingest([plain(1, 500, kind="character_deleted", char=CHAR2)])
        self.assertEqual(self.read(tail=150), {})                                         # 150 examined, all deleted: nobody
        self.assertEqual(set(self.read(tail=151)), {oid(1)})

    def test_bounded_work_at_100000_rows(self):
        rows = [(f"1-{i}", 1, i, NPC, CHAR, "talked_with",
                 json.dumps({"day": "2026-11-10", "mpt": float(i), "other_kind": "townsperson", "other_id": oid(i % 5000), "lines": 2}),
                 "2026-11-10T15:00:00Z", 1, "live", "2026-11-10T15:00:00Z") for i in range(1, 100001)]
        self.led.db.execute("BEGIN")
        self.led.db.executemany("INSERT INTO events VALUES (?,?,?,?,?,?,?,?,?,?,?)", rows)
        self.led.db.execute("COMMIT")
        plan = " | ".join(r[3] for r in self.led.rdb.execute(
            """EXPLAIN QUERY PLAN SELECT char_id, facts FROM events INDEXED BY events_talked
               WHERE npc_id = ? AND kind = 'talked_with' AND status = 'live' ORDER BY epoch DESC, seq DESC LIMIT ?""", (NPC, 400)))
        self.assertIn("events_talked", plan)
        self.assertNotIn("TEMP B-TREE", plan.upper())                        # no sort: the index gives the order
        seen = []
        self.led.rdb.set_progress_handler(lambda: seen.append(1) or 0, 1000)
        got = self.read(now=200000.0, fade=1e12)
        self.led.rdb.set_progress_handler(None, 0)
        self.assertEqual(len(got), 8)
        self.assertEqual(list(got)[0], oid(100000 % 5000))                   # the newest row's speaker
        self.assertLess(len(seen), 40)                                       # under 40,000 VM steps: the history is not walked


class DeletionLookupIsBounded(Base):
    """Codex code R1 (A3-C1): the read must not grow with the number of deleted characters in the world."""
    def fill(self, n):
        ev, tomb = [], []
        for i in range(n):
            cid = f"77777777-7777-4777-8777-{i:012d}"
            eid = f"1-{1000 + i}"
            status = "live" if i % 2 == 0 else "rolled_back"          # half the deletions were undone by a restore
            ev.append((eid, 1, 1000 + i, NPC, cid, "character_deleted", "{}",
                       "2026-11-10T15:00:00Z", 1, status, "2026-11-10T15:00:00Z"))
            tomb.append((cid, "2026-11-10T15:00:00Z", eid))
        self.led.db.execute("BEGIN")
        self.led.db.executemany("INSERT INTO events VALUES (?,?,?,?,?,?,?,?,?,?,?)", ev)
        self.led.db.executemany("INSERT INTO tombstones VALUES (?,?,?)", tomb)
        self.led.db.execute("COMMIT")

    def steps(self):
        seen = []
        self.led.rdb.set_progress_handler(lambda: seen.append(1) or 0, 100)
        got = self.read()
        self.led.rdb.set_progress_handler(None, 0)
        return len(seen), got

    def test_unrelated_deletions_do_not_grow_the_read(self):
        self.led.ingest([talked(1, 1)])
        base, got0 = self.steps()
        self.fill(100000)
        many, got1 = self.steps()
        self.assertEqual(got0, got1)
        self.assertEqual(set(got1), {ELS})
        self.assertLess(many, base + 20, (base, many))                 # within 2,000 VM steps of the empty world

    def test_live_and_rolled_back_deletions_of_examined_owners(self):
        self.led.ingest([talked(1, 1, other=oid(1), char=CHAR), talked(1, 2, other=oid(2), char=CHAR2)])
        self.led.ingest([plain(1, 3, kind="character_deleted", char=CHAR), plain(1, 4, kind="character_deleted", char=CHAR2)])
        self.assertEqual(self.read(), {})
        self.led.db.execute("UPDATE events SET status = 'rolled_back' WHERE event_id = '1-4'")   # CHAR2's deletion undone
        self.assertEqual(set(self.read()), {oid(2)})

    def test_more_owners_than_one_lookup_chunk(self):
        evs = [talked(1, 1 + i, other=oid(1), char=f"88888888-8888-4888-8888-{i:012d}", day=f"2026-{1 + i // 28:02d}-{1 + i % 28:02d}")
               for i in range(330)]
        self.led.ingest(evs[:200])
        self.led.ingest(evs[200:])
        self.led.ingest([plain(1, 900, kind="character_deleted", char=f"88888888-8888-4888-8888-{329:012d}")])   # in the 2nd chunk
        self.assertEqual(self.read(fade=1e12)[oid(1)]["days"], 329)


class Isolation(unittest.TestCase):
    """Codex design R1-1: with talked_with rows in the same pair, every existing reader gives the identical result."""
    T0 = datetime(2026, 11, 10, 15, 0, 0, tzinfo=timezone.utc)

    def build(self, with_talked, late_talked=False):
        d = tempfile.TemporaryDirectory()
        self.addCleanup(d.cleanup)
        led = memory.Ledger(os.path.join(d.name, "m.db"))
        self.addCleanup(led.db.close)
        self.addCleanup(led.rdb.close)
        led.reconcile(1, 0, 0)
        stamp = [self.T0]
        with mock.patch.object(memory, "now_utc", lambda: stamp[0].strftime("%Y-%m-%dT%H:%M:%SZ")):
            led.ingest([plain(1, 1, kind="joined", npc=ELS, text="She agreed to travel with you."),
                        plain(1, 2, kind="fight_survived", npc=ELS, text="You fought wolves together near Britain.", foes="wolves"),
                        plain(1, 3, kind="conversation", npc=ELS, player_said="do you like the road", she_said="Well enough."),
                        plain(1, 4, kind="region_entered", npc=ELS, region="Britain"),
                        plain(1, 5, kind="wounded", npc=ELS, text="She was badly hurt and you carried her.")])
            if with_talked:
                stamp[0] = self.T0 + timedelta(seconds=3000 if late_talked else 1)
                led.ingest([talked(1, 10 + i, npc=ELS, other=NPC, kind="townsperson", day=f"2026-11-{1 + i:02d}") for i in range(6)]
                           + [talked(1, 30, npc=NPC, other=ELS)])        # and a pair of nothing but talked_with
        return led

    def test_retrieval_is_identical_in_both_layouts(self):
        a, b = self.build(False), self.build(True)
        now = self.T0 + timedelta(days=1)
        for utter in ("", "do you remember the wolves", "britain road talked with", "talked"):
            for stable_first in (False, True):
                for records in (False, True):
                    kw = dict(now=now, stable_first=stable_first, records=records)
                    self.assertEqual(a.retrieve(ELS, CHAR, utter, **kw), b.retrieve(ELS, CHAR, utter, **kw), (utter, kw))
        self.assertTrue(a.retrieve(ELS, CHAR, "wolves", now=now)[0])        # control: there IS something to retrieve
        self.assertEqual(a.recent(ELS, CHAR), b.recent(ELS, CHAR))
        self.assertEqual(b.retrieve(NPC, CHAR, "talked with", now=now), ([], []))   # the talked-only pair recalls nothing
        self.assertEqual(b.recent(NPC, CHAR), [])

    def test_reflection_inputs_and_scheduling_are_identical(self):
        a, b, c = self.build(False), self.build(True), self.build(True, late_talked=True)
        for now in (self.T0 + timedelta(seconds=600), self.T0 + timedelta(seconds=1000), self.T0 + timedelta(seconds=5000)):
            want = a.pending_reflections(idle_s=900, min_events=2, now=now)
            self.assertEqual(want, b.pending_reflections(idle_s=900, min_events=2, now=now), now)
            # rows that arrived 3000 s later neither postpone the companion's reflection nor start one of their own
            self.assertEqual(want, c.pending_reflections(idle_s=900, min_events=2, now=now), now)
        ready = a.pending_reflections(idle_s=900, min_events=2, now=self.T0 + timedelta(seconds=1000))
        self.assertEqual([(p["npc_id"], len(p["events"])) for p in ready], [(ELS, 4)])   # control: one real batch
        self.assertEqual(a.reflection_status()["facts_waiting"], b.reflection_status()["facts_waiting"])


class Wording(unittest.TestCase):
    def test_buckets_at_their_edges(self):
        self.assertEqual([acquaint.how_often(n) for n in (1, 2, 4, 5, 40)], ["once", "a few times", "a few times", "often", "often"])
        t = "2026-11-10"
        self.assertEqual([acquaint.when_last(d, t) for d in ("2026-11-10", "2026-11-09", "2026-11-08", "2026-11-04", "2026-11-03",
                                                              "2026-11-12", "junk")],
                         ["earlier today", "yesterday", "a few days ago", "a few days ago", "a while back", "earlier today",
                          "a while back"])

    def test_sentence(self):
        s = acquaint.sentence({"days": 3, "last_day": "2026-11-09"}, "Elspeth", "2026-11-10")
        self.assertIn("You and Elspeth have chatted a few times before, last yesterday.", s)
        self.assertIn("do not invent any", s)
        self.assertIn("chatted once before, a while back.", acquaint.sentence({"days": 1, "last_day": "2026-10-01"}, "Osric", "2026-11-10"))
        self.assertEqual(acquaint.sentence(None, "Elspeth", "2026-11-10"), "")   # a miss adds nothing (Codex R1-6)
        self.assertEqual(acquaint.sentence({}, "Elspeth", "2026-11-10"), "")
        self.assertNotIn("not spoken", s)

    def test_calendar_days_not_the_game_clock(self):
        """A chat a week ago is 'a while back' even though the game clock has not moved since (nobody played)."""
        self.assertEqual(acquaint.when_last("2026-11-03", "2026-11-10"), "a while back")

    def test_fade_setting(self):
        self.assertEqual(acquaint.fade_hours({"acquaintance_fade_memory_hours": 60}), 60.0)
        for bad in (None, {}, {"acquaintance_fade_memory_hours": 0}, {"acquaintance_fade_memory_hours": "x"},
                    {"acquaintance_fade_memory_hours": True}):
            self.assertEqual(acquaint.fade_hours(bad), 120.0)
        self.assertEqual(cards.rules()["acquaintance_fade_memory_hours"], 120)   # the shipped value (the owner, 2026-11-10)


class Handlers(unittest.TestCase):
    """Both prompt paths through the real handlers, with a real ledger and a faked model."""
    ELSPETH = {"kind": "companion", "name": "Elspeth Vale", "title": "the field healer"}
    TANNER = {"kind": "townsperson", "name": "Osric", "title": "the tanner"}
    LEAD = "You and Elspeth have chatted a few times before"

    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.led.reconcile(7, 0, 0)
        today = acquaint.central_today(cards.CENTRAL)
        self.led.ingest([talked(7, 1, day="2026-11-01", mpt=10), talked(7, 2, day=today, mpt=20),
                         talked(7, 3, npc=ELS, other=NPC, kind="townsperson", day=today, mpt=20)])
        self.srv = ThreadingHTTPServer(("127.0.0.1", 0), brain.Handler)
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        self.sent, self.logs = [], []
        brain.end_background()
        brain.FOREGROUND["n"] = 0
        brain.CANCELLED_EXCHANGES.clear()
        for p in (mock.patch.object(brain, "LEDGER", self.led), mock.patch.object(brain, "log", lambda **k: self.logs.append(k)),
                  mock.patch.dict(brain.CFG, {"party_companion_share": 1.0, "party_pet_share": 0.0})):
            p.start()
            self.addCleanup(p.stop)

    def tearDown(self):
        self.srv.shutdown()
        self.srv.server_close()
        brain.end_background()
        self.led.db.close()
        self.led.rdb.close()
        self.dir.cleanup()

    def post(self, path, body):
        return json.load(urllib.request.build_opener().open(urllib.request.Request(
            f"http://127.0.0.1:{self.srv.server_port}{path}", data=json.dumps(body).encode(),
            headers={"Content-Type": "application/json"}), timeout=20))

    def exchange(self, known, speaker_is_npc=True):
        def fake(url, payload, timeout_s):
            self.sent.append(json.loads(payload))
            return {"message": {"content": json.dumps({"say": "Good to see you again."})}, "prompt_eval_count": 1, "eval_count": 1}
        if speaker_is_npc:
            x = {"id": "x1", "step": 1, "speaker": dict(self.TANNER), "addressee": dict(self.ELSPETH), "trust_band": "",
                 "transcript": [{"who": "Osric", "text": "Good day, Elspeth."}, {"who": "Elspeth Vale", "text": "And to you."}],
                 "roster": []}
            body = {"npc_id": NPC, "persona_key": "townsfolk", "persona_vars": {"name": "Osric", "role": "tanner", "town": "Britain", "trade": "vendor"}}
        else:
            x = {"id": "x1", "step": 0, "speaker": dict(self.ELSPETH), "addressee": dict(self.TANNER), "trust_band": "warming",
                 "transcript": [{"who": "Osric", "text": "Good day, Elspeth."}], "roster": []}
            body = {"npc_id": ELS, "persona_key": "elspeth"}
        if known is not None:
            x["known"] = known
        body.update({"request_id": "t", "subject_id": CHAR, "priority": "Background", "budget_ms": 6000, "exchange": x})
        with mock.patch.object(brain, "_post_cancellable", fake):
            out = self.post("/v1/npc/exchange", body)
        rec = next(x for x in reversed(self.logs) if x.get("event") == "exchange")
        return out, json.dumps(self.sent[-1]["messages"]), rec

    def test_exchange_prompt_says_they_have_met(self):
        out, prompt, rec = self.exchange({"epoch": 7, "mpt": 30.0, "other_id": ELS})
        self.assertTrue(out["ok"])
        self.assertIn(self.LEAD + ", last earlier today.", prompt)
        self.assertEqual(rec["acq"], "known")
        out, prompt, rec = self.exchange({"epoch": 7, "mpt": 30.0, "other_id": NPC}, speaker_is_npc=False)   # her direction
        self.assertIn("You and Osric have chatted once before, earlier today.", prompt)
        self.assertEqual(rec["acq"], "known")

    def test_no_sentence_on_a_miss_unavailable_or_off(self):
        cases = [({"epoch": 7, "mpt": 30.0, "other_id": oid(9)}, "none"),            # nobody she knows
                 ({"epoch": 8, "mpt": 30.0, "other_id": ELS}, "unavailable"),        # this shard start has not reconciled
                 ({"epoch": 7, "mpt": 30.0 + 121 * HOUR, "other_id": ELS}, "none"),  # faded
                 (None, "off"), ({"epoch": 7, "mpt": 30.0}, "off"), ({"epoch": 7, "other_id": ELS}, "off")]
        for known, state in cases:
            out, prompt, rec = self.exchange(known)
            self.assertTrue(out["ok"], known)                                         # the exchange itself carries on
            self.assertNotIn("have chatted", prompt, known)
            self.assertNotIn("not spoken", prompt)
            self.assertEqual(rec["acq"], state, known)
        with mock.patch.dict(brain.CFG, {"acquaintance": False}):                     # the brain-side stop
            out, prompt, rec = self.exchange({"epoch": 7, "mpt": 30.0, "other_id": ELS})
            self.assertEqual((out["ok"], rec["acq"]), (True, "off"))
            self.assertNotIn("have chatted", prompt)

    def test_a_broken_ledger_costs_the_sentence_never_the_line(self):
        class Raiser:
            def __getattr__(self, name):
                raise AssertionError("ledger down")
        with mock.patch.object(brain, "LEDGER", Raiser()):
            out, prompt, rec = self.exchange({"epoch": 7, "mpt": 30.0, "other_id": ELS})
        self.assertEqual((out["ok"], rec["acq"]), (True, "unavailable"))
        self.assertNotIn("have chatted", prompt)

    def test_a_broken_rules_file_costs_the_sentence_never_the_line(self):
        """Codex code R1 (A3-C2): with A3 off, or nothing asked, the rules file is not even opened."""
        boom = mock.patch.object(cards, "rules", side_effect=OSError("memory_rules.json unreadable"))
        with boom as rules:
            out, prompt, rec = self.exchange(None)                                    # the shard asked for nothing
            self.assertEqual((out["ok"], rec["acq"], rules.call_count), (True, "off", 0))
            with mock.patch.dict(brain.CFG, {"acquaintance": False}):                 # the brain switch is off
                out, prompt, rec = self.exchange({"epoch": 7, "mpt": 30.0, "other_id": ELS})
                self.assertEqual((out["ok"], rec["acq"], rules.call_count), (True, "off", 0))
            out, prompt, rec = self.exchange({"epoch": 7, "mpt": 30.0, "other_id": ELS})   # asked, and the file is broken
            self.assertEqual((out["ok"], rec["acq"], rules.call_count), (True, "unavailable", 1))
            self.assertNotIn("have chatted", prompt)
            self.assertEqual(len(self.sent), 3)                                       # the model was called every time
            # the ordinary reply keeps its whole A1 paragraph; only the added sentence is gone
            out, prompt, rec = self.respond(self.party())
            self.assertTrue(out["ok"])
            self.assertIn("one short separate sentence to or about Elspeth", prompt)
            self.assertNotIn("have chatted", prompt)
            self.assertEqual((rec["party"]["turn"], rec["party"]["acq"]), ("companion", "unavailable"))
            out, prompt, rec = self.respond(self.party(known=None))
            self.assertIn("one short separate sentence to or about Elspeth", prompt)
            self.assertEqual(rec["party"]["acq"], "off")
        out, prompt, rec = self.exchange({"epoch": 7, "mpt": 30.0, "other_id": ELS})  # positive control: file readable
        self.assertEqual(rec["acq"], "known")
        self.assertIn(self.LEAD, prompt)

    def test_remark_never_raises(self):
        class Bad:
            def acquaintances(self, *a):
                return {ELS: {"days": "x"}}                                          # a malformed entry
        k = {"epoch": 7, "mpt": 30.0, "other_id": ELS}
        self.assertEqual(acquaint.remark(Bad(), NPC, k, "Elspeth", cards.rules, True, cards.CENTRAL), ("unavailable", ""))
        self.assertEqual(acquaint.remark(self.led, NPC, k, "Elspeth", lambda: 1 / 0, True, cards.CENTRAL), ("unavailable", ""))
        self.assertEqual(acquaint.remark(self.led, NPC, k, "", cards.rules, True, cards.CENTRAL), ("none", ""))
        self.assertEqual(acquaint.remark(self.led, NPC, k, "Elspeth", cards.rules, True, cards.CENTRAL)[0], "known")

    def respond(self, party, companion_share=1.0):
        class R:
            def __init__(s, b): s.b = b
            def __enter__(s): return s
            def __exit__(s, *a): return False
            def read(s, *a): return s.b

        def fake(rq, timeout=None):
            self.sent.append(json.loads(rq.data))
            return R(json.dumps({"message": {"content": json.dumps({"say": "Good day to you.", "action": "none"})},
                                 "prompt_eval_count": 1, "eval_count": 1}).encode())
        body = {"request_id": "t", "npc_id": NPC, "subject_id": CHAR, "priority": "Conversation", "persona": "PERSONA",
                "utterance": "Good day.", "budget_ms": 8000, "history": [], "party": party}
        with mock.patch.object(brain.urllib.request, "urlopen", fake), \
                mock.patch.dict(brain.CFG, {"party_companion_share": companion_share}):
            out = self.post("/v1/npc/respond", body)
        rec = next(x for x in reversed(self.logs) if x.get("event") == "respond")
        return out, json.dumps(self.sent[-1]["messages"]), rec

    def party(self, **over):
        p = {"companions": [{"name": "Elspeth Vale", "title": "the field healer", "id": ELS}], "pets": [], "nudge": False,
             "known": {"epoch": 7, "mpt": 30.0}}
        p.update(over)
        return p

    def test_ordinary_reply_on_a_companion_turn(self):
        out, prompt, rec = self.respond(self.party())
        self.assertTrue(out["ok"])
        self.assertIn(self.LEAD + ", last earlier today.", prompt)
        self.assertEqual((rec["party"]["turn"], rec["party"]["acq"]), ("companion", "known"))

    def test_ordinary_reply_quiet_turn_or_no_request_adds_nothing(self):
        out, prompt, rec = self.respond(self.party(), companion_share=0.0)            # the dice said: leave them be
        self.assertNotIn("have chatted", prompt)
        self.assertEqual((rec["party"]["turn"], rec["party"]["acq"]), ("quiet", "off"))
        for p in (self.party(known=None), self.party(companions=[{"name": "Elspeth Vale", "title": "the field healer"}]),
                  self.party(known={"epoch": 8, "mpt": 30.0}),
                  self.party(companions=[{"name": "Elspeth Vale", "title": "the field healer", "id": oid(9)}])):
            out, prompt, rec = self.respond(p)
            self.assertTrue(out["ok"])
            self.assertNotIn("have chatted", prompt, p)
            self.assertIn("one short separate sentence to or about Elspeth", prompt)  # the A1 paragraph is unchanged

    def test_a_name_that_now_clashes_with_a_player_is_called_by_title(self):
        """The row holds no name: the label comes from THIS request under A1's rules (a player 'Elspeth Gray' exists now)."""
        out, prompt, rec = self.respond(self.party(roster=["Elspeth Gray"]))
        self.assertIn("You and the field healer have chatted a few times before", prompt)
        self.assertNotIn("You and Elspeth", prompt)


if __name__ == "__main__":
    unittest.main()
