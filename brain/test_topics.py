"""Stage A3 step 2 (2026-11-10): which broad subjects two speakers have talked about (topics.py, acquaint.py, memory.py).
Covers design v2 and Codex design R1/R2: the permanent id registry; the keyword classifier (whole words, top two, ties,
absent vs empty vs ids, opener never counted, opener echo); ingest that CLEANS `topics` and never rejects over it, without
the classifier file; the control that the step-1 validator does refuse such a row; the reader (union by day, restart
duplicates, ranking, same bounded pass, isolation); fresh subjects offered instead of covered ones named; every optional failure costing only the subjects;
and both prompt paths through the real handlers with a faked model. Run: python3 -m unittest test_topics"""
import json
import os
import tempfile
import unittest
from unittest import mock

import test_acquaint as ta
from test_acquaint import CHAR, ELS, NPC, oid, plain, talked  # noqa: F401

import acquaint
import brain
import cards
import memory
import topics

HOUR = 3600.0
FADE = 120 * HOUR


def trow(epoch, seq, tp, **kw):
    ev = talked(epoch, seq, **kw)
    ev["facts"]["topics"] = tp
    return ev


class Registry(unittest.TestCase):
    # Append-only (Codex S2-D1). When an id is ADDED, add a new line here; never edit an older one.
    AS_OF_2026_11_10 = ("road", "weather", "trade", "food", "healing", "work", "animals", "sea", "town", "family", "rest",
                        "clothes", "danger", "magic", "land")

    def test_ids_are_never_removed_renamed_or_moved(self):
        self.assertEqual(acquaint.TOPIC_IDS[:len(self.AS_OF_2026_11_10)], self.AS_OF_2026_11_10)
        self.assertEqual(len(set(acquaint.TOPIC_IDS)), len(acquaint.TOPIC_IDS))
        for t in acquaint.TOPIC_IDS:
            self.assertRegex(t, r"^[a-z]{1,16}$")           # the shard keeps only ids of this shape

    def test_the_shipped_file_covers_the_registry_and_nothing_else(self):
        topics.reset()
        cfg = topics.load()
        self.assertEqual(cfg["ignored"], [])
        self.assertEqual(tuple(cfg["topics"]), acquaint.TOPIC_IDS)
        for tid, t in cfg["topics"].items():
            self.assertTrue(t["label"] and t["rx"], tid)


