using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Server.Avallone.AI;
using Server.Avallone.Named;
using Server.Logging;
using Server.Mobiles;

namespace Server.Avallone.Townsfolk;

// Townsfolk step, Stage 1 - the roster (design §4 [v4]; Codex design R4 GO, 2026-11-15).
//
// One document for the whole world: every town's resident townsfolk (who is where). The shard sends it to the brain,
// which in Stage 1 only stores it. What makes it safe to use later is ONE rule: content equality. Every prompt request
// carries the hash of the shard's current document (the "stamp"); the brain may use its held roster for that turn only
// if its hash is the same. Epoch and revision are carried for the logs and decide nothing.
//
// The stamp is computed from the live world each time it is asked for, so there is no "dirty" window to get wrong: a
// rename, a deletion or a new occupant changes the very next stamp. It is "none" whenever the seats are not in a state
// to vouch for who is who.
//
// Wire form (pinned on both sides, design §13 item 3): {"epoch", "rev", "hash", "content"} where content is the
// canonical document AS A STRING and hash is SHA-256 (lower-case hex) of that string's UTF-8 bytes. The brain hashes
// the string it received; nothing is re-serialised, so the two sides cannot disagree about the canonical form.
//
// Switch: avallone.townsfolk.roster (default off). Off = no stamp on requests and nothing sent.
public static class TownRoster
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(TownRoster));

    public const string None = "none";

    private static readonly Regex _plain = new(@"^[A-Za-z][A-Za-z '\-]{0,39}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static HttpClient _http;
    private static bool _inFlight;
    private static string _ackedHash; // the hash the brain last said it holds
    private static string _lastHash;
    private static long _rev;
    private static long _retryAt;
    private static long _probeAt;
    private static List<BaseCreature> _others = [];
    private static long _othersAt = long.MinValue;
    private static readonly HashSet<Serial> _warned = [];

    public static bool Enabled { get; private set; }

    public static long Rev => _rev;

    public static string AckedHash => _ackedHash;

    // How long the last Build took on the sender's tick, in milliseconds. The same work is done once per prompt request.
    public static double LastBuildMs { get; private set; }

    public static void Configure()
    {
        Enabled = ServerConfiguration.GetOrUpdateSetting("avallone.townsfolk.roster", false);
        var url = ServerConfiguration.GetOrUpdateSetting("avallone.ai.sidecarUrl", "http://127.0.0.1:8765/");
        _http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };

        if (Enabled)
        {
            EventSink.ServerStarted += () => Timer.StartTimer(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), Tick);
        }

        // DEV RIG ONLY (Codex code R1: "measure steady state and peak before enabling live"): N builds back to back on
        // the game thread 30 s after start, timed, plus builds that include the membership scan. Never set on live.
        var bench = ServerConfiguration.GetSetting("avallone.townsfolk.rosterBench", 0);

        if (Enabled && bench > 0)
        {
            EventSink.ServerStarted += () => Timer.StartTimer(TimeSpan.FromSeconds(30), () => Bench(bench));
        }
    }

    private static void Bench(int n)
    {
        if (!Usable)
        {
            logger.Warning("Roster bench: the seats cannot vouch yet - not run");
            return;
        }

        var plain = new List<double>(n);
        var bytes = 0;

        for (var i = 0; i < n; i++)
        {
            var t = System.Diagnostics.Stopwatch.GetTimestamp();
            bytes = Build().Content.Length;
            plain.Add(System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }

        var scan = new List<double>(10);

        for (var i = 0; i < 10; i++)
        {
            _othersAt = long.MinValue; // force the once-a-minute scan of every Mobile into this build
            var t = System.Diagnostics.Stopwatch.GetTimestamp();
            Build();
            scan.Add(System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }

        plain.Sort();
        scan.Sort();
        logger.Warning(
            "Roster bench: DEV ONLY - {N} builds back to back, {Bytes} bytes each: median {Median:F2} ms, 95th {P95:F2} ms, max {Max:F2} ms, total {Total:F0} ms; with the scan of {Mobiles} Mobiles: median {ScanMedian:F2} ms, max {ScanMax:F2} ms",
            n, bytes, plain[n / 2], plain[(int)(n * 0.95)], plain[n - 1], plain.Sum(), World.Mobiles.Count, scan[5], scan[9]
        );
    }

    // Can the seats vouch for who is who right now? If not, there is no roster and the stamp is "none".
    public static bool Usable => Seats.Active && !Seats.Off && Seats.Exposed;

    // The stamp every prompt request carries (design §4.3). null = the switch is off: the field is left out entirely.
    public static string StampForRequest() => !Enabled ? null : Usable ? Build().Hash : None;

    private sealed record Person(string Id, string Name, string Role, string Sex, string Kind, Point3D? Home);

    // Hand-placed townsfolk and the named NPCs (a handful): found by a scan at most once a minute. Membership can lag by
    // that minute - a new hand-placed NPC is absent until then, which says less, never more. Everything ABOUT a listed
    // NPC is read live at Build.
    private static List<BaseCreature> Others()
    {
        if (Core.TickCount - _othersAt < 60_000 && _othersAt != long.MinValue)
        {
            return _others;
        }

        var list = new List<BaseCreature>();

        foreach (var m in World.Mobiles.Values)
        {
            if (m is BaseCreature bc && !bc.Deleted && !Seats.TryGetSeat(bc, out _) &&
                (IsNamed(bc) || TownsfolkTalk.IsTownsperson(bc) && bc is not BaseEscortable))
            {
                list.Add(bc);
            }
        }

        _others = list;
        _othersAt = Core.TickCount;
        return list;
    }

    private static bool IsNamed(Mobile m) => m is Mirabel or INamedNpc;

    // The canonical document and its hash, from the world as it is at this instant.
    public static (string Content, string Hash) Build()
    {
        var towns = new SortedDictionary<(string Map, string Town), SortedDictionary<string, Person>>(
            Comparer<(string Map, string Town)>.Create(
                (a, b) =>
                {
                    var c = string.CompareOrdinal(a.Map, b.Map);
                    return c != 0 ? c : string.CompareOrdinal(a.Town, b.Town);
                }
            )
        );

        foreach (var (m, _) in Seats.OccupiedForRoster())
        {
            Add(towns, m, "resident");
        }

        foreach (var bc in Others())
        {
            if (!bc.Deleted && !Seats.TryGetSeat(bc, out _))
            {
                Add(towns, bc, IsNamed(bc) ? "named" : "placed");
            }
        }

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartArray("towns");

            foreach (var ((map, town), people) in towns)
            {
                w.WriteStartObject();
                w.WriteString("map", map);
                w.WriteString("town", town);
                w.WriteStartArray("people");

                foreach (var p in people.Values)
                {
                    w.WriteStartObject();
                    w.WriteString("id", p.Id);
                    w.WriteString("name", p.Name);
                    w.WriteString("role", p.Role);
                    w.WriteString("sex", p.Sex);
                    w.WriteString("kind", p.Kind);

                    if (p.Home is { } home)
                    {
                        w.WriteStartObject("home");
                        w.WriteNumber("x", home.X);
                        w.WriteNumber("y", home.Y);
                        w.WriteEndObject();
                    }
                    else
                    {
                        w.WriteNull("home");
                    }

                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        var bytes = ms.ToArray();
        return (Encoding.UTF8.GetString(bytes), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static void Add(
        SortedDictionary<(string Map, string Town), SortedDictionary<string, Person>> towns, Mobile m, string kind
    )
    {
        if (m is not BaseCreature bc || bc.Deleted || bc.Map == null || bc.Map == Map.Internal)
        {
            return;
        }

        // The town key (design §4.2, refined in §14.2): the named TownRegion at the NPC's HOME point, on its home map.
        var hasHome = bc.Home != Point3D.Zero;
        var map = hasHome && bc.HomeMap != null && bc.HomeMap != Map.Internal ? bc.HomeMap : bc.Map;
        var town = TownsfolkCensus.TownAt(map, hasHome ? bc.Home : bc.Location);

        if (town == null)
        {
            return; // not in a town: on no roster
        }

        // The id the NPC uses in its own requests: a seat's id for a resident; a named NPC's own id (INamedNpc.AiId -
        // "one id per named NPC for requests AND memories"); the per-Mobile id for a hand-placed townsperson.
        var id = kind switch
        {
            "resident" => AiIdentity.TryGet(bc, out var seated) ? seated : Guid.Empty,
            "named"    => bc is INamedNpc n ? n.AiId : Guid.Empty,
            _          => AiIdentity.GetOrCreate(bc)
        };
        var role = TownsfolkTalk.RoleOf(bc);

        // One odd name must not sink the whole document: the brain refuses a document whole (design §4.4).
        if (id == Guid.Empty || bc.Name == null || !_plain.IsMatch(bc.Name) || !_plain.IsMatch(role) || !_plain.IsMatch(town))
        {
            if (_warned.Add(bc.Serial))
            {
                logger.Warning("Roster: {Name} ({Serial}) is left off the roster - no id, or a name, role or town the roster cannot carry", bc.Name, bc.Serial);
            }

            return;
        }

        if (!towns.TryGetValue((map.Name, town), out var people))
        {
            towns[(map.Name, town)] = people = new SortedDictionary<string, Person>(StringComparer.Ordinal);
        }

        // Home is given only when it is a real point on the town's own map (design §4.7).
        people[id.ToString()] = new Person(
            id.ToString(), bc.Name, role, bc.Female ? "woman" : "man", kind, hasHome && map == bc.Map ? bc.Home : null
        );
    }

    private sealed record Wire(long Epoch, long Rev, string Hash, string Content, bool Probe);

    private sealed record Reply(bool Ok, string RosterHeld, string Error);

    // Every 5 s on the game thread. One request in flight, strictly one after another (design §4.3).
    private static void Tick()
    {
        if (!Enabled || _inFlight || Core.TickCount < _retryAt || !Usable)
        {
            return;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var (content, hash) = Build();
        LastBuildMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (hash != _lastHash)
        {
            _rev++;
            _lastHash = hash;
        }

        var full = hash != _ackedHash;

        if (!full && Core.TickCount < _probeAt)
        {
            return;
        }

        _inFlight = true;
        _ = SendAsync(content, hash, full);
    }

    // Awaits resume on the game thread (ModernUO's EventLoopContext), so state is only touched there.
    private static async Task SendAsync(string content, string hash, bool full)
    {
        try
        {
            // A probe asks only "which roster do you hold?" - how a brain restart or a lost database is noticed (§4.5a).
            var response = await _http.PostAsJsonAsync(
                "v1/roster", new Wire(AiLedger.Epoch, _rev, hash, full ? content : null, !full), _json
            );

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // An older brain, or its roster switch is off: wire compatibility only (design §4.6). Ask again later.
                _ackedHash = null;
                _retryAt = Core.TickCount + 300_000;
                return;
            }

            var reply = await response.Content.ReadFromJsonAsync<Reply>(_json);

            if (reply?.RosterHeld == hash)
            {
                if (_ackedHash != hash)
                {
                    logger.Information(
                        "Roster: the brain holds revision {Rev} ({Hash}); {Bytes} bytes, built in {Ms:F1} ms",
                        _rev, hash[..12], content.Length, LastBuildMs
                    );
                }

                _ackedHash = hash;
                _probeAt = Core.TickCount + 30_000;
                return;
            }

            _ackedHash = null; // it holds something else, or nothing: send the whole document next

            if (full)
            {
                logger.Warning("Roster: the brain did not take revision {Rev} - {Error}", _rev, reply?.Error ?? $"HTTP {(int)response.StatusCode}");
                _retryAt = Core.TickCount + 30_000;
            }
        }
        catch (Exception e)
        {
            _ackedHash = null;
            _retryAt = Core.TickCount + 15_000;
            logger.Debug("Roster: send failed - {Error}", e.Message);
        }
        finally
        {
            _inFlight = false;
        }
    }

    // ---- Tests only.
    public static void ResetForTests(bool enabled, HttpMessageHandler handler = null)
    {
        Enabled = enabled;
        _inFlight = false;
        _ackedHash = null;
        _lastHash = null;
        _rev = 0;
        _retryAt = 0;
        _probeAt = 0;
        _others = [];
        _othersAt = long.MinValue;
        _warned.Clear();
        _http = handler == null
            ? null
            : new HttpClient(handler) { BaseAddress = new Uri("http://brain.test/"), Timeout = TimeSpan.FromSeconds(10) };
    }

    public static Task TickForTests()
    {
        if (!Enabled || _inFlight || !Usable)
        {
            return Task.CompletedTask;
        }

        var (content, hash) = Build();

        if (hash != _lastHash)
        {
            _rev++;
            _lastHash = hash;
        }

        _inFlight = true;
        return SendAsync(content, hash, hash != _ackedHash);
    }
}
