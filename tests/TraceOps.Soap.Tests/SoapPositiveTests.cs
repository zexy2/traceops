using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security;
using System.ServiceModel;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TraceOps.Application;
using TraceOps.Domain;
using TraceOps.Web.Soap;
using Xunit;

namespace TraceOps.Soap.Tests;

public sealed class SoapPositiveTests
{
    [Fact]
    [Trait("Acceptance", "M5-T01")]
    public void Public_service_contract_and_XML_serializer_DTOs_match_frozen_surface()
    {
        const string soapNamespace = "urn:traceops:soap:v1";
        var contract = typeof(ITraceOpsSoapService).GetCustomAttribute<ServiceContractAttribute>();
        Assert.NotNull(contract);
        Assert.Equal("ITraceOpsSoapService", contract.Name);
        Assert.Equal(soapNamespace, contract.Namespace);

        var operations = new[]
        {
            ("RegisterShipment", typeof(SoapRegisterShipmentRequest), typeof(SoapShipmentResponse)),
            ("GetShipment", typeof(SoapGetShipmentRequest), typeof(SoapShipmentResponse)),
            ("AddShipmentEvent", typeof(SoapAddShipmentEventRequest), typeof(SoapEventResponse)),
        };
        foreach (var (name, requestType, responseType) in operations)
        {
            var method = typeof(ITraceOpsSoapService).GetMethod(name);
            Assert.NotNull(method);
            Assert.Equal(responseType, method!.ReturnType);
            var parameter = Assert.Single(method.GetParameters());
            Assert.Equal("request", parameter.Name);
            Assert.Equal(requestType, parameter.ParameterType);
            Assert.False(parameter.IsOut);
            var operation = method.GetCustomAttribute<OperationContractAttribute>();
            Assert.NotNull(operation);
            var action = $"{soapNamespace}/ITraceOpsSoapService/{name}";
            Assert.Equal(action, operation!.Action);
            Assert.Equal(action + "Response", operation.ReplyAction);
            var fault = Assert.Single(method.GetCustomAttributes<FaultContractAttribute>());
            Assert.Equal(typeof(SoapFaultDetail), fault.DetailType);
            Assert.Equal("TraceOpsFault", fault.Name);
            Assert.Equal(soapNamespace, fault.Namespace);
        }

        var dtoTypes = new (Type Type, (string Property, Type PropertyType, string WireName)[] Properties)[]
        {
            (typeof(SoapRegisterShipmentRequest), [("TrackingNumber", typeof(string), "trackingNumber")]),
            (typeof(SoapGetShipmentRequest), [("TrackingNumber", typeof(string), "trackingNumber")]),
            (typeof(SoapAddShipmentEventRequest),
            [
                ("TrackingNumber", typeof(string), "trackingNumber"),
                ("EventId", typeof(string), "eventId"),
                ("Status", typeof(string), "status"),
                ("OccurredAt", typeof(string), "occurredAt"),
                ("SourceSystem", typeof(string), "sourceSystem"),
            ]),
            (typeof(SoapShipmentResponse), [("Code", typeof(string), "code"), ("Shipment", typeof(SoapShipmentSummary), "shipment")]),
            (typeof(SoapEventResponse),
            [
                ("Code", typeof(string), "code"),
                ("EventId", typeof(string), "eventId"),
                ("Duplicate", typeof(bool), "duplicate"),
                ("Shipment", typeof(SoapShipmentSummary), "shipment"),
            ]),
            (typeof(SoapShipmentSummary),
            [
                ("TrackingNumber", typeof(string), "trackingNumber"),
                ("Status", typeof(string), "status"),
                ("LastOccurredAt", typeof(string), "lastOccurredAt"),
                ("Version", typeof(long), "version"),
            ]),
            (typeof(SoapFaultDetail), [("Code", typeof(string), "code"), ("CorrelationId", typeof(string), "correlationId")]),
        };

        foreach (var (type, properties) in dtoTypes)
        {
            Assert.True(type.IsPublic && type.IsSealed, $"{type.Name} must be public and sealed.");
            Assert.Equal(soapNamespace, type.GetCustomAttribute<XmlTypeAttribute>()?.Namespace);
            Assert.NotNull(type.GetConstructor(Type.EmptyTypes));
            var instance = Activator.CreateInstance(type)!;
            for (var order = 0; order < properties.Length; order++)
            {
                var expected = properties[order];
                var property = type.GetProperty(expected.Property, BindingFlags.Instance | BindingFlags.Public);
                Assert.NotNull(property);
                Assert.Equal(expected.PropertyType, property!.PropertyType);
                Assert.True(property.GetMethod?.IsPublic);
                Assert.True(property.SetMethod?.IsPublic);
                var element = property.GetCustomAttribute<XmlElementAttribute>();
                Assert.NotNull(element);
                Assert.Equal(expected.WireName, element!.ElementName);
                Assert.Equal(soapNamespace, element.Namespace);
                Assert.Equal(order, element.Order);
                if (expected.PropertyType == typeof(string))
                    Assert.Equal(string.Empty, property.GetValue(instance));
            }
        }

        Assert.NotNull(new SoapShipmentResponse().Shipment);
        Assert.NotNull(new SoapEventResponse().Shipment);
        var argumentException = Assert.Throws<ArgumentNullException>(() => new TraceOpsSoapService(null!));
        Assert.Equal("shipmentService", argumentException.ParamName);
    }

