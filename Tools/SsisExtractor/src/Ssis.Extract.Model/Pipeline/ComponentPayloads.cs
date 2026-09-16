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
/// <c>Microsoft.OLEDBCommand</c> -- added 2026-08-28 testing this tool against
/// SSIS_From_Sandeep's own <c>Package_Advanced.dtsx</c> (<c>DFT_FlagCustomers</c>'s
/// <c>OLECMD_SetFlag</c>: <c>EXEC dbo.usp_SetCustomerFlag ?, N'flagged'</c>). Unlike every
/// other bespoke destination-shaped payload in this file, this component has NO destination
/// table at all -- it executes <see cref="SqlCommand"/> once per input row, with each
/// <c>?</c> placeholder bound to one input column.
///
/// <para><b>Real binding order, corrected 2026-09-02 (gap-audit Phase 3.1), TWICE in one
/// round:</b> the original 2026-08-28 doc comment here claimed binding order was "the
/// component's own &lt;inputColumns&gt;, in declaration order" -- true only by coincidence for
/// the single-parameter case that was the only evidence available then. A live SSIS 16.0/2022
/// object-model probe (two parameters, deliberately attached in the OPPOSITE declaration order
/// from their own placeholder position) proved that's wrong in general. The first fix tried
/// resolving order from a <c>Param_&lt;N&gt;</c> numeric suffix on each bound external column's
/// own name -- which then REGRESSED the one real evidenced instance above, because a real
/// EXEC-stored-procedure call's external column is named after the procedure's own parameter
/// (<c>"@CustomerID"</c>), not <c>"Param_0"</c>. A second probe (a real 2-parameter stored
/// procedure, columns again attached out of order) proved the actually-reliable signal is
/// simpler and covers both shapes: each bound external column's own INDEX within the input's
/// <c>&lt;externalMetadataColumns&gt;</c> list, in the order <c>ReinitializeMetaData</c> itself
/// saved them -- confirmed to already be the true call/parameter order in both the positional
/// and the named-parameter case, independent of both &lt;inputColumns&gt; declaration order and
/// of whether the external column's own name encodes a position at all.
/// <see cref="ColumnMappings"/> (built the same way <see cref="OleDbSourcePayload.ColumnMappings"/>
/// is) carries each input column's own bound external column NAME; codegen resolves true
/// position by looking that name up in the input's own external-column list order.</para>
///
/// <para><see cref="ParameterMapping"/> was also probed the same session and found to never
/// be populated at all -- not merely empty, genuinely absent from the saved XML even under a
/// deliberately non-default binding. It appears to be dead/vestigial metadata for this SSIS
/// version, not the real carrier of order; kept only so codegen can still treat a future,
/// unevidenced non-empty value as a named gap rather than silently ignoring it.</para>
/// </summary>
public sealed class OleDbCommandPayload
{
    public string? ConnectionName { get; init; }
    public string? SqlCommand { get; init; }
    public string? ParameterMapping { get; init; }
    public int? CommandTimeout { get; init; }

    /// <summary>The command's own input columns, in raw declaration order -- kept for backward
    /// compatibility and diagnostics, but NOT a reliable source of placeholder binding order.
    /// Use <see cref="ColumnMappings"/> for that.</summary>
    public List<OleDbCommandParameterSpec> Parameters { get; init; } = [];

    /// <summary>Input columns ↔ external <c>Param_N</c> columns, built from the main input's
    /// columns against their own <c>externalMetadataColumns</c> -- same construction as
    /// <see cref="OleDbSourcePayload.ColumnMappings"/>. <see cref="PipelineColumnMappingSpec.ExternalColumnName"/>
    /// is the real, position-bearing name (<c>"Param_0"</c>, <c>"Param_1"</c>, ...); codegen
    /// resolves the numeric suffix to determine true placeholder order.</summary>
    public List<PipelineColumnMappingSpec> ColumnMappings { get; init; } = [];
}

public sealed class OleDbCommandParameterSpec
{
    public required string Name { get; init; }
    public string? DataType { get; init; }
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

