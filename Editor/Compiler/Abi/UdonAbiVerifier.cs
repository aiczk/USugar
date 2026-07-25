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
        if (parameter.Type.TryMatch(actual, genericBindings, typeFacts, out var reason))
            return;
        throw new VerificationException(
            $"Extern '{call.Sig}' stack operand {stackIndex} ('{parameter.Name}', "
            + $"{parameter.Mode}) expects ABI type '{parameter.Type}', got '{actual}': "
            + $"{reason} (function '{functionName}').");
    }
}
