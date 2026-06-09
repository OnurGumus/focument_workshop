// The HTTP endpoints bridging the web API to the CQRS domain. The same
// read-your-writes flow, driven by an HTTP form. (Distinct from FCQRS's
// Handler<,> delegate, which is what these call to send a command.)
//
// An instance class: the cross-cutting dependencies (the read-model connection
// string, the projection subscription, the document command handler and a
// logger) are injected once via the constructor, so each endpoint method takes
// only the request — the HttpContext (and, for a review, the verdict).

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Model;
using static FCQRS.CSharp;
using static FCQRS.Query;
using FCQRS;

namespace Server;

public sealed class Endpoints(
    string connString,
    ISubscribe subs,
    Handler<DocumentCommand, DocumentEvent> documentHandler,
    ILogger<Endpoints> log)
{
    public Query.Document[] GetDocuments() =>
        ServerQuery.GetDocuments(connString).ToArray();

    public Query.DocumentVersion[] GetDocumentHistory(HttpContext ctx)
    {
        var id = ctx.Request.RouteValues["id"]?.ToString() ?? "";
        return ServerQuery.GetDocumentHistory(connString, id).ToArray();
    }

    public async Task<string> CreateOrUpdateDocument(HttpContext ctx)
    {
        try
        {
            var form = await ctx.Request.ReadFormAsync();
            var title = form["Title"].ToString();
            var content = form["Content"].ToString();
            var existingId = form["Id"].ToString();
            var username = form["Username"].ToString();

            if (!Username.TryCreate(username, out var owner))
                return "Error: a username is required";

            var docId = string.IsNullOrEmpty(existingId)
                ? Guid.NewGuid()
                : Guid.Parse(existingId);

            if (!Document.TryCreate(docId, title, content, out var document, out var docError))
                return $"Error: {docError}";

            var cid = Values.NewCID();
            var aggregateId = Values.CreateAggregateId(docId.ToString());

            // Subscribe before sending so we can wait for the read model to catch
            // up. The projection notifies only terminal events, so one is enough,
            // whether this turns out to be a create (Approved/HeldForApproval) or
            // an edit of an existing doc (Updated).
            using var awaiter = subs.SubscribeForFirst(cid);

            var result = await documentHandler(
                e => e is DocumentEvent.Approved or DocumentEvent.HeldForApproval or DocumentEvent.Updated,
                cid,
                aggregateId,
                new DocumentCommand.CreateOrUpdate(document, owner));

            await awaiter.Task;
            return result.EventDetails switch
            {
                DocumentEvent.HeldForApproval => "Quota exceeded — sent for approval.",
                DocumentEvent.Updated => "Document updated!",
                _ => "Document saved!"
            };
        }
        catch (Exception ex)
        {
            log.LogError(ex, "CreateOrUpdateDocument failed");
            return "Error: something went wrong";
        }
    }

    // Restore an earlier version by re-issuing it as a new CreateOrUpdate.
    // We never rewrite history — restoring simply adds a new version whose
    // content matches the old one.
    public async Task<string> RestoreVersion(HttpContext ctx)
    {
        try
        {
            var form = await ctx.Request.ReadFormAsync();
            var docId = form["Id"].ToString();
            var versionStr = form["Version"].ToString();
            var username = form["Username"].ToString();

            if (!Username.TryCreate(username, out var owner))
                return "Error: a username is required";
            if (string.IsNullOrWhiteSpace(docId) || !Guid.TryParse(docId, out var guid))
                return "Error: invalid document id";
            if (!long.TryParse(versionStr, out var version))
                return "Error: invalid version";

            var snapshot = ServerQuery.GetDocumentHistory(connString, docId)
                .Find(v => v.Version == version);
            if (snapshot is null)
                return "Error: version not found";

            if (!Document.TryCreate(guid, snapshot.Title, snapshot.Body, out var document, out var docError))
                return $"Error: {docError}";

            var cid = Values.NewCID();
            var aggregateId = Values.CreateAggregateId(docId);

            using var awaiter = subs.SubscribeForFirst(cid);

            // A restore targets an existing document, so it's a plain edit
            // (Updated) — no quota, no saga.
            await documentHandler(
                e => e is DocumentEvent.Updated or DocumentEvent.Approved or DocumentEvent.HeldForApproval,
                cid,
                aggregateId,
                new DocumentCommand.CreateOrUpdate(document, owner));

            await awaiter.Task;
            return "Version restored!";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "RestoreVersion failed");
            return "Error: something went wrong";
        }
    }

    // A colleague's verdict on a held (over-quota) document — approve or reject.
    // A *different* user finalises it; the owner can't decide their own, which is
    // the whole point.
    public async Task<string> ReviewDocument(HttpContext ctx, bool approve)
    {
        var verb = approve ? "approve" : "reject";
        try
        {
            var form = await ctx.Request.ReadFormAsync();
            var docId = form["Id"].ToString();
            var username = form["Username"].ToString();

            if (!Username.TryCreate(username, out var approver))
                return "Error: a username is required";
            if (string.IsNullOrWhiteSpace(docId) || !Guid.TryParse(docId, out _))
                return "Error: invalid document id";

            // Pre-check that the document exists (an unknown id would otherwise be
            // ignored by the aggregate and the await below would never complete).
            // Whether the reviewer may decide it (separation of duties) is the
            // aggregate's call, not ours — see below.
            if (ServerQuery.GetDocument(connString, docId) is null)
                return "Error: document not found";

            var cid = Values.NewCID();
            var aggregateId = Values.CreateAggregateId(docId);

            using var awaiter = subs.SubscribeForFirst(cid);

            DocumentCommand command = approve
                ? new DocumentCommand.Approve(approver)
                : new DocumentCommand.Reject(approver);

            var result = await documentHandler(
                e => (approve ? e is DocumentEvent.Approved : e is DocumentEvent.Rejected)
                     || e is DocumentEvent.Error,
                cid,
                aggregateId,
                command);

            // The aggregate refused (e.g. self-approval): a deferred Error, so the
            // read model never changes — return the refusal without awaiting it.
            if (result.EventDetails is DocumentEvent.Error)
                return $"You can't {verb} your own document.";

            await awaiter.Task;
            return approve ? "Approved!" : "Rejected.";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "{Verb} failed", verb);
            return "Error: something went wrong";
        }
    }
}
