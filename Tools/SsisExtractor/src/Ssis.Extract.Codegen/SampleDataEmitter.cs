using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen;

/// <summary>One resolved column of a Tier-A sample file -- the row property name PLUS the exact
/// C# literal a source-read starter test should assert against, computed ONCE here (alongside the
/// representative value actually written to the file) so the two can never quietly disagree --
/// see <see cref="ComponentTestEmitter"/>'s own use of this.</summary>
public sealed record SampleDataColumn(string PropertyName, string ExpectedLiteral);

public sealed record SampleDataResult(GeneratedFile File, string FileSourceKey, string FileName, IReadOnlyList<SampleDataColumn> Columns);

/// <summary>
/// Tier A (Docs/Generated-Tests-Plan.md): a deterministic, synthesized 2-row CSV per Flat File
/// Source, emitted into <c>{Package}.Tests/SampleData/</c>, zero AI, zero fills. This is what
/// makes a starter "Source -- CSV" test green immediately after a fresh `ssisx generate` -- the
/// alternative (Tier B, human/AI-supplied realistic data under `TestData/`) answers a different
/// question and is deliberately NOT a substitute (see the plan's own "two tiers, deliberately"
/// section).
///
/// Reuses the exact same representative values <see cref="TransformTestEmitter"/>/
/// <see cref="ComponentTestEmitter"/> already use for a starter transform/sink test (a plain
/// string is "Sample", a plain int is 7, ...) so a human reading a source test, a transform test,
/// and a sink test for the same package recognizes one convention, not three.
/// </summary>
public static class SampleDataEmitter
{
    public static SampleDataResult? EmitCsv(string fileSourceKey, ConnectionManagerSpec connectionManager)
    {
        var format = connectionManager.FlatFileFormat;
        if (format is null) return null;

        var resolvedColumns = new List<(FlatFileColumnSpec Column, SsisPipelineType Type)>();
        foreach (var column in format.Columns)
        {
            if (SsisPipelineTypeMap.Resolve(CsvRowEmitter.ToPipelineTypeKey(column.DataTypeName)) is { } type)
                resolvedColumns.Add((column, type));
        }
        // A column CsvRowEmitter/ClassMapEmitter themselves skipped (unmapped data type) has no
        // row property to synthesize a value for either -- not a new gap, that column's own gap
        // was already reported by those two emitters.
        if (resolvedColumns.Count == 0) return null;

        var header = string.Join(",", resolvedColumns.Select(rc => rc.Column.ObjectName));
        var rawValues = resolvedColumns
            .Select(rc => RepresentativeCsvValue(rc.Type.ClrTypeName, rc.Column.MaximumWidth))
            .ToList();
        var dataLine = string.Join(",", rawValues);

        // Two identical rows -- enough to prove "reads more than one row" without needing a
        // second, independently-chosen representative value; a starter test asserting the FIRST
        // row's columns (per the taxonomy table) never needs to distinguish them.
        var content = string.Join("\r\n", [header, dataLine, dataLine]) + "\r\n";

        var fileName = $"{fileSourceKey}.csv";
        var columns = resolvedColumns.Zip(rawValues, (rc, raw) =>
            new SampleDataColumn(rc.Column.ObjectName, ToLiteral(rc.Type.ClrTypeName, raw))).ToList();

        return new SampleDataResult(new GeneratedFile($"SampleData/{fileName}", content), fileSourceKey, fileName, columns);
    }

    /// <summary>The plain-text (never C#-literal-quoted) form written to the CSV file itself --
    /// truncated to fit a declared column width, the same real constraint
    /// <c>CsvRowSource{TRow}.EnforceWidths</c> (via <c>[SsisWidth]</c>) applies at READ time, so a
    /// too-long representative value can never make the starter test it backs throw instead of
    /// pass.</summary>
    private static string RepresentativeCsvValue(string clrTypeName, int? maxWidth) => clrTypeName switch
    {
        "string" => Truncate("Sample", maxWidth),
        "short" or "int" or "long" => "7",
        "float" or "double" or "decimal" => "7.5",
        "bool" => "true",
        "DateOnly" => "2020-06-15",
        "DateTime" => "2020-06-15 12:00:00",
        _ => Truncate("Sample", maxWidth),
    };

