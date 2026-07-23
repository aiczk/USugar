using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UdonSharp;
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
        var redirect = new HarmonyMethod(typeof(USugarHarmonyPatcher), nameof(Prefix_Redirect));
        var redirectSync = new HarmonyMethod(typeof(USugarHarmonyPatcher), nameof(Prefix_RedirectSync));

        try
        {
            // Redirect CompileAllCsPrograms
            var compileAll = typeof(UdonSharpProgramAsset)
                .GetMethod("CompileAllCsPrograms", BindingFlags.Public | BindingFlags.Static);
            if (compileAll != null)
            {
                if (harmony.Patch(compileAll, prefix: redirect) == null)
                    USugarLog.Warn("Harmony patch failed: UdonSharpProgramAsset.CompileAllCsPrograms");
            }

            // Redirect Compile(UdonSharpCompileOptions) and CompileSync(UdonSharpCompileOptions)
            var compile = compilerType.GetMethod("Compile", BindingFlags.Public | BindingFlags.Static);
            if (compile != null)
            {
                if (harmony.Patch(compile, prefix: redirect) == null)
                    USugarLog.Warn($"Harmony patch failed: {compilerType.Name}.Compile");
            }

            if (USugarReflectionTargets.CompileSyncMethod != null)
            {
                if (harmony.Patch(USugarReflectionTargets.CompileSyncMethod, prefix: redirectSync) == null)
                    USugarLog.Warn($"Harmony patch failed: {compilerType.Name}.CompileSync");
            }

            // Override AnyUdonSharpScriptHasError
            var errorCheck = typeof(UdonSharpProgramAsset)
                .GetMethod("AnyUdonSharpScriptHasError", BindingFlags.Public | BindingFlags.Static);
            if (errorCheck != null)
            {
                if (harmony.Patch(errorCheck, prefix: new HarmonyMethod(
                        typeof(USugarHarmonyPatcher), nameof(Prefix_NoError))) == null)
                    USugarLog.Warn("Harmony patch failed: UdonSharpProgramAsset.AnyUdonSharpScriptHasError");
            }

            var heapStoragePatched = harmony.Patch(
                USugarReflectionTargets.HeapGetElementStorageMethod,
                prefix: new HarmonyMethod(typeof(USugarHarmonyPatcher), nameof(Prefix_ProxyStorage))) != null;
            var variableStoragePatched = harmony.Patch(
                USugarReflectionTargets.VariableGetElementStorageMethod,
                prefix: new HarmonyMethod(typeof(USugarHarmonyPatcher),
                    nameof(Prefix_VariableProxyStorage))) != null;
            if (!heapStoragePatched || !variableStoragePatched)
                throw new System.InvalidOperationException(
                    "The proxy-storage isolation patches could not be applied.");

            _harmony = harmony;
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

    internal static void RemovePatches()
    {
        _harmony?.UnpatchAll(HarmonyId);
        _harmony = null;
        USugarLog.Info("Compiler override removed");
    }

    static bool Prefix_Redirect()
    {
        if (!USugarCompiler.OverrideEnabled) return true;
        USugarCompilationOrchestrator.RequestCompile();
        return false;
    }

    static bool Prefix_RedirectSync()
    {
        if (!USugarCompiler.OverrideEnabled) return true;
        USugarCompilationOrchestrator.CompileInternal(applyToAssets: true);
        return false;
    }

    static bool Prefix_NoError(ref bool __result)
    {
        if (!USugarCompiler.OverrideEnabled) return true;
        __result = USugarCompilationOrchestrator.LastCompileHadErrors;
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
            USugarCompilationOrchestrator.CompileInternal(applyToAssets: true);
    }
}
