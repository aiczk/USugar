using System.Collections.Generic;

static class PureExternTable
{
    static readonly HashSet<string> PureMethodNames = new()
    {
        // Arithmetic
        "__op_Addition__", "__op_Subtraction__", "__op_Multiplication__",
        "__op_Division__", "__op_Remainder__",
        // Shift
        "__op_LeftShift__", "__op_RightShift__",
        // Bitwise
        "__op_LogicalAnd__", "__op_LogicalOr__", "__op_LogicalXor__",
        // Comparison
        "__op_Equality__", "__op_Inequality__",
        "__op_LessThan__", "__op_GreaterThan__",
        "__op_LessThanOrEqual__", "__op_GreaterThanOrEqual__",
        // Unary
        "__op_UnaryMinus__", "__op_UnaryNegation__",
        "__op_ConditionalAnd__", "__op_ConditionalOr__",
    };

    public static bool IsPure(string externSig)
    {
        // Extract method name: "Type.MethodName__Params__RetType"
        var dotIdx = externSig.IndexOf('.');
        if (dotIdx < 0) return false;
        var afterDot = externSig.Substring(dotIdx + 1);

        // Check SystemConvert.__To* prefix
        if (externSig.StartsWith("SystemConvert.__To")) return true;

        // Check method name prefix match
        foreach (var name in PureMethodNames)
            if (afterDot.StartsWith(name)) return true;

        return false;
    }
}
