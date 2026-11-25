using TraceOps.Web.Models;
using TraceOps.Web.Runtime;
using TraceOps.Web.Soap;
using TraceOps.Web.Transport;

namespace TraceOps.Web.Middleware;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";
    private const string CorrelationItem = "TraceOps.CorrelationId";
    private const string OriginalPathItem = "TraceOps.OriginalPath";
    private const string ErrorDispatchItem = "TraceOps.ErrorDispatch";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Items.ContainsKey(OriginalPathItem))
        {
            context.Items[OriginalPathItem] = context.Request.Path.Value ?? "/";
        }

        if (context.Items.ContainsKey(ErrorDispatchItem))
        {
            SetResponseHeader(context);
            await next(context);
            return;
        }

        var hasHeader = context.Request.Headers.TryGetValue(HeaderName, out var values);
        var supplied = values.Count == 1 ? values[0] : null;
        var valid = !hasHeader
            || (values.Count == 1
                && Guid.TryParseExact(supplied, "D", out _));
        var correlationId = valid && hasHeader
            ? supplied!
            : Guid.NewGuid().ToString("D");
        context.Items[CorrelationItem] = correlationId;
        SetResponseHeader(context);

        if (!valid)
        {
            if (RuntimeBanner.For(context).SoapEnabled
                && SoapRequestGuardMiddleware.IsExactEndpoint(context.Request.Path))
            {
                await SoapFaultWriter.WriteAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "INVALID_CORRELATION_ID",
                    "Sender");
            }
            else if (IsApiRequest(context))
            {
                await ProblemResponseWriter.WriteAsync(context, StatusCodes.Status400BadRequest, "INVALID_CORRELATION_ID");
            }
            else
            {
                await DispatchHtmlErrorAsync(context, next, StatusCodes.Status400BadRequest, "Invalid request");
            }

            return;
        }

        await next(context);
    }

    public static string GetCorrelationId(HttpContext context) =>
        context.Items[CorrelationItem] as string ?? Guid.NewGuid().ToString("D");

    public static string GetOriginalPath(HttpContext context) =>
        context.Items[OriginalPathItem] as string ?? context.Request.Path.Value ?? "/";

    public static bool IsApiRequest(HttpContext context)
    {
        var path = GetOriginalPath(context);
        return path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/api", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task DispatchHtmlErrorAsync(
        HttpContext context,
        RequestDelegate next,
        int statusCode,
        string title,
        string? trackingNumber = null)
    {
        var correlationId = GetCorrelationId(context);
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.Headers[HeaderName] = correlationId;
        context.Items[ErrorDispatchItem] = true;
        context.Items[ErrorPageMiddleware.ErrorPageItem] =
            new ErrorPage(statusCode, title, correlationId, trackingNumber);
        context.SetEndpoint(null);
        context.Request.RouteValues.Clear();

        var previousPath = context.Request.Path;
        var previousQuery = context.Request.QueryString;
        var previousMethod = context.Request.Method;
        context.Request.Path = "/__traceops/error";
        context.Request.QueryString = QueryString.Empty;
        context.Request.Method = HttpMethods.Get;
        try
        {
            await next(context);
        }
        finally
        {
            context.Request.Path = previousPath;
            context.Request.QueryString = previousQuery;
            context.Request.Method = previousMethod;
        }
    }

    private static void SetResponseHeader(HttpContext context)
    {
        var correlationId = GetCorrelationId(context);
        context.Response.Headers[HeaderName] = correlationId;
        context.Response.OnStarting(static state =>
        {
            var httpContext = (HttpContext)state;
            httpContext.Response.Headers[HeaderName] = GetCorrelationId(httpContext);
            return Task.CompletedTask;
        }, context);
    }
}
