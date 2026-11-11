"""One weather for the day, the same for every NPC (the owner's step 2 play test, 2026-11-10
- one said the morning sun was warm, the next that the air was heavy, a third warned of mud).

Pure derivation, nothing stored, no model call: a stable hash of the Central date picks one description, so a restart or
a second brain gives the same answer. It is a fact to stay consistent with, NOT a subject to raise - the wording says so,
because a subject that is named is a subject that gets talked about (step 2 trial 1).
Known limit: the game client can show its own passing rain or snow (Misc/Weather.cs); this line does not follow it."""
import hashlib

# (weight, description) - worded so it is true by day and by night
# Play test 2: on a "dry and mild, with a few clouds" day one NPC spoke of "this heat" - so each one now also says how
# warm it is. Order and weights are unchanged, so the day's pick is the same as before.
KINDS = ((4, "dry, with a clear sky, neither hot nor cold"), (4, "dry and mild - neither hot nor cold - with a few clouds"),
         (3, "grey and overcast, but dry, and cool"), (2, "windy and dry, and cool"), (2, "cold and still, and dry"),
         (2, "a light, steady rain, and cool"), (1, "heavy rain that comes and goes, and cool"),
         (1, "a fog that has not lifted, cool and damp"))


def today(day):
    """The description for a Central calendar day ("YYYY-MM-DD")."""
    n = int.from_bytes(hashlib.sha256(f"avallone-weather|{day}".encode()).digest()[:8], "big") % sum(w for w, _ in KINDS)
    for w, text in KINDS:
        if n < w:
            return text
        n -= w
    return KINDS[0][1]


def line(day, enabled=True):
    """One sentence for a prompt, or "" when switched off or on any trouble. NEVER raises."""
    try:
        if not enabled or not day:
            return ""
        return (f"The weather today, the same for everyone here: {today(str(day))}. Do not bring the weather, the sky, the sun or the heat or cold up "
                "yourself; if it does come up, it is exactly this.")
    except Exception:
        return ""
