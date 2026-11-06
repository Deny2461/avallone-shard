"""Phase 5b: speaking rumours - selection, one named person per reply, sentinel binding and reply checks
(Codex 5b design review 2026-10-29, findings 1-5 and its test list). Run: python3 test_rumour_speech.py"""
import os
import tempfile
import unittest

import memory
import rumour_speech as rs
import rumours

CFG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "rumours.json")
WREN, ALT, OTHER = ("22222222-2222-2222-2222-222222222222", "44444444-4444-4444-4444-444444444444",
                  "33333333-3333-3333-3333-333333333333")
H = 3600
NEAR_BRITAIN = {"map": "Trammel", "x": 1492, "y": 1641}
ROSTER = [{"id": WREN, "name": "Racer X"}, {"id": ALT, "name": "Brynn Handy"}, {"id": OTHER, "name": "Thandor"},
          {"name": "Quillon"}]   # a never-deeded alt: name only, no id yet
WORDS = frozenset({"racer", "handy", "will", "the", "boots"})


def strict(fn):
    """Run a test under the strict 5b/5c-1 name rules (rumour_free_names off: the rollback setting)."""
    def run(self, *a, **k):
        old = rs.FREE_NAMES
        rs.FREE_NAMES = False
        try:
            return fn(self, *a, **k)
        finally:
            rs.FREE_NAMES = old
    run.__name__ = fn.__name__
    return run


class SpeechTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.led = memory.Ledger(os.path.join(self.dir.name, "m.db"))
        self.led.reconcile(1, 0, 0)
        self.r = rumours.Rumours(self.led, CFG)
        self.seq = 0

    def tearDown(self):
        self.led.db.close()
        self.led.rdb.close()
        self.dir.cleanup()

    def deed(self, typ, pt, char=WREN, who="Racer X", subject="dragon", descriptor="a tamer", **extra):
        self.seq += 1
        facts = {"type": typ, "subject": subject, "count": 1, "who": who, "descriptor": descriptor, "place": "Destard",
                 "pt": pt, **NEAR_BRITAIN, **extra}
        acked, rej = self.led.ingest([{
            "event_id": f"1-{self.seq}", "epoch": 1, "seq": self.seq, "npc_id": rumours.WORLD_ID, "char_id": char,
            "kind": "deed", "facts": facts, "at_utc": "2026-10-28T16:00:00Z", "schema": memory.SCHEMA_VERSION}])
        self.assertEqual(rej, [])
        return f"1-{self.seq}"

    def offer(self, speaker=WREN, mode="news", pt=H, town="Britain", **kw):
        return rs.select(self.r, pt, self.r.resolve_town(town), speaker, mode, roster=ROSTER, **kw)

    # ---- gossip by name (the owner, chat, 2026-10-30): ----
    def test_about_offers_only_that_persons_stories(self):
        import random as _r
        self.deed("kill_notable", 0, char=OTHER, who="Thandor", descriptor="a warrior")
        self.deed("fished_boots", 60, char=ALT, who="Brynn Handy", subject="old boots", count=3, descriptor="a fisher")
        for i in range(20):
            o = self.offer(speaker=WREN, mode="about", about=ALT, rng=_r.Random(i))
            self.assertEqual(o.mode, "about")
            self.assertEqual(o.char_id, ALT)
            self.assertEqual(len(o.ids), 1)
            self.assertIn("boots", o.block)
            self.assertNotIn("dragon", o.block)                   # Thandor's story never rides along
            self.assertNotIn("Brynn", o.block)                    # the name only as the sentinel
            self.assertIn(o.sentinel, o.block)
            self.assertIsNone(rs.check(f"Oh, {o.sentinel}? Boots, more like.", o, ROSTER, WORDS))
            self.assertEqual(rs.check("Oh, Brynn Handy? Boots, more like.", o, ROSTER, WORDS), "player name")

    def test_about_someone_with_no_gossip(self):
        self.deed("kill_notable", 0, char=OTHER, who="Thandor", descriptor="a warrior")
        o = self.offer(speaker=WREN, mode="about", about=ALT)
        self.assertEqual(o.ids, [])
        self.assertIn("heard no gossip at all about that person", o.block)
        self.assertIn(o.sentinel, o.block)
        self.assertNotIn("dragon", o.block)
        self.assertEqual(rs.wire_reply(o, [])["ids"], [])        # nothing told: no heard timer, no cap

    def test_about_binds_the_asked_name_only(self):
        o = self.offer(speaker=WREN, mode="about", about=ALT)
        said = rs.bind_asked("I have heard nothing of Brynn Handy, nor of brynn.", o, ROSTER)
        self.assertEqual(said, f"I have heard nothing of {o.sentinel}, nor of {o.sentinel}.")
        self.assertIsNone(rs.check(said, o, ROSTER, WORDS))
        other = rs.bind_asked("Brynn Handy and Thandor, both fools.", o, ROSTER)
        self.assertEqual(rs.check(other, o, ROSTER, WORDS), "player name")   # someone else: still refused
        news = self.offer(speaker=WREN, mode="news") or rs.Offer()
        self.assertEqual(rs.bind_asked("Brynn Handy", news, ROSTER), "Brynn Handy")   # only for "about"

    def test_about_a_player_with_no_ai_identity(self):
        o = self.offer(speaker=WREN, mode="about", about="")      # named, but never met an AI NPC: no id
        self.assertEqual((o.sentinel, o.ids), ("", []))
        self.assertIn("without repeating the name", o.block)

    def test_about_yourself_is_you(self):
        self.deed("fished_boots", 60, char=WREN, subject="old boots", count=3, descriptor="a fisher")
        o = self.offer(speaker=WREN, mode="about", about=WREN)
        self.assertTrue(o.is_speaker)
        self.assertEqual(o.sentinel, "")
        self.assertIn('"you"', o.block)

    def test_news_never_retells_a_story_just_heard(self):
        import random as _r
        a = self.deed("kill_notable", 0, char=OTHER, who="Thandor", descriptor="a warrior")
        b = self.deed("fished_boots", 60, char=ALT, who="Brynn Handy", subject="old boots", count=3, descriptor="a fisher")
        for i in range(30):
            o = rs.select(self.r, H, "Britain", WREN, "news", exclude=[a, b], recent=[b], roster=ROSTER, rng=_r.Random(i))
            self.assertNotIn(b, o.ids)                        # just heard: skipped outright
        self.assertIsNone(rs.select(self.r, H, "Britain", WREN, "news", exclude=[a, b], recent=[a, b], roster=ROSTER))
        o = self.offer(speaker=WREN, mode="about", about=ALT, recent=[b])
        self.assertEqual(o.ids, [b])                          # asked for by name: still told

    def test_about_an_ambiguous_name_asks_which_one(self):
        self.deed("fished_boots", 60, char=ALT, who="Brynn Handy", subject="old boots", count=3, descriptor="a fisher")
        o = self.offer(speaker=WREN, mode="about", about="", ambiguous=True)
        self.assertEqual((o.ids, o.sentinel, o.char_id), ([], "", ""))
        self.assertIn("ask them which one they mean", o.block)
        self.assertNotIn("boots", o.block)                     # never guesses whose story it is
        self.assertEqual(rs.check("Which Brynn Handy do you mean?", o, ROSTER, WORDS), "player name")

    def test_about_never_uses_a_vague_story(self):
        self.deed("fished_boots", 60, char=ALT, who="Brynn Handy", subject="old boots", count=3, descriptor="a fisher")
        rows = [dict(r, vague=True) for r in self.r.speech_rows(H, "Britain")]
        o = rs.select_about(self.r, rows, H, ALT, WREN, ROSTER)
        self.assertEqual(o.ids, [])                               # a name-free story cannot be pinned on Brynn

    # ---- finding 1: one named person per reply; a swap is impossible by construction ----
    def test_only_one_named_person_and_no_deed_time_name(self):
        import random as _r
        self.deed("kill_notable", 0, char=OTHER, who="Thandor", descriptor="a warrior")
        self.deed("fished_boots", 60, char=WREN, subject="old boots", count=3, descriptor="a fisher")
        for i in range(50):
            o = rs.select(self.r, H, "Britain", WREN, "news", roster=ROSTER, rng=_r.Random(i))
            self.assertNotIn("Thandor", o.block)              # deed-time names never reach the prompt
            self.assertNotIn("Racer X", o.block)
            self.assertEqual(len({o.char_id}), 1)
            self.assertLessEqual(len(set(rs.SENTINEL_RE.findall(o.block))), 0 if o.is_speaker else 1)
            if o.is_speaker:
                self.assertEqual(o.sentinel, "")              # about the listener: "you", no name at all
    def test_swap_cannot_bind_two_people(self):
        self.deed("kill_notable", 0, char=OTHER, who="Thandor", descriptor="a warrior")
        self.deed("fished_boots", 60, char=ALT, who="Brynn Handy", subject="old boots", count=3, descriptor="a fisher")
        o = self.offer(speaker=WREN)                            # neither rumour is about the speaker
        named = [o.char_id]
        self.assertEqual(len(set(named)), 1)
        # a model that "swaps" can only attach the one sentinel to the vague deed: still one real person, never two -
        # and since Codex 2a finding 2 the slot may not carry a deed that is not its own story at all
        line = f"{o.sentinel} slew a dragon, and a warrior fished up boots"
        if not o.sentinel:                                    # the nameless pair: nobody to pin it on
            self.assertIsNone(rs.check(line, o, ROSTER, WORDS))
        else:   # one person offered: the other deed was never offered, so beside their slot it is invented
            self.assertEqual(rs.check(line, o, ROSTER, WORDS), "claim not in the story")
            self.assertIsNone(rs.check(f"{o.sentinel} " + ("slew a dragon." if o.char_id == OTHER else
                                                         "fished up three old boots."), o, ROSTER, WORDS))
        self.assertEqual(rs.check(f"{o.sentinel} slew a dragon, and Thandor fished up boots", o, ROSTER, WORDS),
                         "player name")

    def test_sentinel_is_random_per_request(self):
        self.deed("kill_notable", 0, char=ALT, who="Brynn Handy")
        a, b = self.offer(speaker=WREN), self.offer(speaker=WREN)
        self.assertTrue(a.sentinel and b.sentinel)
        self.assertNotEqual(a.sentinel, b.sentinel)

    # ---- the reply checks (Codex's test list) ----
    def test_reply_checks(self):
        self.deed("kill_notable", 0, char=ALT, who="Brynn Handy")
        o = self.offer(speaker=WREN)
        ok = f"They say {o.sentinel} slew a dragon near Destard."
        self.assertIsNone(rs.check(ok, o, ROSTER, WORDS))
        self.assertIsNone(rs.check(f"{o.sentinel}'s dragon days are legend.", o, ROSTER, WORDS))   # possessive
        bad = {
            "foreign sentinel": f"⟦P-0000⟧ slew a dragon.",
            "mangled sentinel": "P-" + o.sentinel[3:7] + " slew a dragon.",
            "mangled sentinel ": f"{o.sentinel[:-1]} slew a dragon.",
            "person a": "Person A slew a dragon.",
            "player name": "Brynn Handy slew a dragon.",
            "first name": "Brynn slew a dragon.",
            "near spelling": "Brinn Handy slew a dragon.",
            "never-deeded alt": "Quillon slew a dragon.",
            "lookalike": "Рacer X slew a dragon.",      # Cyrillic R
        }
        for label, text in bad.items():
            self.assertIsNotNone(rs.check(text, o, ROSTER, WORDS), label)
        # a word that is also part of a name is not a name on its own
        self.assertIsNone(rs.check("Handy work, that. The racer won.", o, ROSTER, WORDS))

    def test_duplicated_and_omitted_sentinel_are_fine(self):
        self.deed("kill_notable", 0, char=ALT, who="Brynn Handy")
        o = self.offer(speaker=WREN)
        self.assertIsNone(rs.check(f"{o.sentinel}, aye, {o.sentinel} again.", o, ROSTER, WORDS))  # one person, twice
        self.assertIsNone(rs.check("Someone slew a dragon, they say.", o, ROSTER, WORDS))       # omitted: no name

    def test_replayed_sentinel_from_another_request_refused(self):
        self.deed("kill_notable", 0, char=ALT, who="Brynn Handy")
        a, b = self.offer(speaker=WREN), self.offer(speaker=WREN)
        self.assertEqual(rs.check(f"{a.sentinel} slew a dragon.", b, ROSTER, WORDS), "foreign sentinel")

    def test_no_offer_no_checks(self):
        self.assertIsNone(rs.check("Racer X bought bread.", None, ROSTER, WORDS))   # ordinary talk: today's behaviour

    # ---- finding 3: names come from the shard ----
    def test_fallback_uses_current_name_from_roster_not_deed_time(self):
        self.deed("kill_notable", 0, char=ALT, who="Old Name")
        o = self.offer(speaker=WREN, name_mode="name")
        self.assertEqual(o.name, "Brynn Handy")
        self.assertIn("Brynn Handy", o.block)
        self.assertNotIn("Old Name", o.block)
        self.assertIsNone(rs.check("Brynn Handy slew a dragon.", o, ROSTER, WORDS))
        self.assertEqual(rs.check("Thandor slew a dragon.", o, ROSTER, WORDS), "player name")

    def test_fallback_without_a_current_name_keeps_the_sentinel(self):
        self.deed("kill_notable", 0, char="55555555-5555-5555-5555-555555555555", who="Gone")  # deleted: not in roster
        o = self.offer(speaker=WREN, name_mode="name")
        self.assertEqual(o.name, "")
        self.assertTrue(o.sentinel)
        self.assertEqual(o.descriptor, "a tamer")             # the shard fills the descriptor for an unknown id

    # ---- finding 4: town from the shard; unknown town = nothing ----
    def test_unknown_town_gives_nothing(self):
        self.deed("kill_notable", 0)
        self.assertIsNone(self.offer(town="Despise"))
        self.assertIsNone(self.offer(town=""))
        self.assertIsNotNone(self.offer(town="britain"))
        self.assertEqual(self.r.resolve_town("Ocllo"), self.r.resolve_town("Haven") or self.r.resolve_town("Ocllo"))

    # ---- modes and caps (finding 5, the owner decision 2) ----
    def test_greet_own_first_then_others_and_respects_exclude(self):
        rid = self.deed("kill_notable", 0, char=ALT, who="Brynn Handy")
        o = self.offer(speaker=WREN, mode="greet")                          # about someone else: allowed now (the owner 2026-10-29)
        self.assertEqual(o.ids, [rid])
        self.assertFalse(o.is_speaker)
        self.assertTrue(o.tease and o.sentinel in o.tease)                # 2026-11-02: a tease naming them, no tale
        self.assertNotIn("Brynn", o.block)
        self.assertNotIn("Town gossip", o.block)
        mine = self.deed("fished_boots", 30, char=WREN, subject="old boots", count=3, descriptor="a fisher")
        import random as _r
        firsts = [rs.select(self.r, H, "Britain", WREN, "greet", roster=ROSTER, rng=_r.Random(i)).ids[0] for i in range(200)]
        self.assertTrue(mine in firsts and rid in firsts)                  # a weighted draw, no streak
        self.assertEqual(self.offer(speaker=WREN, mode="greet", exclude=[mine]).ids, [rid])
        self.assertIsNone(self.offer(speaker=WREN, mode="greet", exclude=[mine, rid]))   # heard both recently

    def test_angle_and_no_stock_openings(self):
        self.deed("fished_boots", 0, char=WREN, subject="old boots", count=3, descriptor="a fisher")
        o = self.offer()
        self.assertIn("How you tell it this time: " + o.angle, o.block)
        self.assertIn('"They say"', o.block)
        self.assertIn("do not copy the wording", o.block)
        self.assertIn("never say \"he\", \"she\"", o.block)

    def test_own_rumour_offered_alone_and_called_you(self):
        import random as _r
        self.deed("kill_notable", 0, char=OTHER, who="Thandor")
        mine = self.deed("drunk", 30, char=WREN, subject="", place="Britain")
        seen = 0
        for i in range(100):
            o = rs.select(self.r, H, "Britain", WREN, "news", roster=ROSTER, rng=_r.Random(i))
            if o.is_speaker:
                seen += 1
                self.assertEqual(o.ids[0], mine)              # the listener's own story leads; any second is vague
                self.assertNotIn("Thandor", o.block)
                self.assertIn('Call the person you are talking to "you"', o.block)
                self.assertNotIn('"that one"', o.block)
            else:
                self.assertNotIn(mine, o.ids)
        self.assertGreater(seen, 10)
    def test_news_mix_no_streaks(self):
        import random as _r
        self.deed("drunk", 0, char=OTHER, who="Thandor", subject="", place="Britain")
        mine = self.deed("fished_boots", 30, char=WREN, subject="old boots", count=3, descriptor="a fisher")
        def share_own(exclude):
            got = [rs.select(self.r, H, "Britain", WREN, "news", exclude=exclude, roster=ROSTER, rng=_r.Random(i)).is_speaker
                   for i in range(400)]
            return sum(got) / len(got)
        fresh, heard = share_own([]), share_own([mine])
        self.assertGreater(fresh, 0.5)                         # own story boosted (2x vs 1x)
        self.assertGreater(heard, 0.05)                        # heard: damped, not ruled out
        self.assertLess(heard, fresh)
        self.assertLess(heard, 0.5)                            # someone else's story is now the likelier
    def test_follow_depth_and_vague_second_story(self):
        M = rs.MARKER
        h = [{"speaker": "player", "text": "news?"}, {"speaker": "npc", "text": M},
             {"speaker": "player", "text": "more"}, {"speaker": "npc", "text": M}]
        self.assertEqual(rs.follow_depth(h), 2)
        self.assertEqual(rs.follow_depth(h + [{"speaker": "npc", "text": "Good day."}]), 0)
        self.assertEqual(rs.follow_depth([]), 0)
        import random as _r
        self.deed("kill_notable", 0, char=OTHER, who="Thandor", descriptor="a warrior")
        self.deed("drunk", 10, char=ALT, who="Brynn Handy", subject="", place="Britain")
        two = [o for o in (rs.select(self.r, H, "Britain", WREN, "news", roster=ROSTER, rng=_r.Random(i))
                           for i in range(200)) if len(o.ids) == 2]
        self.assertEqual(two, [])   # the owner's dev test 2026-11-02: the nameless pair is off - one story per news reply

    # ---- Codex code review 2026-10-30 ----
    def _offers(self, speaker, mode="news", n=300, **kw):
        import random as _r
        return [rs.select(self.r, H, "Britain", speaker, mode, roster=ROSTER, rng=_r.Random(i), **kw) for i in range(n)]

    def test_codex1_named_person_never_beside_another_persons_deed(self):
        self.deed("fished_boots", 0, char=ALT, who="Brynn Handy", subject="old boots", count=3, descriptor="a fisher")
        self.deed("kill_notable", 10, char=OTHER, who="Thandor", subject="dragon", descriptor="a warrior")
        chars = {r["id"]: r["char"] for r in self.r.speech_rows(H, "Britain")}
        for o in self._offers(WREN):
            if o.sentinel:   # a named reply holds only that person's deeds
                self.assertEqual({chars[i] for i in o.ids}, {o.char_id})
            self.assertEqual(len(o.ids), 1)   # the owner's dev test 2026-11-02: one story per news reply, never a pair

    def test_one_story_per_news_reply_even_for_one_person(self):
        # 2026-11-02: Diamanta blended a murder and a pet story; one person's two deeds go one per reply too
        self.deed("kill_notable", 0, char=OTHER, who="Thandor", subject="dragon", descriptor="a warrior")
        self.deed("murder", 10, char=OTHER, who="Thandor", descriptor="a warrior")
        for o in self._offers(WREN):
            if o is not None:
                self.assertEqual(len(o.ids), 1)

    def test_no_braggart_on_a_grim_story(self):
        import random as _r
        for i in range(300):
            self.assertNotIn("braggart", rs.pick_angle({"murder"}, False, True, _r.Random(i)))
        self.assertTrue(any("braggart" in rs.pick_angle({"kill_notable"}, False, True, _r.Random(i)) for i in range(300)))

    def test_codex1_listener_never_in_a_pair(self):
        self.deed("fished_boots", 0, char=WREN, subject="old boots", count=3, descriptor="a fisher")
        self.deed("kill_notable", 10, char=OTHER, who="Thandor", subject="dragon", descriptor="a warrior")
        chars = {r["id"]: r["char"] for r in self.r.speech_rows(H, "Britain")}
        for o in self._offers(WREN):
            if o.is_speaker:   # "you" is a person slot too: nobody else's deed beside it
                self.assertEqual({chars[i] for i in o.ids}, {WREN})
            self.assertFalse(WREN in {chars[i] for i in o.ids} and len({chars[i] for i in o.ids}) > 1)

    def test_codex2_context_flag_even_when_nothing_detected(self):
        self.deed("kill_notable", 0, char=WREN, subject="dragon", place="Destard")
        o = self.offer(speaker=WREN)
        paraphrase = "The winged beast will trouble us no more, thanks to you."   # Codex's synonym-only case
        told = rs.used(paraphrase, o)
        w = rs.wire_reply(o, told)
        self.assertIsNotNone(w)                        # the shard is told gossip was in the prompt -> marker only
        self.assertEqual(w["ids"], told)               # heard/caps only for what was detected
        self.assertIsNone(rs.wire_reply(None, []))

    @strict
    def test_codex3_name_guard_without_an_offer(self):
        g = rs.guard(None, True)                       # news / small talk / lore turn: no live offer
        self.assertEqual(rs.check("Racer X paid for everyone's drinks and slipped away before dawn.", g, ROSTER, WORDS),
                         "player name")
        self.assertIsNone(rs.check("A stranger paid for everyone's drinks.", g, ROSTER, WORDS))
        self.assertIsNone(rs.guard(None, False))       # ordinary shop talk: unchanged

    def test_listener_named_only_as_an_address(self):
        # Play test 2026-10-31: "I remember your face, <listener>" + gossip was refused twice. Codex 2a finding 2: the
        # listener is exempt only as an ADDRESS - said about them, their name is checked like anyone's.
        L = "Racer X"
        self.assertEqual(rs.listener_name(ROSTER, WREN), L)
        self.assertEqual(rs.listener_name(ROSTER, WREN.replace("-", "").upper()), L)
        self.assertEqual(rs.listener_name(ROSTER, ""), "")
        g = rs.guard(None, True, "news")                                      # strict: every name checked
        def chk(text, o=g):
            return rs.check(rs.mask_listener(text, L), o, ROSTER, WORDS)
        for ok in ("I remember your face, Racer X. A stranger paid for everyone's drinks.", "Good to see you again, Racer X.",
                   "You are Racer X.", "Welcome back, Racer X!", "Racer X, a word with you.", "[nods] Racer X, sit down."):
            self.assertIsNone(chk(ok), ok)
        self.assertEqual(rs.check(ok, g, ROSTER, WORDS), "player name")      # positive control: unmasked is refused
        for bad in ("Racer X paid for everyone's drinks.", "I hear Racer X paid for everyone's drinks.",
                    "Racer X, Brynn Handy was asking for you."):
            self.assertIsNotNone(chk(bad), bad)
        self.assertEqual(rs.scrub("Good evening, Racer X. Racer X paid for the drinks.", ROSTER, keep=L),
                         "Good evening, Racer X. that one paid for the drinks.")
        self.deed("kill_notable", 0, char=OTHER, who="Thandor", descriptor="a warrior")   # someone else's story
        o = self.offer(speaker=WREN, mode="greet")
        self.assertTrue(o is not None and o.sentinel)                          # the greet+gossip case ran
        said = f"Welcome back, Racer X. {o.sentinel} slew a dragon, they say."
        self.assertEqual(rs.check(said, o, ROSTER, WORDS), "player name")
        self.assertIsNone(chk(said, o))
        self.assertIsNotNone(chk(f"Welcome back. Racer X slew a dragon, and {o.sentinel} saw it.", o))

    def test_free_names_in_ordinary_talk(self):
        # the owner, chat, 2026-10-31: - no gossip on offer, no name check at all.
        self.assertTrue(rs.FREE_NAMES)                                 # the shipped default
        g = rs.guard(None, True, "greet")
        self.assertEqual(g.mode, rs.MODE_FREE)
        for ok in ("Racer X and Brynn Handy were in here earlier.", "Say hello to Thandor for me.",
                   "Brynn Handy? A fisher, I think.", "Thandor was asking after you."):
            self.assertIsNone(rs.check(ok, g, ROSTER, WORDS), ok)
        # Codex 2a finding 2: no record says it - a deed beside a real name is refused (and the handler scrubs it)
        for bad in ("Thandor paid for everyone's drinks.", "Brynn stole a purse yesterday.",
                    "Racer X was drunk last night.", "Quillon killed a guard."):
            self.assertEqual(rs.check(bad, g, ROSTER, WORDS), "player name (claim)", bad)
        self.assertEqual(rs.check("Someone paid for everyone's drinks.", g, ROSTER, WORDS), None)

    def test_free_names_news_without_a_story_stays_strict(self):
        # Codex 5b finding 3's case: "What news of Racer X?" answered from a made-up seed must not invent a deed for them.
        g = rs.guard(None, True, "news")
        self.assertIsNotNone(g)
        self.assertEqual(rs.check("Racer X paid for everyone's drinks and slipped away before dawn.", g, ROSTER, WORDS),
                         "player name")
        self.assertIsNone(rs.check("A stranger paid for everyone's drinks.", g, ROSTER, WORDS))
        self.assertEqual(rs.scrub("Racer X paid for everyone's drinks.", ROSTER), "that one paid for everyone's drinks.")

    def test_free_follow_up_names_the_story_person_only(self):
        # Play test 2026-10-31: "Who was it?" -> "I don't know who that one was." Now the story's own person is named.
        self.deed("fished_boots", 0, char=ALT, who="Brynn Handy", subject="old boots", count=3, descriptor="a fisher")
        rid = "1-1"
        o = self.offer(speaker=WREN, mode="follow", follow=rid)
        self.assertTrue(o.sentinel)
        self.assertEqual(o.char_id, ALT)
        self.assertIn(f"If they ask who it was, it was {o.sentinel}.", o.block)
        self.assertIn("no other person", o.block)
        self.assertNotIn("Brynn", o.block)                             # deed-time names still never reach the prompt
        self.assertIsNone(rs.check(f"That was {o.sentinel}, or so the fishers say.", o, ROSTER, WORDS))
        self.assertIsNone(rs.check(rs.mask_listener(f"That was {o.sentinel}. Racer X, you would laugh.", "Racer X"), o,
                                   ROSTER, WORDS))
        refused = {
            "wrong person": "That was Thandor, or so they say.",
            "real name typed instead of the slot": "That was Brynn Handy, I hear.",
            "foreign sentinel": "That was ⟦P-ab12⟧.",
            "new deed": f"{o.sentinel} was drunk when they did it.",
            "new place": f"{o.sentinel} was boasting about it in Yew.",
        }
        for label, text in refused.items():
            self.assertIsNotNone(rs.check(rs.mask_listener(text, "Racer X"), o, ROSTER, WORDS), label)

    def test_codex2a_follow_up_slot_only_answers_who_or_the_story(self):
        # Codex 2a finding 2: with only an old-boot record, "<slot> stole a purse yesterday." passed.
        self.deed("fished_boots", 0, char=ALT, who="Brynn Handy", subject="old boots", count=1, descriptor="a fisher")
        o = self.offer(speaker=WREN, mode="follow", follow="1-1")
        S = o.sentinel
        for ok in (f"It was {S}.", f"[nods] It was {S}, so I heard.", f"It was {S} who did that.", f"[nods toward the window] {S} was the one.", f"That was {S}, or so the fishers say.",
                   f"{S} pulled up an old boot, they say.", f"It was {S}. I would not want those boots myself."):
            self.assertIsNone(rs.check(ok, o, ROSTER, WORDS), ok)
        for bad in (f"{S} stole a purse yesterday.", f"{S} pulled up a boot and stole a purse.",
                    f"It was {S}. They stole a purse too.", f"{S} is a known cheat.", f"I would not trust {S} near my till.",
                    f"It was {S}, who was drunk at the time and robbed a man."):
            self.assertIsNotNone(rs.check(bad, o, ROSTER, WORDS), bad)
        fb = rs.follow_fallback(o, "Who was it?")
        self.assertEqual(fb, f"It was {S}, so I heard. That is all I know of it.")
        self.assertIsNone(rs.check(fb, o, ROSTER, WORDS))
        self.assertEqual(rs.follow_fallback(o, "tell me more"), "That is all I know of it.")

    def test_codex2a_round2_address_syntax_and_whole_reply(self):
        L = "Racer X"
        self.assertEqual(rs.mask_listener("At dawn, Racer X killed a guard.", L), "At dawn, Racer X killed a guard.")
        self.assertEqual(rs.mask_listener("Good to see you again, Racer X. Hello Racer X, sit.", L),
                         "Good to see you again, you. Hello you, sit.")
        g = rs.guard(None, True, "greet")
        self.assertEqual(rs.check("Thandor was here. They stole a purse.", g, ROSTER, WORDS), "player name (claim)")
        self.assertIsNone(rs.check("Thandor was here earlier, asking after you.", g, ROSTER, WORDS))
        self.deed("fished_boots", 0, char=ALT, who="Brynn Handy", subject="old boots", count=1, descriptor="a fisher")
        f = self.offer(speaker=WREN, mode="follow", follow="1-1")
        self.assertIsNotNone(rs.check(f"It was {f.sentinel}, who abducted a child.", f, ROSTER, WORDS))
        self.assertIsNotNone(rs.check(f"It was {f.sentinel}, and they lied about it.", f, ROSTER, WORDS))
        import random as _r
        o = next(x for x in (rs.select(self.r, H, "Britain", WREN, "news", roster=ROSTER, rng=_r.Random(i)) for i in range(40))
                 if x and x.sentinel)
        self.assertIsNotNone(rs.check(f"{o.sentinel} fished up a boot. They murdered a guard.", o, ROSTER, WORDS))
        self.assertIsNone(rs.check(f"{o.sentinel} fished up a boot. Lucky, I suppose.", o, ROSTER, WORDS))

    def test_codex2a_story_slot_cannot_carry_another_deed(self):
        self.deed("fished_boots", 0, char=ALT, who="Brynn Handy", subject="old boots", count=3, descriptor="a fisher")
        o = self.offer(speaker=WREN, mode="news")
        if not o.sentinel:                     # a nameless pair: draw again until the named form comes up
            import random as _r
            o = next(x for x in (rs.select(self.r, H, "Britain", WREN, "news", roster=ROSTER, rng=_r.Random(i))
                                 for i in range(40)) if x and x.sentinel)
        S = o.sentinel
        self.assertIsNone(rs.check(f"{S} fished up three old boots, the fool.", o, ROSTER, WORDS))
        self.assertIsNotNone(rs.check(f"{S} fished up three old boots and stole a purse.", o, ROSTER, WORDS))

    def test_free_follow_up_on_own_story_is_still_you(self):
        self.deed("fished_boots", 0, char=WREN, subject="old boots", count=3, descriptor="a fisher")
        o = self.offer(speaker=WREN, mode="follow", follow="1-1")
        self.assertTrue(o.is_speaker)
        self.assertEqual(o.sentinel, "")
        self.assertNotIn("If they ask who", o.block)

    @strict
    def test_codex4_follow_up_cannot_add_facts(self):
        rid = self.deed("kill_notable", 0, char=ALT, who="Brynn Handy", subject="dragon", place="Destard")
        o = self.offer(speaker=WREN, mode="follow", follow=rid)
        self.assertEqual(o.sentinel, "")               # no name slot on a follow-up
        self.assertNotIn("who told you", o.block)
        self.assertIn("React to it only", o.block)
        bad = {
            "name": "Brynn Handy was drunk when they did it.",
            "sentinel": "⟦P-ab12⟧ was drunk when they did it; a guard saw them boasting.",
            "new deed (condition)": "That one was drunk when they did it, mark me.",
            "new place": "I heard they were boasting about it in Yew.",
            "new deed (murder)": "Some say they murdered a guard on the way back.",
        }
        for label, text in bad.items():
            self.assertIsNotNone(rs.check(text, o, ROSTER, WORDS), label)
        for ok in ("I will believe it when I see a dragon's tooth on my counter.",
                   "Slain a dragon, they say. Good for business at the forge.",
                   "Destard is no place for me, I can tell you that."):
            self.assertIsNone(rs.check(ok, o, ROSTER, WORDS), ok)

    def test_codex4_own_follow_up_is_you_without_a_name(self):
        rid = self.deed("drunk", 0, char=WREN, subject="", place="Britain")
        o = self.offer(speaker=WREN, mode="follow", follow=rid)
        self.assertTrue(o.is_speaker)
        self.assertEqual(o.sentinel, "")
        self.assertIn('"you"', o.block)

    def test_scrub_swaps_real_names_only(self):
        self.assertEqual(rs.scrub("I know naught of Thandor, stranger.", ROSTER), "I know naught of that one, stranger.")
        self.assertEqual(rs.scrub("I have no knowledge of a Wren Ashby.", ROSTER + [{"name": "Wren Ashby"}]),
                         "I have no knowledge of that one.")
        self.assertEqual(rs.scrub("I know naught of a man named Racer X.", ROSTER), "I know naught of a man named that one.")
        self.assertEqual(rs.scrub("Fine weather for fishing.", ROSTER), "Fine weather for fishing.")
        g = rs.guard(None, True)
        self.assertIsNone(rs.check(rs.scrub("Racer X paid for everyone's drinks.", ROSTER), g, ROSTER, WORDS))

    def test_no_owes_coin_angle(self):
        self.assertFalse(any("owes" in a for a, _ in rs.ANGLES_ANY + rs.ANGLES_SPEAKER + rs.ANGLES_OTHER))

    def test_no_cheery_angle_for_a_death(self):
        import random as _r
        cheery = {a for a, c in rs.ANGLES_ANY + rs.ANGLES_SPEAKER + rs.ANGLES_OTHER if c}
        for seed in range(200):
            self.assertNotIn(rs.pick_angle(["murder"], True, False, _r.Random(seed)), cheery)

    def test_weight_orders_news(self):
        import random as _r
        self.deed("fished_boots", 0, char=OTHER, who="Thandor", subject="old boots", count=3, descriptor="a fisher")
        self.deed("kill_notable", 0, char=ALT, who="Brynn Handy")
        firsts = [rs.select(self.r, H, "Britain", WREN, "news", roster=ROSTER, rng=_r.Random(i)).char_id for i in range(300)]
        self.assertGreater(firsts.count(ALT), firsts.count(OTHER))   # weight 3 beats weight 1

    def test_follow_up_reoffers_only_a_live_rumour(self):
        rid = self.deed("kill_notable", 0)
        self.assertEqual(self.offer(mode="follow", follow=rid).ids, [rid])
        self.r.drop(rid, "2026-10-29T00:00:00Z")
        self.assertIsNone(self.offer(mode="follow", follow=rid))           # GM-dropped: gone from speech too
        rid2 = self.deed("kill_notable", 10, subject="lich")
        expired = self.r.cfg["deeds"]["kill_notable"].get("lifetime_h", 24) * H + 20 * H
        self.assertIsNone(self.offer(mode="follow", follow=rid2, pt=int(expired)))   # expired

    def test_restore_removes_it_from_speech(self):
        self.deed("kill_notable", 0)
        self.assertIsNotNone(self.offer(pt=H))
        self.assertIsNone(self.offer(pt=-1))                  # clock rewound past the deed (world restore)

    def test_block_size_bounded_and_two_rows_max(self):
        for i, s in enumerate(["dragon", "lich", "balron", "titan", "kraken"]):
            self.deed("kill_notable", i * 60, subject=s)
        o = self.offer(pt=H)
        self.assertLessEqual(len(o.ids), rs.MAX_ROWS)
        self.assertLess(len(o.block), rs.MAX_CHARS + 600)

    def test_nickname_line_for_the_speaker(self):
        for i in range(12):
            self.deed("kill_notable", i * 30)
        o = self.offer(pt=2 * H)
        self.assertIn("call the person you are talking to", o.block)


    # ---- dev play test 2026-10-29: offered is not told ----
    def test_only_told_rumours_count(self):
        rid = self.deed("fished_boots", 0, subject="old boots", count=3, descriptor="a fisher")
        o = self.offer(speaker=WREN)
        self.assertEqual(rs.used("They say the Crown will tax Tokuno tea.", o), [])
        self.assertEqual(rs.used("I hear you pulled three old boots from the water!", o), [rid])
        self.assertEqual(rs.used("Fishing again, are you?", o), [rid])          # "fish" stem of the deed text
        dr = self.deed("drunk", 5, char=ALT, who="Brynn Handy", subject="", place="Britain")
        import random as _r
        o = next(x for x in (rs.select(self.r, H, "Britain", ALT, "news", roster=ROSTER, rng=_r.Random(i))
                             for i in range(100)) if dr in x.ids)
        self.assertIn(dr, rs.used("That one was seen quite intoxicated.", o))   # told_words
        d = self.deed("kill_notable", 10, char=WREN, subject="dragon")
        import random as _r
        o = next(x for x in (rs.select(self.r, H, "Britain", WREN, "news", roster=ROSTER, rng=_r.Random(i))
                             for i in range(100)) if d in x.ids)
        self.assertIn(d, rs.used("You felled that beast near Destard!", o))      # the place counts too

    def test_sentinel_counts_as_told(self):
        rid = self.deed("kill_notable", 0, char=ALT, who="Brynn Handy")
        o = self.offer(speaker=WREN)
        self.assertEqual(rs.used(f"{o.sentinel} did a great thing.", o), [rid])
        self.assertEqual(rs.used("Nothing much happens here.", o), [])


class Tease(unittest.TestCase):
    """the owner, chat, 2026-11-02: a greeting teases; the tale only if they ask."""

    def test_self_and_vague_teasers_and_the_reveal_wording(self):
        self.assertTrue(all("{t}" in t for t in rs.TEASE_OTHER))
        self.assertFalse(any("{t}" in t for t in rs.TEASE_SELF + rs.TEASE_VAGUE))


if __name__ == "__main__":
    unittest.main()
