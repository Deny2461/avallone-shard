"""Errands step, build 1: the text layer (errands_text.py). Run: python3 test_errands_text.py"""
import json
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import errands_text as et  # noqa: E402

D = et.load()
BASE = {"S": "Tomas", "R": "Anna", "rel": "sister", "rel_back": "brother", "a_trade": "a baker", "town": "Vesper",
        "S_town": "Minoc", "O": "Edda", "O_rel": "mother", "them": "her", "their": "her"}
LONG = dict(BASE, S="Bartholomew", R="Wilhelmina", rel="daughter", rel_back="father", a_trade="an animal trainer",
            town="Serpent's Hold", S_town="Buccaneer's Den", O="Clementine", O_rel="grandmother")


class Lists(unittest.TestCase):
    def test_the_approved_file_passes_every_rule(self):
        self.assertTrue(et.check_lists(D))
        self.assertEqual(D["lists_version"], 2)   # 2: design v6 added the two fixed lines

    def test_the_two_lines_the_shard_speaks_itself_are_the_same_words_in_its_code(self):
        """Design v6 12.2: the reward line and the "as you heard it" line are said by the shard with no brain call, so
        the words live in both places. In a source tree both are checked to be the same; a deployed brain folder has
        no shard source beside it and the check is skipped there."""
        cs = os.path.join(os.path.dirname(os.path.abspath(et.__file__)), "..", "Projects", "UOContent", "Avallone",
                          "Errands", "Errands.cs")
        if not os.path.exists(cs):
            self.skipTest("no shard source beside this brain folder")
        with open(cs, encoding="utf-8") as fh:
            source = fh.read()
        self.assertIn('RewardLine = "' + D["reward"][0]["text"] + '";', source)
        self.assertIn('AsHeardLine = "' + D["as_heard"][0]["text"] + '";', source)
        for name in ("see_log", "not_recalled", "help"):
            self.assertIn('"' + D["system"][name] + '"', source)
        self.assertIn(D["system"]["more_than_one"].replace("{S}", "{npc.Name}"), source)
        for r in D["neutral"]:
            self.assertIn('"' + r["text"] + '"', source)

    def test_every_kind_and_route_has_an_ask_line(self):
        for kind, route in et.KIND_ROUTES:
            self.assertTrue(D["ask"][f"{kind}.{route}"])

    def test_a_digit_a_coin_word_an_unknown_slot_and_a_duplicate_key_are_refused(self):
        for change in (lambda d: d["yes"][0].update(text="Take 5 of them."),
                       lambda d: d["yes"][0].update(text="There is gold in it for you."),
                       lambda d: d["yes"][0].update(text="Thank you, {player}."),
                       lambda d: d["yes"][1].update(key=d["yes"][0]["key"]),
                       lambda d: d["msg"][0].update(text="that {R} is well"),
                       lambda d: d["msg"][0].update(text="that " + "x" * 80),
                       lambda d: d["ask"].update({"word.kin": []}),
                       # design v6: the two fixed lines the shard speaks itself - one each, no slot, the usual rules
                       lambda d: d.pop("reward"),
                       lambda d: d.update(as_heard=[]),
                       lambda d: d["reward"].append({"key": "reward.2", "text": "Nothing at all."}),
                       lambda d: d["reward"][0].update(text="Nothing for it, {S}."),
                       lambda d: d["reward"][0].update(text="There is a reward in it."),
                       lambda d: d["as_heard"][0].update(text="Carry it as you heard it, all 3 parts."),
                       lambda d: d.update(lists_version=0)):
            bad = json.loads(json.dumps(D))
            change(bad)
            with self.assertRaises(et.Refused):
                et.check_lists(bad)

    def test_message_lines_by_relation_and_route(self):
        keys = lambda rows: {r["key"] for r in rows}
        self.assertEqual(len(et.messages_for(D, "kin", "cousin")), 10)             # no close, no far
        self.assertIn("m11", keys(et.messages_for(D, "kin", "wife")))
        self.assertNotIn("m14", keys(et.messages_for(D, "kin", "wife")))
        self.assertIn("m14", keys(et.messages_for(D, "cousin", "cousin")))
        self.assertNotIn("m11", keys(et.messages_for(D, "cousin", "cousin")))
        self.assertEqual(len(et.messages_for(D, "standin", "mother")), 15)

    def test_text_hash_changes_with_either_form_of_an_answer(self):
        a = dict(D["answer"][0])
        h = et.text_hash(a)
        self.assertNotEqual(h, et.text_hash(dict(a, said=a["said"] + ".")))
        self.assertNotEqual(h, et.text_hash(dict(a, reported=a["reported"] + ".")))


