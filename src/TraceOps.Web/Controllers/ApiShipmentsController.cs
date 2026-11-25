using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TraceOps.Application;
using TraceOps.Domain;
using TraceOps.Web.Transport;

namespace TraceOps.Web.Controllers;

[Route("api/v1/shipments")]
public sealed class ApiShipmentsController(ShipmentService shipmentService) : ControllerBase
{
    private static readonly string[] RegisterFields = ["trackingNumber"];
    private static readonly string[] EventFields = ["eventId", "status", "occurredAt", "sourceSystem"];

    [HttpPost("")]
    public async Task<IActionResult> Register()
    {
        using var document = await ReadJsonAsync();
        if (document is null
            || !TryGetObjectFields(document.RootElement, RegisterFields, out var fields)
            || !TryGetString(fields["trackingNumber"], out var trackingNumber))
        {
            return Problem(400, "INVALID_REQUEST");
        }

        var operation = shipmentService.Register(new RegisterShipmentCommand(trackingNumber));
        if (operation.Code == "REGISTERED" && operation.Shipment is not null)
        {
            var location = $"/api/v1/shipments/{operation.Shipment.TrackingNumber}";
            Response.Headers["Location"] = location;
            return Json(Summary(operation.Shipment), statusCode: StatusCodes.Status201Created);
        }

        return operation.Code switch
        {
            "INVALID_TRACKING_NUMBER" => Problem(422, operation.Code),
            "TRACKING_NUMBER_CONFLICT" => Problem(409, operation.Code),
            _ => Problem(500, "INTERNAL_ERROR"),
        };
    }

    [HttpGet("{trackingNumber}")]
    public IActionResult Get(string trackingNumber)
    {
        if (!StrictInputParser.IsCanonicalTrackingNumber(trackingNumber))
        {
            return Problem(404, "SHIPMENT_NOT_FOUND");
        }

        var details = shipmentService.Find(trackingNumber);
        return details is null
            ? Problem(404, "SHIPMENT_NOT_FOUND")
            : Json(Summary(details.Shipment));
    }

    [HttpPost("{trackingNumber}/events")]
    public async Task<IActionResult> AddEvent(string trackingNumber)
    {
        using var document = await ReadJsonAsync();
        if (document is null
            || !TryGetObjectFields(document.RootElement, EventFields, out var fields))
        {
            return Problem(400, "INVALID_REQUEST");
        }

        foreach (var name in EventFields)
        {
            if (fields[name].ValueKind == JsonValueKind.Null)
            {
                return Problem(400, "INVALID_REQUEST");
            }
        }

        if (!TryGetString(fields["eventId"], out var eventIdText)
            || !TryGetString(fields["occurredAt"], out var occurredAtText)
            || !TryGetString(fields["sourceSystem"], out var sourceSystem))
        {
            return Problem(400, "INVALID_REQUEST");
        }

        if (fields["status"].ValueKind == JsonValueKind.Number)
        {
            return Problem(422, "INVALID_STATUS");
        }

        if (fields["status"].ValueKind != JsonValueKind.String)
        {
            return Problem(400, "INVALID_REQUEST");
        }

        var statusText = fields["status"].GetString();
        if (!Guid.TryParseExact(eventIdText, "D", out var eventId)
            || !StrictInputParser.TryParseTimestamp(occurredAtText, out var occurredAt))
        {
            return Problem(400, "INVALID_REQUEST");
        }

        if (!StrictInputParser.TryParseStatus(statusText, out var status))
        {
            return Problem(422, "INVALID_STATUS");
        }

        if (occurredAt.Offset == TimeSpan.Zero && !occurredAtText.EndsWith('Z'))
        {
            return Problem(422, "TIMESTAMP_MUST_BE_UTC");
        }

        if (!StrictInputParser.IsCanonicalTrackingNumber(trackingNumber))
        {
            return Problem(404, "SHIPMENT_NOT_FOUND");
        }

        var operation = shipmentService.AddEvent(new AddShipmentEventCommand(
            trackingNumber,
            eventId,
            status,
            occurredAt,
            sourceSystem));

        if (operation.Code is "APPLIED" or "DUPLICATE")
        {
            if (operation.Shipment is null)
            {
                return Problem(500, "INTERNAL_ERROR");
            }

            var result = new EventReceipt(
                eventId.ToString("D"),
                operation.Code == "DUPLICATE",
                Summary(operation.Shipment));
            return Json(result, statusCode: operation.Code == "DUPLICATE"
                ? StatusCodes.Status200OK
                : StatusCodes.Status201Created);
        }

        var (statusCode, code) = operation.Code switch
        {
            "SHIPMENT_NOT_FOUND" => (404, operation.Code),
            "TRACKING_NUMBER_CONFLICT" or "EVENT_ID_CONFLICT" or "INVALID_TRANSITION" or "EVENT_TIME_REGRESSION"
                => (409, operation.Code),
            "INVALID_TRACKING_NUMBER" or "INVALID_EVENT_ID" or "INVALID_STATUS" or "INVALID_SOURCE_SYSTEM" or "TIMESTAMP_MUST_BE_UTC"
                => (422, operation.Code),
            _ => (500, "INTERNAL_ERROR"),
        };
        return Problem(statusCode, code);
    }

    private async Task<JsonDocument?> ReadJsonAsync()
    {
        try
        {
            return await JsonDocument.ParseAsync(
                Request.Body,
                new JsonDocumentOptions { MaxDepth = 8 },
                HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetObjectFields(
        JsonElement root,
        IReadOnlyCollection<string> requiredNames,
        out Dictionary<string, JsonElement> fields)
    {
        fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (!requiredNames.Contains(property.Name, StringComparer.Ordinal)
                || !fields.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }

        return requiredNames.All(fields.ContainsKey);
    }

    private static bool TryGetString(JsonElement value, out string text)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            text = value.GetString()!;
            return true;
        }

        text = string.Empty;
        return false;
    }

    private JsonResult Problem(int status, string code) =>
        ProblemResponseWriter.CreateResult(HttpContext, status, code);

    private JsonResult Json(object value, int statusCode = StatusCodes.Status200OK) => new(value)
    {
        StatusCode = statusCode,
        ContentType = "application/json",
    };

    private static ShipmentSummaryResponse Summary(ShipmentSummary summary) => new(
        summary.TrackingNumber,
        summary.Status.ToString(),
        StrictInputParser.FormatUtc(summary.LastOccurredAt),
        summary.Version);

    private sealed record ShipmentSummaryResponse(
        string TrackingNumber,
        string Status,
        string LastOccurredAt,
        long Version);

    private sealed record EventReceipt(
        string EventId,
        bool Duplicate,
        ShipmentSummaryResponse Shipment);
}

[Route("api/{**path}")]
public sealed class ApiFallbackController : ControllerBase
{
    [AcceptVerbs("GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS")]
    public IActionResult NotFoundApi() =>
        ProblemResponseWriter.CreateResult(HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND");
}
