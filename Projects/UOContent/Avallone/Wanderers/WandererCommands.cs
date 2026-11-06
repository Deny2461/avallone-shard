using System;
using Server.Commands;
using Server.Logging;
using Server.Mobiles;

namespace Server.Avallone.AI;

// [Wanderer - status and control for wanderers (wanderer prototype, 2026-10-14).
public static class WandererCommands
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(WandererCommands));

    public static void Configure()
    {
        CommandSystem.Register("Wanderer", AccessLevel.Administrator, Wanderer_OnCommand);
    }

    // Dev rig only: AVALLONE_WANDERER_SELFTEST=1 places Bram at the inn and turns wanderers on (no GM in game there).
    public static void Initialize()
    {
        if (Environment.GetEnvironmentVariable("AVALLONE_WANDERER_SELFTEST") != "1")
        {
            return;
        }

        Timer.StartTimer(
            TimeSpan.FromSeconds(10),
            () =>
            {
                BaseWanderer.SetEnabled(true, false);
                foreach (var w in BaseWanderer.All)
                {
                    logger.Information("Selftest: loaded {State}; {Lines} journal line(s)", w.Describe(), w.FullJournal().Length);
                }

                var send = WandererPlaces.FindHunt(Environment.GetEnvironmentVariable("AVALLONE_WANDERER_SEND") ?? "");
                foreach (var w in BaseWanderer.All)
                {
                    if (w is BramHolloway)
                    {
                        if (send != null)
                        {
                            w.SendTo(send);
                        }
                        else
                        {
                            w.Hurry();
                        }

                        return;
                    }
                }

                var bram = new BramHolloway();
                bram.MoveToWorld(WandererPlaces.Inn.Location, WandererPlaces.Map);
                bram.Hurry();
            }
        );
    }

    [Usage("Wanderer <status|on|off|spawn|hurry|send <hunt>|journal|home>")]
    [Description(
        "Avallone wanderers. status = where each is and what he is doing. on/off = wanderers' own switch, saved " +
        "(off: they walk to the inn and wait; the AI gateway kill switch also stops their model calls). spawn = place " +
        "Bram Holloway at the Britain inn if he does not exist. hurry = end the current rest or hunt now. send <hunt> = walk to that hunting ground now. " +
        "journal = the last 12 journal lines. home = move every wanderer to the inn."
    )]
    public static void Wanderer_OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;
        var verb = e.Length > 0 ? e.GetString(0).ToLowerInvariant() : "status";

        switch (verb)
        {
            case "on":
                BaseWanderer.SetEnabled(true, true);
                from.SendMessage("Wanderers ON (saved).");
                break;
            case "off":
                BaseWanderer.SetEnabled(false, true);
                from.SendMessage("Wanderers OFF (saved). They walk to the inn and wait.");
                break;
            case "spawn":
                foreach (var w in BaseWanderer.All)
                {
                    if (w is BramHolloway && !w.Deleted)
                    {
                        from.SendMessage($"Bram already exists: {w.Describe()}");
                        return;
                    }
                }

                var bram = new BramHolloway();
                bram.MoveToWorld(WandererPlaces.Inn.Location, WandererPlaces.Map);
                from.SendMessage($"Placed {bram.Name} at the Britain inn ({WandererPlaces.Inn.Location}, Trammel).");
                break;
            case "hurry":
                foreach (var w in BaseWanderer.All)
                {
                    w.Hurry();
                }

                from.SendMessage("Wanderers will move on at their next tick.");
                break;
            case "home":
                foreach (var w in BaseWanderer.All)
                {
                    w.MoveToWorld(WandererPlaces.Inn.Location, WandererPlaces.Map);
                }

                from.SendMessage("Moved every wanderer to the Britain inn.");
                break;
            case "send":
                var hunt = e.Length > 1 ? WandererPlaces.FindHunt(e.GetString(1)) : null;
                if (hunt == null)
                {
                    from.SendMessage($"Usage: [Wanderer send <{string.Join("|", WandererPlaces.HuntKeys)}>");
                    return;
                }

                foreach (var w in BaseWanderer.All)
                {
                    w.SendTo(hunt);
                }

                from.SendMessage($"Wanderers sent to {hunt.Key}.");
                break;
            case "journal":
                foreach (var w in BaseWanderer.All)
                {
                    from.SendMessage($"-- {w.Name}");
                    foreach (var line in w.FullJournal())
                    {
                        from.SendMessage(line);
                    }
                }

                break;
            default:
                from.SendMessage($"Wanderers {(BaseWanderer.Enabled ? "ON" : "OFF")}, AI gateway {(AiGateway.Enabled ? "ON" : "OFF")}, {BaseWanderer.All.Count} placed.");
                foreach (var w in BaseWanderer.All)
                {
                    from.SendMessage(w.Describe());
                }

                break;
        }
    }
}
