namespace Ssis.Extract.Model.Pipeline;

/// <summary>One design-time column ↔ external (file/table) column mapping, derived from an input/output column's <c>externalMetadataColumnId</c> resolved against the same input/output's own <c>externalMetadataColumns</c> list -- not a separate mapping section in the source XML.</summary>
public sealed class PipelineColumnMappingSpec
{
    public required string ComponentColumnName { get; init; }
    public required string ExternalColumnName { get; init; }
    public string? ExternalDataType { get; init; }
}

/// <summary>
/// <c>Microsoft.FlatFileSource</c> (plan §4.7 table: "connection + column mapping,
/// Overwrite, header"). <see cref="ColumnMappings"/> is built from the main (non-error)
/// output's columns against their <c>externalMetadataColumns</c> -- the design-time
/// contract with the flat file's own schema.
/// </summary>
public sealed class FlatFileSourcePayload
{
    public string? ConnectionName { get; init; }

    /// <summary>Whether zero-length columns are treated as null.</summary>
    public bool? RetainNulls { get; init; }

    /// <summary>Name of an output column carrying the source file name, if configured. Empty/null means no such column is generated.</summary>
    public string? FileNameColumnName { get; init; }

    public List<PipelineColumnMappingSpec> ColumnMappings { get; init; } = [];
}

/// <summary>
/// <c>Microsoft.ADONETDestination</c>. Unlike <see cref="OleDbDestinationPayload"/>, this
/// component's own <c>componentClassID</c> is the generic <c>Microsoft.ManagedComponentHost</c>
/// -- <c>UserComponentTypeName</c> is what actually disambiguates it, the same discrimination
/// <see cref="ScriptComponentPayload"/>'s own doc comment already documents for Script
/// Component. Confirmed real via RBC_Demo_ETL's own Package_Exports.dtsx (ADO_DST_ExportLog),
/// discovered testing this tool against a real third-party portfolio (SSIS_From_Sandeep,
/// 2026-08-27). <see cref="TableOrViewName"/> is double-quoted (<c>"dbo"."Table"</c>), NOT
/// bracketed like <see cref="OleDbDestinationPayload.OpenRowset"/> (<c>[dbo].[Table]</c>) --
/// a genuinely different quoting convention between the two UI components, not a typo.
/// </summary>
public sealed class AdoNetDestinationPayload
{
    public string? ConnectionName { get; init; }

    /// <summary>The target table/view, double-quoted, e.g. <c>"dbo"."CustomerExportLog"</c>.</summary>
    public string? TableOrViewName { get; init; }

    public int? BatchSize { get; init; }
    public int? CommandTimeout { get; init; }

    /// <summary>SSIS's own "use SqlBulkCopy when the provider supports it" toggle -- observed
    /// true on the one real evidenced instance, which is why this tool treats an ADO NET
    /// Destination with this set as fast-load-configured, the same gate an OLE DB Destination's
    /// FastLoadMaxInsertCommitSize/FastLoadOptions already are.</summary>
    public bool? UseBulkInsertWhenPossible { get; init; }

    public List<PipelineColumnMappingSpec> ColumnMappings { get; init; } = [];
}

/// <summary>
/// <c>Microsoft.DataReaderSourceAdapter</c> (the ADO NET Source's own UserComponentTypeName --
/// confirmed real, NOT "Microsoft.ADONETSource" as the destination's naming might suggest).
/// Same discrimination shape as <see cref="AdoNetDestinationPayload"/>. <see cref="AccessMode"/>
/// is kept raw, same "don't hand-guess an enum" rule as <see cref="OleDbSourcePayload.AccessMode"/>
/// -- and deliberately UNUSED by this tool's own codegen, since the one real evidenced instance
/// (AccessMode=1) doesn't obviously match either of OLE DB Source's own evidenced values (0/2);
/// codegen instead branches on which of <see cref="SqlCommand"/>/<see cref="TableOrViewName"/>
/// is actually populated, sidestepping the need to know this enum's real mapping at all.
/// </summary>
public sealed class AdoNetSourcePayload
{
    public string? ConnectionName { get; init; }

