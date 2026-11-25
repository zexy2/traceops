using Microsoft.AspNetCore.Mvc;
using TraceOps.Web.Middleware;
using TraceOps.Web.Models;

namespace TraceOps.Web.Controllers;

[Route("__traceops/error")]
public sealed class ErrorPageController : Controller
{
    [HttpGet]
    public IActionResult Show()
    {
        var page = HttpContext.Items["TraceOps.ErrorPage"] as ErrorPage
            ?? new ErrorPage(
                StatusCodes.Status404NotFound,
                "Page not found",
                CorrelationIdMiddleware.GetCorrelationId(HttpContext));
        Response.StatusCode = page.StatusCode;
        return View("~/Views/Shared/Error.cshtml", page);
    }
}
