using System;
using System.IO;
using System.Linq;
using UnityEditor;
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
    public static void CompileAndApply() =>
        USugarCompilationOrchestrator.CompileSynchronously(force: true);

    // Regenerates the tracked Event_* snapshot exclusively from the public SDK editor registry.
    // EventRegistryCensusTests validates LayoutPlanner's event tables bidirectionally against it.

    public static void DumpEventRegistry()
    {
        var header = new[]
        {
            "# Udon event node registry census: one row per SDK Event_* node definition, sorted by node name",
            "# (ordinal). Event_Custom* and Event_OnVariableChange are excluded — they are not method-name-bound",
            "# UdonSharpBehaviour events (custom events are user-named; OnVariableChange binds via FieldChangeCallback).",
            "# Format: Event_Name|outParam:TypeFullName,... — RAW registry truth. Export names (_lowerFirst) and",
            "# param heap-var names ({lowerEvent}{UpperParam}) are DERIVED from these rows exactly like stock",
            "# UdonSharp's CompilerUdonInterface.CacheInit; EventRegistryCensusTests pins LayoutPlanner's tables",
            "# bidirectionally against this file.",
            "# Source: UdonEditorManager.Instance.GetNodeDefinitions() — regenerate via USugarCompiler.DumpEventRegistry.",
        };
        var rows = UdonEditorManager.Instance.GetNodeDefinitions()
            .Where(d => !string.IsNullOrEmpty(d.fullName)
                        && d.fullName.StartsWith("Event_", StringComparison.Ordinal)
                        && !d.fullName.StartsWith("Event_Custom", StringComparison.Ordinal)
                        && d.fullName != "Event_OnVariableChange")
            .OrderBy(d => d.fullName, StringComparer.Ordinal)
            .Select(d => d.Outputs.Count == 0
                ? d.fullName
                : d.fullName + "|" + string.Join(",", d.Outputs.Select(p => $"{p.name}:{p.type.FullName}")))
            .ToArray();

        var outputPath = "Assets/USugar/Editor~/Tests/Fixtures/udon_event_registry.txt";
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        File.WriteAllLines(outputPath, header.Concat(rows));
        USugarLog.Info($"Dumped {rows.Length} event node definitions → {outputPath}");
    }
}
