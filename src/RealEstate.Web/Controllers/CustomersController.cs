using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Documents;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

[HasPermission(Permissions.CustomersView)]
public class CustomersController(CustomerService customers) : Controller
{
    public async Task<IActionResult> Index(CustomerListRequest filter, bool desc = false, CancellationToken ct = default)
    {
        filter.PageSize = 20;
        filter.Descending = desc;
        return View(new CustomerListViewModel { Filter = filter, Descending = desc, Customers = await customers.ListAsync(filter, ct) });
    }

    public async Task<IActionResult> Details(long id, CancellationToken ct) =>
        View(new CustomerDetailsViewModel { Customer = await customers.GetAsync(id, ct), Documents = await customers.ListDocumentsAsync(id, ct) });

    // ----- create / edit -----
    [HttpGet, HasPermission(Permissions.CustomersManage)]
    public IActionResult Create() => View("Form", new CustomerFormViewModel { Addresses = [new AddressInput { IsPrimary = true }] });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.CustomersManage)]
    public async Task<IActionResult> Create(CustomerFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var created = await customers.CreateAsync(new CreateCustomerRequest(m.FullName, m.IdentityType, m.IdentityNumber, m.Mobile, m.Email, m.Address, m.Remarks, m.ToAddresses(), m.ToContacts(), m.ToNominees()), ct);
                TempData["Success"] = $"Customer {created.CustomerCode} registered.";
                return RedirectToAction(nameof(Details), new { id = created.Id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View("Form", m);
    }

    [HttpGet, HasPermission(Permissions.CustomersManage)]
    public async Task<IActionResult> Edit(long id, CancellationToken ct)
    {
        var c = await customers.GetAsync(id, ct);
        return View("Form", new CustomerFormViewModel
        {
            Id = c.Id, RowVersion = c.RowVersion, CustomerCode = c.CustomerCode, FullName = c.FullName, IdentityType = c.IdentityType, IdentityNumber = c.IdentityNumber, Mobile = c.Mobile, Email = c.Email,
            Address = c.Address, Remarks = c.Remarks, Status = c.Status, OriginalStatus = c.Status,
            Addresses = c.Addresses.Select(a => new AddressInput { AddressType = a.AddressType, Line1 = a.Line1, City = a.City, Country = a.Country, IsPrimary = a.IsPrimary }).ToList(),
            Contacts = c.Contacts.Select(a => new ContactInput { Name = a.Name, Relation = a.Relation, Phone = a.Phone, Email = a.Email }).ToList(),
            Nominees = c.Nominees.Select(a => new NomineeInput { FullName = a.FullName, Relationship = a.Relationship, IdentityNumber = a.IdentityNumber, Mobile = a.Mobile, SharePercent = a.SharePercent }).ToList(),
        });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.CustomersManage)]
    public async Task<IActionResult> Edit(long id, CustomerFormViewModel m, CancellationToken ct)
    {
        m.Id = id;
        if (ModelState.IsValid)
        {
            try
            {
                await customers.UpdateAsync(id, new UpdateCustomerRequest(m.FullName, m.IdentityType, m.IdentityNumber, m.Mobile, m.Email, m.Address, m.Remarks, m.Status, m.StatusReason,
                    m.ToAddresses(), m.ToContacts(), m.ToNominees(), m.RowVersion), ct);
                TempData["Success"] = "Customer saved.";
                return RedirectToAction(nameof(Details), new { id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View("Form", m);
    }

    // ----- relationships -----
    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.CustomersManage)]
    public async Task<IActionResult> AddRelationship(long id, long relatedCustomerId, CustomerRelationshipType relationshipType, string? notes, CancellationToken ct)
    {
        try { await customers.AddRelationshipAsync(id, new AddRelationshipRequest(relatedCustomerId, relationshipType, notes), ct); TempData["Success"] = "Relationship added."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.CustomersManage)]
    public async Task<IActionResult> RemoveRelationship(long id, long relationshipId, string reason, CancellationToken ct)
    {
        try { await customers.RemoveRelationshipAsync(id, relationshipId, reason ?? "", ct); TempData["Success"] = "Relationship removed."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    // ----- documents -----
    public async Task<IActionResult> Documents(long id, CancellationToken ct) =>
        View(new CustomerDetailsViewModel { Customer = await customers.GetAsync(id, ct), Documents = await customers.ListDocumentsAsync(id, ct) });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.CustomersDocuments)]
    public async Task<IActionResult> UploadDocument(long id, IFormFile? file, CustomerDocumentType type, string? title, CancellationToken ct)
    {
        if (file is null || file.Length == 0) { TempData["Error"] = "Choose a file to upload."; return RedirectToAction(nameof(Documents), new { id }); }
        try
        {
            await using var s = file.OpenReadStream();
            await customers.UploadDocumentAsync(id, type, title, new FileUpload(file.FileName, file.ContentType, file.Length, s), ct);
            TempData["Success"] = "Document uploaded.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Documents), new { id });
    }

    public async Task<IActionResult> DownloadDocument(long id, long documentId, CancellationToken ct)
    {
        var f = await customers.DownloadDocumentAsync(id, documentId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.CustomersDocuments)]
    public async Task<IActionResult> DeleteDocument(long id, long documentId, string reason, CancellationToken ct)
    {
        try { await customers.DeleteDocumentAsync(id, documentId, reason ?? "", ct); TempData["Success"] = "Document removed."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Documents), new { id });
    }

    private static bool Soft(Exception ex) => ex is DomainException && ex is not ForbiddenException && ex is not NotFoundException;
    private static string Message(Exception ex) => ex is RequestValidationException v ? string.Join(" ", v.Errors.SelectMany(e => e.Value)) : ex.Message;
}
