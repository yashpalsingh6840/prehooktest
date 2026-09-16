namespace Ssis.Extract.Model.Shared;

/// <summary>How a column's facet is expressed in an EF Core <c>OnModelCreating</c> block.</summary>
public enum SsisFacetKind
{
    /// <summary>No facet line at all -- the CLR type alone is sufficient (e.g. <c>int</c>).</summary>
    None,

    /// <summary><c>.HasMaxLength(n)</c>, using the column's <c>Length</c>.</summary>
    MaxLength,

    /// <summary><c>.HasColumnType("...")</c>, rendered from <see cref="SsisPipelineType.ColumnTypeTemplate"/>.</summary>
    ColumnType,
}

/// <summary>
/// One pipeline buffer type, resolved into everything a C# emitter needs to declare a
/// property and describe it to EF Core.
/// </summary>
/// <param name="DtName">The canonical DT_* name, for reporting.</param>
/// <param name="ClrTypeName">The C# type keyword/name to emit, e.g. <c>string</c>, <c>DateOnly</c>.</param>
/// <param name="Facet">Which EF facet line (if any) the emitter should write.</param>
/// <param name="ColumnTypeTemplate">
/// For <see cref="SsisFacetKind.ColumnType"/>: a template with <c>{length}</c>, <c>{precision}</c>
/// and <c>{scale}</c> placeholders, e.g. <c>decimal({precision},{scale})</c>. Null otherwise.
/// </param>
/// <param name="Evidenced">
/// True when this exact textual code appears on a real column in this repo's own fixtures.
/// Everything else comes from the published DT_* table and is lower confidence -- the same
/// evidenced/best-effort split <see cref="SsisTypeCodeMaps.PipelineDataTypeName"/> already draws.
/// </param>
public sealed record SsisPipelineType(
    string DtName,
    string ClrTypeName,
    SsisFacetKind Facet,
    string? ColumnTypeTemplate,
    bool Evidenced);

/// <summary>
/// Resolves the <b>textual</b> <c>dataType</c> attribute that pipeline columns carry
/// (<c>"wstr"</c>, <c>"i4"</c>, <c>"numeric"</c>, <c>"dbTimeStamp2"</c>, ...) into a CLR type
/// and an EF Core facet.
///
/// <para><b>Why this is a separate table from everything in <see cref="SsisTypeCodeMaps"/>.</b>
/// That class resolves three distinct <i>numeric</i> type spaces (System.TypeCode ordinals in
/// Project.params, VARIANT codes on variables, DT_* codes on flat-file columns). Pipeline
/// <i>columns</i> use a fourth representation entirely -- a short lowercase string, never a
/// number (see <c>PipelineReader</c>'s column readers). Before this table, nothing in the repo
/// mapped that string to anything: <see cref="SsisTypeCodeMaps.PipelineDataTypeName"/> takes an
/// int and is wired only to <c>FlatFileColumnSpec</c>. A code generator cannot emit a single
/// property declaration without this.</para>
///
/// <para><b>Deliberately NOT folded into <c>Ssis.Runtime.Expressions.SsisType</c>.</b> That enum
/// is pinned by the measured oracle corpus and has no <c>DbTimeStamp2</c>; widening it to suit a
/// code generator would change what the corpus is asserting. The two stay separate and the
/// generator maps between them where it needs to.</para>
///
/// <para><b>Unknown returns null, never a guess</b> -- same rule the rest of this file's
/// neighbours follow. For a generator that distinction is load-bearing: a wrong CLR type
/// compiles cleanly and corrupts data, so an unrecognised type must stop generation of that
/// column and be reported, not quietly become <c>string</c>.</para>
/// </summary>
public static class SsisPipelineTypeMap
{
    // Codes marked evidenced:true were observed on real columns in this repo's own fixtures
    // (out/packages/*.spec.json): wstr, i4, numeric, dbDate, dbTimeStamp, dbTimeStamp2, text.
    // "text" is evidenced only on a Flat File Source's ERROR output column, never on a data
    // path -- kept mapped so an emitter can recognise and skip it rather than fail on it.
    private static readonly Dictionary<string, SsisPipelineType> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // --- strings ---
        ["wstr"] = new("DT_WSTR", "string", SsisFacetKind.MaxLength, null, Evidenced: true),
        ["str"] = new("DT_STR", "string", SsisFacetKind.MaxLength, null, Evidenced: false),
        ["text"] = new("DT_TEXT", "string", SsisFacetKind.ColumnType, "varchar(max)", Evidenced: true),
        ["ntext"] = new("DT_NTEXT", "string", SsisFacetKind.ColumnType, "nvarchar(max)", Evidenced: false),

