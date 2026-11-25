using TraceOps.Domain;
using Xunit;

namespace TraceOps.Domain.Tests;

public sealed class ShipmentLifecycleValidationTests
{
    public static IEnumerable<object[]> InvalidTrackingNumbers =>
    [
        [""],
        ["lower-case"],
        [" TRACK-001"],
        ["TRACK-001 "],
        ["TRACK-001\n"],
        ["TRACK-001\r\n"],
        [new string('A', 33)],
        ["TRÄCK-001"],
        ["TR\u0130CK-001"],
        ["TRACK_001"],
    ];

    public static IEnumerable<object[]> ValidSourceSystems =>
    [
        ["a"],
        ["system-1"],
        ["A0._-z"],
        [new string('a', 64)],
    ];

    public static IEnumerable<object[]> InvalidSourceSystems =>
    [
        [""],
        [new string('a', 65)],
        ["has space"],
        ["system\n"],
        ["system\r\n"],
        ["systém"],
        ["system/@"],
    ];

    [Theory]
    [MemberData(nameof(InvalidTrackingNumbers))]
    public void Apply_rejects_noncanonical_tracking_without_changing_state(string trackingNumber)
    {
        var current = DomainFixture.StateAt(ShipmentStatus.Registered);

        DomainFixture.AssertRejected(
            current,
            DomainFixture.Event(trackingNumber: trackingNumber),
            ApplyEventCode.InvalidTrackingNumber);
    }

    [Fact]
    public void Apply_does_not_trim_or_case_normalize_tracking_number()
    {
        var current = DomainFixture.StateAt(ShipmentStatus.Registered);

        DomainFixture.AssertRejected(
            current,
            DomainFixture.Event(trackingNumber: "track-001"),
            ApplyEventCode.InvalidTrackingNumber);
    }

    [Fact]
    public void Apply_rejects_a_different_but_well_formed_shipment_tracking_number()
    {
        var current = DomainFixture.StateAt(ShipmentStatus.Registered);

        DomainFixture.AssertRejected(
            current,
            DomainFixture.Event(trackingNumber: "OTHER-002"),
            ApplyEventCode.TrackingNumberMismatch);
    }

    [Theory]
    [MemberData(nameof(ValidSourceSystems))]
    public void Apply_accepts_canonical_source_system_at_length_edges(string sourceSystem)
    {
        var current = DomainFixture.StateAt(ShipmentStatus.Registered);
        var input = DomainFixture.Event(sourceSystem: sourceSystem);

        var result = ShipmentLifecycle.Apply(current, input);

        DomainFixture.AssertApplied(current, input, result, current.Version + 1);
    }

    [Theory]
    [MemberData(nameof(InvalidSourceSystems))]
    public void Apply_rejects_noncanonical_source_system_without_changing_state(string sourceSystem)
    {
        var current = DomainFixture.StateAt(ShipmentStatus.Registered);

        DomainFixture.AssertRejected(
            current,
            DomainFixture.Event(sourceSystem: sourceSystem),
            ApplyEventCode.InvalidSourceSystem);
    }

    [Fact]
    public void Apply_rejects_empty_event_id()
    {
        var current = DomainFixture.StateAt(ShipmentStatus.Registered);

        DomainFixture.AssertRejected(
            current,
            DomainFixture.Event(eventId: Guid.Empty),
            ApplyEventCode.InvalidEventId);
    }

    [Fact]
    public void Apply_null_current_throws_with_current_parameter_name()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => ShipmentLifecycle.Apply(null!, null!));

        Assert.Equal("current", exception.ParamName);
    }

    [Fact]
    public void Apply_null_input_throws_with_input_parameter_name()
    {
        var current = DomainFixture.StateAt(ShipmentStatus.Registered);

        var exception = Assert.Throws<ArgumentNullException>(
            () => ShipmentLifecycle.Apply(current, null!));

        Assert.Equal("input", exception.ParamName);
    }
}
