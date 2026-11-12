using System;
using Server.Commands;
using Server.Mobiles;

namespace Server.Avallone;

// Speech colours (the owner, 2026-11-11, handoff Admin/Handoff-2026-11-11-Avallone-NPC-Speech-Colours.md): stock ModernUO gives
// every shopkeeper a RANDOM speech colour when it spawns, some of them dark and hard to read. With this switch on, ONE
// rule picks the colour of everything a non-player says through its speech colour (Say / SayTo - every AI reply, vendor
// line, guard and monster line): companions and wanderers (Elspeth, Bram) one colour, every other NPC or monster another.
// A player's own colour is the player's client setting and is never touched. The colour stored on each mobile is left
// alone and still saved, so switching this off gives back exactly what was there before.
// NOT covered: lines the stock code sends in a fixed colour of its own (mostly light grey), emotes, yells and whispers.
public static class SpeechColours
{
    public const int DefaultNpcHue = 68;        // bright green in the owner's client (his party/guild colour)
    public const int DefaultCompanionHue = 48;  // yellow in the owner's client (his label colour)

    public static bool Enabled { get; private set; }
    public static int NpcHue { get; private set; } = DefaultNpcHue;
    public static int CompanionHue { get; private set; } = DefaultCompanionHue;

    public static void Configure()
    {
        NpcHue = Clean(ServerConfiguration.GetOrUpdateSetting("avallone.speechColours.npcHue", DefaultNpcHue), DefaultNpcHue);
        CompanionHue = Clean(
            ServerConfiguration.GetOrUpdateSetting("avallone.speechColours.companionHue", DefaultCompanionHue),
            DefaultCompanionHue
        );
        Set(ServerConfiguration.GetOrUpdateSetting("avallone.speechColours", false));
        CommandSystem.Register("SpeechColours", AccessLevel.Administrator, SpeechColours_OnCommand);
    }

    // The one rule. `stored` is the colour kept on the mobile itself (unused while the rule is on).
    public static int For(Mobile m, int stored) => IsCompanion(m) ? CompanionHue : NpcHue;

    public static bool IsCompanion(Mobile m) => m is BaseCompanion or BaseWanderer;

    private static int Clean(int hue, int fallback) => hue is >= 1 and <= 2999 ? hue : fallback;

    private static void Set(bool on)
    {
        Enabled = on;
        Mobile.NpcSpeechHueRule = on ? For : null;
    }

    [Usage("SpeechColours [on|off | npc <hue> | companion <hue>]")]
    [Description("Shows or sets the one speech colour rule: companions one colour, every other NPC and monster another.")]
    private static void SpeechColours_OnCommand(CommandEventArgs e)
    {
        var verb = e.Length > 0 ? e.GetString(0).ToLowerInvariant() : "";
        if (verb is "on" or "off")
        {
            Set(verb == "on");
            ServerConfiguration.SetSetting("avallone.speechColours", Enabled ? "true" : "false");
        }
        else if (verb is "npc" or "companion" && e.Length > 1)
        {
            var hue = e.GetInt32(1);
            if (Clean(hue, -1) != hue)
            {
                e.Mobile.SendMessage("A hue is a number from 1 to 2999.");
            }
            else if (verb == "npc")
            {
                NpcHue = hue;
                ServerConfiguration.SetSetting("avallone.speechColours.npcHue", hue.ToString());
            }
            else
            {
                CompanionHue = hue;
                ServerConfiguration.SetSetting("avallone.speechColours.companionHue", hue.ToString());
            }
        }

        e.Mobile.SendMessage(
            $"Speech colours are {(Enabled ? "ON" : "OFF")} (saved): companions hue {CompanionHue}, every other NPC and monster hue {NpcHue}."
        );
        if (Enabled)
        {
            e.Mobile.SendMessage(NpcHue, "An NPC or monster speaks in this colour.");
            e.Mobile.SendMessage(CompanionHue, "A companion speaks in this colour.");
        }
    }

    internal static void SetForTests(bool on, int npcHue = DefaultNpcHue, int companionHue = DefaultCompanionHue)
    {
        NpcHue = npcHue;
        CompanionHue = companionHue;
        Set(on);
    }
}
