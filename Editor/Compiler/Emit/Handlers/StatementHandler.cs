using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class StatementHandler : HandlerBase, IOperationHandler
{
    public StatementHandler(EmitContext ctx) : base(ctx) { }

    public bool CanHandle(IOperation operation)
        => operation is IBlockOperation
            or IExpressionStatementOperation
            or IVariableDeclarationGroupOperation
            or IConditionalOperation
            or IReturnOperation
            or IBranchOperation
            or ILabeledOperation
            or ILocalFunctionOperation
            or IUsingOperation
            or IUsingDeclarationOperation
            or IEmptyOperation;

    public void Handle(IOperation operation)
    {
        switch (operation)
        {
            case IBlockOperation op: HandleBlock(op); break;
            case IExpressionStatementOperation exprStmt
                when exprStmt.Operation is IDeconstructionAssignmentOperation deconExpr:
                _ctx.VisitOperation(deconExpr);
                break;
            case IExpressionStatementOperation exprStmt:
            {
                var innerOp = exprStmt.Operation;
                // Assignment/increment handlers already emit their side effects during VisitExpression.
                // Only emit as ExprStmt if the expression is purely for its side effects (method call, etc.)
                if (innerOp is ISimpleAssignmentOperation
                    or ICompoundAssignmentOperation
                    or IIncrementOrDecrementOperation
                    or ICoalesceAssignmentOperation
                    or IDeconstructionAssignmentOperation)
                {
                    VisitExpression(innerOp);
                }
                else
                {
                    var expr = VisitExpression(innerOp);
                    if (expr != null)
                        EmitExprStmt(expr);
                }
                break;
            }
            case IVariableDeclarationGroupOperation declGroup:
                foreach (var decl in declGroup.Declarations)
                    VisitVariableDeclaration(decl);
                break;
            case IConditionalOperation op: VisitConditional(op); break;
            case IReturnOperation op: VisitReturn(op); break;
            case IBranchOperation op: VisitBranch(op); break;
            // Round-9 [Y8]: generic local functions are monomorphized per call site
            // (RegisterGenericSpecialization), exactly like generic methods — registering the
            // DEFINITION here declared 'T'-typed param/return heap vars, and the spec body
            // emission bound THOSE instead of the spec's own (CReturn type-mismatch ICE on a
            // single legal instantiation). Non-generic local functions still register at the
            // declaration so declaration-first shapes keep their index allocation order.
            case ILocalFunctionOperation op:
                if (!op.Symbol.IsGenericMethod)
                    RegisterLocalFunction(op.Symbol);
                break;
            case ILabeledOperation labeled:
                _builder.EmitLabel(labeled.Label.Name);
                if (labeled.Operation != null)
                    VisitOperation(labeled.Operation);
                break;
            // An empty statement (`;`), e.g. a labeled empty target `Outer:;` used as a goto landing pad — no-op.
            case IEmptyOperation: break;
            case IUsingOperation op: VisitUsing(op); break;
            case IUsingDeclarationOperation usingDecl:
                foreach (var decl in usingDecl.DeclarationGroup.Declarations)
                {
                    VisitVariableDeclaration(decl);
                    foreach (var declarator in decl.Declarators)
                    {
                        var localId = _localBindings.TryGetValue(declarator.Symbol, out var ub) ? ub.Id : declarator.Symbol.Name;
                        var localType = GetUdonType(declarator.Symbol.Type);
                        _usingDisposableStack.Peek().Add((LoadField(localId, localType), declarator.Symbol.Type));
                    }
                }
                break;
            default: throw new System.NotSupportedException(operation.GetType().Name);
        }
    }

    void HandleBlock(IBlockOperation block)
    {
        _usingDisposableStack.Push(new List<(CLeaf, ITypeSymbol)>());
        foreach (var stmt in block.Operations)
            VisitOperation(stmt);
        var disposables = _usingDisposableStack.Pop();
        for (int i = disposables.Count - 1; i >= 0; i--)
        {
            var (val, type) = disposables[i];
            EmitDispose(val, type);
        }
    }

    void VisitConditional(IConditionalOperation op)
    {
        // Optimization: if (!cond) → invert branches to avoid negation extern
        if (op.Condition is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } unary)
        {
            var condVal = VisitExpression(unary.Operand);

            if (op.WhenFalse != null)
            {
                // if (!c) A else B → if (c) B else A
                _builder.EmitIf(condVal,
                    _ => VisitOperation(op.WhenFalse),
                    _ => VisitOperation(op.WhenTrue));
            }
            else
            {
                // if (!c) A → if (c) {} else A
                _builder.EmitIf(condVal,
                    _ => { },
                    _ => VisitOperation(op.WhenTrue));
            }
            return;
        }

        var condVal2 = VisitExpression(op.Condition);

        if (op.WhenFalse != null)
        {
            _builder.EmitIf(condVal2,
                _ => VisitOperation(op.WhenTrue),
                _ => VisitOperation(op.WhenFalse));
        }
        else
        {
            _builder.EmitIf(condVal2,
                _ => VisitOperation(op.WhenTrue));
        }
    }

    void VisitReturn(IReturnOperation op)
    {
        // §2.8(b): returning a capturing lambda (or a tainted-equivalent value) escapes the
        // flat-capture model — loud compile error in Stage 1 (fcd36 stays rejected; closure
        // environments arrive in Stage 2). Returning a delegate-typed PARAM stays legal
        // (identity flow — the caller's invocation-result taint guards a laundered result).
        if (op.ReturnedValue != null)
            GuardCaptureEscapeReturn(op.ReturnedValue);

        // Tail call optimization: return self(args) → overwrite params + goto entry.
        // Wave-9 round-8 [Y3]: TCO is only sound when every ref/out arg threads the SAME parameter
        // (param→param is an identity rebind under the shared flat heap). A re-chained ref/out
        // (`return M(m - 1, ref w);`) rode this arm straight past GuardRefOutArguments — the snapshot
        // loop treated `ref w` as a value arg, so every frame's writes threaded one param cell and the
        // outer copy-back read the innermost value (VM-proven 21021 vs CLR 9021). Fall through to the
        // ordinary call path, whose unfiltered cycle-edge guard rejects the re-chain loudly (§8-3).
        // Round-9: TCO must compare against the RESOLVED dispatch target, not the syntactic one.
        // Inside a BASE-COPY body a virtual self-call's semantic TargetMethod IS the base method
        // (== _currentMethod), but C# dispatches the compiled class's LEAF override — the goto
        // short-circuited the override (each "recursive" frame ran only the base body; VM-proven
        // outv 363 vs CLR 528 at depth 30 once the cross-method ref-thread acceptance let the
        // shape compile). The leaf's own self-call resolves to itself, keeping TCO byte-identical
        // for every non-base-copy shape.
        if (op.ReturnedValue is IInvocationOperation tailCall
            && _currentMethod != null
            && SymbolEqualityComparer.Default.Equals(tailCall.TargetMethod, _currentMethod)
            && (tailCall.Instance is not IInstanceReferenceOperation
                || tailCall.TargetMethod.IsStatic
                || !(tailCall.TargetMethod.IsVirtual || tailCall.TargetMethod.IsOverride
                     || tailCall.TargetMethod.IsAbstract)
                || SymbolEqualityComparer.Default.Equals(
                    ResolveMostDerivedOverride(tailCall.TargetMethod), _currentMethod))
            && TailCallRefArgsSelfThreaded(tailCall))
        {
            EmitTailCall(tailCall);
            return;
        }

        if (op.ReturnedValue != null && _currentMethod != null
            && _methodReturns.TryGetValue(_currentMethod, out var retSlots) && retSlots.Length > 0)
        {
            // All returns are single-value (aggregates are SystemObjectArray)
            var srcVal = VisitExpression(op.ReturnedValue);
            if (_currentMethod.Name == "OnOwnershipRequest")
            {
                _ctx.TryDeclareVar("__returnValue", "SystemBoolean");
                EmitStoreField("__returnValue", srcVal);
            }
            EmitPendingDispose();
            EmitReturn(srcVal);
            return;
        }
        else
        {
            EmitPendingDispose();
            EmitReturn();
        }
    }

    /// <summary>[Y3]: true when every ref/out argument of a self tail call is the callee parameter
    /// itself (self-threading) — the only shape TCO's param-rebind preserves. Calls without ref/out
    /// args trivially qualify (the common TCO case, byte-identical).</summary>
    bool TailCallRefArgsSelfThreaded(IInvocationOperation call)
    {
        foreach (var arg in call.Arguments)
        {
            var p = arg.Parameter;
            if (p == null || (p.RefKind != RefKind.Ref && p.RefKind != RefKind.Out)) continue;
            if (UnwrapConversions(arg.Value) is not IParameterReferenceOperation apr
                || !SymbolEqualityComparer.Default.Equals(
                    apr.Parameter.OriginalDefinition, p.OriginalDefinition))
                return false;
        }
        return true;
    }

    void EmitTailCall(IInvocationOperation tailCall)
    {
        var paramIds = _methodParamVarIds[_currentMethod];

        // Snapshot every arg into a temp BEFORE overwriting any param. VisitExpression returns a lazy expr
        // that reads its operand slots when lowered, not a materialized value — so storing param i first
        // would corrupt a later arg that reads param i (e.g. `return Count(n-1, acc+n)` must use the OLD n
        // for `acc+n`). EmitAssign forces evaluation now, against the pre-overwrite param values.
        var argSlots = new int[tailCall.Arguments.Length];
        for (int i = 0; i < tailCall.Arguments.Length; i++)
        {
            var argVal = VisitExpression(tailCall.Arguments[i].Value);
            var slot = _ctx.AllocTemp(GetUdonType(tailCall.Arguments[i].Value.Type));
            EmitAssign(slot, argVal);
            argSlots[i] = slot;
        }

        // Overwrite param vars from the snapshots
        for (int i = 0; i < tailCall.Arguments.Length; i++)
            EmitStoreField(paramIds[i], SlotRef(argSlots[i]));

        // Jump back to method entry via goto label
        var func = _methodFunctions[_currentMethod];
        _builder.EmitGoto($"__tco_{func.Name}");
    }

    void VisitBranch(IBranchOperation op)
    {
        if (op.BranchKind == BranchKind.Break)
        {
            EmitPendingDisposeForBreakContinue();
            // Switch breaks use goto to end label; loop breaks use structured CBreak
            if (_ctx.SwitchBreakLabels.Count > 0 && _ctx.SwitchBreakLabels.Peek() != null)
                _builder.EmitGoto(_ctx.SwitchBreakLabels.Peek());
            else
                _builder.EmitBreak();
        }
        else if (op.BranchKind == BranchKind.Continue)
        {
            EmitPendingDisposeForBreakContinue();
            _builder.EmitContinue();
        }
        else if (op.BranchKind == BranchKind.GoTo)
        {
            // Wave-9 round-2 [W4]: a goto that leaves one or more using scopes must run their
            // Dispose()s first (C# lowers using to try/finally; the finally runs on EVERY exit
            // edge — break/continue/return were covered, the goto edge was not: VM-proven
            // ref total=14/mark=1 vs 4/0, nested usings skipped BOTH disposes).
            EmitPendingDisposeForGoto(op);
            // goto case <const>; / goto default; target a Roslyn label ("case 2:", "default") that is not a
            // valid UASM token — the enclosing switch maps it to a sanitized landing label. A plain user goto
            // (its label is emitted verbatim by ILabeledOperation) is not in the map and uses its own name.
            var target = _ctx.GotoCaseLabels.Count > 0 && _ctx.GotoCaseLabels.Peek().TryGetValue(op.Target.Name, out var mapped)
                ? mapped : op.Target.Name;
            _builder.EmitGoto(target);
        }
        else
        {
            throw new System.InvalidOperationException(
                $"Unresolved branch: {op.BranchKind}"
              + (op.BranchKind == BranchKind.GoTo ? $" to '{op.Target?.Name}'" : "")
              + ". No matching label on the stack.");
        }
    }

    /// <summary>
    /// Emit Dispose() for all active using disposables (innermost scope first).
    /// Called before return to ensure all scopes are cleaned up.
    /// </summary>
    void EmitPendingDispose()
    {
        foreach (var scope in _usingDisposableStack)
        {
            for (int i = scope.Count - 1; i >= 0; i--)
            {
                var (val, type) = scope[i];
                EmitDispose(val, type);
            }
        }
    }

    /// <summary>
    /// Emit Dispose() only for using scopes inside the current loop/switch.
    /// Called before break/continue to clean up scopes that will be exited.
    /// </summary>
    void EmitPendingDisposeForBreakContinue()
    {
        var loopDepth = _ctx.LoopUsingDepthStack.Count > 0
            ? _ctx.LoopUsingDepthStack.Peek()
            : 0;
        var currentDepth = _usingDisposableStack.Count;
        var scopesToDispose = currentDepth - loopDepth;
        if (scopesToDispose <= 0) return;

        int count = 0;
        foreach (var scope in _usingDisposableStack)
        {
            if (count >= scopesToDispose) break;
            for (int i = scope.Count - 1; i >= 0; i--)
            {
                var (val, type) = scope[i];
                EmitDispose(val, type);
            }
            count++;
        }
    }

    /// <summary>
    /// Wave-9 round-2 [W4]: emit Dispose() for every using scope a goto exits — the scopes between the
    /// goto and its target label. C# scoping guarantees the label lives in the same or an ENCLOSING
    /// block, so the exited scopes are exactly the innermost N entries of <c>_usingDisposableStack</c>,
    /// where N counts the Block/UsingStatement syntax ancestors of the goto that do NOT contain the
    /// label (each braced block pushes one scope in HandleBlock, each using statement one in VisitUsing —
    /// same order, innermost first). A goto whose label is inside every enclosing scope disposes nothing
    /// (jumping within one try region); `goto case` labels resolve through the switch the same way.
    /// </summary>
    void EmitPendingDisposeForGoto(IBranchOperation op)
    {
        if (op.Syntax == null || op.Target == null) return;
        Microsoft.CodeAnalysis.Text.TextSpan labelSpan;
        var labelRef = op.Target.DeclaringSyntaxReferences.FirstOrDefault();
        if (labelRef != null && labelRef.SyntaxTree == op.Syntax.SyntaxTree)
            labelSpan = labelRef.Span;
        else
        {
            var loc = op.Target.Locations.FirstOrDefault(l => l.IsInSource && l.SourceTree == op.Syntax.SyntaxTree);
            if (loc == null) return; // cannot locate the label — conservative no-op (pre-fix behavior)
            labelSpan = loc.SourceSpan;
        }

        int scopesToDispose = 0;
        bool found = false;
        for (var node = op.Syntax.Parent; node != null; node = node.Parent)
        {
            if (node is Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.UsingStatementSyntax)
            {
                if (node.Span.Contains(labelSpan)) { found = true; break; }
                scopesToDispose++;
                continue;
            }
            // Function boundary: a goto can never cross it — defensive stop.
            if (node is Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.AccessorDeclarationSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.AnonymousFunctionExpressionSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.LocalFunctionStatementSyntax)
                break;
        }
        if (!found || scopesToDispose <= 0) return;

        int count = 0;
        foreach (var scope in _usingDisposableStack)
        {
            if (count >= scopesToDispose) break;
            for (int i = scope.Count - 1; i >= 0; i--)
            {
                var (val, type) = scope[i];
                EmitDispose(val, type);
            }
            count++;
        }
    }

    void VisitUsing(IUsingOperation op)
    {
        // Collect declared locals (for Dispose calls after body)
        var disposableVars = new List<(CLeaf val, ITypeSymbol type)>();
        if (op.Resources is IVariableDeclarationGroupOperation declGroup)
        {
            foreach (var decl in declGroup.Declarations)
            {
                VisitVariableDeclaration(decl);
                foreach (var declarator in decl.Declarators)
                {
                    var localId = _localBindings.TryGetValue(declarator.Symbol, out var ub2) ? ub2.Id : declarator.Symbol.Name;
                    var localType = GetUdonType(declarator.Symbol.Type);
                    disposableVars.Add((LoadField(localId, localType), declarator.Symbol.Type));
                }
            }
        }
        else if (op.Resources != null)
        {
            var resourceVal = VisitExpression(op.Resources);
            disposableVars.Add((resourceVal, op.Resources.Type));
        }

        // Push onto using stack so early exit (return/break/continue) can emit Dispose
        _usingDisposableStack.Push(disposableVars);

        if (op.Body != null)
            VisitOperation(op.Body);

        _usingDisposableStack.Pop();

        // Emit Dispose() in reverse declaration order (no try/finally in Udon)
        for (int i = disposableVars.Count - 1; i >= 0; i--)
        {
            var (val, type) = disposableVars[i];
            EmitDispose(val, type);
        }
    }

    /// <summary>Emit a using resource's Dispose(). A user struct is object[]-backed, so its Udon type is
    /// SystemObjectArray which has no Dispose extern; route through a JUMP to the struct's registered
    /// Dispose method (collected in CollectStructMethodsInOperation). Real Udon disposables keep the extern.</summary>
    void EmitDispose(CLeaf val, ITypeSymbol type)
    {
        if (type is INamedTypeSymbol nt && EmitContext.IsUserStruct(nt)
            && EmitContext.FindStructDisposeMethod(nt) is { } dispose)
        {
            EmitCallToMethod(dispose, new List<CLeaf> { val });
            return;
        }
        EmitExternVoid($"{GetUdonType(type)}.__Dispose__SystemVoid", new List<CLeaf> { val });
    }

    void VisitVariableDeclaration(IVariableDeclarationOperation decl)
    {
        foreach (var declarator in decl.Declarators)
        {
            var local = declarator.Symbol;

            // ref locals (round 7, §8-3 loud): the flat-heap VM has no variable aliases, so
            // `ref int r = ref x` can only emit as a VALUE copy — every flavor silently decouples
            // (VM-proven: write-through 1 vs CLR 5, read-through 1 vs 5, array element 0 vs 7,
            // struct member 0 vs 9, delegate 2 vs 11). ref/out PARAMS stay legal (caller-side
            // copy-back convention, struct_ref_param-pinned).
            if (local.IsRef)
                throw new System.NotSupportedException(
                    $"ref local '{local.Name}' is not supported: the flat-heap Udon VM has no "
                    + "variable aliases, so a ref local would silently degrade to a value copy. "
                    + "Use the referenced variable directly, or index the array element instead.");

            // Aggregate-typed local (tuple / user-defined struct) → object[] emulation
            if (local.Type is INamedTypeSymbol namedType && EmitContext.IsAggregateType(namedType))
            {
                VisitAggregateLocalDeclaration(local, namedType, declarator.Initializer);
                continue;
            }

            // Delegate-typed locals are SystemObjectArray bundle references via the type-map delegate arm
            // (design §2.1); the initializer's VisitDelegateCreation hoists any lambda and builds the bundle.
            var udonType = GetUdonType(local.Type);
            var id = _ctx.DeclareLocal(local.Name, udonType);
            _localBindings[local] = new EmitContext.LocalBinding(id);

            var init = declarator.Initializer;
            if (init != null)
            {
                // F3 backstop: a capturing lambda buried in a composite initializer (ternary/coalesce/
                // switch arm) evades the direct-shape taint below — loud reject.
                GuardBuriedCapturingLambda(init.Value);

                // §2.8(b): a capturing lambda initializing a local TAINTS it (flow-insensitive); an
                // object-typed local is itself an escaping store and is rejected loudly. A tainted-local
                // read taints the new local too (F4: copies must not launder the taint), as do the
                // laundering shapes: a tainted delegate-capable invocation result (`var t = Id(()=>v);`),
                // a delegate-capable param read (`var t = x;` inside the callee), and a laundering
                // member read (recipient member / param-rooted envelope member, §2.8 round-2).
                if (IsDirectCapturingLambda(init.Value) || IsCaptureTaintedRead(init.Value)
                    || IsTaintedDelegateInvocationResult(init.Value) || IsDelegateParamRead(init.Value)
                    || IsLaunderingMemberRead(init.Value))
                {
                    if (IsObjectish(local.Type))
                        throw new System.NotSupportedException(CaptureEscapeError);
                    RegisterLocalTaint(local, init.Value); // [X9] tiered backstop
                    // Wave-9 [W1]: a declaration is always inside its initializer's loop, so no
                    // reject here — record the fragility so copies of this local are checked
                    // (redundant backstop; the pre-scan computes the same set order-independently).
                    _ctx.AddIterationFragileLoops(local, _ctx.GetPerIterationCaptureLoops(init.Value));
                }

                var srcVal = VisitExpression(init.Value);
                EmitStoreField(id, srcVal);
            }
        }
    }

    void VisitAggregateLocalDeclaration(ILocalSymbol local, INamedTypeSymbol aggregateType,
        IVariableInitializerOperation init)
    {
        var layout = _ctx.GetAggregateLayout(aggregateType);

        // Declare as SystemObjectArray. Always REdeclare + REallocate, mirroring the scalar path:
        // _localBindings is keyed by ILocalSymbol and shared across generic-spec emissions of the
        // same definition body, so a reuse guard here made the SECOND specialization skip both the
        // declaration and the object[] ctor — every spec-2 activation then aliased spec-1's one
        // array and per-frame struct locals broke under recursion (wave-9 round-5 [X7], VM-proven
        // 183 vs 126 in the second instantiation).
        var id = _ctx.DeclareLocal(local.Name, "SystemObjectArray");
        _localBindings[local] = new EmitContext.LocalBinding(id);

        // Create object[] of correct size
        var arrExpr = ExternCall("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray",
            new List<CLeaf> { Const(layout.Count, "SystemInt32") }, "SystemObjectArray");
        EmitStoreField(id, arrExpr);

        var localId = id;
        if (init == null)
        {
            // No initializer (`Outer n;`): C# definite-assignment permits field writes before any read.
            // The flat array allocated above is NOT enough for a NESTED struct — its inner struct-typed
            // fields must be recursively allocated (exactly like default(T)/new T()), or a write to a
            // nested field (`n.inner.x = …`) hits a null sub-array and faults the real VM. (diff-fuzz w2)
            DefaultInitAggregate(localId, layout);
            return;
        }

        // §2.8 round-2 (H2): aggregate-typed locals `continue` past the scalar declaration guard
        // block in VisitVariableDeclaration, so this path needs its own capture-escape guard — a
        // tuple literal carrying a capturing lambda otherwise escapes into the backing object[]
        // (an unguarded __Set) and launders out via a member read (VM-verified wrong values).
        // Composite/tuple-literal shapes go loud via the buried-lambda walk; tainted-equivalent
        // aggregate initializers (param read / tainted local / tainted invocation result) taint
        // the local so escaping reads of it (or its members, via the tainted-root member rule)
        // stay loud.
        GuardBuriedCapturingLambda(init.Value);
        if (IsCaptureTaintedRead(init.Value) || IsTaintedDelegateInvocationResult(init.Value)
            || IsDelegateParamRead(init.Value) || IsLaunderingMemberRead(init.Value))
            RegisterLocalTaint(local, init.Value); // [X9] tiered backstop

        var value = UnwrapConversions(init.Value);

        if (value is ITupleOperation tupleLit)
        {
            // Tuple literal: set each element via __Set__
            for (int i = 0; i < tupleLit.Elements.Length && i < layout.Count; i++)
            {
                // §2.8 round-2 (H2): guard each element exactly like an array-initializer element
                // (ArrayHandler) — the backing store is the same escaping object[] shape.
                GuardCaptureEscapeValue(tupleLit.Elements[i]);
                EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
                    new List<CLeaf> { LoadField(localId, "SystemObjectArray"), Const(i, "SystemInt32"),
                        VisitExpression(tupleLit.Elements[i]) });
            }
        }
        else if (value is IDefaultValueOperation)
        {
            DefaultInitAggregate(localId, layout);
        }
        else if (value is IObjectCreationOperation ocCtor && ocCtor.Arguments.Length > 0
                 && EmitContext.IsUserStruct(aggregateType) && ocCtor.Constructor != null
                 && _methodFunctions.ContainsKey(ocCtor.Constructor))
        {
            // new V(args): default-init the already-allocated array, then run the registered ctor
            // (receiver = this array, mutated in place via this.field = … in the ctor body).
            // §2.8 round-2: erasing-typed ctor args are guarded like any call boundary.
            GuardCaptureEscapeArguments(ocCtor.Arguments);
            DefaultInitAggregate(localId, layout);
            var ctorArgs = new List<CLeaf> { LoadField(localId, "SystemObjectArray") };
            foreach (var arg in ocCtor.Arguments)
                ctorArgs.Add(VisitExpression(arg.Value));
            EmitExprStmt(EmitCallToMethod(ocCtor.Constructor, ctorArgs));
        }
        else if (value is IObjectCreationOperation oc && oc.Arguments.Length == 0)
        {
            // new V() / new V { field = ... }: the array is already allocated above; value-type
            // fields need 0/false/etc., then apply any object-initializer assignments. (A parameterless
            // struct ctor's VisitObjectCreation returns a null placeholder, so handle creation here.)
            DefaultInitAggregate(localId, layout);
            if (oc.Initializer != null)
            {
                foreach (var member in oc.Initializer.Initializers)
                {
                    if (member is not ISimpleAssignmentOperation sa) continue;
                    // Field or auto-property (incl. init) target → object[] element by member name.
                    var memberName = sa.Target switch
                    {
                        IFieldReferenceOperation fr => fr.Field.Name,
                        IPropertyReferenceOperation pr => pr.Property.Name,
                        _ => null,
                    };
                    if (memberName != null && layout.TryGetIndex(memberName, out var idx))
                    {
                        // §2.8 round-3 [C]: object-initializer member stores are escaping stores
                        // into the backing object[] — guard each value exactly like an
                        // array-initializer element (raw __Set used to bypass the guard cluster;
                        // VM-verified laundering). The whole-value buried-lambda walk above
                        // already rejects most shapes; this is the per-member backstop.
                        GuardCaptureEscapeValue(sa.Value);
                        EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
                            new List<CLeaf> { LoadField(localId, "SystemObjectArray"),
                                Const(idx, "SystemInt32"), VisitExpression(sa.Value) });
                    }
                }
            }
        }
        else
        {
            // Method return, other local, etc.
            // VisitExpression clones aggregate locals/params automatically (Clone-on-read).
            var srcVal = VisitExpression(init.Value);
            EmitStoreField(localId, srcVal);
        }
    }

    /// <summary>Default-initialize an object[]-emulated aggregate local (delegates to the shared
    /// recursive HandlerBase helper).</summary>
    void DefaultInitAggregate(string localId, AggregateLayout layout)
        => EmitDefaultInitAggregate(LoadField(localId, "SystemObjectArray"), layout);

}
