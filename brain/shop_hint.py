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
# Codex round 3 R2-3 exclusion lists (ABSTRACT, NOT_GOODS) were replaced by the positive goods test below (R3-3).
# Codex round 4 prep R3-3 (2026-11-06): "Do you have any FRESH news?", "How much will you miss me?", "What is the price of
# keeping a secret?" still fired. The generic branches (do-you-have-any, how-much, price, cost) now need a THING TO BUY in
# the same sentence: a goods word, an explicit trade word, or a bare pointer at the goods ("how much is that?").
GOODS = (r"(?:ale|beer|wine|mead|cider|stew|soup|bread|loaf|loaves|meals?|food|rooms?|beds?|flasks?|bottles?|potions?|"
         r"bandages?|oil|lanterns?|torch|torches|candles?|rope|arrows?|bolts?|swords?|blades?|daggers?|axes?|maces?|shields?|"
         r"armou?r|helms?|bows?|tools?|pick|pickaxe|shovel|cloth|leather|pelts?|hides?|furs?|herbs?|reagents?|cheese|apples?|"
         r"fruit|fish|meat|pies?|drinks?|pints?|round|supplies|provisions|goods|wares|stock|items?|things?|feed|oats|horses?|"
         r"boots|cloaks?|clothes|ingots?|ore|wood|logs|lumber|salt|spices?|grain|flour|eggs|milk|honey|ham|sausages?|jerky|"
         r"rations|tea|scrolls?|books?|maps?|lockpicks|kits?|rings?|gems?|jewels?|necklaces?|bags?|packs?|backpacks?|pouch|"
         r"sacks?|tents?|bedrolls?|blankets?|whetstones?|lamps?|buckets?|nails|thread|needles?|dyes?|wool|pots?|pans?|kettles?|"
         r"mugs?|tankards?|barrels?|kegs?|crates?|bundles?|bowls?|cups?|plates?|nights?|stuff)")
# Round 5b (2026-11-06): "How much does it cost a person to forgive?" fired. cost / price words no longer count as a trade
# word for "how much" - those sentences go through the PRICE test below, which wants a thing being priced.
TRADE = r"(?:charge|charges|sell|buy|pay|coins?|gold|copper|silver)"
POINTER = r"(?:it|that|this|those|these|they|them|one|each|apiece)"
# Codex R4-3 (2026-11-06): "Do you have any news about bread?", "How much do horses frighten you?", "How much do you trust a
# man with gold?" fired - a goods or money word merely NEAR "how much" / "do you have any" / a price word counted. Now the
# item has to be what is asked about: "how much is/for [the] ITEM", "how much does [a] ITEM cost/be", "how much do you
# charge/want ... for", "how much gold for", "how much to stay"; "do you have any [fresh] ITEM"; "the price of ITEM",
# "ITEM costs". Up to two describing words may sit between, never a linking word ("news ABOUT bread", "fear OF horses").
DET = r"(?:a|an|the|some|any|your|my|our|this|that|these|those|one|two|three|four|five|a\s+few|each)"
# Codex R7-2 (2026-11-06): "any two words" let "idea why" / "memories involving" pass as describing horses, and "how much is
# the ale" fired on "...the ale shortage hurting the town?". Describing words are now a short list (or a goods word used as
# one: "lantern oil"), and the availability / how-much-is branches need the item to END the question, or be followed only by
# shop words ("for sale", "left", "a night", "here", "for two"). Anything unsure is left alone - no shop line.
DESC = (r"(?:fresh|good|decent|cheap|cheaper|cheapest|fine|finer|cold|hot|warm|strong|stronger|more|extra|spare|new|clean|dry|"
        r"salted|dried|smoked|roast|roasted|baked|sharp|sturdy|healing|cure|greater|lesser|small|smaller|large|larger|big|"
        r"bigger|little|light|heavy|long|short|red|white|black|blue|green|yellow|iron|steel|wooden|quality|proper|private|"
        r"single|double|whole|half|honey|apple|mutton|beef|pork|rabbit|venison|elven|dwarven)")
