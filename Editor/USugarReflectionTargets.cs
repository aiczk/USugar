using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharp.Serialization;
using UnityEngine;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// The minimal UdonSharp reflection surface required by the compiler override.
/// All private SDK access is quarantined behind typed operations here.
/// </summary>
static class USugarReflectionTargets
{
    static readonly Assembly UdonSharpAsm =
        typeof(UdonSharpProgramAsset).Assembly;

    // Stock compile lifecycle.
    static readonly Type EditorCacheType =
        UdonSharpAsm.GetType("UdonSharp.UdonSharpEditorCache");
    static readonly PropertyInfo EditorCacheInstance =
        EditorCacheType?.GetProperty(
            "Instance",
            BindingFlags.Public | BindingFlags.NonPublic
                                | BindingFlags.Static);
    static readonly MethodInfo RehashAllScripts =
        FindMethod(
            EditorCacheType,
            "RehashAllScripts",
            BindingFlags.Public | BindingFlags.NonPublic
                                | BindingFlags.Instance);
    static readonly PropertyInfo LastBuildType =
        EditorCacheType?.GetProperty(
            "LastBuildType",
            BindingFlags.Public | BindingFlags.NonPublic
                                | BindingFlags.Instance);
    static readonly PropertyInfo LastCompileDiagnostics =
        EditorCacheType?.GetProperty(
            "LastCompileDiagnostics",
            BindingFlags.Public | BindingFlags.NonPublic
                                | BindingFlags.Instance);
    static readonly Type CompileDiagnosticType =
        LastCompileDiagnostics?.PropertyType.GetElementType();
    static readonly FieldInfo DiagnosticSeverity =
        CompileDiagnosticType?.GetField("severity");
    static readonly FieldInfo DiagnosticFile =
        CompileDiagnosticType?.GetField("file");
    static readonly FieldInfo DiagnosticLine =
        CompileDiagnosticType?.GetField("line");
    static readonly FieldInfo DiagnosticCharacter =
        CompileDiagnosticType?.GetField("character");
    static readonly FieldInfo DiagnosticMessage =
        CompileDiagnosticType?.GetField("message");
    static readonly MethodInfo SetUasm =
        FindMethod(
            EditorCacheType,
            "SetUASMStr",
            BindingFlags.Public | BindingFlags.NonPublic
                                | BindingFlags.Instance,
            typeof(UdonSharpProgramAsset), typeof(string));
    static readonly MethodInfo RunPostBuildSceneFixup =
        FindMethod(
            UdonSharpAsm.GetType(
                "UdonSharpEditor.UdonSharpEditorManager"),
            "RunPostBuildSceneFixup",
            BindingFlags.Public | BindingFlags.NonPublic
                                | BindingFlags.Static);

    // Stock source selection and Unity compile-error gate.
    static readonly MethodInfo FilterBlacklistedPaths =
        FindMethod(
            UdonSharpAsm.GetType(
                "UdonSharpEditor.UdonSharpSettings"),
            "FilterBlacklistedPaths",
            BindingFlags.Public | BindingFlags.NonPublic
                                | BindingFlags.Static,
            typeof(IEnumerable<string>));
    static readonly MethodInfo DoesUnityProjectHaveCompileErrors =
        FindMethod(
            UdonSharpAsm.GetType("UdonSharp.UdonSharpUtils"),
            "DoesUnityProjectHaveCompileErrors",
            BindingFlags.Public | BindingFlags.NonPublic
                                | BindingFlags.Static);

    // One stock operation owns assembly, program/error state, and interact
    // detection. This replaces separate access to the assembler and fields.
    static readonly MethodInfo AssembleCsProgram =
        FindMethod(
            typeof(UdonSharpProgramAsset),
            "AssembleCsProgram",
            BindingFlags.Public | BindingFlags.NonPublic
                                | BindingFlags.Instance,
            typeof(string), typeof(uint));

