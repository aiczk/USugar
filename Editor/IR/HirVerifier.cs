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
            throw new VerificationException(
                $"Type mismatch in {context}: expected '{expected}', got '{actual}' (function '{Func.Name}')");
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
                ctx.AssertType(slotType, assign.Value.Type, $"HAssign to slot{assign.DestSlot}");
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
                VerifyExpr(whileStmt.Cond, ctx);
                ctx.AssertType("SystemBoolean", whileStmt.Cond.Type, "HWhile condition");
                ctx.LoopDepth++;
                VerifyBlock(whileStmt.Body, ctx);
                ctx.LoopDepth--;
                break;

            case HFor forStmt:
                VerifyBlock(forStmt.Init, ctx);
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
                break;

            case HBlock block:
                VerifyBlock(block, ctx);
                break;

            case HGoto:
            case HLabelStmt:
                break; // label validity could be checked but is not critical

            default:
                throw new VerificationException($"Unknown HStmt type: {stmt.GetType().Name}");
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
