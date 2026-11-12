"""Rumour step C2 (2026-11-11): made-up town gossip as part of the normal rumour flow.

the owner, chat, 2026-11-11

Before this step a made-up line (smalltalk.json) was a nameless fallback: drawn at random per reply, only when the town
had no real rumour to tell, never tracked. Here a made-up story is a rumour in its own right:

- IDENTITY. Every pool line has a content key (8 hex of a hash of its text). A story's id is
  "mu-<content key>-<town code>-<birth window>" (<= 40 characters, fits the GM commands); a one-off line's id is
  "mo-<content key>". Source type "made_up". Two pool lines with the same key are refused at load, never merged.
- A ROLLING SET PER TOWN, from the played clock: every `window_h` played hours each town gives birth to one story that
  lives `life_min_h`..`life_max_h` played hours, so about six are live at a time and they turn over one by one. Which
  line, and how long, come from a fixed hash of (town, window): nothing random, the same on every call.
- PINNED. The first time a story is worked out it is written to `madeup_stories` with its text, birth and expiry, and
  read from there afterwards. So editing or deleting a pool line never changes what a live id means - the story simply
  runs out (Codex design R2). A world restore rewinds the played clock; the same pinned stories then play again.
- THEY TRAVEL like any rumour (Rumours._spread: same graph, same +-25 % jitter per story) and never change in the telling.
- LIMITS: a town holds at most `town_cap` of them counting arrivals, freshest first - cut before the draw.
- VARIETY: each NPC has heard only `npc_share` of the town's set, fixed per NPC (a hash of NPC and story).
- A made-up story has no player in it: no name, no nickname, no correction, no grouping, no player statistics.

This module only works stories out; rumour_speech.select() makes the one weighted draw, and the shard's existing
"heard" bookkeeping (by id) does the rest. Nothing here calls the model."""
import hashlib
import json
import os
import re
import threading

import smalltalk

SOURCE = "made_up"
T_STORY, T_ONE = "made_up", "one_off"
MU_RE = re.compile(r"^mu-([0-9a-f]{8})-[0-9a-f]{4}-[0-9a-z]{1,10}$")
# A one-off INSTANCE: content key - six hex of the rendered text - the played-time window it was first told in. The bare
# form "mo-<key>" names no instance (it never resolves); it is still read for its content key.
MO_RE = re.compile(r"^mo-([0-9a-f]{8})(?:-[0-9a-f]{6}-[0-9a-z]{1,10})?$")
DEFAULTS = {"window_h": 4.0, "life_min_h": 12.0, "life_max_h": 36.0, "town_cap": 8, "npc_share": 0.5, "weight": 1.0,
            "one_off_weight": 2.0, "one_off_life_h": 2.0, "lore_weight": 1.0}
# Everyday words that say nothing about WHICH story was told (Codex design R2: "The town is quiet today" must not count
# as telling a story about the town council). Compared as 4-letter stems, like rumour_speech.used().
STOP = frozenset(w[:4] for w in """town towns folk folks people person today tonight yesterday morning evening night week
weeks month year years time times thing things some someone something much many more most very just only even still
again always never quite rather really surely about above after before being between could would should might there
their them they these those this that what when where which while whose with within without from into onto over under
have having been were will shall said says saying tell tells telling told hear hears heard hearing talk talks talking
word words news tale tales story stories rumour rumours gossip know knows known think thinks thought seem seems
good well fine quiet busy little long short like likes look looks make makes made come comes came went goes going
here near nearby around round back down away other another every each both either left right place places""".split())
_lock = threading.Lock()
_pool = {"mtime": None, "path": None, "data": None}


def _u(*parts):
    """A stable 0..1 from the parts (SHA-256, never Python's hash())."""
    h = hashlib.sha256("|".join(("avallone-madeup",) + tuple(str(p) for p in parts)).encode("utf-8")).digest()
    return int.from_bytes(h[:8], "big") / 2.0 ** 64


def content_key(line):
    """8 hex for a pool line's text (case and spacing do not count; any other edit is a new key)."""
    norm = " ".join(str(line).split()).lower()
    return hashlib.sha256(("avallone-madeup-line|" + norm).encode("utf-8")).hexdigest()[:8]


def town_code(town):
    return hashlib.sha256(("avallone-madeup-town|" + str(town).lower()).encode("utf-8")).hexdigest()[:4]


