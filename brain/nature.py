"""Step B (2026-11-08): a nature (personality) for every ordinary NPC. Called "nature" so it is not confused with the
companions' personality traits (AiPersonality.cs, turning-points.json), which are a separate thing.

the owner, chat, 2026-11-08 (play test 2):
Every ordinary NPC shares the townsfolk template, so before this only mood told them apart. Now each one gets one
personality + one speech habit + one pet subject, from nature.json.

- Chosen from the NPC's AI id alone, never stored: no database change, same pick after a restart. Weighted rendezvous
  hashing, so adding or removing an entry only moves the NPCs whose pick it was.
- Only template personas (the shard sends persona_vars). Mirabel, Bram and Elspeth keep their hand-written selves.
- The file is re-read when it changes; "enabled": false turns it all off (no prompt lines, no mood tilt).
"""
import hashlib
import json
import math
import os
import threading

CONFIG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "nature.json")
_lock = threading.Lock()
_cache = {"mtime": None, "cfg": None}


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
            "subject": subject, "jab": jab}


def prompt_block(p):
    """The lines added to a template persona: who this NPC is, beyond their trade."""
    if not p:
        return ""
    lines = [f"Your nature: you are {p['name']}. {p['line']}"]
    if p["group"] == "prickly" and p["jab"]:
        lines.append(cfg().get("prickly_line", "").replace("{jab}", p["jab"]))
    if p["habit_line"]:
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
