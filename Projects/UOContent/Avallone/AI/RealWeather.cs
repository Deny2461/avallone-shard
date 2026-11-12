using Server.Misc;

namespace Server.Avallone.AI;

// Rumour step C3 (2026-11-11; the owner's carry-in): one word for what the game shows falling where a character stands, for
// the brain's weather line. Read-only (Weather.FallingAt). null = unknown, which the brain treats as "say nothing new".
public static class RealWeather
{
    public static string Word(Mobile m) => m == null || m.Deleted ? null : Word(Weather.FallingAt(m.Map, m.Location));

    public static string Word(Weather.Falling falling) => falling switch
    {
        Weather.Falling.Dry  => "dry",
        Weather.Falling.Rain => "rain",
        Weather.Falling.Snow => "snow",
        _                    => null
    };
}
