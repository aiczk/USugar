using System;

/// <summary>
/// Compatibility of one materialized Udon heap operand with an SDK extern port.
///
/// This is intentionally independent from <see cref="RawCopyCompatibility"/>.
/// A COPY moves one heap value between compiler-owned slots. An extern wrapper
/// reads or writes a slot through the CLR type declared by UdonNodeDefinition.
/// They currently share several representation relaxations, but neither policy
/// is allowed to inherit future relaxations from the other by accident.
/// </summary>
public static class ExternOperandCompatibility
{
    /// <summary>
    /// Null when the operand is compatible; otherwise a diagnostic reason.
    ///
    /// Reference strongboxes remain representation-compatible here because
    /// UdonHeap.Get/SetHeapVariable performs the runtime value check rather
    /// than requiring the heap variable's declared type to equal the wrapper
    /// port type. Semantic binding must still prove that the selected extern
    /// belongs to the receiver/member; this predicate never selects an extern.
    /// </summary>
    public static string WhyIncompatible(
        string expected,
        string actual,
        UdonAbiParameterMode mode,
        UdonTypeFactRegistry facts)
    {
        if (facts == null) throw new ArgumentNullException(nameof(facts));
        if (expected == actual) return null;

        // Udon wrappers re-box values through System.Object in both directions.
        if (expected == "SystemObject" || actual == "SystemObject") return null;

        if (IsNullableErasure(expected, actual)
            || IsNullableErasure(actual, expected))
            return null;

        var expectedReference = facts.IsReferenceFact(expected);
        var actualReference = facts.IsReferenceFact(actual);
        if (expectedReference == true && actualReference == true)
            return null;
        if (expectedReference == null) return NoFact(expected);
        if (actualReference == null) return NoFact(actual);

        return $"extern {mode} operand facts deny every declared adaptation "
               + $"({Describe(expected, expectedReference)}; "
               + $"{Describe(actual, actualReference)})";
    }

    static bool IsNullableErasure(string boxed, string bare)
        => boxed.StartsWith("SystemNullable", StringComparison.Ordinal)
           && boxed.Substring("SystemNullable".Length) == bare;

    static string NoFact(string name)
        => $"no extern operand fact recorded for '{name}' "
           + "(neither source type lowering nor the installed SDK ABI snapshot "
           + "classified it)";

    static string Describe(string name, bool? isReference)
        => $"'{name}' is a fact "
           + (isReference == true ? "reference" : "value type");
}
