"""Avallone Phase 4c2 - reflection and beliefs (design v2 #9, Projects/Avallone-AI-NPC-Plan.md).

When a companion and a character have been quiet for a while, the model is asked to condense what happened
since the last reflection into at most three beliefs. Code, not the model, decides what is kept:

- the model sees facts only - events the shard recorded. Conversation is left out entirely, so nothing a
  player *said* can become a belief (the poisoning rule);
- every belief names a topic from a fixed list and cites event ids; a belief with an unknown topic, no
  citation, or a citation that was not in what the model was shown is discarded;
- the live belief on a topic is the newest one that is still valid, worked out when asked - so if a world
  restore rolls back the event a newer belief rests on, the older one counts again;
- every event is reflected on at most once, oldest first, in batches (reflection_inputs lists exactly which);
- an answer that is cut off or is not the JSON asked for stores nothing and is tried again later.

Shadow mode: beliefs are stored and counted but never put in a prompt. Going live later uses only beliefs
from reflections marked reviewed. Nothing here runs on the reply path.
"""
import json
import re
from datetime import datetime, timedelta, timezone

import memory

PROMPT_VERSION = "r2"   # 2026-11-04: ids kept out of the belief sentence (Codex r2)
MAX_BELIEFS = 3
MAX_EVENTS = 40
MIN_BELIEF_CHARS, MAX_BELIEF_CHARS = 20, 220
MAX_CITES = 6
MAX_LINE_CHARS = 240

# Names inside event text (characters, renamed pets) are chosen by players: keep plain words only, bounded.
UNSAFE = re.compile(r"[^A-Za-z0-9 .,'\-:;!?()]")


class UnusableAnswer(Exception):
    """The model answered, but not with the JSON asked for (cut off, malformed). Nothing is stored."""

# Facts only. Conversation = player claims; turning points are already permanent memories with their own text.
SKIP_KINDS = ("conversation", "turning_point", "turning_point_revoked", "character_deleted",
              "deed",  # Phase 5a: world deeds feed rumours, never a companion's reflection
              "visit_summary", "visited")  # Phase 5c: a player's claims never become a belief (poisoning rule)

# Controlled topic keys: one live belief per topic per pair, so the newest view replaces the old one.
TOPICS = {
    "their_courage": "how they face danger - brave, reckless, careful",
    "their_honour": "how they treat other people and the law",
    "their_care_for_you": "how they treat you - your safety, your wounds, sending you home",
    "their_fortunes": "how they fare - deaths, close calls, strength",
    "your_travels": "the places you have gone together",
    "your_bond": "where the two of you stand now",
}

SCHEMA = {
    "type": "object",
    "properties": {"beliefs": {"type": "array", "maxItems": MAX_BELIEFS, "items": {
        "type": "object",
        "properties": {
            "topic": {"type": "string", "enum": sorted(TOPICS)},
            "belief": {"type": "string"},
            "cites": {"type": "array", "items": {"type": "string"}},
        },
        "required": ["topic", "belief", "cites"]}}},
    "required": ["beliefs"],
}

EVENT_ID = re.compile(r"\[?(\d{1,9}-\d{1,9})\]?")
# A bracketed id list written into the sentence ("... together. [1-1, 1-6]") - removed whole, so no ", " is left
# behind (gemma4:26b-a4b did this; 2026-11-04 probe).
ID_LIST = re.compile(r"\s*\[\s*\d{1,9}-\d{1,9}(?:\s*[,;]?\s*\d{1,9}-\d{1,9})*\s*\]")

