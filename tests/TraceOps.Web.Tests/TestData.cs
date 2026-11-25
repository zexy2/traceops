using System.Globalization;
using System.Text.Json;
using TraceOps.Domain;

namespace TraceOps.Web.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
    public static readonly Guid EventId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public const string Tracking = "TEST-001";
    public const string Banner = "Synthetic demo — volatile in-memory storage. Restarting loses changes. SQL Server and SOAP are not connected.";
    public const string Secret = "SECRET-sql-password-do-not-echo";

    public static LifecycleEvent Event(
        ShipmentStatus status = ShipmentStatus.Accepted,
        DateTimeOffset? time = null,
        Guid? id = null,
        string source = "test.api",
        string tracking = Tracking) =>
        new(id ?? EventId, tracking, status, time ?? Now, source);

    public static string EventJson(
        string status = "Accepted", string? time = null, string? id = null,
        string source = "test.api") => JsonSerializer.Serialize(new
        {
            eventId = id ?? EventId.ToString("D"),
            status,
            occurredAt = time ?? Now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            sourceSystem = source,
        });

    public static Dictionary<string, string> EventForm(
        string status = "Accepted", string? time = null, string? id = null,
        string source = "test.api") => new()
        {
            ["EventId"] = id ?? EventId.ToString("D"),
            ["Status"] = status,
            ["OccurredAt"] = time ?? Now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["SourceSystem"] = source,
        };
}

internal sealed class FixedTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => TestData.Now;
}
