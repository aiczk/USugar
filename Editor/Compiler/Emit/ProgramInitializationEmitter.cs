using System;
using System.Collections.Generic;

/// <summary>
/// Owns the per-program construction barrier.
///
/// A Udon program has no CLR constructor hook.  Running instance field initializers only from
/// <c>_start</c> is observably too late: another behaviour can call an exported method before the
/// receiver's Start event.  Every externally reachable entry therefore crosses this one-shot barrier.
/// The initialized flag is set before the body so an initializer that re-enters the program cannot
/// recursively initialize it a second time.
/// </summary>
internal sealed class ProgramInitializationEmitter
{
    internal const string FunctionName = "__usugar_initialize";
    internal const string StateField = "__usugar_initialized";

    readonly LoweringState _context;
    readonly CoreBuilder _builder;

    public ProgramInitializationEmitter(LoweringState context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _builder = context.Builder;
    }

    public void Emit(Action initializerBody)
    {
        if (initializerBody == null) throw new ArgumentNullException(nameof(initializerBody));

        _context.Storage.DeclareGeneratedField(StateField, StorageTypes.Boolean, false);
        var previousFunction = _builder.CurrentFunction;
        var function = _context.Module.AddFunction(FunctionName);
        _builder.SetFunction(function);
        try
        {
            var initialized = _builder.LoadField(StateField, StorageTypes.Boolean);
            var needsInitialization = _builder.ExternCall(
                UdonAbi.BooleanNot,
                new List<CLeaf> { initialized },
                StorageTypes.Boolean);
            _builder.EmitIf(needsInitialization, _ =>
            {
                // Mark first.  Initializers may bind delegates or call user code that re-enters an
                // export; that entry must observe construction-in-progress as already guarded.
                _builder.EmitStoreField(
                    StateField,
                    _builder.Const(true, StorageTypes.Boolean));
                initializerBody();
            });
            _builder.EmitReturn();
        }
        finally
        {
            if (previousFunction != null)
                _builder.SetFunction(previousFunction);
        }
    }

    public void GuardEveryExport()
    {
        foreach (var function in _context.Module.Functions)
        {
            if (function.ExportName == null || function.Name == FunctionName)
                continue;
            function.Body.Stmts.Insert(0, new CExprStmt(new CInternalCall(
                FunctionName,
                new List<CLeaf>(),
                StorageTypes.Void)));
        }
    }
}
