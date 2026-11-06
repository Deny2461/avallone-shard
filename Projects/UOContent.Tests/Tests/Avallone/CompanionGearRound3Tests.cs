using System;
using System.Diagnostics;
using System.Linq;
using Server;
using Server.Avallone.AI;
using Server.Avallone.Companions;
using Server.Items;
using Server.Mobiles;
using Xunit;
using Xunit.Abstractions;

namespace UOContent.Tests;

// Phase 4e fixes after Codex's code review (round 3, 2026-10-24). One test group per finding; each follows the
// ORIGINAL item serial and checks that nothing is lost, copied or closed early.
[Collection("Sequential UOContent Tests")]
public class CompanionGearRound3Tests
{
    private readonly ITestOutputHelper _out;

    public CompanionGearRound3Tests(ITestOutputHelper output) => _out = output;

    private sealed class Scene : IDisposable
    {
        public readonly ElspethVale E;
        public readonly PlayerMobile P;
        public readonly PlayerMobile Gm;

        public Scene()
        {
            CompanionGear.ClearForTests();
            E = new ElspethVale();
            E.MoveToWorld(new Point3D(1400, 1600, 0), Map.Felucca);
            E.RawStr = 120;
            P = NewPlayer("Racer X", 1401);
            Gm = NewPlayer("Wren GM", 1402);
            Gm.AccessLevel = AccessLevel.GameMaster;
            E.SetControlMaster(P);
            CompanionGear.SeedStarters(E);
        }

        public static PlayerMobile NewPlayer(string name, int x)
        {
            var m = new PlayerMobile { Name = name };
            m.DefaultMobileInit();
            m.AddItem(new Backpack { Movable = false });
            m.MoveToWorld(new Point3D(x, 1600, 0), Map.Felucca);
            return m;
        }

        public T Give<T>(T item) where T : Item
        {
            P.Backpack.DropItem(item);
            Assert.True(E.OnDragDrop(P, item));
            return item;
        }

        public void Dispose()
        {
            CompanionGear.IsOnline = m => m.NetState != null;
            if (!E.Deleted)
            {
                E.Delete();
            }

            P.Delete();
            Gm.Delete();
            CompanionGear.ClearForTests();
        }
    }

    // ---- P0-1: box recovery keeps custody until the item is verified with its giver ---------------------------

    [Fact]
    public void GmReturnSendsItStraightToTheOnlineGiver()
    {
        using var s = new Scene();
        var sword = s.Give(new Longsword());
        var serial = sword.Serial;
        s.E.Delete();
        CompanionGear.IsOnline = _ => true;

        CompanionGear.GearboxCommand(s.Gm, ["gearbox", "return", "1"]);

        Assert.Same(s.P.Backpack, sword.Parent);
        Assert.Equal(serial, sword.Serial);
        Assert.Null(CompanionGear.Get(sword));
        Assert.Empty(CompanionGearBox.Current.Items);
        Assert.NotSame(s.Gm.Backpack, sword.Parent);
    }

    [Fact]
    public void GmReturnWaitsWhileTheGiverIsOffline()
    {
        using var s = new Scene();
        var sword = s.Give(new Longsword());
        s.E.Delete();
        CompanionGear.IsOnline = _ => false;

        CompanionGear.GearboxCommand(s.Gm, ["gearbox", "return", "1"]);

        Assert.Same(CompanionGearBox.Current, sword.Parent);
        Assert.Equal(GearState.InHoldingBox, CompanionGear.Get(sword).State);
    }

    [Fact]
    public void GmReturnWithTheGiverGoneLeavesItInTheBox()
    {
        using var s = new Scene();
        var sword = s.Give(new Longsword());
        s.E.Delete();
        s.P.Delete();
        CompanionGear.IsOnline = _ => true;

        CompanionGear.GearboxCommand(s.Gm, ["gearbox", "return", "1"]);

        Assert.Same(CompanionGearBox.Current, sword.Parent);
        Assert.NotNull(CompanionGear.Get(sword));
    }

