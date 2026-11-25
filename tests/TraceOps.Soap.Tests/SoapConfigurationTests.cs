using System.Net;
using System.Xml.Linq;
using Xunit;

namespace TraceOps.Soap.Tests;

public sealed class SoapConfigurationTests
{
    [Fact]
    [Trait("Acceptance", "M5-T08")]
    public async Task SOAP_defaults_off_and_legacy_MVC_banner_stays_unchanged()
    {
        using var factory = SoapAuthorAFactory.Create(enabled: null, port: null, seed: false);
        using var client = factory.NewClient();
        using var endpoint = await client.GetAsync("/soap/shipments");
        using var wsdl = await client.GetAsync("/soap/shipments?wsdl");
        Assert.Equal(HttpStatusCode.NotFound, endpoint.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wsdl.StatusCode);

        using var page = await client.GetAsync("/shipments");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Synthetic demo — volatile in-memory storage. Restarting loses changes. SQL Server and SOAP are not connected.", html, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Acceptance", "M5-T08")]
    public async Task SOAP_enabled_in_memory_mode_has_truthful_banner_and_keeps_demo_seed()
    {
        using var factory = SoapAuthorAFactory.Create(enabled: true, port: 5081, seed: null);
        using var client = factory.NewClient();
        using var page = await client.GetAsync("/shipments");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Synthetic demo — volatile in-memory storage. Restarting loses changes. SOAP 1.2 enabled; SQL Server is not connected.", html, StringComparison.Ordinal);

        using var seeded = await client.GetAsync("/shipments/DEMO-001");
        Assert.Equal(HttpStatusCode.OK, seeded.StatusCode);
        Assert.Contains("DEMO-001", await seeded.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var wsdl = await client.GetAsync("/soap/shipments?wsdl");
        Assert.Equal(HttpStatusCode.OK, wsdl.StatusCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5081)]
    [InlineData(65535)]
    [Trait("Acceptance", "M5-T08")]
    public async Task Valid_port_configuration_is_reflected_in_WSDL_loopback_address(int port)
    {
        using var factory = SoapAuthorAFactory.Create(enabled: true, port: port, seed: false);
        using var client = factory.NewClient();
        using var response = await client.GetAsync("/soap/shipments?wsdl");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var wsdl = XDocument.Parse(await response.Content.ReadAsStringAsync());
        XNamespace wsdlNs = "http://schemas.xmlsoap.org/wsdl/";
        XNamespace soap12 = "http://schemas.xmlsoap.org/wsdl/soap12/";
        var address = wsdl.Descendants(wsdlNs + "port").Single().Element(soap12 + "address");
        Assert.Equal($"http://127.0.0.1:{port}/soap/shipments", (string?)address?.Attribute("location"));
    }

    [Fact]
    [Trait("Acceptance", "M5-T08")]
    public void Invalid_SOAP_enabled_value_fails_host_startup()
    {
        using var factory = SoapAuthorAFactory.Create(enabled: null, port: 5081, seed: false);
        factory.WithRawSetting("TraceOps:Soap:Enabled", "perhaps");
        var exception = Record.Exception(() =>
        {
            using var client = factory.NewClient();
        });
        Assert.NotNull(exception);
        Assert.Contains("TraceOps:Soap:Enabled", exception!.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("65536")]
    [Trait("Acceptance", "M5-T08")]
    public void Empty_malformed_or_out_of_range_port_fails_host_startup(string port)
    {
        using var factory = SoapAuthorAFactory.Create(enabled: true, port: null, seed: false);
        factory.WithRawSetting("TraceOps:Http:Port", port);
        var exception = Record.Exception(() =>
        {
            using var client = factory.NewClient();
        });
        Assert.NotNull(exception);
        Assert.Contains("TraceOps:Http:Port", exception!.ToString(), StringComparison.Ordinal);
    }
}
