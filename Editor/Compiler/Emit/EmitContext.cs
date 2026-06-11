using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class AggregateLayout
{
    public readonly struct FieldInfo
    {
        public readonly string Name;
        public readonly int Index;
        public readonly ITypeSymbol Type;
        public FieldInfo(string name, int index, ITypeSymbol type)
        { Name = name; Index = index; Type = type; }
    }

    public readonly IReadOnlyList<FieldInfo> Fields;
    readonly Dictionary<string, int> _nameToIndex;

    public int Count => Fields.Count;

    public bool TryGetIndex(string fieldName, out int index)
        => _nameToIndex.TryGetValue(fieldName, out index);

    public bool TryGetIndex(IFieldSymbol field, out int index)
    {
        if (_nameToIndex.TryGetValue(field.Name, out index)) return true;
        if (field.CorrespondingTupleField != null
            && _nameToIndex.TryGetValue(field.CorrespondingTupleField.Name, out index)) return true;
        // Reverse: check if any layout field's CorrespondingTupleField matches
        return false;
    }

    AggregateLayout(IReadOnlyList<FieldInfo> fields, Dictionary<string, int> nameToIndex)
    { Fields = fields; _nameToIndex = nameToIndex; }

    public static AggregateLayout Build(INamedTypeSymbol type)
    {
        var fields = new List<FieldInfo>();
        var nameToIndex = new Dictionary<string, int>();

        if (type.IsTupleType)
        {
            var elements = type.TupleElements;
            for (int i = 0; i < elements.Length; i++)
            {
                var name = elements[i].Name;
                fields.Add(new FieldInfo(name, i, elements[i].Type));
                nameToIndex[name] = i;
                var itemName = $"Item{i + 1}";
                if (name != itemName) nameToIndex[itemName] = i;
                if (elements[i].CorrespondingTupleField != null)
                {
                    var corrName = elements[i].CorrespondingTupleField.Name;
                    if (!nameToIndex.ContainsKey(corrName)) nameToIndex[corrName] = i;
                }
            }
        }
        else if (type.TypeKind == TypeKind.Struct)
        {
            // User struct → instance fields mapped to indices in declaration order. Auto-property backing
            // fields are implicitly declared but carry the property as AssociatedSymbol; map them by the
            // property name so `get`/`set`/`init` resolve to the same object[] element.
            int i = 0;
            foreach (var member in type.GetMembers())
            {
                if (member is not IFieldSymbol { IsStatic: false, IsConst: false } f) continue;
                if (!f.IsImplicitlyDeclared)
                {
                    fields.Add(new FieldInfo(f.Name, i, f.Type));
                    nameToIndex[f.Name] = i++;
                }
                else if (f.AssociatedSymbol is IPropertySymbol prop)
                {
                    fields.Add(new FieldInfo(prop.Name, i, f.Type));
                    nameToIndex[prop.Name] = i++;
                }
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"AggregateLayout.Build called on non-aggregate type '{type.Name}'");
        }

        return new AggregateLayout(fields.AsReadOnly(), nameToIndex);
    }
}

public class EmitContext
{
    // Core dependencies
    public readonly Compilation Compilation;
    public readonly INamedTypeSymbol ClassSymbol;
    public readonly CModule Module;
    public readonly CoreBuilder Builder;
    public readonly LayoutPlanner Planner;

    // Method bookkeeping
    public readonly Dictionary<IMethodSymbol, CFunction> MethodFunctions = new(SymbolEqualityComparer.Default);
    public readonly struct MethodSlot
    {
        public readonly int Index;
        public readonly string VarPrefix;
        public MethodSlot(int index, string varPrefix) { Index = index; VarPrefix = varPrefix; }
    }

    public readonly Dictionary<IMethodSymbol, MethodSlot> MethodSlots = new(SymbolEqualityComparer.Default);

    public MethodSlot RegisterMethod(IMethodSymbol method, Func<int, string> prefixFactory)
    {
        var idx = NextMethodIndex++;
        var slot = new MethodSlot(idx, prefixFactory(idx));
        MethodSlots[method] = slot;
        return slot;
    }
    /// <summary>Per-method return slots. Empty array for void. Length 1 for scalar. Length N for tuple.</summary>
    public readonly Dictionary<IMethodSymbol, ReturnSlot[]> MethodReturns = new(SymbolEqualityComparer.Default);
    public readonly Dictionary<IMethodSymbol, string[]> MethodParamVarIds = new(SymbolEqualityComparer.Default);
    public IMethodSymbol CurrentMethod;

    /// <summary>When emitting a user-struct method/ctor, the receiver object[] param var id; otherwise null.
    /// Makes <c>this</c> / <c>this.field</c> resolve to the receiver array instead of the Behaviour.</summary>
    public string CurrentStructReceiverParamId;

    /// <summary>For each internal method, the set of callees that lie in the same strongly-connected
    /// component (i.e. calls that can re-enter the caller). Calls along these edges must spill the
    /// caller's live values to the software stack, because Udon's flat heap shares param/local slots
    /// across call frames. Populated by <c>UasmEmitter.BuildRecursionInfo</c> before emit.</summary>
    public Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> RecursiveCallees;

    /// <summary>True when a call from <paramref name="caller"/> to <paramref name="callee"/> is a
    /// recursion-cycle edge (callee in caller's non-trivial SCC, including direct self-recursion).</summary>
    public bool IsRecursiveEdge(IMethodSymbol caller, IMethodSymbol callee)
        => caller != null && callee != null && RecursiveCallees != null
           // Reduce BOTH ends to OriginalDefinition: RecursiveCallees is keyed by definition, but a
           // monomorphized generic specialization (e.g. Fact<int>) emits with the constructed symbol as
           // _currentMethod/target — without this its self-edge would be missed and the frame not spilled.
           && RecursiveCallees.TryGetValue(caller.OriginalDefinition, out var callees)
           && callees.Contains(callee.OriginalDefinition);

    /// <summary>Wave-9 round-4 [X2]/[X3]: per HOISTED recursion-cycle member (lambda / local
    /// function symbol), the read-only capture cells (locals/params of an enclosing function in
    /// the SAME SCC) that must join the node's frame spill at its marked dispatch/recursive-call
    /// sites. A dispatch from inside the node can re-enter the cell's declaring function, whose
    /// fresh activation re-seeds the one flat slot — the node's post-dispatch read then sees the
    /// inner value (DiffFuzz ref=60 vs VM 50). Cells WRITTEN by any hoisted node are excluded
    /// (same-environment mutation must stay visible through the shared slot). Populated by
    /// <c>UasmEmitter.BuildRecursionInfo</c>; consumed by CollectRecursionSpillFields.</summary>
    public readonly Dictionary<IMethodSymbol, List<ISymbol>> HoistedCaptureSpillCells
        = new(SymbolEqualityComparer.Default);

