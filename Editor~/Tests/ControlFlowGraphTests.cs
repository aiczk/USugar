using System.Linq;
using Xunit;

namespace USugar.Tests;

public class BitSetTests
{
    [Fact]
    public void Create_AllZero()
    {
        var bs = BitSet.Create(128);
        for (int i = 0; i < 128; i++)
            Assert.False(bs.Get(i));
    }

    [Fact]
    public void SetAndGet()
    {
        var bs = BitSet.Create(64);
        bs.Set(0);
        bs.Set(42);
        bs.Set(63);
        Assert.True(bs.Get(0));
        Assert.True(bs.Get(42));
        Assert.True(bs.Get(63));
        Assert.False(bs.Get(1));
    }

    [Fact]
    public void Clear()
    {
        var bs = BitSet.Create(64);
        bs.Set(10);
        bs.Clear(10);
        Assert.False(bs.Get(10));
    }

    [Fact]
    public void UnionWith()
    {
        var a = BitSet.Create(64);
        var b = BitSet.Create(64);
        a.Set(1); a.Set(3);
        b.Set(2); b.Set(3);
        a.UnionWith(b);
        Assert.True(a.Get(1));
        Assert.True(a.Get(2));
        Assert.True(a.Get(3));
    }

    [Fact]
    public void ExceptWith()
    {
        var a = BitSet.Create(64);
        var b = BitSet.Create(64);
        a.Set(1); a.Set(2); a.Set(3);
        b.Set(2); b.Set(3);
        a.ExceptWith(b);
        Assert.True(a.Get(1));
        Assert.False(a.Get(2));
        Assert.False(a.Get(3));
    }

    [Fact]
    public void Equals_SameBits_True()
    {
        var a = BitSet.Create(64);
        var b = BitSet.Create(64);
        a.Set(5); b.Set(5);
        Assert.True(a.SetEquals(b));
    }

    [Fact]
    public void Equals_DifferentBits_False()
    {
        var a = BitSet.Create(64);
        var b = BitSet.Create(64);
        a.Set(5); b.Set(6);
        Assert.False(a.SetEquals(b));
    }

    [Fact]
    public void Copy_Independent()
    {
        var a = BitSet.Create(64);
        a.Set(10);
        var b = a.Copy();
        b.Set(20);
        Assert.False(a.Get(20));
        Assert.True(b.Get(10));
    }

    [Fact]
    public void LargeCapacity_Works()
    {
        var bs = BitSet.Create(500);
        bs.Set(499);
        Assert.True(bs.Get(499));
        Assert.False(bs.Get(498));
    }
}

public class CfgBuildTests
{
    static ControlFlowGraph BuildFromModule(System.Action<UasmModule> setup)
    {
        var module = new UasmModule();
        setup(module);
        return ControlFlowGraph.Build(module);
    }

    [Fact]
    public void LinearCode_SingleBlock()
    {
        var cfg = BuildFromModule(m =>
        {
            m.DeclareVariable("a", "SystemInt32", null, VarFlags.None);
            var start = m.DefineLabel("_start");
            m.AddExport("_start", start);
            m.MarkLabel(start);
            m.AddPush("a");
            m.AddPop();
        });
        Assert.True(cfg.Blocks.Count >= 1);
        Assert.NotNull(cfg.Entry);
    }

    [Fact]
    public void IfElse_ThreeBlocks()
    {
        var cfg = BuildFromModule(m =>
        {
            m.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
            m.DeclareVariable("a", "SystemInt32", null, VarFlags.None);
            m.DeclareVariable("b", "SystemInt32", null, VarFlags.None);
            var start = m.DefineLabel("_start");
            var elseL = m.DefineLabel("else");
            var end = m.DefineLabel("end");
            m.AddExport("_start", start);
            m.MarkLabel(start);
            m.AddPush("cond");
            m.AddJumpIfFalse(elseL);
            m.AddPush("a");
            m.AddPop();
            m.AddJump(end);
            m.MarkLabel(elseL);
            m.AddPush("b");
            m.AddPop();
            m.MarkLabel(end);
        });
        Assert.True(cfg.Blocks.Count >= 3);
    }

    [Fact]
    public void Loop_HasBackEdge()
    {
        var cfg = BuildFromModule(m =>
        {
            m.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
            var start = m.DefineLabel("_start");
            var loop = m.DefineLabel("loop");
            m.AddExport("_start", start);
            m.MarkLabel(start);
            m.MarkLabel(loop);
            m.AddPush("cond");
            m.AddJumpIfFalse(loop);
        });
        var loopBlock = cfg.Blocks.FirstOrDefault(b =>
            b.Successors.Any(s => s.Id <= b.Id));
        Assert.NotNull(loopBlock);
    }

    [Fact]
    public void Predecessors_Symmetric_WithSuccessors()
    {
        var cfg = BuildFromModule(m =>
        {
            m.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
            var start = m.DefineLabel("_start");
            var target = m.DefineLabel("target");
            m.AddExport("_start", start);
            m.MarkLabel(start);
            m.AddPush("cond");
            m.AddJumpIfFalse(target);
            m.MarkLabel(target);
        });
        foreach (var b in cfg.Blocks)
            foreach (var s in b.Successors)
                Assert.Contains(b, s.Predecessors);
    }
}

