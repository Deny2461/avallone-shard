"""Small talk pool (the owner 2026-10-30). Run: python3 test_smalltalk.py"""
import random
import unittest

import smalltalk


class SmallTalkTests(unittest.TestCase):
    def test_pick_fills_town_and_neighbour(self):
        for i in range(200):
            seed = smalltalk.pick("Yew", ["Britain", "Skara Brae"], random.Random(i))
            self.assertTrue(seed)
            self.assertNotIn("{", seed)

    def test_unknown_town_gives_nothing(self):
        self.assertEqual(smalltalk.pick("", [], random.Random(1)), "")

    def test_town_lines_used(self):
        seeds = {smalltalk.pick("Minoc", ["Vesper"], random.Random(i)) for i in range(300)}
        self.assertTrue(any("miners" in s or "tinkers" in s or "Minoc" in s for s in seeds))

    def test_action_sometimes(self):
        lines = [smalltalk.action_line(random.Random(i)) for i in range(200)]
        self.assertTrue(any(lines) and not all(lines))

    def test_block(self):
        b = smalltalk.block("wolves near Yew", random.Random(3))
        self.assertIn("wolves near Yew", b)
        self.assertIn("Do not begin with", b)


if __name__ == "__main__":
    unittest.main()