    /// <summary>Round-7 follow-up [Q5]: per internal method (keyed by OriginalDefinition), the
    /// this-FIELDS the method touches — directly (field reference through an implicit/explicit
    /// this/base receiver anywhere in its body) or transitively (closed over the internal call
    /// graph, including this-property accessor edges and the synthetic dispatch edges —
    /// conservative, §8-3). A ref/out argument rooted at a this-field hands the callee an alias
    /// of storage it can also reach directly; the caller-side copy-in/copy-back convention
    /// snapshots it (callee param reads go stale, callee direct field writes are reverted by the
    /// stale copy-back — VM-proven 19 vs CLR 59). Populated by UasmEmitter.BuildRecursionInfo;
    /// consulted by EmitUserMethodCall's ref/out-argument guard. Non-touching callees
    /// (Inc(ref field) / Swap(ref a, ref b)) stay legal.</summary>
    public Dictionary<IMethodSymbol, HashSet<IFieldSymbol>> ThisFieldTouches;

    /// <summary>[Q5] True when <paramref name="callee"/>'s transitive touch set contains the
    /// this-field <paramref name="field"/> (both compared by OriginalDefinition).</summary>
    public bool CalleeTouchesThisField(IMethodSymbol callee, IFieldSymbol field)
        => callee != null && field != null && ThisFieldTouches != null
           && ThisFieldTouches.TryGetValue(callee.OriginalDefinition, out var set)
           && set.Contains(field.OriginalDefinition);

    /// <summary>Syntax nodes of delegate-dispatch invocations that can re-enter their containing
    /// function: the containing function lies on a synthetic-edge-inclusive SCC cycle AND the dispatch
    /// is non-tail (design §4.2/§4.3). Computed by <c>UasmEmitter.BuildRecursionInfo</c>; keyed by the
    /// invocation's red SYNTAX node because operation trees are NOT shared between the analysis and emit
    /// walks (each GetSemanticModel call builds a fresh operation tree) while red syntax nodes ARE shared.
    /// MEMBERSHIP-ONLY — never enumerated (§1.5 determinism).</summary>
    public HashSet<SyntaxNode> ReentrantDispatchSites;

    /// <summary>True if <paramref name="t"/> is <c>Nullable&lt;T&gt;</c>; yields the underlying T.
    /// Nullable is emulated as a boxed object (null | boxed T) — see ExternResolver type mapping.</summary>
    public static bool IsNullableT(ITypeSymbol t, out ITypeSymbol underlying)
    {
        if (t is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            underlying = n.TypeArguments[0];
            return true;
        }
        underlying = null;
        return false;
    }

    // ── Tail-call analysis (shared by named-method and recursive-lambda recursion detection) ──
    // A self-recursive call only needs spilling when it is NOT in tail position: a tail call reads nothing
    // of its frame afterwards, so the flat-heap clobber is harmless and deep tail recursion must not spill.

    /// <summary>Returns the call's argument list if <paramref name="op"/> is a self-recursive call to
    /// track, else default (and false via the out usage). Lets one tail walker serve named calls and
    /// delegate-variable invocations.</summary>
    public delegate bool SelfCallMatcher(IOperation op, out System.Collections.Immutable.ImmutableArray<IArgumentOperation> args);

    /// <summary>True if <paramref name="body"/> contains a NON-tail self-recursive call (per the matcher).
    /// Conditional (`cond ? a : self(..)`) branches count as tail positions; the condition does not.
    /// Wave-9 round-6 [X1]: STATEMENT-form tail positions count too — a void self-call that is the
    /// LAST statement executed before the function's implicit return (`M(m-1);` / `da(m-1);` as the
    /// final statement, including through nested blocks and if/else branches in tail position) reads
    /// nothing of its frame afterwards, exactly like `return M(m-1);`. Pre-fix these spilled every
    /// frame and overflowed the 512-entry __recurStack at depth (compile-clean VmFault on legal C#).</summary>
    public static bool HasNonTailSelfCall(IOperation body, SelfCallMatcher isSelf)
        => HasNonTailSelfCall(body, isSelf, tail: true);

    static bool HasNonTailSelfCall(IOperation body, SelfCallMatcher isSelf, bool tail)
    {
        if (body == null) return false;
        if (body is IReturnOperation ret) return NonTailInTailExpr(ret.ReturnedValue, isSelf);
        if (tail)
        {
            switch (body)
            {
                // Method/accessor bodies arrive as IMethodBodyOperation (block XOR expression body).
                case IMethodBodyBaseOperation mb:
                    return HasNonTailSelfCall(mb.BlockBody, isSelf, tail: true)
                        || HasNonTailSelfCall(mb.ExpressionBody, isSelf, tail: true);
                // Only a block's LAST statement stays in tail position.
                case IBlockOperation block:
                {
                    var ops = block.Operations;
                    for (int i = 0; i < ops.Length; i++)
                        if (HasNonTailSelfCall(ops[i], isSelf, tail: i == ops.Length - 1)) return true;
                    return false;
                }
                // A statement-form if/else in tail position: branches stay tail, the condition does not
                // (mirrors the expression-form conditional rule in NonTailInTailExpr). Loops, usings,
                // switches etc. deliberately fall through to the generic non-tail walk below — code
                // (back-edges, Dispose) runs after their last statement.
                case IConditionalOperation cond:
                    if (AnySelfCall(cond.Condition, isSelf)) return true;
                    return HasNonTailSelfCall(cond.WhenTrue, isSelf, tail: true)
                        || HasNonTailSelfCall(cond.WhenFalse, isSelf, tail: true);
                case IExpressionStatementOperation exprStmt:
                    return NonTailInTailStatement(exprStmt.Operation, isSelf);
            }
        }
        if (isSelf(body, out _)) return true; // self-call as a statement / non-tail position
        foreach (var child in body.Children)
        {
            if (child is ILocalFunctionOperation || child is IAnonymousFunctionOperation) continue;
            if (HasNonTailSelfCall(child, isSelf, tail: false)) return true;
        }
        return false;
    }

    /// <summary>The discarded-result twin of NonTailInTailExpr: classify the expression of a TAIL
    /// statement. A matched self-call is tail (only its argument/receiver subexpressions are non-tail);
    /// a call carrying ref/out arguments is NOT spared — its copy-back reads the param heap vars AFTER
    /// the call, and the tail classification also gates the [Q2] re-chained-ref reject.</summary>
    static bool NonTailInTailStatement(IOperation expr, SelfCallMatcher isSelf)
    {
        if (expr == null) return false;
        if (isSelf(expr, out var args)
            && (expr is not IInvocationOperation refInv
                || refInv.TargetMethod.Parameters.All(p => p.RefKind == RefKind.None)))
        {
            foreach (var a in args)
                if (AnySelfCall(a, isSelf)) return true;
            return AnySelfCall((expr as IInvocationOperation)?.Instance, isSelf);
        }
        return AnySelfCall(expr, isSelf);
    }

    static bool NonTailInTailExpr(IOperation expr, SelfCallMatcher isSelf)
    {
        if (expr == null) return false;
        if (isSelf(expr, out var args)) // a tail self-call; only its arguments are non-tail
        {
            foreach (var a in args)
                if (AnySelfCall(a, isSelf)) return true;
            return false;
        }
        if (expr is IConditionalOperation cond) // branches stay in tail position; the condition does not
        {
            if (AnySelfCall(cond.Condition, isSelf)) return true;
            return NonTailInTailExpr(cond.WhenTrue, isSelf) || NonTailInTailExpr(cond.WhenFalse, isSelf);
        }
        return AnySelfCall(expr, isSelf); // any self-call buried in a non-tail expression
    }

