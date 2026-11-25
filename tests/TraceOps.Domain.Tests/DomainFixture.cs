using TraceOps.Domain;
using Xunit;

namespace TraceOps.Domain.Tests;

internal static class DomainFixture
{
    public static readonly DateTimeOffset RegisteredAt =
        new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    public const string TrackingNumber = "TRACK-001";
    public const string SourceSystem = "test.api";

    public static LifecycleEvent Event(
        string? trackingNumber = null,
        ShipmentStatus status = ShipmentStatus.Accepted,
        DateTimeOffset? occurredAt = null,
        string? sourceSystem = null,
        Guid? eventId = null) =>
        new(
            eventId ?? Guid.NewGuid(),
            trackingNumber ?? TrackingNumber,
            status,
            occurredAt ?? RegisteredAt,
            sourceSystem ?? SourceSystem);

    public static ShipmentState StateAt(
        ShipmentStatus target,
        string trackingNumber = TrackingNumber)
    {
        var state = ShipmentState.Register(trackingNumber, RegisteredAt);
        ShipmentStatus[] path = target switch
        {
            ShipmentStatus.Registered => Array.Empty<ShipmentStatus>(),
            ShipmentStatus.Accepted => [ShipmentStatus.Accepted],
            ShipmentStatus.InTransit => [ShipmentStatus.Accepted, ShipmentStatus.InTransit],
            ShipmentStatus.OutForDelivery =>
                [ShipmentStatus.Accepted, ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery],
            ShipmentStatus.DeliveryFailed =>
                [ShipmentStatus.Accepted, ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery, ShipmentStatus.DeliveryFailed],
            ShipmentStatus.Delivered =>
                [ShipmentStatus.Accepted, ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery, ShipmentStatus.Delivered],
            ShipmentStatus.Cancelled => [ShipmentStatus.Cancelled],
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown shipment status."),
        };

        foreach (var status in path)
        {
            var input = Event(trackingNumber, status);
            var result = ShipmentLifecycle.Apply(state, input);
            Assert.Equal(ApplyEventCode.Applied, result.Code);
            Assert.True(result.Succeeded);
            Assert.Equal(input, result.RecordedEvent);
            state = result.State;
        }

        return state;
    }

    public static void AssertApplied(
        ShipmentState before,
        LifecycleEvent input,
        ApplyEventResult result,
        long expectedVersion)
    {
        Assert.Equal(ApplyEventCode.Applied, result.Code);
        Assert.True(result.Succeeded);
        Assert.NotSame(before, result.State);
        Assert.Equal(input.TrackingNumber, result.State.TrackingNumber);
        Assert.Equal(input.Status, result.State.Status);
        Assert.Equal(input.OccurredAt, result.State.LastOccurredAt);
        Assert.Equal(expectedVersion, result.State.Version);
        Assert.Equal(before.Events.Count + 1, result.State.Events.Count);
        Assert.Equal(input, result.State.Events[^1]);
        Assert.Equal(input, result.RecordedEvent);
    }

    public static void AssertRejected(
        ShipmentState before,
        LifecycleEvent input,
        ApplyEventCode expectedCode)
    {
        var result = ShipmentLifecycle.Apply(before, input);

        Assert.Equal(expectedCode, result.Code);
        Assert.False(result.Succeeded);
        Assert.Same(before, result.State);
        Assert.Null(result.RecordedEvent);
        Assert.Equal(before.Status, result.State.Status);
        Assert.Equal(before.LastOccurredAt, result.State.LastOccurredAt);
        Assert.Equal(before.Version, result.State.Version);
        Assert.Equal(before.Events, result.State.Events);
    }
}
