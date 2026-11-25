using TraceOps.Domain;
using Xunit;

namespace TraceOps.Domain.Tests;

public sealed class ShipmentLifecycleDuplicateTests
{
    public static IEnumerable<object[]> ConflictingPayloadFields =>
    [
        ["tracking-valid"],
        ["tracking-invalid"],
        ["status-valid"],
        ["status-invalid"],
        ["time-valid"],
        ["time-same-instant-different-offset"],
        ["source-valid"],
        ["source-invalid"],
        ["source-case-only"],
    ];

    [Fact]
    public void Apply_returns_original_event_and_unchanged_state_for_exact_duplicate()
    {
        var current = DomainFixture.StateAt(ShipmentStatus.Registered);
        var original = DomainFixture.Event();
        var first = ShipmentLifecycle.Apply(current, original);
        DomainFixture.AssertApplied(current, original, first, current.Version + 1);

        var duplicate = ShipmentLifecycle.Apply(first.State, original);

        Assert.Equal(ApplyEventCode.Duplicate, duplicate.Code);
        Assert.True(duplicate.Succeeded);
        Assert.Same(first.State, duplicate.State);
        Assert.Same(first.State.Events[0], duplicate.RecordedEvent);
        Assert.Equal(first.State.Version, duplicate.State.Version);
        Assert.Equal(first.State.Events, duplicate.State.Events);
    }

    [Fact]
    public void Apply_recognizes_separately_constructed_equal_event_as_duplicate()
    {
        var current = DomainFixture.StateAt(ShipmentStatus.Registered);
        var original = DomainFixture.Event();
        var first = ShipmentLifecycle.Apply(current, original);
        var replay = new LifecycleEvent(
            original.EventId,
            original.TrackingNumber,
            original.Status,
            original.OccurredAt,
            original.SourceSystem);

        Assert.NotSame(original, replay);
        Assert.Equal(original, replay);

        var duplicate = ShipmentLifecycle.Apply(first.State, replay);

        Assert.Equal(ApplyEventCode.Duplicate, duplicate.Code);
        Assert.True(duplicate.Succeeded);
        Assert.Same(first.State, duplicate.State);
        Assert.Same(first.State.Events[0], duplicate.RecordedEvent);
        Assert.Equal(first.State.Version, duplicate.State.Version);
        Assert.Equal(first.State.Events, duplicate.State.Events);
    }

    [Theory]
    [InlineData(ShipmentStatus.Delivered)]
    [InlineData(ShipmentStatus.Cancelled)]
    public void Apply_replaying_an_older_event_after_later_terminal_transition_preserves_current_state(
        ShipmentStatus terminalStatus)
    {
        var registered = DomainFixture.StateAt(ShipmentStatus.Registered);
        var original = DomainFixture.Event(status: ShipmentStatus.Accepted);
        var accepted = ShipmentLifecycle.Apply(registered, original);
        DomainFixture.AssertApplied(registered, original, accepted, registered.Version + 1);

        var path = terminalStatus switch
        {
            ShipmentStatus.Delivered => new[]
            {
                ShipmentStatus.InTransit,
                ShipmentStatus.OutForDelivery,
                ShipmentStatus.Delivered,
            },
            ShipmentStatus.Cancelled => new[] { ShipmentStatus.Cancelled },
            _ => throw new ArgumentOutOfRangeException(nameof(terminalStatus)),
        };
        var current = accepted.State;
        var occurredAt = DomainFixture.RegisteredAt.AddSeconds(1);
        foreach (var status in path)
        {
            var laterInput = DomainFixture.Event(status: status, occurredAt: occurredAt);
            var laterResult = ShipmentLifecycle.Apply(current, laterInput);
            DomainFixture.AssertApplied(current, laterInput, laterResult, current.Version + 1);
            current = laterResult.State;
            occurredAt = occurredAt.AddSeconds(1);
        }

        var replay = new LifecycleEvent(
            original.EventId,
            original.TrackingNumber,
            original.Status,
            original.OccurredAt,
            original.SourceSystem);

        var duplicate = ShipmentLifecycle.Apply(current, replay);

        Assert.Equal(ApplyEventCode.Duplicate, duplicate.Code);
        Assert.True(duplicate.Succeeded);
        Assert.Same(current, duplicate.State);
        Assert.Equal(terminalStatus, duplicate.State.Status);
        Assert.Equal(current.LastOccurredAt, duplicate.State.LastOccurredAt);
        Assert.Equal(current.Version, duplicate.State.Version);
        Assert.Equal(path.Length + 1, duplicate.State.Events.Count);
        Assert.Same(current.Events[0], duplicate.RecordedEvent);
        Assert.Equal(original, duplicate.State.Events[0]);
        Assert.Equal(path, duplicate.State.Events.Skip(1).Select(item => item.Status));
    }

    [Theory]
    [MemberData(nameof(ConflictingPayloadFields))]
    public void Apply_reports_event_id_conflict_before_validating_changed_payload(string changedField)
    {
        var current = DomainFixture.StateAt(ShipmentStatus.Registered);
        var original = DomainFixture.Event();
        var first = ShipmentLifecycle.Apply(current, original);
        var conflicting = ChangeField(original, changedField);

        DomainFixture.AssertRejected(
            first.State,
            conflicting,
            ApplyEventCode.EventIdConflict);
    }

    [Fact]
    public void Apply_scopes_event_id_deduplication_to_one_shipment()
    {
        var eventId = Guid.NewGuid();
        var firstShipment = DomainFixture.StateAt(ShipmentStatus.Registered, "TRACK-001");
        var firstEvent = DomainFixture.Event(trackingNumber: "TRACK-001", eventId: eventId);
        var firstResult = ShipmentLifecycle.Apply(firstShipment, firstEvent);

        var secondShipment = DomainFixture.StateAt(ShipmentStatus.Registered, "OTHER-002");
        var secondEvent = DomainFixture.Event(trackingNumber: "OTHER-002", eventId: eventId);
        var secondResult = ShipmentLifecycle.Apply(secondShipment, secondEvent);

        DomainFixture.AssertApplied(
            secondShipment,
            secondEvent,
            secondResult,
            secondShipment.Version + 1);
        Assert.Equal(ApplyEventCode.Applied, firstResult.Code);
        Assert.NotSame(firstResult.State, secondResult.State);
        Assert.Equal("TRACK-001", firstResult.State.TrackingNumber);
        Assert.Equal("OTHER-002", secondResult.State.TrackingNumber);
    }

    private static LifecycleEvent ChangeField(LifecycleEvent original, string field) => field switch
    {
        "tracking-valid" => original with { TrackingNumber = "OTHER-002" },
        "tracking-invalid" => original with { TrackingNumber = "lowercase" },
        "status-valid" => original with { Status = ShipmentStatus.InTransit },
        "status-invalid" => original with { Status = (ShipmentStatus)999 },
        "time-valid" => original with { OccurredAt = original.OccurredAt.AddTicks(1) },
        "time-same-instant-different-offset" => original with
        {
            OccurredAt = original.OccurredAt.ToOffset(TimeSpan.FromHours(2)),
        },
        "source-valid" => original with { SourceSystem = "other.api" },
        "source-invalid" => original with { SourceSystem = "invalid source" },
        "source-case-only" => original with { SourceSystem = "TEST.API" },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown field case."),
    };
}
