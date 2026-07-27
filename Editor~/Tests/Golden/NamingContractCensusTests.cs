using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Pins the naming-contract single-sourcing (hand-enumeration audit 2026-07-17 item ④): the
/// __param/__ret/__body slot-id/label shapes are produced ONLY by NameAllocator's formatters
/// (ParamKey/RetKey/ParamId/RetId/BodyLabel), and the __dlg_ bridge prefix ONLY by
/// DelegateAbi.BridgeName/BridgeTargetKey/BridgePrefix (DelegateProtocol.cs). A hand-respelled
/// copy at a synthetic-bridge site is one byte away from silently unbinding a param/ret slot or
/// body label — exactly the drift class this census forbids.
/// </summary>
public class NamingContractCensusTests
{
    // A string literal ENDING with a contract suffix (covers $"...__param", + "__ret", etc.) or
    // STARTING with the delegate bridge prefix. Comment mentions never carry the adjacent quote.
    // Third shape (2026-07-28): an interpolated identifier starting with "__{" is a hand-rolled
    // NameAllocator.FormatId / LabelNames spelling — 10 of them had drifted outside the owner.
    static readonly Regex Forbidden = new Regex("__(param|ret|body)\"|\"__dlg_|\\$\"__\\{");

    static readonly string[] OwnerFiles = { "NameAllocator.cs", "DelegateProtocol.cs" };

    [Fact]
    public void Editor_HasNoRespelledNamingContractProducers()
    {
        var editorDir = TestPaths.EditorDir;
        Assert.True(Directory.Exists(editorDir), $"Editor dir not found at {editorDir}");

        var hits = Directory.EnumerateFiles(editorDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !OwnerFiles.Contains(Path.GetFileName(f)))
            .SelectMany(f => File.ReadAllLines(f)
                .Select((line, i) => (file: Path.GetFileName(f), no: i + 1, line)))
            .Where(t => Forbidden.IsMatch(t.line))
            .Select(t => $"{t.file}:{t.no}: {t.line.Trim()}")
            .ToList();

        Assert.True(hits.Count == 0,
            "Naming-contract shape re-spelled outside its owner (route through NameAllocator.ParamKey/"
            + "RetKey/ParamId/RetId/BodyLabel or DelegateAbi.BridgeName/BridgeTargetKey/BridgePrefix):\n"
            + string.Join("\n", hits));
    }

    [Fact]
    public void OwnerFormatters_ProduceTheContractShapes()
    {
        Assert.Equal("x__param", NameAllocator.ParamKey("x"));
        Assert.Equal("M__ret", NameAllocator.RetKey("M"));
        Assert.Equal("__3_x__param", NameAllocator.ParamId("x", 3));
        Assert.Equal("__0_M__ret", NameAllocator.RetId("M", 0));
        Assert.Equal("Run__body", NameAllocator.BodyLabel("Run"));
        Assert.Equal("__dlg_Run", DelegateAbi.BridgeName("Run"));
        Assert.Equal("Run", DelegateAbi.BridgeTargetKey("__dlg_Run"));
        Assert.Equal("Plain", DelegateAbi.BridgeTargetKey("Plain"));
    }
}