    [Fact]
    public void OldTakeCommandMovesNothing()
    {
        using var s = new Scene();
        var sword = s.Give(new Longsword());
        s.E.Delete();

        CompanionGear.GearboxCommand(s.Gm, ["gearbox", "take", "1"]);

        Assert.Same(CompanionGearBox.Current, sword.Parent);
        Assert.NotNull(CompanionGear.Get(sword));
    }

    [Fact]
    public void TakenOutOfTheBoxByOtherMeansIsPutBackWithItsRecord()
    {
        using var s = new Scene();
        var sword = s.Give(new Longsword());
        s.E.Delete();

        s.Gm.Backpack.DropItem(sword);                       // a GM drags it out by hand
        Assert.NotNull(CompanionGear.Get(sword));            // record kept at once
        Assert.Equal(1, CompanionGear.Reconcile("test"));

        Assert.Same(CompanionGearBox.Current, sword.Parent);
        Assert.Equal(GearState.InHoldingBox, CompanionGear.Get(sword).State);
    }

    [Fact]
    public void ABoxItemThatReachedItsGiverIsFinished()
    {
        using var s = new Scene();
        var sword = s.Give(new Longsword());
        s.E.Delete();

        s.P.Backpack.DropItem(sword);
        Assert.Equal(1, CompanionGear.Reconcile("test"));

        Assert.Null(CompanionGear.Get(sword));
        Assert.Same(s.P.Backpack, sword.Parent);
    }

    [Fact]
    public void DisposeNeedsAnAdministratorAndConfirm()
    {
        using var s = new Scene();
        var sword = s.Give(new Longsword());
        s.E.Delete();

        CompanionGear.GearboxCommand(s.Gm, ["gearbox", "dispose", "1", "confirm"]); // GameMaster: refused
        Assert.False(sword.Deleted);

        s.Gm.AccessLevel = AccessLevel.Administrator;
        CompanionGear.GearboxCommand(s.Gm, ["gearbox", "dispose", "1"]);            // no confirm: refused
        Assert.False(sword.Deleted);

        CompanionGear.GearboxCommand(s.Gm, ["gearbox", "dispose", "1", "confirm"]);
        Assert.True(sword.Deleted);
        Assert.Null(CompanionGear.Get(sword));
    }

    // ---- P0-2: nothing deletes a player's gift ----------------------------------------------------------------

    [Fact]
    public void DeletingAWornGiftIsRefused()
    {
        using var s = new Scene();
        var sword = s.Give(new Longsword());

        sword.Delete();                                      // what [remove / [wipe do

        Assert.False(sword.Deleted);
        Assert.Same(s.E, sword.Parent);
        Assert.Equal(GearState.OnCompanion, CompanionGear.Get(sword).State);
    }

    [Fact]
    public void DeletingAGiftInHerPackIsRefused()
    {
        using var s = new Scene();
        var first = s.Give(new LeatherChest());
        s.Give(new StuddedChest());                          // the first chest goes to her pack
        Assert.Same(s.E.Backpack, first.Parent);

        first.Delete();

        Assert.False(first.Deleted);
        Assert.NotNull(CompanionGear.Get(first));
    }

    [Fact]
    public void DeletingAGiftInTheBoxIsRefusedAndTheRecordStays()
    {
        using var s = new Scene();
        var sword = s.Give(new Longsword());
        s.E.Delete();

        sword.Delete();

        Assert.False(sword.Deleted);
        Assert.Same(CompanionGearBox.Current, sword.Parent);
        Assert.Equal(GearState.InHoldingBox, CompanionGear.Get(sword).State);
    }

    [Fact]
    public void DeletingAGiftKeptOnTheInternalMapIsRefused()
    {
        using var s = new Scene();
        var sword = s.Give(new Longsword());
        CompanionGearBox.Broken = true;
        s.E.Delete();
        Assert.Equal(Map.Internal, sword.Map);

        sword.Delete();

        Assert.False(sword.Deleted);
        Assert.NotNull(CompanionGear.Get(sword));
        CompanionGearBox.Broken = false;
    }