    /// <summary>The source table/view, double-quoted -- same convention as
    /// <see cref="AdoNetDestinationPayload.TableOrViewName"/>. Empty on the one real evidenced
    /// instance (which uses <see cref="SqlCommand"/> instead).</summary>
    public string? TableOrViewName { get; init; }

    public string? SqlCommand { get; init; }
    public int? AccessMode { get; init; }
    public int? CommandTimeout { get; init; }

    public List<PipelineColumnMappingSpec> ColumnMappings { get; init; } = [];
}

/// <summary>
/// <c>Microsoft.FlatFileDestination</c>, the write-side mirror of <see cref="FlatFileSourcePayload"/>.
/// Confirmed real via RBC_Demo_ETL's own Package_Exports.dtsx (<c>DFT_ExportDelimited_DST</c> /
/// <c>DFT_ExportFixedWidth_DST</c>), discovered testing this tool against a real third-party
/// portfolio (SSIS_From_Sandeep, 2026-08-27). Unlike <see cref="OleDbDestinationPayload"/>,
/// there is no <c>usesDispositions</c>/AccessMode-style attribute at all -- the component's
/// own three properties (<see cref="Overwrite"/>/<see cref="Header"/>/<see cref="EscapeQualifier"/>)
/// are the entire bespoke surface; everything about the FILE'S OWN layout (delimited vs
/// fixed-width, delimiters, column widths) lives on the FLAT FILE CONNECTION MANAGER instead
/// (<see cref="Ssis.Extract.Model.Shared.FlatFileFormatSpec"/>, already modeled generically for
/// the source side) -- resolve via <see cref="ConnectionName"/>, don't duplicate it here.
///
/// A genuinely surprising real-evidenced quirk, worth recording rather than re-deriving: SSIS's
/// own Flat File Destination wizard, in FixedWidth mode, adds an extra trailing pipeline column
/// (<c>RowEnd</c> in the real package) with NO fixed width of its own -- its connection-manager
/// column entry is <c>ColumnType="Delimited"</c> with the row delimiter as its own
/// <c>ColumnDelimiter</c> -- purely to carry the CRLF row terminator. The real package's own
/// upstream SQL literally selects <c>'' AS RowEnd</c> to feed it. This needs NO special
/// generator handling: it's an ordinary mapped column like any other, and the row-terminator
/// behavior falls out of walking <c>FlatFileFormatSpec.Columns</c> in order at write time.
/// </summary>
public sealed class FlatFileDestinationPayload
{
    public string? ConnectionName { get; init; }

    /// <summary>Whether each run truncates and rewrites the destination file (true) or appends to it (false).</summary>
    public bool? Overwrite { get; init; }

    /// <summary>Literal text written before any data row, or empty/null for none. Distinct from <see cref="Ssis.Extract.Model.Shared.FlatFileFormatSpec.ColumnNamesInFirstDataRow"/> -- observed empty on both real instances (they use the connection manager's own header-row-of-column-names behavior instead).</summary>
    public string? Header { get; init; }

    public bool? EscapeQualifier { get; init; }

    /// <summary>Input columns ↔ external (file) columns, built from the main input's columns against their <c>externalMetadataColumns</c> -- same shape as <see cref="OleDbDestinationPayload.ColumnMappings"/>.</summary>
    public List<PipelineColumnMappingSpec> ColumnMappings { get; init; } = [];
}

/// <summary>
/// <c>Microsoft.OLEDBDestination</c> (plan §4.7 table). <see cref="AccessMode"/> is kept as
/// the raw integer only -- the XML tags it <c>typeConverter="AccessMode"</c> but doesn't
/// spell out the enum's text values in this file, and CLAUDE.md trap 12's lesson is to not
/// hand-guess an enum without runtime/documentation evidence; a future slice can resolve it
/// via the object-model oracle (plan §6, slice 6) if needed.
/// </summary>
public sealed class OleDbDestinationPayload
{
    public string? ConnectionName { get; init; }

