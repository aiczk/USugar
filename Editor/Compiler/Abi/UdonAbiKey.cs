using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Semantic identity of one Udon extern. Compiler code constructs keys from an
/// owner, member, ordered parameter types, and optional result type; the SDK's
/// punctuation-based registry protocol is assembled only by
/// <see cref="ToRegistryName"/>.
/// </summary>
public readonly struct UdonAbiKey : IEquatable<UdonAbiKey>
{
    readonly IReadOnlyList<string> _parameterTypes;

    public string Owner { get; }
    public string Member { get; }
    public IReadOnlyList<string> ParameterTypes
        => _parameterTypes ?? Array.Empty<string>();
    public string ResultType { get; }
    public bool HasResult => ResultType != null;
    public bool HasExplicitParameterList { get; }

    UdonAbiKey(string owner, string member, IEnumerable<string> parameterTypes,
        string resultType, bool hasExplicitParameterList = false)
    {
        Owner = NormalizeOwner(owner);
        Member = NormalizeMember(member);
        _parameterTypes = Array.AsReadOnly(
            (parameterTypes ?? throw new ArgumentNullException(nameof(parameterTypes)))
                .Select(NormalizeType)
                .ToArray());
        ResultType = resultType == null ? null : NormalizeType(resultType);
        HasExplicitParameterList = hasExplicitParameterList;
    }

    public static UdonAbiKey Method(string owner, string member,
        IEnumerable<string> parameterTypes, string resultType)
        => new(owner, member, parameterTypes, resultType);

    public static UdonAbiKey Method(string owner, string member, string resultType)
        => new(owner, member, Array.Empty<string>(), resultType);

    public static UdonAbiKey VoidMethod(string owner, string member,
        params string[] parameterTypes)
        => new(owner, member, parameterTypes, "SystemVoid");

    public static UdonAbiKey OmittedResult(string owner, string member,
        params string[] parameterTypes)
        => new(owner, member, parameterTypes, null);

    public static UdonAbiKey Constructor(string owner,
        IEnumerable<string> parameterTypes, string resultType)
        => new(owner, "ctor", parameterTypes, resultType,
            hasExplicitParameterList: true);

    public static UdonAbiKey Unary(string owner, string member,
        string operandType, string resultType)
        => new(owner, member, new[] { operandType }, resultType);

    public static UdonAbiKey Binary(string owner, string member,
        string leftType, string rightType, string resultType)
        => new(owner, member, new[] { leftType, rightType }, resultType);

    public UdonAbiKey WithOwner(string owner)
        => new(owner, Member, ParameterTypes, ResultType,
            HasExplicitParameterList);

    static string NormalizeOwner(string owner)
    {
        if (string.IsNullOrEmpty(owner))
            throw new ArgumentException("An ABI owner is required.", nameof(owner));
        return ExternResolver.RemapExternOwnerType(
            ExternResolver.SanitizeTypeName(owner));
    }

    static string NormalizeType(string type)
    {
        if (string.IsNullOrEmpty(type))
            throw new ArgumentException("An ABI storage type is required.", nameof(type));
        return ExternResolver.SanitizeTypeName(type);
    }

    static string NormalizeMember(string member)
    {
        if (string.IsNullOrEmpty(member))
            throw new ArgumentException("An ABI member is required.", nameof(member));
        if (member.StartsWith("__", StringComparison.Ordinal))
            throw new ArgumentException(
                "An ABI key uses a semantic member name, without registry punctuation.",
                nameof(member));
        return member;
    }

    public bool Equals(UdonAbiKey other)
        => string.Equals(Owner, other.Owner, StringComparison.Ordinal)
           && string.Equals(Member, other.Member, StringComparison.Ordinal)
           && string.Equals(ResultType, other.ResultType, StringComparison.Ordinal)
           && HasExplicitParameterList == other.HasExplicitParameterList
           && ParameterTypes.SequenceEqual(other.ParameterTypes, StringComparer.Ordinal);

    public override bool Equals(object obj)
        => obj is UdonAbiKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = StringComparer.Ordinal.GetHashCode(Owner ?? "");
            hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(Member ?? "");
            hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(ResultType ?? "");
            hash = hash * 397 ^ HasExplicitParameterList.GetHashCode();
            foreach (var parameter in ParameterTypes)
                hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(parameter);
            return hash;
        }
    }

    /// <summary>The sole encoder for the SDK extern registry-name protocol.</summary>
    internal string ToRegistryName()
    {
        if (string.IsNullOrEmpty(Owner) || string.IsNullOrEmpty(Member))
            throw new InvalidOperationException(
                "A default UdonAbiKey cannot be serialized.");
        var text = Owner + ".__" + Member;
        if (ParameterTypes.Count > 0 || HasExplicitParameterList)
            text += "__" + string.Join("_", ParameterTypes);
        if (HasResult)
            text += "__" + ResultType;
        return text;
    }

    public override string ToString() => ToRegistryName();

    public static bool operator ==(UdonAbiKey left, UdonAbiKey right) => left.Equals(right);
    public static bool operator !=(UdonAbiKey left, UdonAbiKey right) => !left.Equals(right);
}

