"""Move doc §23.2 (2026-11-08): a model call that times out (e.g. mid cold load, which Ollama then cancels) starts ONE
background re-warm so the next caller finds the model loaded. Ollama is never called; urlopen is a stand-in.
Run: python3 test_rewarm.py"""
import json
import os
import sys
import tempfile
import threading
import time
import unittest
import urllib.error
from unittest import mock

_d = tempfile.TemporaryDirectory()
with open(os.path.join(_d.name, "c.json"), "w") as f:
    json.dump({"memory_db": os.path.join(_d.name, "x.db"), "secrets_dir": _d.name, "quiet_windows": [],
               "reflection_mode": "off", "lore_dir": _d.name, "keep_alive": "24h"}, f)
os.environ["AVALLONE_BRAIN_CONFIG"] = os.path.join(_d.name, "c.json")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import brain  # noqa: E402


class Fake:
    """urlopen stand-in: /api/chat fails with `chat_error`; /api/generate (the warm) is recorded and held until released."""
    def __init__(self, chat_error):
        self.chat_error, self.warms, self.release = chat_error, [], threading.Event()

    def __call__(self, req, timeout=None):
        url = req if isinstance(req, str) else req.full_url
        if url.endswith("/api/chat"):
            raise self.chat_error
        if url.endswith("/api/generate"):
            self.warms.append(json.loads(req.data))
            self.release.wait(5)

            class R:
                def __enter__(s): return s
                def __exit__(s, *a): return False
                def read(s, *a): return b"{}"
            return R()
        raise AssertionError(url)


class Rewarm(unittest.TestCase):
    def run_chat(self, fake, n=1, quiet=False):
        logs = []
        with mock.patch.object(brain.urllib.request, "urlopen", fake), \
                mock.patch.object(brain, "log", lambda **k: logs.append(k)), \
                mock.patch.object(brain, "in_quiet_window", lambda now=None: quiet):
            for _ in range(n):
                with self.assertRaises(Exception):
                    brain.ollama_chat("PERSONA", "hello", 7.75)
                with self.assertRaises(Exception):
                    brain.ollama_json("sys", "user", {"type": "object"}, 7.75, 40)   # the plan path
            time.sleep(0.2)
            fake.release.set()
            for _ in range(50):
                if not brain.REWARM_LOCK.locked():
                    break
                time.sleep(0.05)
        return logs

    def test_timeout_starts_one_warm_with_keep_alive(self):
        fake = Fake(TimeoutError("reply not complete within the budget"))
        logs = self.run_chat(fake, n=3)
        self.assertEqual(len(fake.warms), 1, fake.warms)                 # single flight across 6 timeouts
        self.assertEqual(fake.warms[0]["keep_alive"], "24h")             # never shortens the shared model's residency
        self.assertEqual(fake.warms[0]["model"], brain.CFG["model"])
        self.assertTrue(any(l.get("op") == "after_timeout" and l.get("outcome") == "ok" for l in logs), logs)

    def test_urlerror_timeout_counts(self):
        fake = Fake(urllib.error.URLError(TimeoutError("timed out")))
        self.run_chat(fake)
        self.assertEqual(len(fake.warms), 1)

    def test_other_errors_do_not_warm(self):   # positive control is the test above: same harness, warm seen
        fake = Fake(urllib.error.URLError(ConnectionRefusedError("refused")))
        self.run_chat(fake)
        self.assertEqual(fake.warms, [])

    def test_quiet_window_does_not_warm(self):
        fake = Fake(TimeoutError("x"))
        self.run_chat(fake, quiet=True)
        self.assertEqual(fake.warms, [])

    def test_lock_released_after(self):
        fake = Fake(TimeoutError("x"))
        self.run_chat(fake)
        self.assertFalse(brain.REWARM_LOCK.locked())
        fake2 = Fake(TimeoutError("x"))
        self.run_chat(fake2)
        self.assertEqual(len(fake2.warms), 1)   # a later timeout can warm again


if __name__ == "__main__":
    unittest.main(verbosity=1)
