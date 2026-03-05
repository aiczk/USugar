using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UdonSharp;

/// <summary>
/// Manages Harmony patches that redirect UdonSharp compilation to USugar.
/// </summary>
static class USugarHarmonyPatcher
{
    const string HarmonyId = "com.usugar.compiler-override";

    static Harmony _harmony;

    internal static void Initialize()
    {
        USugarReflectionTargets.Validate();
        if (USugarCompiler.OverrideEnabled)
            ApplyPatches();
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    internal static void ApplyPatches()
    {
        if (_harmony != null) return;

        var compilerType = USugarReflectionTargets.CompilerType;
        if (compilerType == null)
        {
            USugarLog.Warn("UdonSharpCompilerV1 not found — patches skipped");
            return;
        }

        _harmony = new Harmony(HarmonyId);
        var redirect = new HarmonyMethod(typeof(USugarHarmonyPatcher), nameof(Prefix_Redirect));
        var redirectSync = new HarmonyMethod(typeof(USugarHarmonyPatcher), nameof(Prefix_RedirectSync));

        // Redirect CompileAllCsPrograms
        var compileAll = typeof(UdonSharpProgramAsset)
            .GetMethod("CompileAllCsPrograms", BindingFlags.Public | BindingFlags.Static);
        if (compileAll != null)
        {
            if (_harmony.Patch(compileAll, prefix: redirect) == null)
                USugarLog.Warn("Harmony patch failed: UdonSharpProgramAsset.CompileAllCsPrograms");
        }

        // Redirect Compile(UdonSharpCompileOptions) and CompileSync(UdonSharpCompileOptions)
        var compile = compilerType.GetMethod("Compile", BindingFlags.Public | BindingFlags.Static);
        if (compile != null)
        {
            if (_harmony.Patch(compile, prefix: redirect) == null)
                USugarLog.Warn($"Harmony patch failed: {compilerType.Name}.Compile");
        }

        if (USugarReflectionTargets.CompileSyncMethod != null)
        {
            if (_harmony.Patch(USugarReflectionTargets.CompileSyncMethod, prefix: redirectSync) == null)
                USugarLog.Warn($"Harmony patch failed: {compilerType.Name}.CompileSync");
        }

        // Override AnyUdonSharpScriptHasError
        var errorCheck = typeof(UdonSharpProgramAsset)
            .GetMethod("AnyUdonSharpScriptHasError", BindingFlags.Public | BindingFlags.Static);
        if (errorCheck != null)
        {
            if (_harmony.Patch(errorCheck, prefix: new HarmonyMethod(typeof(USugarHarmonyPatcher), nameof(Prefix_NoError))) == null)
                USugarLog.Warn("Harmony patch failed: UdonSharpProgramAsset.AnyUdonSharpScriptHasError");
        }

        USugarLog.Info("Compiler override applied");
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

    static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode && USugarCompiler.OverrideEnabled)
            USugarCompilationOrchestrator.CompileInternal(applyToAssets: true);
    }
}
