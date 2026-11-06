"""Phase 5a - the rumour network (design: Projects/Avallone-AI-NPC-Plan.md, "Phase 5").

Rumours are DERIVED, not stored: every call recomputes them from (a) the live "deed" events in the memory ledger
(npc_id = WORLD_ID) and (b) the played clock the shard passes in. So:
  * a world restore rolls deeds back (existing reconciliation) and rewinds the clock (saved with the world) -
    the rumours they made, and how far those had spread, go back with them. Nothing here to reconcile.
  * spreading and distortion are deterministic (hashes of the rumour id), so the same rumour always reaches the
    same towns at the same played time with the same garbling - no model call, works with the model host down.
The only state of its own: GM drops (rumour_drops) and which arrivals were already logged (rumour_arrivals); both
are pruned once older than the horizon.
Bounded (Codex code review 2026-10-28 finding 2): rumours read only deeds from the last horizon_h played hours, through
an index; heroic nicknames are counted by SQLite, not by replaying every deed in Python.
Rules (the owner, chat, 2026-10-28): mild garbling - numbers grow, places drift, names get vaguer; NEVER a different
player's name and never an invented deed. Public deeds only - the shard never sends anything else.
"""
import hashlib
import heapq
import json
import os
import re

WORLD_ID = "a7a110e0-0000-4000-8000-000000000001"
DISTORTIONS = ("count_up", "place_drift", "name_vague")


def _h(*parts):
    """Deterministic 0..1 from the parts."""
    d = hashlib.sha256("|".join(str(p) for p in parts).encode()).digest()
    return int.from_bytes(d[:8], "big") / 2 ** 64


def _plural(word, n):
    if n == 1:
        return word
    return word if word.endswith("s") else word + "s"


def _a(word):
    if not word:
        return "something"
    if word[:1].isupper():  # a unique name ("Barracoon") takes no article
        return word
    return ("an " if word[0].lower() in "aeiou" else "a ") + word


def _count_words(n):
    return {1: "once", 2: "twice"}.get(n, f"{n} times")


