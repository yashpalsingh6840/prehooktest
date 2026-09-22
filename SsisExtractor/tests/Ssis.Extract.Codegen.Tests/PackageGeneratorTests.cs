using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen.Tests;

public class PackageGeneratorTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    // tests/Ssis.Extract.Codegen.Tests -> tests/Ssis.Extract.Tests/Fixtures.
    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    // Docs/Generated-Tests-Plan.md's own "Opt-out": --skip-tests omits the whole
    // {Package}.Tests project, and its own TEST-ORACLE/LocalFileSourceData gaps alongside it --
    // there is nothing for either to be an oracle/sample-data answer to once no test exists at
    // all. Uses a package with a real Script Task AND a real CSV source so both companion-gap
    // removal paths are exercised in one test, not just one.
    [Fact]
    public void Generate_OmitsTheWholeTestsProject_AndItsOwnTestOracleAndLocalDataGaps_WhenSkipTestsIsSet()
    {
        var package = LoadSyntheticFixture("SyntheticScriptTaskSeams.dtsx");

        var withTests = PackageGenerator.Generate(package, namespacePrefix: null, emitSeams: true, skipTests: false);
        var withoutTests = PackageGenerator.Generate(package, namespacePrefix: null, emitSeams: true, skipTests: true);

        // Sanity: the fixture genuinely produces test-related gaps when tests are ON, or this
        // test would pass vacuously.
        Assert.Contains(withTests.Gaps, g => g.Kind == GapKind.TestOracle);
        Assert.Contains(withTests.Gaps, g => g.Kind == GapKind.LocalFileSourceData);
        Assert.NotEmpty(withTests.SiblingFiles);

        Assert.Empty(withoutTests.SiblingFiles);
        Assert.DoesNotContain(withoutTests.Gaps, g => g.Kind is GapKind.TestOracle or GapKind.LocalFileSourceData);
        // The unrelated ScriptTask seam gaps (a genuinely different concern from --skip-tests)
        // are completely unaffected -- skipping tests never silently hides a real seam gap.
        Assert.Equal(
            withTests.Gaps.Count(g => g.Kind == GapKind.ScriptTask),
            withoutTests.Gaps.Count(g => g.Kind == GapKind.ScriptTask));

        // README.md must not describe tests that were never written -- checked directly, not
        // assumed, since PackageReadmeEmitter reads the same (now-cleared) testFiles list. The
        // WITH-tests README names this exact file in SQL_Truncate's own "Test" column; confirmed
        // present there first, then confirmed absent once tests are skipped.
        var readmeWithTests = Assert.Single(withTests.Files, f => f.RelativePath == "README.md");
        Assert.Contains("SQL_TruncateStatementTests.cs", readmeWithTests.Content);

        var readmeWithoutTests = Assert.Single(withoutTests.Files, f => f.RelativePath == "README.md");
        Assert.DoesNotContain("SQL_TruncateStatementTests.cs", readmeWithoutTests.Content);
    }

    // Found running this generator against a real third-party portfolio (D:\PoC\
    // SSIS_Packages_From_GitHub, sql-server-samples' own DailyETLMain.dtsx): SSIS lets two
    // Execute SQL Tasks anywhere in one package share the identical display name (a real,
    // evidenced authoring pattern -- a reusable task template copy-pasted into many branches,
    // each with its own DIFFERENT SQL statement) -- that real package had 13 tasks all named
    // "Get Lineage Key". Before this fix, SanitizeIdentifier alone named both tasks'
    // Mapping/{Name}Statement.cs identically, and a plain File.WriteAllText silently kept only
    // the LAST one written -- discarding the earlier task's own distinct SQL text with no error
    // and no gap. SyntheticDuplicateTaskNames.dtsx reproduces the minimal shape: SQL_PreLoad
    // (pre-load position, TRUNCATE) and a SECOND task also named "SQL_PreLoad" (post-flow
    // position, UPDATE) -- deliberately built by renaming SyntheticPostFlowSql.dtsx's own
    // SQL_PostLoad task's DTS:ObjectName, a byte-preserving edit (never a hand-written XML
    // fixture) so every other structural detail (refIds, precedence constraints, the CSV source)
    // stays exactly as already-proven correct elsewhere.
    [Fact]
    public void Generate_DisambiguatesTwoExecuteSqlTasks_ThatShareTheIdenticalDisplayName()
    {
        var package = LoadSyntheticFixture("SyntheticDuplicateTaskNames.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var preLoad = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SQL_PreLoadStatement.cs");
        var preLoad2 = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SQL_PreLoad_2Statement.cs");
        Assert.Contains("TRUNCATE TABLE dbo.SyntheticPostFlowTarget;", preLoad.Content);
        Assert.Contains("UPDATE dbo.SyntheticPostFlowTarget SET Name = UPPER(Name);", preLoad2.Content);

        // The paired starter tests must reference the SAME disambiguated name, not silently
        // collide (or reference a name PackageClassEmitter's own Reserve() didn't actually use).
        Assert.Single(result.SiblingFiles, f => f.RelativePath == $"{package.ObjectName}.Tests/SQL_PreLoadStatementTests.cs");
        Assert.Single(result.SiblingFiles, f => f.RelativePath == $"{package.ObjectName}.Tests/SQL_PreLoad_2StatementTests.cs");
        Assert.Single(result.SiblingFiles, f => f.RelativePath == $"{package.ObjectName}.Tests/SQL_PreLoad_2StepTests.cs");
        var stepTest = Assert.Single(result.SiblingFiles, f => f.RelativePath == $"{package.ObjectName}.Tests/SQL_PreLoad_2StepTests.cs");
        Assert.Contains("package.SQL_PreLoad_2(", stepTest.Content);
    }

    [Fact]
    public void Generate_ReportsAnEncryptedConnectionManagerSecret_AsATier1Gap()
    {
        var package = new PackageSpec
        {
            ObjectName = "PkgWithEncryptedSecret",
            SourceDtsxPath = "PkgWithEncryptedSecret.dtsx",
            Sha256 = new string('0', 64),
            FileSizeBytes = 1,
            LastWriteTimeUtc = DateTime.UnixEpoch,
            ProtectionLevelRaw = 3,
            ProtectionLevelName = "EncryptSensitiveWithUserKey",
            Coverage = new CoverageStats { TotalElements = 1, UnmappedElements = 0, ExcludedElements = 0, CoveragePercent = 100 },
            ConnectionManagers =
            [
                new ConnectionManagerSpec
                {
                    ObjectName = "CM_TargetDb",
                    CreationName = "OLEDB",
                    Scope = "Package",
                    WasRedacted = false,
                    EncryptedProperties = ["Password"],
                },
            ],
        };

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var gap = Assert.Single(result.Gaps, g => g.Kind == Ssis.Extract.Model.Analysis.GapKind.EncryptedConnectionManagerSecret);
        Assert.Equal("CM_TargetDb", gap.Location);
        Assert.Contains("Password", gap.Reason);
        Assert.Contains("never extracts the ciphertext", gap.Reason);
        // No secret material anywhere in the gap's own reason text.
        Assert.DoesNotContain("AQAAA", gap.Reason);
    }

    [Fact]
    public void Generate_WiresAPostFlowExecuteSqlTask_IntoProgramCsAfterTheFlowItFollows()
    {
        // SyntheticPostFlowSql.dtsx: SQL_PreLoad -> DFT_Load (Flat File Source -> Derived
        // Column -> OLE DB Destination) -> SQL_PostLoad. Unlike SyntheticParallelShapes.dtsx's
        // own post-flow Execute SQL Task (which follows a Data Flow Task with no Derived
        // Column and so never gets wired), this fixture's flow IS wired, so it's the one that
        // proves the post-flow SQL step reaches a real, compiling Program.cs -- confirmed
        // beyond this test by actually running the generated exe against .\SQLFORPOC_2022 and
        // reading dbo.SyntheticPostFlowTarget back afterward (see Tools/SsisExtractor/CLAUDE.md).
        var package = LoadSyntheticFixture("SyntheticPostFlowSql.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        // Emitter rewrite: one method per SSIS task/component, on the package class
        // (SyntheticPostFlowSql.cs), not Program.cs -- named after the task itself
        // (DFT_Load/SQL_PreLoad/SQL_PostLoad), called in order from RunAsync.
        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticPostFlowSql.cs");
        Assert.Contains("internal async Task DFT_Load(IUnitOfWork uow, CancellationToken ct)", classFile.Content);
        Assert.Contains("var step = new DataFlowStep<", classFile.Content);
        // The SQL text itself is no longer an inline literal -- it's a named, independently
        // testable BuildStatement() call (SqlStatementBuilderEmitter).
        Assert.Contains(
            "internal async Task SQL_PostLoad(IUnitOfWork uow, CancellationToken ct)",
            classFile.Content);
        Assert.Contains("var step = new ExecuteSqlStep(\"SQL_PostLoad\", SQL_PostLoadStatement.BuildStatement(), Log<ExecuteSqlStep>());", classFile.Content);
        Assert.Contains("await DFT_Load(uow, ct);", classFile.Content);
        Assert.Contains("await SQL_PostLoad(uow, ct);", classFile.Content);
        // The flow must run BEFORE the post-flow SQL step it follows.
        Assert.True(classFile.Content.IndexOf("await DFT_Load(uow, ct);", StringComparison.Ordinal)
                  < classFile.Content.IndexOf("await SQL_PostLoad(uow, ct);", StringComparison.Ordinal));
        // Emitter rewrite phase 2: the pre-flow statement is no longer a special hard-coded
        // pre-transaction call -- it's an ordinary method, same treatment as the post-flow one,
        // and it must run BEFORE the flow it precedes.
        Assert.Contains(
            "internal async Task SQL_PreLoad(IUnitOfWork uow, CancellationToken ct)",
            classFile.Content);
        Assert.Contains("var step = new ExecuteSqlStep(\"SQL_PreLoad\", SQL_PreLoadStatement.BuildStatement(), Log<ExecuteSqlStep>());", classFile.Content);
        Assert.True(classFile.Content.IndexOf("await SQL_PreLoad(uow, ct);", StringComparison.Ordinal)
                  < classFile.Content.IndexOf("await DFT_Load(uow, ct);", StringComparison.Ordinal));

        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        var statementFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SQL_PostLoadStatement.cs");
        Assert.Contains("public static class SQL_PostLoadStatement", statementFile.Content);
        Assert.Contains(
            "public static string BuildStatement() => \"UPDATE dbo.SyntheticPostFlowTarget SET Name = UPPER(Name);\";",
            statementFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(statementFile.Content);

        var preLoadStatementFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SQL_PreLoadStatement.cs");
        Assert.Contains("public static class SQL_PreLoadStatement", preLoadStatementFile.Content);
        Assert.Contains(
            "public static string BuildStatement() => \"TRUNCATE TABLE dbo.SyntheticPostFlowTarget;\";",
            preLoadStatementFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(preLoadStatementFile.Content);

        // The only two real gaps: the unavoidable, non-blocking Notification one, and a
        // LocalFileSourceData gap for the CSV source (Docs/Generated-Tests-Plan.md phase 3).
        Assert.Equal(2, result.Gaps.Count);
        Assert.Single(result.Gaps, g => g.Location == "SyntheticPostFlowSql.Notification");
        Assert.Single(result.Gaps, g => g.Kind == GapKind.LocalFileSourceData);
    }

    [Fact]
    public void Generate_RoutesAPostFlowExecuteSqlTask_ToItsOwnSecondaryConnection_WhenItTargetsADifferentDatabase()
    {
        // SyntheticSecondConnectionSql.dtsx: SQL_PreLoad -> DFT_Load (primary CM_Sql) ->
        // SQL_CacheSet_SecondDb (CM_SqlSecondDb, a DIFFERENT database on the same instance).
        // Reproduces the real bug found running RBC_Demo_ETL's own Package_Legacy.dtsx end to
        // end: before this, every Execute SQL Task ran through the package's ONE shared
        // connection regardless of its own connection manager, so this step's own statement
        // would have silently run against the wrong database. Confirmed beyond this test by
        // actually running the generated exe against .\SQLFORPOC_2022 (both SsisPoC and the
        // second database, SsisPoC_Secondary) and reading dbo.SyntheticSecondConnectionLog
        // back afterward -- see Tools/SsisExtractor/CLAUDE.md.
        var package = LoadSyntheticFixture("SyntheticSecondConnectionSql.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticSecondConnectionSql.cs");
        Assert.Contains("internal async Task SQL_CacheSet_SecondDb(IUnitOfWork uow, CancellationToken ct)", classFile.Content);
        Assert.Contains(
            "var connectionString = SqlConnectionStringFactory.Build(Config().GetSection(\"SecondaryConnections:CM_SqlSecondDb\").Get<DatabaseOptions>()",
            classFile.Content);
        Assert.Contains(
            "var step = new SecondaryConnectionSqlStep(\"SQL_CacheSet_SecondDb\", \"CM_SqlSecondDb\", connectionString, "
            + "SQL_CacheSet_SecondDbStatement.BuildStatement(), "
            + "Log<SecondaryConnectionSqlStep>());",
            classFile.Content);
        // Never an ordinary ExecuteSqlStep for this task -- that would run it on the wrong connection.
        Assert.DoesNotContain("new ExecuteSqlStep(\"SQL_CacheSet_SecondDb\"", classFile.Content);
        Assert.Contains("await DFT_Load(uow, ct);", classFile.Content);
        Assert.Contains("await SQL_CacheSet_SecondDb(uow, ct);", classFile.Content);

        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        var statementFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SQL_CacheSet_SecondDbStatement.cs");
        Assert.Contains(
            "public static string BuildStatement() => \"INSERT INTO dbo.SyntheticSecondConnectionLog (LogKey, LogValue) VALUES (N'LegacyImport', N'fixed-width import completed');\";",
            statementFile.Content);

        var appSettingsFile = Assert.Single(result.Files, f => f.RelativePath == "appsettings.json");
        Assert.Contains("\"SecondaryConnections\"", appSettingsFile.Content);
        Assert.Contains("\"CM_SqlSecondDb\"", appSettingsFile.Content);
        Assert.Contains("\"Database\": \"SsisPoC_Secondary\"", appSettingsFile.Content);
        // Never the password -- same rule as TargetDatabase.
        Assert.DoesNotContain("Password", appSettingsFile.Content);

        // "Secondary-connection SQL" taxonomy row (Docs/Generated-Tests-Plan.md) -- a step-level
        // starter test asserting SQL_CacheSet_SecondDb never touches the shared uow, needing no
        // real server (see TestDoublesEmitter's own unreachableSecondaryServer comment). Verified
        // for real end to end (generated, built, actually run -- both green and deliberately
        // corrupted-red) against .\SQLFORPOC_2022 -- see CLAUDE.md.
        var stepTestFile = Assert.Single(result.SiblingFiles, f => f.RelativePath == "SyntheticSecondConnectionSql.Tests/SQL_CacheSet_SecondDbStepTests.cs");
        Assert.Contains("await Assert.ThrowsAsync<SqlException>(() => package.SQL_CacheSet_SecondDb(uow, CancellationToken.None));", stepTestFile.Content);
        Assert.Contains("Assert.Empty(uow.ExecutedSql);", stepTestFile.Content);
        Assert.Contains("Assert.False(uow.BeginCalled);", stepTestFile.Content);

        // PackageHarness must wire a fake, fast-failing SecondaryConnections config entry for
        // this connection manager, or Config() would throw "not registered" before the step ever
        // gets a chance to prove it never touches uow.
        var harnessFile = Assert.Single(result.SiblingFiles, f => f.RelativePath == "SyntheticSecondConnectionSql.Tests/TestDoubles/PackageHarness.cs");
        Assert.Contains("[\"SecondaryConnections:CM_SqlSecondDb:Server\"] = \"(local)\\\\NonexistentInstance12345\",", harnessFile.Content);
        Assert.Contains("services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(secondaryConnectionConfig).Build());", harnessFile.Content);

        // The only two real gaps: the unavoidable, non-blocking Notification one, and a
        // LocalFileSourceData gap for the CSV source (Docs/Generated-Tests-Plan.md phase 3) --
        // both non-blocking, neither affects generatability.
        Assert.Equal(2, result.Gaps.Count);
        var notificationGap = Assert.Single(result.Gaps, g => g.Location == "SyntheticSecondConnectionSql.Notification");
        Assert.False(notificationGap.IsBlocking);
        var localDataGap = Assert.Single(result.Gaps, g => g.Kind == GapKind.LocalFileSourceData);
        Assert.False(localDataGap.IsBlocking);
    }

    [Fact]
    public void Generate_WiresAnOleDbSource_WhoseConnectionManagerTargetsADifferentDatabase_ViaASecondaryConnection()
    {
        // Phase 5 of the gap-audit plan (concurrent-whistling-turing.md, 2026-09-17):
        // SyntheticCrossDatabaseSource.dtsx -- OLE DB Source (CM_SourceSecondDb, resolving to
        // SsisPoC_Secondary) -> Derived Column -> OLE DB Destination (CM_Sql, resolving to
        // SsisPoC). Before this, ANY server/database mismatch between a source and its flow's
        // destination was an unconditional gap ("a separate source connection is not supported
        // yet") -- reproducing the real shape found across three GitHub portfolios (`dimcustomer`,
        // `fact_sales`, all 13 of `DailyETLMain`'s own "Extract ... to Staging" flows). Confirmed
        // beyond this test by actually running the generated exe against .\SQLFORPOC_2022 (both
        // SsisPoC and SsisPoC_Secondary) and reading dbo.SyntheticCrossDatabaseSourceTarget back
        // afterward -- see Tools/SsisExtractor/CLAUDE.md.
        var package = LoadSyntheticFixture("SyntheticCrossDatabaseSource.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticCrossDatabaseSource.cs");
        // No uow argument at all -- a secondary-connection source is never bound to the package's
        // own transaction (see SqlFlowSource's own doc comment).
        Assert.Contains("internal IRowSource<SyntheticCrossDatabaseSourceTargetSqlRow> OLEDBSource()", classFile.Content);
        Assert.Contains(
            "var connectionString = SqlConnectionStringFactory.Build(Config().GetSection(\"SecondaryConnections:CM_SourceSecondDb\").Get<DatabaseOptions>()",
            classFile.Content);
        Assert.Contains(
            "return new SqlRowSource<SyntheticCrossDatabaseSourceTargetSqlRow>(\"OLE DB Source\", "
            + "new SqlSourceOptions { ConnectionString = connectionString, CommandText = \"SELECT ID, Name FROM dbo.SyntheticCrossDatabaseSourceInput\" }, "
            + "SyntheticCrossDatabaseSourceTargetSqlRowReader.Read);",
            classFile.Content);
        // The call site passes no argument either -- SourceCallArgs mirrors SourceNeedsUow.
        Assert.Contains("var source = OLEDBSource();", classFile.Content);

        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        var appSettingsFile = Assert.Single(result.Files, f => f.RelativePath == "appsettings.json");
        Assert.Contains("\"SecondaryConnections\"", appSettingsFile.Content);
        Assert.Contains("\"CM_SourceSecondDb\"", appSettingsFile.Content);
        Assert.Contains("\"Database\": \"SsisPoC_Secondary\"", appSettingsFile.Content);

        // Both generated starter-test call sites (the standalone "Source -- SQL" test and the
        // "Data Flow Task -- direct invocation" test) must ALSO drop the uow argument -- a real,
        // previously-latent bug this feature surfaced: PackageGenerator.FlowSourceNeedsUow (a
        // duplicate of PackageClassEmitter.SourceNeedsUow for test-emission purposes) still
        // returned true unconditionally for ANY SqlFlowSource, producing a starter test calling
        // OLEDBSource(uow) against a method that takes no arguments at all (CS1501) -- caught only
        // by actually building the generated project, not by this gap-count/report check alone.
        var sourceTestFile = Assert.Single(result.SiblingFiles, f => f.RelativePath == "SyntheticCrossDatabaseSource.Tests/OLEDBSourceSourceTests.cs");
        Assert.Contains("var source = package.OLEDBSource();", sourceTestFile.Content);
        Assert.DoesNotContain("harness.NewUnitOfWork()", sourceTestFile.Content);
        Assert.DoesNotContain("NewRealUnitOfWorkAsync", sourceTestFile.Content);

        var dataFlowTestFile = Assert.Single(result.SiblingFiles, f => f.RelativePath == "SyntheticCrossDatabaseSource.Tests/DFT_LoadStepDataFlowTests.cs");
        Assert.Contains("var source = package.OLEDBSource();", dataFlowTestFile.Content);

        // Only the two unavoidable, non-blocking advisories -- no cross-database gap at all.
        Assert.Equal(2, result.Gaps.Count);
        Assert.All(result.Gaps, g => Assert.False(g.IsBlocking));
        Assert.DoesNotContain(result.Gaps, g => g.Reason.Contains("separate source connection is not supported"));
    }

    [Fact]
    public void Generate_WiresTwoExpressionTaskAssignments_ForTheSyntheticExpressionTaskFixture()
    {
        // SyntheticExpressionTask.dtsx: Phase 2 of the unsupported-component-types plan
        // (Microsoft.ExpressionTask). EXPR_SetCutoff computes a DateTime from DATEADD/GETUTCDATE
        // (the real evidenced call from DailyETLMain.dtsx); EXPR_SetTableName assigns a plain
        // string literal. A downstream Execute SQL Task is gated on the SECOND assignment's own
        // effect, proving the ExpressionTask's Set() and the guard's own Get() resolve the exact
        // same "packageVariables" field. Confirmed beyond this test by actually running the
        // generated exe against .\SQLFORPOC_2022 and reading dbo.SyntheticExpressionTaskTarget/
        // dbo.SyntheticExpressionTaskLog back afterward -- see Tools/SsisExtractor/CLAUDE.md.
        var package = LoadSyntheticFixture("SyntheticExpressionTask.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticExpressionTask.cs");
        Assert.Contains("internal async Task EXPR_SetCutoff(IUnitOfWork uow, CancellationToken ct)", classFile.Content);
        Assert.Contains(
            "var step = new ExpressionTaskStep(\"EXPR_SetCutoff\", () => packageVariables.Set(\"User::TargetETLCutoffTime\", DateTime.UtcNow.AddMinutes(-(5))), Log<ExpressionTaskStep>());",
            classFile.Content);

        Assert.Contains("internal async Task EXPR_SetTableName(IUnitOfWork uow, CancellationToken ct)", classFile.Content);
        Assert.Contains(
            "var step = new ExpressionTaskStep(\"EXPR_SetTableName\", () => packageVariables.Set(\"User::TableName\", \"City\"), Log<ExpressionTaskStep>());",
            classFile.Content);

        Assert.Contains("await EXPR_SetCutoff(uow, ct);", classFile.Content);
        Assert.Contains("await EXPR_SetTableName(uow, ct);", classFile.Content);

        // The guard reads back the same variable EXPR_SetTableName wrote -- proves the two
        // features share one GuardVariable table, not two independently-derived namings.
        Assert.Contains("packageVariables.GetRequired<string>(\"User::TableName\")", classFile.Content);

        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        // The only two real gaps: the unavoidable, non-blocking Notification one, and a
        // LocalFileSourceData gap for the CSV source (Docs/Generated-Tests-Plan.md phase 3) --
        // neither related to ExpressionTask at all.
        Assert.Equal(2, result.Gaps.Count);
        var notificationGap = Assert.Single(result.Gaps, g => g.Location == "SyntheticExpressionTask.Notification");
        Assert.False(notificationGap.IsBlocking);
        var localDataGap = Assert.Single(result.Gaps, g => g.Kind == GapKind.LocalFileSourceData);
        Assert.False(localDataGap.IsBlocking);
    }

    [Fact]
    public void Generate_WiresAnErrorRedirectDestination_WithBothSinksNamedAfterTheirOwnComponent()
    {
        // SyntheticErrorRedirect.dtsx: OLE DB Source -> OLEDST_Target (ErrorRowDisposition=
        // RedirectRow) -> (error output) -> OLEDST_Errors. Closes the real silent-drop bug found
        // reviewing RBC_Demo_ETL's own real Package.dtsx (DFT_LoadCustomers's own
        // OLEDST_StagingErrors) -- see Tools/SsisExtractor/CLAUDE.md's own account. Confirmed
        // beyond this test by a real dtexec probe against .\SQLFORPOC_2022 (measuring real SSIS's
        // own row-by-row-under-FastLoadMaxInsertCommitSize=1 redirect behavior) AND by actually
        // running the generated exe against the same database -- exact row-for-row match,
        // ErrorCode carrying the real SQL Server error number (2627, a unique-constraint
        // violation), not a guess.
        var package = LoadSyntheticFixture("SyntheticErrorRedirect.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticErrorRedirect.cs");
        // Both destinations named after their own SSIS component -- unlike a plain single-
        // destination flow, which is still named after the entity ({Entity}Destination).
        Assert.Contains("internal IBulkSink<SyntheticErrorRedirectTarget> OLEDST_Target()", classFile.Content);
        Assert.Contains("internal IBulkSink<SyntheticErrorRedirectTargetError> OLEDST_Errors()", classFile.Content);
        Assert.Contains("var sink = OLEDST_Target();", classFile.Content);
        Assert.Contains(
            "return new RedirectingSqlSink<SyntheticErrorRedirectTarget, SyntheticErrorRedirectTargetError>(",
            classFile.Content);
        Assert.Contains("OLEDST_Errors(), SyntheticErrorRedirectTargetErrorMap.Map,", classFile.Content);
        Assert.Contains(
            "return new SqlBulkSink<SyntheticErrorRedirectTargetError>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<SyntheticErrorRedirectTargetError>>());",
            classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        // The error entity: business columns (Code/Name) plus ErrorCode/ErrorColumn (genuine
        // mapped <inputColumn>s, lineage-traced to the primary's own error output) plus FailedAt
        // (EntityEmitter's new extraProperties path -- no <inputColumn> for it at all, a DB-side
        // default in the real schema). No ErrorRowID -- the identity PK, never mapped, correctly
        // invisible.
        var errorEntityFile = Assert.Single(result.Files, f => f.RelativePath == "Model/SyntheticErrorRedirectTargetError.cs");
        Assert.Contains("public string Code { get; set; }", errorEntityFile.Content);
        Assert.Contains("public string Name { get; set; }", errorEntityFile.Content);
        Assert.Contains("public int ErrorCode { get; set; }", errorEntityFile.Content);
        Assert.Contains("public int ErrorColumn { get; set; }", errorEntityFile.Content);
        Assert.Contains("public DateTime FailedAt { get; set; }", errorEntityFile.Content);
        Assert.DoesNotContain("ErrorRowID", errorEntityFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(errorEntityFile.Content);

        // The error map: business columns copied from the FAILED primary entity (never
        // re-derived via lineage through the transform), diagnostic columns set by the sink
        // itself. ErrorColumn deliberately left at 0 -- SSIS's own lineage-id semantics have no
        // equivalent once redirected through a raw ADO.NET exception.
        var mapFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticErrorRedirectTargetErrorMap.cs");
        Assert.Contains("internal static SyntheticErrorRedirectTargetError Map(SyntheticErrorRedirectTarget failed, DbException ex) => new()", mapFile.Content);
        Assert.Contains("Code = failed.Code,", mapFile.Content);
        Assert.Contains("Name = failed.Name,", mapFile.Content);
        Assert.Contains("FailedAt = DateTime.UtcNow,", mapFile.Content);
        Assert.Contains("ErrorCode = DbExceptionErrorCode.Resolve(ex),", mapFile.Content);
        Assert.Contains("ErrorColumn = 0,", mapFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(mapFile.Content);

        // Both destinations get their own DbContext table registration.
        var dbContextFile = Assert.Single(result.Files, f => f.RelativePath == "Model/SyntheticErrorRedirectDbContext.cs");
        Assert.Contains("modelBuilder.Entity<SyntheticErrorRedirectTarget>", dbContextFile.Content);
        Assert.Contains("modelBuilder.Entity<SyntheticErrorRedirectTargetError>", dbContextFile.Content);

        // Only the two unavoidable/non-blocking advisories -- no blocking gap at all.
        Assert.All(result.Gaps, g => Assert.False(g.IsBlocking));
    }

    [Fact]
    public void Generate_WiresAnOleDbSourcedFlow_IntoProgramCsViaSqlRowSource()
    {
        // SyntheticOleDbSourceTransform.dtsx: OLE DB Source (SqlCommand, AccessMode=2) ->
        // Derived Column -> OLE DB Destination. Confirmed beyond this test by actually
        // running the generated exe against .\SQLFORPOC_2022 and reading
        // dbo.SyntheticOleDbSourceTarget back afterward (see Tools/SsisExtractor/CLAUDE.md).
        var package = LoadSyntheticFixture("SyntheticOleDbSourceTransform.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        Assert.Contains(result.Files, f => f.RelativePath == "Sql/SyntheticOleDbSourceTargetSqlRow.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Sql/SyntheticOleDbSourceTargetSqlRowReader.cs");

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticOleDbSourceTransform.cs");
        Assert.Contains("using Etl.Core.Data;", classFile.Content);
        // Phase 4: constructed in its own method, named after the SSIS component itself
        // (sanitized to a valid identifier -- "OLE DB Source" has no letters/digits/underscore
        // stripped, so it becomes "OLEDBSource"), called from the flow's own method rather than
        // inlined -- uow is passed straight through (no DI resolution), and the connection
        // string comes from the Db() helper.
        Assert.Contains("var source = OLEDBSource(uow);", classFile.Content);
        Assert.Contains("internal IRowSource<SyntheticOleDbSourceTargetSqlRow> OLEDBSource(IUnitOfWork uow)", classFile.Content);
        Assert.Contains("return new SqlRowSource<SyntheticOleDbSourceTargetSqlRow>(\"OLE DB Source\", new SqlSourceOptions { ConnectionString = SqlConnectionStringFactory.Build(Db()), CommandText = \"SELECT ID, Amount FROM dbo.SyntheticOleDbSourceInput\" }, SyntheticOleDbSourceTargetSqlRowReader.Read, uow);", classFile.Content);

        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        // Two expected gaps: the SqlCommand-mode column-name assumption, and the unavoidable
        // Notification one.
        Assert.Equal(2, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Reason.Contains("assumes each result-set column is named/aliased"));
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticOleDbSourceTransform.Notification");
    }

    [Fact]
    public void Generate_TranslatesADataConversion_IntoSsisFnCallsAgainstTheRawSourceColumn()
    {
        // SyntheticDataConversion.dtsx: OLE DB Source (SqlCommand: ID, CustomerIdText,
        // SignupDateText, all strings) -> Data Conversion (DCONV_Types: CustomerIdText ->
        // CustomerId_i4 DT_I4, SignupDateText -> SignupDate_dt DT_DBDATE, both dispositions
        // IgnoreFailure) -> OLE DB Destination. No Derived Column anywhere -- proves the
        // "no Derived Column or Data Conversion found" gate correctly widened rather than
        // blocking this flow. Confirmed beyond this test by actually running the generated
        // exe against .\SQLFORPOC_2022 (seeded with a valid row, a whitespace-padded valid
        // row, an invalid row, an empty-string row, and a NULL row) and reading
        // dbo.SyntheticDataConversionTarget back afterward, matching a real dtexec run of this
        // same fixture row for row -- see Tools/SsisExtractor/CLAUDE.md's own "Data Conversion
        // component" section.
        var package = LoadSyntheticFixture("SyntheticDataConversion.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var entityFile = Assert.Single(result.Files, f => f.RelativePath == "Model/SyntheticDataConversionTarget.cs");
        Assert.Contains("public int? CustomerId_i4 { get; set; }", entityFile.Content);
        Assert.Contains("public DateOnly? SignupDate_dt { get; set; }", entityFile.Content);

        var rowFile = Assert.Single(result.Files, f => f.RelativePath == "Sql/SyntheticDataConversionTargetSqlRow.cs");
        Assert.Contains("public string? CustomerIdText { get; set; }", rowFile.Content);
        Assert.Contains("public string? SignupDateText { get; set; }", rowFile.Content);

        var readerFile = Assert.Single(result.Files, f => f.RelativePath == "Sql/SyntheticDataConversionTargetSqlRowReader.cs");
        Assert.Contains("reader.IsDBNull(reader.GetOrdinal(\"CustomerIdText\")) ? null : reader.GetFieldValue<string>(reader.GetOrdinal(\"CustomerIdText\"))", readerFile.Content);

        var transformFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticDataConversionTargetTransform.cs");
        Assert.Contains("CustomerId_i4 = SsisFn.ToNullableI4(row.CustomerIdText),", transformFile.Content);
        Assert.Contains("SignupDate_dt = SsisFn.ToNullableDate(row.SignupDateText),", transformFile.Content);

        var ssisFnFile = Assert.Single(result.Files, f => f.RelativePath == "Ssis/SsisFn.cs");
        Assert.Contains("public static int? ToNullableI4(string? value) =>", ssisFnFile.Content);
        Assert.Contains("public static DateOnly? ToNullableDate(string? value) =>", ssisFnFile.Content);

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(entityFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(rowFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(readerFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(transformFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(ssisFnFile.Content);

        // Two expected gaps: the SqlCommand-mode column-name assumption, and the unavoidable
        // Notification one -- same shape as every other SqlCommand-sourced flow's own test.
        Assert.Equal(2, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Reason.Contains("assumes each result-set column is named/aliased"));
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticDataConversion.Notification");
    }

    [Fact]
    public void Generate_ResolvesACopyMapColumn_AsAPlainPassthroughAgainstTheRawSourceColumn()
    {
        // Phase 1 of the unsupported-component-types plan: SyntheticCopyMap.dtsx -- OLE DB
        // Source (SqlCommand: ID, FullName) -> Copy Column (CPY_FullName: FullName ->
        // FullNameCopy, a pure duplicate, no cast/expression) -> OLE DB Destination. No Derived
        // Column/Data Conversion anywhere -- proves the single-destination direct-copy path
        // resolves a Copy Column column correctly (not just the pre-existing "no Derived Column
        // found" gate widening those two components needed). Confirmed beyond this test by
        // actually running the generated exe against .\SQLFORPOC_2022 and reading
        // dbo.SyntheticCopyMapTarget back afterward -- see this project's own CLAUDE.md.
        var package = LoadSyntheticFixture("SyntheticCopyMap.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var entityFile = Assert.Single(result.Files, f => f.RelativePath == "Model/SyntheticCopyMapTarget.cs");
        Assert.Contains("public string FullName { get; set; }", entityFile.Content);
        Assert.Contains("public string FullNameCopy { get; set; }", entityFile.Content);

        var rowFile = Assert.Single(result.Files, f => f.RelativePath == "Sql/SyntheticCopyMapTargetSqlRow.cs");
        Assert.Contains("public string FullName { get; set; }", rowFile.Content);
        // The row type must NOT have a FullNameCopy property of its own -- it's resolved as a
        // plain passthrough against the TRUE source column, not a same-named row property.
        Assert.DoesNotContain("FullNameCopy", rowFile.Content);

        var transformFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticCopyMapTargetTransform.cs");
        Assert.Contains("FullName = row.FullName,", transformFile.Content);
        Assert.Contains("FullNameCopy = row.FullName,", transformFile.Content);

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(entityFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(rowFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(transformFile.Content);

        // Two expected gaps: the SqlCommand-mode column-name assumption, and the unavoidable
        // Notification one -- same shape as every other SqlCommand-sourced flow's own test.
        Assert.Equal(2, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Reason.Contains("assumes each result-set column is named/aliased"));
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticCopyMap.Notification");
    }

    [Fact]
    public void Generate_TranslatesTheThreeSpeculativeDataConversionTargets_R8BoolWstr()
    {
        // SyntheticDataConversionTypes.dtsx: OLE DB Source (SqlCommand: ID, R8Text, WstrText,
        // BoolText, all strings) -> Data Conversion (DCONV_Types: R8Text -> ValR8 DT_R8,
        // WstrText -> ValWstr DT_WSTR,10, BoolText -> ValBool DT_BOOL, all dispositions
        // IgnoreFailure) -> OLE DB Destination. Built speculatively (2026-08-28, with the
        // user's sign-off) -- no real package needs any of these three target types, unlike
        // SyntheticDataConversion.dtsx's own DT_I4/DT_DBDATE pair. Confirmed beyond this test by
        // actually running the generated exe against .\SQLFORPOC_2022 and reading
        // dbo.SyntheticDataConversionTypesTarget back afterward, matching a real dtexec run of
        // this same fixture row for row -- see Tools/SsisExtractor/CLAUDE.md's own
        // "Numeric-passthrough-coercion" / Data Conversion speculative-types section.
        var package = LoadSyntheticFixture("SyntheticDataConversionTypes.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var entityFile = Assert.Single(result.Files, f => f.RelativePath == "Model/SyntheticDataConversionTypesTarget.cs");
        Assert.Contains("public double? ValR8 { get; set; }", entityFile.Content);
        Assert.Contains("public string? ValWstr { get; set; }", entityFile.Content);
        Assert.Contains("public bool? ValBool { get; set; }", entityFile.Content);

        var transformFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticDataConversionTypesTargetTransform.cs");
        Assert.Contains("ValR8 = SsisFn.ToNullableR8(row.R8Text),", transformFile.Content);
        // Truncates to the declared target width (10) -- the one genuinely different failure
        // mode among the three, not a NULL-on-failure shape.
        Assert.Contains("ValWstr = SsisFn.ToWstr(row.WstrText, 10),", transformFile.Content);
        Assert.Contains("ValBool = SsisFn.ToNullableBool(row.BoolText),", transformFile.Content);

        var ssisFnFile = Assert.Single(result.Files, f => f.RelativePath == "Ssis/SsisFn.cs");
        Assert.Contains("public static double? ToNullableR8(string? value) =>", ssisFnFile.Content);
        Assert.Contains("public static bool? ToNullableBool(string? value)", ssisFnFile.Content);
        Assert.Contains("public static string? ToWstr(string? value, int maxLength) =>", ssisFnFile.Content);

        CodeAssertions.AssertNoSyntaxErrors(entityFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(transformFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(ssisFnFile.Content);

        // Two expected gaps: the SqlCommand-mode column-name assumption, and the unavoidable
        // Notification one -- same shape as every other SqlCommand-sourced flow's own test.
        Assert.Equal(2, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Reason.Contains("assumes each result-set column is named/aliased"));
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticDataConversionTypes.Notification");
    }

    [Fact]
    public void Generate_TranslatesThreeMoreSpeculativeDataConversionTargets_I2I8DbTimestamp()
    {
        // SyntheticDataConversionTypes2.dtsx: OLE DB Source (SqlCommand: ID, I2Text, I8Text,
        // TimestampText, all strings) -> Data Conversion (DCONV_Types2: I2Text -> Val_i2 DT_I2,
        // I8Text -> Val_i8 DT_I8, TimestampText -> Val_dt DT_DBTIMESTAMP, all dispositions
        // IgnoreFailure) -> OLE DB Destination. Built speculatively 2026-08-30 -- no real
        // package needs any of these three target types. Confirmed beyond this test by actually
        // running the generated exe against .\SQLFORPOC_2022 and reading
        // dbo.SyntheticDataConversionTypes2Target back afterward, matching a real dtexec run of
        // this same fixture row for row.
        var package = LoadSyntheticFixture("SyntheticDataConversionTypes2.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var entityFile = Assert.Single(result.Files, f => f.RelativePath == "Model/SyntheticDataConversionTypes2Target.cs");
        Assert.Contains("public short? Val_i2 { get; set; }", entityFile.Content);
        Assert.Contains("public long? Val_i8 { get; set; }", entityFile.Content);
        Assert.Contains("public DateTime? Val_dt { get; set; }", entityFile.Content);

        var transformFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticDataConversionTypes2TargetTransform.cs");
        Assert.Contains("Val_i2 = SsisFn.ToNullableI2(row.I2Text),", transformFile.Content);
        Assert.Contains("Val_i8 = SsisFn.ToNullableI8(row.I8Text),", transformFile.Content);
        Assert.Contains("Val_dt = SsisFn.ToNullableDateTime(row.TimestampText),", transformFile.Content);

        var ssisFnFile = Assert.Single(result.Files, f => f.RelativePath == "Ssis/SsisFn.cs");
        Assert.Contains("public static short? ToNullableI2(string? value) =>", ssisFnFile.Content);
        Assert.Contains("public static long? ToNullableI8(string? value) =>", ssisFnFile.Content);
        Assert.Contains("public static DateTime? ToNullableDateTime(string? value) =>", ssisFnFile.Content);

        CodeAssertions.AssertNoSyntaxErrors(entityFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(transformFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(ssisFnFile.Content);

        Assert.Equal(2, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Reason.Contains("assumes each result-set column is named/aliased"));
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticDataConversionTypes2.Notification");
    }

    [Fact]
    public void Generate_ResolvesADataConversionColumn_ReferencedByAConditionalSplitConditionAndAPostSplitDerivedColumn()
    {
        // SyntheticDataConversionSplit.dtsx: OLE DB Source -> Data Conversion (CustomerIdText ->
        // CustomerId_i4, SignupDateText -> SignupDate_dt) -> Conditional Split
        // (!ISNULL(CustomerId_i4) -> Valid; default -> Invalid) -> each branch's own Derived
        // Column (TenureDays <- ISNULL(SignupDate_dt) ? -1 : DATEDIFF(...)) -> Union All -> one
        // destination. Proves the two Data-Conversion cross-reference paths
        // SyntheticDataConversion.dtsx alone never exercises (its converted columns flow
        // straight to a destination, referenced by nothing else) -- the exact shape RBC_Demo_ETL's
        // own Package_Transforms.dtsx (DFT_DerivedAndSplit) needs. Confirmed beyond this test by
        // actually running the generated exe against .\SQLFORPOC_2022 (seeded with a valid row,
        // a valid-CustomerID/NULL-date row, and an invalid-CustomerID row) and reading
        // dbo.SyntheticDataConversionSplitTarget back afterward -- see CLAUDE.md's own "Data
        // Conversion component" section.
        var package = LoadSyntheticFixture("SyntheticDataConversionSplit.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var routerFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/CSPLIT_ValidityRouter.cs");
        Assert.Contains("!((SsisFn.ToNullableI4(row.CustomerIdText) is null))", routerFile.Content);

        var validTransformFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/CSPLIT_Validity_Valid.cs");
        Assert.Contains("SignupDate_dt = SsisFn.ToNullableDate(row.SignupDateText),", validTransformFile.Content);
        // TenureDays is a genuine Derived Column output, so it's now its own named, callable
        // function on a static holder class named after the real SSIS component (DER_InvalidTenure
        // -- both branches resolve to the SAME real component here, so both correctly reference
        // it identically) rather than inlined or duplicated -- unlike SignupDate_dt above (a Data
        // Conversion column, deliberately left as a single already-named SsisFn.* call, not
        // double-wrapped). The null-forgiving "!" before .Value in the non-null ternary branch --
        // without it, calling SsisFn.ToNullableDate twice (once for the ISNULL check, once here)
        // is a build ERROR (CS8629) under this project's Nullable+TreatWarningsAsErrors, since
        // Roslyn's flow analysis never narrows a repeated METHOD CALL the way it narrows a plain
        // row-property reference. Caught only by actually building the generated project, not
        // unit tests alone.
        Assert.Contains("TenureDays = DER_InvalidTenure.TenureDays(row, ctx),", validTransformFile.Content);
        var tenureHolder = Assert.Single(result.Files, f => f.RelativePath == "Mapping/DER_InvalidTenure.cs");
        Assert.Contains("public static int TenureDays(DFT_DataConversionSplitDemoSqlRow row, in RowContext ctx) => ((SsisFn.ToNullableDate(row.SignupDateText) is null) ? -(1) : SsisFn.DateDiffDays(SsisFn.ToNullableDate(row.SignupDateText)!.Value, ctx.LoadedAtUtc));", tenureHolder.Content);
        CodeAssertions.AssertNoSyntaxErrors(tenureHolder.Content);

        var invalidTransformFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/CSPLIT_Validity_Invalid.cs");
        Assert.Contains("SignupDate_dt = SsisFn.ToNullableDate(row.SignupDateText),", invalidTransformFile.Content);
        Assert.Contains("TenureDays = DER_InvalidTenure.TenureDays(row, ctx),", invalidTransformFile.Content);

        var entityFile = Assert.Single(result.Files, f => f.RelativePath == "Model/SyntheticDataConversionSplitTarget.cs");
        Assert.Contains("public int? CustomerId_i4 { get; set; }", entityFile.Content);
        Assert.Contains("public DateOnly? SignupDate_dt { get; set; }", entityFile.Content);

        // The raw source columns feeding the conversion are just as capable of being NULL --
        // both need the same IsDBNull-guarded reader, a real bug caught only by actually running
        // the generated code against a seeded NULL SignupDateText row (ID=2 in the backing
        // table), not by this test alone (see ResolveNullableColumnNames's own doc comment for
        // why it must run before ResolveFlowSource in this Conditional Split code path).
        var readerFile = Assert.Single(result.Files, f => f.RelativePath == "Sql/DFT_DataConversionSplitDemoSqlRowReader.cs");
        Assert.Contains("CustomerIdText = reader.IsDBNull(reader.GetOrdinal(\"CustomerIdText\")) ? null : reader.GetFieldValue<string>(reader.GetOrdinal(\"CustomerIdText\")),", readerFile.Content);
        Assert.Contains("SignupDateText = reader.IsDBNull(reader.GetOrdinal(\"SignupDateText\")) ? null : reader.GetFieldValue<string>(reader.GetOrdinal(\"SignupDateText\")),", readerFile.Content);

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(routerFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(validTransformFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(invalidTransformFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(entityFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(readerFile.Content);

        // Five expected gaps: the SqlCommand-mode column-name assumption, the unavoidable
        // Notification one, a non-blocking one from RouterTestEmitter's own pilot scope (its
        // representative-row oracle evaluation has no way to resolve CustomerId_i4, a
        // Data-Conversion-produced value deliberately excluded from the row it builds, so the
        // condition's own evaluation throws "unresolved reference" and this pilot skips the
        // whole router starter test rather than guessing), and -- since TransformTestEmitter is
        // now called once per Conditional Split branch (Docs/Generated-Tests-Plan.md's own
        // "extended to every flow path" promise, closed for this shape) -- TWO cross-reference
        // gaps for TenureDays, one per branch (Valid/Invalid), both hitting the identical
        // "not attempted" pilot-scope limit the single-destination case already states, since
        // BOTH branches independently reference the same Data-Conversion-produced SignupDate_dt.
        Assert.Equal(5, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Reason.Contains("assumes each result-set column is named/aliased"));
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticDataConversionSplit.Notification");
        Assert.Contains(result.Gaps, g => g.Location == "CSPLIT_ValidityRouter" && g.Reason.Contains("unresolved reference"));
        Assert.Equal(2, result.Gaps.Count(g => g.Location == "SyntheticDataConversionSplitTarget.TenureDays" && g.Reason.Contains("cross-reference", StringComparison.Ordinal)));
    }

    [Fact]
    public void Generate_ResolvesAStringSourceIntDestinationPassthroughColumn_ViaParseWstrToI4()
    {
        // SyntheticStringToIntCoercion.dtsx: OLE DB Source (SqlCommand, an NVARCHAR "Val"
        // column) -> OLE DB Destination (an int "Val" column), a plain passthrough with no
        // Data Conversion/Derived Column in between -- the exact real shape RBC_Demo_ETL's own
        // Package_Exports/DFT_AdoNetRoundTrip has (CustomerID: wstr,20 from an ADO NET Source,
        // i4 at CustomerExportLog). Confirmed beyond this test by actually running the generated
        // exe against .\SQLFORPOC_2022 and reading dbo.SyntheticStringToIntCoercionTarget back
        // afterward -- see CLAUDE.md's own "Numeric-passthrough-coercion for OLE DB/ADO NET
        // destinations" section for the r8->i4 precedent and this same section's own
        // string->int follow-up.
        var package = LoadSyntheticFixture("SyntheticStringToIntCoercion.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var transformFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticStringToIntCoercionTargetTransform.cs");
        Assert.Contains("Val = SsisFn.ParseWstrToI4(row.Val),", transformFile.Content);

        // ParseWstrToI4 always returns int? (string/string? share one runtime type, so unlike
        // NarrowR8ToI4's double/double? pair there is no non-nullable overload to fall back on)
        // -- the destination entity property must be nullable BY CONSTRUCTION, resolved BEFORE
        // EntityEmitter runs (PackageGenerator.ResolveNullableColumnNames), not just inside
        // TransformEmitter's own later mismatch detection.
        var entityFile = Assert.Single(result.Files, f => f.RelativePath == "Model/SyntheticStringToIntCoercionTarget.cs");
        Assert.Contains("public int? Val { get; set; }", entityFile.Content);

        // The raw source column is just as capable of being NULL in real data (confirmed
        // empirically -- ID=7 in the backing table) -- SqlRowReaderEmitter needs the same
        // IsDBNull guard a Data Conversion's own raw source column already gets.
        var readerFile = Assert.Single(result.Files, f => f.RelativePath == "Sql/SyntheticStringToIntCoercionTargetSqlRowReader.cs");
        Assert.Contains("Val = reader.IsDBNull(reader.GetOrdinal(\"Val\")) ? null : reader.GetFieldValue<string>(reader.GetOrdinal(\"Val\")),", readerFile.Content);

        var ssisFnFile = Assert.Single(result.Files, f => f.RelativePath == "Ssis/SsisFn.cs");
        Assert.Contains("public static int? ParseWstrToI4(string? value) =>", ssisFnFile.Content);

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        CodeAssertions.AssertNoSyntaxErrors(transformFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(entityFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(readerFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(ssisFnFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

        // Two expected gaps: the SqlCommand-mode column-name assumption, and the unavoidable
        // Notification one -- same shape as every other SqlCommand-sourced flow's own test.
        Assert.Equal(2, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Reason.Contains("assumes each result-set column is named/aliased"));
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticStringToIntCoercion.Notification");
    }

    [Fact]
    public void Generate_ProducesAReferenceTableCacheScaffold_ForALookup_ButDoesNotWireItIntoProgramCs()
    {
        // SyntheticLookupSplit.dtsx: OLE DB Source -> Lookup -> Conditional Split -> 3 OLE DB
        // Destinations. Conditional Split's mere presence causes no separate gap (nothing
        // inspects ConditionalSplitPayload yet) -- this test is purely about the Lookup gate.
        var package = LoadSyntheticFixture("SyntheticLookupSplit.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var cacheFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/LookupCache.cs");
        Assert.Contains("public static class LookupCache", cacheFile.Content);
        Assert.Contains("public sealed class ReferenceRow", cacheFile.Content);
        Assert.Contains("public int CustomerID { get; set; }", cacheFile.Content);
        Assert.Contains("command.CommandText = \"SELECT CustomerID, CustomerName, Region FROM dbo.SyntheticCustomer\";", cacheFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(cacheFile.Content);

        // No Program.cs at all -- the Lookup flow is this fixture's only Data Flow Task, and
        // it was correctly never wired.
        Assert.DoesNotContain(result.Files, f => f.RelativePath == "Program.cs");

        // The reason changed 2026-08-31 and the correction matters: SSIS DOES persist a Lookup's
        // join key (on the input column's own JoinToReferenceColumn property). THIS fixture has
        // none only because it was built through the object model without ever mapping its
        // Lookup's input columns -- which is precisely what was previously mistaken for the format
        // never carrying one. The test's own intent is unchanged: a Lookup with no resolvable join
        // key still gets its cache scaffold and still is not wired.
        Assert.Contains(result.Gaps, g => g.Reason.Contains("declares no join key")
            && g.Reason.Contains("Mapping/LookupCache.cs"));
    }

    [Fact]
    public void Generate_MergesTwoBranchesIntoOneSharedEntityAndTable_WhenAUnionAllRemergesThem()
    {
        // SyntheticConditionalSplitRemerge.dtsx: two branches, each through their own "tag"
        // Derived Column, remerge via a Union All into ONE shared OLE DB Destination -- the
        // real-world shape RBC_Demo_ETL's Package_Transforms.dtsx (DFT_DerivedAndSplit) has.
        // Confirmed beyond this test by actually building and running the generated exe
        // against .\SQLFORPOC_2022 and reading dbo.SyntheticRemergeTarget back afterward,
        // including the exact "Amount > 1000" boundary row (ID=3, Amount=1000.00 -> Low, not
        // High) -- see Tools/SsisExtractor/CLAUDE.md's "Conditional Split -- multi-hop Union
        // All remerge" section.
        var package = LoadSyntheticFixture("SyntheticConditionalSplitRemerge.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        // ONE entity/table, not two -- both branches converge on the same destination.
        Assert.Single(result.Files, f => f.RelativePath == "Model/SyntheticRemergeTarget.cs");

        // Two DISTINCT transforms, disambiguated by branch output name since they'd otherwise
        // both be "SyntheticRemergeTargetTransform".
        var highFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/CSPLIT_Amount_High.cs");
        var lowFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/CSPLIT_Amount_Low.cs");

        // Each transform combines the flow's shared Derived Column (LoadedAtUtc) with its OWN
        // per-branch one (Segment) -- proving TransformEmitter's merged-DerivedColumns list
        // resolves columns from both, not just one. Both are genuine Derived Column outputs, so
        // each is now its own named, callable function on a static holder class named after the
        // real SSIS component -- and the SHARED one (DER_LoadedAtUtc) is referenced identically
        // from BOTH branches, proving the 1-to-1 component-to-function mapping round's own
        // de-duplication mechanism: exactly ONE Mapping/DER_LoadedAtUtc.cs file is emitted (see
        // the assertion below), not one duplicated per branch.
        Assert.Contains("LoadedAtUtc = DER_LoadedAtUtc.LoadedAtUtc(row, ctx),", highFile.Content);
        Assert.Contains("Segment = DER_TagHigh.Segment(row, ctx),", highFile.Content);
        Assert.Contains("LoadedAtUtc = DER_LoadedAtUtc.LoadedAtUtc(row, ctx),", lowFile.Content);
        Assert.Contains("Segment = DER_TagLow.Segment(row, ctx),", lowFile.Content);
        var sharedHolder = Assert.Single(result.Files, f => f.RelativePath == "Mapping/DER_LoadedAtUtc.cs");
        Assert.Contains("public static DateTime LoadedAtUtc(DFT_ConditionalSplitRemergeDemoSqlRow row, in RowContext ctx) => ctx.LoadedAtUtc;", sharedHolder.Content);
        var highTagHolder = Assert.Single(result.Files, f => f.RelativePath == "Mapping/DER_TagHigh.cs");
        Assert.Contains("public static string Segment(DFT_ConditionalSplitRemergeDemoSqlRow row, in RowContext ctx) => \"High\";", highTagHolder.Content);
        var lowTagHolder = Assert.Single(result.Files, f => f.RelativePath == "Mapping/DER_TagLow.cs");
        Assert.Contains("public static string Segment(DFT_ConditionalSplitRemergeDemoSqlRow row, in RowContext ctx) => \"Low\";", lowTagHolder.Content);
        CodeAssertions.AssertNoSyntaxErrors(sharedHolder.Content);
        CodeAssertions.AssertNoSyntaxErrors(highTagHolder.Content);
        CodeAssertions.AssertNoSyntaxErrors(lowTagHolder.Content);
        CodeAssertions.AssertNoSyntaxErrors(highFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(lowFile.Content);

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticConditionalSplitRemerge.cs");
        // No IRowTransform<> DI registration at all -- see PackageClassEmitter's own comment on
        // why (two branches sharing an entity would collide on the same closed generic if this
        // were DI-resolved), and no AddBulkSink<>()/DI resolution for the sink either -- each
        // branch constructs its own SqlBulkSink<SyntheticRemergeTarget> directly.
        Assert.DoesNotContain("AddBulkSink", classFile.Content);
        Assert.DoesNotContain("AddScoped<IRowTransform<", classFile.Content);
        Assert.Contains("new CSPLIT_Amount_High(),", classFile.Content);
        Assert.Contains("new CSPLIT_Amount_Low(),", classFile.Content);
        // Both branches share the one shared table, each constructing its own sink.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(classFile.Content, "new SqlBulkSink<SyntheticRemergeTarget>\\(Opt<BulkCopyOptions>\\(\\), Log<SqlBulkSink<SyntheticRemergeTarget>>\\(\\)\\)").Count);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        // Only the two unavoidable/non-blocking gaps -- the SqlCommand column-name assumption
        // and Notification. Confirms PortfolioDigest.IsBlockingGap would count this package as
        // fully generatable (0 blocking gaps), not just "produces some files".
        Assert.Equal(2, result.Gaps.Count);
        Assert.All(result.Gaps, g => Assert.False(g.IsBlocking));
    }

    [Fact]
    public void Generate_EmitsAStarterSourceTest_ForAConditionalSplitFlowsSqlSource()
    {
        // Real, previously-latent gap (caught 2026-09-06 verifying the generated-tests plan by
        // hand against SyntheticConditionalSplitRemerge.dtsx's own walkthrough): its own OLE DB
        // Source method compiled fine in production code, but PackageGenerator's "Source -- SQL /
        // Excel" starter-test loop scanned wiredFlows/wiredMulticasts/wiredOleDbCommands only --
        // never wiredSplits -- so a Conditional Split flow's own source got no test at all, unlike
        // the structurally identical Multicast case (which DOES get one). Fixed by adding
        // wiredSplits to that concat and threading csvSampleCandidates into
        // GenerateConditionalSplitFlow's own ResolveFlowSource call, matching Multicast's already-
        // correct wiring.
        var package = LoadSyntheticFixture("SyntheticConditionalSplitRemerge.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var sourceTest = Assert.Single(result.SiblingFiles, f => f.RelativePath == "SyntheticConditionalSplitRemerge.Tests/OLEDBSourceSourceTests.cs");
        Assert.Contains("Category", sourceTest.Content);
        Assert.Contains("Integration", sourceTest.Content);
        CodeAssertions.AssertNoSyntaxErrors(sourceTest.Content);
    }

    [Fact]
    public void Generate_WiresBothFileSystemTaskPositions_PreLoadAndPostFlow()
    {
        // SyntheticFileSystemTask.dtsx: SQL_PreLoad -> FST_PreLoadCopy (pre-load) -> DFT_Load ->
        // FST_PostLoadCopy (post-flow). Confirmed beyond this test by actually building and
        // running the generated exe against .\SQLFORPOC_2022 -- see
        // Tools/SsisExtractor/CLAUDE.md's "File System Task" section.
        var package = LoadSyntheticFixture("SyntheticFileSystemTask.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticFileSystemTask.cs");
        // Emitter rewrite: both positions are now ordinary FileSystemStep-constructing methods --
        // neither is a special hard-coded pre-transaction call any more.
        Assert.Contains("internal async Task FST_PreLoadCopy(IUnitOfWork uow, CancellationToken ct)", classFile.Content);
        Assert.Contains("var step = new FileSystemStep(\"FST_PreLoadCopy\", new FileSystemPreLoadAction(FileSystemOperation.Copy,", classFile.Content);
        Assert.Contains("internal async Task FST_PostLoadCopy(IUnitOfWork uow, CancellationToken ct)", classFile.Content);
        Assert.Contains("var step = new FileSystemStep(\"FST_PostLoadCopy\", new FileSystemPreLoadAction(FileSystemOperation.Copy,", classFile.Content);
        Assert.Contains("await FST_PreLoadCopy(uow, ct);", classFile.Content);
        Assert.Contains("await DFT_Load(uow, ct);", classFile.Content);
        Assert.Contains("await FST_PostLoadCopy(uow, ct);", classFile.Content);
        // Phase 6: each destination's own path is read from appsettings.json via File(key),
        // keyed by its own FILE connection manager -- not an embedded, client-machine-specific
        // absolute literal.
        Assert.Contains("File(\"CM_FILE_PreLoadArchive\")", classFile.Content);
        Assert.Contains("File(\"CM_FILE_PostFlowArchive\")", classFile.Content);
        Assert.DoesNotContain("archived-preload.txt", classFile.Content);
        Assert.DoesNotContain("archived-postflow.txt", classFile.Content);
        var appSettingsFile = Assert.Single(result.Files, f => f.RelativePath == "appsettings.json");
        Assert.Contains("archived-preload.txt", appSettingsFile.Content);
        Assert.Contains("archived-postflow.txt", appSettingsFile.Content);
        // Ordering: pre-load copy before the flow, post-load copy after it.
        Assert.True(classFile.Content.IndexOf("await FST_PreLoadCopy(uow, ct);", StringComparison.Ordinal)
                  < classFile.Content.IndexOf("await DFT_Load(uow, ct);", StringComparison.Ordinal));
        Assert.True(classFile.Content.IndexOf("await DFT_Load(uow, ct);", StringComparison.Ordinal)
                  < classFile.Content.IndexOf("await FST_PostLoadCopy(uow, ct);", StringComparison.Ordinal));
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        // Only the unavoidable, non-blocking Notification gap plus a LocalFileSourceData gap for
        // the CSV source (Docs/Generated-Tests-Plan.md phase 3) -- this fixture is fully
        // generatable.
        Assert.Equal(2, result.Gaps.Count);
        Assert.Single(result.Gaps, g => g.Location == "SyntheticFileSystemTask.Notification");
        Assert.Single(result.Gaps, g => g.Kind == GapKind.LocalFileSourceData);
    }

    [Fact]
    public void Generate_WiresAConditionalSplit_IntoProgramCsWithOneBranchPerDestination()
    {
        // SyntheticConditionalSplit.dtsx: OLE DB Source -> Derived Column (LoadedAtUtc) ->
        // Conditional Split (one case, "Amount > 1000" -> HighValue; default -> LowValue) -> 2
        // OLE DB Destinations. Confirmed beyond this test by actually running the generated exe
        // against .\SQLFORPOC_2022 and reading dbo.SyntheticHighValue/SyntheticLowValue back
        // afterward (see Tools/SsisExtractor/CLAUDE.md).
        var package = LoadSyntheticFixture("SyntheticConditionalSplit.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        Assert.Contains(result.Files, f => f.RelativePath == "Model/SyntheticHighValue.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Model/SyntheticLowValue.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Mapping/ConditionalSplit_HighValue.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Mapping/ConditionalSplit_LowValue.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Mapping/ConditionalSplitRouter.cs");

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticConditionalSplit.cs");
        Assert.Contains("using Etl.Core.Data;", classFile.Content);
        // Each branch's own sink is constructed directly -- a Conditional Split branch is always
        // SQL-sunk (no Sink field at all on the branch record), so no AddBulkSink<>()/DI
        // resolution is needed for either one.
        Assert.Contains("new SqlBulkSink<SyntheticHighValue>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<SyntheticHighValue>>())", classFile.Content);
        Assert.Contains("new SqlBulkSink<SyntheticLowValue>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<SyntheticLowValue>>())", classFile.Content);
        Assert.Contains("new ConditionalSplitRouter()", classFile.Content);
        // No IRowTransform<> DI registration at all any more -- each branch's transform is
        // constructed directly (`new`), since two branches can share EntityName (a Union All
        // remerge) and would otherwise collide on the same closed generic registration.
        Assert.DoesNotContain("AddScoped<IRowTransform<", classFile.Content);
        Assert.Contains("internal async Task DFT_ConditionalSplitDemo(IUnitOfWork uow, CancellationToken ct)", classFile.Content);
        Assert.Contains("var step = new ConditionalSplitStep<DFT_ConditionalSplitDemoSqlRow>(", classFile.Content);
        Assert.Contains("new ConditionalSplitBranch<DFT_ConditionalSplitDemoSqlRow, SyntheticHighValue>(", classFile.Content);
        Assert.Contains("new ConditionalSplitBranch<DFT_ConditionalSplitDemoSqlRow, SyntheticLowValue>(", classFile.Content);
        Assert.Contains("new ConditionalSplit_HighValue(),", classFile.Content);
        Assert.Contains("new ConditionalSplit_LowValue(),", classFile.Content);
        Assert.Contains("await DFT_ConditionalSplitDemo(uow, ct);", classFile.Content);

        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        // Two expected gaps: the SqlCommand-mode column-name assumption (same as every
        // AccessMode=2 OLE DB Source, see BuildSqlFlowSource), and the unavoidable Notification
        // one -- both branches and the router all resolved cleanly, no split-specific gap.
        Assert.Equal(2, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Reason.Contains("assumes each result-set column is named/aliased"));
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticConditionalSplit.Notification");
    }

    [Fact]
    public void Generate_WiresTwoFlatFileDestinationFlows_AsDirectCopiesWithFlatFileBulkSinks()
    {
        // SyntheticFlatFileDestination.dtsx: DFT_ExportDelimited/DFT_ExportFixedWidth (both OLE
        // DB Source -> Flat File Destination directly, NO Derived Column -- the exact real
        // evidenced shape of RBC_Demo_ETL's own Package_Exports.dtsx) plus DFT_Log (an ordinary
        // OLE DB Source -> Derived Column -> OLE DB Destination flow, giving the package a real
        // SQL table so AddEtlDbContext<T>() has something to back it). Confirmed beyond this
        // test by actually running the generated exe against .\SQLFORPOC_2022 and reading the
        // real output files/table back afterward (see Tools/SsisExtractor/CLAUDE.md) -- including
        // real fixed-width truncation/padding on a deliberately over-length seeded value.
        var package = LoadSyntheticFixture("SyntheticFlatFileDestination.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        // No Model/Mapping row-writer files at all for the flat-file flows beyond the plain
        // source row type + a direct-copy transform -- EntityEmitter/TransformEmitter are reused
        // verbatim, same as any SQL destination.
        Assert.Contains(result.Files, f => f.RelativePath == "Model/DFT_ExportDelimited.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Model/DFT_ExportFixedWidth.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Mapping/DFT_ExportDelimitedTransform.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Mapping/DFT_ExportFixedWidthTransform.cs");
        // No SQL table/DbSet for either flat-file destination -- only DFT_Log's own SyntheticFlatFileDestinationLog does.
        var dbContextFile = Assert.Single(result.Files, f => f.RelativePath == "Model/SyntheticFlatFileDestinationDbContext.cs");
        Assert.Contains("public DbSet<SyntheticFlatFileDestinationLog>", dbContextFile.Content);
        Assert.DoesNotContain("DFT_ExportDelimited", dbContextFile.Content);
        Assert.DoesNotContain("DFT_ExportFixedWidth", dbContextFile.Content);

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticFlatFileDestination.cs");
        Assert.Contains("using Etl.Core.Data;", classFile.Content);
        // Flat file sinks are constructed directly (never SqlBulkSink -- wrong for a file), and
        // the SQL destination's own sink is likewise constructed directly now, not via
        // AddBulkSink<T>()/DI.
        Assert.DoesNotContain("AddBulkSink", classFile.Content);
        Assert.Contains("new SqlBulkSink<SyntheticFlatFileDestinationLog>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<SyntheticFlatFileDestinationLog>>())", classFile.Content);
        Assert.Contains("internal async Task DFT_ExportDelimited(IUnitOfWork uow, CancellationToken ct)", classFile.Content);
        Assert.Contains("new FlatFileColumnFormat(\"CustomerID\", null, \",\")", classFile.Content);
        Assert.Contains("new FlatFileColumnFormat(\"CleanEmail\", null, \"\\r\\n\")", classFile.Content);
        Assert.Contains("\"CustomerID,FullName,CleanEmail\\r\\n\"", classFile.Content); // the header line
        Assert.Contains("internal async Task DFT_ExportFixedWidth(IUnitOfWork uow, CancellationToken ct)", classFile.Content);
        Assert.Contains("new FlatFileColumnFormat(\"CustomerID\", 10, \"\")", classFile.Content);
        Assert.Contains("new FlatFileColumnFormat(\"FullName\", 15, \"\")", classFile.Content);
        Assert.Contains("new FlatFileColumnFormat(\"RowEnd\", null, \"\\r\\n\")", classFile.Content);
        Assert.Contains("File(\"DFT_ExportDelimited\")", classFile.Content);
        Assert.Contains("File(\"DFT_ExportFixedWidth\")", classFile.Content);
        // All three root flows are precedence-independent -- the emitter rewrite's phase 7 now
        // generates real concurrency for that shape (see PackageStep.Wave): one Task.WhenAll wave
        // of all three, each in its own RunBranchAsync transaction, rather than three sequential
        // awaits under separate "// Flow N:" headings.
        Assert.Contains("// Concurrent (3 executables -- SSIS ran these with no ordering constraint between them):", classFile.Content);
        Assert.Contains("//   - DFT_ExportDelimited", classFile.Content);
        Assert.Contains("//   - DFT_ExportFixedWidth", classFile.Content);
        Assert.Contains("//   - DFT_Log", classFile.Content);
        Assert.Contains("await Task.WhenAll(", classFile.Content);
        Assert.Contains("RunBranchAsync(async branchUow =>", classFile.Content);
        Assert.Contains("await DFT_ExportDelimited(branchUow, ct);", classFile.Content);
        Assert.Contains("await DFT_ExportFixedWidth(branchUow, ct);", classFile.Content);
        Assert.Contains("await DFT_Log(branchUow, ct);", classFile.Content);
        Assert.Contains("private async Task RunBranchAsync(Func<IUnitOfWork, Task> body, CancellationToken ct)", classFile.Content);

        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(dbContextFile.Content);

        // Four expected gaps: the SqlCommand-mode column-name assumption on all three OLE DB
        // Sources, and the unavoidable Notification one -- the old parallelism advisory is gone
        // now that real concurrency is generated, and neither flat-file flow reports its own gap.
        Assert.Equal(4, result.Gaps.Count);
        Assert.Equal(3, result.Gaps.Count(g => g.Reason.Contains("assumes each result-set column is named/aliased")));
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticFlatFileDestination.Notification");
    }

    [Fact]
    public void Generate_WiresAConditionalSplitFlowWithNoPerBranchTransform_ThroughSortAndMerge()
    {
        // SyntheticSortMergeRemerge.dtsx: Conditional Split -> Sort -> Sort -> Merge -> one
        // shared destination, with only a flow-level shared Derived Column (LoadedAtUtc) and NO
        // per-branch transform at all -- the real evidenced shape of RBC_Demo_ETL's own
        // DFT_MergeSortedBranches. Before this round, PackagePlanner.ResolveBranch had no
        // pass-through case for Sort/Merge at all (a fatal "not supported" gap). Confirmed
        // beyond this test by actually building and running the generated exe against
        // .\SQLFORPOC_2022 and reading dbo.SyntheticSortMergeTarget back afterward -- all 4
        // seeded rows landed with the correct Name regardless of Sort's own reordering -- see
        // Tools/SsisExtractor/CLAUDE.md's "Sort + Merge -- Conditional Split remerge" section.
        var package = LoadSyntheticFixture("SyntheticSortMergeRemerge.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        // ONE entity/table -- both branches converge on the same destination.
        Assert.Single(result.Files, f => f.RelativePath == "Model/SyntheticSortMergeTarget.cs");

        var canadaFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/CSPLIT_ByCategory_Canada.cs");
        var restFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/CSPLIT_ByCategory_RestOfWorld.cs");

        // Every destination column is a plain passthrough (no Derived Column of its own beyond
        // the shared LoadedAtUtc) -- proving TransformEmitter's existing "row.PipelineColumnName"
        // fallback resolves correctly straight through Sort/Merge, with zero new
        // value-resolution code needed for this feature. LoadedAtUtc IS a genuine Derived Column
        // (GETUTCDATE()), so it's its own named, callable function on a static holder class named
        // after the real SSIS component (DER_LoadedAtUtc) -- shared identically by both branches,
        // and emitted exactly once (see the assertion below), not duplicated per branch.
        Assert.Contains("ID = row.ID,", canadaFile.Content);
        Assert.Contains("Name = row.Name,", canadaFile.Content);
        Assert.Contains("LoadedAtUtc = DER_LoadedAtUtc.LoadedAtUtc(row, ctx),", canadaFile.Content);
        Assert.Contains("ID = row.ID,", restFile.Content);
        Assert.Contains("Name = row.Name,", restFile.Content);
        Assert.Contains("LoadedAtUtc = DER_LoadedAtUtc.LoadedAtUtc(row, ctx),", restFile.Content);
        var sharedHolder = Assert.Single(result.Files, f => f.RelativePath == "Mapping/DER_LoadedAtUtc.cs");
        CodeAssertions.AssertNoSyntaxErrors(sharedHolder.Content);
        CodeAssertions.AssertNoSyntaxErrors(canadaFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(restFile.Content);

        var routerFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/CSPLIT_ByCategoryRouter.cs");
        Assert.Contains("row.Category", routerFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(routerFile.Content);

        // Only the two unavoidable/non-blocking gaps -- the SqlCommand column-name assumption
        // and Notification.
        Assert.Equal(2, result.Gaps.Count);
        Assert.All(result.Gaps, g => Assert.False(g.IsBlocking));
    }

    [Fact]
    public void Generate_WiresAForEachFileLoop_AlongsideAnOrdinaryDataFlowTask()
    {
        // SyntheticForEachFileLoop.dtsx: DFT_Load (an ordinary flow, included only so the whole
        // package has at least one Data Flow Task -- ProgramEmitter's own "no Data Flow Task
        // could be planned" gate would otherwise reject the whole package) alongside
        // FEL_SampleFiles, the real evidenced RBC_Demo_ETL shape. Confirmed beyond this test by
        // actually building and running the generated exe against .\SQLFORPOC_2022 and the real
        // sample .txt files -- see Tools/SsisExtractor/CLAUDE.md's own "ForEach Loop" section.
        var package = LoadSyntheticFixture("SyntheticForEachFileLoop.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticForEachFileLoop.cs");
        Assert.Contains("internal async Task FEL_SampleFiles(IUnitOfWork uow, CancellationToken ct)", classFile.Content);
        Assert.Contains("var step = new ForEachLoopStep(", classFile.Content);
        Assert.Contains("\"FEL_SampleFiles\",", classFile.Content);
        Assert.Contains("new ForEachFileLoopAction(", classFile.Content);
        Assert.Contains("ForEachFileNameMode.NameAndExtension),", classFile.Content);
        // The per-iteration SQL text is a call to its own named, parameterized statement class
        // (SqlStatementBuilderEmitter.EmitParameterized) -- never an inline lambda body.
        Assert.Contains("currentFile => SQL_LogFileNameStatement.BuildStatement(currentFile),", classFile.Content);
        Assert.Contains("Log<ForEachLoopStep>());", classFile.Content);
        // DFT_Load and FEL_SampleFiles are precedence-independent siblings -- the emitter
        // rewrite's phase 7 now runs them concurrently (see PackageStep.Wave), each in its own
        // RunBranchAsync transaction, rather than two sequential awaits.
        Assert.Contains("await Task.WhenAll(", classFile.Content);
        Assert.Contains("await FEL_SampleFiles(branchUow, ct);", classFile.Content);
        Assert.Contains("await DFT_Load(branchUow, ct);", classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        var statementFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SQL_LogFileNameStatement.cs");
        Assert.Contains("public static class SQL_LogFileNameStatement", statementFile.Content);
        Assert.Contains(
            "public static string BuildStatement(string currentFile) => \"INSERT INTO dbo.SyntheticForEachFileLoopLog (FileName) VALUES (N'\" + currentFile + \"');\";",
            statementFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(statementFile.Content);

        // Only two unavoidable/non-blocking gaps -- the SqlCommand column-name assumption on
        // DFT_Load's own source, and Notification. The old parallelism advisory is gone now that
        // DFT_Load/FEL_SampleFiles run as real concurrency; no ForEach-Loop-specific gap either.
        Assert.Equal(2, result.Gaps.Count);
        Assert.All(result.Gaps, g => Assert.False(g.IsBlocking));
    }

    [Fact]
    public void Generate_WiresAForEachDataFlowLoop_ConstructingAFreshCsvRowSourcePerIteration()
    {
        // SyntheticForEachDataFlowLoop.dtsx, built speculatively 2026-08-30: a ForEach Loop whose
        // body is a whole Data Flow Task (Flat File Source, expression-driven off the loop's own
        // mapped variable -> Derived Column -> OLE DB Destination), re-run once per enumerated
        // file. Confirmed beyond this test by actually building the generated exe against a copy
        // of Etl.Core and running it against .\SQLFORPOC_2022 and two real checked-in .csv files
        // -- see CLAUDE.md's "ForEach Loop over a Data Flow Task" section: all 3 rows across both
        // files landed correctly, read back from the table afterward.
        var package = LoadSyntheticFixture("SyntheticForEachDataFlowLoop.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        Assert.Contains(result.Files, f => f.RelativePath == "Csv/SyntheticForEachDataFlowLoopTargetCsvRow.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Csv/SyntheticForEachDataFlowLoopTargetCsvRowMap.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Mapping/SyntheticForEachDataFlowLoopTargetTransform.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Model/SyntheticForEachDataFlowLoopTarget.cs");

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticForEachDataFlowLoop.cs");
        // No top-level/shared IRowSource<TRow> at all for this step -- the source is constructed
        // fresh, inline, per iteration; only its transform is constructed once (directly, not via
        // DI -- a generated transform class never has constructor dependencies).
        Assert.DoesNotContain("IRowSource<SyntheticForEachDataFlowLoopTargetCsvRow>>", classFile.Content);
        Assert.Contains("internal async Task FEL_SampleFiles(IUnitOfWork uow, CancellationToken ct)", classFile.Content);
        Assert.Contains(
            "var step = new ForEachFileDataFlowStep<SyntheticForEachDataFlowLoopTargetCsvRow, SyntheticForEachDataFlowLoopTarget>(",
            classFile.Content);
        Assert.Contains("new ForEachFileLoopAction(", classFile.Content);
        Assert.Contains(
            "currentFile => new CsvRowSource<SyntheticForEachDataFlowLoopTargetCsvRow>(\"Flat File Source\", new CsvSourceOptions { FilePath = currentFile }, new SyntheticForEachDataFlowLoopTargetCsvRowMap()),",
            classFile.Content);
        Assert.Contains("new SyntheticForEachDataFlowLoopTargetTransform(),", classFile.Content);
        Assert.Contains("new SqlBulkSink<SyntheticForEachDataFlowLoopTarget>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<SyntheticForEachDataFlowLoopTarget>>()),", classFile.Content);
        Assert.Contains("Log<DataFlowStep<SyntheticForEachDataFlowLoopTargetCsvRow, SyntheticForEachDataFlowLoopTarget>>());", classFile.Content);
        Assert.Contains("await FEL_SampleFiles(uow, ct);", classFile.Content); // referenced in RunAsync
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        // Two non-blocking gaps: the unavoidable Notification one, plus a real, previously-silent
        // gap caught 2026-09-06 by an independent review -- this loop-body shape used to reach
        // this point with NO starter test coverage AND no acknowledgment at all (unlike every
        // other flow shape, which degrades to an honest advisory when it can't test something).
        // No test files exist beyond RunAsyncTests.cs (asserted separately below).
        Assert.Equal(2, result.Gaps.Count);
        var testOracleGap = Assert.Single(result.Gaps, g => g.Kind == GapKind.TestOracle);
        Assert.False(testOracleGap.IsBlocking);
        Assert.Contains("no starter test coverage in this pilot", testOracleGap.Reason);
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticForEachDataFlowLoop.Notification");

        Assert.DoesNotContain(result.SiblingFiles, f => f.RelativePath.EndsWith("SourceTests.cs"));
        Assert.DoesNotContain(result.SiblingFiles, f => f.RelativePath.EndsWith("TransformTests.cs"));
    }

    [Fact]
    public void Generate_WiresAForLoop_ConstructingAFreshCsvRowSourcePerIteration_DrivenByAPackageVariableCounter()
    {
        // SyntheticForLoop.dtsx -- Phase 3 of the unsupported-component-types plan (STOCK:FORLOOP
        // / For Loop Container), confirmed real from a genuine SSDT-authored package (see
        // ForLoopPayload's own doc comment). Init="@Part = 1", Eval="@Part < 4",
        // Assign="@Part = @Part + 1" -> the loop body (Flat File Source, expression-driven off
        // the loop's own counter -> Derived Column -> OLE DB Destination) re-runs 3 times.
        // Confirmed beyond this test by actually building the generated exe against a copy of
        // Etl.Core and running it against .\SQLFORPOC_2022: 3 rows landed, one per iteration,
        // each sourced from the correct counter-named CSV file (part-1.csv/part-2.csv/part-3.csv
        // -> "one"/"two"/"three"), read back from the table afterward -- not just a row count.
        var package = LoadSyntheticFixture("SyntheticForLoop.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        Assert.Contains(result.Files, f => f.RelativePath == "Csv/SyntheticForLoopTargetCsvRow.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Csv/SyntheticForLoopTargetCsvRowMap.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Mapping/SyntheticForLoopTargetTransform.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Model/SyntheticForLoopTarget.cs");

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticForLoop.cs");
        Assert.Contains("internal async Task ForLoopContainer(IUnitOfWork uow, CancellationToken ct)", classFile.Content);
        Assert.Contains(
            "var step = new ForLoopStep<SyntheticForLoopTargetCsvRow, SyntheticForLoopTarget>(",
            classFile.Content);
        // Init/Eval/Assign are plain parameterless delegates closing over packageVariables
        // directly -- Init runs the literal "1" the fixture's own InitExpression assigns.
        Assert.Contains("() => packageVariables.Set(\"User::Part\", 1),", classFile.Content);
        Assert.Contains("() => (packageVariables.GetRequired<int>(\"User::Part\") < 4),", classFile.Content);
        Assert.Contains(
            "() => packageVariables.Set(\"User::Part\", (packageVariables.GetRequired<int>(\"User::Part\")) + (1)),",
            classFile.Content);
        // A fresh CsvRowSource per iteration, reading the counter's own LIVE value -- not a lambda
        // parameter (unlike ForEachFileDataFlowStep's own "currentFile" -- see ForLoopStep<TRow,
        // TEntity>'s own doc comment for why).
        Assert.Contains("() => new CsvRowSource<SyntheticForLoopTargetCsvRow>(\"Flat File Source\", new CsvSourceOptions { FilePath =", classFile.Content);
        Assert.Contains("packageVariables.GetRequired<int>(\"User::Part\")", classFile.Content);
        Assert.Contains("new SyntheticForLoopTargetTransform(),", classFile.Content);
        Assert.Contains("new SqlBulkSink<SyntheticForLoopTarget>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<SyntheticForLoopTarget>>()),", classFile.Content);
        Assert.Contains("Log<DataFlowStep<SyntheticForLoopTargetCsvRow, SyntheticForLoopTarget>>());", classFile.Content);
        Assert.Contains("await ForLoopContainer(uow, ct);", classFile.Content); // referenced in RunAsync
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        // Two non-blocking gaps: the unavoidable Notification one, plus the same "no starter test
        // coverage in this pilot" TestOracle advisory GenerateForEachDataFlowLoop's own identical
        // loop-body shape gets (a For Loop Container has no Tier-A sample file to synthesize
        // either -- the source path is computed per iteration, not a static location).
        Assert.Equal(2, result.Gaps.Count);
        var testOracleGap = Assert.Single(result.Gaps, g => g.Kind == GapKind.TestOracle);
        Assert.False(testOracleGap.IsBlocking);
        Assert.Contains("no starter test coverage in this pilot", testOracleGap.Reason);
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticForLoop.Notification");

        Assert.DoesNotContain(result.SiblingFiles, f => f.RelativePath.EndsWith("SourceTests.cs"));
        Assert.DoesNotContain(result.SiblingFiles, f => f.RelativePath.EndsWith("TransformTests.cs"));
    }

    [Fact]
    public void Generate_ResolvesTheThreeSpeculativeNumericToI4Pairings_I8NumericR4()
    {
        // SyntheticIntNumericCoercion.dtsx, built speculatively 2026-08-30 -- see
        // TransformEmitterTests' own theory test for the real-portfolio context. Confirmed
        // beyond this test by actually building the generated exe against a copy of Etl.Core
        // and running it against .\SQLFORPOC_2022 -- an exact row-for-row match against a real
        // dtexec probe run of the same fixture across all nine seeded rows, midpoints included.
        var package = LoadSyntheticFixture("SyntheticIntNumericCoercion.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var transform = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticIntNumericCoercionTargetTransform.cs");
        Assert.Contains("I8Val = SsisFn.NarrowI8ToI4(row.I8Val),", transform.Content);
        Assert.Contains("NumericVal = SsisFn.NarrowNumericToI4(row.NumericVal),", transform.Content);
        Assert.Contains("R4Val = SsisFn.NarrowR4ToI4(row.R4Val),", transform.Content);
        CodeAssertions.AssertNoSyntaxErrors(transform.Content);

        var ssisFn = Assert.Single(result.Files, f => f.RelativePath == "Ssis/SsisFn.cs");
        Assert.Contains("NarrowI8ToI4", ssisFn.Content);
        Assert.Contains("NarrowNumericToI4", ssisFn.Content);
        Assert.Contains("NarrowR4ToI4", ssisFn.Content);

        // Only the two unavoidable/non-blocking gaps -- the SqlCommand column-name assumption
        // on the source, and Notification. No numeric-mismatch gap for any of the three columns.
        Assert.Equal(2, result.Gaps.Count);
        Assert.All(result.Gaps, g => Assert.False(g.IsBlocking));
    }

    [Fact]
    public void Generate_ResolvesAnIntSourceDecimalDestinationPassthroughColumn_ViaWidenI4ToNumeric()
    {
        // SyntheticIntNumericWiden.dtsx, added 2026-09-18 -- the real shape sql-server-samples'
        // own DailyETLMain.dtsx has (StockHolding_Staging's own "Last Cost Price": buffered i4
        // from a SqlCommand's own `int` result-set column, decimal(18,2) at the real destination
        // table). UNLIKE every other numeric-coercion pairing, this one needed no dtexec probe --
        // int -> decimal is a strictly widening, lossless C#-native implicit conversion. Confirmed
        // beyond this test by actually building the generated exe against a copy of Etl.Core and
        // running it against .\SQLFORPOC_2022: 42/-17/0 landed as 42.00/-17.00/.00, an exact
        // round-trip with no precision loss.
        var package = LoadSyntheticFixture("SyntheticIntNumericWiden.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var transform = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticIntNumericWidenTargetTransform.cs");
        Assert.Contains("Val = SsisFn.WidenI4ToNumeric(row.Val),", transform.Content);
        CodeAssertions.AssertNoSyntaxErrors(transform.Content);

        var ssisFn = Assert.Single(result.Files, f => f.RelativePath == "Ssis/SsisFn.cs");
        Assert.Contains("WidenI4ToNumeric", ssisFn.Content);

        // Only the two unavoidable/non-blocking gaps -- the SqlCommand column-name assumption
        // on the source, and Notification. No numeric-mismatch gap for the Val column.
        Assert.Equal(2, result.Gaps.Count);
        Assert.All(result.Gaps, g => Assert.False(g.IsBlocking));
    }

    [Fact]
    public void Generate_WiresAnAggregateFlow_ViaAggregateRowSourceWrappingTheRawSqlSource()
    {
        // SyntheticAggregate.dtsx, built speculatively 2026-08-30 (see PackagePlannerTests' own
        // sibling test for the real-portfolio context). Confirmed beyond this test by actually
        // building the generated exe against a copy of Etl.Core and running it against
        // .\SQLFORPOC_2022 -- an exact row-for-row match against a real dtexec probe run of the
        // same fixture (East=2, West=1 -- CustomerCount excludes the one NULL CustomerID row).
        var package = LoadSyntheticFixture("SyntheticAggregate.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        Assert.Contains(result.Files, f => f.RelativePath == "Sql/AGG_ByRegionSourceSqlRow.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Sql/SyntheticAggregateTargetAggregateRow.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Mapping/SyntheticAggregateTargetTransform.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Model/SyntheticAggregateTarget.cs");

        var aggregateRow = Assert.Single(result.Files, f => f.RelativePath == "Sql/SyntheticAggregateTargetAggregateRow.cs");
        Assert.Contains("public string Region { get; set; } = \"\";", aggregateRow.Content);
        Assert.Contains("public long CustomerCount { get; set; }", aggregateRow.Content);

        var sourceRow = Assert.Single(result.Files, f => f.RelativePath == "Sql/AGG_ByRegionSourceSqlRow.cs");
        Assert.Contains("public string? CustomerID { get; set; }", sourceRow.Content); // nullable by construction -- Count excludes NULL

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticAggregate.cs");
        Assert.Contains("internal async Task DFT_Aggregate(IUnitOfWork uow, CancellationToken ct)", classFile.Content);
        Assert.Contains("var source = AGG_ByRegion(uow);", classFile.Content);
        Assert.Contains(
            "return new AggregateRowSource<AGG_ByRegionSourceSqlRow, string, SyntheticAggregateTargetAggregateRow>(",
            classFile.Content);
        Assert.Contains("row => row.Region,", classFile.Content);
        Assert.Contains("CustomerCount = rows.Count(r => r.CustomerID != null)", classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        // Only the two unavoidable/non-blocking gaps -- the SqlCommand column-name assumption
        // on the source, and Notification.
        Assert.Equal(2, result.Gaps.Count);
        Assert.All(result.Gaps, g => Assert.False(g.IsBlocking));
    }

    [Fact]
    public void Generate_WiresEveryAggregationType_SumAverageMinimumMaximumCountDistinctCountAll()
    {
        // SyntheticAggregateFunctions.dtsx, built 2026-09-02 (gap-audit Phase 3.3). Confirmed
        // beyond this test by actually building the generated exe against a copy of Etl.Core and
        // running it against .\SQLFORPOC_2022 -- an EXACT match, all-NULL group included, against
        // a real dtexec probe run of the same fixture (Region A: TotalRows=4, DistinctAmounts=2,
        // SumAmount=40, AverageAmount=13.333..., MinAmount=10, MaxAmount=20; Region C, all NULL:
        // TotalRows=2, DistinctAmounts=0, Sum/Average/Min/MaxAmount all NULL -- proving generated
        // code reproduces SQL semantics, not .NET's own Enumerable.Sum-returns-0-for-all-null
        // quirk, independently verified this same round to diverge).
        var package = LoadSyntheticFixture("SyntheticAggregateFunctions.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var aggregateRow = Assert.Single(result.Files, f => f.RelativePath == "Sql/SyntheticAggregateFunctionsTargetAggregateRow.cs");
        Assert.Contains("public string Region { get; set; } = \"\";", aggregateRow.Content);
        Assert.Contains("public long TotalRows { get; set; }", aggregateRow.Content);
        Assert.Contains("public long DistinctAmounts { get; set; }", aggregateRow.Content);
        Assert.Contains("public double? SumAmount { get; set; }", aggregateRow.Content);
        Assert.Contains("public double? AverageAmount { get; set; }", aggregateRow.Content);
        Assert.Contains("public double? MinAmount { get; set; }", aggregateRow.Content);
        Assert.Contains("public double? MaxAmount { get; set; }", aggregateRow.Content);

        var entity = Assert.Single(result.Files, f => f.RelativePath == "Model/SyntheticAggregateFunctionsTarget.cs");
        Assert.Contains("public double? SumAmount { get; set; }", entity.Content);

        var transform = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticAggregateFunctionsTargetTransform.cs");
        Assert.Contains("SumAmount = row.SumAmount,", transform.Content);

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticAggregateFunctions.cs");
        Assert.Contains("TotalRows = rows.Count,", classFile.Content);
        Assert.Contains("DistinctAmounts = rows.Select(r => r.Amount).Where(v => v != null).Distinct().Count(),", classFile.Content);
        Assert.Contains("SumAmount = rows.All(r => r.Amount == null) ? null : rows.Sum(r => r.Amount),", classFile.Content);
        Assert.Contains("AverageAmount = rows.Average(r => r.Amount),", classFile.Content);
        Assert.Contains("MinAmount = rows.Min(r => r.Amount),", classFile.Content);
        Assert.Contains("MaxAmount = rows.Max(r => r.Amount)", classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        // Only the two unavoidable/non-blocking gaps -- the SqlCommand column-name assumption
        // on the source, and Notification.
        Assert.Equal(2, result.Gaps.Count);
        Assert.All(result.Gaps, g => Assert.False(g.IsBlocking));
    }

    [Fact]
    public void Generate_WiresAnExcelSourcedFlow_IntoProgramCsViaExcelRowSource()
    {
        // SyntheticExcelSource.dtsx: Microsoft.ExcelSource (reading the real, checked-in
        // synthetic-excel-source.xlsx) -> OLE DB Destination, a genuine direct-copy pipeline
        // with no Derived Column at all. Proves BOTH new pieces added the same round: Excel
        // Source's own read-side wiring, AND removing the pre-existing "no Derived Column"
        // gate for OLE DB/ADO NET destinations (Flat File Destination was the only prior
        // exemption). Confirmed beyond this test by actually building the generated exe against
        // a copy of Etl.Core and running it against .\SQLFORPOC_2022 -- see
        // Tools/SsisExtractor/CLAUDE.md's own "Excel Source" section.
        var package = LoadSyntheticFixture("SyntheticExcelSource.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        Assert.Contains(result.Files, f => f.RelativePath == "Excel/SyntheticExcelSourceTargetExcelRow.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Excel/SyntheticExcelSourceTargetExcelRowReader.cs");

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticExcelSource.cs");
        Assert.Contains("using Etl.Core.Excel;", classFile.Content);
        Assert.Contains("using SyntheticExcelSource.Excel;", classFile.Content);
        // No `uow` parameter/argument -- an Excel source never touches the package's transaction,
        // unlike a SQL one (see PackageClassEmitter.SourceNeedsUow's own doc comment).
        Assert.Contains("var source = ExcelSource();", classFile.Content);
        Assert.Contains("internal IRowSource<SyntheticExcelSourceTargetExcelRow> ExcelSource()", classFile.Content);
        Assert.Contains(
            "return new ExcelRowSource<SyntheticExcelSourceTargetExcelRow>(\"Excel Source\", new ExcelSourceOptions { FilePath = File(\"Excel Source\"), WorksheetName = \"DripEligibility$\", HasHeaderRow = true }, SyntheticExcelSourceTargetExcelRowReader.Read);",
            classFile.Content);
        Assert.Contains("var step = new DataFlowStep<SyntheticExcelSourceTargetExcelRow, SyntheticExcelSourceTarget>(", classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        // The unavoidable, non-blocking Notification gap plus a LocalFileSourceData gap (Excel
        // has no Tier-A synthesizer at all -- Docs/Generated-Tests-Plan.md phase 3) -- no Derived
        // Column needed, and no numeric-type-mismatch gap either, since this fixture's own
        // CustomerID is FLOAT (matching the worksheet's real r8 type exactly, unlike
        // RBC_Demo_ETL's own INT column).
        Assert.Equal(2, result.Gaps.Count);
        Assert.Single(result.Gaps, g => g.Location == "SyntheticExcelSource.Notification");
        Assert.Single(result.Gaps, g => g.Kind == GapKind.LocalFileSourceData);
    }

    [Fact]
    public void Generate_WiresAnXmlSourcedFlow_IntoProgramCsViaXmlRowSource()
    {
        // SyntheticXmlSource.dtsx: Microsoft.XmlSourceAdapter (discriminated via
        // UserComponentTypeName, ComponentClassId is the generic "Microsoft.ManagedComponentHost")
        // reading the real, checked-in synthetic-xml-source.xml/.xsd pair -> OLE DB Destination, a
        // genuine direct-copy pipeline with no Derived Column at all -- Phase 5 of the
        // unsupported-component-types plan. Confirmed beyond this test by actually building the
        // generated exe against a copy of Etl.Core and running it against .\SQLFORPOC_2022, and
        // matching a real dtexec run of the same fixture row-for-row (see this project's own
        // CLAUDE.md "XML Source" section).
        var package = LoadSyntheticFixture("SyntheticXmlSource.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        Assert.Contains(result.Files, f => f.RelativePath == "Xml/SyntheticXmlSourceTargetXmlRow.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Xml/SyntheticXmlSourceTargetXmlRowReader.cs");

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticXmlSource.cs");
        Assert.Contains("using Etl.Core.Xml;", classFile.Content);
        Assert.Contains("using SyntheticXmlSource.Xml;", classFile.Content);
        // No `uow` parameter/argument -- an XML source never touches the package's transaction,
        // the same shape Excel's own source method already has (see
        // PackageClassEmitter.SourceNeedsUow's own doc comment).
        Assert.Contains("var source = XMLSource();", classFile.Content);
        Assert.Contains("internal IRowSource<SyntheticXmlSourceTargetXmlRow> XMLSource()", classFile.Content);
        Assert.Contains(
            "return new XmlRowSource<SyntheticXmlSourceTargetXmlRow>(\"XML Source\", new XmlSourceOptions { FilePath = File(\"XML Source\"), RowElementName = \"record\" }, SyntheticXmlSourceTargetXmlRowReader.Read);",
            classFile.Content);
        Assert.Contains("var step = new DataFlowStep<SyntheticXmlSourceTargetXmlRow, SyntheticXmlSourceTarget>(", classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        // The unavoidable, non-blocking Notification gap plus a LocalFileSourceData gap (XML has
        // no Tier-A synthesizer at all, same reason Excel doesn't) -- no Derived Column needed.
        Assert.Equal(2, result.Gaps.Count);
        Assert.Single(result.Gaps, g => g.Location == "SyntheticXmlSource.Notification");
        Assert.Single(result.Gaps, g => g.Kind == GapKind.LocalFileSourceData);
    }

    [Fact]
    public void Generate_WiresAnExcelSourceInSqlCommandMode_WhenTheQueryIsABareSelectStarFromSheet()
    {
        // SyntheticExcelSourceSqlCommand.dtsx: hand-derived (byte-preserving edit, not a fresh
        // object-model build -- see the fixture's own note in CLAUDE.md's "Excel Source
        // SqlCommand mode" section for why: attaching an OLE DB Destination downstream of a
        // SqlCommand-mode Excel Source fails SSIS's own object-model validation in this
        // environment, a confirmed, left-unresolved quirk, unrelated to the SQL itself -- a raw
        // System.Data.OleDb probe against the real workbook proved the query executes fine)
        // from SyntheticExcelSource.dtsx: AccessMode=2 (SqlCommand), SqlCommand="SELECT * FROM
        // [DripEligibility$]" instead of AccessMode=0/OpenRowset="DripEligibility$". Built
        // SPECULATIVELY 2026-08-30 -- no real package in the tracked portfolio uses Excel Source
        // in SqlCommand mode. Proves the bare "SELECT * FROM [SheetName$]" pattern resolves to
        // the exact same WorksheetName a real OpenRowset-mode Excel Source would use. Confirmed
        // beyond this test by actually building the generated exe against a copy of Etl.Core and
        // running it against .\SQLFORPOC_2022 and the real checked-in workbook -- byte-for-byte
        // the same 9 rows as the OpenRowset-mode fixture's own verified run.
        var package = LoadSyntheticFixture("SyntheticExcelSourceSqlCommand.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticExcelSourceSqlCommand.cs");
        Assert.Contains(
            "new ExcelSourceOptions { FilePath = File(\"Excel Source\"), WorksheetName = \"DripEligibility$\", HasHeaderRow = true }",
            classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        Assert.Equal(2, result.Gaps.Count);
        Assert.Single(result.Gaps, g => g.Location == "SyntheticExcelSourceSqlCommand.Notification");
        Assert.Single(result.Gaps, g => g.Kind == GapKind.LocalFileSourceData);
    }

    [Fact]
    public void Generate_ReportsAGap_ForAnExcelSourceSqlCommand_ThatIsNotABareSelectStarFromSheet()
    {
        // Same fixture family as above, with an explicit column list -- gap-audit Phase 3.4
        // (2026-09-02) confirmed via a real raw System.Data.OleDb probe against the checked-in
        // workbook that ACE OLEDB genuinely REORDERS/NARROWS its result set to match the SELECT
        // list's own stated order (not the worksheet's physical layout). Etl.Core.Excel.
        // ExcelRowSource has no query engine at all -- it reads the raw physical grid, always in
        // true physical order, with no way to reorder or skip columns, and the .dtsx alone never
        // records a narrowed query's true physical layout. So a column list is a PERMANENT
        // architectural ceiling (see ExcelWhereClauseTranslator's own doc comment), not a
        // temporary gap -- this fixture used to carry a WHERE clause instead (now supported, see
        // SyntheticExcelSourceSqlCommandWhere.dtsx), bumped here to the one shape that remains
        // genuinely, permanently unsupported.
        var package = LoadSyntheticFixture("SyntheticExcelSourceSqlCommandUnsupported.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        Assert.DoesNotContain(result.Files, f => f.RelativePath == "Program.cs");
        var gap = Assert.Single(result.Gaps, g => g.Location == "DFT_ExcelLoad");
        Assert.Contains("cannot translate", gap.Reason);
        Assert.Contains("a column list, JOIN, or range address is not", gap.Reason);
    }

    [Fact]
    public void Generate_WiresAnExcelSourceInSqlCommandMode_WithAWhereClause()
    {
        // SyntheticExcelSourceSqlCommandWhere.dtsx: gap-audit Phase 3.4 (2026-09-02), a
        // byte-preserving derived edit of SyntheticExcelSourceSqlCommand.dtsx (same reason as
        // that fixture's own note -- see its doc comment above). SqlCommand = "SELECT * FROM
        // [DripEligibility$] WHERE CustomerID > 5". Confirmed via the same real raw
        // System.Data.OleDb probe that a bare "SELECT *" WHERE clause genuinely filters
        // server-side (4 of the 9 real rows: CustomerID 6/7/9/10), and that string equality is
        // measured CASE-INSENSITIVE ('y' matched the same rows as 'Y') -- this fixture's own
        // numeric comparison doesn't exercise that half directly, but ExcelWhereClauseTranslator
        // applies it uniformly per its own doc comment. Confirmed beyond this test by actually
        // building the generated exe against a copy of Etl.Core and running it against
        // .\SQLFORPOC_2022 and the real checked-in workbook -- exactly those 4 rows landed.
        var package = LoadSyntheticFixture("SyntheticExcelSourceSqlCommandWhere.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticExcelSourceSqlCommandWhere.cs");
        // No `uow` parameter/argument -- an Excel source (even with a WHERE filter applied
        // client-side) never touches the package's transaction.
        Assert.Contains("var source = ExcelSource();", classFile.Content);
        Assert.Contains(
            "return new FilteringRowSource<SyntheticExcelSourceSqlCommandWhereTargetExcelRow>(\"Excel Source\", new ExcelRowSource<SyntheticExcelSourceSqlCommandWhereTargetExcelRow>(\"Excel Source\", new ExcelSourceOptions { FilePath = File(\"Excel Source\"), WorksheetName = \"DripEligibility$\", HasHeaderRow = true }, SyntheticExcelSourceSqlCommandWhereTargetExcelRowReader.Read), row => row.CustomerID > 5);",
            classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        Assert.Equal(2, result.Gaps.Count);
        Assert.Single(result.Gaps, g => g.Location == "SyntheticExcelSourceSqlCommandWhere.Notification");
        Assert.Single(result.Gaps, g => g.Kind == GapKind.LocalFileSourceData);
    }

    // Grammar edge cases (unknown column, OR, mixed operators, type mismatches) are tested
    // directly against ExcelWhereClauseTranslator in ExcelWhereClauseTranslatorTests.cs --
    // simpler than fabricating a fourth PipelineComponentSpec/PackageSpec for each negative case.

    [Fact]
    public void Generate_WiresAnOleDbCommandFlow_WithNoEntityOrDbTable()
    {
        // SyntheticOleDbCommand.dtsx: OLE DB Source -> OLE DB Command, no destination component
        // at all -- proves the whole new flow shape: no EntityEmitter/DbContextEmitter.TableSpec/
        // TransformEmitter output, a table-less DbContext still generated (IUnitOfWork still
        // needs one), and no Mapping/ folder or using at all. Confirmed beyond this test by
        // actually building the generated exe against a copy of Etl.Core and running it against
        // .\SQLFORPOC_2022 -- all 3 seeded rows correctly flagged -- see
        // Tools/SsisExtractor/CLAUDE.md's own "OLE DB Command + Multicast" section.
        var package = LoadSyntheticFixture("SyntheticOleDbCommand.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        Assert.DoesNotContain(result.Files, f => f.RelativePath.StartsWith("Mapping/", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Files, f => f.RelativePath.StartsWith("Model/") && !f.RelativePath.EndsWith("DbContext.cs"));
        Assert.Contains(result.Files, f => f.RelativePath == "Model/SyntheticOleDbCommandDbContext.cs");

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticOleDbCommand.cs");
        Assert.DoesNotContain("using SyntheticOleDbCommand.Mapping;", classFile.Content);
        Assert.Contains("internal async Task DFT_FlagCustomers(IUnitOfWork uow, CancellationToken ct)", classFile.Content);
        Assert.Contains(
            "var step = new OleDbCommandStep<DFT_FlagCustomersSqlRow>(",
            classFile.Content);
        Assert.Contains(
            "\"UPDATE dbo.SyntheticOleDbCommandTarget SET Flagged = 1 WHERE CustomerID = {0}\",",
            classFile.Content);
        Assert.Contains("row => new object?[] { row.CustomerID },", classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        // Only the unavoidable non-blocking gaps: the SqlCommand-mode advisory and Notification.
        Assert.Equal(2, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticOleDbCommand.Notification");
    }

    [Fact]
    public void Generate_WrapsTheRowSourceInACountingRowSource_ForALiveMidChainRowCount()
    {
        // SyntheticRowCountVariable.dtsx (gap-audit Phase 3.2): OLE DB Source -> RowCount
        // (User::RowsLoaded, live, mid-chain) -> OLE DB Destination, then a post-flow Script
        // Task reads the variable back. Before this round the RowCount's own side effect was
        // silently dropped -- TransformEmitter already treats it as a safe passthrough for
        // COLUMN resolution, so the flow generated cleanly with zero gaps and no reproduction of
        // the count at all. Confirmed beyond this test by actually building the generated exe
        // against a copy of Etl.Core and running it against .\SQLFORPOC_2022 -- the logged
        // RowsLoaded value matched the real row count exactly, and a real dtexec run of the same
        // fixture confirmed RowCount's own passthrough never drops/alters rows.
        var package = LoadSyntheticFixture("SyntheticRowCountVariable.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null, emitSeams: true);

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticRowCountVariable.cs");
        Assert.Contains("private readonly PackageVariables packageVariables = new();", classFile.Content);
        Assert.Contains(
            "source = new CountingRowSource<SyntheticRowCountVariableTargetSqlRow>(\"DFT_Load.RowCount\", source, packageVariables, \"User::RowsLoaded\");",
            classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        // Only the unavoidable/expected gaps: the SqlCommand-mode advisory, the Script Task seam
        // (this fixture's post-flow reader) plus its own companion TEST-ORACLE gap
        // (Docs/Generated-Tests-Plan.md phase 3 -- "a filled seam is human logic"), and
        // Notification -- none about RC_RowsLoaded itself.
        Assert.Equal(4, result.Gaps.Count);
        Assert.DoesNotContain(result.Gaps, g => g.Location.Contains("RC_RowsLoaded", StringComparison.Ordinal));
        Assert.Single(result.Gaps, g => g.Kind == GapKind.TestOracle);
    }

    [Fact]
    public void Generate_WiresAMulticastFlow_FanningToBothASqlAndAFlatFileBulkSink()
    {
        // SyntheticMulticast.dtsx: OLE DB Source -> Multicast -> {OLE DB Destination, Flat File
        // Destination}. Proves the whole round's own real discovery: a Multicast branch's own
        // int ID column feeding a Flat File Destination needed a ToString() widening, not a
        // type-mismatch gap (a Flat File Destination has no real narrowing risk -- every column
        // it writes is text). Confirmed beyond this test by actually building the generated exe
        // against a copy of Etl.Core and running it against .\SQLFORPOC_2022 -- all 3 rows
        // landed correctly in BOTH the table and the audit CSV -- see
        // Tools/SsisExtractor/CLAUDE.md's own "OLE DB Command + Multicast" section.
        var package = LoadSyntheticFixture("SyntheticMulticast.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticMulticast.cs");
        Assert.Contains("internal async Task DFT_FixedWidthImport(IUnitOfWork uow, CancellationToken ct)", classFile.Content);
        Assert.Contains("var step = new MulticastStep<DFT_FixedWidthImportSqlRow>(", classFile.Content);
        Assert.Contains("new SqlBulkSink<SyntheticMulticastTarget>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<SyntheticMulticastTarget>>())", classFile.Content);
        Assert.Contains("new FlatFileBulkSink<DFT_FixedWidthImportFFDST_AuditTrail_APPEND>(", classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        var flatFileTransform = Assert.Single(result.Files, f => f.RelativePath == "Mapping/MC_Fanout_MulticastOutput2.cs");
        // The ToString() widening itself -- an int source column assigned to the flat file
        // destination's own string-typed entity property.
        Assert.Contains("ID = row.ID.ToString(),", flatFileTransform.Content);
        CodeAssertions.AssertNoSyntaxErrors(flatFileTransform.Content);

        // No numeric-passthrough-coercion gap for the flat file branch's own ID column -- only
        // the unavoidable non-blocking gaps (SqlCommand-mode advisory, Notification).
        Assert.Equal(2, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticMulticast.Notification");
    }

    [Fact]
    public void Generate_WiresAPercentageSamplingFlow_AsAConditionalSplitStepWithASamplingRouter()
    {
        // SyntheticPctSampling.dtsx (Phase 4 of the unsupported-component-types plan): OLE DB
        // Source -> Percentage Sampling -> {OLE DB Destination (sampled), OLE DB Destination (not
        // sampled)}. Percentage Sampling is structurally a routing DECISION between two mutually
        // exclusive branches (like Conditional Split), not an unconditional fan-out (like
        // Multicast) -- so it reuses ConditionalSplitStep<TRow>/ProgramConditionalSplitStep
        // verbatim, with a PctSamplingRouterEmitter-produced router class instead of an
        // expression-translated one. Verified end-to-end (build + run against
        // .\SQLFORPOC_2022, seed reproducibility measured via a real dtexec probe) -- see
        // PctSamplingRouterEmitter's own doc comment and Tools/SsisExtractor/CLAUDE.md.
        var package = LoadSyntheticFixture("SyntheticPctSampling.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticPctSampling.cs");
        Assert.Contains("internal async Task DFT_Sample(IUnitOfWork uow, CancellationToken ct)", classFile.Content);
        Assert.Contains("var step = new ConditionalSplitStep<DFT_SampleSqlRow>(", classFile.Content);
        Assert.Contains("new PCTSAMP_TenPercentSamplingRouter(),", classFile.Content);
        Assert.Contains("new ConditionalSplitBranch<DFT_SampleSqlRow, SyntheticPctSamplingSampled>(", classFile.Content);
        Assert.Contains("new ConditionalSplitBranch<DFT_SampleSqlRow, SyntheticPctSamplingNotSampled>(", classFile.Content);
        Assert.Contains("new SqlBulkSink<SyntheticPctSamplingSampled>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<SyntheticPctSamplingSampled>>())", classFile.Content);
        Assert.Contains("new SqlBulkSink<SyntheticPctSamplingNotSampled>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<SyntheticPctSamplingNotSampled>>())", classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        var routerFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/PCTSAMP_TenPercentSamplingRouter.cs");
        Assert.Contains("private readonly Random _random = new(424242);", routerFile.Content);
        Assert.Contains("return _random.Next(100) < 30 ? 0 : 1;", routerFile.Content);
        Assert.Contains(": IRowRouter<DFT_SampleSqlRow>", routerFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(routerFile.Content);

        // A pure row router transforms no column at all -- both branch transforms are plain
        // passthroughs, no gap about a missing Derived Column (unlike Conditional Split's own
        // direct-copy restriction, deliberately not applied here -- see GeneratePctSamplingFlow's
        // own doc comment for why a Derived-Column-less flow is NOT a gap for this component).
        // Only the two unavoidable non-blocking gaps remain (SqlCommand-mode advisory,
        // Notification).
        Assert.Equal(2, result.Gaps.Count);
        Assert.DoesNotContain(result.Gaps, g => g.IsBlocking);
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticPctSampling.Notification");
    }

    [Fact]
    public void Generate_WiresARealShapedScdFlow_WithACommandOnlyBranchAndAConvergedInsertBranch()
    {
        // SyntheticScd.dtsx (Phase 7 of the unsupported-component-types plan): reproduces the one
        // real evidenced SCD package's own chain shape link for link -- OLE DB Source -> SCD ->
        // {Changing Attribute Updates Output -> bare OLE DB Command (Type 1 in-place UPDATE, no
        // destination), Historical Attribute Inserts Output -> OLE DB Command (close the old row)
        // -> Union All (converging with New Output) -> Derived Column (StartDate <- GETDATE()) ->
        // OLE DB Destination}. Unchanged/Fixed Attribute/Inferred Member Updates all left unwired,
        // exactly as in the real package. Verified end-to-end: generated, built against a copy of
        // Etl.Core (0 warnings/0 errors), and actually RUN against .\SQLFORPOC_2022 -- an exact
        // row-for-row match (business-key/attribute columns; timestamps necessarily differ by
        // wall clock) against a real dtexec run of this identical fixture. See
        // Tools/SsisExtractor/CLAUDE.md's own Phase 7 section for the full account.
        var package = LoadSyntheticFixture("SyntheticScd.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticScd.cs");
        Assert.Contains("internal async Task DFT_LoadDimEmployee(IUnitOfWork uow, CancellationToken ct)", classFile.Content);
        Assert.Contains("var step = new SlowlyChangingDimensionStep<DFT_LoadDimEmployeeSqlRow, int>(", classFile.Content);
        Assert.Contains("loadCt => SCD_DimEmployeeCache.LoadAsync(SqlConnectionStringFactory.Build(Db()), loadCt),", classFile.Content);
        Assert.Contains("row => row.EmpId,", classFile.Content);
        Assert.Contains("[ScdColumnRole.Fixed, ScdColumnRole.Changing, ScdColumnRole.Historical],", classFile.Content);
        Assert.Contains("failOnFixedAttributeChange: false,", classFile.Content);
        Assert.Contains("updateChangingAttributeHistory: false", classFile.Content);

        // Changing Attribute Updates Output: command only, no sink -- the in-place UPDATE IS the
        // whole effect (sink parameter is a bare `null`, not a ConditionalSplitBranch).
        Assert.Contains(
            "ChangingAttributeUpdates = new ScdBranch<DFT_LoadDimEmployeeSqlRow>(\n" +
            "                    \"Changing Attribute Updates Output\",\n" +
            "                    null,\n" +
            "                    \"UPDATE [dbo].[SyntheticScdDim] SET [LastName] = {0} WHERE [EmpId] = {1} AND [EndDate] IS NULL\",",
            classFile.Content);

        // Historical Attribute Inserts Output: command AND sink -- close the old row, then insert
        // through the same destination New Output feeds (the Union All convergence).
        Assert.Contains(
            "HistoricalAttributeInserts = new ScdBranch<DFT_LoadDimEmployeeSqlRow>(\n" +
            "                    \"Historical Attribute Inserts Output\",\n" +
            "                    new ConditionalSplitBranch<DFT_LoadDimEmployeeSqlRow, SyntheticScdDim>(\"Historical Attribute Inserts Output\", new SCD_DimEmployee_HistoricalAttributeInsertsOutput(), OLEDST_DimEmployee()),\n" +
            "                    \"UPDATE [dbo].[SyntheticScdDim] SET [EndDate] = GETDATE() WHERE [EmpId] = {0} AND [EndDate] IS NULL\",",
            classFile.Content);

        // New Output: insert only, sharing the SAME OLEDST_DimEmployee() sink factory call as
        // Historical's own insert half -- one shared destination, not two.
        Assert.Contains(
            "New = new ScdBranch<DFT_LoadDimEmployeeSqlRow>(\n" +
            "                    \"New Output\",\n" +
            "                    new ConditionalSplitBranch<DFT_LoadDimEmployeeSqlRow, SyntheticScdDim>(\"New Output\", new SCD_DimEmployee_NewOutput(), OLEDST_DimEmployee()),\n" +
            "                    null,\n" +
            "                    null),",
            classFile.Content);

        // Unchanged/Fixed Attribute/Inferred Member Updates are all absent entirely -- a null
        // ScdBranches<T> property, not an empty/no-op branch object.
        Assert.DoesNotContain("Unchanged =", classFile.Content);
        Assert.DoesNotContain("FixedAttribute =", classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        var newTransform = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SCD_DimEmployee_NewOutput.cs");
        Assert.Contains("StartDate = DER_StartDate.StartDate(row, ctx),", newTransform.Content);
        CodeAssertions.AssertNoSyntaxErrors(newTransform.Content);

        var historicalTransform = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SCD_DimEmployee_HistoricalAttributeInsertsOutput.cs");
        Assert.Contains("StartDate = DER_StartDate.StartDate(row, ctx),", historicalTransform.Content);
        CodeAssertions.AssertNoSyntaxErrors(historicalTransform.Content);

        var cache = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SCD_DimEmployeeCache.cs");
        Assert.Contains("public static async Task<Dictionary<int, object?[]>> LoadAsync(string connectionString, CancellationToken ct)", cache.Content);
        Assert.Contains(
            "SELECT * FROM ( SELECT [Designation], [EmpId], [FirstName], [LastName],[StartDate],[EndDate] FROM [dbo].[SyntheticScdDim] ) AS scd_reference WHERE [StartDate] IS NOT NULL AND [EndDate] IS NULL",
            cache.Content);
        // Columns read by NAME, not position -- the dimension query's own SELECT list order does
        // not match the input columns' order.
        Assert.Contains("reader.GetOrdinal(\"FirstName\")", cache.Content);
        Assert.Contains("reader.GetOrdinal(\"LastName\")", cache.Content);
        Assert.Contains("reader.GetOrdinal(\"Designation\")", cache.Content);
        CodeAssertions.AssertNoSyntaxErrors(cache.Content);

        // Only the non-blocking advisories every real-shaped flow like this one gets (SqlCommand
        // advisory, Notification) -- no blocking gap for the dangling third Union All input, and
        // no gap at all for the two unwired outputs (they're simply absent, not reported).
        Assert.DoesNotContain(result.Gaps, g => g.IsBlocking);
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticScd.Notification");
    }

    [Fact]
    public void Generate_WiresAnScdFlow_WithACompositeBusinessKey()
    {
        // SyntheticScdCompositeKey.dtsx (Phase 4 of the gap-audit plan,
        // concurrent-whistling-turing.md, 2026-09-16): a Microsoft.SCD declaring TWO business-key
        // columns (RegionCode, StoreCode). Verified end-to-end against a real dtexec run and a
        // real generated run -- exact match: a row sharing only ONE of the two key columns with
        // the existing dimension row is New, never wrongly matched (AND-of-equality, confirmed
        // real, not guessed). See CLAUDE.md's own account. This test pins the generated tuple-key
        // wiring.
        var package = LoadSyntheticFixture("SyntheticScdCompositeKey.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        Assert.DoesNotContain(result.Gaps, g => g.IsBlocking);

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticScdCompositeKey.cs");
        // A composite key becomes a plain, unnamed C# tuple -- byte-identical to the single-key
        // shape's own `int`/`row => row.EmpId` whenever there is exactly one key column (see
        // Generate_WiresARealShapedScdFlow_WithACommandOnlyBranchAndAConvergedInsertBranch above).
        Assert.Contains("var step = new SlowlyChangingDimensionStep<DFT_ScdCompositeSqlRow, (string, string)>(", classFile.Content);
        Assert.Contains("row => (row.RegionCode, row.StoreCode),", classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        var cache = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SCD_DimStoreCache.cs");
        Assert.Contains("public static async Task<Dictionary<(string, string), object?[]>> LoadAsync(string connectionString, CancellationToken ct)", cache.Content);
        Assert.Contains("var keyOrdinal0 = reader.GetOrdinal(\"RegionCode\");", cache.Content);
        Assert.Contains("var keyOrdinal1 = reader.GetOrdinal(\"StoreCode\");", cache.Content);
        Assert.Contains(
            "var key = (reader.GetFieldValue<string>(keyOrdinal0), reader.GetFieldValue<string>(keyOrdinal1));",
            cache.Content);
        CodeAssertions.AssertNoSyntaxErrors(cache.Content);
    }

    [Fact]
    public void Generate_WiresAnScdFlow_WithAnAttributeProducedByAnInterveningDataConversion()
    {
        // SyntheticScdDataConversionAttribute.dtsx (gap-audit plan glittery-spinning-mochi.md,
        // 2026-09-18, Step 3): OLE DB Source (EmpId, FirstName) -> Derived Column (CodeText <-
        // "HELLO WORLD", a static literal referencing no column, matching the real evidenced
        // package's own "ssc" shape exactly) -> Data Conversion (CodeText wstr -> CodeStr
        // DT_STR,5) -> SCD (key: EmpId; Changing: FirstName; Fixed: CodeStr). Reproduces the real
        // evidenced gap this round closes: Sales-DataWarehouse-with-Incremental-Load-SSIS-ETL-
        // Pipeline's own dimcustomer.dtsx compares a "Copy of ssc" attribute (ColumnType=4)
        // produced by an intervening Microsoft.DataConvert, which PackageGenerator.GenerateScdFlow
        // used to reject outright ("compares column(s) not present on its own source's output")
        // even though the exact resolution machinery already existed
        // (TransformEmitter.TranslateDataConversion). Verified end-to-end beyond this test: a real
        // dtexec run of this exact fixture measured DT_STR's own truncation under IgnoreFailure
        // ("HELLO WORLD" -> "HELLO", matching DT_WSTR's already-measured rule) and the full
        // routing table (Unchanged/Fixed/New/Changing, one row each); the generated exe, built
        // against a copy of Tools/Etl.Core (0 warnings/0 errors) and run against
        // .\SQLFORPOC_2022 with the output tables reset first, wrote the EXACT SAME rows -- see
        // Tools/SsisExtractor/CLAUDE.md's own account.
        var package = LoadSyntheticFixture("SyntheticScdDataConversionAttribute.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        Assert.DoesNotContain(result.Gaps, g => g.IsBlocking);
        Assert.DoesNotContain(result.Gaps, g => g.Reason.Contains("compares column(s) not present"));

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticScdDataConversionAttribute.cs");
        // The business key stays a plain row reference (EmpId is a raw source column); the
        // attribute array mixes an ordinary passthrough (FirstName) with the Data-Conversion-
        // sourced one, resolved through the Derived Column's own literal expression ("HELLO
        // WORLD") rather than a nonexistent row.CodeText property.
        Assert.Contains("row => row.EmpId,", classFile.Content);
        Assert.Contains(
            "row => new object?[] { row.FirstName, SyntheticScdDataConversionAttribute.Ssis.SsisFn.ToWstr(\"HELLO WORLD\", 5) },",
            classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        // Each branch's own destination transform independently resolves CodeStr too (for the
        // INSERT), through the exact same chained resolution -- TransformEmitter.Emit's own
        // convertedByName branch, not just GenerateScdFlow's attribute-array selector.
        var unchangedTransform = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SCD_DimCustomer_UnchangedOutput.cs");
        Assert.Contains("SsisFn.ToWstr(\"HELLO WORLD\", 5)", unchangedTransform.Content);
        CodeAssertions.AssertNoSyntaxErrors(unchangedTransform.Content);
    }

    [Fact]
    public void Generate_WrapsTheRowSourceInSortingRowSource_ForAStandaloneSort()
    {
        // SyntheticStandaloneSort.dtsx (gap-audit Phase 3.5, 2026-09-02): OLE DB Source (seed
        // rows deliberately out of ID order: 3, 1, 2) -> Sort (by ID, ascending) -> Flat File
        // Destination. Confirmed beyond this test: a real dtexec run of this exact fixture wrote
        // "1,Alice / 2,Bob / 3,Carol"; the generated exe, built against a copy of Etl.Core (0
        // warnings/0 errors) and run against .\SQLFORPOC_2022 with the output file cleared
        // first, wrote the EXACT SAME byte-for-byte content -- see
        // Tools/SsisExtractor/CLAUDE.md's own "standalone Sort" section.
        var package = LoadSyntheticFixture("SyntheticStandaloneSort.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticStandaloneSort.cs");
        Assert.Contains(
            "source = new SortingRowSource<DFT_SortSqlRow, int>(\"DFT_Sort.Sort\", source, row => row.ID, Comparer<int>.Default);",
            classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        // Three unavoidable/unrelated gaps -- the SqlCommand-mode column-naming advisory (same
        // as every other SqlCommand-sourced flow's own test), the unresolved-auth-mode fallback
        // (this fixture's own connection manager carries no auth info), and Notification. None
        // of them is about the Sort itself, confirming it generated with zero gaps of its own.
        Assert.Equal(3, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticStandaloneSort.Notification");
    }

    [Fact]
    public void Generate_WrapsBothSidesInMergeInterleaveRowSource_ForAGenuinelyMultiSourceMerge()
    {
        // SyntheticMergeInterleaveProbe.dtsx (gap-audit Phase 3.6, 2026-09-02): two genuinely
        // independent OLE DB Sources with disjoint, interleaved key ranges (Left: 1,3,5; Right:
        // 2,4,6, both seeded out of order) -> Sort each -> Microsoft.Merge -> Flat File
        // Destination. Confirmed beyond this test: a real dtexec run of this exact fixture wrote
        // "1,2,3,4,5,6" -- a TRUE sort-preserving interleave, not concatenation; the generated
        // exe, built against a copy of Etl.Core (0 warnings/0 errors) and run against
        // .\SQLFORPOC_2022, wrote the EXACT SAME byte-for-byte content -- see
        // Tools/SsisExtractor/docs/report-schema.md's own "gap-audit Phase 3.6" section.
        var package = LoadSyntheticFixture("SyntheticMergeInterleaveProbe.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticMergeInterleaveProbe.cs");
        Assert.Contains("var source = MRG_Interleave(uow);", classFile.Content);
        Assert.Contains(
            "return new MergeInterleaveRowSource<MRG_InterleaveRow, int>(\"MRG_Interleave\", BuildSide0(), BuildSide1(), row => row.ID, Comparer<int>.Default);",
            classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        // Five unavoidable/unrelated gaps -- a SqlCommand-mode column-naming advisory PER SIDE
        // (both sources are SqlCommand-mode), the unresolved-auth-mode fallback (a Flat File
        // Destination resolves no target server/database of its own), Notification, and (a real,
        // previously-silent gap caught 2026-09-06 by an independent review, same as
        // Generate_WrapsBothSidesInConcatenatingRowSource_ForAGenuinelyMultiSourceUnionAll below)
        // the no-starter-test-coverage advisory every GenerateUnionFlow-shaped flow now gets.
        // None of them is about the Merge/interleave logic itself.
        Assert.Equal(5, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticMergeInterleaveProbe.Notification");
    }

    [Fact]
    public void Generate_WrapsBothSidesInConcatenatingRowSource_ForAGenuinelyMultiSourceUnionAll()
    {
        // SyntheticUnionTwoSources.dtsx (gap-audit Phase 3.6, 2026-09-02): two genuinely
        // independent OLE DB Sources, no Sort at all -> Microsoft.UnionAll -> Flat File
        // Destination. The generated exe, built against a copy of Etl.Core (0 warnings/0
        // errors) and run against .\SQLFORPOC_2022, wrote each side's own rows in full, in that
        // side's own order, before moving to the next -- see
        // Tools/SsisExtractor/docs/report-schema.md's own "gap-audit Phase 3.6" section for the
        // real, left-unresolved dtexec construction quirk that makes this shape's OWN dtexec run
        // unavailable (unlike the Merge case above).
        var package = LoadSyntheticFixture("SyntheticUnionTwoSources.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var classFile = Assert.Single(result.Files, f => f.RelativePath == "SyntheticUnionTwoSources.cs");
        Assert.Contains("var source = UNION_TwoSources(uow);", classFile.Content);
        Assert.Contains(
            "return new ConcatenatingRowSource<UNION_TwoSourcesRow>(\"UNION_TwoSources\", [BuildSide0(), BuildSide1()]);",
            classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);

        // 5, not 4: a real, previously-silent gap caught 2026-09-06 by an independent review --
        // this flow used to reach this point with NO starter test coverage AND no acknowledgment
        // at all (its own UnionFlowSource matches neither the generic SQL/Excel source-test
        // loop's type checks nor csvSampleCandidates, and ComponentTestEmitter has no
        // Union-specific test method the way Merge Join gets its own mapper test).
        Assert.Equal(5, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticUnionTwoSources.Notification");
        var testOracleGap = Assert.Single(result.Gaps, g => g.Kind == GapKind.TestOracle);
        Assert.False(testOracleGap.IsBlocking);
        Assert.Contains("no starter test coverage in this pilot", testOracleGap.Reason);
        Assert.DoesNotContain(result.SiblingFiles, f => f.RelativePath.EndsWith("SourceTests.cs"));
    }
}
