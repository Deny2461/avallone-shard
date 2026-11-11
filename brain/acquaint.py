"""Stage A3 step 1 (2026-11-10, Projects/Avallone-Stage-A3-NPC-Memory-Design.md v2, Codex design R2 GO): an NPC remembers
WHO it has traded lines with - a companion today, another NPC later - never what was said.

The shard writes one `talked_with` event per rememberer when a Stage A2 exchange said a line. Facts only:
{day, mpt, other_kind, other_id, lines}. No names, no words that were said, nothing a player typed. The name used in a
prompt always comes from the request being answered.

Read side: the ledger's acquaintances() (memory.py) - reconciled for this shard epoch or nothing, newest-first tail of at
most TAIL examined rows, grouped by the other speaker, the QUEUE newest speakers kept. This module turns one entry into one
sentence. A miss says nothing (it may be forgotten, pushed out, or older than this feature); "unavailable" says nothing too.
"""
import math
import random
import re
from datetime import date, datetime

KIND = "talked_with"
OTHER_KINDS = ("companion", "townsperson", "named")
TAIL = 400       # rows EXAMINED per read, including ones skipped (Codex design R2)
QUEUE = 8        # other speakers an NPC keeps
DEFAULT_FADE_HOURS = 120.0   # memory-hours (the owner, chat, 2026-11-10); memory_rules.json acquaintance_fade_memory_hours
_DAY = re.compile(r"^\d{4}-\d{2}-\d{2}$")
_ID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")
KEYS = {"day", "mpt", "other_kind", "other_id", "lines"}
_RNG = random.Random()
# Step 2 (Codex S2-D1): the PERMANENT topic-id registry. Ids are only ever appended - never removed, renamed or given a new
# meaning - so a stored or queued row can never become invalid because the keyword file (topics.json) was tuned.
# The order is the tie-break order. test_topics.py pins this list release by release.
TOPIC_IDS = ("road", "weather", "trade", "food", "healing", "work", "animals", "sea", "town", "family", "rest", "clothes",
             "danger", "magic", "land")
TOPIC_SET = frozenset(TOPIC_IDS)
MAX_TOPICS = 2      # ids on one stored row
FRESH_TOPICS = 2    # uncovered everyday subjects offered in one prompt


def _num(v):
    return not isinstance(v, bool) and isinstance(v, (int, float)) and math.isfinite(v)


def clean_topics(raw):
    """(ids, why): the optional `topics` field reduced to what is certain - registry ids, no duplicates, at most
    MAX_TOPICS. `why` names the first thing that had to be cleaned ("" when nothing was). Never raises and never keeps
    anything that is not a registry id, so the stored field cannot hold free text."""
    if not isinstance(raw, list):
        return [], "not_list"
    out, why = [], ""
    for x in raw[:20]:
        if isinstance(x, str) and x in TOPIC_SET:
            if x in out:
                why = why or "duplicate"
            else:
                out.append(x)
        else:
            why = why or "unknown"
    if len(raw) > 20 or len(out) > MAX_TOPICS:
        why = why or "too_many"
    return out[:MAX_TOPICS], why


def check_facts(facts):
    """The five base keys are checked strictly: anything wrong there, or any other extra key, is rejected at the door.
    The optional `topics` is CLEANED, never a reason to refuse (Codex S2-D1: the shard drops a rejected row for good).
    Returns (facts to store, why-cleaned): the base keys in the order they came, plus `topics` only if ids survive."""
    if not isinstance(facts, dict) or not KEYS <= set(facts) or set(facts) - KEYS - {"topics"}:
        raise ValueError("talked_with needs exactly {day, mpt, other_kind, other_id, lines} (+ optional topics)")
    if not isinstance(facts["day"], str) or not _DAY.match(facts["day"]):
        raise ValueError("talked_with day malformed")
    try:
        date.fromisoformat(facts["day"])
    except ValueError:
        raise ValueError("talked_with day malformed") from None
    if not _num(facts["mpt"]) or facts["mpt"] < 0:
        raise ValueError("talked_with mpt malformed")
    if facts["other_kind"] not in OTHER_KINDS:
        raise ValueError("talked_with other_kind malformed")
    if not isinstance(facts["other_id"], str) or not _ID.match(facts["other_id"]):
        raise ValueError("talked_with other_id malformed")
    n = facts["lines"]
    if isinstance(n, bool) or not isinstance(n, int) or not 1 <= n <= 10:
        raise ValueError("talked_with lines malformed")
    out = {k: v for k, v in facts.items() if k != "topics"}
    why = ""
    if "topics" in facts:
        ids, why = clean_topics(facts["topics"])
        if ids:
            out["topics"] = ids
    return out, why


