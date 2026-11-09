"""Stage A1 (2026-11-09, Projects/Avallone-Stage-A-NPCs-Address-Companions.md): a townsperson or Mirabel is told who travels
with the player - companions by name + title, pets by species - and MAY speak to or about them (the owner
the shard counts that and sets `nudge`).

Rules (Codex Stage A design review F4):
- Only what anyone standing there could see. The shard sends name + title for companions and species (+ the player-chosen pet
  name) for pets; nothing private ever reaches this module.
- A player-chosen pet name never reaches the model. The model sees a per-reply token ({PET1}); the checked name is bound in
  after the reply passed every other check, then the whole reply is decency-checked again (a hit falls back to the species).
- A pet name that is badly shaped, too long, fails the decency list, or shares a word with a player name on the roster or with a
  companion's name is dropped - the pet is then mentioned by species only.
- A companion whose name shares a word with a roster player name is offered by title only, so the reply name check (which
  protects real players) never has to be loosened.
Memory only, nothing stored."""
import re

import decency

MAX_COMPANIONS = 2
MAX_PETS = 4                      # up to 3 pets nearby + the mount being ridden
TOKEN_RE = re.compile(r"\{PET(\d)\}")
_NAME = re.compile(r"^[A-Za-z][A-Za-z' \-]{0,38}[A-Za-z]$")
_TITLE = re.compile(r"^[A-Za-z][A-Za-z' \-]{0,38}[A-Za-z]$")
_SPECIES = re.compile(r"^[a-z][a-z' \-]{0,28}[a-z]$")
_LABEL = re.compile(r"^[A-Za-z0-9][A-Za-z0-9' \-]{0,14}[A-Za-z0-9]$|^[A-Za-z]$")
_WORD = re.compile(r"[a-z0-9']+")


def _words(s):
    return set(_WORD.findall(str(s or "").lower()))


def _species(raw):
    s = str(raw or "").strip().lower()
    for art in ("a ", "an ", "the "):
        if s.startswith(art):
            s = s[len(art):]
    return s if _SPECIES.match(s) and not decency.check(s) else ""


def parse(req, roster_names=()):
    """The validated party for this request, or None. `roster_names`: real player names the reply check protects."""
    raw = req.get("party")
    if not isinstance(raw, dict):
        return None
    taken = set()
    # Codex A1-2: the party carries the shard's own player roster, so the clash rules hold on every request - not only
    # when a gossip roster happened to come along (a missed greeting draw, an ordinary question, gossip off).
    own = raw.get("roster") if isinstance(raw.get("roster"), list) else []
    for n in list(roster_names or ()) + [str(x) for x in own[:2000] if isinstance(x, str)]:
        taken |= _words(n)
    comps = []
    for c in (raw.get("companions") or [])[:MAX_COMPANIONS]:
        if not isinstance(c, dict):
            continue
        name, title = str(c.get("name") or "").strip(), str(c.get("title") or "").strip()
        if not _NAME.match(name) or decency.check(name):
            continue
        if title and (not _TITLE.match(title) or decency.check(title)):
            title = ""
        clash = bool(_words(name) & taken)
        if clash and not title:
            continue                                   # nothing safe to call them by
        comps.append({"name": name, "title": title, "by_title": clash, "first": name.split()[0]})
    comp_words = set()
    for c in comps:
        comp_words |= _words(c["name"])
    pets, n = [], 0
    for p in (raw.get("pets") or [])[:MAX_PETS]:
        if not isinstance(p, dict):
            continue
        sp = _species(p.get("species"))
        if not sp:
            continue
        label = str(p.get("label") or "").strip()
        ok = (bool(label) and bool(_LABEL.match(label)) and len(label.split()) <= 2 and not decency.check(label)
              and label.lower() != sp and not (_words(label) & (taken | comp_words)))
        tok = None
        if ok:
            n += 1
            tok = "{PET%d}" % n
        pets.append({"species": sp, "label": label if ok else "", "token": tok, "ridden": bool(p.get("ridden"))})
    if not comps and not pets:
        return None
    return {"companions": comps, "pets": pets, "nudge": raw.get("nudge") is True}