    // Harmony compiler entry points. Only WaitForCompile is non-public.
    internal static readonly MethodInfo CompileMethod =
        FindMethod(
            typeof(UdonSharpCompilerV1),
            nameof(UdonSharpCompilerV1.Compile),
            BindingFlags.Public | BindingFlags.Static,
            typeof(UdonSharpCompileOptions));
    internal static readonly MethodInfo CompileSyncMethod =
        FindMethod(
            typeof(UdonSharpCompilerV1),
            nameof(UdonSharpCompilerV1.CompileSync),
            BindingFlags.Public | BindingFlags.Static,
            typeof(UdonSharpCompileOptions));
    internal static readonly MethodInfo WaitForCompileMethod =
        FindMethod(
            typeof(UdonSharpCompilerV1),
            "WaitForCompile",
            BindingFlags.Public | BindingFlags.NonPublic
                                | BindingFlags.Static);
    internal static readonly MethodInfo AnyScriptHasErrorMethod =
        FindMethod(
            typeof(UdonSharpProgramAsset),
            nameof(UdonSharpProgramAsset.AnyUdonSharpScriptHasError),
            BindingFlags.Public | BindingFlags.Static);

    // Proxy storage internals. These fields associate a stock storage object
    // with the UdonBehaviour whose bundle ABI must be protected.
    internal static readonly FieldInfo HeapStorageBehaviour =
        typeof(UdonHeapStorageInterface).GetField(
            "behaviour",
            BindingFlags.Instance | BindingFlags.NonPublic);
    internal static readonly FieldInfo VariableStorageBehaviour =
        typeof(UdonVariableStorageInterface).GetField(
            "udonBehaviour",
            BindingFlags.Instance | BindingFlags.NonPublic);
    internal static readonly MethodInfo HeapGetElementStorageMethod =
        FindMethod(
            typeof(UdonHeapStorageInterface),
            nameof(UdonHeapStorageInterface.GetElementStorage),
            BindingFlags.Public | BindingFlags.Instance,
            typeof(string));
    internal static readonly MethodInfo VariableGetElementStorageMethod =
        FindMethod(
            typeof(UdonVariableStorageInterface),
            nameof(UdonVariableStorageInterface.GetElementStorage),
            BindingFlags.Public | BindingFlags.Instance,
            typeof(string));

    // This is the complete SDK reflection surface required by the override.
    // Explicit enumeration avoids reflection over this bridge itself and
    // prevents helper/container types from producing duplicate failures.
    static readonly (string Name, MemberInfo Binding)[] RequiredBindings =
    {
        (nameof(EditorCacheInstance), EditorCacheInstance),
        (nameof(RehashAllScripts), RehashAllScripts),
        (nameof(LastBuildType), LastBuildType),
        (nameof(LastCompileDiagnostics), LastCompileDiagnostics),
        (nameof(DiagnosticSeverity), DiagnosticSeverity),
        (nameof(DiagnosticFile), DiagnosticFile),
        (nameof(DiagnosticLine), DiagnosticLine),
        (nameof(DiagnosticCharacter), DiagnosticCharacter),
        (nameof(DiagnosticMessage), DiagnosticMessage),
        (nameof(SetUasm), SetUasm),
        (nameof(RunPostBuildSceneFixup), RunPostBuildSceneFixup),
        (nameof(FilterBlacklistedPaths), FilterBlacklistedPaths),
        (nameof(DoesUnityProjectHaveCompileErrors),
            DoesUnityProjectHaveCompileErrors),
        (nameof(AssembleCsProgram), AssembleCsProgram),
        (nameof(CompileMethod), CompileMethod),
        (nameof(CompileSyncMethod), CompileSyncMethod),
        (nameof(WaitForCompileMethod), WaitForCompileMethod),
        (nameof(AnyScriptHasErrorMethod), AnyScriptHasErrorMethod),
        (nameof(HeapStorageBehaviour), HeapStorageBehaviour),
        (nameof(VariableStorageBehaviour), VariableStorageBehaviour),
        (nameof(HeapGetElementStorageMethod),
            HeapGetElementStorageMethod),
        (nameof(VariableGetElementStorageMethod),
            VariableGetElementStorageMethod),
    };

    static MethodInfo FindMethod(
        Type type, string name, BindingFlags flags,
        params Type[] parameterTypes)
    {
        if (type == null) return null;
        return type.GetMethod(
            name, flags, binder: null,
            types: parameterTypes ?? Type.EmptyTypes,
            modifiers: null);
    }

