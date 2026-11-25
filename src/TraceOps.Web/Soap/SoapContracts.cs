using System.ServiceModel;
using System.Xml.Serialization;

namespace TraceOps.Web.Soap;

internal static class SoapContractMetadata
{
    public const string Namespace = "urn:traceops:soap:v1";
    public const string RegisterAction = Namespace + "/ITraceOpsSoapService/RegisterShipment";
    public const string GetAction = Namespace + "/ITraceOpsSoapService/GetShipment";
    public const string AddEventAction = Namespace + "/ITraceOpsSoapService/AddShipmentEvent";
}

[ServiceContract(Name = "ITraceOpsSoapService", Namespace = SoapContractMetadata.Namespace)]
public interface ITraceOpsSoapService
{
    [OperationContract(
        Action = SoapContractMetadata.RegisterAction,
        ReplyAction = SoapContractMetadata.RegisterAction + "Response")]
    [FaultContract(typeof(SoapFaultDetail), Name = "TraceOpsFault", Namespace = SoapContractMetadata.Namespace)]
    SoapShipmentResponse RegisterShipment(SoapRegisterShipmentRequest request);

    [OperationContract(
        Action = SoapContractMetadata.GetAction,
        ReplyAction = SoapContractMetadata.GetAction + "Response")]
    [FaultContract(typeof(SoapFaultDetail), Name = "TraceOpsFault", Namespace = SoapContractMetadata.Namespace)]
    SoapShipmentResponse GetShipment(SoapGetShipmentRequest request);

    [OperationContract(
        Action = SoapContractMetadata.AddEventAction,
        ReplyAction = SoapContractMetadata.AddEventAction + "Response")]
    [FaultContract(typeof(SoapFaultDetail), Name = "TraceOpsFault", Namespace = SoapContractMetadata.Namespace)]
    SoapEventResponse AddShipmentEvent(SoapAddShipmentEventRequest request);
}

[XmlType(Namespace = SoapContractMetadata.Namespace)]
public sealed class SoapRegisterShipmentRequest
{
    public SoapRegisterShipmentRequest() { }

    [XmlElement("trackingNumber", Order = 0, Namespace = SoapContractMetadata.Namespace)]
    public string TrackingNumber { get; set; } = string.Empty;
}

[XmlType(Namespace = SoapContractMetadata.Namespace)]
public sealed class SoapGetShipmentRequest
{
    public SoapGetShipmentRequest() { }

    [XmlElement("trackingNumber", Order = 0, Namespace = SoapContractMetadata.Namespace)]
    public string TrackingNumber { get; set; } = string.Empty;
}

[XmlType(Namespace = SoapContractMetadata.Namespace)]
public sealed class SoapAddShipmentEventRequest
{
    public SoapAddShipmentEventRequest() { }

    [XmlElement("trackingNumber", Order = 0, Namespace = SoapContractMetadata.Namespace)]
    public string TrackingNumber { get; set; } = string.Empty;

    [XmlElement("eventId", Order = 1, Namespace = SoapContractMetadata.Namespace)]
    public string EventId { get; set; } = string.Empty;

    [XmlElement("status", Order = 2, Namespace = SoapContractMetadata.Namespace)]
    public string Status { get; set; } = string.Empty;

    [XmlElement("occurredAt", Order = 3, Namespace = SoapContractMetadata.Namespace)]
    public string OccurredAt { get; set; } = string.Empty;

    [XmlElement("sourceSystem", Order = 4, Namespace = SoapContractMetadata.Namespace)]
    public string SourceSystem { get; set; } = string.Empty;
}

[XmlType(Namespace = SoapContractMetadata.Namespace)]
public sealed class SoapShipmentResponse
{
    public SoapShipmentResponse() { }

    [XmlElement("code", Order = 0, Namespace = SoapContractMetadata.Namespace)]
    public string Code { get; set; } = string.Empty;

    [XmlElement("shipment", Order = 1, Namespace = SoapContractMetadata.Namespace)]
    public SoapShipmentSummary Shipment { get; set; } = new();
}

[XmlType(Namespace = SoapContractMetadata.Namespace)]
public sealed class SoapEventResponse
{
    public SoapEventResponse() { }

    [XmlElement("code", Order = 0, Namespace = SoapContractMetadata.Namespace)]
    public string Code { get; set; } = string.Empty;

    [XmlElement("eventId", Order = 1, Namespace = SoapContractMetadata.Namespace)]
    public string EventId { get; set; } = string.Empty;

    [XmlElement("duplicate", Order = 2, Namespace = SoapContractMetadata.Namespace)]
    public bool Duplicate { get; set; }

    [XmlElement("shipment", Order = 3, Namespace = SoapContractMetadata.Namespace)]
    public SoapShipmentSummary Shipment { get; set; } = new();
}

[XmlType(Namespace = SoapContractMetadata.Namespace)]
public sealed class SoapShipmentSummary
{
    public SoapShipmentSummary() { }

    [XmlElement("trackingNumber", Order = 0, Namespace = SoapContractMetadata.Namespace)]
    public string TrackingNumber { get; set; } = string.Empty;

    [XmlElement("status", Order = 1, Namespace = SoapContractMetadata.Namespace)]
    public string Status { get; set; } = string.Empty;

    [XmlElement("lastOccurredAt", Order = 2, Namespace = SoapContractMetadata.Namespace)]
    public string LastOccurredAt { get; set; } = string.Empty;

    [XmlElement("version", Order = 3, Namespace = SoapContractMetadata.Namespace)]
    public long Version { get; set; }
}

[XmlType(Namespace = SoapContractMetadata.Namespace)]
public sealed class SoapFaultDetail
{
    public SoapFaultDetail() { }

    [XmlElement("code", Order = 0, Namespace = SoapContractMetadata.Namespace)]
    public string Code { get; set; } = string.Empty;

    [XmlElement("correlationId", Order = 1, Namespace = SoapContractMetadata.Namespace)]
    public string CorrelationId { get; set; } = string.Empty;
}
