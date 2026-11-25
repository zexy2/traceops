using System.Globalization;
using AngleSharp.Html.Dom;
using Microsoft.Extensions.DependencyInjection;
using TraceOps.Application;
using TraceOps.Domain;
using TraceOps.Web.Storage;
using Xunit;

namespace TraceOps.Web.Tests;

public sealed class SeedAndViewTests
{
    public static IEnumerable<object[]> FreshStatuses =>
    [
        [ShipmentStatus.Registered, "Accepted"],
        [ShipmentStatus.Accepted, "InTransit"],
        [ShipmentStatus.InTransit, "OutForDelivery"],
        [ShipmentStatus.OutForDelivery, "Delivered"],
        [ShipmentStatus.DeliveryFailed, "OutForDelivery"],
        [ShipmentStatus.Delivered, ""],
        [ShipmentStatus.Cancelled, ""],
    ];

    [Theory]
    [MemberData(nameof(FreshStatuses))]
    [Trait("Acceptance", "M3-T10")]
    public async Task Fresh_GET_has_legal_status_default_all_enum_options_and_terminal_form_policy(ShipmentStatus status, string expected)
    {
        var state = StateAt(status);
        var store = new InMemoryShipmentStore();
        Assert.True(store.TryAdd(state));
        using var factory = new DemoFactory(store: store);
        using var client = factory.Client();
        using var response = await client.GetAsync("/shipments/TEST-001");
        var document = await WebHarness.Html(response, 200);
        Assert.Contains(status.ToString(), document.Body!.TextContent);
        if (expected == "")
        {
            Assert.DoesNotContain(document.Forms, form => form.GetAttribute("action") == "/shipments/TEST-001/events");
            Assert.Contains("No further lifecycle transitions.", document.Body.TextContent);
        }
        else
        {
            var form = WebHarness.Form(document, "/shipments/TEST-001/events");
            var select = Assert.IsAssignableFrom<IHtmlSelectElement>(form.QuerySelector("select[name='Status']"));
            Assert.Equal(expected, select.Value);
            Assert.Equal(Enum.GetNames<ShipmentStatus>().Order(), select.Options.Select(option => option.Value).Order());
        }
        Assert.Equal(state.Version, store.Find(TestData.Tracking)!.Version);
        Assert.Equal(state.Events, store.Find(TestData.Tracking)!.Events);
    }

