using Server;
using Server.Avallone.Companions;
using Server.Items;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// the owner, chat, 2026-11-03: Recall moves only bonded
// pets (BaseCreature.TeleportPets(..., onlyBonded: true)); a companion now counts as one - and only on the usual terms.
[Collection("Sequential UOContent Tests")]
public class CompanionRecallTests
{
    private static readonly Point3D Dest = new(1500, 1700, 0);

    private static (ElspethVale E, PlayerMobile P) Scene()
    {
        CompanionGear.ClearForTests();
        var p = new PlayerMobile { Name = "Racer X" };
        p.DefaultMobileInit();
        p.AddItem(new Backpack { Movable = false });
        p.MoveToWorld(new Point3D(1401, 1600, 0), Map.Felucca);
        var e = new ElspethVale();
        e.MoveToWorld(new Point3D(1400, 1600, 0), Map.Felucca);
        e.SetControlMaster(p);
        e.ControlOrder = OrderType.Follow;
        return (e, p);
    }

    [Fact]
    public void AFollowingCompanionGoesOnARecall()
    {
        var (e, p) = Scene();
        try
        {
            Assert.False(e.IsBonded);                        // never bonded - it travels anyway
            BaseCreature.TeleportPets(p, Dest, Map.Felucca, true);
            Assert.Equal(Dest, e.Location);
        }
        finally
        {
            e.Delete();
            p.Delete();
        }
    }

    [Fact]
    public void ATamedPetThatIsNotBondedStillStaysBehind()
    {
        var (e, p) = Scene();
        var horse = new Horse();
        horse.MoveToWorld(new Point3D(1402, 1600, 0), Map.Felucca);
        horse.SetControlMaster(p);
        horse.ControlOrder = OrderType.Follow;
        try
        {
            BaseCreature.TeleportPets(p, Dest, Map.Felucca, true);
            Assert.Equal(new Point3D(1402, 1600, 0), horse.Location);   // stock rule unchanged
        }
        finally
        {
            horse.Delete();
            e.Delete();
            p.Delete();
        }
    }

    [Fact]
    public void AWaitingOrFarCompanionStays()
    {
        var (e, p) = Scene();
        try
        {
            e.ControlOrder = OrderType.Stay;
            BaseCreature.TeleportPets(p, Dest, Map.Felucca, true);
            Assert.Equal(new Point3D(1400, 1600, 0), e.Location);
            e.ControlOrder = OrderType.Follow;
            e.MoveToWorld(new Point3D(1410, 1600, 0), Map.Felucca);   // 9 tiles away
            BaseCreature.TeleportPets(p, Dest, Map.Felucca, true);
            Assert.Equal(new Point3D(1410, 1600, 0), e.Location);
        }
        finally
        {
            e.Delete();
            p.Delete();
        }
    }
}
