"""Errands step, build 1 (design v5, Projects/Avallone-Errands-Step-Design.md in the wiki; Codex "Errands-Design R5" GO WITH
NOTES 2026-11-21). The fixed-code text layer: the approved lists (errands_lists.json, approved by the owner 2026-11-21), the
sentences code writes from them, and the three hashes the design pins - an event's fingerprint (3.8), the fact digest
(3.1) and the answer digest (3.7). Nothing here reads the model, the database or the clock. The model never writes any
of these sentences and is never read for a fact, a yes or a sum."""
import hashlib
import json
import os
import re

PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "errands_lists.json")
SCHEMA = 1                                    # the version of the digest records below
KINDS = ("word", "ask_after", "introduce")    # closed; build 2 adds goods, news
ROUTES = ("kin", "cousin", "standin", "contact")
KIND_ROUTES = {("word", "kin"), ("word", "cousin"), ("word", "standin"), ("ask_after", "kin"), ("ask_after", "cousin"),
               ("introduce", "contact")}
SLOTS = ("S", "R", "rel", "rel_back", "a_trade", "town", "S_town", "O", "O_rel", "them", "their", "msg", "said",
         "reported")
SLOT = re.compile(r"\{([A-Za-z_]+)\}")
KEY = re.compile(r"^[a-z_]+(\.[a-z_]+)*\.?[0-9]{1,2}$|^[am][0-9]{2}$")
DIGIT = re.compile(r"\d")
# 3.7: no line names a sum or a reward. Whole words; "custom" (trade) and "dear" are fine.
COIN = re.compile(r"\b(gold|coins?|silver|copper|pay|paid|payment|wages?|reward|rewards|fee|price|purse|gp)\b", re.I)
AN = re.compile(r"^(?:[aeiou]|hour|heir|honest)", re.I)
_cache = {"mtime": None, "data": None}


class Refused(ValueError):
    """A sentence that must not be said, or lists that must not be used. The step does not happen (3.7)."""


def sha(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


# ---- the canonical form and the three hashes ---------------------------------------------------------

def canonical(value):
    """3.8: keys sorted, no added spaces, non-ASCII left as it is. Strict: a value outside the protocol (a number that is
    not finite, a lone surrogate) raises ValueError - design 11.1, never a half-made fingerprint."""
    try:
        return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False).encode("utf-8")
    except (UnicodeEncodeError, TypeError) as e:
        raise ValueError(f"not canonical: {type(e).__name__}")


def loads_strict(raw):
    """Design 11.1: the transport boundary for a request that carries an errand event. Refuses a duplicate key, NaN or
    Infinity, and a lone surrogate, BEFORE any receipt is looked up or written."""
    def pairs(items):
        seen = {}
        for k, v in items:
            if k in seen:
                raise ValueError("duplicate key")
            seen[k] = v
        return seen

    def constant(name):
        raise ValueError(f"non-finite number {name}")

    text = raw.decode("utf-8") if isinstance(raw, (bytes, bytearray)) else raw
    value = json.loads(text, object_pairs_hook=pairs, parse_constant=constant)
    canonical(value)                       # raises on a lone surrogate hidden in an escape
    return value


FINGERPRINT_FIELDS = ("event_id", "epoch", "seq", "kind", "schema", "npc_id", "char_id", "at_utc", "facts")


def event_fingerprint(ev):
    """3.8: SHA-256 over a JSON array of exactly nine values, in this order, of the event AS IT ARRIVED - before anything
    is cleaned or cut. A value the event lacks is null (that never makes it valid; the check still refuses it)."""
    if not isinstance(ev, dict):
        raise ValueError("event is not an object")
    return hashlib.sha256(canonical([ev.get(f) for f in FINGERPRINT_FIELDS])).hexdigest()


def fact_digest(*, kind, route, sender, receiver, about, edge, line_key, line_hash, lists_version):
    """3.1: the identity-level facts an errand asserts. sender/receiver = (id, name); about = None or (id, name, relation
    word, alive, stored place); edge = what makes the route true (a tie, cousin pair or contact row, as a short list).
    Current trade and current town are NOT in it: every proof checks those afresh."""
    if (kind, route) not in KIND_ROUTES:
        raise ValueError("kind/route")
    record = [SCHEMA, kind, route, list(sender), list(receiver), None if about is None else list(about), list(edge),
              line_key, line_hash, int(lists_version)]
    return hashlib.sha256(canonical(record)).hexdigest()


def answer_digest(*, receiver_id, receiver_name, trade, town, map_name, answer_key, answer_hash):
    """3.7: fixed on the row when the relative gives the answer. The trade and town exactly as they were named."""
    record = [SCHEMA, receiver_id, receiver_name, trade, town, map_name, answer_key, answer_hash]
    return hashlib.sha256(canonical(record)).hexdigest()


# ---- the lists ---------------------------------------------------------------------------------------

def _entries(d):
    for group in ("ask", "deliver"):
        for name, rows in d[group].items():
            for r in rows:
                yield group, name, r
    for group in ("msg", "yes", "no", "neutral", "answer", "return", "thanks", "reward", "as_heard"):
        for r in d.get(group, ()):       # "reward" and "as_heard": design v6 12.2 - spoken by the shard with no brain call
            yield group, "", r


