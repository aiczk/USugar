using System;
using System.Collections.Generic;

/// <summary>
/// Verifies structured Core IR invariants. Run after Core construction and after each structured
/// optimization pass. Throws <see cref="VerificationException"/> on the first violation found.
/// </summary>
public static class CoreVerify
{
    public static void Verify(CModule module)
    {
        foreach (var func in module.Functions)
            VerifyFunction(func);
    }

    public static void VerifyFunction(CFunction func)
    {
        var ctx = new VerifyContext(func);
        VerifyBlock(func.Body, ctx);
        VerifyGotoLabels(func);
    }

    sealed class VerifyContext
    {
        public readonly CFunction Func;
        public readonly HashSet<int> DeclaredSlots = new();
        public int LoopDepth;

        public VerifyContext(CFunction func)
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

        /// <summary>Type check for CAssign — more relaxed because Udon VM stores enums as Int32.</summary>
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

    static void VerifyBlock(CBlock block, VerifyContext ctx)
    {
        foreach (var stmt in block.Stmts)
            VerifyStmt(stmt, ctx);
    }

    static void VerifyStmt(CStmt stmt, VerifyContext ctx)
    {
        switch (stmt)
        {
            case CAssign assign:
                ctx.AssertSlotExists(assign.DestSlot, "CAssign");
                VerifyExpr(assign.Value, ctx);
                // Type check: assigned value must match slot type
                var slotType = ctx.Func.Slots[assign.DestSlot].Type;
                ctx.AssertAssignType(slotType, assign.Value.Type, $"CAssign to slot{assign.DestSlot}");
                break;

            case CStoreField store:
                VerifyExpr(store.Value, ctx);
                break;

            case CIf ifStmt:
                VerifyExpr(ifStmt.Cond, ctx);
                ctx.AssertType("SystemBoolean", ifStmt.Cond.Type, "CIf condition");
                VerifyBlock(ifStmt.Then, ctx);
                VerifyBlock(ifStmt.Else, ctx);
                break;

            case CWhile whileStmt:
                VerifyBlock(whileStmt.CondBlock, ctx);
                VerifyExpr(whileStmt.Cond, ctx);
                ctx.AssertType("SystemBoolean", whileStmt.Cond.Type, "CWhile condition");
                ctx.LoopDepth++;
                VerifyBlock(whileStmt.Body, ctx);
                ctx.LoopDepth--;
                break;

            case CFor forStmt:
                VerifyBlock(forStmt.Init, ctx);
                VerifyBlock(forStmt.CondBlock, ctx);
                if (forStmt.Cond != null)
                {
                    VerifyExpr(forStmt.Cond, ctx);
                    ctx.AssertType("SystemBoolean", forStmt.Cond.Type, "CFor condition");
                }
                ctx.LoopDepth++;
                VerifyBlock(forStmt.Body, ctx);
                ctx.LoopDepth--;
                VerifyBlock(forStmt.Update, ctx);
                break;

            case CBreak:
                if (ctx.LoopDepth <= 0)
                    throw new VerificationException(
                        $"CBreak outside of loop (function '{ctx.Func.Name}')");
                break;

            case CContinue:
                if (ctx.LoopDepth <= 0)
                    throw new VerificationException(
                        $"CContinue outside of loop (function '{ctx.Func.Name}')");
                break;

            case CReturn ret:
                if (ret.Value != null)
                {
                    VerifyExpr(ret.Value, ctx);
                    if (ctx.Func.ReturnType != null)
                        ctx.AssertType(ctx.Func.ReturnType, ret.Value.Type, "CReturn");
                }
                break;

            case CExprStmt exprStmt:
                VerifyExpr(exprStmt.Expr, ctx);
                // Future improvement: warn if exprStmt.Expr is pure (no side effects),
                // as pure expression statements are dead code. Requires a warning mechanism
                // since CoreVerify currently only throws exceptions.
                break;

            case CBlock block:
                VerifyBlock(block, ctx);
                break;

            case CGoto:
            case CLabel:
                break; // goto/label pairing is verified by VerifyGotoLabels

            default:
                throw new VerificationException($"Unknown CStmt type: {stmt.GetType().Name}");
        }
    }

