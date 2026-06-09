// The HTTP endpoints bridging the web API to the CQRS domain. The same
// read-your-writes flow, driven by an HTTP form. (Distinct from FCQRS's
// Handler<,> delegate, which is what these call to send a command.)
//
// An instance class: the cross-cutting dependencies (the read-model connection
// string, the projection subscription, the document command handler and a
// logger) are injected once via the constructor, so each endpoint method takes
// only the request — the HttpContext.

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

            var docId = string.IsNullOrEmpty(existingId)
                ? Guid.NewGuid()
                : Guid.Parse(existingId);

            if (!Document.TryCreate(docId, title, content, out var document, out var docError))
                return $"Error: {docError}";

            var cid = Values.NewCID();
            var aggregateId = Values.CreateAggregateId(docId.ToString());

            // Subscribe before sending so we can wait for the read model to catch up.
            using var awaiter = subs.SubscribeForFirst(cid);

            await documentHandler(
                e => e is DocumentEvent.CreatedOrUpdated,
                cid,
                aggregateId,
                new DocumentCommand.CreateOrUpdate(document));

            await awaiter.Task;
            return "Document saved!";
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

            await documentHandler(
                e => e is DocumentEvent.CreatedOrUpdated,
                cid,
                aggregateId,
                new DocumentCommand.CreateOrUpdate(document));

            await awaiter.Task;
            return "Version restored!";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "RestoreVersion failed");
            return "Error: something went wrong";
        }
    }
}
