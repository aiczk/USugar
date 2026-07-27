using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Xunit;

namespace USugar.Tests;

// Language-surface census (2026-07-11, owner-ordered): makes the claim "everything outside VM
// constraints is writable" MACHINE-CHECKABLE. Every Roslyn OperationKind must land in exactly one
// bucket: HANDLED (the live pipeline's declared operation kinds, plus the internal-arm list),
// VM_FUNDAMENTAL (documented loud reject rooted in a missing VM capability), OUT_OF_SCOPE (VB-only,
// CFG-lowered-only, above the C# 9 Unity LCD, or metadata-only), DOCUMENTED_REJECT (designed
// non-VM boundary with a loud message), or KNOWN_GAP (the honest to-do list). "Complete" is then
// the predicate KNOWN_GAP == empty — and a Roslyn upgrade or handler removal breaks this test
// loudly instead of silently widening the default-throw surface.
public class LanguageSurfaceTests
{
    // Kinds handled INSIDE emitter/handler switch arms rather than via the dispatch tables
    // (verified by the census failure output on first run; keep anchors current).
    static readonly OperationKind[] HandledInternal =
    {
        OperationKind.Block,                    // VisitOperation structural walk
        OperationKind.LocalFunction,            // StatementHandler declaration arm
        OperationKind.Labeled,                  // StatementHandler
        OperationKind.Empty,                    // StatementHandler (labeled empty targets)
        OperationKind.MethodBody,               // EmitMethod roots
        OperationKind.ConstructorBody,          // EmitMethod roots (struct ctors)
        OperationKind.AnonymousFunction,        // hoisted during callable planning
        OperationKind.DelegateCreation,         // ExpressionHandler VisitDelegateCreation
        OperationKind.FieldInitializer,         // EmitFieldInitializers
        OperationKind.VariableInitializer,      // within declarations
        OperationKind.Argument,                 // consumed by invocation staging
        OperationKind.TypeParameterObjectCreation, // new T() -> concrete mint (InvocationHandler)
        OperationKind.AnonymousObjectCreation,     // new { X = .. } -> aggregate mint (InvocationHandler)
        // sub-nodes consumed by their parent's handler (verified by corpus green + code grep):
        OperationKind.ArrayInitializer,         // ArrayHandler / AggregateLayout
        OperationKind.ObjectOrCollectionInitializer, OperationKind.MemberInitializer, // aggregate init
        OperationKind.CaseClause, OperationKind.SwitchCase, OperationKind.SwitchExpressionArm, // SwitchHandler
        OperationKind.ConstantPattern, OperationKind.DeclarationPattern, OperationKind.DiscardPattern,
        OperationKind.RelationalPattern, OperationKind.BinaryPattern, OperationKind.NegatedPattern,
        OperationKind.RecursivePattern, OperationKind.TypePattern, OperationKind.PropertySubpattern, // pattern lowering (B73/B74 pins)
        OperationKind.InterpolatedStringText, OperationKind.Interpolation, // InvocationHandler.Members interpolation
        OperationKind.MethodReference,          // delegate creation / MG resolution
        OperationKind.VariableDeclaration, OperationKind.VariableDeclarator, // declaration-group walk
        OperationKind.ParameterInitializer,     // default args (call sites receive the constant)
        OperationKind.PropertyInitializer,      // auto-prop initializers (EmitFieldInitializers family)
        OperationKind.Range,                    // range-slice loops (EmitPolicy/NdimArrayAbi)
    };

    // VM-fundamental: the Udon VM lacks the executing capability; loud documented rejects.
    static readonly OperationKind[] VmFundamental =
    {
        OperationKind.Throw,                    // no exceptions
        OperationKind.Try,                      // no exception unwinding
        OperationKind.Lock,                     // single-threaded, no monitors
        OperationKind.CaughtException,          // try/catch family
        OperationKind.CatchClause,              // try/catch family
        OperationKind.Await,                    // no continuation scheduling runtime
        OperationKind.AddressOf,                // unsafe pointers
        OperationKind.FunctionPointerInvocation, // no function pointers
        OperationKind.DynamicInvocation, OperationKind.DynamicMemberReference,
        OperationKind.DynamicIndexerAccess, OperationKind.DynamicObjectCreation, // no DLR
    };

