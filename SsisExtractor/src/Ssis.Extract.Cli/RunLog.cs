using System.Text;

namespace Ssis.Extract.Cli;

/// <summary>
/// Persists a command's own console output to a file alongside whatever it already prints live,
/// so a run driven through something like GitHub Copilot chat -- where there is no way to ask a
/// live follow-up once the terminal output has scrolled past -- leaves behind something a human
/// can actually read afterward. Deliberately NOT a replacement for a command's own structured
/// output (<c>generate-report.md</c>, <c>gaps.json</c>, <c>fills-applied.json</c>) -- this is a
/// durable copy of the run's own narration, not a new source of truth.
///
/// Usage: <c>using var _ = RunLog.Start(Path.Combine(outDir, "apply-fills.log"));</c> at the top
/// of a command's <c>Run()</c>, right after <c>--out</c> is known. Every later <c>return</c> in
/// that method restores the console automatically via the <c>using</c>. Safe to use even when a
/// TEST has already redirected <see cref="Console.Out"/>/<see cref="Console.Error"/> to its own
/// <see cref="StringWriter"/> before calling <c>Run()</c> -- this wraps whatever the CURRENT
/// writer is, so that writer still receives every byte it always would have, just duplicated to
/// the file as well.
/// </summary>
internal static class RunLog
{
    public static IDisposable Start(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Overwritten fresh every run, matching this project's own convention for
        // fills-applied.json/generate-report.md -- a log from a prior run describing a package
        // that no longer exists (or no longer has the problem it once had) would be actively
        // misleading if left lying around.
        var file = new StreamWriter(path, append: false) { AutoFlush = true };
        file.WriteLine($"# {Path.GetFileName(path)} -- {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var originalOut = Console.Out;
        var originalError = Console.Error;
        Console.SetOut(new TeeTextWriter(originalOut, file));
        Console.SetError(new TeeTextWriter(originalError, file));

        return new Restorer(originalOut, originalError, file);
    }

    private sealed class Restorer(TextWriter originalOut, TextWriter originalError, StreamWriter file) : IDisposable
    {
        public void Dispose()
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            file.Dispose();
        }
    }

    /// <summary>Forwards every write to both inner writers. Only the overloads this codebase's
    /// commands actually call (<c>Write(string?)</c>/<c>Write(char)</c>/<c>WriteLine(string?)</c>/
    /// <c>WriteLine()</c>) are overridden directly -- <see cref="TextWriter"/>'s base
    /// implementations of every other overload route through these, so nothing is silently
    /// dropped, just possibly one extra virtual call deep.</summary>
    private sealed class TeeTextWriter(TextWriter a, TextWriter b) : TextWriter
    {
        public override Encoding Encoding => a.Encoding;

        public override void Write(char value)
        {
            a.Write(value);
            b.Write(value);
        }

        public override void Write(string? value)
        {
            a.Write(value);
            b.Write(value);
        }

        public override void WriteLine(string? value)
        {
            a.WriteLine(value);
            b.WriteLine(value);
        }

        public override void WriteLine()
        {
            a.WriteLine();
            b.WriteLine();
        }

        public override void Flush()
        {
            a.Flush();
            b.Flush();
        }
    }
}
