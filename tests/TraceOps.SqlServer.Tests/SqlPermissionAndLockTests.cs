using System.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace TraceOps.SqlServer.Tests;

public sealed class SqlPermissionAndLockTests
{
    [Fact, Trait("Acceptance", "SQL-02")]
    public async Task Runtime_has_procedure_execute_but_cannot_read_or_modify_any_table()
    {
        await using var f = await LiveSqlFixture.Create();
        await using var c = await f.OpenRuntime();
        foreach (var procedure in new[] { "Shipment_List", "Shipment_Find", "Shipment_TryRegister", "Shipment_AppendEvent" })
            Assert.Equal(1, Convert.ToInt32(await LiveSqlFixture.Scalar(c, "SELECT HAS_PERMS_BY_NAME(@object,'OBJECT','EXECUTE')", ("@object", f.Schema + "." + procedure))));
        foreach (var table in new[] { "Shipments", "ShipmentEvents", "SchemaVersion" })
        {
            foreach (var permission in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "ALTER", "CONTROL" })
                Assert.Equal(0, Convert.ToInt32(await LiveSqlFixture.Scalar(c,
                    "SELECT HAS_PERMS_BY_NAME(@object,'OBJECT',@permission)", ("@object", f.Schema + "." + table), ("@permission", permission))));
            using var select = LiveSqlFixture.Command(c, $"SELECT * FROM {f.Q}.[{table}]");
            Assert.Equal(229, await LiveSqlFixture.SqlError(async () => { using var r = await select.ExecuteReaderAsync(); }));
        }
        await f.Seed("PERM-A");
        await f.Seed("PERM-B");
        await using (var admin = await f.OpenAdmin())
        {
            await LiveSqlFixture.Execute(admin, $"INSERT {f.Q}.ShipmentEvents(TrackingNumber,EventSequence,EventId,Status,OccurredAt,SourceSystem) VALUES('PERM-A',1,@id,1,@at,'SRC')",
                ("@id", Guid.NewGuid()), ("@at", LiveSqlFixture.Time.AddTicks(1)));
            await LiveSqlFixture.Execute(admin, $"UPDATE {f.Q}.Shipments SET Version=1,Status=1,LastOccurredAt=@at WHERE TrackingNumber='PERM-A'", ("@at", LiveSqlFixture.Time.AddTicks(1)));
        }
        var statements = new[] {
            ("Shipments", "INSERT", $"INSERT {f.Q}.Shipments(TrackingNumber,RegisteredAt,Status,LastOccurredAt,Version) VALUES('PERM-C',@at,0,@at,0)"),
            ("Shipments", "UPDATE", $"UPDATE {f.Q}.Shipments SET RegisteredAt=RegisteredAt WHERE TrackingNumber='PERM-A'"),
            ("Shipments", "DELETE", $"DELETE FROM {f.Q}.Shipments WHERE TrackingNumber='PERM-B'"),
            ("ShipmentEvents", "INSERT", $"INSERT {f.Q}.ShipmentEvents(TrackingNumber,EventSequence,EventId,Status,OccurredAt,SourceSystem) VALUES('PERM-B',1,@id,1,@at,'SRC')"),
            ("ShipmentEvents", "UPDATE", $"UPDATE {f.Q}.ShipmentEvents SET SourceSystem='SRC' WHERE TrackingNumber='PERM-A' AND EventSequence=1"),
            ("ShipmentEvents", "DELETE", $"DELETE FROM {f.Q}.ShipmentEvents WHERE TrackingNumber='PERM-A' AND EventSequence=1"),
            ("SchemaVersion", "INSERT", $"INSERT {f.Q}.SchemaVersion(Version,MigrationId) VALUES(1,'M4.1')"),
            ("SchemaVersion", "UPDATE", $"UPDATE {f.Q}.SchemaVersion SET MigrationId='M4.1' WHERE Version=1"),
            ("SchemaVersion", "DELETE", $"DELETE FROM {f.Q}.SchemaVersion WHERE Version=1") };
        var attempts = new List<(string Table, string Operation, int? Error, string Before, string After)>();
        foreach (var (table, operation, sql) in statements)
        {
            // Make ledger INSERT valid, not a duplicate-key test disguised as a permission test.
            var emptyLedger = table == "SchemaVersion" && operation == "INSERT";
            if (emptyLedger)
            {
                await using var admin = await f.OpenAdmin();
                await LiveSqlFixture.Execute(admin, $"DELETE FROM {f.Q}.SchemaVersion");
            }
            var before = await f.Snapshot();
            int? error;
            using (var command = LiveSqlFixture.Command(c, sql))
            {
                LiveSqlFixture.Timestamp(command, "@at", LiveSqlFixture.Time);
                command.Parameters.AddWithValue("@id", Guid.NewGuid());
                error = await LiveSqlFixture.SqlError(async () => { await command.ExecuteNonQueryAsync(); });
            }
            var after = await f.Snapshot();
            attempts.Add((table, operation, error, before, after));
            if (emptyLedger)
            {
                await using var admin = await f.OpenAdmin();
                // The attempted INSERT may have unexpectedly succeeded; its captured diff still fails below.
                await LiveSqlFixture.Execute(admin, $"DELETE FROM {f.Q}.SchemaVersion; INSERT {f.Q}.SchemaVersion(Version,MigrationId) VALUES(1,'M4.1')");
            }
        }
        f.RecordProbe("runtime-dml-matrix", new { attempts = attempts.Select(x => new { table = x.Table, operation = x.Operation, error = x.Error, unchanged = x.Before == x.After }) });
        Assert.Equal(9, attempts.Count);
        Assert.All(attempts, attempt =>
        {
            Assert.Equal(229, attempt.Error);
            Assert.Equal(attempt.Before, attempt.After);
        });
        using var list = f.Procedure(c, "Shipment_List");
        using var reader = await list.ExecuteReaderAsync();
        do { while (await reader.ReadAsync()) { } } while (await reader.NextResultAsync());
    }
    [Theory, InlineData("Shipment_Find"), InlineData("Shipment_List"), Trait("Acceptance", "SQL-02")]
    public async Task Read_procedures_own_only_standalone_transactions_and_leave_caller_transaction_open(string procedure)
    {
        await using var f = await LiveSqlFixture.Create();
        await f.Seed("A");
        await using var c = await f.OpenRuntime();
        using (var standalone = f.Procedure(c, procedure))
        {
            if (procedure == "Shipment_Find") LiveSqlFixture.Wide(standalone, "@TrackingNumber", "A");
            using var reader = await standalone.ExecuteReaderAsync();
            do { while (await reader.ReadAsync()) { } } while (await reader.NextResultAsync());
        }
        Assert.Equal(0, Convert.ToInt32(await LiveSqlFixture.Scalar(c, "SELECT @@TRANCOUNT")));
        using var tx = c.BeginTransaction(IsolationLevel.Serializable);
        using (var caller = f.Procedure(c, procedure, tx))
        {
            if (procedure == "Shipment_Find") LiveSqlFixture.Wide(caller, "@TrackingNumber", "A");
            using var reader = await caller.ExecuteReaderAsync();
            do { while (await reader.ReadAsync()) { } } while (await reader.NextResultAsync());
        }
        using var count = LiveSqlFixture.Command(c, "SELECT @@TRANCOUNT", tx);
        Assert.Equal(1, Convert.ToInt32(await count.ExecuteScalarAsync()));
        tx.Rollback();
    }
    [Theory, InlineData(false), InlineData(true), Trait("Acceptance", "SQL-06")]
    public async Task Find_holds_parent_or_missing_key_range_until_caller_rolls_back(bool missing)
    {
        await using var f = await LiveSqlFixture.Create();
        if (!missing) await f.Seed("LOCK-A");
        await using var first = await f.OpenRuntime();
        using var tx = first.BeginTransaction(IsolationLevel.Serializable);
        using (var find = f.Procedure(first, "Shipment_Find", tx))
        {
            LiveSqlFixture.Wide(find, "@TrackingNumber", "LOCK-A");
            using var reader = await find.ExecuteReaderAsync();
            do { while (await reader.ReadAsync()) { } } while (await reader.NextResultAsync());
        }
        await using var second = await f.OpenAdmin();
        using var writer = LiveSqlFixture.Command(second, missing
            ? $"SET LOCK_TIMEOUT 1000; INSERT {f.Q}.Shipments(TrackingNumber,RegisteredAt,Status,LastOccurredAt,Version) VALUES('LOCK-A',@at,0,@at,0)"
            : $"SET LOCK_TIMEOUT 1000; UPDATE {f.Q}.Shipments SET LastOccurredAt=LastOccurredAt WHERE TrackingNumber='LOCK-A'");
        if (missing) LiveSqlFixture.Timestamp(writer, "@at", LiveSqlFixture.Time);
        Assert.Equal(1222, await LiveSqlFixture.SqlError(async () => { await writer.ExecuteNonQueryAsync(); }));
        tx.Rollback();
        Assert.Equal(1, await writer.ExecuteNonQueryAsync());
    }
    [Theory, InlineData("Shipment_List"), InlineData("Shipment_Find"), Trait("Acceptance", "SQL-02"), Trait("Acceptance", "SQL-03")]
    public async Task Standalone_read_keeps_parent_locked_while_server_history_select_is_blocked(string procedure)
    {
        await using var f = await LiveSqlFixture.Create();
        await f.Seed("LOCK-A");
        var eventId = Guid.NewGuid();
        await using (var seed = await f.OpenAdmin())
        {
            await LiveSqlFixture.Execute(seed, $"INSERT {f.Q}.ShipmentEvents(TrackingNumber,EventSequence,EventId,Status,OccurredAt,SourceSystem) VALUES('LOCK-A',1,@id,1,@at,'SRC')",
                ("@id", eventId), ("@at", LiveSqlFixture.Time.AddTicks(1)));
            await LiveSqlFixture.Execute(seed, $"UPDATE {f.Q}.Shipments SET Version=1,Status=1,LastOccurredAt=@at WHERE TrackingNumber='LOCK-A'", ("@at", LiveSqlFixture.Time.AddTicks(1)));
        }
        await using var historyBlocker = await f.OpenAdmin();
        using var historyTransaction = historyBlocker.BeginTransaction(IsolationLevel.Serializable);
        using (var hold = LiveSqlFixture.Command(historyBlocker,
            $"SELECT EventId FROM {f.Q}.ShipmentEvents WITH(XLOCK,HOLDLOCK,ROWLOCK) WHERE TrackingNumber='LOCK-A' AND EventSequence=1", historyTransaction))
        {
            if (!Equals(eventId, await hold.ExecuteScalarAsync()))
                throw new SqlFixturePrerequisiteException("Owned history blocker baseline was not found.");
        }
        var blockerSpid = historyBlocker.ServerProcessId;
        var applicationSuffix = "standalone." + procedure;
        await using var runtime = await f.OpenRuntime(applicationSuffix);
        await using var observer = await f.OpenAdmin();
        using var readCommand = f.Procedure(runtime, procedure); // Deliberately no caller transaction.
        if (procedure == "Shipment_Find") LiveSqlFixture.Wide(readCommand, "@TrackingNumber", "LOCK-A");
        async Task<Exception?> ConsumeOnServer()
        {
            try
            {
                using var reader = await readCommand.ExecuteReaderAsync();
                do { while (await reader.ReadAsync()) { } } while (await reader.NextResultAsync());
                return null;
            }
            catch (Exception error) { return error; }
        }
        var read = ConsumeOnServer();
        SqlObservationProbes.PendingRead? pending = null;
        int? parentWriterError = null;
        try
        {
            pending = await f.HistoryWait(observer, f.ApplicationPrefix + applicationSuffix,
                runtime.ServerProcessId, blockerSpid, read);
            if (pending is not null)
            {
                using var contender = LiveSqlFixture.Command(observer,
                    $"SET LOCK_TIMEOUT 1000; UPDATE {f.Q}.Shipments SET Version=Version WHERE TrackingNumber='LOCK-A'");
                parentWriterError = await LiveSqlFixture.SqlError(async () => { await contender.ExecuteNonQueryAsync(); });
            }
        }
        finally
        {
            historyTransaction.Rollback();
            // Release server-side blocking before awaiting completion, even if the observation failed.
            await read.WaitAsync(TimeSpan.FromSeconds(15));
        }
        using var after = LiveSqlFixture.Command(observer,
            $"SET LOCK_TIMEOUT 1000; UPDATE {f.Q}.Shipments SET Version=Version WHERE TrackingNumber='LOCK-A'");
        var writtenAfterCompletion = await after.ExecuteNonQueryAsync();
        var remainingTransactions = Convert.ToInt32(await LiveSqlFixture.Scalar(runtime, "SELECT @@TRANCOUNT"));
        var readError = await read;
        f.RecordProbe("standalone-server-read", new { procedure, pending, parentWriterError, writtenAfterCompletion,
            remainingTransactions, readErrorType = readError?.GetType().Name });
        // Buffering/client NextResult timing is irrelevant: the observer must see the server waiting on owned history.
        Assert.NotNull(pending);
        Assert.True(pending.HistoryWaiting);
        Assert.True(pending.Transactions > 0);
        Assert.Equal(4, pending.Isolation);
        Assert.Equal(blockerSpid, pending.Blocker);
        Assert.Equal(1222, parentWriterError);
        Assert.Null(readError);
        Assert.Equal(0, remainingTransactions);
        Assert.Equal(1, writtenAfterCompletion);
    }
    [Fact, Trait("Acceptance", "SQL-06")]
    public async Task Direct_Append_takes_parent_lock_before_history_insert()
    {
        await using var f = await LiveSqlFixture.Create();
        await f.Seed("LOCK-A");
        await using var owner = await f.OpenAdmin();
        using var ownerTx = owner.BeginTransaction(IsolationLevel.Serializable);
        using (var command = LiveSqlFixture.Command(owner, $"SELECT TrackingNumber FROM {f.Q}.Shipments WITH(UPDLOCK,HOLDLOCK) WHERE TrackingNumber='LOCK-A'", ownerTx))
            await command.ExecuteScalarAsync();
        await using var runtime = await f.OpenRuntime();
        using var tx = runtime.BeginTransaction(IsolationLevel.Serializable);
        using (var timeout = LiveSqlFixture.Command(runtime, "SET LOCK_TIMEOUT 1000", tx)) await timeout.ExecuteNonQueryAsync();
        using var append = f.AppendCommand(runtime, tx, LiveSqlFixture.Event("LOCK-A"));
        Assert.Equal(1222, await LiveSqlFixture.SqlError(async () => { await append.ExecuteNonQueryAsync(); }));
        try { tx.Rollback(); } catch (InvalidOperationException) { }
        ownerTx.Rollback();
        Assert.Equal(0, await f.EventCount("LOCK-A"));
    }
}
