using System;
using Server;
using Server.Avallone.Companions;
using Server.Items;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// Phase 4e, G2 + G3: giving gear and asking for it back. Every test checks the ORIGINAL item serial and that no item is
// created or lost (Codex acceptance tests 1-4, 10).
[Collection("Sequential UOContent Tests")]
public class CompanionGearGiveTests
{
    private sealed class RefusingSword : Longsword
    {
        public override bool CanEquip(Mobile m) => false;
    }

    private sealed class Scene : IDisposable
    {
        public readonly ElspethVale E;
        public readonly PlayerMobile P;
        public readonly Item Dagger;

        public Scene()
        {
            CompanionGear.ClearForTests();
            E = new ElspethVale();
            E.MoveToWorld(new Point3D(1400, 1600, 0), Map.Felucca);
            P = NewPlayer("Racer X", 1401);
            E.SetControlMaster(P);
            CompanionGear.SeedStarters(E);
            Dagger = E.FindItemOnLayer(Layer.OneHanded);
        }

        public static PlayerMobile NewPlayer(string name, int x)
        {
            var m = new PlayerMobile { Name = name };
            m.DefaultMobileInit();
            m.AddItem(new Backpack { Movable = false });
            m.MoveToWorld(new Point3D(x, 1600, 0), Map.Felucca);
            return m;
        }

        public T Hold<T>(T item) where T : Item
        {
            P.Backpack.DropItem(item);
            return item;
        }

        public void Dispose()
        {
            E.Delete();
            P.Delete();
            CompanionGear.ClearForTests();
        }
    }

    [Fact]
    public void GivingASwordEquipsTheSameSerialAndPacksHerDagger()
    {
        using var s = new Scene();
        var sword = s.Hold(new Longsword());
        var serial = sword.Serial;

        Assert.True(s.E.OnDragDrop(s.P, sword));

        Assert.Same(s.E, sword.Parent);
        Assert.Equal(serial, sword.Serial);
        Assert.Same(s.E.Backpack, s.Dagger.Parent);                 // her own dagger moved to her pack, not lost
        var r = CompanionGear.Get(sword);
        Assert.Equal(GearOrigin.Given, r.Origin);
        Assert.Equal("Racer X", r.GiverName);
        Assert.Equal(GearState.OnCompanion, CompanionGear.Get(s.Dagger).State);
        Assert.True(CompanionGear.IsProtected(sword));
    }

    [Fact]
    public void AskingBackReturnsItToTheBackpackAndHerDaggerGoesBackOn()
    {
        using var s = new Scene();
        var sword = s.Hold(new Longsword());
        Assert.True(s.E.OnDragDrop(s.P, sword));

        Assert.True(s.E.TryGearRequest(s.P, "Elspeth, give me back the sword"));

        Assert.Same(s.P.Backpack, sword.Parent);
        Assert.Null(CompanionGear.Get(sword));
        Assert.Same(s.E, s.Dagger.Parent);
        Assert.Equal(Layer.OneHanded, s.Dagger.Layer);
        Assert.False(sword.Deleted);
    }

    [Fact]
    public void SomeoneElseCannotAskForIt()
    {
        using var s = new Scene();
        var sword = s.Hold(new Longsword());
        Assert.True(s.E.OnDragDrop(s.P, sword));
        var other = Scene.NewPlayer("Stranger", 1399);
        try
        {
            Assert.False(s.E.TryGearRequest(other, "give me back the sword"));
            Assert.Same(s.E, sword.Parent);
            Assert.NotNull(CompanionGear.Get(sword));
        }
        finally
        {
            other.Delete();
        }
    }

    [Fact]
    public void FullBackpackMeansNoChangeAtAll()
    {
        using var s = new Scene();
        var sword = s.Hold(new Longsword());
        Assert.True(s.E.OnDragDrop(s.P, sword));
        while (s.P.Backpack.TotalItems < s.P.Backpack.MaxItems)
        {
            s.P.Backpack.DropItem(new Torch());
        }

        var r = CompanionGear.Get(sword);
        Assert.False(s.E.ReturnGear(s.P, r));

        Assert.Same(s.E, sword.Parent);
        Assert.Same(r, CompanionGear.Get(sword));
    }

    [Fact]
    public void SeveralMatchesAskWhichAndMoveNothing()
    {
        using var s = new Scene();
        var sword = s.Hold(new Longsword());
        var cloak = s.Hold(new Cloak());
        Assert.True(s.E.OnDragDrop(s.P, sword));
        Assert.True(s.E.OnDragDrop(s.P, cloak));

        Assert.True(s.E.TryGearRequest(s.P, "give me back my stuff"));  // a list would be shown; nothing moves yet

        Assert.Same(s.E, sword.Parent);
        Assert.Same(s.E, cloak.Parent);
        Assert.Equal(2, s.E.GearGivenBy(s.P).Count);
        Assert.Single(s.E.MatchRequest("give me back the cloak", s.E.GearGivenBy(s.P)));
    }

    [Fact]
    public void FailedEquipPutsEverythingBack()
    {
        using var s = new Scene();
        var odd = s.Hold(new RefusingSword());
        var itemsBefore = World.Items.Count;

        Assert.False(s.E.TryAcceptGear(s.P, odd));

        Assert.NotSame(s.E, odd.Parent);
        Assert.Null(CompanionGear.Get(odd));
        Assert.Same(s.E, s.Dagger.Parent);                           // dagger back on, not left in the pack
        Assert.Equal(Layer.OneHanded, s.Dagger.Layer);
        Assert.False(odd.Deleted);
        Assert.True(World.Items.Count >= itemsBefore - 0 && World.Items.Count <= itemsBefore + 1); // at most her new pack
    }

