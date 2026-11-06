using Server;
using Server.Avallone.Companions;
using Server.Items;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// Phase 4e G4: a player's gift is never lost and never copied when the companion is deleted, the giver is deleted, or
// the item turns up where it should not be. Every test follows the ORIGINAL serial.
[Collection("Sequential UOContent Tests")]
public class CompanionGearCustodyTests
{
    private static (ElspethVale E, PlayerMobile P) Scene()
    {
        CompanionGear.ClearForTests();
        var e = new ElspethVale();
        e.MoveToWorld(new Point3D(1400, 1600, 0), Map.Felucca);
        var p = NewPlayer("Racer X");
        e.SetControlMaster(p);
        CompanionGear.SeedStarters(e);
        return (e, p);
    }

    private static PlayerMobile NewPlayer(string name)
    {
        var p = new PlayerMobile { Name = name };
        p.DefaultMobileInit();
        p.AddItem(new Backpack { Movable = false });
        p.MoveToWorld(new Point3D(1401, 1600, 0), Map.Felucca);
        return p;
    }

    private static Item Give(ElspethVale e, PlayerMobile p, Item item)
    {
        p.Backpack.DropItem(item);
        Assert.True(e.OnDragDrop(p, item));
        return item;
    }

    private static void End(params Mobile[] ms)
    {
        foreach (var m in ms)
        {
            m.Delete();
        }

        CompanionGear.ClearForTests();
    }

    [Fact]
    public void DeletingHerSendsGiftsToTheBoxAndHerOwnKitGoesWithHer()
    {
        var (e, p) = Scene();
        try
        {
            var sword = Give(e, p, new Longsword());
            var serial = sword.Serial;
            var dagger = e.Backpack.FindItemByType<Dagger>();
            Assert.NotNull(dagger);

            e.Delete();

            Assert.False(sword.Deleted);
            Assert.Equal(serial, sword.Serial);
            Assert.Same(CompanionGearBox.Current, sword.Parent);
            var r = CompanionGear.Get(sword);
            Assert.Equal(GearState.InHoldingBox, r.State);
            Assert.Equal("Racer X", r.GiverName);
            Assert.True(dagger.Deleted);                       // hers, never a player's
            Assert.Equal(0, CompanionGear.TombstoneMissing("test")); // her kit was closed, not "missing"
        }
        finally
        {
            End(e, p);
        }
    }

    [Fact]
    public void GiverDeletedMovesTheGiftToTheBoxAtTheNextCheck()
    {
        var (e, p) = Scene();
        try
        {
            var cloak = Give(e, p, new Cloak());
            Assert.Equal(0, CompanionGear.Reconcile("test"));  // all in order: nothing changes

            p.Delete();
            Assert.Equal(1, CompanionGear.Reconcile("test"));

            Assert.Same(CompanionGearBox.Current, cloak.Parent);
            Assert.Equal(GearState.InHoldingBox, CompanionGear.Get(cloak).State);
            Assert.Equal(0, CompanionGear.Reconcile("test"));  // and stays put
        }
        finally
        {
            End(e, p);
        }
    }

    [Fact]
    public void AGiftFoundAwayFromHerIsQuarantined()
    {
        var (e, p) = Scene();
        try
        {
            var sword = Give(e, p, new Longsword());
            sword.MoveToWorld(new Point3D(1405, 1600, 0), Map.Felucca); // something outside this system moved it

            Assert.Equal(1, CompanionGear.Reconcile("test"));
            Assert.Same(CompanionGearBox.Current, sword.Parent);
        }
        finally
        {
            End(e, p);
        }
    }

    [Fact]
    public void AReturnThatReachedThePlayerIsFinished()
    {
        var (e, p) = Scene();
        try
        {
            var sword = Give(e, p, new Longsword());
            p.Backpack.DropItem(sword);                         // moved, but the record was never closed

            Assert.Equal(1, CompanionGear.Reconcile("test"));
            Assert.Null(CompanionGear.Get(sword));
            Assert.Same(p.Backpack, sword.Parent);              // left with its owner, not quarantined
        }
        finally
        {
            End(e, p);
        }
    }

    // AGmCanTakeAnItemOutForTheOwner removed 2026-10-24: 'take' closed the record in the GM's pack (Codex round 3 P0).
    // Replaced by the 'return' tests in CompanionGearRound3Tests.

    [Fact]
    public void TheBoxWillNotBeDeletedWhileItHoldsAnything()
    {
        var (e, p) = Scene();
        try
        {
            var sword = Give(e, p, new Longsword());
            e.Delete();
            var box = CompanionGearBox.Current;

            box.Delete();

            Assert.False(box.Deleted);
            Assert.False(sword.Deleted);
        }
        finally
        {
            End(e, p);
        }
    }

    [Fact]
    public void WithNoUsableBoxTheGiftIsStillKeptAlive()
    {
        var (e, p) = Scene();
        try
        {
            var sword = Give(e, p, new Longsword());
            CompanionGearBox.Broken = true;

            e.Delete();

            Assert.False(sword.Deleted);
            Assert.Null(sword.Parent);
            Assert.Equal(Map.Internal, sword.Map);
            Assert.Equal(GearState.InHoldingBox, CompanionGear.Get(sword).State);

            CompanionGearBox.Broken = false;                    // box usable again: the next check puts it in
            Assert.Equal(1, CompanionGear.Reconcile("test"));
            Assert.Same(CompanionGearBox.Current, sword.Parent);
        }
        finally
        {
            End(e, p);
        }
    }
}
