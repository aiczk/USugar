using System.Linq;
using Xunit;

namespace USugar.Tests;

/// <summary>Arms the single-producer merge of the EscapeTarget mapping. A precise-provenance dispatch
/// site uses ONLY its own callee set and skips the blanket signature match, so a target the site's
/// derivation omits is a missing Reentrant mark and an unspilled frame across a reentrant dispatch.
/// The interface fan-out arm (a method group taken off an interface implemented only by v1 user
/// classes) is the one the emitter's copy used to lack.</summary>
public class EscapeTargetProvenanceTests
{
    const string InterfaceMethodGroupInCycle = @"
using UdonSharp;
using System;
public interface IStep { void Run(Recur owner); }
public class StepA : IStep { public void Run(Recur owner) { owner.Go(); } }
public class StepB : IStep { public void Run(Recur owner) { } }
public class Recur : UdonSharpBehaviour {
    public int n;
    [RecursiveMethod]
    public void Go() {
        IStep s = n > 0 ? (IStep)new StepA() : (IStep)new StepB();
        Action<Recur> a = s.Run;
        a(this);
        if (n > 0) { n--; Go(); }
    }
}";

    [Fact]
    public void InterfaceMethodGroupTargetsReachTheCycleAndMarkTheSiteReentrant()
    {
        TestHelper.CompileToUasm(InterfaceMethodGroupInCycle, "Recur", out var emitter);
        var info = emitter.DebugRecursionInfo;

        Assert.NotNull(info?.RecursionGraphNodes);
        // StepA.Run reaches Go, so the dispatch through the interface method group re-enters the SCC.
        // Deriving the site's callees without the interface fan-out leaves this set empty.
        Assert.NotEmpty(info.ReentrantDispatchSites);
        Assert.Contains(info.ReentrantDispatchSites, site => site.ToString().Contains("a(this)"));
    }
}
