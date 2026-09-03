using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Ssis.Runtime.Expressions;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Phase 4 layer 2 of the `ssisx generate` plan -- the "anti-circularity anchor" for
/// <see cref="ExpressionTranslator"/>. Every other test touching a translated expression
/// (<c>ExpressionTranslatorTests</c>, <c>TransformEmitterTests</c>) checks the emitted C#
/// TEXT -- a byte-identity check against what a human already wrote, or against a hand-typed
/// expected string. None of them actually RUN the generated code. That's a real gap: a
/// translation bug that produces syntactically fine, textually-plausible-looking C# with
/// wrong runtime behaviour (operator precedence, argument order, an off-by-one) would sail
/// through every one of those.
///
/// This test closes that gap by taking the REAL generated files for a real Derived Column
/// (Csv row, Model entity, Mapping transform, Ssis/SsisFn.cs -- straight from
/// <see cref="PackageGenerator.Generate"/>, not hand-typed), compiling them with Roslyn into
/// a real in-memory assembly, and ACTUALLY EXECUTING <c>Transform.Map(row, ctx)</c> against
/// concrete sample values. The result is compared against
/// <c>Ssis.Runtime.Expressions.SsisExpression.Evaluate</c> run against the SAME sample values
/// -- an entirely independent code path, itself pinned to the real SSIS oracle corpus (see
/// docs/gate2-schema.md). Two independent implementations of "what does this SSIS expression
/// mean" agreeing on real output is a much stronger claim than either one looking right on
/// its own, and unlike <c>TransformEmitterTests</c>' hand-written-source comparison, this
/// generalizes to a future client package with no hand-written counterpart to diff against.
///
/// GETUTCDATE()-derived columns (LoadedAtUtc on all three tables) are excluded, same as
/// `ssisx testgen`'s own exclusion -- there is no fixed expected value for a non-deterministic
/// function to differential-test against.
/// </summary>
public class DifferentialExpressionTests
{
    private static readonly DateTime FixedLoadedAtUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EmployeeTransform_FullName_MatchesTheOracleEvaluator()
    {
        AssertComputedColumnMatchesOracle(
            "LoadEmployees.dtsx", "Employee",
            rowValues: new Dictionary<string, object> { ["FirstName"] = "Jane", ["LastName"] = "Doe" },
            oracleEnv: new Dictionary<string, SsisValue> { ["FirstName"] = SsisValue.OfString("Jane"), ["LastName"] = SsisValue.OfString("Doe") },
            derivedColumnName: "FullName",
            entityPropertyName: "FullName");
    }

    [Fact]
    public void EmployeeTransform_Location_MatchesTheOracleEvaluator()
    {
        AssertComputedColumnMatchesOracle(
            "LoadEmployees.dtsx", "Employee",
            rowValues: new Dictionary<string, object> { ["City"] = "Austin", ["State"] = "TX" },
            oracleEnv: new Dictionary<string, SsisValue> { ["City"] = SsisValue.OfString("Austin"), ["State"] = SsisValue.OfString("TX") },
            derivedColumnName: "Location",
            entityPropertyName: "Location");
    }

    [Fact]
    public void EmployeeTransform_EmployeeKey_MatchesTheOracleEvaluator()
    {
        // Exercises SUBSTRING + UPPER + the integer (DT_WSTR) cast chained together -- the
        // most complex of the three real EmployeeTransform expressions.
        AssertComputedColumnMatchesOracle(
            "LoadEmployees.dtsx", "Employee",
            rowValues: new Dictionary<string, object> { ["Department"] = "Engineering", ["EmployeeID"] = 42 },
            oracleEnv: new Dictionary<string, SsisValue> { ["Department"] = SsisValue.OfString("Engineering"), ["EmployeeID"] = SsisValue.OfInt(42, SsisType.I4) },
            derivedColumnName: "EmployeeKey",
            entityPropertyName: "EmployeeKey");
    }

