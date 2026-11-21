"""Townsfolk step, Stage 2 - storing people and writing the "who you are" block (design §6.1, §6.5, §6.6, §8.1).
First built as a prototype for a measurement run; reviewed by Codex and live since November 2026.

Storage: one transaction per town, under the ledger's writer lock, no model or network call inside. The town's
backgrounds are assigned in the same transaction; the finished town is audited (people.audit) BEFORE anything is
written, and a town that fails writes nothing. A person who has a row is never built again, wherever they stand.
v0 keeps each record as one JSON text column - a prototype's storage, not the final table design.

The block: `block()` writes, in code, every sentence about the NPC's own life that the model is given. Names and
places of REAL relatives come from the roster usable for this turn, never from stored text; with no usable roster a
real relative is given without name, role or place (design §6.1). Nothing here reads the player's words.
"""
import hashlib
import json
import time
import re
import math
import os
from datetime import datetime, timezone

import backgrounds
import nature
import people
import town_knowledge

FILES = ("people_tags.json", "people_lists.json", "people_names.json", "people_rules.json", "backgrounds.json", "nature.json")


def ensure_tables(ledger):
    with ledger.lock:
        ledger.db.execute("""CREATE TABLE IF NOT EXISTS people_town (
                               map TEXT NOT NULL, town TEXT NOT NULL, built_utc TEXT NOT NULL, rules_version INTEGER NOT NULL,
                               roster_hash TEXT NOT NULL, catalog_hash TEXT NOT NULL, summary TEXT NOT NULL, PRIMARY KEY (map, town))""")
        ledger.db.execute("""CREATE TABLE IF NOT EXISTS people_person (
                               person_id TEXT PRIMARY KEY, map TEXT NOT NULL, town TEXT NOT NULL, status TEXT NOT NULL,
                               record TEXT NOT NULL, built_utc TEXT NOT NULL)""")
        ledger.db.execute("CREATE TABLE IF NOT EXISTS people_offstage (off_id TEXT PRIMARY KEY, record TEXT NOT NULL)")
        ledger.db.execute("""CREATE TABLE IF NOT EXISTS people_tie (
                               a TEXT NOT NULL, rel TEXT NOT NULL, b TEXT NOT NULL, PRIMARY KEY (a, rel, b))""")
        # repair A3 (Codex Method R1 F9): who shares a home, as built - real ids and off-stage ids
        ledger.db.execute("""CREATE TABLE IF NOT EXISTS people_household (
                               hid TEXT PRIMARY KEY, map TEXT NOT NULL, town TEXT NOT NULL, record TEXT NOT NULL)""")


class CatalogueChanged(Exception):
    pass


def _file_bytes():
    out = {}
    for name in FILES:
        with open(os.path.join(people.HERE, name), "rb") as f:
            out[name] = f.read()
    return out


def _hash_of(raw):
    h = hashlib.sha256()
    for name in FILES:
        h.update(name.encode() + b"\0" + raw[name] + b"\0")
    return h.hexdigest()


def freeze():
    """Repair B3 (Codex Method R1 F13): read the exact catalogue bytes ONCE, prove the catalogues the build will use in
    memory are those bytes, and return their provenance - or raise CatalogueChanged (a file edited since it was loaded:
    nothing is built until the brain has reloaded it). The builder's own code is hashed too (algorithm identity)."""
    raw = _file_bytes()
    parsed = {n: json.loads(b.decode("utf-8")) for n, b in raw.items()}
    d = people.data()
    bg, nat = backgrounds.cfg(), nature.cfg()
    checks = {
        "people_tags.json": d["tags"] == parsed["people_tags.json"]["tags"],
        "people_lists.json": d["lists"] == parsed["people_lists.json"],
        "people_names.json": d["names"] == parsed["people_names.json"],
        "people_rules.json": d["rules"] == parsed["people_rules.json"],
        "backgrounds.json": bg is not None and all(bg.get(k) == v for k, v in parsed["backgrounds.json"].items())
                            and len([k for k in bg if k != "_by_id"]) == len(parsed["backgrounds.json"]),
        "nature.json": nat == parsed["nature.json"],
        "known ids": d["known"] == people.known_from(parsed["backgrounds.json"], parsed["nature.json"]),
    }
    stale = sorted(k for k, ok in checks.items() if not ok)
    if stale:
        raise CatalogueChanged("in memory differs from disk: " + ", ".join(stale))
    code = hashlib.sha256()
    for name in ("people.py", "people_store.py"):
        with open(os.path.join(people.HERE, name), "rb") as f:
            code.update(name.encode() + b"\0" + f.read() + b"\0")
    return {"catalog_hash": _hash_of(raw), "files": {n: hashlib.sha256(b).hexdigest()[:16] for n, b in raw.items()},
            "builder_code": code.hexdigest()[:16]}


