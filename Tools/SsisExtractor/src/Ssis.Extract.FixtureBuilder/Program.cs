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
/// Task sequenced AFTER a Data Flow Task extracts correctly -- <c>PackageRunner</c> in the
/// C# rewrite only models pre-load SQL today, so this is what makes that gap concrete
/// rather than theoretical.</item>
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
            Console.Error.WriteLine("usage: ssis-fixture-builder <output-dtsx-path> [parallel-shapes|nested-container|post-flow-sql|oledb-source-transform|conditional-split|conditional-split-remerge|file-system-task|findstring-trim|ternary|email-domain|datediff|data-conversion|data-conversion-split|flat-file-destination|merge-join|sort-merge-remerge|foreach-file-loop|excel-source|oledb-command|multicast|numeric-coercion|data-conversion-types]");
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
        if (fixtureName == "sort-merge-remerge")
        {
            return BuildSortMergeRemergeFixture(args[0]);
        }
        if (fixtureName == "foreach-file-loop")
        {
            return BuildForEachFileLoopFixture(args[0]);
        }
        if (fixtureName == "excel-source")
        {
            return BuildExcelSourceFixture(args[0]);
        }
        if (fixtureName == "oledb-command")
        {
            return BuildOleDbCommandFixture(args[0]);
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
        if (fixtureName != "parallel-shapes")
        {
            Console.Error.WriteLine($"error: unknown fixture '{fixtureName}' -- expected 'parallel-shapes', 'nested-container', 'post-flow-sql', 'oledb-source-transform', 'conditional-split', 'conditional-split-remerge', 'file-system-task', 'findstring-trim', 'ternary', 'email-domain', 'datediff', 'data-conversion', 'data-conversion-split', 'flat-file-destination', 'merge-join', 'sort-merge-remerge', 'foreach-file-loop', 'excel-source', 'oledb-command', 'multicast', 'numeric-coercion', or 'data-conversion-types'.");
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
    /// "PackageRunner only models pre-load SQL" gap (SyntheticParallelShapes' own branch 1);
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
    /// <b>NOT YET PRODUCING A CLEAN, CHECKED-IN FIXTURE -- left here as a documented starting
    /// point for whoever continues this, not deleted, since every fact below IS confirmed real.</b>
    /// Intended shape: two OLE DB Sources (Left: ID/Name; Right: ID/Phone) -> Sort each (key: ID,
    /// ascending) -> Microsoft.MergeJoin (JoinType=2, the real evidenced raw value from
    /// RBC_Demo_ETL's own MRG_CustomerContacts) -> OLE DB Destination, SQL-sourced (not Flat
    /// File + Data Conversion, unlike the real package) specifically to isolate Sort/MergeJoin's
    /// own mechanics in the smallest possible shape.
    ///
    /// <para><b>Object-model facts confirmed empirically while building this</b> (same "ask the
    /// runtime, don't guess" discipline as trap 12), all independently verified, none guessed:
    /// <list type="bullet">
    /// <item>Sort's own key designation is set via <c>SetInputColumnProperty(input.ID, column.ID,
    /// "NewSortKeyPosition", 1)</c> on its OWN INPUT column -- NOT the output side, confirmed
    /// only after reading a real SSDT-authored Sort's own saved XML during the extractor's own
    /// reader work (see <c>SortPayload</c>'s doc comment for the full story, including an
    /// initial wrong reading). <see cref="AddSortByKey"/> works correctly end to end.</item>
    /// <item><c>Microsoft.MergeJoin</c> starts with exactly ONE input from <c>ProvideComponentProperties()</c>
    /// -- <c>InsertInput(DTSInsertPlacement.IP_AFTER, 0)</c> adds the second.</item>
    /// <item>The designtime <c>InsertOutputColumnAt</c> throws <c>COMException 0xC0208019</c>
    /// unconditionally for Merge Join's own output (unlike Conditional Split, where the
    /// equivalent call works) -- the raw <c>output.OutputColumnCollection.New()</c> plus setting
    /// <c>Name</c>/<c>SetDataTypeProperties</c>/<c>MappedColumnID</c> directly on the resulting
    /// <c>IDTSOutputColumn100</c> works instead, and <c>MappedColumnID</c> is confirmed to be
    /// what persists as the saved XML's own <c>InputColumnID</c> custom property.</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>The actual unresolved problem, found by reading the saved XML back, not
    /// guessed:</b> after <c>SetComponentProperty("JoinType"/"NumKeyColumns"/"TreatNullsAsEqual", ...)</c>
    /// runs, the component appears to auto-generate its OWN additional output columns (via the
    /// SAME <c>InputColumnID</c>-custom-property persistence form real SSDT output uses, not the
    /// raw <c>MappedColumnID</c> attribute this method's own manual calls produce) for every
    /// input column already marked used on BOTH inputs -- confirmed by reading the saved XML,
    /// which shows SIX output columns (ID/Name/ID1 via the auto form, ID2/Name1/Phone1 via this
    /// method's own manual calls) instead of the 3 intended, with the auto-generated set's own
    /// refId path (<c>Inputs[83]</c>, an unnamed numeric input id) suggesting the TWO inputs'
    /// own identities also don't line up with this method's own index-0/index-1 assumption the
    /// way expected. Not root-caused -- a future attempt should dump the component's own
    /// <c>InputCollection</c>/<c>OutputCollection</c> state at each step (after
    /// <c>InsertInput</c>, after each <c>SetComponentProperty</c> call) to see exactly when the
    /// extra columns appear, rather than assuming this method's own call ordering is at fault.</para>
    ///
    /// Given this, Sort/MergeJoin's own RUNTIME join algorithm was instead verified via a plain
    /// C# unit test with no SSIS involved at all
    /// (<c>D:\PoC\SSIS_Rewrite\tests\Etl.Core.Tests\Data\MergeJoinRowSourceTests.cs</c>), and the
    /// real evidenced package (RBC_Demo_ETL's own Package_Transforms.dtsx) already proves
    /// extraction/codegen/build correctness end to end (0 warnings/0 errors regenerating its
    /// whole solution) -- see CLAUDE.md's own Merge Join section for the full account.
    ///
    /// Requires the backing tables from
    /// <c>tests/Ssis.Extract.Tests/Fixtures/synthetic-merge-join-tables.sql</c> already created
    /// and seeded on <c>.\SQLFORPOC_2022</c>/<c>SsisPoC</c>.
    /// </summary>
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
        mjInst.InsertInput(DTSInsertPlacement.IP_AFTER, 0); // starts with 1 input; this adds the 2nd

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
        MarkInputColumnsUsed(mjInst, mjMeta.InputCollection[0], "ID", "Name");
        MarkInputColumnsUsed(mjInst, mjMeta.InputCollection[1], "ID", "Phone");

        mjInst.SetComponentProperty("JoinType", 2); // the real evidenced raw value -- this fixture exists to learn what it means
        mjInst.SetComponentProperty("NumKeyColumns", 1);
        mjInst.SetComponentProperty("TreatNullsAsEqual", true);

        var mjOutput = mjMeta.OutputCollection[0];
        AddMergeJoinOutputColumn(mjOutput, mjMeta.InputCollection[0], "ID");
        AddMergeJoinOutputColumn(mjOutput, mjMeta.InputCollection[0], "Name");
        AddMergeJoinOutputColumn(mjOutput, mjMeta.InputCollection[1], "Phone");

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

    /// <summary>Adds one Merge Join output column mapped back to <paramref name="sourceColumnName"/>
    /// on <paramref name="fromInput"/>. Merge Join's own output is NOT auto-populated the way a
    /// synchronous passthrough transform's is, so every wanted column must be added explicitly --
    /// and, confirmed empirically, NOT via the designtime <c>InsertOutputColumnAt</c> (which
    /// threw <c>COMException 0xC0208019</c> unconditionally for this component, the same "raw
    /// .New() instead of the designtime call" shape <c>Microsoft.UnionAll</c>'s own second INPUT
    /// already needed for a different reason). The raw <c>IDTSOutputColumn100</c> exposes
    /// <c>Name</c>/<c>DataType</c>/etc. as ordinary settable properties (no <c>IDTSName100</c>
    /// cast needed, unlike a flat-file column) plus a <c>MappedColumnID</c> property that turns
    /// out to BE what persists as the saved XML's own <c>InputColumnID</c> custom property --
    /// confirmed by reading the saved XML back after setting it this way, not guessed from the
    /// property's name alone.</summary>
    private static void AddMergeJoinOutputColumn(IDTSOutput100 output, IDTSInput100 fromInput, string sourceColumnName)
    {
        var sourceCol = fromInput.InputColumnCollection.Cast<IDTSInputColumn100>().First(c => c.Name == sourceColumnName);
        var newCol = (IDTSOutputColumn100)output.OutputColumnCollection.New();
        newCol.Name = sourceColumnName;
        newCol.SetDataTypeProperties(sourceCol.DataType, sourceCol.Length, sourceCol.Precision, sourceCol.Scale, sourceCol.CodePage);
        newCol.MappedColumnID = sourceCol.LineageID;
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

    private static void SetCustomProperty(IDTSComponentMetaData100 meta, string name, object value)
    {
        var prop = meta.CustomPropertyCollection.Cast<IDTSCustomProperty100>().FirstOrDefault(p => p.Name == name)
            ?? throw new InvalidOperationException($"fixture build error: component '{meta.Name}' has no custom property named '{name}' -- ProvideComponentProperties() may not have run, or the property name is wrong.");
        prop.Value = value;
    }
}
