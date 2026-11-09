"""5c-2 2b - relationship dials. Design: wiki Projects/Avallone-Memory-Affinity-Design.md, "Step 2b - relationship
dials: DESIGN v1" as amended by B12 (Codex 2b design review, 2026-11-01). Pure derivation, no model call, nothing stored:

- familiarity: distinct live `visited` days (as 5c-1; townsfolk and wanderers now record them too);
- warmth: the NPC's rest point + daily contributions (first visit of a day, a trade day; gifts later) that fade on the
  memory clock. Only rows carrying the memory clock (2b on) count - older visits count for familiarity only;
- trust (karma) and respect (fame), four sources (the owner, 2026-11-01): the player's standing; the NPC's own line and leaning
  (a stable hash of its id - never Python's hash()); town unease from the stories that really reached its town
  (exposure, B12 #1 - not the rumour_arrivals log); the moons and the hour (trust only, superstitious NPCs for the moons:
  the owner, chat, 2026-11-02 - glow = real moon x an 18 h moon, compared against each superstitious NPC's own line).
  Sources 3 + 4 together are capped at +-env_cap, then the deed bonus (respect), then the whole dial is clamped.

The shard sends `standing` {karma, fame, hour, moon, town, pt, mpt} only for dial-eligible NPCs; without it nothing runs.
Its `moon` (the stock game moon, a phase every ~2.5 real minutes) is no longer used - the moons are computed here.
The model gets one plain line (band words, stable across a conversation); dials change tone and how much is volunteered,
never prices or service."""
import hashlib
import json
import math
import os
import threading
import time

from memory import TOMBSTONED

CONFIG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "dials.json")
_lock = threading.Lock()
_state = {"cfg": None, "mtime": None}


def cfg():
    m = os.stat(CONFIG).st_mtime
    with _lock:
        if m != _state["mtime"]:
            with open(CONFIG, encoding="utf-8") as fh:
                c = json.load(fh)
            for dial in ("warmth", "trust", "respect"):
                if len(c["bands"][dial]) != len(c["bands"]["edges"]) + 1:
                    raise ValueError(f"dials.json: {dial} needs {len(c['bands']['edges']) + 1} band words")
            _state["cfg"], _state["mtime"] = c, m
        return _state["cfg"]


def unit(npc_id, domain):
    """A stable number in [0, 1) for this NPC and purpose (same on every restart and every machine)."""
    h = hashlib.sha256(f"avallone-2b|{domain}|{npc_id}".encode("utf-8")).digest()
    return int.from_bytes(h[:8], "big") / 2 ** 64


def _lerp(lo_hi, u):
    lo, hi = lo_hi
    return lo + (hi - lo) * u


def _clamp(v, lo, hi):
    return max(lo, min(hi, v))


def traits(npc_id, persona_key="", role="", trade="", town=""):
    """The NPC's own lines and leanings. Named personas may fix any of them in dials.json."""
    c = cfg()
    over = c.get("personas", {}).get(persona_key or "", {})
    role_l = (role or "").lower()
    shady = (town or "") in c.get("shady_towns", ()) or any(w in role_l for w in c.get("shady_roles", ()))
    t = {"shady": shady}
    for dial, share in (("trust", c["trust"]["inverted_share_shady"] if shady else c["trust"]["inverted_share"]),
                        ("respect", c["respect"]["inverted_share"])):
        t[f"{dial}_T"] = float(over.get(f"{dial}_T", _lerp(c[dial]["T"], unit(npc_id, f"{dial}_T"))))
        t[f"{dial}_W"] = max(1.0, float(over.get(f"{dial}_W", _lerp(c[dial]["W"], unit(npc_id, f"{dial}_W")))))
        t[f"{dial}_lean"] = int(over.get(f"{dial}_lean", -1 if unit(npc_id, f"{dial}_lean") < share else 1))
        if "W_neg" in c[dial]:
            t[f"{dial}_Wneg"] = max(1.0, float(over.get(f"{dial}_Wneg", _lerp(c[dial]["W_neg"], unit(npc_id, f"{dial}_Wneg")))))
    t["superstitious"] = bool(over.get("superstitious",
                                       unit(npc_id, "superstitious") < c["ambience"]["superstitious_share"]))
    mo = c["ambience"]["moons"]
    t["moon_T"] = float(over.get("moon_T", _lerp(mo["line"], unit(npc_id, "moon_T"))))   # their own line on the glow
    t["moon_W"] = max(0.01, float(over.get("moon_W", _lerp(mo["width"], unit(npc_id, "moon_W")))))
    w = c["warmth"]
    rest = over.get("warmth_rest")
    if rest is None:
        rest = float(w["rest_by_trade"].get(trade or "", w["rest_default"]))
        rest += _lerp((-w["rest_spread"], w["rest_spread"]), unit(npc_id, "rest_spread"))
        if unit(npc_id, "cool") < w["cool_share"]:
            rest -= _lerp(w["cool_by"], unit(npc_id, "cool_by"))
    t["warmth_rest"] = float(rest)
    return t


