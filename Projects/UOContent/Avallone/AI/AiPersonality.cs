using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Server.Logging;
using Server.Mobiles;

namespace Server.Avallone.AI;

// Phase 4c1 - how a companion's personality grows with one character (design v2, Projects/Avallone-AI-NPC-Plan.md).
// Immutable core = the persona file. On top, a few bounded traits (-2..+2, start 0) that change ONLY at
// turning points: rules evaluated by code on event counts and trust bands, each firing at most once per
// companion + character. The model never changes a trait. Event-sourced: traits are recomputed from the
// applied, un-revoked turning points; a GM undo revokes one and suppresses it so it can't re-fire.
//
// Rules live in Distribution/Data/Avallone/turning-points.json, written by Claude and approved UNSEEN
// (the owner is the player). Saved with the world.
public class AiPersonality : GenericPersistence
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(AiPersonality));

    private const int Version = 0;
    public const int TraitMin = -2;
    public const int TraitMax = 2;

    public static readonly string[] Bands = ["wary", "warming", "trusting", "devoted"];

    public sealed record Rule(string Id, string Band, Dictionary<string, int> Counts, Dictionary<string, int> Changes, string Memory);

    public sealed record Applied(string Id, DateTime AtUtc, bool Revoked);

    public sealed class Pair
    {
        public readonly Dictionary<string, int> Counts = new();
        public readonly List<Applied> Applied = [];
        public readonly HashSet<string> Suppressed = [];
    }

    private static AiPersonality _instance;
    private static readonly Dictionary<(Guid Npc, Guid Char), Pair> _pairs = new();
    private static Dictionary<string, List<Rule>> _rules = new();
    private static Dictionary<string, string[]> _traitText = new();
    private static bool _evaluating;

    public AiPersonality() : base("AvallonePersonality", 10)
    {
    }

    public static void Configure()
    {
        _instance = new AiPersonality();
        Load(Path.Combine(Core.BaseDirectory, "Data", "Avallone", "turning-points.json"));
    }

    public static void Load(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var traits = new Dictionary<string, string[]>();
            foreach (var t in doc.RootElement.GetProperty("traits").EnumerateObject())
            {
                var lines = new List<string>();
                foreach (var l in t.Value.EnumerateArray())
                {
                    lines.Add(l.GetString() ?? "");
                }

                if (lines.Count != TraitMax - TraitMin + 1)
                {
                    throw new InvalidDataException($"trait {t.Name} needs {TraitMax - TraitMin + 1} lines");
                }

                traits[t.Name] = lines.ToArray();
            }

            var rules = new Dictionary<string, List<Rule>>();
            foreach (var c in doc.RootElement.GetProperty("companions").EnumerateObject())
            {
                var list = new List<Rule>();
                foreach (var r in c.Value.EnumerateArray())
                {
                    var counts = new Dictionary<string, int>();
                    if (r.TryGetProperty("counts", out var ce))
                    {
                        foreach (var p in ce.EnumerateObject())
                        {
                            counts[p.Name] = p.Value.GetInt32();
                        }
                    }

                    var changes = new Dictionary<string, int>();
                    foreach (var p in r.GetProperty("changes").EnumerateObject())
                    {
                        if (!traits.ContainsKey(p.Name))
                        {
                            throw new InvalidDataException($"{r.GetProperty("id").GetString()}: unknown trait {p.Name}");
                        }

                        changes[p.Name] = Math.Clamp(p.Value.GetInt32(), -1, 1);   // one step per turning point
                    }

                    var band = r.TryGetProperty("band", out var b) ? b.GetString() : null;
                    if (band != null && Array.IndexOf(Bands, band) < 0)
                    {
                        throw new InvalidDataException($"unknown band {band}");
                    }

                    list.Add(new Rule(r.GetProperty("id").GetString(), band, counts, changes, r.GetProperty("memory").GetString()));
                }

                rules[c.Name] = list;
            }

            _traitText = traits;
            _rules = rules;
            logger.Information("Avallone personality: {Traits} traits, rules for {Companions}", traits.Count, string.Join(", ", rules.Keys));
        }
        catch (Exception ex)
        {
            logger.Warning("Avallone personality: no turning points loaded from {Path} ({Error}) - traits stay at 0", path, ex.Message);
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

    // Pure rule check (unit-tested): every condition must hold.
    public static bool Met(Rule r, int bandIndex, IReadOnlyDictionary<string, int> counts)
    {
        if (r.Band != null && bandIndex < Array.IndexOf(Bands, r.Band))
        {
            return false;
        }

        foreach (var (kind, min) in r.Counts)
        {
            if (!counts.TryGetValue(kind, out var n) || n < min)
            {
                return false;
            }
        }

        return true;
    }

    // Traits are a projection of applied, un-revoked turning points (so undo never leaves a bad sum behind).
    public static Dictionary<string, int> Project(IEnumerable<Applied> applied, IReadOnlyDictionary<string, Rule> byId)
    {
        var traits = new Dictionary<string, int>();
        foreach (var a in applied)
        {
            if (a.Revoked || !byId.TryGetValue(a.Id, out var r))
            {
                continue;
            }

            foreach (var (t, d) in r.Changes)
            {
                traits[t] = Math.Clamp(traits.GetValueOrDefault(t) + d, TraitMin, TraitMax);
            }
        }

        return traits;
    }

    private static Dictionary<string, Rule> RulesById(string key)
    {
        var d = new Dictionary<string, Rule>();
        foreach (var r in _rules.GetValueOrDefault(key ?? "") ?? [])
        {
            d[r.Id] = r;
        }

        return d;
    }

    // From AiLedger.Record for every companion event: count it, then check this pair's rules.
    public static void OnEvent(BaseCompanion c, Mobile chr, string kind)
    {
        if (c == null || chr == null || chr.Deleted || kind == "character_deleted")
        {
            return;
        }

        var p = Get(c.AiId, AiIdentity.GetOrCreate(chr));
        p.Counts[kind] = p.Counts.GetValueOrDefault(kind) + 1;

        if (_evaluating || !_rules.TryGetValue(c.PersonaKey ?? "", out var rules))
        {
            return;
        }

        var band = Array.IndexOf(Bands, CompanionTrustBand(c.GetTrust(chr)));
        _evaluating = true;
        try
        {
            foreach (var r in rules)
            {
                if (p.Suppressed.Contains(r.Id) || p.Applied.Exists(a => a.Id == r.Id) || !Met(r, band, p.Counts))
                {
                    continue;
                }

                p.Applied.Add(new Applied(r.Id, DateTime.UtcNow, false));
                logger.Information("Avallone personality: {Key} turning point {Id} with {Char}", c.PersonaKey, r.Id, chr.Name);
                AiLedger.Record(c, chr, "turning_point", new { turning_point = r.Id, text = r.Memory });
            }
        }
        finally
        {
            _evaluating = false;
        }
    }

    private static string CompanionTrustBand(int trust) => Server.Avallone.Companions.CompanionTrust.Band(trust);

    // Lines for her prompt ("what is happening now"), from the projected traits. Empty at 0.
    public static string Describe(BaseCompanion c, Mobile chr)
    {
        if (!_pairs.TryGetValue((c.AiId, AiIdentity.GetOrCreate(chr)), out var p))
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (var (t, v) in Project(p.Applied, RulesById(c.PersonaKey)))
        {
            if (v != 0 && _traitText.TryGetValue(t, out var lines) && lines[v - TraitMin].Length > 0)
            {
                sb.Append(lines[v - TraitMin]).Append(' ');
            }
        }

        return sb.ToString().Trim();
    }

    // GM view: traits and turning point ids (memory lines are not shown).
    public static string Status(BaseCompanion c, Mobile chr)
    {
        var p = Get(c.AiId, AiIdentity.GetOrCreate(chr));
        var traits = Project(p.Applied, RulesById(c.PersonaKey));
        var t = traits.Count == 0 ? "all 0" : string.Join(", ", Array.ConvertAll([..traits], kv => $"{kv.Key} {kv.Value:+0;-0;0}"));
        var tps = p.Applied.Count == 0 ? "none" : string.Join(", ", p.Applied.ConvertAll(a => a.Revoked ? a.Id + " (undone)" : a.Id));
        return $"{c.PersonaKey} with {chr.Name}: traits {t}; turning points {tps}";
    }

    // GM undo: revoke (kept, marked), suppress so it can't fire again, traits recomputed.
    public static bool Undo(BaseCompanion c, Mobile chr, string id)
    {
        var p = Get(c.AiId, AiIdentity.GetOrCreate(chr));
        var i = p.Applied.FindIndex(a => a.Id == id && !a.Revoked);
        if (i < 0)
        {
            return false;
        }

        p.Applied[i] = p.Applied[i] with { Revoked = true };
        p.Suppressed.Add(id);
        logger.Information("Avallone personality: {Key} turning point {Id} UNDONE with {Char}", c.PersonaKey, id, chr.Name);
        AiLedger.Record(c, chr, "turning_point_revoked", new { turning_point = id });
        return true;
    }

    public override void Serialize(IGenericWriter writer)
    {
        writer.WriteEncodedInt(Version);
        writer.WriteEncodedInt(_pairs.Count);
        foreach (var ((npc, chr), p) in _pairs)
        {
            writer.Write(npc);
            writer.Write(chr);
            writer.WriteEncodedInt(p.Counts.Count);
            foreach (var (k, n) in p.Counts)
            {
                writer.Write(k);
                writer.WriteEncodedInt(n);
            }

            writer.WriteEncodedInt(p.Applied.Count);
            foreach (var a in p.Applied)
            {
                writer.Write(a.Id);
                writer.Write(a.AtUtc);
                writer.Write(a.Revoked);
            }

            writer.WriteEncodedInt(p.Suppressed.Count);
            foreach (var s in p.Suppressed)
            {
                writer.Write(s);
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
            var nc = reader.ReadEncodedInt();
            for (var j = 0; j < nc; j++)
            {
                var k = reader.ReadString();
                p.Counts[k] = reader.ReadEncodedInt();
            }

            var na = reader.ReadEncodedInt();
            for (var j = 0; j < na; j++)
            {
                p.Applied.Add(new Applied(reader.ReadString(), reader.ReadDateTime(), reader.ReadBool()));
            }

            var ns = reader.ReadEncodedInt();
            for (var j = 0; j < ns; j++)
            {
                p.Suppressed.Add(reader.ReadString());
            }

            _pairs[(npc, chr)] = p;
        }
    }
}
