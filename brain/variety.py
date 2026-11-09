"""Step B (the owner's dev play test 2026-11-08). Ten NPCs answered with
near-identical openings ("The sun is warm on the ...") and Tano said "I have work to do" twice to the same player.
check() spots two kinds of repeat in a model reply; the caller asks the model once more with nudge(), and keeps the
new reply only if it is not a repeat too. Memory only (lost on restart, which is fine): openings of the last KEEP replies
from ANY NPC, and the 4-word phrases of each NPC's last OWN_KEEP replies to each player, both within WINDOW_S.
Step C (2026-11-09, live log of 2026-11-08: "State your business" / "State your ailment" from four different NPCs, always
after "Hail." or "Greetings.", so the opening check never saw it): a third kind, "shared" - a 3-word phrase with two real
words in it that SHARED_MIN other NPCs have already used anywhere in a reply within WINDOW_S."""
import collections
import re
import threading
import time

WINDOW_S = 1800          # half an hour of play
KEEP = 40                # recent openings, any NPC
OWN_KEEP = 4             # one NPC's recent replies to one player
OPEN_WORDS = 3
GRAM = 4
SHARED_GRAM = 3
SHARED_MIN = 1           # other NPCs that must already have used it (replay 2026-11-09: 2 missed every case)
# Words that make a 4-word phrase "ordinary speech" when every word of length >= 4 is one of them.
COMMON = {"what", "that", "this", "with", "have", "your", "here", "there", "they", "them", "then", "than", "would",
          "could", "should", "will", "well", "just", "like", "come", "back", "good", "some", "from", "into", "about",
          "been", "were", "when", "where", "which", "while", "also", "only", "very", "more", "much", "today", "need",
          "want", "know", "said", "says", "does", "make", "take", "give", "look", "find", "keep", "help", "else"}
# Town and place names do not make a phrase "stock": every Minoc shopkeeper may say "in Minoc".
PLACES = {"britain", "minoc", "moonglow", "trinsic", "yew", "vesper", "jhelom", "skara", "brae", "magincia", "cove",
          "ocllo", "nujel'm", "nujelm", "serpent's", "delucia", "papua", "buccaneer's", "haven", "wind", "sosaria",
          "blue", "boar", "felucca", "trammel", "ilshenar", "malas", "tokuno"}
_WORD = re.compile(r"[a-z']+")
_ACTION = re.compile(r"\[[^\]]*\]")
_lock = threading.Lock()
_recent = collections.deque(maxlen=KEEP)   # (t, npc, subject, opening)
_own = {}                                  # (npc, subject) -> deque of (t, set of grams)
_shared = collections.deque(maxlen=KEEP)   # (t, npc, set of shared_grams)


def _words(text):
    return _WORD.findall(_ACTION.sub(" ", str(text or "")).lower().replace("’", "'"))


def opening(text):
    w = _words(text)[:OPEN_WORDS]
    return " ".join(w) if len(w) == OPEN_WORDS else ""


_SENTENCE = re.compile(r"(?<=[.!?])\s+")


def grams(text):
    out = set()
    for sentence in _SENTENCE.split(_ACTION.sub(" ", str(text or ""))):
        w = _words(sentence)
        if any(x.strip("'") == "vendor" for x in w):   # also when quoted: say 'vendor buy'                  # the how-to-buy sentence is fixed wording on purpose
            continue
        for i in range(len(w) - GRAM + 1):
            g = w[i:i + GRAM]
            if any(len(x) >= 4 and x not in COMMON for x in g):
                out.add(" ".join(g))
    return out


def shared_grams(text):
    """3-word phrases with at least two words of 4+ letters that are not COMMON ("state your business": yes;
    "i have a": no). The how-to-buy sentence is skipped, as in grams()."""
    out = set()
    for sentence in _SENTENCE.split(_ACTION.sub(" ", str(text or ""))):
        w = _words(sentence)
        if any(x.strip("'") == "vendor" for x in w):   # also when quoted: say 'vendor buy'
            continue
        for i in range(len(w) - SHARED_GRAM + 1):
            g = w[i:i + SHARED_GRAM]
            if not PLACES & set(g) and sum(len(x) >= 4 and x not in COMMON for x in g) >= 2:
                out.add(" ".join(g))
    return out


def check(npc, subject, text, now=None):
    """{"kind": "opening"|"phrase"|"shared", "words": ...} for the first repeat found, or None."""
    now = time.time() if now is None else now
    o, gs, ss = opening(text), grams(text), shared_grams(text)
    with _lock:
        if o:
            for t, _n, _s, o2 in _recent:
                if now - t <= WINDOW_S and o2 == o:
                    return {"kind": "opening", "words": o}
        for t, g2 in _own.get((npc, subject), ()):
            if now - t <= WINDOW_S:
                same = gs & g2
                if same:
                    return {"kind": "phrase", "words": sorted(same)[0]}
        stock = sorted(g for g in ss
                       if len({n for t, n, g2 in _shared if now - t <= WINDOW_S and n != npc and g in g2}) >= SHARED_MIN)
        if stock:
            return {"kind": "shared", "words": stock[0], "all": stock[:3]}
    return None


def note(npc, subject, text, now=None):
    now = time.time() if now is None else now
    with _lock:
        o = opening(text)
        if o:
            _recent.append((now, npc, subject, o))
        _own.setdefault((npc, subject), collections.deque(maxlen=OWN_KEEP)).append((now, grams(text)))
        _shared.append((now, npc, shared_grams(text)))
        if len(_own) > 5000:               # bound memory: drop the oldest pairs
            for k in list(_own)[:1000]:
                del _own[k]


def nudge(hit):
    if hit["kind"] == "opening":
        what = f'do not begin with "{hit["words"]}"'
    elif hit["kind"] == "shared":
        ws = hit.get("all") or [hit["words"]]
        what = "do not use the stock phrase" + ("s " if len(ws) > 1 else " ") + ", ".join(f'"{w}"' for w in ws)
    else:
        what = f'do not use the words "{hit["words"]}" again'
    return (f"Say it differently this time: {what}. Same meaning and the same mood, one or two short sentences, in your "
            "own words.")


def reset_for_tests():
    with _lock:
        _recent.clear()
        _own.clear()
        _shared.clear()