def check_lists(d):
    """Every rule of 3.7 that can be checked without a person: unique well-formed keys, known slots only, no digit and no
    coin word in anything spoken, message lines within their bound. Raises Refused with the first fault."""
    if not isinstance(d.get("lists_version"), int) or d["lists_version"] < 1:
        raise Refused("lists_version")
    seen = set()
    for group, name, r in _entries(d):
        key = r.get("key", "")
        if not KEY.match(key) or key in seen:
            raise Refused(f"key {key!r}")
        seen.add(key)
        for field in (("said", "reported") if group == "answer" else ("text",)):
            text = r.get(field)
            if not isinstance(text, str) or not text.strip() or text != text.strip():
                raise Refused(f"{key}: text")
            if DIGIT.search(text) or COIN.search(text):
                raise Refused(f"{key}: a digit or a coin word")
            for slot in SLOT.findall(text):
                if slot not in SLOTS:
                    raise Refused(f"{key}: unknown slot {slot}")
            if any(ord(c) < 32 for c in text) or "{" in SLOT.sub("", text) or "}" in SLOT.sub("", text):
                raise Refused(f"{key}: stray character")
        if group == "msg":
            if r.get("for") not in ("any", "close", "far") or len(r["text"]) > d["msg_max"]:
                raise Refused(f"{key}: message line")
            if set(SLOT.findall(r["text"])) - {"them", "their"}:
                raise Refused(f"{key}: a message line may only use them/their")
    for kind, route in KIND_ROUTES:
        if not d["ask"].get(f"{kind}.{route}"):
            raise Refused(f"no ask line for {kind}.{route}")
    for group in ("msg", "yes", "no", "neutral", "answer", "return", "thanks", "reward", "as_heard"):
        if not d.get(group):
            raise Refused(f"no {group} line")
    for group in ("reward", "as_heard"):                 # v6: one fixed sentence each, with no slot to fill
        if len(d[group]) != 1 or SLOT.search(d[group][0]["text"]):
            raise Refused(f"{group}: exactly one line, no slot")
    return True


def load(path=PATH):
    m = os.path.getmtime(path)
    if _cache["mtime"] != (path, m):
        with open(path, encoding="utf-8") as fh:
            data = json.load(fh)
        check_lists(data)
        _cache["data"], _cache["mtime"] = data, (path, m)
    return _cache["data"]


def text_hash(entry):
    """The hash stored on a row beside a line's key. An answer hashes both of its forms."""
    return sha(entry["said"] + "\n" + entry["reported"]) if "said" in entry else sha(entry["text"])


def by_key(d, key):
    for _, _, r in _entries(d):
        if r["key"] == key:
            return r
    return None


def messages_for(d, route, relation):
    """Which message lines fit: any; 'close' for close family by relation word; 'far' for a cousin or a stand-in."""
    close = relation in d["close_relations"]
    far = route in ("cousin", "standin")
    return [r for r in d["msg"] if r["for"] == "any" or (r["for"] == "close" and close) or (r["for"] == "far" and far)]


# ---- writing a sentence ------------------------------------------------------------------------------

def a_trade(trade):
    """'baker' -> 'a baker', 'innkeeper' -> 'an innkeeper'. The trade is the town list's current one, lower-cased."""
    t = " ".join(str(trade).split()).lower()
    if not t or not re.fullmatch(r"[a-z][a-z' \-]{1,40}", t):
        raise Refused("trade")
    return ("an " if AN.match(t) else "a ") + t


def pronouns(sex):
    if sex == "man":
        return {"them": "him", "their": "his"}
    if sex == "woman":
        return {"them": "her", "their": "her"}
    raise Refused("sex")


def _sentence_case(text):
    """A slot can open a sentence ('{a_trade} in {town}, and ...'): capitalise the first letter of each sentence."""
    out, start = [], True
    for i, ch in enumerate(text):
        if start and ch.isalpha():
            ch, start = ch.upper(), False
        elif ch in ".?!" and text[i + 1:i + 2] in (" ", ""):
            start = True
        elif not ch.isspace() and ch not in "\"'(—":
            start = start and False
        out.append(ch)
    return "".join(out)


def fill(template, values, speech_max=300):
    """One finished spoken sentence, or Refused. Every slot the template names must be given and non-empty; the result
    holds no digit and no coin word, has no brace left, and fits the speech limit WHOLE - a line is never cut (3.3)."""
    def put(m):
        v = values.get(m.group(1))
        if not isinstance(v, str) or not v.strip():
            raise Refused(f"slot {m.group(1)}")
        if "{" in v or "}" in v or any(ord(c) < 32 for c in v):
            raise Refused(f"slot {m.group(1)}: stray character")
        return v.strip()
    text = _sentence_case(SLOT.sub(put, template))
    if "{" in text or "}" in text:
        raise Refused("brace left")
    if DIGIT.search(text) or COIN.search(text):
        raise Refused("a digit or a coin word")
    if len(text) > speech_max:
        raise Refused("too long")
    return text


def message_text(d, msg_key, sex_of_subject):
    """The {msg} slot: a message line with him/her filled for the person the word is FOR."""
    r = by_key(d, msg_key)
    if r is None or "for" not in r:
        raise Refused("message key")
    return SLOT.sub(lambda m: pronouns(sex_of_subject)[m.group(1)], r["text"])


def answer_forms(d, answer_key, receiver_name):
    """({said}, {reported}) for one answer; {R} in the reported form is the relative's name."""
    r = by_key(d, answer_key)
    if r is None or "said" not in r:
        raise Refused("answer key")
    return r["said"], r["reported"].replace("{R}", receiver_name)


def returns_for(d, reported):
    """Return templates this answer can be said in: one template reads on only from a 'that ...' form."""
    return [r for r in d["return"] if not r.get("needs_that") or reported.startswith("that ")]
