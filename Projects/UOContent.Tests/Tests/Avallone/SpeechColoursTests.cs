using System;
using System.Collections.Generic;
using Server;
using Server.Avallone;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// Speech colours (the owner, 2026-11-11): with the switch on, ONE rule picks the colour every non-player speaks in - companions
// and wanderers one colour, every other NPC and monster another. A player is never touched, the colour stored on a
// mobile is never changed, and with the switch off everything reads exactly as it was stored.
[Collection("Sequential UOContent Tests")]
public class SpeechColoursTests : IDisposable
{
    private readonly List<Mobile> _made = [];

    public void Dispose()
    {
        SpeechColours.SetForTests(false);
        foreach (var m in _made)
        {
            m.Delete();
        }
    }

    private T Made<T>(T m) where T : Mobile
    {
        _made.Add(m);
        return m;
    }

    [Fact]
    public void Off_EveryMobileSpeaksInItsStoredColour()
    {
        SpeechColours.SetForTests(false);
        var banker = Made(new Banker());
        banker.SpeechHue = 1150;
        Assert.Null(Mobile.NpcSpeechHueRule);
        Assert.Equal(1150, banker.SpeechHue);
        Assert.Equal(1150, banker.StoredSpeechHue);
    }

    [Fact]
    public void On_NpcsAndMonstersGreen_CompanionsAndBramYellow()
    {
        SpeechColours.SetForTests(true);
        Assert.Equal(68, Made(new Banker()).SpeechHue);
        Assert.Equal(68, Made(new Mirabel()).SpeechHue);          // a named AI innkeeper is an NPC, not a companion
        Assert.Equal(68, Made(new Orc()).SpeechHue);              // monsters that talk (the owner)
        Assert.Equal(68, Made(new HireSailor()).SpeechHue);
        Assert.Equal(48, Made(new ElspethVale()).SpeechHue);
        Assert.Equal(48, Made(new BramHolloway()).SpeechHue);     // the owner:
    }

    [Fact]
    public void On_APlayersOwnColourIsNeverTouched()
    {
        SpeechColours.SetForTests(true);
        var p = Made(new PlayerMobile { Name = "Colourtester", Player = true });
        p.SpeechHue = 90;
        Assert.Equal(90, p.SpeechHue);
    }

    [Fact]
    public void On_TheStoredColourIsKept_SoOffGivesItBack()
    {
        var banker = Made(new Banker());
        banker.SpeechHue = 1150;
        SpeechColours.SetForTests(true);
        Assert.Equal(68, banker.SpeechHue);
        Assert.Equal(1150, banker.StoredSpeechHue);               // what a save writes
        banker.SpeechHue = 1175;                                  // stock code setting a colour still stores it
        Assert.Equal(68, banker.SpeechHue);
        SpeechColours.SetForTests(false);
        Assert.Equal(1175, banker.SpeechHue);
    }

    [Fact]
    public void On_TheHuesCanBeChanged()
    {
        SpeechColours.SetForTests(true, 63, 53);
        Assert.Equal(63, Made(new Banker()).SpeechHue);
        Assert.Equal(53, Made(new ElspethVale()).SpeechHue);
    }
}
