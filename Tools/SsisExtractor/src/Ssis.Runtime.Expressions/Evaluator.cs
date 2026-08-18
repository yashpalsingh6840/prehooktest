using System.Globalization;

namespace Ssis.Runtime.Expressions;

/// <summary>
/// Tree-walking evaluator matching the semantics measured in the oracle corpus (see
/// docs/gate2-schema.md for the catalog). The single rule everything else in this class
/// follows from: <b>SSIS never implicitly coerces between types</b> -- <c>1 + "a"</c>,
/// <c>"1" + 1</c>, <c>LEN(123)</c>, and <c>SUBSTRING(s,"1",2)</c> are all measured errors.
/// Every operator here therefore checks both operand types explicitly and throws
/// <see cref="SsisExpressionError"/> on a mismatch rather than attempting a conversion --
/// which, as a side effect, is also what makes a mistyped argument to <see cref="SsisValue.AsInt64"/>
/// etc. fail loudly instead of silently coercing.
/// </summary>
public static class Evaluator
{
    public static SsisValue Evaluate(ExprNode node, IReadOnlyDictionary<string, SsisValue> env) => node switch
    {
        StringLiteral n => SsisValue.OfString(n.Value),
        IntLiteral n => SsisValue.OfInt(n.Value),
        FloatLiteral n => SsisValue.OfDouble(n.Value),
        BoolLiteral n => SsisValue.OfBool(n.Value),
        NullLiteral n => SsisValue.Null(n.Type),
        Reference n => env.TryGetValue(n.Name, out var v) ? v : throw new SsisExpressionError($"unresolved reference '{n.Name}' -- not present in the supplied environment"),
        Cast n => EvaluateCast(Evaluate(n.Operand, env), n.Type, n.Arg1, n.Arg2),
        UnaryExpr n => EvaluateUnary(n, env),
        BinaryExpr n => EvaluateBinary(n, env),
        Conditional n => EvaluateConditional(n, env),
        FunctionCall n => Functions.Call(n.Name, n.Args.Select(a => Evaluate(a, env)).ToList()),
        _ => throw new SsisExpressionError($"unhandled node type {node.GetType().Name}"),
    };

    private static SsisValue EvaluateUnary(UnaryExpr n, IReadOnlyDictionary<string, SsisValue> env)
    {
        var v = Evaluate(n.Operand, env);
        if (n.Op == UnaryOp.Negate)
        {
            if (!SsisTypes.IsNumeric(v.Type)) throw new SsisExpressionError($"unary '-' requires a numeric operand, got {v.Type}");
            if (v.IsNull) return SsisValue.Null(v.Type);
            return v.Type switch
            {
                SsisType.Numeric => SsisValue.OfDecimal(-v.AsDecimal),
                SsisType.R4 => SsisValue.OfFloat(-(float)v.AsDouble),
                SsisType.R8 => SsisValue.OfDouble(-v.AsDouble),
                _ => SsisValue.OfInt(-v.AsInt64, v.Type),
            };
        }

        // UnaryOp.Not
        if (v.Type != SsisType.Bool) throw new SsisExpressionError($"unary '!' requires a boolean operand, got {v.Type}");
        return v.IsNull ? SsisValue.Null(SsisType.Bool) : SsisValue.OfBool(!v.AsBool);
    }

    private static SsisValue EvaluateBinary(BinaryExpr n, IReadOnlyDictionary<string, SsisValue> env)
    {
        var l = Evaluate(n.Left, env);
        var r = Evaluate(n.Right, env);
        return n.Op switch
        {
            BinaryOp.Add => Add(l, r),
            BinaryOp.Sub => Arithmetic(l, r, "-", (a, b) => a - b, (a, b) => a - b, (a, b) => a - b),
            BinaryOp.Mul => Arithmetic(l, r, "*", (a, b) => a * b, (a, b) => a * b, (a, b) => a * b),
            BinaryOp.Div => Divide(l, r),
            BinaryOp.Mod => Modulo(l, r),
            BinaryOp.Eq => Equality(l, r, negate: false),
            BinaryOp.NotEq => Equality(l, r, negate: true),
            BinaryOp.Lt => Relational(l, r, cmp => cmp < 0),
            BinaryOp.Gt => Relational(l, r, cmp => cmp > 0),
            BinaryOp.Le => Relational(l, r, cmp => cmp <= 0),
            BinaryOp.Ge => Relational(l, r, cmp => cmp >= 0),
            BinaryOp.And => KleeneAnd(l, r),
            BinaryOp.Or => KleeneOr(l, r),
            _ => throw new SsisExpressionError($"unhandled operator {n.Op}"),
        };
    }

