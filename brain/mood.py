"""5c-2 2c - NPC mood. Design: wiki Projects/Avallone-Memory-Affinity-Design.md section V1.5, as amended by M7 (design
v1.1 after the Codex 2c design review, 2026-11-03). Pure derivation, no model call, nothing stored:

- one mood per NPC, the same for every player (the owner, 2026-11-03, 2c-1); two scores, cheer and energy, -100..+100;
- final = clamp(rest + day + clamp(events, -cap, +cap)) per axis (M7.1). rest: the persona (named) or a stable hash of
  the AI id (townsfolk); day: a stable hash of the AI id + the Central date (never the epoch - a restart must not
  reroll it); events: gifts to this NPC from anyone (faded over played hours), how many people visited it today, the
  game hour, and the unease of its town (2b exposure);
- the model gets ONE mood word (+ strength) folded into the 2b dials line (M3b trial: a word was recognised 75% of the
  time, a bare score 22% = no better than no mood) - never the numbers, never a reason;
- causes are code's (M7.5): one rendered clause on "how are you?", from the source that really moves the deciding
  axis now; none -> no explanation. "Who was it?" -> a fixed discretion line;
- at most one small bracketed action per reply, rare, rate-limited per conversation and per NPC (M7.6); in memory.

Mood changes tone, optional gossip and flavour only - never prices, goods, services, facts, quests, or answering."""
import nature  # Step B 2026-11-08: ordinary NPCs' nature tilts their mood
import hashlib
import json
import math
import os
import random
import re
import threading
import time
from datetime import datetime
from zoneinfo import ZoneInfo

CONFIG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "mood.json")
CENTRAL = ZoneInfo("America/Chicago")
RULES_VERSION = "2c-3"   # 2c-2 (Step B, 2026-11-08): edge 15, jitter 15, content styles, how angles; 2c-3: personality tilt
_lock = threading.Lock()
_state = {"cfg": None, "mtime": None}
_how_n = {}   # (npc, date) -> how many "how are you?" asks so far today: each ask gets the next angle
RNG = random.Random()


def cfg():
    with _lock:
        m = os.path.getmtime(CONFIG)
        if _state["mtime"] != m:
            with open(CONFIG, encoding="utf-8") as fh:
                c = json.load(fh)
            for band in c["bands"]["order"]:
                if band not in c["actions"] or band not in c["greet_percent"]:
                    raise ValueError(f"mood.json: band {band} needs actions and greet_percent")
            _state["cfg"], _state["mtime"] = c, m
        return _state["cfg"]


def _clamp(x, lo, hi):
    return max(lo, min(hi, x))


def _unit(*parts):
    """A stable 0..1 from the parts (SHA-256, never Python's hash())."""
    h = hashlib.sha256("|".join(("avallone-mood",) + tuple(str(p) for p in parts)).encode("utf-8")).digest()
    return int.from_bytes(h[:8], "big") / 2.0 ** 64


def central_date(now=None):
    return datetime.fromtimestamp(time.time() if now is None else float(now), CENTRAL).strftime("%Y-%m-%d")


def nature_of(req):
    """The ordinary NPC's personality (template personas only - the shard sends persona_vars), or None."""
    return nature.of(req.get("npc_id")) if req.get("persona_vars") else None


def rest(npc_id, persona_key="", templated=False):
    """The NPC's resting point: the persona's own (named NPCs) or a stable draw (townsfolk), leaning pleasant. Step B
    (the owner, chat, 2026-11-08: personality tilts mood): a templated NPC's nature moves it (e.g. grumpy -10 cheer)."""
    c = cfg()
    p = c["personas"].get(str(persona_key or "").lower())
    if p:
        return float(p.get("cheer", 0)), float(p.get("energy", 0))
    lo, hi = c["rest"]["cheer"]
    elo, ehi = c["rest"]["energy"]
    tc, te, _ = nature.tilt(npc_id) if templated else (0.0, 0.0, None)
    return (round(lo + (hi - lo) * _unit("rest-cheer", npc_id) + tc, 1),
            round(elo + (ehi - elo) * _unit("rest-energy", npc_id) + te, 1))


