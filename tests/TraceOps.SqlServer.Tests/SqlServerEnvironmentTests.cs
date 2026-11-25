using Xunit;

namespace TraceOps.SqlServer.Tests;

public sealed class SqlServerEnvironmentTests
{
    [Fact]
    [Trait("Acceptance", "SQL-01")]
    public async Task Real_engine_database_marker_and_unprivileged_runtime_identity_are_verified_read_only()
    {
        await LiveSqlFixture.VerifyTargetOnly();
    }
    [Fact]
    [Trait("Acceptance", "SqlEnvironment")]
    public void Requires_configured_dedicated_target_before_any_SQL_connection_is_opened()
    {
        var target = SqlServerFixture.RequireConfiguredTarget(SqlServerFixture.ReadProcessEnvironment());

        Assert.Equal(SqlServerFixture.DedicatedDatabaseName, target.DatabaseName);
        Assert.False(string.IsNullOrWhiteSpace(target.DataSource));
    }
}
