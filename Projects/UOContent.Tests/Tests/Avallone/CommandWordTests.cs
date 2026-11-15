using System.Collections.Generic;
using Server;
using Server.Avallone.AI;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// Step 2e (design §8.8; the owner, chat, 2026-11-12: /; Codex design R1 Q7, R6 = GO).
// E1: "Mady, any news?" beside a town crier went unanswered in the owner's demo - a line that OPENS with one townsperson's name
// is that townsperson's, while a bare "news?" stays the crier's. E2: "where is the bank from here?" went unanswered -
// bank words are a game command only with a banker within 12 tiles (the stock banker's own range).
[Collection("Sequential UOContent Tests")]
public class CommandWordTests : System.IDisposable
{
    private const int X = 3100, Y = 1600;
    private readonly List<Mobile> _made = [];

    public void Dispose()
    {
        foreach (var m in _made)
        {
            if (!m.Deleted)
            {
                m.Delete();
            }
        }

        GameCommands.BankWordsNeedBanker = true;
        TownsfolkTalk.NamedNewsBeatsCrier = true;
    }

    private T At<T>(T m, int dx) where T : Mobile
    {
        m.MoveToWorld(new Point3D(X + dx, Y, 0), Map.Felucca);
        _made.Add(m);
        return m;
    }

    private PlayerMobile Player()
    {
        var p = new PlayerMobile(World.NewMobile);
        p.DefaultMobileInit();
        return At(p, 0);
    }

    [Theory]
    [InlineData("Mady, any news?", false)]            // her line, crier or not
    [InlineData("hail Mady, any news?", false)]
    [InlineData("Maddy, any news?", false)]           // the resolver's near spelling, close by
    [InlineData("any news?", true)]                   // bare news: the crier's
    [InlineData("any news, Mady?", true)]             // her name not first: unchanged
    [InlineData("Bob, any news?", true)]              // nobody here by that name
    public void NamedNewsBesideACrier(string text, bool isCommand)
    {
        var p = Player();
        At(new TownCrier { Name = "Ayana" }, 3);
        At(new Provisioner { Name = "Mady" }, 2);
        Assert.Equal(isCommand, TownsfolkTalk.IsGameCommand(p, [0x30], text));
    }

    // Code R1-4 / R2: at the crier's own speech handler. It stays silent ONLY for the speech event the townsfolk AI took;
    // a line the AI did not take - AI off, a yell, any refusal - still gets the local news.
    private SpeechEventArgs Line(PlayerMobile p, string text, MessageType type = MessageType.Regular) =>
        new(p, text, type, 0x3B2, [0x30]);

    private static bool CrierAnswers(TownCrier crier, SpeechEventArgs e)
    {
        var before = TownCrier.NewsAnsweredForTests;
        crier.OnSpeech(e);
        return TownCrier.NewsAnsweredForTests > before;
    }

    [Fact]
    public void TheCrierIsSilentOnlyForTheEventTheAiTook()
    {
        var p = Player();
        var crier = At(new TownCrier { Name = "Ayana" }, 3);
        At(new Provisioner { Name = "Mady" }, 2);
        var taken = Line(p, "Mady, any news?");
        TownsfolkTalk.ClaimForTests(taken);
        Assert.False(CrierAnswers(crier, taken));                         // the AI answers this one
        Assert.True(CrierAnswers(crier, Line(p, "Mady, any news?")));    // a later, untaken line: local news again
        Assert.True(CrierAnswers(crier, Line(p, "any news?")));
    }

    [Fact]
    public void LocalNewsSurvivesWhenTheAiDoesNotTakeTheLine()
    {
        var p = Player();
        var crier = At(new TownCrier { Name = "Ayana" }, 3);
        At(new Provisioner { Name = "Mady" }, 2);
        var yelled = Line(p, "Mady, any news?", MessageType.Yell);         // townsfolk AI hears regular speech only
        TownsfolkTalk.OnSpeechForTests(yelled);
        Assert.True(CrierAnswers(crier, yelled));
        var regular = Line(p, "Mady, any news?");                        // the AI gateway is off in tests: not taken
        TownsfolkTalk.OnSpeechForTests(regular);
        Assert.False(TownsfolkTalk.ClaimedNamedNews(regular));
        Assert.True(CrierAnswers(crier, regular));
    }

