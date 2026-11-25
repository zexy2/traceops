using TraceOps.Application;
using TraceOps.Domain;
using Xunit;

namespace TraceOps.Web.Tests;

public sealed class DomainCodeMappingTests
{
    public static IEnumerable<object[]> Codes =>
    [
        ["Applied", "APPLIED"], ["Duplicate", "DUPLICATE"],
        ["EventIdConflict", "EVENT_ID_CONFLICT"], ["InvalidEventId", "INVALID_EVENT_ID"],
        ["InvalidTrackingNumber", "INVALID_TRACKING_NUMBER"],
        ["TrackingNumberMismatch", "TRACKING_NUMBER_MISMATCH"],
        ["InvalidStatus", "INVALID_STATUS"], ["InvalidSourceSystem", "INVALID_SOURCE_SYSTEM"],
        ["TimestampMustBeUtc", "TIMESTAMP_MUST_BE_UTC"],
        ["EventTimeRegression", "EVENT_TIME_REGRESSION"], ["InvalidTransition", "INVALID_TRANSITION"],
    ];

    [Theory]
    [MemberData(nameof(Codes))]
    [Trait("Acceptance", "M3-T02")]
    public void Defensive_mapping_from_nonconforming_result_store_preserves_each_domain_code(string name, string expected)
    {
        var domainResult = ResultFor(name);
        var store = new NonconformingResultStore(domainResult);
        var command = new AddShipmentEventCommand(TestData.Tracking, TestData.EventId, ShipmentStatus.Accepted, TestData.Now, "test.api");
        var service = new ShipmentService(store, new FixedTimeProvider());
        var result = service.AddEvent(command);
        Assert.Equal(expected, result.Code);
        Assert.Equal(1, store.Calls);
        Assert.Equal(TestData.Event(), store.Captured);
        Assert.Equal(domainResult.State.TrackingNumber, result.Shipment!.TrackingNumber);
        Assert.Equal(domainResult.State.Status, result.Shipment.Status);
        Assert.Equal(domainResult.State.Version, result.Shipment.Version);
        Assert.True(domainResult.State.LastOccurredAt.EqualsExact(result.Shipment.LastOccurredAt));
        Assert.Equal(domainResult.RecordedEvent, result.RecordedEvent);
    }

    // Pure public frozen-domain outputs, not fabricated ApplyEventResult instances.
    internal static ApplyEventResult ResultFor(string name)
    {
        var state = ShipmentState.Register(TestData.Tracking, TestData.Now);
        var original = TestData.Event();
        if (name is "Duplicate" or "EventIdConflict")
        {
            state = ShipmentLifecycle.Apply(state, original).State;
            var input = name == "Duplicate" ? original with { } : original with { SourceSystem = "TEST.API" };
            return ShipmentLifecycle.Apply(state, input);
        }
        var candidate = name switch
        {
            "Applied" => original,
            "InvalidEventId" => original with { EventId = Guid.Empty },
            "InvalidTrackingNumber" => original with { TrackingNumber = "lower" },
            "TrackingNumberMismatch" => original with { TrackingNumber = "OTHER" },
            "InvalidStatus" => original with { Status = (ShipmentStatus)999 },
            "InvalidSourceSystem" => original with { SourceSystem = "bad source" },
            "TimestampMustBeUtc" => original with { OccurredAt = TestData.Now.ToOffset(TimeSpan.FromHours(2)) },
            "EventTimeRegression" => original with { OccurredAt = TestData.Now.AddTicks(-1) },
            "InvalidTransition" => original with { Status = ShipmentStatus.Delivered },
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        return ShipmentLifecycle.Apply(state, candidate);
    }

    // Deliberately nonconforming: returns a controlled public-domain result unrelated to
    // the valid command. InvalidTrackingNumber and TrackingNumberMismatch are unreachable
    // with a conforming keyed store; these are defensive mapping tests, not store evidence.
    // Actual atomic store behavior is tested separately by AtomicStoreTests.
    private sealed class NonconformingResultStore(ApplyEventResult result) : IShipmentStore
    {
        public int Calls;
        public LifecycleEvent? Captured;
        public ApplyEventResult? ApplyAtomically(LifecycleEvent input) { Calls++; Captured = input; return result; }
        public IReadOnlyList<ShipmentState> List() => throw new InvalidOperationException("Mapping must not read store.");
        public ShipmentState? Find(string trackingNumber) => throw new InvalidOperationException("Mapping must not perform read-then-write.");
        public bool TryAdd(ShipmentState state) => throw new InvalidOperationException("Mapping must not register.");
    }
}
