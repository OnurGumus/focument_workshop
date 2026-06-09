// The Document aggregate: an event-sourced, cluster-sharded actor.
// It decides commands into events (HandleCommand) and folds events
// back into state (ApplyEvent). FCQRS handles persistence and sharding.

using Microsoft.Extensions.Logging;
using Model;
using static FCQRS.Common;
using static FCQRS.CSharp;

namespace Server;

// In-memory state, rebuilt from the event journal on demand.
public record DocumentState(Document? Document, long Version)
{
    public static readonly DocumentState Initial = new(null, 0L);
}

// The aggregate only has to supply state + name + decide + fold; the base
// (Aggregate<>, from FCQRS) provides the wiring. A logger is injected for the example.
public sealed class DocumentAggregate : Aggregate<DocumentState, DocumentCommand, DocumentEvent>
{
    private readonly ILogger<DocumentAggregate> _log;

    public DocumentAggregate(ILogger<DocumentAggregate> log) => _log = log;

    public override DocumentState InitialState => DocumentState.Initial;
    public override string EntityName => "Document";

    // Fold an event into the current state. Pure — no side effects.
    public override DocumentState ApplyEvent(Event<DocumentEvent> evt, DocumentState state)
    {
        _log.LogInformation("apply event");
        return evt.EventDetails switch
        {
            DocumentEvent.CreatedOrUpdated e =>
                state with { Document = e.Document, Version = state.Version + 1L },
            _ => state
        };
    }

    // Decide what should happen for a command, given current state.
    public override EventAction<DocumentEvent> HandleCommand(
        Command<DocumentCommand> cmd,
        DocumentState state)
    {
        _log.LogInformation("handle command");
        return (cmd.CommandDetails, state.Document) switch
        {
            // First write — this actor holds no document yet.
            (DocumentCommand.CreateOrUpdate c, null) =>
                EventActions.Persist<DocumentEvent>(new DocumentEvent.CreatedOrUpdated(c.Document)),

            // Update — same id as the document we already hold.
            (DocumentCommand.CreateOrUpdate c, { } existing) when existing.Id == c.Document.Id =>
                EventActions.Persist<DocumentEvent>(new DocumentEvent.CreatedOrUpdated(c.Document)),

            // Wrong id routed to this actor.
            (DocumentCommand.CreateOrUpdate, _) =>
                EventActions.Defer<DocumentEvent>(new DocumentEvent.Error(new DocumentError.DocumentNotFound())),

            _ => EventActions.Ignore<DocumentEvent>()
        };
    }
}