    /// <summary>The target table/view, e.g. "[dbo].[Employee]". Null/empty when <see cref="SqlCommand"/> is used instead (AccessMode = SQL command).</summary>
    public string? OpenRowset { get; init; }

    public string? SqlCommand { get; init; }
    public int? AccessMode { get; init; }
    public string? FastLoadOptions { get; init; }
    public bool? FastLoadKeepIdentity { get; init; }
    public bool? FastLoadKeepNulls { get; init; }
    public int? FastLoadMaxInsertCommitSize { get; init; }
    public int? CommandTimeout { get; init; }

    /// <summary>Input columns ↔ external (target table) columns, built from the main input's columns against their <c>externalMetadataColumns</c>.</summary>
    public List<PipelineColumnMappingSpec> ColumnMappings { get; init; } = [];
}

/// <summary>
/// <c>Microsoft.OLEDBSource</c> -- the read-side mirror of <see cref="OleDbDestinationPayload"/>,
/// added during the synthetic component-coverage pass (docs/report-schema.md) alongside
/// Lookup/Conditional Split since it's the natural upstream component for both and this PoC
/// had no prior OLE DB Source evidence (both real PoC packages read from flat files only).
/// Same <see cref="AccessMode"/> raw-integer caveat as the destination payload -- confirmed
/// value <c>2</c> = SQL command against the synthetic fixture, not otherwise decoded.
/// </summary>
public sealed class OleDbSourcePayload
{
    public string? ConnectionName { get; init; }

    /// <summary>The source table/view. Null/empty when <see cref="SqlCommand"/> is used instead.</summary>
    public string? OpenRowset { get; init; }

    public string? SqlCommand { get; init; }
    public int? AccessMode { get; init; }
    public int? CommandTimeout { get; init; }

    /// <summary>Output columns ↔ external (source table) columns, built from the main (non-error) output's columns against their <c>externalMetadataColumns</c> -- same construction as <see cref="FlatFileSourcePayload.ColumnMappings"/>.</summary>
    public List<PipelineColumnMappingSpec> ColumnMappings { get; init; } = [];
}

/// <summary>
/// <c>Microsoft.ExcelSource</c> -- added 2026-08-28 testing this tool against a real
/// third-party portfolio (SSIS_From_Sandeep, <c>Package_Advanced.dtsx</c>'s own
/// <c>EXCEL_SRC_Drip</c>). Structurally identical to <see cref="OleDbSourcePayload"/>
/// (same property names -- <c>OpenRowset</c>/<c>SqlCommand</c>/<c>AccessMode</c>/
/// <c>CommandTimeout</c> -- confirmed by reading the real component's own saved XML,
/// which even reuses the connection's own name "OleDbConnection"), kept as its own type
/// rather than reusing <see cref="OleDbSourcePayload"/> because the two are read via a
/// different, Excel-specific ADO OLE DB provider (<c>Microsoft.ACE.OLEDB.12.0</c>) with
/// its own real-world quirks (a worksheet is addressed as <c>OpenRowset="Sheet1$"</c> --
/// the trailing <c>$</c> is the ADO/Jet convention for "this is a worksheet, not a named
/// range" -- and every column comes back typed only as <c>r8</c>/<c>wstr</c>, since Excel
/// itself has no real column-type concept). The one evidenced <see cref="AccessMode"/>
/// value is <c>0</c> (OpenRowset) -- <c>SqlCommand</c>-mode Excel Source is unevidenced
/// and, unlike OLE DB Source's own SqlCommand mode, has no obvious SQL dialect to assume
/// (the Jet/ACE "SQL" over a worksheet is its own, non-standard dialect) so is a named,
/// not guessed, gap if ever seen.
/// </summary>
public sealed class ExcelSourcePayload
{
    public string? ConnectionName { get; init; }