    [Fact]
    [Trait("Acceptance", "M5-T01")]
    public async Task SOAP_service_is_registered_scoped_with_the_web_application_DI()
    {
        using var factory = SoapAuthorAFactory.Create(enabled: true, port: 5081, seed: false);
        using var client = factory.NewClient();
        using var response = await client.GetAsync("/shipments");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scopeOne = factory.Services.CreateScope();
        using var scopeTwo = factory.Services.CreateScope();
        var registeredService = scopeOne.ServiceProvider.GetService<ITraceOpsSoapService>();
        Assert.NotNull(registeredService);
        Assert.IsType<TraceOpsSoapService>(registeredService);
        var serviceOne = scopeOne.ServiceProvider.GetRequiredService<ITraceOpsSoapService>();
        Assert.Same(serviceOne, scopeOne.ServiceProvider.GetRequiredService<ITraceOpsSoapService>());
        Assert.NotSame(serviceOne, scopeTwo.ServiceProvider.GetRequiredService<ITraceOpsSoapService>());
        Assert.IsType<TraceOpsSoapService>(serviceOne);
    }

    [Fact]
    [Trait("Acceptance", "M5-T01")]
    [Trait("Acceptance", "M5-T03")]
    public async Task Three_SOAP_operations_share_store_clock_and_projection_with_REST_and_MVC()
    {
        using var factory = SoapAuthorAFactory.Create(enabled: true, port: 5081, seed: false);
        using var client = factory.NewClient();

        using (var register = await SoapAuthorAHarness.PostAsync(
            client,
            "RegisterShipment",
            SoapAuthorAHarness.RegisterRequest("SOAP-001")))
        {
            var response = await SoapAuthorAHarness.ReadSuccessAsync(register, "RegisterShipment", "REGISTERED");
            Assert.Equal("SOAP-001", SoapAuthorAHarness.Result(response, "RegisterShipment").Descendants(SoapAuthorAHarness.ServiceNs + "trackingNumber").Single().Value);
        }

        using (var restRead = await client.GetAsync("/api/v1/shipments/SOAP-001"))
        {
            Assert.Equal(HttpStatusCode.OK, restRead.StatusCode);
            using var json = JsonDocument.Parse(await restRead.Content.ReadAsStringAsync());
            Assert.Equal("SOAP-001", json.RootElement.GetProperty("trackingNumber").GetString());
            Assert.Equal("Registered", json.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, json.RootElement.GetProperty("version").GetInt64());
            Assert.Equal(SoapAuthorAHarness.FixedNow, json.RootElement.GetProperty("lastOccurredAt").GetDateTimeOffset());
        }

        const string firstEvent = "bbbbbbbb-0000-0000-0000-000000000001";
        using (var restEvent = await client.PostAsync(
            "/api/v1/shipments/SOAP-001/events",
            new StringContent(
                "{\"eventId\":\"" + firstEvent + "\",\"status\":\"Accepted\",\"occurredAt\":\"2026-11-17T10:00:01Z\",\"sourceSystem\":\"rest.test\"}",
                Encoding.UTF8,
                "application/json")))
        {
            Assert.Equal(HttpStatusCode.Created, restEvent.StatusCode);
        }

        using (var get = await SoapAuthorAHarness.PostAsync(
            client,
            "GetShipment",
            SoapAuthorAHarness.GetRequest("SOAP-001")))
        {
            var response = await SoapAuthorAHarness.ReadSuccessAsync(get, "GetShipment", "FOUND");
            var shipment = SoapAuthorAHarness.Result(response, "GetShipment").Descendants(SoapAuthorAHarness.ServiceNs + "shipment").Single();
            Assert.Equal("Accepted", shipment.Element(SoapAuthorAHarness.ServiceNs + "status")!.Value);
            Assert.Equal("1", shipment.Element(SoapAuthorAHarness.ServiceNs + "version")!.Value);
            Assert.EndsWith("Z", shipment.Element(SoapAuthorAHarness.ServiceNs + "lastOccurredAt")!.Value);
        }

        using (var add = await SoapAuthorAHarness.PostAsync(
            client,
            "AddShipmentEvent",
            SoapAuthorAHarness.AddEventRequest("SOAP-001", "bbbbbbbb-0000-0000-0000-000000000002", "InTransit", "2026-11-17T10:00:02Z", "soap.test")))
        {
            var response = await SoapAuthorAHarness.ReadSuccessAsync(add, "AddShipmentEvent", "APPLIED");
            Assert.Equal("false", SoapAuthorAHarness.Result(response, "AddShipmentEvent").Descendants(SoapAuthorAHarness.ServiceNs + "duplicate").Single().Value);
        }

        using (var restRead = await client.GetAsync("/api/v1/shipments/SOAP-001"))
        {
            Assert.Equal(HttpStatusCode.OK, restRead.StatusCode);
            using var json = JsonDocument.Parse(await restRead.Content.ReadAsStringAsync());
            Assert.Equal("InTransit", json.RootElement.GetProperty("status").GetString());
            Assert.Equal(2, json.RootElement.GetProperty("version").GetInt64());
        }

        using (var mvcRead = await client.GetAsync("/shipments/SOAP-001"))
        {
            Assert.Equal(HttpStatusCode.OK, mvcRead.StatusCode);
            var html = await mvcRead.Content.ReadAsStringAsync();
            Assert.Contains("SOAP-001", html, StringComparison.Ordinal);
            Assert.Contains("InTransit", html, StringComparison.Ordinal);
        }

        using var scope = factory.Services.CreateScope();
        var details = scope.ServiceProvider.GetRequiredService<ShipmentService>().Find("SOAP-001");
        Assert.NotNull(details);
        Assert.Equal(2, details!.Events.Count);
        Assert.Equal(2, details.Shipment.Version);
    }
}

