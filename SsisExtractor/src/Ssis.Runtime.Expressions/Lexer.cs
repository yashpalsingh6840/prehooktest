using System.Globalization;
using System.Text;

namespace Ssis.Runtime.Expressions;

public enum TokenKind
{
    String,
    Integer,
    Float,
    Identifier,
    /// <summary>An <c>@[Namespace::Name]</c> reference -- <see cref="Token.Text"/> holds the inside text, e.g. "User::X".</summary>
    AtReference,
    Plus, Minus, Star, Slash, Percent,
    Eq, NotEq, Lt, Gt, Le, Ge,
    AndAnd, OrOr, Not,
    Question, Colon, Comma,
    LParen, RParen,
    Eof,
}

public readonly record struct Token(TokenKind Kind, string Text, int Position);

/// <summary>
/// Tokenizes an SSIS expression. Covers the syntax the extractor's <c>ExpressionHarvester</c>
/// can surface (plan §5.5) -- string/numeric/boolean literals, <c>@[Namespace::Name]</c>
/// variable references, bare identifiers (Derived Column's <c>FriendlyExpression</c> writes
/// column references this way), function calls, casts, and the full operator set the oracle
/// corpus exercises. Not a general-purpose tokenizer for the entire SSIS grammar (no line/
/// column tracking beyond a raw character offset, no locale-specific number formats).
/// </summary>
public sealed class Lexer(string source)
{
    private readonly string _s = source;
    private int _pos;

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            var t = Next();
            tokens.Add(t);
            if (t.Kind == TokenKind.Eof) break;
        }
        return tokens;
    }

    private Token Next()
    {
        SkipWhitespace();
        if (_pos >= _s.Length) return new Token(TokenKind.Eof, "", _pos);

        var start = _pos;
        var c = _s[_pos];

        if (c == '"') return LexString();
        if (char.IsDigit(c)) return LexNumber();
        if (c == '@' && Peek(1) == '[') return LexAtReference();
        if (char.IsLetter(c) || c == '_') return LexIdentifier();

        // Two-character operators must be checked before their one-character prefix.
        switch (c)
        {
            case '=' when Peek(1) == '=': _pos += 2; return new Token(TokenKind.Eq, "==", start);
            case '!' when Peek(1) == '=': _pos += 2; return new Token(TokenKind.NotEq, "!=", start);
            case '<' when Peek(1) == '=': _pos += 2; return new Token(TokenKind.Le, "<=", start);
            case '>' when Peek(1) == '=': _pos += 2; return new Token(TokenKind.Ge, ">=", start);
            case '&' when Peek(1) == '&': _pos += 2; return new Token(TokenKind.AndAnd, "&&", start);
            case '|' when Peek(1) == '|': _pos += 2; return new Token(TokenKind.OrOr, "||", start);
        }

        _pos++;
        return c switch
        {
            '+' => new Token(TokenKind.Plus, "+", start),
            '-' => new Token(TokenKind.Minus, "-", start),
            '*' => new Token(TokenKind.Star, "*", start),
            '/' => new Token(TokenKind.Slash, "/", start),
            '%' => new Token(TokenKind.Percent, "%", start),
            '<' => new Token(TokenKind.Lt, "<", start),
            '>' => new Token(TokenKind.Gt, ">", start),
            '!' => new Token(TokenKind.Not, "!", start),
            '?' => new Token(TokenKind.Question, "?", start),
            ':' => new Token(TokenKind.Colon, ":", start),
            ',' => new Token(TokenKind.Comma, ",", start),
            '(' => new Token(TokenKind.LParen, "(", start),
            ')' => new Token(TokenKind.RParen, ")", start),
            _ => throw new SsisExpressionError($"unexpected character '{c}' at position {start}"),
        };
    }

    private char Peek(int offset) => _pos + offset < _s.Length ? _s[_pos + offset] : '\0';

    private void SkipWhitespace()
    {
        while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos])) _pos++;
    }

    private Token LexString()
    {
        var start = _pos;
        _pos++; // opening quote
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _s.Length) throw new SsisExpressionError($"unterminated string literal starting at position {start}");
            var c = _s[_pos];
            if (c == '"')
            {
                // SSIS escapes an embedded quote as "" (doubled), matching the raw XML/string
                // literal convention seen in harvested expressions.
                if (Peek(1) == '"') { sb.Append('"'); _pos += 2; continue; }
                _pos++;
                break;
            }
            sb.Append(c);
            _pos++;
        }
        return new Token(TokenKind.String, sb.ToString(), start);
    }

    private Token LexNumber()
    {
        var start = _pos;
        while (_pos < _s.Length && char.IsDigit(_s[_pos])) _pos++;
        var isFloat = false;
        if (_pos < _s.Length && _s[_pos] == '.' && char.IsDigit(Peek(1)))
        {
            isFloat = true;
            _pos++;
            while (_pos < _s.Length && char.IsDigit(_s[_pos])) _pos++;
        }
        if (_pos < _s.Length && (_s[_pos] == 'e' || _s[_pos] == 'E'))
        {
            var save = _pos;
            _pos++;
            if (_pos < _s.Length && (_s[_pos] == '+' || _s[_pos] == '-')) _pos++;
            if (_pos < _s.Length && char.IsDigit(_s[_pos]))
            {
                isFloat = true;
                while (_pos < _s.Length && char.IsDigit(_s[_pos])) _pos++;
            }
            else
            {
                _pos = save; // not actually an exponent (e.g. a following identifier) -- back off
            }
        }
        var text = _s[start.._pos];
        return new Token(isFloat ? TokenKind.Float : TokenKind.Integer, text, start);
    }

    private Token LexAtReference()
    {
        var start = _pos;
        _pos += 2; // "@["
        var contentStart = _pos;
        while (_pos < _s.Length && _s[_pos] != ']') _pos++;
        if (_pos >= _s.Length) throw new SsisExpressionError($"unterminated @[...] reference starting at position {start}");
        var text = _s[contentStart.._pos];
        _pos++; // ']'
        return new Token(TokenKind.AtReference, text, start);
    }

    private Token LexIdentifier()
    {
        var start = _pos;
        while (_pos < _s.Length && (char.IsLetterOrDigit(_s[_pos]) || _s[_pos] == '_')) _pos++;
        return new Token(TokenKind.Identifier, _s[start.._pos], start);
    }
}
