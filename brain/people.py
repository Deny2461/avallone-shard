"""Townsfolk step, Stage 2 - people: ages, years, family (design §6). First built as a prototype for a measurement
run; reviewed by Codex and live since November 2026, behind the "people" switch (off unless set).

What it does: for one town's residents it works out, from the sentences each one ALREADY has (background, personality,
habit, pet subject - through the constraint tags in people_tags.json), a profile; then an age, years in town and at the
trade; then small units of real townspeople (U1 a married pair, U2 a pair and a grown child, U3 a parent and a grown
child, U4 two siblings); then everyone's off-stage family, each with a fate line and a place; then households.
`audit()` checks the result against every rule of design §6.2a (N1-N11) and against every profile. A town whose audit
is not empty must not be stored.

Everything is a pure function of (the roster's people, their backgrounds, the data files): no clock, no random module,
no model, no network. Choice is by SHA-256 of ids, as in nature.py and backgrounds.py.

v0 simplifications, each a place where "targets yield" (§6.5) - all reported by `summary()`:
- a resident whose sentences fix its siblings (youngest of seven, a younger brother, a married sister in town) or say
  its parents are dead is never made the CHILD or SIBLING of a real townsperson; its family is off-stage;
- a real child must be born in the town, and a parent must have been in the town since before the child was born;
- two real siblings must both be born here, or share the same "came from" sentence;
- a living off-stage husband or wife lives in the speaker's town.
"""
import hashlib
import json
import math
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
_DATA = {}


def data():
    if not _DATA:
        for key, name in (("tags", "people_tags.json"), ("lists", "people_lists.json"), ("names", "people_names.json"),
                          ("rules", "people_rules.json")):
            with open(os.path.join(HERE, name), encoding="utf-8") as f:
                _DATA[key] = json.load(f)
        _DATA["tags"] = _DATA["tags"]["tags"]          # the file also carries its vocabulary and a note
        _DATA["known"] = _known_ids()
    return _DATA


def _known_ids():
    """Repair B2 (Codex Method R1 F11): every id the catalogue really holds, by kind - read from the catalogue files
    themselves, not from the tag file (an id with no tags is valid; an id that is not in the catalogue is not)."""
    with open(os.path.join(HERE, "backgrounds.json"), encoding="utf-8") as f:
        b = json.load(f)
    with open(os.path.join(HERE, "nature.json"), encoding="utf-8") as f:
        n = json.load(f)
    return known_from(b, n)


def known_from(b, n):
    """The known ids of parsed backgrounds.json (`b`) and nature.json (`n`) - also used by the B3 catalogue freeze."""
    return {"home": {e["id"] for e in b.get("homes") or []}, "life": {e["id"] for e in b.get("lives") or []},
            "event": {e["id"] for e in b.get("events") or []},
            "other": {"personality:" + e["key"] for e in n.get("personalities") or []}
                     | {"habit:" + e["key"] for e in n.get("habits") or []} | {"habit:"}
                     | {"subject:" + x for x in n.get("subjects") or []} | {"subject:"}}


TAG_INT = ("minage", "maxage", "siblings_exact", "siblings_min", "kin_min", "former_years", "former_years_min", "years_trade_min",
           "away_years")
TAG_ENUM = {"home": ("here", "elsewhere"), "arrived": ("child", "young_adult"), "parents": ("dead",), "birth_order": ("youngest",),
            "sibling": ("younger_brother",), "kin_here": ("married_sister",), "marital": ("never", "unplaced")}
TAG_TRUE = ("free", "absence", "family_trade", "before_trade", "trade_only", "years_town_open")


def _check_tag(i, k, v):
    """Repair B2: a tag value outside its type, range or allowed words makes the person unresolved - never a default."""
    ok = (k in TAG_INT and isinstance(v, int) and not isinstance(v, bool) and 0 <= v <= 120) or \
         (k in TAG_ENUM and v in TAG_ENUM[k]) or (k in TAG_TRUE and v is True) or \
         (k == "unsupported" and isinstance(v, str) and v.strip() != "")
    if not ok:
        raise Unresolved(f"malformed tag {k} on {i}" if k in TAG_INT or k in TAG_ENUM or k in TAG_TRUE or k == "unsupported"
                         else f"unknown tag {k} on {i}")


def u(*parts):
    """A stable number in [0, 1) from the parts."""
    h = hashlib.sha256("|".join(("avallone-people",) + tuple(str(p) for p in parts)).encode("utf-8")).digest()
    return int.from_bytes(h[:8], "big") / 2.0 ** 64


def ordered(items, *salt, key=lambda x: x):
    return sorted(items, key=lambda x: (u(key(x), *salt), key(x)))


def weighted(weights, *parts):
    """Index into `weights` chosen by the stable number."""
    x, acc = u(*parts) * sum(weights), 0.0
    for i, w in enumerate(weights):
        acc += w
        if x < acc:
            return i
    return len(weights) - 1


def band(bands, age):
    for lo, hi, v in bands:
        if lo <= age <= hi:
            return v
    return bands[-1][2]


class Unresolved(Exception):
    pass


MOTHER_MAX, FATHER_MAX = 45, 65     # oldest a parent may be when a child is born - plain sense, not one of N1-N11


BLANK = {"minage": 18, "maxage": 80, "home": None, "absence": False, "arrived": None, "parents_dead": False,
         "sib_exact": None, "sib_min": 0, "youngest": False, "younger_brother": False, "sister_here": False,
         "kin_min": 0, "marital": None, "family_trade": False, "former": 0, "former_before": False, "trade_only": False,
         "trade_min": 0, "away": 0, "absence_open": False, "years_open": False}