    /// <summary>The worksheet, e.g. <c>"Sheet1$"</c> -- the trailing <c>$</c> is part of the real value, not stripped here (stripping happens in codegen, where the runtime API's own worksheet-naming convention is what decides whether it's needed).</summary>
    public string? OpenRowset { get; init; }

    public string? SqlCommand { get; init; }
    public int? AccessMode { get; init; }
    public int? CommandTimeout { get; init; }

    /// <summary>Output columns ↔ external (worksheet) columns, built from the main (non-error) output's columns against their <c>externalMetadataColumns</c> -- same construction as <see cref="OleDbSourcePayload.ColumnMappings"/>.</summary>
    public List<PipelineColumnMappingSpec> ColumnMappings { get; init; } = [];
}

/// <summary>
/// <c>Microsoft.Lookup</c> (plan §5.7 names it explicitly as a common gap component).
/// Confirmed by building a real Lookup via the SSIS 17 object model against a live
/// SqlServer reference table (docs/report-schema.md "Synthetic component-coverage
/// fixtures") -- <see cref="MatchOutputName"/>/<see cref="NoMatchOutputName"/> are SSIS's
/// own fixed output names for this component (not user-renamed the way a Conditional
/// Split case is), confirmed against that fixture's own saved XML.
/// <see cref="ReferenceColumns"/> is a best-effort parse of the component's own
/// <c>ReferenceMetadataXml</c> custom property -- an internal, undocumented nested-XML-
/// in-a-string format (not the standard <c>externalMetadataColumns</c> mechanism
/// OLEDBDestination/OLEDBSource use, confirmed empirically: this collection stayed empty
/// on the synthetic fixture even after the join key was set and the component fully
/// reinitialized). Parsed defensively -- null on any shape this format doesn't match,
/// rather than a hard failure, since nothing here documents that this internal shape is
/// stable across SSIS versions.
/// </summary>
public sealed class LookupPayload
{
    public string? ConnectionName { get; init; }
    public string? SqlCommand { get; init; }

    /// <summary>Raw <c>NoMatchBehavior</c> integer -- 1 = redirect no-match rows to <see cref="NoMatchOutputName"/> on the synthetic fixture; not otherwise decoded (same raw-enum caveat as OleDbDestinationPayload.AccessMode).</summary>
    public int? NoMatchBehaviorRaw { get; init; }

    /// <summary>Raw <c>CacheType</c> integer (0 = Full cache on the synthetic fixture's default).</summary>
    public int? CacheTypeRaw { get; init; }

    public string? MatchOutputName { get; init; }
    public string? NoMatchOutputName { get; init; }

    public List<LookupReferenceColumnSpec> ReferenceColumns { get; init; } = [];
}

/// <summary>One <c>&lt;referenceColumn&gt;</c> parsed out of a Lookup's <c>ReferenceMetadataXml</c> custom property -- see <see cref="LookupPayload.ReferenceColumns"/>.</summary>
public sealed class LookupReferenceColumnSpec
{
    public required string Name { get; init; }
    public string? DataType { get; init; }
    public int? Length { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
}

/// <summary>
/// <c>Microsoft.ConditionalSplit</c>. Built from each non-error/non-default output's own
/// <see cref="PipelineOutputSpec.Properties"/> (see that field's doc comment for the
/// coverage gap discovering this closed) -- confirmed against a real Conditional Split
/// built via the object model, one case output plus the always-present default and error
/// outputs. <see cref="Cases"/> is ordered by <see cref="ConditionalSplitCaseSpec.EvaluationOrder"/>
/// where present, matching the order SSIS itself evaluates them in at runtime (first match
/// wins) -- not just document order.
/// </summary>
public sealed class ConditionalSplitPayload
{
    public string? DefaultOutputName { get; init; }
    public List<ConditionalSplitCaseSpec> Cases { get; init; } = [];
}

/// <summary>One non-default, non-error output of a Conditional Split -- one evaluated case.</summary>
public sealed class ConditionalSplitCaseSpec
{
    public required string OutputName { get; init; }

