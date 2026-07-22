using System.Text;
using Microsoft.CodeAnalysis;

/// <summary>Per-Udon-program storage identity for source static fields.</summary>
public static class StaticOwnerAbi
{
    public static bool IsSourceStatic(IFieldSymbol field)
        => field is { IsStatic: true, HasConstantValue: false }
           && field.DeclaringSyntaxReferences.Length > 0
           && !USugarCompilerHelper.IsFrameworkNamespace(field.ContainingNamespace);

    public static string FieldName(IFieldSymbol field)
        => FieldName(field, field.ContainingType);

    public static string FieldName(IFieldSymbol field, INamedTypeSymbol owner)
        => "__static_" + Sanitize(ClassTypeObjectContext.SpecKey(owner))
           + "_" + Sanitize(field.MetadataName);

    public static string PropertyName(IPropertySymbol property, INamedTypeSymbol owner)
        => "__static_" + Sanitize(ClassTypeObjectContext.SpecKey(owner))
           + "_prop_" + Sanitize(property.MetadataName);

    static string Sanitize(string value)
    {
        var b = new StringBuilder(value.Length);
        foreach (var ch in value)
            b.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        return b.ToString();
    }
}
