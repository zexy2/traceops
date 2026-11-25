using TraceOps.Application;
using TraceOps.Domain;
using System.Data;
using System.Runtime.ExceptionServices;
using Microsoft.Data.SqlClient;

namespace TraceOps.SqlServer;

public sealed class SqlServerShipmentStore : IShipmentStore
{
    private readonly string _connectionString;
    private readonly string _quotedSchema;

    public SqlServerShipmentStore(string connectionString, string schema = "trace")
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentNullException.ThrowIfNull(schema);
        if (connectionString.Length == 0)
        {
            throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString));
        }

        if (!IsValidSchemaIdentifier(schema))
        {
            throw new ArgumentException("Schema must match [A-Za-z][A-Za-z0-9_]{0,127}.", nameof(schema));
        }

        _connectionString = connectionString;
        _quotedSchema = $"[{schema.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    public IReadOnlyList<ShipmentState> List()
    {
        using var connection = new SqlConnection(_connectionString);
        connection.Open();
        var states = ReadStates(connection, transaction: null, "Shipment_List", trackingNumber: null);
        return Array.AsReadOnly(states.ToArray());
    }

    public ShipmentState? Find(string trackingNumber)
    {
        using var connection = new SqlConnection(_connectionString);
        connection.Open();
        var states = ReadStates(connection, transaction: null, "Shipment_Find", trackingNumber);
        if (states.Count > 1)
        {
            throw IntegrityError("Shipment_Find returned more than one shipment.");
        }

        return states.Count == 0 ? null : states[0];
    }

    public bool TryAdd(ShipmentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Version != 0
            || state.Status != ShipmentStatus.Registered
            || state.Events.Count != 0
            || state.LastOccurredAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Only a new registered shipment state can be added.", nameof(state));
        }

        using var connection = new SqlConnection(_connectionString);
        connection.Open();
        using var command = CreateProcedure(connection, transaction: null, "Shipment_TryRegister");
        AddNVarCharMax(command, "@TrackingNumber", state.TrackingNumber);
        AddDateTimeOffset(command, "@RegisteredAt", state.LastOccurredAt);
        var resultCode = AddOutput(command, "@ResultCode", size: 32);

        command.ExecuteNonQuery();
        return ReadOutputCode(resultCode) switch
        {
            "REGISTERED" => true,
            "TRACKING_NUMBER_CONFLICT" => false,
            "INVALID_TRACKING_NUMBER" or "TIMESTAMP_MUST_BE_UTC" =>
                throw IntegrityError("Shipment_TryRegister rejected a domain-created registration state."),
            var code => throw IntegrityError($"Shipment_TryRegister returned unexpected result code '{code}'."),
        };
    }

    public ApplyEventResult? ApplyAtomically(LifecycleEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);

        using var connection = new SqlConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        try
        {
            var current = ReadStates(connection, transaction, "Shipment_Find", input.TrackingNumber);
            if (current.Count > 1)
            {
                throw IntegrityError("Shipment_Find returned more than one shipment.");
            }

            if (current.Count == 0)
            {
                transaction.Commit();
                return null;
            }

            var result = ShipmentLifecycle.Apply(current[0], input);
            if (result.Code == ApplyEventCode.Applied)
            {
                AppendEvent(connection, transaction, input, current[0].Version);
            }

            transaction.Commit();
            return result;
        }
        catch (Exception operationError)
        {
            try
            {
                transaction.Rollback();
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "SQL shipment operation failed and its transaction could not be rolled back.",
                    operationError,
                    rollbackError);
            }

            ExceptionDispatchInfo.Capture(operationError).Throw();
            throw;
        }
    }

    private void AppendEvent(
        SqlConnection connection,
        SqlTransaction transaction,
        LifecycleEvent input,
        long expectedVersion)
    {
        using var command = CreateProcedure(connection, transaction, "Shipment_AppendEvent");
        AddNVarCharMax(command, "@TrackingNumber", input.TrackingNumber);
        command.Parameters.Add("@ExpectedVersion", SqlDbType.BigInt).Value = expectedVersion;
        command.Parameters.Add("@EventId", SqlDbType.UniqueIdentifier).Value = input.EventId;
        command.Parameters.Add("@Status", SqlDbType.Int).Value = (int)input.Status;
        AddDateTimeOffset(command, "@OccurredAt", input.OccurredAt);
        AddNVarCharMax(command, "@SourceSystem", input.SourceSystem);
        var resultCode = AddOutput(command, "@ResultCode", size: 24);

        command.ExecuteNonQuery();
        var code = ReadOutputCode(resultCode);
        if (!string.Equals(code, "APPENDED", StringComparison.Ordinal))
        {
            throw IntegrityError($"Shipment_AppendEvent returned unexpected result code '{code}'.");
        }
    }

    private List<ShipmentState> ReadStates(
        SqlConnection connection,
        SqlTransaction? transaction,
        string procedure,
        string? trackingNumber)
    {
        using var command = CreateProcedure(connection, transaction, procedure);
        if (procedure == "Shipment_Find")
        {
            AddNVarCharMax(command, "@TrackingNumber", trackingNumber);
        }

        using var reader = command.ExecuteReader();
        RequireColumns(reader, "parent", ("TrackingNumber", typeof(string)),
            ("RegisteredAt", typeof(DateTimeOffset)), ("Status", typeof(byte)),
            ("LastOccurredAt", typeof(DateTimeOffset)), ("Version", typeof(long)));

        var parents = new List<ShipmentRow>();
        string? previousTrackingNumber = null;
        while (reader.Read())
        {
            var row = new ShipmentRow(
                ReadString(reader, 0, "TrackingNumber"),
                ReadDateTimeOffset(reader, 1, "RegisteredAt"),
                ReadStatus(reader, 2, "Status"),
                ReadDateTimeOffset(reader, 3, "LastOccurredAt"),
                ReadInt64(reader, 4, "Version"));

            if (!IsValidTrackingNumber(row.TrackingNumber)
                || row.RegisteredAt.Offset != TimeSpan.Zero
                || row.LastOccurredAt.Offset != TimeSpan.Zero
                || row.Version < 0)
            {
                throw IntegrityError("Shipment_List/Shipment_Find returned an invalid parent row.");
            }

            if (previousTrackingNumber is not null
                && StringComparer.Ordinal.Compare(previousTrackingNumber, row.TrackingNumber) >= 0)
            {
                throw IntegrityError("Shipment_List/Shipment_Find parent rows are duplicated or out of order.");
            }

            previousTrackingNumber = row.TrackingNumber;
            parents.Add(row);
        }

        if (!reader.NextResult())
        {
            throw IntegrityError("Shipment_List/Shipment_Find omitted its history result set.");
        }

        RequireColumns(reader, "history", ("TrackingNumber", typeof(string)),
            ("EventSequence", typeof(long)), ("EventId", typeof(Guid)), ("Status", typeof(byte)),
            ("OccurredAt", typeof(DateTimeOffset)), ("SourceSystem", typeof(string)));

        var events = new List<EventRow>();
        string? previousEventTrackingNumber = null;
        long previousEventSequence = 0;
        while (reader.Read())
        {
            var row = new EventRow(
                ReadString(reader, 0, "TrackingNumber"),
                ReadInt64(reader, 1, "EventSequence"),
                ReadGuid(reader, 2, "EventId"),
                ReadStatus(reader, 3, "Status"),
                ReadDateTimeOffset(reader, 4, "OccurredAt"),
                ReadString(reader, 5, "SourceSystem"));

            if (!IsValidTrackingNumber(row.TrackingNumber)
                || row.EventSequence <= 0
                || row.EventId == Guid.Empty
                || row.OccurredAt.Offset != TimeSpan.Zero
                || !IsValidSourceSystem(row.SourceSystem))
            {
                throw IntegrityError("Shipment_List/Shipment_Find returned an invalid history row.");
            }

            if (previousEventTrackingNumber is not null)
            {
                var trackingOrder = StringComparer.Ordinal.Compare(previousEventTrackingNumber, row.TrackingNumber);
                if (trackingOrder > 0 || (trackingOrder == 0 && row.EventSequence <= previousEventSequence))
                {
                    throw IntegrityError("Shipment_List/Shipment_Find history rows are duplicated or out of order.");
                }
            }

            previousEventTrackingNumber = row.TrackingNumber;
            previousEventSequence = row.EventSequence;
            events.Add(row);
        }

        if (reader.NextResult())
        {
            throw IntegrityError("Shipment_List/Shipment_Find returned an unexpected result set.");
        }

        return RebuildStates(parents, events);
    }

    private static List<ShipmentState> RebuildStates(
        IReadOnlyList<ShipmentRow> parents,
        IReadOnlyList<EventRow> events)
    {
        var rowsByTrackingNumber = new Dictionary<string, ShipmentRow>(StringComparer.Ordinal);
        var statesByTrackingNumber = new Dictionary<string, ShipmentState>(StringComparer.Ordinal);
        foreach (var parent in parents)
        {
            if (!rowsByTrackingNumber.TryAdd(parent.TrackingNumber, parent))
            {
                throw IntegrityError("Shipment result contains duplicate tracking numbers.");
            }

            try
            {
                statesByTrackingNumber.Add(
                    parent.TrackingNumber,
                    ShipmentState.Register(parent.TrackingNumber, parent.RegisteredAt));
            }
            catch (ArgumentException error)
            {
                throw IntegrityError("Shipment row cannot initialize a domain state.", error);
            }
        }

        var nextSequenceByTrackingNumber = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var storedEvent in events)
        {
            if (!rowsByTrackingNumber.ContainsKey(storedEvent.TrackingNumber)
                || !statesByTrackingNumber.TryGetValue(storedEvent.TrackingNumber, out var current))
            {
                throw IntegrityError("Shipment history contains an orphan tracking number.");
            }

            var expectedSequence = nextSequenceByTrackingNumber.TryGetValue(storedEvent.TrackingNumber, out var next)
                ? next
                : 1;
            if (storedEvent.EventSequence != expectedSequence)
            {
                throw IntegrityError("Shipment history sequence is not contiguous from one.");
            }

            LifecycleEvent lifecycleEvent;
            try
            {
                lifecycleEvent = new LifecycleEvent(
                    storedEvent.EventId,
                    storedEvent.TrackingNumber,
                    storedEvent.Status,
                    storedEvent.OccurredAt,
                    storedEvent.SourceSystem);
            }
            catch (ArgumentException error)
            {
                throw IntegrityError("Shipment history row cannot initialize a domain event.", error);
            }

            var result = ShipmentLifecycle.Apply(current, lifecycleEvent);
            if (result.Code != ApplyEventCode.Applied || result.RecordedEvent is null)
            {
                throw IntegrityError($"Shipment history cannot be replayed ({result.Code}).");
            }

            statesByTrackingNumber[storedEvent.TrackingNumber] = result.State;
            nextSequenceByTrackingNumber[storedEvent.TrackingNumber] = checked(expectedSequence + 1);
        }

        var rebuilt = new List<ShipmentState>(parents.Count);
        foreach (var parent in parents)
        {
            var state = statesByTrackingNumber[parent.TrackingNumber];
            if (state.Version != parent.Version
                || state.Status != parent.Status
                || !state.LastOccurredAt.EqualsExact(parent.LastOccurredAt))
            {
                throw IntegrityError("Shipment projection does not match replayed history.");
            }

            rebuilt.Add(state);
        }

        return rebuilt;
    }

    private SqlCommand CreateProcedure(SqlConnection connection, SqlTransaction? transaction, string procedure)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = $"{_quotedSchema}.[{procedure}]";
        return command;
    }

    private static void AddNVarCharMax(SqlCommand command, string name, string? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.NVarChar, -1);
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    private static void AddDateTimeOffset(SqlCommand command, string name, DateTimeOffset value) =>
        command.Parameters.Add(name, SqlDbType.DateTimeOffset).Value = value;

    private static SqlParameter AddOutput(SqlCommand command, string name, int size)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.VarChar, size);
        parameter.Direction = ParameterDirection.Output;
        return parameter;
    }

    private static string ReadOutputCode(SqlParameter parameter) =>
        parameter.Value is string code
            ? code
            : throw IntegrityError("Stored procedure did not return its required result code.");

    private static void RequireColumns(SqlDataReader reader, string resultSet, params (string Name, Type Type)[] columns)
    {
        if (reader.FieldCount != columns.Length)
        {
            throw IntegrityError($"Stored procedure {resultSet} result set has an unexpected column count.");
        }

        for (var index = 0; index < columns.Length; index++)
        {
            if (!string.Equals(reader.GetName(index), columns[index].Name, StringComparison.Ordinal)
                || reader.GetFieldType(index) != columns[index].Type)
            {
                throw IntegrityError($"Stored procedure {resultSet} result set has an unexpected column shape.");
            }
        }
    }

    private static string ReadString(SqlDataReader reader, int ordinal, string column) =>
        !reader.IsDBNull(ordinal)
            ? reader.GetString(ordinal)
            : throw IntegrityError($"Stored procedure returned NULL for {column}.");

    private static DateTimeOffset ReadDateTimeOffset(SqlDataReader reader, int ordinal, string column) =>
        !reader.IsDBNull(ordinal)
            ? reader.GetFieldValue<DateTimeOffset>(ordinal)
            : throw IntegrityError($"Stored procedure returned NULL for {column}.");

    private static long ReadInt64(SqlDataReader reader, int ordinal, string column) =>
        !reader.IsDBNull(ordinal)
            ? reader.GetInt64(ordinal)
            : throw IntegrityError($"Stored procedure returned NULL for {column}.");

    private static Guid ReadGuid(SqlDataReader reader, int ordinal, string column) =>
        !reader.IsDBNull(ordinal)
            ? reader.GetGuid(ordinal)
            : throw IntegrityError($"Stored procedure returned NULL for {column}.");

    private static ShipmentStatus ReadStatus(SqlDataReader reader, int ordinal, string column)
    {
        if (reader.IsDBNull(ordinal))
        {
            throw IntegrityError($"Stored procedure returned NULL for {column}.");
        }

        var value = reader.GetByte(ordinal);
        if (value > (byte)ShipmentStatus.Cancelled)
        {
            throw IntegrityError($"Stored procedure returned invalid {column} value.");
        }

        return (ShipmentStatus)value;
    }

    private static bool IsValidSchemaIdentifier(string schema)
    {
        if (schema.Length is < 1 or > 128 || !IsAsciiLetter(schema[0]))
        {
            return false;
        }

        for (var index = 1; index < schema.Length; index++)
        {
            var character = schema[index];
            if (!IsAsciiLetter(character) && character is not (>= '0' and <= '9') and not '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidTrackingNumber(string value)
    {
        if (value.Length is < 1 or > 32)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= 'A' and <= 'Z')
                and not (>= '0' and <= '9')
                and not '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidSourceSystem(string value)
    {
        if (value.Length is < 1 or > 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= 'A' and <= 'Z')
                and not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                and not '.'
                and not '_'
                and not '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetter(char character) =>
        character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');

    private static InvalidOperationException IntegrityError(string message, Exception? innerException = null) =>
        new($"SQL shipment storage integrity failure: {message}", innerException);

    private sealed record ShipmentRow(
        string TrackingNumber,
        DateTimeOffset RegisteredAt,
        ShipmentStatus Status,
        DateTimeOffset LastOccurredAt,
        long Version);

    private sealed record EventRow(
        string TrackingNumber,
        long EventSequence,
        Guid EventId,
        ShipmentStatus Status,
        DateTimeOffset OccurredAt,
        string SourceSystem);
}
