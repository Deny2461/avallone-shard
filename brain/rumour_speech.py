"""Phase 5b: townsfolk and Mirabel speak the live rumours (design v2 after Codex, 2026-10-29).

The server decides what is true and who it is about; the model only says it.
- ONE named person per reply (Codex 5b finding 1): rumours about anyone else ride along only in their vague, name-free form.
- That person appears as a random per-request sentinel, e.g. "[[P-7f2a]]" written with the brackets below; the shard fills
  in the CURRENT name (finding 3). Deed-time names (facts.who) never reach a prompt.
- The reply is refused if it carries any other sentinel, a mangled one, a real player name (from the shard's roster), or
  letters from scripts used for look-alike spoofing.
- History never carries rumour text (finding 2): the shard stores a neutral marker; a follow-up re-offers the rumour by id.
"""
import random
import re
import secrets
import unicodedata

import smalltalk

OPEN, CLOSE = "⟦", "⟧"          # the mathematical white square brackets
SENTINEL_RE = re.compile(OPEN + r"P-[0-9a-f]{4}" + CLOSE)
LOOSE_RE = re.compile("[" + OPEN + CLOSE + r"]|\bP-[0-9a-f]{4}\b|\bPerson [A-Z]\b", re.IGNORECASE)
SPOOF_SCRIPTS = ("CYRILLIC", "GREEK", "ARMENIAN", "CHEROKEE", "FULLWIDTH", "MATHEMATICAL")
MAX_ROWS = 2
MAX_CHARS = 400

MODE_NEWS, MODE_GREET, MODE_FOLLOW = "news", "greet", "follow"
MODE_FREE = "free"     # Codex 2a finding 2: free names, but no deed beside a real name in ordinary talk
MODE_ABOUT = "about"   # a line that names a real player: gossip about that one person only (the owner, chat, 2026-10-30)

RNG = random.Random()
GRIM = frozenset({"died_notable", "died_pathetic", "murder", "pet_died"})
STOCK_OPENINGS = ('"They say"', '"I hear"', '"I heard"', '"Word is"', '"Rumour has it"', '"Folk say"')
# How the NPC tells it this time, one picked at random per reply (the owner, chat, 2026-10-29).
# (text, cheery) - cheery angles are skipped for deaths and murders.
ANGLES_ANY = [
    ("you doubt it, and say why", False),
    ("you are plainly impressed", True),
    ("you heard it from a customer, and say what sort of customer", False),
    ("you tie it to your own trade or your goods", False),
    ("you lower your voice as if it were a secret", False),
    ("you are tired of hearing about it already", False),
    ("you reckon the tale has grown in the telling", False),
    ("you wonder aloud what it means for the town", False),
    ("you find it funny", True),
    ("you mention it while grumbling about your own day", False),
    ("you are a little envious", True),
    ("you call it an omen, half joking", False),
    ("you compare it to some old tale of your town", False),
    ("you only half remember the details", False),
    ("you are scandalised", False),
]
ANGLES_SPEAKER = [
    ("you tease them about it", True),
    ("you ask them how it really happened", False),
    ("you congratulate them, grudgingly", True),
    ("you are not sure whether to believe it of them", False),
]
ANGLES_OTHER = [
    ("you poke a little fun at that person", True),
    ("you think that person is a braggart", True),   # 2026-11-02: never on a grim story ("a braggart" for a murder)
    ("you warn them not to end up like that person", False),
    ("you would not trust that person near your goods", False),
]


FOLLOW_UNSAFE = ("customer", "ask them how")   # angles that invite a source or new details - not on follow-ups


def pick_angle(types, is_speaker, about_other, rng=None, follow=False):
    rng = rng or RNG
    grim = any(t in GRIM for t in types)
    pool = ANGLES_ANY + (ANGLES_SPEAKER if is_speaker else []) + (ANGLES_OTHER if about_other else [])
    pool = [a for a, cheery in pool if not (grim and cheery)]
    if follow:
        pool = [a for a in pool if not any(x in a for x in FOLLOW_UNSAFE)]
    return rng.choice(pool)


