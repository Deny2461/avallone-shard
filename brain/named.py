"""Phase 5c: the ten named NPCs - durable memory as CLAIMS, never as facts (design v2 after Codex, 2026-10-30).

- A visit summary is structured JSON only: {"claims": [things the player said], "topics": [short labels]}. No "what happened"
  field, and the NPC's own replies are never summarised (the summariser sees only the player's lines) - so nothing the
  player said can come back as something the NPC knows, and nothing the NPC once said becomes evidence (Codex finding 1).
- Attribution is fixed code wording, never the model's: They told you: "..." (their word, not a known fact).
- Every real player name in a summary becomes "someone"; the speaker is always "they".
- Familiarity is counted from distinct visit days (live `visited` events) by the brain and returned as data (finding 4).
"""
import re
import unicodedata

import decency
import rumour_speech

MAX_CLAIMS, MAX_TOPICS = 3, 3
MAX_CLAIM_CHARS, MAX_TOPIC_CHARS = 120, 40
BANDS = (2, 5, 12)            # days for: known face, regular, old friend (a lever: brain CFG named_bands)
BAND_NAMES = ("someone new - today or one earlier day is all you have seen of them", "a known face", "a regular",
              "an old friend")
# Wording that would turn a claim into knowledge. A summary using it is dropped whole.
CERTAIN = re.compile(r"\b(you learned|you learnt|it happened|is a fact|known fact|in truth|it is true|confirmed|"
                     r"you know that|you now know|proved|proven)\b", re.IGNORECASE)

SUMMARY_RULES = """You keep short notes for an innkeeper or townsperson about one visitor, after a visit.
You are given ONLY the lines the visitor said, numbered. Write JSON with two lists:
- "claims": at most 3 things the visitor STATED about themselves or the world. For each, give "line" (the line's number) and
  "text": a whole statement COPIED WORD FOR WORD from that one line - e.g. {"line": 2, "text": "I slew an ancient wyrm in
  Destard yesterday"}. Keep every "not", "never" and number exactly as written. Never copy from a question, a joke or
  sarcasm, and never cut a sentence so its meaning changes. Leave out greetings, questions and small talk.
- "topics": at most 3 labels of two to four words for what the visit was about, e.g. "buying a room", "dragon hunting".
Never add anything that is not in the lines. If there is nothing worth keeping, return empty lists."""

SCHEMA = {
    "type": "object",
    "properties": {
        "claims": {"type": "array", "maxItems": MAX_CLAIMS, "items": {
            "type": "object", "properties": {"line": {"type": "integer"}, "text": {"type": "string"}},
            "required": ["line", "text"]}},
        "topics": {"type": "array", "items": {"type": "string"}, "maxItems": MAX_TOPICS},
    },
    "required": ["claims", "topics"],
}


class Unusable(Exception):
    """The summary is not safe or not the shape asked for. Nothing is stored."""


def player_lines(history):
    """The player's own lines only, oldest first, at most 12. Gossip markers and NPC lines never reach the summariser."""
    lines = []
    for h in history or []:
        if not isinstance(h, dict) or h.get("speaker") != "player":
            continue
        t = " ".join(str(h.get("text") or "").split())[:300]
        if t and not decency.check(t):   # 2a-0: a line that trips the house rule never reaches memory
            lines.append(t)
    return lines[-12:]


def transcript(history):
    """What the summariser sees: the player's lines, numbered from 1 (the numbers are how claims point back)."""
    return "\n".join(f"{i}. {t}" for i, t in enumerate(player_lines(history), 1))


def _swap(text, name, to):
    return re.sub(r"(?<![\w])" + re.escape(name) + r"(?![\w])", to, text, flags=re.IGNORECASE)


