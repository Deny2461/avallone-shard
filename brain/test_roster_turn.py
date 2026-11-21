"""Townsfolk step, Stage 1 - every prompt request's roster stamp is checked against the held roster and logged
(design §4.5; nothing is shown from the roster in Stage 1). Run: python3 -m unittest test_roster_turn"""
import hashlib
import json
import unittest

import brain
import townroster


def doc(names):
    people = [{"id": f"{i:08d}-1111-4111-8111-111111111111", "name": n, "role": "tailor", "sex": "woman", "kind": "resident",
               "home": {"x": 1, "y": 2}} for i, n in enumerate(names)]
    content = json.dumps({"towns": [{"map": "Felucca", "town": "Britain", "people": people}]}, separators=(",", ":"))
    return {"epoch": 1, "rev": 1, "content": content, "hash": hashlib.sha256(content.encode()).hexdigest()}


class RosterTurnTests(unittest.TestCase):
    def setUp(self):
        self.logs = []
        self._log, self._roster = brain.log, brain.ROSTER
        brain.log = lambda **kw: self.logs.append(kw)
        brain.ROSTER = townroster.Roster(None)

    def tearDown(self):
        brain.log, brain.ROSTER = self._log, self._roster

    def turns(self):
        return [l for l in self.logs if l.get("op") == "turn"]

    def test_a_turn_with_the_held_hash_matches_and_any_other_stamp_does_not(self):
        held, other = doc(["Mady"]), doc(["Mady", "Garrick"])
        brain.ROSTER.accept(held)
        for stamp, want in ((held["hash"], True), (other["hash"], False), ("none", False), (None, False), (7, False)):
            brain.note_roster_stamp({"roster_stamp": stamp}, "/v1/npc/respond")
            self.assertEqual(self.turns()[-1]["match"], want, stamp)
        self.assertEqual(self.turns()[0]["path"], "/v1/npc/respond")

    def test_a_request_without_the_field_or_a_brain_without_the_roster_logs_nothing(self):
        brain.note_roster_stamp({"utterance": "hello"}, "/v1/npc/respond")     # an older shard, or the shard's switch off
        brain.note_roster_stamp(["not", "a", "dict"], "/v1/npc/plan")
        brain.ROSTER = None                                                     # this brain's roster setting is off
        brain.note_roster_stamp({"roster_stamp": "0" * 64}, "/v1/npc/respond")
        self.assertEqual(self.turns(), [])


if __name__ == "__main__":
    unittest.main()
