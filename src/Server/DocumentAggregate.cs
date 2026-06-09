// The Document aggregate: an event-sourced, cluster-sharded actor.
// It decides commands into events (HandleCommand) and folds events
// back into state (ApplyEvent). FCQRS handles persistence and sharding.

using Microsoft.Extensions.Logging;
using Model;
using static FCQRS.Common;
using static FCQRS.CSharp;

namespace Server;

// The document's place in the quota workflow. Tracked precisely (not just a
// bool) so the verdict commands can be made idempotent — re-approving an already
// approved document must be a no-op, not a second event.
public enum Approval { Pending, AwaitingApproval, Approved, Rejected }

// In-memory state, rebuilt from the event journal on demand.
public record DocumentState(Document? Document, long Version, Approval Approval = Approval.Pending)
{
    public static readonly DocumentState Initial = new(null, 0L);
}

// The aggregate only has to supply state + name + decide + fold; the base
// (Aggregate<>) provides the wiring. A logger is injected for the example.
public sealed class DocumentAggregate : Aggregate<DocumentState, DocumentCommand, DocumentEvent>
{
    private readonly ILogger<DocumentAggregate> _log;

    public DocumentAggregate(ILogger<DocumentAggregate> log) => _log = log;

    public override DocumentState InitialState => DocumentState.Initial;
    public override string EntityName => "Document";

    // Fold an event into the current state. Pure — no side effects.
    public override DocumentState ApplyEvent(Event<DocumentEvent> evt, DocumentState state)
    {
        _log.LogInformation("apply {Event}", Describe.Case(evt));
        return evt.EventDetails switch
        {
            // A new pending document: store content, bump version, awaiting verdict.
            DocumentEvent.CreateOrUpdateRequested e =>
                state with { Document = e.Document, Version = state.Version + 1L, Approval = Approval.Pending },
            // A plain edit of an existing document: new content/version, status kept.
            DocumentEvent.Updated e =>
                state with { Document = e.Document, Version = state.Version + 1L },
            DocumentEvent.Approved => state with { Approval = Approval.Approved },
            DocumentEvent.Rejected => state with { Approval = Approval.Rejected },
            DocumentEvent.HeldForApproval => state with { Approval = Approval.AwaitingApproval },
            _ => state
        };
    }

    // Decide what should happen for a command, given current state.
    public override EventAction<DocumentEvent> HandleCommand(
        Command<DocumentCommand> cmd,
        DocumentState state)
    {
        _log.LogInformation("handle {Command}", Describe.Case(cmd));
        return (cmd.CommandDetails, state.Document) switch
        {
            // First write — no document yet. Records a pending request that starts
            // the quota saga (only creation is quota-gated).
            (DocumentCommand.CreateOrUpdate c, null) =>
                EventActions.Persist<DocumentEvent>(new DocumentEvent.CreateOrUpdateRequested(c.Document, c.Owner)),

            // Edit of the document we already hold — no saga, no quota.
            (DocumentCommand.CreateOrUpdate c, { } existing) when existing.Id == c.Document.Id =>
                EventActions.Persist<DocumentEvent>(new DocumentEvent.Updated(c.Document)),

            // Wrong id routed to this actor.
            (DocumentCommand.CreateOrUpdate, _) =>
                EventActions.Defer<DocumentEvent>(new DocumentEvent.Error(new DocumentError.DocumentNotFound())),

            // Verdicts — from the saga (quota ok) or a colleague. Each is
            // idempotent: if already in the target state, defer (still published
            // so a re-issuing saga sees it and proceeds) rather than persist a
            // duplicate event. This makes the saga's at-least-once retries safe.
            (DocumentCommand.Approve, { } doc) =>
                state.Approval == Approval.Approved
                    ? EventActions.Defer<DocumentEvent>(new DocumentEvent.Approved(doc.Id))
                    : EventActions.Persist<DocumentEvent>(new DocumentEvent.Approved(doc.Id)),

            (DocumentCommand.Reject, { } doc) =>
                state.Approval == Approval.Rejected
                    ? EventActions.Defer<DocumentEvent>(new DocumentEvent.Rejected(doc.Id))
                    : EventActions.Persist<DocumentEvent>(new DocumentEvent.Rejected(doc.Id)),

            // Over quota: park the document pending a colleague's approval.
            (DocumentCommand.Hold, { } doc) =>
                state.Approval == Approval.AwaitingApproval
                    ? EventActions.Defer<DocumentEvent>(new DocumentEvent.HeldForApproval(doc.Id))
                    : EventActions.Persist<DocumentEvent>(new DocumentEvent.HeldForApproval(doc.Id)),

            _ => EventActions.Ignore<DocumentEvent>()
        };
    }
}