internal static class SoapAuthorAHarness
{
    public const string ServiceNamespace = "urn:traceops:soap:v1";
    public static readonly XNamespace ServiceNs = ServiceNamespace;
    public static readonly XNamespace Soap12Ns = "http://www.w3.org/2003/05/soap-envelope";
    public static readonly DateTimeOffset FixedNow = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    public static string Action(string operation) => $"{ServiceNamespace}/ITraceOpsSoapService/{operation}";

    public static string Envelope(string body, string? header = null) =>
        $"<env:Envelope xmlns:env=\"{Soap12Ns}\" xmlns:t=\"{ServiceNamespace}\">{header}<env:Body>{body}</env:Body></env:Envelope>";

    public static string Operation(string operation, string requestFields) =>
        $"<t:{operation} xmlns:t=\"{ServiceNamespace}\"><t:request>{requestFields}</t:request></t:{operation}>";

    public static string RegisterRequest(string tracking) =>
        Envelope(Operation("RegisterShipment", $"<t:trackingNumber>{Escape(tracking)}</t:trackingNumber>"));

    public static string GetRequest(string tracking) =>
        Envelope(Operation("GetShipment", $"<t:trackingNumber>{Escape(tracking)}</t:trackingNumber>"));

    public static string AddEventRequest(string tracking, string id, string status, string occurredAt, string source) =>
        Envelope(Operation("AddShipmentEvent",
            $"<t:trackingNumber>{Escape(tracking)}</t:trackingNumber>" +
            $"<t:eventId>{Escape(id)}</t:eventId>" +
            $"<t:status>{Escape(status)}</t:status>" +
            $"<t:occurredAt>{Escape(occurredAt)}</t:occurredAt>" +
            $"<t:sourceSystem>{Escape(source)}</t:sourceSystem>"));

