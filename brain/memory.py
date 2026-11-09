"""Avallone Phase 4a1 - the memory ledger (design v2, Projects/Avallone-AI-NPC-Plan.md).

Append-only record of what happened between a companion and a character, sent by the shard. The shard
numbers every event (epoch, seq) and each world save records the last one it contains (the high-water
mark). On every shard start the shard asks us to reconcile: events newer than the loaded save are marked
rolled_back - kept for audit, never used - so a restored world never leaves a companion remembering
things that, in that world, never happened.

Rows are never deleted or edited apart from status. Anything that doesn't reconcile switches memory off
(the shard is told why) rather than guessing.
"""
import json
import re
import sqlite3
import threading
import unicodedata
from datetime import datetime, timezone

SCHEMA_VERSION = 1   # event schema the shard sends
DB_VERSION = 5       # 5 = events_npc_kind index, 5c-2 2c (mood reads one NPC across all characters);
                    # PRAGMA user_version (2 = full-text index + npc remaps, 4a2; 3 = reflections + beliefs, 4c2;
                    # 4 = legacy_marks, 5c-2 2a)

KINDS = {
    "joined", "sent_home", "left_over_cruelty", "wounded", "owner_died", "owner_resurrected",
    "criminal_act", "murder", "fight_survived", "trust_band_changed", "region_entered", "conversation",
    "character_deleted", "secret_revealed", "turning_point", "turning_point_revoked", "session_summary",
    "gear_given", "gear_returned", "gear_refused",  # 4e companion gear
    "deed",  # Phase 5a: a public deed, npc_id = the world id; read by rumours.py
    "visit_summary", "visited",  # Phase 5c: a named NPC's claims-only visit notes, and one row per visit day
    "reply_delivered",           # 5c-2 2a: which memory cards a delivered named-NPC reply was offered (ids only, no text)
    "traded",                    # 5c-2 2b: a real vendor transfer, at most one per NPC + character + Central day
    "gift_given",                # 5c-2 2b: an accepted tip or liked gift, at most one per NPC + character + Central day
    "player_used_hateful_language",  # 5c-2 2b (B4): a line on the narrow slur list, once per NPC + character + day, no text
}
ITEM = re.compile(r"^((?:pet|petfix)\|[a-z]+( [a-z]+)?\|[A-Z][A-Za-z'\-]{1,15}|trade\|[a-z]+(?: and [a-z]+)?|home\|[A-Z][A-Za-z' ]{1,20}|request\|[^|]{3,90})$")
CARD_ID = re.compile(r"^[a-z]:[A-Za-z0-9:_\-]{1,40}$")
VISIT_DAY = re.compile(r"^\d{4}-\d{2}-\d{2}$")

# Phase 4a2 retrieval. Kinds that matter most: one of these is always in the prompt if there is one.
BIG_KINDS = ("owner_died", "owner_resurrected", "wounded", "left_over_cruelty", "murder", "criminal_act",
             "trust_band_changed", "secret_revealed", "turning_point")
IMPORTANCE = {"owner_died": 5, "owner_resurrected": 5, "left_over_cruelty": 5, "murder": 5, "wounded": 4,
              "criminal_act": 4, "trust_band_changed": 4, "joined": 3, "sent_home": 2, "fight_survived": 2,
              "region_entered": 1, "conversation": 1, "secret_revealed": 5, "turning_point": 5,
              "turning_point_revoked": 0, "session_summary": 3,
              "gear_given": 3, "gear_returned": 2, "gear_refused": 2, "visit_summary": 2, "visited": 0,
              "reply_delivered": 0, "traded": 0, "gift_given": 0, "player_used_hateful_language": 0}
STOPWORDS = set("""a an the and or but if then so to of in on at for with from by about as is are was were be been am
do does did have has had you your yours me my mine we our us it its this that these those there here what when where who
why how can could would should will shall may might must not no yes i i'm im you're youre it's its let's lets just
really very too also only some any all one ok okay oh hey hi hello please thanks thank remember recall again""".split())
# Small, game-shaped synonym sets so "when I died" finds "was killed". Lexical retrieval only (no embeddings, v2 #2).
SYNONYMS = [
    {"die", "died", "dead", "death", "killed", "kill", "slain", "fell"},
    {"fight", "fought", "battle", "fighting", "attacked", "combat"},
    {"hurt", "wounded", "injured", "bleeding", "carried"},
    {"home", "sent", "leave", "left"},
    {"crime", "criminal", "stole", "steal", "murder", "murdered", "innocent", "cruel", "cruelty"},
    {"travel", "travelled", "traveled", "journey", "trip", "went", "visited"},
]

# A character counts as deleted only while its character_deleted event is live: a world restore that rolls the
# deletion back brings the character's memories back with it.
TOMBSTONED = ("SELECT t.char_id FROM tombstones t JOIN events te ON te.event_id = t.event_id "
              "WHERE te.status = 'live'")

MAX_FACTS_CHARS = 3000
MAX_TEXT_CHARS = 600


def now_utc():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def clean_text(value, limit=MAX_TEXT_CHARS):
    """Untrusted display text: no control/format characters (bidi, zero-width), bounded length."""
    s = "".join(ch for ch in str(value) if unicodedata.category(ch)[0] != "C" or ch == " ")
    return s[:limit]


# Phase 5a: a deed's facts, checked on the way in (defence in depth behind the shard's own gate). Unknown keys, wrong
# types and anything but plain words in the word fields reject the event. Only GM plants carry free text.
DEED_TYPES = {"kill_notable", "tame_notable", "died_notable", "murder", "died_pathetic", "tame_failstreak", "fished_boots",
              "fished_bottle", "drunk", "provoked", "pacified", "pet_died", "returned", "gm_planted"}
DEED_INTS = {"count", "x", "y", "pt"}
DEED_WORDS = {"subject", "who", "descriptor", "place", "other", "town"}
DEED_MAPS = {"Felucca", "Trammel", "Ilshenar", "Malas", "Tokuno"}
_WORD = re.compile(r"^(|[A-Za-z][A-Za-z' \-]{0,39})$")


