using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnityEditor;
using UnityEditor.Compilation;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharpEditor;
using VRC.Udon;
using VRC.Udon.Editor;
using VRC.Udon.Common.Interfaces;
using VRC.SDK3.UdonNetworkCalling;
using UnityCompilationAssembly = UnityEditor.Compilation.Assembly;

/// <summary>
/// Orchestrates the 3-phase compile pipeline: serial preparation, parallel emit, serial apply.
/// </summary>
static class USugarCompilationOrchestrator
{
    static readonly USugarCompileRequestState CompileState = new();
    static bool RequestedEditorBuild = true;
    static bool RequestedForce;
    static int RequestedPayloadVersion;
    internal static bool IsCompiling;
    internal static bool CompileScheduled;
    internal static USugarCompileHealth Health => CompileState.Health;
    internal static bool LastCompileHadErrors
    {
        get => Health != USugarCompileHealth.Clean;
        set
        {
            if (value) CompileState.MarkFailed();
            else CompileState.MarkClean();
        }
    }

    const string FingerprintKey = "USugar_LastFingerprint";
    const string AppliedKey = "USugar_LastApplied";
    const string FingerprintSchema = "editor-integration-v4";
    internal sealed class CompilationUnit
    {
        public UnityCompilationAssembly UnityAssembly { get; }
        public IReadOnlyList<UnityCompilationAssembly>
            SourceAssemblies { get; }
        public IReadOnlyList<string> RootSourcePaths { get; }
        public IReadOnlyList<string> SourcePaths { get; }
        public IReadOnlyDictionary<string, UnityCompilationAssembly>
            SourceOwners { get; }
        public CSharpCompilation Compilation { get; set; }
        public CompilationSession Session { get; set; }
        public FrozenLayoutPlan Planner { get; set; }
        public IReadOnlyList<(INamedTypeSymbol symbol, SyntaxTree tree)> Behaviours { get; set; }

        public CompilationUnit(
            UnityCompilationAssembly unityAssembly,
            IReadOnlyList<UnityCompilationAssembly> sourceAssemblies,
            IReadOnlyDictionary<string, UnityCompilationAssembly>
                sourceOwners)
        {
            UnityAssembly = unityAssembly;
            SourceAssemblies = sourceAssemblies;
            SourceOwners = sourceOwners;
            RootSourcePaths = unityAssembly.sourceFiles
                .Where(USugarEditorIntegrationPolicy.IsCSharpSource)
                .Select(NormalizeUnitySourcePath)
                .Where(sourceOwners.ContainsKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            SourcePaths = sourceOwners.Keys
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
    }

    internal struct EmitResult
    {
        public INamedTypeSymbol Symbol;
        public SyntaxTree Tree;
        public string Uasm;
        public List<(string Id, string UdonType, object Value)> Constants;
        public uint HeapSize;
        public IReadOnlyList<EmitDiagnostic> EmitterDiagnostics;
        public List<(string file, int line, int character, string message, string severity)> ErrorDiagnostics;
        public BoundProgram Program;
        public bool IsError;

        public EmitResult(INamedTypeSymbol symbol, SyntaxTree tree, string uasm,
            List<(string Id, string UdonType, object Value)> constants, uint heapSize,
            IReadOnlyList<EmitDiagnostic> diagnostics, BoundProgram program)
        {
            Symbol = symbol; Tree = tree; Uasm = uasm;
            Constants = constants; HeapSize = heapSize;
            EmitterDiagnostics = diagnostics;
            Program = program;
            ErrorDiagnostics = null; IsError = false;
        }

        public static EmitResult Error(INamedTypeSymbol symbol, SyntaxTree tree,
            string file, int line, int character, string message)
        {
            return new EmitResult
            {
                Symbol = symbol, Tree = tree, IsError = true,
                ErrorDiagnostics = new() { (file, line, character, message, "Error") }
            };
        }
    }

    sealed class PreparedApply
    {
        public EmitResult Result;
        public UdonSharpProgramAsset Asset;
        public IUdonProgram Program;
        public Dictionary<string, FieldDefinition> FieldDefinitions;
        public NetworkCallingEntrypointMetadata[] NetworkMetadata;
        public int SyncMode;
    }

    sealed class ProgramAssetBinding
    {
        public string ProgramAssetGuid;
        public string ProgramAssetPath;
        public UdonSharpProgramAsset Asset;
        public string ScriptGuid;
        public string ScriptPath;
        public string NormalizedSourcePath;
        public string UnityAssemblyName;
        public Type SourceClass;
        public bool Selected;
        public bool Matched;
    }

    internal static void MarkCompileUnknown()
        => CompileState.MarkUnknown();

    internal static void RequestCompile(
        bool editorBuild = true,
        bool force = false)
    {
        RecordCompileRequest(editorBuild, force);
        SchedulePendingCompile();
    }

    static int RecordCompileRequest(
        bool editorBuild,
        bool force)
    {
        var version = CompileState.Request();
        RequestedEditorBuild = editorBuild;
        RequestedForce |= force;
        RequestedPayloadVersion = version;
        return version;
    }

    static void SchedulePendingCompile()
    {
        if (!CompileState.HasPendingRequest || CompileScheduled) return;
        CompileScheduled = true;
        EditorApplication.delayCall += RunCompile;
    }

    internal static void RunCompile()
    {
        CompileScheduled = false;
        if (!CompileState.HasPendingRequest) return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            SchedulePendingCompile();
            return;
        }
        var versionAtStart = CompileState.RequestedVersion;
        if (RequestedPayloadVersion != versionAtStart)
            throw new InvalidOperationException(
                "The pending USugar compile request has no build-mode payload.");
        var editorBuild = RequestedEditorBuild;
        var force = RequestedForce;
        RequestedForce = false;
        IsCompiling = true;
        try
        {
            CompileInternal(
                applyToAssets: true,
                force: force,
                editorBuild: editorBuild);
        }
        finally
        {
            CompileState.Complete(versionAtStart);
            IsCompiling = false;
            SchedulePendingCompile();
        }
    }

