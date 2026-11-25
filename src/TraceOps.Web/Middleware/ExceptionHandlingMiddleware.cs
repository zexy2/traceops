using TraceOps.Web.Runtime;
using TraceOps.Web.Soap;
using TraceOps.Web.Transport;

namespace TraceOps.Web.Middleware;

public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception)
        {
            try
            {
                logger.LogError("Unhandled TraceOps request failure.");
            }
            catch (Exception)
            {
                // Logging must not interrupt the safe error response.
            }

            if (context.Items.ContainsKey("TraceOps.ErrorDispatch"))
            {
                await WriteMinimalHtmlFailureAsync(context);
                return;
            }

            try
            {
                if (RuntimeBanner.For(context).SoapEnabled
                    && SoapRequestGuardMiddleware.IsExactEndpoint(context.Request.Path))
                {
                    await SoapFaultWriter.WriteAsync(
                        context,
                        StatusCodes.Status500InternalServerError,
                        "INTERNAL_ERROR",
                        "Receiver");
                }
                else if (CorrelationIdMiddleware.IsApiRequest(context))
                {
                    await ProblemResponseWriter.WriteAsync(
                        context,
                        StatusCodes.Status500InternalServerError,
                        "INTERNAL_ERROR");
                }
                else
                {
                    await CorrelationIdMiddleware.DispatchHtmlErrorAsync(
                        context,
                        next,
                        StatusCodes.Status500InternalServerError,
                        "An unexpected error occurred");
                }
            }
            catch (Exception)
            {
                if (RuntimeBanner.For(context).SoapEnabled
                    && SoapRequestGuardMiddleware.IsExactEndpoint(context.Request.Path))
                {
                    await WriteMinimalSoapFailureAsync(context);
                }
                else
                {
                    await WriteMinimalHtmlFailureAsync(context);
                }
            }
        }
    }

    private static async Task WriteMinimalHtmlFailureAsync(HttpContext context)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers[CorrelationIdMiddleware.HeaderName] =
            CorrelationIdMiddleware.GetCorrelationId(context);
        var correlationId = System.Net.WebUtility.HtmlEncode(CorrelationIdMiddleware.GetCorrelationId(context));
        await context.Response.WriteAsync(
            "<!doctype html><html><body><h1>An unexpected error occurred</h1>"
            + $"<p>{System.Net.WebUtility.HtmlEncode(RuntimeBanner.For(context).Text)}</p>"
            + $"<p>{correlationId}</p></body></html>",
            context.RequestAborted);
    }

    private static async Task WriteMinimalSoapFailureAsync(HttpContext context)
    {
        try
        {
            await SoapFaultWriter.WriteAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "INTERNAL_ERROR",
                "Receiver");
        }
        catch (Exception)
        {
            // The connection may already be unavailable; never expose exception data.
        }
    }
}