def profile(ids):
    """Everything this resident's existing sentences fix, as one dict - or Unresolved(why). `ids` are catalogue ids:
    H.., L.., E.., personality:<key>, habit:<key>, subject:<text>. An id with no tags fixes nothing."""
    tags, known = data()["tags"], data()["known"]
    p = dict(BLANK, unsupported=[], sources={})
    homes = {}
    for i in ids:
        if not any(i in known[kind] for kind in known):
            raise Unresolved(f"unknown-id:{i}")             # repair B2: a removed or unknown entry is never "no tags"
        entry = tags[i] if i in tags else {}                 # Codex Method R2 F9: only an ABSENT entry means "no tags"
        if not isinstance(entry, dict):
            raise Unresolved(f"malformed tags on {i}")
        for k, v in entry.items():
            _check_tag(i, k, v)
            p["sources"].setdefault(k, []).append(i)
            if k == "free":
                continue
            if k == "home":
                if v not in ("here", "elsewhere"):
                    raise Unresolved(f"malformed tag on {i}")
                homes[v] = i
            elif k == "minage":
                p["minage"] = max(p["minage"], int(v))
            elif k == "maxage":
                p["maxage"] = min(p["maxage"], int(v))
            elif k == "absence":
                p["absence"] = True
                if "away_years" not in entry:
                    p["absence_open"] = True        # time away of no stated length: years in town cannot be counted
            elif k == "away_years":
                if not entry.get("absence"):
                    raise Unresolved(f"away-years-without-absence on {i}")
                p["away"] += int(v)                 # decision 8: a stated stretch away
            elif k == "years_town_open":
                p["years_open"] = True              # decision 8: the sentence leaves time in town open (H06)
            elif k == "arrived":
                p["arrived"] = v
            elif k == "parents":
                p["parents_dead"] = v == "dead"
            elif k == "siblings_exact":
                p["sib_exact"] = int(v)
            elif k == "siblings_min":
                p["sib_min"] = max(p["sib_min"], int(v))
            elif k == "birth_order":
                p["youngest"] = v == "youngest"
            elif k == "sibling":
                p["younger_brother"] = v == "younger_brother"
            elif k == "kin_here":
                p["sister_here"] = v == "married_sister"
            elif k == "kin_min":
                p["kin_min"] = max(p["kin_min"], int(v))
            elif k == "marital":
                p["marital"] = "never" if "never" in (v, p["marital"]) else "unplaced"
            elif k == "family_trade":
                p["family_trade"] = True
            elif k in ("former_years", "former_years_min"):
                p["former"] = max(p["former"], int(v))
            elif k == "before_trade":
                p["former_before"] = True
            elif k == "trade_only":
                p["trade_only"] = True
            elif k == "years_trade_min":
                p["trade_min"] = max(p["trade_min"], int(v))
            elif k == "unsupported":
                p["unsupported"].append(str(v))
            else:
                raise Unresolved(f"unknown tag {k} on {i}")
    if len(homes) > 1:
        raise Unresolved("home-here-and-elsewhere:" + "+".join(sorted(homes.values())))
    p["home"] = next(iter(homes), None)
    if p["youngest"] and p["younger_brother"]:
        raise Unresolved("youngest-yet-has-a-younger-brother")
    if p["trade_only"] and p["former"]:
        raise Unresolved("only-this-trade-yet-years-at-another")
    if p["sib_exact"] is not None and p["sib_exact"] < p["sib_min"]:
        raise Unresolved("sibling-counts-disagree")
    if p["minage"] > p["maxage"]:
        raise Unresolved("no-age-fits")
    return p


def pin_nature(pid, natural, bg_ids, personalities):
    """(personality key, profile) for this resident: the natural pick if its tags fit the rest, else the next
    personality in this NPC's own order that does (§6.4). `personalities` = [(key, weight)], `natural` = nature.of()."""
    rest = list(bg_ids) + ["habit:" + natural["habit"], "subject:" + natural["subject"]]
    first = None
    order = sorted((k for k, w in personalities if w > 0),
                   key=lambda k: -math.log(max(_nature_unit(pid, "personality", k), 1e-300)) / dict(personalities)[k])
    for key in [natural["key"]] + [k for k in order if k != natural["key"]]:
        try:
            return key, profile(rest + ["personality:" + key])
        except Unresolved as e:
            if first is None:
                first = e
            if not str(e).startswith("home-here-and-elsewhere") or "personality:" not in str(e):
                raise        # not a clash a different personality can cure
    raise first


def _nature_unit(*parts):
    h = hashlib.sha256("|".join(("avallone-nature",) + tuple(str(p) for p in parts)).encode("utf-8")).digest()
    return (int.from_bytes(h[:8], "big") + 1) / (2.0 ** 64 + 2)


def choose_age(pid, lo, hi, bands):
    ages = list(range(lo, hi + 1))
    return ages[weighted([band(bands, a) for a in ages], pid, "age")]


def set_years(r, R):
    """Years in town (N8) and at the trade (N9-N11) into the record. None = unknown.
    Born here (decision 8, the owner, chat, 2026-11-17, replacing N8's): the number comes from
    the person's own sentences - their whole life, less any stated stretch away; unknown when a sentence leaves it open
    or puts them away for a stretch of no stated length."""
    p, age, pid = r["prof"], r["age"], r["id"]
    r["years_town"], r["years_town_kind"] = None, "unknown"
    if p["home"] == "here":
        r["years_town_kind"] = "born"
        if not p["absence_open"] and not p["years_open"] and age - p["away"] >= 1:
            r["years_town"] = age - p["away"]
    elif p["home"] == "elsewhere" and not p["absence"]:
        lo, hi = {"child": (3, 13), "young_adult": (16, 25)}.get(p["arrived"], (14, age))
        hi = min(hi, age)
        if lo <= hi:
            arrival = lo + int(u(pid, "arrival") * (hi - lo + 1))
            r["years_town"], r["years_town_kind"] = max(0, min(age - 1, age - arrival)), "number"
    former = p["former"] if p["former_before"] else 0
    most = age - 14 - former
    r["years_trade"] = None
    if p["trade_only"]:
        y = age - (14 + int(u(pid, "tradestart") * 5))
        r["years_trade"] = y if p["trade_min"] <= y <= age - 14 else None
    elif most >= p["trade_min"]:
        r["years_trade"] = p["trade_min"] + int(u(pid, "tradeyears") * (most - p["trade_min"] + 1))


def _role_minage(role, R):
    for pattern, n in R["role_minage"]:
        if re.search(pattern, (role or "").lower()):
            return n
    return 18


