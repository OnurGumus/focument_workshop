// Read-only queries against the projected read model.

using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using Dapper;

namespace Server;

public static class ServerQuery
{
    // The last event offset the projection processed; used to resume on startup.
    public static long GetLastOffset(string connString)
    {
        using var conn = new SqliteConnection(connString);
        conn.Open();
        return conn.QueryFirstOrDefault<long>(
            "SELECT OffsetCount FROM Offsets WHERE OffsetName = 'DocumentProjection'");
    }

    // All documents, most recently updated first.
    public static List<Query.Document> GetDocuments(string connString)
    {
        using var conn = new SqliteConnection(connString);
        conn.Open();
        return conn.Query<Query.Document>(
            "SELECT Id, Title, Body, Version, CreatedAt, UpdatedAt FROM Documents ORDER BY UpdatedAt DESC")
            .ToList();
    }

    // Every version of one document, newest first (for time travel).
    public static List<Query.DocumentVersion> GetDocumentHistory(string connString, string docId)
    {
        using var conn = new SqliteConnection(connString);
        conn.Open();
        return conn.Query<Query.DocumentVersion>(
            "SELECT Id, Version, Title, Body, CreatedAt FROM DocumentVersions WHERE Id = @Id ORDER BY Version DESC",
            new { Id = docId })
            .ToList();
    }
}
