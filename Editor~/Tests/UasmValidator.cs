using System;
using System.Collections.Generic;
using System.Linq;

namespace USugar.Tests;

public class UasmValidationException : Exception
{
    public UasmValidationException(string message) : base(message) { }
}

public static class UasmValidator
{
    public static void Validate(string uasm)
    {
        var declaredVars = ParseDeclaredVariables(uasm);
        ValidateVariableReferences(uasm, declaredVars);
        ValidateJumpTargets(uasm);
        ValidateExterns(uasm);
        ValidateNoDuplicateVars(uasm);
        ValidateStackBalance(uasm);
    }

    static HashSet<string> ParseDeclaredVariables(string uasm)
    {
        var vars = new HashSet<string>();
        var inData = false;
        foreach (var line in uasm.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed == ".data_start") { inData = true; continue; }
            if (trimmed == ".data_end") { inData = false; continue; }
            if (!inData) continue;
            if (trimmed.StartsWith(".export") || trimmed.StartsWith(".sync")) continue;

            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx > 0)
                vars.Add(trimmed.Substring(0, colonIdx));
        }
        return vars;
    }

    static void ValidateVariableReferences(string uasm, HashSet<string> declaredVars)
    {
        var errors = new List<string>();
        var inCode = false;
        foreach (var line in uasm.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed == ".code_start") { inCode = true; continue; }
            if (trimmed == ".code_end") { inCode = false; continue; }
            if (!inCode) continue;

            if (trimmed.StartsWith("PUSH, "))
            {
                var varId = trimmed.Substring("PUSH, ".Length);
                if (!declaredVars.Contains(varId))
                    errors.Add($"Undeclared variable in PUSH: {varId}");
            }
            else if (trimmed.StartsWith("JUMP_INDIRECT, "))
            {
                var varId = trimmed.Substring("JUMP_INDIRECT, ".Length);
                if (!declaredVars.Contains(varId))
                    errors.Add($"Undeclared variable in JUMP_INDIRECT: {varId}");
            }
        }
        if (errors.Count > 0)
            throw new UasmValidationException(
                $"UASM variable reference errors:\n{string.Join("\n", errors)}");
    }

    static bool IsLabel(string trimmed) =>
        trimmed.EndsWith(":") && !trimmed.StartsWith("PUSH") && !trimmed.StartsWith("JUMP")
        && !trimmed.StartsWith("COPY") && !trimmed.StartsWith("POP") && !trimmed.StartsWith("EXTERN")
        && !trimmed.StartsWith("NOP");

    static void ValidateJumpTargets(string uasm)
    {
        // Collect all instruction boundary addresses (not just labels)
        var validAddresses = new HashSet<uint>();
        var jumpTargets = new List<(uint address, string raw)>();
        uint currentAddr = 0;
        var inCode = false;

        foreach (var line in uasm.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed == ".code_start") { inCode = true; continue; }
            if (trimmed == ".code_end") { inCode = false; continue; }
            if (!inCode) continue;
            if (trimmed.StartsWith(".export") || trimmed.StartsWith("#") || trimmed.Length == 0) continue;

            if (IsLabel(trimmed))
            {
                validAddresses.Add(currentAddr);
                continue;
            }

            // Every instruction start is a valid jump target
            validAddresses.Add(currentAddr);

            if (trimmed.StartsWith("JUMP, 0x"))
            {
                var hex = trimmed.Substring("JUMP, ".Length);
                jumpTargets.Add((Convert.ToUInt32(hex, 16), hex));
                currentAddr += 8;
            }
            else if (trimmed.StartsWith("JUMP_IF_FALSE, 0x"))
            {
                var hex = trimmed.Substring("JUMP_IF_FALSE, ".Length);
                jumpTargets.Add((Convert.ToUInt32(hex, 16), hex));
                currentAddr += 8;
            }
            else if (trimmed.StartsWith("PUSH"))
                currentAddr += 8;
            else if (trimmed == "POP" || trimmed == "COPY")
                currentAddr += 4;
            else if (trimmed.StartsWith("JUMP_INDIRECT"))
                currentAddr += 8;
            else if (trimmed.StartsWith("EXTERN"))
                currentAddr += 8;
            else if (trimmed.StartsWith("NOP"))
                currentAddr += 4;
        }

        // Address past last instruction is valid (skip-to-end)
        validAddresses.Add(currentAddr);

        var errors = jumpTargets
            .Where(j => !validAddresses.Contains(j.address))
            .Select(j => $"Jump to invalid address: {j.raw}")
            .ToList();

        if (errors.Count > 0)
            throw new UasmValidationException(
                $"UASM jump target errors:\n{string.Join("\n", errors)}");
    }

    static void ValidateExterns(string uasm)
    {
        var errors = new List<string>();
        foreach (var line in uasm.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("EXTERN, \"")) continue;
            var externName = trimmed.Substring("EXTERN, \"".Length).TrimEnd('"');
            if (!ExternRegistry.IsValid(externName))
                errors.Add($"Unknown extern: {externName}");
        }
        if (errors.Count > 0)
            throw new UasmValidationException(
                $"UASM extern errors:\n{string.Join("\n", errors)}");
    }

    static void ValidateNoDuplicateVars(string uasm)
    {
        var seen = new HashSet<string>();
        var duplicates = new List<string>();
        var inData = false;
        foreach (var line in uasm.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed == ".data_start") { inData = true; continue; }
            if (trimmed == ".data_end") { inData = false; continue; }
            if (!inData) continue;
            if (trimmed.StartsWith(".export") || trimmed.StartsWith(".sync")) continue;

            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx > 0)
            {
                var varName = trimmed.Substring(0, colonIdx);
                if (!seen.Add(varName))
                    duplicates.Add(varName);
            }
        }
        if (duplicates.Count > 0)
            throw new UasmValidationException(
                $"UASM duplicate variable declarations: {string.Join(", ", duplicates)}");
    }

    /// Verifies that the reported heap size is at least as large as the number of
    /// variables declared in the UASM data block plus unique externs in the code block.
    /// The Udon assembler allocates anonymous heap slots for extern strings, so heap
    /// size must cover both declared variables and unique extern signatures.
    public static void ValidateHeapConsistency(string uasm, uint reportedHeapSize)
    {
        var declaredVars = ParseDeclaredVariables(uasm);
        var uniqueExterns = new HashSet<string>();
        foreach (var line in uasm.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("EXTERN, \"")) continue;
            var externName = trimmed.Substring("EXTERN, \"".Length).TrimEnd('"');
            uniqueExterns.Add(externName);
        }
        uint requiredSize = (uint)(declaredVars.Count + uniqueExterns.Count);
        if (reportedHeapSize < requiredSize)
            throw new UasmValidationException(
                $"Heap size mismatch: reported {reportedHeapSize} but UASM declares {declaredVars.Count} vars + {uniqueExterns.Count} externs = {requiredSize} required");
    }

    // Stack balance validation: verifies that each consuming instruction
    // receives the correct number of PUSHed values within its linear segment.
    //
    // The stack-based calling convention uses the Udon VM stack to pass return addresses
    // across JUMP boundaries (matching UdonSharp's protocol):
    // - Internal call: PUSH returnAddr; JUMP callee (stack carries return addr)
    // - Method entry: PUSH sentinel (for external callers)
    // - Return: PUSH returnJump; COPY; JUMP_INDIRECT returnJump
    //   (COPY consumes returnJump + previously-pushed sentinel/returnAddr from stack)
    //
    // Because values cross JUMP boundaries, the validator allows:
    // - JUMP with non-zero stack (PUSHes are stack-passed values for the callee)
    // - COPY with pushCount==1 when followed by JUMP_INDIRECT (the return pattern;
    //   the missing PUSH is the sentinel/returnAddr pushed before entry or call)
    static void ValidateStackBalance(string uasm)
    {
        var errors = new List<string>();
        var externPushCounts = new Dictionary<string, int>();
        int pushCount = 0;
        bool inCode = false;
        int lineNum = 0;

        var allLines = uasm.Split('\n');

        for (int i = 0; i < allLines.Length; i++)
        {
            lineNum = i + 1;
            var trimmed = allLines[i].Trim();
            if (trimmed == ".code_start") { inCode = true; continue; }
            if (trimmed == ".code_end")
            {
                inCode = false;
                continue;
            }
            if (!inCode) continue;
            if (trimmed.Length == 0 || trimmed.StartsWith(".export") || trimmed.StartsWith("#")) continue;

            if (IsLabel(trimmed))
            {
                pushCount = 0;
                continue;
            }

            if (trimmed.StartsWith("PUSH, "))
            {
                pushCount++;
                continue;
            }

            if (trimmed == "COPY")
            {
                // Return pattern: PUSH returnJump; COPY; JUMP_INDIRECT returnJump
                // The COPY consumes 1 local PUSH + 1 cross-boundary value from the VM stack
                bool isReturnPattern = pushCount == 1
                    && i + 1 < allLines.Length
                    && allLines[i + 1].Trim().StartsWith("JUMP_INDIRECT, ");

                if (!isReturnPattern && pushCount != 2)
                    errors.Add($"Line {lineNum}: COPY expects 2 PUSHes, got {pushCount}");
                pushCount = 0;
                continue;
            }

            if (trimmed.StartsWith("JUMP_IF_FALSE, "))
            {
                if (pushCount != 1)
                    errors.Add($"Line {lineNum}: JUMP_IF_FALSE expects 1 PUSH, got {pushCount}");
                pushCount = 0;
                continue;
            }

            if (trimmed.StartsWith("EXTERN, "))
            {
                var externName = trimmed.Substring("EXTERN, \"".Length).TrimEnd('"');

                // Consistency: same extern must always get same push count
                if (externPushCounts.TryGetValue(externName, out var prev))
                {
                    if (pushCount != prev)
                        errors.Add($"Line {lineNum}: EXTERN \"{externName}\" got {pushCount} PUSHes, previously {prev}");
                }
                else
                {
                    externPushCounts[externName] = pushCount;
                }

                // Signature-based bounds check
                var (minPush, maxPush) = GetExternPushBounds(externName);
                if (minPush >= 0 && (pushCount < minPush || pushCount > maxPush))
                    errors.Add($"Line {lineNum}: EXTERN \"{externName}\" got {pushCount} PUSHes, expected {minPush}-{maxPush}");

                pushCount = 0;
                continue;
            }

            if (trimmed.StartsWith("JUMP, "))
            {
                // Stack-based calling convention: PUSHes before JUMP are stack-passed
                // values (return addresses, sentinel) for the callee — this is valid.
                pushCount = 0;
                continue;
            }

            if (trimmed.StartsWith("JUMP_INDIRECT, "))
            {
                // JUMP_INDIRECT used in: (1) AddReturn (after COPY, pushCount==0),
                // (2) delegate invocation (pushCount should be 0 after PushLabel goes to callee)
                pushCount = 0;
                continue;
            }

            if (trimmed == "POP")
            {
                if (pushCount < 1)
                    errors.Add($"Line {lineNum}: POP on empty stack");
                else
                    pushCount--;
                continue;
            }

            // NOP etc. — no stack effect
        }

        if (errors.Count > 0)
            throw new UasmValidationException(
                $"UASM stack balance errors:\n{string.Join("\n", errors)}");
    }

    // Parse extern signature to determine valid PUSH count range.
    // Returns (staticCount, instanceCount) where actual push count must be one of the two.
    // Returns (-1, -1) if signature cannot be parsed.
    static (int min, int max) GetExternPushBounds(string externName)
    {
        var dotIdx = externName.IndexOf('.');
        if (dotIdx < 0) return (-1, -1);

        var afterDot = externName.Substring(dotIdx + 1);
        var lastDunder = afterDot.LastIndexOf("__");
        if (lastDunder < 0) return (-1, -1);

        var returnType = afterDot.Substring(lastDunder + 2);
        int returnPush = returnType == "SystemVoid" ? 0 : 1;

        // Find parameter section: between method name and return type
        var beforeReturn = afterDot.Substring(0, lastDunder);
        var paramDunder = beforeReturn.LastIndexOf("__");
        int paramCount = 0;
        if (paramDunder >= 0)
        {
            var candidate = beforeReturn.Substring(paramDunder + 2);
            // Type names start with uppercase; method name components (get_, set_, op_) start with lowercase
            if (candidate.Length > 0 && char.IsUpper(candidate[0]))
                paramCount = candidate.Split('_').Length;
        }

        var staticCount = paramCount + returnPush;
        return (staticCount, staticCount + 1);
    }
}
