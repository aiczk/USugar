using System;
using System.Collections.Generic;

/// <summary>
/// Verifies HIR structural invariants. Run after HIR construction and after each HIR optimization pass.
/// Throws VerificationException on first violation found.
/// </summary>
public static class HirVerifier
{
    public static void Verify(HModule module)
    {
        foreach (var func in module.Functions)
            VerifyFunction(func);
    }

    public static void VerifyFunction(HFunction func)
    {
        var ctx = new VerifyContext(func);
        VerifyBlock(func.Body, ctx);
        VerifyGotoLabels(func);
    }

    sealed class VerifyContext
    {
        public readonly HFunction Func;
        public readonly HashSet<int> DeclaredSlots = new();
        public int LoopDepth;

        public VerifyContext(HFunction func)
        {
            Func = func;
            for (int i = 0; i < func.Slots.Count; i++)
                DeclaredSlots.Add(i);
        }

        public void AssertSlotExists(int slotId, string context)
        {
            if (!DeclaredSlots.Contains(slotId))
                throw new VerificationException(
                    $"Undeclared slot{slotId} in {context} (function '{Func.Name}')");
        }

        public void AssertType(string expected, string actual, string context)
        {
            if (expected == actual) return;
            // SystemObject is compatible with any type (Udon VM boxing/unboxing)
            if (expected == "SystemObject" || actual == "SystemObject") return;
            // Reference types are compatible via COPY in Udon VM (no type enforcement)
            if (IsReferenceUdonType(expected) && IsReferenceUdonType(actual)) return;
            // Nullable<T> erased to T in Udon VM — SystemNullableX ↔ X are compatible
            if (expected.StartsWith("SystemNullable") && expected.Substring("SystemNullable".Length) == actual) return;
            if (actual.StartsWith("SystemNullable") && actual.Substring("SystemNullable".Length) == expected) return;
            throw new VerificationException(
                $"Type mismatch in {context}: expected '{expected}', got '{actual}' (function '{Func.Name}')");
        }

        /// <summary>Type check for HAssign — more relaxed because Udon VM stores enums as Int32.</summary>
        public void AssertAssignType(string slotType, string valueType, string context)
        {
            if (slotType == valueType) return;
            if (slotType == "SystemObject" || valueType == "SystemObject") return;
            if (IsReferenceUdonType(slotType) && IsReferenceUdonType(valueType)) return;
            // Nullable<T> erased to T in Udon VM
            if (slotType.StartsWith("SystemNullable") && slotType.Substring("SystemNullable".Length) == valueType) return;
            if (valueType.StartsWith("SystemNullable") && valueType.Substring("SystemNullable".Length) == slotType) return;
            // Enum types use Int32 underlying type in Udon VM.
            // Allow Int32 ↔ non-primitive types (potential enums).
            if (slotType == "SystemInt32" && !IsKnownNonEnumType(valueType)) return;
            if (valueType == "SystemInt32" && !IsKnownNonEnumType(slotType)) return;
            throw new VerificationException(
                $"Type mismatch in {context}: expected '{slotType}', got '{valueType}' (function '{Func.Name}')");
        }

        /// <summary>
        /// Known non-enum types that should NOT be allowed to interop with Int32.
        /// Unrecognized types are assumed to be potential enums (which use Int32 underlying type).
        /// </summary>
        static bool IsKnownNonEnumType(string type) => type is
            "SystemSingle" or "SystemDouble" or "SystemBoolean" or "SystemString"
            or "SystemByte" or "SystemSByte" or "SystemInt16" or "SystemUInt16"
            or "SystemInt64" or "SystemUInt64" or "SystemChar" or "SystemDecimal"
            or "SystemObject" or "SystemType";

