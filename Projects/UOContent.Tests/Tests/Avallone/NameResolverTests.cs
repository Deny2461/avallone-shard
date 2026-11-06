using Server;
using Server.Avallone.AI;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// Phase 4e N: one name resolver for every AI character. the owner, chat, 2026-10-20: (a
// townsperson named Ailiynn); "close enough". Sound-alike is log-only (the owner, chat, 2026-10-21).
[Collection("Sequential UOContent Tests")]
public class NameResolverTests
{
    private static PlayerMobile NewPlayer()
    {
        var m = new PlayerMobile { Name = "Racer X" };
        m.DefaultMobileInit();
        m.MoveToWorld(new Point3D(2000, 1500, 0), Map.Felucca);
        return m;
    }

    private static Provisioner Vendor(string name, int dx)
    {
        var v = new Provisioner { Name = name };
        v.MoveToWorld(new Point3D(2000 + dx, 1500, 0), Map.Felucca);
        return v;
    }

    private static NameResult Resolve(Mobile from, string text)
    {
        NameResolver.ResetCacheForTests();
        return NameResolver.Resolve(from, text);
    }

    [Fact]
    public void TheWordListIsLoaded() => Assert.True(TownsfolkTalk.WordList.Count > 1000); // else near misses are off

    [Theory]
    [InlineData("elspteh", "elspeth", 1)] // swapped neighbours count once
    [InlineData("ailyn", "ailiyn", 1)]
    [InlineData("brem", "bram", 1)]
    [InlineData("corwin", "corwen", 1)]
    public void Distances(string a, string b, int d) => Assert.Equal(d, NameResolver.Osa(a, b));

    [Fact]
    public void DoubledLettersCollapse() => Assert.Equal("ailiyn", NameResolver.CollapseDoubles("ailiynn"));

    [Theory]
    [InlineData("Ailiynn, what news?", "Ailiynn", NameMatch.Exact)]
    [InlineData("Ailynn, what news?", "Ailiynn", NameMatch.Near)]
    [InlineData("ailiyn what news", "Ailiynn", NameMatch.Near)]
    [InlineData("Alyinn hello", "Ailiynn", NameMatch.Near)]   // 7+ letters: two off is still her
    [InlineData("hello there", null, NameMatch.None)]
    public void TownspersonWithAHardName(string text, string expected, NameMatch match)
    {
        var p = NewPlayer();
        var v = Vendor("Ailiynn", 2);
        try
        {
            var r = Resolve(p, text);
            Assert.Equal(expected, r.Winner?.Name);
            Assert.Equal(match, r.Match);
            Assert.False(r.Ambiguous);
        }
        finally
        {
            v.Delete();
            p.Delete();
        }
    }

    [Fact]
    public void ElspethCloseEnoughAndPossessive()
    {
        var p = NewPlayer();
        var e = new ElspethVale();
        e.MoveToWorld(new Point3D(2001, 1500, 0), Map.Felucca);
        e.SetControlMaster(p);
        try
        {
            Assert.Same(e, Resolve(p, "Elspth, wait here").Winner);
            Assert.Same(e, Resolve(p, "elspteh follow me").Winner);
            Assert.Same(e, Resolve(p, "is that Elspeth's dagger").Winner);
            NameResolver.ResetCacheForTests();
            Assert.True(e.IsAddressedBy(p, "Elspth, wait here"));
        }
        finally
        {
            e.Delete();
            p.Delete();
        }
    }

    [Fact]
    public void ALineForSomeoneElseIsNotHers()
    {
        var p = NewPlayer();
        var e = new ElspethVale();
        e.MoveToWorld(new Point3D(2001, 1500, 0), Map.Felucca);
        e.SetControlMaster(p);
        var v = Vendor("Ailiynn", 2);
        try
        {
            NameResolver.ResetCacheForTests();
            Assert.False(e.IsAddressedBy(p, "Ailynn, what news?"));   // she is right there, but it is Ailiynn's
            NameResolver.ResetCacheForTests();
            Assert.True(TownsfolkTalk.NamesSomeoneNearby(p, "Ailynn, what news?", e));
        }
        finally
        {
            v.Delete();
            e.Delete();
            p.Delete();
        }
    }

    [Fact]
    public void OneWordFittingTwoDifferentNamesIsNobodys()
    {
        var p = NewPlayer();
        var a = Vendor("Corwen", 1);
        var b = Vendor("Corwin", 2);
        try
        {
            var r = Resolve(p, "corwan, hello");
            Assert.Null(r.Winner);
            Assert.True(r.Ambiguous);

            Assert.Same(b, Resolve(p, "corwin, hello").Winner);       // exact beats close
            Assert.Same(a, Resolve(p, "Corwen, tell Corwin hello").Winner); // the name said first
        }
        finally
        {
            a.Delete();
            b.Delete();
            p.Delete();
        }
    }

    [Fact]
    public void TwoPeopleWithTheSameNameTheNearerAnswers()
    {
        var p = NewPlayer();
        var far = Vendor("Ailiynn", 3);
        var near = Vendor("Ailiynn", 1);
        try
        {
            Assert.Same(near, Resolve(p, "Ailiynn, hello").Winner);
        }
        finally
        {
            far.Delete();
            near.Delete();
            p.Delete();
        }
    }

    [Fact]
    public void GuardsAgainstOrdinaryWordsShortNamesAndDistance()
    {
        var p = NewPlayer();
        var thera = Vendor("Thera", 1);
        var ned = Vendor("Ned", 2);
        var far = Vendor("Ailiynn", 6);
        try
        {
            Assert.Null(Resolve(p, "there you go").Winner);           // an English word is never a near miss
            Assert.Null(Resolve(p, "nad, hello").Winner);             // 3-letter names: exact only
            Assert.Same(ned, Resolve(p, "Ned, hello").Winner);
            Assert.Null(Resolve(p, "Ailynn, hello").Winner);          // near misses only within 4 tiles
            Assert.Same(far, Resolve(p, "Ailiynn, hello").Winner);    // the exact name reaches further
        }
        finally
        {
            thera.Delete();
            ned.Delete();
            far.Delete();
            p.Delete();
        }
    }

    [Fact]
    public void SoundAlikeIsNeverAnswered()
    {
        Assert.True(NameResolver.SoundsLike("aleen", "ailiynn"));
        var p = NewPlayer();
        var v = Vendor("Ailiynn", 2);
        try
        {
            var r = Resolve(p, "aleen, hello");
            Assert.Null(r.Winner);
            Assert.False(r.Ambiguous);
        }
        finally
        {
            v.Delete();
            p.Delete();
        }
    }
}
