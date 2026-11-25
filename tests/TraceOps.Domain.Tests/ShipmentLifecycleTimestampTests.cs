using TraceOps.Domain;
using Xunit;

namespace TraceOps.Domain.Tests;

public sealed class ShipmentLifecycleTimestampTests
{
    [Fact]
    public void Apply_accepts_event_at_exactly_same_UTC_time_as_current_state()
    {
        var current = DomainFixture.StateAt(ShipmentStatus.Registered);
        var input = DomainFixture.Event(occurredAt: DomainFixture.RegisteredAt);

        var result = ShipmentLifecycle.Apply(current, input);

        DomainFixture.AssertApplied(current, input, result, current.Version + 1);
    }

    [Fact]
    public void Apply_accepts_strictly_later_UTC_time_and_updates_last_occurred_at()
    {
        var current = DomainFixture.StateAt(ShipmentStatus.Registered);
        var later = DomainFixture.RegisteredAt.AddTicks(1);
        var input = DomainFixture.Event(occurredAt: later);

        var result = ShipmentLifecycle.Apply(current, input);

        DomainFixture.AssertApplied(current, input, result, current.Version + 1);
        Assert.Equal(later, result.State.LastOccurredAt);
        Assert.Equal(TimeSpan.Zero, result.State.LastOccurredAt.Offset);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-7)]
    public void Apply_rejects_non_UTC_offset_even_when_instant_matches_state(int offsetHours)
    {
        var current = DomainFixture.StateAt(ShipmentStatus.Registered);
        var sameInstantWithOffset = DomainFixture.RegisteredAt.ToOffset(TimeSpan.FromHours(offsetHours));

        Assert.Equal(DomainFixture.RegisteredAt, sameInstantWithOffset);
        Assert.NotEqual(DomainFixture.RegisteredAt.Offset, sameInstantWithOffset.Offset);

        DomainFixture.AssertRejected(
            current,
            DomainFixture.Event(occurredAt: sameInstantWithOffset),
            ApplyEventCode.TimestampMustBeUtc);
    }

    [Fact]
    public void Apply_rejects_time_before_current_last_event()
    {
        var current = DomainFixture.StateAt(ShipmentStatus.Registered);

        DomainFixture.AssertRejected(
            current,
            DomainFixture.Event(occurredAt: DomainFixture.RegisteredAt.AddTicks(-1)),
            ApplyEventCode.EventTimeRegression);
    }
}
