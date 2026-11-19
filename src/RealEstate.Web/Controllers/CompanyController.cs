using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Companies;
using RealEstate.Application.Documents;
using RealEstate.Domain.Common;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

[HasPermission(Permissions.CompanyView)]
public class CompanyController(CompanyService company) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct) => View(Map(await company.GetAsync(ct)));

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.CompanyManage)]
    public async Task<IActionResult> Index(CompanyViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await company.UpdateAsync(new UpdateCompanyRequest(m.Name, m.LegalName, m.TaxNumber, m.Email, m.Phone, m.Website, m.Address, m.City, m.Country,
                    m.PostalCode, m.CurrencyCode, m.CurrencySymbol, m.CurrencyDecimalPlaces, m.RoundingMode, m.FiscalYearStartMonth, m.TimeZoneId,
                    m.MaxUploadSizeMb, m.AllowedFileExtensions, m.RowVersion), ct);
                TempData["Success"] = "Company profile saved.";
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.HasLogo = (await company.GetAsync(ct)).HasLogo;
        return View(m);
    }

    [HttpGet]
    public async Task<IActionResult> Logo(CancellationToken ct)
    {
        var f = await company.GetLogoAsync(ct);
        return f is null ? NotFound() : File(f.Content, f.ContentType);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.CompanyManage)]
    public async Task<IActionResult> UploadLogo(IFormFile? logo, CancellationToken ct)
    {
        if (logo is null || logo.Length == 0) { TempData["Error"] = "Choose an image file first."; return RedirectToAction(nameof(Index)); }
        try
        {
            await using var s = logo.OpenReadStream();
            await company.SetLogoAsync(new FileUpload(logo.FileName, logo.ContentType, logo.Length, s), ct);
            TempData["Success"] = "Logo updated.";
        }
        catch (Exception ex) when (ex is Application.Common.RequestValidationException or DomainException && ex is not Application.Common.ForbiddenException) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.CompanyManage)]
    public async Task<IActionResult> RemoveLogo(CancellationToken ct)
    {
        await company.RemoveLogoAsync(ct);
        TempData["Success"] = "Logo removed.";
        return RedirectToAction(nameof(Index));
    }

    private static CompanyViewModel Map(CompanyDto c) => new()
    {
        Name = c.Name, LegalName = c.LegalName, TaxNumber = c.TaxNumber, Email = c.Email, Phone = c.Phone, Website = c.Website, Address = c.Address,
        City = c.City, Country = c.Country, PostalCode = c.PostalCode, CurrencyCode = c.CurrencyCode, CurrencySymbol = c.CurrencySymbol,
        CurrencyDecimalPlaces = c.CurrencyDecimalPlaces, RoundingMode = c.RoundingMode, FiscalYearStartMonth = c.FiscalYearStartMonth, TimeZoneId = c.TimeZoneId,
        MaxUploadSizeMb = c.MaxUploadSizeMb, AllowedFileExtensions = c.AllowedFileExtensions, RowVersion = c.RowVersion, HasLogo = c.HasLogo,
    };
}
