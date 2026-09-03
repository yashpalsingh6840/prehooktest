using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
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

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains("var syntheticPostFlowTargetFlow = new DataFlowStep<", programFile.Content);
        Assert.Contains(
            "var sQL_PostLoadStep = new ExecuteSqlStep(\"SQL_PostLoad\", \"UPDATE dbo.SyntheticPostFlowTarget SET Name = UPPER(Name);\", sp.GetRequiredService<ILogger<ExecuteSqlStep>>());",
            programFile.Content);
        Assert.Contains("Steps: [syntheticPostFlowTargetFlow, sQL_PostLoadStep],", programFile.Content);
        Assert.Contains("PreLoadFileActions: []);", programFile.Content);
        Assert.Contains("PreLoadStatements: [\"TRUNCATE TABLE dbo.SyntheticPostFlowTarget;\"],", programFile.Content);

        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

        // The only real gap is the unavoidable, non-blocking Notification one.
        var gap = Assert.Single(result.Gaps);
        Assert.Equal("SyntheticPostFlowSql.Notification", gap.Location);
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

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains(
            "var cM_SqlSecondDbConnectionString = SqlConnectionStringFactory.Build(builder.Configuration.GetSection(\"SecondaryConnections:CM_SqlSecondDb\").Get<DatabaseOptions>()",
            programFile.Content);
        Assert.Contains(
            "var sQL_CacheSet_SecondDbStep = new SecondaryConnectionSqlStep(\"SQL_CacheSet_SecondDb\", \"CM_SqlSecondDb\", cM_SqlSecondDbConnectionString, "
            + "\"INSERT INTO dbo.SyntheticSecondConnectionLog (LogKey, LogValue) VALUES (N'LegacyImport', N'fixed-width import completed');\", "
            + "sp.GetRequiredService<ILogger<SecondaryConnectionSqlStep>>());",
            programFile.Content);
        // Never an ordinary ExecuteSqlStep for this task -- that would run it on the wrong connection.
        Assert.DoesNotContain("new ExecuteSqlStep(\"SQL_CacheSet_SecondDb\"", programFile.Content);
        Assert.Contains("Steps: [syntheticSecondConnectionTargetFlow, sQL_CacheSet_SecondDbStep],", programFile.Content);

        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

        var appSettingsFile = Assert.Single(result.Files, f => f.RelativePath == "appsettings.json");
        Assert.Contains("\"SecondaryConnections\"", appSettingsFile.Content);
        Assert.Contains("\"CM_SqlSecondDb\"", appSettingsFile.Content);
        Assert.Contains("\"Database\": \"SsisPoC_Secondary\"", appSettingsFile.Content);
        // Never the password -- same rule as TargetDatabase.
        Assert.DoesNotContain("Password", appSettingsFile.Content);

        // The only real gap is the unavoidable, non-blocking Notification one.
        var gap = Assert.Single(result.Gaps);
        Assert.Equal("SyntheticSecondConnectionSql.Notification", gap.Location);
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

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains("using Etl.Core.Data;", programFile.Content);
        Assert.Contains("var dbOptions = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;", programFile.Content);
        Assert.Contains(
            "CommandText = \"SELECT ID, Amount FROM dbo.SyntheticOleDbSourceInput\" };",
            programFile.Content);
        Assert.Contains("return new SqlRowSource<SyntheticOleDbSourceTargetSqlRow>(\"OLE DB Source\", sqlOptions, SyntheticOleDbSourceTargetSqlRowReader.Read, sp.GetRequiredService<IUnitOfWork>());", programFile.Content);

        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

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

        var validTransformFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticDataConversionSplitTargetValidTransform.cs");
        Assert.Contains("SignupDate_dt = SsisFn.ToNullableDate(row.SignupDateText),", validTransformFile.Content);
        // The null-forgiving "!" before .Value in the non-null ternary branch -- without it,
        // calling SsisFn.ToNullableDate twice (once for the ISNULL check, once here) is a build
        // ERROR (CS8629) under this project's Nullable+TreatWarningsAsErrors, since Roslyn's
        // flow analysis never narrows a repeated METHOD CALL the way it narrows a plain
        // row-property reference. Caught only by actually building the generated project, not
        // unit tests alone.
        Assert.Contains("TenureDays = ((SsisFn.ToNullableDate(row.SignupDateText) is null) ? -(1) : SsisFn.DateDiffDays(SsisFn.ToNullableDate(row.SignupDateText)!.Value, ctx.LoadedAtUtc)),", validTransformFile.Content);

        var invalidTransformFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticDataConversionSplitTargetInvalidTransform.cs");
        Assert.Contains("SignupDate_dt = SsisFn.ToNullableDate(row.SignupDateText),", invalidTransformFile.Content);

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

        // Two expected gaps: the SqlCommand-mode column-name assumption, and the unavoidable
        // Notification one -- same shape as SyntheticDataConversion.dtsx's own test.
        Assert.Equal(2, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Reason.Contains("assumes each result-set column is named/aliased"));
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticDataConversionSplit.Notification");
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
        var highFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticRemergeTargetHighTransform.cs");
        var lowFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticRemergeTargetLowTransform.cs");

        // Each transform combines the flow's shared Derived Column (LoadedAtUtc) with its OWN
        // per-branch one (Segment) -- proving TransformEmitter's merged-DerivedColumns list
        // resolves columns from both, not just one.
        Assert.Contains("LoadedAtUtc = ctx.LoadedAtUtc,", highFile.Content);
        Assert.Contains("Segment = \"High\",", highFile.Content);
        Assert.Contains("LoadedAtUtc = ctx.LoadedAtUtc,", lowFile.Content);
        Assert.Contains("Segment = \"Low\",", lowFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(highFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(lowFile.Content);

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        // ONE AddBulkSink<> call, not two -- PortfolioDigest/ProgramEmitter already dedupe by
        // entity name (unaffected by this change), reconfirmed here for the converged case.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(programFile.Content, "AddBulkSink<SyntheticRemergeTarget>"));
        // No per-branch IRowTransform<> DI registration -- see ProgramEmitter's own comment on
        // why (two branches sharing an entity would collide on the same closed generic).
        Assert.DoesNotContain("AddScoped<IRowTransform<", programFile.Content);
        Assert.Contains("new SyntheticRemergeTargetHighTransform(),", programFile.Content);
        Assert.Contains("new SyntheticRemergeTargetLowTransform(),", programFile.Content);
        // Both branches still share the one bulk sink for the one shared table.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(programFile.Content, "GetRequiredService<IBulkSink<SyntheticRemergeTarget>>\\(\\)").Count);
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

        // Only the two unavoidable/non-blocking gaps -- the SqlCommand column-name assumption
        // and Notification. Confirms PortfolioDigest.IsBlockingGap would count this package as
        // fully generatable (0 blocking gaps), not just "produces some files".
        Assert.Equal(2, result.Gaps.Count);
        Assert.All(result.Gaps, g => Assert.False(g.IsBlocking));
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

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains("var fST_PostLoadCopyStep = new FileSystemStep(\"FST_PostLoadCopy\", new FileSystemPreLoadAction(FileSystemOperation.Copy,", programFile.Content);
        Assert.Contains("Steps: [syntheticFileSystemTaskTargetFlow, fST_PostLoadCopyStep],", programFile.Content);
        Assert.Contains("PreLoadFileActions: [new FileSystemPreLoadAction(FileSystemOperation.Copy,", programFile.Content);
        Assert.Contains("archived-preload.txt", programFile.Content);
        Assert.Contains("archived-postflow.txt", programFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

        // Only the unavoidable, non-blocking Notification gap -- this fixture is fully
        // generatable.
        var gap = Assert.Single(result.Gaps);
        Assert.Equal("SyntheticFileSystemTask.Notification", gap.Location);
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
        Assert.Contains(result.Files, f => f.RelativePath == "Mapping/SyntheticHighValueTransform.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Mapping/SyntheticLowValueTransform.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Mapping/ConditionalSplitRouter.cs");

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains("using Etl.Core.Data;", programFile.Content);
        Assert.Contains("builder.Services.AddBulkSink<SyntheticHighValue>();", programFile.Content);
        Assert.Contains("builder.Services.AddBulkSink<SyntheticLowValue>();", programFile.Content);
        Assert.Contains("builder.Services.AddScoped<IRowRouter<DFT_ConditionalSplitDemoSqlRow>, ConditionalSplitRouter>();", programFile.Content);
        // No per-branch IRowTransform<> DI registration -- each branch's transform is
        // constructed directly (`new`) below, since two branches can share EntityName (a Union
        // All remerge) and would otherwise collide on the same closed generic registration.
        Assert.DoesNotContain("AddScoped<IRowTransform<DFT_ConditionalSplitDemoSqlRow, SyntheticHighValue>", programFile.Content);
        Assert.DoesNotContain("AddScoped<IRowTransform<DFT_ConditionalSplitDemoSqlRow, SyntheticLowValue>", programFile.Content);
        Assert.Contains("var dFT_ConditionalSplitDemoSplit = new ConditionalSplitStep<DFT_ConditionalSplitDemoSqlRow>(", programFile.Content);
        Assert.Contains("new ConditionalSplitBranch<DFT_ConditionalSplitDemoSqlRow, SyntheticHighValue>(", programFile.Content);
        Assert.Contains("new ConditionalSplitBranch<DFT_ConditionalSplitDemoSqlRow, SyntheticLowValue>(", programFile.Content);
        Assert.Contains("new SyntheticHighValueTransform(),", programFile.Content);
        Assert.Contains("new SyntheticLowValueTransform(),", programFile.Content);
        Assert.Contains("Steps: [dFT_ConditionalSplitDemoSplit],", programFile.Content);
        Assert.Contains("PreLoadFileActions: []);", programFile.Content);

        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

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

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains("using Etl.Core.Data;", programFile.Content);
        // Flat file sinks are constructed directly (not via AddBulkSink<T>(), which would
        // resolve to a SqlBulkSink<T> -- wrong for a file).
        Assert.DoesNotContain("AddBulkSink<DFT_ExportDelimited>", programFile.Content);
        Assert.DoesNotContain("AddBulkSink<DFT_ExportFixedWidth>", programFile.Content);
        Assert.Contains("builder.Services.AddBulkSink<SyntheticFlatFileDestinationLog>();", programFile.Content);
        Assert.Contains("builder.Services.AddScoped<IBulkSink<DFT_ExportDelimited>>(sp =>", programFile.Content);
        Assert.Contains("new FlatFileColumnFormat(\"CustomerID\", null, \",\")", programFile.Content);
        Assert.Contains("new FlatFileColumnFormat(\"CleanEmail\", null, \"\\r\\n\")", programFile.Content);
        Assert.Contains("\"CustomerID,FullName,CleanEmail\\r\\n\"", programFile.Content); // the header line
        Assert.Contains("builder.Services.AddScoped<IBulkSink<DFT_ExportFixedWidth>>(sp =>", programFile.Content);
        Assert.Contains("new FlatFileColumnFormat(\"CustomerID\", 10, \"\")", programFile.Content);
        Assert.Contains("new FlatFileColumnFormat(\"FullName\", 15, \"\")", programFile.Content);
        Assert.Contains("new FlatFileColumnFormat(\"RowEnd\", null, \"\\r\\n\")", programFile.Content);
        Assert.Contains("fileOptions[\"DFT_ExportDelimited\"].ResolvedPath", programFile.Content);
        Assert.Contains("fileOptions[\"DFT_ExportFixedWidth\"].ResolvedPath", programFile.Content);
        Assert.Contains("Steps: [dFT_ExportDelimitedFlow, dFT_ExportFixedWidthFlow, syntheticFlatFileDestinationLogFlow],", programFile.Content);

        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(dbContextFile.Content);

        // Five expected gaps: the SqlCommand-mode column-name assumption on all three OLE DB
        // Sources, the unavoidable Notification one, and the parallelism advisory (all three root
        // flows are precedence-independent) -- neither flat-file flow reports its own gap.
        Assert.Equal(5, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticFlatFileDestination.Parallelism");
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

        var canadaFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticSortMergeTargetCanadaTransform.cs");
        var restFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticSortMergeTargetRestOfWorldTransform.cs");

        // Every destination column is a plain passthrough (no Derived Column of its own beyond
        // the shared LoadedAtUtc) -- proving TransformEmitter's existing "row.PipelineColumnName"
        // fallback resolves correctly straight through Sort/Merge, with zero new
        // value-resolution code needed for this feature.
        Assert.Contains("ID = row.ID,", canadaFile.Content);
        Assert.Contains("Name = row.Name,", canadaFile.Content);
        Assert.Contains("LoadedAtUtc = ctx.LoadedAtUtc,", canadaFile.Content);
        Assert.Contains("ID = row.ID,", restFile.Content);
        Assert.Contains("Name = row.Name,", restFile.Content);
        Assert.Contains("LoadedAtUtc = ctx.LoadedAtUtc,", restFile.Content);
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

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains("var fEL_SampleFilesStep = new ForEachLoopStep(", programFile.Content);
        Assert.Contains("\"FEL_SampleFiles\",", programFile.Content);
        Assert.Contains("new ForEachFileLoopAction(", programFile.Content);
        Assert.Contains("ForEachFileNameMode.NameAndExtension),", programFile.Content);
        Assert.Contains(
            "currentFile => \"INSERT INTO dbo.SyntheticForEachFileLoopLog (FileName) VALUES (N'\" + currentFile + \"');\",",
            programFile.Content);
        Assert.Contains("sp.GetRequiredService<ILogger<ForEachLoopStep>>());", programFile.Content);
        Assert.Contains("fEL_SampleFilesStep", programFile.Content); // present in the final Steps: [...] wiring too
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

        // Only three unavoidable/non-blocking gaps -- the SqlCommand column-name assumption on
        // DFT_Load's own source, Notification, and the parallelism advisory (DFT_Load and
        // FEL_SampleFiles are precedence-independent siblings). No ForEach-Loop-specific gap.
        Assert.Equal(3, result.Gaps.Count);
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

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        // No top-level IRowSource<TRow> registration for this step at all -- the source is
        // constructed fresh, inline, per iteration.
        Assert.DoesNotContain("AddScoped<IRowSource<SyntheticForEachDataFlowLoopTargetCsvRow>>", programFile.Content);
        Assert.Contains(
            "builder.Services.AddScoped<IRowTransform<SyntheticForEachDataFlowLoopTargetCsvRow, SyntheticForEachDataFlowLoopTarget>, SyntheticForEachDataFlowLoopTargetTransform>();",
            programFile.Content);
        Assert.Contains(
            "var fEL_SampleFilesStep = new ForEachFileDataFlowStep<SyntheticForEachDataFlowLoopTargetCsvRow, SyntheticForEachDataFlowLoopTarget>(",
            programFile.Content);
        Assert.Contains("new ForEachFileLoopAction(", programFile.Content);
        Assert.Contains(
            "currentFile => new CsvRowSource<SyntheticForEachDataFlowLoopTargetCsvRow>(\"Flat File Source\", new CsvSourceOptions { FilePath = currentFile }, new SyntheticForEachDataFlowLoopTargetCsvRowMap()),",
            programFile.Content);
        Assert.Contains("sp.GetRequiredService<IBulkSink<SyntheticForEachDataFlowLoopTarget>>(),", programFile.Content);
        Assert.Contains("sp.GetRequiredService<ILogger<DataFlowStep<SyntheticForEachDataFlowLoopTargetCsvRow, SyntheticForEachDataFlowLoopTarget>>>());", programFile.Content);
        Assert.Contains("fEL_SampleFilesStep", programFile.Content); // present in the final Steps: [...] wiring too
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

        // Only the unavoidable, non-blocking Notification gap.
        var gap = Assert.Single(result.Gaps);
        Assert.Equal("SyntheticForEachDataFlowLoop.Notification", gap.Location);
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

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains(
            "builder.Services.AddScoped<IRowSource<SyntheticAggregateTargetAggregateRow>>(sp =>",
            programFile.Content);
        Assert.Contains(
            "return new AggregateRowSource<AGG_ByRegionSourceSqlRow, string, SyntheticAggregateTargetAggregateRow>(",
            programFile.Content);
        Assert.Contains("row => row.Region,", programFile.Content);
        Assert.Contains("CustomerCount = rows.Count(r => r.CustomerID != null)", programFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

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

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains("TotalRows = rows.Count,", programFile.Content);
        Assert.Contains("DistinctAmounts = rows.Select(r => r.Amount).Where(v => v != null).Distinct().Count(),", programFile.Content);
        Assert.Contains("SumAmount = rows.All(r => r.Amount == null) ? null : rows.Sum(r => r.Amount),", programFile.Content);
        Assert.Contains("AverageAmount = rows.Average(r => r.Amount),", programFile.Content);
        Assert.Contains("MinAmount = rows.Min(r => r.Amount),", programFile.Content);
        Assert.Contains("MaxAmount = rows.Max(r => r.Amount)", programFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

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

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains("using Etl.Core.Excel;", programFile.Content);
        Assert.Contains("using SyntheticExcelSource.Excel;", programFile.Content);
        Assert.Contains(
            "var excelOptions = new ExcelSourceOptions { FilePath = fileOptions[\"Excel Source\"].ResolvedPath, WorksheetName = \"DripEligibility$\", HasHeaderRow = true };",
            programFile.Content);
        Assert.Contains(
            "return new ExcelRowSource<SyntheticExcelSourceTargetExcelRow>(\"Excel Source\", excelOptions, SyntheticExcelSourceTargetExcelRowReader.Read);",
            programFile.Content);
        Assert.Contains("var syntheticExcelSourceTargetFlow = new DataFlowStep<SyntheticExcelSourceTargetExcelRow, SyntheticExcelSourceTarget>(", programFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

        // Only the unavoidable, non-blocking Notification gap -- no Derived Column needed, and
        // no numeric-type-mismatch gap either, since this fixture's own CustomerID is FLOAT
        // (matching the worksheet's real r8 type exactly, unlike RBC_Demo_ETL's own INT column).
        var gap = Assert.Single(result.Gaps);
        Assert.Equal("SyntheticExcelSource.Notification", gap.Location);
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

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains(
            "var excelOptions = new ExcelSourceOptions { FilePath = fileOptions[\"Excel Source\"].ResolvedPath, WorksheetName = \"DripEligibility$\", HasHeaderRow = true };",
            programFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

        var gap = Assert.Single(result.Gaps);
        Assert.Equal("SyntheticExcelSourceSqlCommand.Notification", gap.Location);
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

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains(
            "return new FilteringRowSource<SyntheticExcelSourceSqlCommandWhereTargetExcelRow>(\"Excel Source\", new ExcelRowSource<SyntheticExcelSourceSqlCommandWhereTargetExcelRow>(\"Excel Source\", excelOptions, SyntheticExcelSourceSqlCommandWhereTargetExcelRowReader.Read), row => row.CustomerID > 5);",
            programFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

        var gap = Assert.Single(result.Gaps);
        Assert.Equal("SyntheticExcelSourceSqlCommandWhere.Notification", gap.Location);
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

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.DoesNotContain("using SyntheticOleDbCommand.Mapping;", programFile.Content);
        Assert.Contains(
            "var dFT_FlagCustomersStep = new OleDbCommandStep<DFT_FlagCustomersSqlRow>(",
            programFile.Content);
        Assert.Contains(
            "\"UPDATE dbo.SyntheticOleDbCommandTarget SET Flagged = 1 WHERE CustomerID = {0}\",",
            programFile.Content);
        Assert.Contains("row => new object?[] { row.CustomerID },", programFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

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

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains("var packageVariables = new PackageVariables();", programFile.Content);
        Assert.Contains(
            "new CountingRowSource<SyntheticRowCountVariableTargetSqlRow>(\"DFT_Load.RowCount\", sp.GetRequiredService<IRowSource<SyntheticRowCountVariableTargetSqlRow>>(), packageVariables, \"User::RowsLoaded\"),",
            programFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

        // Only the unavoidable/expected gaps: the SqlCommand-mode advisory, the Script Task seam
        // (this fixture's post-flow reader), and Notification -- none about RC_RowsLoaded itself.
        Assert.Equal(3, result.Gaps.Count);
        Assert.DoesNotContain(result.Gaps, g => g.Location.Contains("RC_RowsLoaded", StringComparison.Ordinal));
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

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains("var dFT_FixedWidthImportSplit = new MulticastStep<DFT_FixedWidthImportSqlRow>(", programFile.Content);
        Assert.Contains("builder.Services.AddBulkSink<SyntheticMulticastTarget>();", programFile.Content);
        Assert.Contains("builder.Services.AddScoped<IBulkSink<DFT_FixedWidthImportFFDST_AuditTrail_APPEND>>(sp =>", programFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

        var flatFileTransform = Assert.Single(result.Files, f => f.RelativePath == "Mapping/DFT_FixedWidthImportFFDST_AuditTrail_APPENDTransform.cs");
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

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains(
            "new SortingRowSource<DFT_SortSqlRow, int>(\"DFT_Sort.Sort\", sp.GetRequiredService<IRowSource<DFT_SortSqlRow>>(), row => row.ID, Comparer<int>.Default),",
            programFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

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

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains(
            "return new MergeInterleaveRowSource<MRG_InterleaveRow, int>(\n" +
            "        \"MRG_Interleave\", BuildSide0(), BuildSide1(),\n" +
            "        row => row.ID, Comparer<int>.Default);",
            programFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

        // Four unavoidable/unrelated gaps -- a SqlCommand-mode column-naming advisory PER SIDE
        // (both sources are SqlCommand-mode), the unresolved-auth-mode fallback (a Flat File
        // Destination resolves no target server/database of its own), and Notification. None of
        // them is about the Merge/interleave itself.
        Assert.Equal(4, result.Gaps.Count);
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

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains(
            "return new ConcatenatingRowSource<UNION_TwoSourcesRow>(\"UNION_TwoSources\", [BuildSide0(), BuildSide1()]);",
            programFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

        Assert.Equal(4, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticUnionTwoSources.Notification");
    }
}