    [Fact]
    public void HerOwnKitCanStillBeDeleted()
    {
        using var s = new Scene();
        var dagger = s.E.FindItemOnLayer(Layer.OneHanded);
        Assert.Equal(GearOrigin.Starter, CompanionGear.Get(dagger).Origin);

        dagger.Delete();

        Assert.True(dagger.Deleted);                         // only players' gifts are protected
    }

    [Fact]
    public void AReturnedItemIsOrdinaryAgain()
    {
        using var s = new Scene();
        var sword = s.Give(new Longsword());
        Assert.True(s.E.TryGearRequest(s.P, "Elspeth, give me back my longsword"));
        Assert.Same(s.P.Backpack, sword.Parent);

        sword.Delete();

        Assert.True(sword.Deleted);
    }

    // ---- P1-1: ordinary items only, old-style magic included ----------------------------------------------------

    [Theory]
    [InlineData("force")]
    [InlineData("accurate")]
    [InlineData("indestructible")]
    [InlineData("poisoned")]
    [InlineData("cursed")]
    [InlineData("consecrated")]
    [InlineData("defense-armour")]
    [InlineData("fortified-armour")]
    [InlineData("str-bonus-armour")]
    [InlineData("aos-clothing")]
    public void OldStyleMagicIsRefused(string kind)
    {
        using var s = new Scene();
        Item item = kind switch
        {
            "force"          => new Longsword { DamageLevel = WeaponDamageLevel.Force },
            "accurate"       => new Longsword { AccuracyLevel = WeaponAccuracyLevel.Accurate },
            "indestructible" => new Longsword { DurabilityLevel = WeaponDurabilityLevel.Indestructible },
            "poisoned"       => new Kryss { Poison = Poison.Lesser, PoisonCharges = 10 },
            "cursed"         => new Longsword { Cursed = true },
            "consecrated"    => new Longsword { Consecrated = true },
            "defense-armour" => new LeatherChest { ProtectionLevel = ArmorProtectionLevel.Defense },
            "fortified-armour" => new LeatherChest { Durability = ArmorDurabilityLevel.Fortified },
            "str-bonus-armour" => new LeatherChest { StrBonus = 5 },
            _                => new Cloak()
        };
        if (item is Cloak c)
        {
            c.Attributes.Luck = 50;
        }

        s.P.Backpack.DropItem(item);
        Assert.False(s.E.OnDragDrop(s.P, item));
        Assert.Same(s.P.Backpack, item.Parent);
        Assert.Null(CompanionGear.Get(item));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("exceptional")]
    [InlineData("dull-copper")]
    [InlineData("barbed-leather")]
    public void OrdinaryAndCraftedItemsAreAccepted(string kind)
    {
        // the owner, chat, 2026-10-24: exceptional quality and coloured materials count as ordinary.
        using var s = new Scene();
        Item item = kind switch
        {
            "plain"          => new Longsword(),
            "exceptional"    => new Longsword { Quality = WeaponQuality.Exceptional },
            "dull-copper"    => new RingmailChest { Resource = CraftResource.DullCopper },
            _                => new LeatherChest { Resource = CraftResource.BarbedLeather }
        };

        s.P.Backpack.DropItem(item);
        Assert.True(s.E.OnDragDrop(s.P, item));
        Assert.Same(s.E, item.Parent);
    }

    // ---- P1-2: ordinary talk is never swallowed ------------------------------------------------------------------

    [Theory]
    [InlineData("Elspeth, can you give me the history of Britain?")]
    [InlineData("Elspeth, I would like you back at home.")]
    [InlineData("Elspeth, I need you back here to talk about armor.")]
    [InlineData("I want to go back to town")]
    [InlineData("Elspeth, I'll give you my old cloak later")]
    [InlineData("hand her the bandages")]
    [InlineData("we should head back before dark")]
    public void OrdinaryTalkGoesToTheModelAndMovesNothing(string line)
    {
        using var s = new Scene();
        var chest = s.Give(new LeatherChest());
        var sword = s.Give(new Longsword());

        Assert.False(s.E.TryGearRequest(s.P, line));

        Assert.Same(s.E, chest.Parent);
        Assert.Same(s.E, sword.Parent);
    }

