using System.Globalization;

namespace Ssis.Runtime.Expressions;

/// <summary>
/// Recursive-descent parser over <see cref="Lexer"/>'s tokens, precedence low-to-high:
/// ternary (?:, right-assoc) &gt; || &gt; &amp;&amp; &gt; ==/!= &gt; &lt;/&gt;/&lt;=/&gt;= &gt; +/- &gt; * / % &gt;
/// unary (!, -, and the (DT_XXX) cast prefix) &gt; primary. Matches the operator set the oracle
/// corpus exercises -- not the full SSIS grammar (no bitwise operators, no <c>#{lineageId}</c>
/// syntax, since testgen consumes <c>FriendlyExpression</c> which never contains those).
/// </summary>
public sealed class Parser
{
    private readonly List<Token> _tokens;
    private int _pos;

    public Parser(string source)
    {
        _tokens = new Lexer(source).Tokenize();
    }

    public static ExprNode Parse(string source)
    {
        var parser = new Parser(source);
        var expr = parser.ParseExpression();
        parser.Expect(TokenKind.Eof);
        return expr;
    }

    private Token Current => _tokens[_pos];
    private bool Check(TokenKind kind) => Current.Kind == kind;

    private Token Advance()
    {
        var t = Current;
        if (_pos < _tokens.Count - 1) _pos++;
        return t;
    }

    private Token Expect(TokenKind kind)
    {
        if (!Check(kind)) throw new SsisExpressionError($"expected {kind} but found {Current.Kind} ('{Current.Text}') at position {Current.Position}");
        return Advance();
    }

    private ExprNode ParseExpression() => ParseTernary();

    private ExprNode ParseTernary()
    {
        var cond = ParseOr();
        if (!Check(TokenKind.Question)) return cond;
        Advance();
        var whenTrue = ParseTernary();
        Expect(TokenKind.Colon);
        var whenFalse = ParseTernary();
        return new Conditional(cond, whenTrue, whenFalse);
    }

    private ExprNode ParseOr()
    {
        var left = ParseAnd();
        while (Check(TokenKind.OrOr))
        {
            Advance();
            left = new BinaryExpr(BinaryOp.Or, left, ParseAnd());
        }
        return left;
    }

    private ExprNode ParseAnd()
    {
        var left = ParseEquality();
        while (Check(TokenKind.AndAnd))
        {
            Advance();
            left = new BinaryExpr(BinaryOp.And, left, ParseEquality());
        }
        return left;
    }

    private ExprNode ParseEquality()
    {
        var left = ParseRelational();
        while (Check(TokenKind.Eq) || Check(TokenKind.NotEq))
        {
            var op = Advance().Kind == TokenKind.Eq ? BinaryOp.Eq : BinaryOp.NotEq;
            left = new BinaryExpr(op, left, ParseRelational());
        }
        return left;
    }

    private ExprNode ParseRelational()
    {
        var left = ParseAdditive();
        while (Check(TokenKind.Lt) || Check(TokenKind.Gt) || Check(TokenKind.Le) || Check(TokenKind.Ge))
        {
            var op = Advance().Kind switch
            {
                TokenKind.Lt => BinaryOp.Lt,
                TokenKind.Gt => BinaryOp.Gt,
                TokenKind.Le => BinaryOp.Le,
                _ => BinaryOp.Ge,
            };
            left = new BinaryExpr(op, left, ParseAdditive());
        }
        return left;
    }