    // Designed non-VM rejects (documented boundary, loud message exists).
    static readonly OperationKind[] DocumentedReject =
    {
        OperationKind.YieldReturn,
        OperationKind.YieldBreak,
    };

    // Out of scope: VB-only, CFG-lowered-only (never in a semantic-model tree), above C# 9,
    // or metadata/none kinds a C# 9 source walk cannot produce.
    static readonly OperationKind[] OutOfScope =
    {
        OperationKind.None, OperationKind.Invalid,
        // CFG-lowered only:
        OperationKind.FlowCapture, OperationKind.FlowCaptureReference, OperationKind.IsNull,
        OperationKind.StaticLocalInitializationSemaphore, OperationKind.FlowAnonymousFunction,
        // VB-only:
        OperationKind.Stop, OperationKind.End, OperationKind.RaiseEvent, OperationKind.OmittedArgument,
        OperationKind.ReDim, OperationKind.ReDimClause,
        OperationKind.TranslatedQuery,
        // > C# 9 (Unity 2022.3 LCD):
        OperationKind.ListPattern, OperationKind.SlicePattern, OperationKind.Utf8String,
        OperationKind.InterpolatedStringHandlerCreation, OperationKind.InterpolatedStringAddition,
        OperationKind.ImplicitIndexerReference,
        OperationKind.InterpolatedStringAppendFormatted, OperationKind.InterpolatedStringAppendLiteral,
        OperationKind.InterpolatedStringAppendInvalid, OperationKind.InterpolatedStringHandlerArgumentPlaceholder, // C# 10 handler lowering
        OperationKind.Parenthesized,            // VB-practical (C# elides parens in IOperation)
    };

    // The honest gap list: C# 9 surface that is neither handled nor rejected with a documented
    // message today — it falls to the default "Unsupported operation/expression" throw. Shrinking
    // this list to EMPTY is the machine-checkable definition of "complete outside VM constraints".
    static readonly OperationKind[] KnownGaps =
    {
        // EMPTY (2026-07-11): every C# 9 OperationKind is now HANDLED, VM_FUNDAMENTAL, DOCUMENTED_REJECT,
        // or OUT_OF_SCOPE. "Everything outside VM constraints is writable" is now machine-checked at the
        // kind level. Remaining capability holes are SUB-kind (guarded by loud per-arm rejects): v2b
        // (typeobj/is-cast/virtual) and interface method groups.
    };

    [Fact]
    public void EveryOperationKind_IsClassified()
    {
        TestHelper.CompileToUasm(@"using UdonSharp;
public class Cen : UdonSharpBehaviour { public int result; void Start(){ result = 1; } }", "Cen", out var emitter);

        var handled = new HashSet<OperationKind>(emitter.HandledOperationKinds);
        foreach (var k in HandledInternal) handled.Add(k);

        var buckets = new Dictionary<OperationKind, string>();
        void Put(IEnumerable<OperationKind> kinds, string name)
        {
            foreach (var k in kinds)
            {
                Assert.False(buckets.TryGetValue(k, out var prev) && prev != name,
                    $"{k} classified twice: {buckets.GetValueOrDefault(k)} and {name}");
                buckets[k] = name;
            }
        }
        Put(handled, "handled");
        Put(VmFundamental, "vm");
        Put(DocumentedReject, "reject");
        Put(OutOfScope, "oos");
        Put(KnownGaps, "gap");

        // CollectionElementInitializer is [Obsolete(error)] and unreferencable in source: the kind is
        // dead — collection initializers lower to IInvocationOperation (Add), which the ordinary
        // invocation path handles/rejects. Filter by name at runtime.
        var all = Enum.GetValues<OperationKind>().Distinct()
            .Where(k => k.ToString() != "CollectionElementInitializer").ToList();
        var unclassified = all.Where(k => !buckets.ContainsKey(k)).OrderBy(k => k.ToString()).ToList();

        Assert.True(unclassified.Count == 0,
            "UNCLASSIFIED OperationKinds (assign each to a bucket):\n  "
            + string.Join("\n  ", unclassified)
            + $"\n\ncounts: handled={buckets.Count(kv => kv.Value == "handled")}"
            + $" vm={VmFundamental.Length} reject={DocumentedReject.Length}"
            + $" oos={OutOfScope.Length} gap={KnownGaps.Length} total={all.Count}");
    }
}