# "what news": chance of two different people's stories, both told without names (Codex finding 1; the owner 2026-10-30
# ). OFF since the owner's dev test 2026-11-02: handed a murder and a pet story together, gemma4:12b blended them
# into one false tale ("a tamer would take a life so heartlessly") - one story per news reply (the owner).
PAIR_SHARE = 0.0
NEWS_ROWS = 1        # same reason: even one person's two deeds go one per reply on "what news" ("about" keeps MAX_ROWS)
OWN_BOOST = 2.0      # the listener's own story is twice as likely (the owner decision 1: NPCs recognise you)
# the owner, chat, 2026-10-31
# True: no name guard on turns with no gossip on offer, and a follow-up may name the story's person.
# False: the strict 5b/5c-1 rules (Codex findings 3 and 4), for rollback without a code change.
FREE_NAMES = True
# Greeting teasers (the owner, chat, 2026-11-02). {t} = the sentinel the shard fills with that player's current name.
TEASE_OTHER = ["Say, did you hear about {t}?", "Have you heard the talk about {t}?", "Folk have been talking about {t}, you know.",
               "Did you hear what {t} has been up to?", "Heard the latest about {t}?"]
TEASE_SELF = ["Folk have been talking about you, you know.", "Have you heard what they are saying about you?",
              "Did you know you are the talk of the town?"]
TEASE_VAGUE = ["Have you heard the latest talk around town?", "There is a tale going round, if you have an ear for it.",
               "Heard the gossip going round?"]
HEARD_DAMP = 0.25    # heard in the last 2 h from any NPC: a quarter as likely, not ruled out (no streaks, the owner 2026-10-30)


def _weighted(rows, rumours, rng, boost=None):
    """Weighted random order (each deed type's "weight" in rumours.json, default 1, times boost(row))."""
    deeds = (getattr(rumours, "cfg", {}) or {}).get("deeds", {})

    def key(r):
        w = float((deeds.get(r["type"]) or {}).get("weight", 1) or 1) * (boost(r) if boost else 1.0)
        w = max(w, 0.01)
        return -(rng.random() ** (1.0 / w))
    return sorted(rows, key=key)


def new_sentinel():
    return f"{OPEN}P-{secrets.token_hex(2)}{CLOSE}"


class Offer:
    """What one request was given: the block for the prompt, and what the shard needs to fill and record."""

    def __init__(self):
        self.block = ""
        self.sentinel = ""
        self.char_id = ""
        self.descriptor = ""
        self.ids = []
        self.is_speaker = False
        self.name = ""          # fallback mode only: the subject's CURRENT name (from the shard), never facts.who
        self.mode = ""
        self.angle = ""
        self.forbid = frozenset()   # follow-ups: town names and other deeds' words the reply may not add (finding 4)
        self.keys = {}          # rumour id -> word stems that show the reply told it (dev play test 2026-10-29)
        self.own = set()        # claim words of the offered deeds themselves (a sentinel sentence may use only these)
        self.tease = ""         # greet (2026-11-02): the one teaser line the code adds after the model's reply

    def wire(self):
        return {"sentinel": self.sentinel, "char_id": self.char_id, "descriptor": self.descriptor,
                "ids": list(self.ids), "mode": self.mode}


def _speaker_lines(epithets, speaker):
    titles = (epithets or {}).get(speaker) or []
    if not titles:
        return []
    return [f'Folk in these parts call the person you are talking to "{titles[0]}".']


def _tail(out, off, picked, rumours, pt, speaker, rng, told=True):
    """The shared closing lines: no gender, the angle, own words, an action, the listener's epithet."""
    out.append('Nobody here is known to be a man or a woman: never say "he", "she", "him", "her", "man", "woman", '
               '"fellow" or "lass".' + (' Call the person you are talking to "you".' if off.is_speaker
                                        else ' Call anyone in the gossip "they" or "that one".'))
    if told:
        types = [r["type"] for r in picked]
        off.angle = pick_angle(types, off.is_speaker, bool(off.char_id) and not off.is_speaker, rng)
        out.append(f"How you tell it this time: {off.angle}.")
        out.append("Say it in your own words, the way someone of your trade would - do not copy the wording of the "
                   "notes, and do not begin with " + ", ".join(STOCK_OPENINGS) + ". One or two short sentences.")
    act = smalltalk.action_line(rng)
    if act:
        out.append(act)
    out += _speaker_lines(rumours.epithets(pt) if hasattr(rumours, "epithets") else {}, speaker)
    off.block = "\n".join(out)
    return off