    private ExprNode ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Check(TokenKind.Plus) || Check(TokenKind.Minus))
        {
            var op = Advance().Kind == TokenKind.Plus ? BinaryOp.Add : BinaryOp.Sub;
            left = new BinaryExpr(op, left, ParseMultiplicative());
        }
        return left;
    }

    private ExprNode ParseMultiplicative()
    {
        var left = ParseUnary();
        while (Check(TokenKind.Star) || Check(TokenKind.Slash) || Check(TokenKind.Percent))
        {
            var op = Advance().Kind switch
            {
                TokenKind.Star => BinaryOp.Mul,
                TokenKind.Slash => BinaryOp.Div,
                _ => BinaryOp.Mod,
            };
            left = new BinaryExpr(op, left, ParseUnary());
        }
        return left;
    }

    private ExprNode ParseUnary()
    {
        if (Check(TokenKind.Minus)) { Advance(); return new UnaryExpr(UnaryOp.Negate, ParseUnary()); }
        if (Check(TokenKind.Not)) { Advance(); return new UnaryExpr(UnaryOp.Not, ParseUnary()); }
        return ParsePrimary();
    }

    private ExprNode ParsePrimary()
    {
        if (Check(TokenKind.LParen))
        {
            if (TryParseCastHeader(out var castType, out var arg1, out var arg2))
            {
                var operand = ParseUnary();
                return new Cast(castType, arg1, arg2, operand);
            }

            Advance(); // '('
            var inner = ParseExpression();
            Expect(TokenKind.RParen);
            return inner;
        }

        if (Check(TokenKind.String)) return new StringLiteral(Advance().Text);
        if (Check(TokenKind.Integer)) return new IntLiteral(long.Parse(Advance().Text, CultureInfo.InvariantCulture));
        if (Check(TokenKind.Float)) return new FloatLiteral(double.Parse(Advance().Text, CultureInfo.InvariantCulture));
        if (Check(TokenKind.AtReference)) return new Reference(Advance().Text);

        if (Check(TokenKind.Identifier))
        {
            var name = Current.Text;
            if (string.Equals(name, "TRUE", StringComparison.OrdinalIgnoreCase)) { Advance(); return new BoolLiteral(true); }
            if (string.Equals(name, "FALSE", StringComparison.OrdinalIgnoreCase)) { Advance(); return new BoolLiteral(false); }
            if (string.Equals(name, "NULL", StringComparison.OrdinalIgnoreCase)) return ParseNullLiteral();

            Advance();
            if (Check(TokenKind.LParen)) return ParseFunctionCall(name);
            return new Reference(name);
        }

        throw new SsisExpressionError($"unexpected token {Current.Kind} ('{Current.Text}') at position {Current.Position}");
    }

    private ExprNode ParseNullLiteral()
    {
        Advance(); // "NULL"
        Expect(TokenKind.LParen);
        var typeName = Expect(TokenKind.Identifier).Text;
        var type = SsisTypes.Parse(typeName);
        // Optional length/precision args -- irrelevant to a null's value, consumed so the
        // parser stays in sync (e.g. NULL(DT_WSTR,10), NULL(DT_NUMERIC,10,2)).
        while (Check(TokenKind.Comma))
        {
            Advance();
            ParseExpression();
        }
        Expect(TokenKind.RParen);
        return new NullLiteral(type);
    }

    private ExprNode ParseFunctionCall(string name)
    {
        Advance(); // '('
        var args = new List<ExprNode>();
        if (!Check(TokenKind.RParen))
        {
            args.Add(ParseExpression());
            while (Check(TokenKind.Comma))
            {
                Advance();
                args.Add(ParseExpression());
            }
        }
        Expect(TokenKind.RParen);
        return new FunctionCall(name, args);
    }

    /// <summary>
    /// Looks ahead from the current '(' to decide cast-header vs. plain grouping: a cast
    /// header is '(' followed by an identifier starting with "DT_" (case-insensitive),
    /// optionally ",int" up to twice, then ')'. Consumes the header (through the closing
    /// ')') only when it matches; leaves position unchanged otherwise.
    /// </summary>
    private bool TryParseCastHeader(out SsisType type, out int? arg1, out int? arg2)
    {
        type = default;
        arg1 = null;
        arg2 = null;

        var save = _pos;
        Advance(); // tentatively consume '('
        if (!Check(TokenKind.Identifier) || !Current.Text.StartsWith("DT_", StringComparison.OrdinalIgnoreCase))
        {
            _pos = save;
            return false;
        }

        var typeName = Advance().Text;
        try
        {
            type = SsisTypes.Parse(typeName);
        }
        catch (SsisExpressionError)
        {
            _pos = save;
            return false;
        }

        if (Check(TokenKind.Comma))
        {
            Advance();
            arg1 = int.Parse(Expect(TokenKind.Integer).Text, CultureInfo.InvariantCulture);
            if (Check(TokenKind.Comma))
            {
                Advance();
                arg2 = int.Parse(Expect(TokenKind.Integer).Text, CultureInfo.InvariantCulture);
            }
        }

        if (!Check(TokenKind.RParen))
        {
            _pos = save;
            return false;
        }
        Advance(); // ')'
        return true;
    }
}
