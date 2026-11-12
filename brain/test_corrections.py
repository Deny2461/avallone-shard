"""Rumour step, Part A (2026-11-11): a player corrects a story about themselves (corrections.py, rumours.py, the record
kind "rumour_corrected", and the respond handler). Ollama is never called. Run: python3 -m unittest test_corrections"""
import json
import os
import re
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
import brain  # noqa: E402
import corrections  # noqa: E402
import memory  # noqa: E402
import rumour_speech  # noqa: E402
import rumours  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
NPC = "44444444-4444-4444-8444-444444444444"
CHAR, OTHER = "22222222-2222-4222-8222-222222222222", "33333333-3333-4333-8333-333333333333"
H = 3600
LISTS = corrections.Lists(corrections.default_path(), towns=json.load(open(os.path.join(HERE, "rumours.json")))["towns"])
ALL = corrections.FIELDS


def deed(seq, typ, char=CHAR, who="Aldren Thorn", subject="", count=1, place="Britain", pt=9 * H, epoch=1, **extra):
    return {"event_id": f"{epoch}-{seq}", "epoch": epoch, "seq": seq, "npc_id": rumours.WORLD_ID, "char_id": char,
            "kind": "deed", "facts": {"type": typ, "subject": subject, "count": count, "who": who, "descriptor": "a fisher",
                                      "place": place, "pt": pt, "map": "Trammel", "x": 1492, "y": 1641, **extra},
            "at_utc": "2026-11-11T16:00:00Z", "schema": memory.SCHEMA_VERSION}


def fix(seq, story, char=CHAR, pt=9 * H + 60, epoch=1, npc=rumours.WORLD_ID, **changes):
    return {"event_id": f"{epoch}-{seq}", "epoch": epoch, "seq": seq, "npc_id": npc, "char_id": char,
            "kind": corrections.KIND, "facts": {"story": story, "pt": pt, **changes},
            "at_utc": "2026-11-11T16:01:00Z", "schema": memory.SCHEMA_VERSION}


class Table(unittest.TestCase):
    def test_the_table_matches_what_each_story_wording_shows(self):
        deeds = json.load(open(os.path.join(HERE, "rumours.json")))["deeds"]
        for kind, spec in deeds.items():
            if kind == "gm_planted":
                self.assertIsNone(corrections.fields_for(kind))            # a GM's text: no ticket at all
                continue
            text = spec["text"]
            shows = set()
            if "{count}" in text or "{times}" in text or "{a_subject}" in text:
                shows.add("count")
            if re.search(r"\{(a_subject|a_subject_one|subject|Subject)\}", text):
                shows.add("thing")
            if "{near_place}" in text or "{place}" in text:
                shows.add("place")
            if kind in corrections.STANDS:
                self.assertEqual(corrections.fields_for(kind), ())          # S4: nothing of a murder or a death
            else:
                self.assertEqual(set(corrections.fields_for(kind)), shows, kind)

    def test_murders_and_deaths_take_nothing(self):
        self.assertEqual(set(corrections.STANDS), {"murder", "died_notable", "died_pathetic"})
        for kind in corrections.STANDS:
            self.assertNotIn(kind, corrections.TABLE)

    def test_the_lists_hold_no_capitalised_thing_and_every_town(self):
        self.assertIn("giant rat", LISTS.things)
        self.assertEqual(LISTS.place("minoc"), "Minoc")
        self.assertEqual(LISTS.place("serpent's hold"), "Serpent's Hold")
        self.assertIsNone(LISTS.place("Mordor"))
        self.assertIsNone(LISTS.thing("Dragon"))


