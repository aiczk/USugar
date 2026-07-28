using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Centralized semantic type classification used by emit-time policy. This intentionally sits above
/// raw Udon type names: several ABI values share SystemObjectArray at runtime but have different
/// compiler semantics (class bundle, aggregate bundle, delegate bundle, env record).
/// </summary>
public readonly struct TypeClassifierContext
{
    public readonly IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap;

    public TypeClassifierContext(IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
        => TypeParamMap = typeParamMap;
}

public enum RuntimeBundleKind
{
    None,
    Class,
    Aggregate,
    Delegate,
}

[System.Flags]
public enum TransportCapabilities
{
    None = 0,
    TypedProgramChannel = 1 << 0,
    ExternCall = 1 << 1,
}

[System.Flags]
public enum TypeContents
{
    None = 0,
    UserClass = 1 << 0,
    Delegate = 1 << 1,
    OpaqueObject = 1 << 2,
}

/// <summary>
/// Authoritative compiler-side description of a source type. Several source types erase to
/// SystemObjectArray, so storage, boundary, capture, and lowering policy consume these facts rather
/// than independently walking the symbol or rediscovering meaning from the emitted Udon type name.
/// </summary>
public readonly struct RuntimeShape
{
    public readonly RuntimeBundleKind Bundle;
    public readonly TransportCapabilities Transport;
    public readonly TypeContents Contents;

    internal RuntimeShape(RuntimeBundleKind bundle, TransportCapabilities transport,
        TypeContents contents)
    {
        Bundle = bundle;
        Transport = transport;
        Contents = contents;
    }

    public bool IsBundle => Bundle != RuntimeBundleKind.None;
    public bool ContainsUserClassPayload => Contains(TypeContents.UserClass);
    public bool ContainsDelegate => Contains(TypeContents.Delegate);
    public bool ContainsOpaqueObject => Contains(TypeContents.OpaqueObject);
    public bool Contains(TypeContents contents) => (Contents & contents) == contents;
    public bool Supports(TransportCapabilities capability) => (Transport & capability) == capability;
}

public static class TypeClassifier
{
    public static RuntimeShape ShapeOf(ITypeSymbol type, TypeClassifierContext ctx)
    {
        type = Resolve(type, ctx);
        SourceSemanticProfile.RequireSupportedType(type);
        RequireSupportedArrayRank(type);
        var contents = ContentsOf(type, ctx.TypeParamMap,
            new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));
        var bundle = IsUserClassLeaf(type)
            ? RuntimeBundleKind.Class
            : type is INamedTypeSymbol { DelegateInvokeMethod: not null }
                    ? RuntimeBundleKind.Delegate
                    : IsAggregateValueLeaf(type)
                        ? RuntimeBundleKind.Aggregate
                        : RuntimeBundleKind.None;
        var containsUserClass = (contents & TypeContents.UserClass) != 0;
        var compilerOwnedArrayCarrier =
            UsesCompilerOwnedArrayCarrier(type, ctx);
        var transport = bundle switch
        {
            RuntimeBundleKind.Class
                or RuntimeBundleKind.Aggregate
                => TransportCapabilities.TypedProgramChannel,
            RuntimeBundleKind.Delegate
                => containsUserClass
                    ? TransportCapabilities.None
                    : TransportCapabilities.TypedProgramChannel,
            _ when compilerOwnedArrayCarrier
                => containsUserClass
                    ? TransportCapabilities.None
                    : TransportCapabilities.TypedProgramChannel,
            _ => containsUserClass
                ? TransportCapabilities.ExternCall
                : TransportCapabilities.TypedProgramChannel | TransportCapabilities.ExternCall,
        };
        return new RuntimeShape(bundle, transport, contents);
    }

    internal static bool UsesCompilerOwnedArrayCarrier(
        ITypeSymbol type,
        TypeClassifierContext ctx)
    {
        type = Resolve(type, ctx);
        if (type is not IArrayTypeSymbol array)
            return false;
        var element = Resolve(array.ElementType, ctx);
        if (element is IArrayTypeSymbol
            || element is INamedTypeSymbol
                { DelegateInvokeMethod: not null }
            || IsUserClassLeaf(element)
            || IsAggregateValueLeaf(element))
            return true;
        return element is INamedTypeSymbol nullable
               && nullable.OriginalDefinition.SpecialType
                   == SpecialType.System_Nullable_T
               && UsesCompilerOwnedNullablePayload(
                   nullable.TypeArguments[0], ctx);
    }

    static bool UsesCompilerOwnedNullablePayload(
        ITypeSymbol type,
        TypeClassifierContext ctx)
    {
        type = Resolve(type, ctx);
        return IsUserClassLeaf(type)
               || IsAggregateValueLeaf(type)
               || type is INamedTypeSymbol
                   { DelegateInvokeMethod: not null };
    }

    public static bool ContainsUserClassPayload(ITypeSymbol type, TypeClassifierContext ctx)
        => ShapeOf(type, ctx).ContainsUserClassPayload;

    public static void RequireSupportedArrayRank(ITypeSymbol type)
    {
        if (type is not IArrayTypeSymbol { Rank: > 1 } array)
            return;
        throw new System.NotSupportedException(
            $"Multidimensional array '{array.ToDisplayString()}' is not supported: "
            + "Udon has no native rank-greater-than-one array representation. "
            + "Use a one-dimensional or jagged array.");
    }

    public static bool IsUserClass(ITypeSymbol type)
        => ShapeOf(type, new TypeClassifierContext(null)).Bundle == RuntimeBundleKind.Class;

    internal static bool IsUserClassLeaf(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named) return false;
        if (named.IsAnonymousType
            || !ExternResolver.IsPlainUserClass(named))
            return false;
        if (named.BaseType != null && named.BaseType.SpecialType != SpecialType.System_Object)
        {
            var baseType = named.BaseType;
            if (!ExternResolver.IsPlainUserClass(baseType)
                || !IsUserClassLeaf(baseType)) return false;
        }
        return true;
    }

    public static bool IsAggregateValue(ITypeSymbol type)
        => ShapeOf(type, new TypeClassifierContext(null)).Bundle == RuntimeBundleKind.Aggregate;

    internal static bool IsAggregateValueLeaf(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named || named.TypeKind == TypeKind.Delegate) return false;
        return named.IsTupleType || named.IsAnonymousType || IsUserStruct(named);
    }

    public static bool IsUserStruct(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Struct || type.SpecialType != SpecialType.None) return false;
        if (type.DeclaringSyntaxReferences.Length == 0) return false;
        return !ExternResolver.IsSdkNamespace(type.ContainingNamespace);
    }

    public static bool IsObjectArrayEmulated(ITypeSymbol type)
    {
        var bundle = ShapeOf(type, new TypeClassifierContext(null)).Bundle;
        return bundle is RuntimeBundleKind.Aggregate or RuntimeBundleKind.Class;
    }

    static ITypeSymbol Resolve(ITypeSymbol type, TypeClassifierContext ctx)
    {
        while (type is ITypeParameterSymbol parameter
               && ctx.TypeParamMap != null
               && ctx.TypeParamMap.TryGetValue(parameter, out var resolved))
        {
            if (SymbolEqualityComparer.Default.Equals(parameter, resolved)) break;
            type = resolved;
        }
        return type;
    }

    static TypeContents ContentsOf(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap,
        HashSet<ITypeSymbol> visited)
    {
        type = Resolve(type, new TypeClassifierContext(typeParameterMap));
        if (type == null) return TypeContents.None;

        var contents = TypeContents.None;
        if (IsUserClassLeaf(type)) contents |= TypeContents.UserClass;
        if (type.SpecialType == SpecialType.System_Object) contents |= TypeContents.OpaqueObject;
        if (type is IArrayTypeSymbol array)
            return contents | ContentsOf(array.ElementType, typeParameterMap, visited);
        if (type is not INamedTypeSymbol named || !visited.Add(named)) return contents;

        if (named.DelegateInvokeMethod is { } invoke)
        {
            contents |= TypeContents.Delegate;
            foreach (var parameter in invoke.Parameters)
                contents |= ContentsOf(parameter.Type, typeParameterMap, visited);
            contents |= ContentsOf(invoke.ReturnType, typeParameterMap, visited);
        }
        if (IsAggregateValueLeaf(named))
            foreach (var member in named.GetMembers())
                if (member is IFieldSymbol { IsStatic: false } field)
                    contents |= ContentsOf(field.Type, typeParameterMap, visited);
        foreach (var argument in named.TypeArguments)
            contents |= ContentsOf(argument, typeParameterMap, visited);
        return contents;
    }
}

