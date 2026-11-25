using System.Net;
using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TraceOps.Application;
using TraceOps.Domain;
using TraceOps.Web.Middleware;
using Xunit;

namespace TraceOps.Web.Tests;

internal sealed class DemoFactory(
    bool? seed = false,
    IShipmentStore? store = null,
    ILogger<ExceptionHandlingMiddleware>? exceptionLogger = null)
    : WebApplicationFactory<global::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        if (seed.HasValue) builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TraceOps:SeedDemoData"] = seed.Value.ToString(),
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider());
            if (store is not null)
            {
                services.RemoveAll<IShipmentStore>();
                services.AddSingleton(store);
            }
            if (exceptionLogger is not null)
            {
                services.RemoveAll<ILogger<ExceptionHandlingMiddleware>>();
                services.AddSingleton(exceptionLogger);
            }
        });
    }

    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = new Uri("http://localhost"),
    });
}

internal static class WebHarness
{
    public const string Api = "/api/v1/shipments";
    public static Task<HttpResponseMessage> JsonPost(HttpClient client, string path, string body) =>
        client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));

    public static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    public static async Task Register(HttpClient client, string tracking = TestData.Tracking)
    {
        using var response = await JsonPost(client, Api, JsonSerializer.Serialize(new { trackingNumber = tracking }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    public static async Task<JsonElement> Summary(HttpClient client, string tracking = TestData.Tracking)
    {
        using var response = await client.GetAsync($"{Api}/{tracking}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json(response);
    }

    public static void SameSummary(JsonElement before, JsonElement after)
    {
        foreach (var property in new[] { "trackingNumber", "status", "lastOccurredAt", "version" })
            Assert.Equal(before.GetProperty(property).ToString(), after.GetProperty(property).ToString());
    }

    public static async Task<JsonElement> Problem(HttpResponseMessage response, int status, string code, string path)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await Json(response);
        Assert.Equal("about:blank", json.GetProperty("type").GetString());
        Assert.Equal(status, json.GetProperty("status").GetInt32());
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.Equal(path.Split('?')[0], json.GetProperty("instance").GetString());
        Assert.Equal(Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status), json.GetProperty("title").GetString());
        var correlation = Correlation(response);
        Assert.Equal(correlation, json.GetProperty("correlationId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("detail").GetString()));
        SafeText(await response.Content.ReadAsStringAsync());
        return json;
    }

    public static string Correlation(HttpResponseMessage response)
    {
        var id = Assert.Single(response.Headers.GetValues("X-Correlation-ID"));
        Assert.True(Guid.TryParseExact(id, "D", out _), $"Not a UUID D: {id}");
        return id;
    }

    public static async Task<IHtmlDocument> Html(HttpResponseMessage response, int status)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());
        Assert.Contains(TestData.Banner, document.Body!.TextContent);
        Correlation(response);
        return document;
    }

    public static IHtmlFormElement Form(IHtmlDocument document, string action)
    {
        var form = Assert.Single(document.Forms, item => item.GetAttribute("action") == action);
        Assert.Equal("post", form.Method.ToLowerInvariant());
        return form;
    }

    public static string Token(IHtmlFormElement form)
    {
        var token = Assert.IsAssignableFrom<IHtmlInputElement>(
            form.QuerySelector("input[name='__RequestVerificationToken']"));
        Assert.False(string.IsNullOrWhiteSpace(token.Value));
        return token.Value;
    }

    public static async Task<(IHtmlDocument Document, string Token)> GetForm(HttpClient client, string page, string action)
    {
        using var response = await client.GetAsync(page);
        var document = await Html(response, 200);
        return (document, Token(Form(document, action)));
    }

    public static void SafeText(string text)
    {
        foreach (var forbidden in new[]
        {
            TestData.Secret, "InvalidOperationException", "System.Exception", "StackTrace",
            "DeveloperExceptionPage", "SqlException", "password=",
        }) Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
    }
}

// Observation/fault injection only. Success-path integration uses the real demo store.
internal sealed class ProbeStore : IShipmentStore
{
    public int Writes;
    public int Reads;
    public bool ThrowOnAccess;
    public bool ThrowOnWrite;
    public ShipmentState? FindResult;
    public IReadOnlyList<ShipmentState> List() { Access(false); return FindResult is null ? [] : [FindResult]; }
    public ShipmentState? Find(string trackingNumber) { Access(false); return FindResult; }
    public bool TryAdd(ShipmentState state) { Access(true); return false; }
    public ApplyEventResult? ApplyAtomically(LifecycleEvent input) { Access(true); return null; }
    private void Access(bool write)
    {
        if (write) Interlocked.Increment(ref Writes); else Interlocked.Increment(ref Reads);
        if (ThrowOnAccess || (write && ThrowOnWrite)) throw new InvalidOperationException(TestData.Secret);
    }
}
