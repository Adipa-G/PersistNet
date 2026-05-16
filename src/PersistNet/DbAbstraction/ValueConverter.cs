using System;

namespace PersistNet.DbAbstraction;

/// <summary>
/// Shared type-coercion helper used by both entity and DTO materializers.
/// </summary>
internal static class ValueConverter
{
    /// <summary>
    /// Converts a raw database value to the CLR <paramref name="targetType"/>.
    /// Returns <c>null</c> for null inputs. Uses <see cref="Convert.ChangeType"/> as
    /// a fallback when the value is not directly assignable.
    /// </summary>
    internal static object? Convert(object? dbValue, Type targetType)
    {
        if (dbValue is null) return null;
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying.IsAssignableFrom(dbValue.GetType())) return dbValue;
        return System.Convert.ChangeType(dbValue, underlying);
    }
}
