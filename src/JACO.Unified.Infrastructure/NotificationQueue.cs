using System.Threading.Channels;

namespace JACO.Unified.Infrastructure;

// An in-process, in-memory handoff so Submit/Decide/Nudge can return to the browser
// immediately instead of blocking on however long PpfExecutor's synchronous SMTP sends take
// (each recipient is its own TCP+TLS round trip, sent one at a time -- see PpfExecutor).
// NotificationDispatcher (registered as a BackgroundService) drains this off the request
// thread. Not durable across a restart -- an event queued right before the app recycles is
// lost -- which is an accepted tradeoff here: PpfExecutor's own doc comment already states a
// notification failure never touches Request.Status, so a lost/delayed notification was
// never allowed to affect the business outcome in the first place.
public sealed class NotificationQueue
{
    readonly Channel<(long RequestId, string EventCode)> channel = Channel.CreateUnbounded<(long, string)>();

    public void Enqueue(long requestId, string eventCode) => channel.Writer.TryWrite((requestId, eventCode));

    public ChannelReader<(long RequestId, string EventCode)> Reader => channel.Reader;
}
