using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

/// <summary>
/// Converts a Roslyn constant payload to the CLR type required by an assembled Udon heap slot.
/// SDK value types such as UnityEngine.LayerMask expose their constant semantics through
/// user-defined conversions rather than IConvertible, so Convert.ChangeType alone is incomplete.
/// </summary>
public static class HeapConstantConverter
{
    public static object ConvertTo(object value, Type destinationType)
    {
        if (destinationType == null)
            throw new ArgumentNullException(nameof(destinationType));
        if (value == null)
        {
            if (!destinationType.IsValueType || Nullable.GetUnderlyingType(destinationType) != null)
                return null;
            throw new InvalidCastException(
                $"Null cannot initialize heap value type '{destinationType.FullName}'.");
        }
        if (destinationType.IsInstanceOfType(value))
            return value;

        var nullableUnderlying = Nullable.GetUnderlyingType(destinationType);
        if (nullableUnderlying != null)
            return ConvertTo(value, nullableUnderlying);
        if (destinationType.IsEnum)
            return Enum.ToObject(destinationType, value);

        var sourceType = value.GetType();
        var conversion = FindUserConversion(sourceType, destinationType, value);
        if (conversion != null)
            return conversion.Invoke(null, new[] { value });

        return Convert.ChangeType(value, destinationType, CultureInfo.InvariantCulture);
    }

    static MethodInfo FindUserConversion(Type sourceType, Type destinationType, object value)
    {
        var owners = new List<Type> { destinationType };
        if (sourceType != destinationType)
            owners.Add(sourceType);

        return owners
            .SelectMany(owner => owner.GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            .Where(method =>
                method.IsSpecialName
                && (method.Name == "op_Implicit" || method.Name == "op_Explicit")
                && method.GetParameters().Length == 1
                && destinationType.IsAssignableFrom(method.ReturnType)
                && method.GetParameters()[0].ParameterType.IsInstanceOfType(value))
            .OrderBy(method => method.Name == "op_Implicit" ? 0 : 1)
            .ThenBy(method => method.DeclaringType?.FullName, StringComparer.Ordinal)
            .ThenBy(method => method.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
