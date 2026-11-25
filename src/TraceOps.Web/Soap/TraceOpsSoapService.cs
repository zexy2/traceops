using TraceOps.Application;
using TraceOps.Domain;
using TraceOps.Web.Transport;
using System.ComponentModel;
using System.Net.Sockets;
using System.ServiceModel;
using Microsoft.Data.SqlClient;

namespace TraceOps.Web.Soap;

public sealed class TraceOpsSoapService : ITraceOpsSoapService
{
    private const int SqlClientTransportWaitTimeoutNumber = 258;
    private const int SqlClientQueryTimeoutNumber = -2;

    private readonly ShipmentService _shipmentService;

    public TraceOpsSoapService(ShipmentService shipmentService)
    {
        _shipmentService = shipmentService
            ?? throw new ArgumentNullException(nameof(shipmentService));
    }

    public SoapShipmentResponse RegisterShipment(SoapRegisterShipmentRequest request) =>
        Execute(() => RegisterShipmentCore(request));

    public SoapShipmentResponse GetShipment(SoapGetShipmentRequest request) =>
        Execute(() => GetShipmentCore(request));

    public SoapEventResponse AddShipmentEvent(SoapAddShipmentEventRequest request) =>
        Execute(() => AddShipmentEventCore(request));

    private SoapShipmentResponse RegisterShipmentCore(SoapRegisterShipmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!StrictInputParser.IsCanonicalTrackingNumber(request.TrackingNumber))
        {
            throw SoapFaultWriter.CreateException("INVALID_TRACKING_NUMBER");
        }

        var result = _shipmentService.Register(new RegisterShipmentCommand(request.TrackingNumber));
        if (result.Code == "TRACKING_NUMBER_CONFLICT")
        {
            throw SoapFaultWriter.CreateException(result.Code);
        }

        if (result.Code != "REGISTERED" || result.Shipment is null)
        {
            throw SoapFaultWriter.CreateException("INTERNAL_ERROR", "Receiver", 500);
        }

