using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace USugar.Tests;

public static class ExternRegistry
{
    static readonly HashSet<string> ValidExterns = LoadRegistry();

    static HashSet<string> LoadRegistry()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "udon_extern_registry.txt");
        return new HashSet<string>(File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)));
    }

    public static bool IsValid(string externName) => ValidExterns.Contains(externName);
}
