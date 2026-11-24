"""Errands build 1, stage 5b: the brain's endpoints and turn handler with errands - the readiness probe, the model-free
proof, the ask riding on an ordinary reply (the model is told nothing), the fixed instruction lines (v6), the
remind step (v6), and the
operator's one pass. Real handler over HTTP, real ledger and builder, a stand-in for the model (Ollama is never called).
Run: python3 test_errands_brain.py"""
import hashlib
import json
import os
import sys
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer
from unittest import mock

_d = tempfile.TemporaryDirectory()
with open(os.path.join(_d.name, "c.json"), "w") as f:
    json.dump({"memory_db": os.path.join(_d.name, "x.db"), "secrets_dir": _d.name, "quiet_windows": [],
               "reflection_mode": "off", "lore_dir": _d.name}, f)
os.environ["AVALLONE_BRAIN_CONFIG"] = os.path.join(_d.name, "c.json")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import backgrounds  # noqa: E402
import brain  # noqa: E402
import errands  # noqa: E402
import errands_people as ep  # noqa: E402
import errands_text as et  # noqa: E402
import memory  # noqa: E402
import people_store as store  # noqa: E402
import townroster  # noqa: E402
from test_people_store import folk, roster_doc  # noqa: E402

CHAR = "22222222-2222-4222-8222-222222222222"
CONFIRM = "draw the cousin and contact tables once"