def _scrub(text, speaker, roster):
    """Full names first, then the forms the name check also refuses: a first name alone ("Brynn" for "Brynn Handy")
    and a word one letter off a name ("Tharazin"). The speaker becomes "they", everyone else "someone"."""
    me = (speaker or "").strip()
    names = sorted({str(p.get("name", "")).strip() for p in roster or ()} | ({me} if me else set()), key=len, reverse=True)
    out = text
    for name in names:
        if len(name) >= 2:
            out = _swap(out, name, "they" if name.lower() == me.lower() else "someone else")
    for name in names:
        to = "they" if name.lower() == me.lower() else "someone else"
        parts = name.split()
        if len(parts) > 1 and len(parts[0]) >= 4:
            out = _swap(out, parts[0], to)
        flat = rumour_speech._norm(name.replace(" ", ""))
        if len(flat) >= 5:
            for w in set(re.findall(r"[^\W\d_]{4,}", out)):
                n = rumour_speech._norm(w)
                if n == flat or rumour_speech._edit1(n, flat):
                    out = _swap(out, w, to)
    return out


# Words a faithful claim or label may add that the player never said: pronoun swaps, the name scrub, linking words.
FREE_WORDS = frozenset("""they them their theirs themselves someone else that this these those with from have having been
were will would could should about into onto than then there here what when where which while some very just also only
much many more most said says told tell named called name does doing done being other another every each such""".split())


def _stems(text):
    return {w[:4] for w in re.findall(r"[a-z]+", str(text).lower()) if len(w) >= 4 and w not in FREE_WORDS}


TOPIC_SHORT_FREE = frozenset("""a an the my your his her its our their them they of to in on at for with and or by
from as is was are be has had new own""".split())


def _topic_supported(core, source):
    """Every substantive word of a topic, short ones included ("my dog"), must be in the player's own lines - exact or
    by its first four letters. Only reviewed generic words are free (cards.TOPIC_FREE, TOPIC_SHORT_FREE, FREE_WORDS)."""
    have = [w for w in re.findall(r"[a-z]+", str(source).lower())]
    have_set, have_stems = set(have), {w[:4] for w in have if len(w) >= 4}
    for w in re.findall(r"[a-z]+", str(core).lower()):
        if len(w) < 2 or w in TOPIC_SHORT_FREE or w in FREE_WORDS:
            continue
        if w in have_set or (len(w) >= 4 and w[:4] in have_stems) or (w.endswith("s") and w[:-1] in have_set):
            continue
        return False
    return True


def supported(item, source, whole=True):
    """Topics only now (labels may summarise): at least one content word must be in the player's lines."""
    mine = _stems(item)
    if not mine:
        return not whole
    have = _stems(source)
    return mine <= have if whole else bool(mine & have)


QUESTION_START = frozenset("""where what who whom whose how why when which did do does can could would will is are was were
have has shall should may might""".split())
SARCASM = re.compile(r"^\W*(oh sure|oh yes|oh aye|yeah right|sure sure|as if)\b|\b(lol|lmao|/s)\b|\*[^*]+\*", re.IGNORECASE)
NEGATION = frozenset("""not never no nor nothing nobody none cannot can't don't didn't doesn't isn't wasn't weren't won't
wouldn't couldn't shouldn't haven't hasn't hadn't ain't""".split())
LINKS = frozenset(("and", "but", "so", "also", "plus", "then", "though"))
CUT = ",.;:!?—–-"


def _norm(t):
    return " ".join(str(t).split()).strip().strip("\"'").strip()


def _is_question(line):
    words = re.findall(r"[a-z']+", line.lower())
    return line.rstrip().endswith("?") or bool(words) and words[0] in QUESTION_START


def _boundary_before(line, i):
    before = line[:i].rstrip()
    if not before:
        return True
    if before[-1] in CUT:
        return True
    last = re.findall(r"[a-z']+", before.lower())
    return bool(last) and last[-1] in LINKS


def _boundary_after(line, j):
    after = line[j:].lstrip()
    if not after:
        return True
    if after[0] in CUT:
        return True
    first = re.findall(r"[a-z']+", after.lower())
    return bool(first) and first[0] in LINKS


def _negation_outside(line, i, j):
    outside = (line[:i] + " " + line[j:]).lower()
    outside = re.sub(r"^\W*(no|nah|nay)\s*,", " ", outside)   # a leading "No," is an answer, not a negation of the claim
    return any(w in NEGATION or w.endswith("n't") for w in re.findall(r"[a-z']+", outside))