MIGRATION_V3 = (   # IF NOT EXISTS: a brain rolled back to version 2 and upgraded again must not trip on these
    """CREATE TABLE IF NOT EXISTS reflections (
        id INTEGER PRIMARY KEY,
        npc_id TEXT NOT NULL,
        char_id TEXT NOT NULL,
        upto_epoch INTEGER NOT NULL,
        upto_seq INTEGER NOT NULL,
        at_utc TEXT NOT NULL,
        mode TEXT NOT NULL,
        model TEXT NOT NULL,
        prompt_version TEXT NOT NULL,
        events INTEGER NOT NULL,
        kept INTEGER NOT NULL,
        discarded INTEGER NOT NULL,
        note TEXT,
        reviewed INTEGER NOT NULL DEFAULT 0,
        UNIQUE (npc_id, char_id, upto_epoch, upto_seq))""",
    """CREATE TABLE IF NOT EXISTS reflection_inputs (
        event_id TEXT PRIMARY KEY REFERENCES events (event_id),
        reflection_id INTEGER NOT NULL REFERENCES reflections (id))""",
    """CREATE TABLE IF NOT EXISTS beliefs (
        id INTEGER PRIMARY KEY,
        reflection_id INTEGER NOT NULL REFERENCES reflections (id),
        npc_id TEXT NOT NULL,
        char_id TEXT NOT NULL,
        topic TEXT NOT NULL,
        text TEXT NOT NULL,
        at_utc TEXT NOT NULL)""",
    "CREATE INDEX IF NOT EXISTS beliefs_pair ON beliefs (npc_id, char_id, topic)",
    """CREATE TABLE IF NOT EXISTS belief_cites (
        belief_id INTEGER NOT NULL REFERENCES beliefs (id),
        event_id TEXT NOT NULL REFERENCES events (event_id),
        PRIMARY KEY (belief_id, event_id))""",
    """CREATE TABLE IF NOT EXISTS npc_personas (
        npc_id TEXT PRIMARY KEY,
        persona_key TEXT NOT NULL,
        at_utc TEXT NOT NULL)""",
)


# Columns this code needs. CREATE TABLE IF NOT EXISTS would quietly accept an older, different table.
REQUIRED_COLUMNS = {
    "reflections": {"id", "npc_id", "char_id", "upto_epoch", "upto_seq", "mode", "model", "prompt_version", "events",
                    "kept", "discarded", "note", "reviewed"},
    "reflection_inputs": {"event_id", "reflection_id"},
    "beliefs": {"id", "reflection_id", "npc_id", "char_id", "topic", "text", "at_utc"},
    "belief_cites": {"belief_id", "event_id"},
    "npc_personas": {"npc_id", "persona_key", "at_utc"},
}
GAVE_UP = "gave up: repeated unusable answers"


def check_schema(db):
    for table, need in REQUIRED_COLUMNS.items():
        have = {r[1] for r in db.execute(f"PRAGMA table_info({table})")}
        if need - have:
            raise RuntimeError(f"memory db table {table} lacks {sorted(need - have)} - not the schema this brain knows")


def _parse(at_utc):
    return datetime.strptime(at_utc, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)


def _note_persona(self, npc_id, persona_key):
    """Which persona speaks for this companion - so a reflection after a restart still knows whose voice to use."""
    seen = self.__dict__.setdefault("_personas", {})   # one write per companion per run, not one per reply
    if seen.get(npc_id) == persona_key:
        return
    with self.lock:
        if seen.get(npc_id) == persona_key:
            return
        self.db.execute(
            """INSERT INTO npc_personas (npc_id, persona_key, at_utc) VALUES (?, ?, ?)
               ON CONFLICT (npc_id) DO UPDATE SET persona_key = excluded.persona_key, at_utc = excluded.at_utc
               WHERE persona_key != excluded.persona_key""", (str(npc_id)[:36], str(persona_key)[:40], memory.now_utc()))
        seen[npc_id] = persona_key   # only once the write has succeeded


def _pending_reflections(self, idle_s=900, min_events=2, now=None, skip=(), limit=None):
    """Batches ready to reflect on: for a pair with nothing received for `idle_s` seconds, the OLDEST live factual
    events not yet reflected on (at most MAX_EVENTS; the rest wait for the next pass). A batch needs `min_events`
    facts, or one big one (a death is worth thinking about by itself). Pairs in `skip` are left out.
    Returns [{npc_id, char_id, persona_key, upto: (epoch, seq), events: [{event_id, kind, facts, at_utc}]}]."""
    now = now or datetime.now(timezone.utc)
    cutoff = (now - timedelta(seconds=idle_s)).strftime("%Y-%m-%dT%H:%M:%SZ")
    marks = ",".join("?" * len(SKIP_KINDS))
    out = []
    with self.lock:
        pairs = self.db.execute(
            f"""SELECT npc_id, char_id, MAX(received_utc) last FROM events
                WHERE status = 'live' AND char_id NOT IN ({memory.TOMBSTONED})
                GROUP BY npc_id, char_id HAVING last <= ? ORDER BY last""", (cutoff,)).fetchall()
        for p in pairs:
            if (p["npc_id"], p["char_id"]) in skip:
                continue
            rows = self.db.execute(
                f"""SELECT event_id, epoch, seq, kind, facts, at_utc FROM events
                    WHERE npc_id = ? AND char_id = ? AND status = 'live' AND kind NOT IN ({marks})
                      AND event_id NOT IN (SELECT event_id FROM reflection_inputs)
                    ORDER BY epoch, seq LIMIT ?""",
                (p["npc_id"], p["char_id"], *SKIP_KINDS, MAX_EVENTS)).fetchall()
            if len(rows) < min_events and not any(r["kind"] in memory.BIG_KINDS for r in rows):
                continue
            persona = self.db.execute("SELECT persona_key FROM npc_personas WHERE npc_id = ?", (p["npc_id"],)).fetchone()
            out.append({
                "npc_id": p["npc_id"], "char_id": p["char_id"], "persona_key": persona[0] if persona else None,
                "upto": (rows[-1]["epoch"], rows[-1]["seq"]),
                "events": [{"event_id": r["event_id"], "kind": r["kind"], "facts": json.loads(r["facts"]),
                            "at_utc": r["at_utc"]} for r in rows],
            })
            if limit and len(out) >= limit:
                break
    return out


