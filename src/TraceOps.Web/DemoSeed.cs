using TraceOps.Application;
using TraceOps.Domain;

namespace TraceOps.Web;

internal static class DemoSeed
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    public static void Load(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IShipmentStore>();
        var shipmentService = new ShipmentService(store, new SeedTimeProvider(Start));

        Require(shipmentService.Register(new RegisterShipmentCommand("DEMO-001")), "REGISTERED", "DEMO-001");
        Require(shipmentService.Register(new RegisterShipmentCommand("DEMO-002")), "REGISTERED", "DEMO-002");
        Require(shipmentService.Register(new RegisterShipmentCommand("DEMO-003")), "REGISTERED", "DEMO-003");

        Require(shipmentService.AddEvent(new AddShipmentEventCommand(
            "DEMO-002",
            Guid.Parse("00000000-0000-0000-0000-000000000201"),
            ShipmentStatus.Accepted,
            Start.AddMinutes(1),
            "demo-seed")), "APPLIED", "DEMO-002 event 1");
        Require(shipmentService.AddEvent(new AddShipmentEventCommand(
            "DEMO-002",
            Guid.Parse("00000000-0000-0000-0000-000000000202"),
            ShipmentStatus.InTransit,
            Start.AddMinutes(2),
            "demo-seed")), "APPLIED", "DEMO-002 event 2");
        Require(shipmentService.AddEvent(new AddShipmentEventCommand(
            "DEMO-003",
            Guid.Parse("00000000-0000-0000-0000-000000000301"),
            ShipmentStatus.Cancelled,
            Start.AddMinutes(1),
            "demo-seed")), "APPLIED", "DEMO-003 event 1");
    }

    private static void Require(ShipmentOperationResult result, string expectedCode, string subject)
    {
        if (!string.Equals(result.Code, expectedCode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Demo seed failed for {subject}: expected {expectedCode}, got {result.Code}.");
        }
    }

    private sealed class SeedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
