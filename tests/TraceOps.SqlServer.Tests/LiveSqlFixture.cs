using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using TraceOps.Domain;
using TraceOps.SqlServer;
using Xunit;

namespace TraceOps.SqlServer.Tests;

internal sealed class LiveSqlFixture : IAsyncDisposable
{
    private readonly string _admin;
    private readonly string _runtime;
    private readonly Guid _run = Guid.NewGuid();
    private bool _owned;
    private static readonly object EvidenceLock = new();
    internal string Schema { get; }
    internal string Q => SqlServerFixture.QuoteSchemaName(Schema);
    internal static readonly DateTimeOffset Time = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);
    internal static string Tracking() => "T" + Guid.NewGuid().ToString("N")[..30].ToUpperInvariant();
    private LiveSqlFixture(IReadOnlyDictionary<string, string?> environment)
    {
        SqlServerFixture.RequireConfiguredTarget(environment);
        _admin = Normalize(environment[SqlServerFixture.FixtureConnectionKey]!);
        _runtime = Normalize(environment[SqlServerFixture.RuntimeConnectionKey]!);
        Schema = SqlServerFixture.CreateSchemaName(_run);
    }
    private static string Normalize(string value)
    {
        var builder = new SqlConnectionStringBuilder(value) { Pooling = false, ConnectTimeout = 8, PersistSecurityInfo = false };
        return builder.ConnectionString;
    }
    internal string ApplicationPrefix => $"TraceOps.M4.{Schema}.";
    internal string WorkerApplicationName(int worker) => ApplicationPrefix + worker;
    internal void RecordProbe(string stage, object detail) => Evidence(stage, detail);
    internal SqlServerShipmentStore Store(int? worker = null)
    {
        var builder = new SqlConnectionStringBuilder(_runtime) { ApplicationName = ApplicationPrefix + worker };
        return new(builder.ConnectionString, Schema);
    }
    internal static async Task<LiveSqlFixture> Create()
    {
        var fixture = new LiveSqlFixture(SqlServerFixture.ReadProcessEnvironment());
        try { await fixture.Initialize(); return fixture; }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }
    internal static async Task VerifyTargetOnly()
    {
        var fixture = new LiveSqlFixture(SqlServerFixture.ReadProcessEnvironment());
        await using var admin = await fixture.OpenAdmin();
        await fixture.VerifyDatabase(admin);
        await using var runtime = await fixture.OpenRuntime();
        await fixture.VerifyRuntime(runtime);
    }
    private async Task Initialize()
    {
        var migrationPath = FindMigration();
        if (!File.Exists(migrationPath)) throw new SqlFixturePrerequisiteException("Product migration absent; setup blocked, not behavior RED.");
        var script = File.ReadAllText(migrationPath);
        if (!script.Contains("$(Schema)", StringComparison.Ordinal))
            throw new SqlFixturePrerequisiteException("Product migration must use the explicit $(Schema) placeholder.");
        script = script.Replace("$(Schema)", Schema, StringComparison.Ordinal);
        if (script.Contains("$(", StringComparison.Ordinal) || Regex.IsMatch(script, @"(?im)^\s*(USE\s|:|GO\s+\d)"))
            throw new SqlFixturePrerequisiteException("Unsupported migration directive; setup refused.");
        await using var admin = await OpenAdmin();
        await VerifyDatabase(admin); // No DDL precedes this identity and marker proof.
        await using var runtime = await OpenRuntime();
        var user = await VerifyRuntime(runtime);
        Assert.Equal(0, Convert.ToInt32(await Scalar(admin, "SELECT COUNT(*) FROM sys.schemas WHERE name=@s", ("@s", Schema))));
        await Execute(admin, $"CREATE SCHEMA {Q} AUTHORIZATION dbo");
        await Execute(admin, "EXEC sys.sp_addextendedproperty @name=N'TraceOps.M4.Run', @value=@v, @level0type=N'SCHEMA', @level0name=@s",
            ("@v", _run.ToString("D")), ("@s", Schema));
        _owned = true;
        foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*(?:--[^\r\n]*)?\r?$").Where(x => !string.IsNullOrWhiteSpace(x)))
            await Execute(admin, batch);
        // Grant only to the real runtime database user; no user/login creation or global roles.
        if (user.Length is < 1 or > 128) throw new SqlFixturePrerequisiteException("Runtime database principal metadata invalid.");
        var sidMatches = Convert.ToInt32(await Scalar(admin,
            "SELECT COUNT(*) FROM sys.database_principals WHERE name=@u AND sid=SUSER_SID(@login)",
            ("@u", user), ("@login", await Scalar(runtime, "SELECT ORIGINAL_LOGIN()"))));
        if (sidMatches != 1) throw new SqlFixturePrerequisiteException("Runtime principal SID mapping not verified.");
        await Execute(admin, $"GRANT EXECUTE ON SCHEMA::{Q} TO [{user.Replace("]", "]]", StringComparison.Ordinal)}]");
        Evidence("initialized", new { migrationSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(migrationPath))) });
    }
    private static string FindMigration()
    {
        var root = typeof(LiveSqlFixture).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(x => x.Key == "TraceOpsRepositoryRoot").Value!;
        var productPath = Path.GetFullPath(Path.Combine(root, "src/TraceOps.SqlServer/Migrations/001_M4.sql"));
        if (File.Exists(productPath)) return productPath;
        // Build output is external; walk cwd as well, without copying product SQL into tests.
        for (var current = new DirectoryInfo(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src/TraceOps.SqlServer/Migrations/001_M4.sql");
            if (Directory.Exists(Path.Combine(current.FullName, "src/TraceOps.SqlServer"))) return candidate;
        }
        throw new SqlFixturePrerequisiteException("Run from repository root or set test RunSettings working directory to repository; product migration unavailable.");
    }
    internal async Task<SqlConnection> OpenAdmin() => await Open(_admin);
    internal async Task<SqlConnection> OpenRuntime(string? applicationSuffix = null)
    {
        var builder = new SqlConnectionStringBuilder(_runtime) { ApplicationName = ApplicationPrefix + (applicationSuffix ?? "fixture") };
        return await Open(builder.ConnectionString);
    }
    private static async Task<SqlConnection> Open(string value)
    {
        var connection = new SqlConnection(value);
        try { await connection.OpenAsync(); return connection; }
        catch (Exception error)
        {
            await connection.DisposeAsync();
            throw new SqlFixturePrerequisiteException($"SQL connection prerequisite failed ({SafeCode(error)}); details redacted.");
        }
    }
    private async Task VerifyDatabase(SqlConnection connection)
    {
        using var command = Command(connection, "SELECT DB_NAME(),CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion')),CONVERT(int,SERVERPROPERTY('EngineEdition')),(SELECT CONVERT(nvarchar(256),value) FROM sys.extended_properties WHERE class=0 AND name=@m),CONVERT(nvarchar(128),SERVERPROPERTY('Edition')),CONVERT(nvarchar(128),CONNECTIONPROPERTY('local_net_address')),CONVERT(int,CONNECTIONPROPERTY('local_tcp_port'))");
        command.Parameters.AddWithValue("@m", SqlServerFixture.OwnershipMarkerName);
        try
        {
            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync() || reader.IsDBNull(3)) throw new SqlFixturePrerequisiteException("Database identity/ownership prerequisite failed.");
            SqlServerFixture.RequireOwnedDatabaseIdentity(reader.GetString(0), SqlServerFixture.OwnershipMarkerName, reader.GetString(3));
            if (reader.GetInt32(2) != 3 || !reader.GetString(1).StartsWith("16.", StringComparison.Ordinal))
                throw new SqlFixturePrerequisiteException("Expected SQL Server 16 engine identity was not verified.");
            var endpoint = new SqlConnectionStringBuilder(_admin).DataSource;
            var edition = reader.GetString(4);
            if (!edition.StartsWith("Developer Edition", StringComparison.Ordinal)
                || endpoint != "127.0.0.1,14331" || reader.IsDBNull(6) || reader.GetInt32(6) != 1433)
                throw new SqlFixturePrerequisiteException("Approved Developer/local endpoint identity was not verified; values redacted.");
            Evidence("database-verified", new { database = reader.GetString(0), productVersion = reader.GetString(1), engineEdition = reader.GetInt32(2),
                edition, configuredHost = "127.0.0.1", configuredPort = 14331, serverLocalAddress = reader.IsDBNull(5) ? null : reader.GetString(5), serverLocalPort = reader.GetInt32(6) });
        }
        catch (SqlException error) { throw new SqlFixturePrerequisiteException($"Database identity read failed (SQL {error.Number}); details redacted."); }
    }
    private async Task<string> VerifyRuntime(SqlConnection connection)
    {
        using var command = Command(connection, "SELECT DB_NAME(),USER_NAME(),IS_SRVROLEMEMBER('sysadmin'),IS_MEMBER('db_owner'),HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CONNECT'),HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CREATE TABLE'),HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CREATE SCHEMA'),HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CREATE PROCEDURE')");
        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.GetString(0) != SqlServerFixture.DedicatedDatabaseName
            || reader.GetString(1) == "dbo" || reader.IsDBNull(2) || reader.GetInt32(2) != 0 || reader.IsDBNull(3) || reader.GetInt32(3) != 0)
            throw new SqlFixturePrerequisiteException("Runtime identity must be dedicated database user without sysadmin/db_owner membership.");
        if (reader.IsDBNull(4) || reader.GetInt32(4) != 1
            || Enumerable.Range(5, 3).Any(i => reader.IsDBNull(i) || reader.GetInt32(i) != 0))
            throw new SqlFixturePrerequisiteException("Runtime CONNECT/DDL permission prerequisite failed; values redacted.");
        Evidence("runtime-principal-verified", new { connect = 1, sysadmin = 0, dbOwner = 0, createTable = 0, createSchema = 0, createProcedure = 0 });
        return reader.GetString(1);
    }
    internal static SqlCommand Command(SqlConnection connection, string sql, SqlTransaction? transaction = null) =>
        new(sql, connection, transaction) { CommandTimeout = 10 };
    internal static async Task<object?> Scalar(SqlConnection connection, string sql, params (string Name, object? Value)[] values)
    {
        using var command = Command(connection, sql);
        foreach (var item in values) command.Parameters.AddWithValue(item.Name, item.Value ?? DBNull.Value);
        try { return await command.ExecuteScalarAsync(); }
        catch (SqlException error) { throw new SqlFixturePrerequisiteException($"SQL fixture scalar failed ({error.Number}); details redacted."); }
    }
    internal static async Task Execute(SqlConnection connection, string sql, params (string Name, object? Value)[] values)
    {
        using var command = Command(connection, sql);
        foreach (var item in values) command.Parameters.AddWithValue(item.Name, item.Value ?? DBNull.Value);
        try { await command.ExecuteNonQueryAsync(); }
        catch (SqlException error) { throw new SqlFixturePrerequisiteException($"SQL fixture statement failed ({error.Number}); details redacted."); }
    }
    internal SqlCommand Procedure(SqlConnection connection, string name, SqlTransaction? transaction = null)
    {
        if (name is not ("Shipment_List" or "Shipment_Find" or "Shipment_TryRegister" or "Shipment_AppendEvent")) throw new ArgumentException("Unknown procedure.");
        var command = Command(connection, $"{Q}.[{name}]", transaction);
        command.CommandType = CommandType.StoredProcedure;
        return command;
    }
    internal static SqlParameter Wide(SqlCommand command, string name, string? value) =>
        command.Parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, -1) { Value = (object?)value ?? DBNull.Value });
    internal static void Timestamp(SqlCommand command, string name, DateTimeOffset time) =>
        command.Parameters.Add(new SqlParameter(name, SqlDbType.DateTimeOffset) { Scale = 7, Value = time });
    internal async Task<string> Register(string? tracking, DateTimeOffset? at = null)
    {
        await using var connection = await OpenRuntime();
        using var command = Procedure(connection, "Shipment_TryRegister");
        Wide(command, "@TrackingNumber", tracking);
        Timestamp(command, "@RegisteredAt", at ?? Time);
        var output = command.Parameters.Add("@ResultCode", SqlDbType.VarChar, 32);
        output.Direction = ParameterDirection.Output;
        try { await command.ExecuteNonQueryAsync(); }
        catch (SqlException error) { throw new Exception($"Register storage fault SQL {error.Number}; details redacted."); }
        return output.Value as string ?? "<NULL>";
    }
    internal SqlCommand AppendCommand(SqlConnection connection, SqlTransaction? transaction, LifecycleEvent input, long version = 0)
    {
        var command = Procedure(connection, "Shipment_AppendEvent", transaction);
        Wide(command, "@TrackingNumber", input.TrackingNumber);
        command.Parameters.Add("@ExpectedVersion", SqlDbType.BigInt).Value = version;
        command.Parameters.Add("@EventId", SqlDbType.UniqueIdentifier).Value = input.EventId;
        command.Parameters.Add("@Status", SqlDbType.Int).Value = (int)input.Status;
        Timestamp(command, "@OccurredAt", input.OccurredAt);
        Wide(command, "@SourceSystem", input.SourceSystem);
        command.Parameters.Add("@ResultCode", SqlDbType.VarChar, 24).Direction = ParameterDirection.Output;
        return command;
    }
    internal static async Task<int?> SqlError(Func<Task> action)
    {
        try { await action(); return null; }
        catch (SqlException error) { return error.Number; }
    }
    internal static LifecycleEvent Event(string tracking, ShipmentStatus status = ShipmentStatus.Accepted, string source = "SRC", Guid? id = null, DateTimeOffset? at = null) =>
        new(id ?? Guid.NewGuid(), tracking, status, at ?? Time.AddTicks(1), source);
    internal async Task<long> EventCount(string tracking)
    {
        await using var connection = await OpenAdmin();
        return Convert.ToInt64(await Scalar(connection, $"SELECT COUNT_BIG(*) FROM {Q}.ShipmentEvents WHERE TrackingNumber=@t", ("@t", tracking)));
    }
    internal async Task Seed(string tracking)
    {
        await using var connection = await OpenAdmin();
        await Execute(connection, $"INSERT {Q}.Shipments(TrackingNumber,RegisteredAt,Status,LastOccurredAt,Version) VALUES(@t,@at,0,@at,0)",
            ("@t", tracking), ("@at", Time));
    }
    private static string SafeCode(Exception error) => error is SqlException sql ? $"SQL {sql.Number}" : error.GetType().Name;
    private void Evidence(string stage, object detail)
    {
        var path = Environment.GetEnvironmentVariable("M4_SQLSERVER_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (EvidenceLock)
        {
            Directory.CreateDirectory(path);
            File.AppendAllText(Path.Combine(path, "fixture-events.jsonl"), JsonSerializer.Serialize(new { schema = Schema, stage, detail }) + "\n");
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (!_owned) return;
        await using var admin = await OpenAdmin();
        await VerifyDatabase(admin);
        var marker = await Scalar(admin, "SELECT CONVERT(nvarchar(128),value) FROM sys.extended_properties WHERE class=3 AND major_id=SCHEMA_ID(@s) AND name=N'TraceOps.M4.Run'", ("@s", Schema));
        if (!string.Equals(marker as string, _run.ToString("D"), StringComparison.Ordinal))
            throw new SqlFixturePrerequisiteException("Cleanup refused: exact schema ownership marker mismatch.");
        var known = new HashSet<string>(StringComparer.Ordinal) { "SchemaVersion", "Shipments", "ShipmentEvents", "Shipment_List", "Shipment_Find", "Shipment_TryRegister", "Shipment_AppendEvent", "M4RollbackFault" };
        using (var command = Command(admin, "SELECT name,type FROM sys.objects WHERE schema_id=SCHEMA_ID(@s) AND parent_object_id=0"))
        {
            command.Parameters.AddWithValue("@s", Schema);
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                if (!known.Contains(reader.GetString(0))) throw new SqlFixturePrerequisiteException("Cleanup refused: unexpected object outside exact owned allowlist.");
        }
        foreach (var name in new[] { "Shipment_List", "Shipment_Find", "Shipment_TryRegister", "Shipment_AppendEvent" })
            await Execute(admin, $"DROP PROCEDURE IF EXISTS {Q}.[{name}]");
        await Execute(admin, $"DROP TRIGGER IF EXISTS {Q}.[M4RollbackFault]");
        foreach (var name in new[] { "ShipmentEvents", "Shipments", "SchemaVersion" })
            await Execute(admin, $"DROP TABLE IF EXISTS {Q}.[{name}]");
        await Execute(admin, $"DROP SCHEMA {Q}");
        var residue = Convert.ToInt32(await Scalar(admin, "SELECT COUNT(*) FROM sys.schemas WHERE name=@s", ("@s", Schema)));
        Evidence("cleanup", new { residue });
        if (residue != 0) throw new SqlFixturePrerequisiteException("Owned schema residue remains after cleanup.");
        _owned = false;
    }
}
