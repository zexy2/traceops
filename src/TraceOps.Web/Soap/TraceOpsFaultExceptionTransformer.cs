using System.Net;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Xml;
using SoapCore;
using SoapCore.Extensibility;

namespace TraceOps.Web.Soap;

internal sealed class TraceOpsFaultExceptionTransformer : IFaultExceptionTransformer
{
    private static readonly HashSet<string> KnownFaultCodes = new(StringComparer.Ordinal)
    {
        "INVALID_REQUEST",
        "INVALID_ACTION",
        "INVALID_CORRELATION_ID",
        "INVALID_TRACKING_NUMBER",
        "INVALID_EVENT_ID",
        "INVALID_STATUS",
        "INVALID_SOURCE_SYSTEM",
        "TIMESTAMP_MUST_BE_UTC",
        "SHIPMENT_NOT_FOUND",
        "TRACKING_NUMBER_CONFLICT",
        "EVENT_ID_CONFLICT",
        "INVALID_TRANSITION",
        "EVENT_TIME_REGRESSION",
        "PAYLOAD_TOO_LARGE",
        "UNSUPPORTED_MEDIA_TYPE",
        "SOAP_VERSION_MISMATCH",
        "SOAP_MUST_UNDERSTAND",
        "DATABASE_UNAVAILABLE",
        "INTERNAL_ERROR",
    };

    public Message ProvideFault(
        Exception exception,
        MessageVersion messageVersion,
        Message requestMessage,
        ConcurrentXmlNamespaceLookup xmlNamespaceLookup)
    {
        string detailCode;
        string envelopeCode;
        string correlationId;

        if (exception is FaultException<SoapFaultDetail> fault
            && fault.Detail is { } detail)
        {
            detailCode = KnownFaultCodes.Contains(detail.Code)
                ? detail.Code
                : "INTERNAL_ERROR";
            envelopeCode = string.Equals(fault.Code?.Name, "Receiver", StringComparison.Ordinal)
                ? "Receiver"
                : "Sender";
            correlationId = Guid.TryParseExact(detail.CorrelationId, "D", out _)
                ? detail.CorrelationId
                : SoapRequestContext.CorrelationId;
        }
        else
        {
            detailCode = "INTERNAL_ERROR";
            envelopeCode = "Receiver";
            correlationId = SoapRequestContext.CorrelationId;
        }

        var statusCode = envelopeCode == "Receiver"
            ? (int)HttpStatusCode.InternalServerError
            : (int)HttpStatusCode.BadRequest;
        SoapRequestContext.RecordFault(statusCode);

        var message = Message.CreateMessage(
            messageVersion,
            action: null,
            new TraceOpsFaultBodyWriter(detailCode, envelopeCode, correlationId));
        message.Properties[HttpResponseMessageProperty.Name] = new HttpResponseMessageProperty
        {
            StatusCode = (HttpStatusCode)statusCode,
        };
        return message;
    }
}

internal sealed class TraceOpsFaultBodyWriter(
    string detailCode,
    string envelopeCode,
    string correlationId) : BodyWriter(isBuffered: true)
{
    private const string Soap12Namespace = SoapFaultWriter.Soap12Namespace;
    private const string ServiceNamespace = "urn:traceops:soap:v1";

    protected override void OnWriteBodyContents(XmlDictionaryWriter writer)
    {
        writer.WriteStartElement("env", "Fault", Soap12Namespace);

        writer.WriteStartElement("env", "Code", Soap12Namespace);
        writer.WriteElementString("env", "Value", Soap12Namespace, "env:" + envelopeCode);
        writer.WriteEndElement();

        writer.WriteStartElement("env", "Reason", Soap12Namespace);
        writer.WriteStartElement("env", "Text", Soap12Namespace);
        writer.WriteAttributeString("xml", "lang", null, "en");
        writer.WriteString(SoapFaultWriter.ReasonFor(detailCode));
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("env", "Detail", Soap12Namespace);
        writer.WriteStartElement("t", "TraceOpsFault", ServiceNamespace);
        writer.WriteElementString("t", "code", ServiceNamespace, detailCode);
        writer.WriteElementString("t", "correlationId", ServiceNamespace, correlationId);
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteEndElement();
    }
}
