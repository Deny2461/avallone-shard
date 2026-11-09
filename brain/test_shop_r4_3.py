"""Shop hint - Codex R4-3 (2026-11-06): a goods or money word near "how much" / "do you have any" / a price word no longer
makes a shop request; the item must be what is asked about. Also carries every earlier shop case (round 3 R3-3 from
test_round4.py and round 5b from test_round5b.py on the shelved avallone-live-port branch) so they stay covered now that
those guard test files are not part of the live port. Live Step A, 2026-11-06."""
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import shop_hint as S  # noqa: E402

R4_3_NO = ["Do you have any news about bread?", "How much do horses frighten you?", "How much do you trust a man with gold?",
           # variants of the same shape (2026-11-06)
           "How much do you love your horses?", "How much gold did you lose to the thieves?", "how much do you know about swords?",
           "do you have any stories of swords?", "do you have any word on the ale shortage?", "Did the war cost you your horses?",
           "How much do you miss the bread of your youth?", "how much gold do you have?", "how much does the gold matter to you?",
           "How much do you fear the wolves that took our horses?"]
R7_2_NO = [  # Codex R7-2 (2026-11-06): its three, then variants
    "Do you have any idea why horses are restless?", "Do you have any memories involving horses?",
    "How much is the ale shortage hurting the town?", "Do you have any fear of horses?",
    "do you have any bread left in the town after the fire burned the mill?", "How much is the bread worth to the poor?",
    "My horses cost me dearly last winter.", "Do you have any thoughts about the swords the guards carry?",
    "How much are the swords of the guards worth to them?"]
R7_2_YES = ["do you have any bread left?", "do you have any rooms free tonight?", "how much is a room for the night?",
            "how much is the bread, Nell?", "do you have any lantern oil?", "do you have any healing potions for sale?",
            "how much are the cheap swords?", "what is the price of ale here?", "is the ale priced fairly?",
            "how much for the thing I asked about first?", "how much is a pint of ale?"]
EARLIER_NO = [  # round 3 R3-3 (test_round4.py) + round 5b (test_round5b.py)
    "Do you have any fresh news?", "Do you have any other news?", "How much will you miss me?",
    "How much does that trouble you?", "What is the price of keeping a secret?", "How much are you willing to risk?",
    "How much is your life worth to you?", "what did it cost you to leave?", "Do you have any family left?",
    "What does it cost a person to forgive?", "what does it cost to forgive?", "What does it cost to forgive someone?",
    "How much does it cost a person to forgive?", "How much does it cost to forgive?",
    "what's the price of forgiveness?", "What does mercy cost a man?", "what does it cost a soul to lie?",
    "What's the price of loyalty?", "How much does forgiveness cost?", "what does it cost to be kind?",
    "What does it cost to say sorry?", "what would it cost you to trust me?", "That cost me."]
R4_3_YES = ["how much gold for a room?", "how much do you want for the sword?", "how much will you pay for these pelts?",
            "how much to stay the night?", "how much to stable my horse?", "how much are your potions?", "how much is a pint?",
            "how much for two loaves?", "how much would you charge to mend my armour?", "how much does a flask of oil cost?",
            "do you have any rope?", "do you have any good strong ale?", "how much coin for the lantern?",
            "how much do horses cost?", "how much are horses these days?", "what is the cost of a room?",
            "is the ale priced fairly?", "how much do you charge?", "how much silver is that?"]
EARLIER_YES = [
    "do you have any fresh bread?", "how much is that?", "what does it cost?", "What are your prices?",
    "how much would a room be?", "how much?", "How much will it cost?", "I don't want to buy; I want to sell",
    "I'm not buying today, but I'd sell you my pelts", "show me your wares", "what's the price of bread",
    "what does the bread cost?", "how much does a room cost?", "what's the price of ale?",
    "what does that cost?", "what does that one cost?", "how much would it cost me?",
    "what does it cost here, Nell?", "what's the price of that?", "what does it cost to stay the night?",
    "what does it cost to stay?", "what does it cost per night?", "it costs how much?",
    "I'd like to know the price of bread.", "I want to buy something", "what's on the menu?"]


class R4_3(unittest.TestCase):
    def test_r4_3_negatives(self):
        for u in R4_3_NO:
            self.assertFalse(S.asks_to_trade(u), u)

    def test_earlier_negatives_still_hold(self):
        for u in EARLIER_NO:
            self.assertFalse(S.asks_to_trade(u), u)

    def test_r4_3_positives(self):
        for u in R4_3_YES:
            self.assertTrue(S.asks_to_trade(u), u)

    def test_r7_2_negatives(self):
        for u in R7_2_NO:
            self.assertFalse(S.asks_to_trade(u), u)

    def test_r7_2_positives(self):
        for u in R7_2_YES:
            self.assertTrue(S.asks_to_trade(u), u)

    def test_earlier_positives_still_fire(self):
        for u in EARLIER_YES:
            self.assertTrue(S.asks_to_trade(u), u)



# Step B (2026-11-08): "a room" / "have you" shapes. Positives must fire; look-alikes must not (same R7-2 rule: the item
# ends the question or only shop words follow; a linking word after the item means it is not what is asked for).
ROOM_YES = ["do you have a room?", "Do you have a room for the night?", "have you a room for the night?",
            "Have you a room, Mirabel?", "have you got any ale?", "do you have a spare bed tonight?",
            "Have ye any bread left?", "do you have a cup of tea?", "do you have two rooms for us?"]
ROOM_NO = ["Do you have a moment?", "Do you have a map of the caves where the bandits hide?", "have you a family?",
           "Do you have a room where we can talk in private about the murders?", "have you any news of the war?",
           "Do you have a sword you would trust with your life?", "have you any idea why the ale tastes off?",
           "Do you have a horse that you love?", "have you got a minute?", "do you have a name?"]


class RoomShapes(unittest.TestCase):
    def test_room_positives(self):
        for u in ROOM_YES:
            self.assertTrue(S.asks_to_trade(u), u)

    def test_room_negatives(self):
        for u in ROOM_NO:
            self.assertFalse(S.asks_to_trade(u), u)


if __name__ == "__main__":
    unittest.main()
