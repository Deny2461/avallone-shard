"""Errands build 1, stage 3 (design v5 3.4): two added tables beside the stored lives - `people_cousin` (pairs of real
people in different towns) and `people_contact` (a directed table, sender -> a resident of the same trade in another
town). Nothing already stored is changed and the five existing tables are only read.

Both tables are written ONCE, by `run_pass`, an operator's step inside a reviewed deploy. Nothing at run time writes
them. Each has a provenance record and a fingerprint; the fingerprints are pinned in the deployed settings, outside the
database. At every brain start `state` audits each table - independently of the code that drew it - and compares the
fingerprint with the pin. A table that is absent, part built, failing its audit or not the pinned one is UNAVAILABLE:
never used, never redrawn, never re-pinned to suit the database found."""
import hashlib
import json
import sqlite3
from datetime import datetime, timezone

RULES_VERSION = 1
KNOWN_RULES = (1,)
AGE_GAP = 25             # a cousin pair's ages are within this many years
AGE_MIN, AGE_MAX = 1, 110
TARGET_PAIRS = 100       # decision 10: about 200 of the townsfolk with no real relative
REAL_RELS = ("spouse", "parent", "child", "sibling")   # "the old four kinds"


def _canon(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def _unit(seed, *parts):
    return hashlib.sha256("|".join([str(seed)] + [str(p) for p in parts]).encode("utf-8")).hexdigest()


def ensure_tables(db):
    """Called only by the pass. A brain that was never given the pass has no such tables: 'absent'."""
    db.execute("""CREATE TABLE IF NOT EXISTS people_cousin (
                    a TEXT NOT NULL, b TEXT NOT NULL, map TEXT NOT NULL, town_a TEXT NOT NULL, town_b TEXT NOT NULL,
                    rules_version INTEGER NOT NULL, PRIMARY KEY (a, b))""")
    db.execute("""CREATE TABLE IF NOT EXISTS people_contact (
                    sender TEXT PRIMARY KEY, contact TEXT NOT NULL, map TEXT NOT NULL, trade TEXT NOT NULL,
                    town_sender TEXT NOT NULL, town_contact TEXT NOT NULL, rules_version INTEGER NOT NULL)""")
    db.execute("CREATE TABLE IF NOT EXISTS people_extra (name TEXT PRIMARY KEY, record TEXT NOT NULL)")


# ---- reading what is stored ------------------------------------------------------------------------------

def _people(db):
    out = {}
    for r in db.execute("SELECT person_id, record FROM people_person").fetchall():
        try:
            out[r[0]] = json.loads(r[1])
        except ValueError:
            continue
    return out


def _with_real_relative(db):
    """Ids that have, or are, a real relative of the old four kinds (an off-stage relative's id starts with 'o-')."""
    out = set()
    for a, rel, b in db.execute("SELECT a, rel, b FROM people_tie").fetchall():
        if rel in REAL_RELS and not str(b).startswith("o-") and not str(a).startswith("o-"):
            out.add(a)
            out.add(b)
    return out


def _past_names_a_cousin(rec):
    """True when this person's stored past already speaks of a cousin (one background line does, 2026-11-21). Such a
    person is given no cousin by the draw, so a named cousin can never sit beside a different one in their own story.
    Anything that cannot be read counts as True: unsure, so no cousin."""
    bg = rec.get("bg")
    if not bg:
        return False
    try:
        import backgrounds
        return "cousin" in (backgrounds.text(bg, rec.get("town") or "", here=True, same_role=True) or "").lower()
    except Exception:
        return True


def _age_ok(rec):
    return type(rec.get("age")) is int and AGE_MIN <= rec["age"] <= AGE_MAX


def _resolved(rec):
    return isinstance(rec, dict) and rec.get("status") == "resolved"


def snapshot_hash(db, held):
    people = sorted([pid, r.get("map"), r.get("town"), r.get("status"), r.get("age"), r.get("sex")]
                    for pid, r in _people(db).items())
    ties = sorted([a, rel, b] for a, rel, b in db.execute("SELECT a, rel, b FROM people_tie").fetchall())
    return hashlib.sha256(_canon([people, ties, held.hash])).hexdigest()


def cousin_rows(db):
    return [tuple(r) for r in db.execute(
        "SELECT a, b, map, town_a, town_b, rules_version FROM people_cousin ORDER BY a, b").fetchall()]


def contact_rows(db):
    return [tuple(r) for r in db.execute(
        "SELECT sender, contact, map, trade, town_sender, town_contact, rules_version FROM people_contact "
        "ORDER BY sender").fetchall()]


def fingerprint(rows):
    """A hash of a table's ordered rows and the rules version."""
    return hashlib.sha256(_canon([RULES_VERSION, [list(r) for r in rows]])).hexdigest()


def provenance(db, name):
    row = db.execute("SELECT record FROM people_extra WHERE name = ?", (name,)).fetchone()
    return json.loads(row[0]) if row else None


# ---- the draw (the pass only) ----------------------------------------------------------------------------

def draw_cousins(people, with_real, held, seed, target_pairs=TARGET_PAIRS):
    """Pairs [(a, b, map, town_a, town_b)], a < b, and the report. Rules, none ever relaxed to reach the target: both
    resolved, both residents on the held town list in their own stored town, neither with a real relative, valid ages
    within AGE_GAP, different home towns on one map, each person in at most one pair, and nobody whose stored past
    already speaks of a cousin."""
    by_town = {}
    for pid, r in people.items():
        at = held.where(pid)
        if (not _resolved(r) or pid in with_real or not _age_ok(r) or at is None or at[2].get("kind") != "resident"
                or (at[0], at[1]) != (r.get("map"), r.get("town")) or _past_names_a_cousin(r)):
            continue
        by_town.setdefault((r["map"], r["town"]), []).append(pid)
    for key in by_town:
        by_town[key].sort(key=lambda pid: _unit(seed, "cousin", pid))
    total = sum(len(v) for v in by_town.values())
    want = min(2 * target_pairs, total)
    # each town's share of the places, by its size (largest remainder), never more than it has
    quota, rest = {}, []
    for key, ids in by_town.items():
        exact = want * len(ids) / total if total else 0
        quota[key] = min(int(exact), len(ids))
        rest.append((-(exact - int(exact)), key))
    left = want - sum(quota.values())
    for _, key in sorted(rest):
        if left <= 0:
            break
        if quota[key] < len(by_town[key]):
            quota[key] += 1
            left -= 1
    waiting = {key: list(by_town[key]) for key in by_town}     # each town's people, in its seeded order
    need = dict(quota)                                          # places each town still has to fill
    pairs = []
    while True:
        # always the town with the most places still to fill (ties by name), and that town's next person
        order = sorted((key for key in waiting if need[key] > 0 and waiting[key]), key=lambda k: (-need[k], k))
        if not order:
            break
        key = order[0]
        p = waiting[key].pop(0)
        for other in order[1:]:
            if other[0] != key[0]:                       # one map
                continue
            q = next((x for x in waiting[other] if abs(people[x]["age"] - people[p]["age"]) <= AGE_GAP), None)
            if q is None:
                continue
            waiting[other].remove(q)
            a, b = sorted((p, q))
            ta, tb = (key[1], other[1]) if a == p else (other[1], key[1])
            pairs.append((a, b, key[0], ta, tb))
            need[key] -= 1
            need[other] -= 1
            break
        # nobody fits this person: they get no cousin and the town's next person is tried - never a looser rule
    paired = {key: quota[key] - need[key] for key in quota}
    report = {"eligible": total, "target_pairs": target_pairs, "pairs": len(pairs),
              "shortfall": {f"{k[0]}/{k[1]}": quota[k] - paired[k] for k in sorted(quota) if quota[k] - paired[k] > 0}}
    return sorted(pairs), report


def draw_contacts(people, held, seed):
    """Rows [(sender, contact, map, trade, town_sender, town_contact)]: every resident with a resolved record who has at
    least one such resident of the same trade in another town on the same map gets one, by a seeded choice."""
    by_trade = {}
    for pid, (map_, town, person) in held.by_id.items():
        if person.get("kind") == "resident" and _resolved(people.get(pid)):
            by_trade.setdefault((map_, person["role"].lower()), []).append((pid, town))
    rows, without = [], 0
    for (map_, trade), group in sorted(by_trade.items()):
        for pid, town in sorted(group):
            others = [(o, t) for o, t in group if t != town]
            if not others:
                without += 1
                continue
            contact, ctown = min(others, key=lambda x: _unit(seed, "contact", pid, x[0]))
            rows.append((pid, contact, map_, trade, town, ctown))
    return sorted(rows), {"senders": len(rows), "without": without}


def run_pass(ledger, held, seed, now=None, target_pairs=TARGET_PAIRS):
    """The operator's one pass. Refuses if either table already holds anything or has a provenance record - it never
    draws again. One transaction under the ledger's writer lock: both tables and both provenance records, or nothing.
    Returns {"cousin": {...}, "contact": {...}} with the two fingerprints to pin in the deployed settings."""
    if held is None:
        raise RuntimeError("no town list held")
    at = (now or datetime.now(timezone.utc)).strftime("%Y-%m-%dT%H:%M:%SZ")
    with ledger.lock:
        db = ledger.db
        db.execute("BEGIN IMMEDIATE")
        try:
            ensure_tables(db)
            if (db.execute("SELECT COUNT(*) FROM people_cousin").fetchone()[0]
                    or db.execute("SELECT COUNT(*) FROM people_contact").fetchone()[0]
                    or db.execute("SELECT COUNT(*) FROM people_extra").fetchone()[0]):
                raise RuntimeError("the pass has already been run - the tables are never drawn again")
            people = _people(db)
            snap = snapshot_hash(db, held)
            pairs, creport = draw_cousins(people, _with_real_relative(db), held, seed, target_pairs)
            contacts, treport = draw_contacts(people, held, seed)
            for a, b, map_, ta, tb in pairs:
                db.execute("INSERT INTO people_cousin (a, b, map, town_a, town_b, rules_version) VALUES (?, ?, ?, ?, ?, ?)",
                           (a, b, map_, ta, tb, RULES_VERSION))
            for sender, contact, map_, trade, ts, tc in contacts:
                db.execute("INSERT INTO people_contact (sender, contact, map, trade, town_sender, town_contact, "
                           "rules_version) VALUES (?, ?, ?, ?, ?, ?, ?)", (sender, contact, map_, trade, ts, tc, RULES_VERSION))
            out = {}
            for name, rows, report in (("cousin", cousin_rows(db), creport), ("contact", contact_rows(db), treport)):
                rec = dict(report, snapshot=snap, seed=str(seed), rules_version=RULES_VERSION, at_utc=at,
                           count=len(rows), fingerprint=fingerprint(rows))
                db.execute("INSERT INTO people_extra (name, record) VALUES (?, ?)", (name, json.dumps(rec, sort_keys=True)))
                out[name] = rec
            db.execute("COMMIT")
        except Exception:
            db.in_transaction and db.execute("ROLLBACK")
            raise
    return out


# ---- the audits (every start; independent of the draw) ---------------------------------------------------

def audit_cousins(db):
    """Every fault found, as short strings; [] = sound. Reads the table, the people and the ties - never the draw."""
    faults, seen = [], {}
    people, with_real = _people(db), _with_real_relative(db)
    for a, b, map_, ta, tb, rv in cousin_rows(db):
        tag = f"{a[:8]}/{b[:8]}"
        if rv not in KNOWN_RULES:
            faults.append(f"{tag}: unknown rules version")
        if a == b:
            faults.append(f"{tag}: the same person")
        elif not a < b:
            faults.append(f"{tag}: not in the fixed order")
        for pid in (a, b):
            if pid in seen:
                faults.append(f"{tag}: {pid[:8]} is in more than one pair")
            seen[pid] = True
            if pid.startswith("o-") or not _resolved(people.get(pid)):
                faults.append(f"{tag}: {pid[:8]} is not a resolved real person")
        ra, rb = people.get(a), people.get(b)
        if _resolved(ra) and _resolved(rb):
            if not (ra.get("map") == rb.get("map") == map_):
                faults.append(f"{tag}: not one map")
            if not (_age_ok(ra) and _age_ok(rb)) or abs(ra["age"] - rb["age"]) > AGE_GAP:
                faults.append(f"{tag}: ages")
            # against the creation snapshot only (people move): the home towns as stored, and no real relative
            if ta == tb or (ra.get("town"), rb.get("town")) != (ta, tb):
                faults.append(f"{tag}: home towns")
            if a in with_real or b in with_real:
                faults.append(f"{tag}: has a real relative")
    return faults


def audit_contacts(db):
    """The directed table's own audit - no pair order, no age bound, a contact may serve many senders."""
    faults = []
    people = _people(db)
    for sender, contact, map_, trade, ts, tc, rv in contact_rows(db):
        tag = f"{sender[:8]}>{contact[:8]}"
        if rv not in KNOWN_RULES:
            faults.append(f"{tag}: unknown rules version")
        if sender == contact:
            faults.append(f"{tag}: the same person")
        rs, rc = people.get(sender), people.get(contact)
        for pid, rec in ((sender, rs), (contact, rc)):
            if pid.startswith("o-") or not _resolved(rec):
                faults.append(f"{tag}: {pid[:8]} is not a resolved real person")
        if _resolved(rs) and _resolved(rc) and not (rs.get("map") == rc.get("map") == map_):
            faults.append(f"{tag}: not one map")
        if not trade or ts == tc:               # the creation snapshot: same trade then, different towns then
            faults.append(f"{tag}: creation facts")
    return faults


def _one(db, name, rows_of, audit, pin):
    """('off'|'available'|'unavailable', reason, fingerprint or None) for one table. No pin = never expected."""
    if not pin:
        return "off", "no fingerprint pinned", None
    try:
        rows, prov = rows_of(db), provenance(db, name)
    except sqlite3.OperationalError:
        return "unavailable", "table absent", None
    if prov is None or prov.get("count") != len(rows) or prov.get("rules_version") not in KNOWN_RULES:
        return "unavailable", "part built or without its provenance record", None
    fp = fingerprint(rows)
    if fp != pin or prov.get("fingerprint") != fp:
        return "unavailable", "fingerprint is not the pinned one", fp
    faults = audit(db)
    if faults:
        return "unavailable", f"audit: {len(faults)} fault(s), first: {faults[0]}", fp
    return "available", None, fp


def state(ledger, cfg):
    """What the brain may use, worked out once at start (and again on request). `cfg` = the deployed settings:
    errand_cousin_pin / errand_contact_pin (fingerprints written once by the runbook) and errand_tables (the switch).
    A pinned table with the switch off is still 'expected' - see completeness()."""
    with ledger.lock:
        cousin = _one(ledger.db, "cousin", cousin_rows, audit_cousins, cfg.get("errand_cousin_pin"))
        contact = _one(ledger.db, "contact", contact_rows, audit_contacts, cfg.get("errand_contact_pin"))
    on = bool(cfg.get("errand_tables"))
    return {"switch": on,
            "cousin": {"state": cousin[0], "reason": cousin[1], "fingerprint": cousin[2]},
            "contact": {"state": contact[0], "reason": contact[1], "fingerprint": contact[2]}}


def completeness(st):
    """3.4: the family view's completeness. 'off' ONLY when no cousin fingerprint is pinned (today's output exactly);
    a pin means cousins are expected for good - 'whole' if the table is available and switched on, else 'unknown'.
    Read from the deployed settings through state(), never from a flag of the running process."""
    c = st["cousin"]["state"]
    if c == "off":
        return "off"
    return "whole" if (c == "available" and st["switch"]) else "unknown"


def usable(st, name):
    return st["switch"] and st[name]["state"] == "available"


def cousin_of(ledger, person_id):
    """The other half of this person's pair, or None. Callers check usable(state, 'cousin') first."""
    pid = str(person_id or "")[:36].lower()
    with ledger.rlock:
        try:
            row = ledger.rdb.execute("SELECT a, b FROM people_cousin WHERE a = ? OR b = ?", (pid, pid)).fetchone()
        except sqlite3.OperationalError:
            return None
    return None if row is None else (row[1] if row[0] == pid else row[0])


def contact_of(ledger, sender_id):
    """{contact, map, trade} for this sender, or None. A -> B never makes B -> A."""
    pid = str(sender_id or "")[:36].lower()
    with ledger.rlock:
        try:
            row = ledger.rdb.execute("SELECT contact, map, trade FROM people_contact WHERE sender = ?", (pid,)).fetchone()
        except sqlite3.OperationalError:
            return None
    return None if row is None else {"contact": row[0], "map": row[1], "trade": row[2]}