def build_town(map_, town, people, backgrounds, natures, personalities, other_towns=(), rules=None, origins=None):
    """One town. people: roster persons of kind 'resident' ({id, name, role, sex, ...}); backgrounds: {id: {home, life,
    event}} (stored or prospective; a resident without one gets no background constraints); natures: {id: nature.of(id)};
    personalities: [(key, weight)]; other_towns: town names a relative may live in; origins: {id: the town a
    "came from" sentence names} - that person's parents and siblings mostly live there. Returns the whole result as a dict
    of plain data: records, offstage, ties, units, households, log."""
    R = rules or data()["rules"]
    L = data()["lists"]
    recs, log = {}, []
    for p in people:
        bg = backgrounds.get(p["id"])
        r = {"id": p["id"], "name": p["name"], "role": p["role"], "sex": p["sex"], "map": map_, "town": town, "bg": bg,
             "habit": natures[p["id"]]["habit"], "subject": natures[p["id"]]["subject"], "unit": None, "unit_kind": None,
             "status": "resolved", "reason": None, "age": None, "marital": None, "household": None, "home_line": None}
        try:
            if bg is not None:                              # repair B2: a background is whole and of the right kinds, or unresolved
                known = data()["known"]
                if not isinstance(bg, dict) or any(not bg.get(k) for k in ("home", "life", "event")):
                    raise Unresolved("background-incomplete")
                for k in ("home", "life", "event"):
                    if bg[k] not in known[k]:
                        raise Unresolved(f"background-{k}-unknown:{bg[k]}")
            r["personality"], r["prof"] = pin_nature(p["id"], natures[p["id"]], [bg["home"], bg["life"], bg["event"]] if bg else [],
                                                     personalities)
            if r["personality"] != natures[p["id"]]["key"]:
                log.append(("repinned", p["id"], natures[p["id"]]["key"], r["personality"]))
            lo, hi = max(r["prof"]["minage"], _role_minage(p["role"], R)), r["prof"]["maxage"]
            if lo > hi:
                raise Unresolved("no-age-fits-role")
            r["age"] = choose_age(p["id"], lo, hi, R["age_bands"])
            set_years(r, R)
        except Unresolved as e:
            r.update(status="unresolved", reason=str(e), personality=natures[p["id"]]["key"], prof=None,
                     years_town=None, years_town_kind="unknown", years_trade=None)
            log.append(("unresolved", p["id"], str(e)))
        recs[p["id"]] = r
    units = _form_units(map_, town, recs, R, log)
    off, ties = {}, []
    fam = _Family(map_, town, recs, off, ties, R, L, data()["names"], [t for t in other_towns if t != town], log,
                  {p["name"] for p in people}, origins or {})
    fam.build(units)
    households = _households(recs, units, off, ties, R, L)
    return {"map": map_, "town": town, "rules_version": R["version"], "records": recs, "offstage": off, "ties": ties,
            "units": units, "households": households, "log": log}


# ---------------------------------------------------------------------------------------------------- real units
def _sib_free(p):
    return not (p["sib_exact"] is not None or p["youngest"] or p["younger_brother"] or p["sister_here"])


def _can_spouse(a, b):
    return a["name"] != b["name"] and a["prof"]["marital"] is None and b["prof"]["marital"] is None and abs(a["age"] - b["age"]) <= 12


def _can_parent(par, ch):
    pp, cp = par["prof"], ch["prof"]
    if par["name"] == ch["name"] or par["age"] < ch["age"] + 18 or pp["marital"] is not None:
        return False
    if par["age"] - ch["age"] > (MOTHER_MAX if par["sex"] == "woman" else FATHER_MAX):
        return False
    if cp["parents_dead"] or not _sib_free(cp) or cp["sib_min"] or cp["home"] != "here":
        return False
    return pp["home"] == "here" or (par["years_town"] is not None and par["years_town"] >= ch["age"])


def _can_sibling(a, b):
    ap, bp = a["prof"], b["prof"]
    if a["name"] == b["name"] or abs(a["age"] - b["age"]) > 15 or not _sib_free(ap) or not _sib_free(bp):
        return False
    if ap["parents_dead"] != bp["parents_dead"]:
        return False
    if ap["home"] == "here" and bp["home"] == "here":
        return True
    return bool(a["bg"] and b["bg"] and ap["home"] == "elsewhere" and a["bg"]["home"] == b["bg"]["home"])


def _form_units(map_, town, recs, R, log):
    free = lambda r: r["status"] == "resolved" and r["unit"] is None
    cand = ordered([r for r in recs.values() if r["status"] == "resolved"], map_, town, "unit", key=lambda r: r["id"])
    target = R["real_kin_share"] * len(recs)
    quota = {k: int(round(target * share / size)) for (k, size), share in
             zip((("U1", 2), ("U2", 3), ("U3", 2), ("U4", 2)), (R["unit_mix_by_people"][k] for k in ("U1", "U2", "U3", "U4")))}
    units, pair_no = [], [0]

    def pair_ok(a, b):
        if not _can_spouse(a, b):
            return False
        same = u(map_, town, "pairmix", pair_no[0]) < R["mixed_pair_share"]
        return (a["sex"] == b["sex"]) == same

    def make(kind, members, roles):
        uid = kind + "-" + hashlib.sha256("|".join(sorted(m["id"] for m in members)).encode()).hexdigest()[:12]
        for m in members:
            m["unit"], m["unit_kind"] = uid, kind
        units.append({"id": uid, "kind": kind, "members": [m["id"] for m in members], "roles": roles})

    for kind in ("U2", "U3", "U4", "U1"):          # the hardest shapes first, while the most people are still free
        made = 0
        for a in cand:
            if made >= quota[kind]:
                break
            if not free(a):
                continue
            if kind == "U1":
                b = next((b for b in cand if b is not a and free(b) and pair_ok(a, b)), None)
                if b is None and u(map_, town, "pairmix", pair_no[0]) < R["mixed_pair_share"]:
                    # wanted two men or two women and found none: the mix yields to an ordinary pair, never the reverse
                    b = next((b for b in cand if b is not a and free(b) and _can_spouse(a, b) and a["sex"] != b["sex"]), None)
                if b:
                    make("U1", [a, b], {a["id"]: "spouse", b["id"]: "spouse"}); pair_no[0] += 1; made += 1
            elif kind == "U4":
                b = next((b for b in cand if b is not a and free(b) and _can_sibling(a, b)), None)
                if b:
                    make("U4", [a, b], {a["id"]: "sibling", b["id"]: "sibling"}); made += 1
            elif kind == "U3":      # a = the child
                b = next((b for b in cand if b is not a and free(b) and _can_parent(b, a)), None)
                if b:
                    make("U3", [b, a], {b["id"]: "parent", a["id"]: "child"}); made += 1
            else:                   # U2: a = the child; two parents who can also be each other's spouse
                found = None
                for b in cand:
                    if b is a or not free(b) or not _can_parent(b, a):
                        continue
                    c = next((c for c in cand if c is not a and c is not b and free(c) and _can_parent(c, a) and pair_ok(b, c)), None)
                    if c:
                        found = (b, c)
                        break
                if found:
                    b, c = found
                    make("U2", [b, c, a], {b["id"]: "parent", c["id"]: "parent", a["id"]: "child"}); pair_no[0] += 1; made += 1
        if made < quota[kind]:
            log.append(("target-yielded", kind, quota[kind], made))
    return units


