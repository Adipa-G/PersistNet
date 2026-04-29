using System.Threading;
using System.Threading.Tasks;

namespace PersistNet.DbAbstraction;

internal interface IDbSchema
{
    Task<SchemaSnapshot> GetCurrentSchemaAsync(CancellationToken ct = default);

    Task CreateTableAsync(SchemaTable table, CancellationToken ct = default);
    Task DropTableAsync(string tableName, string? tableSchema, CancellationToken ct = default);

    Task AddColumnAsync(string tableName, string? tableSchema, SchemaColumn column, CancellationToken ct = default);
    Task AlterColumnAsync(string tableName, string? tableSchema, SchemaColumn column, CancellationToken ct = default);
    Task DropColumnAsync(string tableName, string? tableSchema, string columnName, CancellationToken ct = default);

    Task CreateIndexAsync(string tableName, string? tableSchema, SchemaIndex index, CancellationToken ct = default);
    Task DropIndexAsync(string tableName, string? tableSchema, string indexName, CancellationToken ct = default);

    Task AddForeignKeyAsync(string tableName, string? tableSchema, SchemaForeignKey foreignKey, CancellationToken ct = default);
    Task DropForeignKeyAsync(string tableName, string? tableSchema, string foreignKeyName, CancellationToken ct = default);
}