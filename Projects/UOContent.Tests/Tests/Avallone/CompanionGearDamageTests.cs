using Server;
using Server.Avallone.Companions;
using Server.Items;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// Phase 4e G5: a weapon a player gives her deals its own damage; otherwise (her own dagger, or bare hands) she keeps her
// own fixed damage, exactly as before 4e.
[Collection("Sequential UOContent Tests")]
public class CompanionGearDamageTests
{
    private static (ElspethVale E, PlayerMobile P) Scene()
    {
        CompanionGear.ClearForTests();
        var e = new ElspethVale();
        e.MoveToWorld(new Point3D(1400, 1600, 0), Map.Felucca);
        var p = new PlayerMobile { Name = "Racer X" };
        p.DefaultMobileInit();
        p.AddItem(new Backpack { Movable = false });
        p.MoveToWorld(new Point3D(1401, 1600, 0), Map.Felucca);
        e.SetControlMaster(p);
        CompanionGear.SeedStarters(e);
        return (e, p);
    }

    private static (int, int) Range(BaseCreature c)
    {
        ((BaseWeapon)c.Weapon).GetBaseDamageRange(c, out var min, out var max);
        return (min, max);
    }

    [Fact]
    public void AGivenWeaponDealsItsOwnDamageAndHerOwnComesBackAfter()
    {
        var (e, p) = Scene();
        try
        {
            Assert.Equal((4, 8), Range(e));                      // her own dagger: unchanged from before 4e

            var sword = new Longsword();
            p.Backpack.DropItem(sword);
            Assert.True(e.OnDragDrop(p, sword));
            Assert.Same(sword, e.Weapon);
            Assert.Equal((sword.MinDamage, sword.MaxDamage), Range(e));

            Assert.True(e.TryGearRequest(p, "give me back the sword"));
            Assert.Equal((4, 8), Range(e));
        }
        finally
        {
            e.Delete();
            p.Delete();
            CompanionGear.ClearForTests();
        }
    }

    [Fact]
    public void TheCheckPutsItRightAfterLoad()
    {
        var (e, p) = Scene();
        try
        {
            e.SetDamage(-1, -1);                                 // as if saved while holding a gift that is gone now
            CompanionGear.Reconcile("test");
            Assert.Equal(4, e.DamageMin);
            Assert.Equal(8, e.DamageMax);
        }
        finally
        {
            e.Delete();
            p.Delete();
            CompanionGear.ClearForTests();
        }
    }
}
