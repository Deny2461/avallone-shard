using System.Linq;
using System.Text.Json;
using Server;
using Server.Avallone.Named;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// 5c-2 2b design B8: [Named why]. The brain side (numbers) is tested in brain/test_dials.py (class Why).
[Collection("Sequential UOContent Tests")]
public class NamedWhyTests
{
    private const string Reply = """
        { "ok": true, "familiarity": 2, "warmth": 7.6, "trust": 3.1, "respect": -0.5, "likes": "good ore or ingots",
          "bands": {"familiarity": "once or twice", "warmth": "neutral", "trust": "as a stranger", "respect": "neutral"},
          "sources": {"karma": 120, "fame": 0, "trust_base": 0.0, "trust_env": 3.1, "respect_base": 0.0, "respect_env": 0.0,
                      "deeds": 0.0, "unease": 0.12, "ambience": 3.0, "hour": 22, "moon": "FullMoon", "town": "Britain",
                      "warmth_rest": 3.6, "warmth_events": 4.0, "trust_lean": -1, "respect_lean": 1,
                      "superstitious": true, "stories_live": 1},
          "detail": {"traits": {"trust_T": 1500.0, "trust_W": 2500.0, "trust_lean": -1, "respect_T": 800.0, "respect_W": 2000.0,
                                "respect_lean": 1, "superstitious": true},
                     "warmth_items": [{"kind": "visited", "day": "2026-11-02", "now": 2.0},
                                      {"kind": "gift", "day": "2026-11-02", "what": "liked", "value": "fair", "now": 2.0}],
                     "stories": [{"type": "player_died", "about_them": true, "arrived_h_ago": 1.5, "fades_in_h": 20.0}]},
          "mood": {"band": "weary", "strength": "a little", "cheer": 4.0, "energy": -36.5, "day_type": "weary",
                   "cause": "long_day", "gift_helped": true, "date": "2026-11-03", "pt": 44000, "hour": 22, "rules": "2c-1",
                   "cheer_parts": {"rest": 5.0, "day": 2.0, "gift": 8.5, "town_news": -1.8},
                   "energy_parts": {"rest": 0.0, "day": -48.0, "long_day": -8.0, "late_hour": 0.0},
                   "evidence": {"gifts_counted": 1, "gift_rows": 2, "visitors_today": 5, "unease": 0.12}} }
        """;

    [Fact]
    public void TheBreakdownNamesEachDialAndEachSource()
    {
        using var doc = JsonDocument.Parse(Reply);
        var lines = NamedWhy.Format("Nell", "Gabby Johnson", doc.RootElement);
        var all = string.Join("\n", lines);
        Assert.StartsWith("Why Nell feels as they do about Gabby Johnson (town Britain, hour 22, moon FullMoon)", lines[0]);
        Assert.Contains("Warmth 7.6 (neutral): their resting point 3.6 + visits, trades and gifts +4", all);
        Assert.Contains("gift (liked, fair) 2026-11-02: +2 now", all);
        Assert.Contains("Trust 3.1 (as a stranger): karma 120 against their line 1500 (width 2500, INVERTED)", all);
        Assert.Contains("superstitious", all);
        Assert.Contains("player_died (about them): arrived 1.5 h ago, fades in 20 h", all);
        Assert.Equal("Likes: good ore or ingots", lines.Last());
        Assert.Contains("Mood (same for everyone): a little weary - cheer +4, energy -36.5; day 2026-11-03 (weary); reason given: a gift helped", all);
        Assert.Contains("   energy: rest +0, day -48, long day -8, late hour +0", all);
        Assert.Contains("   5 visitors today, 1 of 2 gifts counting now; hour 22, rules 2c-1", all);
    }

    [Fact]
    public void NoMoodIsSaidPlainly()
    {
        using var doc = JsonDocument.Parse(Reply.Replace("\"mood\": {", "\"nomood\": {"));
        Assert.Contains("Mood: none (switched off, or the memory ledger is not ready)", NamedWhy.Format("Nell", "Gabby", doc.RootElement));
    }

    [Fact]
    public void ABrainRefusalIsShownAsOneLine()
    {
        using var doc = JsonDocument.Parse("""{"ok": false, "error": "no dials"}""");
        Assert.Equal(["Why: no dials"], NamedWhy.Format("Nell", "Gabby", doc.RootElement));
    }

