// The write-side model for the User aggregate, whose only job (for now) is to
// own a per-user write quota: at most 5 document create/update actions per
// rolling hour. The quota is a domain concept here — the saga asks the User to
// consume a slot, and the User approves or rejects.

using System;
using System.Diagnostics.CodeAnalysis;
using static FCQRS.Model.CSharp;
using static FCQRS.Model.Data;

namespace Model;

// Username identifies the person making a request. It doubles as the User
// aggregate's id, so the saga can route a quota check to the right User.
public readonly record struct Username(ShortString Value)
{
    public static bool TryCreate(string s, [NotNullWhen(true)] out Username result)
    {
        if (StringTypes.TryCreateShortString(s, out var v)) { result = new Username(v); return true; }
        result = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}

// Commands are intentions. ConsumeQuota asks the user to spend one slot for a
// specific document. The DocId makes it idempotent: the saga re-issues this
// command on crash-recovery (at-least-once delivery), and the aggregate must
// not consume a second slot for the same document. The decision uses the
// command's own timestamp (see the aggregate) so replay stays deterministic.
public union UserCommand(UserCommand.ConsumeQuota)
{
    public record ConsumeQuota(DocumentId DocId);
}

// Events are facts. QuotaApproved carries the document it was for (the dedup
// key) and the moment the slot was consumed, so ApplyEvent never folds
// wall-clock time; QuotaRejected means the window is full.
public union UserEvent(UserEvent.QuotaApproved, UserEvent.QuotaRejected)
{
    public record QuotaApproved(DocumentId DocId, DateTime ConsumedAt);
    public record QuotaRejected;
}