class Classifier(unittest.TestCase):
    def setUp(self):
        topics.reset()
        self.cfg = topics.load()

    def c(self, *lines):
        return topics.classify(lines, self.cfg)

    def test_each_topic_hits_on_a_plain_line(self):
        lines = {"road": "The road was long.", "weather": "It looks like rain.", "trade": "Prices are up at the market.",
                 "food": "Fresh bread today.", "healing": "Keep your bandages dry.", "work": "The forge keeps me busy.",
                 "animals": "That horse needs shoeing.", "sea": "The tide is out.", "town": "The streets are busy.",
                 "family": "My sister sends word.", "rest": "You look tired.", "clothes": "Is that a new cloak?",
                 "danger": "Bandits again, they say.", "magic": "The reagents are fresh.", "land": "The harvest is in."}
        self.assertEqual(set(lines), set(acquaint.TOPIC_IDS))
        for tid, line in lines.items():
            self.assertEqual(self.c(line)[:1], [tid], line)

    def test_whole_words_only(self):
        for line in ("Seaward bound.", "A seasoned hand.", "Broadly speaking.", "I was painting.", "A catalogue.",
                     "He is homeless no more.", "Trademark nonsense.", "Sunday best."):
            self.assertEqual(self.c(line), [], line)
        self.assertEqual(self.c("The SEA, the sea!"), ["sea"])                # case does not matter
        self.assertEqual(self.c("A good night’s sleep."), ["rest"])      # a curly apostrophe is an apostrophe

    def test_top_two_by_hits_then_registry_order(self):
        self.assertEqual(self.c("Rain, wind and more rain on the road, with bread."), ["weather", "road"])
        self.assertEqual(self.c("Bread on the road."), ["road", "food"])     # one hit each: registry order
        self.assertEqual(self.c("Bread, bread, and the road."), ["food", "road"])
        self.assertEqual(self.c("Hm.", "Aye."), [])

    def test_absent_is_not_empty(self):
        self.assertIsNone(topics.candidate(["The road."], False))            # switched off: no classification offered
        self.assertEqual(topics.candidate(["Hm."], True), [])                # classified, nothing matched
        self.assertEqual(topics.candidate(["The road."], True), ["road"])

    def test_file_trouble_offers_nothing_and_never_raises(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "t.json")
            self.assertIsNone(topics.candidate(["The road."], True, p))      # missing
            self.assertEqual(topics.suggest_map(p), {})
            for body in ("{not json", "[]", '{"topics": 5}', '{"topics": {}}',
                         '{"topics": {"road": {"label": "the road", "words": []}}}',           # no usable keywords at all
                         '{"topics": {"gossip": {"label": "gossip", "words": ["gossip"]}}}'):  # only an unknown id
                with open(p, "w") as f:
                    f.write(body)
                topics.reset()
                self.assertIsNone(topics.candidate(["The road and some gossip."], True, p), body)

    def test_unknown_ids_in_the_file_are_ignored_the_rest_is_used(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "t.json")
            with open(p, "w") as f:
                json.dump({"topics": {"gossip": {"label": "gossip", "words": ["gossip"]},
                                      "sea": {"label": "the sea", "words": ["tide", 7, "BAD WORD!", "Tide"], "suggest": True},
                                      "road": {"label": "", "words": []}}}, f)       # retired: no words, no label
            topics.reset()
            self.assertEqual(topics.load(p)["ignored"], ["gossip"])
            self.assertEqual(topics.candidate(["Gossip about the tide on the road."], True, p), ["sea"])
            self.assertEqual(topics.suggest_map(p), {"sea": "the sea"})

    def test_a_tuned_file_is_picked_up(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "t.json")
            for words, want in ((["tide"], []), (["tide", "kelp"], ["sea"])):
                with open(p, "w") as f:
                    json.dump({"topics": {"sea": {"label": "the sea", "words": words}}}, f)
                self.assertEqual(topics.candidate(["Kelp everywhere."], True, p), want)


class Ingest(ta.Base):
    def facts(self, eid):
        return json.loads(self.led.db.execute("SELECT facts FROM events WHERE event_id = ?", (eid,)).fetchone()["facts"])

    def test_valid_topics_are_stored_and_a_step_1_row_is_unchanged(self):
        self.assertEqual(self.led.ingest([trow(1, 1, ["road", "weather"]), talked(1, 2)]), (["1-1", "1-2"], []))
        self.assertEqual(self.facts("1-1")["topics"], ["road", "weather"])
        self.assertEqual(list(self.facts("1-2")), ["day", "mpt", "other_kind", "other_id", "lines"])
        self.assertEqual(self.led.topics_cleaned, {})
        self.assertEqual(self.led.db.execute("SELECT COUNT(*) FROM events_fts WHERE event_id = '1-1'").fetchone()[0], 0)

    def test_bad_topics_are_cleaned_and_the_row_is_always_accepted(self):
        cases = [("x", None, "not_list"), ({"road": 1}, None, "not_list"), (None, None, "not_list"), (5, None, "not_list"),
                 ([], None, ""), (["gossip"], None, "unknown"), (["road", "gossip"], ["road"], "unknown"),
                 ([["road"]], None, "unknown"), ([1, True, None], None, "unknown"),
                 (["road", "road"], ["road"], "duplicate"), (["road", "sea", "magic"], ["road", "sea"], "too_many"),
                 (["road"] * 30, ["road"], "duplicate"), (["what she said about him"], None, "unknown"),
                 (["Road"], None, "unknown")]
        for i, (raw, want, why) in enumerate(cases, start=1):
            before = dict(self.led.topics_cleaned)
            acked, rejected = self.led.ingest([trow(1, i, raw)])
            self.assertEqual((acked, rejected), ([f"1-{i}"], []), raw)                  # never rejected
            f = self.facts(f"1-{i}")
            self.assertEqual(f.get("topics"), want, raw)
            self.assertEqual(set(f) - {"topics"}, acquaint.KEYS)
            after = self.led.topics_cleaned
            self.assertEqual(after.get(why, 0) - before.get(why, 0) if why else sum(after.values()) - sum(before.values()),
                             1 if why else 0, raw)
        self.assertEqual(self.led.db.execute("SELECT COUNT(*) FROM rejected").fetchone()[0], 0)

    def test_base_keys_stay_strict_and_other_extra_keys_are_still_refused(self):
        a = trow(1, 1, ["road"]); a["facts"]["name"] = "Elspeth Vale"
        b = trow(1, 2, ["road"]); b["facts"]["lines"] = 0
        c = trow(1, 3, ["road"]); del c["facts"]["day"]
        self.assertEqual(self.led.ingest([a, b, c, trow(1, 4, ["road"])]), (["1-4"], ["1-1", "1-2", "1-3"]))

    def test_ingest_never_opens_the_classifier_file(self):
        """Codex S2-D1: validity depends on the code registry only - also for an id the file has since retired."""
        with mock.patch.dict(os.environ, {"AVALLONE_TOPICS": "/nonexistent/topics.json"}), \
                mock.patch.object(topics, "load", side_effect=AssertionError("ingest opened the classifier")):
            topics.reset()
            self.assertEqual(self.led.ingest([trow(1, 1, ["road", "magic"])]), (["1-1"], []))
        self.assertEqual(self.facts("1-1")["topics"], ["road", "magic"])
        topics.reset()

    def test_a_replayed_batch_is_acked_and_changes_nothing(self):
        ev = trow(1, 1, ["road", "weather"])
        self.led.ingest([ev])
        self.assertEqual(self.led.ingest([trow(1, 1, ["sea"])]), (["1-1"], []))           # same id again, after "tuning"
        self.assertEqual(self.facts("1-1")["topics"], ["road", "weather"])

    def test_control_the_step_1_validator_refuses_a_topics_row(self):
        """Why the rollback floor matters: this is the check the live step-1 build runs (copied from 130724a4e)."""
        def step1_check(facts):
            if not isinstance(facts, dict) or set(facts) != acquaint.KEYS:
                raise ValueError("talked_with needs exactly {day, mpt, other_kind, other_id, lines}")
        with self.assertRaises(ValueError):
            step1_check(trow(1, 1, ["road"])["facts"])
        step1_check(talked(1, 1)["facts"])