    [Theory]
    [InlineData("elsbeth give me my tunic", "chest")]                // the owner's own words, typo'd name and all
    [InlineData("Elspeth, give me back the sword", "sword")]
    [InlineData("can I have my longsword back please", "sword")]
    [InlineData("return my leather chest", "chest")]
    [InlineData("give the sword back to me", "sword")]
    [InlineData("Elspth, hand me my armour back", "chest")]          // kind word counts with "my ... back"
    public void ClearRequestsReturnTheRightItem(string line, string which)
    {
        using var s = new Scene();
        var chest = s.Give(new LeatherChest());
        var sword = s.Give(new Longsword());

        Assert.True(s.E.TryGearRequest(s.P, line));

        var back = which == "chest" ? (Item)chest : sword;
        var stays = which == "chest" ? (Item)sword : chest;
        Assert.Same(s.P.Backpack, back.Parent);
        Assert.Same(s.E, stays.Parent);
    }

    [Theory]
    [InlineData("give me my")]
    [InlineData("give me my stuff back")]
    [InlineData("Elspeth, give me back my helmet")]                  // nothing called that: the list, not the model
    public void UnclearRequestsShowTheListAndMoveNothing(string line)
    {
        using var s = new Scene();
        var chest = s.Give(new LeatherChest());
        var sword = s.Give(new Longsword());

        Assert.True(s.E.TryGearRequest(s.P, line));

        Assert.Same(s.E, chest.Parent);
        Assert.Same(s.E, sword.Parent);
    }

    // ---- P2: duplicate boxes and save versions -------------------------------------------------------------------

    [Fact]
    public void WithTwoBoxesNoRecordIsClosed()
    {
        using var s = new Scene();
        var sword = s.Give(new Longsword());
        var cloak = s.Give(new Cloak());
        s.E.Delete();                                        // both into box 1
        var second = new CompanionGearBox();
        second.MoveToWorld(Point3D.Zero, Map.Internal);
        second.DropItem(cloak);                              // one moved into a stray second box
        CompanionGearBox.FindAfterLoad();
        Assert.True(CompanionGearBox.Broken);

        CompanionGear.Reconcile("test");

        Assert.Equal(GearState.InHoldingBox, CompanionGear.Get(sword).State);
        Assert.Equal(GearState.InHoldingBox, CompanionGear.Get(cloak).State);
        Assert.Same(second, cloak.Parent);                   // nothing moved while broken
    }

    [Fact]
    public void AnUnknownSaveVersionIsRefused()
    {
        CompanionGear.CheckVersion(1);
        Assert.Throws<InvalidOperationException>(() => CompanionGear.CheckVersion(2));
        Assert.Throws<InvalidOperationException>(() => CompanionGear.CheckVersion(0));
    }

    // ---- P1-3: one name computation per spoken line, even in a crowd ----------------------------------------------

    [Fact]
    public void OneNameComputationPerSpokenLineInACrowd()
    {
        CompanionGear.ClearForTests();
        var p = Scene.NewPlayer("Racer X", 3000);
        var crowd = new Provisioner[40];
        try
        {
            for (var i = 0; i < crowd.Length; i++)
            {
                crowd[i] = new Provisioner { Name = $"Ailiynn{(char)('a' + i % 26)}{i}" };
                crowd[i].MoveToWorld(new Point3D(2997 + i % 7, 1597 + i / 7, 0), Map.Felucca);
            }

            // 40 tokens, none an English word, so every NPC is checked for a near miss on every token.
            var line = string.Join(' ', Enumerable.Range(0, 40).Select(i => $"zqx{(char)('a' + i % 26)}{(char)('a' + i / 26)}"));
            var e = new SpeechEventArgs(p, line, MessageType.Regular, 0, []);
            NameResolver.ResetCacheForTests();
            NameResolver.BeginSpeech(e);
            var before = NameResolver.ComputeCount;
            var sw = Stopwatch.StartNew();

            for (var i = 0; i < crowd.Length + 5; i++)       // every NPC handler plus the townsfolk listener asks
            {
                NameResolver.Resolve(p, line);
            }

            sw.Stop();
            Assert.Equal(1, NameResolver.ComputeCount - before);
            _out.WriteLine($"40 NPCs, 40-token line: {sw.Elapsed.TotalMilliseconds:F2} ms for one computation + {crowd.Length + 4} cache hits");
            Assert.True(sw.Elapsed.TotalMilliseconds < 250, $"took {sw.Elapsed.TotalMilliseconds:F1} ms");

            var next = new SpeechEventArgs(p, line, MessageType.Regular, 0, []);  // the same words said again
            NameResolver.BeginSpeech(next);
            NameResolver.Resolve(p, line);
            Assert.Equal(2, NameResolver.ComputeCount - before);                  // is worked out afresh
        }
        finally
        {
            foreach (var v in crowd)
            {
                v?.Delete();
            }

            p.Delete();
            NameResolver.ResetCacheForTests();
        }
    }

