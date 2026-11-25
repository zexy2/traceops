using System.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace TraceOps.SqlServer.Tests;

public sealed class StoredProcedureContractTests
{
    public static IEnumerable<object?[]> InvalidTracking()
    {
        foreach (var value in new string?[] { null, "", new('A', 33), "A ", " A", "A\0", "é", "Ａ", "😀", "a", "A\t" }) yield return new object?[] { value };
    }
    public static IEnumerable<object[]> InvalidSource()
    {
        foreach (var value in new[] { "", new string('A', 65), "SRC ", " SRC", "S\0", "é", "ＳＲＣ", "😀", "S\t" }) yield return new object[] { value };
    }
    [Fact, Trait("Acceptance", "SQL-01")]
    public async Task Ledger_and_four_procedure_parameter_metadata_match_fixed_contract()
    {
        await using var f = await LiveSqlFixture.Create();
        await using var c = await f.OpenAdmin();
        Assert.Equal(1, Convert.ToInt32(await LiveSqlFixture.Scalar(c, $"SELECT COUNT(*) FROM {f.Q}.SchemaVersion WHERE Version=1 AND MigrationId='M4.1'")));
        Assert.Equal(1, Convert.ToInt32(await LiveSqlFixture.Scalar(c, $"SELECT COUNT(*) FROM {f.Q}.SchemaVersion")));
        var expected = new[] {
            "Shipment_Find|1|@TrackingNumber|nvarchar|-1|0|0",
            "Shipment_TryRegister|1|@TrackingNumber|nvarchar|-1|0|0",
            "Shipment_TryRegister|2|@RegisteredAt|datetimeoffset|10|7|0",
            "Shipment_TryRegister|3|@ResultCode|varchar|32|0|1",
            "Shipment_AppendEvent|1|@TrackingNumber|nvarchar|-1|0|0",
            "Shipment_AppendEvent|2|@ExpectedVersion|bigint|8|0|0",
            "Shipment_AppendEvent|3|@EventId|uniqueidentifier|16|0|0",
            "Shipment_AppendEvent|4|@Status|int|4|0|0",
            "Shipment_AppendEvent|5|@OccurredAt|datetimeoffset|10|7|0",
            "Shipment_AppendEvent|6|@SourceSystem|nvarchar|-1|0|0",
            "Shipment_AppendEvent|7|@ResultCode|varchar|24|0|1" };
        using var command = LiveSqlFixture.Command(c, "SELECT o.name,p.parameter_id,p.name,TYPE_NAME(p.user_type_id),p.max_length,p.scale,p.is_output FROM sys.procedures o JOIN sys.parameters p ON p.object_id=o.object_id WHERE o.schema_id=SCHEMA_ID(@s) ORDER BY o.name,p.parameter_id");
        command.Parameters.AddWithValue("@s", f.Schema);
        using var reader = await command.ExecuteReaderAsync();
        var actual = new List<string>();
        while (await reader.ReadAsync()) actual.Add($"{reader.GetString(0)}|{reader.GetInt32(1)}|{reader.GetString(2)}|{reader.GetString(3)}|{reader.GetInt16(4)}|{reader.GetByte(5)}|{(reader.GetBoolean(6) ? 1 : 0)}");
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }
    [Theory, InlineData("Shipment_List"), InlineData("Shipment_Find"), Trait("Acceptance", "SQL-02")]
    public async Task Empty_reads_have_exactly_two_typed_result_sets(string procedure)
    {
        await using var f = await LiveSqlFixture.Create();
        await using var c = await f.OpenRuntime();
        using var command = f.Procedure(c, procedure);
        if (procedure == "Shipment_Find") LiveSqlFixture.Wide(command, "@TrackingNumber", "MISSING");
        using var reader = await command.ExecuteReaderAsync();
        await AssertEmptySets(reader);
    }
    private static async Task AssertEmptySets(SqlDataReader reader)
    {
        Assert.Equal(new[] { "TrackingNumber", "RegisteredAt", "Status", "LastOccurredAt", "Version" }, Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
        Assert.Equal(new[] { typeof(string), typeof(DateTimeOffset), typeof(byte), typeof(DateTimeOffset), typeof(long) }, Enumerable.Range(0, reader.FieldCount).Select(reader.GetFieldType));
        Assert.False(await reader.ReadAsync());
        Assert.True(await reader.NextResultAsync());
        Assert.Equal(new[] { "TrackingNumber", "EventSequence", "EventId", "Status", "OccurredAt", "SourceSystem" }, Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
        Assert.Equal(new[] { typeof(string), typeof(long), typeof(Guid), typeof(byte), typeof(DateTimeOffset), typeof(string) }, Enumerable.Range(0, reader.FieldCount).Select(reader.GetFieldType));
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
    }
    [Theory, MemberData(nameof(InvalidTracking)), Trait("Acceptance", "SQL-02"), Trait("Acceptance", "SQL-05")]
    public async Task Invalid_raw_tracking_is_not_trimmed_truncated_or_best_fit_converted(string? tracking)
    {
        await using var f = await LiveSqlFixture.Create();
        Assert.Equal("INVALID_TRACKING_NUMBER", await f.Register(tracking, LiveSqlFixture.Time.ToOffset(TimeSpan.FromHours(1))));
        await using var c = await f.OpenRuntime();
        using var command = f.Procedure(c, "Shipment_Find");
        LiveSqlFixture.Wide(command, "@TrackingNumber", tracking);
        using var reader = await command.ExecuteReaderAsync();
        await AssertEmptySets(reader);
    }
    [Theory, InlineData(1), InlineData(32), Trait("Acceptance", "SQL-02")]
    public async Task Register_accepts_exact_ASCII_boundaries_and_preserves_timestamp7(int length)
    {
        await using var f = await LiveSqlFixture.Create();
        var tracking = new string('A', length);
        Assert.Equal("REGISTERED", await f.Register(tracking));
        Assert.Equal("TRACKING_NUMBER_CONFLICT", await f.Register(tracking));
        await using var c = await f.OpenAdmin();
        Assert.Equal(LiveSqlFixture.Time, await LiveSqlFixture.Scalar(c, $"SELECT RegisteredAt FROM {f.Q}.Shipments WHERE TrackingNumber=@t", ("@t", tracking)));
    }
    [Fact, Trait("Acceptance", "SQL-02")]
    public async Task Register_rejects_nonUTC_without_writes()
    {
        await using var f = await LiveSqlFixture.Create();
        Assert.Equal("TIMESTAMP_MUST_BE_UTC", await f.Register("A", LiveSqlFixture.Time.ToOffset(TimeSpan.FromMinutes(30))));
        await using var c = await f.OpenAdmin();
        Assert.Equal(0, Convert.ToInt32(await LiveSqlFixture.Scalar(c, $"SELECT COUNT(*) FROM {f.Q}.Shipments")));
    }
    [Theory, MemberData(nameof(InvalidSource)), Trait("Acceptance", "SQL-02"), Trait("Acceptance", "SQL-05")]
    public async Task Append_rejects_original_wide_source_before_cast_and_changes_nothing(string source)
    {
        await using var f = await LiveSqlFixture.Create();
        var tracking = LiveSqlFixture.Tracking();
        await f.Seed(tracking);
        await using var c = await f.OpenRuntime();
        using var tx = c.BeginTransaction(IsolationLevel.Serializable);
        using var command = f.AppendCommand(c, tx, LiveSqlFixture.Event(tracking, source: source));
        Assert.Equal(51010, await LiveSqlFixture.SqlError(async () => { await command.ExecuteNonQueryAsync(); }));
        try { tx.Rollback(); } catch (InvalidOperationException) { }
        Assert.Equal(0, await f.EventCount(tracking));
    }
    [Theory, InlineData("eventId"), InlineData("status"), InlineData("offset"), InlineData("version"), InlineData("tracking"), InlineData("sourceNull"), Trait("Acceptance", "SQL-02")]
    public async Task Append_invalid_scalar_or_key_throws_51010_inside_caller_transaction(string field)
    {
        await using var f = await LiveSqlFixture.Create();
        var tracking = LiveSqlFixture.Tracking();
        await f.Seed(tracking);
        await using var c = await f.OpenRuntime();
        using var tx = c.BeginTransaction(IsolationLevel.Serializable);
        using var command = f.AppendCommand(c, tx, LiveSqlFixture.Event(tracking));
        switch (field)
        {
            case "eventId": command.Parameters["@EventId"].Value = Guid.Empty; break;
            case "status": command.Parameters["@Status"].Value = 7; break;
            case "offset": command.Parameters["@OccurredAt"].Value = LiveSqlFixture.Time.ToOffset(TimeSpan.FromHours(1)); break;
            case "version": command.Parameters["@ExpectedVersion"].Value = -1L; break;
            case "tracking": command.Parameters["@TrackingNumber"].Value = tracking + " "; break;
            case "sourceNull": command.Parameters["@SourceSystem"].Value = DBNull.Value; break;
        }
        Assert.Equal(51010, await LiveSqlFixture.SqlError(async () => { await command.ExecuteNonQueryAsync(); }));
        try { tx.Rollback(); } catch (InvalidOperationException) { }
        Assert.Equal(0, await f.EventCount(tracking));
    }
    [Theory, InlineData(1), InlineData(64), Trait("Acceptance", "SQL-02")]
    public async Task Append_valid_source_boundaries_commits_only_in_caller_transaction(int length)
    {
        await using var f = await LiveSqlFixture.Create();
        var tracking = LiveSqlFixture.Tracking();
        await f.Seed(tracking);
        await using var c = await f.OpenRuntime();
        using var tx = c.BeginTransaction(IsolationLevel.Serializable);
        using var command = f.AppendCommand(c, tx, LiveSqlFixture.Event(tracking, source: new string('a', length)));
        await command.ExecuteNonQueryAsync();
        Assert.Equal("APPENDED", command.Parameters["@ResultCode"].Value);
        using var count = LiveSqlFixture.Command(c, "SELECT @@TRANCOUNT", tx);
        Assert.Equal(1, Convert.ToInt32(await count.ExecuteScalarAsync()));
        tx.Commit();
        Assert.Equal(1, await f.EventCount(tracking));
    }
    [Fact, Trait("Acceptance", "SQL-02")]
    public async Task Append_without_caller_transaction_throws_51012()
    {
        await using var f = await LiveSqlFixture.Create();
        await using var c = await f.OpenRuntime();
        using var command = f.AppendCommand(c, null, LiveSqlFixture.Event("MISSING"));
        Assert.Equal(51012, await LiveSqlFixture.SqlError(async () => { await command.ExecuteNonQueryAsync(); }));
    }
    [Fact, Trait("Acceptance", "SQL-02")]
    public async Task Stale_version_throws_51011_without_partial_insert()
    {
        await using var f = await LiveSqlFixture.Create();
        await f.Seed("A");
        await using var c = await f.OpenRuntime();
        using var tx = c.BeginTransaction(IsolationLevel.Serializable);
        using var command = f.AppendCommand(c, tx, LiveSqlFixture.Event("A"), 99);
        Assert.Equal(51011, await LiveSqlFixture.SqlError(async () => { await command.ExecuteNonQueryAsync(); }));
        try { tx.Rollback(); } catch (InvalidOperationException) { }
        Assert.Equal(0, await f.EventCount("A"));
    }
    [Theory, InlineData("Shipment_List", false), InlineData("Shipment_Find", false), InlineData("Shipment_List", true), InlineData("Shipment_Find", true), Trait("Acceptance", "SQL-02")]
    public async Task Read_procedures_assert_exact_ledger_even_for_invalid_Find_key(string procedure, bool extraRow)
    {
        await using var f = await LiveSqlFixture.Create();
        await using (var admin = await f.OpenAdmin())
        {
            await LiveSqlFixture.Execute(admin, extraRow
                ? $"INSERT {f.Q}.SchemaVersion(Version,MigrationId) VALUES(2,'WRONG')"
                : $"UPDATE {f.Q}.SchemaVersion SET MigrationId='WRONG'");
        }
        await using var c = await f.OpenRuntime();
        using var command = f.Procedure(c, procedure);
        if (procedure == "Shipment_Find") LiveSqlFixture.Wide(command, "@TrackingNumber", "Ａ ");
        Assert.Equal(51013, await LiveSqlFixture.SqlError(async () => { using var r = await command.ExecuteReaderAsync(); do { while (await r.ReadAsync()) { } } while (await r.NextResultAsync()); }));
    }
}
