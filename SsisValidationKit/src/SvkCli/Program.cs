using Ssis.Extract.Model.Diagnostics;

namespace Svk.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 2;
        }

        var command = args[0];
        var rest = args[1..];

        try
        {
            return Dispatch(command, rest);
        }
        catch (Exception ex)
        {
            // Same reasoning as ssisx's own top-level catch (Ssis.Extract.Cli/Program.cs) --
            // reached only by a real svk bug, at a client site where nothing else can leave the
            // machine. Write a compact, client-data-free crash report instead of nothing.
            var scrubbed = DiagnosticReport.ScrubArgs("svk", command, rest);
            var path = DiagnosticReport.Capture("svk", scrubbed, "top-level (uncaught)", ex,
                outDir: DiagnosticReport.TryFindOutDir(rest));
            Console.Error.WriteLine("error: svk hit an unexpected internal error and stopped.");
            Console.Error.WriteLine($"A diagnostic file with no client data was written to: {path}");
            Console.Error.WriteLine("Please share that file (e.g. a screenshot of it) so this can be fixed.");
            return 99;
        }
    }

    private static int Dispatch(string command, string[] rest) => command switch
        {
            "sampledata" => SampleDataCommand.Run(rest),
            "walkthrough" => WalkthroughCommand.Run(rest),
            "--help" or "-h" or "help" => Help(),
            _ => Unknown(command),
        };

    private static int Help()
    {
        PrintHelp();
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"error: unknown command '{command}'. Run 'svk --help'.");
        return 2;
    }

    private static void PrintHelp() => Console.WriteLine("""
        svk -- validation tooling for the SSIS-to-C# rewrite.

        Reads packages/<Package>.spec.json produced by `ssisx extract` (Tools/SsisExtractor);
        never edits Tools/SsisExtractor itself.

        Commands:
          sampledata   Generate deterministic synthetic test data (CSV/SQL) and DDL per package.
          walkthrough  Generate a per-package execution-order review document with a
                       human-owned validation-status claims file.

        Run 'svk <command> --help' for command-specific options.

        Exit code 98: one or more packages hit a genuine, unexpected internal svk error and were
        skipped (every other package still ran normally); 99: an unexpected error crashed the
        whole run. Both write a compact svk-diagnostic-<timestamp>.txt containing no package/
        column/file names or other client data -- only the exception type, a stack trace
        filtered to this tool's own source, and which package ordinal (never its name) was being
        processed. If this happens at a client site (where nothing else can leave the machine),
        that file is what to screenshot and share back so the tool itself can be fixed.
        """);
}