    /// <summary>
    /// <c>TreatDuplicateKeysAsError</c>. <c>false</c> on every Lookup evidenced anywhere in the
    /// tracked portfolio and fixtures, and that is the case whose behaviour is MEASURED: with it
    /// false, a full-cache Lookup whose reference query returns a duplicated key does not error
    /// and resolves matches to the FIRST occurrence (see <c>LookupCacheEmitter</c>). <c>true</c>
    /// is unevidenced and unmeasured -- the generated cache cannot error on a duplicate at all,
    /// so codegen gaps rather than silently ignoring the setting.
    /// </summary>
    public bool? TreatDuplicateKeysAsError { get; init; }

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
///
/// <para><b>2026-08-28, same day -- three more target types measured SPECULATIVELY</b> (with the
/// user's explicit sign-off; no real package in this tool's own portfolio needs any of them):
/// DT_R8 follows the exact same "NULL on any failure" convention as DT_I4/DT_DBDATE above,
/// including scientific-notation and whitespace-trimmed parsing. DT_BOOL also yields NULL on
/// failure, but accepts a WIDER input set than a bare <c>bool.TryParse</c> would -- "True"/
/// "False" case-insensitively AND the numeric strings "1"/"0". <b>DT_WSTR is a genuinely
/// different failure mode</b>, the first Data Conversion target this tool has measured that
/// ISN'T "NULL on failure": an overlong source string TRUNCATES to fit the declared target
/// width under IgnoreFailure/TruncationRowDisposition=IgnoreFailure, rather than nulling out --
/// there is no real numeric/date/bool "failure" to ignore for a string-to-string conversion, so
/// the disposition's only real job is truncation. An empty source string passes through as an
/// empty string, not NULL (unlike every numeric/date/bool target). See
/// <c>synthetic-data-conversion-types-tables.sql</c> for the exact seeded values and
/// <c>Ssis.Extract.Codegen.SsisFnEmitter</c>'s own <c>ToNullableR8</c>/<c>ToNullableBool</c>/
/// <c>ToWstr</c> for the reproducing code.</para>
/// </summary>
public sealed class DataConvertPayload
{
    public List<DataConversionColumnSpec> Columns { get; init; } = [];
}

/// <summary>
/// <c>Microsoft.CopyMap</c> -- the "Copy Column" transform (SSIS Toolbox name; the runtime's
/// own <c>PipelineComponentInfo.Name</c> is "Copy Column", <c>CreationName</c>
/// <c>DTSTransform.CopyMap.8</c>, confirmed via a live <c>Application.PipelineComponentInfos</c>
/// enumeration against this machine's real SSIS 16.0 install -- not guessed from documentation).
/// Pure column duplication: an input column copied verbatim into a new, identically-typed
/// output column under a new name, no expression, no cast. Structurally almost identical to
/// <see cref="DataConvertPayload"/> -- the same generic <see cref="PipelineOutputColumnSpec"/>
/// captures every copied column's own type/length/precision/scale, and the one bespoke piece is
/// <see cref="CopyMapColumnSpec.SourceColumnLineageId"/> -- except the underlying custom
/// property is named <c>copyColumnId</c> (lowercase, its own distinct name; NOT
/// <c>SourceInputColumnLineageID</c>, which is Data Conversion's own property name), confirmed
/// via a real, live SSIS object-model round trip: building a real <c>Microsoft.CopyMap</c>
/// component, marking a source column <c>UT_READONLY</c>, then <c>InsertOutputColumnAt</c> +
/// <c>SetOutputColumnProperty(..., "copyColumnId", sourceLineageId)</c> is what actually
/// resolves the new column's data type/length automatically from the source (mirroring
/// <c>Microsoft.Aggregate</c>'s own <c>AggregationColumnId</c>-driven type derivation, not Data
/// Conversion's explicit <c>SetOutputColumnDataTypeProperties</c> call -- that call throws
/// <c>COMException 0xC020401A</c> here, exactly like Aggregate/Lookup do, confirming the type is
/// derived, not settable). The persisted value is the same <c>#{lineageId-path}</c> wrapper
/// every other lineage-reference custom property in this tool already uses (e.g.
/// <c>SourceInputColumnLineageID</c>), stripped the same way via
/// <c>DtsxPackageReader.StripLineageRef</c>.
///
/// <para>Also confirmed live: <c>SetUsageType(..., UT_READWRITE)</c> on the source column throws
/// <c>COMException 0xC0204023</c> -- this component only ever marks a copied-from column
/// <c>UT_READONLY</c> (the original stays an untouched passthrough, resolved by
/// <c>Ssis.Extract.Dtsx.LineageBuilder</c> the same way every other synchronous transform's
/// untouched input columns already are), and the SAME source column can be copied into
/// multiple, differently-named output columns (each with its own <c>copyColumnId</c> pointing
/// at the identical source lineageId) -- both confirmed by a live round trip, not assumed.</para>
/// </summary>
public sealed class CopyMapPayload
{
    public List<CopyMapColumnSpec> Columns { get; init; } = [];
}

/// <summary>One copied output column of a Copy Column component. Every field except <see cref="SourceColumnLineageId"/> is a direct promotion of the same-named <see cref="PipelineOutputColumnSpec"/> field on this column -- same shape as <see cref="DataConversionColumnSpec"/>, kept here too so a consumer of <see cref="CopyMapPayload"/> doesn't need to cross-reference <see cref="PipelineComponentSpec.Outputs"/> by name.</summary>
public sealed class CopyMapColumnSpec
{
    public required string OutputColumnName { get; init; }