class Rumours:
    def __init__(self, ledger, config_path):
        self.ledger = ledger
        self.config_path = config_path
        self._mtime = None
        self.cfg = None
        self._load()
        with ledger.lock:
            ledger.db.execute("CREATE TABLE IF NOT EXISTS rumour_drops (id TEXT PRIMARY KEY, at_utc TEXT NOT NULL)")
            ledger.db.execute("""CREATE TABLE IF NOT EXISTS rumour_arrivals (
                                   id TEXT NOT NULL, town TEXT NOT NULL, pt INTEGER NOT NULL, PRIMARY KEY (id, town))""")
            ledger.db.execute("""CREATE INDEX IF NOT EXISTS events_deed_pt
                                   ON events (npc_id, json_extract(facts, '$.pt')) WHERE kind = 'deed'""")
            ledger.db.execute("CREATE INDEX IF NOT EXISTS rumour_arrivals_pt ON rumour_arrivals (pt)")

    # ---- config ----
    def _load(self):
        m = os.stat(self.config_path).st_mtime
        if m != self._mtime:
            with open(self.config_path, encoding="utf-8") as fh:
                cfg = json.load(fh)
            graph = {t: [] for t in cfg["towns"]}
            for a, b, hours in cfg["edges"]:
                if a not in graph or b not in graph:
                    raise ValueError(f"edge {a}-{b}: unknown town")
                graph[a].append((b, float(hours)))
                graph[b].append((a, float(hours)))
            for name, d in cfg["deeds"].items():
                for k in d.get("distort", []):
                    if k not in DISTORTIONS:
                        raise ValueError(f"deed {name}: unknown distortion {k}")
            cfg["_graph"] = graph
            longest = max(float(d.get("lifetime_h", 24)) + float(d.get("window_h", 0)) for d in cfg["deeds"].values())
            if float(cfg.get("horizon_h", 168)) < 2 * longest:
                raise ValueError(f"horizon_h must be at least {2 * longest} (twice the longest lifetime + window)")
            self.cfg, self._mtime = cfg, m
        return self.cfg

    def origin_town(self, facts):
        cfg = self.cfg
        if facts.get("type") == "gm_planted":
            want = str(facts.get("town", "")).lower()
            for t in cfg["towns"]:
                if t.lower() == want or cfg["towns"][t].get("felucca_name", "").lower() == want:
                    return t
        world = cfg["maps"].get(str(facts.get("map", "")))
        best, best_d = None, None
        for t, d in cfg["towns"].items():
            if d["world"] != world:
                continue
            dist = (d["x"] - float(facts.get("x", 0))) ** 2 + (d["y"] - float(facts.get("y", 0))) ** 2
            if best_d is None or dist < best_d:
                best, best_d = t, dist
        return best or cfg["fallback_town"]

    def on_mapped_world(self, facts):
        """False for a facet with no towns (Ilshenar): its deeds start in the fallback town, so a place garble must
        never turn "near the Ilshenar dungeon" into "somewhere around Britain" (Codex code review finding 4)."""
        return str(facts.get("map", "")) in self.cfg["maps"]

    # ---- deeds -> rumours ----
    def horizon(self):
        return int(float(self.cfg.get("horizon_h", 168)) * 3600)

    def _deeds(self, pt):
        """Live deeds from the last horizon_h played hours up to `pt`, oldest first (indexed range read)."""
        with self.ledger.rlock:  # the read-only connection: never waits behind a write (4d soak lesson)
            rows = self.ledger.rdb.execute(
                """SELECT event_id, char_id, facts FROM events
                   WHERE npc_id = ? AND kind = 'deed' AND json_extract(facts, '$.pt') BETWEEN ? AND ?
                     AND status = 'live' ORDER BY epoch, seq""", (WORLD_ID, pt - self.horizon(), pt)).fetchall()
        return [(r[0], r[1], json.loads(r[2])) for r in rows]

    def _group(self, deeds):
        """Repeats of the same deed close together in played time become ONE rumour that grows (farming 20 dragons
        is one story, not twenty)."""
        cfg = self.cfg
        groups, open_by_key = [], {}
        for eid, char, f in deeds:
            typ = f.get("type", "")
            spec = cfg["deeds"].get(typ)
            if not spec or not spec.get("enabled", True):
                continue
            origin = self.origin_town(f)
            pt = int(f.get("pt", 0))
            key = (char, typ, f.get("subject", ""), origin)
            g = open_by_key.get(key)
            window = float(spec.get("window_h", 0)) * 3600
            if g and window > 0 and pt - g["last_pt"] <= window:
                g["count"] = max(g["count"], int(f.get("count", 1))) if typ in ("fished_boots", "tame_failstreak",
                                                                               "returned") else g["count"] + 1
                g["last_pt"] = pt
                g["events"].append(eid)
                continue
            g = {"id": eid, "char": char, "type": typ, "facts": f, "origin": origin, "first_pt": pt, "last_pt": pt,
                 "count": int(f.get("count", 1)), "events": [eid]}
            groups.append(g)
            open_by_key[key] = g
        return groups

    def _spread(self, g):
        """Played-hour arrival time and path for every town the rumour can reach (Dijkstra; each edge's delay is
        jittered +-25% per rumour, deterministically)."""
        cfg, graph = self.cfg, self.cfg["_graph"]
        speed = float(cfg.get("speed", 1.0))
        dist, prev = {g["origin"]: 0.0}, {g["origin"]: None}
        heap = [(0.0, g["origin"])]
        while heap:
            d, town = heapq.heappop(heap)
            if d > dist.get(town, 1e18):
                continue
            for nxt, hours in graph[town]:
                edge = "-".join(sorted((town, nxt)))
                nd = d + hours * speed * (0.75 + 0.5 * _h(g["id"], edge))
                if nd < dist.get(nxt, 1e18):
                    dist[nxt], prev[nxt] = nd, town
                    heapq.heappush(heap, (nd, nxt))
        out = {}
        for town, d in dist.items():
            path, t = [], town
            while t is not None:
                path.append(t)
                t = prev[t]
            out[town] = (g["first_pt"] + int(d * 3600), list(reversed(path)))
        return out

    def _distortions(self, g, path):
        """Which garbles have crept in by the time the story walked `path`. Mild: at most max_distortions, chosen
        per hop from the deed type's allowed list. Never touches anything but count, place and the name's detail."""
        spec = self.cfg["deeds"][g["type"]]
        allowed = list(spec.get("distort", []))
        chance = float(self.cfg.get("distort_chance", 0.35))
        cap = int(self.cfg.get("max_distortions", 2))
        out = []
        for i in range(1, len(path)):
            if not allowed or len(out) >= cap:
                break
            if _h(g["id"], "hop", path[i]) < chance:
                k = allowed[int(_h(g["id"], "kind", path[i]) * len(allowed)) % len(allowed)]
                if k not in out:
                    out.append(k)
        return out

    def render(self, g, garbles, town=None, who=None):
        f, spec = g["facts"], self.cfg["deeds"][g["type"]]
        count = g["count"]
        if "count_up" in garbles:
            count = count + 1
        if who is None:
            who = f.get("who", "") or "someone"
        if "name_vague" in garbles:
            who = f.get("descriptor") or "an adventurer"
        place = f.get("place") or g["origin"]
        near_place = "near " + place
        if "place_drift" in garbles and self.on_mapped_world(f):
            place = g["origin"]
            near_place = "somewhere around " + g["origin"]
        subject = str(f.get("subject", ""))
        other = str(f.get("other", ""))
        if count > 1 and subject and not subject[:1].isupper():
            a_subject = f"{'many' if count > 4 else count} {_plural(subject, count)}"
        else:
            a_subject = _a(subject)
        vals = {
            "who": who, "place": place, "near_place": near_place, "subject": subject, "Subject": subject[:1].upper() + subject[1:],
            "a_subject": a_subject, "a_subject_one": _a(subject), "a_other": _a(other), "count": count,
            "times": _count_words(count), "text": f.get("text", ""),
            "by_pet": ", with the help of their beasts" if f.get("by_pet") else "",
        }
        text = spec["text"].format(**vals)
        if town and self.cfg["towns"].get(town, {}).get("felucca_name") and f.get("map") == "Felucca":
            text = text.replace(town, self.cfg["towns"][town]["felucca_name"])
        return re.sub(r"\s+", " ", text).strip()

    def _dropped(self):
        with self.ledger.rlock:
            return {r[0] for r in self.ledger.rdb.execute("SELECT id FROM rumour_drops").fetchall()}

    def _town_rows(self, pt, town=None):
        """[(town, g, garbles, arrived_pt, hops)] live at played second `pt`: per town at most one live rumour per funny
        deed type and at most town_cap in all, freshest arrival first. Shared by view() and speech_rows()."""
        self._load()
        pt = int(pt)
        dropped = self._dropped()
        per_town = {}
        for g in self._group(self._deeds(pt)):
            if g["id"] in dropped:
                continue
            spec = self.cfg["deeds"][g["type"]]
            fades = g["last_pt"] + int(float(spec.get("lifetime_h", 24)) * 3600)
            if pt >= fades:
                continue
            for t, (arrive, path) in self._spread(g).items():
                if town and t.lower() != town.lower():
                    continue
                if arrive > pt:
                    continue
                per_town.setdefault(t, []).append((t, g, self._distortions(g, path), arrive, len(path) - 1))
        out = []
        cap = int(self.cfg.get("town_cap", 5))
        for t in sorted(per_town):
            rows = sorted(per_town[t], key=lambda r: -r[3])
            kept, funny_seen = [], set()
            for r in rows:
                spec = self.cfg["deeds"][r[1]["type"]]
                if spec.get("funny"):
                    if r[1]["type"] in funny_seen:
                        continue
                    funny_seen.add(r[1]["type"])
                kept.append(r)
                if len(kept) >= cap:
                    break
            out.extend(kept)
        return out

    def view(self, pt, town=None):
        """Live rumours at played second `pt`, per town: [{town, id, type, text, hops, arrived_pt, funny, char}]."""
        return [{"town": t, "id": g["id"], "type": g["type"], "char": g["char"], "hops": hops, "arrived_pt": arrive,
                 "funny": bool(self.cfg["deeds"][g["type"]].get("funny")), "garbles": garbles,
                 "text": self.render(g, garbles, t), "events": len(g["events"])}
                for t, g, garbles, arrive, hops in self._town_rows(pt, town)]

    def resolve_town(self, name):
        """Phase 5b: the shard sends the NPC's region name (or a pinned town). Only an exact town name - or a town's
        Felucca name - counts; anything else (dungeon, wilderness, unknown) gives None: no live rumours there."""
        self._load()
        want = str(name or "").strip().lower()
        if not want:
            return None
        for t, d in self.cfg["towns"].items():
            if t.lower() == want or str(d.get("felucca_name", "")).lower() == want:
                return t
        return None

    def speech_rows(self, pt, town):
        """Phase 5b: the town's live rumours for an NPC to speak, freshest first. No rendered name here: the caller
        renders each with render_for(), which takes the sentinel (or forces the vague form)."""
        rows = []
        for t, g, garbles, arrive, hops in self._town_rows(pt, town):
            f = g["facts"]
            rows.append({"id": g["id"], "char": g["char"], "type": g["type"], "town": t, "arrived_pt": arrive,
                         "vague": "name_vague" in garbles or not g["char"] or g["type"] == "gm_planted",
                         "descriptor": f.get("descriptor") or "an adventurer", "_g": g, "_garbles": garbles})
        return rows

    def render_for(self, row, who=None, vague=False):
        garbles = list(row["_garbles"])
        if vague and "name_vague" not in garbles:
            garbles.append("name_vague")
        return self.render(row["_g"], garbles, row["town"], who=who)

    def epithets(self, pt):
        """Town nicknames earned by repeated deeds: {char_id: [epithet, ...]}.
        Heroic nicknames count every live deed forever. Funny ones count only deeds from the last
        funny_epithet_window_h played hours, so a joke name fades once you stop earning it (the owner, chat, 2026-10-28)."""
        self._load()
        pt = int(pt)
        window = int(float(self.cfg.get("funny_epithet_window_h", 50)) * 3600)
        tally = {}
        q = """SELECT char_id, json_extract(facts, '$.type'), json_extract(facts, '$.subject'), COUNT(*) FROM events
               WHERE npc_id = ? AND kind = 'deed' AND json_extract(facts, '$.pt') BETWEEN ? AND ? AND status = 'live'
                 AND json_extract(facts, '$.type') IN ({}) GROUP BY 1, 2, 3"""
        for funny in (False, True):
            types = [t for t, s in self.cfg["deeds"].items()
                     if s.get("enabled", True) and s.get("epithets") and bool(s.get("funny")) == funny]
            if not types:
                continue
            since = pt - window if funny else -1
            with self.ledger.rlock:
                rows = self.ledger.rdb.execute(q.format(",".join("?" * len(types))),
                                               (WORLD_ID, since, pt, *types)).fetchall()
            for char, typ, subject, n in rows:
                tally[(char, typ, subject or "")] = n
        out = {}
        for (char, typ, subject), n in tally.items():
            best = None
            for need, title in self.cfg["deeds"][typ]["epithets"]:
                if n >= need:
                    best = title.format(Subject=subject[:1].upper() + subject[1:], subject=subject)
            if best:
                out.setdefault(char, []).append(best)
        return out

    def tick(self, pt, limit=20):
        """Arrivals not yet logged, for the shard's once-a-minute log line."""
        rows = self.view(pt)
        new = []
        with self.ledger.lock:
            for r in rows:
                if len(new) >= limit:
                    break  # the rest are logged on the next tick, never marked logged unseen
                cur = self.ledger.db.execute("INSERT OR IGNORE INTO rumour_arrivals (id, town, pt) VALUES (?, ?, ?)",
                                             (r["id"], r["town"], r["arrived_pt"]))
                if cur.rowcount:
                    new.append(r)
            # Retention: nothing older than the horizon can be served again.
            old = int(pt) - self.horizon()
            self.ledger.db.execute("DELETE FROM rumour_arrivals WHERE pt < ?", (old,))
            self.ledger.db.execute(
                """DELETE FROM rumour_drops WHERE id IN (SELECT event_id FROM events
                   WHERE npc_id = ? AND kind = 'deed' AND json_extract(facts, '$.pt') < ?)""", (WORLD_ID, old))
        return new

    def drop(self, rid, now_utc):
        with self.ledger.lock:
            self.ledger.db.execute("INSERT OR IGNORE INTO rumour_drops (id, at_utc) VALUES (?, ?)", (str(rid)[:40], now_utc))
