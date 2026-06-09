// The quota saga: the one piece that genuinely needs a saga, because it spans
// two aggregates. It starts from the Document's CreateOrUpdateRequested event
// (originator = Document), asks the User aggregate to consume a quota slot, and
// then tells the Document to Approve or Reject based on the User's verdict.
//
// Cross-aggregate mechanics (traced in FCQRS): the saga subscribes to its
// originator's (Document's) CID topic; when it sends a command to a *different*
// aggregate (User), that aggregate replies its event straight back to the saga,
// so the saga hears QuotaApproved/QuotaRejected even though User isn't the
// originator. The CID is preserved throughout, so the web's read-your-writes
// awaiter still fires when the Document's final event is projected.

using System;
using Akkling.Cluster.Sharding;
using Microsoft.Extensions.Logging;
using Microsoft.FSharp.Core;
using Model;
using static FCQRS.Common;
using static FCQRS.CSharp;

namespace Server;

public sealed class QuotaSaga : Saga<DocumentEvent, QuotaSagaData, QuotaState>
{
    private readonly AggregateFactory _documentFactory;
    private readonly AggregateFactory _userFactory;
    private readonly ILogger<QuotaSaga> _log;

    // Takes the aggregates' already-registered factories (from App.Build) so the
    // saga doesn't re-init them — init lives in one place.
    public QuotaSaga(
        AggregateFactory documentFactory,
        AggregateFactory userFactory,
        ILogger<QuotaSaga> log)
    {
        _documentFactory = documentFactory;
        _userFactory = userFactory;
        _log = log;
    }

    public override QuotaSagaData InitialData => new();
    public override string SagaName => "QuotaSaga";
    // The saga starts from the Document; Approve/Reject go back to it.
    public override AggregateFactory Originator => _documentFactory;

    // Reacts to events from BOTH the originating Document and the User. The full
    // (obj-based) saga builder is used precisely so we can match two event types;
    // the state arrives as an FSharpOption (None = not yet in a user-defined state).
    public override EventAction<QuotaState> HandleEvent(
        object evt,
        SagaState<QuotaSagaData, FSharpOption<QuotaState>> sagaState)
    {
        var current = sagaState.State?.Value; // null while still <initial>
        var stateName = current is null ? "<initial>" : Describe.Case(current);
        _log.LogInformation("saga handle {Event} in {State}", Describe.Case(evt), stateName);

        return (evt, current) switch
        {
            // A write was requested → ask the user to consume a quota slot.
            (Event<DocumentEvent> { EventDetails: DocumentEvent.CreateOrUpdateRequested co }, null) =>
                StateChanged(new QuotaState.CheckingQuota(co.Owner, co.Document.Id)),

            // The user's verdict → approve, or park for a colleague's approval.
            (Event<UserEvent> { EventDetails: UserEvent.QuotaApproved }, QuotaState.CheckingQuota s) =>
                StateChanged(new QuotaState.Approving(s.DocId)),
            (Event<UserEvent> { EventDetails: UserEvent.QuotaRejected }, QuotaState.CheckingQuota s) =>
                StateChanged(new QuotaState.Holding(s.DocId)),

            // The document finalised → we're done.
            (Event<DocumentEvent> { EventDetails: DocumentEvent.Approved }, QuotaState.Approving) =>
                StateChanged(new QuotaState.Done()),
            (Event<DocumentEvent> { EventDetails: DocumentEvent.HeldForApproval }, QuotaState.Holding) =>
                StateChanged(new QuotaState.Done()),

            _ => Unhandled()
        };
    }

    // Decide which commands to issue for the current state. The command params
    // are typed as the union (not the bare case) so the case record converts
    // implicitly — FCQRS's dispatcher needs the union to build Command<_>.
    public override SagaSideEffectResult<QuotaState> ApplySideEffects(
        SagaState<QuotaSagaData, QuotaState> sagaState,
        bool recovering)
    {
        ExecuteCommand ToUser(Username owner, UserCommand command) =>
            SagaCommands.ToAggregate(_userFactory, owner.ToString(), command);
        ExecuteCommand ToDocument(DocumentCommand command) =>
            SagaCommands.ToOriginator(_documentFactory, command);

        return sagaState.State switch
        {
            QuotaState.CheckingQuota s => new()
            {
                Transition = Stay(),
                // DocId makes this idempotent — the saga re-issues it verbatim
                // on recovery, and the User won't consume a second slot.
                Commands = [ToUser(s.Owner, new UserCommand.ConsumeQuota(s.DocId))]
            },
            QuotaState.Approving => new()
            {
                Transition = Stay(),
                Commands = [ToDocument(new DocumentCommand.Approve())]
            },
            QuotaState.Holding => new()
            {
                Transition = Stay(),
                Commands = [ToDocument(new DocumentCommand.Hold())]
            },
            QuotaState.Done => new() { Transition = StopSaga(), Commands = [] },
            _ => new() { Transition = Stay(), Commands = [] }
        };
    }
}
