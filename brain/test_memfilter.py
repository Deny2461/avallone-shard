"""§21 memory filter (the owner, chat, 2026-11-06): money talk said in one conversation never reaches a later one.
Unit tests for the helper and each read path, the write-side strip in /v1/npc/summarize, and a deterministic
two-visit check through the real request handler (plant a debt in visit 1; the visit-2 prompt holds no trace of it,
while a non-money line from the same visit still comes through - the positive control). Ollama is never called.
Run: python3 test_memfilter.py"""
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
    json.dump({"memory_db": os.path.join(_d.name, "x.db"), "secrets_dir": _d.name, "quiet_windows": [],
               "prompt_layout": "v2", "reflection_mode": "off", "lore_dir": _d.name}, f)
os.environ["AVALLONE_BRAIN_CONFIG"] = os.path.join(_d.name, "c.json")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import brain  # noqa: E402
import memory  # noqa: E402

NPC, CHAR = "11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222"
OPENER = urllib.request.build_opener()
DEBT_P, DEBT_N = "You still owe me fifty gold for those wolf pelts.", "Aye, I owe you that. I'll pay you back tomorrow."
WATCH_P, WATCH_N = "Remember you swore to keep watch by the wolves at Yew?", "Aye, I keep my word on the watch."
DEBT_MARKS = ("fifty", "pelts", "owe", "pay you back")


def ev(seq, kind="conversation", at="2026-10-02T15:00:00Z", **facts):
    return {"event_id": f"1-{seq}", "epoch": 1, "seq": seq, "npc_id": NPC, "char_id": CHAR, "kind": kind,
            "facts": facts, "at_utc": at, "schema": memory.SCHEMA_VERSION}


class Helper(unittest.TestCase):
    def test_money_lines_match(self):
        for t in ("You owe me", "I'll pay you fifty gold", "we split the reward", "put it on my tab",
                  "a loan of 20 coins", "lend me some silver", "you said I'd get a cut of the take",
                  "the bounty on that troll", "Your wages, as agreed", "I bet you a crown", "OWED", "repaid in full",
                  "my purse is empty"):
            self.assertTrue(memory.money_talk(t), t)

    def test_promises_and_plain_talk_do_not_match(self):
        for t in ("You promised to keep watch", "you swore you'd wait for me at the gate", "that storm was awful",
                  "my tabby cat", "the golden hour", "a paintbrush", "shared?", "", None):
            self.assertFalse(memory.money_talk(t), repr(t))

    def test_strip_money_copies_and_drops(self):
        facts = {"claims": ["they are owed forty gold by the smith", "they slew a wyrm"], "topics": ["debts", "dragons"],
                 "items": ["request|lend them ten coins", "request|keep watch tonight", "pet|dog|Rex"], "pt": 5}
        out = memory.strip_money(facts)
        self.assertEqual(out["claims"], ["they slew a wyrm"])
        self.assertEqual(out["topics"], ["dragons"])
        self.assertEqual(out["items"], ["request|keep watch tonight", "pet|dog|Rex"])
        self.assertEqual(out["pt"], 5)
        self.assertEqual(len(facts["claims"]), 2)   # the original is untouched