def _b36(n):
    n, out = int(n), ""
    while True:
        n, r = divmod(n, 36)
        out = "0123456789abcdefghijklmnopqrstuvwxyz"[r] + out
        if not n:
            return out


def story_id(key, town, window):
    return f"mu-{key}-{town_code(town)}-{_b36(window)}"


def key_of(sid):
    """The content key inside a made-up or one-off id, or None for any other id."""
    m = MU_RE.match(str(sid or "")) or MO_RE.match(str(sid or ""))
    return m.group(1) if m else None


# Three-letter words that say nothing; any OTHER three-letter word (eel, ale, pig, fox, inn) is as telling as a long one.
STOP3 = frozenset("""the and for but was has had are not all any out off now new old one two its his her who how why you
our say saw got get let yet too may can did way day man men lot bit few big own due per via she him yes nor ago far set
put ran run sat use try ask end top low odd bad""".split())


def _stems(text):
    """Word stems as rumour_speech.used() compares them: the first four letters of a word of four or more, and a
    three-letter word whole."""
    return [w[:4] for w in re.findall(r"[a-z]+", str(text).lower()) if len(w) >= 4 or (len(w) == 3 and w not in STOP3)]


def load_pool(path=None):
    """{any, towns, town_share, flow, lines: {key: line}, df: {stem: lines it is in}, n}. Re-read when the file changes.
    Raises ValueError when two different lines share a content key (never treated as one story)."""
    path = path or smalltalk.PATH
    with _lock:
        m = os.path.getmtime(path)
        if _pool["mtime"] == m and _pool["path"] == path:
            return _pool["data"]
        with open(path, encoding="utf-8") as fh:
            d = json.load(fh)
        lines, df = {}, {}
        every = list(d.get("any") or []) + [x for v in (d.get("towns") or {}).values() for x in v]
        for line in every:
            k = content_key(line)
            if k in lines and " ".join(lines[k].split()).lower() != " ".join(str(line).split()).lower():
                raise ValueError(f"made-up pool: two lines share the key {k}: {lines[k]!r} and {line!r}")
            lines[k] = line
        for line in lines.values():
            for s in set(_stems(line.replace("{town}", " ").replace("{near}", " "))):
                df[s] = df.get(s, 0) + 1
        data = {"any": list(d.get("any") or []), "towns": dict(d.get("towns") or {}),
                "town_share": float(d.get("town_share", 0.3)), "flow": {**DEFAULTS, **(d.get("flow") or {})},
                "lines": lines, "df": df, "n": len(lines)}
        _pool.update(mtime=m, path=path, data=data)
        return data


