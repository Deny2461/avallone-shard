using System;

namespace Server.Avallone.Companions;

// A companion's trust in one character, 0-100. Server-owned and event-driven (AI plan v2, item 8): the model
// reads a description of it and never changes it. Every change has a reason, a size and (where farmable) a
// cooldown or a daily cap - all of which live here so they can be tested and tuned in one place.
public static class CompanionTrust
{
    public const int Min = 0;
    public const int Max = 100;

    // Wary at first (the owner, chat, 2026-10-01): she starts out wary of strangers.
    public const int Start = 20;

    // She walks away below LeaveBelow and will travel again from RejoinAt; the gap stops her flip-flopping.
    public const int LeaveBelow = 5;
    public const int RejoinAt = 10;

    public const int TimeTogether = 1;
    public const int TimeTogetherDailyCap = 6;
    public static readonly TimeSpan TimeTogetherStep = TimeSpan.FromMinutes(20);

    public const int FightSurvived = 1;
    public static readonly TimeSpan FightCooldown = TimeSpan.FromMinutes(10);

    // "Can be talked round": talking to her while she refuses to travel slowly mends it.
    public const int TalkedRound = 2;
    public static readonly TimeSpan TalkedRoundCooldown = TimeSpan.FromMinutes(10);

    public const int CriminalAct = -5;
    public const int Murder = -15;

    public static int Apply(int trust, int delta) => Math.Clamp(trust + delta, Min, Max);

    public static bool WillJoin(int trust) => trust >= RejoinAt;

    public static bool WillLeave(int trust) => trust < LeaveBelow;

    public static string Band(int trust) =>
        trust switch
        {
            < 25 => "wary",
            < 50 => "warming",
            < 75 => "trusting",
            _    => "devoted"
        };

    // What the model is told. Plain behaviour, no numbers - numbers invite the model to recite them.
    public static string Describe(int trust, string name) =>
        Band(trust) switch
        {
            "wary"     => $"You do not trust {name} yet. You watch what they do, not what they say.",
            "warming"  => $"You are starting to like {name}, though you would not say so outright.",
            "trusting" => $"You trust {name} and speak freely with them, though you still scold them when they are reckless.",
            _          => $"You would follow {name} anywhere, and they know it. You still scold them."
        };
}