def catalog_hash():
    h = hashlib.sha256()
    for name in FILES:
        with open(os.path.join(people.HERE, name), "rb") as f:
            h.update(name.encode() + b"\0" + f.read() + b"\0")
    return h.hexdigest()


def _stored(r):
    """What is kept of a resident's record: facts, the pinned nature, and the few flags the block needs."""
    p = r["prof"] or {}
    return {k: r[k] for k in ("id", "role", "sex", "map", "town", "status", "reason", "age", "years_town", "years_town_kind",
                              "years_trade", "marital", "unit", "unit_kind", "household", "home_line", "personality", "habit",
                              "subject", "bg")} | {"home": p.get("home"), "unsupported": p.get("unsupported", [])}


def build_town(ledger, held, map_, town, log=None, now=None, epoch=None, frozen=None):
    """Build and store one town from the held roster. Returns 'built', 'already', 'empty', 'audit-failed', or - repair
    B3 - 'not-reconciled' (no record is written unless the memory ledger's LATEST reconciliation is a success for the
    shard session `epoch`, checked inside the write transaction) or 'catalogue-changed' (the catalogue bytes are not the
    frozen ones). `frozen` = freeze() taken once for a whole build_all; taken here when not given."""
    residents = [p for p in held.people(map_, town) if p["kind"] == "resident"]
    if not residents:
        return "empty"
    if epoch is None:
        return "not-reconciled"
    try:
        frozen = frozen or freeze()
    except CatalogueChanged as e:
        if log:
            log(event="people", op="build", outcome="catalogue-changed", map=map_, town=town, error=str(e)[:200])
        return "catalogue-changed"
    cat = backgrounds.cfg()
    pers = [(p["key"], p.get("weight", 0)) for p in nature.cfg()["personalities"]]
    stamp = (now or datetime.now(timezone.utc)).strftime("%Y-%m-%dT%H:%M:%SZ")
    t0 = time.monotonic()
    with ledger.lock:
        db = ledger.db
        db.execute("BEGIN IMMEDIATE")
        try:
            if not ledger.reconciled(epoch):                 # repair B3: under the same lock as any reconciliation
                db.execute("ROLLBACK")
                if log:
                    log(event="people", op="build", outcome="not-reconciled", map=map_, town=town, epoch=epoch)
                return "not-reconciled"
            if db.execute("SELECT 1 FROM people_town WHERE map = ? AND town = ?", (map_, town)).fetchone():
                db.execute("COMMIT")
                return "already"
            have = {r[0] for r in db.execute("SELECT person_id FROM people_person")}
            fresh = [p for p in residents if p["id"] not in have]          # §6.6: a person with a row is never rebuilt
            # Codex Code R1 F1: a resident who was given a past in ANOTHER town before any record existed (they have
            # since moved) gets no record here - a record built now would say "born here in <this town>" over a past
            # already told as another town's, for ever. They keep the earlier way of talking (their stored past, no
            # family record); their ids go in the log and in the town's stored summary. The past itself is not touched.
            elsewhere = sorted(p["id"] for p in fresh if any(
                str(t or "").lower() != str(town).lower()
                for (t,) in db.execute("SELECT town FROM npc_backgrounds WHERE npc_id = ?", (p["id"],))))
            fresh = [p for p in fresh if p["id"] not in set(elsewhere)]
            bgs, held_events, new_bg = {}, {}, []
            for (eid,) in db.execute("SELECT event_id FROM npc_backgrounds WHERE town = ?", (town,)):
                held_events[eid] = held_events.get(eid, 0) + 1
            for p in sorted(fresh, key=lambda p: hashlib.sha256(p["id"].encode()).hexdigest()):
                row = db.execute("SELECT town, home_id, life_id, event_id FROM npc_backgrounds WHERE npc_id = ?", (p["id"],)).fetchone()
                if row is not None:
                    bgs[p["id"]] = {"town": row[0], "home": row[1], "life": row[2], "event": row[3]}
                    continue
                got = backgrounds._choose(cat, p["id"], town, p["role"], "", held_events) if cat else None
                if got:
                    h, life, e, _ = got
                    bgs[p["id"]] = {"town": town, "home": h["id"], "life": life["id"], "event": e["id"]}
                    held_events[e["id"]] = held_events.get(e["id"], 0) + 1
                    new_bg.append((p["id"], town, h["id"], life["id"], e["id"], stamp))
            others = sorted({t for (m, t) in held.towns if m == map_})
            by_id = (cat or {}).get("_by_id") or {}
            origins = {i: (by_id.get(b["home"]) or {}).get("town") for i, b in bgs.items()}
            res = people.build_town(map_, town, fresh, bgs, {p["id"]: nature.natural(p["id"]) for p in fresh}, pers, others,
                                    origins={i: t for i, t in origins.items() if t})
            bad = people.audit(res)
            if bad:
                db.execute("ROLLBACK")
                if log:
                    log(event="people", op="build", outcome="audit-failed", map=map_, town=town, violations=len(bad), first=str(bad[0])[:200])
                return "audit-failed"
            db.executemany("INSERT INTO npc_backgrounds (npc_id, town, home_id, life_id, event_id, assigned_utc) VALUES (?, ?, ?, ?, ?, ?)", new_bg)
            db.executemany("INSERT INTO people_person (person_id, map, town, status, record, built_utc) VALUES (?, ?, ?, ?, ?, ?)",
                           [(r["id"], map_, town, r["status"], json.dumps(_stored(r), sort_keys=True), stamp) for r in res["records"].values()])
            db.executemany("INSERT INTO people_offstage (off_id, record) VALUES (?, ?)",
                           [(o["id"], json.dumps({k: o[k] for k in ("id", "sex", "alive", "age", "name", "fate", "place", "place_town", "married_here")},
                                                 sort_keys=True)) for o in res["offstage"].values()])
            db.executemany("INSERT INTO people_tie (a, rel, b) VALUES (?, ?, ?)", [tuple(t) for t in res["ties"]])
            db.executemany("INSERT INTO people_household (hid, map, town, record) VALUES (?, ?, ?, ?)",
                           [(h["id"], map_, town, json.dumps({"id": h["id"], "members": sorted(h["members"]), "home_line": h["home_line"]},
                                                              sort_keys=True)) for h in res["households"].values()])
            summary = people.summary(res)
            summary["past_elsewhere"] = elsewhere
            if _hash_of(_file_bytes()) != frozen["catalog_hash"]:       # repair B3: the bytes did not change under us
                db.execute("ROLLBACK")
                if log:
                    log(event="people", op="build", outcome="catalogue-changed", map=map_, town=town)
                return "catalogue-changed"
            summary["provenance"] = {"catalogue_files": frozen["files"], "builder_code": frozen["builder_code"],
                                     "shard_epoch": epoch}
            db.execute("INSERT INTO people_town (map, town, built_utc, rules_version, roster_hash, catalog_hash, summary) VALUES (?, ?, ?, ?, ?, ?, ?)",
                       (map_, town, stamp, res["rules_version"], held.hash, frozen["catalog_hash"], json.dumps(summary, sort_keys=True)))
            db.execute("COMMIT")
        except Exception:
            db.execute("ROLLBACK")
            raise
    if log:
        # ms (Codex Code R1 F3): how long this town held the memory writer - what a talk turn could have waited behind
        log(event="people", op="build", outcome="built", map=map_, town=town, residents=summary["residents"],
            in_units=summary["in_a_real_unit"], offstage=summary["offstage_people"], unresolved=summary["unresolved"],
            past_elsewhere=len(elsewhere), past_elsewhere_ids=elsewhere[:20], ms=int((time.monotonic() - t0) * 1000))
    return "built"