def fact_line(ev, now):
    text = " ".join(UNSAFE.sub(" ", memory.render(ev["kind"], ev["facts"])).split())[:MAX_LINE_CHARS]
    return f"[{ev['event_id']}] {memory.when(ev['at_utc'], now)}: {text}"


def build_prompt(session, persona_text="", now=None):
    """(system, user). Event text goes in as data lines under the instructions, never inside them."""
    now = now or datetime.now(timezone.utc)
    topics = "\n".join(f"- {k}: {v}" for k, v in TOPICS.items())
    system = (
        "You keep the private thoughts of one character in a medieval fantasy world. You are given a list of things "
        "that happened between her and one travelling companion. Each line is a fact and starts with its id in "
        "square brackets.\n\n"
        f"Write at most {MAX_BELIEFS} beliefs she has come to hold about this companion because of these facts. Rules:\n"
        "- Write each belief to her, in the second person and present tense, one plain sentence "
        '("You think they ...").\n'
        "- Use only what the facts show. Do not invent events, names, places, numbers or words anyone said.\n"
        "- Each belief must list in \"cites\" the ids of the facts it rests on - only ids from the list.\n"
        "- Never put ids or square brackets in the belief sentence itself; ids go only in \"cites\".\n"
        "- Pick each belief's topic from this list, each topic at most once:\n" + topics + "\n"
        "- If the facts show nothing worth believing, return an empty list.")
    if persona_text:
        system += "\n\nThe character, for her voice only (nothing here is a fact about the companion):\n" + persona_text[:2500]
    return system, "The facts:\n" + "\n".join(fact_line(ev, now) for ev in session["events"])


def validate(raw, session, frame_check=None):
    """Keep only beliefs that stand on the facts shown. Returns (kept [{topic, text, cites}], discarded [reason])."""
    allowed = {ev["event_id"] for ev in session["events"]}
    kept, discarded, topics = [], [], set()
    items = raw.get("beliefs") if isinstance(raw, dict) else None
    if not isinstance(items, list):
        return [], ["not a list"]
    for item in items:
        if len(kept) >= MAX_BELIEFS:
            discarded.append("over the limit")
            continue
        if not isinstance(item, dict):
            discarded.append("not an object")
            continue
        topic = item.get("topic")
        if topic not in TOPICS:
            discarded.append("unknown topic")
            continue
        if topic in topics:
            discarded.append("topic repeated")
            continue
        text = " ".join(memory.clean_text(item.get("belief") or "", 600).split()).strip().strip('"').strip()
        text = EVENT_ID.sub("", ID_LIST.sub("", text)).replace("  ", " ").strip()
        text = re.sub(r"\s+([,.;!?])", r"\1", text).rstrip(" ,;").strip()
        if not MIN_BELIEF_CHARS <= len(text) <= MAX_BELIEF_CHARS:
            discarded.append("bad length")
            continue
        if frame_check and frame_check(text):
            discarded.append("breaks frame")
            continue
        cites = item.get("cites")
        if not isinstance(cites, list) or not cites:
            discarded.append("cites nothing")
            continue
        ids = []
        for c in cites[:MAX_CITES + 1]:
            m = EVENT_ID.fullmatch(str(c).strip())
            ids.append(m.group(1) if m else None)
        if len(cites) > MAX_CITES or any(i not in allowed for i in ids):
            discarded.append("cites something it was not shown")
            continue
        topics.add(topic)
        kept.append({"topic": topic, "text": text, "cites": sorted(set(ids))})
    return kept, discarded


