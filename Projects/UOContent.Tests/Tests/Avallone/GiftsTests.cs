using System;
using System.Collections.Generic;
using Server;
using Server.Avallone.AI;
using Server.Avallone.Companions;
using Server.Items;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// 5c-2 2b step 2: gifts and tips (design B5 + B12 #3-#4, Codex 2b review F3/F4 acceptance on the shard side).
[Collection("Sequential UOContent Tests")]
public class GiftsTests
{
    private const string Likes = """
        { "tip": {"min": 10, "max": 250}, "default": {"say": "a coin or two never goes amiss"},
          "roles": [ {"match": ["smith"], "say": "good ore or ingots", "items": ["BaseIngot", "BaseOre"]} ] }
        """;

    private sealed class Rig : IDisposable
    {
        public readonly List<(Guid Npc, string Kind, object Facts)> Got = [];
        public readonly Blacksmith Smith;
        public readonly PlayerMobile P;
        public bool Fail;

        public Rig()
        {
            Gifts.LoadLikes(Likes);
            Gifts.ClearForTests();
            Gifts.Recorder = (n, c, k, f) =>
            {
                if (Fail)
                {
                    return false;
                }

                Got.Add((n, k, f));
                return true;
            };
            Smith = new Blacksmith();
            Smith.MoveToWorld(new Point3D(1400, 1600, 0), Map.Felucca);
            P = new PlayerMobile { Name = "Gabby" };
            P.DefaultMobileInit();
            P.MoveToWorld(new Point3D(1401, 1600, 0), Map.Felucca);
        }

        public void Dispose()
        {
            Gifts.Recorder = AiLedger.TryRecord;
            Smith.Delete();
            P.Delete();
        }
    }

    [Fact]
    public void ATipIsTakenOnceADayThroughTheStockDropPath()
    {
        using var r = new Rig();
        var tip = new Gold(50);
        Assert.True(r.Smith.OnDragDrop(r.P, tip));      // BaseVendor -> BaseCreature -> OnGoldGiven -> Gifts
        Assert.True(tip.Deleted);
        Assert.Single(r.Got);
        Assert.Equal("gift_given", r.Got[0].Kind);
        Assert.Equal(Relations.NpcId(r.Smith), r.Got[0].Npc);
        // 2c (design M7.3): the played clock rides on every new gift row (mood fades gifts over played hours)
        Assert.Contains("\"pt\":", System.Text.Json.JsonSerializer.Serialize(r.Got[0].Facts));

        var second = new Gold(50);
        Assert.False(r.Smith.OnDragDrop(r.P, second));  // declined intact - one a day
        Assert.False(second.Deleted);
        Assert.Equal(50, second.Amount);
        Assert.Single(r.Got);
        second.Delete();
    }

    [Theory]
    [InlineData(5)]
    [InlineData(251)]
    [InlineData(60000)]
    public void TipsOutsideTheRangeAreDeclinedWholeAndUseNoSlot(int amount)
    {
        using var r = new Rig();
        var g = new Gold(amount);
        Assert.False(r.Smith.OnDragDrop(r.P, g));
        Assert.False(g.Deleted);
        Assert.Equal(amount, g.Amount);
        Assert.Empty(r.Got);
        Assert.False(Gifts.GaveToday(Relations.NpcId(r.Smith), AiIdentity.GetOrCreate(r.P)));
        g.Delete();
    }

    [Fact]
    public void NothingIsTakenWhenTheRecordCannotBeWritten()
    {
        using var r = new Rig();
        r.Fail = true;
        var g = new Gold(50);
        Assert.False(r.Smith.OnDragDrop(r.P, g));
        Assert.False(g.Deleted);
        Assert.False(Gifts.GaveToday(Relations.NpcId(r.Smith), AiIdentity.GetOrCreate(r.P)));
        r.Fail = false;
        Assert.True(r.Smith.OnDragDrop(r.P, g));        // the day's slot was not used by the failed try
        Assert.Single(r.Got);
    }

    [Fact]
    public void OneUnitOfALikedStackIsTakenAndTheRestGoesBack()
    {
        using var r = new Rig();
        var ingots = new IronIngot(5);
        Assert.False(r.Smith.OnDragDrop(r.P, ingots));  // false: the remainder bounces back to the player
        Assert.False(ingots.Deleted);
        Assert.Equal(4, ingots.Amount);
        Assert.Single(r.Got);
        ingots.Delete();
    }

