using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace MyTranslator.Api;

public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is ApiRequestException requestException)
        {
            httpContext.Response.StatusCode = requestException.StatusCode;
            return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = ApiProblem.CreateDetails(
                    httpContext,
                    requestException.Code,
                    requestException.Message,
                    requestException.StatusCode,
                    requestException.Errors)
            });
        }

        logger.LogError(exception, "Unhandled API exception for {Path}", httpContext.Request.Path);
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred",
                Type = "https://httpstatuses.com/500",
                Instance = httpContext.Request.Path
            },
            Exception = exception
        });
    }
}
