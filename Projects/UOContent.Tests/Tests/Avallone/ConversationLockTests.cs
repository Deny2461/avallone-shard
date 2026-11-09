using Server;
using Server.Avallone.AI;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// Step B (the owner, chat, 2026-11-08)
// - one conversation "token" per player.
[Collection("Sequential UOContent Tests")]
public class ConversationLockTests
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

    private static BaseCreature Pick(Mobile p, string text)
    {
        NameResolver.ResetCacheForTests();
        return TownsfolkTalk.PickListener(p, text, Core.Now);
    }

    [Fact]
    public void TheHolderKeepsUnnamedLinesOverANearerTownsperson()
    {
        Conversation.ResetForTests();
        var p = NewPlayer();
        var gus = Vendor("Gus", 3);
        var nell = Vendor("Nell", 1);
        try
        {
            Assert.Equal(nell, Pick(p, "how are you today?"));   // control: no token -> the nearest answers
            Conversation.Note(p, gus, "hail Gus");
            Assert.Equal(gus, Pick(p, "how are you today?"));
            Assert.Equal(gus, Pick(p, "tell me more"));
            Assert.Equal(nell, Pick(p, "Nell, any news?"));      // naming someone else: theirs
        }
        finally
        {
            gus.Delete();
            nell.Delete();
            p.Delete();
        }
    }

    [Fact]
    public void TheHolderCanBeFartherThanTheOldFourTiles()
    {
        Conversation.ResetForTests();
        var p = NewPlayer();
        var gus = Vendor("Gus", 7);
        try
        {
            Assert.Null(Pick(p, "what news?"));                  // control: 7 tiles, no token -> nobody
            Conversation.Note(p, gus);
            Assert.Equal(gus, Pick(p, "what news?"));
        }
        finally
        {
            gus.Delete();
            p.Delete();
        }
    }

    [Fact]
    public void OutOfRangeReleasesIt()
    {
        Conversation.ResetForTests();
        var p = NewPlayer();
        var gus = Vendor("Gus", 3);
        var nell = Vendor("Nell", 1);
        try
        {
            Conversation.Note(p, gus);
            gus.MoveToWorld(new Point3D(2000 + Conversation.Range + 2, 1500, 0), Map.Felucca);
            Assert.Null(Conversation.Partner(p));
            Assert.Equal(nell, Pick(p, "hello"));
        }
        finally
        {
            gus.Delete();
            nell.Delete();
            p.Delete();
        }
    }

    [Theory]
    [InlineData("goodbye", true)]
    [InlineData("Farewell, Gus", true)]
    [InlineData("ok bye", true)]
    [InlineData("safe travels", true)]
    [InlineData("see you later", true)]
    [InlineData("I said goodbye to her yesterday", false)]
    [InlineData("how are you?", false)]
    public void Farewells(string text, bool farewell) => Assert.Equal(farewell, Conversation.IsFarewell(text));

    [Fact]
    public void AFarewellEndsItAndDeathOrDeletionToo()
    {
        Conversation.ResetForTests();
        var p = NewPlayer();
        var gus = Vendor("Gus", 3);
        try
        {
            Conversation.Note(p, gus, "hail");
            Assert.True(Conversation.HeldBy(p, gus));
            Conversation.Note(p, gus, "farewell");
            Conversation.EndClosingForTests(p);
            Assert.Null(Conversation.Partner(p));
            Conversation.Note(p, gus, "hail");
            gus.Delete();
            Assert.Null(Conversation.Partner(p));
        }
        finally
        {
            p.Delete();
        }
    }

    // Codex Step B r1 finding 2: the farewell line is still the partner's while the other handlers run (Mirabel nearby must
    // not also answer it), and once it is over the old 45 s window must not hand the next unnamed line back to the partner.
    [Fact]
    public void AFarewellStaysThePartnersForThatLineThenTheNearestAnswers()
    {
        Conversation.ResetForTests();
        var p = NewPlayer();
        var gus = Vendor("Gus", 3);
        var nell = Vendor("Nell", 1);
        try
        {
            Conversation.Note(p, gus, "hail Gus");
            TownsfolkTalk.NoteTalkForTests(gus, p);                  // the old window's record, as after a real answer
            Conversation.Note(p, gus, "goodbye");
            Assert.True(Conversation.HeldBy(p, gus));                // same speech event: still his ...
            Assert.True(Conversation.HeldByOther(p, nell));          // ... so nobody else (Mirabel's check) takes it
            Assert.Equal(gus, Pick(p, "goodbye"));
            Conversation.EndClosingForTests(p);
            Assert.Null(Conversation.Partner(p));
            Assert.False(TownsfolkTalk.InConversation(p));           // companions are not held off by the old window
            Assert.Equal(nell, Pick(p, "hello"));                    // not Gus through the old 45 s window
        }
        finally
        {
            gus.Delete();
            nell.Delete();
            p.Delete();
        }
    }

    [Fact]
    public void WithoutATokenTheOldWindowNoLongerPicks()   // control for the test above: the window alone, no token at all
    {
        Conversation.ResetForTests();
        var p = NewPlayer();
        var gus = Vendor("Gus", 3);
        var nell = Vendor("Nell", 1);
        try
        {
            TownsfolkTalk.NoteTalkForTests(gus, p);
            Assert.Equal(nell, Pick(p, "how are you?"));
        }
        finally
        {
            gus.Delete();
            nell.Delete();
            p.Delete();
        }
    }

    [Fact]
    public void ACompanionYieldsToTheTokenButNotForHerOwnOrdersOrHerName()
    {
        Conversation.ResetForTests();
        var p = NewPlayer();
        var e = new ElspethVale();
        e.MoveToWorld(new Point3D(2001, 1500, 0), Map.Felucca);
        e.SetControlMaster(p);
        var gus = Vendor("Gus", 3);
        try
        {
            NameResolver.ResetCacheForTests();
            Assert.True(e.IsAddressedBy(p, "how are you?"));          // control: no token, she is right beside him
            Conversation.Note(p, gus, "hail Gus");
            NameResolver.ResetCacheForTests();
            Assert.False(e.IsAddressedBy(p, "how are you?"));         // Gus holds it
            NameResolver.ResetCacheForTests();
            Assert.True(e.IsAddressedBy(p, "follow me"));             // her own plain order is still hers
            NameResolver.ResetCacheForTests();
            Assert.True(e.IsAddressedBy(p, "Elspeth, how are you?")); // named
            Conversation.Note(p, e);
            NameResolver.ResetCacheForTests();
            Assert.True(e.IsAddressedBy(p, "how are you?"));          // she holds it now
            Assert.Null(Pick(p, "what news?"));                       // and no townsperson takes it
        }
        finally
        {
            gus.Delete();
            e.Delete();
            p.Delete();
        }
    }
}
