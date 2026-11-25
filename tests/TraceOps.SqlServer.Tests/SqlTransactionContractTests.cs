using System.Data;
using Xunit;

namespace TraceOps.SqlServer.Tests;

public sealed class SqlTransactionContractTests
{
    [Theory, InlineData("Shipment_List"), InlineData("Shipment_Find"), Trait("Acceptance", "SQL-02")]
    public async Task Read_storage_fault_rethrows_without_committing_or_explicitly_rolling_back_caller_transaction(string procedure)
    {
        await using var f = await LiveSqlFixture.Create();
        await using (var admin = await f.OpenAdmin())
            await LiveSqlFixture.Execute(admin, $"DELETE FROM {f.Q}.SchemaVersion");
        await using var c = await f.OpenRuntime();
        using var tx = c.BeginTransaction(IsolationLevel.Serializable);
        using var command = f.Procedure(c, procedure, tx);
        if (procedure == "Shipment_Find") LiveSqlFixture.Wide(command, "@TrackingNumber", null);
        Assert.Equal(51013, await LiveSqlFixture.SqlError(async () =>
        {
            using var reader = await command.ExecuteReaderAsync();
            do { while (await reader.ReadAsync()) { } } while (await reader.NextResultAsync());
        }));
        using var count = LiveSqlFixture.Command(c, "SELECT @@TRANCOUNT", tx);
        Assert.Equal(1, Convert.ToInt32(await count.ExecuteScalarAsync()));
        tx.Rollback();
    }
    [Theory, InlineData("Shipment_List"), InlineData("Shipment_Find"), Trait("Acceptance", "SQL-02")]
    public async Task Standalone_storage_fault_leaves_no_open_transaction(string procedure)
    {
        await using var f = await LiveSqlFixture.Create();
        await using (var admin = await f.OpenAdmin())
            await LiveSqlFixture.Execute(admin, $"DELETE FROM {f.Q}.SchemaVersion");
        await using var c = await f.OpenRuntime();
        using var command = f.Procedure(c, procedure);
        if (procedure == "Shipment_Find") LiveSqlFixture.Wide(command, "@TrackingNumber", null);
        var error = await LiveSqlFixture.SqlError(async () =>
        {
            using var reader = await command.ExecuteReaderAsync();
            do { while (await reader.ReadAsync()) { } } while (await reader.NextResultAsync());
        });
        var remainingTransactions = Convert.ToInt32(await LiveSqlFixture.Scalar(c, "SELECT @@TRANCOUNT"));
        Assert.Equal(51013, error);
        Assert.Equal(0, remainingTransactions);
    }
    [Fact, Trait("Acceptance", "SQL-02")]
    public async Task Append_refuses_non_serializable_caller_transaction_without_writes()
    {
        await using var f = await LiveSqlFixture.Create();
        await f.Seed("A");
        await using var c = await f.OpenRuntime();
        using var tx = c.BeginTransaction(IsolationLevel.ReadCommitted);
        using var command = f.AppendCommand(c, tx, LiveSqlFixture.Event("A"));
        var error = await LiveSqlFixture.SqlError(async () => { await command.ExecuteNonQueryAsync(); });
        Assert.NotNull(error); // The spec requires SERIALIZABLE but assigns no separate error number here.
        try { tx.Rollback(); } catch (InvalidOperationException) { }
        Assert.Equal(0, await f.EventCount("A"));
    }
}
