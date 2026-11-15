"""Step 2e - NPC backgrounds (design §3, §8.5, §9.3-9.5; the owner, chat, 2026-11-12/13; Codex design R6 = GO).

Every ordinary townsperson gets one home, one former life and one past event from hand-written, the owner-approved lists
(backgrounds.json, generated from the wiki page). Stored the FIRST time it is needed and never changed - not by a move,
a despawn, a list edit or a world restore (the owner: a past is a past).
- One writer transaction (Codex R2-3): under the ledger's writer lock, BEGIN IMMEDIATE, re-read the NPC's row (return it
  untouched if there), read that town's occupied events, choose, insert. Inspection (`get`) never assigns.
- Choice: for each slot, the entries ranked by a stable hash of the NPC's AI id (rendezvous, by permanent entry id), the
  first that keeps the whole biography compatible: no tag pair on both sides; a "came from X" home never for an NPC in X;
  a former life is never the NPC's present trade (except L40) and never clashes with what that trade needs; the past event
  is not already held by an NPC assigned in the same town. Only when a town has used every compatible event, the least-used
  one (logged) - uniqueness is for the town where the past was ASSIGNED (the owner, chat, 2026-11-12).
- Wording follows the stored town: an NPC standing elsewhere says it was born in its stored town, never "here".
"""
import hashlib
import json
import math
import os
import re
import threading
from datetime import datetime, timezone

CONFIG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "backgrounds.json")
_lock = threading.Lock()
_cache = {"mtime": None, "cfg": None}

# Codex design R2-4 / §9.4: what an NPC's present trade rules out (matched on the role words the shard sends).
TRADE_EXCLUDES = (
    (re.compile(r"\b(healer|physician|surgeon|herbalist)\b"), {"faint"}),
    (re.compile(r"\b(ship\w*|fisher\w*|sailor|mariner|captain|dock\w*|boatwright)\b"), {"nosea"}),
    (re.compile(r"\b(scribe|mage|wizard|librarian|book\w*|scholar|cartographer|alchemist)\b"), {"noread"}),
    (re.compile(r"\b(animal|stable\w*|vet\w*|rancher|tamer|kennel\w*)\b"), {"nodogs"}),
)
ASKED = re.compile(
    r"\b(where (are|were) you (from|born|raised)|where('?s| is) your home|your home ?town|grew up|"
    r"what did you do before|before (this|that|you were)|your past|how long (have|has) you (been|lived|worked)|"
    r"tell me about yourself|your (story|life story))\b",
    re.IGNORECASE,
)


def cfg():
    with _lock:
        try:
            mt = os.stat(CONFIG).st_mtime_ns
        except OSError:
            return None
        if _cache["mtime"] != mt:
            with open(CONFIG, encoding="utf-8") as f:
                c = json.load(f)
            c["_by_id"] = {r["id"]: r for k in ("homes", "lives", "events") for r in c[k]}
            _cache["cfg"], _cache["mtime"] = c, mt
        return _cache["cfg"]


def ensure_table(ledger):
    with ledger.lock:
        ledger.db.execute("""CREATE TABLE IF NOT EXISTS npc_backgrounds (
                               npc_id TEXT PRIMARY KEY, town TEXT NOT NULL, home_id TEXT NOT NULL, life_id TEXT NOT NULL,
                               event_id TEXT NOT NULL, assigned_utc TEXT NOT NULL)""")
        ledger.db.execute("CREATE INDEX IF NOT EXISTS npc_backgrounds_town ON npc_backgrounds (town, event_id)")


def _score(npc, slot, item_id):
    h = hashlib.sha256(f"avallone-background|{npc}|{slot}|{item_id}".encode("utf-8")).digest()
    return -math.log((int.from_bytes(h[:8], "big") + 1) / (2.0 ** 64 + 2))


def _ranked(npc, slot, rows):
    return sorted(rows, key=lambda r: (_score(npc, slot, r["id"]), r["id"]))


def trade_excludes(role, trade):
    words = f"{role or ''} {trade or ''}".lower()
    out = set()
    for rx, tags in TRADE_EXCLUDES:
        if rx.search(words):
            out |= tags
    return out


def _clash(tags, pairs):
    return any(a in tags and b in tags for a, b in pairs)


def _same_trade(life, role):
    if life["id"] == "L40" or not role:
        return False
    words = {w for w in re.findall(r"[a-z]+", role.lower()) if len(w) > 3}
    return bool(words & set(re.findall(r"[a-z]+", life["text"].lower())))


def get(ledger, npc_id):
    """The stored row or None. Read-only: never assigns."""
    with ledger.rlock:
        row = ledger.rdb.execute("SELECT town, home_id, life_id, event_id FROM npc_backgrounds WHERE npc_id = ?",
                                 (str(npc_id)[:36],)).fetchone()
    return None if row is None else {"town": row[0], "home": row[1], "life": row[2], "event": row[3]}