    [Fact]
    [Trait("Acceptance", "M3-T10")]
    public async Task Seed_true_has_exact_three_domain_histories_and_visible_demo_labels()
    {
        using var factory = new DemoFactory(seed: true);
        using var client = factory.Client();
        using var response = await client.GetAsync("/shipments");
        var document = await WebHarness.Html(response, 200);
        var links = document.QuerySelectorAll("a[href]")
            .Where(link => link.GetAttribute("href")!.StartsWith("/shipments/DEMO-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "/shipments/DEMO-001", "/shipments/DEMO-002", "/shipments/DEMO-003" }, links.Select(link => link.GetAttribute("href")));
        foreach (var link in links)
            Assert.Contains("Demo seed", link.Closest("tr")!.TextContent);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ShipmentService>();
        Assert.Equal(new[] { "DEMO-001", "DEMO-002", "DEMO-003" }, service.List().Select(item => item.TrackingNumber));
        var registered = service.Find("DEMO-001")!;
        Assert.Equal(ShipmentStatus.Registered, registered.Shipment.Status);
        Assert.Equal(0L, registered.Shipment.Version);
        Assert.Equal(SeedTime(0), registered.Shipment.LastOccurredAt);
        Assert.Empty(registered.Events);
        AssertSeed(service.Find("DEMO-002")!, ShipmentStatus.InTransit, new[]
        {
            new LifecycleEvent(Guid.Parse("00000000-0000-0000-0000-000000000201"), "DEMO-002", ShipmentStatus.Accepted, SeedTime(1), "demo-seed"),
            new LifecycleEvent(Guid.Parse("00000000-0000-0000-0000-000000000202"), "DEMO-002", ShipmentStatus.InTransit, SeedTime(2), "demo-seed"),
        });
        AssertSeed(service.Find("DEMO-003")!, ShipmentStatus.Cancelled, new[]
        {
            new LifecycleEvent(Guid.Parse("00000000-0000-0000-0000-000000000301"), "DEMO-003", ShipmentStatus.Cancelled, SeedTime(1), "demo-seed"),
        });
        foreach (var tracking in new[] { "DEMO-001", "DEMO-002", "DEMO-003" })
        {
            using var detailResponse = await client.GetAsync("/shipments/" + tracking);
            var detail = await WebHarness.Html(detailResponse, 200);
            Assert.Contains("Demo seed", detail.Body!.TextContent);
        }
        using var again = await client.GetAsync("/shipments");
        await WebHarness.Html(again, 200);
        Assert.Equal(2L, service.Find("DEMO-002")!.Shipment.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "M3-T10")]
    public async Task New_host_resets_user_changes_and_seed_switch_controls_initial_inventory(bool seed)
    {
        using (var first = new DemoFactory(seed))
        using (var client = first.Client())
        {
            await WebHarness.Register(client);
            using var scope = first.Services.CreateScope();
            Assert.Equal(seed ? 4 : 1, scope.ServiceProvider.GetRequiredService<ShipmentService>().List().Count);
        }
        using var second = new DemoFactory(seed);
        using var secondClient = second.Client();
        using var secondScope = second.Services.CreateScope();
        var service = secondScope.ServiceProvider.GetRequiredService<ShipmentService>();
        Assert.Null(service.Find(TestData.Tracking));
        Assert.Equal(seed ? 3 : 0, service.List().Count);
        using var response = await secondClient.GetAsync("/shipments");
        var document = await WebHarness.Html(response, 200);
        if (!seed) Assert.Contains("No shipments yet.", document.Body!.TextContent);
    }

    [Fact]
    [Trait("Acceptance", "M3-T10")]
    public void Default_configuration_enables_seed_without_explicit_seed_override()
    {
        using var factory = new DemoFactory(seed: null);
        using var client = factory.Client();
        using var scope = factory.Services.CreateScope();
        Assert.Equal(new[] { "DEMO-001", "DEMO-002", "DEMO-003" },
            scope.ServiceProvider.GetRequiredService<ShipmentService>().List().Select(item => item.TrackingNumber));
    }

    internal static ShipmentState StateAt(ShipmentStatus status)
    {
        var state = ShipmentState.Register(TestData.Tracking, TestData.Now);
        ShipmentStatus[] path = status switch
        {
            ShipmentStatus.Registered => [],
            ShipmentStatus.Accepted => [ShipmentStatus.Accepted],
            ShipmentStatus.InTransit => [ShipmentStatus.Accepted, ShipmentStatus.InTransit],
            ShipmentStatus.OutForDelivery => [ShipmentStatus.Accepted, ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery],
            ShipmentStatus.DeliveryFailed => [ShipmentStatus.Accepted, ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery, ShipmentStatus.DeliveryFailed],
            ShipmentStatus.Delivered => [ShipmentStatus.Accepted, ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery, ShipmentStatus.Delivered],
            ShipmentStatus.Cancelled => [ShipmentStatus.Cancelled],
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        foreach (var next in path)
        {
            var result = ShipmentLifecycle.Apply(state, TestData.Event(status: next, id: Guid.NewGuid()));
            Assert.Equal(ApplyEventCode.Applied, result.Code);
            state = result.State;
        }
        return state;
    }

    private static DateTimeOffset SeedTime(int minutes) => new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero).AddMinutes(minutes);
    private static void AssertSeed(ShipmentDetails details, ShipmentStatus status, LifecycleEvent[] events)
    {
        Assert.Equal(status, details.Shipment.Status);
        Assert.Equal((long)events.Length, details.Shipment.Version);
        Assert.Equal(events[^1].OccurredAt, details.Shipment.LastOccurredAt);
        Assert.Equal(events, details.Events);
    }
}
