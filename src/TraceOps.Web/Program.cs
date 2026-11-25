using System.Net;
using System.Globalization;
using System.ServiceModel.Channels;
using System.Text;
using System.Xml;
using TraceOps.Application;
using TraceOps.Web;
using TraceOps.Web.Controllers;
using TraceOps.Web.Middleware;
using TraceOps.Web.Runtime;
using TraceOps.Web.Soap;
using TraceOps.Web.Storage;
using SoapCore;
using SoapCore.Extensibility;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
    options.Listen(IPAddress.Loopback, ParseHttpPort(builder.Configuration["TraceOps:Http:Port"])));

var storeMode = builder.Configuration["TraceOps:Store"];
var sqlServerMode = string.Equals(storeMode, "SqlServer", StringComparison.OrdinalIgnoreCase);
if (storeMode is not null
    && !string.Equals(storeMode, "InMemory", StringComparison.OrdinalIgnoreCase)
    && !sqlServerMode)
{
    throw new InvalidOperationException(
        "TraceOps:Store selects a storage mode that is not enabled by the completed integration gates.");
}

IShipmentStore shipmentStore = sqlServerMode
    ? SqlServerStartupValidator.CreateValidatedStore(builder.Configuration)
    : new InMemoryShipmentStore();

builder.Services.AddControllersWithViews();
builder.Services.AddAntiforgery();
builder.Services.AddSingleton<IShipmentStore>(shipmentStore);
builder.Services.AddScoped<ShipmentService>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<HtmlAntiforgeryFilter>();
builder.Services.AddSingleton<RuntimeBanner>(services => new RuntimeBanner(
    ParseSoapEnabled(services.GetRequiredService<IConfiguration>()["TraceOps:Soap:Enabled"]),
    sqlServerMode));
builder.Services.AddSoapCore();
builder.Services.AddScoped<TraceOpsSoapService>();
builder.Services.AddScoped<ITraceOpsSoapService>(
    services => services.GetRequiredService<TraceOpsSoapService>());
builder.Services.AddSingleton<IFaultExceptionTransformer, TraceOpsFaultExceptionTransformer>();

var app = builder.Build();

var soapEnabled = ParseSoapEnabled(app.Configuration["TraceOps:Soap:Enabled"]);
var port = ParseHttpPort(app.Configuration["TraceOps:Http:Port"]);

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SoapRequestGuardMiddleware>(soapEnabled, port);
app.UseMiddleware<RequestBodyLimitMiddleware>();
app.UseMiddleware<ApiContentTypeMiddleware>();

if (soapEnabled)
{
    var quotas = new XmlDictionaryReaderQuotas
    {
        MaxDepth = 32,
        MaxStringContentLength = 16_384,
        MaxArrayLength = 16_384,
        MaxBytesPerRead = 4_096,
        MaxNameTableCharCount = 16_384,
    };
    var encoder = new SoapEncoderOptions
    {
        MessageVersion = MessageVersion.Soap12,
        WriteEncoding = Encoding.UTF8,
        ReaderQuotas = quotas,
        BindingName = "TraceOpsShipmentSoap12Binding",
        PortName = "TraceOpsShipmentSoap12Port",
    };
    Microsoft.AspNetCore.Builder.IApplicationBuilder soapPipeline = app;
    soapPipeline.UseSoapEndpoint<TraceOpsSoapService>(
        SoapRequestGuardMiddleware.EndpointPath,
        encoder,
        SoapSerializer.XmlSerializer,
        caseInsensitivePath: false,
        indentXml: true,
        omitXmlDeclaration: false,
        schemeOverride: "http");
}

app.UseMiddleware<ErrorPageMiddleware>();
app.UseStaticFiles();
app.UseRouting();
app.MapControllers();

if (!sqlServerMode && (app.Configuration.GetValue<bool?>("TraceOps:SeedDemoData") ?? true))
{
    DemoSeed.Load(app.Services);
}

app.Run();

static bool ParseSoapEnabled(string? value)
{
    if (value is null)
    {
        return false;
    }

    if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    throw new InvalidOperationException(
        "TraceOps:Soap:Enabled must be exactly true or false when configured.");
}

static int ParseHttpPort(string? value)
{
    if (value is null)
    {
        return 5080;
    }

    if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
        && port is >= 1 and <= 65_535)
    {
        return port;
    }

    throw new InvalidOperationException(
        "TraceOps:Http:Port must be an integer from 1 through 65535.");
}

public partial class Program { }
