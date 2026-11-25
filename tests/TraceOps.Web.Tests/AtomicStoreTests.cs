using TraceOps.Domain;
using TraceOps.Web.Storage;
using Xunit;

namespace TraceOps.Web.Tests;

public sealed class AtomicStoreTests
{
    [Fact]
    [Trait("Acceptance", "M3-T03")]
    public async Task Sixteen_barrier_released_registrations_have_one_winner()
    {
        var state = ShipmentState.Register(TestData.Tracking, TestData.Now);
        var store = new InMemoryShipmentStore();
        var results = await Race(index => store.TryAdd(state));
        Assert.Equal(1, results.Count(result => result));
        var saved = Assert.Single(store.List());
        Assert.Equal(0L, saved.Version);
        Assert.Empty(saved.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "M3-T03")]
    public async Task Sixteen_atomic_events_have_one_applied_and_fifteen_expected_nonwrites(bool distinctPayloads)
    {
        // All 16 inputs are constructed and validated by fixture-sanity tests separately.
        var inputs = Enumerable.Range(0, 16)
            .Select(index => TestData.Event(source: distinctPayloads ? $"source-{index}" : "same-source")).ToArray();
        var store = new InMemoryShipmentStore();
        Assert.True(store.TryAdd(ShipmentState.Register(TestData.Tracking, TestData.Now)));
        var before = store.Find(TestData.Tracking)!;
        var oldList = store.List();
        var results = await Race(index =>
        {
            var result = store.ApplyAtomically(inputs[index]);
            var snapshot = Assert.Single(store.List());
            Assert.Equal((long)snapshot.Events.Count, snapshot.Version);
            Assert.Equal(ShipmentStatus.Accepted, snapshot.Status);
            Assert.Single(snapshot.Events);
            return result;
        });
        Assert.All(results, result => Assert.NotNull(result));
        Assert.Single(results, result => result!.Code == ApplyEventCode.Applied);
        var expected = distinctPayloads ? ApplyEventCode.EventIdConflict : ApplyEventCode.Duplicate;
        Assert.Equal(15, results.Count(result => result!.Code == expected));
        var final = store.Find(TestData.Tracking)!;
        Assert.Equal(1L, final.Version);
        var winner = Assert.Single(final.Events);
        Assert.Contains(winner, inputs);
        Assert.Equal(ShipmentStatus.Accepted, final.Status);
        Assert.All(results, result =>
        {
            Assert.Equal(1L, result!.State.Version);
            Assert.Equal(winner, Assert.Single(result.State.Events));
        });
        Assert.Equal(0L, before.Version);
        Assert.Empty(before.Events);
        Assert.Equal(0L, Assert.Single(oldList).Version);
        var replay = store.ApplyAtomically(winner with { });
        Assert.Equal(ApplyEventCode.Duplicate, replay!.Code);
        Assert.Equal(1L, replay.State.Version);
        Assert.Single(replay.State.Events);
    }

    [Fact]
    [Trait("Acceptance", "M3-T03")]
    public void Store_instances_are_isolated_and_snapshots_cannot_mutate_storage()
    {
        var first = new InMemoryShipmentStore();
        var second = new InMemoryShipmentStore();
        Assert.Empty(first.List());
        Assert.Null(first.Find(TestData.Tracking));
        Assert.Null(first.ApplyAtomically(TestData.Event()));
        var emptySnapshot = first.List();
        ApplicationTests.TryMutate(emptySnapshot, ShipmentState.Register("INJECTED", TestData.Now));
        Assert.Null(first.Find("INJECTED"));
        Assert.True(first.TryAdd(ShipmentState.Register(TestData.Tracking, TestData.Now)));
        var snapshot = first.List();
        var old = first.Find(TestData.Tracking)!;
        Assert.Equal(ApplyEventCode.Applied, first.ApplyAtomically(TestData.Event())!.Code);
        ApplicationTests.TryMutate(snapshot, ShipmentState.Register("INJECTED", TestData.Now));
        ApplicationTests.TryMutate(old.Events, TestData.Event());
        Assert.Null(first.Find("INJECTED"));
        Assert.Equal(1L, first.Find(TestData.Tracking)!.Version);
        Assert.Single(first.Find(TestData.Tracking)!.Events);
        Assert.Empty(second.List());
    }

    [Fact]
    [Trait("Acceptance", "M3-T03")]
    public void Store_null_guards_use_public_parameter_names()
    {
        var store = new InMemoryShipmentStore();
        Assert.Equal("trackingNumber", Assert.Throws<ArgumentNullException>(() => store.Find(null!)).ParamName);
        Assert.Equal("state", Assert.Throws<ArgumentNullException>(() => store.TryAdd(null!)).ParamName);
        Assert.Equal("input", Assert.Throws<ArgumentNullException>(() => store.ApplyAtomically(null!)).ParamName);
    }

    private static async Task<T[]> Race<T>(Func<int, T> action)
    {
        using var barrier = new Barrier(17);
        var tasks = Enumerable.Range(0, 16).Select(index => Task.Factory.StartNew(() =>
        {
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Race barrier did not release.");
            return action(index);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(15)), "All 16 participants must arrive before release.");
        return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
    }
}
