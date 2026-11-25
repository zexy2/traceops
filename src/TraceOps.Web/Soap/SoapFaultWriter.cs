using System.ServiceModel;
using System.Threading;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TraceOps.Web.Middleware;

namespace TraceOps.Web.Soap;

internal static class SoapFaultWriter
{
    public const string Soap12Namespace = "http://www.w3.org/2003/05/soap-envelope";
    public const string Soap11Namespace = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Soap12 = Soap12Namespace;
    private static readonly XNamespace Service = SoapContractMetadata.Namespace;

    public static async Task WriteAsync(
        HttpContext context,
        int statusCode,
        string detailCode,
        string envelopeCode)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        var correlationId = CorrelationIdMiddleware.GetCorrelationId(context);
        var document = new XDocument(
            new XElement(Soap12 + "Envelope",
                new XAttribute(XNamespace.Xmlns + "env", Soap12Namespace),
                new XAttribute(XNamespace.Xmlns + "t", Service.NamespaceName),
                new XElement(Soap12 + "Body",
                    new XElement(Soap12 + "Fault",
                        new XElement(Soap12 + "Code",
                            new XElement(Soap12 + "Value", "env:" + envelopeCode)),
                        new XElement(Soap12 + "Reason",
                            new XElement(Soap12 + "Text",
                                new XAttribute(XNamespace.Xml + "lang", "en"),
                                ReasonFor(detailCode))),
                        new XElement(Soap12 + "Detail",
                            new XElement(Service + "TraceOpsFault",
                                new XElement(Service + "code", detailCode),
                                new XElement(Service + "correlationId", correlationId)))))));

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/soap+xml; charset=utf-8";
        context.Response.Headers[CorrelationIdMiddleware.HeaderName] = correlationId;
        await context.Response.WriteAsync(
            document.ToString(SaveOptions.DisableFormatting),
            System.Text.Encoding.UTF8,
            context.RequestAborted);
    }

    public static FaultException<SoapFaultDetail> CreateException(
        string detailCode,
        string envelopeCode = "Sender",
        int statusCode = StatusCodes.Status400BadRequest)
    {
        SoapRequestContext.RecordFault(statusCode);
        var detail = new SoapFaultDetail
        {
            Code = detailCode,
            CorrelationId = SoapRequestContext.CorrelationId,
        };
        return new FaultException<SoapFaultDetail>(
            detail,
            new FaultReason(ReasonFor(detailCode)),
            new FaultCode(envelopeCode, Soap12Namespace));
    }

    internal static string ReasonFor(string code) => code switch
    {
        "SOAP_VERSION_MISMATCH" => "The SOAP version is not supported.",
        "SOAP_MUST_UNDERSTAND" => "A mandatory SOAP header is not understood.",
        "INTERNAL_ERROR" or "DATABASE_UNAVAILABLE" => "The request could not be processed.",
        _ => "The request is invalid.",
    };
}

internal static class SoapRequestContext
{
    private const string FaultStatusItem = "TraceOps.SoapFaultStatus";
    private static readonly AsyncLocal<HttpContext?> CurrentContext = new();

    public static HttpContext? Current
    {
        get => CurrentContext.Value;
        set => CurrentContext.Value = value;
    }

    public static string CorrelationId =>
        Current is null
            ? Guid.NewGuid().ToString("D")
            : CorrelationIdMiddleware.GetCorrelationId(Current);

    public static void RecordFault(int statusCode)
    {
        if (Current is not null)
        {
            Current.Items[FaultStatusItem] = statusCode;
        }
    }

    public static bool TryGetFaultStatus(HttpContext context, out int statusCode)
    {
        if (context.Items.TryGetValue(FaultStatusItem, out var value)
            && value is int recordedStatus)
        {
            statusCode = recordedStatus;
            return true;
        }

        statusCode = default;
        return false;
    }

    public static void LogInternalErrorSafely()
    {
        try
        {
            var logger = Current?.RequestServices.GetService<ILogger<ExceptionHandlingMiddleware>>();
            logger?.LogError("Unhandled TraceOps SOAP operation failure.");
        }
        catch (Exception)
        {
            // Logging failure must not alter the safe SOAP fault.
        }
    }
}
