using System.Text.Json.Serialization;

namespace TraceOps.Web.Transport;

public sealed record ApiProblem(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("instance")] string Instance,
    [property: JsonPropertyName("correlationId")] string CorrelationId);