    internal static void CompileSynchronously(
        bool force = false,
        bool editorBuild = true)
    {
        var versionAtStart =
            RecordCompileRequest(editorBuild, force);
        if (IsCompiling)
        {
            SchedulePendingCompile();
            return;
        }

        force = RequestedForce;
        RequestedForce = false;
        IsCompiling = true;
        try
        {
            CompileInternal(
                applyToAssets: true,
                force: force,
                editorBuild: editorBuild);
        }
        finally
        {
            CompileState.Complete(versionAtStart);
            IsCompiling = false;
            SchedulePendingCompile();
        }
    }

    internal static void WaitForCompile()
    {
        if (IsCompiling || !CompileState.HasPendingRequest)
            return;

        if (CompileScheduled)
        {
            EditorApplication.delayCall -= RunCompile;
            CompileScheduled = false;
        }
        CompileSynchronously(
            force: RequestedForce,
            editorBuild: RequestedEditorBuild);
    }

    internal static void CompileInternal(
        bool applyToAssets,
        bool force = false,
        bool editorBuild = true)
    {
        MarkCompileUnknown();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var collectedDiagnostics = new List<(string file, int line, int character, string message, string severity)>();
        string fingerprint = null;
        var preparedApplies = new List<PreparedApply>();

        try
        {
            if (USugarReflectionTargets.DoesUnityProjectHaveCompileErrors.Invoke(
                    null, null) is true)
            {
                const string message =
                    "All Unity C# compiler errors must be resolved before running an UdonSharp compile.";
                USugarLog.Error(message);
                collectedDiagnostics.Add(("", 0, 0, message, "Error"));
                LastCompileHadErrors = true;
                return;
            }

            // ── Phase 1: Serial preparation ──
            USugarExactSourcePathIndex<ProgramAssetBinding> programAssetIndex = null;
            List<ProgramAssetBinding> programAssetBindings = null;
            if (applyToAssets)
                programAssetIndex = CollectProgramAssetBindings(out programAssetBindings);
            var compilationUnits = CollectCompilationUnits(
                programAssetBindings?.Select(binding => binding.UnityAssemblyName),
                editorBuild);
            if (compilationUnits.Count == 0)
            {
                if (programAssetBindings != null && programAssetBindings.Count > 0)
                    throw new InvalidOperationException(
                        $"{programAssetBindings.Count} UdonSharpProgramAsset(s) exist, but no player "
                        + "source assembly containing an UdonSharpBehaviour was collected. Refusing "
                        + "to leave their previously compiled programs active.");
                USugarLog.Warn("No UdonSharpBehaviour sources found");
                LastCompileHadErrors = false;
                return;
            }

            fingerprint = ComputeFingerprint(
                compilationUnits,
                programAssetBindings,
                editorBuild);
            var lastFp = SessionState.GetString(FingerprintKey, "");
            var lastApplied = SessionState.GetBool(AppliedKey, false);
            if (!force && fingerprint == lastFp && (!applyToAssets || lastApplied))
            {
                // The cached fingerprint only advances on a failures==0 run, so matching content is
                // known-clean. Clear the error flag here: otherwise reverting (Ctrl+Z) to the last clean
                // content after a failed compile would strand LastCompileHadErrors=true (this return skips
                // the normal assignment below) and block Play/upload with no visible diagnostic — and stock
                // UdonSharp's recovery CompileSync would early-return here again, never clearing it.
                LastCompileHadErrors = false;
                return;
            }

            var externRegistry = UdonAbiCatalogFactory.Create(
                UdonEditorManager.Instance.GetNodeDefinitions());

            foreach (var unit in compilationUnits)
            {
                unit.Compilation = BuildCompilation(
                    unit, editorBuild);
                unit.Session = new CompilationSession(unit.Compilation, externRegistry);
                unit.Behaviours = CollectBehaviourDeclarations(
                    unit.Compilation, unit.RootSourcePaths);
            }
            PruneTreeCache(compilationUnits.SelectMany(unit => unit.SourcePaths));

            // A Roslyn Compilation is the unit of correctness. Checking only the representative
            // declaration tree of each behaviour misses errors in helper files and in the other parts
            // of a partial class. Reject the whole run before layout planning or asset mutation.
            var compilationErrors = compilationUnits
                .SelectMany(unit => unit.Compilation.GetDiagnostics())
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();
            if (compilationErrors.Length > 0)
            {
                foreach (var diag in compilationErrors)
                {
                    var span = diag.Location.IsInSource
                        ? diag.Location.GetLineSpan() : default;
                    var file = span.Path ?? "";
                    var line = diag.Location.IsInSource ? span.StartLinePosition.Line + 1 : 0;
                    var character = diag.Location.IsInSource ? span.StartLinePosition.Character + 1 : 0;
                    var message = diag.GetMessage();
                    USugarLog.Error($"{file}({line},{character}): {message}");
                    collectedDiagnostics.Add((file, line, character, message, "Error"));
                }
                LastCompileHadErrors = true;
                return;
            }

            // Collect all UdonSharpBehaviour classes
            var classList = new List<(INamedTypeSymbol symbol, SyntaxTree tree,
                CompilationSession session, FrozenLayoutPlan planner)>();
            var selectedProgramAssets = applyToAssets
                ? new Dictionary<INamedTypeSymbol, ProgramAssetBinding>(
                    SymbolEqualityComparer.Default)
                : null;
            var rootSelectionFailed = false;
            void RootSelectionError(string file, string message)
            {
                USugarLog.Error($"{file}: {message}");
                collectedDiagnostics.Add((file, 0, 0, message, "Error"));
                rootSelectionFailed = true;
            }
            // A partial class has one declaration node per part but ONE symbol — emit it once, not once per part.
            foreach (var unit in compilationUnits)
            {
                unit.Planner = new LayoutPlanBuilder(unit.Session).Build();
                foreach (var (symbol, tree) in unit.Behaviours)
                {
                    if (applyToAssets)
                    {
                        var declarationPaths = GetDeclarationPaths(symbol, tree);
                        var pathCandidates = programAssetIndex.GetCandidates(declarationPaths);
                        if (pathCandidates.Count == 0)
                            continue;

                        // A source file may legally contain helper behaviours in addition to the
                        // MonoScript's primary class. The ProgramAsset belongs to the CLR type
                        // returned by MonoScript.GetClass(), not to every behaviour declaration in
                        // that file.
                        var resolvedBehaviourType = ClrTypeResolver.Resolve(symbol);
                        var candidates = resolvedBehaviourType == null
                            ? Array.Empty<ProgramAssetBinding>()
                            : pathCandidates
                                .Where(candidate => candidate.SourceClass == resolvedBehaviourType)
                                .ToArray();
                        if (candidates.Length == 0)
                            continue;
                        if (candidates.Length != 1)
                        {
                            RootSelectionError(
                                tree.FilePath,
                                $"{candidates.Length} UdonSharpProgramAssets are bound to behaviour "
                                + $"'{symbol.ToDisplayString()}' through declaration sources "
                                + $"[{string.Join(", ", declarationPaths)}].");
                            continue;
                        }

                        var binding = candidates[0];
                        if (binding.Selected)
                        {
                            RootSelectionError(
                                binding.ScriptPath,
                                $"Program asset '{binding.ProgramAssetPath}' resolves to more than one "
                                + "UdonSharpBehaviour declaration.");
                            continue;
                        }
                        binding.Selected = true;
                        if (!USugarProgramRootPolicy.ShouldEmit(
                                symbol, hasProgramAsset: true, out var rootError))
                        {
                            RootSelectionError(binding.ScriptPath, rootError);
                            continue;
                        }
                        var emittedAssemblyName = symbol.ContainingAssembly.Identity.Name;
                        if (!string.Equals(
                                binding.UnityAssemblyName, emittedAssemblyName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            RootSelectionError(
                                binding.ScriptPath,
                                $"Program asset '{binding.ProgramAssetPath}' belongs to Unity assembly "
                                + $"'{binding.UnityAssemblyName}', not emitted assembly "
                                + $"'{emittedAssemblyName}'.");
                            continue;
                        }
                        selectedProgramAssets.Add(symbol, binding);
                    }
                    classList.Add((symbol, tree, unit.Session, unit.Planner));
                }
            }
            if (applyToAssets)
                foreach (var orphan in programAssetBindings.Where(binding => !binding.Selected))
                    RootSelectionError(
                        orphan.ScriptPath,
                        $"Program asset '{orphan.ProgramAssetPath}' is not backed by one concrete, "
                        + "non-generic UdonSharpBehaviour in its exact source script.");
            if (rootSelectionFailed)
            {
                LastCompileHadErrors = true;
                return;
            }
            // Pre-plan all layouts (serial, populates cache)

            // The immutable SDK ABI catalog and type facts are already bound to each CompilationSession.
            // Phase 2 consumes only that session-owned authority; no ambient registry hook exists.
            // ── Phase 2: Parallel emit ──
            var emitResults = new System.Collections.Concurrent.ConcurrentBag<EmitResult>();
            System.Threading.Tasks.Parallel.ForEach(classList, classInfo =>
            {
                var (symbol, tree, session, planner) = classInfo;
                try
                {
                    var emitter = new UasmEmitter(session, symbol, planner);
                    var uasm = emitter.Emit();
                    emitResults.Add(new EmitResult(symbol, tree, uasm,
                        emitter.CodeGenResult.Constants, emitter.GetHeapSize(),
                        emitter.Diagnostics, emitter.Program));
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie
                        && tie.InnerException != null ? tie.InnerException : ex;
                    // Use the declaration's actual source path so editor diagnostics remain clickable.
                    var filePath = tree.FilePath ?? "";
                    var line = 0;
                    var character = 0;
                    var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
                    if (syntaxRef != null)
                    {
                        filePath = syntaxRef.SyntaxTree.FilePath ?? filePath;
                        var span = syntaxRef.SyntaxTree.GetLineSpan(syntaxRef.Span);
                        line = span.StartLinePosition.Line + 1;
                        character = span.StartLinePosition.Character + 1;
                    }
                    emitResults.Add(EmitResult.Error(
                        symbol, tree, filePath, line, character,
                        $"Failed to compile {symbol.Name}: {inner.Message}"));
                }
            });

            // ── Phase 3: Serial apply ──
            // Order by assembly and fully-qualified type for deterministic output
            // (ConcurrentBag yields in arbitrary order).
            var orderedResults = emitResults
                .OrderBy(r => r.Symbol.ContainingAssembly.Identity.Name, StringComparer.Ordinal)
                .ThenBy(r => r.Symbol.ToDisplayString(), StringComparer.Ordinal)
                .ToArray();
            var validResults = new List<EmitResult>();
            int count = 0, failures = 0;
            foreach (var result in orderedResults)
            {
                if (result.IsError)
                {
                    foreach (var d in result.ErrorDiagnostics)
                    {
                        USugarLog.Error($"{d.file}({d.line},{d.character}): {d.message}");
                        collectedDiagnostics.Add(d);
                    }
                    failures++;
                    continue;
                }
                count++;

                // Merge emitter diagnostics. Anything not explicitly "Warning" is treated as an error
                // (default-deny: a typo'd severity must not silently demote to a shipped program).
                bool hasEmitError = false;
                foreach (var d in result.EmitterDiagnostics)
                {
                    collectedDiagnostics.Add((d.FilePath, d.Line, d.Character, d.Message, d.Severity));
                    if (d.Severity == "Warning")
                        USugarLog.Warn($"{d.FilePath}({d.Line},{d.Character}): {d.Message}");
                    else
                    {
                        USugarLog.Error($"{d.FilePath}({d.Line},{d.Character}): {d.Message}");
                        hasEmitError = true;
                    }
                }
                if (hasEmitError)
                {
                    // The emitted program is known-broken (e.g. aliased lambda captures, null-placeholder
                    // 'new'). Never apply it to the asset; counting it as a failure keeps the fingerprint
                    // from advancing and makes LastCompileHadErrors block Play/upload.
                    failures++;
                    continue;
                }
                validResults.Add(result);
            }

            if (applyToAssets && failures == 0)
            {
                foreach (var result in validResults)
                {
                    var binding = selectedProgramAssets[result.Symbol];
                    binding.Matched = true;
                    var programAsset = binding.Asset;

                    var program = USugarConstantApplier.AssembleUasm(
                        result.Uasm,
                        result.HeapSize,
                        out var assemblyError);
                    if (program == null)
                    {
                        var failMsg =
                            $"Failed to assemble UASM for {result.Symbol.Name}: "
                            + assemblyError;
                        USugarReflectionTargets.ProgramField.SetValue(
                            programAsset, null);
                        USugarReflectionTargets.AssemblyErrorField.SetValue(
                            programAsset, assemblyError);
                        programAsset.hasInteractEvent = false;
                        USugarLog.Error(failMsg);
                        collectedDiagnostics.Add((result.Tree.FilePath, 0, 0, failMsg, "Error"));
                        failures++;
                        continue;
                    }
                    try
                    {
                        USugarConstantApplier.ApplyConstantValues(program, result.Constants);
                        var fieldDefinitions =
                            USugarTypeCacheManager.BuildFieldDefinitions(
                                result.Program.Fields,
                                result.Symbol);
                        // [NetworkCallable] entry-point metadata — required for SendCustomNetworkEvent with
                        // parameters (the runtime looks up the event + its param types via this metadata).
                        var netMeta = BuildNetworkCallingMetadata(
                            result.Symbol,
                            result.Program.Types,
                            result.Program.Layouts);
                        var serializedAsset = programAsset.SerializedProgramAsset;
                        if (serializedAsset == null)
                            throw new InvalidOperationException(
                                $"Serialized program asset is missing for {result.Symbol.Name}");
                        preparedApplies.Add(new PreparedApply
                        {
                            Result = result,
                            Asset = programAsset,
                            Program = program,
                            FieldDefinitions = fieldDefinitions,
                            NetworkMetadata = netMeta,
                            SyncMode = USugarCompilerHelper.GetBehaviourSyncMode(result.Symbol)
                        });
                    }
                    catch (Exception ex)
                    {
                        var failMsg =
                            $"Failed to prepare program asset for {result.Symbol.Name}: {ex.Message}";
                        USugarLog.Error(failMsg);
                        collectedDiagnostics.Add((result.Tree.FilePath, 0, 0, failMsg, "Error"));
                        failures++;
                    }
                }
            }

            if (applyToAssets && failures == 0)
            {
                foreach (var orphan in programAssetBindings.Where(binding => !binding.Matched))
                {
                    var message = $"Program asset '{orphan.ProgramAssetPath}' is not backed by any "
                                  + "emitted UdonSharpBehaviour. Its stale compiled program will not be "
                                  + "accepted; delete or relink the asset.";
                    USugarLog.Error(message);
                    collectedDiagnostics.Add((orphan.ScriptPath, 0, 0, message, "Error"));
                    failures++;
                }
            }

            if (applyToAssets && failures == 0)
            {
                foreach (var prepared in preparedApplies)
                {
                    var programAsset = prepared.Asset;
                    USugarReflectionTargets.ProgramField.SetValue(
                        programAsset, prepared.Program);
                    USugarReflectionTargets.AssemblyErrorField.SetValue(
                        programAsset, null);
                    programAsset.hasInteractEvent =
                        prepared.Program.EntryPoints
                            .GetExportedSymbols()
                            .Contains("_interact");
                    programAsset.scriptID =
                        UdonBehaviourTypeMetadata.TypeId(
                            prepared.Result.Symbol);
                    programAsset.fieldDefinitions = prepared.FieldDefinitions;
                    programAsset.SetNetworkCallingMetadata(prepared.NetworkMetadata);
                    programAsset.behaviourSyncMode = prepared.SyncMode >= 0
                        ? (BehaviourSyncMode)prepared.SyncMode
                        : BehaviourSyncMode.Any;
                    programAsset.SetUdonAssembly("");
                    programAsset.ApplyProgram();
                    programAsset.CompiledVersion = UdonSharpProgramVersion.CurrentVersion;
                    EditorUtility.SetDirty(programAsset);
                    PushUasmToEditorCache(programAsset, prepared.Result.Uasm);
                }

                LastCompileHadErrors = false;
                CompleteSuccessfulEditorIntegration(editorBuild);
            }
            sw.Stop();
            LastCompileHadErrors = failures > 0;
            // Advance the "up-to-date / applied" success state ONLY on a clean run. On ANY failure (emit error,
            // asset-miss, or assemble failure) leave the fingerprint UNCHANGED so the next compile re-runs and
            // re-diagnoses instead of early-returning on a falsely-cached fingerprint (which would strand a stale
            // asset with no diagnostic, and leave LastCompileHadErrors holding this run's value unread by the
            // skipped next compile). Mirrors stock UdonSharp's rehash-only-on-success discipline.
            if (failures == 0)
            {
                SessionState.SetString(FingerprintKey, fingerprint);
                SessionState.SetBool(AppliedKey, applyToAssets || lastApplied);
            }
            var msg = failures > 0
                ? $"Compile of {count} script{(count != 1 ? "s" : "")} finished in {sw.Elapsed:mm\\:ss\\.fff} ({failures} failed)"
                : $"Compile of {count} script{(count != 1 ? "s" : "")} finished in {sw.Elapsed:mm\\:ss\\.fff}";
            USugarLog.Info(msg);
        }
        catch (Exception ex)
        {
            USugarLog.Error(ex);
            collectedDiagnostics.Add(("", 0, 0, ex.Message, "Error"));
            LastCompileHadErrors = true;
        }
        finally
        {
            PushDiagnosticsToEditorCache(collectedDiagnostics);
        }
    }

    // ── Editor cache integration ──

    static void PushDiagnosticsToEditorCache(List<(string file, int line, int character, string message, string severity)> diagnostics)
    {
        try
        {
            var instance = USugarReflectionTargets.GetEditorCacheInstance();
            if (instance == null) return;

            var diagType = USugarReflectionTargets.CompileDiagnosticType;
            if (diagType == null) return;

            // Fail loud if the per-field bindings broke (e.g. an SDK rename of a CompileDiagnostic field, where
            // the type still resolves but a FieldInfo becomes null). Otherwise the null FieldInfo would NRE
            // inside the loop, get swallowed by the catch below as a mere Warn, and silently drop ALL inline
            // diagnostics while the compile still appears to succeed — the documented fail-silent failure mode.
            if (USugarReflectionTargets.DiagSeverity == null || USugarReflectionTargets.DiagFile == null
                || USugarReflectionTargets.DiagLine == null || USugarReflectionTargets.DiagCharacter == null
                || USugarReflectionTargets.DiagMessage == null)
            {
                USugarLog.Error("CompileDiagnostic field bindings did not resolve — inline editor diagnostics disabled. The UdonSharp SDK may have changed; check USugarReflectionTargets. (Console errors still report normally.)");
                return;
            }

            var arr = Array.CreateInstance(diagType, diagnostics.Count);
            for (int i = 0; i < diagnostics.Count; i++)
            {
                var diag = Activator.CreateInstance(diagType);
                var sevName = diagnostics[i].severity ?? "Error";
                try { USugarReflectionTargets.DiagSeverity.SetValue(diag, Enum.Parse(USugarReflectionTargets.DiagSeverity.FieldType, sevName)); }
                catch { USugarReflectionTargets.DiagSeverity.SetValue(diag, Enum.Parse(USugarReflectionTargets.DiagSeverity.FieldType, "Error")); }
                USugarReflectionTargets.DiagFile.SetValue(diag, diagnostics[i].file ?? "");
                USugarReflectionTargets.DiagLine.SetValue(
                    diag,
                    diagnostics[i].line > 0
                        ? diagnostics[i].line - 1
                        : 0);
                USugarReflectionTargets.DiagCharacter.SetValue(
                    diag,
                    diagnostics[i].character > 0
                        ? diagnostics[i].character - 1
                        : 0);
                USugarReflectionTargets.DiagMessage.SetValue(diag, diagnostics[i].message ?? "");
                arr.SetValue(diag, i);
            }

            USugarReflectionTargets.LastCompileDiagnosticsProp?.SetValue(instance, arr);
        }
        catch (Exception ex)
        {
            USugarLog.Warn($"Failed to push diagnostics to editor cache: {ex.Message}");
        }
    }

    // Build the [NetworkCallable] entry-point metadata for a class: for each tagged method, its (unmangled)
    // export name + per-parameter (mangled export name, CLR type). The runtime/ClientSim uses this to resolve
    // a SendCustomNetworkEvent-with-parameters call to the receiver method and marshal its arguments.
    static NetworkCallingEntrypointMetadata[] BuildNetworkCallingMetadata(
        INamedTypeSymbol classSymbol, BoundUdonTypeSystem types,
        FrozenLayoutPlan planner)
    {
        var layout = planner.GetLayout(classSymbol);
        if (layout == null) return Array.Empty<NetworkCallingEntrypointMetadata>();
        var list = new List<NetworkCallingEntrypointMetadata>();
        var exports = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in USugarNetworkMetadataPolicy.GetCallableMethods(layout))
        {
            var method = pair.Key;
            var ml = pair.Value;
            if (!exports.Add(ml.ExportName))
                throw new InvalidOperationException(
                    $"Multiple [NetworkCallable] methods map to export '{ml.ExportName}' "
                    + $"on '{classSymbol.ToDisplayString()}'.");
            if (ml.ParamIds.Count != method.Parameters.Length)
                throw new InvalidOperationException(
                    $"[NetworkCallable] parameter layout drift for '{method.ToDisplayString()}'.");

            var attrData = method.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "NetworkCallableAttribute");
            int rate = attrData != null && attrData.ConstructorArguments.Length > 0
                       && attrData.ConstructorArguments[0].Value is int r ? r : 0;
            var attr = new NetworkCallableAttribute(rate);

            var pmeta = new NetworkCallingParameterMetadata[method.Parameters.Length];
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                var sourceType = USugarTypeCacheManager.ResolveClrType(
                    method.Parameters[i].Type);
                var lowering = types.Describe(
                    method.Parameters[i].Type);
                var parameterType =
                    USugarTypeCacheManager.ResolveStorageClrType(
                        lowering.Storage.Id, sourceType)
                    ?? throw new InvalidOperationException(
                        $"Could not resolve Udon storage type for [NetworkCallable] parameter "
                        + $"'{method.Parameters[i].Name}' on '{method.ToDisplayString()}'.");
                pmeta[i] = new NetworkCallingParameterMetadata(
                    ml.ParamIds[i], parameterType);
            }

