using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Server.Avallone.AI;
using Xunit;

// The gateway completes synchronously with the fake client, so these tests read .Result on tasks that
// are already finished (asserted where it matters). Nothing blocks.
#pragma warning disable xUnit1031

namespace UOContent.Tests;

// No world fixture: the gateway needs no game state. Its tests only share the gateway's static state,
// and xUnit runs the tests of one class one at a time.
public class AiGatewayTests : IDisposable
{
    private long _now = 1_000_000;
    private readonly FakeClient _client = new();

    public AiGatewayTests()
    {
        AiGateway.ResetForTests();
        AiGateway.Client = _client;
        AiGateway.Clock = () => _now;
        AiGateway.RequestTimeout = TimeSpan.FromSeconds(4);
        AiGateway.QueueDeadline = TimeSpan.FromSeconds(6);
        AiGateway.MaxQueue = 16;
        AiGateway.MaxInFlight = 1;
        AiGateway.BreakerThreshold = 3;
        AiGateway.BreakerCooldown = TimeSpan.FromSeconds(60);
    }

    public void Dispose()
    {
        AiGateway.ResetForTests();
        AiGateway.SetEnabled(false, false);
        AiGateway.Client = null;
        AiGateway.Clock = () => Server.Core.TickCount;
        AiGateway.UtcNow = () => Server.Core.Now;
    }

    private static AiRequest Req(AiPriority priority = AiPriority.Conversation, Guid? npc = null) =>
        new(npc ?? Guid.NewGuid(), Guid.NewGuid(), "persona", "hello", priority);

    [Fact]
    public void KillSwitchOffFailsFastWithoutCallingTheModel()
    {
        AiGateway.SetEnabled(false, false);

        var task = AiGateway.RequestAsync(Req());

        Assert.True(task.IsCompleted);
        Assert.Equal(AiOutcome.Disabled, task.Result.Outcome);
        Assert.Empty(_client.Calls);
    }

    [Fact]
    public void SuccessfulReplyIsReturnedAndLatencyRecorded()
    {
        var task = AiGateway.RequestAsync(Req());
        _client.Calls[0].Reply.SetResult(AiReply.Success("Welcome, traveller.", "test-model"));

        Assert.True(task.IsCompleted);
        Assert.Equal(AiOutcome.Ok, task.Result.Outcome);
        Assert.Equal("Welcome, traveller.", task.Result.Text);
        Assert.Equal(1, AiGateway.Metrics[AiOutcome.Ok]);
        Assert.True(AiGateway.Metrics.LatencyPercentile(50) >= 0);
    }

    [Fact]
    public void SecondRequestForSameNpcAndSubjectIsBusy()
    {
        var first = Req();
        AiGateway.RequestAsync(first);

        var again = new AiRequest(first.NpcId, first.SubjectId, "persona", "hello again", AiPriority.Conversation);
        var task = AiGateway.RequestAsync(again);

        Assert.Equal(AiOutcome.Busy, task.Result.Outcome);
        Assert.Single(_client.Calls);
    }

    [Fact]
    public void HigherPriorityWorkIsSentFirst()
    {
        AiGateway.RequestAsync(Req(AiPriority.Background)); // goes straight out
        var background = Req(AiPriority.Background);
        var companion = Req(AiPriority.Companion);
        AiGateway.RequestAsync(background);
        AiGateway.RequestAsync(companion);

        _client.Calls[0].Reply.SetResult(AiReply.Success("a", "m"));

        Assert.Equal(2, _client.Calls.Count);
        Assert.Equal(companion.RequestId, _client.Calls[1].Request.RequestId);
    }

    [Fact]
    public void FullQueuePushesOutLowerPriorityWorkOnly()
    {
        AiGateway.MaxQueue = 2;
        AiGateway.RequestAsync(Req()); // in flight, not queued
        var bg1 = AiGateway.RequestAsync(Req(AiPriority.Background));
        var bg2 = AiGateway.RequestAsync(Req(AiPriority.Background));

        var anotherBg = AiGateway.RequestAsync(Req(AiPriority.Background));
        Assert.Equal(AiOutcome.QueueFull, anotherBg.Result.Outcome);

        var companion = AiGateway.RequestAsync(Req(AiPriority.Companion));
        Assert.False(companion.IsCompleted);
        Assert.Equal(AiOutcome.Evicted, bg2.Result.Outcome);
        Assert.False(bg1.IsCompleted);
    }

    [Fact]
    public void QueuedWorkPastItsDeadlineIsDroppedNotSent()
    {
        AiGateway.RequestAsync(Req());
        var waiting = AiGateway.RequestAsync(Req());

        _now += 7_000; // past the 6 s queue deadline
        _client.Calls[0].Reply.SetResult(AiReply.Success("late", "m"));

        Assert.Equal(AiOutcome.Expired, waiting.Result.Outcome);
        Assert.Single(_client.Calls);
    }

    [Fact]
    public async Task SlowModelTimesOut()
    {
        AiGateway.RequestTimeout = TimeSpan.FromMilliseconds(100);
        _client.HangForever = true;

        var reply = await AiGateway.RequestAsync(Req()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AiOutcome.Timeout, reply.Outcome);
    }