    internal static bool Validate()
    {
        var valid = true;
        foreach (var required in RequiredBindings)
        {
            if (required.Binding != null) continue;
            USugarLog.Error(
                $"CRITICAL reflection target not found: {required.Name} "
                + "- the UdonSharp SDK may have changed. Compiler override "
                + "cannot be applied safely.");
            valid = false;
        }
        return valid;
    }

    internal static bool HasUnityProjectCompileErrors()
        => DoesUnityProjectHaveCompileErrors.Invoke(
            null, null) is true;

    internal static IEnumerable<string> FilterSourcePaths(
        IEnumerable<string> sourcePaths)
        => FilterBlacklistedPaths.Invoke(
               null, new object[] { sourcePaths })
           as IEnumerable<string>
           ?? throw new InvalidOperationException(
               "UdonSharpSettings.FilterBlacklistedPaths returned no "
               + "source set.");

    internal static IUdonProgram AssembleProgram(
        UdonSharpProgramAsset asset, string uasm, uint heapSize)
    {
        if (asset == null) throw new ArgumentNullException(nameof(asset));
        try
        {
            AssembleCsProgram.Invoke(
                asset, new object[] { uasm, heapSize });
        }
        catch (TargetInvocationException ex)
            when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(
                ex.InnerException).Throw();
        }
        return asset.GetRealProgram()
               ?? throw new InvalidOperationException(
                   "UdonSharpProgramAsset.AssembleCsProgram returned no "
                   + "program.");
    }

    internal static void CompleteCompileLifecycle(bool editorBuild)
    {
        var cache = GetEditorCache();
        RehashAllScripts.Invoke(cache, null);
        RunPostBuildSceneFixup.Invoke(null, null);
        LastBuildType.SetValue(
            cache,
            Enum.Parse(
                LastBuildType.PropertyType,
                editorBuild ? "Editor" : "Client"));
    }

    internal static void PublishDiagnostics(
        IReadOnlyList<(string file, int line, int character,
            string message, string severity)> diagnostics)
    {
        if (diagnostics == null)
            throw new ArgumentNullException(nameof(diagnostics));
        var values = Array.CreateInstance(
            CompileDiagnosticType, diagnostics.Count);
        for (var index = 0; index < diagnostics.Count; index++)
        {
            var source = diagnostics[index];
            var value = Activator.CreateInstance(CompileDiagnosticType);
            var severity = string.Equals(
                source.severity, "Warning",
                StringComparison.Ordinal)
                ? "Warning"
                : "Error";
            DiagnosticSeverity.SetValue(
                value,
                Enum.Parse(DiagnosticSeverity.FieldType, severity));
            DiagnosticFile.SetValue(value, source.file ?? "");
            DiagnosticLine.SetValue(
                value, source.line > 0 ? source.line - 1 : 0);
            DiagnosticCharacter.SetValue(
                value,
                source.character > 0 ? source.character - 1 : 0);
            DiagnosticMessage.SetValue(
                value, source.message ?? "");
            values.SetValue(value, index);
        }
        LastCompileDiagnostics.SetValue(GetEditorCache(), values);
    }

    internal static void StoreUasm(
        UdonSharpProgramAsset asset, string uasm)
    {
        if (asset == null) throw new ArgumentNullException(nameof(asset));
        SetUasm.Invoke(
            GetEditorCache(),
            new object[] { asset, uasm ?? "" });
    }

    static object GetEditorCache()
        => EditorCacheInstance.GetValue(null)
           ?? throw new InvalidOperationException(
               "UdonSharpEditorCache.Instance is unavailable.");
}

/// <summary>
/// Centralized logging for USugar (matches UdonSharp style).
/// </summary>
static class USugarLog
{
    const string Tag = "[<color=#4ec9b0>USugar</color>]";
    const string TagWarn = "[<color=#FF00FF>USugar</color>]";

    internal static void Info(object msg) =>
        Debug.Log($"{Tag} {msg}");
    internal static void Warn(object msg) =>
        Debug.LogWarning($"{TagWarn} {msg}");
    internal static void Error(object msg) =>
        Debug.LogError($"{TagWarn} {msg}");
}