ADJ = r"(?:(?:" + DESC + r"|" + GOODS + r")\s+){0,2}?"
ITEM = r"(?:" + DET + r"\s+)?" + ADJ + GOODS + r"\b"
# What may follow the item in an availability / price question, then the end of the sentence.
ITEM_TAIL = (r"(?:\s+(?:here|now|today|tonight|then|left|free|available|going|ready|please|each|apiece|altogether|"
             r"for\s+sale|to\s+sell|to\s+spare|in\s+stock|on\s+offer|on\s+hand|at\s+all|"
             r"these\s+days|of\s+(?:" + DESC + r"\s+)?" + GOODS + r"|"
             r"a\s+(?:night|day|week|bowl|pint|piece|head|loaf|cup|mug|plate|round)|per\s+\w+|"
             r"for\s+(?:one|two|three|four|both|us|me|each|all|the\s+lot|the\s+night|a\s+night|tonight)|"
             r"in\s+(?:gold|coins?|copper|silver))){0,3}"
             r"\s*(?:,\s*(?:\w+\s*){1,2})?(?:[.?!;]|$)")
MONEY = r"(?:gold|coins?|copper|silver|money)"
TRADE_ACT = (r"(?:eat|dine|drink|stay|sleep|lodge|board|rent|hire|stable|repair|mend|fix|sharpen|forge|shoe|buy|purchase|"
             r"order|outfit|equip|provision)")
ASKS = re.compile(
    r"\b(?:buy|buying|purchase|purchasing)\b"
    r"|\b(?:sell|selling)\b(?!\s+(?:\w+\s+)?out\b)"                       # not "sell me out"
    r"|\bhow\s+much\s+for\b"
    r"|\bhow\s+much\s+(?:is|are|'s|'re)\s+" + ITEM + ITEM_TAIL
    + r"|\bhow\s+much\s+(?:does|do|would|will|did|might|could|should)\s+" + ITEM
    + r"(?:\s+\w+){0,3}?\s+(?:cost|costs|run|be|come\s+to|go\s+for|sell\s+for|fetch)\b"
    r"|\bhow\s+much\s+(?:do|does|would|will|did|can|could|should|might)\s+(?:you|ye|I|we|they|he|she)\s+(?:\w+\s+)?"
    r"(?:charge|sell|(?:ask|want|take|give|offer|pay)\b[^.?!]{0,30}\bfor)\b"
    r"|\bhow\s+much\s+" + MONEY + r"\s+(?:for|is|'s)\b"
    r"|\bhow\s+much\s+" + MONEY + r"\s+(?:would|will|do|does)\s+(?:you|it|that|this|a|the)\s+(?:\w+\s+){0,2}?"
    r"(?:charge|want|ask|take|cost|costs|be|run)\b"
    r"|\bhow\s+much\s+to\s+(?:\w+\s+){0,2}?" + TRADE_ACT + r"\b"
    r"|\bhow\s+much\s+(?:is|are|for|does|do|would|will)\s+" + POINTER + r"\b\s*(?:[.?!]|$|come\s+to|run|be\b)"
    r"|\bhow\s+much\s+(?:for|is|are)\s*(?:[.?!]|$)|^\s*how\s+much\s*[.?!]*\s*$"
    r"|\bwhat\s+do\s+you\s+(?:sell|stock|serve|offer|carry|have\s+for\s+sale|charge)\b"
    r"|\bwhat(?:'s|\s+is|\s+have\s+you|'ve\s+you)\s+(?:got\s+)?(?:for\s+sale|on\s+offer|on\s+the\s+menu|in\s+stock|to\s+sell)\b"
    r"|\b(?:for\s+sale|your\s+wares|your\s+goods|your\s+stock|the\s+menu|in\s+stock)\b"
    r"|\bdo\s+you\s+(?:sell|stock|carry|serve)\b|\bdo\s+you\s+have\s+(?:any|some)\s+" + ADJ + GOODS + r"\b" + ITEM_TAIL
    # Step B (2026-11-08): "do you have a room?", "have you a room for the night?", "have you got any ale?" were missed in
    # every version. Same strictness as above: the item ends the question or only shop words follow (R7-2).
    + r"|\bdo\s+you\s+have\s+(?:a|an|one|two|three|a\s+few)\s+" + ADJ + GOODS + r"\b" + ITEM_TAIL
    + r"|\bhave\s+(?:you|ye)\s+(?:got\s+)?(?:a|an|any|some|one|two|three|a\s+few)\s+" + ADJ + GOODS + r"\b" + ITEM_TAIL
    + r"|\b(?:show|let)\s+me\s+(?:see\s+)?(?:your|the|what)\s+(?:wares|goods|stock|menu|you\s+have)\b"
    r"|\bI'?ll\s+take\s+(?:a|an|some|the|two|three)\b"
    r"|\b(?:your|the|what\s+are\s+your|what\s+are\s+the)\s+prices\b(?!\s+of\b)|\bname\s+your\s+price\b",
    re.I)
