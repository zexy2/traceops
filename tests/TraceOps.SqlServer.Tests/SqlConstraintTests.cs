using Microsoft.Data.SqlClient;
using Xunit;

namespace TraceOps.SqlServer.Tests;

public sealed class SqlConstraintTests
{
    [Theory, InlineData("trackingSpace"), InlineData("trackingLower"), InlineData("trackingEmpty"), InlineData("status"), InlineData("version"), InlineData("offset"), Trait("Acceptance", "SQL-02")]
    public async Task Shipment_table_rejects_invalid_storage_values(string kind)
    {
        await using var f = await LiveSqlFixture.Create();
        await using var c = await f.OpenAdmin();
        using var command = LiveSqlFixture.Command(c, $"INSERT {f.Q}.Shipments(TrackingNumber,RegisteredAt,Status,LastOccurredAt,Version) VALUES(@t,@at,@status,@at,@version)");
        command.Parameters.AddWithValue("@t", kind switch { "trackingSpace" => "A ", "trackingLower" => "a", "trackingEmpty" => "", _ => "A" });
        LiveSqlFixture.Timestamp(command, "@at", kind == "offset" ? LiveSqlFixture.Time.ToOffset(TimeSpan.FromHours(1)) : LiveSqlFixture.Time);
        command.Parameters.AddWithValue("@status", kind == "status" ? 7 : 0);
        command.Parameters.AddWithValue("@version", kind == "version" ? -1L : 0L);
        Assert.Equal(547, await LiveSqlFixture.SqlError(async () => { await command.ExecuteNonQueryAsync(); }));
    }
    [Theory, InlineData("fk"), InlineData("sequence"), InlineData("eventId"), InlineData("status"), InlineData("sourceSpace"), InlineData("sourceEmpty"), InlineData("offset"), Trait("Acceptance", "SQL-02")]
    public async Task Event_table_checks_and_non_cascading_foreign_key_reject_invalid_rows(string kind)
    {
        await using var f = await LiveSqlFixture.Create();
        await f.Seed("A");
        await using var c = await f.OpenAdmin();
        using var command = LiveSqlFixture.Command(c, $"INSERT {f.Q}.ShipmentEvents(TrackingNumber,EventSequence,EventId,Status,OccurredAt,SourceSystem) VALUES(@t,@seq,@id,@status,@at,@source)");
        command.Parameters.AddWithValue("@t", kind == "fk" ? "MISSING" : "A");
        command.Parameters.AddWithValue("@seq", kind == "sequence" ? 0L : 1L);
        command.Parameters.AddWithValue("@id", kind == "eventId" ? Guid.Empty : Guid.NewGuid());
        command.Parameters.AddWithValue("@status", kind == "status" ? 7 : 1);
        command.Parameters.AddWithValue("@source", kind switch { "sourceSpace" => "SRC ", "sourceEmpty" => "", _ => "SRC" });
        LiveSqlFixture.Timestamp(command, "@at", kind == "offset" ? LiveSqlFixture.Time.ToOffset(TimeSpan.FromHours(1)) : LiveSqlFixture.Time);
        Assert.Equal(547, await LiveSqlFixture.SqlError(async () => { await command.ExecuteNonQueryAsync(); }));
        Assert.Equal(0, Convert.ToInt32(await LiveSqlFixture.Scalar(c, "SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(@o) AND (delete_referential_action<>0 OR update_referential_action<>0)", ("@o", f.Schema + ".ShipmentEvents"))));
    }
    [Fact, Trait("Acceptance", "SQL-02")]
    public async Task Event_sequence_and_eventId_are_unique_per_shipment_and_parent_delete_cannot_cascade()
    {
        await using var f = await LiveSqlFixture.Create();
        await f.Seed("A");
        await using var c = await f.OpenAdmin();
        var id = Guid.NewGuid();
        var insert = $"INSERT {f.Q}.ShipmentEvents(TrackingNumber,EventSequence,EventId,Status,OccurredAt,SourceSystem) VALUES('A',@seq,@id,1,@at,'SRC')";
        await LiveSqlFixture.Execute(c, insert, ("@seq", 1L), ("@id", id), ("@at", LiveSqlFixture.Time));
        foreach (var (seq, eventId) in new[] { (1L, Guid.NewGuid()), (2L, id) })
        {
            using var command = LiveSqlFixture.Command(c, insert);
            command.Parameters.AddWithValue("@seq", seq);
            command.Parameters.AddWithValue("@id", eventId);
            LiveSqlFixture.Timestamp(command, "@at", LiveSqlFixture.Time);
            Assert.Contains(await LiveSqlFixture.SqlError(async () => { await command.ExecuteNonQueryAsync(); }), new int?[] { 2601, 2627 });
        }
        using var delete = LiveSqlFixture.Command(c, $"DELETE FROM {f.Q}.Shipments WHERE TrackingNumber='A'");
        Assert.Equal(547, await LiveSqlFixture.SqlError(async () => { await delete.ExecuteNonQueryAsync(); }));
        Assert.Equal(1, await f.EventCount("A"));
    }
}
