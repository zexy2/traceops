using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace TraceOps.Web.Transport;

public static class ProblemResponseWriter
{
    public static JsonResult CreateResult(HttpContext context, int status, string code)
    {
        var correlationId = Middleware.CorrelationIdMiddleware.GetCorrelationId(context);
        var instance = Middleware.CorrelationIdMiddleware.GetOriginalPath(context);
        var problem = new ApiProblem(
            "about:blank",
            ReasonPhrases.GetReasonPhrase(status),
            status,
            code,
            DetailFor(code),
            instance,
            correlationId);

        context.Response.Headers[Middleware.CorrelationIdMiddleware.HeaderName] = correlationId;
        return new JsonResult(problem)
        {
            StatusCode = status,
            ContentType = "application/problem+json",
        };
    }

    public static async Task WriteAsync(HttpContext context, int status, string code)
    {
        context.Response.Clear();
        context.Response.Headers[Middleware.CorrelationIdMiddleware.HeaderName] =
            Middleware.CorrelationIdMiddleware.GetCorrelationId(context);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json; charset=utf-8";

        var problem = new ApiProblem(
            "about:blank",
            ReasonPhrases.GetReasonPhrase(status),
            status,
            code,
            DetailFor(code),
            Middleware.CorrelationIdMiddleware.GetOriginalPath(context),
            Middleware.CorrelationIdMiddleware.GetCorrelationId(context));

        await System.Text.Json.JsonSerializer.SerializeAsync(
            context.Response.Body,
            problem,
            cancellationToken: context.RequestAborted);
    }

    private static string DetailFor(string code) => code switch
    {
        "INVALID_REQUEST" => "The request is invalid.",
        "INVALID_CORRELATION_ID" => "The correlation identifier is invalid.",
        "SHIPMENT_NOT_FOUND" => "The shipment was not found.",
        "NOT_FOUND" => "The API endpoint was not found.",
        "TRACKING_NUMBER_CONFLICT" => "A shipment with this tracking number already exists.",
        "EVENT_ID_CONFLICT" => "This event identifier was already used with different event data.",
        "INVALID_TRANSITION" => "The requested shipment status transition is not allowed.",
        "EVENT_TIME_REGRESSION" => "The event time is earlier than the latest recorded event.",
        "INVALID_TRACKING_NUMBER" => "The tracking number is invalid.",
        "INVALID_EVENT_ID" => "The event identifier is invalid.",
        "INVALID_STATUS" => "The shipment status is invalid.",
        "INVALID_SOURCE_SYSTEM" => "The source system is invalid.",
        "TIMESTAMP_MUST_BE_UTC" => "The event timestamp must use UTC.",
        "PAYLOAD_TOO_LARGE" => "The request body exceeds the allowed size.",
        "UNSUPPORTED_MEDIA_TYPE" => "The request content type is not supported.",
        "INTERNAL_ERROR" => "An unexpected error occurred.",
        _ => "The request could not be completed.",
    };
}