def build_all(ledger, held, log=None, epoch=None):
    """Every town of the held roster that is not built yet. {outcome: count}. One catalogue freeze for the whole pass;
    each town still checks the session and the bytes itself (repair B3)."""
    out = {}
    try:
        frozen = freeze()
    except CatalogueChanged as e:
        if log:
            log(event="people", op="build_all", outcome="catalogue-changed", epoch=epoch, roster=held.hash[:12], error=str(e)[:200])
        return {"catalogue-changed": len(held.towns)}
    for (map_, town) in sorted(held.towns):
        try:
            r = build_town(ledger, held, map_, town, log=log, epoch=epoch, frozen=frozen)
        except Exception as e:                    # Codex Code R1 F4: one town's failure (rolled back) does not stop the rest
            r = "error"
            if log:
                log(event="people", op="build", outcome="error", map=map_, town=town, error=f"{type(e).__name__}: {e}"[:200])
        out[r] = out.get(r, 0) + 1
    if log:
        # Codex GoLive R1 F1: the line says WHICH work it describes - the shard session and the roster (first 12 of its
        # hash, as the roster's own "held" line prints it) - and how many of those residents' STORED rows are not
        # 'resolved', read from the table: a pass that built nothing itself still shows it.
        c = coverage(ledger, held)
        log(event="people", op="coverage", epoch=epoch, roster=held.hash[:12], residents=c["residents"],
            with_record=c["with_record"], without=len(c["without"]), towns=c["towns"], first=c["without"][:20],
            unresolved=len(c["unresolved"]), unresolved_first=c["unresolved"][:20])
    return out


RETRY = ("error", "audit-failed", "catalogue-changed", "not-reconciled")     # outcomes that leave a town still to build


