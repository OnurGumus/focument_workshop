// Tier 1 — pure decision/fold tests for the Document aggregate.
//
// HandleCommand and ApplyEvent are pure: given a command (or event) and the
// current state, they return an action (or the next state) with no side effects.
// We test the real rules with plain method calls — no actor system, no DB, no
// async. The only ceremony is wrapping a payload in the Command<>/Event<>
// envelope, which FCQRS's TestEnvelope helpers do for us.

using Microsoft.Extensions.Logging.Abstractions;
using Model;
using Server;
using Xunit;
using static FCQRS.CSharp;

namespace Server.Tests;

public class DocumentAggregateTests
{
    private static readonly DocumentAggregate Aggregate = new(NullLogger<DocumentAggregate>.Instance);

    private static Document Doc(System.Guid? id = null, string title = "Title", string body = "Body")
    {
        Assert.True(Document.TryCreate(id ?? System.Guid.NewGuid(), title, body, out var doc, out _));
        return doc!;
    }

    private static Username Owner(string name = "alice")
    {
        Assert.True(Username.TryCreate(name, out var u));
        return u;
    }

    // --- HandleCommand: the decision ---

    [Fact]
    public void Create_on_empty_state_requests_a_pending_write()
    {
        var doc = Doc();
        var owner = Owner();
        var cmd = TestEnvelope.Command<DocumentCommand>(new DocumentCommand.CreateOrUpdate(doc, owner));

        var action = Aggregate.HandleCommand(cmd, DocumentState.Initial);

        Assert.Equal(
            EventActions.Persist<DocumentEvent>(new DocumentEvent.CreateOrUpdateRequested(doc, owner)),
            action);
    }

    [Fact]
    public void Update_of_an_existing_document_skips_the_saga()
    {
        // Editing a doc we already hold is a plain Updated event — no quota.
        var id = System.Guid.NewGuid();
        var state = DocumentState.Initial with { Document = Doc(id), Version = 1L };
        var updated = Doc(id, "New title", "New body");
        var cmd = TestEnvelope.Command<DocumentCommand>(new DocumentCommand.CreateOrUpdate(updated, Owner()));

        var action = Aggregate.HandleCommand(cmd, state);

        Assert.Equal(
            EventActions.Persist<DocumentEvent>(new DocumentEvent.Updated(updated)),
            action);
    }

    [Fact]
    public void Command_for_a_different_id_defers_a_DocumentNotFound_error()
    {
        var state = DocumentState.Initial with { Document = Doc(System.Guid.NewGuid()), Version = 1L };
        var cmd = TestEnvelope.Command<DocumentCommand>(
            new DocumentCommand.CreateOrUpdate(Doc(System.Guid.NewGuid()), Owner()));

        var action = Aggregate.HandleCommand(cmd, state);

        var deferred = Assert.IsType<FCQRS.Common.EventAction<DocumentEvent>.DeferEvent>(action);
        Assert.True(
            deferred.Item is DocumentEvent.Error { ErrorDetails: DocumentError.DocumentNotFound },
            $"expected Error(DocumentNotFound), got {deferred.Item}");
    }

    [Fact]
    public void Approve_on_a_held_document_persists_Approved()
    {
        var doc = Doc();
        var state = DocumentState.Initial with { Document = doc, Version = 1L, Approval = Approval.AwaitingApproval };
        var cmd = TestEnvelope.Command<DocumentCommand>(new DocumentCommand.Approve());

        var action = Aggregate.HandleCommand(cmd, state);

        Assert.Equal(
            EventActions.Persist<DocumentEvent>(new DocumentEvent.Approved(doc.Id)),
            action);
    }

    [Fact]
    public void Approve_when_already_approved_defers_instead_of_persisting()
    {
        // Idempotency: the saga re-issues Approve on recovery — a second Approved
        // must not be journaled, but it's still deferred (published) so the saga proceeds.
        var doc = Doc();
        var state = DocumentState.Initial with { Document = doc, Version = 1L, Approval = Approval.Approved };
        var cmd = TestEnvelope.Command<DocumentCommand>(new DocumentCommand.Approve());

        var action = Aggregate.HandleCommand(cmd, state);

        Assert.Equal(
            EventActions.Defer<DocumentEvent>(new DocumentEvent.Approved(doc.Id)),
            action);
    }

    [Fact]
    public void Hold_on_a_pending_document_persists_HeldForApproval()
    {
        var doc = Doc();
        var state = DocumentState.Initial with { Document = doc, Version = 1L }; // Pending
        var cmd = TestEnvelope.Command<DocumentCommand>(new DocumentCommand.Hold());

        var action = Aggregate.HandleCommand(cmd, state);

        Assert.Equal(
            EventActions.Persist<DocumentEvent>(new DocumentEvent.HeldForApproval(doc.Id)),
            action);
    }

    [Fact]
    public void Hold_when_already_held_defers_instead_of_persisting()
    {
        var doc = Doc();
        var state = DocumentState.Initial with { Document = doc, Version = 1L, Approval = Approval.AwaitingApproval };
        var cmd = TestEnvelope.Command<DocumentCommand>(new DocumentCommand.Hold());

        var action = Aggregate.HandleCommand(cmd, state);

        Assert.Equal(
            EventActions.Defer<DocumentEvent>(new DocumentEvent.HeldForApproval(doc.Id)),
            action);
    }

    // --- ApplyEvent: the fold ---

    [Fact]
    public void Applying_CreateOrUpdateRequested_stores_the_document_pending()
    {
        var doc = Doc();
        var evt = TestEnvelope.Event<DocumentEvent>(
            new DocumentEvent.CreateOrUpdateRequested(doc, Owner()), 1L);

        var state = Aggregate.ApplyEvent(evt, DocumentState.Initial);

        Assert.Equal(doc, state.Document);
        Assert.Equal(1L, state.Version);
        Assert.Equal(Approval.Pending, state.Approval);
    }

    [Fact]
    public void Applying_Updated_replaces_content_and_keeps_status()
    {
        var doc1 = Doc();
        var before = DocumentState.Initial with { Document = doc1, Version = 1L, Approval = Approval.Approved };
        var doc2 = Doc(doc1.Id.Value, "v2 title", "v2 body");
        var evt = TestEnvelope.Event<DocumentEvent>(new DocumentEvent.Updated(doc2), 2L);

        var state = Aggregate.ApplyEvent(evt, before);

        Assert.Equal(doc2, state.Document);
        Assert.Equal(2L, state.Version);
        Assert.Equal(Approval.Approved, state.Approval); // an edit doesn't reset approval
    }

    [Fact]
    public void Applying_Approved_marks_the_document_approved()
    {
        var doc = Doc();
        var before = DocumentState.Initial with { Document = doc, Version = 1L };
        var evt = TestEnvelope.Event<DocumentEvent>(new DocumentEvent.Approved(doc.Id), 2L);

        var state = Aggregate.ApplyEvent(evt, before);

        Assert.Equal(Approval.Approved, state.Approval);
    }

    [Fact]
    public void Applying_an_unrelated_event_leaves_state_untouched()
    {
        var before = DocumentState.Initial with { Document = Doc(), Version = 5L };
        var evt = TestEnvelope.Event<DocumentEvent>(
            new DocumentEvent.Error(new DocumentError.DocumentNotFound()), 5L);

        var after = Aggregate.ApplyEvent(evt, before);

        Assert.Equal(before, after);
    }
}
