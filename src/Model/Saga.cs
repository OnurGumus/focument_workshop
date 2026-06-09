// The quota saga's own state machine, kept in the Model with the other
// serializable domain types (FCQRS persists saga state as it transitions).
// The saga starts when a document write is requested, asks the User aggregate
// to consume a quota slot, then tells the Document to approve or reject.

namespace Model;

// No cross-step data is needed — each state carries what its side effect needs.
public sealed record QuotaSagaData;

public union QuotaState(
    QuotaState.CheckingQuota,
    QuotaState.Approving,
    QuotaState.Holding,
    QuotaState.Done)
{
    // Waiting to hear the quota verdict; remembers who and which document.
    public record CheckingQuota(Username Owner, DocumentId DocId);
    // Quota granted — tell the document to approve.
    public record Approving(DocumentId DocId);
    // Quota denied — tell the document to hold for a colleague's approval.
    public record Holding(DocumentId DocId);
    // Final state — stop the saga.
    public record Done;
}