def line_value(stat, T, W, lean, Wneg=None):
    """The standing part (the owner, 2026-11-01).
    Below the NPC's line: 0 - a stranger, not a suspect. Above it the dial grows (about 76 one width past the line, 96 two
    widths). With Wneg (trust only): a NEGATIVE stat pulls the dial down the same way (a notorious character is
    distrusted). An inverted leaning flips the whole result. Dev trial 2026-11-01: the first two-sided curve made a new
    0-karma / 0-fame character distrusted and despised by most NPCs - replaced."""
    v = 100.0 * math.tanh(max(0.0, stat - T) / W)
    if Wneg is not None and stat < 0:
        v -= 100.0 * math.tanh(-stat / Wneg)
    return lean * v


def moon_glow(now=None):
    """(real, fast, glow), each 0..1 (0 new, 1 full) - the owner, chat, 2026-11-02
    combined as a multiplier. Real time, so the moons move while nobody is online."""
    m = cfg()["ambience"]["moons"]
    now = time.time() if now is None else float(now)

    def lit(period_s, new_at):
        return (1.0 - math.cos(2.0 * math.pi * (((now - new_at) % period_s) / period_s))) / 2.0

    real = lit(m["real_days"] * 86400.0, m["real_new_epoch"])
    fast = lit(m["fast_hours"] * 3600.0, m["fast_new_epoch"])
    return real, fast, real * fast


def ambience(hour, glow=0.0, superstitious=False, moon_T=1.0, moon_W=1.0):
    """Night for everyone; the moons' glow only for the superstitious, and only past their own line - spooked, trust
    down (the owner, chat, 2026-11-02), up to spooked_trust when the glow is far past it."""
    a = cfg()["ambience"]
    v = 0.0
    start, end = a["night"]
    if hour >= start or hour < end:
        v += a["night_trust"]
    if superstitious:
        v += a["moons"]["spooked_trust"] * line_value(glow, moon_T, moon_W, 1) / 100.0
    return round(float(v), 1)


class Exposure:
    """B12 #1: which deed stories are really live in a town at played second `pt` - live (not rolled back) deeds,
    grouped as the rumour engine groups them, not withdrawn, not from a deleted character, inside each type's real
    lifetime, and arrived by the engine's own deterministic spread. No presentation limits (town cap, one joke per
    kind): those are for what gets SAID, not for how a town feels."""

    def __init__(self, rumours):
        self.r = rumours

    def _tombstoned(self):
        led = self.r.ledger
        with led.rlock:
            return {row[0] for row in led.rdb.execute(TOMBSTONED).fetchall()}

    def live_in(self, town, pt):
        r = self.r
        r._load()
        if not town or pt <= 0:
            return []
        dropped, tomb = r._dropped(), self._tombstoned()
        out = []
        for g in r._group(r._deeds(pt)):
            if g["id"] in dropped or g["char"] in tomb:
                continue
            spec = r.cfg["deeds"].get(g["type"], {})
            fades = g["last_pt"] + int(float(spec.get("lifetime_h", 24)) * 3600)
            if pt >= fades:
                continue
            arrive = next((a for t, (a, _p) in r._spread(g).items() if t.lower() == town.lower()), None)
            if arrive is None or arrive > pt:
                continue
            out.append((g, arrive, fades))
        return out


