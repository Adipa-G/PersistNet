using System;
using System.Collections.Generic;

namespace PersistNet.DbInfo;

internal sealed class SubType
{
    public Type EntityType { get; }
    public object DiscriminatorValue { get; }
    public IReadOnlyList<Column> ExtraColumns { get; }

    internal SubType(Type entityType, object discriminatorValue, IReadOnlyList<Column> extraColumns)
    {
        EntityType = entityType;
        DiscriminatorValue = discriminatorValue;
        ExtraColumns = extraColumns;
    }
}
