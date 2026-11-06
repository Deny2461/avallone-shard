"""Phase 5c-1: named NPC memory - claims-only notes, fixed attribution, familiarity from visit days, walls
(Codex 5c design review 2026-10-30, findings 1-4). Run: python3 test_named.py"""
import os
import tempfile
import unittest

import memory
import named
import reflection  # noqa: F401  (attaches pending_reflections)

MIRA, ALWEN = "11111111-1111-1111-1111-111111111111", "55555555-5555-5555-5555-555555555555"
WREN, ALT = "22222222-2222-2222-2222-222222222222", "44444444-4444-4444-4444-444444444444"
ROSTER = [{"id": WREN, "name": "Brynn Handy"}, {"id": ALT, "name": "Thandor"}, {"name": "Racer X"}]


class Validate(unittest.TestCase):
    def test_good_notes(self):
        n = named.validate({"claims": ["they slew an ancient wyrm yesterday"], "topics": ["dragon hunting"]})
        self.assertEqual(n, {"claims": ["they slew an ancient wyrm yesterday"], "topics": ["dragon hunting"]})

    def test_empty_is_fine(self):
        self.assertEqual(named.validate({"claims": [], "topics": []}), {"claims": [], "topics": []})

    def test_certainty_wording_drops_the_summary(self):
        for bad in ("you learned they are a knight", "it happened near Yew", "it is true they are rich",
                    "this is a known fact"):
            with self.assertRaises(named.Unusable, msg=bad):
                named.validate({"claims": [bad], "topics": []})

    def test_names_scrubbed(self):
        n = named.validate({"claims": ["Brynn Handy says Thandor is a cultist", "Racer X owes them coin"], "topics": []},
                           speaker="Brynn Handy", roster=ROSTER)
        self.assertEqual(n["claims"], ["they says someone else is a cultist", "someone else owes them coin"])

    def test_first_names_and_near_spellings_scrubbed(self):
        # dev play test 2026-10-30: a first name alone sank the whole note
        roster = ROSTER + [{"name": "Tharazyn"}]
        n = named.validate({"claims": ["Brynn owes them gold", "they met Tharazin at the bank", "their dog is Spot"],
                            "topics": ["Brynn's debt"]}, speaker="Legolas", roster=roster)
        self.assertEqual(n["claims"], ["someone else owes them gold", "they met someone else at the bank", "their dog is Spot"])
        self.assertEqual(n["topics"], ["someone else's debt"])
        n = named.validate({"claims": ["Brynn is me"], "topics": []}, speaker="Brynn Handy", roster=roster)
        self.assertEqual(n["claims"], ["they is them"])   # first person swapped too

    def test_one_bad_line_does_not_sink_the_note(self):
        # "Bryn Handy": neither word alone is a name, but together they are one letter off - the check still refuses it
        n = named.validate({"claims": ["they slew a wyrm", "Bryn Handy owes them"], "topics": []}, speaker="Legolas",
                           roster=ROSTER)
        self.assertEqual(n["claims"], ["they slew a wyrm"])

    def _notes(self, said, claims, topics=(), **kw):
        hist = [{"speaker": "player", "text": t} for t in said]
        return named.notes_from({"claims": [{"line": n, "text": t} for n, t in claims], "topics": list(topics)}, hist, **kw)

    def test_codex_round1_case_stores_nothing(self):
        n = self._notes(["Hello, lovely weather."], [(1, "they slew an ancient wyrm yesterday")], ["dragon hunting"])
        self.assertEqual(n, {"claims": [], "topics": []})

    def test_codex_round2_cases_store_nothing(self):
        for said, claim in ((["I did not kill the dragon."], (1, "kill the dragon")),
                            (["I did not kill the dragon."], (1, "they killed the dragon")),
                            (["I never killed the dragon."], (1, "killed the dragon")),
                            (["Did you kill the dragon?"], (1, "you kill the dragon")),
                            (["Did I kill the dragon"], (1, "I kill the dragon")),             # a question with no "?"
                            (["I owe 5 gold."], (1, "I owe 500 gold")),
                            (["Brynn killed the dragon.", "I hunt orcs."], (2, "I killed orcs")),
                            (["I killed it - no, I did not"], (1, "I killed it")),             # a correction
                            (["oh sure, I'm the king of Britannia"], (1, "I'm the king of Britannia")),   # sarcasm
                            (["the ale here is *wonderful*"], (1, "the ale here is *wonderful*")),
                            (["I slew a wyrm"], (2, "I slew a wyrm")),                          # no such line
                            (["I slew a wyrm"], (1, "slew a wyrm"))):                           # cut mid-clause
            self.assertEqual(self._notes(said, [claim])["claims"], [], msg=f"{said} -> {claim}")

    def test_faithful_statements_are_kept_by_code(self):
        n = self._notes(["Have you met my mule Tonk", "I slew an ancient wyrm in Destard yesterday",
                         "Thandor owes me gold", "No, I'm not going to Yew after all", "My name's Racer, I'm a tamer from Yew",
                         "I owe 5 gold."],
                        [(2, "I slew an ancient wyrm in Destard yesterday"), (3, "Thandor owes me gold"),
                         (4, "I'm not going to Yew after all")], ["dragon hunting", "the wyrm"], speaker="Racer X",
                        roster=ROSTER)
        self.assertEqual(n["claims"], ["they slew an ancient wyrm in Destard yesterday", "someone else owes them gold",
                                       "they are not going to Yew after all"])
        self.assertEqual(n["topics"], ["the wyrm"])            # "dragon hunting" shares no word with what they said
        n = self._notes(["My name's Racer, I'm a tamer from Yew", "I owe 5 gold."], [(1, "I'm a tamer from Yew"), (2, "I owe 5 gold")])
        self.assertEqual(n["claims"], ["they are a tamer from Yew", "they owe 5 gold"])   # digits kept exactly
        n = self._notes(["that wasn't me", "the queen is dead"], [(1, "that wasn't me"), (2, "the queen is dead")])
        self.assertEqual(n["claims"], ["that wasn't them", "the queen is dead"])

    def test_mine_the_verb_and_the_speakers_own_name(self):
        n = self._notes(["I can smith and mine", "the axe is mine", "My name's Racer, I'm a tamer from Yew"],
                        [(1, "I can smith and mine"), (2, "the axe is mine"), (3, "My name's Racer, I'm a tamer from Yew")],
                        ["Racer X status", "smithing"], speaker="Racer X", roster=ROSTER)
        self.assertEqual(n["claims"], ["they can smith and mine", "the axe is theirs"])
        self.assertEqual(n["topics"], ["smithing"])

    def test_malformed_items_are_rejected(self):
        for bad in ({"claims": ["a string, not a pointer"], "topics": []}, {"claims": [{"line": "1", "text": "x y"}], "topics": []},
                    {"claims": [], "topics": [7]}, {"claims": [{"line": True, "text": "I slew it"}], "topics": []}):
            with self.assertRaises(named.Unusable):
                named.notes_from(bad, [{"speaker": "player", "text": "I slew it"}])

    def test_shape(self):
        for bad in (None, [], {"claims": "x", "topics": []}, {"claims": ["a"] * 4, "topics": []}):
            with self.assertRaises(named.Unusable):
                named.validate(bad)

    def test_odd_characters_and_sentinels(self):
        for bad in ("they are ⟦P-ab12⟧", "Рacer wins", "[lowers voice] they are king"):
            with self.assertRaises(named.Unusable, msg=bad):
                named.validate({"claims": [bad], "topics": []})

    def test_render_fixed_attribution(self):
        line = named.render({"claims": ["they slew a wyrm"], "topics": ["a room", "dragons"]})
        self.assertEqual(line, 'They told you: "they slew a wyrm" (their word, not a known fact). '
                               "You talked about: a room, dragons.")
        self.assertIn("nothing much", named.render({"claims": [], "topics": []}))

    def test_transcript_is_player_only(self):
        h = [{"speaker": "player", "text": "I am the king"}, {"speaker": "npc", "text": "Then I am the queen."},
             {"speaker": "npc", "text": "[shared a piece of town gossip]"}, {"speaker": "player", "text": "  hi  "}]
        self.assertEqual(named.transcript(h), "1. I am the king\n2. hi")

    def test_no_inventing_memories(self):
        block = named.memory_block(["3 days ago: They stopped by; nothing much was said."], "To you they are a regular.")
        self.assertIn("never make up a memory", block)
        self.assertIn("never say you are keeping something for them", block)
        self.assertIn("news to you", block)
        self.assertNotIn("make up a memory", named.memory_block([], "You have never met this person before.", met=False))

    def test_familiarity(self):
        self.assertIn("never met", named.familiarity_line(0, None, "2026-10-30"))
        self.assertIn("someone new", named.familiarity_line(1, "2026-10-29", "2026-10-30"))
        self.assertNotIn("stranger", named.familiarity_line(1, "2026-10-30", "2026-10-30"))
        self.assertIn("first met them earlier today", named.familiarity_line(1, "2026-10-30", "2026-10-30"))
        self.assertIn("a known face", named.familiarity_line(2, "2026-10-28", "2026-10-30"))
        self.assertIn("last 2 days ago", named.familiarity_line(2, "2026-10-28", "2026-10-30"))
        self.assertIn("a regular", named.familiarity_line(5, "2026-10-30", "2026-10-30"))
        self.assertIn("an old friend", named.familiarity_line(12, "2026-10-30", "2026-10-30"))
        self.assertIn("tone only", named.familiarity_line(3, "2026-10-29", "2026-10-30"))