def select_about(rumours, rows, pt, about, speaker, roster=(), name_mode="sentinel", rng=None, ambiguous=False):
    """They named a real player. Offer only THAT person's named stories (vague ones cannot be pinned on anyone), still
    one person per reply with the sentinel; with none, an offer that tells her she has heard nothing of them."""
    rng = rng or RNG
    off = Offer()
    off.mode = MODE_ABOUT
    if ambiguous:
        # Codex 5c-1 code review finding 2: two people named, or a first name several share - never guess the person.
        out = ["They named more than one person, or a name more than one person here goes by: ask them which one they "
               "mean. Name nobody yourself."]
        return _tail(out, off, [], rumours, pt, speaker, rng, told=False)
    off.char_id = about or ""
    off.is_speaker = bool(about) and about == speaker
    mine = [r for r in rows or () if about and not r["vague"] and r["char"] == about]
    mine = _weighted(mine, rumours, rng)
    names = {str(p.get("id", "")): str(p.get("name", "")) for p in roster or () if p.get("id")}
    if off.is_speaker:
        token = "the person you are talking to"
    elif about and name_mode == "name" and names.get(about):
        off.name = names[about]
        token = off.name
    elif about:
        off.sentinel = new_sentinel()
        token = off.sentinel
    else:
        token = ""
    lines, picked = [], []
    for r in mine[:MAX_ROWS]:
        lines.append("- " + rumours.render_for(r, who=token))
        picked.append(r)
        off.ids.append(r["id"])
        off.keys[r["id"]] = _keys(r, rumours)
        off.own |= CLAIM_WORDS.get(r["type"], set())
        if sum(len(x) for x in lines) > MAX_CHARS:
            lines.pop()
            picked.pop()
            off.ids.pop()
            break
    if picked:
        off.descriptor = picked[0]["descriptor"]
    out = []
    if lines:
        out = ["Town gossip you have picked up about the person they asked after (hearsay - notes only, it may have "
               "grown in the telling):"] + lines
        if off.is_speaker:
            out.append("They asked about themselves - that gossip is about the person you are talking to: speak to them "
                       "as \"you\", never by a name.")
        elif off.sentinel:
            out.append(f"When you mention that person, write {token} exactly as shown. Never guess or add any other name.")
        out.append("They asked after that person, an adventurer of these lands: answer what they said, and pass on "
                   "this gossip briefly.")
    elif off.is_speaker:
        out.append("They asked whether you have heard anything of them: you have heard no gossip about them. Say so "
                   "briefly in your own way, and invent nothing.")
    elif token:
        out.append(f"They asked after {token}, an adventurer of these lands. You have heard no gossip at all about that "
                   f"person: say so briefly in your own way. If you name them, write {token} exactly. Never make anything "
                   f"up about them.")
    else:
        out.append("They asked after someone by name; you have heard nothing of that person: say so without repeating "
                   "the name.")
    return _tail(out, off, picked, rumours, pt, speaker, rng, told=bool(lines))


