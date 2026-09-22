using System.Text.RegularExpressions;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen;

/// <summary>One ordinary single-destination flow's own delimited (never fixed-width) Flat File
/// Source, captured by <see cref="PackageGenerator.ResolveFlowSource"/> for
/// <see cref="SampleDataEmitter"/>/the "Source -- CSV" starter test to consume once the source
/// method's own generated NAME is known (only <c>methodInventory</c>, returned by
/// <see cref="PackageClassEmitter.Emit"/>, actually has that -- see
/// <see cref="ComponentMethodEntry"/>'s own doc comment for why this can't be re-derived).</summary>
public sealed record CsvSampleCandidate(string FileSourceKey, ConnectionManagerSpec ConnectionManager, string RowTypeName, string RowTypeNamespace, string ComponentName, PipelineComponentSpec? SourceComponent = null);

/// <summary>An OLE DB Command flow's own row type/parameter facts, captured by
/// <see cref="PackageGenerator.GenerateOleDbCommandFlow"/> once it has already resolved the
/// flow's real source's row type AND joined every parameter column back to its own CLR type via
/// <see cref="PipelineResolver"/> -- exactly what <see cref="ComponentTestEmitter.EmitOleDbCommandTest"/>
/// needs to synthesize rows for the "OLE DB Command" starter test, without re-resolving the
/// source (or the parameter binding) a second time.</summary>
public sealed record OleDbCommandTestCandidate(
    string StepName, string RowTypeName, string RowTypeNamespace, string SqlTemplate,
    IReadOnlyList<string> ParameterColumnNames, IReadOnlyList<ResolvedColumn> SourceColumns);

/// <summary>A Multicast flow's own row type/branch facts, captured by
/// <see cref="PackageGenerator.GenerateMulticastFlow"/> once every branch has already resolved
/// successfully -- exactly what <see cref="ComponentTestEmitter.EmitMulticastTest"/> needs to
/// construct the same <c>ConditionalSplitBranch&lt;TRow,TEntity&gt;</c> list the generated step
/// method itself builds, without re-resolving the source or any branch a second time.</summary>
public sealed record MulticastTestCandidate(
    string StepName, string RowTypeName, string RowTypeNamespace,
    IReadOnlyList<ResolvedColumn> SourceColumns, IReadOnlyList<ComponentTestEmitter.MulticastTestBranch> Branches);

/// <summary>An Aggregate flow's own row type/function facts, captured by
/// <see cref="PackageGenerator.GenerateAggregateFlow"/> once it has already resolved the flow's
/// real (always-SQL) source and every Count function's own source column to a CLR type --
/// exactly what <see cref="ComponentTestEmitter.EmitAggregateSourceTest"/> needs to synthesize
/// rows for the "Aggregate source" starter test, without re-resolving the source a second
/// time.</summary>
public sealed record AggregateSourceTestCandidate(
    string ComponentName, string SourceRowTypeNamespace, string SourceRowTypeName,
    string AggregateRowTypeNamespace, string AggregateRowTypeName,
    string GroupByPropertyName, string GroupByKeyClrType,
    IReadOnlyList<ComponentTestEmitter.AggregateSourceTestFunction> Functions);

/// <summary>One ordinary single-destination flow's own Flat File Destination, captured (again,
/// pilot-scoped to the ordinary flow path only, not Conditional Split/Multicast) so the "Sink --
/// flat file" starter test can write real rows through the real sink to a real temp file and read
/// them back, with no format-duplication risk -- keyed by ENTITY NAME, the same identity
/// <c>ComponentMethodEntry.SsisName</c> already carries for a "Sink" method (see
/// <c>PackageClassEmitter</c>'s own <c>inventory.Add(new ComponentMethodEntry("Sink", entityName,
/// ...))</c> -- a destination component's own free-text SSIS name is never threaded this far, so
/// entityName is already the join key the SQL sink test uses too).</summary>
public sealed record FlatFileSinkCandidate(FlatFileFlowSink Sink, string EntityName, PipelineComponentSpec DestinationComponent);

public sealed record PackageGenerateResult(
    string PackageName,
    List<GeneratedFile> Files,
    List<GenerationGap> Gaps,
    string? TargetServer = null,
    string? TargetDatabase = null)
{
    /// <summary>Files that must NOT land under <c>generate/{PackageName}/</c> alongside
    /// <see cref="Files"/> -- each one's own <see cref="GeneratedFile.RelativePath"/> is relative
    /// to the `--out` directory's own <c>generate/</c> folder directly. Today this is exactly
    /// <c>{PackageName}.Tests/</c>'s own project file and starter test classes: a sibling
    /// directory, not a subdirectory of the main project, is what keeps the main project's own
    /// SDK-style <c>**/*.cs</c> compile glob from picking up test files that reference xUnit (a
    /// package reference the main, non-test project never takes) -- see
    /// <c>GenerateCommand.WriteFixedFiles</c>'s own doc comment for the rest of that wiring.
    /// Defaults to empty so every existing caller/test is unaffected.</summary>
    public List<GeneratedFile> SiblingFiles { get; init; } = [];
}

