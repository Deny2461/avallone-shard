"""Avallone Phase 4b - companion secrets, brain side (design v2, Projects/Avallone-AI-NPC-Plan.md).

The brain holds secret TEXT; the shard owns who has EARNED what. Pages arrive via a host sync in
/opt/avallone-secrets (only canon pages with visibility: secret, known_by exactly one companion, tier 1-3).

- gates(name) -> ids and tiers only, never text. The shard asks for these at start.
- A normal reply only ever gets the generic hint (that she keeps things to herself) - never text.
- Only a stage-2 request naming one secret id, which the shard has already approved, gets that one body.
Bodies never go into logs, errors or status output.
"""
import hashlib
import os
import re

import lore

VALID_TIERS = (1, 2, 3)


def parse_known_by(value):
    return [x.strip() for x in str(value or "").strip("[]").split(",") if x.strip()]


class SecretIndex:
    def __init__(self, root):
        self.root = root
        self.version = None
        self.by_id = {}        # id -> {"id", "companion", "tier", "body"}
        self.problems = []

    def refresh(self):
        """Re-read when VERSION changes. Returns True if reloaded. Invalid sets are rejected whole (keep last good)."""
        try:
            version = open(os.path.join(self.root, "VERSION"), encoding="utf-8").read().strip()
        except OSError:
            return False
        if version == self.version:
            return False
        by_id, problems = {}, []
        for name in sorted(os.listdir(self.root)):
            if not name.endswith(".md"):
                continue
            sid = name[:-3]
            try:
                text = open(os.path.join(self.root, name), encoding="utf-8").read()
                meta, body = lore.split_front_matter(text)
                known = parse_known_by(meta.get("known_by"))
                tier = int(str(meta.get("tier", "0")).strip())
                if str(meta.get("visibility")) != "secret" or len(known) != 1 or tier not in VALID_TIERS:
                    raise ValueError("needs visibility: secret, exactly one known_by, tier 1-3")
                if not body.strip():
                    raise ValueError("empty body")
                by_id[sid] = {"id": sid, "companion": known[0], "tier": tier, "body": body.strip()}
            except Exception as e:   # message names the page and the rule, never the text
                problems.append(f"{sid}: {type(e).__name__}: {str(e)[:120]}")
        tiers = {}
        for s in by_id.values():
            tiers.setdefault(s["companion"], []).append(s["tier"])
        for companion, ts in tiers.items():
            if sorted(ts) != list(range(1, len(ts) + 1)):
                problems.append(f"{companion}: tiers {sorted(ts)} are not 1..n without gaps or repeats")
        self.problems = problems
        if problems:
            return False
        self.by_id, self.version = by_id, version
        return True

    def gates(self, companion):
        return sorted(({"id": s["id"], "tier": s["tier"]} for s in self.by_id.values() if s["companion"] == companion),
                      key=lambda g: g["tier"])

    def body(self, sid, companion):
        s = self.by_id.get(sid)
        if not s or s["companion"] != companion:
            raise KeyError("no such secret for this companion")
        return s["body"]

    def stats(self):
        return {"version": (self.version or "")[:12] or None, "secrets": len(self.by_id), "problems": self.problems}


def hint_block(open_tiers, any_unrevealed):
    """What a normal reply may know. No secret text, ever."""
    if not any_unrevealed:
        return ""
    text = ("There are things about your own past you have never told anyone. You may let it show that there is "
            "something you don't speak of, but you never say what it is.")
    if open_tiers:
        text += (" You have come to trust this person enough to tell them one of those things. If the moment truly "
                 "fits - they ask about your past, or something brings it to mind - you may choose the action "
                 "\"reveal_secret\": then say only a short lead-in (that you want to tell them something), NOT the "
                 "thing itself. You will tell it in your next words.")
    return text


def reveal_instruction(body):
    return ("You have decided to tell this person something you have never told anyone. Tell it now, in your own "
            "voice, in two or three sentences, as yourself. Do not soften it into a hint. What you are telling them:\n"
            + body)


def fingerprint(text):
    return hashlib.sha256(str(text).encode()).hexdigest()[:12]


SECRET_ID = re.compile(r"^[A-Za-z0-9-]{1,80}$")
