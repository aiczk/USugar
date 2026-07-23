using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnityEditor;
using UdonSharp;
using UdonSharp.Compiler;
using VRC.Udon.Editor;
using VRC.SDK3.UdonNetworkCalling;

/// <summary>
/// Orchestrates the 3-phase compile pipeline: serial preparation, parallel emit, serial apply.
/// </summary>
static class USugarCompilationOrchestrator
{
    internal static int RequestedVersion;
    internal static int CompiledVersion;
    internal static bool IsCompiling;
    internal static bool CompileScheduled;
    internal static bool LastCompileHadErrors;

    const string FingerprintKey = "USugar_LastFingerprint";
    const string AppliedKey = "USugar_LastApplied";

    internal struct EmitResult
    {
        public INamedTypeSymbol Symbol;
        public SyntaxTree Tree;
        public string Uasm;
        public List<(string Id, string UdonType, object Value)> Constants;
        public uint HeapSize;
        public IReadOnlyList<EmitDiagnostic> EmitterDiagnostics;
        public List<(string file, int line, int character, string message, string severity)> ErrorDiagnostics;
        public bool IsError;

        public EmitResult(INamedTypeSymbol symbol, SyntaxTree tree, string uasm,
            List<(string Id, string UdonType, object Value)> constants, uint heapSize,
            IReadOnlyList<EmitDiagnostic> diagnostics)
        {
            Symbol = symbol; Tree = tree; Uasm = uasm;
            Constants = constants; HeapSize = heapSize;
            EmitterDiagnostics = diagnostics;
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

    internal static void RequestCompile()
    {
        RequestedVersion++;
        if (IsCompiling || CompileScheduled) return;
        CompileScheduled = true;
        EditorApplication.delayCall += RunCompile;
    }

    internal static void RunCompile()
    {
        CompileScheduled = false;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            CompileScheduled = true;
            EditorApplication.delayCall += RunCompile;
            return;
        }
        IsCompiling = true;
        var versionAtStart = RequestedVersion;
        try
        {
            CompileInternal(applyToAssets: true);
        }
        finally
        {
            CompiledVersion = versionAtStart;
            IsCompiling = false;
        }
        if (RequestedVersion > CompiledVersion && !CompileScheduled)
        {
            CompileScheduled = true;
            EditorApplication.delayCall += RunCompile;
        }
    }

    internal static void CompileInternal(bool applyToAssets, bool force = false)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var collectedDiagnostics = new List<(string file, int line, int character, string message, string severity)>();
        string fingerprint = null;
        var marks = new List<(string label, double ms)>();
        var lastMark = TimeSpan.Zero;
        void Mark(string label)
        {
            var now = sw.Elapsed;
            marks.Add((label, (now - lastMark).TotalMilliseconds));
            lastMark = now;
        }
        var classTimes = new System.Collections.Concurrent.ConcurrentBag<(string name, double ms)>();
        double assembleMs = 0, storeMs = 0, uasmIoMs = 0;