    private static SsisValue EvaluateConditional(Conditional n, IReadOnlyDictionary<string, SsisValue> env)
    {
        var cond = Evaluate(n.Condition, env);
        if (cond.Type != SsisType.Bool) throw new SsisExpressionError($"ternary condition must be boolean, got {cond.Type}");

        if (!cond.IsNull) return cond.AsBool ? Evaluate(n.WhenTrue, env) : Evaluate(n.WhenFalse, env);

        // Measured: NULL(DT_BOOL) ? "a" : "b" => NULL. The VALUE is unconditionally null, but
        // a typed null still needs a type. Evaluating a branch to learn its type is an
        // approximation (real SSIS presumably infers this statically, without evaluating
        // either branch) -- good enough here since a null result's type only matters for a
        // further cast/operator downstream, not for equality with the corpus's own NULL rows.
        try { return SsisValue.Null(Evaluate(n.WhenTrue, env).Type); }
        catch (SsisExpressionError) { return SsisValue.Null(Evaluate(n.WhenFalse, env).Type); }
    }

    // --- arithmetic -----------------------------------------------------------------------

    private static SsisValue Add(SsisValue l, SsisValue r)
    {
        if (SsisTypes.IsString(l.Type) && SsisTypes.IsString(r.Type))
        {
            if (l.IsNull || r.IsNull) return SsisValue.Null(SsisType.WStr);
            return SsisValue.OfString(l.AsString + r.AsString);
        }
        return Arithmetic(l, r, "+", (a, b) => a + b, (a, b) => a + b, (a, b) => a + b);
    }

    private static SsisValue Arithmetic(SsisValue l, SsisValue r, string opSymbol, Func<decimal, decimal, decimal> decOp, Func<double, double, double> dblOp, Func<long, long, long> longOp)
    {
        if (!SsisTypes.IsNumeric(l.Type) || !SsisTypes.IsNumeric(r.Type))
            throw new SsisExpressionError($"'{opSymbol}' is not defined between {l.Type} and {r.Type} -- SSIS never implicitly coerces string<->number");

        var resultType = SsisTypes.Promote(l.Type, r.Type);
        if (l.IsNull || r.IsNull) return SsisValue.Null(resultType);

        if (resultType == SsisType.Numeric) return SsisValue.OfDecimal(decOp(l.AsDecimal, r.AsDecimal));
        if (SsisTypes.IsFloat(resultType)) return resultType == SsisType.R4 ? SsisValue.OfFloat((float)dblOp(l.AsDouble, r.AsDouble)) : SsisValue.OfDouble(dblOp(l.AsDouble, r.AsDouble));
        return SsisValue.OfInt(longOp(l.AsInt64, r.AsInt64), resultType);
    }

