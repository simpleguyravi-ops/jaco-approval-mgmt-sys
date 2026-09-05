using System.Threading.Channels;

namespace JACO.Unified.Infrastructure;

// A point-in-time read of NotificationQueue's counters -- Pending is derived (EnqueuedCount
// minus ProcessedCount) rather than asking the Channel itself, since an unbounded channel's
// own Count isn't guaranteed cheap/supported across every configuration.
public sealed record NotificationQueueStatus(long EnqueuedCount, long ProcessedCount, long FailedCount, DateTime? LastProcessedAtUtc, DateTime DispatcherStartedAtUtc)
{
    public long Pending => Math.Max(0, EnqueuedCount - ProcessedCount);
}

// An in-process, in-memory handoff so Submit/Decide/Nudge can return to the browser
// immediately instead of blocking on however long PpfExecutor's synchronous SMTP sends take
// (each recipient is its own TCP+TLS round trip, sent one at a time -- see PpfExecutor).
// NotificationDispatcher (registered as a BackgroundService) drains this off the request
// thread. Not durable across a restart -- an event queued right before the app recycles is
// lost -- which is an accepted tradeoff here: PpfExecutor's own doc comment already states a
// notification failure never touches Request.Status, so a lost/delayed notification was
// never allowed to affect the business outcome in the first place.
//
// Counters below exist purely so an admin can see the background dispatcher is actually
// alive and keeping up (PPF Monitor renders them) -- not for anything the app logic reads.
public sealed class NotificationQueue
{
    readonly Channel<(long RequestId, string EventCode)> channel = Channel.CreateUnbounded<(long, string)>();
    long enqueuedCount;
    long processedCount;
    long failedCount;
    DateTime? lastProcessedAtUtc;
    DateTime dispatcherStartedAtUtc = DateTime.UtcNow;

    public void Enqueue(long requestId, string eventCode)
    {
        channel.Writer.TryWrite((requestId, eventCode));
        Interlocked.Increment(ref enqueuedCount);
    }

    public ChannelReader<(long RequestId, string EventCode)> Reader => channel.Reader;

    // Called by NotificationDispatcher once it started its read loop, so "started at"
    // reflects the loop actually running rather than just this object being constructed.
    public void MarkDispatcherStarted() => dispatcherStartedAtUtc = DateTime.UtcNow;

    public void MarkProcessed(bool success)
    {
        Interlocked.Increment(ref processedCount);
        if (!success) Interlocked.Increment(ref failedCount);
        lastProcessedAtUtc = DateTime.UtcNow;
    }

    public NotificationQueueStatus GetStatus() => new(
        Interlocked.Read(ref enqueuedCount),
        Interlocked.Read(ref processedCount),
        Interlocked.Read(ref failedCount),
        lastProcessedAtUtc,
        dispatcherStartedAtUtc);
}