    // ---- Codex round 4, P1: Administrator dispose fails closed ------------------------------------------------

    private sealed class StubbornSword : Longsword
    {
        public bool Refuse = true;

        public override void Delete()
        {
            if (!Refuse)
            {
                base.Delete();
            }
        }
    }

    private sealed class ThrowingSword : Longsword
    {
        public bool Throw = true;

        public override void Delete()
        {
            if (Throw)
            {
                throw new InvalidOperationException("test: delete blew up");
            }

            base.Delete();
        }
    }

    // While it is being destroyed, it tries to destroy a different gift (a re-entrant delete).
    private sealed class GrabbySword : Longsword
    {
        public Item Other;

        public override void Delete()
        {
            Other?.Delete();
            base.Delete();
        }
    }

    private static Mobile Admin(Scene s)
    {
        s.Gm.AccessLevel = AccessLevel.Administrator;
        return s.Gm;
    }

    [Fact]
    public void DisposeKeepsTheRecordWhenTheItemWillNotDelete()
    {
        using var s = new Scene();
        var sword = s.Give(new StubbornSword());
        try
        {
            s.E.Delete();
            var before = CompanionGear.Get(sword);
            Assert.Equal(GearState.InHoldingBox, before.State);

            Assert.False(CompanionGear.DisposeFromBox(Admin(s), before, true));

            Assert.False(sword.Deleted);
            Assert.Same(before, CompanionGear.Get(sword));
            Assert.Equal(GearState.InHoldingBox, before.State);
        }
        finally
        {
            sword.Refuse = false; // so cleanup can remove it, pass or fail
            CompanionGear.DeleteBypassingGuardForTests(sword);
        }
    }

    [Fact]
    public void DisposeKeepsTheRecordWhenTheDeleteThrows()
    {
        using var s = new Scene();
        var sword = s.Give(new ThrowingSword());
        try
        {
            s.E.Delete();
            var before = CompanionGear.Get(sword);

            Assert.False(CompanionGear.DisposeFromBox(Admin(s), before, true));

            Assert.False(sword.Deleted);
            Assert.Same(before, CompanionGear.Get(sword));
            Assert.Equal(GearState.InHoldingBox, before.State);

            // The guard is back on afterwards: an ordinary delete is refused.
            sword.Throw = false;
            sword.Delete();
            Assert.False(sword.Deleted);
        }
        finally
        {
            sword.Throw = false; // so cleanup can remove it, pass or fail
            CompanionGear.DeleteBypassingGuardForTests(sword);
        }
    }

    [Fact]
    public void DisposeUnlocksOnlyTheChosenGift()
    {
        using var s = new Scene();
        var other = s.Give(new Longsword());
        var grabby = s.Give(new GrabbySword());
        grabby.Other = other;
        s.E.Delete();

        Assert.True(CompanionGear.DisposeFromBox(Admin(s), CompanionGear.Get(grabby), true));

        Assert.True(grabby.Deleted);
        Assert.Null(CompanionGear.Get(grabby));
        Assert.False(other.Deleted);                 // the re-entrant delete was refused
        Assert.NotNull(CompanionGear.Get(other));
        Assert.Equal(GearState.InHoldingBox, CompanionGear.Get(other).State);
    }
}
