using TraceOps.Domain;

namespace TraceOps.Application;

public sealed record RegisterShipmentCommand(string TrackingNumber);
public sealed record AddShipmentEventCommand(
    string TrackingNumber, Guid EventId, ShipmentStatus Status,
    DateTimeOffset OccurredAt, string SourceSystem);
public sealed record ShipmentSummary(
    string TrackingNumber, ShipmentStatus Status,
    DateTimeOffset LastOccurredAt, long Version);
public sealed record ShipmentDetails(
    ShipmentSummary Shipment, IReadOnlyList<LifecycleEvent> Events);
public sealed record ShipmentOperationResult(
    string Code, ShipmentSummary? Shipment, LifecycleEvent? RecordedEvent);

public interface IShipmentStore
{
    IReadOnlyList<ShipmentState> List();
    ShipmentState? Find(string trackingNumber);
    bool TryAdd(ShipmentState state);
    ApplyEventResult? ApplyAtomically(LifecycleEvent input);
}

public sealed class ShipmentService
{
    private readonly IShipmentStore _store;
    private readonly TimeProvider _timeProvider;

    public ShipmentService(IShipmentStore store, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _timeProvider = timeProvider;
    }

    public IReadOnlyList<ShipmentSummary> List()
    {
        var summaries = _store.List()
            .Select(ToSummary)
            .OrderBy(shipment => shipment.TrackingNumber, StringComparer.Ordinal)
            .ToArray();

        return Array.AsReadOnly(summaries);
    }

    public ShipmentDetails? Find(string trackingNumber)
    {
        ArgumentNullException.ThrowIfNull(trackingNumber);
        if (!IsCanonicalTrackingNumber(trackingNumber))
        {
            return null;
        }

        var state = _store.Find(trackingNumber);
        return state is null ? null : ToDetails(state);
    }

    public ShipmentOperationResult Register(RegisterShipmentCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsCanonicalTrackingNumber(command.TrackingNumber))
        {
            return new ShipmentOperationResult("INVALID_TRACKING_NUMBER", null, null);
        }

        var state = ShipmentState.Register(command.TrackingNumber, _timeProvider.GetUtcNow());
        if (!_store.TryAdd(state))
        {
            return new ShipmentOperationResult("TRACKING_NUMBER_CONFLICT", null, null);
        }

        return new ShipmentOperationResult("REGISTERED", ToSummary(state), null);
    }

    public ShipmentOperationResult AddEvent(AddShipmentEventCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsCanonicalTrackingNumber(command.TrackingNumber))
        {
            return new ShipmentOperationResult("INVALID_TRACKING_NUMBER", null, null);
        }

        var input = new LifecycleEvent(
            command.EventId,
            command.TrackingNumber,
            command.Status,
            command.OccurredAt,
            command.SourceSystem);
        var result = _store.ApplyAtomically(input);
        if (result is null)
        {
            return new ShipmentOperationResult("SHIPMENT_NOT_FOUND", null, null);
        }

        var recordedEvent = result.Code is ApplyEventCode.Applied or ApplyEventCode.Duplicate
            ? result.RecordedEvent
            : null;

        return new ShipmentOperationResult(
            ToOperationCode(result.Code),
            ToSummary(result.State),
            recordedEvent);
    }

    private static ShipmentSummary ToSummary(ShipmentState state) =>
        new(state.TrackingNumber, state.Status, state.LastOccurredAt, state.Version);

    private static ShipmentDetails ToDetails(ShipmentState state) =>
        new(ToSummary(state), Array.AsReadOnly(state.Events.ToArray()));

    private static bool IsCanonicalTrackingNumber(string? trackingNumber)
    {
        if (trackingNumber is null || trackingNumber.Length is < 1 or > 32)
        {
            return false;
        }

        foreach (var character in trackingNumber)
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

    private static string ToOperationCode(ApplyEventCode code) => code switch
    {
        ApplyEventCode.Applied => "APPLIED",
        ApplyEventCode.Duplicate => "DUPLICATE",
        ApplyEventCode.EventIdConflict => "EVENT_ID_CONFLICT",
        ApplyEventCode.InvalidEventId => "INVALID_EVENT_ID",
        ApplyEventCode.InvalidTrackingNumber => "INVALID_TRACKING_NUMBER",
        ApplyEventCode.TrackingNumberMismatch => "TRACKING_NUMBER_MISMATCH",
        ApplyEventCode.InvalidStatus => "INVALID_STATUS",
        ApplyEventCode.InvalidSourceSystem => "INVALID_SOURCE_SYSTEM",
        ApplyEventCode.TimestampMustBeUtc => "TIMESTAMP_MUST_BE_UTC",
        ApplyEventCode.EventTimeRegression => "EVENT_TIME_REGRESSION",
        ApplyEventCode.InvalidTransition => "INVALID_TRANSITION",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown domain result code."),
    };
}