def coverage(ledger, held):
    """Codex Code R1 F5: which residents of the held roster have NO stored record - {residents, with_record, without:
    [ids], towns: {"map/town": count}}. A town is built once, so a resident who appears on the roster later (or was
    left out by F1) has none and talks the earlier way; 'already' for every town does not mean everyone is covered -
    this does. Codex GoLive R1 F1: `unresolved` - the residents who HAVE a row whose stored status is not 'resolved' (a
    row existing is not the same as a record that resolved). Nothing is written."""
    with ledger.rlock:
        have = dict(ledger.rdb.execute("SELECT person_id, status FROM people_person").fetchall())
    n, without, towns, unresolved = 0, [], {}, []
    for (map_, town) in sorted(held.towns):
        ids = [p["id"] for p in held.people(map_, town) if p["kind"] == "resident"]
        miss = [i for i in ids if i not in have]
        n += len(ids)
        without += miss
        unresolved += [i for i in ids if i in have and have[i] != "resolved"]
        if miss:
            towns[f"{map_}/{town}"] = len(miss)
    return {"residents": n, "with_record": n - len(without), "without": sorted(without), "towns": towns,
            "unresolved": sorted(unresolved)}


def view(ledger, person_id):
    """Everything stored about one person, or None: {record, kin: [(rel, 'real'|'off', record)], house}. `house` is the
    OTHER members of the person's household as built - [('real'|'off', record)], [] for a household of one - or None
    when no household row is stored (a town built before repair A3): then nothing is said about who shares the home."""
    pid = str(person_id or "")[:36].lower()
    with ledger.rlock:
        row = ledger.rdb.execute("SELECT record FROM people_person WHERE person_id = ?", (pid,)).fetchone()
        if row is None:
            return None
        rec = json.loads(row[0])
        kin = []
        for rel, b in ledger.rdb.execute("SELECT rel, b FROM people_tie WHERE a = ? ORDER BY rel, b", (pid,)).fetchall():
            if b.startswith("o-"):
                o = ledger.rdb.execute("SELECT record FROM people_offstage WHERE off_id = ?", (b,)).fetchone()
                if o:
                    kin.append((rel, "off", json.loads(o[0])))
            else:
                o = ledger.rdb.execute("SELECT record FROM people_person WHERE person_id = ?", (b,)).fetchone()
                if o:
                    kin.append((rel, "real", json.loads(o[0])))
        house = None
        h = ledger.rdb.execute("SELECT record FROM people_household WHERE hid = ?", (rec.get("household") or "",)).fetchone() \
            if rec.get("household") else None
        if h is not None:
            house = []
            for m in json.loads(h[0])["members"]:
                if m == pid:
                    continue
                table, key = ("people_offstage", "off_id") if m.startswith("o-") else ("people_person", "person_id")
                o = ledger.rdb.execute(f"SELECT record FROM {table} WHERE {key} = ?", (m,)).fetchone()
                if o:
                    house.append(("off" if m.startswith("o-") else "real", json.loads(o[0])))
    return {"record": rec, "kin": kin, "house": house}


_pin_cache = {}


def pins(ledger, npc_id):
    """The stored nature of one person as nature.set_pins() wants it - {"key", "habit", "subject"} - or None when there
    is no resolved record. A stored key that is no longer in the catalogue is replaced by the next pick, in this NPC's
    own order, whose tags still fit the rest of its record (design 6.4); the "years" habit is switched off for a person
    missing either number of years (N11, repair A2). Deterministic; cached per (person, catalogue state)."""
    pid = str(npc_id or "")[:36].lower()
    c = nature.cfg()
    state = (pid, nature._cache.get("mtime"))
    if state in _pin_cache:
        return _pin_cache[state]
    with ledger.rlock:
        row = ledger.rdb.execute("SELECT record FROM people_person WHERE person_id = ?", (pid,)).fetchone()
    out = None
    if row is not None:
        r = json.loads(row[0])
        if r["status"] == "resolved":
            bg = r.get("bg") or {}
            bg_ids = [bg[k] for k in ("home", "life", "event") if bg.get(k)]
            keys = {"personality": [x["key"] for x in c.get("personalities") or []], "habit": [x["key"] for x in c.get("habits") or []],
                    "subject": list(c.get("subjects") or [])}
            chosen = {"personality": r["personality"], "habit": r["habit"], "subject": r["subject"]}

            def fits(slot, value):
                trial = dict(chosen, **{slot: value})
                # repair B2: a slot still holding a REMOVED key is left out of the trial (it is replaced in its own turn);
                # unknown ids are no longer silently "no tags"
                extra = [f"{sl}:{trial[sl]}" for sl in ("personality", "habit", "subject") if trial[sl] in keys[sl]]
                try:
                    p = people.profile(bg_ids + extra)
                except people.Unresolved:
                    return False
                return p["minage"] <= r["age"] <= p["maxage"] and (p["home"] in (None, r.get("home")))

            for slot in ("personality", "habit", "subject"):
                if chosen[slot] in keys[slot]:
                    continue
                weights = {x["key"]: x.get("weight", 0) for x in c.get("personalities") or []} if slot == "personality" else {}
                order = sorted((k for k in keys[slot] if weights.get(k, 1) > 0),
                               key=lambda k: (-math.log(max(people._nature_unit(pid, slot, k), 1e-300)) / weights.get(k, 1), k))
                chosen[slot] = next((k for k in order if fits(slot, k)), chosen[slot])
            habit = chosen["habit"]
            if habit == "years" and not years_habit_allowed(r):
                habit = ""
            out = {"key": chosen["personality"], "habit": habit, "subject": chosen["subject"]}
    if out is None:
        return None              # Codex Method R2 F8: never cache a miss - a build committed later must be seen at once
    if len(_pin_cache) > 20000:
        _pin_cache.clear()
    _pin_cache[state] = out
    return out


