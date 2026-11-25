using System.Data;
using Xunit;

namespace TraceOps.SqlServer.Tests;

public sealed class SqlReadFidelityTests
{
    [Theory, MemberData(nameof(StoredProcedureContractTests.InvalidTracking), MemberType = typeof(StoredProcedureContractTests)), Trait("Acceptance", "SQL-02"), Trait("Acceptance", "SQL-05")]
    public async Task Invalid_Find_raw_key_returns_two_empty_sets_without_matching_valid_prefix_or_best_fit(string? key)
    {
        await using var f = await LiveSqlFixture.Create();
        await f.Seed("A");
        await f.Seed(new string('A', 32));
        await using var c = await f.OpenRuntime();
        using var command = f.Procedure(c, "Shipment_Find");
        LiveSqlFixture.Wide(command, "@TrackingNumber", key);
        using var reader = await command.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync());
        Assert.True(await reader.NextResultAsync());
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
    }
    [Theory, MemberData(nameof(StoredProcedureContractTests.InvalidTracking), MemberType = typeof(StoredProcedureContractTests)), Trait("Acceptance", "SQL-02"), Trait("Acceptance", "SQL-05")]
    public async Task Append_validates_original_wide_tracking_before_cast_and_does_not_write(string? key)
    {
        await using var f = await LiveSqlFixture.Create();
        await f.Seed("A");
        await f.Seed(new string('A', 32));
        await using var c = await f.OpenRuntime();
        using var tx = c.BeginTransaction(IsolationLevel.Serializable);
        using var command = f.AppendCommand(c, tx, LiveSqlFixture.Event("A"));
        command.Parameters["@TrackingNumber"].Value = (object?)key ?? DBNull.Value;
        Assert.Equal(51010, await LiveSqlFixture.SqlError(async () => { await command.ExecuteNonQueryAsync(); }));
        try { tx.Rollback(); } catch (InvalidOperationException) { }
        Assert.Equal(0, await f.EventCount("A"));
        Assert.Equal(0, await f.EventCount(new string('A', 32)));
    }
    [Theory, InlineData("Shipment_List"), InlineData("Shipment_Find"), Trait("Acceptance", "SQL-02"), Trait("Acceptance", "SQL-03")]
    public async Task Populated_read_sets_preserve_ordinal_order_projection_and_history_precision(string procedure)
    {
        await using var f = await LiveSqlFixture.Create();
        foreach (var tracking in new[] { "Z", "A-2", "A-10" }) await f.Seed(tracking);
        var id1 = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var id2 = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var at = LiveSqlFixture.Time.AddTicks(1);
        await using (var admin = await f.OpenAdmin())
        {
            await LiveSqlFixture.Execute(admin, $"INSERT {f.Q}.ShipmentEvents(TrackingNumber,EventSequence,EventId,Status,OccurredAt,SourceSystem) VALUES('A-2',1,@id1,1,@at,'SRC'),('A-2',2,@id2,2,@at,'src')",
                ("@id1", id1), ("@id2", id2), ("@at", at));
            await LiveSqlFixture.Execute(admin, $"UPDATE {f.Q}.Shipments SET Version=2,Status=2,LastOccurredAt=@at WHERE TrackingNumber='A-2'", ("@at", at));
        }
        await using var c = await f.OpenRuntime();
        using var command = f.Procedure(c, procedure);
        if (procedure == "Shipment_Find") LiveSqlFixture.Wide(command, "@TrackingNumber", "A-2");
        using var reader = await command.ExecuteReaderAsync();
        var parents = new List<string>();
        while (await reader.ReadAsync())
        {
            parents.Add(reader.GetString(0));
            Assert.True(reader.GetFieldValue<DateTimeOffset>(1).EqualsExact(LiveSqlFixture.Time));
            if (reader.GetString(0) == "A-2")
            {
                Assert.Equal(2, reader.GetByte(2));
                Assert.True(reader.GetFieldValue<DateTimeOffset>(3).EqualsExact(at));
                Assert.Equal(2, reader.GetInt64(4));
            }
        }
        Assert.Equal(procedure == "Shipment_List" ? new[] { "A-10", "A-2", "Z" } : new[] { "A-2" }, parents);
        Assert.True(await reader.NextResultAsync());
        for (var i = 0; i < 2; i++)
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("A-2", reader.GetString(0));
            Assert.Equal(i + 1, reader.GetInt64(1));
            Assert.Equal(i == 0 ? id1 : id2, reader.GetGuid(2));
            Assert.Equal(i + 1, reader.GetByte(3));
            Assert.True(reader.GetFieldValue<DateTimeOffset>(4).EqualsExact(at));
            Assert.Equal(i == 0 ? "SRC" : "src", reader.GetString(5));
        }
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
    }
}