def extract_span(lines, number, text):
    """The exact statement from player line `number` (1-based) that `text` copies, or None if it does not prove out."""
    if not isinstance(number, int) or isinstance(number, bool) or not isinstance(text, str):
        raise Unusable("not the JSON asked for")
    if not 1 <= number <= len(lines):
        return None
    line = lines[number - 1]
    span = _norm(text).rstrip(CUT + " ")
    if len(span.split()) < 2 or _is_question(line) or SARCASM.search(line):
        return None
    low, sp = line.lower(), span.lower()
    i = low.find(sp)
    while i >= 0:
        j = i + len(sp)
        if _boundary_before(line, i) and _boundary_after(line, j) and not _negation_outside(line, i, j) \
                and not framed_before(line, i) and not loose_reference(line[i:j]):
            return line[i:j]
        i = low.find(sp, i + 1)
    return None


# 5c-2 2a (Codex design round 2 finding 4): a statement cut out of a larger sentence must not lose what framed it.
# "The song goes: my mule is called Tonk" / "If I get a mule, I will call it Tonk" / "He said: I slew a wyrm" - the cut
# piece is NOT their own plain statement. The whole line, copied as it stands, is still allowed (it keeps the frame).
REPORTING = re.compile(r"\b(goes|went|sang|sing|sings|singing|said|say|says|saying|told|tells|wrote|writes|reads|read|"
                       r"heard|hear|claims?|claimed|quote|quoted|asks?|asked|recites?|recited|chants?|chanted|"
                       r"rhyme|verse|song|ballad|tune|lyric|lyrics)\b", re.IGNORECASE)
CONDITIONAL = frozenset("""if unless when whenever once suppose supposing imagine wish wished wishes hope hoped hopes
maybe perhaps might would could should someday dream dreamt dreamed pretend""".split())
QUOTES = "\"“”«»'‘’"


def framed_before(line, i):
    """True when the text before position i frames what follows as a quote, report, wish or condition."""
    before = line[:i]
    if not before.strip():
        return False
    lone = len(re.findall(r"(?<![A-Za-z])['‘’]|['‘’](?![A-Za-z])", before))   # not the ' in I'm / Tonk's
    if any(q in before for q in "\"“”«»") or lone % 2 == 1 or before.rstrip().endswith(":"):
        return True
    words = re.findall(r"[a-z']+", before.lower())
    return bool(REPORTING.search(before)) or any(w in CONDITIONAL for w in words)


# A pronoun or pointer whose noun is not in the same span ("they think he may have died", "it was huge", "over there").
LOOSE = re.compile(r"\b(he|she|him|her|his|hers|it|its|they|them|their|theirs|that one|this one|the other one|there|"
                   r"those ones|these ones)\b", re.IGNORECASE)
STARTERS = frozenset("""the a an my our your his her their its it he she they we you this that these those there then
so and but yes no well oh now when after before once last yesterday today tomorrow tonight aye nay if as at in on""".split())
EXISTENTIAL = re.compile(r"\bthere\s+(is|are|was|were|be|'s|’s)\b|\bthere's\b|\bthere’s\b", re.IGNORECASE)


def loose_reference(span):
    """True if the span leans on something it does not contain. A name (a capitalised word after the first) or a
    known noun from the concept list before the pronoun counts as its antecedent."""
    s = EXISTENTIAL.sub(" ", span)
    m = LOOSE.search(s)
    if not m:
        return False
    head = s[:m.start()]
    words = re.findall(r"[A-Za-z][\w'-]*", head)
    if any(w[0].isupper() and w not in ("I", "I'm", "I've", "I'll", "I'd") and (n > 0 or w.lower() not in STARTERS)
           for n, w in enumerate(words)):
        return False
    try:
        import cards   # the concept list; optional so this module still imports on its own
        if any(cards.concepts().leaf(w.lower()) for w in words):
            return False
    except Exception:
        pass
    return True


