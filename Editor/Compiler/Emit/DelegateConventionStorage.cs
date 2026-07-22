using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>Declares signature-keyed delegate convention argument and return fields.</summary>
public sealed class DelegateConventionStorage
{
    readonly EmitContext _context;
    StorageContext Storage => _context.Storage;

    public DelegateConventionStorage(EmitContext context) => _context = context;

    public StorageType? Declare(string signaturePart, IMethodSymbol invoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap)
        => Declare(signaturePart, invoke, typeParameterMap, out _);

    public StorageType? Declare(string signaturePart, IMethodSymbol invoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap,
        out StorageType[] argumentTypes)
    {
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
