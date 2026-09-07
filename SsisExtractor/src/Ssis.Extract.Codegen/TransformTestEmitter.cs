using System.Globalization;

using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;
using Ssis.Runtime.Expressions;

namespace Ssis.Extract.Codegen;

/// <summary>Everything <see cref="TransformTestEmitter"/> needs to scaffold one starter test
/// class for a Data Flow Task's transform. Deliberately a SUBSET of <see cref="TransformRequest"/>'s
/// own fields, not a shared type -- see this emitter's own doc comment for why its scope is
/// narrower than what <see cref="TransformEmitter"/> itself supports.</summary>
public sealed record TransformTestRequest(
    string TestNamespace,
    string TransformClassName,
    string MappingNamespace,
    string RowTypeNamespace,
    string RowTypeName,
    string EntityNamespace,
    string EntityName,
    PipelineSpec Pipeline,
    IReadOnlyList<PipelineComponentSpec>? DerivedColumns,
    PipelineComponentSpec DestinationComponent,
    IReadOnlyList<PipelineComponentSpec>? DataConversions = null);

/// <summary>
/// Scaffolds ONE starter xUnit test class per Data Flow Task transform, asserting the exact
/// input -&gt; function -&gt; output shape the original ask for this whole plan was about: given a
/// concrete <c>{RowType}</c> and a fixed <c>RowContext</c>, does <c>{Transform}.Map(row, ctx)</c>
/// produce the expected <c>{Entity}</c>. This is deliberately a STARTER, not a full auto-oracle
/// test generator -- see the "Pilot scope" section below for exactly what is and is not covered,
/// and CLAUDE.md-equivalent plan notes for why the remaining scope was deferred rather than
/// guessed at.
///
/// <para><b>Expected values are computed, not guessed, for every column this pilot covers</b> --
/// the same "gaps not guesses" discipline as everywhere else in this tool. A plain passthrough
/// column's expected value is trivially the same literal used for the row's own property (that
/// is what <see cref="TransformEmitter"/> itself emits: <c>Prop = row.Prop,</c> verbatim). A
/// <c>GETUTCDATE()</c>/<c>GETDATE()</c>-derived column's expected value is the SAME literal
/// <c>RowContext.LoadedAtUtc</c> the test itself constructs (again matching
/// <see cref="TransformEmitter"/>'s own translation, <c>Prop = ctx.LoadedAtUtc,</c> verbatim). A
/// genuinely computed column's expected value is evaluated through
/// <see cref="Ssis.Runtime.Expressions.SsisExpression"/> -- the SAME oracle library
/// <c>ssisx testgen</c>/gate 2 uses, itself independently verified against the real SSIS
/// evaluator (see docs/gate2-schema.md) -- fed the SAME literal values this test assigns to the
/// row's own referenced properties, so the row construction and the expected-value computation
/// can never disagree about what the input actually was.</para>
///
/// <para><b>Pilot scope, stated plainly rather than silently narrowed:</b> a flow needs a Derived
/// Column and/or a Data Conversion to get a starter test at all. This now includes a Conditional
/// Split/Multicast branch's own per-branch Derived Column (see <see cref="TransformTestRequest.DerivedColumns"/>,
/// plural -- a branch can carry both a shared upstream one and its own), called once per branch
/// from <c>PackageGenerator.GenerateConditionalSplitFlow</c>/<c>GenerateMulticastFlow</c> the same
/// way the single-destination path already does. Lookup/Merge Join/numeric-coercion flows still
/// get none -- each is a materially different shape needing its own row-construction strategy,
/// but each of those now surfaces a non-blocking <c>TestOracle</c> gap instead of staying silent
/// when it actually has a Derived Column/Data Conversion to be missing a test for
/// (see <c>PackageGenerator.GenerateMergeJoinFlow</c>/<c>GenerateLookupFlow</c>). Aggregate is the
/// one exception left with no gap at all, and deliberately so: its own row type has only group/
/// aggregated columns, with no clean analog of "the flow's own Derived Column" to gap on the way
/// there is for the other shapes (a pre-aggregation Derived Column, if any, belongs to the
/// SOURCE side, a different question) -- naming that correctly needs its own design work, not a
/// mechanical one-liner, so it is named here as a real follow-up rather than guessed at.
/// Within a Derived-Column-bearing flow, a computed column is only asserted when its own OUTPUT
/// type is string (<c>DT_WSTR</c>/<c>DT_STR</c>) -- every real evidenced Derived Column output in
/// the tracked portfolio's simplest flows is string-typed (a WidthGuard-wrapped concatenation),
/// and asserting a non-string oracle result against a typed C# property
/// (<c>int</c>/<c>decimal</c>/<c>DateOnly</c>/...) needs its own value-to-literal bridge this
/// pilot does not yet build for the SSIS-EXPRESSION-LANGUAGE case. A Data-Conversion-produced
/// column IS covered for every target type <see cref="TransformEmitter"/> itself translates
/// (<c>DT_I4</c>/<c>DT_DBDATE</c>/<c>DT_R8</c>/<c>DT_BOOL</c>/<c>DT_I2</c>/<c>DT_I8</c>/
/// <c>DT_DBTIMESTAMP</c>/<c>DT_WSTR</c>) -- unlike the Derived-Column case, a Data Conversion's
/// non-string targets need no value-to-literal bridge at all, because its whole translation is a
/// single deterministic <c>string.TryParse</c>-family call (see <c>SsisFnEmitter</c>'s own
/// emitted bodies), not the SSIS expression language -- so the expected value is computed by
/// literally invoking the SAME .NET parse call this pilot's own <c>RepresentativeDataConversion</c>
/// uses, on the SAME representative raw string the row is built from, rather than hand-encoding a
/// parsed result that could quietly drift from what the generated code actually does. A Derived
/// Column expression that CROSS-REFERENCES a Data-Conversion-produced column (e.g. <c>ISNULL(
/// SignupDate_dt) ? -1 : DATEDIFF(...)</c>) is deliberately NOT attempted -- that referenced name
/// has no row property of its own (it's a value computed inline via <c>SsisFn.ToNullable*</c> at
/// <c>Map()</c> time), and naively resolving it via <see cref="TransformEmitter.BuildColumnTypeLookup"/>
/// the way an ordinary referenced column resolves would silently emit a row initializer assigning
/// a property that doesn't exist. Detected explicitly (a referenced column whose own producer is
/// a "DataConversion" lineage edge) and reported as a gap instead. A column outside all of this
/// scope is skipped with a non-blocking <see cref="GenerationGap"/> naming exactly what to add by
/// hand -- never silently dropped, and never guessed at.</para>
/// </summary>
public static class TransformTestEmitter
{
    public static EmitResult Emit(TransformTestRequest request)
    {
        // Neither a Derived Column nor a Data Conversion -- nothing for this pilot to compute an
        // expected value against yet (a pure passthrough flow's own "test" would just restate the
        // mapping with no independent check of anything). Not a gap: there is genuinely nothing
        // missing here, this shape is simply out of THIS emitter's pilot scope.
        var derivedColumns = request.DerivedColumns ?? [];
        var dataConversions = request.DataConversions ?? [];
        if (derivedColumns.Count == 0 && dataConversions.Count == 0) return new EmitResult([], []);

        var gaps = new List<GenerationGap>();
        var lineage = LineageBuilder.Build(request.Pipeline);
        var columnTypes = TransformEmitter.BuildColumnTypeLookup(request.Pipeline);

        // A Conditional Split/Multicast branch can carry BOTH a shared upstream Derived Column
        // and its own per-branch one (e.g. SyntheticConditionalSplitRemerge.dtsx) -- first
        // registration wins on a name collision, matching TransformEmitter's own
        // TransformRequest.DerivedColumns merge precedent (no evidenced real package needs a
        // second one to win).
        var computedByName = new Dictionary<string, PipelineOutputColumnSpec>();
        foreach (var derivedColumn in derivedColumns)
            foreach (var output in derivedColumn.Outputs.Where(o => o.IsErrorOut != true))
                foreach (var col in output.Columns.Where(c => c.Expression is not null))
                    computedByName.TryAdd(col.Name, col);

        // A Data Conversion output column has no Expression at all (see DataConvertPayload's own
        // doc comment) -- keyed separately here, same split TransformEmitter itself makes.
        var convertedByName = new Dictionary<string, DataConversionColumnSpec>();
        foreach (var dataConversion in dataConversions)
            foreach (var col in dataConversion.DataConvert?.Columns ?? [])
                convertedByName.TryAdd(col.OutputColumnName, col);

        var resolved = PipelineResolver.ResolveDestinationInput(request.DestinationComponent);

        // The row's own representative literal values, keyed by ROW PROPERTY name -- shared
        // between every column that references or IS a given property, so e.g. EmployeeID (both
        // a plain passthrough destination column AND referenced inside EmployeeKey's own
        // expression) gets exactly one value used consistently everywhere it appears. Order
        // tracked explicitly (not relying on Dictionary's own enumeration order) so repeated
        // generation is byte-identical, the same discipline every other emitter in this tool
        // follows.
        var rowLiterals = new Dictionary<string, string>(StringComparer.Ordinal);
        var rowLiteralOrder = new List<string>();
        void AssignRowLiteral(string propertyName, string literal)
        {
            if (rowLiterals.TryAdd(propertyName, literal)) rowLiteralOrder.Add(propertyName);
        }

        const string fixedLoadedAtUtc = "new DateTime(2020, 6, 15, 12, 0, 0, DateTimeKind.Utc)";
        var assertions = new List<string>();

        foreach (var column in resolved.Columns)
        {
            if (computedByName.TryGetValue(column.PipelineColumnName, out var derivedCol))
            {
                var friendly = derivedCol.FriendlyExpression ?? derivedCol.Expression;
                if (friendly is null) continue; // unreachable: computedByName only holds columns with Expression is not null

                ExprNode ast;
                try { ast = SsisExpression.Parse(friendly); }
                catch (SsisExpressionError ex)
                {
                    gaps.Add(new GenerationGap($"{request.EntityName}.{column.ExternalColumnName}",
                        $"starter test not generated for this column -- its expression could not be parsed: {ex.Message}. Add an assertion by hand.", IsBlocking: false));
                    continue;
                }

                if (ast is FunctionCall { Name: "GETUTCDATE" or "GETDATE", Args.Count: 0 })
                {
                    assertions.Add($"        // {derivedCol.Name} <- {friendly}");
                    assertions.Add($"        Assert.Equal({fixedLoadedAtUtc}, result.{column.ExternalColumnName});");
                    continue;
                }

                var referencedEdges = lineage.Edges.Where(e => e.Kind == "ExpressionDerived" && e.ToColumnRefId == derivedCol.RefId).ToList();
                var referencedNames = referencedEdges.Select(e => e.FromColumnName).Distinct().ToList();

                var env = new Dictionary<string, SsisValue>();
                var unresolved = false;
                foreach (var edge in referencedEdges)
                {
                    if (env.ContainsKey(edge.FromColumnName)) continue;

                    // A reference to a Data-Conversion-PRODUCED column has no row property of its
                    // own (it's a value computed inline via SsisFn.ToNullable* at Map() time, not
                    // a raw source column) -- resolving it the way an ordinary referenced column
                    // resolves below would silently emit a row initializer assigning a property
                    // that doesn't exist. See this emitter's own class doc comment.
                    if (lineage.Edges.Any(de => de.Kind == "DataConversion" && de.ToColumnRefId == edge.FromColumnRefId))
                    {
                        unresolved = true;
                        break;
                    }

                    if (!columnTypes.TryGetValue(edge.FromColumnRefId, out var producer)
                        || TransformEmitter.MapPipelineTypeToSsisType(producer.DataType) is not { } ssisType
                        || SsisPipelineTypeMap.Resolve(producer.DataType) is not { } clrType)
                    {
                        unresolved = true;
                        break;
                    }

                    env[edge.FromColumnName] = RepresentativeOracleValue(ssisType);
                    AssignRowLiteral(edge.FromColumnName, RepresentativeLiteral(clrType.ClrTypeName));
                }

                if (unresolved || referencedNames.Any(n => !env.ContainsKey(n)))
                {
                    gaps.Add(new GenerationGap($"{request.EntityName}.{column.ExternalColumnName}",
                        "starter test not generated for this column -- a referenced column's pipeline type could not be resolved (or is itself produced by a Data Conversion, which this pilot does not follow as a cross-reference). Add an assertion by hand.", IsBlocking: false));
                    continue;
                }

                var outputSsisType = TransformEmitter.MapPipelineTypeToSsisType(derivedCol.DataType);
                if (outputSsisType is not (SsisType.WStr or SsisType.Str))
                {
                    gaps.Add(new GenerationGap($"{request.EntityName}.{column.ExternalColumnName}",
                        $"starter test not generated for this column -- this pilot only covers string-typed Derived Column outputs (this one is '{derivedCol.DataType ?? "(unknown)"}'). Add an assertion by hand.", IsBlocking: false));
                    continue;
                }

                SsisValue evaluated;
                try { evaluated = SsisExpression.Evaluate(friendly, env); }
                catch (SsisExpressionError ex)
                {
                    gaps.Add(new GenerationGap($"{request.EntityName}.{column.ExternalColumnName}",
                        $"starter test not generated for this column -- could not evaluate a representative case: {ex.Message}. Add an assertion by hand.", IsBlocking: false));
                    continue;
                }
                if (evaluated.IsNull)
                {
                    gaps.Add(new GenerationGap($"{request.EntityName}.{column.ExternalColumnName}",
                        "starter test not generated for this column -- the representative case evaluated to NULL, which this pilot does not assert. Add an assertion by hand.", IsBlocking: false));
                    continue;
                }

                assertions.Add($"        // {derivedCol.Name} <- {friendly}");
                assertions.Add($"        Assert.Equal({Literal(evaluated.ToDisplayString())}, result.{column.ExternalColumnName});");
            }
            else if (convertedByName.TryGetValue(column.PipelineColumnName, out var conversion))
            {
                var edge = lineage.Edges.FirstOrDefault(e => e.Kind == "DataConversion" && e.ToColumnName == column.PipelineColumnName);
                if (edge is null)
                {
                    gaps.Add(new GenerationGap($"{request.EntityName}.{column.ExternalColumnName}",
                        "starter test not generated for this column -- its Data Conversion source column could not be resolved. Add an assertion by hand.", IsBlocking: false));
                    continue;
                }

                var representative = RepresentativeDataConversion(conversion.TargetDataType, conversion.Length);
                if (representative is not { } rep)
                {
                    gaps.Add(new GenerationGap($"{request.EntityName}.{column.ExternalColumnName}",
                        $"starter test not generated for this column -- this pilot has no representative value for Data Conversion target type '{conversion.TargetDataType ?? "(unknown)"}'. Add an assertion by hand.", IsBlocking: false));
                    continue;
                }

                if (rowLiterals.TryGetValue(edge.FromColumnName, out var existingLiteral) && existingLiteral != rep.RawLiteral)
                {
                    gaps.Add(new GenerationGap($"{request.EntityName}.{column.ExternalColumnName}",
                        $"starter test not generated for this column -- its raw source column '{edge.FromColumnName}' already has a different representative value assigned elsewhere in this row. Add an assertion by hand.", IsBlocking: false));
                    continue;
                }
                AssignRowLiteral(edge.FromColumnName, rep.RawLiteral);

                assertions.Add($"        // {column.ExternalColumnName} <- Data Conversion ({edge.FromColumnName} -> {conversion.TargetDataType})");
                assertions.Add($"        Assert.Equal({rep.ExpectedLiteral}, result.{column.ExternalColumnName});");
            }
            else
            {
                if (column.Type is not { } clrType)
                {
                    gaps.Add(new GenerationGap($"{request.EntityName}.{column.ExternalColumnName}",
                        $"starter test not generated for this column -- its external data type ('{column.ExternalDataType ?? "(unknown)"}') has no CLR mapping. Add an assertion by hand.", IsBlocking: false));
                    continue;
                }

                var literal = RepresentativeLiteral(clrType.ClrTypeName);
                AssignRowLiteral(column.PipelineColumnName, literal);
                assertions.Add($"        Assert.Equal({literal}, result.{column.ExternalColumnName});");
            }
        }

        if (assertions.Count == 0)
        {
            gaps.Add(new GenerationGap(request.EntityName,
                "no starter test generated -- every column fell outside this pilot's scope. See the other gaps reported for this entity.", IsBlocking: false));
            return new EmitResult([], gaps);
        }

        var rowInitLines = rowLiteralOrder.Select(name => $"            {name} = {rowLiterals[name]},").ToList();

        var lines = new List<string>
        {
            "// <auto-generated>",
            $"// Generated by `ssisx generate` -- a STARTER unit test for {request.TransformClassName}.Map,",
            "// scaffolding the input -> function -> output shape every generated transform follows.",
            "// This is deliberately not full coverage: extend it by hand for edge cases, additional",
            "// rows, or a column this generator's own pilot scope did not cover (see the package's",
            "// own generate-report.md for exactly which columns were skipped and why). Regenerating",
            "// this file freely discards any hand edits -- move those into a second file instead.",
            "// </auto-generated>",
            "using System;",
            "using Etl.Core.Abstractions;",
            $"using {request.RowTypeNamespace};",
            $"using {request.MappingNamespace};",
            $"using {request.EntityNamespace};",
            "using Xunit;",
            "",
            $"namespace {request.TestNamespace};",
            "",
            $"public class {request.TransformClassName}Tests",
            "{",
            "    [Fact]",
            "    public void Map_ComputesExpectedOutput_ForARepresentativeRow()",
            "    {",
            $"        var row = new {request.RowTypeName}",
            "        {",
        };
        lines.AddRange(rowInitLines);
        lines.Add("        };");
        lines.Add($"        var ctx = new RowContext(RowNumber: 1, LoadedAtUtc: {fixedLoadedAtUtc}, SourceName: {Literal(request.TransformClassName)});");
        lines.Add("");
        lines.Add($"        var result = new {request.TransformClassName}().Map(row, ctx);");
        lines.Add("");
        lines.AddRange(assertions);
        lines.Add("    }");
        lines.Add("}");

        return new EmitResult([new GeneratedFile($"{request.TransformClassName}Tests.cs", Rendering.JoinLines(lines))], gaps);
    }

