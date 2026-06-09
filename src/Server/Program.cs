// The delivery layer: a minimal ASP.NET app over the composition root.
//
// The whole write side is registered with one call — builder.Services.AddFocument
// (see App.cs) — and FCQRS starts it with the host. The HTTP endpoints live in an
// injected Endpoints instance (its cross-cutting deps wired once), so each map is
// a one-liner. No security or ops middleware — this is a teaching app meant to
// run on localhost.

using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Model;
using Server;
using static FCQRS.CSharp;
using static FCQRS.Query;

var builder = WebApplication.CreateBuilder(args);

// Compact one-line console logs (format + which categories show: appsettings.json).
builder.Logging.AddConsoleFormatter<WorkshopConsoleFormatter, WorkshopFormatterOptions>();

var dbPath = Environment.GetEnvironmentVariable("FOCUMENT_DB_PATH") ?? "focument_workshop.db";
var connectionString = $"Data Source={dbPath};";

// Register the write side, projection and command handler in one call.
builder.Services.AddFocument(connectionString);

// The HTTP endpoints as one injected instance: the read-model connection plus the
// pieces FCQRS wired (the subscription and the command handler).
builder.Services.AddSingleton(sp => new Endpoints(
    connectionString,
    sp.GetRequiredService<ISubscribe>(),
    sp.GetRequiredService<Handler<DocumentCommand, DocumentEvent>>(),
    sp.GetRequiredService<ILogger<Endpoints>>()));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/documents", (Endpoints endpoints) => endpoints.GetDocuments());
app.MapGet("/api/document/{id}/history", (Endpoints endpoints, HttpContext ctx) => endpoints.GetDocumentHistory(ctx));
app.MapPost("/api/document", (Endpoints endpoints, HttpContext ctx) => endpoints.CreateOrUpdateDocument(ctx));
app.MapPost("/api/document/restore", (Endpoints endpoints, HttpContext ctx) => endpoints.RestoreVersion(ctx));

app.Run();
