"""Rumour step, Part A - a player corrects a story about themselves (design: Projects/Avallone-Rumour-Step-Design.md,
sections 3 and 12; the owner, chat, 2026-11-11: - details only: how
many, what, where; "murder or death cannot be talked away").

What this module owns (no model call anywhere in it):
  * TABLE / STANDS - which details each kind of deed shows, and the kinds that take no correction at all (S4).
  * the closed lists (corrections.json + the towns of rumours.json): a corrected thing is a creature kind from the
    list, a corrected place a town or dungeon from the list. Never a word the player typed that is not on a list.
  * parse() - reads ONE player line by its own patterns (not the "tell me more" matcher) and returns the changes it
    plainly asks for, or None. A line that reads two ways, denies the deed, asks a question or looks like shopping is
    refused, never guessed.
  * accept_line() / neutral_line() - both written by code from the CHECKED values, never from the player's words.
  * check_facts() - the stored record's shape (kind "rumour_corrected"), checked on the way in.
The shard decides whether a correction counts (its ticket) and whether it is recorded; an accepting line is spoken only
after the record is in the shard's saved outbox. This module never stores anything.
"""
import hashlib
import json
import os
import re

KIND = "rumour_corrected"
FIELDS = ("count", "thing", "place")
MAX_COUNT = 999

# Which details a deed kind's wording shows (rumours.json "text"; test_corrections checks the two against each other).
TABLE = {
    "kill_notable": ("count", "thing", "place"),
    "tame_notable": ("count", "thing", "place"),
    "tame_failstreak": ("thing", "place"),
    "pacified": ("thing", "place"),
    "provoked": ("count", "thing", "place"),   # build re-check: "{a_subject}" shows the number once it is above one
    "fished_boots": ("count",),
    "pet_died": ("count", "thing"),
    "drunk": ("place",),
    "fished_bottle": (),
    "returned": (),
}
# S4: a murder or a death cannot be talked away - no detail of these is ever changed.
STANDS = ("murder", "died_notable", "died_pathetic")

STORY_ID = re.compile(r"^[1-9]\d{0,11}-[1-9]\d{0,11}$")
_WORD = re.compile(r"^[A-Za-z][A-Za-z' \-]{0,39}$")

# What the NPC is told when a detail is disputed but nothing is (or can be) changed. Plain rules, no story text.
STANDS_RULE = ("The traveller may dispute the tale you just told about them. That tale stands as it is told: do not "
               "agree to change any part of it, do not say you were wrong, and do not promise to tell it differently. "
               "Say only, kindly, that you tell it as it reached you.")
AS_HEARD_RULE = ("If the traveller disputes a detail of the tale you just told about them, do not agree to change it "
                 "and do not promise to tell it differently - say only that you tell it as it reached you.")

_UNITS = {"one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7, "eight": 8, "nine": 9, "ten": 10,
          "eleven": 11, "twelve": 12, "thirteen": 13, "fourteen": 14, "fifteen": 15, "sixteen": 16, "seventeen": 17,
          "eighteen": 18, "nineteen": 19, "once": 1, "twice": 2, "thrice": 3, "dozen": 12, "single": 1}
_TENS = {"twenty": 20, "thirty": 30, "forty": 40, "fifty": 50, "sixty": 60, "seventy": 70, "eighty": 80, "ninety": 90}
_NEGATORS = {"not", "wasn't", "wasnt", "weren't", "werent", "isn't", "isnt", "never", "no", "nor"}
_FILLER = {"a", "an", "the", "just", "only", "even", "near", "in", "at", "by", "one", "any", "really", "quite"}
_PREPOSITIONS = {"near", "in", "at", "by", "outside", "around", "from", "to", "of", "inside", "beside", "towards"}

# The deed itself denied, or the tale called a lie: never a correction (details only - S2, S3).
_DENIAL = re.compile(
    r"\b(was ?n[o']?t me|not me\b|never happened|did ?n[o']?t happen|i did ?n[o']?t|i did not|i never|"
    r"(a|all|pack of|damned?) lies?\b|you lie|liar|untrue|false\b|nonsense|made ?up|rubbish|slander|no such thing)", re.I)