    /// <summary>One representative literal per CLR type this pilot's row/entity properties can
    /// take -- deliberately the SAME small, fixed values <c>ssisx testgen</c>'s own NormalValue
    /// uses (via <see cref="RepresentativeOracleValue"/>), so a human reading both a testgen file
    /// and a starter test recognizes the same convention. <c>internal</c> (not <c>private</c>) so
    /// <see cref="ComponentTestEmitter"/> can reuse the exact same values for a sink/source starter
    /// test's own synthesized rows, rather than a second, independently-drifting literal set.</summary>
    internal static string RepresentativeLiteral(string clrTypeName) => clrTypeName switch
    {
        "string" => "\"Sample\"",
        "short" => "(short)7",
        "int" => "7",
        "long" => "7L",
        "float" => "7.5f",
        "double" => "7.5",
        "decimal" => "7.5m",
        "bool" => "true",
        "DateOnly" => "new DateOnly(2020, 6, 15)",
        "DateTime" => "new DateTime(2020, 6, 15, 12, 0, 0)",
        _ => throw new NotSupportedException($"TransformTestEmitter has no representative literal for CLR type '{clrTypeName}' -- add one before relying on it for a starter test."),
    };

    /// <summary>The SsisValue-typed counterpart of <see cref="RepresentativeLiteral"/>, for
    /// feeding <see cref="Ssis.Runtime.Expressions.SsisExpression.Evaluate"/> the same
    /// representative input a referenced row property was assigned. <c>internal</c> (not
    /// <c>private</c>) so <see cref="RouterTestEmitter"/> can independently evaluate a Conditional
    /// Split case's own FriendlyExpression against the identical representative values, rather
    /// than a second, independently-drifting set.</summary>
    internal static SsisValue RepresentativeOracleValue(SsisType type) => type switch
    {
        SsisType.WStr or SsisType.Str => SsisValue.OfString("Sample", type),
        SsisType.I2 or SsisType.I4 or SsisType.I8 => SsisValue.OfInt(7, type),
        SsisType.R4 => SsisValue.OfFloat(7.5f),
        SsisType.R8 => SsisValue.OfDouble(7.5),
        SsisType.Numeric => SsisValue.OfDecimal(7.5m),
        SsisType.Bool => SsisValue.OfBool(true),
        SsisType.DbTimeStamp or SsisType.DbDate => SsisValue.OfDate(new DateTime(2020, 6, 15, 12, 0, 0), type),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>A representative raw string value and its C# expected-value literal for one Data
    /// Conversion target type -- one per type <see cref="TransformEmitter.WrapDataConversion"/>
    /// itself translates, mirrored exactly (including <c>null</c> for an unsupported type or a
    /// DT_WSTR with no declared width, matching that method's own <c>NotTranslatable</c> cases).
    /// The expected literal is produced by literally invoking the SAME .NET parse call
    /// <c>SsisFnEmitter</c>'s own emitted <c>SsisFn.ToNullable*</c> body uses (same
    /// <c>NumberStyles</c>/<c>CultureInfo</c> arguments), on the SAME raw string the row's own
    /// property is assigned -- computed, not hand-encoded, so it can never quietly drift from
    /// what the generated code actually does for this input. <c>internal</c> (not <c>private</c>)
    /// so <see cref="ComponentTestEmitter"/> can reuse it for a Merge Join mapper's own
    /// Data-Conversion-derived join key/column starter test, rather than a second,
    /// independently-drifting representative set.</summary>
    internal readonly record struct DataConversionRepresentative(string RawLiteral, string ExpectedLiteral);

    internal static DataConversionRepresentative? RepresentativeDataConversion(string? targetDataType, int? length)
    {
        switch (targetDataType?.ToLowerInvariant())
        {
            case "i4":
            {
                const string raw = "123";
                var value = int.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture);
                return new DataConversionRepresentative(Literal(raw), $"(int?){value.ToString(CultureInfo.InvariantCulture)}");
            }
            case "dbdate":
            {
                const string raw = "2020-06-15";
                var value = DateOnly.Parse(raw, CultureInfo.InvariantCulture);
                return new DataConversionRepresentative(Literal(raw), $"(DateOnly?)new DateOnly({value.Year}, {value.Month}, {value.Day})");
            }
            case "r8":
            {
                const string raw = "7.5";
                var value = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
                return new DataConversionRepresentative(Literal(raw), $"(double?){value.ToString(CultureInfo.InvariantCulture)}");
            }
            case "bool":
            {
                const string raw = "true";
                var value = bool.Parse(raw);
                return new DataConversionRepresentative(Literal(raw), $"(bool?){(value ? "true" : "false")}");
            }
            case "i2":
            {
                const string raw = "7";
                var value = short.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture);
                return new DataConversionRepresentative(Literal(raw), $"(short?){value.ToString(CultureInfo.InvariantCulture)}");
            }
            case "i8":
            {
                const string raw = "123456789012";
                var value = long.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture);
                return new DataConversionRepresentative(Literal(raw), $"(long?){value.ToString(CultureInfo.InvariantCulture)}L");
            }
            case "dbtimestamp":
            {
                const string raw = "2020-06-15 12:00:00";
                var value = DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None);
                return new DataConversionRepresentative(Literal(raw), $"(DateTime?)new DateTime({value.Year}, {value.Month}, {value.Day}, {value.Hour}, {value.Minute}, {value.Second})");
            }
            case "wstr":
            {
                // Truncates rather than nulling out -- the one Data Conversion target with a
                // genuinely different failure mode (see WrapDataConversion's own doc comment). No
                // declared width is the same NotTranslatable case that method reports; mirrored
                // here rather than guessed at.
                if (length is not { } maxLength) return null;
                const string candidate = "Sample";
                var expected = candidate.Length <= maxLength ? candidate : candidate[..maxLength];
                return new DataConversionRepresentative(Literal(candidate), Literal(expected));
            }
            default:
                return null;
        }
    }

    private static string Literal(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