        // --- integers ---
        ["i1"] = new("DT_I1", "sbyte", SsisFacetKind.None, null, Evidenced: false),
        ["i2"] = new("DT_I2", "short", SsisFacetKind.None, null, Evidenced: false),
        ["i4"] = new("DT_I4", "int", SsisFacetKind.None, null, Evidenced: true),
        ["i8"] = new("DT_I8", "long", SsisFacetKind.None, null, Evidenced: false),
        ["ui1"] = new("DT_UI1", "byte", SsisFacetKind.None, null, Evidenced: false),

        // DT_UI2 (unsigned 2-byte int) -- confirmed real 2026-09 building Phase 5 of the
        // unsupported-component-types plan: an XML Source's own real evidenced "id" column
        // (ETL-SSIS-Real-Scenarios' own UseCase_73, backed by an xs:unsignedShort in the XSD)
        // resolves to this. Mapped to `int`, not `ushort` -- same reasoning as DT_UI8/DT_UI4
        // below: `int` fully represents every DT_UI2 value (0-65535) and avoids the identical
        // "no native EF Core SqlServer mapping for an unsigned integer wider than byte" failure
        // those two already document, so a DT_UI2 column reaching a real destination table stays
        // safe with zero new coercion code.
        ["ui2"] = new("DT_UI2", "int", SsisFacetKind.None, null, Evidenced: true),

        // DT_UI8 (unsigned 8-byte int) is the type Microsoft.Aggregate's own Count function
        // always produces (confirmed real from RBC_Demo_ETL's own AGG_ByRegion, and via the
        // object model: an Aggregate output column's data type resolves to this automatically,
        // never settable directly -- see Ssis.Extract.FixtureBuilder's own BuildAggregateFixture
        // doc comment). Mapped to `long`, not the more literally-accurate `ulong` -- EF Core's
        // SQL Server provider has no native type mapping for any unsigned integer wider than
        // byte, so a `ulong` property would fail model creation; `long`/`bigint` is what a real
        // destination column for this value is in practice anyway (a row count can never
        // realistically approach long's own max value), so this is safe, not just convenient.
        ["ui8"] = new("DT_UI8", "long", SsisFacetKind.None, null, Evidenced: true),

        // DT_UI4 (unsigned 4-byte int) is what Microsoft.Aggregate's own CountDistinct function
        // produces (confirmed real via a live object-model probe, gap-audit Phase 3.3,
        // 2026-09-02 -- narrower than Count/CountAll's own DT_UI8). Mapped to `long`, not `uint`,
        // for the same reason and by the same convention as `ui8` above -- not because
        // AggregateRowEmitter's own row class is EF-mapped (it isn't, confirmed: it's a plain
        // POCO only ever consumed by AggregateRowSource<TSourceRow,TKey,TRow>, never registered
        // with DbContextEmitter), but because this table is SHARED across every emitter, and a
        // `uint` reaching EntityEmitter/SqlRowEmitter for some future real UI4-typed destination
        // column would hit the identical "no native EF Core SqlServer mapping" failure `ui8`'s
        // own comment already documents. Mapping both to the same safe signed type also means a
        // CountDistinct value landing at a typical INT destination column flows through the
        // EXISTING, already-measured NarrowI8ToI4 coercion with zero new code.
        ["ui4"] = new("DT_UI4", "long", SsisFacetKind.None, null, Evidenced: true),