class Base(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.dir = tempfile.TemporaryDirectory()
        cls.led = memory.Ledger(os.path.join(cls.dir.name, "m.db"))
        assert cls.led.reconcile(8, 7, 0, None)[0] in ("ok", "fresh")
        backgrounds.ensure_table(cls.led)
        store.ensure_tables(cls.led)
        doc = roster_doc([{"map": "Felucca", "town": "Britain", "people": folk(80)},
                          {"map": "Felucca", "town": "Vesper", "people": folk(80, start=200)}])
        cls.roster = townroster.Roster(cls.led)
        cls.roster.accept(doc)
        cls.stamp, cls.held = doc["hash"], cls.roster.held()
        for town in ("Britain", "Vesper"):
            assert store.build_town(cls.led, cls.held, "Felucca", town, epoch=8) == "built"
        cls.srv = ThreadingHTTPServer(("127.0.0.1", 0), brain.Handler)
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()
        cls.logs = []
        cls.patches = [mock.patch.object(brain, "LEDGER", cls.led), mock.patch.object(brain, "ROSTER", cls.roster),
                       mock.patch.object(brain, "RUMOURS", None),
                       mock.patch.object(brain, "log", lambda **k: cls.logs.append(k)),
                       mock.patch.dict(brain.CFG, {"people": True, "always_know": "A", "errand_pass": True})]
        for p in cls.patches:
            p.start()
        code, out = cls.post("/v1/errands/pass", {"confirm": CONFIRM, "seed": "5"})
        assert code == 200, out
        cls.pass_out = out
        brain.CFG.update(errand_cousin_pin=out["cousin"]["fingerprint"], errand_contact_pin=out["contact"]["fingerprint"],
                         errand_tables=True)
        brain.errand_tables(refresh=True)
        cls.seq = 0

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()
        cls.srv.server_close()
        for p in cls.patches:
            p.stop()
        brain._errand_tables["state"] = None
        cls.dir.cleanup()

    @classmethod
    def post(cls, path, body):
        req = urllib.request.Request(f"http://127.0.0.1:{cls.srv.server_address[1]}{path}", json.dumps(body).encode(),
                                     {"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=20) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read())

    @classmethod
    def visits(cls, npc, days):
        evs = []
        for d in days:
            cls.seq += 1
            evs.append({"event_id": f"8-{cls.seq}", "epoch": 8, "seq": cls.seq, "kind": "visited", "schema": 1,
                        "npc_id": npc, "char_id": CHAR, "at_utc": "2026-11-21T10:00:00Z", "facts": {"day": d, "mpt": 0}})
        assert cls.led.ingest(evs)[1] == []

    def sender_with(self, kind, route, skip=0):
        """The (skip+1)th person who could ask this - tests that record visits each take their own sender."""
        w = errands.World(self.led, self.held, brain.errand_tables(), et.load())
        for pid in self.held.by_id:
            hit = next((c for c in errands.candidates(w, pid) if c[0] == kind and c[1] == route), None)
            if hit and skip == 0:
                return pid, hit
            skip -= 1 if hit else 0
        self.fail("nobody")

    def turn(self, npc, errand=None, utterance="good day to you", rid="r1"):
        at = self.held.where(npc)
        calls = []

        def fake(persona, *a, **k):
            calls.append((persona, a, k))
            return "Good day to you too.", "none"
        body = {"request_id": rid, "npc_id": npc, "subject_id": CHAR, "persona_key": "townsfolk",
                "persona_vars": {"name": at[2]["name"], "role": at[2]["role"], "town": at[1], "trade": "vendor"},
                "utterance": utterance, "budget_ms": 8000, "roster_stamp": self.stamp,
                "standing": {"karma": 0, "fame": 0, "hour": 12, "town": at[1], "pt": 0, "mpt": 0, "epoch": 8}}
        if errand is not None:
            body["errand"] = errand
        with mock.patch.object(brain, "ollama_chat", fake):
            code, out = self.post("/v1/npc/respond", body)
        self.assertEqual(code, 200)
        # everything the model was sent, as text: the system prompt and every other argument except the time budget
        sent = [json.dumps([c[0]] + [x for x in c[1] if not isinstance(x, (int, float))]
                           + [[k, v] for k, v in sorted(c[2].items()) if k not in ("stats", "max_tokens")], default=str)
                for c in calls]
        return out, "\n".join(sent)

    def ask(self, **more):
        return dict({"step": "ask", "gen": 3, "request": "q1", "ticket": "t-0001", "char": CHAR, "speech_rev": 7,
                     "stamp": self.stamp, "intro_offered": False, "intro_blocked": []}, **more)


class Ready(Base):
    def test_the_probe_echoes_and_reports_cached_facts_without_the_model(self):
        with mock.patch.object(brain, "ollama_chat", side_effect=AssertionError("model")), \
                mock.patch.object(brain, "ollama_reachable", side_effect=AssertionError("ollama")), \
                mock.patch.object(ep, "state", side_effect=AssertionError("audit again")):
            code, out = self.post("/v1/errands/ready", {"probe": 41, "gen": 3, "stamp": self.stamp})
        self.assertEqual((code, out["ok"], out["probe"], out["gen"], out["errands"], out["stamp_ok"]),
                         (200, True, 41, 3, 1, True))
        self.assertEqual(out["boot"], errands.BOOT_ID)
        self.assertEqual(out["data_gen"], errands.data_generation(brain.errand_tables(), et.load()))
        self.assertFalse(self.post("/v1/errands/ready", {"probe": 42, "gen": 3, "stamp": "none"})[1]["stamp_ok"])
        self.assertFalse(self.post("/v1/errands/ready", {})[1]["stamp_ok"])

    def test_health_reports_the_two_tables(self):
        with mock.patch.object(brain, "ollama_reachable", return_value=None):
            with urllib.request.urlopen(f"http://127.0.0.1:{self.srv.server_address[1]}/health", timeout=10) as r:
                h = json.loads(r.read())
        self.assertEqual(h["errand_tables"], {"switch": True, "cousin": {"state": "available", "reason": None},
                                              "contact": {"state": "available", "reason": None}})

    def test_unknown_errand_path(self):
        self.assertEqual(self.post("/v1/errands/nothing", {})[0], 404)


class ThePass(Base):
    def test_it_ran_once_and_cannot_run_again_or_without_leave(self):
        self.assertGreater(self.pass_out["cousin"]["count"], 0)
        self.assertGreater(self.pass_out["contact"]["count"], 0)
        code, out = self.post("/v1/errands/pass", {"confirm": CONFIRM, "seed": "9"})
        self.assertEqual((code, out["ok"]), (500, False))                       # already run: never drawn again
        self.assertEqual(self.post("/v1/errands/pass", {"confirm": "yes", "seed": "9"})[0], 403)
        with mock.patch.dict(brain.CFG, {"errand_pass": False}):
            self.assertEqual(self.post("/v1/errands/pass", {"confirm": CONFIRM, "seed": "9"})[0], 403)
        self.assertEqual(ep.fingerprint(ep.cousin_rows(self.led.db)), self.pass_out["cousin"]["fingerprint"])


class Ask(Base):
    def test_no_ask_before_two_visit_days_and_the_reply_is_untouched(self):
        sender, _ = self.sender_with("word", "kin", skip=3)
        out, _ = self.turn(sender, self.ask())
        self.assertTrue(out["say"].endswith("Good day to you too."))
        self.assertNotIn("errand", out)
        self.visits(sender, ["2026-11-19"])
        self.assertNotIn("errand", self.turn(sender, self.ask())[0])

    def test_after_two_days_a_proof_rides_on_an_ordinary_reply_and_the_model_knows_nothing(self):
        sender, _ = self.sender_with("ask_after", "cousin")
        self.visits(sender, ["2026-11-10", "2026-11-11"])
        plain, plain_prompt = self.turn(sender)
        out, prompt = self.turn(sender, self.ask())
        self.assertEqual(out["say"].split("] ")[-1], plain["say"].split("] ")[-1])   # a small action may lead the line
        self.assertEqual(prompt, plain_prompt)                                  # the model was sent exactly the same
        e = out["errand"]
        self.assertEqual(e["echo"], {"gen": 3, "request": "q1", "ticket": "t-0001", "row": None, "version": None,
                                     "char": CHAR, "speech_rev": 7, "stamp": self.stamp})
        self.assertEqual((e["step"], e["sender"]), ("ask", sender))
        self.assertTrue(e["ask_line"] and e["yes_line"] and e["no_line"] and len(e["digest"]) == 64)
        self.assertNotIn(e["ask_line"], prompt)
        self.assertEqual(e["data_gen"], errands.data_generation(brain.errand_tables(), et.load()))
        again, _ = self.turn(sender, self.ask())
        self.assertEqual(again["errand"]["ask_line"], e["ask_line"])            # the ticket fixes the choice
        other, _ = self.turn(sender, self.ask(ticket="t-0002"))
        self.assertIn("errand", other)

    def test_a_stale_stamp_or_no_ticket_asks_nothing(self):
        sender, _ = self.sender_with("word", "kin", skip=1)
        self.visits(sender, ["2026-11-12", "2026-11-13"])
        self.assertIn("errand", self.turn(sender, self.ask())[0])
        self.assertNotIn("errand", self.turn(sender, self.ask(stamp="0" * 64))[0])
        self.assertNotIn("errand", self.turn(sender, self.ask(ticket=""))[0])
        self.assertNotIn("errand", self.turn(sender, {"step": "nonsense"})[0])

    def test_a_line_the_house_rule_stops_gets_the_fixed_answer_and_no_errand(self):
        sender, _ = self.sender_with("word", "kin", skip=2)
        self.visits(sender, ["2026-11-14", "2026-11-15"])
        with mock.patch.object(brain.decency, "check", side_effect=lambda t: "slur" if t == "bad words" else None), \
                mock.patch.dict(brain.CFG, {"decency_input_rebuke": True}):
            out, _ = self.turn(sender, self.ask(), utterance="bad words")
        self.assertEqual(out.get("kind"), "decency")
        self.assertNotIn("errand", out)

    def test_an_introduction_is_offered_only_when_the_shard_says_it_was_never_offered(self):
        sender, c = self.sender_with("introduce", "contact")
        self.visits(sender, ["2026-11-16", "2026-11-17"])
        kinds = lambda **m: {(self.turn(sender, self.ask(ticket=f"t-{i}", **m))[0].get("errand") or {}).get("kind")
                             for i in range(30)}
        self.assertIn("introduce", kinds())
        self.assertNotIn("introduce", kinds(intro_offered=True))
        self.assertNotIn("introduce", kinds(intro_blocked=[c[2]]))
        no_flag = self.ask()
        del no_flag["intro_offered"]
        self.assertNotEqual((self.turn(sender, no_flag)[0].get("errand") or {}).get("kind"), "introduce")


class ProofAndContext(Base):
    def row(self, sender, c, step, **more):
        return dict({"step": step, "gen": 3, "request": "q9", "row": 12, "version": 2, "char": CHAR, "speech_rev": 9,
                     "stamp": self.stamp, "kind": c[0], "route": c[1], "sender": sender, "receiver": c[2],
                     "about": c[3], "line_key": "m01" if c[0] == "word" else None, "seed": "12"}, **more)

    def test_a_round_trip_through_the_endpoint_without_the_model(self):
        sender, c = self.sender_with("ask_after", "cousin")
        with mock.patch.object(brain, "ollama_chat", side_effect=AssertionError("model")):
            code, acc = self.post("/v1/errands/proof", self.row(sender, c, "accept"))
            _, dlv = self.post("/v1/errands/proof", self.row(sender, c, "deliver"))
            _, ret = self.post("/v1/errands/proof", self.row(sender, c, "return", answer=dlv["proof"]["answer"]))
        self.assertEqual(code, 200)
        self.assertEqual(acc["echo"]["row"], 12)
        self.assertEqual(len({acc["proof"]["digest"], dlv["proof"]["digest"], ret["proof"]["digest"]}), 1)
        self.assertFalse(ret["proof"]["answer_changed"])
        self.assertEqual(acc["data_gen"], acc["proof"]["data_gen"])

    def test_a_line_the_house_rule_stops_gets_no_errand_step(self):
        sender, c = self.sender_with("word", "kin")
        good = self.row(sender, c, "deliver", utterance="here is your letter")
        self.assertIsNotNone(self.post("/v1/errands/proof", good)[1]["proof"])
        with mock.patch.object(brain.decency, "check", side_effect=lambda t: "slur" if t == "bad words" else None), \
                mock.patch.dict(brain.CFG, {"decency_input_rebuke": True}):
            code, out = self.post("/v1/errands/proof", dict(good, utterance="bad words"))
            self.assertEqual((code, out["ok"], out["proof"]), (200, True, None))
            self.assertIsNotNone(self.post("/v1/errands/proof", good)[1]["proof"])
        self.assertTrue(any(k.get("outcome") == "yields" for k in self.logs))
        self.assertFalse(any("bad words" in json.dumps(k) for k in self.logs))

    def test_no_proof_is_an_answer_too(self):
        sender, c = self.sender_with("word", "kin")
        for bad in (self.row(sender, c, "ask"), self.row(sender, c, "deliver", stamp="0" * 64),
                    self.row(sender, c, "deliver", receiver=sender), self.row(sender, c, "return")):
            code, out = self.post("/v1/errands/proof", bad)
            self.assertEqual((code, out["ok"], out["proof"]), (200, True, None))
            self.assertEqual(out["echo"]["request"], "q9")
            self.assertTrue(out["data_gen"])

    def test_the_pointer_line_holds_no_detail_and_the_old_context_lines_are_gone(self):
        """Design v6 12.3: one fixed instruction per role, no proof call, no name, trade or town of the errand."""
        sender, c = self.sender_with("word", "kin")
        name = self.held.where(c[2])[2]["name"]
        _, plain = self.turn(sender)
        with mock.patch.object(errands, "prove", side_effect=AssertionError("no proof call on an ordinary turn")):
            _, sp = self.turn(sender, {"step": "pointer", "role": "sender"})
            _, rp = self.turn(sender, {"step": "pointer", "role": "receiver"})
            _, none = self.turn(sender, {"step": "pointer", "role": "bystander"})
            _, missing = self.turn(sender, {"step": "pointer"})
            # a v5 shard's request: no line, and still no proof call
            _, old = self.turn(sender, self.row(sender, c, "context", role="sender", digest="0" * 64))
        self.assertIn("has agreed to do you a small favour", sp)
        self.assertIn("ask you plainly what it was you wanted", sp)
        self.assertIn("carrying a word from you back to one of your kin", rp)
        self.assertIn("ask you plainly what they should tell them", rp)
        self.assertNotIn("small favour", rp)
        for prompt in (none, missing, old):
            self.assertEqual(prompt, plain)                                      # exactly an ordinary turn
        for prompt in (sp, rp):
            self.assertNotIn("You asked this traveller to carry word to", prompt)
            self.assertNotIn("brought you word from", prompt)
            # the instruction itself adds no name: the receiver is named in it nowhere
            self.assertEqual(prompt.count(name), plain.count(name))
        for line in brain.ERRAND_POINTER.values():
            self.assertIsNone(et.DIGIT.search(line))
            self.assertNotIn("{", line)

    def test_the_measurement_trace_says_whether_the_line_was_really_applied(self):
        """Codex Code R2 M2: 'asked for' is not 'applied' - the dev-only trace reports what happened."""
        sender, c = self.sender_with("word", "kin")
        ctx = {"step": "pointer", "role": "sender"}
        with mock.patch.dict(brain.CFG, {"measure_trace": True}):
            applied, aprompt = self.turn(sender, ctx)
            other, oprompt = self.turn(sender, {"step": "pointer", "role": "receiver"})
            refused, rprompt = self.turn(sender, dict(ctx, role="bystander"))
            closed, cprompt = self.turn(sender, {"step": "closed"})
            plain, pprompt = self.turn(sender)

        def situations(prompt):   # the "what is happening now" text of every model request of that turn
            got = [part[1] for call in prompt.split("\n") for part in json.loads(call)
                   if isinstance(part, list) and len(part) == 2 and part[0] == "situation"]
            self.assertTrue(got)
            return got
        # "applied" is checked against what the model was REALLY sent: the very line the trace fingerprints is a
        # paragraph of every model request of that turn - and of none where the trace says it was not applied
        for out, prompt in ((applied, aprompt), (other, oprompt), (closed, cprompt)):
            for text in situations(prompt):
                self.assertIn(out["trace"]["errand"]["line_sha256"],
                              [hashlib.sha256(p.encode()).hexdigest() for p in text.split("\n\n")])
        for prompt in (rprompt, pprompt):
            for text in situations(prompt):
                self.assertNotIn("this traveller", text.lower())
        self.assertEqual(len({applied["trace"]["errand"]["line_sha256"], other["trace"]["errand"]["line_sha256"],
                              closed["trace"]["errand"]["line_sha256"]}), 3)
        self.assertEqual({applied["trace"]["boot"], plain["trace"]["boot"]}, {errands.BOOT_ID})
        self.assertEqual((applied["trace"]["errand"]["step"], applied["trace"]["errand"]["applied"]), ("pointer", True))
        self.assertEqual(len(applied["trace"]["errand"]["line_sha256"]), 64)
        self.assertEqual(refused["trace"]["errand"], {"step": "pointer", "applied": False, "line_sha256": None})
        self.assertEqual((closed["trace"]["errand"]["step"], closed["trace"]["errand"]["applied"]), ("closed", True))
        self.assertIsNone(plain["trace"]["errand"])
        self.assertTrue(applied["trace"]["facts"])                               # the speaker's own facts block is there too
        self.assertNotIn("trace", self.turn(sender, ctx)[0])                     # and with the switch off, no trace at all

    def test_the_closing_line_after_an_answer_that_was_neither_yes_nor_no(self):
        sender, _ = self.sender_with("word", "kin")
        out, prompt = self.turn(sender, {"step": "closed"})
        self.assertIn("The matter is closed", prompt)
        self.assertNotIn("errand", out)

    def test_the_standing_block_gets_its_map_from_the_town_list_usable_for_the_request(self):
        """Codex Code R1 C3: the map beside the town comes from the town list, never from a name."""
        sender, _ = self.sender_with("word", "kin")
        at = self.held.where(sender)
        req = {"npc_id": sender, "roster_stamp": self.stamp, "standing": {"town": at[1]}}
        brain.standing_map(req)
        self.assertEqual(req["standing"]["map"], at[0])
        for bad in ({"npc_id": sender, "roster_stamp": "0" * 64, "standing": {"town": at[1]}},
                    {"npc_id": "nobody", "roster_stamp": self.stamp, "standing": {"town": at[1]}},
                    {"npc_id": sender, "roster_stamp": self.stamp}):
            brain.standing_map(bad)
            self.assertNotIn("map", bad.get("standing") or {})
        kept = {"npc_id": sender, "roster_stamp": self.stamp, "standing": {"town": at[1], "map": "Elsewhere"}}
        brain.standing_map(kept)
        self.assertEqual(kept["standing"]["map"], "Elsewhere")                   # a map already given is not overwritten

    def test_pay_band_falls_to_the_lower_one_when_the_town_graph_cannot_say(self):
        self.assertTrue(brain.errand_neighbours("Britain", "Vesper"))            # RUMOURS is None here

        class Graph:
            def _load(self):
                return {"_graph": {"Britain": [("Vesper", 4.0)], "Vesper": [("Britain", 4.0)], "Yew": []}}

            def resolve_town(self, name):
                return name
        with mock.patch.object(brain, "RUMOURS", Graph()):
            self.assertTrue(brain.errand_neighbours("Britain", "Vesper"))
            self.assertFalse(brain.errand_neighbours("Britain", "Yew"))
            self.assertTrue(brain.errand_neighbours("Britain", "Nowhere"))



class Remind(Base):
    """Design v6 12.2 (Codex "Errands-Redesign" RD1 and R2 note 1): the remind step rebuilds the sentence that was
    spoken, with the stored seed and the stored answer. It draws nothing, writes nothing and calls no model. Whether
    the result may be SAID is the shard's comparison against the hash it stored; these tests show that comparison has
    something to bite on - the two counterexamples of the review give a different sentence under an equal digest."""

    def row(self, sender, c, step, **more):
        return dict({"step": step, "gen": 3, "request": "q9", "row": 12, "version": 2, "char": CHAR, "speech_rev": 9,
                     "stamp": self.stamp, "kind": c[0], "route": c[1], "sender": sender, "receiver": c[2],
                     "about": c[3], "line_key": "m01" if c[0] == "word" else None, "seed": "12"}, **more)

    def proof(self, body):
        with mock.patch.object(brain, "ollama_chat", side_effect=AssertionError("model")):
            code, out = self.post("/v1/errands/proof", body)
        self.assertEqual((code, out["ok"]), (200, True))
        return out["proof"]

    def world(self):
        return errands.World(self.led, self.held, brain.errand_tables(), et.load())

    def test_the_ask_comes_back_word_for_word_with_the_stored_seed_and_nothing_is_written(self):
        for kind, route in (("word", "kin"), ("word", "cousin"), ("ask_after", "kin"), ("introduce", "contact")):
            sender, c = self.sender_with(kind, route)
            asked = errands.prove(self.world(), dict(self.row(sender, c, "ask"), seed="ticket-a"))
            before = self.led.db.total_changes
            again = self.proof(dict(self.row(sender, c, "remind", role="sender"), seed="ticket-a"))
            self.assertEqual(again["line"], asked["ask_line"], (kind, route))
            self.assertEqual(again["digest"], asked["digest"])
            self.assertEqual(again["step"], "remind")
            self.assertNotIn("yes_line", again)
            self.assertNotIn("answer", again)
            self.assertEqual(self.led.db.total_changes, before)                   # nothing written
        self.assertTrue(any(k.get("op") == "remind" and k.get("outcome") == "proof" for k in self.logs))

    def test_the_same_facts_with_another_seed_give_another_sentence_under_an_equal_digest(self):
        """Codex RD1, second counterexample: the digest cannot tell these apart; the stored hash can."""
        sender, c = self.sender_with("word", "kin")
        lines, digests = set(), set()
        for seed in ("t1", "t2", "t3", "t4", "t5", "t6", "t7", "t8"):
            p = self.proof(dict(self.row(sender, c, "remind", role="sender"), seed=seed))
            lines.add(p["line"])
            digests.add(p["digest"])
        self.assertEqual(len(digests), 1)
        self.assertGreater(len(lines), 1)

    def test_a_changed_trade_gives_another_sentence_under_an_equal_digest(self):
        """Codex RD1, first counterexample."""
        sender, c = self.sender_with("word", "kin")
        body = dict(self.row(sender, c, "remind", role="sender"), seed="ticket-a")
        first = self.proof(body)
        person = self.held.where(c[2])[2]
        self.assertIn(et.a_trade(person["role"]), first["line"])
        with mock.patch.dict(person, {"role": "Lamplighter"}):
            moved = self.proof(body)
        self.assertEqual(moved["digest"], first["digest"])
        self.assertNotEqual(moved["line"], first["line"])
        self.assertIn("a lamplighter", moved["line"])

    def test_the_relatives_sentence_is_rebuilt_from_the_stored_answer_and_never_drawn_again(self):
        sender, c = self.sender_with("ask_after", "kin")
        out = self.proof(self.row(sender, c, "deliver"))
        answer = out["answer"]
        before = self.led.db.total_changes
        again = self.proof(self.row(sender, c, "remind", role="receiver", answer=answer))
        self.assertEqual((again["line"], again["answer_changed"]), (out["line"], False))
        self.assertEqual(again["digest"], out["digest"])
        self.assertNotIn("answer", again)                                        # nothing new to fix on a row
        self.assertEqual(self.led.db.total_changes, before)
        # a seed under which the delivery step would have DRAWN another answer: remind still says the stored one
        lists = et.load()
        said = et.answer_forms(lists, answer["key"], self.held.where(c[2])[2]["name"])[0]
        drew_other = 0
        for n in range(40):
            seed = f"s{n}"
            drew_other += errands._pick(lists["answer"], "answer", seed)["key"] != answer["key"]
            line = self.proof(dict(self.row(sender, c, "remind", role="receiver", answer=answer), seed=seed))["line"]
            self.assertIn(said, line)
        self.assertGreater(drew_other, 0)

    def test_the_relatives_sentence_is_not_given_once_the_stored_answer_no_longer_binds(self):
        sender, c = self.sender_with("ask_after", "kin")
        out = self.proof(self.row(sender, c, "deliver"))
        body = self.row(sender, c, "remind", role="receiver", answer=out["answer"])
        person = self.held.where(c[2])[2]
        with mock.patch.dict(person, {"role": "Lamplighter"}):                   # the answer digest holds the trade
            moved = self.proof(body)
        self.assertEqual(moved["digest"], out["digest"])                          # the errand itself is still true
        self.assertTrue(moved["answer_changed"])
        self.assertNotIn("line", moved)
        for bad in (dict(body, answer=dict(out["answer"], hash="0" * 64)),):
            p = self.proof(bad)
            self.assertTrue(p["answer_changed"])
            self.assertNotIn("line", p)

    def test_no_proof_for_a_request_that_is_not_a_reminder_of_this_errand(self):
        sender, c = self.sender_with("ask_after", "kin")
        answer = self.proof(self.row(sender, c, "deliver"))["answer"]
        wsender, wc = self.sender_with("word", "kin")
        for bad in (self.row(sender, c, "remind"),                                               # no role
                    self.row(sender, c, "remind", role="bystander"),
                    self.row(sender, c, "remind", role="receiver"),                              # no answer
                    self.row(sender, c, "remind", role="receiver", answer={"key": "m01", "hash": "0" * 64, "digest": "0" * 64}),
                    self.row(sender, c, "remind", role="receiver", answer=dict(answer, digest="short")),
                    self.row(wsender, wc, "remind", role="receiver", answer=answer),             # not a round trip
                    self.row(sender, c, "remind", role="sender", stamp="0" * 64),
                    self.row(sender, c, "remind", role="sender", receiver=sender),
                    dict(self.row(wsender, wc, "remind", role="sender"), line_key="nope")):
            with mock.patch.object(brain, "ollama_chat", side_effect=AssertionError("model")):
                code, out = self.post("/v1/errands/proof", bad)
            self.assertEqual((code, out["ok"], out["proof"]), (200, True, None), bad)

    def test_a_sentence_the_last_check_stops_is_no_proof(self):
        sender, c = self.sender_with("word", "kin")
        with mock.patch.object(brain, "errand_final_check", return_value=False):
            self.assertIsNone(self.proof(self.row(sender, c, "remind", role="sender")))

    def test_the_hash_the_shard_stores_is_plain_sha256_of_the_utf8_sentence(self):
        """The same vector is asserted in the shard's tests (ErrandsV6Tests): an em dash must hash alike on both sides."""
        self.assertEqual(hashlib.sha256("Hm. Never mind — it will keep.".encode("utf-8")).hexdigest(),
                         "4acc4fce1ab87956d626327d0d9536e09934eb6c91bd93212c04dfeaaf5be28f")


if __name__ == "__main__":
    unittest.main()
