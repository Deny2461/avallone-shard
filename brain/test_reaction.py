"""Rumour step C1 (2026-11-11): the companion's one-line reaction when an NPC mentions her while telling gossip.
The wall (Codex design R1, RUM-D1): her model call is given NOTHING the NPC said - checked here on the whole outgoing
request. Ollama is never called. Run: python3 -m unittest test_reaction"""
import json
import os
import sys
import tempfile
import threading
import unittest
import urllib.request
from http.server import ThreadingHTTPServer
from unittest import mock

_d = tempfile.TemporaryDirectory()
with open(os.path.join(_d.name, "c.json"), "w") as f:
    json.dump({"memory_db": os.path.join(_d.name, "x.db"), "lore_dir": _d.name, "secrets_dir": _d.name,
               "quiet_windows": [], "reflection_mode": "off"}, f)
os.environ["AVALLONE_BRAIN_CONFIG"] = os.path.join(_d.name, "c.json")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import brain  # noqa: E402
import exchange  # noqa: E402
import memory  # noqa: E402
import party  # noqa: E402
import rumour_speech  # noqa: E402
import rumours  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
NPC, ELS = "44444444-4444-4444-8444-444444444444", "5bacd751-d4b8-421a-b539-6aaa2948c6ef"
CHAR, OTHER = "22222222-2222-4222-8222-222222222222", "33333333-3333-4333-8333-333333333333"
ELSPETH = {"kind": "companion", "name": "Elspeth Vale", "title": "the field healer"}
TANNER = {"kind": "townsperson", "name": "Osric", "title": "the tanner"}
SECRET = "Brynn Ashby murdered a watchman behind the Blue Boar"       # what the NPC said: must never reach her call
MARKS = ("Brynn", "Ashby", "murdered", "watchman", "Blue Boar")         # words her own persona and the rules never use
H = 3600


def react(sort=1, **over):
    x = {"id": "x1", "step": 0, "speaker": dict(ELSPETH), "addressee": dict(TANNER), "trust_band": "warming",
         "roster": ["Brynn Ashby", "Aldren Thorn"], "last": True, "react": sort}
    x.update(over)
    return {"exchange": x}


class Parse(unittest.TestCase):
    def test_six_sorts_in_the_same_order_as_the_npcs_prompt(self):
        self.assertEqual(len(exchange.REACTIONS), len(party.ANGLES))
        for sort, angle, key in zip(exchange.REACTIONS, party.ANGLES, ("greet", "trade", "day", "welcome", "mood", "courtesy")):
            self.assertIn(key, sort)
            self.assertIn(key, angle)

    def test_the_reader_never_looks_at_a_transcript(self):
        a = exchange.parse_reaction(react(transcript=[{"who": "Osric", "text": SECRET}], known={"epoch": 1, "mpt": 2.0, "other_id": NPC},
                                          situation=SECRET, history=[{"speaker": "npc", "text": SECRET}]))
        self.assertEqual(a["transcript"], [])
        self.assertIsNone(a["known"])
        self.assertEqual((a["step"], a["last"], a["react"], a["band"]), (0, True, 1, "warming"))
        text = exchange.reaction_rules(a, "WEATHER.", off_road=True) + exchange.reaction_record(a)
        for word in MARKS:
            self.assertNotIn(word, text)
        self.assertIn("Osric, the tanner was talking with the traveller and, in passing, made a remark on your trade or calling.", text)
        self.assertIn("ONE short sentence", text)
        self.assertIn("WEATHER.", text)
        self.assertIn(exchange.OFF_THE_ROAD, text)
        self.assertEqual(exchange.reaction_record(a), "Now say your one line to Osric.")

    def test_only_a_companion_reacts_and_only_to_one_of_the_six_sorts(self):
        for bad in (react(speaker=dict(TANNER), addressee=dict(ELSPETH)), react(sort=6), react(sort=-1), react(sort="1"),
                    react(sort=True), react(step=1), react(speaker=dict(ELSPETH, name="Brynn Ashby"))):
            with self.assertRaises(exchange.Refused, msg=bad):
                exchange.parse_reaction(bad)
        self.assertFalse(exchange.is_reaction({"exchange": {"id": "x1"}}))
        self.assertTrue(exchange.is_reaction(react(0)))                       # sort 0 is a sort, not "absent"

    def test_one_line(self):
        self.assertEqual(exchange.one_line("  A fair day for it,   Osric. "), "A fair day for it, Osric.")
        long = "It is good to see the vats so full, Osric. " + "And another thing about hides that goes on and on. " * 6
        cut = exchange.one_line(long)
        self.assertLessEqual(len(cut), 160)
        self.assertTrue(cut.endswith("."))
        self.assertTrue(cut.startswith("It is good to see the vats so full, Osric."))
        self.assertLessEqual(len(exchange.one_line("x" * 400)), 161)
        self.assertEqual(exchange.one_line(""), "")


