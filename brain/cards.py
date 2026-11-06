"""Phase 5c-2 step 2a: a named NPC's structured memory of one character (design v1.2, Projects/Avallone-Memory-
Affinity-Design.md §V1.2, §V1.11, §V1.12).

Everything here is WORKED OUT FROM THE LEDGER ON EVERY READ - there is no cache to go stale (design §V1.0; 2a builds no
cache at all, so the cache-invalidation rules of §V1.11 #1 / §V1.12 #1 have nothing to act on yet). Inputs: the live
ledger rows for exactly this NPC + character, the shard's two clocks sent with the request (memory clock `mpt`, played
clock `pt`), and two hand-edited files: concepts.json (finding words) and memory_rules.json (fade times, limits).

Four kinds of card:
- fact     (lasting; never fades): a pet's name and kind, their trade, their home - made only by the code patterns in
           named.typed_items(), always shown as THEIR word;
- request  (fades like a heavy event): something they ASKED of her - never an agreed obligation (Codex round 1 F2);
- episode  (fades on memory hours, light/mid/heavy): an extractive claim, or what they talked about;
- habit    (made by code): one concept on >= habit_days distinct visit days.
Old 5c-1 notes (no `mpt`) become `legacy` episodes aged from the one migration marker; nothing is inferred from them.

Recall is chosen by code (Gemma only speaks): best match for the line, then - only when the turn is about the person -
one fact or request, one recent episode, maybe one habit. Cards offered in a delivered reply cool down for a few played
hours unless the player asks about them directly. Offering a card never makes it last longer.
"""
import hashlib
import json
import os
import re
from datetime import datetime, timezone
from zoneinfo import ZoneInfo

HERE = os.path.dirname(os.path.abspath(__file__))
CENTRAL = ZoneInfo("America/Chicago")
CLASSES = ("light", "mid", "heavy")
STOP = frozenset("""the and but for with from that this these those have has had was were are is be been being you your
yours they them their theirs what when where which while who whom will would could should shall about into onto than
then there here just also only very much many more most some such other another every each said says told tell does
doing done like want need please thank thanks hello greetings well good fine know think remember recall again before
earlier last time today yesterday tomorrow""".split())


class Concepts:
    def __init__(self, path):
        with open(path, encoding="utf-8") as f:
            raw = json.load(f)
        self.version = str(raw.get("version", "?"))
        self.parents, self.labels, self.word = {}, {}, {}
        for cid, c in raw["concepts"].items():
            self.parents[cid] = list(c.get("parents") or [])
            self.labels[cid] = str(c.get("label") or cid.lower())
            for w in c.get("words") or []:
                self.word[w.lower()] = cid
        self.phrases = sorted((w for w in self.word if " " in w), key=len, reverse=True)
        self._anc = {}

    def leaf(self, word):
        return self.word.get(str(word).lower())

    def ancestors(self, cid):
        """{ancestor: depth} (1 = parent), nearest depth kept."""
        if cid not in self._anc:
            out, frontier, d = {}, list(self.parents.get(cid, [])), 1
            while frontier:
                nxt = []
                for p in frontier:
                    if p not in out:
                        out[p] = d
                        nxt += self.parents.get(p, [])
                frontier, d = nxt, d + 1
            self._anc[cid] = out
        return self._anc[cid]

    def leaves_under(self, root):
        """Words whose concept is a STRICT descendant of `root` (a root's own words, e.g. "town", never count)."""
        return {w for w, c in self.word.items() if root in self.ancestors(c)}

    def label(self, word):
        c = self.leaf(word)
        return self.labels.get(c, word) if c else word

    def found(self, text):
        """Concept ids in `text` (phrases first, then single words)."""
        t = " " + " ".join(re.findall(r"[a-z']+", str(text).lower())) + " "
        out = set()
        for p in self.phrases:
            if f" {p} " in t:
                out.add(self.word[p])
                t = t.replace(f" {p} ", " ")
        for w in t.split():
            c = self.word.get(w) or self.word.get(w.rstrip("s")) if len(w) > 3 else self.word.get(w)
            if c:
                out.add(c)
        return out


_CON, _RULES = None, None


def concepts():
    global _CON
    if _CON is None:
        _CON = Concepts(os.environ.get("AVALLONE_CONCEPTS") or os.path.join(HERE, "concepts.json"))
    return _CON


