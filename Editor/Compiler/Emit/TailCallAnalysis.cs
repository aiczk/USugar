using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

// ============================================================================
// Pre-wave-12 dedup: EmitContext.HasNonTailSelfCall (self-recursion / delegate-dispatch-site tail
// classification) and UasmEmitter.HasNonTailCallTo (named-callee recursion-spill tail classification)
// used to be two hand-copied ~200-line walkers with an identical shape for every statement-tail
// position (block-last-statement, if/else, switch arm, labeled statement, expression statement) —
// wave-8/10 had to patch new tail shapes in BOTH, and a drift between them silently mis-classifies a
// spill site. This file is the ONE walk both callers now delegate to.
//
// The two callers differ in two genuine ways, both expressed as parameters rather than forked code:
//   - MATCHER SHAPE: EmitContext's delegate-dispatch-site matcher only ever matches a specific
//     IInvocationOperation by reference equality; UasmEmitter's named-callee matcher (IsInternalCallTo)
//     can also match a constructor call or a this-property accessor, and needs getter/setter
//     disambiguation for the compound-assignment / inc-dec statement forms (PropertyAccessorMatches).
//     A matcher that structurally cannot produce a property-shaped match (the dispatch-site one) just
//     leaves those arms permanently inert — same effect as never having had them.
//   - RETURN-POSITION PRECISION: EmitContext's classifier always resolves `return expr;` through a
//     dedicated ternary-aware sub-walk (a `cond ? a : b` return value keeps both branches in tail
//     position). UasmEmitter's never had that — a ternary return value that doesn't ITSELF directly
//     match the callee falls through to the generic non-tail walk instead. In practice this rarely
//     costs an actual spill: UasmEmitter.BuildRecursionInfo's per-CALLEE HasNonTailCallTo answer only
//     gates whether a callee is registered as a possible recursion edge at all — the per-SITE decision
//     of whether that specific call is actually wrapped with spill/reload always re-runs through
//     EmitPolicy.IsNonTailDispatchSite (ternary-precise, and reused for named call sites too, not
//     just delegate dispatch — see HandlerBase.EmitCallToMethod's TailSparedDirectCallSites check), so
//     an all-tail ternary return still ends up spill-free either way (verified: a same-UASM
//     differential across baseline vs. this dedup for `return cond ? Rec(a) : Rec(b);` and deeper
//     nested/mixed variants — see PreWave12TailDedupTests). `ternaryPreciseReturn` still exists to
//     reproduce each caller's original per-callee answer bit-for-bit rather than assume the same
//     inertness holds for every future caller of the shared walk — a genuine precision change is
//     outside a behavior-neutral refactor's scope regardless of how inert it measures today.
// ============================================================================
static class TailCallAnalysis
{
    /// <summary>True if <paramref name="op"/> IS ITSELF the tracked call (not merely containing one).
    /// <paramref name="matched"/> is the matched node (normally <paramref name="op"/>) — its runtime
    /// type (invocation / object-creation / property-reference) drives tail-argument-leg extraction
    /// for the return-position arm.</summary>
    public delegate bool DirectMatcher(IOperation op, out IOperation matched);

    /// <summary>Named-callee matching only: true if <paramref name="pr"/>'s get accessor
    /// (<paramref name="getter"/> true) or set accessor (false) is the tracked call. A matcher that
    /// can never produce a property-shaped match (e.g. a delegate-dispatch-site matcher) should pass
    /// a predicate that always returns false — the compound/inc-dec statement arms below then never
    /// fire, identical to a caller that never had them.</summary>
    public delegate bool AccessorMatcher(IPropertyReferenceOperation pr, bool getter);

    /// <summary>True if <paramref name="body"/> contains a NON-tail occurrence of the tracked call.
    /// See the file header for what <paramref name="checkReturnInstanceLeg"/> and
    /// <paramref name="ternaryPreciseReturn"/> reproduce.</summary>
    public static bool HasNonTailCall(IOperation body, DirectMatcher isMatch, AccessorMatcher matchesAccessor,
        bool checkReturnInstanceLeg, bool ternaryPreciseReturn)
        => Walk(body, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: true);