    private static SsisValue Divide(SsisValue l, SsisValue r)
    {
        if (!SsisTypes.IsNumeric(l.Type) || !SsisTypes.IsNumeric(r.Type))
            throw new SsisExpressionError($"'/' is not defined between {l.Type} and {r.Type}");

        var resultType = SsisTypes.Promote(l.Type, r.Type);
        if (l.IsNull || r.IsNull) return SsisValue.Null(resultType);

        // Divide-by-zero: measured for integer modulo (5 % 0 errors in the corpus); no
        // float/decimal divide-by-zero row exists. Throwing uniformly here rather than
        // producing a silent Infinity/NaN is the conservative choice, not a measured fact --
        // add a corpus case before relying on the float/decimal path specifically.
        if (resultType == SsisType.Numeric)
        {
            if (r.AsDecimal == 0m) throw new SsisExpressionError("division by zero");
            return SsisValue.OfDecimal(l.AsDecimal / r.AsDecimal);
        }
        if (SsisTypes.IsFloat(resultType))
        {
            if (r.AsDouble == 0.0) throw new SsisExpressionError("division by zero");
            var d = l.AsDouble / r.AsDouble;
            return resultType == SsisType.R4 ? SsisValue.OfFloat((float)d) : SsisValue.OfDouble(d);
        }
        // Both integral: SSIS's integer division (measured: 5/2 => 2). Truncation direction
        // for negative operands is not in the corpus; C#'s '/' (toward zero) is used as the
        // ordinary default, not a verified fact.
        if (r.AsInt64 == 0) throw new SsisExpressionError("division by zero");
        return SsisValue.OfInt(l.AsInt64 / r.AsInt64, resultType);
    }

    private static SsisValue Modulo(SsisValue l, SsisValue r)
    {
        // Measured only for integral operands (5 % 3 => 2, -5 % 3 => -2, 5 % 0 => error).
        // SSIS may well support '%' on floats too (most languages do) but that's not in the
        // corpus, so this deliberately throws rather than guess a semantics for it.
        if (!SsisTypes.IsIntegral(l.Type) || !SsisTypes.IsIntegral(r.Type))
            throw new SsisExpressionError($"'%' is only implemented for integral operands here (not measured for {l.Type}/{r.Type} in the oracle corpus)");

        var resultType = SsisTypes.Promote(l.Type, r.Type);
        if (l.IsNull || r.IsNull) return SsisValue.Null(resultType);
        if (r.AsInt64 == 0) throw new SsisExpressionError("division by zero");
        return SsisValue.OfInt(l.AsInt64 % r.AsInt64, resultType);
    }

    // --- comparisons: NULL propagates (three-valued, matching the oracle corpus exactly) ---

    private static SsisValue Equality(SsisValue l, SsisValue r, bool negate)
    {
        if (l.IsNull || r.IsNull) return SsisValue.Null(SsisType.Bool);

        bool eq;
        if (SsisTypes.IsString(l.Type) && SsisTypes.IsString(r.Type)) eq = string.Equals(l.AsString, r.AsString, StringComparison.Ordinal);
        else if (SsisTypes.IsNumeric(l.Type) && SsisTypes.IsNumeric(r.Type)) eq = CompareNumeric(l, r) == 0;
        else if (l.Type == SsisType.Bool && r.Type == SsisType.Bool) eq = l.AsBool == r.AsBool;
        else if (SsisTypes.IsDate(l.Type) && SsisTypes.IsDate(r.Type)) eq = l.AsDate == r.AsDate;
        else throw new SsisExpressionError($"'==' is not defined between {l.Type} and {r.Type}");

        return SsisValue.OfBool(negate ? !eq : eq);
    }

    private static SsisValue Relational(SsisValue l, SsisValue r, Func<int, bool> test)
    {
        if (l.IsNull || r.IsNull) return SsisValue.Null(SsisType.Bool);

        int cmp;
        // Measured: relational string comparison is CULTURE-AWARE (lowercase sorts before
        // its own uppercase; base letters compare alphabetically regardless of case), NOT
        // ordinal -- unlike '=='/'!=' above. Verified against .NET 8's own
        // CultureInfo.InvariantCulture comparer, which reproduces every relational row in
        // the oracle corpus exactly (see docs/gate2-schema.md) despite running on a
        // different CLR/globalization stack (ICU) than the SSIS process that produced the
        // corpus (Windows NLS) -- a real risk that was checked, not assumed away.
        if (SsisTypes.IsString(l.Type) && SsisTypes.IsString(r.Type)) cmp = string.Compare(l.AsString, r.AsString, CultureInfo.InvariantCulture, CompareOptions.None);
        else if (SsisTypes.IsNumeric(l.Type) && SsisTypes.IsNumeric(r.Type)) cmp = CompareNumeric(l, r);
        else if (SsisTypes.IsDate(l.Type) && SsisTypes.IsDate(r.Type)) cmp = l.AsDate.CompareTo(r.AsDate);
        else throw new SsisExpressionError($"relational operator is not defined between {l.Type} and {r.Type}");

        return SsisValue.OfBool(test(cmp));
    }

