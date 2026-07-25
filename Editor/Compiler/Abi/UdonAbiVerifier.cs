using System;
using System.Collections.Generic;

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
        VerifyResultEvidence(call, functionName);

        // Name-only registry fixtures are deliberately confined to the test
        // assembly. Production prototypes are always typed.
        if (!prototype.HasTypedParameters) return;

        var hasResult = call.Type != StorageTypes.Void;
        var stackOperandCount = call.Args.Count + (hasResult ? 1 : 0);
        if (prototype.Parameters.Count != stackOperandCount)
            throw new VerificationException(
                $"Extern '{call.Sig}' consumes {prototype.Parameters.Count} SDK stack operands, "
                + $"but Core IR supplies {call.Args.Count} argument(s)"
                + (hasResult ? " plus one result destination" : " and no result destination")
                + $" (function '{functionName}').");

        var genericBindings = new Dictionary<string, StorageType>(StringComparer.Ordinal);
        for (var i = 0; i < call.Args.Count; i++)
            VerifyOperand(call, prototype.Parameters[i], call.Args[i].Type, i,
                genericBindings, typeFacts, functionName);

        if (!hasResult) return;
        var resultParameter = prototype.Parameters[prototype.Parameters.Count - 1];
        if (resultParameter.Mode == UdonAbiParameterMode.In)
            throw new VerificationException(
                $"Extern '{call.Sig}' returns '{call.Type}', but its final SDK operand "
                + $"'{resultParameter.Name}' is input-only (function '{functionName}').");
        VerifyOperand(call, resultParameter, call.Type, call.Args.Count,
            genericBindings, typeFacts, functionName);
    }

    static void VerifyOperand(CExternCall call, UdonAbiParameter parameter,
        StorageType actual, int stackIndex,
        IDictionary<string, StorageType> genericBindings,
        UdonTypeFactRegistry typeFacts, string functionName)
    {
        if (RequiresTransformStrongbox(call.Sig.Key, stackIndex)
            && actual != StorageTypes.Transform)
            throw new VerificationException(
                $"Extern '{call.Sig}' stack operand 0 ('{parameter.Name}', {parameter.Mode}) "
                + "is a generic component-query receiver and must be backed by a "
                + $"'{StorageTypes.Transform}' strongbox, got '{actual}' "
                + $"(function '{functionName}').");

        // GetProgramVariable is an intentionally type-erased VM channel. Its SDK ABI output is
        // object, while CProgramVariableLoad/CrossCall carry the statically known remote field or
        // return-slot type through flattening. The wrapper writes the dynamic value without a CLR
        // conversion; preserving that producer-owned schema is the one legitimate object->T result
        // exception. Ordinary object-returning externs remain directional and cannot claim T.
        if (IsTypedProgramVariableResult(call, parameter, stackIndex))
            return;

        if (parameter.Type.TryMatch(
                actual, parameter.Mode, genericBindings, typeFacts, out var reason))
            return;
        throw new VerificationException(
            $"Extern '{call.Sig}' stack operand {stackIndex} ('{parameter.Name}', "
            + $"{parameter.Mode}) expects ABI type '{parameter.Type}', got '{actual}': "
            + $"{reason} (function '{functionName}').");
    }

    static bool IsTypedProgramVariableResult(CExternCall call,
        UdonAbiParameter parameter, int stackIndex)
        => call.ResultEvidence == ExternResultEvidence.TypedProgramVariableSchema
           && stackIndex == call.Args.Count
           && parameter.Mode == UdonAbiParameterMode.Out
           && parameter.Type.Kind == UdonAbiType.PatternKind.Exact
           && parameter.Type.ExactType == StorageTypes.Object
           && call.Sig.Key == ExternResolver.EventReceiverGetProgramVariable;

    static void VerifyResultEvidence(CExternCall call, string functionName)
    {
        switch (call.ResultEvidence)
        {
            case ExternResultEvidence.None:
                return;
            case ExternResultEvidence.TypedProgramVariableSchema:
                if (call.Sig.Key == ExternResolver.EventReceiverGetProgramVariable
                    && call.Type != StorageTypes.Void)
                    return;
                throw new VerificationException(
                    $"Extern result evidence '{call.ResultEvidence}' is invalid for "
                    + $"'{call.Sig}' returning '{call.Type}' (function '{functionName}').");
            default:
                throw new VerificationException(
                    $"Unknown extern result evidence '{call.ResultEvidence}' "
                    + $"(function '{functionName}').");
        }
    }

    /// <summary>The SDK's generic component-query wrapper does not merely call
    /// GetHeapVariable&lt;Component&gt;: it branches on the receiver strongbox's declared CLR type.
    /// Lowering therefore normalizes every explicit receiver through .transform. Pin that execution
    /// contract here so a future removal of the normalization is rejected before UASM emission.</summary>
    static bool RequiresTransformStrongbox(UdonAbiKey key, int stackIndex)
    {
        if (stackIndex != 0
            || (key.ResultType != "T" && key.ResultType != "TArray"))
            return false;
        switch (key.Member)
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
}
