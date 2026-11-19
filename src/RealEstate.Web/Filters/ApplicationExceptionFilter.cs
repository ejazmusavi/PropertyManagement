using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;

namespace RealEstate.Web.Filters;

/// <summary>Turns application exceptions into friendly responses. Validation errors are handled per action (ModelState).</summary>
public class ApplicationExceptionFilter(ILogger<ApplicationExceptionFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        switch (context.Exception)
        {
            case ForbiddenException:
                context.Result = new ForbidResult();
                break;
            case NotFoundException:
                context.Result = new NotFoundResult();
                break;
            case DomainException d when d is not RequestValidationException:
                logger.LogWarning("Business rule violation: {Message}", d.Message);
                var temp = context.HttpContext.RequestServices.GetRequiredService<ITempDataDictionaryFactory>().GetTempData(context.HttpContext);
                temp["Error"] = d.Message;
                context.Result = new RedirectToActionResult("Index", "Home", null);
                break;
            default:
                return;
        }
        context.ExceptionHandled = true;
    }
}

public static class ModelStateExtensions
{
    public static void AddErrors(this Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary state, RequestValidationException ex, string? prefix = null)
    {
        foreach (var (key, messages) in ex.Errors)
            foreach (var m in messages) state.AddModelError(string.IsNullOrEmpty(key) ? string.Empty : key, m);
    }
}

public static class ControllerErrorExtensions
{
    /// <summary>Shows validation and business-rule errors inline. Forbidden/not-found are rethrown for the global filter. Returns false when not handled.</summary>
    public static bool TryAddError(this Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary state, Exception ex)
    {
        switch (ex)
        {
            case RequestValidationException v: state.AddErrors(v); return true;
            case ForbiddenException or NotFoundException: return false;
            case DomainException d: state.AddModelError(string.Empty, d.Message); return true;
            default: return false;
        }
    }
}
