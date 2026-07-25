using System;
using System.Linq;

/// <summary>Shared structured/flat verifier for the SDK extern stack contract.</summary>
public static class UdonAbiVerifier
{
    public static void VerifyInvocation(CExternCall call,
        UdonTypeFactRegistry typeFacts, string functionName)
    {
        if (call == null) throw new ArgumentNullException(nameof(call));
        if (typeFacts == null) throw new ArgumentNullException(nameof(typeFacts));

        var prototype = call.Sig.Prototype
            ?? throw new VerificationException(
                $"Extern '{call.Sig}' has no SDK ABI prototype (function '{functionName}').");

        if (!prototype.HasTypedParameters)
            throw new VerificationException(
                $"Extern '{call.Sig}' has only a name-only ABI fixture; operand verification "
                + $"requires a typed prototype (function '{functionName}').");

        var hasResult = call.Type != StorageTypes.Void;
        var stackOperandCount = call.Args.Count + (hasResult ? 1 : 0);
        if (prototype.Parameters.Count != stackOperandCount)
            throw new VerificationException(
                $"Extern '{call.Sig}' consumes {prototype.Parameters.Count} SDK stack operands, "
                + $"but Core IR supplies {call.Args.Count} argument(s)"
                + (hasResult ? " plus one result destination" : " and no result destination")
                + $" (function '{functionName}').");

        for (var i = 0; i < call.Args.Count; i++)
            VerifyOperand(call, prototype.Parameters[i], call.Args[i].Type, i,
                typeFacts, functionName);

        if (!hasResult) return;
        VerifyGenericComponentResultBinding(call, prototype, functionName);
        var resultParameter = prototype.Parameters[prototype.Parameters.Count - 1];
        if (resultParameter.Mode == UdonAbiParameterMode.In)
            throw new VerificationException(
                $"Extern '{call.Sig}' returns '{call.Type}', but its final SDK operand "
                + $"'{resultParameter.Name}' is input-only (function '{functionName}').");
        VerifyOperand(call, resultParameter, call.Type, call.Args.Count,
            typeFacts, functionName);
    }

    static void VerifyOperand(CExternCall call, UdonAbiParameter parameter,
        StorageType actual, int stackIndex,
        UdonTypeFactRegistry typeFacts, string functionName)
    {
        if (RequiresTransformStrongbox(call.Sig.Key, stackIndex)
            && actual != StorageTypes.Transform)
            throw new VerificationException(
                $"Extern '{call.Sig}' stack operand 0 ('{parameter.Name}', {parameter.Mode}) "
                + "is a generic component-query receiver and must be backed by a "
                + $"'{StorageTypes.Transform}' strongbox, got '{actual}' "
                + $"(function '{functionName}').");

        if (parameter.Type.TryMatch(actual, typeFacts, out var reason))
            return;
        throw new VerificationException(
            $"Extern '{call.Sig}' stack operand {stackIndex} ('{parameter.Name}', "
            + $"{parameter.Mode}) expects ABI type '{parameter.Type}', got '{actual}': "
            + $"{reason} (function '{functionName}').");
    }

    /// <summary>The SDK prototype erases a generic component getter's output to Object/ObjectArray.
    /// Its SystemType constant is the actual T binding. Keep that relationship explicit here so the
    /// general reference raw-copy relaxation cannot accept an unrelated result strongbox.</summary>
    static void VerifyGenericComponentResultBinding(CExternCall call,
        UdonExternPrototype prototype, string functionName)
    {
        var resultPlaceholder = call.Sig.Key.ResultType;
        if ((resultPlaceholder != "T" && resultPlaceholder != "TArray")
            || !IsGenericComponentQueryMember(call.Sig.Key.Member))
            return;

        var typeTokenIndex = -1;
        for (var i = 0; i < call.Args.Count; i++)
        {
            var parameter = prototype.Parameters[i];
            if (parameter.Mode == UdonAbiParameterMode.In
                && parameter.Type.ExactType.Name == StorageTypes.Type.Name)
            {
                if (typeTokenIndex >= 0)
                    throw new VerificationException(
                        $"Extern '{call.Sig}' has more than one SystemType input, so its generic "
                        + $"component result binding is ambiguous (function '{functionName}').");
                typeTokenIndex = i;
            }
        }

        if (typeTokenIndex < 0
            || call.Args[typeTokenIndex] is not CConst { Value: string tokenType })
            throw new VerificationException(
                $"Extern '{call.Sig}' must bind generic result '{resultPlaceholder}' from one "
                + $"constant SystemType operand (function '{functionName}').");

        string expected;
        if (tokenType == "VRCUdonUdonBehaviour")
            expected = resultPlaceholder == "T"
                ? StorageTypes.UdonEventReceiver.Name
                : StorageTypes.ComponentArray.Name;
        else
            expected = resultPlaceholder == "T"
                ? tokenType
                : tokenType + "Array";

        if (call.Type.Name != expected)
            throw new VerificationException(
                $"Extern '{call.Sig}' binds generic result '{resultPlaceholder}' to '{expected}' "
                + $"from SystemType token '{tokenType}', got result strongbox '{call.Type}' "
                + $"(function '{functionName}').");
    }

    /// <summary>The SDK's generic component-query wrapper does not merely call
    /// GetHeapVariable&lt;Component&gt;: it branches on the receiver strongbox's declared CLR type.
    /// Lowering therefore normalizes every explicit receiver through .transform. Pin that execution
    /// contract here so a future removal of the normalization is rejected before UASM emission.</summary>
    static bool RequiresTransformStrongbox(UdonAbiKey key, int stackIndex)
    {
        if (stackIndex != 0 || !HasGenericComponentPayload(key)) return false;
        return IsGenericComponentQueryMember(key.Member);
    }

    static bool IsGenericComponentQueryMember(string member)
    {
        switch (member)
        {
            case "GetComponent":
            case "GetComponentInChildren":
            case "GetComponentInParent":
            case "GetComponents":
            case "GetComponentsInChildren":
            case "GetComponentsInParent":
                return true;
            default:
                return false;
        }
    }

    static bool HasGenericComponentPayload(UdonAbiKey key)
        => IsGenericComponentType(key.ResultType)
           || key.ParameterTypes.Any(IsGenericComponentType);

    static bool IsGenericComponentType(string type)
        => type == "T" || type == "TArray" || type == "ListT";
}
