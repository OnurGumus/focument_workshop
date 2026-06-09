// The composition root, as a DI registration. `AddFocument` declares the write
// side to FCQRS — the actor system, the Document aggregate and the projection —
// and FCQRS owns the startup (it Init's the aggregate and starts the projection
// from a hosted service when the host starts).
//
// What stays explicit here is the lesson: *which* aggregate goes live and *what*
// the projection does. What disappears is the boilerplate (creating the actor
// system, threading the config/logger, calling Init). Program.cs and the tests
// both call AddFocument, so they compose the very same app.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Model;
using FCQRS;

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

            // Register the Document aggregate so it goes live. Its Handler is put in
            // DI so the delivery layer can resolve it.
            .AddAggregate<DocumentAggregate, DocumentState, DocumentCommand, DocumentEvent>()

            // The read-model projection, resumed from the last offset it committed.
            // The handler resolves its logger from DI, like the actor system does.
            .AddProjection(
                handler: sp => (offset, evt) =>
                    Projection.HandleEventWrapper(sp.GetRequiredService<ILoggerFactory>(), connectionString, offset, evt),
                lastOffset: _ => (int)ServerQuery.GetLastOffset(connectionString));

        return services;
    }
}