    private static int CompareNumeric(SsisValue l, SsisValue r)
    {
        var promoted = SsisTypes.Promote(l.Type, r.Type);
        if (promoted == SsisType.Numeric) return l.AsDecimal.CompareTo(r.AsDecimal);
        if (SsisTypes.IsFloat(promoted)) return l.AsDouble.CompareTo(r.AsDouble);
        return l.AsInt64.CompareTo(r.AsInt64);
    }

    // --- logical: Kleene three-valued AND/OR, matching the corpus's NULL-operand rows ------

    private static SsisValue KleeneAnd(SsisValue l, SsisValue r)
    {
        RequireBool(l); RequireBool(r);
        if ((!l.IsNull && !l.AsBool) || (!r.IsNull && !r.AsBool)) return SsisValue.OfBool(false);
        if (l.IsNull || r.IsNull) return SsisValue.Null(SsisType.Bool);
        return SsisValue.OfBool(true);
    }

    private static SsisValue KleeneOr(SsisValue l, SsisValue r)
    {
        RequireBool(l); RequireBool(r);
        if ((!l.IsNull && l.AsBool) || (!r.IsNull && r.AsBool)) return SsisValue.OfBool(true);
        if (l.IsNull || r.IsNull) return SsisValue.Null(SsisType.Bool);
        return SsisValue.OfBool(false);
    }

    private static void RequireBool(SsisValue v)
    {
        if (v.Type != SsisType.Bool) throw new SsisExpressionError($"'&&'/'||' require boolean operands, got {v.Type}");
    }

    // --- casts ------------------------------------------------------------------------------

    internal static SsisValue EvaluateCast(SsisValue operand, SsisType targetType, int? arg1, int? arg2)
    {
        // Measured: casting a NULL of any type to any other type stays NULL unconditionally
        // -- e.g. (DT_WSTR,10)NULL(DT_I4) => NULL, with no length check applied even though
        // the target length is small. Null-in-null-out short-circuits everything below.
        if (operand.IsNull) return SsisValue.Null(targetType);

        return targetType switch
        {
            SsisType.WStr or SsisType.Str => CastToString(operand, targetType, arg1),
            SsisType.I2 or SsisType.I4 or SsisType.I8 => CastToInteger(operand, targetType),
            SsisType.R4 => SsisValue.OfFloat((float)CastToDouble(operand)),
            SsisType.R8 => SsisValue.OfDouble(CastToDouble(operand)),
            SsisType.Numeric => CastToNumeric(operand, arg2),
            SsisType.Bool => CastToBool(operand),
            SsisType.DbTimeStamp or SsisType.DbDate => CastToDate(operand, targetType),
            _ => throw new SsisExpressionError($"cast to {targetType} is not implemented"),
        };
    }

    private static SsisValue CastToString(SsisValue operand, SsisType targetType, int? length)
    {
        if (length is not int n) throw new SsisExpressionError($"({(targetType == SsisType.WStr ? "DT_WSTR" : "DT_STR")}) cast requires a length argument");
        var s = operand.ToDisplayString();
        // Measured: SSIS's string cast NEVER silently truncates -- an over-length result is a
        // hard error, both for DT_WSTR and (codepage aside, not modeled here) DT_STR.
        if (s.Length > n) throw new SsisExpressionError($"string of length {s.Length} does not fit in {(targetType == SsisType.WStr ? "DT_WSTR" : "DT_STR")}({n})");
        return SsisValue.OfString(s, targetType);
    }