# Shopping and requests: this line is about something else.
_OTHER_BUSINESS = re.compile(
    r"\b(buy|sell|selling|price|cost|costs|gold|coins?|vendor|train|teach|bank|give me|i want|i need|i'?d like|"
    r"i would like|how much|show me)\b", re.I)


def fields_for(deed_type):
    """The details a story of this kind can show; () for none. None for a kind that takes no ticket at all."""
    if deed_type in STANDS:
        return ()
    return TABLE.get(deed_type)


class Lists:
    """The closed lists. `things` are creature kinds (lowercase), `places` towns and dungeons in their own spelling.
    `plain_places` are places whose name is an everyday word ("Fire", "Shame"): they count only after a preposition."""

    def __init__(self, path, towns=()):
        with open(path, encoding="utf-8") as fh:
            raw = json.load(fh)
        things = [str(t) for t in raw.get("things", [])]
        places = [str(p) for p in raw.get("places", [])] + [str(t) for t in towns]
        for w in things + places:
            if not _WORD.match(w):
                raise ValueError(f"corrections lists: {w[:40]!r} is not a plain word")
        for t in things:
            if t != t.lower():
                raise ValueError(f"corrections lists: thing {t!r} must be lowercase")
        self.things = frozenset(things)
        self.places = {}
        for p in places:
            self.places.setdefault(p.lower(), p)     # lowercase -> the one canonical spelling
        self.plain_places = frozenset(str(p).lower() for p in raw.get("plain_places", []))
        if not self.plain_places <= set(self.places):
            raise ValueError("corrections lists: plain_places must be listed places")
        longest = lambda words: sorted(words, key=lambda w: (-len(w), w))
        self._thing_re = re.compile(
            r"(?<![a-z'])(" + "|".join(re.escape(t) for t in longest(self.things)) + r")(?:e?s)?(?![a-z'])") \
            if self.things else None
        self._place_re = re.compile(
            r"(?<![a-z'])(" + "|".join(re.escape(p) for p in longest(self.places)) + r")(?![a-z'])") \
            if self.places else None

    def thing(self, value):
        return value if isinstance(value, str) and value in self.things else None

    def place(self, value):
        """The canonical spelling of a listed place, or None."""
        return self.places.get(value.lower()) if isinstance(value, str) else None


def _tokens(text):
    return [(m.group(0), m.start(), m.end()) for m in re.finditer(r"[a-z]+(?:'[a-z]+)?|\d+", text)]


def _negated(tokens, index):
    """True when the value starting at token `index` is the thing being denied: "not forty", "wasn't a horse",
    "instead of Minoc", "rather than the dragon". Looks back over at most two filler words."""
    i, skipped = index - 1, 0
    while i >= 0 and tokens[i][0] in _FILLER and skipped < 2:
        i, skipped = i - 1, skipped + 1
    if i < 0:
        return False
    w = tokens[i][0]
    if w in _NEGATORS and not (w == "no" and i == 0):      # "No, it was four" - that "no" denies nothing after it
        return True
    if w == "of" and i >= 1 and tokens[i - 1][0] == "instead":
        return True
    return w == "than" and i >= 1 and tokens[i - 1][0] == "rather"


def _numbers(tokens, text=""):
    """[(value, first token index, last token index)] for every number in the line: digits, number words,
    "twenty five", "a hundred"."""
    out, i = [], 0
    while i < len(tokens):
        w = tokens[i][0]
        if w.isdigit():
            out.append((int(w) if len(w) <= 4 else MAX_COUNT + 1, i, i))
        elif w in _TENS:
            v = _TENS[w]
            joined = not text or text[tokens[i][2]:tokens[i + 1][1]].strip(" -") == "" if i + 1 < len(tokens) else False
            if joined and _UNITS.get(tokens[i + 1][0], 99) < 10 and tokens[i + 1][0] not in ("once", "single"):   # "forty, four" is two numbers
                out.append((v + _UNITS[tokens[i + 1][0]], i, i + 1))
                i += 1
            else:
                out.append((v, i, i))
        elif w == "hundred":
            out.append((100, i - 1 if i >= 1 and tokens[i - 1][0] in ("a", "one") else i, i))
        elif w in _UNITS:
            if w == "one" and i + 1 < len(tokens) and tokens[i + 1][0] == "hundred":
                pass                                      # counted with "hundred"
            elif w == "one" and not _counting_one(tokens, i):
                pass                                      # "that one", "no one": not a number
            else:
                out.append((_UNITS[w], i, i))
        i += 1
    return out


