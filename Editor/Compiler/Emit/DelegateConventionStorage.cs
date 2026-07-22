using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>Declares signature-keyed delegate convention argument and return fields.</summary>
public sealed class DelegateConventionStorage
{
    readonly StorageContext _storage;

    public DelegateConventionStorage(StorageContext storage) => _storage = storage;

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
            argumentTypes[i] = ExternResolver.GetStorageType(
                new RuntimeType(invoke.Parameters[i].Type), typeParameterMap);
            _storage.TryDeclareVar(DelegateAbi.ConvArgName(signaturePart, i), argumentTypes[i]);
        }

        if (invoke.ReturnsVoid) return null;
        var returnType = ExternResolver.GetStorageType(new RuntimeType(invoke.ReturnType), typeParameterMap);
        _storage.TryDeclareVar(DelegateAbi.ConvRetName(signaturePart), returnType);
        return returnType;
    }
}
