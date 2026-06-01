using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace USugar.Tests;

/// <summary>
/// Normalizes benign internal-scratch renumbering so two UASM texts that differ ONLY in
/// __intnl_&lt;Type&gt;_&lt;idx&gt; numeric indices (with the SAME first-appearance order)
/// compare equal, while real regressions (reordering, type change, different externs) stay
/// different. The numeric index of coalesced scratch (LirToUasm.cs:213) is an implementation
/// detail; only first-appearance order is semantically meaningful. The fixed return-jump
/// sentinel is preserved verbatim.
/// </summary>
public static class UasmCanonicalizer
{
    // __intnl_<body>_<idx> where <body> may itself contain underscores (e.g. dlgptr_SystemUInt32).
    static readonly Regex IntnlVar =
        new(@"__intnl_(?<body>.+?)_(?<idx>\d+)\b", RegexOptions.Compiled);

    const string ReturnJump = "__intnl_returnJump_SystemUInt32_0";

    public static string Canonicalize(string uasm)
    {
        var perBody = new Dictionary<string, int>();   // body -> next canonical seq
        var map = new Dictionary<string, string>();     // full var -> canonical name

        return IntnlVar.Replace(uasm, m =>
        {
            var full = m.Value;
            if (full == ReturnJump) return full;        // fixed sentinel, never renumber
            if (!map.TryGetValue(full, out var canon))
            {
                var body = m.Groups["body"].Value;      // type preserved -> type change still differs
                perBody.TryGetValue(body, out var seq);
                perBody[body] = seq + 1;
                canon = $"__intnl_{body}_c{seq}";        // index normalized to appearance order
                map[full] = canon;
            }
            return canon;
        });
    }
}
