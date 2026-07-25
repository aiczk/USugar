using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Hand-enumeration audit item ⑤ (2026-07-17): LayoutPlanBuilder's two hand-maintained event tables
/// (UdonEventNames, UdonEventParamNames) are pinned BIDIRECTIONALLY against the SDK event node
/// registry census in Fixtures/udon_event_registry.txt. A missing UdonEventNames row silently
/// compiles a new SDK event as an inert ordinary method; a wrong/missing UdonEventParamNames row
/// silently unbinds the event's parameters (the runtime writes registry-derived heap vars —
/// UdonBehaviour.GetEventParameterName). Export and param heap-var names are DERIVED here exactly
/// like stock UdonSharp's CompilerUdonInterface.CacheInit derives them from the same registry.
/// Regenerate the fixture in-editor via USugarCompiler.DumpEventRegistry after an SDK update.
/// </summary>
public class EventRegistryCensusTests
{
    sealed record RegistryEvent(string MethodName, string ExportName, string[] ParamVars);

    static readonly IReadOnlyList<RegistryEvent> Registry = LoadRegistry();

    // Planner rows with NO Event_* node in the SDK registry — kept deliberately: OnVideoPlay and
    // OnVideoPause are declared on UdonSharpBehaviour and fired by the client's video system as
    // _onVideoPlay/_onVideoPause (graph programs subscribe via a literal custom event; the registry
    // has no node for them). Both are param-less, so no UdonEventParamNames row exists or is exempt.
    // If a future SDK adds these nodes to the registry, ExemptRows_DoNotShadowRegistryRows fails and
    // this list must shrink.
    static readonly Dictionary<string, string> RegistryLessEvents = new()
    {
        ["OnVideoPlay"] = "_onVideoPlay",
        ["OnVideoPause"] = "_onVideoPause",
    };

