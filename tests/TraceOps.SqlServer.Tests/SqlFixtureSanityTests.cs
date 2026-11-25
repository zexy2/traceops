using Microsoft.Data.SqlClient;
using Xunit;

namespace TraceOps.SqlServer.Tests;

public sealed class SqlFixtureSanityTests
{
    [Fact]
    [Trait("Acceptance", "FixtureSanity")]
    public void Accepts_only_matching_runtime_and_fixture_connections_to_the_dedicated_database()
    {
        var target = SqlServerFixture.RequireConfiguredTarget(EnvironmentFor());

        Assert.Equal(SqlServerFixture.DedicatedDatabaseName, target.DatabaseName);
        Assert.Equal("fixture-target.invalid", target.DataSource);
    }

    [Theory]
    [InlineData("master")]
    [InlineData("tempdb")]
    [InlineData("TraceOpsOtherDB")]
    [InlineData("traceopslabdb")]
    [Trait("Acceptance", "FixtureSanity")]
    public void Rejects_non_dedicated_or_non_exact_database_names(string databaseName)
    {
        var environment = EnvironmentFor(databaseName: databaseName);

        Assert.Throws<SqlFixturePrerequisiteException>(
            () => SqlServerFixture.RequireConfiguredTarget(environment));
    }

    [Fact]
    [Trait("Acceptance", "FixtureSanity")]
    public void Rejects_fixture_and_runtime_connections_pointing_at_different_servers()
    {
        var environment = EnvironmentFor(fixtureDataSource: "other-target.invalid");

        Assert.Throws<SqlFixturePrerequisiteException>(
            () => SqlServerFixture.RequireConfiguredTarget(environment));
    }

    [Fact]
    [Trait("Acceptance", "FixtureSanity")]
    public void Requires_exact_database_ownership_marker_identity()
    {
        SqlServerFixture.RequireOwnedDatabaseIdentity(
            SqlServerFixture.DedicatedDatabaseName,
            SqlServerFixture.OwnershipMarkerName,
            SqlServerFixture.OwnershipMarkerValue);

        Assert.Throws<SqlFixturePrerequisiteException>(() =>
            SqlServerFixture.RequireOwnedDatabaseIdentity(
                "master",
                SqlServerFixture.OwnershipMarkerName,
                SqlServerFixture.OwnershipMarkerValue));
        Assert.Throws<SqlFixturePrerequisiteException>(() =>
            SqlServerFixture.RequireOwnedDatabaseIdentity(
                SqlServerFixture.DedicatedDatabaseName,
                SqlServerFixture.OwnershipMarkerName,
                "near-match"));
    }

    [Fact]
    [Trait("Acceptance", "FixtureSanity")]
    public void Creates_and_quotes_only_generated_GUID_schema_names()
    {
        var runId = Guid.Parse("8d31bc0a-5e76-48e2-a17e-13ca55b0f71c");
        var schemaName = SqlServerFixture.CreateSchemaName(runId);

        Assert.Equal("m4_8d31bc0a5e7648e2a17e13ca55b0f71c", schemaName);
        Assert.Equal($"[{schemaName}]", SqlServerFixture.QuoteSchemaName(schemaName));
        Assert.Throws<SqlFixturePrerequisiteException>(() => SqlServerFixture.QuoteSchemaName("dbo"));
        Assert.Throws<SqlFixturePrerequisiteException>(() =>
            SqlServerFixture.QuoteSchemaName("m4_8d31bc0a5e7648e2a17e13ca55b0f71c]; DROP DATABASE master;--"));
        Assert.Throws<ArgumentException>(() => SqlServerFixture.CreateSchemaName(Guid.Empty));
    }

    [Theory]
    [InlineData(SqlServerFixture.RuntimeConnectionKey)]
    [InlineData(SqlServerFixture.FixtureConnectionKey)]
    [Trait("Acceptance", "FixtureSanity")]
    public void Rejects_missing_connection_variables_without_values(string missingKey)
    {
        var environment = EnvironmentFor();
        environment[missingKey] = null;

        var error = Assert.Throws<SqlFixturePrerequisiteException>(
            () => SqlServerFixture.RequireConfiguredTarget(environment));

        Assert.Contains(missingKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Acceptance", "FixtureSanity")]
    public void Redacts_invalid_target_connection_values_from_prerequisite_errors()
    {
        const string sentinel = "NotARealCredential-SqlFixtureTest";
        var environment = EnvironmentFor(databaseName: "master", password: sentinel);

        var error = Assert.Throws<SqlFixturePrerequisiteException>(
            () => SqlServerFixture.RequireConfiguredTarget(environment));

        Assert.DoesNotContain(sentinel, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("User ID=", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string?> EnvironmentFor(
        string databaseName = SqlServerFixture.DedicatedDatabaseName,
        string runtimeDataSource = "fixture-target.invalid",
        string? fixtureDataSource = null,
        string? password = null)
    {
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [SqlServerFixture.RuntimeConnectionKey] = BuildConnectionString(
                runtimeDataSource,
                databaseName,
                password),
            [SqlServerFixture.FixtureConnectionKey] = BuildConnectionString(
                fixtureDataSource ?? runtimeDataSource,
                databaseName,
                password),
        };
    }

    private static string BuildConnectionString(string dataSource, string databaseName, string? password)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            InitialCatalog = databaseName,
            Encrypt = true,
            TrustServerCertificate = true,
        };

        if (password is null)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = "fixture-test-user";
            builder.Password = password;
        }

        return builder.ConnectionString;
    }
}
