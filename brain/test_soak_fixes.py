"""Phase 4d soak fixes (2026-10-20): one test per finding. Invented data only; Ollama is never called.
Run: python3 test_soak_fixes.py"""
import json
import os
import sqlite3
import sys
import tempfile
import threading
import time
import unittest
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

_d = tempfile.TemporaryDirectory()
with open(os.path.join(_d.name, "c.json"), "w") as f:
    json.dump({"memory_db": os.path.join(_d.name, "x.db"), "secrets_dir": _d.name, "quiet_windows": [],
               "reflection_mode": "off", "lore_dir": _d.name}, f)
os.environ["AVALLONE_BRAIN_CONFIG"] = os.path.join(_d.name, "c.json")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import brain  # noqa: E402
import memory  # noqa: E402
import reflection  # noqa: E402,F401  (adds beliefs/note_persona to Ledger)

NPC, CHAR = "11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222"


def ev(seq, kind="conversation", **facts):
    return {"event_id": f"1-{seq}", "epoch": 1, "seq": seq, "npc_id": NPC, "char_id": CHAR, "kind": kind,
            "facts": facts or {"player": "hello", "reply": "well met"}, "at_utc": "2026-10-02T15:00:00Z",
            "schema": memory.SCHEMA_VERSION}


class Drip(BaseHTTPRequestHandler):
    """Headers at once, then one byte every 0.3 s: each read is quick, the whole never finishes."""
    def log_message(self, *a):
        pass

    def do_GET(self):
        self.send_response(200)
        self.end_headers()
        try:
            for _ in range(100):
                self.wfile.write(b" ")
                self.wfile.flush()
                time.sleep(0.3)
        except OSError:
            pass


class Finding1SlowDrip(unittest.TestCase):
    def test_overall_deadline(self):
        srv = ThreadingHTTPServer(("127.0.0.1", 0), Drip)
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        try:
            t = time.monotonic()
            with self.assertRaises(TimeoutError):
                with urllib.request.urlopen(f"http://127.0.0.1:{srv.server_port}/", timeout=1.0) as r:
                    brain.read_json_by(r, time.monotonic() + 1.0)
            self.assertLess(time.monotonic() - t, 2.0)   # per-read timeout alone would run ~30 s
        finally:
            srv.shutdown()

    def test_normal_body_still_parses(self):
        class R:
            def __init__(self):
                self.parts = [b'{"a": ', b'1}', b""]

            def read1(self, n):
                return self.parts.pop(0)
        self.assertEqual(brain.read_json_by(R(), time.monotonic() + 5), {"a": 1})


class Finding2ControlCharacters(unittest.TestCase):
    def test_stripped(self):
        out = brain.clean("Hello\u0000‮​\u0007 there\r\n[Kill⁦ friend﻿")
        self.assertEqual(out, "Hello there [Kill friend")

    def test_ordinary_text_unchanged(self):
        self.assertEqual(brain.clean('"Well met, traveller!  Ælfwine — café."'), "Well met, traveller! Ælfwine — café.")

    def test_cap(self):
        self.assertEqual(len(brain.clean("a" * 1000)), 240)


class Finding3ReadsDoNotWaitOnWrites(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.dir.name, "m.db")
        self.led = memory.Ledger(self.path)
        self.led.reconcile(1, 0, 0)
        self.led.ingest([ev(1)])

    def tearDown(self):
        self.dir.cleanup()

    def test_reply_reads_during_stuck_ingest(self):
        other = sqlite3.connect(self.path, timeout=1, isolation_level=None)
        other.execute("BEGIN EXCLUSIVE")   # another process holds the write lock
        result = {}

        def stuck():
            try:
                self.led.ingest([ev(2)])
            except Exception as e:
                result["err"] = e
        th = threading.Thread(target=stuck)
        th.start()
        time.sleep(0.3)   # ingest is now inside its 5 s busy wait, holding the writer lock
        t = time.monotonic()
        hist = self.led.history(NPC, CHAR)
        self.led.retrieve(NPC, CHAR, "hello")
        self.led.beliefs(NPC, CHAR)
        took = time.monotonic() - t
        write_still_stuck = th.is_alive()
        other.execute("ROLLBACK")
        th.join()
        self.assertTrue(hist)
        self.assertTrue(write_still_stuck, "control: the ingest was meant to be waiting on the lock")
        self.assertLess(took, 0.5, f"reads waited {took:.2f} s behind a stuck write")
        self.assertNotIn("err", result)   # once the lock went, the waiting ingest went through

    def test_note_persona_seen_needs_no_lock(self):
        self.led.note_persona(NPC, "elspeth")
        with self.led.lock:   # held by a writer
            th = threading.Thread(target=self.led.note_persona, args=(NPC, "elspeth"), daemon=True)
            th.start()
            th.join(0.5)
            self.assertFalse(th.is_alive(), "note_persona waited on the writer lock")

    def test_reader_cannot_write(self):
        with self.assertRaises(sqlite3.OperationalError):
            self.led.rdb.execute("DELETE FROM events")


class Finding4DiskFullErrorKept(unittest.TestCase):
    def test_full_says_full(self):
        d = tempfile.TemporaryDirectory()
        led = memory.Ledger(os.path.join(d.name, "m.db"))
        led.reconcile(1, 0, 0)
        pages = led.db.execute("PRAGMA page_count").fetchone()[0]
        led.db.execute(f"PRAGMA max_page_count = {pages + 2}")
        big = [ev(i, player="x" * 400, reply="y" * 400) for i in range(1, 200)]
        with self.assertRaises(sqlite3.Error) as cm:
            led.ingest(big)
        self.assertIn("full", str(cm.exception))
        self.assertFalse(led.db.in_transaction)
        d.cleanup()


class CodexNumBatch(unittest.TestCase):
    def check(self, value, expect):
        with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as f:
            json.dump({"num_batch": value}, f)
        old = brain.CONFIG_PATH
        try:
            brain.CONFIG_PATH = f.name
            self.assertEqual(brain.load_config()["num_batch"], expect)
        finally:
            brain.CONFIG_PATH = old
            os.unlink(f.name)

    def test_values(self):
        self.check(768, 768)
        self.check(None, None)
        for bad in (0, -5, "768", 7.5, True):
            self.check(bad, None)


if __name__ == "__main__":
    unittest.main()
