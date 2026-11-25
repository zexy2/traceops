using TraceOps.Domain;
using Xunit;

namespace TraceOps.SqlServer.Tests;

public sealed class SqlRollbackTests
{
    [Fact, Trait("Acceptance", "SQL-07")]
    public async Task Owned_schema_after_insert_fault_rolls_back_event_and_projection_then_retry_commits_once()
    {
        await using var f = await LiveSqlFixture.Create();
        await f.Seed("ROLLBACK");
        var input = LiveSqlFixture.Event("ROLLBACK");
        await using (var admin = await f.OpenAdmin())
        {
            // Identifier comes only from the fixture's exact generated GUID schema allowlist.
            // Marker EventId is test-generated, not external input; no production schema is addressed.
            await LiveSqlFixture.Execute(admin, $"CREATE TRIGGER {f.Q}.[M4RollbackFault] ON {f.Q}.ShipmentEvents AFTER INSERT AS BEGIN SET NOCOUNT ON; IF EXISTS(SELECT 1 FROM inserted WHERE EventId=CONVERT(uniqueidentifier,'{input.EventId:D}')) THROW 51090,'Owned M4 rollback fault',1; END");
        }
        try
        {
            var error = Record.Exception(() => f.Store().ApplyAtomically(input));
            Assert.NotNull(error);
            var sqlError = Assert.IsType<Microsoft.Data.SqlClient.SqlException>(error);
            Assert.Contains(sqlError.Errors.Cast<Microsoft.Data.SqlClient.SqlError>(), x => x.Number == 51090);
            Assert.Equal(0, await f.EventCount("ROLLBACK"));
            await using var verify = await f.OpenAdmin();
            Assert.Equal(0L, Convert.ToInt64(await LiveSqlFixture.Scalar(verify, $"SELECT Version FROM {f.Q}.Shipments WHERE TrackingNumber='ROLLBACK'")));
            Assert.Equal(0, Convert.ToInt32(await LiveSqlFixture.Scalar(verify, $"SELECT Status FROM {f.Q}.Shipments WHERE TrackingNumber='ROLLBACK'")));
            Assert.Equal(LiveSqlFixture.Time, await LiveSqlFixture.Scalar(verify, $"SELECT LastOccurredAt FROM {f.Q}.Shipments WHERE TrackingNumber='ROLLBACK'"));
        }
        finally
        {
            await using var admin = await f.OpenAdmin();
            await LiveSqlFixture.Execute(admin, $"DROP TRIGGER {f.Q}.[M4RollbackFault]");
        }
        Assert.Equal(ApplyEventCode.Applied, f.Store().ApplyAtomically(input)?.Code);
        Assert.Equal(ApplyEventCode.Duplicate, f.Store().ApplyAtomically(input)?.Code);
        Assert.Equal(1, await f.EventCount("ROLLBACK"));
        var final = Assert.IsType<ShipmentState>(f.Store().Find("ROLLBACK"));
        Assert.Equal(1, final.Version);
        Assert.Equal(input, Assert.Single(final.Events));
    }
}
