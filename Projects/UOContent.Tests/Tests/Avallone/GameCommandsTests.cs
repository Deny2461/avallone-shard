using Server.Avallone.AI;
using Xunit;

namespace UOContent.Tests;

public class GameCommandsTests
{
    // Keyword ids as the client sent them for these lines (shard log, 2026-09-28 to 2026-10-07).
    [Theory]
    [InlineData(new[] { 0x2 })]                  // bank
    [InlineData(new[] { 0x15D, 0x168 })]         // all kill
    [InlineData(new[] { 0xE8, 0x163, 0x16C })]   // all follow me
    [InlineData(new[] { 0x16F })]                // bob stay
    [InlineData(new[] { 0x25 })]                 // i wish to secure this
    [InlineData(new[] { 0x7 })]                  // guards
    [InlineData(new[] { 0x8 })]                  // stable
    [InlineData(new[] { 0x9 })]                  // claim
    [InlineData(new[] { 0x3 })]                  // check 750000
    [InlineData(new[] { 0x3C })]                 // vendor buy
    [InlineData(new[] { 0x2, 0x4 })]             // "... join me?bank"
    public void RealCommandsStayWithTheGame(int[] keywords) => Assert.True(GameCommands.AnyCommand(keywords));

    [Theory]
    [InlineData(new[] { 0x180 })]                // what about taming dragons
    [InlineData(new[] { 0xF7 })]                 // at least let me pay you for your skill
    [InlineData(new[] { 0x30, 0x180 })]          // what news?
    [InlineData(new[] { 0x4 })]                  // care to join me
    [InlineData(new[] { 0x9E })]                 // time to ride
    [InlineData(new[] { 0x134 })]                // the treasure
    [InlineData(new[] { 0x16 })]                 // you are a great help
    [InlineData(new[] { 0x12 })]                 // aye! now we're getting somewhere
    [InlineData(new[] { 0x3B })]                 // hello
    [InlineData(new[] { 0x1BE })]                // wait here
    [InlineData(new int[0])]
    [InlineData(null)]
    public void OrdinaryWordsAreTalk(int[] keywords) => Assert.False(GameCommands.AnyCommand(keywords));

    // Dev play test 4 (the owner, chat, 2026-10-30): was silent - a sentence that mentions buying is talk.
    [Theory]
    [InlineData("where can I buy a sword", new[] { 0x15, 0x13B, 0x171, 0x1E6 }, true)]
    [InlineData("Mirabel, where can I buy a sword", new[] { 0x15, 0x13B, 0x171 }, true)]
    [InlineData("can I buy a room?", new[] { 0x171 }, true)]
    [InlineData("can I buy a room", new[] { 0x171 }, true)]                 // the owner 2026-10-31: no needed
    [InlineData("Mirabel can I buy a room", new[] { 0x171 }, true)]
    [InlineData("do you sell bread", new[] { 0x177 }, true)]
    [InlineData("Zahra do you sell bread", new[] { 0x177 }, true)]
    [InlineData("who will buy my fish", new[] { 0x171 }, true)]
    // Codex 5c-1 code review finding 3: named, longer requests are still shop commands
    [InlineData("Mirabel I want to buy", new[] { 0x171 }, false)]
    [InlineData("Mirabel I would like to buy", new[] { 0x171 }, false)]
    [InlineData("Mirabel I have fish to sell", new[] { 0x177 }, false)]
    [InlineData("Zahra I want to sell some things", new[] { 0x177 }, false)]
    [InlineData("I would like to sell some fish to you", new[] { 0x177 }, false)]
    [InlineData("buy?", new[] { 0x171 }, false)]                             // a "?" alone does not make a question
    // Codex 5c-1 round 2 finding 4: a greeting or "please" in front does not hide the question
    [InlineData("Hello Mirabel can I buy a room", new[] { 0x171 }, true)]
    [InlineData("Mirabel please can I buy a room", new[] { 0x171 }, true)]
    [InlineData("Good evening Mirabel do you sell bread", new[] { 0x177 }, true)]
    [InlineData("hey zahra, do you sell bread", new[] { 0x177 }, true)]
    [InlineData("Hello Mirabel I want to buy", new[] { 0x171 }, false)]
    [InlineData("Mirabel buy please", new[] { 0x171 }, false)]
    [InlineData("buy", new[] { 0x171 }, false)]
    [InlineData("sell", new[] { 0x177 }, false)]
    [InlineData("vendor buy", new[] { 0x3C }, false)]
    [InlineData("Mirabel buy", new[] { 0x171 }, false)]
    [InlineData("I want to buy", new[] { 0x171 }, false)]
    [InlineData("where can I bank and buy?", new[] { 0x2, 0x171 }, false)]   // a real non-trade command: still the game's
    [InlineData("where is the forge", new[] { 0x15 }, false)]                 // no trade word at all
    public void BuyingSentencesAreTalk(string line, int[] keywords, bool talk) =>
        Assert.Equal(talk, GameCommands.OnlyTradeTalk(keywords, line));

