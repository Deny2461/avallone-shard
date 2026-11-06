"""Shop hint (the owner, chat, 2026-11-04: - code adds the shop hint). When a player asks a shopkeeper about buying,
selling or prices and the model's reply leaves out the command that opens the shop ("Mirabel buy", "vendor buy"),
code adds one short line naming it. Trials 2026-11-03/04: Nell left the hint out 6-12 times in 48 in bright moods on
gemma4:26b-a4b, and Mirabel gave it 1 time in 9 on the live gemma4:12b - wording alone did not fix either.

Codex round 2 (2026-11-04) F3: the first trigger fired on ordinary talk ("How much do you trust me?", "sold me out",
"I am not here to shop"). Now: bounded transaction phrases only, negated ones removed first (so "I don't want to buy;
I want to sell" still counts as selling). F4: the caller passes the room left after anything that is appended later."""
import re

# Which NPCs keep a shop, and the line that names their command. Keyed by persona; townsfolk by their trade.
BY_PERSONA = {"mirabel": ("mirabel buy", 'Say "Mirabel buy" to see what I have.')}
BY_TRADE = {"vendor": ("vendor buy", 'Say "vendor buy" to see my wares.')}

# "not here to shop", "don't want to buy", "never sell" - removed before looking for a request.
NEGATED = re.compile(
    r"(?:\b(?:not|never|no)|n't)\s+(?:\w+\s+){0,3}?(?:to\s+)?(?:buy|buying|purchase|purchasing|sell|selling|shop|shopping|trade|trading)\b",
    re.I)
# Codex round 3 R2-3: "the price of loyalty", "that battle cost me my family", "do you have any news?" are not shopping.
ABSTRACT = (r"(?:loyalty|freedom|war|peace|love|friendship|honou?r|glory|victory|failure|betrayal|life|lives|silence|"
            r"pride|greed|power|knowledge|truth|sin|fame|vengeance|revenge|courage|trust|mercy|justice|progress)\b")
NOT_GOODS = (r"news|word|words|idea|ideas|gossip|time|clue|clues|advice|rumou?rs?|tales?|stories|questions?|family|friends|"
             r"kin|children|regrets?|doubts?|fears?|hope|plans?|memory|memories|sense|patience|luck|answers?|thoughts?|reason")
ASKS = re.compile(
    r"\b(?:buy|buying|purchase|purchasing)\b"
    r"|\b(?:sell|selling)\b(?!\s+(?:\w+\s+)?out\b)"                       # not "sell me out"
    r"|\bhow\s+much\s+(?:is|are|for|does|do\s+(?:you\s+)?(?:charge|want|ask)|will|would|to)\b"
    r"|\bhow\s+much\b[^.?!]{0,40}\b(?:cost|costs|coin|coins|gold|copper|silver)\b"
    r"|\b(?:price|prices|priced|pricing)\b(?!\s+of\s+(?:a\s+|your\s+|my\s+)?" + ABSTRACT + r")"
    r"|\b(?:cost|costs)\b(?!\s+(?:me|us|him|her|them|you|my|our|his|their|lives|a\s+life|dearly|nothing))"
    r"|\bwhat\s+do\s+you\s+(?:sell|stock|serve|offer|carry|have\s+for\s+sale|charge)\b"
    r"|\bwhat(?:'s|\s+is|\s+have\s+you|'ve\s+you)\s+(?:got\s+)?(?:for\s+sale|on\s+offer|on\s+the\s+menu|in\s+stock|to\s+sell)\b"
    r"|\b(?:for\s+sale|your\s+wares|your\s+goods|your\s+stock|the\s+menu|in\s+stock)\b"
    r"|\bdo\s+you\s+(?:sell|stock|carry|serve)\b|\bdo\s+you\s+have\s+(?:any|some)\b(?!\s+(?:" + NOT_GOODS + r")\b)"
    r"|\b(?:show|let)\s+me\s+(?:see\s+)?(?:your|the|what)\s+(?:wares|goods|stock|menu|you\s+have)\b"
    r"|\bI'?ll\s+take\s+(?:a|an|some|the|two|three)\b",
    re.I)


def hint_for(persona_key, persona_vars):
    """(key phrase, line) for an NPC that keeps a shop, else None."""
    if persona_key in BY_PERSONA:
        return BY_PERSONA[persona_key]
    trade = str((persona_vars or {}).get("trade") or "").strip().lower()
    return BY_TRADE.get(trade)


def asks_to_trade(utterance):
    return bool(ASKS.search(NEGATED.sub(" ", utterance or "")))


def needed(say, utterance, persona_key, persona_vars):
    """The shop line this reply needs, or None."""
    h = hint_for(persona_key, persona_vars)
    if not h or not asks_to_trade(utterance) or h[0] in (say or "").lower():
        return None
    return h[1]


def reserve(say, utterance, persona_key, persona_vars):
    """Characters to keep free for the shop line (0 if none is needed) - so a cosmetic action gives way first."""
    line = needed(say, utterance, persona_key, persona_vars)
    return len(line) + 1 if line else 0


def add(say, utterance, persona_key, persona_vars, room):
    """`say` with the shop line added when it is needed and the result fits in `room` (the final limit minus anything
    appended after this). Returns (say, reason): reason None = not needed, 'added', or 'no_room'."""
    line = needed(say, utterance, persona_key, persona_vars)
    if not line:
        return say, None
    out = f"{(say or '').rstrip()} {line}".strip()
    if len(out) > room:
        return say, "no_room"
    return out, "added"
