using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;

namespace RealEstate.Api.Middleware;

/// <summary>Maps exceptions to RFC 7807 Problem Details. Unexpected errors are logged and never leaked.</summary>
public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception ex, CancellationToken ct)
    {
        ProblemDetails problem;
        switch (ex)
        {
            case RequestValidationException v:
                problem = new ValidationProblemDetails(v.Errors) { Status = StatusCodes.Status400BadRequest, Title = "Validation failed" };
                break;
            case ForbiddenException f:
                problem = new ProblemDetails { Status = StatusCodes.Status403Forbidden, Title = "Forbidden", Detail = f.Message };
                break;
            case NotFoundException n:
                problem = new ProblemDetails { Status = StatusCodes.Status404NotFound, Title = "Not found", Detail = n.Message };
                break;
            case DomainException d:
                problem = new ProblemDetails { Status = StatusCodes.Status422UnprocessableEntity, Title = "Business rule violated", Detail = d.Message };
                break;
            case OperationCanceledException when http.RequestAborted.IsCancellationRequested:
                return true;
            default:
                logger.LogError(ex, "Unhandled exception for {Method} {Path}", http.Request.Method, http.Request.Path);
                problem = new ProblemDetails { Status = StatusCodes.Status500InternalServerError, Title = "An unexpected error occurred" };
                break;
        }

        http.Response.StatusCode = problem.Status!.Value;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem, Exception = ex });
    }
}
