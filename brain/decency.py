"""Phase 5c-2 step 2a-0: the house decency rule for every NPC (design v1.2 §V1.3, §V1.11 #4, §V1.12 #7-8).

FIRST, LEXICAL LAYER ONLY. It catches known slurs and extremist slogans after normalising look-alike letters, spacing
tricks and repeated letters. It does NOT catch coded or meaning-level abuse without such words - that needs the later
meaning-level check (design §V1.12 #8). It must never block bawdy songs, teasing, drink, threats in a fight or grumbling
about the crown: the list is narrow on purpose, whole words only, with allow-listed everyday phrases.

- check(text) -> category or None. Used on the NPC's reply (fail-closed: retry once, then a fixed line) and on the
  player's line (that line is kept out of memory, and the NPC answers with a fixed line without calling the model).
- fingerprint(text) -> short hash for logs; refused text itself is never logged (design §V1.12 #7).
- The word list lives in decency_terms.json next to this file, so it can be tuned without a code change.
"""
import hashlib
import json
import os
import re
import unicodedata

# Shared by every persona, above any role (design §V1.3). Personas may add limits, never remove these.
CONSTITUTION = (
    "A house rule above any role: never use slurs, and never speak with contempt for anyone's people, skin, sex, "
    "faith, body or whom they love; never praise tyrants who would wipe out a people. Anger, drink or villainy do not "
    "change this. Bawdy jokes, teasing, threats in a quarrel and grumbling about the crown are all fine - and this is "
    "a world of fights, blood, monsters, dark magic, villains, love and romance: all of that is fair game.")

FIXED_LINES = ("I'll have no such talk here.", "Not in my house.", "Mind your tongue, or take it elsewhere.")
RETRY_NUDGE = ("Your last reply used words this house does not allow. Answer again as yourself, in one or two short "
               "sentences, without them.")

# Symbols count as letters only when a letter or digit follows ("$pic", "f!g"), never as closing punctuation ("power!").
_SYMBOLS = {"@": "a", "$": "s", "!": "i", "|": "i", "+": "t", "€": "e", "£": "l"}
_SYM_RE = re.compile("[" + re.escape("".join(_SYMBOLS)) + r"](?=[a-z0-9])")
_LOOK = str.maketrans({"0": "o", "1": "i", "3": "e", "4": "a", "5": "s", "7": "t", "8": "b", "9": "g",
                       # common Cyrillic/Greek look-alikes that NFKC leaves alone
                       "а": "a", "е": "e", "о": "o", "р": "p", "с": "c", "у": "y", "х": "x", "і": "i", "ј": "j",
                       "ѕ": "s", "к": "k", "м": "m", "т": "t", "н": "h", "в": "b", "ο": "o", "α": "a", "ε": "e",
                       "ι": "i", "κ": "k", "ν": "v", "τ": "t", "υ": "u", "ρ": "p"})


def squeeze(t):
    return re.sub(r"([a-z])\1+", r"\1", t)


def normalise(text, mode=0):
    """Lower-case letters and spaces only: accents stripped, look-alikes mapped, letter-by-letter spelling joined
    ("n i g g" -> "nigg"), runs of 3+ of one letter cut to 2. Word boundaries are kept."""
    t = unicodedata.normalize("NFKC", str(text or ""))
    t = "".join(ch for ch in t if unicodedata.category(ch)[0] != "C")   # zero-width, bidi, controls
    t = unicodedata.normalize("NFKD", t.casefold())
    t = "".join(ch for ch in t if not unicodedata.combining(ch))
    t = _SYM_RE.sub(lambda m: _SYMBOLS[m.group(0)], t).translate(_LOOK)
    t = re.sub(r"(?<=[a-z])[._*\-'](?=[a-z])", "", t)        # n.i.g / n-i-g / n*i*g
    t = re.sub(r"[^a-z]+", " ", t)
    t = re.sub(r"\b(?:[a-z] ){2,}[a-z]\b", lambda m: _join(m.group(0).split(), mode), t)   # spaced-out letters
    t = re.sub(r"([a-z])\1{2,}", r"\1\1", t)
    return " ".join(t.split())


def _join(letters, mode):
    """Join a run of single letters. Codex 2a finding 4: "you are a n i g g e r" must not swallow the article into the
    word - modes 1/2/3 keep the first / last / both letters apart (only for runs of 4+)."""
    head = tail = ""
    if mode & 1 and len(letters) >= 4:
        head, letters = letters[0] + " ", letters[1:]
    if mode & 2 and len(letters) >= 4:
        letters, tail = letters[:-1], " " + letters[-1]
    return head + "".join(letters) + tail