    // Dev play test 2 (2026-10-31), fixed 2026-11-01: buying/selling statements that name nobody were silent - no shop
    // opens without a name and the AI stayed quiet. `named` = the line names someone nearby (NamesSomeoneNearby).
    [Theory]
    // the owner's silent lines: nameless statements are talk
    [InlineData("sell me some cheaper wine", new[] { 0x177 }, false, true)]
    [InlineData("sell it cheaper", new[] { 0x177 }, false, true)]
    [InlineData("sell me something cheap", new[] { 0x177 }, false, true)]
    [InlineData("I want to buy some cheap wine", new[] { 0x171 }, false, true)]
    [InlineData("I want to buy", new[] { 0x171 }, false, true)]
    [InlineData("I have fish to sell", new[] { 0x177 }, false, true)]
    [InlineData("Hello, I would like to buy a room please", new[] { 0x171 }, false, true)]
    // "sell me" / "sell it" is talk even when named - never the "sell to the vendor" window
    [InlineData("Mirabel, sell me some wine", new[] { 0x177 }, true, true)]
    [InlineData("Zahra sell it cheaper", new[] { 0x177 }, true, true)]
    // named lines stay shop commands (Codex 5c-1 code review finding 3)
    [InlineData("Mirabel I want to buy", new[] { 0x171 }, true, false)]
    [InlineData("mirable buy", new[] { 0x171 }, true, false)]
    [InlineData("I want to buy some wine, Mirabel", new[] { 0x171 }, true, false)]
    [InlineData("Zahra I want to sell some things", new[] { 0x177 }, true, false)]
    // the bare words and the vendor words stay the game's
    [InlineData("buy", new[] { 0x171 }, false, false)]
    [InlineData("sell", new[] { 0x177 }, false, false)]
    [InlineData("buy please", new[] { 0x171 }, false, false)]
    [InlineData("hello buy", new[] { 0x171 }, false, false)]
    [InlineData("buy?", new[] { 0x171 }, false, false)]
    [InlineData("vendor buy", new[] { 0x3C }, false, false)]
    [InlineData("vendor sell", new[] { 0x14D }, false, false)]
    [InlineData("vendor buy some wine", new[] { 0x3C }, false, false)]
    // a real non-trade command is still the game's
    [InlineData("I want to buy a bank box", new[] { 0x2, 0x171 }, false, false)]
    [InlineData("guards! I want to buy", new[] { 0x7, 0x171 }, false, false)]
    [InlineData("I want to see you", new[] { 0x180 }, false, false)]           // no trade word at all
    // questions are talk as before, named or not
    [InlineData("where can I buy a sword", new[] { 0x171 }, false, true)]
    [InlineData("Mirabel can I buy a room", new[] { 0x171 }, true, true)]
    public void BuyingStatementsThatNameNobodyAreTalk(string line, int[] keywords, bool named, bool talk) =>
        Assert.Equal(talk, GameCommands.OnlyTradeTalk(keywords, line, named));
}
