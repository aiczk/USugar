using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEditor;
using UnityEngine;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// Harmony patch to capture the exact PC and context when UdonVM halts.
/// Two patches: RunProgram saves context, Interpret captures PC before it's restored.
/// </summary>
[InitializeOnLoad]
static class UdonVMDebugPatch
{
    static UdonVMDebugPatch()
    {
        new Harmony("com.usugar.vmdebug").PatchAll();
        Debug.Log("[USugar] VM debug patch applied");
    }

    // --- Reflection cache ---
    static readonly FieldInfo FVM =
        typeof(UdonBehaviour).GetField("_udonVM", BindingFlags.NonPublic | BindingFlags.Instance);
    static readonly FieldInfo FProg =
        typeof(UdonBehaviour).GetField("_program", BindingFlags.NonPublic | BindingFlags.Instance);

    static FieldInfo FHalted, FPC, FByteCode;

    static void InitVMFields(object vm)
    {
        if (FHalted != null) return;
        var t = vm.GetType();
        FHalted = t.GetField("_halted", BindingFlags.NonPublic | BindingFlags.Instance);
        FPC = t.GetField("_programCounter", BindingFlags.NonPublic | BindingFlags.Instance);
        FByteCode = t.GetField("_processedByteCode", BindingFlags.NonPublic | BindingFlags.Instance);
    }

    // --- Helpers ---
    static string OpName(uint op) => op switch
    {
        0 => "NOP", 1 => "PUSH", 2 => "POP", 4 => "JUMP_IF_FALSE",
        5 => "JUMP", 6 => "EXTERN", 7 => "ANNOTATION", 8 => "JUMP_INDIRECT",
        9 => "COPY", _ => $"?({op})"
    };

    static bool HasOperand(uint op) => op is 1 or 4 or 5 or 6 or 7 or 8;

    static string ResolveSymbol(IUdonProgram prog, uint addr)
    {
        try
        {
            if (prog?.SymbolTable?.HasSymbolForAddress(addr) == true)
                return prog.SymbolTable.GetSymbolFromAddress(addr);
        }
        catch { /* ignore */ }
        return null;
    }

    // --- Context passing from RunProgram to Interpret ---
    struct CallContext
    {
        public string ObjName, ProgName, EntryName;
        public uint EntryPoint;
        public IUdonProgram Program;
    }

    [ThreadStatic] static CallContext _ctx;

    // --- Patch 1: RunProgram — save calling context ---
    [HarmonyPatch(typeof(UdonBehaviour), "RunProgram", typeof(uint))]
    static class RunProgramPatch
    {
        static void Prefix(UdonBehaviour __instance, uint entryPoint)
        {
            try
            {
                var prog = (IUdonProgram)FProg?.GetValue(__instance);
                string entryName = null;
                try { prog?.EntryPoints?.TryGetSymbolFromAddress(entryPoint, out entryName); }
                catch { /* ignore */ }

                _ctx = new CallContext
                {
                    ObjName = __instance.gameObject?.name ?? "?",
                    ProgName = __instance.programSource?.name ?? "?",
                    EntryName = entryName ?? "?",
                    EntryPoint = entryPoint,
                    Program = prog
                };
            }
            catch { /* ignore */ }
        }
    }

    // --- Patch 2: Interpret — capture PC at the moment of halt ---
    [HarmonyPatch]
    static class InterpretPatch
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                AccessTools.TypeByName("VRC.Udon.VM.UdonVM"), "Interpret");
        }

        static void Prefix(object __instance, ref bool __state)
        {
            try
            {
                InitVMFields(__instance);
                __state = !(bool)FHalted.GetValue(__instance);
            }
            catch { __state = false; }
        }

        static void Postfix(object __instance, bool __state, uint __result)
        {
            if (!__state || __result == 0) return;
            try
            {
                // PC still holds the failure address (RunProgram hasn't restored it yet)
                uint pc = (uint)FPC.GetValue(__instance);
                var ctx = _ctx;
                var prog = ctx.Program;

                var sb = new StringBuilder();
                sb.AppendLine("[USugar VM Debug] === HALT DETECTED ===");
                sb.AppendLine($"  Object:  {ctx.ObjName}");
                sb.AppendLine($"  Program: {ctx.ProgName}");
                sb.AppendLine($"  Entry:   {ctx.EntryName} (0x{ctx.EntryPoint:X8})");
                sb.AppendLine($"  PC:      0x{pc:X8}");

                // Bytecode dump around PC
                var code = (uint[])FByteCode?.GetValue(__instance);
                if (code != null)
                {
                    int pcIdx = (int)(pc / 4);
                    sb.AppendLine($"  Bytecode (total {code.Length * 4} bytes):");
                    int from = Math.Max(0, pcIdx - 12);
                    int to = Math.Min(code.Length, pcIdx + 8);
                    for (int i = from; i < to;)
                    {
                        string mark = (i == pcIdx) ? ">>>" : "   ";
                        uint op = code[i];
                        if (HasOperand(op) && i + 1 < code.Length)
                        {
                            uint operand = code[i + 1];
                            string sym = ResolveSymbol(prog, operand);
                            sb.AppendLine($"  {mark} 0x{i * 4:X8}: {OpName(op)} 0x{operand:X8}" +
                                          (sym != null ? $" ({sym})" : ""));
                            i += 2;
                        }
                        else
                        {
                            sb.AppendLine($"  {mark} 0x{i * 4:X8}: {OpName(op)}");
                            i++;
                        }
                    }
                }

                // Stack dump
                try
                {
                    var stackField = __instance.GetType().GetField("_stack",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    var stack = stackField?.GetValue(__instance);
                    if (stack != null)
                    {
                        var sizeField = stack.GetType().GetField("_size",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        var arrField = stack.GetType().GetField("_array",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        int size = (int)sizeField.GetValue(stack);
                        sb.AppendLine($"  Stack (depth={size}):");
                        if (size > 0)
                        {
                            var arr = (uint[])arrField.GetValue(stack);
                            int showFrom = Math.Max(0, size - 10);
                            for (int i = showFrom; i < size; i++)
                            {
                                string sym = ResolveSymbol(prog, arr[i]);
                                sb.AppendLine($"    [{i}] = 0x{arr[i]:X8}" +
                                              (sym != null ? $" ({sym})" : ""));
                            }
                        }
                    }
                }
                catch { /* stack dump is best-effort */ }

                Debug.LogError(sb.ToString());
            }
            catch (Exception e)
            {
                Debug.LogError($"[USugar VM Debug] Patch error: {e}");
            }
        }
    }
}