        try
        {
            // ── Phase 1: Serial preparation ──
            var sourcePaths = CollectSourcePaths();
            Mark("collect-sources");
            if (sourcePaths.Count == 0)
            {
                USugarLog.Warn("No UdonSharpBehaviour sources found");
                LastCompileHadErrors = false;
                return;
            }

            fingerprint = ComputeFingerprint(sourcePaths);
            Mark("fingerprint");
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

            var validExterns = new HashSet<string>(
                UdonEditorManager.Instance.GetNodeDefinitions()
                    .Select(d => d.fullName)
                    .Where(n => !string.IsNullOrEmpty(n)));
            // CA-M0 B79: registry-truth for class support — the set of udon type names that carry any extern.
            var externTypePrefixes = new HashSet<string>(validExterns.Select(ExternResolver.ExternTypePrefix));
            var externRegistry = new ExternRegistryFacts(validExterns.Contains, externTypePrefixes.Contains);
            using var externScope = ExternResolver.UseRegistry(externRegistry);
            Mark("extern-set");

            var compilation = BuildCompilation(sourcePaths);
            Mark("build-compilation");

            // A Roslyn Compilation is the unit of correctness. Checking only the representative
            // declaration tree of each behaviour misses errors in helper files and in the other parts
            // of a partial class. Reject the whole run before layout planning or asset mutation.
            var compilationErrors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();
            Mark("get-diagnostics");
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

            Dictionary<string, List<(UdonSharpProgramAsset asset, string scriptPath)>> programAssetLookup = null;
            if (applyToAssets)
            {
                programAssetLookup = new();
                foreach (var guid in AssetDatabase.FindAssets("t:UdonSharpProgramAsset"))
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    var asset = AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(p);
                    if (asset?.sourceCsScript == null) continue;
                    var cn = asset.sourceCsScript.GetClass()?.Name;
                    if (cn == null) continue;
                    if (!programAssetLookup.TryGetValue(cn, out var list))
                    {
                        list = new();
                        programAssetLookup[cn] = list;
                    }
                    list.Add((asset, AssetDatabase.GetAssetPath(asset.sourceCsScript)));
                }
            }
            Mark("program-asset-lookup");

