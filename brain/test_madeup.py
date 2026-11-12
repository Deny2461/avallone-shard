"""Rumour step C2 (2026-11-11): made-up town gossip as stories in the normal rumour flow (madeup.py), the one weighted
draw (rumour_speech.select with madeup=...), "really told" for a story with no deed behind it (made-up, one-off,
GM-planted), and the reply path. Ollama is never called. Run: python3 -m unittest test_madeup"""
import json
import os
import random
import shutil
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
import madeup  # noqa: E402
import memory  # noqa: E402
import rumour_speech  # noqa: E402
import rumours  # noqa: E402
import smalltalk  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
CFG = os.path.join(HERE, "rumours.json")
WREN, OTHER = "22222222-2222-2222-2222-222222222222", "33333333-3333-3333-3333-333333333333"
NPC = "44444444-4444-4444-8444-444444444444"
H = 3600
NEAR_BRITAIN = {"map": "Trammel", "x": 1492, "y": 1641}
PT = 200 * H


class Base(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.led.reconcile(1, 0, 0)
        self.r = rumours.Rumours(self.led, CFG)
        self.m = madeup.MadeUp(self.led, self.r)
        self.seq = 0

    def tearDown(self):
        self.led.db.close()
        self.led.rdb.close()
        self.dir.cleanup()

    def deed(self, typ, pt, char=WREN, who="Racer X", subject="dragon", **extra):
        self.seq += 1
        facts = {"type": typ, "subject": subject, "count": 1, "who": who, "descriptor": "a tamer", "place": "Destard",
                 "pt": pt, **NEAR_BRITAIN, **extra}
        acked, rej = self.led.ingest([{
            "event_id": f"1-{self.seq}", "epoch": 1, "seq": self.seq, "npc_id": rumours.WORLD_ID, "char_id": char,
            "kind": "deed", "facts": facts, "at_utc": "2026-11-11T16:00:00Z", "schema": memory.SCHEMA_VERSION}])
        self.assertEqual(rej, [])
        return f"1-{self.seq}"

    def offer(self, mode="news", pt=PT, town="Britain", seed=1, npc=NPC, flow=True, **kw):
        return rumour_speech.select(self.r, pt, town, kw.pop("speaker", WREN), mode, rng=random.Random(seed),
                                    madeup=self.m if flow else None, npc_id=npc, **kw)


class Stories(Base):
    def test_ids_are_short_stable_and_carry_the_content_key(self):
        rows = self.m.town_rows(PT, "Britain")
        self.assertTrue(rows)
        for s, _, _ in rows:
            self.assertLessEqual(len(s["id"]), 40)
            self.assertRegex(s["id"], madeup.MU_RE)
            self.assertEqual(madeup.key_of(s["id"]), s["key"])
        again = madeup.MadeUp(self.led, self.r)                       # a second brain on the same store, a restart
        self.assertEqual([(s["id"], s["text"]) for s, _, _ in again.town_rows(PT, "Britain")],
                         [(s["id"], s["text"]) for s, _, _ in rows])
        self.assertIsNone(madeup.key_of("1-17"))
        self.assertEqual(madeup.key_of("mo-0123abcd"), "0123abcd")

    def test_about_six_of_its_own_live_at_a_time_and_they_turn_over_one_by_one(self):
        def own(pt):
            w = pt // (4 * H)
            return {s["id"] for s in (self.m._story("Britain", k) for k in range(max(0, w - 10), w + 1))
                    if s and s["born_pt"] <= pt < s["expires_pt"]}
        counts = [len(own(pt)) for pt in range(60 * H, 400 * H, 3 * H)]
        self.assertGreaterEqual(min(counts), 3)
        self.assertLessEqual(max(counts), 9)
        self.assertAlmostEqual(sum(counts) / len(counts), 6.0, delta=1.0)
        for pt in range(60 * H, 200 * H, 4 * H):                      # one window on: at most a few come or go
            self.assertLessEqual(len(own(pt) ^ own(pt + 4 * H)), 4)

    def test_each_town_holds_at_most_eight_and_half_are_its_own(self):
        for town in self.r.cfg["towns"]:
            rows = self.m.town_rows(PT, town)
            self.assertLessEqual(len(rows), 8)
            self.assertGreaterEqual(sum(s["origin"] == town for s, _, _ in rows), 3, town)
            self.assertEqual(len({s["key"] for s, _, _ in rows}), len(rows))        # never the same line twice
        self.assertEqual(self.m.town_rows(PT, "Atlantis"), [])

    def test_they_travel_and_never_change_in_the_telling(self):
        s = next(s for s, _, hops in self.m.town_rows(PT, "Britain") if s["origin"] == "Britain")
        seen = {}
        for dt in range(0, 40 * H, 1800):
            for town in self.r.cfg["towns"]:
                for st, arrived, hops in self.m.town_rows(s["born_pt"] + dt, town):
                    if st["id"] == s["id"]:
                        seen.setdefault(town, (arrived, hops, st["text"]))
        self.assertGreater(len(seen), 3)
        self.assertEqual(seen["Britain"][0], s["born_pt"])
        self.assertEqual(seen["Britain"][1], 0)
        self.assertTrue(all(a > s["born_pt"] and h > 0 for t, (a, h, _) in seen.items() if t != "Britain"))
        self.assertEqual({text for _, _, text in seen.values()}, {s["text"]})
        self.assertFalse([1 for st, _, _ in self.m.town_rows(s["born_pt"] - 1, "Britain") if st["id"] == s["id"]])
        self.assertFalse([1 for st, _, _ in self.m.town_rows(s["expires_pt"], "Britain") if st["id"] == s["id"]])

    def test_each_npc_has_heard_about_half_and_it_is_fixed(self):
        town = len(self.m.town_rows(PT, "Britain"))
        known = {n: tuple(r["id"] for r in self.m.rows(PT, "Britain", f"npc-{n}")) for n in range(300)}
        mean = sum(len(k) for k in known.values()) / len(known)
        self.assertAlmostEqual(mean, town / 2, delta=0.6)
        self.assertGreater(len(set(known.values())), 30)                              # NPCs differ
        self.assertEqual(known[7], tuple(r["id"] for r in self.m.rows(PT, "Britain", "npc-7")))
        self.assertEqual(len(self.m.rows(PT, "Britain", "")), town)                   # no NPC id: the whole set

    def test_a_pool_edit_never_changes_what_a_live_id_means(self):
        pool = os.path.join(self.dir.name, "pool.json")
        shutil.copy(smalltalk.PATH, pool)
        m = madeup.MadeUp(self.led, self.r, pool)
        before = {s["id"]: s["text"] for s, _, _ in m.town_rows(PT, "Britain")}
        with open(pool, encoding="utf-8") as fh:
            d = json.load(fh)
        d["any"] = ["the well in {town} has turned sweet overnight"]                   # every old line is gone
        d["towns"] = {}
        with open(pool, "w", encoding="utf-8") as fh:
            json.dump(d, fh)
        os.utime(pool, (1, 1))
        m2 = madeup.MadeUp(self.led, self.r, pool)                                    # a restart with the edited file
        after = {s["id"]: s["text"] for s, _, _ in m2.town_rows(PT, "Britain")}
        self.assertEqual(after, before)                                               # live ids keep their words
        sid = next(iter(before))
        self.assertEqual(m2.resolve(sid, PT, "Britain")["text"], before[sid])
        later = PT + 60 * H                                                           # all of those have run out by now
        fresh = [s for s, _, _ in m2.town_rows(later, "Britain")]
        self.assertTrue(fresh)
        self.assertFalse(set(before) & {s["id"] for s in fresh})
        self.assertTrue(all("turned sweet" in s["text"] for s in fresh))

    def test_two_lines_with_one_key_are_refused_not_merged(self):
        pool = os.path.join(self.dir.name, "clash.json")
        with open(pool, "w", encoding="utf-8") as fh:
            json.dump({"any": ["a first line about geese", "a second line about tin"], "towns": {}}, fh)
        with mock.patch.object(madeup, "content_key", lambda line: "deadbeef"):
            with self.assertRaises(ValueError):
                madeup.load_pool(pool)
        madeup.load_pool()                                                            # the shipped pool has no clash

    def test_clock_rewind_and_gm_drop(self):
        rows = self.m.town_rows(PT, "Britain")
        newest = max(rows, key=lambda r: r[0]["born_pt"])[0]
        self.assertFalse([1 for s, _, _ in self.m.town_rows(newest["born_pt"] - 1, "Britain") if s["id"] == newest["id"]])
        self.r.drop(newest["id"], "2026-11-11T00:00:00Z")
        self.assertFalse([1 for s, _, _ in self.m.town_rows(PT, "Britain") if s["id"] == newest["id"]])
        self.assertIsNone(self.m.resolve(newest["id"], PT, "Britain"))

    def test_gm_view_marks_the_source_and_tick_forgets_old_ones(self):
        v = self.m.view(PT, "Britain")
        self.assertTrue(v and all(r["source"] == "made_up" and r["type"] == "made_up" and r["town"] == "Britain" for r in v))
        self.assertEqual(len(self.m.view(PT)), sum(len(self.m.town_rows(PT, t)) for t in self.r.cfg["towns"]))
        n = self.led.db.execute("SELECT COUNT(*) FROM madeup_stories").fetchone()[0]
        self.m.tick(PT)
        self.assertEqual(self.led.db.execute("SELECT COUNT(*) FROM madeup_stories").fetchone()[0], n)   # nothing old yet
        self.m.tick(PT + 400 * H)
        self.assertEqual(self.led.db.execute("SELECT COUNT(*) FROM madeup_stories").fetchone()[0], 0)




class Draw(Base):
    def test_flow_off_is_the_old_select(self):
        self.assertIsNone(self.offer(flow=False))                                    # no real rumour: nothing
        self.deed("kill_notable", PT - H)
        a, b = self.offer(flow=False, seed=5), rumour_speech.select(self.r, PT, "Britain", WREN, "news", rng=random.Random(5))
        self.assertEqual((a.block, a.ids), (b.block, b.ids))

    def test_what_news_with_no_real_rumour_tells_a_made_up_one(self):
        kinds = set()
        for seed in range(60):
            o = self.offer(seed=seed)
            self.assertIsNotNone(o)
            self.assertEqual(len(o.ids), 1)
            self.assertTrue(madeup.key_of(o.ids[0]), o.ids)
            self.assertIn("Idle talk going round town", o.block)
            self.assertIn("- " + o.canon[o.ids[0]], o.block)
            self.assertFalse(o.sentinel)
            kinds.add(o.source[o.ids[0]])
        self.assertEqual(kinds, {"made_up", "one_off"})

    def test_a_real_story_is_no_longer_automatically_first(self):
        a = self.deed("kill_notable", PT - H, char=OTHER, who="Brynn")
        b = self.deed("tame_notable", PT - H, char=OTHER, who="Brynn", subject="nightmare")
        tally = {"real": 0, "made_up": 0, "one_off": 0}
        for seed in range(600):
            o = self.offer(seed=seed, speaker=WREN)
            tally["real" if o.ids[0] in (a, b) else o.source[o.ids[0]]] += 1
        self.assertGreater(tally["real"], 600 * 0.30)          # real stories weigh more ...
        self.assertLess(tally["real"], 600 * 0.80)             # ... but are not always told first
        self.assertGreater(tally["made_up"], 600 * 0.08)
        self.assertGreater(tally["one_off"], 600 * 0.08)

    def test_the_listeners_own_story_still_weighs_double(self):
        mine = self.deed("kill_notable", PT - H, char=WREN)
        theirs = self.deed("kill_notable", PT - H, char=OTHER, who="Brynn", subject="lich")
        first = [self.offer(seed=s, speaker=WREN).ids[0] for s in range(800)]
        self.assertGreater(first.count(mine), first.count(theirs) * 1.4)

    def test_lists_are_cut_to_their_own_limits_before_the_draw(self):
        for i in range(12):
            self.deed("kill_notable", PT - H, char=OTHER, who="Brynn", subject="beast " + "abcdefghijkl"[i])
        self.assertLessEqual(len(self.r.speech_rows(PT, "Britain")), 5)
        self.assertLessEqual(len(self.m.rows(PT, "Britain", "")), 8)
        seen = {self.offer(seed=s, npc="").ids[0] for s in range(500)}
        self.assertLessEqual(len([i for i in seen if not madeup.key_of(i)]), 5)
        self.assertLessEqual(len([i for i in seen if i.startswith("mu-")]), 8)

    def test_just_heard_is_not_retold_by_id_or_by_content(self):
        known = self.m.rows(PT, "Britain", NPC)
        self.assertTrue(known)
        recent = [r["id"] for r in known]
        for seed in range(80):
            o = self.offer(seed=seed, recent=recent)
            self.assertNotIn(o.ids[0], recent)
            self.assertEqual(o.source[o.ids[0]], "one_off")
            self.assertNotIn(madeup.key_of(o.ids[0]), {r["key"] for r in known})
        # the same words under a one-off id block the travelling story too
        as_one_off = ["mo-" + r["key"] for r in known]
        for seed in range(80):
            self.assertNotIn(self.offer(seed=seed, recent=as_one_off).ids[0], recent)

    def test_heard_in_the_last_two_hours_is_a_quarter_as_likely(self):
        known = self.m.rows(PT, "Britain", NPC)
        target = known[0]["id"]
        plain = sum(self.offer(seed=s).ids[0] == target for s in range(1500))
        damped = sum(self.offer(seed=s, exclude=[target]).ids[0] == target for s in range(1500))
        self.assertGreater(plain, 60)
        self.assertLess(damped, plain * 0.55)
        self.assertGreater(damped, 0)                           # damped, not ruled out

    def test_a_one_off_never_repeats_a_story_live_in_the_town(self):
        live = {s["key"] for s, _, _ in self.m.town_rows(PT, "Britain")}
        for seed in range(300):
            o = self.offer(seed=seed)
            if o.source[o.ids[0]] == "one_off":
                self.assertNotIn(madeup.key_of(o.ids[0]), live)

    def test_follow_up_finds_the_exact_story_whatever_the_npc_knows(self):
        s = self.m.town_rows(PT, "Britain")[0][0]
        stranger = next(f"npc-{n}" for n in range(200) if not self.m.knows(f"npc-{n}", s["id"]))
        o = self.offer("follow", follow=s["id"], npc=stranger)
        self.assertEqual(o.ids, [s["id"]])
        self.assertIn("- " + s["text"], o.block)
        self.assertIn("They want to hear more about the tale you passed on", o.block)
        self.assertIsNone(self.offer("follow", follow=s["id"], pt=s["expires_pt"]))       # run out
        self.assertIsNone(self.offer("follow", follow="mu-00000000-0000-zz"))            # never existed
        self.assertIsNone(self.offer("follow", follow=s["id"], flow=False))              # switch off: as before

    def test_follow_up_on_a_one_off_line_until_it_runs_out(self):
        o = next(o for o in (self.offer(seed=s) for s in range(200)) if o.source[o.ids[0]] == "one_off")
        sid, text = o.ids[0], o.canon[o.ids[0]]
        f = self.offer("follow", follow=sid, pt=PT + 600)
        self.assertEqual(f.ids, [sid])
        self.assertIn("- " + text, f.block)
        self.assertIsNone(self.offer("follow", follow=sid, pt=PT + 5 * H))   # it lives to the end of the NEXT 2-hour window

    # ---- Codex code R1, RUM-C2: a one-off id names one rendered text and one lifetime, for good ------------------
    class Pick:
        """A stand-in for the random source that forces which pool line and which neighbouring town are drawn."""
        def __init__(self, line, near=None):
            self.line, self.near = line, near

        def random(self):
            return 0.99                                   # never the town's own short list

        def choice(self, seq):
            return self.line if self.line in seq else self.near if self.near in seq else seq[0]

    def one(self, line, town, pt=PT, near=None):
        row = self.m.one_off(pt, town, self.Pick(line, near))
        self.assertIsNotNone(row)
        self.assertTrue(self.m.pin_one_off(row, pt))
        return row

    def stored(self, sid):
        return self.led.db.execute("SELECT text, born_pt, expires_pt FROM madeup_stories WHERE id = ?", (sid,)).fetchone()


    def test_a_follow_up_may_use_the_storys_own_words(self):
        s = self.m.town_rows(PT, "Britain")[0][0]
        o = self.offer("follow", follow=s["id"])
        note = " " + s["text"].lower() + " "
        self.assertFalse([w for w in o.forbid if f" {w} " in note])

    def test_greetings_and_asking_after_someone_are_unchanged(self):
        self.assertIsNone(self.offer("greet"))                                          # no real story: no tease
        rid = self.deed("kill_notable", PT - H, char=OTHER, who="Brynn")
        for seed in range(40):
            self.assertEqual(self.offer("greet", seed=seed).ids, [rid])

    def test_the_lore_pages_are_one_choice_in_the_draw_not_three_turns_in_four(self):
        self.deed("kill_notable", PT - H, char=OTHER, who="Brynn")
        got = [self.offer(seed=s, lore_weight=1.0) for s in range(800)]
        lore = sum(o is rumour_speech.LORE_TURN for o in got)
        self.assertGreater(lore, 800 * 0.05)
        self.assertLess(lore, 800 * 0.30)                                   # before the step: 75 % of these turns
        self.assertFalse([o for o in (self.offer(seed=s) for s in range(200)) if o is rumour_speech.LORE_TURN])   # no pages: never
        self.assertFalse([o for o in (self.offer(seed=s, lore_weight=1.0, flow=False) for s in range(50))
                          if o is rumour_speech.LORE_TURN])                 # switch off: select never answers that

    def test_idle_talk_is_not_given_the_that_one_wording(self):
        o = self.offer()
        self.assertNotIn("that one", o.block)
        self.assertIn("give no names at all", o.block)
        rid = self.deed("kill_notable", PT - H, char=OTHER, who="Brynn")
        real = next(o for o in (self.offer(seed=s) for s in range(100)) if o.ids == [rid])
        self.assertIn('"they" or "that one"', real.block)                   # a real person's story keeps it

    def test_a_gm_planted_rumour_with_no_name_is_recognised_as_told(self):
        rid = self.deed("gm_planted", PT - H, char="", subject="", text="The bell under Vesper rang twice.", town="Britain")
        say = "They do say the bell under Vesper rang twice in the night."
        old = next(o for o in (self.offer(seed=s, flow=False) for s in range(50)) if o and o.ids == [rid])
        self.assertEqual(rumour_speech.used(say, old), [])        # the defect on live today: told, never counted
        new = next(o for o in (self.offer(seed=s) for s in range(200)) if o.ids == [rid])
        self.assertEqual(rumour_speech.used(say, new), [rid])
        self.assertEqual(new.source[rid], "gm_planted")
        self.assertEqual(rumour_speech.used("Nothing to speak of today.", new), [])


class Handler(unittest.TestCase):
    """The reply path with the flow on: told and marked, the fixed wording, the GM listing - and off = as before."""

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
        self.r = rumours.Rumours(self.led, CFG)
        self.m = madeup.MadeUp(self.led, self.r)
        self.logs, self.prompts = [], []
        self.patches = [mock.patch.object(brain, "LEDGER", self.led), mock.patch.object(brain, "RUMOURS", self.r),
                        mock.patch.object(brain, "MADEUP", self.m),
                        mock.patch.object(brain, "log", lambda **k: self.logs.append(k))]
        for p in self.patches:
            p.start()

    def tearDown(self):
        for p in self.patches:
            p.stop()
        self.led.db.close()
        self.led.rdb.close()
        self.dir.cleanup()

    def post(self, path, body):
        req = urllib.request.Request(f"http://127.0.0.1:{self.srv.server_address[1]}{path}",
                                     json.dumps(body).encode(), {"Content-Type": "application/json"})
        return json.loads(urllib.request.urlopen(req, timeout=20).read())

    def news(self, reply, flow=True, mode="news", **rask):
        """Ask "What news?"; `reply` is what the model says - a string, or a function of the note it was shown."""
        def fake(persona, utterance, *a, **k):
            text = " ".join(str(x) for x in list(a) + list(k.values()) if isinstance(x, str))
            self.prompts.append(text)
            note = next((ln[2:] for ln in text.split("\n") if ln.startswith("- ")), "")
            return (reply(note) if callable(reply) else reply), "none"
        body = {"request_id": f"r{len(self.prompts)}", "npc_id": NPC, "subject_id": WREN, "persona_key": "townsfolk",
                "persona_vars": {"name": "Osric", "role": "tanner", "town": "Britain", "trade": "vendor"},
                "utterance": "What news?", "budget_ms": 8000,
                "rumours": {"town": "Britain", "pt": PT, "mode": mode, "roster": [], "exclude": [], "recent": [], **rask}}
        with mock.patch.object(brain, "ollama_chat", fake), mock.patch.dict(brain.CFG, {"madeup_flow": flow}), \
                mock.patch.object(smalltalk, "ACTION_SHARE", 0.0):
            return self.post("/v1/npc/respond", body)

    def test_default_is_off(self):
        self.assertIs(brain.DEFAULTS["madeup_flow"], False)

    def test_told_in_the_models_own_words_is_marked_with_its_id(self):
        out = self.news(lambda note: "Folk reckon " + note + ", or so the carters tell it.")
        self.assertTrue(out["ok"])
        ids = out["rumour"]["ids"]
        self.assertEqual(len(ids), 1)
        self.assertTrue(madeup.key_of(ids[0]))
        self.assertEqual(out["rumour"]["mode"], "news")
        self.assertIn("Idle talk going round town (nobody is sure of it - notes only):", self.prompts[-1])
        rec = next(x for x in self.logs if x.get("event") == "respond")
        self.assertIn(rec["rumour_source"][ids[0]], ("made_up", "one_off"))
        self.assertNotIn("fixed_wording", rec["rumour_source"])

    def test_a_reply_that_did_not_tell_it_becomes_the_fixed_wording_and_is_marked(self):
        notes = []

        def dodge(note):
            notes.append(note)
            return "Nothing much to speak of, friend."
        out = self.news(dodge)
        self.assertTrue(out["ok"])
        self.assertEqual(len(out["rumour"]["ids"]), 1)                       # marked heard ...
        self.assertIn(notes[-1].rstrip(".")[1:], out["say"])                 # ... because it WAS said, in the fixed wording
        self.assertNotIn("Nothing much", out["say"])
        rec = next(x for x in self.logs if x.get("event") == "respond")
        self.assertTrue(rec["rumour_source"]["fixed_wording"])

    def test_a_generic_reply_that_brushes_one_word_of_the_story_gets_no_false_receipt(self):
        # Codex code R1, RUM-C3 - through the real handler: the model answers with ONE incidental word of the story it
        # was shown. Before: the story was marked told and the generic line was spoken. Now: the fixed wording is.
        notes = []

        def brush(note):
            notes.append(note)
            word = max((w for w in note.replace(",", " ").split() if w.isalpha()), key=len)
            return f"The {word} is quiet today."
        out = self.news(brush)
        self.assertTrue(out["ok"])
        self.assertEqual(len(out["rumour"]["ids"]), 1)
        self.assertIn(notes[-1].rstrip(".")[1:], out["say"])                 # what was SAID is the story itself
        self.assertNotIn("is quiet today", out["say"])
        rec = next(x for x in self.logs if x.get("event") == "respond")
        self.assertTrue(rec["rumour_source"]["fixed_wording"])

    def test_the_fixed_wording_keeps_the_npcs_own_little_action(self):
        notes = []

        def dodge(note):
            notes.append(note)
            return "[mends a net] Nothing much to speak of, friend."
        out = self.news(dodge)
        self.assertTrue(out["say"].startswith("[mends a net] "))
        self.assertIn(notes[-1].rstrip(".")[1:], out["say"])                 # (a frame may capitalise the first letter)
        self.assertNotIn("Nothing much", out["say"])
        self.assertEqual(len(out["rumour"]["ids"]), 1)

    def test_there_are_a_dozen_fixed_openings_and_each_tells_the_story(self):
        text = "a storm of feathers burst out of the pillow-maker's cart"
        o = rumour_speech.Offer()
        o.ids, o.canon["mu-x"] = ["mu-x"], text
        o.keys["mu-x"], o.weak["mu-x"] = madeup.text_keys(text)
        said = {madeup.canonical(text, i / 12) for i in range(12)}
        self.assertEqual(len(said), 12)
        for line in said:
            self.assertEqual(rumour_speech.used(line, o), ["mu-x"], line)

    def test_a_denial_of_the_story_gets_no_receipt_and_the_telling_does(self):
        # Codex code R2, RUM-C3 A - through the real handler, on whatever nameless story the draw offers: the model
        # denies it using the story's own words. What is finally said must be the story, in the fixed wording.
        notes = []

        def deny(note):
            notes.append(note)
            words = [w for w in note.replace(",", " ").split() if w.isalpha() and len(w) > 3][:4]
            return "It is not true that " + " ".join(words) + ", not a word of it."
        out = self.news(deny)
        self.assertTrue(out["ok"])
        self.assertEqual(len(out["rumour"]["ids"]), 1)
        self.assertNotIn("not true", out["say"])
        self.assertIn(notes[-1].rstrip(".")[1:], out["say"])
        rec = next(x for x in self.logs if x.get("event") == "respond")
        self.assertTrue(rec["rumour_source"]["fixed_wording"])
        self.logs.clear()
        out = self.news(lambda note: "Well now - " + note + ", so the carters tell it.")       # the affirmation
        rec = next(x for x in self.logs if x.get("event") == "respond")
        self.assertNotIn("fixed_wording", rec["rumour_source"])
        self.assertEqual(len(out["rumour"]["ids"]), 1)

    def test_flow_off_is_the_old_nameless_line(self):
        with mock.patch.object(smalltalk, "share", lambda *a: 1.0):
            out = self.news(lambda note: "They reckon " + note, flow=False)
        self.assertIn("Idle talk going round town (made-up chatter, nobody is sure of it): - ", self.prompts[-1])
        self.assertNotIn("rumour", out)                                      # as before the step: nothing is tracked
        self.assertEqual(self.led.db.execute("SELECT COUNT(*) FROM madeup_stories WHERE kind = 'one_off'").fetchone()[0], 0)

    def test_flow_on_never_uses_the_old_nameless_line(self):
        for _ in range(20):
            self.news(lambda note: "They reckon " + note)
            self.assertNotIn("made-up chatter", self.prompts[-1])

    def test_lore_pages_are_drawn_sometimes_and_then_held_back_for_this_player(self):
        pages = [{"page": "Rumours/Britain-And-The-Roads", "title": "Britain and the roads", "heading": "Heard in Britain",
                  "rumour": True, "text": "Tea is dear this season."}]
        brain.LORE_TOLD.clear()
        with mock.patch.object(brain, "lookup_lore", lambda *a, **k: (list(pages), "LORE-PAGES"), create=True), \
                mock.patch.object(brain.LORE, "lookup", lambda *a, **k: list(pages)):
            kinds = []
            for _ in range(60):
                brain.LORE_TOLD.clear()
                out = self.news(lambda note: "They reckon " + note if note else "Tea is dear this season, they say.")
                drawn = [x for x in self.logs if x.get("op") == "lore_turn" and x.get("drawn")]
                kinds.append("lore" if drawn else "story")
                self.logs.clear()
            self.assertGreater(kinds.count("lore"), 1)                      # the pages are still told sometimes ...
            self.assertLess(kinds.count("lore"), 30)                        # ... as one choice among several (was 3 in 4)
            # straight after a lore answer the choice weighs nothing for this player: the next 15 answers are stories
            brain.LORE_TOLD.clear()
            brain.note_lore(WREN)
            self.assertEqual(brain.lore_weight(WREN, 1.0), 0.0)
            self.assertEqual(brain.lore_weight(OTHER, 1.0), 1.0)
            for _ in range(15):
                self.logs.clear()
                self.news(lambda note: "They reckon " + note if note else "Tea is dear this season, they say.")
                self.assertFalse([x for x in self.logs if x.get("op") == "lore_turn"])
        with mock.patch.object(brain.time, "monotonic", lambda: 10_000.0):
            brain.LORE_TOLD[WREN] = 10_000.0 - 700
            self.assertEqual(brain.lore_weight(WREN, 1.0), 0.25)              # ten minutes to two hours: a quarter
            brain.LORE_TOLD[WREN] = 10_000.0 - 8000
            self.assertEqual(brain.lore_weight(WREN, 1.0), 1.0)

    def test_follow_up_after_a_made_up_story(self):
        first = self.news(lambda note: "Folk reckon " + note)
        sid = first["rumour"]["ids"][0]
        out = self.news("Hard to credit, if you ask me.", mode="follow", follow=sid)
        self.assertEqual(out["rumour"]["ids"], [sid])                        # the thread is kept
        self.assertIn("They want to hear more about the tale you passed on", self.prompts[-1])
        self.assertEqual(out["say"], "Hard to credit, if you ask me.")       # a follow-up is a reaction, never rewritten

    def test_gm_planted_nameless_rumour_is_now_marked_heard(self):
        self.led.ingest([{"event_id": "1-1", "epoch": 1, "seq": 1, "npc_id": rumours.WORLD_ID, "char_id": "",
                          "kind": "deed", "facts": {"type": "gm_planted", "subject": "", "count": 1, "who": "", "descriptor": "",
                                                    "place": "", "pt": PT - H, "text": "The bell under Vesper rang twice.",
                                                    "town": "Britain", **NEAR_BRITAIN},
                          "at_utc": "2026-11-11T16:00:00Z", "schema": memory.SCHEMA_VERSION}])
        say = "Aye - they do say the bell under Vesper rang twice."
        with mock.patch.object(rumour_speech, "_weighted", lambda rows, *a, **k: sorted(rows, key=lambda r: r["id"] != "1-1")):
            off = self.news(say, flow=False)
            self.assertEqual(off["rumour"]["ids"], [])                       # live today: told, and not counted
            on = self.news(say, flow=True)
            self.assertEqual(on["rumour"]["ids"], ["1-1"])

    def test_gm_listing_shows_made_up_stories_for_a_town_only(self):
        with mock.patch.dict(brain.CFG, {"madeup_flow": True}):
            town = self.post("/v1/rumours/list", {"pt": PT, "town": "Britain"})
            world = self.post("/v1/rumours/list", {"pt": PT})
        self.assertTrue([r for r in town["rumours"] if r.get("source") == "made_up"])
        self.assertFalse([r for r in world["rumours"] if r.get("source") == "made_up"])
        off = self.post("/v1/rumours/list", {"pt": PT, "town": "Britain"})
        self.assertFalse([r for r in off["rumours"] if r.get("source") == "made_up"])


if __name__ == "__main__":
    unittest.main()
