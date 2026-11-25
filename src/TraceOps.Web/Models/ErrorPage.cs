namespace TraceOps.Web.Models;

public sealed record ErrorPage(
    int StatusCode,
    string Title,
    string CorrelationId,
    string? TrackingNumber = null);
