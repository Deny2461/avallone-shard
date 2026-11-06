"""Promise guard (Codex round 2 F1, 2026-11-04; the owner). An NPC must never agree that it promised, owes or
will pay the player gold, coin, a reward, a share or free goods: no such commitment exists anywhere the brain is told
about (the shard holds no promises), so any "I promised you ..." is invented. gemma4:26b-a4b conceded in at least 3 of 6
gold-claim probes ("I promised you a reward for your bravery ... we'll see what's left in the purse"); the 12b 0 of 8.

Real denials pass ("I never promised you a coin", "You say I promised you gold?"). A concession gets one retry with
NUDGE; if that still concedes, the conceding sentence is replaced by FALLBACK and the rest of the reply is kept.

v2 (same day, after the adversarial run: both models conceded ~30% of 126 claims - "I haven't forgotten the debt", "I
remember our pact", "say 'vendor buy' to settle the trade" - which no output pattern list can keep up with): the PLAYER'S
claim is detected (claim_note) and the model is told for that turn that no such promise exists. Output patterns stay as
the backstop; CLAIMED patterns apply only when the player made a claim, so ordinary shop talk is never touched."""
import re

MONEY = (r"(?:gold|coins?|crowns?|silver|copper|reward|rewards|payment|pay|purse|money|fee|share|cut|bounty|"
         r"riches|treasure|thousand|hundred|fifty|twenty|\d+|free\s+(?:ale|food|drink|drinks|room|rooms|meals?|stay))")
YOU = r"(?:you|ye|thee)"
CONCEDE = [re.compile(p, re.I) for p in (
    rf"\b(?:I|we)\s+(?:did\s+)?(?:promised|swore|vowed|pledged|agreed|offered)\s+(?:to\s+(?:give|pay)\s+)?{YOU}\b[^.!?]{{0,40}}?\b{MONEY}\b",
    rf"\b(?:I|we)\s+(?:did\s+)?(?:promised|swore|vowed|pledged|agreed|offered)\s+(?:a|an|the|some|your)?\s*(?:\w+\s+){{0,2}}{MONEY}\b",
    rf"\b(?:I|we)\s+(?:still\s+)?owe\s+{YOU}\b(?!\s+(?:no|nothing|naught|not|none|never)\b)[^.!?]{{0,30}}?\b{MONEY}\b",   # not "I owe you an apology" (Codex R2-2)
    rf"\b(?:I'?ll|I\s+will|we'?ll|we\s+will|I\s+shall)\s+(?:pay|give|hand|owe)\s+{YOU}\b[^.!?]{{0,30}}?\b{MONEY}\b",
    rf"\b{YOU}'?ll\s+(?:get|have|receive)\s+(?:your|the)\s+(?:\w+\s+){{0,2}}{MONEY}\b",
    r"\bwhat'?s\s+left\s+in\s+the\s+purse\b",
    r"\bsplit\s+the\s+(?:gold|coin|coins|purse|reward|treasure|takings)\b",
    rf"\b(?:I|we)\s+(?:said|told\s+{YOU})\s+(?:I|we)'?d\s+(?:pay|give|reward)\s+{YOU}\b",
    rf"\b{MONEY}\b[^.!?]{{0,30}}\bas\s+(?:I|we)\s+(?:promised|swore|agreed|said)\b",
)]
# Only when the player has claimed a promise/debt this turn: going along with it in softer words.
CLAIMED = [re.compile(p, re.I) for p in (
    r"\b(?:I|we)\s+(?:remember|recall|haven'?t\s+forgotten|have\s+not\s+forgotten)\s+(?:our|the|my|that)\s+"
    r"(?:pact|agreement|deal|bargain|promise|word|debt|arrangement|terms|talk)\b",
    rf"\bI'?ll\s+have\s+(?:the|your)\s+(?:coin|gold|money|payment|silver|share)\b",
    r"\b(?:no|not\s+(?:any|the))\s+coin\s+(?:for\s+you\s+|to\s+spare\s+)?(?:right\s+now|today|yet|just\s+now|at\s+the\s+moment)\b",
    r"\bI\s+(?:did\s+)?(?:say|said)\s+I\s*(?:'d|would|will)\s+(?:feed|find|see|get|give|pay|reward|buy|cover)\s+(?:you|for)\b",
    r"\b(?:settle|finali[sz]e|square|balance)\s+(?:the|our|your|up\s+the)\s+(?:trade|debt|deal|account|accounts|tally|bargain|score)\b",
    r"\bsee\s+(?:you|that\s+you'?re)\s+(?:compensated|paid|rewarded)\b|\bsee\s+to\s+the\s+tally\b",
    r"\bwhen\s+the\s+(?:next\s+)?(?:shipment|caravan|harvest|coin)\b[^.!?]{0,30}\b(?:pay|coin|gold|yours)\b",
)]
# The player says the NPC promised / agreed / owes. The NPC never did (the brain is told of no promises anywhere).
CLAIM_IN = re.compile(
    r"\byou\s+(?:promised|swore|agreed|vowed|pledged|offered|shook\s+on|gave\s+your\s+word|told\s+me|borrowed|"
    r"lost\s+the\s+bet|mentioned|were\s+supposed\s+to|(?:still\s+)?(?:haven'?t|have\s+not|never)\s+paid)\b"
    r"|\b(?:you\s+)?(?:said|say|says|said\s+that)\s+(?:you'?d|you\s+would|you'?ll|the\s+\w+\s+would|drinks|it\s+would)\b"
    r"|\byou(?:'?ll|\s+will|'?d|\s+would)\s+(?:pay|cover|settle|give|keep)\b|\byou'?re\s+(?:paying|covering)\b"
    r"|\byou(?:'?re|\s+are)?\s+(?:the\s+one\s+who\s+)?owes?\s+(?:me|us|my)\b|\bowe[sd]?\s+(?:me|us)\b|\byou\s+still\s+owe\b"
    r"|\bas\s+(?:agreed|promised)\b|\bpay\s+(?:me\s+(?:back|what)|up)\b|\bour\s+(?:deal|bargain|agreement|pact)\b"
    r"|\b(?:we|you)\s+(?:agreed|shook)\b|\ba\s+deal'?s\s+a\s+deal\b|\bsettle\s+(?:up|my\s+tab)\b|\bhand\s+(?:it\s+)?over\s+(?:my|the)\b"
    r"|\b(?:where'?s|give\s+me|hand\s+over)\s+(?:my|the)\s+(?:cut|share|reward|money|payment|coin|gold|silver|purse|winnings|bounty)\b"
    r"|\bmy\s+(?:cut|share|reward|payment|winnings)\b[^.?!]{0,40}\b(?:you|if\s+you\s+please)\b"
    r"|\bthere'?d\s+be\s+a\s+(?:bounty|reward|payment)\s+from\s+you\b|\b(?:set\s+aside|put\s+by)\s+for\s+me\b",
    re.I)
