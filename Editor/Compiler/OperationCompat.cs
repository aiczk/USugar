using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>Unity's bundled Roslyn predates IOperation.ChildOperations (added 4.2); Children is the
/// only child-enumeration API that exists in both it and the test toolchain's 4.3 (where it is merely
/// deprecated). Single suppression point — call sites must use this, not Children directly.</summary>
public static class OperationCompat
{
    public static IEnumerable<IOperation> ChildOps(this IOperation op)
    {
#pragma warning disable CS0618
        return op.Children;
#pragma warning restore CS0618
    }
}
