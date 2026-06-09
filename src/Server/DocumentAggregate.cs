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

// In-memory state, rebuilt from the event journal on demand. We keep the Owner
// (from the creating event) so the aggregate itself can enforce separation of
// duties — the creator may not approve their own document.
public record DocumentState(Document? Document, long Version, Approval Approval = Approval.Pending, Username? Owner = null)
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
            // A new pending document: store content + owner, bump version, awaiting verdict.
            DocumentEvent.CreateOrUpdateRequested e =>
                state with { Document = e.Document, Owner = e.Owner, Version = state.Version + 1L, Approval = Approval.Pending },
            // A plain edit of an existing document: new content/version, status kept.
            DocumentEvent.Updated e =>
                state with { Document = e.Document, Version = state.Version + 1L },
            DocumentEvent.Approved => state with { Approval = Approval.Approved },
            DocumentEvent.Rejected => state with { Approval = Approval.Rejected },
            DocumentEvent.HeldForApproval => state with { Approval = Approval.AwaitingApproval },
            // Errors are deferred, never persisted — folding one changes nothing.
            // No discard arm: the compiler proves the switch covers every case.
            DocumentEvent.Error => state
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

            // Within quota → the saga auto-approves. No human is involved, so no
            // separation-of-duties check. Idempotent: re-issued on saga recovery,
            // so defer if already approved rather than journal a duplicate.
            (DocumentCommand.AutoApprove, { } doc) =>
                state.Approval == Approval.Approved
                    ? EventActions.Defer<DocumentEvent>(new DocumentEvent.Approved(doc.Id))
                    : EventActions.Persist<DocumentEvent>(new DocumentEvent.Approved(doc.Id)),

            // Separation of duties — the creator may not decide their own document.
            // Enforced here, in the domain, against the Owner folded from the
            // creating event. Deferred (published to the caller, not journaled — no
            // state change), so the web layer learns of the refusal.
            (DocumentCommand.Approve c, { }) when state.Owner == c.Approver =>
                EventActions.Defer<DocumentEvent>(new DocumentEvent.Error(new DocumentError.SelfApproval())),
            (DocumentCommand.Reject c, { }) when state.Owner == c.Approver =>
                EventActions.Defer<DocumentEvent>(new DocumentEvent.Error(new DocumentError.SelfApproval())),

            // A colleague's verdict. Idempotent: if already in the target state,
            // defer (still published so a re-issuing caller sees it) rather than
            // persist a duplicate event.
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
