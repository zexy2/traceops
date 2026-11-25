namespace TraceOps.Web.Middleware;

public sealed class ErrorPageMiddleware(RequestDelegate next)
{
    internal const string ErrorPageItem = "TraceOps.ErrorPage";

    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

        if (context.Response.StatusCode == StatusCodes.Status404NotFound
            && !context.Response.HasStarted
            && !CorrelationIdMiddleware.IsApiRequest(context)
            && !context.Items.ContainsKey(ErrorPageItem))
        {
            await CorrelationIdMiddleware.DispatchHtmlErrorAsync(
                context,
                next,
                StatusCodes.Status404NotFound,
                "Page not found");
        }
    }
}
