"""Step 2e - a town's reputation of a player (design §2, §8.4, §9.6, §11.2, §12; Codex design R6 = GO).

Derived, nothing stored: from the player's live public deeds whose stories REACHED the town, over a window of
REPUTATION_S played seconds after each story arrived there (the owner, chat, 2026-11-12: 7 played days), fading linearly.
- Read: only this character's deeds, bucket-aligned (the same canonical groups as speech), from far enough back that a
  story born before the window but arriving inside it still counts. Reach and arrival from Rumours._spread - the one
  arrival function speech and town unease use. GM drops and deleted characters honoured.
- Categories from ORIGINAL facts only (never a garble or a player's correction): renown = notable kill, notable tame,
  pacifying; dread = murder only; jest = deed types flagged funny in rumours.json (provoking = creature set against
  creature: a joke, never dread - Codex design R1-4). Anything else counts for nothing.
- Score per category = sum over groups of fade x repeat damping (dials.json unease repeat_window_h / repeat_mult, the
  n-th group of the same type inside the window in arrival order), capped at CAP.
- The LINE (Codex design R2-6): active while the category holds at least one group still inside its window - so one
  notable deed is recognised for its whole 7 days. Strongest score wins; ties renown > dread > jest; the supporting story
  is the strongest group IN that category, and the line is that one story's own sentence (raw: no corrections, no
  garbles) - never a general claim about the player.
- The DIALS use the faded score (dials.py): respect + DIAL_EACH x renown, trust - DIAL_EACH x dread, each capped.
"""
import json

from memory import TOMBSTONED

CAP = 3.0
ORDER = ("renown", "dread", "jest")
RENOWN = ("kill_notable", "tame_notable", "pacified")
DREAD = ("murder",)
FEELING = {"renown": "they are admired for it", "dread": "folk are wary of them for it", "jest": "folk find it funny"}


def category(rumours, typ):
    if typ in RENOWN:
        return "renown"
    if typ in DREAD:
        return "dread"
    if rumours.cfg["deeds"].get(typ, {}).get("funny"):
        return "jest"
    return None


def lookback_s(rumours):
    """Window + the longest a story takes to arrive + one bucket (a group spans less than one)."""
    import rumours as _r
    return _r.REPUTATION_S + rumours.max_travel_s() + _r.BUCKET_S


def _deeds_of(rumours, char_id, pt):
    """This character's live deeds, bucket-aligned exactly as Rumours._deeds aligns (so groups are the same groups)."""
    import rumours as _r
    start = ((int(pt) - lookback_s(rumours)) // _r.BUCKET_S) * _r.BUCKET_S
    with rumours.ledger.rlock:
        rows = rumours.ledger.rdb.execute(
            """SELECT event_id, char_id, facts FROM events
               WHERE npc_id = ? AND kind = 'deed' AND char_id = ? AND json_extract(facts, '$.pt') BETWEEN ? AND ?
                 AND status = 'live' ORDER BY epoch, seq""", (_r.WORLD_ID, str(char_id)[:36], start, int(pt))).fetchall()
    return [(r[0], r[1], json.loads(r[2])) for r in rows]


def of(rumours, char_id, town, pt, repeat_window_h=24.0, repeat_mult=(1.0, 0.5, 0.25)):
    """{"renown", "dread", "jest": scores, "label": category or None, "story": group or None, "line": text or None}."""
    import rumours as _r
    empty = {"renown": 0.0, "dread": 0.0, "jest": 0.0, "label": None, "story": None, "line": None}
    if rumours is None or not char_id or not town:
        return empty
    rumours._load()
    town_key = next((t for t in rumours.cfg["towns"] if t.lower() == str(town).lower()), None)
    if town_key is None:
        return empty
    pt = int(pt)
    with rumours.ledger.rlock:
        tomb = {row[0] for row in rumours.ledger.rdb.execute(TOMBSTONED).fetchall()}
    if char_id in tomb:
        return empty
    dropped = rumours._dropped()
    live = []
    for g in rumours._group(_deeds_of(rumours, char_id, pt)):
        if g["id"] in dropped or g["char"] != char_id:
            continue
        cat = category(rumours, g["type"])
        if cat is None:
            continue
        arrive = rumours._spread(g).get(town_key)
        if arrive is None or arrive[0] > pt:
            continue
        age = pt - arrive[0]
        if age >= _r.REPUTATION_S:
            continue
        live.append((arrive[0], g, cat, 1.0 - age / _r.REPUTATION_S))
    score = {c: 0.0 for c in ORDER}
    best = {}
    seen = {}
    for arrive, g, cat, fade in sorted(live, key=lambda x: (x[0], x[1]["id"])):
        recent = [a for a in seen.get(g["type"], []) if arrive - a < float(repeat_window_h) * 3600]
        seen.setdefault(g["type"], []).append(arrive)
        mult = repeat_mult[len(recent)] if len(recent) < len(repeat_mult) else 0.0
        w = fade * mult
        score[cat] = min(CAP, score[cat] + w)
        if cat not in best or w > best[cat][0] or (w == best[cat][0] and g["id"] < best[cat][1]["id"]):
            best[cat] = (w, g)
    out = {**empty, **{c: round(score[c], 4) for c in ORDER}}
    active = [c for c in ORDER if c in best]          # at least one live group in the window
    if not active:
        return out
    label = max(active, key=lambda c: (score[c], -ORDER.index(c)))
    g = best[label][1]
    sentence = rumours.render(g, [], town_key, who="this person", raw=True)
    out.update(label=label, story=g,
               line=f"Folk in {town_key} have heard of the person you are talking to: {sentence} ({FEELING[label]}). "
                    f"It may colour how you see them; mention it only if it fits.")
    return out
