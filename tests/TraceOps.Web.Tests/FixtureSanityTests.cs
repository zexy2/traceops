using System.Text.Json;
using System.Text;
using System.Globalization;
using AngleSharp.Html.Parser;
using TraceOps.Domain;
using Xunit;

namespace TraceOps.Web.Tests;

// These never construct ShipmentService/InMemoryShipmentStore or start the web host.
public sealed class FixtureSanityTests
{
    [Fact]
    [Trait("Acceptance", "FixtureSanity")]
    public void Present_empty_header_fixture_has_one_empty_value_and_is_distinct_from_absence()
    {
        var context = TransportTests.PresentEmptyCorrelationContext();
        Assert.True(context.Request.Headers.ContainsKey("X-Correlation-ID"));
        Assert.True(context.Request.Headers.TryGetValue("X-Correlation-ID", out var values));
        Assert.Equal("", Assert.Single(values));
        Assert.False(new Microsoft.AspNetCore.Http.DefaultHttpContext().Request.Headers.ContainsKey("X-Correlation-ID"));
    }

    [Theory]
    [InlineData("/api/v1/shipments")]
    [InlineData("/api/v1/shipments/TEST-001/events")]
    [Trait("Acceptance", "FixtureSanity")]
    public void Streaming_size_fixture_is_valid_route_JSON_padded_to_exactly_16385_UTF8_bytes(string path)
    {
        var body = TransportTests.OversizedJson(path);
        Assert.Equal(16385, Encoding.UTF8.GetByteCount(body));
        Assert.True(Encoding.UTF8.GetByteCount(body.TrimEnd()) <= 16384);
        Assert.All(body[body.TrimEnd().Length..], character => Assert.Equal(' ', character));
        using var document = JsonDocument.Parse(body);
        var json = document.RootElement;
        Assert.Equal(JsonValueKind.Object, json.ValueKind);
        Assert.All(json.EnumerateObject(), member => Assert.Equal(JsonValueKind.String, member.Value.ValueKind));
        if (path == "/api/v1/shipments")
        {
            Assert.Equal("trackingNumber", Assert.Single(json.EnumerateObject()).Name);
            var tracking = json.GetProperty("trackingNumber").GetString()!;
            Assert.Equal(TestData.Tracking, tracking);
            Assert.Equal(tracking, ShipmentState.Register(tracking, TestData.Now).TrackingNumber);
        }
        else
        {
            Assert.Equal(new[] { "eventId", "occurredAt", "sourceSystem", "status" }, json.EnumerateObject().Select(member => member.Name).Order());
            Assert.True(Guid.TryParseExact(json.GetProperty("eventId").GetString(), "D", out var eventId));
            Assert.NotEqual(Guid.Empty, eventId);
            Assert.True(Enum.TryParse<ShipmentStatus>(json.GetProperty("status").GetString(), false, out var status));
            var timestamp = json.GetProperty("occurredAt").GetString()!;
            Assert.EndsWith("Z", timestamp);
            var occurredAt = DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture);
            Assert.Equal(TimeSpan.Zero, occurredAt.Offset);
            Assert.Equal(TestData.Now, occurredAt);
            var input = new LifecycleEvent(eventId, TestData.Tracking, status, occurredAt, json.GetProperty("sourceSystem").GetString()!);
            Assert.Equal(ApplyEventCode.Applied, ShipmentLifecycle.Apply(ShipmentState.Register(TestData.Tracking, TestData.Now), input).Code);
        }
    }

    [Theory]
    [MemberData(nameof(MvcTests.BadEventForms), MemberType = typeof(MvcTests))]
    [Trait("Acceptance", "FixtureSanity")]
    public void MVC_form_fixtures_distinguish_missing_keys_from_explicit_empty_values(string key, string value, bool missing)
    {
        var fields = MvcTests.InvalidFormFields(key, value, missing);
        Assert.Equal(!missing, fields.ContainsKey(key));
        if (!missing) Assert.Equal(value, fields[key]);
        foreach (var other in TestData.EventForm().Keys.Where(name => name != key))
            Assert.Equal(TestData.EventForm()[other], fields[other]);
    }

    [Theory]
    [MemberData(nameof(DomainCodeMappingTests.Codes), MemberType = typeof(DomainCodeMappingTests))]
    [Trait("Acceptance", "FixtureSanity")]
    public void Mapping_result_fixtures_reach_all_declared_domain_results(string name, string _)
    {
        var result = DomainCodeMappingTests.ResultFor(name);
        Assert.Equal(name, result.Code.ToString());
        Assert.Equal(result.Succeeded, result.RecordedEvent is not null);
    }

    [Theory]
    [MemberData(nameof(ApplicationTests.Rejections), MemberType = typeof(ApplicationTests))]
    [Trait("Acceptance", "FixtureSanity")]
    public void Domain_rejection_inputs_are_constructible_and_reach_intended_error(string kind, string expected)
    {
        var input = ApplicationTests.RejectedInput(kind);
        var current = ShipmentState.Register(TestData.Tracking, TestData.Now);
        var result = ShipmentLifecycle.Apply(current, input);
        var actual = result.Code switch
        {
            ApplyEventCode.InvalidEventId => "INVALID_EVENT_ID",
            ApplyEventCode.InvalidStatus => "INVALID_STATUS",
            ApplyEventCode.InvalidSourceSystem => "INVALID_SOURCE_SYSTEM",
            ApplyEventCode.TimestampMustBeUtc => "TIMESTAMP_MUST_BE_UTC",
            ApplyEventCode.EventTimeRegression => "EVENT_TIME_REGRESSION",
            ApplyEventCode.InvalidTransition => "INVALID_TRANSITION",
            _ => throw new InvalidOperationException("Unexpected fixture domain outcome"),
        };
        Assert.Equal(expected, actual);
        if (kind == "offset")
        {
            Assert.Equal(TestData.Now, input.OccurredAt);
            Assert.Equal(TimeSpan.FromHours(2), input.OccurredAt.Offset);
            Assert.False(TestData.Now.EqualsExact(input.OccurredAt));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "FixtureSanity")]
    public void All_sixteen_race_payloads_are_individually_valid_and_have_required_identity(bool distinct)
    {
        var inputs = Enumerable.Range(0, 16).Select(index => TestData.Event(source: distinct ? $"source-{index}" : "same-source")).ToArray();
        Assert.Single(inputs.Select(item => item.EventId).Distinct());
        Assert.Equal(distinct ? 16 : 1, inputs.Distinct().Count());
        Assert.All(inputs, input => Assert.Equal(ApplyEventCode.Applied,
            ShipmentLifecycle.Apply(ShipmentState.Register(TestData.Tracking, TestData.Now), input).Code));
    }

    [Fact]
    [Trait("Acceptance", "FixtureSanity")]
    public void Serialized_event_and_forms_have_explicit_expected_values_and_no_UTC_constructor_trap()
    {
        using var document = JsonDocument.Parse(TestData.EventJson());
        Assert.Equal(TestData.EventId.ToString("D"), document.RootElement.GetProperty("eventId").GetString());
        Assert.Equal("Accepted", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("2026-11-17T10:00:00Z", document.RootElement.GetProperty("occurredAt").GetString());
        Assert.Equal(TestData.Now, new FixedTimeProvider().GetUtcNow());
        foreach (var offset in new[] { -7, 1, 2 })
        {
            var converted = TestData.Now.ToOffset(TimeSpan.FromHours(offset));
            Assert.Equal(TestData.Now, converted);
            Assert.Equal(TimeSpan.FromHours(offset), converted.Offset);
        }
        Assert.Equal(new[] { "EventId", "OccurredAt", "SourceSystem", "Status" }, TestData.EventForm().Keys.Order());
    }

    [Fact]
    [Trait("Acceptance", "FixtureSanity")]
    public async Task DOM_parser_decodes_encoded_input_without_creating_script_element()
    {
        var dom = await new HtmlParser().ParseDocumentAsync("<input name='SourceSystem' value='&lt;script&gt;alert(1)&lt;/script&gt;'>");
        Assert.Empty(dom.QuerySelectorAll("script"));
        Assert.Equal("<script>alert(1)</script>", dom.QuerySelector("input")!.GetAttribute("value"));
    }

    [Theory]
    [MemberData(nameof(SeedAndViewTests.FreshStatuses), MemberType = typeof(SeedAndViewTests))]
    [Trait("Acceptance", "FixtureSanity")]
    public void Every_view_state_fixture_is_reachable_without_calling_product_stubs(ShipmentStatus status, string next)
    {
        var state = SeedAndViewTests.StateAt(status);
        Assert.Equal(status, state.Status);
        if (next.Length > 0)
        {
            var nextStatus = Enum.Parse<ShipmentStatus>(next);
            Assert.Equal(ApplyEventCode.Applied, ShipmentLifecycle.Apply(state,
                TestData.Event(status: nextStatus, id: Guid.NewGuid())).Code);
        }
    }

    [Fact]
    [Trait("Acceptance", "FixtureSanity")]
    public void Public_page_record_contract_retains_exact_strings_and_Application_DTO_types()
    {
        var summary = new TraceOps.Application.ShipmentSummary(TestData.Tracking, ShipmentStatus.Registered, TestData.Now, 0);
        var list = new TraceOps.Web.Models.ListPage(new[] { summary }, "raw-tracking");
        Assert.Equal("raw-tracking", list.RetainedTrackingNumber);
        Assert.Equal(summary, Assert.Single(list.Shipments));
        var details = new TraceOps.Application.ShipmentDetails(summary, Array.Empty<LifecycleEvent>());
        var page = new TraceOps.Web.Models.DetailPage(details, "raw-id", "Unknown", "raw-time", "raw-source");
        Assert.Equal(details, page.Details);
        Assert.Equal("raw-id", page.RetainedEventId);
        Assert.Equal("Unknown", page.RetainedStatus);
        Assert.Equal("raw-time", page.RetainedOccurredAt);
        Assert.Equal("raw-source", page.RetainedSourceSystem);
    }
}