def _counting_one(tokens, i):
    prev = tokens[i - 1][0] if i >= 1 else ""
    return prev in ("only", "just", "was", "were", "not", "but", "caught", "killed", "slew", "tamed", "lost") or \
        (i + 1 < len(tokens) and tokens[i + 1][0] in ("time", "boot", "old"))


def _index_at(tokens, pos):
    for n, (_, start, end) in enumerate(tokens):
        if start <= pos < end:
            return n
    return -1


# Codex code R2 (RUM-C1): a correction is read by WHOLE-LINE shapes, not by picking values out of any sentence that has
# correction wording. R1's rule ("a number that ends its clause is the count") still took "I killed the dragon when I
# was four", "It was a dragon aged four" and "That was on day four" as "four dragons". Now the listed things, the listed
# places and the numbers in the line are replaced by slots (T, P, N) and what is left must match, from the first word to
# the last, one of a few fixed shapes:
#     it was / there were / 'twas  [only, just ...]  VALUE          "It was four."  "That was near Minoc."
#     I [only] caught / killed / tamed ...  VALUE                    "I only killed four, near Covetous."
#     make that / you mean / more like  VALUE
#   optionally with what it was NOT:  "..., not VALUE"  /  "it wasn't VALUE, it was VALUE"  /  "not a horse but a llama"
#     VALUE = a number [of them | <what that kind of story counts>]  |  [a number] a listed thing  |  near a listed place
# One value of each sort at most. Any word the shapes do not provide for - "when I was", "aged", "on day", "hours ago",
# "times bigger", a name, an unlisted place - leaves the line unmatched, and an unmatched line is refused, never mined.
_INTJ = (r"(?:(?:no|nay|nah|nope|well|actually|truly|honestly|in truth|truth is|the truth is|truth be told|"
         r"for the record|to be fair|look|listen|friend|hm+|ah|oh|ha)\s*,?\s+)*")
_SUBJ = r"(?:it|that|there|they|those|these)"
_COPULA = r"(?:" + _SUBJ + r"\s+(?:was|were)|'?twas|it's|that's)"
_NEG_COPULA = (r"(?:" + _SUBJ + r"\s+(?:was|were)\s*(?:n't|not)|" + _SUBJ + r"\s+(?:wasn't|weren't|wasnt|werent)|"
               r"'?twas\s+not)")
_SOFT = r"(?:(?:only|just|really|but|actually|in fact|more like|nearer|about|around|at most|no more than|merely)\s+)*"
# "I only <did it to> VALUE": only the verb of that kind of deed ("I lost four" says nothing about dragons slain).
_VERBS = {"kill_notable": r"(?:killed|slew|slayed|felled)", "tame_notable": r"(?:tamed)", "pacified": r"(?:calmed|soothed)",
          "provoked": r"(?:provoked)", "fished_boots": r"(?:caught|fished up|fished|pulled up|pulled|landed|hauled up|hauled)"}
_PREP = r"(?:near|in|at|by|outside|around|inside|beside)"
_DESCR = r"(?:\s+(?:old|big|large|huge|great|little|small|young|wild|full|grown|dead|live|fat|ugly|fine|poor|sorry|measly)){0,2}"
_COUNT_NOUNS = {"fished_boots": r"(?:old\s+)?boots?", "pet_died": r"times?"}
_SHAPES = {}


