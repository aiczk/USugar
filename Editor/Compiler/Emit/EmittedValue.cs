using Microsoft.CodeAnalysis;

/// <summary>
/// A lowered expression plus the semantic facts that remain relevant after it has become Core IR.
/// Phase 5 keeps existing <see cref="CLeaf"/> return paths intact and introduces this as the
/// migration target for boundary-sensitive values.
/// </summary>
public readonly struct EmittedValue
{
    public readonly CLeaf Leaf;
    public readonly ValueInfo Info;

    public EmittedValue(CLeaf leaf, ValueInfo info)
    {
        Leaf = leaf;
        Info = info;
    }

    public ITypeSymbol StaticType => Info.StaticType;
}
