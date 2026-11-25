// M4 SQL-06 concurrency oracle amendment v2 candidate; frozen v1 remains externally archived.
using System.Diagnostics;
using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using TraceOps.Domain;
using Xunit;

namespace TraceOps.SqlServer.Tests;

public sealed class SqlConcurrencyTests
{
    private sealed record AdapterRequest(int Worker, int Spid, Guid ConnectionId, string ApplicationName,
        int RequestId, string Status, string? WaitType, int BlockingSessionId);
    private sealed record SnapshotColumn(string Name, string Type);
    private sealed record SnapshotTable(string Name, SnapshotColumn[] Columns, string?[][] Rows);

    private static SnapshotTable[] ParseSnapshot(string snapshot)
    {
        using var document = JsonDocument.Parse(snapshot);
        return document.RootElement.EnumerateArray().Select(table => new SnapshotTable(
            table.GetProperty("table").GetString()!,
            table.GetProperty("columns").EnumerateArray().Select(column => new SnapshotColumn(
                column.GetProperty("name").GetString()!, column.GetProperty("type").GetString()!)).ToArray(),
            table.GetProperty("rows").EnumerateArray().Select(row => row.EnumerateArray().Select(value =>
                value.ValueKind == JsonValueKind.Null ? null : value.GetString()).ToArray()).ToArray())).ToArray();
    }

    private static int ColumnIndex(SnapshotTable table, string name) =>
        Array.FindIndex(table.Columns, column => column.Name == name) is var index && index >= 0
            ? index
            : throw new Xunit.Sdk.XunitException($"Snapshot column missing: {table.Name}.{name}.");

    private static void AssertColumns(SnapshotTable table, params (string Name, string Type)[] expected) =>
        Assert.Equal(expected, table.Columns.Select(column => (column.Name, column.Type)).ToArray());

    private static void AssertRowEqual(string?[] expected, string?[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++) Assert.Equal(expected[index], actual[index]);
    }

    private static bool RowsEqual(string?[] left, string?[] right) => left.SequenceEqual(right, StringComparer.Ordinal);

