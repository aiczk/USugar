using System;
using System.Collections.Generic;
using System.Reflection;
using UdonSharp;
using UdonSharp.Serialization;
using UnityEngine;

/// <summary>
/// Cached reflection targets for UdonSharp internal types (resolved once per domain reload).
/// Shared across all USugar compiler modules.
/// </summary>
static class USugarReflectionTargets
{
    // UdonSharp assembly (common base for all internal type lookups)
    internal static readonly Assembly UdonSharpAsm = typeof(UdonSharpProgramAsset).Assembly;

    // UdonSharpEditorCache (diagnostic push, UASM cache read/write)
    internal static readonly Type EditorCacheType = UdonSharpAsm.GetType("UdonSharp.UdonSharpEditorCache");
    internal static readonly PropertyInfo EditorCacheInstanceProp =
        EditorCacheType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
    internal static readonly PropertyInfo LastCompileDiagnosticsProp =
        EditorCacheType?.GetProperty("LastCompileDiagnostics", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    internal static readonly MethodInfo SetUasmStr =
        EditorCacheType?.GetMethod("SetUASMStr", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    internal static readonly MethodInfo GetUasmStr =
        EditorCacheType?.GetMethod("GetUASMStr", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    // CompileDiagnostic nested type + fields
    internal static readonly Type CompileDiagnosticType =
        EditorCacheType?.GetNestedType("CompileDiagnostic", BindingFlags.Public | BindingFlags.NonPublic);
    internal static readonly FieldInfo DiagSeverity = CompileDiagnosticType?.GetField("severity");
    internal static readonly FieldInfo DiagFile = CompileDiagnosticType?.GetField("file");
    internal static readonly FieldInfo DiagLine = CompileDiagnosticType?.GetField("line");
    internal static readonly FieldInfo DiagCharacter = CompileDiagnosticType?.GetField("character");
    internal static readonly FieldInfo DiagMessage = CompileDiagnosticType?.GetField("message");

    // UdonSharpCompilerV1 (Harmony patch targets + CompileSync invocation)
    internal static readonly Type CompilerType =
        Type.GetType("UdonSharp.Compiler.UdonSharpCompilerV1, UdonSharp.Editor");
    internal static readonly MethodInfo CompileAllProgramsMethod =
        typeof(UdonSharpProgramAsset).GetMethod(
            "CompileAllCsPrograms", BindingFlags.Public | BindingFlags.Static);
    internal static readonly MethodInfo CompileMethod =
        CompilerType?.GetMethod("Compile", BindingFlags.Public | BindingFlags.Static);
    internal static readonly MethodInfo CompileSyncMethod =
        CompilerType?.GetMethod("CompileSync", BindingFlags.Public | BindingFlags.Static);
    internal static readonly MethodInfo AnyScriptHasErrorMethod =
        typeof(UdonSharpProgramAsset).GetMethod(
            "AnyUdonSharpScriptHasError", BindingFlags.Public | BindingFlags.Static);
    internal static readonly Type CompileOptionsType =
        CompilerType?.Assembly.GetType("UdonSharp.Compiler.UdonSharpCompileOptions");

    // CompilerUdonInterface (UASM assembly)
    internal static readonly Type UdonInterfaceType =
        UdonSharpAsm.GetType("UdonSharp.Compiler.Udon.CompilerUdonInterface");
    internal static readonly MethodInfo AssembleMethod =
        UdonInterfaceType?.GetMethod("Assemble", BindingFlags.Public | BindingFlags.Static);
    internal static readonly MethodInfo IsExternTypeMethod =
        UdonInterfaceType?.GetMethod("IsExternType", BindingFlags.Public | BindingFlags.Static);

    // Proxy storage internals. Missing either behaviour field makes the storage prefix unable to
    // distinguish a USugar object[] ABI field from stock UdonSharp storage.
    internal static readonly FieldInfo HeapStorageBehaviour =
        typeof(UdonHeapStorageInterface).GetField(
            "behaviour", BindingFlags.Instance | BindingFlags.NonPublic);
    internal static readonly FieldInfo VariableStorageBehaviour =
        typeof(UdonVariableStorageInterface).GetField(
            "udonBehaviour", BindingFlags.Instance | BindingFlags.NonPublic);
    internal static readonly MethodInfo HeapGetElementStorageMethod =
        typeof(UdonHeapStorageInterface).GetMethod(
            nameof(UdonHeapStorageInterface.GetElementStorage),
            BindingFlags.Public | BindingFlags.Instance);
    internal static readonly MethodInfo VariableGetElementStorageMethod =
        typeof(UdonVariableStorageInterface).GetMethod(
            nameof(UdonVariableStorageInterface.GetElementStorage),
            BindingFlags.Public | BindingFlags.Instance);

    // Bindings whose ABSENCE breaks correctness — either no codegen applies or USugar object[] ABI fields
    // cannot be isolated from proxy serialization.
    // A null critical binding fails LOUD at domain load so an SDK rename is visible immediately, instead of silently
    // no-op'ing the override or stranding stale serialization. The rest (diagnostics / UASM display) merely degrade
    // the inspector and stay at Warn.
    static readonly HashSet<string> CriticalBindings = new()
    {
        nameof(CompilerType), nameof(CompileAllProgramsMethod), nameof(CompileMethod),
        nameof(CompileSyncMethod), nameof(AnyScriptHasErrorMethod),
        nameof(AssembleMethod), nameof(UdonInterfaceType), nameof(EditorCacheType),
        nameof(IsExternTypeMethod), nameof(HeapStorageBehaviour), nameof(VariableStorageBehaviour),
        nameof(HeapGetElementStorageMethod), nameof(VariableGetElementStorageMethod),
    };

    internal static bool Validate()
    {
        var valid = true;
        var fields = typeof(USugarReflectionTargets).GetFields(BindingFlags.Static | BindingFlags.NonPublic);
        foreach (var f in fields)
        {
            if (f.GetValue(null) != null) continue;
            if (CriticalBindings.Contains(f.Name))
            {
                USugarLog.Error($"CRITICAL reflection target not found: {f.Name} — the UdonSharp SDK may have changed. USugar codegen will not apply correctly (or serialized data may be stale); check USugarReflectionTargets.");
                valid = false;
            }
            else
                USugarLog.Warn($"Reflection target not found: {f.Name} (a display/diagnostic feature may be degraded)");
        }
        return valid;
    }

    internal static object GetEditorCacheInstance()
        => EditorCacheInstanceProp?.GetValue(null);
}

/// <summary>
/// Centralized logging for USugar (matches UdonSharp style).
/// </summary>
static class USugarLog
{
    const string Tag = "[<color=#4ec9b0>USugar</color>]";       // teal
    const string TagWarn = "[<color=#FF00FF>USugar</color>]";    // magenta (same as U#)

    internal static void Info(object msg) => Debug.Log($"{Tag} {msg}");
    internal static void Warn(object msg) => Debug.LogWarning($"{TagWarn} {msg}");
    internal static void Error(object msg) => Debug.LogError($"{TagWarn} {msg}");
}