class Handler(unittest.TestCase):
    def setUp(self):
        self.srv = ThreadingHTTPServer(("127.0.0.1", 0), brain.Handler)
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        self.sent, self.logs = [], []
        brain.end_background()
        brain.FOREGROUND["n"] = 0
        brain.CANCELLED_EXCHANGES.clear()

    def tearDown(self):
        self.srv.shutdown()
        self.srv.server_close()
        brain.end_background()
        brain.FOREGROUND["n"] = 0

    def post(self, path, body):
        r = urllib.request.build_opener().open(urllib.request.Request(
            f"http://127.0.0.1:{self.srv.server_port}{path}", data=json.dumps(body).encode(),
            headers={"Content-Type": "application/json"}), timeout=20)
        return json.load(r)

    def reaction(self, body, model_say="A fair day for it, Osric.", on=True):
        def fake(url, payload, timeout_s):
            self.sent.append(json.loads(payload))
            return {"message": {"content": json.dumps({"say": model_say})}, "prompt_eval_count": 1, "eval_count": 1}
        base = {"request_id": "t", "npc_id": ELS, "subject_id": "00000000-0000-0000-0000-000000000000", "priority": "Background",
                "persona_key": "elspeth", "budget_ms": 6000}
        base.update(body)

        class Raiser:
            def __getattr__(self, name):
                raise AssertionError(f"a reaction touched {name}")
        with mock.patch.object(brain, "_post_cancellable", fake), mock.patch.dict(brain.CFG, {"companion_reaction": on}), \
                mock.patch.object(brain, "log", lambda **k: self.logs.append(k)), \
                mock.patch.object(brain, "LEDGER", Raiser()), mock.patch.object(brain, "RUMOURS", Raiser()), \
                mock.patch.object(brain, "MADEUP", Raiser()), \
                mock.patch.object(brain.LORE, "lookup", side_effect=AssertionError("a reaction looked up lore")), \
                mock.patch.object(brain.SECRETS, "body", side_effect=AssertionError("a reaction read a secret")):
            return self.post("/v1/npc/exchange", base)

    def test_default_is_off_and_a_reaction_is_refused_then(self):
        self.assertIs(brain.DEFAULTS["companion_reaction"], False)
        out = self.reaction(react(), on=False)
        self.assertFalse(out["ok"])
        self.assertIn("switched off", out["error"])
        self.assertEqual(self.sent, [])                                       # the model was never asked

    def test_nothing_the_npc_said_reaches_her_model_call(self):
        # The shard sends none of this for a reaction - but even a request stuffed with the NPC's words in every field
        # the handler could read must leave them all out of what the model is sent.
        body = react(transcript=[{"who": "Osric", "text": SECRET}])
        body.update(utterance=SECRET, situation=SECRET, history=[{"speaker": "npc", "text": SECRET}], persona=SECRET,
                    rumours={"town": "Britain", "pt": 10, "mode": "news", "roster": []}, party={"note": SECRET},
                    named={"note": SECRET}, standing={"note": SECRET}, secrets=[{"id": SECRET}], words=SECRET)
        out = self.reaction(body)
        self.assertTrue(out["ok"], out)
        whole = json.dumps(self.sent[-1])                                     # the ENTIRE outgoing request
        for word in MARKS:
            self.assertNotIn(word, whole)
        clean = json.dumps(self.sent[0]) if len(self.sent) > 1 else None      # (control below)
        msgs = self.sent[-1]["messages"]
        self.assertEqual([m["role"] for m in msgs], ["system", "user"])       # no history, no extra turns
        self.assertEqual(msgs[1]["content"], "Now say your one line to Osric.")
        self.assertIn("made a remark on your trade or calling", msgs[0]["content"])
        self.assertIn("You are Elspeth Vale", msgs[0]["content"])             # her own public persona
        self.assertNotIn("Said aloud here just now", whole)                   # the ordinary exchange record is not used

    def test_control_the_marker_words_would_be_seen_in_an_ordinary_exchange(self):
        # The check above only means something if these words DO show up when a transcript is used.
        x = {"id": "x3", "step": 0, "speaker": dict(ELSPETH), "addressee": dict(TANNER), "trust_band": "warming",
             "transcript": [{"who": "Osric", "text": "They say a watchman was found behind the Blue Boar."}], "roster": []}
        self.assertTrue(self.reaction({"exchange": x})["ok"])
        whole = json.dumps(self.sent[-1])
        self.assertIn("watchman", whole)
        self.assertIn("Blue Boar", whole)

    def test_exactly_one_line_comes_back(self):
        out = self.reaction(react(), model_say="It is good to see the vats full, Osric. " + "More and more words follow here. " * 8)
        self.assertTrue(out["ok"])
        self.assertLessEqual(len(out["say"]), 160)
        self.assertNotIn("topics", out)                                       # a reaction offers no subjects to remember
        rec = next(x for x in self.logs if x.get("event") == "exchange")
        self.assertEqual((rec["outcome"], rec["react"], rec["step"]), ("ok", 1, 0))

    def test_her_line_is_checked_like_any_exchange_line(self):
        self.assertFalse(self.reaction(react(), model_say="Brynn Ashby would know more of that, Osric.")["ok"])   # a real player's name
        self.assertFalse(self.reaction(react(), model_say="")["ok"])
        self.assertFalse(self.reaction(react(sort=9))["ok"])

    def test_an_ordinary_exchange_step_is_untouched(self):
        x = {"id": "x2", "step": 0, "speaker": dict(ELSPETH), "addressee": dict(TANNER), "trust_band": "warming",
             "transcript": [{"who": "Osric", "text": "Good day to you. And you, Elspeth?"}], "roster": []}
        out = self.reaction({"exchange": x}, on=False)                        # no "react": the switch does not matter
        self.assertTrue(out["ok"], out)
        self.assertIn("Said aloud here just now", json.dumps(self.sent[-1]))