    /// <summary>Raw form, with <c>#{lineageId}</c> references -- same two-form pattern as <see cref="PipelineOutputColumnSpec.Expression"/>/<see cref="PipelineOutputColumnSpec.FriendlyExpression"/>, just promoted from the output level instead of a column.</summary>
    public string? Expression { get; init; }
    public string? FriendlyExpression { get; init; }
    public int? EvaluationOrder { get; init; }
}

/// <summary>
/// A Script Component -- the Data Flow pipeline transform sibling of Script Task, plan
/// §4.5's "same source extraction as Script Task, plus input/output column usage" (the
/// latter is already covered generically: <see cref="PipelineComponentSpec.Inputs"/>/
/// <see cref="PipelineComponentSpec.Outputs"/> and their columns are read the same way for
/// every component). Confirmed real via the object model, not the same discriminator as
/// every other bespoke payload in this file: the component's own <c>ComponentClassId</c>
/// is the generic <c>"Microsoft.ManagedComponentHost"</c> (the same host class any managed
/// pipeline component could in principle use), never a Script-Component-specific ID --
/// <see cref="PipelineComponentSpec"/>'s <c>UserComponentTypeName</c> custom property
/// (value <c>"Microsoft.ScriptComponentHost"</c>) is what actually disambiguates it, so
/// <c>PipelineReader.ReadComponent</c> checks that property's value, not
/// <c>ComponentClassId</c>, before building this payload.
///
/// <see cref="ReadOnlyVariables"/>/<see cref="ReadWriteVariables"/> are comma-split, NOT
/// semicolon-split like <c>ScriptTaskPayload</c>'s -- confirmed genuinely different by
/// reading the property's own self-declared description text from the object model
/// ("Specifies a comma-separated list of read-only variables."), not assumed to match
/// Script Task just because the two features are conceptually similar.
///
/// <see cref="SourceCodeItems"/> is confirmed real evidence of structure, not content: the
/// <c>SourceCode</c> custom property is <c>isArray="true"</c>, and setting an array value
/// through the object model's own property system (bypassing the VSTA editor entirely,
/// which -- same as Script Task's synthetic fixture -- never writes a real project when
/// driven by bare property calls) proved the exact persisted shape is
/// <c>&lt;arrayElements&gt;&lt;arrayElement&gt;text&lt;/arrayElement&gt;...&lt;/arrayElements&gt;</c>,
/// each element carrying no name/index attribute of its own. What is NOT confirmed: the
/// real per-element file identity a genuine SSDT-authored Script Component would produce
/// (unlike Script Task, no real SSDT-authored Script Component was available to read for
/// ground truth here) -- so elements are kept in document order only, never assumed to be
/// "one element = ScriptMain.cs" the way Script Task's named <c>ProjectItem</c> list is.
/// <see cref="HasBinaryCode"/> mirrors <c>ScriptTaskPayload.BinaryItemNames</c>'s
/// "name only, never content" rule, except <c>BinaryCode</c>'s array elements carry no
/// name at all to even capture -- just a presence flag.
/// </summary>
/// <summary>
/// <c>Microsoft.DataConvert</c> -- discovered testing this tool against a real third-party
/// portfolio (SSIS_From_Sandeep, 2026-08-27/28: <c>Package_Transforms.dtsx</c>'s
/// <c>DFT_DerivedAndSplit.DCONV_Types</c>, converting <c>CustomerID</c>/<c>SignupDate</c>
/// (both raw strings) to <c>CustomerID_i4</c> (DT_I4)/<c>SignupDate_dt</c> (DT_DBDATE), both
/// dispositions IgnoreFailure). Unlike Lookup/Conditional Split, this component needed no new
/// generic model fields at all -- every converted column's type/length/precision/scale/
/// dispositions are already captured by the fully-generic <see cref="PipelineOutputColumnSpec"/>
/// every other component's output columns already go through. The only genuinely bespoke piece
/// is <see cref="DataConversionColumnSpec.SourceColumnLineageId"/>: unlike Derived Column (whose
/// output column carries an <c>Expression</c> property with embedded <c>#{lineageId}</c>
/// references), a Data Conversion output column instead carries a single-purpose
/// <c>SourceInputColumnLineageID</c> custom property whose entire value is one
/// <c>#{lineageId}</c> reference to the upstream column being converted -- confirmed via a
/// live object-model round-trip (<c>SyntheticDataConversion.dtsx</c>), not guessed from
/// Microsoft's own (evidently outdated) Data Conversion Transformation SDK sample, which shows
/// a <c>MapOutputColumn</c>-based API this GAC's real <c>IDTSDesigntimeComponent100</c> doesn't
/// use for this purpose at all (see the fixture builder's own doc comment for the two
/// mechanism-level findings). <c>Ssis.Extract.Dtsx.LineageBuilder</c> treats this the same way
/// it already treats an <c>Expression</c>'s embedded refs -- see that type's own doc comment.
///
/// <para><b>Empirically observed IgnoreFailure behaviour</b> (real dtexec run against
/// <c>.\SQLFORPOC_2022</c>, seeded with a valid value, a valid-but-whitespace-padded value, a
/// non-numeric/non-date string, an empty string, and a NULL): the row is <b>never dropped</b> --
/// every seeded row landed at the destination -- and a failing conversion (or a NULL/empty
/// source) simply yields <c>NULL</c> in that one output column, independent of every other
/// column on the same row. Whitespace padding parses correctly (locale-neutral trim). This is
/// what <c>ssisx generate</c>'s own <c>SsisFn</c> conversion helper is built to reproduce --
/// see <c>Etl.Core</c>'s own doc comment for the generated code.</para>
/// </summary>
public sealed class DataConvertPayload
{
    public List<DataConversionColumnSpec> Columns { get; init; } = [];
}

/// <summary>One converted output column of a Data Conversion component. Every field except <see cref="SourceColumnLineageId"/> is a direct promotion of the same-named <see cref="PipelineOutputColumnSpec"/> field on this column -- kept here too so a consumer of <see cref="DataConvertPayload"/> doesn't need to cross-reference <see cref="PipelineComponentSpec.Outputs"/> by name.</summary>
public sealed class DataConversionColumnSpec
{
    public required string OutputColumnName { get; init; }

