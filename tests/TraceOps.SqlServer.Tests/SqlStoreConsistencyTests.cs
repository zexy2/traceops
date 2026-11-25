using TraceOps.Domain;
using Xunit;

namespace TraceOps.SqlServer.Tests;

public sealed class SqlStoreConsistencyTests
{
    [Fact, Trait("Acceptance", "SQL-03")]
    public async Task Registration_persists_across_new_adapters_and_fresh_connections()
    {
        await using var f = await LiveSqlFixture.Create();
        var tracking = LiveSqlFixture.Tracking();
        Assert.True(f.Store().TryAdd(ShipmentState.Register(tracking, LiveSqlFixture.Time)));
        Assert.False(f.Store().TryAdd(ShipmentState.Register(tracking, LiveSqlFixture.Time.AddTicks(1))));
        var state = Assert.IsType<ShipmentState>(f.Store().Find(tracking));
        Assert.Equal(0, state.Version);
        Assert.True(state.LastOccurredAt.EqualsExact(LiveSqlFixture.Time));
        Assert.Empty(state.Events);
        Assert.Equal(ShipmentStatus.Registered, state.Status);
        Assert.Single(f.Store().List());
        await using var c = await f.OpenAdmin();
        Assert.Equal(LiveSqlFixture.Time, await LiveSqlFixture.Scalar(c, $"SELECT RegisteredAt FROM {f.Q}.Shipments"));
    }
    [Fact, Trait("Acceptance", "SQL-03")]
    public async Task Same_timestamp_events_keep_append_order_and_seven_digit_precision_on_replay()
    {
        await using var f = await LiveSqlFixture.Create();
        await f.Seed("A");
        var time = LiveSqlFixture.Time.AddTicks(1);
        var events = new[] {
            LiveSqlFixture.Event("A", ShipmentStatus.Accepted, id: Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), at: time),
            LiveSqlFixture.Event("A", ShipmentStatus.InTransit, id: Guid.Parse("00000000-0000-0000-0000-000000000001"), at: time),
            LiveSqlFixture.Event("A", ShipmentStatus.OutForDelivery, at: time) };
        foreach (var item in events) Assert.Equal(ApplyEventCode.Applied, f.Store().ApplyAtomically(item)?.Code);
        var state = Assert.IsType<ShipmentState>(f.Store().Find("A"));
        Assert.Equal(3, state.Version);
        Assert.Equal(ShipmentStatus.OutForDelivery, state.Status);
        Assert.Equal(events, state.Events);
        Assert.All(state.Events, item => Assert.True(item.OccurredAt.EqualsExact(time)));
        Assert.Equal(3, await f.EventCount("A"));
    }
    [Fact, Trait("Acceptance", "SQL-03")]
    public async Task List_orders_tracking_ordinal_and_histories_by_sequence()
    {
        await using var f = await LiveSqlFixture.Create();
        foreach (var tracking in new[] { "Z", "A-2", "A-10", "0" }) await f.Seed(tracking);
        Assert.Equal(new[] { "0", "A-10", "A-2", "Z" }, f.Store().List().Select(s => s.TrackingNumber));
    }
    [Fact, Trait("Acceptance", "SQL-04")]
    public async Task Missing_parent_returns_null_before_event_validation()
    {
        await using var f = await LiveSqlFixture.Create();
        Assert.Null(f.Store().ApplyAtomically(LiveSqlFixture.Event("MISSING", id: Guid.Empty)));
        Assert.Null(f.Store().Find("MISSING"));
    }
    [Fact, Trait("Acceptance", "SQL-04")]
    public async Task Exact_duplicate_returns_original_record_and_does_not_increment_version()
    {
        await using var f = await LiveSqlFixture.Create();
        await f.Seed("A");
        var input = LiveSqlFixture.Event("A");
        Assert.Equal(ApplyEventCode.Applied, f.Store().ApplyAtomically(input)?.Code);
        var duplicate = Assert.IsType<ApplyEventResult>(f.Store().ApplyAtomically(input with { }));
        Assert.Equal(ApplyEventCode.Duplicate, duplicate.Code);
        Assert.Equal(input, duplicate.RecordedEvent);
        Assert.Equal(1, duplicate.State.Version);
        Assert.Equal(1, await f.EventCount("A"));
    }
    [Theory, InlineData("offset"), InlineData("sourceCase"), InlineData("status"), InlineData("timestamp"), InlineData("badSource"), Trait("Acceptance", "SQL-04"), Trait("Acceptance", "SQL-05")]
    public async Task Existing_eventId_payload_conflict_precedes_new_payload_validation(string change)
    {
        await using var f = await LiveSqlFixture.Create();
        await f.Seed("A");
        var input = LiveSqlFixture.Event("A");
        Assert.Equal(ApplyEventCode.Applied, f.Store().ApplyAtomically(input)?.Code);
        var changed = change switch {
            "offset" => input with { OccurredAt = input.OccurredAt.ToOffset(TimeSpan.FromHours(1)) },
            "sourceCase" => input with { SourceSystem = "src" },
            "status" => input with { Status = (ShipmentStatus)99 },
            "timestamp" => input with { OccurredAt = input.OccurredAt.AddTicks(1) },
            _ => input with { SourceSystem = "SRC " } };
        var result = Assert.IsType<ApplyEventResult>(f.Store().ApplyAtomically(changed));
        Assert.Equal(ApplyEventCode.EventIdConflict, result.Code);
        Assert.Null(result.RecordedEvent);
        Assert.Equal(1, result.State.Version);
        Assert.Equal(input, Assert.Single(Assert.IsType<ShipmentState>(f.Store().Find("A")).Events));
        Assert.Equal(1, await f.EventCount("A"));
    }
    [Theory, InlineData("emptyId", ApplyEventCode.InvalidEventId), InlineData("badStatus", ApplyEventCode.InvalidStatus),
        InlineData("source", ApplyEventCode.InvalidSourceSystem), InlineData("offset", ApplyEventCode.TimestampMustBeUtc),
        InlineData("regression", ApplyEventCode.EventTimeRegression), InlineData("transition", ApplyEventCode.InvalidTransition), Trait("Acceptance", "SQL-04")]
    public async Task Validation_order_matches_contract_and_rejection_never_writes(string change, ApplyEventCode expected)
    {
        await using var f = await LiveSqlFixture.Create();
        await f.Seed("A");
        var input = LiveSqlFixture.Event("A");
        input = change switch {
            "emptyId" => input with { EventId = Guid.Empty, Status = (ShipmentStatus)99, SourceSystem = " " },
            "badStatus" => input with { Status = (ShipmentStatus)99, SourceSystem = " " },
            "source" => input with { SourceSystem = " ", OccurredAt = input.OccurredAt.ToOffset(TimeSpan.FromHours(1)) },
            "offset" => input with { OccurredAt = LiveSqlFixture.Time.AddDays(-1).ToOffset(TimeSpan.FromHours(1)) },
            "regression" => input with { OccurredAt = LiveSqlFixture.Time.AddTicks(-1), Status = ShipmentStatus.Delivered },
            _ => input with { Status = ShipmentStatus.Delivered } };
        var result = Assert.IsType<ApplyEventResult>(f.Store().ApplyAtomically(input));
        Assert.Equal(expected, result.Code);
        Assert.Equal(0, result.State.Version);
        Assert.Equal(ShipmentStatus.Registered, result.State.Status);
        Assert.Null(result.RecordedEvent);
        Assert.Equal(0, await f.EventCount("A"));
        Assert.True(Assert.IsType<ShipmentState>(f.Store().Find("A")).LastOccurredAt.EqualsExact(LiveSqlFixture.Time));
    }
    [Theory, InlineData("a"), InlineData("A "), InlineData("Ａ"), InlineData("é"), Trait("Acceptance", "SQL-05")]
    public async Task Invalid_tracking_is_not_normalized_to_existing_parent(string tracking)
    {
        await using var f = await LiveSqlFixture.Create();
        await f.Seed("A");
        Assert.Null(f.Store().Find(tracking));
        Assert.Null(f.Store().ApplyAtomically(LiveSqlFixture.Event(tracking)));
        Assert.Equal(0, await f.EventCount("A"));
    }
    [Theory, InlineData("projection"), InlineData("version"), InlineData("sequence"), InlineData("replay"), Trait("Acceptance", "SQL-03")]
    public async Task Corrupt_storage_is_reported_without_silent_healing(string corruption)
    {
        await using var f = await LiveSqlFixture.Create();
        await f.Seed("A");
        await using (var c = await f.OpenAdmin())
        {
            await LiveSqlFixture.Execute(c, $"INSERT {f.Q}.ShipmentEvents(TrackingNumber,EventSequence,EventId,Status,OccurredAt,SourceSystem) VALUES('A',@seq,@id,@status,@at,'SRC')",
                ("@seq", corruption == "sequence" ? 2L : 1L), ("@id", Guid.NewGuid()), ("@status", corruption == "replay" ? 5 : 1), ("@at", LiveSqlFixture.Time.AddTicks(1)));
            await LiveSqlFixture.Execute(c, $"UPDATE {f.Q}.Shipments SET Version=@version,Status=@status,LastOccurredAt=@at WHERE TrackingNumber='A'",
                ("@version", corruption == "version" ? 2L : 1L), ("@status", corruption == "projection" ? 2 : corruption == "replay" ? 5 : 1), ("@at", LiveSqlFixture.Time.AddTicks(1)));
        }
        var baseline = await f.Snapshot();
        var observations = new List<(Exception? Error, string Snapshot)>();
        foreach (var read in new Action[] { () => f.Store().ApplyAtomically(LiveSqlFixture.Event("A")), () => f.Store().Find("A"), () => f.Store().List() })
        {
            var error = Record.Exception(read);
            observations.Add((error, await f.Snapshot()));
        }
        f.RecordProbe("integrity-snapshots", new { corruption,
            before = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(baseline))),
            after = observations.Select(x => new { snapshot = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(x.Snapshot))), errorType = x.Error?.GetType().Name }) });
        // Check every complete corrupted state even when the target throws the wrong exception.
        Assert.All(observations, observation => Assert.Equal(baseline, observation.Snapshot));
        Assert.All(observations, observation =>
        {
            Assert.NotNull(observation.Error);
            Assert.IsNotType<NotImplementedException>(observation.Error);
        });
    }
}
