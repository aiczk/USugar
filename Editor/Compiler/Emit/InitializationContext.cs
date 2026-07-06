using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Owns field initializer queues and field-change callback registrations for one class emission.
/// </summary>
public sealed class InitializationContext
{
    public readonly List<(string FieldName, IOperation InitOp, ITypeSymbol FieldType)> FieldInitOps = new();

    public readonly List<(string FieldName, IOperation InitOp, ITypeSymbol FieldType)> StaticFieldInitOps = new();

    public readonly Dictionary<string, string> FieldChangeCallbacks = new();
}
