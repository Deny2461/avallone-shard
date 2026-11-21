"""Townsfolk step, Stage 1 - the roster (design §4 [v4]; Codex design R4 GO, 2026-11-15).

The shard sends one document for the whole world: every town's resident townsfolk. In Stage 1 the brain only validates,
stores and acknowledges it - no prompt changes.

The one rule that will make it safe to use (Stage 2): CONTENT EQUALITY. Every prompt request carries the hash of the
shard's current document (the "stamp"). `usable(stamp)` returns the held roster only when the stamp IS a hash and equals
the held roster's hash. Epoch and revision are stored for the logs; they decide nothing. "none", a missing stamp, an old
hash, a hash of other content - none of them match, so nothing is shown.

Wire form (pinned on both sides): {"epoch", "rev", "hash", "content"}; `content` is the canonical document as a STRING and
`hash` is the lower-case hex SHA-256 of that string's UTF-8 bytes. The brain hashes the string it received and only then
parses it, so the two sides cannot disagree about a canonical form. {"probe": true, "hash"} only asks what is held.

A valid document replaces the held one; the same hash again changes nothing; a document that fails ANY check is refused
whole and the held one stays (usable only for a turn that carries its hash).
"""
import hashlib
import json
import re
import threading
from datetime import datetime, timezone

KINDS = ("resident", "placed", "named")
SEXES = ("man", "woman")
MAX_CONTENT = 2 * 1024 * 1024      # bytes of the content string
MAX_PEOPLE = 5000    # the live world holds about 1,400 (census of 2026-11-15)
MAX_TOWNS = 500
HASH = re.compile(r"^[0-9a-f]{64}$")
PLAIN = re.compile(r"^[A-Za-z][A-Za-z '\-]{0,39}$")          # names, roles and towns: the shape the shard sends
MAP = re.compile(r"^[A-Za-z]{1,20}$")
UUID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")
COORD = 16384


class Held:
    """One accepted roster. Never changed after it is made: a turn reads it whole or not at all."""
    __slots__ = ("hash", "epoch", "rev", "towns", "by_id", "accepted_utc", "content")

    def __init__(self, hash_, epoch, rev, towns, by_id, accepted_utc, content):
        self.hash, self.epoch, self.rev, self.towns, self.by_id = hash_, epoch, rev, towns, by_id
        self.accepted_utc, self.content = accepted_utc, content

    def people(self, map_, town):
        """The people of one town key, or () - a town absent from the document is empty."""
        return self.towns.get((map_, town), ())

    def where(self, npc_id):
        """(map, town, person) for an id, or None - an NPC absent from the document is absent."""
        return self.by_id.get(str(npc_id or "").lower())


def parse(content, hash_):
    """(towns, by_id) from a content string, or raise ValueError with the reason. Checks the WHOLE document."""
    if not isinstance(content, str) or not isinstance(hash_, str) or not HASH.match(hash_):
        raise ValueError("content and a lower-case hex SHA-256 hash are required")
    raw = content.encode("utf-8")
    if len(raw) > MAX_CONTENT:
        raise ValueError("content too large")
    if hashlib.sha256(raw).hexdigest() != hash_:
        raise ValueError("hash does not match the content")
    try:
        doc = json.loads(content)
    except ValueError:
        raise ValueError("content is not JSON") from None
    if not isinstance(doc, dict) or set(doc) != {"towns"} or not isinstance(doc["towns"], list):
        raise ValueError("content must be {\"towns\": [...]}")
    if len(doc["towns"]) > MAX_TOWNS:
        raise ValueError("too many towns")
    towns, by_id, total = {}, {}, 0
    for t in doc["towns"]:
        if not isinstance(t, dict) or set(t) != {"map", "town", "people"} or not isinstance(t["people"], list):
            raise ValueError("a town must be {map, town, people}")
        map_, town = t["map"], t["town"]
        if not isinstance(map_, str) or not MAP.match(map_) or not isinstance(town, str) or not PLAIN.match(town):
            raise ValueError("bad map or town name")
        if (map_, town) in towns:
            raise ValueError(f"town listed twice: {map_}/{town}")
        people = []
        for p in t["people"]:
            total += 1
            if total > MAX_PEOPLE:
                raise ValueError("too many people")
            if not isinstance(p, dict) or set(p) != {"id", "name", "role", "sex", "kind", "home"}:
                raise ValueError("a person must be {id, name, role, sex, kind, home}")
            pid = p["id"]
            if not isinstance(pid, str) or not UUID.match(pid):
                raise ValueError("bad id")
            if pid in by_id:
                raise ValueError(f"id listed twice: {pid}")   # across ALL towns: one id is in one town
            if not all(isinstance(p[k], str) and PLAIN.match(p[k]) for k in ("name", "role")):
                raise ValueError(f"bad name or role for {pid}")
            if p["sex"] not in SEXES or p["kind"] not in KINDS:
                raise ValueError(f"unknown sex or kind for {pid}")
            home = p["home"]
            if home is not None:
                if (not isinstance(home, dict) or set(home) != {"x", "y"}
                        or not all(type(home[k]) is int and 0 <= home[k] < COORD for k in ("x", "y"))):
                    raise ValueError(f"bad home for {pid}")
                home = (home["x"], home["y"])
            person = {"id": pid, "name": p["name"], "role": p["role"], "sex": p["sex"], "kind": p["kind"], "home": home}
            people.append(person)
            by_id[pid] = (map_, town, person)
        towns[(map_, town)] = tuple(people)
    return towns, by_id


