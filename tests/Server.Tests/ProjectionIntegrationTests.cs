// Tier 2 — integration through the real stack.
//
// This builds the *actual* app (App.Build: actor system + projection + command
// handler) over a throwaway SQLite database, then drives it exactly the way the
// HTTP handler does: subscribe for the correlation id, send the command, await
// the projection catching up (read-your-writes), then read the projected model.
//
// "Wait for the projection" is not test scaffolding — it is the same awaiter the
// web layer uses to serve a consistent read. The test reuses the domain flow.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Model;
using Server;
using Xunit;
using static FCQRS.CSharp;
using static FCQRS.Query;
using FCQRS;

namespace Server.Tests;

// Builds the app exactly the way Program.cs does — the same AddFocument
// registration in a real host — then starts it and resolves the pieces the tests
// drive. One actor system is shared by every test in the class (each test uses
// its own document id). Exercising the real DI wiring is the point.
public sealed class AppFixture : IDisposable
{
    public Handler<DocumentCommand, DocumentEvent> DocumentHandler { get; }
    public ISubscribe Subscriptions { get; }
    public string ConnectionString { get; }

    private readonly ServiceProvider _services;
    private readonly string _dbPath;

    public AppFixture()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"focument_test_{Guid.NewGuid():N}.db");
        ConnectionString = $"Data Source={_dbPath};";

        var services = new ServiceCollection();
        // Empty configuration: FCQRS falls back to its embedded default HOCON,
        // which is exactly what Program.cs relies on (no appsettings/hocon ship).
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddFocument(ConnectionString);
        _services = services.BuildServiceProvider();

        // Run FCQRS's startup wiring (aggregate Init + projection).
        foreach (var hosted in _services.GetServices<IHostedService>())
            hosted.StartAsync(default).GetAwaiter().GetResult();

        DocumentHandler = _services.GetRequiredService<Handler<DocumentCommand, DocumentEvent>>();
        Subscriptions = _services.GetRequiredService<ISubscribe>();
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

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Send a CreateOrUpdate and wait for the projection to record it.
    private async Task Save(Guid id, string title, string body)
    {
        Assert.True(Document.TryCreate(id, title, body, out var doc, out var err), err);

        var cid = Values.NewCID();
        var aggregateId = Values.CreateAggregateId(id.ToString());

        // Subscribe before sending so we don't miss the projection's notification.
        using var awaiter = _app.Subscriptions.SubscribeForFirst(cid);

        await _app.DocumentHandler(
            e => e is DocumentEvent.CreatedOrUpdated,
            cid,
            aggregateId,
            new DocumentCommand.CreateOrUpdate(doc!));

        await awaiter.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Creating_a_document_projects_it_to_the_read_model()
    {
        var id = Guid.NewGuid();

        await Save(id, "Hello", "World");

        var saved = ServerQuery.GetDocuments(_app.ConnectionString)
            .Single(d => d.Id == id.ToString());
        Assert.Equal("Hello", saved.Title);
        Assert.Equal("World", saved.Body);
        Assert.Equal(1, saved.Version);
    }

    [Fact]
    public async Task Updating_a_document_bumps_the_version_and_records_history()
    {
        var id = Guid.NewGuid();

        await Save(id, "v1 title", "v1 body");
        await Save(id, "v2 title", "v2 body");

        var current = ServerQuery.GetDocuments(_app.ConnectionString)
            .Single(d => d.Id == id.ToString());
        Assert.Equal("v2 title", current.Title);
        Assert.Equal(2, current.Version);

        // Every write appends a version row — the raw material for time travel.
        var history = ServerQuery.GetDocumentHistory(_app.ConnectionString, id.ToString());
        Assert.Equal(2, history.Count);
        Assert.Equal(new long[] { 2, 1 }, history.Select(v => v.Version).ToArray());
    }
}