def _gm_weight(facts):
    v = facts.get("unease")
    if isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v):
        return 0.0
    return _clamp(float(v), -1.0, 1.0)


def unease(live, pt):
    """Town unease u in 0..1 from the live stories (B12 #6): a bounded weight per story group (never times its count),
    fading linearly over the story's own lifetime; repeats of one character + kind within the window count less."""
    u = cfg()["unease"]
    total, seen = 0.0, {}
    for g, arrive, fades in sorted(live, key=lambda x: x[1]):
        weight = _gm_weight(g["facts"]) if g["type"] == "gm_planted" else float(u["weights"].get(g["type"], 0.0))
        if not weight:
            continue
        key = (g["char"], g["type"])
        recent = [a for a in seen.get(key, []) if arrive - a < float(u["repeat_window_h"]) * 3600]
        seen.setdefault(key, []).append(arrive)
        mult = u["repeat_mult"][len(recent)] if len(recent) < len(u["repeat_mult"]) else 0.0
        total += weight * mult * max(0.0, (fades - pt) / max(1, fades - arrive))
    return _clamp(total, 0.0, 1.0)


def deed_respect(live, char_id):
    r = cfg()["respect"]
    n = sum(1 for g, _a, _f in live if g["char"] == char_id and g["type"] in r["deed_types"])
    return float(min(r["deed_cap"], r["deed_each"] * n))


def warmth(ledger, npc_id, char_id, mpt_now, rest, items=None):
    """(warmth, contributions) or None for a deleted character. Each kind counts once per Central day, at the memory
    clock of that day's first row that carries one; contributions fade to the rest point (half-life on `mpt`)."""
    w = cfg()["warmth"]
    rows = ledger.pair_rows(npc_id, char_id, kinds=("visited", "traded", "gift_given", "player_used_hateful_language"))
    if rows is None:
        return None
    hl = float(w["half_life_mh"]) * 3600
    g = w["gift"]
    first, total, gifts, gift_days = {}, 0.0, [], set()
    window = float(g["recent_mh"]) * 3600
    for r in rows:
        f = r["facts"]
        m = f.get("mpt")
        if r["kind"] == "gift_given":
            # Codex 2b code review F4: one gift per day here too (a rollback round trip can let the shard take a second)
            if f.get("day") in gift_days:
                continue
            gift_days.add(f.get("day"))
            # B5: smaller with each recent gift. Sliding window (Codex optional: was quadratic over years of gifts).
            gifts = [x for x in gifts if 0 <= float(m) - x < window]
            recent = len(gifts)
            gifts.append(float(m))
            mult = g["liked"] if f.get("kind") == "liked" else g["value"].get(f.get("value"), 0.5)
            worth = g["base"] * mult / (1 + recent) * 0.5 ** (max(0.0, float(mpt_now) - float(m)) / hl)
            total += worth
            if items is not None:
                items.append({"kind": "gift", "day": f.get("day"), "what": f.get("kind"), "value": f.get("value"),
                              "now": round(worth, 2)})
            continue
        if m is None:
            continue                                   # pre-2b row: familiarity only
        key = (r["kind"], f.get("day"))
        if key in first:
            continue
        first[key] = m
        pts = {"visited": w["visit_day"], "traded": w["trade_day"]}.get(r["kind"], w.get("hateful_day", -20))
        worth = pts * 0.5 ** (max(0.0, float(mpt_now) - float(m)) / hl)
        total += worth
        if items is not None:
            items.append({"kind": r["kind"], "day": f.get("day"), "now": round(worth, 2)})
    return _clamp(rest + total, -100.0, 100.0), total