def check_deed(facts):
    if not isinstance(facts, dict):
        raise ValueError("deed facts must be an object")
    allowed = {"type", "map", "by_pet", "text", "unease"} | DEED_INTS | DEED_WORDS
    extra = set(facts) - allowed
    if extra:
        raise ValueError(f"deed: unknown fact {sorted(extra)[0][:30]!r}")
    if facts.get("type") not in DEED_TYPES:
        raise ValueError("deed: unknown type")
    if facts.get("map") not in DEED_MAPS:
        raise ValueError("deed: unsupported map")
    for k in DEED_INTS:
        v = facts.get(k, 0)
        if isinstance(v, bool) or not isinstance(v, int) or not -1 < v < 10 ** 12:
            raise ValueError(f"deed: {k} must be a whole number")
    for k in DEED_WORDS:
        if not isinstance(facts.get(k, ""), str) or not _WORD.match(facts.get(k, "")):
            raise ValueError(f"deed: {k} is not a plain word")
    if "by_pet" in facts and not isinstance(facts["by_pet"], bool):
        raise ValueError("deed: by_pet must be true/false")
    if "text" in facts and facts["type"] != "gm_planted":
        raise ValueError("deed: only GM plants carry text")
    if "unease" in facts:   # 2b (B3): a GM plant's own weight on town unease, -1 (reassuring) .. 1 (unsettling)
        u = facts["unease"]
        if facts["type"] != "gm_planted" or isinstance(u, bool) or not isinstance(u, (int, float)) or not -1 <= u <= 1:
            raise ValueError("deed: unease is a GM plant's number from -1 to 1")


def clean_facts(facts):
    if not isinstance(facts, dict):
        raise ValueError("facts must be an object")
    out = {}
    for k, v in list(facts.items())[:20]:
        key = clean_text(k, 40)
        if isinstance(v, bool) or isinstance(v, (int, float)) or v is None:
            out[key] = v
        elif isinstance(v, list) and len(v) <= 10 and all(isinstance(x, str) for x in v):
            out[key] = [clean_text(x) for x in v]   # Phase 5c visit notes: short lists of strings stay lists
        else:
            out[key] = clean_text(v)
    text = json.dumps(out, ensure_ascii=False)
    if len(text) > MAX_FACTS_CHARS:
        raise ValueError("facts too long")
    return text