def _store_reflection(self, session, kept, discarded, mode, model, note=None):
    """An event is reflected on once, ever. Returns (reflection_id or None if already done, beliefs stored)."""
    npc, char = session["npc_id"], session["char_id"]
    inputs = [e["event_id"] for e in session["events"]]
    with self.lock:
        self.db.execute("BEGIN IMMEDIATE")
        try:
            marks = ",".join("?" * len(inputs))
            if self.db.execute(f"SELECT 1 FROM reflection_inputs WHERE event_id IN ({marks}) LIMIT 1", inputs).fetchone():
                self.db.execute("COMMIT")
                return None, 0
            rid = self.db.execute(
                """INSERT INTO reflections (npc_id, char_id, upto_epoch, upto_seq, at_utc, mode, model,
                   prompt_version, events, kept, discarded, note) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 0, 0, ?)""",
                (npc, char, session["upto"][0], session["upto"][1], memory.now_utc(), mode, model, PROMPT_VERSION,
                 len(inputs), note)).lastrowid
            self.db.executemany("INSERT INTO reflection_inputs (event_id, reflection_id) VALUES (?, ?)",
                                [(e, rid) for e in inputs])
            stored, dropped = 0, 0
            for b in kept:
                marks = ",".join("?" * len(b["cites"]))
                live = self.db.execute(
                    f"""SELECT COUNT(*) FROM events WHERE event_id IN ({marks}) AND status = 'live'
                        AND npc_id = ? AND char_id = ?""", (*b["cites"], npc, char)).fetchone()[0]
                if live != len(b["cites"]):   # rolled back while the model was thinking
                    dropped += 1
                    continue
                bid = self.db.execute(
                    "INSERT INTO beliefs (reflection_id, npc_id, char_id, topic, text, at_utc) VALUES (?, ?, ?, ?, ?, ?)",
                    (rid, npc, char, b["topic"], b["text"], memory.now_utc())).lastrowid
                self.db.executemany("INSERT INTO belief_cites (belief_id, event_id) VALUES (?, ?)",
                                    [(bid, e) for e in b["cites"]])
                stored += 1
            self.db.execute("UPDATE reflections SET kept = ?, discarded = ? WHERE id = ?",
                            (stored, len(discarded) + dropped, rid))
            self.db.execute("COMMIT")
        except Exception:
            self.db.in_transaction and self.db.execute("ROLLBACK")
            raise
    return rid, stored


def _valid(a, reviewed_only):
    """SQL: belief `a` still stands - character not deleted, every cited event live (and, for prompts, reviewed)."""
    sql = (f"{a}.char_id NOT IN ({memory.TOMBSTONED}) AND NOT EXISTS (SELECT 1 FROM belief_cites c "
           f"JOIN events e ON e.event_id = c.event_id WHERE c.belief_id = {a}.id AND e.status != 'live')")
    if reviewed_only:
        sql += (f" AND EXISTS (SELECT 1 FROM reflections r WHERE r.id = {a}.reflection_id "
                "AND (r.reviewed = 1 OR r.mode = 'live'))")
    return sql


def _live_sql(reviewed_only=False):
    """The newest valid belief per pair and topic. Decided when asked, never stored, so a rollback that kills a
    newer belief lets the older one count again."""
    return (f"FROM beliefs b WHERE {_valid('b', reviewed_only)} AND NOT EXISTS (SELECT 1 FROM beliefs n "
            "WHERE n.npc_id = b.npc_id AND n.char_id = b.char_id AND n.topic = b.topic AND n.id > b.id "
            f"AND {_valid('n', reviewed_only)})")


def _beliefs(self, npc_id, char_id, limit=2, reviewed_only=False):
    """Live beliefs for a pair, newest first. `reviewed_only` (what a prompt gets): only beliefs written in live
    mode or from a reflection marked reviewed - nothing made in shadow mode goes live by a config change alone."""
    with self.rlock:
        rows = self.rdb.execute(
            f"SELECT b.id, b.topic, b.text {_live_sql(reviewed_only)} AND b.npc_id = ? AND b.char_id = ? "
            "ORDER BY b.id DESC LIMIT ?", (npc_id, char_id, int(limit))).fetchall()
    return [dict(r) for r in rows]


def _mark_reviewed(self, reflection_ids):
    with self.lock:
        self.db.executemany("UPDATE reflections SET reviewed = 1 WHERE id = ?", [(int(i),) for i in reflection_ids])


