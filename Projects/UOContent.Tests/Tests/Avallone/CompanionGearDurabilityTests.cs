using Server;
using Server.Avallone.Companions;
using Server.Items;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// Phase 4e, G4 durability: a recorded item on a companion never loses durability and is never worn away. Each test has
// a positive control - the same kind of item in the player's pack, hit the same way, does wear.
[Collection("Sequential UOContent Tests")]
public class CompanionGearDurabilityTests
{
    private static (ElspethVale E, PlayerMobile P) Scene()
    {
        CompanionGear.ClearForTests();
        var e = new ElspethVale();
        e.MoveToWorld(new Point3D(1400, 1600, 0), Map.Felucca);
        e.RawStr = 120;
        var p = new PlayerMobile { Name = "Racer X" };
        p.DefaultMobileInit();
        p.AddItem(new Backpack { Movable = false });
        p.MoveToWorld(new Point3D(1401, 1600, 0), Map.Felucca);
        e.SetControlMaster(p);
        CompanionGear.SeedStarters(e);
        return (e, p);
    }

    private static void End(ElspethVale e, PlayerMobile p)
    {
        e.Delete();
        p.Delete();
        CompanionGear.ClearForTests();
    }

    private static T Durable<T>(T item) where T : Item, IDurability
    {
        item.MaxHitPoints = 50;
        item.HitPoints = 50;
        return item;
    }

    private static void Hammer(Item item, int times)
    {
        var mace = new Mace();
        for (var i = 0; i < times && !item.Deleted; i++)
        {
            switch (item)
            {
                case BaseArmor a:    a.OnHit(mace, 30); break;
                case BaseClothing c: c.OnHit(mace, 30); break;
            }
        }

        mace.Delete();
    }

    [Theory]
    [InlineData("chest")]
    [InlineData("shield")]
    [InlineData("cloak")]
    public void GearOnHerDoesNotWearButTheSameInAPackDoes(string kind)
    {
        var (e, p) = Scene();
        try
        {
            Item Make() => kind switch
            {
                "chest"  => Durable(new RingmailChest()),
                "shield" => Durable(new WoodenShield()),
                _        => Durable(new Cloak())
            };

            var given = Make();
            p.Backpack.DropItem(given);
            Assert.True(e.OnDragDrop(p, given));
            Assert.True(CompanionGear.IsProtected(given));

            var control = Make();
            p.Backpack.DropItem(control);

            Hammer(given, 400);
            Hammer(control, 400);

            var g = (IDurability)given;
            Assert.False(given.Deleted);
            Assert.Equal(50, g.HitPoints);
            Assert.Equal(50, g.MaxHitPoints);
            Assert.True(control.Deleted || ((IDurability)control).HitPoints < 50, "positive control did not wear");
            control.Delete();
        }
        finally
        {
            End(e, p);
        }
    }

    [Fact]
    public void DirectDurabilityWritersCannotLowerItButCanStillRaiseIt()
    {
        var (e, p) = Scene();
        try
        {
            var sword = Durable(new Longsword());
            p.Backpack.DropItem(sword);
            Assert.True(e.OnDragDrop(p, sword));

            IDurability d = sword;
            d.HitPoints -= 10;                    // the shape Ilhenir's corrosive goo uses
            Assert.Equal(50, d.HitPoints);

            var chest = Durable(new RingmailChest());
            p.Backpack.DropItem(chest);
            Assert.True(e.OnDragDrop(p, chest));
            ((IDurability)chest).HitPoints = -1;  // would delete an unprotected piece
            Assert.False(chest.Deleted);

            Assert.True(e.TryGearRequest(p, "give me back the sword"));
            d.HitPoints -= 10;                    // back with the player: wears normally again
            Assert.Equal(40, d.HitPoints);

            var dagger = e.FindItemOnLayer(Layer.OneHanded);
            Assert.True(CompanionGear.IsProtected(dagger)); // her own kit is protected too
        }
        finally
        {
            End(e, p);
        }
    }
}