    static bool AnySelfCall(IOperation op, SelfCallMatcher isSelf)
    {
        if (op == null) return false;
        if (isSelf(op, out _)) return true;
        foreach (var child in op.Children)
        {
            if (child is ILocalFunctionOperation || child is IAnonymousFunctionOperation) continue;
            if (AnySelfCall(child, isSelf)) return true;
        }
        return false;
    }

    /// <summary>Generalized delegate-dispatch matcher: ANY-receiver delegate Invoke (design §4.2;
    /// the pre-§4 local-variable-only matcher was removed per deletion #12).</summary>
    public static bool IsDelegateDispatch(IOperation op)
        => op is IInvocationOperation inv && inv.TargetMethod?.MethodKind == MethodKind.DelegateInvoke;

    /// <summary>True when THIS specific dispatch operation occurs in NON-tail position within
    /// <paramref name="body"/> (per-site tail sparing, design §4.3/§4.4: tail dispatches are never
    /// marked Reentrant so bundle-driven deep tail recursion stays spill-free). Reference-equality
    /// matcher — body and site must come from the SAME operation tree.</summary>
    public static bool IsNonTailDispatchSite(IOperation body, IOperation site)
        => HasNonTailSelfCall(body, (IOperation op, out System.Collections.Immutable.ImmutableArray<IArgumentOperation> args) =>
        {
            if (ReferenceEquals(op, site) && op is IInvocationOperation inv)
            {
                args = inv.Arguments;
                return true;
            }
            args = default;
            return false;
        });
    public int NextMethodIndex;
    public readonly List<(IMethodSymbol symbol, CFunction func)> PendingLocalFunctions = new();

    // Generic monomorphization
    public readonly List<IMethodSymbol> PendingGenericSpecs = new();
    public Dictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap;

    // Wave-9 round-5 [X6]: first registered specialization per generic DEFINITION. Lambdas and
    // local functions hoisted from a generic body are keyed by IMethodSymbol and therefore SHARED
    // across that body's specializations — a capturing closure's capture cells are seeded by
    // whichever spec emitted LAST (last-spec-wins; VM-proven r1=8 vs 3). A second DISTINCT
    // instantiation of a definition whose body contains a capturing closure is loud (per-spec
    // closure environments are Stage-2 territory, design §8-3). LOOKUP-ONLY (§1.5).
    public readonly Dictionary<IMethodSymbol, IMethodSymbol> FirstGenericSpec
        = new(SymbolEqualityComparer.Default);

    // Persistent local symbol → field name mapping (survives scope pop, for capture resolution).
    //
    // KNOWN LIMITATION (v2.2): All lambdas within the same UdonSharpBehaviour share this flat
    // mapping. A captured local is hoisted to a single module-level field. When two distinct
    // lambdas / delegate fields capture the SAME local, they alias — reassigning one delegate
    // overwrites the other's captured value. v2.2 detects this structurally via
    // LambdaCaptureAnalyzer + AllLambdaCaptures aggregation and raises an emit-time Error
    // (was a Warning in v2.1). Full cure requires a closure-object emulation layer
    // (long-term Phase F).
    public readonly struct LocalBinding
    {
        public readonly string Id;
        public LocalBinding(string id) { Id = id; }
    }

    public readonly Dictionary<ILocalSymbol, LocalBinding> LocalBindings = new(SymbolEqualityComparer.Default);

    // Lambda capture analysis (replaces HandlerBase.HasCaptures pre-v2.2).
    // See LambdaCaptureAnalyzer for rationale on manual walker vs Roslyn AnalyzeDataFlow.
    public readonly LambdaCaptureAnalyzer CaptureAnalyzer;

    // Aliasing detection: per captured symbol, list of closure-creation sites (lambdas and — wave-9
    // [W2] — capturing local-function METHOD GROUPS, which are the same closure in
    // IMethodReferenceOperation clothing) that captured it long-lived. Populated by
    // RecordLongLivedLambdaStore when a capturing value is assigned to a delegate field.
    // UasmEmitter inspects this after emit and raises an Error if any captured symbol has > 1 site.
    public readonly Dictionary<ISymbol, List<IOperation>> AllLambdaCaptures
        = new(SymbolEqualityComparer.Default);

    // §2.8(b) capture-escape guard: locals initialized/reassigned with a CAPTURING lambda (flow-insensitive
    // taint). Reading such a local in an escaping position (array/object store, return, field/property/
    // struct store) is a compile error in Stage 1 — flat capture + a delegate that outlives the frame
    // would be a compile-clean wrong value otherwise. MEMBERSHIP-ONLY set (§1.5): never enumerate it to
    // drive emission order (symbol-keyed iteration order would break the 2-compile determinism gate).
    public readonly HashSet<ILocalSymbol> CapturingLambdaLocals = new(SymbolEqualityComparer.Default);

    // Wave-9 round-5 [X9]: WEAK tier of the capture taint — locals whose ONLY taint source is a
    // bare delegate-param copy (`T cur = v;`, [J5]/[J6] param arms, and copies thereof). [J5] made
    // these strong, which loud-rejected the legal generic fold idiom `T cur = v; …; return cur;`
    // at the RETURN guard. A param-copy-only local stays rejected at every escaping STORE and
    // erasing ARGUMENT (it stays in CapturingLambdaLocals — the laundering channels are sealed),
    // but returning it is exactly returning the param, which is legal by design (the CALLER's
    // invocation-result taint owns the laundered result). Strict subset of CapturingLambdaLocals;
    // strong taint dominates (promotion removes the marker; the fixpoint is monotone over
    // clean < weak < strong). MEMBERSHIP-ONLY set (§1.5).
    public readonly HashSet<ILocalSymbol> ParamCopyTaintLocals = new(SymbolEqualityComparer.Default);

    /// <summary>[X9] STRONG capture taint: tainted everywhere, including returns. Clears any weak
    /// marker. Returns true when the taint state changed (new taint or weak→strong promotion).</summary>
    public bool AddCaptureTaint(ILocalSymbol local)
    {
        bool added = CapturingLambdaLocals.Add(local);
        bool promoted = ParamCopyTaintLocals.Remove(local);
        return added || promoted;
    }

    /// <summary>[X9] WEAK param-copy taint: store/argument-position taint only (returns stay
    /// legal). Never demotes an existing strong taint. Returns true when newly tainted.</summary>
    public bool AddParamCopyTaint(ILocalSymbol local)
    {
        if (CapturingLambdaLocals.Contains(local)) return false;
        CapturingLambdaLocals.Add(local);
        ParamCopyTaintLocals.Add(local);
        return true;
    }

    /// <summary>[X9] The local's taint is exclusively the weak param-copy tier.</summary>
    public bool IsParamCopyOnlyTaint(ILocalSymbol local) => ParamCopyTaintLocals.Contains(local);

