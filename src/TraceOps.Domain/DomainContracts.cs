namespace TraceOps.Domain;

public enum ShipmentStatus
{
    Registered,
    Accepted,
    InTransit,
    OutForDelivery,
    DeliveryFailed,
    Delivered,
    Cancelled,
}

public sealed record LifecycleEvent(
    Guid EventId,
    string TrackingNumber,
    ShipmentStatus Status,
    DateTimeOffset OccurredAt,
    string SourceSystem);

public sealed class ShipmentState
{
    private readonly IReadOnlyList<LifecycleEvent> _events;

    private ShipmentState(
        string trackingNumber,
        ShipmentStatus status,
        DateTimeOffset lastOccurredAt,
        long version,
        LifecycleEvent[] events)
    {
        TrackingNumber = trackingNumber;
        Status = status;
        LastOccurredAt = lastOccurredAt;
        Version = version;
        _events = Array.AsReadOnly(events);
    }

    public string TrackingNumber { get; }

    public ShipmentStatus Status { get; }

    public DateTimeOffset LastOccurredAt { get; }

    public long Version { get; }

    public IReadOnlyList<LifecycleEvent> Events => _events;

    public static ShipmentState Register(string trackingNumber, DateTimeOffset registeredAt)
    {
        if (!IsValidTrackingNumber(trackingNumber))
        {
            throw new ArgumentException("Tracking number is invalid.", nameof(trackingNumber));
        }

        if (registeredAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Registration time must be UTC.", nameof(registeredAt));
        }

        return new ShipmentState(
            trackingNumber,
            ShipmentStatus.Registered,
            registeredAt,
            0,
            Array.Empty<LifecycleEvent>());
    }

    internal ShipmentState Apply(LifecycleEvent input)
    {
        var events = new LifecycleEvent[_events.Count + 1];
        for (var index = 0; index < _events.Count; index++)
        {
            events[index] = _events[index];
        }

        events[^1] = input;

        return new ShipmentState(
            TrackingNumber,
            input.Status,
            input.OccurredAt,
            checked(Version + 1),
            events);
    }

    internal static bool IsValidTrackingNumber(string? trackingNumber)
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
}

public enum ApplyEventCode
{
    Applied,
    Duplicate,
    EventIdConflict,
    InvalidEventId,
    InvalidTrackingNumber,
    TrackingNumberMismatch,
    InvalidStatus,
    InvalidSourceSystem,
    TimestampMustBeUtc,
    EventTimeRegression,
    InvalidTransition,
}

public sealed class ApplyEventResult
{
    internal ApplyEventResult(ApplyEventCode code, ShipmentState state, LifecycleEvent? recordedEvent)
    {
        Code = code;
        State = state;
        RecordedEvent = recordedEvent;
    }

    public ApplyEventCode Code { get; }

    public ShipmentState State { get; }

    public LifecycleEvent? RecordedEvent { get; }

    public bool Succeeded => Code is ApplyEventCode.Applied or ApplyEventCode.Duplicate;
}

public static class ShipmentLifecycle
{
    public static ApplyEventResult Apply(ShipmentState current, LifecycleEvent input)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(input);

        if (input.EventId == Guid.Empty)
        {
            return Rejected(current, ApplyEventCode.InvalidEventId);
        }

        foreach (var recorded in current.Events)
        {
            if (recorded.EventId != input.EventId)
            {
                continue;
            }

            return HasSamePayload(recorded, input)
                ? new ApplyEventResult(ApplyEventCode.Duplicate, current, recorded)
                : Rejected(current, ApplyEventCode.EventIdConflict);
        }

        if (!ShipmentState.IsValidTrackingNumber(input.TrackingNumber))
        {
            return Rejected(current, ApplyEventCode.InvalidTrackingNumber);
        }

        if (!string.Equals(input.TrackingNumber, current.TrackingNumber, StringComparison.Ordinal))
        {
            return Rejected(current, ApplyEventCode.TrackingNumberMismatch);
        }

        if (!IsValidStatus(input.Status))
        {
            return Rejected(current, ApplyEventCode.InvalidStatus);
        }

        if (!IsValidSourceSystem(input.SourceSystem))
        {
            return Rejected(current, ApplyEventCode.InvalidSourceSystem);
        }

        if (input.OccurredAt.Offset != TimeSpan.Zero)
        {
            return Rejected(current, ApplyEventCode.TimestampMustBeUtc);
        }

        if (input.OccurredAt < current.LastOccurredAt)
        {
            return Rejected(current, ApplyEventCode.EventTimeRegression);
        }

        if (!CanTransition(current.Status, input.Status))
        {
            return Rejected(current, ApplyEventCode.InvalidTransition);
        }

        return new ApplyEventResult(ApplyEventCode.Applied, current.Apply(input), input);
    }

    private static ApplyEventResult Rejected(ShipmentState current, ApplyEventCode code) =>
        new(code, current, null);

    private static bool HasSamePayload(LifecycleEvent recorded, LifecycleEvent input) =>
        recorded.EventId == input.EventId
        && string.Equals(recorded.TrackingNumber, input.TrackingNumber, StringComparison.Ordinal)
        && recorded.Status == input.Status
        && recorded.OccurredAt.EqualsExact(input.OccurredAt)
        && string.Equals(recorded.SourceSystem, input.SourceSystem, StringComparison.Ordinal);

    private static bool IsValidStatus(ShipmentStatus status) =>
        status is ShipmentStatus.Registered
            or ShipmentStatus.Accepted
            or ShipmentStatus.InTransit
            or ShipmentStatus.OutForDelivery
            or ShipmentStatus.DeliveryFailed
            or ShipmentStatus.Delivered
            or ShipmentStatus.Cancelled;

    private static bool IsValidSourceSystem(string? sourceSystem)
    {
        if (sourceSystem is null || sourceSystem.Length is < 1 or > 64)
        {
            return false;
        }

        foreach (var character in sourceSystem)
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

    private static bool CanTransition(ShipmentStatus current, ShipmentStatus next) =>
        current switch
        {
            ShipmentStatus.Registered => next is ShipmentStatus.Accepted or ShipmentStatus.Cancelled,
            ShipmentStatus.Accepted => next is ShipmentStatus.InTransit or ShipmentStatus.Cancelled,
            ShipmentStatus.InTransit => next is ShipmentStatus.OutForDelivery,
            ShipmentStatus.OutForDelivery => next is ShipmentStatus.Delivered or ShipmentStatus.DeliveryFailed,
            ShipmentStatus.DeliveryFailed => next is ShipmentStatus.OutForDelivery,
            ShipmentStatus.Delivered or ShipmentStatus.Cancelled => false,
            _ => false,
        };
}