def day_draw(npc_id, date, swing=None):
    """(day type, cheer, energy) for this NPC on this Central date - the same all day, new tomorrow. 1 in 6 off (weary,
    glum or short-tempered), 1 in 6 bright (cheerful or lively), the rest ordinary (the owner, 2026-11-03, 2c-2). Each off/bright
    kind pushes its own axis (sim 2026-11-03: with one push per day, lively never happened)."""
    d = cfg()["day"]
    sw = swing or {}   # Step B: a moody/dramatic/eccentric/hot-tempered nature swings wider (more off and bright days)
    jx, sx = float(sw.get("jitter_x", 1.0)), float(sw.get("share_x", 1.0))
    off, bright = d["off_share"] * sx, d["bright_share"] * sx
    u = _unit("day", npc_id, date)
    jc = round(d["jitter"] * jx * (2 * _unit("day-jc", npc_id, date) - 1), 1)
    je = round(d["jitter"] * jx * (2 * _unit("day-je", npc_id, date) - 1), 1)
    if u < off + bright:
        mix = d["off_mix"] if u < off else d["bright_mix"]
        v, acc = _unit("day-kind", npc_id, date), 0.0
        for kind, share in mix.items():
            acc += share
            if v < acc:
                break
        cheer, energy = d["shape"][kind]
        return kind, float(cheer) + jc / 2, float(energy) + je / 2
    return "ordinary", jc, je


def gift_effect(rows, pt_now):
    """NPC-wide gift lift on cheer: one per character per Central day (Codex 2c F5: a rollback round trip can leave a
    second real row - it never counts twice), only rows carrying the played clock (M7.3), each fading straight to 0
    over fade_ph played hours; the sum saturates. Returns (lift, gifts counted)."""
    g = cfg()["gift"]
    seen, total, n = set(), 0.0, 0
    fade = float(g["fade_ph"]) * 3600.0
    for r in rows:
        if r["kind"] != "gift_given":
            continue
        f = r["facts"]
        key = (r["char_id"], f.get("day"))
        if key in seen:
            continue
        seen.add(key)
        pt = f.get("pt")
        if isinstance(pt, bool) or not isinstance(pt, int):
            continue                                   # pre-2c gift: warmth only, no mood nudge (M7.3)
        age = pt_now - pt
        if age < 0 or age >= fade:
            continue                                   # a future sample is nothing, never fresh (Codex 2c F3)
        total += float(g["impulse"]) * (1.0 - age / fade)
        n += 1
    sat = float(g["saturate"])
    return (sat * (1.0 - math.exp(-total / sat)) if total > 0 else 0.0), n


def busy_effect(visitors):
    b = cfg()["busy"]
    return float(max(b["cap"], b["each"] * max(0, visitors - b["free"])))


def hour_effect(hour):
    for lo, hi, v in cfg()["hour"]:
        if (lo <= hour < hi) if lo < hi else (hour >= lo or hour < hi):
            return float(v)
    return 0.0


def band_of(cheer, energy):
    """(band, strength word, deciding axis). First match wins (M7.1)."""
    b = cfg()["bands"]
    t = b["edge"]
    if cheer <= -t and energy >= b["tempered_energy"]:
        name, axis = "short-tempered", "cheer"
    elif cheer <= -t:
        name, axis = "glum", "cheer"
    elif energy <= -t:
        name, axis = "weary", "energy"
    elif cheer >= t:
        name, axis = "cheerful", "cheer"
    elif energy >= t:
        name, axis = "lively", "energy"
    else:
        return "content", "", None
    v = abs(cheer if axis == "cheer" else energy)
    strength = b["strength"]
    word = strength["mild"] if v < strength["plain_from"] else "" if v < strength["very_from"] else strength["strong"]
    return name, word, axis


