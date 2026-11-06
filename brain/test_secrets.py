"""Phase 4b brain-side secret tests - FAKE pages only, never the real secrets. Run: python3 test_secrets.py"""
import os
import tempfile
import unittest

import companion_secrets as cs

PAGE = "---\ntitle: x\nvisibility: secret\nknown_by: [{who}]\ntier: {tier}\n---\n{body}\n"


class SecretIndexTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.write({1: "FAKEBODY-ONE", 2: "FAKEBODY-TWO", 3: "FAKEBODY-THREE"}, "v1")

    def tearDown(self):
        self.dir.cleanup()

    def write(self, tiers, version, who="Test Companion"):
        for f in os.listdir(self.dir.name):
            os.remove(os.path.join(self.dir.name, f))
        for t, body in tiers.items():
            with open(os.path.join(self.dir.name, f"Test-Tier-{t}.md"), "w") as f:
                f.write(PAGE.format(who=who, tier=t, body=body))
        with open(os.path.join(self.dir.name, "VERSION"), "w") as f:
            f.write(version)

    def test_gates_carry_no_text(self):
        idx = cs.SecretIndex(self.dir.name)
        self.assertTrue(idx.refresh())
        gates = idx.gates("Test Companion")
        self.assertEqual([g["tier"] for g in gates], [1, 2, 3])
        self.assertNotIn("FAKEBODY", repr(gates) + repr(idx.stats()))

    def test_body_only_for_its_companion(self):
        idx = cs.SecretIndex(self.dir.name)
        idx.refresh()
        self.assertEqual(idx.body("Test-Tier-2", "Test Companion"), "FAKEBODY-TWO")
        with self.assertRaises(KeyError):
            idx.body("Test-Tier-2", "Someone Else")

    def test_bad_set_rejected_whole_and_last_good_kept(self):
        idx = cs.SecretIndex(self.dir.name)
        idx.refresh()
        self.write({1: "FAKEBODY-ONE", 3: "FAKEBODY-THREE"}, "v2")   # tier 2 missing
        self.assertFalse(idx.refresh())
        self.assertTrue(idx.problems)
        self.assertNotIn("FAKEBODY", repr(idx.problems))
        self.assertEqual(len(idx.gates("Test Companion")), 3)

    def test_hint_never_contains_text(self):
        self.assertEqual(cs.hint_block([], False), "")
        h = cs.hint_block([{"id": "x"}], True)
        self.assertIn("reveal_secret", h)
        self.assertIn("NOT the thing itself", h)


if __name__ == "__main__":
    unittest.main(verbosity=1)