    // Code R3: the townsperson took the line but its reply never came - the stock news is given after all, once.
    private sealed class Refuser : IAiNpcClient
    {
        public string Name => "refuse";
        public System.Threading.Tasks.Task<AiReply> SendAsync(AiRequest request, System.TimeSpan budget,
            System.Threading.CancellationToken token) => System.Threading.Tasks.Task.FromResult(AiReply.Fail(AiOutcome.Busy, "busy"));
        public System.Threading.Tasks.Task WarmAsync(System.Threading.CancellationToken token) => System.Threading.Tasks.Task.CompletedTask;
    }

    [Fact]
    public void TheFallbackReleasesTheClaimAndGivesLocalNewsOnce()
    {
        var p = Player();
        var crier = At(new TownCrier { Name = "Ayana" }, 3);
        At(new Provisioner { Name = "Mady" }, 2);
        var e = Line(p, "Mady, any news?");
        TownsfolkTalk.ClaimForTests(e);
        var before = TownCrier.NewsAnsweredForTests;
        TownsfolkTalk.NewsFallback(e);
        Assert.Equal(before + 1, TownCrier.NewsAnsweredForTests);
        Assert.False(TownsfolkTalk.ClaimedNamedNews(e));
        Assert.False(CrierAnswers(crier, e));                   // code R4: the SAME event is not answered twice
        Assert.True(CrierAnswers(crier, Line(p, "any news?")));  // a new line still is
    }

