"""The brain listens on loopback only (Codex public-snapshot review 2026-11-06, finding 6). Run: python3 test_listen_lock.py"""
import unittest
from unittest import mock

import brain


class ListenLock(unittest.TestCase):
    def test_loopback_hosts_are_allowed(self):
        for h in ("127.0.0.1", "127.0.0.2", "::1", "[::1]", "localhost", "LOCALHOST"):
            self.assertTrue(brain.loopback_only(h), h)

    def test_everything_else_is_refused(self):
        for h in ("0.0.0.0", "::", "", None, "192.0.2.10", "198.51.100.7", "example.org", "127.0.0.1.example.org", "2001:db8::1"):
            self.assertFalse(brain.loopback_only(h), h)

    def test_main_refuses_before_binding(self):
        with mock.patch.dict(brain.CFG, {"listen_host": "0.0.0.0"}), \
                mock.patch.object(brain, "ThreadingHTTPServer") as server, \
                mock.patch.object(brain, "open_ledger") as ledger:
            with self.assertRaises(SystemExit):
                brain.main()
            server.assert_not_called()
            ledger.assert_not_called()


if __name__ == "__main__":
    unittest.main()
