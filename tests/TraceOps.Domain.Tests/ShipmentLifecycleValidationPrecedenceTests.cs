using TraceOps.Domain;
using Xunit;

namespace TraceOps.Domain.Tests;

public sealed class ShipmentLifecycleValidationPrecedenceTests
{
    public static IEnumerable<object[]> LaterInvalidFields =>
    [
        ["tracking"],
        ["mismatch"],
        ["status"],
        ["source"],
        ["timestamp"],
        ["regression"],
        ["transition"],
    ];

    [Fact]
    public void Apply_validates_in_the_documented_order_when_multiple_fields_are_invalid()
    {
        var current = DomainFixture.StateAt(ShipmentStatus.Registered);
        var cases = new (LifecycleEvent Event, ApplyEventCode Expected)[]
        {
            (DomainFixture.Event(
                trackingNumber: "lower",
                status: (ShipmentStatus)999,
                occurredAt: DomainFixture.RegisteredAt.AddTicks(-1),
                sourceSystem: "invalid source",
                eventId: Guid.Empty), ApplyEventCode.InvalidEventId),
            (DomainFixture.Event(
                trackingNumber: "lower",
                status: (ShipmentStatus)999,
                occurredAt: DomainFixture.RegisteredAt.AddTicks(-1),
                sourceSystem: "invalid source"), ApplyEventCode.InvalidTrackingNumber),
            (DomainFixture.Event(
                trackingNumber: "OTHER-002",
                status: (ShipmentStatus)999,
                occurredAt: DomainFixture.RegisteredAt.AddTicks(-1),
                sourceSystem: "invalid source"), ApplyEventCode.TrackingNumberMismatch),
            (DomainFixture.Event(
                status: (ShipmentStatus)999,
                occurredAt: DomainFixture.RegisteredAt.AddTicks(-1),
                sourceSystem: "invalid source"), ApplyEventCode.InvalidStatus),
            (DomainFixture.Event(
                occurredAt: DomainFixture.RegisteredAt.AddTicks(-1),
                sourceSystem: "invalid source"), ApplyEventCode.InvalidSourceSystem),
            (DomainFixture.Event(
                occurredAt: DomainFixture.RegisteredAt.ToOffset(TimeSpan.FromHours(1)),
                status: ShipmentStatus.Delivered), ApplyEventCode.TimestampMustBeUtc),
            (DomainFixture.Event(
                occurredAt: DomainFixture.RegisteredAt.AddTicks(-1),
                status: ShipmentStatus.Delivered), ApplyEventCode.EventTimeRegression),
            (DomainFixture.Event(status: ShipmentStatus.Delivered), ApplyEventCode.InvalidTransition),
        };

        foreach (var (input, expected) in cases)
        {
            DomainFixture.AssertRejected(current, input, expected);
        }
    }

    [Theory]
    [MemberData(nameof(LaterInvalidFields))]
    public void Apply_checks_duplicate_id_before_other_payload_validation(string changedField)
    {
        var current = DomainFixture.StateAt(ShipmentStatus.Registered);
        var original = DomainFixture.Event();
        var applied = ShipmentLifecycle.Apply(current, original);
        var conflicting = changedField switch
        {
            "tracking" => original with { TrackingNumber = "lower" },
            "mismatch" => original with { TrackingNumber = "OTHER-002" },
            "status" => original with { Status = (ShipmentStatus)999 },
            "source" => original with { SourceSystem = "invalid source" },
            "timestamp" => original with
            {
                OccurredAt = original.OccurredAt.ToOffset(TimeSpan.FromHours(1)),
            },
            "regression" => original with { OccurredAt = original.OccurredAt.AddTicks(-1) },
            "transition" => original with { Status = ShipmentStatus.Delivered },
            _ => throw new ArgumentOutOfRangeException(nameof(changedField)),
        };

        DomainFixture.AssertRejected(
            applied.State,
            conflicting,
            ApplyEventCode.EventIdConflict);
    }
}
