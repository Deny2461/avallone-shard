"""Layout v2 through the real request handler (Codex review round 2, 2026-10-16): a deleted character, the exact
suffix cap, recalled old words never in a system message. Invented events only; Ollama is never called."""
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
OPENER = urllib.request.build_opener()   # the client must not go through the patched urlopen


def ev(seq, kind="fight_survived", at="2026-10-02T15:00:00Z", **facts):
    return {"event_id": f"1-{seq}", "epoch": 1, "seq": seq, "npc_id": NPC, "char_id": CHAR, "kind": kind,
            "facts": facts, "at_utc": at, "schema": memory.SCHEMA_VERSION}


class Handler(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        brain.LEDGER = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        brain.LEDGER.reconcile(1, 0, 0)
        self.logs, self.sent = [], []
        self.srv = ThreadingHTTPServer(("127.0.0.1", 0), brain.Handler)
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()

    def tearDown(self):
        self.srv.shutdown()
        brain.LEDGER.db.close()
        self.dir.cleanup()

    def ask(self, utterance="tell me about the storm", situation="It is evening.", **extra):
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
                "utterance": utterance, "situation": situation, "budget_ms": 8000,
                # non-empty, or the brain restores (and then skips) the stored conversations as history
                "history": [{"speaker": "player", "text": "hi"}, {"speaker": "npc", "text": "Well met."}], **extra}
        with mock.patch.object(brain.urllib.request, "urlopen", fake), \
                mock.patch.object(brain, "log", lambda **k: self.logs.append(k)):
            r = OPENER.open(urllib.request.Request(f"http://127.0.0.1:{self.srv.server_port}/v1/npc/respond",
                                                   data=json.dumps(body).encode(),
                                                   headers={"Content-Type": "application/json"}), timeout=10)
            out = json.load(r)
        return out, next(l for l in self.logs if l.get("event") == "respond")

    def storms(self, n, said="that storm on the road was awful"):
        brain.LEDGER.ingest([ev(i + 1, "conversation", f"2026-10-0{1 + i % 8}T15:00:00Z", player_said=f"{said} {i}",
                                she_said="It was.") for i in range(n)])

    def test_deleted_character_no_memory_error(self):
        brain.LEDGER.ingest([ev(1), ev(2, kind="character_deleted")])
        out, rec = self.ask()
        self.assertTrue(out["ok"])
        self.assertFalse([l for l in self.logs if l.get("event") == "memory"], self.logs)

    def test_recalled_words_never_in_a_system_message(self):
        self.storms(2)
        out, rec = self.ask()
        msgs = self.sent[0]["messages"]
        self.assertFalse(any("storm on the road" in m["content"] for m in msgs if m["role"] == "system"))
        self.assertIn("storm on the road", json.loads(msgs[-1]["content"])["old_words_recalled"][0])
        self.assertEqual(rec["suffix_disposition"], "within")

    def suffix_of(self, msgs):
        return len(msgs[-2]["content"]) + len(msgs[-1]["content"])   # the per-turn block + the player's message

    def test_cap_is_exact_and_trims_old_words_last(self):
        self.storms(3, said="storm " * 60)
        with mock.patch.dict(brain.CFG, {"turn_suffix_chars": 1200, "turn_memory_chars": 5000}):
            out, rec = self.ask(situation="S" * 300)
        msgs = self.sent[0]["messages"]
        self.assertEqual(rec["suffix_disposition"], "trimmed")
        self.assertLessEqual(self.suffix_of(msgs) + len(brain.RETRY_NUDGE), 1200)
        self.assertEqual(rec["chars"]["suffix"], self.suffix_of(msgs))
        self.assertEqual(len(json.loads(msgs[-1]["content"])["old_words_recalled"]), 1)   # 2 recalled, 1 kept
        self.assertIn(" 1\"", json.loads(msgs[-1]["content"])["old_words_recalled"][0])   # 0 and 1 recalled (2 is in the
        # history), and the oldest went first

    def test_situation_and_line_alone_over_is_logged_not_cut(self):
        with mock.patch.dict(brain.CFG, {"turn_suffix_chars": 300}):
            out, rec = self.ask(situation="S" * 400)
        self.assertEqual(rec["suffix_disposition"], "over")
        self.assertIn("S" * 400, self.sent[0]["messages"][-2]["content"])
        self.assertEqual(self.sent[0]["messages"][-1]["content"], "tell me about the storm")

    def test_non_companions_stay_on_v1(self):
        out, rec = self.ask(priority="Ambient")
        self.assertEqual(rec.get("layout"), "v1")
        self.assertNotIn("suffix_disposition", rec)
        msgs = self.sent[0]["messages"]
        self.assertNotIn(brain.RECALL_RULE, msgs[0]["content"])
        self.assertIn("It is evening.", msgs[0]["content"])   # v1: the situation sits in the one system message


if __name__ == "__main__":
    unittest.main()