    /// <summary>The <c>#{...}</c> reference stripped down to the raw lineageId string, pointing at the upstream column being converted -- resolve the actual source column NAME via <c>Ssis.Extract.Dtsx.LineageBuilder</c>'s own producer index (same join key everything else in this model uses), not by string-matching here.</summary>
    public string? SourceColumnLineageId { get; init; }

    public string? TargetDataType { get; init; }
    public int? Length { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    public int? CodePage { get; init; }
    public string? ErrorRowDisposition { get; init; }
    public string? TruncationRowDisposition { get; init; }

    /// <summary>SSIS's own "use the faster, locale-neutral parsing routines" toggle -- observed <c>false</c> on the one real evidenced instance.</summary>
    public bool? FastParse { get; init; }
}

public sealed class ScriptComponentPayload
{
    public string? Language { get; init; }
    public string? ProjectName { get; init; }
    public List<string> ReadOnlyVariables { get; init; } = [];
    public List<string> ReadWriteVariables { get; init; } = [];

    public List<string> SourceCodeItems { get; init; } = [];
    public bool HasBinaryCode { get; init; }

    /// <summary>True when <c>BinaryCode</c> is non-empty but <see cref="SourceCodeItems"/> is empty -- same "source genuinely gone" signal as <c>ScriptTaskPayload.SourceStripped</c>, not yet observed on any real package.</summary>
    public bool SourceStripped { get; init; }
}

/// <summary>
/// <c>Microsoft.Sort</c>. Confirmed real via RBC_Demo_ETL's own Package_Transforms.dtsx
/// (<c>SORT_Customers</c>/<c>SORT_Contacts</c>/<c>SORT_Canada</c>/<c>SORT_RestOfWorld</c>),
/// discovered testing this tool against a real third-party portfolio (SSIS_From_Sandeep,
/// 2026-08-27). The key designation lives on Sort's own INPUT columns (every one -- key or
/// plain passthrough -- carries its own <c>NewSortKeyPosition</c> custom property; <c>0</c>
/// means "not a key, just passed through", a non-zero value means "sort key at this position"),
/// NOT the output side, contrary to an initial misreading of the raw XML that attributed it to
/// the output column instead. Each OUTPUT column separately carries its own <c>SortColumnId</c>,
/// a <c>#{...}</c> reference to the INPUT column it mirrors -- <c>Ssis.Extract.Dtsx.PipelineReader</c>
/// resolves the two together (matching stripped <c>SortColumnId</c> against each input column's
/// own LineageId) to know which OUTPUT column name is the real sort key. Both real evidenced
/// instances use exactly one positive-valued key -- see <see cref="Keys"/>'s own doc comment for
/// what a negative value would mean, unconfirmed.
/// </summary>
public sealed class SortPayload
{
    public List<SortKeySpec> Keys { get; init; } = [];
}

/// <summary>One of a Sort component's own key columns, in the ORDER SSIS itself applies them
/// (<see cref="Position"/>, ascending by absolute value -- SSIS's own multi-column sort key
/// ordering). <see cref="Position"/> is kept as the RAW signed value read from
/// <c>NewSortKeyPosition</c> rather than pre-decoded into an <c>IsDescending</c> flag: only a
/// positive value is evidenced in this portfolio (an ascending sort), so a negative value --
/// which a naive reading of SSIS's documented "negative position means descending" convention
/// would suggest -- is deliberately NOT decoded here, following the same "don't hand-guess an
/// enum/sign without independent confirmation" rule as <c>OleDbDestinationPayload.AccessMode</c>.</summary>
public sealed class SortKeySpec
{
    public required string ColumnName { get; init; }
    public required int Position { get; init; }
}

/// <summary>
/// <c>Microsoft.MergeJoin</c>. Confirmed real via RBC_Demo_ETL's own Package_Transforms.dtsx
/// (<c>MRG_CustomerContacts</c>), discovered testing this tool against a real third-party
/// portfolio (SSIS_From_Sandeep, 2026-08-27). <see cref="JoinTypeRaw"/> is kept as the raw
/// integer only, same "don't hand-guess an enum without independent confirmation" rule as
/// <c>OleDbDestinationPayload.AccessMode</c> -- see <c>Etl.Core.Abstractions.MergeJoinType</c>'s
/// own doc comment for which raw value is actually confirmed (2 = LeftOuter, via a real
/// generated run) versus merely documented.
/// </summary>
public sealed class MergeJoinPayload
{
    public int? JoinTypeRaw { get; init; }
    public int? NumKeyColumns { get; init; }
    public bool? TreatNullsAsEqual { get; init; }
    public List<MergeJoinOutputColumnSpec> OutputColumns { get; init; } = [];
}

/// <summary>One of a Merge Join's own output columns, resolved back to whichever of its OWN
/// Left/Right Input columns produced it (<c>InputColumnID</c>, a <c>#{...}</c> reference to
/// that INPUT column's own refId -- not a lineageId reference the way Data Conversion's
/// <c>SourceInputColumnLineageID</c> is; resolved once at read time by matching against this
/// same component's own <see cref="Ssis.Extract.Model.Pipeline.PipelineComponentSpec.Inputs"/>).
/// <see cref="SourceColumnLineageId"/> is that matched input column's own LineageId, so codegen
/// can continue tracing back to the true origin (Sort's own passthrough output, ultimately the
/// raw source column) exactly like any other column reference.</summary>
public sealed class MergeJoinOutputColumnSpec
{
    public required string OutputColumnName { get; init; }

    /// <summary>"Left" or "Right" -- which of the Merge Join's own two inputs produced this column.</summary>
    public required string Side { get; init; }

    public required string SourceColumnLineageId { get; init; }
}
