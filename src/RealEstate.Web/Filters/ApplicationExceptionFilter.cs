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
