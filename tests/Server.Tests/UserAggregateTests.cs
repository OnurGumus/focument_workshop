// Tier 1 — pure tests for the User aggregate's sliding-window quota.
//
// The aggregate is deliberately clock-free: HandleCommand decides from the
// command's timestamp and ApplyEvent folds only the timestamp carried on the
// event. That is what keeps event sourcing deterministic on replay — and it is
// also what makes these tests possible without waiting in real time. We control
// time with a FakeTimeProvider and stamp each command through it via
// TestEnvelope, so the whole window can be exercised in microseconds.

using System;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Model;
using Server;
using Xunit;
using static FCQRS.CSharp;

namespace Server.Tests;

public class UserAggregateTests
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly UserAggregate Aggregate = new(NullLogger<UserAggregate>.Instance);

    // Decide ConsumeQuota for a document at the FakeTimeProvider's current
    // instant, and — when approved — fold the resulting event so the window
    // advances, mirroring how the runtime persists then applies. Each call uses
    // a fresh document unless one is given (to exercise re-delivery).
    private static (bool Approved, UserState State) Consume(
        UserState state, TimeProvider time, System.Guid? docId = null)
    {
        var id = DocumentId.CreateFrom(docId ?? System.Guid.NewGuid());
        var cmd = TestEnvelope.Command<UserCommand>(new UserCommand.ConsumeQuota(id), time);
        var action = Aggregate.HandleCommand(cmd, state);

        // A first grant changes state → persisted, so we fold it.
        if (action is FCQRS.Common.EventAction<UserEvent>.PersistEvent { Item: UserEvent.QuotaApproved approved })
        {
            // The event carries the decision time — fold exactly that.
            var evt = TestEnvelope.Event<UserEvent>(approved, 1L, time);
            return (true, Aggregate.ApplyEvent(evt, state));
        }

        // A re-grant for a document that already holds a slot is an idempotent
        // no-op → deferred (re-delivered to the saga, not journaled), nothing to fold.
        if (action is FCQRS.Common.EventAction<UserEvent>.DeferEvent { Item: UserEvent.QuotaApproved })
            return (true, state);

        Assert.True(
            action is FCQRS.Common.EventAction<UserEvent>.DeferEvent { Item: UserEvent.QuotaRejected },
            $"expected QuotaApproved or QuotaRejected, got {action}");
        return (false, state);
    }

    [Fact]
    public void Allows_up_to_the_limit_then_rejects_within_the_window()
    {
        var time = new FakeTimeProvider(Start);
        var state = UserState.Initial;

        for (var i = 0; i < UserAggregate.Limit; i++)
        {
            bool approved;
            (approved, state) = Consume(state, time);
            Assert.True(approved, $"slot {i + 1} should be approved");
            time.Advance(TimeSpan.FromSeconds(10));
        }

        // One past the limit, still inside the one-minute window → rejected.
        var (overLimit, _) = Consume(state, time);
        Assert.False(overLimit);
    }

    [Fact]
    public void Allows_again_once_the_window_has_slid_past_old_slots()
    {
        var time = new FakeTimeProvider(Start);
        var state = UserState.Initial;

        for (var i = 0; i < UserAggregate.Limit; i++)
            (_, state) = Consume(state, time);

        // Still full right now.
        Assert.False(Consume(state, time).Approved);

        // Let the window slide past all earlier slots, then it's allowed again.
        time.Advance(UserAggregate.Window + TimeSpan.FromSeconds(1));
        var (approved, _) = Consume(state, time);
        Assert.True(approved);
    }

    [Fact]
    public void Re_consuming_the_same_document_does_not_take_a_second_slot()
    {
        var time = new FakeTimeProvider(Start);
        var doc = System.Guid.NewGuid();
        var state = UserState.Initial;

        (_, state) = Consume(state, time, doc);
        Assert.Single(state.Consumed);

        // The saga re-issues ConsumeQuota verbatim on recovery: still approved,
        // but the slot is not consumed twice.
        var (approved, after) = Consume(state, time, doc);
        Assert.True(approved);
        Assert.Single(after.Consumed);
    }

    [Fact]
    public void ApplyEvent_folds_only_the_event_timestamp()
    {
        var docId = DocumentId.CreateFrom(System.Guid.NewGuid());
        var consumedAt = Start.UtcDateTime;
        var evt = TestEnvelope.Event<UserEvent>(new UserEvent.QuotaApproved(docId, consumedAt), 1L);

        var state = Aggregate.ApplyEvent(evt, UserState.Initial);

        Assert.Equal(new[] { new Consumption(docId, consumedAt) }, state.Consumed);
    }
}