def rank_topics(by_day):
    """Step 2 reader (Codex Q2). `by_day`: {day: set of ids} for ONE other speaker - the union of that day's rows, so the
    duplicate rows a shard restart writes add no weight. Most distinct days first, then the most recent such day, then
    registry order. A sample of what came up, not a complete list."""
    score = {}
    for day, ids in by_day.items():
        for t in ids:
            if t in TOPIC_SET:
                n, last = score.get(t, (0, ""))
                score[t] = (n + 1, max(last, day))
    order = {t: i for i, t in enumerate(TOPIC_IDS)}
    # newest day first among equals: sort by registry order, then stably by day descending, then by count descending
    ranked = sorted(score, key=lambda t: order[t])
    ranked.sort(key=lambda t: score[t][1], reverse=True)
    ranked.sort(key=lambda t: score[t][0], reverse=True)
    return ranked


def parse_known(raw):
    """The read request the shard attaches ({epoch, mpt, other_id}), or None: no read is made."""
    if not isinstance(raw, dict):
        return None
    ep, mpt, oid = raw.get("epoch"), raw.get("mpt"), raw.get("other_id")
    if isinstance(ep, bool) or not isinstance(ep, int) or ep < 1 or not _num(mpt) or mpt < 0:
        return None
    if not isinstance(oid, str) or not _ID.match(oid):
        return None
    return {"epoch": ep, "mpt": float(mpt), "other_id": oid}


def fade_hours(rules):
    v = (rules or {}).get("acquaintance_fade_memory_hours", DEFAULT_FADE_HOURS)
    return float(v) if _num(v) and v > 0 else DEFAULT_FADE_HOURS


def look(ledger, npc_id, known, rules=None, enabled=True):
    """(state, entry). state: "off" | "unavailable" | "none" | "known". entry: {"days": n, "last_day": "YYYY-MM-DD"}."""
    if not enabled or known is None or ledger is None:
        return "off", None
    try:
        found = ledger.acquaintances(str(npc_id), known["epoch"], known["mpt"], fade_hours(rules) * 3600.0)
    except Exception:
        return "unavailable", None
    if found is None:
        return "unavailable", None
    entry = found.get(known["other_id"])
    return ("known", entry) if entry else ("none", None)


def remark3(ledger, npc_id, known, who, rules_fn=None, enabled=True, tz=None, suggest_fn=None, path="exchange", pick=None):
    """Step 2: (state, sentence, fresh). The sentence is ALWAYS the step-1 sentence - covered subjects are never named
    (trial 1) - and `fresh` is up to two everyday subjects the two have NOT covered: [] when topics are off
    (`suggest_fn` None), nothing is on record, or anything at all goes wrong. NEVER raises; trouble with the subjects
    costs only the subjects. State "known_topics" when fresh subjects are offered. On the "party" path the offer is one
    extra sentence scoped to the aside to the companion; on the "exchange" path the caller hands `fresh` to
    exchange.rules(), which swaps them into its list of everyday subjects."""
    state, text = remark(ledger, npc_id, known, who, rules_fn, enabled, tz)
    if state != "known" or not suggest_fn:
        return state, text, []
    try:
        entry = look(ledger, npc_id, known, rules_fn() if rules_fn else None, enabled)[1]
        fresh = [str(x) for x in fresh_labels(entry, suggest_fn, pick)][:FRESH_TOPICS]
    except Exception:
        fresh = []
    if fresh and path == "party":
        text += f" If you add a word to {who}, {listed_or(fresh)} would be fresh ground between you."
    return ("known_topics" if fresh else "known"), text, fresh


