using System.Collections.Generic;
using TraceOps.Domain;
using Xunit;

namespace TraceOps.Domain.Tests;

public sealed class ShipmentLifecycleHistoryTests
{
    [Fact]
    public void Apply_builds_sequential_history_and_preserves_every_prior_immutable_state()
    {
        var states = new List<ShipmentState>
        {
            DomainFixture.StateAt(ShipmentStatus.Registered),
        };
        var snapshots = new List<StateSnapshot> { Snapshot(states[0]) };
        var statuses = new[]
        {
            ShipmentStatus.Accepted,
            ShipmentStatus.InTransit,
            ShipmentStatus.OutForDelivery,
            ShipmentStatus.DeliveryFailed,
            ShipmentStatus.OutForDelivery,
            ShipmentStatus.Delivered,
        };

        foreach (var status in statuses)
        {
            var before = states[^1];
            var input = DomainFixture.Event(status: status);
            var result = ShipmentLifecycle.Apply(before, input);
            DomainFixture.AssertApplied(before, input, result, states.Count);
            states.Add(result.State);
            snapshots.Add(Snapshot(result.State));
        }

        for (var index = 0; index < states.Count; index++)
        {
            var state = states[index];
            var snapshot = snapshots[index];
            Assert.Equal((long)index, state.Version);
            Assert.Equal(index, state.Events.Count);
            Assert.Equal(snapshot.Status, state.Status);
            Assert.Equal(snapshot.LastOccurredAt, state.LastOccurredAt);
            Assert.Equal(snapshot.Version, state.Version);
            Assert.Equal(snapshot.Events, state.Events);
        }

        Assert.Equal(ShipmentStatus.Registered, states[0].Status);
        Assert.Empty(states[0].Events);
        Assert.Equal(ShipmentStatus.Delivered, states[^1].Status);

        AssertEventsCannotBeMutatedThroughRuntimeListCast(states[0]);
        AssertEventsCannotBeMutatedThroughRuntimeListCast(states[^1]);
    }

    private static void AssertEventsCannotBeMutatedThroughRuntimeListCast(ShipmentState state)
    {
        var runtimeList = state.Events as IList<LifecycleEvent>;
        if (runtimeList is null)
        {
            return;
        }

        var before = state.Events.ToArray();
        var candidate = DomainFixture.Event(status: ShipmentStatus.Delivered);
        var addError = Record.Exception(() => runtimeList.Add(candidate));
        if (addError is null)
        {
            runtimeList.RemoveAt(runtimeList.Count - 1);
        }

        Assert.NotNull(addError);
        Assert.Equal(before, state.Events);

        var setError = Record.Exception(() => runtimeList[0] = candidate);
        Assert.NotNull(setError);
        Assert.Equal(before, state.Events);
    }

    private static StateSnapshot Snapshot(ShipmentState state) =>
        new(state.Status, state.LastOccurredAt, state.Version, state.Events.ToArray());

    private sealed record StateSnapshot(
        ShipmentStatus Status,
        DateTimeOffset LastOccurredAt,
        long Version,
        LifecycleEvent[] Events);
}