            list.Add(new NetworkCallingEntrypointMetadata(ml.ExportName, attr, pmeta));
        }
        return list.ToArray();
    }

    static void PushUasmToEditorCache(UdonSharpProgramAsset programAsset, string uasm)
    {
        try
        {
            var instance = USugarReflectionTargets.GetEditorCacheInstance();
            if (instance == null) return;

            USugarReflectionTargets.SetUasmStr?.Invoke(instance, new object[] { programAsset, uasm });
        }
        catch (Exception ex)
        {
            USugarLog.Warn($"Failed to push UASM to editor cache: {ex.Message}");
        }
    }

    // ── Helpers ──

    static void CompleteSuccessfulEditorIntegration(bool editorBuild)
    {
        var cache = USugarReflectionTargets.GetEditorCacheInstance()
                    ?? throw new InvalidOperationException(
                        "UdonSharpEditorCache.Instance is unavailable.");
        USugarReflectionTargets.RehashAllScripts.Invoke(cache, null);
        USugarReflectionTargets.RunPostBuildSceneFixup.Invoke(null, null);
        USugarReflectionTargets.LastBuildType.SetValue(
            cache,
            Enum.Parse(
                USugarReflectionTargets.DebugInfoType,
                editorBuild ? "Editor" : "Client"));
    }

    static USugarExactSourcePathIndex<ProgramAssetBinding> CollectProgramAssetBindings(
        out List<ProgramAssetBinding> bindings)
    {
        bindings = new List<ProgramAssetBinding>();
        var index = new USugarExactSourcePathIndex<ProgramAssetBinding>();
        foreach (var asset in UdonSharpProgramAsset.GetAllUdonSharpPrograms()
            .Where(value => value != null)
            .OrderBy(AssetDatabase.GetAssetPath, StringComparer.Ordinal))
        {
            var programAssetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(programAssetPath))
                throw new InvalidOperationException(
                    $"UdonSharpProgramAsset '{asset.name}' has no AssetDatabase path.");
            var guid = AssetDatabase.AssetPathToGUID(programAssetPath);
            if (string.IsNullOrEmpty(guid))
                throw new InvalidOperationException(
                    $"UdonSharpProgramAsset '{programAssetPath}' has no GUID.");
            if (asset.sourceCsScript == null)
                throw new InvalidOperationException(
                    $"UdonSharpProgramAsset '{programAssetPath}' has no source MonoScript. "
                    + "Delete or relink the orphaned asset before compiling.");

            var scriptPath = AssetDatabase.GetAssetPath(asset.sourceCsScript);
            if (string.IsNullOrEmpty(scriptPath))
                throw new InvalidOperationException(
                    $"The source MonoScript for '{programAssetPath}' has no AssetDatabase path.");
            var scriptGuid = AssetDatabase.AssetPathToGUID(scriptPath);
            if (string.IsNullOrEmpty(scriptGuid))
                throw new InvalidOperationException(
                    $"The source MonoScript '{scriptPath}' for '{programAssetPath}' has no GUID.");
            var sourceClass = asset.sourceCsScript.GetClass();
            if (sourceClass == null || !typeof(UdonSharpBehaviour).IsAssignableFrom(sourceClass))
                throw new InvalidOperationException(
                    $"The source MonoScript '{scriptPath}' for '{programAssetPath}' does not resolve "
                    + "to an UdonSharpBehaviour CLR type.");

            var assemblyName = CompilationPipeline.GetAssemblyNameFromScriptPath(scriptPath);
            if (string.IsNullOrEmpty(assemblyName))
                throw new InvalidOperationException(
                    $"Unity did not resolve a player assembly for UdonSharp source '{scriptPath}'.");

            var binding = new ProgramAssetBinding
            {
                ProgramAssetGuid = guid,
                ProgramAssetPath = programAssetPath,
                Asset = asset,
                ScriptGuid = scriptGuid,
                ScriptPath = scriptPath,
                NormalizedSourcePath = NormalizeUnitySourcePath(scriptPath),
                UnityAssemblyName = Path.GetFileNameWithoutExtension(assemblyName),
                SourceClass = sourceClass,
            };
            bindings.Add(binding);
            index.Add(binding.NormalizedSourcePath, binding);
        }
        return index;
    }

    static string[] GetDeclarationPaths(INamedTypeSymbol symbol, SyntaxTree representativeTree)
        => symbol.DeclaringSyntaxReferences
            .Select(reference => reference.SyntaxTree.FilePath)
            .Append(representativeTree?.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeUnitySourcePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    static string NormalizeUnitySourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Unity source path is required.", nameof(path));

        var assetPath = path.Replace('\\', '/');
        if (!Path.IsPathRooted(path)
            && assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            if (package != null && !string.IsNullOrEmpty(package.resolvedPath)
                && !string.IsNullOrEmpty(package.assetPath)
                && assetPath.StartsWith(
                    package.assetPath + "/", StringComparison.OrdinalIgnoreCase))
            {
                var packageRelative = assetPath.Substring(package.assetPath.Length + 1);
                return USugarEditorIntegrationPolicy.NormalizeSourcePath(
                    package.resolvedPath, packageRelative);
            }
        }

        var projectRoot = Directory.GetParent(UnityEngine.Application.dataPath)?.FullName
                          ?? throw new InvalidOperationException(
                              "Unity project root could not be resolved.");
        return USugarEditorIntegrationPolicy.NormalizeSourcePath(projectRoot, path);
    }

    static string GetObjectIdentity(UnityEngine.Object value)
    {
        if (value == null) return "<null>";
        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                value, out string guid, out long localId))
            return guid + ":" + localId;
        return value.GetType().FullName + ":" + value.GetInstanceID();
    }

    static string ComputeFingerprint(
        IReadOnlyList<CompilationUnit> compilationUnits,
        IReadOnlyList<ProgramAssetBinding> programAssetBindings,
        bool editorBuild)
    {
        using var sha256 = SHA256.Create();
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(FingerprintSchema);
        writer.Write(editorBuild);
        writer.Write(typeof(USugarCompilationOrchestrator).Assembly.ManifestModule.ModuleVersionId
            .ToString("N"));
        var referenceIdentities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in compilationUnits.OrderBy(
            unit => unit.UnityAssembly.name, StringComparer.Ordinal))
        {
            writer.Write(unit.UnityAssembly.name);
            foreach (var p in unit.SourcePaths.OrderBy(s => s, StringComparer.Ordinal))
            {
                writer.Write(p);
                writer.Write(ComputeFileContentHash(p));
            }
            foreach (var sourceAssembly in unit.SourceAssemblies.OrderBy(
                         assembly => assembly.name, StringComparer.Ordinal))
            {
                writer.Write(sourceAssembly.name);
                foreach (var d in BuildPreprocessorDefines(
                             sourceAssembly, editorBuild)
                             .OrderBy(s => s, StringComparer.Ordinal))
                    writer.Write(d);
                writer.Write(sourceAssembly.compilerOptions.AllowUnsafeCode);
                writer.Write(sourceAssembly.compilerOptions.ApiCompatibilityLevel.ToString());
                writer.Write(sourceAssembly.compilerOptions.LanguageVersion ?? "");
                foreach (var responseFile in (
                             sourceAssembly.compilerOptions.ResponseFiles
                             ?? Array.Empty<string>())
                         .Where(path => !string.IsNullOrEmpty(path))
                         .Select(NormalizeUnitySourcePath)
                         .OrderBy(path => path, StringComparer.Ordinal))
                {
                    writer.Write(responseFile);
                    writer.Write(File.Exists(responseFile)
                        ? ComputeFileContentHash(responseFile)
                        : "<missing>");
                }
                foreach (var referencePath in GetMetadataReferencePaths(
                             sourceAssembly))
                {
                    writer.Write(referencePath);
                    if (!referenceIdentities.TryGetValue(
                            referencePath, out var identity))
                    {
                        identity = ManagedModuleIdentity.GetIdentity(referencePath);
                        referenceIdentities.Add(referencePath, identity);
                    }
                    writer.Write(identity);
                }
            }
        }
        foreach (var binding in (programAssetBindings ?? Array.Empty<ProgramAssetBinding>())
            .OrderBy(binding => binding.ProgramAssetGuid, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProgramAssetPath, StringComparer.Ordinal))
        {
            writer.Write(binding.ProgramAssetGuid ?? "");
            writer.Write(binding.ProgramAssetPath ?? "");
            writer.Write(binding.ScriptGuid ?? "");
            writer.Write(binding.ScriptPath ?? "");
            writer.Write(binding.NormalizedSourcePath ?? "");
            writer.Write(binding.UnityAssemblyName ?? "");
            writer.Write(binding.SourceClass?.AssemblyQualifiedName ?? "");
            writer.Write(GetObjectIdentity(binding.Asset?.SerializedProgramAsset));
        }
        writer.Flush();
        ms.Position = 0;
        var hash = sha256.ComputeHash(ms);
        return BitConverter.ToString(hash);
    }

    static string ComputeFileContentHash(string path)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(path);
        return BitConverter.ToString(sha256.ComputeHash(stream));
    }

    internal static bool IsUdonSharpBehaviour(INamedTypeSymbol symbol)
    {
        var baseType = symbol.BaseType;
        while (baseType != null)
        {
            if (baseType.Name == "UdonSharpBehaviour") return true;
            baseType = baseType.BaseType;
        }
        return false;
    }

    static IReadOnlyList<(INamedTypeSymbol symbol, SyntaxTree tree)>
        CollectBehaviourDeclarations(
            CSharpCompilation compilation,
            IEnumerable<string> rootSourcePaths)
    {
        var declarations = new List<(INamedTypeSymbol symbol, SyntaxTree tree)>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var roots = new HashSet<string>(
            rootSourcePaths.Select(NormalizeUnitySourcePath),
            StringComparer.OrdinalIgnoreCase);
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!roots.Contains(
                    NormalizeUnitySourcePath(tree.FilePath)))
                continue;
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol
                    || !IsUdonSharpBehaviour(symbol)
                    || !seen.Add(symbol))
                    continue;
                declarations.Add((symbol, tree));
            }
        }
        return declarations;
    }

    internal static List<CompilationUnit> CollectCompilationUnits(
        IEnumerable<string> rootAssemblyNames = null,
        bool editorBuild = true)
    {
        var playerAssemblies = CompilationPipeline.GetAssemblies(
            editorBuild
                ? AssembliesType.Editor
                : AssembliesType.PlayerWithoutTestAssemblies);
        var assemblyByName = playerAssemblies.ToDictionary(
            assembly => assembly.name,
            StringComparer.OrdinalIgnoreCase);
        var assemblyReferences = playerAssemblies.ToDictionary(
            assembly => assembly.name,
            assembly => (IReadOnlyList<string>)assembly.assemblyReferences
                .Select(reference => reference.name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
        var sourceDomain = CollectSourceDomainAssemblyNames();
        var behaviourAssemblyNames = rootAssemblyNames != null
            ? new HashSet<string>(
                rootAssemblyNames.Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rootAssemblyNames == null)
        {
            var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic)
                .GroupBy(assembly => assembly.GetName().Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.OrdinalIgnoreCase);
            foreach (var unityAssembly in playerAssemblies)
            {
                if (!loadedAssemblies.TryGetValue(unityAssembly.name, out var loadedAssembly))
                    continue;
                if (GetLoadableTypes(loadedAssembly).Any(type =>
                    type != null && type != typeof(UdonSharpBehaviour)
                    && typeof(UdonSharpBehaviour).IsAssignableFrom(type)))
                    behaviourAssemblyNames.Add(unityAssembly.name);
            }
        }

        var outsideDomain = behaviourAssemblyNames
            .Where(assemblyByName.ContainsKey)
            .Where(name => !sourceDomain.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (outsideDomain.Length > 0)
            throw new InvalidOperationException(
                "UdonSharpBehaviour source assemblies must belong to the explicit "
                + "USugar source domain. Create a U# Assembly Definition for: "
                + string.Join(", ", outsideDomain));

        return behaviourAssemblyNames
            .Where(assemblyByName.ContainsKey)
            .Select(name => CreateCompilationUnit(
                assemblyByName[name],
                assemblyByName,
                assemblyReferences,
                sourceDomain))
            .Where(unit => unit.SourcePaths.Count > 0)
            .OrderBy(unit => unit.UnityAssembly.name, StringComparer.Ordinal)
            .ToList();
    }

    static HashSet<string> CollectSourceDomainAssemblyNames()
    {
        var names = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "Assembly-CSharp",
        };
        foreach (var guid in AssetDatabase.FindAssets(
                     $"t:{nameof(UdonSharpAssemblyDefinition)}"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var definition =
                AssetDatabase.LoadAssetAtPath<UdonSharpAssemblyDefinition>(
                    path);
            if (definition?.sourceAssembly != null)
                names.Add(definition.sourceAssembly.name);
        }
        return names;
    }

    static CompilationUnit CreateCompilationUnit(
        UnityCompilationAssembly root,
        IReadOnlyDictionary<string, UnityCompilationAssembly>
            assemblyByName,
        IReadOnlyDictionary<string, IReadOnlyList<string>>
            assemblyReferences,
        ISet<string> sourceDomain)
    {
        var sourceAssemblies =
            USugarEditorIntegrationPolicy.SelectSourceAssemblyClosure(
                    root.name, assemblyReferences, sourceDomain)
                .Where(assemblyByName.ContainsKey)
                .Select(name => assemblyByName[name])
                .ToArray();
        var sourceOwners =
            new Dictionary<string, UnityCompilationAssembly>(
                StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in sourceAssemblies)
            foreach (var path in FilterBlacklistedSourcePaths(
                             assembly.sourceFiles)
                         .Where(
                             USugarEditorIntegrationPolicy
                                 .IsCSharpSource)
                         .Select(NormalizeUnitySourcePath)
                         .OrderBy(
                             item => item,
                             StringComparer.Ordinal))
            {
                if (sourceOwners.TryGetValue(
                        path, out var owner)
                    && !string.Equals(
                        owner.name, assembly.name,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Source '{path}' belongs to both "
                        + $"'{owner.name}' and '{assembly.name}'.");
                sourceOwners[path] = assembly;
            }
        return new CompilationUnit(
            root,
            sourceAssemblies,
            sourceOwners);
    }

    static IEnumerable<string> FilterBlacklistedSourcePaths(
        IEnumerable<string> sourcePaths)
    {
        var filtered =
            USugarReflectionTargets.FilterBlacklistedPaths.Invoke(
                null, new object[] { sourcePaths })
            as IEnumerable<string>;
        return filtered
               ?? throw new InvalidOperationException(
                   "UdonSharpSettings.FilterBlacklistedPaths returned no source set.");
    }

    static readonly Dictionary<string, (string sourceText, string defineKey, SyntaxTree tree)> _treeCache = new();

    // Preprocessor symbols for parsing user Udon sources. The intercepted UdonSharp
    // build mode is authoritative: editor/Play compiles keep UNITY_EDITOR*, while
    // client builds remove them. Assembly-specific defines remain intact and the
    // active editor defines supply the UNITY_EDITOR family absent from Player
    // CompilationPipeline assemblies.
    static string[] BuildPreprocessorDefines(
        UnityCompilationAssembly assembly,
        bool editorBuild)
        => USugarEditorIntegrationPolicy
            .SelectPreprocessorDefines(
                assembly.defines,
                EditorUserBuildSettings
                    .activeScriptCompilationDefines,
                editorBuild)
            .ToArray();

    internal static CSharpCompilation BuildCompilation(
        CompilationUnit unit,
        bool editorBuild = true)
    {
        var trees = new SyntaxTree[unit.SourcePaths.Count];
        for (int i = 0; i < unit.SourcePaths.Count; i++)
        {
            var path = unit.SourcePaths[i];
            var owner = unit.SourceOwners[path];
            var defines = BuildPreprocessorDefines(
                owner, editorBuild);
            var defineKey = owner.name + "\n"
                + string.Join("\n", defines.OrderBy(
                    define => define, StringComparer.Ordinal));
            var parseOptions =
                new CSharpParseOptions(LanguageVersion.Latest)
                    .WithPreprocessorSymbols(defines);
            var sourceText = File.ReadAllText(path);
            if (_treeCache.TryGetValue(path, out var cached)
                && cached.defineKey == defineKey
                && string.Equals(cached.sourceText, sourceText, StringComparison.Ordinal))
            {
                trees[i] = cached.tree;
            }
            else
            {
                trees[i] = CSharpSyntaxTree.ParseText(sourceText, parseOptions, path: path);
                _treeCache[path] = (sourceText, defineKey, trees[i]);
            }
        }

        var sourceOutputs = new HashSet<string>(
            unit.SourceAssemblies
                .Select(assembly => assembly.outputPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
        var referencePaths = unit.SourceAssemblies
            .SelectMany(GetMetadataReferencePaths)
            .Where(path => !sourceOutputs.Contains(
                Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var metadataReferences = referencePaths
            .Select(path => MetadataReference.CreateFromFile(path))
            .Cast<MetadataReference>()
            .ToArray();

        return CSharpCompilation.Create(unit.UnityAssembly.name, trees, metadataReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    static void PruneTreeCache(IEnumerable<string> activeSourcePaths)
    {
        var pathSet = new HashSet<string>(activeSourcePaths, StringComparer.OrdinalIgnoreCase);
        foreach (var key in _treeCache.Keys.ToArray())
            if (!pathSet.Contains(key))
                _treeCache.Remove(key);
    }

    static string[] GetMetadataReferencePaths(UnityCompilationAssembly assembly)
        => assembly.compiledAssemblyReferences
            .Concat(assembly.assemblyReferences.Select(reference => reference.outputPath))
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    static IEnumerable<Type> GetLoadableTypes(System.Reflection.Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null);
        }
    }
}