# ------------------------------------------------------------------------------------------------- the block
WORD = {("spouse", "man"): "husband", ("spouse", "woman"): "wife", ("parent", "man"): "father", ("parent", "woman"): "mother",
        ("child", "man"): "son", ("child", "woman"): "daughter", ("sibling", "man"): "brother", ("sibling", "woman"): "sister"}
PLAIN = {"spouse": "husband or wife", "parent": "parent", "child": "child", "sibling": "brother or sister"}
ORDER = {"spouse": 0, "child": 1, "parent": 2, "sibling": 3}
RULES = ("Never state a number, a name or a relative that is not given above. If you are asked about your own life and the "
         "answer is not above, say plainly that you do not know, or that it is not something you go into - never invent it.")
NOW_AND_THEN = ("You know all of this as anyone knows their own life. Mention it only now and then, when the talk turns that "
                "way, and never recite it. Asked directly, answer plainly and truthfully from it.")
HOLD_BACK = "In this reply, do not bring up your own age, family or past unless the traveller asks about them."
UNRESOLVED = ("About your own life: you do not talk about your age, your family or how long you have been here. If asked, turn "
              "the question aside in your own manner - never invent an age, a relative or a number of years.")


def unknown(what):
    """Repair A2 (Codex Method R1 F12): a fact the record does not hold is stated as not known, for that one fact only -
    no invented history ("you have never kept count") and no "give no number" order that can spill onto another fact.
    How to answer is left to RULES, the one general line."""
    return f"- Not known to you: {what}."


BANDS = ((3, "you are still new here"), (9, "you have been here a while"), (None, "you have been here a long time"))


def band_line(n):
    """Decision 8 (the owner, chat, 2026-11-17): years in town said plainly - 0-3 new, 4-9 a while, 10 or more a long time.
    Written by code beside the number, which stays the fact; only when the person stands in their own town."""
    return "- Put plainly, " + next(w for top, w in BANDS if top is None or n <= top) + "."


def years_habit_allowed(r):
    """N11 / repair A2: the "years" habit is left out whenever either number of years is unknown."""
    return r.get("years_town") is not None and r.get("years_trade") is not None


def _a(noun):
    return ("an " if str(noun)[:1].lower() in "aeiou" else "a ") + str(noun)


def name_words(names):
    """Lower-case full names and each of their words, for the off-stage name clash test."""
    out = set()
    for n in names:
        n = str(n or "").strip().lower()
        if n:
            out.add(n)
            out.update(w for w in re.split(r"[^a-z']+", n) if len(w) > 1)
    return out


def _join(xs):
    return xs[0] if len(xs) == 1 else ", ".join(xs[:-1]) + " and " + xs[-1]


def household(v, r, here, where, avoid, got=None):
    """Repair A3 (Codex Method R1 F9): one line saying who shares this person's home, or None.
    - Said only when the person stands in their own stored town, and only from the stored household (`v["house"]`).
    - An off-stage member (a husband or wife, a young child) always shares it - a permanent fact.
    - A real member shares it only when the roster usable for THIS turn has them in the person's own map and town;
      moved away, or not on that roster -> not asserted (a moved member's own kin line still says where they are).
    - "You live alone" only for a household built as one person; never inferred from members who have moved."""
    house = v.get("house")
    got = got if isinstance(got, list) else []
    if not here or house is None:
        return None
    if not house:
        return "- You live alone."
    rel_of = {o.get("id"): rel for rel, _, o in v["kin"]}
    with_ = []
    for kind, o in sorted(house, key=lambda m: (ORDER.get(rel_of.get(m[1].get("id")), 9), str(m[1].get("id")))):
        rel = rel_of.get(o.get("id"))
        if kind == "off":
            word = WORD.get((rel, o.get("sex")))
            if word is None:
                continue
            shown = str(o.get("name", "")).lower() not in avoid
            with_.append(f"your {word} {o['name']}" if shown else f"your {word}")
            got.append({"kind": "off", "id": o.get("id"), "rel": rel, "word": word, "name": o["name"] if shown else None})
            continue
        at = where(o["id"]) if where else None
        if at is None or str(at[0]).lower() != str(r["map"]).lower() or str(at[1] or "").lower() != str(r["town"]).lower():
            continue
        person = at[2]
        word = WORD.get((rel, person.get("sex")))
        with_.append(f"your {word} {person['name']}" if word else person["name"])
        got.append({"kind": "real", "id": o.get("id"), "rel": rel, "word": word, "name": person["name"]})
    return f"- You share your home with {_join(with_)}." if with_ else None