    private static SsisValue CastToInteger(SsisValue operand, SsisType targetType)
    {
        long iv;
        if (SsisTypes.IsString(operand.Type))
        {
            // Measured: no partial parse ("12abc" and "" both error), leading sign and
            // surrounding whitespace are fine (" 12 ", "+12" both succeed), no decimal point.
            var trimmed = operand.AsString.Trim();
            if (!long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out iv))
                throw new SsisExpressionError($"'{operand.AsString}' cannot be cast to {targetType} -- not a valid integer literal");
        }
        else if (SsisTypes.IsFloat(operand.Type))
        {
            // Measured: rounds half-to-even ((DT_I4)0.5 => 0, 1.5 => 2, 2.5 => 2), not
            // truncation and not round-half-away-from-zero.
            iv = (long)Math.Round(operand.AsDouble, MidpointRounding.ToEven);
        }
        else if (operand.Type == SsisType.Numeric)
        {
            iv = (long)Math.Round(operand.AsDecimal, MidpointRounding.ToEven);
        }
        else if (operand.Type == SsisType.Bool)
        {
            // Measured: (DT_I4)TRUE => -1 (the classic VB/COM Automation Boolean-as-integer
            // convention), not 1.
            iv = operand.AsBool ? -1 : 0;
        }
        else if (SsisTypes.IsIntegral(operand.Type))
        {
            iv = operand.AsInt64;
        }
        else
        {
            throw new SsisExpressionError($"cannot cast {operand.Type} to {targetType}");
        }
        return SsisValue.OfInt(iv, targetType);
    }

    private static double CastToDouble(SsisValue operand)
    {
        if (SsisTypes.IsString(operand.Type))
        {
            // Measured: (DT_R8)"1e3" => 1000 -- exponent notation accepted.
            if (!double.TryParse(operand.AsString.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                throw new SsisExpressionError($"'{operand.AsString}' cannot be cast to a float");
            return d;
        }
        if (SsisTypes.IsNumeric(operand.Type)) return operand.AsDouble;
        if (operand.Type == SsisType.Bool) return operand.AsBool ? -1 : 0;
        throw new SsisExpressionError($"cannot cast {operand.Type} to a float");
    }

    private static SsisValue CastToNumeric(SsisValue operand, int? scale)
    {
        decimal dec;
        if (SsisTypes.IsString(operand.Type))
        {
            if (!decimal.TryParse(operand.AsString.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out dec))
                throw new SsisExpressionError($"'{operand.AsString}' cannot be cast to DT_NUMERIC");
        }
        else if (SsisTypes.IsNumeric(operand.Type))
        {
            dec = operand.AsDecimal;
        }
        else
        {
            throw new SsisExpressionError($"cannot cast {operand.Type} to DT_NUMERIC");
        }
        // Measured: (DT_NUMERIC,10,2)"1.005" => "1.00" -- half-to-even on the declared scale.
        if (scale is int s) dec = Math.Round(dec, s, MidpointRounding.ToEven);
        return SsisValue.OfDecimal(dec);
    }

    private static SsisValue CastToBool(SsisValue operand)
    {
        if (operand.Type == SsisType.Bool) return operand;

        if (SsisTypes.IsString(operand.Type))
        {
            var s = operand.AsString.Trim();
            // Measured: "true"/"1" both => True, "0" => False, "2" => True (nonzero-numeric
            // fallback), tried in that order.
            if (bool.TryParse(s, out var b)) return SsisValue.OfBool(b);
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return SsisValue.OfBool(n != 0);
            throw new SsisExpressionError($"'{operand.AsString}' cannot be cast to DT_BOOL");
        }
        if (SsisTypes.IsNumeric(operand.Type)) return SsisValue.OfBool(operand.AsDouble != 0);
        throw new SsisExpressionError($"cannot cast {operand.Type} to DT_BOOL");
    }

    private static SsisValue CastToDate(SsisValue operand, SsisType targetType)
    {
        DateTime date;
        if (SsisTypes.IsDate(operand.Type))
        {
            date = operand.AsDate;
        }
        else if (SsisTypes.IsString(operand.Type))
        {
            if (!DateTime.TryParse(operand.AsString, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                throw new SsisExpressionError($"'{operand.AsString}' cannot be cast to {targetType}");
        }
        else
        {
            throw new SsisExpressionError($"cannot cast {operand.Type} to {targetType}");
        }
        if (targetType == SsisType.DbDate) date = date.Date;
        return SsisValue.OfDate(date, targetType);
    }
}