class Reader(ta.Base):
    def read(self, npc=NPC, epoch=1, now=2000.0, fade=FADE, **kw):
        return self.led.acquaintances(npc, epoch, now, fade, **kw)

    def test_union_by_day_restart_duplicates_add_no_weight(self):
        self.led.ingest([trow(1, 1, ["sea"], day="2026-11-08"), trow(1, 2, ["sea"], day="2026-11-08"),
                         trow(1, 3, ["sea"], day="2026-11-08"),                       # three rows, ONE day (restarts)
                         trow(1, 4, ["road"], day="2026-11-09"), trow(1, 5, ["road"], day="2026-11-10")])
        got = self.read()[ELS]
        self.assertEqual((got["days"], got["topics"]), (3, ["road", "sea"]))           # road on 2 days beats sea on 1

    def test_same_day_rows_that_disagree_both_count(self):
        self.led.ingest([trow(1, 1, ["sea"]), trow(1, 2, ["magic", "sea"]), talked(1, 3)])
        self.assertEqual(self.read()[ELS]["topics"], ["sea", "magic"])                 # one day each; registry order

    def test_ranking_days_then_recency_then_registry_order(self):
        self.led.ingest([trow(1, 1, ["magic", "road"], day="2026-11-07"), trow(1, 2, ["magic"], day="2026-11-08"),
                         trow(1, 3, ["land", "sea"], day="2026-11-09"), trow(1, 4, ["weather"], day="2026-11-10"),
                         trow(1, 5, ["trade"], day="2026-11-10")])
        # magic 2 days; then one day each: weather+trade (newest day, registry order), land+sea (11-09: sea first), road
        self.assertEqual(self.read()[ELS]["topics"], ["magic", "weather", "trade", "sea", "land", "road"])

    def test_no_topics_is_an_empty_list_and_step_1_fields_are_unchanged(self):
        self.led.ingest([talked(1, 1), talked(1, 2, day="2026-11-09")])
        self.assertEqual(self.read(), {ELS: {"days": 2, "last_day": "2026-11-10", "topics": []}})

    def test_topics_follow_the_same_filters_as_the_rows(self):
        self.led.ingest([trow(1, 1, ["sea"], mpt=10.0), trow(1, 2, ["road"], mpt=1000.0, day="2026-11-09"),
                         trow(1, 3, ["magic"], mpt=1e9),                                      # the clock's future
                         trow(1, 4, ["land"], other=oid(1), char=ta.CHAR2, mpt=1000.0)])
        got = self.read(now=10.0 + FADE + 1)                                                  # 1-1 has just faded
        self.assertEqual(got[ELS]["topics"], ["road"])
        self.led.ingest([plain(1, 9, kind="character_deleted", char=ta.CHAR2)])
        self.assertNotIn(oid(1), self.read())                                                 # deleted owner: gone with its topics
        self.assertIsNone(self.read(epoch=2))                                                 # not reconciled: unavailable

    def test_a_stored_row_with_a_bad_topics_value_is_read_as_no_topics(self):
        """Rows are cleaned at ingest; the reader cleans again, so a hand-seeded or future row cannot leak text."""
        for i, raw in enumerate(('"x"', '["Elspeth said the baker cheats"]', '{"a": 1}', "null"), start=1):
            f = dict(talked(1, i)["facts"])
            self.led.db.execute("INSERT INTO events VALUES (?,?,?,?,?,?,?,?,?,?,?)",
                                (f"1-{i}", 1, i, NPC, CHAR, "talked_with", json.dumps(f)[:-1] + f', "topics": {raw}}}',
                                 "2026-11-10T15:00:00Z", 1, "live", "2026-11-10T15:00:00Z"))
        self.assertEqual(self.read(), {ELS: {"days": 1, "last_day": "2026-11-10", "topics": []}})

    def test_still_one_bounded_pass_at_100000_rows(self):
        rows = [(f"1-{i}", 1, i, NPC, CHAR, "talked_with",
                 json.dumps({"day": "2026-11-10", "mpt": float(i), "other_kind": "townsperson", "other_id": oid(i % 5000),
                             "lines": 2, "topics": ["road", "sea"]}),
                 "2026-11-10T15:00:00Z", 1, "live", "2026-11-10T15:00:00Z") for i in range(1, 100001)]
        self.led.db.execute("BEGIN")
        self.led.db.executemany("INSERT INTO events VALUES (?,?,?,?,?,?,?,?,?,?,?)", rows)
        self.led.db.execute("COMMIT")
        seen, sql = [], []
        self.led.rdb.set_trace_callback(sql.append)
        self.led.rdb.set_progress_handler(lambda: seen.append(1) or 0, 1000)
        got = self.read(now=200000.0, fade=1e12)
        self.led.rdb.set_progress_handler(None, 0)
        self.led.rdb.set_trace_callback(None)
        self.assertEqual(len(got), 8)
        self.assertEqual(got[oid(0)]["topics"], ["road", "sea"])
        self.assertLess(len(seen), 40)                                                  # the history is not walked
        self.assertEqual(sum("FROM events INDEXED BY events_talked" in s for s in sql), 1)   # no second row query

    def test_isolation_is_unchanged_by_topics(self):
        self.led.ingest([trow(1, 1, ["road", "sea"]), plain(1, 2, text="a wolf on the road")])
        self.assertEqual([e["event_id"] for e in self.led.recent(NPC, CHAR)], ["1-2"])
        self.assertEqual(self.led.db.execute("SELECT COUNT(*) FROM events_fts WHERE event_id = '1-1'").fetchone()[0], 0)


