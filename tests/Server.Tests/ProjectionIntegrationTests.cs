// Tier 2 — integration through the real stack: Document + User aggregates, the
// quota saga, the projection, and (for colleague approval) the real HTTP handler
// over a DefaultHttpContext. Creation is quota-gated; edits are free; over-quota
// writes are parked for a colleague to approve.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Model;
using Server;
using Xunit;
using static FCQRS.CSharp;
using static FCQRS.Query;
using FCQRS;

namespace Server.Tests;

// Builds the app exactly the way Program.cs does — the same AddFocument
// registration in a real host — then starts it and resolves the pieces the tests
// drive. Exercising the real DI wiring (not a hand-built stand-in) is the point.
public sealed class AppFixture : IDisposable
{
    public Handler<DocumentCommand, DocumentEvent> DocumentHandler { get; }
    public ISubscribe Subscriptions { get; }
    public string ConnectionString { get; }
    public Endpoints Endpoints { get; }

    private readonly ServiceProvider _services;
    private readonly string _dbPath;

    public AppFixture()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"focument_test_{Guid.NewGuid():N}.db");
        ConnectionString = $"Data Source={_dbPath};";

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddFocument(ConnectionString);
        _services = services.BuildServiceProvider();

        // Run FCQRS's startup wiring (aggregates → saga → saga-starter → projection).
        foreach (var hosted in _services.GetServices<IHostedService>())
            hosted.StartAsync(default).GetAwaiter().GetResult();

        DocumentHandler = _services.GetRequiredService<Handler<DocumentCommand, DocumentEvent>>();
        Subscriptions = _services.GetRequiredService<ISubscribe>();
        Endpoints = new Endpoints(ConnectionString, Subscriptions, DocumentHandler, NullLogger<Endpoints>.Instance);
    }

    public void Dispose()
    {
        _services.Dispose();
        foreach (var f in Directory.GetFiles(
                     Path.GetDirectoryName(_dbPath)!,
                     Path.GetFileName(_dbPath) + "*"))
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
    }
}

public class ProjectionIntegrationTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;
    public ProjectionIntegrationTests(AppFixture fixture) => _app = fixture;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static string FreshUser() => "u" + Guid.NewGuid().ToString("N")[..8];

    // Drive a create/update through the stack; returns the outcome.
    private async Task<string> Save(Guid id, string title, string body, string username)
    {
        Assert.True(Document.TryCreate(id, title, body, out var doc, out var err), err);
        Assert.True(Username.TryCreate(username, out var owner));

        var cid = Values.NewCID();
        var aggregateId = Values.CreateAggregateId(id.ToString());
        using var awaiter = _app.Subscriptions.SubscribeForFirst(cid);

        var result = await _app.DocumentHandler(
                e => e is DocumentEvent.Approved or DocumentEvent.HeldForApproval or DocumentEvent.Updated,
                cid, aggregateId, new DocumentCommand.CreateOrUpdate(doc!, owner))
            .WaitAsync(Timeout);

        await awaiter.Task.WaitAsync(Timeout);
        return result.EventDetails switch
        {
            DocumentEvent.HeldForApproval => "AwaitingApproval",
            DocumentEvent.Updated => "Updated",
            _ => "Approved"
        };
    }

    // Colleague approve/reject through the real HTTP handler (exercises the owner check).
    private Task<string> Approve(Guid id, string username) => Decide("approve", id, username);
    private Task<string> Reject(Guid id, string username) => Decide("reject", id, username);

    private Task<string> Decide(string action, Guid id, string username)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["Id"] = id.ToString(),
            ["Username"] = username
        });
        return _app.Endpoints.ReviewDocument(ctx, approve: action == "approve");
    }

    private Query.Document Find(Guid id) =>
        ServerQuery.GetDocuments(_app.ConnectionString).Single(d => d.Id == id.ToString());

    [Fact]
    public async Task A_creation_under_quota_is_approved_and_projected()
    {
        var id = Guid.NewGuid();

        Assert.Equal("Approved", await Save(id, "Hello", "World", FreshUser()));

        var saved = Find(id);
        Assert.Equal("Hello", saved.Title);
        Assert.Equal("Approved", saved.ApprovalStatus);
        Assert.Equal(1, saved.Version);
    }

    [Fact]
    public async Task A_creation_past_the_limit_is_held_for_approval()
    {
        var user = FreshUser();
        for (var i = 0; i < UserAggregate.Limit; i++)
            Assert.Equal("Approved", await Save(Guid.NewGuid(), $"doc {i}", "body", user));

        var heldId = Guid.NewGuid();
        Assert.Equal("AwaitingApproval", await Save(heldId, "over", "limit", user));
        Assert.Equal("AwaitingApproval", Find(heldId).ApprovalStatus);
    }

    [Fact]
    public async Task Edits_do_not_consume_quota()
    {
        var user = FreshUser();
        var ids = new List<Guid>();
        for (var i = 0; i < UserAggregate.Limit; i++)
        {
            var id = Guid.NewGuid();
            Assert.Equal("Approved", await Save(id, $"doc {i}", "body", user));
            ids.Add(id);
        }

        // Quota is now full, yet editing existing docs still works (no quota hit).
        for (var i = 0; i < 4; i++)
            Assert.Equal("Updated", await Save(ids[0], "edit", $"body {i}", user));

        Assert.Equal("Approved", Find(ids[0]).ApprovalStatus); // edits keep status
    }

    [Fact]
    public async Task A_colleague_can_approve_a_held_document()
    {
        var alice = FreshUser();
        for (var i = 0; i < UserAggregate.Limit; i++)
            await Save(Guid.NewGuid(), $"doc {i}", "body", alice);

        var heldId = Guid.NewGuid();
        Assert.Equal("AwaitingApproval", await Save(heldId, "over", "limit", alice));

        Assert.Equal("Approved!", await Approve(heldId, FreshUser()));
        Assert.Equal("Approved", Find(heldId).ApprovalStatus);
    }

    [Fact]
    public async Task An_owner_cannot_approve_their_own_held_document()
    {
        var alice = FreshUser();
        for (var i = 0; i < UserAggregate.Limit; i++)
            await Save(Guid.NewGuid(), $"doc {i}", "body", alice);

        var heldId = Guid.NewGuid();
        Assert.Equal("AwaitingApproval", await Save(heldId, "over", "limit", alice));

        var message = await Approve(heldId, alice);
        Assert.Contains("can't approve your own", message);
        Assert.Equal("AwaitingApproval", Find(heldId).ApprovalStatus); // unchanged
    }

    [Fact]
    public async Task Updating_a_document_bumps_the_version_and_records_history()
    {
        var user = FreshUser();
        var id = Guid.NewGuid();

        Assert.Equal("Approved", await Save(id, "v1 title", "v1 body", user));
        Assert.Equal("Updated", await Save(id, "v2 title", "v2 body", user));

        var current = Find(id);
        Assert.Equal("v2 title", current.Title);
        Assert.Equal(2, current.Version);

        var history = ServerQuery.GetDocumentHistory(_app.ConnectionString, id.ToString());
        Assert.Equal(new long[] { 2, 1 }, history.Select(v => v.Version).ToArray());
    }
}