def compute(req, ledger, dial, now=None):
    """The NPC's mood now, or None (no standing, ledger not reconciled for this epoch, disabled). `dial` = the 2b dials
    for this request (its town unease is reused, so both read one exposure snapshot)."""
    c = cfg()
    st = req.get("standing")
    if not c.get("enabled", True) or not isinstance(st, dict) or dial is None or ledger is None:
        return None
    npc = str(req.get("npc_id") or "")
    ep = st.get("epoch")
    if not npc or isinstance(ep, bool) or not isinstance(ep, int):
        return None
    now = time.time() if now is None else float(now)
    date = central_date(now)
    try:
        pt = int(_clamp(float(st.get("pt") or 0), 0, 1e12))
        hour = int(_clamp(float(st.get("hour") or 0), 0, 23))
    except (TypeError, ValueError):
        return None
    rows = ledger.npc_rows(npc, ep, kinds=("gift_given",))
    visits = ledger.npc_rows(npc, ep, kinds=("visited",), day=date)
    if rows is None or visits is None:
        return None
    nat = nature_of(req)
    r_cheer, r_energy = rest(npc, req.get("persona_key"), templated=nat is not None)
    kind, d_cheer, d_energy = day_draw(npc, date, nature.tilt(npc)[2] if nat else None)
    gift, n_gifts = gift_effect(rows, pt)
    visitors = len({v["char_id"] for v in visits})
    u = float((dial.get("sources") or {}).get("unease") or 0.0)
    ev = {"cheer": {"gift": gift, "town_news": float(c["town_news"]["per_u"]) * u},
          "energy": {"long_day": busy_effect(visitors), "late_hour": hour_effect(hour)}}
    cap = float(c["events_cap"])
    out = {"date": date, "pt": pt, "hour": hour, "rules": RULES_VERSION, "day_type": kind,
           "nature": nature.label(npc) if nat else "",
           "evidence": {"gifts_counted": n_gifts, "gift_rows": len(rows), "visitors_today": visitors, "unease": u}}
    scores = {}
    for axis, r_v, d_v in (("cheer", r_cheer, d_cheer), ("energy", r_energy, d_energy)):
        raw = sum(ev[axis].values())
        capped = _clamp(raw, -cap, cap)
        scale = capped / raw if raw else 0.0         # the combined cap shrinks every source alike (for causes)
        eff = {k: v * scale for k, v in ev[axis].items()}
        scores[axis] = round(_clamp(r_v + d_v + capped, -100.0, 100.0), 1)
        out[axis + "_parts"] = {"rest": r_v, "day": d_v, **{k: round(v, 1) for k, v in eff.items()}}
    out["cheer"], out["energy"] = scores["cheer"], scores["energy"]
    out["band"], out["strength"], axis = band_of(scores["cheer"], scores["energy"])
    out["cause"], out["gift_helped"] = cause(out, axis)
    return out


def cause(m, axis):
    """(cause class, gift helped). Only an event that really moves the deciding axis in the band's direction now, by at
    least min_points, and the biggest such; the rest point and the day's draw are no stated reason (Codex 2c F2)."""
    c = cfg()["causes"]
    neg = m["band"] in ("glum", "weary", "short-tempered")
    helped = neg and m["cheer_parts"].get("gift", 0.0) >= c["helped_min"]
    if axis is None:
        return "none", False
    parts = {k: v for k, v in m[axis + "_parts"].items() if k not in ("rest", "day")}
    best, val = "none", 0.0
    for k, v in parts.items():
        if (v < 0) == neg and abs(v) >= c["min_points"] and abs(v) > abs(val):
            best, val = k, v
    if helped and best == "gift":
        helped = False
    return best, helped


# ---- what the model is told, and what code adds ----------------------------------------------------------------

def word(m):
    return (m["strength"] + " " + m["band"]).strip() if m else ""


def place():
    """Where the mood half of the voice line goes: "head" (beside the dials cue) or "turn" (beside this turn)."""
    return "turn" if cfg().get("place") == "turn" else "head"


def pick(options, *keys):
    """A plain string as is; from a list, one stable choice for these keys (Step B 2026-11-08: variety per NPC)."""
    if isinstance(options, list):
        return options[int(_unit("pick", *keys) * len(options)) % len(options)] if options else ""
    return options or ""


def voice(m, npc=""):
    """The mood half of the one voice line (Codex idea 2: mood = how they speak today; the 2b dials that follow = how
    they receive this person). Never a reason, never a number (M3b)."""
    if not m:
        return ""
    if cfg().get("voice_style"):   # M11: a speaking-style hint per band (tone only)
        hint = pick((cfg().get("style") or {}).get(m["band"], ""), "style", npc, m.get("date", ""))
        return (f"Today you are {word(m)}. {hint} It shows only in how you speak, never in what you sell, do or answer, "
                "and you never give a reason for it.").replace("  ", " ")
    return (f"Today you are {word(m)} - let it colour how you speak, never what you sell, do or answer, and never give "
            "a reason for it.")