/// <summary>
/// One package's worth of orchestration over the 8 emitters + <see cref="PackagePlanner"/>:
/// plan the control flow, decide names for the generated types (derived from the
/// destination table name -- the one identifier a .dtsx actually carries that's fit to
/// become a C# type name), then call each emitter with the pieces it asks for. A flow that
/// fails an early gate (no OpenRowset, not fast-load, no Flat File Source) still lets every
/// OTHER flow in the package generate -- same per-item isolation ctl.usp_DispatchRunItems
/// uses, applied here to flows instead of packages.
///
/// Pulled out of `ssisx generate`'s CLI shell (GenerateCommand.cs) specifically so it can be
/// unit- and golden-file-tested without going through PackageLoader/file I/O -- the same
/// "pure function returning GeneratedFiles, command is a thin shell" split every other
/// emitter in this project already follows.
/// </summary>
public static partial class PackageGenerator
{
    /// <param name="decisions">
    /// Confirmed Tier-1 answers (see <see cref="GapDecisions"/>) -- today just Lookup join keys.
    /// Optional so every existing caller and test is unaffected; absent or unconfirmed, the gaps
    /// they would have closed are reported exactly as before.
    /// </param>
    /// <param name="skipTests">Docs/Generated-Tests-Plan.md's own "Opt-out": generates the main
    /// package project only, with none of the starter `{Package}.Tests` project this method
    /// otherwise always produces. Defaults to false (tests on) so every existing caller/test is
    /// unaffected -- named plainly, unlike `--unsafe-skip-seams`, because skipping tests loses
    /// coverage but can never produce silently WRONG code the way skipping a seam can.</param>
    /// <param name="includeNotifications">Whether to wire IPackageResultNotifier/
    /// AddEmailNotifications at all. Defaults to false: a .dtsx carries no notification-recipient
    /// information at all (see the {Package}.Notification gap below), so wiring an email-sending
    /// hook on every generated package by default would add a capability the original SSIS
    /// package never had any equivalent of, on the strength of nothing but "maybe someone
    /// configures it later." Opt in via `ssisx generate --notifications` once real recipients/SMTP
    /// settings actually exist.</param>
    public static PackageGenerateResult Generate(PackageSpec package, string? namespacePrefix, GapDecisions? decisions = null, bool emitSeams = false, bool skipTests = false, bool includeNotifications = false)
    {
        decisions ??= GapDecisions.None;
        var ns = namespacePrefix is null ? package.ObjectName : $"{namespacePrefix}.{package.ObjectName}";
        var files = new List<GeneratedFile>();
        var gaps = new List<GenerationGap>();
        // {PackageName}.Tests/ -- populated per-flow (see the plain single-destination flow
        // path below), written to a SIBLING directory of the main project, never nested inside
        // it. Empty unless at least one flow's own starter test was actually generated.
        var testFiles = new List<GeneratedFile>();
        // One honest note per starter test file -- see TestCoverageNote's own doc comment and
        // Docs/AI-Test-Enrichment-Plan.md. Populated via NoteTest right alongside every
        // testFiles.Add(...) below; never a gap, never read by PortfolioDigest.
        var testCoverageNotes = new List<TestCoverageNote>();

        // Found running this generator against a real third-party portfolio
        // (D:\PoC\SSIS_Packages_From_GitHub, `sql-server-samples`' own `DailyETLMain.dtsx`):
        // SSIS lets any two Execute SQL Tasks anywhere in one package share the identical display
        // name -- a real, evidenced authoring pattern (a reusable task template, e.g. "Get
        // Lineage Key", copy-pasted into many branches, each with its own DIFFERENT SQL statement
        // for a different dimension; that one package had it 13 times, plus a second name 6
        // times). SanitizeIdentifier alone would collide every duplicate's own
        // Mapping/{Name}Statement.cs onto the identical file path, and a plain File.WriteAllText
        // would silently keep only the LAST one written -- discarding every earlier instance's own
        // distinct SQL text with no error and no gap (confirmed: reproduced the exact collision,
        // 12 of 13 "Get Lineage Key" statements and 5 of 6 "Get Last Movement..." ones lost).
        // Mirrors PackageClassEmitter's own Reserve() exactly (first occurrence keeps its bare
        // name, every later collision appends _2/_3/...), scoped to this ONE package's own
        // {ns}.Mapping namespace -- shared across every Execute SQL Task shape (pre-load,
        // post-flow, secondary-connection, ForEach-loop-per-iteration) since they all land there
        // and must not collide with each other either, not just within their own shape.
        var sqlStatementIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        string ReserveStatementIdentifier(string taskName)
        {
            var desired = SanitizeIdentifier(taskName);
            var name = desired;
            var n = 2;
            while (!sqlStatementIdentifiers.Add(name)) name = $"{desired}_{n++}";
            return name;
        }

        // A connection manager whose sensitive property is DPAPI-encrypted (ProtectionLevel
        // EncryptSensitiveWithUserKey/EncryptSensitiveWithPassword) is undecryptable outside the
        // original author's Windows account -- the extractor records that the value existed and
        // is missing, never the ciphertext. Raised up front, independent of flows/plan.Gaps, since
        // it's about a connection manager, not a Data Flow Task.
        //
        // Deliberately does NOT claim a User Secrets slot gets specially wired FOR this gap --
        // checked directly: ResolveDatabaseAuth/the SecondaryConnections builder both derive
        // AuthMode purely from ConnectionManagerSpec.Parsed.AuthMode == "SqlLogin" (itself just
        // "does the raw connection string carry User ID=/Integrated Security="), which is
        // completely independent of whether a <DTS:Password Encrypted="1"> node exists alongside
        // it. In every realistic case a SQL-auth connection manager's string already says so, so
        // the existing mechanism already wires the slot -- this gap's only job is to say the loud
        // part: THAT a real value must be supplied there, since the extractor found no other way
        // to tell a human this credential exists at all. If this connection manager is never
        // resolved into any generated flow, nothing generated needs the secret either, and there
        // is genuinely no slot to fill.
        foreach (var cm in package.ConnectionManagers.Where(cm => cm.EncryptedProperties.Count > 0))
        {
            var secretGapId = GapIdentity.ComputeId(package.ObjectName, GapKind.EncryptedConnectionManagerSecret, cm.ObjectName);
            var acknowledged = decisions.ResolveEncryptedSecretAcknowledgment(secretGapId, currentEvidenceSha256: null);
            var propertyList = string.Join(", ", cm.EncryptedProperties);
            gaps.Add(new GenerationGap(cm.ObjectName,
                $"Connection manager '{cm.ObjectName}' carries an encrypted sensitive value ({propertyList}) -- its ProtectionLevel DPAPI-encrypts this in the .dtsx, undecryptable outside the original author's Windows account. This tool never extracts the ciphertext or guesses the real value. If this connection manager is used by generated code and requires SQL authentication, User Secrets is already wired for it (see appsettings.json/the generate report for the exact key -- TargetDatabase:Password, or SecondaryConnections:{cm.ObjectName}:Password) -- supply the real value there before running the generated project" +
                (acknowledged ? "; acknowledged, see the report's decisions table" : "") + ".",
                Kind: GapKind.EncryptedConnectionManagerSecret,
                EvidenceRefId: cm.RefId));
        }

        var plan = PackagePlanner.Plan(package, emitSeams);
        gaps.AddRange(plan.Gaps);

        // A ForEach-Data-Flow-Loop's own inner Data Flow Task is deliberately NOT duplicated
        // into plan.Flows (see PackagePlan.Flows' own doc comment -- it exists purely as a
        // convenience projection of ordinary FlowSteps), so this gate must also check for one
        // directly or a package whose ONLY real content is such a loop would be wrongly
        // reported as having nothing to generate at all.
        if (plan.Flows.Count == 0 && !plan.Steps.Any(s => s is ForEachDataFlowLoopStep or ForLoopStep))
        {
            gaps.Add(new GenerationGap(package.ObjectName, "no Data Flow Task could be planned for this package -- nothing to generate"));
            return new PackageGenerateResult(package.ObjectName, files, gaps);
        }

        var allExecutables = PackageTree.AllExecutables(package).ToList();
        var primaryKeysByDestination = PrimaryKeyInference.Infer(package, allExecutables)
            .ToDictionary(pk => pk.DestinationComponentRefId);

        var tables = new List<DbContextEmitter.TableSpec>();
        var wiredFlows = new Dictionary<DataFlowPlan, ProgramFlowSpec>();
        var wiredSplits = new Dictionary<DataFlowPlan, ProgramConditionalSplitStep>();
        var wiredMulticasts = new Dictionary<DataFlowPlan, ProgramMulticastStep>();
        var wiredOleDbCommands = new Dictionary<DataFlowPlan, ProgramOleDbCommandStep>();
        var wiredScds = new Dictionary<DataFlowPlan, ProgramScdStep>();
        var wiredDataFlowLoops = new Dictionary<ForEachFileDataFlowPlan, ProgramForEachDataFlowLoopStep>();
        var wiredForLoops = new Dictionary<ForLoopPlan, ProgramForLoopStep>();
        var fileSourceEntries = new List<FileSourceEntryRequest>();
        // "File System Task" taxonomy row (Docs/Generated-Tests-Plan.md): every source/destination
        // connection-manager key a File System Task step actually resolved, collected here (where
        // FileSystemStep is still directly in scope) so PackageHarness's own FileSourceOptions can
        // wire up a real, isolated temp path for each -- see TestDoublesEmitter's own doc comment.
        var fileSystemTaskKeys = new List<string>();
        var secondaryConnections = new Dictionary<string, SecondaryConnectionRequest>();
        var functionsUsed = new HashSet<string>();
        // 1-to-1 component-to-function mapping round (2026-09): one registry shared across EVERY
        // TransformEmitter.Emit call for this whole package -- see ComponentHolderRegistry's own
        // doc comment. This is what lets a Derived Column shared upstream of a Conditional Split
        // (or reused across a Union-All-remerge pair of branches) get exactly ONE static holder
        // class, referenced identically from every branch that needs it.
        var componentHolders = new ComponentHolderRegistry();
        // Entity name -> its own SQL destination component, for a single-destination flow's own
        // ordinary (non-Flat-File) sink -- populated here (where the destination component is
        // still directly in scope) and consumed AFTER PackageClassEmitter.Emit returns, since only
        // that call knows the sink method's own generated NAME (see ComponentMethodEntry). Keyed by
        // entity name because that is the one identifier both sides of this join share.
        var sqlSinkEntities = new Dictionary<string, PipelineComponentSpec>();
        // Entities whose sink is a RedirectingSqlFlowSink -- found 2026-09 generating the whole
        // real SSIS_From_Sandeep portfolio for the first time and actually running the result:
        // "Sink -- SQL"'s own starter test (EmitSqlSinkTest) assumes a plain SqlBulkSink and
        // asserts uow.BulkInserts, but RedirectingSqlSink writes row-by-row via
        // uow.ExecuteSqlAsync and never touches BulkInserts at all -- a real, previously-
        // undiscovered test-generation bug (RBC_Demo_ETL's own Package.dtsx/OLEDST_StagingCustomers
        // is the one real evidenced instance), not something a naive gap count would ever catch,
        // since the test still compiled -- only actually RUNNING it failed. Same "no test, no gap"
        // degrade EmitSqlSinkTest already uses for an unresolvable column, not a guessed fix.
        var redirectSinkEntityNames = new HashSet<string>(StringComparer.Ordinal);
        // Populated only by the ordinary single-destination flow's own ResolveFlowSource call
        // (Phase 2 of the generated-tests plan, "Source -- CSV" row) -- a genuinely delimited Flat
        // File Source, never a fixed-width one (a different physical write format, not attempted
        // this round), never a Conditional Split/Merge Join side (same "pilot one shape first"
        // discipline TransformTestEmitter's own doc comment already states).
        var csvSampleCandidates = new List<CsvSampleCandidate>();
        // Same "pilot one shape first" scoping as csvSampleCandidates -- only the ordinary
        // single-destination flow's own Flat File Destination, for the "Sink -- flat file"
        // starter test.
        var flatFileSinkCandidates = new List<FlatFileSinkCandidate>();
        // "OLE DB Command" taxonomy row (Docs/Generated-Tests-Plan.md): populated by
        // GenerateOleDbCommandFlow itself, once it has already resolved the source row type and
        // joined every parameter column to its own CLR type -- see OleDbCommandTestCandidate's
        // own doc comment for why this can't be re-derived a second time after the fact.
        var oleDbCommandTestCandidates = new List<OleDbCommandTestCandidate>();
        // "Multicast" taxonomy row (Docs/Generated-Tests-Plan.md): populated by
        // GenerateMulticastFlow itself, once every branch has already resolved successfully -- see
        // MulticastTestCandidate's own doc comment for why this can't be re-derived a second time.
        var multicastTestCandidates = new List<MulticastTestCandidate>();
        // "Aggregate source" taxonomy row (Docs/Generated-Tests-Plan.md): populated by
        // GenerateAggregateFlow itself, once the source and every Count function's own column
        // have already resolved -- see AggregateSourceTestCandidate's own doc comment.
        var aggregateSourceTestCandidates = new List<AggregateSourceTestCandidate>();
        string? authMode = null;
        string? userId = null;
        string? targetServer = null;
        string? targetDatabase = null;

        foreach (var flow in plan.Flows)
        {
            // Checked before ConditionalSplit and before any entity-name/source resolution --
            // SyntheticLookupSplit.dtsx has BOTH a Lookup and a Conditional Split in the same
            // flow, and the Lookup's own unrecoverable join key makes the whole flow unsafe to
            // wire regardless of what's downstream, so it must win. LookupCacheEmitter needs
            // only the Lookup component itself, not entityName/DestinationComponent, so hoisting
            // this check ahead of everything else changes nothing about its own behavior.
            if (flow.Lookup is { } lookup)
            {
                var cacheClassName = SanitizeIdentifier(lookup.Name) + "Cache";
                Merge(files, gaps, LookupCacheEmitter.Emit($"{ns}.Mapping", cacheClassName, lookup));

                // The join key IS persisted after all -- on the Lookup's own input column, as a
                // JoinToReferenceColumn custom property (see TryDeriveLookupJoinKey). A confirmed
                // decision is only the FALLBACK, for a Lookup that genuinely lacks it.
                var lookupGapId = GapIdentity.ComputeId(package.ObjectName, GapKind.LookupJoinKey, flow.TaskName);
                var joinKey = TryDeriveLookupJoinKey(lookup)
                              ?? decisions.ResolveLookupJoinKey(lookupGapId, currentEvidenceSha256: null);
                if (joinKey is { } resolvedJoinKey)
                {
                    var lookupFlow = GenerateLookupFlow(
                        package, ns, flow, lookup, cacheClassName, resolvedJoinKey, fileSourceEntries, files, tables,
                        functionsUsed, primaryKeysByDestination, gaps,
                        ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams,
                        aggregateSourceTestCandidates, csvSampleCandidates, componentHolders, secondaryConnections);
                    if (lookupFlow is not null)
                    {
                        wiredFlows[flow] = lookupFlow;
                        continue;
                    }
                    // Fell through: GenerateLookupFlow recorded exactly which part of the shape it
                    // could not handle. Deliberately does NOT re-report the generic
                    // "no join key" gap below -- the key WAS supplied; something else is missing.
                    continue;
                }

                gaps.Add(new GenerationGap(flow.TaskName,
                    $"Lookup '{lookup.Name}' declares no join key: none of its input columns carries a JoinToReferenceColumn property, which is where SSIS records it (an SSDT-authored Lookup always has one -- a Lookup built through the object model without mapping its input columns does not). This tool will not guess a join key: a wrong one compiles, runs, and silently produces wrong joined data on every row. A reference-table cache scaffold was generated at Mapping/{cacheClassName}.cs, but this flow's transform/Program.cs wiring were NOT. To close it: answer this flow's own work packet under gaps/ and record the confirmed key in fills/{package.ObjectName}.decisions.json -- the whole flow is then generated deterministically, with no hand-written code at all.",
                    // Tier 1: the ONE thing missing here is a datum (which reference column the
                    // input joins against), not code -- LookupCacheEmitter already emits a cache with
                    // a generic LoadAsync<TKey> designed to defer exactly this. Once the key is
                    // confirmed, the deterministic emitter can generate the whole flow itself.
                    Kind: GapKind.LookupJoinKey,
                    EvidenceRefId: lookup.RefId));
                continue;
            }

            // Checked right after Lookup, before ConditionalSplit/MergeJoin/etc. -- built
            // speculatively 2026-08-30 (see AggregatePlan's own doc comment). An Aggregate's own
            // output is a structurally different row shape from the flow's raw source (fewer
            // rows, new column set), so it must be resolved on its own dedicated path rather
            // than the ordinary single-destination flow below.
            //
            // A real, previously-SILENT correctness bug, found 2026-09-06 by an independent
            // review, not by this project's own test suite: flow.Multicast and flow.Aggregate are
            // resolved entirely independently by PackagePlanner (unlike ConditionalSplit/Multicast,
            // which ARE mutually exclusive there), so a plain Multicast feeding one live branch
            // straight to a destination and a SECOND live branch into an Aggregate -- with no
            // Lookup at all, the only shape this generator's own GenerateLookupThenAggregateFlow
            // was ever built to handle -- used to fall straight into GenerateAggregateFlow here,
            // which never reads flow.Multicast at all. Every OTHER branch is silently dropped (no
            // file, no gap), flow.DestinationComponent resolves to whichever branch's destination
            // PackagePlanner happened to pick FIRST (not necessarily the Aggregate's own real
            // target), and the Aggregate's own row shape gets wired against THAT destination's
            // unrelated schema -- reported as a fully-generatable package (0 blocking gaps) while
            // the generated project fails to even COMPILE (a real CS1061, confirmed against a
            // disposable fixture built to reproduce this exact shape). Gapped explicitly instead,
            // the same "don't guess, name it" response every other unsupported flow composition in
            // this file already gets -- this shape is unevidenced anywhere in the tracked portfolio
            // (the one real Aggregate-behind-a-Multicast case always goes through a Lookup with
            // exactly one live branch, which GenerateLookupThenAggregateFlow already handles
            // correctly), so this generator makes no attempt to support it, only to stop silently
            // mis-generating it.
            if (flow.Aggregate is not null && flow.Multicast is not null)
            {
                gaps.Add(new GenerationGap(flow.TaskName,
                    $"Aggregate '{flow.Aggregate.Component.Name}' and Multicast '{flow.Multicast.Component.Name}' both appear in the same Data Flow Task with no Lookup involved -- an Aggregate downstream of a Multicast is only supported when reached through a Lookup's own live branch (see GenerateLookupThenAggregateFlow); a plain Multicast+Aggregate composition with no Lookup is not evidenced anywhere and not supported yet. Every other Multicast branch would otherwise be silently dropped."));
                continue;
            }

            // Same bug CLASS as the Multicast+Aggregate check immediately above, closed
            // proactively rather than discovered by a real crash -- Phase 4 of the
            // unsupported-component-types plan. flow.PctSampling and flow.Aggregate are resolved
            // entirely independently by PackagePlanner (PctSampling is only mutually exclusive
            // with ConditionalSplit/Multicast there, not with Aggregate), so an Aggregate
            // downstream of one of Percentage Sampling's two branches would otherwise fall
            // straight into GenerateAggregateFlow below, which never reads flow.PctSampling at
            // all -- the OTHER branch would be silently dropped (no file, no gap) and the
            // Aggregate's own row shape would be wired against whichever destination
            // PackagePlanner happened to resolve first. Unevidenced anywhere in the tracked
            // portfolio, so gapped explicitly rather than guessed at.
            if (flow.Aggregate is not null && flow.PctSampling is not null)
            {
                gaps.Add(new GenerationGap(flow.TaskName,
                    $"Aggregate '{flow.Aggregate.Component.Name}' and Percentage Sampling '{flow.PctSampling.Component.Name}' both appear in the same Data Flow Task -- an Aggregate downstream of one of Percentage Sampling's two branches is not evidenced anywhere and not supported yet. The other branch would otherwise be silently dropped."));
                continue;
            }

            if (flow.Aggregate is { } aggregate)
            {
                var aggregateFlow = GenerateAggregateFlow(
                    package, ns, flow, aggregate, fileSourceEntries, files, tables, functionsUsed,
                    primaryKeysByDestination, gaps, ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams,
                    aggregateSourceTestCandidates);
                if (aggregateFlow is not null) wiredFlows[flow] = aggregateFlow;
                continue;
            }

            if (flow.ConditionalSplit is { } split)
            {
                var splitStep = GenerateConditionalSplitFlow(
                    package, ns, flow, split, fileSourceEntries, files, testFiles, testCoverageNotes, tables, functionsUsed,
                    primaryKeysByDestination, gaps, ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams,
                    csvSampleCandidates, componentHolders);
                if (splitStep is not null) wiredSplits[flow] = splitStep;
                continue;
            }

            if (flow.MergeJoin is { } mergeJoin)
            {
                var mergeJoinFlow = GenerateMergeJoinFlow(
                    package, ns, flow, mergeJoin, fileSourceEntries, files, testFiles, testCoverageNotes, tables, functionsUsed,
                    primaryKeysByDestination, gaps, ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams,
                    csvSampleCandidates);
                if (mergeJoinFlow is not null) wiredFlows[flow] = mergeJoinFlow;
                continue;
            }

            // A genuinely multi-independent-source Microsoft.Merge/UnionAll -- gap-audit
            // Phase 3.6 (2026-09-02). PlanUnion already guarantees flow.Union is the ONLY
            // populated field alongside DestinationComponent, mirroring MergeJoin's own
            // convention.
            if (flow.Union is { } union)
            {
                var unionFlow = GenerateUnionFlow(
                    package, ns, flow, union, fileSourceEntries, files, tables, functionsUsed,
                    primaryKeysByDestination, gaps, ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams);
                if (unionFlow is not null) wiredFlows[flow] = unionFlow;
                continue;
            }

            if (flow.Multicast is { } multicast)
            {
                var multicastStep = GenerateMulticastFlow(
                    package, ns, flow, multicast, fileSourceEntries, files, testFiles, testCoverageNotes, tables, functionsUsed,
                    primaryKeysByDestination, gaps, ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams,
                    multicastTestCandidates, csvSampleCandidates, componentHolders);
                if (multicastStep is not null) wiredMulticasts[flow] = multicastStep;
                continue;
            }

            // Phase 7 of the unsupported-component-types plan -- a fourth routing mechanism
            // alongside Conditional Split/Multicast/Percentage Sampling, but the only one whose
            // branches can run a per-row SQL command instead of (or as well as) inserting, so it
            // gets its own dictionary and its own emission case rather than reusing one of theirs.
            if (flow.Scd is { } scdPlan)
            {
                var scdStep = GenerateScdFlow(
                    package, ns, flow, scdPlan, fileSourceEntries, files, testFiles, testCoverageNotes, tables, functionsUsed,
                    primaryKeysByDestination, gaps, ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams,
                    csvSampleCandidates, componentHolders, secondaryConnections);
                if (scdStep is not null) wiredScds[flow] = scdStep;
                continue;
            }

            // Phase 4 of the unsupported-component-types plan. Structurally a routing DECISION
            // between two mutually exclusive branches (like Conditional Split), not an
            // unconditional fan-out (like Multicast) -- so this returns the SAME
            // ProgramConditionalSplitStep shape GenerateConditionalSplitFlow produces, and is
            // wired into the identical wiredSplits dictionary, needing no new dictionary or
            // downstream emission case at all.
            if (flow.PctSampling is { } pctSampling)
            {
                var pctSamplingStep = GeneratePctSamplingFlow(
                    package, ns, flow, pctSampling, fileSourceEntries, files, testFiles, testCoverageNotes, tables, functionsUsed,
                    primaryKeysByDestination, gaps, ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams,
                    csvSampleCandidates, componentHolders);
                if (pctSamplingStep is not null) wiredSplits[flow] = pctSamplingStep;
                continue;
            }

            // No destination table/entity/EF at all -- the command itself is the flow's sink, so
            // this bypasses every SQL-destination-shaped step below entirely (fast-load gate,
            // EntityEmitter, DbContextEmitter.TableSpec, TransformEmitter) the same way the
            // Lookup path bypasses them.
            if (flow.OleDbCommand is { } oleDbCommandPlan)
            {
                var oleDbCommandStep = GenerateOleDbCommandFlow(
                    package, ns, flow, oleDbCommandPlan, fileSourceEntries, files, gaps,
                    ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams,
                    oleDbCommandTestCandidates, csvSampleCandidates);
                if (oleDbCommandStep is not null) wiredOleDbCommands[flow] = oleDbCommandStep;
                continue;
            }

            // A Flat File Destination has no OpenRowset/table at all, so it gets a DIFFERENT
            // entity-name source (the task's own name, always present) and skips every
            // SQL-shaped gate/step below (fast-load, EF Core table, the "no Derived Column"
            // direct-copy restriction) that assumes a destination TABLE exists.
            var isFlatFileDestination = flow.DestinationComponent.ComponentClassId == "Microsoft.FlatFileDestination";

            var entityName = isFlatFileDestination
                ? SanitizeIdentifier(flow.TaskName)
                : TryResolveEntityName(flow.TaskName, flow.DestinationComponent, gaps);
            if (entityName is null) continue;

            // Milestone 1 covers fast-load destinations only (plan §0.3's own resolution --
            // AccessMode itself is never decoded, this is the minimal check that decision
            // settled on). Anything else needs row-by-row insert generation this tool doesn't
            // have yet. A Flat File Destination has no fast-load concept at all -- it's always
            // "supported" in that sense, so this gate simply doesn't apply to it.
            if (!isFlatFileDestination && !DestinationInfo.IsFastLoadConfigured(flow.DestinationComponent))
            {
                gaps.Add(new GenerationGap(flow.TaskName, $"destination '{flow.DestinationComponent.Name}' is not configured for fast load -- row-by-row insert generation is not supported yet"));
                continue;
            }

            var nullableColumnNames = ResolveNullableColumnNames(flow);

            var sourceResult = ResolveFlowSource(package, ns, entityName, flow, flow.DestinationComponent, fileSourceEntries, files, gaps, nullableColumnNames, csvSampleCandidates, secondaryConnections);
            if (sourceResult is null) continue; // ResolveFlowSource already added the reason
            var (rowTypeName, programSource) = sourceResult.Value;

            var transformClassName = entityName + "Transform";
            Merge(files, gaps, EntityEmitter.Emit($"{ns}.Model", entityName, flow.DestinationComponent, nullableColumnNames));

            // A direct-copy pipeline (no Derived Column/Data Conversion at all) was, until
            // 2026-08-28, ungenerated for an OLE DB/ADO NET destination -- flagged as "a separate,
            // still-unproven generator path for a SQL destination" purely because nothing had
            // ever built+run one. Flat File Destination got this exemption earlier (its own two
            // real evidenced instances, RBC_Demo_ETL's own DFT_ExportDelimited/DFT_ExportFixedWidth,
            // are BOTH pure column-copy exports), and TransformEmitter.Emit already handles an
            // empty DerivedColumns/DataConversions list correctly for ANY destination shape --
            // every column just becomes a plain `row.ColumnName` passthrough assignment, nothing
            // SQL-specific about it. RBC_Demo_ETL's own DFT_ExcelImport (Excel Source straight to
            // an OLE DB Destination, no Derived Column) is now what proves it for a SQL
            // destination too: generated, built against a copy of Etl.Core (0 warnings/0 errors),
            // and actually run against .\SQLFORPOC_2022, landing all 9 real rows from
            // DripEligibility.xlsx unchanged -- see Tools/SsisExtractor/CLAUDE.md's own "Excel
            // Source" section. So this gate is gone entirely now, not just widened.

            FlowSinkSpec sink;
            if (isFlatFileDestination)
            {
                var flatFileSink = ResolveFlatFileSink(package, flow.TaskName, flow.DestinationComponent, entityName, fileSourceEntries, gaps);
                if (flatFileSink is null) continue; // ResolveFlatFileSink already added the reason
                sink = flatFileSink;
                flatFileSinkCandidates.Add(new FlatFileSinkCandidate(flatFileSink, entityName, flow.DestinationComponent));
            }
            else
            {
                primaryKeysByDestination.TryGetValue(flow.DestinationComponent.RefId, out var primaryKey);
                tables.Add(new DbContextEmitter.TableSpec(entityName, flow.DestinationComponent, primaryKey));
                sqlSinkEntities.TryAdd(entityName, flow.DestinationComponent);

                if (flow.ErrorRedirect is { } errorRedirect)
                {
                    var redirectSink = ResolveErrorRedirectSink(ns, entityName, flow.DestinationComponent, errorRedirect,
                        files, tables, primaryKeysByDestination, flow.TaskName, gaps);
                    if (redirectSink is null) continue; // ResolveErrorRedirectSink already added the reason
                    sink = redirectSink;
                    redirectSinkEntityNames.Add(entityName);
                }
                else
                {
                    sink = new SqlFlowSink(flow.DestinationComponent.Name);
                }
            }

            var transformResult = TransformEmitter.Emit(new TransformRequest(
                EmitSeams: emitSeams,
                MappingNamespace: $"{ns}.Mapping",
                TransformClassName: transformClassName,
                RowTypeNamespace: programSource is CsvFlowSource or FixedWidthFlowSource ? $"{ns}.Csv" : programSource is ExcelFlowSource ? $"{ns}.Excel" : programSource is XmlFlowSource ? $"{ns}.Xml" : $"{ns}.Sql",
                RowTypeName: rowTypeName,
                EntityNamespace: $"{ns}.Model",
                EntityName: entityName,
                SsisFnNamespace: $"{ns}.Ssis",
                Pipeline: flow.Pipeline,
                DerivedColumns: flow.DerivedColumn is null ? [] : [flow.DerivedColumn],
                DataConversions: flow.DataConversion is null ? [] : [flow.DataConversion],
                CopyMaps: flow.CopyMap is null ? [] : [flow.CopyMap],
                DestinationComponent: flow.DestinationComponent,
                NullableColumnNames: nullableColumnNames,
                Holders: componentHolders));
            Merge(files, gaps, transformResult.Result);
            foreach (var fn in transformResult.SsisFunctionsUsed) functionsUsed.Add(fn);

            if (transformResult.Result.Files.Count == 0)
            {
                // TransformEmitter itself already recorded why (e.g. every column untranslatable) --
                // don't also wire a Program.cs flow around a transform class that doesn't exist.
                continue;
            }

            // A starter unit test for the transform that just got wired -- see
            // TransformTestEmitter's own doc comment for exactly what's covered. Now includes a
            // Data-Conversion-produced destination column referenced DIRECTLY (TransformTestEmitter
            // itself detects and gaps the harder case -- a Derived Column expression that
            // CROSS-REFERENCES a Data-Conversion column -- rather than misgenerating a row
            // initializer for a property that doesn't exist). Now ALSO called for a Flat File
            // Destination flow (Phase 2 of the generated-tests plan) -- TransformTestEmitter's own
            // row/column resolution goes through PipelineResolver.ResolveDestinationInput, which
            // already supports Microsoft.FlatFileDestination the same way (the destination's own
            // input columns, not its physical write format), so no emitter change was needed to
            // widen this call site; only the gate here.
            {
                var testResult = TransformTestEmitter.Emit(new TransformTestRequest(
                    TestNamespace: $"{package.ObjectName}.Tests",
                    TransformClassName: transformClassName,
                    MappingNamespace: $"{ns}.Mapping",
                    RowTypeNamespace: programSource is CsvFlowSource or FixedWidthFlowSource ? $"{ns}.Csv" : programSource is ExcelFlowSource ? $"{ns}.Excel" : programSource is XmlFlowSource ? $"{ns}.Xml" : $"{ns}.Sql",
                    RowTypeName: rowTypeName,
                    EntityNamespace: $"{ns}.Model",
                    EntityName: entityName,
                    Pipeline: flow.Pipeline,
                    DerivedColumns: flow.DerivedColumn is null ? [] : [flow.DerivedColumn],
                    DataConversions: flow.DataConversion is null ? [] : [flow.DataConversion],
                    CopyMaps: flow.CopyMap is null ? [] : [flow.CopyMap],
                    DestinationComponent: flow.DestinationComponent));
                gaps.AddRange(testResult.Gaps);
                foreach (var testFile in testResult.Files)
                {
                    var testFileFinal = testFile with { RelativePath = $"{package.ObjectName}.Tests/{testFile.RelativePath}" };
                    testFiles.Add(testFileFinal);
                    NoteTest(testCoverageNotes, testFileFinal, "Transform",
                        "asserts each output column for one representative row; does not exercise NULL/boundary inputs beyond the chosen representative value.");
                }
            }

            var rowCountVariableNames = flow.RowCounts is { Count: > 0 }
                ? flow.RowCounts.Select(rc => rc.VariableName).ToList()
                : null;
            var sortKey = flow.SortKey is { } sk ? new ProgramSortKeySpec(sk.KeyColumnName, sk.KeyClrType.ClrTypeName) : null;
            wiredFlows[flow] = new ProgramFlowSpec(flow.TaskName, programSource, rowTypeName, entityName, transformClassName, sink,
                RowCountVariableNames: rowCountVariableNames, SortKey: sortKey);

            if (!isFlatFileDestination && authMode is null && ResolveDatabaseAuth(package, flow.DestinationComponent) is { } auth)
            {
                (authMode, userId, targetServer, targetDatabase) = auth;
            }
        }

        // A ForEach-Data-Flow-Loop's own inner flow is not in plan.Flows (see the gate above),
        // so it needs its own generation loop here -- built speculatively 2026-08-30, see
        // GenerateForEachDataFlowLoop's own doc comment.
        foreach (var dataFlowLoopStep in plan.Steps.OfType<ForEachDataFlowLoopStep>())
        {
            var programLoop = GenerateForEachDataFlowLoop(
                package, ns, dataFlowLoopStep.Loop, files, tables, functionsUsed,
                primaryKeysByDestination, fileSourceEntries, gaps, ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams,
                componentHolders);
            if (programLoop is not null) wiredDataFlowLoops[dataFlowLoopStep.Loop] = programLoop;
        }

        // A For Loop Container's own inner flow is likewise not in plan.Flows -- same reason as
        // the ForEach-Data-Flow-Loop case immediately above.
        foreach (var forLoopStep in plan.Steps.OfType<ForLoopStep>())
        {
            var programForLoop = GenerateForLoop(
                package, ns, forLoopStep.Loop, files, tables, functionsUsed,
                primaryKeysByDestination, gaps, ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams,
                componentHolders);
            if (programForLoop is not null) wiredForLoops[forLoopStep.Loop] = programForLoop;
        }

        // A package whose only successfully-wired flows are Flat File Destinations has
        // tables.Count == 0 but still needs a (now possibly-empty) DbContext class -- Program.cs
        // always emits AddEtlDbContext<T>()/IUnitOfWork's own transaction wrapping needs a real
        // DbContext type regardless of whether anything maps to a SQL table (see
        // DbContextEmitter's own updated doc comment). Still correctly skipped when NOTHING
        // wired at all (an all-gapped package), matching existing behavior.
        // An OLE DB Command flow contributes no table of its own, but its own OleDbCommandStep
        // still runs through IUnitOfWork.ExecuteSqlAsync -- the same shared, DbContext-backed
        // connection/transaction as everything else in the package -- so a package whose ONLY
        // successfully-wired flow is an OLE DB Command still needs a (possibly table-less)
        // DbContext generated, same reasoning as the Flat File Destination case right above.
        // True once ANY flow shape successfully wires -- the one condition that decides both
        // whether a real PackageClassEmitter/ProgramEmitter/PackageHarness gets generated at all
        // (below) AND whether ResolveSqlStep's own step-level test can safely reference a
        // generated package method (see its own use of this flag). Computed once, here, so the
        // three places that used to repeat this same predicate independently can never drift.
        var willWire = wiredFlows.Count > 0 || wiredSplits.Count > 0 || wiredMulticasts.Count > 0 || wiredOleDbCommands.Count > 0 || wiredScds.Count > 0 || wiredDataFlowLoops.Count > 0 || wiredForLoops.Count > 0;

        if (tables.Count > 0 || willWire)
            Merge(files, gaps, DbContextEmitter.Emit($"{ns}.Model", $"{package.ObjectName}DbContext", tables));

        // A Microsoft.ExpressionTask's own translated assignment can call an SsisFn helper too
        // (e.g. SsisFn.DateAddMillisecond/DatePartMillisecond, Phase 6) -- collected here, BEFORE
        // SsisFnEmitter.Emit below, the same "SsisFn.X(" string-match convention
        // TransformEmitter.CollectSsisFunctions already uses for a Derived Column, so this
        // package's Ssis/SsisFn.cs gets emitted with the right bodies even for a package with no
        // Data Flow Task of its own.
        foreach (var expressionStep in plan.Steps.OfType<ExpressionStep>())
            TransformEmitter.CollectSsisFunctions(expressionStep.CSharpValueExpression, functionsUsed);

        Merge(files, gaps, SsisFnEmitter.Emit($"{ns}.Ssis", functionsUsed));

        // Built by walking plan.Steps (not wiredFlows.Values) so a SqlStep between two flows
        // keeps its real position -- a flow that never made it into wiredFlows/wiredSplits/
        // wiredMulticasts/wiredOleDbCommands (failed a gate above) simply contributes nothing
        // here, same as it always has.
        var programSteps = new List<ProgramStep>();
        foreach (var step in plan.Steps)
        {
            var programStepsBefore = programSteps.Count;
            switch (step)
            {
                case FlowStep { Flow: var flow } when wiredFlows.TryGetValue(flow, out var programFlow):
                    programSteps.Add(new ProgramFlowStep(programFlow));
                    break;
                case FlowStep { Flow: var flow } when wiredSplits.TryGetValue(flow, out var programSplit):
                    programSteps.Add(programSplit);
                    break;
                case FlowStep { Flow: var flow } when wiredMulticasts.TryGetValue(flow, out var programMulticast):
                    programSteps.Add(programMulticast);
                    break;
                case FlowStep { Flow: var flow } when wiredOleDbCommands.TryGetValue(flow, out var programOleDbCommand):
                    programSteps.Add(programOleDbCommand);
                    break;
                case FlowStep { Flow: var flow } when wiredScds.TryGetValue(flow, out var programScd):
                    programSteps.Add(programScd);
                    break;
                case SqlStep sqlStep:
                    programSteps.Add(ResolveSqlStep(package, sqlStep, targetServer, targetDatabase, secondaryConnections, files, testFiles, testCoverageNotes, ns, $"{ns}.Mapping", gaps, ReserveStatementIdentifier, willWire));
                    break;
                case ExpressionStep expressionStep:
                    // Fully-qualified rather than relying on a "using {ns}.Ssis;" in the
                    // generated package class -- an ExpressionTaskStep's assignment lambda lives
                    // directly on that class (see ExpressionTaskStep's own doc comment), which
                    // has no such using today, and this is a smaller, more isolated change than
                    // adding one purely for the rare case a control-flow assignment calls an
                    // SsisFn helper (Phase 6's own DATEADD("Millisecond",...) is the only real
                    // evidenced case so far).
                    programSteps.Add(new ProgramExpressionStep(
                        expressionStep.TaskName, expressionStep.SsisVariableName,
                        expressionStep.CSharpValueExpression.Replace("SsisFn.", $"{ns}.Ssis.SsisFn.")));
                    break;
                case ScriptTaskStep { Task: var scriptTask }:
                {
                    // The class is emitted whatever else happens in this package, and the step
                    // is wired unconditionally -- unlike a flow, there is no gate it can fail.
                    // Its own gap (naming the seam) rides along in the EmitResult.
                    var scriptResult = ScriptTaskEmitter.Emit($"{ns}.ScriptTasks", scriptTask);
                    files.AddRange(scriptResult.Files);
                    gaps.AddRange(scriptResult.Gaps);
                    programSteps.Add(new ProgramScriptTaskStep(
                        scriptTask.ObjectName ?? scriptTask.RefId, ScriptTaskEmitter.ClassName(scriptTask)));
                    break;
                }
                case FileSystemStep fileSystemStep:
                    RegisterFileSystemActionPaths(package, fileSystemStep.Action, fileSourceEntries, gaps);
                    programSteps.Add(new ProgramFileSystemStep(fileSystemStep.TaskName, fileSystemStep.Action));
                    if (fileSystemStep.Action.SourceConnectionName is { } fstSourceKey) fileSystemTaskKeys.Add(fstSourceKey);
                    if (fileSystemStep.Action.DestinationConnectionName is { } fstDestKey) fileSystemTaskKeys.Add(fstDestKey);
                    // Gated on willWire -- this test's own generated code calls
                    // package.{safeTaskName}(...), a method that only exists once PackageClassEmitter
                    // actually runs (see ResolveSqlStep's own identical willWire gate above).
                    if (willWire && ComponentTestEmitter.EmitFileSystemTaskTest($"{package.ObjectName}.Tests", ns, fileSystemStep.TaskName, fileSystemStep.Action) is { } fstTest)
                    {
                        var fstTestFinal = fstTest with { RelativePath = $"{package.ObjectName}.Tests/{fstTest.RelativePath}" };
                        testFiles.Add(fstTestFinal);
                        NoteTest(testCoverageNotes, fstTestFinal, "FileSystemTask",
                            "runs against a real temp directory; asserts the destination file exists and its content.");
                    }
                    break;
                case ForEachFileLoopStep loopStep:
                {
                    var programLoop = ResolveForEachFileLoop(loopStep.Loop, files, $"{ns}.Mapping", fileSourceEntries, gaps, ReserveStatementIdentifier);
                    if (programLoop is not null) programSteps.Add(programLoop);
                    break;
                }
                case ForEachDataFlowLoopStep { Loop: var dataFlowLoop } when wiredDataFlowLoops.TryGetValue(dataFlowLoop, out var programDataFlowLoop):
                    programSteps.Add(programDataFlowLoop);
                    break;
                case ForLoopStep { Loop: var forLoop } when wiredForLoops.TryGetValue(forLoop, out var programForLoop):
                    programSteps.Add(programForLoop);
                    break;
            }

            // Carried across rather than re-derived, and applied here rather than in each case
            // above so no per-kind branch needs to know guards/flow-groups exist. A step whose own
            // gate failed contributes no program step at all, in which case there is nothing to
            // stamp -- the guard/flow-group is not silently lost either, since that flow already
            // reported its own gap.
            if (programSteps.Count == programStepsBefore + 1)
            {
                programSteps[^1] = programSteps[^1] with { FlowGroup = step.FlowGroup, ContainerPath = step.ContainerPath, Wave = step.Wave };
                if (step.Guard is { } guard)
                    programSteps[^1] = programSteps[^1] with
                    {
                        Guard = new ProgramStepGuard(guard.SsisExpression, guard.CSharpPredicate),
                    };
            }
        }

        // Unconditional, mirroring ResolveSqlStep's own treatment of a post-flow/secondary-
        // connection statement exactly -- the STATEMENT CLASS and its class-independent
        // statement-text test are real, generatable output regardless of whether this package
        // ever wires a flow (see the "nothing wired" branch below, which needs these to already
        // exist). Only the "which ones got wired INTO Program.cs" list-building stays inside
        // willWire, since a pre-load statement is invoked inline in Program.cs's own try block --
        // there is no Program.cs at all to invoke it from when nothing wires.
        var preLoadStatements = new List<ProgramPreLoadStatement>();
        foreach (var stmt in plan.PreLoadStatements)
        {
            // ReserveStatementIdentifier (see its own doc comment above) -- not a bare
            // SanitizeIdentifier(stmt.TaskName), since two pre-load statements (or a pre-load
            // and a post-flow/secondary-connection one) can share the identical display name.
            var identifierBase = ReserveStatementIdentifier(stmt.TaskName);
            var statementClassName = identifierBase + "Statement";
            Merge(files, gaps, SqlStatementBuilderEmitter.Emit($"{ns}.Mapping", stmt.TaskName, identifierBase, stmt.Sql));
            var preLoadStatementTest = SqlStatementTestEmitter.Emit($"{package.ObjectName}.Tests", $"{ns}.Mapping", stmt.TaskName, identifierBase, stmt.Sql);
            var preLoadStatementTestFinal = preLoadStatementTest with { RelativePath = $"{package.ObjectName}.Tests/{preLoadStatementTest.RelativePath}" };
            testFiles.Add(preLoadStatementTestFinal);
            NoteTest(testCoverageNotes, preLoadStatementTestFinal, "StatementText",
                "asserts the built SQL text only; never executes it.");
            preLoadStatements.Add(new ProgramPreLoadStatement(stmt.TaskName, statementClassName));
        }

        IReadOnlyList<ComponentMethodEntry> methodInventory = [];

        if (willWire)
        {
            var programRequest = new ProgramRequest(
                PackageName: package.ObjectName,
                RootNamespace: ns,
                DbContextTypeName: $"{package.ObjectName}DbContext",
                PreLoadStatements: preLoadStatements,
                PreLoadFileActions: plan.PreLoadFileActions,
                Steps: programSteps)
            {
                VariableSeeds = plan.VariableSeeds,
                FailureHandlers = plan.FailureHandlers,
                IncludeNotifications = includeNotifications,
            };
            Merge(files, gaps, ProgramEmitter.Emit(programRequest));
            Merge(files, gaps, PackageClassEmitter.Emit(programRequest, out methodInventory, out var supportsFakeHappyPath, out var usesLookupPreload));

            // Foundation phase of the generated-tests plan (Docs/Generated-Tests-Plan.md):
            // FakeUnitOfWork/RecordingNotifier/PackageHarness, emitted per package (the same
            // self-contained, disposable pattern SsisFnEmitter already uses for Ssis/SsisFn.cs),
            // plus the one starter test that's always emittable regardless of this package's own
            // shape -- see RunAsyncFailureTestEmitter's own doc comment for why GetBindTokenAsync
            // specifically is the universal fault-injection point.
            // 2026-09-06: every file-based source's own starter READ test now points at the SAME
            // package-relative TestData/ folder appsettings.Development.json already overrides
            // FileSource:Files:<Key>:SourceFolder to (see TestDoublesEmitter's own doc comment) --
            // there is no separate, tool-synthesized Tier-A fallback the test harness reads
            // instead. A synthetic reference sample is still written to SampleData/ below (a
            // schema-correct STARTING POINT for the LOCAL-DATA fill, the same role svk sampledata
            // already plays for a package with no ssisx-generate-produced reference at all) but
            // nothing at runtime -- production or test -- reads it any more; only TestData/<real
            // file name>, filled in by a human/AI, makes the read test in
            // ComponentTestEmitter.EmitFileSourceTest actually pass. This makes CSV/fixed-width
            // sources consistent with SQL (needs a real server) and Excel (needs a real workbook):
            // every file/database-backed source's own real read is now Integration-tagged and
            // gapped as LocalFileSourceData/needs-a-real-server until supplied, rather than one
            // shape (CSV/fixed-width) quietly passing on synthetic data while the others don't.
            //
            // A real bug, found running the Integration suite for real against a live database
            // (not by this project's own test suite): csvSampleCandidates used to only ever be
            // populated by the plain single-destination/ConditionalSplit/Multicast paths'
            // ResolveFlowSource calls -- a CSV/fixed-width source feeding a Lookup+Aggregate
            // composed flow (RBC_Demo_ETL's own DFT_LookupAndAggregate, once its Lookup preload
            // stopped failing against a real server) or a Merge Join side (DFT_SortAndMergeJoin)
            // got NO TestData/SampleData wiring and NO LocalFileSourceData gap at all -- so
            // PackageHarness's fake FileSourceOptions had no entry for it, and the generated
            // code's own File(key) call threw KeyNotFoundException the instant that flow's local
            // BuildRawSource/BuildLeft/BuildRight function actually ran. Fixed by threading
            // csvSampleCandidates into GenerateLookupFlow/GenerateLookupThenAggregateFlow/
            // GenerateOleDbCommandFlow's own ResolveFlowSource calls and into
            // ResolveMergeJoinSideSource (GenerateAggregateFlow needs no such fix -- it already
            // requires flow.OleDbSource, i.e. a SQL-only source, so its own ResolveFlowSource call
            // can never take the CSV branch). This closes the TestData/gap-reporting half of the
            // gap for every flow shape; it deliberately does NOT add a starter SOURCE TEST for
            // these nested sources, since none of them get their own top-level generated method
            // to test against (each is a local function inside the owning flow's one method, e.g.
            // AGG_ByRegion()'s own BuildRawSource()) -- that remains the allWiredSources loop's
            // own documented, separate limitation below.
            var sampleDataByKey = new Dictionary<string, SampleDataResult>();
            var testDataFileEntries = new List<(string Key, string FileName)>();
            foreach (var candidate in csvSampleCandidates)
            {
                // A fixed-width/RaggedRight source needs a genuinely different sample-file SHAPE
                // (padded fixed-position text, not comma-delimited) -- see SampleDataEmitter's own
                // "Source -- fixed-width" doc comment. Kept purely as a schema-correct reference
                // now -- see this block's own comment above -- so a failure to synthesize one
                // (unusual delimiter/format SampleDataEmitter doesn't model) no longer blocks
                // anything: the real TestData/ file name is resolved independently, below.
                var sample = FlatFileRuntimeShape.IsFixedWidthWithoutHeader(candidate.ConnectionManager.FlatFileFormat)
                    ? SampleDataEmitter.EmitFixedWidth(candidate.FileSourceKey, candidate.ConnectionManager)
                    : SampleDataEmitter.EmitCsv(candidate.FileSourceKey, candidate.ConnectionManager, candidate.SourceComponent);
                if (sample is not null)
                    sampleDataByKey[candidate.FileSourceKey] = sample;

                // The real file name a human/AI must supply under TestData/ -- the connection
                // manager's own declared file name (matching BuildFileSourceEntry's own
                // resolution, reused here rather than re-derived independently, so the two can
                // never disagree), NOT the Tier-A sample's own {key}.csv/.txt name.
                var realFileName = Path.GetFileName(candidate.ConnectionManager.Parsed?.FilePath);
                if (string.IsNullOrEmpty(realFileName)) realFileName = "TODO";
                testDataFileEntries.Add((candidate.FileSourceKey, realFileName));

                // "LocalFileSourceData" taxonomy row (Docs/Generated-Tests-Plan.md's own Tier B):
                // this is now the ONLY way this source's own Integration-tagged read test gets
                // real data to run against, not an optional upgrade over an already-passing test.
                gaps.Add(new GenerationGap(candidate.FileSourceKey,
                    $"'{candidate.FileSourceKey}' has no real data yet -- its own Integration-tagged read test needs a REALISTIC file matching this connection manager's own declared schema at `TestData/{realFileName}` (see the LOCAL-DATA work packet; a schema-correct but synthetic reference was written to `SampleData/{sample?.FileName ?? candidate.FileSourceKey}` as a starting point, not a substitute).",
                    IsBlocking: false, Kind: GapKind.LocalFileSourceData, EvidenceRefId: candidate.ConnectionManager.RefId));
            }
            foreach (var sample in sampleDataByKey.Values)
                testFiles.Add(sample.File with { RelativePath = $"{package.ObjectName}.Tests/{sample.File.RelativePath}" });

            // The Tier-A synthetic sample's own (Key, FileName) -- a SECOND, PARALLEL PackageHarness
            // wiring, distinct from testDataFileEntries above, used ONLY by the happy-path RunAsync
            // starter test (RunAsyncFailureTestEmitter's own "succeeds against fakes" test). That
            // test actually runs the WHOLE pipeline, including a real CSV/fixed-width read, so once
            // every source's real read moved to needing a LOCAL-DATA fill (2026-09-06), the
            // happy-path test would otherwise fail on a fresh generate with none supplied yet --
            // this fallback is what keeps it fakes-only-green while the isolated source-read test
            // (via testDataFileEntries) still correctly demands the real file. Deliberately does
            // NOT cover Excel (no Tier-A sample exists for it at all) -- harmless, since
            // PackageClassEmitter.SupportsFakeHappyPath already excludes any Excel-sourced package
            // from ever needing this fallback in the first place.
            var sampleDataFallbackEntries = sampleDataByKey.Select(kv => (kv.Key, kv.Value.FileName)).ToList();

            var className = PackageClassEmitter.ClassName(package.ObjectName);
            var dbContextTypeName = $"{package.ObjectName}DbContext";
            var sinkFileKeys = flatFileSinkCandidates.Select(c => c.Sink.FileSourceKey).Distinct().ToList();
            var distinctFileSystemTaskKeys = fileSystemTaskKeys.Distinct().ToList();
            var forEachLoopKeys = programSteps.OfType<ProgramForEachFileLoopStep>().Select(s => s.FileSourceKey).Distinct().ToList();
            // An Excel source's own ExcelSourceOptions resolves File(key) EAGERLY at construction
            // time (unlike Sql/Csv/FixedWidth's own lazily-opened options), so even the ".Name"-only
            // starter test needs SOME registered entry -- folded into the same testDataFileEntries
            // list as CSV/fixed-width now (there was never a Tier-A synthesizer for Excel -- no
            // .xlsx writer in this stack -- so this was always the ONLY way its own Integration-
            // tagged read test could ever get real data to run against).
            var excelFlowSources = wiredFlows.Values.Select(f => f.Source)
                .Concat(wiredMulticasts.Values.Select(m => m.Source))
                .Concat(wiredOleDbCommands.Values.Select(c => c.Source))
                .OfType<ExcelFlowSource>().DistinctBy(s => s.FileSourceKey).ToList();
            foreach (var excel in excelFlowSources)
            {
                var connectionManager = fileSourceEntries.FirstOrDefault(e => e.Key == excel.FileSourceKey);
                var realFileName = connectionManager?.SourceFileName;
                if (string.IsNullOrEmpty(realFileName)) realFileName = $"{excel.FileSourceKey}.xlsx";
                testDataFileEntries.Add((excel.FileSourceKey, realFileName));
                gaps.Add(new GenerationGap(excel.FileSourceKey,
                    $"'{excel.FileSourceKey}' (worksheet '{excel.WorksheetName}') has no Tier-A synthetic sample at all -- this tool has no .xlsx writer. A real workbook matching the declared schema under `TestData/{realFileName}` is the only way its own Integration-tagged read test can run (see the LOCAL-DATA work packet).",
                    IsBlocking: false, Kind: GapKind.LocalFileSourceData, EvidenceRefId: null));
            }
            // An XML Source has EAGER FileSourceOptions resolution too (XmlRowSource<TRow>'s own
            // constructor takes the file path directly, same File(key) eagerness Excel's own
            // ExcelSourceOptions already established) and, like Excel, no Tier-A synthesizer at
            // all (no .xml writer either) -- same reasoning, same "no fallback exists" gap.
            var xmlFlowSources = wiredFlows.Values.Select(f => f.Source)
                .Concat(wiredMulticasts.Values.Select(m => m.Source))
                .Concat(wiredOleDbCommands.Values.Select(c => c.Source))
                .OfType<XmlFlowSource>().DistinctBy(s => s.FileSourceKey).ToList();
            foreach (var xmlFlow in xmlFlowSources)
            {
                var connectionManager = fileSourceEntries.FirstOrDefault(e => e.Key == xmlFlow.FileSourceKey);
                var realFileName = connectionManager?.SourceFileName;
                if (string.IsNullOrEmpty(realFileName)) realFileName = $"{xmlFlow.FileSourceKey}.xml";
                testDataFileEntries.Add((xmlFlow.FileSourceKey, realFileName));
                gaps.Add(new GenerationGap(xmlFlow.FileSourceKey,
                    $"'{xmlFlow.FileSourceKey}' (row element '{xmlFlow.RowElementName}') has no Tier-A synthetic sample at all -- this tool has no .xml writer. A real XML file matching the declared schema under `TestData/{realFileName}` is the only way its own Integration-tagged read test can run (see the LOCAL-DATA work packet).",
                    IsBlocking: false, Kind: GapKind.LocalFileSourceData, EvidenceRefId: null));
            }
            foreach (var f in TestDoublesEmitter.Emit(ns, className, dbContextTypeName, testDataFileEntries, sinkFileKeys, distinctFileSystemTaskKeys, forEachLoopKeys, secondaryConnections.Keys.ToList(), sampleDataFallbackEntries).Files)
                testFiles.Add(f with { RelativePath = $"{package.ObjectName}.Tests/{f.RelativePath}" });
            var runAsyncTest = RunAsyncFailureTestEmitter.Emit(ns, className, plan.FailureHandlers.Count > 0, supportsFakeHappyPath, usesLookupPreload, includeNotifications);
            var runAsyncTestFinal = runAsyncTest with { RelativePath = $"{package.ObjectName}.Tests/{runAsyncTest.RelativePath}" };
            testFiles.Add(runAsyncTestFinal);
            NoteTest(testCoverageNotes, runAsyncTestFinal, "RunAsync",
                "asserts the failure path (a fake throws on a chosen step -> rollback, failure handlers ran, notifier saw failure) and, when every source is file-based, the happy path (step count/order, per-branch transactions); does not vary input data beyond the fixed sample/rows.");

            // Sink -- SQL (Phase 2 of the generated-tests plan): one starter test per generated
            // sink method, matched back to its own destination component via sqlSinkEntities
            // (populated above, keyed by entity name) and the sink method's own real generated
            // NAME, which only methodInventory (just returned by PackageClassEmitter.Emit)
            // actually knows -- see ComponentMethodEntry's own doc comment for why this can't be
            // re-derived a second time. Joined via EntityName, not SsisName (SsisName is now the
            // sink's own real SSIS component name, e.g. "OLEDST_CustomerEnriched" -- see the 1-to-1
            // component-to-function mapping round). A Flat File Destination's own entity is never
            // in sqlSinkEntities, so this naturally skips it -- that destination type's own
            // write-format starter test is a separate, not-yet-built taxonomy row. An
            // error-redirect entity is skipped too (redirectSinkEntityNames) -- its own real sink
            // is a RedirectingSqlSink, which writes row-by-row via uow.ExecuteSqlAsync and never
            // touches uow.BulkInserts at all, so EmitSqlSinkTest's plain-SqlBulkSink assertion
            // would always fail for it (found for real, 2026-09, generating and RUNNING the whole
            // RBC_Demo_ETL portfolio's own Package.dtsx/OLEDST_StagingCustomers) -- RedirectingSqlSink's
            // own row-by-row/redirect behaviour is unit-tested directly in Etl.Core.Tests, not
            // duplicated here.
            foreach (var sinkEntry in methodInventory.Where(m => m.Kind == "Sink"))
            {
                if (sinkEntry.EntityName is null || redirectSinkEntityNames.Contains(sinkEntry.EntityName)) continue;
                if (!sqlSinkEntities.TryGetValue(sinkEntry.EntityName, out var destinationComponent)) continue;
                var sinkTest = ComponentTestEmitter.EmitSqlSinkTest(
                    $"{package.ObjectName}.Tests", ns, $"{ns}.Model", sinkEntry.EntityName, sinkEntry.MethodName, destinationComponent);
                foreach (var f in sinkTest.Files)
                {
                    var sinkTestFinal = f with { RelativePath = $"{package.ObjectName}.Tests/{f.RelativePath}" };
                    testFiles.Add(sinkTestFinal);
                    NoteTest(testCoverageNotes, sinkTestFinal, "Sink-Sql",
                        "writes 2 synthesized entities through the fake unit of work; asserts DestinationTable + ColumnMappings only, never a real INSERT.");
                }
            }

            // Sink -- flat file (Phase 2 of the generated-tests plan): same join-by-entity-name
            // shape as the SQL sink loop above, over flatFileSinkCandidates instead of
            // sqlSinkEntities -- the two are mutually exclusive per entity (a destination is
            // either SQL-sunk or flat-file-sunk, never both), so there is no naming collision
            // between the two starter tests despite both landing in "{Entity}SinkTests.cs".
            var flatFileSinkByEntity = flatFileSinkCandidates.ToDictionary(c => c.EntityName);
            foreach (var sinkEntry in methodInventory.Where(m => m.Kind == "Sink"))
            {
                if (sinkEntry.EntityName is null || !flatFileSinkByEntity.TryGetValue(sinkEntry.EntityName, out var candidate)) continue;
                var rowTerminator = candidate.Sink.Columns.Count > 0 ? candidate.Sink.Columns[^1].Delimiter : "";
                var sinkTest = ComponentTestEmitter.EmitFlatFileSinkTest(
                    $"{package.ObjectName}.Tests", ns, $"{ns}.Model", sinkEntry.EntityName, sinkEntry.MethodName,
                    candidate.Sink.FileSourceKey, candidate.Sink.HeaderLine is not null, rowTerminator, candidate.DestinationComponent);
                foreach (var f in sinkTest.Files)
                {
                    var sinkTestFinal = f with { RelativePath = $"{package.ObjectName}.Tests/{f.RelativePath}" };
                    testFiles.Add(sinkTestFinal);
                    NoteTest(testCoverageNotes, sinkTestFinal, "Sink-FlatFile",
                        "writes to a real temp path; asserts real widths/delimiters/padding against the actual file written.");
                }
            }

            // Source -- CSV / fixed-width / SQL / Excel (Phase 2 of the generated-tests plan; unified
            // 2026-09-06 -- see this method's own testDataFileEntries comment above): matched by ROW
            // TYPE NAME, never SsisName/ComponentName (a Flat File Source's own SSIS object name is
            // free text, routinely left at the schema default across every Data Flow Task in a
            // package, so two different flows' sources can and do share one -- caught for real
            // generating SyntheticParallelShapes.dtsx, where this originally compiled a starter test
            // against the WRONG row type), against every wired flow's own already-resolved Source.
            // wiredSplits/wiredMulticasts/wiredOleDbCommands all carry the same (RowTypeName, Source)
            // shape as wiredFlows' own ProgramFlowSpec -- a real, previously-latent gap (caught
            // 2026-09-06 verifying the generated-tests plan against
            // SyntheticConditionalSplitRemerge.dtsx's own walkthrough) had wiredSplits missing from
            // this concat entirely, so a Conditional Split flow's own source method compiled fine but
            // got no starter test at all, unlike the structurally identical Multicast case. A source
            // nested inside a Merge Join/Union/Aggregate side is still deliberately NOT covered here
            // -- each of those flows' own Source is an inner side, not the flow's OUTERMOST one.
            var allWiredSources = wiredFlows.Values.Select(f => (f.RowTypeName, f.Source))
                .Concat(wiredSplits.Values.Select(s => (s.RowTypeName, s.Source)))
                .Concat(wiredMulticasts.Values.Select(m => (m.RowTypeName, m.Source)))
                .Concat(wiredOleDbCommands.Values.Select(c => (c.RowTypeName, c.Source)))
                .ToList();
            foreach (var sourceEntry in methodInventory.Where(m => m.Kind == "Source"))
            {
                var (rowTypeName, source) = allWiredSources.FirstOrDefault(s => s.RowTypeName == sourceEntry.RowTypeName);
                if (rowTypeName is null) continue;

                if (source is CsvFlowSource or FixedWidthFlowSource)
                {
                    // A genuinely delimited/fixed-width Flat File Source is never in
                    // SourceNeedsUow's true set (see PackageClassEmitter.SourceNeedsUow), so this
                    // always calls the source method with no uow argument.
                    var fileSourceTest = ComponentTestEmitter.EmitFileSourceTest(
                        $"{package.ObjectName}.Tests", ns, $"{ns}.Csv", rowTypeName,
                        sourceEntry.MethodName, sourceNeedsUow: false, source.ComponentName);
                    var fileSourceTestFinal = fileSourceTest with { RelativePath = $"{package.ObjectName}.Tests/{fileSourceTest.RelativePath}" };
                    testFiles.Add(fileSourceTestFinal);
                    NoteTest(testCoverageNotes, fileSourceTestFinal, "Source-File",
                        "enumerates ReadAsync over the emitted Tier-A sample file; asserts row count and the first row's columns. A real-data Integration read needs its own LOCAL-DATA fill.");
                }
                else if (source is SqlFlowSource sqlSource)
                {
                    var sqlSourceTest = ComponentTestEmitter.EmitSqlSourceTest(
                        $"{package.ObjectName}.Tests", ns, $"{ns}.Sql", rowTypeName, sourceEntry.MethodName, source.ComponentName,
                        isSecondaryConnection: sqlSource.SecondaryConnectionManagerName is not null);
                    var sqlSourceTestFinal = sqlSourceTest with { RelativePath = $"{package.ObjectName}.Tests/{sqlSourceTest.RelativePath}" };
                    testFiles.Add(sqlSourceTestFinal);
                    NoteTest(testCoverageNotes, sqlSourceTestFinal, "Source-Sql",
                        "asserts `.Name` only by default; the real read is `[Trait(\"Category\",\"Integration\")]`, run only against a reachable server.");
                }
                else if (source is ExcelFlowSource)
                {
                    var excelSourceTest = ComponentTestEmitter.EmitExcelSourceTest(
                        $"{package.ObjectName}.Tests", ns, $"{ns}.Excel", rowTypeName, sourceEntry.MethodName, source.ComponentName);
                    var excelSourceTestFinal = excelSourceTest with { RelativePath = $"{package.ObjectName}.Tests/{excelSourceTest.RelativePath}" };
                    testFiles.Add(excelSourceTestFinal);
                    NoteTest(testCoverageNotes, excelSourceTestFinal, "Source-Excel",
                        "asserts `.Name` only; the real read is Integration-tagged (no .xlsx writer in this stack, needs a LOCAL-DATA fill).");
                }
                else if (source is XmlFlowSource)
                {
                    // XmlRowSource<TRow>'s own constructor is plain field assignment, exactly like
                    // CsvRowSource<TRow>/FixedWidthRowSource<TRow> (lazy -- no file I/O until
                    // ReadAsync, unlike ExcelRowSource<TRow>'s eager one) and it is never in
                    // PackageClassEmitter.SourceNeedsUow's own true set, so EmitFileSourceTest's
                    // existing shape (sourceNeedsUow: false) is reused verbatim rather than adding
                    // a fourth, near-identical emitter method.
                    var xmlSourceTest = ComponentTestEmitter.EmitFileSourceTest(
                        $"{package.ObjectName}.Tests", ns, $"{ns}.Xml", rowTypeName,
                        sourceEntry.MethodName, sourceNeedsUow: false, source.ComponentName);
                    var xmlSourceTestFinal = xmlSourceTest with { RelativePath = $"{package.ObjectName}.Tests/{xmlSourceTest.RelativePath}" };
                    testFiles.Add(xmlSourceTestFinal);
                    NoteTest(testCoverageNotes, xmlSourceTestFinal, "Source-Xml",
                        "asserts `.Name` only; the real read is Integration-tagged (no .xml writer in this stack, needs a LOCAL-DATA fill).");
                }
            }

            // Sequence container (Phase 2 of the generated-tests plan): one starter test per
            // generated container method, its direct children reconstructed from programSteps'
            // own ContainerPath (PackagePlanner.WalkContainer stamps it; a Sequence Container never
            // appears as its own PackageStep entry -- only its children do, see that method's own
            // doc comment) rather than re-walking the .dtsx executable tree a second time. Matched
            // to the container's own reserved MethodName (never SsisName -- see
            // ComponentMethodEntry's own doc comment for why a bare name can collide) via
            // methodInventory's "Container" entries, populated by PackageClassEmitter.Emit just
            // above.
            foreach (var containerEntry in methodInventory.Where(m => m.Kind == "Container"))
            {
                var hasDeeperNesting = programSteps.Any(s =>
                    s.ContainerPath.Contains(containerEntry.SsisName) && s.ContainerPath[^1] != containerEntry.SsisName);
                if (hasDeeperNesting) continue; // a nested sub-container -- out of this pilot's scope, see EmitSequenceContainerTest's own doc comment

                var directChildren = programSteps
                    .Where(s => s.ContainerPath.Count > 0 && s.ContainerPath[^1] == containerEntry.SsisName)
                    .ToList();
                var containerTest = ComponentTestEmitter.EmitSequenceContainerTest(
                    $"{package.ObjectName}.Tests", ns, containerEntry.MethodName, directChildren);
                if (containerTest is not null)
                {
                    var containerTestFinal = containerTest with { RelativePath = $"{package.ObjectName}.Tests/{containerTest.RelativePath}" };
                    testFiles.Add(containerTestFinal);
                    NoteTest(testCoverageNotes, containerTestFinal, "Container",
                        "asserts direct children ran, in order, via the fake's own recorders/file effects.");
                }
            }

            // OLE DB Command (Phase 2 of the generated-tests plan): one starter test per resolved
            // flow, using oleDbCommandTestCandidates (populated by GenerateOleDbCommandFlow itself,
            // above) rather than methodInventory -- this test deliberately bypasses the generated
            // source method entirely (see EmitOleDbCommandTest's own doc comment for why), so it
            // never needs the source method's own reserved NAME the way every other component test
            // in this file does.
            foreach (var candidate in oleDbCommandTestCandidates)
            {
                var cmdTest = ComponentTestEmitter.EmitOleDbCommandTest(
                    $"{package.ObjectName}.Tests", ns, candidate.RowTypeNamespace, candidate.RowTypeName,
                    candidate.StepName, candidate.SqlTemplate, candidate.ParameterColumnNames, candidate.SourceColumns);
                if (cmdTest is not null)
                {
                    var cmdTestFinal = cmdTest with { RelativePath = $"{package.ObjectName}.Tests/{cmdTest.RelativePath}" };
                    testFiles.Add(cmdTestFinal);
                    NoteTest(testCoverageNotes, cmdTestFinal, "OleDbCommand",
                        "asserts uow.ExecutedParameterizedSql -- the template text plus one parameter array per source row.");
                }
            }

            // ForEach loop (Phase 2 of the generated-tests plan): one starter test per
            // ProgramForEachFileLoopStep -- everything the test needs (StepName, FileSourceKey,
            // FileSpec, NameMode, StatementClassName) already lives on that fully-resolved step
            // record, no separate candidate list needed the way OLE DB Command's own does.
            foreach (var loopStep in programSteps.OfType<ProgramForEachFileLoopStep>())
            {
                var loopTest = ComponentTestEmitter.EmitForEachLoopTest(
                    $"{package.ObjectName}.Tests", ns, loopStep.StepName, loopStep.FileSourceKey, loopStep.FileSpec, loopStep.NameMode, loopStep.StatementClassName);
                if (loopTest is not null)
                {
                    var loopTestFinal = loopTest with { RelativePath = $"{package.ObjectName}.Tests/{loopTest.RelativePath}" };
                    testFiles.Add(loopTestFinal);
                    NoteTest(testCoverageNotes, loopTestFinal, "ForEachLoop",
                        "runs against a temp directory with 2 files; asserts 2 substituted statements in ExecutedSql, in ordinal file order.");
                }
            }

            // Multicast (Phase 2 of the generated-tests plan): one starter test per resolved flow,
            // using multicastTestCandidates (populated by GenerateMulticastFlow itself, above) --
            // same "bypasses the flow's own generated source/sink methods entirely" reasoning
            // EmitOleDbCommandTest already established.
            foreach (var candidate in multicastTestCandidates)
            {
                var mcastTest = ComponentTestEmitter.EmitMulticastTest(
                    $"{package.ObjectName}.Tests", ns, candidate.RowTypeNamespace, candidate.RowTypeName,
                    candidate.StepName, candidate.SourceColumns, candidate.Branches);
                if (mcastTest is not null)
                {
                    var mcastTestFinal = mcastTest with { RelativePath = $"{package.ObjectName}.Tests/{mcastTest.RelativePath}" };
                    testFiles.Add(mcastTestFinal);
                    NoteTest(testCoverageNotes, mcastTestFinal, "Multicast",
                        "asserts every branch received every source row.");
                }
            }

            // Aggregate source (Phase 2 of the generated-tests plan): one starter test per resolved
            // Aggregate flow, using aggregateSourceTestCandidates (populated by
            // GenerateAggregateFlow itself, above).
            foreach (var candidate in aggregateSourceTestCandidates)
            {
                var aggTest = ComponentTestEmitter.EmitAggregateSourceTest(
                    $"{package.ObjectName}.Tests", ns, candidate.SourceRowTypeNamespace, candidate.SourceRowTypeName,
                    candidate.AggregateRowTypeNamespace, candidate.AggregateRowTypeName, candidate.ComponentName,
                    candidate.GroupByPropertyName, candidate.GroupByKeyClrType, candidate.Functions);
                if (aggTest is not null)
                {
                    var aggTestFinal = aggTest with { RelativePath = $"{package.ObjectName}.Tests/{aggTest.RelativePath}" };
                    testFiles.Add(aggTestFinal);
                    NoteTest(testCoverageNotes, aggTestFinal, "Aggregate",
                        "synthesizes rows and asserts the grouped keys and counts.");
                }
            }

            // Data Flow Task -- direct invocation (added 2026-09-07, closing a real gap found
            // reviewing actual coverage data on two real packages): every OTHER starter test above
            // exercises one ISOLATED piece of a Data Flow Task -- nothing calls source, transform,
            // and sink TOGETHER except RunAsyncTests.cs's own happy-path test, which many real
            // packages never even get (a Lookup or an Excel source excludes the whole package from
            // PackageClassEmitter.SupportsFakeHappyPath entirely -- confirmed true for BOTH real
            // packages this was found against). One starter test per resolved single-destination
            // or Conditional-Split flow, reconstructing a fresh DataFlowStep/ConditionalSplitStep
            // from the same already-public source/transform/sink pieces every other starter test
            // in this file already calls -- see EmitDataFlowStepTest's own doc comment for why
            // this does NOT call the generated DFT_X(uow, ct) method directly (a real bug caught
            // building this: DFT_X reads a private _load field only RunAsync() populates). A flow
            // whose spec carries a Lookup preload (the original single-output shape, or the
            // composed Lookup+Aggregate shape -- both set ProgramFlowSpec.Lookup, see
            // GenerateLookupFlow/GenerateLookupThenAggregateFlow) is skipped with an honest,
            // non-blocking advisory instead: its own reference cache is a private field populated
            // only by RunAsync's own bootstrap, before any step runs, so NO construction reachable
            // from a test can populate it -- there is no fakes-only way to test this shape at all,
            // only a real Integration run against a real server exercises it. A Conditional Split
            // flow is never Lookup-dependent (the Lookup gate in this method's own per-flow
            // dispatch runs BEFORE ConditionalSplit is ever resolved), so wiredSplits needs no such
            // check.
            var sourceMethodByRowType = methodInventory.Where(m => m.Kind == "Source" && m.RowTypeName is not null)
                .GroupBy(m => m.RowTypeName!).ToDictionary(g => g.Key, g => g.First().MethodName, StringComparer.Ordinal);
            var sinkMethodByEntity = methodInventory.Where(m => m.Kind == "Sink" && m.EntityName is not null)
                .GroupBy(m => m.EntityName!).ToDictionary(g => g.Key, g => g.First().MethodName, StringComparer.Ordinal);
            foreach (var (flow, spec) in wiredFlows)
            {
                if (!sourceMethodByRowType.TryGetValue(spec.RowTypeName, out var sourceMethodName)) continue;
                if (spec.Lookup is not null)
                {
                    if (!skipTests)
                        gaps.Add(new GenerationGap($"{flow.TaskName}.DataFlowTest",
                            $"no direct-invocation starter test generated for '{flow.TaskName}' -- its own source reads a Lookup reference cache populated only by RunAsync's own bootstrap (before any step runs), which no test-reachable construction can populate; verify manually or via an Integration run against a real, reachable server.",
                            IsBlocking: false));
                    continue;
                }
                // A Flat File-sunk flow's own sink never populates uow.BulkInserts (it writes a
                // real file, via IUnitOfWork not at all -- see FlatFileBulkSink's own doc
                // comment), so this test's own assertion would never be meaningful for one; a
                // separate, not-yet-built taxonomy shape, not a silent gap.
                if (spec.Sink is not SqlFlowSink) continue;
                if (!sinkMethodByEntity.TryGetValue(spec.EntityName, out var sinkMethodName)) continue;
                var needsIntegration = RequiresIntegrationForDirectInvocation(spec.Source);
                var dataFlowTest = ComponentTestEmitter.EmitDataFlowStepTest(
                    $"{package.ObjectName}.Tests", ns, flow.TaskName,
                    sourceMethodName, FlowSourceNeedsUow(spec.Source), RowTypeNamespaceFor(spec.Source, ns), spec.RowTypeName,
                    spec.TransformClassName, sinkMethodName, $"{ns}.Model", spec.EntityName,
                    needsIntegration);
                var dataFlowTestFinal = dataFlowTest with { RelativePath = $"{package.ObjectName}.Tests/{dataFlowTest.RelativePath}" };
                testFiles.Add(dataFlowTestFinal);
                NoteTest(testCoverageNotes, dataFlowTestFinal, "DataFlowTask",
                    needsIntegration
                        ? "reconstructs the flow's real source/transform/sink and asserts at least one row was bulk-inserted; needs real connectivity (a SQL/Excel source somewhere in the flow), so this is Integration-tagged."
                        : "reconstructs the flow's real source/transform/sink (Tier-A sample data) and asserts at least one row was bulk-inserted -- proves the whole pipeline composes correctly, not just its isolated pieces.");
            }
            foreach (var (flow, spec) in wiredSplits)
            {
                if (!sourceMethodByRowType.TryGetValue(spec.RowTypeName, out var sourceMethodName)) continue;
                var needsIntegration = RequiresIntegrationForDirectInvocation(spec.Source);
                var branches = spec.Branches.Select(b => (b.OutputName, b.TransformClassName, b.EntityName)).ToList();
                var dataFlowTest = ComponentTestEmitter.EmitConditionalSplitDataFlowTest(
                    $"{package.ObjectName}.Tests", ns, flow.TaskName,
                    sourceMethodName, FlowSourceNeedsUow(spec.Source), RowTypeNamespaceFor(spec.Source, ns), spec.RowTypeName,
                    spec.RouterClassName, branches, $"{ns}.Model", needsIntegration);
                var dataFlowTestFinal = dataFlowTest with { RelativePath = $"{package.ObjectName}.Tests/{dataFlowTest.RelativePath}" };
                testFiles.Add(dataFlowTestFinal);
                NoteTest(testCoverageNotes, dataFlowTestFinal, "DataFlowTask",
                    needsIntegration
                        ? "reconstructs the flow's real source/router/branches and asserts at least one row was bulk-inserted; needs real connectivity (a SQL/Excel source somewhere in the flow), so this is Integration-tagged."
                        : "reconstructs the flow's real source/router/branches (Tier-A sample data) and asserts at least one row was bulk-inserted across at least one branch -- proves the whole pipeline composes correctly, not just its isolated pieces.");
            }

            if (authMode is null)
            {
                gaps.Add(new GenerationGap(package.ObjectName, "could not resolve the target connection manager's auth mode -- defaulting to Windows auth in the generated appsettings.json; verify manually"));
                authMode = "Windows";
            }

            Merge(files, gaps, ProjectEmitter.Emit(new ProjectRequest(
                PackageName: package.ObjectName,
                FileSourceEntries: fileSourceEntries,
                DatabaseAuth: new DatabaseAuthRequest(authMode, userId),
                OnSuccessRecipients: [],
                OnFailureRecipients: [],
                SecondaryConnections: secondaryConnections.Values.ToList())
            {
                IncludeNotifications = includeNotifications,
            }));

            // Reported either way, wording tied to what was actually generated -- with
            // --notifications, IPackageResultNotifier/AddEmailNotifications are wired but
            // recipients are empty (the .dtsx has no notion of them); without it (the default),
            // no notification code was generated at all, and this gap is how a human finds out
            // that's an opt-in, not an oversight.
            gaps.Add(new GenerationGap($"{package.ObjectName}.Notification",
                includeNotifications
                    ? "a .dtsx carries no notification-recipient information -- OnSuccessRecipients/OnFailureRecipients were generated empty; fill in appsettings.json manually"
                    : "no notification wiring was generated (IPackageResultNotifier/AddEmailNotifications) -- a .dtsx carries no notification-recipient information at all, so this is opt-in; re-run with --notifications once real recipients/SMTP settings exist",
                IsBlocking: false));
        }
        else if (testFiles.Count > 0)
        {
            // Found running this generator against a real third-party portfolio (sql-server-
            // samples' own DailyETLMain.dtsx): 0 flows wired anywhere in this package (every one
            // independently blocked on an unrelated Tier-3 gap), yet real, correctly-generated
            // standalone Execute SQL Task statement classes exist (pre-load/post-flow/secondary-
            // connection tasks are all resolved unconditionally, above) -- with no ProjectEmitter/
            // PackageClassEmitter output, they were orphaned loose .cs files with no project at
            // all to build them, main or test. A minimal, dependency-free main project (no
            // Program.cs, no appsettings -- nothing reads them without an EtlHost) gives them
            // somewhere real to live, so this genuinely-correct output stops being unusable.
            files.Add(EmitMinimalCsproj(package.ObjectName));
        }

        // --skip-tests (Docs/Generated-Tests-Plan.md's own "Opt-out"): discard every starter
        // test computed above, right before anything downstream (the README, the final
        // result) would otherwise describe or ship them -- a single clean cut point rather
        // than guarding dozens of individual testFiles.Add(...) call sites throughout this
        // method, which would be far more invasive for the same outcome. TestOracle/
        // LocalFileSourceData gaps are dropped alongside the tests they exist FOR: with no
        // generated test at all, there is nothing for either to be an oracle/sample-data
        // answer to. appsettings.Development.json (ProjectEmitter, above) is deliberately
        // UNCHANGED -- it also serves a real, non-test `dotnet run --environment Development`,
        // which --skip-tests has no opinion about. Unconditional (not nested in willWire) since
        // the "nothing wired" branch above can produce real starter tests too.
        if (skipTests)
        {
            testFiles.Clear();
            testCoverageNotes.Clear();
            gaps.RemoveAll(g => g.Kind is GapKind.TestOracle or GapKind.LocalFileSourceData);
        }

        // README.md -- the primary token-saving artifact (Docs/Generated-Tests-Plan.md): every
        // generated method's own full signature, which control-flow task produced it, and
        // where its test/gap (if any) live, so a reader/assistant never has to re-derive that
        // by reading {Package}.cs top to bottom. Built AFTER every gap for this package (the
        // Notification one included) so it shows the complete picture. Unconditional -- even a
        // "nothing wired" package's own standalone SQL statements deserve a README describing
        // them, the same as any other package's.
        var readmeRequest = new ReadmeRequest(
            PackageName: package.ObjectName,
            Steps: programSteps,
            MethodInventory: methodInventory,
            Flows: plan.Flows.Select(f => new ReadmeFlowSection(f.TaskName, f.Pipeline)).ToList(),
            TestFiles: testFiles,
            TestCoverageNotes: testCoverageNotes,
            Gaps: gaps);
        files.Add(PackageReadmeEmitter.Emit(readmeRequest));

        // The test PROJECT file itself is only worth emitting once at least one starter
        // test class exists to put in it -- an empty xUnit project is not useful output.
        if (testFiles.Count > 0)
            testFiles.Add(TestProjectEmitter.Emit(package.ObjectName));

        return new PackageGenerateResult(package.ObjectName, files, gaps, targetServer, targetDatabase) { SiblingFiles = testFiles };
    }

