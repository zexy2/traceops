using TraceOps.Web.Transport;

namespace TraceOps.Web.Middleware;

public sealed class RequestBodyLimitMiddleware(RequestDelegate next)
{
    public const int MaximumBodyBytes = 16_384;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Method != HttpMethods.Post || !IsProtectedPostPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        if (context.Request.ContentLength is > MaximumBodyBytes)
        {
            await RejectAsync(context);
            return;
        }

        var originalBody = context.Request.Body;
        var buffered = new MemoryStream(capacity: MaximumBodyBytes);
        var chunk = new byte[4096];
        var total = 0;

        while (true)
        {
            var remainingProbeBytes = MaximumBodyBytes + 1 - total;
            var readSize = Math.Min(chunk.Length, remainingProbeBytes);
            var read = await originalBody.ReadAsync(chunk.AsMemory(0, readSize), context.RequestAborted);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaximumBodyBytes)
            {
                await buffered.DisposeAsync();
                await RejectAsync(context);
                return;
            }

            await buffered.WriteAsync(chunk.AsMemory(0, read), context.RequestAborted);
        }

        buffered.Position = 0;
        context.Request.Body = buffered;
        await next(context);
    }

    private static bool IsProtectedPostPath(PathString path) =>
        path.StartsWithSegments("/api/v1/shipments", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/shipments", StringComparison.OrdinalIgnoreCase);

    private Task RejectAsync(HttpContext context)
    {
        if (CorrelationIdMiddleware.IsApiRequest(context))
        {
            return ProblemResponseWriter.WriteAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "PAYLOAD_TOO_LARGE");
        }

        return CorrelationIdMiddleware.DispatchHtmlErrorAsync(
            context,
            next,
            StatusCodes.Status413PayloadTooLarge,
            "Request payload too large");
    }
}
