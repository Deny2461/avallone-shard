using Server;
using Server.Avallone;
using Server.Engines.Craft;
using Server.Engines.Harvest;
using Server.Factions;
using Server.Items;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// the owner, 2026-10-17: a failed craft loses half its materials and never silver; coloured woods without Mondain's Legacy.
[Collection("Sequential UOContent Tests")]
public class CraftFailAndWoodTests : System.IDisposable
{
    // Every test runs as the live shard does: Samurai Empire, so Core.ML is false.
    private readonly Expansion _saved = Core.Expansion;

    public CraftFailAndWoodTests() => Core.Expansion = Expansion.SE;

    public void Dispose()
    {
        Core.Expansion = _saved;
        AvalloneRules.FailedCraftLosesHalf = true;
        AvalloneRules.ColoredWoods = true;
    }

    [Fact]
    public void RunsAsSamuraiEmpire() => Assert.False(Core.ML);

    private static PlayerMobile Player(int x)
    {
        var m = new PlayerMobile(World.NewMobile);
        m.DefaultMobileInit();
        m.MoveToWorld(new Point3D(x, 600, 0), Map.Felucca);
        m.AddItem(new Backpack());
        m.Skills.Tinkering.Base = 50.0; // equal to the recipe minimum: 0% chance, so every attempt fails
        return m;
    }

    private static CraftSystem Tinkering()
    {
        if (DefTinkering.CraftSystem == null)
        {
            DefTinkering.Initialize();
        }

        return DefTinkering.CraftSystem;
    }

    private static CraftItem Recipe(params (System.Type type, int amount)[] res)
    {
        var item = new CraftItem(typeof(GoldRing), "test", "test ring");
        item.AddSkill(SkillName.Tinkering, 50.0, 100.0);
        foreach (var (type, amount) in res)
        {
            item.AddRes(type, "res", amount, "You do not have enough.");
        }

        return item;
    }

    [Theory]
    [InlineData(true, 15, 12)]  // colour-matching path: 15 needed, 8 lost (half rounded up), 12 of 20 left
    [InlineData(false, 15, 12)] // plain path, same rule
    [InlineData(true, 1, 19)]   // one needed: still costs one
    public void FailedCraft_LosesHalfRoundedUp(bool hued, int needed, int left)
    {
        AvalloneRules.FailedCraftLosesHalf = true;
        var player = Player(4200 + needed + (hued ? 0 : 40));
        var tool = new TinkerTools();
        try
        {
            player.Backpack.AddItem(new IronIngot(20));
            player.Backpack.AddItem(tool);
            var recipe = Recipe((typeof(IronIngot), needed));

            if (hued)
            {
                recipe.CompleteCraft(1, false, player, Tinkering(), typeof(IronIngot), tool, null, 0);
            }
            else
            {
                recipe.CompleteCraft(1, false, player, Tinkering(), typeof(IronIngot), tool, null);
            }

            Assert.Equal(left, player.Backpack.GetAmount(typeof(IronIngot)));
        }
        finally
        {
            tool.Delete();
            player.Delete();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedCraft_NeverLosesSilver(bool hued)
    {
        AvalloneRules.FailedCraftLosesHalf = true;
        var player = Player(4300 + (hued ? 0 : 20));
        var tool = new TinkerTools();
        try
        {
            player.Backpack.AddItem(new IronIngot(10));
            player.Backpack.AddItem(new Silver(10));
            player.Backpack.AddItem(tool);
            var recipe = Recipe((typeof(IronIngot), 4), (typeof(Silver), 6));

            if (hued)
            {
                recipe.CompleteCraft(1, false, player, Tinkering(), typeof(IronIngot), tool, null, 0);
            }
            else
            {
                recipe.CompleteCraft(1, false, player, Tinkering(), typeof(IronIngot), tool, null);
            }

            Assert.Equal(10, player.Backpack.GetAmount(typeof(Silver)));
            Assert.Equal(8, player.Backpack.GetAmount(typeof(IronIngot)));
        }
        finally
        {
            tool.Delete();
            player.Delete();
        }
    }

    [Fact]
    public void FailedCraft_SettingOff_IsStockLoseAll()
    {
        AvalloneRules.FailedCraftLosesHalf = false;
        var player = Player(4400);
        var tool = new TinkerTools();
        try
        {
            player.Backpack.AddItem(new IronIngot(20));
            player.Backpack.AddItem(tool);
            Recipe((typeof(IronIngot), 15)).CompleteCraft(1, false, player, Tinkering(), typeof(IronIngot), tool, null, 0);
            Assert.Equal(5, player.Backpack.GetAmount(typeof(IronIngot)));
        }
        finally
        {
            AvalloneRules.FailedCraftLosesHalf = true;
            tool.Delete();
            player.Delete();
        }
    }

    [Fact]
    public void ColoredWoods_LumberjackingOffersSevenWoods_NoBonusFinds()
    {
        AvalloneRules.ColoredWoods = true;
        var def = ((Lumberjacking)System.Activator.CreateInstance(typeof(Lumberjacking), true)).Definitions[0];
        Assert.Equal(7, def.Resources.Length);
        Assert.Equal(typeof(FrostwoodLog), def.Resources[6].Types[0]);
        Assert.Null(def.BonusResources);
    }

    [Fact]
    public void ColoredWoods_Off_IsStockSingleWood()
    {
        AvalloneRules.ColoredWoods = false;
        var def = ((Lumberjacking)System.Activator.CreateInstance(typeof(Lumberjacking), true)).Definitions[0];
        Assert.Single(def.Resources);
    }

    [Theory]
    [InlineData(true, 10, 5)]
    [InlineData(false, 0, 0)]
    public void ColoredWoods_YewBowBonusFollowsSetting(bool on, int damage, int hitChance)
    {
        AvalloneRules.ColoredWoods = on;
        var player = Player(4500);
        var bow = new Bow();
        try
        {
            if (DefBowFletching.CraftSystem == null)
            {
                DefBowFletching.Initialize();
            }

            bow.OnCraft(1, false, player, DefBowFletching.CraftSystem, typeof(YewLog), null, null, 0);
            Assert.Equal(damage, bow.Attributes.WeaponDamage);
            Assert.Equal(hitChance, bow.Attributes.AttackChance);
        }
        finally
        {
            bow.Delete();
            player.Delete();
        }
    }
}
