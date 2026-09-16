namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits Mapping/{TaskName}Statement.cs -- a small, named, pure static class computing one
/// Execute SQL Task's own statement text, so it is independently unit-testable and human-
/// reviewable instead of being an anonymous string literal embedded directly in Program.cs.
/// Used for a post-flow SQL step's own text (Etl.Core's ExecuteSqlStep/
/// SecondaryConnectionSqlStep call BuildStatement() once, at construction time, and run the
/// result) and identically for a pre-load statement's own text (called once inline in the
/// generated Program.cs's own try block, both for the log line and for ExecuteSqlAsync) --
/// neither Etl.Core type changes at all in either case; this is purely a generated-code-shape
/// convention.
///
/// Deliberately package-scoped (one file per task, like SsisFn.cs is one file per package) rather
/// than a shared Etl.Core helper -- the SQL text is this ONE task's own literal statement, with
/// no cross-package reuse to speak of. A statement carrying real per-run parameters (once
/// SqlParameterBindingSpec is evidenced -- see TaskPayloads.cs's own doc comment) is the natural
/// future extension of BuildStatement() taking arguments, mirroring the shape ForEachLoopStep's
/// own Func&lt;string,string&gt; buildSql already established for the per-iteration case.
/// </summary>
public static class SqlStatementBuilderEmitter
{
    /// <param name="identifierBase">The already-disambiguated identifier for this task (see
    /// PackageGenerator's own ReserveStatementIdentifier) -- NOT re-derived from
    /// <paramref name="taskName"/> here, because two Execute SQL Tasks anywhere in one package can
    /// share the identical display name (a template task copy-pasted into many branches is a real,
    /// evidenced SSIS authoring pattern), and deriving the class name from taskName alone would
    /// collide both of their Mapping/{Name}Statement.cs files onto the same path -- silently
    /// discarding one's own distinct SQL text via a plain File.WriteAllText overwrite, with no
    /// error and no gap. The caller reserves a package-wide-unique identifier once per task and
    /// passes it here.</param>
    public static EmitResult Emit(string mappingNamespace, string taskName, string identifierBase, string sql)
    {
        var className = identifierBase + "Statement";
        var lines = new List<string>
        {
            $"namespace {mappingNamespace};",
            "",
            $"/// <summary>Ported SSIS Execute SQL Task '{taskName}'. Pure: computes the statement",
            "/// text, does no I/O -- called once, either by Etl.Core's ExecuteSqlStep/",
            "/// SecondaryConnectionSqlStep at construction time, or directly as a pre-load",
            "/// statement, and the result is run.</summary>",
            $"public static class {className}",
            "{",
            $"    public static string BuildStatement() => {ProgramEmitter.CSharpStringLiteral(sql)};",
            "}",
        };

        return new EmitResult([new GeneratedFile($"Mapping/{className}.cs", Rendering.JoinLines(lines))], []);
    }

    /// <summary>The per-iteration counterpart of <see cref="Emit"/> -- for a ForEach Loop's own
    /// body Execute SQL Task, whose statement text is rebuilt fresh each iteration from the
    /// current file value. Unlike <see cref="Emit"/>, <paramref name="csharpExpression"/> is
    /// already a C# EXPRESSION (string concatenation involving <paramref name="parameterName"/>,
    /// produced by <see cref="ForEachLoopEmitter.TranslateSqlTemplate"/>), not a raw SQL string to
    /// escape -- spliced in verbatim rather than passed through <c>CSharpStringLiteral</c>. Named
    /// and parameterized rather than an anonymous <c>currentFile => "..." + currentFile</c> lambda
    /// embedded directly in Program.cs, the exact extension this file's own doc comment above
    /// already anticipated.</summary>
    /// <param name="identifierBase">See <see cref="Emit"/>'s own doc comment -- same shared,
    /// package-wide disambiguation, so this ForEach Loop body statement can never collide with an
    /// ordinary Execute SQL Task's own Mapping/{Name}Statement.cs either.</param>
    public static EmitResult EmitParameterized(string mappingNamespace, string taskName, string identifierBase, string parameterName, string csharpExpression)
    {
        var className = identifierBase + "Statement";
        var lines = new List<string>
        {
            $"namespace {mappingNamespace};",
            "",
            $"/// <summary>Ported SSIS ForEach Loop Execute SQL Task '{taskName}'. Pure: computes the",
            "/// per-iteration statement text from the current file value, does no I/O -- Etl.Core's",
            "/// ForEachLoopStep calls this once per iteration and runs the result.</summary>",
            $"public static class {className}",
            "{",
            $"    public static string BuildStatement(string {parameterName}) => {csharpExpression};",
            "}",
        };

        return new EmitResult([new GeneratedFile($"Mapping/{className}.cs", Rendering.JoinLines(lines))], []);
    }
}
