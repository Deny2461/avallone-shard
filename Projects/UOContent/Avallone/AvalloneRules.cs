using System;
using System.Text;
using Server.Logging;
using Server.Mobiles;

namespace Server.Avallone;

// House rules for this shard that are not part of the AI work.
public static class AvalloneRules
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(AvalloneRules));

    private const int StockFollowersMax = 5;

    public static int FollowersBonus { get; private set; }

    // the owner, 2026-10-05: /. Champion spawns and their power scrolls exist
    // on Trammel as well as Felucca. The Harrower stays Felucca-only for now, so a Trammel champion gives scrolls
    // but no skull, and opens no star-room gate. AvalloneChampions places the Trammel spawns.
    public static bool ChampionsOnTrammel { get; private set; }

    public static bool GivesChampionScrolls(Map map) =>
        map == Map.Felucca || ChampionsOnTrammel && map == Map.Trammel;

    // the owner, 2026-10-13: Stock allows one house for the whole
    // account. With this on, the limit is one house for each character, and every house on an active account
    // refreshes itself (stock: only the newest one does; the others must be visited or they collapse).
    public static bool HousePerCharacter { get; set; }

    // the owner, 2026-10-17: - meaning a production shard at SE: keep the
    // silver protection and lose half on a fail. Stock here took every material on a failed one-at-a-time craft,
    // and the colour-matching path also ignored the Tinkering silver rule. With this on, a failed craft costs half
    // of each material (rounded up) and silver is never lost. Batch-made items keep their own stock half rule.
    public static bool FailedCraftLosesHalf { get; set; }

    // the owner, 2026-10-17: /. Oak, ash, yew, heartwood, bloodwood and
    // frostwood come from Lumberjacking, and bows made from them get their wood bonuses, without switching the
    // shard to Mondain's Legacy. Nothing else from that expansion (bark fragments, amber, elves) comes with it.
    public static bool ColoredWoods { get; set; }

    // the owner, 2026-10-20: / monster corpses. A player's corpse lasts this long
    // before it turns to bones, and the bones this long before they vanish (stock: 7 and 7). Monster corpses keep 7.
    // the owner, 2026-10-20. Doom's lever puzzle (the lamp room) needs five living
    // players on its five spots at once (OSI). With this on, one player on any spot is enough and that player is
    // the one sent into the lamp room. The lever order, statue hints, rocks on a wrong order and 30 s reset are stock.
    public static bool SoloLeverPuzzle { get; set; } = true;

    // the owner, 2026-10-20: New Haven's
    // townsfolk exist only in the later (post-uoml) spawn set; this SE world populates the old Haven. With this on,
    // the old Haven heads the starting-city list, so a new Paladin starts where Uzeraan's quest is.
    public static bool OldHavenStart { get; set; } = true;

    // the owner, 2026-10-20: a troll to Trammel Britain and did nothing.
    // Stock guards only take a monster whose crime (its hit on you) happened inside the town. With this on, a wild
    // monster that is still flagged criminal from a hit outside becomes a guard target the moment it walks in.
    // Tamed pets and summons are left stock.
    public static bool GuardsTakeChasers { get; set; } = true;

    // the owner, 2026-10-27. Every player can stable at least this many pets (stock SE: 2 to 5 from Animal
    // Taming + Animal Lore + Veterinary, plus a bonus per skill over 100). The skill formula still applies if it gives
    // more. The stable master's claim list has no pages and grows 20 px per pet, so above about 35 it runs off a
    // 1080-pixel screen - raise this past 35 only together with paging that list.
    public static int MinStableSlots { get; set; } = 30;

    public static TimeSpan PlayerCorpseDecay { get; private set; } = TimeSpan.FromMinutes(30);

    public static TimeSpan PlayerBonesDecay { get; private set; } = TimeSpan.FromMinutes(30);

    // the owner, 2026-10-20. A ship sailed into a serpent pillar on Trammel or Felucca comes out at its
    // twin, between the Britannia sea and the Lost Lands sea, as on production shards of this era. Stock: no ship can
    // cross. See SerpentPillars.
    public static bool SerpentPillarShips { get; set; } = true;

    public static void Configure()
    {
        HousePerCharacter = ServerConfiguration.GetOrUpdateSetting("avallone.housePerCharacter", true);
        // the owner, 2026-10-03. Everyone gets stock 5 + this.
        FollowersBonus = ServerConfiguration.GetOrUpdateSetting("avallone.followersBonus", 2);
        ChampionsOnTrammel = ServerConfiguration.GetOrUpdateSetting("avallone.championsOnTrammel", true);
        FailedCraftLosesHalf = ServerConfiguration.GetOrUpdateSetting("avallone.failedCraftLosesHalf", true);
        ColoredWoods = ServerConfiguration.GetOrUpdateSetting("avallone.coloredWoods", true);
        SoloLeverPuzzle = ServerConfiguration.GetOrUpdateSetting("avallone.soloLeverPuzzle", true);
        OldHavenStart = ServerConfiguration.GetOrUpdateSetting("avallone.oldHavenStart", true);
        GuardsTakeChasers = ServerConfiguration.GetOrUpdateSetting("avallone.guardsTakeChasers", true);
        PlayerCorpseDecay = TimeSpan.FromMinutes(ServerConfiguration.GetOrUpdateSetting("avallone.playerCorpseMinutes", 30));
        PlayerBonesDecay = TimeSpan.FromMinutes(ServerConfiguration.GetOrUpdateSetting("avallone.playerBonesMinutes", 30));
        EventSink.Connected += ApplyFollowerLimit;
        EventSink.ServerStarted += OnServerStarted;
        SerpentPillarShips = ServerConfiguration.GetOrUpdateSetting("avallone.serpentPillarShips", true);
        MinStableSlots = ServerConfiguration.GetOrUpdateSetting("avallone.minStableSlots", 30);
    }

    // The limit is saved per character (stock 5), so it is set each time a character enters the world: that covers
    // old characters, new ones, and a later change of the setting, with no save-file change.
    private static void ApplyFollowerLimit(Mobile m)
    {
        var max = StockFollowersMax + FollowersBonus;
        if (m is PlayerMobile && m.FollowersMax != max)
        {
            m.FollowersMax = max;
        }
    }

    private static void OnServerStarted()
    {
        logger.Information(
            "Avallone rules: follower limit {Max} (stock {Stock} + {Bonus})",
            StockFollowersMax + FollowersBonus,
            StockFollowersMax,
            FollowersBonus
        );
        logger.Information(
            "Avallone rules: at least {Slots} stable slots per player (avallone.minStableSlots; stock 2-5 by skill){Warn}",
            MinStableSlots,
            MinStableSlots > 35 ? " - WARNING: over 35, the claim list runs off the screen" : ""
        );
        logger.Information(
            "Avallone rules: house limit is one per {Scope} (avallone.housePerCharacter)",
            HousePerCharacter ? "character" : "account"
        );
        logger.Information(
            "Avallone rules: failed craft loses {Loss} (avallone.failedCraftLosesHalf); coloured woods {Woods} (avallone.coloredWoods)",
            FailedCraftLosesHalf ? "half" : "all (stock)",
            ColoredWoods ? "on" : "off (stock)"
        );
        logger.Information(
            "Avallone rules: player corpse {Corpse} min, player bones {Bones} min (stock 7 and 7; monster corpses 7); Doom lever puzzle {Lever} (avallone.soloLeverPuzzle)",
            PlayerCorpseDecay.TotalMinutes,
            PlayerBonesDecay.TotalMinutes,
            SoloLeverPuzzle ? "one player" : "five players (stock)"
        );
        logger.Information(
            "Avallone rules: ships {Ships} the serpent pillars between Britannia and the Lost Lands (avallone.serpentPillarShips)",
            SerpentPillarShips ? "sail through" : "cannot pass (stock)"
        );
        logger.Information(
            "Avallone rules: starting cities {Cities} (avallone.oldHavenStart {Old})",
            string.Join(", ", System.Linq.Enumerable.Select(Server.Engines.CharacterCreation.CharacterCreation.GetStartingCities(), c => c.City)),
            OldHavenStart
        );
        logger.Information(
            "Avallone rules: guards take a monster that hit you outside town and follows you in: {Chasers} (avallone.guardsTakeChasers)",
            GuardsTakeChasers ? "on" : "off (stock)"
        );
        // the owner, 2026-10-20. Treasures of Tokuno is set in Configuration/tot.json (stock: no file,
        // so no drops). Logged here so a restart shows whether it took.
        logger.Information(
            "Avallone rules: Treasures of Tokuno drops {Drop}, rewards {Reward} (Configuration/tot.json)",
            Server.Misc.TreasuresOfTokuno.DropEra,
            Server.Misc.TreasuresOfTokuno.RewardEra
        );

        foreach (var c in BaseCompanion.All)
        {
            logger.Information("Avallone companion: {Line}", Describe(c));
        }
    }

    // One line: who she is with, her stats, and every skill she has any of. For the log and for [ai companions.
    public static string Describe(BaseCompanion c)
    {
        var sb = new StringBuilder();
        sb.Append(c.Name).Append(" - with ").Append(c.ControlMaster?.Name ?? "nobody");
        sb.Append($"; Str {c.RawStr} Dex {c.RawDex} Int {c.RawInt} Hits {c.HitsMax}; skills:");

        for (var i = 0; i < c.Skills.Length; i++)
        {
            var skill = c.Skills[i];
            if (skill.Base > 0)
            {
                sb.Append($" {skill.Name} {skill.Base:F1},");
            }
        }

        return sb.ToString().TrimEnd(',');
    }
}