class Parse(unittest.TestCase):
    def p(self, line, fields=ALL, current=None, kind="kill_notable"):
        return corrections.parse(line, fields, LISTS, current, kind=kind)[0]

    def test_a_number_that_is_not_the_count_is_never_taken(self):
        # Codex code R1, RUM-C1: these were all read as "the count is four / twenty".
        now = {"count": 40, "thing": "dragon", "place": "Destard"}
        for line in ("I killed the dragon when I was four.", "It was a dragon aged four.", "That was on day four.",   # Codex code R2
                     "I was four.", "It was the fourth of them, four.", "it was four then", "It was four, I think, years back",
                     "It was four hours ago.", "That was twenty years ago.", "It was four times bigger than that.",
                     "It was two days ago near Minoc.",                # the number spoils the whole line, place and all
                     "It was twice as big.", "It was 3 miles from Destard.", "it was four gold a head",
                     "It was three o'clock.", "I was twenty then.", "it was two of us against it",
                     "It was four times."):                           # "times" counts only where the story counts times
            self.assertIsNone(self.p(line, current=now), line)
        for line, want in (("It was four dragons, not forty.", {"count": 4}),          # the legitimate controls
                           ("It was four.", {"count": 4}), ("No, it was 4", {"count": 4}),
                           ("it was four, not forty", {"count": 4}), ("there were only four of them", {"count": 4}),
                           ("It was four old dragons", {"count": 4}), ("it was 4 drakes", {"count": 4, "thing": "drake"}),
                           ("I only killed four, near Covetous", {"count": 4, "place": "Covetous"})):
            self.assertEqual(self.p(line, current=now), want, line)
        horse = {"count": 3, "thing": "horse", "place": "Britain"}
        self.assertEqual(self.p("It was only twice.", ("count", "thing"), horse, "pet_died"), {"count": 2})
        self.assertEqual(self.p("It was two times, not three", ("count", "thing"), horse, "pet_died"), {"count": 2})
        self.assertIsNone(self.p("It was two times worse than that", ("count", "thing"), horse, "pet_died"))
        self.assertEqual(self.p("It was four boots, not forty.", ("count",), {"count": 40}, "fished_boots"), {"count": 4})
        self.assertIsNone(self.p("It was four hours of fishing", ("count",), {"count": 40}, "fished_boots"))

    def test_plain_corrections(self):
        self.assertEqual(self.p("It was four boots, not forty.", kind="fished_boots"), {"count": 4})
        self.assertEqual(self.p("it wasn't forty, it was four"), {"count": 4})
        self.assertEqual(self.p("No, it was 4"), {"count": 4})
        self.assertEqual(self.p("that was near Minoc"), {"place": "Minoc"})
        self.assertEqual(self.p("It was a llama, not a horse"), {"thing": "llama"})
        self.assertEqual(self.p("it was not a horse but a llama"), {"thing": "llama"})
        self.assertEqual(self.p("Actually it was three dragons near Destard"), {"count": 3, "thing": "dragon", "place": "Destard"})
        self.assertEqual(self.p("there were twenty five of them"), {"count": 25})
        self.assertEqual(self.p("It was only twice."), {"count": 2})
        self.assertEqual(self.p("I only caught a hundred", kind="fished_boots"), {"count": 100})
        self.assertEqual(self.p("it was in Skara Brae, not Britain"), {"place": "Skara Brae"})
        self.assertEqual(self.p("It was a giant rat"), {"thing": "giant rat"})

    def test_only_details_this_story_shows(self):
        self.assertEqual(self.p("it was four boots near Minoc", fields=("count",), kind="fished_boots"), {"count": 4})
        self.assertIsNone(self.p("that was near Minoc", fields=("count",)))
        self.assertIsNone(self.p("it was four", fields=()))

    def test_a_change_to_what_it_already_says_is_nothing(self):
        now = {"count": 3, "thing": "dragon", "place": "Destard"}
        self.assertEqual(self.p("it was four dragons", current=now), {"count": 4})
        self.assertIsNone(self.p("it was three dragons near Destard", current=now))

    def test_refused_never_guessed(self):
        for line in ("it was four, or maybe five",                 # two readings
                     "it was a horse and a llama",
                     "it was near Minoc and Vesper",
                     "that wasn't me",                               # the deed denied
                     "That never happened, it was four",             # a denial mixed with a correction
                     "that is a lie, it was two",
                     "was it four?",                                 # a question
                     "it wasn't forty",                              # a detail denied, nothing put in its place
                     "I'd like 4 apples",                            # shopping
                     "actually how much for 4 loaves",
                     "four",                                         # no correction wording
                     "tell me more",
                     "really?",
                     "it was 5000",                                  # out of range
                     "it was zero",
                     "it was a unicorn in my garden " + "and so on " * 10,   # too long
                     ""):
            self.assertIsNone(self.p(line), line)

    def test_a_word_that_is_not_on_a_list_is_never_taken(self):
        self.assertIsNone(self.p("it was a Brynn Ashby, not a horse"))           # a name is not a creature kind
        self.assertIsNone(self.p("that was near Aldren's house"))                # nor a place
        self.assertIsNone(self.p("it was near Mordor"))
        self.assertIsNone(self.p("it was a dragon near Mordor"))                 # an unlisted word spoils the whole line

    def test_everyday_word_places_need_a_preposition_and_their_capital(self):
        self.assertIsNone(self.p("it was a shame, that"))
        self.assertEqual(self.p("it was near Shame"), {"place": "Shame"})
        self.assertEqual(self.p("it was in Cove"), {"place": "Cove"})
        for line in ("it was in fire", "It was near shame", "it was by wind", "It was Fire", "that was in haven"):
            self.assertIsNone(self.p(line), line)
        self.assertEqual(self.p("it was near minoc"), {"place": "Minoc"})       # a name that is no everyday word: any case

    def test_the_did_it_verb_belongs_to_the_kind_of_deed(self):
        self.assertEqual(self.p("I only killed four."), {"count": 4})
        for line in ("I lost four.", "I caught four.", "I tamed four.", "I was four.", "I had four.", "I saw four."):
            self.assertIsNone(self.p(line), line)                                # on a story of something slain
        self.assertEqual(self.p("I only caught four.", ("count",), {"count": 40}, "fished_boots"), {"count": 4})
        self.assertIsNone(self.p("I only killed four.", ("count",), {"count": 40}, "fished_boots"))
        self.assertEqual(self.p("I was in Vesper.", ("place",), {"place": "Britain"}, "drunk"), {"place": "Vesper"})
        self.assertIsNone(self.p("I was in Vesper.", current={"place": "Britain"}))   # only the drunk story is about where you were

    def test_only_whole_line_shapes_are_read(self):
        # Codex code R2: nothing is picked out of a sentence that merely contains correction wording and a value.
        for line in ("It was four and then some.", "I think it was four.", "They say it was four dragons but I forget.",
                     "It was four dragons that I saw die.", "it was near Minoc that I first saw it", "four",
                     "It was a llama I rode there", "Not forty.", "it was 4 4"):
            self.assertIsNone(self.p(line), line)
        for line, want in (("'Twas three drakes.", {"count": 3, "thing": "drake"}), ("Make that six.", {"count": 6}),
                           ("You mean a drake.", {"thing": "drake"}), ("Not forty, four.", {"count": 4}),
                           ("Well, it was only one.", {"count": 1}), ("there were twenty-five in all", {"count": 25}),
                           ("It was near Destard, not Covetous.", {"place": "Destard"})):
            self.assertEqual(self.p(line), want, line)

    def test_that_one_and_no_one_are_not_numbers(self):
        self.assertIsNone(self.p("it was that one I told you of"))
        self.assertEqual(self.p("it was only one"), {"count": 1})


