using System.Text;
using System.Text.RegularExpressions;

namespace Ssis.Extract.Model.Diagnostics;

/// <summary>
/// Writes a compact, client-data-free crash report for an unhandled exception hit while running
/// <c>ssisx</c>/<c>svk</c> at a client site, where nothing at all can be ported off the client's
/// own machine -- this exists purely so a person there can screenshot ONE small .txt and share it
/// back for the tool itself to be fixed, without sending any real package/column/table/file
/// content.
///
/// <b>What is deliberately NEVER written here, and why:</b> <see cref="Exception.Message"/> (this
/// codebase's own conventions -- gap messages, load-failure reasons, and plenty of BCL exceptions
/// like <see cref="IOException"/>/<see cref="ArgumentException"/> -- routinely embed the exact
/// value, column name, table name, or file path that triggered the failure); any command-line
/// ARGUMENT VALUE (a path, a package name, a namespace prefix -- only the flag NAMES the tool
/// itself defines are safe, via <see cref="ScrubArgs"/>); any stack frame outside this tool's own
/// source (a framework/BCL frame carries no client data either, but enumerating it defeats the
/// point of "compact" and buys nothing a developer needs); the absolute file path a frame's
/// source line came from (only the bare source FILE NAME + line number -- this tool's own,
/// checked-in source structure, not anything about the client's machine).
///
/// What IS captured: the exception type chain (type names only), HResult, and a stack trace
/// filtered to frames whose declaring type lives in this tool's own namespaces, with everything
/// else collapsed to a frame count rather than dropped silently.
/// </summary>
public static class DiagnosticReport
{
    // Only a frame whose method's declaring-type name starts with one of these is treated as
    // "this tool's own code" and shown in full. Every other prefix (BCL, EF Core, a third-party
    // NuGet package, ...) collapses into a bare count -- see the class doc comment for why.
    private static readonly string[] OwnCodePrefixes =
    [
        "Ssis.Extract.", "Ssis.Runtime.Expressions.", "Svk.", "Etl.Core.",
    ];

    // .NET's Exception.StackTrace line shape:
    //   "   at Namespace.Type.Method(ParamType p1, ...) in C:\...\File.cs:line 123"
    // The "in ...:line N" suffix is OPTIONAL (absent when no PDB / the frame was inlined).
    private static readonly Regex StackFrameRegex = new(
        @"^\s*at\s+(?<method>[^(]+)\([^)]*\)(?:\s+in\s+.*[\\/](?<file>[^\\/]+):line\s+(?<line>\d+))?",
        RegexOptions.Compiled);

    private const int MaxOwnFrames = 10;

    /// <summary>
    /// Builds a safe-to-share echo of a command invocation: only the FLAG NAMES a caller passed
    /// (e.g. "--recursive", "--framework"), never a flag's value -- a value can be a client path,
    /// package name, or namespace prefix, and there is no per-flag allowlist to maintain or get
    /// out of date this way. Safe by construction against any flag this tool defines today or
    /// adds later.
    /// </summary>
    public static string ScrubArgs(string tool, string command, IReadOnlyList<string> args)
    {
        var flagNames = args.Where(a => a.StartsWith("--", StringComparison.Ordinal));
        return $"{tool} {command} {string.Join(' ', flagNames)}".TrimEnd();
    }

