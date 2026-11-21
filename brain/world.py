"""The world table (2026-11-15) - the few fixed facts every townsperson knows about where it is.

the owner, chat, 2026-11-15, during the townsfolk Stage 1 play test
Seen in the dev
brain log of that test, both in answer to "where are you from?" with the town correctly sent as Britain:
"I was born right here in Britannia, though I spent my youth [...]" and
"I am from right here, in Britannia."

Until now the townsfolk persona read "You are {name}, {role} in {town}, in Britannia during the age of the Samurai
Empire." - two place names side by side and nothing saying which is which. This module supplies two template values:
- realm_tail: ", in Britannia" while the table is OFF (so the old sentence is unchanged, byte for byte), "" while ON;
- place_line: while ON, a sentence that names the town the NPC lives and works in and says what "here" means, and - only
  for a town the table lists as the kingdom's - names the kingdom and the world and says neither is the name of a town.
A town the table does not list as the kingdom's gets its own name and "here" only: saying less is the safe side.

WHAT THE LINE NEVER SAYS (Codex talk-fixes R1, A1): where the NPC was BORN or came FROM. That is the stored biography's
fact (backgrounds.py), and a biography can say "Born and raised in Trinsic, and lives elsewhere now." The first version
of this line said 'When you say "here", or say where you live or were born, the place is {town}', which contradicted
exactly that. The line is about the PRESENT place and the three names, nothing else.

ONE CHECKED SNAPSHOT (Codex talk-fixes R1, A2): world.json is checked as a whole when it is read - the switch, both
names, both lists. Anything not as promised (a list that is a number, "enabled": "yes") switches the table OFF: the old
sentence, nothing new, said once to the brain's log and shown on /health as "damaged". Both template values come from
the same snapshot (values()), and values() never raises: a problem here costs the new line, never the reply.

The town comes from the shard and has already passed brain.VAR_VALUE (short plain words) before it reaches here.
world.json is re-read when it changes.
"""
import json
import os
import re
import threading

CONFIG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "world.json")
OLD_TAIL = ", in Britannia"   # what the persona sentence said before this table existed
NAME = re.compile(r"[A-Za-z][A-Za-z '\-]{0,39}\Z")   # a place name as the table may hold it: short, plain, one line
OFF = {"on": False, "kingdom": "", "world": "", "kingdom_towns": frozenset(), "not_a_town": frozenset()}
report = None   # brain sets this to a function(problem_text); called once per damaged version of the file
_lock = threading.Lock()
_cache = {"key": None, "snap": OFF, "problem": "", "told": None}


def check(c):
    """(snapshot, problem). A table that is validly switched off, or damaged, gives the OFF snapshot; problem is "" unless
    it is damaged. A switched-on table is taken only whole."""
    if not isinstance(c, dict):
        return OFF, "the file is not a JSON object"
    on = c.get("enabled")
    if on is False:
        return OFF, ""                     # switched off on purpose: nothing else is looked at
    if on is not True:
        return OFF, '"enabled" is not true or false'
    names = {}
    for key in ("kingdom", "world"):
        val = c.get(key)
        if not isinstance(val, str) or not NAME.match(val.strip()):
            return OFF, f'"{key}" is not a short plain name'
        names[key] = val.strip()
    lists = {}
    for key in ("kingdom_towns", "not_a_town"):
        val = c.get(key)
        if not isinstance(val, list):
            return OFF, f'"{key}" is not a list'
        for item in val:
            if not isinstance(item, str) or not NAME.match(item.strip()):
                return OFF, f'"{key}" holds something that is not a short plain name'
        lists[key] = frozenset(item.strip().lower() for item in val)
    for key in ("kingdom", "world"):
        if names[key].lower() in lists["kingdom_towns"]:
            return OFF, f'the {key} is listed as one of its own towns'   # the very confusion this table exists to end
    return {"on": True, **names, **lists}, ""


def _snapshot():
    """The current checked snapshot. Never raises."""
    with _lock:
        try:
            st = os.stat(CONFIG)
            key = (st.st_mtime_ns, st.st_size, st.st_ino)
        except OSError as e:
            key, snap, problem = ("missing", type(e).__name__), OFF, f"the file cannot be read ({type(e).__name__})"
        else:
            if _cache["key"] == key:
                return _cache["snap"]
            try:
                with open(CONFIG, encoding="utf-8") as f:
                    snap, problem = check(json.load(f))
            except (OSError, ValueError) as e:
                snap, problem = OFF, f"the file cannot be read ({type(e).__name__})"
            except Exception as e:   # nothing in a table may ever cost a reply
                snap, problem = OFF, f"the file could not be checked ({type(e).__name__})"
        _cache["key"], _cache["snap"], _cache["problem"] = key, snap, problem
        tell = problem and _cache["told"] != (key, problem)
        if tell:
            _cache["told"] = (key, problem)
    if tell and report is not None:
        try:
            report(problem)
        except Exception:
            pass
    return snap


def state():
    """For /health: "on", "off" (switched off on purpose) or "damaged: <why>" (off because the file is not as promised)."""
    snap = _snapshot()
    with _lock:
        problem = _cache["problem"]
    return "on" if snap["on"] else (f"damaged: {problem}" if problem else "off")


def _line(snap, town):
    town = str(town or "").strip()
    if not snap["on"] or not town:
        return ""
    low = town.lower()
    if low in snap["not_a_town"]:
        return ""
    if low in snap["kingdom_towns"]:
        k, w = snap["kingdom"], snap["world"]
        return (f" The town you live and work in is {town}. {town} is one town in the kingdom of {k}, and the world is "
                f"called {w}. When you say \"here\", the place is {town} - {k} is the name of the whole kingdom and {w} "
                f"of the whole world, never of a town.")
    return f" The place where you live and work is {town}. When you say \"here\", the place is {town}."


def values(town):
    """(realm_tail, place_line) for the persona, both from ONE snapshot. Never raises: on any problem, the old sentence."""
    try:
        snap = _snapshot()
        return ("" if snap["on"] else OLD_TAIL), _line(snap, town)
    except Exception:
        return OLD_TAIL, ""


def realm_tail():
    return values("")[0]


def place_line(town):
    """The sentence for this town, with a leading space (it follows the persona's first sentence), or ""."""
    return values(town)[1]
