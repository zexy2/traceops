using TraceOps.Domain;
using Xunit;

namespace TraceOps.Domain.Tests;

public sealed class ShipmentStateRegisterTests
{
    public static IEnumerable<object[]> ValidTrackingNumbers =>
    [
        ["A"],
        ["0"],
        ["TRACK-001"],
        [new string('Z', 32)],
        ["A0-B9"],
    ];

    public static IEnumerable<object[]> InvalidTrackingNumbers =>
    [
        [""],
        [new string('A', 33)],
        ["lower"],
        [" TRACK-001"],
        ["TRACK-001 "],
        ["TRACK-001\n"],
        ["TRACK-001\r\n"],
        ["TRÄCK-001"],
        ["TR\u0130CK-001"],
        ["TRACK_001"],
    ];

    [Theory]
    [MemberData(nameof(ValidTrackingNumbers))]
    public void Register_creates_initial_registered_state_for_canonical_tracking(string trackingNumber)
    {
        var state = ShipmentState.Register(trackingNumber, DomainFixture.RegisteredAt);

        Assert.Equal(trackingNumber, state.TrackingNumber);
        Assert.Equal(ShipmentStatus.Registered, state.Status);
        Assert.Equal(DomainFixture.RegisteredAt, state.LastOccurredAt);
        Assert.Equal(TimeSpan.Zero, state.LastOccurredAt.Offset);
        Assert.Equal(0, state.Version);
        Assert.Empty(state.Events);
    }

    [Theory]
    [MemberData(nameof(InvalidTrackingNumbers))]
    public void Register_rejects_noncanonical_tracking_with_trackingNumber_parameter(string trackingNumber)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => ShipmentState.Register(trackingNumber, DomainFixture.RegisteredAt));

        Assert.Equal("trackingNumber", exception.ParamName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(-8)]
    public void Register_rejects_non_UTC_time_with_registeredAt_parameter(int offsetHours)
    {
        var localTime = new DateTimeOffset(
            2026, 11, 17, 12, 0, 0, TimeSpan.FromHours(offsetHours));

        var exception = Assert.Throws<ArgumentException>(
            () => ShipmentState.Register(DomainFixture.TrackingNumber, localTime));

        Assert.Equal("registeredAt", exception.ParamName);
    }
}
