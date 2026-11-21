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


_pins = None   # townsfolk Stage 2: npc_id -> {"key", "habit", "subject"} (the STORED nature) or None; set by the brain


def set_pins(fn):
    """Townsfolk Stage 2 (design 6.4): from now on of() - and so every caller: both prompt paths, the habit gate, mood,
    the labels - answers with the nature stored in the NPC's permanent record when there is one. None switches it off."""
    global _pins
    _pins = fn


def of(npc_id):
    """The NPC's EFFECTIVE nature: the stored (pinned) one when the brain has set a pin source and this NPC has a
    record, else the natural pick. Same shape as natural()."""
    p = natural(npc_id)
    if p is None or _pins is None:
        return p
    try:
        pin = _pins(str(npc_id))
    except Exception:
        pin = None          # a problem costs the pin for this call, never the reply
    if not pin:
        return p
    c = cfg()
    out = dict(p)
    per = next((x for x in c.get("personalities") or [] if x["key"] == pin.get("key")), None)
    if per is not None:
        out.update(key=per["key"], name=per["name"], group=per.get("group", ""), line=per["line"], lean=per.get("lean", ""))
    if "habit" in pin:
        h = next((x for x in c.get("habits") or [] if x["key"] == pin["habit"]), None) if pin["habit"] else None
        if h is None:       # no habit this NPC may show (e.g. the "years" habit with no number held - design N11)
            out.update(habit="", habit_line="", habit_manner=False, habit_share=None)
        else:
            pet = _choose(str(npc_id), "pet", c.get("pet_names") or []) or "friend"
            word = _choose(str(npc_id), "word", c.get("fav_words") or []) or "truly"
            out.update(habit=h["key"], habit_line=h["line"].replace("{pet}", pet).replace("{word}", word),
                       habit_manner=h.get("manner") is True, habit_share=h.get("share"))
    if pin.get("subject") is not None:
        out["subject"] = pin["subject"]
    return out


def natural(npc_id):
    """The unpinned pick, straight from the catalogue: {key, name, group, line, lean, habit, habit_line, subject, jab}
    for this NPC, or None (off, no id, empty list). The town build starts from this; talk uses of()."""
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
    # Step 2e (design A3; the owner, chat, 2026-11-12: gossip 1/4, cautious 1/5, sceptic 1/6, the rest plain)
    gh = _choose(npc, "gossip_habit", c.get("gossip_habits") or [], lambda x: x.get("weight", 0), lambda x: x["key"])
    return {"gossip": gh["key"] if gh else "plain","key": p["key"], "name": p["name"], "group": p.get("group", ""), "line": p["line"], "lean": p.get("lean", ""),
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


_bg_turns = collections.OrderedDict()   # (npc, conversation) -> (turn id, shown?, monotonic time)


def background_in_prompt(npc_id, conversation, turn, slot_taken, now=None):
    """Step 2e (design §8.5): is the NPC's background in THIS turn's prompt, unasked? It shares the ONE flourish slot with
    a flourish habit: never when the habit is in this turn (slot_taken), never two turns running, otherwise for
    backgrounds.share of turns. A retry of the same turn gets the same answer. Asked-about turns bypass this (brain)."""
    b = cfg().get("backgrounds") or {}
    if not b.get("enabled") or slot_taken:
        return False
    share = min(1.0, max(0.0, float(b.get("share", 0.15))))
    gap = float((cfg().get("flourishes") or {}).get("gap_s", 600))
    now = time.monotonic() if now is None else now
    key = (str(npc_id), str(conversation or ""))
    turn = str(turn or "")
    with _turn_lock:
        last = _bg_turns.get(key)
        if last and turn and last[0] == turn:
            return last[1]
        just_shown = bool(last and last[1] and now - last[2] <= gap)
        u = _unit(npc_id, "background", key[1], turn) if turn else _unit(npc_id, "background", key[1], now, len(_bg_turns))
        shown = (not just_shown) and u < share
        _bg_turns[key] = (turn, shown, now)
        _bg_turns.move_to_end(key)
        while len(_bg_turns) > TURNS_MAX:
            _bg_turns.popitem(last=False)
        return shown


def habit_is_flourish(p):
    """True when this NPC's habit is a flourish under the gate (so a turn showing it fills the shared slot)."""
    f = cfg().get("flourishes") or {}
    return bool(p and p["habit"] and f.get("enabled") and not p["habit_manner"])


def prompt_block(p, habit=True):
    """The lines added to a template persona: who this NPC is, beyond their trade. habit=False leaves the habit
    sentence out of this turn's prompt (C4 - see habit_in_prompt)."""
    if not p:
        return ""
    lines = [f"Your nature: you are {p['name']}. {p['line']}" if p.get("line") else ""]   # "" = left out this turn (A4)
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