def notes_from(parsed, history, speaker="", roster=()):
    """What the summary endpoint stores (Codex 5c-1 round 2 finding 1): extractive claims proven against the player's own
    numbered lines, then pronoun swap + name scrub by code; topics that share a word with those lines."""
    if not isinstance(parsed, dict) or not isinstance(parsed.get("claims"), list) or not isinstance(parsed.get("topics"), list):
        raise Unusable("not the JSON asked for")
    if len(parsed["claims"]) > MAX_CLAIMS or len(parsed["topics"]) > MAX_TOPICS:
        raise Unusable("too many items")
    lines = player_lines(history)
    me = (speaker or "").strip()
    my_names = [n for n in {me, me.split()[0] if me else ""} if len(n) >= 3]

    def names_speaker(t):   # "My name's Racer" says nothing she does not know, and scrubs to "their name's they"
        return any(re.search(r"(?<![\w])" + re.escape(n) + r"(?![\w])", t, re.IGNORECASE) for n in my_names)

    claims = []
    for c in parsed["claims"]:
        if not isinstance(c, dict):
            raise Unusable("not the JSON asked for")
        span = extract_span(lines, c.get("line"), c.get("text"))
        if span is None or names_speaker(span):
            continue
        out = _clean_item(span, MAX_CLAIM_CHARS + 40, speaker, roster)
        if out and len(out) <= MAX_CLAIM_CHARS:     # never cut a statement short - a cut could change its meaning
            claims.append(out)
    source = " ".join(lines)
    topics = []
    for t in parsed["topics"]:
        if not isinstance(t, str):
            raise Unusable("not the JSON asked for")
        import cards
        if _topic_supported(cards.topic_words(t), source) and not names_speaker(t):   # Codex 2a findings 3 + r2 4
            out = _clean_item(t, MAX_TOPIC_CHARS, speaker, roster)
            if out:
                topics.append(out)
    return {"claims": claims, "topics": topics}


# The visitor in their own words, turned into "they" (deterministic; Codex 5c-1 allows pronoun rewriting only).
PRONOUNS = [(r"i'm|i am", "they are"), (r"i've|i have", "they have"), (r"i'll|i will", "they will"), (r"i'd", "they would"),
            (r"i was", "they were"), (r"myself", "themselves"), (r"my", "their"), (r"me", "them"), (r"i", "they")]


def _third_person(text):
    # "mine" is theirs only as a possessive ("the axe is mine"), never the verb ("I can smith and mine") - re-trial t5x
    out = re.sub(r"\b(is|are|was|were)\s+mine\b", r"\1 theirs", text, flags=re.IGNORECASE)
    for pat, to in PRONOUNS:
        out = re.sub(r"(?<![\w'])(?:" + pat + r")(?![\w'])", to, out, flags=re.IGNORECASE)
    return out


def _clean_item(text, limit, speaker, roster):
    t = " ".join(str(text or "").split()).strip().strip('"').strip("'").strip()
    if not t:
        return ""
    if CERTAIN.search(t):
        raise Unusable("certainty wording")
    if rumour_speech._spoofy(t) or rumour_speech.LOOSE_RE.search(t) or "[" in t or "]" in t:
        raise Unusable("odd characters")
    t = _third_person(t)
    t = _scrub(t, speaker, roster)
    guard = rumour_speech.Offer()   # always strict: stored notes never keep another player's name (FREE_NAMES is speech only)
    if rumour_speech.check(t, guard, roster, frozenset()):
        return ""   # still looks like a player's name: lose this one line, keep the rest of the note
    return t[:limit].rstrip()


