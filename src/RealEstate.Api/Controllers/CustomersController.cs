using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Api.Controllers;

[ApiController, Authorize]
[Route("api/v1/customers")]
public class CustomersController(CustomerService customers) : ControllerBase
{
    /// <summary>Lists the customers the caller may see (own registrations and customers of assigned projects; all for holders of projects.access.all).</summary>
    [HttpGet, HasPermission(Permissions.CustomersView)]
    public async Task<ActionResult<PagedResult<CustomerListItemDto>>> List([FromQuery] CustomerListRequest request, CancellationToken ct) => await customers.ListAsync(request, ct);

    [HttpGet("lookup"), Authorize]
    public async Task<ActionResult<IReadOnlyList<CustomerLookupDto>>> Lookup([FromQuery] string? term, [FromQuery] bool activeOnly = true, CancellationToken ct = default) => Ok(await customers.LookupAsync(term, activeOnly, ct));

    [HttpPost, HasPermission(Permissions.CustomersManage)]
    [ProducesResponseType<CustomerDetailDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateCustomerRequest request, CancellationToken ct)
    {
        var created = await customers.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet("{id:long}"), HasPermission(Permissions.CustomersView)]
    public async Task<ActionResult<CustomerDetailDto>> Get(long id, CancellationToken ct) => await customers.GetAsync(id, ct);

    [HttpPut("{id:long}"), HasPermission(Permissions.CustomersManage)]
    public async Task<ActionResult<CustomerDetailDto>> Update(long id, UpdateCustomerRequest request, CancellationToken ct) => await customers.UpdateAsync(id, request, ct);

    [HttpPost("{id:long}/relationships"), HasPermission(Permissions.CustomersManage)]
    public async Task<ActionResult<CustomerDetailDto>> AddRelationship(long id, AddRelationshipRequest request, CancellationToken ct) => await customers.AddRelationshipAsync(id, request, ct);

    [HttpDelete("{id:long}/relationships/{relationshipId:long}"), HasPermission(Permissions.CustomersManage)]
    public async Task<ActionResult<CustomerDetailDto>> RemoveRelationship(long id, long relationshipId, [FromQuery] string reason, CancellationToken ct) => await customers.RemoveRelationshipAsync(id, relationshipId, reason, ct);

    // ----- documents -----
    [HttpGet("{id:long}/documents"), HasPermission(Permissions.CustomersView)]
    public async Task<ActionResult<IReadOnlyList<CustomerDocumentDto>>> Documents(long id, CancellationToken ct) => Ok(await customers.ListDocumentsAsync(id, ct));

    [HttpPost("{id:long}/documents"), HasPermission(Permissions.CustomersDocuments), RequestSizeLimit(110_000_000)]
    public async Task<ActionResult<CustomerDocumentDto>> Upload(long id, IFormFile file, [FromForm] CustomerDocumentType type = CustomerDocumentType.Other, [FromForm] string? title = null, CancellationToken ct = default) =>
        await customers.UploadDocumentAsync(id, type, title, file.ToUpload(), ct);

    [HttpGet("{id:long}/documents/{documentId:long}/download"), HasPermission(Permissions.CustomersView)]
    public async Task<IActionResult> Download(long id, long documentId, CancellationToken ct)
    {
        var f = await customers.DownloadDocumentAsync(id, documentId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }

    [HttpDelete("{id:long}/documents/{documentId:long}"), HasPermission(Permissions.CustomersDocuments)]
    public async Task<IActionResult> DeleteDocument(long id, long documentId, [FromQuery] string reason, CancellationToken ct)
    {
        await customers.DeleteDocumentAsync(id, documentId, reason, ct);
        return NoContent();
    }
}
