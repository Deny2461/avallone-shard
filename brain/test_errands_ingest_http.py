"""Errands build 1, stage 2: the brain's ingest endpoint with errand events - the strict transport boundary (design 11.1),
the errands capability on both replies, and the set-aside list read from the receipts. Real handler, temp database, no
model. Run: python3 test_errands_ingest_http.py"""
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
               "prompt_layout": "v2", "reflection_mode": "off", "lore_dir": _d.name}, f)
os.environ["AVALLONE_BRAIN_CONFIG"] = os.path.join(_d.name, "c.json")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import brain  # noqa: E402
import memory  # noqa: E402
from test_errands_ledger import done, other  # noqa: E402


class Endpoint(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.logs = []
        self.srv = ThreadingHTTPServer(("127.0.0.1", 0), brain.Handler)
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        for p in (mock.patch.object(brain, "LEDGER", self.led),
                  mock.patch.object(brain, "log", lambda **k: self.logs.append(k))):
            p.start()
            self.addCleanup(p.stop)

    def tearDown(self):
        self.srv.shutdown()
        self.srv.server_close()
        self.led.db.close()
        self.led.rdb.close()
        self.dir.cleanup()

    def post(self, path, body):
        raw = body if isinstance(body, bytes) else json.dumps(body).encode()
        req = urllib.request.Request(f"http://127.0.0.1:{self.srv.server_address[1]}{path}", data=raw,
                                     headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=10) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read())

    def test_both_replies_carry_the_capability(self):
        code, rec = self.post("/v1/memory/reconcile", {"new_epoch": 8, "saved_epoch": 7, "saved_seq": 0})
        self.assertEqual((code, rec["errands"], rec["rumour_heard"]), (200, 1, 1))
        code, ing = self.post("/v1/memory/events", {"events": [other(8, 1)]})
        self.assertEqual((code, ing["errands"], ing["errand_set_aside"], ing["acked"]), (200, 1, [], ["8-1"]))

    def test_set_aside_is_reported_and_reported_again_on_a_resend(self):
        self.post("/v1/memory/reconcile", {"new_epoch": 8, "saved_epoch": 7, "saved_seq": 0})
        batch = {"events": [done(8, 1), done(8, 2, facts={"v": 2}), other(8, 3)]}
        for _ in range(2):
            code, ing = self.post("/v1/memory/events", batch)
            self.assertEqual((code, ing["acked"], ing["rejected"], ing["errand_set_aside"]),
                             (200, ["8-1", "8-2", "8-3"], [], ["8-2"]))
        self.assertTrue(any(k.get("outcome") == "errand_set_aside" for k in self.logs))

    def test_a_conflict_comes_back_rejected(self):
        self.post("/v1/memory/reconcile", {"new_epoch": 8, "saved_epoch": 7, "saved_seq": 0})
        self.post("/v1/memory/events", {"events": [done(8, 1)]})
        code, ing = self.post("/v1/memory/events", {"events": [done(8, 1, facts={"receiver_town": "Yew"})]})
        self.assertEqual((code, ing["acked"], ing["rejected"], ing["errand_set_aside"]), (200, [], ["8-1"], []))

    def test_an_errand_batch_outside_the_protocol_fails_whole_and_writes_nothing(self):
        self.post("/v1/memory/reconcile", {"new_epoch": 8, "saved_epoch": 7, "saved_seq": 0})
        good = json.dumps(done(8, 2))
        for raw in ('{"events":[' + json.dumps(other(8, 1)) + "," + good[:-1] + ',"kind":"errand_done"}]}',
                    '{"events":[' + good.replace('"played": 86400', '"played": NaN') + "]}",
                    '{"events":[' + good.replace('"Vesper"', '"Ves\\ud800per"') + "]}"):
            code, reply = self.post("/v1/memory/events", raw.encode())
            self.assertEqual((code, reply["ok"]), (400, False), raw[:60])
        self.assertEqual(self.led.db.execute("SELECT COUNT(*) FROM events").fetchone()[0], 0)
        self.assertEqual(self.led.db.execute("SELECT COUNT(*) FROM errand_receipt").fetchone()[0], 0)
        self.assertTrue(any(k.get("outcome") == "errand_transport_refused" for k in self.logs))

    def test_an_ordinary_batch_is_read_as_before(self):
        self.post("/v1/memory/reconcile", {"new_epoch": 8, "saved_epoch": 7, "saved_seq": 0})
        raw = '{"events":[' + json.dumps(other(8, 1))[:-1] + ',"kind":"joined"}]}'      # a duplicate key, no errand
        code, ing = self.post("/v1/memory/events", raw.encode())
        self.assertEqual((code, ing["acked"]), (200, ["8-1"]))


if __name__ == "__main__":
    unittest.main()