    /// <summary>The <c>#{...}</c> reference stripped down to the raw lineageId string, pointing at the upstream column being copied -- resolve the actual source column NAME via <c>Ssis.Extract.Dtsx.LineageBuilder</c>'s own producer index (same join key everything else in this model uses), not by string-matching here.</summary>
    public string? SourceColumnLineageId { get; init; }

    public string? TargetDataType { get; init; }
    public int? Length { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    public int? CodePage { get; init; }
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

    /// <summary>
    /// <see cref="SourceCodeItems"/> parsed into real files. The raw array is a flat sequence of
    /// repeating <b>(name, encoding, content)</b> triples -- see
    /// <c>PipelineReader.ParseScriptComponentSourceFiles</c> for how that was established and why
    /// a non-multiple-of-3 count yields an empty list rather than a guessed alignment.
    ///
    /// Note the entry point is <c>main.cs</c>/<c>main.vb</c> here, NOT the <c>ScriptMain.*</c> a
    /// Script *Task* uses -- one more place these two features diverge despite looking identical
    /// in the SSIS UI (see <see cref="ScriptTaskPayload"/> for the others). Everything else in the
    /// list is VSTA-generated scaffolding (ComponentWrapper.cs, BufferWrapper.cs, Properties\*,
    /// the .csproj, and an internal "Project" descriptor), not authored logic.
    /// </summary>
    public List<ScriptComponentSourceFileSpec> SourceFiles { get; init; } = [];

    public bool HasBinaryCode { get; init; }

    /// <summary>True when <c>BinaryCode</c> is non-empty but <see cref="SourceCodeItems"/> is empty -- same "source genuinely gone" signal as <c>ScriptTaskPayload.SourceStripped</c>, not yet observed on any real package.</summary>
    public bool SourceStripped { get; init; }
}

/// <summary>One VSTA project file inside a Script Component's own <c>SourceCode</c> array -- see <see cref="ScriptComponentPayload.SourceFiles"/>.</summary>
public sealed class ScriptComponentSourceFileSpec
{
    /// <summary>e.g. "main.cs" (the entry point) or "Properties\Settings.Designer.cs" -- a backslash-separated relative path, same convention as <see cref="ScriptProjectItemSpec.Name"/>.</summary>
    public required string Name { get; init; }

