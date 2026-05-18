using Microsoft.Data.SqlClient;

namespace PersistNet.Perf;

public static class SchemaHelper
{
    private const string MasterConnStr =
        "Data Source=(localdb)\\MSSQLLocalDB;Integrated Security=SSPI;Initial Catalog=master";

    public static string CreateDatabase(string dbName)
    {
        using var conn = new SqlConnection(MasterConnStr);
        conn.Open();
        Execute(conn, $"CREATE DATABASE [{dbName}]");
        return $"Data Source=(localdb)\\MSSQLLocalDB;Integrated Security=SSPI;Initial Catalog={dbName}";
    }

    public static void CreateSchema(SqlConnection conn)
    {
        // Products: explicit int PK (no IDENTITY) — seeded via raw SQL before benchmarks.
        Execute(conn, """
            CREATE TABLE perf_product (
                ItemId        INT           NOT NULL PRIMARY KEY,
                Name          NVARCHAR(200) NOT NULL,
                Category      NVARCHAR(100) NOT NULL,
                UnitPrice     DECIMAL(18,4) NOT NULL,
                BulkUnitPrice DECIMAL(18,4) NULL
            )
            """);

        // Orders / items / charges: IDENTITY PKs for realistic ORM insert testing.
        Execute(conn, """
            CREATE TABLE perf_order (
                OrderId   INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                Name      NVARCHAR(200)     NOT NULL,
                OrderDate DATETIME2         NOT NULL
            )
            """);

        Execute(conn, """
            CREATE TABLE perf_order_item (
                ItemLineId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                OrderId    INT               NOT NULL REFERENCES perf_order(OrderId),
                IndexNo    INT               NOT NULL,
                ItemId     INT               NOT NULL REFERENCES perf_product(ItemId),
                Quantity   INT               NOT NULL
            )
            """);

        Execute(conn, """
            CREATE TABLE perf_order_charge (
                ChargeId    INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                ItemLineId  INT               NOT NULL REFERENCES perf_order_item(ItemLineId),
                ChargeIndex INT               NOT NULL,
                ChargeValue DECIMAL(18,4)     NOT NULL
            )
            """);
    }

    public static async Task SeedProductsAsync(SqlConnection conn, IReadOnlyList<SeedProduct> products)
    {
        await using var txn = (SqlTransaction)await conn.BeginTransactionAsync();
        foreach (var p in products)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = txn;
            cmd.CommandText =
                "INSERT INTO perf_product(ItemId,Name,Category,UnitPrice,BulkUnitPrice) " +
                "VALUES(@Id,@N,@Cat,@Up,@Bp)";
            cmd.Parameters.AddWithValue("@Id",  p.ItemId);
            cmd.Parameters.AddWithValue("@N",   p.Name);
            cmd.Parameters.AddWithValue("@Cat", p.Category);
            cmd.Parameters.AddWithValue("@Up",  p.UnitPrice);
            cmd.Parameters.AddWithValue("@Bp",  (object?)p.BulkUnitPrice ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        await txn.CommitAsync();
    }

    public static void DropDatabase(string dbName)
    {
        using var conn = new SqlConnection(MasterConnStr);
        conn.Open();
        Execute(conn, $"ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
        Execute(conn, $"DROP DATABASE [{dbName}]");
    }

    private static void Execute(SqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