def validate(parsed, speaker="", roster=(), source=None):
    """Clean already-trusted claim strings (scrub, pronouns, certainty, odd characters). NOT the summary endpoint's path -
    that is notes_from(), which proves every claim against the player's own lines first (Codex 5c-1 round 2 finding 1)."""
    if not isinstance(parsed, dict) or not isinstance(parsed.get("claims"), list) or not isinstance(parsed.get("topics"), list):
        raise Unusable("not the JSON asked for")
    if len(parsed["claims"]) > MAX_CLAIMS or len(parsed["topics"]) > MAX_TOPICS:
        raise Unusable("too many items")
    if any(not isinstance(x, str) for x in parsed["claims"] + parsed["topics"]):
        raise Unusable("not the JSON asked for")
    raw_claims, raw_topics = parsed["claims"], parsed["topics"]
    if source is not None:
        raw_claims = [x for x in raw_claims if supported(x, source, whole=True)]
        raw_topics = [x for x in raw_topics if supported(x, source, whole=False)]
    claims = [c for c in (_clean_item(x, MAX_CLAIM_CHARS, speaker, roster) for x in raw_claims) if c]
    topics = [c for c in (_clean_item(x, MAX_TOPIC_CHARS, speaker, roster) for x in raw_topics) if c]
    return {"claims": claims, "topics": topics}


def render(facts):
    """Fixed wording (Codex finding 1): the model never chooses the attribution or the certainty."""
    parts = [f'They told you: "{c}" (their word, not a known fact).' for c in (facts.get("claims") or [])[:MAX_CLAIMS]]
    topics = [str(t) for t in (facts.get("topics") or [])[:MAX_TOPICS] if t]
    if topics:
        parts.append("You talked about: " + ", ".join(topics) + ".")
    return " ".join(parts) or "They stopped by; nothing much was said."


def band(days, bands=BANDS):
    n = sum(1 for b in bands if days >= b)
    return BAND_NAMES[min(n, len(BAND_NAMES) - 1)]


def familiarity_line(days, last_day, today, bands=BANDS):
    if days <= 0:
        return "You have never met this person before."
    out = f"To you they are {band(days, bands)}: you have seen them on {days} day{'s' if days != 1 else ''}"
    try:
        from datetime import date
        ago = (date.fromisoformat(today) - date.fromisoformat(last_day)).days if last_day and today else None
    except ValueError:
        ago = None
    if days == 1 and ago == 0:
        return "You first met them earlier today; before today you had never seen them. Let it colour your tone only."
    if ago is not None and ago > 0:
        out += f", last {ago} day{'s' if ago != 1 else ''} ago"
    elif ago == 0 and days > 1:
        out += ", including earlier today"
    return out + ". Let it colour your tone only."


# 5c-1 trial (2026-10-30): asked "you remember my dog's name?", an old friend with no such note got "I remember you
# mentioning your hound" - she took the question's word for it. These notes are ALL she remembers of them.
NO_INVENTING = ("These notes are all you remember of them. If they ask about something from before that is not here, say "
                "plainly that you do not recall it - never go along with it, never say you are keeping something for "
                "them, never make up a memory. What they tell you now is news to you: respond to it.")


def memory_block(lines, fam_line, met=True, speaker=""):
    out = [f"You are talking to {speaker}." if speaker else ""] + ([fam_line] if fam_line else [])
    out = [x for x in out if x]
    if lines:
        out.append("What you remember of this person from earlier visits (your own notes; anything they told you is only "
                   "their word). Bring one up only when it fits naturally - never recite them:\n- " + "\n- ".join(lines))
    if met:
        out.append(NO_INVENTING)
    return "\n".join(out)


