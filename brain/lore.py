"""Avallone lore lookup -- Phase 2.

Reads the canon, non-secret pages that a host timer `avallone-lore-sync` copies into /opt/avallone-lore,
splits them into `##` sections, and picks the few sections that fit what a player just said and that the
speaking NPC is allowed to know.

The lookup contract (wiki Admin/Handoff-2026-10-01-ChatGPT-Worldbuilding-Brief.md, "answers sent to ChatGPT"):
- only pages with `status: canon` and `canon: true`;
- never pages with `visibility: secret` or `spoiler_level: secret` (the sync already leaves them out; checked again here);
- a page is visible to an NPC only if its `known_by` shares a group with the NPC's groups;
- "Heard in <Town>" sections only for NPCs whose town is <Town>.
No trust levels, player location or live world state yet (later phases).

The index reloads by itself when the sync writes a new VERSION, so a wiki edit reaches NPCs without a restart.
Standard library only.
"""

import os
import re
import threading
import unicodedata

STOP = set("""
a about above after again against all am an and any are as at be because been before being below between both but by
can could did do does doing down during each few for from further had has have having he her here hers herself him
himself his how i if in into is it its itself just me more most my myself no nor not now of off on once only or other
our ours ourselves out over own same she should so some such than that the their theirs them themselves then there
these they this those through to too under until up very was we were what when where which while who whom why will
with would you your yours yourself yourselves tell know heard hear any anything something news say said ask
thee thou thy hail good evening morning day well ye aye pray
""".split())

HEARD_IN = re.compile(r"^heard in (.+)$", re.I)
# "Any news?" names no topic; it asks for the local rumours.
ASKS_NEWS = re.compile(r"\b(news|rumou?rs?|gossip|tidings|happenings?|going on|word on|heard anything|what'?s new)\b")


def fold(text):
    """Lower-case, accents off, curly quotes to plain: Nujel'm and Nujel’m and NUJEL'M all match."""
    text = unicodedata.normalize("NFKD", text)
    text = "".join(c for c in text if not unicodedata.combining(c))
    return text.replace("’", "'").replace("‘", "'").lower()


def words(text):
    out = []
    for w in re.findall(r"[a-z0-9']+", fold(text)):
        w = w.strip("'")
        if w.endswith("'s"):
            w = w[:-2]
        w = w.replace("'", "")
        if len(w) > 3 and w.endswith("s") and not w.endswith("ss"):
            w = w[:-1]
        if len(w) > 2 and w not in STOP:
            out.append(w)
    return out


def parse_list(value):
    value = value.strip()
    if value.startswith("[") and value.endswith("]"):
        return [v.strip() for v in value[1:-1].split(",") if v.strip()]
    return [value] if value else []


def split_front_matter(text):
    """Return (meta, body). Front matter: one `key: value` per line between `---` lines, `# comment` allowed."""
    meta, rest = {}, text
    if text.startswith("---"):
        parts = text.split("\n---", 1)
        if len(parts) == 2:
            for line in parts[0].splitlines()[1:]:
                if ":" not in line:
                    continue
                key, value = line.split(":", 1)
                value = re.sub(r"\s+#.*$", "", value).strip()
                meta[key.strip()] = value
            rest = parts[1].split("\n", 1)[1] if "\n" in parts[1] else ""
    return meta, rest


def parse_page(text):
    """Return (meta, title, [(heading, body)])."""
    meta, rest = split_front_matter(text)
    title, sections, heading, body = meta.get("title", ""), [], None, []
    for line in rest.splitlines():
        if line.startswith("# ") and not title:
            title = line[2:].strip()
        elif line.startswith("## "):
            if heading and " ".join(body).strip():
                sections.append((heading, " ".join(" ".join(body).split())))
            heading, body = line[3:].strip(), []
        elif heading is not None:
            body.append(line.strip())
    if heading and " ".join(body).strip():
        sections.append((heading, " ".join(" ".join(body).split())))
    return meta, title, sections


def usable(meta):
    return (fold(meta.get("status", "")) == "canon"
            and fold(meta.get("canon", "")) == "true"
            and fold(meta.get("visibility", "")) != "secret"
            and fold(meta.get("spoiler_level", "")) != "secret")


