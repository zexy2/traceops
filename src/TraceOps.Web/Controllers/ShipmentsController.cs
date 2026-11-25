using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using TraceOps.Application;
using TraceOps.Domain;
using TraceOps.Web.Middleware;
using TraceOps.Web.Models;
using TraceOps.Web.Transport;

namespace TraceOps.Web.Controllers;

[Route("shipments")]
public sealed class ShipmentsController(
    ShipmentService shipmentService,
    TimeProvider timeProvider) : Controller
{
    [HttpGet("/")]
    public IActionResult Root() => Redirect("/shipments");

    [HttpGet("")]
    public IActionResult Index() => View(
        "~/Views/Shipments/Index.cshtml",
        new ListPage(shipmentService.List(), string.Empty));

    [HttpGet("{trackingNumber}")]
    public IActionResult Details(string trackingNumber)
    {
        var details = shipmentService.Find(trackingNumber);
        if (details is null)
        {
            return Error(StatusCodes.Status404NotFound, "Shipment not found", trackingNumber);
        }

        var nextStatus = DefaultNextStatus(details.Shipment.Status);
        var page = new DetailPage(
            details,
            Guid.NewGuid().ToString("D"),
            nextStatus,
            StrictInputParser.FormatUtc(timeProvider.GetUtcNow()),
            "demo-ui");
        return View("~/Views/Shipments/Details.cshtml", page);
    }

    [HttpPost("")]
    [ServiceFilter(typeof(HtmlAntiforgeryFilter))]
    public async Task<IActionResult> Register()
    {
        var form = await ReadFormAsync();
        var trackingNumber = GetValue(form, "TrackingNumber");
        var operation = shipmentService.Register(new RegisterShipmentCommand(trackingNumber));

        if (operation.Code == "REGISTERED" && operation.Shipment is not null)
        {
            return SeeOther($"/shipments/{operation.Shipment.TrackingNumber}");
        }

        if (operation.Code == "INVALID_TRACKING_NUMBER")
        {
            ModelState.AddModelError(
                "TrackingNumber",
                "Enter a canonical tracking number using uppercase letters, digits, and hyphens.");
        }
        else if (operation.Code == "TRACKING_NUMBER_CONFLICT")
        {
            ModelState.AddModelError("TrackingNumber", "A shipment with this tracking number already exists.");
        }
        else
        {
            ModelState.AddModelError(string.Empty, "The shipment could not be registered.");
        }

        return View(
            "~/Views/Shipments/Index.cshtml",
            new ListPage(shipmentService.List(), trackingNumber));
    }

    [HttpPost("{trackingNumber}/events")]
    [ServiceFilter(typeof(HtmlAntiforgeryFilter))]
    public async Task<IActionResult> AddEvent(string trackingNumber)
    {
        var current = shipmentService.Find(trackingNumber);
        if (current is null)
        {
            return Error(StatusCodes.Status404NotFound, "Shipment not found", trackingNumber);
        }

        var form = await ReadFormAsync();
        var eventIdText = GetValue(form, "EventId");
        var statusText = GetValue(form, "Status");
        var occurredAtText = GetValue(form, "OccurredAt");
        var sourceSystem = GetValue(form, "SourceSystem");

        var valid = true;
        if (!Guid.TryParseExact(eventIdText, "D", out var eventId))
        {
            ModelState.AddModelError("EventId", "Enter an event ID in UUID format.");
            valid = false;
        }

        if (!StrictInputParser.TryParseStatus(statusText, out var status))
        {
            ModelState.AddModelError("Status", "Select a valid shipment status.");
            valid = false;
        }

        if (!StrictInputParser.TryParseTimestamp(occurredAtText, out var occurredAt))
        {
            ModelState.AddModelError("OccurredAt", "Enter an ISO 8601 timestamp with an explicit timezone.");
            valid = false;
        }

        if (valid && ModelState.IsValid)
        {
            var operation = shipmentService.AddEvent(new AddShipmentEventCommand(
                trackingNumber,
                eventId,
                status,
                occurredAt,
                sourceSystem));

            if (operation.Code is "APPLIED" or "DUPLICATE")
            {
                return SeeOther($"/shipments/{trackingNumber}");
            }

            AddOperationError(operation.Code);
        }

        var latest = shipmentService.Find(trackingNumber);
        if (latest is null)
        {
            return Error(StatusCodes.Status404NotFound, "Shipment not found", trackingNumber);
        }

        var page = new DetailPage(
            latest,
            eventIdText,
            statusText,
            occurredAtText,
            sourceSystem);
        return View("~/Views/Shipments/Details.cshtml", page);
    }

    private IActionResult Error(int statusCode, string title, string? trackingNumber = null)
    {
        Response.StatusCode = statusCode;
        var page = new ErrorPage(
            statusCode,
            title,
            CorrelationIdMiddleware.GetCorrelationId(HttpContext),
            trackingNumber);
        HttpContext.Items[ErrorPageMiddleware.ErrorPageItem] = page;
        return View("~/Views/Shared/Error.cshtml", page);
    }

    private IActionResult SeeOther(string location)
    {
        Response.StatusCode = StatusCodes.Status303SeeOther;
        Response.Headers["Location"] = location;
        return new EmptyResult();
    }

    private async Task<IFormCollection> ReadFormAsync()
    {
        if (!Request.HasFormContentType)
        {
            ModelState.AddModelError(string.Empty, "Submit a URL-encoded form.");
            return new FormCollection(new Dictionary<string, StringValues>());
        }

        try
        {
            return await Request.ReadFormAsync();
        }
        catch (InvalidDataException)
        {
            ModelState.AddModelError(string.Empty, "The form submission is invalid.");
            return new FormCollection(new Dictionary<string, StringValues>());
        }
    }

    private static string GetValue(IFormCollection form, string key) =>
        form.TryGetValue(key, out var values) && values.Count > 0
            ? values[0] ?? string.Empty
            : string.Empty;

    private void AddOperationError(string code)
    {
        var (field, message) = code switch
        {
            "INVALID_EVENT_ID" => ("EventId", "Enter a non-empty event ID."),
            "INVALID_TRACKING_NUMBER" => ("", "The tracking number is invalid."),
            "INVALID_STATUS" => ("Status", "Select a valid shipment status."),
            "INVALID_SOURCE_SYSTEM" => ("SourceSystem", "Use 1 to 64 letters, digits, periods, underscores, or hyphens."),
            "TIMESTAMP_MUST_BE_UTC" => ("OccurredAt", "The event timestamp must use UTC."),
            "EVENT_ID_CONFLICT" => ("EventId", "This event ID already belongs to different event data."),
            "EVENT_TIME_REGRESSION" => ("", "The event time cannot be earlier than the latest recorded event."),
            "INVALID_TRANSITION" => ("", "This shipment status transition is not allowed."),
            "TRACKING_NUMBER_MISMATCH" => ("", "The event does not match this shipment."),
            "SHIPMENT_NOT_FOUND" => ("", "Shipment not found."),
            _ => ("", "The event could not be added."),
        };

        ModelState.AddModelError(field, message);
    }

    private static string DefaultNextStatus(ShipmentStatus status) => status switch
    {
        ShipmentStatus.Registered => nameof(ShipmentStatus.Accepted),
        ShipmentStatus.Accepted => nameof(ShipmentStatus.InTransit),
        ShipmentStatus.InTransit => nameof(ShipmentStatus.OutForDelivery),
        ShipmentStatus.OutForDelivery => nameof(ShipmentStatus.Delivered),
        ShipmentStatus.DeliveryFailed => nameof(ShipmentStatus.OutForDelivery),
        ShipmentStatus.Delivered or ShipmentStatus.Cancelled => string.Empty,
        _ => string.Empty,
    };
}

public sealed class HtmlAntiforgeryFilter(
    IAntiforgery antiforgery,
    IModelMetadataProvider metadataProvider) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (!MediaTypeHeaderValue.TryParse(context.HttpContext.Request.ContentType, out var contentType)
            || !string.Equals(contentType.MediaType.Value, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            SetErrorResult(context, StatusCodes.Status415UnsupportedMediaType, "Unsupported form content type");
            return;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            SetErrorResult(context, StatusCodes.Status400BadRequest, "Invalid form submission");
        }
    }

    private void SetErrorResult(AuthorizationFilterContext context, int statusCode, string title)
    {
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(context.HttpContext);
        var page = new ErrorPage(statusCode, title, correlationId);
        var viewData = new ViewDataDictionary<ErrorPage>(metadataProvider, context.ModelState)
        {
            Model = page,
        };
        context.HttpContext.Response.StatusCode = statusCode;
        context.HttpContext.Response.Headers[CorrelationIdMiddleware.HeaderName] = correlationId;
        context.Result = new ViewResult
        {
            ViewName = "~/Views/Shared/Error.cshtml",
            ViewData = viewData,
        };
    }
}