# ---- 5c-2 step 2a (design v1.2): typed facts by narrow code patterns, the pre-check, the speaker's name ----------
# A lasting fact is created ONLY when a whole plain statement matches one of these patterns (Codex round 2 finding 4).
# The model never chooses a fact's kind. Anything else stays an attributed episode, or nothing.
_NAME = r"(?P<name>[A-Za-z][a-z'\-]{1,15})"
PET_PATTERNS = tuple(re.compile(p, re.IGNORECASE) for p in (
    r"\bmy (?P<animal>[a-z]+(?: [a-z]+)?) (?:is|'s|’s) (?:called|named) " + _NAME + r"\b",
    r"\bmy (?P<animal>[a-z]+(?: [a-z]+)?)(?:'s|’s) name is " + _NAME + r"\b",
    r"\b" + _NAME + r" is my (?P<animal>[a-z]+(?: [a-z]+)?)\b",
    r"\bi (?:call|named) my (?P<animal>[a-z]+(?: [a-z]+)?) " + _NAME + r"\b",
))
# "Tonk is a mule, not a horse" corrects the KIND of a pet already known to be theirs - it never makes a new pet
# (Codex 2a code review finding 1: "Pip is a mule" made the visitor own Pip). Stored as petfix|..., which the cards
# apply only to an entity this visitor already named as their pet.
# Live play 2026-11-01: "Got anything for my mule Tonk?" taught her nothing. The name straight after the animal, with a
# capital letter (case-sensitive on purpose: "my mule needs" is not a name), names a pet even inside a question - the
# question presupposes the pet; the usual negation/frame/qualifier checks still apply.
PET_APPOSITIVE = re.compile(r"\b[Mm]y (?P<animal>[a-z]+(?: [a-z]+)?) (?P<name>[A-Z][a-z'\-]{1,15})\b")
PET_FIX = re.compile(r"\b" + _NAME + r" is an? (?P<animal>[a-z]+(?: [a-z]+)?)\b", re.IGNORECASE)
NOT_A_NAME = frozenset("""after for by the a an and or but my your his her their our its it he she they we you i me
him them this that these those who what which here there now then still just very really not never no
also too so is was are be been called named""".split())
TRADE_PATTERNS = (
    re.compile(r"\b(?:i am|i'm|i’m) an? (?P<trade>[a-z]+)(?!['’\w])(?: and (?:an? )?(?P<trade2>[a-z]+)(?!['’\w]))?"
               r"(?: by trade)?\b", re.IGNORECASE),   # not "a smith's daughter"; "a miner and a lumberjack" = both
    re.compile(r"^\W*(?:an? )?(?P<trade>[a-z]+) by trade\W*$", re.IGNORECASE),   # the whole line only (finding 1)
    re.compile(r"\bi work as an? (?P<trade>[a-z]+)(?!['’\w])", re.IGNORECASE),
)
HOME_PATTERNS = tuple(re.compile(p, re.IGNORECASE) for p in (
    r"\bi (?:live|dwell) in (?P<place>[a-z]+(?: [a-z]+)?)\b",
    r"\bmy home is (?:in )?(?P<place>[a-z]+(?: [a-z]+)?)\b",
))   # "I am from X" is where they come from, not where they live (Codex 2a round 2 finding 3) - no card
REQUEST_PATTERNS = (
    re.compile(r"^(?:please,? )?(?:could|can|would|will) you,?(?: please)? (?P<what>(?:keep|hold|save|reserve|mind|watch|"
               r"look after|find|fetch|set aside|put aside) .{3,70}?)[\s?.!]*$", re.IGNORECASE),
    re.compile(r"^(?:please,? )?(?P<what>(?:keep|hold|save|reserve|mind|watch|look after|set aside|put aside) "
               r"(?:my|this|these|that|a|an|the|me) .{2,70}?)(?:,? please)?[\s.!]*$", re.IGNORECASE),
)
MAX_ITEMS = 6


# Words after a match, in the same sentence, that take the statement back (Codex 2a finding 1): "if I ever get one",
# "only in my dreams", "or so the song goes".
QUALIFIED_AFTER = re.compile(r"\b(if|unless|only in|in my dreams?|dreams?|dreaming|someday|one day|some day|or so|song|songs|tale|"
                             r"story|they say|maybe|perhaps|hope|hoped|wish|wished|pretend|pretending|imagine|imagined|"
                             r"kidding|joking|jest|supposedly|going to|gonna|want to|wanted to|used to|anymore|"
                             r"no longer)\b", re.IGNORECASE)


