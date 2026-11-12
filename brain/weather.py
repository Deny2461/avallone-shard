"""One weather for the day, the same for every NPC (the owner's step 2 play test, 2026-11-10
- one said the morning sun was warm, the next that the air was heavy, a third warned of mud).

Pure derivation, nothing stored, no model call: a stable hash of the Central date picks one description, so a restart or
a second brain gives the same answer. It is a fact to stay consistent with, NOT a subject to raise - the wording says so,
because a subject that is named is a subject that gets talked about (step 2 trial 1).
Known limit: the game client can show its own passing rain or snow (Misc/Weather.cs); this line does not follow it.

Rumour step C3 (2026-11-11; the owner's carry-in) lifts that limit when the
"real_weather" setting is on. The shard sends one word for the spot where the NPC stands - "dry", "rain" or "snow"
(Weather.FallingAt: the same rules the game uses when it sends weather to a screen) - or nothing when it cannot tell.
  rain / snow -> that replaces the day's description, for as long as it falls.
  dry         -> the day's description, but never one of the wet kinds (rain, fog): the screen shows none.
  nothing     -> unknown: the day's description exactly as before (an old shard, a spot the game cannot place).
Setting off = this file behaves exactly as it did before the step, whatever the shard sends."""
import hashlib

# (weight, description) - worded so it is true by day and by night
# Play test 2: on a "dry and mild, with a few clouds" day one NPC spoke of "this heat" - so each one now also says how
# warm it is. Order and weights are unchanged, so the day's pick is the same as before.
KINDS = ((4, "dry, with a clear sky, neither hot nor cold"), (4, "dry and mild - neither hot nor cold - with a few clouds"),
         (3, "grey and overcast, but dry, and cool"), (2, "windy and dry, and cool"), (2, "cold and still, and dry"),
         (2, "a light, steady rain, and cool"), (1, "heavy rain that comes and goes, and cool"),
         (1, "a fog that has not lifted, cool and damp"))


DRY_KINDS = KINDS[:5]                                    # the kinds with nothing falling and no fog
FALLING = {"rain": "rain is falling, and it is cool", "snow": "snow is falling, and it is cold"}
WORDS = ("dry", "rain", "snow")


def word(raw):
    """The shard's word for what is falling where the NPC stands, or None = unknown (missing, or anything else)."""
    return raw if isinstance(raw, str) and raw in WORDS else None


def today(day, kinds=KINDS):
    """The description for a Central calendar day ("YYYY-MM-DD")."""
    n = int.from_bytes(hashlib.sha256(f"avallone-weather|{day}".encode()).digest()[:8], "big") % sum(w for w, _ in kinds)
    for w, text in kinds:
        if n < w:
            return text
        n -= w
    return kinds[0][1]


def describe(day, falling=None, real=False):
    """(description, is it what falls right now?) for this day and this spot. `falling` must already be word()-cleaned."""
    if real and falling in FALLING:
        return FALLING[falling], True
    text = today(str(day))
    if real and falling == "dry" and text not in [t for _, t in DRY_KINDS]:
        text = today(str(day), DRY_KINDS)                # a wet day in the list, a dry screen: the screen wins
    return text, False


def line(day, enabled=True, falling=None, real=False):
    """One sentence for a prompt, or "" when switched off or on any trouble. NEVER raises. `falling` = the shard's word
    (any value; cleaned here), `real` = the real_weather setting. real False = exactly the sentence before the step."""
    try:
        if not enabled or not day:
            return ""
        text, now = describe(day, word(falling), bool(real))
        return (f"The weather {'right now' if now else 'today'}, the same for everyone here: {text}. Do not bring the weather, the sky, the sun or the heat or cold up "
                "yourself; if it does come up, it is exactly this.")
    except Exception:
        return ""


def falling_line(enabled, falling, real):
    """For a companion's own reply, whose per-turn block is short: a sentence ONLY while rain or snow is falling where
    she stands (and both settings are on); "" otherwise. NEVER raises."""
    try:
        w = word(falling)
        if not enabled or not real or w not in FALLING:
            return ""
        return f"Right now, where you stand, {FALLING[w]}. Do not bring it up yourself; if the weather comes up, it is exactly this."
    except Exception:
        return ""
