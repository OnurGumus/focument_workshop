// The write-side domain model: value objects, the Document aggregate root,
// and the command/event/error unions. No persistence or actor code here.

using System;
using System.Diagnostics.CodeAnalysis;
using static FCQRS.Model.CSharp;
using static FCQRS.Model.Data;

namespace Model;

// A document's identity. We can parse one off the wire with TryParse.
public readonly record struct DocumentId(Guid Value)
{
    public static DocumentId Create() => new(Guid.NewGuid());
    public static DocumentId CreateFrom(Guid g) => new(g);

    public static bool TryParse(string s, [NotNullWhen(true)] out DocumentId result)
    {
        if (Guid.TryParse(s, out var g)) { result = new DocumentId(g); return true; }
        result = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}

// Title wraps FCQRS's length-limited ShortString.
public readonly record struct Title(ShortString Value)
{
    public static bool TryCreate(string s, [NotNullWhen(true)] out Title result)
    {
        if (StringTypes.TryCreateShortString(s, out var v)) { result = new Title(v); return true; }
        result = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}

// Content wraps the larger LongString.
public readonly record struct Content(LongString Value)
{
    public static bool TryCreate(string s, [NotNullWhen(true)] out Content result)
    {
        if (StringTypes.TryCreateLongString(s, out var v)) { result = new Content(v); return true; }
        result = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}

// The aggregate root. TryCreate validates each field before building one.
public sealed record Document(DocumentId Id, Title Title, Content Content)
{
    public static bool TryCreate(
        Guid docId,
        string title,
        string content,
        [NotNullWhen(true)] out Document? result,
        [NotNullWhen(false)] out string? error)
    {
        if (!Title.TryCreate(title, out var t)) { result = null; error = "Invalid title"; return false; }
        if (!Content.TryCreate(content, out var c)) { result = null; error = "Invalid content"; return false; }

        result = new Document(DocumentId.CreateFrom(docId), t, c);
        error = null;
        return true;
    }
}

// Commands are intentions. A first write is requested by an Owner (quota-gated
// via the saga); the saga then issues Approve or Hold. Approve/Reject are also
// the colleague-approval actions. Updating an existing document skips the saga.
public union DocumentCommand(
    DocumentCommand.CreateOrUpdate,
    DocumentCommand.Approve,
    DocumentCommand.Reject,
    DocumentCommand.Hold)
{
    public record CreateOrUpdate(Document Document, Username Owner);
    public record Approve;
    public record Reject;
    public record Hold;
}

// Business-rule violations.
public union DocumentError(DocumentError.DocumentNotFound)
{
    public record DocumentNotFound;
}

// Events are facts. CreateOrUpdateRequested records a new pending document and
// starts the quota saga; Updated is a plain edit (no saga, no quota); Approved/
// Rejected/HeldForApproval are the saga's or a colleague's verdict.
public union DocumentEvent(
    DocumentEvent.CreateOrUpdateRequested,
    DocumentEvent.Updated,
    DocumentEvent.Error,
    DocumentEvent.Approved,
    DocumentEvent.Rejected,
    DocumentEvent.HeldForApproval)
{
    public record CreateOrUpdateRequested(Document Document, Username Owner);
    public record Updated(Document Document);
    public record Error(DocumentError ErrorDetails);
    public record Approved(DocumentId DocumentId);
    public record Rejected(DocumentId DocumentId);
    public record HeldForApproval(DocumentId DocumentId);
}
