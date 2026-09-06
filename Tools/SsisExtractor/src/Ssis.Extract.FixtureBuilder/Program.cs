using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.SqlServer.Dts.Runtime;
using Microsoft.SqlServer.Dts.Runtime.Wrapper;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using RtPackage = Microsoft.SqlServer.Dts.Runtime.Package;
using RtTaskHost = Microsoft.SqlServer.Dts.Runtime.TaskHost;
using RtDataType = Microsoft.SqlServer.Dts.Runtime.Wrapper.DataType;

namespace Ssis.Extract.FixtureBuilder;

/// <summary>
/// Builds <c>SyntheticParallelShapes.dtsx</c> via the real SSIS object model -- the same
/// "ask the runtime, don't guess" discipline as every other object-model tool in this repo
/// (CLAUDE.md trap 12), extended here from single-property probing to whole-package
/// construction with real live connections.
///
/// <para><b>Shape, and what each branch is real evidence for:</b> three independent
/// top-level branches with NO precedence constraints between them, so
/// <c>ControlFlowDagSpec.ParallelLevels</c> is exercised for real for the first time --
/// both PoC packages and all three prior synthetic fixtures are straight-line chains.</para>
/// <list type="bullet">
/// <item><b>Branch 1</b> (a 3-node chain): Execute SQL (create-if-missing) -> Flat File
/// Source -> OLE DB Destination -> Execute SQL (post-load update). Proves an Execute SQL
/// Task sequenced AFTER a Data Flow Task extracts correctly -- the generated C# rewrite
/// only modeled pre-load SQL at the time this fixture was built, so this is what made
/// that gap concrete rather than theoretical.</item>
/// <item><b>Branch 2</b> (a single Data Flow Task, no precedence to branch 1): two
/// independent OLE DB Source -> OLE DB Destination pairs with NO transform between them --
/// the direct-copy shape neither PoC package has, and no code in <c>Etl.Core</c> models.</item>
/// <item><b>Branch 3</b> (a single Data Flow Task, no precedence to branches 1/2): a Flat
/// File Source whose main output feeds one destination and whose ERROR output is routed
/// through a Script Component (a genuine, minimal passthrough transform, its custom
/// properties set directly rather than through the VSTA editor -- the same technique
/// CLAUDE.md's Script Component notes already document) into a second destination.
/// Error-row redirection is completely unmodeled in the extractor before this fixture.</item>
/// </list>
///
/// <para><b>Requires, before running:</b> the backing tables from
/// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-parallel-shapes-tables.sql</c> already
/// created on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c> (ReinitializeMetaData needs a real live
/// schema to resolve, exactly like the Lookup/Split fixture before this one), and the two CSV
/// files under <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-parallel-shapes-csv/</c> present
/// on disk. The output <c>.dtsx</c> itself is written to <c>SSIS/SyntheticParallelShapes.dtsx</c>
/// (registered in <c>SSIS.dtproj</c>, visible in SSDT alongside the real PoC packages) -- a
/// DIFFERENT directory from its CSV/SQL dependencies, which stay under this tool's own test
/// fixtures. The CSV lookup below is therefore anchored to this SOURCE FILE's own location
/// via <see cref="CallerFilePathAttribute"/>, not to wherever the caller points <c>--out</c>
/// -- the two used to be the same directory (before the .dtsx moved into SSIS/ for
/// visibility) and deriving one from the other broke the moment they diverged.</para>
///
/// <para><b>Deliberately checked in</b> -- see this project's own csproj header for why the
/// first three synthetic fixtures' builders were NOT, and why that was a real loss.</para>
/// </summary>
internal static class Program
{
    private const string SqlConnectionString =
        "Provider=MSOLEDBSQL.1;Data Source=.\\SQLFORPOC_2022;Initial Catalog=SsisPoC;Integrated Security=SSPI;Auto Translate=False;";

