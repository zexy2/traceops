using Microsoft.Net.Http.Headers;
using TraceOps.Web.Transport;

namespace TraceOps.Web.Middleware;

public sealed class ApiContentTypeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Method == HttpMethods.Post
            && IsShipmentApiPath(context.Request.Path)
            && !IsJsonContentType(context.Request.ContentType))
        {
            await ProblemResponseWriter.WriteAsync(
                context,
                StatusCodes.Status415UnsupportedMediaType,
                "UNSUPPORTED_MEDIA_TYPE");
            return;
        }

        await next(context);
    }

    private static bool IsShipmentApiPath(PathString path) =>
        path.StartsWithSegments("/api/v1/shipments", StringComparison.OrdinalIgnoreCase);

    private static bool IsJsonContentType(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && string.Equals(parsed.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase);
}