        /// <summary>
        /// Heuristic: a Udon type name that does NOT end with known value-type suffixes
        /// and is not a known primitive is treated as a reference type.
        /// Udon VM COPY on reference types just copies heap addresses; no type tag enforcement.
        /// </summary>
        static bool IsReferenceUdonType(string udonType)
        {
            return udonType switch
            {
                "SystemBoolean" or "SystemByte" or "SystemSByte"
                    or "SystemInt16" or "SystemUInt16"
                    or "SystemInt32" or "SystemUInt32"
                    or "SystemInt64" or "SystemUInt64"
                    or "SystemSingle" or "SystemDouble" or "SystemDecimal"
                    or "SystemChar" => false,
                _ when udonType.StartsWith("UnityEngineVector")
                    || udonType.StartsWith("UnityEngineQuaternion")
                    || udonType.StartsWith("UnityEngineColor")
                    || udonType.StartsWith("UnityEngineMatrix")
                    || udonType.StartsWith("UnityEngineRect")
                    || udonType.StartsWith("UnityEngineRay") => false,
                _ => true,
            };
        }
    }

    static void VerifyBlock(HBlock block, VerifyContext ctx)
    {
        foreach (var stmt in block.Stmts)
            VerifyStmt(stmt, ctx);
    }

    static void VerifyStmt(HStmt stmt, VerifyContext ctx)
    {
        switch (stmt)
        {
            case HAssign assign:
                ctx.AssertSlotExists(assign.DestSlot, "HAssign");
                VerifyExpr(assign.Value, ctx);
                // Type check: assigned value must match slot type
                var slotType = ctx.Func.Slots[assign.DestSlot].Type;
                ctx.AssertAssignType(slotType, assign.Value.Type, $"HAssign to slot{assign.DestSlot}");
                break;

            case HStoreField store:
                VerifyExpr(store.Value, ctx);
                break;

            case HIf ifStmt:
                VerifyExpr(ifStmt.Cond, ctx);
                ctx.AssertType("SystemBoolean", ifStmt.Cond.Type, "HIf condition");
                VerifyBlock(ifStmt.Then, ctx);
                VerifyBlock(ifStmt.Else, ctx);
                break;

            case HWhile whileStmt:
                VerifyBlock(whileStmt.CondBlock, ctx);
                VerifyExpr(whileStmt.Cond, ctx);
                ctx.AssertType("SystemBoolean", whileStmt.Cond.Type, "HWhile condition");
                ctx.LoopDepth++;
                VerifyBlock(whileStmt.Body, ctx);
                ctx.LoopDepth--;
                break;

            case HFor forStmt:
                VerifyBlock(forStmt.Init, ctx);
                VerifyBlock(forStmt.CondBlock, ctx);
                if (forStmt.Cond != null)
                {
                    VerifyExpr(forStmt.Cond, ctx);
                    ctx.AssertType("SystemBoolean", forStmt.Cond.Type, "HFor condition");
                }
                ctx.LoopDepth++;
                VerifyBlock(forStmt.Body, ctx);
                ctx.LoopDepth--;
                VerifyBlock(forStmt.Update, ctx);
                break;

            case HBreak:
                if (ctx.LoopDepth <= 0)
                    throw new VerificationException(
                        $"HBreak outside of loop (function '{ctx.Func.Name}')");
                break;

            case HContinue:
                if (ctx.LoopDepth <= 0)
                    throw new VerificationException(
                        $"HContinue outside of loop (function '{ctx.Func.Name}')");
                break;

            case HReturn ret:
                if (ret.Value != null)
                {
                    VerifyExpr(ret.Value, ctx);
                    if (ctx.Func.ReturnType != null)
                        ctx.AssertType(ctx.Func.ReturnType, ret.Value.Type, "HReturn");
                }
                break;

            case HExprStmt exprStmt:
                VerifyExpr(exprStmt.Expr, ctx);
                // Future improvement: warn if exprStmt.Expr is pure (no side effects),
                // as pure expression statements are dead code. Requires a warning mechanism
                // since HirVerifier currently only throws exceptions.
                break;

            case HBlock block:
                VerifyBlock(block, ctx);
                break;

            case HGoto:
            case HLabelStmt:
                break; // goto/label pairing is verified by VerifyGotoLabels

            default:
                throw new VerificationException($"Unknown HStmt type: {stmt.GetType().Name}");
        }
    }

