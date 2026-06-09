// The User aggregate: an event-sourced actor that owns a per-user write quota.
// Its state is a sliding window of the timestamps at which slots were consumed.
// The saga sends ConsumeQuota; the User decides Approve/Reject. Determinism rule:
// HandleCommand decides using the *command's* timestamp, and ApplyEvent folds
// only the timestamp carried on the event — never wall-clock — so replay and
// snapshots are deterministic.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Model;
using static FCQRS.Common;
using static FCQRS.CSharp;

namespace Server;

// One consumed quota slot: which document it was for (the idempotency key) and
// when it was consumed (an event-carried timestamp, never wall-clock).
public readonly record struct Consumption(DocumentId DocId, DateTime At);

// In-memory state: the slots still inside the rolling window. Keyed-by-document
// so a re-delivered ConsumeQuota (saga recovery) doesn't consume twice.
public record UserState(IReadOnlyList<Consumption> Consumed)
{
    public static readonly UserState Initial = new(Array.Empty<Consumption>());
}

public sealed class UserAggregate : Aggregate<UserState, UserCommand, UserEvent>
{
    // The quota: at most Limit consumed slots within Window. Kept small/short so
    // the limit and the window sliding are both easy to demo live in a workshop
    // (the "real" target was 5 per hour).
    public const int Limit = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ILogger<UserAggregate> _log;

    public UserAggregate(ILogger<UserAggregate> log) => _log = log;

    public override UserState InitialState => UserState.Initial;
    public override string EntityName => "User";

    // Keep only the slots within `Window` of the reference instant.
    private static IReadOnlyList<Consumption> Prune(IEnumerable<Consumption> slots, DateTime reference)
    {
        var cutoff = reference - Window;
        return slots.Where(c => c.At > cutoff).ToArray();
    }

    // Fold an event into state. Pure — folds only the event-carried timestamp,
    // and is idempotent: re-folding a slot for a document already recorded is a
    // no-op, so at-least-once delivery can't inflate the count.
    public override UserState ApplyEvent(Event<UserEvent> evt, UserState state)
    {
        _log.LogInformation("apply {Event}", Describe.Case(evt));
        return evt.EventDetails switch
        {
            UserEvent.QuotaApproved e when state.Consumed.Any(c => c.DocId == e.DocId) =>
                state, // already recorded this document's slot — idempotent
            UserEvent.QuotaApproved e =>
                state with { Consumed = Prune(state.Consumed.Append(new Consumption(e.DocId, e.ConsumedAt)), e.ConsumedAt) },
            _ => state
        };
    }

    // Decide whether a slot is available, using the command's timestamp.
    public override EventAction<UserEvent> HandleCommand(Command<UserCommand> cmd, UserState state)
    {
        _log.LogInformation("handle {Command}", Describe.Case(cmd));
        return cmd.CommandDetails switch
        {
            UserCommand.ConsumeQuota c => Decide(state, c.DocId, cmd.CreationDate),
            _ => EventActions.Ignore<UserEvent>()
        };
    }

    private static EventAction<UserEvent> Decide(UserState state, DocumentId docId, DateTime at)
    {
        // Already granted for this document → re-grant the SAME slot, don't
        // consume another. This makes the saga's at-least-once retry safe (e.g. a
        // crash in CheckingQuota re-issues ConsumeQuota). The re-grant is an
        // idempotent no-op on state, so Defer: re-deliver the event to the saga
        // without journaling a duplicate the fold would only ignore.
        var existing = state.Consumed.FirstOrDefault(c => c.DocId == docId);
        if (state.Consumed.Any(c => c.DocId == docId))
            return EventActions.Defer<UserEvent>(new UserEvent.QuotaApproved(docId, existing.At));

        return Prune(state.Consumed, at).Count < Limit
            ? EventActions.Persist<UserEvent>(new UserEvent.QuotaApproved(docId, at))
            : EventActions.Defer<UserEvent>(new UserEvent.QuotaRejected());
    }
}
