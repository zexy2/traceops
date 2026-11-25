using Microsoft.Data.SqlClient;

namespace TraceOps.SqlServer.Tests;

internal static class SqlServerFixture
{
    public const string RuntimeConnectionKey = "ConnectionStrings__TraceOpsSqlServer";
    public const string FixtureConnectionKey = "M4_SQLSERVER_FIXTURE_CONNECTION";
    public const string DedicatedDatabaseName = "TraceOpsLabDB";
    public const string OwnershipMarkerName = "TraceOps.M4.Owner";
    public const string OwnershipMarkerValue = "TraceOpsLabDB:M4-SQL-INTEGRATION:v1";

    public static IReadOnlyDictionary<string, string?> ReadProcessEnvironment() =>
        SqlSecretEnvironment.Read();

    public static TargetConfiguration RequireConfiguredTarget(
        IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var runtime = ParseRequiredConnection(environment, RuntimeConnectionKey);
        var fixture = ParseRequiredConnection(environment, FixtureConnectionKey);

        RequireDedicatedDatabase(runtime, RuntimeConnectionKey);
        RequireDedicatedDatabase(fixture, FixtureConnectionKey);

        if (!string.Equals(runtime.DataSource, fixture.DataSource, StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlFixturePrerequisiteException(
                "Fixture and runtime connections must target the same server; connection values redacted.");
        }

        return new TargetConfiguration(runtime.DataSource, runtime.InitialCatalog);
    }

    public static void RequireOwnedDatabaseIdentity(
        string databaseName,
        string markerName,
        string markerValue)
    {
        if (!string.Equals(databaseName, DedicatedDatabaseName, StringComparison.Ordinal)
            || !string.Equals(markerName, OwnershipMarkerName, StringComparison.Ordinal)
            || !string.Equals(markerValue, OwnershipMarkerValue, StringComparison.Ordinal))
        {
            throw new SqlFixturePrerequisiteException(
                "Dedicated database ownership marker mismatch; no test schema may be created.");
        }
    }

    public static string CreateSchemaName(Guid runId)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty run identifier is required.", nameof(runId));
        }

        return $"m4_{runId:N}";
    }

    public static string QuoteSchemaName(string schemaName)
    {
        ArgumentNullException.ThrowIfNull(schemaName);
        if (!IsGeneratedSchemaName(schemaName))
        {
            throw new SqlFixturePrerequisiteException(
                "Schema name is outside the generated M4 allowlist; no SQL identifier was accepted.");
        }

        return $"[{schemaName}]";
    }

    public static bool IsGeneratedSchemaName(string schemaName)
    {
        if (schemaName.Length != 35
            || !schemaName.StartsWith("m4_", StringComparison.Ordinal)
            || !Guid.TryParseExact(schemaName[3..], "N", out var runId)
            || runId == Guid.Empty)
        {
            return false;
        }

        return string.Equals(schemaName, CreateSchemaName(runId), StringComparison.Ordinal);
    }

    private static SqlConnectionStringBuilder ParseRequiredConnection(
        IReadOnlyDictionary<string, string?> environment,
        string key)
    {
        if (!environment.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new SqlFixturePrerequisiteException(
                $"SQL target prerequisite missing: environment variable '{key}' is not set; values redacted.");
        }

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(value);
        }
        catch (ArgumentException)
        {
            throw new SqlFixturePrerequisiteException(
                $"SQL target configuration in '{key}' is invalid; value redacted.");
        }

        if (string.IsNullOrWhiteSpace(builder.DataSource)
            || string.IsNullOrWhiteSpace(builder.InitialCatalog)
            || !string.IsNullOrWhiteSpace(builder.AttachDBFilename))
        {
            throw new SqlFixturePrerequisiteException(
                $"SQL target configuration in '{key}' is incomplete or unsupported; value redacted.");
        }

        return builder;
    }

    private static void RequireDedicatedDatabase(SqlConnectionStringBuilder builder, string key)
    {
        if (!string.Equals(builder.InitialCatalog, DedicatedDatabaseName, StringComparison.Ordinal))
        {
            throw new SqlFixturePrerequisiteException(
                $"SQL target in '{key}' must name dedicated database '{DedicatedDatabaseName}'; value redacted.");
        }
    }

    internal sealed class TargetConfiguration(string dataSource, string databaseName)
    {
        public string DataSource { get; } = dataSource;
        public string DatabaseName { get; } = databaseName;

        public override string ToString() => "[redacted SQL target configuration]";
    }
}

internal sealed class SqlFixturePrerequisiteException(string message) : Exception(message)
{
}
