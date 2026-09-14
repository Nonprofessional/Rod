using Rod.Implant.Internal;
using Rod.V1;

namespace Rod.Implant.Tests;

// Pins the held-task ledger (architecture.md Sec 10.3 -- the dispatch
// strand): the dedup that recognizes a redelivered task after a stream died
// before its ack crossed, and the bounded result cache whose undelivered
// half re-sends on the next connection while the server records
// first-wins. This is the reference implant's half of the arm; the server's
// requeue and idempotency are pinned by the integration suite.
public class HeldTaskLedgerTests
{
    [Fact]
    public void Hold_MakesTheTaskRecognized_OnAnyLaterDelivery()
    {
        var ledger = new HeldTaskLedger();

        Assert.False(ledger.Contains("t-1"));
        ledger.Hold("t-1");
        Assert.True(ledger.Contains("t-1"));

        // A held id with no cached result is a task that is running (or died
        // with its channel): nothing to re-send, but never a re-run.
        Assert.False(ledger.TryGetUndelivered("t-1", out _, out _));
    }

    [Fact]
    public void Remember_AndReSend_FollowTheDeliveryMarks()
    {
        var ledger = new HeldTaskLedger();
        ledger.Hold("t-1");
        ledger.Remember("t-1", TaskOutcome.Succeeded, "ran once");

        Assert.True(ledger.TryGetUndelivered("t-1", out var outcome, out var output));
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Equal("ran once", output);

        // The write landed on a connection that lived to send it: the cached
        // result goes silent for the post-connection sweep, so a reconnect
        // does not re-send every cached result it holds.
        ledger.MarkDelivered("t-1");
        Assert.False(ledger.TryGetUndelivered("t-1", out _, out _));

        // The connection died after all: the marks clear wholesale and the
        // result re-sends on the next one.
        ledger.InvalidateDeliveries();
        Assert.True(ledger.TryGetUndelivered("t-1", out _, out var resent));
        Assert.Equal("ran once", resent);
    }

    [Fact]
    public void DeliveredResult_IsStillReadable_ForARedeliveryAnswer()
    {
        // The redelivery answer ignores the delivery marks: a redelivery
        // implies the server holds no recorded result (its first-wins either
        // dropped or never saw the original), so the cached outcome re-sends
        // regardless of what an earlier connection did.
        var ledger = new HeldTaskLedger();
        ledger.Hold("t-1");
        ledger.Remember("t-1", TaskOutcome.Succeeded, "ran once");
        ledger.MarkDelivered("t-1");

        Assert.True(ledger.TryGetResult("t-1", out var outcome, out var output));
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Equal("ran once", output);
    }

    [Fact]
    public void Undelivered_ListsOldestFirst_SkippingIncompleteAndDelivered()
    {
        var ledger = new HeldTaskLedger();
        ledger.Hold("t-1");
        ledger.Hold("t-2");
        ledger.Hold("t-3");
        ledger.Remember("t-1", TaskOutcome.Succeeded, "first");
        ledger.Remember("t-3", TaskOutcome.Failed, "third");
        ledger.MarkDelivered("t-3");

        var pending = ledger.Undelivered();

        // t-2 never completed, t-3 completed and delivered: only t-1 rides.
        var id = Assert.Single(pending);
        Assert.Equal("t-1", id.TaskId);
        Assert.Equal("first", id.Output);
    }

    [Fact]
    public void Capacity_EvictsTheOldestHeldId()
    {
        var ledger = new HeldTaskLedger();

        // Fill past the bound with ids nobody will ever redeliver again;
        // insert "keep" first so it is the one that falls out.
        ledger.Hold("keep");
        for (var i = 0; i < 1100; i++)
            ledger.Hold($"t-{i}");

        Assert.False(ledger.Contains("keep"));
        Assert.True(ledger.Contains("t-1099"));

        // A re-hold refreshes recency, so a redelivered task does not evict
        // itself.
        ledger.Hold("t-500");
        for (var i = 2000; i < 2600; i++)
            ledger.Hold($"t-{i}");
        Assert.True(ledger.Contains("t-500"));
        Assert.False(ledger.Contains("t-0"));
    }
}