def band(value, dial):
    b = cfg()["bands"]
    for edge, word in zip(b["edges"], b[dial]):
        if value < edge:
            return word
    return b[dial][-1]


def familiarity_words(days):
    b = cfg()["bands"]
    for edge, word in zip(b["familiarity_days"], b["familiarity"]):
        if days < edge:
            return word
    return b["familiarity"][-1]


def _num(v, lo, hi, default=0):
    try:
        x = float(v)
    except (TypeError, ValueError):
        return default
    return _clamp(x, lo, hi) if math.isfinite(x) else default


def compute(req, ledger, rumours, detail=False, now=None):
    """All four dials for this request, or None (no standing block, disabled, bad ids, deleted character).
    detail=True (admin [Named why] only): also the NPC's own lines, each warmth row's share now, and the live stories."""
    c = cfg()
    st = req.get("standing")
    if not c.get("enabled", True) or not isinstance(st, dict):
        return None
    npc, char = str(req.get("npc_id") or ""), str(req.get("subject_id") or "")
    if not npc or not char or npc == char:
        return None
    # Codex 2b code review F1: nothing ledger-based until this shard epoch has reconciled (a restored world must not see
    # visits, gifts or penalties from the future it discarded).
    ep = st.get("epoch")
    if isinstance(ep, bool) or not isinstance(ep, int) or not ledger.reconciled(ep):
        return None
    karma, fame = _num(st.get("karma"), -15000, 15000), _num(st.get("fame"), 0, 15000)
    hour = int(_num(st.get("hour"), 0, 23))
    town = str(st.get("town") or "")[:40]
    pt, mpt = int(_num(st.get("pt"), 0, 1e12)), _num(st.get("mpt"), 0, 1e12)
    pv = req.get("persona_vars") or {}
    tr = traits(npc, str(req.get("persona_key") or ""), str(pv.get("role") or ""), str(pv.get("trade") or ""), town)
    live = Exposure(rumours).live_in(town, pt) if rumours is not None else []
    u = unease(live, pt)
    cap = float(c["env_cap"])
    real, fast, glow = moon_glow(now)
    amb = ambience(hour, glow, tr["superstitious"], tr["moon_T"], tr["moon_W"])
    moon = (f"glow {glow:.2f} (real {real * 100:.0f}% lit x fast {fast * 100:.0f}% lit" +
            (f"; spooked above {tr['moon_T']:.2f})" if tr["superstitious"] else ")"))
    trust_base = line_value(karma, tr["trust_T"], tr["trust_W"], tr["trust_lean"], tr["trust_Wneg"])
    trust_env = _clamp(c["unease"]["trust_per_u"] * u + amb, -cap, cap)
    trust = _clamp(trust_base + trust_env, -100.0, 100.0)
    resp_base = line_value(fame, tr["respect_T"], tr["respect_W"], tr["respect_lean"])
    resp_env = _clamp(c["unease"]["respect_per_u"] * u, -cap, cap) if resp_base > 0 else 0.0  # those it regards already
    deeds = deed_respect(live, char)
    respect = _clamp(resp_base + resp_env + deeds, -100.0, 100.0)
    items = [] if detail else None
    wm = warmth(ledger, npc, char, mpt, tr["warmth_rest"], items)
    if wm is None:
        return None
    days, _last = ledger.visit_days(npc, char)
    likes = " ".join(str(st.get("likes") or "").split())[:120]
    out = {"familiarity": days, "warmth": wm[0], "trust": trust, "respect": respect, "likes": likes,
            "sources": {"karma": karma, "fame": fame, "trust_base": round(trust_base, 1), "trust_env": round(trust_env, 1),
                        "respect_base": round(resp_base, 1), "respect_env": round(resp_env, 1), "deeds": deeds,
                        "unease": round(u, 3), "ambience": amb, "hour": hour, "moon": moon, "moon_glow": round(glow, 3),
                        "town": town,
                        "warmth_rest": round(tr["warmth_rest"], 1), "warmth_events": round(wm[1], 2),
                        "trust_lean": tr["trust_lean"], "respect_lean": tr["respect_lean"],
                        "superstitious": tr["superstitious"], "stories_live": len(live)}}
    if detail:
        out["detail"] = {
            "traits": {k: (round(v, 1) if isinstance(v, float) else v) for k, v in tr.items()},
            "warmth_items": items,
            "stories": [{"type": g["type"], "about_them": g["char"] == char,
                         "arrived_h_ago": round((pt - a) / 3600, 1), "fades_in_h": round((f - pt) / 3600, 1)}
                        for g, a, f in sorted(live, key=lambda x: x[1], reverse=True)][:12],
        }
    return out


