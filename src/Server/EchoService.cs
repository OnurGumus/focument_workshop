// How a saga calls an external service: it doesn't. The saga journals a state
// that *means* "a call is owed" (Confirming), and describes a command to an
// actor that owns the side effect. This EchoService stands in for any real
// dependency — a compliance API, a payment gateway, an email sender. Swap the
// scheduled reply for an HttpClient call and the saga never knows.
//
// It's a plain Akka.NET actor — no journal, no sharding. Durability lives in
// the saga: if the process dies mid-call, the saga recovers in Confirming and
// re-issues Confirm (at-least-once), so the real endpoint behind this pattern
// must be idempotent — which is why the request carries the DocId as a key.

using System;
using Akka.Actor;
using Microsoft.Extensions.Logging;
using Model;

namespace Server;

// The saga's request and the service's reply. Plain records, deliberately not
// in the Model project: these are integration messages, not domain events —
// they are never journaled and never reach the read side.
public sealed record Confirm(DocumentId DocId);
public sealed record Confirmed(DocumentId DocId);

public sealed class EchoService : ReceiveActor
{
    public EchoService(ILogger<EchoService> log)
    {
        Receive<Confirm>(c =>
        {
            log.LogInformation("echo: confirming {DocId}", c.DocId);
            // Simulate external latency without blocking the actor. Sender is the
            // saga (FCQRS tells us from inside the saga actor), and anything sent
            // back lands in its HandleEvent like any other event.
            Context.System.Scheduler.ScheduleTellOnce(
                TimeSpan.FromMilliseconds(150), Sender, new Confirmed(c.DocId), Self);
        });
    }
}