    public string? Encoding { get; init; }
    public required string Content { get; init; }
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
/// integer here (this layer decodes nothing); the semantics are MEASURED against real SSIS via
/// dtexec and mapped in <c>PackagePlanner.ResolveMergeJoinType</c> --
/// <b>0 = full outer, 1 = left outer, 2 = inner</b>. An earlier version of this comment claimed
/// "2 = LeftOuter, via a real generated run", which was wrong on both counts: raw 2 is INNER,
/// and that run only exercised generated code with LeftOuter hardcoded into it.
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

/// <summary>
/// <c>Microsoft.Aggregate</c>, built speculatively 2026-08-30 (GroupBy/Count only): RBC_Demo_ETL's
/// own real instance (<c>DFT_LookupAndAggregate\AGG_ByRegion</c>) sits downstream of a
/// <c>Microsoft.Lookup</c> that already blocks the whole flow regardless of Aggregate support
/// (confirmed twice by re-surveying the tracked portfolio), so this was built on the user's own
/// explicit request rather than to close a real gap. <see cref="AggregateColumnSpec.AggregationTypeRaw"/>'s
/// own enum has no public Microsoft SDK reference and no managed CLR type backs it (confirmed by
/// reflecting every loaded assembly and finding nothing named <c>AggregationType</c>, and by
/// directly probing every string in {"GroupBy","Sum","Average","Min","Max","Count",
/// "CountDistinct","CountAll",...} against the live COM property -- every one REJECTED with
/// <c>0xC0204006</c>, confirming this is a pure native int enum with no string-coercion path at
/// all).
///
/// <para><b>Widened 2026-09-02 (gap-audit Phase 3.3) to the full raw-value mapping, measured via a
/// dedicated live dtexec probe</b> (deliberately distinguishing seed data -- a group with a NULL, a
/// duplicate value, and a wide numeric spread so Sum/Average/Min/Max/CountDistinct/CountAll could
/// never coincidentally collide -- see <c>PackagePlanner.PlanAggregate</c>'s own doc comment and
/// <c>Ssis.Extract.FixtureBuilder</c>'s temporary <c>aggregate-type-semantics-probe</c> mode):</para>
/// <list type="bullet">
/// <item><b>0 = GroupBy.</b></item>
/// <item><b>1 = Count</b> -- excludes NULL (ordinary SQL <c>COUNT(column)</c>), confirmed
/// 2026-08-30. Output type <c>DT_UI8</c>, never null.</item>
/// <item><b>2 = CountAll</b> -- includes every row regardless of NULL (a genuine
/// <c>COUNT(*)</c>), and is the ONE function SSIS accepts with no <c>AggregationColumnId</c> at
/// all (confirmed: setting AggregationType=2 with no column reference is accepted and produces
/// the identical row count as a column-qualified CountAll). Output type <c>DT_UI8</c>, never
/// null.</item>
/// <item><b>3 = CountDistinct</b> -- excludes NULL, dedups non-null values (ordinary SQL
/// <c>COUNT(DISTINCT column)</c>). Output type <c>DT_UI4</c> (narrower than Count/CountAll's own
/// UI8 -- confirmed real, not assumed uniform), never null.</item>
/// <item><b>4 = Sum</b> -- excludes NULL; <b>returns NULL, not 0, when a group has zero non-null
/// values</b> (confirmed via a dedicated all-NULL group -- this is SQL semantics, NOT .NET LINQ's
/// own <c>Enumerable.Sum</c> behavior over a nullable sequence, which was independently verified
/// in this same round to return 0 for an all-null/empty sequence -- so generated code needs an
/// explicit guard here, unlike Average/Min/Max below). Output type depends on the SOURCE column's
/// own type: widens an integer source to <c>DT_I8</c>, promotes a float source to <c>DT_R8</c>
/// (both confirmed real, not assumed to always promote to float).</item>
/// <item><b>5 = Average</b> -- excludes NULL; NULL for an all-NULL group. Output type is ALWAYS
/// <c>DT_R8</c>, regardless of the source column's own type (confirmed: an INT source still
/// produces a DT_R8 average, unlike Sum's own type-dependent promotion) -- and .NET's own
/// <c>Enumerable.Average</c> over a nullable sequence already returns null for an all-null/empty
/// sequence, so no explicit guard is needed in generated code, unlike Sum.</item>
/// <item><b>6 = Minimum</b>, <b>7 = Maximum</b> -- excludes NULL; NULL for an all-NULL group
/// (confirmed for both a float and an integer source column -- output type PRESERVES the source
/// column's own type in both cases, e.g. an INT source yields a DT_I4 Minimum/Maximum). .NET's
/// own <c>Enumerable.Min</c>/<c>Max</c> over a nullable sequence already return null for an
/// all-null/empty sequence, matching this exactly -- no explicit guard needed.</item>
/// </list>
/// <para>Any other raw value is a named generation gap, never a guess.</para>
/// </summary>
public sealed class AggregatePayload
{
    public List<AggregateColumnSpec> Columns { get; init; } = [];
}

/// <summary>One output column of an Aggregate component -- the GroupBy key
/// (<see cref="AggregationTypeRaw"/> == 0) or one of the seven measured aggregation functions
/// (1-7, see <see cref="AggregatePayload"/>'s own doc comment for the full mapping). Every other
/// value is a generation gap.</summary>
public sealed class AggregateColumnSpec
{
    public required string OutputColumnName { get; init; }

