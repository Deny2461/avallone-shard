using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Server.Commands;
using Server.Logging;
using Server.Mobiles;

namespace Server.Avallone.Companions;

// Where each companion lives, as data (the owner, chat, 2026-10-01: Claude places NPCs, the owner adjusts with [move).
// Data/Avallone/companions.json is the source of truth for home spots. At startup every listed companion
// that does not exist yet is created at home; existing ones take their home from the file.
// [companion sethome <key> writes her current spot back to the file.
public static class CompanionPlacement
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(CompanionPlacement));

    private const string FilePath = "Data/Avallone/companions.json";

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public sealed class Entry
    {
        public string Key { get; set; }
        public string Type { get; set; }
        public string Map { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }
        public string Note { get; set; }
    }

    private static string FullPath => Path.Combine(Core.BaseDirectory, FilePath);

    public static void Configure()
    {
        BaseCompanion.MaxActivePerCharacter =
            ServerConfiguration.GetOrUpdateSetting("avallone.companions.maxActive", 2);

        CommandSystem.Register("Companion", AccessLevel.GameMaster, Companion_OnCommand);
    }

    // After the world has loaded.
    public static void Initialize() => Place(null);

    private static List<Entry> Load()
    {
        var path = FullPath;
        if (!File.Exists(path))
        {
            logger.Warning("Avallone companions: {Path} not found, nobody placed", path);
            return [];
        }

        return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), _json) ?? [];
    }

    private static void Save(List<Entry> entries) =>
        File.WriteAllText(FullPath, JsonSerializer.Serialize(entries, _json) + Environment.NewLine);

    public static BaseCompanion Find(string key) =>
        BaseCompanion.All.FirstOrDefault(c => !c.Deleted && string.Equals(c.CompanionKey, key, StringComparison.OrdinalIgnoreCase));

    private static void Place(Mobile reportTo)
    {
        List<Entry> entries;
        try
        {
            entries = Load();
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Avallone companions: could not read {Path}", FullPath);
            reportTo?.SendMessage($"Could not read {FilePath}: {ex.Message}");
            return;
        }

        foreach (var e in entries)
        {
            var map = Map.Parse(e.Map);
            if (map == null || map == Map.Internal)
            {
                logger.Warning("Avallone companions: {Key} has an unknown map {Map}", e.Key, e.Map);
                continue;
            }

            var home = new Point3D(e.X, e.Y, e.Z);
            var companion = Find(e.Key);

            if (companion == null)
            {
                var type = AssemblyHandler.FindTypeByName(e.Type);
                if (type == null || !typeof(BaseCompanion).IsAssignableFrom(type) ||
                    Activator.CreateInstance(type) is not BaseCompanion created)
                {
                    logger.Warning("Avallone companions: {Key} has an unknown type {Type}", e.Key, e.Type);
                    continue;
                }

                companion = created;
                companion.SetHome(home, map);
                companion.MoveHome();
                logger.Information("Avallone companions: created {Name} at {Home} ({Map})", companion.Name, home, map);
                reportTo?.SendMessage($"Created {companion.Name} at {home} ({map}).");
            }
            else
            {
                companion.SetHome(home, map);
            }
        }
    }

    [Usage("Companion [list | place | home <key> | sethome <key> | trust <key> [value]]")]
    [Description(
        "Avallone companions. list = where each is, who she travels with, trust per character. place = create any listed in Data/Avallone/companions.json that do not exist. home = send her home. sethome = make her current spot her home (saved to the file). trust = show or set her trust in you (testing)."
    )]
    public static void Companion_OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;
        var verb = e.Length > 0 ? e.GetString(0).ToLowerInvariant() : "list";

        if (verb == "gearbox")
        {
            CompanionGear.GearboxCommand(from, e.Arguments);
            return;
        }

        if (verb == "list")
        {
            if (BaseCompanion.All.Count == 0)
            {
                from.SendMessage("No companions exist.");
            }

            foreach (var c in BaseCompanion.All)
            {
                var with = c.ControlMaster?.Name ?? (c.IsStabled ? $"{c.StabledBy?.Name} (logged out)" : "nobody");
                from.SendMessage(
                    $"{c.CompanionKey}: {c.Name} at {c.Location} ({c.Map}), home {c.HomeLocation} ({c.HomeMap}), with {with}, order {c.ControlOrder}{(c.IsWounded ? ", WOUNDED" : "")}"
                );
                foreach (var (m, t) in c.TrustEntries)
                {
                    from.SendMessage($"  trust {m?.Name}: {t} ({CompanionTrust.Band(t)})");
                }
            }

            return;
        }

        if (verb == "place")
        {
            Place(from);
            from.SendMessage("Placement done.");
            return;
        }

        if (e.Length < 2)
        {
            from.SendMessage("Usage: [companion home|sethome|trust <key>");
            return;
        }

        var companion = Find(e.GetString(1));
        if (companion == null)
        {
            from.SendMessage($"No companion '{e.GetString(1)}'.");
            return;
        }

        switch (verb)
        {
            case "home":
                companion.GoHome();
                from.SendMessage($"{companion.Name} is going home.");
                break;
            case "sethome":
                SetHomeHere(from, companion);
                break;
            case "trust":
                if (e.Length >= 3)
                {
                    companion.SetTrust(from, e.GetInt32(2), $"set by {from.Name} with [companion trust");
                }

                var t = companion.GetTrust(from);
                from.SendMessage($"{companion.Name}'s trust in you: {t} ({CompanionTrust.Band(t)}).");
                break;
            default:
                from.SendMessage("Usage: [companion list|place|home|sethome|trust");
                break;
        }
    }

    private static void SetHomeHere(Mobile from, BaseCompanion companion)
    {
        if (companion.Map == null || companion.Map == Map.Internal)
        {
            from.SendMessage($"{companion.Name} is not in the world right now.");
            return;
        }

        try
        {
            var entries = Load();
            var entry = entries.FirstOrDefault(x => string.Equals(x.Key, companion.CompanionKey, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entries.Add(entry = new Entry { Key = companion.CompanionKey, Type = companion.GetType().Name });
            }

            entry.Map = companion.Map.Name;
            entry.X = companion.X;
            entry.Y = companion.Y;
            entry.Z = companion.Z;
            Save(entries);

            companion.SetHome(companion.Location, companion.Map);
            from.SendMessage($"{companion.Name}'s home is now {companion.Location} ({companion.Map}), saved to {FilePath}.");
            logger.Information("Avallone companions: {Key} home set to {Home} ({Map}) by {Who}",
                companion.CompanionKey, companion.Location, companion.Map, from.Name);
        }
        catch (Exception ex)
        {
            from.SendMessage($"Could not save {FilePath}: {ex.Message}");
        }
    }
}