        // --- floating point / exact numeric ---
        ["r4"] = new("DT_R4", "float", SsisFacetKind.None, null, Evidenced: false),
        ["r8"] = new("DT_R8", "double", SsisFacetKind.None, null, Evidenced: false),
        ["numeric"] = new("DT_NUMERIC", "decimal", SsisFacetKind.ColumnType, "decimal({precision},{scale})", Evidenced: true),
        ["decimal"] = new("DT_DECIMAL", "decimal", SsisFacetKind.ColumnType, "decimal({precision},{scale})", Evidenced: false),
        ["cy"] = new("DT_CY", "decimal", SsisFacetKind.ColumnType, "money", Evidenced: false),

        // --- boolean ---
        // DT_BOOL is emitted as bool. Note the expression evaluator casts TRUE to -1 for
        // (DT_I4) -- that is a CAST rule, not a storage rule, and does not apply here.
        ["bool"] = new("DT_BOOL", "bool", SsisFacetKind.None, null, Evidenced: false),

        // --- date / time ---
        // dbDate is a pure calendar date: DateOnly, not DateTime. Getting this wrong is
        // silently lossy rather than a compile error, which is why it is mapped explicitly.
        ["dbdate"] = new("DT_DBDATE", "DateOnly", SsisFacetKind.ColumnType, "date", Evidenced: true),
        ["dbtimestamp"] = new("DT_DBTIMESTAMP", "DateTime", SsisFacetKind.ColumnType, "datetime", Evidenced: true),
        ["dbtimestamp2"] = new("DT_DBTIMESTAMP2", "DateTime", SsisFacetKind.ColumnType, "datetime2({scale})", Evidenced: true),
        ["dbtimestampoffset"] = new("DT_DBTIMESTAMPOFFSET", "DateTimeOffset", SsisFacetKind.ColumnType, "datetimeoffset({scale})", Evidenced: false),
        ["dbtime"] = new("DT_DBTIME", "TimeSpan", SsisFacetKind.ColumnType, "time", Evidenced: false),
        ["dbtime2"] = new("DT_DBTIME2", "TimeOnly", SsisFacetKind.ColumnType, "time({scale})", Evidenced: false),

        // --- other ---
        ["guid"] = new("DT_GUID", "Guid", SsisFacetKind.None, null, Evidenced: false),
        ["bytes"] = new("DT_BYTES", "byte[]", SsisFacetKind.ColumnType, "varbinary({length})", Evidenced: false),
        ["image"] = new("DT_IMAGE", "byte[]", SsisFacetKind.ColumnType, "varbinary(max)", Evidenced: false),
    };

    /// <summary>
    /// Resolves a pipeline column's textual <c>dataType</c>. Returns null for anything not in
    /// the table -- callers must treat that as "cannot generate this column" and report it,
    /// never substitute a default.
    /// </summary>
    public static SsisPipelineType? Resolve(string? pipelineDataType) =>
        pipelineDataType is not null && Map.TryGetValue(pipelineDataType, out var t) ? t : null;

    /// <summary>
    /// Renders <see cref="SsisPipelineType.ColumnTypeTemplate"/> against a column's actual
    /// facets. Returns null when the type needs no <c>HasColumnType</c>, or when the template
    /// requires a facet the column does not carry -- again, a reportable gap rather than a
    /// substituted default (a <c>decimal(0,0)</c> would be worse than no code at all).
    /// </summary>
    public static string? RenderColumnType(SsisPipelineType type, int? length, int? precision, int? scale)
    {
        if (type.Facet != SsisFacetKind.ColumnType || type.ColumnTypeTemplate is null) return null;

        var rendered = type.ColumnTypeTemplate;
        if (rendered.Contains("{length}", StringComparison.Ordinal))
        {
            if (length is null) return null;
            rendered = rendered.Replace("{length}", length.Value.ToString(), StringComparison.Ordinal);
        }
        if (rendered.Contains("{precision}", StringComparison.Ordinal))
        {
            if (precision is null) return null;
            rendered = rendered.Replace("{precision}", precision.Value.ToString(), StringComparison.Ordinal);
        }
        if (rendered.Contains("{scale}", StringComparison.Ordinal))
        {
            if (scale is null) return null;
            rendered = rendered.Replace("{scale}", scale.Value.ToString(), StringComparison.Ordinal);
        }
        return rendered;
    }
}
