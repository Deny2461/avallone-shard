using System;
using System.Collections.Generic;
using Server;
using Server.Avallone.AI;
using Server.Avallone.Companions;
using Server.Avallone.Named;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// 5c-2 2b, step 1 (shard plumbing): one npc id per NPC (Codex 2b review F2), the standing block, daily evidence (F5/F7/F8).
[Collection("Sequential UOContent Tests")]
public class RelationsTests
{
    private static T Place<T>(T m) where T : Mobile
    {
        m.MoveToWorld(new Point3D(1400, 1600, 0), Map.Felucca);
        return m;
    }

    private static PlayerMobile NewPlayer(string name, int karma = 0, int fame = 0)
    {
        var m = new PlayerMobile { Name = name };
        m.DefaultMobileInit();
        m.Karma = karma;
        m.Fame = fame;
        m.MoveToWorld(new Point3D(1401, 1600, 0), Map.Felucca);
        return m;
    }

    private static List<(Guid Npc, Mobile Chr, string Kind, object Facts)> Capture()
    {
        var got = new List<(Guid, Mobile, string, object)>();
        Relations.ClearForTests();
        Relations.Recorder = (n, c, k, f) => { got.Add((n, c, k, f)); return true; };
        return got;
    }

    [Fact]
    public void MirabelUsesHerOwnNamedIdNotAiIdentity()
    {
        var mirabel = Place(new Mirabel());
        try
        {
            var named = ((INamedNpc)mirabel).AiId;
            Assert.NotEqual(Guid.Empty, named);
            Assert.Equal(named, Relations.NpcId(mirabel));
            Assert.False(AiIdentity.TryGet(mirabel, out _)); // no second id was created for her
        }
        finally
        {
            mirabel.Delete();
        }
    }

    // Codex 2b code review F1: no standing (so no ledger-based dials) until this run has reconciled with the brain.
    [Fact]
    public void NoStandingUntilTheLedgerHasReconciled()
    {
        var p = new PlayerMobile { Name = "Racer X" };
        p.DefaultMobileInit();
        p.MoveToWorld(new Point3D(1401, 1600, 0), Map.Felucca);
        var vendor = new Provisioner();
        vendor.MoveToWorld(new Point3D(1400, 1600, 0), Map.Felucca);
        try
        {
            using (Relations.LedgerReadyForTests(false))
            {
                Assert.Null(Relations.Standing(vendor, p, "Britain"));
            }

            using var ready = Relations.LedgerReadyForTests(true);
            Assert.NotNull(Relations.Standing(vendor, p, "Britain"));
        }
        finally
        {
            vendor.Delete();
            p.Delete();
        }
    }

    // Codex round 2, finding A: a full outbox before the first reconcile must not count as reconciled, and Standing stays
    // off until a real reconcile (AiLedger.Reconciled) - the default gate, not the test seam.
    [Fact]
    public void AFullOutboxBeforeReconcileKeepsStartingAndNoStanding()
    {
        var p = new PlayerMobile { Name = "Racer X" };
        p.DefaultMobileInit();
        p.MoveToWorld(new Point3D(1401, 1600, 0), Map.Felucca);
        var vendor = new Provisioner();
        vendor.MoveToWorld(new Point3D(1400, 1600, 0), Map.Felucca);
        try
        {
            AiLedger.ResetForTests(7, 2);
            Assert.True(AiLedger.TryRecord(Guid.NewGuid(), p, "visited", new { day = "2026-11-03", mpt = 1.0 }));
            Assert.True(AiLedger.TryRecord(Guid.NewGuid(), p, "visited", new { day = "2026-11-03", mpt = 1.0 }));
            Assert.False(AiLedger.TryRecord(Guid.NewGuid(), p, "visited", new { day = "2026-11-03", mpt = 1.0 })); // full
            Assert.Equal(AiLedger.LedgerState.Starting, AiLedger.State);   // still reconciles first
            Assert.False(AiLedger.Reconciled);
            Assert.Null(Relations.Standing(vendor, p, "Britain"));       // the real gate, no seam
        }
        finally
        {
            AiLedger.ResetForTests(0, 2000);
            vendor.Delete();
            p.Delete();
        }
    }

    // Codex 2b code review F3: a row that did not enter the outbox leaves the day open.
    [Fact]
    public void AFailedDailyWriteDoesNotUseUpTheDay()
    {
        var p = new PlayerMobile { Name = "Racer X" };
        p.DefaultMobileInit();
        p.MoveToWorld(new Point3D(1401, 1600, 0), Map.Felucca);
        var vendor = new Provisioner();
        vendor.MoveToWorld(new Point3D(1400, 1600, 0), Map.Felucca);
        var tries = 0;
        var saved = Relations.Recorder;
        try
        {
            Relations.ClearForTests();
            Relations.Recorder = (n, c, k, f) => ++tries > 1;   // the first write fails (outbox full), the second lands
            Relations.Traded(vendor, p);
            Relations.Traded(vendor, p);
            Relations.Traded(vendor, p);
            Assert.Equal(2, tries);                             // retried once, then the day is marked
        }
        finally
        {
            Relations.Recorder = saved;
            vendor.Delete();
            p.Delete();
        }
    }

