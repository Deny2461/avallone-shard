"""Stage A3 step 2 (2026-11-10, Projects/Avallone-Stage-A3-NPC-Memory-Design.md, step 2 design v2, Codex design R2 GO):
which broad subjects came up in an exchange - by keyword, in code, with no model call.

Two separate things (Codex S2-D1):
- the permanent id registry is acquaint.TOPIC_IDS (code, append-only). Whether a stored row is valid depends on that only.
- THIS module reads the tunable classifier file, topics.json: for each id a label, whole-word keywords and whether
  it may be OFFERED as a fresh everyday subject ("suggest").
  Keywords and labels may change freely; ingest never opens the file.

Nothing here may cost an exchange line: candidate() and suggest_map() never raise. A missing or broken file means "no
classification offered" (None), which is not the same as "classified, nothing matched" ([]).
"""
import json
import os
import re

import acquaint

HERE = os.path.dirname(os.path.abspath(__file__))
_WORD = re.compile(r"^[a-z][a-z' -]{0,30}$")
_CACHE = {"key": None, "cfg": None}


def path():
    return os.environ.get("AVALLONE_TOPICS") or os.path.join(HERE, "topics.json")


def build(raw):
    """The usable classifier from the file's content. Ids that are not in the registry are ignored (and listed)."""
    src = raw.get("topics") if isinstance(raw, dict) else None
    if not isinstance(src, dict):
        raise ValueError("topics.json needs a 'topics' object")
    out = {}
    for tid in acquaint.TOPIC_IDS:          # registry order = tie-break order
        t = src.get(tid)
        if not isinstance(t, dict):
            continue
        label = t.get("label")
        label = " ".join(label.split())[:40] if isinstance(label, str) else ""
        words = []
        for w in t.get("words") if isinstance(t.get("words"), list) else []:
            w = " ".join(w.lower().replace("’", "'").split()) if isinstance(w, str) else ""
            if _WORD.match(w) and w not in words:
                words.append(w)
        rx = re.compile(r"(?<![a-z])(?:" + "|".join(re.escape(w) for w in sorted(words, key=len, reverse=True))
                        + r")(?![a-z])") if words else None
        out[tid] = {"label": label, "rx": rx, "suggest": t.get("suggest") is True}
    return {"topics": out, "ignored": sorted(str(k)[:30] for k in src if k not in acquaint.TOPIC_SET)}


def load(p=None):
    p = p or path()
    st = os.stat(p)
    key = (p, st.st_mtime_ns, st.st_size)
    if _CACHE["key"] != key:
        with open(p, encoding="utf-8") as f:
            cfg = build(json.load(f))
        _CACHE["key"], _CACHE["cfg"] = key, cfg
    return _CACHE["cfg"]


def reset():
    _CACHE["key"], _CACHE["cfg"] = None, None


def classify(lines, cfg):
    """Up to acquaint.MAX_TOPICS ids: most keyword hits first, registry order breaking ties. [] when nothing matched."""
    text = "\n".join(str(x) for x in lines).lower().replace("’", "'")
    hits = []
    for i, (tid, t) in enumerate(cfg["topics"].items()):
        n = len(t["rx"].findall(text)) if t["rx"] else 0
        if n:
            hits.append((-n, i, tid))
    return [tid for _, _, tid in sorted(hits)[:acquaint.MAX_TOPICS]]


def candidate(lines, enabled, p=None):
    """The step answer's `topics`: a list of ids, [] for "nothing matched", or None for "no classification offered"
    (switched off, file missing or broken, no usable keywords, any error). `lines` are exchange lines only - the caller
    never passes the opening line. NEVER raises."""
    try:
        if not enabled:
            return None
        cfg = load(p)
        if not any(t["rx"] for t in cfg["topics"].values()):
            return None
        return classify(lines, cfg)
    except Exception:
        return None


def suggest_map(p=None):
    """{id: label} for the ids that may be offered as a fresh everyday subject. NEVER raises: {} on any trouble."""
    try:
        return {tid: t["label"] for tid, t in load(p)["topics"].items() if t["label"] and t["suggest"]}
    except Exception:
        return {}
