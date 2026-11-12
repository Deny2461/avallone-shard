"""Step B (2026-11-08): a nature (personality) for every ordinary NPC. Called "nature" so it is not confused with the
companions' personality traits (AiPersonality.cs, turning-points.json), which are a separate thing.

the owner, chat, 2026-11-08 (play test 2):
Every ordinary NPC shares the townsfolk template, so before this only mood told them apart. Now each one gets one
personality + one speech habit + one pet subject, from nature.json.

- Chosen from the NPC's AI id alone, never stored: no database change, same pick after a restart. Weighted rendezvous
  hashing, so adding or removing an entry only moves the NPCs whose pick it was.
- Only template personas (the shard sends persona_vars). Mirabel, Bram and Elspeth keep their hand-written selves.
- The file is re-read when it changes; "enabled": false turns it all off (no prompt lines, no mood tilt).

Rumour step C4 (2026-11-11; the owner's carry-in: the overused line). Habits are of two sorts:
- a MANNER ("manner": true in nature.json - the pet name, slow speech, never using names) is how the NPC always
  speaks; its sentence is in every prompt, as before.
- a FLOURISH (all the others) is an occasional touch. Its sentence is put in a turn's prompt only for a share of turns
  ("flourishes".share, or the habit's own "share") and never on two turns running in one conversation.
habit_in_prompt() decides, ONCE per turn: a retry of the same turn gets the same answer. of(), label() and
prompt_block() never touch that state, so the inspection commands stay read-only. What code controls is whether the
sentence is IN THE PROMPT; the model can still copy a habit it sees in the conversation so far.
"flourishes".enabled false = the sentence is in every prompt (the behaviour before this step).
"""
import collections
import hashlib
import json
import math
import os
import threading
import time

CONFIG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "nature.json")
_lock = threading.Lock()
_cache = {"mtime": None, "cfg": None}
_turn_lock = threading.Lock()
_turns = collections.OrderedDict()   # (npc, conversation) -> (turn id, sentence in that turn's prompt?, monotonic time)
TURNS_MAX = 4000                     # conversations remembered; the oldest are forgotten first (forgetting = a fresh draw)


def cfg():
    with _lock:
        try:
            mt = os.stat(CONFIG).st_mtime_ns
        except OSError:
            return {"enabled": False}
        if _cache["mtime"] != mt:
            with open(CONFIG, encoding="utf-8") as f:
                _cache["cfg"] = json.load(f)
            _cache["mtime"] = mt
        return _cache["cfg"]


def _unit(*parts):
    """A stable 0..1 from the parts (SHA-256, never Python's hash()). Never exactly 0."""
    h = hashlib.sha256("|".join(("avallone-nature",) + tuple(str(p) for p in parts)).encode("utf-8")).digest()
    return (int.from_bytes(h[:8], "big") + 1) / (2.0 ** 64 + 2)


def _choose(npc, slot, items, weight=lambda x: 1.0, key=lambda x: x):
    """Weighted rendezvous: the item with the lowest -ln(u)/w wins. Stable per (npc, slot, item key)."""
    best, score = None, None
    for it in items:
        w = float(weight(it))
        if w <= 0:
            continue
        s = -math.log(_unit(npc, slot, key(it))) / w
        if score is None or s < score:
            best, score = it, s
    return best


def of(npc_id):
    """{key, name, group, line, lean, habit, habit_line, subject, jab} for this NPC, or None (off, no id, empty list)."""
    c = cfg()
    npc = str(npc_id or "")
    if not c.get("enabled") or not npc:
        return None
    p = _choose(npc, "personality", c.get("personalities") or [], lambda x: x.get("weight", 0), lambda x: x["key"])
    if p is None:
        return None
    h = _choose(npc, "habit", c.get("habits") or [], key=lambda x: x["key"]) or {"key": "", "line": ""}
    subject = _choose(npc, "subject", c.get("subjects") or []) or ""
    pet = _choose(npc, "pet", c.get("pet_names") or []) or "friend"
    word = _choose(npc, "word", c.get("fav_words") or []) or "truly"
    jab = _choose(npc, "jab", c.get("jabs") or []) or ""
    return {"key": p["key"], "name": p["name"], "group": p.get("group", ""), "line": p["line"], "lean": p.get("lean", ""),
            "habit": h["key"], "habit_line": h["line"].replace("{pet}", pet).replace("{word}", word),
            "habit_manner": h.get("manner") is True, "habit_share": h.get("share"),
            "subject": subject, "jab": jab}


