using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Companies;
using RealEstate.Domain.Common;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Api.Controllers;

[ApiController, Authorize]
[Route("api/v1/company")]
public class CompanyController(CompanyService company) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.CompanyView)]
    public async Task<ActionResult<CompanyDto>> Get(CancellationToken ct) => await company.GetAsync(ct);

    [HttpPut, HasPermission(Permissions.CompanyManage)]
    public async Task<ActionResult<CompanyDto>> Update(UpdateCompanyRequest request, CancellationToken ct) => await company.UpdateAsync(request, ct);

    [HttpPut("logo"), HasPermission(Permissions.CompanyManage)]
    public async Task<IActionResult> SetLogo(IFormFile file, CancellationToken ct)
    {
        await company.SetLogoAsync(file.ToUpload(), ct);
        return NoContent();
    }

    [HttpGet("logo")]
    public async Task<IActionResult> Logo(CancellationToken ct)
    {
        var f = await company.GetLogoAsync(ct);
        return f is null ? NotFound() : File(f.Content, f.ContentType);
    }
}