    [Fact]
    public void CompanionsGetNoIdAndNoStanding()
    {
        var elspeth = Place(new ElspethVale());
        var p = NewPlayer("Gabby");
        try
        {
            Assert.Equal(Guid.Empty, Relations.NpcId(elspeth));
            Assert.Null(Relations.Standing(elspeth, p));
        }
        finally
        {
            elspeth.Delete();
            p.Delete();
        }
    }

    [Fact]
    public void TownspersonUsesAiIdentityAndWandererItsOwnId()
    {
        var vendor = Place(new Provisioner());
        var bram = Place(new BramHolloway());
        try
        {
            Assert.Equal(AiIdentity.GetOrCreate(vendor), Relations.NpcId(vendor));
            Assert.Equal(Relations.NpcId(vendor), Relations.NpcId(vendor));
            Assert.Equal(bram.AiId, Relations.NpcId(bram));
            Assert.NotEqual(Guid.Empty, bram.AiId);
        }
        finally
        {
            vendor.Delete();
            bram.Delete();
        }
    }

    [Fact]
    public void TradedIsRecordedOncePerDayUnderTheSameIdAsTalk()
    {
        var got = Capture();
        var mirabel = Place(new Mirabel());
        var vendor = Place(new Provisioner());
        var p = NewPlayer("Gabby");
        var q = NewPlayer("Racer");
        try
        {
            Relations.Traded(vendor, p);
            Relations.Traded(vendor, p);   // same day: nothing new
            Relations.Traded(vendor, q);   // another character: its own pair
            Relations.Traded(mirabel, p);  // her named id, the one her dialogue uses
            Assert.Equal(3, got.Count);
            Assert.All(got, g => Assert.Equal("traded", g.Kind));
            Assert.Equal(AiIdentity.GetOrCreate(vendor), got[0].Npc);
            Assert.Same(q, got[1].Chr);
            Assert.Equal(((INamedNpc)mirabel).AiId, got[2].Npc);
            Assert.Equal(NamedNpcs.Today(Core.Now), (string)got[0].Facts.GetType().GetProperty("day")!.GetValue(got[0].Facts));
            Assert.NotNull(got[0].Facts.GetType().GetProperty("mpt"));                  // 2b: warmth fades on mpt
        }
        finally
        {
            Relations.Recorder = AiLedger.TryRecord;
            mirabel.Delete();
            vendor.Delete();
            p.Delete();
            q.Delete();
        }
    }

    [Fact]
    public void VisitedIsDailyAndLeavesNamedNpcsToNamedNpcs()
    {
        var got = Capture();
        var mirabel = Place(new Mirabel());
        var vendor = Place(new Provisioner());
        var elspeth = Place(new ElspethVale());
        var p = NewPlayer("Gabby");
        try
        {
            Relations.Visited(mirabel, p);  // NamedNpcs.Ask records hers
            Relations.Visited(elspeth, p);  // companions: never
            Relations.Visited(vendor, p);
            Relations.Visited(vendor, p);
            Assert.Single(got);
            Assert.Equal("visited", got[0].Kind);
            Assert.Equal(AiIdentity.GetOrCreate(vendor), got[0].Npc);
        }
        finally
        {
            Relations.Recorder = AiLedger.TryRecord;
            mirabel.Delete();
            vendor.Delete();
            elspeth.Delete();
            p.Delete();
        }
    }

    [Fact]
    public void StandingCarriesClampedKarmaFameAndTheAmbience()
    {
        var vendor = Place(new Provisioner());
        var p = NewPlayer("Gabby", karma: -3200, fame: 20000);
        try
        {
            using var ready = Relations.LedgerReadyForTests(true);   // no brain in tests (Codex F1 gate)
            var s = Relations.Standing(vendor, p, "Britain");
            Assert.NotNull(s);
            Assert.Equal(-3200, s.Karma);
            Assert.Equal(15000, s.Fame);      // clamped to Titles.MaxFame
            Assert.InRange(s.Hour, 0, 23);
            Assert.False(string.IsNullOrEmpty(s.Moon));
            Assert.Equal("Britain", s.Town);
            Assert.Equal(Server.Avallone.Rumours.PlayedClock.Seconds, s.Pt);           // clocks ride along for fading
            Assert.Equal(Server.Avallone.Rumours.PlayedClock.MemorySeconds, s.Mpt);
            Assert.Null(Relations.Standing(vendor, vendor)); // not a player
        }
        finally
        {
            vendor.Delete();
            p.Delete();
        }
    }
}
