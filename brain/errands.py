"""Errands build 1, stage 5 (design v5 3.1, 3.3, 3.5, 3.7, 3.9): the brain's fixed code for an errand - who may be asked
to carry what to whom (the picker), the CURRENT PROOF every step needs, and the readiness reply. No model is called and
nothing is written here. The shard sends ids and keys only; this module looks everything else up in the records held
now, re-runs the route's own rules, and only then hashes. Anything unsure returns None: no errand is asked, accepted,
delivered or paid on a guess.

A proof is a dict: the request's fields echoed, the data generation, the fact digest, and the code-written sentence(s)
for that step. The shard accepts it only if the echo, both generations, the town-list stamp and the digest match."""
import hashlib
import os

import errands_people
import errands_text
import people_store

BOOT_ID = os.urandom(8).hex()        # new at every brain start: part of the data generation (3.1)
HUB_TRADES = ("innkeeper", "tavernkeeper", "barkeeper", "provisioner", "banker")   # 3.5, in this fixed order
INVERSE = {"spouse": "spouse", "parent": "child", "child": "parent", "sibling": "sibling", "cousin": "cousin"}
STEPS = ("ask", "accept", "deliver", "return", "remind")
MIN_VISIT_DAYS = 2                   # decision 11


def _unit(*parts):
    return int(hashlib.sha256("|".join(str(p) for p in parts).encode("utf-8")).hexdigest()[:12], 16)


def _pick(rows, *seed):
    return rows[_unit(*seed) % len(rows)] if rows else None


def _trade_key(role):
    return "".join(str(role or "").lower().split())


def data_generation(tables, lists):
    """3.1: a hash of the boot id, the two table fingerprints and the lists version."""
    return hashlib.sha256("|".join([BOOT_ID, str(tables["cousin"]["fingerprint"]), str(tables["contact"]["fingerprint"]),
                                    str(lists["lists_version"])]).encode()).hexdigest()[:16]


def readiness(tables, lists, held_hash, stamp):
    """3.9: the readiness reply - values already held in memory; no model, no Ollama, no table read."""
    return {"errands": 1, "boot": BOOT_ID, "data_gen": data_generation(tables, lists),
            "stamp_ok": bool(stamp) and stamp == held_hash}


def may_ask(dial):
    """3.3 step 2: talked on two different days, and regard not cold. `dial` = dials.compute() for this pair, or None."""
    if not dial:
        return False
    import dials
    return (dial.get("familiarity", 0) >= MIN_VISIT_DAYS
            and dials.short_band(dial["warmth"], "warmth") != dials.SHORT["warmth"][0]
            and dials.short_band(dial["trust"], "trust") != dials.SHORT["trust"][0])


class World:
    """What one proof may look at: the ledger (stored lives), the town list usable for THIS turn (or None), the state of
    the two added tables, the approved lists, which towns neighbour which, and names that must not be used this turn."""

    def __init__(self, ledger, held, tables, lists, neighbours=None, avoid=()):
        self.ledger, self.held, self.tables, self.lists = ledger, held, tables, lists
        self.neighbours = neighbours or (lambda a, b: False)
        self.avoid = people_store.name_words(avoid)

    def at(self, pid):
        """(map, town, person) for a seated ordinary resident on the usable town list, else None."""
        if self.held is None:
            return None
        got = self.held.where(pid)
        return got if got is not None and got[2].get("kind") == "resident" else None


# ---- the routes: each returns the facts of ONE candidate, or None. Re-run by every proof (3.5). ----------

def _kin(w, sender, receiver):
    v = people_store.view(w.ledger, sender)
    s, r = w.at(sender), w.at(receiver)
    if v is None or v["record"].get("status") != "resolved" or s is None or r is None:
        return None
    tie = next((rel for rel, kind, o in v["kin"] if kind == "real" and o.get("id") == receiver), None)
    if tie is None or (s[0], s[1]) != (r[0], r[1]):                   # a stored tie; both in one town, now
        return None
    return {"rel": tie, "edge": ["tie", tie, sender, receiver], "about": None, "band": "same"}


def _cousin(w, sender, receiver):
    if not errands_people.usable(w.tables, "cousin") or errands_people.cousin_of(w.ledger, sender) != receiver:
        return None
    s, r = w.at(sender), w.at(receiver)
    if s is None or r is None or s[0] != r[0] or s[1] == r[1]:        # one map, different towns, now
        return None
    a, b = sorted((sender, receiver))
    return {"rel": "cousin", "edge": ["cousin", a, b], "about": None,
            "band": "near" if w.neighbours(s[1], r[1]) else "far"}