class Roster:
    def __init__(self, ledger=None, log=None):
        self._ledger, self._log = ledger, log
        self._swap = threading.Lock()
        self._held = None
        if ledger is not None:
            with ledger.lock:
                ledger.db.execute("""CREATE TABLE IF NOT EXISTS roster_held (
                                       one INTEGER PRIMARY KEY CHECK (one = 1), hash TEXT NOT NULL, epoch INTEGER NOT NULL,
                                       rev INTEGER NOT NULL, content TEXT NOT NULL, accepted_utc TEXT NOT NULL)""")
            row = ledger.db.execute("SELECT hash, epoch, rev, content, accepted_utc FROM roster_held WHERE one = 1").fetchone()
            if row is not None:
                try:   # the stored copy is checked like a new one: a damaged row is simply not held
                    towns, by_id = parse(row[3], row[0])
                    self._held = Held(row[0], row[1], row[2], towns, by_id, row[4], row[3])
                except ValueError as e:
                    if log:
                        log(event="roster", op="load", outcome="discarded", error=str(e))

    def held(self):
        return self._held

    def held_hash(self):
        h = self._held
        return h.hash if h is not None else None

    def usable(self, stamp):
        """The held roster for a turn carrying this stamp, or None. Only an equal 64-hex hash matches: never "none"."""
        h = self._held   # one read: the turn sees one roster whole
        if h is None or not isinstance(stamp, str) or not HASH.match(stamp) or stamp != h.hash:
            return None
        return h

    def accept(self, doc, now=None):
        """(ok, error). A valid document replaces the held one; anything else changes nothing."""
        try:
            if not isinstance(doc, dict):
                raise ValueError("a JSON object is required")
            epoch, rev = doc.get("epoch"), doc.get("rev")
            if type(epoch) is not int or type(rev) is not int or epoch < 0 or rev < 0:
                raise ValueError("epoch and rev must be whole numbers")
            hash_ = doc.get("hash")
            cur = self._held
            if cur is not None and isinstance(hash_, str) and hash_ == cur.hash and isinstance(doc.get("content"), str) \
                    and doc["content"] == cur.content:
                return True, None   # the same document again (a lost acknowledgement): nothing changes
            towns, by_id = parse(doc.get("content"), hash_)
        except ValueError as e:
            if self._log:
                self._log(event="roster", op="accept", outcome="refused", error=str(e))
            return False, str(e)
        stamp = (now or datetime.now(timezone.utc)).strftime("%Y-%m-%dT%H:%M:%SZ")
        new = Held(hash_, epoch, rev, towns, by_id, stamp, doc["content"])
        with self._swap:
            if self._ledger is not None:
                with self._ledger.lock:
                    self._ledger.db.execute(
                        "INSERT INTO roster_held (one, hash, epoch, rev, content, accepted_utc) VALUES (1, ?, ?, ?, ?, ?) "
                        "ON CONFLICT(one) DO UPDATE SET hash = excluded.hash, epoch = excluded.epoch, rev = excluded.rev, "
                        "content = excluded.content, accepted_utc = excluded.accepted_utc",
                        (hash_, epoch, rev, doc["content"], stamp))
            if self._log and cur is not None and (cur.epoch, cur.rev) == (epoch, rev) and cur.hash != hash_:
                # design §4.5: a repeated (epoch, rev) with other content is logged - and selects nothing
                self._log(event="roster", op="accept", outcome="pair_repeated", epoch=epoch, rev=rev)
            self._held = new
        if self._log:
            self._log(event="roster", op="accept", outcome="held", epoch=epoch, rev=rev, hash=hash_[:12],
                      towns=len(towns), people=len(by_id))
        return True, None
