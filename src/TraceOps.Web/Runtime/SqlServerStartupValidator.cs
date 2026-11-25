using System.Data;
using Microsoft.Data.SqlClient;
using TraceOps.SqlServer;

namespace TraceOps.Web.Runtime;

internal static class SqlServerStartupValidator
{
    private const string StartupFailureMessage = "SQL Server startup validation failed.";

    internal static SqlServerShipmentStore CreateValidatedStore(IConfiguration configuration)
    {
        var connectionString = RequireSetting(
            configuration.GetConnectionString("TraceOpsSqlServer"),
            "ConnectionStrings:TraceOpsSqlServer");
        var expectedDatabase = RequireSetting(
            configuration["TraceOps:SqlServer:ExpectedDatabase"],
            "TraceOps:SqlServer:ExpectedDatabase");
        var expectedServerName = RequireSetting(
            configuration["TraceOps:SqlServer:ExpectedServerName"],
            "TraceOps:SqlServer:ExpectedServerName");
        var schema = configuration["TraceOps:SqlServer:Schema"] ?? "trace";

        try
        {
            SqlServerShipmentStore store;
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                ValidateTargetIdentity(connection, expectedDatabase, expectedServerName);

                store = new SqlServerShipmentStore(connectionString, schema);
                ValidateRequiredProcedures(connection, schema);
            }

            _ = store.List();
            return store;
        }
        catch
        {
            throw new InvalidOperationException(StartupFailureMessage);
        }
    }

    private static string RequireSetting(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Required configuration '{key}' is missing or blank.");
        }

        return value;
    }

    private static void ValidateTargetIdentity(
        SqlConnection connection,
        string expectedDatabase,
        string expectedServerName)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DB_NAME(), CONVERT(nvarchar(128), SERVERPROPERTY('ServerName'));";

        using var reader = command.ExecuteReader(CommandBehavior.SingleRow);
        if (!reader.Read()
            || reader.IsDBNull(0)
            || reader.IsDBNull(1)
            || !string.Equals(reader.GetString(0), expectedDatabase, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), expectedServerName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(StartupFailureMessage);
        }
    }

    private static void ValidateRequiredProcedures(SqlConnection connection, string schema)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM sys.procedures AS procedureMetadata
            INNER JOIN sys.schemas AS schemaMetadata
                ON schemaMetadata.schema_id = procedureMetadata.schema_id
            WHERE schemaMetadata.name = @Schema
              AND procedureMetadata.name IN
                  (N'Shipment_List', N'Shipment_Find', N'Shipment_TryRegister', N'Shipment_AppendEvent');
            """;
        command.Parameters.Add("@Schema", SqlDbType.NVarChar, 128).Value = schema;

        if (command.ExecuteScalar() is not long procedureCount || procedureCount != 4)
        {
            throw new InvalidOperationException(StartupFailureMessage);
        }
    }
}
