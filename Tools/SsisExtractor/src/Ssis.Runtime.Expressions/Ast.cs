namespace Ssis.Runtime.Expressions;

public abstract class ExprNode;

public sealed class StringLiteral(string value) : ExprNode
{
    public string Value { get; } = value;
}

public sealed class IntLiteral(long value) : ExprNode
{
    public long Value { get; } = value;
}

public sealed class FloatLiteral(double value) : ExprNode
{
    public double Value { get; } = value;
}

public sealed class BoolLiteral(bool value) : ExprNode
{
    public bool Value { get; } = value;
}

/// <summary>The <c>NULL(DT_XXX[,len])</c> literal -- a typed null, not a function call.</summary>
public sealed class NullLiteral(SsisType type) : ExprNode
{
    public SsisType Type { get; } = type;
}

/// <summary>A bare identifier (Derived Column's FriendlyExpression form, e.g. "FirstName") or an <c>@[Namespace::Name]</c> reference -- both resolved from the caller-supplied environment, keyed by <see cref="Name"/> exactly as written.</summary>
public sealed class Reference(string name) : ExprNode
{
    public string Name { get; } = name;
}

public sealed class FunctionCall(string name, List<ExprNode> args) : ExprNode
{
    public string Name { get; } = name;
    public List<ExprNode> Args { get; } = args;
}

/// <summary>A <c>(DT_XXX[,a[,b]])</c> cast applied to <see cref="Operand"/>. <see cref="Arg1"/>/<see cref="Arg2"/> are length/precision-scale/codepage, per <see cref="SsisType"/>.</summary>
public sealed class Cast(SsisType type, int? arg1, int? arg2, ExprNode operand) : ExprNode
{
    public SsisType Type { get; } = type;
    public int? Arg1 { get; } = arg1;
    public int? Arg2 { get; } = arg2;
    public ExprNode Operand { get; } = operand;
}

public enum UnaryOp { Negate, Not }

public sealed class UnaryExpr(UnaryOp op, ExprNode operand) : ExprNode
{
    public UnaryOp Op { get; } = op;
    public ExprNode Operand { get; } = operand;
}

public enum BinaryOp { Add, Sub, Mul, Div, Mod, Eq, NotEq, Lt, Gt, Le, Ge, And, Or }

public sealed class BinaryExpr(BinaryOp op, ExprNode left, ExprNode right) : ExprNode
{
    public BinaryOp Op { get; } = op;
    public ExprNode Left { get; } = left;
    public ExprNode Right { get; } = right;
}

public sealed class Conditional(ExprNode condition, ExprNode whenTrue, ExprNode whenFalse) : ExprNode
{
    public ExprNode Condition { get; } = condition;
    public ExprNode WhenTrue { get; } = whenTrue;
    public ExprNode WhenFalse { get; } = whenFalse;
}
