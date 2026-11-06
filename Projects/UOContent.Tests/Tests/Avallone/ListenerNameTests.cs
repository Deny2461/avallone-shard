using Server;
using Server.Avallone.AI;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// the owner, 2026-10-16: next to the town crier was answered by Ayana AND Elspeth, because a TownCrier
// (a plain Mobile) was never a candidate for "names someone else nearby". Codex review 2026-10-16: exact leading name
// only for such NPCs - no common-word false positives, no near-miss spelling.
[Collection("Sequential UOContent Tests")]
public class ListenerNameTests
{
    private static PlayerMobile NewPlayer(int x)
    {
        var m = new PlayerMobile(World.NewMobile);
        m.DefaultMobileInit();
        m.MoveToWorld(new Point3D(x, 1500, 0), Map.Felucca);
        return m;
    }

    private static TownCrier NewCrier(string name, int x)
    {
        var c = new TownCrier { Name = name };
        c.MoveToWorld(new Point3D(x, 1500, 0), Map.Felucca);
        return c;
    }

    [Theory]
    [InlineData("Ayana, what news", true)]
    [InlineData("ayana, what news?", true)]
    [InlineData("Ayana!", true)]
    [InlineData("Ayana", true)]
    [InlineData("hail Ayana", true)]
    [InlineData("Hello Ayana, any news?", true)]
    [InlineData("what news?", false)]              // unnamed: still Elspeth's mid-conversation (unchanged)
    [InlineData("I heard Ayana say it", false)]    // not addressed to her - only a leading name counts
    [InlineData("Ayanna, what news", false)]       // no near-miss spelling for stock NPCs
    [InlineData("Elspeth, what news", false)]
    public void TownCrierIsACandidate(string text, bool expected)
    {
        var player = NewPlayer(2000);
        var crier = NewCrier("Ayana", 2002);
        try
        {
            Assert.Equal(expected, TownsfolkTalk.NamesSomeoneNearby(player, text));
        }
        finally
        {
            crier.Delete();
            player.Delete();
        }
    }

    [Theory]
    [InlineData("Will you follow me?", false)]
    [InlineData("will you watch the road", false)]
    [InlineData("Will, what news?", true)]
    [InlineData("May I ask you something", false)]
    [InlineData("Mark the map for me", false)]
    public void CommonWordNamesNeedTheVocative(string text, bool expected)
    {
        var player = NewPlayer(2100);
        var will = NewCrier("Will", 2101);
        var may = NewCrier("May", 2102);
        var mark = NewCrier("Mark", 2103);
        try
        {
            Assert.Equal(expected, TownsfolkTalk.NamesSomeoneNearby(player, text));
        }
        finally
        {
            will.Delete();
            may.Delete();
            mark.Delete();
            player.Delete();
        }
    }

    [Fact]
    public void HiddenOrFarCriersDoNotCount()
    {
        var player = NewPlayer(2200);
        var hidden = NewCrier("Ayana", 2202);
        var far = NewCrier("Brom", 2230);
        hidden.Hidden = true;
        try
        {
            Assert.False(TownsfolkTalk.NamesSomeoneNearby(player, "Ayana, what news"));
            Assert.False(TownsfolkTalk.NamesSomeoneNearby(player, "Brom, what news"));
        }
        finally
        {
            hidden.Delete();
            far.Delete();
            player.Delete();
        }
    }

    [Fact]
    public void ThePlayersOwnNameIsNotSomeoneElse()
    {
        var player = NewPlayer(2300);
        player.Name = "Racer X";
        try
        {
            Assert.False(TownsfolkTalk.NamesSomeoneNearby(player, "Racer, that's me"));
        }
        finally
        {
            player.Delete();
        }
    }
}

// The leading-name rule on its own - needs no world, so it runs where the world fixture cannot (the shard container).
public class OpensWithNameTests
{
    [Theory]
    [InlineData("Ayana, what news", "Ayana", true)]
    [InlineData("ayana, what news?", "Ayana", true)]
    [InlineData("Ayana!", "Ayana", true)]
    [InlineData("Ayana", "Ayana", true)]
    [InlineData("  hail Ayana", "Ayana", true)]
    [InlineData("Hello Ayana, any news?", "Ayana", true)]
    [InlineData("hi, Ayana.", "Ayana", true)]
    [InlineData("what news?", "Ayana", false)]
    [InlineData("I heard Ayana say it", "Ayana", false)]
    [InlineData("Ayanna, what news", "Ayana", false)]
    [InlineData("Ayanas, what news", "Ayana", false)]
    [InlineData("Ayana what news", "Ayana", false)]       // strict rule (name treated as a word): comma needed
    [InlineData("Will you follow me?", "Will", false)]
    [InlineData("will you watch the road", "Will", false)]
    [InlineData("Will, what news?", "Will", true)]
    [InlineData("May I ask you something", "May", false)]
    [InlineData("Mark the map for me", "Mark", false)]
    [InlineData("hello there", "Will", false)]
    [InlineData("O'Brien, a word", "O'Brien", true)]       // names with regex characters are escaped
    public void OnlyALeadingNameCounts(string text, string name, bool expected) =>
        Assert.Equal(expected, TownsfolkTalk.OpensWithName(text, name));

    // the owner, 2026-10-17: a name that is not an ordinary word counts without the comma.
    [Theory]
    [InlineData("Ayana what news?", true)]
    [InlineData("ayana what news", true)]
    [InlineData("hail Ayana how goes it", true)]
    [InlineData("Ayana, what news", true)]
    [InlineData("Ayanas what news", false)]
    [InlineData("Ayana's stall is closed", false)]
    [InlineData("I heard Ayana say it", false)]
    [InlineData("what news Ayana", false)]
    public void UncommonNamesNeedNoComma(string text, bool expected) =>
        Assert.Equal(expected, TownsfolkTalk.OpensWithName(text, "Ayana", nameIsWord: false));

    [Theory]
    [InlineData("Will you follow me?")]
    [InlineData("May I ask you something")]
    [InlineData("Mark the map for me")]
    public void WordNamesStillNeedTheComma(string text) =>
        Assert.False(TownsfolkTalk.OpensWithName(text, text.Split(' ')[0], nameIsWord: true));
}
