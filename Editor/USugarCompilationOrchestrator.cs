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

        try
        {
            // ── Phase 1: Serial preparation ──
            var sourcePaths = CollectSourcePaths();
            if (sourcePaths.Count == 0)
            {
                USugarLog.Warn("No UdonSharpBehaviour sources found");
                return;
            }

            fingerprint = ComputeFingerprint(sourcePaths);
            var lastFp = SessionState.GetString(FingerprintKey, "");
            var lastApplied = SessionState.GetBool(AppliedKey, false);
            if (!force && fingerprint == lastFp && (!applyToAssets || lastApplied))
                return;

            var validExterns = new HashSet<string>(
                UdonEditorManager.Instance.GetNodeDefinitions()
                    .Select(d => d.fullName)
                    .Where(n => !string.IsNullOrEmpty(n)));
            ExternResolver.IsExternValid = validExterns.Contains;

            var compilation = BuildCompilation(sourcePaths);

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

            // Collect all UdonSharpBehaviour classes
            var classList = new List<(INamedTypeSymbol symbol, SemanticModel model, SyntaxTree tree)>();
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var classDecl in tree.GetRoot().DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>())
                {
                    var symbol = model.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
                    if (symbol == null || !IsUdonSharpBehaviour(symbol)) continue;
                    classList.Add((symbol, model, tree));
                }
            }

            // Pre-plan all layouts (serial, populates cache)
            var planner = new LayoutPlanner(compilation);
            foreach (var (symbol, _, _) in classList)
            {
                planner.Plan(symbol);
                foreach (var iface in symbol.AllInterfaces)
                    planner.Plan(iface);
            }
            planner.Freeze();

            // Pre-compute diagnostics per tree (serial — avoids Roslyn lock contention)
            var treeDiagnostics = new Dictionary<SyntaxTree, Diagnostic[]>();
            foreach (var tree in classList.Select(c => c.tree).Distinct())
            {
                var model = compilation.GetSemanticModel(tree);
                treeDiagnostics[tree] = model.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            }

            // ── Phase 2: Parallel emit ──
            var emitResults = new System.Collections.Concurrent.ConcurrentBag<EmitResult>();
            System.Threading.Tasks.Parallel.ForEach(classList, classInfo =>
            {
                var (symbol, model, tree) = classInfo;
                try
                {
                    var treeErrors = treeDiagnostics[tree];
                    if (treeErrors.Length > 0)
                    {
                        foreach (var diag in treeErrors.Take(3))
                        {
                            var loc = diag.Location.GetLineSpan();
                            emitResults.Add(EmitResult.Error(symbol, tree,
                                loc.Path ?? "", loc.StartLinePosition.Line + 1,
                                loc.StartLinePosition.Character + 1, diag.GetMessage()));
                        }
                        return;
                    }

                    var emitter = new UasmEmitter(compilation, symbol, planner);
                    var uasm = emitter.Emit();
                    emitResults.Add(new EmitResult(symbol, tree, uasm,
                        emitter.CodeGenResult.Constants, emitter.GetHeapSize(), emitter.Diagnostics));
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie
                        && tie.InnerException != null ? tie.InnerException : ex;
                    emitResults.Add(EmitResult.Error(symbol, tree, "", 0, 0,
                        $"Failed to compile {symbol.Name}: {inner.Message}\n{inner.StackTrace}"));
                }
            });

            // ── Phase 3: Serial apply ──
            int count = 0, failures = 0;
            var outputDir = "Library/USugarCache/UASM";
            Directory.CreateDirectory(outputDir);

            foreach (var result in emitResults)
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

                var ns = result.Symbol.ContainingNamespace?.IsGlobalNamespace == false
                    ? result.Symbol.ContainingNamespace.ToDisplayString() + "." : "";
                var outputPath = Path.Combine(outputDir, $"{ns}{result.Symbol.Name}.uasm");
                File.WriteAllText(outputPath, result.Uasm);

                // Merge emitter diagnostics
                foreach (var d in result.EmitterDiagnostics)
                {
                    collectedDiagnostics.Add((d.FilePath, d.Line, d.Character, d.Message, d.Severity));
                    if (d.Severity == "Warning")
                        USugarLog.Warn($"{d.FilePath}({d.Line},{d.Character}): {d.Message}");
                }

                if (applyToAssets)
                {
                    var programAsset = USugarTypeCacheManager.FindProgramAsset(result.Symbol.Name,
                        result.Tree.FilePath, programAssetLookup);
                    if (programAsset == null) 
                        continue;
                    
                    var program = USugarConstantApplier.AssembleUasm(result.Uasm, result.HeapSize);
                    if (program != null)
                    {
                        USugarConstantApplier.ApplyConstantValues(program, result.Constants);
                        programAsset.fieldDefinitions = USugarTypeCacheManager.BuildFieldDefinitions(result.Symbol);
                        programAsset.SerializedProgramAsset.StoreProgram(program);
                        programAsset.CompiledVersion = UdonSharpProgramVersion.CurrentVersion;
                        var syncMode = USugarCompilerHelper.GetBehaviourSyncMode(result.Symbol);
                        if (syncMode >= 0)
                            programAsset.behaviourSyncMode = (BehaviourSyncMode)syncMode;
                        EditorUtility.SetDirty(programAsset);
                        PushUasmToEditorCache(programAsset, result.Uasm);
                    }
                    else
                    {
                        USugarLog.Error($"Failed to assemble UASM for {result.Symbol.Name}");
                        failures++;
                    }
                }
            }

            if (applyToAssets && count > 0)
            {
                InvalidateSerializationCaches();
                AssetDatabase.SaveAssets();
            }

            sw.Stop();
            SessionState.SetString(FingerprintKey, fingerprint);
            SessionState.SetBool(AppliedKey, applyToAssets || lastApplied);
            LastCompileHadErrors = failures > 0;
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

        if (USugarReflectionTargets.FormattersField?.GetValue(null) is System.Collections.IDictionary formatters)
        {
            if (USugarReflectionTargets.EmittedFormatterOpenType != null)
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
        foreach (var p in sourcePaths)
        {
            writer.Write(p);
            writer.Write(File.GetLastWriteTimeUtc(p).Ticks);
        }
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
    static readonly Dictionary<string, (long ticks, SyntaxTree tree)> _treeCache = new();

    internal static CSharpCompilation BuildCompilation(List<string> sourcePaths)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest)
            .WithPreprocessorSymbols("COMPILER_UDONSHARP", "UDONSHARP");

        var trees = new SyntaxTree[sourcePaths.Count];
        for (int i = 0; i < sourcePaths.Count; i++)
        {
            var path = sourcePaths[i];
            var ticks = File.GetLastWriteTimeUtc(path).Ticks;
            if (_treeCache.TryGetValue(path, out var cached) && cached.ticks == ticks)
            {
                trees[i] = cached.tree;
            }
            else
            {
                trees[i] = CSharpSyntaxTree.ParseText(
                    File.ReadAllText(path), parseOptions, path: path);
                _treeCache[path] = (ticks, trees[i]);
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
