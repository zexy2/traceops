using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using TraceOps.Application;
using TraceOps.Domain;
using TraceOps.Web.Middleware;
using Xunit;

namespace TraceOps.Web.Tests;

public sealed class TransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "M3-T06")]
    public async Task Valid_correlation_is_preserved_exactly_or_generated_when_absent(bool supplied)
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        const string id = "ABCDEFAB-0000-0000-0000-000000000001";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/shipments/ABSENT");
        if (supplied) request.Headers.Add("X-Correlation-ID", id);
        using var response = await client.SendAsync(request);
        await WebHarness.Problem(response, 404, "SHIPMENT_NOT_FOUND", "/api/v1/shipments/ABSENT");
        if (supplied) Assert.Equal(id, WebHarness.Correlation(response));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("non-D")]
    [InlineData("multiple")]
    [Trait("Acceptance", "M3-T06")]
    public async Task Invalid_or_multiple_correlation_is_rejected_before_mutation_with_new_safe_id(string kind)
    {
        var probe = new ProbeStore { ThrowOnAccess = true };
        using var factory = new DemoFactory(store: probe);
        using var client = factory.Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, WebHarness.Api)
        {
            Content = new StringContent("{\"trackingNumber\":\"TEST-001\"}", Encoding.UTF8, "application/json"),
        };
        var values = kind switch
        {
            "invalid" => new[] { TestData.Secret },
            "non-D" => new[] { TestData.EventId.ToString("N") },
            "multiple" => new[] { TestData.EventId.ToString("D"), Guid.NewGuid().ToString("D") },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", values);
        using var response = await client.SendAsync(request);
        await WebHarness.Problem(response, 400, "INVALID_CORRELATION_ID", WebHarness.Api);
        Assert.DoesNotContain(WebHarness.Correlation(response), values);
        Assert.Equal(0, probe.Writes);
    }

    [Fact]
    [Trait("Acceptance", "M3-T06")]
    public async Task Present_empty_correlation_is_rejected_by_real_middleware_before_downstream_or_store()
    {
        // Equivalent replacement for the HTTP theory's empty row: TestServer drops
        // that client header, so exercise the real compiled middleware with explicit arrival.
        var context = PresentEmptyCorrelationContext();
        Assert.True(context.Request.Headers.ContainsKey("X-Correlation-ID"));
        Assert.True(context.Request.Headers.TryGetValue("X-Correlation-ID", out var incoming));
        Assert.Equal("", Assert.Single(incoming));
        var store = new ProbeStore();
        var downstreamCalls = 0;
        var middleware = new CorrelationIdMiddleware(_ =>
        {
            downstreamCalls++;
            store.TryAdd(ShipmentState.Register(TestData.Tracking, TestData.Now));
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(context);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType?.Split(';')[0]);
        Assert.Equal(0, downstreamCalls);
        Assert.Equal(0, store.Writes);
        var correlation = Assert.Single(context.Response.Headers["X-Correlation-ID"]);
        Assert.True(Guid.TryParseExact(correlation, "D", out _));
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        var problem = document.RootElement;
        Assert.Equal("about:blank", problem.GetProperty("type").GetString());
        Assert.Equal("Bad Request", problem.GetProperty("title").GetString());
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal("INVALID_CORRELATION_ID", problem.GetProperty("code").GetString());
        Assert.Equal(WebHarness.Api, problem.GetProperty("instance").GetString());
        Assert.Equal(correlation, problem.GetProperty("correlationId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        WebHarness.SafeText(problem.GetRawText());
    }

    internal static DefaultHttpContext PresentEmptyCorrelationContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = WebHarness.Api;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"trackingNumber\":\"TEST-001\"}"));
        context.Request.Headers["X-Correlation-ID"] = new StringValues("");
        context.Response.Body = new MemoryStream();
        return context;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "M3-T06")]
    public async Task Fault_injected_store_returns_safe_API_problem_in_Development(bool write)
    {
        var probe = new ProbeStore { ThrowOnAccess = true };
        using var factory = new DemoFactory(store: probe);
        using var client = factory.Client();
        var path = write ? WebHarness.Api : WebHarness.Api + "/TEST-001";
        using var response = write
            ? await WebHarness.JsonPost(client, path, "{\"trackingNumber\":\"TEST-001\"}")
            : await client.GetAsync(path);
        await WebHarness.Problem(response, 500, "INTERNAL_ERROR", path);
        Assert.True(write ? probe.Writes > 0 : probe.Reads > 0, "Fault injection must reach the selected store operation.");
    }

    [Fact]
    [Trait("Acceptance", "M3-T04")]
    public void DI_store_is_singleton_service_scoped_and_test_clock_is_honored()
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        var service = first.ServiceProvider.GetRequiredService<ShipmentService>();
        Assert.Same(service, first.ServiceProvider.GetRequiredService<ShipmentService>());
        Assert.NotSame(service, second.ServiceProvider.GetRequiredService<ShipmentService>());
        Assert.Same(first.ServiceProvider.GetRequiredService<IShipmentStore>(), second.ServiceProvider.GetRequiredService<IShipmentStore>());
        Assert.Same(first.ServiceProvider.GetRequiredService<TimeProvider>(), second.ServiceProvider.GetRequiredService<TimeProvider>());
        Assert.Equal(TestData.Now, first.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow());
    }

    [Theory]
    [InlineData("/api/v1/shipments")]
    [InlineData("/api/v1/shipments/TEST-001/events")]
    [Trait("Acceptance", "M3-T05")]
    public async Task Streaming_oversize_JSON_without_content_length_is_rejected_before_store_access(string path)
    {
        var probe = new ProbeStore { ThrowOnAccess = true };
        using var factory = new DemoFactory(store: probe);
        using var client = factory.Client();
        using var response = await client.PostAsync(path, new UnknownLengthContent(OversizedJson(path), "application/json"));
        await WebHarness.Problem(response, 413, "PAYLOAD_TOO_LARGE", path);
        Assert.Equal(0, probe.Writes);
    }

    internal static string OversizedJson(string path)
    {
        var payload = path switch
        {
            "/api/v1/shipments" => "{\"trackingNumber\":\"TEST-001\"}",
            "/api/v1/shipments/TEST-001/events" => TestData.EventJson(),
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };
        return payload + new string(' ', 16385 - Encoding.UTF8.GetByteCount(payload));
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] bytes;
        public UnknownLengthContent(string text, string contentType)
        {
            bytes = Encoding.UTF8.GetBytes(text);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
}
