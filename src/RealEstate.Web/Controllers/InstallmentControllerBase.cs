using RealEstate.Domain.Common;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;

namespace RealEstate.Web.Controllers;

/// <summary>Shared helpers for the Phase 5 MVC controllers (thin: bind, call the service, map).</summary>
public abstract class InstallmentControllerBase : Controller
{
    /// <summary>Business-rule failures shown as a toast; forbidden/not-found go to the global filter.</summary>
    protected static bool Soft(Exception ex) => ex is DomainException && ex is not ForbiddenException && ex is not NotFoundException;

    protected static string Message(Exception ex) => ex is RequestValidationException v ? string.Join(" ", v.Errors.SelectMany(e => e.Value)) : ex.Message;

    /// <summary>Runs a lookup that may be outside the user's permissions (e.g. inventory lists); returns the fallback instead of failing the page.</summary>
    protected static async Task<T> OrAsync<T>(Func<Task<T>> load, T fallback)
    {
        try { return await load(); }
        catch (ForbiddenException) { return fallback; }
    }
}