def rules():
    global _RULES
    if _RULES is None:
        with open(os.environ.get("AVALLONE_MEMORY_RULES") or os.path.join(HERE, "memory_rules.json"),
                  encoding="utf-8") as f:
            _RULES = json.load(f)
    return _RULES


def reload():
    global _CON, _RULES
    _CON, _RULES = None, None


def digest():
    """Which rules produced a recall (logged with it, so `why did she say that` can be traced to a file version)."""
    h = hashlib.sha256()
    for name in ("concepts.json", "memory_rules.json"):
        try:
            with open(os.path.join(HERE, name), "rb") as f:
                h.update(f.read())
        except OSError:
            h.update(b"-")
    return h.hexdigest()[:12]


# Generic words a model uses to label a topic ("asking about someone else", "seeking news"). Every OTHER content word of
# a topic must be in the player's own lines (Codex 2a finding 3), and habits never count these words.
TOPIC_FREE = frozenset("""asking asked about someone something somebody else seeking wanting wants buying bought
selling sold talk talking talked chat chatting general small local their them they news plans plan things thing stuff
greeting greetings hello questions question ordering order getting looking passing visit visiting""".split())


def topic_words(text):
    return " ".join(w for w in re.findall(r"[A-Za-z']+", str(text)) if w.lower() not in TOPIC_FREE)


def _words(text):
    return {w for w in re.findall(r"[a-z']+", str(text).lower()) if len(w) >= 4 and w not in STOP}


def _day(at_utc):
    try:
        return datetime.fromisoformat(str(at_utc).replace("Z", "+00:00")).astimezone(CENTRAL).date().isoformat()
    except ValueError:
        return ""


def _a(word):
    return "an" if word[:1].lower() in "aeiou" else "a"


class Card:
    __slots__ = ("id", "lane", "text", "words", "concepts", "entity", "cls", "order", "ev_mpt", "life_h", "age_h",
                 "rank")

    def __init__(self, cid, lane, text, words=(), cons=(), entity="", cls="mid", order=(0, 0), ev_mpt=0.0):
        self.id, self.lane, self.text = cid, lane, text
        self.words, self.concepts, self.entity = set(words), set(cons), entity
        self.cls, self.order, self.ev_mpt = cls, order, ev_mpt
        self.life_h, self.age_h, self.rank = None, 0.0, 0.0


