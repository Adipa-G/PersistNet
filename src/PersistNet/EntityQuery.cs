using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace PersistNet;

internal sealed class EntityQuery<T> : IEntityQuery<T> where T : class
{
    private readonly Transaction _transaction;
    private readonly object[] _keyValues;
    private readonly List<string> _includes = new();
    private bool _loadAll;
    private Task<T>? _task;

    internal EntityQuery(Transaction transaction, object[] keyValues)
    {
        _transaction = transaction;
        _keyValues = keyValues;
    }

    public IEntityQuery<T> Include(Expression<Func<T, object?>> navigation)
    {
        _includes.Add(GetPropertyName(navigation));
        return this;
    }

    public IEntityQuery<T> IncludeAll()
    {
        _loadAll = true;
        return this;
    }

    public TaskAwaiter<T> GetAwaiter() => AsTask().GetAwaiter();

    public Task<T> AsTask() => _task ??= ExecuteCoreAsync();

    private async Task<T> ExecuteCoreAsync()
    {
        var entity = await _transaction.LoadEntityCoreAsync<T>(_keyValues);

        IReadOnlyList<string> effectiveIncludes = _loadAll
            ? _transaction.GetAllRelationshipNames(typeof(T))
            : _includes;

        if (effectiveIncludes.Count > 0)
            await _transaction.LoadEntityGraphAsync(entity, effectiveIncludes, new HashSet<string>());

        return entity;
    }

    private static string GetPropertyName(Expression<Func<T, object?>> expression)
    {
        var body = expression.Body;
        // Strip the boxing cast that C# adds for value types / when return type is object?
        if (body is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
            body = unary.Operand;
        if (body is MemberExpression member)
            return member.Member.Name;
        throw new ArgumentException(
            $"Expression must be a simple property accessor (e.g. x => x.Property). Got: {expression.Body}",
            nameof(expression));
    }
}