    private static void AssertSnapshotDelta(string beforeJson, string afterJson, LifecycleEvent appliedEvent)
    {
        var before = ParseSnapshot(beforeJson);
        var after = ParseSnapshot(afterJson);
        var expectedTables = new[] { "SchemaVersion", "Shipments", "ShipmentEvents" };
        Assert.Equal(expectedTables, before.Select(table => table.Name).ToArray());
        Assert.Equal(expectedTables, after.Select(table => table.Name).ToArray());

        var beforeLedger = before[0];
        var afterLedger = after[0];
        AssertColumns(beforeLedger, ("Version", "int"), ("MigrationId", "varchar"));
        AssertColumns(afterLedger, ("Version", "int"), ("MigrationId", "varchar"));
        AssertRowEqual(new string?[] { "1", "M4.1" }, Assert.Single(beforeLedger.Rows));
        AssertRowEqual(Assert.Single(beforeLedger.Rows), Assert.Single(afterLedger.Rows));

        var beforeShipments = before[1];
        var afterShipments = after[1];
        AssertColumns(beforeShipments, ("TrackingNumber", "varchar"), ("RegisteredAt", "datetimeoffset"),
            ("Status", "tinyint"), ("LastOccurredAt", "datetimeoffset"), ("Version", "bigint"));
        AssertColumns(afterShipments, ("TrackingNumber", "varchar"), ("RegisteredAt", "datetimeoffset"),
            ("Status", "tinyint"), ("LastOccurredAt", "datetimeoffset"), ("Version", "bigint"));
        var trackingIndex = ColumnIndex(beforeShipments, "TrackingNumber");
        var beforeByTracking = beforeShipments.Rows.ToDictionary(row => row[trackingIndex]!, StringComparer.Ordinal);
        var afterByTracking = afterShipments.Rows.ToDictionary(row => row[trackingIndex]!, StringComparer.Ordinal);
        Assert.Equal(beforeByTracking.Keys.Order(StringComparer.Ordinal), afterByTracking.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("RACE", beforeByTracking.Keys);
        Assert.Contains("RACE", afterByTracking.Keys);
        var statusIndex = ColumnIndex(beforeShipments, "Status");
        var occurredAtIndex = ColumnIndex(beforeShipments, "LastOccurredAt");
        var versionIndex = ColumnIndex(beforeShipments, "Version");
        var allowedProjectionChanges = new HashSet<int> { statusIndex, occurredAtIndex, versionIndex };
        foreach (var (tracking, oldRow) in beforeByTracking)
        {
            var newRow = afterByTracking[tracking];
            if (!string.Equals(tracking, appliedEvent.TrackingNumber, StringComparison.Ordinal))
            {
                AssertRowEqual(oldRow, newRow);
                continue;
            }

            Assert.Equal(oldRow.Length, newRow.Length);
            for (var column = 0; column < oldRow.Length; column++)
                if (!allowedProjectionChanges.Contains(column)) Assert.Equal(oldRow[column], newRow[column]);
            Assert.Equal(((int)appliedEvent.Status).ToString(CultureInfo.InvariantCulture), newRow[statusIndex]);
            Assert.Equal(appliedEvent.OccurredAt.ToString("O", CultureInfo.InvariantCulture), newRow[occurredAtIndex]);
            Assert.Equal(long.Parse(oldRow[versionIndex]!, CultureInfo.InvariantCulture) + 1,
                long.Parse(newRow[versionIndex]!, CultureInfo.InvariantCulture));
        }

        var beforeEvents = before[2];
        var afterEvents = after[2];
        AssertColumns(beforeEvents, ("TrackingNumber", "varchar"), ("EventSequence", "bigint"),
            ("EventId", "uniqueidentifier"), ("Status", "tinyint"), ("OccurredAt", "datetimeoffset"),
            ("SourceSystem", "varchar"));
        AssertColumns(afterEvents, ("TrackingNumber", "varchar"), ("EventSequence", "bigint"),
            ("EventId", "uniqueidentifier"), ("Status", "tinyint"), ("OccurredAt", "datetimeoffset"),
            ("SourceSystem", "varchar"));
        Assert.DoesNotContain(beforeEvents.Rows, row => string.Equals(row[ColumnIndex(beforeEvents, "TrackingNumber")],
            appliedEvent.TrackingNumber, StringComparison.Ordinal));
        Assert.Equal(beforeEvents.Rows.Length + 1, afterEvents.Rows.Length);
        foreach (var oldEvent in beforeEvents.Rows)
            Assert.Contains(afterEvents.Rows, newEvent => RowsEqual(oldEvent, newEvent));
        var addedEvents = afterEvents.Rows.Where(newEvent => !beforeEvents.Rows.Any(oldEvent => RowsEqual(oldEvent, newEvent))).ToArray();
        var addedEvent = Assert.Single(addedEvents);
        var beforeEventRows = beforeEvents.Rows.Select(row => JsonSerializer.Serialize(row))
            .Order(StringComparer.Ordinal).ToArray();
        var afterPriorEventRows = afterEvents.Rows.Where(row => !RowsEqual(row, addedEvent))
            .Select(row => JsonSerializer.Serialize(row)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(beforeEventRows, afterPriorEventRows);
        var expectedSequence = long.Parse(beforeByTracking[appliedEvent.TrackingNumber][versionIndex]!, CultureInfo.InvariantCulture) + 1;
        AssertRowEqual(new string?[]
        {
            appliedEvent.TrackingNumber,
            expectedSequence.ToString(CultureInfo.InvariantCulture),
            appliedEvent.EventId.ToString("D"),
            ((int)appliedEvent.Status).ToString(CultureInfo.InvariantCulture),
            appliedEvent.OccurredAt.ToString("O", CultureInfo.InvariantCulture),
            appliedEvent.SourceSystem
        }, addedEvent);
    }

    internal static async Task<T[]> Race<T>(Func<int, T> action)
    {
        using var barrier = new Barrier(16);
        var tasks = Enumerable.Range(0, 16).Select(worker => Task.Factory.StartNew(() =>
        {
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(20))) throw new TimeoutException("16 worker start barrier failed.");
            return action(worker);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static async Task<AdapterRequest[]> ObserveAdapterRequests(LiveSqlFixture fixture, SqlConnection observer,
        int blockerSpid, Task workers)
    {
        var elapsed = Stopwatch.StartNew();
        AdapterRequest[] requests;
        do
        {
            using var command = LiveSqlFixture.Command(observer, """
                SELECT s.session_id,c.connection_id,s.program_name,r.request_id,r.status,r.wait_type,r.blocking_session_id
                FROM sys.dm_exec_sessions AS s
                JOIN sys.dm_exec_requests AS r ON r.session_id=s.session_id
                JOIN sys.dm_exec_connections AS c ON c.connection_id=r.connection_id
                WHERE s.database_id=DB_ID() AND r.database_id=DB_ID()
                  AND LEFT(s.program_name,LEN(@prefix))=@prefix
                ORDER BY s.program_name,s.session_id
                """);
            command.Parameters.AddWithValue("@prefix", fixture.ApplicationPrefix);
            var observed = new List<AdapterRequest>();
            try
            {
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var applicationName = reader.GetString(2);
                    var worker = int.TryParse(applicationName.AsSpan(fixture.ApplicationPrefix.Length),
                        NumberStyles.None, CultureInfo.InvariantCulture, out var parsedWorker) ? parsedWorker : -1;
                    observed.Add(new(worker, Convert.ToInt32(reader.GetValue(0)), reader.GetGuid(1), applicationName,
                        Convert.ToInt32(reader.GetValue(3)), reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5), Convert.ToInt32(reader.GetValue(6))));
                }
            }
            catch (SqlException error)
            {
                throw new SqlFixturePrerequisiteException($"Owned-session observer failed (SQL {error.Number}); values redacted.");
            }

            requests = observed.ToArray();
            if (requests.Select(request => request.Worker).Distinct().Count() == 16 || workers.IsCompleted) break;
            await Task.Delay(25);
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(20));

        fixture.RecordProbe("adapter-race-in-flight", new
        {
            expectedWorkers = 16,
            observedRequests = requests,
            serverObservedSessions = requests.Select(request => request.Spid).Distinct().Count(),
            inFlightRequests = requests.Length,
            workersCompleted = workers.IsCompleted,
            elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds,
            directBlockerRequests = requests.Count(request => request.BlockingSessionId == blockerSpid)
        });
        return requests;
    }

    [Fact, Trait("Acceptance", "SQL-02"), Trait("Acceptance", "SQL-06")]
    public async Task Sixteen_open_physical_connections_race_the_real_registration_procedure()
    {
        await using var f = await LiveSqlFixture.Create();
        var connections = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => f.OpenRuntime()));
        try
        {
            Assert.Equal(16, connections.Select(c => c.ClientConnectionId).Distinct().Count());
            var result = await Race(worker =>
            {
                using var command = f.Procedure(connections[worker], "Shipment_TryRegister");
                LiveSqlFixture.Wide(command, "@TrackingNumber", "SP-RACE");
                LiveSqlFixture.Timestamp(command, "@RegisteredAt", LiveSqlFixture.Time);
                command.Parameters.Add("@ResultCode", SqlDbType.VarChar, 32).Direction = ParameterDirection.Output;
                command.ExecuteNonQuery();
                return command.Parameters["@ResultCode"].Value as string;
            });
            Assert.Equal(1, result.Count(x => x == "REGISTERED"));
            Assert.Equal(15, result.Count(x => x == "TRACKING_NUMBER_CONFLICT"));
            await using var admin = await f.OpenAdmin();
            Assert.Equal(1, Convert.ToInt32(await LiveSqlFixture.Scalar(admin, $"SELECT COUNT(*) FROM {f.Q}.Shipments WHERE TrackingNumber='SP-RACE'")));
        }
        finally
        {
            foreach (var connection in connections) await connection.DisposeAsync();
        }
    }
    [Fact, Trait("Acceptance", "SQL-02"), Trait("Acceptance", "SQL-06")]
    public async Task Sixteen_independent_registration_connections_have_exactly_one_winner()
    {
        await using var f = await LiveSqlFixture.Create();
        var result = await Race(worker => f.Store(worker).TryAdd(ShipmentState.Register("RACE", LiveSqlFixture.Time)));
        Assert.Equal(1, result.Count(x => x));
        Assert.Equal(15, result.Count(x => !x));
        Assert.Equal(0, Assert.IsType<ShipmentState>(f.Store().Find("RACE")).Version);
    }
    [Theory, InlineData(false), InlineData(true), Trait("Acceptance", "SQL-06")]
    public async Task Sixteen_independent_adapters_have_one_commit_and_fifteen_duplicates_or_conflicts(bool conflicting)
    {
        await using var f = await LiveSqlFixture.Create();
        await f.Seed("RACE");
        var input = LiveSqlFixture.Event("RACE");
        var preSnapshot = await f.Snapshot();
        f.RecordProbe("adapter-race-pre-snapshot", new { conflicting, snapshot = preSnapshot });
        var before = Assert.IsType<ShipmentState>(f.Store().Find("RACE"));
        Assert.Equal(0L, before.Version);
        Assert.Equal(ShipmentStatus.Registered, before.Status);
        Assert.Equal(LiveSqlFixture.Time, before.LastOccurredAt);
        Assert.Empty(before.Events);

        await using var blocker = await f.OpenAdmin();
        using var blockerTransaction = blocker.BeginTransaction(IsolationLevel.Serializable);
        using (var hold = LiveSqlFixture.Command(blocker,
            $"SELECT TrackingNumber FROM {f.Q}.Shipments WITH(XLOCK,HOLDLOCK) WHERE TrackingNumber='RACE'", blockerTransaction))
        {
            if (!Equals("RACE", await hold.ExecuteScalarAsync()))
                throw new SqlFixturePrerequisiteException("Owned adapter race parent was not found.");
        }
        await using var observer = await f.OpenAdmin();
        using var barrier = new Barrier(16);
        var workers = Enumerable.Range(0, 16).Select(worker => Task.Factory.StartNew(() =>
        {
            DateTimeOffset? startedAtUtc = null;
            var timer = Stopwatch.StartNew();
            try
            {
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(20)))
                    throw new TimeoutException("16 worker start barrier failed.");
                startedAtUtc = DateTimeOffset.UtcNow;
                var result = f.Store(worker).ApplyAtomically(conflicting ? input with { SourceSystem = "SRC-" + worker } : input);
                timer.Stop();
                return (Worker: worker, StartedAtUtc: startedAtUtc, CompletedAtUtc: DateTimeOffset.UtcNow,
                    ElapsedMilliseconds: timer.Elapsed.TotalMilliseconds, Result: result, ErrorType: (string?)null);
            }
            catch (Exception error)
            {
                timer.Stop();
                return (Worker: worker, StartedAtUtc: startedAtUtc, CompletedAtUtc: DateTimeOffset.UtcNow,
                    ElapsedMilliseconds: timer.Elapsed.TotalMilliseconds, Result: (ApplyEventResult?)null,
                    ErrorType: error.GetType().FullName);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        var allWorkers = Task.WhenAll(workers);
        AdapterRequest[] requests;
        try
        {
            requests = await ObserveAdapterRequests(f, observer, blocker.ServerProcessId, allWorkers);
        }
        finally
        {
            blockerTransaction.Rollback();
            await allWorkers.WaitAsync(TimeSpan.FromSeconds(60));
        }
        var outcomes = await allWorkers;
        f.RecordProbe("adapter-race-outcomes", new
        {
            conflicting,
            workers = outcomes.OrderBy(outcome => outcome.Worker).Select(outcome => new
            {
                outcome.Worker,
                outcome.StartedAtUtc,
                outcome.CompletedAtUtc,
                outcome.ElapsedMilliseconds,
                resultCode = outcome.Result?.Code.ToString(),
                recordedEventId = outcome.Result?.RecordedEvent?.EventId,
                outcome.ErrorType
            }).ToArray()
        });

        var untypedState = f.Store().Find("RACE");
        var eventCount = await f.EventCount("RACE");
        var postSnapshot = await f.Snapshot();
        f.RecordProbe("adapter-race-post-snapshot", new { conflicting, snapshot = postSnapshot });

        // These are all active requests from this fixture's 16 worker identities; blocker chains are diagnostic only.
        Assert.Equal(16, requests.Length);
        Assert.Equal(16, requests.Select(request => request.Spid).Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 16), requests.Select(request => request.Worker).Order());
        Assert.Equal(16, requests.Select(request => request.ConnectionId).Distinct().Count());
        Assert.Equal(Enumerable.Range(0,16).Select(f.WorkerApplicationName).Order(StringComparer.Ordinal),
            requests.Select(request => request.ApplicationName).Order(StringComparer.Ordinal));
        Assert.Equal(Enumerable.Range(0, 16), outcomes.Select(outcome => outcome.Worker).Order());
        Assert.All(outcomes, outcome => Assert.NotNull(outcome.StartedAtUtc));
        Assert.All(outcomes, outcome => Assert.Null(outcome.ErrorType));
        var result = outcomes.Select(x => x.Result).ToArray();
        Assert.Equal(1, result.Count(x => x?.Code == ApplyEventCode.Applied));
        Assert.Equal(15, result.Count(x => x?.Code == (conflicting ? ApplyEventCode.EventIdConflict : ApplyEventCode.Duplicate)));
        var appliedResult = Assert.Single(result, x => x?.Code == ApplyEventCode.Applied);
        var appliedEvent = Assert.IsType<LifecycleEvent>(appliedResult!.RecordedEvent);
        var state = Assert.IsType<ShipmentState>(untypedState);
        Assert.Equal(before.Version + 1, state.Version);
        Assert.Equal("RACE", state.TrackingNumber);
        var recorded = Assert.Single(state.Events);
        Assert.Equal(input.EventId, recorded.EventId);
        Assert.Equal(ShipmentStatus.Accepted, state.Status);
        Assert.Equal(input.OccurredAt, state.LastOccurredAt);
        Assert.Equal(appliedEvent, recorded);
        Assert.Equal(1, eventCount);
        if (!conflicting) Assert.All(result, r => Assert.Equal(input, r!.RecordedEvent));
        AssertSnapshotDelta(preSnapshot, postSnapshot, appliedEvent);
    }
}
