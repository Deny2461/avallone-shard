"""The brain listens on loopback only (Codex public-snapshot review 2026-11-06, finding 6; round 2 P3: the
IPv6 and localhost forms must actually bind). Run: python3 test_listen_lock.py"""
import socket
import unittest
from unittest import mock

import brain


class ListenLock(unittest.TestCase):
    def test_loopback_hosts_are_allowed(self):
        for h in ("127.0.0.1", "127.0.0.2", "::1", "[::1]", "localhost", "LOCALHOST"):
            self.assertTrue(brain.loopback_only(h), h)

    def test_everything_else_is_refused(self):
        for h in ("0.0.0.0", "::", "", None, "192.0.2.10", "198.51.100.7", "example.org", "127.0.0.1.example.org",
                  "2001:db8::1", "[2001:db8::1]", "::ffff:192.0.2.10", "localhost.example.org"):
            self.assertFalse(brain.loopback_only(h), h)

    def test_normal_forms(self):
        self.assertEqual(brain.loopback_bind_address("localhost"), "127.0.0.1")
        self.assertEqual(brain.loopback_bind_address("[::1]"), "::1")
        self.assertEqual(brain.loopback_bind_address("0:0:0:0:0:0:0:1"), "::1")

    def test_every_allowed_form_really_binds_on_loopback(self):
        for h, family, want in (("127.0.0.1", socket.AF_INET, "127.0.0.1"), ("localhost", socket.AF_INET, "127.0.0.1"),
                                ("::1", socket.AF_INET6, "::1"), ("[::1]", socket.AF_INET6, "::1")):
            server = brain.make_server(h, 0)
            try:
                self.assertEqual(server.socket.family, family, h)
                self.assertEqual(server.server_address[0], want, h)
            finally:
                server.server_close()

    def test_make_server_refuses_non_loopback(self):
        for h in ("0.0.0.0", "::", "192.0.2.10"):
            with self.assertRaises(ValueError):
                brain.make_server(h, 0)

    def test_main_refuses_before_binding(self):
        with mock.patch.dict(brain.CFG, {"listen_host": "0.0.0.0"}), \
                mock.patch.object(brain, "make_server") as server, \
                mock.patch.object(brain, "open_ledger") as ledger:
            with self.assertRaises(SystemExit):
                brain.main()
            server.assert_not_called()
            ledger.assert_not_called()


if __name__ == "__main__":
    unittest.main()
