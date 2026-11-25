using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TraceOps.Web.Tests;

public sealed class ApiTests
{
    [Fact]
    [Trait("Acceptance", "M3-T04")]
    public async Task REST_success_chain_and_old_duplicate_return_current_terminal_projection()
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        using var registered = await WebHarness.JsonPost(client, WebHarness.Api, "{\"trackingNumber\":\"TEST-001\"}");
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        Assert.Equal("/api/v1/shipments/TEST-001", registered.Headers.Location?.OriginalString);
        Assert.Equal("application/json", registered.Content.Headers.ContentType?.MediaType);
        var initial = await WebHarness.Json(registered);
        AssertSummary(initial, "Registered", 0, TestData.Now);
        Assert.False(initial.TryGetProperty("events", out _));
        WebHarness.SameSummary(initial, await WebHarness.Summary(client));

        var statuses = new[] { "Accepted", "InTransit", "OutForDelivery", "Delivered" };
        for (var index = 0; index < statuses.Length; index++)
        {
            var id = index == 0 ? TestData.EventId : Guid.NewGuid();
            var time = TestData.Now.AddSeconds(index + 1);
            var body = TestData.EventJson(statuses[index], time.ToString("O", CultureInfo.InvariantCulture).Replace("+00:00", "Z"), id.ToString("D"));
            using var response = await WebHarness.JsonPost(client, $"{WebHarness.Api}/TEST-001/events", body);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            var json = await WebHarness.Json(response);
            Assert.Equal(id.ToString("D"), json.GetProperty("eventId").GetString());
            Assert.False(json.GetProperty("duplicate").GetBoolean());
            AssertSummary(json.GetProperty("shipment"), statuses[index], index + 1, time);
        }

        using var replay = await WebHarness.JsonPost(client, $"{WebHarness.Api}/TEST-001/events",
            TestData.EventJson(time: "2026-11-17T10:00:01Z"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var duplicate = await WebHarness.Json(replay);
        Assert.True(duplicate.GetProperty("duplicate").GetBoolean());
        Assert.Equal(TestData.EventId.ToString("D"), duplicate.GetProperty("eventId").GetString());
        AssertSummary(duplicate.GetProperty("shipment"), "Delivered", 4, TestData.Now.AddSeconds(4));
        AssertSummary(await WebHarness.Summary(client), "Delivered", 4, TestData.Now.AddSeconds(4));
    }

    public static IEnumerable<object[]> InvalidRegistrationBodies =>
    [
        ["{", 400, "INVALID_REQUEST"],
        ["{}", 400, "INVALID_REQUEST"],
        ["{\"trackingNumber\":null}", 400, "INVALID_REQUEST"],
        ["{\"trackingNumber\":42}", 400, "INVALID_REQUEST"],
        ["[]", 400, "INVALID_REQUEST"],
        ["null", 400, "INVALID_REQUEST"],
        ["{\"TrackingNumber\":\"TEST-001\"}", 400, "INVALID_REQUEST"],
        ["{\"trackingNumber\":\"TEST-001\",\"trackingNumber\":\"TEST-001\"}", 400, "INVALID_REQUEST"],
        ["{\"trackingNumber\":\"\"}", 422, "INVALID_TRACKING_NUMBER"],
        ["{\"trackingNumber\":\"TEST-001\",\"extra\":true}", 400, "INVALID_REQUEST"],
        ["{\"trackingNumber\":\"lower\"}", 422, "INVALID_TRACKING_NUMBER"],
        ["{\"trackingNumber\":\"TEST-001\\n\"}", 422, "INVALID_TRACKING_NUMBER"],
        ["{\"trackingNumber\":\"TÉST\"}", 422, "INVALID_TRACKING_NUMBER"],
        ["{\"trackingNumber\":\"" + new string('A', 33) + "\"}", 422, "INVALID_TRACKING_NUMBER"],
    ];

    [Theory]
    [MemberData(nameof(InvalidRegistrationBodies))]
    [Trait("Acceptance", "M3-T05")]
    public async Task Register_errors_return_safe_problem_and_leave_existing_state_unchanged(string body, int status, string code)
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        await WebHarness.Register(client);
        var before = await WebHarness.Summary(client);
        using var response = await WebHarness.JsonPost(client, WebHarness.Api, body);
        await WebHarness.Problem(response, status, code, WebHarness.Api);
        WebHarness.SameSummary(before, await WebHarness.Summary(client));
        using var scope = factory.Services.CreateScope();
        Assert.Single(scope.ServiceProvider.GetRequiredService<TraceOps.Application.ShipmentService>().List());
    }