def _requeue_gave_up(self):
    """Put batches the model never answered properly back in the queue (after fixing whatever cut it off).
    The reflection row stays, marked; its events become unreflected again. Returns how many events."""
    with self.lock:
        self.db.execute("BEGIN IMMEDIATE")
        try:
            n = self.db.execute("DELETE FROM reflection_inputs WHERE reflection_id IN "
                                "(SELECT id FROM reflections WHERE note = ?)", (GAVE_UP,)).rowcount
            # The row no longer covers any event: move its "up to" mark out of the way (0, -id) so the batch's
            # next, proper reflection can take the same mark (they are unique per pair).
            self.db.execute("UPDATE reflections SET note = ?, upto_epoch = 0, upto_seq = -id WHERE note = ?",
                            (f"requeued {memory.now_utc()} (was: {GAVE_UP})", GAVE_UP))
            self.db.execute("COMMIT")
        except Exception:
            self.db.in_transaction and self.db.execute("ROLLBACK")
            raise
    return n


def _reflection_status(self):
    with self.lock:
        n = self.db.execute("SELECT COUNT(*), COALESCE(SUM(kept), 0), COALESCE(SUM(discarded), 0), MAX(at_utc), "
                            "COALESCE(SUM(reviewed), 0) FROM reflections").fetchone()
        live = self.db.execute(f"SELECT COUNT(*) {_live_sql()}").fetchone()[0]
        dead = self.db.execute("SELECT COUNT(*), MIN(at_utc) FROM reflections WHERE note = ?", (GAVE_UP,)).fetchone()
        waiting = self.db.execute(
            f"""SELECT COUNT(*) FROM events WHERE status = 'live' AND kind NOT IN ({",".join("?" * len(SKIP_KINDS))})
                AND event_id NOT IN (SELECT event_id FROM reflection_inputs)""", SKIP_KINDS).fetchone()[0]
    return {"reflections": n[0], "reflections_reviewed": n[4], "beliefs_kept": n[1], "beliefs_discarded": n[2],
            "beliefs_live": live, "facts_waiting": waiting, "last_reflection_utc": n[3],
            "gave_up": dead[0], "oldest_gave_up_utc": dead[1]}


def reflect_once(ledger, session, ask_model, persona_text="", mode="shadow", model="", frame_check=None, now=None,
                 give_up=False):
    """Reflect on one batch. `ask_model(system, user, schema)` returns the parsed JSON, None if the answer was cut
    off or malformed, or raises if the model could not be reached. Only a proper answer (an empty list included)
    uses the batch up; otherwise nothing is stored and UnusableAnswer / the error is raised for a later try -
    unless `give_up`, when the batch is closed with no beliefs so one bad batch cannot block the rest for ever.
    Returns a summary for the log (ids, topics, counts - no text)."""
    system, user = build_prompt(session, persona_text, now)
    raw = ask_model(system, user, SCHEMA)
    note = None
    if not isinstance(raw, dict) or not isinstance(raw.get("beliefs"), list):
        if not give_up:
            raise UnusableAnswer("not the JSON asked for")
        raw, note = {"beliefs": []}, GAVE_UP   # a dead letter, not a reflection: counted apart, can be requeued
    kept, discarded = validate(raw, session, frame_check)
    rid, stored = ledger.store_reflection(session, kept, discarded, mode, model, note)
    return {"outcome": "duplicate" if rid is None else ("gave_up" if note else "ok"), "reflection": rid,
            "events": len(session["events"]), "kept": stored, "topics": [b["topic"] for b in kept],
            "cites": [b["cites"] for b in kept], "discarded": discarded,
            "upto": f"{session['upto'][0]}-{session['upto'][1]}"}


def prompt_block(beliefs):
    """Live mode only (never called in shadow mode)."""
    if not beliefs:
        return ""
    return ("What you have come to believe about them (your own conclusions, not proven fact; let them colour how you "
            "speak - never recite them):\n- " + "\n- ".join(b["text"] for b in beliefs))


memory.Ledger.note_persona = _note_persona
memory.Ledger.pending_reflections = _pending_reflections
memory.Ledger.store_reflection = _store_reflection
memory.Ledger.beliefs = _beliefs
memory.Ledger.mark_reviewed = _mark_reviewed
memory.Ledger.requeue_gave_up = _requeue_gave_up
memory.Ledger.reflection_status = _reflection_status