def _choose(c, npc, town, role, trade, held):
    pairs = [tuple(p) for p in c["pairs"]]
    banned = trade_excludes(role, trade)
    town_l = (town or "").lower()
    homes = [h for h in _ranked(npc, "home", c["homes"])
             if not (h.get("town", "").lower() == town_l and "moved" in h["tags"]) and not (set(h["tags"]) & banned)]
    lives = [x for x in _ranked(npc, "life", c["lives"]) if not _same_trade(x, role) and not (set(x["tags"]) & banned)]
    events = [e for e in _ranked(npc, "event", c["events"]) if not (set(e["tags"]) & banned)]
    fresh = [e for e in events if e["id"] not in held]
    for pool, exhausted in ((fresh, False), (sorted(events, key=lambda e: (held.get(e["id"], 0), _score(npc, "event", e["id"]))), True)):
        for e in pool:
            for h in homes:
                if _clash(set(e["tags"]) | set(h["tags"]), pairs):
                    continue
                for life in lives:
                    if not _clash(set(e["tags"]) | set(h["tags"]) | set(life["tags"]), pairs):
                        return h, life, e, exhausted
    return None


def assign(ledger, npc_id, town, role="", trade="", now=None, log=None):
    """The NPC's background, assigned now if it has none. One writer transaction; an existing row is returned untouched."""
    c = cfg()
    npc = str(npc_id or "")[:36]
    if not c or not npc or not town:
        return None
    with ledger.lock:
        ledger.db.execute("BEGIN IMMEDIATE")
        try:
            row = ledger.db.execute("SELECT town, home_id, life_id, event_id FROM npc_backgrounds WHERE npc_id = ?",
                                    (npc,)).fetchone()
            if row is not None:
                ledger.db.execute("COMMIT")
                return {"town": row[0], "home": row[1], "life": row[2], "event": row[3]}
            held = {}
            for (eid,) in ledger.db.execute("SELECT event_id FROM npc_backgrounds WHERE town = ?", (town,)):
                held[eid] = held.get(eid, 0) + 1
            got = _choose(c, npc, town, role, trade, held)
            if got is None:
                ledger.db.execute("COMMIT")
                return None
            h, life, e, exhausted = got
            stamp = (now or datetime.now(timezone.utc)).strftime("%Y-%m-%dT%H:%M:%SZ")
            ledger.db.execute("INSERT INTO npc_backgrounds (npc_id, town, home_id, life_id, event_id, assigned_utc) "
                              "VALUES (?, ?, ?, ?, ?, ?)", (npc, town, h["id"], life["id"], e["id"], stamp))
            ledger.db.execute("COMMIT")
        except Exception:
            ledger.db.execute("ROLLBACK")
            raise
    if exhausted and log:
        log(event="backgrounds", op="assign", outcome="exhausted", town=town, event_id=e["id"])
    return {"town": town, "home": h["id"], "life": life["id"], "event": e["id"]}


def text(bg, standing_in):
    """The three sentences for the prompt, wording bound to the stored town. None if an id is no longer in the lists."""
    c = cfg()
    if not c or not bg:
        return None
    by_id = c["_by_id"]
    home, life, event = by_id.get(bg["home"]), by_id.get(bg["life"]), by_id.get(bg["event"])
    if not (home and life and event):
        return None   # a removed entry is never silently replaced by another past
    here = (standing_in or "").lower() == (bg["town"] or "").lower()
    if "born" in home["tags"]:
        home_line = home["text"] if here else f"Born and raised in {bg['town']}, and lives elsewhere now."
    elif home.get("town", "").lower() == (standing_in or "").lower():
        home_line = ""            # never "came from X" while standing in X
    else:
        home_line = home["text"]
    life_line = life["text"]
    return " ".join(x for x in (home_line, "Before this: " + life_line[0].lower() + life_line[1:], event["text"]) if x)


# Codex code R6 (play-test patch): a STRICT check for ending a gossip thread - the question must be plainly about the
# NPC itself ("you"/"your"). Bare "before that" or "where did they grow up" is a question about the STORY and keeps the
# thread. ASKED (above) stays the looser test for putting the background in the prompt.
ASKED_SELF = re.compile(
    r"\b(where (are|were) you (from|born|raised)|where did you (grow up|come from|live before)|where('?s| is) your home|"
    r"your home ?town|did you grow up|you grew up|what did you do before|what were you before|before you (were|came)|"
    r"how long (have|has) you (been|lived|worked)|tell me about yourself|your (own )?(past|life story))\b",
    re.IGNORECASE,
)


def asked(utterance):
    return bool(ASKED.search(str(utterance or "")))


def asked_self(utterance):
    return bool(ASKED_SELF.search(str(utterance or "")))


def prompt_line(sentences, asked_now):
    if not sentences:
        return ""
    if asked_now:
        return (f"Your own past (you were asked about it - answer from this, in your own words, and invent nothing beyond "
                f"it): {sentences}")
    return (f"Something from your own past you may mention in passing if it fits (never invent more): {sentences}")