def hold_back(npc_id, conversation, turn, share):
    """Arrangement B: is the code-written hold-back line in this turn's prompt? Stable per (npc, conversation, turn)."""
    return people.u(npc_id, "hold", conversation, turn) < float(share)


def standing(r, where, town_now):
    """Repair A4 (design 6.1, 6.2b): where this person stands on THIS turn - (cur, here). cur = (map, town), lower case,
    from the roster usable for the turn, or None when there is no usable roster, the person is not on it, or it
    disagrees with the town the game reports for the turn; here = cur is the person's own stored map and town.
    With cur None the stored town's NAME is used throughout - never "here"."""
    at = where(r["id"]) if where else None
    if at is None:
        return None, False
    m, t = str(at[0] or "").lower(), str(at[1] or "").lower()
    if town_now and str(town_now).lower() != t:
        return None, False
    return (m, t), (m, t) == (str(r["map"] or "").lower(), str(r["town"] or "").lower())


def _origin(r):
    by_id = (backgrounds.cfg() or {}).get("_by_id") or {}
    return ((by_id.get((r.get("bg") or {}).get("home")) or {}).get("town") or "").lower()


def nature_drops(v, where, town_now, role_now, p):
    """Repair A4 (and the A2 changed-role gap): what of the effective nature `p` must be left out of THIS turn's prompt
    because it would be untrue - a list of "habit" / "personality". Design 6.2b: years are tenure only while the person
    stands in the town they were counted in, trade years only while the role is the one they were counted for;
    a place-bound personality line ("homesick") is checked against the stored home and the current town."""
    if v is None or not p or v["record"]["status"] != "resolved":
        return []
    r = v["record"]
    cur, here = standing(r, where, town_now)
    same_role = role_now is None or str(role_now).lower() == str(r["role"]).lower()
    out = []
    if p.get("habit") == "years" and not (here and same_role and years_habit_allowed(r)):
        out.append("habit")
    if p.get("key") == "homesick":
        if r.get("home") == "here" and (cur is None or here):        # may be standing in the town they were born in
            out.append("personality")
        elif cur is not None and _origin(r) and cur[1] == _origin(r):  # standing in the very town they came from
            out.append("personality")
    return out


def for_turn(p, drops):
    """The nature `p` with this turn's drops applied (a copy), for nature.prompt_block()."""
    if not p or not drops:
        return p
    q = dict(p)
    if "habit" in drops:
        q.update(habit="", habit_line="")
    if "personality" in drops:
        q.update(line="")
    return q


def figurative_line(p):
    """Repair A7 (Codex Method R1 F15): when this turn's nature carries a jab tagged `figurative` (the grandmother who
    "has been dead ten years"), one line saying it is only a saying - so it is never read as a fact about the family,
    nor as a number about anyone's life. "" when no such jab is in the prompt."""
    if not p or p.get("group") != "prickly" or not p.get("jab"):
        return ""
    jabs = nature.cfg().get("jabs") or []
    if p["jab"] not in jabs:
        return ""
    tag = (people.data()["tags"].get(f"jab:{jabs.index(p['jab'])}") or {})
    kin = tag.get("figurative")
    if not kin:
        return ""
    return (f"- Your rude jab about a {kin} is only a figure of speech: it is not about anyone in your own family, and "
            "it gives no age or year for anyone.")