/// <summary>
/// Source-language features whose CLR semantics cannot be represented by the
/// Udon execution model. This is intentionally separate from storage-shape
/// classification: a type must first belong to the supported semantic profile
/// before the compiler chooses a physical carrier for it.
/// </summary>
internal static class SourceSemanticProfile
{
    const string ModuleInitializerMetadataName =
        "System.Runtime.CompilerServices.ModuleInitializerAttribute";

    public static void RequireSupportedCompilation(
        Compilation compilation)
    {
        if (compilation == null)
            throw new ArgumentNullException(nameof(compilation));

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot()
                         .DescendantNodes()
                         .OfType<Microsoft.CodeAnalysis.CSharp.Syntax
                             .MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration)
                    is not IMethodSymbol method)
                    continue;
                if (!method.GetAttributes().Any(attribute =>
                        attribute.AttributeClass?.ToDisplayString()
                        == ModuleInitializerMetadataName))
                    continue;
                throw new NotSupportedException(
                    $"Module initializer '{method.ToDisplayString()}' is "
                    + "not supported: Udon programs have no CLR module-load "
                    + "hook. Move the initialization into an explicit "
                    + "UdonSharpBehaviour event or method.");
            }
        }
    }

    public static void RequireSupportedBehaviour(
        INamedTypeSymbol behaviour)
    {
        for (var type = behaviour;
             type != null
             && type.DeclaringSyntaxReferences.Length > 0;
             type = type.BaseType)
        {
            RequireSupportedNamedType(type);
            var constructor = type.InstanceConstructors
                .FirstOrDefault(candidate =>
                    !candidate.IsImplicitlyDeclared);
            if (constructor == null)
                continue;
            throw new NotSupportedException(
                $"Constructor '{constructor.ToDisplayString()}' is not "
                + "supported on UdonSharpBehaviour types: Udon does not "
                + "execute CLR instance constructors for behaviour "
                + "programs. Use field initializers and an explicit "
                + "Udon event such as Start instead.");
        }
    }

    public static void RequireSupportedType(ITypeSymbol type)
        => RequireSupportedType(
            type,
            new HashSet<ITypeSymbol>(
                SymbolEqualityComparer.Default));

    public static void RequireSupportedProgram(
        IEnumerable<BoundMethodBody> bodies,
        IEnumerable<IOperation> initializerRoots)
    {
        foreach (var body in bodies
                     ?? Enumerable.Empty<BoundMethodBody>())
        {
            RequireSupportedMethod(body.MethodDefinition);
            RequireSupportedOperation(
                body.AnalysisRoot,
                body.StableLocalInitializers);
        }
        foreach (var root in initializerRoots
                     ?? Enumerable.Empty<IOperation>())
            RequireSupportedOperation(root, null);
    }

    static void RequireSupportedOperation(
        IOperation root,
        IReadOnlyDictionary<ILocalSymbol, IOperation>
            stableLocalInitializers)
    {
        if (root == null)
            return;
        foreach (var operation in root.DescendantsAndSelf())
        {
            RequireSupportedType(operation.Type);
            RequireSupportedDelegateOperation(
                operation, stableLocalInitializers);
            switch (operation)
            {
                case IInvocationOperation invocation:
                    RequireSupportedMethod(
                        invocation.TargetMethod);
                    break;
                case IObjectCreationOperation creation:
                    RequireSupportedMethod(creation.Constructor);
                    break;
                case IMethodReferenceOperation reference:
                    RequireSupportedMethod(reference.Method);
                    break;
                case IFieldReferenceOperation field:
                    RequireSupportedType(
                        field.Field.ContainingType);
                    RequireSupportedType(field.Field.Type);
                    break;
                case IPropertyReferenceOperation property:
                    RequireSupportedType(
                        property.Property.ContainingType);
                    RequireSupportedType(
                        property.Property.Type);
                    break;
                case IEventReferenceOperation eventReference:
                    RequireSupportedType(
                        eventReference.Event.ContainingType);
                    RequireSupportedType(
                        eventReference.Event.Type);
                    break;
            }
        }
    }

    static void RequireSupportedDelegateOperation(
        IOperation operation,
        IReadOnlyDictionary<ILocalSymbol, IOperation>
            stableLocalInitializers)
    {
        bool IsDelegateValue(IOperation value)
            => value != null
               && ValueClassifier.ClassifyStable(
                       value,
                       new TypeClassifierContext(null),
                       captureScope: null,
                       stableLocalInitializers)
                   .Kind == ValueKind.Delegate;

        bool ContainsDelegateProtocol(IOperation value)
            => IsDelegateValue(value)
               || value?.Type != null
               && TypeClassifier.ShapeOf(
                       value.Type,
                       new TypeClassifierContext(null))
                   .ContainsDelegate;

        switch (operation)
        {
            case IBinaryOperation binary
                when binary.OperatorKind
                    is BinaryOperatorKind.Equals
                    or BinaryOperatorKind.NotEquals
                && !NullableAbi.IsNullLiteral(
                    binary.LeftOperand)
                && !NullableAbi.IsNullLiteral(
                    binary.RightOperand)
                && (IsDelegateValue(binary.LeftOperand)
                    || IsDelegateValue(
                        binary.RightOperand)):
                throw UnsupportedDelegateObservation(
                    "delegate-to-delegate equality");

            case ITupleBinaryOperation tuple
                when ContainsDelegateProtocol(
                         tuple.LeftOperand)
                     || ContainsDelegateProtocol(
                         tuple.RightOperand):
                throw UnsupportedDelegateObservation(
                    "tuple equality containing a delegate");

            case IInvocationOperation invocation
                when invocation.Instance != null
                && IsDelegateValue(invocation.Instance)
                && invocation.TargetMethod.Name
                    != "Invoke":
                throw UnsupportedDelegateObservation(
                    $"delegate member "
                    + $"'{invocation.TargetMethod.Name}'");

            case IInvocationOperation invocation
                when invocation.TargetMethod.IsStatic
                && invocation.TargetMethod.ContainingType
                    .SpecialType
                    == SpecialType.System_Object
                && invocation.TargetMethod.Name
                    is "Equals" or "ReferenceEquals"
                && invocation.Arguments.Any(argument =>
                    IsDelegateValue(argument.Value)):
                throw UnsupportedDelegateObservation(
                    $"object.{invocation.TargetMethod.Name} "
                    + "on a delegate value");

            case IPropertyReferenceOperation property
                when property.Instance != null
                && IsDelegateValue(property.Instance):
                throw UnsupportedDelegateObservation(
                    $"delegate property "
                    + $"'{property.Property.Name}'");

            case IConversionOperation conversion
                when ContainsDelegateProtocol(
                         conversion.Operand)
                && (conversion.Type == null
                    || !TypeClassifier.ShapeOf(
                            conversion.Type,
                            new TypeClassifierContext(null))
                        .ContainsDelegate)
                && !IsImmediateSameDelegateRoundTrip(
                    conversion):
                throw UnsupportedDelegateObservation(
                    $"conversion from delegate "
                    + $"'{conversion.Operand.Type}' to "
                    + $"'{conversion.Type}' carries no statically "
                    + "visible signature adapter");
        }
    }

    static bool IsDelegateType(ITypeSymbol type)
        => type is INamedTypeSymbol
            { DelegateInvokeMethod: not null };

    static bool IsImmediateSameDelegateRoundTrip(
        IConversionOperation erasure)
    {
        if (erasure.Parent
            is not IConversionOperation recovery
            || !IsDelegateType(recovery.Type))
            return false;
        var source = erasure.Operand.Type;
        return IsDelegateType(source)
               && SymbolEqualityComparer.Default.Equals(
                   source, recovery.Type);
    }

    static NotSupportedException
        UnsupportedDelegateObservation(string operation)
        => new(
            $"{operation} is not supported by USugar's callable-only "
            + "delegate model. Delegates may be created, copied, invoked, "
            + "combined, removed, and compared with null, but their CLR "
            + "object identity, invocation-list equality, reflection, "
            + "hashing, and string representation are not exposed.");

    static void RequireSupportedMethod(IMethodSymbol method)
    {
        if (method == null)
            return;
        RequireSupportedType(method.ContainingType);
        RequireSupportedType(method.ReturnType);
        foreach (var parameter in method.Parameters)
            RequireSupportedType(parameter.Type);
        foreach (var argument in method.TypeArguments)
            RequireSupportedType(argument);
    }

    static void RequireSupportedType(
        ITypeSymbol type,
        HashSet<ITypeSymbol> visited)
    {
        if (type == null || !visited.Add(type))
            return;
        switch (type)
        {
            case IArrayTypeSymbol array:
                RequireSupportedType(
                    array.ElementType, visited);
                return;
            case IPointerTypeSymbol pointer:
                RequireSupportedType(
                    pointer.PointedAtType, visited);
                return;
            case INamedTypeSymbol named:
                RequireSupportedNamedType(named);
                foreach (var argument in named.TypeArguments)
                    RequireSupportedType(argument, visited);
                return;
        }
    }

    static void RequireSupportedNamedType(
        INamedTypeSymbol type)
    {
        if (type.DeclaringSyntaxReferences.Length == 0)
            return;
        if (type.IsRecord)
            throw new NotSupportedException(
                $"Record type '{type.ToDisplayString()}' is not supported: "
                + "Udon cannot preserve the CLR record contracts for "
                + "runtime type identity, equality, hashing, cloning, and "
                + "printing. Use a class for reference semantics or a "
                + "struct with explicit equality and copy operations for "
                + "value semantics.");

        var staticConstructor = type.GetMembers()
            .OfType<IMethodSymbol>()
            .FirstOrDefault(method =>
                method.MethodKind
                    == MethodKind.StaticConstructor
                && !method.IsImplicitlyDeclared);
        if (staticConstructor != null)
            throw new NotSupportedException(
                $"Static constructor "
                + $"'{staticConstructor.ToDisplayString()}' is not "
                + "supported: Udon has no CLR type-initialization hook. "
                + "Use a storage-free static expression or initialize "
                + "state explicitly from a UdonSharpBehaviour event.");

        var destructor = type.GetMembers()
            .OfType<IMethodSymbol>()
            .FirstOrDefault(method =>
                method.MethodKind == MethodKind.Destructor);
        if (destructor != null)
            throw new NotSupportedException(
                $"Destructor '{destructor.ToDisplayString()}' is not "
                + "supported: Udon exposes no CLR finalization or "
                + "garbage-collection callback.");
    }
}