class Lines(unittest.TestCase):
    def test_the_accepting_line_is_built_from_the_checked_values_only(self):
        self.assertIsNone(corrections.parse("It was a llama near Minoc, not a horse, by Aldren's SILLY beard", ALL, LISTS)[0])
        said = "It was a llama near Minoc, not a horse."
        changes = corrections.parse(said, ALL, LISTS)[0]
        self.assertEqual(changes, {"thing": "llama", "place": "Minoc"})
        for seed in range(40):
            line = corrections.accept_line(changes, seed)
            self.assertIn("llama near Minoc", line)
            for word in ("SILLY", "silly", "beard", "Aldren", "horse"):
                self.assertNotIn(word, line)
            self.assertLessEqual(len(line), 200)

    def test_details_in_plain_words(self):
        self.assertEqual(corrections.detail({"count": 4}), "four")
        self.assertEqual(corrections.detail({"count": 3, "thing": "llama"}), "three llamas")
        self.assertEqual(corrections.detail({"count": 1, "thing": "ettin", "place": "Yew"}), "one ettin near Yew")
        self.assertEqual(corrections.detail({"thing": "ettin"}), "an ettin")
        self.assertEqual(corrections.detail({"count": 137}), "137")

    def test_the_neutral_line_neither_accepts_nor_refuses(self):
        for line in corrections.NEUTRAL:
            self.assertIsNone(re.search(r"\b(wrong|right|correct|will tell|set .* straight|lie|never)\b", line, re.I), line)
        self.assertEqual(len({corrections.neutral_line(s) for s in range(60)}), len(corrections.NEUTRAL))