    /// <summary>A SECOND database on the same instance -- see BuildSecondConnectionSqlFixture's
    /// own doc comment.</summary>
    private const string SecondarySqlConnectionString =
        "Provider=MSOLEDBSQL.1;Data Source=.\\SQLFORPOC_2022;Initial Catalog=SsisPoC_Secondary;Integrated Security=SSPI;Auto Translate=False;";

    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    /// <summary>
    /// tests/Ssis.Extract.Tests/Fixtures, resolved from THIS SOURCE FILE's own location
    /// (src/Ssis.Extract.FixtureBuilder/Program.cs -> src -> Tools/SsisExtractor ->
    /// tests/Ssis.Extract.Tests/Fixtures), not from the output path. See this class's own
    /// doc comment for why deriving one from the other broke once the built .dtsx moved to
    /// SSIS/ but its CSV/SQL dependencies stayed here.
    /// </summary>
    private static string TestFixturesDir() => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(ThisFilePath())!, "..", "..", "tests", "Ssis.Extract.Tests", "Fixtures"));

    private static int Main(string[] args)
    {
        if (args.Length is < 1 or > 2)
        {
            Console.Error.WriteLine("usage: ssis-fixture-builder <output-dtsx-path> [parallel-shapes|nested-container|post-flow-sql|oledb-source-transform|conditional-split|conditional-split-remerge|file-system-task|findstring-trim|ternary|email-domain|datediff|data-conversion|data-conversion-split|flat-file-destination|merge-join|derived-column-replace|character-map-inplace|sort-merge-remerge|foreach-file-loop|foreach-data-flow-loop|excel-source|oledb-command|multicast|numeric-coercion|data-conversion-types|string-to-int-coercion|excel-source-sqlcommand|aggregate|int-numeric-coercion|data-conversion-types2|lookup-single|script-component-seams|disabled-task|script-task-seams|script-task-hoist-inversion|cond-constraint|cond-guard|failure-handler|second-connection-sql|lookup-then-aggregate|multicast-discard|multicast-aggregate-sibling|execute-package-task|oledb-command-reordered|oledb-command-exec-named|row-count-variable|standalone-sort|merge-interleave-probe|union-two-sources|union-plus-extra-source|aggregate-then-oledb-command]");
            return 2;
        }

        var fixtureName = args.Length == 2 ? args[1] : "parallel-shapes";
        if (fixtureName == "nested-container")
        {
            return BuildNestedContainerFixture(args[0]);
        }
        if (fixtureName == "post-flow-sql")
        {
            return BuildPostFlowSqlFixture(args[0]);
        }
        if (fixtureName == "lookup-single")
        {
            return BuildLookupSingleFixture(args[0]);
        }
        if (fixtureName == "oledb-source-transform")
        {
            return BuildOleDbSourceTransformFixture(args[0]);
        }
        if (fixtureName == "conditional-split")
        {
            return BuildConditionalSplitFixture(args[0]);
        }
        if (fixtureName == "conditional-split-remerge")
        {
            return BuildConditionalSplitRemergeFixture(args[0]);
        }
        if (fixtureName == "file-system-task")
        {
            return BuildFileSystemTaskFixture(args[0]);
        }
        if (fixtureName == "findstring-trim")
        {
            return BuildFindStringTrimFixture(args[0]);
        }
        if (fixtureName == "script-component-seams")
        {
            return BuildScriptComponentSeamsFixture(args[0]);
        }
        if (fixtureName == "ternary")
        {
            return BuildTernaryFixture(args[0]);
        }
        if (fixtureName == "email-domain")
        {
            return BuildEmailDomainFixture(args[0]);
        }
        if (fixtureName == "datediff")
        {
            return BuildDateDiffFixture(args[0]);
        }
        if (fixtureName == "data-conversion")
        {
            return BuildDataConversionFixture(args[0]);
        }
        if (fixtureName == "data-conversion-split")
        {
            return BuildDataConversionSplitFixture(args[0]);
        }
        if (fixtureName == "flat-file-destination")
        {
            return BuildFlatFileDestinationFixture(args[0]);
        }
        if (fixtureName == "merge-join")
        {
            return BuildMergeJoinFixture(args[0]);
        }
        if (fixtureName == "derived-column-replace")
        {
            return BuildDerivedColumnReplaceFixture(args[0]);
        }
        if (fixtureName == "character-map-inplace")
        {
            return BuildCharacterMapInPlaceFixture(args[0]);
        }
        if (fixtureName == "sort-merge-remerge")
        {
            return BuildSortMergeRemergeFixture(args[0]);
        }
        if (fixtureName == "foreach-file-loop")
        {
            return BuildForEachFileLoopFixture(args[0]);
        }
        if (fixtureName == "foreach-data-flow-loop")
        {
            return BuildForEachDataFlowLoopFixture(args[0]);
        }
        if (fixtureName == "aggregate")
        {
            return BuildAggregateFixture(args[0]);
        }
        if (fixtureName == "aggregate-functions")
        {
            return BuildAggregateFunctionsFixture(args[0]);
        }
        if (fixtureName == "int-numeric-coercion")
        {
            return BuildIntNumericCoercionFixture(args[0]);
        }
        if (fixtureName == "data-conversion-types2")
        {
            return BuildDataConversionTypes2Fixture(args[0]);
        }
        if (fixtureName == "excel-source")
        {
            return BuildExcelSourceFixture(args[0]);
        }
        if (fixtureName == "oledb-command")
        {
            return BuildOleDbCommandFixture(args[0]);
        }
        if (fixtureName == "oledb-command-reordered")
        {
            return BuildOleDbCommandReorderedFixture(args[0]);
        }
        if (fixtureName == "oledb-command-exec-named")
        {
            return BuildOleDbCommandExecNamedFixture(args[0]);
        }
        if (fixtureName == "row-count-variable")
        {
            return BuildRowCountVariableFixture(args[0]);
        }
        if (fixtureName == "multicast")
        {
            return BuildMulticastFixture(args[0]);
        }
        if (fixtureName == "numeric-coercion")
        {
            return BuildNumericCoercionFixture(args[0]);
        }
        if (fixtureName == "data-conversion-types")
        {
            return BuildDataConversionTypesFixture(args[0]);
        }
        if (fixtureName == "string-to-int-coercion")
        {
            return BuildStringToIntCoercionFixture(args[0]);
        }
        if (fixtureName == "excel-source-sqlcommand")
        {
            return BuildExcelSourceSqlCommandFixture(args[0]);
        }
        if (fixtureName == "disabled-task")
        {
            return BuildDisabledTaskFixture(args[0]);
        }
        if (fixtureName == "script-task-seams")
        {
            return BuildScriptTaskSeamsFixture(args[0]);
        }
        if (fixtureName == "script-task-hoist-inversion")
        {
            return BuildScriptTaskHoistInversionFixture(args[0]);
        }
        if (fixtureName == "cond-constraint")
        {
            return BuildCondConstraintProbeFixture(args[0]);
        }
        if (fixtureName == "cond-guard")
        {
            return BuildCondGuardFixture(args[0]);
        }
        if (fixtureName == "failure-handler")
        {
            return BuildFailureHandlerFixture(args[0]);
        }
        if (fixtureName == "second-connection-sql")
        {
            return BuildSecondConnectionSqlFixture(args[0]);
        }
        if (fixtureName == "lookup-then-aggregate")
        {
            return BuildLookupThenAggregateFixture(args[0]);
        }
        if (fixtureName == "multicast-discard")
        {
            return BuildMulticastDiscardFixture(args[0]);
        }
        if (fixtureName == "multicast-aggregate-sibling")
        {
            return BuildMulticastAggregateSiblingFixture(args[0]);
        }
        if (fixtureName == "execute-package-task")
        {
            return BuildExecutePackageTaskFixture(args[0]);
        }
        if (fixtureName == "standalone-sort")
        {
            return BuildStandaloneSortFixture(args[0]);
        }
        if (fixtureName == "merge-interleave-probe")
        {
            return BuildMergeInterleaveProbeFixture(args[0]);
        }
        if (fixtureName == "union-plus-extra-source")
        {
            return BuildUnionPlusExtraSourceFixture(args[0]);
        }
        if (fixtureName == "aggregate-then-oledb-command")
        {
            return BuildAggregateThenOleDbCommandFixture(args[0]);
        }
        if (fixtureName == "union-two-sources")
        {
            return BuildUnionTwoSourcesFixture(args[0]);
        }
        if (fixtureName == "event-handler-probe")
        {
            return BuildEventHandlerProbeFixture(args[0]);
        }
        if (fixtureName != "parallel-shapes")
        {
            Console.Error.WriteLine($"error: unknown fixture '{fixtureName}' -- expected 'parallel-shapes', 'nested-container', 'post-flow-sql', 'oledb-source-transform', 'conditional-split', 'conditional-split-remerge', 'file-system-task', 'findstring-trim', 'ternary', 'email-domain', 'datediff', 'data-conversion', 'data-conversion-split', 'flat-file-destination', 'merge-join', 'sort-merge-remerge', 'foreach-file-loop', 'foreach-data-flow-loop', 'excel-source', 'oledb-command', 'multicast', 'numeric-coercion', 'data-conversion-types', 'string-to-int-coercion', 'excel-source-sqlcommand', 'aggregate', 'int-numeric-coercion', 'data-conversion-types2', 'lookup-single', 'script-component-seams', 'disabled-task', 'script-task-seams', 'script-task-hoist-inversion', 'cond-constraint', 'cond-guard', 'failure-handler', 'second-connection-sql', 'lookup-then-aggregate', 'multicast-discard', 'multicast-aggregate-sibling', 'execute-package-task', 'standalone-sort', 'merge-interleave-probe', 'union-two-sources', 'union-plus-extra-source', 'aggregate-then-oledb-command', or 'event-handler-probe'.");

            return 2;
        }

        var csvDir = Path.Combine(TestFixturesDir(), "synthetic-parallel-shapes-csv");
        var loadACsvPath = Path.Combine(csvDir, "SyntheticLoadA.csv");
        var errorRoutingCsvPath = Path.Combine(csvDir, "SyntheticErrorRouting.csv");

        if (!File.Exists(loadACsvPath) || !File.Exists(errorRoutingCsvPath))
        {
            Console.Error.WriteLine($"error: expected CSV fixtures at {csvDir} -- this tool's checked-in test fixtures are missing or moved.");
            return 2;
        }

        var pkg = new RtPackage { Name = "SyntheticParallelShapes" };
        // Match the other three packages' ProtectionLevel ("0" = DontSaveSensitive) explicitly --
        // a freshly constructed Package's object-model default differs, and SSDT's project
        // consistency check compares every package's ProtectionLevel against the project's own,
        // failing the whole solution build with "SyntheticParallelShapes.dtsx has a different
        // ProtectionLevel than the project" otherwise. Confirmed via devenv /Build's own error.
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var loadACsvCm = AddFlatFileConnectionManager(pkg, "CM_LoadACsv", loadACsvPath,
            ("ID", "DT_I4", 0, 0, 0),
            ("Name", "DT_WSTR", 50, 0, 0),
            ("Amount", "DT_NUMERIC", 0, 12, 2),
            ("EntryDate", "DT_DBDATE", 0, 0, 0));

        var errorRoutingCsvCm = AddFlatFileConnectionManager(pkg, "CM_ErrorRoutingCsv", errorRoutingCsvPath,
            ("ID", "DT_I4", 0, 0, 0),
            ("Description", "DT_WSTR", 100, 0, 0),
            ("Value", "DT_NUMERIC", 0, 10, 2));

        BuildBranch1(pkg, sqlCm, loadACsvCm);
        BuildBranch2DirectCopy(pkg, sqlCm);
        BuildBranch3ErrorRouting(pkg, sqlCm, errorRoutingCsvCm);

        pkg.SaveToXML(out var xml, null);
        // Byte-preserving write, not File.WriteAllText with a default encoding -- CLAUDE.md
        // trap 18: this file's exact bytes feed the extractor's golden-file tests.
        File.WriteAllText(args[0], xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {args[0]}");
        return 0;
    }

    /// <summary>
    /// Builds <c>SyntheticNestedContainer.dtsx</c> -- a Sequence Container (<c>STOCK:SEQUENCE</c>,
    /// confirmed via an isolated object-model probe before writing any planner code against it,
    /// same "ask the runtime, don't guess" discipline as everywhere else in this repo) wrapping
    /// an Execute SQL Task and a Data Flow Task, plus a second Data Flow Task at the PACKAGE
    /// root that runs after the container. Neither PoC package nor any prior synthetic fixture
    /// nests anything, so <c>Ssis.Extract.Codegen.PackagePlanner</c> (root-level only until
    /// now) had zero real evidence for the nested case. Deliberately excludes an Execute SQL
    /// Task positioned AFTER a Data Flow Task -- that's the separate, already-documented
    /// "the generated code only models pre-load SQL" gap (SyntheticParallelShapes' own branch 1);
    /// mixing it in here would muddy what this fixture is testing.
    /// Written to <c>tests/Ssis.Extract.Tests/Fixtures/</c> directly (test-only, not
    /// registered in SSIS.dtproj) -- the same tier the ForEach/Lookup-Split/Script-Component
    /// fixtures already live in, unlike SyntheticParallelShapes/ScriptTest which are visible
    /// in SSDT on purpose.
    /// </summary>
    private static int BuildNestedContainerFixture(string outputPath)
    {
        var csvDir = Path.Combine(TestFixturesDir(), "synthetic-nested-container-csv");
        var nestedCsvPath = Path.Combine(csvDir, "SyntheticNested.csv");
        var rootCsvPath = Path.Combine(csvDir, "SyntheticNestedRoot.csv");

        if (!File.Exists(nestedCsvPath) || !File.Exists(rootCsvPath))
        {
            Console.Error.WriteLine($"error: expected CSV fixtures at {csvDir} -- this tool's checked-in test fixtures are missing or moved.");
            return 2;
        }

        var pkg = new RtPackage { Name = "SyntheticNestedContainer" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var nestedCsvCm = AddFlatFileConnectionManager(pkg, "CM_NestedCsv", nestedCsvPath,
            ("ID", "DT_I4", 0, 0, 0),
            ("Label", "DT_WSTR", 50, 0, 0),
            ("Amount", "DT_NUMERIC", 0, 10, 2));

        var rootCsvCm = AddFlatFileConnectionManager(pkg, "CM_RootCsv", rootCsvPath,
            ("ID", "DT_I4", 0, 0, 0),
            ("Label", "DT_WSTR", 50, 0, 0),
            ("Amount", "DT_NUMERIC", 0, 10, 2));

        var seqExec = pkg.Executables.Add("STOCK:SEQUENCE");
        var seq = (Microsoft.SqlServer.Dts.Runtime.Sequence)seqExec;
        seq.Name = "SEQ_Load";

        var truncateTask = (RtTaskHost)seq.Executables.Add("Microsoft.ExecuteSQLTask");
        truncateTask.Name = "SQL_Truncate";
        truncateTask.Properties["Connection"].SetValue(truncateTask, sqlCm.Name);
        truncateTask.Properties["SqlStatementSource"].SetValue(truncateTask, "TRUNCATE TABLE dbo.SyntheticNestedTarget;");

        var nestedDftHost = (RtTaskHost)seq.Executables.Add("Microsoft.Pipeline");
        nestedDftHost.Name = "DFT_NestedLoad";
        BuildFlatFileToOleDbLoad((MainPipe)nestedDftHost.InnerObject, nestedCsvCm, sqlCm, "[dbo].[SyntheticNestedTarget]");

        seq.PrecedenceConstraints.Add((Executable)truncateTask, (Executable)nestedDftHost);

        var rootDftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        rootDftHost.Name = "DFT_RootLoad";
        BuildFlatFileToOleDbLoad((MainPipe)rootDftHost.InnerObject, rootCsvCm, sqlCm, "[dbo].[SyntheticNestedTarget2]");

        pkg.PrecedenceConstraints.Add((Executable)seqExec, (Executable)rootDftHost);

        pkg.SaveToXML(out var xml, null);
        // Byte-preserving write, not File.WriteAllText with a default encoding -- CLAUDE.md
        // trap 18: this file's exact bytes feed the extractor's golden-file tests.
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// SQL_PreLoad (TRUNCATE) -> DFT_Load (Flat File Source -> Derived Column -> OLE DB
    /// Destination) -> SQL_PostLoad (UPDATE), a single straight chain. Built specifically
    /// because SyntheticParallelShapes.dtsx's own post-flow Execute SQL Task (Branch 1)
    /// follows a Data Flow Task with no Derived Column, so PackageGenerator never wires that
    /// flow into a Program.cs at all (a separate, unrelated "no Derived Column" gap) -- this
    /// fixture's flow DOES have one, so it's the one that can prove "Execute SQL Task after a
    /// Data Flow Task" generates a runnable Program.cs end to end, not just at the planner/
    /// emitter unit-test level. Lives under tests/Ssis.Extract.Tests/Fixtures/ (test-tier, not
    /// SSDT-registered), same as SyntheticLookupSplit.dtsx -- no reason for this one to be
    /// visible in SSDT's Solution Explorer.
    /// </summary>
    private static int BuildPostFlowSqlFixture(string outputPath)
    {
        var csvPath = Path.Combine(TestFixturesDir(), "synthetic-post-flow-sql-csv", "SyntheticPostFlowSql.csv");
        if (!File.Exists(csvPath))
        {
            Console.Error.WriteLine($"error: expected CSV fixture at {csvPath} -- this tool's checked-in test fixtures are missing or moved.");
            return 2;
        }

        var pkg = new RtPackage { Name = "SyntheticPostFlowSql" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var csvCm = AddFlatFileConnectionManager(pkg, "CM_PostFlowCsv", csvPath,
            ("ID", "DT_I4", 0, 0, 0),
            ("Name", "DT_WSTR", 50, 0, 0));

        var preLoadTask = AddExecuteSql(pkg, "SQL_PreLoad", sqlCm, "TRUNCATE TABLE dbo.SyntheticPostFlowTarget;");

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Load";
        BuildFlatFileToOleDbLoad((MainPipe)dftHost.InnerObject, csvCm, sqlCm, "[dbo].[SyntheticPostFlowTarget]");

        var postLoadTask = AddExecuteSql(pkg, "SQL_PostLoad", sqlCm, "UPDATE dbo.SyntheticPostFlowTarget SET Name = UPPER(Name);");

        pkg.PrecedenceConstraints.Add(preLoadTask, dftHost);
        pkg.PrecedenceConstraints.Add(dftHost, postLoadTask);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// PURE MEASUREMENT PROBE, not a translation-target fixture -- built to answer three real
    /// questions about OnError semantics before writing any codegen against the one real
    /// evidenced case (RBC_Demo_ETL's own Package_Advanced.dtsx, a package-root OnError handler
    /// containing one static Execute SQL Task) that no fixture in this repo had ever exercised:
    /// (1) does the handler fire once per failing task, or does the FIRST failure halt the run
    /// before a second, independent failure gets a chance to happen; (2) does it fire when the
    /// failure happens in a CHILD container that declares no handler of its own (propagation up
    /// the tree); (3) does a task ordered "on success" after a failing task actually get skipped.
    /// `Package.MaximumErrorCount` is raised well past 1 specifically so a real second failure
    /// gets the chance to occur instead of the run stopping after the first -- at the default of
    /// 1 this fixture could not distinguish "fires once" from "only one failure ever happened."
    /// </summary>
    private static int BuildEventHandlerProbeFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticEventHandlerProbe" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;
        pkg.MaximumErrorCount = 10;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var errHandler = (Microsoft.SqlServer.Dts.Runtime.DtsEventHandler)pkg.EventHandlers.Add("OnError");
        var logTask = (RtTaskHost)errHandler.Executables.Add("Microsoft.ExecuteSQLTask");
        logTask.Name = "SQL_LogOnError";
        logTask.Properties["Connection"].SetValue(logTask, sqlCm.Name);
        logTask.Properties["SqlStatementSource"].SetValue(logTask,
            "INSERT INTO dbo.SyntheticEventHandlerLog (Marker) VALUES (N'onerror-fired');");

        // A child container with NO handler of its own -- its failure must propagate up to the
        // package-root handler above for that handler to fire at all here.
        var seq = (Microsoft.SqlServer.Dts.Runtime.Sequence)pkg.Executables.Add("STOCK:SEQUENCE");
        seq.Name = "SEQ_A";

        var failA = (RtTaskHost)seq.Executables.Add("Microsoft.ExecuteSQLTask");
        failA.Name = "SQL_FailA";
        failA.Properties["Connection"].SetValue(failA, sqlCm.Name);
        failA.Properties["SqlStatementSource"].SetValue(failA, "RAISERROR('probe failure A', 16, 1);");

        var afterA = (RtTaskHost)seq.Executables.Add("Microsoft.ExecuteSQLTask");
        afterA.Name = "SQL_AfterA";
        afterA.Properties["Connection"].SetValue(afterA, sqlCm.Name);
        afterA.Properties["SqlStatementSource"].SetValue(afterA,
            "INSERT INTO dbo.SyntheticEventHandlerLog (Marker) VALUES (N'after-a-ran');");
        // Default constraint value is Success -- afterA must NOT run once failA fails.
        seq.PrecedenceConstraints.Add((Executable)failA, (Executable)afterA);

        // A second, INDEPENDENT root-level failure with no ordering relationship to SEQ_A at
        // all -- this is what actually answers "does the handler fire more than once."
        var failB = AddExecuteSql(pkg, "SQL_FailB", sqlCm, "RAISERROR('probe failure B', 16, 1);");

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// SQL_PreLoad (TRUNCATE, primary CM) -> DFT_Load (CSV -> Derived Column LoadedAtUtc ->
    /// OLE DB Destination, primary CM) -> SQL_CacheSet_SecondDb (INSERT, SECOND connection
    /// manager -- a genuinely different database on the same instance). Reproduces, minimally,
    /// the real bug found running RBC_Demo_ETL's own Package_Legacy.dtsx end to end:
    /// SQL_CacheSet_SecondDb's own connection manager (CM_SQL_SSISDemoCache) is not the
    /// package's primary one (CM_SQL_SSISDemo) -- before the fix this proves, every Execute
    /// SQL Task ran through the package's ONE shared connection regardless, so this task's own
    /// statement silently ran against the wrong database. See
    /// synthetic-second-connection-sql-tables.sql for the two backing databases.
    /// </summary>
    private static int BuildSecondConnectionSqlFixture(string outputPath)
    {
        var csvPath = Path.Combine(TestFixturesDir(), "synthetic-second-connection-sql-csv", "SyntheticSecondConnectionSql.csv");
        if (!File.Exists(csvPath))
        {
            Console.Error.WriteLine($"error: expected CSV fixture at {csvPath} -- this tool's checked-in test fixtures are missing or moved.");
            return 2;
        }

        var pkg = new RtPackage { Name = "SyntheticSecondConnectionSql" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var secondSqlCm = pkg.Connections.Add("OLEDB");
        secondSqlCm.Name = "CM_SqlSecondDb";
        secondSqlCm.ConnectionString = SecondarySqlConnectionString;

        var csvCm = AddFlatFileConnectionManager(pkg, "CM_SecondConnectionCsv", csvPath,
            ("ID", "DT_I4", 0, 0, 0),
            ("Name", "DT_WSTR", 50, 0, 0));

        var preLoadTask = AddExecuteSql(pkg, "SQL_PreLoad", sqlCm, "TRUNCATE TABLE dbo.SyntheticSecondConnectionTarget;");

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Load";
        BuildFlatFileToOleDbLoad((MainPipe)dftHost.InnerObject, csvCm, sqlCm, "[dbo].[SyntheticSecondConnectionTarget]");

        var secondDbTask = AddExecuteSql(pkg, "SQL_CacheSet_SecondDb", secondSqlCm,
            "INSERT INTO dbo.SyntheticSecondConnectionLog (LogKey, LogValue) VALUES (N'LegacyImport', N'fixed-width import completed');");

        pkg.PrecedenceConstraints.Add(preLoadTask, dftHost);
        pkg.PrecedenceConstraints.Add(dftHost, secondDbTask);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// OLE DB Source (SqlCommand mode, AccessMode=2) -> Derived Column (LoadedAtUtc &lt;-
    /// GETUTCDATE()) -> OLE DB Destination. Built specifically to prove ssisx generate's OLE
    /// DB Source support reaches a real, compiling, RUNNABLE Program.cs -- unlike
    /// SyntheticParallelShapes.dtsx's own OLE DB Source evidence (Branch 2, a direct copy
    /// with no Derived Column, which hits the separate "no Derived Column" gate and is never
    /// wired). Requires the source table pre-seeded with rows (unlike every other fixture's
    /// tables, which only need to exist for ReinitializeMetaData) -- see
    /// synthetic-oledb-source-tables.sql -- so a real end-to-end run actually reads rows.
    /// Lives under tests/Ssis.Extract.Tests/Fixtures/ (test-tier, not SSDT-registered).
    /// </summary>
    /// <summary>
    /// OLE DB Source -> Lookup (full cache, fail on no match) -> OLE DB Destination.
    ///
    /// The point of this fixture is the one thing <c>SyntheticLookupSplit.dtsx</c> never had: a
    /// Lookup whose input column is actually MAPPED, so it carries the
    /// <c>JoinToReferenceColumn</c> property SSIS really does persist. That fixture's absence of
    /// one is what led this project to conclude for months that SSIS "does not persist a Lookup's
    /// join key" -- see <c>PackageGenerator.TryDeriveLookupJoinKey</c>'s own doc comment.
    ///
    /// Also deliberately single-routed: NoMatchBehavior is left at fail-on-no-match and only the
    /// match output is attached, which is the one Lookup shape the generator wires today (a
    /// redirected no-match branch is a composed shape it gaps instead).
    /// </summary>
    private static int BuildLookupSingleFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticLookupSingle" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_LookupLoad";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null,
            sqlCommand: "SELECT ID, Country FROM dbo.SyntheticLookupSingleInput");
        ResolveOleDbMetadata(srcMeta, isDestination: false);
        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);

        // --- the Lookup itself ---
        var lookupMeta = pipe.ComponentMetaDataCollection.New();
        lookupMeta.ComponentClassID = "Microsoft.Lookup";
        var lookupInst = lookupMeta.Instantiate();
        lookupInst.ProvideComponentProperties();
        lookupMeta.Name = "LKP_Country";

        var lookupConn = lookupMeta.RuntimeConnectionCollection[0];
        lookupConn.ConnectionManagerID = sqlCm.ID;
        lookupConn.ConnectionManager = DtsConvert.GetExtendedInterface(sqlCm);

        lookupInst.SetComponentProperty("CacheType", 0);        // full cache
        lookupInst.SetComponentProperty("NoMatchBehavior", 0);  // fail on no match -> one routed output
        lookupInst.SetComponentProperty("SqlCommand",
            "SELECT CountryName, CountryCode, Region FROM dbo.SyntheticLookupSingleReference");

        AttachPath(pipe, srcOutput, lookupMeta.InputCollection[0]);
        lookupInst.AcquireConnections(null);
        lookupInst.ReinitializeMetaData();

        // Map the joining input column. This is what writes JoinToReferenceColumn -- the whole
        // reason this fixture exists.
        var lookupInput = lookupMeta.InputCollection[0];
        var virtualInput = lookupInput.GetVirtualInput();
        var virtualCountry = virtualInput.VirtualInputColumnCollection.Cast<IDTSVirtualInputColumn100>()
            .First(c => c.Name == "Country");
        var mappedCountry = lookupInst.SetUsageType(
            lookupInput.ID, virtualInput, virtualCountry.LineageID, DTSUsageType.UT_READONLY);
        lookupInst.SetInputColumnProperty(lookupInput.ID, mappedCountry.ID, "JoinToReferenceColumn", "CountryName");

        // Copy two reference columns onto matched rows.
        var matchOutput = lookupMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        // NOTE: do NOT call SetOutputColumnDataTypeProperties here -- it throws COMException
        // 0xC020401A for a Lookup, exactly as it does for a Microsoft.Aggregate output column. The
        // component derives a copied column's type from the reference column itself, so setting
        // CopyFromReferenceColumn is both necessary and sufficient.
        foreach (var (colName, refCol) in new[] { ("CountryCode", "CountryCode"), ("Region", "Region") })
        {
            var outCol = lookupInst.InsertOutputColumnAt(matchOutput.ID, matchOutput.OutputColumnCollection.Count, colName, "");
            lookupInst.SetOutputColumnProperty(matchOutput.ID, outCol.ID, "CopyFromReferenceColumn", refCol);
        }

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticLookupSingleTarget]");
        AttachPath(pipe, matchOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// The real evidenced Lookup+Aggregate composed shape (RBC_Demo_ETL's own
    /// DFT_LookupAndAggregate), built 2026-09-02: OLE DB Source (Country, CustomerID) -> Lookup
    /// (full cache, NoMatchBehavior=1/REDIRECT -- unlike BuildLookupSingleFixture's own
    /// fail-on-no-match, this is the first fixture needing a genuinely wired No-Match output) ->
    /// Multicast -> {Output 1 -> Aggregate (GroupBy Region, Count CustomerID) -> OLE DB
    /// Destination, Output 2 -> RowCount dead end}, Lookup's own No-Match output -> a SECOND
    /// RowCount dead end. Both RowCount targets are genuinely never read anywhere else in this
    /// package -- the whole point of the discard recognition this fixture exercises.
    /// </summary>
    private static int BuildLookupThenAggregateFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticLookupThenAggregate" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        pkg.Variables.Add("MatchedRows", false, "User", 0);
        pkg.Variables.Add("UnmatchedRows", false, "User", 0);

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_LookupAndAggregate";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null,
            sqlCommand: "SELECT Country, CustomerID FROM dbo.SyntheticLookupThenAggregateSource");
        ResolveOleDbMetadata(srcMeta, isDestination: false);
        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);

        // --- the Lookup itself, NoMatchBehavior=1 this time (redirect, not fail) ---
        var lookupMeta = pipe.ComponentMetaDataCollection.New();
        lookupMeta.ComponentClassID = "Microsoft.Lookup";
        var lookupInst = lookupMeta.Instantiate();
        lookupInst.ProvideComponentProperties();
        lookupMeta.Name = "LKP_Country";

        var lookupConn = lookupMeta.RuntimeConnectionCollection[0];
        lookupConn.ConnectionManagerID = sqlCm.ID;
        lookupConn.ConnectionManager = DtsConvert.GetExtendedInterface(sqlCm);

        lookupInst.SetComponentProperty("CacheType", 0);        // full cache
        lookupInst.SetComponentProperty("NoMatchBehavior", 1);  // redirect to no-match output
        lookupInst.SetComponentProperty("SqlCommand",
            "SELECT CountryName, Region FROM dbo.SyntheticLookupThenAggregateReference");

        AttachPath(pipe, srcOutput, lookupMeta.InputCollection[0]);
        lookupInst.AcquireConnections(null);
        lookupInst.ReinitializeMetaData();
        lookupInst.ReleaseConnections();

        var lookupInput = lookupMeta.InputCollection[0];
        var virtualInput = lookupInput.GetVirtualInput();
        var virtualCountry = virtualInput.VirtualInputColumnCollection.Cast<IDTSVirtualInputColumn100>()
            .First(c => c.Name == "Country");
        var mappedCountry = lookupInst.SetUsageType(
            lookupInput.ID, virtualInput, virtualCountry.LineageID, DTSUsageType.UT_READONLY);
        lookupInst.SetInputColumnProperty(lookupInput.ID, mappedCountry.ID, "JoinToReferenceColumn", "CountryName");

        var matchOutput = lookupMeta.OutputCollection.Cast<IDTSOutput100>().First(o => o.Name == "Lookup Match Output");
        var noMatchOutput = lookupMeta.OutputCollection.Cast<IDTSOutput100>().First(o => o.Name == "Lookup No Match Output");

        var regionOutCol = lookupInst.InsertOutputColumnAt(matchOutput.ID, matchOutput.OutputColumnCollection.Count, "Region", "");
        lookupInst.SetOutputColumnProperty(matchOutput.ID, regionOutCol.ID, "CopyFromReferenceColumn", "Region");

        // --- Lookup's own No-Match output -> a dead-end RowCount, discarded ---
        var rcUnmatchedMeta = pipe.ComponentMetaDataCollection.New();
        rcUnmatchedMeta.ComponentClassID = "Microsoft.RowCount";
        var rcUnmatchedInst = rcUnmatchedMeta.Instantiate();
        rcUnmatchedInst.ProvideComponentProperties();
        rcUnmatchedMeta.Name = "RC_UnmatchedRows";
        rcUnmatchedInst.SetComponentProperty("VariableName", "User::UnmatchedRows");
        AttachPath(pipe, noMatchOutput, rcUnmatchedMeta.InputCollection[0]);
        rcUnmatchedInst.AcquireConnections(null);
        rcUnmatchedInst.ReinitializeMetaData();
        rcUnmatchedInst.ReleaseConnections();

        // --- Multicast downstream of the Match output ---
        var mcMeta = pipe.ComponentMetaDataCollection.New();
        mcMeta.ComponentClassID = "Microsoft.Multicast";
        var mcInst = mcMeta.Instantiate();
        mcInst.ProvideComponentProperties();
        mcMeta.Name = "MCAST_Matched";
        AttachPath(pipe, matchOutput, mcMeta.InputCollection[0]);
        mcInst.AcquireConnections(null);
        mcInst.ReinitializeMetaData();
        mcInst.ReleaseConnections();

        // Same auto-provisioning behavior as BuildMulticastFixture: exactly one output exists
        // right after ReinitializeMetaData; attaching a path to it grows the collection to two.
        var mcOutput1 = mcMeta.OutputCollection.Cast<IDTSOutput100>().Single();

        // --- Output 1 -> Aggregate -> OLE DB Destination (the one real live path) ---
        var aggMeta = pipe.ComponentMetaDataCollection.New();
        aggMeta.ComponentClassID = "Microsoft.Aggregate";
        var aggInst = aggMeta.Instantiate();
        aggInst.ProvideComponentProperties();
        aggMeta.Name = "AGG_ByRegion";

        AttachPath(pipe, mcOutput1, aggMeta.InputCollection[0]);
        aggInst.AcquireConnections(null);
        aggInst.ReinitializeMetaData();
        aggInst.ReleaseConnections();

        // Unlike BuildAggregateFixture's own plain OLE DB Source (Region, CustomerID only --
        // every virtual input column IS referenced by an output), this fixture's Multicast
        // passes through EVERY Lookup Match output column, including "Country" -- confirmed
        // real via a dtexec probe: mapping it as UT_READONLY without an AggregationColumnId
        // referencing it fails validation with 0xC0208218 ("input column ... is not
        // referenced"). Aggregate demands every mapped input column actually be used by SOME
        // output, unlike a plain destination. So only the two columns this Aggregate actually
        // needs are mapped here.
        var aggInput = aggMeta.InputCollection[0];
        var aggVirtualInput = aggInput.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in aggVirtualInput.VirtualInputColumnCollection)
        {
            if (vcol.Name is "Region" or "CustomerID")
                aggInst.SetUsageType(aggInput.ID, aggVirtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }

        var regionInputCol = aggInput.InputColumnCollection.Cast<IDTSInputColumn100>().First(c => c.Name == "Region");
        var customerIdInputCol = aggInput.InputColumnCollection.Cast<IDTSInputColumn100>().First(c => c.Name == "CustomerID");

        var aggOutput = aggMeta.OutputCollection[0];

        var groupByOutCol = aggInst.InsertOutputColumnAt(aggOutput.ID, 0, "Region", "");
        aggInst.SetOutputColumnProperty(aggOutput.ID, groupByOutCol.ID, "AggregationColumnId", regionInputCol.LineageID);
        aggInst.SetOutputColumnProperty(aggOutput.ID, groupByOutCol.ID, "AggregationType", 0); // GroupBy

        var countOutCol = aggInst.InsertOutputColumnAt(aggOutput.ID, 1, "CustomerCount", "");
        aggInst.SetOutputColumnProperty(aggOutput.ID, countOutCol.ID, "AggregationColumnId", customerIdInputCol.LineageID);
        aggInst.SetOutputColumnProperty(aggOutput.ID, countOutCol.ID, "AggregationType", 1); // Count

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticLookupThenAggregateTarget]");
        AttachPath(pipe, aggOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        // --- Output 2 -> a second dead-end RowCount, discarded ---
        var mcOutput2 = mcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => o.ID != mcOutput1.ID);

        var rcMatchedMeta = pipe.ComponentMetaDataCollection.New();
        rcMatchedMeta.ComponentClassID = "Microsoft.RowCount";
        var rcMatchedInst = rcMatchedMeta.Instantiate();
        rcMatchedInst.ProvideComponentProperties();
        rcMatchedMeta.Name = "RC_MatchedRows";
        rcMatchedInst.SetComponentProperty("VariableName", "User::MatchedRows");
        AttachPath(pipe, mcOutput2, rcMatchedMeta.InputCollection[0]);
        rcMatchedInst.AcquireConnections(null);
        rcMatchedInst.ReinitializeMetaData();
        rcMatchedInst.ReleaseConnections();
        // A third Multicast output now exists, unconnected/"dangling" -- left as-is, matching
        // BuildMulticastFixture's own already-confirmed real behavior.

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// A plain Multicast + discard, no Lookup and no Aggregate: OLE DB Source -> Multicast ->
    /// {Output 1 -> OLE DB Destination (the one live branch), Output 2 -> a dead-end RowCount}.
    /// Built 2026-09-02 specifically to prove GenerateMulticastFlow's own defensive discard-skip
    /// independently of the Lookup+Aggregate composed shape -- a direct, foreseeable consequence
    /// of ResolveBranch now accepting a discard, not the real motivating case (which always
    /// reaches GenerateLookupThenAggregateFlow instead, since an Aggregate anywhere in the
    /// pipeline wins dispatch first).
    /// </summary>
    private static int BuildMulticastDiscardFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticMulticastDiscard" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        pkg.Variables.Add("RowsSeen", false, "User", 0);

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_MulticastDiscard";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null,
            sqlCommand: "SELECT ID, Name FROM dbo.SyntheticMulticastDiscardSource");
        ResolveOleDbMetadata(srcMeta, isDestination: false);
        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);

        var mcMeta = pipe.ComponentMetaDataCollection.New();
        mcMeta.ComponentClassID = "Microsoft.Multicast";
        var mcInst = mcMeta.Instantiate();
        mcInst.ProvideComponentProperties();
        mcMeta.Name = "MCAST_Discard";
        AttachPath(pipe, srcOutput, mcMeta.InputCollection[0]);
        mcInst.AcquireConnections(null);
        mcInst.ReinitializeMetaData();
        mcInst.ReleaseConnections();

        var mcOutput1 = mcMeta.OutputCollection.Cast<IDTSOutput100>().Single();

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticMulticastDiscardTarget]");
        AttachPath(pipe, mcOutput1, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        var mcOutput2 = mcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => o.ID != mcOutput1.ID);

        var rcMeta = pipe.ComponentMetaDataCollection.New();
        rcMeta.ComponentClassID = "Microsoft.RowCount";
        var rcInst = rcMeta.Instantiate();
        rcInst.ProvideComponentProperties();
        rcMeta.Name = "RC_RowsSeen";
        rcInst.SetComponentProperty("VariableName", "User::RowsSeen");
        AttachPath(pipe, mcOutput2, rcMeta.InputCollection[0]);
        rcInst.AcquireConnections(null);
        rcInst.ReinitializeMetaData();
        rcInst.ReleaseConnections();

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// A plain Multicast whose two live branches go to TWO DIFFERENT destinations -- one straight
    /// (a passthrough), one through an Aggregate -- with NO Lookup anywhere. Built 2026-09-06 to
    /// reproduce a real, previously-silent correctness bug (not a missing-test gap, unlike every
    /// other synthetic fixture built for this "gaps not guesses" reason): PackageGenerator's own
    /// per-flow dispatch checks flow.Aggregate before flow.Multicast and, once Aggregate is set,
    /// calls GenerateAggregateFlow unconditionally -- which never reads flow.Multicast at all, so
    /// the OTHER branch (this fixture's own straight-passthrough destination) used to be silently
    /// dropped (no file, no gap) while the Aggregate's own row got wired against whichever
    /// destination PackagePlanner picked first, producing a build-breaking CS1061 while
    /// generate-report.md claimed 0 blocking gaps. The ONE real evidenced Aggregate-behind-a-
    /// Multicast shape (RBC_Demo_ETL's own DFT_LookupAndAggregate) always goes through a Lookup
    /// with exactly one live branch -- see BuildLookupThenAggregateFixture -- so this exact
    /// no-Lookup composition is deliberately unevidenced here too; this fixture exists purely to
    /// prove the generator now GAPS it instead of silently mis-generating it.
    ///
    /// Backing tables: tests/Ssis.Extract.Tests/Fixtures/synthetic-multicast-aggregate-sibling-tables.sql.
    /// </summary>
    private static int BuildMulticastAggregateSiblingFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticMulticastAggregateSibling" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_MulticastAggregateSibling";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null,
            sqlCommand: "SELECT ID, Region, CustomerID FROM dbo.SyntheticMulticastAggSiblingSource");
        ResolveOleDbMetadata(srcMeta, isDestination: false);
        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);

        var mcMeta = pipe.ComponentMetaDataCollection.New();
        mcMeta.ComponentClassID = "Microsoft.Multicast";
        var mcInst = mcMeta.Instantiate();
        mcInst.ProvideComponentProperties();
        mcMeta.Name = "MCAST_Split";
        AttachPath(pipe, srcOutput, mcMeta.InputCollection[0]);
        mcInst.AcquireConnections(null);
        mcInst.ReinitializeMetaData();
        mcInst.ReleaseConnections();

        // --- Output 1 -> straight passthrough to Destination A (the branch that used to be
        // silently dropped) ---
        var mcOutput1 = mcMeta.OutputCollection.Cast<IDTSOutput100>().Single();

        var destAMeta = AddOleDbComponent(pipe, "OLE DB Destination A", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticMulticastAggSiblingTargetA]");
        AttachPath(pipe, mcOutput1, destAMeta.InputCollection[0]);
        ResolveOleDbMetadata(destAMeta, isDestination: true);

        // --- Output 2 -> Aggregate -> Destination B (the Aggregate's own real target) ---
        var mcOutput2 = mcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => o.ID != mcOutput1.ID);

        var aggMeta = pipe.ComponentMetaDataCollection.New();
        aggMeta.ComponentClassID = "Microsoft.Aggregate";
        var aggInst = aggMeta.Instantiate();
        aggInst.ProvideComponentProperties();
        aggMeta.Name = "AGG_ByRegion";

        AttachPath(pipe, mcOutput2, aggMeta.InputCollection[0]);
        aggInst.AcquireConnections(null);
        aggInst.ReinitializeMetaData();
        aggInst.ReleaseConnections();

        // Same real constraint BuildLookupThenAggregateFixture's own comment already measured:
        // Aggregate demands every mapped input column actually be used by SOME output, unlike a
        // plain destination -- only the two columns this Aggregate actually needs are mapped.
        var aggInput = aggMeta.InputCollection[0];
        var aggVirtualInput = aggInput.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in aggVirtualInput.VirtualInputColumnCollection)
        {
            if (vcol.Name is "Region" or "CustomerID")
                aggInst.SetUsageType(aggInput.ID, aggVirtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }

        var regionInputCol = aggInput.InputColumnCollection.Cast<IDTSInputColumn100>().First(c => c.Name == "Region");
        var customerIdInputCol = aggInput.InputColumnCollection.Cast<IDTSInputColumn100>().First(c => c.Name == "CustomerID");

        var aggOutput = aggMeta.OutputCollection[0];

        var groupByOutCol = aggInst.InsertOutputColumnAt(aggOutput.ID, 0, "Region", "");
        aggInst.SetOutputColumnProperty(aggOutput.ID, groupByOutCol.ID, "AggregationColumnId", regionInputCol.LineageID);
        aggInst.SetOutputColumnProperty(aggOutput.ID, groupByOutCol.ID, "AggregationType", 0); // GroupBy

        var countOutCol = aggInst.InsertOutputColumnAt(aggOutput.ID, 1, "CustomerCount", "");
        aggInst.SetOutputColumnProperty(aggOutput.ID, countOutCol.ID, "AggregationColumnId", customerIdInputCol.LineageID);
        aggInst.SetOutputColumnProperty(aggOutput.ID, countOutCol.ID, "AggregationType", 1); // Count

        var destBMeta = AddOleDbComponent(pipe, "OLE DB Destination B", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticMulticastAggSiblingTargetB]");
        AttachPath(pipe, aggOutput, destBMeta.InputCollection[0]);
        ResolveOleDbMetadata(destBMeta, isDestination: true);
        // A third Multicast output now exists, unconnected/"dangling" -- left as-is, matching
        // BuildMulticastFixture's own already-confirmed real behavior.

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// OLE DB Source (a literal 3-row VALUES() list, no separate source table needed) ->
    /// RowCount (User::RowsLoaded, MID-CHAIN passthrough to a real destination -- NOT a
    /// discarded dead end, the already-handled 2026-09-02 shape) -> OLE DB Destination, then a
    /// post-flow Script Task reads the variable back and logs it into a real table -- gap-audit
    /// Phase 3.2's motivating shape. Two things a real dtexec run against this fixture must
    /// prove: RowCount's own count is genuinely "rows that reached this component" (not, say,
    /// the source's own declared row estimate or something measured a different way), and a
    /// downstream STEP correctly sees the TRUE final count rather than a stale/default one --
    /// both unverified assumptions until measured for real.
    ///
    /// Backing tables: tests/Ssis.Extract.Tests/Fixtures/synthetic-row-count-variable-tables.sql.
    /// </summary>
    private static int BuildRowCountVariableFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticRowCountVariable" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var rowsLoaded = pkg.Variables.Add("RowsLoaded", false, "User", 0);

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Load";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null,
            sqlCommand: "SELECT * FROM (VALUES (1,N'Alice'),(2,N'Bob'),(3,N'Carol')) AS X(ID,Name)");
        ResolveOleDbMetadata(srcMeta, isDestination: false);
        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);

        var rcMeta = pipe.ComponentMetaDataCollection.New();
        rcMeta.ComponentClassID = "Microsoft.RowCount";
        var rcInst = rcMeta.Instantiate();
        rcInst.ProvideComponentProperties(); // resets Name -- must set Name after this, see AddOleDbComponent's comment
        rcMeta.Name = "RC_RowsLoaded";
        rcInst.SetComponentProperty("VariableName", "User::RowsLoaded");
        AttachPath(pipe, srcOutput, rcMeta.InputCollection[0]);
        rcInst.AcquireConnections(null);
        rcInst.ReinitializeMetaData();
        rcInst.ReleaseConnections();

        var rcOutput = rcMeta.OutputCollection.Cast<IDTSOutput100>().Single();

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticRowCountVariableTarget]");
        AttachPath(pipe, rcOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        var logTask = AddScriptTask(pkg, "SCR_LogCount", readOnlyVariables: rowsLoaded.QualifiedName);
        pkg.PrecedenceConstraints.Add(dftHost, (Executable)logTask);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    private static int BuildOleDbSourceTransformFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticOleDbSourceTransform" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Load";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Amount FROM dbo.SyntheticOleDbSourceInput");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var mainOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        var derivedMeta = BuildDerivedColumnLoadedAtUtc(pipe, mainOutput);

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticOleDbSourceTarget]");
        AttachPath(pipe, derivedMeta.OutputCollection[0], destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// OLE DB Source (SqlCommand mode) -> Derived Column (LoadedAtUtc &lt;- GETUTCDATE(), reused
    /// unchanged from BuildOleDbSourceTransformFixture -- the split's routing condition
    /// references only a raw source column (Amount), never this computed one, matching scope
    /// decision 1 in CLAUDE.md's "Three new component types" section) -> Conditional Split
    /// (one case, "Amount &gt; 1000" -> HighValue; default -> LowValue) -> 2 OLE DB Destinations.
    /// Built to prove ssisx generate's Conditional Split support reaches a real, compiling,
    /// RUNNABLE Program.cs -- SyntheticLookupSplit.dtsx's own Conditional Split is masked by
    /// its Lookup (PackageGenerator's Lookup gate wins when a flow has both), so it proves the
    /// READER handles Conditional Split's XML, never codegen.
    ///
    /// <para><b>Object-model API confirmed empirically before/while writing this</b> (same "ask
    /// the runtime, don't guess" discipline as trap 12), not assumed from general SSIS
    /// documentation -- three real findings, none obvious from the API surface alone:
    /// <list type="bullet">
    /// <item><c>IDTSDesigntimeComponent100.SetOutputProperty(int, string, object)</c> and
    /// <c>.InsertOutput(DTSInsertPlacement, int)</c> both exist (reflected off this GAC's
    /// DTSPipelineWrap 16.0.0.0 before writing any calling code), and <c>IDTSOutput100.Name</c>
    /// has a public setter (no <c>IDTSName100</c> cast needed, unlike a flat-file column).</item>
    /// <item><c>IsDefaultOut</c> is READ-ONLY -- calling <c>SetOutputProperty(id, "IsDefaultOut",
    /// true)</c> throws <c>COMException 0xC0204006</c>. The component manages it itself: a fresh
    /// Conditional Split's <c>ReinitializeMetaData()</c> creates exactly one non-error output
    /// (the implicit default) plus the fixed error output, and that output is automatically
    /// marked <c>IsDefaultOut="true"</c> in the saved XML once a real case exists, purely by
    /// never having been given an Expression of its own -- nothing needs to (or can) set it.</item>
    /// <item><c>InsertOutput</c>'s second parameter is an anchor output ID, not a "no anchor"
    /// sentinel -- passing an existing output's own ID (trying to insert "before" it) also threw
    /// <c>0xC0204006</c>; <c>0</c> (append at the end) is what actually works.</item>
    /// </list>
    /// <c>SetOutputProperty("Expression", ...)</c> is NOT stored verbatim, unlike Derived
    /// Column's own Expression/FriendlyExpression pair (<see cref="BuildDerivedColumnLoadedAtUtc"/>):
    /// the component validates the literal text ("Amount &gt; 1000") against the input's real
    /// columns and rewrites it into the <c>#{lineageId}</c> form at save time, confirmed by
    /// reading the saved XML -- the exact same lineageId-substituted shape
    /// SyntheticLookupSplit.dtsx's own real Conditional Split already has.
    /// <c>FriendlyExpression</c> is stored as the literal text given, unchanged -- the only one
    /// this tool's own reader/RouterEmitter ever consumes.
    /// </para>
    ///
    /// Requires the backing tables from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-conditional-split-tables.sql</c> already
    /// created and seeded on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>. Lives under
    /// tests/Ssis.Extract.Tests/Fixtures/ (test-tier, not SSDT-registered), same as
    /// SyntheticLookupSplit.dtsx/SyntheticOleDbSourceTransform.dtsx.
    /// </summary>
    private static int BuildConditionalSplitFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticConditionalSplit" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_ConditionalSplitDemo";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Amount FROM dbo.SyntheticConditionalSplitInput");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        var derivedMeta = BuildDerivedColumnLoadedAtUtc(pipe, srcOutput);
        var derivedOutput = derivedMeta.OutputCollection[0];

        var splitMeta = pipe.ComponentMetaDataCollection.New();
        splitMeta.ComponentClassID = "Microsoft.ConditionalSplit";
        var splitInst = splitMeta.Instantiate();
        splitInst.ProvideComponentProperties(); // resets Name to the class default -- must set Name after this, see AddOleDbComponent's comment
        splitMeta.Name = "Conditional Split";

        AttachPath(pipe, derivedOutput, splitMeta.InputCollection[0]);
        splitInst.AcquireConnections(null);
        splitInst.ReinitializeMetaData();
        splitInst.ReleaseConnections();

        // Synchronous passthrough -- same "mark every upstream column used" rule as every other
        // component in this file (ResolveOleDbMetadata's destination path, BuildDerivedColumnLoadedAtUtc,
        // BuildPassthroughScriptComponent).
        var splitInput = splitMeta.InputCollection[0];
        var splitVirtualInput = splitInput.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in splitVirtualInput.VirtualInputColumnCollection)
        {
            splitInst.SetUsageType(splitInput.ID, splitVirtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }

        // IsDefaultOut is READ-ONLY, not settable -- confirmed empirically (0xC0204006 COMException
        // when this method first tried SetOutputProperty(defaultOutput.ID, "IsDefaultOut", true)).
        // The component manages this itself: the pre-existing output ReinitializeMetaData() created
        // is automatically marked IsDefaultOut="true" in the saved XML once a real case (below) has
        // been added, purely by virtue of never having been given an Expression of its own. Do not
        // try to set it directly.
        var defaultOutput = splitMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        defaultOutput.Name = "LowValue";

        // InsertOutput's second parameter is an anchor OUTPUT ID, not a "no anchor" sentinel --
        // also confirmed empirically: passing an existing output's own ID (e.g. defaultOutput.ID)
        // as the anchor threw the same 0xC0204006; 0 (append at end) is what actually works.
        var caseOutput = splitInst.InsertOutput(DTSInsertPlacement.IP_AFTER, 0);
        caseOutput.Name = "HighValue";
        // SetOutputProperty("Expression", ...) here is NOT stored verbatim -- the component
        // validates "Amount" against the input's real columns and rewrites it into the
        // #{lineageId} form at save time (confirmed by reading the saved XML), the same
        // lineageId-substituted shape SyntheticLookupSplit.dtsx's own real Conditional Split
        // already has. FriendlyExpression is stored as the literal text given, unchanged --
        // that's the only one this tool's own reader/RouterEmitter ever consumes.
        splitInst.SetOutputProperty(caseOutput.ID, "FriendlyExpression", "Amount > 1000");
        splitInst.SetOutputProperty(caseOutput.ID, "Expression", "Amount > 1000");
        splitInst.SetOutputProperty(caseOutput.ID, "EvaluationOrder", 0);

        var highValueDestMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticHighValue]");
        AttachPath(pipe, caseOutput, highValueDestMeta.InputCollection[0]);
        ResolveOleDbMetadata(highValueDestMeta, isDestination: true);

        var lowValueDestMeta = AddOleDbComponent(pipe, "OLE DB Destination 1", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticLowValue]");
        AttachPath(pipe, defaultOutput, lowValueDestMeta.InputCollection[0]);
        ResolveOleDbMetadata(lowValueDestMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// OLE DB Source (SqlCommand) -> Derived Column (Category &lt;- Amount &gt; 1000 ? "High" :
    /// "Low"; SignFlag &lt;- Amount &lt; 0 ? -1 : 1) -> OLE DB Destination.
    ///
    /// <para>Built to prove ExpressionTranslator's ternary (Conditional) and unary-negate
    /// support (added 2026-08-27) end to end, from ONLY already-supported building blocks --
    /// a numeric comparison, string literals, an int literal negation. RBC_Demo_ETL's own
    /// Package_Transforms.dtsx (DER_Enrich.EmailDomain/.TenureDays) is where ternary/negate were
    /// actually discovered, but that package's own ternary branches ALSO depend on two separate,
    /// still-open gaps (SUBSTRING with a non-literal start argument, DATEDIFF) that would mask
    /// whether ternary/negate themselves work if this fixture reused those expressions verbatim
    /// -- so this fixture deliberately uses simpler branches instead, proving the STRUCTURAL
    /// ternary/negate support in isolation.</para>
    ///
    /// Requires the backing tables from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-ternary-tables.sql</c> already created and
    /// seeded on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>. Lives under
    /// tests/Ssis.Extract.Tests/Fixtures/ (test-tier, not SSDT-registered), same as
    /// SyntheticFindStringTrim.dtsx.
    /// </summary>
    private static int BuildTernaryFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticTernary" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Load";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Amount FROM dbo.SyntheticTernaryInput");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var mainOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        var derivedMeta = BuildDerivedColumnTernary(pipe, mainOutput);

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticTernaryTarget]");
        AttachPath(pipe, derivedMeta.OutputCollection[0], destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// A Derived Column that passes every upstream column through untouched and adds TWO new
    /// columns -- <c>Category &lt;- Amount &gt; 1000 ? "High" : "Low"</c> (ternary of string
    /// literals) and <c>SignFlag &lt;- Amount &lt; 0 ? -1 : 1</c> (ternary + unary negate, int) --
    /// proving both new expression shapes as VALUE-producing expressions in one component, the
    /// same shape RBC_Demo_ETL's own DER_Enrich uses for multiple computed columns. Same
    /// passthrough/InsertOutputColumnAt mechanics as <see cref="BuildDerivedColumnLiteralTag"/>.
    /// </summary>
    private static IDTSComponentMetaData100 BuildDerivedColumnTernary(MainPipe pipe, IDTSOutput100 upstreamOutput)
    {
        var meta = pipe.ComponentMetaDataCollection.New();
        meta.ComponentClassID = "Microsoft.DerivedColumn";
        var inst = meta.Instantiate();
        inst.ProvideComponentProperties(); // resets Name -- must set Name after this, see AddOleDbComponent's comment
        meta.Name = "DER_Enrich";

        AttachPath(pipe, upstreamOutput, meta.InputCollection[0]);
        inst.AcquireConnections(null);
        inst.ReinitializeMetaData();
        inst.ReleaseConnections();

        var input = meta.InputCollection[0];
        var virtualInput = input.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in virtualInput.VirtualInputColumnCollection)
        {
            inst.SetUsageType(input.ID, virtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }

        var output = meta.OutputCollection[0];

        var categoryCol = inst.InsertOutputColumnAt(output.ID, 0, "Category", "");
        // DT_WSTR is Unicode -- codepage is 0, not 1252 (see BuildDerivedColumnLiteralTag's own
        // identical note; confirmed empirically, 1252 throws 0xC0204025).
        inst.SetOutputColumnDataTypeProperties(output.ID, categoryCol.ID, DataType.DT_WSTR, 10, 0, 0, 0);
        var categoryExpr = "Amount > 1000 ? \"High\" : \"Low\"";
        inst.SetOutputColumnProperty(output.ID, categoryCol.ID, "FriendlyExpression", categoryExpr);
        inst.SetOutputColumnProperty(output.ID, categoryCol.ID, "Expression", categoryExpr);

        var signFlagCol = inst.InsertOutputColumnAt(output.ID, 1, "SignFlag", "");
        inst.SetOutputColumnDataTypeProperties(output.ID, signFlagCol.ID, DataType.DT_I4, 0, 0, 0, 0);
        var signFlagExpr = "Amount < 0 ? -1 : 1";
        inst.SetOutputColumnProperty(output.ID, signFlagCol.ID, "FriendlyExpression", signFlagExpr);
        inst.SetOutputColumnProperty(output.ID, signFlagCol.ID, "Expression", signFlagExpr);

        return meta;
    }

    /// <summary>
    /// OLE DB Source (SqlCommand) -> Derived Column (EmailDomain &lt;-
    /// FINDSTRING(TRIM(Email),"@",1) &gt; 0 ? SUBSTRING(TRIM(Email),
    /// FINDSTRING(TRIM(Email),"@",1) + 1,100) : "(none)") -> OLE DB Destination.
    ///
    /// <para>Reproduces RBC_Demo_ETL's own Package_Transforms.dtsx (DER_Enrich.EmailDomain)
    /// expression VERBATIM, not a simplified stand-in -- unlike SyntheticFindStringTrim.dtsx and
    /// SyntheticTernary.dtsx (each deliberately simpler, to isolate one feature before the rest
    /// existed), by this point every piece this expression needs (FINDSTRING, TRIM, ternary,
    /// numeric '+', a SUBSTRING start argument that's itself a computed expression) is supported,
    /// so this fixture closes the loop on the actual real-world column rather than a stand-in
    /// for it.</para>
    ///
    /// Requires the backing tables from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-email-domain-tables.sql</c> already created
    /// and seeded on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>. Lives under
    /// tests/Ssis.Extract.Tests/Fixtures/ (test-tier, not SSDT-registered), same as
    /// SyntheticTernary.dtsx.
    /// </summary>
    private static int BuildEmailDomainFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticEmailDomain" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Load";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Email FROM dbo.SyntheticEmailDomainInput");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var mainOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        var derivedMeta = BuildDerivedColumnEmailDomain(pipe, mainOutput);

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticEmailDomainTarget]");
        AttachPath(pipe, derivedMeta.OutputCollection[0], destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// A Derived Column that passes every upstream column through untouched and adds ONE new
    /// column, the verbatim RBC_Demo_ETL DER_Enrich.EmailDomain expression -- see
    /// <see cref="BuildEmailDomainFixture"/>'s own doc comment for why it's reproduced exactly
    /// rather than simplified. Same passthrough/InsertOutputColumnAt mechanics as
    /// <see cref="BuildDerivedColumnLiteralTag"/>.
    /// </summary>
    private static IDTSComponentMetaData100 BuildDerivedColumnEmailDomain(MainPipe pipe, IDTSOutput100 upstreamOutput)
    {
        var meta = pipe.ComponentMetaDataCollection.New();
        meta.ComponentClassID = "Microsoft.DerivedColumn";
        var inst = meta.Instantiate();
        inst.ProvideComponentProperties(); // resets Name -- must set Name after this, see AddOleDbComponent's comment
        meta.Name = "DER_Enrich";

        AttachPath(pipe, upstreamOutput, meta.InputCollection[0]);
        inst.AcquireConnections(null);
        inst.ReinitializeMetaData();
        inst.ReleaseConnections();

        var input = meta.InputCollection[0];
        var virtualInput = input.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in virtualInput.VirtualInputColumnCollection)
        {
            inst.SetUsageType(input.ID, virtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }

        var output = meta.OutputCollection[0];
        var newCol = inst.InsertOutputColumnAt(output.ID, 0, "EmailDomain", "");
        // DT_WSTR is Unicode -- codepage is 0, not 1252 (see BuildDerivedColumnLiteralTag's own
        // identical note; confirmed empirically, 1252 throws 0xC0204025).
        inst.SetOutputColumnDataTypeProperties(output.ID, newCol.ID, DataType.DT_WSTR, 200, 0, 0, 0);
        var expr = "FINDSTRING(TRIM(Email),\"@\",1) > 0 ? SUBSTRING(TRIM(Email),FINDSTRING(TRIM(Email),\"@\",1) + 1,100) : \"(none)\"";
        inst.SetOutputColumnProperty(output.ID, newCol.ID, "FriendlyExpression", expr);
        inst.SetOutputColumnProperty(output.ID, newCol.ID, "Expression", expr);

        return meta;
    }

    /// <summary>
    /// OLE DB Source (SqlCommand) -> Derived Column (TenureDays &lt;- ISNULL(SignupDate) ? -1 :
    /// DATEDIFF("dd",SignupDate,GETDATE())) -> OLE DB Destination.
    ///
    /// <para>Now the FULL RBC_Demo_ETL DER_Enrich.TenureDays expression, verbatim -- originally
    /// built without the ISNULL(...) wrapper (see git history / CLAUDE.md's own "DATEDIFF..."
    /// section) because it surfaced a real, separate gap: no SQL row/entity emitter tracked
    /// column nullability, so ISNULL's own "x is null" failed to COMPILE against the resulting
    /// non-nullable DateOnly. That gap is now closed (NullabilityInference +
    /// SqlRowEmitter/SqlRowReaderEmitter/EntityEmitter/ExpressionTranslator changes, same
    /// session) -- SignupDate is nullable again in this fixture's own backing table
    /// specifically to exercise the fix for real.</para>
    ///
    /// Requires the backing tables from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-datediff-tables.sql</c> already created
    /// and seeded on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c> -- seed CLOSE TO RUN TIME (same
    /// UTC calendar day), see that script's own header for why. Lives under
    /// tests/Ssis.Extract.Tests/Fixtures/ (test-tier, not SSDT-registered), same as
    /// SyntheticEmailDomain.dtsx.
    /// </summary>
    private static int BuildDateDiffFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticDateDiff" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Load";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, SignupDate FROM dbo.SyntheticDateDiffInput");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var mainOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        var derivedMeta = BuildDerivedColumnTenureDays(pipe, mainOutput);

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticDateDiffTarget]");
        AttachPath(pipe, derivedMeta.OutputCollection[0], destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// A Derived Column that passes every upstream column through untouched and adds ONE new
    /// column, the verbatim RBC_Demo_ETL DER_Enrich.TenureDays expression -- see
    /// <see cref="BuildDateDiffFixture"/>'s own doc comment. Same passthrough/
    /// InsertOutputColumnAt mechanics as <see cref="BuildDerivedColumnLiteralTag"/>.
    /// </summary>
    private static IDTSComponentMetaData100 BuildDerivedColumnTenureDays(MainPipe pipe, IDTSOutput100 upstreamOutput)
    {
        var meta = pipe.ComponentMetaDataCollection.New();
        meta.ComponentClassID = "Microsoft.DerivedColumn";
        var inst = meta.Instantiate();
        inst.ProvideComponentProperties(); // resets Name -- must set Name after this, see AddOleDbComponent's comment
        meta.Name = "DER_Enrich";

        AttachPath(pipe, upstreamOutput, meta.InputCollection[0]);
        inst.AcquireConnections(null);
        inst.ReinitializeMetaData();
        inst.ReleaseConnections();

        var input = meta.InputCollection[0];
        var virtualInput = input.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in virtualInput.VirtualInputColumnCollection)
        {
            inst.SetUsageType(input.ID, virtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }

        var output = meta.OutputCollection[0];
        var newCol = inst.InsertOutputColumnAt(output.ID, 0, "TenureDays", "");
        inst.SetOutputColumnDataTypeProperties(output.ID, newCol.ID, DataType.DT_I4, 0, 0, 0, 0);
        var expr = "ISNULL(SignupDate) ? -1 : DATEDIFF(\"dd\",SignupDate,GETDATE())";
        inst.SetOutputColumnProperty(output.ID, newCol.ID, "FriendlyExpression", expr);
        inst.SetOutputColumnProperty(output.ID, newCol.ID, "Expression", expr);

        return meta;
    }

    /// <summary>
    /// OLE DB Source (SqlCommand: ID, CustomerIdText, SignupDateText -- all three plain
    /// strings) -> Data Conversion (CustomerIdText -&gt; CustomerId_i4 (DT_I4, IgnoreFailure/
    /// IgnoreFailure); SignupDateText -&gt; SignupDate_dt (DT_DBDATE, IgnoreFailure/
    /// IgnoreFailure)) -> OLE DB Destination.
    ///
    /// <para>Reproduces RBC_Demo_ETL's own Package_Transforms.dtsx (DFT_DerivedAndSplit,
    /// DCONV_Types) shape exactly: two string-to-typed conversions, both dispositions
    /// IgnoreFailure, discovered testing this tool against a real third-party portfolio
    /// (SSIS_From_Sandeep, 2026-08-27/28). First used as an EMPIRICAL PROBE -- seeded with a
    /// deliberate mix of valid/invalid/blank/whitespace-padded strings and actually EXECUTED
    /// via dtexec against a real SSIS run (not guessed from Microsoft's own transformation
    /// docs) to observe IgnoreFailure's real effect before any extractor/codegen code was
    /// written against it -- then reused unchanged as the Gate-3 fixture once
    /// DataConvertPayload/codegen existed. See CLAUDE.md's "Data Conversion component" section
    /// for the exact observed results.</para>
    ///
    /// <para><b>Object-model shape, confirmed empirically before writing any reader/codegen
    /// code</b> (same "ask the runtime, don't guess" discipline as trap 12) -- two real
    /// findings neither obvious from Microsoft's own Data Conversion Transformation SDK sample
    /// (which shows a DIFFERENT, apparently outdated API surface than this GAC's actual
    /// <c>IDTSDesigntimeComponent100</c>):
    /// <list type="bullet">
    /// <item><c>MapOutputColumn</c> is NOT how a converted output column is created -- its real
    /// signature (<c>MapOutputColumn(outputId, outputColumnId, externalMetadataColumnId,
    /// bMatch)</c>, confirmed via reflection) is the OUTPUT-side mirror of
    /// <c>MapInputColumn</c>, for wiring an EXISTING output column to an external metadata
    /// column -- unrelated to creating a new converted column at all. The real mechanism is the
    /// same <c>InsertOutputColumnAt</c> every other bespoke Derived-Column-shaped fixture in
    /// this file already uses, plus <c>SetOutputColumnProperty(output.ID, newCol.ID,
    /// "SourceInputColumnLineageID", vcol.LineageID)</c> to point the new column back at its
    /// source -- matching the real evidenced XML's own <c>containsID="true"</c> custom
    /// property exactly.</item>
    /// <item><c>IDTSOutputColumn100.ErrorRowDisposition</c>/<c>.TruncationRowDisposition</c>
    /// are ordinary settable properties (<c>DTSRowDisposition</c> enum: NotUsed=0,
    /// IgnoreFailure=1, RedirectRow=2, FailComponent=4 -- confirmed via reflection, not
    /// guessed), not custom properties -- set directly on the column returned by
    /// <c>InsertOutputColumnAt</c>, unlike <c>SourceInputColumnLineageID</c>/<c>FastParse</c>
    /// which ARE custom properties.</item>
    /// </list>
    /// The SOURCE string column being converted is marked <c>UT_READONLY</c> (its value is
    /// read, not modified -- Data Conversion produces a NEW output column, exactly like Derived
    /// Column's own convention), and -- matching the real DCONV_Types evidence exactly -- the
    /// untouched <c>ID</c> passthrough column is NEVER explicitly marked at the Data Conversion
    /// component itself (its own <c>&lt;inputColumns&gt;</c> lists only the two converted
    /// source columns, not every upstream column); it still reaches the destination correctly,
    /// confirmed by the destination's own <c>ResolveOleDbMetadata</c> resolving it via the
    /// pipeline buffer's own lineage propagation, not by any explicit action here.</para>
    ///
    /// Requires the backing table from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-data-conversion-tables.sql</c> already
    /// created and seeded on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>. Lives under
    /// tests/Ssis.Extract.Tests/Fixtures/ (test-tier, not SSDT-registered), same as
    /// SyntheticDateDiff.dtsx.
    /// </summary>
    private static int BuildDataConversionFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticDataConversion" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_DataConversionDemo";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, CustomerIdText, SignupDateText FROM dbo.SyntheticDataConversionInput");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        var dconvMeta = BuildDataConversion(pipe, srcOutput);

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticDataConversionTarget]");
        AttachPath(pipe, dconvMeta.OutputCollection[0], destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    private static IDTSComponentMetaData100 BuildDataConversion(MainPipe pipe, IDTSOutput100 upstreamOutput)
    {
        var meta = pipe.ComponentMetaDataCollection.New();
        meta.ComponentClassID = "Microsoft.DataConvert";
        var inst = meta.Instantiate();
        inst.ProvideComponentProperties(); // resets Name -- must set Name after this, see AddOleDbComponent's comment
        meta.Name = "DCONV_Types";

        AttachPath(pipe, upstreamOutput, meta.InputCollection[0]);
        inst.AcquireConnections(null);
        inst.ReinitializeMetaData();
        inst.ReleaseConnections();

        var input = meta.InputCollection[0];
        var virtualInput = input.GetVirtualInput();
        var output = meta.OutputCollection[0];

        AddConvertedColumn(meta, inst, input, virtualInput, output, 0, "CustomerIdText", "CustomerId_i4", DataType.DT_I4, 0, 0, 0, 0);
        AddConvertedColumn(meta, inst, input, virtualInput, output, 1, "SignupDateText", "SignupDate_dt", DataType.DT_DBDATE, 0, 0, 0, 0);

        return meta;
    }

    /// <summary>
    /// Marks the SOURCE string column UT_READONLY (read, not modified) and adds ONE new
    /// converted output column via InsertOutputColumnAt, pointed back at its source via the
    /// SourceInputColumnLineageID custom property -- see BuildDataConversionFixture's own doc
    /// comment for why this is the real mechanism (MapOutputColumn is not). Both dispositions
    /// set to IgnoreFailure explicitly -- matches the real evidenced DCONV_Types shape, and is
    /// this fixture's whole point: proving what IgnoreFailure actually does.
    /// </summary>
    private static void AddConvertedColumn(
        IDTSComponentMetaData100 meta, IDTSDesigntimeComponent100 inst,
        IDTSInput100 input, IDTSVirtualInput100 virtualInput, IDTSOutput100 output, int insertIndex,
        string sourceColumnName, string outputColumnName, DataType targetType,
        int length, int precision, int scale, int codePage)
    {
        var vcol = virtualInput.VirtualInputColumnCollection.Cast<IDTSVirtualInputColumn100>()
            .First(v => v.Name == sourceColumnName);
        inst.SetUsageType(input.ID, virtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);

        var newCol = inst.InsertOutputColumnAt(output.ID, insertIndex, outputColumnName, "");
        inst.SetOutputColumnDataTypeProperties(output.ID, newCol.ID, targetType, length, precision, scale, codePage);
        inst.SetOutputColumnProperty(output.ID, newCol.ID, "SourceInputColumnLineageID", vcol.LineageID);
        newCol.ErrorRowDisposition = DTSRowDisposition.RD_IgnoreFailure;
        newCol.TruncationRowDisposition = DTSRowDisposition.RD_IgnoreFailure;
    }

    /// <summary>
    /// OLE DB Source (SqlCommand: ID, CustomerIdText, SignupDateText) -> Data Conversion
    /// (CustomerIdText -&gt; CustomerId_i4 DT_I4; SignupDateText -&gt; SignupDate_dt DT_DBDATE, both
    /// dispositions IgnoreFailure) -> Conditional Split (case <c>!ISNULL(CustomerId_i4)</c> ->
    /// Valid; default -> Invalid) -> each branch through its OWN Derived Column (<c>TenureDays
    /// &lt;- ISNULL(SignupDate_dt) ? -1 : DATEDIFF("dd",SignupDate_dt,GETDATE())</c>) -> Union
    /// All -> ONE shared OLE DB Destination.
    ///
    /// <para>Proves the two Data-Conversion cross-reference paths RBC_Demo_ETL's own
    /// Package_Transforms.dtsx (DFT_DerivedAndSplit) actually needs, neither of which
    /// <see cref="BuildDataConversionFixture"/> alone exercises (that fixture's converted
    /// columns flow straight to a destination, never referenced by anything else):
    /// <list type="bullet">
    /// <item><b>RouterEmitter</b> resolving a Conditional Split condition that references a Data
    /// Conversion output column (<c>CustomerId_i4</c>) -- the real package's own CSPLIT_Validity
    /// does exactly this (<c>!ISNULL(CustomerID_i4) &amp;&amp; FINDSTRING(...)</c>), simplified
    /// here to just the ISNULL half to isolate the cross-reference itself from FINDSTRING/TRIM
    /// (already proven separately).</item>
    /// <item><b>TransformEmitter</b> resolving a (per-branch, post-split) Derived Column
    /// expression that references a Data Conversion output column (<c>SignupDate_dt</c>) -- the
    /// real package's own DER_Enrich.TenureDays does exactly this.</item>
    /// </list>
    /// Added 2026-08-28, the same day full Conditional-Split/cross-reference support was added
    /// to close the real package's own DFT_DerivedAndSplit gap -- confirmed structurally against
    /// the real package first (regenerating it end to end, reading the emitted
    /// CSPLIT_ValidityRouter.cs/CustomerEnrichedValidTransform.cs directly), then proven with
    /// this dedicated, MINIMAL fixture (real package's own condition also needs FINDSTRING/TRIM,
    /// which would mask whether the cross-reference itself works if reused verbatim here).</para>
    ///
    /// Requires the backing tables from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-data-conversion-split-tables.sql</c>
    /// already created and seeded on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>. Lives under
    /// tests/Ssis.Extract.Tests/Fixtures/ (test-tier, not SSDT-registered), same as
    /// SyntheticDataConversion.dtsx.
    /// </summary>
    private static int BuildDataConversionSplitFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticDataConversionSplit" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_DataConversionSplitDemo";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, CustomerIdText, SignupDateText FROM dbo.SyntheticDataConversionSplitInput");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        var dconvMeta = BuildDataConversion(pipe, srcOutput);
        var dconvOutput = dconvMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);

        var splitMeta = pipe.ComponentMetaDataCollection.New();
        splitMeta.ComponentClassID = "Microsoft.ConditionalSplit";
        var splitInst = splitMeta.Instantiate();
        splitInst.ProvideComponentProperties(); // resets Name -- must set Name after this, see AddOleDbComponent's comment
        splitMeta.Name = "CSPLIT_Validity";

        AttachPath(pipe, dconvOutput, splitMeta.InputCollection[0]);
        splitInst.AcquireConnections(null);
        splitInst.ReinitializeMetaData();
        splitInst.ReleaseConnections();

        var splitInput = splitMeta.InputCollection[0];
        var splitVirtualInput = splitInput.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in splitVirtualInput.VirtualInputColumnCollection)
        {
            splitInst.SetUsageType(splitInput.ID, splitVirtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }

        var defaultOutput = splitMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        defaultOutput.Name = "Invalid";

        var caseOutput = splitInst.InsertOutput(DTSInsertPlacement.IP_AFTER, 0);
        caseOutput.Name = "Valid";
        // The real package's own CSPLIT_Validity is `!ISNULL(CustomerID_i4) &&
        // FINDSTRING(...)` -- kept to just the ISNULL half here so this fixture isolates the
        // Data-Conversion cross-reference itself, not FINDSTRING/TRIM (already proven
        // separately by SyntheticFindStringTrim.dtsx).
        var conditionText = "!ISNULL(CustomerId_i4)";
        splitInst.SetOutputProperty(caseOutput.ID, "FriendlyExpression", conditionText);
        splitInst.SetOutputProperty(caseOutput.ID, "Expression", conditionText);
        splitInst.SetOutputProperty(caseOutput.ID, "EvaluationOrder", 0);

        var validTenureMeta = BuildDerivedColumnTenureDaysFromConversion(pipe, caseOutput, "DER_ValidTenure");
        var invalidTenureMeta = BuildDerivedColumnTenureDaysFromConversion(pipe, defaultOutput, "DER_InvalidTenure");

        var unionMeta = pipe.ComponentMetaDataCollection.New();
        unionMeta.ComponentClassID = "Microsoft.UnionAll";
        var unionInst = unionMeta.Instantiate();
        unionInst.ProvideComponentProperties(); // resets Name -- must set Name after this, see AddOleDbComponent's comment
        unionMeta.Name = "UNION_Recombine";

        // Second input created BEFORE any path is attached -- see
        // BuildConditionalSplitRemergeFixture's own doc comment for why (InsertInput throws
        // 0xC020800E once the first input already has a live upstream connection).
        var unionSecondInputId = unionMeta.InputCollection.New().ID;

        AttachPath(pipe, validTenureMeta.OutputCollection[0], unionMeta.InputCollection[0]);
        AttachPath(pipe, invalidTenureMeta.OutputCollection[0], unionMeta.InputCollection.GetObjectByID(unionSecondInputId));
        unionInst.AcquireConnections(null);
        unionInst.ReinitializeMetaData();
        unionInst.ReleaseConnections();

        var unionFirstInput = unionMeta.InputCollection[0];
        var unionFirstVirtualInput = unionFirstInput.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in unionFirstVirtualInput.VirtualInputColumnCollection)
        {
            unionInst.SetUsageType(unionFirstInput.ID, unionFirstVirtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }

        var unionSecondInput = unionMeta.InputCollection.GetObjectByID(unionSecondInputId);
        var unionSecondVirtualInput = unionSecondInput.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in unionSecondVirtualInput.VirtualInputColumnCollection)
        {
            unionInst.SetUsageType(unionSecondInput.ID, unionSecondVirtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }

        // The second input's columns are NOT auto-mapped by ReinitializeMetaData() -- see
        // BuildConditionalSplitRemergeFixture's own doc comment for why each must be pointed at
        // the matching (by name) output column the first input already created.
        var unionOutput = unionMeta.OutputCollection[0];
        foreach (IDTSInputColumn100 inCol in unionSecondInput.InputColumnCollection)
        {
            var matchingOutputCol = unionOutput.OutputColumnCollection.Cast<IDTSOutputColumn100>()
                .FirstOrDefault(oc => oc.Name == inCol.Name)
                ?? throw new InvalidOperationException($"fixture build error: Union All has no output column named '{inCol.Name}' to map the second input's own column onto.");
            unionInst.SetInputColumnProperty(unionSecondInput.ID, inCol.ID, "OutputColumnLineageID", matchingOutputCol.LineageID);
        }

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticDataConversionSplitTarget]");
        AttachPath(pipe, unionOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// A Derived Column that passes every upstream column through untouched and adds ONE new
    /// column, <c>TenureDays &lt;- ISNULL(SignupDate_dt) ? -1 : DATEDIFF("dd",SignupDate_dt,
    /// GETDATE())</c> -- referencing a Data Conversion column (SignupDate_dt) rather than a
    /// plain source column, the exact shape RBC_Demo_ETL's own DER_Enrich.TenureDays uses. Named
    /// per call site (<paramref name="componentName"/>) since this fixture needs two instances,
    /// one per Conditional Split branch, matching BuildDerivedColumnLiteralTag's own per-branch
    /// naming convention.
    /// </summary>
    private static IDTSComponentMetaData100 BuildDerivedColumnTenureDaysFromConversion(MainPipe pipe, IDTSOutput100 upstreamOutput, string componentName)
    {
        var meta = pipe.ComponentMetaDataCollection.New();
        meta.ComponentClassID = "Microsoft.DerivedColumn";
        var inst = meta.Instantiate();
        inst.ProvideComponentProperties(); // resets Name -- must set Name after this, see AddOleDbComponent's comment
        meta.Name = componentName;

        AttachPath(pipe, upstreamOutput, meta.InputCollection[0]);
        inst.AcquireConnections(null);
        inst.ReinitializeMetaData();
        inst.ReleaseConnections();

        var input = meta.InputCollection[0];
        var virtualInput = input.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in virtualInput.VirtualInputColumnCollection)
        {
            inst.SetUsageType(input.ID, virtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }

        var output = meta.OutputCollection[0];
        var newCol = inst.InsertOutputColumnAt(output.ID, 0, "TenureDays", "");
        inst.SetOutputColumnDataTypeProperties(output.ID, newCol.ID, DataType.DT_I4, 0, 0, 0, 0);
        var expr = "ISNULL(SignupDate_dt) ? -1 : DATEDIFF(\"dd\",SignupDate_dt,GETDATE())";
        inst.SetOutputColumnProperty(output.ID, newCol.ID, "FriendlyExpression", expr);
        inst.SetOutputColumnProperty(output.ID, newCol.ID, "Expression", expr);

        return meta;
    }

    /// <summary>
    /// OLE DB Source (SqlCommand) -> Derived Column (TrimmedEmail &lt;- TRIM(Email), shared,
    /// upstream of the split -- proves TRIM as a VALUE-producing expression, not just inside a
    /// condition) -> Conditional Split (one case, "FINDSTRING(TRIM(Email),\"@\",1) &gt; 0" ->
    /// Valid; default -> Invalid) -> 2 OLE DB Destinations.
    ///
    /// <para>Built to prove ExpressionTranslator's FINDSTRING/TRIM support (added 2026-08-27)
    /// end to end, in the exact nested shape discovered testing ssisx against a real
    /// ~30-component-type third-party portfolio (SSIS_From_Sandeep, 2026-08-27):
    /// RBC_Demo_ETL's own Package_Transforms.dtsx, CSPLIT_Validity's "Valid" case, is literally
    /// <c>!ISNULL(CustomerID_i4) &amp;&amp; FINDSTRING(TRIM(Email),"@",1) &gt; 0</c> -- this
    /// fixture keeps the FINDSTRING(TRIM(...)) nesting but drops the ISNULL/CustomerID_i4 half
    /// (already proven working last session) and, more importantly, the Data Conversion
    /// component that produces CustomerID_i4/SignupDate_dt in the real package. Regenerating
    /// the real package after adding FINDSTRING/TRIM surfaced a SEPARATE, pre-existing,
    /// previously-invisible gap: a Data Conversion component's renamed/retyped output columns
    /// (e.g. CustomerID_i4) are referenced by downstream expressions exactly like any other
    /// pipeline column, but the generated CSV row type is built only from the Flat File source's
    /// own raw declared columns, so referencing a Data Conversion output fails to compile
    /// (CS1061, "does not contain a definition for 'CustomerID_i4'"). That's a new, unmodeled
    /// component type (Microsoft.DataConvert), unrelated to expression translation -- CLAUDE.md
    /// documents it as a separate, deliberately out-of-scope gap for a future round, and this
    /// fixture is built without it specifically so it proves FINDSTRING/TRIM in isolation,
    /// unblocked by that unrelated gap.</para>
    ///
    /// Requires the backing tables from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-findstring-trim-tables.sql</c> already
    /// created and seeded on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>. Lives under
    /// tests/Ssis.Extract.Tests/Fixtures/ (test-tier, not SSDT-registered), same as
    /// SyntheticConditionalSplit.dtsx.
    /// </summary>
    private static int BuildFindStringTrimFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticFindStringTrim" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_FindStringTrimDemo";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Email FROM dbo.SyntheticFindStringTrimInput");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        var derivedMeta = BuildDerivedColumnTrimmedEmail(pipe, srcOutput);
        var derivedOutput = derivedMeta.OutputCollection[0];

        var splitMeta = pipe.ComponentMetaDataCollection.New();
        splitMeta.ComponentClassID = "Microsoft.ConditionalSplit";
        var splitInst = splitMeta.Instantiate();
        splitInst.ProvideComponentProperties(); // resets Name to the class default -- must set Name after this, see AddOleDbComponent's comment
        splitMeta.Name = "CSPLIT_Validity";

        AttachPath(pipe, derivedOutput, splitMeta.InputCollection[0]);
        splitInst.AcquireConnections(null);
        splitInst.ReinitializeMetaData();
        splitInst.ReleaseConnections();

        var splitInput = splitMeta.InputCollection[0];
        var splitVirtualInput = splitInput.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in splitVirtualInput.VirtualInputColumnCollection)
        {
            splitInst.SetUsageType(splitInput.ID, splitVirtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }

        var defaultOutput = splitMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        defaultOutput.Name = "Invalid";

        var caseOutput = splitInst.InsertOutput(DTSInsertPlacement.IP_AFTER, 0);
        caseOutput.Name = "Valid";
        // Same nested FINDSTRING(TRIM(...)) shape as RBC_Demo_ETL's own CSPLIT_Validity,
        // verbatim minus the ISNULL/CustomerID_i4 half (a separate, already-proven feature).
        var conditionText = "FINDSTRING(TRIM(Email),\"@\",1) > 0";
        splitInst.SetOutputProperty(caseOutput.ID, "FriendlyExpression", conditionText);
        splitInst.SetOutputProperty(caseOutput.ID, "Expression", conditionText);
        splitInst.SetOutputProperty(caseOutput.ID, "EvaluationOrder", 0);

        var validDestMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticFindStringTrimValid]");
        AttachPath(pipe, caseOutput, validDestMeta.InputCollection[0]);
        ResolveOleDbMetadata(validDestMeta, isDestination: true);

        var invalidDestMeta = AddOleDbComponent(pipe, "OLE DB Destination 1", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticFindStringTrimInvalid]");
        AttachPath(pipe, defaultOutput, invalidDestMeta.InputCollection[0]);
        ResolveOleDbMetadata(invalidDestMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// A Derived Column that passes every upstream column through untouched and adds ONE new
    /// string column, <c>TrimmedEmail &lt;- TRIM(Email)</c> -- proves TRIM as a VALUE-producing
    /// expression (as opposed to inside a Conditional Split condition, which
    /// <see cref="BuildFindStringTrimFixture"/>'s case expression separately proves). Same
    /// passthrough/InsertOutputColumnAt mechanics as <see cref="BuildDerivedColumnLiteralTag"/>,
    /// just a function-call expression instead of a literal.
    /// </summary>
    private static IDTSComponentMetaData100 BuildDerivedColumnTrimmedEmail(MainPipe pipe, IDTSOutput100 upstreamOutput)
    {
        var meta = pipe.ComponentMetaDataCollection.New();
        meta.ComponentClassID = "Microsoft.DerivedColumn";
        var inst = meta.Instantiate();
        inst.ProvideComponentProperties(); // resets Name -- must set Name after this, see AddOleDbComponent's comment
        meta.Name = "DER_TrimEmail";

        AttachPath(pipe, upstreamOutput, meta.InputCollection[0]);
        inst.AcquireConnections(null);
        inst.ReinitializeMetaData();
        inst.ReleaseConnections();

        var input = meta.InputCollection[0];
        var virtualInput = input.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in virtualInput.VirtualInputColumnCollection)
        {
            inst.SetUsageType(input.ID, virtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }

        var output = meta.OutputCollection[0];
        var newCol = inst.InsertOutputColumnAt(output.ID, 0, "TrimmedEmail", "");
        // DT_WSTR is Unicode -- codepage is 0, not 1252 (that's DT_STR's own non-Unicode
        // convention). Passing 1252 here threw 0xC0204025, confirmed empirically (see
        // BuildDerivedColumnLiteralTag's own identical note).
        inst.SetOutputColumnDataTypeProperties(output.ID, newCol.ID, DataType.DT_WSTR, 200, 0, 0, 0);
        var expr = "TRIM(Email)";
        inst.SetOutputColumnProperty(output.ID, newCol.ID, "FriendlyExpression", expr);
        inst.SetOutputColumnProperty(output.ID, newCol.ID, "Expression", expr);

        return meta;
    }

    /// <summary>
    /// OLE DB Source (SqlCommand) -> Derived Column (LoadedAtUtc &lt;- GETUTCDATE(), shared,
    /// upstream of the split) -> Conditional Split (one case, "Amount &gt; 1000" -> High;
    /// default -> Low) -> each branch through its OWN Derived Column (Segment &lt;- "High" /
    /// "Low", a literal) -> Union All -> ONE shared OLE DB Destination.
    ///
    /// <para>Built to prove ssisx generate's Conditional Split support for the "two branches
    /// remerge to one shared destination" shape -- distinct from
    /// <see cref="BuildConditionalSplitFixture"/>, whose two branches each keep their own
    /// destination. Real-world motivation: RBC_Demo_ETL's Package_Transforms.dtsx
    /// (DFT_DerivedAndSplit) has exactly this shape (CSPLIT_Validity -> DER_TagValid/
    /// DER_TagInvalid -> UNION_Recombine -> OLEDST_CustomerEnriched), discovered testing
    /// ssisx against a real ~30-component-type client-shaped portfolio (SSIS_From_Sandeep,
    /// 2026-08-27). That package's own case condition additionally needs FINDSTRING/TRIM,
    /// which ExpressionTranslator does not support yet -- a separate, unrelated gap; this
    /// fixture's condition ("Amount &gt; 1000") and per-branch tags (literals) are kept
    /// deliberately simple so it proves the STRUCTURAL remerge support in isolation.</para>
    ///
    /// <para><b>Microsoft.UnionAll's object-model shape, confirmed empirically before/while
    /// writing this</b> (same "ask the runtime, don't guess" discipline as trap 12), not
    /// assumed from RBC_Demo_ETL's SESSION-HANDOFF.md prose alone (which only names the
    /// symptom -- "Union All auto-maps its first input only; later inputs need
    /// OutputColumnLineageID" -- not the exact API calls):
    /// <list type="bullet">
    /// <item>A second input is added via the raw collection method
    /// <c>unionMeta.InputCollection.New()</c> -- NOT the component-level
    /// <c>IDTSDesigntimeComponent100.InsertInput(...)</c> Conditional Split's own
    /// <c>InsertOutput</c> made a reasonable pattern to try first: Union All's own
    /// <c>InsertInput</c> throws <c>COMException 0xC020800E</c> unconditionally, called
    /// before or after the first input has a live connection -- confirmed both orderings
    /// fail identically, so this is a real difference between how these two components
    /// manage a variable-count collection, not a call-order mistake.</item>
    /// <item>Both paths are attached before the first <c>ReinitializeMetaData()</c> call, which
    /// then resolves both inputs in one pass -- auto-mapping the FIRST input's columns to
    /// newly created, identically-named output columns (the same auto-map-by-name behavior a
    /// Merge/Merge Join's first input gets), but NOT the second's (confirmed:
    /// <c>OutputColumnLineageID</c> reads back unset on the second input's own columns) -- each
    /// of those must be mapped explicitly, matching output columns BY NAME, via
    /// <c>SetInputColumnProperty(inputId, colId, "OutputColumnLineageID", outputColumn.LineageID)</c>.</item>
    /// </list>
    /// Requires the backing tables from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-conditional-split-remerge-tables.sql</c>
    /// already created and seeded on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>. Lives under
    /// tests/Ssis.Extract.Tests/Fixtures/ (test-tier, not SSDT-registered), same as
    /// SyntheticConditionalSplit.dtsx.</para>
    ///
    /// <para><b>Known limitation, stated rather than hidden: this fixture is NOT confirmed
    /// dtexec-executable.</b> A real run fails SSIS's own pipeline validation with
    /// <c>"CSPLIT_Amount.Inputs[Conditional Split Input].Columns[...] has lineage ID N that
    /// was not previously used in the Data Flow task"</c> -- a metadata inconsistency on the
    /// SPLIT's own input, introduced somewhere by attaching a Derived Column directly
    /// downstream of one of its case/default outputs (something no other fixture in this file
    /// does; every other Conditional Split output goes straight to a destination). Tried and
    /// ruled out as the cause: re-running the split's own <c>ReinitializeMetaData()</c> after
    /// adding the case output, and resolving the Union All's two inputs in one pass vs.
    /// incrementally -- neither changed the outcome, so this is left unresolved rather than
    /// papered over with an unverified guess. This does NOT block using the fixture: `ssisx
    /// extract`/`generate` parse the saved XML directly (plan §2.1) and never invoke SSIS's own
    /// object model or its execution-time validation, so they read this fixture correctly
    /// regardless. The feature's real-world runtime correctness is independently confirmed via
    /// RBC_Demo_ETL's own Package_Transforms.dtsx instead (SSDT-designer-built, so guaranteed
    /// valid and executable) -- see CLAUDE.md's "Conditional Split -- multi-hop Union All
    /// remerge" section. Same acceptable tier as this file's own
    /// <see cref="BuildPassthroughScriptComponent"/>, which states outright it is "never
    /// compiled/executed... structural evidence for the extractor, not a runnable pipeline."</para>
    /// </summary>
    private static int BuildConditionalSplitRemergeFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticConditionalSplitRemerge" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_ConditionalSplitRemergeDemo";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Amount FROM dbo.SyntheticRemergeInput");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        var sharedDerivedMeta = BuildDerivedColumnLoadedAtUtc(pipe, srcOutput);
        var sharedDerivedOutput = sharedDerivedMeta.OutputCollection[0];

        var splitMeta = pipe.ComponentMetaDataCollection.New();
        splitMeta.ComponentClassID = "Microsoft.ConditionalSplit";
        var splitInst = splitMeta.Instantiate();
        splitInst.ProvideComponentProperties(); // resets Name -- must set Name after this, see AddOleDbComponent's comment
        splitMeta.Name = "CSPLIT_Amount";

        AttachPath(pipe, sharedDerivedOutput, splitMeta.InputCollection[0]);
        splitInst.AcquireConnections(null);
        splitInst.ReinitializeMetaData();
        splitInst.ReleaseConnections();

        var splitInput = splitMeta.InputCollection[0];
        var splitVirtualInput = splitInput.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in splitVirtualInput.VirtualInputColumnCollection)
        {
            splitInst.SetUsageType(splitInput.ID, splitVirtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }

        var defaultOutput = splitMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        defaultOutput.Name = "Low";

        var caseOutput = splitInst.InsertOutput(DTSInsertPlacement.IP_AFTER, 0);
        caseOutput.Name = "High";
        splitInst.SetOutputProperty(caseOutput.ID, "FriendlyExpression", "Amount > 1000");
        splitInst.SetOutputProperty(caseOutput.ID, "Expression", "Amount > 1000");
        splitInst.SetOutputProperty(caseOutput.ID, "EvaluationOrder", 0);

        var highTagMeta = BuildDerivedColumnLiteralTag(pipe, caseOutput, "Segment", "High");
        var lowTagMeta = BuildDerivedColumnLiteralTag(pipe, defaultOutput, "Segment", "Low");

        var unionMeta = pipe.ComponentMetaDataCollection.New();
        unionMeta.ComponentClassID = "Microsoft.UnionAll";
        var unionInst = unionMeta.Instantiate();
        unionInst.ProvideComponentProperties(); // resets Name -- must set Name after this, see AddOleDbComponent's comment
        unionMeta.Name = "UNION_Recombine";

        // Second input created BEFORE any path is attached/ReinitializeMetaData is called --
        // confirmed empirically: calling InsertInput AFTER the first input already had a live
        // upstream connection threw 0xC020800E ("component does not support inserting new
        // inputs" -- Union All apparently only accepts this while still in its just-
        // constructed state). Both paths are then attached, and metadata resolved for BOTH in
        // a single ReinitializeMetaData() pass -- resolving incrementally (attach path 1,
        // resolve, attach path 2, resolve again) left the split's OWN unrelated input with a
        // stale lineage ID at dtexec validation time ("has lineage ID N that was not previously
        // used"), confirmed by comparing both orderings against a real run.
        var unionSecondInputId = unionMeta.InputCollection.New().ID;

        AttachPath(pipe, highTagMeta.OutputCollection[0], unionMeta.InputCollection[0]);
        AttachPath(pipe, lowTagMeta.OutputCollection[0], unionMeta.InputCollection.GetObjectByID(unionSecondInputId));
        unionInst.AcquireConnections(null);
        unionInst.ReinitializeMetaData();
        unionInst.ReleaseConnections();

        // Re-fetched by ID, not by reusing the pre-RIMD references -- COM RCWs from this
        // object model are not guaranteed stable across a ReinitializeMetaData() call (same
        // lesson CLAUDE.md's object-model oracle notes learned comparing .Parent references by
        // ID, not by ReferenceEquals).
        var unionFirstInput = unionMeta.InputCollection[0];
        var unionFirstVirtualInput = unionFirstInput.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in unionFirstVirtualInput.VirtualInputColumnCollection)
        {
            unionInst.SetUsageType(unionFirstInput.ID, unionFirstVirtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }

        var unionSecondInput = unionMeta.InputCollection.GetObjectByID(unionSecondInputId);
        var unionSecondVirtualInput = unionSecondInput.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in unionSecondVirtualInput.VirtualInputColumnCollection)
        {
            unionInst.SetUsageType(unionSecondInput.ID, unionSecondVirtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }

        // The second input's columns are NOT auto-mapped by ReinitializeMetaData() -- each
        // must be pointed at the matching (by name) output column the first input already
        // created, via OutputColumnLineageID. Confirmed empirically: without this, values
        // from the second (Low) branch arrive NULL at the destination -- exactly the failure
        // mode RBC_Demo_ETL's own SESSION-HANDOFF.md names.
        var unionOutput = unionMeta.OutputCollection[0];
        foreach (IDTSInputColumn100 inCol in unionSecondInput.InputColumnCollection)
        {
            var matchingOutputCol = unionOutput.OutputColumnCollection.Cast<IDTSOutputColumn100>()
                .FirstOrDefault(oc => oc.Name == inCol.Name)
                ?? throw new InvalidOperationException($"fixture build error: Union All has no output column named '{inCol.Name}' to map the second input's own column onto.");
            unionInst.SetInputColumnProperty(unionSecondInput.ID, inCol.ID, "OutputColumnLineageID", matchingOutputCol.LineageID);
        }

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticRemergeTarget]");
        AttachPath(pipe, unionOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// A Derived Column that passes every upstream column through untouched and adds ONE new
    /// string column set to a fixed literal, e.g. <c>Segment &lt;- "High"</c> -- the same shape
    /// RBC_Demo_ETL's own DER_TagValid/DER_TagInvalid use to tag a Conditional Split branch
    /// before a Union All remerges it. Structurally identical to
    /// <see cref="BuildDerivedColumnLoadedAtUtc"/> (same passthrough/InsertOutputColumnAt
    /// mechanics), just a string literal expression instead of GETUTCDATE().
    /// </summary>
    private static IDTSComponentMetaData100 BuildDerivedColumnLiteralTag(
        MainPipe pipe, IDTSOutput100 upstreamOutput, string columnName, string literalValue)
    {
        var meta = pipe.ComponentMetaDataCollection.New();
        meta.ComponentClassID = "Microsoft.DerivedColumn";
        var inst = meta.Instantiate();
        inst.ProvideComponentProperties(); // resets Name -- must set Name after this, see AddOleDbComponent's comment
        meta.Name = $"DER_Tag{literalValue}";

        AttachPath(pipe, upstreamOutput, meta.InputCollection[0]);
        inst.AcquireConnections(null);
        inst.ReinitializeMetaData();
        inst.ReleaseConnections();

        var input = meta.InputCollection[0];
        var virtualInput = input.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in virtualInput.VirtualInputColumnCollection)
        {
            inst.SetUsageType(input.ID, virtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }

        var output = meta.OutputCollection[0];
        var newCol = inst.InsertOutputColumnAt(output.ID, 0, columnName, "");
        // DT_WSTR is Unicode -- codepage is 0, not 1252 (that's DT_STR's own non-Unicode
        // convention). Passing 1252 here threw 0xC0204025 ("invalid data type properties"),
        // confirmed empirically.
        inst.SetOutputColumnDataTypeProperties(output.ID, newCol.ID, DataType.DT_WSTR, 10, 0, 0, 0);
        var literalExpr = $"\"{literalValue}\"";
        inst.SetOutputColumnProperty(output.ID, newCol.ID, "FriendlyExpression", literalExpr);
        inst.SetOutputColumnProperty(output.ID, newCol.ID, "Expression", literalExpr);

        return meta;
    }

    /// <summary>
    /// SQL_PreLoad (TRUNCATE) -> FST_PreLoadCopy (a pre-load File System Task, Copy) ->
    /// DFT_Load (Flat File Source -> Derived Column -> OLE DB Destination) -> FST_PostLoadCopy
    /// (a post-flow File System Task, Copy). Proves PackagePlanner's two File System Task
    /// positions -- pre-load (RBC_Demo_ETL's own FST_ArchiveWorkbook shape: SQL_TruncateTargets
    /// always precedes it, inside the same Sequence Container) and post-flow (not evidenced on
    /// any real package yet, but Etl.Core.Pipeline.FileSystemStep supports it symmetrically with
    /// ExecuteSqlStep's own pre-load/post-flow split, so it's proven here rather than left
    /// theoretical). Both File System Tasks reference FILE connection managers by name, matching
    /// FST_ArchiveWorkbook's real shape -- not a literal/variable path, the two OTHER supported
    /// shapes this tool never got real evidence for.
    /// </summary>
    private static int BuildFileSystemTaskFixture(string outputPath)
    {
        var filesDir = Path.Combine(TestFixturesDir(), "synthetic-file-system-task-files");
        var sourcePath = Path.Combine(filesDir, "source.txt");
        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine($"error: expected source file at {sourcePath} -- this tool's checked-in test fixtures are missing or moved.");
            return 2;
        }

        var csvPath = Path.Combine(TestFixturesDir(), "synthetic-file-system-task-csv", "SyntheticFileSystemTaskLoad.csv");
        if (!File.Exists(csvPath))
        {
            Console.Error.WriteLine($"error: expected CSV fixture at {csvPath} -- this tool's checked-in test fixtures are missing or moved.");
            return 2;
        }

        var pkg = new RtPackage { Name = "SyntheticFileSystemTask" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var sourceFileCm = pkg.Connections.Add("FILE");
        sourceFileCm.Name = "CM_FILE_Source";
        sourceFileCm.ConnectionString = sourcePath;
        // No FileUsageType set -- matches FST_ArchiveWorkbook's real SOURCE connection manager
        // (CM_FILE_SourceWorkbook), which carries none either; the default (0, ExistingFile) is
        // correct for a copy source that must already exist.

        var preLoadDestCm = pkg.Connections.Add("FILE");
        preLoadDestCm.Name = "CM_FILE_PreLoadArchive";
        preLoadDestCm.ConnectionString = Path.Combine(filesDir, "archived-preload.txt");
        // Set via .Properties[...], not a strongly-typed interface -- there is no
        // IDTSConnectionManagerFile100 (confirmed by reflecting the assembly for any type
        // matching *ConnectionManagerFile*; none exists). CLAUDE.md's own trap notes
        // FileUsageType "wants the int enum, not its name" -- passing the cast int value here,
        // not the bare enum symbol, matches that lesson.
        preLoadDestCm.Properties["FileUsageType"].SetValue(preLoadDestCm, (int)Microsoft.SqlServer.Dts.Runtime.DTSFileConnectionUsageType.CreateFile);

        var postFlowDestCm = pkg.Connections.Add("FILE");
        postFlowDestCm.Name = "CM_FILE_PostFlowArchive";
        postFlowDestCm.ConnectionString = Path.Combine(filesDir, "archived-postflow.txt");
        postFlowDestCm.Properties["FileUsageType"].SetValue(postFlowDestCm, (int)Microsoft.SqlServer.Dts.Runtime.DTSFileConnectionUsageType.CreateFile);

        var preLoadTask = AddExecuteSql(pkg, "SQL_PreLoad", sqlCm, "TRUNCATE TABLE dbo.SyntheticFileSystemTaskTarget;");
        var preLoadCopyTask = AddFileSystemTask(pkg, "FST_PreLoadCopy", sourceFileCm, preLoadDestCm, overwrite: true);

        var csvCm = AddFlatFileConnectionManager(pkg, "CM_FileSystemTaskCsv", csvPath,
            ("ID", "DT_I4", 0, 0, 0),
            ("Name", "DT_WSTR", 50, 0, 0));

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Load";
        BuildFlatFileToOleDbLoad((MainPipe)dftHost.InnerObject, csvCm, sqlCm, "[dbo].[SyntheticFileSystemTaskTarget]");

        var postFlowCopyTask = AddFileSystemTask(pkg, "FST_PostLoadCopy", sourceFileCm, postFlowDestCm, overwrite: true);

        pkg.PrecedenceConstraints.Add(preLoadTask, (Executable)preLoadCopyTask);
        pkg.PrecedenceConstraints.Add((Executable)preLoadCopyTask, dftHost);
        pkg.PrecedenceConstraints.Add(dftHost, (Executable)postFlowCopyTask);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    private static Microsoft.SqlServer.Dts.Runtime.TaskHost AddFileSystemTask(
        RtPackage pkg, string name, ConnectionManager sourceCm, ConnectionManager destCm, bool overwrite)
    {
        var host = (RtTaskHost)pkg.Executables.Add("Microsoft.FileSystemTask");
        host.Name = name;
        var inner = (Microsoft.SqlServer.Dts.Tasks.FileSystemTask.FileSystemTask)host.InnerObject;
        // Operation left at its default (CopyFile) -- confirmed via a live probe that this is
        // genuinely omitted from the saved XML rather than written as "CopyFile" (see
        // FileSystemTaskPayload's own doc comment), so a fixture wanting Copy specifically
        // should not set it at all, matching how the real evidenced package looks.
        inner.Source = sourceCm.Name;
        inner.Destination = destCm.Name;
        inner.OverwriteDestinationFile = overwrite;
        return host;
    }

    /// <summary>
    /// Flat File Source -> Derived Column (LoadedAtUtc &lt;- GETUTCDATE(), the same audit-stamp
    /// shape both real PoC packages already use) -> OLE DB Destination. Deliberately NOT a
    /// direct copy (Source -> Destination with no transform) -- that shape hits a separate,
    /// already-documented generator gap ("a direct-copy pipeline's transform is not generated
    /// yet", first surfaced by SyntheticParallelShapes' branch 2) that has nothing to do with
    /// nested containers; using it here would mask whether THIS fixture's nesting support
    /// actually reaches a real, compiling <c>Program.cs</c>.
    /// </summary>
    private static void BuildFlatFileToOleDbLoad(MainPipe pipe, ConnectionManager srcCm, ConnectionManager destCm, string destTable)
    {
        var srcMeta = pipe.ComponentMetaDataCollection.New();
        srcMeta.ComponentClassID = "Microsoft.FlatFileSource";
        var srcInst = srcMeta.Instantiate();
        srcInst.ProvideComponentProperties(); // resets Name to the class default -- must set Name after this, see AddOleDbComponent's comment
        srcMeta.Name = "Flat File Source";
        var srcConn = srcMeta.RuntimeConnectionCollection[0];
        srcConn.ConnectionManagerID = srcCm.ID;
        srcConn.ConnectionManager = DtsConvert.GetExtendedInterface(srcCm);
        srcInst.AcquireConnections(null);
        srcInst.ReinitializeMetaData();
        srcInst.ReleaseConnections();

        var mainOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);

        var derivedMeta = BuildDerivedColumnLoadedAtUtc(pipe, mainOutput);

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", destCm,
            accessMode: 3, openRowset: destTable);
        AttachPath(pipe, derivedMeta.OutputCollection[0], destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);
    }

    /// <summary>
    /// A Derived Column that passes every upstream column through untouched and adds ONE new
    /// column, <c>LoadedAtUtc &lt;- GETUTCDATE()</c> -- the same minimal shape as the real PoC
    /// packages' own audit-stamp column (CLAUDE.md's non-determinism-manifest note). Marking
    /// every virtual input column UT_READONLY is what makes it flow through to the output by
    /// lineageId -- same mechanism <see cref="BuildPassthroughScriptComponent"/> already uses
    /// for the Script Component fixture, and the same "passthrough via lineageId, not a
    /// re-declared output column" rule <c>LineageBuilder</c> documents from reading the real
    /// LoadEmployees.dtsx.
    /// </summary>
    private static IDTSComponentMetaData100 BuildDerivedColumnLoadedAtUtc(MainPipe pipe, IDTSOutput100 upstreamOutput)
    {
        var meta = pipe.ComponentMetaDataCollection.New();
        meta.ComponentClassID = "Microsoft.DerivedColumn";
        var inst = meta.Instantiate();
        inst.ProvideComponentProperties(); // resets Name to the class default -- must set Name after this, see AddOleDbComponent's comment
        meta.Name = "DER_LoadedAtUtc";

        AttachPath(pipe, upstreamOutput, meta.InputCollection[0]);
        inst.AcquireConnections(null);
        inst.ReinitializeMetaData();
        inst.ReleaseConnections();

        var input = meta.InputCollection[0];
        var virtualInput = input.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in virtualInput.VirtualInputColumnCollection)
        {
            inst.SetUsageType(input.ID, virtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }

        // A plain output.OutputColumnCollection.New() column has no Expression/
        // FriendlyExpression custom properties attached (those are Derived Column's own
        // per-column schema, seeded only via the designtime component's own column-insert
        // API) -- confirmed the hard way: SetOutputColumnProperty on a raw New() column threw
        // COMException 0xC0204006. InsertOutputColumnAt is the correct call.
        var output = meta.OutputCollection[0];
        var newCol = inst.InsertOutputColumnAt(output.ID, 0, "LoadedAtUtc", "");
        inst.SetOutputColumnDataTypeProperties(output.ID, newCol.ID, DataType.DT_DBTIMESTAMP, 0, 0, 0, 0);
        inst.SetOutputColumnProperty(output.ID, newCol.ID, "FriendlyExpression", "GETUTCDATE()");
        inst.SetOutputColumnProperty(output.ID, newCol.ID, "Expression", "GETUTCDATE()");

        return meta;
    }

    private static ConnectionManager AddFlatFileConnectionManager(
        RtPackage pkg, string name, string csvPath,
        params (string ColumnName, string DtType, int MaxWidth, int Precision, int Scale)[] columns)
    {
        var cm = pkg.Connections.Add("FLATFILE");
        cm.Name = name;
        cm.ConnectionString = csvPath;

        var ff = (IDTSConnectionManagerFlatFile100)cm.InnerObject;
        ff.Format = "Delimited";
        ff.ColumnNamesInFirstDataRow = true;
        ff.HeaderRowDelimiter = "\r\n";
        ff.CodePage = 1252;

        for (var i = 0; i < columns.Length; i++)
        {
            var (columnName, dtType, maxWidth, precision, scale) = columns[i];
            var col = ff.Columns.Add();
            col.ColumnType = "Delimited";
            // Last column is row-delimited, not comma-delimited -- matches how a real CSV
            // connection manager configures its own final column (verified against
            // LoadReferenceData.dtsx's own CM_DepartmentCsv, same convention).
            col.ColumnDelimiter = i == columns.Length - 1 ? "\r\n" : ",";
            col.DataType = (RtDataType)Enum.Parse(typeof(RtDataType), dtType);
            col.MaximumWidth = maxWidth;
            col.DataPrecision = precision;
            col.DataScale = scale;
            // The column has NO Name property on its own COM interface -- naming requires
            // casting to IDTSName100 instead. Found empirically (reflection over the static
            // interface showed nothing; the real name only surfaced after casting a LIVE
            // column from a loaded real package to IDTSName100), not documented anywhere
            // obvious. Skipping this cast leaves every column silently unnamed.
            ((IDTSName100)col).Name = columnName;
        }

        return cm;
    }

    /// <summary>
    /// Two OLE DB Sources (Left: ID/Name; Right: ID/Phone) -> Sort each (key: ID, ascending) ->
    /// <c>Microsoft.MergeJoin</c> (JoinType=2 = INNER, the real evidenced raw value from
    /// RBC_Demo_ETL's own MRG_CustomerContacts) -> OLE DB Destination. SQL-sourced (not Flat
    /// File + Data Conversion, unlike the real package) specifically to isolate Sort/MergeJoin's
    /// own mechanics in the smallest possible shape.
    ///
    /// <para><b>This fixture used to be blocked on a "phantom output columns" quirk documented
    /// here as unresolved. It is RESOLVED (gap-audit Phase 1, 2026-09-02) and the fixture now
    /// builds, validates VS_ISVALID, and runs under real dtexec.</b> The old account had TWO
    /// wrong premises, both corrected by dumping the live component state instead of reasoning
    /// about it:
    /// <list type="bullet">
    /// <item>It claimed Merge Join "starts with exactly ONE input from
    /// <c>ProvideComponentProperties()</c>", so it called <c>InsertInput</c> to add a second.
    /// It actually starts with <b>TWO</b> (printed directly off a live component). The
    /// <c>InsertInput</c> therefore created a THIRD, which is why the inputs' own identities
    /// "didn't line up with the index-0/index-1 assumption" -- the paths were attached to
    /// inputs 0 and 1 while the component's own join logic used a different pair. Same shape as
    /// <c>Microsoft.Merge</c>, which this file already documents as starting with two.</item>
    /// <item>It added output columns by hand via <c>AddMergeJoinOutputColumn</c>. Merge Join
    /// <b>auto-creates one output column per marked-used INPUT column</b>, so those manual calls
    /// were duplicates -- that is the whole source of the "SIX output columns (ID/Name/ID1 via
    /// the auto form, ID2/Name1/Phone1 via manual calls)" the old comment reported. The auto
    /// form was never a mystery mechanism; it was the component doing its job.</item>
    /// </list>
    /// The two facts together also explain the duplicate <c>ID</c>: with both inputs' ID marked
    /// used, the component emits two output columns of the same name. Marking the key on the LEFT
    /// only is sufficient -- the join key is taken from each input's own <c>SortKeyPosition</c>
    /// plus <c>NumKeyColumns</c>, independent of which columns are marked used.</para>
    ///
    /// <para><b>Object-model facts confirmed empirically while building this</b> (same "ask the
    /// runtime, don't guess" discipline as trap 12), all independently verified, none guessed:
    /// <list type="bullet">
    /// <item>Sort's own key designation is set via <c>SetInputColumnProperty(input.ID, column.ID,
    /// "NewSortKeyPosition", 1)</c> on its OWN INPUT column -- NOT the output side, confirmed
    /// only after reading a real SSDT-authored Sort's own saved XML during the extractor's own
    /// reader work (see <c>SortPayload</c>'s doc comment for the full story, including an
    /// initial wrong reading). <see cref="AddSortByKey"/> works correctly end to end.</item>
    /// <item><c>Microsoft.MergeJoin</c> starts with <b>TWO</b> inputs from
    /// <c>ProvideComponentProperties()</c> -- do NOT call <c>InsertInput</c>. Both inputs and the
    /// output also come back UNNAMED and must be named explicitly, or the package fails
    /// validation with "the object name is not valid. The name cannot be empty".</item>
    /// <item>The designtime <c>InsertOutputColumnAt</c> throws <c>COMException 0xC0208019</c>
    /// unconditionally for Merge Join's own output (unlike Conditional Split, where the
    /// equivalent call works) -- but nothing needs to add an output column at all, since the
    /// component creates them itself from the marked input columns.</item>
    /// <item>A source's own output can be declared pre-sorted directly
    /// (<c>output.IsSorted = true</c> plus <c>outputColumn.SortKeyPosition = 1</c>), which lets a
    /// Merge Join fixture skip the Sort components entirely. Not used here -- this fixture keeps
    /// its two Sorts on purpose, because the codegen path under test
    /// (<c>PackagePlanner.ResolveMergeJoinSide</c>) requires a Sort per side -- but it is the
    /// smallest way to build a Merge Join probe when codegen is not what is being tested.</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>What this fixture is for.</b> JoinType's raw-value-to-semantics mapping had no
    /// independent verification at all until this fixture ran: the codegen layer hardcoded
    /// <c>MergeJoinType.LeftOuter</c> for every Merge Join regardless of the raw value, and the
    /// claim that raw 2 meant LeftOuter was inferred from the real destination's column shape,
    /// never measured. Running this fixture under dtexec once per raw value settled it --
    /// <b>0 = FULL OUTER, 1 = LEFT OUTER, 2 = INNER</b> -- so the one real evidenced package
    /// (JoinType=2) had been generating an INNER join as a LEFT OUTER one, emitting unmatched
    /// left rows that real SSIS drops. See
    /// <c>PackagePlanner.ResolveMergeJoinType</c> for the measurement and the mapping.</para>
    ///
    /// <para>Seed data is chosen to separate all three join types by row count alone: left keys
    /// {1,2,3}, right keys {2,3,4}, so key 1 is left-only and key 4 is right-only. INNER yields
    /// 2 rows, LEFT OUTER 3, FULL OUTER 4. The two derived variants
    /// (<c>SyntheticMergeJoinLeftOuter.dtsx</c>/<c>...FullOuter.dtsx</c>) are byte-preserving
    /// edits of this one with only the JoinType property value changed.</para>
    ///
    /// Requires the backing tables from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-merge-join-tables.sql</c> already created
    /// and seeded on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>.
    /// </summary>
    /// <summary>
    /// Flat File Source -&gt; Derived Column in <b>"Replace &lt;column&gt;" mode</b>
    /// (<c>Name &lt;- UPPER(Name)</c>, in place) -&gt; OLE DB Destination.
    ///
    /// <para>This is the fixture for the structural blind spot found by the 2026-09-02 gap audit
    /// and closed in Phase 2. An in-place Derived Column persists its expression on its own
    /// readWrite INPUT column and declares <b>no output column at all</b>, and the column keeps
    /// its upstream lineageId -- so every output-column-based mechanism in this tool saw nothing:
    /// codegen emitted <c>Name = row.Name</c> (the RAW value) where SSIS computes
    /// <c>UPPER(Name)</c>, with 0 blocking gaps, 100% extraction coverage, nothing unmapped, and
    /// the expression absent from expressions.csv. Silently wrong data, reported as success.</para>
    ///
    /// <para>Deliberately has <b>no</b> post-load SQL and <b>no</b> second transform. An earlier
    /// attempt reused SyntheticPostFlowSql.dtsx, whose own SQL_PostLoad runs
    /// <c>UPDATE ... SET Name = UPPER(Name)</c> -- under which a working and a broken Derived
    /// Column produce identical rows, so the fixture could not have detected the bug it exists
    /// for. The Derived Column is the ONLY thing here that can change a value. Seed data is
    /// lowercase and mixed-case (see synthetic-derived-column-replace-tables.sql).</para>
    /// </summary>
    private static int BuildDerivedColumnReplaceFixture(string outputPath)
    {
        var csvPath = Path.Combine(TestFixturesDir(), "synthetic-derived-column-replace-csv", "SyntheticDerivedColumnReplace.csv");
        if (!File.Exists(csvPath))
        {
            Console.Error.WriteLine($"error: expected CSV fixture at {csvPath} -- this tool's checked-in test fixtures are missing or moved.");
            return 2;
        }

        var pkg = new RtPackage { Name = "SyntheticDerivedColumnReplace" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var csvCm = AddFlatFileConnectionManager(pkg, "CM_ReplaceCsv", csvPath,
            ("ID", "DT_I4", 0, 0, 0),
            ("Name", "DT_WSTR", 50, 0, 0));

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Load";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddFlatFileSource(pipe, csvCm);
        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);

        var dcMeta = AddInPlaceTransform(pipe, "DER_Replace", "Microsoft.DerivedColumn", srcOutput, "Name",
            (inst, inputId, columnId) =>
            {
                inst.SetInputColumnProperty(inputId, columnId, "Expression", "UPPER(Name)");
                inst.SetInputColumnProperty(inputId, columnId, "FriendlyExpression", "UPPER(Name)");
            });

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticDerivedColumnReplaceTarget]");
        AttachPath(pipe, dcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut), destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// Flat File Source -&gt; <c>Microsoft.CharacterMap</c> operating IN PLACE
    /// (<c>MapFlags = 8</c>, uppercase, on a readWrite input column) -&gt; OLE DB Destination.
    ///
    /// <para>The same structural shape as <see cref="BuildDerivedColumnReplaceFixture"/> -- no
    /// output column, upstream lineageId retained -- which is what makes in-place modification a
    /// CLASS of bug rather than a Derived Column quirk. Unlike the Derived Column case this one
    /// is deliberately NOT translated: the ~20 MapFlags values have never been measured against
    /// real SSIS, and guessing that 8 means "uppercase" from the flag name is exactly the kind of
    /// unmeasured inference that produced the Merge Join JoinType bug. So this fixture's expected
    /// outcome is a precise GAP naming the column, not generated code.</para>
    /// </summary>
    private static int BuildCharacterMapInPlaceFixture(string outputPath)
    {
        var csvPath = Path.Combine(TestFixturesDir(), "synthetic-character-map-csv", "SyntheticCharacterMap.csv");
        if (!File.Exists(csvPath))
        {
            Console.Error.WriteLine($"error: expected CSV fixture at {csvPath} -- this tool's checked-in test fixtures are missing or moved.");
            return 2;
        }

        var pkg = new RtPackage { Name = "SyntheticCharacterMap" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var csvCm = AddFlatFileConnectionManager(pkg, "CM_CharacterMapCsv", csvPath,
            ("ID", "DT_I4", 0, 0, 0),
            ("Name", "DT_WSTR", 50, 0, 0));

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Load";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddFlatFileSource(pipe, csvCm);
        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);

        var cmapMeta = AddInPlaceTransform(pipe, "CMAP_UpperName", "Microsoft.CharacterMap", srcOutput, "Name",
            (inst, inputId, columnId) => inst.SetInputColumnProperty(inputId, columnId, "MapFlags", 8));

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticCharacterMapTarget]");
        AttachPath(pipe, cmapMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut), destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>A plain Flat File Source resolved against its connection manager's real schema.
    /// Factored out of <see cref="BuildFlatFileToOleDbLoad"/>'s own inline copy so the two
    /// in-place fixtures can build a source without also getting that helper's Derived Column.
    /// </summary>
    private static IDTSComponentMetaData100 AddFlatFileSource(MainPipe pipe, ConnectionManager srcCm)
    {
        var srcMeta = pipe.ComponentMetaDataCollection.New();
        srcMeta.ComponentClassID = "Microsoft.FlatFileSource";
        var srcInst = srcMeta.Instantiate();
        srcInst.ProvideComponentProperties(); // resets Name -- must set Name after, see AddOleDbComponent
        srcMeta.Name = "Flat File Source";
        var srcConn = srcMeta.RuntimeConnectionCollection[0];
        srcConn.ConnectionManagerID = srcCm.ID;
        srcConn.ConnectionManager = DtsConvert.GetExtendedInterface(srcCm);
        srcInst.AcquireConnections(null);
        srcInst.ReinitializeMetaData();
        srcInst.ReleaseConnections();
        return srcMeta;
    }

    /// <summary>
    /// Adds a synchronous transform that MODIFIES one upstream column IN PLACE: every column is
    /// marked used, and <paramref name="inPlaceColumnName"/> specifically is marked
    /// <c>UT_READWRITE</c> before <paramref name="configure"/> sets whatever property carries the
    /// in-place semantics (a Derived Column's <c>Expression</c>, a Character Map's
    /// <c>MapFlags</c>).
    ///
    /// <para>The readWrite usage type is the whole point: it is the ONLY marker in the saved XML
    /// that a column was rewritten, because such a component declares no output column for it and
    /// the column keeps its upstream lineageId.</para>
    /// </summary>
    private static IDTSComponentMetaData100 AddInPlaceTransform(
        MainPipe pipe, string name, string componentClassId, IDTSOutput100 upstreamOutput,
        string inPlaceColumnName, Action<IDTSDesigntimeComponent100, int, int> configure)
    {
        var meta = pipe.ComponentMetaDataCollection.New();
        meta.ComponentClassID = componentClassId;
        var inst = meta.Instantiate();
        inst.ProvideComponentProperties(); // resets Name -- must set Name after, see AddOleDbComponent
        meta.Name = name;

        AttachPath(pipe, upstreamOutput, meta.InputCollection[0]);
        inst.AcquireConnections(null);
        inst.ReinitializeMetaData();
        inst.ReleaseConnections();

        var input = meta.InputCollection[0];
        var virtualInput = input.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vCol in virtualInput.VirtualInputColumnCollection)
        {
            var isInPlace = string.Equals(vCol.Name, inPlaceColumnName, StringComparison.Ordinal);
            var mapped = inst.SetUsageType(input.ID, virtualInput, vCol.LineageID,
                isInPlace ? DTSUsageType.UT_READWRITE : DTSUsageType.UT_READONLY);
            if (isInPlace) configure(inst, input.ID, mapped.ID);
        }

        return meta;
    }

    private static int BuildMergeJoinFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticMergeJoin" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_MergeJoin";
        var pipe = (MainPipe)dftHost.InnerObject;

        var leftSrcMeta = AddOleDbComponent(pipe, "OLE DB Source Left", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Name FROM dbo.SyntheticMergeJoinLeft");
        ResolveOleDbMetadata(leftSrcMeta, isDestination: false);

        var rightSrcMeta = AddOleDbComponent(pipe, "OLE DB Source Right", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Phone FROM dbo.SyntheticMergeJoinRight");
        ResolveOleDbMetadata(rightSrcMeta, isDestination: false);

        var leftSortMeta = AddSortByKey(pipe, "SORT_Left", leftSrcMeta, "ID");
        var rightSortMeta = AddSortByKey(pipe, "SORT_Right", rightSrcMeta, "ID");

        var mjMeta = pipe.ComponentMetaDataCollection.New();
        mjMeta.ComponentClassID = "Microsoft.MergeJoin";
        var mjInst = mjMeta.Instantiate();
        mjInst.ProvideComponentProperties(); // resets Name -- see AddOleDbComponent's own comment
        mjMeta.Name = "MRG_LeftRight";
        // Microsoft.MergeJoin already has BOTH inputs after ProvideComponentProperties() --
        // measured, see this method's own doc comment. Calling InsertInput here (as an earlier
        // version did) created a THIRD input and was the real cause of the "phantom output
        // columns" quirk this fixture was blocked on for days. The guard is defensive only.
        if (mjMeta.InputCollection.Count < 2) mjInst.InsertInput(DTSInsertPlacement.IP_AFTER, 0);
        if (string.IsNullOrEmpty(mjMeta.InputCollection[0].Name)) mjMeta.InputCollection[0].Name = "Merge Join Left Input";
        if (string.IsNullOrEmpty(mjMeta.InputCollection[1].Name)) mjMeta.InputCollection[1].Name = "Merge Join Right Input";
        if (string.IsNullOrEmpty(mjMeta.OutputCollection[0].Name)) mjMeta.OutputCollection[0].Name = "Merge Join Output";

        AttachPath(pipe, leftSortMeta.OutputCollection[0], mjMeta.InputCollection[0]);
        AttachPath(pipe, rightSortMeta.OutputCollection[0], mjMeta.InputCollection[1]);
        mjInst.AcquireConnections(null);
        mjInst.ReinitializeMetaData();
        mjInst.ReleaseConnections();

        // Same "InputColumnCollection stays empty until marked used" rule as Sort's own input
        // (see AddSortByKey) -- confirmed real from RBC_Demo_ETL's own MRG_CustomerContacts,
        // whose own Left/Right Input each carry only a SUBSET of their upstream Sort's own
        // output columns (the join key plus whatever's actually needed downstream), not every
        // column Sort passed through.
        // The RIGHT side's own "ID" is deliberately NOT marked used: Merge Join auto-creates one
        // OUTPUT column per MARKED INPUT column (measured -- see the doc comment), so marking ID
        // on both sides produces two output columns both called "ID" and the package saves
        // corrupt. The join itself is unaffected, because the join key comes from each input's
        // own SortKeyPosition plus NumKeyColumns, NOT from which columns are marked used --
        // confirmed by a real dtexec run that joined correctly with the right key unmarked.
        MarkInputColumnsUsed(mjInst, mjMeta.InputCollection[0], "ID", "Name");
        MarkInputColumnsUsed(mjInst, mjMeta.InputCollection[1], "Phone");

        mjInst.SetComponentProperty("JoinType", 2); // the real evidenced raw value -- this fixture exists to learn what it means
        mjInst.SetComponentProperty("NumKeyColumns", 1);
        mjInst.SetComponentProperty("TreatNullsAsEqual", true);

        // No manual output columns: the component already created ID/Name/Phone itself from the
        // three marked input columns above. Adding them by hand (as an earlier version did) is
        // what produced SIX output columns in the saved XML.
        var mjOutput = mjMeta.OutputCollection[0];
        var producedColumns = string.Join(",", mjOutput.OutputColumnCollection.Cast<IDTSOutputColumn100>().Select(c => c.Name));
        Console.WriteLine($"  Merge Join auto-created output columns: {producedColumns}");

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticMergeJoinTarget]");
        AttachPath(pipe, mjOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// OLE DB Source (SqlCommand: ID, Name, Category) -&gt; Conditional Split
    /// (<c>Category == "Canada"</c> -&gt; Canada; default -&gt; RestOfWorld) -&gt;
    /// Sort_Canada/Sort_RestOfWorld (each by ID) -&gt; <c>Microsoft.Merge</c> -&gt; one OLE DB
    /// Destination -- the real evidenced shape of RBC_Demo_ETL's own DFT_MergeSortedBranches
    /// (closed 2026-08-28 by extending <c>PackagePlanner.ResolveBranch</c>'s own chain-walk to
    /// treat Sort/Merge as pure pass-throughs, the same way it already treated Union All).
    ///
    /// <para>Deliberately excludes Data Conversion (the real package's own
    /// <c>DCONV_MergeId</c>) -- already proven separately by
    /// SyntheticDataConversionSplit.dtsx and the widened "no Derived Column found" gate's own
    /// dedicated unit test. This fixture's whole job is to prove the NEW part in isolation: that
    /// Sort/Merge are genuinely name-preserving pass-throughs at RUNTIME, not just structurally
    /// walkable, so TransformEmitter's existing "row.PipelineColumnName" fallback resolves a
    /// destination column correctly with ZERO new value-resolution code, on real seeded data.</para>
    ///
    /// <para><b>Object-model facts confirmed empirically while building this</b> (same "ask the
    /// runtime, don't guess" discipline as trap 12), two of them found only after both other
    /// known "add a second input" recipes in this file FAILED against Merge specifically:
    /// <list type="bullet">
    /// <item><c>Microsoft.Merge</c> already has TWO inputs immediately after
    /// <c>ProvideComponentProperties()</c> -- unlike <c>Microsoft.UnionAll</c>/
    /// <c>Microsoft.MergeJoin</c> (both start with exactly ONE, needing
    /// <c>InputCollection.New()</c>/<c>InsertInput</c> respectively to add a second). Attempting
    /// UnionAll's own <c>InputCollection.New()</c> recipe here crashed the native
    /// <c>ReinitializeMetaData()</c> with an <c>AccessViolationException</c> (a malformed THIRD
    /// input); attempting MergeJoin's own <c>InsertInput</c> recipe instead threw
    /// <c>COMException 0xC020800E</c> immediately, even before any path was attached. Merge's own
    /// fixed two-input shape needs neither call -- both inputs already exist, just attach paths
    /// directly to <c>InputCollection[0]</c>/<c>[1]</c>.</item>
    /// <item>Merge auto-populates its own output columns from the FIRST input once
    /// <c>ReinitializeMetaData()</c> runs (same as UnionAll), but the SECOND input's own columns
    /// are not auto-mapped -- each must be pointed at the matching (by name) output column via
    /// <c>SetInputColumnProperty(..., "OutputColumnLineageID", ...)</c>, identical to UnionAll's
    /// own second input.</item>
    /// <item>Unlike <c>Microsoft.MergeJoin</c> (see <see cref="BuildMergeJoinFixture"/>'s own
    /// unresolved quirk), <c>Microsoft.Merge</c> did NOT auto-generate any extra output columns
    /// after <c>ReinitializeMetaData()</c> -- confirmed by reading the saved XML back, exactly 3
    /// output columns (ID/Name/Category), matching what was intended.</item>
    /// </list>
    /// </para>
    ///
    /// Requires the backing tables from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-sort-merge-remerge-tables.sql</c> already
    /// created and seeded on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>.
    /// </summary>
    private static int BuildSortMergeRemergeFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticSortMergeRemerge" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_SortMergeRemerge";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Name, Category FROM dbo.SyntheticSortMergeInput");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var splitMeta = pipe.ComponentMetaDataCollection.New();
        splitMeta.ComponentClassID = "Microsoft.ConditionalSplit";
        var splitInst = splitMeta.Instantiate();
        splitInst.ProvideComponentProperties(); // resets Name -- see AddOleDbComponent's own comment
        splitMeta.Name = "CSPLIT_ByCategory";

        // A shared upstream Derived Column (the standard LoadedAtUtc audit stamp every other
        // fixture in this file already uses) -- without it, this flow would be a pure
        // direct-copy-with-routing pipeline (no transform work anywhere), which is a separate,
        // already-known, deliberately unsupported gap (see SyntheticParallelShapes.dtsx's own
        // DFT_DirectCopy). Adding it here keeps this fixture representative of the real
        // evidenced package's own shape (DFT_MergeSortedBranches also has no per-branch
        // transform, but the real package's own Data Conversion satisfies the same gate --
        // deliberately excluded here, see this method's own doc comment, so a Derived Column
        // fills that role instead).
        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        var derivedMeta = BuildDerivedColumnLoadedAtUtc(pipe, srcOutput);
        var derivedOutput = derivedMeta.OutputCollection[0];

        AttachPath(pipe, derivedOutput, splitMeta.InputCollection[0]);
        splitInst.AcquireConnections(null);
        splitInst.ReinitializeMetaData();
        splitInst.ReleaseConnections();

        var splitInput = splitMeta.InputCollection[0];
        var splitVirtualInput = splitInput.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in splitVirtualInput.VirtualInputColumnCollection)
            splitInst.SetUsageType(splitInput.ID, splitVirtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);

        // IsDefaultOut is READ-ONLY -- see BuildConditionalSplitFixture's own comment.
        var defaultOutput = splitMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        defaultOutput.Name = "RestOfWorld";

        var caseOutput = splitInst.InsertOutput(DTSInsertPlacement.IP_AFTER, 0);
        caseOutput.Name = "Canada";
        splitInst.SetOutputProperty(caseOutput.ID, "FriendlyExpression", "Category == \"Canada\"");
        splitInst.SetOutputProperty(caseOutput.ID, "Expression", "Category == \"Canada\"");
        splitInst.SetOutputProperty(caseOutput.ID, "EvaluationOrder", 0);

        var sortCanadaMeta = AddSortByKey(pipe, "SORT_Canada", caseOutput, "ID");
        var sortRestMeta = AddSortByKey(pipe, "SORT_RestOfWorld", defaultOutput, "ID");

        var mergeMeta = pipe.ComponentMetaDataCollection.New();
        mergeMeta.ComponentClassID = "Microsoft.Merge";
        var mergeInst = mergeMeta.Instantiate();
        mergeInst.ProvideComponentProperties(); // resets Name -- see AddOleDbComponent's own comment
        mergeMeta.Name = "MRG_SortedUnion";

        // Unlike Microsoft.UnionAll/Microsoft.MergeJoin (both start with exactly ONE input from
        // ProvideComponentProperties(), needing InsertInput/InputCollection.New() to add a
        // second), Microsoft.Merge already has TWO inputs from ProvideComponentProperties() --
        // confirmed empirically after both of those other components' own recipes failed here:
        // InputCollection.New() (Union All's approach) crashed Merge's native
        // ReinitializeMetaData() with an AccessViolationException (a THIRD, malformed input),
        // and InsertInput (Merge Join's approach) threw COMException 0xC020800E immediately, even
        // before any path was attached -- Merge's own fixed two-input shape apparently rejects
        // any attempt to add a third. No input-creation call is needed at all; both of Merge's
        // own inputs already exist and just need their paths attached directly.
        AttachPath(pipe, sortCanadaMeta.OutputCollection[0], mergeMeta.InputCollection[0]);
        AttachPath(pipe, sortRestMeta.OutputCollection[0], mergeMeta.InputCollection[1]);
        mergeInst.AcquireConnections(null);
        mergeInst.ReinitializeMetaData();
        mergeInst.ReleaseConnections();

        var mergeFirstInput = mergeMeta.InputCollection[0];
        var mergeFirstVirtualInput = mergeFirstInput.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in mergeFirstVirtualInput.VirtualInputColumnCollection)
            mergeInst.SetUsageType(mergeFirstInput.ID, mergeFirstVirtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);

        var mergeSecondInput = mergeMeta.InputCollection[1];
        var mergeSecondVirtualInput = mergeSecondInput.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in mergeSecondVirtualInput.VirtualInputColumnCollection)
            mergeInst.SetUsageType(mergeSecondInput.ID, mergeSecondVirtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);

        // The second input's columns are NOT auto-mapped by ReinitializeMetaData() -- same rule
        // as BuildConditionalSplitRemergeFixture's own Union All second input; each must be
        // pointed at the matching (by name) output column the first input already created.
        var mergeOutput = mergeMeta.OutputCollection[0];
        foreach (IDTSInputColumn100 inCol in mergeSecondInput.InputColumnCollection)
        {
            var matchingOutputCol = mergeOutput.OutputColumnCollection.Cast<IDTSOutputColumn100>()
                .FirstOrDefault(oc => oc.Name == inCol.Name)
                ?? throw new InvalidOperationException($"fixture build error: Merge has no output column named '{inCol.Name}' to map the second input's own column onto.");
            mergeInst.SetInputColumnProperty(mergeSecondInput.ID, inCol.ID, "OutputColumnLineageID", matchingOutputCol.LineageID);
        }

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticSortMergeTarget]");
        AttachPath(pipe, mergeOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// OLE DB Source -&gt; Derived Column (<c>LoadedAtUtc</c>) -&gt; OLE DB Destination (an
    /// ordinary flow, existing only so the whole package has at least one real Data Flow Task --
    /// <c>ProgramEmitter.Emit</c>'s own "no Data Flow Task could be planned" gate would otherwise
    /// reject the whole package before the ForEach Loop's own step ever got a chance to be
    /// wired, exactly the shape RBC_Demo_ETL's own real Package_Advanced.dtsx has: FEL_SampleFiles
    /// sits ALONGSIDE DFT_ExcelImport, not instead of it) -- plus, as a SIBLING top-level
    /// executable with no precedence constraint to it (mirroring
    /// SyntheticParallelShapes.dtsx's own independent-branches shape), a <c>STOCK:FOREACHLOOP</c>
    /// container using <c>Microsoft.ForEachFileEnumerator</c> (Folder/FileSpec/Recurse/
    /// FileNameRetrievalType=1, matching RBC_Demo_ETL's own real evidenced value) whose single
    /// body task, an Execute SQL Task, has its own <c>SqlStatementSource</c> driven entirely by
    /// a <c>PropertyExpression</c> referencing the loop's own mapped variable -- the exact real
    /// shape of RBC_Demo_ETL's own <c>FEL_SampleFiles</c>/<c>SQL_LogFileName</c>.
    ///
    /// <para><b>Object-model facts confirmed empirically while building this</b> (same "ask the
    /// runtime, don't guess" discipline as trap 12), none guessed from general SSIS
    /// documentation:
    /// <list type="bullet">
    /// <item><c>Application.EnumeratorInfos["Foreach File Enumerator"]</c> (the enumerator's own
    /// DISPLAY name, not its <c>CreationName</c> "Microsoft.ForEachFileEnumerator") is the
    /// correct indexer key -- confirmed by enumerating <c>app.EnumeratorInfos</c>'s own
    /// <c>Name</c>/<c>CreationName</c> pairs before writing the lookup.</item>
    /// <item><c>ForEachFileEnumerator</c>'s own settable properties are plain CLR properties
    /// (<c>Folder</c>, <c>FileSpec</c>, <c>Recurse</c>, <c>FileNameRetrievalType</c>) on the
    /// concrete runtime type, not <c>IDTSCustomProperty</c>-style dynamic properties the way a
    /// pipeline component's designtime properties are -- confirmed by reflecting the type before
    /// use, avoiding a wasted `.Properties[...]` attempt that would have thrown.</item>
    /// <item><c>ForEachLoop.VariableMappings.Add(variableName, valueIndex)</c> is the correct
    /// call for the runtime object model (distinct from the pipeline object model's own
    /// <c>ForEachVariableMapping</c> XML shape this tool's OWN reader already parses) --
    /// confirmed by reading the saved XML back and finding the identical
    /// <c>&lt;DTS:ForEachVariableMapping&gt;</c> shape RBC_Demo_ETL's own real package has.</item>
    /// <item>An Execute SQL Task's <c>SqlStatementSource</c> PropertyExpression is set exactly
    /// like any other task property expression already used elsewhere in this codebase (CLAUDE.md's
    /// own "Sensitive credential" section: <c>host.Properties["SqlStatementSource"].
    /// SetExpression(host, exprText)</c>) -- no task-specific API needed.</item>
    /// </list>
    /// </para>
    ///
    /// Requires the backing tables from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-foreach-file-loop-tables.sql</c> already
    /// created on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>, and the sample files under
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-foreach-file-loop-files/</c> present on
    /// disk (three small .txt files, so the loop has something real to enumerate).
    /// </summary>
    private static int BuildForEachFileLoopFixture(string outputPath)
    {
        var filesDir = Path.Combine(TestFixturesDir(), "synthetic-foreach-file-loop-files");
        if (!Directory.Exists(filesDir) || Directory.GetFiles(filesDir, "*.txt").Length == 0)
        {
            Console.Error.WriteLine($"error: expected .txt sample files at {filesDir} -- this tool's checked-in test fixtures are missing or moved.");
            return 2;
        }

        var pkg = new RtPackage { Name = "SyntheticForEachFileLoop" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        pkg.Variables.Add("CurrentFile", false, "User", "");

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Load";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Amount FROM dbo.SyntheticForEachFileLoopInput");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var mainOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        var derivedMeta = BuildDerivedColumnLoadedAtUtc(pipe, mainOutput);

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticForEachFileLoopTarget]");
        AttachPath(pipe, derivedMeta.OutputCollection[0], destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        var feLoopExec = pkg.Executables.Add("STOCK:FOREACHLOOP");
        var feLoop = (Microsoft.SqlServer.Dts.Runtime.ForEachLoop)feLoopExec;
        feLoop.Name = "FEL_SampleFiles";

        // ForEachEnumeratorInfos (NOT "EnumeratorInfos"), keyed by the enumerator's own DISPLAY
        // name ("Foreach File Enumerator"), not its CreationName ("DTS.ForEachFileEnumerator.8")
        // -- confirmed by reflecting Application/ForEachEnumeratorInfos directly, not guessed.
        // CreateNew() returns a ForEachEnumeratorHost whose InnerObject is a raw COM object with
        // no strongly-typed wrapper (unlike a pipeline component's IDTSDesigntimeComponent100) --
        // its own settable properties are plain dynamic DtsProperty entries on the HOST itself
        // (feHost.Properties[...]), reflected directly rather than guessed: "Directory" (NOT
        // "Folder", despite the saved XML's own <ForEachFileEnumeratorProperties Folder="..."/>
        // attribute using that name) and "FileNameRetrieval" (NOT "FileNameRetrievalType",
        // likewise different from the saved XML's own attribute name) -- another instance of the
        // runtime property name differing from the persisted XML attribute name, the same lesson
        // trap 12 already established for other components.
        var app = new Microsoft.SqlServer.Dts.Runtime.Application();
        var feInfo = app.ForEachEnumeratorInfos["Foreach File Enumerator"];
        var feHost = feInfo.CreateNew();
        feHost.Properties["Directory"].SetValue(feHost, filesDir);
        feHost.Properties["FileSpec"].SetValue(feHost, "*.txt");
        feHost.Properties["Recurse"].SetValue(feHost, false);
        feHost.Properties["FileNameRetrieval"].SetValue(feHost, 1); // 1 = Name and extension, matching RBC_Demo_ETL's own real evidenced value
        feLoop.ForEachEnumerator = feHost;

        // VariableMappings.Add() takes NO arguments (confirmed by reflection, unlike the
        // guessed-and-wrong Add(name, index) signature) -- it returns a ForEachVariableMapping
        // whose own VariableName/ValueIndex are ordinary settable properties, the same
        // "Add() then set properties on the result" shape InsertOutput already uses elsewhere in
        // this file for a Conditional Split's own case output.
        var mapping = feLoop.VariableMappings.Add();
        mapping.VariableName = "User::CurrentFile";
        mapping.ValueIndex = 0;

        var sqlTaskHost = (RtTaskHost)feLoop.Executables.Add("Microsoft.ExecuteSQLTask");
        sqlTaskHost.Name = "SQL_LogFileName";
        sqlTaskHost.Properties["Connection"].SetValue(sqlTaskHost, sqlCm.Name);
        sqlTaskHost.Properties["SqlStatementSource"].SetExpression(sqlTaskHost,
            "\"INSERT INTO dbo.SyntheticForEachFileLoopLog (FileName) VALUES (N'\" + @[User::CurrentFile] + \"');\"");

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// A ForEach File Enumerator loop whose BODY is a Data Flow Task, re-run once per enumerated
    /// file -- built speculatively 2026-08-30 (zero real evidenced package anywhere in the
    /// tracked portfolio has this shape; the one real ForEach Loop, RBC_Demo_ETL's own
    /// FEL_SampleFiles, uses a single Execute SQL Task body instead, see
    /// <see cref="BuildForEachFileLoopFixture"/>). The loop's single child, <c>DFT_Load</c>, is a
    /// Flat File Source (its OWN connection manager's <c>ConnectionString</c> driven entirely by
    /// a PropertyExpression referencing the loop's own mapped variable, <c>@[User::CurrentFile]</c>
    /// -- confirmed via an isolated object-model probe before writing this method: the connection
    /// manager's <c>Properties["ConnectionString"]</c> supports <c>SetExpression</c> exactly like
    /// any task property, producing the identical <c>&lt;DTS:PropertyExpression Name=
    /// "ConnectionString"&gt;</c> shape <c>ConnectionManagerSpec.PropertyExpressions</c> already
    /// reads for a real package's own expression-driven CSV connection managers, e.g.
    /// LoadReferenceData's <c>CM_DepartmentCsv</c>) -&gt; Derived Column (<c>LoadedAtUtc</c>,
    /// the same minimal audit-stamp shape every other fixture in this file uses) -&gt; OLE DB
    /// Destination. The enumerator's <c>FileNameRetrieval</c> is left at its schema default (0 =
    /// FullyQualified) so <c>@[User::CurrentFile]</c> resolves to each file's own FULL path per
    /// iteration -- the simplest, bare-reference connection-string expression shape this round's
    /// own <c>ForEachLoopEmitter</c> reuse supports directly, needing no folder-concatenation
    /// translation.
    ///
    /// <para><b>No companion top-level flow is needed</b> (unlike <c>SyntheticForEachFileLoop.dtsx</c>,
    /// which added <c>DFT_Load</c> outside its own loop purely to avoid Generate's own "no Data
    /// Flow Task" early-exit gate) -- this round's own generator fix widens that gate to also
    /// recognize a <c>ForEachDataFlowLoopStep</c>, so a package whose ONLY content is this loop
    /// is exactly what proves that fix.</para>
    ///
    /// Requires the backing tables from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-foreach-data-flow-loop-tables.sql</c>
    /// already created on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>, and the two small .csv files
    /// under <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-foreach-data-flow-loop-files/</c>
    /// present on disk.
    /// </summary>
    /// <summary>
    /// A Microsoft.Aggregate flow -- built speculatively 2026-08-30 (RBC_Demo_ETL's own real
    /// instance, <c>DFT_LookupAndAggregate\AGG_ByRegion</c>, sits downstream of a Lookup that
    /// already blocks the whole flow regardless of Aggregate support, confirmed by re-surveying
    /// the portfolio twice; built anyway on the user's own explicit request, "for completeness"
    /// rather than to close a real gap). OLE DB Source (SqlCommand: Region, CustomerID) -&gt;
    /// Aggregate (GroupBy Region, Count(CustomerID) AS CustomerCount -- the exact real evidenced
    /// shape from <c>AGG_ByRegion</c> itself: AggregationType=0 for the group-by column,
    /// AggregationType=1 for the count column) -&gt; OLE DB Destination.
    ///
    /// <para><b>Object-model facts confirmed empirically while building this</b> (same "ask the
    /// runtime, don't guess" discipline as trap 12): the ad hoc PowerShell probe this method's
    /// own commit replaced failed with "Element not found" trying to <c>Instantiate()</c> a
    /// Microsoft.Aggregate component -- NOT because Aggregate itself is unsupported, but because
    /// that PowerShell script's own <c>LoadWithPartialName</c> calls resolve a different (wrong)
    /// GAC assembly set than this project's own <c>Build/Ssis.Gac.props</c>-driven references;
    /// the identical construction sequence works cleanly once run through this properly-
    /// referenced project. <c>AggregationType</c>/<c>KeyScale</c> are native COM type converters
    /// with no managed CLR enum backing them (confirmed by reflecting every loaded assembly and
    /// finding nothing named either) -- unlike some other raw-enum properties in this codebase,
    /// there is no public Microsoft SDK reference for this one either, so its real semantics
    /// were measured via a genuine dtexec run (see <c>synthetic-aggregate-tables.sql</c>'s own
    /// seed data, which deliberately includes one NULL CustomerID specifically to distinguish
    /// COUNT(column) semantics -- excludes NULL -- from COUNT(*) semantics -- includes it).
    /// Confirmed real: AggregationType=1 with an <c>AggregationColumnId</c> set is COUNT(column)
    /// -- NULLs excluded, matching ordinary SQL COUNT(column) rather than COUNT(*). Also
    /// confirmed: calling <c>SetOutputColumnDataTypeProperties</c> on a freshly-inserted output
    /// column throws <c>COMException 0xC020401A</c> -- unlike Derived Column/Conditional Split,
    /// an Aggregate output column's data type is derived AUTOMATICALLY from whichever input
    /// column <c>AggregationColumnId</c> references (confirmed by reading the column back after
    /// setting only <c>AggregationColumnId</c>/<c>AggregationType</c>: Region resolved to
    /// DT_WSTR/50, matching its source column exactly; CustomerCount resolved to DT_UI8/0 on its
    /// own, matching the real evidenced XML's <c>ui8</c> Count-column type) -- so this method
    /// never calls it at all.</para>
    /// </summary>
    private static int BuildAggregateFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticAggregate" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Aggregate";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT Region, CustomerID FROM dbo.SyntheticAggregateSource");
        ResolveOleDbMetadata(srcMeta, isDestination: false);
        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);

        var aggMeta = pipe.ComponentMetaDataCollection.New();
        aggMeta.ComponentClassID = "Microsoft.Aggregate";
        var aggInst = aggMeta.Instantiate();
        aggInst.ProvideComponentProperties(); // resets Name to the class default -- must set Name after this
        aggMeta.Name = "AGG_ByRegion";

        AttachPath(pipe, srcOutput, aggMeta.InputCollection[0]);
        aggInst.AcquireConnections(null);
        aggInst.ReinitializeMetaData();
        aggInst.ReleaseConnections();

        var aggInput = aggMeta.InputCollection[0];
        var aggVirtualInput = aggInput.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in aggVirtualInput.VirtualInputColumnCollection)
            aggInst.SetUsageType(aggInput.ID, aggVirtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);

        var regionInputCol = aggInput.InputColumnCollection.Cast<IDTSInputColumn100>().First(c => c.Name == "Region");
        var customerIdInputCol = aggInput.InputColumnCollection.Cast<IDTSInputColumn100>().First(c => c.Name == "CustomerID");

        var aggOutput = aggMeta.OutputCollection[0];

        var regionOutCol = aggInst.InsertOutputColumnAt(aggOutput.ID, 0, "Region", "");
        aggInst.SetOutputColumnProperty(aggOutput.ID, regionOutCol.ID, "AggregationColumnId", regionInputCol.LineageID);
        aggInst.SetOutputColumnProperty(aggOutput.ID, regionOutCol.ID, "AggregationType", 0); // GroupBy -- real evidenced value

        var countOutCol = aggInst.InsertOutputColumnAt(aggOutput.ID, 1, "CustomerCount", "");
        aggInst.SetOutputColumnProperty(aggOutput.ID, countOutCol.ID, "AggregationColumnId", customerIdInputCol.LineageID);
        aggInst.SetOutputColumnProperty(aggOutput.ID, countOutCol.ID, "AggregationType", 1); // Count -- real evidenced value

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticAggregateTarget]");
        AttachPath(pipe, aggOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// Closes gap-audit Phase 3.3 (2026-09-02): widens Aggregate support beyond the original
    /// GroupBy/Count-only round to Sum(4)/Average(5)/Minimum(6)/Maximum(7)/CountDistinct(3)/
    /// CountAll(2) -- every raw value measured via two temporary object-model probes (since
    /// removed) run against `.\SQLFORPOC_2022`: <c>aggregate-type-probe</c> (discovered the live
    /// COM property's own valid range is exactly 0-7, string names all REJECTED with
    /// <c>0xC0204006</c> -- a pure native int enum, no string coercion) and
    /// <c>aggregate-type-semantics-probe</c> (a real dtexec run against deliberately
    /// distinguishing seed data -- a NULL, a duplicate, a wide spread, and an all-NULL group --
    /// mapped each raw value to its real function unambiguously, see
    /// <c>Ssis.Extract.Model.Pipeline.AggregatePayload</c>'s own doc comment for the full result).
    ///
    /// OLE DB Source (SqlCommand: the exact seed shape the probe itself used) -&gt; Aggregate
    /// (GroupBy Region; TotalRows = CountAll with NO AggregationColumnId at all, proving SSIS's
    /// own "CountAll needs no column reference" behavior for real, not just in the probe;
    /// DistinctAmounts = CountDistinct(Amount); SumAmount/AverageAmount/MinAmount/MaxAmount =
    /// Sum/Average/Minimum/Maximum(Amount)) -&gt; OLE DB Destination.
    ///
    /// Requires the backing tables from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-aggregate-functions-tables.sql</c> already
    /// created on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>.
    /// </summary>
    private static int BuildAggregateFunctionsFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticAggregateFunctions" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_AggregateFunctions";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null,
            sqlCommand: "SELECT Region, Amount FROM dbo.SyntheticAggregateFunctionsSource");
        ResolveOleDbMetadata(srcMeta, isDestination: false);
        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);

        var aggMeta = pipe.ComponentMetaDataCollection.New();
        aggMeta.ComponentClassID = "Microsoft.Aggregate";
        var aggInst = aggMeta.Instantiate();
        aggInst.ProvideComponentProperties();
        aggMeta.Name = "AGG_Functions";

        AttachPath(pipe, srcOutput, aggMeta.InputCollection[0]);
        aggInst.AcquireConnections(null);
        aggInst.ReinitializeMetaData();
        aggInst.ReleaseConnections();

        var aggInput = aggMeta.InputCollection[0];
        var aggVirtualInput = aggInput.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in aggVirtualInput.VirtualInputColumnCollection)
            aggInst.SetUsageType(aggInput.ID, aggVirtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);

        var regionInputCol = aggInput.InputColumnCollection.Cast<IDTSInputColumn100>().First(c => c.Name == "Region");
        var amountInputCol = aggInput.InputColumnCollection.Cast<IDTSInputColumn100>().First(c => c.Name == "Amount");

        var aggOutput = aggMeta.OutputCollection[0];

        var regionOutCol = aggInst.InsertOutputColumnAt(aggOutput.ID, 0, "Region", "");
        aggInst.SetOutputColumnProperty(aggOutput.ID, regionOutCol.ID, "AggregationColumnId", regionInputCol.LineageID);
        aggInst.SetOutputColumnProperty(aggOutput.ID, regionOutCol.ID, "AggregationType", 0); // GroupBy

        // CountAll -- deliberately NO AggregationColumnId at all, proving the real "no column
        // reference needed" behavior end to end, not just in the removed probe.
        var totalRowsOutCol = aggInst.InsertOutputColumnAt(aggOutput.ID, aggOutput.OutputColumnCollection.Count, "TotalRows", "");
        aggInst.SetOutputColumnProperty(aggOutput.ID, totalRowsOutCol.ID, "AggregationType", 2); // CountAll

        var distinctOutCol = aggInst.InsertOutputColumnAt(aggOutput.ID, aggOutput.OutputColumnCollection.Count, "DistinctAmounts", "");
        aggInst.SetOutputColumnProperty(aggOutput.ID, distinctOutCol.ID, "AggregationColumnId", amountInputCol.LineageID);
        aggInst.SetOutputColumnProperty(aggOutput.ID, distinctOutCol.ID, "AggregationType", 3); // CountDistinct

        var sumOutCol = aggInst.InsertOutputColumnAt(aggOutput.ID, aggOutput.OutputColumnCollection.Count, "SumAmount", "");
        aggInst.SetOutputColumnProperty(aggOutput.ID, sumOutCol.ID, "AggregationColumnId", amountInputCol.LineageID);
        aggInst.SetOutputColumnProperty(aggOutput.ID, sumOutCol.ID, "AggregationType", 4); // Sum

        var avgOutCol = aggInst.InsertOutputColumnAt(aggOutput.ID, aggOutput.OutputColumnCollection.Count, "AverageAmount", "");
        aggInst.SetOutputColumnProperty(aggOutput.ID, avgOutCol.ID, "AggregationColumnId", amountInputCol.LineageID);
        aggInst.SetOutputColumnProperty(aggOutput.ID, avgOutCol.ID, "AggregationType", 5); // Average

        var minOutCol = aggInst.InsertOutputColumnAt(aggOutput.ID, aggOutput.OutputColumnCollection.Count, "MinAmount", "");
        aggInst.SetOutputColumnProperty(aggOutput.ID, minOutCol.ID, "AggregationColumnId", amountInputCol.LineageID);
        aggInst.SetOutputColumnProperty(aggOutput.ID, minOutCol.ID, "AggregationType", 6); // Minimum

        var maxOutCol = aggInst.InsertOutputColumnAt(aggOutput.ID, aggOutput.OutputColumnCollection.Count, "MaxAmount", "");
        aggInst.SetOutputColumnProperty(aggOutput.ID, maxOutCol.ID, "AggregationColumnId", amountInputCol.LineageID);
        aggInst.SetOutputColumnProperty(aggOutput.ID, maxOutCol.ID, "AggregationType", 7); // Maximum

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticAggregateFunctionsTarget]");
        AttachPath(pipe, aggOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// A PROBE, built 2026-08-30: OLE DB Source (SqlCommand: ID, I8Val bigint, NumericVal
    /// decimal(18,2), R4Val real) -&gt; OLE DB Destination (all three narrowed to int), a plain
    /// passthrough with NO Data Conversion/Derived Column in between -- three simultaneous
    /// numeric-passthrough-coercion pairings this tool has never measured (i8-to-i4,
    /// numeric-to-i4, r4-to-i4), named as candidates in CLAUDE.md's own "remaining known gaps"
    /// list. Same shape as the original r8-to-i4/wstr-to-i4 probes, just three columns in one
    /// flow instead of one, since none of the three needs its own destination-shape difference.
    ///
    /// Requires the backing tables from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-int-numeric-coercion-tables.sql</c> already
    /// created on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>.
    /// </summary>
    private static int BuildIntNumericCoercionFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticIntNumericCoercion" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Coerce";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, I8Val, NumericVal, R4Val FROM dbo.SyntheticIntNumericCoercionSource");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticIntNumericCoercionTarget]");
        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        AttachPath(pipe, srcOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    private static int BuildForEachDataFlowLoopFixture(string outputPath)
    {
        var filesDir = Path.Combine(TestFixturesDir(), "synthetic-foreach-data-flow-loop-files");
        if (!Directory.Exists(filesDir) || Directory.GetFiles(filesDir, "*.csv").Length == 0)
        {
            Console.Error.WriteLine($"error: expected .csv sample files at {filesDir} -- this tool's checked-in test fixtures are missing or moved.");
            return 2;
        }

        var pkg = new RtPackage { Name = "SyntheticForEachDataFlowLoop" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        pkg.Variables.Add("CurrentFile", false, "User", "");

        var feLoopExec = pkg.Executables.Add("STOCK:FOREACHLOOP");
        var feLoop = (Microsoft.SqlServer.Dts.Runtime.ForEachLoop)feLoopExec;
        feLoop.Name = "FEL_SampleFiles";

        var app = new Microsoft.SqlServer.Dts.Runtime.Application();
        var feInfo = app.ForEachEnumeratorInfos["Foreach File Enumerator"];
        var feHost = feInfo.CreateNew();
        feHost.Properties["Directory"].SetValue(feHost, filesDir);
        feHost.Properties["FileSpec"].SetValue(feHost, "*.csv");
        feHost.Properties["Recurse"].SetValue(feHost, false);
        feHost.Properties["FileNameRetrieval"].SetValue(feHost, 0); // 0 = fully qualified name -- see this method's own doc comment
        feLoop.ForEachEnumerator = feHost;

        var mapping = feLoop.VariableMappings.Add();
        mapping.VariableName = "User::CurrentFile";
        mapping.ValueIndex = 0;

        // A FLATFILE connection manager whose ConnectionString is entirely expression-driven --
        // deliberately NOT AddFlatFileConnectionManager (that helper always sets a STATIC
        // ConnectionString), since the whole point here is a per-iteration path with no design-
        // time default at all.
        var csvCm = pkg.Connections.Add("FLATFILE");
        csvCm.Name = "CM_CurrentFileCsv";
        var ff = (IDTSConnectionManagerFlatFile100)csvCm.InnerObject;
        ff.Format = "Delimited";
        ff.ColumnNamesInFirstDataRow = true;
        ff.HeaderRowDelimiter = "\r\n";
        ff.CodePage = 1252;
        (string ColumnName, string DtType, int MaxWidth)[] csvColumns = [("ID", "DT_I4", 0), ("Name", "DT_WSTR", 50)];
        for (var i = 0; i < csvColumns.Length; i++)
        {
            var (columnName, dtType, maxWidth) = csvColumns[i];
            var col = ff.Columns.Add();
            col.ColumnType = "Delimited";
            col.ColumnDelimiter = i == csvColumns.Length - 1 ? "\r\n" : ",";
            col.DataType = (RtDataType)Enum.Parse(typeof(RtDataType), dtType);
            col.MaximumWidth = maxWidth;
            ((IDTSName100)col).Name = columnName;
        }
        // Confirmed via an isolated probe (see this method's own doc comment): ConnectionString
        // IS an ordinary expression-capable property on a connection manager, same
        // Properties["X"].SetExpression(cm, expr) shape a task property uses -- no design-time
        // default is set at all, matching a real package whose CSV connection manager has none
        // (CLAUDE.md's own note on LoadReferenceData's expression-driven connection managers).
        csvCm.Properties["ConnectionString"].SetExpression(csvCm, "@[User::CurrentFile]");

        var dftHost = (RtTaskHost)feLoop.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Load";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = pipe.ComponentMetaDataCollection.New();
        srcMeta.ComponentClassID = "Microsoft.FlatFileSource";
        var srcInst = srcMeta.Instantiate();
        srcInst.ProvideComponentProperties(); // resets Name to the class default -- must set Name after this
        srcMeta.Name = "Flat File Source";
        var srcConn = srcMeta.RuntimeConnectionCollection[0];
        srcConn.ConnectionManagerID = csvCm.ID;
        srcConn.ConnectionManager = DtsConvert.GetExtendedInterface(csvCm);
        srcInst.AcquireConnections(null);
        srcInst.ReinitializeMetaData();
        srcInst.ReleaseConnections();

        var mainOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);
        var derivedMeta = BuildDerivedColumnLoadedAtUtc(pipe, mainOutput);

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticForEachDataFlowLoopTarget]");
        AttachPath(pipe, derivedMeta.OutputCollection[0], destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// Excel Source (AccessMode=0/OpenRowset, the real evidenced shape) -> OLE DB Destination,
    /// a genuine direct-copy pipeline (no transform at all) -- proving both Excel Source support
    /// AND the 2026-08-28 removal of the "no Derived Column" gate for OLE DB destinations, added
    /// alongside it for exactly this reason (RBC_Demo_ETL's own DFT_ExcelImport is the identical
    /// shape). Reuses the REAL third-party <c>synthetic-excel-source.xlsx</c> (a checked-in copy
    /// of RBC_Demo_ETL's own <c>sample-data/DripEligibility.xlsx</c>, not a hand-built stand-in --
    /// this project has no .xlsx WRITER anywhere, and the real file is real evidence already) as
    /// this fixture's own Excel connection manager data source, so <c>ReinitializeMetaData()</c>
    /// resolves genuine worksheet columns (CustomerID/DripEligible/ReviewedBy) through the real
    /// ACE OLEDB provider, confirmed installed and working on this machine before writing this
    /// method (a plain <c>System.Data.OleDb.OleDbConnection</c> probe against the same file).
    ///
    /// <c>AddOleDbComponent</c>/<c>ResolveOleDbMetadata</c> work UNCHANGED for
    /// <c>Microsoft.ExcelSource</c> -- confirmed real from RBC_Demo_ETL's own saved XML before
    /// writing this method: an Excel Source component reuses OLE DB Source's exact own
    /// OpenRowset/AccessMode custom property names and even its connection's own name,
    /// "OleDbConnection".
    /// </summary>
    private static int BuildExcelSourceFixture(string outputPath)
    {
        var excelPath = Path.Combine(TestFixturesDir(), "synthetic-excel-source.xlsx");
        if (!File.Exists(excelPath))
        {
            Console.Error.WriteLine($"error: expected the real sample .xlsx at {excelPath} -- this tool's checked-in test fixture is missing or moved.");
            return 2;
        }

        var pkg = new RtPackage { Name = "SyntheticExcelSource" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var excelCm = pkg.Connections.Add("EXCEL");
        excelCm.Name = "CM_Excel_Fixture";
        excelCm.ConnectionString = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={excelPath};Extended Properties=\"EXCEL 12.0 XML;HDR=YES\";";

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_ExcelLoad";
        var pipe = (MainPipe)dftHost.InnerObject;

        // "DripEligibility$" -- the real worksheet's own name inside the real checked-in
        // workbook, confirmed from the earlier probe against RBC_Demo_ETL's own package.
        var srcMeta = AddOleDbComponent(pipe, "Excel Source", "Microsoft.ExcelSource", excelCm,
            accessMode: 0, openRowset: "DripEligibility$");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var mainOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticExcelSourceTarget]");
        AttachPath(pipe, mainOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// PROBE, built speculatively 2026-08-30 (no real package needs this -- the only real Excel
    /// Source in the tracked portfolio, RBC_Demo_ETL's own EXCEL_SRC_Drip, is AccessMode=0/
    /// OpenRowset): an Excel Source with AccessMode=2 (SqlCommand) instead of OpenRowset,
    /// SqlCommand set to a bare "SELECT * FROM [DripEligibility$]" -- the simplest possible Jet/
    /// ACE SQL query, functionally equivalent to just naming the worksheet. Reuses the exact
    /// same real checked-in workbook (<c>synthetic-excel-source.xlsx</c>) as the OpenRowset-mode
    /// fixture, so its own known-good row values are the ground truth to diff against. Confirms
    /// (or refutes) whether the real ACE OLEDB provider in this environment accepts this SQL
    /// shape and resolves metadata identically to OpenRowset mode -- ask the runtime, don't
    /// guess, same discipline as everywhere else in this file.
    /// </summary>
    private static int BuildExcelSourceSqlCommandFixture(string outputPath)
    {
        var excelPath = Path.Combine(TestFixturesDir(), "synthetic-excel-source.xlsx");
        if (!File.Exists(excelPath))
        {
            Console.Error.WriteLine($"error: expected the real sample .xlsx at {excelPath} -- this tool's checked-in test fixture is missing or moved.");
            return 2;
        }

        var pkg = new RtPackage { Name = "SyntheticExcelSourceSqlCommand" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var excelCm = pkg.Connections.Add("EXCEL");
        excelCm.Name = "CM_Excel_Fixture";
        excelCm.ConnectionString = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={excelPath};Extended Properties=\"EXCEL 12.0 XML;HDR=YES\";";

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_ExcelLoad";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "Excel Source", "Microsoft.ExcelSource", excelCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT * FROM [DripEligibility$]");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var mainOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticExcelSourceSqlCommandTarget]");
        AttachPath(pipe, mainOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>Attaches a Sort component downstream of <paramref name="upstream"/>'s own main
    /// output and marks <paramref name="keyColumnName"/> as its sole ascending sort key (position
    /// 1) via <c>SetInputColumnProperty</c> on the Sort's OWN input column -- see
    /// <see cref="BuildMergeJoinFixture"/>'s own doc comment for why this is the input side, not
    /// the output side.</summary>
    private static IDTSComponentMetaData100 AddSortByKey(MainPipe pipe, string name, IDTSComponentMetaData100 upstream, string keyColumnName) =>
        AddSortByKey(pipe, name, upstream.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut), keyColumnName);

    /// <summary>Same as the <see cref="IDTSComponentMetaData100"/> overload, but takes the
    /// upstream OUTPUT directly -- needed when the upstream component has more than one
    /// non-error output (e.g. a Conditional Split's own named case/default outputs), where
    /// ".Single(o => !o.IsErrorOut)" would throw.</summary>
    private static IDTSComponentMetaData100 AddSortByKey(MainPipe pipe, string name, IDTSOutput100 upstreamOutput, string keyColumnName)
    {
        var meta = pipe.ComponentMetaDataCollection.New();
        meta.ComponentClassID = "Microsoft.Sort";
        var inst = meta.Instantiate();
        inst.ProvideComponentProperties();
        meta.Name = name;

        AttachPath(pipe, upstreamOutput, meta.InputCollection[0]);
        inst.AcquireConnections(null);
        inst.ReinitializeMetaData();
        inst.ReleaseConnections();

        // ReinitializeMetaData() only populates VirtualInputColumnCollection (available-but-unused
        // columns) -- InputColumnCollection (actually-used ones) stays empty until every wanted
        // column is explicitly marked via SetUsageType, same mechanism BuildDerivedColumnLoadedAtUtc
        // already uses for a passthrough transform.
        var input = meta.InputCollection[0];
        var virtualInput = input.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in virtualInput.VirtualInputColumnCollection)
            inst.SetUsageType(input.ID, virtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);

        var keyCol = input.InputColumnCollection.Cast<IDTSInputColumn100>().First(c => c.Name == keyColumnName);
        inst.SetInputColumnProperty(input.ID, keyCol.ID, "NewSortKeyPosition", 1);
        inst.SetInputColumnProperty(input.ID, keyCol.ID, "NewComparisonFlags", 0);

        return meta;
    }

    /// <summary>
    /// OLE DB Source (SqlCommand, seed rows deliberately out of ID order) -> Sort (by ID,
    /// ascending) -> Flat File Destination, gap-audit Phase 3.5 (2026-09-02). Deliberately a
    /// Flat File, not a SQL, destination -- <c>SqlBulkCopy</c> gives no storage-order guarantee,
    /// so ordering has no OBSERVABLE effect against a SQL table; a flat file's own row order is
    /// directly readable back. Reuses <see cref="AddSortByKey"/> verbatim (already proven via
    /// the Merge Join round) and <see cref="AddFlatFileDestinationConnectionManager"/>/the same
    /// raw Flat File Destination construction <see cref="BuildOleDbToFlatFileExport"/> already
    /// uses -- no new object-model technique needed for this fixture.</summary>
    private static int BuildStandaloneSortFixture(string outputPath)
    {
        var outDir = Path.Combine(TestFixturesDir(), "synthetic-standalone-sort-output");
        Directory.CreateDirectory(outDir);
        var exportPath = Path.Combine(outDir, "sorted-export.csv");

        var pkg = new RtPackage { Name = "SyntheticStandaloneSort" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var ffCm = AddFlatFileDestinationConnectionManager(pkg, "CM_FF_Export", exportPath,
            format: "Delimited", columnNamesInFirstDataRow: true,
            ("ID", 20, false), ("Name", 50, true));

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Sort";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Name FROM dbo.SyntheticStandaloneSortSource");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var sortMeta = AddSortByKey(pipe, "SORT_ById", srcMeta, "ID");

        var destMeta = pipe.ComponentMetaDataCollection.New();
        destMeta.ComponentClassID = "Microsoft.FlatFileDestination";
        var destInst = destMeta.Instantiate();
        destInst.ProvideComponentProperties();
        destMeta.Name = "Flat File Destination";
        destInst.SetComponentProperty("Overwrite", true);

        var destConn = destMeta.RuntimeConnectionCollection[0];
        destConn.ConnectionManagerID = ffCm.ID;
        destConn.ConnectionManager = DtsConvert.GetExtendedInterface(ffCm);

        var sortOutput = sortMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);
        AttachPath(pipe, sortOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// Gap-audit Phase 3.6 -- the single most important probe in that phase, per its own
    /// plan: two GENUINELY INDEPENDENT OLE DB Sources (not diverged from one shared upstream,
    /// unlike every prior Merge fixture in this file -- <see cref="BuildSortMergeRemergeFixture"/>'s
    /// own Merge always joins two branches of the SAME Conditional Split), each with a
    /// DISJOINT, INTERLEAVED key range (Left: ID 1,3,5; Right: ID 2,4,6, both seeded out of
    /// order so <see cref="AddSortByKey"/>'s own Sort is genuinely exercised on each side) ->
    /// <c>Microsoft.Merge</c> -> Flat File Destination, so the real output ROW ORDER is
    /// directly readable back (a SQL destination gives no storage-order guarantee, same
    /// reasoning as <see cref="BuildStandaloneSortFixture"/>).
    ///
    /// <para><b>What this settles, read directly off the output file, no guessing:</b> a
    /// result of <c>1,2,3,4,5,6</c> means <c>Microsoft.Merge</c> performs a TRUE
    /// sort-preserving two-pointer interleave of its two sorted inputs (needing a new
    /// algorithm, essentially <c>MergeJoinRowSource</c> minus the key-match test, over two
    /// <c>SortingRowSource</c>-wrapped sides); a result of <c>1,3,5,2,4,6</c> means it is
    /// plain concatenation after independently sorting each side (no new algorithm beyond a
    /// trivial sequential-concatenation source fed by two <c>SortingRowSource</c>-wrapped
    /// sides). Object-model construction reuses <see cref="BuildSortMergeRemergeFixture"/>'s
    /// own already-proven Merge recipe (two inputs already exist from
    /// <c>ProvideComponentProperties()</c>; second input's output columns must be mapped
    /// explicitly via <c>OutputColumnLineageID</c>) verbatim -- nothing new needed there.</para>
    ///
    /// Requires the backing tables from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-merge-interleave-probe-tables.sql</c>
    /// already created and seeded on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>.
    /// </summary>
    private static int BuildMergeInterleaveProbeFixture(string outputPath)
    {
        var outDir = Path.Combine(TestFixturesDir(), "synthetic-merge-interleave-probe-output");
        Directory.CreateDirectory(outDir);
        var exportPath = Path.Combine(outDir, "merged-export.csv");

        var pkg = new RtPackage { Name = "SyntheticMergeInterleaveProbe" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var ffCm = AddFlatFileDestinationConnectionManager(pkg, "CM_FF_Export", exportPath,
            format: "Delimited", columnNamesInFirstDataRow: true,
            ("ID", 20, false), ("Name", 50, true));

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_MergeInterleaveProbe";
        var pipe = (MainPipe)dftHost.InnerObject;

        var leftSrcMeta = AddOleDbComponent(pipe, "OLE DB Source Left", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Name FROM dbo.SyntheticMergeProbeLeft");
        ResolveOleDbMetadata(leftSrcMeta, isDestination: false);

        var rightSrcMeta = AddOleDbComponent(pipe, "OLE DB Source Right", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Name FROM dbo.SyntheticMergeProbeRight");
        ResolveOleDbMetadata(rightSrcMeta, isDestination: false);

        var leftSortMeta = AddSortByKey(pipe, "SORT_Left", leftSrcMeta, "ID");
        var rightSortMeta = AddSortByKey(pipe, "SORT_Right", rightSrcMeta, "ID");

        var mergeMeta = pipe.ComponentMetaDataCollection.New();
        mergeMeta.ComponentClassID = "Microsoft.Merge";
        var mergeInst = mergeMeta.Instantiate();
        mergeInst.ProvideComponentProperties(); // resets Name -- see AddOleDbComponent's own comment
        mergeMeta.Name = "MRG_Interleave";

        // Microsoft.Merge already has TWO inputs from ProvideComponentProperties() -- see
        // BuildSortMergeRemergeFixture's own doc comment for why (both UnionAll's and Merge
        // Join's own "add a second input" recipes fail against this component specifically).
        AttachPath(pipe, leftSortMeta.OutputCollection[0], mergeMeta.InputCollection[0]);
        AttachPath(pipe, rightSortMeta.OutputCollection[0], mergeMeta.InputCollection[1]);
        mergeInst.AcquireConnections(null);
        mergeInst.ReinitializeMetaData();
        mergeInst.ReleaseConnections();

        var mergeFirstInput = mergeMeta.InputCollection[0];
        var mergeFirstVirtualInput = mergeFirstInput.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in mergeFirstVirtualInput.VirtualInputColumnCollection)
            mergeInst.SetUsageType(mergeFirstInput.ID, mergeFirstVirtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);

        var mergeSecondInput = mergeMeta.InputCollection[1];
        var mergeSecondVirtualInput = mergeSecondInput.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in mergeSecondVirtualInput.VirtualInputColumnCollection)
            mergeInst.SetUsageType(mergeSecondInput.ID, mergeSecondVirtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);

        // The second input's own columns are NOT auto-mapped by ReinitializeMetaData() -- same
        // rule as BuildSortMergeRemergeFixture's own second input.
        var mergeOutput = mergeMeta.OutputCollection[0];
        foreach (IDTSInputColumn100 inCol in mergeSecondInput.InputColumnCollection)
        {
            var matchingOutputCol = mergeOutput.OutputColumnCollection.Cast<IDTSOutputColumn100>()
                .FirstOrDefault(oc => oc.Name == inCol.Name)
                ?? throw new InvalidOperationException($"fixture build error: Merge has no output column named '{inCol.Name}' to map the second input's own column onto.");
            mergeInst.SetInputColumnProperty(mergeSecondInput.ID, inCol.ID, "OutputColumnLineageID", matchingOutputCol.LineageID);
        }

        var destMeta = pipe.ComponentMetaDataCollection.New();
        destMeta.ComponentClassID = "Microsoft.FlatFileDestination";
        var destInst = destMeta.Instantiate();
        destInst.ProvideComponentProperties();
        destMeta.Name = "Flat File Destination";
        destInst.SetComponentProperty("Overwrite", true);

        var destConn = destMeta.RuntimeConnectionCollection[0];
        destConn.ConnectionManagerID = ffCm.ID;
        destConn.ConnectionManager = DtsConvert.GetExtendedInterface(ffCm);

        AttachPath(pipe, mergeOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// Gap-audit Phase 3.6's second probe: two genuinely independent OLE DB Sources (no Sort
    /// anywhere -- deliberately, since <c>Microsoft.UnionAll</c> carries no "sorted input"
    /// concept the way <c>Microsoft.Merge</c> does) -> <c>Microsoft.UnionAll</c> -> Flat File
    /// Destination, seeded with each side's own rows deliberately NOT in ID order, meant to read
    /// back whether UnionAll is a plain, order-preserving, side-then-side concatenation (the
    /// assumed behavior every prior "diverge-then-reconverge from one shared source" UnionAll
    /// fixture in this file was never positioned to actually test, since those always feed from
    /// ONE upstream source through a Conditional Split, never two independently-ordered sources).
    ///
    /// <para><b>NOT confirmed dtexec-executable -- a real, left-unresolved object-model quirk,
    /// documented rather than papered over, same acceptable tier as this file's own ADO NET
    /// connection-manager and Merge Join extra-output-column quirks.</b> Two independent
    /// construction recipes were tried, both producing a package that builds and saves cleanly
    /// via the object model but fails dtexec's own XML LOAD validation (before execution even
    /// starts) with "Error setting input object during XML load" / "Load error encountered near
    /// object with ID 60" -- confirmed to be dtexec's own internal load-position counter, not a
    /// real refId (grepping the saved XML for any "60"-bearing id finds nothing):
    /// <list type="bullet">
    /// <item>The all-at-once recipe <see cref="BuildConditionalSplitRemergeFixture"/>'s own
    /// UnionAll construction uses (second input via <c>InputCollection.New()</c> before either
    /// path is attached, both paths attached, ONE joint <c>ReinitializeMetaData()</c> call) --
    /// this uncovered a real, previously-undocumented fact along the way: attaching a path to an
    /// input makes UnionAll auto-provision a FRESH spare input the instant that happens, the
    /// exact same "auto-provisions a spare the instant a path attaches" behavior CLAUDE.md's
    /// Multicast section already documents for that component's OUTPUT side -- confirmed here
    /// for UnionAll's INPUT side instead (unlike Multicast's own harmless dangling spare output,
    /// a dangling zero-column spare INPUT was itself explicitly removable via
    /// <c>RemoveObjectByID</c>, and removing it DID reduce the saved XML from 3 inputs to the
    /// intended 2 -- but the load failure persisted identically regardless, so the spare was
    /// never the true cause).</item>
    /// <item>An INCREMENTAL recipe (attach + resolve the first input alone, then create the
    /// second input, attach + resolve again) -- the opposite ordering
    /// <see cref="BuildConditionalSplitRemergeFixture"/>'s own doc comment says was tried and
    /// REJECTED for an unrelated symptom on that fixture (a stale lineage ID on the SPLIT's own
    /// input) -- produced the byte-identical dtexec failure here too.</item>
    /// </list>
    /// Also confirmed: <c>IDTSInput100</c> does NOT support an <c>IDTSName100</c> cast (throws
    /// <c>InvalidCastException</c>, HRESULT <c>E_NOINTERFACE</c>) -- unlike a flat-file column or
    /// a Conditional Split output, an input's own <c>Name</c> is not independently settable via
    /// any interface probed here, so the second input persists unnamed (<c>Inputs[46]</c>-style
    /// refId rather than a human-readable one) regardless of which recipe is used.</para>
    ///
    /// <para>This does NOT block using the fixture for its actual purpose: <c>ssisx extract</c>/
    /// <c>generate</c> parse the saved XML directly (plan §2.1) and never invoke SSIS's own
    /// object model or its execution-time validation, so they read this fixture's genuinely
    /// two-independent-source UnionAll shape correctly regardless. UnionAll's real runtime row-
    /// order semantics are instead evidenced two other ways: (1) the component's own official
    /// Microsoft-authored description text, extracted directly from this live installation's own
    /// object model and visible verbatim in the saved XML's <c>description</c> attribute --
    /// "Combines rows from multiple data flows WITHOUT SORTING" -- a real fact pulled from the
    /// runtime, not documentation guesswork; (2) a plain C# unit test of the new
    /// <c>ConcatenatingRowSource&lt;TRow&gt;</c> proving its own sequential-concatenation
    /// algorithm correct with no SSIS involved at all, the same fallback tier
    /// <see cref="BuildMergeJoinFixture"/>'s own unresolved quirk already established for
    /// Merge Join's join algorithm.</para>
    ///
    /// Requires the backing tables from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-merge-interleave-probe-tables.sql</c>
    /// (reused verbatim -- same two source tables the Merge probe above uses, just read in a
    /// deliberately different, non-ID order this time) already created and seeded on
    /// <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>.
    /// </summary>
    private static int BuildUnionTwoSourcesFixture(string outputPath)
    {
        var outDir = Path.Combine(TestFixturesDir(), "synthetic-union-two-sources-output");
        Directory.CreateDirectory(outDir);
        var exportPath = Path.Combine(outDir, "union-export.csv");

        var pkg = new RtPackage { Name = "SyntheticUnionTwoSources" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var ffCm = AddFlatFileDestinationConnectionManager(pkg, "CM_FF_Export", exportPath,
            format: "Delimited", columnNamesInFirstDataRow: true,
            ("ID", 20, false), ("Name", 50, true));

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_UnionTwoSources";
        var pipe = (MainPipe)dftHost.InnerObject;

        // Deliberately NOT ordered by ID -- each side's own SELECT ... ORDER BY pins a known,
        // non-ID-ascending row sequence per side, so the readback can tell "concatenation
        // preserving each side's own order" apart from "something reorders within a side too."
        var leftSrcMeta = AddOleDbComponent(pipe, "OLE DB Source Left", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Name FROM dbo.SyntheticMergeProbeLeft ORDER BY ID DESC");
        ResolveOleDbMetadata(leftSrcMeta, isDestination: false);

        var rightSrcMeta = AddOleDbComponent(pipe, "OLE DB Source Right", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Name FROM dbo.SyntheticMergeProbeRight ORDER BY ID DESC");
        ResolveOleDbMetadata(rightSrcMeta, isDestination: false);

        var unionMeta = pipe.ComponentMetaDataCollection.New();
        unionMeta.ComponentClassID = "Microsoft.UnionAll";
        var unionInst = unionMeta.Instantiate();
        unionInst.ProvideComponentProperties(); // resets Name -- see AddOleDbComponent's own comment
        unionMeta.Name = "UNION_TwoSources";

        // Second input created BEFORE any path is attached -- see
        // BuildConditionalSplitRemergeFixture's own doc comment for why (InsertInput throws
        // 0xC020800E unconditionally for this component; InputCollection.New() is the only
        // recipe that works, and only while still in the just-constructed state). Both real
        // inputs' own IDs captured up front and re-fetched via GetObjectByID below -- COM RCWs
        // are not guaranteed stable across a ReinitializeMetaData() call (same lesson
        // BuildConditionalSplitRemergeFixture already learned the hard way).
        //
        // <b>A real, previously-undocumented object-model fact, confirmed empirically:</b>
        // attaching a path to an input (AttachPathAndPropagateNotifications) makes UnionAll
        // auto-provision a FRESH spare input the instant that happens -- the exact same
        // "auto-provisions a spare the instant a path attaches" behavior CLAUDE.md's Multicast
        // section already documents for THAT component's OUTPUT side, confirmed here for
        // UnionAll's INPUT side instead. ReinitializeMetaData() resolves the two REAL inputs
        // (both end up with real columns) but leaves exactly one dangling, zero-column spare
        // input behind. Unlike Multicast's own dangling spare OUTPUT (harmless -- an output
        // simply never fires), a dangling spare INPUT with zero columns fails dtexec's own XML
        // LOAD validation outright ("Error setting input object during XML load") -- so, unlike
        // Multicast, it must be explicitly removed via RemoveObjectByID before saving, not just
        // skipped downstream in codegen.
        var unionFirstInputId = unionMeta.InputCollection[0].ID;
        var leftOutput = leftSrcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);
        var rightOutput = rightSrcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);

        // Resolved INCREMENTALLY -- attach + resolve the first input alone, THEN create the
        // second input, attach + resolve again -- rather than creating both inputs and
        // attaching both paths before any ReinitializeMetaData() call (which left one dangling,
        // zero-column spare input behind; explicit RemoveObjectByID cleanup of that spare still
        // left the two REAL inputs in a shape dtexec's own XML loader rejected outright, so the
        // spare wasn't the true cause after all).
        AttachPath(pipe, leftOutput, unionMeta.InputCollection.GetObjectByID(unionFirstInputId));
        unionInst.AcquireConnections(null);
        unionInst.ReinitializeMetaData();
        unionInst.ReleaseConnections();

        var unionSecondInputId = unionMeta.InputCollection.New().ID;
        AttachPath(pipe, rightOutput, unionMeta.InputCollection.GetObjectByID(unionSecondInputId));
        unionInst.AcquireConnections(null);
        unionInst.ReinitializeMetaData();
        unionInst.ReleaseConnections();

        foreach (var dangling in unionMeta.InputCollection.Cast<IDTSInput100>()
                     .Where(i => i.ID != unionFirstInputId && i.ID != unionSecondInputId)
                     .ToList())
        {
            unionMeta.InputCollection.RemoveObjectByID(dangling.ID);
        }

        // InputColumnCollection stays empty until each input's own virtual columns are marked
        // used -- same rule as every other UnionAll/Merge/Sort input in this file.
        var unionFirstInput = unionMeta.InputCollection.GetObjectByID(unionFirstInputId);
        MarkInputColumnsUsed(unionInst, unionFirstInput, "ID", "Name");

        var unionSecondInput = unionMeta.InputCollection.GetObjectByID(unionSecondInputId);
        MarkInputColumnsUsed(unionInst, unionSecondInput, "ID", "Name");

        // The second input's own columns are NOT auto-mapped -- same rule as every other
        // UnionAll/Merge second input in this file.
        var unionOutput = unionMeta.OutputCollection[0];
        foreach (IDTSInputColumn100 inCol in unionSecondInput.InputColumnCollection)
        {
            var matchingOutputCol = unionOutput.OutputColumnCollection.Cast<IDTSOutputColumn100>()
                .FirstOrDefault(oc => oc.Name == inCol.Name)
                ?? throw new InvalidOperationException($"fixture build error: UnionAll has no output column named '{inCol.Name}' to map the second input's own column onto.");
            unionInst.SetInputColumnProperty(unionSecondInput.ID, inCol.ID, "OutputColumnLineageID", matchingOutputCol.LineageID);
        }

        var destMeta = pipe.ComponentMetaDataCollection.New();
        destMeta.ComponentClassID = "Microsoft.FlatFileDestination";
        var destInst = destMeta.Instantiate();
        destInst.ProvideComponentProperties();
        destMeta.Name = "Flat File Destination";
        destInst.SetComponentProperty("Overwrite", true);

        var destConn = destMeta.RuntimeConnectionCollection[0];
        destConn.ConnectionManagerID = ffCm.ID;
        destConn.ConnectionManager = DtsConvert.GetExtendedInterface(ffCm);

        AttachPath(pipe, unionOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// The same 2-source UnionAll -> Flat File Destination shape as
    /// <see cref="BuildUnionTwoSourcesFixture"/>, PLUS a wholly separate, unconnected OLE DB
    /// Source -> OLE DB Destination pair in the SAME Data Flow Task. Built 2026-09-06 to
    /// reproduce a real, previously-silent correctness bug found by a third independent review:
    /// PlanDataFlow's own MergeJoin/Union carve-out used to return PlanUnion's result
    /// unconditionally, before the generic "more than one source component" gate ever ran --
    /// so a genuinely-resolved 2-source Union said nothing at all about a THIRD, completely
    /// unrelated source elsewhere in the same pipeline, which vanished with no file and no gap.
    /// The extra pair reuses SyntheticMulticastAggregateSibling's own backing tables (Source ->
    /// TargetA) rather than declaring new ones.
    ///
    /// Backing tables: synthetic-union-two-sources-tables.sql (for the union side) plus
    /// synthetic-multicast-aggregate-sibling-tables.sql (for the extra, unrelated pair).
    /// </summary>
    private static int BuildUnionPlusExtraSourceFixture(string outputPath)
    {
        var outDir = Path.Combine(TestFixturesDir(), "synthetic-union-plus-extra-source-output");
        Directory.CreateDirectory(outDir);
        var exportPath = Path.Combine(outDir, "union-export.csv");

        var pkg = new RtPackage { Name = "SyntheticUnionPlusExtraSource" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var ffCm = AddFlatFileDestinationConnectionManager(pkg, "CM_FF_Export", exportPath,
            format: "Delimited", columnNamesInFirstDataRow: true,
            ("ID", 20, false), ("Name", 50, true));

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_UnionPlusExtraSource";
        var pipe = (MainPipe)dftHost.InnerObject;

        var leftSrcMeta = AddOleDbComponent(pipe, "OLE DB Source Left", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Name FROM dbo.SyntheticMergeProbeLeft ORDER BY ID DESC");
        ResolveOleDbMetadata(leftSrcMeta, isDestination: false);

        var rightSrcMeta = AddOleDbComponent(pipe, "OLE DB Source Right", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Name FROM dbo.SyntheticMergeProbeRight ORDER BY ID DESC");
        ResolveOleDbMetadata(rightSrcMeta, isDestination: false);

        var unionMeta = pipe.ComponentMetaDataCollection.New();
        unionMeta.ComponentClassID = "Microsoft.UnionAll";
        var unionInst = unionMeta.Instantiate();
        unionInst.ProvideComponentProperties();
        unionMeta.Name = "UNION_TwoSources";

        var unionFirstInputId = unionMeta.InputCollection[0].ID;
        var leftOutput = leftSrcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);
        var rightOutput = rightSrcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);

        AttachPath(pipe, leftOutput, unionMeta.InputCollection.GetObjectByID(unionFirstInputId));
        unionInst.AcquireConnections(null);
        unionInst.ReinitializeMetaData();
        unionInst.ReleaseConnections();

        var unionSecondInputId = unionMeta.InputCollection.New().ID;
        AttachPath(pipe, rightOutput, unionMeta.InputCollection.GetObjectByID(unionSecondInputId));
        unionInst.AcquireConnections(null);
        unionInst.ReinitializeMetaData();
        unionInst.ReleaseConnections();

        foreach (var dangling in unionMeta.InputCollection.Cast<IDTSInput100>()
                     .Where(i => i.ID != unionFirstInputId && i.ID != unionSecondInputId)
                     .ToList())
        {
            unionMeta.InputCollection.RemoveObjectByID(dangling.ID);
        }

        var unionFirstInput = unionMeta.InputCollection.GetObjectByID(unionFirstInputId);
        MarkInputColumnsUsed(unionInst, unionFirstInput, "ID", "Name");

        var unionSecondInput = unionMeta.InputCollection.GetObjectByID(unionSecondInputId);
        MarkInputColumnsUsed(unionInst, unionSecondInput, "ID", "Name");

        var unionOutput = unionMeta.OutputCollection[0];
        foreach (IDTSInputColumn100 inCol in unionSecondInput.InputColumnCollection)
        {
            var matchingOutputCol = unionOutput.OutputColumnCollection.Cast<IDTSOutputColumn100>()
                .FirstOrDefault(oc => oc.Name == inCol.Name)
                ?? throw new InvalidOperationException($"fixture build error: UnionAll has no output column named '{inCol.Name}' to map the second input's own column onto.");
            unionInst.SetInputColumnProperty(unionSecondInput.ID, inCol.ID, "OutputColumnLineageID", matchingOutputCol.LineageID);
        }

        var destMeta = pipe.ComponentMetaDataCollection.New();
        destMeta.ComponentClassID = "Microsoft.FlatFileDestination";
        var destInst = destMeta.Instantiate();
        destInst.ProvideComponentProperties();
        destMeta.Name = "Flat File Destination";
        destInst.SetComponentProperty("Overwrite", true);

        var destConn = destMeta.RuntimeConnectionCollection[0];
        destConn.ConnectionManagerID = ffCm.ID;
        destConn.ConnectionManager = DtsConvert.GetExtendedInterface(ffCm);

        AttachPath(pipe, unionOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        // --- The extra, wholly UNRELATED source/destination pair -- never connected to the
        // Union above at all. Reuses SyntheticMulticastAggregateSibling's own backing tables. ---
        var extraSrcMeta = AddOleDbComponent(pipe, "OLE DB Source Extra", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null,
            sqlCommand: "SELECT ID, Region, CustomerID FROM dbo.SyntheticMulticastAggSiblingSource");
        ResolveOleDbMetadata(extraSrcMeta, isDestination: false);
        var extraOutput = extraSrcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);

        var extraDestMeta = AddOleDbComponent(pipe, "OLE DB Destination Extra", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticMulticastAggSiblingTargetA]");
        AttachPath(pipe, extraOutput, extraDestMeta.InputCollection[0]);
        ResolveOleDbMetadata(extraDestMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>Marks exactly the named columns as used (UT_READONLY) on one input --
    /// InputColumnCollection stays empty until this is called, same rule as
    /// <see cref="AddSortByKey"/>'s own input.</summary>
    private static void MarkInputColumnsUsed(IDTSDesigntimeComponent100 inst, IDTSInput100 input, params string[] names)
    {
        var virtualInput = input.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in virtualInput.VirtualInputColumnCollection)
        {
            if (names.Contains(vcol.Name))
                inst.SetUsageType(input.ID, virtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }
    }

    /// <summary>
    /// OLE DB Source (SqlCommand mode, 3 string columns matching RBC_Demo_ETL's own
    /// DFT_ExportDelimited/DFT_ExportFixedWidth shape: CustomerID/FullName/CleanEmail) -> Flat
    /// File Destination, twice -- once Delimited (with a ColumnNamesInFirstDataRow header, the
    /// one real evidenced instance that sets it), once FixedWidth (with the real evidenced
    /// synthetic trailing "RowEnd" delimited column carrying the CRLF row terminator, fed by a
    /// literal <c>'' AS RowEnd</c> in the SELECT -- exactly how the real package's own author
    /// worked around the destination's required row-terminator column). FullName is
    /// deliberately seeded with one value LONGER than the fixed-width column's own width (see
    /// the backing SQL script), to prove real truncation/padding behavior, not an assumption.
    ///
    /// Object-model facts confirmed empirically before writing this (same "ask the runtime,
    /// don't guess" discipline as trap 12): <c>IDTSConnectionManagerFlatFileColumn100</c> has a
    /// real <c>ColumnWidth</c> property distinct from <c>MaximumWidth</c> (both set here, both
    /// evidenced true on the real package's own FixedWidth connection manager) -- reflected off
    /// this GAC's DTSRuntimeWrap 16.0.0.0 first, not assumed from the earlier Delimited-only
    /// helper's own signature.
    ///
    /// Requires the backing table from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-flat-file-destination-tables.sql</c>
    /// already created and seeded on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>, and the two output
    /// paths' own directory to already exist on disk (the connection managers' own
    /// ConnectionString values -- Overwrite=true means dtexec creates the files themselves, but
    /// not their containing folder).
    /// </summary>
    private static int BuildFlatFileDestinationFixture(string outputPath)
    {
        var outDir = Path.Combine(TestFixturesDir(), "synthetic-flat-file-destination-output");
        Directory.CreateDirectory(outDir);
        var delimitedPath = Path.Combine(outDir, "customers-export.csv");
        var fixedWidthPath = Path.Combine(outDir, "customers-fixed.txt");

        var pkg = new RtPackage { Name = "SyntheticFlatFileDestination" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var delimitedCm = AddFlatFileDestinationConnectionManager(pkg, "CM_FF_ExportDelimited", delimitedPath,
            format: "Delimited", columnNamesInFirstDataRow: true,
            ("CustomerID", 20, false), ("FullName", 200, false), ("CleanEmail", 200, true));

        var fixedWidthCm = AddFlatFileDestinationConnectionManager(pkg, "CM_FF_ExportFixed", fixedWidthPath,
            format: "FixedWidth", columnNamesInFirstDataRow: false,
            ("CustomerID", 10, false), ("FullName", 15, false), ("CleanEmail", 20, false), ("RowEnd", 1, true));

        var delimitedDftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        delimitedDftHost.Name = "DFT_ExportDelimited";
        BuildOleDbToFlatFileExport((MainPipe)delimitedDftHost.InnerObject, sqlCm, delimitedCm,
            "SELECT CustomerID, FullName, CleanEmail FROM dbo.SyntheticFlatFileDestinationInput");

        var fixedWidthDftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        fixedWidthDftHost.Name = "DFT_ExportFixedWidth";
        BuildOleDbToFlatFileExport((MainPipe)fixedWidthDftHost.InnerObject, sqlCm, fixedWidthCm,
            "SELECT CustomerID, FullName, CleanEmail, '' AS RowEnd FROM dbo.SyntheticFlatFileDestinationInput");

        // A third, ordinary OLE DB Source -> Derived Column -> OLE DB Destination flow -- matches
        // RBC_Demo_ETL's own Package_Exports.dtsx real shape (which ALSO has a SQL destination,
        // DFT_AdoNetRoundTrip's ADO_DST_ExportLog), and is what gives the generated package a
        // real SQL table for AddEtlDbContext<T>()/IUnitOfWork's transaction to wrap -- a package
        // whose ONLY flows are Flat File Destinations is a separate, unevidenced shape this
        // fixture is deliberately not proving.
        var logDftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        logDftHost.Name = "DFT_Log";
        var logPipe = (MainPipe)logDftHost.InnerObject;
        var logSrcMeta = AddOleDbComponent(logPipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Note FROM dbo.SyntheticFlatFileDestinationLogSource");
        ResolveOleDbMetadata(logSrcMeta, isDestination: false);
        var logMainOutput = logSrcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);
        var logDerivedMeta = BuildDerivedColumnLoadedAtUtc(logPipe, logMainOutput);
        var logDestMeta = AddOleDbComponent(logPipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticFlatFileDestinationLog]");
        AttachPath(logPipe, logDerivedMeta.OutputCollection[0], logDestMeta.InputCollection[0]);
        ResolveOleDbMetadata(logDestMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>OLE DB Source (SqlCommand mode) -> Flat File Destination directly -- a deliberate
    /// DIRECT COPY (no Derived Column), matching the real evidenced DFT_ExportDelimited/
    /// DFT_ExportFixedWidth exactly, which is the whole point of this fixture (proving codegen's
    /// Flat File Destination exemption from the "no Derived Column" gate reaches a real,
    /// compiling, RUNNABLE Program.cs).</summary>
    private static void BuildOleDbToFlatFileExport(MainPipe pipe, ConnectionManager sqlCm, ConnectionManager flatFileCm, string sqlCommand)
    {
        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: sqlCommand);
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var destMeta = pipe.ComponentMetaDataCollection.New();
        destMeta.ComponentClassID = "Microsoft.FlatFileDestination";
        var destInst = destMeta.Instantiate();
        destInst.ProvideComponentProperties(); // resets Name -- see AddOleDbComponent's own comment
        destMeta.Name = "Flat File Destination";
        destInst.SetComponentProperty("Overwrite", true);

        var destConn = destMeta.RuntimeConnectionCollection[0];
        destConn.ConnectionManagerID = flatFileCm.ID;
        destConn.ConnectionManager = DtsConvert.GetExtendedInterface(flatFileCm);

        var mainOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);
        AttachPath(pipe, mainOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);
    }

    private static ConnectionManager AddFlatFileDestinationConnectionManager(
        RtPackage pkg, string name, string filePath, string format, bool columnNamesInFirstDataRow,
        params (string ColumnName, int Width, bool IsLast)[] columns)
    {
        var cm = pkg.Connections.Add("FLATFILE");
        cm.Name = name;
        cm.ConnectionString = filePath;

        var ff = (IDTSConnectionManagerFlatFile100)cm.InnerObject;
        ff.Format = format;
        ff.ColumnNamesInFirstDataRow = columnNamesInFirstDataRow;
        ff.HeaderRowDelimiter = "\r\n";
        ff.CodePage = 1252;

        foreach (var (columnName, width, isLast) in columns)
        {
            var col = ff.Columns.Add();
            col.DataType = RtDataType.DT_WSTR;
            col.DataPrecision = 0;
            col.DataScale = 0;
            if (format == "FixedWidth" && !isLast)
            {
                // A fixed-width data column: no delimiter of its own, laid out purely by width.
                col.ColumnType = "FixedWidth";
                col.ColumnDelimiter = "";
                col.ColumnWidth = width;
                col.MaximumWidth = width;
            }
            else
            {
                // Every Delimited-format column, AND FixedWidth's own synthetic trailing
                // row-terminator column (see this method's caller for the real-evidenced
                // "RowEnd" shape) -- delimited, no fixed ColumnWidth.
                col.ColumnType = "Delimited";
                col.ColumnDelimiter = isLast ? "\r\n" : ",";
                col.MaximumWidth = width;
            }

            ((IDTSName100)col).Name = columnName;
        }

        return cm;
    }

    /// <summary>
    /// OLE DB Source (<c>SELECT CustomerID FROM dbo.SyntheticOleDbCommandTarget</c>) -> OLE DB
    /// Command (<c>UPDATE dbo.SyntheticOleDbCommandTarget SET Flagged = 1 WHERE CustomerID = ?</c>,
    /// one parameter) -- NO destination component at all, proving the "the command itself is the
    /// flow's sink" shape end to end, the exact real evidence from
    /// SSIS_From_Sandeep's own Package_Advanced.dtsx (DFT_FlagCustomers). Added 2026-08-28.
    ///
    /// Requires the backing table from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-oledb-command-tables.sql</c> already created
    /// on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>.
    /// </summary>
    private static int BuildOleDbCommandFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticOleDbCommand" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_FlagCustomers";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT CustomerID FROM dbo.SyntheticOleDbCommandTarget");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var cmdMeta = pipe.ComponentMetaDataCollection.New();
        cmdMeta.ComponentClassID = "Microsoft.OLEDBCommand";
        var cmdInst = cmdMeta.Instantiate();
        cmdInst.ProvideComponentProperties(); // resets Name -- must set Name after this, see AddOleDbComponent's comment
        cmdMeta.Name = "OLECMD_SetFlag";

        var cmdConn = cmdMeta.RuntimeConnectionCollection[0];
        cmdConn.ConnectionManagerID = sqlCm.ID;
        cmdConn.ConnectionManager = DtsConvert.GetExtendedInterface(sqlCm);

        cmdInst.SetComponentProperty("SqlCommand", "UPDATE dbo.SyntheticOleDbCommandTarget SET Flagged = 1 WHERE CustomerID = ?");

        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        AttachPath(pipe, srcOutput, cmdMeta.InputCollection[0]);

        cmdInst.AcquireConnections(null);
        cmdInst.ReinitializeMetaData();
        cmdInst.ReleaseConnections();

        var cmdInput = cmdMeta.InputCollection[0];
        var externalParam = cmdInput.ExternalMetadataColumnCollection.Cast<IDTSExternalMetadataColumn100>().Single();

        var virtualInput = cmdInput.GetVirtualInput();
        var vcol = virtualInput.VirtualInputColumnCollection.Cast<IDTSVirtualInputColumn100>().First(v => v.Name == "CustomerID");
        cmdInst.SetUsageType(cmdInput.ID, virtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        var matchColumn = cmdInput.InputColumnCollection.Cast<IDTSInputColumn100>().First(c => c.Name == "CustomerID");
        cmdInst.MapInputColumn(cmdInput.ID, matchColumn.ID, externalParam.ID);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// OLE DB Source -> Aggregate -> OLE DB Command, no destination anywhere -- built 2026-09-06
    /// to reproduce a real, previously-only-incidentally-safe shape found by a third independent
    /// review: flow.Aggregate and flow.OleDbCommand used to be able to coexist on one
    /// DataFlowPlan with no explicit guard (PlanDataFlow's OLE DB Command carve-out checked
    /// conditionalSplit/multicast but not aggregate), and PackageGenerator's dispatch checks
    /// Aggregate before OleDbCommand -- so this shape used to reach GenerateAggregateFlow with a
    /// stray flow.OleDbCommand it never reads. It happened to fail safely only because
    /// DestinationInfo.IsFastLoadConfigured always returns false for an OLE DB Command component
    /// (it has no destination side at all), not because anything deliberately refused it. Now
    /// PlanDataFlow's own `aggregate is null` guard makes this an explicit, named gap instead.
    /// </summary>
    private static int BuildAggregateThenOleDbCommandFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticAggregateThenOleDbCommand" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_AggregateThenOleDbCommand";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT Region, CustomerID FROM dbo.SyntheticAggregateSource");
        ResolveOleDbMetadata(srcMeta, isDestination: false);
        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);

        var aggMeta = pipe.ComponentMetaDataCollection.New();
        aggMeta.ComponentClassID = "Microsoft.Aggregate";
        var aggInst = aggMeta.Instantiate();
        aggInst.ProvideComponentProperties();
        aggMeta.Name = "AGG_ByRegion";

        AttachPath(pipe, srcOutput, aggMeta.InputCollection[0]);
        aggInst.AcquireConnections(null);
        aggInst.ReinitializeMetaData();
        aggInst.ReleaseConnections();

        var aggInput = aggMeta.InputCollection[0];
        var aggVirtualInput = aggInput.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in aggVirtualInput.VirtualInputColumnCollection)
            aggInst.SetUsageType(aggInput.ID, aggVirtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);

        var regionInputCol = aggInput.InputColumnCollection.Cast<IDTSInputColumn100>().First(c => c.Name == "Region");
        var customerIdInputCol = aggInput.InputColumnCollection.Cast<IDTSInputColumn100>().First(c => c.Name == "CustomerID");

        var aggOutput = aggMeta.OutputCollection[0];

        var regionOutCol = aggInst.InsertOutputColumnAt(aggOutput.ID, 0, "Region", "");
        aggInst.SetOutputColumnProperty(aggOutput.ID, regionOutCol.ID, "AggregationColumnId", regionInputCol.LineageID);
        aggInst.SetOutputColumnProperty(aggOutput.ID, regionOutCol.ID, "AggregationType", 0); // GroupBy

        var countOutCol = aggInst.InsertOutputColumnAt(aggOutput.ID, 1, "CustomerCount", "");
        aggInst.SetOutputColumnProperty(aggOutput.ID, countOutCol.ID, "AggregationColumnId", customerIdInputCol.LineageID);
        aggInst.SetOutputColumnProperty(aggOutput.ID, countOutCol.ID, "AggregationType", 1); // Count

        var cmdMeta = pipe.ComponentMetaDataCollection.New();
        cmdMeta.ComponentClassID = "Microsoft.OLEDBCommand";
        var cmdInst = cmdMeta.Instantiate();
        cmdInst.ProvideComponentProperties();
        cmdMeta.Name = "OLECMD_LogRegionCount";

        var cmdConn = cmdMeta.RuntimeConnectionCollection[0];
        cmdConn.ConnectionManagerID = sqlCm.ID;
        cmdConn.ConnectionManager = DtsConvert.GetExtendedInterface(sqlCm);
        cmdInst.SetComponentProperty("SqlCommand", "UPDATE dbo.SyntheticAggregateTarget SET CustomerCount = ? WHERE Region = ?");

        AttachPath(pipe, aggOutput, cmdMeta.InputCollection[0]);
        cmdInst.AcquireConnections(null);
        cmdInst.ReinitializeMetaData();
        cmdInst.ReleaseConnections();

        var cmdInput = cmdMeta.InputCollection[0];
        var cmdVirtualInput = cmdInput.GetVirtualInput();
        var countVcol = cmdVirtualInput.VirtualInputColumnCollection.Cast<IDTSVirtualInputColumn100>().First(v => v.Name == "CustomerCount");
        cmdInst.SetUsageType(cmdInput.ID, cmdVirtualInput, countVcol.LineageID, DTSUsageType.UT_READONLY);
        var regionVcol = cmdVirtualInput.VirtualInputColumnCollection.Cast<IDTSVirtualInputColumn100>().First(v => v.Name == "Region");
        cmdInst.SetUsageType(cmdInput.ID, cmdVirtualInput, regionVcol.LineageID, DTSUsageType.UT_READONLY);

        var cmdParams = cmdInput.ExternalMetadataColumnCollection.Cast<IDTSExternalMetadataColumn100>().ToList();
        var countMatchCol = cmdInput.InputColumnCollection.Cast<IDTSInputColumn100>().First(c => c.Name == "CustomerCount");
        var regionMatchCol = cmdInput.InputColumnCollection.Cast<IDTSInputColumn100>().First(c => c.Name == "Region");
        cmdInst.MapInputColumn(cmdInput.ID, countMatchCol.ID, cmdParams[0].ID);
        cmdInst.MapInputColumn(cmdInput.ID, regionMatchCol.ID, cmdParams[1].ID);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// OLE DB Source -> OLE DB Command, TWO parameters, deliberately attached (SetUsageType
    /// order) in the OPPOSITE order from how they're bound (MapInputColumn) to the SQL's own
    /// '?' placeholders -- gap-audit Phase 3.1's motivating shape. A live object-model probe
    /// (2026-09-02, not checked in -- see <c>OleDbCommandPayload</c>'s own doc comment for the
    /// findings) proved real SSIS binds each parameter via the input column's own
    /// <c>externalMetadataColumnId</c> (a <c>Param_N</c> reference), independent of
    /// &lt;inputColumns&gt; declaration order, and that <c>ParameterMapping</c> itself is never
    /// populated at all. <c>StatusCode</c> is seeded at 0 and the source computes
    /// <c>NewStatus = CustomerID + 100</c> (101-103, a range that can never equal a real
    /// CustomerID 1-3) specifically so a declaration-order regression is unambiguously
    /// observable: it would bind the WHERE clause to a NewStatus value, matching zero rows, so
    /// StatusCode would stay 0 for every row instead of becoming CustomerID + 100.
    ///
    /// Requires the backing table from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-oledb-command-reordered-tables.sql</c>
    /// already created on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>.
    /// </summary>
    private static int BuildOleDbCommandReorderedFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticOleDbCommandReordered" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_ReorderedParams";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null,
            sqlCommand: "SELECT CustomerID, CustomerID + 100 AS NewStatus FROM dbo.SyntheticOleDbCommandReorderedTarget");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var cmdMeta = pipe.ComponentMetaDataCollection.New();
        cmdMeta.ComponentClassID = "Microsoft.OLEDBCommand";
        var cmdInst = cmdMeta.Instantiate();
        cmdInst.ProvideComponentProperties(); // resets Name -- must set Name after this, see AddOleDbComponent's comment
        cmdMeta.Name = "OLECMD_SetStatus";

        var cmdConn = cmdMeta.RuntimeConnectionCollection[0];
        cmdConn.ConnectionManagerID = sqlCm.ID;
        cmdConn.ConnectionManager = DtsConvert.GetExtendedInterface(sqlCm);

        // Placeholder 0 = the new StatusCode value (NewStatus), placeholder 1 = the WHERE key
        // (CustomerID).
        cmdInst.SetComponentProperty("SqlCommand",
            "UPDATE dbo.SyntheticOleDbCommandReorderedTarget SET StatusCode = ? WHERE CustomerID = ?");

        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        AttachPath(pipe, srcOutput, cmdMeta.InputCollection[0]);

        cmdInst.AcquireConnections(null);
        cmdInst.ReinitializeMetaData();
        cmdInst.ReleaseConnections();

        var cmdInput = cmdMeta.InputCollection[0];
        var param0 = cmdInput.ExternalMetadataColumnCollection.Cast<IDTSExternalMetadataColumn100>().Single(e => e.Name == "Param_0");
        var param1 = cmdInput.ExternalMetadataColumnCollection.Cast<IDTSExternalMetadataColumn100>().Single(e => e.Name == "Param_1");

        var virtualInput = cmdInput.GetVirtualInput();
        // Attach in DECLARATION order CustomerID, NewStatus -- the OPPOSITE of the SQL's own
        // placeholder order (NewStatus is placeholder 0, CustomerID is placeholder 1).
        var vCustomerId = virtualInput.VirtualInputColumnCollection.Cast<IDTSVirtualInputColumn100>().First(v => v.Name == "CustomerID");
        cmdInst.SetUsageType(cmdInput.ID, virtualInput, vCustomerId.LineageID, DTSUsageType.UT_READONLY);
        var vNewStatus = virtualInput.VirtualInputColumnCollection.Cast<IDTSVirtualInputColumn100>().First(v => v.Name == "NewStatus");
        cmdInst.SetUsageType(cmdInput.ID, virtualInput, vNewStatus.LineageID, DTSUsageType.UT_READONLY);

        // Bind to the TRUE placeholder position, opposite of declaration order: CustomerID
        // (declared first) -> Param_1 (the SECOND '?'), NewStatus (declared second) -> Param_0
        // (the FIRST '?').
        var customerIdCol = cmdInput.InputColumnCollection.Cast<IDTSInputColumn100>().First(c => c.Name == "CustomerID");
        var newStatusCol = cmdInput.InputColumnCollection.Cast<IDTSInputColumn100>().First(c => c.Name == "NewStatus");
        cmdInst.MapInputColumn(cmdInput.ID, customerIdCol.ID, param1.ID);
        cmdInst.MapInputColumn(cmdInput.ID, newStatusCol.ID, param0.ID);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// OLE DB Source -> OLE DB Command calling a real stored procedure via EXEC, TWO parameters,
    /// deliberately attached (SetUsageType order) in the OPPOSITE order from the procedure's own
    /// declared parameters -- reproduces the REAL evidenced shape from SSIS_From_Sandeep's own
    /// Package_Advanced.dtsx (DFT_FlagCustomers, EXEC dbo.usp_SetCustomerFlag ?, N'flagged'),
    /// whose bound external columns are named after the procedure's own parameters
    /// ("@CustomerID") -- NOT "Param_N" the way a bare parameterized UPDATE/SELECT names them
    /// (see SyntheticOleDbCommandReordered.dtsx). A live object-model probe (2026-09-02) proved
    /// codegen must resolve true binding order from each bound external column's own INDEX
    /// within the input's &lt;externalMetadataColumns&gt; list -- which stays call-order-correct
    /// regardless of whether the name itself encodes a position -- not from parsing the name.
    ///
    /// Requires the backing table/procedure from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-oledb-command-exec-named-tables.sql</c>
    /// already created on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>.
    /// </summary>
    private static int BuildOleDbCommandExecNamedFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticOleDbCommandExecNamed" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_ExecNamedParams";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null,
            sqlCommand: "SELECT CustomerID, CustomerID + 100 AS NewStatus FROM dbo.SyntheticOleDbCommandExecNamedTarget");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var cmdMeta = pipe.ComponentMetaDataCollection.New();
        cmdMeta.ComponentClassID = "Microsoft.OLEDBCommand";
        var cmdInst = cmdMeta.Instantiate();
        cmdInst.ProvideComponentProperties(); // resets Name -- must set Name after this, see AddOleDbComponent's comment
        cmdMeta.Name = "OLECMD_SetStatus";

        var cmdConn = cmdMeta.RuntimeConnectionCollection[0];
        cmdConn.ConnectionManagerID = sqlCm.ID;
        cmdConn.ConnectionManager = DtsConvert.GetExtendedInterface(sqlCm);

        // dbo.usp_SyntheticSetStatus(@CustomerID INT, @NewStatus INT) -- procedure's own
        // declared order: @CustomerID first, @NewStatus second.
        cmdInst.SetComponentProperty("SqlCommand", "EXEC dbo.usp_SyntheticSetStatus ?, ?");

        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        AttachPath(pipe, srcOutput, cmdMeta.InputCollection[0]);

        cmdInst.AcquireConnections(null);
        cmdInst.ReinitializeMetaData();
        cmdInst.ReleaseConnections();

        var cmdInput = cmdMeta.InputCollection[0];
        var externalCols = cmdInput.ExternalMetadataColumnCollection.Cast<IDTSExternalMetadataColumn100>().ToList();
        var customerIdExt = externalCols.First(e => e.Name.IndexOf("CustomerID", StringComparison.OrdinalIgnoreCase) >= 0);
        var newStatusExt = externalCols.First(e => e.Name.IndexOf("NewStatus", StringComparison.OrdinalIgnoreCase) >= 0);

        var virtualInput = cmdInput.GetVirtualInput();
        // Attach in the OPPOSITE order from the procedure's own declared parameters: NewStatus
        // first, CustomerID second.
        var vNewStatus = virtualInput.VirtualInputColumnCollection.Cast<IDTSVirtualInputColumn100>().First(v => v.Name == "NewStatus");
        cmdInst.SetUsageType(cmdInput.ID, virtualInput, vNewStatus.LineageID, DTSUsageType.UT_READONLY);
        var vCustomerId = virtualInput.VirtualInputColumnCollection.Cast<IDTSVirtualInputColumn100>().First(v => v.Name == "CustomerID");
        cmdInst.SetUsageType(cmdInput.ID, virtualInput, vCustomerId.LineageID, DTSUsageType.UT_READONLY);

        // Bind by the TRUE parameter identity, not declaration order.
        var customerIdCol = cmdInput.InputColumnCollection.Cast<IDTSInputColumn100>().First(c => c.Name == "CustomerID");
        var newStatusCol = cmdInput.InputColumnCollection.Cast<IDTSInputColumn100>().First(c => c.Name == "NewStatus");
        cmdInst.MapInputColumn(cmdInput.ID, customerIdCol.ID, customerIdExt.ID);
        cmdInst.MapInputColumn(cmdInput.ID, newStatusCol.ID, newStatusExt.ID);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// OLE DB Source -> Multicast -> {OLE DB Destination, Flat File Destination} -- fans EVERY
    /// row unconditionally to BOTH a table load and a flat file audit copy, the exact real
    /// evidenced shape from SSIS_From_Sandeep's own Package_Legacy.dtsx (DFT_FixedWidthImport,
    /// Multicast -> {FFDST_AuditTrail_APPEND, OLE DB Destination}). Added 2026-08-28.
    ///
    /// Requires the backing table from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-multicast-tables.sql</c> already created on
    /// <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>.
    /// </summary>
    private static int BuildMulticastFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticMulticast" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var outputDir = Path.Combine(TestFixturesDir(), "synthetic-multicast-output");
        Directory.CreateDirectory(outputDir);
        var outputCsvPath = Path.Combine(outputDir, "audit-trail.csv");
        var fileCm = AddFlatFileDestinationConnectionManager(pkg, "CM_Audit", outputCsvPath,
            format: "Delimited", columnNamesInFirstDataRow: true,
            ("ID", 20, false), ("Name", 50, true));

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_FixedWidthImport";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Name FROM dbo.SyntheticMulticastSource");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var mcMeta = pipe.ComponentMetaDataCollection.New();
        mcMeta.ComponentClassID = "Microsoft.Multicast";
        var mcInst = mcMeta.Instantiate();
        mcInst.ProvideComponentProperties(); // resets Name -- must set Name after this, see AddOleDbComponent's comment
        mcMeta.Name = "MC_Fanout";

        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        AttachPath(pipe, srcOutput, mcMeta.InputCollection[0]);

        mcInst.AcquireConnections(null);
        mcInst.ReinitializeMetaData();
        mcInst.ReleaseConnections();

        // Multicast auto-provisions a fresh "spare" output the instant an existing one gets a
        // path attached -- confirmed empirically via a debug probe (NOT guessed): exactly ONE
        // output exists right after ReinitializeMetaData; attaching a path to it immediately
        // grows the collection to two; attaching a path to THAT new one grows it to three, the
        // last one left permanently unconnected. This exactly matches the real evidenced XML
        // (SSIS_From_Sandeep's own MCAST_FixedRows: two real, path-connected outputs plus a
        // third with dangling="true") -- so no InsertOutput call is needed or wanted here, unlike
        // Conditional Split's own recipe (which this method originally, incorrectly, copied).
        var firstOutput = mcMeta.OutputCollection.Cast<IDTSOutput100>().Single();

        var oleDbDestMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticMulticastTarget]");
        AttachPath(pipe, firstOutput, oleDbDestMeta.InputCollection[0]);
        ResolveOleDbMetadata(oleDbDestMeta, isDestination: true);

        var secondOutput = mcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => o.ID != firstOutput.ID);

        var flatFileDestMeta = pipe.ComponentMetaDataCollection.New();
        flatFileDestMeta.ComponentClassID = "Microsoft.FlatFileDestination";
        var ffInst = flatFileDestMeta.Instantiate();
        ffInst.ProvideComponentProperties();
        flatFileDestMeta.Name = "FFDST_AuditTrail_APPEND";
        var ffConn = flatFileDestMeta.RuntimeConnectionCollection[0];
        ffConn.ConnectionManagerID = fileCm.ID;
        ffConn.ConnectionManager = DtsConvert.GetExtendedInterface(fileCm);
        ffInst.SetComponentProperty("Overwrite", false);
        AttachPath(pipe, secondOutput, flatFileDestMeta.InputCollection[0]);
        ResolveOleDbMetadata(flatFileDestMeta, isDestination: true);
        // A third output now exists, unconnected/"dangling" -- left as-is, matching the real
        // evidenced shape exactly. Ssis.Extract.Codegen.PackagePlanner.PlanMulticast must skip it.

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// OLE DB Source (SqlCommand, a float "Val" column) -> OLE DB Destination (an int "Val"
    /// column), a plain passthrough with NO Data Conversion and NO Derived Column in between --
    /// reproduces, in isolation, the exact real shape RBC_Demo_ETL's own DFT_ExcelImport has
    /// (a source buffer type that doesn't match the destination's external metadata type on a
    /// column SSIS still lets you map directly). Built as a real dtexec PROBE first, added
    /// 2026-08-28: measures what SSIS's own OLE DB Destination actually does with the mismatch
    /// (round vs. truncate, which midpoint rule) before TransformEmitter's own "numeric
    /// coercion on a plain passthrough column is not generated yet" gap is closed with real
    /// code -- see <c>synthetic-numeric-coercion-tables.sql</c>'s own header for the full
    /// rationale and why an overflow-inducing value is deliberately excluded here.
    ///
    /// Requires the backing tables from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-numeric-coercion-tables.sql</c> already
    /// created on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>.
    /// </summary>
    private static int BuildNumericCoercionFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticNumericCoercion" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Coerce";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Val FROM dbo.SyntheticNumericCoercionInput");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticNumericCoercionTarget]");
        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        AttachPath(pipe, srcOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// OLE DB Source (SqlCommand, an NVARCHAR "Val" column) -> OLE DB Destination (an int "Val"
    /// column), a plain passthrough with NO Data Conversion and NO Derived Column in between --
    /// reproduces, in isolation, the exact real shape RBC_Demo_ETL's own Package_Exports/
    /// DFT_AdoNetRoundTrip has (CustomerID: wstr,20 from an ADO NET Source reading
    /// dbo.StagingCustomers, i4 at CustomerExportLog). Uses OLE DB rather than the real ADO NET
    /// component because this environment cannot construct an ADO.NET connection manager
    /// through the SSIS object model (see CLAUDE.md's "ADO NET Source/Destination" section) --
    /// the coercion this measures is source-agnostic, keyed only off the buffered type (wstr)
    /// vs. the destination's external metadata type (i4), same as the r8-to-i4 round.
    ///
    /// Requires the backing tables from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-string-to-int-coercion-tables.sql</c>
    /// already created on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>.
    /// </summary>
    private static int BuildStringToIntCoercionFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticStringToIntCoercion" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Coerce";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, Val FROM dbo.SyntheticStringToIntCoercionInput");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticStringToIntCoercionTarget]");
        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        AttachPath(pipe, srcOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// OLE DB Source (SqlCommand: ID, R8Text, WstrText, BoolText) -> Data Conversion (R8Text ->
    /// ValR8 DT_R8; WstrText -> ValWstr DT_WSTR,10; BoolText -> ValBool DT_BOOL, all dispositions
    /// IgnoreFailure) -> OLE DB Destination. A real dtexec PROBE, built with the user's explicit
    /// sign-off to measure IgnoreFailure semantics SPECULATIVELY -- unlike every other Data
    /// Conversion fixture in this file, no real package in SSIS_From_Sandeep's own portfolio
    /// currently needs any of these three target types (every real Microsoft.DataConvert
    /// instance there targets only DT_I4/DT_DBDATE, both already supported). Measures a genuinely
    /// NEW failure mode DT_I4/DT_DBDATE never exercised: DT_WSTR's own TRUNCATION disposition
    /// (does an overlong source string truncate to fit, or null out, under IgnoreFailure?).
    ///
    /// Requires the backing tables from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-data-conversion-types-tables.sql</c> already
    /// created on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>.
    /// </summary>
    private static int BuildDataConversionTypesFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticDataConversionTypes" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Coerce";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, R8Text, WstrText, BoolText FROM dbo.SyntheticDataConversionTypesInput");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);

        var dconvMeta = pipe.ComponentMetaDataCollection.New();
        dconvMeta.ComponentClassID = "Microsoft.DataConvert";
        var dconvInst = dconvMeta.Instantiate();
        dconvInst.ProvideComponentProperties(); // resets Name -- must set Name after this, see AddOleDbComponent's comment
        dconvMeta.Name = "DCONV_Types";

        AttachPath(pipe, srcOutput, dconvMeta.InputCollection[0]);
        dconvInst.AcquireConnections(null);
        dconvInst.ReinitializeMetaData();
        dconvInst.ReleaseConnections();

        var dconvInput = dconvMeta.InputCollection[0];
        var dconvVirtualInput = dconvInput.GetVirtualInput();
        var dconvOutput = dconvMeta.OutputCollection[0];

        AddConvertedColumn(dconvMeta, dconvInst, dconvInput, dconvVirtualInput, dconvOutput, 0, "R8Text", "ValR8", DataType.DT_R8, 0, 0, 0, 0);
        AddConvertedColumn(dconvMeta, dconvInst, dconvInput, dconvVirtualInput, dconvOutput, 1, "WstrText", "ValWstr", DataType.DT_WSTR, 10, 0, 0, 0);
        AddConvertedColumn(dconvMeta, dconvInst, dconvInput, dconvVirtualInput, dconvOutput, 2, "BoolText", "ValBool", DataType.DT_BOOL, 0, 0, 0, 0);

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticDataConversionTypesTarget]");
        AttachPath(pipe, dconvOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// A PROBE, built speculatively 2026-08-30: OLE DB Source (SqlCommand: ID, three raw text
    /// columns) -> Data Conversion (I2Text -> Val_i2 DT_I2; I8Text -> Val_i8 DT_I8; TimestampText
    /// -> Val_dt DT_DBTIMESTAMP, all dispositions IgnoreFailure) -> OLE DB Destination. Three
    /// Data Conversion target types this tool has never measured -- named as candidates in
    /// CLAUDE.md's own "remaining known gaps" list, no real evidenced instance in the tracked
    /// portfolio. Same seed shape (valid/padded-valid/invalid/empty/NULL) as the original
    /// data-conversion-types probe, to confirm IgnoreFailure nulls out on failure the same way.
    ///
    /// Requires the backing tables from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-lookup-single-tables.sql</c>
    /// already created on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>.
    /// </summary>
    private static int BuildDataConversionTypes2Fixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticDataConversionTypes2" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Coerce";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null, sqlCommand: "SELECT ID, I2Text, I8Text, TimestampText FROM dbo.SyntheticDataConversionTypes2Input");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var srcOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);

        var dconvMeta = pipe.ComponentMetaDataCollection.New();
        dconvMeta.ComponentClassID = "Microsoft.DataConvert";
        var dconvInst = dconvMeta.Instantiate();
        dconvInst.ProvideComponentProperties(); // resets Name -- must set Name after this, see AddOleDbComponent's comment
        dconvMeta.Name = "DCONV_Types2";

        AttachPath(pipe, srcOutput, dconvMeta.InputCollection[0]);
        dconvInst.AcquireConnections(null);
        dconvInst.ReinitializeMetaData();
        dconvInst.ReleaseConnections();

        var dconvInput = dconvMeta.InputCollection[0];
        var dconvVirtualInput = dconvInput.GetVirtualInput();
        var dconvOutput = dconvMeta.OutputCollection[0];

        AddConvertedColumn(dconvMeta, dconvInst, dconvInput, dconvVirtualInput, dconvOutput, 0, "I2Text", "Val_i2", DataType.DT_I2, 0, 0, 0, 0);
        AddConvertedColumn(dconvMeta, dconvInst, dconvInput, dconvVirtualInput, dconvOutput, 1, "I8Text", "Val_i8", DataType.DT_I8, 0, 0, 0, 0);
        AddConvertedColumn(dconvMeta, dconvInst, dconvInput, dconvVirtualInput, dconvOutput, 2, "TimestampText", "Val_dt", DataType.DT_DBTIMESTAMP, 0, 0, 0, 0);

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticDataConversionTypes2Target]");
        AttachPath(pipe, dconvOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    private static IDTSComponentMetaData100 AddOleDbComponent(
        MainPipe pipe, string name, string classId, ConnectionManager cm, int? accessMode, string? openRowset, string? sqlCommand = null)
    {
        var meta = pipe.ComponentMetaDataCollection.New();
        meta.ComponentClassID = classId;
        var inst = meta.Instantiate();
        // ProvideComponentProperties() resets Name to the component TYPE's own default
        // ("OLE DB Destination" for every instance, "OLE DB Source" for every instance) --
        // confirmed the hard way: setting .Name BEFORE this call is silently discarded, so
        // two destinations in the same Data Flow Task both persisted name="OLE DB
        // Destination" (only their auto-uniquified refId differed), and SSDT rejected the
        // package on manual "Add Existing Item" with "two objects with the duplicate name of
        // 'OLE DB Destination' and 'OLE DB Destination'". Must be set AFTER, not before.
        inst.ProvideComponentProperties();
        meta.Name = name;

        var conn = meta.RuntimeConnectionCollection[0];
        conn.ConnectionManagerID = cm.ID;
        conn.ConnectionManager = DtsConvert.GetExtendedInterface(cm);

        if (accessMode is not null) inst.SetComponentProperty("AccessMode", accessMode.Value);
        if (openRowset is not null) inst.SetComponentProperty("OpenRowset", openRowset);
        if (sqlCommand is not null) inst.SetComponentProperty("SqlCommand", sqlCommand);

        return meta;
    }

    /// <summary>Resolves live schema (source) or maps every external column by name (destination) -- must run AFTER the component's connection and OpenRowset/SqlCommand are set, and after any upstream path is attached for a destination.</summary>
    private static void ResolveOleDbMetadata(IDTSComponentMetaData100 meta, bool isDestination)
    {
        var inst = meta.Instantiate();
        inst.AcquireConnections(null);
        inst.ReinitializeMetaData();
        inst.ReleaseConnections();

        if (!isDestination) return;

        var input = meta.InputCollection[0];
        var virtualInput = input.GetVirtualInput();
        var externalNames = new HashSet<string>(
            input.ExternalMetadataColumnCollection.Cast<IDTSExternalMetadataColumn100>().Select(e => e.Name));
        // Only pull in virtual input columns the destination's own external metadata actually
        // names -- a superfluous upstream column (e.g. a Data Conversion's own untouched raw
        // string source, converted to a differently-named column the destination DOES want)
        // marked UT_READONLY with no external mapping made "OLE DB Destination" fail runtime
        // validation with VS_NEEDSNEWMETADATA (0xC004706B), confirmed empirically by comparing
        // this fixture (which has exactly this shape) against every prior single-destination
        // fixture (which never did). Every prior fixture's virtual input columns were already
        // a subset of the destination's external columns, so this filter is a no-op for them.
        foreach (IDTSVirtualInputColumn100 vcol in virtualInput.VirtualInputColumnCollection)
        {
            if (!externalNames.Contains(vcol.Name)) continue;
            inst.SetUsageType(input.ID, virtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }

        foreach (IDTSExternalMetadataColumn100 ext in input.ExternalMetadataColumnCollection)
        {
            var match = input.InputColumnCollection.Cast<IDTSInputColumn100>().FirstOrDefault(c => c.Name == ext.Name);
            if (match is null)
            {
                throw new InvalidOperationException($"fixture build error: no upstream column named '{ext.Name}' to map onto destination '{meta.Name}' -- source and destination column names must match exactly for this builder's simple by-name mapping.");
            }
            inst.MapInputColumn(input.ID, match.ID, ext.ID);
        }
    }

    private static void AttachPath(MainPipe pipe, IDTSOutput100 output, IDTSInput100 input)
    {
        var path = pipe.PathCollection.New();
        path.AttachPathAndPropagateNotifications(output, input);
    }

    private static RtTaskHost AddExecuteSql(RtPackage pkg, string name, ConnectionManager cm, string sql)
    {
        var host = (RtTaskHost)pkg.Executables.Add("Microsoft.ExecuteSQLTask");
        host.Name = name;
        host.Properties["Connection"].SetValue(host, cm.Name);
        host.Properties["SqlStatementSource"].SetValue(host, sql);
        return host;
    }

    /// <summary>Execute SQL (create-if-missing) -> Flat File Source -> OLE DB Destination -> Execute SQL (post-load update). A 3-node chain, so it lands in its own ParallelLevels group distinct from branches 2/3's single-node groups.</summary>
    private static void BuildBranch1(RtPackage pkg, ConnectionManager sqlCm, ConnectionManager loadACsvCm)
    {
        var createTask = AddExecuteSql(pkg, "SQL_CreateLoadATemp", sqlCm,
            "IF OBJECT_ID('dbo.SyntheticLoadATemp') IS NULL\n" +
            "CREATE TABLE dbo.SyntheticLoadATemp (ID INT NOT NULL, Name NVARCHAR(50) NOT NULL, Amount DECIMAL(12,2) NOT NULL, EntryDate DATE NOT NULL);");

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_LoadA";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = pipe.ComponentMetaDataCollection.New();
        srcMeta.ComponentClassID = "Microsoft.FlatFileSource";
        var srcInst = srcMeta.Instantiate();
        srcInst.ProvideComponentProperties(); // resets Name to the class default -- must set Name after this, see AddOleDbComponent's comment
        srcMeta.Name = "Flat File Source";
        var srcConn = srcMeta.RuntimeConnectionCollection[0];
        srcConn.ConnectionManagerID = loadACsvCm.ID;
        srcConn.ConnectionManager = DtsConvert.GetExtendedInterface(loadACsvCm);
        srcInst.AcquireConnections(null);
        srcInst.ReinitializeMetaData();
        srcInst.ReleaseConnections();

        var mainOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticLoadATemp]");
        AttachPath(pipe, mainOutput, destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        var updateTask = AddExecuteSql(pkg, "SQL_UpdateLoadA", sqlCm,
            "UPDATE dbo.SyntheticLoadATemp SET EntryDate = EntryDate;");

        pkg.PrecedenceConstraints.Add(createTask, dftHost);
        pkg.PrecedenceConstraints.Add(dftHost, updateTask);
    }

    /// <summary>One Data Flow Task, two independent OLE DB Source -> Destination pairs, no transform. No precedence to branch 1 -- an isolated node in the DAG.</summary>
    private static void BuildBranch2DirectCopy(RtPackage pkg, ConnectionManager sqlCm)
    {
        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_DirectCopy";
        var pipe = (MainPipe)dftHost.InnerObject;

        BuildDirectCopyPair(pipe, sqlCm, "OLE DB Source", "OLE DB Destination", "[dbo].[SyntheticDirectA]", "[dbo].[SyntheticDirectACopy]");
        BuildDirectCopyPair(pipe, sqlCm, "OLE DB Source 1", "OLE DB Destination 1", "[dbo].[SyntheticDirectB]", "[dbo].[SyntheticDirectBCopy]");
    }

    private static void BuildDirectCopyPair(MainPipe pipe, ConnectionManager sqlCm, string srcName, string destName, string srcTable, string destTable)
    {
        var srcMeta = AddOleDbComponent(pipe, srcName, "Microsoft.OLEDBSource", sqlCm, accessMode: 0, openRowset: srcTable);
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var destMeta = AddOleDbComponent(pipe, destName, "Microsoft.OLEDBDestination", sqlCm, accessMode: 3, openRowset: destTable);
        AttachPath(pipe, srcMeta.OutputCollection[0], destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);
    }

    /// <summary>Flat File Source's main output -> OLE DB Destination; its ERROR output -> a Script Component (minimal, real, passthrough) -> a second OLE DB Destination. No precedence to branches 1/2 -- an isolated node in the DAG.</summary>
    private static void BuildBranch3ErrorRouting(RtPackage pkg, ConnectionManager sqlCm, ConnectionManager errorRoutingCsvCm)
    {
        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_ErrorRouting";
        // The Script Component below never goes through the VSTA editor/compiler (see
        // BuildPassthroughScriptComponent's own comment), so it has no cached BinaryCode.
        // Without DelayValidation, SSDT's package validation (confirmed to fire on "Add
        // Existing Item", not just execution) fails hard: "The binary code for the script is
        // not found" followed by a raw ArgumentException from LoadScriptFromComponent trying
        // to interpret the SourceCode text itself as a lookup key. DelayValidation defers this
        // task's validation to execution time -- which this structural-evidence-only fixture,
        // documented as never compiled/executed, never reaches.
        dftHost.DelayValidation = true;
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = pipe.ComponentMetaDataCollection.New();
        srcMeta.ComponentClassID = "Microsoft.FlatFileSource";
        var srcInst = srcMeta.Instantiate();
        srcInst.ProvideComponentProperties(); // resets Name to the class default -- must set Name after this, see AddOleDbComponent's comment
        srcMeta.Name = "Flat File Source";
        var srcConn = srcMeta.RuntimeConnectionCollection[0];
        srcConn.ConnectionManagerID = errorRoutingCsvCm.ID;
        srcConn.ConnectionManager = DtsConvert.GetExtendedInterface(errorRoutingCsvCm);
        srcInst.AcquireConnections(null);
        srcInst.ReinitializeMetaData();
        srcInst.ReleaseConnections();

        var mainOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => !o.IsErrorOut);
        var errorOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().Single(o => o.IsErrorOut);

        var mainDestMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticErrorRoutingMain]");
        AttachPath(pipe, mainOutput, mainDestMeta.InputCollection[0]);
        ResolveOleDbMetadata(mainDestMeta, isDestination: true);

        var scriptMeta = BuildPassthroughScriptComponent(pipe, "Script Component", errorOutput);

        var errDestMeta = AddOleDbComponent(pipe, "OLE DB Destination 1", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticErrorRoutingCaught]");
        AttachPath(pipe, scriptMeta.OutputCollection[0], errDestMeta.InputCollection[0]);
        ResolveOleDbMetadata(errDestMeta, isDestination: true);
    }

    /// <summary>
    /// A genuine, minimal Script Component wired as a synchronous passthrough of the Flat
    /// File Source's error-output columns. Its custom properties (<c>UserComponentTypeName</c>,
    /// <c>SourceCode</c>) are set directly via <c>IDTSCustomProperty100.Value</c> rather than
    /// through the VSTA project editor -- the same "bypass the editor entirely" technique
    /// CLAUDE.md's Script Component notes already document as verified: the object model
    /// persists exactly what is set here, no compiled VSTA project needed for a structural
    /// (never-executed) fixture.
    /// </summary>
    private static IDTSComponentMetaData100 BuildPassthroughScriptComponent(MainPipe pipe, string name, IDTSOutput100 upstreamOutput)
    {
        var meta = pipe.ComponentMetaDataCollection.New();
        meta.ComponentClassID = "Microsoft.ManagedComponentHost";

        // UserComponentTypeName must be seeded BEFORE Instantiate()/ProvideComponentProperties()
        // -- confirmed empirically, not documented anywhere obvious. Without this, the managed
        // host initializes as a bare "Managed Component Wrapper" with none of the script-specific
        // custom properties (SourceCode/BinaryCode/ScriptLanguage/...) at all; setting
        // UserComponentTypeName only AFTER the fact (the order CLAUDE.md's existing Script
        // Component notes describe for populating an ALREADY-SCRIPT component's properties) is
        // too late for a component built from scratch.
        var seedProp = meta.CustomPropertyCollection.New();
        seedProp.Name = "UserComponentTypeName";
        seedProp.Value = "Microsoft.ScriptComponentHost";

        var inst = meta.Instantiate();
        // ProvideComponentProperties() ALSO resets Name to the class default here (confirmed
        // the same way as AddOleDbComponent's identical gotcha) -- must set Name after, not
        // combined with the UserComponentTypeName seed above, which has the opposite ordering
        // requirement (must be set BEFORE). The two rules do not conflict; each just has its
        // own correct side of this one call.
        inst.ProvideComponentProperties();
        meta.Name = name;

        SetCustomProperty(meta, "SourceCode", new[]
        {
            "// Synthetic fixture: passthrough of caught error rows. Never compiled/executed --",
            "// this package is structural evidence for the extractor, not a runnable pipeline.",
            "public class ScriptMain : UserComponent { }",
        });

        AttachPath(pipe, upstreamOutput, meta.InputCollection[0]);

        // Mark every upstream column as used, exactly like ResolveOleDbMetadata's destination
        // path -- a synchronous transform's untouched passthrough columns are consumed
        // downstream by lineageId, never re-declared as this component's own output columns
        // (LineageBuilder's own documented rule, reconfirmed here on a fourth component type).
        var input = meta.InputCollection[0];
        var virtualInput = input.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in virtualInput.VirtualInputColumnCollection)
        {
            inst.SetUsageType(input.ID, virtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }

        return meta;
    }

    /// <summary>
    /// OLE DB Source -> Script Component -> OLE DB Destination, where the Script Component
    /// genuinely SYNTHESIZES two new output columns (FullName, IsValid) that exist nowhere on
    /// its input -- the shape RBC_Demo_ETL's own SCR_CleanseCustomerRow has, and the one
    /// `ssisx generate --seams` exists for.
    ///
    /// Deliberately NOT reusing SyntheticScriptComponent.dtsx: that fixture's Script Component is
    /// a pure PASSTHROUGH and declares no output columns of its own, so it cannot exercise the
    /// seam path at all. Assuming otherwise would repeat this project's own twice-made mistake of
    /// treating a fixture that never sets X as evidence about X (see CLAUDE.md 2026-08-31).
    /// </summary>
    private static int BuildScriptComponentSeamsFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticScriptComponentSeams" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Load";
        var pipe = (MainPipe)dftHost.InnerObject;

        var srcMeta = AddOleDbComponent(pipe, "OLE DB Source", "Microsoft.OLEDBSource", sqlCm,
            accessMode: 2, openRowset: null,
            sqlCommand: "SELECT ID, FirstName, LastName FROM dbo.SyntheticScriptSeamsInput");
        ResolveOleDbMetadata(srcMeta, isDestination: false);

        var mainOutput = srcMeta.OutputCollection.Cast<IDTSOutput100>().First(o => !o.IsErrorOut);
        var scriptMeta = BuildSynthesizingScriptComponent(pipe, "SCR_Cleanse", mainOutput);

        var destMeta = AddOleDbComponent(pipe, "OLE DB Destination", "Microsoft.OLEDBDestination", sqlCm,
            accessMode: 3, openRowset: "[dbo].[SyntheticScriptSeamsTarget]");
        AttachPath(pipe, scriptMeta.OutputCollection[0], destMeta.InputCollection[0]);
        ResolveOleDbMetadata(destMeta, isDestination: true);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// Like <see cref="BuildPassthroughScriptComponent"/> (same UserComponentTypeName-before-
    /// Instantiate() ordering rule, same bypass of the VSTA editor), but additionally declares
    /// two output columns of its own so downstream components reference values no upstream
    /// component produces.
    ///
    /// <c>SourceCode</c> is written as a real (file name, encoding, content) TRIPLE, matching the
    /// layout confirmed 2026-08-31 from a genuine SSDT-authored Script Component -- so
    /// AiPacketEmitter's own triple parser sees a realistic array here rather than one it has to
    /// reject as misaligned.
    /// </summary>
    private static IDTSComponentMetaData100 BuildSynthesizingScriptComponent(MainPipe pipe, string name, IDTSOutput100 upstreamOutput)
    {
        var meta = pipe.ComponentMetaDataCollection.New();
        meta.ComponentClassID = "Microsoft.ManagedComponentHost";

        var seedProp = meta.CustomPropertyCollection.New();
        seedProp.Name = "UserComponentTypeName";
        seedProp.Value = "Microsoft.ScriptComponentHost";

        var inst = meta.Instantiate();
        inst.ProvideComponentProperties();
        meta.Name = name;

        SetCustomProperty(meta, "SourceCode", new[]
        {
            "main.cs",
            "UTF8",
            string.Join("\r\n",
                "using System;",
                "using Microsoft.SqlServer.Dts.Pipeline;",
                "",
                "[SSISScriptComponentEntryPoint]",
                "public class ScriptMain : UserComponent",
                "{",
                "    public override void Input0_ProcessInputRow(Input0Buffer Row)",
                "    {",
                "        // Trim both parts, join with a single space, and drop a trailing space",
                "        // when LastName is empty -- the same shape as the real",
                "        // SCR_CleanseCustomerRow this fixture stands in for.",
                "        Row.FullName = ((Row.FirstName ?? \"\").Trim() + \" \" + (Row.LastName ?? \"\").Trim()).Trim();",
                "",
                "        // A row is valid only when BOTH name parts are non-empty after trimming.",
                "        Row.IsValid = (Row.FirstName ?? \"\").Trim().Length > 0",
                "                   && (Row.LastName ?? \"\").Trim().Length > 0;",
                "    }",
                "}"),
        });

        AttachPath(pipe, upstreamOutput, meta.InputCollection[0]);

        var input = meta.InputCollection[0];
        var virtualInput = input.GetVirtualInput();
        foreach (IDTSVirtualInputColumn100 vcol in virtualInput.VirtualInputColumnCollection)
        {
            inst.SetUsageType(input.ID, virtualInput, vcol.LineageID, DTSUsageType.UT_READONLY);
        }

        // The synthesized columns. DT_WSTR's codepage argument must be 0, not 1252 -- 1252 is
        // DT_STR's non-Unicode convention and throws 0xC0204025 here (already documented in
        // CLAUDE.md for the Union All round; reconfirmed on this component type).
        var output = meta.OutputCollection[0];
        var fullName = inst.InsertOutputColumnAt(output.ID, output.OutputColumnCollection.Count, "FullName", "");
        inst.SetOutputColumnDataTypeProperties(output.ID, fullName.ID, DataType.DT_WSTR, 100, 0, 0, 0);
        var isValid = inst.InsertOutputColumnAt(output.ID, output.OutputColumnCollection.Count, "IsValid", "");
        inst.SetOutputColumnDataTypeProperties(output.ID, isValid.ID, DataType.DT_BOOL, 0, 0, 0, 0);

        return meta;
    }

    private static void SetCustomProperty(IDTSComponentMetaData100 meta, string name, object value)
    {
        var prop = meta.CustomPropertyCollection.Cast<IDTSCustomProperty100>().FirstOrDefault(p => p.Name == name)
            ?? throw new InvalidOperationException($"fixture build error: component '{meta.Name}' has no custom property named '{name}' -- ProvideComponentProperties() may not have run, or the property name is wrong.");
        prop.Value = value;
    }

    /// <summary>
    /// A single fully-ordered chain whose second link is DISABLED, plus a DISABLED Sequence
    /// Container at the end holding one enabled child:
    ///
    ///   SQL_MarkPre -> SQL_Disabled_MarkNever -> DFT_Load -> SQL_MarkPost -> SEQ_Disabled
    ///                  (Disabled=True)                                      (Disabled=True)
    ///                                                                        +- SQL_InsideDisabledSeq
    ///
    /// Built to answer, against the real SSIS runtime, the one question ssisx's own
    /// disabled-skip depends on: whether a disabled task's SUCCESSORS still run. If they do
    /// not, skipping just the disabled node in codegen would be wrong -- the whole downstream
    /// chain would have to go with it.
    ///
    /// Deliberately with NO parallelism (every root node ordered), so the only thing this
    /// fixture varies is disabled-ness -- nothing here should produce a Parallelism advisory.
    /// The disabled task sits in PRE-LOAD position and the disabled container in POST-FLOW
    /// position, so one fixture covers both of PackagePlanner's own placement paths.
    /// Backing tables: tests/Ssis.Extract.Tests/Fixtures/synthetic-disabled-task-tables.sql.
    /// </summary>
    private static int BuildDisabledTaskFixture(string outputPath)
    {
        var csvPath = Path.Combine(TestFixturesDir(), "synthetic-disabled-task-csv", "SyntheticDisabledTask.csv");
        if (!File.Exists(csvPath))
        {
            Console.Error.WriteLine($"error: expected CSV fixture at {csvPath} -- this tool's checked-in test fixtures are missing or moved.");
            return 2;
        }

        var pkg = new RtPackage { Name = "SyntheticDisabledTask" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var csvCm = AddFlatFileConnectionManager(pkg, "CM_DisabledTaskCsv", csvPath,
            ("ID", "DT_I4", 0, 0, 0),
            ("Name", "DT_WSTR", 50, 0, 0));

        var markPre = AddExecuteSql(pkg, "SQL_MarkPre", sqlCm,
            "DELETE FROM dbo.SyntheticDisabledTaskLog; INSERT dbo.SyntheticDisabledTaskLog (Marker) VALUES (N'pre');");

        // The whole point of the fixture. Its statement must never execute -- under SSIS
        // because the task is disabled, and in generated code because PackagePlanner skips it.
        var disabled = AddExecuteSql(pkg, "SQL_Disabled_MarkNever", sqlCm,
            "INSERT dbo.SyntheticDisabledTaskLog (Marker) VALUES (N'disabled-ran');");
        disabled.Disable = true;

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Load";
        BuildFlatFileToOleDbLoad((MainPipe)dftHost.InnerObject, csvCm, sqlCm, "[dbo].[SyntheticDisabledTaskTarget]");

        var markPost = AddExecuteSql(pkg, "SQL_MarkPost", sqlCm,
            "INSERT dbo.SyntheticDisabledTaskLog (Marker) VALUES (N'post');");

        // A disabled CONTAINER: its children are skipped too, which is a separate code path
        // from a disabled leaf task (the walk must not descend at all).
        var seqExec = pkg.Executables.Add("STOCK:SEQUENCE");
        var seq = (Microsoft.SqlServer.Dts.Runtime.Sequence)seqExec;
        seq.Name = "SEQ_Disabled";
        seq.Disable = true;

        var insideSeq = (RtTaskHost)seq.Executables.Add("Microsoft.ExecuteSQLTask");
        insideSeq.Name = "SQL_InsideDisabledSeq";
        insideSeq.Properties["Connection"].SetValue(insideSeq, sqlCm.Name);
        insideSeq.Properties["SqlStatementSource"].SetValue(insideSeq,
            "INSERT dbo.SyntheticDisabledTaskLog (Marker) VALUES (N'inside-disabled-seq');");

        pkg.PrecedenceConstraints.Add(markPre, (Executable)disabled);
        pkg.PrecedenceConstraints.Add((Executable)disabled, dftHost);
        pkg.PrecedenceConstraints.Add(dftHost, (Executable)markPost);
        pkg.PrecedenceConstraints.Add((Executable)markPost, (Executable)seqExec);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// SQL_Truncate -> SCR_Start -> DFT_Load -> SCR_Finish, the shape that actually exercises
    /// Script Task seams end to end:
    ///
    ///  - SCR_Start is in PRE-FLOW position (before any Data Flow Task) and SCR_Finish in
    ///    POST-FLOW, so one fixture covers both, and the generated Steps list must interleave them
    ///    with the flow in exactly that order.
    ///  - SQL_Truncate is ordered FIRST, so it is legitimately hoistable and must NOT trip the
    ///    hoisting-inversion guard -- the negative case for it. (BuildScriptTaskHoistInversionFixture
    ///    is the positive one.)
    ///  - SCR_Start writes User::Marker and SCR_Finish reads it, so a real run proves the shared
    ///    PackageVariables carries a value from one ported Script Task to the other. That sharing
    ///    is the whole reason PackageVariables exists (RBC_Demo_ETL's own Package does exactly this
    ///    with User::BatchStartTime), and nothing else in Etl.Core passes state between steps.
    ///
    /// The Script Tasks carry no VSTA source of their own -- setting one through the object model
    /// is not something this builder can do (see CLAUDE.md's Script Task note). That does not
    /// weaken the fixture: what is under test is the SEAM and its wiring, and the logic arrives as
    /// a hand-written fill either way.
    /// Backing tables: tests/Ssis.Extract.Tests/Fixtures/synthetic-script-task-tables.sql.
    /// </summary>
    private static int BuildScriptTaskSeamsFixture(string outputPath) =>
        BuildScriptTaskFixture(outputPath, "SyntheticScriptTaskSeams", invertHoisting: false);

    /// <summary>
    /// SCR_First -> SQL_Truncate -> DFT_Load: the same parts, with the Execute SQL Task ordered
    /// AFTER the Script Task. PreLoadStatements are hoisted ahead of every step and a Script Task
    /// IS a step, so generating this as-is would run the TRUNCATE first and invert the package's
    /// real order. Exists purely to prove PackagePlanner.ReportHoistingInversion fires -- a guard
    /// nothing else in the corpus can trigger.
    /// </summary>
    private static int BuildScriptTaskHoistInversionFixture(string outputPath) =>
        BuildScriptTaskFixture(outputPath, "SyntheticScriptTaskHoistInversion", invertHoisting: true);

    private static int BuildScriptTaskFixture(string outputPath, string packageName, bool invertHoisting)
    {
        var csvPath = Path.Combine(TestFixturesDir(), "synthetic-script-task-csv", "SyntheticScriptTask.csv");
        if (!File.Exists(csvPath))
        {
            Console.Error.WriteLine($"error: expected CSV fixture at {csvPath} -- this tool's checked-in test fixtures are missing or moved.");
            return 2;
        }

        var pkg = new RtPackage { Name = packageName };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var csvCm = AddFlatFileConnectionManager(pkg, "CM_ScriptTaskCsv", csvPath,
            ("ID", "DT_I4", 0, 0, 0),
            ("Name", "DT_WSTR", 50, 0, 0));

        // The variable the two Script Tasks pass between themselves. Declared for real so the
        // extracted ReadWriteVariables list is genuine rather than a name this builder invented.
        var marker = pkg.Variables.Add("Marker", false, "User", "");

        var truncateTask = AddExecuteSql(pkg, "SQL_Truncate", sqlCm,
            "TRUNCATE TABLE dbo.SyntheticScriptTaskTarget; DELETE FROM dbo.SyntheticScriptTaskLog;");

        var startTask = AddScriptTask(pkg, "SCR_Start", readWriteVariables: marker.QualifiedName);

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Load";
        BuildFlatFileToOleDbLoad((MainPipe)dftHost.InnerObject, csvCm, sqlCm, "[dbo].[SyntheticScriptTaskTarget]");

        if (invertHoisting)
        {
            // SCR_First -> SQL_Truncate -> DFT_Load. Nothing after the flow: the point is only the
            // pre-flow ordering.
            startTask.Name = "SCR_First";
            pkg.PrecedenceConstraints.Add((Executable)startTask, (Executable)truncateTask);
            pkg.PrecedenceConstraints.Add((Executable)truncateTask, dftHost);
        }
        else
        {
            var finishTask = AddScriptTask(pkg, "SCR_Finish", readOnlyVariables: marker.QualifiedName);
            pkg.PrecedenceConstraints.Add((Executable)truncateTask, (Executable)startTask);
            pkg.PrecedenceConstraints.Add((Executable)startTask, dftHost);
            pkg.PrecedenceConstraints.Add(dftHost, (Executable)finishTask);
        }

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// A Script Task with its declared variable surface set but no VSTA project and no declared
    /// language.
    ///
    /// ScriptLanguage is deliberately NOT set, and the reason is worth recording: the runtime
    /// property does not take the value that gets persisted. Setting it to "CSharp" -- exactly what
    /// the saved XML's own <c>Language</c> attribute contains, as read straight out of
    /// RBC_Demo_ETL's real Package.dtsx -- throws
    /// <c>UnrecognizedVSTAScriptLanguageException: "CSharp" was not recognized as a valid script
    /// language</c>, because the setter wants a VSTA DISPLAY name ("Microsoft Visual C# 2022" and
    /// the like). Same runtime-name-vs-XML-attribute divergence as the ForEach File Enumerator's
    /// own "Directory"/"FileNameRetrieval" properties. Left unset because nothing under test needs
    /// it: PackagePlanner keys off ExecutableType, and the language only ever reaches a comment
    /// line in the generated class.
    ///
    /// The script SOURCE is likewise not settable through the object model (see CLAUDE.md's Script
    /// Task note), which also does not weaken the fixture -- the seam is what is under test, and
    /// its logic arrives as a hand-written fill either way.
    /// </summary>
    private static RtTaskHost AddScriptTask(
        RtPackage pkg, string name, string? readOnlyVariables = null, string? readWriteVariables = null)
    {
        var host = (RtTaskHost)pkg.Executables.Add("Microsoft.ScriptTask");
        host.Name = name;
        if (readOnlyVariables is not null) host.Properties["ReadOnlyVariables"].SetValue(host, readOnlyVariables);
        if (readWriteVariables is not null) host.Properties["ReadWriteVariables"].SetValue(host, readWriteVariables);

        // Without a compiled script the runtime would fail validation at design time; the same
        // reason SyntheticParallelShapes' own Script Component needs it (see report-schema.md).
        host.DelayValidation = true;
        return host;
    }

    /// <summary>
    /// The conditional-precedence-constraint PROBE. Its only job is to let a real dtexec run
    /// answer questions no documentation this project trusts answers precisely, before any
    /// generator code is written against them -- the same probe-first discipline as the
    /// numeric-coercion and Data Conversion rounds.
    ///
    /// One chain, seven successors, all hanging off the SAME node so ONE run measures every edge
    /// kind at once:
    ///
    ///   SQL_Reset -> SQL_Body -> { Success | Failure | Completion
    ///                            | Expression(true) | Expression(false)
    ///                            | ExpressionAndConstraint(Success, true)
    ///                            | ExpressionOrConstraint(Failure, false) }
    ///
    /// Each successor writes its own marker row, so what ran is read back from the table rather
    /// than inferred from console output. Every successor has exactly ONE incoming constraint,
    /// which keeps DTS:LogicalAnd (the AND/OR combination of MULTIPLE incoming constraints)
    /// entirely out of the measurement -- as does RBC_Demo_ETL's own real package.
    ///
    /// SQL_Body raises an error only when dbo.SyntheticCondConstraintControl.ShouldFail = 1, so
    /// the SUCCESS and FAILURE runs are the same fixture with one row flipped between them --
    /// no rebuild, and nothing else varying.
    ///
    /// User::ProbeRows is a literal 0, so "== 0" is always true and "&gt; 0" always false. That
    /// deliberately needs no mechanism to POPULATE a variable at run time (a result-set mapping
    /// would be a second unmeasured thing in the same probe), while still exercising both
    /// outcomes of expression evaluation in a single run.
    /// Backing tables: tests/Ssis.Extract.Tests/Fixtures/synthetic-cond-constraint-tables.sql.
    /// </summary>
    private static int BuildCondConstraintProbeFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticCondConstraint" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        pkg.Variables.Add("ProbeRows", false, "User", 0);

        var reset = AddExecuteSql(pkg, "SQL_Reset", sqlCm,
            "DELETE FROM dbo.SyntheticCondConstraintLog;");

        var body = AddExecuteSql(pkg, "SQL_Body", sqlCm,
            "INSERT dbo.SyntheticCondConstraintLog (Marker) VALUES (N'body');\n" +
            "IF (SELECT ShouldFail FROM dbo.SyntheticCondConstraintControl) = 1\n" +
            "    RAISERROR ('probe-forced-failure', 16, 1);");

        pkg.PrecedenceConstraints.Add((Executable)reset, (Executable)body);

        void Successor(string name, string marker, Action<Microsoft.SqlServer.Dts.Runtime.PrecedenceConstraint> configure)
        {
            var task = AddExecuteSql(pkg, name, sqlCm,
                $"INSERT dbo.SyntheticCondConstraintLog (Marker) VALUES (N'{marker}');");
            configure(pkg.PrecedenceConstraints.Add((Executable)body, (Executable)task));
        }

        Successor("SQL_OnSuccess", "on-success", pc => pc.Value = Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Success);
        Successor("SQL_OnFailure", "on-failure", pc => pc.Value = Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Failure);
        Successor("SQL_OnCompletion", "on-completion", pc => pc.Value = Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Completion);

        Successor("SQL_ExprTrue", "expr-true", pc =>
        {
            pc.EvalOp = Microsoft.SqlServer.Dts.Runtime.DTSPrecedenceEvalOp.Expression;
            pc.Expression = "@[User::ProbeRows] == 0";
        });
        Successor("SQL_ExprFalse", "expr-false", pc =>
        {
            pc.EvalOp = Microsoft.SqlServer.Dts.Runtime.DTSPrecedenceEvalOp.Expression;
            pc.Expression = "@[User::ProbeRows] > 0";
        });
        Successor("SQL_ExprAndSuccess", "expr-and-success", pc =>
        {
            pc.EvalOp = Microsoft.SqlServer.Dts.Runtime.DTSPrecedenceEvalOp.ExpressionAndConstraint;
            pc.Value = Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Success;
            pc.Expression = "@[User::ProbeRows] == 0";
        });
        Successor("SQL_ExprOrFailure", "expr-or-failure", pc =>
        {
            pc.EvalOp = Microsoft.SqlServer.Dts.Runtime.DTSPrecedenceEvalOp.ExpressionOrConstraint;
            pc.Value = Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Failure;
            pc.Expression = "@[User::ProbeRows] > 0";
        });

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// SQL_Reset -> SCR_SetRows -> DFT_Load -> three CONDITIONALLY gated Execute SQL Tasks, the
    /// shape that exercises a translated conditional precedence constraint end to end:
    ///
    ///  - all three gates are <c>EvalOp=ExpressionAndConstraint</c> with the constraint half left at
    ///    Success -- the one form the probe measured as reproducible under the generated code's single
    ///    whole-package transaction, and the real evidenced form (RBC_Demo_ETL's own
    ///    SCR_NotifyAndLogProgress -> DFT_BuildSalesSummary).
    ///  - SQL_GateTrue's condition reads a variable at its DESIGN-TIME default, so it proves the
    ///    generated seeding is what makes the default hold rather than Get&lt;T&gt;'s fallback
    ///    silently agreeing by luck.
    ///  - SQL_GateFalse must NOT run. A fixture where every gate fires would pass just as well with
    ///    the condition ignored entirely, which is the bug being guarded against.
    ///  - SQL_GateFromScript's condition reads User::RowsLoaded, which the Script Task WRITES.
    ///    That is the real package's own composition, and the reason PackageVariables had to exist
    ///    before this round was possible: a guard reading what a ported Script Task set.
    ///
    /// Deliberately gates Execute SQL Tasks rather than a second Data Flow Task: the gated-flow case
    /// is already proven directly on the real Package.dtsx (its DFT_BuildSalesSummary is wrapped),
    /// and a second flow here would need another source/destination pair for nothing this fixture
    /// does not already show.
    /// Backing tables: tests/Ssis.Extract.Tests/Fixtures/synthetic-cond-guard-tables.sql.
    /// </summary>
    private static int BuildCondGuardFixture(string outputPath)
    {
        var csvPath = Path.Combine(TestFixturesDir(), "synthetic-cond-guard-csv", "SyntheticCondGuard.csv");
        if (!File.Exists(csvPath))
        {
            Console.Error.WriteLine($"error: expected CSV fixture at {csvPath} -- this tool's checked-in test fixtures are missing or moved.");
            return 2;
        }

        var pkg = new RtPackage { Name = "SyntheticCondGuard" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var csvCm = AddFlatFileConnectionManager(pkg, "CM_CondGuardCsv", csvPath,
            ("ID", "DT_I4", 0, 0, 0),
            ("Name", "DT_WSTR", 50, 0, 0));

        // Declared for real, so the extracted design-time default (and the generated seed) is
        // genuine rather than a value this builder asserted.
        pkg.Variables.Add("Threshold", false, "User", 0);
        var rowsLoaded = pkg.Variables.Add("RowsLoaded", false, "User", 0);

        var reset = AddExecuteSql(pkg, "SQL_Reset", sqlCm,
            "TRUNCATE TABLE dbo.SyntheticCondGuardTarget; DELETE FROM dbo.SyntheticCondGuardLog;");

        var setRows = AddScriptTask(pkg, "SCR_SetRows", readWriteVariables: rowsLoaded.QualifiedName);

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Load";
        BuildFlatFileToOleDbLoad((MainPipe)dftHost.InnerObject, csvCm, sqlCm, "[dbo].[SyntheticCondGuardTarget]");

        pkg.PrecedenceConstraints.Add((Executable)reset, (Executable)setRows);
        pkg.PrecedenceConstraints.Add((Executable)setRows, dftHost);

        void Gate(string name, string marker, string expression)
        {
            var task = AddExecuteSql(pkg, name, sqlCm,
                $"INSERT dbo.SyntheticCondGuardLog (Marker) VALUES (N'{marker}');");
            var pc = pkg.PrecedenceConstraints.Add(dftHost, (Executable)task);
            pc.EvalOp = Microsoft.SqlServer.Dts.Runtime.DTSPrecedenceEvalOp.ExpressionAndConstraint;
            pc.Expression = expression;
        }

        Gate("SQL_GateTrue", "gate-true", "@[User::Threshold] == 0");
        Gate("SQL_GateFalse", "gate-false", "@[User::Threshold] > 0");
        Gate("SQL_GateFromScript", "gate-from-script", "@[User::RowsLoaded] > 0");

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// SQL_Truncate -> DFT_Load -> SQL_Body -> (Failure) -> SQL_Handler: the shape that exercises a
    /// Failure precedence constraint end to end, under BOTH real SSIS and generated code.
    ///
    ///  - SQL_Body raises an error only when dbo.SyntheticFailureHandlerControl.ShouldFail = 1, so
    ///    the SUCCESS and FAILURE runs are the same fixture with one row flipped. The success run is
    ///    what proves the handler does NOT run when nothing failed -- a fixture that only ever fails
    ///    would pass just as well with the handler wired unconditionally, which is the original bug.
    ///  - SQL_Handler is terminal and has exactly one incoming constraint, matching the one real
    ///    evidenced handler (RBC_Demo_ETL's SQL_LogLoadFailure).
    ///  - DFT_Load loads real rows BEFORE the failure, which is what makes the deliberate divergence
    ///    visible: SSIS auto-commits per task so those rows survive, while the generated package
    ///    rolls its single transaction back and only the handler's own row remains.
    ///
    /// Nothing in the package resets dbo.SyntheticFailureHandlerLog: a pre-load statement runs INSIDE
    /// the transaction, so on the failure run it would be rolled back and the log read-back would mix
    /// old rows with the handler's. The harness clears it externally before each run instead.
    /// Backing tables: tests/Ssis.Extract.Tests/Fixtures/synthetic-failure-handler-tables.sql.
    /// </summary>
    private static int BuildFailureHandlerFixture(string outputPath)
    {
        var csvPath = Path.Combine(TestFixturesDir(), "synthetic-failure-handler-csv", "SyntheticFailureHandler.csv");
        if (!File.Exists(csvPath))
        {
            Console.Error.WriteLine($"error: expected CSV fixture at {csvPath} -- this tool's checked-in test fixtures are missing or moved.");
            return 2;
        }

        var pkg = new RtPackage { Name = "SyntheticFailureHandler" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var sqlCm = pkg.Connections.Add("OLEDB");
        sqlCm.Name = "CM_Sql";
        sqlCm.ConnectionString = SqlConnectionString;

        var csvCm = AddFlatFileConnectionManager(pkg, "CM_FailureHandlerCsv", csvPath,
            ("ID", "DT_I4", 0, 0, 0),
            ("Name", "DT_WSTR", 50, 0, 0));

        var truncate = AddExecuteSql(pkg, "SQL_Truncate", sqlCm,
            "TRUNCATE TABLE dbo.SyntheticFailureHandlerTarget;");

        var dftHost = (RtTaskHost)pkg.Executables.Add("Microsoft.Pipeline");
        dftHost.Name = "DFT_Load";
        BuildFlatFileToOleDbLoad((MainPipe)dftHost.InnerObject, csvCm, sqlCm, "[dbo].[SyntheticFailureHandlerTarget]");

        var body = AddExecuteSql(pkg, "SQL_Body", sqlCm,
            "IF (SELECT ShouldFail FROM dbo.SyntheticFailureHandlerControl) = 1\n" +
            "    RAISERROR ('forced-failure-after-load', 16, 1);");

        var handler = AddExecuteSql(pkg, "SQL_Handler", sqlCm,
            "INSERT dbo.SyntheticFailureHandlerLog (Marker) VALUES (N'handled');");

        pkg.PrecedenceConstraints.Add((Executable)truncate, dftHost);
        pkg.PrecedenceConstraints.Add(dftHost, (Executable)body);

        var onFailure = pkg.PrecedenceConstraints.Add((Executable)body, (Executable)handler);
        onFailure.Value = Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Failure;

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// Reproduces BOTH real Execute Package Task shapes confirmed via <c>probe-execpkg</c> (see
    /// <c>ExecutePackageTaskPayload</c>'s own doc comment for the full round-trip evidence) in one
    /// package, two independent sibling tasks: <c>EPT_ProjectRef</c> (project-reference mode --
    /// <c>UseProjectReference=true</c>, <c>PackageName</c> only) and <c>EPT_FileRef</c> (legacy
    /// file-reference mode -- a FILE connection manager plus <c>PackageName</c>/
    /// <c>ExecuteOutOfProcess</c>). No backing SQL tables or real child .dtsx needed -- extraction
    /// reads the saved XML, it never invokes the referenced package.
    /// </summary>
    private static int BuildExecutePackageTaskFixture(string outputPath)
    {
        var pkg = new RtPackage { Name = "SyntheticExecutePackageTask" };
        pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive;

        var projRefTask = (RtTaskHost)pkg.Executables.Add("Microsoft.ExecutePackageTask");
        projRefTask.Name = "EPT_ProjectRef";
        projRefTask.Properties["UseProjectReference"].SetValue(projRefTask, true);
        projRefTask.Properties["PackageName"].SetValue(projRefTask, "ChildInSameProject.dtsx");

        var fileCm = pkg.Connections.Add("FILE");
        fileCm.Name = "CM_FILE_ChildPackage";
        fileCm.Properties["FileUsageType"].SetValue(fileCm, 0); // 0 = FileExists, same convention as FileSystemTask's own probe
        fileCm.ConnectionString = @"C:\SsisChildPackages\Legacy\ChildPackage.dtsx";

        var fileRefTask = (RtTaskHost)pkg.Executables.Add("Microsoft.ExecutePackageTask");
        fileRefTask.Name = "EPT_FileRef";
        fileRefTask.Properties["ExecuteOutOfProcess"].SetValue(fileRefTask, true);
        fileRefTask.Properties["PackageName"].SetValue(fileRefTask, "ChildPackage.dtsx");
        fileRefTask.Properties["Connection"].SetValue(fileRefTask, fileCm.Name);

        pkg.SaveToXML(out var xml, null);
        File.WriteAllText(outputPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }
}
