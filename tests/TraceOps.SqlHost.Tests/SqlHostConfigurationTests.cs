using System.Net;
using Xunit;

namespace TraceOps.SqlHost.Tests;

public sealed class SqlHostConfigurationTests
{
    [Theory]
    [InlineData("TraceOps__SqlServer__ExpectedDatabase",null)]
    [InlineData("TraceOps__SqlServer__ExpectedDatabase","")]
    [InlineData("TraceOps__SqlServer__ExpectedDatabase"," \t ")]
    [InlineData("TraceOps__SqlServer__ExpectedDatabase","traceopslabdb")]
    [InlineData("TraceOps__SqlServer__ExpectedServerName",null)]
    [InlineData("TraceOps__SqlServer__ExpectedServerName","")]
    [InlineData("TraceOps__SqlServer__ExpectedServerName"," \t ")]
    [InlineData("TraceOps__SqlServer__ExpectedServerName","TRACEOPS-SQL")]
    [Trait("Acceptance","M4-Host-Identity")]
    public async Task Valid_SQL_control_then_one_missing_empty_whitespace_or_ordinal_wrong_identity_fails_closed(string key,string? value)
    {
        await using var fixture=await SqlHostFixture.Create();
        var baseline=await fixture.Snapshot();
        var validSettings=fixture.Configuration();
        Assert.Equal("TraceOpsLabDB",validSettings["TraceOps__SqlServer__ExpectedDatabase"]);
        Assert.Equal(SqlHostFixture.ExpectedLabServerName,validSettings["TraceOps__SqlServer__ExpectedServerName"]);
        await using(var valid=await HostRuntimeProbe.Start(validSettings))
        {
            Assert.True(valid.Started,"Prerequisite-valid SQL identity control must start before the single-setting negative.");
            using var response=await valid.Client.GetAsync("/shipments");
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            HostRuntimeProbe.AssertVerifiedSqlBanner(await response.Content.ReadAsStringAsync());
            Assert.True(valid.LogsSafe,"Child diagnostic privacy failure; contents withheld.");
        }
        Assert.Equal(baseline,await fixture.Snapshot());
        // Calibrate that this exact child/config reaches List when identity is correct.
        await fixture.PoisonListWithOwnedInvocationCounter();
        var beforePoison=await fixture.ListInvocation();
        await using(var poisoned=await HostRuntimeProbe.Start(validSettings))
        {
            Assert.False(poisoned.Started,"Poisoned List must prevent startup.");
            Assert.True(poisoned.Exited);
            Assert.False(poisoned.FalseConnectedClaim);
            Assert.True(poisoned.LogsSafe,"Child diagnostic privacy failure; contents withheld.");
        }
        var afterPoison=await fixture.ListInvocation();
        Assert.NotNull(afterPoison);
        Assert.True(afterPoison> (beforePoison??0),"Positive poison control must actually invoke owned List.");
        baseline=await fixture.Snapshot();
        var settings=fixture.Configuration();
        if(value is null)settings.Remove(key);else settings[key]=value;
        Assert.Single(validSettings.Keys.Union(settings.Keys),k=>validSettings.GetValueOrDefault(k)!=settings.GetValueOrDefault(k));
        await using(var rejected=await HostRuntimeProbe.Start(settings))
        {
            Assert.False(rejected.Started,"Identity mismatch/missing setting must not serve fallback HTTP.");
            Assert.True(rejected.Exited,"Identity rejection must stop startup rather than hang.");
            Assert.False(rejected.FalseConnectedClaim);
            Assert.True(rejected.LogsSafe,"Child diagnostic privacy failure; contents withheld.");
        }
        Assert.Equal(afterPoison,await fixture.ListInvocation());
        Assert.Equal(baseline,await fixture.Snapshot());
    }
    [Fact,Trait("Acceptance","M4-Host-SchemaContract")]
    public void SQL_store_constructor_default_schema_is_trace_without_opening_SQL()
    {
        var constructor=typeof(TraceOps.SqlServer.SqlServerShipmentStore).GetConstructor(new[]{typeof(string),typeof(string)});
        Assert.NotNull(constructor);
        var schema=constructor.GetParameters()[1];
        Assert.True(schema.HasDefaultValue);
        Assert.Equal("trace",schema.DefaultValue);
        // Adapter API default evidence only. Host selection/forwarding needs the separate source review.
    }
    [Theory,InlineData(null),InlineData("InMemory"),Trait("Acceptance","M4-Host-Default")]
    public async Task Default_and_explicit_InMemory_preserve_demo_banner_without_SQL_credentials(string? mode)
    {
        await using var host=await HostRuntimeProbe.Start(new(){["TraceOps__Store"]=mode,["TraceOps__Soap__Enabled"]="false"});
        Assert.True(host.Started,"Default host failed to start; child diagnostics withheld.");
        using var response=await host.Client.GetAsync("/shipments");
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.Contains("Synthetic demo — volatile in-memory storage. Restarting loses changes. SQL Server and SOAP are not connected.",WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()),StringComparison.Ordinal);
        Assert.True(host.LogsSafe,"Child diagnostic privacy failure; contents withheld.");
    }
    [Theory,InlineData(null),InlineData(""),InlineData(" \t "),Trait("Acceptance","M4-Host-Startup")]
    public async Task SQL_mode_missing_secret_terminates_startup_without_fallback_or_connected_claim(string? secret)
    {
        await using var fixture=await SqlHostFixture.Create();
        var baseline=await fixture.Snapshot();
        var validSettings=fixture.Configuration();
        await using(var valid=await HostRuntimeProbe.Start(validSettings))
        {
            Assert.True(valid.Started,"Valid SQL control must start before removing only the secret.");
            using var response=await valid.Client.GetAsync("/shipments");
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            HostRuntimeProbe.AssertVerifiedSqlBanner(await response.Content.ReadAsStringAsync());
            Assert.True(valid.LogsSafe,"Child diagnostic privacy failure; contents withheld.");
        }
        Assert.Equal(baseline,await fixture.Snapshot());
        var settings=fixture.Configuration();
        if(secret is null)settings.Remove("ConnectionStrings__TraceOpsSqlServer");else settings["ConnectionStrings__TraceOpsSqlServer"]=secret;
        Assert.Single(validSettings.Keys.Union(settings.Keys),key=>validSettings.GetValueOrDefault(key)!=settings.GetValueOrDefault(key));
        await using(var host=await HostRuntimeProbe.Start(settings))
        {
            Assert.False(host.Started,"SQL mode must not serve an InMemory fallback.");
            Assert.True(host.Exited,"Rejected SQL configuration must stop startup, not hang.");
            Assert.False(host.FalseConnectedClaim);
            Assert.True(host.LogsSafe,"Child diagnostic privacy failure; contents withheld.");
        }
        Assert.Equal(baseline,await fixture.Snapshot());
    }
    [Theory,InlineData("unreachable"),InlineData("missingSchema"),InlineData("ledger"),InlineData("missingSP"),InlineData("oneSet"),InlineData("wrongColumns"),Trait("Acceptance","M4-Host-Startup")]
    public async Task Valid_SQL_control_then_single_defect_fails_closed_without_writes_or_secret_logs(string defect)
    {
        await using var fixture=await SqlHostFixture.Create();
        // Mandatory successful control prevents generic SqlServer rejection from satisfying all negatives.
        await using(var valid=await HostRuntimeProbe.Start(fixture.Configuration()))
        {
            Assert.True(valid.Started,"Prerequisite-valid SQL host control must start before negative mutation.");
            using var page=await valid.Client.GetAsync("/shipments");
            HostRuntimeProbe.AssertVerifiedSqlBanner(await page.Content.ReadAsStringAsync());
            Assert.True(valid.LogsSafe,"Child diagnostic privacy failure; contents withheld.");
        }
        var settings=fixture.Configuration();
        switch(defect)
        {
            case "unreachable":
                settings["ConnectionStrings__TraceOpsSqlServer"]=HostRuntimeProbe.ChangeConnection(fixture.Runtime,b=>{b.DataSource="127.0.0.1,1";b.ConnectTimeout=1;});break;
            case "missingSchema":settings["TraceOps__SqlServer__Schema"]="m4_"+Guid.NewGuid().ToString("N");break;
            case "ledger":await fixture.Execute($"UPDATE {fixture.Q}.SchemaVersion SET MigrationId='HOST-INVALID'");break;
            case "missingSP":await fixture.Execute($"DROP PROCEDURE {fixture.Q}.Shipment_Find");break;
            case "oneSet":await fixture.Execute($"ALTER PROCEDURE {fixture.Q}.Shipment_List AS BEGIN SET NOCOUNT ON; SELECT TOP(0) TrackingNumber,RegisteredAt,Status,LastOccurredAt,Version FROM {fixture.Q}.Shipments; END");break;
            case "wrongColumns":await fixture.Execute($"ALTER PROCEDURE {fixture.Q}.Shipment_List AS BEGIN SET NOCOUNT ON; SELECT TOP(0) TrackingNumber AS Incorrect,RegisteredAt,Status,LastOccurredAt,Version FROM {fixture.Q}.Shipments; SELECT TOP(0) TrackingNumber,EventSequence,EventId,Status,OccurredAt,SourceSystem FROM {fixture.Q}.ShipmentEvents; END");break;
        }
        var baseline=await fixture.Snapshot();
        await using(var host=await HostRuntimeProbe.Start(settings))
        {
            Assert.False(host.Started,"Broken SQL startup prerequisite must not serve fallback.");
            Assert.True(host.Exited,"Broken SQL startup prerequisite must terminate startup.");
            Assert.False(host.FalseConnectedClaim);
            Assert.True(host.LogsSafe,"Child diagnostic privacy failure; contents withheld.");
        }
        Assert.Equal(baseline,await fixture.Snapshot());
    }
}
