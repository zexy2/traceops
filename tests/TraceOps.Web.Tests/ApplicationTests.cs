using TraceOps.Application;
using TraceOps.Domain;
using TraceOps.Web.Storage;
using Xunit;

namespace TraceOps.Web.Tests;

public sealed class ApplicationTests
{
    public static IEnumerable<object[]> InvalidTracking =>
        new[] { "", "lower", " TEST-001", "TEST-001 ", "TEST-001\n", "TÉST", new string('A', 33) }
            .Select(value => new object[] { value });

    [Fact]
    [Trait("Acceptance", "M3-T01")]
    public void Register_uses_injected_UTC_clock_and_reports_conflict_without_mutation()
    {
        var service = NewService();
        var first = service.Register(new(TestData.Tracking));
        Assert.Equal("REGISTERED", first.Code);
        Assert.NotNull(first.Shipment);
        Assert.Equal(TestData.Tracking, first.Shipment.TrackingNumber);
        Assert.Equal(ShipmentStatus.Registered, first.Shipment.Status);
        Assert.Equal(TestData.Now, first.Shipment.LastOccurredAt);
        Assert.Equal(TimeSpan.Zero, first.Shipment.LastOccurredAt.Offset);
        Assert.Equal(0L, first.Shipment.Version);
        Assert.Null(first.RecordedEvent);
        var before = service.Find(TestData.Tracking);
        var duplicate = service.Register(new(TestData.Tracking));
        Assert.Equal("TRACKING_NUMBER_CONFLICT", duplicate.Code);
        Assert.Null(duplicate.Shipment);
        Assert.Null(duplicate.RecordedEvent);
        Assert.Equal(before!.Shipment, service.Find(TestData.Tracking)!.Shipment);
        Assert.Equal(before.Events, service.Find(TestData.Tracking)!.Events);
        Assert.Single(service.List());
    }

    [Theory]
    [MemberData(nameof(InvalidTracking))]
    [Trait("Acceptance", "M3-T01")]
    public void Invalid_tracking_is_rejected_before_store_access(string tracking)
    {
        var probe = new ProbeStore { ThrowOnAccess = true };
        var service = new ShipmentService(probe, new FixedTimeProvider());
        var registration = service.Register(new(tracking));
        var added = service.AddEvent(new(tracking, TestData.EventId, ShipmentStatus.Accepted, TestData.Now, "test"));
        Assert.Equal("INVALID_TRACKING_NUMBER", registration.Code);
        Assert.Equal("INVALID_TRACKING_NUMBER", added.Code);
        Assert.Null(registration.Shipment);
        Assert.Null(added.Shipment);
        Assert.Null(registration.RecordedEvent);
        Assert.Null(added.RecordedEvent);
        Assert.Null(service.Find(tracking));
        Assert.Equal(0, probe.Writes);
        Assert.Equal(0, probe.Reads);
    }

    [Fact]
    [Trait("Acceptance", "M3-T01")]
    public void List_is_ordinal_sorted_and_returns_snapshots_independent_of_future_writes_and_casts()
    {
        var service = NewService();
        foreach (var tracking in new[] { "Z-1", "A1", "A-1", "0" })
            Assert.Equal("REGISTERED", service.Register(new(tracking)).Code);
        var list = service.List();
        Assert.Equal(new[] { "0", "A-1", "A1", "Z-1" }, list.Select(item => item.TrackingNumber));
        var details = service.Find("A-1");
        Assert.NotNull(details);
        Assert.Empty(details.Events);
        Assert.Null(service.Find("ABSENT"));
        Assert.Equal("APPLIED", service.AddEvent(new("A-1", TestData.EventId, ShipmentStatus.Accepted, TestData.Now, "test")).Code);
        Assert.Equal(0L, details.Shipment.Version);
        Assert.Empty(details.Events);
        Assert.Equal(0L, list.Single(item => item.TrackingNumber == "A-1").Version);
        TryMutate(list, new ShipmentSummary("INJECTED", ShipmentStatus.Registered, TestData.Now, 0));
        TryMutate(details.Events, TestData.Event(tracking: "A-1"));
        Assert.Null(service.Find("INJECTED"));
        Assert.Equal(4, service.List().Count);
        Assert.Single(service.Find("A-1")!.Events);
    }