class Stored(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.led.reconcile(1, 0, 0)
        self.r = rumours.Rumours(self.led, os.path.join(HERE, "rumours.json"))
        self.assertIsNone(self.r.lists_error)
        acked, rejected = self.led.ingest([
            deed(1, "fished_boots", count=40),
            deed(2, "kill_notable", subject="dragon", count=1, place="Destard"),
            deed(3, "kill_notable", char=OTHER, who="Brynn Ashby", subject="ettin", place="Yew"),
            deed(4, "murder", place="Britain")])
        self.assertEqual(rejected, [])

    def tearDown(self):
        self.led.db.close()
        self.led.rdb.close()
        self.dir.cleanup()

    def text(self, story, town="Britain", pt=10 * H):
        return {r["id"]: r for r in self.r.view(pt)}.get(story) and \
            next(r["text"] for r in self.r.view(pt) if r["id"] == story and r["town"] == town)

    def test_a_correction_changes_the_wording_and_nothing_else(self):
        before = self.r.view(10 * H)
        nick = self.r.epithets(10 * H)
        self.assertIn("40 old boots", self.text("1-1"))
        self.assertEqual(self.led.ingest([fix(10, "1-1", count=4)]), (["1-10"], []))
        self.assertIn("fished up 4 old boots", self.text("1-1"))
        after = self.r.view(10 * H)
        strip = lambda rows: [{k: v for k, v in r.items() if k not in ("text", "corrected", "was")} for r in rows]
        self.assertEqual(strip(before), strip(after))        # same ids, towns, arrival times, hops, garbles
        self.assertEqual(self.r.epithets(10 * H), nick)      # nicknames go on reading the real deeds
        row = next(r for r in after if r["id"] == "1-1")
        self.assertEqual(row["corrected"], {"count": 4})
        self.assertIn("40 old boots", row["was"])             # a GM sees both versions

    def test_the_latest_live_correction_wins_per_detail(self):
        self.led.ingest([fix(10, "1-2", count=3), fix(11, "1-2", place="Covetous"), fix(12, "1-2", count=7, thing="drake")])
        self.assertEqual(self.text("1-2"), "Aldren Thorn slew many drakes near Covetous")
        self.assertEqual(self.r.shown(self.r.story(10 * H, "1-2")), {"count": 7, "thing": "drake", "place": "Covetous"})

    def test_only_the_storys_own_character_and_only_live_rows(self):
        self.led.ingest([fix(10, "1-2", char=OTHER, count=9)])                    # someone else's "correction"
        self.assertIn("slew a dragon near Destard", self.text("1-2"))
        self.led.ingest([fix(11, "1-2", count=5)])
        self.assertIn("many dragons", self.text("1-2"))
        with self.led.lock:
            self.led.db.execute("UPDATE events SET status = 'rolled_back' WHERE event_id = '1-11'")
        self.assertIn("slew a dragon near Destard", self.text("1-2"))             # a rolled-back correction is gone

    def test_a_rolled_back_story_takes_its_correction_with_it(self):
        self.led.ingest([fix(10, "1-1", count=4)])
        with self.led.lock:
            self.led.db.execute("UPDATE events SET status = 'rolled_back' WHERE event_id = '1-1'")
        self.assertIsNone(self.r.story(10 * H, "1-1"))
        self.assertNotIn("1-1", [r["id"] for r in self.r.view(10 * H)])

    def test_a_murder_is_never_changed_even_if_a_row_says_so(self):
        self.assertEqual(self.led.ingest([fix(10, "1-4", place="Minoc")])[1], [])   # the shape is fine, so it is stored...
        self.assertIn("murdered an innocent near Britain", self.text("1-4"))        # ...and never shown
        self.assertNotIn("corrected", next(r for r in self.r.view(10 * H) if r["id"] == "1-4"))

    def test_a_detail_the_story_does_not_show_or_a_value_off_the_lists_is_not_shown(self):
        self.led.ingest([fix(10, "1-1", place="Minoc"), fix(11, "1-2", thing="mule"), fix(12, "1-2", place="Mordor")])
        self.assertIn("40 old boots", self.text("1-1"))
        self.assertIn("slew a dragon near Destard", self.text("1-2"))

    def test_the_show_switch(self):
        self.led.ingest([fix(10, "1-1", count=4)])
        self.r.show_corrections = False
        self.assertIn("40 old boots", self.text("1-1"))
        self.assertNotIn("was", next(r for r in self.r.view(10 * H) if r["id"] == "1-1"))

    def test_a_corrected_detail_no_longer_changes_with_travel(self):
        g = self.r.story(10 * H, "1-1")
        self.assertIn("41 old boots", self.r.render(g, ["count_up"]))
        self.led.ingest([fix(10, "1-1", count=4)])
        g = self.r.story(10 * H, "1-1")
        self.assertIn("4 old boots", self.r.render(g, ["count_up"]))
        self.assertIn("40 old boots", self.r.render(g, [], raw=True))

    def test_the_record_shape_is_checked_on_the_way_in(self):
        bad = [fix(20, "1-1"),                                         # no change
               fix(21, "1-1", count=0), fix(22, "1-1", count=1000), fix(23, "1-1", count="4"), fix(24, "1-1", count=True),
               fix(25, "1-1", thing="a {who} thing"), fix(26, "1-1", place="Minoc\nBritain"),
               fix(27, "not-an-id", count=4), fix(28, "1-1", count=4, said="it was four boots not forty"),
               fix(29, "1-1", count=4, npc=NPC)]                        # not a world record
        acked, rejected = self.led.ingest(bad)
        self.assertEqual(acked, [])
        self.assertEqual(len(rejected), len(bad))
        self.assertEqual(self.led.ingest([fix(30, "1-1", count=4, thing="llama", place="Minoc")]), (["1-30"], []))
        stored = json.loads(self.led.db.execute("SELECT facts FROM events WHERE event_id = '1-30'").fetchone()[0])
        self.assertEqual(stored, {"story": "1-1", "pt": 9 * H + 60, "count": 4, "thing": "llama", "place": "Minoc"})

    def test_no_other_reader_sees_the_kind(self):
        self.led.ingest([fix(10, "1-1", count=4)])
        self.assertIn(corrections.KIND, memory.PRIVATE_KINDS)
        self.assertEqual(self.led.db.execute("SELECT COUNT(*) FROM events_fts WHERE event_id = '1-10'").fetchone()[0], 0)
        self.assertNotIn(corrections.KIND, [r["kind"] for r in self.led.recent(rumours.WORLD_ID, CHAR, limit=50)])
        self.assertEqual([g["id"] for g in self.r._group(self.r._deeds(10 * H))], ["1-1", "1-2", "1-3", "1-4"])   # not a deed


class Turn(unittest.TestCase):
    """The respond handler: a ticket line that is a plain correction is answered by code; anything else by the model."""

    @classmethod
    def setUpClass(cls):
        cls.srv = ThreadingHTTPServer(("127.0.0.1", 0), brain.Handler)
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()

    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.led.reconcile(1, 0, 0)
        self.r = rumours.Rumours(self.led, os.path.join(HERE, "rumours.json"))
        self.led.ingest([deed(1, "fished_boots", count=40), deed(2, "kill_notable", char=OTHER, who="Brynn Ashby",
                                                                 subject="ettin", place="Yew"), deed(3, "murder")])
        self.prompts, self.calls = [], 0
        self.patches = [mock.patch.object(brain, "LEDGER", self.led), mock.patch.object(brain, "RUMOURS", self.r),
                        mock.patch.object(brain, "log", lambda **k: None)]
        for p in self.patches:
            p.start()

    def tearDown(self):
        for p in self.patches:
            p.stop()
        self.led.db.close()
        self.led.rdb.close()
        self.dir.cleanup()

    def ask(self, utterance, ticket=None, on=True, rumours_ask=None, say="Aye, so they say."):
        def fake(persona, utt, *a, **k):
            self.calls += 1
            self.prompts.append(" ".join(str(x) for x in list(a) + list(k.values()) if isinstance(x, str)))
            tok = rumour_speech.SENTINEL_RE.search(self.prompts[-1])
            want = re.search(r'word for word, with nothing in front of it but your action: "([^"]+)"', self.prompts[-1])
            self.sentence = want.group(1) if want else ""        # the sentence the code wrote for this reply, if any
            # SAID = that sentence; SAID- = the same without its full stop (a reply that sets out with it and runs on)
            return (say.replace("SAID-", self.sentence[:-1]).replace("SAID", self.sentence)
                    .replace("WHO", tok.group(0) if tok else "Someone")), "none"
        body = {"request_id": f"r{self.calls}-{len(self.prompts)}", "npc_id": NPC, "subject_id": CHAR,
                "persona_key": "townsfolk", "persona_vars": {"name": "Osric", "role": "tanner", "town": "Britain", "trade": "vendor"},
                "utterance": utterance, "budget_ms": 8000}
        if ticket is not None:
            body["correction"] = ticket
        if rumours_ask:
            body["rumours"] = rumours_ask
        req = urllib.request.Request(f"http://127.0.0.1:{self.srv.server_address[1]}/v1/npc/respond",
                                     json.dumps(body).encode(), {"Content-Type": "application/json"})
        with mock.patch.object(brain, "ollama_chat", fake), mock.patch.dict(brain.CFG, {"rumour_corrections": on}):
            return json.loads(urllib.request.urlopen(req, timeout=20).read())

    @staticmethod
    def ticket(story="1-1", kind="fished_boots", fields=("count",), pt=10 * H):
        return {"story": story, "type": kind, "fields": list(fields), "pt": pt,
                "roster": [{"id": CHAR, "name": "Aldren Thorn"}, {"id": OTHER, "name": "Brynn Ashby"}]}

    def test_a_plain_correction_gets_the_neutral_line_and_the_candidate_as_extras(self):
        out = self.ask("It was four boots, not forty.", self.ticket())
        self.assertTrue(out["ok"])
        self.assertEqual(self.calls, 0)                                         # no model call
        self.assertIn(out["say"], corrections.NEUTRAL)                          # an old shard would speak this
        self.assertEqual(out["correction"]["story"], "1-1")
        self.assertEqual(out["correction"]["changes"], {"count": 4})
        self.assertIn("four", out["correction"]["accept"].lower())
        self.assertNotIn("rumour", out)
        self.assertNotIn("forty", json.dumps(out).lower())                      # nothing of the old value or the sentence

    def test_a_time_an_age_or_a_size_gives_no_candidate_and_no_accepting_line(self):
        # Codex code R1, RUM-C1 - through the real handler, on a live story of the player's own.
        self.led.ingest([deed(7, "kill_notable", subject="dragon", count=40, place="Destard")])
        t = self.ticket(story="1-7", kind="kill_notable", fields=ALL)
        for line in ("It was four hours ago.", "That was twenty years ago.", "It was four times bigger than that.",
                     "I killed the dragon when I was four.", "It was a dragon aged four.", "That was on day four."):   # R1, then R2
            out = self.ask(line, t)
            self.assertNotIn("correction", out, line)
            self.assertNotIn(out["say"], corrections.NEUTRAL, line)             # an ordinary reply, from the model
            self.assertIn("do not agree to change it", self.prompts[-1], line)
        for line in ("It was four dragons, not forty.", "It was four.", "I only killed four."):   # the controls
            out = self.ask(line, t)
            self.assertEqual(out["correction"]["changes"], {"count": 4}, line)
            self.assertIn("four", out["correction"]["accept"].lower())

    def test_switch_off_no_candidate_but_the_npc_still_may_not_agree(self):
        out = self.ask("It was four boots, not forty.", self.ticket(), on=False)
        self.assertNotIn("correction", out)
        self.assertGreaterEqual(self.calls, 1)                                  # the model answered
        self.assertIn("do not agree to change it", self.prompts[-1])           # a ticket came: nothing will be recorded

    def test_no_ticket_no_candidate(self):
        out = self.ask("It was four boots, not forty.")
        self.assertNotIn("correction", out)
        self.assertGreaterEqual(self.calls, 1)
        self.assertNotIn("as it reached you", self.prompts[-1])                 # no ticket: the prompt is untouched

    def test_a_line_that_is_not_a_plain_correction_goes_to_the_model_with_the_as_heard_rule(self):
        for line in ("It was four, or maybe five.", "That wasn't me!", "Tell me more."):
            out = self.ask(line, self.ticket())
            self.assertNotIn("correction", out, line)
            self.assertIn("do not agree to change it", self.prompts[-1], line)

    def test_someone_elses_story_or_a_dead_story_gives_no_candidate(self):
        out = self.ask("It was two ettins.", self.ticket(story="1-2", kind="kill_notable", fields=ALL))
        self.assertNotIn("correction", out)                                     # Brynn's story, not this player's
        out = self.ask("It was four boots, not forty.", self.ticket(pt=400 * H))
        self.assertNotIn("correction", out)                                     # long faded
        out = self.ask("It was four boots, not forty.", self.ticket(story="9-9"))
        self.assertNotIn("correction", out)
        out = self.ask("It was four boots, not forty.", self.ticket(kind="drunk", fields=("place",)))
        self.assertNotIn("correction", out)                                     # the ticket's kind is not the story's

    def test_a_murder_stands(self):
        out = self.ask("It was near Minoc, not Britain.", self.ticket(story="1-3", kind="murder", fields=()))
        self.assertNotIn("correction", out)
        self.assertIn("That tale stands as it is told", self.prompts[-1])
        out = self.ask("It was near Minoc.", self.ticket(story="1-3", kind="murder", fields=("place",)))   # a ticket that lies
        self.assertNotIn("correction", out)

    NEWS = {"town": "Britain", "pt": 10 * H, "mode": "news", "exclude": [], "recent": ["1-2", "1-3"],
            "roster": [{"id": CHAR, "name": "Aldren Thorn"}, {"id": OTHER, "name": "Brynn Ashby"}]}

    def test_a_reply_that_begins_with_the_code_sentence_is_marked_correctable(self):
        out = self.ask("What news?", rumours_ask=self.NEWS, say="[leans in] SAID That is a lot of leather.")
        self.assertIn(self.sentence, [f"{o} you fished up forty old boots in one sitting." for o in rumour_speech.PLAIN_OPENINGS])
        self.assertEqual(out["say"], f"[leans in] {self.sentence} That is a lot of leather.")   # the model's reply, untouched
        self.assertEqual(out["rumour"]["ids"], ["1-1"])
        self.assertEqual(out["correctable"], {"id": "1-1", "type": "fished_boots"})
        other = dict(self.NEWS, recent=["1-1", "1-3"])
        out = self.ask("What news?", rumours_ask=other, say="SAID A bold one, that.")
        self.assertEqual(out["rumour"]["ids"], ["1-2"])
        self.assertNotIn("correctable", out)                                    # someone else's story

    def test_a_reply_that_does_not_is_never_read_the_brain_says_the_sentence_itself(self):
        # Codex code R2-R5, RUM-C3 B - through the real handler, the listener's own dragon story on offer. Each line
        # passed one of the four readers that tried to understand free wording, and gave a correction-ticket mark for
        # a story nobody had told. Now none is read at all: the final reply IS the code's sentence, so the mark that
        # goes with it is true - and nothing of the model's wrong telling is left in what is said.
        self.led.ingest([deed(7, "kill_notable", subject="dragon", count=3, place="Britain")])
        news = dict(self.NEWS, recent=["1-1", "1-2", "1-3"])
        for corrections, plain in ((True, False), (False, True)):
            with mock.patch.dict(brain.CFG, {"plain_telling": plain}):
                for say in ("Dragons have fine scales, friend.",                                    # R2
                            "I slew a dragon while you were away.", "You know, I killed a dragon once.",   # R3
                            "A dragon slew you near Britain.", "Did you slay a dragon near Britain?",
                            "They say you slew a troll and a knight took a dragon.",               # R4
                            "Imagine: you slew forty dragons.", "You slew my hopes of a dragon.",
                            "They say you slew three dragonflies near Britain.",                   # R5
                            "A dragon was slain in effigy by you near Britain.",
                            "You slew three dragons near Britain.", "Quiet times, friend."):
                    out = self.ask("What news?", on=corrections, rumours_ask=news, say=say)
                    want = [f"{o} you slew three dragons near Britain." for o in rumour_speech.PLAIN_OPENINGS]
                    self.assertIn(self.sentence, want, say)
                    self.assertEqual(out["say"], self.sentence, (say, corrections, plain))          # the code's sentence, alone
                    self.assertEqual(out["rumour"]["ids"], ["1-7"], say)
                    self.assertEqual(out["correctable"], {"id": "1-7", "type": "kill_notable"}, say)
                # the model's action stays in front; and when the reply SET OUT with the sentence, its remark is kept
                out = self.ask("What news?", on=corrections, rumours_ask=news, say="[mends a net] Dragonflies, three of them. Ha.")
                self.assertEqual(out["say"], "[mends a net] " + self.sentence)
                out = self.ask("What news?", on=corrections, rumours_ask=news, say="SAID A fine thing, that.")
                self.assertEqual(out["say"], self.sentence + " A fine thing, that.")     # exact: the model's reply, untouched
                for ran_on in ("SAID- in a play, I hear. A fine thing, that.", "SAID-, mark you. A fine thing, that.",
                               "SAID-flies. A fine thing, that."):
                    out = self.ask("What news?", on=corrections, rumours_ask=news, say=ran_on)
                    self.assertEqual(out["say"], self.sentence + " A fine thing, that.", ran_on)   # the run-on is gone with its sentence
                self.assertEqual(out["correctable"], {"id": "1-7", "type": "kill_notable"})

    def test_with_every_switch_off_nothing_is_written_and_there_is_never_a_ticket_mark(self):
        self.led.ingest([deed(7, "kill_notable", subject="dragon", count=3, place="Britain")])
        news = dict(self.NEWS, recent=["1-1", "1-2", "1-3"])
        for say, heard in (("Dragons have fine scales, friend.", ["1-7"]),      # live's loose reading: one key word
                           ("They say you slew three dragons near Britain.", ["1-7"]),
                           ("They say you slew three dragonflies near Britain.", ["1-7"]),
                           ("Quiet times, friend.", [])):
            out = self.ask("What news?", on=False, rumours_ask=news, say=say)
            self.assertEqual(self.sentence, "")                                  # no sentence was written: the old prompt
            self.assertIn(rumour_speech.OWN_WORDS, self.prompts[-1])
            self.assertEqual(out["say"], say)                                    # nothing is replaced
            self.assertEqual(out["rumour"]["ids"], heard, say)
            self.assertNotIn("correctable", out, say)                            # no exact sentence, no mark - ever

    def test_the_record_endpoints_say_this_brain_accepts_corrections(self):
        for op, body in (("events", {"events": []}), ("reconcile", {"new_epoch": 2, "saved_epoch": 1, "saved_seq": 3})):
            req = urllib.request.Request(f"http://127.0.0.1:{self.srv.server_address[1]}/v1/memory/{op}",
                                         json.dumps(body).encode(), {"Content-Type": "application/json"})
            with mock.patch.dict(brain.CFG, {"rumour_corrections": False}):     # accepting never depends on the switch
                out = json.loads(urllib.request.urlopen(req, timeout=20).read())
            self.assertEqual(out["rumour_corrected"], 1, op)
            self.assertEqual(out["talked_with_topics"], 1, op)


if __name__ == "__main__":
    unittest.main()
