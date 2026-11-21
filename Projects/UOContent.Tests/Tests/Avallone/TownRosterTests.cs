using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Server;
using Server.Avallone.AI;
using Server.Avallone.Townsfolk;
using Server.Engines.Spawners;
using Server.Mobiles;
using Server.Regions;
using Xunit;

namespace UOContent.Tests;

// Townsfolk step, Stage 1 - the roster, shard side (design §4 [v4]; build checklist §13 items 3 and 4).
// The brain's half of the wire contract is brain/test_townroster.py.
[Collection("Sequential UOContent Tests")]
public class TownRosterTests : IDisposable
{
    private static readonly Point3D Spot = new(5910, 110, 0);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roster-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<IEntity> _made = [];
    private readonly TownRegion _town;
    private readonly Brain _brain = new();

    public TownRosterTests()
    {
        Directory.CreateDirectory(_dir);
        _town = new TownRegion("Seatstown", Map.Felucca, 50, new Rectangle3D(5900, 100, -128, 50, 50, 256));
        _town.Register();

        Seats.ResetForTests(true, Path.Combine(_dir, "seat-register.json"));
        var reg = new SeatRegister { MigrationComplete = true };
        reg.Commit(Seats.RegisterPath);
        Seats.LoadForTests([], new SeatReconciler.SaveStamp(true, reg.RegisterId, reg.Counter));
        TownRoster.ResetForTests(true, _brain);
    }