def block(v, town_now, where=None, avoid_names=(), role_now=None, arrangement="A", held_back=False, nature_now=None,
          snap=None, works=False, kin_here=False):
    """The "who you are" lines for one NPC (design §8.1), or "" when nothing is stored. `works` (play test 2026-11-19,
    the owner): for a real relative standing in the town this person stands in,
    the line also says where that relative works, measured by code from this person's own place - off by default, and
    with it off the text is exactly what it was. `kin_here` (same play test, the owner): one extra line gathering the
    living relatives the lines above place in the town this person stands in - or saying there are none - written only
    while the roster usable for the turn says where the person stands (their own town or, moved, another) and every
    real relative could be named this turn; off by default, text unchanged. `v` = view(); `where(id)` gives
    (map, town, roster person) for a real relative on the roster usable for THIS turn, or None; `avoid_names` are names
    that must not be used for an off-stage relative this turn (the player, a companion, a resident standing here).
    `snap` (repair C1/D3, for the measurement only): a dict filled, by the same code that writes each line, with exactly
    what this turn's text states - the truth a reply is scored against. Passing it never changes the text."""
    s = snap if isinstance(snap, dict) else {}
    if v is None or arrangement not in ("A", "B"):
        s.update(given=False)
        return ""
    r = v["record"]
    if r["status"] != "resolved":
        s.update(given=True, status="unresolved")
        return UNRESOLVED
    lists = people.data()["lists"]
    fates = {f["id"]: f["text"] for f in lists["fates"]}
    homes = {d["id"]: d["text"] for d in lists["homes"]}
    cur, here = standing(r, where, town_now)          # repair A4: from the roster, never from a town name alone
    avoid = name_words(avoid_names)                  # repair A6: every word of every name ("Elspeth Vale" covers "Elspeth")
    T = r["town"]
    out = ["The facts of your own life:", f"- You are {r['age']} years old."]
    s.update(given=True, status="resolved", id=r["id"], town=T, here=here, cur=list(cur) if cur else None, age=r["age"],
             sex=r.get("sex"), role=r["role"], role_now=role_now, kin=[], household=None, avoided=sorted(avoid))
    if r["years_town"] is not None:
        n = r["years_town"]
        if r["years_town_kind"] == "born":                                  # decision 8: counted from their own sentences
            away = r["age"] - n
            out.append((f"- You were born here in {T} and have lived here all your life, {n} years." if not away
                        else f"- You were born here in {T} and have lived here for {n} years: all your life except "
                             f"{away} {'year' if away == 1 else 'years'} away.") if here
                       else f"- You were born in {T} and lived there for {n} years." if cur is not None
                       else f"- You were born in {T} and have spent {n} years of your life there.")
        else:
            out.append(f"- You have lived here in {T} for {n} years." if here
                       else f"- You lived in {T} for {n} years." if cur is not None       # moved: past time
                       else f"- You have spent {n} years of your life in {T}.")         # where you are now: not known
        if here:
            out.append(band_line(n))
        s["years_town"] = {"n": n, "kind": r["years_town_kind"], "away": r["age"] - n if r["years_town_kind"] == "born" else None,
                           "band": out[-1] if here else None}
    else:
        s["years_town"] = {"n": None, "kind": r["years_town_kind"], "born_stated": r["years_town_kind"] == "born"}
        if r["years_town_kind"] == "born":
            out.append(f"- You were born here in {r['town']}." if here else f"- You were born in {r['town']}.")
        out.append(unknown(f"how many years you have lived in {r['town']}"))   # N8: "born here" is not a number of years
    if cur is not None and not here:       # moved: the years above are the old town's, never a time lived in this one
        now_town = where(r["id"])[1]
        out.append(unknown(f"how many years you have lived in {now_town}"))
        s["years_now"] = {"n": None, "town": now_town}
    same_role = role_now is None or str(role_now).lower() == str(r["role"]).lower()
    if same_role:
        out.append(f"- You have worked as {_a(r['role'])} for {r['years_trade']} years." if r["years_trade"] is not None
                   else unknown(f"how many years you have worked as {_a(r['role'])}"))
        s["years_trade"] = {"n": r["years_trade"], "stated": True}
    else:
        s["years_trade"] = {"n": None, "stated": False, "role_changed": True}
    kin = sorted(v["kin"], key=lambda k: (ORDER[k[0]], k[2].get("id", "")))
    has = {rel for rel, _, _ in kin}
    near = []          # Codex Code R1 F7: the living relatives placed, by map and town, where this person stands
    marital = {"married": "- You are married.", "widowed": "- You are widowed.", "single": "- You are not married.",
               "never": "- You have never married."}.get(r["marital"])
    if marital:
        out.append(marital)
    s["marital"] = r["marital"] if marital else None
    for rel, kind, o in kin:
        if kind == "real":
            at = where(o["id"]) if where else None
            if at is None:                       # not on the roster usable for this turn: no name, no role, no place
                out.append(f"- You have a {PLAIN[rel]} who is a townsperson; you do not speak of where they are just now.")
                s["kin"].append({"rel": rel, "kind": "real", "id": o["id"], "word": PLAIN[rel], "name": None, "alive": True,
                                 "age": None, "role": None, "lives": None})
                continue
            m2, t2, person = at
            same = cur is not None and (str(m2).lower(), str(t2 or "").lower()) == cur
            place = f"here in {t2}" if same else f"in {t2}"
            way = None
            if works and same:                             # same map and town as this person stands in, this turn
                mine = where(r["id"])
                if mine is not None and mine[2].get("home") is not None and person.get("home") is not None:
                    way = town_knowledge.how(person["home"][0] - mine[2]["home"][0], person["home"][1] - mine[2]["home"][1])
            work = "" if way is None else (f" {'He' if person['sex'] == 'man' else 'She'} works {way}"
                                           + ("." if way == "right beside you" else ", from where you work."))
            out.append(f"- Your {WORD[(rel, person['sex'])]} is {person['name']}, the {person['role']} {place}; {'he' if person['sex'] == 'man' else 'she'} is {o['age']}.{work}")
            s["kin"].append({"rel": rel, "kind": "real", "id": o["id"], "word": WORD[(rel, person["sex"])], "name": person["name"],
                             "alive": True, "age": o["age"], "role": person["role"], "lives": place, "town": t2})
            if way is not None:
                s["kin"][-1]["works"] = way
            if same:
                near.append(s["kin"][-1])
            continue
        word = WORD[(rel, o["sex"])]
        he = "he" if o["sex"] == "man" else "she"
        named = f"Your {word} {o['name']}" if o["name"].lower() not in avoid else f"Your {word}"
        entry = {"rel": rel, "kind": "off", "id": o["id"], "word": word, "name": o["name"] if o["name"].lower() not in avoid else None,
                 "name_withheld": o["name"].lower() in avoid, "alive": o["alive"], "fate": fates[o["fate"]]}
        s["kin"].append(entry)
        if not o["alive"]:
            out.append(f"- {named} is dead: {he} {fates[o['fate']]}. You do not know how old {he} was, or in what year it happened.")
            entry.update(age=None, lives=None)
            continue
        if o["place"] == "here":                 # stored as the person's own town (6.2b), rendered against where they stand
            local = here
            lives = f"lives here in {T}" if here else f"lives in {T}"
            if rel in ("spouse", "child") and (rel == "spouse" or o["age"] < 18):
                lives = "lives with you" if here else f"lives in {T}"
        elif o["place"] == "town":
            there = (str(r["map"] or "").lower(), str(o["place_town"] or "").lower())
            local = cur is not None and cur == there
            lives = f"lives here in {o['place_town']}" if local else f"lives in {o['place_town']}"
        else:                                     # "far" was chosen from the person's own town - say which
            local = False
            lives = "lives far from here" if here else f"lives far from {T}"
        extra = " (she is married)" if o.get("married_here") else ""
        out.append(f"- {named}, {o['age']}{extra}: {he} {fates[o['fate']]}; {he} {lives}.")
        entry.update(age=o["age"], lives=lives, place=o["place"], place_town=o.get("place_town"), married=bool(o.get("married_here")))
        if local:
            near.append(entry)
    if kin_here and cur is not None and not any(k["kind"] == "real" and k["name"] is None for k in s["kin"]):
        # reading batch 2026-11-19: a resident who had moved named a sister in the OLD town as family "here" - so the
        # line is written wherever the roster places this person, about that town (their own town's name when at home)
        Tn = T if here else where(r["id"])[1]
        said = [f"your {k['word']} {k['name']}" if k["name"] else f"your {k['word']}" for k in near]
        out.append(f"- You have no kin living here in {Tn}." if not said else
                   f"- All your kin here in {Tn}: " + (", ".join(said[:-1]) + " and " if len(said) > 1 else "") + said[-1] + ".")
        s["kin_here"] = [k["id"] for k in near]
    got = []
    shares = household(v, r, here, where, avoid, got)
    if shares:
        out.append(shares)
    s["household"] = {"stated": bool(shares), "alone": shares == "- You live alone.", "with": got}
    s["no_children"] = "child" not in has and r["marital"] in ("married", "widowed", "single", "never")
    if s["no_children"]:
        out.append("- You have no children.")
    s["no_siblings"] = "sibling" not in has
    if s["no_siblings"]:
        out.append("- You have no brothers or sisters.")
    s["home_line"] = homes[r["home_line"]] if r["home_line"] in homes and here else None
    if s["home_line"]:
        out.append(f"- You live in {homes[r['home_line']]}.")
    bg = backgrounds.text(r["bg"], cur[1] if cur else "", here=here, same_role=same_role) if r.get("bg") else None
    s["background"] = bg or None
    if bg:
        out.append("- Your past: " + bg)
    fig = figurative_line(nature_now)
    s["figurative"] = fig or None
    if fig:
        out.append(fig)
    out.append(RULES)
    s["unsupported"] = list(r["unsupported"] or [])
    if r["unsupported"]:
        out.append("If asked to say more about anyone in your past who is not listed above, say only that it was long ago and you "
                   "would rather not go into it.")
    if r["habit"] == "exaggerates":
        out.append("Your habit of exaggerating never applies to your own age, your family or your years - those you state exactly.")
    if r["habit"] == "years" and years_habit_allowed(r) and here and same_role:
        out.append("When you mention how long you have lived or worked here, use only the numbers above.")
    out.append(NOW_AND_THEN)
    if arrangement == "B" and held_back:
        out.append(HOLD_BACK)
    s["held_back"] = bool(arrangement == "B" and held_back)
    s["habit"] = r["habit"]
    return "\n".join(out)