def derive(rows, now_mpt, marker_mpt, persona_key="", when=None, render_legacy=None):
    """Every card still remembered right now. `rows`: live visit_summary rows of exactly this pair, each a dict with
    event_id, epoch, seq, facts (dict), at_utc. `now_mpt` in memory-seconds; `marker_mpt` = the legacy migration marker
    (None = legacy notes are left out). A row from the clock's future (age < 0: a restore mismatch) is left out."""
    con, r = concepts(), rules()
    fade = r["fade_memory_hours"]
    heavy = set(r.get("heavy_concepts") or [])
    interest = set((r.get("interests") or {}).get(persona_key or "", []))
    when = when or (lambda at: "")
    rows = sorted(rows, key=lambda x: (x["epoch"], x["seq"]))

    def widen(cs):
        out = set(cs)
        for c in cs:
            out |= set(con.ancestors(c))
        return out

    def cls_for(base, cs):
        wide = widen(cs)
        i = CLASSES.index(base)
        if wide & heavy:
            i = 2
        if wide & interest:
            i = min(2, i + 1)
        return CLASSES[i]

    pets, selfs, requests, episodes = {}, {}, [], []
    concept_days = {}     # concept -> {day: newest ev_mpt}
    for row in rows:
        f, eid, order = row["facts"], row["event_id"], (row["epoch"], row["seq"])
        legacy = not isinstance(f.get("mpt"), (int, float))
        ev_mpt = marker_mpt if legacy else float(f["mpt"])
        if ev_mpt is None:
            continue
        day = _day(row.get("at_utc"))
        if legacy:
            text = f"{when(row.get('at_utc'))} (an older note): {render_legacy(f) if render_legacy else ''}".strip()
            cs = con.found(" ".join((f.get("claims") or []) + (f.get("topics") or [])))
            episodes.append(Card(f"l:{eid}", "legacy", text, _words(text), cs, cls="mid", order=order, ev_mpt=ev_mpt))
            continue
        for i, item in enumerate(f.get("items") or []):
            parts = str(item).split("|")
            if parts[0] == "pet" and len(parts) == 3:
                animal, name = parts[1], parts[2]
                key = name.lower()
                pets[key] = Card(f"f:pet:{key}", "fact",
                                 f"They told you (their word): their {animal} is called {name}.",
                                 {key, animal}, {c for c in (con.leaf(animal),) if c}, entity=key, order=order,
                                 ev_mpt=ev_mpt)
            elif parts[0] == "petfix" and len(parts) == 3:
                # Codex 2a finding 1: a kind correction only for a pet this visitor already named as theirs
                old = pets.get(parts[2].lower())
                if old is not None:
                    animal, name, key = parts[1], parts[2], parts[2].lower()
                    pets[key] = Card(f"f:pet:{key}", "fact",
                                     f"They told you (their word): their {animal} is called {name}.",
                                     {key, animal}, {c for c in (con.leaf(animal),) if c}, entity=key, order=order,
                                     ev_mpt=ev_mpt)
            elif parts[0] in ("trade", "home") and len(parts) == 2:
                v = parts[1]
                text = (f"They told you (their word): they are {' and '.join(f'{_a(x)} {x}' for x in v.split(' and '))}."
                        if parts[0] == "trade" else
                        f"They told you (their word): they live in {v}.")
                selfs[parts[0]] = Card(f"f:{parts[0]}", "fact", text, _words(v), con.found(v), order=order,
                                       ev_mpt=ev_mpt)
            elif parts[0] == "request" and len(parts) == 2:
                cs = con.found(parts[1])
                requests.append(Card(f"r:{eid}:{i}", "request",
                                     f"They asked you (only asked - nothing was agreed): to {parts[1]}.",
                                     _words(parts[1]), cs, cls="heavy", order=order, ev_mpt=ev_mpt))
        for i, c in enumerate(f.get("claims") or []):
            cs = con.found(c)
            episodes.append(Card(f"e:{eid}:{i}", "episode",
                                 f'{when(row.get("at_utc"))}: They told you: "{c}" (their word, not a known fact).'.strip(),
                                 _words(c), cs, cls=cls_for("mid", cs), order=order, ev_mpt=ev_mpt))
        topics = [t for t in (f.get("topics") or []) if t]
        if topics:
            cs = con.found(" ".join(topics))
            episodes.append(Card(f"t:{eid}", "episode",
                                 f'{when(row.get("at_utc"))}: You talked about: {", ".join(topics)}.'.strip(),
                                 _words(" ".join(topics)), cs, cls=cls_for("light", cs), order=order, ev_mpt=ev_mpt))
        if day:
            # habits from the player's own words only: claims are their spans; topics minus the generic label words
            for c in con.found(" ".join((f.get("claims") or []) + [topic_words(t) for t in topics])):
                d = concept_days.setdefault(c, {})
                d[day] = max(d.get(day, ev_mpt), ev_mpt)

    out = []

    def keep(card, life_h):
        card.age_h = (now_mpt - card.ev_mpt) / 3600.0
        card.life_h = life_h
        if card.age_h < 0:
            return   # evidence from the clock's future: a restore mismatch - leave it out (Codex round 2 F2)
        if life_h is None or card.age_h <= life_h:
            out.append(card)

    for card in list(pets.values()) + list(selfs.values()):
        keep(card, None)
    for card in requests:
        keep(card, float(r["request_fade_memory_hours"]))
    step, cap = float(r["reinforce_step"]), float(r["reinforce_cap"])
    for card in episodes:
        day = None
        later = 0
        if card.lane == "episode":
            # told again on another, later day -> lasts longer (diminishing); talk by the NPC never counts
            seen_days = set()
            for c in card.concepts:
                for d, m in concept_days.get(c, {}).items():
                    if m > card.ev_mpt:
                        seen_days.add(d)
            later = len(seen_days)
        keep(card, float(fade[card.cls]) * min(cap, 1.0 + step * later))
    habit_fade = float(r["habit_fade_memory_hours"])
    for c, days in concept_days.items():
        fresh = [m for m in days.values() if (now_mpt - m) / 3600.0 <= habit_fade and m <= now_mpt]
        if len(fresh) >= int(r["habit_days"]):
            label = con.labels.get(c, c.lower())
            card = Card(f"h:{c}", "habit", f"You have noticed: they often talk about {label}.", _words(label), {c},
                        cls="mid", ev_mpt=max(fresh))
            card.order = (len(fresh), 0)
            keep(card, habit_fade)

    hot = r["hot"]
    lanes = {}
    for card in out:
        lanes.setdefault("episode" if card.lane == "legacy" else card.lane, []).append(card)
    for lane, cs in lanes.items():
        if lane == "episode":
            cs.sort(key=lambda k: (CLASSES.index(k.cls), k.order), reverse=True)
        else:
            cs.sort(key=lambda k: k.order, reverse=True)
        for n, card in enumerate(cs):
            card.rank = 1.0 if n < int(hot.get(lane, 4)) else 0.0   # 1 = hot, 0 = cold (still searchable)
    return out


