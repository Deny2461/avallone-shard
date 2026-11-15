"""Step 2e - Codex code R1-1/R1-2 through the real handler and the real selector. Run: python3 -m unittest test_heard_e2e"""
import json
import os
import random
import re
import tempfile
import threading
import unittest
import urllib.request
from http.server import ThreadingHTTPServer
from unittest import mock

import brain
import madeup
import memory
import rumour_speech
import rumours

HERE = os.path.dirname(os.path.abspath(__file__))
NPC = "44444444-4444-4444-8444-444444444444"
CHAR, OTHER = "22222222-2222-4222-8222-222222222222", "33333333-3333-4333-8333-333333333333"
H = 3600


def deed(seq, typ, char=CHAR, subject="", count=1, pt=9 * H):
    return {"event_id": f"1-{seq}", "epoch": 1, "seq": seq, "npc_id": rumours.WORLD_ID, "char_id": char, "kind": "deed",
            "facts": {"type": typ, "subject": subject, "count": count, "who": "Aldren Thorn", "descriptor": "a fisher",
                      "place": "Britain", "pt": pt, "map": "Trammel", "x": 1492, "y": 1641},
            "at_utc": "2026-11-11T16:00:00Z", "schema": memory.SCHEMA_VERSION}


class _HandlerBase(unittest.TestCase):
    """A real brain HTTP handler on a throwaway ledger with one eligible deed about CHAR (no tests of its own)."""
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
        self.led.ingest([deed(1, "fished_boots", count=40)])
        self.prompts, self.sentence = [], ""
        real_of = brain.nature.of
        self.patches = [mock.patch.object(brain, "LEDGER", self.led), mock.patch.object(brain, "RUMOURS", self.r),
                        mock.patch.object(brain, "MADEUP", None), mock.patch.object(brain, "log", lambda **k: None),
                        mock.patch.object(brain.nature, "of", lambda n: (lambda p: p and dict(p, gossip="plain"))(real_of(n)))]
        for p in self.patches:
            p.start()

    def tearDown(self):
        for p in self.patches:
            p.stop()
        self.led.db.close()
        self.led.rdb.close()
        self.dir.cleanup()


class DeliveryThroughTheHandler(_HandlerBase):
    def news(self, plain, say):
        def fake(persona, utt, *a, **k):
            self.prompts.append(" ".join(str(x) for x in list(a) + list(k.values()) if isinstance(x, str)))
            tok = rumour_speech.SENTINEL_RE.search(self.prompts[-1])
            want = re.search(r'word for word, with nothing in front of it but your action: "([^"]+)"', self.prompts[-1])
            self.sentence = want.group(1) if want else ""
            return say.replace("SAID", self.sentence).replace("WHO", tok.group(0) if tok else "Someone"), "none"
        body = {"request_id": f"r{len(self.prompts)}", "npc_id": NPC, "subject_id": CHAR, "persona_key": "townsfolk",
                "persona_vars": {"name": "Osric", "role": "tanner", "town": "Britain", "trade": "vendor"},
                "utterance": "What news?", "budget_ms": 8000,
                "rumours": {"town": "Britain", "pt": 10 * H, "mode": "news", "exclude": [], "recent": [],
                            "roster": [{"id": CHAR, "name": "Aldren Thorn"}, {"id": OTHER, "name": "Brynn Ashby"}]}}
        req = urllib.request.Request(f"http://127.0.0.1:{self.srv.server_address[1]}/v1/npc/respond",
                                     json.dumps(body).encode(), {"Content-Type": "application/json"})
        with mock.patch.object(brain, "ollama_chat", fake), \
                mock.patch.dict(brain.CFG, {"plain_telling": plain, "rumour_corrections": plain, "madeup_flow": False,
                                              "rumour_live_share": 1.0}):
            return json.loads(urllib.request.urlopen(req, timeout=20).read())

    def test_plain_telling_on_records_the_real_deed(self):
        out = self.news(True, "SAID A lot of leather.")
        d = out["rumour"].get("delivered") or []
        self.assertEqual([x["story"] for x in d], ["1-1"])
        self.assertTrue(d[0]["hk"].startswith("s:1-1@"))

    def test_plain_telling_off_records_nothing_for_a_real_deed_even_when_told(self):
        out = self.news(False, "They say WHO fished up forty old boots in one sitting.")
        self.assertIn("1-1", out["rumour"]["ids"])                 # the loose reading still keeps the thread and timer
        self.assertNotIn("delivered", out["rumour"])               # but no durable "heard" without the exact sentence

    def test_switching_plain_telling_while_heard_logging_is_on(self):
        a = self.news(True, "SAID Quite a haul.")
        b = self.news(False, "They say WHO fished up forty old boots in one sitting.")
        c = self.news(True, "SAID Quite a haul.")
        self.assertIn("delivered", a["rumour"])
        self.assertNotIn("delivered", b["rumour"])
        self.assertIn("delivered", c["rumour"])


