using System;
using System.Linq;
using System.Threading.Tasks;
using Server.Commands;

namespace Server.Avallone.AI;

public static class AiCommands
{
    // A fixed ID for [ai test, so test calls are recognisable in the logs.
    private static readonly Guid TestNpcId = new("a5a11000-0000-4000-8000-000000000001");

    private const string TestPersona =
        "You are a friendly innkeeper in Britain, in the land of Britannia. Answer in one or two short sentences, in character.";

    public static void Configure()
    {
        CommandSystem.Register("AI", AccessLevel.Administrator, AI_OnCommand);
    }

    [Usage("AI <status|on|off|pause|resume|reset|warm|test <words>|persona <key>|lore <key> <words>|reload|memory|secrets|companions|personality [undo <companion> <id> <character>]>")]
    [Description(
        "Avallone AI gateway. on/off = kill switch, saved. pause/resume = until restart. reset = close the circuit breaker. warm = load the model now. test = one round trip to the model. persona = what the brain knows about a persona file. lore = which lore sections that NPC would draw on for those words (no model call). reload = re-read the lore now. memory = the memory ledger: state, outbox, what the brain holds. secrets = companion secret progress per character (tiers and counters, never text). personality = companion traits and turning points per character; undo revokes one."
    )]
    public static void AI_OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;
        var verb = e.Length > 0 ? e.GetString(0).ToLowerInvariant() : "status";

        switch (verb)
        {
            case "on":
                AiGateway.SetEnabled(true, true);
                from.SendMessage("AI gateway ON (saved).");
                break;
            case "off":
                AiGateway.SetEnabled(false, true);
                from.SendMessage("AI gateway OFF (saved). NPCs fall back to plain UO behaviour.");
                break;
            case "pause":
                AiGateway.SetEnabled(false, false);
                from.SendMessage("AI gateway paused until restart.");
                break;
            case "resume":
                AiGateway.SetEnabled(true, false);
                from.SendMessage("AI gateway resumed until restart.");
                break;
            case "reset":
                AiGateway.ResetBreaker();
                from.SendMessage("Circuit breaker closed.");
                break;
            case "warm":
                _ = AiGateway.WarmAsync();
                from.SendMessage("Asked the sidecar to load the model.");
                break;
            case "test":
                var words = e.Length > 1 ? e.ArgString[e.GetString(0).Length..].Trim() : "Good evening! What's the news?";
                _ = TestAsync(from, words);
                break;
            case "persona":
                if (e.Length < 2)
                {
                    from.SendMessage("Usage: [ai persona <key>   e.g. [ai persona mirabel");
                    break;
                }

                _ = PersonaAsync(from, e.GetString(1).ToLowerInvariant());
                break;
            case "lore":
                if (e.Length < 3)
                {
                    from.SendMessage("Usage: [ai lore <key> <words>   e.g. [ai lore mirabel what news of Zento");
                    break;
                }

                _ = LoreAsync(from, e.GetString(1).ToLowerInvariant(), string.Join(" ", e.Arguments.Skip(2)));
                break;
            case "reload":
                _ = ReloadAsync(from);
                break;
            case "memory":
                _ = MemoryAsync(from);
                break;
            case "companions":
                foreach (var c in Server.Mobiles.BaseCompanion.All)
                {
                    from.SendMessage(AvalloneRules.Describe(c));
                }

                break;
            case "personality":
                foreach (var c in Server.Mobiles.BaseCompanion.All)
                {
                    foreach (var (m, _) in c.TrustEntries)
                    {
                        if (e.Length < 4)
                        {
                            from.SendMessage(AiPersonality.Status(c, m));
                        }
                        else if (e.GetString(1).ToLowerInvariant() == "undo" &&
                                 string.Equals(c.PersonaKey, e.GetString(2), StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(m.Name, string.Join(" ", e.Arguments.Skip(4)), StringComparison.OrdinalIgnoreCase))
                        {
                            from.SendMessage(AiPersonality.Undo(c, m, e.GetString(3)) ? "Undone." : "No such active turning point.");
                        }
                    }
                }

                break;
            case "secrets":
                foreach (var c in Server.Mobiles.BaseCompanion.All)
                {
                    foreach (var (m, _) in c.TrustEntries)
                    {
                        from.SendMessage(AiSecrets.Describe(c, m));
                    }
                }

                break;
            default:
                SendStatus(from);
                break;
        }
    }

    private static async Task TestAsync(Mobile from, string words)
    {
        from.SendMessage($"AI test: sending \"{words}\"...");

        var request = new AiRequest(
            TestNpcId,
            AiIdentity.GetOrCreate(from),
            TestPersona,
            words,
            AiPriority.Conversation
        );

        var reply = await AiGateway.RequestAsync(request);

        if (from.Deleted)
        {
            return;
        }

        from.SendMessage(
            reply.Ok
                ? $"AI test OK in {reply.ElapsedMs} ms ({reply.Model}): {reply.Text}"
                : $"AI test {reply.Outcome} after {reply.ElapsedMs} ms{(reply.Error == null ? "" : $": {reply.Error}")}"
        );
    }

