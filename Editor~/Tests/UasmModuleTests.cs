using System.Linq;
using Xunit;

namespace USugar.Tests;

public class UasmModuleTests
{
    [Fact]
    public void EmptyModule_BuildsValidUasm()
    {
        var module = new UasmModule();
        var uasm = module.BuildUasmStr();
        Assert.Contains(".data_start", uasm);
        Assert.Contains(".data_end", uasm);
        Assert.Contains(".code_start", uasm);
        Assert.Contains(".code_end", uasm);
    }

    [Fact]
    public void DeclareVariable_AppearsInDataBlock()
    {
        var module = new UasmModule();
        module.DeclareVariable("myField", "SystemInt32", null, VarFlags.Export);
        var uasm = module.BuildUasmStr();
        Assert.Contains("    .export myField", uasm);
        Assert.Contains("    myField: %SystemInt32, null", uasm);
    }

    [Fact]
    public void SyncVariable_AppearsInDataBlock()
    {
        var module = new UasmModule();
        module.DeclareVariable("_synced", "SystemInt32", null, VarFlags.Sync);
        var uasm = module.BuildUasmStr();
        Assert.Contains("    .sync _synced, none", uasm);
        Assert.Contains("    _synced: %SystemInt32, null", uasm);
    }

    [Fact]
    public void PushCopyJump_CorrectUasm()
    {
        var module = new UasmModule();
        module.DeclareVariable("src", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("dst", "SystemInt32", null, VarFlags.None);
        var lbl = module.DefineLabel("target");
        module.AddExport("_start", lbl);
        module.MarkLabel(lbl);
        module.AddCopy("src", "dst");
        module.AddJump(lbl);
        var uasm = module.BuildUasmStr();
        Assert.Contains("        PUSH, src\n        PUSH, dst\n        COPY", uasm);
        Assert.Matches(@"JUMP, 0x[0-9A-F]{8}", uasm);
    }

    [Fact]
    public void Extern_CorrectUasm()
    {
        var module = new UasmModule();
        module.DeclareVariable("a", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("b", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("r", "SystemInt32", null, VarFlags.None);
        var lbl = module.DefineLabel("_start");
        module.AddExport("_start", lbl);
        module.MarkLabel(lbl);
        module.AddPush("a");
        module.AddPush("b");
        module.AddPush("r");
        module.AddExtern("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        var uasm = module.BuildUasmStr();
        Assert.Contains("        EXTERN, \"SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32\"", uasm);
    }

    [Fact]
    public void JumpIfFalse_CorrectFormat()
    {
        var module = new UasmModule();
        module.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
        var lbl = module.DefineLabel("_start");
        var skip = module.DefineLabel("skip");
        module.AddExport("_start", lbl);
        module.MarkLabel(lbl);
        module.AddPush("cond");
        module.AddJumpIfFalse(skip);
        module.MarkLabel(skip);
        var uasm = module.BuildUasmStr();
        Assert.Matches(@"JUMP_IF_FALSE, 0x[0-9a-f]{8}", uasm);
    }

    [Fact]
    public void Return_StackBased_PushCopyJumpIndirect()
    {
        var module = new UasmModule();
        module.DeclareVariable("__intnl_returnJump_SystemUInt32_0", "SystemUInt32", null, VarFlags.None);
        var lbl = module.DefineLabel("_start");
        module.AddExport("_start", lbl);
        module.MarkLabel(lbl);
        module.AddReturn("__intnl_returnJump_SystemUInt32_0");
        var uasm = module.BuildUasmStr();
        // Stack-based return: PUSH returnJump; COPY; JUMP_INDIRECT returnJump
        Assert.Contains("        PUSH, __intnl_returnJump_SystemUInt32_0\n        COPY\n        JUMP_INDIRECT, __intnl_returnJump_SystemUInt32_0", uasm);
    }

    [Fact]
    public void HeapSize_CountsVariablesAndExterns()
    {
        var module = new UasmModule();
        module.DeclareVariable("a", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("b", "SystemInt32", null, VarFlags.None);
        var lbl = module.DefineLabel("_start");
        module.AddExport("_start", lbl);
        module.MarkLabel(lbl);
        module.AddExtern("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        Assert.Equal((uint)3, module.GetHeapSize());
    }

    [Fact]
    public void HeapSize_IncludesPushLabelRetaddrVars()
    {
        var module = new UasmModule();
        module.DeclareVariable("__intnl_returnJump_SystemUInt32_0", "SystemUInt32", "0xFFFFFFFF", VarFlags.None);
        var start = module.DefineLabel("_start");
        var target = module.DefineLabel("__target");
        var ret = module.DefineLabel("__ret");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        // Stack-based call: PushLabel generates a retaddr variable in BuildUasmStr
        module.AddPushLabel(ret);
        module.AddJump(target);
        module.MarkLabel(ret);
        module.MarkLabel(target);
        module.AddReturn("__intnl_returnJump_SystemUInt32_0");
        // 1 var + 0 externs + 1 PushLabel = 2
        Assert.Equal((uint)2, module.GetHeapSize());
    }

    [Fact]
    public void BuildUasmStr_IsIdempotent()
    {
        var module = new UasmModule();
        module.DeclareVariable("a", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_returnJump_SystemUInt32_0", "SystemUInt32", "0xFFFFFFFF", VarFlags.None);
        var start = module.DefineLabel("_start");
        var ret = module.DefineLabel("__ret");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        // Stack-based call pattern
        module.AddPushLabel(ret);
        module.AddJump(start);
        module.MarkLabel(ret);
        module.AddReturn("__intnl_returnJump_SystemUInt32_0");

        var first = module.BuildUasmStr();
        var second = module.BuildUasmStr();
        Assert.Equal(first, second);
    }

    // === CopyTest Optimization Tests ===

    [Fact]
    public void CopyTest_BasicPattern_EliminatesCopyAndTemp()
    {
        var module = new UasmModule();
        module.DeclareVariable("src", "SystemBoolean", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemBoolean_0", "SystemBoolean", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var skip = module.DefineLabel("skip");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        // Pattern: PUSH src; PUSH __intnl; COPY; PUSH __intnl; JIF
        module.AddPush("src");
        module.AddPush("__intnl_SystemBoolean_0");
        module.AddCopyRaw();
        module.AddPush("__intnl_SystemBoolean_0");
        module.AddJumpIfFalse(skip);
        module.MarkLabel(skip);

        module.Optimize();
        var uasm = module.BuildUasmStr();

        // Should become: PUSH src; JIF
        Assert.Contains("PUSH, src", uasm);
        Assert.DoesNotContain("COPY", uasm);
        Assert.DoesNotContain("__intnl_SystemBoolean_0", uasm);
    }

    [Fact]
    public void CopyTest_DstUsedElsewhere_NotOptimized()
    {
        var module = new UasmModule();
        module.DeclareVariable("src", "SystemBoolean", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemBoolean_0", "SystemBoolean", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var skip = module.DefineLabel("skip");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        // Pattern: PUSH src; PUSH __intnl; COPY; PUSH __intnl; JIF
        module.AddPush("src");
        module.AddPush("__intnl_SystemBoolean_0");
        module.AddCopyRaw();
        module.AddPush("__intnl_SystemBoolean_0");
        module.AddJumpIfFalse(skip);
        // Extra use of __intnl → 3 PUSHes total → not optimized
        module.AddPush("__intnl_SystemBoolean_0");
        module.AddExtern("some_extern");
        module.MarkLabel(skip);

        module.Optimize();
        var uasm = module.BuildUasmStr();

        Assert.Contains("COPY", uasm);
        Assert.Contains("__intnl_SystemBoolean_0", uasm);
    }

    [Fact]
    public void CopyTest_NonIntnlDst_NotOptimized()
    {
        var module = new UasmModule();
        module.DeclareVariable("src", "SystemBoolean", null, VarFlags.None);
        module.DeclareVariable("__lcl_cond", "SystemBoolean", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var skip = module.DefineLabel("skip");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddPush("src");
        module.AddPush("__lcl_cond");
        module.AddCopyRaw();
        module.AddPush("__lcl_cond");
        module.AddJumpIfFalse(skip);
        module.MarkLabel(skip);

        module.Optimize();
        var uasm = module.BuildUasmStr();

        Assert.Contains("COPY", uasm);
        Assert.Contains("__lcl_cond", uasm);
    }

    // === Variable Reduction Tests ===

    [Fact]
    public void VarReduction_NonOverlapping_Merged()
    {
        // Two internal vars with non-overlapping lifetimes (written by extern, then read)
        var module = new UasmModule();
        module.DeclareVariable("arg", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_1", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("out1", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("out2", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        // Phase 1: _0 written by extern, read, then dead
        module.AddPush("arg");
        module.AddPush("__intnl_SystemInt32_0");
        module.AddExtern("SystemConvert.__ToInt32__SystemObject__SystemInt32");
        module.AddCopy("__intnl_SystemInt32_0", "out1");
        // Phase 2: _1 written by extern, read → non-overlapping with _0
        module.AddPush("arg");
        module.AddPush("__intnl_SystemInt32_1");
        module.AddExtern("SystemConvert.__ToInt32__SystemObject__SystemInt32");
        module.AddCopy("__intnl_SystemInt32_1", "out2");

        module.Optimize();
        var uasm = module.BuildUasmStr();

        // __intnl_1 should be merged into __intnl_0
        Assert.DoesNotContain("__intnl_SystemInt32_1", uasm);
        Assert.Contains("__intnl_SystemInt32_0", uasm);
    }

    [Fact]
    public void VarReduction_Overlapping_NotMerged()
    {
        var module = new UasmModule();
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_1", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("r", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        // Overlapping: both used, then both used again
        module.AddPush("__intnl_SystemInt32_0");
        module.AddPush("__intnl_SystemInt32_1");
        module.AddPush("r");
        module.AddExtern("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        module.AddPush("__intnl_SystemInt32_0");
        module.AddPush("__intnl_SystemInt32_1");
        module.AddPush("r");
        module.AddExtern("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");

        module.Optimize();
        var uasm = module.BuildUasmStr();

        Assert.Contains("__intnl_SystemInt32_0", uasm);
        Assert.Contains("__intnl_SystemInt32_1", uasm);
    }

    [Fact]
    public void VarReduction_Loop_ExtendsLifetime()
    {
        var module = new UasmModule();
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_1", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var loopTop = module.DefineLabel("loop");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.MarkLabel(loopTop);
        // __intnl_0 used at loop top
        module.AddPush("__intnl_SystemInt32_0");
        module.AddPop();
        // __intnl_1 used after __intnl_0 but within the loop
        module.AddPush("__intnl_SystemInt32_1");
        module.AddPop();
        // Back-jump creates loop region
        module.AddPush("cond");
        module.AddJumpIfFalse(loopTop);

        module.Optimize();
        var uasm = module.BuildUasmStr();

        // Loop extends both lifetimes to overlap → not merged
        Assert.Contains("__intnl_SystemInt32_0", uasm);
        Assert.Contains("__intnl_SystemInt32_1", uasm);
    }

    [Fact]
    public void VarReduction_DifferentTypes_NotMerged()
    {
        var module = new UasmModule();
        module.DeclareVariable("__intnl_SystemBoolean_0", "SystemBoolean", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("out1", "SystemBoolean", null, VarFlags.None);
        module.DeclareVariable("out2", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        // Non-overlapping but different types
        module.AddPush("__intnl_SystemBoolean_0");
        module.AddPush("out1");
        module.AddCopyRaw();
        module.AddPush("__intnl_SystemInt32_0");
        module.AddPush("out2");
        module.AddCopyRaw();

        module.Optimize();
        var uasm = module.BuildUasmStr();

        // Different types → cannot merge
        Assert.Contains("__intnl_SystemBoolean_0", uasm);
        Assert.Contains("__intnl_SystemInt32_0", uasm);
    }

    [Fact]
    public void UnmarkedLabel_ReferencedByJump_Throws()
    {
        var module = new UasmModule();
        var label = module.DefineLabel("__unreachable");
        module.AddJump(label); // reference but never MarkLabel
        Assert.Throws<System.InvalidOperationException>(() => module.BuildUasmStr());
    }

    [Fact]
    public void UnmarkedLabel_NotReferenced_DoesNotThrow()
    {
        var module = new UasmModule();
        module.DefineLabel("__orphan"); // defined but never referenced or marked
        var uasm = module.BuildUasmStr(); // should NOT throw
        Assert.Contains(".code_start", uasm);
    }

    // === Copy Propagation Tests ===

    [Fact]
    public void CopyProp_BasicChain_EliminatesTemp()
    {
        var module = new UasmModule();
        module.DeclareVariable("a", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("dst", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddCopy("a", "__intnl_SystemInt32_0");
        module.AddCopy("__intnl_SystemInt32_0", "dst");

        module.Optimize();
        var uasm = module.BuildUasmStr();

        Assert.DoesNotContain("__intnl_SystemInt32_0", uasm);
        Assert.Contains("PUSH, a", uasm);
        Assert.Contains("PUSH, dst", uasm);
    }

    [Fact]
    public void CopyProp_SrcModifiedBetween_NotPropagated()
    {
        var module = new UasmModule();
        module.DeclareVariable("a", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("dst", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddCopy("a", "__intnl_SystemInt32_0");
        module.AddCopy("x", "a");  // modifies src between def and use
        module.AddCopy("__intnl_SystemInt32_0", "dst");

        module.Optimize();
        var uasm = module.BuildUasmStr();

        var codeSection = uasm.Substring(uasm.IndexOf(".code_start"));
        Assert.Contains("__intnl_SystemInt32_0", codeSection);
    }

    [Fact]
    public void CopyProp_ConstSource_AlwaysSafe()
    {
        var module = new UasmModule();
        module.DeclareVariable("__const_SystemInt32_0", "SystemInt32", "42", VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("dst", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddCopy("__const_SystemInt32_0", "__intnl_SystemInt32_0");
        module.AddCopy("x", "dst"); // intervening instruction
        module.AddCopy("__intnl_SystemInt32_0", "dst");

        module.Optimize();
        var uasm = module.BuildUasmStr();

        Assert.DoesNotContain("__intnl_SystemInt32_0", uasm);
        Assert.Contains("__const_SystemInt32_0", uasm);
    }

    // === Dead Store Elimination Tests ===

    [Fact]
    public void DeadStore_ConsecutiveWrites_FirstRemoved()
    {
        var module = new UasmModule();
        module.DeclareVariable("a", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("b", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("dst", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddCopy("a", "__intnl_SystemInt32_0");  // dead store
        module.AddCopy("b", "__intnl_SystemInt32_0");  // overwrites
        module.AddCopy("__intnl_SystemInt32_0", "dst");

        module.Optimize();
        var uasm = module.BuildUasmStr();

        var codeSection = uasm.Substring(uasm.IndexOf(".code_start"));
        Assert.DoesNotContain("PUSH, a", codeSection);
        Assert.Contains("PUSH, b", codeSection);
    }

    [Fact]
    public void DeadStore_LabelBetween_NotRemoved()
    {
        // Cross-block DSE: conditional branch to mid means _0=a is live on the false path
        var module = new UasmModule();
        module.DeclareVariable("a", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("b", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("dst", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var mid = module.DefineLabel("mid");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddCopy("a", "__intnl_SystemInt32_0");
        module.AddPush("cond");
        module.AddJumpIfFalse(mid); // branch to mid → _0=a is live on false path
        module.AddCopy("b", "__intnl_SystemInt32_0");
        module.MarkLabel(mid);
        module.AddCopy("__intnl_SystemInt32_0", "dst");

        module.Optimize();
        var uasm = module.BuildUasmStr();

        var codeSection = uasm.Substring(uasm.IndexOf(".code_start"));
        Assert.Contains("PUSH, a", codeSection); // first write preserved (live cross-block)
    }

    // === Jump Threading Tests ===

    [Fact]
    public void JumpThread_ChainedJumps_ResolvesToFinal()
    {
        var module = new UasmModule();
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var L1 = module.DefineLabel("L1");
        var L2 = module.DefineLabel("L2");
        var final_ = module.DefineLabel("final");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddJump(L1);
        module.MarkLabel(L1);
        module.AddJump(L2);
        module.MarkLabel(L2);
        module.AddJump(final_);
        module.MarkLabel(final_);
        module.AddPush("x");
        module.AddPop();

        module.Optimize();
        var uasm = module.BuildUasmStr();

        // All JUMPs should resolve to the same final address
        var jumpLines = uasm.Split('\n')
            .Where(l => l.Trim().StartsWith("JUMP,"))
            .Select(l => l.Trim())
            .ToArray();
        if (jumpLines.Length > 1)
        {
            var addresses = jumpLines.Select(l => l.Split(',')[1].Trim()).Distinct().ToArray();
            Assert.Single(addresses);
        }
    }

    [Fact]
    public void JumpThread_Circular_StopsGracefully()
    {
        var module = new UasmModule();
        var start = module.DefineLabel("_start");
        var L1 = module.DefineLabel("L1");
        var L2 = module.DefineLabel("L2");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddJump(L1);
        module.MarkLabel(L1);
        module.AddJump(L2);
        module.MarkLabel(L2);
        module.AddJump(L1); // circular

        module.Optimize();
        var uasm = module.BuildUasmStr();

        Assert.Contains("JUMP", uasm); // does not hang, produces valid output
    }

    // === Redundant Jump Elimination Tests ===

    [Fact]
    public void RedundantJump_TargetIsNext_Removed()
    {
        var module = new UasmModule();
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var next = module.DefineLabel("next");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddJump(next);
        module.MarkLabel(next);
        module.AddPush("x");
        module.AddPop();

        module.Optimize();
        var uasm = module.BuildUasmStr();

        Assert.DoesNotContain("JUMP", uasm);
    }

    [Fact]
    public void RedundantJump_CodeBetween_NotRemoved()
    {
        // CFG: unreachable code between JUMP and target is removed,
        // then JUMP becomes fall-through and is also removed.
        var module = new UasmModule();
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var mid = module.DefineLabel("mid");
        var target = module.DefineLabel("target");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddJump(target);
        module.MarkLabel(mid); // no jump to mid → unreachable
        module.AddPush("x");
        module.AddPop();
        module.MarkLabel(target);
        module.AddPush("x");
        module.AddPop();

        module.Optimize();
        var uasm = module.BuildUasmStr();

        Assert.DoesNotContain("JUMP", uasm); // unreachable block removed, fall-through optimized
    }

    // === Dead Code Elimination Tests ===

    [Fact]
    public void DeadCode_AfterJump_Removed()
    {
        var module = new UasmModule();
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("y", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var target = module.DefineLabel("target");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddJump(target);
        module.AddPush("x");    // dead
        module.AddPush("y");    // dead
        module.AddCopyRaw();    // dead
        module.MarkLabel(target);
        module.AddPush("x");
        module.AddPop();

        module.Optimize();
        var uasm = module.BuildUasmStr();

        var codeSection = uasm.Substring(uasm.IndexOf(".code_start"));
        Assert.DoesNotContain("PUSH, y", codeSection);
        Assert.DoesNotContain("COPY", codeSection);
    }

    [Fact]
    public void DeadCode_StopsAtLabel()
    {
        // CFG: unreachable blocks are fully removed regardless of labels.
        // mid is not a jump target → entire block is unreachable.
        var module = new UasmModule();
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("y", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var mid = module.DefineLabel("mid");
        var target = module.DefineLabel("target");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddJump(target);
        module.AddPush("x");   // dead (unreachable)
        module.AddPop();        // dead
        module.MarkLabel(mid);  // no jump to mid → still unreachable
        module.AddPush("y");    // dead (unreachable)
        module.AddPop();
        module.MarkLabel(target);
        module.AddPush("x");
        module.AddPop();

        module.Optimize();
        var uasm = module.BuildUasmStr();

        var codeSection = uasm.Substring(uasm.IndexOf(".code_start"));
        var pushXCount = codeSection.Split('\n').Count(l => l.Contains("PUSH, x"));
        Assert.Equal(1, pushXCount); // dead PUSH x removed, one at target preserved
        Assert.DoesNotContain("PUSH, y", codeSection); // unreachable block fully removed
    }

    // === Unused Variable Removal Test ===

    [Fact]
    public void UnusedVar_Removed_AfterOptimization()
    {
        var module = new UasmModule();
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_1", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("dst", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddCopy("__intnl_SystemInt32_0", "dst");
        // __intnl_1 is never referenced

        module.Optimize();
        var uasm = module.BuildUasmStr();

        Assert.DoesNotContain("__intnl_SystemInt32_1", uasm);
        Assert.Contains("__intnl_SystemInt32_0", uasm);
    }
}

public class ConstPoolTests
{
    [Fact]
    public void CreateConstVariable_SameValue_ReturnsSameId()
    {
        var mod = new UasmModule();
        var id1 = mod.CreateConstVariable("SystemInt32", 42);
        var id2 = mod.CreateConstVariable("SystemInt32", 42);
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void CreateConstVariable_DifferentValue_ReturnsDifferentId()
    {
        var mod = new UasmModule();
        var id1 = mod.CreateConstVariable("SystemInt32", 42);
        var id2 = mod.CreateConstVariable("SystemInt32", 99);
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void CreateConstVariable_DifferentType_ReturnsDifferentId()
    {
        var mod = new UasmModule();
        var id1 = mod.CreateConstVariable("SystemInt32", 42);
        var id2 = mod.CreateConstVariable("SystemInt64", 42);
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void DeclareVariable_PopulatesConstCache_ForCreateConstVariable()
    {
        // M-1: DeclareVariable with constValue should be found by CreateConstVariable
        var mod = new UasmModule();
        mod.DeclareVariable("__const_SystemInt32_0", "SystemInt32", null, VarFlags.None, constValue: 42);
        var id = mod.CreateConstVariable("SystemInt32", 42);
        Assert.Equal("__const_SystemInt32_0", id);
    }
}
