// A compact, colored, one-line console format for workshop clarity:
//     12:34:56 info DocumentAggregate  handle CreateOrUpdate
// timestamp (dim) + level (green/yellow/red by severity) + short category (a stable
// per-category hue, so the saga flow is easy to follow across actors) + message.
// The timestamp format and which categories show are in appsettings.json; colors
// honor ColorBehavior (default: on at a terminal, off when output is redirected).

using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Server;

// The base options (TimestampFormat, UseUtcTimestamp) plus a color toggle —
// mirroring the built-in simple console formatter.
public sealed class WorkshopFormatterOptions : ConsoleFormatterOptions
{
    public LoggerColorBehavior ColorBehavior { get; set; }
}

public sealed class WorkshopConsoleFormatter : ConsoleFormatter, IDisposable
{
    public const string FormatterName = "workshop";

    private readonly IDisposable? _optionsReload;
    private WorkshopFormatterOptions _options;

    public WorkshopConsoleFormatter(IOptionsMonitor<WorkshopFormatterOptions> options)
        : base(FormatterName)
    {
        _options = options.CurrentValue;
        _optionsReload = options.OnChange(o => _options = o);
    }

    public override void Write<TState>(
        in LogEntry<TState> entry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = entry.Formatter?.Invoke(entry.State, entry.Exception);
        if (message is null)
            return;

        var color = _options.ColorBehavior switch
        {
            LoggerColorBehavior.Disabled => false,
            LoggerColorBehavior.Enabled => true,
            _ => !Console.IsOutputRedirected,
        };

        // timestamp (dim), if a format is configured.
        if (!string.IsNullOrEmpty(_options.TimestampFormat))
        {
            var now = _options.UseUtcTimestamp ? DateTimeOffset.UtcNow : DateTimeOffset.Now;
            Paint(textWriter, color, Dim, now.ToString(_options.TimestampFormat));
        }

        // "Server.DocumentAggregate" -> "DocumentAggregate"; "Projection" stays as-is.
        var category = entry.Category;
        var shortCategory = category[(category.LastIndexOf('.') + 1)..];

        Paint(textWriter, color, LevelColor(entry.LogLevel), Level(entry.LogLevel));
        textWriter.Write(' ');
        Paint(textWriter, color, CategoryColor(shortCategory), shortCategory.PadRight(18));
        textWriter.Write(' ');
        textWriter.WriteLine(message);

        if (entry.Exception is not null)
            textWriter.WriteLine(entry.Exception);
    }

    private const string Dim = "90";

    private static void Paint(TextWriter writer, bool color, string code, string text)
    {
        if (color)
            writer.Write($"\x1b[{code}m{text}\x1b[0m");
        else
            writer.Write(text);
    }

    // Four-char level tags, matching the built-in console formatter's convention.
    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "    ",
    };

    private static string LevelColor(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "90",   // gray
        LogLevel.Information => "32",                // green
        LogLevel.Warning => "33",                   // yellow
        LogLevel.Error or LogLevel.Critical => "31", // red
        _ => "0",
    };

    // Curated, stable hue per actor — cool colors kept clear of the warm level
    // colors (green/yellow/red). Document and its Projection share the cyan family
    // (the read model is the document's projection); the QuotaSaga — the cross-
    // aggregate coordinator — pops in magenta.
    private static string CategoryColor(string category) => category switch
    {
        "DocumentAggregate" => "36", // cyan
        "Projection"        => "96", // bright cyan (the document's read side)
        "UserAggregate"     => "34", // blue
        "QuotaSaga"         => "35", // magenta (the coordinator)
        "Lifetime"          => "90", // dim — host/startup lines
        _                   => "39", // terminal default for anything else
    };

    public void Dispose() => _optionsReload?.Dispose();
}