def text_keys(text, skip=(), path=None):
    """(strong, weak) word stems that show a reply told THIS story. A reply counts as telling it when it carries ONE
    strong stem or TWO weak ones (rumour_speech.used).
      strong = the story's distinctive words: not an everyday word (STOP), not `skip` (the town's own name), and not a
               word that sits in many pool lines;
      weak   = its other content words - common in the pool ("river", "child", "boat") but telling when two of them turn
               up together in one answer.
    First real-model trial, 2026-11-11 (c2_r1.out): asking for two distinctive stems missed 8 of 30 good tellings, all
    paraphrases ("a fancy bird ... making a mess of the market" for the escaped peacock), and the fixed wording then
    replaced a better reply. A story with no usable words gives ([], []) and always gets the fixed wording."""
    try:
        pool = load_pool(path)
        df, n = pool["df"], pool["n"]
    except Exception:
        df, n = {}, 0
    # "common" = in more than 3 % of the pool's lines. (2 % until the pool grew from 678 to 724 lines on 2026-11-11 and
    # "market" tipped over it, which un-recognised a good telling from the trial; 3 % keeps it and its like distinctive.)
    common = max(6, n * 3 // 100)
    skip = {w[:4] for w in re.findall(r"[a-z]+", " ".join(skip).lower()) if len(w) >= 4}
    content = {s for s in _stems(text) if s not in STOP and s not in skip}
    strong = sorted(s for s in content if df.get(s, 0) <= common)
    return strong, sorted(content - set(strong))


# The fixed wordings. After Codex code R2 a made-up story must be plainly recognisable in the reply to count as told,
# and the model rewords so freely that in the trials a third to a half of them end up here (c2_r5.out: 10 of 23) - so
# there are a dozen openings, not four, and the caller keeps the NPC's own little action in front (brain.py).
FRAMES = ("Folk are saying {t}.", "The talk going round is that {t}.", "All I have heard is that {t}.",
          "Well, they do say {t}.", "Word in the market is that {t}.", "They tell me {t}.", "Have you not heard? {T}.",
          "The only talk today is that {t}.", "It seems {t}.", "I am told {t}.", "You will hear it everywhere: {t}.",
          "Here is one for you: {t}.")
FRAMES_SENTENCE = ("The talk going round: {t}", "All I have heard: {t}", "Word in the market: {t}", "Here is one for you: {t}")


def canonical(text, pick=0.0):
    """A fixed, checked way to say a nameless story: used when the model's own reply does not show it was told, so the
    story is never spoken without being marked as heard (Codex design R2). `pick` in 0..1 chooses the frame."""
    t = " ".join(str(text).split())
    if not t:
        return ""
    if t[:1].isupper() and len(t.split()) > 3:          # a GM's own sentence: quoted whole, with its own full stop
        if t[-1] not in ".!?":
            t += "."
        return FRAMES_SENTENCE[int(pick * len(FRAMES_SENTENCE)) % len(FRAMES_SENTENCE)].format(t=t)
    t = t.rstrip(".!? ")
    return FRAMES[int(pick * len(FRAMES)) % len(FRAMES)].format(t=t, T=t[:1].upper() + t[1:])


class MadeUp:
    def __init__(self, ledger, rumours, path=None):
        self.ledger, self.rumours, self.path = ledger, rumours, path or smalltalk.PATH
        self._born = {}      # (town, window) -> story dict (the pinned row, in memory)
        self._spread = {}    # story id -> {town: (arrive_pt, hops)}
        with ledger.lock:
            ledger.db.execute("""CREATE TABLE IF NOT EXISTS madeup_stories (
                                   id TEXT PRIMARY KEY, kind TEXT NOT NULL, town TEXT NOT NULL, key TEXT NOT NULL,
                                   text TEXT NOT NULL, born_pt INTEGER NOT NULL, expires_pt INTEGER NOT NULL)""")
            ledger.db.execute("""CREATE UNIQUE INDEX IF NOT EXISTS madeup_stories_born
                                   ON madeup_stories (town, born_pt) WHERE kind = 'made_up'""")
            ledger.db.execute("CREATE INDEX IF NOT EXISTS madeup_stories_expires ON madeup_stories (expires_pt)")

    # ---- settings ----
    def flow(self):
        return load_pool(self.path)["flow"]

    def _window_s(self):
        return max(600, int(float(self.flow()["window_h"]) * 3600))

    def _back(self):
        """How many windows back a story can still be alive."""
        return int(float(self.flow()["life_max_h"]) * 3600 // self._window_s()) + 1

    def _neighbours(self, town):
        return sorted(b if a == town else a for a, b, *_ in self.rumours.cfg.get("edges", []) if town in (a, b))

    # ---- the story born in `town` in window `w` ----
    def _story(self, town, w):
        got = self._born.get((town, w))
        if got is not None:
            return got
        born = w * self._window_s()
        with self.ledger.rlock:
            row = self.ledger.rdb.execute(
                "SELECT id, key, text, born_pt, expires_pt FROM madeup_stories WHERE kind = 'made_up' AND town = ? AND born_pt = ?",
                (town, born)).fetchone()
        if row is None:
            row = self._work_out(town, w, born)
            if row is None:
                return None
            with self.ledger.lock:
                self.ledger.db.execute(
                    "INSERT OR IGNORE INTO madeup_stories (id, kind, town, key, text, born_pt, expires_pt) VALUES (?, 'made_up', ?, ?, ?, ?, ?)",
                    (row[0], town, row[1], row[2], row[3], row[4]))
                again = self.ledger.db.execute(
                    "SELECT id, key, text, born_pt, expires_pt FROM madeup_stories WHERE kind = 'made_up' AND town = ? AND born_pt = ?",
                    (town, born)).fetchone()
            row = again or row      # two threads at once: whichever was written first is the story
        s = {"id": row[0], "key": row[1], "text": row[2], "born_pt": int(row[3]), "expires_pt": int(row[4]), "origin": town}
        if len(self._born) > 20000:
            self._born.clear()
        self._born[(town, w)] = s
        return s

    def _work_out(self, town, w, born):
        pool, flow = load_pool(self.path), self.flow()
        own = pool["towns"].get(town) or []
        # the keys this town's own recent stories already use: never the same line twice at once in its home town
        taken = set()
        for k in range(1, self._back() + 1):
            if w - k < 0:
                break
            prev = self._born.get((town, w - k))
            if prev is None:
                with self.ledger.rlock:
                    r = self.ledger.rdb.execute(
                        "SELECT key FROM madeup_stories WHERE kind = 'made_up' AND town = ? AND born_pt = ?",
                        (town, (w - k) * self._window_s())).fetchone()
                if r:
                    taken.add(r[0])
            else:
                taken.add(prev["key"])
        for salt in range(8):
            lines = own if own and _u(town, w, "pool", salt) < pool["town_share"] else pool["any"]
            if not lines:
                lines = pool["any"] or own
            if not lines:
                return None
            line = lines[int(_u(town, w, "line", salt) * len(lines)) % len(lines)]
            key = content_key(line)
            if key not in taken or salt == 7:
                break
        near = self._neighbours(town)
        text = line.format(town=town, near=near[int(_u(town, w, "near") * len(near)) % len(near)] if near else town)
        lo, hi = float(flow["life_min_h"]), float(flow["life_max_h"])
        life = int((lo + _u(town, w, "life") * max(0.0, hi - lo)) * 3600)
        return story_id(key, town, w), key, text, born, born + life

    def _arrivals(self, s):
        got = self._spread.get(s["id"])
        if got is None:
            sp = self.rumours._spread({"id": s["id"], "origin": s["origin"], "first_pt": s["born_pt"]})
            got = {t: (arrive, len(path) - 1) for t, (arrive, path) in sp.items()}
            if len(self._spread) > 5000:
                self._spread.clear()
            self._spread[s["id"]] = got
        return got

    # ---- what a town holds ----
    def town_rows(self, pt, town):
        """[(story, arrived_pt, hops)] live in `town` at played second `pt`: freshest arrival first, at most town_cap."""
        self.rumours._load()
        pt = int(pt)
        if town not in self.rumours.cfg["towns"]:
            return []
        dropped = self.rumours._dropped()
        w_now, out = pt // self._window_s(), []
        for origin in self.rumours.cfg["towns"]:
            for w in range(max(0, w_now - self._back()), w_now + 1):
                s = self._story(origin, w)
                if s is None or not (s["born_pt"] <= pt < s["expires_pt"]) or s["id"] in dropped:
                    continue
                hit = self._arrivals(s).get(town)
                if hit and hit[0] <= pt:
                    out.append((s, hit[0], hit[1]))
        out.sort(key=lambda r: (-r[1], r[0]["id"]))
        cap = int(self.flow()["town_cap"])
        seen, kept = set(), []

        def take(rows, limit):
            for r in rows:                  # the same line from two towns is one piece of talk here, not two
                if len(kept) >= limit:
                    break
                if r[0]["key"] in seen:
                    continue
                seen.add(r[0]["key"])
                kept.append(r)
        # Half the places go to the town's OWN freshest stories, so each town keeps its own talk; the rest to whatever
        # arrived most recently from anywhere (build note, 2026-11-11: freshest-first alone filled Britain with seven
        # arrivals and one of its own, and every town held nearly the same eight).
        take([r for r in out if r[0]["origin"] == town], cap // 2)
        take(out, cap)
        kept.sort(key=lambda r: (-r[1], r[0]["id"]))
        return kept

    def knows(self, npc_id, sid):
        """Each NPC has heard only a share of the town's set - fixed for that NPC and that story."""
        return not npc_id or _u("npc", npc_id, sid) < float(self.flow()["npc_share"])

    @staticmethod
    def _row(s, town, arrived, kind=T_STORY, weight=1.0):
        return {"id": s["id"], "char": "", "type": kind, "town": town, "arrived_pt": arrived, "vague": True,
                "descriptor": "", "text": s["text"], "key": s["key"], "source": SOURCE, "weight": float(weight),
                "_g": {"id": s["id"], "type": kind, "facts": {}}, "_garbles": []}

    def rows(self, pt, town, npc_id=""):
        """The town's made-up stories THIS NPC has heard, as speech rows (rumour_speech.select)."""
        w = float(self.flow()["weight"])
        return [self._row(s, town, arrived, weight=w) for s, arrived, _ in self.town_rows(pt, town) if self.knows(npc_id, s["id"])]

    def one_off(self, pt, town, rng, avoid=()):
        """One line from the big pool, told once: it has an identity too (its content key), so it is tracked as heard.
        Never a line whose key is in `avoid` or is live in this town as a story. None when nothing is left."""
        pool = load_pool(self.path)
        avoid = set(avoid) | {s["key"] for s, _, _ in self.town_rows(pt, town)}
        own = [x for x in (pool["towns"].get(town) or []) if content_key(x) not in avoid]
        lines = own if own and rng.random() < pool["town_share"] else [x for x in pool["any"] if content_key(x) not in avoid]
        if not lines:
            lines = own
        if not lines:
            return None
        line = rng.choice(lines)
        near = self._neighbours(town)
        key = content_key(line)
        text = line.format(town=town, near=rng.choice(near) if near else town)
        s = {"id": self.one_off_id(key, text, pt), "key": key, "text": text}
        return self._row(s, town, int(pt), T_ONE)

    def _one_off_life(self):
        return max(60, int(float(self.flow()["one_off_life_h"]) * 3600))

    def one_off_id(self, key, text, pt):
        """Codex code R1 (RUM-C2): one id names ONE rendered text and ONE lifetime, for good. The template's content key
        is only part of it (repeat suppression goes by that key); the rendered text - the town and the neighbouring
        town filled in - and the played-time window make up the rest, so the same template told in another town, or
        with another neighbour, or hours later, is another instance and can never change what this id means."""
        window = max(0, int(pt)) // self._one_off_life()
        return f"mo-{key}-{hashlib.sha256(str(text).encode()).hexdigest()[:6]}-{_b36(window)}"

    def pin_one_off(self, row, pt):
        """A one-off that is actually offered is kept for a short while, so "tell me more" finds exactly that line.
        The row is written ONCE and never changed: its birth and expiry come from the id's own window (it lives from the
        start of that window to the end of the next), so a second offer, another listener or a rewound clock cannot
        move them. False = another text already holds this id (a hash collision): the caller does not offer the line."""
        m = re.match(r"^mo-[0-9a-f]{8}-[0-9a-f]{6}-([0-9a-z]{1,10})$", str(row["id"]))
        if not m:
            return False
        life = self._one_off_life()
        born = int(m.group(1), 36) * life
        with self.ledger.lock:
            self.ledger.db.execute(
                "INSERT OR IGNORE INTO madeup_stories (id, kind, town, key, text, born_pt, expires_pt) VALUES (?, 'one_off', ?, ?, ?, ?, ?)",
                (row["id"], row["town"], row["key"], row["text"], born, born + 2 * life))
            kept = self.ledger.db.execute("SELECT text FROM madeup_stories WHERE id = ?", (row["id"],)).fetchone()
        return kept is not None and kept[0] == row["text"]

    def resolve(self, sid, pt, town):
        """The exact story an id names, for a follow-up - whatever this NPC's share of the set is now - unless it has
        expired, has not happened yet (clock rewound) or was dropped. None otherwise."""
        if not key_of(sid):
            return None
        with self.ledger.rlock:
            r = self.ledger.rdb.execute(
                "SELECT id, kind, key, text, born_pt, expires_pt FROM madeup_stories WHERE id = ?", (str(sid),)).fetchone()
        if r is None or not (int(r[4]) <= int(pt) < int(r[5])) or r[0] in self.rumours._dropped():
            return None
        return self._row({"id": r[0], "key": r[2], "text": r[3]}, town, int(r[4]), T_ONE if r[1] == "one_off" else T_STORY)

    # ---- GM tools ----
    def view(self, pt, town=None):
        """Live made-up stories per town, in the shape of Rumours.view() plus their source type."""
        self.rumours._load()
        out = []
        for t in sorted(self.rumours.cfg["towns"]):
            if town and t.lower() != str(town).lower():
                continue
            for s, arrived, hops in self.town_rows(pt, t):
                out.append({"town": t, "id": s["id"], "type": T_STORY, "source": SOURCE, "char": "", "hops": hops,
                            "arrived_pt": arrived, "funny": False, "garbles": [], "text": s["text"], "events": 0,
                            "origin": s["origin"], "expires_pt": s["expires_pt"]})
        return out

    def tick(self, pt):
        """Once a minute (with the rumour tick): forget stories that ran out more than a horizon ago."""
        with self.ledger.lock:
            self.ledger.db.execute("DELETE FROM madeup_stories WHERE expires_pt < ?", (int(pt) - self.rumours.horizon(),))