    /// <summary>
    /// Finds the value that follows a bare <c>--out</c> in a raw, not-yet-parsed argument list --
    /// used ONLY to decide where <see cref="Capture"/> should WRITE the diagnostic file (so a
    /// crash reached before a command finishes parsing its own flags still lands the file next to
    /// everything else this run produced, not in whatever the current directory happens to be),
    /// never to put that value INTO the report's own content.
    /// </summary>
    public static string? TryFindOutDir(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--out") return args[i + 1];
        }
        return null;
    }

    /// <summary>
    /// Writes the report and returns the full path written (or a placeholder string if the write
    /// itself failed, e.g. a read-only output directory -- the exception's own detail is then
    /// printed to stderr as a last resort, since failing to WRITE a diagnostic must never crash
    /// the tool a second time). <paramref name="commandLine"/> should come from
    /// <see cref="ScrubArgs"/>. <paramref name="context"/> must already be safe: ordinals/counts
    /// ("package 4 of 12"), never a name pulled from client data.
    /// </summary>
    public static string Capture(
        string tool,
        string commandLine,
        string phase,
        Exception ex,
        IReadOnlyList<(string Key, string Value)>? context = null,
        string? outDir = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"===== {tool} diagnostic report -- no client data included, safe to screenshot and share =====");
        sb.AppendLine($"Time (UTC) : {DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}");
        sb.AppendLine($"Runtime    : .NET {Environment.Version} / {Environment.OSVersion.Platform}");
        sb.AppendLine($"Command    : {commandLine}");
        sb.AppendLine($"Phase      : {phase}");
        if (context is { Count: > 0 })
        {
            foreach (var (key, value) in context)
            {
                sb.AppendLine($"{key,-11}: {value}");
            }
        }
        sb.AppendLine();
        AppendExceptionChain(sb, ex);
        sb.AppendLine("=================================================================================");

        var fileName = $"{tool}-diagnostic-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt";
        var targetDir = string.IsNullOrEmpty(outDir) ? Directory.GetCurrentDirectory() : outDir;
        var path = Path.Combine(targetDir, fileName);
        try
        {
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch
        {
            // A read-only/missing output dir must not turn "report a crash" into a second,
            // less diagnosable crash -- fall back to printing the same (already-scrubbed)
            // content to stderr instead.
            Console.Error.WriteLine(sb.ToString());
            return "(could not write the diagnostic file -- printed to stderr instead)";
        }
    }

    private static void AppendExceptionChain(StringBuilder sb, Exception ex)
    {
        var depth = 0;
        Exception? current = ex;
        while (current is not null && depth < 5)
        {
            sb.AppendLine(depth == 0
                ? $"Exception  : {current.GetType().FullName}  (HResult 0x{current.HResult:X8})"
                : $"  caused by: {current.GetType().FullName}  (HResult 0x{current.HResult:X8})");
            current = current.InnerException;
            depth++;
        }

        sb.AppendLine("Stack (this tool's own frames only -- framework/library frames collapsed):");
        AppendFilteredStack(sb, ex.StackTrace);
    }

    private static void AppendFilteredStack(StringBuilder sb, string? stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace))
        {
            sb.AppendLine("  (no stack trace available)");
            return;
        }

        var shown = 0;
        var collapsedRun = 0;
        foreach (var rawLine in stackTrace.Split('\n'))
        {
            var m = StackFrameRegex.Match(rawLine);
            if (!m.Success) continue;

            var method = m.Groups["method"].Value.Trim();
            var isOwnCode = OwnCodePrefixes.Any(p => method.StartsWith(p, StringComparison.Ordinal));
            if (!isOwnCode)
            {
                collapsedRun++;
                continue;
            }

            if (collapsedRun > 0)
            {
                sb.AppendLine($"  ... {collapsedRun} framework/library frame(s) omitted ...");
                collapsedRun = 0;
            }

            if (shown >= MaxOwnFrames)
            {
                sb.AppendLine("  ... (+ more own-code frames omitted for brevity) ...");
                break;
            }

            var shortMethod = TrimToLastTwoSegments(method);
            var loc = m.Groups["file"].Success ? $"{m.Groups["file"].Value}:{m.Groups["line"].Value}" : "no line info";
            sb.AppendLine($"  {shortMethod}  [{loc}]");
            shown++;
        }

        if (collapsedRun > 0)
        {
            sb.AppendLine($"  ... {collapsedRun} framework/library frame(s) omitted ...");
        }
        if (shown == 0)
        {
            sb.AppendLine("  (no recognizable frame from this tool's own code -- likely a framework-level failure)");
        }
    }

    private static string TrimToLastTwoSegments(string fullyQualifiedMethod)
    {
        var parts = fullyQualifiedMethod.Split('.');
        return parts.Length <= 2 ? fullyQualifiedMethod : string.Join('.', parts[^2..]);
    }
}