    [Fact]
    public void DepartmentTransform_DepartmentKey_MatchesTheOracleEvaluator()
    {
        AssertComputedColumnMatchesOracle(
            "LoadReferenceData.dtsx", "Department",
            rowValues: new Dictionary<string, object> { ["DepartmentCode"] = "fin", ["DepartmentID"] = 7 },
            oracleEnv: new Dictionary<string, SsisValue> { ["DepartmentCode"] = SsisValue.OfString("fin"), ["DepartmentID"] = SsisValue.OfInt(7, SsisType.I4) },
            derivedColumnName: "DepartmentKey",
            entityPropertyName: "DepartmentKey");
    }

    [Fact]
    public void DesignationTransform_DesignationKey_MatchesTheOracleEvaluator()
    {
        // Keyed off JobLevel, not DesignationID -- same fact TransformEmitterTests pins;
        // worth re-proving at runtime, not just in the generated text.
        AssertComputedColumnMatchesOracle(
            "LoadReferenceData.dtsx", "Designation",
            rowValues: new Dictionary<string, object> { ["DesignationCode"] = "mgr", ["JobLevel"] = 3 },
            oracleEnv: new Dictionary<string, SsisValue> { ["DesignationCode"] = SsisValue.OfString("mgr"), ["JobLevel"] = SsisValue.OfInt(3, SsisType.I4) },
            derivedColumnName: "DesignationKey",
            entityPropertyName: "DesignationKey");
    }

    /// <summary>
    /// Generates the real package (same call as GenerateGoldenFileTests), compiles its
    /// Csv/Model/Mapping/Ssis output together with a minimal test-local stand-in for the
    /// three Etl.Core types they reference, runs Transform.Map against
    /// <paramref name="rowValues"/>, and compares the named entity property against
    /// SsisExpression.Evaluate run against <paramref name="oracleEnv"/> for the SAME derived
    /// column's real .dtsx expression text (read from the package, not hardcoded).
    /// </summary>
    private static void AssertComputedColumnMatchesOracle(
        string dtsxFileName, string entityName,
        Dictionary<string, object> rowValues, Dictionary<string, SsisValue> oracleEnv,
        string derivedColumnName, string entityPropertyName)
    {
        var package = TestFixtures.LoadPackage(dtsxFileName);
        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var rootNamespace = package.ObjectName;
        var rowTypeName = entityName + "CsvRow";
        var expressionText = FindDerivedColumnExpression(package, entityName, derivedColumnName);

        // Deliberately NOT "every .cs file this package generated" -- Model/*DbContext.cs
        // needs EF Core, Program.cs needs Etl.Core.Hosting/Microsoft.Extensions.*, and
        // Csv/*Map.cs needs CsvHelper, none of which this test project references (and
        // shouldn't need to, just to prove one Derived Column expression's runtime value).
        // Only the four files Transform.Map's own call graph actually touches.
        var neededPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            $"Csv/{rowTypeName}.cs",
            $"Model/{entityName}.cs",
            $"Mapping/{entityName}Transform.cs",
            "Ssis/SsisFn.cs",
        };
        var sources = new List<string> { ImplicitGlobalUsings, EtlCoreStandIns };
        sources.AddRange(result.Files.Where(f => neededPaths.Contains(f.RelativePath)).Select(f => f.Content));

        var assembly = Compile(sources);

        var rowType = assembly.GetType($"{rootNamespace}.Csv.{rowTypeName}", throwOnError: true)!;
        var row = Activator.CreateInstance(rowType)!;
        foreach (var (name, value) in rowValues)
            rowType.GetProperty(name)!.SetValue(row, value);

        var rowContextType = assembly.GetType("Etl.Core.Abstractions.RowContext", throwOnError: true)!;
        var ctx = Activator.CreateInstance(rowContextType, 1L, FixedLoadedAtUtc, "differential-test")!;

        var transformType = assembly.GetType($"{rootNamespace}.Mapping.{entityName}Transform", throwOnError: true)!;
        var transform = Activator.CreateInstance(transformType)!;
        var mapMethod = transformType.GetMethod("Map")!;
        var entity = mapMethod.Invoke(transform, [row, ctx])!;