HOW_ARE_YOU = re.compile(
    r"\b(how\s+(are|r)\s+(you|ya|u|thee|things)|how('?s|\s+is)\s+(it\s+going|your\s+day|life|business|trade)|"
    r"how\s+do\s+you\s+(fare|feel|do)|how\s+fare|are\s+you\s+(well|ok|okay|alright|all\s+right)|"
    r"you\s+(look|seem)\s+\w+|what'?s\s+(wrong|the\s+matter|eating\s+you)|bad\s+day|in\s+a\s+(good|bad|foul)\s+mood)\b",
    re.I)
WHO_WAS_IT = re.compile(r"\b(who\s+(was\s+it|was\s+that|gave|brought|left|visited|came)|was\s+(it|that)\s+me|"
                        r"who\s+tipped)\b", re.I)


def asks_how(utterance):
    return bool(HOW_ARE_YOU.search(utterance or ""))


def how_angle(npc, date, extra=()):
    """Step B (2026-11-08): the angle for this ask - different NPCs start at different places, and asking the same NPC
    again moves on to the next one."""
    angles = list(cfg().get("how_angles") or []) + [a for a in extra if a]   # Step B: + the NPC's pet subject
    if not angles:
        return ""
    with _lock:
        n = _how_n.get((npc, date), 0)
        _how_n[(npc, date)] = n + 1
        if len(_how_n) > 20000:
            _how_n.clear()
    start = int(_unit("how", npc, date) * len(angles))
    return angles[(start + n) % len(angles)]


def how_block(m, npc="", templated=False):
    """Situation line for a "how are you?" turn: the model says how they feel in a few words; code adds the reason."""
    # Integrated trial 2026-11-03: "say that you are glum" made 36 of 44 replies say the word outright ("I am glum
    # today.") - stilted. Now: show it in a few words of your own, without naming it.
    return (f"They asked how you are. You feel {word(m)} today: let it show in a few words of your own, without naming "
            "the feeling outright. Give no reason or explanation. " +
            how_angle(npc, m.get("date", ""), [nature.subject_angle(npc)] * 2 if templated else ())).strip() if m else ""


def cause_clause(m, rng=None):
    """The one rendered clause for "how are you?" (minimal clues - no time, amount, item, count, role or place), or ""."""
    if not m:
        return ""
    rng = rng or RNG
    c = cfg()["clauses"]
    if m.get("gift_helped"):
        return rng.choice(c["gift_helped"])
    k = m.get("cause") or "none"
    if k == "gift" and m["band"] not in ("cheerful", "lively"):
        return ""
    return rng.choice(c[k]) if k in c and c[k] else ""


def said_already(say, clause):
    """True when the model's own words already carry the clause's gist ("The day has been long." + "It's been a long day.")."""
    for pat in cfg().get("clause_echo", []):
        if re.search(pat, clause, re.I) and re.search(pat, say or "", re.I):
            return True
    return False


def add_clause(say, clause):
    """The cause clause goes after what they said about themselves, before a closing question ("What can I get you?"),
    never tacked on after it (integrated trial 2026-11-03: "What can I get for you? It's been a long day.")."""
    if not clause or said_already(say, clause):
        return say
    parts = [x.strip() for x in SENTENCE.findall(say or "") if x.strip()]
    if len(parts) > 1 and parts[-1].endswith("?"):
        return " ".join(parts[:-1] + [clause, parts[-1]])
    return f"{(say or '').rstrip()} {clause}".strip()


def discretion_line(rng=None):
    return (rng or RNG).choice(cfg()["discretion"])


# ---- the excuse check (M7.5) -------------------------------------------------------------------------------------

def _excuses():
    """(kind, pattern) pairs: mood excuses, then the owner's risky invented claims (M10: illness, shortage, delay, people)."""
    out = [("excuse", re.compile(p, re.I)) for p in cfg()["excuse_patterns"]]
    for kind, pats in (cfg().get("risky_patterns") or {}).items():
        if not kind.startswith("_"):
            out += [(kind, re.compile(p, re.I)) for p in pats]
    return out


SENTENCE = re.compile(r"[^.!?]+[.!?]*")


def flagged(text):
    """(kind, matched text) for the first mood excuse or risky invented claim in `text`, or None."""
    for kind, p in _excuses():
        mm = p.search(text or "")
        if mm:
            return kind, mm.group(0)
    return None


def excuse(text):
    """The first mood-excuse shape or risky claim in `text` ("too tired to ...", "a headache brewing"), or None."""
    f = flagged(text)
    return f[1] if f else None