def habit_in_prompt(npc_id, conversation, turn, now=None):
    """C4: is this NPC's habit sentence in THIS turn's prompt? `conversation` = who they are talking with (a player's
    id, or an exchange id); `turn` = the request's own id, so a retry of the same turn gets the same answer.
    True when the gate is off, for a manner, and when the NPC has no nature (prompt_block() adds nothing then anyway)."""
    p = of(npc_id)
    f = cfg().get("flourishes") or {}
    if not p or not p["habit"] or not f.get("enabled") or p["habit_manner"]:
        return True
    share = p["habit_share"] if p["habit_share"] is not None else f.get("share", 0.5)
    share = min(1.0, max(0.0, float(share)))
    gap = float(f.get("gap_s", 600))
    now = time.monotonic() if now is None else now
    key = (str(npc_id), str(conversation or ""))
    turn = str(turn or "")
    with _turn_lock:
        last = _turns.get(key)
        if last and turn and last[0] == turn:
            return last[1]                                   # the same turn again (a retry): the same answer
        just_shown = bool(last and last[1] and now - last[2] <= gap)
        # no turn id: nothing to hash on, so draw; with one, the draw is the same whoever asks about this turn
        u = _unit(npc_id, "flourish", key[1], turn) if turn else _unit(npc_id, "flourish", key[1], now, len(_turns))
        shown = (not just_shown) and u < share
        _turns[key] = (turn, shown, now)
        _turns.move_to_end(key)
        while len(_turns) > TURNS_MAX:
            _turns.popitem(last=False)
        return shown


def prompt_block(p, habit=True):
    """The lines added to a template persona: who this NPC is, beyond their trade. habit=False leaves the habit
    sentence out of this turn's prompt (C4 - see habit_in_prompt)."""
    if not p:
        return ""
    lines = [f"Your nature: you are {p['name']}. {p['line']}"]
    if p["group"] == "prickly" and p["jab"]:
        lines.append(cfg().get("prickly_line", "").replace("{jab}", p["jab"]))
    if p["habit_line"] and habit:
        lines.append("A habit of speech: " + p["habit_line"])
    if p["subject"]:
        lines.append(f"Often on your mind lately: {p['subject']}. That is small talk only - never a danger, an event others "
                     "should act on, or a reason you cannot serve.")
    return "\n".join(x for x in lines if x)


def tilt(npc_id):
    """(cheer, energy) push on the mood resting point, and the swing multipliers {jitter_x, share_x} (1.0 = none)."""
    p = of(npc_id)
    none = (0.0, 0.0, {"jitter_x": 1.0, "share_x": 1.0})
    if not p or not p["lean"]:
        return none
    c = cfg()
    if p["lean"] == "swing":
        s = c.get("swing") or {}
        return 0.0, 0.0, {"jitter_x": float(s.get("jitter_x", 1.0)), "share_x": float(s.get("share_x", 1.0))}
    dc, de = (c.get("lean") or {}).get(p["lean"], [0, 0])
    return float(dc), float(de), none[2]


def subject_angle(npc_id):
    """A "how are you?" angle built from the pet subject, or ""."""
    p = of(npc_id)
    return (f"You may mention, in passing, what is often on your mind: {p['subject']} - small talk, not the reason for how "
            "you feel." if p and p["subject"] else "")


def label(npc_id):
    """Short text for [Named why]: "grumpy; habit sayings; on their mind: your cat"."""
    p = of(npc_id)
    if not p:
        return ""
    subj = p["subject"].replace("your ", "their ").replace("you have", "they have").replace("you are", "they are")
    return f"{p['name']}; habit {p['habit'].replace('_', ' ')}; on their mind: {subj}"