def _plain(line, start, end):
    """A typed fact's sentence must stand as a plain statement: no frame before it, no negation before it or inside
    the matched words ("Tonk is a mule, NOT a horse" negates the horse, not the mule), and nothing after it in the
    same sentence that qualifies it ("... if I ever get one")."""
    if framed_before(line, start) or SARCASM.search(line):
        return False
    if any(w in NEGATION or w.endswith("n't") for w in re.findall(r"[a-z']+", line[:end].lower())):
        return False
    rest = re.split(r"[.!?;]", line[end:], maxsplit=1)[0]
    return not QUALIFIED_AFTER.search(rest)


# Live play 2026-11-01 (14:08 CST): "Did you know my mule's name is Tonk?" taught her nothing - a line starting "Did" or
# ending "?" is a question, and questions only try the "my mule Tonk" shape. A wrapper like this, followed straight away by
# the speaker's own "my ..." / "I ...", is a statement wrapped in a question: drop the wrapper (and the closing "?") and
# check the rest like any plain statement - the negation/frame/qualifier checks still apply to it.
WRAPPED_STATEMENT = re.compile(
    r"^\W*(?:(?:did|do)(?:n['’]t)? you (?:know|hear|remember)|have(?:n['’]t)? you heard|(?:did|have) i (?:tell|told) you|"
    r"(?:did|have) i mention(?:ed)?|you know)(?: that)?\s*,?\s+(?=(?:my|i)\b)", re.IGNORECASE)


def _unwrap_statement(line):
    m = WRAPPED_STATEMENT.match(line)
    return re.sub(r"[\s?]+$", "", line[m.end():]) if m else line


def typed_items(history, speaker="", roster=()):
    """Facts and requests from the player's own lines, by pattern only. Returns strings "kind|a|b" (the ledger keeps
    flat string lists): pet|<animal>|<Name>, trade|<trade>, home|<Place>, request|<what, in their words, as 'they'>."""
    import cards
    con = cards.concepts()
    names = {str(p.get("name", "")).strip().lower() for p in roster or ()} | {(speaker or "").strip().lower()}
    names |= {n.split()[0] for n in names if n}
    out = []

    def add(item):
        if item not in out and len(out) < MAX_ITEMS and len(item) <= 100:
            out.append(item)

    for line in player_lines(history):
        line = _unwrap_statement(line)
        question_only = _is_question(line) and not any(p.search(line) for p in REQUEST_PATTERNS)
        # Codex 2a round 2 finding 3: every match is applied in the order it was said, so the latest statement wins and
        # a kind correction only touches a pet already named as theirs EARLIER.
        events = []

        def animal_of(m):
            animal = m.group("animal").lower()
            if animal not in con.leaves_under("ANIMAL"):
                animal = animal.split()[-1]
            return animal if animal in con.leaves_under("ANIMAL") else None

        for pat in ((PET_APPOSITIVE,) if question_only else (PET_APPOSITIVE,) + PET_PATTERNS):
            for m in pat.finditer(line):
                name, animal = m.group("name"), animal_of(m)
                if not animal or name.lower() in NOT_A_NAME:
                    continue
                name = name[0].upper() + name[1:]
                if name.lower() in names or name in ("I", "My", "It", "He", "She") or not _plain(line, m.start(), m.end()):
                    continue
                events.append((m.start(), "pet", animal, name))
        for m in (() if question_only else PET_FIX.finditer(line)):
            name, animal = m.group("name"), animal_of(m)
            if not animal or name.lower() in NOT_A_NAME or name.lower() in names or not _plain(line, m.start(), m.end()):
                continue
            events.append((m.start(), "fix", animal, name[0].upper() + name[1:]))
        for pat in (() if question_only else TRADE_PATTERNS):
            for m in pat.finditer(line):
                trade = m.group("trade").lower()
                t2 = (m.groupdict().get("trade2") or "").lower()
                if trade in con.leaves_under("TRADE") and _plain(line, m.start(), m.end()):
                    # "I am a miner and a lumberjack" keeps both (live play 2026-11-01); a non-trade second word is dropped
                    both = t2 and t2 != trade and t2 in con.leaves_under("TRADE")
                    events.append((m.start(), "trade", f"{trade} and {t2}" if both else trade, None))
        for pat in (() if question_only else HOME_PATTERNS):
            for m in pat.finditer(line):
                place = m.group("place").lower()
                if place not in con.leaves_under("PLACE"):
                    place = place.split()[0]
                if place in con.leaves_under("PLACE") and _plain(line, m.start(), m.end()):
                    events.append((m.start(), "home", con.label(place), None))
        for _, kind, a, b in sorted(events, key=lambda e: e[0]):
            if kind == "pet":
                for x in [x for x in out if x.split("|")[0] in ("pet", "petfix") and x.split("|")[-1] == b]:
                    out.remove(x)
                add(f"pet|{a}|{b}")
            elif kind == "fix":
                mine = next((x for x in out if x.startswith("pet|") and x.endswith(f"|{b}")), None)
                if mine:   # named as theirs earlier in this visit: correct its kind in place
                    out[out.index(mine)] = f"pet|{a}|{b}"
                elif f"petfix|{a}|{b}" not in out:
                    for x in [x for x in out if x.startswith("petfix|") and x.endswith(f"|{b}")]:
                        out.remove(x)
                    add(f"petfix|{a}|{b}")
            else:   # trade / home: one current value - the latest said
                for x in [x for x in out if x.startswith(kind + "|")]:
                    out.remove(x)
                add(f"{kind}|{a}")
        if question_only:
            continue
        for pat in REQUEST_PATTERNS:
            m = pat.search(line)
            if m and not SARCASM.search(line):
                what = _clean_item(m.group("what"), 80, speaker, roster)
                what = what[:1].lower() + what[1:]
                if what:
                    add(f"request|{what}")
    return out


