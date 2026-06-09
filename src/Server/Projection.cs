// The projection: it turns the event stream into a read-optimised SQLite table.
// FCQRS replays events past the stored offset and calls HandleEventWrapper for
// each one; we update the Documents table and advance the offset in the same
// transaction so the read model and the offset never drift apart.

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Dapper;
using Model;
using static FCQRS.Model.Data;

namespace Server;

public static class Projection
{
    public static void EnsureTables(string connString)
    {
        using var conn = new SqliteConnection(connString);
        conn.Open();

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS Documents (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                Body TEXT NOT NULL,
                Version INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            )
            """);

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS Offsets (
                OffsetName TEXT PRIMARY KEY,
                OffsetCount INTEGER NOT NULL
            )
            """);

        conn.Execute(
            "INSERT OR IGNORE INTO Offsets (OffsetName, OffsetCount) VALUES ('DocumentProjection', 0)");

        // Every CreatedOrUpdated event appends one row here, so we keep the
        // full history of a document and can restore any earlier version.
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS DocumentVersions (
                Id TEXT NOT NULL,
                Version INTEGER NOT NULL,
                Title TEXT NOT NULL,
                Body TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                PRIMARY KEY (Id, Version)
            )
            """);
    }

    // Called once per event. Returns the events to re-publish to subscribers
    // (used for read-your-writes); an empty list means "nothing to notify".
    public static IList<IMessageWithCID> HandleEventWrapper(
        ILoggerFactory loggerFactory,
        string connString,
        long offsetValue,
        object eventObj)
    {
        var log = loggerFactory.CreateLogger("Projection");

        using var conn = new SqliteConnection(connString);
        conn.Open();
        using var tx = conn.BeginTransaction();

        var notify = new List<IMessageWithCID>();

        if (eventObj is FCQRS.Common.Event<DocumentEvent> docEvent
            && docEvent.EventDetails is DocumentEvent.CreatedOrUpdated created)
        {
            var doc = created.Document;
            var id = doc.Id.ToString();
            var title = doc.Title.ToString();
            var body = doc.Content.ToString();
            var now = docEvent.CreationDate.ToString("o");

            var current = conn.QueryFirstOrDefault<long?>(
                "SELECT Version FROM Documents WHERE Id = @Id", new { Id = id }, tx);
            var version = (current ?? 0) + 1;

            if (current is null)
            {
                conn.Execute("""
                    INSERT INTO Documents (Id, Title, Body, Version, CreatedAt, UpdatedAt)
                    VALUES (@Id, @Title, @Body, @Version, @Now, @Now)
                    """, new { Id = id, Title = title, Body = body, Version = version, Now = now }, tx);
            }
            else
            {
                conn.Execute("""
                    UPDATE Documents
                    SET Title = @Title, Body = @Body, Version = @Version, UpdatedAt = @Now
                    WHERE Id = @Id
                    """, new { Id = id, Title = title, Body = body, Version = version, Now = now }, tx);
            }

            // Append this version to the history.
            conn.Execute("""
                INSERT OR IGNORE INTO DocumentVersions (Id, Version, Title, Body, CreatedAt)
                VALUES (@Id, @Version, @Title, @Body, @Now)
                """, new { Id = id, Version = version, Title = title, Body = body, Now = now }, tx);

            log.LogInformation("projected {Id} v{Version} at offset {Offset}", id, version, offsetValue);
            notify.Add(docEvent);
        }

        conn.Execute(
            "UPDATE Offsets SET OffsetCount = @Offset WHERE OffsetName = 'DocumentProjection'",
            new { Offset = offsetValue }, tx);

        tx.Commit();
        return notify;
    }
}
