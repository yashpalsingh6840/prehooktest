using System.Text;
using System.Text.RegularExpressions;

namespace Svk.Core;

public static class Naming
{
    private static readonly char[] InvalidFileChars = Path.GetInvalidFileNameChars();

    public static string SafeFileName(string name, int maxLength = 60)
    {
        var sb = new StringBuilder();
        foreach (var ch in name)
        {
            sb.Append(InvalidFileChars.Contains(ch) ? '_' : ch);
        }
        var result = sb.ToString();
        return result.Length > maxLength ? result[..maxLength] : result;
    }

    /// <summary>Normalizes a table reference from any observed quoting convention (bracketed
    /// "[dbo].[Employee]", double-quoted "dbo"."Table" -- the ADO NET destination/source
    /// convention, see AdoNetDestinationPayload's own doc comment -- or bare) into one
    /// consistent bracketed form for DDL/INSERT rendering.</summary>
    public static string NormalizeTableName(string raw)
    {
        var parts = raw.Split('.')
            .Select(p => p.Trim().Trim('[', ']', '"'))
            .Where(p => p.Length > 0)
            .ToList();
        return parts.Count == 0 ? "[dbo].[UnnamedTable]" : string.Join(".", parts.Select(p => $"[{p}]"));
    }

    public static string SanitizeIdentifier(string raw)
    {
        var cleaned = Regex.Replace(raw, @"[^A-Za-z0-9_]", "_");
        return cleaned.Length == 0 || char.IsDigit(cleaned[0]) ? "T_" + cleaned : cleaned;
    }

    /// <summary>Best-effort single-table extraction from a Lookup's own reference query -- a
    /// plain regex, not a real SQL parser (deliberately avoiding a ScriptDom dependency for
    /// this generator, unlike Ssis.Extract.Sql). Returns null on anything beyond one FROM/JOIN,
    /// rather than guessing which one is "the" table.</summary>
    public static string? TryExtractSingleFromTable(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return null;
        var matches = Regex.Matches(sql, @"\b(?:FROM|JOIN)\s+([\[\]""\w\.]+)", RegexOptions.IgnoreCase);
        return matches.Count == 1 ? matches[0].Groups[1].Value : null;
    }
}