def _article(sp):
    return ("an " if sp[:1] in "aeiou" else "a ") + sp


def block(party):
    """One paragraph for the prompt (data, never instructions from the player). Wording after the 2026-11-09 trials:
    v1 ("you may ... you need not") gave 0/24 unprompted mentions, the nudge reached only the pet (7/24, never the
    companion), and a pet's name was once used as if it were the traveller's. v2 (plain sight, "folk notice") gave 6/24 and
    17/24, but some replies answered the traveller's question to the companion, by full name. v3 (answer the traveller;
    first name) gave 16/24 unprompted - too chatty - and names tacked onto the answer. v4: now and then, one separate
    sentence, never a name tacked on."""
    if not party:
        return ""
    seen = []
    for c in party["companions"]:
        if c["by_title"]:
            seen.append(f"their travelling companion, {c['title']} (call them that, not by a name)")
        else:
            seen.append(f"their travelling companion {c['name']}" + (f", {c['title']}" if c["title"] else ""))
    for p in party["pets"]:
        what = f"their {p['species']}"
        if p["token"]:
            what += f", which they call {p['token']}"
        seen.append(("the " + p["species"] + " they ride" + (f", called {p['token']}" if p["token"] else "")) if p["ridden"] else what)
    parts = ["Right beside the person you are talking to, in plain sight: " + "; ".join(seen) + "."]
    first = [c["name"].split()[0] for c in party["companions"] if not c["by_title"]]
    parts.append("Folk in town notice company like that. Your answer is for the traveller. Now and then, when it fits - not "
                 "every time - add one short separate sentence to or about their company: to a companion by first name"
                 + (f" ({', '.join(first)})" if first else "") + ", or about an animal. Never tack a companion's name onto your "
                 "answer to the traveller, and never call the traveller by an animal's name. Say nothing about them beyond "
                 "what you can see. Write an animal's name exactly as given in curly brackets.")
    if party["nudge"]:
        tgt = next((c for c in party["companions"] if not c["by_title"]), None)
        if tgt is not None:
            parts.append(f"This time, include a short word to or about {tgt['name'].split()[0]}.")
        elif party["companions"]:
            parts.append(f"This time, include a short word to or about {party['companions'][0]['title']}.")
        else:
            p = party["pets"][0]
            parts.append(f"This time, include a short remark about their {p['species']}.")
    return " ".join(parts)


def bind(say, party):
    """Put the checked pet names in for their tokens; any other token becomes the plain species (or "your animal").
    The bound reply is decency-checked again; on a hit every name falls back to its species."""
    if not party or "{PET" not in str(say):
        return say
    by_tok = {p["token"]: p for p in party["pets"] if p["token"]}

    def fill(use_names):
        def one(m):
            p = by_tok.get(m.group(0))
            if p is None:
                return "your animal"
            return p["label"] if use_names else "your " + p["species"]
        return TOKEN_RE.sub(one, say)
    out = fill(True)
    if decency.check(out):
        out = fill(False)
    return out


def mentioned(say, party):
    """For the log: did the final reply name or describe any of them (first name, title's last word, pet name, species)."""
    if not party:
        return False
    w = _words(say)
    for c in party["companions"]:
        if (not c["by_title"] and c["first"].lower() in w) or (c["title"] and c["title"].split()[-1].lower() in w):
            return True
    for p in party["pets"]:
        if (p["label"] and _words(p["label"]) <= w) or p["species"].split()[-1] in w:
            return True
    return False


def log_fields(party, say):
    if not party:
        return {}
    return {"companions": len(party["companions"]), "pets": len(party["pets"]),
            "named_pets": sum(1 for p in party["pets"] if p["token"]),
            "by_title": sum(1 for c in party["companions"] if c["by_title"]),
            "nudge": party["nudge"], "mentioned": mentioned(say, party)}