    /// <summary>Verify that every HGoto target has a corresponding HLabelStmt in the same function.</summary>
    static void VerifyGotoLabels(HFunction func)
    {
        var labels = new HashSet<string>();
        var gotos = new HashSet<string>();
        CollectLabelsAndGotos(func.Body, labels, gotos);

        foreach (var target in gotos)
        {
            if (!labels.Contains(target))
                throw new VerificationException(
                    $"HGoto targets undefined label '{target}' (function '{func.Name}')");
        }
    }

    static void CollectLabelsAndGotos(HBlock block, HashSet<string> labels, HashSet<string> gotos)
    {
        foreach (var stmt in block.Stmts)
            CollectLabelsAndGotosStmt(stmt, labels, gotos);
    }

    static void CollectLabelsAndGotosStmt(HStmt stmt, HashSet<string> labels, HashSet<string> gotos)
    {
        switch (stmt)
        {
            case HLabelStmt lbl:
                labels.Add(lbl.Label);
                break;
            case HGoto gt:
                gotos.Add(gt.Label);
                break;
            case HBlock blk:
                CollectLabelsAndGotos(blk, labels, gotos);
                break;
            case HIf hif:
                CollectLabelsAndGotos(hif.Then, labels, gotos);
                CollectLabelsAndGotos(hif.Else, labels, gotos);
                break;
            case HWhile hw:
                CollectLabelsAndGotos(hw.CondBlock, labels, gotos);
                CollectLabelsAndGotos(hw.Body, labels, gotos);
                break;
            case HFor hf:
                CollectLabelsAndGotos(hf.Init, labels, gotos);
                CollectLabelsAndGotos(hf.CondBlock, labels, gotos);
                CollectLabelsAndGotos(hf.Update, labels, gotos);
                CollectLabelsAndGotos(hf.Body, labels, gotos);
                break;
        }
    }

    static void VerifyExpr(HExpr expr, VerifyContext ctx)
    {
        switch (expr)
        {
            case HConst:
                break; // always valid

            case HSlotRef slotRef:
                ctx.AssertSlotExists(slotRef.SlotId, "HSlotRef");
                var declaredType = ctx.Func.Slots[slotRef.SlotId].Type;
                ctx.AssertType(declaredType, slotRef.Type, $"HSlotRef slot{slotRef.SlotId}");
                break;

            case HLoadField:
            case HFieldAddr:
                break; // field existence checked at a higher level

            case HExternCall call:
                foreach (var arg in call.Args)
                    VerifyExpr(arg, ctx);
                break;

            case HInternalCall call:
                foreach (var arg in call.Args)
                    VerifyExpr(arg, ctx);
                break;

            case HSelect sel:
                VerifyExpr(sel.Cond, ctx);
                ctx.AssertType("SystemBoolean", sel.Cond.Type, "HSelect condition");
                VerifyExpr(sel.TrueVal, ctx);
                VerifyExpr(sel.FalseVal, ctx);
                // Branch types may differ from result type due to inheritance
                // (e.g., RenderTexture vs Texture). Udon VM handles implicit conversion.
                break;

            case HCrossBehaviourCall cc:
                VerifyExpr(cc.Instance, ctx);
                foreach (var (_, value) in cc.Params)
                    VerifyExpr(value, ctx);
                // Note: param value type checking against the target method's parameter types
                // is not possible here — HIR only stores param names, not the target method's
                // type signature. Type errors will surface at runtime via Udon VM.
                break;

            case HFuncRef:
                break;

            default:
                throw new VerificationException($"Unknown HExpr type: {expr.GetType().Name}");
        }
    }
}

/// <summary>Exception thrown when HIR verification fails.</summary>
public sealed class VerificationException : Exception
{
    public VerificationException(string message) : base(message) { }
}
