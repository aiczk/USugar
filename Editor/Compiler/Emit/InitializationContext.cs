using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Owns field initializer queues and field-change callback registrations for one class emission.
/// </summary>
public sealed class InitializationContext
{
    public readonly List<(string FieldName, IOperation InitOp, ITypeSymbol FieldType)> FieldInitOps = new();

    // static readonly initializers - same shape as FieldInitOps, kept separate so UasmEmitter can
    // base-first reorder the static TIER independently, then splice it in front of FieldInitOps
    // (static tier runs before instance tier, mirroring C#'s initializer order).
    public readonly List<(string FieldName, IOperation InitOp, ITypeSymbol FieldType)> StaticFieldInitOps = new();

    public readonly Dictionary<string, string> FieldChangeCallbacks = new();
}
