"""Step B (2026-11-08): repeat catcher. Run: python3 test_variety.py"""
import json
import os
import sys
import tempfile
import threading
import unittest
import urllib.request
from http.server import ThreadingHTTPServer
from unittest import mock

_d = tempfile.TemporaryDirectory()
with open(os.path.join(_d.name, "c.json"), "w") as f:
    json.dump({"memory_db": os.path.join(_d.name, "x.db"), "lore_dir": _d.name, "secrets_dir": _d.name,
               "quiet_windows": [], "reflection_mode": "off"}, f)
os.environ["AVALLONE_BRAIN_CONFIG"] = os.path.join(_d.name, "c.json")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import variety  # noqa: E402
import brain  # noqa: E402

PLAY = [  # real NPC replies from a play test on 2026-11-08 (taken from the brain log)
    ("adoncia", "The sun is warm on the shop window and my needles are sharp. It is a fine day for steady work."),
    ("mansi", "The threads are running smooth and the sun is warm on the shop floor. I have little to complain of."),
    ("cody", "The sun is high and the tides are steady. I have no complaints today."),
    ("caine", "The sun is high and the stone is steady under my chisel. It is a fine day for building."),
]


class Variety(unittest.TestCase):
    def setUp(self):
        variety.reset_for_tests()

    def test_same_opening_from_another_npc_is_caught(self):
        variety.note("adoncia", "p", PLAY[0][1], now=0)
        self.assertEqual(variety.check("cody", "p", PLAY[2][1], now=60), {"kind": "opening", "words": "the sun is"})

    def test_a_fresh_opening_passes(self):   # positive control for the test above: same rig, different words
        variety.note("adoncia", "p", PLAY[0][1], now=0)
        self.assertIsNone(variety.check("mansi", "p", "Well enough, thank you. Business has been steady.", now=60))

    def test_tano_says_the_same_thing_twice(self):
        variety.note("tano", "racer", "Nothing worth repeating. I have work to do.", now=0)
        hit = variety.check("tano", "racer", "The hearth is warm and the tinctures are settling well. I have work to do.",
                            now=600)
        self.assertEqual(hit, {"kind": "phrase", "words": "have work to do"})

    def test_phrase_repeat_is_per_npc_and_player(self):
        variety.note("tano", "racer", "Nothing worth repeating. I have work to do.", now=0)
        self.assertIsNone(variety.check("tano", "elspeth", "Mind the steps. I have work to do.", now=60))
        self.assertIsNone(variety.check("winda", "racer", "Mind the steps. I have work to do.", now=60))

    def test_old_replies_are_forgotten(self):
        variety.note("adoncia", "p", PLAY[0][1], now=0)
        self.assertIsNone(variety.check("cody", "p", PLAY[2][1], now=variety.WINDOW_S + 1))

    def test_common_phrases_and_shop_line_do_not_count(self):
        variety.note("tano", "racer", "What would you like? Say vendor buy to see my stock.", now=0)
        self.assertIsNone(variety.check("tano", "racer", "Good to see you. Say vendor buy to see my stock.", now=60))
        self.assertIsNone(variety.check("tano", "racer", "And what would you like from me then?", now=60))

    def test_bracketed_action_is_not_the_opening(self):
        variety.note("winda", "p", "[wipes hands] The light is pleasant on the docks.", now=0)
        self.assertEqual(variety.check("cody", "p", "The light is fading now.", now=5)["kind"], "opening")

    def test_nudge_names_the_words(self):
        self.assertIn('"the sun is"', variety.nudge({"kind": "opening", "words": "the sun is"}))
        self.assertIn('"have work to do"', variety.nudge({"kind": "phrase", "words": "have work to do"}))



