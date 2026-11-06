"""Made-up local chatter for "what news" (the owner, chat, 2026-10-30). Not lore: a pool of seed lines in smalltalk.json,
one picked per reply, told by the NPC in their own words. Never names a player."""
import json
import os
import random

PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "smalltalk.json")
# Small actions in square brackets (the owner, chat, 2026-10-30)
ACTIONS = ["[lowers voice]", "[leans in]", "[glances around]", "[snorts]", "[chuckles]", "[shakes head]", "[taps nose]",
           "[wipes hands]", "[rolls eyes]", "[sighs]", "[grins]", "[looks over shoulder]", "[raises an eyebrow]",
           "[shrugs]", "[jerks thumb toward the door]", "[drops voice to a whisper]"]
ACTION_SHARE = 0.5
_cache = {"mtime": None, "data": None}


def _load(path=PATH):
    m = os.path.getmtime(path)
    if _cache["mtime"] != m:
        with open(path, encoding="utf-8") as fh:
            _cache["data"], _cache["mtime"] = json.load(fh), m
    return _cache["data"]


def action_line(rng):
    if rng.random() >= ACTION_SHARE:
        return ""
    return f"Begin your reply with a small action in square brackets, such as {rng.choice(ACTIONS)}."


def pick(town, neighbours, rng, path=PATH):
    """One seed line for `town`, or "" (unknown town). A town-specific line `town_share` of the time."""
    if not town:
        return ""
    d = _load(path)
    own = d.get("towns", {}).get(town) or []
    # town_share: chance of a town-specific line when the town has some (few per town, so kept low to avoid repeats)
    pool = own if own and rng.random() < float(d.get("town_share", 0.3)) else d.get("any", [])
    if not pool:
        return ""
    near = rng.choice(neighbours) if neighbours else town
    return rng.choice(pool).format(town=town, near=near)


def share(path=PATH):
    return float(_load(path).get("share", 0.65))


def block(seed, rng):
    out = ["Idle talk going round town (made-up chatter, nobody is sure of it): - " + seed,
           "They asked for news: pass this on in one or two short sentences, in your own words and the manner of your "
           "trade. Do not begin with \"They say\", \"I hear\", \"I heard\" or \"Word is\"."]
    act = action_line(rng)
    if act:
        out.append(act)
    return "\n".join(out)