    /// <summary>The <c>#{...}</c> reference stripped down to the raw lineageId string, pointing
    /// at the upstream column this aggregation reads (the GroupBy key's own source column, or
    /// the column being counted) -- resolve the actual source column NAME via
    /// <c>Ssis.Extract.Dtsx.LineageBuilder</c>'s own producer index, same as every other
    /// lineageId-shaped reference in this model.</summary>
    public string? SourceColumnLineageId { get; init; }

    public int? AggregationTypeRaw { get; init; }
}

/// <summary>
/// <c>Microsoft.RowCount</c> -- added 2026-09-02 (gap-audit Phase 3.2). A pure synchronous
/// passthrough (confirmed real: its own saved <c>&lt;output&gt;</c> carries
/// <c>synchronousInputId</c>, and a downstream destination's own columns resolve by lineageId
/// straight past it to the true upstream producer, the same rule every other untouched
/// passthrough transform in this tool already follows -- so this payload carries no column
/// information at all, only the one real side effect: writing the number of rows that reached
/// it into a package variable.
///
/// <see cref="VariableName"/> is a plain <c>System.String</c> custom property (e.g.
/// <c>"User::MatchedRows"</c>), confirmed directly from RBC_Demo_ETL's own real, SSDT-authored
/// <c>Package_Transforms.dtsx</c> -- no live probe needed for this fact, since a real evidenced
/// file already settles it. What DID need a live SSIS 16.0/2022 dtexec probe (measured, not
/// assumed): that the count really is "rows that reached this specific component" -- proven by
/// a mid-chain RowCount feeding a real downstream destination, with a post-flow Script Task
/// reading the variable back and logging it, matching the destination's own actual row count
/// exactly.
/// </summary>
public sealed class RowCountPayload
{
    public string? VariableName { get; init; }
}

/// <summary>
/// <c>Microsoft.PctSampling</c> ("Percentage Sampling") -- Phase 4 of the unsupported-component-
/// types plan. A pure row router with exactly two mutually exclusive outputs
/// (<c>exclusionGroup="1"</c> on both), confirmed real from evidenced XML (UseCase_89's own
/// Package.dtsx): both outputs' own <c>&lt;externalMetadataColumns/&gt;</c> are empty, i.e. this
/// component neither adds, removes, nor transforms any column -- it only decides which of two
/// outputs each row goes to. <see cref="SamplingValue"/> is the declared sampling percentage
/// (0-100); <see cref="SamplingSeed"/> is the RNG seed.
///
/// Confirmed via a live object-model probe (<c>Ssis.Extract.FixtureBuilder</c>'s own
/// <c>ProbePctSampling</c>, not guessed): right after <c>ProvideComponentProperties()</c> the
/// component already declares exactly two outputs, in this fixed order --
/// "Sampling Selected Output" (the sampled rows) then "Sampling Unselected Output" (the rest) --
/// unlike Conditional Split/Multicast/Merge/MergeJoin, none of which start with their real
/// output set already in place. <see cref="SamplingValue"/>/<see cref="SamplingSeed"/> are plain
/// <c>System.Int32</c> custom properties, schema defaults 10/0 respectively.
///
/// Seed reproducibility was measured via a real dtexec probe (see the codegen project's own
/// <c>PctSamplingRouterEmitter</c> for the exact runs performed): a fixed <c>SamplingSeed</c>
/// reproduces the IDENTICAL row split across repeated runs of the real SSIS package; changing
/// the seed changes which rows are selected.
/// </summary>
public sealed class PctSamplingPayload
{
    public int SamplingValue { get; init; }
    public int SamplingSeed { get; init; }
}

/// <summary>
/// <c>Microsoft.XmlSourceAdapter</c> ("XML Source") -- Phase 5 of the unsupported-component-types
/// plan. Same discrimination shape as Script Component/ADO NET (own <c>ComponentClassId</c> is
/// the generic <c>Microsoft.ManagedComponentHost</c>; <c>UserComponentTypeName</c> is what
/// actually disambiguates it), confirmed real from evidenced XML
/// (<c>ETL-SSIS-Real-Scenarios/UseCase_73</c>'s own <c>Package.dtsx</c>, "XML Source" reading
/// <c>Sellers.xml</c> against <c>Sellers.xsd</c>) -- a flat <c>&lt;dataset&gt;&lt;record&gt;...
/// &lt;/record&gt;...&lt;/dataset&gt;</c> shape, one repeating <c>record</c> element per row,
/// direct scalar children as columns (<c>id</c>/<c>first_name</c>/<c>last_name</c>/<c>email</c>/
/// <c>gender</c>/<c>country</c>).
///
/// <para><see cref="AccessModeRaw"/>'s enum was confirmed via a live GAC reflection probe against
/// <c>Microsoft.SqlServer.XmlSrc.dll</c>'s own
/// <c>Microsoft.SqlServer.Dts.Pipeline.XmlSourceAdapter+AccessMode</c> type (same "ask the
/// runtime, don't guess" discipline as CLAUDE.md's trap 12) -- <b>0 = Default</b> (read
/// <see cref="XmlDataPath"/>, a literal design-time file path -- the one evidenced real value),
/// <b>1 = FileInVariable</b> (<see cref="XmlDataVariable"/> holds a path), <b>2 =
/// TextInVariable</b> (<see cref="XmlDataVariable"/> holds the XML document text itself, not a
/// path). Only mode 0 is supported by codegen -- 1/2 have no runtime-config mapping for an SSIS
/// variable, the same rule File System Task's own variable-driven path already established.</para>
///
/// <para>Unlike every other bespoke source payload in this file, the real evidenced component has
/// <b>no connection manager reference at all</b> -- confirmed by reading the raw XML directly:
/// there is no <c>&lt;connections&gt;</c> element between <c>&lt;component&gt;</c> and
/// <c>&lt;properties&gt;</c>. <see cref="XmlDataPath"/> is a plain literal property on the
/// component itself, the same shape File System Task's own literal (non-connection-manager) path
/// form already has.</para>
///
/// <para><see cref="XmlIntegerMappingRaw"/> (0 = Decimal, 1 = Int32, also confirmed via the same
/// GAC reflection probe) is captured but deliberately UNUSED by codegen -- the output column's own
/// resolved data type (e.g. the evidenced <c>id</c> column's <c>ui2</c>) already reflects whatever
/// this setting produced at design time, so there is nothing left for codegen to decide from it.</para>
///
/// <para><b>Deliberately scoped to the one evidenced flat-rowset shape only</b> -- a hierarchical
/// XML schema (more than one non-error, non-RowsetID-distinct output on this component) is a
/// generation gap, never guessed at; see <c>PackagePlanner</c>'s own XML source resolution for
/// where that count is checked (this payload's own <see cref="Columns"/> is always just the
/// FIRST non-error output's columns, mirroring every other single-output source payload here).</para>
/// </summary>
public sealed class XmlSourcePayload
{
    /// <summary>The literal design-time XML file path (<see cref="AccessModeRaw"/> == 0/null) --
    /// e.g. <c>D:\...\Sellers.xml</c>. Null/empty when <see cref="XmlDataVariable"/> is used
    /// instead.</summary>
    public string? XmlDataPath { get; init; }