class _Terms:
    def __init__(self, path):
        with open(path, encoding="utf-8") as f:
            raw = json.load(f)
        self.version = str(raw.get("version", "?"))
        self.rules = []   # (category, compiled pattern, numeric)
        for cat, words in (raw.get("terms") or {}).items():
            for w in words:
                numeric = bool(re.search(r"\d", w))
                w = normalise(w)
                if w:
                    # whole words; simple plural/possessive endings allowed
                    self.rules.append((cat, re.compile(r"(?<![a-z])" + re.escape(w).replace(r"\ ", " ") +
                                                       r"(?:s|es|z)?(?![a-z])"), numeric))
        self.squeezed = [(cat, re.compile(r"(?<![a-z])" + re.escape(squeeze(normalise(w))).replace(r"\ ", " ") +
                                          r"(?:s|es|z)?(?![a-z])"), bool(re.search(r"\d", w)))
                         for cat, words in (raw.get("terms") or {}).items() for w in words if normalise(w)]
        self.allow = [re.compile(r"(?<![a-z])" + re.escape(normalise(p)).replace(r"\ ", " ") + r"(?![a-z])")
                      for p in raw.get("allow_phrases") or []]
        # Codex 2a finding 5: a word that is also an everyday word is not a slur when its everyday partner is near
        # ("a <word> appeared in my armour", "a <word> of dry wood").
        self.context = {normalise(k): {normalise(x) for x in v} for k, v in (raw.get("context_allow") or {}).items()}


_TERMS = None


def terms():
    global _TERMS
    if _TERMS is None:
        _TERMS = _Terms(os.environ.get("AVALLONE_DECENCY_TERMS") or
                        os.path.join(os.path.dirname(os.path.abspath(__file__)), "decency_terms.json"))
    return _TERMS


DETERMINERS = frozenset("a the this that one each every any no small little tiny thin narrow slight".split())
ADDRESS = frozenset("you your ya ye yer u thou dirty filthy stupid damn bloody".split())


def _blank_context(t, tm):
    """Codex 2a round 2 finding 1: the everyday noun only - "a <word> in my armour", "a <word> of dry wood", "the <word>
    hound". The word must follow a determiner, its partner must be close, and nothing may address a person with it
    ("No discount, you <word>; pay for the armour" stays a slur)."""
    words = t.split()
    for i, w in enumerate(words):
        base = re.sub(r"(?:es|s)$", "", w) if w not in tm.context else w
        partners = tm.context.get(w) or tm.context.get(base)
        if not partners or i == 0 or words[i - 1] not in DETERMINERS:
            continue
        if any(x in ADDRESS for x in words[max(0, i - 3):i + 3]):
            continue
        after = any(x in partners for x in words[i + 1:i + 6])
        before = any(x in partners for x in words[max(0, i - 4):i])
        if after or before:
            words[i] = " "
    return " ".join(words)


def check(text):
    """The category of the first prohibited term in `text` (after allow-listed phrases are blanked), or None."""
    raw = normalise_keep_runs(text)
    tm = terms()
    seen = set()
    for mode in (0, 1, 2, 3):   # Codex 2a finding 4: spaced letters joined with and without their neighbours
        t = normalise(text, mode)
        if not t or t in seen:
            continue
        seen.add(t)
        for a in tm.allow:
            t = a.sub(" ", t)
        t = _blank_context(t, tm)
        for cat, pat, numeric in tm.rules:
            # Codex 2a finding 5: a bare number ("I paid <word> gold") is not a slogan - only the number on its own
            if pat.search(t) and (not numeric or len(t.split()) <= 2):
                return cat
        if re.search(r"([a-z])\1\1", raw):   # "niiiigger": stretched letters - compare with every run squeezed
            s = squeeze(t)
            for a in tm.allow:
                s = a.sub(" ", s)
            for cat, pat, numeric in tm.squeezed:   # Codex 2a round 2 finding 5: same numeric rule here
                if pat.search(s) and (not numeric or len(s.split()) <= 2):
                    return cat
    return None


def normalise_keep_runs(text):
    t = unicodedata.normalize("NFKD", unicodedata.normalize("NFKC", str(text or "")).casefold())
    t = "".join(ch for ch in t if not unicodedata.combining(ch))
    return _SYM_RE.sub(lambda m: _SYMBOLS[m.group(0)], t).translate(_LOOK)