        var entityType = assembly.GetType($"{rootNamespace}.Model.{entityName}", throwOnError: true)!;
        var actual = (string)entityType.GetProperty(entityPropertyName)!.GetValue(entity)!;

        var expected = SsisExpression.Evaluate(expressionText, oracleEnv);
        Assert.False(expected.IsNull, $"Oracle evaluation of '{expressionText}' unexpectedly returned NULL for this sample.");
        Assert.Equal(expected.ToDisplayString(), actual);
    }

    private static string FindDerivedColumnExpression(Ssis.Extract.Model.Package.PackageSpec package, string entityName, string derivedColumnName)
    {
        var dataFlowTaskName = entityName switch
        {
            "Employee" => "DFT_LoadEmployees",
            "Department" => "DFT_LoadDepartment",
            "Designation" => "DFT_LoadDesignation",
            _ => throw new ArgumentOutOfRangeException(nameof(entityName), entityName, "no known Data Flow Task for this entity"),
        };
        var pipeline = TestFixtures.FindPipeline(package, dataFlowTaskName);
        var column = pipeline.Components
            .SelectMany(c => c.Outputs)
            .SelectMany(o => o.Columns)
            .First(c => c.Name == derivedColumnName && c.Expression is not null);
        return column.FriendlyExpression ?? column.Expression!;
    }

    private static Assembly Compile(List<string> sources)
    {
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s)).ToList();
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            $"DifferentialHarness_{Guid.NewGuid():N}",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms);
        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
            throw new InvalidOperationException(
                "Generated code failed to compile in the differential harness:\n" +
                string.Join("\n", errors) + "\n\n---\n" + string.Join("\n---\n", sources));
        }

        ms.Seek(0, SeekOrigin.Begin);
        return Assembly.Load(ms.ToArray());
    }

    /// <summary>The real generated files compile with `ImplicitUsings=enable`, which the SDK
    /// satisfies via a generated GlobalUsings.g.cs this harness doesn't produce -- without it,
    /// every generated file's bare `DateTime`/`Math`/`ArgumentOutOfRangeException` reference
    /// fails with CS0246. Reproduces just the usings the SDK's non-web implicit set actually
    /// includes.</summary>
    private const string ImplicitGlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.Linq;
        global using System.Threading.Tasks;
        """;

    /// <summary>
    /// A minimal, test-local stand-in for the three Etl.Core types generated Csv/Model/Mapping
    /// code references (IRowTransform, RowContext, WidthGuard, SsisWidthAttribute) --
    /// behaviourally equivalent to the real ones for normal (non-overflow) inputs, which is
    /// all this harness exercises (overflow/truncation behaviour is Etl.Core.Tests'/
    /// LoadEmployees.Tests' job in D:\PoC\SSIS_Rewrite, not this one). This project cannot
    /// reference the real Etl.Core: it's a hand-written net10.0 library in a DIFFERENT repo
    /// (D:\PoC\SSIS_Rewrite), and this tool must stay usable on a client site that has no such
    /// repo -- the exact same reasoning GenerateCommand's own WriteFixedFiles comment gives
    /// for not copying Etl.Core into generated output.
    /// </summary>
    private const string EtlCoreStandIns = """
        using System;

        namespace Etl.Core.Abstractions
        {
            public interface IRowTransform<TRow, TEntity>
            {
                TEntity Map(TRow row, in RowContext ctx);
            }

            public readonly record struct RowContext(long RowNumber, DateTime LoadedAtUtc, string SourceName);
        }

        namespace Etl.Core.Ssis
        {
            [AttributeUsage(AttributeTargets.Property)]
            public sealed class SsisWidthAttribute : Attribute
            {
                public SsisWidthAttribute(int maxWidth) => MaxWidth = maxWidth;
                public int MaxWidth { get; }
            }

            public static class WidthGuard
            {
                public static string Wstr(string value, int declaredWidth, string columnName, long rowNumber)
                {
                    if (value.Length > declaredWidth)
                        throw new InvalidOperationException($"{columnName} exceeds {declaredWidth} chars at row {rowNumber}.");
                    return value;
                }
            }
        }
        """;
}
