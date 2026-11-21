"""Townsfolk step, Stage 2 - town_knowledge.py (design §7). Run: python3 -m unittest test_town_knowledge"""
import hashlib
import json
import unittest

import town_knowledge as tk
import townroster


def pid(i):
    return f"{i:08d}-4444-4444-8444-444444444444"


def held(people, town="Britain"):
    content = json.dumps({"towns": [{"map": "Felucca", "town": town, "people": people},
                                    {"map": "Felucca", "town": "Yew", "people": [person(90, "Odo", "banker", 5000, 5000)]}]}, separators=(",", ":"))
    r = townroster.Roster(None)
    ok, err = r.accept({"epoch": 1, "rev": 1, "content": content, "hash": hashlib.sha256(content.encode()).hexdigest()})
    assert ok, err
    return r.held()


def person(i, name, role, x, y, home=True):
    return {"id": pid(i), "name": name, "role": role, "sex": "man", "kind": "resident", "home": {"x": x, "y": y} if home else None}


class TownKnowledge(unittest.TestCase):
    def test_eight_directions_with_y_growing_south(self):
        for dx, dy, want in ((10, 0, "east"), (10, -10, "north-east"), (0, -10, "north"), (-10, -10, "north-west"), (-10, 0, "west"),
                             (-10, 10, "south-west"), (0, 10, "south"), (10, 10, "south-east"), (10, -3, "east"), (3, -10, "north")):
            self.assertEqual(tk.direction(dx, dy), want, (dx, dy))

    def test_nearest_of_each_kind_in_the_same_town_only(self):
        h = held([person(1, "Mady", "tailor", 1000, 1000), person(2, "Hale", "banker", 1010, 1000), person(3, "Far", "banker", 1300, 1000),
                  person(4, "Jacob", "healer", 1000, 1040), person(5, "Tess", "wandering healer", 1001, 1001), person(6, "Garrick", "blacksmith", 900, 900)])
        got = {p: (name, d, way) for p, name, role, d, way in tk.nearest(h, pid(1))}
        self.assertEqual(got["bank"], ("Hale", 10, "east"))                 # the nearer banker, never the one in Yew
        self.assertEqual(got["healer"], ("Jacob", 40, "south"))             # a wandering healer is not a place
        self.assertEqual(got["smith"], ("Garrick", 100, "north-west"))
        self.assertEqual(got["tailor"], ("Mady", 0, "own"))                 # she is the tailor (repair A5)
        self.assertNotIn("stable", got)                                     # no such place in town: no line
        text = tk.lines(h, pid(1))
        self.assertIn("the nearest bank: close by to the east (Hale is the banker there)", text)
        self.assertIn("the nearest healer: a short walk to the south", text)
        self.assertIn("the nearest smith: across town to the north-west", text)

    def test_keepers_on_one_spot_are_right_beside_you(self):
        h = held([person(1, "Mady", "tailor", 1000, 1000), person(2, "Hale", "banker", 1001, 1000)])
        self.assertIn("the nearest bank: right beside you (Hale is the banker there)", tk.lines(h, pid(1)))

    def test_a_keeper_asked_for_their_own_kind_of_place_says_it_is_theirs(self):
        h = held([person(1, "Hale", "banker", 1000, 1000), person(2, "Far", "banker", 1010, 1000), person(3, "Mady", "tailor", 1005, 1000)])
        text = tk.lines(h, pid(1))
        self.assertIn("- the nearest bank: your own, where you work - you are the banker.", text)
        self.assertNotIn("Far", text)                                       # never sent to a rival down the street
        self.assertIn("the nearest tailor: close by to the east", text)  # other kinds of place still pointed at
        alone = held([person(1, "Garrick", "blacksmith", 1000, 1000)])           # no other place in town at all
        self.assertEqual(tk.lines(alone, pid(1)).splitlines()[1], "- the nearest smith: your own, where you work - you are the blacksmith.")

    def test_no_roster_no_home_or_a_stranger_gives_nothing(self):
        h = held([person(1, "Mady", "tailor", 1000, 1000, home=False), person(2, "Hale", "banker", 1001, 1000)])
        self.assertEqual(tk.lines(h, pid(1)), "")
        self.assertEqual(tk.lines(None, pid(1)), "")
        self.assertEqual(tk.lines(h, pid(77)), "")



class How(unittest.TestCase):
    def test_the_words_for_an_offset_use_the_place_lines_bands_and_directions(self):
        import town_knowledge as tk
        self.assertEqual(tk.how(0, 0), "right beside you")
        self.assertEqual(tk.how(2, -2), "right beside you")
        self.assertEqual(tk.how(10, 0), "close by to the east")
        self.assertEqual(tk.how(0, -30), "a short walk to the north")          # the game's y grows southward
        self.assertEqual(tk.how(100, 100), "across town to the south-east")
        self.assertEqual(tk.how(-tk.NEAR, 0), "close by to the west")
        self.assertEqual(tk.how(-tk.NEAR - 1, 0), "a short walk to the west")
        self.assertEqual(tk.how(0, tk.MID + 1), "across town to the south")

if __name__ == "__main__":
    unittest.main()