class PastQuestionIsNoFollowUp(_HandlerBase):
    """Play test 2026-11-13: a question about the NPC's own past right after a story was taken as a follow-up."""

    def ask_follow(self, utterance):
        import backgrounds
        backgrounds.ensure_table(self.led)

        def fake(persona, utt, *a, **k):
            self.prompts.append(" ".join(str(x) for x in [str(persona)] + list(a) + list(k.values()) if isinstance(x, str)))
            return "Jhelom, if you must know.", "none"
        body = {"request_id": f"f{len(self.prompts)}", "npc_id": NPC, "subject_id": CHAR, "persona_key": "townsfolk",
                "persona_vars": {"name": "Pakuna", "role": "tavern keeper", "town": "Britain", "trade": "vendor"},
                "utterance": utterance, "budget_ms": 8000,
                "rumours": {"town": "Britain", "pt": 10 * H, "mode": "follow", "follow": "1-1", "exclude": [], "recent": [],
                            "roster": [{"id": CHAR, "name": "Aldren Thorn"}]}}
        req = urllib.request.Request(f"http://127.0.0.1:{self.srv.server_address[1]}/v1/npc/respond",
                                     json.dumps(body).encode(), {"Content-Type": "application/json"})
        with mock.patch.object(brain, "ollama_chat", fake), \
                mock.patch.dict(brain.CFG, {"rumour_corrections": True, "madeup_flow": False, "rumour_live_share": 1.0}):
            return json.loads(urllib.request.urlopen(req, timeout=20).read())

    def test_a_question_about_their_past_is_answered_not_treated_as_a_follow_up(self):
        out = self.ask_follow("where are you from?")
        self.assertNotIn("rumour", out)                                  # no gossip thread on this turn
        self.assertNotEqual(out["say"], "That is all I know of it.")
        self.assertTrue(any("Your own past" in p for p in self.prompts))  # its background is in the prompt

    def test_a_real_follow_up_is_still_one(self):
        out = self.ask_follow("tell me more")
        self.assertIn("rumour", out)

    def test_questions_about_the_story_keep_the_thread(self):
        # Codex code R6: these are about the STORY, not the NPC - the thread stays and no past goes in the prompt
        for line in ("What happened before that?", "Where did they live before that?", "Tell me where they grew up"):
            self.prompts.clear()
            out = self.ask_follow(line)
            self.assertIn("rumour", out, line)
            self.assertFalse(any("Your own past" in p for p in self.prompts), line)

    def test_other_questions_about_the_npc_end_the_thread(self):
        for line in ("Where were you born?", "What did you do before this?", "Tell me about yourself."):
            out = self.ask_follow(line)
            self.assertNotIn("rumour", out, line)