    [Theory]
    [InlineData("ring")]
    [InlineData("bow")]
    [InlineData("magic")]
    [InlineData("insured")]
    [InlineData("plate")]
    [InlineData("halberd")]
    public void RefusedItemsStayWithThePlayer(string kind)
    {
        using var s = new Scene();
        s.E.RawStr = 120; // so only the rule under test can refuse
        Item item = kind switch
        {
            "ring"    => new GoldRing(),
            "bow"     => new Bow(),
            "magic"   => new Longsword(),
            "insured" => new Longsword { Insured = true },
            "plate"   => new PlateGloves(),
            _         => new Halberd()
        };
        if (item is Longsword && kind == "magic")
        {
            ((Longsword)item).Attributes.WeaponDamage = 10;
        }

        s.Hold(item);
        Assert.False(s.E.OnDragDrop(s.P, item));
        Assert.Same(s.P.Backpack, item.Parent);
        Assert.Null(CompanionGear.Get(item));
        Assert.Same(s.E, s.Dagger.Parent);
    }

    [Fact]
    public void StavesAreAllowedForElspeth()
    {
        using var s = new Scene();
        var staff = s.Hold(new QuarterStaff());
        Assert.True(s.E.OnDragDrop(s.P, staff));
        Assert.Same(s.E, staff.Parent);
    }

    [Fact]
    public void OnlyHerCompanionMayGiveAndNotMidFight()
    {
        using var s = new Scene();
        var other = Scene.NewPlayer("Stranger", 1399);
        try
        {
            var sword = new Longsword();
            other.Backpack.DropItem(sword);
            Assert.False(s.E.OnDragDrop(other, sword));
            Assert.Same(other.Backpack, sword.Parent);

            var mine = s.Hold(new Longsword());
            s.E.Warmode = true;
            Assert.False(s.E.OnDragDrop(s.P, mine));
            Assert.Same(s.P.Backpack, mine.Parent);
        }
        finally
        {
            other.Delete();
        }
    }

    [Fact]
    public void GiveBackThenGiveAgainIsWornOnTheSameLayer()
    {
        using var s = new Scene();
        var gloves = s.Hold(new RingmailGloves());
        Assert.True(s.E.OnDragDrop(s.P, gloves));
        Assert.True(s.E.TryGearRequest(s.P, "give me back the gloves"));
        Assert.Same(s.P.Backpack, gloves.Parent);

        Assert.True(s.E.OnDragDrop(s.P, gloves));

        Assert.Same(s.E, gloves.Parent);
        Assert.Equal(Layer.Gloves, gloves.Layer);
        Assert.Same(gloves, s.E.FindItemOnLayer(Layer.Gloves));
        Assert.True(gloves.Visible);
        Assert.Equal(1, System.Linq.Enumerable.Count(s.E.Items, i => i == gloves));
    }

    [Fact]
    public void ChestArmourTakesHerDressOffAndItComesBackWithTheArmour()
    {
        using var s = new Scene();
        var dress = s.E.FindItemOnLayer(Layer.OuterTorso);
        Assert.NotNull(dress);
        var chest = s.Hold(new RingmailChest());

        Assert.True(s.E.OnDragDrop(s.P, chest));
        Assert.Same(s.E, chest.Parent);
        Assert.Same(s.E.Backpack, dress.Parent);                     // in her pack, not lost
        Assert.Null(s.E.FindItemOnLayer(Layer.OuterTorso));
        Assert.NotNull(CompanionGear.Get(dress));

        Assert.True(s.E.TryGearRequest(s.P, "give me back the chest"));
        Assert.Same(s.P.Backpack, chest.Parent);
        Assert.Same(dress, s.E.FindItemOnLayer(Layer.OuterTorso));   // dress back on
    }

    [Fact]
    public void DressStaysOffWhileOtherChestArmourIsStillWorn()
    {
        using var s = new Scene();
        var dress = s.E.FindItemOnLayer(Layer.OuterTorso);
        var first = s.Hold(new RingmailChest());
        var second = s.Hold(new LeatherChest());
        Assert.True(s.E.OnDragDrop(s.P, first));
        Assert.True(s.E.OnDragDrop(s.P, second));                    // first goes to her pack, second worn
        Assert.Same(s.E.Backpack, first.Parent);

        Assert.True(s.E.ReturnGear(s.P, CompanionGear.Get(first)));  // the packed one goes back; second still worn
        Assert.Same(s.E.Backpack, dress.Parent);
        Assert.Same(second, s.E.FindItemOnLayer(Layer.InnerTorso));

        Assert.True(s.E.ReturnGear(s.P, CompanionGear.Get(second)));
        Assert.Same(dress, s.E.FindItemOnLayer(Layer.OuterTorso));
    }

    [Fact]
    public void ARobeOrCloakDoesNotTakeHerDressOff()
    {
        using var s = new Scene();
        var dress = s.E.FindItemOnLayer(Layer.OuterTorso);
        var cloak = s.Hold(new Cloak());
        Assert.True(s.E.OnDragDrop(s.P, cloak));
        Assert.Same(s.E, dress.Parent);
    }
}