# The pre-check (Codex round 1 F6): skip the background model call for visits with nothing to remember. A load saver,
# never a safety line - a miss costs one disposable note. Every skip is logged with its reason.
STOCK_START = frozenset("""buy sell vendor bank banker balance train stable claim hire hello hi hey hail greetings
good bye goodbye farewell thanks thank cheers news gossip rumour rumours rumor rumors yes no aye nay ok okay""".split())
TRANSACTION = re.compile(r"^(?:i(?: want| need| would like|'d like|’d like| wish)(?: to)? ?(?:buy|sell|rent|get|have|order)?\s*|"
                         r"(?:give|sell|bring) me )(?:an? |some |the |one |two |a few )?"
                         r"(room|drink|ale|beer|wine|mead|meal|food|bed|bread|bandages?|reagents?|potions?|something|"
                         r"\w+ to (?:drink|eat))\b", re.IGNORECASE)


def worth_summarizing(history):
    """(True, "") if some player line could hold something to remember, else (False, reason)."""
    lines = player_lines(history)
    if not lines:
        return False, "no player lines"
    reasons = set()
    for line in lines:
        words = re.findall(r"[a-z']+", line.lower())
        if not words:
            reasons.add("no words")
        elif any(p.search(line) for p in REQUEST_PATTERNS):
            return True, ""
        elif _is_question(line):
            reasons.add("questions")
        elif words[0] in STOCK_START and len(words) <= 4:
            reasons.add("greeting or stock command")
        elif TRANSACTION.search(line):
            reasons.add("shopping")
        elif len(words) >= 3:
            return True, ""
        else:
            reasons.add("too short")
    return False, ", ".join(sorted(reasons))


# The speaker's name in the AI prompt (Codex round 1 F7): typed data, cleaned; an unsafe name is simply left out.
NAME_OK = re.compile(r"^[A-Za-z][A-Za-z '\-]{0,30}[A-Za-z]$")


def safe_name(name):
    n = unicodedata.normalize("NFKC", str(name or ""))
    n = "".join(ch for ch in n if unicodedata.category(ch)[0] != "C")
    n = " ".join(n.split())
    if not NAME_OK.match(n) or decency.check(n) or rumour_speech._spoofy(n) or "  " in n:
        return ""
    if re.search(r"\b(system|assistant|user|ignore|instruction|prompt|rules?)\b", n, re.IGNORECASE):
        return ""
    return n
