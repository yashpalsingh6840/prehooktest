using Ssis.Extract.Model.Pipeline;
using Ssis.Runtime.Expressions;

namespace Ssis.Extract.Codegen;

/// <summary>
/// A `.dtsx` carries no nullability concept anywhere (confirmed the same way
/// <c>PrimaryKeyInference</c>'s own doc comment already established for primary keys -- neither
/// is in a saved package's <c>externalMetadataColumn</c>/pipeline column XML), so this can't be
/// a fact lookup the way <see cref="Ssis.Extract.Dtsx.LineageBuilder"/>'s edges are. Instead it's
/// evidence-based, the same "gaps not guesses" shape as every other inference in this tool:
/// a Derived Column expression that checks <c>ISNULL(x)</c> is real, direct evidence the author
/// believed <c>x</c> could be null, so <see cref="ColumnReference.IsNullable"/> for that
/// reference is set from exactly that -- nothing else.
///
/// Deliberately scoped to Derived Column expressions only, not Conditional Split conditions
/// (RouterEmitter's own, separate reference-resolution path) -- the one real evidenced need
/// (RBC_Demo_ETL's Package_Transforms.dtsx, DER_Enrich.TenureDays) is a Derived Column, and
/// extending this to Conditional Split conditions too is a genuine, separate, still-open
/// follow-up (see CLAUDE.md's own nullability section) rather than something guessed at here.
/// Also deliberately SQL-source only for now, not CSV (CsvRowEmitter/ClassMapEmitter) -- the
/// one real motivating package's own source is a Flat File, but that flow is separately blocked
/// on the (also unfixed) Data Conversion component gap, so a CSV-sourced fix couldn't be
/// end-to-end verified against it anyway.
/// </summary>
public static class NullabilityInference
{
    /// <summary>Walks every output column's own Expression/FriendlyExpression across the given
    /// Derived Column components, collecting the NAME of any bare <c>Reference</c> passed
    /// directly to <c>ISNULL(...)</c> anywhere in that expression's AST (not just at the root --
    /// the real evidenced case has it as a ternary's own condition, not the whole expression).
    /// Column names, not RefIds -- matches how every row-type emitter this feeds already
    /// identifies a buffer column (by name), and how <c>TransformEmitter</c>'s own
    /// <c>references</c> dictionary is keyed.</summary>
    public static HashSet<string> InferNullableColumnNames(IEnumerable<PipelineComponentSpec> derivedColumns)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var derivedColumn in derivedColumns)
        {
            foreach (var column in derivedColumn.Outputs
                .Where(o => o.IsErrorOut != true)
                .SelectMany(o => o.Columns)
                .Where(c => c.Expression is not null))
            {
                var text = column.FriendlyExpression ?? column.Expression;
                ExprNode ast;
                try
                {
                    ast = SsisExpression.Parse(text!);
                }
                catch (SsisExpressionError)
                {
                    continue; // TransformEmitter's own translation already reports the parse failure as a gap
                }

                CollectIsNullReferences(ast, result);
            }
        }

        return result;
    }

    private static void CollectIsNullReferences(ExprNode node, HashSet<string> result)
    {
        switch (node)
        {
            case FunctionCall { Name: "ISNULL", Args: [Reference r] }:
                result.Add(r.Name);
                break;
            case FunctionCall f:
                foreach (var arg in f.Args) CollectIsNullReferences(arg, result);
                break;
            case BinaryExpr b:
                CollectIsNullReferences(b.Left, result);
                CollectIsNullReferences(b.Right, result);
                break;
            case UnaryExpr u:
                CollectIsNullReferences(u.Operand, result);
                break;
            case Cast c:
                CollectIsNullReferences(c.Operand, result);
                break;
            case Conditional cond:
                CollectIsNullReferences(cond.Condition, result);
                CollectIsNullReferences(cond.WhenTrue, result);
                CollectIsNullReferences(cond.WhenFalse, result);
                break;
        }
    }
}