# price / cost words count only with a thing to buy in the same sentence ("what's the price of bread", "what does a flask
# cost?", "what does it cost?"), never "the price of loyalty / of keeping a secret", "that battle cost me my family".
# Round 5b (2026-11-06): "What does it cost a person to forgive?" fired on the bare pointer ("it cost"). A pointer now
# counts only when it IS the thing priced - the sentence ends there ("what does that cost?", "how much will it cost me?",
# "what does it cost here, Nell?") - or the cost is of a trade act ("what does it cost to stay / to stable my horse?").
# "what does it cost to forgive", "what does mercy cost a man", "the price of forgiveness" have no thing to buy.
PRICE = re.compile(r"\b(?:price|prices|priced|pricing|cost|costs)\b", re.I)
_PRICE_TAIL = (r"(?:\s+(?:here|now|today|tonight|then|each|apiece|altogether|in\s+all|all\s+told|these\s+days|"
               r"per\s+\w+|a\s+(?:night|day|week|bowl|pint|piece|head|loaf|cup|mug)|"
               r"for\s+(?:one|two|three|four|both|us|me|each|all|the\s+lot)|"
               r"in\s+(?:gold|coins?|copper|silver)|(?:me|us)|how\s+much|what)){0,3}"
               r"\s*(?:,\s*(?!to\b)\w+(?:\s+\w+)?\s*)?(?:[.?!;]|$)")
# R4-3: the goods word must be the thing priced ("price of ITEM", "cost of ITEM", "ITEM ... costs/priced"), not anywhere in
# the sentence ("Did the war cost you your horses?").
PRICE_OK = re.compile(r"\b(?:prices?|costs?)\s+(?:of|for|on)\s+" + ITEM + ITEM_TAIL
                      + r"|\bcosts?\s+(?:(?:me|us)\s+)?to\s+(?:\w+\s+)?" + TRADE_ACT + r"\b", re.I)
# R7-2: "the ale costs / is the ale priced" counts only in a question ("Did my horses cost me dearly" is not a price ask).
ITEM_COSTS = re.compile(r"\b" + GOODS + r"\b(?:\s+\w+){0,2}?\s+(?:cost|costs|priced|price|prices)\b", re.I)
# a bare pointer is the thing priced only in a question ("what does that cost?"), not "Ugh, that cost me."
PRICE_POINTER = re.compile(r"\b" + POINTER + r"\s+(?:cost|costs)\b" + _PRICE_TAIL
                           + r"|\bprices?\s+(?:of|for|on)\s+(?:that|this|those|these|it|them|each|one)(?:\s+one)?" + _PRICE_TAIL,
                           re.I)
ASKING = re.compile(r"\?|\b(?:what|how)\b", re.I)
SENT = re.compile(r"[^.?!;]+[.?!;]*")


def _priced(sent):
    return bool(PRICE.search(sent) and (PRICE_OK.search(sent) or
                                        ((PRICE_POINTER.search(sent) or ITEM_COSTS.search(sent)) and ASKING.search(sent))))


def hint_for(persona_key, persona_vars):
    """(key phrase, line) for an NPC that keeps a shop, else None."""
    if persona_key in BY_PERSONA:
        return BY_PERSONA[persona_key]
    trade = str((persona_vars or {}).get("trade") or "").strip().lower()
    return BY_TRADE.get(trade)


def asks_to_trade(utterance):
    u = NEGATED.sub(" ", utterance or "")
    if ASKS.search(u):
        return True
    return any(_priced(x) for x in SENT.findall(u))


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
