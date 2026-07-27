using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharp.Serialization;

/// <summary>
/// Manages Harmony patches that redirect UdonSharp compilation to USugar.
/// </summary>
static class USugarHarmonyPatcher
{
    const string HarmonyId = "com.usugar.compiler-override";

    static Harmony _harmony;

    internal static void Initialize()
    {
        if (USugarCompiler.OverrideEnabled)
            ApplyPatches();
        else
            USugarReflectionTargets.Validate();
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    internal static void ApplyPatches()
    {
        if (_harmony != null) return;
        if (!USugarReflectionTargets.Validate())
        {
            USugarCompiler.OverrideEnabled = false;
            USugarCompilationOrchestrator.LastCompileHadErrors = true;
            USugarLog.Error(
                "Compiler override disabled because required UdonSharp reflection targets are unavailable.");
            return;
        }

        var compilerType = USugarReflectionTargets.CompilerType;
        if (compilerType == null)
        {
            USugarLog.Warn("UdonSharpCompilerV1 not found — patches skipped");
            return;
        }

        var harmony = new Harmony(HarmonyId);
        var redirectAll = new HarmonyMethod(
            typeof(USugarHarmonyPatcher),
            nameof(Prefix_RedirectAll));
        var redirectCompile = new HarmonyMethod(
            typeof(USugarHarmonyPatcher),
            nameof(Prefix_RedirectCompile));
        var redirectSync = new HarmonyMethod(
            typeof(USugarHarmonyPatcher),
            nameof(Prefix_RedirectSync));

        try
        {
            PatchRequired(harmony, USugarReflectionTargets.CompileAllProgramsMethod, redirectAll,
                "UdonSharpProgramAsset.CompileAllCsPrograms");
            PatchRequired(harmony, USugarReflectionTargets.CompileMethod, redirectCompile,
                $"{compilerType.Name}.Compile");
            PatchRequired(harmony, USugarReflectionTargets.CompileSyncMethod, redirectSync,
                $"{compilerType.Name}.CompileSync");
            PatchRequired(harmony, USugarReflectionTargets.AnyScriptHasErrorMethod,
                new HarmonyMethod(typeof(USugarHarmonyPatcher), nameof(Prefix_NoError)),
                "UdonSharpProgramAsset.AnyUdonSharpScriptHasError");

            PatchRequired(harmony,
                USugarReflectionTargets.HeapGetElementStorageMethod,
                new HarmonyMethod(typeof(USugarHarmonyPatcher), nameof(Prefix_ProxyStorage)),
                "UdonHeapStorageInterface.GetElementStorage");
            PatchRequired(harmony,
                USugarReflectionTargets.VariableGetElementStorageMethod,
                new HarmonyMethod(typeof(USugarHarmonyPatcher),
                    nameof(Prefix_VariableProxyStorage)),
                "UdonVariableStorageInterface.GetElementStorage");

            _harmony = harmony;
            USugarCompilationOrchestrator.RequestCompile();
            USugarLog.Info("Compiler override applied");
        }
        catch (System.Exception ex)
        {
            USugarCompiler.OverrideEnabled = false;
            USugarCompilationOrchestrator.LastCompileHadErrors = true;
            try
            {
                harmony.UnpatchAll(HarmonyId);
            }
            catch (System.Exception cleanupEx)
            {
                USugarLog.Error($"Failed to roll back partial Harmony patches: {cleanupEx}");
            }
            USugarLog.Error($"Compiler override disabled after an atomic patch failure: {ex}");
        }
    }

    static void PatchRequired(
        Harmony harmony, MethodBase original, HarmonyMethod prefix, string displayName)
    {
        if (original == null || harmony.Patch(original, prefix: prefix) == null)
            throw new System.InvalidOperationException(
                $"Required Harmony patch failed: {displayName}");
    }

    internal static void RemovePatches()
    {
        _harmony?.UnpatchAll(HarmonyId);
        _harmony = null;
        USugarCompilationOrchestrator.MarkCompileUnknown();
        USugarLog.Info("Compiler override removed");
    }

    static bool Prefix_RedirectAll(
        bool forceCompile,
        bool editorBuild)
    {
        if (!USugarCompiler.OverrideEnabled) return true;
        USugarCompilationOrchestrator.RequestCompile(
            editorBuild, forceCompile);
        return false;
    }

    static bool Prefix_RedirectCompile(
        UdonSharpCompileOptions options)
    {
        if (!USugarCompiler.OverrideEnabled) return true;
        USugarCompilationOrchestrator.RequestCompile(
            options?.IsEditorBuild ?? true);
        return false;
    }

    static bool Prefix_RedirectSync(
        UdonSharpCompileOptions options)
    {
        if (!USugarCompiler.OverrideEnabled) return true;
        USugarCompilationOrchestrator.CompileSynchronously(
            editorBuild: options?.IsEditorBuild ?? true);
        return false;
    }

    static bool Prefix_NoError(ref bool __result)
    {
        if (!USugarCompiler.OverrideEnabled) return true;
        __result = USugarCompilationOrchestrator.Health != USugarCompileHealth.Clean;
        return false;
    }

    static bool Prefix_ProxyStorage(
        UdonHeapStorageInterface __instance,
        string elementKey,
        ref IValueStorage __result)
    {
        if (!USugarProxySerialization.TryCreateStorage(__instance, elementKey, out var storage))
            return true;
        __result = storage;
        return false;
    }

    static bool Prefix_VariableProxyStorage(
        UdonVariableStorageInterface __instance,
        string elementKey,
        ref IValueStorage __result)
    {
        if (!USugarProxySerialization.TryCreateStorage(__instance, elementKey, out var storage))
            return true;
        __result = storage;
        return false;
    }

    static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode && USugarCompiler.OverrideEnabled)
        {
            USugarCompilationOrchestrator.CompileSynchronously(
                editorBuild: true);
            if (USugarCompilationOrchestrator.Health != USugarCompileHealth.Clean)
            {
                USugarLog.Error(
                    "Play Mode entry cancelled because the USugar compile is not clean.");
                EditorApplication.isPlaying = false;
            }
        }
    }
}
