using System.Net;
using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TraceOps.Application;
using TraceOps.Domain;
using TraceOps.Web.Middleware;
using Xunit;

namespace TraceOps.Web.Tests;

public sealed class HostingRegressionTests
{
    [Fact]
    [Trait("Acceptance", "M3-T13")]
    public async Task Stylesheet_link_from_actual_MVC_page_is_served_as_nonempty_CSS()
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        using var page = await client.GetAsync("/shipments");
        var document = await WebHarness.Html(page, 200);
        var stylesheets = document.QuerySelectorAll("link[rel~='stylesheet'][href]")
            .Select(link => new Uri(client.BaseAddress!, link.GetAttribute("href")!)).ToArray();
        var stylesheet = Assert.Single(stylesheets, uri => uri.AbsolutePath == "/css/site.css");
        Assert.Equal(client.BaseAddress!.GetLeftPart(UriPartial.Authority), stylesheet.GetLeftPart(UriPartial.Authority));
        using var response = await client.GetAsync(stylesheet.PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
        Assert.False(string.IsNullOrWhiteSpace(await response.Content.ReadAsStringAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "M3-T14")]
    public async Task Multipart_MVC_POST_with_valid_cookie_token_and_payload_is_415_without_mutation(bool eventRoute)
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        if (eventRoute) await WebHarness.Register(client);
        var page = eventRoute ? "/shipments/TEST-001" : "/shipments";
        var action = eventRoute ? page + "/events" : page;
        var token = await CookieToken(client, page, action);
        var values = eventRoute ? TestData.EventForm() : new Dictionary<string, string>
        {
            ["TrackingNumber"] = TestData.Tracking,
        };
        values["__RequestVerificationToken"] = token;
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IShipmentStore>();
        var before = store.List().ToArray();
        using var multipart = new MultipartFormDataContent();
        foreach (var (name, value) in values) multipart.Add(new StringContent(value), name);
        using var response = await client.PostAsync(action, multipart);
        var document = await WebHarness.Html(response, 415);
        Assert.Contains(WebHarness.Correlation(response), document.Body!.TextContent);
        WebHarness.SafeText(await response.Content.ReadAsStringAsync());
        AssertStoreUnchanged(before, store.List());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Acceptance", "M3-T15")]
    public async Task Store_fault_plus_typed_error_logger_fault_keeps_safe_500_response_format(bool api)
    {
        var store = new ProbeStore { ThrowOnAccess = true };
        var logger = new ThrowingErrorLogger();
        using var factory = new DemoFactory(store: store, exceptionLogger: logger);
        using var client = factory.Client();
        var path = api ? "/api/v1/shipments/TEST-001" : "/shipments";
        const string correlation = "ABCDEFAB-0000-0000-0000-000000000315";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Correlation-ID", correlation);
        request.Headers.Accept.ParseAdd(api ? "application/json" : "text/html");
        using var response = await client.SendAsync(request);
        Assert.True(store.Reads > 0, "The actual request must trigger the injected store fault.");
        Assert.True(logger.ErrorCalls > 0, "The exception handler must reach the injected Error logger fault.");
        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var correlationValues),
            "Even logger failure must preserve the safe response correlation header.");
        Assert.Equal(correlation, Assert.Single(correlationValues!));
        if (api)
        {
            await WebHarness.Problem(response, 500, "INTERNAL_ERROR", path);
        }
        else
        {
            var document = await WebHarness.Html(response, 500);
            Assert.Contains(correlation, document.Body!.TextContent);
        }
        var text = await response.Content.ReadAsStringAsync();
        WebHarness.SafeText(text);
        Assert.DoesNotContain(ThrowingErrorLogger.Secret, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(ThrowingErrorLogger), text, StringComparison.Ordinal);
    }

    private static async Task<string> CookieToken(HttpClient client, string page, string action)
    {
        using var response = await client.GetAsync(page);
        var document = await WebHarness.Html(response, 200);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.NotEmpty(cookies!);
        return WebHarness.Token(WebHarness.Form(document, action));
    }

    private static void AssertStoreUnchanged(IReadOnlyList<ShipmentState> before, IReadOnlyList<ShipmentState> after)
    {
        var expected = before.OrderBy(state => state.TrackingNumber, StringComparer.Ordinal).ToArray();
        var actual = after.OrderBy(state => state.TrackingNumber, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].TrackingNumber, actual[index].TrackingNumber);
            Assert.Equal(expected[index].Status, actual[index].Status);
            Assert.True(expected[index].LastOccurredAt.EqualsExact(actual[index].LastOccurredAt));
            Assert.Equal(expected[index].Version, actual[index].Version);
            Assert.Equal(expected[index].Events, actual[index].Events);
        }
    }

    private sealed class ThrowingErrorLogger : ILogger<ExceptionHandlingMiddleware>
    {
        public const string Secret = "SECRET-LOGGER-ERROR-V3";
        public int ErrorCalls;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                Interlocked.Increment(ref ErrorCalls);
                throw new InvalidOperationException(Secret);
            }
        }
    }
}
