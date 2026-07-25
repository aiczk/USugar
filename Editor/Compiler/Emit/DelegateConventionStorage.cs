using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>Declares the complete signature-keyed delegate convention surface.</summary>
public sealed class DelegateConventionStorage
{
    readonly LoweringState _context;
    StorageContext Storage => _context.Storage;

    public DelegateConventionStorage(LoweringState context) => _context = context;

    public StorageType? Declare(string signaturePart, IMethodSymbol invoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap)
        => Declare(signaturePart, invoke, typeParameterMap, out _);

    public StorageType? Declare(string signaturePart, IMethodSymbol invoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap,
        out StorageType[] argumentTypes)
    {
        // Every program that exposes a bridge for this signature accepts the environment field,
        // including capture-free targets. Cross-program dispatch therefore never depends on how
        // SetProgramVariable handles an unknown symbol.
        Storage.TryDeclareVar(DelegateAbi.ConvEnvName(signaturePart), new StorageType(EnvEmit.EnvType));

        argumentTypes = new StorageType[invoke.Parameters.Length];
        for (int i = 0; i < invoke.Parameters.Length; i++)
        {
            argumentTypes[i] = _context.ResolveStorageType(invoke.Parameters[i].Type, typeParameterMap);
            Storage.TryDeclareVar(DelegateAbi.ConvArgName(signaturePart, i), argumentTypes[i]);
        }

        if (invoke.ReturnsVoid) return null;
        var returnType = _context.ResolveStorageType(invoke.ReturnType, typeParameterMap);
        Storage.TryDeclareVar(DelegateAbi.ConvRetName(signaturePart), returnType);
        return returnType;
    }
}