class HandoffThroughTheSelector(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.led.reconcile(1, 0, 0)
        self.r = rumours.Rumours(self.led, os.path.join(HERE, "rumours.json"))
        self.m = madeup.MadeUp(self.led, self.r)
        self.led.ingest([deed(1, "kill_notable", char=OTHER, subject="dragon", pt=40000)])
        self.pt = 50000

    def tearDown(self):
        self.led.db.close()
        self.led.rdb.close()
        self.dir.cleanup()

    def offers(self, plain):
        for seed in range(400):
            for n in range(6):
                npc = f"5{n}5{n}5{n}5{n}-0000-4000-8000-000000000001"
                off = rumour_speech.select(self.r, self.pt, "Britain", CHAR, rumour_speech.MODE_NEWS, rng=random.Random(seed),
                                           madeup=self.m, npc_id=npc, plain=plain)
                if off is not None and off is not rumour_speech.LORE_TURN:
                    yield off

    def test_a_made_up_story_offered_beside_a_real_deed_is_never_handed_on(self):
        # a pair is offered only without plain telling (PAIR_SHARE of "what news") - as in Codex's probe
        pair = next((o for o in self.offers(False) if any(i.startswith("mu-") for i in o.ids) and "1-1" in o.ids), None)
        self.assertIsNotNone(pair, "the selector never offered a pair")
        mu = next(i for i in pair.ids if i.startswith("mu-"))
        said = pair.canon[mu]                                        # the reply tells ONLY the made-up story
        told = rumour_speech.used(said, pair, strict=True)
        self.assertEqual(told, [mu])
        wired = rumour_speech.wire_reply(pair, told, delivered=told)
        self.assertEqual(wired["ids"], [mu])                          # what the shard would see...
        self.assertNotIn("handoff", wired)                            # ...and the brain's verdict: not eligible

    def test_a_made_up_story_offered_alone_and_told_is_handed_on(self):
        alone = next((o for o in self.offers(True) if len(o.ids) == 1 and o.ids[0].startswith("mu-")), None)
        self.assertIsNotNone(alone)
        mu = alone.ids[0]
        told = rumour_speech.used(alone.canon[mu], alone, strict=True)
        wired = rumour_speech.wire_reply(alone, told, delivered=told)
        self.assertEqual(wired["handoff"], {"story": mu, "source": "made_up"})


class HandoffLiveness(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.led.reconcile(1, 0, 0)
        self.r = rumours.Rumours(self.led, os.path.join(HERE, "rumours.json"))
        self.m = madeup.MadeUp(self.led, self.r)
        self.pt = 50000
        self.story = next(s for s, _a, _h in self.m.town_rows(self.pt, "Britain") if s["origin"] == "Britain")

    def tearDown(self):
        self.led.db.close()
        self.led.rdb.close()
        self.dir.cleanup()

    def test_expiry_between_steps_is_seen_at_the_current_clock(self):
        last = self.story["expires_pt"] - 1
        self.assertIsNotNone(self.m.handoff_story(self.story["id"], last, "Britain"))
        self.assertIsNone(self.m.handoff_story(self.story["id"], last + 1, "Britain"))

    def test_wrong_town_and_before_arrival_are_refused(self):
        sp = self.m._arrivals(self.story)
        far, (arrive, _h) = max(sp.items(), key=lambda kv: kv[1][0])
        self.assertGreater(arrive, self.story["born_pt"])
        self.assertIsNone(self.m.handoff_story(self.story["id"], arrive - 1, far))     # not there yet
        self.assertIsNotNone(self.m.handoff_story(self.story["id"], arrive, far))      # arrived
        self.assertIsNone(self.m.handoff_story(self.story["id"], arrive, "Atlantis"))  # not a town


if __name__ == "__main__":
    unittest.main()


class SilenceThroughTheHandler(_HandlerBase):
    """Codex code R7 finding 1: "Don't talk about me" was routed as a self-gossip request, and plain telling then put the
    speaker's deed into a reply that said "I will keep quiet." The shard now sends such a line as an ordinary turn (no
    rumour block, RumourSpeech.IsStopTalk); here the handler side, with an eligible self deed and plain telling ON."""

    def send(self, utterance, rumour_block, say):
        def fake(persona, utt, *a, **k):
            self.prompts.append(" ".join(str(x) for x in list(a) + list(k.values()) if isinstance(x, str)))
            want = re.search(r'word for word, with nothing in front of it but your action: "([^"]+)"', self.prompts[-1])
            self.sentence = want.group(1) if want else ""
            return say.replace("SAID", self.sentence), "none"
        body = {"request_id": f"s{len(self.prompts)}", "npc_id": NPC, "subject_id": CHAR, "persona_key": "townsfolk",
                "persona_vars": {"name": "Osric", "role": "tanner", "town": "Britain", "trade": "vendor"},
                "utterance": utterance, "budget_ms": 8000}
        if rumour_block is not ...:
            body["rumours"] = rumour_block
        req = urllib.request.Request(f"http://127.0.0.1:{self.srv.server_address[1]}/v1/npc/respond",
                                     json.dumps(body).encode(), {"Content-Type": "application/json"})
        with mock.patch.object(brain, "ollama_chat", fake), \
                mock.patch.dict(brain.CFG, {"plain_telling": True, "rumour_corrections": True, "madeup_flow": False,
                                              "rumour_live_share": 1.0}):
            return json.loads(urllib.request.urlopen(req, timeout=20).read())

    def about_me(self):
        return {"town": "Britain", "pt": 10 * H, "mode": "about", "about": CHAR, "exclude": [], "recent": [],
                "roster": [{"id": CHAR, "name": "Aldren Thorn"}, {"id": OTHER, "name": "Brynn Ashby"}]}

    def test_a_real_question_about_me_tells_my_own_story(self):
        out = self.send("Any rumours about me?", self.about_me(), "SAID Quite the haul.")   # positive control
        self.assertEqual([x["story"] for x in out["rumour"].get("delivered") or []], ["1-1"])
        self.assertIn("old boots", out["say"])

    def test_a_request_for_silence_tells_nothing(self):
        for line in ("Don't talk about me.", "Stop talking about me.", "Please say nothing about me.",
                     "Don't ever talk about me.", "Stop all this gossip about me.", "Don't keep talking about me.",
                     "Could you stop talking about me?", "Would you please stop spreading rumours about me"):
            for block in (..., None):                       # the shard sends no block (field absent or null)
                out = self.send(line, block, "I will keep quiet.")
                self.assertEqual(out["say"], "I will keep quiet.", line)
                self.assertFalse(out.get("rumour"), line)
                self.assertNotIn("old boots", " ".join(self.prompts[-1:]), line)   # the deed is not even in the prompt

    def test_asking_again_after_hearing_it_all_tells_nothing_new(self):
        # Repeat asks (the owner, chat, 2026-11-13): the shard's recent window carries what was just delivered; with the only
        # story about the speaker in it, the second answer is "that is all I know" - no story, no forced sentence.
        first = self.send("what rumors have you heard about me?", self.about_me(), "SAID Quite the haul.")
        hk = first["rumour"]["delivered"][0]["hk"]
        again = dict(self.about_me(), recent=[hk])
        out = self.send("what rumors have you heard about me?", again, "That is all I have heard of you.")
        self.assertEqual(out["say"], "That is all I have heard of you.")
        self.assertFalse((out.get("rumour") or {}).get("delivered"))
        self.assertFalse((out.get("rumour") or {}).get("ids"))
        self.assertIn("already told them everything", self.prompts[-1])
        self.assertNotIn("old boots", self.prompts[-1])

    def test_about_me_never_falls_back_to_someone_elses_story(self):
        # Codex code R9-1: the asker stays the target even when their own stories are exhausted - another character's
        # eligible deed is never offered instead.
        self.led.ingest([deed(2, "kill_notable", char=OTHER, subject="dragon")])
        first = self.send("Could you tell me any rumours about me", self.about_me(), "SAID Quite the haul.")
        self.assertEqual([x["story"] for x in first["rumour"]["delivered"]], ["1-1"])
        hk = first["rumour"]["delivered"][0]["hk"]
        out = self.send("Could you tell me any rumours about me", dict(self.about_me(), recent=[hk]),
                        "That is all I have heard of you.")
        self.assertEqual(out["say"], "That is all I have heard of you.")
        self.assertFalse((out.get("rumour") or {}).get("ids"))
        self.assertFalse((out.get("rumour") or {}).get("delivered"))
        self.assertNotIn("dragon", self.prompts[-1])
        self.assertIn("already told them everything", self.prompts[-1])


class StoryChoiceThroughTheHandler(_HandlerBase):
    """Step 2e (Codex code R7-R11; the owner, chat, 2026-11-13: - the NPC decides). On a turn with a story on offer
    the model must choose "tell" or "keep"; on anything but "tell" the brain puts NO story into the reply - whatever route
    admitted the gossip (news, a named player, the speaker) and whatever the player's wording."""

    def ask(self, mode, choice, say, utterance="What news?", **rask):
        seen = {}
        def fake(persona, utt, *a, **k):
            seen["story_kw"] = k.get("story")
            if k.get("story") and isinstance(k.get("stats"), dict) and choice is not None:
                k["stats"]["story"] = choice
            self.prompts.append(" ".join(str(x) for x in list(a) + list(k.values()) if isinstance(x, str)))
            want = re.search(r'word for word, with nothing in front of it but your action: "([^"]+)"', self.prompts[-1])
            return say.replace("SAID", want.group(1) if want else ""), "none"
        body = {"request_id": f"c{len(self.prompts)}", "npc_id": NPC, "subject_id": CHAR, "persona_key": "townsfolk",
                "persona_vars": {"name": "Osric", "role": "tanner", "town": "Britain", "trade": "vendor"},
                "utterance": utterance, "budget_ms": 8000,
                "rumours": {"town": "Britain", "pt": 10 * H, "mode": mode, "exclude": [], "recent": [],
                            "roster": [{"id": CHAR, "name": "Aldren Thorn"}, {"id": OTHER, "name": "Brynn Ashby"}], **rask}}
        req = urllib.request.Request(f"http://127.0.0.1:{self.srv.server_address[1]}/v1/npc/respond",
                                     json.dumps(body).encode(), {"Content-Type": "application/json"})
        with mock.patch.object(brain, "ollama_chat", fake), \
                mock.patch.dict(brain.CFG, {"plain_telling": True, "rumour_corrections": True, "madeup_flow": False,
                                              "rumour_live_share": 1.0}):
            out = json.loads(urllib.request.urlopen(req, timeout=20).read())
        out["_story_kw"] = seen.get("story_kw")
        return out

    def test_keep_is_never_overridden_on_any_route(self):
        self.led.ingest([deed(2, "kill_notable", char=OTHER, subject="dragon")])
        routes = (("news", {}), ("about", {"about": CHAR}), ("about", {"about": OTHER}))
        for mode, extra in routes:
            for choice in ("keep", None, "", "TELL", "yes"):        # only an exact "tell" counts
                out = self.ask(mode, choice, "I will keep quiet.", utterance="Please do not discuss that.", **extra)
                self.assertTrue(out["_story_kw"], (mode, extra))        # the choice was asked for
                self.assertEqual(out["say"], "I will keep quiet.", (mode, extra, choice))
                self.assertFalse((out.get("rumour") or {}).get("ids"), (mode, extra, choice))
                self.assertFalse((out.get("rumour") or {}).get("delivered"), (mode, extra, choice))
                self.assertNotIn("old boots", out["say"])
                self.assertNotIn("dragon", out["say"])

    def test_tell_keeps_plain_telling(self):
        # the NPC chose to tell but wandered off the sentence: the brain says the sentence itself, as before
        out = self.ask("news", "tell", "Oh, a strange business with some boots.")
        self.assertTrue(out["say"].split("] ")[-1].startswith(("They say", "I hear", "Word is", "Folk say")))
        self.assertIn("old boots", out["say"])
        self.assertEqual([x["story"] for x in out["rumour"]["delivered"]], ["1-1"])

    def test_keep_but_said_anyway_is_recorded_honestly(self):
        # the NPC chose keep yet began with the exact sentence: nothing forced, and what it really said is recorded
        out = self.ask("news", "keep", "SAID Odd, that.")
        self.assertIn("old boots", out["say"])
        self.assertEqual([x["story"] for x in out["rumour"].get("delivered") or []], ["1-1"])

    def test_the_choice_is_only_asked_when_a_story_is_on_offer(self):
        out = self.ask("about", "keep", "Never heard of them.", about="")   # nothing to tell: no field, no line
        self.assertFalse(out["_story_kw"])
        self.assertNotIn('JSON field "story"', self.prompts[-1])
        self.ask("news", "tell", "Hm.")
        self.assertIn('JSON field "story"', self.prompts[-1])

    def test_switch_off_is_the_behaviour_before(self):
        with mock.patch.dict(brain.CFG, {"rumour_story_choice": False}):
            out = self.ask("news", None, "I will keep quiet.")
        self.assertFalse(out["_story_kw"])
        self.assertIn("old boots", out["say"])                          # forced, exactly as before the change

    def test_schema_puts_the_decisions_first(self):
        self.assertEqual(brain.reply_schema([], False), brain.REPLY_SCHEMA)
        sc = brain.reply_schema([], True)
        self.assertEqual(list(sc["properties"]), ["story", "say"])
        self.assertEqual(sc["required"], ["story", "say"])
        self.assertEqual(sc["properties"]["story"]["enum"], ["tell", "keep"])
        sc = brain.reply_schema(["none", "follow"], True)
        self.assertEqual(list(sc["properties"]), ["story", "action", "say"])
        self.assertEqual(brain.reply_schema(["none", "follow"])["required"], ["action", "say"])   # unchanged without it



class MoodRetryKeepsItsChoice(_HandlerBase):
    """Codex code R12-1: when a mood excuse is cut, the tell/keep choice must be the one of the reply the kept words came
    from - the retry's when they come from the retry (missing = keep), the first reply's when they come from it, and none
    at all for the code-written mood fallback. Codex's seven probe cases plus the no-retry cut; real dials and mood."""

    def turn(self, name, first, second, second_say, error=False, first_say="I am too tired to talk."):
        import backgrounds
        backgrounds.ensure_table(self.led)
        calls, logs = [], []
        def fake(persona, utt, *a, **k):
            i = len(calls)
            calls.append(k.get("story"))
            if i and error:
                raise ValueError("synthetic retry failure")
            choice = first if i == 0 else second
            if choice is not None:
                k["stats"]["story"] = choice
            text = " ".join(str(x) for x in list(a) + list(k.values()) if isinstance(x, str))
            want = re.search(r'word for word, with nothing in front of it but your action: "([^"]+)"', text)
            return (first_say if i == 0 else second_say).replace("SAID", want.group(1) if want else ""), "none"
        body = {"request_id": name, "npc_id": NPC, "subject_id": CHAR, "persona_key": "townsfolk",
                "persona_vars": {"name": "Osric", "role": "tanner", "town": "Britain", "trade": "vendor"},
                "utterance": "Could you avoid talking about me?", "budget_ms": 8000,
                "standing": {"epoch": 1, "pt": 36000, "mpt": 0, "hour": 12, "town": "Britain", "karma": 15000, "fame": 5000},
                "rumours": {"town": "Britain", "pt": 36000, "mode": "about", "about": CHAR, "exclude": [], "recent": [],
                            "roster": [{"id": CHAR, "name": "Aldren Thorn"}]}}
        req = urllib.request.Request(f"http://127.0.0.1:{self.srv.server_address[1]}/v1/npc/respond",
                                     json.dumps(body).encode(), {"Content-Type": "application/json"})
        with mock.patch.object(brain, "ollama_chat", fake), mock.patch.object(brain, "log", lambda **k: logs.append(k)), \
                mock.patch.dict(brain.CFG, {"plain_telling": True, "rumour_corrections": True, "madeup_flow": False,
                                              "rumour_live_share": 1.0, "rumour_story_choice": True}):
            out = json.loads(urllib.request.urlopen(req, timeout=20).read())
        resp = next(x for x in reversed(logs) if x.get("event") == "respond" and x.get("outcome") == "ok")
        return out, resp, calls

    def check(self, name, first, second, second_say, error=False, told=False, choice=None, first_say="I am too tired to talk.",
              calls_n=2):
        out, resp, calls = self.turn(name, first, second, second_say, error, first_say)
        self.assertEqual(len(calls), calls_n, name)
        self.assertTrue(all(calls), name)                                  # the choice was asked of every call
        self.assertIn("excuse", resp.get("mood") or {}, name)              # the mood repair really ran
        delivered = (out.get("rumour") or {}).get("delivered") or []
        self.assertEqual(bool(delivered), told, (name, out.get("say"), resp.get("story_choice")))
        self.assertEqual(resp.get("story_choice"), choice, name)
        if not told:
            self.assertNotIn("old boots", out["say"], name)
        return out

    def test_codex_r12_cases(self):
        self.check("tell_then_keep_cut", "tell", "keep", "I am too tired to talk. I will keep quiet.", choice="keep")
        self.check("tell_then_missing_cut", "tell", None, "I am too tired to talk. I will keep quiet.", choice="")
        self.check("tell_then_keep_fallback", "tell", "keep", "I am too tired to talk.", choice="")
        self.check("tell_then_retry_error", "tell", None, "", error=True, choice="")
        self.check("tell_then_keep_clean", "tell", "keep", "I will keep quiet.", choice="keep")
        self.check("keep_then_keep_cut", "keep", "keep", "I am too tired to talk. I will keep quiet.", choice="keep")
        # the reverse: the kept words are the retry's and it chose tell - so the story may be told (the pairing is right)
        self.check("keep_then_tell_cut", "keep", "tell", "I am too tired to talk. I could tell a tale.", told=True,
                   choice="tell")

    def test_words_cut_from_the_first_reply_keep_the_first_choice(self):
        # the retry failed; what is kept is cut from the FIRST reply, so the first reply's choice stands
        self.check("keep_first_cut", "keep", None, "", error=True, choice="keep",
                   first_say="I am too tired to talk. I will keep quiet.")
        self.check("tell_first_cut", "tell", None, "", error=True, choice="tell", told=True,
                   first_say="I am too tired to talk. Hm.")
