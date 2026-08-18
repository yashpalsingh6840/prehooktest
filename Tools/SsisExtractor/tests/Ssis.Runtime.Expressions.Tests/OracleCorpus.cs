using System.Text;

namespace Ssis.Runtime.Expressions.Tests;

/// <summary>One row of <c>Fixtures/ssis-expression-oracle.tsv</c>. See <see cref="OracleCorpusTests"/> for how it's used, and <c>src/Ssis.Expression.Oracle/Program.cs</c> for how it was produced.</summary>
public sealed class OracleRow
{
    public required string Expression { get; init; }
    public required string Outcome { get; init; }
    public required string Value { get; init; }

    // xUnit's [MemberData] renders each theory case's display name from ToString() by
    // default for non-primitive parameters -- without this override every row shows up as
    // "Ssis.Runtime.Expressions.Tests.OracleRow" in test output, indistinguishable from each
    // other when one fails.
    public override string ToString() => $"{Expression} => {Outcome} {Value}";
}

public static class OracleCorpus
{
    public static List<OracleRow> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ssis-expression-oracle.tsv");
        var rows = new List<OracleRow>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split('\t');
            if (parts.Length != 3) throw new InvalidOperationException($"malformed oracle corpus line: {line}");
            rows.Add(new OracleRow { Expression = Unescape(parts[0]), Outcome = parts[1], Value = Unescape(parts[2]) });
        }
        return rows;
    }

    /// <summary>Reverses <c>Ssis.Expression.Oracle.Program.Escape</c>.</summary>
    private static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i] switch
                {
                    '\\' => '\\',
                    't' => '\t',
                    'r' => '\r',
                    'n' => '\n',
                    var other => other,
                });
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
