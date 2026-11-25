using TraceOps.Domain;
using Xunit;

namespace TraceOps.Domain.Tests;

public sealed class ShipmentLifecycleTransitionTests
{
    public static IEnumerable<object[]> AllStatusPairs =>
        from current in Enum.GetValues<ShipmentStatus>()
        from next in Enum.GetValues<ShipmentStatus>()
        select new object[] { current, next };

    public static IEnumerable<object[]> UnknownStatuses =>
    [
        [(ShipmentStatus)(-1)],
        [(ShipmentStatus)int.MaxValue],
    ];

    [Theory]
    [MemberData(nameof(AllStatusPairs))]
    public void Apply_matches_every_pair_in_the_transition_table(
        ShipmentStatus currentStatus,
        ShipmentStatus nextStatus)
    {
        var current = DomainFixture.StateAt(currentStatus);
        var input = DomainFixture.Event(status: nextStatus);

        var result = ShipmentLifecycle.Apply(current, input);

        if (IsAllowed(currentStatus, nextStatus))
        {
            DomainFixture.AssertApplied(current, input, result, current.Version + 1);
        }
        else
        {
            Assert.Equal(ApplyEventCode.InvalidTransition, result.Code);
            Assert.False(result.Succeeded);
            Assert.Same(current, result.State);
            Assert.Null(result.RecordedEvent);
            Assert.Equal(current.Version, result.State.Version);
            Assert.Equal(current.Events, result.State.Events);
        }
    }

    [Theory]
    [MemberData(nameof(UnknownStatuses))]
    public void Apply_rejects_enum_values_outside_declared_statuses(ShipmentStatus status)
    {
        var current = DomainFixture.StateAt(ShipmentStatus.Registered);

        DomainFixture.AssertRejected(
            current,
            DomainFixture.Event(status: status),
            ApplyEventCode.InvalidStatus);
    }

    [Fact]
    public void Apply_allows_delivery_retry_after_delivery_failed()
    {
        var current = DomainFixture.StateAt(ShipmentStatus.DeliveryFailed);
        var input = DomainFixture.Event(status: ShipmentStatus.OutForDelivery);

        var result = ShipmentLifecycle.Apply(current, input);

        DomainFixture.AssertApplied(current, input, result, current.Version + 1);
    }

    private static bool IsAllowed(ShipmentStatus current, ShipmentStatus next) =>
        (current, next) switch
        {
            (ShipmentStatus.Registered, ShipmentStatus.Accepted or ShipmentStatus.Cancelled) => true,
            (ShipmentStatus.Accepted, ShipmentStatus.InTransit or ShipmentStatus.Cancelled) => true,
            (ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery) => true,
            (ShipmentStatus.OutForDelivery, ShipmentStatus.Delivered or ShipmentStatus.DeliveryFailed) => true,
            (ShipmentStatus.DeliveryFailed, ShipmentStatus.OutForDelivery) => true,
            _ => false,
        };
}
