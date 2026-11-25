using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using TraceOps.Application;
using TraceOps.Web.Storage;
using Xunit;

namespace TraceOps.Soap.Tests;

public sealed class SoapFixtureSanityTests
{
    [Fact]
    public async Task TestServer_uses_isolated_in_memory_store_and_fixed_clock()
    {
        using var factory = SoapAuthorAFactory.Create(enabled: false, port: 5081, seed: false);
        using var client = factory.NewClient();
        using var response = await client.GetAsync("/shipments");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.Headers, header => header.Key == "X-Correlation-ID");

        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        var storeOne = first.ServiceProvider.GetRequiredService<IShipmentStore>();
        var storeTwo = second.ServiceProvider.GetRequiredService<IShipmentStore>();
        Assert.IsType<InMemoryShipmentStore>(storeOne);
        Assert.Same(storeOne, storeTwo);

        var clockOne = first.ServiceProvider.GetRequiredService<TimeProvider>();
        var clockTwo = second.ServiceProvider.GetRequiredService<TimeProvider>();
        Assert.Same(clockOne, clockTwo);
        Assert.Equal(SoapAuthorAHarness.FixedNow, clockOne.GetUtcNow());

        var appOne = first.ServiceProvider.GetRequiredService<ShipmentService>();
        var appTwo = second.ServiceProvider.GetRequiredService<ShipmentService>();
        Assert.NotSame(appOne, appTwo);
    }
    [Theory]
    [InlineData("RegisterShipment", false)]
    [InlineData("RegisterShipment", true)]
    [InlineData("GetShipment", false)]
    [InlineData("GetShipment", true)]
    [InlineData("AddShipmentEvent", false)]
    [InlineData("AddShipmentEvent", true)]
    public async Task Actual_success_helper_accepts_valid_contract_response(string operation, bool header)
    {
        using var response = SuccessResponse(operation, header);
        await SoapAuthorAHarness.ReadSuccessAsync(response, operation, "OK");
    }

    [Theory]
    [InlineData("foreign-root")]
    [InlineData("extra-body")]
    [InlineData("extra-result")]
    [InlineData("unknown-payload")]
    [InlineData("extra-operation-response")]
    [InlineData("header-after-body")]
    [InlineData("extra-header")]
    [InlineData("foreign-result")]
    [InlineData("foreign-field")]
    [InlineData("extra-field")]
    [InlineData("missing-field")]
    [InlineData("wrong-field-order")]
    [InlineData("nested-scalar")]
    [InlineData("mixed-text")]
    [InlineData("wrong-mime")]
    [InlineData("wrong-charset")]
    [InlineData("missing-charset")]
    public async Task Actual_success_helper_rejects_mutated_response(string mutation)
    {
        const string operation = "RegisterShipment";
        using var response = SuccessResponse(operation, false);
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var envelope = document.Root!;
        var body = envelope.Element(SoapAuthorAHarness.Soap12Ns + "Body")!;
        var payload = body.Elements().Single();
        var result = payload.Elements().Single();
        var code = result.Element(SoapAuthorAHarness.ServiceNs + "code")!;
        switch (mutation)
        {
            case "foreign-root": envelope.Name = XName.Get("Envelope", "urn:foreign"); break;
            case "extra-body": envelope.Add(new XElement(body)); break;
            case "extra-result": payload.Add(new XElement(result)); break;
            case "unknown-payload": body.Add(new XElement(SoapAuthorAHarness.ServiceNs + "unexpected")); break;
            case "extra-operation-response": body.Add(new XElement(payload)); break;
            case "header-after-body": envelope.Add(new XElement(SoapAuthorAHarness.Soap12Ns + "Header")); break;
            case "extra-header": envelope.AddFirst(new XElement(SoapAuthorAHarness.Soap12Ns + "Header"), new XElement(SoapAuthorAHarness.Soap12Ns + "Header")); break;
            case "foreign-result": result.Name = XName.Get(operation + "Result", "urn:foreign"); break;
            case "foreign-field": code.Name = XName.Get("code", "urn:foreign"); break;
            case "extra-field": result.Add(new XElement(SoapAuthorAHarness.ServiceNs + "unexpected")); break;
            case "missing-field": result.Element(SoapAuthorAHarness.ServiceNs + "shipment")!.Remove(); break;
            case "wrong-field-order": code.Remove(); result.Add(code); break;
            case "nested-scalar": code.Add(new XElement(SoapAuthorAHarness.ServiceNs + "unexpected")); break;
            case "mixed-text": body.Add("unexpected"); break;
            case "wrong-mime": case "wrong-charset": case "missing-charset": break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        response.Content.Dispose();
        response.Content = new StringContent(document.ToString(), Encoding.UTF8, "application/soap+xml");
        var contentType = response.Content.Headers.ContentType!;
        if (mutation == "wrong-mime") contentType.MediaType = "text/xml";
        if (mutation == "wrong-charset") contentType.CharSet = "utf-16";
        if (mutation == "missing-charset") contentType.CharSet = null;
        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() =>
            SoapAuthorAHarness.ReadSuccessAsync(response, operation, "OK"));
    }

    private static HttpResponseMessage SuccessResponse(string operation, bool header)
    {
        var eventFields = operation == "AddShipmentEvent"
            ? "<t:eventId>aaaaaaaa-0000-0000-0000-000000000001</t:eventId><t:duplicate>false</t:duplicate>" : "";
        var fields = "<t:code>OK</t:code>" + eventFields +
            "<t:shipment><t:trackingNumber>SOAP-CONTROL-001</t:trackingNumber><t:status>Registered</t:status>" +
            "<t:lastOccurredAt>2026-11-17T10:00:00Z</t:lastOccurredAt><t:version>0</t:version></t:shipment>";
        var xml = SoapAuthorAHarness.Envelope(
            $"<t:{operation}Response><t:{operation}Result>{fields}</t:{operation}Result></t:{operation}Response>",
            header ? "<env:Header />" : null);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/soap+xml"),
        };
    }
}