class LedgerPaths(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.led.reconcile(1, 0, 0)
        self.led.ingest([ev(1, player_said=WATCH_P, she_said=WATCH_N),
                         ev(2, player_said=DEBT_P, she_said=DEBT_N),
                         ev(3, player_said="Nice weather.", she_said="It is, for once.")])

    def tearDown(self):
        self.led.db.close()
        self.dir.cleanup()

    def test_history_restore_skips_money(self):
        text = json.dumps(self.led.history(NPC, CHAR, pairs=4))
        self.assertIn("keep watch", text)
        self.assertIn("Nice weather", text)
        for m in DEBT_MARKS:
            self.assertNotIn(m, text)

    def test_history_backfills_to_n_non_money(self):
        h = self.led.history(NPC, CHAR, pairs=2)
        self.assertEqual([x["text"] for x in h if x["speaker"] == "player"], [WATCH_P, "Nice weather."])

    def test_matched_recall_never_money(self):
        for utt in ("about the gold you owe me for the pelts", "fifty gold", "wolf pelts", "the wolves at Yew"):
            lines, _ = self.led.retrieve(NPC, CHAR, utt, skip_conversations=0)
            text = " ".join(lines)
            for m in DEBT_MARKS:
                self.assertNotIn(m, text, utt)
        lines, _ = self.led.retrieve(NPC, CHAR, "the wolves at Yew", skip_conversations=0)
        self.assertIn("keep watch", " ".join(lines))   # positive control: the same query shape does recall

    def test_skip_set_matches_restore(self):
        h = self.led.history(NPC, CHAR, pairs=2)
        lines, _ = self.led.retrieve(NPC, CHAR, "wolves weather watch Yew", skip_conversations=len(h) // 2)
        self.assertEqual([l for l in lines if "You talked" in l], [])   # both restored exchanges skipped, debt never

    def test_visit_notes_and_pair_rows_strip_money(self):
        self.led.ingest([ev(10, "visit_summary", claims=["they are owed fifty gold by Mirabel", "they slew a wyrm"],
                            topics=["the debt", "dragons"])])
        notes = " ".join(self.led.visit_notes(NPC, CHAR, "gold wyrm debt"))
        self.assertIn("slew a wyrm", notes)
        self.assertNotIn("fifty", notes)
        self.assertNotIn("debt", notes)
        rows = [r for r in self.led.pair_rows(NPC, CHAR) if r["kind"] == "visit_summary"]
        self.assertEqual(rows[0]["facts"]["claims"], ["they slew a wyrm"])
        self.assertEqual(rows[0]["facts"]["topics"], ["dragons"])
        stored = json.loads(self.led.db.execute("SELECT facts FROM events WHERE event_id = '1-10'").fetchone()[0])
        self.assertEqual(len(stored["claims"]), 2)   # read-side only: nothing in the ledger is changed


class TwoVisits(unittest.TestCase):
    """Visit 1 plants a debt (stored as the shard stores it); visit 2 is a fresh conversation. Code check, no model."""
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        brain.LEDGER = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        brain.LEDGER.reconcile(1, 0, 0)
        brain.LEDGER.ingest([ev(1, at="2026-10-01T15:00:00Z", player_said=WATCH_P, she_said=WATCH_N),
                             ev(2, at="2026-10-01T15:01:00Z", player_said=DEBT_P, she_said=DEBT_N)])
        self.logs, self.sent = [], []
        self.srv = ThreadingHTTPServer(("127.0.0.1", 0), brain.Handler)
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()

    def tearDown(self):
        self.srv.shutdown()
        brain.LEDGER.db.close()
        self.dir.cleanup()

    def ask(self, utterance, history):
        class R:
            def __init__(s, b): s.b = b
            def __enter__(s): return s
            def __exit__(s, *a): return False
            def read(s, *a): return s.b

        def fake(req, timeout=None):
            self.sent.append(json.loads(req.data))
            return R(json.dumps({"message": {"content": json.dumps({"say": "Aye.", "action": "none"})},
                                 "prompt_eval_count": 1, "eval_count": 1}).encode())
        body = {"request_id": "t", "npc_id": NPC, "subject_id": CHAR, "priority": "Companion", "persona": "PERSONA",
                "utterance": utterance, "situation": "It is evening.", "budget_ms": 8000, "history": history}
        with mock.patch.object(brain.urllib.request, "urlopen", fake), \
                mock.patch.object(brain, "log", lambda **k: self.logs.append(k)):
            r = OPENER.open(urllib.request.Request(f"http://127.0.0.1:{self.srv.server_port}/v1/npc/respond",
                                                   data=json.dumps(body).encode(),
                                                   headers={"Content-Type": "application/json"}), timeout=10)
            self.assertTrue(json.load(r)["ok"])
        return json.dumps(self.sent[-1]["messages"])

    def check(self, layout):
        with mock.patch.dict(brain.CFG, {"prompt_layout": layout}):
            for history in ([], [{"speaker": "player", "text": "hi"}, {"speaker": "npc", "text": "Well met."}]):
                if history:   # as the shard does: this visit's own exchange is in the ledger too (newest)
                    brain.LEDGER.ingest([ev(3, at="2026-10-05T15:00:00Z", player_said="hi", she_said="Well met.")])
                for utt in ("So, about the fifty gold you owe me for the wolf pelts?", "Do you remember the wolves at Yew?"):
                    prompt = self.ask(utt, history)
                    after = prompt.replace(json.dumps(utt)[1:-1], "")   # the player's own line this turn is not memory
                    for m in DEBT_MARKS:
                        self.assertNotIn(m, after, (layout, bool(history), utt))
                    if not history or "Yew" in utt:   # positive control (restored history, or a matching line)
                        self.assertIn("keep watch", prompt, (layout, bool(history), utt))

    def test_v2_no_trace(self):
        self.check("v2")

    def test_v1_no_trace(self):
        self.check("v1")

    def test_standing_money_line_removed(self):
        prompt = self.ask("hello", [])
        self.assertNotIn("You owe no one any money", prompt)


class SummarizeWrite(unittest.TestCase):
    def test_money_claims_never_written(self):
        srv = ThreadingHTTPServer(("127.0.0.1", 0), brain.Handler)
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        hist = [{"speaker": "player", "text": "I am owed fifty gold by the smith."},
                {"speaker": "npc", "text": "Are you now."},
                {"speaker": "player", "text": "I slew a wyrm near Yew last week."},
                {"speaker": "npc", "text": "Did you!"}]
        parsed = {"claims": [{"line": 1, "text": "I am owed fifty gold by the smith."},
                             {"line": 2, "text": "I slew a wyrm near Yew last week."}],
                  "topics": ["debts", "dragons"]}
        try:
            with mock.patch.object(brain, "ollama_json", lambda *a, **k: parsed), \
                    mock.patch.object(brain.named, "worth_summarizing", lambda h: (True, "")), \
                    mock.patch.object(brain, "log", lambda **k: None):
                r = OPENER.open(urllib.request.Request(
                    f"http://127.0.0.1:{srv.server_port}/v1/npc/summarize",
                    data=json.dumps({"request_id": "s", "npc_id": NPC, "history": hist,
                                     "named": {"speaker": "Mirabel", "roster": []}}).encode(),
                    headers={"Content-Type": "application/json"}), timeout=10)
                out = json.load(r)
        finally:
            srv.shutdown()
        self.assertTrue(out["ok"], out)
        notes = json.loads(out["say"])
        self.assertTrue(any("wyrm" in c for c in notes["claims"]), notes)   # positive control
        self.assertFalse(any(memory.money_talk(c) for c in notes["claims"] + notes["topics"]), notes)


# Codex R5-1 (2026-11-06): ordinary bill / account / balance / credit / settle-up language was missed by the detector and
# carried into later memory. Codex's four exchanges, verbatim, through every reader; plus the visit-note/item cases.
R51 = [("Did I settle up for last night?", "Your bill for last night's meal is still outstanding."),
       ("Have I cleared my account?", "The balance for last night's room is still outstanding."),
       ("What about my bill?", "You have an unpaid bill for the meal."),
       ("You said I could eat on credit.", "Yes, I extended you credit for last night's meal.")]
R51_MARKS = ("bill", "balance", "credit", "settle", "outstanding", "unpaid", "account")


class CodexR51(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.led.reconcile(1, 0, 0)
        evs = [ev(1, player_said=WATCH_P, she_said=WATCH_N)]
        evs += [ev(2 + i, player_said=p, she_said=n) for i, (p, n) in enumerate(R51)]
        evs += [ev(10 + i, player_said=f"Clean line {i} about the wolves.", she_said="Aye.") for i in range(4)]
        self.led.ingest(evs)

    def tearDown(self):
        self.led.db.close()
        self.dir.cleanup()

    def test_helper_catches_each_line(self):
        for p, n in R51:
            self.assertTrue(memory.money_talk(p), p)
            self.assertTrue(memory.money_talk(n), n)

    def test_restore_and_recall_after_four_clean_exchanges(self):
        text = json.dumps(self.led.history(NPC, CHAR, pairs=8)).lower()
        self.assertIn("keep watch", text)   # positive control: the older non-money exchange is restored
        for m in R51_MARKS:
            self.assertNotIn(m, text)
        for utt in ("my bill for last night", "the balance on my account", "eat on credit", "did I settle up", "the meal"):
            for recs in (self.led.retrieve(NPC, CHAR, utt, skip_conversations=0, records=True),
                         self.led.retrieve(NPC, CHAR, utt, skip_conversations=4, stable_first=True,
                                           matched_budget=600, records=True)):
                text = " ".join(r["line"] for r in recs).lower()
                for m in R51_MARKS:
                    self.assertNotIn(m, text, utt)
        recs = self.led.retrieve(NPC, CHAR, "the wolves at Yew", skip_conversations=0, records=True)
        self.assertIn("keep watch", " ".join(r["line"] for r in recs))   # positive control: same query shape recalls

    def test_visit_notes_items_and_cards(self):
        facts = {"claims": ["they have an unpaid bill for the meal", "they slew a wyrm"],
                 "topics": ["unpaid bills", "dragons"], "items": ["request|settle their account", "request|keep watch tonight"]}
        out = memory.strip_money(facts)
        self.assertEqual(out["claims"], ["they slew a wyrm"])
        self.assertEqual(out["topics"], ["dragons"])
        self.assertEqual(out["items"], ["request|keep watch tonight"])
        self.led.ingest([ev(20, "visit_summary", **facts)])
        notes = " ".join(self.led.visit_notes(NPC, CHAR, "bill account wyrm")).lower()
        self.assertIn("wyrm", notes)
        for m in R51_MARKS:
            self.assertNotIn(m, notes)
        row = [r for r in self.led.pair_rows(NPC, CHAR) if r["kind"] == "visit_summary"][0]["facts"]
        self.assertEqual((row["claims"], row["topics"], row["items"]),
                         (["they slew a wyrm"], ["dragons"], ["request|keep watch tonight"]))


# Codex GitHub-Publishing R5-1 (2026-11-07): word families were missed (repayment, reimburse, deposits). Codex's three
# exchanges, verbatim, through the REAL request handler across two visits; plus every family form through the helper.
R51B = [("When will you reimburse me for the meal?", "I shall reimburse you tomorrow."),
        ("What happened to the repayment you promised?", "The repayment will be ready tomorrow."),
        ("What happened to my deposits?", "Your deposits will be returned tomorrow.")]
R51B_MARKS = ("reimburse", "repayment", "deposits", "the meal", "tomorrow")
R51B_FAMILIES = ("repay", "repays", "repaid", "repaying", "repayment", "repayments", "reimburse", "reimburses",
                 "reimbursed", "reimbursing", "reimbursement", "reimbursements", "deposit", "deposits", "deposited",
                 "depositing", "lender", "borrower", "rented", "rental", "priced", "earned", "profit", "taxes", "pennies")


class CodexR51Families(TwoVisits):
    def setUp(self):
        super().setUp()
        brain.LEDGER.ingest([ev(10 + i, at=f"2026-10-01T15:0{2 + i}:00Z", player_said=p, she_said=n)
                             for i, (p, n) in enumerate(R51B)])

    def test_helper_catches_each_form(self):
        for w in R51B_FAMILIES:
            for t in (w, w.capitalize(), f"about the {w} we spoke of"):
                self.assertTrue(memory.money_talk(t), t)
        for p, n in R51B:
            self.assertTrue(memory.money_talk(p), p)
            self.assertTrue(memory.money_talk(n), n)

    def check_families(self, layout):
        with mock.patch.dict(brain.CFG, {"prompt_layout": layout}):
            for utt in ("So, when will I be reimbursed for the meal?", "About my deposits and that repayment...",
                        "Do you remember the wolves at Yew?"):
                prompt = self.ask(utt, [])
                after = prompt.replace(json.dumps(utt)[1:-1], "").lower()
                for m in R51B_MARKS + DEBT_MARKS:
                    self.assertNotIn(m, after, (layout, utt, m))
                self.assertIn("keep watch", prompt, (layout, utt))   # positive control: same visit, non-money line

    def test_families_v2_no_trace(self):
        self.check_families("v2")

    def test_families_v1_no_trace(self):
        self.check_families("v1")



# Codex GH-Pub R5-2 (2026-11-07): recall must not decode the pair's whole conversation history each reply. Count the
# money checks on a pair with many conversation rows: bounded by the skip count plus the few candidates, not the rows.
class CodexR52Bounded(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.led.reconcile(1, 0, 0)
        evs = [ev(1, player_said=WATCH_P, she_said=WATCH_N), ev(2, player_said=DEBT_P, she_said=DEBT_N)]
        evs += [ev(10 + i, player_said=f"Line {i} about the weather.", she_said="Aye.") for i in range(2000)]
        evs += [ev(5000, "visit_summary", claims=["they slew a wyrm"], topics=["dragons"], items=[])]
        for i in range(0, len(evs), 200):   # ingest takes at most 200 events per call
            self.led.ingest(evs[i:i + 200])
        assert self.led.rdb.execute("SELECT COUNT(*) FROM events WHERE kind = ?", ("conversation",)).fetchone()[0] == 2002

    def tearDown(self):
        self.led.db.close()
        self.dir.cleanup()

    def count(self, **kw):
        n = [0]
        real = memory.conversation_money

        def counting(f):
            n[0] += 1
            return real(f)
        with mock.patch.object(memory, "conversation_money", counting):
            recs = self.led.retrieve(NPC, CHAR, kw.pop("utt"), records=True, **kw)
        return n[0], " ".join(r["line"] for r in recs)

    def test_checks_do_not_grow_with_history(self):
        for kw in ({"skip_conversations": 0}, {"skip_conversations": 4},
                   {"skip_conversations": 4, "stable_first": True, "matched_budget": 600}):
            n, text = self.count(utt="the wolves at Yew and the fifty gold", **dict(kw))
            self.assertLess(n, 40, (kw, n))                 # was 2,002 (every row) before R5-2
            self.assertIn("keep watch", text, kw)          # positive control: the old non-money line still recalled
            for m in DEBT_MARKS:
                self.assertNotIn(m, text, kw)              # and the debt still never is

    def test_skip_still_counts_only_non_money(self):
        led = self.led
        led.ingest([ev(6000 + i, player_said=DEBT_P, she_said=DEBT_N) for i in range(3)])   # newest three are money
        n, text = self.count(utt="weather", skip_conversations=2)
        self.assertNotIn("Line 1999 ", text)   # the two newest NON-money exchanges are still skipped (in chat history)
        self.assertNotIn("Line 1998 ", text)
        for m in DEBT_MARKS:
            self.assertNotIn(m, text)


if __name__ == "__main__":
    unittest.main(verbosity=1)