    /// <summary>The <c>Namespace::Variable</c> reference used instead of <see cref="XmlDataPath"/>
    /// when <see cref="AccessModeRaw"/> is 1 (a path) or 2 (the XML text itself) -- unevidenced,
    /// always a fatal gap when populated (see this type's own doc comment).</summary>
    public string? XmlDataVariable { get; init; }

    public int? AccessModeRaw { get; init; }
    public int? XmlIntegerMappingRaw { get; init; }

    /// <summary>The main (non-error) output's columns ↔ external (schema) columns, built the same
    /// way as <see cref="OleDbSourcePayload.ColumnMappings"/>/<see cref="ExcelSourcePayload.ColumnMappings"/>.
    /// Always the FIRST non-error output only -- see this type's own doc comment for the
    /// multi-output (hierarchical XML) scoping rule.</summary>
    public List<PipelineColumnMappingSpec> ColumnMappings { get; init; } = [];
}

/// <summary>
/// <c>Microsoft.SCD</c> ("Slowly Changing Dimension") -- Phase 7 of the unsupported-component-types
/// plan, and the largest single component this tool models. Replaces what used to be nothing but a
/// generic <c>scd-component-present</c> "rewrite this by hand" finding in <c>RulesEngine</c> with
/// real structured data.
///
/// <para><b>Confirmed real</b> from evidenced XML (<c>ETL-SSIS-Real-Scenarios/SCD SSIS</c>'s own
/// <c>SCD.dtsx</c>: <c>Emp_Source</c> -&gt; SCD -&gt; four wired downstream chains against
/// <c>dbo.DimEmployee</c>) <b>and</b> from a live object-model probe
/// (<c>Ssis.Extract.FixtureBuilder</c>'s own <c>ProbeScd</c>, not guessed). The probe establishes
/// the out-of-the-box shape, which matters because unlike Conditional Split/Multicast/Merge/MergeJoin
/// -- each of which needed its own distinct "how do I add an output" recipe -- <b>this component
/// declares all SIX of its outputs immediately after <c>ProvideComponentProperties()</c></b>, in this
/// fixed order and with these fixed exclusion groups, and one runtime connection named
/// <c>LookupConnection</c>:
/// <list type="number">
/// <item><c>Unchanged Output</c> (exclusionGroup 1)</item>
/// <item><c>New Output</c> (1)</item>
/// <item><c>Fixed Attribute Output</c> (1)</item>
/// <item><c>Changing Attribute Updates Output</c> (1)</item>
/// <item><c>Historical Attribute Inserts Output</c> (<b>2</b> -- deliberately its own group, so a
/// row can reach it as well as a group-1 output)</item>
/// <item><c>Inferred Member Updates Output</c> (1)</item>
/// </list>
/// Every one is synchronous on the single input and declares no output columns of its own, so this
/// component neither adds nor transforms a column -- it only decides which output each row goes to
/// (the same pure-router shape <see cref="PctSamplingPayload"/> already documents, just with six
/// outputs instead of two).</para>
///
/// <para><b>Schema defaults measured by the same probe</b>, which is what makes an ABSENT property in
/// a real <c>.dtsx</c> readable: <see cref="SqlCommand"/>/<see cref="CurrentRowWhere"/>/
/// <see cref="InferredMemberIndicator"/> empty, <see cref="UpdateChangingAttributeHistory"/> false,
/// <b><see cref="FailOnFixedAttributeChange"/> TRUE</b> (the real evidenced package explicitly sets
/// it false -- so this one property's absence would mean the OPPOSITE of the other booleans'),
/// <see cref="EnableInferredMember"/> false, <see cref="FailOnLookupFailure"/> false,
/// <see cref="IncomingRowChangeTypeRaw"/> 1, <see cref="DefaultCodePage"/> 1252.</para>
///
/// <para><b><see cref="ScdColumnSpec.ColumnTypeRaw"/>'s enum is NOT resolvable by reflection here</b>
/// -- unlike <see cref="XmlSourcePayload.AccessModeRaw"/>, whose enum was read straight out of a
/// managed GAC assembly. <c>Microsoft.SCD</c> is implemented by a NATIVE <c>TxSCD.dll</c> (confirmed:
/// the probe's own instance type is the generic <c>CManagedComponentWrapperClass</c>, and a scan of
/// every managed SSIS assembly in <c>DTS\Binn</c>/the VS SSIS extension found no <c>ColumnType</c>
/// or <c>IncomingRowChangeType</c> enum at all), and the <c>typeConverter="ColumnType"</c> attribute
/// the saved XML carries is resolved by the designer UI, not by anything loadable here. The mapping
/// below is therefore <b>measured behaviourally via a real dtexec run</b> (Phase 7b) -- see
/// <c>PackagePlanner.ScdColumnType</c> for the measurement and its evidence, and note that the real
/// evidenced package's own companion <c>update.txt</c> independently labels each changed column with
/// the SCD type it is meant to demonstrate, which agrees with the measurement exactly.</para>
/// </summary>
public sealed class ScdPayload
{
    /// <summary>The <c>LookupConnection</c> runtime connection's resolved connection-manager name --
    /// the connection the dimension table is read through. Named differently from every other
    /// component's own connection (which are <c>OleDbConnection</c>/<c>OLE DB Connection</c>), so it
    /// is resolved from the component's own single runtime connection rather than by that name.</summary>
    public string? ConnectionName { get; init; }