    public static HttpContent Content(string xml, string operation)
    {
        var content = new StringContent(xml, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
        content.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("action", $"\"{Action(operation)}\""));
        return content;
    }

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string operation, string xml) =>
        client.PostAsync("/soap/shipments", Content(xml, operation));

    public static async Task<XDocument> ReadSuccessAsync(HttpResponseMessage response, string operation, string code)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/soap+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet, ignoreCase: true);
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = Result(document, operation);
        Assert.Equal(code, result.Element(ServiceNs + "code")!.Value);
        return document;
    }

    public static XElement Payload(XDocument document)
    {
        var envelope = document.Root;
        Assert.NotNull(envelope);
        Assert.Equal(Soap12Ns + "Envelope", envelope!.Name);
        var children = envelope.Elements().ToArray();
        Assert.True(children.Length is 1 or 2);
        AssertContainer(envelope);
        if (children.Length == 2)
            Assert.Equal(Soap12Ns + "Header", children[0].Name);
        var body = children[^1];
        Assert.Equal(Soap12Ns + "Body", body.Name);
        AssertContainer(body);
        return Assert.Single(body.Elements());
    }

    public static XElement Result(XDocument document, string operation)
    {
        var payload = Payload(document);
        Assert.Equal(ServiceNs + operation + "Response", payload.Name);
        AssertContainer(payload);
        var result = Assert.Single(payload.Elements());
        Assert.Equal(ServiceNs + operation + "Result", result.Name);
        var fields = operation switch
        {
            "RegisterShipment" or "GetShipment" => new[] { "code", "shipment" },
            "AddShipmentEvent" => new[] { "code", "eventId", "duplicate", "shipment" },
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        AssertFields(result, fields);
        foreach (var field in result.Elements().Where(field => field.Name != ServiceNs + "shipment"))
            AssertScalar(field);
        if (operation == "AddShipmentEvent")
            XmlConvert.ToBoolean(result.Element(ServiceNs + "duplicate")!.Value);
        var shipment = result.Element(ServiceNs + "shipment")!;
        AssertFields(shipment, ["trackingNumber", "status", "lastOccurredAt", "version"]);
        foreach (var field in shipment.Elements())
            AssertScalar(field);
        XmlConvert.ToInt64(shipment.Element(ServiceNs + "version")!.Value);
        return result;
    }

    private static void AssertFields(XElement element, string[] fields)
    {
        AssertContainer(element);
        Assert.Equal(fields.Select(field => ServiceNs + field), element.Elements().Select(field => field.Name));
    }

    private static void AssertContainer(XElement element)
    {
        Assert.DoesNotContain(element.Attributes(), attribute => !attribute.IsNamespaceDeclaration);
        Assert.All(element.Nodes().OfType<XText>(), text => Assert.True(string.IsNullOrWhiteSpace(text.Value)));
    }

    private static void AssertScalar(XElement element)
    {
        Assert.Empty(element.Elements());
        Assert.DoesNotContain(element.Attributes(), attribute => !attribute.IsNamespaceDeclaration);
    }

    public static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
}

internal sealed class SoapAuthorAFactory : WebApplicationFactory<global::Program>
{
    private readonly Dictionary<string, string?> _settings;

    private SoapAuthorAFactory(bool? enabled, int? port, bool? seed)
    {
        _settings = new Dictionary<string, string?>
        {
            ["TraceOps:Soap:Enabled"] = enabled?.ToString(),
            ["TraceOps:Http:Port"] = port?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["TraceOps:SeedDemoData"] = seed?.ToString(),
        };
    }

    public static SoapAuthorAFactory Create(bool? enabled = null, int? port = null, bool? seed = false) => new(enabled, port, seed);

    public void WithRawSetting(string key, string? value) => _settings[key] = value;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_settings));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new SoapAuthorATimeProvider());
        });
    }

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri("http://localhost"),
    });
}

internal sealed class SoapAuthorATimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => SoapAuthorAHarness.FixedNow;
}