    [Fact]
    [Trait("Acceptance", "M3-T02")]
    public void AddEvent_applies_duplicate_conflict_and_missing_shipment_with_current_projection()
    {
        var service = NewService();
        service.Register(new(TestData.Tracking));
        var command = new AddShipmentEventCommand(TestData.Tracking, TestData.EventId, ShipmentStatus.Accepted, TestData.Now, "test.api");
        var applied = service.AddEvent(command);
        Assert.Equal("APPLIED", applied.Code);
        Assert.Equal(1L, applied.Shipment!.Version);
        Assert.Equal(TestData.Event(), applied.RecordedEvent);
        var later = command with { EventId = Guid.NewGuid(), Status = ShipmentStatus.InTransit, OccurredAt = TestData.Now.AddSeconds(1) };
        Assert.Equal("APPLIED", service.AddEvent(later).Code);
        var duplicate = service.AddEvent(command with { });
        Assert.Equal("DUPLICATE", duplicate.Code);
        Assert.Equal(ShipmentStatus.InTransit, duplicate.Shipment!.Status);
        Assert.Equal(2L, duplicate.Shipment.Version);
        Assert.Equal(TestData.Event(), duplicate.RecordedEvent);
        var before = service.Find(TestData.Tracking)!;
        var conflict = service.AddEvent(command with { SourceSystem = "TEST.API" });
        Assert.Equal("EVENT_ID_CONFLICT", conflict.Code);
        Assert.Equal(before.Shipment, conflict.Shipment);
        Assert.Null(conflict.RecordedEvent);
        Assert.Equal(before.Events, service.Find(TestData.Tracking)!.Events);
        var absent = service.AddEvent(command with { TrackingNumber = "ABSENT" });
        Assert.Equal("SHIPMENT_NOT_FOUND", absent.Code);
        Assert.Null(absent.Shipment);
        Assert.Null(absent.RecordedEvent);
    }

    public static IEnumerable<object[]> Rejections =>
    [
        ["empty-id", "INVALID_EVENT_ID"],
        ["status", "INVALID_STATUS"],
        ["source", "INVALID_SOURCE_SYSTEM"],
        ["offset", "TIMESTAMP_MUST_BE_UTC"],
        ["regression", "EVENT_TIME_REGRESSION"],
        ["transition", "INVALID_TRANSITION"],
    ];

    internal static LifecycleEvent RejectedInput(string kind) => kind switch
    {
        "empty-id" => TestData.Event(id: Guid.Empty),
        "status" => TestData.Event(status: (ShipmentStatus)999),
        "source" => TestData.Event(source: "bad source"),
        "offset" => TestData.Event(time: TestData.Now.ToOffset(TimeSpan.FromHours(2))),
        "regression" => TestData.Event(time: TestData.Now.AddTicks(-1)),
        "transition" => TestData.Event(status: ShipmentStatus.Delivered),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    [Trait("Acceptance", "M3-T02")]
    public void AddEvent_propagates_domain_rejections_and_does_not_write(string kind, string code)
    {
        var input = RejectedInput(kind); // Construct before any product stub call.
        var service = NewService();
        service.Register(new(TestData.Tracking));
        var before = service.Find(TestData.Tracking)!;
        var result = service.AddEvent(new(input.TrackingNumber, input.EventId, input.Status, input.OccurredAt, input.SourceSystem));
        Assert.Equal(code, result.Code);
        Assert.Equal(before.Shipment, result.Shipment);
        Assert.Null(result.RecordedEvent);
        Assert.Equal(before.Events, service.Find(TestData.Tracking)!.Events);
    }

    [Fact]
    [Trait("Acceptance", "M3-T01")]
    public void Public_null_guards_report_contract_parameter_names()
    {
        Assert.Equal("store", Assert.Throws<ArgumentNullException>(() => new ShipmentService(null!, new FixedTimeProvider())).ParamName);
        Assert.Equal("timeProvider", Assert.Throws<ArgumentNullException>(() => new ShipmentService(new InMemoryShipmentStore(), null!)).ParamName);
        var service = NewService();
        Assert.Equal("command", Assert.Throws<ArgumentNullException>(() => service.Register(null!)).ParamName);
        Assert.Equal("command", Assert.Throws<ArgumentNullException>(() => service.AddEvent(null!)).ParamName);
        Assert.Equal("trackingNumber", Assert.Throws<ArgumentNullException>(() => service.Find(null!)).ParamName);
    }

    private static ShipmentService NewService() => new(new InMemoryShipmentStore(), new FixedTimeProvider());

    internal static void TryMutate<T>(IReadOnlyList<T> view, T value)
    {
        if (view is IList<T> list)
        {
            _ = Record.Exception(() => list.Add(value));
            if (list.Count > 0) _ = Record.Exception(() => list[0] = value);
        }
        if (view is System.Collections.IList untyped)
            _ = Record.Exception(() => untyped.Add(value));
    }
}