class Sentences(unittest.TestCase):
    def all_lines(self, values):
        out = []
        for name, rows in D["ask"].items():
            route = name.split(".")[1]
            for r in rows:
                for m in et.messages_for(D, route, values["O_rel"] if route == "standin" else values["rel"]):
                    out.append(et.fill(r["text"], dict(values, msg=et.message_text(D, m["key"], "woman"))))
        for rows in D["deliver"].values():
            for r in rows:
                for a in D["answer"]:
                    said, _ = et.answer_forms(D, a["key"], values["R"])
                    out.append(et.fill(r["text"], dict(values, said=said)))
        for a in D["answer"]:
            _, reported = et.answer_forms(D, a["key"], values["R"])
            for r in et.returns_for(D, reported):
                out.append(et.fill(r["text"], dict(values, reported=reported)))
        for group in ("yes", "no", "neutral", "thanks", "reward", "as_heard"):
            out += [et.fill(r["text"], values) for r in D[group]]
        return out

    def test_every_template_with_every_line_makes_a_whole_sentence(self):
        for values in (BASE, LONG):
            lines = self.all_lines(values)
            self.assertGreater(len(lines), 250)
            for line in lines:
                self.assertLessEqual(len(line), 300, line)
                self.assertNotIn("{", line)
                self.assertFalse(et.DIGIT.search(line), line)
                self.assertFalse(et.COIN.search(line), line)
                self.assertTrue(line[0].isupper() or not line[0].isalpha(), line)

    def test_the_worked_example_reads_as_approved(self):
        msg = et.message_text(D, "m02", "woman")
        line = et.fill(D["ask"]["word.cousin"][0]["text"], dict(BASE, msg=msg))
        self.assertEqual(line, "You travel more than I do. Would you carry a word to my cousin Anna, a baker in Vesper? "
                               "Tell her that the door is always open to her.")

    def test_a_slot_that_opens_a_sentence_is_capitalised(self):
        _, reported = et.answer_forms(D, "a01", "Anna")
        line = et.fill(D["return"][2]["text"], dict(BASE, reported=reported))
        self.assertIn("Good. Good. A baker in Vesper, and thriving", line)

    def test_the_third_return_line_is_only_used_after_a_that_form(self):
        _, that = et.answer_forms(D, "a01", "Anna")
        _, named = et.answer_forms(D, "a02", "Anna")
        self.assertEqual(len(et.returns_for(D, that)), 3)
        self.assertEqual([r["key"] for r in et.returns_for(D, named)], ["ret.1", "ret.2"])
        self.assertEqual(named, "Anna is kept busy, and glad of it")

    def test_refusals(self):
        t = D["ask"]["word.kin"][0]["text"]
        ok = dict(BASE, msg="that I send my love")
        for bad in (dict(ok, R=""), {k: v for k, v in ok.items() if k != "town"}, dict(ok, R="Anna{S}"),
                    dict(ok, town="Vesper 2"), dict(ok, msg="that there is gold in it"), dict(ok, R="A" * 200),
                    dict(ok, town="Ves\nper")):
            with self.assertRaises(et.Refused):
                et.fill(t, bad)
        self.assertEqual(et.fill(D["deliver"]["word.standin"][2]["text"], dict(ok, S_town="Minoc"))[:10], "From Tomas")
        with self.assertRaises(et.Refused):
            et.fill(D["deliver"]["word.standin"][2]["text"], dict(ok, S_town="Min\noc"))

    def test_trade_article_and_pronouns(self):
        self.assertEqual(et.a_trade("Baker"), "a baker")
        self.assertEqual(et.a_trade("innkeeper"), "an innkeeper")
        self.assertEqual(et.a_trade("Animal  Trainer"), "an animal trainer")
        for bad in ("", "x", "baker 2", "baker{S}"):
            with self.assertRaises(et.Refused):
                et.a_trade(bad)
        self.assertEqual(et.message_text(D, "m07", "man"), "that there is a place at my table whenever his road allows")
        with self.assertRaises(et.Refused):
            et.pronouns("unknown")
        with self.assertRaises(et.Refused):
            et.message_text(D, "a01", "man")
        with self.assertRaises(et.Refused):
            et.answer_forms(D, "m01", "Anna")