def standin_for(w, map_, town):
    """The stand-in in a town: a resident with a hub trade, in the fixed order of trades, lowest id within a trade, by
    the CURRENT town-list trade. None if the town has none."""
    people = [p for p in w.held.people(map_, town) if p.get("kind") == "resident"] if w.held is not None else []
    for trade in HUB_TRADES:
        ids = sorted(p["id"] for p in people if _trade_key(p["role"]) == trade)
        if ids:
            return ids[0]
    return None


def _standin(w, sender, receiver, about_id):
    v = people_store.view(w.ledger, sender)
    s = w.at(sender)
    if v is None or v["record"].get("status") != "resolved" or s is None:
        return None
    rec = v["record"]
    if (s[0], s[1]) != (rec.get("map"), rec.get("town")):             # only while the sender stands in their own home town
        return None
    hit = next(((rel, o) for rel, kind, o in v["kin"] if kind == "off" and o.get("id") == about_id), None)
    if hit is None:
        return None
    rel, o = hit
    town = o.get("place_town")
    if not o.get("alive") or o.get("place") != "town" or not town or str(town).lower() == str(rec["town"]).lower():
        return None
    if str(o.get("name", "")).lower() in w.avoid or not o.get("name"):       # a name withheld this turn
        return None
    there = w.held.people(rec["map"], town)
    if not there or any(p["name"].lower() == o["name"].lower() for p in there):   # nobody there shares the name
        return None
    if standin_for(w, rec["map"], town) != receiver or w.at(receiver) is None:
        return None
    return {"rel": rel, "edge": ["tie", rel, sender, about_id],
            "about": (o["id"], o["name"], people_store.WORD[(rel, o["sex"])], True, town), "off": o,
            "band": "near" if w.neighbours(s[1], town) else "far"}


def _contact(w, sender, receiver):
    row = errands_people.contact_of(w.ledger, sender) if errands_people.usable(w.tables, "contact") else None
    s, r = w.at(sender), w.at(receiver)
    if row is None or row["contact"] != receiver or s is None or r is None:
        return None
    if s[0] != r[0] or s[1] == r[1] or _trade_key(s[2]["role"]) != _trade_key(r[2]["role"]):   # now, not as built
        return None
    return {"rel": None, "edge": ["contact", sender, receiver], "about": None, "band": "none"}


def route_facts(w, kind, route, sender, receiver, about_id=None):
    """Re-runs one route's rules on the records held NOW. None = the route is not true (any more)."""
    if (kind, route) not in errands_text.KIND_ROUTES or not sender or not receiver or sender == receiver:
        return None
    if route == "kin":
        return _kin(w, sender, receiver)
    if route == "cousin":
        return _cousin(w, sender, receiver)
    if route == "standin":
        return _standin(w, sender, receiver, about_id)
    return _contact(w, sender, receiver)


# ---- the picker (3.3 step 2) ------------------------------------------------------------------------------

def candidates(w, sender, intro_offered=False, intro_blocked=()):
    """Every (kind, route, receiver, about id) this sender could ask for right now, in a stable order."""
    out = []
    v = people_store.view(w.ledger, sender)
    if v is None or v["record"].get("status") != "resolved" or w.at(sender) is None:
        return out
    for rel, kind, o in sorted(v["kin"], key=lambda k: (people_store.ORDER.get(k[0], 9), str(k[2].get("id")))):
        if kind == "real":
            if _kin(w, sender, o["id"]):
                out += [("word", "kin", o["id"], None), ("ask_after", "kin", o["id"], None)]
        else:
            rec = v["record"]
            receiver = standin_for(w, rec.get("map"), o.get("place_town")) if o.get("place") == "town" else None
            if receiver and _standin(w, sender, receiver, o["id"]):
                out.append(("word", "standin", receiver, o["id"]))
    cousin = errands_people.cousin_of(w.ledger, sender) if errands_people.usable(w.tables, "cousin") else None
    if cousin and _cousin(w, sender, cousin):
        out += [("word", "cousin", cousin, None), ("ask_after", "cousin", cousin, None)]
    row = errands_people.contact_of(w.ledger, sender) if errands_people.usable(w.tables, "contact") else None
    if row and not intro_offered and row["contact"] not in set(intro_blocked) and _contact(w, sender, row["contact"]):
        out.append(("introduce", "contact", row["contact"], None))
    return out