    [Fact]
    public void ClientExceptionBecomesFailedAndNeverThrowsToGameCode()
    {
        _client.Throw = new InvalidOperationException("connection refused");

        var task = AiGateway.RequestAsync(Req());

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(AiOutcome.Failed, task.Result.Outcome);
        Assert.Contains("connection refused", task.Result.Error);
    }

    [Fact]
    public void BreakerOpensAfterRepeatedFailuresAndRetriesAfterCooldown()
    {
        _client.Throw = new InvalidOperationException("model down");
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(AiOutcome.Failed, AiGateway.RequestAsync(Req()).Result.Outcome);
        }

        var blocked = AiGateway.RequestAsync(Req());
        Assert.Equal(AiOutcome.BreakerOpen, blocked.Result.Outcome);
        Assert.Equal(3, _client.CallCount);

        _now += 61_000;
        _client.Throw = null;
        var retry = AiGateway.RequestAsync(Req());
        _client.Calls[^1].Reply.SetResult(AiReply.Success("back", "m"));

        Assert.Equal(AiOutcome.Ok, retry.Result.Outcome);
        Assert.False(AiGateway.BreakerIsOpen);
    }

    [Fact]
    public void KillSwitchCancelsQueuedWork()
    {
        AiGateway.RequestAsync(Req());
        var queued = AiGateway.RequestAsync(Req());

        AiGateway.SetEnabled(false, false);

        Assert.Equal(AiOutcome.Disabled, queued.Result.Outcome);
        Assert.Equal(0, AiGateway.QueueCount);
    }

    [Fact]
    public void QuietWindowAnswersQuietWithoutCallingTheModelOrTrippingTheBreaker()
    {
        AiGateway.QuietStart = new TimeSpan(4, 55, 0);
        AiGateway.QuietEnd = new TimeSpan(5, 50, 0);
        AiGateway.UtcNow = () => new DateTime(2026, 10, 1, 5, 10, 0, DateTimeKind.Utc); // no zone set: UTC

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(AiOutcome.Quiet, AiGateway.RequestAsync(Req()).Result.Outcome);
        }

        Assert.Empty(_client.Calls);
        Assert.False(AiGateway.BreakerIsOpen);

        AiGateway.UtcNow = () => new DateTime(2026, 10, 1, 5, 50, 0, DateTimeKind.Utc); // window end is exclusive
        Assert.False(AiGateway.InQuietWindow);
        AiGateway.RequestAsync(Req());
        Assert.Single(_client.Calls);
    }

    [Fact]
    public void QuietWindowCanWrapPastMidnight()
    {
        AiGateway.QuietStart = new TimeSpan(23, 0, 0);
        AiGateway.QuietEnd = new TimeSpan(1, 0, 0);

        AiGateway.UtcNow = () => new DateTime(2026, 10, 1, 0, 30, 0, DateTimeKind.Utc);
        Assert.True(AiGateway.InQuietWindow);
        AiGateway.UtcNow = () => new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(AiGateway.InQuietWindow);
    }

    [Fact]
    public void RequestCarriesPersonaKeyAndHistory()
    {
        var req = Req() with { };
        var withHistory = new AiRequest(req.NpcId, req.SubjectId, null, "and the tea?", AiPriority.Conversation)
        {
            PersonaKey = "mirabel",
            History = [new AiLine("player", "hello"), new AiLine("npc", "Evening, traveller.")]
        };

        AiGateway.RequestAsync(withHistory);

        Assert.Equal("mirabel", _client.Calls[0].Request.PersonaKey);
        Assert.Equal(2, _client.Calls[0].Request.History.Length);
    }

    [Fact]
    public void GatewayHoldsTheGameThreadBriefly()
    {
        for (var i = 0; i < 50; i++)
        {
            AiGateway.RequestAsync(Req(AiPriority.Background));
        }

        Assert.True(AiGateway.Metrics.MaxHitch < TimeSpan.FromMilliseconds(50), $"max hitch {AiGateway.Metrics.MaxHitch}");
    }

    private sealed class FakeClient : IAiNpcClient
    {
        public List<(AiRequest Request, TaskCompletionSource<AiReply> Reply)> Calls { get; } = [];
        public int CallCount { get; private set; }
        public Exception Throw { get; set; }
        public bool HangForever { get; set; }

        public string Name => "fake";

        public int Warms { get; private set; }

        public Task WarmAsync(CancellationToken token)
        {
            Warms++;
            return Task.CompletedTask;
        }

        public async Task<AiReply> SendAsync(AiRequest request, TimeSpan budget, CancellationToken token)
        {
            CallCount++;

            if (Throw != null)
            {
                throw Throw;
            }

            if (HangForever)
            {
                await Task.Delay(Timeout.Infinite, token);
            }

            var tcs = new TaskCompletionSource<AiReply>();
            Calls.Add((request, tcs));
            return await tcs.Task;
        }
    }
}