    private static void Merge(List<GeneratedFile> files, List<GenerationGap> gaps, EmitResult result)
    {
        files.AddRange(result.Files);
        gaps.AddRange(result.Gaps);
    }

    /// <summary>The "nothing wired, but real standalone Execute SQL Task statement classes
    /// exist" case's own main project -- deliberately NOT <see cref="ProjectEmitter"/>'s usual
    /// <c>.csproj</c> (that one assumes a runnable <c>Program.cs</c> exists: <c>OutputType=Exe</c>,
    /// appsettings.json/appsettings.Development.json, an Etl.Core reference -- none of which
    /// apply here, since a plain <c>SqlStatementBuilderEmitter</c>-produced class has zero
    /// dependencies beyond <c>System</c>). A bare SDK-style library project, referencing
    /// nothing, exists purely so <c>TestProjectEmitter</c>'s own hard-coded
    /// <c>&lt;ProjectReference Include="..\{Package}\{Package}.csproj" /&gt;</c> resolves to a
    /// real project instead of a dangling path.</summary>
    private static GeneratedFile EmitMinimalCsproj(string packageName)
    {
        var lines = new List<string>
        {
            "<Project Sdk=\"Microsoft.NET.Sdk\">",
            "",
            "  <ItemGroup>",
            $"    <InternalsVisibleTo Include=\"{packageName}.Tests\" />",
            "  </ItemGroup>",
            "",
            "</Project>",
        };

        return new GeneratedFile($"{packageName}.csproj", Rendering.JoinLines(lines));
    }

    /// <summary>Records a <see cref="TestCoverageNote"/> for <paramref name="file"/>, keyed by its
    /// own (already package-prefixed) RelativePath -- the exact join key PackageReadmeEmitter uses,
    /// see TestCoverageNote's own doc comment for why RelativePath, not a method name, is what's
    /// reliable at every call site. A no-op for anything that isn't an actual xUnit test class
    /// (TestDoubles/*.cs support files, SampleData/*.csv, the .csproj itself) -- every one of those
    /// filenames provably does NOT end in "Tests.cs" (confirmed by reading every GeneratedFile
    /// construction site in this assembly), so this can be called unconditionally, right alongside
    /// every existing testFiles.Add(...), with no per-site filtering needed.</summary>
    /// <summary>True whenever any leaf source reachable from <paramref name="source"/> is
    /// SQL/Excel-typed -- neither has a synthetic Tier-A fallback, so a "Data Flow Task -- direct
    /// invocation" starter test built against it needs real connectivity and is tagged
    /// Integration, mirroring <c>SourceNeedsUow</c>'s own recursive shape immediately below in
    /// <c>PackageClassEmitter</c>.</summary>
    private static bool RequiresIntegrationForDirectInvocation(FlowSourceSpec source) => source switch
    {
        SqlFlowSource => true,
        ExcelFlowSource => true,
        XmlFlowSource => true,
        MergeJoinFlowSource mj => RequiresIntegrationForDirectInvocation(mj.LeftSource) || RequiresIntegrationForDirectInvocation(mj.RightSource),
        UnionFlowSource union => union.Sides.Any(RequiresIntegrationForDirectInvocation),
        AggregateFlowSource agg => RequiresIntegrationForDirectInvocation(agg.InnerSource),
        _ => false,
    };

    /// <summary>Mirrors <c>PackageClassEmitter.SourceNeedsUow</c>'s own recursive shape exactly --
    /// duplicated here (not shared) since that one is a local function private to
    /// <c>PackageClassEmitter.Emit</c>. Used by the "Data Flow Task -- direct invocation" starter
    /// test to decide whether its own reconstructed <c>package.{sourceMethodName}(...)</c> call
    /// needs a <c>uow</c> argument, exactly like the real generated call site does.</summary>
    private static bool FlowSourceNeedsUow(FlowSourceSpec source) => source switch
    {
        // A secondary-connection source never receives uow (see SqlFlowSource's own doc
        // comment and PackageClassEmitter.SourceNeedsUow's identical exclusion, added the same
        // round, 2026-09-17) -- omitted here first, this mirror silently drifted out of sync
        // with the real generated call site the instant a secondary-connection source became
        // possible, producing a starter test that called package.{sourceMethodName}(uow) against
        // a method that takes no arguments at all (CS1501), caught only by actually building the
        // generated project, not by the gap-count report.
        SqlFlowSource sql => sql.SecondaryConnectionManagerName is null,
        MergeJoinFlowSource mj => FlowSourceNeedsUow(mj.LeftSource) || FlowSourceNeedsUow(mj.RightSource),
        UnionFlowSource union => union.Sides.Any(FlowSourceNeedsUow),
        AggregateFlowSource agg => FlowSourceNeedsUow(agg.InnerSource),
        _ => false,
    };

    /// <summary>The namespace a flow's own combined ROW type lives in -- mirrors the identical
    /// ternary already used at the single-destination call site in this method
    /// (`programSource is CsvFlowSource or FixedWidthFlowSource ? ... : ...`), so this never
    /// disagrees with what the real row-type emitters actually did.</summary>
    private static string RowTypeNamespaceFor(FlowSourceSpec source, string ns) => source switch
    {
        CsvFlowSource or FixedWidthFlowSource => $"{ns}.Csv",
        ExcelFlowSource => $"{ns}.Excel",
        XmlFlowSource => $"{ns}.Xml",
        _ => $"{ns}.Sql",
    };

    private static void NoteTest(List<TestCoverageNote> testCoverageNotes, GeneratedFile file, string kind, string summary)
    {
        var name = Path.GetFileName(file.RelativePath);
        if (!name.EndsWith("Tests.cs", StringComparison.Ordinal)) return;
        var hint = name[..^"Tests.cs".Length];
        testCoverageNotes.Add(new TestCoverageNote(file.RelativePath, hint, kind, summary));
    }

    /// <summary>The destination's own OpenRowset table name is the one identifier in a .dtsx
    /// actually fit to become a C# type name -- "[dbo].[Employee]" -> "Employee". Deliberately
    /// duplicates DbContextEmitter's own ParseOpenRowset (a three-line gate) rather than
    /// sharing it: this call site needs only the table name, DbContextEmitter needs the schema
    /// too, and threading a shared "parsed OpenRowset" type through both for three lines isn't
    /// worth it. Takes the destination directly (not a whole DataFlowPlan) so a Conditional
    /// Split's per-branch destinations can reuse it too.</summary>
    private static string? TryResolveEntityName(string taskName, PipelineComponentSpec destinationComponent, List<GenerationGap> gaps)
    {
        var openRowset = DestinationInfo.TableName(destinationComponent);
        if (string.IsNullOrEmpty(openRowset))
        {
            gaps.Add(new GenerationGap(taskName, "destination has no OpenRowset -- cannot derive an entity name for a SQL-command destination"));
            return null;
        }

        var parts = openRowset.Split("].[", StringSplitOptions.None);
        return parts.Length == 2 ? parts[1].TrimEnd(']') : openRowset.Trim('[', ']');
    }

    private static ConnectionManagerSpec? FindConnectionManager(PackageSpec package, string name) =>
        package.ConnectionManagers.FirstOrDefault(cm => cm.ObjectName == name);

    /// <summary>"Lookup Customer" -> "LookupCustomer" -- a component's own display name is
    /// free text a designer author can type anything into (spaces, punctuation), unlike
    /// entityName (always derived from a table's OpenRowset, already alphanumeric). Strips
    /// everything but letters/digits/underscore and guards against a leading digit, which C#
    /// identifiers don't allow.
    ///
    /// Also used for a real, external SQL/pipeline COLUMN name (e.g. a real WWI schema column
    /// literally named "WWI Stock Item ID") wherever that name is about to be emitted in an
    /// IDENTIFIER position (a C# property/field/local name, or a <c>row.{X}</c>/
    /// <c>entity.Property(e => e.{X})</c> reference) -- found 2026-09-18 regenerating a real
    /// GitHub portfolio package (sql-server-samples' own DailyETLMain.dtsx) end to end: every
    /// such name had been used VERBATIM as a C# identifier across ~10 emitter files, producing a
    /// real ~1181-error CS1002/CS1003 parse failure the moment a real column name contained a
    /// space. A LITERAL string use (an ordinal lookup by name, an XML element name, raw SQL
    /// text) must never route through this -- only a declaration/reference site needs it.
    ///
    /// A C# reserved keyword (e.g. a column literally named "class") is escaped with a leading
    /// <c>@</c> rather than mangled -- not evidenced anywhere in the tracked corpus, but a
    /// general-purpose identifier sanitizer must not silently emit an invalid identifier for
    /// one. Checked AFTER the character-stripping/leading-digit guard, since <c>@0class</c> is
    /// not a real C# keyword and needs no escaping, but <c>@class</c> does.</summary>
    internal static string SanitizeIdentifier(string name)
    {
        var chars = name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray();
        var result = new string(chars);
        if (result.Length == 0) return "Component";
        if (char.IsDigit(result[0])) result = "_" + result;
        return ReservedCSharpKeywords.Contains(result) ? "@" + result : result;
    }

