using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UdonSharp;
using VRC.Udon.Editor;

/// <summary>
/// Facade: entry point for USugar compiler (menu items, toggle, static init).
/// Delegates to USugarHarmonyPatcher, USugarCompilationOrchestrator, etc.
/// </summary>
[InitializeOnLoad]
public static class USugarCompiler
{
    const string OverrideMenuPath = "USugar/Override Compiler";
    const string OverridePrefKey = "USugar_OverrideUdonSharp";

    internal static bool OverrideEnabled
    {
        get => EditorPrefs.GetBool(OverridePrefKey, false);
        set => EditorPrefs.SetBool(OverridePrefKey, value);
    }

    static USugarCompiler()
    {
        try
        {
            USugarHarmonyPatcher.Initialize();
        }
        catch (Exception ex)
        {
            USugarLog.Error($"Static init failed: {ex}");
        }
    }

    [MenuItem(OverrideMenuPath)]
    static void ToggleOverride()
    {
        var next = !OverrideEnabled;
        OverrideEnabled = next;
        if (next) 
            USugarHarmonyPatcher.ApplyPatches();
        else 
            USugarHarmonyPatcher.RemovePatches();
    }

    [MenuItem(OverrideMenuPath, true)]
    static bool ToggleOverrideValidate()
    {
        Menu.SetChecked(OverrideMenuPath, OverrideEnabled);
        return true;
    }
    [MenuItem("USugar/Compile/USugar")]
    public static void CompileAndApply() => USugarCompilationOrchestrator.CompileInternal(applyToAssets: true, force: true, dumpEnabled: DumpIREnabled);

    static bool DumpIREnabled
    {
        get => UnityEditor.EditorPrefs.GetBool("USugar_DumpIR", false);
        set => UnityEditor.EditorPrefs.SetBool("USugar_DumpIR", value);
    }

    const string DumpIRMenuPath = "USugar/Dump IR";

    [MenuItem(DumpIRMenuPath)]
    static void ToggleDumpIR() => DumpIREnabled = !DumpIREnabled;

    [MenuItem(DumpIRMenuPath, true)]
    static bool ToggleDumpIRValidate()
    {
        Menu.SetChecked(DumpIRMenuPath, DumpIREnabled);
        return true;
    }

    [MenuItem("USugar/Compile/UdonSharp")]
    static void ExportReferenceUasm()
    {
        var wasEnabled = OverrideEnabled;
        OverrideEnabled = false;
        // Remove all Harmony patches to ensure UdonSharp runs completely unmodified
        USugarHarmonyPatcher.RemovePatches();
        try
        {
            var compileSync = USugarReflectionTargets.CompileSyncMethod;
            if (compileSync == null)
            {
                USugarLog.Error("CompileSync not found");
                return;
            }
            var options = USugarReflectionTargets.CompileOptionsType != null
                ? Activator.CreateInstance(USugarReflectionTargets.CompileOptionsType) : null;
            compileSync.Invoke(null, new[] { options });

            var refDir = "Library/USugarCache/Reference";
            Directory.CreateDirectory(refDir);

            var instance = USugarReflectionTargets.GetEditorCacheInstance();
            if (instance == null)
            {
                USugarLog.Info("UdonSharpEditorCache instance not available — skipping reference export");
                return;
            }

            var programAssets = Resources.FindObjectsOfTypeAll<UdonSharpProgramAsset>();
            int saved = 0;
            foreach (var asset in programAssets)
            {
                if (asset.sourceCsScript == null) continue;
                var uasm = USugarReflectionTargets.GetUasmStr?.Invoke(instance, new object[] { asset }) as string;
                if (string.IsNullOrEmpty(uasm)) continue;
                var name = asset.sourceCsScript.name;
                File.WriteAllText(Path.Combine(refDir, $"{name}.uasm"), uasm);
                saved++;
            }

            USugarLog.Info($"Exported {saved} reference UASM files to {refDir}");
        }
        catch (Exception ex)
        {
            USugarLog.Error($"Reference export failed: {ex}");
        }
        finally
        {
            OverrideEnabled = wasEnabled;
            if (wasEnabled)
            {
                USugarHarmonyPatcher.ApplyPatches();
                USugarCompilationOrchestrator.CompileInternal(applyToAssets: true, force: true);
            }
        }
    }

    // Debug utility: uncomment [MenuItem] to dump all registered Udon externs to a text file.
    // Useful for checking if a specific extern signature exists in the current SDK version.
    //[MenuItem("USugar/Dump Udon Extern Registry")]
    public static void DumpExternRegistry()
    {
        var defs = UdonEditorManager.Instance.GetNodeDefinitions()
            .Select(d => d.fullName)
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(n => n)
            .ToArray();

        var outputPath = "Library/USugarCache/udon_extern_registry.txt";
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        File.WriteAllLines(outputPath, defs);
        USugarLog.Info($"Dumped {defs.Length} node definitions → {outputPath}");
    }
}