    /// <summary>The SELECT that reads the dimension table -- "Specifies the SELECT statement used to
    /// create a schema rowset" per the property's own description. Real evidenced value:
    /// <c>SELECT [Designation], [EmpId], [FirstName], [LastName],[StartDate],[EndDate] FROM [dbo].[DimEmployee]</c>.
    /// Note it selects the CURRENT-row marker columns (<c>StartDate</c>/<c>EndDate</c>) even though
    /// neither is an input column -- they exist only to satisfy <see cref="CurrentRowWhere"/>.</summary>
    public string? SqlCommand { get; init; }

    /// <summary>"Specifies the WHERE clause in the SELECT statement that selects the current row among
    /// rows with identical business keys" -- real evidenced value
    /// <c>[StartDate] IS NOT NULL AND [EndDate] IS NULL</c>. Empty when the dimension keeps no history
    /// at all (no historical-attribute column), in which case every business key has exactly one row.</summary>
    public string? CurrentRowWhere { get; init; }

    /// <summary>"Indicates whether historical attribute updates are directed to the transformation
    /// output for changing attribute updates" -- i.e. when true, a CHANGING-attribute change on a row
    /// that also has history is routed differently. False on the one real evidenced instance and on
    /// the schema default.</summary>
    public bool? UpdateChangingAttributeHistory { get; init; }