    /// <summary>Every C# reserved keyword (not a contextual one like <c>var</c>/<c>async</c>,
    /// which remain legal as a plain identifier) -- the exact set that needs an <c>@</c> escape
    /// to be used as an identifier at all.</summary>
    private static readonly HashSet<string> ReservedCSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
        "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw",
        "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
        "virtual", "void", "volatile", "while",
    };

    /// <summary>Reserve()-style collision guard for one row/entity's own declared property list,
    /// reused by every emitter that declares a whole class of properties from a raw column list
    /// in one shot (SqlRowEmitter, EntityEmitter, ExcelRowEmitter, XmlRowEmitter, ...) -- the
    /// exact same first-occurrence-keeps-its-name/later-collision-gets-a-numeric-suffix pattern
    /// PackageClassEmitter.cs's own method-naming <c>Reserve()</c> already uses, extended here to
    /// MEMOIZE by raw name (not just by desired identifier): a raw column referenced twice within
    /// the same list (e.g. once to decide a primary key, once in the main property loop) must
    /// resolve to the identical identifier both times, not consume a second suffix slot.
    ///
    /// This is a per-call-site closure, not a value threaded across files -- two SEPARATE row-
    /// declaring emitters that independently resolve the SAME PIPELINE OUTPUT (e.g.
    /// SqlRowEmitter's property declarations and SqlRowReaderEmitter's own read-back assignments)
    /// stay in agreement with NO shared state at all, because both iterate the identical
    /// <c>PipelineResolver.Resolve(...)</c> column list in the identical order and so independently
    /// reproduce the identical suffix sequence -- a pure function of the same input, called twice.
    /// The one HONEST, ACCEPTED limitation (unevidenced in any real corpus so far, not silently
    /// pretended away): a reference site that does NOT have the whole column list in hand (most of
    /// this project's <c>row.{X}</c>-emitting code, which resolves one column name at a time) calls
    /// bare <see cref="SanitizeIdentifier"/> instead, so it can disagree with a declaration-side
    /// suffix ONLY in the rare case where two genuinely different raw names collide to the same
    /// sanitized identifier.</summary>
    internal static Func<string, string> MakeColumnIdentifierResolver()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var resolvedByRaw = new Dictionary<string, string>(StringComparer.Ordinal);
        return raw =>
        {
            if (resolvedByRaw.TryGetValue(raw, out var existing)) return existing;
            var desired = SanitizeIdentifier(raw);
            var name = desired;
            var n = 2;
            while (!used.Add(name)) name = $"{desired}_{n++}";
            resolvedByRaw[raw] = name;
            return name;
        };
    }

    /// <summary>Evidence-based, not a fact lookup -- see NullabilityInference's own doc comment
    /// for the DerivedColumn half. Shared by the single-destination path and
    /// <see cref="GenerateConditionalSplitFlow"/> (a Conditional Split's own branches can
    /// reference a Data Conversion column exactly like a plain destination flow can -- e.g.
    /// RBC_Demo_ETL's CSPLIT_Validity referencing CustomerID_i4 -- so both paths need the same
    /// set). A Data Conversion output column is nullable BY CONSTRUCTION, not by ISNULL evidence
    /// the way NullabilityInference's other entries are -- IgnoreFailure (the only disposition
    /// this tool generates against, see DataConvertPayload's own doc comment) can always null
    /// out the value, regardless of whether anything downstream ever guards it. EntityEmitter/
    /// TransformEmitter/RouterEmitter all key off this same set by the destination's/split's own
    /// pipeline column name, so computing it once here is sufficient for all three.</summary>
    private static HashSet<string> ResolveNullableColumnNames(DataFlowPlan flow)
    {
        var nullableColumnNames = flow.DerivedColumn is null
            ? new HashSet<string>()
            : NullabilityInference.InferNullableColumnNames([flow.DerivedColumn]);

        if (flow.DataConversion?.DataConvert is { } dataConvert)
        {
            foreach (var col in dataConvert.Columns)
                nullableColumnNames.Add(col.OutputColumnName);

            // The RAW source column feeding each conversion is just as capable of being NULL in
            // the real data (confirmed empirically -- see DataConvertPayload's own doc comment,
            // row ID=5's NULL source columns). SqlRowReaderEmitter's own IsDBNull guard already
            // applies to a nullable-inferred STRING column (unlike EntityEmitter/SqlRowEmitter's
            // "string never needs a type change" exclusion -- see that emitter's own comment) --
            // it just needs these names in the same set. Without this, GetFieldValue<string>
            // throws SqlNullValueException on a real NULL row, caught by actually running the
            // generated code against seeded NULL data, not assumed.
            var flowLineage = LineageBuilder.Build(flow.Pipeline);
            foreach (var col in dataConvert.Columns)
            {
                var sourceEdge = flowLineage.Edges.FirstOrDefault(e => e.Kind == "DataConversion" && e.ToColumnName == col.OutputColumnName);
                if (sourceEdge is not null) nullableColumnNames.Add(sourceEdge.FromColumnName);
            }
        }

        // An XML Source's own string columns are nullable BY CONSTRUCTION, the same reasoning as
        // a Data Conversion column above -- measured via a real dtexec run (SyntheticXmlSource.dtsx):
        // both an empty XML element and an entirely OMITTED optional element resolve to a genuine
        // NULL, never an empty string (see XmlRowReaderEmitter's own doc comment). Only string
        // columns -- a numeric/date column's own missing-element behavior was never measured.
        if (flow.XmlSource is { } xmlSourceForNullability)
        {
            var xmlOutput = xmlSourceForNullability.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
            if (xmlOutput is not null)
            {
                foreach (var column in PipelineResolver.Resolve(xmlOutput).Columns)
                {
                    if (column.Type?.ClrTypeName == "string") nullableColumnNames.Add(column.PipelineColumnName);
                }
            }
        }

        // A string-source/int-destination passthrough column (SsisFn.ParseWstrToI4, 2026-08-30)
        // is nullable BY CONSTRUCTION, the same reasoning as a Data Conversion column above --
        // ParseWstrToI4 always returns int? (string and string? are the same runtime type, so
        // there is no non-nullable overload the way NarrowR8ToI4's double/double? pair has), so
        // the destination entity property must already be nullable by the time EntityEmitter
        // runs, not just inside TransformEmitter's own later mismatch detection.
        foreach (var col in TransformEmitter.DetectParsedWstrToI4Columns(flow.DestinationComponent))
            nullableColumnNames.Add(col);

        return nullableColumnNames;
    }

    /// <summary>Resolves a flow's source (Flat File or OLE DB) into a row-type file pair plus a
    /// <see cref="FlowSourceSpec"/> Program.cs can wire up -- shared by both the single-
    /// destination path and <see cref="GenerateConditionalSplitFlow"/>, since a Conditional
    /// Split reads from exactly the same kind of source a single-destination flow does; only
    /// what happens to each row downstream differs. <paramref name="rowTypeBaseName"/> is
    /// "{entityName}" for a single-destination flow and a sanitized flow/task name for a split
    /// (which has no single destination entity to key it off). <paramref name="destinationForSqlCheck"/>
    /// is the destination BuildSqlFlowSource compares an OLE DB Source's server/database
    /// against -- the flow's own destination, or a split's default branch's destination. Null
    /// when the flow has no SQL destination at all (an OLE DB Command flow, or a Multicast whose
    /// only branches are Flat File Destinations) -- BuildSqlFlowSource skips the check entirely
    /// in that case, the same reasoning already established for a Flat File Destination.</summary>
    private static (string RowTypeName, FlowSourceSpec Source)? ResolveFlowSource(
        PackageSpec package, string ns, string rowTypeBaseName, DataFlowPlan flow, PipelineComponentSpec? destinationForSqlCheck,
        List<FileSourceEntryRequest> fileSourceEntries, List<GeneratedFile> files, List<GenerationGap> gaps,
        IReadOnlySet<string>? nullableColumnNames = null, List<CsvSampleCandidate>? csvSampleCandidates = null,
        Dictionary<string, SecondaryConnectionRequest>? secondaryConnections = null)
    {
        if (flow.FlatFileSource is { } flatFileSource && flatFileSource.FlatFileSource?.ConnectionName is { } csvCmName
            && FindConnectionManager(package, csvCmName) is { } csvConnectionManager)
        {
            var rowTypeName = rowTypeBaseName + "CsvRow";
            var fileSourceKey = rowTypeBaseName;
            var format = csvConnectionManager.FlatFileFormat;

            Merge(files, gaps, CsvRowEmitter.Emit($"{ns}.Csv", rowTypeName, csvConnectionManager, flatFileSource));
            fileSourceEntries.Add(BuildFileSourceEntry(fileSourceKey, csvConnectionManager, gaps));

            if (FlatFileRuntimeShape.IsFixedWidthWithoutHeader(format))
            {
                Merge(files, gaps, FixedWidthRowReaderEmitter.Emit($"{ns}.Csv", rowTypeName, csvConnectionManager));
                // "Source -- fixed-width" taxonomy row (Docs/Generated-Tests-Plan.md): the SAME
                // CsvSampleCandidate list a delimited source registers below -- SampleDataEmitter's
                // own EmitCsv-vs-EmitFixedWidth dispatch (at the one place this list is consumed)
                // is what actually writes the right shape of sample file; ComponentTestEmitter's
                // own "Source -- CSV" test emitter needs no format-specific knowledge at all, it
                // only ever reads SampleDataColumn's own PropertyName/ExpectedLiteral.
                csvSampleCandidates?.Add(new CsvSampleCandidate(fileSourceKey, csvConnectionManager, rowTypeName, $"{ns}.Csv", flatFileSource.Name, flatFileSource));
                return (rowTypeName, new FixedWidthFlowSource(flatFileSource.Name, fileSourceKey,
                    FlatFileRuntimeShape.BuildColumnPlans(format!), format!.HeaderRowsToSkip ?? 0));
            }

            Merge(files, gaps, ClassMapEmitter.Emit($"{ns}.Csv", rowTypeName, csvConnectionManager));
            csvSampleCandidates?.Add(new CsvSampleCandidate(fileSourceKey, csvConnectionManager, rowTypeName, $"{ns}.Csv", flatFileSource.Name, flatFileSource));
            return (rowTypeName, new CsvFlowSource(flatFileSource.Name, fileSourceKey, format?.HeaderRowsToSkip ?? 0));
        }

        if (flow.OleDbSource is { } oleDbSource)
        {
            var sqlSource = BuildSqlFlowSource(package, flow.TaskName, oleDbSource, destinationForSqlCheck, gaps, secondaryConnections: secondaryConnections);
            if (sqlSource is null) return null; // BuildSqlFlowSource already added the reason

            var rowTypeName = rowTypeBaseName + "SqlRow";
            Merge(files, gaps, SqlRowEmitter.Emit($"{ns}.Sql", rowTypeName, oleDbSource, nullableColumnNames));
            Merge(files, gaps, SqlRowReaderEmitter.Emit($"{ns}.Sql", rowTypeName, oleDbSource, nullableColumnNames));

            return (rowTypeName, sqlSource);
        }

        if (flow.ExcelSource is { } excelSource)
        {
            var excelFlowSource = BuildExcelFlowSource(package, flow.TaskName, excelSource, fileSourceEntries, gaps);
            if (excelFlowSource is null) return null; // BuildExcelFlowSource already added the reason

            var rowTypeName = rowTypeBaseName + "ExcelRow";
            Merge(files, gaps, ExcelRowEmitter.Emit($"{ns}.Excel", rowTypeName, excelSource));
            Merge(files, gaps, ExcelRowReaderEmitter.Emit($"{ns}.Excel", rowTypeName, excelSource));

            return (rowTypeName, excelFlowSource);
        }

        if (flow.XmlSource is { } xmlSource)
        {
            var xmlFlowSource = BuildXmlFlowSource(flow.TaskName, xmlSource, fileSourceEntries, gaps);
            if (xmlFlowSource is null) return null; // BuildXmlFlowSource already added the reason

            var rowTypeName = rowTypeBaseName + "XmlRow";
            Merge(files, gaps, XmlRowEmitter.Emit($"{ns}.Xml", rowTypeName, xmlSource));
            Merge(files, gaps, XmlRowReaderEmitter.Emit($"{ns}.Xml", rowTypeName, xmlSource));

            return (rowTypeName, xmlFlowSource);
        }

        gaps.Add(new GenerationGap(flow.TaskName, "no Flat File Source, OLE DB Source, ADO NET Source, Excel Source, or XML Source (with a resolvable connection manager) found -- only [Flat File Source|OLE DB Source|ADO NET Source|Excel Source|XML Source] -> [Derived Column] -> [OLE DB Destination|ADO NET Destination] is supported yet"));
        return null;
    }

    /// <summary>Same per-side resolution as <see cref="ResolveFlowSource"/>'s own two branches,
    /// just keyed off a raw source <see cref="PipelineComponentSpec"/> directly (a Merge Join's
    /// own two sides are never stored on a <see cref="DataFlowPlan"/> itself, since a
    /// single-destination flow only ever has one).</summary>
    private static (string RowTypeName, FlowSourceSpec Source)? ResolveMergeJoinSideSource(
        PackageSpec package, string ns, string rowTypeBaseName, PipelineComponentSpec sourceComponent,
        PipelineComponentSpec destinationForSqlCheck, List<FileSourceEntryRequest> fileSourceEntries,
        List<GeneratedFile> files, List<GenerationGap> gaps, List<CsvSampleCandidate>? csvSampleCandidates = null)
    {
        if (sourceComponent.ComponentClassId == "Microsoft.FlatFileSource" && sourceComponent.FlatFileSource?.ConnectionName is { } csvCmName
            && FindConnectionManager(package, csvCmName) is { } csvConnectionManager)
        {
            var rowTypeName = rowTypeBaseName + "CsvRow";
            var fileSourceKey = rowTypeBaseName;
            var format = csvConnectionManager.FlatFileFormat;

            Merge(files, gaps, CsvRowEmitter.Emit($"{ns}.Csv", rowTypeName, csvConnectionManager, sourceComponent));
            fileSourceEntries.Add(BuildFileSourceEntry(fileSourceKey, csvConnectionManager, gaps));

            if (FlatFileRuntimeShape.IsFixedWidthWithoutHeader(format))
            {
                Merge(files, gaps, FixedWidthRowReaderEmitter.Emit($"{ns}.Csv", rowTypeName, csvConnectionManager));
                csvSampleCandidates?.Add(new CsvSampleCandidate(fileSourceKey, csvConnectionManager, rowTypeName, $"{ns}.Csv", sourceComponent.Name, sourceComponent));
                return (rowTypeName, new FixedWidthFlowSource(sourceComponent.Name, fileSourceKey,
                    FlatFileRuntimeShape.BuildColumnPlans(format!), format!.HeaderRowsToSkip ?? 0));
            }

            Merge(files, gaps, ClassMapEmitter.Emit($"{ns}.Csv", rowTypeName, csvConnectionManager));
            csvSampleCandidates?.Add(new CsvSampleCandidate(fileSourceKey, csvConnectionManager, rowTypeName, $"{ns}.Csv", sourceComponent.Name, sourceComponent));
            return (rowTypeName, new CsvFlowSource(sourceComponent.Name, fileSourceKey, format?.HeaderRowsToSkip ?? 0));
        }

        if (SourceInfo.IsSqlSource(sourceComponent))
        {
            var sqlSource = BuildSqlFlowSource(package, sourceComponent.Name, sourceComponent, destinationForSqlCheck, gaps);
            if (sqlSource is null) return null; // BuildSqlFlowSource already added the reason

            var rowTypeName = rowTypeBaseName + "SqlRow";
            Merge(files, gaps, SqlRowEmitter.Emit($"{ns}.Sql", rowTypeName, sourceComponent));
            Merge(files, gaps, SqlRowReaderEmitter.Emit($"{ns}.Sql", rowTypeName, sourceComponent));

            return (rowTypeName, sqlSource);
        }

        gaps.Add(new GenerationGap(sourceComponent.Name, "no Flat File Source or SQL source (with a resolvable connection manager) found for this Merge Join side"));
        return null;
    }

    /// <summary>
    /// Generates a whole Merge Join flow: each side's own row type (via
    /// <see cref="ResolveMergeJoinSideSource"/>, the same emitters any single-source flow uses),
    /// the combined row type + mapper (<see cref="MergeJoinEmitter"/>), and the destination's own
    /// Model/Mapping/DbSet trio exactly like a plain single-destination flow -- a Merge Join's
    /// own combined row type is deliberately shaped so the destination's existing
    /// EntityEmitter/TransformEmitter code paths need no changes at all to consume it.
    /// </summary>
    private static ProgramFlowSpec? GenerateMergeJoinFlow(
        PackageSpec package, string ns, DataFlowPlan flow, MergeJoinPlan mergeJoin,
        List<FileSourceEntryRequest> fileSourceEntries, List<GeneratedFile> files, List<GeneratedFile> testFiles,
        List<TestCoverageNote> testCoverageNotes,
        List<DbContextEmitter.TableSpec> tables, HashSet<string> functionsUsed, Dictionary<string, PrimaryKeyCandidateSpec> primaryKeysByDestination,
        List<GenerationGap> gaps, ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase,
        bool emitSeams, List<CsvSampleCandidate>? csvSampleCandidates = null)
    {
        var entityName = TryResolveEntityName(flow.TaskName, flow.DestinationComponent, gaps);
        if (entityName is null) return null;

        if (!DestinationInfo.IsFastLoadConfigured(flow.DestinationComponent))
        {
            gaps.Add(new GenerationGap(flow.TaskName, $"destination '{flow.DestinationComponent.Name}' is not configured for fast load -- row-by-row insert generation is not supported yet"));
            return null;
        }

        var baseName = SanitizeIdentifier(mergeJoin.Component.Name);
        var leftResult = ResolveMergeJoinSideSource(package, ns, baseName + "Left", mergeJoin.Left.SourceComponent, flow.DestinationComponent, fileSourceEntries, files, gaps, csvSampleCandidates);
        if (leftResult is null) return null;
        var rightResult = ResolveMergeJoinSideSource(package, ns, baseName + "Right", mergeJoin.Right.SourceComponent, flow.DestinationComponent, fileSourceEntries, files, gaps, csvSampleCandidates);
        if (rightResult is null) return null;
        var (leftRowTypeName, leftSource) = leftResult.Value;
        var (rightRowTypeName, rightSource) = rightResult.Value;
        var leftRowTypeNamespace = leftSource is CsvFlowSource or FixedWidthFlowSource ? $"{ns}.Csv" : $"{ns}.Sql";
        var rightRowTypeNamespace = rightSource is CsvFlowSource or FixedWidthFlowSource ? $"{ns}.Csv" : $"{ns}.Sql";

        var rowTypeName = baseName + "Row";
        var mapperClassName = baseName + "Mapper";
        var mergeJoinResult = MergeJoinEmitter.Emit(
            $"{ns}.Mapping", $"{ns}.Sql", $"{ns}.Ssis", rowTypeName, mapperClassName,
            leftRowTypeNamespace, leftRowTypeName, rightRowTypeNamespace, rightRowTypeName, mergeJoin);
        files.AddRange(mergeJoinResult.Files);
        gaps.AddRange(mergeJoinResult.Gaps);
        if (mergeJoinResult.Files.Count == 0) return null; // MergeJoinEmitter already recorded why

        // Merge Join mapper (Phase 2 of the generated-tests plan): a pure static-function test, no
        // PackageHarness needed -- emitted here, not batched with the other Phase-2 starter tests
        // further down in Generate, because left/rightRowTypeName/mapperClassName are only in
        // scope inside this method (same reasoning the Conditional Split router test already
        // established for its own emission site).
        if (mergeJoinResult.LeftKeyTest is { } leftKeyTest && mergeJoinResult.RightKeyTest is { } rightKeyTest)
        {
            var mapperTest = ComponentTestEmitter.EmitMergeJoinMapperTest(
                $"{package.ObjectName}.Tests", leftRowTypeNamespace, leftRowTypeName, rightRowTypeNamespace, rightRowTypeName,
                $"{ns}.Mapping", mapperClassName, leftKeyTest, rightKeyTest, mergeJoinResult.TestColumns ?? []);
            if (mapperTest is not null)
            {
                var mapperTestFinal = mapperTest with { RelativePath = $"{package.ObjectName}.Tests/{mapperTest.RelativePath}" };
                testFiles.Add(mapperTestFinal);
                NoteTest(testCoverageNotes, mapperTestFinal, "MergeJoinMapper",
                    "asserts LeftKey/RightKey/Map over synthesized rows -- a pure static-function test, no PackageHarness needed.");
            }
        }

        Merge(files, gaps, EntityEmitter.Emit($"{ns}.Model", entityName, flow.DestinationComponent, mergeJoinResult.NullableColumnNames));

        var transformClassName = entityName + "Transform";
        var transformResult = TransformEmitter.Emit(new TransformRequest(
            EmitSeams: emitSeams,
            MappingNamespace: $"{ns}.Mapping",
            TransformClassName: transformClassName,
            RowTypeNamespace: $"{ns}.Sql",
            RowTypeName: rowTypeName,
            EntityNamespace: $"{ns}.Model",
            EntityName: entityName,
            SsisFnNamespace: $"{ns}.Ssis",
            Pipeline: flow.Pipeline,
            DerivedColumns: [],
            DestinationComponent: flow.DestinationComponent,
            NullableColumnNames: mergeJoinResult.NullableColumnNames,
            // MergeJoinEmitter already resolved every one of rowTypeName's own columns itself
            // (including through a Data Conversion, via its own SourceColumnLineageId walk) --
            // the unrecognized-producer gate has no visibility into that resolution, so it must
            // be skipped here (see TransformRequest.TrustAllPassthroughColumns's own doc comment).
            TrustAllPassthroughColumns: true));
        Merge(files, gaps, transformResult.Result);
        foreach (var fn in transformResult.SsisFunctionsUsed) functionsUsed.Add(fn);
        foreach (var fn in mergeJoinResult.SsisFunctionsUsed) functionsUsed.Add(fn);

        if (transformResult.Result.Files.Count == 0) return null; // TransformEmitter already recorded why

        // TransformTestEmitter cannot be called for this flow at all (see the
        // TrustAllPassthroughColumns comment above -- it has no visibility into MergeJoinEmitter's
        // own column resolution), distinct from the Merge Join MAPPER test just above, which
        // already covers LeftKey/RightKey/Map. Report it only when there's actually something
        // this flow's own downstream Derived Column/Data Conversion would otherwise have gotten
        // tested -- an unconditional gap here would fire on every Merge Join flow regardless,
        // exactly the miscounting shape PortfolioDigest.IsBlockingGap's own design already exists
        // to prevent.
        if (flow.DerivedColumn is not null || flow.DataConversion is not null)
        {
            gaps.Add(new GenerationGap(entityName,
                $"'{transformClassName}' has a Derived Column/Data Conversion downstream of this Merge Join, but no starter test was generated for it -- TransformTestEmitter does not yet cover a Merge Join's own downstream transform (a materially different row-construction shape from the single-destination case). Add an assertion by hand.",
                IsBlocking: false, Kind: GapKind.TestOracle, EvidenceRefId: mergeJoin.Component.RefId));
        }

        primaryKeysByDestination.TryGetValue(flow.DestinationComponent.RefId, out var primaryKey);
        tables.Add(new DbContextEmitter.TableSpec(entityName, flow.DestinationComponent, primaryKey));

        var mergeJoinSource = new MergeJoinFlowSource(
            mergeJoin.Component.Name, leftSource, rightSource, leftRowTypeName, rightRowTypeName, mapperClassName, mergeJoinResult.KeyClrType,
            mergeJoin.JoinType);

        if (authMode is null && ResolveDatabaseAuth(package, flow.DestinationComponent) is { } auth)
            (authMode, userId, targetServer, targetDatabase) = auth;

        return new ProgramFlowSpec(flow.TaskName, mergeJoinSource, rowTypeName, entityName, transformClassName, new SqlFlowSink(flow.DestinationComponent.Name));
    }

    /// <summary>
    /// Generates a whole genuinely multi-independent-source Merge/UnionAll flow -- gap-audit
    /// Phase 3.6 (2026-09-02). Unlike <see cref="GenerateMergeJoinFlow"/>'s own two DIFFERENT
    /// row types (Left/Right, joined), every side here shares ONE row type/reader (via
    /// <see cref="UnionEmitter"/>), since real SSIS enforces matching schemas across every
    /// Merge/UnionAll input -- each side is just its own <see cref="BuildSqlFlowSource"/> result
    /// against that one shared row type (this round's own scope trim: SQL-sourced sides only,
    /// see <c>UnionPlan</c>'s own doc comment).
    /// </summary>
    private static ProgramFlowSpec? GenerateUnionFlow(
        PackageSpec package, string ns, DataFlowPlan flow, UnionPlan union,
        List<FileSourceEntryRequest> fileSourceEntries, List<GeneratedFile> files, List<DbContextEmitter.TableSpec> tables, HashSet<string> functionsUsed,
        Dictionary<string, PrimaryKeyCandidateSpec> primaryKeysByDestination, List<GenerationGap> gaps,
        ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase,
        bool emitSeams)
    {
        // Same isFlatFileDestination branching the single-destination path already has -- both
        // real probe fixtures for THIS round (gap-audit Phase 3.6) are Flat File destinations
        // deliberately (a SQL destination gives no storage-order guarantee, so ordering has no
        // OBSERVABLE effect there -- the same reasoning gap-audit Phase 3.5's own standalone Sort
        // round already established), so this path must support it, not just OLE DB/ADO NET.
        var isFlatFileDestination = flow.DestinationComponent.ComponentClassId == "Microsoft.FlatFileDestination";
        var entityName = isFlatFileDestination
            ? SanitizeIdentifier(flow.TaskName)
            : TryResolveEntityName(flow.TaskName, flow.DestinationComponent, gaps);
        if (entityName is null) return null;

        if (!isFlatFileDestination && !DestinationInfo.IsFastLoadConfigured(flow.DestinationComponent))
        {
            gaps.Add(new GenerationGap(flow.TaskName, $"destination '{flow.DestinationComponent.Name}' is not configured for fast load -- row-by-row insert generation is not supported yet"));
            return null;
        }

        var rowTypeName = SanitizeIdentifier(union.Component.Name) + "Row";
        var rowTypeResult = UnionEmitter.EmitRowType($"{ns}.Sql", rowTypeName, union.Component);
        Merge(files, gaps, rowTypeResult);
        if (rowTypeResult.Files.Count == 0) return null; // UnionEmitter already recorded why

        var readerResult = UnionEmitter.EmitReader($"{ns}.Sql", rowTypeName, union.Component);
        Merge(files, gaps, readerResult);
        if (readerResult.Files.Count == 0) return null;

        var sides = new List<FlowSourceSpec>();
        foreach (var side in union.Sides)
        {
            var sqlSource = BuildSqlFlowSource(package, side.SourceComponent.Name, side.SourceComponent, flow.DestinationComponent, gaps, side.ColumnAliases);
            if (sqlSource is null) return null; // BuildSqlFlowSource already added the reason
            sides.Add(sqlSource);
        }

        Merge(files, gaps, EntityEmitter.Emit($"{ns}.Model", entityName, flow.DestinationComponent));

        var transformClassName = entityName + "Transform";
        var transformResult = TransformEmitter.Emit(new TransformRequest(
            EmitSeams: emitSeams,
            MappingNamespace: $"{ns}.Mapping",
            TransformClassName: transformClassName,
            RowTypeNamespace: $"{ns}.Sql",
            RowTypeName: rowTypeName,
            EntityNamespace: $"{ns}.Model",
            EntityName: entityName,
            SsisFnNamespace: $"{ns}.Ssis",
            Pipeline: flow.Pipeline,
            DerivedColumns: [],
            DestinationComponent: flow.DestinationComponent,
            NullableColumnNames: null,
            // UnionEmitter already resolved every one of rowTypeName's own columns straight off
            // the union component's own raw output -- same reasoning GenerateMergeJoinFlow's own
            // call already established for its combined row type.
            TrustAllPassthroughColumns: true));
        Merge(files, gaps, transformResult.Result);
        foreach (var fn in transformResult.SsisFunctionsUsed) functionsUsed.Add(fn);

        if (transformResult.Result.Files.Count == 0) return null; // TransformEmitter already recorded why

        FlowSinkSpec sink;
        if (isFlatFileDestination)
        {
            var flatFileSink = ResolveFlatFileSink(package, flow.TaskName, flow.DestinationComponent, entityName, fileSourceEntries, gaps);
            if (flatFileSink is null) return null; // ResolveFlatFileSink already added the reason
            sink = flatFileSink;
        }
        else
        {
            primaryKeysByDestination.TryGetValue(flow.DestinationComponent.RefId, out var primaryKey);
            tables.Add(new DbContextEmitter.TableSpec(entityName, flow.DestinationComponent, primaryKey));
            sink = new SqlFlowSink(flow.DestinationComponent.Name);
        }

        string? keyPropertyName = null;
        string? keyClrType = null;
        if (union.IsSortedInterleave)
        {
            // PlanUnion already guarantees exactly 2 sides, both with a resolved SortKey, and
            // both sides' own key CLR types matching -- safe to read side 0's alone.
            keyPropertyName = union.Sides[0].SortKey!.KeyColumnName;
            keyClrType = union.Sides[0].SortKey!.KeyClrType.ClrTypeName;
        }

        var unionSource = new UnionFlowSource(union.Component.Name, union.IsSortedInterleave, sides, rowTypeName, keyPropertyName, keyClrType);

        if (!isFlatFileDestination && authMode is null && ResolveDatabaseAuth(package, flow.DestinationComponent) is { } auth)
            (authMode, userId, targetServer, targetDatabase) = auth;

        // No starter test coverage exists for a multi-source Merge/UnionAll flow at all -- a real,
        // previously-silent gap (caught 2026-09-06 by an independent review against
        // Docs/Generated-Tests-Plan.md, the same "wired for shape X, not shape Y" bug class the
        // Lookup+Aggregate and ForEach-Loop-over-a-Data-Flow-Task gaps above already named): a
        // UnionFlowSource is its own type, matching neither the generic SQL/Excel source-test
        // loop's `is SqlFlowSource`/`is ExcelFlowSource` checks nor csvSampleCandidates, and
        // ComponentTestEmitter has no Union-specific test method the way Merge Join gets its own
        // mapper test. Gapped honestly instead of guessing at a new test shape under time pressure.
        gaps.Add(new GenerationGap(flow.TaskName,
            $"'{transformClassName}' (Merge/UnionAll '{union.Component.Name}') has no starter test coverage in this pilot -- neither its own multi-source read, its transform, nor its sink has a dedicated test shape yet. Verify by hand.",
            IsBlocking: false, Kind: GapKind.TestOracle, EvidenceRefId: union.Component.RefId));

        return new ProgramFlowSpec(flow.TaskName, unionSource, rowTypeName, entityName, transformClassName, sink);
    }

    /// <summary>
    /// Generates a whole Conditional Split flow: one shared source (via <see cref="ResolveFlowSource"/>),
    /// the flow's own shared Derived Column (if any, unchanged from the single-destination path
    /// -- scope decision 1, CLAUDE.md), one router (<see cref="RouterEmitter"/>), and one
    /// Model/Mapping/DbSet trio per DISTINCT destination -- two or more branches can resolve to
    /// the SAME destination (a Union All remerge, see PackagePlanner.ResolveBranch), in which
    /// case they share one entity/table but still get their own Mapping/*Transform.cs, combining
    /// the flow's shared Derived Column with whatever per-branch Derived Column(s) that branch's
    /// own chain passed through (e.g. a "tag" applied after the split). Buffers every branch's
    /// generated files/table/functions-used locally and only commits them (plus the router's own
    /// file) to the real <paramref name="files"/>/<paramref name="tables"/>/
    /// <paramref name="functionsUsed"/> once EVERY branch and the router succeed -- scope
    /// decision 2 (no partial split generation). A flow's own source-row-type file(s) (emitted
    /// eagerly by ResolveFlowSource, before any branch is attempted) are the one exception: they
    /// describe the source, not any branch, so they're valid output regardless of whether a
    /// branch downstream ends up failing -- an orphaned-but-harmless Sql/Csv row-type file is not
    /// "half-wired" the way a partially-wired Program.cs would be.
    /// </summary>
    private static ProgramConditionalSplitStep? GenerateConditionalSplitFlow(
        PackageSpec package, string ns, DataFlowPlan flow, ConditionalSplitPlan split,
        List<FileSourceEntryRequest> fileSourceEntries, List<GeneratedFile> files, List<GeneratedFile> testFiles,
        List<TestCoverageNote> testCoverageNotes,
        List<DbContextEmitter.TableSpec> tables, HashSet<string> functionsUsed,
        Dictionary<string, PrimaryKeyCandidateSpec> primaryKeysByDestination, List<GenerationGap> gaps,
        ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase,
        bool emitSeams, List<CsvSampleCandidate>? csvSampleCandidates = null, ComponentHolderRegistry? componentHolders = null)
    {
        var flowBaseName = SanitizeIdentifier(flow.TaskName);
        // Never null here: ConditionalSplitBranchPlan.Discarded is gated to Multicast only in
        // ResolveBranch (a Conditional Split branch is always fatal-for-the-whole-flow if it
        // can't resolve to a real destination -- that invariant is unchanged).
        var defaultDestination = split.Branches[^1].Destination!;

        // Computed before ResolveFlowSource (unlike the single-destination path's own ordering
        // note doesn't apply here since there's no DerivedColumn-null gate before it in THIS
        // path) -- CsvRowEmitter/SqlRowEmitter/SqlRowReaderEmitter all need it to mark a Data
        // Conversion's own raw source column nullable. Missing this was a real bug, caught only
        // by actually running the generated code against a seeded NULL row: the reader's
        // GetFieldValue<string> (no IsDBNull guard, since the column wasn't in the set) threw
        // SqlNullValueException on ID=2's NULL SignupDateText, not by a unit test.
        var nullableColumnNames = ResolveNullableColumnNames(flow);

        var sourceResult = ResolveFlowSource(package, ns, flowBaseName, flow, defaultDestination, fileSourceEntries, files, gaps, nullableColumnNames, csvSampleCandidates);
        if (sourceResult is null) return null;
        var (rowTypeName, programSource) = sourceResult.Value;
        var rowTypeNamespace = programSource is CsvFlowSource or FixedWidthFlowSource ? $"{ns}.Csv" : programSource is ExcelFlowSource ? $"{ns}.Excel" : programSource is XmlFlowSource ? $"{ns}.Xml" : $"{ns}.Sql";

        // A split flow with neither a shared upstream Derived Column, a shared upstream Data
        // Conversion, nor any per-branch Derived Column (see ConditionalSplitBranchPlan.
        // DerivedColumns) is a direct-copy-with-routing flow -- out of scope for the same reason
        // a plain direct-copy flow is (the single-destination path's own gate, right above this
        // method, widened for the identical reason in the Data Conversion round). Real evidenced
        // case this widening closes: RBC_Demo_ETL's own DFT_MergeSortedBranches has NO Derived
        // Column anywhere (Split -> Sort -> Merge on each branch, no per-branch transform), yet
        // its destination genuinely needs SsisFn.ToNullableI4(row.CustomerID) for one column --
        // TransformEmitter already resolves that correctly via flow.DataConversion's own
        // convertedByName lookup (passed in below regardless of DerivedColumns.Count), this gate
        // just needs to stop rejecting the flow before that resolution ever runs.
        if (flow.DerivedColumn is null && flow.DataConversion is null && split.Branches.All(b => b.DerivedColumns.Count == 0))
        {
            // Wording matters here: the general "a direct-copy pipeline is not generated" gate was
            // REMOVED from the single-destination path (Excel Source round), so a message saying
            // that flatly is now misleading -- it reads as a tool-wide limitation when it is
            // specific to the Conditional Split/Multicast path, which still has no evidenced
            // direct-copy case to verify against.
            gaps.Add(new GenerationGap(flow.TaskName, "this Conditional Split/Multicast flow has no Derived Column and no Data Conversion on any branch -- a pure column copy through a branching flow is not generated yet (single-destination flows DO support it), so this flow is not wired into Program.cs"));
            return null;
        }

        var routerClassName = SanitizeIdentifier(split.Component.Name) + "Router";
        var routerResult = RouterEmitter.Emit($"{ns}.Mapping", routerClassName, rowTypeNamespace, rowTypeName, $"{ns}.Ssis", split, flow.Pipeline, flow.DataConversion, flow.CopyMap);
        if (routerResult.Result.Files.Count == 0)
        {
            gaps.AddRange(routerResult.Result.Gaps);
            return null;
        }

        // Phase A: validate every branch and resolve its entity name, without emitting
        // anything yet -- resolving every branch's name up front is what lets Phase B tell a
        // genuine convergence (two branches, same entity, via a shared destination) apart from
        // the ordinary one-branch-one-destination shape (see emittedDestinationRefIds below).
        // Transform class naming itself no longer depends on entity-name collision counting --
        // see branchTransformClassName's own comment below.
        var entityNames = new List<string>();
        foreach (var branch in split.Branches)
        {
            var branchDestination = branch.Destination!; // never null -- see defaultDestination's own comment above
            if (!DestinationInfo.IsFastLoadConfigured(branchDestination))
            {
                gaps.Add(new GenerationGap(flow.TaskName, $"Conditional Split branch '{branch.OutputName}': destination '{branchDestination.Name}' is not configured for fast load -- row-by-row insert generation is not supported yet"));
                return null;
            }

            var entityName = TryResolveEntityName(flow.TaskName, branchDestination, gaps);
            if (entityName is null) return null;
            entityNames.Add(entityName);
        }

        var branchFiles = new List<GeneratedFile>();
        var branchTables = new List<DbContextEmitter.TableSpec>();
        var branchFunctionsUsed = new HashSet<string>();
        var branches = new List<ProgramConditionalSplitBranch>();
        var branchTestFiles = new List<GeneratedFile>();
        var emittedDestinationRefIds = new HashSet<string>(StringComparer.Ordinal);
        (string AuthMode, string? UserId, string? Server, string? Database)? resolvedAuth = null;

        for (var i = 0; i < split.Branches.Count; i++)
        {
            var branch = split.Branches[i];
            var branchDestination = branch.Destination!; // never null -- see defaultDestination's own comment above
            var entityName = entityNames[i];

            // 1-to-1 component-to-function mapping round (2026-09), user-confirmed naming:
            // {SplitComponent}_{OutputName} -- e.g. "CSPLIT_Validity_Valid" -- rather than the
            // entity-derived "{Entity}Transform"/"{Entity}{OutputName}Transform" this used to be.
            // A Conditional Split's own branch OutputNames are distinct by construction, so this
            // is always unique within one split with no entityNameCounts check needed the way the
            // old entity-keyed naming required.
            var branchTransformClassName = $"{SanitizeIdentifier(split.Component.Name)}_{SanitizeIdentifier(branch.OutputName)}";

            if (emittedDestinationRefIds.Add(branchDestination.RefId))
            {
                Merge(branchFiles, gaps, EntityEmitter.Emit($"{ns}.Model", entityName, branchDestination, nullableColumnNames));
                primaryKeysByDestination.TryGetValue(branchDestination.RefId, out var primaryKey);
                branchTables.Add(new DbContextEmitter.TableSpec(entityName, branchDestination, primaryKey));
            }

            var derivedColumns = new List<PipelineComponentSpec>();
            if (flow.DerivedColumn is not null) derivedColumns.Add(flow.DerivedColumn);
            derivedColumns.AddRange(branch.DerivedColumns);

            var transformResult = TransformEmitter.Emit(new TransformRequest(
                EmitSeams: emitSeams,
                MappingNamespace: $"{ns}.Mapping",
                TransformClassName: branchTransformClassName,
                RowTypeNamespace: rowTypeNamespace,
                RowTypeName: rowTypeName,
                EntityNamespace: $"{ns}.Model",
                EntityName: entityName,
                SsisFnNamespace: $"{ns}.Ssis",
                Pipeline: flow.Pipeline,
                DerivedColumns: derivedColumns,
                DataConversions: flow.DataConversion is null ? [] : [flow.DataConversion],
                DestinationComponent: branchDestination,
                NullableColumnNames: nullableColumnNames,
                Holders: componentHolders));
            Merge(branchFiles, gaps, transformResult.Result);
            foreach (var fn in transformResult.SsisFunctionsUsed) branchFunctionsUsed.Add(fn);

            if (transformResult.Result.Files.Count == 0) return null; // TransformEmitter already recorded why

            // A starter unit test for THIS branch's own transform -- same call the
            // single-destination path already makes, just once per branch here. Buffered like
            // branchFiles/branchTables above (never added to testFiles directly), since a later
            // branch in this same loop can still fail the whole flow.
            var branchTestResult = TransformTestEmitter.Emit(new TransformTestRequest(
                TestNamespace: $"{package.ObjectName}.Tests",
                TransformClassName: branchTransformClassName,
                MappingNamespace: $"{ns}.Mapping",
                RowTypeNamespace: rowTypeNamespace,
                RowTypeName: rowTypeName,
                EntityNamespace: $"{ns}.Model",
                EntityName: entityName,
                Pipeline: flow.Pipeline,
                DerivedColumns: derivedColumns,
                DataConversions: flow.DataConversion is null ? [] : [flow.DataConversion],
                DestinationComponent: branchDestination));
            gaps.AddRange(branchTestResult.Gaps);
            branchTestFiles.AddRange(branchTestResult.Files);

            branches.Add(new ProgramConditionalSplitBranch(branch.OutputName, entityName, branchTransformClassName));

            if (resolvedAuth is null && ResolveDatabaseAuth(package, branchDestination) is { } auth)
                resolvedAuth = auth;
        }

        // Every branch and the router succeeded -- commit the buffered output for real.
        files.AddRange(branchFiles);
        tables.AddRange(branchTables);
        foreach (var fn in branchFunctionsUsed) functionsUsed.Add(fn);
        foreach (var fn in routerResult.SsisFunctionsUsed) functionsUsed.Add(fn);
        files.AddRange(routerResult.Result.Files);
        foreach (var f in branchTestFiles)
        {
            var branchTestFinal = f with { RelativePath = $"{package.ObjectName}.Tests/{f.RelativePath}" };
            testFiles.Add(branchTestFinal);
            NoteTest(testCoverageNotes, branchTestFinal, "Transform",
                "asserts each output column for one representative row; does not exercise NULL/boundary inputs beyond the chosen representative value.");
        }

        // Router -- Conditional Split (Phase 2 of the generated-tests plan): SelectBranch is a
        // pure function, no PackageHarness needed at all -- see RouterTestEmitter's own doc
        // comment. Emitted here, not batched with the other Phase-2 starter tests further down in
        // Generate, because split/rowTypeName/routerClassName are only in scope inside this
        // method.
        var routerTest = RouterTestEmitter.Emit($"{package.ObjectName}.Tests", $"{ns}.Mapping", routerClassName, rowTypeNamespace, rowTypeName, split, flow.DerivedColumn, flow.DataConversion, flow.CopyMap);
        foreach (var f in routerTest.Files)
        {
            var routerTestFinal = f with { RelativePath = $"{package.ObjectName}.Tests/{f.RelativePath}" };
            testFiles.Add(routerTestFinal);
            NoteTest(testCoverageNotes, routerTestFinal, "Router",
                "asserts one case per branch plus the default, using a representative literal per condition; a condition that can't be resolved to a literal degrades to a test-oracle packet instead of a guess.");
        }
        gaps.AddRange(routerTest.Gaps);

        if (authMode is null && resolvedAuth is { } resolved)
            (authMode, userId, targetServer, targetDatabase) = resolved;

        return new ProgramConditionalSplitStep(flow.TaskName, programSource, rowTypeName, routerClassName, branches);
    }

    /// <summary>Mirrors <see cref="GenerateConditionalSplitFlow"/>'s own Phase A/B shape (resolve
    /// every branch's entity name up front, then emit, committing only if everything succeeds) --
    /// the two real differences are that there's no RouterEmitter call at all (Multicast has no
    /// condition to translate) and each branch is independently checked for whether it targets a
    /// Flat File Destination (the real evidenced shape, RBC_Demo_ETL's own DFT_FixedWidthImport,
    /// fans to BOTH an OLE DB Destination and a Flat File Destination from one Multicast -- no
    /// evidenced Conditional Split ever needed this, so that method's own branches are still
    /// assumed uniformly SQL-sunk).</summary>
    private static ProgramMulticastStep? GenerateMulticastFlow(
        PackageSpec package, string ns, DataFlowPlan flow, MulticastPlan multicast,
        List<FileSourceEntryRequest> fileSourceEntries, List<GeneratedFile> files, List<GeneratedFile> testFiles,
        List<TestCoverageNote> testCoverageNotes,
        List<DbContextEmitter.TableSpec> tables, HashSet<string> functionsUsed,
        Dictionary<string, PrimaryKeyCandidateSpec> primaryKeysByDestination, List<GenerationGap> gaps,
        ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase,
        bool emitSeams, List<MulticastTestCandidate>? testCandidates = null, List<CsvSampleCandidate>? csvSampleCandidates = null,
        ComponentHolderRegistry? componentHolders = null)
    {
        var flowBaseName = SanitizeIdentifier(flow.TaskName);

        // A Discarded branch (added 2026-09-02 -- see ConditionalSplitBranchPlan's own doc
        // comment) has no Destination and generates nothing: its advisory gap was already
        // recorded by ResolveBranch at planning time (that gaps list flows straight into
        // PackageGenerateResult.Gaps), so nothing more to do here than exclude it. Not the
        // motivating real shape (which always reaches here via GenerateLookupFlow's own composed
        // path instead, since an Aggregate anywhere in the pipeline wins dispatch first -- see
        // that method's own doc comment) but a direct, foreseeable consequence of ResolveBranch
        // now accepting a discard: a plain Multicast with one live branch and one discarded
        // RowCount branch, no Lookup and no Aggregate, would otherwise reach this method and
        // crash dereferencing a null Destination.
        var liveBranches = multicast.Branches.Where(b => !b.Discarded).ToList();
        if (liveBranches.Count == 0)
        {
            gaps.Add(new GenerationGap(flow.TaskName, $"Multicast '{multicast.Component.Name}' has no live branches -- every output is a discarded RowCount dead end"));
            return null;
        }

        var defaultDestination = liveBranches[^1].Destination!; // never null for a live branch
        var defaultIsFlatFile = defaultDestination.ComponentClassId == "Microsoft.FlatFileDestination";

        var nullableColumnNames = ResolveNullableColumnNames(flow);

        // A null destinationForSqlCheck is passed when every branch is a Flat File Destination --
        // there is no SQL destination anywhere in this flow to compare the source's connection
        // against, the same reasoning BuildSqlFlowSource's own null-destinationComponent case
        // documents for an OLE DB Command flow.
        var sourceResult = ResolveFlowSource(package, ns, flowBaseName, flow,
            defaultIsFlatFile ? null : defaultDestination, fileSourceEntries, files, gaps, nullableColumnNames, csvSampleCandidates);
        if (sourceResult is null) return null;
        var (rowTypeName, programSource) = sourceResult.Value;
        var rowTypeNamespace = programSource is CsvFlowSource or FixedWidthFlowSource ? $"{ns}.Csv" : programSource is ExcelFlowSource ? $"{ns}.Excel" : programSource is XmlFlowSource ? $"{ns}.Xml" : $"{ns}.Sql";

        // Phase A: validate every branch and resolve its entity name, without emitting anything
        // yet -- same convergence-detection shape as GenerateConditionalSplitFlow's own Phase A.
        var entityNames = new List<string>();
        var branchIsFlatFile = new List<bool>();
        foreach (var branch in liveBranches)
        {
            var branchDestination = branch.Destination!; // never null -- liveBranches excludes Discarded
            var isFlatFile = branchDestination.ComponentClassId == "Microsoft.FlatFileDestination";
            branchIsFlatFile.Add(isFlatFile);

            if (!isFlatFile && !DestinationInfo.IsFastLoadConfigured(branchDestination))
            {
                gaps.Add(new GenerationGap(flow.TaskName, $"Multicast branch '{branch.OutputName}': destination '{branchDestination.Name}' is not configured for fast load -- row-by-row insert generation is not supported yet"));
                return null;
            }

            // Keyed off the DESTINATION component's own name, not the branch's output name --
            // consistent with the SQL branch's own convention (entityName comes from the
            // destination table, never the branch) so two branches that converge to the SAME
            // Flat File Destination (via a Union All) naturally compute the identical entity
            // name too, the same convergence-detection shape emittedDestinationRefIds below
            // relies on. Transform class naming itself no longer depends on entity-name collision
            // counting -- see branchTransformClassName's own comment below.
            var entityName = isFlatFile
                ? SanitizeIdentifier(flow.TaskName) + SanitizeIdentifier(branchDestination.Name)
                : TryResolveEntityName(flow.TaskName, branchDestination, gaps);
            if (entityName is null) return null;
            entityNames.Add(entityName);
        }

        var branchFiles = new List<GeneratedFile>();
        var branchTables = new List<DbContextEmitter.TableSpec>();
        var branchFunctionsUsed = new HashSet<string>();
        var branches = new List<ProgramMulticastBranch>();
        var branchTestFiles = new List<GeneratedFile>();
        var emittedDestinationRefIds = new HashSet<string>(StringComparer.Ordinal);
        (string AuthMode, string? UserId, string? Server, string? Database)? resolvedAuth = null;

        for (var i = 0; i < liveBranches.Count; i++)
        {
            var branch = liveBranches[i];
            var branchDestination = branch.Destination!; // never null -- liveBranches excludes Discarded
            var entityName = entityNames[i];
            var isFlatFile = branchIsFlatFile[i];

            // 1-to-1 component-to-function mapping round (2026-09), user-confirmed naming:
            // {MulticastComponent}_{OutputName} -- e.g. "MCAST_FixedRows_Output1" -- rather than
            // the entity-derived "{Entity}Transform"/"{Entity}{OutputName}Transform" this used to
            // be. A Multicast's own branch OutputNames are distinct by construction, so this is
            // always unique within one Multicast with no entityNameCounts check needed.
            var branchTransformClassName = $"{SanitizeIdentifier(multicast.Component.Name)}_{SanitizeIdentifier(branch.OutputName)}";

            FlowSinkSpec? sink = null;
            if (emittedDestinationRefIds.Add(branchDestination.RefId))
            {
                Merge(branchFiles, gaps, EntityEmitter.Emit($"{ns}.Model", entityName, branchDestination, nullableColumnNames));
                if (isFlatFile)
                {
                    var flatFileSink = ResolveFlatFileSink(package, flow.TaskName, branchDestination, entityName, fileSourceEntries, gaps);
                    if (flatFileSink is null) return null; // ResolveFlatFileSink already added the reason
                    sink = flatFileSink;
                }
                else
                {
                    primaryKeysByDestination.TryGetValue(branchDestination.RefId, out var primaryKey);
                    branchTables.Add(new DbContextEmitter.TableSpec(entityName, branchDestination, primaryKey));
                    sink = new SqlFlowSink(branchDestination.Name);
                }
            }

            var derivedColumns = new List<PipelineComponentSpec>();
            if (flow.DerivedColumn is not null) derivedColumns.Add(flow.DerivedColumn);
            derivedColumns.AddRange(branch.DerivedColumns);

            var transformResult = TransformEmitter.Emit(new TransformRequest(
                EmitSeams: emitSeams,
                MappingNamespace: $"{ns}.Mapping",
                TransformClassName: branchTransformClassName,
                RowTypeNamespace: rowTypeNamespace,
                RowTypeName: rowTypeName,
                EntityNamespace: $"{ns}.Model",
                EntityName: entityName,
                SsisFnNamespace: $"{ns}.Ssis",
                Pipeline: flow.Pipeline,
                DerivedColumns: derivedColumns,
                DataConversions: flow.DataConversion is null ? [] : [flow.DataConversion],
                DestinationComponent: branchDestination,
                NullableColumnNames: nullableColumnNames,
                Holders: componentHolders));
            Merge(branchFiles, gaps, transformResult.Result);
            foreach (var fn in transformResult.SsisFunctionsUsed) branchFunctionsUsed.Add(fn);

            if (transformResult.Result.Files.Count == 0) return null; // TransformEmitter already recorded why

            // A starter unit test for THIS branch's own transform -- same call
            // GenerateConditionalSplitFlow's own identical shape already makes, once per branch
            // (including a converged one, mirroring the unconditional TransformEmitter call
            // just above -- each branch can still carry its own distinct per-branch Derived
            // Column even when its destination converges with another branch's).
            var branchTestResult = TransformTestEmitter.Emit(new TransformTestRequest(
                TestNamespace: $"{package.ObjectName}.Tests",
                TransformClassName: branchTransformClassName,
                MappingNamespace: $"{ns}.Mapping",
                RowTypeNamespace: rowTypeNamespace,
                RowTypeName: rowTypeName,
                EntityNamespace: $"{ns}.Model",
                EntityName: entityName,
                Pipeline: flow.Pipeline,
                DerivedColumns: derivedColumns,
                DataConversions: flow.DataConversion is null ? [] : [flow.DataConversion],
                DestinationComponent: branchDestination));
            gaps.AddRange(branchTestResult.Gaps);
            branchTestFiles.AddRange(branchTestResult.Files);

            // sink is only null when this destination RefId was already emitted by an earlier
            // branch (a convergence) -- that earlier branch shares this one's own entityName (see
            // the naming comment above), so ProgramEmitter's own DistinctBy(EntityName) is what
            // actually picks which branch's Sink value gets used for the shared DI registration;
            // this placeholder is a type-correct stand-in for the branch record, never read for
            // real data.
            sink ??= isFlatFile
                ? new FlatFileFlowSink(branchDestination.Name, entityName, false, null, [])
                : new SqlFlowSink(branchDestination.Name);

            branches.Add(new ProgramMulticastBranch(branch.OutputName, entityName, branchTransformClassName, sink));

            if (!isFlatFile && resolvedAuth is null && ResolveDatabaseAuth(package, branchDestination) is { } auth)
                resolvedAuth = auth;
        }

        // Every branch succeeded -- commit the buffered output for real.
        files.AddRange(branchFiles);
        tables.AddRange(branchTables);
        foreach (var fn in branchFunctionsUsed) functionsUsed.Add(fn);
        foreach (var f in branchTestFiles)
        {
            var branchTestFinal = f with { RelativePath = $"{package.ObjectName}.Tests/{f.RelativePath}" };
            testFiles.Add(branchTestFinal);
            NoteTest(testCoverageNotes, branchTestFinal, "Transform",
                "asserts each output column for one representative row; does not exercise NULL/boundary inputs beyond the chosen representative value.");
        }

        if (authMode is null && resolvedAuth is { } resolved)
            (authMode, userId, targetServer, targetDatabase) = resolved;

        // "Multicast" starter test (Phase 2 of the generated-tests plan): resolved from the flow's
        // own upstream source component (Excel/Flat File/SQL, whichever is present -- the same
        // fallback order OLE DB Command's own test candidate already uses), independent of
        // rowTypeNamespace/programSource's own already-resolved SHAPE. A source with no resolvable
        // non-error output is silently skipped -- ComponentTestEmitter degrades to "no test, no
        // gap" on an empty column list regardless, and this flow already generated successfully
        // without one.
        // A branch whose own destination CONVERGED with an earlier one (see the "sink ??=" placeholder
        // comment above) carries a bogus stand-in Sink never meant to be read for real -- skip the
        // test candidate entirely rather than synthesize a second, incorrect sink for it. Distinct
        // entity names is exactly the same signal ProgramEmitter's own DistinctBy(EntityName) uses.
        var hasConvergedBranch = branches.Select(b => b.EntityName).Distinct(StringComparer.Ordinal).Count() != branches.Count;
        var multicastSourceComponent = flow.OleDbSource ?? flow.ExcelSource ?? flow.FlatFileSource;
        var multicastSourceOutput = multicastSourceComponent?.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        if (multicastSourceOutput is not null && !hasConvergedBranch)
        {
            var resolvedMulticastSource = PipelineResolver.Resolve(multicastSourceOutput);
            var testBranches = branches.Select(b => new ComponentTestEmitter.MulticastTestBranch(
                b.OutputName, $"{ns}.Model", b.EntityName, b.TransformClassName, b.Sink)).ToList();
            testCandidates?.Add(new MulticastTestCandidate(flow.TaskName, rowTypeName, rowTypeNamespace, resolvedMulticastSource.Columns, testBranches));
        }

        return new ProgramMulticastStep(flow.TaskName, programSource, rowTypeName, branches);
    }

    /// <summary>Phase 4 of the unsupported-component-types plan. Resolves a Percentage Sampling
    /// flow -- structurally almost identical to <see cref="GenerateConditionalSplitFlow"/> (two
    /// mutually exclusive branches, each its own SQL destination, wired via a real
    /// <c>IRowRouter&lt;TRow&gt;</c> and <c>ConditionalSplitStep&lt;TRow&gt;</c>) rather than
    /// <see cref="GenerateMulticastFlow"/>'s unconditional fan-out shape -- so this returns a
    /// <see cref="ProgramConditionalSplitStep"/> too, and the caller wires it into the SAME
    /// <c>wiredSplits</c> dictionary <see cref="GenerateConditionalSplitFlow"/>'s own result goes
    /// into, needing no new dictionary/emission case at all
    /// (<see cref="PackageClassEmitter"/>'s own <c>ProgramConditionalSplitStep</c> case already
    /// constructs whatever <c>RouterClassName</c> names, with zero knowledge of which SSIS
    /// component produced it).
    ///
    /// Unlike Conditional Split, there is no expression to translate and no per-branch Derived
    /// Column chain restriction: Percentage Sampling adds/removes/transforms NO column at all
    /// (confirmed real -- see <c>PctSamplingPayload</c>'s own doc comment), so a flow with no
    /// Derived Column anywhere is not a gap here the way it is for Conditional Split's own
    /// direct-copy restriction -- a random row split with no other transform is exactly this
    /// component's real, intended behaviour, not an unevidenced shape to guess at.</summary>
    private static ProgramConditionalSplitStep? GeneratePctSamplingFlow(
        PackageSpec package, string ns, DataFlowPlan flow, PctSamplingPlan pctSampling,
        List<FileSourceEntryRequest> fileSourceEntries, List<GeneratedFile> files, List<GeneratedFile> testFiles,
        List<TestCoverageNote> testCoverageNotes,
        List<DbContextEmitter.TableSpec> tables, HashSet<string> functionsUsed,
        Dictionary<string, PrimaryKeyCandidateSpec> primaryKeysByDestination, List<GenerationGap> gaps,
        ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase,
        bool emitSeams, List<CsvSampleCandidate>? csvSampleCandidates = null, ComponentHolderRegistry? componentHolders = null)
    {
        var flowBaseName = SanitizeIdentifier(flow.TaskName);
        // Never null -- PlanPctSampling requires both branches to resolve to a real destination
        // (no discard/Aggregate pass-through widening the way Multicast's own ResolveBranch call
        // gets, since neither is evidenced for this component).
        var defaultDestination = pctSampling.Sampled.Destination!;

        var nullableColumnNames = ResolveNullableColumnNames(flow);

        var sourceResult = ResolveFlowSource(package, ns, flowBaseName, flow, defaultDestination, fileSourceEntries, files, gaps, nullableColumnNames, csvSampleCandidates);
        if (sourceResult is null) return null;
        var (rowTypeName, programSource) = sourceResult.Value;
        var rowTypeNamespace = programSource is CsvFlowSource or FixedWidthFlowSource ? $"{ns}.Csv" : programSource is ExcelFlowSource ? $"{ns}.Excel" : programSource is XmlFlowSource ? $"{ns}.Xml" : $"{ns}.Sql";

        var routerClassName = SanitizeIdentifier(pctSampling.Component.Name) + "SamplingRouter";
        var routerResult = PctSamplingRouterEmitter.Emit($"{ns}.Mapping", routerClassName, rowTypeNamespace, rowTypeName,
            pctSampling.Component.PctSampling?.SamplingValue ?? 0, pctSampling.Component.PctSampling?.SamplingSeed ?? 0);
        files.Add(routerResult.Files[0]);

        // Branch 0 = Sampled ("Sampling Selected Output"), branch 1 = NotSampled ("Sampling
        // Unselected Output") -- matches PctSamplingRouterEmitter's own fixed SelectBranch
        // convention exactly (0 when the random draw falls under SamplingValue, 1 otherwise).
        var orderedBranches = new[] { pctSampling.Sampled, pctSampling.NotSampled };

        // Phase A: validate every branch and resolve its entity name, without emitting anything
        // yet -- same convergence-detection shape as GenerateConditionalSplitFlow's own Phase A.
        var entityNames = new List<string>();
        foreach (var branch in orderedBranches)
        {
            var branchDestination = branch.Destination!; // never null -- see defaultDestination's own comment above
            if (!DestinationInfo.IsFastLoadConfigured(branchDestination))
            {
                gaps.Add(new GenerationGap(flow.TaskName, $"Percentage Sampling branch '{branch.OutputName}': destination '{branchDestination.Name}' is not configured for fast load -- row-by-row insert generation is not supported yet"));
                return null;
            }

            var entityName = TryResolveEntityName(flow.TaskName, branchDestination, gaps);
            if (entityName is null) return null;
            entityNames.Add(entityName);
        }

        var branchFiles = new List<GeneratedFile>();
        var branchTables = new List<DbContextEmitter.TableSpec>();
        var branchFunctionsUsed = new HashSet<string>();
        var branches = new List<ProgramConditionalSplitBranch>();
        var branchTestFiles = new List<GeneratedFile>();
        var emittedDestinationRefIds = new HashSet<string>(StringComparer.Ordinal);
        (string AuthMode, string? UserId, string? Server, string? Database)? resolvedAuth = null;

        for (var i = 0; i < orderedBranches.Length; i++)
        {
            var branch = orderedBranches[i];
            var branchDestination = branch.Destination!; // never null -- see defaultDestination's own comment above
            var entityName = entityNames[i];

            // Same 1-to-1 component-to-function naming convention Conditional Split/Multicast
            // branches already use: {PctSamplingComponent}_{OutputName}.
            var branchTransformClassName = $"{SanitizeIdentifier(pctSampling.Component.Name)}_{SanitizeIdentifier(branch.OutputName)}";

            if (emittedDestinationRefIds.Add(branchDestination.RefId))
            {
                Merge(branchFiles, gaps, EntityEmitter.Emit($"{ns}.Model", entityName, branchDestination, nullableColumnNames));
                primaryKeysByDestination.TryGetValue(branchDestination.RefId, out var primaryKey);
                branchTables.Add(new DbContextEmitter.TableSpec(entityName, branchDestination, primaryKey));
            }

            // Percentage Sampling never computes a column of its own (see this method's own doc
            // comment) -- a branch's own derivedColumns list here can only ever come from the
            // flow's shared upstream Derived Column/Data Conversion (both threaded straight
            // through, same as every other branch shape) or a per-branch chain
            // ResolveBranch happened to walk through (e.g. a tag Derived Column between the
            // sampling output and the destination) -- branch.DerivedColumns already carries
            // whichever of those actually occurred.
            var derivedColumns = new List<PipelineComponentSpec>();
            if (flow.DerivedColumn is not null) derivedColumns.Add(flow.DerivedColumn);
            derivedColumns.AddRange(branch.DerivedColumns);

            var transformResult = TransformEmitter.Emit(new TransformRequest(
                EmitSeams: emitSeams,
                MappingNamespace: $"{ns}.Mapping",
                TransformClassName: branchTransformClassName,
                RowTypeNamespace: rowTypeNamespace,
                RowTypeName: rowTypeName,
                EntityNamespace: $"{ns}.Model",
                EntityName: entityName,
                SsisFnNamespace: $"{ns}.Ssis",
                Pipeline: flow.Pipeline,
                DerivedColumns: derivedColumns,
                DataConversions: flow.DataConversion is null ? [] : [flow.DataConversion],
                DestinationComponent: branchDestination,
                NullableColumnNames: nullableColumnNames,
                Holders: componentHolders));
            Merge(branchFiles, gaps, transformResult.Result);
            foreach (var fn in transformResult.SsisFunctionsUsed) branchFunctionsUsed.Add(fn);

            if (transformResult.Result.Files.Count == 0) return null; // TransformEmitter already recorded why

            var branchTestResult = TransformTestEmitter.Emit(new TransformTestRequest(
                TestNamespace: $"{package.ObjectName}.Tests",
                TransformClassName: branchTransformClassName,
                MappingNamespace: $"{ns}.Mapping",
                RowTypeNamespace: rowTypeNamespace,
                RowTypeName: rowTypeName,
                EntityNamespace: $"{ns}.Model",
                EntityName: entityName,
                Pipeline: flow.Pipeline,
                DerivedColumns: derivedColumns,
                DataConversions: flow.DataConversion is null ? [] : [flow.DataConversion],
                DestinationComponent: branchDestination));
            gaps.AddRange(branchTestResult.Gaps);
            branchTestFiles.AddRange(branchTestResult.Files);

            branches.Add(new ProgramConditionalSplitBranch(branch.OutputName, entityName, branchTransformClassName));

            if (resolvedAuth is null && ResolveDatabaseAuth(package, branchDestination) is { } auth)
                resolvedAuth = auth;
        }

        // Every branch succeeded -- commit the buffered output for real.
        files.AddRange(branchFiles);
        tables.AddRange(branchTables);
        foreach (var fn in branchFunctionsUsed) functionsUsed.Add(fn);
        foreach (var f in branchTestFiles)
        {
            var branchTestFinal = f with { RelativePath = $"{package.ObjectName}.Tests/{f.RelativePath}" };
            testFiles.Add(branchTestFinal);
            NoteTest(testCoverageNotes, branchTestFinal, "Transform",
                "asserts each output column for one representative row; does not exercise NULL/boundary inputs beyond the chosen representative value.");
        }

        if (authMode is null && resolvedAuth is { } resolved)
            (authMode, userId, targetServer, targetDatabase) = resolved;

        // No RouterTestEmitter call, unlike Conditional Split -- that emitter is tightly coupled
        // to ConditionalSplitPlan's own expression-based branches (RouterTestEmitter picks a
        // representative literal per condition to assert against). Percentage Sampling's own
        // routing decision is a random draw, not a literal-resolvable condition, so no starter
        // test is generated for the router itself -- a deliberate, named simplification, not a
        // silent omission: the branch transform tests above still cover every column each branch
        // produces, and this flow's own reproducibility (same seed -> same split across repeated
        // runs) is verified at the tool level via PctSamplingRouterEmitterTests, not per-package.

        return new ProgramConditionalSplitStep(flow.TaskName, programSource, rowTypeName, routerClassName, branches);
    }

    /// <summary>
    /// Generates a <c>Microsoft.SCD</c> ("Slowly Changing Dimension") flow -- Phase 7 of the
    /// unsupported-component-types plan, and the largest single generator method here.
    ///
    /// <para>Structurally closest to <see cref="GenerateMulticastFlow"/> -- the same Phase A/Phase B
    /// shape (validate and name every branch first, emit second, commit only if every branch
    /// succeeded) and the same per-destination deduplication, so two branches converging on one
    /// destination through a Union All emit its entity/table/sink exactly once. Two things are
    /// genuinely new:</para>
    /// <list type="bullet">
    /// <item>A branch may have <b>no destination at all</b>, its whole effect being a per-row
    /// <c>UPDATE</c> -- the real evidenced <c>Changing Attribute Updates Output</c> shape. Such a
    /// branch emits no entity, no table, no transform and no sink.</item>
    /// <item>The dimension's own current rows are read up front through a generated cache
    /// (<see cref="ScdCacheEmitter"/>), the same mechanism a full-cache Lookup already uses.</item>
    /// </list>
    ///
    /// <para><b>Scope, named rather than implied.</b> A command parameter must resolve to a column on
    /// the flow's own SOURCE row type. The real evidenced package's own historical branch computes its
    /// <c>EndDate</c> parameter in a Derived Column (<c>(DT_DBTIMESTAMP)(@[System::StartTime])</c>)
    /// rather than reading it from the source, and that is reported as a gap rather than guessed at:
    /// resolving it would mean evaluating a branch Derived Column's expression inside a command
    /// parameter lambda, where the transform's own <c>ctx</c> does not exist, AND teaching
    /// <see cref="ExpressionTranslator"/> about <c>System::</c> variables -- two separate features,
    /// neither measured. A dimension that closes out its old row with SQL rather than a pipeline
    /// expression (<c>SET [EndDate] = GETDATE() WHERE [EmpId] = ?</c>) is fully supported today.</para>
    /// </summary>
    private static ProgramScdStep? GenerateScdFlow(
        PackageSpec package, string ns, DataFlowPlan flow, ScdPlan scd,
        List<FileSourceEntryRequest> fileSourceEntries, List<GeneratedFile> files, List<GeneratedFile> testFiles,
        List<TestCoverageNote> testCoverageNotes,
        List<DbContextEmitter.TableSpec> tables, HashSet<string> functionsUsed,
        Dictionary<string, PrimaryKeyCandidateSpec> primaryKeysByDestination, List<GenerationGap> gaps,
        ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase,
        bool emitSeams, List<CsvSampleCandidate>? csvSampleCandidates = null,
        ComponentHolderRegistry? componentHolders = null,
        Dictionary<string, SecondaryConnectionRequest>? secondaryConnections = null)
    {
        var liveBranches = scd.LiveBranches().ToList();
        if (liveBranches.Count == 0)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"Slowly Changing Dimension '{scd.Component.Name}' has no connected outputs at all -- nothing downstream to generate"));
            return null;
        }

        // Phase A -- validate every branch before emitting anything.
        var firstSqlDestination = liveBranches.Select(b => b.Branch.Destination).FirstOrDefault(d => d is not null);
        foreach (var branchPlan in liveBranches)
        {
            var destination = branchPlan.Branch.Destination;
            if (destination is null)
            {
                if (branchPlan.Command is null)
                {
                    gaps.Add(new GenerationGap(flow.TaskName,
                        $"Slowly Changing Dimension branch '{branchPlan.Branch.OutputName}' reaches neither a destination nor an OLE DB Command -- not supported"));
                    return null;
                }
                continue;
            }

            if (destination.ComponentClassId == "Microsoft.FlatFileDestination")
            {
                gaps.Add(new GenerationGap(flow.TaskName,
                    $"Slowly Changing Dimension branch '{branchPlan.Branch.OutputName}' feeds Flat File Destination '{destination.Name}' -- only a SQL destination is supported for a dimension load"));
                return null;
            }

            if (!DestinationInfo.IsFastLoadConfigured(destination))
            {
                gaps.Add(new GenerationGap(flow.TaskName,
                    $"Slowly Changing Dimension branch '{branchPlan.Branch.OutputName}': destination '{destination.Name}' is not configured for fast load -- row-by-row insert generation is not supported yet"));
                return null;
            }
        }

        var flowBaseName = SanitizeIdentifier(flow.TaskName);
        var nullableColumnNames = ResolveNullableColumnNames(flow);
        var sourceResult = ResolveFlowSource(package, ns, flowBaseName, flow,
            firstSqlDestination, fileSourceEntries, files, gaps, nullableColumnNames, csvSampleCandidates, secondaryConnections);
        if (sourceResult is null) return null; // ResolveFlowSource already added the reason
        var (rowTypeName, programSource) = sourceResult.Value;
        var rowTypeNamespace = programSource is CsvFlowSource or FixedWidthFlowSource ? $"{ns}.Csv"
            : programSource is ExcelFlowSource ? $"{ns}.Excel"
            : programSource is XmlFlowSource ? $"{ns}.Xml"
            : $"{ns}.Sql";

        // Every column the classifier reads -- the business key and each compared attribute -- has
        // to exist on the resolved row type, or the generated selector would not compile. Checked
        // here, once, against the source's own resolved buffer columns rather than discovered at
        // build time.
        var sourceComponent = flow.OleDbSource ?? flow.ExcelSource ?? flow.FlatFileSource ?? flow.XmlSource;
        var sourceOutput = sourceComponent?.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        if (sourceOutput is null)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"Slowly Changing Dimension '{scd.Component.Name}' has no resolvable upstream source output -- not supported"));
            return null;
        }
        var rowColumnNames = PipelineResolver.Resolve(sourceOutput).Columns
            .Select(c => c.PipelineColumnName).ToHashSet(StringComparer.Ordinal);

        // The business key is matched via a plain C# ValueTuple/scalar key (TKey : notnull),
        // so it must exist directly on the raw source row -- a Data-Conversion-produced business
        // key is a distinct, unevidenced shape (its own nullable CLR type would conflict with
        // that constraint) and is deliberately left as a named future gap, not guessed at.
        var missingKeys = scd.BusinessKeyColumns.Where(c => !rowColumnNames.Contains(c)).ToList();
        if (missingKeys.Count > 0)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"Slowly Changing Dimension '{scd.Component.Name}' compares business key column(s) not present on its own source's output ({string.Join(", ", missingKeys)}) -- a column produced by an intervening transform is not supported for the business key (its nullable CLR type would conflict with the classifier's own notnull key constraint)"));
            return null;
        }

        // Each compared attribute is either a plain passthrough column (already on the raw source
        // row) or one produced by an intervening Data Conversion component -- resolved through the
        // SAME "DataConversion" lineage edge and SsisFn.ToNullable* wrapping RouterEmitter/Derived
        // Column cross-references already reuse (TransformEmitter.TranslateDataConversion), not
        // re-derived here. This is safe for an ATTRIBUTE (unlike the business key above): the
        // classifier's own attribute array is object?[], and ScdClassifier.ValuesEqual already
        // null-checks both sides -- a Data Conversion column is nullable by construction
        // (IgnoreFailure), which this generator already treats as an ordinary case elsewhere.
        var dataConvertColumnsByName = (flow.DataConversion?.DataConvert?.Columns ?? [])
            .ToDictionary(c => c.OutputColumnName, StringComparer.Ordinal);
        var scdLineage = LineageBuilder.Build(flow.Pipeline);
        var attributeExpressionsByName = new Dictionary<string, string>(StringComparer.Ordinal);
        var unresolvedAttributes = new List<string>();
        foreach (var attribute in scd.Attributes)
        {
            if (rowColumnNames.Contains(attribute.ColumnName))
            {
                attributeExpressionsByName[attribute.ColumnName] = $"row.{SanitizeIdentifier(attribute.ColumnName)}";
                continue;
            }

            if (dataConvertColumnsByName.TryGetValue(attribute.ColumnName, out var conversion))
            {
                // TranslateDataConversion always resolves the "DataConversion" lineage edge to
                // SOME producing column name and unconditionally emits a bare row.{name}
                // passthrough for it -- it has no way to know whether that name is actually a
                // real row property, so the check has to happen HERE, before calling it, not by
                // reading its return type. A raw source column is the simple, already-proven
                // case; anything else means the Data Conversion's own raw input is ITSELF a
                // Derived Column's computed output rather than a raw source column -- the real
                // evidenced package's own shape (dimcustomer.dtsx's static-literal "ssc" Derived
                // Column feeding "Copy of ssc"'s Data Conversion). Resolved by translating that
                // Derived Column's own expression first (reusing TranslateDerivedColumn -- the
                // mirror-image mechanism a Derived Column referencing a Data Conversion output
                // already reuses), then wrapping the result the same way TranslateDataConversion
                // wraps a plain row reference.
                var conversionEdge = scdLineage.Edges.FirstOrDefault(e => e.Kind == "DataConversion" && e.ToColumnName == attribute.ColumnName);
                if (conversionEdge is not null && rowColumnNames.Contains(conversionEdge.FromColumnName))
                {
                    if (TransformEmitter.TranslateDataConversion(conversion, attribute.ColumnName, scdLineage) is TranslatedOk translated)
                    {
                        TransformEmitter.CollectSsisFunctions(translated.CSharpExpression, functionsUsed);
                        // Fully-qualified, not "using {ns}.Ssis;" -- the SCD step's own key/
                        // attribute selectors live directly on the generated package class,
                        // which has no such using (same reasoning/precedent as the ExpressionStep
                        // case a few lines up).
                        attributeExpressionsByName[attribute.ColumnName] = translated.CSharpExpression.Replace("SsisFn.", $"{ns}.Ssis.SsisFn.");
                        continue;
                    }
                }
                else if (conversionEdge is not null && flow.DerivedColumn is not null)
                {
                    var derivedSourceColumn = flow.DerivedColumn.Outputs
                        .Where(o => o.IsErrorOut != true)
                        .SelectMany(o => o.Columns)
                        .FirstOrDefault(c => c.Name == conversionEdge.FromColumnName && c.Expression is not null);

                    if (derivedSourceColumn is not null)
                    {
                        var derivedExpression = new TransformEmitter.DerivedExpression(
                            derivedSourceColumn.Name, derivedSourceColumn.RefId, derivedSourceColumn.Expression,
                            derivedSourceColumn.FriendlyExpression, flow.DerivedColumn.Name, flow.DerivedColumn.RefId);
                        var scdColumnTypes = TransformEmitter.BuildColumnTypeLookup(flow.Pipeline);
                        var derivedTranslated = TransformEmitter.TranslateDerivedColumn(
                            derivedExpression, flow.TaskName, attribute.ColumnName, scdLineage, scdColumnTypes,
                            nullableColumnNames, dataConvertColumnsByName);

                        if (derivedTranslated is TranslatedOk derivedOk
                            && TransformEmitter.TranslateDataConversionFromRawExpression(conversion, derivedOk.CSharpExpression) is TranslatedOk chainedOk)
                        {
                            TransformEmitter.CollectSsisFunctions(chainedOk.CSharpExpression, functionsUsed);
                            attributeExpressionsByName[attribute.ColumnName] = chainedOk.CSharpExpression.Replace("SsisFn.", $"{ns}.Ssis.SsisFn.");
                            continue;
                        }
                    }
                }
            }

            unresolvedAttributes.Add(attribute.ColumnName);
        }

        if (unresolvedAttributes.Count > 0)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"Slowly Changing Dimension '{scd.Component.Name}' compares attribute column(s) not present on its own source's output and not resolvable through an intervening Data Conversion ({string.Join(", ", unresolvedAttributes)}) -- a column produced by another kind of transform is not supported here yet"));
            return null;
        }

        // Same restriction, for every branch's own command parameters -- see this method's own doc
        // comment for exactly which real shape this excludes and why it is a gap rather than a guess.
        foreach (var branchPlan in liveBranches)
        {
            if (branchPlan.Command is null) continue;
            var unresolved = branchPlan.Command.ParameterColumnNames.Where(p => !rowColumnNames.Contains(p)).ToList();
            if (unresolved.Count == 0) continue;

            gaps.Add(new GenerationGap(flow.TaskName,
                $"Slowly Changing Dimension branch '{branchPlan.Branch.OutputName}': OLE DB Command '{branchPlan.Command.Component.Name}' binds column(s) that are not on this flow's own source row ({string.Join(", ", unresolved)}) -- a parameter computed by a Derived Column on the branch (e.g. an EndDate stamped from a package variable) is not supported yet; computing it in the statement itself (SET [EndDate] = GETDATE()) is"));
            return null;
        }

        // Phase B -- emit, buffering everything so a later branch's failure leaves nothing behind.
        var branchFiles = new List<GeneratedFile>();
        var branchTables = new List<DbContextEmitter.TableSpec>();
        var branchFunctionsUsed = new HashSet<string>();
        var branchTestFiles = new List<GeneratedFile>();
        var emittedDestinationRefIds = new HashSet<string>(StringComparer.Ordinal);
        var programBranches = new List<ProgramScdBranch>();
        (string AuthMode, string? UserId, string? Server, string? Database)? resolvedAuth = null;

        var cacheClassName = $"{SanitizeIdentifier(scd.Component.Name)}Cache";
        Merge(branchFiles, gaps, ScdCacheEmitter.Emit(
            $"{ns}.Mapping", cacheClassName, scd.ReferenceSql, scd.BusinessKeyColumns, scd.BusinessKeyTypes,
            scd.Attributes.Select(a => a.ColumnName).ToList()));

        foreach (var branchPlan in liveBranches)
        {
            var destination = branchPlan.Branch.Destination;
            string? entityName = null;
            string? transformClassName = null;
            FlowSinkSpec? sink = null;

            if (destination is not null)
            {
                entityName = TryResolveEntityName(flow.TaskName, destination, gaps);
                if (entityName is null) return null;

                // Named after the SCD component and its own output, matching the 1-to-1
                // component-to-function convention Multicast's own branch transforms already use --
                // an SCD's output names are distinct by construction, so no collision counting.
                transformClassName = $"{SanitizeIdentifier(scd.Component.Name)}_{SanitizeIdentifier(branchPlan.Branch.OutputName)}";

                if (emittedDestinationRefIds.Add(destination.RefId))
                {
                    Merge(branchFiles, gaps, EntityEmitter.Emit($"{ns}.Model", entityName, destination, nullableColumnNames));
                    primaryKeysByDestination.TryGetValue(destination.RefId, out var primaryKey);
                    branchTables.Add(new DbContextEmitter.TableSpec(entityName, destination, primaryKey));
                    sink = new SqlFlowSink(destination.Name);
                }
                else
                {
                    // A convergence (two branches through one Union All to one destination) -- the
                    // earlier branch already emitted the entity/table/sink. Same type-correct
                    // placeholder GenerateMulticastFlow uses; PackageClassEmitter emits one sink
                    // method per distinct destination component regardless.
                    sink = new SqlFlowSink(destination.Name);
                }

                var derivedColumns = new List<PipelineComponentSpec>();
                if (flow.DerivedColumn is not null) derivedColumns.Add(flow.DerivedColumn);
                derivedColumns.AddRange(branchPlan.Branch.DerivedColumns);

                var transformResult = TransformEmitter.Emit(new TransformRequest(
                    EmitSeams: emitSeams,
                    MappingNamespace: $"{ns}.Mapping",
                    TransformClassName: transformClassName,
                    RowTypeNamespace: rowTypeNamespace,
                    RowTypeName: rowTypeName,
                    EntityNamespace: $"{ns}.Model",
                    EntityName: entityName,
                    SsisFnNamespace: $"{ns}.Ssis",
                    Pipeline: flow.Pipeline,
                    DerivedColumns: derivedColumns,
                    DataConversions: flow.DataConversion is null ? [] : [flow.DataConversion],
                    DestinationComponent: destination,
                    NullableColumnNames: nullableColumnNames,
                    Holders: componentHolders));
                Merge(branchFiles, gaps, transformResult.Result);
                foreach (var fn in transformResult.SsisFunctionsUsed) branchFunctionsUsed.Add(fn);
                if (transformResult.Result.Files.Count == 0) return null; // TransformEmitter already recorded why

                var branchTestResult = TransformTestEmitter.Emit(new TransformTestRequest(
                    TestNamespace: $"{package.ObjectName}.Tests",
                    TransformClassName: transformClassName,
                    MappingNamespace: $"{ns}.Mapping",
                    RowTypeNamespace: rowTypeNamespace,
                    RowTypeName: rowTypeName,
                    EntityNamespace: $"{ns}.Model",
                    EntityName: entityName,
                    Pipeline: flow.Pipeline,
                    DerivedColumns: derivedColumns,
                    DataConversions: flow.DataConversion is null ? [] : [flow.DataConversion],
                    DestinationComponent: destination));
                gaps.AddRange(branchTestResult.Gaps);
                branchTestFiles.AddRange(branchTestResult.Files);

                if (resolvedAuth is null && ResolveDatabaseAuth(package, destination) is { } auth) resolvedAuth = auth;
            }

            programBranches.Add(new ProgramScdBranch(
                branchPlan.Slot, branchPlan.Branch.OutputName, entityName, transformClassName, sink,
                branchPlan.Command?.SqlTemplate, branchPlan.Command?.ParameterColumnNames ?? []));
        }

        files.AddRange(branchFiles);
        tables.AddRange(branchTables);
        foreach (var fn in branchFunctionsUsed) functionsUsed.Add(fn);
        foreach (var f in branchTestFiles)
        {
            var final = f with { RelativePath = $"{package.ObjectName}.Tests/{f.RelativePath}" };
            testFiles.Add(final);
            NoteTest(testCoverageNotes, final, "Transform",
                "asserts each output column for one representative row; does not exercise NULL/boundary inputs beyond the chosen representative value.");
        }

        if (authMode is null && resolvedAuth is { } resolved)
            (authMode, userId, targetServer, targetDatabase) = resolved;

        // The routing decision itself is deliberately NOT given a generated starter test, for the
        // same reason Percentage Sampling's own router isn't: it is not a per-package expression to
        // assert, it is a fixed algorithm shared by every generated SCD, and its rules are pinned
        // independently (and far more thoroughly) by Etl.Core.Tests' own ScdClassifierTests -- one
        // test per measured rule, against no database at all.
        testCoverageNotes.Add(new TestCoverageNote(
            $"(none -- {scd.Component.Name})", scd.Component.Name, "Slowly Changing Dimension routing",
            "no generated starter test -- the classification rules are fixed, shared, and pinned by Etl.Core.Tests.ScdClassifierTests (one test per measured SSIS rule) rather than per package."));

        return new ProgramScdStep(
            flow.TaskName, programSource, rowTypeName, cacheClassName,
            scd.BusinessKeyColumns, scd.BusinessKeyTypes.Select(t => t.ClrTypeName).ToList(),
            scd.Attributes.Select(a => a.ColumnName).ToList(),
            scd.Attributes.Select(a => attributeExpressionsByName[a.ColumnName]).ToList(),
            scd.Attributes.Select(a => a.Role.ToString()).ToList(),
            scd.FailOnFixedAttributeChange, scd.UpdateChangingAttributeHistory,
            programBranches);
    }

    /// <summary>Resolves an OLE DB Command flow -- no destination component, no EF entity, no
    /// IBulkSink; the command runs once per source row via Etl.Core.Pipeline.
    /// OleDbCommandStep&lt;TRow&gt;, on the same shared connection/transaction as everything else
    /// in the package (IUnitOfWork.ExecuteSqlAsync). Validates that the command's own connection
    /// manager targets the SAME server/database as the flow's own source -- there is no
    /// destination here to compare against, so this is the one remaining place a second,
    /// untracked connection could otherwise slip through unnoticed.</summary>
    private static ProgramOleDbCommandStep? GenerateOleDbCommandFlow(
        PackageSpec package, string ns, DataFlowPlan flow, OleDbCommandPlan commandPlan,
        List<FileSourceEntryRequest> fileSourceEntries, List<GeneratedFile> files, List<GenerationGap> gaps,
        ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase,
        bool emitSeams, List<OleDbCommandTestCandidate>? testCandidates = null, List<CsvSampleCandidate>? csvSampleCandidates = null)
    {
        var sourceComponent = flow.OleDbSource ?? flow.ExcelSource ?? flow.FlatFileSource;
        if (sourceComponent is null)
        {
            gaps.Add(new GenerationGap(flow.TaskName, $"OLE DB Command '{commandPlan.Component.Name}' has no resolvable upstream source -- not supported"));
            return null;
        }

        var commandCmName = commandPlan.Component.OleDbCommand?.ConnectionName;
        var commandParsed = commandCmName is not null ? FindConnectionManager(package, commandCmName)?.Parsed : null;
        var sourceCmName = SourceInfo.ConnectionName(sourceComponent);
        var sourceParsed = sourceCmName is not null ? FindConnectionManager(package, sourceCmName)?.Parsed : null;
        if (commandParsed is null || sourceParsed is null || commandParsed.Server != sourceParsed.Server || commandParsed.Database != sourceParsed.Database)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"OLE DB Command '{commandPlan.Component.Name}' targets a different server/database than this flow's own source -- a second connection is not supported for a command-only flow"));
            return null;
        }

        var flowBaseName = SanitizeIdentifier(flow.TaskName);
        var sourceResult = ResolveFlowSource(package, ns, flowBaseName, flow, destinationForSqlCheck: null, fileSourceEntries, files, gaps, csvSampleCandidates: csvSampleCandidates);
        if (sourceResult is null) return null; // ResolveFlowSource already added the reason
        var (rowTypeName, programSource) = sourceResult.Value;

        // Every parameter must resolve to a real column on the resolved row type -- matched by
        // NAME (the command's own input column cachedName, which is itself cached FROM the
        // upstream source's own output column name at design time, confirmed real from the
        // evidenced XML) since there is no intervening transform between source and command in
        // any evidenced shape.
        var sourceOutput = sourceComponent.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        if (sourceOutput is null)
        {
            gaps.Add(new GenerationGap(flow.TaskName, $"Source '{sourceComponent.Name}' has no non-error output -- not supported"));
            return null;
        }
        var resolvedSource = PipelineResolver.Resolve(sourceOutput);
        var rowColumnNames = resolvedSource.Columns.Select(c => c.PipelineColumnName).ToHashSet(StringComparer.Ordinal);
        var unresolved = commandPlan.ParameterColumnNames.Where(p => !rowColumnNames.Contains(p)).ToList();
        if (unresolved.Count > 0)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"OLE DB Command '{commandPlan.Component.Name}' references column(s) not found on its own source's output ({string.Join(", ", unresolved)}) -- not supported"));
            return null;
        }

        // commandParsed is guaranteed non-null here -- the earlier server/database check already
        // required it to resolve (and to match the source's own connection).
        if (authMode is null)
        {
            (authMode, userId, targetServer, targetDatabase) = commandParsed!.AuthMode == "SqlLogin"
                ? ("SqlServer", commandParsed.UserId, commandParsed.Server, commandParsed.Database)
                : ("Windows", null, commandParsed.Server, commandParsed.Database);
        }

        // Row-type namespace mirrors ResolveFlowSource's own branch precedence exactly
        // (OleDbSource before ExcelSource before FlatFileSource -- the same order sourceComponent
        // itself was resolved with, above) rather than re-deriving it from rowTypeName's own
        // suffix, which is not guaranteed unique across source kinds.
        var rowTypeNamespace = flow.OleDbSource is not null ? $"{ns}.Sql"
            : flow.ExcelSource is not null ? $"{ns}.Excel"
            : $"{ns}.Csv";
        testCandidates?.Add(new OleDbCommandTestCandidate(
            flow.TaskName, rowTypeName, rowTypeNamespace, commandPlan.SqlTemplate, commandPlan.ParameterColumnNames, resolvedSource.Columns));

        return new ProgramOleDbCommandStep(flow.TaskName, programSource, rowTypeName, commandPlan.SqlTemplate, commandPlan.ParameterColumnNames);
    }

    /// <summary>
    /// Generates a Microsoft.Aggregate flow (GroupBy + Count) -- built speculatively 2026-08-30,
    /// see <see cref="PackagePlanner.AggregatePlan"/>'s own doc comment for why (the one real
    /// evidenced instance is unreachable, blocked by its own upstream Lookup regardless). The
    /// destination's own row type is the AGGREGATE's own output shape
    /// (<see cref="AggregateRowEmitter"/>, NOT the flow's raw source row -- an Aggregate's own
    /// output has no external metadata columns at all, the same reason <c>MergeJoinEmitter</c>
    /// resolves its own combined row type straight off the raw output column rather than through
    /// <see cref="PipelineResolver"/>), fed at runtime by <c>Etl.Core.Data.AggregateRowSource</c>
    /// wrapping the flow's own true SQL source. Scoped deliberately narrow: only a SQL-sourced
    /// flow (OLE DB/ADO NET Source) is supported -- a Flat File/Excel-sourced Aggregate is
    /// unevidenced and out of scope for this already-speculative round.
    /// </summary>
    /// <summary>
    /// Derives a Lookup's join key from the package itself: SSIS records it on the joining INPUT
    /// column, as a <c>JoinToReferenceColumn</c> custom property naming the reference-table column
    /// it matches against.
    ///
    /// <b>This corrects a long-standing wrong conclusion in this project</b> -- that "SSIS does not
    /// persist a Lookup's join key anywhere in the saved .dtsx". That was concluded from
    /// <c>SyntheticLookupSplit.dtsx</c>, a fixture built through the object model whose Lookup
    /// input columns were never mapped at all, so it genuinely had no join key to persist; absence
    /// in that one fixture was read as absence in the format. A real SSDT-authored Lookup
    /// (RBC_Demo_ETL's own <c>LKP_Country</c>: input <c>Country</c> -> reference
    /// <c>CountryName</c>) persists it, and the extractor was already capturing it generically via
    /// <c>PipelineInputColumnSpec.Properties</c> -- no reader change was needed to read it back.
    ///
    /// Returns null when no input column declares one, which is a real reachable case (that same
    /// synthetic fixture) -- the caller then falls back to a human-confirmed decision rather than
    /// guessing.
    /// </summary>
    private static (string InputColumn, string ReferenceColumn)? TryDeriveLookupJoinKey(PipelineComponentSpec lookup)
    {
        foreach (var column in lookup.Inputs.SelectMany(i => i.Columns))
        {
            var joinTo = column.Properties.FirstOrDefault(p => p.Name == "JoinToReferenceColumn")?.Value;
            if (!string.IsNullOrWhiteSpace(joinTo)) return (column.CachedName, joinTo);
        }
        return null;
    }

    /// <summary>
    /// Generates a full-cache Lookup flow from a resolved join key -- derived from the package by
    /// <see cref="TryDeriveLookupJoinKey"/>, or (only when the package genuinely declares none)
    /// human-confirmed via <see cref="GapDecisionSpec"/>. Everything else about a Lookup is in the
    /// saved .dtsx -- the reference query, the reference columns, and each output column's own
    /// <c>CopyFromReferenceColumn</c> -- so no part of the generated output is guessed.
    ///
    /// Classifies every routed (non-error) output first (2026-09-02): a dead-end
    /// <c>Microsoft.RowCount</c> -- one with no outgoing path of its own -- is a discarded,
    /// non-fatal branch (advisory gap, mirroring <c>ResolveBranch</c>'s own Multicast-branch
    /// advisory), not a hard failure; everything else is a candidate LIVE output, and exactly
    /// one must survive. This is what closes RBC_Demo_ETL's own DFT_LookupAndAggregate: its
    /// Lookup routes BOTH a Match output (live) and a No-Match output (a dead-end RowCount,
    /// discarded) -- see this file's own CLAUDE.md entry for the full real-evidence account.
    ///
    /// The live output's own next hop then decides the shape:
    /// - Goes straight to <see cref="DataFlowPlan.DestinationComponent"/> -- today's original,
    ///   still most-common shape, completely unchanged.
    /// - Goes into <see cref="DataFlowPlan.Multicast"/> (the SAME instance PlanMulticast already
    ///   resolved, including recognizing ITS own dead-end/Aggregate branches) with exactly one
    ///   live Multicast branch, feeding <see cref="DataFlowPlan.Aggregate"/> -- the new composed
    ///   shape, delegated to <see cref="GenerateLookupThenAggregateFlow"/>.
    /// - Anything else composed (Multicast leading straight to a destination with no Aggregate,
    ///   or more than one live Multicast branch) is unevidenced and gapped rather than guessed.
    /// </summary>
    private static ProgramFlowSpec? GenerateLookupFlow(
        PackageSpec package, string ns, DataFlowPlan flow, PipelineComponentSpec lookup, string cacheClassName,
        (string InputColumn, string ReferenceColumn) joinKey,
        List<FileSourceEntryRequest> fileSourceEntries, List<GeneratedFile> files, List<DbContextEmitter.TableSpec> tables,
        HashSet<string> functionsUsed, Dictionary<string, PrimaryKeyCandidateSpec> primaryKeysByDestination, List<GenerationGap> gaps,
        ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase,
        bool emitSeams, List<AggregateSourceTestCandidate>? testCandidates = null, List<CsvSampleCandidate>? csvSampleCandidates = null,
        ComponentHolderRegistry? componentHolders = null,
        Dictionary<string, SecondaryConnectionRequest>? secondaryConnections = null)
    {
        if (flow.DestinationComponent.ComponentClassId == "Microsoft.FlatFileDestination")
        {
            gaps.Add(new GenerationGap(flow.TaskName, $"Lookup '{lookup.Name}' feeds a Flat File Destination -- only a SQL destination is supported for a Lookup flow yet"));
            return null;
        }

        if (!DestinationInfo.IsFastLoadConfigured(flow.DestinationComponent))
        {
            gaps.Add(new GenerationGap(flow.TaskName, $"destination '{flow.DestinationComponent.Name}' is not configured for fast load -- row-by-row insert generation is not supported yet"));
            return null;
        }

        PipelineOutputSpec? matchOutput = null;
        PipelineComponentSpec? liveOutputNext = null;
        var needsFiltering = false;
        foreach (var output in lookup.Outputs.Where(o => o.IsErrorOut != true && flow.Pipeline.Paths.Any(p => p.StartId == o.RefId)))
        {
            var path = flow.Pipeline.Paths.First(p => p.StartId == output.RefId);
            var next = flow.Pipeline.Components.FirstOrDefault(c => c.Inputs.Any(i => i.RefId == path.EndId));

            if (next is not null && next.ComponentClassId == "Microsoft.RowCount"
                && !flow.Pipeline.Paths.Any(p => next.Outputs.Any(o2 => o2.RefId == p.StartId)))
            {
                var variableName = next.Properties.FirstOrDefault(p => p.Name == "VariableName")?.Value;
                gaps.Add(new GenerationGap(flow.TaskName,
                    $"Lookup '{lookup.Name}' output '{output.Name}' counts rows into '{variableName ?? "(unknown variable)"}' via RowCount '{next.Name}', which nothing else in this package reads -- this count is not reproduced in generated code",
                    IsBlocking: false));
                needsFiltering = true;
                continue;
            }

            if (matchOutput is not null)
            {
                gaps.Add(new GenerationGap(flow.TaskName,
                    $"Lookup '{lookup.Name}' routes more than one output to real downstream processing ('{matchOutput.Name}', '{output.Name}') -- only a single live routed output is supported yet"));
                return null;
            }
            matchOutput = output;
            liveOutputNext = next;
        }

        if (matchOutput is null)
        {
            gaps.Add(new GenerationGap(flow.TaskName, $"Lookup '{lookup.Name}' routes no output to real downstream processing -- not supported"));
            return null;
        }

        // Phase 3 (gap-audit plan concurrent-whistling-turing.md, 2026-09-16): the "no-match is
        // the live route" shape -- the classic "insert-if-new" dimension pattern (author_dim.dtsx/
        // address_dim.dtsx, both real, both identical in shape). Match is the discarded/unrouted
        // side, No-Match is the one live output. Detected by NAME, not by which side happened to
        // survive the discard loop above -- a Match output with literally no path at all (never
        // even routed to a dead-end RowCount) is simply never iterated, so matchOutput here can
        // legitimately BE the No-Match output.
        var liveOutputIsNoMatch = matchOutput.Name is not null && matchOutput.Name == lookup.Lookup?.NoMatchOutputName;

        // What the generated code does on a lookup MISS is decided entirely by needsFiltering:
        // with it, FilteringRowSource drops the row before the transform ever sees it; without
        // it, the transform indexes the cache (lookup[key]) and therefore THROWS. Neither is
        // right for every setting, and nothing here used to check which setting is in force --
        // NoMatchBehaviorRaw had no reader anywhere in this project.
        //
        // Measured/evidenced meanings:
        //   0  fail the component on a miss. Measured via dtexec: the package returns
        //      DTSER_FAILURE and loads nothing once one unmatched row is present. A throwing
        //      indexer is the faithful translation.
        //   1  redirect misses to the No Match output. Evidenced on every real routed-no-match
        //      Lookup in the tracked corpus, including the third-party Package_Transforms.
        //      Faithful ONLY when those misses are provably excluded from the row stream.
        //   2  ignore the failure and pass NULLs for the looked-up columns. Unevidenced
        //      anywhere, and not reproducible here: it needs every Lookup-added column made
        //      nullable, which is a real feature, not a mapping.
        var noMatch = lookup.Lookup?.NoMatchBehaviorRaw;
        switch (noMatch)
        {
            case 0 when needsFiltering:
                gaps.Add(new GenerationGap(flow.TaskName,
                    $"Lookup '{lookup.Name}' has NoMatchBehavior=0 (fail on no match), so SSIS fails the package on a miss -- but this flow's no-match output is a discarded dead end, which would make generated code silently DROP those rows instead. Refusing rather than changing failure into data loss."));
                return null;

            case 0:
                break; // throwing indexer == SSIS failing the component

            case 1 when liveOutputIsNoMatch:
                // Phase 3: no-match is the live route. Filtering is required regardless of
                // whether Match happened to be a discarded RowCount dead end or entirely
                // unrouted -- every row reaching the destination must be a genuine miss.
                needsFiltering = true;
                break;

            case 1 when needsFiltering:
                break; // misses provably excluded from the stream

            case 1:
                gaps.Add(new GenerationGap(flow.TaskName,
                    $"Lookup '{lookup.Name}' has NoMatchBehavior=1 (redirect no-match rows), but this flow gives no route for them that generated code can reproduce -- so a miss would THROW where SSIS quietly redirects it. Connect the no-match output, or expect this gap."));
                return null;

            default:
                gaps.Add(new GenerationGap(flow.TaskName,
                    $"Lookup '{lookup.Name}' has NoMatchBehavior={(noMatch?.ToString() ?? "(absent)")}. Only 0 (fail) and 1 (redirect) are evidenced and measured; 2 (ignore failure, pass NULLs) would need every Lookup-added column made nullable, and generated code would instead throw on the first miss."));
                return null;
        }

        // The generated cache cannot error on a duplicated reference key -- it keeps the first
        // occurrence, matching measured SSIS behaviour for TreatDuplicateKeysAsError=false. If a
        // package asks for the error, say so instead of ignoring the request.
        if (lookup.Lookup?.TreatDuplicateKeysAsError == true)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"Lookup '{lookup.Name}' sets TreatDuplicateKeysAsError=true, but the generated reference cache cannot fail on a duplicated key -- it keeps the first occurrence (the measured behaviour when this setting is false). Not supported."));
            return null;
        }

        // The join key column has to exist on the Lookup's own input, or the confirmed decision
        // does not describe this package -- a stale decision reaching generation is exactly what
        // must fail loudly rather than produce code referencing a column that isn't there.
        var inputColumn = lookup.Inputs.SelectMany(i => i.Columns)
            .FirstOrDefault(c => c.CachedName == joinKey.InputColumn);
        if (inputColumn is null)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"the confirmed join key names input column '{joinKey.InputColumn}', which Lookup '{lookup.Name}' does not have (its input columns are: {string.Join(", ", lookup.Inputs.SelectMany(i => i.Columns).Select(c => $"'{c.CachedName}'"))}) -- re-review the decision against the current package"));
            return null;
        }

        var keyType = SsisPipelineTypeMap.Resolve(inputColumn.CachedDataType);
        if (keyType is null)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"join key column '{joinKey.InputColumn}' has unmapped pipeline data type '{inputColumn.CachedDataType}'"));
            return null;
        }

        if (lookup.Lookup?.ReferenceColumns.Any(c => c.Name == joinKey.ReferenceColumn) != true)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"the confirmed join key names reference column '{joinKey.ReferenceColumn}', which is not in Lookup '{lookup.Name}''s own reference query result set -- re-review the decision against the current package"));
            return null;
        }

        // Each added column's source reference column IS persisted, on the output column's own
        // CopyFromReferenceColumn custom property -- so only the join key ever needed a human.
        var outputToReference = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in matchOutput.Columns)
        {
            var copyFrom = column.Properties.FirstOrDefault(p => p.Name == "CopyFromReferenceColumn")?.Value;
            if (!string.IsNullOrWhiteSpace(copyFrom)) outputToReference[column.Name] = copyFrom;
        }

        var entityName = TryResolveEntityName(flow.TaskName, flow.DestinationComponent, gaps);
        if (entityName is null) return null;

        // The live output's own next hop tells us which shape this is. flow.Multicast, when
        // populated, is the SAME instance PackagePlanner.PlanMulticast already fully resolved
        // (including recognizing its own dead-end/Aggregate branches) -- reused directly rather
        // than re-walked here, since ResolveBranch's hop-walk assumes a single linear chain per
        // hop and a Multicast is a genuine fan-out.
        var viaMulticast = flow.Multicast is not null && liveOutputNext is not null && liveOutputNext.RefId == flow.Multicast.Component.RefId;
        if (viaMulticast)
        {
            var liveMulticastBranches = flow.Multicast!.Branches.Where(b => !b.Discarded).ToList();
            if (liveMulticastBranches.Count != 1)
            {
                gaps.Add(new GenerationGap(flow.TaskName,
                    $"Multicast '{flow.Multicast.Component.Name}' downstream of Lookup '{lookup.Name}' has {liveMulticastBranches.Count} live branches -- only exactly one is supported for a Lookup+Multicast composed flow"));
                return null;
            }

            if (flow.Aggregate is { } aggregatePlan)
            {
                return GenerateLookupThenAggregateFlow(package, ns, flow, lookup, cacheClassName, joinKey, keyType,
                    outputToReference, aggregatePlan, entityName, fileSourceEntries, files, tables, functionsUsed,
                    primaryKeysByDestination, gaps, ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams,
                    testCandidates, csvSampleCandidates);
            }

            // Not evidenced anywhere (the one real case always goes through an Aggregate) --
            // gapped rather than guessed at wiring a plain passthrough through a Multicast.
            gaps.Add(new GenerationGap(flow.TaskName,
                $"Lookup '{lookup.Name}''s live output leads through Multicast '{flow.Multicast.Component.Name}' straight to a destination with no Aggregate downstream -- this composed shape is not evidenced and not supported yet"));
            return null;
        }

        if (needsFiltering && !liveOutputIsNoMatch)
        {
            // A Lookup that redirects its no-match rows away, but whose live output goes
            // straight to the destination with no Multicast at all, is also not evidenced --
            // gapped rather than guessed at wiring a FilteringRowSource wrap for a shape nothing
            // has ever exercised. (The liveOutputIsNoMatch case -- Phase 3's own "no-match is
            // live" shape -- is handled below, straight to the destination, no Multicast needed.)
            gaps.Add(new GenerationGap(flow.TaskName,
                $"Lookup '{lookup.Name}' redirects its no-match output away, but its live output goes straight to the destination with no Multicast/Aggregate downstream -- this shape is not evidenced and not supported yet"));
            return null;
        }

        var nullableColumnNames = ResolveNullableColumnNames(flow);
        var sourceResult = ResolveFlowSource(package, ns, entityName, flow, flow.DestinationComponent, fileSourceEntries, files, gaps, nullableColumnNames, csvSampleCandidates, secondaryConnections);
        if (sourceResult is null) return null; // ResolveFlowSource already added the reason
        var (rowTypeName, rawProgramSource) = sourceResult.Value;

        // Phase 3: wrap the raw source in a filter keeping only genuine misses (see
        // LookupNoMatchFilteredFlowSource's own doc comment) -- every row reaching the
        // destination in this shape must be one the Lookup did NOT find a reference row for.
        var cacheVariableName = LowerFirst(cacheClassName);
        var programSource = liveOutputIsNoMatch
            ? new LookupNoMatchFilteredFlowSource(lookup.Name, rawProgramSource, cacheVariableName, joinKey.InputColumn)
            : rawProgramSource;

        Merge(files, gaps, EntityEmitter.Emit($"{ns}.Model", entityName, flow.DestinationComponent, nullableColumnNames));

        var transformClassName = entityName + "Transform";
        var transformResult = TransformEmitter.Emit(new TransformRequest(
            EmitSeams: emitSeams,
            MappingNamespace: $"{ns}.Mapping",
            TransformClassName: transformClassName,
            RowTypeNamespace: rowTypeName.EndsWith("CsvRow", StringComparison.Ordinal) ? $"{ns}.Csv" : $"{ns}.Sql",
            RowTypeName: rowTypeName,
            EntityNamespace: $"{ns}.Model",
            EntityName: entityName,
            SsisFnNamespace: $"{ns}.Ssis",
            Pipeline: flow.Pipeline,
            DerivedColumns: flow.DerivedColumn is null ? [] : [flow.DerivedColumn],
            DataConversions: flow.DataConversion is null ? [] : [flow.DataConversion],
            DestinationComponent: flow.DestinationComponent,
            NullableColumnNames: nullableColumnNames,
            // Phase 3's own no-match-is-live shape never copies a reference column (a miss has no
            // reference row to copy from -- outputToReference is empty by construction for this
            // shape), so the transform needs no cache parameter at all; passing LookupJoin: null
            // here (rather than an empty-mapping LookupJoinSpec) is what lets
            // TransformNeedsLookupCache: false below actually match the generated constructor.
            LookupJoin: liveOutputIsNoMatch ? null : new LookupJoinSpec(cacheClassName, "lookup", joinKey.InputColumn, keyType.ClrTypeName, outputToReference),
            Holders: componentHolders));
        Merge(files, gaps, transformResult.Result);
        foreach (var fn in transformResult.SsisFunctionsUsed) functionsUsed.Add(fn);

        if (transformResult.Result.Files.Count == 0) return null; // TransformEmitter already recorded why

        // TransformTestEmitter has no knowledge of a Lookup-added reference column at all --
        // naively calling it here risks emitting an assertion assigning a row property that
        // doesn't exist (the same CS0117 failure class its own doc comment already warns about
        // for a Data-Conversion cross-reference). Reported as a visible gap instead of attempted,
        // and only when there's actually a Derived Column/Data Conversion downstream of this
        // Lookup for it to be missing a test for -- an unconditional gap here would fire on every
        // Lookup flow regardless, the same miscounting shape PortfolioDigest.IsBlockingGap's own
        // design already exists to prevent.
        if (flow.DerivedColumn is not null || flow.DataConversion is not null)
        {
            gaps.Add(new GenerationGap(entityName,
                $"'{transformClassName}' has a Derived Column/Data Conversion downstream of Lookup '{lookup.Name}', but no starter test was generated for it -- TransformTestEmitter does not yet cover a Lookup flow's own downstream transform (its reference-column mapping is a third computed-column mechanism this pilot has no representative-value strategy for). Add an assertion by hand.",
                IsBlocking: false, Kind: GapKind.TestOracle, EvidenceRefId: lookup.RefId));
        }

        primaryKeysByDestination.TryGetValue(flow.DestinationComponent.RefId, out var primaryKey);
        tables.Add(new DbContextEmitter.TableSpec(entityName, flow.DestinationComponent, primaryKey));

        if (authMode is null && ResolveDatabaseAuth(package, flow.DestinationComponent) is { } auth)
            (authMode, userId, targetServer, targetDatabase) = auth;

        var preload = new LookupPreload(
            VariableName: cacheVariableName,
            CacheClassName: cacheClassName,
            KeyClrType: keyType.ClrTypeName,
            ReferenceKeyColumn: joinKey.ReferenceColumn);

        // Phase 3: the no-match-is-live shape's transform takes no cache parameter at all (see
        // the LookupJoin: null comment above) -- the cache is still preloaded (needed by the
        // FilteringRowSource wrap around programSource), just never passed into the transform.
        return new ProgramFlowSpec(flow.TaskName, programSource, rowTypeName, entityName, transformClassName,
            new SqlFlowSink(flow.DestinationComponent.Name), preload, TransformNeedsLookupCache: !liveOutputIsNoMatch);
    }

    // internal, not private -- reused by TransformEmitter for a Script-Component combined-seam
    // local variable name (SanitizeIdentifier gives the PascalCase method/type name; this gives
    // the camelCase local that calls it), same cross-emitter sharing precedent as
    // CollectSsisFunctions/MapPipelineTypeToSsisType.
    internal static string LowerFirst(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static ProgramFlowSpec? GenerateAggregateFlow(
        PackageSpec package, string ns, DataFlowPlan flow, AggregatePlan aggregate,
        List<FileSourceEntryRequest> fileSourceEntries, List<GeneratedFile> files, List<DbContextEmitter.TableSpec> tables,
        HashSet<string> functionsUsed, Dictionary<string, PrimaryKeyCandidateSpec> primaryKeysByDestination, List<GenerationGap> gaps,
        ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase,
        bool emitSeams, List<AggregateSourceTestCandidate>? testCandidates = null)
    {
        if (flow.DestinationComponent.ComponentClassId == "Microsoft.FlatFileDestination")
        {
            gaps.Add(new GenerationGap(flow.TaskName, $"Aggregate '{aggregate.Component.Name}' feeds a Flat File Destination -- only a SQL destination is supported for an Aggregate flow yet"));
            return null;
        }

        if (!DestinationInfo.IsFastLoadConfigured(flow.DestinationComponent))
        {
            gaps.Add(new GenerationGap(flow.TaskName, $"destination '{flow.DestinationComponent.Name}' is not configured for fast load -- row-by-row insert generation is not supported yet"));
            return null;
        }

        if (flow.OleDbSource is not { } sourceComponent)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"Aggregate '{aggregate.Component.Name}': only a SQL-sourced flow (OLE DB/ADO NET Source) is supported -- a Flat File/Excel-sourced Aggregate flow is not evidenced and not supported yet"));
            return null;
        }

        var entityName = TryResolveEntityName(flow.TaskName, flow.DestinationComponent, gaps);
        if (entityName is null) return null;

        var sourceOutput = sourceComponent.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        var resolvedSourceColumnsForGroupBy = sourceOutput is null ? null : PipelineResolver.Resolve(sourceOutput).Columns;
        var groupByFields = new List<AggregateGroupByFieldSpec>();
        foreach (var groupByColumn in aggregate.GroupByColumns)
        {
            var groupByType = resolvedSourceColumnsForGroupBy?.FirstOrDefault(c => c.PipelineColumnName == groupByColumn.SourceColumnName)?.Type;
            if (groupByType is null)
            {
                gaps.Add(new GenerationGap(flow.TaskName,
                    $"Aggregate '{aggregate.Component.Name}': GroupBy source column '{groupByColumn.SourceColumnName}' could not be resolved to a CLR type"));
                return null;
            }
            groupByFields.Add(new AggregateGroupByFieldSpec(groupByColumn.OutputColumnName, groupByType.ClrTypeName));
        }

        // The source row's own aggregated column(s) are nullable BY CONSTRUCTION -- every
        // measured function (Count/CountDistinct/Sum/Average/Minimum/Maximum) excludes NULL from
        // its own computation (see AggregatePayload's own doc comment), so the generated
        // aggregation expression must compile against a genuinely nullable property, not an
        // inferred-non-nullable one. CountAll's own column, when it has one, is included too --
        // harmless, since CountAll never actually reads the value, only counts the row.
        var sourceNullableColumnNames = new HashSet<string>(
            aggregate.Functions.Select(f => f.SourceColumnName).Where(n => n is not null)!);

        var sourceRowBaseName = SanitizeIdentifier(aggregate.Component.Name) + "Source";
        var sourceResult = ResolveFlowSource(package, ns, sourceRowBaseName, flow, flow.DestinationComponent, fileSourceEntries, files, gaps, sourceNullableColumnNames);
        if (sourceResult is null) return null; // ResolveFlowSource already added the reason
        var (sourceRowTypeName, innerSource) = sourceResult.Value;

        var emitted = EmitAggregateRowEntityAndTransform(package, ns, flow, aggregate, innerSource, sourceRowTypeName,
            groupByFields, entityName, lookupKey: null, lookupFilter: null, files, tables, functionsUsed,
            primaryKeysByDestination, gaps, ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams);
        if (emitted is null) return null;

        // "Aggregate source" starter test (Phase 2 of the generated-tests plan): each function's
        // own source column CLR type resolved from the SAME PipelineResolver.Resolve call already
        // done above for the GroupBy column(s) -- a function with no resolvable column is simply
        // skipped, matching EmitAggregateSourceTest's own "no test, no gap" degrade. Its own
        // template assumes exactly one GroupBy column (a plain TKey, never a tuple) -- scoped out
        // here the same way, rather than widening a test template under time pressure; a
        // multi-GroupBy Aggregate flow generates and runs correctly, just without a starter test.
        if (groupByFields.Count == 1)
        {
            var resolvedSourceColumns = resolvedSourceColumnsForGroupBy!;
            var testFunctions = aggregate.Functions
                .Where(f => f.SourceColumnName is not null)
                .Select(f => (Function: f, Type: resolvedSourceColumns.FirstOrDefault(c => c.PipelineColumnName == f.SourceColumnName)?.Type))
                .Where(x => x.Type is not null)
                .Select(x => new ComponentTestEmitter.AggregateSourceTestFunction(x.Function.OutputColumnName, x.Function.SourceColumnName!, x.Type!.ClrTypeName, x.Function.AggregationTypeRaw))
                .ToList();
            testCandidates?.Add(new AggregateSourceTestCandidate(
                aggregate.Component.Name, $"{ns}.Sql", sourceRowTypeName, $"{ns}.Sql", emitted.Value.RowTypeName,
                groupByFields[0].OutputPropertyName, groupByFields[0].ClrType, testFunctions));
        }

        return new ProgramFlowSpec(flow.TaskName, emitted.Value.Source, emitted.Value.RowTypeName, entityName, emitted.Value.TransformClassName, new SqlFlowSink(flow.DestinationComponent.Name));
    }

    /// <summary>
    /// Generates a Lookup+Aggregate composed flow (2026-09-02) -- the real evidenced shape
    /// (RBC_Demo_ETL's own DFT_LookupAndAggregate): a Lookup's live (Match) output feeds a
    /// Multicast whose one live branch feeds an Aggregate, with every other Lookup/Multicast
    /// output already classified as a discarded RowCount dead end by <see cref="GenerateLookupFlow"/>.
    ///
    /// The Aggregate's own GroupBy source column is usually the Lookup's own copied reference
    /// column (e.g. "Region", from <paramref name="outputToReference"/> -- built from the Match
    /// output's own CopyFromReferenceColumn properties, exactly like the plain single-output
    /// Lookup path already uses), not a plain raw source-row property at all -- resolved here via
    /// the Lookup's own reference query result set, the same type-resolution
    /// <c>LookupCacheEmitter</c> already uses for every OTHER reference column. Falls back to the
    /// raw source's own column (the same resolution <see cref="GenerateAggregateFlow"/> uses) when
    /// it isn't a Lookup-copied column, so a GroupBy on a genuinely plain passthrough column
    /// downstream of this Lookup still works.
    ///
    /// A Lookup miss is EXCLUDED from the aggregate entirely (never grouped under a null/sentinel
    /// key) whenever <see cref="GenerateLookupFlow"/> found a discarded no-match branch --
    /// independent of whether the GroupBy value itself is Lookup-derived, see
    /// <see cref="AggregateFlowSource.LookupFilter"/>'s own doc comment.
    /// </summary>
    private static ProgramFlowSpec? GenerateLookupThenAggregateFlow(
        PackageSpec package, string ns, DataFlowPlan flow, PipelineComponentSpec lookup, string cacheClassName,
        (string InputColumn, string ReferenceColumn) joinKey, SsisPipelineType keyType,
        Dictionary<string, string> outputToReference, AggregatePlan aggregate, string entityName,
        List<FileSourceEntryRequest> fileSourceEntries, List<GeneratedFile> files, List<DbContextEmitter.TableSpec> tables,
        HashSet<string> functionsUsed, Dictionary<string, PrimaryKeyCandidateSpec> primaryKeysByDestination, List<GenerationGap> gaps,
        ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase, bool emitSeams,
        List<AggregateSourceTestCandidate>? testCandidates = null, List<CsvSampleCandidate>? csvSampleCandidates = null)
    {
        // Deliberately scoped to exactly one GroupBy column -- widening this composed shape to
        // several is a materially bigger, separate effort: the one real evidenced multi-GroupBy
        // Aggregate (DWH - FactOrders' own AGG_order_id, 10 GroupBy columns fed by FIVE
        // independent upstream Lookups, mixing Lookup-derived and plain-passthrough columns) is
        // nothing like the single-Lookup-then-Multicast-then-Aggregate shape this method was
        // built for, and would need per-column Lookup-vs-plain resolution plus a tuple key that
        // can mix both kinds -- not attempted here. The standalone (non-Lookup) path, see
        // GenerateAggregateFlow, already supports N GroupBy columns.
        if (aggregate.GroupByColumns.Count != 1)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"Aggregate '{aggregate.Component.Name}' downstream of Lookup '{lookup.Name}' has {aggregate.GroupByColumns.Count} GroupBy column(s) -- only exactly one is supported when a Lookup is involved"));
            return null;
        }
        var groupByColumn = aggregate.GroupByColumns[0];

        var lookupVariableName = LowerFirst(cacheClassName);

        AggregateLookupKeySpec? lookupKey = null;
        string? groupByClrType = null;
        if (outputToReference.TryGetValue(groupByColumn.SourceColumnName, out var groupByReferenceColumn))
        {
            var referenceColumnSpec = lookup.Lookup!.ReferenceColumns.FirstOrDefault(c => c.Name == groupByReferenceColumn);
            var resolved = referenceColumnSpec is null ? null : SsisPipelineTypeMap.Resolve(CsvRowEmitter.ToPipelineTypeKey(referenceColumnSpec.DataType ?? ""));
            if (resolved is not null)
            {
                groupByClrType = resolved.ClrTypeName;
                lookupKey = new AggregateLookupKeySpec(lookupVariableName, joinKey.InputColumn, groupByReferenceColumn);
            }
        }
        if (groupByClrType is null && flow.OleDbSource is { } sqlSourceComponent)
        {
            var sourceOutput = sqlSourceComponent.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
            groupByClrType = sourceOutput is null
                ? null
                : PipelineResolver.Resolve(sourceOutput).Columns.FirstOrDefault(c => c.PipelineColumnName == groupByColumn.SourceColumnName)?.Type?.ClrTypeName;
        }
        if (groupByClrType is null)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"Aggregate '{aggregate.Component.Name}': GroupBy source column '{groupByColumn.SourceColumnName}' could not be resolved to a CLR type (neither a Lookup-copied reference column nor a plain source column)"));
            return null;
        }

        var sourceNullableColumnNames = new HashSet<string>(
            aggregate.Functions.Select(f => f.SourceColumnName).Where(n => n is not null)!);
        var sourceRowBaseName = SanitizeIdentifier(aggregate.Component.Name) + "Source";
        var sourceResult = ResolveFlowSource(package, ns, sourceRowBaseName, flow, flow.DestinationComponent, fileSourceEntries, files, gaps, sourceNullableColumnNames, csvSampleCandidates);
        if (sourceResult is null) return null; // ResolveFlowSource already added the reason
        var (sourceRowTypeName, innerSource) = sourceResult.Value;

        // A discarded no-match branch means SOME rows genuinely miss the Lookup -- exclude them
        // from the aggregate entirely, regardless of whether lookupKey above is set (see
        // AggregateFlowSource.LookupFilter's own doc comment on why these are independent axes).
        (string LookupVariableName, string JoinInputPropertyName)? lookupFilter =
            (lookupVariableName, joinKey.InputColumn);

        var groupByFields = new List<AggregateGroupByFieldSpec> { new(groupByColumn.OutputColumnName, groupByClrType) };
        var emitted = EmitAggregateRowEntityAndTransform(package, ns, flow, aggregate, innerSource, sourceRowTypeName,
            groupByFields, entityName, lookupKey, lookupFilter, files, tables, functionsUsed,
            primaryKeysByDestination, gaps, ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams);
        if (emitted is null) return null;

        // "Aggregate source" starter test (same coverage the standalone GenerateAggregateFlow
        // already gets -- see its own identical block) -- a real, previously-latent gap: this
        // composed Lookup+Aggregate flow used to reach this point and simply never populate
        // testCandidates at all (silently no test AND no gap, unlike every other flow shape that
        // degrades to an honest advisory when it can't test something), caught 2026-09-06 by an
        // independent review against Docs/Generated-Tests-Plan.md.
        //
        // Gated on lookupKey being null -- i.e. the GroupBy column is a plain passthrough SOURCE
        // column, not a Lookup-copied reference one. EmitAggregateSourceTest's own template always
        // emits a bare `row => row.{GroupByOutputColumnName}` key selector, which is correct ONLY
        // for that plain-passthrough shape. When lookupKey IS set (the one real evidenced case,
        // SyntheticLookupThenAggregate.dtsx's own "GroupBy Region" -- Region only exists via
        // `lKP_CountryCache[row.Country].Region`, never as a bare row property), a naive reuse of
        // that same template was caught, by actually BUILDING the generated test (not by the gap
        // count, which showed nothing wrong), producing CS0117/CS1061 -- `row.Region` doesn't exist
        // on the raw source row at all. Rather than build a second, cache-aware test template under
        // time pressure, that case degrades to the same honest advisory GenerateLookupFlow's own
        // downstream-transform gap already uses, instead of risking a second wrong-guess test.
        if (lookupKey is not null)
        {
            gaps.Add(new GenerationGap(entityName,
                $"'{entityName}' groups by '{groupByColumn.OutputColumnName}', a Lookup-copied reference column (via '{lookup.Name}') -- no starter test was generated for it: EmitAggregateSourceTest's own template assumes the GroupBy value is a plain row property, which is wrong once it only resolves through the Lookup cache. Add an assertion by hand.",
                IsBlocking: false, Kind: GapKind.TestOracle, EvidenceRefId: aggregate.Component.RefId));
        }
        else if (innerSource is SqlFlowSource && flow.OleDbSource is { } lookupSourceComponent)
        {
            var lookupSourceOutput = lookupSourceComponent.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
            var lookupResolvedSourceColumns = lookupSourceOutput is null ? null : PipelineResolver.Resolve(lookupSourceOutput).Columns;
            if (lookupResolvedSourceColumns is not null)
            {
                var lookupTestFunctions = aggregate.Functions
                    .Where(f => f.SourceColumnName is not null)
                    .Select(f => (Function: f, Type: lookupResolvedSourceColumns.FirstOrDefault(c => c.PipelineColumnName == f.SourceColumnName)?.Type))
                    .Where(x => x.Type is not null)
                    .Select(x => new ComponentTestEmitter.AggregateSourceTestFunction(x.Function.OutputColumnName, x.Function.SourceColumnName!, x.Type!.ClrTypeName, x.Function.AggregationTypeRaw))
                    .ToList();
                testCandidates?.Add(new AggregateSourceTestCandidate(
                    aggregate.Component.Name, $"{ns}.Sql", sourceRowTypeName, $"{ns}.Sql", emitted.Value.RowTypeName,
                    groupByColumn.OutputColumnName, groupByClrType, lookupTestFunctions));
            }
        }

        var preload = new LookupPreload(
            VariableName: lookupVariableName,
            CacheClassName: cacheClassName,
            KeyClrType: keyType.ClrTypeName,
            ReferenceKeyColumn: joinKey.ReferenceColumn);

        // The transform is a plain TrustAllPassthroughColumns mapping (EmitAggregateRowEntityAndTransform
        // never passes a LookupJoin) -- the cache is already fully consumed upstream, by
        // AggregateFlowSource's own key selector/filter, so it takes no constructor argument.
        return new ProgramFlowSpec(flow.TaskName, emitted.Value.Source, emitted.Value.RowTypeName, entityName, emitted.Value.TransformClassName, new SqlFlowSink(flow.DestinationComponent.Name), preload, TransformNeedsLookupCache: false);
    }

    /// <summary>Shared core of an Aggregate flow's own row/entity/transform emission -- reused by
    /// both <see cref="GenerateAggregateFlow"/>'s standalone (SQL-sourced) shape and
    /// <see cref="GenerateLookupThenAggregateFlow"/>'s composed shape, which differ only in how
    /// the raw source and GroupBy CLR type are resolved before this point (and whether a Lookup
    /// cache is involved at all). Returns the ready-to-wrap <see cref="AggregateFlowSource"/>
    /// alongside the row type/transform class names <c>ProgramFlowSpec</c> needs, or null with
    /// the reason already added to <paramref name="gaps"/>.</summary>
    private static (AggregateFlowSource Source, string RowTypeName, string TransformClassName)? EmitAggregateRowEntityAndTransform(
        PackageSpec package, string ns, DataFlowPlan flow, AggregatePlan aggregate, FlowSourceSpec innerSource, string sourceRowTypeName,
        List<AggregateGroupByFieldSpec> groupByFields, string entityName, AggregateLookupKeySpec? lookupKey, (string LookupVariableName, string JoinInputPropertyName)? lookupFilter,
        List<GeneratedFile> files, List<DbContextEmitter.TableSpec> tables, HashSet<string> functionsUsed,
        Dictionary<string, PrimaryKeyCandidateSpec> primaryKeysByDestination, List<GenerationGap> gaps,
        ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase, bool emitSeams)
    {
        var aggregateRowTypeName = entityName + "AggregateRow";
        Merge(files, gaps, AggregateRowEmitter.Emit($"{ns}.Sql", aggregateRowTypeName, aggregate));

        var functions = aggregate.Functions
            .Select(f => new AggregateFunctionFieldSpec(f.OutputColumnName, f.SourceColumnName, f.AggregationTypeRaw))
            .ToList();
        var aggregateSource = new AggregateFlowSource(aggregate.Component.Name, innerSource, sourceRowTypeName,
            groupByFields, functions, lookupKey, lookupFilter);

        // The destination entity's own Sum/Average/Minimum/Maximum property must be nullable too
        // (see AggregateRowEmitter.NullableAggregationTypes' own doc comment for why) -- a plain
        // TrustAllPassthroughColumns assignment from the (now nullable) aggregate row property
        // would otherwise fail to compile.
        var entityNullableColumnNames = new HashSet<string>(
            aggregate.Functions.Where(f => AggregateRowEmitter.NullableAggregationTypes.Contains(f.AggregationTypeRaw)).Select(f => f.OutputColumnName));
        Merge(files, gaps, EntityEmitter.Emit($"{ns}.Model", entityName, flow.DestinationComponent, entityNullableColumnNames));

        var transformClassName = entityName + "Transform";
        var transformResult = TransformEmitter.Emit(new TransformRequest(
            EmitSeams: emitSeams,
            MappingNamespace: $"{ns}.Mapping",
            TransformClassName: transformClassName,
            RowTypeNamespace: $"{ns}.Sql",
            RowTypeName: aggregateRowTypeName,
            EntityNamespace: $"{ns}.Model",
            EntityName: entityName,
            SsisFnNamespace: $"{ns}.Ssis",
            Pipeline: flow.Pipeline,
            DerivedColumns: [],
            DataConversions: [],
            DestinationComponent: flow.DestinationComponent,
            // AggregateRowEmitter resolves aggregateRowTypeName's own properties independently
            // (straight off the Aggregate component's own output columns, not through pipeline
            // lineage) -- the same reasoning GenerateMergeJoinFlow's own call already
            // established for its own independently-resolved combined row type.
            TrustAllPassthroughColumns: true));
        Merge(files, gaps, transformResult.Result);
        foreach (var fn in transformResult.SsisFunctionsUsed) functionsUsed.Add(fn);

        if (transformResult.Result.Files.Count == 0) return null; // TransformEmitter already recorded why

        primaryKeysByDestination.TryGetValue(flow.DestinationComponent.RefId, out var primaryKey);
        tables.Add(new DbContextEmitter.TableSpec(entityName, flow.DestinationComponent, primaryKey));

        if (authMode is null && ResolveDatabaseAuth(package, flow.DestinationComponent) is { } auth)
            (authMode, userId, targetServer, targetDatabase) = auth;

        return (aggregateSource, aggregateRowTypeName, transformClassName);
    }

    /// <summary>Resolves an OLE DB or ADO NET Source into a SqlFlowSource -- dispatches to
    /// <see cref="BuildOleDbFlowSource"/> or <see cref="BuildAdoNetFlowSource"/> after the one
    /// check both share: a resolvable connection manager, targeting either the same
    /// server/database as the flow's own destination, or -- Phase 5, 2026-09-17, and only when
    /// <paramref name="secondaryConnections"/> is supplied -- a genuinely DIFFERENT one, resolved
    /// as a secondary connection (see <see cref="SqlFlowSource"/>'s own doc comment for the full
    /// design). <paramref name="secondaryConnections"/> defaults to null so every pre-existing
    /// call site keeps its unconditional gap unchanged; it is threaded through from the plain
    /// single-destination flow path AND <see cref="GenerateLookupFlow"/> (both real, evidenced
    /// shapes across the 15 cross-database instances found across the GitHub portfolios --
    /// `fact_sales.dtsx`'s own source sits behind a chain of Lookups, `dimcustomer`/
    /// `DailyETLMain`'s own 13 flows are plain single-destination -- see
    /// Tools/SsisExtractor/CLAUDE.md's own Phase 5 section). Conditional Split/Multicast/SCD/
    /// Aggregate/Merge Join's own two sides/Union's own sides/a ForEach-Data-Flow-Loop body have
    /// no real evidenced cross-database instance anywhere in the tracked corpus and are
    /// deliberately left gapped.
    /// </summary>
    /// <param name="columnAliases">Optional component-column-name -&gt; required-exposed-name map,
    /// supplied only by a Union All/Merge side whose own column names differ from the union's own
    /// output column names (the generated reader is keyed on the latter). Identity when omitted.
    /// </param>
    private static SqlFlowSource? BuildSqlFlowSource(
        PackageSpec package, string taskName, PipelineComponentSpec source,
        PipelineComponentSpec? destinationComponent, List<GenerationGap> gaps,
        IReadOnlyDictionary<string, string>? columnAliases = null,
        Dictionary<string, SecondaryConnectionRequest>? secondaryConnections = null)
    {
        var sourceCmName = SourceInfo.ConnectionName(source);
        if (sourceCmName is not { } cmName || FindConnectionManager(package, cmName) is not { } sourceCm
            || sourceCm.Parsed is not { } sourceParsed)
        {
            gaps.Add(new GenerationGap(taskName, $"Source '{source.Name}' has no resolvable connection manager"));
            return null;
        }

        string? secondaryConnectionManagerName = null;

        // A Flat File Destination has no server/database of its own to compare against at all --
        // its own connection is a file path, not a SQL connection -- so this check, which exists
        // to catch a SQL source/SQL destination pair that would silently need a second untracked
        // connection, doesn't apply: a Flat File Destination flow's SQL source is already the
        // only SQL connection this flow uses. A null destinationComponent (an OLE DB Command
        // flow, which has no destination component at all -- see OleDbCommandPlan's own doc
        // comment) is the same situation: the source's own connection is already the only SQL
        // connection this flow uses, so there's nothing to compare it against either.
        if (destinationComponent is not null && destinationComponent.ComponentClassId != "Microsoft.FlatFileDestination")
        {
            var destCmName = DestinationInfo.ConnectionName(destinationComponent);
            var destParsed = destCmName is not null ? FindConnectionManager(package, destCmName)?.Parsed : null;
            if (destParsed is null)
            {
                // Distinguished from the genuine cross-database mismatch below, 2026-09-17 --
                // this is a DIFFERENT, unrelated root cause (the same class of problem as an
                // unresolvable SOURCE connection manager above): the destination's own connection
                // reference never resolved at all (most often a project-scoped .conmgr the
                // project's own files never checked in -- see Phase 1's own "no resolvable
                // connection manager" fix). Conflating the two used to make a genuinely
                // unresolvable destination look like an unsupported cross-database source
                // (real example: fact_sales.dtsx, whose destination references
                // Project.ConnectionManagers[dest.book_sales] with no .conmgr anywhere in that
                // repo to resolve it against -- nothing this feature can fix without the missing
                // file).
                gaps.Add(new GenerationGap(taskName, $"Destination '{destinationComponent.Name}' has no resolvable connection manager -- cannot compare it against source '{source.Name}''s own server/database"));
                return null;
            }
            if (sourceParsed.Server != destParsed.Server || sourceParsed.Database != destParsed.Database)
            {
                if (secondaryConnections is null)
                {
                    gaps.Add(new GenerationGap(taskName, $"Source '{source.Name}' targets a different server/database than this flow's own destination -- a separate source connection is not supported yet"));
                    return null;
                }

                // Same dedup-by-connection-manager-name shape ResolveSqlStep's own write-side
                // case already established -- two flows against the same secondary database
                // still produce ONE appsettings.json entry and one top-level connection string.
                if (!secondaryConnections.ContainsKey(cmName))
                {
                    secondaryConnections[cmName] = sourceParsed.AuthMode == "SqlLogin"
                        ? new SecondaryConnectionRequest(cmName, "SqlServer", sourceParsed.UserId, sourceParsed.Server ?? "", sourceParsed.Database ?? "")
                        : new SecondaryConnectionRequest(cmName, "Windows", null, sourceParsed.Server ?? "", sourceParsed.Database ?? "");
                }
                secondaryConnectionManagerName = cmName;
            }
        }

        SqlFlowSource? result;
        if (source.OleDbSource is { } oleDbPayload)
        {
            result = BuildOleDbFlowSource(taskName, source.Name, oleDbPayload, gaps, columnAliases);
        }
        else
        {
            // The ADO NET path has never been run end to end (no fixture -- an SSIS object-model
            // limitation in this environment blocks building one), so it deliberately does not
            // gain an untested aliasing branch here: a rename is a named gap instead.
            if (RequiresRename(columnAliases))
            {
                var renames = string.Join(", ", columnAliases!.Where(kv => kv.Key != kv.Value).Select(kv => $"{kv.Key} -> {kv.Value}"));
                gaps.Add(new GenerationGap(taskName,
                    $"ADO NET Source '{source.Name}' feeds a Union All/Merge that maps its columns to differently-named output columns ({renames}) -- renaming is only supported for an OLE DB Source in OpenRowset mode, where this tool builds the SELECT itself"));
                return null;
            }
            result = BuildAdoNetFlowSource(taskName, source.Name, source.AdoNetSource!, gaps);
        }

        return result is not null && secondaryConnectionManagerName is not null
            ? result with { SecondaryConnectionManagerName = secondaryConnectionManagerName }
            : result;
    }

    /// <summary>Builds the actual SELECT text per AccessMode: 0 (OpenRowset) builds
    /// <c>SELECT [Ext] AS [Comp], ... FROM {OpenRowset}</c> from ColumnMappings, guaranteeing
    /// every column resolves later by the pipeline's own buffer column name; 2 (SqlCommand)
    /// emits the author's own SQL verbatim (this tool never rewrites recorded SQL text) plus a
    /// non-fatal gap stating the assumption that the result set's own column names/aliases
    /// already match the buffer's column names -- a fact this tool cannot verify from the spec
    /// alone. Any other AccessMode is a fatal gap.</summary>
    /// <summary>True when a Union All/Merge maps one of this side's columns to an output column
    /// of a DIFFERENT name. The generated reader is keyed on the union's own output column names
    /// and is shared by every side, so such a side would be read under names it does not expose:
    /// a throw from GetOrdinal at best, and -- for a SWAPPED mapping, where every name still
    /// resolves -- silently transposed values.</summary>
    private static bool RequiresRename(IReadOnlyDictionary<string, string>? aliases) =>
        aliases is not null && aliases.Any(kv => !string.Equals(kv.Key, kv.Value, StringComparison.Ordinal));

    private static SqlFlowSource? BuildOleDbFlowSource(string taskName, string sourceName, OleDbSourcePayload payload, List<GenerationGap> gaps,
        IReadOnlyDictionary<string, string>? columnAliases = null)
    {
        switch (payload.AccessMode)
        {
            case 0:
                if (string.IsNullOrEmpty(payload.OpenRowset) || payload.ColumnMappings.Count == 0)
                {
                    gaps.Add(new GenerationGap(taskName, $"OLE DB Source '{sourceName}' has AccessMode=0 (OpenRowset) but no OpenRowset/ColumnMappings to build a SELECT from"));
                    return null;
                }
                // A rename is refused here too, not aliased. Aliasing this generated SELECT is
                // the obvious fix and would be a one-line change -- but no fixture in this repo
                // has an OpenRowset-mode Union All/Merge side, so that branch could not be run
                // end to end, and an unexercised translation branch is precisely what this
                // audit is correcting elsewhere. Refuse until there is a fixture.
                if (RequiresRename(columnAliases))
                {
                    var openRowsetRenames = string.Join(", ", columnAliases!.Where(kv => kv.Key != kv.Value).Select(kv => $"{kv.Key} -> {kv.Value}"));
                    gaps.Add(new GenerationGap(taskName,
                        $"OLE DB Source '{sourceName}' feeds a Union All/Merge that maps its columns to differently-named output columns ({openRowsetRenames}). The generated reader is keyed on the union's own output column names, so this side would be read under the wrong names. Aliasing this generated SELECT would fix it, but that path has no fixture to verify it against yet."));
                    return null;
                }
                var columns = string.Join(", ", payload.ColumnMappings.Select(m => $"[{m.ExternalColumnName}] AS [{m.ComponentColumnName}]"));
                return new SqlFlowSource(sourceName, $"SELECT {columns} FROM {payload.OpenRowset}");

            case 2:
                if (string.IsNullOrEmpty(payload.SqlCommand))
                {
                    gaps.Add(new GenerationGap(taskName, $"OLE DB Source '{sourceName}' has AccessMode=2 (SQL command) but no SqlCommand text"));
                    return null;
                }
                // A rename can only be honoured by aliasing, and this tool never rewrites the
                // author's own SQL -- so a union side needing one is a real gap here, not an
                // advisory.
                if (RequiresRename(columnAliases))
                {
                    var renames = string.Join(", ", columnAliases!.Where(kv => kv.Key != kv.Value).Select(kv => $"{kv.Key} -> {kv.Value}"));
                    gaps.Add(new GenerationGap(taskName,
                        $"OLE DB Source '{sourceName}' uses a SqlCommand, but its Union All/Merge maps its columns to differently-named output columns ({renames}). Honouring that needs the SELECT list aliased, and this tool never rewrites the author's own SQL -- alias it in the package instead, or rename the union's output columns to match."));
                    return null;
                }
                gaps.Add(new GenerationGap(taskName, $"OLE DB Source '{sourceName}' uses a SqlCommand -- generated code assumes each result-set column is named/aliased exactly like the pipeline's own buffer column name; verify before running. This tool never rewrites the author's own SQL.", IsBlocking: false));
                return new SqlFlowSource(sourceName, payload.SqlCommand);

            default:
                gaps.Add(new GenerationGap(taskName, $"OLE DB Source '{sourceName}' has AccessMode={(payload.AccessMode?.ToString() ?? "null")} -- only OpenRowset (0) and SQL command (2) are supported yet"));
                return null;
        }
    }

    /// <summary>Matches "SELECT * FROM [SheetName$]" -- optionally without brackets, with a
    /// trailing semicolon, and, since gap-audit Phase 3.4 (2026-09-02), an optional trailing
    /// "WHERE ..." clause -- capturing the sheet reference (including its own trailing "$") and
    /// the raw WHERE text (if any, everything between WHERE and the end/semicolon).
    ///
    /// The column list itself must still be a bare "*" -- this is deliberately the only column-
    /// list shape this tool ever translates for an Excel Source, and it is a PERMANENT ceiling,
    /// not a temporary limitation: see <see cref="ExcelWhereClauseTranslator"/>'s own doc comment
    /// for why an explicit column list (narrowing or reordering) cannot be safely reproduced by
    /// <c>Etl.Core.Excel.ExcelRowSource</c>'s architecture at all, confirmed via a real raw
    /// OleDb probe. A JOIN or range address (e.g. "[Sheet1$A1:C10]") is likewise a named gap,
    /// never guessed -- <see cref="ExcelWhereClauseTranslator"/> is what actually translates the
    /// WHERE half, once matched here.</summary>
    [GeneratedRegex(@"^SELECT\s+\*\s+FROM\s+\[?(?<sheet>[^\[\]\s]+\$)\]?(?:\s+WHERE\s+(?<where>.+?))?\s*;?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ExcelSelectStarPattern();

    /// <summary>Resolves an Excel Source into an <see cref="ExcelFlowSource"/>. AccessMode=0
    /// (OpenRowset, i.e. a worksheet) is the one evidenced value across the tracked portfolio.
    /// AccessMode=2 (SqlCommand) -- built SPECULATIVELY 2026-08-30, no real package needs it --
    /// is accepted ONLY when the column list is a bare "*" (see <see cref="ExcelSelectStarPattern"/>'s
    /// own doc comment for why an explicit column list is a permanent, not temporary, ceiling),
    /// functionally equivalent to naming the worksheet directly (confirmed via a raw
    /// System.Data.OleDb probe against the real checked-in workbook: the real ACE OLEDB provider
    /// returns the identical 9 rows/3 columns for "SELECT * FROM [DripEligibility$]" as it does
    /// for the bare worksheet OpenRowset), optionally followed by a WHERE clause (gap-audit Phase
    /// 3.4, 2026-09-02 -- see <see cref="ExcelWhereClauseTranslator"/>). Any other Jet/ACE SQL
    /// shape (see <see cref="ExcelSourcePayload"/>'s own doc comment) is a named gap, never
    /// guessed. The worksheet's own header-row setting (Extended Properties=HDR=YES/NO) lives on
    /// the EXCEL connection manager, not the component -- resolved from
    /// <see cref="ParsedConnectionString.Extras"/>, the generic pass-through capture every
    /// OLEDB-shaped connection string already gets for a segment with no bespoke field. That
    /// capture has a real, benign quirk here: "Extended Properties=&quot;EXCEL 12.0
    /// XML;HDR=YES&quot;" contains an UNQUOTED-parser-visible ';' inside its own quoted value, so
    /// the generic ';'-split (which has no quote awareness) lands "HDR" as its own extras key
    /// with a trailing stray '"' -- convenient, not a bug to fix, since it means HDR is directly
    /// readable without any Excel-specific parsing.</summary>
    private static ExcelFlowSource? BuildExcelFlowSource(
        PackageSpec package, string taskName, PipelineComponentSpec source,
        List<FileSourceEntryRequest> fileSourceEntries, List<GenerationGap> gaps)
    {
        var payload = source.ExcelSource!;

        string worksheetName;
        string? whereFilter = null;
        if (payload.AccessMode is null or 0)
        {
            if (string.IsNullOrEmpty(payload.OpenRowset))
            {
                gaps.Add(new GenerationGap(taskName, $"Excel Source '{source.Name}' has AccessMode=0 (OpenRowset) but no OpenRowset (worksheet name) to read from"));
                return null;
            }
            worksheetName = payload.OpenRowset;
        }
        else if (payload.AccessMode == 2)
        {
            var match = string.IsNullOrEmpty(payload.SqlCommand) ? null : ExcelSelectStarPattern().Match(payload.SqlCommand.Trim());
            if (match is not { Success: true })
            {
                gaps.Add(new GenerationGap(taskName, $"Excel Source '{source.Name}' has AccessMode=2 (SqlCommand) with a query this tool cannot translate -- this generator's ExcelRowSource has no query engine, only a worksheet-name reader, so only 'SELECT * FROM [SheetName$]' optionally followed by a WHERE clause is supported; a column list, JOIN, or range address is not."));
                return null;
            }
            worksheetName = match.Groups["sheet"].Value;

            if (match.Groups["where"] is { Success: true } whereGroup)
            {
                var output = source.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
                var columnTypes = output is null
                    ? null
                    : PipelineResolver.Resolve(output).Columns
                        .Where(c => c.Type is not null)
                        .ToDictionary(c => c.PipelineColumnName, c => c.Type!.ClrTypeName, StringComparer.OrdinalIgnoreCase);
                if (columnTypes is null)
                {
                    gaps.Add(new GenerationGap(taskName, $"Excel Source '{source.Name}' has a WHERE clause but its own output columns could not be resolved to translate it against"));
                    return null;
                }

                var (expression, translateGap) = ExcelWhereClauseTranslator.Translate(whereGroup.Value, columnTypes);
                if (translateGap is not null)
                {
                    gaps.Add(new GenerationGap(taskName, $"Excel Source '{source.Name}' {translateGap}"));
                    return null;
                }
                whereFilter = expression;
            }
        }
        else
        {
            gaps.Add(new GenerationGap(taskName, $"Excel Source '{source.Name}' has AccessMode={payload.AccessMode} -- only OpenRowset (0) or a 'SELECT * FROM [SheetName$]' [WHERE ...] SqlCommand (2) are supported yet"));
            return null;
        }

        var connectionManager = payload.ConnectionName is { } cmName ? FindConnectionManager(package, cmName) : null;
        if (connectionManager is null)
        {
            gaps.Add(new GenerationGap(taskName, $"Excel Source '{source.Name}' has no resolvable connection manager"));
            return null;
        }

        var fileSourceKey = source.Name;
        fileSourceEntries.Add(BuildFileSourceEntry(fileSourceKey, connectionManager, gaps));

        var hasHeaderRow = true; // the one evidenced real instance is HDR=YES; absent Extended Properties entirely, this is a reasonable fallback, not a fact this tool has confirmed
        var hdrEntry = connectionManager.Parsed?.Extras.FirstOrDefault(kv => kv.Key.Equals("HDR", StringComparison.OrdinalIgnoreCase));
        if (hdrEntry is { Value.Length: > 0 } found)
        {
            hasHeaderRow = found.Value.TrimEnd('"').Equals("YES", StringComparison.OrdinalIgnoreCase);
        }

        return new ExcelFlowSource(source.Name, fileSourceKey, worksheetName, hasHeaderRow, whereFilter);
    }

    /// <summary>Resolves an XML Source into an <see cref="XmlFlowSource"/> -- Phase 5 of the
    /// unsupported-component-types plan. Both preconditions
    /// (<see cref="PackagePlanner"/> already validates them before ever populating
    /// <see cref="DataFlowPlan.XmlSource"/>) are re-checked here defensively, not because either
    /// is reachable via any evidenced path.
    ///
    /// Unlike <see cref="BuildExcelFlowSource"/>, there is no connection manager to resolve at
    /// all (see <see cref="XmlSourcePayload"/>'s own doc comment) -- <see cref="BuildFileSourceEntry"/>
    /// cannot be reused as-is (it splits a resolved <c>ConnectionManagerSpec</c>'s own
    /// <c>Parsed.FilePath</c>), so the literal <c>XMLData</c> path is split directly into the same
    /// folder/filename shape instead, the identical config-driven `File(key)` mechanism CSV/Excel
    /// already use.</summary>
    private static XmlFlowSource? BuildXmlFlowSource(
        string taskName, PipelineComponentSpec source, List<FileSourceEntryRequest> fileSourceEntries, List<GenerationGap> gaps)
    {
        var payload = source.XmlSource!;
        if (string.IsNullOrEmpty(payload.XmlDataPath))
        {
            gaps.Add(new GenerationGap(taskName, $"XML Source '{source.Name}' has no XMLData (literal file path) to read from"));
            return null;
        }

        var output = source.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        if (string.IsNullOrEmpty(output?.Name))
        {
            gaps.Add(new GenerationGap(taskName, $"XML Source '{source.Name}' has no main (non-error) output to determine its own repeating row element name from"));
            return null;
        }

        var fileSourceKey = source.Name;
        fileSourceEntries.Add(new FileSourceEntryRequest(fileSourceKey,
            Path.GetDirectoryName(payload.XmlDataPath) ?? "", Path.GetFileName(payload.XmlDataPath)));

        return new XmlFlowSource(source.Name, fileSourceKey, output.Name);
    }

    /// <summary>ADO NET Source's own AccessMode enum is not reverse-engineered (see
    /// AdoNetSourcePayload's own doc comment for why) -- branches on which of
    /// <see cref="AdoNetSourcePayload.SqlCommand"/>/<see cref="AdoNetSourcePayload.TableOrViewName"/>
    /// is actually populated instead, sidestepping the need to know the enum's real mapping.
    /// Same SqlCommand-verbatim/non-fatal-gap and OpenRowset-column-list shapes as
    /// <see cref="BuildOleDbFlowSource"/>, just sourced from the ADO NET payload's own field
    /// names/quoting.</summary>
    private static SqlFlowSource? BuildAdoNetFlowSource(string taskName, string sourceName, AdoNetSourcePayload payload, List<GenerationGap> gaps)
    {
        if (!string.IsNullOrEmpty(payload.SqlCommand))
        {
            gaps.Add(new GenerationGap(taskName, $"ADO NET Source '{sourceName}' uses a SqlCommand -- generated code assumes each result-set column is named/aliased exactly like the pipeline's own buffer column name; verify before running. This tool never rewrites the author's own SQL.", IsBlocking: false));
            return new SqlFlowSource(sourceName, payload.SqlCommand);
        }

        var tableName = AdoNetSupport.NormalizeTableName(payload.TableOrViewName);
        if (!string.IsNullOrEmpty(tableName) && payload.ColumnMappings.Count > 0)
        {
            var columns = string.Join(", ", payload.ColumnMappings.Select(m => $"[{m.ExternalColumnName}] AS [{m.ComponentColumnName}]"));
            return new SqlFlowSource(sourceName, $"SELECT {columns} FROM {tableName}");
        }

        gaps.Add(new GenerationGap(taskName, $"ADO NET Source '{sourceName}' has neither a SqlCommand nor a TableOrViewName/ColumnMappings to build a SELECT from"));
        return null;
    }

    /// <summary>Design-time default file path, split into folder + file name for
    /// appsettings.json. Both real PoC packages show why this can't always be filled in: every
    /// CSV connection manager here is expression-driven off a project parameter (CLAUDE.md's
    /// own note), and only LoadEmployees' CM_EmployeesCsv happens to still carry a design-time
    /// default in ConnectionString for the Parsed.FilePath extraction to find. When there is
    /// none, a placeholder is emitted and the gap says exactly why, rather than guessing a
    /// path that was never in the package.</summary>
    private static FileSourceEntryRequest BuildFileSourceEntry(string key, ConnectionManagerSpec connectionManager, List<GenerationGap> gaps)
    {
        var filePath = connectionManager.Parsed?.FilePath;
        if (string.IsNullOrEmpty(filePath))
        {
            gaps.Add(new GenerationGap($"FileSource.{key}", $"connection manager '{connectionManager.ObjectName}' has no design-time default file path (likely expression-driven from a project parameter) -- SourceFolder/SourceFileName were generated as TODO placeholders; fill in appsettings.json manually"));
            return new FileSourceEntryRequest(key, "TODO", "TODO");
        }

        return new FileSourceEntryRequest(key, Path.GetDirectoryName(filePath) ?? "", Path.GetFileName(filePath));
    }

    /// <summary>Emitter rewrite phase 6: registers an appsettings.json "FileSource" entry, keyed
    /// by connection manager name, for whichever of a File System Task's own source/destination
    /// actually resolved from a connection manager (<see cref="FileSystemActionPlan.SourceConnectionName"/>/
    /// <c>DestinationConnectionName</c> -- null, and so skipped here, for the object-model-confirmed
    /// but never-evidenced literal-path form). Two File System Tasks referencing the SAME
    /// connection manager (e.g. a package archiving into, then later reading from, one FILE CM)
    /// would otherwise register the identical key twice -- harmless in the emitted JSON (a
    /// Dictionary, last write wins on an identical value) but skipped anyway for a clean
    /// appsettings.json.</summary>
    private static void RegisterFileSystemActionPaths(
        PackageSpec package, FileSystemActionPlan action, List<FileSourceEntryRequest> fileSourceEntries, List<GenerationGap> gaps)
    {
        void RegisterOne(string? connectionName)
        {
            if (connectionName is null) return;
            if (fileSourceEntries.Any(e => e.Key == connectionName)) return;
            if (FindConnectionManager(package, connectionName) is not { } cm) return; // PackagePlanner already resolved this CM by name; should not happen
            fileSourceEntries.Add(BuildFileSourceEntry(connectionName, cm, gaps));
        }
        RegisterOne(action.SourceConnectionName);
        RegisterOne(action.DestinationConnectionName);
    }

    /// <summary>
    /// Resolves a Flat File Destination's own file-format facts into a <see cref="FlatFileFlowSink"/>
    /// -- everything about the file's PHYSICAL layout (delimited vs fixed-width, delimiters,
    /// column widths, and the header row) lives on the flat file CONNECTION MANAGER
    /// (<see cref="FlatFileFormatSpec"/>, already modeled generically for the source side), not
    /// on the component itself; this only reads the component's own three bespoke properties
    /// (<see cref="FlatFileDestinationPayload.Overwrite"/>/.Header/.EscapeQualifier).
    ///
    /// Columns are emitted in the CONNECTION MANAGER's own column order (the real physical file
    /// layout), matched by name against the component's <see cref="PipelineColumnMappingSpec.ExternalColumnName"/>
    /// to find each column's PROPERTY name on the generated row/entity type -- not the mapping
    /// list's own declaration order, which isn't guaranteed to match the file's layout (though it
    /// does in both real evidenced instances).
    /// </summary>
    /// <summary>Builds the error-redirect sink for a flow whose primary destination's own input
    /// is configured <c>ErrorRowDisposition=RedirectRow</c> -- resolves the error entity (reusing
    /// <see cref="EntityEmitter.Emit"/> unchanged, plus one new <c>extraProperties</c> entry per
    /// unmapped DateTime-shaped external column, e.g. <c>FailedAt</c>, which has no
    /// pipeline-derived value at all -- see <see cref="EntityEmitter.Emit"/>'s own doc comment),
    /// its own <see cref="DbContextEmitter.TableSpec"/>, and a small generated static error-map
    /// class translating a failed entity + the causing <c>DbException</c> into the error entity.
    ///
    /// Business columns are resolved by matching NAME against the PRIMARY entity's own
    /// already-resolved columns -- deliberately NOT re-derived via lineage through the transform.
    /// A row that failed to insert was never inserted, so the failed <c>TEntity</c> instance's own
    /// property values ARE exactly what SSIS's own error-output buffer would have carried for that
    /// row (same buffer, same point in the pipeline) -- see <c>RedirectingSqlSink</c>'s own doc
    /// comment in Etl.Core for the full reasoning. A column resolves as ErrorCode/ErrorColumn --
    /// i.e. genuinely NEW to the error output, not passed through from upstream -- when its own
    /// raw &lt;inputColumn&gt; lineage id starts with the PRIMARY destination's own error output's
    /// RefId; those two are set by the sink itself, never copied from <c>failed</c>.
    /// </summary>
    private static RedirectingSqlFlowSink? ResolveErrorRedirectSink(
        string ns, string entityName, PipelineComponentSpec destination, ErrorRedirectPlan errorRedirect,
        List<GeneratedFile> files, List<DbContextEmitter.TableSpec> tables,
        Dictionary<string, PrimaryKeyCandidateSpec> primaryKeysByDestination, string taskName, List<GenerationGap> gaps)
    {
        var errorDestination = errorRedirect.ErrorDestinationComponent;
        var errorInput = errorDestination.Inputs.FirstOrDefault();
        if (errorInput is null)
        {
            gaps.Add(new GenerationGap(taskName, $"'{errorDestination.Name}' has no input -- not supported"));
            return null;
        }

        // An external column with no matching <inputColumn> at all needs deciding -- a .dtsx
        // carries no nullability/identity concept, so nothing can safely be inferred about most of
        // them (an identity PK like ErrorRowID is correctly, silently left off the entity
        // entirely, same as EntityEmitter already does for every other destination). The one shape
        // this generates FOR is a DateTime-shaped unmapped column (e.g. FailedAt) -- populated by
        // the sink itself at write time via DateTime.UtcNow, never guessed at for any other type.
        var mappedExternalIds = new HashSet<string>(errorInput.Columns
            .Where(c => c.ExternalMetadataColumnId is not null)
            .Select(c => c.ExternalMetadataColumnId!));
        var extraProperties = new List<(string Name, string ClrType)>();
        var timestampPropertyNames = new List<string>();
        foreach (var ext in errorInput.ExternalMetadataColumns)
        {
            if (mappedExternalIds.Contains(ext.RefId)) continue;
            if (SsisPipelineTypeMap.Resolve(ext.DataType)?.ClrTypeName != "DateTime") continue;
            extraProperties.Add((ext.Name, "DateTime"));
            timestampPropertyNames.Add(ext.Name);
        }

        var errorEntityName = entityName + "Error";
        Merge(files, gaps, EntityEmitter.Emit($"{ns}.Model", errorEntityName, errorDestination, extraProperties: extraProperties));
        primaryKeysByDestination.TryGetValue(errorDestination.RefId, out var errorPrimaryKey);
        tables.Add(new DbContextEmitter.TableSpec(errorEntityName, errorDestination, errorPrimaryKey));

        var primaryColumns = new HashSet<string>(PipelineResolver.ResolveDestinationInput(destination).Columns.Select(c => c.ExternalColumnName));
        var errorResolved = PipelineResolver.ResolveDestinationInput(errorDestination);
        var primaryErrorOutputRefId = destination.Outputs.First(o => o.IsErrorOut == true).RefId;

        var assignments = new List<string>();
        var anyBusinessColumn = false;
        foreach (var column in errorResolved.Columns)
        {
            if (column.Type is null) continue; // EntityEmitter already reported this gap

            var rawInputColumn = errorInput.Columns.First(c => c.RefId == column.PipelineColumnRefId);
            var isDiagnosticColumn = rawInputColumn.LineageId.StartsWith(primaryErrorOutputRefId, StringComparison.Ordinal);
            if (isDiagnosticColumn) continue; // ErrorCode/ErrorColumn -- set by the sink itself below, never copied from `failed`

            anyBusinessColumn = true;
            if (!primaryColumns.Contains(column.ExternalColumnName))
            {
                gaps.Add(new GenerationGap($"{errorEntityName}.{column.ExternalColumnName}",
                    $"has no matching column on the primary destination entity '{entityName}' -- cannot resolve its value"));
                continue;
            }

            assignments.Add($"        {column.ExternalColumnName} = failed.{column.ExternalColumnName},");
        }

        if (anyBusinessColumn && assignments.Count == 0)
        {
            // every business column failed to match -- nothing usable was generated for this
            // destination; EntityEmitter/the gap just added already explain why.
            return null;
        }

        foreach (var name in timestampPropertyNames)
            assignments.Add($"        {name} = DateTime.UtcNow,");
        // ErrorCode: a real, meaningful SQL Server error number (DbException.Number when the
        // failure IS a real SqlException) -- SSIS's own ErrorCode is a native OLE DB HRESULT,
        // which this rewrite (writing via Microsoft.Data.SqlClient, a different driver stack from
        // the OLE DB provider dtexec uses) cannot reproduce bit-for-bit. ErrorColumn is left at 0
        // deliberately -- SSIS resolves it to a pipeline lineage id, which has no equivalent once
        // redirected through a raw ADO.NET exception; a best-effort ErrorCode is the only
        // diagnostic this generator attempts, stated here rather than silently guessed further.
        assignments.Add("        ErrorCode = DbExceptionErrorCode.Resolve(ex),");
        assignments.Add("        ErrorColumn = 0, // not resolved -- see ex.Message in the log for the real SQL Server error text");

        var errorMapClassName = $"{entityName}ErrorMap";
        var mapLines = new List<string>
        {
            "using System.Data.Common;",
            "using Etl.Core.Data;",
            $"using {ns}.Model;",
            "",
            $"namespace {ns}.Mapping;",
            "",
            $"internal static class {errorMapClassName}",
            "{",
            $"    internal static {errorEntityName} Map({entityName} failed, DbException ex) => new()",
            "    {",
        };
        mapLines.AddRange(assignments);
        mapLines.Add("    };");
        mapLines.Add("}");
        files.Add(new GeneratedFile($"Mapping/{errorMapClassName}.cs", Rendering.JoinLines(mapLines)));

        return new RedirectingSqlFlowSink(destination.Name, errorDestination.Name, errorEntityName, errorMapClassName);
    }

    private static FlatFileFlowSink? ResolveFlatFileSink(
        PackageSpec package, string taskName, PipelineComponentSpec destination, string fileSourceKey,
        List<FileSourceEntryRequest> fileSourceEntries, List<GenerationGap> gaps)
    {
        var payload = destination.FlatFileDestination;
        var connectionManager = payload?.ConnectionName is { } cmName ? FindConnectionManager(package, cmName) : null;
        var format = connectionManager?.FlatFileFormat;
        if (payload is null || connectionManager is null || format is null)
        {
            gaps.Add(new GenerationGap(taskName, $"Flat File Destination '{destination.Name}' has no resolvable connection manager or file format -- not supported"));
            return null;
        }

        if (!string.IsNullOrEmpty(payload.Header))
        {
            gaps.Add(new GenerationGap(taskName, $"Flat File Destination '{destination.Name}' has a literal Header property set -- not supported yet, only the connection manager's own ColumnNamesInFirstDataRow header is generated"));
            return null;
        }

        // FixedWidth format with a generated header row is unevidenced -- neither real instance
        // (RBC_Demo_ETL's own CM_FF_ExportDelimited/CM_FF_ExportFixed) combines the two, and how
        // a header row's own column NAMES would be fixed-width-padded is not something this tool
        // has ground truth for. Named as a gap rather than guessed.
        if (format.Format == "FixedWidth" && format.ColumnNamesInFirstDataRow == true)
        {
            gaps.Add(new GenerationGap(taskName, $"Flat File Destination '{destination.Name}': a FixedWidth connection manager with ColumnNamesInFirstDataRow is not supported yet"));
            return null;
        }

        var componentColumnNameByExternalName = payload.ColumnMappings.ToDictionary(m => m.ExternalColumnName, m => m.ComponentColumnName);

        var columns = new List<FlatFileColumnFormatSpec>();
        foreach (var column in format.Columns)
        {
            if (!componentColumnNameByExternalName.TryGetValue(column.ObjectName, out var propertyName))
            {
                gaps.Add(new GenerationGap(taskName, $"Flat File Destination '{destination.Name}': connection manager column '{column.ObjectName}' has no mapped input column -- not supported"));
                return null;
            }

            // A fixed-width column is one the connection manager itself lays out by width with
            // no delimiter of its own (ColumnDelimiterDecoded empty) -- SSIS's own FixedWidth
            // format still encodes the ROW terminator as a synthetic trailing DELIMITED column
            // (see FlatFileDestinationPayload's own doc comment for the real evidenced example,
            // "RowEnd"), so this is a per-COLUMN decision, not a per-connection-manager one.
            var isFixedWidth = format.Format == "FixedWidth" && string.IsNullOrEmpty(column.ColumnDelimiterDecoded);
            columns.Add(new FlatFileColumnFormatSpec(propertyName, isFixedWidth ? column.MaximumWidth : null, column.ColumnDelimiterDecoded ?? ""));
        }

        // ColumnNamesInFirstDataRow is a source-and-destination-shared connection-manager
        // property (it tells a SOURCE to skip/parse row 1 as headers, and a DESTINATION to WRITE
        // row 1 as headers) -- confirmed real from CM_FF_ExportDelimited, the one evidenced
        // instance that sets it true. The header line uses the identical per-column-delimiter
        // algorithm as a data row, just with column NAMES instead of values (FixedWidth is
        // already rejected above, so every column here is plain delimited).
        string? headerLine = null;
        if (format.ColumnNamesInFirstDataRow == true)
        {
            var header = new System.Text.StringBuilder();
            foreach (var column in format.Columns)
                header.Append(column.ObjectName).Append(column.ColumnDelimiterDecoded);
            headerLine = header.ToString();
        }

        fileSourceEntries.Add(BuildFileSourceEntry(fileSourceKey, connectionManager, gaps));
        return new FlatFileFlowSink(destination.Name, fileSourceKey, payload.Overwrite == true, headerLine, columns);
    }

    /// <summary>"SqlLogin" (ConnectionManagerSpec.Parsed.AuthMode) -> SqlServer auth (user
    /// secrets, per ProjectEmitter); anything else -> Windows auth. Resolved from the
    /// DESTINATION's own connection manager only -- source CSV connection managers have no
    /// auth mode of their own to resolve. Also carries Server/Database along for the CLI's
    /// appsettings.Shared.json -- same connection manager, same call, no reason to re-look it
    /// up separately.</summary>
    private static (string AuthMode, string? UserId, string? Server, string? Database)? ResolveDatabaseAuth(PackageSpec package, PipelineComponentSpec destinationComponent)
    {
        var cmName = DestinationInfo.ConnectionName(destinationComponent);
        if (cmName is null || FindConnectionManager(package, cmName) is not { } cm || cm.Parsed is not { } parsed) return null;

        return parsed.AuthMode == "SqlLogin"
            ? ("SqlServer", parsed.UserId, parsed.Server, parsed.Database)
            : ("Windows", null, parsed.Server, parsed.Database);
    }

    /// <summary>
    /// Real bug: RBC_Demo_ETL's own SQL_CacheSet_SecondDb (Package_Legacy.dtsx) runs
    /// <c>EXEC dbo.Cache_Set ...</c> against CM_SQL_SSISDemoCache -- a genuinely different
    /// database from the flow this package's every other SQL task/destination uses
    /// (CM_SQL_SSISDemo). Before this, every SqlStep ran through the package's ONE
    /// IUnitOfWork/DbContext connection unconditionally, so this task's own EXEC silently ran
    /// against the wrong database (failing there with "Could not find stored procedure", since
    /// dbo.Cache_Set only exists in SSISDemoCache).
    ///
    /// <para>Compares <paramref name="sqlStep"/>'s own resolved connection manager's
    /// (Server, Database) against the package's already-resolved primary
    /// (<paramref name="targetServer"/>, <paramref name="targetDatabase"/>) -- same plain
    /// ordinal comparison <see cref="BuildSqlFlowSource"/> already uses for the equivalent
    /// source-vs-destination check. A mismatch routes to
    /// <see cref="ProgramSecondaryConnectionSqlStep"/> (its own independent, autocommitted
    /// connection -- see that Etl.Core type's own doc comment for why that, and not the shared
    /// transaction, is the faithful translation) instead of the ordinary
    /// <see cref="ProgramSqlStep"/>. Deduped by connection manager name in
    /// <paramref name="secondaryConnections"/> so two SqlSteps against the same secondary
    /// database (not evidenced anywhere, but possible) still produce ONE appsettings.json entry
    /// and one top-level connection string.</para>
    ///
    /// <para>Deliberately unresolved, falling back to the unchanged ordinary
    /// <see cref="ProgramSqlStep"/> path, in three cases where there is no evidence to say
    /// otherwise: <see cref="SqlStep.ConnectionManagerName"/> is null (a dangling DTSID
    /// reference); that name doesn't resolve to a connection manager with parsed connection-
    /// string info; or the package's own primary server/database was never resolved at all (no
    /// flow's destination had a SQL connection -- e.g. every destination is a Flat File
    /// Destination), in which case there is nothing to compare against.</para>
    /// </summary>
    private static ProgramStep ResolveSqlStep(
        PackageSpec package, SqlStep sqlStep, string? targetServer, string? targetDatabase,
        Dictionary<string, SecondaryConnectionRequest> secondaryConnections, List<GeneratedFile> files,
        List<GeneratedFile> testFiles, List<TestCoverageNote> testCoverageNotes, string rootNamespace, string mappingNamespace, List<GenerationGap> gaps,
        Func<string, string> reserveStatementIdentifier, bool willWire)
    {
        // Named, testable statement class (SqlStatementBuilderEmitter) rather than an anonymous
        // string literal embedded in Program.cs -- emitted once here regardless of which of the
        // two ProgramStep shapes below this resolves to, since both reference it identically.
        // reserveStatementIdentifier (see its own doc comment on Generate's own
        // ReserveStatementIdentifier local) -- not a bare SanitizeIdentifier(sqlStep.TaskName),
        // since two Execute SQL Tasks anywhere in this package can share the identical display
        // name.
        var identifierBase = reserveStatementIdentifier(sqlStep.TaskName);
        var statementClassName = identifierBase + "Statement";
        Merge(files, gaps, SqlStatementBuilderEmitter.Emit(mappingNamespace, sqlStep.TaskName, identifierBase, sqlStep.Sql));
        var statementTest = SqlStatementTestEmitter.Emit($"{package.ObjectName}.Tests", mappingNamespace, sqlStep.TaskName, identifierBase, sqlStep.Sql);
        var statementTestFinal = statementTest with { RelativePath = $"{package.ObjectName}.Tests/{statementTest.RelativePath}" };
        testFiles.Add(statementTestFinal);
        NoteTest(testCoverageNotes, statementTestFinal, "StatementText",
            "asserts the built SQL text only; never executes it.");

        if (sqlStep.ConnectionManagerName is not { } cmName
            || FindConnectionManager(package, cmName) is not { } cm
            || cm.Parsed is not { Server: { } server, Database: { } database } parsed
            || targetServer is null || targetDatabase is null
            || (server == targetServer && database == targetDatabase))
        {
            // Step-level test only for an ORDINARY SqlStep, run against the shared transaction --
            // a ProgramSecondaryConnectionSqlStep opens its own real SqlConnection unconditionally
            // and cannot be exercised against a fake at all (see SqlStatementTestEmitter.EmitStepTest's
            // own doc comment); that shape is deliberately left for an Integration-tagged test, a
            // later phase. Gated on willWire: this test's own generated code calls
            // `package.{identifierBase}(uow, ct)`, a method PackageClassEmitter only emits when
            // at least one flow wired -- with nothing wired, no package class exists at all, and
            // this test would reference a method that was never generated.
            if (willWire)
            {
                var stepTest = SqlStatementTestEmitter.EmitStepTest($"{package.ObjectName}.Tests", rootNamespace, sqlStep.TaskName, identifierBase);
                var stepTestFinal = stepTest with { RelativePath = $"{package.ObjectName}.Tests/{stepTest.RelativePath}" };
                testFiles.Add(stepTestFinal);
                NoteTest(testCoverageNotes, stepTestFinal, "ExecuteSqlStep",
                    "asserts SQL_X(fakeUow, ct) records the statement in uow.ExecutedSql; runs against the shared fake transaction, no real database.");
            }
            return new ProgramSqlStep(sqlStep.TaskName, statementClassName);
        }

        if (!secondaryConnections.ContainsKey(cmName))
        {
            secondaryConnections[cmName] = parsed.AuthMode == "SqlLogin"
                ? new SecondaryConnectionRequest(cmName, "SqlServer", parsed.UserId, server, database)
                : new SecondaryConnectionRequest(cmName, "Windows", null, server, database);
        }

        // "Secondary-connection SQL" taxonomy row -- mirrors Etl.Core.Tests' own
        // SecondaryConnectionSqlStepTests.RunAsync_NeverTouchesTheUnitOfWork exactly (see
        // TestDoublesEmitter's own unreachableSecondaryServer comment for why this needs no real
        // server at all, unlike a SQL/Excel SOURCE's own Integration-tagged read). Gated on
        // willWire for the same reason EmitStepTest is above -- this test's own generated code
        // also calls `package.{identifierBase}(...)`.
        if (willWire)
        {
            var secondaryTest = SqlStatementTestEmitter.EmitSecondaryConnectionStepTest($"{package.ObjectName}.Tests", rootNamespace, sqlStep.TaskName, identifierBase);
            var secondaryTestFinal = secondaryTest with { RelativePath = $"{package.ObjectName}.Tests/{secondaryTest.RelativePath}" };
            testFiles.Add(secondaryTestFinal);
            NoteTest(testCoverageNotes, secondaryTestFinal, "SecondaryConnection",
                "asserts the step never touches the shared IUnitOfWork; PackageHarness wires a deliberately unreachable secondary server so a real SqlException is what proves it.");
        }

        return new ProgramSecondaryConnectionSqlStep(sqlStep.TaskName, cmName, statementClassName);
    }

    /// <summary>Translates a <see cref="ForEachFileLoopPlan"/>'s own raw SSIS facts into
    /// everything <see cref="ProgramForEachFileLoopStep"/> needs: the per-iteration SQL
    /// expression (via <see cref="ForEachLoopEmitter"/>, emitted as its own named, parameterized
    /// <c>{InnerTaskName}Statement</c> class -- <see cref="SqlStatementBuilderEmitter.EmitParameterized"/>,
    /// the same treatment <see cref="ResolveSqlStep"/> already gives an ordinary Execute SQL
    /// Task) and the resolved <c>Etl.Core.Abstractions.ForEachFileNameMode</c> member name. Either
    /// can fail independently, each with its own gap -- this step is simply omitted from the
    /// package (not fatal to the rest of it), matching every other single-step gap in this
    /// file.</summary>
    private static ProgramForEachFileLoopStep? ResolveForEachFileLoop(
        ForEachFileLoopPlan loop, List<GeneratedFile> files, string mappingNamespace,
        List<FileSourceEntryRequest> fileSourceEntries, List<GenerationGap> gaps,
        Func<string, string> reserveStatementIdentifier)
    {
        const string currentFileParamName = "currentFile";
        var translated = ForEachLoopEmitter.TranslateSqlTemplate(loop.SqlTemplate, loop.VariableName, currentFileParamName);
        if (translated is NotTranslatable notTranslatable)
        {
            gaps.Add(new GenerationGap(loop.TaskName, $"{loop.InnerTaskName}: {notTranslatable.Reason}"));
            return null;
        }

        var nameMode = ResolveForEachFileNameMode(loop.NameRetrievalTypeRaw, loop.TaskName, gaps);
        if (nameMode is null) return null;

        // reserveStatementIdentifier -- shares the SAME package-wide registry as every ordinary
        // Execute SQL Task's own statement class (see Generate's own ReserveStatementIdentifier
        // doc comment), so this ForEach Loop body statement can't collide with one of those either.
        var identifierBase = reserveStatementIdentifier(loop.InnerTaskName);
        var statementClassName = identifierBase + "Statement";
        Merge(files, gaps, SqlStatementBuilderEmitter.EmitParameterized(
            mappingNamespace, loop.InnerTaskName, identifierBase, currentFileParamName, ((TranslatedOk)translated).CSharpExpression));

        // Emitter rewrite phase 6: the enumerator's own Folder is never backed by a connection
        // manager (unlike a Flat File Source's own path), so it's registered as a folder-only
        // (empty-filename) FileSourceEntryRequest keyed by this task's own name instead.
        var fileSourceKey = loop.TaskName;
        fileSourceEntries.Add(new FileSourceEntryRequest(fileSourceKey, loop.Folder, ""));

        return new ProgramForEachFileLoopStep(loop.TaskName, loop.Folder, loop.FileSpec, loop.Recurse, nameMode,
            currentFileParamName, statementClassName, fileSourceKey);
    }

    /// <summary>Raw values from Microsoft's public IDTSForEachFileEnumerator documentation --
    /// see Etl.Core.Abstractions.ForEachFileNameMode's own doc comment for why this one enum
    /// mapping was not independently probed against a live object model the way most raw enums
    /// elsewhere in this tool are. Shared by both loop-body shapes (SQL and Data Flow) -- the
    /// enumerator's own config is identical regardless of what its body does with it.</summary>
    private static string? ResolveForEachFileNameMode(int? raw, string taskName, List<GenerationGap> gaps)
    {
        var nameMode = raw switch
        {
            null or 0 => "FullyQualified",
            1 => "NameAndExtension",
            2 => "NameOnly",
            _ => null,
        };
        if (nameMode is null)
        {
            gaps.Add(new GenerationGap(taskName,
                $"ForEach File Enumerator's own FileNameRetrievalType ({raw}) is not a recognized value -- not supported"));
        }
        return nameMode;
    }

    /// <summary>
    /// Generates a ForEach File Enumerator loop whose body is a Data Flow Task -- built
    /// speculatively 2026-08-30 (see <see cref="PackagePlanner.ForEachFileDataFlowPlan"/>'s own
    /// doc comment; zero real evidenced package anywhere in the tracked portfolio has this
    /// shape). Mirrors the ordinary single-destination flow's own generation (EntityEmitter/
    /// TransformEmitter/DbContextEmitter.TableSpec all resolve identically -- a per-iteration
    /// Data Flow Task is structurally the same flow, just re-run N times) with a deliberate
    /// narrowing, reported as its own named gap rather than attempted: the flow must be a plain
    /// single-destination SQL flow (Lookup/Conditional Split/Merge Join/Multicast/OLE DB
    /// Command/Flat File Destination inside a loop body would each need their own, separately
    /// unevidenced per-iteration semantics, so combining any of them with this already-
    /// speculative feature is out of scope). Skips ResolveFlowSource/BuildFileSourceEntry
    /// entirely -- unlike an ordinary flow, this source's file path is never a static
    /// appsettings.json value. The CSV row type/class map are still emitted via the SAME
    /// CsvRowEmitter/ClassMapEmitter an ordinary CSV flow uses (they only depend on the
    /// connection manager's own column FORMAT, never its path); the actual IRowSource
    /// construction happens fresh, per iteration, inside ProgramEmitter's own lambda around
    /// <see cref="PackagePlanner.ForEachFileDataFlowPlan.FilePathExpression"/>'s C# translation.
    /// </summary>
    private static ProgramForEachDataFlowLoopStep? GenerateForEachDataFlowLoop(
        PackageSpec package, string ns, ForEachFileDataFlowPlan loop,
        List<GeneratedFile> files, List<DbContextEmitter.TableSpec> tables, HashSet<string> functionsUsed,
        Dictionary<string, PrimaryKeyCandidateSpec> primaryKeysByDestination,
        List<FileSourceEntryRequest> fileSourceEntries, List<GenerationGap> gaps,
        ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase,
        bool emitSeams, ComponentHolderRegistry? componentHolders = null)
    {
        var flow = loop.Flow;

        if (flow.Lookup is not null || flow.ConditionalSplit is not null || flow.MergeJoin is not null
            || flow.Multicast is not null || flow.OleDbCommand is not null || flow.Union is not null)
        {
            gaps.Add(new GenerationGap(loop.TaskName,
                $"{flow.TaskName}: ForEach Loop's own Data Flow Task body uses a Lookup/Conditional Split/Merge Join/Multicast/OLE DB Command/multi-source Merge-UnionAll -- only a plain single-destination flow is supported inside a loop body yet"));
            return null;
        }

        if (flow.DestinationComponent.ComponentClassId == "Microsoft.FlatFileDestination")
        {
            gaps.Add(new GenerationGap(loop.TaskName,
                $"{flow.TaskName}: ForEach Loop's own Data Flow Task body writes to a Flat File Destination -- only a SQL destination is supported inside a loop body yet"));
            return null;
        }

        if (!DestinationInfo.IsFastLoadConfigured(flow.DestinationComponent))
        {
            gaps.Add(new GenerationGap(loop.TaskName,
                $"{flow.TaskName}: destination '{flow.DestinationComponent.Name}' is not configured for fast load -- row-by-row insert generation is not supported yet"));
            return null;
        }

        var entityName = TryResolveEntityName(flow.TaskName, flow.DestinationComponent, gaps);
        if (entityName is null) return null;

        // PlanForEachDataFlowLoop already required a Flat File Source with a resolvable
        // connection manager before a ForEachFileDataFlowPlan was ever built.
        var csvCmName = flow.FlatFileSource!.FlatFileSource!.ConnectionName!;
        var csvConnectionManager = FindConnectionManager(package, csvCmName)!;

        var translated = ForEachLoopEmitter.TranslateSqlTemplate(loop.FilePathExpression, loop.VariableName, "currentFile");
        if (translated is NotTranslatable notTranslatable)
        {
            gaps.Add(new GenerationGap(loop.TaskName, $"{csvCmName}'s own ConnectionString expression: {notTranslatable.Reason}"));
            return null;
        }

        var nameMode = ResolveForEachFileNameMode(loop.NameRetrievalTypeRaw, loop.TaskName, gaps);
        if (nameMode is null) return null;

        var nullableColumnNames = ResolveNullableColumnNames(flow);

        var rowTypeName = entityName + "CsvRow";
        Merge(files, gaps, CsvRowEmitter.Emit($"{ns}.Csv", rowTypeName, csvConnectionManager, flow.FlatFileSource));
        Merge(files, gaps, ClassMapEmitter.Emit($"{ns}.Csv", rowTypeName, csvConnectionManager));

        Merge(files, gaps, EntityEmitter.Emit($"{ns}.Model", entityName, flow.DestinationComponent, nullableColumnNames));

        var transformClassName = entityName + "Transform";
        var transformResult = TransformEmitter.Emit(new TransformRequest(
            EmitSeams: emitSeams,
            MappingNamespace: $"{ns}.Mapping",
            TransformClassName: transformClassName,
            RowTypeNamespace: $"{ns}.Csv",
            RowTypeName: rowTypeName,
            EntityNamespace: $"{ns}.Model",
            EntityName: entityName,
            SsisFnNamespace: $"{ns}.Ssis",
            Pipeline: flow.Pipeline,
            DerivedColumns: flow.DerivedColumn is null ? [] : [flow.DerivedColumn],
            DataConversions: flow.DataConversion is null ? [] : [flow.DataConversion],
            DestinationComponent: flow.DestinationComponent,
            NullableColumnNames: nullableColumnNames,
            Holders: componentHolders));
        Merge(files, gaps, transformResult.Result);
        foreach (var fn in transformResult.SsisFunctionsUsed) functionsUsed.Add(fn);

        if (transformResult.Result.Files.Count == 0) return null; // TransformEmitter already recorded why

        // No starter test coverage exists for this loop-body shape at all -- a real, previously-
        // silent gap (caught 2026-09-06 by an independent review against Docs/Generated-Tests-Plan.md):
        // this function never called ComponentTestEmitter/TransformTestEmitter AND never reported a
        // gap either, unlike every other flow shape that degrades to an honest advisory when it
        // can't produce real coverage (see GenerateLookupFlow's own identical-shaped gap for its
        // downstream transform). The per-iteration CSV source has no static Tier-A sample file to
        // point a "Source -- CSV" test at (its own FileSourceEntryRequest is a folder, not a file --
        // see fileSourceEntries.Add below), and the taxonomy's own "ForEach loop" row only covers
        // the SQL-statement-per-iteration shape, not this Data-Flow-per-iteration one -- a new test
        // shape, not something this pilot can safely synthesize yet.
        gaps.Add(new GenerationGap(loop.TaskName,
            $"{flow.TaskName}: this loop body's own generated methods (the per-iteration CSV source, '{transformClassName}', and the SQL sink) have no starter test coverage in this pilot -- a ForEach-Loop-over-a-Data-Flow-Task has no Tier-A sample file to synthesize (the source path is computed per iteration, not a static location) and no dedicated test shape yet. Verify by hand.",
            IsBlocking: false, Kind: GapKind.TestOracle, EvidenceRefId: flow.DestinationComponent.RefId));

        primaryKeysByDestination.TryGetValue(flow.DestinationComponent.RefId, out var primaryKey);
        tables.Add(new DbContextEmitter.TableSpec(entityName, flow.DestinationComponent, primaryKey));

        if (authMode is null && ResolveDatabaseAuth(package, flow.DestinationComponent) is { } auth)
            (authMode, userId, targetServer, targetDatabase) = auth;

        // Emitter rewrite phase 6: the enumerator's own Folder is never backed by a connection
        // manager (unlike the per-iteration Flat File Source's own path, which is already an
        // expression, not a design-time default), so it's registered as a folder-only
        // (empty-filename) FileSourceEntryRequest keyed by this task's own name instead.
        var fileSourceKey = loop.TaskName;
        fileSourceEntries.Add(new FileSourceEntryRequest(fileSourceKey, loop.Folder, ""));

        return new ProgramForEachDataFlowLoopStep(
            loop.TaskName, loop.Folder, loop.FileSpec, loop.Recurse, nameMode,
            "currentFile", ((TranslatedOk)translated).CSharpExpression,
            flow.FlatFileSource.Name, rowTypeName, entityName, transformClassName, fileSourceKey);
    }

    /// <summary>
    /// Generates a <c>STOCK:FORLOOP</c> whose body is a Data Flow Task -- Phase 3 of the
    /// unsupported-component-types plan. Mirrors <see cref="GenerateForEachDataFlowLoop"/>'s own
    /// generation almost exactly (same gates, same EntityEmitter/TransformEmitter/
    /// DbContextEmitter.TableSpec resolution -- a per-iteration Data Flow Task is structurally the
    /// same flow, just re-run for as long as EvalExpression holds), with two real differences:
    /// there is no enumerator/folder to register a <see cref="FileSourceEntryRequest"/> for at all
    /// (the counter is an ordinary package variable, never backed by a connection manager), and
    /// the per-iteration source construction references the counter DIRECTLY off
    /// <c>packageVariables</c> rather than a lambda parameter -- see
    /// <c>Etl.Core.Pipeline.ForLoopStep{TRow,TEntity}</c>'s own doc comment for why.
    /// </summary>
    private static ProgramForLoopStep? GenerateForLoop(
        PackageSpec package, string ns, ForLoopPlan loop,
        List<GeneratedFile> files, List<DbContextEmitter.TableSpec> tables, HashSet<string> functionsUsed,
        Dictionary<string, PrimaryKeyCandidateSpec> primaryKeysByDestination,
        List<GenerationGap> gaps,
        ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase,
        bool emitSeams, ComponentHolderRegistry? componentHolders = null)
    {
        var flow = loop.Flow;

        if (flow.Lookup is not null || flow.ConditionalSplit is not null || flow.MergeJoin is not null
            || flow.Multicast is not null || flow.OleDbCommand is not null || flow.Union is not null)
        {
            gaps.Add(new GenerationGap(loop.TaskName,
                $"{flow.TaskName}: For Loop Container's own Data Flow Task body uses a Lookup/Conditional Split/Merge Join/Multicast/OLE DB Command/multi-source Merge-UnionAll -- only a plain single-destination flow is supported inside a loop body yet"));
            return null;
        }

        if (flow.DestinationComponent.ComponentClassId == "Microsoft.FlatFileDestination")
        {
            gaps.Add(new GenerationGap(loop.TaskName,
                $"{flow.TaskName}: For Loop Container's own Data Flow Task body writes to a Flat File Destination -- only a SQL destination is supported inside a loop body yet"));
            return null;
        }

        if (!DestinationInfo.IsFastLoadConfigured(flow.DestinationComponent))
        {
            gaps.Add(new GenerationGap(loop.TaskName,
                $"{flow.TaskName}: destination '{flow.DestinationComponent.Name}' is not configured for fast load -- row-by-row insert generation is not supported yet"));
            return null;
        }

        var entityName = TryResolveEntityName(flow.TaskName, flow.DestinationComponent, gaps);
        if (entityName is null) return null;

        // PlanForLoop already required a Flat File Source with a resolvable connection manager
        // before a ForLoopPlan was ever built.
        var csvCmName = flow.FlatFileSource!.FlatFileSource!.ConnectionName!;
        var csvConnectionManager = FindConnectionManager(package, csvCmName)!;

        // Unlike ProgramForEachDataFlowLoopStep (a lambda parameter substituted for the current
        // file), the counter is substituted as a LIVE read straight off packageVariables -- the
        // generated source-construction method has no per-iteration parameter of its own (see
        // ForLoopStep<TRow,TEntity>'s own doc comment), so every iteration's file path expression
        // must re-read the counter's own current value each time it runs, not close over a value
        // captured once.
        var counterAccessExpression = $"packageVariables.GetRequired<{loop.CounterClrTypeName}>({ProgramEmitter.CSharpStringLiteral(loop.CounterVariableName)})";
        var translated = ForEachLoopEmitter.TranslateSqlTemplate(loop.FilePathExpression, loop.CounterVariableName, counterAccessExpression);
        if (translated is NotTranslatable notTranslatable)
        {
            gaps.Add(new GenerationGap(loop.TaskName, $"{csvCmName}'s own ConnectionString expression: {notTranslatable.Reason}"));
            return null;
        }

        var nullableColumnNames = ResolveNullableColumnNames(flow);

        var rowTypeName = entityName + "CsvRow";
        Merge(files, gaps, CsvRowEmitter.Emit($"{ns}.Csv", rowTypeName, csvConnectionManager, flow.FlatFileSource));
        Merge(files, gaps, ClassMapEmitter.Emit($"{ns}.Csv", rowTypeName, csvConnectionManager));

        Merge(files, gaps, EntityEmitter.Emit($"{ns}.Model", entityName, flow.DestinationComponent, nullableColumnNames));

        var transformClassName = entityName + "Transform";
        var transformResult = TransformEmitter.Emit(new TransformRequest(
            EmitSeams: emitSeams,
            MappingNamespace: $"{ns}.Mapping",
            TransformClassName: transformClassName,
            RowTypeNamespace: $"{ns}.Csv",
            RowTypeName: rowTypeName,
            EntityNamespace: $"{ns}.Model",
            EntityName: entityName,
            SsisFnNamespace: $"{ns}.Ssis",
            Pipeline: flow.Pipeline,
            DerivedColumns: flow.DerivedColumn is null ? [] : [flow.DerivedColumn],
            DataConversions: flow.DataConversion is null ? [] : [flow.DataConversion],
            DestinationComponent: flow.DestinationComponent,
            NullableColumnNames: nullableColumnNames,
            Holders: componentHolders));
        Merge(files, gaps, transformResult.Result);
        foreach (var fn in transformResult.SsisFunctionsUsed) functionsUsed.Add(fn);

        if (transformResult.Result.Files.Count == 0) return null; // TransformEmitter already recorded why

        // Same taxonomy gap GenerateForEachDataFlowLoop reports for its own loop-body shape (see
        // that method's own doc comment): no Tier-A sample file/dedicated test shape exists yet
        // for a loop body whose own source path is computed per iteration rather than a static
        // location.
        gaps.Add(new GenerationGap(loop.TaskName,
            $"{flow.TaskName}: this loop body's own generated methods (the per-iteration CSV source, '{transformClassName}', and the SQL sink) have no starter test coverage in this pilot -- a For Loop Container has no Tier-A sample file to synthesize (the source path is computed per iteration, not a static location) and no dedicated test shape yet. Verify by hand.",
            IsBlocking: false, Kind: GapKind.TestOracle, EvidenceRefId: flow.DestinationComponent.RefId));

        primaryKeysByDestination.TryGetValue(flow.DestinationComponent.RefId, out var primaryKey);
        tables.Add(new DbContextEmitter.TableSpec(entityName, flow.DestinationComponent, primaryKey));

        if (authMode is null && ResolveDatabaseAuth(package, flow.DestinationComponent) is { } auth)
            (authMode, userId, targetServer, targetDatabase) = auth;

        return new ProgramForLoopStep(
            loop.TaskName, loop.CounterVariableName, loop.CounterClrTypeName,
            loop.InitCSharpExpression, loop.EvalCSharpPredicate, loop.AssignCSharpValueExpression,
            ((TranslatedOk)translated).CSharpExpression,
            flow.FlatFileSource.Name, rowTypeName, entityName, transformClassName);
    }
}
