using TraceOps.Application;

namespace TraceOps.Web.Models;

public sealed record ListPage(
    IReadOnlyList<ShipmentSummary> Shipments,
    string RetainedTrackingNumber);

public sealed record DetailPage(
    ShipmentDetails Details,
    string RetainedEventId,
    string RetainedStatus,
    string RetainedOccurredAt,
    string RetainedSourceSystem);
