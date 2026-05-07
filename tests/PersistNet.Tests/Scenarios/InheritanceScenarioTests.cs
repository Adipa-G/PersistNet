using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace PersistNet.Tests.Scenarios;

/// <summary>
/// Scenario: Single-Table Inheritance (STI) hierarchy.
///
/// Schema (one table for all vehicle types):
///   sc_inh_vehicles (Id, Make, VehicleType TEXT discriminator,
///                    Doors INTEGER nullable,   -- Car only
///                    Payload REAL nullable)    -- Truck only
///
/// Subtypes: InhCar ("car"), InhTruck ("truck")
/// </summary>
public sealed class InheritanceScenarioTests : ScenarioTestBase
{
    // ── Entity model ────────────────────────────────────────────────────────

    [TableInfo(TableName = "sc_inh_vehicles")]
    [SubTypeInfo(typeof(InhCar),   "car")]
    [SubTypeInfo(typeof(InhTruck), "truck")]
    private class InhVehicle
    {
        [ColumnInfo(Key = true, AutoIncrement = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Make { get; set; } = "";

        [ColumnInfo(IsDiscriminator = true)]
        public string VehicleType { get; set; } = "";
    }

    private class InhCar : InhVehicle
    {
        [ColumnInfo]
        public int Doors { get; set; }
    }

    private class InhTruck : InhVehicle
    {
        [ColumnInfo]
        public double Payload { get; set; }
    }

    // ── DDL ─────────────────────────────────────────────────────────────────

    private async Task CreateTableAsync()
    {
        await ExecAsync(
            "CREATE TABLE sc_inh_vehicles (" +
            "Id INTEGER NOT NULL PRIMARY KEY, " +
            "Make TEXT NOT NULL, " +
            "VehicleType TEXT NOT NULL, " +
            "Doors INTEGER, " +      // nullable — only Cars have this
            "Payload REAL)");        // nullable — only Trucks have this
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Saving a Car: row must use discriminator "car" and have Doors set.
    /// </summary>
    [Fact]
    public async Task Save_Car_DiscriminatorIsCarAndDoorsSet()
    {
        await CreateTableAsync();

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Save(new InhCar { Make = "Toyota", Doors = 4 }); // Id = 0 → INSERT
        await txn.CommitAsync();

        Assert.Equal(1L, await CountAsync("sc_inh_vehicles"));
        Assert.Equal("car", await ScalarAsync(
            "SELECT VehicleType FROM sc_inh_vehicles LIMIT 1"));
        Assert.Equal(4L, Convert.ToInt64(await ScalarAsync(
            "SELECT Doors FROM sc_inh_vehicles LIMIT 1")));
        Assert.Null(await ScalarAsync(
            "SELECT Payload FROM sc_inh_vehicles LIMIT 1"));
    }

    /// <summary>
    /// Saving a Truck: row must use discriminator "truck" and have Payload set.
    /// </summary>
    [Fact]
    public async Task Save_Truck_DiscriminatorIsTruckAndPayloadSet()
    {
        await CreateTableAsync();

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Save(new InhTruck { Make = "Ford", Payload = 5.5 }); // Id = 0 → INSERT
        await txn.CommitAsync();

        Assert.Equal(1L, await CountAsync("sc_inh_vehicles"));
        Assert.Equal("truck", await ScalarAsync(
            "SELECT VehicleType FROM sc_inh_vehicles LIMIT 1"));
        Assert.Equal(5.5, await ScalarAsync(
            "SELECT Payload FROM sc_inh_vehicles LIMIT 1"));
        Assert.Null(await ScalarAsync(
            "SELECT Doors FROM sc_inh_vehicles LIMIT 1"));
    }

    /// <summary>
    /// GetAsync with the concrete subtype returns extra columns correctly.
    /// </summary>
    [Fact]
    public async Task GetAsync_Car_LoadsDoorsColumn()
    {
        await CreateTableAsync();
        await ExecAsync("INSERT INTO sc_inh_vehicles VALUES (1, 'Honda', 'car', 2, NULL)");

        await using var txn = await Factory.OpenTransactionAsync();
        var car = await txn.GetAsync<InhCar>(1);

        Assert.Equal(1,       car.Id);
        Assert.Equal("Honda", car.Make);
        Assert.Equal("car",   car.VehicleType);
        Assert.Equal(2,       car.Doors);
    }

    /// <summary>
    /// GetAsync with the concrete subtype returns extra columns correctly.
    /// </summary>
    [Fact]
    public async Task GetAsync_Truck_LoadsPayloadColumn()
    {
        await CreateTableAsync();
        await ExecAsync("INSERT INTO sc_inh_vehicles VALUES (1, 'Volvo', 'truck', NULL, 12.0)");

        await using var txn = await Factory.OpenTransactionAsync();
        var truck = await txn.GetAsync<InhTruck>(1);

        Assert.Equal(1,       truck.Id);
        Assert.Equal("Volvo", truck.Make);
        Assert.Equal("truck", truck.VehicleType);
        Assert.Equal(12.0,    truck.Payload);
    }

    /// <summary>
    /// Saving a mix of Car and Truck instances in one commit.
    /// Both rows must appear in the same table with correct discriminators.
    /// </summary>
    [Fact]
    public async Task Save_MixedVehicleTypes_CorrectDiscriminatorsPerRow()
    {
        await CreateTableAsync();

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Save(new InhCar   { Make = "BMW",  Doors = 4 });    // Id = 0 → INSERT → auto-id 1
        txn.Save(new InhTruck { Make = "MAN",  Payload = 20.0 }); // Id = 0 → INSERT → auto-id 2
        txn.Save(new InhCar   { Make = "Audi", Doors = 2 });    // Id = 0 → INSERT → auto-id 3
        await txn.CommitAsync();

        Assert.Equal(3L, await CountAsync("sc_inh_vehicles"));
        Assert.Equal(2L, Convert.ToInt64(await ScalarAsync(
            "SELECT COUNT(*) FROM sc_inh_vehicles WHERE VehicleType = 'car'")));
        Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(
            "SELECT COUNT(*) FROM sc_inh_vehicles WHERE VehicleType = 'truck'")));
    }

    /// <summary>
    /// GetAsync + dirty-track a Car: only changed field (Doors) is updated.
    /// Make remains unchanged in the database.
    /// </summary>
    [Fact]
    public async Task GetAsync_Car_ChangeDoorsOnly_MakeUnchangedInDb()
    {
        await CreateTableAsync();
        await ExecAsync("INSERT INTO sc_inh_vehicles VALUES (1, 'Kia', 'car', 5, NULL)");

        await using var txn = await Factory.OpenTransactionAsync();
        var car = await txn.GetAsync<InhCar>(1);
        car.Doors = 3; // change only the subtype extra column
        txn.Save(car);
        await txn.CommitAsync();

        Assert.Equal(3L, Convert.ToInt64(
            await ScalarAsync("SELECT Doors FROM sc_inh_vehicles WHERE Id = 1")));
        Assert.Equal("Kia",
            await ScalarAsync("SELECT Make FROM sc_inh_vehicles WHERE Id = 1"));
    }
}
