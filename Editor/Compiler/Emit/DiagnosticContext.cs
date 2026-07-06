using System.Collections.Generic;

/// <summary>
/// Owns diagnostics collected during a single class emission.
/// </summary>
public sealed class DiagnosticContext
{
    public readonly List<EmitDiagnostic> Diagnostics = new();

    public readonly HashSet<string> ReportedExterns = new();
}
