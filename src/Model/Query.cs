// The read side: a plain DTO that maps to a SQLite row. No validation and no
// behaviour — the data was already validated when it was written.

namespace Query;

public class Document
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public long Version { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    // Pending → Approved | AwaitingApproval (held) → Approved | Rejected.
    public string ApprovalStatus { get; set; } = "Pending";
    // The user who created the document; a colleague (Owner != them) can approve it.
    public string Owner { get; set; } = "";
}

// One historical version of a document — the raw material for time travel.
public class DocumentVersion
{
    public string Id { get; set; } = "";
    public long Version { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}
