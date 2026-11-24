"""Townsfolk step, Stage 2 - knowing their town (design §7). LIVE: reviewed by Codex and in use since townsfolk Stage 2
went live (build ab52a5b0a). (This header still said "PROTOTYPE ... not live" until the errands change corrected it.)

From a roster usable for THIS turn only: for an NPC with a home, the nearest of each common place in its own town and
on its own map - one of eight compass directions, one of three distances, measured home to home, with the keeper's
current name. No usable roster, no home, or no such place in town: the line is simply absent.
The distance bands and the role words are Claude's (flagged for review)."""
import math
import re

PLACES = (("bank", r"\b(banker|minter)\b"), ("healer", r"(?<!wandering )\bhealer\b"), ("inn or tavern", r"\b(innkeeper|tavern keeper|barkeeper)\b"),
          ("smith", r"\b(blacksmith|weaponsmith|armorer)\b"), ("provisioner", r"\bprovisioner\b"), ("stable", r"\banimal trainer\b"),
          ("mage shop", r"\b(mage|alchemist)\b"), ("tailor", r"\b(tailor|weaver)\b"), ("shipwright", r"\bshipwright\b"))
COMPASS = ("east", "north-east", "north", "north-west", "west", "south-west", "south", "south-east")
NEAR, MID = 15, 45          # tiles: up to 15 "close by", up to 45 "a short walk", beyond "across town"


def direction(dx, dy):
    """The game's y grows southward."""
    return COMPASS[int(((math.degrees(math.atan2(-dy, dx)) + 360 + 22.5) % 360) // 45)]


def how(dx, dy):
    """The loose words for an offset in tiles, home to home: "right beside you", or one of three distances and one of
    eight directions - the same bands and words the place lines below use."""
    d = max(abs(dx), abs(dy))
    if d <= 2:
        return "right beside you"
    return ("close by" if d <= NEAR else "a short walk" if d <= MID else "across town") + f" to the {direction(dx, dy)}"


def nearest(held, npc_id):
    """[(place, keeper name, keeper role, distance in tiles, direction or None or "own")] for this NPC, or [] - never
    guessed. Repair A5 (Codex Method R1 F14): a keeper's OWN kind of place is listed as "own" (0 tiles), so a banker
    asked "where is the bank?" says it is theirs instead of "not sure"."""
    at = held.where(npc_id) if held is not None else None
    if at is None or at[2]["home"] is None:
        return []
    map_, town, me = at
    out = []
    for place, pattern in PLACES:
        rx = re.compile(pattern, re.IGNORECASE)
        if rx.search(me["role"]):
            out.append((place, me["name"], me["role"], 0, "own"))     # they keep such a place themselves
            continue
        best = None
        for p in held.people(map_, town):
            if p["id"] == me["id"] or p["home"] is None or not rx.search(p["role"]):
                continue
            dx, dy = p["home"][0] - me["home"][0], p["home"][1] - me["home"][1]
            d = max(abs(dx), abs(dy))
            if best is None or (d, p["id"]) < (best[0], best[1]["id"]):
                best = (d, p, dx, dy)
        if best:
            d, p, dx, dy = best
            out.append((place, p["name"], p["role"], d, direction(dx, dy) if d > 2 else None))
    return out


def lines(held, npc_id):
    """The "your town" lines for the prompt, or ""."""
    found = nearest(held, npc_id)
    if not found:
        return ""
    town = held.where(npc_id)[1]
    out = [f"Where things are in {town}, from where you work (you know your own town; give these directions loosely, in your own words, and never invent others):"]
    for place, name, role, d, way in found:
        if way == "own":
            out.append(f"- the nearest {place}: your own, where you work - you are the {role}.")
            continue
        how = "right beside you" if way is None else ("close by" if d <= NEAR else "a short walk" if d <= MID else "across town") + f" to the {way}"
        out.append(f"- the nearest {place}: {how} ({name} is the {role} there).")
    out.append("For any place not listed here, say you are not sure where it is.")
    return "\n".join(out)
