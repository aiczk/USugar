/// <summary>
/// Represents the three synthetic UASM fields that back a single delegate variable.
/// Constructed from a base field name; provides the resolved field IDs.
/// </summary>
public readonly struct DelegateBundle
{
    public readonly string Target;
    public readonly string Method;
    public readonly string Addr;

    public const string TargetSuffix = "__target";
    public const string MethodSuffix = "__method";
    public const string AddrSuffix   = "__addr";

    public DelegateBundle(string fieldName)
    {
        Target = fieldName + TargetSuffix;
        Method = fieldName + MethodSuffix;
        Addr   = fieldName + AddrSuffix;
    }
}

/// <summary>
/// Calling convention for delegate parameters: which UASM fields hold arguments and return value.
/// </summary>
public struct DelegateConvention
{
    public string[] ArgVarIds;
    public string RetVarId;
}
