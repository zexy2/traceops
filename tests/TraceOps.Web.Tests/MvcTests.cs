using System.Globalization;
using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Microsoft.Extensions.DependencyInjection;
using TraceOps.Application;
using TraceOps.Domain;
using TraceOps.Web.Storage;
using Xunit;

namespace TraceOps.Web.Tests;

public sealed class MvcTests
{
    [Fact]
    [Trait("Acceptance", "M3-T07")]
    public async Task Root_redirects_to_real_empty_HTML_list_with_register_form_and_banner()
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        using var root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Found, root.StatusCode);
        Assert.Equal("/shipments", root.Headers.Location?.OriginalString);
        WebHarness.Correlation(root);
        using var list = await client.GetAsync("/shipments");
        var document = await WebHarness.Html(list, 200);
        Assert.Contains("Shipments", document.Body!.TextContent);
        Assert.Contains("No shipments yet.", document.Body.TextContent);
        Assert.Contains("Register shipment", document.Body.TextContent);
        var form = WebHarness.Form(document, "/shipments");
        Assert.Equal("", Input(form, "TrackingNumber").Value);
        Assert.Equal(new[] { "TrackingNumber", "__RequestVerificationToken" }.Order(StringComparer.Ordinal).ToArray(), FieldNames(form));
        WebHarness.Token(form);
        Assert.True(list.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.NotEmpty(cookies!);
    }

    [Fact]
    [Trait("Acceptance", "M3-T07")]
    public async Task Actual_antiforgery_cookie_and_form_token_support_register_event_and_PRG_without_JS()
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        var (_, token) = await WebHarness.GetForm(client, "/shipments", "/shipments");
        using var register = await client.PostAsync("/shipments?returnUrl=https://evil.invalid", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["TrackingNumber"] = TestData.Tracking,
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.SeeOther, register.StatusCode);
        Assert.Equal("/shipments/TEST-001", register.Headers.Location?.OriginalString);
        const string detailPath = "/shipments/TEST-001";
        const string eventPath = detailPath + "/events";
        var (detail, eventToken) = await WebHarness.GetForm(client, detailPath, eventPath);
        Assert.Contains(TestData.Tracking, detail.Body!.TextContent);
        var form = WebHarness.Form(detail, eventPath);
        Assert.Equal(new[] { "EventId", "OccurredAt", "SourceSystem", "Status", "__RequestVerificationToken" }.Order(StringComparer.Ordinal).ToArray(), FieldNames(form));
        foreach (var name in new[] { "EventId", "OccurredAt" })
        {
            var editable = Input(form, name);
            Assert.NotEqual("hidden", editable.Type);
            Assert.False(editable.HasAttribute("readonly"));
            Assert.False(editable.HasAttribute("disabled"));
            Assert.Null(editable.Closest("fieldset[disabled]"));
        }
        Assert.True(Guid.TryParseExact(Input(form, "EventId").Value, "D", out var generatedId));
        Assert.NotEqual(Guid.Empty, generatedId);
        var time = Input(form, "OccurredAt").Value;
        Assert.EndsWith("Z", time);
        Assert.Equal(TestData.Now, DateTimeOffset.Parse(time, CultureInfo.InvariantCulture));
        Assert.Equal("demo-ui", Input(form, "SourceSystem").Value);
        var fields = TestData.EventForm();
        fields["__RequestVerificationToken"] = eventToken;
        using var added = await client.PostAsync(eventPath, new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.SeeOther, added.StatusCode);
        Assert.Equal(detailPath, added.Headers.Location?.OriginalString);
        using var finalResponse = await client.GetAsync(detailPath);
        var final = await WebHarness.Html(finalResponse, 200);
        Assert.Contains("Accepted", final.Body!.TextContent);
        Assert.Contains(TestData.EventId.ToString("D"), final.Body.TextContent, StringComparison.OrdinalIgnoreCase);
        var summary = await WebHarness.Summary(client);
        Assert.Equal("Accepted", summary.GetProperty("status").GetString());
        Assert.Equal(1L, summary.GetProperty("version").GetInt64());
        var (_, replayToken) = await WebHarness.GetForm(client, detailPath, eventPath);
        fields["__RequestVerificationToken"] = replayToken;
        using var replay = await client.PostAsync(eventPath, new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.SeeOther, replay.StatusCode);
        WebHarness.SameSummary(summary, await WebHarness.Summary(client));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong")]
    [InlineData("token-without-cookie")]
    [Trait("Acceptance", "M3-T08")]
    public async Task Unsafe_MVC_POST_requires_real_cookie_token_pair_and_calls_no_store_mutation(string kind)
    {
        var probe = new ProbeStore();
        using var factory = new DemoFactory(store: probe);
        using var cookieClient = factory.Client();
        var (_, validToken) = await WebHarness.GetForm(cookieClient, "/shipments", "/shipments");
        using var noCookieClient = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, HandleCookies = false,
        });
        var fields = new Dictionary<string, string> { ["TrackingNumber"] = TestData.Tracking };
        if (kind != "missing") fields["__RequestVerificationToken"] = kind == "wrong" ? "invalid-token" : validToken;
        var client = kind == "token-without-cookie" ? noCookieClient : cookieClient;
        foreach (var path in new[] { "/shipments", "/shipments/TEST-001/events" })
        {
            using var response = await client.PostAsync(path, new FormUrlEncodedContent(fields));
            var html = await WebHarness.Html(response, 400);
            Assert.Contains("Invalid form submission", html.Body!.TextContent);
            Assert.Equal(0, probe.Writes);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("lower")]
    [InlineData("TEST-001")]
    [Trait("Acceptance", "M3-T08")]
    public async Task Register_validation_or_conflict_renders_errors_and_retains_input_without_mutation(string? tracking)
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        await WebHarness.Register(client);
        var before = await WebHarness.Summary(client);
        var (_, token) = await WebHarness.GetForm(client, "/shipments", "/shipments");
        var values = new Dictionary<string, string> { ["__RequestVerificationToken"] = token };
        if (tracking is not null) values["TrackingNumber"] = tracking;
        using var response = await client.PostAsync("/shipments", new FormUrlEncodedContent(values));
        var document = await WebHarness.Html(response, 200);
        Assert.Equal(tracking ?? "", Input(WebHarness.Form(document, "/shipments"), "TrackingNumber").Value);
        AssertValidation(document);
        WebHarness.SameSummary(before, await WebHarness.Summary(client));
        using var scope = factory.Services.CreateScope();
        Assert.Single(scope.ServiceProvider.GetRequiredService<ShipmentService>().List());
    }

    public static IEnumerable<object[]> BadEventForms
    {
        get
        {
            foreach (var (key, value) in new[]
            {
                ("EventId", "not-guid"), ("EventId", ""),
                ("OccurredAt", "not-date"), ("OccurredAt", "2026-11-17T10:00:00"), ("OccurredAt", ""),
                ("Status", "Unknown"), ("Status", "Delivered"), ("Status", ""),
                ("SourceSystem", "bad source"), ("SourceSystem", ""),
            }) yield return [key, value, false];
            foreach (var key in new[] { "EventId", "OccurredAt", "Status", "SourceSystem" })
                yield return [key, "", true];
        }
    }

    internal static Dictionary<string, string> InvalidFormFields(string key, string value, bool missing)
    {
        var fields = TestData.EventForm();
        if (missing) fields.Remove(key); else fields[key] = value;
        return fields;
    }

    [Theory]
    [MemberData(nameof(BadEventForms))]
    [Trait("Acceptance", "M3-T08")]
    public async Task Event_validation_retains_raw_values_and_never_mutates_current_state(string key, string value, bool missing)
    {
        var fields = InvalidFormFields(key, value, missing);
        using var factory = new DemoFactory();
        using var client = factory.Client();
        await WebHarness.Register(client);
        var before = await WebHarness.Summary(client);
        const string path = "/shipments/TEST-001/events";
        var (_, token) = await WebHarness.GetForm(client, "/shipments/TEST-001", path);
        fields["__RequestVerificationToken"] = token;
        using var response = await client.PostAsync(path, new FormUrlEncodedContent(fields));
        var document = await WebHarness.Html(response, 200);
        var form = WebHarness.Form(document, path);
        AssertValidation(document);
        foreach (var name in new[] { "EventId", "OccurredAt", "SourceSystem" })
            Assert.Equal(fields.GetValueOrDefault(name, ""), Input(form, name).Value);
        var select = Assert.IsAssignableFrom<IHtmlSelectElement>(form.QuerySelector("select[name='Status']"));
        Assert.Equal(Enum.GetNames<ShipmentStatus>().Order(), select.Options.Select(option => option.Value).Order());
        if (Enum.GetNames<ShipmentStatus>().Contains(fields.GetValueOrDefault("Status", "")))
            Assert.Equal(fields["Status"], select.Value);
        else if (value.Length > 0 && key == "Status")
            Assert.Contains(value, document.Body!.TextContent);
        else if (key == "Status")
            Assert.DoesNotContain(select.Options, option => option.HasAttribute("selected"));
        WebHarness.SameSummary(before, await WebHarness.Summary(client));
    }

    [Theory]
    [InlineData("TrackingNumber")]
    [InlineData("SourceSystem")]
    [InlineData("Status")]
    [InlineData("EventId")]
    [InlineData("OccurredAt")]
    [Trait("Acceptance", "M3-T09")]
    public async Task Rejected_XSS_input_is_retained_encoded_and_creates_no_executable_markup(string field)
    {
        const string attack = "<script id=qa-injected>alert('unsafe')</script>";
        using var factory = new DemoFactory();
        using var client = factory.Client();
        var registration = field == "TrackingNumber";
        if (!registration) await WebHarness.Register(client);
        var page = registration ? "/shipments" : "/shipments/TEST-001";
        var path = registration ? page : page + "/events";
        var (_, token) = await WebHarness.GetForm(client, page, path);
        var fields = registration ? new Dictionary<string, string>() : TestData.EventForm();
        fields[field] = attack;
        fields["__RequestVerificationToken"] = token;
        using var response = await client.PostAsync(path, new FormUrlEncodedContent(fields));
        var document = await WebHarness.Html(response, 200);
        Assert.Null(document.QuerySelector("#qa-injected"));
        Assert.DoesNotContain(document.QuerySelectorAll("script"), script => script.TextContent.Contains("unsafe"));
        Assert.DoesNotContain(document.All.SelectMany(element => element.Attributes), attribute =>
            attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase) && attribute.Value.Contains("unsafe"));
        if (field == "Status")
        {
            Assert.Contains(attack, document.Body!.TextContent);
            Assert.DoesNotContain(document.QuerySelectorAll("option"), option => option.GetAttribute("value") == attack);
        }
        else Assert.Equal(attack, Input(WebHarness.Form(document, path), field).Value);
        AssertValidation(document);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ShipmentService>();
        Assert.Equal(registration ? 0 : 1, service.List().Count);
        if (!registration)
        {
            Assert.Equal(0L, service.Find(TestData.Tracking)!.Shipment.Version);
            Assert.Empty(service.Find(TestData.Tracking)!.Events);
        }
    }

    [Fact]
    [Trait("Acceptance", "M3-T08")]
    public async Task MVC_same_event_id_conflict_renders_validation_and_keeps_recorded_history()
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        await WebHarness.Register(client);
        using var applied = await WebHarness.JsonPost(client, "/api/v1/shipments/TEST-001/events", TestData.EventJson());
        Assert.Equal(HttpStatusCode.Created, applied.StatusCode);
        var before = await WebHarness.Summary(client);
        const string path = "/shipments/TEST-001/events";
        var (_, token) = await WebHarness.GetForm(client, "/shipments/TEST-001", path);
        var fields = TestData.EventForm(source: "other.api");
        fields["__RequestVerificationToken"] = token;
        using var response = await client.PostAsync(path, new FormUrlEncodedContent(fields));
        var document = await WebHarness.Html(response, 200);
        AssertValidation(document);
        Assert.Equal("other.api", Input(WebHarness.Form(document, path), "SourceSystem").Value);
        WebHarness.SameSummary(before, await WebHarness.Summary(client));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(TestData.Event(), Assert.Single(scope.ServiceProvider.GetRequiredService<ShipmentService>().Find(TestData.Tracking)!.Events));
    }

    [Fact]
    [Trait("Acceptance", "M3-T08")]
    public async Task MVC_missing_shipment_with_valid_token_is_404_HTML()
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        var (_, token) = await WebHarness.GetForm(client, "/shipments", "/shipments");
        var fields = TestData.EventForm();
        fields["__RequestVerificationToken"] = token;
        using var response = await client.PostAsync("/shipments/ABSENT/events", new FormUrlEncodedContent(fields));
        var document = await WebHarness.Html(response, 404);
        Assert.Contains("Shipment not found", document.Body!.TextContent);
    }

    [Theory]
    [InlineData(ShipmentStatus.Delivered)]
    [InlineData(ShipmentStatus.Cancelled)]
    [Trait("Acceptance", "M3-T08")]
    public async Task Forged_terminal_MVC_event_cannot_bypass_frozen_transition_validation(ShipmentStatus terminal)
    {
        var state = SeedAndViewTests.StateAt(terminal);
        var store = new InMemoryShipmentStore();
        Assert.True(store.TryAdd(state));
        using var factory = new DemoFactory(store: store);
        using var client = factory.Client();
        var (_, token) = await WebHarness.GetForm(client, "/shipments", "/shipments");
        var fields = TestData.EventForm(status: "OutForDelivery", id: Guid.NewGuid().ToString("D"));
        fields["__RequestVerificationToken"] = token;
        using var response = await client.PostAsync("/shipments/TEST-001/events", new FormUrlEncodedContent(fields));
        var document = await WebHarness.Html(response, 200);
        AssertValidation(document);
        Assert.Equal(terminal, store.Find(TestData.Tracking)!.Status);
        Assert.Equal(state.Version, store.Find(TestData.Tracking)!.Version);
        Assert.Equal(state.Events, store.Find(TestData.Tracking)!.Events);
    }

    [Theory]
    [InlineData("/shipments/ABSENT", 404)]
    [InlineData("/shipments/lower", 404)]
    [InlineData("/shipments", 500)]
    [Trait("Acceptance", "M3-T09")]
    public async Task HTML_errors_are_safe_with_banner_and_correlation_even_in_Development(string path, int status)
    {
        var probe = new ProbeStore { ThrowOnAccess = status == 500 };
        using var factory = new DemoFactory(store: probe);
        using var client = factory.Client();
        using var response = await client.GetAsync(path);
        var document = await WebHarness.Html(response, status);
        WebHarness.SafeText(await response.Content.ReadAsStringAsync());
        Assert.Contains(WebHarness.Correlation(response), document.Body!.TextContent);
        if (status == 404) Assert.Contains("Shipment not found", document.Body.TextContent);
        else Assert.True(probe.Reads > 0, "500 must come from injected failure, not another setup error.");
    }

    [Fact]
    [Trait("Acceptance", "M3-T06")]
    public async Task MVC_invalid_correlation_is_safe_400_before_mutation()
    {
        var probe = new ProbeStore();
        using var factory = new DemoFactory(store: probe);
        using var client = factory.Client();
        var (_, token) = await WebHarness.GetForm(client, "/shipments", "/shipments");
        probe.Reads = 0;
        probe.ThrowOnAccess = true;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/shipments");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", TestData.Secret);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["TrackingNumber"] = TestData.Tracking,
            ["__RequestVerificationToken"] = token,
        });
        using var response = await client.SendAsync(request);
        var document = await WebHarness.Html(response, 400);
        WebHarness.SafeText(await response.Content.ReadAsStringAsync());
        Assert.Contains(WebHarness.Correlation(response), document.Body!.TextContent);
        Assert.Equal(0, probe.Writes);
        Assert.Equal(0, probe.Reads);
    }

    [Fact]
    [Trait("Acceptance", "M3-T08")]
    public async Task MVC_timezone_less_date_is_parse_validation_before_store_event_application()
    {
        var probe = new ProbeStore
        {
            FindResult = ShipmentState.Register(TestData.Tracking, TestData.Now),
            ThrowOnWrite = true,
        };
        using var factory = new DemoFactory(store: probe);
        using var client = factory.Client();
        const string path = "/shipments/TEST-001/events";
        var (_, token) = await WebHarness.GetForm(client, "/shipments/TEST-001", path);
        var fields = TestData.EventForm(time: "2026-11-17T10:00:00");
        fields["__RequestVerificationToken"] = token;
        using var response = await client.PostAsync(path, new FormUrlEncodedContent(fields));
        var document = await WebHarness.Html(response, 200);
        AssertValidation(document);
        Assert.Equal("2026-11-17T10:00:00", Input(WebHarness.Form(document, path), "OccurredAt").Value);
        Assert.Equal(0, probe.Writes);
        Assert.Equal(0L, probe.FindResult.Version);
        Assert.Empty(probe.FindResult.Events);
    }

    [Theory]
    [InlineData("/shipments")]
    [InlineData("/shipments/TEST-001/events")]
    [Trait("Acceptance", "M3-T08")]
    public async Task Oversized_MVC_form_is_413_safe_HTML_without_mutation(string path)
    {
        var probe = new ProbeStore();
        using var factory = new DemoFactory(store: probe);
        using var client = factory.Client();
        var (_, token) = await WebHarness.GetForm(client, "/shipments", "/shipments");
        using var response = await client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["TrackingNumber"] = new string('A', 16385),
            ["__RequestVerificationToken"] = token,
        }));
        var document = await WebHarness.Html(response, 413);
        Assert.Contains(WebHarness.Correlation(response), document.Body!.TextContent);
        Assert.Equal(0, probe.Writes);
    }

    internal static IHtmlInputElement Input(IHtmlFormElement form, string name) =>
        Assert.IsAssignableFrom<IHtmlInputElement>(form.QuerySelector($"input[name='{name}']"));

    private static string[] FieldNames(IHtmlFormElement form) =>
        form.QuerySelectorAll("input[name],select[name],textarea[name]")
            .Where(element => element.GetAttribute("type") is not ("submit" or "button"))
            .Select(element => element.GetAttribute("name")!).Order(StringComparer.Ordinal).ToArray();

    private static void AssertValidation(IHtmlDocument document)
    {
        var text = document.Body!.TextContent;
        Assert.True(new[] { "invalid", "required", "already", "cannot", "must", "not allowed", "conflict" }
            .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)), "Expected readable English validation feedback.");
    }
}