        return new SoapShipmentResponse
        {
            Code = "REGISTERED",
            Shipment = ToSoapSummary(result.Shipment),
        };
    }

    private SoapShipmentResponse GetShipmentCore(SoapGetShipmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!StrictInputParser.IsCanonicalTrackingNumber(request.TrackingNumber))
        {
            throw SoapFaultWriter.CreateException("INVALID_TRACKING_NUMBER");
        }

        var details = _shipmentService.Find(request.TrackingNumber);
        if (details is null)
        {
            throw SoapFaultWriter.CreateException("SHIPMENT_NOT_FOUND");
        }

        return new SoapShipmentResponse
        {
            Code = "FOUND",
            Shipment = ToSoapSummary(details.Shipment),
        };
    }

    private SoapEventResponse AddShipmentEventCore(SoapAddShipmentEventRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!StrictInputParser.IsCanonicalTrackingNumber(request.TrackingNumber))
        {
            throw SoapFaultWriter.CreateException("INVALID_TRACKING_NUMBER");
        }

        if (!Guid.TryParseExact(request.EventId, "D", out var eventId)
            || !string.Equals(
                request.EventId,
                eventId.ToString("D"),
                StringComparison.OrdinalIgnoreCase))
        {
            throw SoapFaultWriter.CreateException("INVALID_REQUEST");
        }

        if (eventId == Guid.Empty)
        {
            throw SoapFaultWriter.CreateException("INVALID_EVENT_ID");
        }

        if (!StrictInputParser.TryParseStatus(request.Status, out var status))
        {
            throw SoapFaultWriter.CreateException("INVALID_STATUS");
        }

        if (!StrictInputParser.TryParseTimestamp(request.OccurredAt, out var occurredAt))
        {
            throw SoapFaultWriter.CreateException("INVALID_REQUEST");
        }

        if (occurredAt.Offset == TimeSpan.Zero
            && !request.OccurredAt.EndsWith('Z'))
        {
            throw SoapFaultWriter.CreateException("TIMESTAMP_MUST_BE_UTC");
        }

        var result = _shipmentService.AddEvent(new AddShipmentEventCommand(
            request.TrackingNumber,
            eventId,
            status,
            occurredAt,
            request.SourceSystem));

        if (result.Code is not ("APPLIED" or "DUPLICATE")
            || result.Shipment is null
            || result.RecordedEvent is null)
        {
            throw SoapFaultWriter.CreateException(result.Code switch
            {
                "TRACKING_NUMBER_MISMATCH" => "INVALID_TRACKING_NUMBER",
                "INVALID_EVENT_ID" => "INVALID_EVENT_ID",
                "INVALID_TRACKING_NUMBER" => "INVALID_TRACKING_NUMBER",
                "INVALID_STATUS" => "INVALID_STATUS",
                "INVALID_SOURCE_SYSTEM" => "INVALID_SOURCE_SYSTEM",
                "TIMESTAMP_MUST_BE_UTC" => "TIMESTAMP_MUST_BE_UTC",
                "EVENT_TIME_REGRESSION" => "EVENT_TIME_REGRESSION",
                "INVALID_TRANSITION" => "INVALID_TRANSITION",
                "EVENT_ID_CONFLICT" => "EVENT_ID_CONFLICT",
                "SHIPMENT_NOT_FOUND" => "SHIPMENT_NOT_FOUND",
                _ => "INTERNAL_ERROR",
            }, result.Code is "APPLIED" or "DUPLICATE" ? "Receiver" : "Sender",
                result.Code is "APPLIED" or "DUPLICATE" ? 500 : 400);
        }

        return new SoapEventResponse
        {
            Code = result.Code,
            EventId = result.RecordedEvent.EventId.ToString("D"),
            Duplicate = result.Code == "DUPLICATE",
            Shipment = ToSoapSummary(result.Shipment),
        };
    }

    private static SoapShipmentSummary ToSoapSummary(ShipmentSummary summary) =>
        new()
        {
            TrackingNumber = summary.TrackingNumber,
            Status = summary.Status.ToString(),
            LastOccurredAt = StrictInputParser.FormatUtc(summary.LastOccurredAt),
            Version = summary.Version,
        };

    private static T Execute<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (FaultException<SoapFaultDetail>)
        {
            throw;
        }
        catch (Exception exception) when (IsDatabaseUnavailable(exception))
        {
            SoapRequestContext.LogInternalErrorSafely();
            throw SoapFaultWriter.CreateException("DATABASE_UNAVAILABLE", "Receiver", 500);
        }
        catch (Exception)
        {
            SoapRequestContext.LogInternalErrorSafely();
            throw SoapFaultWriter.CreateException("INTERNAL_ERROR", "Receiver", 500);
        }
    }

    private static bool IsDatabaseUnavailable(Exception exception)
    {
        if (exception is AggregateException aggregateException)
        {
            var failures = aggregateException.Flatten().InnerExceptions;
            return failures.Count > 0 && failures.All(IsDatabaseUnavailable);
        }

        if (exception is not SqlException sqlException || sqlException.Errors.Count != 1)
        {
            return false;
        }

        var errorNumber = sqlException.Errors[0].Number;
        if (errorNumber == SqlClientTransportWaitTimeoutNumber
            && sqlException.InnerException is Win32Exception)
        {
            return true;
        }

        if (errorNumber == SqlClientQueryTimeoutNumber
            && sqlException.InnerException is not SocketException)
        {
            return false;
        }

        return sqlException.InnerException switch
        {
            SocketException socketException => IsAvailabilitySocketError(socketException.SocketErrorCode),
            Win32Exception win32Exception => IsAvailabilityWin32Error(win32Exception.NativeErrorCode),
            _ => false,
        };
    }

    private static bool IsAvailabilitySocketError(SocketError error) => error is
        SocketError.NetworkDown
            or SocketError.NetworkUnreachable
            or SocketError.NetworkReset
            or SocketError.ConnectionAborted
            or SocketError.ConnectionReset
            or SocketError.TimedOut
            or SocketError.ConnectionRefused
            or SocketError.HostDown
            or SocketError.HostUnreachable;

    private static bool IsAvailabilityWin32Error(int errorCode) => errorCode is
        10050 or 10051 or 10052 or 10053 or 10054 or 10060 or 10061 or 10064 or 10065;
}