public class CfgLinearizeTests
{
    [Fact]
    public void Roundtrip_LinearCode_PreservesSemantics()
    {
        var module = new UasmModule();
        module.DeclareVariable("a", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("b", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddCopy("a", "b");

        var origUasm = module.BuildUasmStr();
        var cfg = ControlFlowGraph.Build(module);
        cfg.Linearize(module);
        var roundtrippedUasm = module.BuildUasmStr();

        Assert.Equal(origUasm, roundtrippedUasm);
    }

    [Fact]
    public void Roundtrip_IfElse_PreservesSemantics()
    {
        var module = new UasmModule();
        module.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
        module.DeclareVariable("a", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("b", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("dst", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var elseL = module.DefineLabel("else");
        var end = module.DefineLabel("end");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddPush("cond");
        module.AddJumpIfFalse(elseL);
        module.AddCopy("a", "dst");
        module.AddJump(end);
        module.MarkLabel(elseL);
        module.AddCopy("b", "dst");
        module.MarkLabel(end);

        var origUasm = module.BuildUasmStr();
        var cfg = ControlFlowGraph.Build(module);
        cfg.Linearize(module);
        var roundtrippedUasm = module.BuildUasmStr();

        Assert.Equal(origUasm, roundtrippedUasm);
    }

    [Fact]
    public void Roundtrip_Loop_PreservesSemantics()
    {
        var module = new UasmModule();
        module.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var loop = module.DefineLabel("loop");
        var end = module.DefineLabel("end");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.MarkLabel(loop);
        module.AddPush("x");
        module.AddPop();
        module.AddPush("cond");
        module.AddJumpIfFalse(end);
        module.AddJump(loop);
        module.MarkLabel(end);

        var origUasm = module.BuildUasmStr();
        var cfg = ControlFlowGraph.Build(module);
        cfg.Linearize(module);
        var roundtrippedUasm = module.BuildUasmStr();

        Assert.Equal(origUasm, roundtrippedUasm);
    }

    [Fact]
    public void FallThrough_RemovesRedundantJump()
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

        var cfg = ControlFlowGraph.Build(module);
        cfg.Linearize(module);
        var uasm = module.BuildUasmStr();

        Assert.DoesNotContain("JUMP", uasm);
    }
}

public class SimplifyCfgTests
{
    [Fact]
    public void JumpThreading_ChainResolved()
    {
        var module = new UasmModule();
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var L1 = module.DefineLabel("L1");
        var L2 = module.DefineLabel("L2");
        var fin = module.DefineLabel("final");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddJump(L1);
        module.MarkLabel(L1);
        module.AddJump(L2);
        module.MarkLabel(L2);
        module.AddJump(fin);
        module.MarkLabel(fin);
        module.AddPush("x");
        module.AddPop();

        var cfg = ControlFlowGraph.Build(module);
        cfg.SimplifyCFG();
        cfg.Linearize(module);
        var uasm = module.BuildUasmStr();

        Assert.Contains("PUSH, x", uasm);
    }

    [Fact]
    public void UnreachableBlock_Removed()
    {
        var module = new UasmModule();
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("y", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var target = module.DefineLabel("target");
        var dead = module.DefineLabel("dead");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddJump(target);
        module.MarkLabel(dead);
        module.AddPush("y");
        module.AddPop();
        module.MarkLabel(target);
        module.AddPush("x");
        module.AddPop();

        var cfg = ControlFlowGraph.Build(module);
        cfg.SimplifyCFG();
        cfg.Linearize(module);
        var uasm = module.BuildUasmStr();

        var codeSection = uasm.Substring(uasm.IndexOf(".code_start"));
        Assert.DoesNotContain("PUSH, y", codeSection);
        Assert.Contains("PUSH, x", codeSection);
    }

    [Fact]
    public void BlockMerging_SinglePredSucc_Merged()
    {
        var module = new UasmModule();
        module.DeclareVariable("a", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("b", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var mid = module.DefineLabel("mid");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddPush("a");
        module.AddPop();
        module.AddJump(mid);
        module.MarkLabel(mid);
        module.AddPush("b");
        module.AddPop();

        var cfg = ControlFlowGraph.Build(module);
        var blocksBefore = cfg.Blocks.Count;
        cfg.SimplifyCFG();

        Assert.True(cfg.Blocks.Count < blocksBefore);
    }

    [Fact]
    public void ThreadJumps_EdgeAndInstruction_StayConsistent()
    {
        // A→B→C chain: after SimplifyCFG, A should jump directly to C
        // and both CFG edges and instruction should agree
        var module = new UasmModule();
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var mid = module.DefineLabel("mid");
        var fin = module.DefineLabel("fin");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddJump(mid);
        module.MarkLabel(mid);
        module.AddJump(fin);
        module.MarkLabel(fin);
        module.AddPush("x");
        module.AddPop();

        var cfg = ControlFlowGraph.Build(module);
        cfg.SimplifyCFG();

        // After simplification, every block's JUMP instruction target
        // should match its successor
        foreach (var block in cfg.Blocks)
        {
            var lastReal = block.Instructions.FindLastIndex(
                inst => inst.Kind is not (InstKind.Label or InstKind.LabelAddr or InstKind.Export));
            if (lastReal < 0) continue;
            var inst = block.Instructions[lastReal];
            if (inst.Kind != InstKind.Jump) continue;
            if (block.Successors.Count != 1) continue;

            var succ = block.Successors[0];
            var succLabel = succ.Instructions
                .Where(i => i.Kind is InstKind.Label or InstKind.Export or InstKind.LabelAddr)
                .Select(i => i.LabelIndex)
                .FirstOrDefault(-1);
            if (succLabel >= 0)
                Assert.Equal(succLabel, inst.LabelIndex);
        }
    }
}

public class LivenessTests
{
    [Fact]
    public void SimpleBlock_GenKill_Correct()
    {
        var module = new UasmModule();
        module.DeclareVariable("a", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("b", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddCopy("a", "b");

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeLiveness();

        var block = cfg.Entry;
        var aIdx = cfg.VarToIndex["a"];
        var bIdx = cfg.VarToIndex["b"];
        Assert.True(block.Gen.Get(aIdx));
        Assert.True(block.Kill.Get(bIdx));
        Assert.False(block.Gen.Get(bIdx));
    }

    [Fact]
    public void CrossBlock_LiveOut_PropagatesBackward()
    {
        var module = new UasmModule();
        module.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var elseL = module.DefineLabel("else");
        var end = module.DefineLabel("end");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddPush("x");
        module.AddPop();
        module.AddPush("cond");
        module.AddJumpIfFalse(elseL);
        module.AddJump(end);
        module.MarkLabel(elseL);
        module.AddPush("x");
        module.AddExtern("SystemVoid.__op_Print__SystemObject__SystemVoid");
        module.MarkLabel(end);

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeLiveness();

        var xIdx = cfg.VarToIndex["x"];
        var elseBlock = cfg.Blocks.First(b =>
            b.Instructions.Any(i => i.Kind == InstKind.Extern));
        Assert.True(elseBlock.LiveIn.Get(xIdx));
    }

    [Fact]
    public void NullCoalesce_DSEBug_DetectedByLiveness()
    {
        var module = new UasmModule();
        module.DeclareVariable("leftVal", "SystemString", null, VarFlags.None);
        module.DeclareVariable("rightVal", "SystemString", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemString_0", "SystemString", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var nullPath = module.DefineLabel("nullPath");
        var end = module.DefineLabel("end");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddCopy("leftVal", "__intnl_SystemString_0");
        module.AddPush("__intnl_SystemString_0");
        module.AddJumpIfFalse(nullPath);
        module.AddJump(end);
        module.MarkLabel(nullPath);
        module.AddCopy("rightVal", "__intnl_SystemString_0");
        module.MarkLabel(end);
        module.AddPush("__intnl_SystemString_0");
        module.AddExtern("SystemVoid.__op_Print__SystemObject__SystemVoid");

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeLiveness();

        var resultIdx = cfg.VarToIndex["__intnl_SystemString_0"];
        Assert.True(cfg.Entry.LiveOut.Get(resultIdx));
    }
}

public class CfgCopyPropTests
{
    [Fact]
    public void BasicChain_Eliminated()
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

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeLiveness();
        cfg.CopyPropagation();
        cfg.Linearize(module);
        var uasm = module.BuildUasmStr();

        var code = uasm.Substring(uasm.IndexOf(".code_start"));
        Assert.DoesNotContain("__intnl_SystemInt32_0", code);
        Assert.Contains("PUSH, a", code);
    }

    [Fact]
    public void SrcModified_NotPropagated()
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
        module.AddCopy("x", "a");
        module.AddCopy("__intnl_SystemInt32_0", "dst");

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeLiveness();
        cfg.CopyPropagation();
        cfg.Linearize(module);
        var uasm = module.BuildUasmStr();

        var codeSection = uasm.Substring(uasm.IndexOf(".code_start"));
        Assert.Contains("__intnl_SystemInt32_0", codeSection);
    }

    [Fact]
    public void CopyTestPattern_AbsorbedByCopyProp()
    {
        var module = new UasmModule();
        module.DeclareVariable("src", "SystemBoolean", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemBoolean_0", "SystemBoolean", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var skip = module.DefineLabel("skip");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddPush("src");
        module.AddPush("__intnl_SystemBoolean_0");
        module.AddCopyRaw();
        module.AddPush("__intnl_SystemBoolean_0");
        module.AddJumpIfFalse(skip);
        module.MarkLabel(skip);

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeLiveness();
        cfg.CopyPropagation();
        cfg.Linearize(module);
        var uasm = module.BuildUasmStr();

        var code = uasm.Substring(uasm.IndexOf(".code_start"));
        Assert.Contains("PUSH, src", code);
        Assert.DoesNotContain("__intnl_SystemBoolean_0", code);
    }
}

public class CfgDseTests
{
    [Fact]
    public void ConsecutiveWrites_FirstRemoved()
    {
        var module = new UasmModule();
        module.DeclareVariable("a", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("b", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("dst", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddCopy("a", "__intnl_SystemInt32_0");
        module.AddCopy("b", "__intnl_SystemInt32_0");
        module.AddCopy("__intnl_SystemInt32_0", "dst");

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeLiveness();
        cfg.DeadStoreElimination();
        cfg.Linearize(module);
        var uasm = module.BuildUasmStr();

        var code = uasm.Substring(uasm.IndexOf(".code_start"));
        Assert.DoesNotContain("PUSH, a", code);
        Assert.Contains("PUSH, b", code);
    }

    [Fact]
    public void NullCoalesce_WriteBeforeBranch_NotRemoved()
    {
        var module = new UasmModule();
        module.DeclareVariable("leftVal", "SystemString", null, VarFlags.None);
        module.DeclareVariable("rightVal", "SystemString", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemString_0", "SystemString", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var nullPath = module.DefineLabel("nullPath");
        var end = module.DefineLabel("end");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddCopy("leftVal", "__intnl_SystemString_0");
        module.AddPush("__intnl_SystemString_0");
        module.AddJumpIfFalse(nullPath);
        module.AddJump(end);
        module.MarkLabel(nullPath);
        module.AddCopy("rightVal", "__intnl_SystemString_0");
        module.MarkLabel(end);
        module.AddPush("__intnl_SystemString_0");
        module.AddExtern("SystemVoid.__op_Print__SystemObject__SystemVoid");

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeLiveness();
        cfg.DeadStoreElimination();
        cfg.Linearize(module);
        var uasm = module.BuildUasmStr();

        var code = uasm.Substring(uasm.IndexOf(".code_start"));
        Assert.Contains("PUSH, leftVal", code);
    }

    [Fact]
    public void NonInternalVar_NotTouched()
    {
        var module = new UasmModule();
        module.DeclareVariable("a", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("b", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("field", "SystemInt32", null, VarFlags.Export);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddCopy("a", "field");
        module.AddCopy("b", "field");

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeLiveness();
        cfg.DeadStoreElimination();
        cfg.Linearize(module);
        var uasm = module.BuildUasmStr();

        var code = uasm.Substring(uasm.IndexOf(".code_start"));
        Assert.Contains("PUSH, a", code);
    }
}

public class CfgCopyPropRegressionTests
{
    [Fact]
    public void CopyProp_DoesNotCrossTypes()
    {
        // Bug 1 regression: Byte src → Int32 dst must NOT be propagated
        var module = new UasmModule();
        module.DeclareVariable("__intnl_SystemByte_0", "SystemByte", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("out", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        // PUSH src(Byte); PUSH dst(Int32); COPY — type mismatch
        module.AddPush("__intnl_SystemByte_0");
        module.AddPush("__intnl_SystemInt32_0");
        module.AddCopyRaw();
        // Read dst
        module.AddCopy("__intnl_SystemInt32_0", "out");

        var cfg = ControlFlowGraph.Build(module);
        cfg.CopyPropagation();
        cfg.Linearize(module);
        var uasm = module.BuildUasmStr();

        var code = uasm.Substring(uasm.IndexOf(".code_start"));
        // dst must NOT be replaced by src — the copy must remain
        Assert.Contains("__intnl_SystemInt32_0", code);
    }
}

public class CfgRemoveEmptyBlocksRegressionTests
{
    [Fact]
    public void RemoveEmptyBlocks_PreservesLabelAddr()
    {
        // Bug 2 regression: LabelAddr in empty block must be preserved in successor
        var module = new UasmModule();
        module.DeclareVariable("__intnl_returnJump_SystemUInt32_0", "SystemUInt32", null, VarFlags.None);
        module.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var retAddr = module.DefineLabel("retAddr");
        var loopHead = module.DefineLabel("loopHead");
        var end = module.DefineLabel("end");

        module.AddExport("_start", start);
        module.MarkLabel(start);
        // Simulate function call: PushLabel retAddr, then jump
        module.AddPushLabel(retAddr);
        module.AddPush("__intnl_returnJump_SystemUInt32_0");
        module.AddCopyRaw();
        module.AddJump(loopHead);
        // Return address label — becomes an empty block (LabelAddr only)
        module.MarkLabel(retAddr);
        // Loop head immediately follows
        module.MarkLabel(loopHead);
        module.AddPush("cond");
        module.AddJumpIfFalse(end);
        module.AddJump(loopHead);
        module.MarkLabel(end);

        var cfg = ControlFlowGraph.Build(module);
        cfg.SimplifyCFG();
        cfg.Linearize(module);

        // Verify LabelAddr for retAddr still exists in the instruction stream
        var insts = module.GetInstructions();
        var labels = module.GetLabels();
        var retAddrLabelIdx = retAddr;
        bool hasLabelAddr = insts.Any(i => i.Kind == InstKind.LabelAddr && i.LabelIndex == retAddrLabelIdx);
        Assert.True(hasLabelAddr, "LabelAddr for return address must survive RemoveEmptyBlocks");

        // Verify UASM builds without error (label addresses are valid)
        var uasm = module.BuildUasmStr();
        Assert.Contains(".code_start", uasm);
    }
}

public class CfgReduceVarsTests
{
    [Fact]
    public void NonOverlapping_Merged()
    {
        // Two __intnl_ vars written by extern then read — non-overlapping → merged
        var module = new UasmModule();
        module.DeclareVariable("arg", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_1", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("out1", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("out2", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        // _0 written by extern, read, then dead
        module.AddPush("arg");
        module.AddPush("__intnl_SystemInt32_0");
        module.AddExtern("SystemConvert.__ToInt32__SystemObject__SystemInt32");
        module.AddCopy("__intnl_SystemInt32_0", "out1");
        // _1 written by extern, read — non-overlapping with _0
        module.AddPush("arg");
        module.AddPush("__intnl_SystemInt32_1");
        module.AddExtern("SystemConvert.__ToInt32__SystemObject__SystemInt32");
        module.AddCopy("__intnl_SystemInt32_1", "out2");

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeLiveness();
        var renames = cfg.ReduceVariables();

        Assert.True(renames.Count > 0);
    }

    [Fact]
    public void Overlapping_NotMerged()
    {
        // _0 written, then _1 written while _0 still live, then both read → interfere
        var module = new UasmModule();
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_1", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("val1", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("val2", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("r", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddCopy("val1", "__intnl_SystemInt32_0");
        module.AddCopy("val2", "__intnl_SystemInt32_1");
        module.AddPush("__intnl_SystemInt32_0");
        module.AddPush("__intnl_SystemInt32_1");
        module.AddPush("r");
        module.AddExtern("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeLiveness();
        var renames = cfg.ReduceVariables();

        // Both live at the same time → should NOT merge
        Assert.DoesNotContain(renames, r => r.Key == "__intnl_SystemInt32_1");
    }

    [Fact]
    public void DifferentTypes_NotMerged()
    {
        // Different UdonTypes → never merged even if non-overlapping
        var module = new UasmModule();
        module.DeclareVariable("__intnl_SystemBoolean_0", "SystemBoolean", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("out1", "SystemBoolean", null, VarFlags.None);
        module.DeclareVariable("out2", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddPush("__intnl_SystemBoolean_0");
        module.AddPush("out1");
        module.AddCopyRaw();
        module.AddPush("__intnl_SystemInt32_0");
        module.AddPush("out2");
        module.AddCopyRaw();

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeLiveness();
        var renames = cfg.ReduceVariables();

        Assert.Empty(renames);
    }
}

public class CfgLinearizeFallThroughTests
{
    [Fact]
    public void JumpIfFalse_TrueBranch_NotNextBlock_InsertsJump()
    {
        // Regression: RPO can place the false-branch block immediately after
        // a JUMP_IF_FALSE block, breaking the true-branch fall-through.
        // Linearize must insert an explicit JUMP to the true branch.
        //
        // CFG: Entry(JIF→C) → B(true), C(false); B→D, C→D
        // RPO might order: Entry, B, C, D  (correct) or Entry, C, B, D  (broken)
        // To force the broken ordering, we make the DFS visit C before B
        // by arranging successors so C is first (false target is first in CFG builder).
        var module = new UasmModule();
        module.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("y", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("r", "SystemInt32", null, VarFlags.None);

        var entry = module.DefineLabel("_start");
        var trueL = module.DefineLabel("trueL");
        var falseL = module.DefineLabel("falseL");
        var merge = module.DefineLabel("merge");

        // Entry block: PUSH cond; JUMP_IF_FALSE → falseL
        module.AddExport("_start", entry);
        module.MarkLabel(entry);
        module.AddPush("cond");
        module.AddJumpIfFalse(falseL);

        // True block: COPY x → r; JUMP merge
        module.MarkLabel(trueL);
        module.AddCopy("x", "r");
        module.AddJump(merge);

        // False block: COPY y → r; JUMP merge
        module.MarkLabel(falseL);
        module.AddCopy("y", "r");
        module.AddJump(merge);

        // Merge block
        module.MarkLabel(merge);
        module.AddPush("r");
        module.AddExtern("SomeExtern__SystemVoid");

        module.Optimize();
        var uasm = module.BuildUasmStr();

        // The code must be semantically correct: both paths should reach merge.
        // Verify that the UASM builds without error and contains expected structure.
        Assert.Contains(".code_start", uasm);
        Assert.Contains("JUMP_IF_FALSE", uasm);

        // Parse the generated instructions to verify no broken fall-throughs:
        // Every JUMP_IF_FALSE must be followed (after optional zero-size insts)
        // by either JUMP or the true-branch code, never the false-branch directly
        // placed right after without an intervening JUMP.
        var insts = module.GetInstructions();
        var labels = module.GetLabels();
        for (int i = 0; i < insts.Count; i++)
        {
            if (insts[i].Kind != InstKind.JumpIfFalse) continue;
            var falseLabelIdx = insts[i].LabelIndex;
            // Find next real instruction after JUMP_IF_FALSE
            int next = i + 1;
            while (next < insts.Count && InstInfo.IsZeroSize(insts[next].Kind))
            {
                // If a zero-size instruction happens to be the false target label,
                // that means the true branch has no code before falling into the false
                // branch — this is only valid if both branches go to the same block.
                next++;
            }
            // If next instruction is the false-branch label, there must be a JUMP before it
            // (unless the true and false branches are the same block)
            if (next < insts.Count)
            {
                // Check: the next real instruction should NOT be inside the false block
                // unless there's an explicit JUMP to handle the true path
                bool nextIsFalseBlock = false;
                for (int j = i + 1; j <= next && j < insts.Count; j++)
                {
                    if (insts[j].Kind is InstKind.Label or InstKind.Export or InstKind.LabelAddr
                        && insts[j].LabelIndex == falseLabelIdx)
                    {
                        nextIsFalseBlock = true;
                        break;
                    }
                }
                if (nextIsFalseBlock)
                {
                    // There must be a JUMP between the JUMP_IF_FALSE and the false label
                    bool hasJump = false;
                    for (int j = i + 1; j <= next && j < insts.Count; j++)
                    {
                        if (insts[j].Kind == InstKind.Jump) { hasJump = true; break; }
                    }
                    Assert.True(hasJump,
                        $"JUMP_IF_FALSE at index {i} falls through directly to false branch — " +
                        "missing explicit JUMP for true branch");
                }
            }
        }
    }

    [Fact]
    public void NonBranch_FallThrough_NotNextBlock_InsertsJump()
    {
        // Regression: a non-branch block whose successor is not the next block
        // in RPO order must get an explicit JUMP.
        //
        // Create: A(non-branch)→B, C→B where RPO might place C between A and B.
        var module = new UasmModule();
        module.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("y", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("r", "SystemInt32", null, VarFlags.None);

        var entry = module.DefineLabel("_start");
        var blockA = module.DefineLabel("blockA");
        var blockB = module.DefineLabel("blockB");
        var blockC = module.DefineLabel("blockC");
        var blockD = module.DefineLabel("blockD");

        module.AddExport("_start", entry);
        module.MarkLabel(entry);
        module.AddPush("cond");
        module.AddJumpIfFalse(blockC);

        // Block A: non-branch, falls through to B
        module.MarkLabel(blockA);
        module.AddCopy("x", "r");
        module.AddJump(blockD);

        // Block C: the false branch
        module.MarkLabel(blockC);
        module.AddCopy("y", "r");
        module.AddJump(blockD);

        // Block D: merge
        module.MarkLabel(blockD);
        module.AddPush("r");
        module.AddExtern("SomeExtern__SystemVoid");

        module.Optimize();
        var uasm = module.BuildUasmStr();

        // Should build without error
        Assert.Contains(".code_start", uasm);

        // Verify the UASM is semantically valid by checking all jump addresses
        // point to valid instruction boundaries
        var insts = module.GetInstructions();
        var labels = module.GetLabels();
        uint addr = 0;
        var validAddresses = new System.Collections.Generic.HashSet<uint>();
        for (int i = 0; i < insts.Count; i++)
        {
            validAddresses.Add(addr);
            addr += InstInfo.Size[insts[i].Kind];
        }
        foreach (var inst in insts)
        {
            if (inst.Kind is InstKind.Jump or InstKind.JumpIfFalse)
            {
                var targetAddr = labels[inst.LabelIndex].Address;
                Assert.Contains(targetAddr, validAddresses);
            }
        }
    }

    [Fact]
    public void FullOptimize_DiamondPattern_ProducesValidUasm()
    {
        // End-to-end test: diamond pattern (if-else) with function calls,
        // ensuring Optimize() produces UASM that doesn't have broken fall-throughs.
        var module = new UasmModule();
        module.DeclareVariable("__intnl_returnJump_SystemUInt32_0", "SystemUInt32", null, VarFlags.None);
        module.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("result", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("arg", "SystemInt32", null, VarFlags.None);

        var start = module.DefineLabel("_start");
        var funcLabel = module.DefineLabel("func");
        var retAddr1 = module.DefineLabel("ret1");
        var retAddr2 = module.DefineLabel("ret2");
        var trueL = module.DefineLabel("trueL");
        var falseL = module.DefineLabel("falseL");
        var merge = module.DefineLabel("merge");

        // Helper function: reads arg, writes result, returns
        module.MarkLabel(funcLabel);
        module.AddPush("arg");
        module.AddPush("__intnl_SystemInt32_0");
        module.AddExtern("SystemConvert.__ToInt32__SystemObject__SystemInt32");
        module.AddCopy("__intnl_SystemInt32_0", "result");
        module.AddReturn("__intnl_returnJump_SystemUInt32_0");

        // Main: if (cond) call func; else call func; use result
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddPush("cond");
        module.AddJumpIfFalse(falseL);

        module.MarkLabel(trueL);
        module.AddPushLabel(retAddr1);
        module.AddJump(funcLabel);
        module.MarkLabel(retAddr1);
        module.AddJump(merge);

        module.MarkLabel(falseL);
        module.AddPushLabel(retAddr2);
        module.AddJump(funcLabel);
        module.MarkLabel(retAddr2);
        module.AddJump(merge);

        module.MarkLabel(merge);
        module.AddPush("result");
        module.AddExtern("SomeExtern__SystemVoid");

        module.Optimize();
        var uasm = module.BuildUasmStr();

        Assert.Contains(".code_start", uasm);
        Assert.Contains("JUMP_IF_FALSE", uasm);

        // Verify all jump targets are valid instruction boundaries
        var insts = module.GetInstructions();
        var labels = module.GetLabels();
        uint a = 0;
        var valid = new System.Collections.Generic.HashSet<uint>();
        for (int i = 0; i < insts.Count; i++)
        {
            valid.Add(a);
            a += InstInfo.Size[insts[i].Kind];
        }
        foreach (var inst in insts)
        {
            if (inst.Kind is InstKind.Jump or InstKind.JumpIfFalse)
                Assert.Contains(labels[inst.LabelIndex].Address, valid);
        }
    }
}

public class ConstantFoldingTests
{
    [Fact]
    public void IntAddition_Folded()
    {
        var module = new UasmModule();
        module.DeclareVariable("__const_SystemInt32_0", "SystemInt32", null, VarFlags.None, constValue: 3);
        module.DeclareVariable("__const_SystemInt32_1", "SystemInt32", null, VarFlags.None, constValue: 4);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("result", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddPush("__const_SystemInt32_0");
        module.AddPush("__const_SystemInt32_1");
        module.AddPush("__intnl_SystemInt32_0");
        module.AddExtern("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        module.AddCopy("__intnl_SystemInt32_0", "result");

        var cfg = ControlFlowGraph.Build(module);
        var folded = cfg.ConstantFolding(module);
        cfg.Linearize(module);
        var uasm = module.BuildUasmStr();

        Assert.True(folded);
        var code = uasm.Substring(uasm.IndexOf(".code_start"));
        Assert.DoesNotContain("EXTERN", code);
        // Result should be 7 — check that a const var is used in the COPY
        Assert.Contains("COPY", code);
    }

    [Fact]
    public void NonConstArg_NotFolded()
    {
        var module = new UasmModule();
        module.DeclareVariable("__const_SystemInt32_0", "SystemInt32", null, VarFlags.None, constValue: 3);
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddPush("__const_SystemInt32_0");
        module.AddPush("x");
        module.AddPush("__intnl_SystemInt32_0");
        module.AddExtern("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");

        var cfg = ControlFlowGraph.Build(module);
        var folded = cfg.ConstantFolding(module);

        Assert.False(folded);
    }

    [Fact]
    public void IntZeroDivision_NotFolded()
    {
        var module = new UasmModule();
        module.DeclareVariable("__const_SystemInt32_0", "SystemInt32", null, VarFlags.None, constValue: 10);
        module.DeclareVariable("__const_SystemInt32_1", "SystemInt32", null, VarFlags.None, constValue: 0);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddPush("__const_SystemInt32_0");
        module.AddPush("__const_SystemInt32_1");
        module.AddPush("__intnl_SystemInt32_0");
        module.AddExtern("SystemInt32.__op_Division__SystemInt32_SystemInt32__SystemInt32");

        var cfg = ControlFlowGraph.Build(module);
        var folded = cfg.ConstantFolding(module);

        Assert.False(folded);
    }

    [Fact]
    public void ChainFolding_MultipleExterns()
    {
        // 3 + 4 = 7, then 7 * 2 = 14
        var module = new UasmModule();
        module.DeclareVariable("__const_SystemInt32_0", "SystemInt32", null, VarFlags.None, constValue: 3);
        module.DeclareVariable("__const_SystemInt32_1", "SystemInt32", null, VarFlags.None, constValue: 4);
        module.DeclareVariable("__const_SystemInt32_2", "SystemInt32", null, VarFlags.None, constValue: 2);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_1", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("result", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        // 3 + 4 → __intnl_0
        module.AddPush("__const_SystemInt32_0");
        module.AddPush("__const_SystemInt32_1");
        module.AddPush("__intnl_SystemInt32_0");
        module.AddExtern("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        // __intnl_0 * 2 → __intnl_1  (after folding first, __intnl_0 becomes const 7)
        // But constant folding replaces first to: PUSH const7; PUSH __intnl_0; COPY
        // Second EXTERN sees __intnl_0 (not const) so won't fold in single pass.
        // After CopyProp eliminates __intnl_0, a second ConstantFolding pass would fold.
        // For this test, verify the first fold happened.
        module.AddPush("__intnl_SystemInt32_0");
        module.AddPush("__const_SystemInt32_2");
        module.AddPush("__intnl_SystemInt32_1");
        module.AddExtern("SystemInt32.__op_Multiplication__SystemInt32_SystemInt32__SystemInt32");
        module.AddCopy("__intnl_SystemInt32_1", "result");

        var cfg = ControlFlowGraph.Build(module);
        var folded = cfg.ConstantFolding(module);

        Assert.True(folded);
        // First EXTERN should be folded, second might remain
        cfg.Linearize(module);
        var uasm = module.BuildUasmStr();
        var code = uasm.Substring(uasm.IndexOf(".code_start"));
        // At minimum, the addition EXTERN is gone
        Assert.DoesNotContain("__op_Addition__", code);
    }

    [Fact]
    public void FloatDivisionByZero_Folded()
    {
        // float / 0f → Inf (safe, should fold)
        var module = new UasmModule();
        module.DeclareVariable("__const_SystemSingle_0", "SystemSingle", null, VarFlags.None, constValue: 1.0f);
        module.DeclareVariable("__const_SystemSingle_1", "SystemSingle", null, VarFlags.None, constValue: 0.0f);
        module.DeclareVariable("__intnl_SystemSingle_0", "SystemSingle", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddPush("__const_SystemSingle_0");
        module.AddPush("__const_SystemSingle_1");
        module.AddPush("__intnl_SystemSingle_0");
        module.AddExtern("SystemSingle.__op_Division__SystemSingle_SystemSingle__SystemSingle");

        var cfg = ControlFlowGraph.Build(module);
        var folded = cfg.ConstantFolding(module);

        Assert.True(folded);
    }

    [Fact]
    public void BoolLogical_Folded()
    {
        var module = new UasmModule();
        module.DeclareVariable("__const_SystemBoolean_0", "SystemBoolean", null, VarFlags.None, constValue: true);
        module.DeclareVariable("__const_SystemBoolean_1", "SystemBoolean", null, VarFlags.None, constValue: false);
        module.DeclareVariable("__intnl_SystemBoolean_0", "SystemBoolean", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddPush("__const_SystemBoolean_0");
        module.AddPush("__const_SystemBoolean_1");
        module.AddPush("__intnl_SystemBoolean_0");
        module.AddExtern("SystemBoolean.__op_ConditionalAnd__SystemBoolean_SystemBoolean__SystemBoolean");

        var cfg = ControlFlowGraph.Build(module);
        var folded = cfg.ConstantFolding(module);

        Assert.True(folded);
    }
}

public class DominatorTreeTests
{
    [Fact]
    public void LinearCFG_Dominance()
    {
        var module = new UasmModule();
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var L1 = module.DefineLabel("L1");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddPush("x");
        module.AddPop();
        module.AddJump(L1);
        module.MarkLabel(L1);
        module.AddPush("x");
        module.AddPop();

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeDominators();

        // Entry dominates all blocks
        foreach (var b in cfg.Blocks)
            Assert.True(cfg.Dominates(cfg.Entry.Id, b.Id));
    }

    [Fact]
    public void IfElse_Dominance()
    {
        var module = new UasmModule();
        module.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("y", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var elseL = module.DefineLabel("else");
        var end = module.DefineLabel("end");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddPush("cond");
        module.AddJumpIfFalse(elseL);
        module.AddPush("x");
        module.AddPop();
        module.AddJump(end);
        module.MarkLabel(elseL);
        module.AddPush("y");
        module.AddPop();
        module.MarkLabel(end);

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeDominators();

        // Entry dominates all; neither branch dominates the other
        var trueBlock = cfg.Blocks.First(b =>
            b.Instructions.Any(i => i.Kind == InstKind.Push && i.Operand == "x"));
        var falseBlock = cfg.Blocks.First(b =>
            b.Instructions.Any(i => i.Kind == InstKind.Push && i.Operand == "y"));

        Assert.True(cfg.Dominates(cfg.Entry.Id, trueBlock.Id));
        Assert.True(cfg.Dominates(cfg.Entry.Id, falseBlock.Id));
        Assert.False(cfg.Dominates(trueBlock.Id, falseBlock.Id));
        Assert.False(cfg.Dominates(falseBlock.Id, trueBlock.Id));
    }

    [Fact]
    public void Loop_Dominance()
    {
        var module = new UasmModule();
        module.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var loop = module.DefineLabel("loop");
        var end = module.DefineLabel("end");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.MarkLabel(loop);
        module.AddPush("x");
        module.AddPop();
        module.AddPush("cond");
        module.AddJumpIfFalse(end);
        module.AddJump(loop);
        module.MarkLabel(end);

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeDominators();

        // Loop header dominates loop body (same block here) and exit
        var endBlock = cfg.Blocks.Last();
        Assert.True(cfg.Dominates(cfg.Entry.Id, endBlock.Id));
    }

    [Fact]
    public void MultiExport_ExportBlocks_NotDominatedByBlock0()
    {
        // Simulates a multi-function Udon program:
        // Block 0: internal function (get_value) — no Export
        // Block N: _start (Export)
        // Block M: _onEvent (Export)
        // The VM can enter _start or _onEvent directly, so neither
        // should be dominated by Block 0.
        var module = new UasmModule();
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("retaddr", "SystemUInt32", null, VarFlags.None);

        // Internal function at address 0 (becomes Block 0)
        var getVal = module.DefineLabel("get_value");
        module.AddLabel(getVal);
        module.MarkLabel(getVal);
        module.AddPush("x");
        module.AddPop();
        module.AddReturn("retaddr");

        // _start export
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddPush("x");
        module.AddPop();

        // _onEvent export
        var onEvent = module.DefineLabel("_onEvent");
        module.AddExport("_onEvent", onEvent);
        module.MarkLabel(onEvent);
        module.AddPush("x");
        module.AddPop();

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeDominators();

        var startBlock = cfg.Blocks.First(b =>
            b.Instructions.Any(i => i.Kind == InstKind.Export && i.Operand == "_start"));
        var eventBlock = cfg.Blocks.First(b =>
            b.Instructions.Any(i => i.Kind == InstKind.Export && i.Operand == "_onEvent"));

        // Export blocks should NOT be dominated by Block 0 (internal function)
        Assert.False(cfg.Dominates(cfg.Entry.Id, startBlock.Id));
        Assert.False(cfg.Dominates(cfg.Entry.Id, eventBlock.Id));
        // Neither export should dominate the other
        Assert.False(cfg.Dominates(startBlock.Id, eventBlock.Id));
        Assert.False(cfg.Dominates(eventBlock.Id, startBlock.Id));
    }

    [Fact]
    public void MultiExport_GVN_DoesNotLeakAcrossFunctions()
    {
        // Two export functions compute the same pure EXTERN.
        // GVN must NOT eliminate the second as redundant,
        // because the VM can enter each function independently.
        var module = new UasmModule();
        module.DeclareVariable("__const_SystemInt32_0", "SystemInt32", null, VarFlags.None, constValue: 3);
        module.DeclareVariable("__const_SystemInt32_1", "SystemInt32", null, VarFlags.None, constValue: 4);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_1", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("retaddr", "SystemUInt32", null, VarFlags.None);

        // Internal function (Block 0, no export)
        var helper = module.DefineLabel("helper");
        module.AddLabel(helper);
        module.MarkLabel(helper);
        module.AddPush("__const_SystemInt32_0");
        module.AddPush("__const_SystemInt32_1");
        module.AddPush("__intnl_SystemInt32_0");
        module.AddExtern("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        module.AddReturn("retaddr");

        // _start: same computation
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddPush("__const_SystemInt32_0");
        module.AddPush("__const_SystemInt32_1");
        module.AddPush("__intnl_SystemInt32_1");
        module.AddExtern("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeDominators();
        var changed = cfg.GlobalValueNumbering();

        // GVN must NOT replace the second EXTERN — it's in an independent function
        Assert.False(changed);
        var startBlock = cfg.Blocks.First(b =>
            b.Instructions.Any(i => i.Kind == InstKind.Export && i.Operand == "_start"));
        Assert.Contains(startBlock.Instructions,
            i => i.Kind == InstKind.Extern);
    }
}

public class GvnTests
{
    [Fact]
    public void DuplicateExtern_SecondRemoved()
    {
        // Same EXTERN with same inputs → second replaced by COPY
        var module = new UasmModule();
        module.DeclareVariable("__const_SystemInt32_0", "SystemInt32", null, VarFlags.None, constValue: 3);
        module.DeclareVariable("__const_SystemInt32_1", "SystemInt32", null, VarFlags.None, constValue: 4);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_1", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("out1", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("out2", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        // First: 3 + 4 → __intnl_0
        module.AddPush("__const_SystemInt32_0");
        module.AddPush("__const_SystemInt32_1");
        module.AddPush("__intnl_SystemInt32_0");
        module.AddExtern("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        module.AddCopy("__intnl_SystemInt32_0", "out1");
        // Second: same 3 + 4 → __intnl_1
        module.AddPush("__const_SystemInt32_0");
        module.AddPush("__const_SystemInt32_1");
        module.AddPush("__intnl_SystemInt32_1");
        module.AddExtern("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        module.AddCopy("__intnl_SystemInt32_1", "out2");

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeDominators();
        var changed = cfg.GlobalValueNumbering();
        cfg.Linearize(module);
        var uasm = module.BuildUasmStr();

        Assert.True(changed);
        var code = uasm.Substring(uasm.IndexOf(".code_start"));
        // Should have only one EXTERN
        var externCount = code.Split("EXTERN").Length - 1;
        Assert.Equal(1, externCount);
    }

    [Fact]
    public void InputRedefined_NotRemoved()
    {
        // Input var is written between two identical EXTERNs → not safe to remove second
        var module = new UasmModule();
        module.DeclareVariable("a", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("b", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_1", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        // First: a + b → __intnl_0
        module.AddPush("a");
        module.AddPush("b");
        module.AddPush("__intnl_SystemInt32_0");
        module.AddExtern("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        // a is redefined → not safe for GVN
        module.AddCopy("__intnl_SystemInt32_0", "a");
        // Second: a + b → __intnl_1 (a is different now)
        module.AddPush("a");
        module.AddPush("b");
        module.AddPush("__intnl_SystemInt32_1");
        module.AddExtern("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeDominators();
        var changed = cfg.GlobalValueNumbering();

        // "a" has multiple writes → not safe, GVN should not remove second EXTERN
        Assert.False(changed);
    }

    [Fact]
    public void AcrossDominatedBlocks_Removed()
    {
        // EXTERN in dominator block, same EXTERN in dominated block → second removed
        var module = new UasmModule();
        module.DeclareVariable("__const_SystemInt32_0", "SystemInt32", null, VarFlags.None, constValue: 3);
        module.DeclareVariable("__const_SystemInt32_1", "SystemInt32", null, VarFlags.None, constValue: 4);
        module.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_1", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("out", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var elseL = module.DefineLabel("else");
        var end = module.DefineLabel("end");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        // First EXTERN in entry
        module.AddPush("__const_SystemInt32_0");
        module.AddPush("__const_SystemInt32_1");
        module.AddPush("__intnl_SystemInt32_0");
        module.AddExtern("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        module.AddPush("cond");
        module.AddJumpIfFalse(elseL);
        // True branch: same EXTERN
        module.AddPush("__const_SystemInt32_0");
        module.AddPush("__const_SystemInt32_1");
        module.AddPush("__intnl_SystemInt32_1");
        module.AddExtern("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        module.AddCopy("__intnl_SystemInt32_1", "out");
        module.AddJump(end);
        module.MarkLabel(elseL);
        module.MarkLabel(end);

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeDominators();
        var changed = cfg.GlobalValueNumbering();

        Assert.True(changed);
    }
}

public class LoopDetectionTests
{
    [Fact]
    public void SimpleLoop_Detected()
    {
        var module = new UasmModule();
        module.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var loop = module.DefineLabel("loop");
        var end = module.DefineLabel("end");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.MarkLabel(loop);
        module.AddPush("cond");
        module.AddJumpIfFalse(end);
        module.AddJump(loop);
        module.MarkLabel(end);

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeDominators();
        var loops = cfg.DetectLoops();

        Assert.Single(loops);
        Assert.True(loops[0].Body.Count >= 1);
    }

    [Fact]
    public void NoLoop_NoneDetected()
    {
        var module = new UasmModule();
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddPush("x");
        module.AddPop();

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeDominators();
        var loops = cfg.DetectLoops();

        Assert.Empty(loops);
    }

    [Fact]
    public void EnsurePreheader_CreatesBlock()
    {
        var module = new UasmModule();
        module.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var loop = module.DefineLabel("loop");
        var end = module.DefineLabel("end");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddPush("x");
        module.AddPop();
        module.AddJump(loop);
        module.MarkLabel(loop);
        module.AddPush("cond");
        module.AddJumpIfFalse(end);
        module.AddJump(loop);
        module.MarkLabel(end);

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeDominators();
        var loops = cfg.DetectLoops();
        Assert.Single(loops);

        var labels = module.GetLabels();
        var blocksBefore = cfg.Blocks.Count;
        var preheader = cfg.EnsurePreheader(loops[0], labels);

        Assert.NotNull(preheader);
        Assert.Contains(preheader, cfg.Blocks);
        Assert.Contains(loops[0].Header, preheader.Successors);
    }
}

public class LicmTests
{
    [Fact]
    public void InvariantExtern_Hoisted()
    {
        // EXTERN with loop-invariant inputs should be moved to preheader
        var module = new UasmModule();
        module.DeclareVariable("__const_SystemInt32_0", "SystemInt32", null, VarFlags.None, constValue: 3);
        module.DeclareVariable("__const_SystemInt32_1", "SystemInt32", null, VarFlags.None, constValue: 4);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var loop = module.DefineLabel("loop");
        var end = module.DefineLabel("end");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddJump(loop);
        module.MarkLabel(loop);
        // Invariant: 3 + 4 → __intnl_0 (inputs not written in loop)
        module.AddPush("__const_SystemInt32_0");
        module.AddPush("__const_SystemInt32_1");
        module.AddPush("__intnl_SystemInt32_0");
        module.AddExtern("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        module.AddPush("cond");
        module.AddJumpIfFalse(end);
        module.AddJump(loop);
        module.MarkLabel(end);

        var cfg = ControlFlowGraph.Build(module);
        var hoisted = cfg.LoopInvariantCodeMotion(module);

        Assert.True(hoisted);

        // Verify: the loop body should no longer contain the EXTERN
        var loops = cfg.DetectLoops();
        foreach (var l in loops)
        {
            foreach (var bid in l.Body)
            {
                var block = cfg.Blocks.Find(b => b.Id == bid);
                if (block == null) continue;
                foreach (var inst in block.Instructions)
                    Assert.NotEqual(InstKind.Extern, inst.Kind);
            }
        }
    }

    [Fact]
    public void ModifiedInput_NotHoisted()
    {
        // Input written in loop → EXTERN stays
        var module = new UasmModule();
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("y", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var loop = module.DefineLabel("loop");
        var end = module.DefineLabel("end");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddJump(loop);
        module.MarkLabel(loop);
        // x is written in loop
        module.AddCopy("y", "x");
        // EXTERN uses x → not invariant
        module.AddPush("x");
        module.AddPush("y");
        module.AddPush("__intnl_SystemInt32_0");
        module.AddExtern("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        module.AddPush("cond");
        module.AddJumpIfFalse(end);
        module.AddJump(loop);
        module.MarkLabel(end);

        var cfg = ControlFlowGraph.Build(module);
        var hoisted = cfg.LoopInvariantCodeMotion(module);

        Assert.False(hoisted);
    }

    [Fact]
    public void ImpureExtern_NotHoisted()
    {
        // Non-pure EXTERN stays in loop
        var module = new UasmModule();
        module.DeclareVariable("x", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("cond", "SystemBoolean", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var loop = module.DefineLabel("loop");
        var end = module.DefineLabel("end");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddJump(loop);
        module.MarkLabel(loop);
        module.AddPush("x");
        module.AddExtern("SomeImpureExtern__SystemVoid");
        module.AddPush("cond");
        module.AddJumpIfFalse(end);
        module.AddJump(loop);
        module.MarkLabel(end);

        var cfg = ControlFlowGraph.Build(module);
        var hoisted = cfg.LoopInvariantCodeMotion(module);

        Assert.False(hoisted);
    }

    [Fact]
    public void FunctionCallPattern_NoFalseLoop()
    {
        // Function call (PushLabel + Jump) should NOT be detected as a loop back edge
        var module = new UasmModule();
        module.DeclareVariable("_x", "SystemInt32", null, VarFlags.Export);
        module.DeclareVariable("__const_SystemInt32_0", "SystemInt32", null, VarFlags.None, constValue: 2);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_returnJump_SystemUInt32_0", "SystemUInt32", "0xFFFFFFFF", VarFlags.None);
        module.DeclareVariable("__retaddr_0", "SystemUInt32", null, VarFlags.None);

        var startLbl = module.DefineLabel("_start");
        var doubleLbl = module.DefineLabel("__Double");
        var retLbl = module.DefineLabel("__retpoint_0");

        // _start: push return address, jump to __Double
        module.AddExport("_start", startLbl);
        module.MarkLabel(startLbl);
        module.AddPushLabel(retLbl);
        module.AddPush("__retaddr_0");
        module.AddCopyRaw();
        module.AddJump(doubleLbl);

        // return point
        module.MarkLabel(retLbl);
        module.AddPush("__intnl_SystemInt32_0");
        module.AddPush("_x");
        module.AddCopyRaw();
        module.AddReturn("__intnl_returnJump_SystemUInt32_0");

        // __Double: _x * 2
        module.AddExport("__Double", doubleLbl);
        module.MarkLabel(doubleLbl);
        module.AddPush("_x");
        module.AddPush("__const_SystemInt32_0");
        module.AddPush("__intnl_SystemInt32_0");
        module.AddExtern("SystemInt32.__op_Multiplication__SystemInt32_SystemInt32__SystemInt32");
        module.AddReturn("__retaddr_0");

        var cfg = ControlFlowGraph.Build(module);
        cfg.SimplifyCFG();
        cfg.ComputeDominators();

        var loops = cfg.DetectLoops();
        Assert.Empty(loops);

        var changed = cfg.LoopInvariantCodeMotion(module);
        Assert.False(changed);
    }
}

public class CrossBlockExternTests
{
    [Fact]
    public void GetWrittenVar_CrossBlockExtern_Detected()
    {
        // Block A: PUSH output_var  (last instruction, no EXTERN in same block)
        // Block B: EXTERN (non-void, single successor of A)
        // → IsExternOutput should detect the cross-block write
        var module = new UasmModule();
        module.DeclareVariable("input", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("output", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var next = module.DefineLabel("next");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        module.AddPush("input");
        module.AddPush("output");
        // Force block split by adding a jump
        module.AddJump(next);
        module.MarkLabel(next);
        module.AddExtern("SystemInt32.__op_UnaryMinus__SystemInt32__SystemInt32");

        var cfg = ControlFlowGraph.Build(module);
        // After building CFG, the PUSH output should be in block A,
        // EXTERN in block B (single successor). Liveness should mark output as killed.
        cfg.ComputeLiveness();

        // output should be in Kill set of the block containing PUSH output
        // (because IsExternOutput detects cross-block extern)
        bool foundKill = false;
        foreach (var block in cfg.Blocks)
        {
            for (int i = 0; i < block.Instructions.Count; i++)
            {
                var inst = block.Instructions[i];
                if (inst.Kind == InstKind.Push && inst.Operand == "output"
                    && cfg.VarToIndex.TryGetValue("output", out var idx))
                {
                    if (block.Kill.Get(idx))
                        foundKill = true;
                }
            }
        }
        Assert.True(foundKill, "output should be in Kill set via cross-block extern detection");
    }

    [Fact]
    public void CopyProp_CrossBlockExternOutput_NotPropagated()
    {
        // Ensure CopyPropagation does not propagate through a cross-block extern output.
        // COPY a → tmp; PUSH tmp (extern output cross-block) → tmp should NOT be propagated away.
        var module = new UasmModule();
        module.DeclareVariable("a", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("__intnl_SystemInt32_0", "SystemInt32", null, VarFlags.None);
        module.DeclareVariable("result", "SystemInt32", null, VarFlags.None);
        var start = module.DefineLabel("_start");
        var next = module.DefineLabel("next");
        module.AddExport("_start", start);
        module.MarkLabel(start);
        // PUSH a; PUSH __intnl; COPY (a → __intnl)
        module.AddCopy("a", "__intnl_SystemInt32_0");
        // PUSH __intnl as extern input, PUSH result as extern output
        module.AddPush("__intnl_SystemInt32_0");
        module.AddPush("result");
        module.AddJump(next);
        module.MarkLabel(next);
        module.AddExtern("SystemInt32.__op_UnaryMinus__SystemInt32__SystemInt32");

        var cfg = ControlFlowGraph.Build(module);
        cfg.ComputeLiveness();
        cfg.CopyPropagation();
        cfg.Linearize(module);
        var uasm = module.BuildUasmStr();

        // result is an extern output across block boundary — it should be detected as written.
        // The copy a → __intnl may or may not be propagated (since __intnl is used as input),
        // but result must still be present as extern output.
        var code = uasm.Substring(uasm.IndexOf(".code_start"));
        Assert.Contains("result", code);
    }
}