    public void Dispose()
    {
        foreach (var e in _made)
        {
            if (!e.Deleted)
            {
                e.Delete();
            }
        }

        TownRoster.ResetForTests(false);
        Seats.ResetForTests(false, null);
        _town.Unregister();

        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // a leftover temp folder is harmless
        }
    }

    // A stand-in for the brain's /v1/roster: it holds what it is sent, as the real one does.
    private sealed class Brain : HttpMessageHandler
    {
        public readonly List<JsonElement> Seen = [];
        public string Held;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public bool Refuse;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token)).RootElement.Clone();
            Seen.Add(body);

            if (Status != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(Status) { Content = new StringContent("{\"ok\":false,\"error\":\"not found\"}") };
            }

            if (!body.GetProperty("probe").GetBoolean() && !Refuse)
            {
                Held = body.GetProperty("hash").GetString();
            }

            var held = Held == null ? "null" : $"\"{Held}\"";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"ok\":{(Refuse ? "false" : "true")},\"roster_held\":{held},\"error\":{(Refuse ? "\"refused\"" : "null")}}}", Encoding.UTF8, "application/json")
            };
        }
    }

    private Spawner NewSpawner()
    {
        var sp = new Spawner(3, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), 0, default, "Tailor");
        sp.MoveToWorld(Spot, Map.Felucca);
        _made.Add(sp);
        return sp;
    }

    private static readonly string[] _names = ["Mady", "Garrick", "Anna", "Bryn", "Corwin", "Dara"];
    private int _named;

    // The test environment has no name lists, so a new vendor has an empty name - and a nameless NPC is, rightly, left
    // off the roster. Each test NPC is given a plain name before it is offered for adoption.
    private Tailor SpawnTailor(Spawner sp, Point3D? at = null, Map map = null)
    {
        var t = new Tailor { Name = _names[_named++ % _names.Length] };
        t.MoveToWorld(at ?? Spot, map ?? Map.Felucca);
        t.Home = at ?? Spot;
        t.HomeMap = map ?? Map.Felucca;
        t.Spawner = sp;
        _made.Add(t);
        Seats.OnSpawned(sp, t);
        return t;
    }

    private static JsonElement Doc(string content) => JsonDocument.Parse(content).RootElement.Clone();

    private static JsonElement? Find(string content, Mobile m)
    {
        AiIdentity.TryGet(m, out var id);

        foreach (var town in Doc(content).GetProperty("towns").EnumerateArray())
        {
            foreach (var p in town.GetProperty("people").EnumerateArray())
            {
                if (p.GetProperty("id").GetString() == id.ToString())
                {
                    return p;
                }
            }
        }

        return null;
    }

    [Fact]
    public void Build_ListsASeatedResidentUnderItsTown_AndTheHashIsOfTheContentItself()
    {
        var t = SpawnTailor(NewSpawner());

        var (content, hash) = TownRoster.Build();

        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(), hash);

        var towns = Doc(content).GetProperty("towns");
        JsonElement? mine = null;
        foreach (var town in towns.EnumerateArray())
        {
            if (town.GetProperty("town").GetString() == "Seatstown")
            {
                mine = town;
            }
        }

        Assert.NotNull(mine);
        Assert.Equal("Felucca", mine.Value.GetProperty("map").GetString());

        var p = Find(content, t);
        Assert.NotNull(p);
        Assert.Equal(t.Name, p.Value.GetProperty("name").GetString());
        Assert.Equal("tailor", p.Value.GetProperty("role").GetString());
        Assert.Equal(t.Female ? "woman" : "man", p.Value.GetProperty("sex").GetString());
        Assert.Equal("resident", p.Value.GetProperty("kind").GetString());
        Assert.Equal(Spot.X, p.Value.GetProperty("home").GetProperty("x").GetInt32());
        Assert.Equal(Spot.Y, p.Value.GetProperty("home").GetProperty("y").GetInt32());
    }

    [Fact]
    public void Build_IsCanonical_TheSameWorldGivesTheSameBytes()
    {
        var sp = NewSpawner();
        SpawnTailor(sp);
        SpawnTailor(sp);

        Assert.Equal(TownRoster.Build(), TownRoster.Build());
    }

    [Fact]
    public void NotListed_AnNpcOutsideAnyTown_AndAQuarantinedOne()
    {
        var sp = NewSpawner();
        var inTown = SpawnTailor(sp);
        var nowhere = SpawnTailor(sp, new Point3D(5700, 300, 0));

        var early = new Tailor { Name = "Early" };
        early.MoveToWorld(Spot, Map.Felucca);
        early.Home = Spot;
        early.HomeMap = Map.Felucca;
        early.Spawner = sp;
        _made.Add(early);
        AiIdentity.GetOrCreate(early); // it has an id before adoption -> conflict, quarantined
        Seats.OnSpawned(sp, early);
        Assert.True(Seats.IsQuarantined(early));

        var content = TownRoster.Build().Content;

        Assert.NotNull(Find(content, inTown));
        Assert.Null(Find(content, nowhere));
        Assert.DoesNotContain("\"Early\"", content);

        // A nameless NPC is left off rather than sinking the whole document at the brain.
        var nameless = SpawnTailor(sp);
        nameless.Name = "";
        Assert.Null(Find(TownRoster.Build().Content, nameless));
        var count = 0;
        foreach (var town in Doc(content).GetProperty("towns").EnumerateArray())
        {
            if (town.GetProperty("town").GetString() == "Seatstown")
            {
                count = town.GetProperty("people").GetArrayLength();
            }
        }

        Assert.Equal(1, count);
    }

    [Fact]
    public void Named_ANamedNpcIsListedUnderTheIdItSpeaksWith()
    {
        var m = new Mirabel { Name = "Mirabel Ashgrove" };
        m.MoveToWorld(Spot, Map.Felucca);
        m.Home = Spot;
        m.HomeMap = Map.Felucca;
        _made.Add(m);

        TownRoster.ResetForTests(true, _brain); // a fresh scan for hand-placed and named NPCs
        var content = TownRoster.Build().Content;

        var named = ((Server.Avallone.Named.INamedNpc)m).AiId;
        Assert.NotEqual(Guid.Empty, named);
        Assert.Contains($"\"id\":\"{named}\"", content);
        Assert.Contains("\"kind\":\"named\"", content);
        Assert.False(Seats.TryGetSeat(m, out _)); // a named NPC never has a seat, so never a generated family
    }

    [Fact]
    public void Stamp_IsLeftOutWhenTheSwitchIsOff_AndIsNoneWhenTheSeatsCannotVouch()
    {
        SpawnTailor(NewSpawner());
        Assert.Matches("^[0-9a-f]{64}$", TownRoster.StampForRequest());

        TownRoster.ResetForTests(false);
        Assert.Null(TownRoster.StampForRequest());

        TownRoster.ResetForTests(true, _brain);
        Seats.ResetForTests(true, Seats.RegisterPath); // enabled but not yet reconciled: nothing seatable is exposed
        Assert.Equal(TownRoster.None, TownRoster.StampForRequest());
        Assert.DoesNotMatch("^[0-9a-f]{64}$", TownRoster.None); // "none" can never be taken for a hash
    }

    [Fact]
    public void Stamp_ChangesTheInstantAListedFactChanges() // R2-D2A: no window in which the old stamp is still carried
    {
        var sp = NewSpawner();
        var a = SpawnTailor(sp);
        var s0 = TownRoster.StampForRequest();

        a.Name = a.Name + "a";
        var s1 = TownRoster.StampForRequest();
        Assert.NotEqual(s0, s1);

        var b = SpawnTailor(sp);
        var s2 = TownRoster.StampForRequest();
        Assert.NotEqual(s1, s2);

        b.Delete();
        var s3 = TownRoster.StampForRequest();
        Assert.NotEqual(s2, s3);
        Assert.Equal(s1, s3); // the same world as before b existed is the same document

        a.Home = new Point3D(Spot.X + 2, Spot.Y, 0);
        Assert.NotEqual(s3, TownRoster.StampForRequest());
    }

    [Fact]
    public void Request_EveryPromptBodyCarriesTheStamp()
    {
        SpawnTailor(NewSpawner());
        var stamp = TownRoster.StampForRequest();
        var request = new AiRequest(Guid.NewGuid(), Guid.NewGuid(), "persona", "hello", AiPriority.Conversation);

        foreach (var mode in new[] { null, "plan", "summarize", "exchange" })
        {
            var json = SidecarAiClient.BodyJson(request with { Mode = mode }, TimeSpan.FromSeconds(4));
            Assert.Contains($"\"roster_stamp\":\"{stamp}\"", json);
        }

        TownRoster.ResetForTests(false);
        Assert.DoesNotContain("roster_stamp", SidecarAiClient.BodyJson(request, TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public async Task Send_TheWholeDocumentFirst_ThenOnlyProbes_ThenTheWholeDocumentAgainAfterAChange()
    {
        var sp = NewSpawner();
        SpawnTailor(sp);

        await TownRoster.TickForTests();

        var first = Assert.Single(_brain.Seen);
        Assert.False(first.GetProperty("probe").GetBoolean());
        var (content, hash) = TownRoster.Build();
        Assert.Equal(content, first.GetProperty("content").GetString()); // the bytes that were hashed, as a string
        Assert.Equal(hash, first.GetProperty("hash").GetString());
        Assert.Equal(hash, TownRoster.AckedHash);
        Assert.Equal(1, TownRoster.Rev);

        await TownRoster.TickForTests(); // nothing changed: ask only what is held
        Assert.Equal(2, _brain.Seen.Count);
        Assert.True(_brain.Seen[1].GetProperty("probe").GetBoolean());
        Assert.Equal(JsonValueKind.Null, _brain.Seen[1].GetProperty("content").ValueKind);
        Assert.Equal(1, TownRoster.Rev);

        SpawnTailor(sp); // the world changed
        await TownRoster.TickForTests();
        Assert.False(_brain.Seen[2].GetProperty("probe").GetBoolean());
        Assert.Equal(2, TownRoster.Rev);
        Assert.Equal(TownRoster.Build().Hash, TownRoster.AckedHash);
    }

    [Fact]
    public async Task Send_ABrainThatLostItsRoster_IsSentTheWholeDocumentAgain() // R2-D2B: no ledger session needed
    {
        SpawnTailor(NewSpawner());
        await TownRoster.TickForTests();
        Assert.NotNull(TownRoster.AckedHash);

        _brain.Held = null; // the brain restarted on an empty database
        await TownRoster.TickForTests(); // the probe finds out
        Assert.Null(TownRoster.AckedHash);

        await TownRoster.TickForTests();
        Assert.False(_brain.Seen[^1].GetProperty("probe").GetBoolean());
        Assert.Equal(TownRoster.Build().Hash, _brain.Held);
        Assert.Equal(_brain.Held, TownRoster.AckedHash);
    }

    [Fact]
    public async Task Send_AnOlderBrainAnswers404_NothingIsAcknowledged_AndARefusalIsNotAnAcknowledgement()
    {
        SpawnTailor(NewSpawner());

        _brain.Status = HttpStatusCode.NotFound;
        await TownRoster.TickForTests();
        Assert.Null(TownRoster.AckedHash);

        _brain.Status = HttpStatusCode.OK;
        _brain.Refuse = true;
        await TownRoster.TickForTests();
        Assert.Null(TownRoster.AckedHash);

        _brain.Refuse = false;
        await TownRoster.TickForTests();
        Assert.Equal(TownRoster.Build().Hash, TownRoster.AckedHash);
    }
}