class Ledger:
    def __init__(self, path):
        self.path = path
        self.lock = threading.Lock()   # one writer: the brain is a ThreadingHTTPServer
        self.db = sqlite3.connect(path, check_same_thread=False, isolation_level=None, timeout=5)
        self.db.row_factory = sqlite3.Row
        for pragma in ("journal_mode=WAL", "synchronous=FULL", "foreign_keys=ON", "busy_timeout=5000"):
            self.db.execute(f"PRAGMA {pragma}")
        self._migrate()
        # Reply-path reads (history, retrieve, beliefs). WAL: a reader never waits on a writer. Opened after _migrate
        # so the schema exists. 4d soak 2026-10-20: a reply waited 4.7 s behind an ingest stuck on a lock.
        self.rlock = threading.Lock()
        self.rdb = sqlite3.connect(path, check_same_thread=False, isolation_level=None, timeout=5)
        self.rdb.row_factory = sqlite3.Row
        for pragma in ("busy_timeout=1000", "query_only=ON"):
            self.rdb.execute(f"PRAGMA {pragma}")

    def _migrate(self):
        version = self.db.execute("PRAGMA user_version").fetchone()[0]
        if version > DB_VERSION:
            raise RuntimeError(f"memory db is version {version}, this brain knows {DB_VERSION}")
        if version < 1:
            with self.lock:
                self.db.execute("BEGIN IMMEDIATE")
                for stmt in (
                    """CREATE TABLE events (
                        event_id TEXT PRIMARY KEY,
                        epoch INTEGER NOT NULL,
                        seq INTEGER NOT NULL,
                        npc_id TEXT NOT NULL,
                        char_id TEXT NOT NULL,
                        kind TEXT NOT NULL,
                        facts TEXT NOT NULL,
                        at_utc TEXT NOT NULL,
                        schema INTEGER NOT NULL,
                        status TEXT NOT NULL DEFAULT 'live' CHECK (status IN ('live', 'rolled_back')),
                        received_utc TEXT NOT NULL,
                        UNIQUE (epoch, seq))""",
                    "CREATE INDEX events_pair ON events (npc_id, char_id, status, epoch, seq)",
                    "CREATE INDEX events_kind ON events (kind)",
                    """CREATE TABLE reconciliations (
                        id INTEGER PRIMARY KEY,
                        at_utc TEXT NOT NULL,
                        new_epoch INTEGER NOT NULL,
                        saved_epoch INTEGER NOT NULL,
                        saved_seq INTEGER NOT NULL,
                        verdict TEXT NOT NULL,
                        rolled_back INTEGER NOT NULL,
                        reason TEXT)""",
                    """CREATE TABLE rejected (
                        id INTEGER PRIMARY KEY,
                        at_utc TEXT NOT NULL,
                        event_id TEXT,
                        reason TEXT NOT NULL)""",
                    """CREATE TABLE tombstones (
                        char_id TEXT PRIMARY KEY,
                        at_utc TEXT NOT NULL,
                        event_id TEXT NOT NULL)""",
                ):
                    self.db.execute(stmt)
                self.db.execute("PRAGMA user_version = 1")
                self.db.execute("COMMIT")
            version = 1
        if version < 2:
            with self.lock:
                self.db.execute("BEGIN IMMEDIATE")
                self.db.execute("CREATE VIRTUAL TABLE events_fts USING fts5(event_id UNINDEXED, body, tokenize='porter')")
                for r in self.db.execute("SELECT event_id, kind, facts FROM events").fetchall():
                    self.db.execute("INSERT INTO events_fts (event_id, body) VALUES (?, ?)",
                                    (r["event_id"], fts_body(r["kind"], json.loads(r["facts"]))))
                self.db.execute("""CREATE TABLE npc_remaps (
                    id INTEGER PRIMARY KEY, at_utc TEXT NOT NULL, old_id TEXT NOT NULL, new_id TEXT NOT NULL,
                    rows INTEGER NOT NULL, reason TEXT NOT NULL)""")
                self.db.execute("PRAGMA user_version = 2")
                self.db.execute("COMMIT")
            version = 2
        if version < 3:
            import reflection   # Phase 4c2: the tables live with their code (reflection imports this module)
            with self.lock:
                self.db.execute("BEGIN IMMEDIATE")
                try:
                    for stmt in reflection.MIGRATION_V3:
                        self.db.execute(stmt)
                    self.db.execute("PRAGMA user_version = 3")
                    self.db.execute("COMMIT")
                except Exception:
                    self.db.in_transaction and self.db.execute("ROLLBACK")
                    raise
            version = 3
        if version < 4:
            # 5c-2 2a: one marker per world branch giving old 5c-1 notes (no memory-clock time) a fixed age baseline.
            # epoch/seq = the ledger position when it was taken, so a world restore to before it removes it too.
            with self.lock:
                self.db.execute("BEGIN IMMEDIATE")
                try:
                    self.db.execute("""CREATE TABLE IF NOT EXISTS legacy_marks (
                        id INTEGER PRIMARY KEY, epoch INTEGER NOT NULL, seq INTEGER NOT NULL, mpt REAL NOT NULL,
                        at_utc TEXT NOT NULL)""")
                    self.db.execute("PRAGMA user_version = 4")
                    self.db.execute("COMMIT")
                except Exception:
                    self.db.in_transaction and self.db.execute("ROLLBACK")
                    raise
            version = 4
        if version < 5:
            # 5c-2 2c (Codex 2c design review F1): mood reads ONE NPC across every character (gifts, visits today);
            # events_pair leads with npc_id but then char_id, and the planner picked events_kind. Rebuildable, no data.
            with self.lock:
                self.db.execute("BEGIN IMMEDIATE")
                try:
                    self.db.execute("CREATE INDEX IF NOT EXISTS events_npc_kind ON events (npc_id, kind, status)")
                    self.db.execute("PRAGMA user_version = 5")
                    self.db.execute("COMMIT")
                except Exception:
                    self.db.in_transaction and self.db.execute("ROLLBACK")
                    raise
        import reflection
        reflection.check_schema(self.db)

    # ---- writes -------------------------------------------------------------------------------------

    def _rolled_back_by_history(self, epoch, seq):
        """An event arriving late whose branch an earlier reconciliation already cut off."""
        row = self.db.execute(
            """SELECT 1 FROM reconciliations WHERE verdict IN ('ok', 'fresh') AND new_epoch > ?
               AND (? > saved_epoch OR (? = saved_epoch AND ? > saved_seq)) LIMIT 1""",
            (epoch, epoch, epoch, seq),
        ).fetchone()
        return row is not None

    def ingest(self, events):
        """Store a batch. Returns (acked, rejected): both lists of event ids the shard may drop from its outbox.
        Duplicates are acked (idempotent); invalid events are rejected and logged, never stored."""
        acked, rejected = [], []
        with self.lock:
            self.db.execute("BEGIN IMMEDIATE")
            try:
                for ev in events[:200]:
                    eid = str(ev.get("event_id", ""))[:40] if isinstance(ev, dict) else ""
                    try:
                        if not eid:
                            raise ValueError("no event_id")
                        epoch, seq = int(ev["epoch"]), int(ev["seq"])
                        if epoch < 1 or seq < 1 or eid != f"{epoch}-{seq}":
                            raise ValueError("bad epoch/seq/event_id")
                        kind = str(ev["kind"])
                        if kind not in KINDS:
                            raise ValueError(f"unknown kind {kind[:30]!r}")
                        if int(ev.get("schema", 0)) != SCHEMA_VERSION:
                            raise ValueError(f"schema {ev.get('schema')} != {SCHEMA_VERSION}")
                        npc, char = str(ev["npc_id"])[:36], str(ev["char_id"])[:36]
                        if kind == "deed":
                            check_deed(ev.get("facts"))
                        if kind in ("visit_summary", "visited", "reply_delivered", "traded", "gift_given",
                                    "player_used_hateful_language"):
                            check_named(kind, ev.get("facts"))
                        facts = clean_facts(ev.get("facts") or {})
                        at = clean_text(ev.get("at_utc", ""), 30)
                    except (KeyError, TypeError, ValueError) as e:
                        rejected.append(eid)
                        self.db.execute("INSERT INTO rejected (at_utc, event_id, reason) VALUES (?, ?, ?)",
                                        (now_utc(), eid, clean_text(e, 200)))
                        continue
                    status = "rolled_back" if self._rolled_back_by_history(epoch, seq) else "live"
                    before = self.db.total_changes
                    self.db.execute(
                        """INSERT OR IGNORE INTO events
                           (event_id, epoch, seq, npc_id, char_id, kind, facts, at_utc, schema, status, received_utc)
                           VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                        (eid, epoch, seq, npc, char, kind, facts, at, SCHEMA_VERSION, status, now_utc()),
                    )
                    if self.db.total_changes != before:
                        self.db.execute("INSERT INTO events_fts (event_id, body) VALUES (?, ?)",
                                        (eid, fts_body(kind, json.loads(facts))))
                    if kind == "character_deleted" and status == "live":
                        self.db.execute("INSERT OR REPLACE INTO tombstones (char_id, at_utc, event_id) VALUES (?, ?, ?)",
                                        (char, now_utc(), eid))
                    acked.append(eid)
                self.db.execute("COMMIT")
            except Exception:
                self.db.in_transaction and self.db.execute("ROLLBACK")
                raise
        return acked, rejected

    def reconciled(self, epoch):
        """Codex 2b code review F1: True only if the LATEST reconciliation is a success for this shard epoch - so reads
        that age or count rows (relationship dials) never use rows a restored world has not reconciled yet."""
        try:
            row = self.rdb.execute("SELECT new_epoch, verdict FROM reconciliations ORDER BY rowid DESC LIMIT 1").fetchone()
        except Exception:
            return False
        return bool(row) and int(row[0]) == int(epoch) and row[1] in ("ok", "fresh")

    def reconcile(self, new_epoch, saved_epoch, saved_seq, outbox_min_seq=None):
        """Called once per shard start, before any new event. Returns (verdict, rolled_back, reason).
        verdict: 'fresh' (empty db), 'ok', or 'conflict' (shard must switch memory off)."""
        new_epoch, saved_epoch, saved_seq = int(new_epoch), int(saved_epoch), int(saved_seq)
        with self.lock:
            self.db.execute("BEGIN IMMEDIATE")
            try:
                verdict, rolled, reason = self._reconcile(new_epoch, saved_epoch, saved_seq, outbox_min_seq)
                self.db.execute(
                    """INSERT INTO reconciliations (at_utc, new_epoch, saved_epoch, saved_seq, verdict, rolled_back, reason)
                       VALUES (?, ?, ?, ?, ?, ?, ?)""",
                    (now_utc(), new_epoch, saved_epoch, saved_seq, verdict, rolled, reason),
                )
                self.db.execute("COMMIT")
            except Exception:
                self.db.in_transaction and self.db.execute("ROLLBACK")
                raise
        return verdict, rolled, reason

    def _reconcile(self, new_epoch, saved_epoch, saved_seq, outbox_min_seq):
        if new_epoch <= saved_epoch:
            return "conflict", 0, f"new epoch {new_epoch} is not after the saved epoch {saved_epoch}"
        total = self.db.execute("SELECT COUNT(*) FROM events").fetchone()[0]
        if total == 0:
            return "fresh", 0, "memory db is empty - any older memories are gone; starting fresh"
        max_epoch = self.db.execute("SELECT MAX(epoch) FROM events").fetchone()[0]
        if max_epoch >= new_epoch:
            return "conflict", 0, (f"db holds events from epoch {max_epoch}, not before the new epoch {new_epoch} - "
                                   "the shard's epoch counter went backwards (file lost or restored?)")
        if saved_seq > 0:
            have = self.db.execute("SELECT COALESCE(MAX(seq), 0) FROM events WHERE epoch = ?",
                                   (saved_epoch,)).fetchone()[0]
            need = saved_seq if outbox_min_seq is None else int(outbox_min_seq) - 1
            if have < need:
                return "conflict", 0, (f"db is missing events {have + 1}..{need} of epoch {saved_epoch} that the "
                                       "world save contains - memory db older than the world?")
        cur = self.db.execute(
            """UPDATE events SET status = 'rolled_back'
               WHERE status = 'live' AND ((epoch > ? AND epoch < ?) OR (epoch = ? AND seq > ?))""",
            (saved_epoch, new_epoch, saved_epoch, saved_seq),
        )
        # 5c-2 2a (Codex round 2 F2): a legacy marker taken after the restored point belongs to a discarded future.
        self.db.execute("""DELETE FROM legacy_marks WHERE (epoch > ? AND epoch < ?) OR (epoch = ? AND seq > ?)""",
                        (saved_epoch, new_epoch, saved_epoch, saved_seq))
        return "ok", cur.rowcount, None

    # ---- reads --------------------------------------------------------------------------------------

    def status(self, npc_id=None, char_id=None):
        where, args = [], []
        if npc_id:
            where.append("npc_id = ?")
            args.append(npc_id)
        if char_id:
            where.append("char_id = ?")
            args.append(char_id)
        clause = ("WHERE " + " AND ".join(where)) if where else ""
        with self.lock:
            counts = {r["status"]: r["n"] for r in self.db.execute(
                f"SELECT status, COUNT(*) n FROM events {clause} GROUP BY status", args)}
            kinds = {r["kind"]: r["n"] for r in self.db.execute(
                f"SELECT kind, COUNT(*) n FROM events {clause} {'AND' if where else 'WHERE'} status = 'live' "
                "GROUP BY kind ORDER BY n DESC", args)}
            last = self.db.execute(f"SELECT MAX(at_utc) FROM events {clause}", args).fetchone()[0]
            rec = self.db.execute(
                "SELECT at_utc, new_epoch, saved_epoch, saved_seq, verdict, rolled_back, reason "
                "FROM reconciliations ORDER BY id DESC LIMIT 1").fetchone()
            rejected = self.db.execute("SELECT COUNT(*) FROM rejected").fetchone()[0]
            tomb = self.db.execute(f"SELECT COUNT(*) FROM ({TOMBSTONED})").fetchone()[0]
        return {
            "live": counts.get("live", 0),
            "rolled_back": counts.get("rolled_back", 0),
            "kinds": kinds,
            "last_event_utc": last,
            "last_reconcile": dict(rec) if rec else None,
            "rejected": rejected,
            "tombstoned_characters": tomb,
        }

    def recent(self, npc_id, char_id, limit=8):
        """Live events for a pair, newest first; nothing for a tombstoned character. (4a2 builds retrieval on this.)"""
        with self.lock:
            if self.db.execute(f"SELECT 1 FROM ({TOMBSTONED}) WHERE char_id = ?", (char_id,)).fetchone():
                return []
            rows = self.db.execute(
                """SELECT event_id, kind, facts, at_utc FROM events
                   WHERE npc_id = ? AND char_id = ? AND status = 'live'
                   ORDER BY epoch DESC, seq DESC LIMIT ?""",
                (npc_id, char_id, int(limit)),
            ).fetchall()
        return [{**dict(r), "facts": json.loads(r["facts"])} for r in rows]

    def backup(self, dest):
        """Consistent copy while live (VACUUM INTO), for the pre-deploy backup step."""
        with self.lock:
            self.db.execute("VACUUM INTO ?", (dest,))


# ---- Phase 4a2: what she remembers, as prompt lines -----------------------------------------------------

def check_named(kind, facts):
    """Phase 5c: the shard sends only these shapes; anything else is rejected at the door."""
    if not isinstance(facts, dict):
        raise ValueError("facts not an object")
    if kind == "gift_given":   # 2b: exactly {day, mpt, kind: gold|liked, value: small|fair|generous} - no item names, no text
        # 2c (design M7.3): + pt, the played clock at the gift (mood fades a gift over played hours). Optional on read -
        # older rows have none and give no mood nudge; the 2c shard always sends it.
        if set(facts) - {"pt"} != {"day", "mpt", "kind", "value"} or not VISIT_DAY.match(str(facts["day"])) or \
                facts["kind"] not in ("gold", "liked") or facts["value"] not in ("small", "fair", "generous"):
            raise ValueError("gift_given needs exactly {day, mpt, kind, value} and at most pt")
        if "pt" in facts:
            _check_clocks(facts)
        m = facts["mpt"]
        if isinstance(m, bool) or not isinstance(m, (int, float)) or m != m or m < 0 or m == float("inf"):
            raise ValueError("gift_given mpt malformed")
        return
    if kind in ("visited", "traded", "player_used_hateful_language"):   # 2b: same shape {day, mpt?}; never any text
        if "day" not in facts or set(facts) - {"day", "mpt"} or not VISIT_DAY.match(str(facts["day"])):
            raise ValueError(f"{kind} needs {{day: YYYY-MM-DD}} and at most mpt")
        m = facts.get("mpt", 0)
        if isinstance(m, bool) or not isinstance(m, (int, float)) or m != m or m < 0 or m == float("inf"):
            raise ValueError(f"{kind} mpt malformed")
        return
    if kind == "reply_delivered":
        if set(facts) - {"cards", "pt", "mpt"} or not isinstance(facts.get("cards"), list):
            raise ValueError("reply_delivered needs {cards, pt, mpt}")
        if len(facts["cards"]) > 6 or any(not isinstance(x, str) or not CARD_ID.match(x) for x in facts["cards"]):
            raise ValueError("reply_delivered cards malformed")
        _check_clocks(facts)
        return
    if set(facts) - {"claims", "topics", "items", "pt", "mpt", "visit"}:
        raise ValueError("visit_summary has unexpected fields")
    if "items" in facts:
        v = facts["items"]
        if not isinstance(v, list) or len(v) > 6 or any(not isinstance(x, str) or not ITEM.match(x) for x in v):
            raise ValueError("visit_summary items malformed")
    if "visit" in facts and not re.match(r"^[0-9a-f]{32}$", str(facts["visit"])):
        raise ValueError("visit_summary visit id malformed")
    if "pt" in facts or "mpt" in facts:
        _check_clocks(facts)
    for k, n, size in (("claims", 3, 120), ("topics", 3, 40)):
        v = facts.get(k, [])
        if not isinstance(v, list) or len(v) > n or any(not isinstance(x, str) or len(x) > size for x in v):
            raise ValueError(f"visit_summary {k} malformed")


def _check_clocks(facts):
    """5c-2 2a: the shard's played clock (int seconds) and memory clock (memory-seconds, may be fractional)."""
    import math
    pt, mpt = facts.get("pt"), facts.get("mpt")
    if isinstance(pt, bool) or not isinstance(pt, int) or pt < 0:
        raise ValueError("pt must be a non-negative integer")
    if isinstance(mpt, bool) or not isinstance(mpt, (int, float)) or not math.isfinite(mpt) or mpt < 0 or mpt > pt + 1:
        raise ValueError("mpt must be finite, >= 0 and not ahead of pt")


def fts_body(kind, facts):
    """Searchable text for an event: its words plus the kind, so "fight" finds fights with no text."""
    parts = [kind.replace("_", " ")]
    for k in ("text", "player_said", "she_said", "region", "foes", "from_band", "to_band", "reason", "places",
              "time_of_day"):
        if facts.get(k):
            parts.append(str(facts[k]))
    for k in ("claims", "topics"):   # Phase 5c visit notes
        if isinstance(facts.get(k), list):
            parts.extend(str(x) for x in facts[k])
    return " ".join(parts)


def query_terms(utterance):
    words = {w.strip("'").lower() for w in re.findall(r"[A-Za-z']+", utterance)}
    words = {w for w in words if len(w) >= 3 and w not in STOPWORDS}
    for group in SYNONYMS:
        if words & group:
            words |= group
    return sorted(words)[:24]


def when(at_utc, now):
    try:
        then = datetime.strptime(at_utc, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    except ValueError:
        return "Some time ago"
    days = (now.date() - then.date()).days
    if (now - then).total_seconds() < 600:
        return "Just now"
    if days <= 0:
        return "Earlier today"
    if days == 1:
        return "Yesterday"
    if days < 14:
        return f"{days} days ago"
    return f"{days // 7} weeks ago"


TIMES_OF_DAY = ("morning", "afternoon", "evening", "late at night")


def _num(v):
    return int(v) if isinstance(v, (int, float)) and not isinstance(v, bool) else None


def _times(n):
    return "once" if n == 1 else "twice" if n == 2 else f"{n} times"


def _duration(mins):
    if mins < 50:
        return f"{max(1, mins)} minutes"
    hours = round(mins / 60 * 2) / 2
    return "an hour" if hours == 1 else f"{hours:g} hours"


# §21 memory filter (the owner, chat, 2026-11-06: an NPC may go along with a debt in the moment, but it must not carry into a
# later conversation). Any exchange or note that touches money is left out of everything recalled from the ledger.
# Deliberately wide: a false match only means one small exchange is forgotten. Applied on READ, so rows already
# stored are covered and nothing is deleted. "promise" alone is NOT money ("you promised to keep watch" stays).
MONEY_WORDS = (
    "owe", "owed", "owes", "owing", "debt", "debts", "debtor", "indebted", "tab", "tabs", "iou",
    "loan", "loans", "loaned", "lend", "lends", "lent", "lending", "borrow", "borrows", "borrowed", "borrowing",
    "repay", "repays", "repaid", "pay", "pays", "paid", "paying", "payment", "payments", "payday", "payout",
    "wage", "wages", "fee", "fees", "reward", "rewards", "rewarded", "bounty", "bounties",
    "share", "shares", "split", "bet", "bets", "wager", "wagers", "wagered",
    "gold", "coin", "coins", "coppers", "copper", "silver", "crown", "crowns", "gp", "purse", "purses",
    "money", "riches", "treasure", "fortune", "cash", "earnings", "salary", "tip", "tips",
    # Codex R5-1 (2026-11-06): bills, accounts, balances, credit and settling up
    "bill", "bills", "billed", "unpaid", "underpaid", "overpaid", "prepaid", "overdue", "outstanding", "arrears", "dues",
    "account", "accounts", "balance", "balances", "credit", "credits", "credited", "settle", "settles", "settled",
    "settling", "settlement", "tally", "tallies", "reckoning", "ledger", "charge", "charges", "charged", "refund", "refunds",
    "refunded", "deposit", "rent", "price", "prices", "cost", "costs", "costed", "expense", "expenses", "compensation",
    # Codex GitHub-Publishing R5-1 (2026-11-07): whole word families, not single forms (repayment, reimburse, deposits...),
    # plus the other missing forms of words above. Still a finite list - best effort, not a guarantee (Codex).
    "repayment", "repayments", "repaying", "repayable", "reimburse", "reimburses", "reimbursed", "reimbursing",
    "reimbursement", "reimbursements", "deposits", "deposited", "depositing", "payed", "payable", "payer", "payers",
    "payee", "payees", "lender", "lenders", "borrower", "borrowers", "debtors", "loaning", "wagering", "betting",
    "tipped", "tipping", "rents", "rented", "renting", "rental", "rentals", "renter", "priced", "pricing", "pricey",
    "costly", "costing", "expensive", "charging", "refunding", "billing", "crediting", "creditor", "creditors",
    "earn", "earns", "earned", "earning", "profit", "profits", "compensate", "compensated", "compensating",
    "recompense", "restitution", "remuneration", "collateral", "pawn", "pawned", "ransom", "tithe", "tithes", "tax",
    "taxes", "toll", "tolls", "penny", "pennies", "pence", "shilling", "shillings", "currency", "invoice", "invoices",
    "receipt", "receipts",
)
MONEY_TALK = re.compile(r"(?<![\w])(?:" + "|".join(MONEY_WORDS) + r"|a\s+cut|cut\s+of)(?![\w])", re.IGNORECASE)


def money_talk(text):
    """True if this text touches money at all (wide on purpose - see MONEY_WORDS)."""
    return bool(MONEY_TALK.search(unicodedata.normalize("NFKC", str(text or ""))))


def conversation_money(facts):
    """A stored conversation exchange that touches money, on either side."""
    return money_talk((facts or {}).get("player_said")) or money_talk((facts or {}).get("she_said"))


def strip_money(facts):
    """A visit_summary's facts with every money claim, topic and request item removed (a copy; the row is untouched)."""
    if not isinstance(facts, dict):
        return facts
    out = dict(facts)
    for k in ("claims", "topics"):
        if isinstance(out.get(k), list):
            out[k] = [c for c in out[k] if not money_talk(c)]
    if isinstance(out.get("items"), list):
        out["items"] = [i for i in out["items"] if not money_talk(str(i).split("|", 1)[-1])]
    return out


def render(kind, facts):
    """One memory, in her own voice (second person, like the rest of her prompt)."""
    if kind == "conversation":
        return (f'You talked. They said: "{facts.get("player_said", "")}" (their words, not proven fact). '
                f'You answered: "{facts.get("she_said", "")}"')
    if kind == "fight_survived":
        foes = facts.get("foes")
        out = f"You survived a fight together{' against ' + foes if foes else ''}."
        # Richer facts (2026-10-06), all measured by the shard. Older events simply lack them.
        low = _num(facts.get("owner_low_pct"))
        if low is not None:
            if low < 25:
                out += " It nearly killed them."
            elif low < 50:
                out += " They were badly hurt in it."
            elif low >= 90:
                out += " They were barely scratched."
        if facts.get("started") == "owner":
            out += " They struck first."
        elif facts.get("started") == "foe":
            out += " It came at them first."
        healed = _num(facts.get("times_healed"))
        if healed:
            out += f" You bandaged them {_times(healed)} during it."
        hers = _num(facts.get("her_low_pct"))
        if hers is not None and hers < 50:
            out += " You were hurt yourself."
        if facts.get("broke_off") is True:
            out += " It ended with the foe still standing."
        return out
    if kind == "session_summary":
        mins = _num(facts.get("minutes_together")) or 0
        tod = facts.get("time_of_day")
        out = f"You travelled together for about {_duration(mins)}"
        out += f", {tod if tod == 'late at night' else 'in the ' + tod}." if tod in TIMES_OF_DAY else "."
        fights, close = _num(facts.get("fights")) or 0, _num(facts.get("close_calls")) or 0
        if fights:
            out += f" You came through {fights} fight{'s' if fights != 1 else ''}"
            out += f", {close} of them close." if close else ", none of them close."
        else:
            out += " There was no fighting."
        healed = _num(facts.get("times_healed"))
        if healed:
            out += f" You bandaged them {_times(healed)}."
        deaths = _num(facts.get("owner_deaths"))
        if deaths:
            out += f" They died {_times(deaths)}."
        if facts.get("places"):
            out += f" You went by way of {facts['places']}."
        return out
    if kind == "secret_revealed":
        return "You told them something about your past that you had never told anyone."
    if kind == "gear_given":
        worn = "carrying" if facts.get("worn") == "carrying" else "wearing"
        return f"They gave you {facts.get('item', 'something')}, and you have been {worn} it since."
    if kind == "gear_returned":
        return f"They asked for {facts.get('item', 'something')} back, and you gave it to them."
    if kind == "gear_refused":
        why = facts.get("reason")
        return f"They offered you {facts.get('item', 'something')} and you turned it down" + (f", because {why}." if why else ".")
    if kind == "region_entered":
        return f"You travelled together to {facts.get('region', 'somewhere new')}."
    if kind == "trust_band_changed":
        return f"Your feelings toward them moved from {facts.get('from_band')} to {facts.get('to_band')}."
    return str(facts.get("text") or kind.replace("_", " "))


def _retrieve(self, npc_id, char_id, utterance, skip_conversations=0, budget_chars=900, now=None,
              stable_first=False, matched_budget=None, records=False):
    """Union of fixed slots (design v2 #5): 1 pinned big event, up to 3 full-text matches, 2 newest significant
    events. The newest `skip_conversations` conversation events are already in the chat history and are skipped.
    stable_first (prompt layout v2): pinned and recent are taken before matched, so the lines that hold for the whole
    conversation stay the same turn to turn even when a line also matches; matched_budget then gives the matched
    lines their own budget (they ride in the small per-turn block) and budget_chars covers the stable ones.
    Returns (lines oldest-first, [(event_id, reason)]); records=True returns instead one list of dicts
    {line, event_id, reason, kind}, oldest first."""
    now = now or datetime.now(timezone.utc)
    with self.rlock:
        if self.rdb.execute(f"SELECT 1 FROM ({TOMBSTONED}) WHERE char_id = ?", (char_id,)).fetchone():
            return [] if records else ([], [])
        pair = "npc_id = ? AND char_id = ? AND status = 'live' AND kind != 'turning_point_revoked'"
        # §21: money exchanges are never recalled, and are not counted here either, so this skip set is exactly the
        # history restore's (_history) when the shard sent none.
        # Codex GH-Pub R5-2 (2026-11-07): the scan stops once the skip set is full (it used to walk the pair's whole
        # history every reply); money exchanges are instead refused one by one in add() below, on the few candidates.
        skip = set()
        if int(skip_conversations) > 0:
            for r in self.rdb.execute(
                    f"SELECT event_id, facts FROM events WHERE {pair} AND kind = 'conversation' ORDER BY epoch DESC, seq DESC "
                    "LIMIT ?", (npc_id, char_id, HISTORY_SCAN_ROWS)):
                if len(skip) >= int(skip_conversations):
                    break
                if not conversation_money(json.loads(r[1])):
                    skip.add(r[0])
        # A GM-undone turning point is never remembered (a turning_point_revoked event names it).
        revoked = {json.loads(r[0]).get("turning_point") for r in self.rdb.execute(
            "SELECT facts FROM events WHERE npc_id = ? AND char_id = ? AND status = 'live' AND kind = 'turning_point_revoked'",
            (npc_id, char_id))}
        if revoked:
            skip |= {r[0] for r in self.rdb.execute(
                "SELECT event_id, facts FROM events WHERE npc_id = ? AND char_id = ? AND kind = 'turning_point'",
                (npc_id, char_id)) if json.loads(r[1]).get("turning_point") in revoked}
        picked = []   # (event_id, reason)

        def is_money(eid):   # §21 + R5-2: checked per candidate (a handful per reply), not over the whole history
            r = self.rdb.execute("SELECT kind, facts FROM events WHERE event_id = ?", (eid,)).fetchone()
            return bool(r) and r[0] == "conversation" and conversation_money(json.loads(r[1]))

        def add(rows, reason):
            for r in rows:
                if r["event_id"] not in skip and all(r["event_id"] != e for e, _ in picked) and not is_money(r["event_id"]):
                    picked.append((r["event_id"], reason))

        marks = ",".join("?" * len(BIG_KINDS))
        big = self.rdb.execute(
            f"SELECT event_id, kind FROM events WHERE {pair} AND kind IN ({marks}) ORDER BY epoch DESC, seq DESC LIMIT 20",
            (npc_id, char_id, *BIG_KINDS)).fetchall()
        if big:
            add([max(big, key=lambda r: IMPORTANCE.get(r["kind"], 0))], "pinned")
        terms = query_terms(utterance)

        def add_recent():
            add(self.rdb.execute(
                f"SELECT event_id FROM events WHERE {pair} AND kind != 'conversation' ORDER BY epoch DESC, seq DESC LIMIT 2",
                (npc_id, char_id)).fetchall(), "recent")
        if stable_first:
            add_recent()
        if terms:
            match = " OR ".join('"' + t.replace('"', "") + '"' for t in terms)
            add(self.rdb.execute(
                f"""SELECT e.event_id FROM events_fts f JOIN events e ON e.event_id = f.event_id
                    WHERE events_fts MATCH ? AND e.{pair.replace(' AND ', ' AND e.')}
                    ORDER BY bm25(events_fts) LIMIT 6""", (match, npc_id, char_id)).fetchall()[:3], "matched")
        if not stable_first:
            add_recent()
        rows = {r["event_id"]: r for r in self.rdb.execute(
            f"SELECT event_id, epoch, seq, kind, facts, at_utc FROM events WHERE event_id IN ({','.join('?' * len(picked))})",
            [e for e, _ in picked]).fetchall()} if picked else {}
    kept, used = [], {"stable": 0, "matched": 0}
    for eid, reason in picked:   # priority order: pinned, matched, recent (stable_first: pinned, recent, matched)
        r = rows[eid]
        line = f"{when(r['at_utc'], now)}: {render(r['kind'], json.loads(r['facts']))}"
        pot = "matched" if matched_budget is not None and reason == "matched" else "stable"
        if used[pot] + len(line) > (matched_budget if pot == "matched" else budget_chars):
            continue
        used[pot] += len(line)
        kept.append((r["epoch"], r["seq"], line, eid, reason, r["kind"]))
    kept.sort()
    if records:
        return [{"line": k[2], "event_id": k[3], "reason": k[4], "kind": k[5]} for k in kept]
    return [k[2] for k in kept], [(k[3], k[4]) for k in kept]


def _visit_days(self, npc_id, char_id):
    """Phase 5c familiarity (Codex finding 4): distinct live visit days for exactly this NPC + character, and the last
    one. Computed from live events only, so a world restore or a deletion tombstone corrects it by itself."""
    with self.rlock:
        if self.rdb.execute(f"SELECT 1 FROM ({TOMBSTONED}) WHERE char_id = ?", (char_id,)).fetchone():
            return 0, None
        r = self.rdb.execute(
            """SELECT COUNT(DISTINCT json_extract(facts, '$.day')), MAX(json_extract(facts, '$.day')) FROM events
               WHERE npc_id = ? AND char_id = ? AND status = 'live' AND kind = 'visited'""", (npc_id, char_id)).fetchone()
    return int(r[0] or 0), r[1]


def _visit_notes(self, npc_id, char_id, utterance="", newest=3, budget_chars=600):
    """Phase 5c recall: the newest visit summaries plus up to 2 matching the line, oldest first, within the budget.
    Only `visit_summary` rows of exactly this NPC + character; nothing word-for-word exists to restore."""
    import named
    with self.rlock:
        if self.rdb.execute(f"SELECT 1 FROM ({TOMBSTONED}) WHERE char_id = ?", (char_id,)).fetchone():
            return []
        pair = "npc_id = ? AND char_id = ? AND status = 'live' AND kind = 'visit_summary'"
        ids = [r[0] for r in self.rdb.execute(
            f"SELECT event_id FROM events WHERE {pair} ORDER BY epoch DESC, seq DESC LIMIT ?",
            (npc_id, char_id, int(newest)))]
        terms = query_terms(utterance)
        if terms:
            match = " OR ".join('"' + t.replace('"', "") + '"' for t in terms)
            for r in self.rdb.execute(
                    f"""SELECT e.event_id FROM events_fts f JOIN events e ON e.event_id = f.event_id
                        WHERE events_fts MATCH ? AND e.{pair.replace(' AND ', ' AND e.')}
                        ORDER BY bm25(events_fts) LIMIT 2""", (match, npc_id, char_id)):
                if r[0] not in ids:
                    ids.append(r[0])
        rows = self.rdb.execute(
            f"SELECT event_id, epoch, seq, facts, at_utc FROM events WHERE event_id IN ({','.join('?' * len(ids))})",
            ids).fetchall() if ids else []
    rows = sorted(rows, key=lambda r: (r["epoch"], r["seq"]))
    out, used = [], 0
    for r in reversed(rows):   # newest first into the budget, then shown oldest first
        line = f"{when(r['at_utc'], datetime.now(timezone.utc))}: {named.render(strip_money(json.loads(r['facts'])))}"
        if used + len(line) > budget_chars:
            continue
        used += len(line)
        out.append(line)
    return list(reversed(out))


# Codex GH-Pub R6 P2 (2026-11-09): the history restore and the recall skip set each look at no more than this many of the
# pair's newest conversation rows. A long money-only tail used to be decoded in full under the reader lock on every reply
# (100,000 money rows ~1.3 s in Codex's probe). Both loops read the same newest rows in the same order with the same cap,
# so the skip set still equals what the history restore restored; past the cap, fewer exchanges are restored - accepted.
HISTORY_SCAN_ROWS = 200


def _history(self, npc_id, char_id, pairs=4):
    """The last few exchanges, so a restart doesn't wipe the thread of a conversation."""
    with self.rlock:
        rows = []
        for r in self.rdb.execute(
                """SELECT facts FROM events WHERE npc_id = ? AND char_id = ? AND status = 'live' AND kind = 'conversation'
                   ORDER BY epoch DESC, seq DESC LIMIT ?""", (npc_id, char_id, HISTORY_SCAN_ROWS)):
            if len(rows) >= int(pairs):
                break
            f = json.loads(r["facts"])
            if not conversation_money(f):   # §21: a money exchange is never restored
                rows.append(f)
    out = []
    for f in reversed(rows):
        out.append({"speaker": "player", "text": f.get("player_said", "")})
        out.append({"speaker": "npc", "text": f.get("she_said", "")})
    return out


def _remap_npc(self, old_id, new_id, reason):
    """Correct a keying bug: move a companion's events to the id her conversations use. Recorded in npc_remaps."""
    with self.lock:
        self.db.execute("BEGIN IMMEDIATE")
        try:
            n = self.db.execute("UPDATE events SET npc_id = ? WHERE npc_id = ?", (new_id, old_id)).rowcount
            self.db.execute("INSERT INTO npc_remaps (at_utc, old_id, new_id, rows, reason) VALUES (?, ?, ?, ?, ?)",
                            (now_utc(), old_id, new_id, n, reason))
            self.db.execute("COMMIT")
        except Exception:
            self.db.in_transaction and self.db.execute("ROLLBACK")
            raise
    return n


Ledger.retrieve = _retrieve
Ledger.visit_days = _visit_days
Ledger.visit_notes = _visit_notes


def _pair_rows(self, npc_id, char_id, kinds=("visit_summary", "reply_delivered")):
    """5c-2 2a: every live row of these kinds for exactly this NPC + character, oldest first; None if the character is
    deleted (a live tombstone). Facts are parsed."""
    marks = ",".join("?" * len(kinds))
    with self.rlock:
        if self.rdb.execute(f"SELECT 1 FROM ({TOMBSTONED}) WHERE char_id = ?", (char_id,)).fetchone():
            return None
        rows = self.rdb.execute(
            f"""SELECT event_id, epoch, seq, kind, facts, at_utc FROM events
                WHERE npc_id = ? AND char_id = ? AND status = 'live' AND kind IN ({marks})
                ORDER BY epoch, seq""", (npc_id, char_id, *kinds)).fetchall()
    # §21: visit notes reach the cards with their money claims/topics/requests removed
    return [{"event_id": r["event_id"], "epoch": r["epoch"], "seq": r["seq"], "kind": r["kind"],
             "facts": strip_money(json.loads(r["facts"])) if r["kind"] == "visit_summary" else json.loads(r["facts"]),
             "at_utc": r["at_utc"]} for r in rows]


def _legacy_marker(self, now_mpt):
    """The memory-clock time old 5c-1 notes are aged from. Taken once per world branch, at the first request that
    carries a memory clock; a restore to before it removes it (reconcile), and the next request takes a new one."""
    with self.rlock:
        r = self.rdb.execute("SELECT mpt FROM legacy_marks ORDER BY id DESC LIMIT 1").fetchone()
    if r is not None:
        return float(r[0])
    if now_mpt is None:
        return None
    with self.lock:
        self.db.execute("BEGIN IMMEDIATE")
        try:
            r = self.db.execute("SELECT mpt FROM legacy_marks ORDER BY id DESC LIMIT 1").fetchone()
            if r is None:
                pos = self.db.execute("""SELECT epoch, seq FROM events WHERE status = 'live'
                                         ORDER BY epoch DESC, seq DESC LIMIT 1""").fetchone()
                self.db.execute("INSERT INTO legacy_marks (epoch, seq, mpt, at_utc) VALUES (?, ?, ?, ?)",
                                (pos[0] if pos else 0, pos[1] if pos else 0, float(now_mpt), now_utc()))
                out = float(now_mpt)
            else:
                out = float(r[0])
            self.db.execute("COMMIT")
        except Exception:
            self.db.in_transaction and self.db.execute("ROLLBACK")
            raise
    return out


def _npc_rows(self, npc_id, epoch, kinds=("visited", "gift_given"), day=None):
    """5c-2 2c (Codex 2c design review F1): every live row of these kinds for ONE NPC across all characters, oldest
    first, for shared mood. None until the latest reconciliation is a success for this shard epoch. Rows of any
    character with a LIVE deletion are left out (a restore that undoes the deletion brings them back); rows from older
    epochs that survived reconciliation stay (status live), they are not filtered by epoch. `day` (YYYY-MM-DD) narrows
    to that Central day. Reconcile state, tombstones and rows come from ONE read transaction (one snapshot)."""
    marks = ",".join("?" * len(kinds))
    sql = (f"""SELECT e.event_id, e.epoch, e.seq, e.char_id, e.kind, e.facts FROM events e
               WHERE e.npc_id = ? AND e.kind IN ({marks}) AND e.status = 'live'
                 AND e.char_id NOT IN ({TOMBSTONED})""" + (" AND json_extract(e.facts, '$.day') = ?" if day else "") +
           " ORDER BY e.epoch, e.seq")
    args = (npc_id, *kinds) + ((day,) if day else ())
    with self.rlock:
        self.rdb.execute("BEGIN")
        try:
            row = self.rdb.execute("SELECT new_epoch, verdict FROM reconciliations ORDER BY rowid DESC LIMIT 1").fetchone()
            ok = bool(row) and isinstance(epoch, int) and not isinstance(epoch, bool) and int(row[0]) == epoch \
                and row[1] in ("ok", "fresh")
            rows = self.rdb.execute(sql, args).fetchall() if ok else None
        finally:
            self.rdb.execute("COMMIT")
    if rows is None:
        return None
    return [{"event_id": r["event_id"], "epoch": r["epoch"], "seq": r["seq"], "char_id": r["char_id"],
             "kind": r["kind"], "facts": json.loads(r["facts"])} for r in rows]


Ledger.pair_rows = _pair_rows
Ledger.npc_rows = _npc_rows
Ledger.legacy_marker = _legacy_marker
Ledger.history = _history
Ledger.remap_npc = _remap_npc


def turn_block(lines):
    """Layout v2: memories brought up by this one line, in the per-turn block (the rest sit in prompt_block)."""
    if not lines:
        return ""
    return "What they just said brings back (your own memories; only their word for anything they told you):\n- " + \
        "\n- ".join(lines)


def prompt_block(lines):
    if not lines:
        return ""
    return ("Things you remember from your time together (your own memories, oldest first; anything they told you is "
            "only their word). Bring one up only when it fits naturally - never recite them:\n- " + "\n- ".join(lines))
