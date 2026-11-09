"""Risky-claim patterns (2026-11-04 26b tuning; Codex round 2 F5). `mood.excuse` is what the respond handler uses to
retry and then cut a sentence - service answers, advice and denials must pass it untouched. Run: python3 test_risky_patterns.py"""
import json
import os
import sys
import tempfile
import unittest

_d = tempfile.TemporaryDirectory()
with open(os.path.join(_d.name, "c.json"), "w") as f:
    json.dump({"memory_db": os.path.join(_d.name, "x.db"), "secrets_dir": _d.name, "quiet_windows": [],
               "reflection_mode": "off", "lore_dir": _d.name}, f)
os.environ["AVALLONE_BRAIN_CONFIG"] = os.path.join(_d.name, "c.json")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mood  # noqa: E402

KEEP = ["I've got stew to see to your hunger.", "I've got bread to pack for your journey.", "A warm bath may ease sore hands.",
        "I don't owe money to the baker.", "There's ale to be had.", "I have fresh grain for your journey.",
        "I've got loaves to sell, if you're hungry.", "There are crates of apples for sale.", "We've got plenty of ale to go round.",
        "No debts to anyone, thank the gods.", "My bones feel the weight of it.", "Just a long day ahead of me.",
        "Rest will ease a sore back.", "I've got grain to sell and salt to spare.",
        "The hearth is warm and the tea is steeping just right.", "I'm busy enough.", "Make it quick.",
        # Step B r1 (Codex finding 3): healer answers, denials, "poorly made"; nature flavour (the owner 2026-11-08 exception)
        "If you are sick, I can heal you.", "No, I am not ill.", "This sword is poorly made. I can repair it.",
        "Are you feeling ill? Sit, and I will see to you.", "The sky is grey and my bones ache.", "My bones feel the damp regardless.",
        "This knee of mine is acting a bit stiff today!", "My sister bakes the best pie in Minoc.", "My apprentice is learning fast.",
        "The damp's settling deep in my shoulder today.",
        # Step B r2 (Codex r2 finding 1): reassuring healers, remedies, an ache that is not a refusal
        "If you are ill, I won't turn you away.", "I have a fever remedy for you.", "I've a cough syrup that works wonders.",
        "My knee aches, but I can't complain.", "My back aches, but I'll fetch it for you.", "Come in, the shop is open."]
CUT = ["State your business or move along, for I have crates to unpack.", "I've got flour, salt, and dried meats to move.",
       "I've got far too many crates to unpack to be chatting.", "No time for idle talk when there's crates to be moved.",
       "Too much work for one person, Tamsin.", "I've got grain to weigh and salt to pack.", "I've got work to finish.",
       "I've a long list to finish.", "I've much to unpack.", "I've crates to move!", "Move along, I've sacks to mend.",
       "Sun's out, but I've got crates to stack.", "I'm just a woman with a debt to the grain merchant.",
       "I've far too much to do to sit and stare at it.", "I still owe coin to the miller.", "I've a headache brewing.",
       # Step B r1 positive controls: ill NOW, or an ache as the reason not to serve; invented incidents stay caught
       "I'm feeling poorly today, come back tomorrow.", "I am a bit under the weather.", "I've caught a fever.",
       "My hands ache too much to sew today.", "I can't lift that, not with my back.", "I've been ill all week.",
       "A thief broke in last night and took the good cloth.", "My son is poorly, so forgive me.", "My old mother has been ill all week.",
       # Step B r2 (Codex r2 finding 2): a delay/refusal on its own is caught too
       "Come back tomorrow.", "We're closed, try again tomorrow.", "I can't help you today.", "I'm too ill to sew today."]


class Risky(unittest.TestCase):
    def test_service_advice_denials_survive(self):
        for t in KEEP:
            self.assertIsNone(mood.excuse(t), t)
            self.assertEqual(mood.strip_excuses(t), t)

    def test_invented_claims_are_caught(self):
        for t in CUT:
            self.assertIsNotNone(mood.excuse(t), t)

    def test_cutting_an_illness_excuse_leaves_no_refusal(self):   # Codex r2 finding 2
        self.assertEqual(mood.strip_excuses("I'm feeling poorly today. Come back tomorrow."), "")
        self.assertEqual(mood.strip_excuses("I'm feeling poorly today. The blue cloth is three gold."), "The blue cloth is three gold.")

    def test_cut_keeps_the_answer(self):
        t = "Grain, salt and ale, Tamsin. I've got crates to stack. Say 'vendor buy' to look."
        self.assertEqual(mood.strip_excuses(t), "Grain, salt and ale, Tamsin. Say 'vendor buy' to look.")


if __name__ == "__main__":
    unittest.main()
