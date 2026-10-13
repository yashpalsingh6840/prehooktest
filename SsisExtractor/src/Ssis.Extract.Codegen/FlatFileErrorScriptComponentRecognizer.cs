using System.Text.RegularExpressions;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Promotes ONE narrow, measured Script Component shape from a human-reviewed Tier-2 seam into a
/// fully deterministic translation: a Script Component downstream of a Flat File Source's error
/// output whose entire body is exactly
/// <c>Row.ErrorDescription = ComponentMetaData.GetErrorDescription(Row.ErrorCode); Row.FileName =
/// "&lt;literal&gt;";</c> -- real, repeating client evidence (the same two-statement script, word
/// for word, appeared on all three Data Flow Tasks of a real client package; see CLAUDE.md's own
/// "Flat File Source error output" round). This is safe to automate, not a guess, because:
///
/// 1. <c>ComponentMetaData.GetErrorDescription(Row.ErrorCode)</c> has an exactly TWO-value domain
///    in GENERATED code -- confirmed via a real dtexec probe -- because the only thing that ever
///    produces <c>row.ErrorCode</c> is this generator's own <c>Etl.Core.Csv.CsvRowSource&lt;TRow&gt;</c>,
///    which hardcodes exactly two outcomes (<see cref="ConversionFailureErrorCode"/>,
///    <see cref="TruncationFailureErrorCode"/>) and nothing else. Reproducing the real measured
///    text for exactly these two codes is a faithful port, not an invented mapping.
/// 2. The <c>FileName</c> literal is read verbatim out of the real script source -- never
///    invented.
///
/// Deliberately narrow and defensive: ANY deviation from this exact shape (extra statements, a
/// computed FileName, a different call, more than these two produced columns) returns null and
/// the column falls back to today's ordinary human-reviewed Tier-2 seam, unchanged. This is the
/// same "promote a recurring Tier-2 pattern into the deterministic emitter once it's measured and
/// evidenced" move this project's own history already describes for several other components --
/// never applied speculatively, only once real evidence (here, a real client package) showed the
/// SAME script repeating verbatim across multiple flows.
/// </summary>
internal static class FlatFileErrorScriptComponentRecognizer
{
    /// <summary>Measured via a real dtexec run -- mirrors Etl.Core.Csv.CsvRowSource's own
    /// private constant of the identical name/value; kept in sync by hand (same risk class as
    /// this project's own documented Etl.Core/generator-emission duplication elsewhere).</summary>
    private const int ConversionFailureErrorCode = -1071607676;

    /// <summary>Measured via a real dtexec run -- see <see cref="ConversionFailureErrorCode"/>.</summary>
    private const int TruncationFailureErrorCode = -1071607675;

    private static readonly Regex ProcessInputRowMethod = new(
        @"ProcessInputRow\s*\([^)]*\)\s*\{(?<body>.*?)\}",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ErrorDescriptionStatement = new(
        @"^Row\s*\.\s*ErrorDescription\s*=\s*ComponentMetaData\s*\.\s*GetErrorDescription\s*\(\s*Row\s*\.\s*ErrorCode\s*\)$",
        RegexOptions.Compiled);

    private static readonly Regex FileNameStatement = new(
        @"^Row\s*\.\s*FileName\s*=\s*""(?<literal>(?:[^""\\]|\\.)*)""$",
        RegexOptions.Compiled);

    /// <summary>Tries to recognize the exact shape described in this class's own doc comment on
    /// <paramref name="component"/>. Returns a map of produced-column-name to the C# VALUE
    /// expression it resolves to (ready to splice straight into an assignment), or null if the
    /// component's source doesn't match this one exact shape -- including "no source at all"
    /// (stripped) and "produces columns other than exactly ErrorDescription/FileName".</summary>
    public static IReadOnlyDictionary<string, string>? TryRecognize(PipelineComponentSpec component)
    {
        var script = component.ScriptComponent;
        if (script is null || script.SourceStripped) return null;

        // Scoped to the one evidenced column set -- a component producing anything else (even
        // ErrorDescription/FileName plus a third column) is not this shape; fall back to the
        // seam rather than guess at a partial match.
        var producedNames = component.Outputs
            .Where(o => o.IsErrorOut != true)
            .SelectMany(o => o.Columns)
            .Select(c => c.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (producedNames.Count != 2 || !producedNames.Contains("ErrorDescription") || !producedNames.Contains("FileName"))
            return null;

        // SourceFiles (parsed name/encoding/content triples) is preferred when available -- see
        // ScriptComponentPayload's own doc comment on why the raw SourceCodeItems array needs
        // this alignment check before it's safe to treat as real files at all.
        var candidates = script.SourceFiles.Count > 0
            ? script.SourceFiles.Where(f => f.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Select(f => f.Content)
            : [];

        foreach (var content in candidates)
        {
            var mapping = TryMatchBody(content);
            if (mapping is not null) return mapping;
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string>? TryMatchBody(string content)
    {
        var match = ProcessInputRowMethod.Match(content);
        if (!match.Success) return null;

        var statements = match.Groups["body"].Value
            .Split(';')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        // Exactly these two statements, nothing else -- any additional logic means this is not
        // the narrow, fully-measured shape this recognizer exists for.
        if (statements.Count != 2) return null;

        if (!statements.Any(s => ErrorDescriptionStatement.IsMatch(s))) return null;
        var fileNameMatch = statements.Select(s => FileNameStatement.Match(s)).FirstOrDefault(m => m.Success);
        if (fileNameMatch is null) return null;

        var literal = fileNameMatch.Groups["literal"].Value;
        return new Dictionary<string, string>
        {
            ["ErrorDescription"] =
                "row.ErrorCode switch\n" +
                "            {\n" +
                $"                {ConversionFailureErrorCode} => \"The data value cannot be converted for reasons other than sign mismatch or data overflow.\",\n" +
                $"                {TruncationFailureErrorCode} => \"The data was truncated.\",\n" +
                "                _ => throw new InvalidOperationException($\"Unsupported SSIS flat-file error code: {row.ErrorCode}\"),\n" +
                "            }",
            ["FileName"] = $"\"{literal}\"",
        };
    }
}