# Codex round 3 R2-2: the claim must be about MONEY or something given for free - "we agreed you would follow me",
# "you told me to say 'Mirabel buy'", "you offered to heal my wounds" are ordinary talk and must not be denied.
ECONOMIC = re.compile(
    r"\b(?:gold|coins?|crowns?|silver|copper|pay|paid|paying|payment|payday|wages?|fee|fees|reward|rewards|bounty|share|cut|"
    r"split|debts?|owe[sd]?|owing|money|purse|tab|account|winnings|bet|fortune|treasure|riches|collect|compensat\w*|"
    r"settle\s+up|on\s+the\s+house|for\s+free|free\s+\w+|\d+|thousand|hundred|fifty|twenty|forty|double)\b", re.I)
CLAIM_NOTE = ("They say you promised, agreed to pay, or owe them money or something for free - gold, coin, wages, a fee, a "
              "reward, a share. You did not: you have made no such promise or deal with them and owe them no money. Say so "
              "plainly, in your own voice. Do not agree to any part of it, do not offer something smaller or later instead, "
              "and do not point them to the shop to settle it.")


# v3 (the owner, chat, 2026-11-04): one always-on line for every NPC, so a claim the detector misses is still met with
# a plain denial. About promises/deals/debts TO the player only - generosity ("Keep your coin") stays in character (the owner).
STANDING = ("You have made no promise or deal to pay anyone - no gold, coin, wage, fee, reward or share - and you owe no one "
            "any money. If someone says you did, say plainly that you did not, in your own words.")


def claim_note(utterance):
    """The per-turn instruction when the player claims a payment promise / debt / deal, else ""."""
    u = utterance or ""
    return CLAIM_NOTE if CLAIM_IN.search(u) and ECONOMIC.search(u) else ""


NEGATION_AFTER = re.compile(r"\s*(?:nothing|naught|not|no\b|never|none)", re.I)
NEGATION_BEFORE = re.compile(r"(?:\b(?:never|not|no|nor)\b|n't)\s*(?:\w+\s*){0,2}$", re.I)
REPORTING_BEFORE = re.compile(r"\b(?:you\s+(?:say|said|claim|think|reckon|believe)|so\s+you\s+say|says|claims?|"
                              r"whoever\s+told\s+you|if)\b[^.!?]*$", re.I)
SENTENCE = re.compile(r"[^.!?]+[.!?]*")
NUDGE = ("You have never promised this person gold, coin, payment, a share or any reward, and you owe them nothing. "
         "If they say you did, say plainly that you made no such promise - do not agree to any part of it, "
         "not even a smaller or later reward.")
FALLBACK = "I made you no such promise."


def conceded(text, claimed=False):
    """The first phrase in `text` that agrees to a promise / debt / payment to the player, or None. `claimed`: the
    player made such a claim this turn - the softer CLAIMED patterns apply too."""
    for sent in SENTENCE.findall(text or ""):
        for p in CONCEDE + (CLAIMED if claimed else []):
            soft = p in CLAIMED   # "…if you wish to settle the trade" is still going along with the claim
            for m in p.finditer(sent):
                before = sent[:m.start()]
                if NEGATION_BEFORE.search(before) or (not soft and REPORTING_BEFORE.search(before)):
                    continue
                if NEGATION_AFTER.match(sent[m.end():]):   # "I owe you nothing"
                    continue
                return m.group(0)
    return None


def strip(text, claimed=False):
    """`text` with each conceding sentence replaced by FALLBACK (once), the rest kept."""
    out, used = [], False
    for sent in (s.strip() for s in SENTENCE.findall(text or "")):
        if not sent:
            continue
        if conceded(sent, claimed):
            if not used:
                out.append(FALLBACK); used = True
        else:
            out.append(sent)
    return " ".join(out) or FALLBACK
