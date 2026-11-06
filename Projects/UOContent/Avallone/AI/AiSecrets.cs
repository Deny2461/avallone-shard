using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Server.Logging;
using Server.Mobiles;

namespace Server.Avallone.AI;

// Phase 4b - who has EARNED which companion secret (design v2, Projects/Avallone-AI-NPC-Plan.md).
// The shard owns this; the brain only holds the text and gets one secret's text only after the shard
// approves that reveal. Saved with the world ("AvalloneSecrets"), so a restored world restores it too.
//
// Rules (Claude's defaults, approved unseen with the secrets; the owner can retune): counters start at the 4b
// deploy (no back-pay); once a tier is earned it stays earned (latched); tiers go in order; at most one
// reveal per ~day per companion and character; only while travelling together.
public class AiSecrets : GenericPersistence
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(AiSecrets));
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private const int Version = 0;
    public static readonly TimeSpan RevealGap = TimeSpan.FromHours(20);

    public enum Status : byte { Locked = 0, Eligible = 1, Pending = 2, Revealed = 3 }

    public sealed record TierRule(int MinTrust, int MinFights, int MinConversations, int MinDays);

    // Index = tier. Trust bands: 25 warming, 50 trusting, 75 devoted.
    public static readonly TierRule[] Rules = [null, new(25, 3, 10, 1), new(50, 15, 0, 3), new(75, 0, 0, 7)];

    public sealed class Pair
    {
        public int Fights;
        public int Conversations;
        public int Days;
        public string LastDay = "";
        public DateTime LastRevealUtc;
        public readonly Dictionary<string, Status> Secrets = new();
    }

    public sealed record Gate(string Id, int Tier);

    private static AiSecrets _instance;
    private static readonly Dictionary<(Guid Npc, Guid Char), Pair> _pairs = new();
    private static readonly Dictionary<string, List<Gate>> _catalog = new();   // persona key -> gates (ids, tiers; no text)
    private static HttpClient _http;

    public AiSecrets() : base("AvalloneSecrets", 10)
    {
    }

    public static void Configure()
    {
        _instance = new AiSecrets();
        var url = ServerConfiguration.GetOrUpdateSetting("avallone.ai.sidecarUrl", "http://127.0.0.1:8765/");
        _http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
    }

    public static void Initialize()
    {
        // Gate lists come from the brain (it reads the pages). Re-fetched every 15 min; until then: no secrets.
        Timer.StartTimer(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(15), () => _ = RefreshCatalogAsync());
    }

    private static async Task RefreshCatalogAsync()
    {
        var keys = new HashSet<string>();
        foreach (var c in BaseCompanion.All)
        {
            if (!string.IsNullOrEmpty(c.PersonaKey))
            {
                keys.Add(c.PersonaKey);
            }
        }

        foreach (var key in keys)
        {
            try
            {
                var response = await _http.PostAsJsonAsync("v1/secrets/gates", new { persona_key = key });
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var r = doc.RootElement;
                if (!r.GetProperty("ok").GetBoolean())
                {
                    throw new InvalidOperationException(r.GetProperty("error").GetString());
                }

                var gates = new List<Gate>();
                foreach (var g in r.GetProperty("gates").EnumerateArray())
                {
                    gates.Add(new Gate(g.GetProperty("id").GetString(), g.GetProperty("tier").GetInt32()));
                }

                gates.Sort((a, b) => a.Tier.CompareTo(b.Tier));
                var changed = !_catalog.TryGetValue(key, out var old) || old.Count != gates.Count;
                _catalog[key] = gates;
                if (changed)
                {
                    logger.Information("Avallone secrets: {Key} has {Count} secret(s) (tiers {Tiers})", key, gates.Count,
                        string.Join(",", gates.ConvertAll(g => g.Tier)));
                }
            }
            catch (Exception ex)
            {
                logger.Warning("Avallone secrets: could not fetch gates for {Key} ({Error}) - secrets off for it until next try", key, ex.Message);
            }
        }
    }

    private static Pair Get(Guid npc, Guid chr)
    {
        if (!_pairs.TryGetValue((npc, chr), out var p))
        {
            _pairs[(npc, chr)] = p = new Pair();
        }

        return p;
    }

    // Called for every companion event (from AiLedger.Record, before any ledger state check).
    public static void Count(Guid npc, Guid chr, string kind)
    {
        if (npc == Guid.Empty || chr == Guid.Empty || kind == "character_deleted")
        {
            return;
        }

        var p = Get(npc, chr);
        if (kind == "fight_survived")
        {
            p.Fights++;
        }
        else if (kind == "conversation")
        {
            p.Conversations++;
        }

        var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (day != p.LastDay)
        {
            p.LastDay = day;
            p.Days++;
        }
    }

    // Pure rule check (unit-tested).
    public static bool Earned(int tier, int trust, int fights, int conversations, int days, bool previousRevealed)
    {
        if (tier < 1 || tier >= Rules.Length || tier > 1 && !previousRevealed)
        {
            return false;
        }

        var r = Rules[tier];
        return trust >= r.MinTrust && fights >= r.MinFights && conversations >= r.MinConversations && days >= r.MinDays;
    }

    // Latch anything newly earned, then describe each secret for the brain: closed / open / pending / revealed.
    // Only the lowest earned-but-untold tier is "open", and only outside the gap after the last reveal.
    public static AiRequest.SecretOffer[] Offers(BaseCompanion c, Mobile chr, int trust, DateTime nowUtc)
    {
        if (c?.PersonaKey == null || !_catalog.TryGetValue(c.PersonaKey, out var gates) || gates.Count == 0)
        {
            return null;
        }

        var p = Get(c.AiId, AiIdentity.GetOrCreate(chr));
        var prevRevealed = true;
        var offers = new List<AiRequest.SecretOffer>();
        var opened = false;
        foreach (var g in gates)
        {
            var s = p.Secrets.GetValueOrDefault(g.Id);
            if (s == Status.Locked && Earned(g.Tier, trust, p.Fights, p.Conversations, p.Days, prevRevealed))
            {
                p.Secrets[g.Id] = s = Status.Eligible;
                logger.Information("Avallone secrets: {Key} tier {Tier} EARNED by {Char}", c.PersonaKey, g.Tier, chr.Name);
            }

            var state = s switch
            {
                Status.Revealed => "revealed",
                Status.Pending => "pending",
                Status.Eligible when !opened && nowUtc - p.LastRevealUtc >= RevealGap && c.ControlMaster == chr => "open",
                _ => "closed"
            };
            opened |= state == "open";
            offers.Add(new AiRequest.SecretOffer(g.Id, g.Tier, state));
            prevRevealed = s == Status.Revealed;
        }

        return offers.ToArray();
    }

    // The model chose reveal_secret: re-check from the shard's own state, then mark it pending (saved).
    public static bool TryApprove(BaseCompanion c, Mobile chr, int trust, DateTime nowUtc, out Gate gate)
    {
        gate = null;
        var offers = Offers(c, chr, trust, nowUtc);
        if (offers == null)
        {
            return false;
        }

        foreach (var o in offers)
        {
            if (o.State == "open")
            {
                gate = new Gate(o.Id, o.Tier);
                Get(c.AiId, AiIdentity.GetOrCreate(chr)).Secrets[o.Id] = Status.Pending;
                return true;
            }
        }

        return false;
    }

    public static Gate PendingFor(BaseCompanion c, Mobile chr)
    {
        if (c?.PersonaKey == null || !_catalog.TryGetValue(c.PersonaKey, out var gates) ||
            !_pairs.TryGetValue((c.AiId, AiIdentity.GetOrCreate(chr)), out var p))
        {
            return null;
        }

        foreach (var g in gates)
        {
            if (p.Secrets.GetValueOrDefault(g.Id) == Status.Pending)
            {
                return g;
            }
        }

        return null;
    }

    public static void MarkRevealed(BaseCompanion c, Mobile chr, Gate g, DateTime nowUtc)
    {
        var p = Get(c.AiId, AiIdentity.GetOrCreate(chr));
        p.Secrets[g.Id] = Status.Revealed;
        p.LastRevealUtc = nowUtc;
        logger.Information("Avallone secrets: {Key} tier {Tier} REVEALED to {Char}", c.PersonaKey, g.Tier, chr.Name);
    }

    // GM view: progress only, never text.
    public static string Describe(BaseCompanion c, Mobile chr)
    {
        var p = Get(c.AiId, AiIdentity.GetOrCreate(chr));
        var tiers = new List<string>();
        if (_catalog.TryGetValue(c.PersonaKey ?? "", out var gates))
        {
            foreach (var g in gates)
            {
                tiers.Add($"tier {g.Tier} {p.Secrets.GetValueOrDefault(g.Id)}");
            }
        }

        return $"{c.PersonaKey} with {chr.Name}: trust {c.GetTrust(chr)}, fights {p.Fights}, talks {p.Conversations}, days {p.Days}; " +
               (tiers.Count == 0 ? "no secrets loaded" : string.Join(", ", tiers));
    }

    public override void Serialize(IGenericWriter writer)
    {
        writer.WriteEncodedInt(Version);
        writer.WriteEncodedInt(_pairs.Count);
        foreach (var ((npc, chr), p) in _pairs)
        {
            writer.Write(npc);
            writer.Write(chr);
            writer.WriteEncodedInt(p.Fights);
            writer.WriteEncodedInt(p.Conversations);
            writer.WriteEncodedInt(p.Days);
            writer.Write(p.LastDay);
            writer.Write(p.LastRevealUtc);
            writer.WriteEncodedInt(p.Secrets.Count);
            foreach (var (id, s) in p.Secrets)
            {
                writer.Write(id);
                writer.Write((byte)s);
            }
        }
    }

    public override void Deserialize(IGenericReader reader)
    {
        reader.ReadEncodedInt(); // version
        var count = reader.ReadEncodedInt();
        for (var i = 0; i < count; i++)
        {
            var p = new Pair();
            var npc = reader.ReadGuid();
            var chr = reader.ReadGuid();
            p.Fights = reader.ReadEncodedInt();
            p.Conversations = reader.ReadEncodedInt();
            p.Days = reader.ReadEncodedInt();
            p.LastDay = reader.ReadString() ?? "";
            p.LastRevealUtc = reader.ReadDateTime();
            var n = reader.ReadEncodedInt();
            for (var j = 0; j < n; j++)
            {
                var id = reader.ReadString();
                p.Secrets[id] = (Status)reader.ReadByte();
            }

            _pairs[(npc, chr)] = p;
        }
    }
}
