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
        return command switch
        {
            "sampledata" => SampleDataCommand.Run(rest),
            "walkthrough" => WalkthroughCommand.Run(rest),
            "--help" or "-h" or "help" => Help(),
            _ => Unknown(command),
        };
    }

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
        """);
}