class Preempt(unittest.TestCase):
    """Codex 5c-1 code review finding 4: a conversation cuts the summary's model call at once."""

    def test_conversation_cuts_a_slow_summary(self):
        import threading
        import time
        from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
        import brain

        class Slow(BaseHTTPRequestHandler):
            def do_POST(self):
                self.rfile.read(int(self.headers.get("Content-Length") or 0))
                time.sleep(5)   # a summary near its budget
                try:
                    self.send_response(200)
                    self.end_headers()
                    self.wfile.write(b'{"done": true, "message": {"content": "{}"}}')
                except OSError:
                    pass

            def log_message(self, *a):
                pass

        srv = ThreadingHTTPServer(("127.0.0.1", 0), Slow)
        srv.daemon_threads = True
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        url = f"http://127.0.0.1:{srv.server_address[1]}/api/chat"
        out = {}

        def run():
            t = time.monotonic()
            try:
                brain._post_cancellable(url, b"{}", 8)
                out["result"] = "finished"
            except Exception as e:
                out["result"] = type(e).__name__
            out["s"] = time.monotonic() - t

        brain.begin_summary()
        th = threading.Thread(target=run)
        th.start()
        for _ in range(100):   # wait until the call is registered
            if brain.SUMMARY_CALL["conn"] is not None and brain.SUMMARY_CALL["conn"].sock is not None:
                break
            time.sleep(0.01)
        time.sleep(0.2)
        self.assertTrue(brain.preempt_summary())
        th.join(3)
        srv.shutdown()
        self.assertNotEqual(out.get("result"), "finished")
        self.assertLess(out["s"], 1.5)          # cut at once, not after the 5 s answer
        self.assertIsNone(brain.SUMMARY_CALL["conn"])
        brain.end_summary()
        self.assertFalse(brain.preempt_summary())   # nothing left to cut

    def test_preempted_while_connecting_never_sends(self):
        # Codex 5c-1 round 2 finding 3, its exact case: connect() paused, pre-empt, release - request() must never run
        import http.client
        import threading
        import brain
        gate, entered, sent = threading.Event(), threading.Event(), []
        old_connect, old_request = http.client.HTTPConnection.connect, http.client.HTTPConnection.request

        def slow_connect(self):
            entered.set()
            gate.wait(2)

        http.client.HTTPConnection.connect = slow_connect
        http.client.HTTPConnection.request = lambda self, *a, **k: sent.append(a)
        out = {}
        try:
            brain.begin_summary()

            def run():
                try:
                    brain._post_cancellable("http://127.0.0.1:9/api/chat", b"{}", 3)
                    out["r"] = "finished"
                except brain.Preempted:
                    out["r"] = "preempted"
                except Exception as e:
                    out["r"] = type(e).__name__

            th = threading.Thread(target=run)
            th.start()
            self.assertTrue(entered.wait(2))
            self.assertTrue(brain.preempt_summary())   # no socket yet - still cancels
            gate.set()
            th.join(3)
        finally:
            http.client.HTTPConnection.connect, http.client.HTTPConnection.request = old_connect, old_request
            brain.end_summary()
        self.assertEqual(out.get("r"), "preempted")
        self.assertEqual(sent, [])


