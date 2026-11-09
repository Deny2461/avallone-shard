"""Step C (2026-11-09): ollama_json says WHY an answer was unusable - "cut_off" (ran out of tokens; Elspeth's long-evening
summaries at the old 320 limit) or "bad_json" - instead of both reading "not the JSON asked for". Ollama is faked.
Run: python3 test_unusable_reason.py"""
import json
import os
import sys
import tempfile
import unittest
from unittest import mock

_d = tempfile.TemporaryDirectory()
with open(os.path.join(_d.name, "c.json"), "w") as f:
    json.dump({"memory_db": os.path.join(_d.name, "x.db"), "secrets_dir": _d.name, "quiet_windows": [],
               "reflection_mode": "off", "lore_dir": _d.name}, f)
os.environ["AVALLONE_BRAIN_CONFIG"] = os.path.join(_d.name, "c.json")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import brain  # noqa: E402


def fake(reply):
    class R:
        def __init__(s, b): s.b = b
        def __enter__(s): return s
        def __exit__(s, *a): return False
        def read(s, *a): return s.b
    return lambda req, timeout=None: R(json.dumps(reply).encode())


class Why(unittest.TestCase):
    def call(self, reply):
        why = {}
        with mock.patch.object(brain.urllib.request, "urlopen", fake(reply)):
            out = brain.ollama_json("sys", "user", {"type": "object"}, 5, 320, why=why)
        return out, why

    def test_cut_off(self):
        out, why = self.call({"done": True, "done_reason": "length", "eval_count": 320,
                              "message": {"content": '{"beliefs": [{"topic": "x", "belief": "You'}})
        self.assertIsNone(out)
        self.assertEqual(why, {"reason": "cut_off", "tokens": 320, "limit": 320})

    def test_bad_json(self):
        out, why = self.call({"done": True, "done_reason": "stop", "message": {"content": "not json"}})
        self.assertIsNone(out)
        self.assertEqual(why, {"reason": "bad_json"})

    def test_good_answer_leaves_why_empty(self):   # control
        out, why = self.call({"done": True, "done_reason": "stop", "message": {"content": '{"beliefs": []}'}})
        self.assertEqual(out, {"beliefs": []})
        self.assertEqual(why, {})

    def test_default_limit_is_800(self):
        self.assertEqual(brain.DEFAULTS["reflection_max_tokens"], 800)


if __name__ == "__main__":
    unittest.main(verbosity=1)