class ThroughTheHandler(unittest.TestCase):
    """The real /v1/npc/respond handler with a stand-in model (Ollama is never called)."""
    @classmethod
    def setUpClass(cls):
        cls.srv = ThreadingHTTPServer(("127.0.0.1", 0), brain.Handler)
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()

    def setUp(self):
        variety.reset_for_tests()

    def ask(self, npc, n):
        body = {"request_id": f"variety-{npc[:4]}-{n}", "npc_id": npc, "persona": "You are a baker in Britain.",
                "utterance": "how are you today?", "budget_ms": 8000}
        req = urllib.request.Request(f"http://127.0.0.1:{self.srv.server_address[1]}/v1/npc/respond",
                                     json.dumps(body).encode(), {"Content-Type": "application/json"})
        return json.loads(urllib.request.urlopen(req, timeout=20).read())

    def run_with(self, replies):
        calls = []

        def fake(*a, **k):
            calls.append(k.get("nudge"))
            return replies[min(len(calls) - 1, len(replies) - 1)], "none"
        with mock.patch.object(brain, "ollama_chat", fake):
            first = self.ask("11111111-1111-4111-8111-111111111111", 1)
            second = self.ask("22222222-2222-4222-8222-222222222222", 2)
        return calls, first, second

    def test_second_npc_same_opening_is_asked_again(self):
        calls, first, second = self.run_with(["The sun is warm on the window.", "The sun is high today.",
                                              "Well enough, and the oven is hot."])
        self.assertEqual(first["say"], "The sun is warm on the window.")
        self.assertEqual(len(calls), 3)                         # one call, then one call + one retry
        self.assertIn('"the sun is"', calls[2])
        self.assertEqual(second["say"], "Well enough, and the oven is hot.")

    def test_retry_that_repeats_too_keeps_the_first_reply(self):
        calls, first, second = self.run_with(["The sun is warm on the window.", "The sun is high today."])
        self.assertEqual(len(calls), 3)                         # never a third model call for one reply
        self.assertEqual(second["say"], "The sun is high today.")

    def test_no_repeat_no_retry(self):   # positive control for the retry count above
        calls, first, second = self.run_with(["The sun is warm on the window.", "Well enough, thank you."])
        self.assertEqual(len(calls), 2)
        self.assertEqual(second["say"], "Well enough, thank you.")


class SharedStockPhrase(unittest.TestCase):
    """Step C (live log 2026-11-08): four NPCs said "State your business" / "State your ailment" mid-sentence."""
    def setUp(self):
        variety.reset_for_tests()

    def test_second_npc_using_a_stock_phrase_is_caught(self):
        variety.note("smith", "p", "[wipes hands] Hail. State your business, I have iron to work.", now=0)
        hit = variety.check("tanner", "q", "Greetings. If you want leather, state your business.", now=60)
        self.assertEqual(hit["kind"], "shared")
        self.assertIn("state your business", hit["all"])
        self.assertIn('"state your business"', variety.nudge(hit))

    def test_same_npc_alone_or_old_use_is_not_shared(self):
        variety.note("smith", "p", "State your business, traveller.", now=0)
        self.assertIsNone(variety.check("smith", "q", "Speak up and state your business.", now=60))   # same NPC
        self.assertIsNone(variety.check("tanner", "q", "Well, state your business then.", now=60 + variety.WINDOW_S + 1))

    def test_town_names_and_plain_words_do_not_count(self):
        variety.note("a", "p", "Are you merely passing through Moonglow today?", now=0)
        self.assertIsNone(variety.check("b", "q", "Just passing through Moonglow, are you?", now=10))
        variety.note("c", "p", "I have a lot of work.", now=0)
        self.assertIsNone(variety.check("d", "q", "Mending a cart, I have a lot to do.", now=10))   # different opening

    def test_quoted_vendor_sentence_is_skipped(self):
        variety.note("a", "p", "Say 'vendor buy' to browse fine potions.", now=0)
        self.assertIsNone(variety.check("b", "q", "My stock is fresh. Say 'vendor buy' to browse fine potions.", now=10))
        variety.reset_for_tests()   # control: the same sentence without the vendor words IS a shared stock phrase
        variety.note("a", "p", "Say 'show me' to browse fine potions.", now=0)
        self.assertEqual(variety.check("b", "q", "My stock is fresh. Say 'show me' to browse fine potions.", now=10)["kind"],
                         "shared")


if __name__ == "__main__":
    unittest.main(verbosity=1)