    /// <summary>Verify that every CGoto target has a corresponding CLabel in the same function.</summary>
    static void VerifyGotoLabels(CFunction func)
    {
        var labels = new HashSet<string>();
        var gotos = new HashSet<string>();
        CollectLabelsAndGotos(func.Body, labels, gotos);

        foreach (var target in gotos)
        {
            if (!labels.Contains(target))
                throw new VerificationException(
                    $"CGoto targets undefined label '{target}' (function '{func.Name}')");
        }
    }

    static void CollectLabelsAndGotos(CBlock block, HashSet<string> labels, HashSet<string> gotos)
    {
        foreach (var stmt in block.Stmts)
            CollectLabelsAndGotosStmt(stmt, labels, gotos);
    }

    static void CollectLabelsAndGotosStmt(CStmt stmt, HashSet<string> labels, HashSet<string> gotos)
    {
        switch (stmt)
        {
            case CLabel lbl:
                labels.Add(lbl.Label);
                break;
            case CGoto gt:
                gotos.Add(gt.Label);
                break;
            case CBlock blk:
                CollectLabelsAndGotos(blk, labels, gotos);
                break;
            case CIf hif:
                CollectLabelsAndGotos(hif.Then, labels, gotos);
                CollectLabelsAndGotos(hif.Else, labels, gotos);
                break;
            case CWhile hw:
                CollectLabelsAndGotos(hw.CondBlock, labels, gotos);
                CollectLabelsAndGotos(hw.Body, labels, gotos);
                break;
            case CFor hf:
                CollectLabelsAndGotos(hf.Init, labels, gotos);
                CollectLabelsAndGotos(hf.CondBlock, labels, gotos);
                CollectLabelsAndGotos(hf.Update, labels, gotos);
                CollectLabelsAndGotos(hf.Body, labels, gotos);
                break;
        }
    }

    static void VerifyExpr(CValue expr, VerifyContext ctx)
    {
        switch (expr)
        {
            case CConst:
                break; // always valid

            case CSlotRef slotRef:
                ctx.AssertSlotExists(slotRef.SlotId, "CSlotRef");
                var declaredType = ctx.Func.Slots[slotRef.SlotId].Type;
                ctx.AssertType(declaredType, slotRef.Type, $"CSlotRef slot{slotRef.SlotId}");
                break;

            case CFieldRef:
                break; // field existence checked at a higher level

            case CExternCall call:
                foreach (var arg in call.Args)
                    VerifyExpr(arg, ctx);
                break;

            case CInternalCall call:
                foreach (var arg in call.Args)
                    VerifyExpr(arg, ctx);
                break;

            case CSelect sel:
                VerifyExpr(sel.Cond, ctx);
                ctx.AssertType("SystemBoolean", sel.Cond.Type, "CSelect condition");
                VerifyExpr(sel.TrueVal, ctx);
                VerifyExpr(sel.FalseVal, ctx);
                // Branch types may differ from result type due to inheritance
                // (e.g., RenderTexture vs Texture). Udon VM handles implicit conversion.
                break;

            case CCrossCall cc:
                VerifyExpr(cc.Instance, ctx);
                foreach (var (_, value) in cc.Params)
                    VerifyExpr(value, ctx);
                // Note: param value type checking against the target method's parameter types
                // is not possible here — HIR only stores param names, not the target method's
                // type signature. Type errors will surface at runtime via Udon VM.
                break;

            case CFuncRef:
                break;

            default:
                throw new VerificationException($"Unknown CValue type: {expr.GetType().Name}");
        }
    }
}

/// <summary>Exception thrown when Core IR verification fails.</summary>
public sealed class VerificationException : Exception
{
    public VerificationException(string message) : base(message) { }
}
