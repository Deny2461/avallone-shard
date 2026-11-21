"""Townsfolk Stage 2 repair A4 (2026-11-16): the reply handler really applies the facts code's per-turn drops to the
nature it sends - a dropped habit or personality line is absent from the prompt the MODEL is sent, the facts block is
still there, and with no drops nothing changes. Ollama is never called. Run: python3 test_people_wiring.py"""
import json
import os
import sys
import tempfile
import threading
import time
import unittest
import urllib.request
import uuid
from http.server import ThreadingHTTPServer
from unittest import mock

_d = tempfile.TemporaryDirectory()
with open(os.path.join(_d.name, "c.json"), "w") as f:
    json.dump({"memory_db": os.path.join(_d.name, "x.db"), "lore_dir": _d.name, "secrets_dir": _d.name,
               "quiet_windows": [], "reflection_mode": "off"}, f)
os.environ["AVALLONE_BRAIN_CONFIG"] = os.path.join(_d.name, "c.json")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import brain  # noqa: E402
import nature  # noqa: E402

IDS = [str(uuid.UUID(int=k)) for k in range(1, 4001)]
CHAR = "22222222-2222-4222-8222-222222222222"
FACTS = "The facts of your own life:\n- You are 40 years old."


class PeopleWiring(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.srv = ThreadingHTTPServer(("127.0.0.1", 0), brain.Handler)
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()
        cls.npc = next(i for i in IDS if (nature.of(i) or {}).get("habit") == "slow")   # a manner: in every prompt

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()

    def setUp(self):
        nature._turns.clear()

    def prompt(self, drops, rid):
        calls = []

        def fake(persona, *a, **k):
            calls.append(persona)
            return "Well met, friend.", "none"
        body = {"request_id": rid, "npc_id": self.npc, "subject_id": CHAR, "persona_key": "townsfolk",
                "persona_vars": {"name": "Osric", "role": "tanner", "town": "Britain", "trade": "vendor"},
                "utterance": "hello there", "budget_ms": 8000}
        req = urllib.request.Request(f"http://127.0.0.1:{self.srv.server_address[1]}/v1/npc/respond",
                                     json.dumps(body).encode(), {"Content-Type": "application/json"})
        stub = lambda *a, **k: (FACTS, {"people": "A", "nature_drop": list(drops)})
        with mock.patch.object(brain, "ollama_chat", fake), mock.patch.object(brain, "people_block", stub):
            self.assertEqual(json.loads(urllib.request.urlopen(req, timeout=20).read()).get("say"), "Well met, friend.")
        return calls[0]

    def test_no_drops_sends_the_whole_nature_and_the_facts(self):
        p = self.prompt([], "w-0")
        self.assertIn("Your nature: you are", p); self.assertIn("A habit of speech:", p); self.assertIn(FACTS, p)
        self.assertLess(p.index("Your nature: you are"), p.index(FACTS))       # same order as before A4

    def test_a_dropped_habit_is_not_sent(self):
        p = self.prompt(["habit"], "w-1")
        self.assertNotIn("A habit of speech:", p); self.assertIn("Your nature: you are", p); self.assertIn(FACTS, p)

    def test_a_dropped_personality_line_is_not_sent(self):
        p = self.prompt(["personality"], "w-2")
        self.assertNotIn("Your nature: you are", p); self.assertIn("A habit of speech:", p); self.assertIn(FACTS, p)


class MeasureTrace(unittest.TestCase):
    """Repair C1/D3 (Codex Method R1 F3, F6): every model request has a fingerprint; with the dev-only measure_trace
    setting the reply carries what it was built from. Off (the default), the reply is exactly as before."""
    @classmethod
    def setUpClass(cls):
        cls.srv = ThreadingHTTPServer(("127.0.0.1", 0), brain.Handler)
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()
        cls.npc = IDS[7]

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()

    def call(self, trace_on, replies=("Well met, friend.",), subject=None, seed=None):
        sent, logs, said = [], [], list(replies)
        subject = subject or str(uuid.uuid4())

        def fake_post(req, timeout_s):
            sent.append(json.loads(req.data))
            return {"message": {"content": json.dumps({"say": said[min(len(sent), len(said)) - 1]})},
                    "prompt_eval_count": 1, "eval_count": 1}
        if seed:
            brain.variety.note(str(self.npc), subject, seed)      # this NPC said it to this player a moment ago
        body = {"request_id": "mt-" + subject[:8], "npc_id": self.npc, "subject_id": subject, "persona_key": "townsfolk",
                "persona_vars": {"name": "Osric", "role": "tanner", "town": "Britain", "trade": "vendor"},
                "utterance": "hello there", "budget_ms": 8000}
        req = urllib.request.Request(f"http://127.0.0.1:{self.srv.server_address[1]}/v1/npc/respond",
                                     json.dumps(body).encode(), {"Content-Type": "application/json"})
        stub = lambda *a, **k: (FACTS, {"people": "A", "nature_drop": [], "_snapshot": {"age": 40, "given": True}})
        with mock.patch.object(brain, "post_by", fake_post), mock.patch.object(brain, "people_block", stub), \
                mock.patch.object(brain, "log", lambda **k: logs.append(k)), \
                mock.patch.dict(brain.CFG, {"measure_trace": trace_on}):
            out = json.loads(urllib.request.urlopen(req, timeout=20).read())
        return out, sent, logs

    def test_off_by_default_and_off_means_no_trace(self):
        self.assertFalse(brain.CFG.get("measure_trace"))
        out, sent, logs = self.call(False, replies=("A fair day to you.",))
        self.assertEqual(out.get("say"), "A fair day to you."); self.assertNotIn("trace", out)

    def test_on_the_reply_carries_the_fingerprint_facts_and_snapshot(self):
        out, sent, logs = self.call(True, replies=("The tannery keeps me busy.",))
        t = out["trace"]
        self.assertEqual(len(sent), 1)
        self.assertEqual(t["prompt_sha"], brain.prompt_sha(sent[0]))         # the fingerprint of what was really sent
        self.assertEqual(t["facts"], FACTS); self.assertEqual(t["snapshot"], {"age": 40, "given": True})
        self.assertNotIn("_snapshot", t["people"]); self.assertIn("by_code", t)
        for k in logs:                                                       # the snapshot never reaches the brain log
            self.assertNotIn("_snapshot", k); self.assertNotIn("snapshot", json.dumps(k, default=str))

    def test_a_kept_re_ask_moves_the_fingerprint_to_the_request_that_made_the_words(self):
        out, sent, logs = self.call(True, replies=("Hides do not tan themselves.", "Good morrow, traveller."),
                                    seed="Hides do not tan themselves.")
        self.assertEqual(len(sent), 2); self.assertEqual(out["say"], "Good morrow, traveller.")
        self.assertEqual(out["trace"]["prompt_sha"], brain.prompt_sha(sent[1]))
        self.assertEqual(out["trace"]["by_code"]["variety"]["fixed"], "retry")

    def test_a_dropped_re_ask_keeps_the_first_fingerprint(self):
        out, sent, logs = self.call(True, replies=("Leather is honest work.", "Leather is honest work."),
                                    seed="Leather is honest work.")
        self.assertEqual(len(sent), 2); self.assertEqual(out["trace"]["by_code"]["variety"]["fixed"], "kept")
        self.assertEqual(out["trace"]["prompt_sha"], brain.prompt_sha(sent[0]))

    def test_the_fingerprint_covers_every_part_of_the_request(self):
        base = {"model": "m", "options": {"temperature": 0.3}, "messages": [{"role": "user", "content": "a"}]}
        h = brain.prompt_sha(base)
        self.assertEqual(h, brain.prompt_sha(json.loads(json.dumps(base))))
        for change in ({"model": "n"}, {"options": {"temperature": 0.4}}, {"messages": [{"role": "user", "content": "b"}]}):
            self.assertNotEqual(h, brain.prompt_sha(dict(base, **change)), change)


class RequestNames(unittest.TestCase):
    """Repair A6: the avoid list is built from every name field the shard really sends, and on an exchange step from
    its two speakers and roster - passed through to the facts block."""

    def test_every_name_field_is_read(self):
        req = {"rumours": {"roster": [{"id": "a", "name": "Rowan"}]},
               "party": {"companions": [{"name": "Elspeth Vale", "title": "the Wanderer"}], "roster": ["Kestrel"]},
               "named": {"speaker": "Bryn", "roster": [{"id": "b", "name": "Tamsin"}]},
               "correction": {"roster": [{"id": "c", "name": "Odo"}]}}
        got = brain.request_names(req, ["Hale", "Mira"])
        for n in ("Rowan", "Elspeth Vale", "Kestrel", "Bryn", "Tamsin", "Odo", "Hale", "Mira"):
            self.assertIn(n, got)
        self.assertEqual(brain.request_names({}), [])

    def test_decision_9_the_player_and_the_player_roster_are_read(self):
        got = brain.request_names({"name_check": {"player": "Corvina Testwell", "roster": ["Corvina Testwell", "Halvard"]}})
        self.assertIn("Corvina Testwell", got); self.assertIn("Halvard", got)
        for bad in ({"name_check": None}, {"name_check": "Corvina"}, {"name_check": {"player": None, "roster": None}}):
            self.assertEqual(brain.request_names(bad), [], bad)

    def test_decision_9_name_check_is_read_only_for_the_name_check(self):
        """The player's name must never reach a prompt: the key is read in exactly one place, request_names."""
        src = open(os.path.join(os.path.dirname(os.path.abspath(brain.__file__)), "brain.py")).read()
        self.assertEqual(src.count('"name_check"'), 2)        # both inside request_names (the get and the isinstance)
        start = src.index("def request_names(")
        end = src.index("\ndef ", start + 1)
        self.assertEqual(src[start:end].count('"name_check"'), 2)

    def test_people_block_hands_the_exchange_names_to_the_block(self):
        seen = {}

        def fake_block(v, town, where=None, avoid_names=(), **k):
            seen["avoid"] = list(avoid_names)
            seen["nature_now"] = k.get("nature_now")
            return "FACTS"
        view = {"record": {"status": "resolved", "id": "x"}, "kin": [], "house": None}
        with mock.patch.dict(brain.CFG, {"people": True, "always_know": "A", "town_knowledge": False}), \
                mock.patch.object(brain, "LEDGER", object()), mock.patch.object(brain, "ROSTER", None), \
                mock.patch.object(brain.people_store, "view", lambda *a: view), \
                mock.patch.object(brain.people_store, "block", fake_block), \
                mock.patch.object(brain.people_store, "nature_drops", lambda *a: []):
            text, _ = brain.people_block({"party": {"roster": ["Kestrel"]}}, "x", {"town": "Britain"}, "c", "t",
                                         extra_names=["Elspeth Vale", "Jacob", "Rowan"])
        self.assertEqual(text, "FACTS")
        for n in ("Kestrel", "Elspeth Vale", "Jacob", "Rowan"):
            self.assertIn(n, seen["avoid"])
        self.assertEqual(seen["nature_now"], nature.of("x"))           # repair A7: the jab check sees this turn's nature


class BuildGate(unittest.TestCase):
    """Repair B3: the brain starts a permanent build only after a reconciliation it saw AND a confirmation, after it,
    that the held roster is the shard's current one - once."""

    def setUp(self):
        import townroster, hashlib as hl
        self.d = tempfile.TemporaryDirectory()
        self.led = brain.memory.Ledger(os.path.join(self.d.name, "g.db"))
        self.roster = townroster.Roster(self.led)
        content = json.dumps({"towns": [{"map": "Felucca", "town": "Britain", "people": []}]}, separators=(",", ":"))
        self.assertTrue(self.roster.accept({"epoch": 7, "rev": 1, "content": content, "hash": hl.sha256(content.encode()).hexdigest()})[0])
        self.hash = self.roster.held().hash
        self.calls = []
        self.done = threading.Event()

        self.result = {}

        def fake_build_all(ledger, held, log=None, epoch=None):
            self.calls.append(epoch); self.done.set()
            if isinstance(self.result, Exception):
                raise self.result
            return self.result
        self.patches = [mock.patch.object(brain, "LEDGER", self.led), mock.patch.object(brain, "ROSTER", self.roster),
                        mock.patch.dict(brain.CFG, {"people": True}), mock.patch.object(brain.people_store, "build_all", fake_build_all),
                        mock.patch.dict(brain.PEOPLE_SESSION, {"epoch": None, "roster": None}),
                        mock.patch.dict(brain.PEOPLE_BUILD, {"running": False, "again": False, "failed": None, "retry_at": 0.0, "key": None}),
                        mock.patch.object(brain, "log", lambda **f: None)]
        for x in self.patches:
            x.start()

    def tearDown(self):
        for x in reversed(self.patches):
            x.stop()
        self.d.cleanup()

    def settle(self):
        self.done.wait(2)

    def idle(self):
        end = time.monotonic() + 2
        while brain.PEOPLE_BUILD["running"] and time.monotonic() < end:
            time.sleep(0.01)
        self.assertFalse(brain.PEOPLE_BUILD["running"])

    def test_f4_a_pass_that_failed_is_made_again_on_a_later_confirmation_but_never_at_once(self):
        """Codex Code R1 F4: a failed pass no longer uses up the confirmation that would have started it again."""
        self.led.reconcile(7, 0, 0); brain.people_session_reconciled(7, True)
        for bad in ({"error": 1, "built": 3}, {"audit-failed": 1}, {"catalogue-changed": 1}, {"not-reconciled": 1}, RuntimeError("made up")):
            with self.subTest(bad=bad):
                self.calls.clear(); brain.PEOPLE_SESSION["roster"] = None
                brain.PEOPLE_BUILD.update(failed=None, retry_at=0.0)
                self.result = bad
                self.done.clear(); brain.people_roster_confirmed(self.hash); self.settle(); self.idle()
                self.assertEqual(self.calls, [7])
                self.assertEqual(brain.PEOPLE_BUILD["failed"], self.hash)
                self.assertGreater(brain.PEOPLE_BUILD["retry_at"], time.monotonic() + brain.PEOPLE_RETRY_S - 5)
                for _ in range(3):                                        # three more turns confirm the same roster straight away
                    self.done.clear(); brain.people_roster_confirmed(self.hash); self.done.wait(0.1); self.idle()
                self.assertEqual(self.calls, [7])                         # ... the wait has not passed: no new pass
                brain.PEOPLE_BUILD["retry_at"] = 0.0                      # the wait has passed
                self.result = {"built": 1, "already": 2}
                self.done.clear(); brain.people_roster_confirmed(self.hash); self.settle(); self.idle()
                self.assertEqual(self.calls, [7, 7])                      # tried again, and this time it worked
                self.assertIsNone(brain.PEOPLE_BUILD["failed"])
                self.done.clear(); brain.people_roster_confirmed(self.hash); self.done.wait(0.1); self.idle()
                self.assertEqual(self.calls, [7, 7])                      # nothing left to do: no further pass

    def test_glf1_a_finished_pass_and_a_pass_that_raised_both_name_their_session_and_roster(self):
        """Codex GoLive R1 F1: the completion line carries the shard session and the roster it was for."""
        self.led.reconcile(7, 0, 0); brain.people_session_reconciled(7, True)
        said = []
        with mock.patch.object(brain, "log", lambda **f: said.append(f)):
            self.result = {"built": 1, "already": 2}
            self.done.clear(); brain.people_roster_confirmed(self.hash); self.settle(); self.idle()
            self.result = RuntimeError("made up"); brain.PEOPLE_SESSION["roster"] = None
            self.done.clear(); brain.people_roster_confirmed(self.hash); self.settle(); self.idle()
        ends = [f for f in said if f.get("event") == "people" and f.get("op") == "build_all"]
        self.assertEqual([(f.get("epoch"), f.get("roster"), f.get("outcome")) for f in ends],
                         [(7, self.hash[:12], None), (7, self.hash[:12], "error")])
        self.assertEqual((ends[0]["built"], ends[0]["already"]), (1, 2))

    def next_roster(self, rev):
        import hashlib as hl
        content = json.dumps({"towns": [{"map": "Felucca", "town": "Britain", "people": []},
                                        {"map": "Felucca", "town": "Trinsic", "people": []}]}, separators=(",", ":"))
        self.assertTrue(self.roster.accept({"epoch": 7, "rev": rev, "content": content, "hash": hl.sha256(content.encode()).hexdigest()})[0])
        return self.roster.held().hash

    def test_f4_one_worker_at_a_time_and_the_same_work_asked_for_meanwhile_is_already_covered(self):
        """Codex Code R2 F1: asking again for the work the running pass is doing queues nothing."""
        self.led.reconcile(7, 0, 0); brain.people_session_reconciled(7, True)
        gate, started, threads = threading.Event(), threading.Event(), threading.active_count()

        def slow(ledger, held, log=None, epoch=None):
            self.calls.append(epoch); started.set(); gate.wait(2); return {}
        with mock.patch.object(brain.people_store, "build_all", slow):
            brain.people_roster_confirmed(self.hash); self.assertTrue(started.wait(2))
            for _ in range(5):                                            # asked five more times while it runs
                brain.people_build_soon()
            self.assertEqual(self.calls, [7]); self.assertEqual(threading.active_count(), threads + 1)
            self.assertFalse(brain.PEOPLE_BUILD["again"])
            gate.set(); self.idle()
        self.assertEqual(self.calls, [7])                                 # the one pass - not two, not six
        self.assertIsNone(brain.PEOPLE_BUILD["key"])

    def test_r2f1_a_new_roster_confirmed_while_a_pass_runs_gets_exactly_one_pass_after_it(self):
        """... and different work is still followed: a new roster confirmed during a pass is built when it ends."""
        self.led.reconcile(7, 0, 0); brain.people_session_reconciled(7, True)
        gate, started, seen = threading.Event(), threading.Event(), []

        def slow(ledger, held, log=None, epoch=None):
            seen.append(held.hash); started.set(); gate.wait(2); return {}
        with mock.patch.object(brain.people_store, "build_all", slow):
            brain.people_roster_confirmed(self.hash); self.assertTrue(started.wait(2))
            new = self.next_roster(2)
            for _ in range(5):                                            # the shard confirms the new one, five times
                brain.people_roster_confirmed(new)
            self.assertEqual(seen, [self.hash])
            gate.set(); self.idle()
        self.assertEqual(seen, [self.hash, new])

    def test_r2f1_a_new_session_for_the_same_roster_while_a_pass_runs_is_different_work(self):
        self.led.reconcile(7, 0, 0); brain.people_session_reconciled(7, True)
        gate, started = threading.Event(), threading.Event()

        def slow(ledger, held, log=None, epoch=None):
            self.calls.append(epoch); started.set(); gate.wait(2); return {}
        with mock.patch.object(brain.people_store, "build_all", slow):
            brain.people_roster_confirmed(self.hash); self.assertTrue(started.wait(2))
            self.led.reconcile(8, 0, 0); brain.people_session_reconciled(8, True)
            brain.people_roster_confirmed(self.hash)
            gate.set(); self.idle()
        self.assertEqual(self.calls, [7, 8])

    def test_r2f1_a_retry_that_fails_again_is_not_followed_at_once_by_a_queued_pass(self):
        """Codex Code R2 F1, as reproduced there: first pass fails; its retry comes due and is running when five ordinary
        confirmations of the same roster arrive; the retry fails too. Two attempts, not three - and the wait starts anew."""
        self.led.reconcile(7, 0, 0); brain.people_session_reconciled(7, True)
        self.result = {"audit-failed": 1}
        self.done.clear(); brain.people_roster_confirmed(self.hash); self.settle(); self.idle()
        self.assertEqual(self.calls, [7]); self.assertEqual(brain.PEOPLE_BUILD["failed"], self.hash)
        brain.PEOPLE_BUILD["retry_at"] = 0.0                              # the wait has passed
        gate, started = threading.Event(), threading.Event()

        def held_up(ledger, held, log=None, epoch=None):
            self.calls.append(epoch); started.set(); gate.wait(2); return {"audit-failed": 1}
        with mock.patch.object(brain.people_store, "build_all", held_up):
            brain.people_roster_confirmed(self.hash); self.assertTrue(started.wait(2))     # the retry, now running
            for _ in range(5):
                brain.people_roster_confirmed(self.hash)                                   # the failure mark is still the old, expired one
            self.assertFalse(brain.PEOPLE_BUILD["again"])
            gate.set(); self.idle()
            self.assertEqual(self.calls, [7, 7])                                           # no third pass
            self.assertGreater(brain.PEOPLE_BUILD["retry_at"], time.monotonic() + brain.PEOPLE_RETRY_S - 5)
            for _ in range(3):
                brain.people_roster_confirmed(self.hash); time.sleep(0.05); self.idle()
            self.assertEqual(self.calls, [7, 7])                                           # and none until the new wait has passed
            brain.PEOPLE_BUILD["retry_at"] = 0.0
            started.clear(); brain.people_roster_confirmed(self.hash); self.assertTrue(started.wait(2))
            gate.set(); self.idle()
            self.assertEqual(self.calls, [7, 7, 7])

    def test_nothing_before_a_reconciliation(self):
        brain.people_roster_confirmed(self.hash); brain.people_build_soon(); self.settle()
        self.assertEqual(self.calls, [])

    def test_a_reconciliation_then_a_confirmation_builds_once(self):
        self.led.reconcile(7, 0, 0)
        brain.people_session_reconciled(7, True)
        brain.people_build_soon(); self.settle()
        self.assertEqual(self.calls, [])                                  # reconciled, but the roster not yet confirmed
        brain.people_roster_confirmed(self.hash); self.settle()
        self.assertEqual(self.calls, [7])
        self.done.clear(); brain.people_roster_confirmed(self.hash); self.done.wait(0.3)
        self.assertEqual(self.calls, [7])                                 # a talk turn never starts another pass

    def test_a_confirmation_of_another_roster_is_not_one(self):
        self.led.reconcile(7, 0, 0); brain.people_session_reconciled(7, True)
        brain.people_roster_confirmed("0" * 64); self.done.wait(0.3)
        self.assertEqual(self.calls, [])

    def test_a_failed_reconciliation_clears_the_session(self):
        self.led.reconcile(7, 0, 0); brain.people_session_reconciled(7, True)
        brain.people_session_reconciled(8, False)
        brain.people_roster_confirmed(self.hash); self.done.wait(0.3)
        self.assertEqual(self.calls, [])

    def test_the_ledger_must_still_agree_when_the_build_starts(self):
        self.led.reconcile(7, 0, 0); brain.people_session_reconciled(7, True)
        self.led.reconcile(8, 7, 0)                                       # reconciled again before the build began
        brain.people_roster_confirmed(self.hash); self.done.wait(0.3)
        self.assertEqual(self.calls, [])


class NamedConfig(unittest.TestCase):
    """Codex Code R1 F2: a config file named for this run that is not there must never be answered with the built-in
    defaults (the live brain's port and memory database). Nothing is opened in either test."""

    class Reached(Exception):
        pass

    def start(self, env):
        with tempfile.TemporaryDirectory() as d:
            missing = os.path.join(d, "not-there.json")
            reached = mock.Mock(side_effect=self.Reached())
            with mock.patch.dict(os.environ, {}, clear=False), mock.patch.object(brain, "CONFIG_PATH", missing), \
                    mock.patch.object(brain, "open_ledger", reached), mock.patch.object(brain, "make_server", reached), \
                    mock.patch.object(brain, "log", lambda **f: None):
                os.environ.pop("AVALLONE_BRAIN_CONFIG", None)
                if env:
                    os.environ["AVALLONE_BRAIN_CONFIG"] = missing
                try:
                    brain.main()
                except SystemExit as e:
                    return "refused", str(e), reached.call_count
                except self.Reached:
                    return "went on", "", reached.call_count

    def test_a_named_config_file_that_is_missing_refuses_to_start_before_anything_is_opened(self):
        outcome, said, opened = self.start(env=True)
        self.assertEqual((outcome, opened), ("refused", 0))
        self.assertIn("does not exist", said)

    def test_with_no_file_named_the_start_is_as_it_was(self):
        outcome, _, opened = self.start(env=False)                        # the live service names no file
        self.assertEqual((outcome, opened), ("went on", 1))


if __name__ == "__main__":
    unittest.main()
