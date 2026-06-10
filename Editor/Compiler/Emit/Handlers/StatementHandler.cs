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
            case ILocalFunctionOperation op: RegisterLocalFunction(op.Symbol); break;
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
        // Tail call optimization: return self(args) → overwrite params + goto entry
        if (op.ReturnedValue is IInvocationOperation tailCall
            && _currentMethod != null
            && SymbolEqualityComparer.Default.Equals(tailCall.TargetMethod, _currentMethod))
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

    public void PreScanGotoLabels(IOperation op)
    {
        // In the Core IR, labels are string-based (EmitLabel/EmitGoto).
        // No pre-scan needed — labels are resolved by name at lowering time.
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

            // Aggregate-typed local (tuple / user-defined struct) → object[] emulation
            if (local.Type is INamedTypeSymbol namedType && EmitContext.IsAggregateType(namedType))
            {
                VisitAggregateLocalDeclaration(local, namedType, declarator.Initializer);
                continue;
            }

            // Delegate-typed locals → SystemUInt32 (holds label address; Udon has no delegate types)
            var udonType = local.Type.TypeKind == TypeKind.Delegate
                ? "SystemUInt32"
                : GetUdonType(local.Type);
            var id = _ctx.DeclareLocal(local.Name, udonType);
            _localBindings[local] = new EmitContext.LocalBinding(id);

            var init = declarator.Initializer;
            if (init != null)
            {
                // Track delegate variable → hoisted method mapping
                if (init.Value is IDelegateCreationOperation delegateInit
                    && delegateInit.Target is IAnonymousFunctionOperation lambdaInit)
                {
                    var hoisted = HoistLambdaToMethod(lambdaInit);
                    _delegateVarMap[local] = hoisted;
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

        // Declare as SystemObjectArray
        if (!_localBindings.ContainsKey(local))
        {
            var id = _ctx.DeclareLocal(local.Name, "SystemObjectArray");
            _localBindings[local] = new EmitContext.LocalBinding(id);

            // Create object[] of correct size
            var arrExpr = ExternCall("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray",
                new List<CLeaf> { Const(layout.Count, "SystemInt32") }, "SystemObjectArray");
            EmitStoreField(id, arrExpr);
        }

        var localId = _localBindings[local].Id;
        if (init == null)
        {
            // No initializer (`Outer n;`): C# definite-assignment permits field writes before any read.
            // The flat array allocated above is NOT enough for a NESTED struct — its inner struct-typed
            // fields must be recursively allocated (exactly like default(T)/new T()), or a write to a
            // nested field (`n.inner.x = …`) hits a null sub-array and faults the real VM. (diff-fuzz w2)
            DefaultInitAggregate(localId, layout);
            return;
        }
        var value = UnwrapConversions(init.Value);

        if (value is ITupleOperation tupleLit)
        {
            // Tuple literal: set each element via __Set__
            for (int i = 0; i < tupleLit.Elements.Length && i < layout.Count; i++)
                EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
                    new List<CLeaf> { LoadField(localId, "SystemObjectArray"), Const(i, "SystemInt32"),
                        VisitExpression(tupleLit.Elements[i]) });
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
                        EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
                            new List<CLeaf> { LoadField(localId, "SystemObjectArray"),
                                Const(idx, "SystemInt32"), VisitExpression(sa.Value) });
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