def _shape(kind):
    """The compiled whole-line pattern for a story of this deed kind (what its number counts differs by kind)."""
    if kind not in _SHAPES:
        tail = r"(?:\s+(?:of\s+(?:them|those|these|'em)|in\s+all|all\s+told" + \
               (r"|" + _COUNT_NOUNS[kind] if kind in _COUNT_NOUNS else "") + r"))?"
        core = r"(?:N\d+" + _DESCR + r"\s+T\d+|N\d+" + tail + r"|(?:(?:a|an|the)\s+)?T\d+)"
        val = r"(?:" + core + r"(?:\s*,?\s*" + _PREP + r"\s+P\d+)?|(?:" + _PREP + r"\s+)?P\d+)"
        did = (r"|i\s+" + _SOFT + _VERBS[kind] + r"\s+" + _SOFT + val) if kind in _VERBS else ""
        if kind == "drunk":                                   # the one story about where the player WAS
            did += r"|i\s+was\s+" + _SOFT + _PREP + r"\s+P\d+"
        full = (r"(?:" + _COPULA + r"\s+" + _SOFT + val + did +
                r"|(?:make that|you mean|more like)\s+" + val + r")")
        bare = _SOFT + val
        neg = (r"(?:(?:and\s+)?not\s+" + _SOFT + val + r"|" + _NEG_COPULA + r"\s+" + _SOFT + val +
               r"|instead of\s+" + val + r"|rather than\s+" + val + r")")
        sep = r"\s*[,;]?\s*(?:but\s+)?"
        _SHAPES[kind] = re.compile(
            r"^" + _INTJ + r"(?:(?P<p1>" + full + r")(?:" + sep + neg + r")?|(?P<p2>" + bare + r")" + sep + neg +
            r"|" + neg + sep + r"(?P<p3>" + full + r"|" + bare + r"))\s*[.!]*$")
    return _SHAPES[kind]


def _slots(text, lists, original=""):
    """(skeleton, values): the line with every listed place, listed thing and number replaced by P<i>, T<i>, N<i>.
    `original` = the same line before lowercasing: a place whose name is an everyday word counts only after a
    preposition AND written with its capital ("near Fire", "in Cove") - "it was in fire" is not the dungeon."""
    tokens = _tokens(text)
    spans = []                                                    # (start, end, letter, value)

    def free(a, b):
        return all(b <= s or a >= e for s, e, _, _ in spans)
    if lists._place_re is not None:
        for m in lists._place_re.finditer(text):
            i = _index_at(tokens, m.start())
            if m.group(1) in lists.plain_places and not (
                    i >= 1 and tokens[i - 1][0] in _PREPOSITIONS and original[m.start():m.start() + 1].isupper()):
                continue                                          # "fire", "shame": a place only as "near Fire"
            if free(m.start(), m.end()):
                spans.append((m.start(), m.end(), "P", lists.places[m.group(1)]))
    if lists._thing_re is not None:
        for m in lists._thing_re.finditer(text):
            if free(m.start(), m.end()):
                spans.append((m.start(), m.end(), "T", m.group(1)))
    for value, first, last in _numbers(tokens, text):
        a, b = tokens[first][1], tokens[last][2]
        if free(a, b):
            spans.append((a, b, "N", value))
    spans.sort()
    out, values, at = [], [], 0
    for start, end, letter, value in spans:
        out.append(text[at:start] + f"{letter}{len(values)}")
        values.append(value)
        at = end
    out.append(text[at:])
    return " ".join("".join(out).split()), values


def parse(utterance, fields, lists, current=None, kind=""):
    """The changes this one line plainly asks for: {"count": 4, "thing": "mule", "place": "Minoc"} limited to `fields`,
    or None. `current` = what the story says now ({field: value}); a "change" to the same value is dropped. `kind` =
    the story's deed kind (what its number counts). Returns (changes or None, reason) - the reason is for the log only."""
    original = " ".join(str(utterance or "").replace("\u2019", "'").split())
    text = original.lower()
    if len(text) != len(original):
        return None, "unusual letters"                        # lowercasing changed the length: positions cannot be trusted
    if not text or not fields:
        return None, "no fields"
    if len(_tokens(text)) > 22:
        return None, "too long"
    if "?" in text:
        return None, "a question"
    if _DENIAL.search(text):
        return None, "denial"
    if _OTHER_BUSINESS.search(text):
        return None, "other business"
    skeleton, values = _slots(text, lists, original)
    m = _shape(kind).match(skeleton)
    if not m:
        return None, "not one of the correction shapes"
    said = m.group("p1") or m.group("p2") or m.group("p3") or ""
    changes = {}
    for letter, index in re.findall(r"([NTP])(\d+)", said):
        f = {"N": "count", "T": "thing", "P": "place"}[letter]
        if f in changes:
            return None, f"two readings of the {f}"          # the shapes allow one value of each sort; belt and braces
        value = values[int(index)]
        if f == "count" and not 1 <= value <= MAX_COUNT:
            return None, "count out of range"
        if f not in fields or (current is not None and current.get(f) == value):
            continue                                          # a detail this story never shows, or already told that way
        changes[f] = value
    if not changes:
        return None, "nothing to change"
    return changes, "ok"


