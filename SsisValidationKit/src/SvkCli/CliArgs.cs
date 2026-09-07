namespace Svk.Cli;

internal static class CliArgs
{
    public static string Require(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"missing value for '{args[i]}'");
        return args[++i];
    }

    public static IEnumerable<string> SplitPackages(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