SHORT = {"warmth": ["hostile", "cool", "neutral", "likes them", "fond"],
         "trust": ["no trust", "wary", "as a stranger", "trusts", "complete trust"],
         "respect": ["scorns", "unimpressed", "neutral", "capable", "high regard"],
         "familiarity": ["stranger", "once or twice", "by sight", "regular"]}


def short_band(value, dial):
    """Admin label for a dial value (the model gets the long band sentence; [Named why] shows these)."""
    b = cfg()["bands"]
    edges = b["familiarity_days"] if dial == "familiarity" else b["edges"]
    for i, edge in enumerate(edges):
        if value < edge:
            return SHORT[dial][i]
    return SHORT[dial][-1]


def explain(req, ledger, rumours):
    """[Named why <character>] (design B8): every dial, its band and each source's share. None as compute()."""
    d = compute(req, ledger, rumours, detail=True)
    if d is None:
        return None
    d["bands"] = {k: short_band(d[k], k) for k in ("familiarity", "warmth", "trust", "respect")}
    for k in ("warmth", "trust", "respect"):
        d[k] = round(d[k], 1)
    return d


def greet_stranger(d):
    """Familiarity below greet_familiarity_min: the NPC does not know them yet."""
    return d["familiarity"] < cfg().get("gossip", {}).get("greet_familiarity_min", 3)


def greet_scale(d):
    """Step B (the owner, chat, 2026-11-08): a stranger still gets a greeting hint, at 1/6 of the NPC's normal greeting rate."""
    return float(cfg().get("gossip", {}).get("greet_stranger_share", 0.0)) if d is not None and greet_stranger(d) else 1.0


def holds_gossip(d, mode, stranger_pass=False):
    """Gossip held back by code (B6 - dev trial 2026-11-01: band words alone barely move gemma4:12b). Wary or worse: no
    live gossip at all, asked or not (small talk is still fine). Cool: nothing volunteered on a greeting. A reason or None."""
    g = cfg().get("gossip", {})
    if d["trust"] < g.get("trust_below", -20):
        return "dials: wary of them - keeps the town's gossip to themselves"
    if mode == "greet" and d["warmth"] < g.get("greet_warmth_below", -20):
        return "dials: cool towards them - volunteers nothing"
    if mode == "greet" and d["familiarity"] < g.get("greet_familiarity_min", 3) and not stranger_pass:
        # the owner, chat, 2026-11-02: gossip only - an NPC that does not know them yet (
        # 3 visit days) volunteers nothing; asked-for news is unaffected. Step B (the owner, 2026-11-08): except when the brain's
        # greeting draw passed at the stranger rate (greet_scale) - stranger_pass. Wary and cool still hold, above.
        return "dials: does not know them yet - volunteers nothing"
    return None


def cue(d):
    """The one plain line for the prompt (band words only, so it stays the same across a conversation)."""
    return ("How you regard this person - let it colour your tone, patience and how much you volunteer; it never "
            "changes prices, goods or service: " + "; ".join((
                familiarity_words(d["familiarity"]), band(d["warmth"], "warmth"), band(d["trust"], "trust"),
                band(d["respect"], "respect"))) + "." +
            (f" If they ask what you like or would welcome as a gift: {d['likes']}." if d.get("likes") else ""))