    /// <summary>"Indicates whether the transformation fails when columns with fixed attributes contain
    /// changes". <b>Schema default is TRUE</b> (probe-confirmed); the real evidenced package sets it
    /// false, which is what makes its own <c>Fixed Attribute Output</c> reachable at all.</summary>
    public bool? FailOnFixedAttributeChange { get; init; }

    /// <summary>"Specifies the column name for the inferred member" -- the dimension column that marks
    /// a placeholder row. Empty on the one real evidenced instance.</summary>
    public string? InferredMemberIndicator { get; init; }

    /// <summary>"Indicates whether inferred member updates are detected". False on the one real
    /// evidenced instance, so <c>Inferred Member Updates Output</c> is never reachable there.</summary>
    public bool? EnableInferredMember { get; init; }

    /// <summary>"Indicates whether the transformation fails when a lookup of an existing record fails".
    /// False on the one real evidenced instance.</summary>
    public bool? FailOnLookupFailure { get; init; }

    /// <summary>Raw <c>IncomingRowChangeType</c> integer -- "Specifies that all rows in the input are
    /// new or the transformation detects the change type". 1 on both the schema default and the one
    /// real evidenced instance; no other value is evidenced, and the enum is not resolvable by
    /// reflection (see this type's own doc comment), so codegen treats anything else as a named gap
    /// rather than guessing (same raw-enum caveat as <see cref="OleDbDestinationPayload.AccessMode"/>).</summary>
    public int? IncomingRowChangeTypeRaw { get; init; }

    public int? DefaultCodePage { get; init; }

    /// <summary>Every input column with its own declared <c>ColumnType</c>, in input declaration
    /// order. This is the component's whole semantic core -- which column is the business key, which
    /// are Type 1, which are Type 2, which are fixed.</summary>
    public List<ScdColumnSpec> Columns { get; init; } = [];
}

/// <summary>One <c>Microsoft.SCD</c> input column and its declared <c>ColumnType</c> -- see
/// <see cref="ScdPayload"/>'s own doc comment for why the enum's meaning had to be measured
/// behaviourally rather than reflected.</summary>
public sealed class ScdColumnSpec
{
    public required string ColumnName { get; init; }

    /// <summary>Raw <c>ColumnType</c> integer, verbatim. Null when the input column declares none at
    /// all -- a real, reachable case (an input column present in the buffer but not participating in
    /// the SCD comparison), kept distinct from any decoded value so nothing is inferred from absence.</summary>
    public int? ColumnTypeRaw { get; init; }

    public string? DataType { get; init; }
    public int? Length { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    public int? CodePage { get; init; }
}
