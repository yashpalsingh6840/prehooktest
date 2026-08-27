using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Parses generated C# with Roslyn and asserts it has no syntax errors -- string-contains
/// assertions alone already let one real bug through once (EntityEmitter/CsvRowEmitter were
/// appending a stray trailing ";" after a non-string auto-property, e.g.
/// "public int EmployeeID { get; set; };", which is CS1519 and would never have compiled).
/// This only checks SYNTAX, not full semantic correctness -- the generated files reference
/// types (CsvHelper.ClassMap, EF Core's DbContext, Etl.Core.Ssis.SsisWidthAttribute) this test
/// project doesn't reference, so a full Compilation isn't set up here. That's Phase 4's job
/// (compile + Gate 3 against the real Etl.Core skeleton); this is the cheap, always-on floor
/// underneath it.
/// </summary>
internal static class CodeAssertions
{
    public static void AssertNoSyntaxErrors(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

        Assert.True(errors.Count == 0,
            $"Generated code has syntax errors:\n{string.Join("\n", errors)}\n\n---\n{source}");
    }
}
