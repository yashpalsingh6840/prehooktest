using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Codegen.Tests;

public class TransformEmitterTests
{
    [Fact]
    public void Emit_ResolvesAPlainPassthroughColumn_WhenSourceIsDoubleAndDestinationIsInt()
    {
        // The exact real shape RBC_Demo_ETL's own DFT_ExcelImport hits: Excel Source always
        // buffers a numeric column as r8/double (Excel/ACE OLEDB has no narrower numeric type
        // at all), but the real destination table's own CustomerID column is i4/int -- SSIS's
        // own OLE DB provider coerces this implicitly at insert time. Resolved (2026-08-28) via
        // SsisFn.NarrowR8ToI4, whose round-to-nearest-ties-to-even rule was measured against a
        // real dtexec run of exactly this pairing (synthetic-numeric-coercion-tables.sql), not
        // guessed -- see CLAUDE.md's own "Numeric-passthrough-coercion" section.
        var destination = new PipelineComponentSpec
        {
            RefId = "DST_Test",
            Name = "OLE DB Destination",
            ComponentClassId = "Microsoft.OLEDBDestination",
            Inputs =
            [
                new PipelineInputSpec
                {
                    RefId = "DST_Test.Inputs[OLE DB Destination Input]",
                    Name = "OLE DB Destination Input",
                    Columns =
                    [
                        new PipelineInputColumnSpec
                        {
                            RefId = "DST_Test.Inputs[OLE DB Destination Input].Columns[CustomerID]",
                            CachedName = "CustomerID",
                            CachedDataType = "r8",
                            LineageId = "SRC.Outputs[Output].Columns[CustomerID]",
                            ExternalMetadataColumnId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[CustomerID]",
                        },
                        new PipelineInputColumnSpec
                        {
                            RefId = "DST_Test.Inputs[OLE DB Destination Input].Columns[ReviewedBy]",
                            CachedName = "ReviewedBy",
                            CachedDataType = "wstr",
                            LineageId = "SRC.Outputs[Output].Columns[ReviewedBy]",
                            ExternalMetadataColumnId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[ReviewedBy]",
                        },
                    ],
                    ExternalMetadataColumns =
                    [
                        new PipelineExternalMetadataColumnSpec { RefId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[CustomerID]", Name = "CustomerID", DataType = "i4" },
                        new PipelineExternalMetadataColumnSpec { RefId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[ReviewedBy]", Name = "ReviewedBy", DataType = "wstr", Length = 50 },
                    ],
                },
            ],
        };
        var pipeline = new PipelineSpec { Components = [destination] };

        var request = new TransformRequest(
            "Generated.Mapping", "DripEligibilityTransform",
            "Generated.Excel", "DripRow",
            "Generated.Model", "DripEligibility",
            "Generated.Ssis",
            pipeline, [], destination);

        var result = TransformEmitter.Emit(request);

        Assert.Empty(result.Result.Gaps);
        Assert.Equal(["NarrowR8ToI4"], result.SsisFunctionsUsed);

        var file = Assert.Single(result.Result.Files);
        Assert.Contains("CustomerID = SsisFn.NarrowR8ToI4(row.CustomerID),", file.Content);
        Assert.Contains("ReviewedBy = row.ReviewedBy,", file.Content);
        Assert.Contains("using Generated.Ssis;", file.Content);
        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Theory]
    [InlineData("i8", "NarrowI8ToI4")]
    [InlineData("numeric", "NarrowNumericToI4")]
    [InlineData("r4", "NarrowR4ToI4")]
    public void Emit_ResolvesAPlainPassthroughColumn_ForEachSpeculativeNumericToI4Pairing(string sourceDataType, string expectedHelper)
    {
        // Built speculatively 2026-08-30 (synthetic-int-numeric-coercion-tables.sql) -- no real
        // evidenced instance of any of these three pairings anywhere in the tracked portfolio,
        // named as candidates in CLAUDE.md's own "remaining known gaps" list. A real dtexec run
        // measured the same round-to-nearest-ties-to-even rule as NarrowR8ToI4 for the two
        // float-shaped pairings (numeric, r4); i8->i4 has no rounding question at all (both sides
        // are already integers).
        var destination = new PipelineComponentSpec
        {
            RefId = "DST_Test",
            Name = "OLE DB Destination",
            ComponentClassId = "Microsoft.OLEDBDestination",
            Inputs =
            [
                new PipelineInputSpec
                {
                    RefId = "DST_Test.Inputs[OLE DB Destination Input]",
                    Name = "OLE DB Destination Input",
                    Columns =
                    [
                        new PipelineInputColumnSpec
                        {
                            RefId = "DST_Test.Inputs[OLE DB Destination Input].Columns[Val]",
                            CachedName = "Val",
                            CachedDataType = sourceDataType,
                            LineageId = "SRC.Outputs[Output].Columns[Val]",
                            ExternalMetadataColumnId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[Val]",
                        },
                        new PipelineInputColumnSpec
                        {
                            RefId = "DST_Test.Inputs[OLE DB Destination Input].Columns[Name]",
                            CachedName = "Name",
                            CachedDataType = "wstr",
                            LineageId = "SRC.Outputs[Output].Columns[Name]",
                            ExternalMetadataColumnId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[Name]",
                        },
                    ],
                    ExternalMetadataColumns =
                    [
                        new PipelineExternalMetadataColumnSpec { RefId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[Val]", Name = "Val", DataType = "i4" },
                        new PipelineExternalMetadataColumnSpec { RefId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[Name]", Name = "Name", DataType = "wstr", Length = 50 },
                    ],
                },
            ],
        };
        var pipeline = new PipelineSpec { Components = [destination] };

        var request = new TransformRequest(
            "Generated.Mapping", "WidgetTransform",
            "Generated.Sql", "WidgetRow",
            "Generated.Model", "Widget",
            "Generated.Ssis",
            pipeline, [], destination);

        var result = TransformEmitter.Emit(request);

        Assert.Empty(result.Result.Gaps);
        Assert.Equal([expectedHelper], result.SsisFunctionsUsed);

        var file = Assert.Single(result.Result.Files);
        Assert.Contains($"Val = SsisFn.{expectedHelper}(row.Val),", file.Content);
        Assert.Contains("Name = row.Name,", file.Content);
        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_ReportsAGap_ForAPlainPassthroughColumn_WithAnUnevidencedNumericMismatch()
    {
        // i2 (smallint) -> i4 (int) has never been measured against a real dtexec run -- unlike
        // r8/i8/numeric/r4 -> i4 above, this must still gap rather than assume the same round-to-
        // nearest-ties-to-even (or plain-narrowing) rule applies. A wrong guess here would
        // silently corrupt data.
        var destination = new PipelineComponentSpec
        {
            RefId = "DST_Test",
            Name = "OLE DB Destination",
            ComponentClassId = "Microsoft.OLEDBDestination",
            Inputs =
            [
                new PipelineInputSpec
                {
                    RefId = "DST_Test.Inputs[OLE DB Destination Input]",
                    Name = "OLE DB Destination Input",
                    Columns =
                    [
                        new PipelineInputColumnSpec
                        {
                            RefId = "DST_Test.Inputs[OLE DB Destination Input].Columns[SmallId]",
                            CachedName = "SmallId",
                            CachedDataType = "i2",
                            LineageId = "SRC.Outputs[Output].Columns[SmallId]",
                            ExternalMetadataColumnId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[SmallId]",
                        },
                        new PipelineInputColumnSpec
                        {
                            RefId = "DST_Test.Inputs[OLE DB Destination Input].Columns[Name]",
                            CachedName = "Name",
                            CachedDataType = "wstr",
                            LineageId = "SRC.Outputs[Output].Columns[Name]",
                            ExternalMetadataColumnId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[Name]",
                        },
                    ],
                    ExternalMetadataColumns =
                    [
                        new PipelineExternalMetadataColumnSpec { RefId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[SmallId]", Name = "SmallId", DataType = "i4" },
                        new PipelineExternalMetadataColumnSpec { RefId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[Name]", Name = "Name", DataType = "wstr", Length = 50 },
                    ],
                },
            ],
        };
        var pipeline = new PipelineSpec { Components = [destination] };

        var request = new TransformRequest(
            "Generated.Mapping", "WidgetTransform",
            "Generated.Sql", "WidgetRow",
            "Generated.Model", "Widget",
            "Generated.Ssis",
            pipeline, [], destination);

        var result = TransformEmitter.Emit(request);

        var gap = Assert.Single(result.Result.Gaps);
        Assert.Equal("Widget.SmallId", gap.Location);
        Assert.Contains("buffered as short but the destination column is int", gap.Reason);

        // The MISMATCHED column is dropped from the assignments, but the file still generates
        // with every OTHER column -- same per-column-gap shape as an untranslatable Derived
        // Column expression, not a whole-flow failure.
        var file = Assert.Single(result.Result.Files);
        Assert.DoesNotContain("SmallId", file.Content);
        Assert.Contains("Name = row.Name,", file.Content);
    }

    [Fact]
    public void Emit_ResolvesAPlainPassthroughColumn_WhenSourceIsStringAndDestinationIsInt()
    {
        // The exact real shape RBC_Demo_ETL's own Package_Exports/DFT_AdoNetRoundTrip hits: an
        // ADO NET Source buffers CustomerID as wstr,20 (its own real dbo.StagingCustomers.CustomerID
        // column is text-typed), but the real destination table's own CustomerID column is i4/int.
        // Resolved (2026-08-30) via SsisFn.ParseWstrToI4, whose round-to-nearest-ties-to-even
        // rule -- and whose hard-failure mode on non-numeric/empty/out-of-range text -- were
        // both measured against a real dtexec run of exactly this pairing
        // (synthetic-string-to-int-coercion-tables.sql), not guessed -- see CLAUDE.md's own
        // "Numeric-passthrough-coercion for OLE DB/ADO NET destinations" section.
        var destination = new PipelineComponentSpec
        {
            RefId = "DST_Test",
            Name = "OLE DB Destination",
            ComponentClassId = "Microsoft.OLEDBDestination",
            Inputs =
            [
                new PipelineInputSpec
                {
                    RefId = "DST_Test.Inputs[OLE DB Destination Input]",
                    Name = "OLE DB Destination Input",
                    Columns =
                    [
                        new PipelineInputColumnSpec
                        {
                            RefId = "DST_Test.Inputs[OLE DB Destination Input].Columns[CustomerID]",
                            CachedName = "CustomerID",
                            CachedDataType = "wstr",
                            LineageId = "SRC.Outputs[Output].Columns[CustomerID]",
                            ExternalMetadataColumnId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[CustomerID]",
                        },
                        new PipelineInputColumnSpec
                        {
                            RefId = "DST_Test.Inputs[OLE DB Destination Input].Columns[FullName]",
                            CachedName = "FullName",
                            CachedDataType = "wstr",
                            LineageId = "SRC.Outputs[Output].Columns[FullName]",
                            ExternalMetadataColumnId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[FullName]",
                        },
                    ],
                    ExternalMetadataColumns =
                    [
                        new PipelineExternalMetadataColumnSpec { RefId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[CustomerID]", Name = "CustomerID", DataType = "i4" },
                        new PipelineExternalMetadataColumnSpec { RefId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[FullName]", Name = "FullName", DataType = "wstr", Length = 200 },
                    ],
                },
            ],
        };
        var pipeline = new PipelineSpec { Components = [destination] };

        var request = new TransformRequest(
            "Generated.Mapping", "CustomerExportLogTransform",
            "Generated.Sql", "CustomerExportLogRow",
            "Generated.Model", "CustomerExportLog",
            "Generated.Ssis",
            pipeline, [], destination);

        var result = TransformEmitter.Emit(request);

        Assert.Empty(result.Result.Gaps);
        Assert.Equal(["ParseWstrToI4"], result.SsisFunctionsUsed);

        var file = Assert.Single(result.Result.Files);
        Assert.Contains("CustomerID = SsisFn.ParseWstrToI4(row.CustomerID),", file.Content);
        Assert.Contains("FullName = row.FullName,", file.Content);
        Assert.Contains("using Generated.Ssis;", file.Content);
        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void DetectParsedWstrToI4Columns_FindsOnlyTheStringToIntMismatch()
    {
        // Shared with PackageGenerator.ResolveNullableColumnNames -- must return the SAME
        // answer Emit's own inline mismatch detection reaches, since ParseWstrToI4 always
        // returns int? and the destination entity property must be nullable-inferred BEFORE
        // EntityEmitter runs (unlike NarrowR8ToI4's double/double? overload pair, string and
        // string? are the same runtime type, so there is no non-nullable overload to select).
        var destination = new PipelineComponentSpec
        {
            RefId = "DST_Test",
            Name = "OLE DB Destination",
            ComponentClassId = "Microsoft.OLEDBDestination",
            Inputs =
            [
                new PipelineInputSpec
                {
                    RefId = "DST_Test.Inputs[OLE DB Destination Input]",
                    Name = "OLE DB Destination Input",
                    Columns =
                    [
                        new PipelineInputColumnSpec
                        {
                            RefId = "DST_Test.Inputs[OLE DB Destination Input].Columns[CustomerID]",
                            CachedName = "CustomerID",
                            CachedDataType = "wstr",
                            LineageId = "SRC.Outputs[Output].Columns[CustomerID]",
                            ExternalMetadataColumnId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[CustomerID]",
                        },
                        new PipelineInputColumnSpec
                        {
                            RefId = "DST_Test.Inputs[OLE DB Destination Input].Columns[FullName]",
                            CachedName = "FullName",
                            CachedDataType = "wstr",
                            LineageId = "SRC.Outputs[Output].Columns[FullName]",
                            ExternalMetadataColumnId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[FullName]",
                        },
                    ],
                    ExternalMetadataColumns =
                    [
                        new PipelineExternalMetadataColumnSpec { RefId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[CustomerID]", Name = "CustomerID", DataType = "i4" },
                        new PipelineExternalMetadataColumnSpec { RefId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[FullName]", Name = "FullName", DataType = "wstr", Length = 200 },
                    ],
                },
            ],
        };

        var result = TransformEmitter.DetectParsedWstrToI4Columns(destination);

        Assert.Equal(new HashSet<string> { "CustomerID" }, result);
    }


    [Fact]
    public void Emit_ReproducesTheHandWrittenEmployeeTransform_FromTheRealLoadEmployeesPackage()
    {
        var package = TestFixtures.LoadPackage("LoadEmployees.dtsx");
        var pipeline = TestFixtures.FindPipeline(package, "DFT_LoadEmployees");
        var derivedColumn = TestFixtures.FindComponent(package, "Microsoft.DerivedColumn", "DER_MergeColumns");
        var destination = TestFixtures.FindComponent(package, "Microsoft.OLEDBDestination", "OLEDST_Employee");

        var request = new TransformRequest(
            MappingNamespace: "LoadEmployees.Mapping",
            TransformClassName: "EmployeeTransform",
            RowTypeNamespace: "LoadEmployees.Csv",
            RowTypeName: "EmployeeCsvRow",
            EntityNamespace: "LoadEmployees.Model",
            EntityName: "Employee",
            SsisFnNamespace: "LoadEmployees.Ssis",
            Pipeline: pipeline,
            DerivedColumns: [derivedColumn],
            DestinationComponent: destination);

        var result = TransformEmitter.Emit(request);

        Assert.Empty(result.Result.Gaps);
        Assert.Equal(["Str", "Substring", "Upper"], result.SsisFunctionsUsed.OrderBy(x => x));

        var file = Assert.Single(result.Result.Files);
        Assert.Equal("Mapping/EmployeeTransform.cs", file.RelativePath);
        Assert.Contains("public sealed class EmployeeTransform : IRowTransform<EmployeeCsvRow, Employee>", file.Content);

        // Computed columns -- exact same expressions EmployeeTransform.cs was hand-written with.
        Assert.Contains(
            "FullName = WidthGuard.Wstr($\"{row.FirstName} {row.LastName}\", 101, nameof(Employee.FullName), ctx.RowNumber),",
            file.Content);
        Assert.Contains(
            "Location = WidthGuard.Wstr($\"{row.City}, {row.State}\", 60, nameof(Employee.Location), ctx.RowNumber),",
            file.Content);
        Assert.Contains(
            "EmployeeKey = WidthGuard.Wstr($\"{SsisFn.Upper(SsisFn.Substring(row.Department, 1, 3))}-{SsisFn.Str(row.EmployeeID)}\", 20, nameof(Employee.EmployeeKey), ctx.RowNumber),",
            file.Content);
        Assert.Contains("LoadedAtUtc = ctx.LoadedAtUtc,", file.Content);

        // Plain passthrough columns -- never re-derived, straight from the row.
        Assert.Contains("EmployeeID = row.EmployeeID,", file.Content);
        Assert.Contains("Department = row.Department,", file.Content);
        Assert.Contains("HireDate = row.HireDate,", file.Content);
        Assert.Contains("Salary = row.Salary,", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_ReproducesTheHandWrittenDepartmentTransform_NoSubstringUsed()
    {
        var package = TestFixtures.LoadPackage("LoadReferenceData.dtsx");
        var pipeline = TestFixtures.FindPipeline(package, "DFT_LoadDepartment");
        var derivedColumn = TestFixtures.FindComponent(package, "Microsoft.DerivedColumn", "DER_DepartmentKey");
        var destination = TestFixtures.FindComponent(package, "Microsoft.OLEDBDestination", "OLEDST_Department");

        var request = new TransformRequest(
            "LoadReferenceData.Mapping", "DepartmentTransform",
            "LoadReferenceData.Csv", "DepartmentCsvRow",
            "LoadReferenceData.Model", "Department",
            "LoadReferenceData.Ssis",
            pipeline, [derivedColumn], destination);

        var result = TransformEmitter.Emit(request);

        Assert.Empty(result.Result.Gaps);
        Assert.Equal(["Str", "Upper"], result.SsisFunctionsUsed.OrderBy(x => x));

        var file = Assert.Single(result.Result.Files);
        Assert.Contains(
            "DepartmentKey = WidthGuard.Wstr($\"{SsisFn.Upper(row.DepartmentCode)}-{SsisFn.Str(row.DepartmentID)}\", 25, nameof(Department.DepartmentKey), ctx.RowNumber),",
            file.Content);
        Assert.Contains("CostCenter = row.CostCenter,", file.Content);
        Assert.Contains("HeadCount = row.HeadCount,", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_ReproducesTheHandWrittenDesignationTransform_KeyedOffJobLevelNotDesignationId()
    {
        var package = TestFixtures.LoadPackage("LoadReferenceData.dtsx");
        var pipeline = TestFixtures.FindPipeline(package, "DFT_LoadDesignation");
        var derivedColumn = TestFixtures.FindComponent(package, "Microsoft.DerivedColumn", "DER_DesignationKey");
        var destination = TestFixtures.FindComponent(package, "Microsoft.OLEDBDestination", "OLEDST_Designation");

        var request = new TransformRequest(
            "LoadReferenceData.Mapping", "DesignationTransform",
            "LoadReferenceData.Csv", "DesignationCsvRow",
            "LoadReferenceData.Model", "Designation",
            "LoadReferenceData.Ssis",
            pipeline, [derivedColumn], destination);

        var result = TransformEmitter.Emit(request);

        Assert.Empty(result.Result.Gaps);
        var file = Assert.Single(result.Result.Files);

        // DER_DesignationKey keys off JobLevel, not DesignationID -- easy to get backwards by
        // pattern-matching DepartmentTransform; this is the same fact
        // LoadReferenceData.Tests\DesignationTransformTests.cs pins on the hand-written side.
        Assert.Contains(
            "DesignationKey = WidthGuard.Wstr($\"{SsisFn.Upper(row.DesignationCode)}-{SsisFn.Str(row.JobLevel)}\", 25, nameof(Designation.DesignationKey), ctx.RowNumber),",
            file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }


    [Fact]
    public void Emit_EmitsASeam_ForAScriptComponentColumn_WhenSeamsAreEnabled()
    {
        // The same real shape as the test above -- but with --seams, the column stops being
        // silently omitted and becomes a Fill_* a human implements. The class turns partial and
        // the project stops compiling until they do, which is the whole point.
        var (scriptComponent, destination) = BuildScriptComponentPassthroughShape(withScriptComponentPayload: true);
        var pipeline = new PipelineSpec { Components = [scriptComponent, destination] };

        var request = new TransformRequest(
            "Generated.Mapping", "StagingCustomersTransform",
            "Generated.Csv", "StagingCustomersCsvRow",
            "Generated.Model", "StagingCustomers",
            "Generated.Ssis",
            pipeline, [], destination,
            EmitSeams: true);

        var result = TransformEmitter.Emit(request);
        var file = Assert.Single(result.Result.Files);

        Assert.Contains("public sealed partial class StagingCustomersTransform", file.Content);
        Assert.Contains("FullName = Fill_FullName(row, ctx),", file.Content);
        Assert.Contains("private partial string Fill_FullName(StagingCustomersCsvRow row, in RowContext ctx);", file.Content);
        // The genuinely-passthrough column is unaffected.
        Assert.Contains("CustomerID = row.CustomerID,", file.Content);
        CodeAssertions.AssertNoSyntaxErrors(file.Content);

        // A seam is outstanding work, not a resolution: the gap stays, stays BLOCKING, and keeps
        // its Tier-2 classification so the work packet is still produced.
        var gap = Assert.Single(result.Result.Gaps);
        Assert.Equal("StagingCustomers.FullName", gap.Location);
        Assert.True(gap.IsBlocking);
        Assert.Equal(GapKind.ScriptComponentColumn, gap.Kind);
        Assert.Contains("CS8795", gap.Reason);
    }

    [Fact]
    public void Emit_DoesNotEmitASeam_ForAnUnrecognizedProducerThatIsNotAScriptComponent()
    {
        // The load-bearing scope rule. A Script Component's own source IS in the .dtsx, so
        // porting it is Tier-2 work a human can do. Any OTHER unrecognized producer is missing
        // TOOL support -- offering a per-package seam there would invite hand-patching one
        // systemic emitter gap N times over, which is exactly what GapTier.MissingToolSupport
        // exists to keep visible as a single fix.
        var (producer, destination) = BuildScriptComponentPassthroughShape(withScriptComponentPayload: false);
        var pipeline = new PipelineSpec { Components = [producer, destination] };

        var request = new TransformRequest(
            "Generated.Mapping", "StagingCustomersTransform",
            "Generated.Csv", "StagingCustomersCsvRow",
            "Generated.Model", "StagingCustomers",
            "Generated.Ssis",
            pipeline, [], destination,
            EmitSeams: true);

        var result = TransformEmitter.Emit(request);
        var file = Assert.Single(result.Result.Files);

        Assert.DoesNotContain("partial", file.Content);
        Assert.DoesNotContain("Fill_", file.Content);
        Assert.DoesNotContain("FullName", file.Content);

        var gap = Assert.Single(result.Result.Gaps);
        Assert.Equal(GapKind.Unclassified, gap.Kind);
        Assert.DoesNotContain("CS8795", gap.Reason);
    }

    [Fact]
    public void Emit_LeavesTheClassNonPartial_WhenSeamsAreEnabledButNothingNeedsOne()
    {
        // --seams must be inert for a flow with no Script Component at all: no `partial`, no
        // behaviour change, byte-identical to running without the flag.
        var package = TestFixtures.LoadPackage("LoadEmployees.dtsx");
        var request = new TransformRequest(
            MappingNamespace: "LoadEmployees.Mapping",
            TransformClassName: "EmployeeTransform",
            RowTypeNamespace: "LoadEmployees.Csv",
            RowTypeName: "EmployeeCsvRow",
            EntityNamespace: "LoadEmployees.Model",
            EntityName: "Employee",
            SsisFnNamespace: "LoadEmployees.Ssis",
            Pipeline: TestFixtures.FindPipeline(package, "DFT_LoadEmployees"),
            DerivedColumns: [TestFixtures.FindComponent(package, "Microsoft.DerivedColumn", "DER_MergeColumns")],
            DestinationComponent: TestFixtures.FindComponent(package, "Microsoft.OLEDBDestination", "OLEDST_Employee"));

        var withoutSeams = TransformEmitter.Emit(request);
        var withSeams = TransformEmitter.Emit(request with { EmitSeams = true });

        Assert.Equal(
            Assert.Single(withoutSeams.Result.Files).Content,
            Assert.Single(withSeams.Result.Files).Content);
        Assert.DoesNotContain("partial", Assert.Single(withSeams.Result.Files).Content);
    }
    private static (PipelineComponentSpec ScriptComponent, PipelineComponentSpec Destination) BuildScriptComponentPassthroughShape(bool withScriptComponentPayload = false)
    {
        // Mirrors the real shape RBC_Demo_ETL's own Package.dtsx/DFT_LoadCustomers has: a
        // Script Component (Microsoft.ManagedComponentHost -- the same generic discriminator
        // ADO NET Source/Destination use, disambiguated elsewhere by UserComponentTypeName, not
        // relevant to this test) synthesizes a brand-new "FullName" column with its OWN fresh
        // lineageId (no upstream producer at all -- the script genuinely created this value).
        var scriptComponent = new PipelineComponentSpec
        {
            RefId = "SCR_Test",
            Name = "SCR_CleanseCustomerRow",
            ComponentClassId = "Microsoft.ManagedComponentHost",
            // A real Script Component carries this payload; an unrecognized producer that is NOT
            // one leaves it null, which is what keeps a Tier-3 gap out of the seam path.
            ScriptComponent = withScriptComponentPayload ? new ScriptComponentPayload { Language = "CSharp" } : null,
            Outputs =
            [
                new PipelineOutputSpec
                {
                    RefId = "SCR_Test.Outputs[Output 0]",
                    Name = "Output 0",
                    Columns =
                    [
                        new PipelineOutputColumnSpec
                        {
                            RefId = "SCR_Test.Outputs[Output 0].Columns[FullName]",
                            Name = "FullName",
                            LineageId = "SCR_Test.Outputs[Output 0].Columns[FullName]",
                        },
                    ],
                },
            ],
        };

        var destination = new PipelineComponentSpec
        {
            RefId = "DST_Test",
            Name = "OLE DB Destination",
            ComponentClassId = "Microsoft.OLEDBDestination",
            Inputs =
            [
                new PipelineInputSpec
                {
                    RefId = "DST_Test.Inputs[OLE DB Destination Input]",
                    Name = "OLE DB Destination Input",
                    Columns =
                    [
                        new PipelineInputColumnSpec
                        {
                            RefId = "DST_Test.Inputs[OLE DB Destination Input].Columns[CustomerID]",
                            CachedName = "CustomerID",
                            CachedDataType = "wstr",
                            LineageId = "SRC.Outputs[Output].Columns[CustomerID]",
                            ExternalMetadataColumnId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[CustomerID]",
                        },
                        new PipelineInputColumnSpec
                        {
                            RefId = "DST_Test.Inputs[OLE DB Destination Input].Columns[FullName]",
                            CachedName = "FullName",
                            CachedDataType = "wstr",
                            LineageId = "SCR_Test.Outputs[Output 0].Columns[FullName]",
                            ExternalMetadataColumnId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[FullName]",
                        },
                    ],
                    ExternalMetadataColumns =
                    [
                        new PipelineExternalMetadataColumnSpec { RefId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[CustomerID]", Name = "CustomerID", DataType = "wstr", Length = 20 },
                        new PipelineExternalMetadataColumnSpec { RefId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[FullName]", Name = "FullName", DataType = "wstr", Length = 200 },
                    ],
                },
            ],
        };

        return (scriptComponent, destination);
    }

    [Fact]
    public void Emit_ReportsAGap_ForAColumnProducedByAnUnrecognizedComponent_InsteadOfGuessingAPassthrough()
    {
        // The exact real, previously-SILENT shape RBC_Demo_ETL's own Package.dtsx/
        // DFT_LoadCustomers hits (found 2026-08-30): a Script Component synthesizes brand-new
        // columns (FullName/CleanEmail/IsValidRow/LoadDateTime) that don't exist on the raw CSV
        // row type at all. Before this fix, "not computed, not converted" silently meant "genuine
        // passthrough", producing a `row.FullName` reference with no such property -- a real
        // CS1061, invisible to ssisx generate's own gap report until the generated project was
        // actually built. See CLAUDE.md's own "Script Component silent passthrough" section.
        var (scriptComponent, destination) = BuildScriptComponentPassthroughShape();
        var pipeline = new PipelineSpec { Components = [scriptComponent, destination] };

        var request = new TransformRequest(
            "Generated.Mapping", "StagingCustomersTransform",
            "Generated.Csv", "StagingCustomersCsvRow",
            "Generated.Model", "StagingCustomers",
            "Generated.Ssis",
            pipeline, [], destination);

        var result = TransformEmitter.Emit(request);

        var gap = Assert.Single(result.Result.Gaps);
        Assert.Equal("StagingCustomers.FullName", gap.Location);
        Assert.Contains("produced by 'SCR_CleanseCustomerRow' (Microsoft.ManagedComponentHost)", gap.Reason);

        // The genuinely-passthrough column still generates -- same per-column-gap shape as
        // every other untranslatable case in this file (an unrelated column doesn't block the
        // whole flow).
        var file = Assert.Single(result.Result.Files);
        Assert.DoesNotContain("FullName", file.Content);
        Assert.Contains("CustomerID = row.CustomerID,", file.Content);
        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_TrustsEveryPassthroughColumn_WhenTrustAllPassthroughColumnsIsSet()
    {
        // GenerateMergeJoinFlow's own call site: MergeJoinEmitter already resolves every column
        // of its own combined row type itself (including through a Data Conversion, via its own
        // SourceColumnLineageId walk), so the unrecognized-producer gate above must be skipped
        // entirely here -- a real false positive found 2026-08-30 testing this fix against
        // RBC_Demo_ETL's own DFT_SortAndMergeJoin (a Merge Join with a Data Conversion on one
        // side), which was wrongly gapped without this opt-out even though MergeJoinEmitter's
        // own row type already has the column. Reuses the exact same pipeline shape as the
        // unrecognized-producer test above to prove the flag suppresses that same gap.
        var (scriptComponent, destination) = BuildScriptComponentPassthroughShape();
        var pipeline = new PipelineSpec { Components = [scriptComponent, destination] };

        var request = new TransformRequest(
            "Generated.Mapping", "StagingCustomersTransform",
            "Generated.Csv", "StagingCustomersCsvRow",
            "Generated.Model", "StagingCustomers",
            "Generated.Ssis",
            pipeline, [], destination,
            TrustAllPassthroughColumns: true);

        var result = TransformEmitter.Emit(request);

        Assert.Empty(result.Result.Gaps);
        var file = Assert.Single(result.Result.Files);
        Assert.Contains("FullName = row.FullName,", file.Content);
        Assert.Contains("CustomerID = row.CustomerID,", file.Content);
        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }
}