    // Round-7 follow-up [Q4]: foreach ITERATION variables. C# makes them READONLY, so invoking a
    // non-readonly struct member on one runs on a DEFENSIVE COPY (the classic foreach-struct-
    // mutation no-op); the loop variable's object[] is live storage in the flat emulation, so the
    // struct-instance-call receiver is CLONED when its chain roots at one of these locals
    // (VM-proven: loop-var reads after a mutating call 1112 vs CLR 102). MEMBERSHIP-ONLY set (§1.5).
    public readonly HashSet<ILocalSymbol> ForeachIterationLocals = new(SymbolEqualityComparer.Default);

    // §2.8 round-2: fields / auto-properties / struct members that receive a DIRECT capturing-lambda
    // store anywhere in this class (pre-scanned by UasmEmitter.CollectCaptureReceivingMembers over all
    // root bodies + field initializers BEFORE body emission, so the taint is emission-order-independent).
    // Reading such a member is tainted-equivalent at escaping positions: the member can legally hold a
    // multi-activation flat capture (one bundle live at a time is correct), but COPYING it out to an
    // array / object / another member / a return re-creates the fcd30-class aliasing wrongness with a
    // single lambda — which the 2+-lambda aliasing detector cannot see. MEMBERSHIP-ONLY set (§1.5).
    public readonly HashSet<ISymbol> CaptureReceivingMembers = new(SymbolEqualityComparer.Default);

    // §2.8 round-3 [A]: local functions whose bodies capture enclosing locals/params. A method-group
    // conversion of such a local function is a closure exactly like a capturing lambda, but it is an
    // IMethodReferenceOperation — invisible to the lambda analyzer — so the guards consult this set
    // to treat it as capturing-lambda-EQUIVALENT (direct stores, the recipient pre-scan, the taint
    // walk, returns). Pre-scanned in UasmEmitter.BuildRecursionInfo from the recursion-info bodies
    // BEFORE any emission (order-independent). MEMBERSHIP-ONLY set (§1.5).
    public readonly HashSet<IMethodSymbol> CapturingLocalFunctions = new(SymbolEqualityComparer.Default);

    /// <summary>Method symbol is a local function that captures enclosing locals/params (§2.8
    /// round-3 [A]). Checks the original definition too: symbol identity across semantic models is
    /// value-based for local functions (syntax + container), same mechanism the recursion graph
    /// relies on.</summary>
    public bool IsCapturingLocalFunction(IMethodSymbol m)
        => m != null && m.MethodKind == MethodKind.LocalFunction
           && (CapturingLocalFunctions.Contains(m) || CapturingLocalFunctions.Contains(m.OriginalDefinition));

    // ── Wave-9 [W1]/[W2]: per-iteration capture escapes ──
    //
    // C# re-instantiates a local declared inside a loop BODY (and the foreach iteration variable)
    // on every iteration, so a closure capturing it references THAT iteration's instance. The flat
    // capture model has ONE heap slot per captured local — later iterations re-seed it — so a
    // closure that outlives its loop iteration reads the LAST iteration's value (VM-proven 16 where
    // C# gives 6, with a SINGLE lambda site the 2+-site aliasing detector can never see). Escapes
    // that outlive the iteration (member stores; stores into locals declared outside the loop —
    // directly, via copies, or via laundered invocation results) are loud rejects; stores into
    // locals declared inside the loop (die with the iteration) and direct invocation/arguments stay
    // legal. Distinguishing the always-overwritten-then-read-after shape (observationally correct)
    // would need dominance analysis — conservative over-rejection accepted per design §8-3.
    //
    // Locals carrying a per-iteration capture, mapped to the loop statements whose iteration they
    // must not outlive. Seeded by the BuildRecursionInfo pre-scan (order-independent) and the
    // emission-time guards (redundant backstop); propagated along the same local-to-local copy
    // edges as the capture taint; checked against each local's own declaration position.
    // LOOKUP-ONLY during emission (§1.5); the post-fixpoint check sorts before throwing.
    public readonly Dictionary<ILocalSymbol, HashSet<SyntaxNode>> IterationFragileLocals
        = new(SymbolEqualityComparer.Default);

    /// <summary>The innermost loop statement whose ITERATION scope contains this local's
    /// declaration — null when the local is not per-iteration. A `for` INITIALIZER declaration is
    /// shared across iterations (C# closes over the one variable; the flat slot matches), so only
    /// body/condition/incrementor positions count; the foreach iteration variable is per-iteration
    /// (C# 5+). The walk stops at function/member boundaries: an enclosing loop OUTSIDE the
    /// declaring function re-enters via CALLS, which is the documented cross-activation tier.</summary>
    public static SyntaxNode GetPerIterationLoop(ILocalSymbol local)
    {
        var decl = local?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (decl == null) return null;
        if (decl is Microsoft.CodeAnalysis.CSharp.Syntax.CommonForEachStatementSyntax)
            return decl; // the foreach iteration variable's declaring syntax IS the loop statement
        for (SyntaxNode node = decl, a = decl.Parent; a != null; node = a, a = a.Parent)
        {
            switch (a)
            {
                case Microsoft.CodeAnalysis.CSharp.Syntax.ForStatementSyntax fs when node == fs.Declaration:
                    break; // for-initializer variable: one instance for the whole loop
                case Microsoft.CodeAnalysis.CSharp.Syntax.ForStatementSyntax
                    or Microsoft.CodeAnalysis.CSharp.Syntax.WhileStatementSyntax
                    or Microsoft.CodeAnalysis.CSharp.Syntax.DoStatementSyntax
                    or Microsoft.CodeAnalysis.CSharp.Syntax.CommonForEachStatementSyntax:
                    return a;
                case Microsoft.CodeAnalysis.CSharp.Syntax.AnonymousFunctionExpressionSyntax
                    or Microsoft.CodeAnalysis.CSharp.Syntax.LocalFunctionStatementSyntax
                    or Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax
                    or Microsoft.CodeAnalysis.CSharp.Syntax.AccessorDeclarationSyntax:
                    return null;
            }
        }
        return null;
    }

    /// <summary>True when this local's declaration lives inside ONE iteration of
    /// <paramref name="loop"/> (it dies with the iteration, so it may legally hold a closure over
    /// that iteration's captures). False for declarations outside the loop, in a `for` initializer
    /// (whole-loop lifetime), or with no source declaration (conservative).</summary>
    public static bool IsWithinIterationScope(ILocalSymbol local, SyntaxNode loop)
    {
        var decl = local?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (decl == null || loop == null) return false;
        if (decl == loop) return true; // the loop's own foreach iteration variable
        for (SyntaxNode node = decl, a = decl.Parent; a != null; node = a, a = a.Parent)
            if (a == loop)
                return !(loop is Microsoft.CodeAnalysis.CSharp.Syntax.ForStatementSyntax fs
                         && node == fs.Declaration);
        return false;
    }