# ------------------------------------------------------------------------------------------------ off-stage family
class _Family:
    def __init__(self, map_, town, recs, off, ties, R, L, names, other_towns, log, roster_names, origins):
        self.map, self.town, self.recs, self.off, self.ties, self.R, self.L = map_, town, recs, off, ties, R, L
        self.origins = origins
        self.names, self.other_towns, self.log = names, other_towns, log
        self.used_names = {rid: set(roster_names) for rid in recs}
        self.used_fates = {rid: set() for rid in recs}
        self.kin = {rid: [] for rid in recs}          # rid -> [(relation, other id)]
        self.fates = {f["id"]: f for f in L["fates"]}

    # -- small helpers
    def tie(self, a, rel, b):
        """b is a's <rel>. Only real people are ever `a`."""
        if (a, rel, b) not in self.ties:
            self.ties.append((a, rel, b))
            self.kin[a].append((rel, b))

    def person(self, key, sex, age, alive, owners):
        """A new off-stage person. `owners` = the real people whose records will name them (for names, fates, trade)."""
        oid = "o-" + hashlib.sha256(("|".join(sorted(owners)) + "|" + key).encode()).hexdigest()[:16]
        assert oid not in self.off, oid
        self.off[oid] = {"id": oid, "sex": sex, "alive": alive, "age": age if alive else None, "_age": age, "name": None,
                         "fate": None, "place": None, "place_town": None, "owners": sorted(owners), "married_here": False}
        return oid

    def finish(self, oid, forced_place=None, birth_of=None):
        """Name, place and fate line for one off-stage person, honouring every owner's constraints. `birth_of` = the
        real person whose parent or sibling this is: such kin mostly live where that person was born."""
        o, R = self.off[oid], self.R
        owners = o["owners"]
        pool = [n for n in self.names[o["sex"]] if all(n not in self.used_names[w] for w in owners)]
        o["name"] = pool[int(u(oid, "name") * len(pool))]
        for w in owners:
            self.used_names[w].add(o["name"])
        age = o["_age"]
        if not o["alive"]:
            classes, place = (("dead_any", "dead_grown") if age >= 18 else ("dead_any",)), None
        elif age < 14:
            classes, place = ("child",), "here"
        elif age < 18:
            classes, place = ("youth",), "here"
        else:
            classes = ("grown",)
            place = forced_place
            origin = None
            if place is None and birth_of is not None:
                home = (birth_of["prof"] or {}).get("home")
                origin = self.origins.get(birth_of["id"]) if home == "elsewhere" else None
                if origin and origin.lower() != self.town.lower():
                    w = R["birth_family_place"]["elsewhere"]
                    place = ("origin", "far", "here")[weighted([w["origin"], w["far"], w["here"]], oid, "place")]
                elif home == "here":
                    w = R["birth_family_place"]["here"]
                    kinds = ["here", "town", "far"] if self.other_towns else ["here", "far"]
                    place = kinds[weighted([w[k] for k in kinds], oid, "place")]
            if place is None:
                w = R["place_weights"]
                kinds = ["here", "town", "far"] if self.other_towns else ["here", "far"]
                place = kinds[weighted([w[k] for k in kinds], oid, "place")]
            if place == "origin":
                place, o["place_town"] = "town", origin
        if place == "town" and not o["place_town"]:
            o["place_town"] = self.other_towns[int(u(oid, "town") * len(self.other_towns))]
        o["place"] = place
        nowork = any(self.recs[w]["prof"] and self.recs[w]["prof"]["family_trade"] for w in owners) or \
            (o["alive"] and age >= R["retired_from_age"])
        quarrel = u(oid, "quarrel") < R["quarrel_share"] and not any(self.fates[x]["quarrel"] for w in owners for x in self.used_fates[w])
        ok = [f for f in self.L["fates"] if f["class"] in classes
              and (f["place"] in ("any", "none", "home") or (f["place"] == "away" and place in ("town", "far"))
                   or (f["place"] == "here" and place == "here"))
              and not (nowork and f["work"] and f["class"] == "grown")
              and f["quarrel"] == (quarrel and f["class"] == "grown")
              and all(f["id"] not in self.used_fates[w] for w in owners)]
        if not ok and quarrel:      # no quarrel line fits this place: an ordinary line instead
            ok = [f for f in self.L["fates"] if f["class"] in classes and not f["quarrel"]
                  and (f["place"] in ("any", "none", "home") or (f["place"] == "away" and place in ("town", "far")))
                  and not (nowork and f["work"] and f["class"] == "grown")
                  and all(f["id"] not in self.used_fates[w] for w in owners)]
        if not ok:      # every fitting line already used in this family: repeats are allowed before a wrong line is
            ok = [f for f in self.L["fates"] if f["class"] in classes and not (nowork and f["work"] and f["class"] == "grown")
                  and (f["place"] in ("any", "none", "home") or (f["place"] == "away" and place in ("town", "far"))
                       or (f["place"] == "here" and place == "here"))]
            self.log.append(("fate-repeated", oid))
        f = ok[int(u(oid, "fate") * len(ok))]
        o["fate"] = f["id"]
        for w in owners:
            self.used_fates[w].add(f["id"])

    # -- the pieces of a family
    def birth_family(self, members):
        """Parents and off-stage siblings for one real person, or for two real siblings (U4) who share them."""
        R, ids = self.R, [m["id"] for m in members]
        me = members[0]
        p = me["prof"]
        ages = [m["age"] for m in members]
        if p["sib_exact"] is not None:
            n = p["sib_exact"]
        else:
            n = max(p["sib_min"], weighted(R["sibling_count_weights"], ids[0], "sibcount") if len(members) == 1
                    else weighted(R["sibling_count_weights"][:3], ids[0], "sibcount"))
        sibs = []                                    # (sex, age, alive, married sister in town?)
        if p["sister_here"]:
            lo, hi = max(18, max(ages) - 10), min(80, min(ages) + 10)
            pool = [a for a in range(lo, hi + 1) if a not in ages] or [lo]
            sibs.append(("woman", pool[int(u(ids[0], "sisterage") * len(pool))], True, True))
        for i in range(n):
            known = ages + [s[1] for s in sibs]
            lo, hi = max(0, max(known) - 22), min(known) + 22          # N5: all the children span at most 22 years
            if p["youngest"]:
                lo = max(lo, max(ages) + 1)
            sex = "man" if u(ids[0], "sibsex", i) < 0.5 else "woman"
            if p["younger_brother"] and i == 0:
                sex, lo, hi = "man", max(lo, min(ages) - 12), min(ages) - 1
            pool = [a for a in range(lo, hi + 1) if a not in known] or [a for a in range(lo, hi + 1)]
            if not pool:
                self.log.append(("sibling-not-made", ids[0], i))
                continue
            age = pool[int(u(ids[0], "sibage", i) * len(pool))]
            alive = not (u(ids[0], "sibdead", i) < R["sibling_dead_share"]) or (p["younger_brother"] and i == 0)
            sibs.append((sex, age, alive, False))
        oldest = max(ages + [s[1] for s in sibs])
        dead_share = 1.0 if p["parents_dead"] or any(m["prof"]["parents_dead"] for m in members) else band(R["parent_dead_by_age"], min(80, max(ages)))
        youngest = min(ages + [s[1] for s in sibs])
        mother = oldest + 18 + int(u(ids[0], "motherage") * max(1, MOTHER_MAX - 18 - (oldest - youngest) + 1))
        father = max(oldest + 18, mother - 4 + int(u(ids[0], "fatherage") * 13))
        parents = []
        for k, (sex, age) in enumerate((("man", father), ("woman", mother))):
            alive = u(ids[0], "parentdead", k) >= dead_share and age <= 95
            parents.append(self.person("parent%d" % k, sex, age, alive, ids))
        sib_ids = []
        for i, (sex, age, alive, here) in enumerate(sibs):
            sid = self.person("sib%d" % i, sex, age, alive, ids)
            self.off[sid]["married_here"] = here
            sib_ids.append(sid)
        for m in members:
            for x in parents:
                self.tie(m["id"], "parent", x)
            for x in sib_ids:
                self.tie(m["id"], "sibling", x)
        for x in parents + sib_ids:
            self.finish(x, forced_place="here" if self.off[x]["married_here"] else None, birth_of=me)

    def offstage_spouse(self, me, must=None, child_age=None):
        """An off-stage husband or wife for a single resident, by the age table - living (in town) or dead."""
        R, p = self.R, me["prof"]
        if p["marital"] is not None:
            return None
        state = must
        if state is None:
            x = u(me["id"], "wed")
            wid = band(R["widowed_by_age"], me["age"])
            state = "dead" if x < wid else "alive" if x < wid + band(R["married_by_age"], me["age"]) else None
        if state is None:
            return None
        same = u(me["id"], "wedmix") < R["mixed_pair_share"]
        sex = me["sex"] if same else ("woman" if me["sex"] == "man" else "man")
        lo, hi = max(18, me["age"] - 12), min(92, me["age"] + 12)
        if child_age is not None:       # this person is also a real townsperson's other parent: 18 to 45 (65) years older
            c_lo, c_hi = child_age + 18, child_age + (MOTHER_MAX if sex == "woman" else FATHER_MAX)
            if max(lo, c_lo) <= min(hi, c_hi):
                lo, hi = max(lo, c_lo), min(hi, c_hi)
            else:                       # no age fits both the marriage and the child: that parent is dead (and has no age)
                lo, hi, state = c_lo, c_hi, "dead"
        sid = self.person("spouse", sex, lo + int(u(me["id"], "wedage") * (hi - lo + 1)), state == "alive", [me["id"]])
        self.tie(me["id"], "spouse", sid)
        self.finish(sid, forced_place="here")
        return sid

    def children(self, parents, real_children=(), other_parent_age=None, weights=None, other_parent_sex=None):
        """Off-stage children shared by these real parents (and the real children as their siblings)."""
        R = self.R
        ids = [m["id"] for m in parents]
        ceiling = min([m["age"] for m in parents] + ([other_parent_age] if other_parent_age is not None else [])) - 18
        women = [m["age"] for m in parents if m["sex"] == "woman"] + ([other_parent_age] if other_parent_sex == "woman" else [])
        men = [m["age"] for m in parents if m["sex"] == "man"] + ([other_parent_age] if other_parent_sex == "man" else [])
        floor = max([0] + [a - MOTHER_MAX for a in women] + [a - FATHER_MAX for a in men])     # nobody born to a mother past 45 or a father past 65
        if ceiling < floor:
            return []
        real_ages = [c["age"] for c in real_children]
        n = weighted(weights or R["child_count_weights"], ids[0], "kidcount")
        kids = []
        for i in range(n):
            pool = [a for a in range(floor, ceiling + 1)
                    if max(real_ages + [k[1] for k in kids] + [a]) - min(real_ages + [k[1] for k in kids] + [a]) <= 22
                    and a not in real_ages and a not in [k[1] for k in kids]]
            if not pool:
                break
            age = pool[int(u(ids[0], "kidage", i) * len(pool))]
            kids.append(("man" if u(ids[0], "kidsex", i) < 0.5 else "woman", age, u(ids[0], "kiddead", i) >= R["child_dead_share"]))
        owners = ids + [c["id"] for c in real_children]
        out = [self.person("kid%d" % i, sex, age, alive, owners) for i, (sex, age, alive) in enumerate(kids)]
        for m in parents:
            for x in out:
                self.tie(m["id"], "child", x)
        for c in real_children:
            for x in out:
                self.tie(c["id"], "sibling", x)
        for x in out:
            self.finish(x)
        return out

    def build(self, units):
        recs = self.recs
        done_birth = set()
        for un in units:
            ms = [recs[i] for i in un["members"]]
            if un["kind"] == "U4":
                self.tie(ms[0]["id"], "sibling", ms[1]["id"]); self.tie(ms[1]["id"], "sibling", ms[0]["id"])
                self.birth_family(ms); done_birth |= {ms[0]["id"], ms[1]["id"]}
            elif un["kind"] == "U1":
                self.tie(ms[0]["id"], "spouse", ms[1]["id"]); self.tie(ms[1]["id"], "spouse", ms[0]["id"])
                self.children(ms)
            elif un["kind"] == "U2":
                a, b, c = ms
                self.tie(a["id"], "spouse", b["id"]); self.tie(b["id"], "spouse", a["id"])
                for par in (a, b):
                    self.tie(par["id"], "child", c["id"]); self.tie(c["id"], "parent", par["id"])
                self.children([a, b], [c], weights=[4, 3, 1]); done_birth.add(c["id"])
            elif un["kind"] == "U3":
                par, c = ms
                self.tie(par["id"], "child", c["id"]); self.tie(c["id"], "parent", par["id"])
                sid = self.offstage_spouse(par, must="dead" if u(par["id"], "u3other") < 0.5 else "alive", child_age=c["age"])
                self.tie(c["id"], "parent", sid)
                self.off[sid]["owners"] = sorted(set(self.off[sid]["owners"]) | {c["id"]})
                self.used_names[c["id"]].add(self.off[sid]["name"]); self.used_fates[c["id"]].add(self.off[sid]["fate"])
                self.children([par], [c], other_parent_age=self.off[sid]["_age"], weights=[4, 3, 1],
                              other_parent_sex=self.off[sid]["sex"]); done_birth.add(c["id"])
        for r in ordered([r for r in recs.values() if r["status"] == "resolved"], "family", key=lambda r: r["id"]):
            if r["id"] not in done_birth:
                self.birth_family([r])
            if r["unit_kind"] in (None, "U4") or (r["unit_kind"] in ("U2", "U3") and not any(rel == "spouse" for rel, _ in self.kin[r["id"]])
                                                    and not any(rel == "child" for rel, _ in self.kin[r["id"]])):
                sid = self.offstage_spouse(r)
                if sid:
                    self.children([r], other_parent_age=self.off[sid]["_age"], other_parent_sex=self.off[sid]["sex"])
        for r in recs.values():
            if r["status"] != "resolved":
                continue
            spouses = [b for rel, b in self.kin[r["id"]] if rel == "spouse"]
            living = lambda b: b in recs or self.off[b]["alive"]
            r["marital"] = ("unstated" if r["prof"]["marital"] == "unplaced" else "never") if r["prof"]["marital"] else \
                "single" if not spouses else "married" if living(spouses[0]) else "widowed"
            if r["prof"]["kin_min"] and sum(1 for _, b in self.kin[r["id"]] if living(b)) < r["prof"]["kin_min"]:
                age_pool = [a for a in range(max(18, r["age"] - 8), min(80, r["age"] + 8) + 1) if a != r["age"]]
                sid = self.person("kin-min", "man" if u(r["id"], "kinminsex") < 0.5 else "woman",
                                  age_pool[int(u(r["id"], "kinminage") * len(age_pool))], True, [r["id"]])
                self.tie(r["id"], "sibling", sid); self.finish(sid)
                self.log.append(("kin-min-added", r["id"]))