class LoreIndex:
    def __init__(self, root):
        self.root = root
        self.version = None
        self.sections = []
        self.pages = 0
        self.error = None
        self._lock = threading.Lock()

    def _read_version(self):
        try:
            with open(os.path.join(self.root, "VERSION"), encoding="utf-8") as f:
                return f.read().strip()
        except OSError:
            return None

    def refresh(self, force=False):
        """Cheap when nothing changed: one small file read."""
        version = self._read_version()
        if not force and version == self.version and self.sections:
            return False
        with self._lock:
            if not force and version == self.version and self.sections:
                return False
            sections, pages = [], 0
            try:
                for dirpath, dirnames, filenames in os.walk(self.root):
                    dirnames.sort()
                    for name in sorted(filenames):
                        if not name.endswith(".md") or name.lower() == "readme.md":
                            continue
                        path = os.path.join(dirpath, name)
                        rel = os.path.relpath(path, self.root)[:-3]
                        with open(path, encoding="utf-8") as f:
                            meta, title, secs = parse_page(f.read())
                        if not usable(meta):
                            continue
                        pages += 1
                        known_by = {fold(g) for g in parse_list(meta.get("known_by", ""))}
                        regions = {fold(r) for r in parse_list(meta.get("regions", ""))}
                        rumour = fold(meta.get("truth_status", "")) in ("rumor", "rumour")
                        title_words = set(words(title + " " + rel.replace("-", " ").replace("/", " ")))
                        for heading, body in secs:
                            m = HEARD_IN.match(heading)
                            sections.append({
                                "page": rel, "title": title, "heading": heading, "text": body,
                                "known_by": known_by, "regions": regions, "rumour": rumour,
                                "heard_in": fold(m.group(1)).strip() if m else None,
                                "title_words": title_words,
                                "heading_words": set(words(heading)),
                                "body_words": set(words(body)),
                            })
                self.sections, self.pages, self.version, self.error = sections, pages, version, None
            except Exception as e:  # keep the last good index; say why in /health
                self.error = f"{type(e).__name__}: {e}"
                return False
        return True

    def visible(self, section, groups, town, exclude):
        # exclude holds whole pages ("People/Mirabel") or single sections ("Rumours/X#Heard in Britain").
        if section["page"] in exclude or f'{section["page"]}#{section["heading"]}' in exclude:
            return False
        if not (section["known_by"] & groups):
            return False
        if section["heard_in"] is not None and section["heard_in"] != town:
            return False
        return True

    def lookup(self, persona, utterance, history=(), limit=3, budget_chars=1400):
        """Best sections for this NPC and this line. History adds context at half weight."""
        self.refresh()
        groups = {fold(g) for g in persona.get("groups", [])} | {"everyone"}
        town = fold(persona.get("town", ""))
        exclude = set(persona.get("exclude", []))
        now = set(words(utterance))
        before = set()
        for line in list(history)[-4:]:
            if line.get("speaker") != "npc":
                before |= set(words(str(line.get("text") or "")))
        before -= now
        news = bool(ASKS_NEWS.search(fold(utterance)))
        if not now and not before and not news:
            return []
        scored = []
        for s in self.sections:
            if not self.visible(s, groups, town, exclude):
                continue
            score = 0.0
            for terms, weight in ((now, 1.0), (before, 0.5)):
                score += weight * (3 * len(terms & s["title_words"])
                                   + 2 * len(terms & s["heading_words"])
                                   + len(terms & s["body_words"]))
            if news and s["heard_in"] is not None:
                score += 3  # visible() already limited "Heard in" sections to the NPC's own town
            if score <= 0:
                continue
            if town and town in s["regions"]:
                score += 0.5
            scored.append((score, s))
        scored.sort(key=lambda x: (-x[0], x[1]["page"], x[1]["heading"]))
        picked, used = [], 0
        for score, s in scored:
            if len(picked) >= limit:
                break
            if score < 2:  # one stray body-word match is noise, not relevance
                break
            if used + len(s["text"]) > budget_chars and picked:
                continue
            picked.append(dict(score=round(score, 1), page=s["page"], title=s["title"],
                               heading=s["heading"], text=s["text"], rumour=s["rumour"]))
            used += len(s["text"])
        return picked

    def stats(self):
        return {"version": (self.version or "")[:12] or None, "pages": self.pages,
                "sections": len(self.sections), "error": self.error}


def prompt_block(sections):
    if not sections:
        return ""
    lines = ["Things you know that may bear on this (use only if they fit; speak in your own words, never "
             "recite; if it is marked rumour, you only heard it and may doubt it):"]
    for s in sections:
        tag = " (rumour)" if s["rumour"] else ""
        lines.append(f"- {s['title']}, {s['heading']}{tag}: {s['text']}")
    return "\n".join(lines)