def select(rumours, pt, town, speaker, mode, follow="", exclude=(), roster=(), name_mode="sentinel", rng=None, about="",
           recent=(), ambiguous=False, teased=False):
    """Build the Offer for one reply, or None when there is nothing to say. `town` must already be the shard's
    region-based town (finding 4); an unknown town gives None."""
    if not town or mode not in (MODE_NEWS, MODE_GREET, MODE_FOLLOW, MODE_ABOUT):
        return None
    rows = rumours.speech_rows(pt, town)
    if mode == MODE_ABOUT:
        return select_about(rumours, rows, pt, about, speaker, roster, name_mode, rng, ambiguous=ambiguous)
    if not rows:
        return None
    rng = rng or RNG
    exclude = set(exclude or ())
    # One weighted draw instead of fixed "own story first" (play test 2026-10-30: it ran in streaks - all Legolas,
    # never Brynn). Own story boosted; recently heard damped.
    def boost(r):
        m = OWN_BOOST if (not r["vague"] and r["char"] == speaker) else 1.0
        return m * (HEARD_DAMP if r["id"] in exclude else 1.0)
    rows = _weighted(rows, rumours, rng, boost)
    if mode == MODE_NEWS and recent:
        # heard in the last few minutes (shard setting, default 10): never retold on "what news" (play test 5)
        recent = set(recent)
        rows = [r for r in rows if r["id"] not in recent]
    if mode == MODE_GREET:
        # Unprompted: never one this player heard in the last 2 h; about anyone (the owner, chat, 2026-10-29).
        rows = [r for r in rows if r["id"] not in exclude][:1]
    elif mode == MODE_FOLLOW:
        rows = [r for r in rows if r["id"] == follow][:1]
    if not rows:
        return None

    # Codex code review 2026-10-30, finding 1: a named person never shares a reply with someone else's deed - the model
    # could move the name onto it. A second story rides along only if it is the same person's, or (PAIR_SHARE of "what
    # news") when BOTH stories are told with no name at all (the owner, chat, 2026-10-30).
    off = Offer()
    off.mode = mode
    top = rows[0]
    if mode == MODE_FOLLOW:
        # finding 4 (strict): no name slot at all, only the listener's "you" for their own story. FREE_NAMES (the owner,
        # 2026-10-31): the story's own person may be named - "Who was it?" gets the answer; nothing else may be added.
        subject = top["char"] if FREE_NAMES and not top["vague"] else ""
        picked = [top]
        off.is_speaker = not top["vague"] and top["char"] == speaker
    else:
        second = next((r for r in rows[1:] if r["char"] != top["char"]), None)
        pair = (mode == MODE_NEWS and second is not None and speaker not in (top["char"], second["char"])
                and rng.random() < PAIR_SHARE)
        if pair:
            subject, picked = "", [top, second]
        else:
            subject = "" if top["vague"] else top["char"]
            picked = ([r for r in rows if not r["vague"] and r["char"] == subject] if subject else [top])[
                :NEWS_ROWS if mode == MODE_NEWS else MAX_ROWS]
        off.is_speaker = bool(subject) and subject == speaker
    off.char_id = subject
    names = {str(p.get("id", "")): str(p.get("name", "")) for p in roster or () if p.get("id")}
    if subject:
        off.descriptor = next(r["descriptor"] for r in picked if r["char"] == subject and not r["vague"])
    if off.is_speaker:
        # About the person speaking (the owner decision 1): no name and no sentinel at all - they are. Trial 2026-10-29:
        # with a sentinel here the model once wrote "a traveler named P-82bc" (caught, but a 5 s retry).
        token = "the person you are talking to"
    elif subject:
        if name_mode == "name" and names.get(subject):
            off.name = names[subject]
            token = off.name
        else:
            off.sentinel = new_sentinel()
            token = off.sentinel
    lines = []
    for r in picked:
        if not r["vague"] and r["char"] == (speaker if off.is_speaker else subject) and (subject or off.is_speaker):
            text = rumours.render_for(r, who=token)
        else:
            text = rumours.render_for(r, vague=True)   # anyone else: never a name (finding 1)
        lines.append("- " + text)
        off.ids.append(r["id"])
        off.keys[r["id"]] = _keys(r, rumours)
        off.own |= CLAIM_WORDS.get(r["type"], set())
        if sum(len(x) for x in lines) > MAX_CHARS:
            lines.pop()
            off.ids.pop()
            break
    if not lines:
        return None

    if mode == MODE_GREET:
        # the owner, chat, 2026-11-02: a greeting only TEASES the tale. The model is not given it (so it can neither tell nor
        # embroider it); the code adds one teaser line; the tale is told only if they ask (follow-up, teased=True).
        if off.is_speaker:
            off.tease = rng.choice(TEASE_SELF)
        elif subject:
            off.tease = rng.choice(TEASE_OTHER).format(t=token)
        else:
            off.tease = rng.choice(TEASE_VAGUE)
        off.ids = off.ids[:1]
        off.block = "Answer what they said in one or two short sentences. Mention no gossip or news of your own."
        return off

    out = ["Town gossip you have picked up (hearsay - notes only, it may have grown in the telling):"] + lines
    if off.is_speaker:
        out.append("That gossip is about the person you are talking to: speak to them as \"you\", never by a name.")
    elif subject and off.sentinel:
        out.append(f"When you mention that person, write {token} exactly as shown. Never guess or add any other name.")
    else:
        out.append("Nobody in this gossip is named: give no names at all.")
    if mode == MODE_NEWS:
        out.append("They asked for news: share one of these briefly.")
    elif mode == MODE_FOLLOW and teased:
        # 2026-11-02: they took the bait of a greeting tease - NOW the tale is told, from the note only.
        who = (f" Name them as {off.sentinel}." if off.sentinel and not off.is_speaker else "")
        out.append("They want to hear the tale you just hinted at. Tell it now, briefly, from the note above." + who +
                   " Add nothing that is not in the note: no other person, place, time, witness or reason.")
    elif mode == MODE_FOLLOW:
        # Codex code review 2026-10-30, finding 4: reaction only - nothing new about a real person can be said.
        who = (f" If they ask who it was, it was {off.sentinel}." if off.sentinel and not off.is_speaker else "")
        out.append("They want to hear more about the tale you passed on. You know nothing more than the note above. "
                   "React to it only - whether you believe it, what you make of it, how it touches your own trade or "
                   "your day." + who + " Add no new detail of any kind: no other person, place, time, witness, reason or "
                   "anything else that happened. Do not retell it.")
    elif off.is_speaker:
        out.append("Answer what they said first, then add one short remark about this gossip - it is about them.")
    else:
        out.append("Answer what they said first, then make small talk: pass on this piece of gossip in one short remark. Your reply must include the gossip.")
    # the owner, chat, 2026-10-29: - nobody is told a character's sex.
    out.append('Nobody here is known to be a man or a woman: never say "he", "she", "him", "her", "man", "woman", '
               '"fellow" or "lass".' + (' Call the person you are talking to "you".' if off.is_speaker
                                        else ' Call anyone in the gossip "they" or "that one".'))
    types = [r["type"] for r in picked]
    off.angle = pick_angle(types, off.is_speaker, bool(subject) and not off.is_speaker, rng,
                           follow=mode == MODE_FOLLOW and not teased)
    if mode == MODE_FOLLOW:
        off.forbid = _follow_forbid(picked[0], rumours)
        if teased:   # 2026-11-02 trial: a reveal retells the note, so the note's own places and words are not new facts
            note = " ".join(lines).lower()
            off.forbid = frozenset(w for w in off.forbid if w not in note)
    out.append(f"How you tell it this time: {off.angle}.")
    out.append("Say it in your own words, the way someone of your trade would - do not copy the wording of the notes, "
               "and do not begin with " + ", ".join(STOCK_OPENINGS) + ". One or two short sentences.")
    act = smalltalk.action_line(rng)
    if act:
        out.append(act)
    out += _speaker_lines(rumours.epithets(pt) if hasattr(rumours, "epithets") else {}, speaker)
    off.block = "\n".join(out)
    return off