def _households(recs, units, off, ties, R, L):
    """Who shares a home, and one home line each."""
    house = {}

    def put(members, anchor):
        hid = "h-" + hashlib.sha256(anchor.encode()).hexdigest()[:12]
        house[hid] = {"id": hid, "members": list(members), "home_line": None}
        for m in members:
            if m in recs:
                recs[m]["household"] = hid
        return hid

    for un in units:
        if un["kind"] != "U4" or u(un["id"], "sibhouse") < R["siblings_share_household"]:
            put(un["members"], un["id"])
    for r in recs.values():
        if r["household"] is None:
            put([r["id"]], r["id"])
    for a, rel, b in ties:
        if b in off and off[b]["alive"] and off[b]["place"] == "here" and (rel == "spouse" or (rel == "child" and off[b]["_age"] < 18)):
            h = house[recs[a]["household"]]
            if b not in h["members"]:
                h["members"].append(b)
    for h in house.values():
        real = [recs[m] for m in h["members"] if m in recs]
        born = all(r["status"] == "resolved" and r["prof"]["home"] == "here" for r in real)
        pool = [d for d in L["homes"] if d["who"] == "any" or (d["who"] == "shared" and len(h["members"]) >= 2) or (d["who"] == "born" and born)]
        h["home_line"] = pool[int(u(h["id"], "homeline") * len(pool))]["id"]
        for r in real:
            r["home_line"] = h["home_line"]
    return house


