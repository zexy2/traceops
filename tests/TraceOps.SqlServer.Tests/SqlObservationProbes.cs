using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace TraceOps.SqlServer.Tests;

internal static class SqlObservationProbes
{
    // Full rows and columns, including ledger, registration timestamps, projection and event payloads.
    // The only table names are fixed; the identifier is the fixture's exact owned GUID schema.
    internal static async Task<string> Snapshot(this LiveSqlFixture fixture)
    {
        await using var connection = await fixture.OpenAdmin();
        var tables = new List<object>();
        foreach (var (table, order) in new[] {
            ("SchemaVersion", "Version,MigrationId"),
            ("Shipments", "TrackingNumber COLLATE Latin1_General_100_BIN2"),
            ("ShipmentEvents", "TrackingNumber COLLATE Latin1_General_100_BIN2,EventSequence,EventId") })
        {
            using var command = LiveSqlFixture.Command(connection, $"SELECT * FROM {fixture.Q}.[{table}] ORDER BY {order}");
            using var reader = await command.ExecuteReaderAsync();
            var columns = Enumerable.Range(0, reader.FieldCount).Select(i => new { name = reader.GetName(i), type = reader.GetDataTypeName(i) }).ToArray();
            var rows = new List<string?[]>();
            while (await reader.ReadAsync())
            {
                var values = new string?[reader.FieldCount];
                for (var i = 0; i < values.Length; i++)
                    values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i) switch {
                        DateTimeOffset value => value.ToString("O", CultureInfo.InvariantCulture),
                        DateTime value => value.ToString("O", CultureInfo.InvariantCulture),
                        byte[] value => Convert.ToHexString(value),
                        IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
                        var value => value.ToString() };
                rows.Add(values);
            }
            tables.Add(new { table, columns, rows });
        }
        return JsonSerializer.Serialize(tables);
    }

    internal sealed record Session(int Spid, Guid ConnectionId, string ApplicationName, int Blocker);
    internal sealed record PendingRead(bool HistoryWaiting, int Transactions, int Isolation, int Blocker);

    internal static async Task<Session[]> AdapterSessions(this LiveSqlFixture fixture, SqlConnection observer,
        int blocker, Task workers)
    {
        var elapsed = Stopwatch.StartNew();
        Session[] sessions;
        do
        {
            using var command = LiveSqlFixture.Command(observer, """
                SELECT s.session_id,c.connection_id,s.program_name,r.blocking_session_id
                FROM sys.dm_exec_sessions AS s
                JOIN sys.dm_exec_requests AS r ON r.session_id=s.session_id
                JOIN sys.dm_exec_connections AS c ON c.connection_id=r.connection_id
                WHERE s.database_id=DB_ID() AND r.database_id=DB_ID()
                  AND LEFT(s.program_name,LEN(@prefix))=@prefix
                  AND r.blocking_session_id=@blocker
                ORDER BY s.program_name
                """);
            command.Parameters.AddWithValue("@prefix", fixture.ApplicationPrefix);
            command.Parameters.AddWithValue("@blocker", blocker);
            var found = new List<Session>();
            try
            {
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) found.Add(new(Convert.ToInt32(reader.GetValue(0)), reader.GetGuid(1), reader.GetString(2), Convert.ToInt32(reader.GetValue(3))));
            }
            catch (SqlException error)
            {
                throw new SqlFixturePrerequisiteException($"Owned-session observer failed (SQL {error.Number}); values redacted.");
            }
            sessions = found.ToArray();
            if (sessions.Length >= 16 || workers.IsCompleted) break;
            await Task.Delay(25);
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(4));
        fixture.RecordProbe("adapter-sessions", new { expected = 16, observed = sessions, workersCompleted = workers.IsCompleted });
        return sessions;
    }

    internal static async Task<PendingRead?> HistoryWait(this LiveSqlFixture fixture, SqlConnection observer,
        string applicationName, int spid, int blocker, Task read)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            using var command = LiveSqlFixture.Command(observer, """
                SELECT r.open_transaction_count,s.transaction_isolation_level,r.blocking_session_id,
                  CONVERT(bit,CASE WHEN EXISTS
                  (SELECT 1 FROM sys.dm_tran_locks AS l
                   WHERE l.request_session_id=r.session_id AND l.resource_database_id=DB_ID()
                     AND l.request_status='WAIT'
                     AND ((l.resource_type='OBJECT' AND l.resource_associated_entity_id=OBJECT_ID(@history))
                       OR EXISTS(SELECT 1 FROM sys.partitions AS p
                                 WHERE p.object_id=OBJECT_ID(@history) AND p.hobt_id=l.resource_associated_entity_id)))
                  THEN 1 ELSE 0 END)
                FROM sys.dm_exec_sessions AS s
                JOIN sys.dm_exec_requests AS r ON r.session_id=s.session_id
                WHERE s.session_id=@spid AND s.program_name=@application
                  AND LEFT(s.program_name,LEN(@prefix))=@prefix
                  AND s.database_id=DB_ID() AND r.database_id=DB_ID()
                  AND r.blocking_session_id=@blocker AND r.wait_type LIKE N'LCK[_]M[_]%'
                """);
            command.Parameters.AddWithValue("@spid", spid);
            command.Parameters.AddWithValue("@blocker", blocker);
            command.Parameters.AddWithValue("@application", applicationName);
            command.Parameters.AddWithValue("@prefix", fixture.ApplicationPrefix);
            command.Parameters.AddWithValue("@history", fixture.Schema + ".ShipmentEvents");
            try
            {
                using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var observation = new PendingRead(reader.GetBoolean(3), Convert.ToInt32(reader.GetValue(0)), Convert.ToInt32(reader.GetValue(1)), Convert.ToInt32(reader.GetValue(2)));
                    if (observation.HistoryWaiting) return observation;
                }
            }
            catch (SqlException error)
            {
                throw new SqlFixturePrerequisiteException($"Owned read-lock observer failed (SQL {error.Number}); values redacted.");
            }
            if (read.IsCompleted) break;
            await Task.Delay(25);
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(4));
        return null; // Missing/early-completed target activity is asserted by the test, not fixture setup.
    }
}
