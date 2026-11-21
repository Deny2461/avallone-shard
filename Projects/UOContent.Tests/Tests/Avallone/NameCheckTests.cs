using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Server;
using Server.Avallone.AI;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// Stage 2 repair, decision 9 (the owner, chat, 2026-11-17): an ordinary townsfolk
// request carries the speaking player's own name and the player roster, so the brain can keep an off-stage relative
// from sharing the name of the player standing there. For the brain's name checks only.
[Collection("Sequential UOContent Tests")]
public class NameCheckTests : IDisposable
{
    private const int X = 3140, Y = 1600;
    private readonly List<Mobile> _made = [];
    private readonly IAiNpcClient _old = AiGateway.Client;

    private sealed class Catcher : IAiNpcClient
    {
        public readonly List<AiRequest> Seen = [];
        public string Name => "catch";

        public Task<AiReply> SendAsync(AiRequest request, TimeSpan budget, CancellationToken token)
        {
            lock (Seen)
            {
                Seen.Add(request);
            }

            return Task.FromResult(AiReply.Fail(AiOutcome.Failed, "test"));
        }

        public Task WarmAsync(CancellationToken token) => Task.CompletedTask;
    }

    public NameCheckTests()
    {
        AiGateway.ResetForTests();
        AiGateway.SetEnabled(true, false);
        Server.Avallone.Rumours.RumourSpeech.ResetRosterForTests();
    }

    public void Dispose()
    {
        foreach (var m in _made)
        {
            if (!m.Deleted)
            {
                m.Delete();
            }
        }

        AiGateway.ResetForTests();
        AiGateway.SetEnabled(false, false);
        AiGateway.Client = _old;
        Server.Avallone.Rumours.RumourSpeech.ResetRosterForTests();
    }

    private T At<T>(T m, int dx) where T : Mobile
    {
        m.MoveToWorld(new Point3D(X + dx, Y, 0), Map.Felucca);
        _made.Add(m);
        return m;
    }

    private PlayerMobile Player(string name, int dx)
    {
        var p = new PlayerMobile { Name = name };      // the plain constructor: the player is in World.Mobiles
        p.DefaultMobileInit();
        return At(p, dx);
    }

    [Fact]
    public async Task AnOrdinaryTownsfolkTurnCarriesThePlayerAndTheRoster()
    {
        var p = Player("Corvina Testwell", 0);
        Player("Halvard Testmoor", 1);
        var mady = At(new Provisioner { Name = "Mady" }, 2);
        var catcher = new Catcher();
        AiGateway.Client = catcher;
        Server.Avallone.Rumours.RumourSpeech.ResetRosterForTests();     // the roster is cached 60 s: build it now
        Assert.Contains(Server.Avallone.Rumours.RumourSpeech.CurrentRoster(), e => e.Name == "Corvina Testwell");   // control

        await TownsfolkTalk.RespondForTests(mady, p, "Mady, how are you?", null);

        var sent = Assert.Single(catcher.Seen);
        Assert.NotNull(sent.NameCheck);
        Assert.Equal("Corvina Testwell", sent.NameCheck.Player);
        Assert.Contains("Corvina Testwell", sent.NameCheck.Roster);
        Assert.Contains("Halvard Testmoor", sent.NameCheck.Roster);   // another player in the world, not in the talk
        Assert.DoesNotContain("Mady", sent.NameCheck.Roster);          // townsfolk are not players

        using var doc = JsonDocument.Parse(SidecarAiClient.BodyJson(sent, TimeSpan.FromSeconds(4)));   // what really goes
        var wire = doc.RootElement.GetProperty("name_check");
        Assert.Equal("Corvina Testwell", wire.GetProperty("player").GetString());
        Assert.True(wire.GetProperty("roster").GetArrayLength() >= 2);
    }

    [Fact]
    public void OtherRequestsLeaveItOffTheWire()
    {
        var request = new AiRequest(Guid.NewGuid(), Guid.NewGuid(), "persona", "hello", AiPriority.Conversation);
        foreach (var mode in new[] { null, "plan", "summarize", "exchange" })
        {
            Assert.DoesNotContain("name_check", SidecarAiClient.BodyJson(request with { Mode = mode }, TimeSpan.FromSeconds(4)));
        }
    }

    [Fact]
    public void NoNameIsNeverANullName()
    {
        var ask = TownsfolkTalk.NameCheckFor(null);
        Assert.Equal("", ask.Player);
        Assert.NotNull(ask.Roster);
    }
}