    /// <summary>Per-iteration loops of a capture SET: every captured local that is per-iteration
    /// contributes its innermost loop. Null when none (captured params and out-of-loop locals are
    /// not per-iteration).</summary>
    public static HashSet<SyntaxNode> ComputePerIterationCaptureLoops(IEnumerable<ISymbol> captures)
    {
        HashSet<SyntaxNode> loops = null;
        foreach (var c in captures)
            if (c is ILocalSymbol l && GetPerIterationLoop(l) is { } loop)
                (loops ??= new HashSet<SyntaxNode>()).Add(loop);
        return loops;
    }

    /// <summary>Per-iteration loops of a DIRECT capturing value (conversions unwrapped): a capturing
    /// lambda's capture set or a capturing local-function method group's transitive set. Null when
    /// the value is not a direct capturing creation or captures nothing per-iteration.</summary>
    public HashSet<SyntaxNode> GetPerIterationCaptureLoops(IOperation value)
    {
        var v = value;
        while (v is IConversionOperation conv) v = conv.Operand;
        if (v is not IDelegateCreationOperation dc) return null;
        return dc.Target switch
        {
            IAnonymousFunctionOperation lambda when CaptureAnalyzer.HasCaptures(lambda)
                => ComputePerIterationCaptureLoops(CaptureAnalyzer.GetCaptures(lambda)),
            IMethodReferenceOperation mr when IsCapturingLocalFunction(mr.Method)
                => ComputePerIterationCaptureLoops(CaptureAnalyzer.GetLocalFunctionCaptures(mr.Method)),
            _ => null,
        };
    }

    /// <summary>Merge per-iteration loops into a local's fragile set; true when something new was
    /// added (drives the pre-scan fixpoint).</summary>
    public bool AddIterationFragileLoops(ILocalSymbol local, IEnumerable<SyntaxNode> loops)
    {
        if (local == null || loops == null) return false;
        if (!IterationFragileLocals.TryGetValue(local, out var set))
        {
            set = new HashSet<SyntaxNode>();
            IterationFragileLocals[local] = set;
        }
        bool added = false;
        foreach (var l in loops) added |= set.Add(l);
        return added;
    }

    public static string PerIterationCaptureError(string escapeDescription)
        => $"Lambda/local function captures a per-iteration loop local, but {escapeDescription}: "
         + "C# re-creates the captured local on every iteration while the flat capture model has "
         + "one heap slot that later iterations re-seed, so a closure outliving its loop iteration "
         + "would read the LAST iteration's value (silent wrong value). Store the delegate only "
         + "into locals declared inside the loop and invoke it within the iteration, or hoist the "
         + "captured local out of the loop.";

    /// <summary>§2.8 round-5 [N2]: THE canonical form of a member symbol, used at EVERY
    /// CaptureReceivingMembers record point AND lookup point (a second ad-hoc symbol comparison is
    /// how the round-5 identity holes were born). Maps named tuple elements to their ItemN field
    /// (within the SAME constructed tuple — ItemN of different instantiations stay distinct),
    /// compares generic members by original definition, and walks override chains to the ROOT
    /// declaration so a store through a derived override symbol and a read through the base
    /// virtual symbol hit the same entry (one virtual slot, one backing store). Returns NULL for
    /// interface members: the implementing class is unknown at emit time (an interface-typed
    /// receiver can dispatch to any behaviour), so there is no canonical form — callers must treat
    /// null as "unknown → conservative/loud" (§8-3).</summary>
    public static ISymbol CanonicalMemberSymbol(ISymbol member)
    {
        if (member == null) return null;
        if (member.ContainingType?.TypeKind == TypeKind.Interface) return null;
        if (member is IFieldSymbol f && f.CorrespondingTupleField is { } tupleField)
            return tupleField;
        member = member.OriginalDefinition;
        while (true)
        {
            ISymbol overridden = member switch
            {
                IPropertySymbol p => p.OverriddenProperty,
                IMethodSymbol m => m.OverriddenMethod,
                IEventSymbol e => e.OverriddenEvent,
                _ => null,
            };
            if (overridden == null) break;
            member = overridden.OriginalDefinition;
        }
        return member;
    }

    /// <summary>§2.8 round-2/3 (single source of truth; HandlerBase wraps with the type-param-map
    /// resolver, the pre-scans pass null = identity so an unresolved T stays conservatively
    /// capable): a type whose value can carry a delegate bundle past the delegate-typed guards —
    /// a delegate itself, System.Object (boxing erases the delegate typing), an unresolved type
    /// parameter, a tuple with any delegate-capable element, or a user struct with a (recursively)
    /// delegate-capable instance field. Over-rejection accepted per §8-3.</summary>
    public static bool IsDelegateCapableType(ITypeSymbol t, Func<ITypeSymbol, ITypeSymbol> resolve)
    {
        if (t == null) return false;
        var r = resolve != null ? resolve(t) : t;
        if (r == null) return false;
        if (r.SpecialType == SpecialType.System_Object) return true;
        return IsNonObjectDelegateCapableType(t, resolve);
    }

    /// <summary>Delegate-capable minus bare System.Object (see HandlerBase doc: bare-object VALUES
    /// cannot legally carry a bundle — every entry point is sealed — so object-typed param/member
    /// reads stay clean and ordinary object plumbing keeps compiling).</summary>
    public static bool IsNonObjectDelegateCapableType(ITypeSymbol t, Func<ITypeSymbol, ITypeSymbol> resolve)
    {
        if (t == null) return false;
        var r = resolve != null ? resolve(t) : t;
        if (r == null) return false;
        if (r is ITypeParameterSymbol) return true;
        if (r is INamedTypeSymbol nt)
        {
            if (nt.DelegateInvokeMethod != null) return true;
            if (nt.IsTupleType)
            {
                foreach (var el in nt.TupleElements)
                    if (IsDelegateCapableType(el.Type, resolve)) return true;
            }
            // §2.8 round-3 [B]: a USER STRUCT with a (recursively) delegate-capable instance field
            // is an envelope — its object[] emulation carries the bundle past every delegate-typed
            // gate (whole-struct array stores / returns / erased args, VM-verified laundering).
            // Auto-prop backing fields are IFieldSymbols, so fields cover all stored members.
            // Terminates: C# forbids value-type field cycles, and array fields are not capable
            // (array-element stores of dangerous values are loud everywhere already).
            else if (IsUserStruct(nt))
            {
                foreach (var member in nt.GetMembers())
                    if (member is IFieldSymbol fld && !fld.IsStatic && IsDelegateCapableType(fld.Type, resolve))
                        return true;
            }
        }
        return false;
    }