    // Code R4: the whole sequence in the engine's order - the AI takes the line, its request is refused at once, the
    // fallback answers, THEN the engine hands the same event to the native listener. Exactly one answer, each type,
    // with an empty news list (no timer to hide a second "I have no news").
    [Fact]
    public async System.Threading.Tasks.Task ImmediateRefusalThenNativeDispatchAnswersOnce_Crier()
    {
        var p = Player();
        var crier = At(new TownCrier { Name = "Ayana" }, 3);
        var mady = At(new Provisioner { Name = "Mady" }, 2);
        var e = Line(p, "Mady, any news?");
        TownsfolkTalk.ClaimForTests(e);
        var old = AiGateway.Client;
        AiGateway.Client = new Refuser();
        try
        {
            var before = TownCrier.NewsAnsweredForTests;
            await TownsfolkTalk.RespondForTests(mady, p, "Mady, any news?", e);   // fallback runs
            crier.OnSpeech(e);                                                   // the engine's dispatch, afterwards
            Assert.Equal(before + 1, TownCrier.NewsAnsweredForTests);
        }
        finally
        {
            AiGateway.Client = old;
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task ImmediateRefusalThenNativeDispatchAnswersOnce_Barkeeper()
    {
        var p = Player();
        var keeper = At(new PlayerBarkeeper(p, null) { Name = "Bart" }, 1);
        var mady = At(new Provisioner { Name = "Mady" }, 2);
        var e = Line(p, "Mady, any news?");
        TownsfolkTalk.ClaimForTests(e);
        var old = AiGateway.Client;
        AiGateway.Client = new Refuser();
        try
        {
            var before = PlayerBarkeeper.NewsAnsweredForTests;
            await TownsfolkTalk.RespondForTests(mady, p, "Mady, any news?", e);
            keeper.OnSpeech(e);
            Assert.Equal(before + 1, PlayerBarkeeper.NewsAnsweredForTests);
        }
        finally
        {
            AiGateway.Client = old;
        }
    }

    [Fact]
    public void DelayedFailureAnswersOnce_SuccessNever()
    {
        var p = Player();
        var crier = At(new TownCrier { Name = "Ayana" }, 3);
        At(new Provisioner { Name = "Mady" }, 2);
        var late = Line(p, "Mady, any news?");
        TownsfolkTalk.ClaimForTests(late);
        var before = TownCrier.NewsAnsweredForTests;
        Assert.False(CrierAnswers(crier, late));                 // native dispatch first: the AI holds it
        TownsfolkTalk.NewsFallback(late);                        // the reply failed later
        Assert.Equal(before + 1, TownCrier.NewsAnsweredForTests);
        var ok = Line(p, "Mady, any news?");
        TownsfolkTalk.ClaimForTests(ok);                         // the AI answers this one: no fallback, no native answer
        Assert.False(CrierAnswers(crier, ok));
        Assert.Equal(before + 1, TownCrier.NewsAnsweredForTests);
    }

    [Fact]
    public async System.Threading.Tasks.Task AClaimedLineWhoseReplyFailsStillGetsLocalNews()
    {
        var p = Player();
        At(new TownCrier { Name = "Ayana" }, 3);
        var mady = At(new Provisioner { Name = "Mady" }, 2);
        var e = Line(p, "Mady, any news?");
        TownsfolkTalk.ClaimForTests(e);
        var old = AiGateway.Client;
        AiGateway.Client = new Refuser();                       // whether the gateway is on (refuses) or off: no reply
        try
        {
            var before = (TownCrier.NewsAnsweredForTests, TownsfolkTalk.NewsFallbacks);
            await TownsfolkTalk.RespondForTests(mady, p, "Mady, any news?", e);
            Assert.Equal(before.NewsAnsweredForTests + 1, TownCrier.NewsAnsweredForTests);
            Assert.Equal(before.NewsFallbacks + 1, TownsfolkTalk.NewsFallbacks);
            Assert.False(TownsfolkTalk.ClaimedNamedNews(e));
        }
        finally
        {
            AiGateway.Client = old;
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task AnUnclaimedReplyNeverTriggersTheFallback()
    {
        var p = Player();
        At(new TownCrier { Name = "Ayana" }, 3);
        var mady = At(new Provisioner { Name = "Mady" }, 2);
        var old = AiGateway.Client;
        AiGateway.Client = new Refuser();
        try
        {
            var before = TownsfolkTalk.NewsFallbacks;
            await TownsfolkTalk.RespondForTests(mady, p, "Mady, how are you?", null);
            Assert.Equal(before, TownsfolkTalk.NewsFallbacks);
        }
        finally
        {
            AiGateway.Client = old;
        }
    }

    // the owner 2026-11-13: the crier hears news within avallone.crierNewsRange (default 5), not stock 12; the AI's
    // "crier close by" check uses the same range; a player barkeeper owns "news" only within its own 3 tiles.
    [Theory]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void TheCrierHearsNewsOnlyWithinItsRange(int at, bool crierAnswers)
    {
        var p = Player();
        var crier = At(new TownCrier { Name = "Ayana" }, at);
        At(new Provisioner { Name = "Mady" }, 2);
        Assert.Equal(crierAnswers, CrierAnswers(crier, Line(p, "any news?")));
        Assert.Equal(crierAnswers, TownsfolkTalk.IsGameCommand(p, [0x30], "any news?"));   // beyond it, the AI answers
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void ABarkeeperOwnsNewsOnlyWhereItAnswers(int at, bool isCommand)
    {
        var p = Player();
        At(new PlayerBarkeeper(p, null) { Name = "Bart" }, at);
        Assert.Equal(isCommand, TownsfolkTalk.IsGameCommand(p, [0x30], "any news?"));
    }

    [Fact]
    public void NamedNewsSwitchOffIsTheOldRule()
    {
        TownsfolkTalk.NamedNewsBeatsCrier = false;
        var p = Player();
        At(new TownCrier { Name = "Ayana" }, 3);
        At(new Provisioner { Name = "Mady" }, 2);
        Assert.True(TownsfolkTalk.IsGameCommand(p, [0x30], "Mady, any news?"));
    }

    [Fact]
    public void NewsWithNoCrierOrBarkeeperIsTalk()
    {
        var p = Player();
        At(new Provisioner { Name = "Mady" }, 2);
        Assert.False(TownsfolkTalk.IsGameCommand(p, [0x30], "any news?"));
    }

    [Theory]
    [InlineData(-1, false)]   // no banker at all
    [InlineData(12, true)]    // within the banker's 12 tiles
    [InlineData(13, false)]   // just out of it
    public void BankWordsNeedABankerInRange(int bankerAt, bool isCommand)
    {
        var p = Player();
        if (bankerAt >= 0)
        {
            At(new Banker(), bankerAt);
        }

        Assert.Equal(isCommand, TownsfolkTalk.IsGameCommand(p, [0x2], "where is the bank from here?"));
        Assert.Equal(isCommand, TownsfolkTalk.IsGameCommand(p, [0x0], "withdraw"));
    }

    [Fact]
    public void ABankerSubtypeCountsToo()
    {
        var p = Player();
        At(new Minter(), 10);
        Assert.True(TownsfolkTalk.IsGameCommand(p, [0x2], "bank"));
    }

    [Fact]
    public void BankSwitchOffIsTheOldRule()
    {
        GameCommands.BankWordsNeedBanker = false;
        var p = Player();
        Assert.True(TownsfolkTalk.IsGameCommand(p, [0x2], "where is the bank from here?"));
    }

    [Fact]
    public void OtherCommandsInAMixedLineStillWin()
    {
        var p = Player();
        Assert.True(TownsfolkTalk.IsGameCommand(p, [0x2, 0x6C], "train me, then where is the bank"));   // train: always
    }
}