    // the owner's evening dev test 2026-11-02:
    [Fact]
    public void NamedHomeMovesThemAndShortensTheLeash()
    {
        var m = new Server.Mobiles.Mirabel();
        m.MoveToWorld(new Point3D(1504, 1690, 20), Map.Trammel);
        try
        {
            var msg = NamedNpcs.SetHome(m, new Point3D(1500, 1686, 20), Map.Trammel, 2);
            Assert.Equal(new Point3D(1500, 1686, 20), m.Location);
            Assert.Equal(new Point3D(1500, 1686, 20), m.Home);
            Assert.Equal(2, m.RangeHome);
            Assert.Contains("at most 2 tiles", msg);
            NamedNpcs.SetHome(m, new Point3D(1500, 1686, 20), Map.Trammel, 99);
            Assert.Equal(10, m.RangeHome);                       // clamped
        }
        finally
        {
            m.Delete();
        }
    }

    [Theory]
    [InlineData("a ghost walks the docks", 0.0, "a ghost walks the docks", null)]
    [InlineData("unease=0.5 a ghost walks the docks", 0.5, "a ghost walks the docks", null)]
    [InlineData("unease=-1 the guard doubled", -1.0, "the guard doubled", null)]
    [InlineData("UNEASE=0.333 boo", 0.33, "boo", null)]
    public void PlantUneaseIsRead(string typed, double value, string text, string error)
    {
        var (v, t, e) = Server.Avallone.Rumours.RumourCommands.PlantUnease(typed);
        Assert.Equal(value, v);
        Assert.Equal(text, t);
        Assert.Equal(error, e);
    }

    [Theory]
    [InlineData("unease=2 boo")]
    [InlineData("unease=high boo")]
    [InlineData("unease-0.8 a ghost was seen walking the docks at night")]   // the owner's typo, 20:27 CST
    [InlineData("unease 0.8 boo")]
    public void BadPlantUneaseIsAnErrorNeverZero(string typed) =>
        Assert.NotNull(Server.Avallone.Rumours.RumourCommands.PlantUnease(typed).Error);

    [Theory]
    [InlineData("kill_notable_dragon", "kill_notable", "dragon")]   // an underscore for the space
    [InlineData("kill_noteable_dragon", "kill_notable", "dragon")]  // and a typo (the owner, 18:29 CST)
    [InlineData("murdr", "murder", "")]
    public void NearMissDeedTypesAreSuggested(string typed, string type, string rest)
    {
        var (t, r) = Server.Avallone.Rumours.RumourCommands.NearestType(typed);
        Assert.Equal(type, t);
        Assert.Equal(rest, r);
    }

    [Fact]
    public void NoNearMissForNonsense()
    {
        Assert.Null(Server.Avallone.Rumours.RumourCommands.NearestType("dragonslayer").Type);
    }

    // the owner's dev test 2026-11-02 13:08 CST: targeting Mirabel gave.
    [Fact]
    public void MirabelHasDialsAndAPersona()
    {
        var m = new Server.Mobiles.Mirabel();
        m.MoveToWorld(new Point3D(1400, 1600, 0), Map.Felucca);
        try
        {
            Assert.NotEqual(System.Guid.Empty, Server.Avallone.AI.Relations.NpcId(m));
            Assert.Equal("mirabel", NamedWhy.PersonaOf(m).Key);
            Assert.Null(NamedWhy.Refusal(m));
            Assert.Contains("a dog", NamedWhy.Refusal(new Dog()) ?? "");
        }
        finally
        {
            m.Delete();
        }
    }

    [Fact]
    public void ATownspersonIsAskedAboutAsTownsfolkWithItsRealRole()
    {
        var smith = new Blacksmith();
        smith.MoveToWorld(new Point3D(1400, 1600, 0), Map.Felucca);
        try
        {
            var (key, vars, town) = NamedWhy.PersonaOf(smith);
            Assert.Equal("townsfolk", key);
            Assert.Equal("blacksmith", vars["role"]);
            Assert.Null(town);
        }
        finally
        {
            smith.Delete();
        }
    }
}