class Offered(unittest.TestCase):
    """The NPC's reply: the brain hands back a sort number only when it really put that sort in the NPC's prompt."""

    @classmethod
    def setUpClass(cls):
        cls.srv = ThreadingHTTPServer(("127.0.0.1", 0), brain.Handler)
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()

    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.led.reconcile(1, 0, 0)
        self.r = rumours.Rumours(self.led, os.path.join(HERE, "rumours.json"))
        self.led.ingest([{"event_id": "1-1", "epoch": 1, "seq": 1, "npc_id": rumours.WORLD_ID, "char_id": OTHER, "kind": "deed",
                          "facts": {"type": "kill_notable", "subject": "dragon", "count": 1, "who": "Brynn Ashby",
                                    "descriptor": "a tamer", "place": "Destard", "pt": 9 * H, "map": "Trammel", "x": 1492, "y": 1641},
                          "at_utc": "2026-11-11T16:00:00Z", "schema": memory.SCHEMA_VERSION}])
        self.prompts = []
        self.patches = [mock.patch.object(brain, "LEDGER", self.led), mock.patch.object(brain, "RUMOURS", self.r),
                        mock.patch.object(brain, "log", lambda **k: None)]
        for p in self.patches:
            p.start()

    def tearDown(self):
        for p in self.patches:
            p.stop()
        self.led.db.close()
        self.led.rdb.close()
        self.dir.cleanup()

    def ask(self, utterance="What news?", news=True, cfg=None, no_answer=False, say=None):
        def fake(persona, utt, *a, **k):
            text = " ".join(str(x) for x in list(a) + list(k.values()) if isinstance(x, str))
            self.prompts.append(text)
            tok = rumour_speech.SENTINEL_RE.search(text)
            return (say or (f"{tok.group(0) if tok else 'Someone'} slew a dragon near Destard, they say. "
                            "And you, Elspeth - good day to you.")), "none"
        body = {"request_id": f"r{len(self.prompts)}", "npc_id": NPC, "subject_id": CHAR, "persona_key": "townsfolk",
                "persona_vars": {"name": "Osric", "role": "tanner", "town": "Britain", "trade": "vendor"},
                "utterance": utterance, "budget_ms": 8000,
                "party": {"companions": [{"name": "Elspeth Vale", "title": "the field healer", "id": ELS}], "pets": [],
                          "nudge": True, **({"no_answer": True} if no_answer else {})}}
        if news:
            body["rumours"] = {"town": "Britain", "pt": 10 * H, "mode": "news", "exclude": [], "recent": [],
                               "roster": [{"id": OTHER, "name": "Brynn Ashby"}, {"id": CHAR, "name": "Aldren Thorn"}]}
        settings = {"companion_reaction": True, "acquaintance_topics": True}
        settings.update(cfg or {})
        req = urllib.request.Request(f"http://127.0.0.1:{self.srv.server_address[1]}/v1/npc/respond",
                                     json.dumps(body).encode(), {"Content-Type": "application/json"})
        with mock.patch.object(brain, "ollama_chat", fake), mock.patch.dict(brain.CFG, settings):
            return json.loads(urllib.request.urlopen(req, timeout=20).read())

    def test_a_gossip_reply_that_addressed_her_carries_the_sort_it_was_asked_for(self):
        for _ in range(12):
            out = self.ask()
            self.assertTrue(out["ok"], out)
            self.assertEqual(out["rumour"]["ids"], ["1-1"])
            sort = out["reaction"]["sort"]
            self.assertIn(f"Make it {party.ANGLES[sort]}.", self.prompts[-1])    # chosen before the model ran, and in ITS prompt
        self.assertEqual(set(out["reaction"]), {"sort"})                         # a number - nothing of the reply

    def test_no_sort_when_the_switch_is_off(self):
        self.assertNotIn("reaction", self.ask(cfg={"companion_reaction": False}))

    def test_no_sort_when_the_npc_was_not_given_one(self):
        self.assertNotIn("reaction", self.ask(cfg={"acquaintance_topics": False}))   # no sort in the prompt at all
        out = self.ask(no_answer=True)                                           # the shard said no answer can follow
        self.assertNotIn("reaction", out)
        self.assertNotIn("Make it ", self.prompts[-1])

    def test_when_she_cannot_answer_she_is_left_out_of_the_reply(self):
        # the owner's play test, 2026-11-11: remarks to or about Elspeth that nobody answered looked ignored, and came too often.
        quiet = "do not bring their company up yourself"
        for news in (True, False):
            self.ask("Good day.", news=news, no_answer=True, cfg={"party_quiet_when_no_answer": True})
            self.assertIn(quiet, self.prompts[-1])                               # even with the shard's nudge due
            self.assertNotIn("one short separate sentence", self.prompts[-1])
            self.assertIn("Elspeth", self.prompts[-1])                           # still listed, so a question about her works
        self.ask("Good day.", news=False, no_answer=True)                        # switch off: the play-test-2 rule, unchanged
        self.assertIn("one short separate sentence about", self.prompts[-1])
        self.assertNotIn(quiet, self.prompts[-1])
        self.ask("Good day.", news=False, cfg={"party_quiet_when_no_answer": True})   # she CAN answer: spoken to as before
        self.assertIn("one short separate sentence to or about", self.prompts[-1])

    def test_no_sort_on_a_reply_without_gossip(self):
        out = self.ask("Good day.", news=False, say="Good day to you. And you, Elspeth - good day.")
        self.assertTrue(out["ok"])
        self.assertNotIn("reaction", out)                                        # an ordinary reply starts a real exchange instead


if __name__ == "__main__":
    unittest.main()