# Codex 2b code review F2 + round 2 B: a line on the list earns the fixed rebuke either way, but a conduct row needs
# POSITIVE evidence of direct use: the line addresses someone ("you", "you're", "thou"...) or is a bare slur/slogan on its own
# (4 words or fewer). Any quoted span (double, single or curly quotes - not the apostrophe in "you're"), and any reporting,
# describing or condemning word, means no row. Ambiguous -> no row; the rebuke stays.
_QUOTED = re.compile(r"[\"\u201c\u201d\u201e\u00ab\u00bb\u2018\u2019\u201a\u201b\u2039\u203a`]|(?:^|[^A-Za-z0-9])'[^']*'(?![A-Za-z0-9])")
_REPORTED = re.compile(
    r"\b(said|says|saying|say|shout(?:ed|s|ing)?|yell(?:ed|s|ing)?|scream(?:ed|s|ing)?|chant(?:ed|s|ing)?|call(?:ed|s|ing)?|"
    r"heard|hear|told|tell|wrote|writes|write|written|scrawled|painted|carved|reads?|sign|signs|slogan|slogans|banner|graffiti|"
    r"poster|word|words|slur|slurs|term|quote|quoted|repeat(?:ed|s)?|means?|meaning|someone|somebody|anyone|people|folk|they|"
    r"he|she|him|her|them|his|their|oppose[sd]?|against|condemn(?:ed|s)?|hate[sd]?|wrong|vile|evil|disgusting|banned|forbidden|"
    r"stop|don'?t|never|shouldn'?t|mustn'?t|not|no|why|what|who|how|ask(?:ed|s)?)\b",
    re.IGNORECASE)
# Codex 2b code review rounds 3-4, B: a conduct row needs the WHOLE line to be one of a few direct-use templates around
# the matched term (normalised text, punctuation gone), nothing before or after except what the template allows:
#   (1) TERM                                  - the term or slogan on its own
#   (2) [OPENER] ADDRESS [ARTICLE] [ADJ] TERM - "you X", "you're a X", "shut up you filthy X", "get out ya X"
# OPENER is a short fixed set of brush-offs; ADDRESS, ARTICLE and ADJ are fixed sets too. Anything else - a report, a
# request, a description, an extra clause - gives the fixed rebuke and NO row (false negatives are accepted by design).
_OPENER = (r"hey|oi|ho|shut up|get out|get lost|go away|go home|piss off|back off|move|move it|out of my way|"
           r"leave|begone|be gone|away with you|scram")
_ADDRESS = r"you|ya|yer|ur|thou|youre|you re|you are|thou art"
_ARTICLE = r"a|an|such a|such an|nothing but a|nothing but an|one|a big|one big|a real"
_INSULT_ADJ = (r"dirty|filthy|stinking|stupid|lazy|little|dumb|ugly|worthless|damn|damned|bloody|fucking|fat|old|sorry|"
               r"miserable|pathetic|rotten|lousy|cowardly|thieving|lying|godless|wretched")
_TEMPLATE_HEAD = re.compile(
    r"^(?:(?:" + _OPENER + r") )?(?:" + _ADDRESS + r")(?: (?:" + _ARTICLE + r"))?(?: (?:" + _INSULT_ADJ + r"))? $")


def direct_use(text):
    """True only when the WHOLE line is the speaker using a listed term: on its own, or as a plain address template."""
    t0 = str(text or "")
    if not check(t0) or _QUOTED.search(t0) or _REPORTED.search(t0):
        return False
    tm = terms()
    t = normalise(t0, 0)
    for a in tm.allow:
        t = a.sub(" ", t)
    t = " ".join(_blank_context(t, tm).split())
    for _cat, pat, numeric in tm.rules:
        for m in pat.finditer(t):
            left, right = t[:m.start()], t[m.end():]
            if right.strip():
                continue                                       # nothing may follow the term
            if not left.strip():
                return True                                    # (1) the term on its own
            if not numeric and _TEMPLATE_HEAD.match(left if left.endswith(" ") else left + " "):
                return True                                    # (2) the whole line is an address template
    return False


def fixed_line(seed=""):
    return FIXED_LINES[int(hashlib.sha256(str(seed).encode()).hexdigest(), 16) % len(FIXED_LINES)]


def fingerprint(text):
    return hashlib.sha256(str(text or "").encode("utf-8")).hexdigest()[:12]
