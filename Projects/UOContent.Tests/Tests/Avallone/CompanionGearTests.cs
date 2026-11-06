using System;
using System.IO;
using Server;
using Server.Avallone.Companions;
using Server.Items;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// Phase 4e, G1: the custody record (CompanionGear). Codex round 2: starter kit recorded only on an exact match; the save
// format round-trips; a deleted item becomes a tombstone and is never recreated; a deleted giver keeps its Guid + name.
[Collection("Sequential UOContent Tests")]
public class CompanionGearTests
{
    private static ElspethVale NewElspeth()
    {
        var e = new ElspethVale();
        e.MoveToWorld(new Point3D(1400, 1600, 0), Map.Felucca);
        return e;
    }

    private static PlayerMobile NewPlayer(string name)
    {
        var m = new PlayerMobile { Name = name };
        m.DefaultMobileInit();
        m.MoveToWorld(new Point3D(1401, 1600, 0), Map.Felucca);
        return m;
    }

    private static GearRecord RoundTrip(GearRecord r)
    {
        var writer = new BufferWriter(true);
        CompanionGear.Write(writer, r);
        var bytes = writer.Buffer.AsSpan(0, (int)writer.Position).ToArray();
        return CompanionGear.Read(new BufferReader(bytes), 1);
    }

    [Fact]
    public void StartingKitIsRecordedExactlyOnce()
    {
        CompanionGear.ClearForTests();
        var e = NewElspeth();
        try
        {
            Assert.Equal(4, CompanionGear.SeedStarters(e));
            Assert.Equal(0, CompanionGear.SeedStarters(e)); // already seeded
            var dagger = e.FindItemOnLayer(Layer.OneHanded);
            Assert.NotNull(dagger);
            var r = CompanionGear.Get(dagger);
            Assert.Equal(GearOrigin.Starter, r.Origin);
            Assert.True(CompanionGear.IsProtected(dagger));
            Assert.Equal(4, CompanionGear.For(e).Count);
        }
        finally
        {
            e.Delete();
            CompanionGear.ClearForTests();
        }
    }

    [Fact]
    public void UnexpectedKitIsNotGuessed()
    {
        CompanionGear.ClearForTests();
        var e = NewElspeth();
        try
        {
            e.FindItemOnLayer(Layer.Waist)?.Delete();           // apron missing
            var hat = new FloppyHat();
            e.AddItem(hat);                                      // something she was never given
            Assert.Equal(3, CompanionGear.SeedStarters(e));
            Assert.Null(CompanionGear.Get(hat));
            Assert.False(CompanionGear.IsProtected(hat));
        }
        finally
        {
            e.Delete();
            CompanionGear.ClearForTests();
        }
    }

    [Fact]
    public void GivenRecordRoundTripsTheSaveFormat()
    {
        CompanionGear.ClearForTests();
        var e = NewElspeth();
        var p = NewPlayer("Racer X");
        var sword = new Longsword();
        try
        {
            e.AddItem(sword);
            var id = Guid.NewGuid();
            var r = CompanionGear.RecordGiven(e, sword, p, id);
            var back = RoundTrip(r);

            Assert.Equal(sword.Serial, back.ItemSerial);
            Assert.Same(sword, back.Item);
            Assert.Equal(GearOrigin.Given, back.Origin);
            Assert.Equal(GearState.OnCompanion, back.State);
            Assert.Same(e, back.Companion);
            Assert.Same(p, back.Giver);
            Assert.Equal(id, back.GiverId);
            Assert.Equal("Racer X", back.GiverName);
            Assert.Equal(r.Layer, back.Layer);
            Assert.Equal(r.AcceptedUtc, back.AcceptedUtc);
            Assert.True(CompanionGear.IsProtected(sword));
        }
        finally
        {
            sword.Delete();
            e.Delete();
            p.Delete();
            CompanionGear.ClearForTests();
        }
    }

    [Fact]
    public void DeletedItemBecomesATombstoneAndIsNeverRecreated()
    {
        CompanionGear.ClearForTests();
        var e = NewElspeth();
        var p = NewPlayer("Giver");
        var sword = new Longsword();
        try
        {
            e.AddItem(sword);
            var serial = sword.Serial;
            var r = CompanionGear.RecordGiven(e, sword, p, Guid.NewGuid());
            var itemsBefore = World.Items.Count;

            sword.Delete();                                      // round 3: the guard refuses an ordinary delete
            Assert.False(sword.Deleted);
            CompanionGear.DeleteBypassingGuardForTests(sword);  // something unforeseen gets past it anyway
            Assert.Equal(1, CompanionGear.TombstoneMissing("test"));
            Assert.Equal(GearState.Tombstone, r.State);
            Assert.Null(r.Item);
            Assert.Equal(itemsBefore - 1, World.Items.Count);   // nothing created in its place

            var back = RoundTrip(r);
            Assert.Equal(serial, back.ItemSerial);               // the evidence survives a save
            Assert.Equal(GearState.Tombstone, back.State);
            Assert.Null(back.Item);
            Assert.False(CompanionGear.IsProtected(sword));
        }
        finally
        {
            e.Delete();
            p.Delete();
            CompanionGear.ClearForTests();
        }
    }

    [Fact]
    public void DeletedGiverKeepsIdentityAndName()
    {
        CompanionGear.ClearForTests();
        var e = NewElspeth();
        var p = NewPlayer("Gone Soon");
        var sword = new Longsword();
        try
        {
            e.AddItem(sword);
            var id = Guid.NewGuid();
            var r = CompanionGear.RecordGiven(e, sword, p, id);
            p.Delete();

            var back = RoundTrip(r);
            Assert.Null(back.Giver);
            Assert.Equal(id, back.GiverId);
            Assert.Equal("Gone Soon", back.GiverName);
            Assert.Same(sword, back.Item);
        }
        finally
        {
            sword.Delete();
            e.Delete();
            CompanionGear.ClearForTests();
        }
    }
}
