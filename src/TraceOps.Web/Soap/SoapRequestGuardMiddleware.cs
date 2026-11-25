using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using TraceOps.Web.Middleware;

namespace TraceOps.Web.Soap;

internal sealed class SoapRequestGuardMiddleware(
    RequestDelegate next,
    bool enabled,
    int port)
{
    public const string EndpointPath = "/soap/shipments";
    private const int MaximumBodyBytes = RequestBodyLimitMiddleware.MaximumBodyBytes;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsEndpointOrChild(context.Request.Path))
        {
            await next(context);
            return;
        }

        if (!enabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!string.Equals(context.Request.Path.Value, EndpointPath, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method)
            && context.Request.Query.ContainsKey("wsdl"))
        {
            await SoapWsdlDocument.WriteAsync(context, port);
            return;
        }

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!TryReadSoapAction(context.Request.ContentType, out var action, out var supportedMediaType))
        {
            await SoapFaultWriter.WriteAsync(
                context,
                StatusCodes.Status415UnsupportedMediaType,
                "UNSUPPORTED_MEDIA_TYPE",
                "Sender");
            return;
        }

        if (!supportedMediaType)
        {
            await SoapFaultWriter.WriteAsync(
                context,
                StatusCodes.Status415UnsupportedMediaType,
                "UNSUPPORTED_MEDIA_TYPE",
                "Sender");
            return;
        }

        var bytes = await ReadBoundedBodyAsync(context);
        if (bytes is null)
        {
            await SoapFaultWriter.WriteAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "PAYLOAD_TOO_LARGE",
                "Sender");
            return;
        }

        ValidatedSoapRequest request;
        try
        {
            request = SoapRequestValidator.Validate(bytes, action);
        }
        catch (SoapRequestValidationException exception)
        {
            await SoapFaultWriter.WriteAsync(
                context,
                exception.StatusCode,
                exception.DetailCode,
                exception.EnvelopeCode);
            return;
        }

        context.Request.Body = new MemoryStream(bytes, writable: false);
        context.Request.Headers.Remove("SOAPAction");
        context.Items["TraceOps.Soap.Operation"] = request.OperationName;
        var previousContext = SoapRequestContext.Current;
        SoapRequestContext.Current = context;
        try
        {
            await next(context);
        }
        finally
        {
            SoapRequestContext.Current = previousContext;
        }
    }

    public static bool IsExactEndpoint(PathString path) =>
        string.Equals(path.Value, EndpointPath, StringComparison.Ordinal);

    private static bool IsEndpointOrChild(PathString path) =>
        path.StartsWithSegments(
            EndpointPath,
            StringComparison.OrdinalIgnoreCase,
            out _);

    private static bool TryReadSoapAction(
        string? rawContentType,
        out string action,
        out bool supportedMediaType)
    {
        action = string.Empty;
        supportedMediaType = false;
        if (!MediaTypeHeaderValue.TryParse(rawContentType, out var parsed)
            || !string.Equals(
                parsed.MediaType,
                "application/soap+xml",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var charsets = parsed.Parameters
            .Where(parameter => string.Equals(parameter.Name, "charset", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (charsets.Length != 1
            || !string.Equals(Unquote(charsets[0].Value), "utf-8", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        supportedMediaType = true;
        var actions = parsed.Parameters
            .Where(parameter => string.Equals(parameter.Name, "action", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (actions.Length == 1)
        {
            action = Unquote(actions[0].Value);
        }

        return true;
    }

    private static string Unquote(string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }

    private static async Task<byte[]?> ReadBoundedBodyAsync(HttpContext context)
    {
        await using var buffered = new MemoryStream(capacity: MaximumBodyBytes);
        var chunk = new byte[4096];
        var total = 0;

        while (true)
        {
            var readSize = Math.Min(chunk.Length, MaximumBodyBytes + 1 - total);
            var read = await context.Request.Body.ReadAsync(
                chunk.AsMemory(0, readSize),
                context.RequestAborted);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaximumBodyBytes)
            {
                return null;
            }

            await buffered.WriteAsync(chunk.AsMemory(0, read), context.RequestAborted);
        }

        return buffered.ToArray();
    }
}