    /// <summary>§2.8 round-3 [D] + round-5 [N2] (single source of truth; HandlerBase wraps with
    /// its class symbol): member of a CLASS other than <paramref name="emittingClass"/> (or its
    /// bases) — the per-class recipient pre-scan cannot make that class's reads loud — or an
    /// INTERFACE member, whose implementing class is unknown at emit time (an interface-typed
    /// receiver can dispatch to any behaviour, so neither the recipient pre-scan nor the foreign
    /// armor can see the real member). Struct members are not foreign: struct values cross call
    /// boundaries as params, where the param-rooted member-read taint applies in the receiving
    /// method regardless of class.</summary>
    public static bool IsForeignOrInterfaceMember(ISymbol member, INamedTypeSymbol emittingClass)
    {
        var ct = member?.ContainingType;
        if (ct == null) return false;
        if (ct.TypeKind == TypeKind.Interface) return true;
        if (ct.TypeKind != TypeKind.Class) return false;
        for (var t = emittingClass; t != null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t, ct)) return false;
        return true;
    }

    /// <summary>
    /// Record that <paramref name="lambda"/> was assigned to a delegate field (or otherwise
    /// stored long-lived). Each captured symbol is appended to AllLambdaCaptures so post-emit
    /// aliasing detection can flag multiple lambdas sharing the same captured local.
    ///
    /// Recording is intentionally limited to delegate-FIELD stores. A delegate LOCAL lives only
    /// within one method invocation, where C# shares the closure environment too, so flat-heap
    /// aliasing is observationally equivalent there (holds under re-entrancy: the recursion spill
    /// saves/restores captured locals of non-hoisted methods). Lambda literals passed as delegate
    /// arguments get a fresh hoist + convention per call site; delegate locals/params as
    /// arguments throw in InvocationHandler. Caveat: a few delegate-field write shapes
    /// (deconstruction targets, cross-behaviour ??=, delegate auto-properties, delegate members
    /// in user structs / object-typed fields) bypass this recording and are only stopped
    /// downstream (CoreVerify / Udon assembler) or ship as dead, uninvokable values — promoting
    /// those to explicit compile errors is tracked in roadmap B28.
    /// </summary>
    public void RecordLambdaCaptures(IAnonymousFunctionOperation lambda)
        => RecordCaptureSites(CaptureAnalyzer.GetCaptures(lambda), lambda);

    /// <summary>Wave-9 [W2]: a capturing local-function METHOD GROUP stored long-lived registers its
    /// (transitive) capture set exactly like a lambda — pre-fix the aliasing dictionary was keyed to
    /// IAnonymousFunctionOperation only, so caplf method-group field stores bypassed
    /// DetectLambdaCaptureAliasing entirely (two caplf fields sharing a captured local shipped a
    /// compile-clean wrong value where the identical two-lambda shape was diagnosed).</summary>
    public void RecordLocalFunctionCaptures(IMethodReferenceOperation methodGroup)
        => RecordCaptureSites(CaptureAnalyzer.GetLocalFunctionCaptures(methodGroup.Method), methodGroup);

    void RecordCaptureSites(System.Collections.Immutable.ImmutableArray<ISymbol> captures, IOperation site)
    {
        foreach (var sym in captures)
        {
            // 'this' is always the same instance — captures of `this` (or instance-method receiver)
            // never alias in the problematic sense. Skip to avoid false positives when multiple
            // lambdas merely access this.field.
            if (sym is IParameterSymbol p && p.IsThis) continue;
            if (!AllLambdaCaptures.TryGetValue(sym, out var list))
            {
                list = new List<IOperation>();
                AllLambdaCaptures[sym] = list;
            }
            if (!list.Contains(site)) list.Add(site);
        }
    }

    /// <summary>Round-7 follow-up [Q3]: `in` parameters (RefKind.In) are a loud declaration-side
    /// reject. The flat-heap calling convention copies arguments by value with no copy-back, so an
    /// `in` param is neither a readonly ALIAS of the caller's storage (VM-proven: a callee observing
    /// a caller field write through the param read 1 vs CLR 5) nor protected by the readonly
    /// DEFENSIVE COPY (a mutating struct method on the param wrote the param storage, 11 vs CLR 1).
    /// Called at every user-method registration point (class/base/struct/foreign-static methods,
    /// generic specializations, local functions); delegates with `in` params already reject via
    /// DelegateAbi.ValidateNoRefOutParams (RefKind != None).</summary>
    public static void RejectInParameters(IMethodSymbol method)
    {
        foreach (var p in method.Parameters)
            if (p.RefKind == RefKind.In)
                throw new System.NotSupportedException(
                    $"'in' parameter '{p.Name}' on '{method.Name}' is not supported: the flat-heap "
                    + "calling convention copies by value, so 'in' would silently lose its readonly-"
                    + "alias and defensive-copy semantics. Use a by-value parameter, or ref if "
                    + "write-back is intended.");
    }

    /// <summary>M4 [T1]: a [NetworkCallable] method's parameters cross the network, but a delegate
    /// value is a program-local object[] bundle — its target reference and funcaddr are meaningless
    /// in any other client's program, so it can never be marshalled. Pre-fix (probed at 931a9ab)
    /// this compiled CLEAN: the method exported unmangled with a SystemObjectArray param var, a
    /// silent runtime miscompile. The delegate-typed RETURN flavor also compiled clean, even though
    /// stock UdonSharp forbids ANY return type on [NetworkCallable] ("cannot have a return type") —
    /// rejected here for the same bundle reason. Called from the class first-pass registration loop
    /// (own + inherited behaviour methods, before the generic skip), so every compile of a class
    /// hits it exactly once per method.</summary>
    public static void RejectNetworkCallableDelegates(IMethodSymbol method)
    {
        if (!LayoutPlanner.IsNetworkCallable(method)) return;
        foreach (var p in method.Parameters)
            if (ContainsDelegateType(p.Type))
                throw new System.NotSupportedException(
                    $"[NetworkCallable] method '{method.Name}' cannot take delegate-typed parameter "
                    + $"'{p.Name}': a delegate value is a program-local object[] bundle and cannot "
                    + "cross a network call. Pass plain data instead and re-create the delegate "
                    + "locally on the receiving side.");
        if (ContainsDelegateType(method.ReturnType))
            throw new System.NotSupportedException(
                $"[NetworkCallable] method '{method.Name}' cannot return a delegate-typed value: "
                + "a delegate value is a program-local object[] bundle and cannot cross a network "
                + "call. Return plain data instead and re-create the delegate locally on the "
                + "receiving side.");
    }

    /// <summary>Delegate proper, or an array (of arrays…) of delegates. Deliberately NOT the wider
    /// IsDelegateCapableType (object / delegate-tuples / type params): [NetworkCallable] methods
    /// with object params are outside this policy item and must not start rejecting.</summary>
    static bool ContainsDelegateType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol n && n.DelegateInvokeMethod != null) return true;
        if (type is IArrayTypeSymbol a) return ContainsDelegateType(a.ElementType);
        return false;
    }

    // Aggregate type support — tuples and user-defined structs share the object[] emulation.
    public static bool IsAggregateType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named) return false;
        // Armor (design §1.2): a delegate value is an object[] BUNDLE copied by reference — it must never
        // ride the aggregate clone-on-read machinery (a clone would break reference identity and
        // (target, method) equality). Single choke point for every clone path.
        if (named.TypeKind == TypeKind.Delegate) return false;
        return named.IsTupleType || IsUserStruct(named);
    }

    /// <summary>Source-defined value struct (object[]-emulated). Excludes SDK/native structs
    /// (Vector3, Color, …) — which have native Udon extern types — by namespace, since in the test
    /// environment SDK types are source stubs (so syntax-refs alone can't tell them apart).</summary>
    public static bool IsUserStruct(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Struct || type.SpecialType != SpecialType.None) return false;
        if (type.DeclaringSyntaxReferences.Length == 0) return false; // from a referenced assembly = native
        return !IsSdkNamespace(type.ContainingNamespace);
    }

    /// <summary>The parameterless void Dispose() of a user type (public or explicit IDisposable impl),
    /// or null. Used to route a `using` resource's implicit Dispose through a real method call rather
    /// than a non-existent SystemObjectArray.__Dispose__ extern when the disposable is a user struct.</summary>
    public static IMethodSymbol FindStructDisposeMethod(ITypeSymbol type)
    {
        foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
            if (!m.IsStatic && m.Parameters.Length == 0 && m.ReturnsVoid
                && (m.Name == "Dispose"
                    || m.ExplicitInterfaceImplementations.Any(e => e.Name == "Dispose")))
                return m;
        return null;
    }

    static bool IsSdkNamespace(INamespaceSymbol ns)
    {
        for (var n = ns; n != null && !n.IsGlobalNamespace; n = n.ContainingNamespace)
        {
            if (n.Name is "System" or "UnityEngine" or "VRC" or "Cinemachine"
                or "TMPro" or "Unity" or "Microsoft")
                return true;
        }
        return false;
    }

    readonly Dictionary<ITypeSymbol, AggregateLayout> _aggregateLayoutCache = new(SymbolEqualityComparer.Default);

    public AggregateLayout GetAggregateLayout(INamedTypeSymbol type)
    {
        if (_aggregateLayoutCache.TryGetValue(type, out var cached)) return cached;
        var layout = AggregateLayout.Build(type);
        _aggregateLayoutCache[type] = layout;
        return layout;
    }

    // Field initializers to emit at _start
    public readonly List<(string fieldName, IOperation initOp, ITypeSymbol fieldType)> FieldInitOps = new();

    // FieldChangeCallback: fieldName → propertyName
    public readonly Dictionary<string, string> FieldChangeCallbacks = new();

    // Conditional access stack (for ?. operator): the evaluated instance leaf. For a delegate-typed
    // receiver this is the BUNDLE leaf itself (design §2.6) — `d?.Invoke()` dispatches on it, and any
    // delegate-valued expression (local/param/element/call result) is a legal ?.Invoke receiver.
    public readonly Stack<CLeaf> ConditionalAccessStack = new();

    // using declaration Dispose tracking
    public readonly Stack<List<(CLeaf val, ITypeSymbol type)>> UsingDisposableStack = new();

    /// <summary>Stack of using-stack depths at loop/switch entry points.
    /// Used to limit Dispose emission for break/continue to scopes inside the loop.</summary>
    public readonly Stack<int> LoopUsingDepthStack = new();

    // Switch break label stack — top is non-null inside switch body, null sentinel inside loop body.
    // StatementHandler.VisitBranch reads top to distinguish switch breaks (goto end label) from loop breaks (CBreak).
    public readonly Stack<string> SwitchBreakLabels = new();

    int _switchLabelCounter;
    /// <summary>Generate a unique end label for a switch statement (per EmitContext = per class).</summary>
    public string NextSwitchEndLabel() => $"__switchEnd_{++_switchLabelCounter}";

    // goto-case / goto-default → sanitized UASM landing label, per enclosing switch (innermost on top). The
    // Roslyn target name ("case 2:", "default") is not a valid UASM label token, so both the case-body label
    // (SwitchHandler) and the goto (StatementHandler.VisitBranch) resolve through this shared map.
    public readonly Stack<Dictionary<string, string>> GotoCaseLabels = new();

    // Delegate fields: tracks which user fields are delegate-typed and were expanded to bundles
    public readonly HashSet<string> DelegateFields = new();

    // Pending delegate bridges for dynamically hoisted lambdas/local functions
    public readonly List<(IMethodSymbol method, string bridgeExportName, Dictionary<ITypeParameterSymbol, ITypeSymbol> resolvedTypeParamMap)> PendingDelegateBridges = new();

    // Diagnostics collected during emission
    public readonly List<EmitDiagnostic> Diagnostics = new();
    public readonly HashSet<string> ReportedExterns = new();

    // Dispatch delegates (Core IR-based)
    Action<IOperation> _visitOperation;
    Func<IOperation, CLeaf> _visitExpression;
    Func<CLeaf, ITypeSymbol, IPatternOperation, CLeaf> _emitPatternCheck;
    Func<INamedTypeSymbol, CLeaf> _emitNewAggregate;

    public Action<IOperation> VisitOperation => _visitOperation
        ?? throw new InvalidOperationException("EmitContext dispatchers not initialized. Call InitializeDispatchers first.");
    public Func<IOperation, CLeaf> VisitExpression => _visitExpression
        ?? throw new InvalidOperationException("EmitContext dispatchers not initialized. Call InitializeDispatchers first.");
    public Func<CLeaf, ITypeSymbol, IPatternOperation, CLeaf> EmitPatternCheck => _emitPatternCheck
        ?? throw new InvalidOperationException("EmitContext dispatchers not initialized. Call InitializeDispatchers first.");
    /// <summary>Allocate + default-initialize a fresh object[]-backed aggregate (struct/tuple) as a value.
    /// Exposed so non-handler emit paths (e.g. default-initializing an aggregate field) can reuse it.</summary>
    public Func<INamedTypeSymbol, CLeaf> EmitNewAggregate => _emitNewAggregate
        ?? throw new InvalidOperationException("EmitContext dispatchers not initialized. Call InitializeDispatchers first.");

    /// <summary>Aggregate (struct/tuple) instance fields with NO explicit initializer. C# default-initializes
    /// them to a zeroed struct; in the object[] emulation that requires a fresh default object[] (else the heap
    /// var stays null and a field write faults). Reference-type / array fields correctly stay null and are absent here.</summary>
    public readonly List<(string fieldName, INamedTypeSymbol aggType)> AggregateFieldDefaults = new();

    public void InitializeDispatchers(
        Action<IOperation> visitOp,
        Func<IOperation, CLeaf> visitExpr,
        Func<CLeaf, ITypeSymbol, IPatternOperation, CLeaf> emitPattern,
        Func<INamedTypeSymbol, CLeaf> emitNewAggregate)
    {
        _visitOperation = visitOp ?? throw new ArgumentNullException(nameof(visitOp));
        _visitExpression = visitExpr ?? throw new ArgumentNullException(nameof(visitExpr));
        _emitPatternCheck = emitPattern ?? throw new ArgumentNullException(nameof(emitPattern));
        _emitNewAggregate = emitNewAggregate ?? throw new ArgumentNullException(nameof(emitNewAggregate));
    }

    public EmitContext(Compilation compilation, INamedTypeSymbol classSymbol, LayoutPlanner planner)
    {
        Compilation = compilation;
        ClassSymbol = classSymbol;
        Module = new CModule { ClassName = classSymbol.ToDisplayString() };
        Builder = new CoreBuilder(Module);
        Planner = planner;
        CaptureAnalyzer = new LambdaCaptureAnalyzer(compilation);
    }

    // ══════════════════════════════════════════════════════════════════
    // Variable naming utilities (replaces VariableTable)
    // ══════════════════════════════════════════════════════════════════

    readonly Dictionary<string, int> _counters = new();
    readonly HashSet<string> _declaredFieldNames = new();
    readonly Dictionary<string, string> _thisVars = new();
    readonly Dictionary<string, string> _structConstIds = new();

    int NextIndex(string key)
    {
        _counters.TryGetValue(key, out var n);
        _counters[key] = n + 1;
        return n;
    }

    /// <summary>Declare a field in Module. Idempotent — returns existing name if already declared.</summary>
    public string DeclareField(string name, string type, FieldFlags flags = FieldFlags.None,
        object defaultValue = null, string syncMode = null)
    {
        if (_declaredFieldNames.Contains(name)) return name;
        var field = new FieldDecl(name, type) { Flags = flags, DefaultValue = defaultValue, SyncMode = syncMode };
        Module.Fields.Add(field);
        _declaredFieldNames.Add(name);
        return name;
    }

    /// <summary>Declare a named variable field. Idempotent.</summary>
    public string DeclareVar(string id, string type)
    {
        if (_declaredFieldNames.Contains(id)) return id;
        Module.Fields.Add(new FieldDecl(id, type));
        _declaredFieldNames.Add(id);
        return id;
    }

    /// <summary>Try to declare a variable. Returns true if newly declared.</summary>
    public bool TryDeclareVar(string id, string type)
    {
        if (_declaredFieldNames.Contains(id)) return false;
        Module.Fields.Add(new FieldDecl(id, type));
        _declaredFieldNames.Add(id);
        return true;
    }

    /// <summary>Declare a local variable with unique field name.</summary>
    public string DeclareLocal(string name, string type)
    {
        var idx = NextIndex($"lcl_{name}_{type}");
        var id = $"__lcl_{name}_{type}_{idx}";
        Module.Fields.Add(new FieldDecl(id, type));
        _declaredFieldNames.Add(id);
        return id;
    }

    /// <summary>Declare a "this" reference field with type remapping for Udon heap.</summary>
    public string DeclareThis(string udonType)
    {
        var heapType = SupportedThisTypes.Contains(udonType) ? udonType : "VRCUdonUdonBehaviour";
        var idx = NextIndex($"this_{heapType}");
        var id = $"__this_{heapType}_{idx}";
        Module.Fields.Add(new FieldDecl(id, heapType) { DefaultValue = "this" });
        _declaredFieldNames.Add(id);
        return id;
    }

    /// <summary>Declare or reuse a "this" reference for the given type.</summary>
    public string DeclareThisOnce(string udonType)
    {
        if (_thisVars.TryGetValue(udonType, out var existing)) return existing;
        var id = DeclareThis(udonType);
        _thisVars[udonType] = id;
        return id;
    }

    static readonly HashSet<string> SupportedThisTypes = new()
    {
        "UnityEngineGameObject", "UnityEngineTransform", "VRCUdonUdonBehaviour",
    };

    // ── Software recursion stack ──
    // Udon's flat heap shares param/local slots across call frames, so recursion-cycle calls must spill
    // the caller's live values to a heap-backed LIFO stack (boxed object[]) and reload after the call.

    public const string RecurStackId = "__recurStack";
    public const string RecurSpId = "__recurSp";
    /// <summary>Max boxed values held across all live recursion frames (depth × live-vars-per-frame).</summary>
    public const int RecurStackSize = 512;
    bool _recurStackDeclared;

    /// <summary>Idempotently declare the per-program recursion stack (object[] backing + int stack pointer).
    /// Heap default allocates the backing array and zeroes the pointer; LIFO spill/reload keeps it balanced.</summary>
    public void EnsureRecursionStack()
    {
        if (_recurStackDeclared) return;
        _recurStackDeclared = true;
        Module.Fields.Add(new FieldDecl(RecurStackId, "SystemObjectArray") { DefaultValue = new object[RecurStackSize] });
        _declaredFieldNames.Add(RecurStackId);
        Module.Fields.Add(new FieldDecl(RecurSpId, "SystemInt32") { DefaultValue = 0 });
        _declaredFieldNames.Add(RecurSpId);
    }


    /// <summary>Declare reflection type IDs array.</summary>
    public void DeclareReflTypeIds(long[] typeIds)
    {
        DeclareField("__refl_typeids", "SystemInt64Array", defaultValue: typeIds);
    }

    /// <summary>Set const value on an existing field.</summary>
    public void SetFieldConstValue(string name, object value)
    {
        var field = Module.Fields.FirstOrDefault(f => f.Name == name);
        if (field != null) field.DefaultValue = value;
    }

    /// <summary>Check if a field name has been declared.</summary>
    public bool IsFieldDeclared(string name) => _declaredFieldNames.Contains(name);

    /// <summary>Allocate a Scratch slot for a temporary value (slot-based, coalesced by register allocator).</summary>
    public int AllocTemp(string type) => Builder.AllocScratch(type);

    /// <summary>Declare a struct constant field with deduplication (e.g., Vector3.zero).</summary>
    public string DeclareStructConst(string type, object value)
    {
        var key = $"{type}_{value}";
        if (_structConstIds.TryGetValue(key, out var existing)) return existing;
        var idx = NextIndex($"structconst_{type}");
        var id = $"__const_{type}_{idx}";
        Module.Fields.Add(new FieldDecl(id, type) { DefaultValue = value });
        _declaredFieldNames.Add(id);
        _structConstIds[key] = id;
        return id;
    }

    /// <summary>Get the Udon type of a declared field by its ID.</summary>
    public string GetFieldType(string id)
    {
        return Module.Fields.FirstOrDefault(f => f.Name == id)?.Type;
    }

    // ── Constant parsing (moved from VariableTable) ──

    /// <summary>Parse a string constant value to a typed CLR object.</summary>
    public static object ParseConstValue(string udonType, string value)
    {
        if (value == "null") return null;
        return udonType switch
        {
            "SystemInt32" => value.StartsWith("0x") ? Convert.ToInt32(value, 16) : int.Parse(value),
            "SystemUInt32" => value.StartsWith("0x") ? Convert.ToUInt32(value, 16) : uint.Parse(value),
            "SystemInt64" => long.Parse(value),
            "SystemUInt64" => ulong.Parse(value),
            "SystemInt16" => short.Parse(value),
            "SystemUInt16" => ushort.Parse(value),
            "SystemSByte" => sbyte.Parse(value),
            "SystemSingle" => float.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            "SystemDouble" => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            "SystemBoolean" => bool.Parse(value),
            "SystemString" => value,
            "SystemByte" => byte.Parse(value),
            "SystemChar" => value[0],
            "SystemType" => value, // Udon type name, resolved to CLR Type at apply time
            _ => long.TryParse(value, out var longVal)
                ? (longVal is >= int.MinValue and <= int.MaxValue ? (object)(int)longVal : longVal)
                : ulong.TryParse(value, out var ulongVal) ? (object)ulongVal : null,
        };
    }
}