def _number_word(n):
    for table in (_TENS, _UNITS):
        for w, v in table.items():
            if v == n and w not in ("once", "twice", "thrice", "dozen", "single"):
                return w
    return str(n)


def _plural(word, n):
    return word if n == 1 or word.endswith("s") else word + "s"


def _a(word):
    return ("an " if word[:1].lower() in "aeiou" else "a ") + word


def detail(changes):
    """The corrected details in plain words, from the checked values only: "four", "a mule", "three mules near Minoc"."""
    parts = []
    count, thing, place = changes.get("count"), changes.get("thing"), changes.get("place")
    if count is not None and thing:
        parts.append(f"{_number_word(count)} {_plural(thing, count)}")
    elif count is not None:
        parts.append(_number_word(count))
    elif thing:
        parts.append(_a(thing))
    if place:
        parts.append("near " + place)
    return " ".join(parts)


ACCEPT = (
    "{Detail}, was it? Then that is the tale I will tell.",
    "Ah - {detail}. I will set folk straight.",
    "{Detail}? Well, you were there. That is how I will have it from now on.",
    "I had it wrong, then - {detail}. So be it.",
    "{Detail}, you say. Then that is how it goes.",
)
NEUTRAL = (
    "Hm. Folk tell it as they tell it.",
    "Well, you know how tales go round.",
    "That is how it reached me, is all.",
    "Could be. I only pass on what reaches me.",
    "So you say. The tale belongs to the town now, not to me.",
)


def _pick(lines, *seed):
    d = hashlib.sha256("|".join(str(s) for s in seed).encode()).digest()
    return lines[int.from_bytes(d[:4], "big") % len(lines)]


def accept_line(changes, seed=""):
    d = detail(changes)
    return _pick(ACCEPT, seed, "accept").format(Detail=d[:1].upper() + d[1:], detail=d)


def neutral_line(seed=""):
    return _pick(NEUTRAL, seed, "neutral")


def check_facts(facts):
    """The stored shape of one correction (kind rumour_corrected, npc = the world id): the story's id, the played
    second, and one to three typed changes. Nothing else - above all nothing of what the player said."""
    if not isinstance(facts, dict):
        raise ValueError("correction facts must be an object")
    extra = set(facts) - {"story", "pt", *FIELDS}
    if extra:
        raise ValueError(f"correction: unknown fact {sorted(extra)[0][:30]!r}")
    if not isinstance(facts.get("story"), str) or not STORY_ID.match(facts["story"]):
        raise ValueError("correction: story is not an event id")
    pt = facts.get("pt")
    if isinstance(pt, bool) or not isinstance(pt, int) or not -1 < pt < 10 ** 12:
        raise ValueError("correction: pt must be a whole number")
    if not any(f in facts for f in FIELDS):
        raise ValueError("correction: no change")
    if "count" in facts:
        c = facts["count"]
        if isinstance(c, bool) or not isinstance(c, int) or not 1 <= c <= MAX_COUNT:
            raise ValueError("correction: count must be 1 to 999")
    for f in ("thing", "place"):
        if f in facts and (not isinstance(facts[f], str) or not _WORD.match(facts[f])):
            raise ValueError(f"correction: {f} is not a plain word")


def changes_of(facts, deed_type, lists):
    """The changes of one stored correction that may be shown on a story of this kind: only its listed fields, only
    values still on the closed lists (a value an older or newer list does not know is simply not shown)."""
    out = {}
    for f in fields_for(deed_type) or ():
        if f not in facts:
            continue
        v = facts[f]
        if f == "count":
            if not isinstance(v, bool) and isinstance(v, int) and 1 <= v <= MAX_COUNT:
                out[f] = v
        elif f == "thing":
            if lists.thing(v):
                out[f] = v
        elif lists.place(v):
            out[f] = lists.place(v)
    return out


def default_path():
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), "corrections.json")