    static IReadOnlyList<RegistryEvent> LoadRegistry()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "udon_event_registry.txt");
        var events = new List<RegistryEvent>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var bar = line.IndexOf('|');
            var fullName = bar < 0 ? line : line[..bar];
            Assert.StartsWith("Event_", fullName, StringComparison.Ordinal);

            // Stock derivation (CompilerUdonInterface.CacheInit): strip "Event_", lower first char
            // → export "_" + lowered; param heap var = loweredEvent + UpperFirst(nodeParamName).
            var methodName = fullName["Event_".Length..];
            var lowered = char.ToLowerInvariant(methodName[0]) + methodName[1..];
            var paramVars = bar < 0
                ? Array.Empty<string>()
                : line[(bar + 1)..].Split(',').Select(p =>
                {
                    var colon = p.IndexOf(':');
                    Assert.True(colon > 0, $"malformed fixture param '{p}' on row {fullName}");
                    var name = p[..colon];
                    return lowered + char.ToUpperInvariant(name[0]) + name[1..];
                }).ToArray();

            events.Add(new RegistryEvent(methodName, "_" + lowered, paramVars));
        }
        return events;
    }

    [Fact]
    public void Fixture_CarriesTheFullEventCensus()
    {
        // 133 Event_* nodes in the SDK 3.10.1 registry minus 9 Event_Custom* minus Event_OnVariableChange.
        Assert.Equal(123, Registry.Count);
        Assert.Equal(Registry.Count, Registry.Select(e => e.MethodName).Distinct().Count());
    }

    // ── Direction 1: registry → tables (no missing events, no missing/wrong params) ──

    [Fact]
    public void EveryRegistryEvent_HasAnEventNameRow_WithTheDerivedExport()
    {
        var bad = Registry
            .Where(ev => !LayoutPlanBuilder.UdonEventNames.TryGetValue(ev.MethodName, out var export)
                         || export != ev.ExportName)
            .Select(ev => $"{ev.MethodName} (expected {ev.ExportName}, "
                          + $"got {(LayoutPlanBuilder.UdonEventNames.TryGetValue(ev.MethodName, out var g) ? g : "<missing — event compiles as an inert ordinary method>")})")
            .ToList();
        Assert.True(bad.Count == 0, "UdonEventNames drift vs SDK registry:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void EveryRegistryEvent_WithParams_HasTheExactParamRow_NamesAndOrder()
    {
        var bad = new List<string>();
        foreach (var ev in Registry.Where(e => e.ParamVars.Length > 0))
        {
            if (!LayoutPlanBuilder.UdonEventParamNames.TryGetValue(ev.MethodName, out var actual))
                bad.Add($"{ev.MethodName}: row MISSING (params silently unbound); expected [{string.Join(", ", ev.ParamVars)}]");
            else if (!actual.SequenceEqual(ev.ParamVars, StringComparer.Ordinal))
                bad.Add($"{ev.MethodName}: expected [{string.Join(", ", ev.ParamVars)}], got [{string.Join(", ", actual)}]");
        }
        Assert.True(bad.Count == 0, "UdonEventParamNames drift vs SDK registry:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void ParamlessRegistryEvents_HaveNoParamRow()
    {
        var bad = Registry
            .Where(ev => ev.ParamVars.Length == 0 && LayoutPlanBuilder.UdonEventParamNames.ContainsKey(ev.MethodName))
            .Select(ev => ev.MethodName)
            .ToList();
        Assert.True(bad.Count == 0, "param rows for param-less events (stale data): " + string.Join(", ", bad));
    }

    // ── Direction 2: tables → registry (no stale rows) ──

    [Fact]
    public void EveryEventNameRow_ExistsInRegistry_OrIsExplicitlyExempt()
    {
        var registryNames = new HashSet<string>(Registry.Select(e => e.MethodName), StringComparer.Ordinal);
        var stale = LayoutPlanBuilder.UdonEventNames.Keys
            .Where(k => !registryNames.Contains(k) && !RegistryLessEvents.ContainsKey(k))
            .ToList();
        Assert.True(stale.Count == 0,
            "UdonEventNames rows with no SDK registry node and no documented exemption: " + string.Join(", ", stale));
    }

    [Fact]
    public void EveryParamRow_KeyExistsInRegistry()
    {
        var registryNames = new HashSet<string>(Registry.Select(e => e.MethodName), StringComparer.Ordinal);
        var stale = LayoutPlanBuilder.UdonEventParamNames.Keys.Where(k => !registryNames.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "UdonEventParamNames rows with no SDK registry node: " + string.Join(", ", stale));
    }

    [Fact]
    public void ExemptRows_DoNotShadowRegistryRows_AndMatchTheTable()
    {
        var registryNames = new HashSet<string>(Registry.Select(e => e.MethodName), StringComparer.Ordinal);
        foreach (var (name, export) in RegistryLessEvents)
        {
            Assert.False(registryNames.Contains(name),
                $"'{name}' is exempt as registry-less but the SDK registry now defines it — delete the exemption");
            Assert.True(LayoutPlanBuilder.UdonEventNames.TryGetValue(name, out var actual) && actual == export,
                $"exempt event '{name}' must exist in UdonEventNames as {export}");
            Assert.False(LayoutPlanBuilder.UdonEventParamNames.ContainsKey(name),
                $"exempt event '{name}' is documented param-less but has a param row");
        }
    }

    // ── Self-consistency pins (fixture-independent — catch hand-edit typos even in the tables alone) ──

    [Fact]
    public void EveryParamRowKey_ExistsInEventNames()
    {
        // Audit hazard #2: a param row whose event row was dropped/renamed is dead data — and the
        // event, if still compiled, binds nothing.
        var orphans = LayoutPlanBuilder.UdonEventParamNames.Keys
            .Where(k => !LayoutPlanBuilder.UdonEventNames.ContainsKey(k))
            .ToList();
        Assert.True(orphans.Count == 0, "UdonEventParamNames keys missing from UdonEventNames: " + string.Join(", ", orphans));
    }

    [Fact]
    public void EveryExportName_FollowsTheUnderscoreLowerFirstRule()
    {
        // No irregulars exist today; a genuine irregular would need its own exempt list with a reason.
        var bad = LayoutPlanBuilder.UdonEventNames
            .Where(kv => kv.Value != "_" + char.ToLowerInvariant(kv.Key[0]) + kv.Key[1..])
            .Select(kv => $"{kv.Key} → {kv.Value}")
            .ToList();
        Assert.True(bad.Count == 0, "export names violating '_' + lowerFirst(event): " + string.Join(", ", bad));
    }

    [Fact]
    public void EveryParamVar_FollowsTheLowerEventUpperParamDerivation()
    {
        // Documented shape {lowerEventNoUnderscore}{UpperParam}: prefix is the lowered event name and
        // the remainder starts uppercase. (Which param name is correct is the fixture tests' job.)
        var bad = new List<string>();
        foreach (var (evt, vars) in LayoutPlanBuilder.UdonEventParamNames)
        {
            var lowered = char.ToLowerInvariant(evt[0]) + evt[1..];
            foreach (var v in vars)
                if (!v.StartsWith(lowered, StringComparison.Ordinal)
                    || v.Length == lowered.Length
                    || !char.IsUpper(v[lowered.Length]))
                    bad.Add($"{evt}: {v}");
        }
        Assert.True(bad.Count == 0, "param vars violating {lowerEvent}{UpperParam}: " + string.Join(", ", bad));
    }

    [Fact]
    public void ExportNameCache_IsExactlyTheEventNameValues()
    {
        Assert.True(LayoutPlanBuilder.UdonEventExportNames.SetEquals(LayoutPlanBuilder.UdonEventNames.Values));
    }
}