            // Collect all UdonSharpBehaviour classes
            var classList = new List<(INamedTypeSymbol symbol, SyntaxTree tree)>();
            // A partial class has one declaration node per part but ONE symbol — emit it once, not once per part.
            var seenClassSymbols = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var classDecl in tree.GetRoot().DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>())
                {
                    var symbol = model.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
                    if (symbol == null) continue;
                    var isBehaviour = IsUdonSharpBehaviour(symbol);
                    if (!isBehaviour) continue;
                    if (!seenClassSymbols.Add(symbol)) continue;
                    classList.Add((symbol, tree));
                }
            }
            Mark("semantic-models");

            // Pre-plan all layouts (serial, populates cache)
            var planner = new LayoutPlanner(compilation);
            planner.PrepareCompilation();
            Mark("layout-plan");

            // Registry hooks MUST be wired before parallel emit — the class-support (B79) and extern-validity
            // gates both read them off the ambient ExternResolver, and a future embedding that forgets to wire
            // one would silently fall to the permissive arm. Fail loud here instead (armor; unreachable today).
            // ── Phase 2: Parallel emit ──
            var emitResults = new System.Collections.Concurrent.ConcurrentBag<EmitResult>();
            System.Threading.Tasks.Parallel.ForEach(classList, classInfo =>
            {
                var (symbol, tree) = classInfo;
                var classSw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var emitter = new UasmEmitter(compilation, symbol, planner, externRegistry);
                    var uasm = emitter.Emit();
                    // Round-3 item 0: hard per-class extern-validation gate before Phase-3 assembly — a bogus
                    // extern becomes a named USugar diagnostic here instead of an opaque SDK assembler error.
                    ExternResolver.AssertEmittedExternsValid(uasm);
                    emitResults.Add(new EmitResult(symbol, tree, uasm,
                        emitter.CodeGenResult.Constants, emitter.GetHeapSize(), emitter.Diagnostics));
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie
                        && tie.InnerException != null ? tie.InnerException : ex;
                    // Use class name + declaration position for error location
                    var className = symbol.Name;
                    var line = 0;
                    var character = 0;
                    var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
                    if (syntaxRef != null)
                    {
                        var span = syntaxRef.SyntaxTree.GetLineSpan(syntaxRef.Span);
                        line = span.StartLinePosition.Line + 1;
                        character = span.StartLinePosition.Character + 1;
                    }
                    emitResults.Add(EmitResult.Error(symbol, tree, className, line, character,
                        $"Failed to compile {symbol.Name}: {inner.Message}"));
                }
                finally
                {
                    classTimes.Add((symbol.Name, classSw.Elapsed.TotalMilliseconds));
                }
            });
            Mark("emit-wall");

            // ── Phase 3: Serial apply ──
            // OrderBy class name for deterministic output order (ConcurrentBag yields in arbitrary order).
            int count = 0, failures = 0;
            foreach (var result in emitResults.OrderBy(r => r.Symbol.Name).ThenBy(r => r.Symbol.ToDisplayString()))
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

                // UASM output goes to same directory as IR dumps
                var ns = result.Symbol.ContainingNamespace?.IsGlobalNamespace == false
                    ? result.Symbol.ContainingNamespace.ToDisplayString() + "." : "";
                var className = $"{ns}{result.Symbol.Name}";
                var opSw = System.Diagnostics.Stopwatch.StartNew();
                var classDir = Path.Combine("Library", "USugarCache", className);
                Directory.CreateDirectory(classDir);
                File.WriteAllText(Path.Combine(classDir, "uasm.txt"), result.Uasm);
                uasmIoMs += opSw.Elapsed.TotalMilliseconds;

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

                if (applyToAssets)
                {
                    var programAsset = USugarTypeCacheManager.FindProgramAsset(result.Symbol.Name,
                        result.Tree.FilePath, programAssetLookup);
                    if (programAsset == null)
                    {
                        // Emitted a behaviour but found no matching UdonSharpProgramAsset (deleted/renamed asset,
                        // or a class<->asset name+path mismatch). The compiled UASM never reaches an asset — a real
                        // failure, not a no-op. Surface it loudly + count it so the run is NOT marked up-to-date.
                        var miss = $"No UdonSharpProgramAsset found for behaviour '{result.Symbol.Name}'; its compiled program was not applied. Create or relink the program asset, then recompile.";
                        USugarLog.Error($"{result.Tree.FilePath}: {miss}");
                        collectedDiagnostics.Add((result.Tree.FilePath, 0, 0, miss, "Error"));
                        failures++;
                        continue;
                    }

                    opSw.Restart();
                    var program = USugarConstantApplier.AssembleUasm(result.Uasm, result.HeapSize);
                    assembleMs += opSw.Elapsed.TotalMilliseconds;
                    if (program != null)
                    {
                        opSw.Restart();
                        USugarConstantApplier.ApplyConstantValues(program, result.Constants);
                        programAsset.fieldDefinitions = USugarTypeCacheManager.BuildFieldDefinitions(result.Symbol);
                        // [NetworkCallable] entry-point metadata — required for SendCustomNetworkEvent with
                        // parameters (the runtime looks up the event + its param types via this metadata).
                        var netMeta = BuildNetworkCallingMetadata(result.Symbol, planner);
                        if (netMeta.Length > 0)
                        {
                            programAsset.SetNetworkCallingMetadata(netMeta);
                            programAsset.SerializedProgramAsset.StoreProgram(program, netMeta);
                        }
                        else
                            programAsset.SerializedProgramAsset.StoreProgram(program);
                        programAsset.CompiledVersion = UdonSharpProgramVersion.CurrentVersion;
                        var syncMode = USugarCompilerHelper.GetBehaviourSyncMode(result.Symbol);
                        if (syncMode >= 0)
                            programAsset.behaviourSyncMode = (BehaviourSyncMode)syncMode;
                        EditorUtility.SetDirty(programAsset);
                        PushUasmToEditorCache(programAsset, result.Uasm);
                        storeMs += opSw.Elapsed.TotalMilliseconds;
                    }
                    else
                    {
                        var failMsg = $"Failed to assemble UASM for {result.Symbol.Name}";
                        USugarLog.Error(failMsg);
                        collectedDiagnostics.Add((result.Tree.FilePath, 0, 0, failMsg, "Error"));
                        failures++;
                    }
                }
            }

            Mark("apply-loop");
            if (applyToAssets && count > 0)
            {
                InvalidateSerializationCaches();
                AssetDatabase.SaveAssets();
            }
            Mark("save-assets");

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
            var emitSum = classTimes.Sum(c => c.ms);
            var slowest = string.Join(", ", classTimes.OrderByDescending(c => c.ms).Take(10)
                .Select(c => $"{c.name}={c.ms:F0}"));
            USugarLog.Info(
                "Compile breakdown (ms): "
                + string.Join(", ", marks.Select(m => $"{m.label}={m.ms:F0}"))
                + $" | apply detail: uasm-io={uasmIoMs:F0}, assemble={assembleMs:F0}, store={storeMs:F0}"
                + $" | emit cpu-sum={emitSum:F0} over {classTimes.Count} classes, slowest: {slowest}");
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
                USugarReflectionTargets.DiagLine.SetValue(diag, diagnostics[i].line);
                USugarReflectionTargets.DiagCharacter.SetValue(diag, diagnostics[i].character);
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
    static NetworkCallingEntrypointMetadata[] BuildNetworkCallingMetadata(INamedTypeSymbol classSymbol, LayoutPlanner planner)
    {
        var layout = planner.GetLayout(classSymbol);
        if (layout == null) return Array.Empty<NetworkCallingEntrypointMetadata>();
        var list = new List<NetworkCallingEntrypointMetadata>();
        foreach (var member in classSymbol.GetMembers())
        {
            if (!(member is IMethodSymbol method) || !LayoutPlanner.IsNetworkCallable(method)) continue;
            if (!layout.Methods.TryGetValue(method, out var ml)) continue;

            var attrData = method.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "NetworkCallableAttribute");
            int rate = attrData != null && attrData.ConstructorArguments.Length > 0
                       && attrData.ConstructorArguments[0].Value is int r ? r : 0;
            var attr = new NetworkCallableAttribute(rate);

            var pmeta = new NetworkCallingParameterMetadata[method.Parameters.Length];
            for (int i = 0; i < method.Parameters.Length; i++)
                pmeta[i] = new NetworkCallingParameterMetadata(
                    ml.ParamIds[i], USugarTypeCacheManager.ResolveClrType(method.Parameters[i].Type));

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

    // ── Serialization cache invalidation ──

    static void InvalidateSerializationCaches()
    {
        ClearStaticDictionary(USugarReflectionTargets.VarStorageType, "_variableTypeLookup");

        // Fail loud if the OdinSerializer formatter-cache bindings broke (SDK rename). Without clearing them, a
        // freshly-compiled program can be (de)serialized by a STALE formatter — a silent runtime-data hazard, far
        // worse than a missing inspector feature — so surface it instead of no-op'ing quietly.
        if (USugarReflectionTargets.FormattersField == null || USugarReflectionTargets.EmittedFormatterOpenType == null)
        {
            USugarLog.Error("Serialization-cache bindings (_formatters / EmittedFormatter) did not resolve — the OdinSerializer formatter cache could not be cleared after compile; serialized program data may be stale. The UdonSharp SDK may have changed; check USugarReflectionTargets.");
            return;
        }

        if (USugarReflectionTargets.FormattersField.GetValue(null) is System.Collections.IDictionary formatters)
        {
            foreach (var key in formatters.Keys.Cast<Type>().ToArray())
            {
                try
                {
                    var closed = USugarReflectionTargets.EmittedFormatterOpenType.MakeGenericType(key);
                    var manager = closed.GetNestedType("UdonSharpBehaviourFormatterManager",
                        BindingFlags.NonPublic);
                    ClearStaticDictionary(manager, "_heapDataLookup");
                }
                catch { }
            }
            formatters.Clear();
        }
    }

    static void ClearStaticDictionary(Type type, string fieldName)
    {
        if (type == null) return;
        var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        if (field?.GetValue(null) is System.Collections.IDictionary dict)
            dict.Clear();
    }

    // ── Helpers ──

    static string ComputeFingerprint(List<string> sourcePaths)
    {
        using var md5 = MD5.Create();
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms);
        // Sort (ordinal, locale-independent) so a permuted-but-identical source set hashes the same —
        // AssetDatabase.FindAssets ordering is not contractually stable, and an unsorted hash would otherwise
        // produce a false cache miss (redundant recompile).
        foreach (var p in sourcePaths.OrderBy(s => s, StringComparer.Ordinal))
        {
            writer.Write(p);
            // Hash file CONTENT, not last-write-time. A git checkout / branch-switch / external tool can change a
            // file's TEXT while preserving (or rolling back) its mtime; an mtime fingerprint misses that and skips
            // a needed recompile, stranding a stale asset. Content is the only sound change signal. (SessionState
            // survives domain reloads, so no on-disk cache is needed; an Editor restart correctly recompiles.)
            try { writer.Write(File.ReadAllText(p)); }
            catch { writer.Write(File.GetLastWriteTimeUtc(p).Ticks); } // unreadable file → fall back to mtime
        }
        // Fold the active preprocessor defines in: a platform/SDK switch changes which #if branches compile
        // without touching any source file, and a content-only hash would skip the needed recompile, shipping a
        // program built against stale defines. Sorted so a benign reorder is not a false cache miss.
        foreach (var d in BuildPreprocessorDefines().OrderBy(s => s, StringComparer.Ordinal))
            writer.Write(d);
        writer.Flush();
        ms.Position = 0;
        var hash = md5.ComputeHash(ms);
        return BitConverter.ToString(hash);
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

    internal static List<string> CollectSourcePaths()
    {
        var paths = new List<string>();
        foreach (var guid in AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".cs")) continue;
            if (path.Contains("/Editor/") || path.Contains("/Editor~/")
                || path.Contains("/Tests/") || path.Contains("/Tests~/")) continue;
            paths.Add(path);
        }
        return paths;
    }

    static MetadataReference[] _cachedMetadataRefs;
    static readonly Dictionary<string, (string sourceText, string defineKey, SyntaxTree tree)> _treeCache = new();

    // Preprocessor symbols for parsing user Udon sources. Mirrors stock UdonSharp's GetProjectDefines with
    // editorBuild:false — the compiled Udon program runs IN-GAME, so honor the project's platform/SDK/custom
    // scripting defines but drop UNITY_EDITOR* (editor-only branches must not leak into the shipped program).
    // USugar's own markers are always defined. Hardcoding only the two markers (the prior behavior) silently
    // compiled the WRONG #if branch for any platform/SDK/custom-symbol guard the user wrote.
    static string[] BuildPreprocessorDefines()
    {
        var defines = new List<string>();
        foreach (var d in UnityEditor.EditorUserBuildSettings.activeScriptCompilationDefines)
            if (!d.StartsWith("UNITY_EDITOR"))
                defines.Add(d);
        defines.Add("COMPILER_UDONSHARP");
        defines.Add("UDONSHARP");
        return defines.ToArray();
    }

    internal static CSharpCompilation BuildCompilation(List<string> sourcePaths)
    {
        var defines = BuildPreprocessorDefines();
        var defineKey = string.Join("\n", defines.OrderBy(d => d, StringComparer.Ordinal));
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest)
            .WithPreprocessorSymbols(defines);

        var trees = new SyntaxTree[sourcePaths.Count];
        for (int i = 0; i < sourcePaths.Count; i++)
        {
            var path = sourcePaths[i];
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

        var pathSet = new HashSet<string>(sourcePaths);
        foreach (var key in _treeCache.Keys.ToArray())
            if (!pathSet.Contains(key))
                _treeCache.Remove(key);

        _cachedMetadataRefs ??= AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Where(a => !a.GetName().Name.StartsWith("Assembly-CSharp"))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToArray();

        return CSharpCompilation.Create("USugarCompilation", trees, _cachedMetadataRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