def _score(card, utt_words, utt_concepts, con):
    best = 0
    if card.entity and card.entity in utt_words:
        best = 100
    if card.words & utt_words:
        best = max(best, 60)
    for uc in utt_concepts:
        for cc in card.concepts:
            if uc == cc:
                best = max(best, 50)
                continue
            ca, ua = con.ancestors(cc), con.ancestors(uc)
            if ca.get(uc) == 1:
                best = max(best, 40)     # "steed" finds a horse
            elif ua.get(cc) == 1:
                best = max(best, 35)
            elif {p for p, d in ca.items() if d == 1 and con.parents.get(p)} & \
                    {p for p, d in ua.items() if d == 1 and con.parents.get(p)}:
                best = max(best, 30)     # siblings under a narrow parent: "steed" and a mule meet at MOUNT
            elif set(ca) & set(ua):
                best = max(best, 10)     # only a broad root in common: counts only if it picks out one card
    return best


ASK_TRADE = re.compile(r"\bfor a living\b|\bwhat (?:do |did )?i do\b|\bmy line of work\b")


def select(cards, utterance, first_line=False, cooled=frozenset(), transaction=False):
    """The cards to show, as [(card_id, line)], within max_cards and budget_chars (memory_rules.json)."""
    con, r = concepts(), rules()
    low = " ".join(re.findall(r"[a-z']+", str(utterance).lower()))
    utt_words, utt_concepts = _words(low) | set(low.split()), con.found(low)
    if ASK_TRADE.search(low):   # live play 2026-11-01: "What do I do for a living?" found nothing
        utt_concepts = set(utt_concepts) | {"TRADE"}
    scored = [(c, _score(c, utt_words, utt_concepts, con)) for c in cards]
    broad = [c for c, s in scored if s == 10]
    matched = sorted([(c, s) for c, s in scored if s >= 30 or (s == 10 and len(broad) == 1)],
                     key=lambda x: (x[1], x[0].rank, x[0].order), reverse=True)
    cue = any(f" {q} " in f" {low} " for q in r.get("memory_cues") or [])
    # A purchase is never a personal turn, even when a word in it matches (trial 2026-10-31: "buy some ale" matched her
    # drink habit and pulled the mule in). The matched card itself may still be shown.
    personal = not transaction and (bool(matched) or cue or first_line)
    chosen = []

    def usable(card, direct=False):
        return card not in chosen and (direct or card.id not in cooled)

    for c, s in matched:
        if usable(c, direct=s >= 50):
            chosen.append(c)
            break
    if personal:
        for lane_pick in (("fact", "request"), ("episode", "legacy"), ("habit",)):
            if lane_pick == ("habit",) and not (cue or first_line):
                continue
            if any(c.lane in lane_pick for c in chosen):
                continue   # the matched card already fills this slot (trial: "steed" found Tonk, then added the dog)
            pool = [c for c in cards if c.lane in lane_pick and c.rank > 0 and usable(c)]
            if lane_pick[0] == "fact":
                pool.sort(key=lambda k: (k.lane == "fact", k.id.startswith("f:pet"), k.order), reverse=True)
            else:
                pool.sort(key=lambda k: (CLASSES.index(k.cls), k.order), reverse=True)
            if pool:
                chosen.append(pool[0])
    out, used = [], 0
    for c in chosen[:int(r["max_cards"])]:
        if used + len(c.text) > int(r["budget_chars"]):
            continue
        used += len(c.text)
        out.append((c.id, c.text))
    return out