    private static async Task PersonaAsync(Mobile from, string key)
    {
        var r = await AiBrainAdmin.QueryAsync(key, "", false);

        if (from.Deleted)
        {
            return;
        }

        if (!r.Ok)
        {
            from.SendMessage($"AI persona {key}: {r.Error}");
            return;
        }

        var p = r.Persona;
        from.SendMessage($"Persona {p.Key}: {p.Name}, town {(string.IsNullOrEmpty(p.Town) ? "-" : p.Town)}");
        from.SendMessage($"Groups: {(p.Groups.Length == 0 ? "none (no lore)" : string.Join(", ", p.Groups))}");
        from.SendMessage($"Excluded lore: {(p.Exclude.Length == 0 ? "none" : string.Join("; ", p.Exclude))}");
        SendLoreStats(from, r.Lore);
    }

    private static async Task LoreAsync(Mobile from, string key, string words)
    {
        var r = await AiBrainAdmin.QueryAsync(key, words, false);

        if (from.Deleted)
        {
            return;
        }

        if (!r.Ok)
        {
            from.SendMessage($"AI lore {key}: {r.Error}");
            return;
        }

        if (r.Sections.Length == 0)
        {
            from.SendMessage($"{key} would draw on no lore for \"{words}\".");
            return;
        }

        from.SendMessage($"{key} would draw on {r.Sections.Length} section(s) for \"{words}\":");
        foreach (var s in r.Sections)
        {
            var text = s.Text.Length > 90 ? s.Text[..90] + "..." : s.Text;
            from.SendMessage($"[{s.Score:0.#}] {s.Page} / {s.Heading}{(s.Rumour ? " (rumour)" : "")}: {text}");
        }
    }

    private static async Task ReloadAsync(Mobile from)
    {
        var r = await AiBrainAdmin.QueryAsync(null, "", true);

        if (from.Deleted)
        {
            return;
        }

        if (!r.Ok)
        {
            from.SendMessage($"AI reload: {r.Error}");
            return;
        }

        from.SendMessage(r.Reloaded ? "Lore re-read." : "Lore re-read (no change, or the read failed - see below).");
        SendLoreStats(from, r.Lore);
    }

    private static void SendLoreStats(Mobile from, LoreStats lore)
    {
        if (lore == null)
        {
            return;
        }

        from.SendMessage(
            $"Lore version {lore.Version ?? "-"}: {lore.Pages} pages, {lore.Sections} sections{(lore.Error == null ? "" : $"; last read FAILED: {lore.Error}")}"
        );
    }

    private static async Task MemoryAsync(Mobile from)
    {
        from.SendMessage(AiLedger.Describe());
        var brain = await AiLedger.BrainStatusAsync(null, null);
        if (!from.Deleted)
        {
            from.SendMessage(brain);
        }
    }

    private static void SendStatus(Mobile from)
    {
        var m = AiGateway.Metrics;

        from.SendMessage($"AI gateway: {(AiGateway.Enabled ? "ON" : "OFF")}, client {AiGateway.Client?.Name ?? "none"}");
        from.SendMessage(
            $"Quiet window {AiGateway.QuietDescription}{(AiGateway.InQuietWindow ? " (NOW QUIET)" : "")}; players online {AiGateway.PlayersOnline()}; warm-ups {m.Warmups} ok / {m.WarmupFailures} failed"
        );
        from.SendMessage(
            $"Queue {AiGateway.QueueCount} (high {m.QueueHighWater}), in flight {AiGateway.InFlight}, breaker {(AiGateway.BreakerIsOpen ? $"OPEN {AiGateway.BreakerRemainingMs / 1000}s" : "closed")}"
        );
        from.SendMessage(
            $"Since {m.Since:HH:mm} UTC: {m.Total} requests - ok {m[AiOutcome.Ok]}, timeout {m[AiOutcome.Timeout]}, failed {m[AiOutcome.Failed]}, off {m[AiOutcome.Disabled]}, quiet {m[AiOutcome.Quiet]}, breaker {m[AiOutcome.BreakerOpen]}, busy {m[AiOutcome.Busy]}, full {m[AiOutcome.QueueFull]}, expired {m[AiOutcome.Expired]}, evicted {m[AiOutcome.Evicted]}"
        );
        from.SendMessage(
            $"Latency p50 {Fmt(m.LatencyPercentile(50))}, p95 {Fmt(m.LatencyPercentile(95))}; game-thread hitch last {m.LastHitch.TotalMilliseconds:0.000} / p95 {m.HitchPercentile(95):0.000} / max {m.MaxHitch.TotalMilliseconds:0.000} ms; AI IDs {AiIdentity.Count}"
        );

        if (m.LastError != null)
        {
            from.SendMessage($"Last error {m.LastErrorAt:HH:mm:ss} UTC: {m.LastError}");
        }
    }

    private static string Fmt(long ms) => ms < 0 ? "-" : $"{ms} ms";
}