class Fresh(unittest.TestCase):
    """Trial 1 -> 2: covered subjects are never named; up to two everyday subjects NOT covered are offered instead."""
    E = {"days": 3, "last_day": "2026-11-09", "topics": ["road", "weather"]}
    SUG = {"road": "the road", "weather": "the weather", "food": "food and drink", "family": "home and family",
           "animals": "animals"}

    def test_offers_only_what_was_not_covered_in_registry_order(self):
        first2 = lambda pool, n: pool[:n]                                             # noqa: E731
        self.assertEqual(acquaint.fresh_labels(self.E, lambda: self.SUG, first2), ["food and drink", "animals"])
        self.assertEqual(acquaint.fresh_labels(dict(self.E, topics=["food"]), lambda: self.SUG, first2),
                         ["the road", "the weather"])
        got = acquaint.fresh_labels(self.E, lambda: self.SUG)                         # the real pick: random, never covered
        self.assertEqual(len(got), 2)
        self.assertTrue(set(got) <= {"food and drink", "home and family", "animals"})

    def test_nothing_to_offer(self):
        self.assertEqual(acquaint.fresh_labels({"days": 1, "last_day": "x", "topics": []}, lambda: self.SUG), [])   # nothing on record
        self.assertEqual(acquaint.fresh_labels({"days": 1, "last_day": "x"}, lambda: self.SUG), [])
        self.assertEqual(acquaint.fresh_labels(self.E, None), [])                     # topics switched off
        self.assertEqual(acquaint.fresh_labels(self.E, lambda: {}), [])               # classifier file unusable
        self.assertEqual(acquaint.fresh_labels(self.E, lambda: {"road": "the road"}), [])   # everything offered is covered
        self.assertEqual(acquaint.fresh_labels(self.E, lambda: {"food": "food and drink"}), ["food and drink"])
        self.assertEqual(acquaint.fresh_labels(self.E, lambda: {"gossip": "gossip", "food": " ", "sea": 7}), [])

    def test_the_shipped_suggestions_are_everyday_subjects(self):
        topics.reset()
        got = topics.suggest_map()
        self.assertTrue({"food", "family", "animals", "town"} <= set(got))
        self.assertFalse({"danger", "magic", "sea", "work", "road", "weather"} & set(got))   # "your work" is always in the rules;
        # the road and the weather are never offered (the owner, 2026-11-10) - they are still recognised when they come up

    def test_exchange_rules_swap_the_everyday_list_and_are_a2_without(self):
        import exchange
        ask = {"speaker": {"kind": "townsperson", "name": "Osric", "title": "the tanner", "first": "Osric"},
               "addressee": {"kind": "companion", "name": "Elspeth Vale", "title": "the field healer", "first": "Elspeth"},
               "band": "", "last": False}
        base = exchange.rules(ask, "KNOWN.")
        self.assertIn("everyday things - the road, the weather, your work. Say nothing", base)
        self.assertEqual(exchange.rules(ask, "KNOWN.", []), base)                     # nothing to offer: byte for byte
        self.assertEqual(exchange.rules(ask, "KNOWN.", ()), base)
        new = exchange.rules(ask, "KNOWN.", ["food and drink", "animals"])
        self.assertIn("everyday things - your work, food and drink, animals. Say nothing", new)
        self.assertNotIn("the road, the weather", new)
        self.assertEqual(new.replace("your work, food and drink, animals", "the road, the weather, your work"), base)

    def test_remark3_never_raises_and_trouble_costs_only_the_subjects(self):
        d = tempfile.TemporaryDirectory()
        self.addCleanup(d.cleanup)
        led = memory.Ledger(os.path.join(d.name, "m.db"))
        self.addCleanup(led.db.close)
        self.addCleanup(led.rdb.close)
        led.reconcile(1, 0, 0)
        led.ingest([trow(1, 1, ["road", "weather"]), talked(1, 2, npc=ELS, other=NPC, kind="townsperson")])
        k = {"epoch": 1, "mpt": 2000.0, "other_id": ELS}
        plain_s = acquaint.remark(led, NPC, k, "Elspeth", cards.rules, True, cards.CENTRAL)
        self.assertEqual(plain_s[0], "known")
        first2 = lambda pool, n: pool[:n]                                             # noqa: E731
        st, text, fresh = acquaint.remark3(led, NPC, k, "Elspeth", cards.rules, True, cards.CENTRAL, lambda: self.SUG, "exchange", first2)
        self.assertEqual((st, text, fresh), ("known_topics", plain_s[1], ["food and drink", "animals"]))   # the sentence never names a subject
        st, text, fresh = acquaint.remark3(led, NPC, k, "Elspeth", cards.rules, True, cards.CENTRAL, lambda: self.SUG, "party", first2)
        self.assertEqual(text, plain_s[1] + " If you add a word to Elspeth, food and drink or animals would be fresh ground between you.")
        for bad in (None, lambda: 1 / 0, lambda: {}, lambda: "junk", lambda: {"food": ["x"]}):
            self.assertEqual(acquaint.remark3(led, NPC, k, "Elspeth", cards.rules, True, cards.CENTRAL, bad), ("known", plain_s[1], []), bad)
        self.assertEqual(acquaint.remark3(led, NPC, k, "Elspeth", cards.rules, True, cards.CENTRAL, lambda: self.SUG, "exchange",
                                          lambda pool, n: 1 / 0), ("known", plain_s[1], []))
        # her own rows carry no topics: nothing to offer, step-1 exactly
        k2 = {"epoch": 1, "mpt": 2000.0, "other_id": NPC}
        self.assertEqual(acquaint.remark3(led, ELS, k2, "Osric", cards.rules, True, cards.CENTRAL, lambda: self.SUG)[0::2], ("known", []))
        # every step-1 outcome passes straight through
        self.assertEqual(acquaint.remark3(led, NPC, None, "Elspeth", cards.rules, True, cards.CENTRAL, lambda: self.SUG), ("off", "", []))
        self.assertEqual(acquaint.remark3(led, NPC, dict(k, epoch=2), "Elspeth", cards.rules, True, cards.CENTRAL, lambda: self.SUG),
                         ("unavailable", "", []))
        self.assertEqual(acquaint.remark3(None, NPC, k, "Elspeth", lambda: 1 / 0, True, cards.CENTRAL, lambda: self.SUG), ("off", "", []))
        self.assertEqual(acquaint.remark3(led, NPC, k, "Elspeth", lambda: 1 / 0, True, cards.CENTRAL, lambda: self.SUG), ("unavailable", "", []))


