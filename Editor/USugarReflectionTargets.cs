using System;
using System.Reflection;
using UdonSharp;
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
    internal static readonly MethodInfo CompileSyncMethod =
        CompilerType?.GetMethod("CompileSync", BindingFlags.Public | BindingFlags.Static);
    internal static readonly Type CompileOptionsType =
        CompilerType?.Assembly.GetType("UdonSharp.Compiler.UdonSharpCompileOptions");

    // CompilerUdonInterface (UASM assembly)
    internal static readonly Type UdonInterfaceType =
        UdonSharpAsm.GetType("UdonSharp.Compiler.Udon.CompilerUdonInterface");
    internal static readonly MethodInfo AssembleMethod =
        UdonInterfaceType?.GetMethod("Assemble", BindingFlags.Public | BindingFlags.Static);

    // Serialization cache types (for InvalidateSerializationCaches)
    internal static readonly Type VarStorageType =
        UdonSharpAsm.GetType("UdonSharp.Serialization.UdonVariableStorageInterface");
    internal static readonly Type FormatterEmitterType =
        UdonSharpAsm.GetType("UdonSharp.Serialization.UdonSharpBehaviourFormatterEmitter");
    internal static readonly FieldInfo FormattersField =
        FormatterEmitterType?.GetField("_formatters", BindingFlags.NonPublic | BindingFlags.Static);
    internal static readonly Type EmittedFormatterOpenType =
        FormatterEmitterType?.GetNestedType("EmittedFormatter`1", BindingFlags.NonPublic);

    internal static void Validate()
    {
        var fields = typeof(USugarReflectionTargets).GetFields(BindingFlags.Static | BindingFlags.NonPublic);
        foreach (var f in fields)
        {
            if (f.GetValue(null) == null)
                USugarLog.Warn($"Reflection target not found: {f.Name}");
        }
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