def listed_or(labels):
    return labels[0] if len(labels) == 1 else ", ".join(labels[:-1]) + " or " + labels[-1]


def remark(ledger, npc_id, known, who, rules_fn=None, enabled=True, tz=None):
    """(state, sentence) for a prompt. NEVER raises (Codex code R1, A3-C2): this memory is optional, so a missing or
    broken rules file, a broken ledger or a bad entry costs the sentence - never the exchange line, never the A1 party
    paragraph. The rules file is not even opened when the switch is off or the shard asked for nothing."""
    try:
        if not enabled or known is None or ledger is None:
            return "off", ""
        state, entry = look(ledger, npc_id, known, rules_fn() if rules_fn else None, enabled)
        if state != "known":
            return state, ""
        text = sentence(entry, who, central_today(tz))
        return ("known", text) if text else ("none", "")
    except Exception:
        return "unavailable", ""


def fresh_labels(entry, suggest_fn, pick=None, n=FRESH_TOPICS):
    """Trial 1 (2026-11-10): NAMING the covered subjects did not move the talk off them (24/48 -> 28/48 lines on the road
    or the weather) - a named subject is a primed subject. So the memory is used the other way round: up to `n` everyday
    subjects these two have NOT covered, for the prompt to offer (trial 2: 29/48 -> 16/48). [] when nothing is on record
    or there is nothing to offer."""
    ids = entry.get("topics") if isinstance(entry, dict) else None
    if not suggest_fn or not ids:
        return []
    have = suggest_fn() or {}
    covered = set(ids)
    pool = [have[t].strip() for t in TOPIC_IDS if t in have and t not in covered and isinstance(have[t], str) and have[t].strip()]
    if len(pool) <= n:
        return pool
    return list((pick or _RNG.sample)(pool, n))


def offer(suggest_fn, pick=None, n=FRESH_TOPICS):
    """Two everyday subjects for an exchange between speakers with nothing on record (a first meeting, memory off or
    unavailable): the same offer, with nothing excluded. the owner, chat, 2026-11-10
    NEVER raises: [] on any trouble."""
    try:
        have = suggest_fn() or {}
        pool = [have[t].strip() for t in TOPIC_IDS if t in have and isinstance(have[t], str) and have[t].strip()]
        return [str(x) for x in (pool if len(pool) <= n else (pick or _RNG.sample)(pool, n))][:n]
    except Exception:
        return []


def how_often(days):
    return "once" if days <= 1 else "a few times" if days <= 4 else "often"


def when_last(last_day, today):
    """Central calendar days, never the game clock: a chat from last week is not "earlier today" because nobody played."""
    try:
        gap = (date.fromisoformat(today) - date.fromisoformat(last_day)).days
    except (TypeError, ValueError):
        return "a while back"
    if gap <= 0:
        return "earlier today"
    if gap == 1:
        return "yesterday"
    if gap <= 6:
        return "a few days ago"
    return "a while back"


def sentence(entry, who, today):
    """One sentence for the prompt, or "" (a miss adds nothing - Codex design R1-6)."""
    if not entry or not who:
        return ""
    often, last = how_often(int(entry["days"])), when_last(entry["last_day"], today)
    # Wording v2 (real-model trial 2026-11-10: v1 "you know each other by sight ... do not invent any" changed about 3 of
    # 24 lines - it read as a caution, not as a fact to act on). Now it says how to treat them, then the limit.
    if often == "once":
        lead = f"You and {who} have chatted once before, {last}. Treat {who} as someone you have met, not as a stranger."
    elif often == "often":
        lead = f"You and {who} have chatted often before, last {last}. By now {who} is a familiar face to you: speak as to someone you know well."
    else:
        lead = f"You and {who} have chatted a few times before, last {last}. Treat {who} as someone you know, not as a stranger."
    return lead + " You do not remember what was said in those talks, so refer to nothing from them and do not invent any."


def central_today(tz, now=None):
    return (now or datetime.now(tz)).astimezone(tz).strftime("%Y-%m-%d")