class H(unittest.TestCase):
    """Both prompt paths through the real handlers (test_acquaint's rig), with topic rows in the ledger."""
    ELSPETH, TANNER = ta.Handlers.ELSPETH, ta.Handlers.TANNER
    post, exchange, respond, party = ta.Handlers.post, ta.Handlers.exchange, ta.Handlers.respond, ta.Handlers.party
    tearDown = ta.Handlers.tearDown
    K = {"epoch": 7, "mpt": 30.0, "other_id": ELS}
    A2_LIST = "everyday things - the road, the weather, your work. Say nothing"
    STEP1_END = "refer to nothing from them and do not invent any"

    def setUp(self):
        ta.Handlers.setUp(self)
        topics.reset()
        self.led.ingest([trow(7, 11, ["road", "weather"], day="2026-11-02", mpt=11),
                         trow(7, 12, ["road"], day="2026-11-03", mpt=12)])
        for p in (mock.patch.dict(brain.CFG, {"acquaintance_topics": True}),
                  mock.patch.object(acquaint._RNG, "sample", lambda pool, n: pool[:n])):
            p.start()
            self.addCleanup(p.stop)

    def test_exchange_prompt_offers_fresh_subjects_and_never_names_the_covered_ones(self):
        out, prompt, rec = self.exchange(self.K)
        self.assertTrue(out["ok"])
        self.assertIn("everyday things - your work, trade and prices, food and drink. Say nothing", prompt)
        self.assertNotIn("the road, the weather", prompt)
        self.assertNotIn("the road and travelling", prompt)
        self.assertNotIn("talked about", prompt)
        self.assertIn(self.STEP1_END, prompt)                    # the familiarity sentence is step 1's, unchanged
        self.assertEqual(rec["acq"], "known_topics")
        self.assertEqual(out["topics"], [])                      # "And to you." + "Good to see you again.": nothing matched
        self.assertEqual(rec["topics"], [])

    def test_the_opening_line_is_never_counted_but_an_echo_of_it_is(self):
        def run(opener, reply, said):
            def fake(url, payload, timeout_s):
                return {"message": {"content": json.dumps({"say": said})}, "prompt_eval_count": 1, "eval_count": 1}
            x = {"id": "x1", "step": 1, "speaker": dict(self.TANNER), "addressee": dict(self.ELSPETH), "trust_band": "",
                 "transcript": [{"who": "Osric", "text": opener}, {"who": "Elspeth Vale", "text": reply}], "roster": []}
            body = {"npc_id": NPC, "persona_key": "townsfolk", "request_id": "t", "subject_id": CHAR, "priority": "Background",
                    "budget_ms": 6000, "exchange": x,
                    "persona_vars": {"name": "Osric", "role": "tanner", "town": "Britain", "trade": "vendor"}}
            with mock.patch.object(brain, "_post_cancellable", fake):
                return self.post("/v1/npc/exchange", body)
        # the opener (the NPC's reply to the PLAYER) talks of the sea and of magic: neither is counted
        out = run("The tide brought in a wizard's scroll, Elspeth.", "And to you.", "Mind the vats.")
        self.assertEqual((out["ok"], out["topics"]), (True, []))
        # ... but an exchange line that picks the subject up IS counted - by design (Codex Q4: not promised otherwise)
        out = run("The tide brought in a wizard's scroll, Elspeth.", "The tide, you say?", "Aye, and rain with it.")
        self.assertEqual(out["topics"], ["weather", "sea"])

    def test_switched_off_is_step_1_exactly(self):
        with mock.patch.dict(brain.CFG, {"acquaintance_topics": False}):
            out, prompt, rec = self.exchange(self.K)
        self.assertTrue(out["ok"])
        self.assertNotIn("topics", out)                         # the field is absent, not empty
        self.assertNotIn("topics", rec)
        self.assertIn(self.A2_LIST, prompt)
        self.assertIn(self.STEP1_END, prompt)
        self.assertEqual(rec["acq"], "known")
        self.assertEqual(sorted(out), ["action", "kind", "model", "ok", "say"])        # the step-1 answer, key for key

    def test_default_is_off(self):
        self.assertIs(brain.DEFAULTS["acquaintance_topics"], False)

    def test_classifier_trouble_costs_only_the_subjects(self):
        with mock.patch.dict(os.environ, {"AVALLONE_TOPICS": "/nonexistent/topics.json"}):
            topics.reset()
            out, prompt, rec = self.exchange(self.K)
            self.assertTrue(out["ok"])                           # the line is still said
            self.assertNotIn("topics", out)
            self.assertIn("You and Elspeth have chatted a few times before", prompt)          # step-1 sentence stands
            self.assertIn(self.A2_LIST, prompt)
            self.assertEqual(rec["acq"], "known")
            out, prompt, rec = self.respond(self.party())
            self.assertTrue(out["ok"])
            self.assertIn("one short separate sentence to or about Elspeth", prompt)           # the A1 paragraph stands
            self.assertIn(self.STEP1_END, prompt)
            self.assertNotIn("fresh ground", prompt)
        topics.reset()
        with mock.patch.object(topics, "suggest_map", side_effect=RuntimeError("boom")):
            out, prompt, rec = self.exchange(self.K)
            self.assertEqual((out["ok"], rec["acq"]), (True, "known"))
            self.assertIn(self.A2_LIST, prompt)
        with mock.patch.object(topics, "load", side_effect=RuntimeError("boom")):       # candidate() swallows it too
            out, prompt, rec = self.exchange(self.K)
            self.assertTrue(out["ok"])
            self.assertNotIn("topics", out)

    def test_party_path_is_scoped_to_the_aside(self):
        out, prompt, rec = self.respond(self.party())
        self.assertTrue(out["ok"])
        self.assertIn("If you add a word to Elspeth, trade and prices or food and drink would be fresh ground between you.", prompt)
        self.assertIn("your answer is for the traveller", prompt)                       # A1's own sentence, untouched
        self.assertNotIn("talked about", prompt)
        self.assertEqual(rec["party"]["acq"], "known_topics")
        self.assertNotIn("topics", out)                                                 # an ordinary reply never carries them
        out, prompt, rec = self.respond(self.party(), companion_share=0.0)              # quiet turn: nothing at all
        self.assertNotIn("fresh ground", prompt)

    def test_nothing_on_record_still_varies_the_everyday_subjects(self):
        """the owner, 2026-11-10: mix it up. A first meeting, a pair with no topics, memory off or unavailable: two everyday
        subjects are offered all the same - never the road or the weather - and the familiarity state is unchanged."""
        for known, state in (({"epoch": 7, "mpt": 30.0, "other_id": NPC}, "known"),       # her rows carry no topics
                             ({"epoch": 7, "mpt": 30.0, "other_id": oid(9)}, "none"),     # never met
                             ({"epoch": 8, "mpt": 30.0, "other_id": ELS}, "unavailable"), (None, "off")):
            out, prompt, rec = self.exchange(known, speaker_is_npc=not (known is not None and known["other_id"] == NPC))
            self.assertTrue(out["ok"])
            self.assertEqual(rec["acq"], state, known)
            self.assertIn("everyday things - your work, trade and prices, food and drink. Say nothing", prompt)
            self.assertNotIn("the road, the weather", prompt)
        with mock.patch.object(topics, "suggest_map", lambda: {}):                        # nothing usable: Stage A2's list
            out, prompt, rec = self.exchange(None)
            self.assertIn(self.A2_LIST, prompt)

    def test_offer_never_raises(self):
        self.assertEqual(acquaint.offer(lambda: 1 / 0), [])
        self.assertEqual(acquaint.offer(lambda: None), [])
        self.assertEqual(acquaint.offer(lambda: {"food": "food and drink", "gossip": "x"}), ["food and drink"])
        self.assertEqual(acquaint.offer(lambda: {"food": "a", "town": "b", "rest": "c"}, lambda pool, n: 1 / 0), [])
        got = acquaint.offer(topics.suggest_map)
        self.assertEqual(len(got), 2)

    def test_both_memory_endpoints_advertise_the_ingest_capability(self):
        r = self.post("/v1/memory/events", {"events": []})
        self.assertEqual((r["ok"], r["talked_with_topics"]), (True, 1))                 # also what a held shard probes with
        r = self.post("/v1/memory/reconcile", {"new_epoch": 8, "saved_epoch": 7, "saved_seq": 12})
        self.assertEqual((r["ok"], r["talked_with_topics"]), (True, 1))
        with mock.patch.dict(brain.CFG, {"acquaintance_topics": False}):                # acceptance is not the on/off switch
            self.assertEqual(self.post("/v1/memory/events", {"events": []})["talked_with_topics"], 1)

    def test_cleaning_is_logged_by_reason_never_with_the_payload(self):
        self.post("/v1/memory/events", {"events": [trow(7, 40, ["road", "she said the baker cheats"])]})
        rec = next(x for x in reversed(self.logs) if x.get("outcome") == "topics_cleaned")
        self.assertEqual(rec["counts"], {"unknown": 1})
        self.assertNotIn("baker", json.dumps(self.logs))


