using Server.Commands;
using Server.Targeting;

namespace Server.Avallone.Townsfolk;

// Townsfolk step, Stage 1 - the operator's commands for the identity lock (design §3.3, §3.4a, §3.6, §14.3).
//   [Seats                 what state the seat system is in
//   [Seats verify <file>   compare with a copy of the register taken BEFORE a restore; reports, changes nothing
//   [Seats clear           (target an NPC) clear its seat's conflict, if it is that seat's one bound NPC
//   [Seats seat            (target an NPC) give an unseated resident a NEW seat of its own, as it is
//   [Seats setname <name>  (target an NPC) rename it through the register
//   [Seats rematch         after restoring a world save older than the register: re-match residents to their seats
public static class SeatCommands
{
    public static void Configure()
    {
        CommandSystem.Register("Seats", AccessLevel.Administrator, OnCommand);
    }

    private static void OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;
        var verb = e.Length > 0 ? e.GetString(0).ToLowerInvariant() : "status";

        switch (verb)
        {
            case "status":
                {
                    from.SendMessage(Seats.Status());
                    break;
                }
            case "verify":
                {
                    if (e.Length < 2)
                    {
                        from.SendMessage("Usage: [Seats verify <path to a copy of seat-register.json taken before the restore>");
                        break;
                    }

                    var lines = Seats.Verify(e.ArgString[e.GetString(0).Length..].Trim());

                    for (var i = 0; i < lines.Count && i < 30; i++)
                    {
                        from.SendMessage(lines[i]);
                    }

                    if (lines.Count > 30)
                    {
                        from.SendMessage($"... and {lines.Count - 30} more lines.");
                    }

                    break;
                }
            case "clear":
                {
                    from.SendMessage("Target the NPC whose seat conflict is to be cleared.");
                    from.BeginTarget(-1, false, TargetFlags.None, (m, t) =>
                    {
                        Seats.ClearConflict(t as Mobile, out var report);
                        m.SendMessage(report);
                    });
                    break;
                }
            case "seat":
                {
                    from.SendMessage("Target the unseated townsperson to give a new seat, as it is.");
                    from.BeginTarget(-1, false, TargetFlags.None, (m, t) =>
                    {
                        Seats.SeatUnbound(t as Mobile, out var report);
                        m.SendMessage(report);
                    });
                    break;
                }
            case "setname":
                {
                    if (e.Length < 2)
                    {
                        from.SendMessage("Usage: [Seats setname <new name>   then target the NPC");
                        break;
                    }

                    var name = e.ArgString[e.GetString(0).Length..].Trim();
                    from.SendMessage($"Target the NPC to rename to \"{name}\".");
                    from.BeginTarget(-1, false, TargetFlags.None, (m, t) =>
                    {
                        Seats.SetName(t as Mobile, name, out var report);
                        m.SendMessage(report);
                    });
                    break;
                }
            case "rematch":
                {
                    Seats.Rematch(out var report);
                    from.SendMessage(report);
                    break;
                }
            default:
                {
                    from.SendMessage("Usage: [Seats | [Seats verify <file> | [Seats clear | [Seats seat | [Seats setname <name> | [Seats rematch");
                    break;
                }
        }
    }
}