# ------------------------------------------------------------------------------------------------------- the audit
INVERSE = {"spouse": "spouse", "parent": "child", "child": "parent", "sibling": "sibling"}
UNIT_SHAPE = {"U1": ("spouse", "spouse"), "U2": ("parent", "parent", "child"), "U3": ("parent", "child"), "U4": ("sibling", "sibling")}


def _whole(x):
    return isinstance(x, int) and not isinstance(x, bool)


def audit_structure(res):
    """Repair B1 (Codex Stage 2 Method R1 F10): the town's SHAPE, checked independently of how it was built - tie
    ends and inverses, whole numbers, shared relatives being the same people, unit shapes, households, home-line
    eligibility, and every profile recomputed from the catalogue. [] = no structural fault found."""
    recs, off, ties, bad = res["records"], res["offstage"], res["ties"], []
    tie_set = {tuple(t) for t in ties}
    rel = {}
    for a, r_, b in ties:
        rel.setdefault((a, r_), set()).add(b)
    of = lambda a, r_: rel.get((a, r_), set())
    resolved = lambda x: x in recs and recs[x]["status"] == "resolved"
    # tie ends, words and inverses
    for a, r_, b in ties:
        if r_ not in INVERSE:
            bad.append(("tie-unknown-relation", a, r_, b)); continue
        if a == b:
            bad.append(("tie-to-self", a))
        if a not in recs:
            bad.append(("tie-from-nobody", a))
        elif not resolved(a):
            bad.append(("tie-from-unresolved", a, b))
        if b not in recs and b not in off:
            bad.append(("tie-to-nobody", a, b))
        elif b in recs and not resolved(b):
            bad.append(("tie-to-unresolved", a, b))
        if a in recs and b in recs and (b, INVERSE[r_], a) not in tie_set:
            bad.append(("inverse-missing", a, r_, b))
    # whole numbers, and the kind of years-in-town matching the number
    for r in recs.values():
        if r["status"] != "resolved":
            continue
        if not _whole(r["age"]):
            bad.append(("type-age", r["id"]))
        for k in ("years_town", "years_trade"):
            if r[k] is not None and not _whole(r[k]):
                bad.append(("type-" + k, r["id"]))
        if r["years_town_kind"] not in ("number", "born", "unknown") or (r["years_town_kind"] == "number" and r["years_town"] is None) \
                or (r["years_town_kind"] == "unknown" and r["years_town"] is not None):
            bad.append(("years-town-kind", r["id"]))
    for o in off.values():
        if not _whole(o.get("_age")) or (o["age"] is not None and not _whole(o["age"])):
            bad.append(("type-offstage-age", o["id"]))
    # shared relatives are the same people on every record
    for a, r_, b in ties:
        if not (resolved(a) and resolved(b)):
            continue
        if r_ == "sibling":
            if of(a, "parent") != of(b, "parent"):
                bad.append(("siblings-parents-differ", a, b))
            if of(a, "sibling") | {a} != of(b, "sibling") | {b}:
                bad.append(("siblings-siblings-differ", a, b))
        elif r_ == "spouse" and of(a, "child") != of(b, "child"):
            bad.append(("spouses-children-differ", a, b))
        elif r_ == "child" and of(b, "sibling") | {b} != of(a, "child"):
            bad.append(("child-siblings-not-parents-children", a, b))
    # unit shapes
    unit_ids = set()
    for un in res["units"]:
        unit_ids.add(un["id"])
        ms, shape = un["members"], UNIT_SHAPE.get(un["kind"])
        if shape is None or len(ms) != len(shape) or len(set(ms)) != len(ms) or any(not resolved(m) for m in ms):
            bad.append(("unit-shape", un["id"])); continue
        if any(recs[m]["unit"] != un["id"] or recs[m]["unit_kind"] != un["kind"] for m in ms):
            bad.append(("unit-membership", un["id"]))
        if [un["roles"].get(m) for m in ms] != list(shape):
            bad.append(("unit-roles", un["id"]))
        want = {"U1": [(0, "spouse", 1)], "U4": [(0, "sibling", 1)], "U3": [(0, "child", 1)],
                "U2": [(0, "spouse", 1), (0, "child", 2), (1, "child", 2)]}[un["kind"]]
        for i, r_, j in want:
            if ms[j] not in of(ms[i], r_):
                bad.append(("unit-tie-missing", un["id"], r_))
        if un["kind"] in ("U2", "U3") and len(of(ms[-1], "parent")) != 2:
            bad.append(("unit-child-parents", un["id"]))
    for r in recs.values():
        if r["unit"] is not None and r["unit"] not in unit_ids:
            bad.append(("unit-unknown", r["id"]))
    # households
    H = res["households"]
    where = {}
    lists = data()["lists"]
    homes = {d["id"]: d for d in lists["homes"]}
    for hid, h in H.items():
        if h["id"] != hid or not h["members"]:
            bad.append(("household-id", hid)); continue
        real = []
        for m in h["members"]:
            if m in where:
                bad.append(("household-two", m))
            where[m] = hid
            if m in recs:
                real.append(recs[m])
                if recs[m]["household"] != hid or recs[m]["home_line"] != h["home_line"]:
                    bad.append(("household-record-mismatch", m, hid))
            elif m in off:
                o = off[m]
                owner = [x for x in h["members"] if resolved(x) and (m in of(x, "spouse") or (m in of(x, "child") and o["_age"] < 18))]
                if not (o["alive"] and o["place"] == "here" and owner):
                    bad.append(("household-offstage-member", m, hid))
            else:
                bad.append(("household-member-unknown", m, hid))
        if not real:
            bad.append(("household-without-resident", hid)); continue
        d = homes.get(h["home_line"])
        born = all(r["status"] == "resolved" and r["prof"]["home"] == "here" for r in real)
        if d is None or not (d["who"] == "any" or (d["who"] == "shared" and len(h["members"]) >= 2) or (d["who"] == "born" and born)):
            bad.append(("home-line-not-eligible", hid, h["home_line"]))
    for r in recs.values():
        if r["household"] not in H or r["id"] not in H[r["household"]]["members"]:
            bad.append(("household-missing", r["id"]))
    for un in res["units"]:
        if un["kind"] in ("U1", "U2", "U3") and len({recs[m]["household"] for m in un["members"] if m in recs}) != 1:
            bad.append(("unit-split-households", un["id"]))
    for a, r_, b in ties:            # a living off-stage spouse here, or young child here, lives in the resident's home
        if a in recs and b in off and off[b]["alive"] and off[b]["place"] == "here" and (r_ == "spouse" or (r_ == "child" and off[b]["_age"] < 18)) \
                and where.get(b) != recs[a]["household"]:
            bad.append(("household-offstage-missing", a, b))
    # every profile recomputed from the catalogue, never taken from the builder
    for r in recs.values():
        if r["status"] != "resolved":
            continue
        bg = r.get("bg") or {}
        ids = [bg[k] for k in ("home", "life", "event") if bg.get(k)] + \
            ["habit:" + str(r["habit"]), "subject:" + str(r["subject"]), "personality:" + str(r["personality"])]
        try:
            again = profile(ids)
        except Unresolved as e:
            bad.append(("profile-unresolvable", r["id"], str(e))); continue
        if again != r["prof"]:
            bad.append(("profile-source", r["id"]))
    return bad