def strip_excuses(text):
    """`text` without the sentences carrying an excuse ("" if nothing is left)."""
    keep = [s.strip() for s in SENTENCE.findall(text or "") if s.strip() and not excuse(s)]
    return " ".join(keep).strip()


# ---- the one presentation action (M7.6) ------------------------------------------------------------------------

LEAD_ACTION = re.compile(r"^\s*\[[^\]]{1,60}\]\s*")
_conv, _npc_act = {}, {}
_act_lock = threading.Lock()


def pick_action(m, npc, subject, persona_key="", rng=None, now=None):
    """A bracketed action for this reply, or "". Rare: once per conversation (NPC x character; a gap of
    conversation_idle_s ends one) and at most once per npc_gap_s for this NPC across everyone; a chance per reply.
    Named NPCs use their own habit (Codex idea 3), the rest a prop-free pool. Nothing is committed here."""
    if not m:
        return ""
    a = cfg()["action"]
    rng = rng or RNG
    now = time.monotonic() if now is None else now
    with _act_lock:
        conv = _conv.get((npc, subject))
        if conv and now - conv["last"] <= a["conversation_idle_s"] and conv["acted"]:
            return ""
        last = _npc_act.get(npc)
        if last is not None and now - last < a["npc_gap_s"]:
            return ""
    if rng.random() >= a["chance"]:
        return ""
    habit = (cfg()["personas"].get(str(persona_key or "").lower()) or {}).get("habit") or {}
    pool = habit.get(m["band"]) or cfg()["actions"].get(m["band"]) or []
    return rng.choice(pool) if pool else ""


def note_turn(npc, subject, acted, now=None):
    """Called when the reply is returned: keeps the conversation alive, and commits the action's cooldowns. Cosmetic,
    in memory, reset on restart (documented); a reply the shard then drops can at most use up one action."""
    now = time.monotonic() if now is None else now
    a = cfg()["action"]
    with _act_lock:
        for k in [k for k, v in _conv.items() if now - v["last"] > a["conversation_idle_s"]]:
            _conv.pop(k, None)
        conv = _conv.get((npc, subject))
        if conv is None:
            conv = _conv[(npc, subject)] = {"last": now, "acted": False}
        conv["last"] = now
        if acted:
            conv["acted"] = True
            _npc_act[npc] = now


def compose(action, say):
    """Exactly one leading action: ours replaces any the model wrote (M7.6)."""
    if not action:
        return say
    return f"{action} {LEAD_ACTION.sub('', say or '', count=1)}".strip()


# ---- "who was it?" (M7.5) --------------------------------------------------------------------------------------

_caused = {}
CAUSE_TTL = 600.0


def note_cause(npc, subject, kind, now=None):
    now = time.monotonic() if now is None else now
    for k in [k for k, t in _caused.items() if now - t[1] > CAUSE_TTL]:
        _caused.pop(k, None)
    if kind in ("gift", "long_day", "gift_helped"):
        _caused[(npc, subject)] = (kind, now)


def asks_who(npc, subject, utterance, now=None):
    """True when they ask who was behind a reason the NPC gave them in the last few minutes."""
    now = time.monotonic() if now is None else now
    t = _caused.get((npc, subject))
    return bool(t and now - t[1] <= CAUSE_TTL and WHO_WAS_IT.search(utterance or ""))


# ---- greeting gossip (M7.6: the one draw moves here from the shard) ---------------------------------------------

def greet_percent(m):
    c = cfg()["greet_percent"]
    return int(c.get(m["band"], c["default"])) if m else int(c["default"])


def greet_draw(m, rng=None, scale=1.0):
    return (rng or RNG).random() * 100 < greet_percent(m) * scale


def explain(m):
    """[Named why] rows (M7.4): everything needed to see why the NPC is as it is."""
    if not m:
        return None
    out = {k: m[k] for k in ("band", "strength", "cheer", "energy", "cheer_parts", "energy_parts", "day_type", "cause",
                             "gift_helped", "date", "pt", "hour", "rules", "evidence")}
    if m.get("nature"):   # Step B: the shard prints day_type as is, so the nature shows there without a rebuild
        out["day_type"] = f'{m["day_type"]}; nature: {m["nature"]}'
        out["nature"] = m["nature"]
    return out
