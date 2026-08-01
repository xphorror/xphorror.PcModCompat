using System.Linq.Expressions;
using System.Reflection;

namespace HarmonyLib;

// ABI mirror of Harmony 2.4 HarmonyLib/Tools/SymbolExtensions.cs.
//
// Pure reflection over an expression tree - no IL, no patching - so the shim reproduces upstream
// behaviour exactly, including which shapes throw. MODs use this to name a method without a string
// literal, and CodeInstruction.Call(Expression) is built on it.
public static class SymbolExtensions
{
    public static MethodInfo GetMethodInfo(Expression<Action> expression) => GetMethodInfo((LambdaExpression)expression);

    public static MethodInfo GetMethodInfo<T>(Expression<Action<T>> expression) => GetMethodInfo((LambdaExpression)expression);

    public static MethodInfo GetMethodInfo<T, TResult>(Expression<Func<T, TResult>> expression) => GetMethodInfo((LambdaExpression)expression);

    public static MethodInfo GetMethodInfo(LambdaExpression expression)
    {
        if (expression.Body is not MethodCallExpression outermostExpression)
        {
            // Delegate-returning shapes such as `() => (Action)Method` land here: the compiler wraps the
            // ldftn in a conversion, and the MethodInfo sits in the constant the call is made on.
            if (expression.Body is UnaryExpression { Operand: MethodCallExpression { Object: ConstantExpression { Value: MethodInfo methodInfo } } })
                return methodInfo;
            throw new ArgumentException("Invalid Expression. Expression should consist of a Method call only.");
        }

        var method = outermostExpression.Method
                     ?? throw new Exception($"Cannot find method for expression {expression}");

        return method;
    }

    public static FieldInfo GetFieldInfo<T>(Expression<Func<T>> expression) => GetFieldInfo((LambdaExpression)expression);

    public static FieldInfo GetFieldInfo(LambdaExpression expression)
    {
        if (expression is null)
            throw new ArgumentNullException(nameof(expression));

        var body = expression.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unaryExpression)
            body = unaryExpression.Operand;

        if (body is MemberExpression { Member: FieldInfo field })
            return field;
        throw new ArgumentException("Invalid Expression. Expression should consist of a field access only.", nameof(expression));
    }
}