KEY_STOP = frozenset({"near", "with", "from", "sitting", "pulled", "into", "their", "them", "while", "after", "once"})


def _keys(row, rumours):
    """Word stems that show a reply told this rumour: the deed subject plus the deed text minus its placeholders."""
    f = row["_g"].get("facts") or {}
    tmpl = ((getattr(rumours, "cfg", {}) or {}).get("deeds", {}).get(row["type"], {}) or {}).get("text", "")
    words = re.findall(r"[a-z]+", str(f.get("subject") or "").lower())
    words += re.findall(r"[a-z]+", re.sub(r"\{[^}]*\}", " ", tmpl).lower())
    words += [str(w).lower() for w in ((getattr(rumours, "cfg", {}) or {}).get("deeds", {}).get(row["type"], {}) or {}).get("told_words", [])]
    town = set(re.findall(r"[a-z]+", str(row.get("town") or "").lower()))   # "Britain" alone is everyday talk there
    words += [w for w in re.findall(r"[a-z]+", str(f.get("place") or "").lower()) if w not in town]
    return sorted({w[:4] for w in words if len(w) >= 4 and w not in KEY_STOP})


MAX_FOLLOW = 4

# Words that state a deed (Codex finding 4: a follow-up may not add one). The row's own type's words are allowed.
CLAIM_WORDS = {
    "kill_notable": {"slew", "slain", "slay", "slaying", "killed", "felled"},
    "tame_notable": {"tamed", "taming"},
    "died_notable": {"died", "perished", "killed"},
    "died_pathetic": {"died", "perished", "killed"},
    "murder": {"murdered", "murderer", "murder", "killed"},
    "drunk": {"drunk", "drunken", "intoxicated", "sozzled", "tipsy"},
    "fished_boots": {"boots"},
    "fished_bottle": {"bottle"},
    "provoked": {"provoked"},
    "pacified": {"pacified", "calmed"},
}


def _follow_forbid(row, rumours):
    """Town names other than the rumour's own, and other deeds' claim words: a follow-up that says one adds a fact."""
    cfg = getattr(rumours, "cfg", {}) or {}
    f = row["_g"].get("facts") or {}
    own_places = {w for w in re.findall(r"[a-z']+", (str(row.get("town") or "") + " " + str(f.get("place") or "")).lower())}
    towns = set()
    for t, d in (cfg.get("towns") or {}).items():
        for name in (t, str((d or {}).get("felucca_name") or "")):
            name = name.lower().strip()
            if name and not (set(re.findall(r"[a-z']+", name)) & own_places):
                towns.add(name)
    own = CLAIM_WORDS.get(row["type"], set())
    claims = {w for k, ws in CLAIM_WORDS.items() if k != row["type"] for w in ws} - own
    return frozenset(towns | claims | ((INVENT_WORDS | TIME_WORDS) - own))