class PlayTest2Fixes(unittest.TestCase):
    """the owner, 2026-11-10; a greeting to Elspeth that she could not answer."""

    def _ask(self, step):
        import exchange
        me = {"kind": "companion", "name": "Elspeth Vale", "title": "the field healer", "first": "Elspeth"}
        other = {"kind": "townsperson", "name": "Kaiman", "title": "the baker", "first": "Kaiman"}
        return exchange, {"speaker": me, "addressee": other, "band": "", "step": step, "last": False}

    def test_subjects_are_offered_only_for_each_speakers_first_line(self):
        exchange, ask = self._ask(1)
        early = exchange.rules(ask, "", ["food", "family"], "", follow=True)
        self.assertIn("your work, food, family", early)
        self.assertIn("plain sense as a reply", early)
        exchange, ask = self._ask(2)
        later = exchange.rules(ask, "", ["food", "family"], "", follow=True)
        self.assertNotIn("food, family", later)
        self.assertIn("what the two of you are already talking about", later)
        self.assertNotIn("the road, the weather", later)

    def test_follow_off_is_the_old_paragraph(self):
        exchange, ask = self._ask(4)
        self.assertEqual(exchange.rules(ask, "", ["food"], ""), exchange.rules(ask, "", ["food"], "", follow=False))
        self.assertNotIn("plain sense", exchange.rules(ask, "", ["food"], ""))
        self.assertIn("the road, the weather, your work", exchange.rules(ask))

    def test_no_answer_makes_the_aside_about_her_not_to_her(self):
        import party
        req = {"party": {"companions": [{"name": "Elspeth Vale", "title": "the field healer"}], "pets": [], "nudge": False}}
        p = party.parse(req)
        self.assertFalse(p["no_answer"])                      # an older shard sends nothing: as before
        self.assertIn("to or about Elspeth", party.block(p, "companion", "", "a plain, friendly greeting"))
        req["party"]["no_answer"] = True
        p = party.parse(req)
        b = party.block(p, "companion", "", "a plain, friendly greeting")
        self.assertIn("about Elspeth (first name only), said to the traveller", b)
        self.assertIn("Do not speak to Elspeth directly", b)
        self.assertNotIn("to or about", b)
        self.assertNotIn("friendly greeting", b)              # no greeting angle for someone who cannot answer
        req["party"]["no_answer"] = "yes"
        self.assertFalse(party.parse(req)["no_answer"])       # only a real true counts


if __name__ == "__main__":
    unittest.main()