    static bool Walk(IOperation op, DirectMatcher isMatch, AccessorMatcher matchesAccessor,
        bool checkReturnInstanceLeg, bool ternaryPreciseReturn, bool tail)
    {
        if (op == null) return false;
        if (op is IReturnOperation ret)
            return TailExpr(ret.ReturnedValue, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn);
        if (tail)
        {
            switch (op)
            {
                // A constructor body carries a `: base(...)`/`: this(...)` INITIALIZER that runs BEFORE the
                // field inits and body — never a tail call. The base IMethodBodyBaseOperation case walks only
                // BlockBody/ExpressionBody, so without this arm the initializer's recursive edge (a ctor that
                // re-enters itself through object spawning) is invisible to the spill analysis and the ctor's
                // params get clobbered. Must precede the IMethodBodyBaseOperation case (more specific).
                case IConstructorBodyOperation cb:
                    if (cb.Initializer != null && Walk(cb.Initializer, isMatch, matchesAccessor,
                            checkReturnInstanceLeg, ternaryPreciseReturn, tail: false)) return true;
                    return Walk(cb.BlockBody, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: true)
                        || Walk(cb.ExpressionBody, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: true);
                // Method/accessor bodies arrive as IMethodBodyOperation (block XOR expression body).
                case IMethodBodyBaseOperation mb:
                    return Walk(mb.BlockBody, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: true)
                        || Walk(mb.ExpressionBody, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: true);
                // Only a block's LAST statement stays in tail position. A hoisted lambda/local-function
                // BLOCK body carries an IMPLICIT value-less trailing IReturnOperation (method bodies via
                // IMethodBodyOperation do not), so the statement BEFORE it is the real tail position.
                case IBlockOperation block:
                {
                    var ops = block.Operations;
                    int last = ops.Length - 1;
                    if (last >= 0 && ops[last] is IReturnOperation { ReturnedValue: null, IsImplicit: true })
                        last--;
                    for (int i = 0; i < ops.Length; i++)
                        if (Walk(ops[i], isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: i == last)) return true;
                    return false;
                }
                // A statement-form if/else in tail position: branches stay tail, the condition does not.
                // Loops, usings etc. deliberately fall through to the generic non-tail walk below — code
                // (back-edges, Dispose) runs after their last statement.
                case IConditionalOperation cond:
                    if (Walk(cond.Condition, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: false)) return true;
                    return Walk(cond.WhenTrue, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: true)
                        || Walk(cond.WhenFalse, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: true);
                // A SWITCH in tail position — unlike loops/usings, nothing runs after an arm's last
                // statement except the implicit break out of the switch (arms cannot fall through), so
                // each arm's trailing statement (before that break) stays tail. The switch value and case
                // clauses are not; a trailing `goto case`/`goto label` stays on the generic walk (another
                // arm's body runs after it).
                case ISwitchOperation sw:
                {
                    if (Walk(sw.Value, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: false)) return true;
                    foreach (var swCase in sw.Cases)
                    {
                        foreach (var clause in swCase.Clauses)
                            if (Walk(clause, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: false)) return true;
                        var caseBody = swCase.Body;
                        int caseLast = caseBody.Length - 1;
                        if (caseLast >= 0 && caseBody[caseLast] is IBranchOperation { BranchKind: BranchKind.Break })
                            caseLast--;
                        for (int i = 0; i < caseBody.Length; i++)
                            if (Walk(caseBody[i], isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: i == caseLast)) return true;
                    }
                    return false;
                }
                // A LABELED statement in tail position — the label wrapper changes where control can
                // ARRIVE, not what runs after, so the wrapped statement keeps the tail flag.
                case ILabeledOperation labeledStmt:
                    return Walk(labeledStmt.Operation, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: true);
                case IExpressionStatementOperation exprStmt:
                    return TailStatement(exprStmt.Operation, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn);
            }
        }
        if (isMatch(op, out _)) return true; // matched call as a statement / non-tail position
        foreach (var child in op.ChildOps())
        {
            if (child is ILocalFunctionOperation || child is IAnonymousFunctionOperation) continue; // own nodes
            if (Walk(child, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: false)) return true;
        }
        return false;
    }