# Words that state a deed or a misdeed (Codex 2a finding 2). Beside a real name in ordinary talk, or in a sentence that
# carries a story's person slot, only the offered deed's own words may appear.
# Everyday verbs that also mean a deed ("caught a whisper", "lost count", "broke my back") are left out on purpose - a
# false refusal of ordinary talk costs a retry (trial 2026-10-31 19:56 UTC: "caught a whisper of that name").
INVENT_WORDS = frozenset("""stole steal stolen stealing thief theft robbed rob robbery cheated cheat cheating lied liar
owes owed paid bought sold attacked fought arrested jailed burned kissed married bragging boasting slew slain slay killed
murdered murder died dead drunk drunken tamed fished stabbed poisoned hanged abducted kidnapped kidnap assaulted raped
beaten betrayed torched smuggled poached deserted beheaded strangled drowned bewitched swindled bribed blackmailed sacked
looted ransacked""".split())
CLAUSE_START = frozenset("who whom whose which that and because after while when since then".split())
TIME_WORDS = frozenset(("yesterday", "tonight", "last night", "this morning", "tomorrow", "last week", "this week"))
_VOC_BEFORE = re.compile(r"(?:,|\b(?:hello|hail|welcome|greetings|evening|morning|afternoon|met|again|back|ho|dear|friend|"
                         r"you are|you're|are you|thanks|thank you|aye|yes|well|farewell|goodbye|bye|night|day))\s*$",
                         re.IGNORECASE)


def listener_name(roster, listener):
    key = _idkey(listener)
    return next((str(p.get("name", "")).strip() for p in roster or () if key and _idkey(p.get("id")) == key), "")


def _vocative_spans(text, name):
    """Where `name` is used to ADDRESS someone ("Good to see you, Gabby." / "Gabby, a word."), not said about them."""
    if not name:
        return []
    forms = [name] + ([name.split()[0]] if len(name.split()) > 1 and len(name.split()[0]) >= 3 else [])
    spans = []
    for form in forms:
        for m in re.finditer(r"(?<![\w])" + re.escape(form) + r"(?![\w])", text, re.IGNORECASE):
            if any(a <= m.start() < b for a, b in spans):
                continue
            before, after = text[:m.start()], text[m.end():]
            starts = re.search(r"(?:^|[.!?])\s*(?:\[[^\]]*\]\s*)?$", before) is not None
            ends = re.match(r"\s*(?:[,.!?;]|$)", after) is not None
            # Codex 2a round 2 finding 2: real address syntax only - "..., Gabby." / "Hello Gabby," / "Gabby, a word" -
            # never "At dawn, Gabby killed a guard."
            if (_VOC_BEFORE.search(before) and ends) or (starts and re.match(r"\s*,", after)):
                spans.append((m.start(), m.end()))
    return sorted(spans)


def mask_listener(text, name, repl="you"):
    """Codex 2a finding 2: the listener's name is fine as an address; said ABOUT them it is checked like anyone's."""
    out = text
    for a, b in reversed(_vocative_spans(text, name)):
        out = out[:a] + repl + out[b:]
    return out


def _sentences(text):
    return [s for s in re.split(r"(?<=[.!?])\s+|\n+", text) if s.strip()]


def _free_check(say, roster):
    """Ordinary talk (no story on offer): any name may be said, but not beside a deed word - there is no record to say
    it is true (the owner, chat, 2026-10-31)."""
    # Codex 2a round 2 finding 2: the whole reply, not one sentence - "Aldren was here. They stole a purse."
    low = _norm(say)
    words = set(w.strip("'-") for w in WORD_RE.findall(low))
    if not (words & INVENT_WORDS):
        return None
    for p in roster or ():
        name = _norm(str(p.get("name", ""))).strip()
        if not name:
            continue
        first = name.split()[0]
        if re.search(r"(?<![\w])" + re.escape(name) + r"(?![\w])", low) or (
                len(name.split()) > 1 and len(first) >= 4 and first in words):
            return "player name (claim)"
    return None