EV = {"event_id": "8-5", "epoch": 8, "seq": 5, "kind": "errand_done", "schema": 1, "npc_id": "n", "char_id": "c",
      "at_utc": "2026-11-21T00:00:00Z", "facts": {"v": 1, "errand": 7, "kind": "word", "head_start": False}}


class Hashes(unittest.TestCase):
    def test_fingerprint_ignores_key_order_and_escape_spelling(self):
        a = et.event_fingerprint(EV)
        b = et.event_fingerprint(json.loads(json.dumps(dict(reversed(list(EV.items()))))))
        self.assertEqual(a, b)
        one = et.loads_strict('{"event_id":"8-5","facts":{"t":"caf\\u00e9"}}')
        two = et.loads_strict('{"facts":{"t":"café"},"event_id":"8-5"}')
        self.assertEqual(et.event_fingerprint(one), et.event_fingerprint(two))

    def test_fingerprint_binds_each_of_the_nine_fields(self):
        base = et.event_fingerprint(EV)
        for f in et.FINGERPRINT_FIELDS:
            changed = dict(EV)
            changed[f] = {"v": 2} if f == "facts" else (EV[f] + 1 if isinstance(EV[f], int) else EV[f] + "x")
            self.assertNotEqual(base, et.event_fingerprint(changed), f)
        self.assertNotEqual(base, et.event_fingerprint({k: v for k, v in EV.items() if k != "at_utc"}))

    def test_fingerprint_is_taken_before_cleaning_so_originals_that_clean_alike_differ(self):
        a = dict(EV, facts={"note": "x" * 500})
        b = dict(EV, facts={"note": "x" * 501})
        self.assertNotEqual(et.event_fingerprint(a), et.event_fingerprint(b))
        self.assertNotEqual(et.event_fingerprint(dict(EV, facts={"n": 1})), et.event_fingerprint(dict(EV, facts={"n": "1"})))
        self.assertNotEqual(et.event_fingerprint(dict(EV, facts={"n": True})), et.event_fingerprint(dict(EV, facts={"n": 1})))

    def test_transport_refuses_what_is_outside_the_protocol(self):
        for raw in ('{"a":1,"a":2}', '{"a":NaN}', '{"a":Infinity}', '{"a":"\\ud800"}', '{"a":{"b":1,"b":1}}', "not json"):
            with self.assertRaises(ValueError):
                et.loads_strict(raw)
        self.assertEqual(et.loads_strict(b'{"a":[1,"x",true,null]}'), {"a": [1, "x", True, None]})
        with self.assertRaises(ValueError):
            et.canonical({"a": float("nan")})
        with self.assertRaises(ValueError):
            et.event_fingerprint([1, 2])

    def test_fact_digest_changes_with_every_identity_fact_and_not_with_order_of_call(self):
        args = dict(kind="word", route="cousin", sender=("s1", "Tomas"), receiver=("r1", "Anna"), about=None,
                    edge=["cousin", "r1", "s1"], line_key="m02", line_hash="h", lists_version=1)
        base = et.fact_digest(**args)
        self.assertEqual(base, et.fact_digest(**dict(reversed(list(args.items())))))
        for k, v in (("kind", "ask_after"), ("sender", ("s1", "Tom")), ("receiver", ("r2", "Anna")),
                     ("about", ("o1", "Edda", "mother", True, "Yew")), ("edge", ["cousin", "r1", "s2"]),
                     ("line_key", "m03"), ("line_hash", "i"), ("lists_version", 2)):
            self.assertNotEqual(base, et.fact_digest(**dict(args, **{k: v})), k)
        with self.assertRaises(ValueError):
            et.fact_digest(**dict(args, kind="ask_after", route="standin"))      # a stand-in carries one-way word only

    def test_answer_digest_changes_with_trade_town_and_text(self):
        args = dict(receiver_id="r1", receiver_name="Anna", trade="baker", town="Vesper", map_name="Felucca",
                    answer_key="a01", answer_hash="h")
        base = et.answer_digest(**args)
        for k, v in (("trade", "weaver"), ("town", "Minoc"), ("map_name", "Trammel"), ("answer_key", "a02"),
                     ("answer_hash", "i"), ("receiver_name", "Ann"), ("receiver_id", "r2")):
            self.assertNotEqual(base, et.answer_digest(**dict(args, **{k: v})), k)


if __name__ == "__main__":
    unittest.main()
