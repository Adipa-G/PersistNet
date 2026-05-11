using System;
using System.Collections.Generic;

namespace PersistNet.DbAbstraction;

/// <summary>
/// The result of a batched navigation-property load.
/// Maps each parent entity's lookup key to its loaded related value:
/// a single entity (for M2O / O2O) or an <see cref="System.Collections.IList"/>
/// of the related entity type (for O2M / M2M).
/// </summary>
internal sealed class BatchNavResult
{
    /// <summary>Shared empty result — avoids allocations when there is nothing to load.</summary>
    public static readonly BatchNavResult Empty = new()
    {
        Entries = new Dictionary<string, object?>(),
        EntityKeySelector = _ => null
    };

    /// <summary>
    /// Dictionary keyed by the parent entity's lookup key string.
    /// Values are either a single entity (<c>object</c>) or a typed
    /// <c>List&lt;RelatedType&gt;</c> cast to <c>object?</c>.
    /// </summary>
    public required IReadOnlyDictionary<string, object?> Entries { get; init; }

    /// <summary>
    /// Given a parent entity, returns the key to look up in <see cref="Entries"/>.
    /// Returns <c>null</c> when the entity has no valid FK value (e.g. nullable FK not set).
    /// </summary>
    public required Func<object, string?> EntityKeySelector { get; init; }
}
