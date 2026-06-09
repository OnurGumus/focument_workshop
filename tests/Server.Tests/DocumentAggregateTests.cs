// Tier 1 — pure decision/fold tests.
//
// HandleCommand and ApplyEvent are pure functions: given a command (or event)
// and the current state, they return an action (or the next state) with no side
// effects — no actor system, no database, no async. So we test the real
// business rules with plain method calls. This is the fast, deterministic core
// of the test suite, and the payoff of keeping the write side pure.
//
// The only ceremony is wrapping a payload in the Command<>/Event<> envelope the
// handlers expect. FCQRS's TestEnvelope helpers do that — they fill in the
// plumbing fields (a fresh id, timestamp, correlation id, empty metadata) so a
// test supplies only the payload (and, for events, the version).

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

    // --- HandleCommand: the decision ---

    [Fact]
    public void Create_on_empty_state_persists_CreatedOrUpdated()
    {
        var doc = Doc();
        var cmd = TestEnvelope.Command<DocumentCommand>(new DocumentCommand.CreateOrUpdate(doc));

        var action = Aggregate.HandleCommand(cmd, DocumentState.Initial);

        Assert.Equal(
            EventActions.Persist<DocumentEvent>(new DocumentEvent.CreatedOrUpdated(doc)),
            action);
    }

    [Fact]
    public void Update_with_matching_id_persists_CreatedOrUpdated()
    {
        var id = System.Guid.NewGuid();
        var state = DocumentState.Initial with { Document = Doc(id), Version = 1L };
        var updated = Doc(id, "New title", "New body");
        var cmd = TestEnvelope.Command<DocumentCommand>(new DocumentCommand.CreateOrUpdate(updated));

        var action = Aggregate.HandleCommand(cmd, state);

        Assert.Equal(
            EventActions.Persist<DocumentEvent>(new DocumentEvent.CreatedOrUpdated(updated)),
            action);
    }

    [Fact]
    public void Command_for_a_different_id_defers_a_DocumentNotFound_error()
    {
        // This actor already holds one document; a command carrying a different
        // id was misrouted here, so the decision is a deferred rejection.
        var state = DocumentState.Initial with { Document = Doc(System.Guid.NewGuid()), Version = 1L };
        var cmd = TestEnvelope.Command<DocumentCommand>(
            new DocumentCommand.CreateOrUpdate(Doc(System.Guid.NewGuid())));

        var action = Aggregate.HandleCommand(cmd, state);

        // The action is a DeferEvent wrapping Error(DocumentNotFound). EventAction
        // is an F# DU (DeferEvent is a real runtime type), but its payload is a C#
        // union whose case is visible only via pattern matching — GetType() on a
        // union value returns the *union* name, so IsType can't see the case.
        var deferred = Assert.IsType<FCQRS.Common.EventAction<DocumentEvent>.DeferEvent>(action);
        Assert.True(
            deferred.Item is DocumentEvent.Error { ErrorDetails: DocumentError.DocumentNotFound },
            $"expected Error(DocumentNotFound), got {deferred.Item}");
    }

    // --- ApplyEvent: the fold ---

    [Fact]
    public void Applying_CreatedOrUpdated_stores_the_document_and_bumps_the_version()
    {
        var doc = Doc();
        var evt = TestEnvelope.Event<DocumentEvent>(new DocumentEvent.CreatedOrUpdated(doc), 1L);

        var state = Aggregate.ApplyEvent(evt, DocumentState.Initial);

        Assert.Equal(doc, state.Document);
        Assert.Equal(1L, state.Version);
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