    public static IEnumerable<object[]> InvalidEventBodies
    {
        get
        {
            yield return ["{", 400, "INVALID_REQUEST"];
            yield return ["{}", 400, "INVALID_REQUEST"];
            foreach (var propertyName in new[] { "eventId", "status", "occurredAt", "sourceSystem" })
            {
                var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(TestData.EventJson())!;
                values.Remove(propertyName);
                yield return [JsonSerializer.Serialize(values), 400, "INVALID_REQUEST"];
                values[propertyName] = null;
                yield return [JsonSerializer.Serialize(values), 400, "INVALID_REQUEST"];
            }
            yield return [TestData.EventJson(id: "not-guid"), 400, "INVALID_REQUEST"];
            yield return [TestData.EventJson(id: ""), 400, "INVALID_REQUEST"];
            yield return [TestData.EventJson(id: TestData.EventId.ToString("N")), 400, "INVALID_REQUEST"];
            yield return [TestData.EventJson(time: "not-date"), 400, "INVALID_REQUEST"];
            yield return [TestData.EventJson(time: ""), 400, "INVALID_REQUEST"];
            yield return [TestData.EventJson(time: "2026-11-17T10:00:00"), 400, "INVALID_REQUEST"];
            yield return [TestData.EventJson(time: "2026-02-30T10:00:00Z"), 400, "INVALID_REQUEST"];
            yield return [TestData.EventJson(time: "2026-11-17T10:00:00.12345678Z"), 400, "INVALID_REQUEST"];
            yield return [TestData.EventJson().Replace("}", ",\"trackingNumber\":\"OTHER\"}"), 400, "INVALID_REQUEST"];
            yield return [TestData.EventJson(status: "accepted"), 422, "INVALID_STATUS"];
            yield return [TestData.EventJson(status: "Unknown"), 422, "INVALID_STATUS"];
            yield return [TestData.EventJson(status: "1"), 422, "INVALID_STATUS"];
            yield return [TestData.EventJson().Replace("\"Accepted\"", "1"), 422, "INVALID_STATUS"];
            yield return [TestData.EventJson(id: Guid.Empty.ToString("D")), 422, "INVALID_EVENT_ID"];
            yield return [TestData.EventJson(source: ""), 422, "INVALID_SOURCE_SYSTEM"];
            yield return [TestData.EventJson(source: "bad source"), 422, "INVALID_SOURCE_SYSTEM"];
            yield return [TestData.EventJson(source: "source\n"), 422, "INVALID_SOURCE_SYSTEM"];
            yield return [TestData.EventJson(source: new string('a', 65)), 422, "INVALID_SOURCE_SYSTEM"];
            yield return [TestData.EventJson(time: "2026-11-17T10:00:00+00:00"), 422, "TIMESTAMP_MUST_BE_UTC"];
            yield return [TestData.EventJson(time: "2026-11-17T12:00:00+02:00"), 422, "TIMESTAMP_MUST_BE_UTC"];
            yield return [TestData.EventJson(time: "2026-11-17T09:59:59.9999999Z"), 409, "EVENT_TIME_REGRESSION"];
            yield return [TestData.EventJson(status: "Delivered"), 409, "INVALID_TRANSITION"];
            yield return [TestData.EventJson(status: ""), 422, "INVALID_STATUS"];
            foreach (var token in new[] { "true", "[]", "{}" })
                yield return [TestData.EventJson().Replace("\"Accepted\"", token), 400, "INVALID_REQUEST"];
            yield return [TestData.EventJson().Replace("\"test.api\"", "42"), 400, "INVALID_REQUEST"];
            yield return [TestData.EventJson().Replace("\"2026-11-17T10:00:00Z\"", "true"), 400, "INVALID_REQUEST"];
            yield return [TestData.EventJson().Replace("\"" + TestData.EventId.ToString("D") + "\"", "[]"), 400, "INVALID_REQUEST"];
            yield return [TestData.EventJson().Replace("\"sourceSystem\"", "\"SourceSystem\""), 400, "INVALID_REQUEST"];
            yield return [TestData.EventJson().Replace("}", ",\"status\":\"Accepted\"}"), 400, "INVALID_REQUEST"];
            yield return ["[]", 400, "INVALID_REQUEST"];
            yield return ["42", 400, "INVALID_REQUEST"];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidEventBodies))]
    [Trait("Acceptance", "M3-T05")]
    public async Task Event_error_matrix_returns_expected_problem_without_changing_state(string body, int status, string code)
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        await WebHarness.Register(client);
        var before = await WebHarness.Summary(client);
        const string path = "/api/v1/shipments/TEST-001/events";
        using var response = await WebHarness.JsonPost(client, path, body);
        await WebHarness.Problem(response, status, code, path);
        WebHarness.SameSummary(before, await WebHarness.Summary(client));
        using var scope = factory.Services.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<TraceOps.Application.ShipmentService>().Find(TestData.Tracking)!.Events);
    }

    [Fact]
    [Trait("Acceptance", "M3-T05")]
    public async Task Tracking_conflict_and_changed_same_id_offset_are_409_without_writes()
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        await WebHarness.Register(client);
        using var conflict = await WebHarness.JsonPost(client, WebHarness.Api, "{\"trackingNumber\":\"TEST-001\"}");
        await WebHarness.Problem(conflict, 409, "TRACKING_NUMBER_CONFLICT", WebHarness.Api);
        const string path = "/api/v1/shipments/TEST-001/events";
        using var applied = await WebHarness.JsonPost(client, path, TestData.EventJson());
        Assert.Equal(HttpStatusCode.Created, applied.StatusCode);
        var before = await WebHarness.Summary(client);
        foreach (var changed in new[]
        {
            TestData.EventJson(time: "2026-11-17T12:00:00+02:00"),
            TestData.EventJson(source: "TEST.API"),
        })
        {
            using var response = await WebHarness.JsonPost(client, path, changed);
            await WebHarness.Problem(response, 409, "EVENT_ID_CONFLICT", path);
            WebHarness.SameSummary(before, await WebHarness.Summary(client));
        }
    }

    [Theory]
    [InlineData("lower")]
    [InlineData("ABSENT")]
    [Trait("Acceptance", "M3-T05")]
    public async Task Missing_or_noncanonical_routes_are_404_for_GET_and_events(string tracking)
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        var path = $"{WebHarness.Api}/{tracking}";
        using var get = await client.GetAsync(path);
        await WebHarness.Problem(get, 404, "SHIPMENT_NOT_FOUND", path);
        using var post = await WebHarness.JsonPost(client, path + "/events", TestData.EventJson());
        await WebHarness.Problem(post, 404, "SHIPMENT_NOT_FOUND", path + "/events");
    }

    [Fact]
    [Trait("Acceptance", "M3-T06")]
    public async Task Unknown_API_route_is_safe_NOT_FOUND_problem_without_query_in_instance()
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        using var response = await client.GetAsync("/api/v1/unknown?secret=" + TestData.Secret);
        await WebHarness.Problem(response, 404, "NOT_FOUND", "/api/v1/unknown");
    }

    [Theory]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("text/plain")]
    [Trait("Acceptance", "M3-T05")]
    public async Task API_rejects_non_JSON_before_calling_store(string contentType)
    {
        var probe = new ProbeStore { ThrowOnAccess = true };
        using var factory = new DemoFactory(store: probe);
        using var client = factory.Client();
        using var response = await client.PostAsync(WebHarness.Api,
            new StringContent("TrackingNumber=TEST-001", Encoding.UTF8, contentType));
        await WebHarness.Problem(response, 415, "UNSUPPORTED_MEDIA_TYPE", WebHarness.Api);
        Assert.Equal(0, probe.Writes);
    }

    [Fact]
    [Trait("Acceptance", "M3-T05")]
    public async Task Exactly_16384_bytes_is_accepted_but_one_more_is_rejected_before_mutation()
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        var valid = "{\"trackingNumber\":\"TEST-001\"}";
        var exact = valid + new string(' ', 16384 - Encoding.UTF8.GetByteCount(valid));
        Assert.Equal(16384, Encoding.UTF8.GetByteCount(exact));
        using var accepted = await WebHarness.JsonPost(client, WebHarness.Api, exact);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var before = await WebHarness.Summary(client);
        using var rejected = await WebHarness.JsonPost(client, WebHarness.Api, exact + " ");
        await WebHarness.Problem(rejected, 413, "PAYLOAD_TOO_LARGE", WebHarness.Api);
        WebHarness.SameSummary(before, await WebHarness.Summary(client));
    }

    [Fact]
    [Trait("Acceptance", "M3-T05")]
    public async Task Zero_offset_without_Z_is_rejected_before_Application_store_call()
    {
        var probe = new ProbeStore { ThrowOnAccess = true };
        using var factory = new DemoFactory(store: probe);
        using var client = factory.Client();
        const string path = "/api/v1/shipments/TEST-001/events";
        using var response = await WebHarness.JsonPost(client, path,
            TestData.EventJson(time: "2026-11-17T10:00:00+00:00"));
        await WebHarness.Problem(response, 422, "TIMESTAMP_MUST_BE_UTC", path);
        Assert.Equal(0, probe.Writes);
        Assert.Equal(0, probe.Reads);
    }

    [Fact]
    [Trait("Acceptance", "M3-T05")]
    public async Task Valid_timestamp_with_seven_fraction_digits_and_source_length_64_is_accepted()
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        await WebHarness.Register(client);
        using var response = await WebHarness.JsonPost(client, "/api/v1/shipments/TEST-001/events",
            TestData.EventJson(time: "2026-11-17T10:00:00.1234567Z", source: new string('a', 64)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var summary = await WebHarness.Summary(client);
        Assert.Equal(TestData.Now.AddTicks(1234567), DateTimeOffset.Parse(summary.GetProperty("lastOccurredAt").GetString()!, CultureInfo.InvariantCulture));
    }

    internal static void AssertSummary(JsonElement summary, string status, long version, DateTimeOffset occurredAt)
    {
        Assert.Equal(new[] { "lastOccurredAt", "status", "trackingNumber", "version" }, summary.EnumerateObject().Select(item => item.Name).Order());
        Assert.Equal(TestData.Tracking, summary.GetProperty("trackingNumber").GetString());
        Assert.Equal(status, summary.GetProperty("status").GetString());
        Assert.Equal(version, summary.GetProperty("version").GetInt64());
        var time = summary.GetProperty("lastOccurredAt").GetString()!;
        Assert.EndsWith("Z", time);
        Assert.Equal(occurredAt, DateTimeOffset.Parse(time, CultureInfo.InvariantCulture));
    }
}