def _sentinel_sentences(say, offer):
    """Codex 2a finding 2: a sentence carrying the story's person slot may use only the story's own deed words; on a
    follow-up it must also either answer "who" or be about the story itself (its key words)."""
    if not offer.sentinel or offer.is_speaker:
        return None
    keys = {k for ks in offer.keys.values() for k in ks}
    who = re.compile(r"^\W*(?:\[[^\]]*\]\s*)?(?:it|that|twas)\s*(?:was|were|'s|’s)?\s*" + re.escape(offer.sentinel) +
                     r"(?P<rest>.*)$", re.IGNORECASE)
    if offer.sentinel in say:
        # Codex 2a round 2 finding 2: once the slot is in the reply, no sentence may add a misdeed - "<slot> fished up a
        # boot. They murdered a guard." (the story's own words are fine)
        allw = [w.strip("'-") for w in WORD_RE.findall(_norm(say.replace(offer.sentinel, " ")))]
        if any(w in INVENT_WORDS and w not in offer.own and w[:4] not in keys for w in allw):
            return "claim not in the story"
    for sent in _sentences(say):
        if offer.sentinel not in sent:
            continue
        low = _norm(sent.replace(offer.sentinel, " "))
        words = [w.strip("'-") for w in WORD_RE.findall(low)]
        if offer.mode == MODE_FOLLOW:
            m = who.match(sent)
            tail = re.sub(r"^\W*who\s+(?:did|done)\s+(?:it|that|this)\b", " ", _norm(m.group("rest"))) if m is not None else ""
            rest = WORD_RE.findall(tail)   # "It was <slot> who did that." (play test 2026-10-31) is a who-answer
            short_who = (m is not None and len(rest) <= 6 and not (set(rest) & CLAUSE_START)) or bool(re.match(
                r"^\W*(?:\[[^\]]*\]\s*)?" + re.escape(offer.sentinel) + r"\s+(?:was|were|is)\s+the\s+one\b", sent,
                re.IGNORECASE))   # "<slot> was the one." (trial 2026-10-31 19:31 UTC)
            if not short_who and not ({w[:4] for w in words if len(w) >= 4} & keys):
                return "claim not in the story"
    return None


def follow_fallback(offer, utterance):
    """Two refusals on a follow-up: a bounded answer instead of silence (Codex 2a finding 2)."""
    if offer is not None and offer.sentinel and not offer.is_speaker and re.search(r"\b(who|whom|whose|name)\b",
                                                                                  str(utterance or ""), re.IGNORECASE):
        return f"It was {offer.sentinel}, so I heard. That is all I know of it."
    return "That is all I know of it."


def _idkey(v):
    return str(v or "").replace("-", "").strip().lower()


def scrub(say, roster=(), keep=""):
    """No live offer (Codex finding 3): a real player's full name becomes "that one", so no real name is spoken and no
    invented deed can attach to one. Longest names first; "a"/"the" before the name goes with it. `keep`: the
    listener's name where it only addresses them ("Good evening, Gabby") stays."""
    held = []
    for a, b in reversed(_vocative_spans(say, keep)):
        held.insert(0, say[a:b])
        say = say[:a] + "\x00" + say[b:]
    out = say
    names = sorted({str(p.get("name", "")).strip() for p in roster or ()}, key=len, reverse=True)
    for name in names:
        if len(name) >= 3:
            out = re.sub(r"(?<![\w])(?:(?:a|an|the|one)\s+(?:person\s+)?(?:called|named)?\s*)?" + re.escape(name)
                         + r"(?![\w])", "that one", out, flags=re.IGNORECASE)
    for h in held:
        out = out.replace("\x00", h, 1)
    return out


def bind_asked(say, offer, roster=()):
    """An "about" reply that writes the asked-after person's real name (full, or the first name of a two-part name) gets
    the sentinel in its place - it is exactly that person, and the shard fills in the CURRENT name. Anyone else's name is
    untouched, so the check still refuses it (trial 2026-10-30: 9/60 silences on "Do you know Wren Ashby?")."""
    if offer is None or offer.mode != MODE_ABOUT or not offer.sentinel or not offer.char_id:
        return say
    name = next((str(p.get("name", "")).strip() for p in roster or () if str(p.get("id", "")) == offer.char_id), "")
    if len(name) < 3:
        return say
    forms = [name] + ([name.split()[0]] if len(name.split()) > 1 and len(name.split()[0]) >= 4 else [])
    out = say
    for f in forms:
        out = re.sub(r"(?<![\w])" + re.escape(f) + r"(?![\w])", offer.sentinel, out, flags=re.IGNORECASE)
    return out


def wire_reply(offer, told):
    """What goes back to the shard whenever live gossip was in the prompt (Codex finding 2): always the context flag
    (the shard then stores only the history marker), with `ids` = only the rumours really told (heard timer, caps)."""
    if offer is None:
        return None
    return {**offer.wire(), "ids": list(told)}