def audit(res):
    """Every rule of §6.2a and every profile, checked on the finished town. [] = may be stored."""
    recs, off, ties, bad = res["records"], res["offstage"], res["ties"], audit_structure(res)
    if any(b[0].startswith("type-") or b[0] in ("tie-to-nobody", "tie-unknown-relation", "tie-from-nobody") for b in bad):
        return bad                   # the number rules below assume whole numbers and ties that lead somewhere
    fates = {f["id"]: f for f in data()["lists"]["fates"]}
    kin = {}
    for a, rel, b in ties:
        kin.setdefault(a, []).append((rel, b))
        if a not in recs:
            bad.append(("tie-from-nobody", a))
    age = lambda x: recs[x]["age"] if x in recs else off[x]["age"]           # None for the dead (N7)
    alive = lambda x: x in recs or off[x]["alive"]
    in_units = {}
    for un in res["units"]:
        for m in un["members"]:
            if m in in_units:
                bad.append(("in-two-units", m))
            in_units[m] = un["id"]
    for o in off.values():
        if not o["alive"] and o["age"] is not None:
            bad.append(("N7-dead-with-age", o["id"]))
        if o["alive"] and (o["age"] is None or o["age"] < 0):
            bad.append(("N7-living-without-age", o["id"]))
        f = fates.get(o["fate"])
        if f is None or not o["name"]:
            bad.append(("no-fate-or-name", o["id"])); continue
        want = ("dead_any", "dead_grown") if not o["alive"] else ("child",) if o["_age"] < 14 else ("youth",) if o["_age"] < 18 else ("grown",)
        if f["class"] not in want or (f["class"] == "dead_grown" and o["_age"] < 18):
            bad.append(("fate-class", o["id"], f["id"]))
        if o["alive"] and ((f["place"] == "away" and o["place"] == "here") or (f["place"] == "here" and o["place"] != "here")):
            bad.append(("fate-place", o["id"], f["id"]))
    for r in recs.values():
        rid, p = r["id"], r["prof"]
        mine = kin.get(rid, [])
        if r["status"] != "resolved":
            if mine or r["age"] is not None or r["unit"]:
                bad.append(("unresolved-with-facts", rid))
            continue
        if not (18 <= r["age"] <= 80 and p["minage"] <= r["age"] <= p["maxage"]):
            bad.append(("N1", rid))
        for rel, b in mine:
            if b in recs and in_units.get(b) != in_units.get(rid):
                bad.append(("real-tie-across-units", rid, b))
            if rel == "spouse":
                if alive(b) and (age(b) < 18 or abs(age(b) - r["age"]) > 12):
                    bad.append(("N2", rid, b))
                if p["marital"] is not None:
                    bad.append(("profile-marital", rid))
            if rel == "parent":
                if alive(b) and age(b) < r["age"] + 18:
                    bad.append(("N3", rid, b))
                if b in off and alive(b) and age(b) > 95:
                    bad.append(("N6", rid, b))
                b_sex, b_age = (recs[b]["sex"], recs[b]["age"]) if b in recs else (off[b]["sex"], off[b]["_age"])
                if b_age - r["age"] > (MOTHER_MAX if b_sex == "woman" else FATHER_MAX):
                    bad.append(("parent-too-old-at-birth", rid, b))
                if p["parents_dead"] and alive(b):
                    bad.append(("profile-parents-dead", rid, b))
            if rel == "child":
                b_age = recs[b]["age"] if b in recs else off[b]["_age"]
                if r["age"] - b_age > (MOTHER_MAX if r["sex"] == "woman" else FATHER_MAX):
                    bad.append(("parent-too-old-at-birth", rid, b))
                if b in recs and recs[b]["age"] < 18:
                    bad.append(("N4-real-child", rid, b))
                if alive(b) and age(b) > r["age"] - 18:
                    bad.append(("N3/N4", rid, b))
                if not alive(b) and r["age"] < 19:
                    bad.append(("N3-dead-child", rid))
            if rel == "sibling":
                if b in recs and abs(recs[b]["age"] - r["age"]) > 15:
                    bad.append(("N5-real", rid, b))
                if p["youngest"] and (off[b]["_age"] if b in off else recs[b]["age"]) <= r["age"]:
                    bad.append(("profile-youngest", rid, b))
            if b in off and alive(b) and off[b]["_age"] >= 18 and p["family_trade"] and fates[off[b]["fate"]]["work"]:
                bad.append(("profile-family-trade", rid, b))
        if sum(1 for _, b in mine if b in off and fates[off[b]["fate"]]["quarrel"]) > 1:
            bad.append(("two-quarrels", rid))
        if len([1 for rel, _ in mine if rel == "spouse"]) > 1:
            bad.append(("two-spouses", rid))
        if len([1 for rel, _ in mine if rel == "parent"]) != 2:
            bad.append(("parents-not-two", rid))
        sib_ages = [off[b]["_age"] if b in off else recs[b]["age"] for rel, b in mine if rel == "sibling"] + [r["age"]]
        if max(sib_ages) - min(sib_ages) > 22:
            bad.append(("N5-span", rid))
        kid_ages = [off[b]["_age"] if b in off else recs[b]["age"] for rel, b in mine if rel == "child"]
        if kid_ages and max(kid_ages) - min(kid_ages) > 22:
            bad.append(("N5-children-span", rid))
        nsib = len(sib_ages) - 1
        if p["sib_exact"] is not None and nsib != p["sib_exact"]:
            bad.append(("profile-siblings-exact", rid, nsib))
        if nsib < p["sib_min"]:
            bad.append(("profile-siblings-min", rid))
        if p["younger_brother"] and not any(rel == "sibling" and b in off and off[b]["sex"] == "man" and alive(b) and off[b]["age"] < r["age"] for rel, b in mine):
            bad.append(("profile-younger-brother", rid))
        if p["sister_here"] and not any(rel == "sibling" and b in off and off[b]["married_here"] and off[b]["place"] == "here" and alive(b) for rel, b in mine):
            bad.append(("profile-sister-here", rid))
        if p["kin_min"] and sum(1 for _, b in mine if alive(b)) < p["kin_min"]:
            bad.append(("profile-kin-min", rid))
        born_count = p["home"] == "here" and not p["absence_open"] and not p["years_open"]     # decision 8
        if r["years_town"] is not None and not ((p["home"] == "elsewhere" and not p["absence"] and 0 <= r["years_town"] <= r["age"] - 1)
                                                or (born_count and r["years_town"] == r["age"] - p["away"])):
            bad.append(("N8", rid))
        if p["home"] == "here" and r["years_town_kind"] != "born":
            bad.append(("N8-born", rid))
        if born_count and r["age"] - p["away"] >= 1 and r["years_town"] is None:
            bad.append(("N8-born-number", rid))     # decision 8: the sentences fix the number, so it must be held
        if r["years_trade"] is not None:
            former = p["former"] if p["former_before"] else 0
            if not (p["trade_min"] <= r["years_trade"] <= r["age"] - 14 - former):
                bad.append(("N9/N10", rid))
        names = [off[b]["name"] for _, b in mine if b in off] + [recs[b]["name"] for _, b in mine if b in recs]
        if len(names) != len(set(names)) or r["name"] in [off[b]["name"] for _, b in mine if b in off]:
            bad.append(("names-clash", rid))
        if (r["marital"] in ("married", "widowed")) != any(rel == "spouse" for rel, _ in mine):
            bad.append(("marital-state", rid))
    return bad


