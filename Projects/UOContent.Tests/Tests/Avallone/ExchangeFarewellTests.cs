using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Server;
using Server.Avallone.AI;
using Server.Avallone.Named;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// 2026-11-15 (the owner, chat, during the townsfolk Stage 1 play test)
// A reply that answers the player's goodbye starts nothing for
// their companion. Every case has a control showing that the same reply to an ordinary line does start an exchange.
[Collection("Sequential UOContent Tests")]
public class ExchangeFarewellTests : IDisposable
{
    private const string Reply = "Good day to you. And you, Elspeth - you look road-weary.";
    private const string Gossip = "They say Zorvald Quenby slew a wyvern near Shame. And you, Elspeth - good day to you.";
    private static readonly Point3D Here = new(2180, 1540, 0);

    private readonly List<Mobile> _made = [];
    private readonly List<(Mobile Who, string Text)> _said = [];
    private readonly Client _client = new();
    private readonly IAiNpcClient _oldClient = AiGateway.Client;
    private long _now = 1_000_000;

    private sealed class Client : IAiNpcClient
    {
        public int Calls;
        public string Name => "farewell-tests";

        public Task<AiReply> SendAsync(AiRequest request, TimeSpan budget, CancellationToken token)
        {
            if (request.Mode == "exchange")
            {
                Calls++;
            }

            return Task.FromResult(AiReply.Success("A line.", "test"));
        }

        public Task WarmAsync(CancellationToken token) => Task.CompletedTask;
    }

    public ExchangeFarewellTests()
    {
        Server.Avallone.Townsfolk.Seats.ResetForTests(false, null);
        AiGateway.ResetForTests();
        AiGateway.Client = _client;
        AiGateway.SetEnabled(true, false);
        AiGateway.Clock = () => _now;
        NamedNpcs.ResetForTests(false);
        TravelParty.ResetForTests();
        Exchange.ResetForTests(true);
        Exchange.Pause = _ => Task.CompletedTask;
        Exchange.PickLength = () => 3;
        Exchange.Speak = (m, t) => _said.Add((m, t));
        Exchange.Online = _ => true;
    }

    public void Dispose()
    {
        Exchange.ResetForTests(false);
        NamedNpcs.ResetForTests(false);
        AiGateway.ResetForTests();
        AiGateway.SetEnabled(false, false);
        AiGateway.Client = _oldClient;
        AiGateway.Clock = () => Core.TickCount;

        foreach (var m in _made)
        {
            if (!m.Deleted)
            {
                m.Delete();
            }
        }
    }

    private PlayerMobile Player(string name)
    {
        var m = new PlayerMobile { Name = name };
        m.DefaultMobileInit();
        m.MoveToWorld(Here, Map.Felucca);
        _made.Add(m);
        return m;
    }

    private Provisioner Vendor(string name)
    {
        var v = new Provisioner { Name = name };
        v.MoveToWorld(new Point3D(Here.X - 1, Here.Y, Here.Z), Map.Felucca);
        _made.Add(v);
        return v;
    }

    private void Companion(Mobile owner)
    {
        var e = new ElspethVale();
        e.MoveToWorld(new Point3D(Here.X + 1, Here.Y, Here.Z), Map.Felucca);
        e.SetControlMaster(owner);
        _made.Add(e);
    }

    // The player says `line`; the NPC's reply to THAT line is then said. True when an exchange (or reaction) started.
    private bool SayAndGetReply(Mobile npc, PlayerMobile p, string line, string spoken = Reply, bool gossip = false, int? sort = null)
    {
        Exchange.OnSpeech(new SpeechEventArgs(p, line, MessageType.Regular, 0x3B2, []));
        var opening = Exchange.RevisionOf(p); // read when the turn begins, as TownsfolkTalk and Mirabel do
        var before = Exchange.Started;
        Exchange.AfterReply(npc, p, TravelParty.For(npc, p), spoken, gossip, opening, sort);
        return Exchange.Started > before;
    }

    [Theory]
    [InlineData("farewell")]
    [InlineData("Goodbye!")]
    [InlineData("Well, bye")]
    [InlineData("fare thee well")]
    public void AReplyToAFarewell_StartsNothingForTheCompanion(string goodbye)
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        Assert.True(Conversation.IsFarewell(goodbye), "not a farewell by the game's own rule - the test would prove nothing");

        Assert.False(SayAndGetReply(osric, p, goodbye));

        Assert.Empty(_said);
        Assert.Equal(0, _client.Calls); // and the model was not asked for a line
        Assert.False(Exchange.Running);
    }

    [Fact]
    public void TheSameReplyToAnOrdinaryLine_DoesStartAnExchange() // the control
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        Assert.False(Conversation.IsFarewell("Good day, how is trade?"));

        Assert.True(SayAndGetReply(osric, p, "Good day, how is trade?"));

        Assert.Equal(3, _said.Count);
    }

    [Fact]
    public void AfterAFarewell_TheNextOrdinaryLineIsNotHeldBack()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        var mady = Vendor("Mady");
        Companion(p);

        Assert.False(SayAndGetReply(osric, p, "farewell"));
        Assert.True(SayAndGetReply(mady, p, "Hello there, what do you sell?")); // a new conversation, a new NPC

        Assert.Equal(3, _said.Count);
    }

    [Fact]
    public void AGossipReplyToAFarewell_GetsNoReactionEither()
    {
        Exchange.SetReactionsForTests(true);
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);

        Assert.False(SayAndGetReply(osric, p, "goodbye", Gossip, gossip: true, sort: 0));
        Assert.Empty(_said);

        // The control: the same gossip reply to an ordinary line does get her one-line reaction.
        Assert.True(SayAndGetReply(osric, p, "any news?", Gossip, gossip: true, sort: 0));
        Assert.Single(_said);
    }

    [Fact]
    public void WithTheSwitchOn_ItIsAsBefore()
    {
        Exchange.SetAfterFarewellForTests(true);
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);

        Assert.True(SayAndGetReply(osric, p, "farewell"));

        Assert.Equal(3, _said.Count);
    }

    [Fact]
    public void AFarewellCountsAsAnOrdinaryReplyInBetween_ItNeitherResetsNorBlocksTheSpacingRule()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        var mady = Vendor("Mady");
        var tam = Vendor("Tam");
        Companion(p);

        Assert.True(SayAndGetReply(osric, p, "hello"));          // an exchange: the spacing count starts again
        _said.Clear();
        Assert.False(SayAndGetReply(mady, p, "how goes it?"));   // 1 reply in between: too soon
        Assert.False(SayAndGetReply(mady, p, "farewell"));       // 2 - a farewell: nothing starts, but it counts
        Assert.True(SayAndGetReply(tam, p, "good morning"));     // the rule is satisfied by the two replies in between
    }
}