class Ledger(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.led.reconcile(1, 0, 0)
        self.seq = 0
        self.epoch = 1

    def tearDown(self):
        self.led.db.close()
        self.led.rdb.close()
        self.dir.cleanup()

    def put(self, kind, facts, npc=MIRA, char=WREN):
        self.seq += 1
        eid = f"{self.epoch}-{self.seq}"
        acked, rej = self.led.ingest([{"event_id": eid, "epoch": self.epoch, "seq": self.seq, "npc_id": npc,
                                       "char_id": char, "kind": kind, "facts": facts,
                                       "at_utc": "2026-10-30T16:00:00Z", "schema": memory.SCHEMA_VERSION}])
        return eid, rej

    def test_shapes_checked_at_the_door(self):
        self.assertEqual(self.put("visited", {"day": "2026-10-30"})[1], [])
        for facts in ({"day": "yesterday"}, {"day": "2026-10-30", "x": 1}, {}):
            self.assertNotEqual(self.put("visited", facts)[1], [])
        self.assertEqual(self.put("visit_summary", {"claims": ["they slew a wyrm"], "topics": []})[1], [])
        for facts in ({"claims": ["a"] * 4, "topics": []}, {"claims": ["x" * 121]}, {"story": "it happened"}):
            self.assertNotEqual(self.put("visit_summary", facts)[1], [])

    def test_visit_days_distinct(self):
        for d in ("2026-10-28", "2026-10-28", "2026-10-29", "2026-10-30", "2026-10-30"):
            self.put("visited", {"day": d})
        self.assertEqual(self.led.visit_days(MIRA, WREN), (3, "2026-10-30"))

    def test_traded_shape_checked_and_not_a_visit(self):
        # 5c-2 2b: "traded" = one real vendor transfer per day; same one-field shape as "visited", never a visit day
        self.assertEqual(self.put("traded", {"day": "2026-11-01"})[1], [])
        for facts in ({"day": "today"}, {"day": "2026-11-01", "qty": 3}, {}):
            self.assertNotEqual(self.put("traded", facts)[1], [])
        self.assertEqual(self.led.visit_days(MIRA, WREN), (0, None))

    def test_gift_given_shape_checked(self):
        ok = {"day": "2026-11-01", "mpt": 3600, "kind": "gold", "value": "small"}
        self.assertEqual(self.put("gift_given", ok)[1], [])
        for bad in ({**ok, "kind": "sword"}, {**ok, "value": "huge"}, {**ok, "item": "Fish"}, {**ok, "mpt": -1},
                    {k: v for k, v in ok.items() if k != "mpt"}):
            self.assertNotEqual(self.put("gift_given", bad)[1], [])

    def test_walls_npc_and_character(self):
        self.put("visited", {"day": "2026-10-30"})
        self.put("visit_summary", {"claims": ["they are hunting a lich"], "topics": []})
        self.assertEqual(self.led.visit_days(ALWEN, WREN), (0, None))      # another NPC sees nothing
        self.assertEqual(self.led.visit_notes(ALWEN, WREN), [])
        self.assertEqual(self.led.visit_days(MIRA, ALT), (0, None))      # another character (alt) sees nothing
        self.assertEqual(self.led.visit_notes(MIRA, ALT), [])
        self.assertEqual(len(self.led.visit_notes(MIRA, WREN)), 1)

    def test_notes_wording_and_order(self):
        self.put("visit_summary", {"claims": ["they slew a wyrm"], "topics": ["dragons"]})
        self.put("visit_summary", {"claims": [], "topics": ["a room for the night"]})
        notes = self.led.visit_notes(MIRA, WREN)
        self.assertEqual(len(notes), 2)
        self.assertIn('They told you: "they slew a wyrm" (their word, not a known fact)', notes[0])
        self.assertIn("a room for the night", notes[1])

    def test_world_restore_rolls_back(self):
        self.put("visited", {"day": "2026-10-29"})                       # 1-1, saved
        self.put("visited", {"day": "2026-10-30"})                       # 1-2, after the save
        self.put("visit_summary", {"claims": ["they lost a boot"], "topics": []})   # 1-3, after the save
        verdict, rolled, _ = self.led.reconcile(2, 1, 1)                 # world restored to 1-1
        self.assertEqual(verdict, "ok")
        self.assertEqual(rolled, 2)
        self.assertEqual(self.led.visit_days(MIRA, WREN), (1, "2026-10-29"))
        self.assertEqual(self.led.visit_notes(MIRA, WREN), [])

    def test_deletion_tombstone_hides_everything(self):
        self.put("visited", {"day": "2026-10-30"})
        self.put("visit_summary", {"claims": ["they hate rats"], "topics": []})
        # Codex finding 3: the global tombstone recorded under the world id hides the character from every NPC
        self.put("character_deleted", {}, npc="a7a110e0-0000-4000-8000-000000000001")
        self.assertEqual(self.led.visit_days(MIRA, WREN), (0, None))
        self.assertEqual(self.led.visit_notes(MIRA, WREN), [])

    def test_never_reflected_on(self):
        for i in range(6):
            self.put("visited", {"day": f"2026-10-{20 + i}"})
            self.put("visit_summary", {"claims": [f"they are the king {i}"], "topics": []})
        self.assertEqual(self.led.pending_reflections(idle_s=0, min_events=1), [])


if __name__ == "__main__":
    unittest.main()