def guard(offer, rumour_context, mode=""):
    """The Offer to check a reply against. With no live offer but a news/greet/follow/small-talk context, an empty one:
    no real player name is allowed at all (Codex finding 3)."""
    if offer is not None:
        return offer
    if FREE_NAMES and mode != MODE_NEWS:
        off = Offer()   # the owner, 2026-10-31: names are free in ordinary talk - but never beside a deed (Codex 2a finding 2)
        off.mode = MODE_FREE
        return off
    # "What news?" with no live story is answered from made-up chatter or the hand-written rumour pages: a real name there
    # would be an invented deed (Codex 5b finding 3 - "not make stuff up" still holds), so it stays strict.
    return Offer() if rumour_context else None
MARKER = "[shared a piece of town gossip]"   # the shard's history marker (RumourSpeech.Marker)


def follow_depth(history):
    """How many gossip replies in a row the NPC has just given (the shard keeps only the marker in history)."""
    n = 0
    for line in reversed(list(history or [])):
        if not isinstance(line, dict) or line.get("speaker") == "player":
            continue
        if line.get("text") == MARKER:
            n += 1
        else:
            break
    return n


def used(say, offer):
    """The rumour ids this reply actually told. Offered is not told: only these count as heard (dev play test
    2026-10-29: the model passed over the deed 3 times out of 3 and the shard still marked it heard)."""
    if offer is None:
        return []
    stems = {w[:4] for w in re.findall(r"[a-z]+", _norm(say)) if len(w) >= 4}
    hit = [i for i in offer.ids if set(offer.keys.get(i) or ()) & stems]
    if not hit and offer.sentinel and offer.sentinel in say:
        hit = offer.ids[:1]
    return hit


def _norm(s):
    return unicodedata.normalize("NFKC", s).casefold()


def _spoofy(text):
    for ch in text:
        if ord(ch) < 128:
            continue
        name = unicodedata.name(ch, "")
        if any(name.startswith(s) for s in SPOOF_SCRIPTS):
            return True
    return False


def _edit1(a, b):
    if a == b:
        return True
    if abs(len(a) - len(b)) > 1:
        return False
    if len(a) > len(b):
        a, b = b, a
    i = j = diff = 0
    while i < len(a) and j < len(b):
        if a[i] != b[j]:
            diff += 1
            if diff > 1:
                return False
            if len(a) == len(b):
                i += 1
            j += 1
        else:
            i += 1
            j += 1
    return diff + (len(b) - j) <= 1


WORD_RE = re.compile(r"[\w'\-]+")


def check(say, offer, roster=(), common_words=frozenset()):
    """None if the reply may be spoken, else the reason it is refused. Only applies when an Offer was made."""
    if offer is None:
        return None
    if offer.mode == MODE_FREE:
        if SENTINEL_RE.search(say) or LOOSE_RE.search(say):
            return "foreign sentinel"
        return _free_check(say, roster)
    found = SENTINEL_RE.findall(say)
    if any(s != offer.sentinel for s in found):
        return "foreign sentinel"
    rest = say.replace(offer.sentinel, " ") if offer.sentinel else say
    if LOOSE_RE.search(rest):
        return "mangled sentinel"
    if _spoofy(rest):   # after the sentinel is removed: its own brackets are "mathematical" characters
        return "look-alike letters"
    allowed = _norm(offer.name) if offer.name else None
    text = _norm(rest)
    words = [w.strip("'-") for w in WORD_RE.findall(text)]
    for p in roster or ():
        name = _norm(str(p.get("name", ""))).strip()
        if not name or name == allowed:
            continue
        if re.search(r"(?<![\w])" + re.escape(name) + r"(?![\w])", text):
            return "player name"
        parts = name.split()
        if len(parts) > 1:   # "Brynn" alone, or "Brynn Handy" spelled one letter off
            first = parts[0]
            if len(first) >= 4 and first not in common_words and first in words:
                return "player name"
        flat = name.replace(" ", "")
        if len(flat) >= 5:
            joined = [w for w in words if len(w) >= 4]
            pairs = joined + [a + b for a, b in zip(joined, joined[1:])]
            for w in pairs:
                if w not in common_words and w != flat and _edit1(w, flat):
                    return "player name (near spelling)"
    if offer.forbid:
        low = " " + " ".join(words) + " "
        for w in offer.forbid:
            if f" {w} " in low:
                return "follow-up added a fact"
    return _sentinel_sentences(say, offer)


def fill(say, offer):
    """Brain-side helper for tests and the trial only; the shard does the real fill with the current name."""
    return say.replace(offer.sentinel, offer.name or "NAME") if offer and offer.sentinel else say
