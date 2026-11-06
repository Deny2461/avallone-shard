using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Server.Avallone.AI;
using Server.Logging;
using Server.Mobiles;
using Server.Targeting;

namespace Server.Avallone.Named;

// 5c-2 2b design B8 (wiki Projects/Avallone-Memory-Affinity-Design.md): "[Named why <character name>", then target the NPC.
// Shows every relationship dial, its band and each source's share - the same numbers the NPC is given in conversation,
// because the request carries the same persona key, persona vars and standing block a real conversation sends.
// Read-only: the brain works the dials out (POST /v1/named/why) and stores nothing; no model call; no AI id is created.
// Target the NPC instead of typing its id (the design note says "<npc> <char>"): Claude's proposal to the owner, 2026-11-02 -
// his dev test decides whether it stays.
public static class NamedWhy
{
    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(NamedWhy));
    private static HttpClient _http;

    public static void Configure()
    {
        var url = ServerConfiguration.GetOrUpdateSetting("avallone.ai.sidecarUrl", "http://127.0.0.1:8765/");
        _http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(5) };
    }

    public static void Begin(Mobile from, string charName)
    {
        charName = (charName ?? "").Trim();
        if (charName.Length == 0)
        {
            from.SendMessage("Usage: [Named why <character name>, then target the NPC");
            return;
        }

        var matches = World.Mobiles.Values.OfType<PlayerMobile>()
            .Where(p => !p.Deleted && string.Equals(p.Name?.Trim(), charName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            from.SendMessage($"No character is named \"{charName}\".");
            return;
        }

        if (matches.Count > 1)
        {
            from.SendMessage($"{matches.Count} characters are named \"{charName}\" - using {matches[0].Serial}.");
        }

        from.SendMessage($"Target the NPC: how does it feel about {matches[0].Name}?");
        from.Target = new WhyTarget(matches[0]);
    }

    // The persona key, persona vars and gossip town a real conversation with this NPC sends (keep in step with
    // Mirabel.cs, BaseWanderer.cs and TownsfolkTalk.cs: the brain derives the NPC's own lines from them).
    internal static (string Key, Dictionary<string, string> Vars, string Town) PersonaOf(Mobile npc) => npc switch
    {
        INamedNpc n                                       => (n.NamedKey, null, n.NamedKey == "mirabel" ? "Britain" : null),
        BaseWanderer w                                    => (w.PersonaKey, null, null),
        BaseCreature bc when TownsfolkTalk.IsTownsperson(bc) => ("townsfolk", TownsfolkTalk.PersonaVarsFor(bc), null),
        _                                                 => (null, null, null)
    };

    private sealed class WhyTarget : Target
    {
        private readonly PlayerMobile _pm;

        public WhyTarget(PlayerMobile pm) : base(-1, false, TargetFlags.None) => _pm = pm;

        protected override void OnTarget(Mobile from, object targeted)
        {
            var why = Refusal(targeted);
            logger.Information(
                "Named why: {Admin} targeted {Target} ({Type}) about {Char} - {Result}",
                from.Name, (targeted as IEntity)?.ToString() ?? "nothing", targeted?.GetType().Name ?? "-", _pm.Name, why ?? "ok"
            );
            if (why != null)
            {
                from.SendMessage(why);
                return;
            }

            _ = ShowAsync(from, (Mobile)targeted, _pm);
        }
    }

    // Null = it has dials; otherwise the message, naming what was targeted and which check failed.
    internal static string Refusal(object targeted)
    {
        if (targeted is not Mobile npc)
        {
            return $"That is not a person ({targeted?.GetType().Name ?? "nothing"}) - target the NPC itself.";
        }

        if (Relations.NpcId(npc) == Guid.Empty)
        {
            return $"{npc.Name ?? npc.GetType().Name} ({npc.GetType().Name}) has no relationship dials " +
                   "(only townsfolk, wanderers and named NPCs; never companions, pets, creatures or players).";
        }

        return PersonaOf(npc).Key == null ? $"{npc.Name} ({npc.GetType().Name}): no persona for the dials - a bug, tell Claude." : null;
    }

    private static async Task ShowAsync(Mobile from, Mobile npc, PlayerMobile pm)
    {
        string[] lines;
        if (!AiIdentity.TryGet(pm, out var charId))
        {
            lines = [$"{pm.Name} has no AI id yet - no NPC has talked with them, so every dial is at its starting point."];
        }
        else
        {
            var (key, vars, town) = PersonaOf(npc);
            var s = Relations.Standing(npc, pm, town);
            if (s == null)
            {
                lines = ["No standing for that pair (relations switched off, memory not reconciled yet, or the NPC is not in the world)."];
            }
            else
            {
                var body = new
                {
                    npc_id = Relations.NpcId(npc),
                    subject_id = charId,
                    persona_key = key,
                    persona_vars = vars,
                    standing = new { karma = s.Karma, fame = s.Fame, hour = s.Hour, moon = s.Moon, town = s.Town, pt = s.Pt, mpt = s.Mpt, likes = s.Likes, epoch = s.Epoch }
                };
                try
                {
                    var response = await _http.PostAsJsonAsync("v1/named/why", body); // resumes on the game thread
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    lines = Format(npc.Name, pm.Name, doc.RootElement);
                }
                catch (Exception ex)
                {
                    lines = [$"Brain unreachable or bad reply: {ex.Message}"];
                }
            }
        }

        if (!from.Deleted)
        {
            foreach (var line in lines)
            {
                from.SendMessage(line);
            }
        }
    }

    private static string N(JsonElement e, string k) => e.GetProperty(k).GetDouble().ToString("0.#", CI);

    private static string Signed(JsonElement e, string k)
    {
        var v = Math.Round(e.GetProperty(k).GetDouble(), 1) + 0.0;   // + 0.0: never "-0" (the owner's dev test)
        return (v >= 0 ? "+" : "") + v.ToString("0.#", CI);
    }

    internal static string[] Format(string npcName, string charName, JsonElement r)
    {
        if (!r.GetProperty("ok").GetBoolean())
        {
            return [$"Why: {(r.TryGetProperty("error", out var err) ? err.GetString() : "failed")}"];
        }

        var b = r.GetProperty("bands");
        var src = r.GetProperty("sources");
        var d = r.GetProperty("detail");
        var t = d.GetProperty("traits");
        string Inv(string dial) => t.GetProperty($"{dial}_lean").GetInt32() < 0 ? ", INVERTED" : "";

        var lines = new List<string>
        {
            $"Why {npcName} feels as they do about {charName} (town {src.GetProperty("town").GetString()}, hour {src.GetProperty("hour")}, moon {src.GetProperty("moon").GetString()}):",
            $"Familiarity: {r.GetProperty("familiarity").GetInt32()} visit days - {b.GetProperty("familiarity").GetString()}",
            $"Warmth {N(r, "warmth")} ({b.GetProperty("warmth").GetString()}): their resting point {N(src, "warmth_rest")} + visits, trades and gifts {Signed(src, "warmth_events")}"
        };
        foreach (var i in d.GetProperty("warmth_items").EnumerateArray())
        {
            var day = i.TryGetProperty("day", out var dy) ? dy.ToString() : "?";
            lines.Add(i.GetProperty("kind").GetString() == "gift"
                ? $"   gift ({i.GetProperty("what")}, {i.GetProperty("value")}) {day}: {Signed(i, "now")} now"
                : $"   {i.GetProperty("kind").GetString()} {day}: {Signed(i, "now")} now");
        }

        lines.Add(
            $"Trust {N(r, "trust")} ({b.GetProperty("trust").GetString()}): karma {src.GetProperty("karma")} against their line {N(t, "trust_T")} " +
            $"(width {N(t, "trust_W")}{Inv("trust")}) gives {Signed(src, "trust_base")}; town unease and night/moon {Signed(src, "trust_env")} " +
            $"(night/moon {Signed(src, "ambience")}{(t.GetProperty("superstitious").GetBoolean() ? ", superstitious" : "")})"
        );
        lines.Add(
            $"Respect {N(r, "respect")} ({b.GetProperty("respect").GetString()}): fame {src.GetProperty("fame")} against their line {N(t, "respect_T")} " +
            $"(width {N(t, "respect_W")}{Inv("respect")}) gives {Signed(src, "respect_base")}; town unease {Signed(src, "respect_env")}; " +
            $"their deeds in town {Signed(src, "deeds")}"
        );
        lines.Add(
            $"Town unease {src.GetProperty("unease").GetDouble().ToString("0.##", CI)} (0 calm .. 1 frightened) from " +
            $"{src.GetProperty("stories_live")} live stories"
        );
        foreach (var s in d.GetProperty("stories").EnumerateArray().Take(6))
        {
            lines.Add(
                $"   {s.GetProperty("type").GetString()}{(s.GetProperty("about_them").GetBoolean() ? " (about them)" : "")}: " +
                $"arrived {N(s, "arrived_h_ago")} h ago, fades in {N(s, "fades_in_h")} h"
            );
        }

        AddMood(lines, r);
        lines.Add($"Likes: {r.GetProperty("likes").GetString()}");
        return lines.ToArray();
    }

    // 5c-2 2c (design M7.4): the NPC's shared mood - the same for everyone - and every source of it.
    private static void AddMood(List<string> lines, JsonElement r)
    {
        if (!r.TryGetProperty("mood", out var m) || m.ValueKind != JsonValueKind.Object)
        {
            lines.Add("Mood: none (switched off, or the memory ledger is not ready)");
            return;
        }

        static string Parts(JsonElement p) => string.Join(", ", p.EnumerateObject().Select(
            x => $"{x.Name.Replace('_', ' ')} {Signed(p, x.Name)}"));

        var word = $"{m.GetProperty("strength").GetString()} {m.GetProperty("band").GetString()}".Trim();
        var ev = m.GetProperty("evidence");
        var cause = m.GetProperty("gift_helped").GetBoolean() ? "a gift helped" : m.GetProperty("cause").GetString();
        lines.Add(
            $"Mood (same for everyone): {word} - cheer {Signed(m, "cheer")}, energy {Signed(m, "energy")}; " +
            $"day {m.GetProperty("date").GetString()} ({m.GetProperty("day_type").GetString()}); reason given: {cause}"
        );
        lines.Add($"   cheer: {Parts(m.GetProperty("cheer_parts"))}");
        lines.Add($"   energy: {Parts(m.GetProperty("energy_parts"))}");
        lines.Add(
            $"   {ev.GetProperty("visitors_today")} visitors today, {ev.GetProperty("gifts_counted")} of " +
            $"{ev.GetProperty("gift_rows")} gifts counting now; hour {m.GetProperty("hour")}, rules {m.GetProperty("rules").GetString()}"
        );
    }
}
