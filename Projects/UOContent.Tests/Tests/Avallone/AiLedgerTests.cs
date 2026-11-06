using System;
using System.IO;
using Server.Avallone.AI;
using Xunit;

namespace UOContent.Tests;

// The epoch is a boot counter outside the saves: it must only ever go up, even when a save is restored
// or the counter file is lost (Phase 4a1, design v2).
public class AiLedgerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ai-epoch-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "ai-epoch.txt");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void FirstBootStartsAtOne()
    {
        Assert.Equal(1, AiLedger.NextEpoch(File_, 0));
        Assert.Equal("1", File.ReadAllText(File_));
    }

    [Fact]
    public void EveryBootGoesUp()
    {
        AiLedger.NextEpoch(File_, 0);
        AiLedger.NextEpoch(File_, 1);
        Assert.Equal(3, AiLedger.NextEpoch(File_, 2));
    }

    [Fact]
    public void RestoredOlderSaveDoesNotRewindTheCounter()
    {
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(_dir).FullName, "ai-epoch.txt"), "9");
        Assert.Equal(10, AiLedger.NextEpoch(File_, 4));
    }

    [Fact]
    public void LostCounterFileStillPassesTheSave()
    {
        Assert.Equal(8, AiLedger.NextEpoch(File_, 7));
    }

    [Fact]
    public void EventIdMatchesWhatTheBrainChecks() => Assert.Equal("3-17", AiLedger.EventId(3, 17));
}