    [Fact]
    public void UnwantedProtectedAndContainerGiftsAreDeclinedIntact()
    {
        using var r = new Rig();
        var bandage = new Bandage(3);
        Assert.False(r.Smith.OnDragDrop(r.P, bandage));  // not on a smith's list
        Assert.Equal(3, bandage.Amount);

        var blessed = new IronIngot(1) { LootType = LootType.Blessed };
        Assert.False(r.Smith.OnDragDrop(r.P, blessed));
        Assert.False(blessed.Deleted);

        var bag = new Bag();
        bag.DropItem(new IronIngot(1));
        Assert.False(r.Smith.OnDragDrop(r.P, bag));
        Assert.False(bag.Deleted);

        Assert.Empty(r.Got);
        Assert.False(Gifts.GaveToday(Relations.NpcId(r.Smith), AiIdentity.GetOrCreate(r.P)));
        bandage.Delete();
        blessed.Delete();
        bag.Delete();
    }

    [Fact]
    public void CompanionsAndNonPlayersAreNotGiftTargets()
    {
        using var r = new Rig();
        var elspeth = new ElspethVale();
        elspeth.MoveToWorld(new Point3D(1402, 1600, 0), Map.Felucca);
        try
        {
            Assert.False(Gifts.Handles(elspeth, r.P));
            Assert.False(Gifts.Handles(r.Smith, r.Smith));
            Assert.True(Gifts.Handles(r.Smith, r.P));
        }
        finally
        {
            elspeth.Delete();
        }
    }

    [Fact]
    public void TheLikesSayLineReachesTheStandingBlock()
    {
        using var r = new Rig();
        using var ready = Relations.LedgerReadyForTests(true);   // no brain in tests (Codex F1 gate)
        Assert.Equal("good ore or ingots", Relations.Standing(r.Smith, r.P).Likes);
    }

    // the owner, chat, 2026-11-02: -> each non-persona NPC gets one odd like of its own.
    private const string LikesWithQuirks = """
        { "tip": {"min": 10, "max": 250}, "default": {"say": "a coin or two never goes amiss"},
          "roles": [ {"match": ["smith"], "say": "good ore or ingots", "items": ["BaseIngot", "BaseOre"]} ],
          "quirks": [ {"say": "a ripe apple", "items": ["Apple"]}, {"say": "a jar of honey", "items": ["JarHoney"]},
                      {"say": "a wheel of cheese", "items": ["CheeseWheel"]} ] }
        """;

    [Fact]
    public void EachNpcGetsOneStableQuirkAndTheyAreSpreadAcrossThePool()
    {
        try
        {
            Gifts.LoadLikes(LikesWithQuirks);
            var picks = new HashSet<string>();
            for (var i = 0; i < 60; i++)
            {
                var id = Guid.NewGuid();
                var q = Gifts.QuirkFor(id);
                Assert.NotNull(q);
                Assert.Same(q, Gifts.QuirkFor(id));     // same NPC -> same pick, every time
                picks.Add(q.Say);
            }

            Assert.Equal(3, picks.Count);                // not everyone gets the same one
            Assert.Null(Gifts.QuirkFor(Guid.Empty));      // no AI id -> no quirk
        }
        finally
        {
            Gifts.LoadLikes(Likes);
        }
    }

    [Fact]
    public void TheQuirkIsAddedToTheTradeLikesAndAQuirkGiftIsTaken()
    {
        using var r = new Rig();
        Gifts.LoadLikes("""
            { "roles": [ {"match": ["smith"], "say": "good ore or ingots", "items": ["BaseIngot"]} ],
              "quirks": [ {"say": "a ripe apple", "items": ["Apple"]} ] }
            """);
        try
        {
            var likes = Gifts.LikesFor(r.Smith);
            Assert.Equal("good ore or ingots - and I have a soft spot for a ripe apple", likes.Say);
            using var ready = Relations.LedgerReadyForTests(true);   // no brain in tests (Codex F1 gate)
            Assert.Equal(likes.Say, Relations.Standing(r.Smith, r.P).Likes);

            var apple = new Apple();
            Assert.True(r.Smith.OnDragDrop(r.P, apple));
            Assert.True(apple.Deleted);
            Assert.Single(r.Got);
        }
        finally
        {
            Gifts.LoadLikes(Likes);
        }
    }
}