def pick(w, sender, seed, intro_offered=False, intro_blocked=()):
    """One candidate and its message line, chosen by `seed` (the ticket id), or None."""
    c = _pick(candidates(w, sender, intro_offered, intro_blocked), "candidate", seed)
    if c is None:
        return None
    kind, route, receiver, about_id = c
    line_key = None
    if kind == "word":
        facts = route_facts(w, kind, route, sender, receiver, about_id)
        relation = facts["about"][2] if route == "standin" else _word(w, facts, receiver)
        line_key = _pick(errands_text.messages_for(w.lists, route, relation), "message", seed)["key"]
    return {"kind": kind, "route": route, "receiver": receiver, "about": about_id, "line_key": line_key}


# ---- the proof (3.1) --------------------------------------------------------------------------------------

def _word(w, facts, receiver):
    """What the receiver is to the sender, in a word."""
    return people_store.WORD[(facts["rel"], w.at(receiver)[2]["sex"])]


def _values(w, kind, route, sender, receiver, facts):
    """The slot values every sentence of this errand is filled from - CURRENT names, trades and towns (3.7)."""
    s, r = w.at(sender), w.at(receiver)
    vals = {"S": s[2]["name"], "S_town": s[1], "R": r[2]["name"], "a_trade": errands_text.a_trade(r[2]["role"]),
            "town": r[1]}
    subject_sex = r[2]["sex"]
    if route == "standin":
        o = facts["off"]
        vals.update(O=o["name"], O_rel=facts["about"][2], town=facts["about"][4])
        subject_sex = o["sex"]
    elif facts["rel"]:
        vals.update(rel=_word(w, facts, receiver),
                    rel_back=people_store.WORD[(INVERSE[facts["rel"]], s[2]["sex"])])
    vals.update(errands_text.pronouns(subject_sex))
    return vals, subject_sex


def prove(w, req, final_check=None):
    """One step's proof, or None. `req`: step, kind, route, sender, receiver, about, line_key, seed (the ticket or row
    id, which chooses among the wordings), and on a return the answer {key, hash, digest}. Step "remind" (design v6
    12.2) adds `role` and, for the relative, the same stored answer; it draws nothing. `final_check(text)` is the
    brain's last check on any spoken line; a sentence that fails it means the step does not happen (3.7)."""
    try:
        return _prove(w, req, final_check or (lambda text: True))
    except (errands_text.Refused, KeyError, TypeError, ValueError, AttributeError):
        return None


