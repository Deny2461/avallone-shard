"""Step C / Codex GH-Pub R6 P2 (2026-11-09): a long MONEY-ONLY tail must not make the history restore or the recall skip set
decode the pair's whole history. Both loops look at no more than memory.HISTORY_SCAN_ROWS newest conversation rows.
Checks: the work bound (counted calls to conversation_money) for history(), retrieve(), and one real /v1/npc/respond
request; the debt never comes back; and the positive control - with a short money tail the clean exchanges behind it
are still restored and recalled, exactly as before the cap. Ollama is never called. Run: python3 test_moneytail.py"""
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
DEBT_P, DEBT_N = "You still owe me fifty gold for those wolf pelts.", "Aye, I owe you that. I'll pay you back tomorrow."
WATCH_P, WATCH_N = "Remember you swore to keep watch by the wolves at Yew?", "Aye, I keep my word on the watch."
DEBT_MARKS = ("fifty", "pelts", "owe", "pay you back")
CAP = memory.HISTORY_SCAN_ROWS
TAIL = 3000                     # money-only rows, newest; far past the cap


def ev(seq, **facts):
    return {"event_id": f"1-{seq}", "epoch": 1, "seq": seq, "npc_id": NPC, "char_id": CHAR, "kind": "conversation",
            "facts": facts, "at_utc": "2026-10-02T15:00:00Z", "schema": memory.SCHEMA_VERSION}


def ledger(clean_prefix, tail):
    d = tempfile.TemporaryDirectory()
    led = memory.Ledger(os.path.join(d.name, "m.db"))
    led.reconcile(1, 0, 0)
    evs = [ev(1 + i, player_said=WATCH_P, she_said=WATCH_N) for i in range(clean_prefix)]
    evs += [ev(100 + i, player_said=DEBT_P, she_said=DEBT_N) for i in range(tail)]
    for i in range(0, len(evs), 200):        # ingest takes at most 200 events per call
        led.ingest(evs[i:i + 200])
    return d, led


class Counting:
    def __init__(self):
        self.n, self.real = 0, memory.conversation_money

    def __call__(self, f):
        self.n += 1
        return self.real(f)


class Bound(unittest.TestCase):
    def run_pair(self, clean_prefix, tail):
        d, led = ledger(clean_prefix, tail)
        try:
            c = Counting()
            with mock.patch.object(memory, "conversation_money", c):
                hist = led.history(NPC, CHAR, pairs=4)                                   # the handler's order:
                recs = led.retrieve(NPC, CHAR, "the wolves at Yew and the fifty gold",   # restore, then recall
                                    records=True, skip_conversations=len(hist) // 2)
            return c.n, json.dumps(hist), " ".join(r["line"] for r in recs)
        finally:
            led.db.close()
            d.cleanup()

    def test_long_money_tail_with_clean_prefix_is_bounded(self):
        n, hist, text = self.run_pair(4, TAIL)
        self.assertLessEqual(n, 2 * CAP + 10, n)          # was 2 x 3,004 before the cap (Codex: 2 x 100,004 at 100k)
        for m in DEBT_MARKS:
            self.assertNotIn(m, hist)
            self.assertNotIn(m, text)

    def test_long_money_tail_without_clean_prefix_is_bounded(self):
        n, hist, text = self.run_pair(0, TAIL)
        self.assertLessEqual(n, 2 * CAP + 10, n)
        self.assertEqual(json.loads(hist), [])             # nothing clean to restore, and nothing money restored
        for m in DEBT_MARKS:
            self.assertNotIn(m, text)

    def test_short_money_tail_restores_clean_exchanges_as_before(self):   # positive control: behaviour under the cap
        n, hist, _ = self.run_pair(4, 3)
        self.assertEqual(hist.count("keep watch"), 4)      # all four clean exchanges restored behind the 3 money rows
        for m in DEBT_MARKS:
            self.assertNotIn(m, hist)
        self.assertLess(n, 30)

    def test_cap_is_what_bounds_it(self):                  # control for the control: without the cap the count is huge
        with mock.patch.object(memory, "HISTORY_SCAN_ROWS", 10 ** 9):
            n, _, _ = self.run_pair(4, TAIL)
        self.assertGreater(n, 2 * TAIL)


class Handler(unittest.TestCase):
    """The complete request path: one /v1/npc/respond with no shard history (the restore runs), long money tail."""
    def setUp(self):
        self.d, brain.LEDGER = ledger(4, TAIL)
        self.srv = ThreadingHTTPServer(("127.0.0.1", 0), brain.Handler)
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()

    def tearDown(self):
        self.srv.shutdown()
        brain.LEDGER.db.close()
        self.d.cleanup()

    def test_one_reply_is_bounded_and_clean(self):
        sent = []

        class R:
            def __init__(s, b): s.b = b
            def __enter__(s): return s
            def __exit__(s, *a): return False
            def read(s, *a): return s.b

        def fake(req, timeout=None):
            sent.append(json.loads(req.data))
            return R(json.dumps({"message": {"content": json.dumps({"say": "Aye.", "action": "none"})},
                                 "prompt_eval_count": 1, "eval_count": 1}).encode())
        body = {"request_id": "t", "npc_id": NPC, "subject_id": CHAR, "priority": "Companion", "persona": "PERSONA",
                "utterance": "Do you remember the wolves at Yew?", "situation": "It is evening.", "budget_ms": 8000,
                "history": []}
        c = Counting()
        with mock.patch.object(brain.urllib.request, "urlopen", fake), mock.patch.object(brain, "log", lambda **k: None), \
                mock.patch.object(memory, "conversation_money", c):
            r = urllib.request.build_opener().open(urllib.request.Request(
                f"http://127.0.0.1:{self.srv.server_port}/v1/npc/respond", data=json.dumps(body).encode(),
                headers={"Content-Type": "application/json"}), timeout=20)
            self.assertTrue(json.load(r)["ok"])
        self.assertLessEqual(c.n, 3 * CAP + 20, c.n)      # restore + skip set + per-candidate refusals, all bounded
        prompt = json.dumps(sent[-1]["messages"])
        for m in DEBT_MARKS:
            self.assertNotIn(m, prompt)


if __name__ == "__main__":
    unittest.main(verbosity=1)