    /// <summary>Classify a `return`'s value expression (or a ternary branch reached from one — see
    /// <paramref name="ternaryPreciseReturn"/>). A direct match is tail (only its argument legs, and
    /// optionally its receiver leg, are non-tail); otherwise a ternary keeps both branches in tail
    /// position when the caller asked for that precision, else falls to the generic non-tail walk.</summary>
    static bool TailExpr(IOperation expr, DirectMatcher isMatch, AccessorMatcher matchesAccessor,
        bool checkReturnInstanceLeg, bool ternaryPreciseReturn)
    {
        if (isMatch(expr, out var matched))
        {
            var tailArgs = (matched as IInvocationOperation)?.Arguments
                           ?? (matched as IObjectCreationOperation)?.Arguments
                           // A this-receiver property/indexer reference in return position is a tail
                           // accessor call — only its index arguments are non-tail positions.
                           ?? (matched as IPropertyReferenceOperation)?.Arguments
                           ?? ImmutableArray<IArgumentOperation>.Empty;
            foreach (var a in tailArgs)
                if (Walk(a, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: false)) return true;
            if (checkReturnInstanceLeg)
                return Walk((matched as IInvocationOperation)?.Instance, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: false);
            return false;
        }
        if (ternaryPreciseReturn && expr is IConditionalOperation cond) // branches stay tail; the condition does not
        {
            if (Walk(cond.Condition, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: false)) return true;
            return TailExpr(cond.WhenTrue, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn)
                || TailExpr(cond.WhenFalse, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn);
        }
        return Walk(expr, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: false);
    }

    /// <summary>Classify the expression of a TAIL statement. A matched call is tail (only its
    /// argument/receiver subexpressions are non-tail) unless it carries ref/out arguments — their
    /// copy-back reads the param heap vars AFTER the call. `P = v;` / `this[i] = v;` runs the SETTER
    /// last: the setter reference is tail, the receiver/index/value legs are not. The COMPOUND
    /// (`P += v;`) and inc/dec (`P--;`) statement forms also run the setter last — but their GETTER
    /// runs first and its result is consumed afterwards, so the setter is spared ONLY when the
    /// tracked call is not also the getter. `d?.Invoke(args);` as the tail statement: the WhenNotNull
    /// dispatch is the last thing the frame runs (the null arm skips straight to the implicit
    /// return), so it stays tail; the receiver leg does not.</summary>
    static bool TailStatement(IOperation expr, DirectMatcher isMatch, AccessorMatcher matchesAccessor,
        bool checkReturnInstanceLeg, bool ternaryPreciseReturn)
    {
        if (expr == null) return false;
        if (isMatch(expr, out var matched) && matched is IInvocationOperation invMatched
            && invMatched.TargetMethod.Parameters.All(p => p.RefKind == RefKind.None))
        {
            foreach (var a in invMatched.Arguments)
                if (Walk(a, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: false)) return true;
            return Walk(invMatched.Instance, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: false);
        }
        if (expr is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation propTarget } setStmt
            && isMatch(propTarget, out _))
        {
            if (Walk(propTarget.Instance, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: false)) return true;
            foreach (var arg in propTarget.Arguments)
                if (Walk(arg, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: false)) return true;
            return Walk(setStmt.Value, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: false);
        }
        if (expr is ICompoundAssignmentOperation { Target: IPropertyReferenceOperation cmpTarget } cmpStmt
            && matchesAccessor(cmpTarget, getter: false) && !matchesAccessor(cmpTarget, getter: true))
        {
            if (Walk(cmpTarget.Instance, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: false)) return true;
            foreach (var arg in cmpTarget.Arguments)
                if (Walk(arg, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: false)) return true;
            return Walk(cmpStmt.Value, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: false);
        }
        if (expr is IIncrementOrDecrementOperation { Target: IPropertyReferenceOperation incTarget }
            && matchesAccessor(incTarget, getter: false) && !matchesAccessor(incTarget, getter: true))
        {
            if (Walk(incTarget.Instance, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: false)) return true;
            foreach (var arg in incTarget.Arguments)
                if (Walk(arg, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: false)) return true;
            return false;
        }
        if (expr is IConditionalAccessOperation condAcc)
        {
            if (Walk(condAcc.Operation, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: false)) return true;
            return TailStatement(condAcc.WhenNotNull, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn);
        }
        return Walk(expr, isMatch, matchesAccessor, checkReturnInstanceLeg, ternaryPreciseReturn, tail: false);
    }
}