def summary(res):
    recs = res["records"].values()
    n = len(res["records"])
    in_unit = sum(1 for r in recs if r["unit"])
    kinds = {}
    for un in res["units"]:
        kinds[un["kind"]] = kinds.get(un["kind"], 0) + 1
    pairs = [un for un in res["units"] if un["kind"] in ("U1", "U2")]
    same = sum(1 for un in pairs if len({res["records"][m]["sex"] for m in un["members"][:2]}) == 1)
    return {"residents": n, "unresolved": sum(1 for r in recs if r["status"] != "resolved"), "in_a_real_unit": in_unit,
            "share_in_a_real_unit": round(in_unit / n, 3) if n else 0, "units": kinds, "pairs": len(pairs), "same_sex_pairs": same,
            "offstage_people": len(res["offstage"]), "offstage_dead": sum(1 for o in res["offstage"].values() if not o["alive"]),
            "households": len(res["households"]), "years_town_number": sum(1 for r in recs if r["years_town"] is not None),
            "years_trade_unknown": sum(1 for r in recs if r["status"] == "resolved" and r["years_trade"] is None),
            "repinned": sum(1 for e in res["log"] if e[0] == "repinned"),
            "targets_yielded": [e[1:] for e in res["log"] if e[0] == "target-yielded"],
            "fate_repeated": sum(1 for e in res["log"] if e[0] == "fate-repeated"),
            # repair B1 (Codex F10): parent/child ties whose N3 age gap cannot be checked because one end is dead (N7)
            "n3_not_applicable": sum(1 for a, r_, b in res["ties"] if r_ in ("parent", "child") and b in res["offstage"]
                                     and not res["offstage"][b]["alive"])}
