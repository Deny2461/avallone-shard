"""Step 2e - the durable "story heard" record (design §8.1, §9.2, §10.1). Run: python3 -m unittest test_heard"""
import os
import random
import tempfile
import unittest

import madeup as madeup_mod
import memory
import rumour_speech
import rumours

CFG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "rumours.json")
WREN, OTHER = "22222222-2222-2222-2222-222222222222", "33333333-3333-3333-3333-333333333333"
NPC = "44444444-4444-4444-4444-444444444444"
H = 3600
NEAR_BRITAIN = {"map": "Trammel", "x": 1492, "y": 1641}


class HeardTests(unittest.TestCase):
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

    def ev(self, kind, facts, npc=rumours.WORLD_ID, char=WREN, epoch=1):
        self.seq += 1
        return {"event_id": f"{epoch}-{self.seq}", "epoch": epoch, "seq": self.seq, "npc_id": npc, "char_id": char,
                "kind": kind, "facts": facts, "at_utc": "2026-11-13T16:00:00Z", "schema": memory.SCHEMA_VERSION}

    def deed(self, typ, pt, subject="dragon", count=1, char=WREN):
        e = self.ev("deed", {"type": typ, "subject": subject, "count": count, "who": "Racer X", "descriptor": "a tamer",
                              "place": "Destard", "pt": pt, **NEAR_BRITAIN}, char=char)
        self.assertEqual(self.led.ingest([e])[1], [])
        return e["event_id"]

    def heard(self, story, hk, char=WREN, town="Britain", pt=100, epoch=1):
        e = self.ev("rumour_heard", {"story": story, "hk": hk, "town": town, "pt": pt, "mpt": pt}, npc=NPC,
                    char=char, epoch=epoch)
        return self.led.ingest([e])

    # ---- the record's shape ----
    def test_valid_record_is_stored_and_private(self):
        sid = self.deed("kill_notable", 10)
        acked, rej = self.heard(sid, f"s:{sid}@{'a' * 16}")
        self.assertEqual(rej, [])
        self.assertIn("rumour_heard", memory.PRIVATE_KINDS)
        self.assertEqual(self.r.heard_keys(WREN), {f"s:{sid}@{'a' * 16}"})
        self.assertEqual(self.r.heard_keys(OTHER), set())

    def test_malformed_records_are_rejected(self):
        sid = self.deed("kill_notable", 10)
        bad = [
            {"story": sid, "hk": f"s:9-9@{'a' * 16}", "town": "Britain", "pt": 1, "mpt": 1},        # key of another story
            {"story": sid, "hk": f"s:{sid}@{'a' * 15}", "town": "Britain", "pt": 1, "mpt": 1},      # short version
            {"story": sid, "hk": f"s:{sid}@{'a' * 16}", "town": "Britain", "pt": 1, "mpt": 1, "say": "x"},  # words
            {"story": "mu-12345678-abcd-1", "hk": "mk:87654321", "town": "Britain", "pt": 1, "mpt": 1},  # wrong content
            {"story": sid, "hk": f"s:{sid}@{'a' * 16}", "town": "Brit<ain", "pt": 1, "mpt": 1},     # odd town
            {"story": sid, "hk": f"s:{sid}@{'a' * 16}", "town": "Britain", "pt": 1, "mpt": 5},      # clock ahead
        ]
        for f in bad:
            _acked, rej = self.led.ingest([self.ev("rumour_heard", f, npc=NPC)])
            self.assertEqual(len(rej), 1, f)
        self.assertEqual(self.r.heard_keys(WREN), set())

    def test_madeup_record_with_its_own_content_key(self):
        sid = "mu-12345678-abcd-1"
        _acked, rej = self.heard(sid, "mk:12345678")
        self.assertEqual(rej, [])
        self.assertEqual(self.r.heard_keys(WREN), {"mk:12345678"})

    # ---- restore safety comes from the ledger (Codex R1-1) ----
    def test_world_restore_discards_tellings_from_the_abandoned_branch(self):
        sid = self.deed("kill_notable", 10)                     # seq 1
        self.heard(sid, f"s:{sid}@{'a' * 16}")                  # seq 2 - before the save
        self.heard(sid, f"s:{sid}@{'b' * 16}")                  # seq 3 - after the save
        verdict, _rolled, _why = self.led.reconcile(2, 1, 2)    # restore to the save holding seq <= 2
        self.assertIn(verdict, ("ok", "fresh"))
        self.assertEqual(self.r.heard_keys(WREN), {f"s:{sid}@{'a' * 16}"})
        # a late acknowledgement from the abandoned branch arrives rolled back, never live
        self.seq = 3
        self.heard(sid, f"s:{sid}@{'c' * 16}")                  # epoch 1, seq 4 > saved 2
        self.assertEqual(self.r.heard_keys(WREN), {f"s:{sid}@{'a' * 16}"})

    # ---- versions: what the listener would hear (Codex R2-2, R3 Q2) ----
    def version_of(self, pt):
        g = self.r._fix(self.r._group(self.r._deeds(pt)))
        return self.r.version(g[-1], [], "Britain"), g[-1]

    def test_unspoken_count_does_not_make_new_news(self):
        self.deed("drunk", 1 * H, subject="")
        v1, g1 = self.version_of(1 * H)
        self.deed("drunk", 2 * H, subject="")
        v2, g2 = self.version_of(2 * H)
        self.assertEqual(g1["id"], g2["id"])
        self.assertEqual(g2["count"], 2)
        self.assertEqual(v1, v2)

    def test_many_saturates_but_one_to_two_changes(self):
        self.deed("kill_notable", 0)
        v1, _ = self.version_of(0)
        self.deed("kill_notable", 60)
        v2, _ = self.version_of(60)
        self.assertNotEqual(v1, v2)                               # "a dragon" -> "2 dragons"
        for i in range(3):
            self.deed("kill_notable", 120 + i * 60)
        v5, g5 = self.version_of(240)
        for i in range(4):
            self.deed("kill_notable", 300 + i * 60)
        v9, g9 = self.version_of(480)
        self.assertEqual((g5["count"], g9["count"]), (5, 9))
        self.assertEqual(v5, v9)                                  # both "many dragons"

    def test_garble_changes_the_version_and_name_does_not(self):
        self.deed("kill_notable", 0)
        g = self.r._group(self.r._deeds(0))[-1]
        plain = self.r.version(g, [], "Britain")
        self.assertNotEqual(plain, self.r.version(g, ["name_vague"], "Britain"))
        g2 = dict(g, facts=dict(g["facts"], who="Somebody Else Entirely"))
        self.assertEqual(plain, self.r.version(g2, [], "Britain"))

    # ---- selection ----
    def test_greeting_never_volunteers_a_story_already_heard(self):
        sid = self.deed("kill_notable", 0, char=OTHER)
        rows = self.r.speech_rows(H, "Britain")
        hk = next(r["hk"] for r in rows if r["id"] == sid)
        for seed in range(20):
            off = rumour_speech.select(self.r, H, "Britain", WREN, rumour_speech.MODE_GREET, rng=random.Random(seed),
                                       heard={hk})
            self.assertTrue(off is None or sid not in off.ids)
        offered = [rumour_speech.select(self.r, H, "Britain", WREN, rumour_speech.MODE_GREET, rng=random.Random(s))
                   for s in range(20)]
        self.assertTrue(any(o is not None and sid in o.ids for o in offered))   # without the record it can be told

    def test_a_new_version_is_news_again(self):
        sid = self.deed("kill_notable", 0, char=OTHER)
        old = next(r["hk"] for r in self.r.speech_rows(H, "Britain") if r["id"] == sid)
        self.deed("kill_notable", 60, char=OTHER)                   # now "2 dragons": different words
        new = next(r["hk"] for r in self.r.speech_rows(H, "Britain") if r["id"] == sid)
        self.assertNotEqual(old, new)
        offered = [rumour_speech.select(self.r, H, "Britain", WREN, rumour_speech.MODE_GREET, rng=random.Random(s),
                                        heard={old}) for s in range(20)]
        self.assertTrue(any(o is not None and sid in o.ids for o in offered))

    def test_offer_carries_keys_and_reply_reports_only_delivered(self):
        sid = self.deed("kill_notable", 0, char=OTHER)
        off = None
        for s in range(30):
            off = rumour_speech.select(self.r, H, "Britain", WREN, rumour_speech.MODE_NEWS, rng=random.Random(s))
            if off is not None and sid in off.ids:
                break
        self.assertIsNotNone(off)
        self.assertTrue(off.hk[sid].startswith(f"s:{sid}@"))
        self.assertEqual(off.town, "Britain")
        told_but_not_delivered = rumour_speech.wire_reply(off, [sid], delivered=[])
        self.assertNotIn("delivered", told_but_not_delivered)
        wired = rumour_speech.wire_reply(off, [sid], delivered=[sid])
        self.assertEqual(wired["delivered"], [{"story": sid, "hk": off.hk[sid], "town": "Britain"}])
        self.assertNotIn("delivered", rumour_speech.wire_reply(off, [sid], delivered=["1-999"]))   # not on offer

    def test_madeup_key_is_by_content_across_instances(self):
        travelling, one_off = "mu-9a36a163-ba79-c", "mo-9a36a163-6261d2-6"     # Codex R3's pair
        self.assertEqual(rumour_speech.hk_of({"id": travelling}), rumour_speech.hk_of({"id": one_off}))
        self.assertEqual(rumour_speech.hk_of({"id": travelling}), "mk:9a36a163")
        self.assertIsNone(rumour_speech.hk_of({"id": rumour_speech.LORE_SLOT}))
        self.assertEqual(madeup_mod.key_of(one_off), "9a36a163")


if __name__ == "__main__":
    unittest.main()