/// <summary>Common VM primitives used by lowering and IR normalization.</summary>
public static class UdonAbi
{
    public static UdonAbiKey Int32(string member)
        => UdonAbiKey.Binary("SystemInt32", member,
            "SystemInt32", "SystemInt32", "SystemInt32");

    public static UdonAbiKey Int32Comparison(string member)
        => UdonAbiKey.Binary("SystemInt32", member,
            "SystemInt32", "SystemInt32", "SystemBoolean");

    public static UdonAbiKey BooleanBinary(string member)
        => UdonAbiKey.Binary("SystemBoolean", member,
            "SystemBoolean", "SystemBoolean", "SystemBoolean");

    public static readonly UdonAbiKey Int32Add = Int32("op_Addition");
    public static readonly UdonAbiKey Int32Subtract = Int32("op_Subtraction");
    public static readonly UdonAbiKey Int32Multiply = Int32("op_Multiplication");
    public static readonly UdonAbiKey Int32LessThan = Int32Comparison("op_LessThan");
    public static readonly UdonAbiKey BooleanNot =
        UdonAbiKey.Unary("SystemBoolean", "op_UnaryNegation",
            "SystemBoolean", "SystemBoolean");
    public static readonly UdonAbiKey StringEquality =
        UdonAbiKey.Binary("SystemString", "op_Equality",
            "SystemString", "SystemString", "SystemBoolean");
    public static readonly UdonAbiKey StringConcatObjects =
        UdonAbiKey.Binary("SystemString", "Concat",
            "SystemObject", "SystemObject", "SystemString");
    public static readonly UdonAbiKey ObjectEquality =
        UdonAbiKey.Binary("SystemObject", "op_Equality",
            "SystemObject", "SystemObject", "SystemBoolean");
    public static readonly UdonAbiKey ObjectInequality =
        UdonAbiKey.Binary("SystemObject", "op_Inequality",
            "SystemObject", "SystemObject", "SystemBoolean");
    public static readonly UdonAbiKey ObjectEquals =
        UdonAbiKey.Binary("SystemObject", "Equals",
            "SystemObject", "SystemObject", "SystemBoolean");
    public static readonly UdonAbiKey BooleanLogicalAnd =
        BooleanBinary("op_LogicalAnd");
    public static readonly UdonAbiKey BooleanLogicalOr =
        BooleanBinary("op_LogicalOr");
    public static readonly UdonAbiKey BooleanConditionalAnd =
        BooleanBinary("op_ConditionalAnd");
    public static readonly UdonAbiKey BooleanConditionalOr =
        BooleanBinary("op_ConditionalOr");
    public static readonly UdonAbiKey DebugLogError =
        UdonAbiKey.VoidMethod("UnityEngineDebug", "LogError", "SystemObject");
    public static readonly UdonAbiKey SystemArrayLength =
        UdonAbiKey.Method("SystemArray", "get_Length", "SystemInt32");

    public static UdonAbiKey ArrayConstructor(string arrayType)
        => UdonAbiKey.Constructor(arrayType, new[] { "SystemInt32" }, arrayType);

    public static UdonAbiKey ArrayGet(string arrayType, string elementType)
        => UdonAbiKey.Method(arrayType, "Get", new[] { "SystemInt32" }, elementType);

    public static UdonAbiKey ArraySet(string arrayType, string elementType)
        => UdonAbiKey.Method(arrayType, "Set",
            new[] { "SystemInt32", elementType }, "SystemVoid");

    public static UdonAbiKey ArrayLength(string arrayType)
        => UdonAbiKey.Method(arrayType, "get_Length", "SystemInt32");

    public static UdonAbiKey Convert(string fromType, string toType)
    {
        var normalizedTarget = ExternResolver.SanitizeTypeName(toType);
        var shortName = normalizedTarget.StartsWith("System", StringComparison.Ordinal)
            ? normalizedTarget.Substring(6)
            : normalizedTarget;
        return UdonAbiKey.Method("SystemConvert", "To" + shortName,
            new[] { fromType }, toType);
    }
}
