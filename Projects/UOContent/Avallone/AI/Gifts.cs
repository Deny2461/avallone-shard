using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Server.Avallone.Named;
using Server.Avallone.Rumours;
using Server.Engines.BulkOrders;
using Server.Items;
using Server.Logging;
using Server.Mobiles;

namespace Server.Avallone.AI;

// Step 5c-2 2b: gifts and tips (the owner, 2026-11-01: tips + likes; players learn by asking in game + the wiki page).
// Design: wiki Projects/Avallone-Memory-Affinity-Design.md "Step 2b ..." B5, amended by B12 #3-#4 (Codex 2b review).
// - Hooked from stock BaseCreature: gold AFTER the training-payment check (and in place of the stock "Thou art giving me
//   gold?" branch for dial-eligible NPCs), items after feeding/quests. Bulk-order deeds never get here as gifts.
// - One accepted gift per NPC + character + Central day. The day's slot is SAVED with the world (this file, "AvalloneGifts"),
//   like the item and the outbox, so a restore puts all three back together.
// - Order, on the game thread with no waiting: check the slot -> write the outbox event (AiLedger.TryRecord) -> only then
//   take the item. If the event cannot be written, nothing is taken. A declined offer leaves the item and the slot.
// - Gold: a tip of MinTip..MaxTip, taken whole; anything outside is declined whole. Items: one unit of a liked kind (the rest
//   of a stack comes back); blessed/newbied/insured/quest items and containers are declined. Gold and items are deleted.
public class Gifts : GenericPersistence
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(Gifts));
    private const int Version = 0;
    private const string LikesPath = "Data/Avallone/likes.json";

    private static Gifts _instance;
    private static readonly Dictionary<(Guid Npc, Guid Chr), string> _lastDay = new();

    internal static Func<Guid, Mobile, string, object, bool> Recorder = AiLedger.TryRecord;

    public static int MinTip { get; private set; } = 10;
    public static int MaxTip { get; private set; } = 250;

    public sealed record Likes(string Say, Type[] Types);
    private static Dictionary<string, Likes> _byPersona = new(StringComparer.OrdinalIgnoreCase);
    private static List<(string[] Match, Likes Likes)> _byRole = [];
    private static Likes _default = new("a coin or two - it never goes amiss", []);
    private static List<Likes> _quirks = [];

    public Gifts() : base("AvalloneGifts", 10)
    {
    }

    public static void Configure()
    {
        _instance = new Gifts();
        var path = Path.Combine(Core.BaseDirectory, LikesPath);
        if (File.Exists(path))
        {
            try
            {
                LoadLikes(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                logger.Warning("Avallone gifts: {Path} not loaded ({Error}) - gold tips only", LikesPath, e.Message);
            }
        }
    }

    // likes.json: { "tip": {"min", "max"}, "default": {"say"}, "personas": {key: {"say", "items"}}, "roles": [{"match", "say", "items"}] }
    internal static void LoadLikes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("tip", out var tip))
        {
            MinTip = Math.Max(1, tip.GetProperty("min").GetInt32());
            MaxTip = Math.Max(MinTip, tip.GetProperty("max").GetInt32());
        }

        var personas = new Dictionary<string, Likes>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("personas", out var ps))
        {
            foreach (var p in ps.EnumerateObject())
            {
                personas[p.Name] = Read(p.Value);
            }
        }

        var roles = new List<(string[], Likes)>();
        if (root.TryGetProperty("roles", out var rs))
        {
            foreach (var r in rs.EnumerateArray())
            {
                roles.Add((r.GetProperty("match").EnumerateArray().Select(x => x.GetString()!.ToLowerInvariant()).ToArray(), Read(r)));
            }
        }

        if (root.TryGetProperty("default", out var d))
        {
            _default = Read(d);
        }

        var quirks = new List<Likes>();
        if (root.TryGetProperty("quirks", out var qs))
        {
            foreach (var q in qs.EnumerateArray())
            {
                quirks.Add(Read(q));
            }
        }

        _byPersona = personas;
        _byRole = roles;
        _quirks = quirks;
    }

    private static Likes Read(JsonElement e)
    {
        var types = new List<Type>();
        if (e.TryGetProperty("items", out var items))
        {
            foreach (var name in items.EnumerateArray().Select(x => x.GetString()))
            {
                var t = AssemblyHandler.FindTypeByName(name);
                if (t == null || !typeof(Item).IsAssignableFrom(t))
                {
                    logger.Warning("Avallone gifts: likes.json names unknown item type {Name} - ignored", name);
                    continue;
                }

                types.Add(t);
            }
        }

        return new Likes(e.GetProperty("say").GetString() ?? "", types.ToArray());
    }

    // What this NPC likes. Named NPCs and wanderers with a persona entry: exactly their hand-written list. Everyone else: the
    // first role match (else the default), plus ONE extra from the "quirks" pool that is this NPC's own - picked by a stable
    // hash of its AI id, so it never changes for that NPC and differs from the next tailor's (the owner, chat, 2026-11-02
    // -> option b). Note: adding or removing a quirk re-deals every NPC's pick (pick = hash mod count).
    public static Likes LikesFor(Mobile npc)
    {
        if (npc is INamedNpc n && _byPersona.TryGetValue(n.NamedKey, out var named))
        {
            return named;
        }

        if (npc is BaseWanderer bw && _byPersona.TryGetValue(bw.PersonaKey, out var w))
        {
            return w;
        }

        var trade = TradeLikesFor(npc);
        var quirk = QuirkFor(Relations.NpcId(npc));
        return quirk == null
            ? trade
            : new Likes($"{trade.Say} - and I have a soft spot for {quirk.Say}", trade.Types.Concat(quirk.Types).Distinct().ToArray());
    }

    // This NPC's own odd like, or null (no AI id, or no pool). Explicit hash, never string.GetHashCode (randomised per process).
    public static Likes QuirkFor(Guid npcId)
    {
        if (npcId == Guid.Empty || _quirks.Count == 0)
        {
            return null;
        }

        var h = SHA256.HashData(Encoding.UTF8.GetBytes("avallone-quirk:" + npcId.ToString("N")));
        return _quirks[(int)(BitConverter.ToUInt32(h, 0) % (uint)_quirks.Count)];
    }

    private static Likes TradeLikesFor(Mobile npc)
    {

        var role = $"{npc.Title} {npc.GetType().Name}".ToLowerInvariant();
        foreach (var (match, likes) in _byRole)
        {
            if (match.Any(m => role.Contains(m)))
            {
                return likes;
            }
        }

        return _default;
    }

    public static bool Handles(Mobile npc, Mobile from) =>
        Relations.Enabled && from is PlayerMobile { Alive: true, Deleted: false } && Relations.NpcId(npc) != Guid.Empty;

    // Gold handed over (after the stock training-payment check). True = taken; false = it goes back.
    public static bool OnGold(Mobile npc, Mobile from, Gold gold)
    {
        if (Protected(gold))   // Codex 2b code review (optional): the same protection contract as items
        {
            Log(npc, from, "gold", "declined: protected gold", gold.Amount);
            npc.SayTo(from, "Keep your coin, friend - kindly meant all the same.");
            return false;
        }

        if (gold.Amount < MinTip)
        {
            Log(npc, from, "gold", "declined: below the smallest tip", gold.Amount);
            npc.SayTo(from, "Keep your coin, friend - kindly meant all the same.");
            return false;
        }

        if (gold.Amount > MaxTip)
        {
            Log(npc, from, "gold", "declined: above the largest tip", gold.Amount);
            npc.SayTo(from, "That is far too generous - I could not take it.");
            return false;
        }

        var value = gold.Amount >= MaxTip * 0.6 ? "generous" : gold.Amount >= MaxTip * 0.2 ? "fair" : "small";
        return Accept(npc, from, gold, "gold", value, whole: true, thanks: "My thanks - that is kind of you.");
    }

    // An item handed over (after the stock feeding/gold/quest checks). True = taken whole; false = it (or the rest) goes back.
    public static bool OnItem(Mobile npc, Mobile from, Item item)
    {
        if (item is Gold or SmallBOD or LargeBOD)
        {
            return false;
        }

        var likes = LikesFor(npc);
        if (Protected(item) || !Liked(likes, item))
        {
            Log(npc, from, item.GetType().Name, Protected(item) ? "declined: protected item" : "declined: not liked", item.Amount);
            // the owner's dev test 2026-11-02: read awkwardly
            npc.SayTo(from, $"I have no use for that, friend. I would welcome {likes.Say}.");
            return false;
        }

        return Accept(npc, from, item, "liked", "fair", whole: item.Amount <= 1, thanks: "Oh, now that is welcome - thank you!");
    }

    public static bool Protected(Item item) =>
        item.LootType is LootType.Blessed or LootType.Newbied || item.Insured || item.QuestItem || item is Container;

    public static bool Liked(Likes likes, Item item)
    {
        var t = item.GetType();
        return likes.Types.Any(l => l.IsAssignableFrom(t));
    }

    private static bool Accept(Mobile npc, Mobile from, Item item, string kind, string value, bool whole, string thanks)
    {
        var npcId = Relations.NpcId(npc);
        var key = (npcId, AiIdentity.GetOrCreate(from));
        var today = NamedNpcs.Today(Core.Now);
        if (_lastDay.TryGetValue(key, out var day) && day == today)
        {
            Log(npc, from, kind == "gold" ? "gold" : item.GetType().Name, "declined: already given today", item.Amount);
            npc.SayTo(from, "You have been generous enough today.");
            return false;
        }

        if (!Recorder(npcId, from, "gift_given", new { day = today, mpt = PlayedClock.MemorySeconds, pt = PlayedClock.Seconds, kind, value } /* 2c (design M7.3): pt = the played clock, mood fades a gift over played hours */))
        {
            Log(npc, from, kind == "gold" ? "gold" : item.GetType().Name, "declined: could not record it", item.Amount);
            npc.SayTo(from, "Not just now, friend."); // the record could not be written: nothing is taken
            return false;
        }

        if (_lastDay.Count >= 5000)   // Codex 2b code review (optional): keep only today's slots in memory
        {
            foreach (var old in _lastDay.Where(p => p.Value != today).Select(p => p.Key).ToList())
            {
                _lastDay.Remove(old);
            }
        }

        _lastDay[key] = today;
        Log(npc, from, kind == "gold" ? "gold" : item.GetType().Name, $"accepted ({kind}, {value})", whole ? item.Amount : 1);
        npc.SayTo(from, thanks);
        if (whole)
        {
            item.Delete();
            return true;
        }

        item.Amount -= 1; // one unit taken; the rest of the stack goes back to the player
        return false;
    }

    // 2b: one line per gift handed over - who, to whom, what type, how many, and why it was taken or not.
    private static void Log(Mobile npc, Mobile from, string what, string outcome, int amount) =>
        logger.Information("Gift: {Player} -> {Npc} ({NpcType}): {What} x{Amount} - {Outcome}",
            from.Name, npc.Name, npc.GetType().Name, what, amount, outcome);

    public static bool GaveToday(Guid npc, Guid chr) =>
        _lastDay.TryGetValue((npc, chr), out var d) && d == NamedNpcs.Today(Core.Now);

    internal static void ClearForTests() => _lastDay.Clear();

    public override void Serialize(IGenericWriter writer)
    {
        writer.WriteEncodedInt(Version);
        var today = NamedNpcs.Today(Core.Now);
        var keep = _lastDay.Where(p => p.Value == today).ToList();   // only today's slots matter
        writer.WriteEncodedInt(keep.Count);
        foreach (var ((npc, chr), day) in keep)
        {
            writer.Write(npc);
            writer.Write(chr);
            writer.Write(day);
        }
    }

    public override void Deserialize(IGenericReader reader)
    {
        reader.ReadEncodedInt(); // version
        var count = reader.ReadEncodedInt();
        for (var i = 0; i < count; i++)
        {
            var npc = reader.ReadGuid();
            var chr = reader.ReadGuid();
            _lastDay[(npc, chr)] = reader.ReadString();
        }
    }
}
