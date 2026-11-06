"""One retry on a garbled model reply (the owner's dev test 2026-11-02 19:23:53 UTC; the owner). Ollama is never called.
Run: python3 test_garbled.py"""
import io
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

PERSONA = "You are Nell, a baker in Britain. Plain-spoken and kind."


def ollama_body(content):
    body = {"message": {"content": content}, "prompt_eval_count": 9, "eval_count": 3}

    class R(io.BytesIO):
        def __enter__(self): return self
        def __exit__(self, *a): return False
    return lambda *a, **k: R(json.dumps(body).encode())


class Parse(unittest.TestCase):
    def test_garbled_and_non_object_replies_are_garbled(self):
        for content in ('{"say": "Well met', '["Well met"]', "Well met"):
            with mock.patch.object(brain.urllib.request, "urlopen", ollama_body(content)):
                with self.assertRaises(brain.GarbledReply):
                    brain.ollama_chat(PERSONA, "hello", 5)

    def test_a_good_reply_still_parses(self):
        with mock.patch.object(brain.urllib.request, "urlopen", ollama_body(json.dumps({"say": "Well met."}))):
            self.assertEqual(brain.ollama_chat(PERSONA, "hello", 5)[0], "Well met.")


class Respond(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.srv = ThreadingHTTPServer(("127.0.0.1", 0), brain.Handler)
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()

    def ask(self, budget_ms=8000):
        body = {"request_id": "garbled-test", "npc_id": "33333333-3333-4333-8333-333333333333", "persona": PERSONA,
                "utterance": "hello", "budget_ms": budget_ms}
        req = urllib.request.Request(f"http://127.0.0.1:{self.srv.server_address[1]}/v1/npc/respond",
                                     json.dumps(body).encode(), {"Content-Type": "application/json"})
        return json.loads(urllib.request.urlopen(req, timeout=20).read())

    def test_one_garbled_reply_is_tried_again(self):
        calls = []

        def fake(*a, **k):
            calls.append(1)
            if len(calls) == 1:
                raise brain.GarbledReply("JSONDecodeError: Unterminated string")
            return "Well met, friend.", "none"
        with mock.patch.object(brain, "ollama_chat", fake):
            r = self.ask()
        self.assertEqual(len(calls), 2)
        self.assertEqual(r.get("say"), "Well met, friend.")

    def test_two_garbled_replies_give_up_after_one_retry(self):
        calls = []

        def fake(*a, **k):
            calls.append(1)
            raise brain.GarbledReply("JSONDecodeError: Unterminated string")
        with mock.patch.object(brain, "ollama_chat", fake):
            r = self.ask()
        self.assertEqual(len(calls), 2)          # one retry, never a third call
        self.assertNotEqual(r.get("outcome"), "ok")


if __name__ == "__main__":
    unittest.main()
