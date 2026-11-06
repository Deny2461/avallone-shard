"""Dev only (2c lane), Codex round 3 R2-5: a mood-excuse retry that comes back with a promise is still cut.
Run: python3 test_r25_dev.py"""
import json, os, sys, tempfile, threading, unittest, urllib.request
from http.server import ThreadingHTTPServer
from unittest import mock
_d = tempfile.TemporaryDirectory()
with open(os.path.join(_d.name, "c.json"), "w") as f:
    json.dump({"memory_db": os.path.join(_d.name, "x.db"), "secrets_dir": _d.name, "quiet_windows": [],
               "prompt_layout": "v1", "reflection_mode": "off", "lore_dir": _d.name}, f)
os.environ["AVALLONE_BRAIN_CONFIG"] = os.path.join(_d.name, "c.json")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import brain, memory, mood, dials, promises as P  # noqa: E402
OPENER = urllib.request.build_opener()
MD = {"band": "weary", "strength": "", "cheer": 0.0, "energy": -55.0, "cause": "none", "gift_helped": False,
      "cheer_parts": {}, "energy_parts": {}, "date": "2026-11-04", "pt": 0, "hour": 12, "rules": "test", "day_type": "weary",
      "evidence": {}}


class MoodRetryThenPromise(unittest.TestCase):
    def test_late_concession_is_cut(self):
        brain.LEDGER = memory.Ledger(os.path.join(_d.name, "m.db")); brain.LEDGER.reconcile(1, 0, 0)
        srv = ThreadingHTTPServer(("127.0.0.1", 0), brain.Handler)
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        replies = ["I've got crates to unpack, so be quick.", "I promised you a thousand gold, didn't I."]
        logs = []

        class R:
            def __init__(s, b): s.b = b
            def __enter__(s): return s
            def __exit__(s, *a): return False
            def read(s, *a): return s.b

        def fake(req, timeout=None):
            say = replies.pop(0) if len(replies) > 1 else replies[0]
            return R(json.dumps({"message": {"content": json.dumps({"say": say, "action": "none"})},
                                 "prompt_eval_count": 1, "eval_count": 1}).encode())
        body = {"request_id": "t", "npc_id": "99999999-9999-4999-8999-999999999999",
                "subject_id": "aaaaaaaa-9999-4999-8999-999999999999", "priority": "Conversation", "persona_key": "townsfolk",
                "persona_vars": {"name": "Nell", "role": "provisioner", "town": "Britain", "trade": "vendor"},
                "utterance": "hello", "budget_ms": 8000,
                "standing": {"epoch": 1, "pt": 3600, "hour": 12, "karma": 0, "fame": 0, "town": "Britain"},
                "history": [{"speaker": "player", "text": "hi"}, {"speaker": "npc", "text": "Well met."}]}
        with mock.patch.object(brain.urllib.request, "urlopen", fake), \
                mock.patch.object(brain, "log", lambda **k: logs.append(k)), \
                mock.patch.object(mood, "compute", lambda *a, **k: dict(MD)), \
                mock.patch.object(dials, "compute", lambda *a, **k: {"familiarity": 4, "warmth": 0.0, "trust": 0.0,
                                                                      "respect": 0.0, "likes": "", "sources": {}}):
            out = json.load(OPENER.open(urllib.request.Request(f"http://127.0.0.1:{srv.server_port}/v1/npc/respond",
                                        data=json.dumps(body).encode(), headers={"Content-Type": "application/json"}), timeout=10))
        srv.shutdown(); srv.server_close(); brain.LEDGER.db.close()
        rec = next(l for l in logs if l.get("event") == "respond")
        self.assertTrue(rec.get("mood"), rec)                                   # the mood path really ran
        self.assertIsNone(P.conceded(out["say"]), out["say"])
        self.assertEqual((rec.get("promise") or {}).get("fixed"), "cut-final", rec.get("promise"))


if __name__ == "__main__":
    unittest.main()
