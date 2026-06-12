// The composition root, as a DI registration. `AddFocument` declares the whole
// write side to FCQRS — the actor system, the two aggregates, the quota saga and
// the projection — and FCQRS owns the *ordering* and *startup* (it wires them in
// the right order from a hosted service when the host starts).
//
// What stays explicit here is the part that's actually the lesson: *which*
// aggregates go live, *which* saga coordinates them and *what* event starts it.
// What disappears is the boilerplate (creating the actor system, threading the
// config/logger, calling Init in the right order). Program.cs and the tests both
// call AddFocument, so they compose the very same app.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Model;
using FCQRS;
using static FCQRS.Common;
using static FCQRS.CSharp;

namespace Server;

public static class FocumentComposition
{
    public static IServiceCollection AddFocument(this IServiceCollection services, string connectionString)
    {
        // The read model lives in the same SQLite file as the Akka journal; make
        // sure its tables exist before the projection starts reading offsets.
        Projection.EnsureTables(connectionString);

        services
            // Create the SQLite-backed actor system (config + logger come from DI).
            .AddFcqrs(connectionString, "FocumentCluster")

            // Register every aggregate that must go live. Both Document and User
            // are registered even though the web only commands Document — the saga
            // drives User. Drop either and the saga silently stalls. The state /
            // command / event types come off each class's Aggregate<,,> base.
            .AddAggregate<DocumentAggregate>()
            .AddAggregate<UserAggregate>()

            // The saga spans the two aggregates: it gets their factories (looked up
            // from the ones AddAggregate registered, so init stays in one place) and
            // starts whenever a document write is requested.
            .AddSaga(
                create: sp => new QuotaSaga(
                    sp.AggregateFactory<DocumentAggregate>(),
                    sp.AggregateFactory<UserAggregate>(),
                    sp.GetRequiredService<ILogger<QuotaSaga>>()),
                startOn: e => e is Event<DocumentEvent> { EventDetails: DocumentEvent.CreateOrUpdateRequested })

            // The read-model projection, resumed from the last offset it committed.
            // The handler resolves its logger from DI, like the actor system does.
            .AddProjection(
                handler: sp => (offset, evt) =>
                    Projection.HandleEventWrapper(sp.GetRequiredService<ILoggerFactory>(), connectionString, offset, evt),
                lastOffset: _ => (int)ServerQuery.GetLastOffset(connectionString));

        return services;
    }
}
