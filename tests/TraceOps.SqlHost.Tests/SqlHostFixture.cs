using System.Data;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using TraceOps.SqlServer.Tests;

namespace TraceOps.SqlHost.Tests;

internal sealed class SqlHostFixture : IAsyncDisposable
{
    // Test-only expected identity: the SQL Server container hostname set in
    // docker-compose.yml and in the CI service definition.
    internal const string ExpectedLabServerName = "traceops-sql";
    internal static string Metadata(string key) => typeof(SqlHostFixture).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(x => x.Key == key).Value!;
    private readonly Guid _run = Guid.NewGuid();
    private readonly string _admin;
    internal string Runtime { get; }
    internal string Schema { get; }
    internal string Q => SqlServerFixture.QuoteSchemaName(Schema);
    private bool _owned;
    private SqlHostFixture(IReadOnlyDictionary<string,string?> environment)
    {
        SqlServerFixture.RequireConfiguredTarget(environment);
        _admin = environment[SqlServerFixture.FixtureConnectionKey]!;
        Runtime = environment[SqlServerFixture.RuntimeConnectionKey]!;
        Schema = SqlServerFixture.CreateSchemaName(_run);
    }
    internal static async Task<SqlHostFixture> Create()
    {
        var fixture = new SqlHostFixture(SqlServerFixture.ReadProcessEnvironment());
        try { await fixture.Initialize(); return fixture; }
        catch(Exception setupError)
        {
            try { await fixture.DisposeAsync(); }
            catch(Exception cleanupError)
            {
                throw new AggregateException("Host fixture setup and guarded cleanup both failed.",setupError,cleanupError);
            }
            throw;
        }
    }
    internal async Task<SqlConnection> Admin()
    {
        var connection = new SqlConnection(_admin);
        try
        {
            await connection.OpenAsync();
            using var proof = new SqlCommand("SELECT DB_NAME(),CONVERT(nvarchar(256),value) FROM sys.extended_properties WHERE class=0 AND name=@marker",connection);
            proof.Parameters.AddWithValue("@marker", SqlServerFixture.OwnershipMarkerName);
            using var reader = await proof.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new InvalidOperationException();
            SqlServerFixture.RequireOwnedDatabaseIdentity(reader.GetString(0),SqlServerFixture.OwnershipMarkerName,reader.GetString(1));
            await reader.DisposeAsync();
            using var engine=new SqlCommand("SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')),CONVERT(int,SERVERPROPERTY('EngineEdition')),CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion'))",connection);
            using var engineReader=await engine.ExecuteReaderAsync();
            if(!await engineReader.ReadAsync()||engineReader.IsDBNull(0)
                ||!string.Equals(engineReader.GetString(0),ExpectedLabServerName,StringComparison.Ordinal)
                ||engineReader.IsDBNull(1)||engineReader.GetInt32(1)!=3
                ||engineReader.IsDBNull(2)||!engineReader.GetString(2).StartsWith("16.",StringComparison.Ordinal))
                throw new InvalidOperationException();
            return connection;
        }
        catch { await connection.DisposeAsync(); throw new SqlFixturePrerequisiteException("Host fixture DB identity/ownership prerequisite failed; details redacted."); }
    }
    internal async Task Execute(string sql)
    {
        await using var c = await Admin();
        using var cmd = new SqlCommand(sql,c) { CommandTimeout=10 };
        try { await cmd.ExecuteNonQueryAsync(); }
        catch(SqlException e) { throw new SqlFixturePrerequisiteException($"Host fixture statement failed SQL {e.Number}; details redacted."); }
    }
    private async Task Initialize()
    {
        var path = Path.GetFullPath(Path.Combine(Metadata("HostRepository"),"src/TraceOps.SqlServer/Migrations/001_M4.sql"));
        if(!File.Exists(path)) throw new SqlFixturePrerequisiteException("Product migration prerequisite absent.");
        var script=File.ReadAllText(path);
        if(!script.Contains("$(Schema)",StringComparison.Ordinal)) throw new SqlFixturePrerequisiteException("Product schema template prerequisite absent.");
        script=script.Replace("$(Schema)",Schema,StringComparison.Ordinal);
        if(script.Contains("$(",StringComparison.Ordinal)||Regex.IsMatch(script,@"(?im)^\s*(USE\s|:|GO\s+\d)")) throw new SqlFixturePrerequisiteException("Unsupported migration directive.");
        await using(var c=await Admin())
        {
            using var transaction=c.BeginTransaction();
            using var create=new SqlCommand($"CREATE SCHEMA {Q} AUTHORIZATION dbo",c,transaction);
            await create.ExecuteNonQueryAsync();
            using var marker=new SqlCommand("EXEC sys.sp_addextendedproperty @name=N'TraceOps.M4.Run',@value=@run,@level0type=N'SCHEMA',@level0name=@schema",c,transaction);
            marker.Parameters.AddWithValue("@run",_run.ToString("D"));marker.Parameters.AddWithValue("@schema",Schema);
            await marker.ExecuteNonQueryAsync();transaction.Commit();_owned=true;
        }
        foreach(var batch in Regex.Split(script,@"(?im)^\s*GO\s*(?:--[^\r\n]*)?\r?$").Where(x=>!string.IsNullOrWhiteSpace(x))) await Execute(batch);
        var procedures=new[]{"Shipment_List","Shipment_Find","Shipment_TryRegister","Shipment_AppendEvent"};
        await using var runtime=new SqlConnection(Runtime);
        try
        {
            await runtime.OpenAsync();
            int runtimePrincipalId;
            string runtimePrincipalName;
            using(var identity=new SqlCommand("SELECT DB_NAME(),CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')),USER_ID(),USER_NAME(),IS_SRVROLEMEMBER('sysadmin'),IS_MEMBER('db_owner')",runtime))
            using(var identityReader=await identity.ExecuteReaderAsync())
            {
                if(!await identityReader.ReadAsync()||identityReader.GetString(0)!=SqlServerFixture.DedicatedDatabaseName
                    ||identityReader.IsDBNull(1)||!string.Equals(identityReader.GetString(1),ExpectedLabServerName,StringComparison.Ordinal)
                    ||identityReader.IsDBNull(2)||identityReader.IsDBNull(3)||identityReader.IsDBNull(4)||identityReader.GetInt32(4)!=0
                    ||identityReader.IsDBNull(5)||identityReader.GetInt32(5)!=0)
                    throw new InvalidOperationException();
                runtimePrincipalId=identityReader.GetInt16(2);
                runtimePrincipalName=identityReader.GetString(3);
            }
            await GrantRuntimeProcedureExecute(runtimePrincipalId,runtimePrincipalName,procedures);
            using var command=new SqlCommand("SELECT DB_NAME(),IS_SRVROLEMEMBER('sysadmin'),IS_MEMBER('db_owner'),HAS_PERMS_BY_NAME(@proc,'OBJECT','EXECUTE')",runtime);
            command.Parameters.AddWithValue("@proc",Schema+".Shipment_List");
            using var reader=await command.ExecuteReaderAsync();
            if(!await reader.ReadAsync()||reader.GetString(0)!=SqlServerFixture.DedicatedDatabaseName||reader.GetInt32(1)!=0||reader.GetInt32(2)!=0||reader.IsDBNull(3)||reader.GetInt32(3)!=1)
                throw new InvalidOperationException();
        }
        catch { throw new SqlFixturePrerequisiteException("Limited runtime identity or fixture procedure EXECUTE setup failed; details redacted."); }
        foreach(var procedure in procedures)
        {
            using var check=new SqlCommand("SELECT HAS_PERMS_BY_NAME(@object,'OBJECT','EXECUTE')",runtime);check.Parameters.AddWithValue("@object",Schema+"."+procedure);
            if(!Equals(await check.ExecuteScalarAsync(),1))throw new SqlFixturePrerequisiteException("Runtime lacks fixture procedure EXECUTE permission.");
        }
        foreach(var table in new[]{"SchemaVersion","Shipments","ShipmentEvents"})
        foreach(var permission in new[]{"SELECT","INSERT","UPDATE","DELETE","ALTER","CONTROL"})
        {
            using var check=new SqlCommand("SELECT HAS_PERMS_BY_NAME(@object,'OBJECT',@permission)",runtime);
            check.Parameters.AddWithValue("@object",Schema+"."+table);check.Parameters.AddWithValue("@permission",permission);
            if(!Equals(await check.ExecuteScalarAsync(),0))throw new SqlFixturePrerequisiteException("Runtime must have no direct table SELECT/DML/DDL permission.");
        }
    }
    private async Task GrantRuntimeProcedureExecute(int principalId,string principalName,IReadOnlyList<string> procedures)
    {
        if(!SqlServerFixture.IsGeneratedSchemaName(Schema)
            ||!string.Equals(Schema,SqlServerFixture.CreateSchemaName(_run),StringComparison.Ordinal)
            ||principalId<=4||string.IsNullOrWhiteSpace(principalName))
            throw new SqlFixturePrerequisiteException("Runtime principal or fixture schema is outside the grant allowlist.");

        await using var c=await Admin();
        using(var schemaProof=new SqlCommand("SELECT CONVERT(nvarchar(128),value) FROM sys.extended_properties WHERE class=3 AND major_id=SCHEMA_ID(@schema) AND name=N'TraceOps.M4.Run'",c))
        {
            schemaProof.Parameters.AddWithValue("@schema",Schema);
            if(!Equals(await schemaProof.ExecuteScalarAsync(),_run.ToString("D")))
                throw new SqlFixturePrerequisiteException("Fixture schema ownership proof failed before permission grant.");
        }
        using(var principalProof=new SqlCommand("SELECT COUNT(*) FROM sys.database_principals WHERE principal_id=@principalId AND name=@principalName AND principal_id>4 AND type IN ('S','U','G','E','X') AND is_fixed_role=0",c))
        {
            principalProof.Parameters.AddWithValue("@principalId",principalId);
            principalProof.Parameters.AddWithValue("@principalName",principalName);
            if(!Equals(await principalProof.ExecuteScalarAsync(),1))
                throw new SqlFixturePrerequisiteException("Runtime database principal is not an existing grantable user.");
        }

        using var transaction=c.BeginTransaction();
        foreach(var procedure in procedures)
        {
            using var grant=new SqlCommand("DECLARE @statement nvarchar(max)=N'GRANT EXECUTE ON OBJECT::'+QUOTENAME(@schema)+N'.'+QUOTENAME(@object)+N' TO '+QUOTENAME(@principal)+N';'; EXEC sys.sp_executesql @statement",c,transaction);
            grant.Parameters.AddWithValue("@schema",Schema);
            grant.Parameters.AddWithValue("@object",procedure);
            grant.Parameters.AddWithValue("@principal",principalName);
            await grant.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }
    internal async Task<string> Snapshot()
    {
        await using var c=await Admin();
        var result=new List<string>();
        using(var objects=new SqlCommand("SELECT name,type,create_date,modify_date,OBJECT_DEFINITION(object_id) AS definition FROM sys.objects WHERE schema_id=SCHEMA_ID(@schema) ORDER BY name,type FOR JSON PATH,INCLUDE_NULL_VALUES",c))
        {
            objects.Parameters.AddWithValue("@schema",Schema);using var reader=await objects.ExecuteReaderAsync();
            var text=new System.Text.StringBuilder();while(await reader.ReadAsync())text.Append(reader.GetString(0));result.Add(text.ToString());
        }
        foreach(var (table,order) in new[]{("SchemaVersion","Version,MigrationId"),("Shipments","TrackingNumber"),("ShipmentEvents","TrackingNumber,EventSequence")})
        {
            using var command=new SqlCommand($"SELECT * FROM {Q}.[{table}] ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES",c);
            using var reader=await command.ExecuteReaderAsync();
            var text=new System.Text.StringBuilder();while(await reader.ReadAsync())text.Append(reader.GetString(0));
            result.Add(text.ToString());
        }
        return string.Join("\n",result);
    }
    internal async Task PoisonListWithOwnedInvocationCounter()
    {
        // A SEQUENCE increment survives the throwing procedure's transaction rollback. No shared object is used.
        await Execute($"CREATE SEQUENCE {Q}.[HostListInvocation] AS bigint START WITH 1 INCREMENT BY 1 NO CACHE");
        await Execute($"ALTER PROCEDURE {Q}.Shipment_List AS BEGIN SET NOCOUNT ON; DECLARE @invocation bigint=NEXT VALUE FOR {Q}.[HostListInvocation]; THROW 51171,'Owned host List invocation sentinel',1; END");
    }
    internal async Task<long?> ListInvocation()
    {
        await using var c=await Admin();
        using var command=new SqlCommand("SELECT CONVERT(bigint,last_used_value) FROM sys.sequences WHERE schema_id=SCHEMA_ID(@schema) AND name=N'HostListInvocation'",c);
        command.Parameters.AddWithValue("@schema",Schema);
        var value=await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }
    internal sealed record Parent(string Tracking,DateTimeOffset RegisteredAt,byte Status,DateTimeOffset LastOccurredAt,long Version);
    internal sealed record Event(string Tracking,long Sequence,Guid Id,byte Status,DateTimeOffset OccurredAt,string Source);
    internal sealed record State(Parent Parent,Event[] Events);
    internal async Task<State> ReadState(string tracking)
    {
        // New physical fixture connection for every observation; no cached host/domain state.
        var builder=new SqlConnectionStringBuilder(_admin){Pooling=false};
        await using var c=new SqlConnection(builder.ConnectionString);await c.OpenAsync();
        using var transaction=c.BeginTransaction(IsolationLevel.Serializable);
        using var command=new SqlCommand($"SELECT TrackingNumber,RegisteredAt,Status,LastOccurredAt,Version FROM {Q}.Shipments WHERE TrackingNumber=@t; SELECT TrackingNumber,EventSequence,EventId,Status,OccurredAt,SourceSystem FROM {Q}.ShipmentEvents WHERE TrackingNumber=@t ORDER BY EventSequence",c,transaction);
        command.Parameters.AddWithValue("@t",tracking);
        Parent parent;
        var events=new List<Event>();
        using(var reader=await command.ExecuteReaderAsync())
        {
            Xunit.Assert.True(await reader.ReadAsync(),"Target request did not persist the expected owned shipment row.");
            parent=new(reader.GetString(0),reader.GetFieldValue<DateTimeOffset>(1),reader.GetByte(2),reader.GetFieldValue<DateTimeOffset>(3),reader.GetInt64(4));
            if(await reader.ReadAsync())throw new InvalidOperationException("Duplicate owned parent.");
            if(!await reader.NextResultAsync())throw new InvalidOperationException("Owned history snapshot result missing.");
            while(await reader.ReadAsync())events.Add(new(reader.GetString(0),reader.GetInt64(1),reader.GetGuid(2),reader.GetByte(3),reader.GetFieldValue<DateTimeOffset>(4),reader.GetString(5)));
        }
        transaction.Commit();return new(parent,events.ToArray());
    }
    internal Dictionary<string,string?> Configuration()=>new()
    {
        ["TraceOps__Store"]="SqlServer",["ConnectionStrings__TraceOpsSqlServer"]=Runtime,
        // Nonsecret lab identity from the independently supplied identity artifact, not product defaults.
        ["TraceOps__SqlServer__ExpectedDatabase"]="TraceOpsLabDB",
        ["TraceOps__SqlServer__ExpectedServerName"]=ExpectedLabServerName,
        ["TraceOps__SqlServer__Schema"]=Schema,["TraceOps__Soap__Enabled"]="true",
        // Intentionally omit SeedDemoData: SQL mode must not auto-seed even with legacy default true.
    };
    public async ValueTask DisposeAsync()
    {
        if(!_owned)return;
        await using var c=await Admin();
        using var transaction=c.BeginTransaction(IsolationLevel.Serializable);
        using(var proof=new SqlCommand("SELECT CONVERT(nvarchar(128),value) FROM sys.extended_properties WHERE class=3 AND major_id=SCHEMA_ID(@s) AND name=N'TraceOps.M4.Run'",c,transaction))
        {
            proof.Parameters.AddWithValue("@s",Schema);
            if(!Equals(await proof.ExecuteScalarAsync(),_run.ToString("D")))throw new SqlFixturePrerequisiteException("Host schema cleanup ownership proof failed.");
        }
        var allow=new HashSet<string>(new[]{"SchemaVersion","Shipments","ShipmentEvents","Shipment_List","Shipment_Find","Shipment_TryRegister","Shipment_AppendEvent","HostListInvocation"},StringComparer.Ordinal);
        using(var scan=new SqlCommand("SELECT name FROM sys.objects WHERE schema_id=SCHEMA_ID(@s) AND parent_object_id=0",c,transaction))
        {
            scan.Parameters.AddWithValue("@s",Schema);using var reader=await scan.ExecuteReaderAsync();
            while(await reader.ReadAsync())if(!allow.Contains(reader.GetString(0)))throw new SqlFixturePrerequisiteException("Host cleanup refused unexpected owned object.");
        }
        foreach(var name in new[]{"Shipment_List","Shipment_Find","Shipment_TryRegister","Shipment_AppendEvent"})
        {
            using var drop=new SqlCommand($"DROP PROCEDURE IF EXISTS {Q}.[{name}]",c,transaction) { CommandTimeout=10 };
            await drop.ExecuteNonQueryAsync();
        }
        using(var drop=new SqlCommand($"DROP SEQUENCE IF EXISTS {Q}.[HostListInvocation]",c,transaction) { CommandTimeout=10 })
            await drop.ExecuteNonQueryAsync();
        foreach(var name in new[]{"ShipmentEvents","Shipments","SchemaVersion"})
        {
            using var drop=new SqlCommand($"DROP TABLE IF EXISTS {Q}.[{name}]",c,transaction) { CommandTimeout=10 };
            await drop.ExecuteNonQueryAsync();
        }
        using(var drop=new SqlCommand($"DROP SCHEMA {Q}",c,transaction) { CommandTimeout=10 })
            await drop.ExecuteNonQueryAsync();
        using var residue=new SqlCommand("SELECT COUNT(*) FROM sys.schemas WHERE name=@s",c,transaction);residue.Parameters.AddWithValue("@s",Schema);
        if(Convert.ToInt32(await residue.ExecuteScalarAsync())!=0)throw new SqlFixturePrerequisiteException("Host schema cleanup residue remains.");
        transaction.Commit();
        _owned=false;
    }
}
