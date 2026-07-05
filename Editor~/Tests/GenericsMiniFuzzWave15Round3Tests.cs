using System;
using Xunit;

namespace USugar.Tests;

// Generics mini-fuzz wave-15 round-3 batch — tracked pins. See docs/roadmap.md (production gate + B61-B65).
public class GenericsMiniFuzzWave15Round3Tests
{
    // ── Item 0: production extern-validation gate — a bogus extern is a named diagnostic, not opaque ──

    [Fact]
    public void ProductionExternGate_RejectsBogusExtern()
    {
        ExternResolver.IsExternValid = ExternRegistry.IsValid;
        var bogus = ".code_start\n        EXTERN, \"SystemEnum.__Equals__SystemObject__SystemBoolean\"\n.code_end";
        var ex = Assert.Throws<NotSupportedException>(() => ExternResolver.AssertEmittedExternsValid(bogus));
        Assert.Contains("SystemEnum.__Equals", ex.Message);
    }

    [Fact]
    public void ProductionExternGate_AcceptsValidExtern()
    {
        ExternResolver.IsExternValid = ExternRegistry.IsValid;
        var valid = ".code_start\n        EXTERN, \"SystemInt32.__Equals__SystemObject__SystemBoolean\"\n.code_end";
        ExternResolver.AssertEmittedExternsValid(valid); // must not throw
    }
}