def _prove(w, req, ok):
    step, kind, route = req.get("step"), req.get("kind"), req.get("route")
    sender, receiver, about_id = req.get("sender"), req.get("receiver"), req.get("about")
    seed, line_key = req.get("seed"), req.get("line_key")
    if step not in STEPS or w.held is None:
        return None
    facts = route_facts(w, kind, route, sender, receiver, about_id)
    if facts is None:
        return None
    lists = w.lists
    line_hash = None
    if kind == "word":
        entry = errands_text.by_key(lists, line_key)
        relation = facts["about"][2] if route == "standin" else _word(w, facts, receiver)
        if entry is None or entry not in errands_text.messages_for(lists, route, relation):
            return None                                   # the line key must exist and still fit this errand
        line_hash = errands_text.text_hash(entry)
    elif line_key is not None:
        return None
    s, r = w.at(sender), w.at(receiver)
    digest = errands_text.fact_digest(kind=kind, route=route, sender=(sender, s[2]["name"]),
                                      receiver=(receiver, r[2]["name"]),
                                      about=facts["about"], edge=facts["edge"], line_key=line_key, line_hash=line_hash,
                                      lists_version=lists["lists_version"])
    vals, subject_sex = _values(w, kind, route, sender, receiver, facts)
    out = {"step": step, "kind": kind, "route": route, "sender": sender, "receiver": receiver, "about": about_id,
           "line_key": line_key, "line_hash": line_hash, "lists_version": lists["lists_version"], "digest": digest,
           "data_gen": data_generation(w.tables, lists), "band": facts["band"],
           "receiver_map": r[0], "receiver_town": r[1], "sender_map": s[0], "sender_town": s[1]}

    def say(group, name=None, **more):
        rows = lists[group][name] if name else lists[group]
        if more.get("reported") is not None:
            rows = errands_text.returns_for(lists, more["reported"])
        text = errands_text.fill(_pick(rows, group, seed)["text"], dict(vals, **more), lists["speech_max"])
        if not ok(text):
            raise errands_text.Refused("final check")
        return text

    if step in ("ask", "accept"):
        if kind == "word":
            vals["msg"] = errands_text.message_text(lists, line_key, subject_sex)
        if step == "ask":
            out["ask_line"] = say("ask", f"{kind}.{route}")
            out["no_line"] = say("no")
        out["yes_line"] = say("yes")
        return out
    if step == "deliver":
        if kind == "ask_after":
            answer = _pick(lists["answer"], "answer", seed)
            said, _ = errands_text.answer_forms(lists, answer["key"], r[2]["name"])
            out["line"] = say("deliver", "ask_after", said=said)
            out["answer"] = _answer(lists, answer, receiver, r)
        else:
            out["line"] = say("deliver", {"word": "word.standin" if route == "standin" else "word.kin",
                                          "introduce": "introduce"}[kind])
        return out
    if step == "remind":
        return _remind(w, req, out, vals, subject_sex, say, r)
    # step == "return": only a round trip has one
    if kind != "ask_after":
        return None
    given = req.get("answer") or {}
    entry = errands_text.by_key(lists, given.get("key"))
    if (entry is None or "said" not in entry
            or not all(isinstance(given.get(k), str) and len(given[k]) == 64 for k in ("hash", "digest"))):
        return None                                       # malformed, or not an answer: a refusal, not a thanks
    now = _answer(lists, entry, receiver, r)
    if now["hash"] == given["hash"] and now["digest"] == given["digest"]:
        _, reported = errands_text.answer_forms(lists, entry["key"], r[2]["name"])
        out["line"], out["answer_changed"] = say("return", reported=reported), False
    else:                                                 # the answer changed: neither repeated nor rebuilt (3.7)
        out["line"], out["answer_changed"] = say("thanks"), True
    return out


def _remind(w, req, out, vals, subject_sex, say, r):
    """Design v6 12.2 (Codex "Errands-Redesign" RD1): the sentence that was spoken, built again for a comparison the
    SHARD makes against the hash it stored when the sentence was said. Nothing is drawn and nothing is written: the
    wording is chosen by the seed the request carries (the row's stored ask seed; for the relative, the row id the
    outward delivery used), and the relative's answer is the one the row holds, never a new pick.
    role "sender": the ask, as on the ask step. role "receiver": a round trip's outward delivery sentence - only while
    the stored answer still binds (key, text hash, answer digest); otherwise the proof comes back with NO line and
    answer_changed set, and the shard says nothing of substance."""
    kind, route, role = out["kind"], out["route"], req.get("role")
    lists = w.lists
    if role == "sender":
        if kind == "word":
            vals["msg"] = errands_text.message_text(lists, out["line_key"], subject_sex)
        out["line"] = say("ask", f"{kind}.{route}")
        return out
    if role != "receiver" or kind != "ask_after":
        return None
    given = req.get("answer") or {}
    entry = errands_text.by_key(lists, given.get("key"))
    if (entry is None or "said" not in entry
            or not all(isinstance(given.get(k), str) and len(given[k]) == 64 for k in ("hash", "digest"))):
        return None
    now = _answer(lists, entry, out["receiver"], r)
    if now["hash"] != given["hash"] or now["digest"] != given["digest"]:
        out["answer_changed"] = True                      # no line: it can no longer be said exactly as it was
        return out
    said, _ = errands_text.answer_forms(lists, entry["key"], r[2]["name"])
    out["line"], out["answer_changed"] = say("deliver", "ask_after", said=said), False
    return out


def _answer(lists, entry, receiver, r):
    """3.7: what is fixed on the row when the relative gives the answer - key, text hash, and the answer digest over
    the receiver's id and name, the trade and town exactly as named, the map, the key and the hash."""
    h = errands_text.text_hash(entry)
    return {"key": entry["key"], "hash": h,
            "digest": errands_text.answer_digest(receiver_id=receiver, receiver_name=r[2]["name"],
                                                 trade=errands_text.a_trade(r[2]["role"]), town=r[1], map_name=r[0],
                                                 answer_key=entry["key"], answer_hash=h)}