    private static string Truncate(string value, int? maxWidth) =>
        maxWidth is { } w && value.Length > w ? value[..Math.Max(w, 1)] : value;

    /// <summary>The C# expression a starter test asserts a parsed row's property equals --
    /// parses the SAME raw text through the same invariant-culture convention CsvHelper itself
    /// uses (<c>CsvSourceOptions.Culture</c>), rather than hand-encoding a DateOnly/DateTime value
    /// that could quietly drift from what a real read actually produces.</summary>
    private static string ToLiteral(string clrTypeName, string raw) => clrTypeName switch
    {
        "string" => "\"" + raw.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        "short" => $"(short){raw}",
        "int" => raw,
        "long" => $"{raw}L",
        "float" => $"{raw}f",
        "double" => raw,
        "decimal" => $"{raw}m",
        "bool" => raw,
        "DateOnly" => $"DateOnly.Parse(\"{raw}\", System.Globalization.CultureInfo.InvariantCulture)",
        "DateTime" => $"DateTime.Parse(\"{raw}\", System.Globalization.CultureInfo.InvariantCulture)",
        _ => "\"" + raw + "\"",
    };

    /// <summary>Tier A for a fixed-width/RaggedRight Flat File Source (<see cref="FlatFileRuntimeShape.IsFixedWidthWithoutHeader"/>)
    /// -- the "Source -- CSV / fixed-width" taxonomy row's OTHER half. Every fixed-width column is
    /// ALWAYS a plain string, read by character position with NO trimming and NO width enforcement
    /// (see <c>FixedWidthRowReaderEmitter</c>'s own doc comment, confirmed against a real dtexec
    /// run) -- so the expected literal for a declared-width column is the PADDED value, not a
    /// trimmed one, and the one column with no declared width (the ragged trailing column, "the
    /// rest of the line") gets no padding at all. <see cref="FlatFileFormatSpec.HeaderRowsToSkip"/>
    /// decorative lines (a control record, never column names here) are written first, unparsed
    /// content, matching <see cref="FixedWidthSourceOptions.SkipRows"/>'s own real behavior.</summary>
    public static SampleDataResult? EmitFixedWidth(string fileSourceKey, ConnectionManagerSpec connectionManager)
    {
        var format = connectionManager.FlatFileFormat;
        if (format is null) return null;

        // Every column (string or not) occupies real physical space in the line and must be
        // written/padded to keep every LATER column's own offset correct -- unlike EmitCsv, a
        // non-string column here cannot simply be skipped when building the raw line itself, only
        // excluded from the starter test's own assertions (matching FixedWidthRowReaderEmitter's
        // own gate, applied to sample-data synthesis rather than duplicated as a second report).
        var plans = FlatFileRuntimeShape.BuildColumnPlans(format);
        string RepresentativeFixedWidthValue(int? width) =>
            width is { } w ? Truncate("Sample", w).PadRight(w) : "Sample";
        var rawValues = plans.Select(p => RepresentativeFixedWidthValue(p.FixedWidth)).ToList();

        var columns = new List<SampleDataColumn>();
        for (var i = 0; i < format.Columns.Count; i++)
        {
            var column = format.Columns[i];
            if (SsisPipelineTypeMap.Resolve(CsvRowEmitter.ToPipelineTypeKey(column.DataTypeName)) is { ClrTypeName: "string" })
                columns.Add(new SampleDataColumn(column.ObjectName, ToLiteral("string", rawValues[i])));
        }
        if (columns.Count == 0) return null;

        var dataLine = string.Concat(rawValues);
        var skipRows = format.HeaderRowsToSkip ?? 0;

        var contentLines = new List<string>();
        for (var i = 0; i < skipRows; i++) contentLines.Add("SKIP");
        contentLines.Add(dataLine);
        contentLines.Add(dataLine); // two identical rows -- same "prove more than one row" reasoning as EmitCsv
        var content = string.Join("\r\n", contentLines) + "\r\n";

        var fileName = $"{fileSourceKey}.txt";
        return new SampleDataResult(new GeneratedFile($"SampleData/{fileName}", content), fileSourceKey, fileName, columns);
    }
}
