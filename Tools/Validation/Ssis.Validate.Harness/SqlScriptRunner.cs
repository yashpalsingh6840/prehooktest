using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Ssis.Validate.Harness;

/// <summary>Runs a `.sql` file containing `GO`-separated batches -- <c>SqlCommand</c> has no notion of `GO` (it's an sqlcmd/SSMS-only convention), so this repo's own DDL scripts (Sql/01_CreateTarget.sql etc.) need it split out before execution.</summary>
public static partial class SqlScriptRunner
{
    public static async Task RunFile(string connectionString, string path)
    {
        var script = await File.ReadAllTextAsync(path);
        var batches = GoSplitter().Split(script).Where(b => !string.IsNullOrWhiteSpace(b));

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        foreach (var batch in batches)
        {
            using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSplitter();
}
