using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Application.Documents;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Customers;

/// <summary>
/// Customer registry. Customer-level access: holders of projects.access.all see every customer; everyone else sees customers they registered
/// and customers who hold a booking in a project they are assigned to. Ids sent by clients are always re-checked against this scope.
/// </summary>
public class CustomerService(
    IAppDbContext db, IPermissionService permissions, ICurrentUser user, ProjectAccessService access, IAuditService audit, DocumentUploader uploader, IFileStorage storage, TimeProvider clock,
    IValidator<CreateCustomerRequest> createValidator, IValidator<UpdateCustomerRequest> updateValidator, IValidator<AddRelationshipRequest> relationshipValidator)
{
    // ---------- scoping ----------
    internal async Task<IQueryable<Customer>> ScopedAsync(CancellationToken ct)
    {
        var q = db.Customers.AsNoTracking();
        if (await access.HasAllAccessAsync(ct)) return q;
        var projects = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        var me = user.UserName;
        return q.Where(c => c.CreatedBy == me || db.Bookings.Any(b => projects.Any(p => p.Id == b.ProjectId) && b.Customers.Any(bc => bc.CustomerId == c.Id)));
    }

    /// <summary>Throws Forbidden for customers outside the caller's scope (and unknown ids for non-privileged callers).</summary>
    internal async Task EnsureAccessAsync(long customerId, CancellationToken ct)
    {
        if (await (await ScopedAsync(ct)).AnyAsync(c => c.Id == customerId, ct)) return;
        if (!await access.HasAllAccessAsync(ct)) throw new ForbiddenException("You do not have access to this customer.");
        throw new NotFoundException("Customer not found.");
    }

    // ---------- queries ----------
    public async Task<PagedResult<CustomerListItemDto>> ListAsync(CustomerListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.CustomersView, ct);
        var q = await ScopedAsync(ct);
        if (r.Status is { } s) q = q.Where(c => c.Status == s);
        if (!string.IsNullOrWhiteSpace(r.Search))
        {
            var t = r.Search.Trim();
            var digits = new string(t.Where(char.IsDigit).ToArray());
            q = q.Where(c => c.CustomerCode.Contains(t) || c.FullName.Contains(t) || c.Mobile.Contains(t) || (c.Email != null && c.Email.Contains(t)) || (digits.Length >= 5 && c.IdentityNumber.Contains(digits)) || c.IdentityNumber.Contains(t.ToUpper()));
        }
        var total = await q.CountAsync(ct);
        q = (r.SortBy?.ToLowerInvariant(), r.Descending) switch
        {
            ("name", false) => q.OrderBy(c => c.FullName).ThenBy(c => c.Id),
            ("name", true) => q.OrderByDescending(c => c.FullName).ThenBy(c => c.Id),
            ("status", false) => q.OrderBy(c => c.Status).ThenBy(c => c.Id),
            ("status", true) => q.OrderByDescending(c => c.Status).ThenBy(c => c.Id),
            (_, false) => q.OrderBy(c => c.CustomerCode),
            _ => q.OrderByDescending(c => c.CustomerCode),
        };
        var items = await q.Skip((r.Page - 1) * r.PageSize).Take(r.PageSize)
            .Select(c => new CustomerListItemDto(c.Id, c.CustomerCode, c.FullName, c.IdentityType, c.IdentityNumber, c.Mobile, c.Email, c.Status,
                db.CustomerOwnerships.Count(o => o.CustomerId == c.Id && o.IsActive))).ToListAsync(ct);
        return new PagedResult<CustomerListItemDto>(items, r.Page, r.PageSize, total);
    }

    /// <summary>Small typeahead lookup used by the booking wizard (active customers only unless includeInactive).</summary>
    public async Task<IReadOnlyList<CustomerLookupDto>> LookupAsync(string? term, bool activeOnly, CancellationToken ct = default)
    {
        if (user.UserId is not { } uid) throw new ForbiddenException("Authentication required.");
        if (!await permissions.HasPermissionAsync(uid, Permissions.CustomersView, ct) && !await permissions.HasPermissionAsync(uid, Permissions.BookingsCreate, ct)) throw new ForbiddenException();
        var q = await ScopedAsync(ct);
        if (activeOnly) q = q.Where(c => c.Status == CustomerStatus.Active);
        if (!string.IsNullOrWhiteSpace(term))
        {
            var t = term.Trim();
            q = q.Where(c => c.CustomerCode.Contains(t) || c.FullName.Contains(t) || c.Mobile.Contains(t) || c.IdentityNumber.Contains(t));
        }
        return await q.OrderBy(c => c.FullName).Take(20).Select(c => new CustomerLookupDto(c.Id, c.CustomerCode, c.FullName, c.Mobile, c.Status)).ToListAsync(ct);
    }

    public async Task<CustomerDetailDto> GetAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.CustomersView, ct);
        await EnsureAccessAsync(id, ct);
        return await BuildDetailAsync(id, ct);
    }

    private async Task<CustomerDetailDto> BuildDetailAsync(long id, CancellationToken ct)
    {
        var c = await db.Customers.AsNoTracking().Include(x => x.Addresses).Include(x => x.Contacts).Include(x => x.Nominees).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("Customer not found.");
        var rels = await (from r in db.CustomerRelationships.AsNoTracking()
                          where r.CustomerId == id || r.RelatedCustomerId == id
                          join o in db.Customers.AsNoTracking() on (r.CustomerId == id ? r.RelatedCustomerId : r.CustomerId) equals o.Id
                          orderby r.Id
                          select new RelationshipDto(r.Id, o.Id, o.CustomerCode, o.FullName, r.RelationshipType, r.Notes)).ToListAsync(ct);
        var owns = await (from o in db.CustomerOwnerships.AsNoTracking()
                          where o.CustomerId == id
                          join p in db.Properties.AsNoTracking() on o.PropertyId equals p.Id
                          join pr in db.Projects.AsNoTracking() on p.ProjectId equals pr.Id
                          join b in db.Bookings.AsNoTracking() on o.BookingId equals b.Id
                          orderby o.IsActive descending, o.StartedAtUtc descending
                          select new OwnershipDto(o.Id, p.Id, p.PropertyCode, pr.ProjectCode, b.Id, b.BookingNumber, o.SharePercent, o.IsActive, o.StartedAtUtc, o.EndedAtUtc, o.EndReason)).ToListAsync(ct);
        return new CustomerDetailDto(c.Id, c.CustomerCode, c.FullName, c.IdentityType, c.IdentityNumber, c.Mobile, c.Email, c.Address, c.Status, c.Remarks,
            c.Addresses.OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Id).Select(a => new AddressDto(a.AddressType, a.Line1, a.City, a.Country, a.IsPrimary)).ToList(),
            c.Contacts.OrderBy(a => a.Id).Select(a => new ContactDto(a.Name, a.Relation, a.Phone, a.Email)).ToList(),
            c.Nominees.OrderBy(a => a.Id).Select(a => new NomineeDto(a.FullName, a.Relationship, a.IdentityNumber, a.Mobile, a.SharePercent)).ToList(),
            rels, owns, c.RowVersion.ToToken(), c.CreatedAtUtc, c.CreatedBy, c.ModifiedAtUtc, c.ModifiedBy);
    }

    // ---------- commands ----------
    public async Task<CustomerDetailDto> CreateAsync(CreateCustomerRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.CustomersManage, ct);
        await createValidator.ValidateOrThrowAsync(r, ct);
        var idNumber = CustomerRules.NormalizeIdentity(r.IdentityType, r.IdentityNumber);
        if (await db.Customers.AnyAsync(c => c.IdentityType == r.IdentityType && c.IdentityNumber == idNumber, ct))
            throw Invalid("IdentityNumber", $"A customer with this {r.IdentityType} number is already registered.");

        var c = new Customer
        {
            CustomerCode = "NEW-" + Guid.NewGuid().ToString("N")[..20], FullName = r.FullName.Trim(), IdentityType = r.IdentityType, IdentityNumber = idNumber,
            Mobile = r.Mobile.Trim(), Email = N(r.Email), Address = N(r.Address), Remarks = N(r.Remarks), Status = CustomerStatus.Active,
        };
        SetChildren(c, r.Addresses, r.Contacts, r.Nominees);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Customers.Add(c);
        try
        {
            await db.SaveChangesAsync(ct);
            c.CustomerCode = $"CUS-{c.Id:D6}";
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) == true)
        { throw Invalid("IdentityNumber", $"A customer with this {r.IdentityType} number is already registered."); }
        await audit.RecordAsync(new AuditEntry("Customer.Created", "Customer", c.Id.ToString(), NewValues: Snapshot(c)), ct);
        await tx.CommitAsync(ct);
        return await BuildDetailAsync(c.Id, ct);
    }

    public async Task<CustomerDetailDto> UpdateAsync(long id, UpdateCustomerRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.CustomersManage, ct);
        await EnsureAccessAsync(id, ct);
        await updateValidator.ValidateOrThrowAsync(r, ct);
        var c = await db.Customers.Include(x => x.Addresses).Include(x => x.Contacts).Include(x => x.Nominees).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Customer not found.");
        var idNumber = CustomerRules.NormalizeIdentity(r.IdentityType, r.IdentityNumber);
        if (!CustomerRules.CanChangeStatus(c.Status, r.Status)) throw Invalid("Status", $"A customer cannot move from {c.Status} to {r.Status}.");
        if (c.Status != r.Status && string.IsNullOrWhiteSpace(r.StatusReason)) throw Invalid("StatusReason", "A reason is required to change the customer status.");
        var identityChanged = c.IdentityType != r.IdentityType || c.IdentityNumber != idNumber;
        if (identityChanged)
        {
            if (await db.CustomerOwnerships.AnyAsync(o => o.CustomerId == id, ct) || await db.BookingCustomers.AnyAsync(b => b.CustomerId == id, ct))
                throw new DomainException("The identity document of a customer with bookings cannot be changed.");
            if (await db.Customers.AnyAsync(x => x.Id != id && x.IdentityType == r.IdentityType && x.IdentityNumber == idNumber, ct))
                throw Invalid("IdentityNumber", $"A customer with this {r.IdentityType} number is already registered.");
        }
        var before = Snapshot(c);
        db.UseRowVersion(c, r.RowVersion);
        c.FullName = r.FullName.Trim(); c.IdentityType = r.IdentityType; c.IdentityNumber = idNumber; c.Mobile = r.Mobile.Trim();
        c.Email = N(r.Email); c.Address = N(r.Address); c.Remarks = N(r.Remarks);
        var oldStatus = c.Status; c.Status = r.Status;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        ReplaceChildren(c, r.Addresses, r.Contacts, r.Nominees);
        try { await db.SaveWithConcurrencyAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) == true)
        { throw Invalid("IdentityNumber", $"A customer with this {r.IdentityType} number is already registered."); }
        await audit.RecordAsync(new AuditEntry(oldStatus != c.Status ? "Customer.StatusChanged" : "Customer.Updated", "Customer", id.ToString(), before, Snapshot(c), N(r.StatusReason)), ct);
        await tx.CommitAsync(ct);
        return await BuildDetailAsync(id, ct);
    }

    public async Task<CustomerDetailDto> AddRelationshipAsync(long id, AddRelationshipRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.CustomersManage, ct);
        await EnsureAccessAsync(id, ct);
        await relationshipValidator.ValidateOrThrowAsync(r, ct);
        if (r.RelatedCustomerId == id) throw Invalid("RelatedCustomerId", "A customer cannot be related to themselves.");
        await EnsureAccessAsync(r.RelatedCustomerId, ct);
        if (await db.CustomerRelationships.AnyAsync(x => (x.CustomerId == id && x.RelatedCustomerId == r.RelatedCustomerId) || (x.CustomerId == r.RelatedCustomerId && x.RelatedCustomerId == id), ct))
            throw Invalid("RelatedCustomerId", "These customers are already linked.");
        var rel = new CustomerRelationship { CustomerId = id, RelatedCustomerId = r.RelatedCustomerId, RelationshipType = r.RelationshipType, Notes = N(r.Notes) };
        db.CustomerRelationships.Add(rel);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("Customer.RelationshipAdded", "Customer", id.ToString(), NewValues: new { r.RelatedCustomerId, Type = r.RelationshipType.ToString(), r.Notes }), ct);
        return await BuildDetailAsync(id, ct);
    }

    public async Task<CustomerDetailDto> RemoveRelationshipAsync(long id, long relationshipId, string reason, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.CustomersManage, ct);
        await EnsureAccessAsync(id, ct);
        if (string.IsNullOrWhiteSpace(reason)) throw Invalid("Reason", "A reason is required.");
        var rel = await db.CustomerRelationships.FirstOrDefaultAsync(x => x.Id == relationshipId && (x.CustomerId == id || x.RelatedCustomerId == id), ct) ?? throw new NotFoundException("Relationship not found.");
        db.CustomerRelationships.Remove(rel);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("Customer.RelationshipRemoved", "Customer", id.ToString(), new { rel.CustomerId, rel.RelatedCustomerId, Type = rel.RelationshipType.ToString() }, Reason: reason.Trim()), ct);
        return await BuildDetailAsync(id, ct);
    }

    // ---------- documents ----------
    public async Task<IReadOnlyList<CustomerDocumentDto>> ListDocumentsAsync(long customerId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.CustomersView, ct);
        await EnsureAccessAsync(customerId, ct);
        return await db.CustomerDocuments.AsNoTracking().Where(d => d.CustomerId == customerId && !d.IsDeleted).OrderByDescending(d => d.CreatedAtUtc)
            .Select(d => new CustomerDocumentDto(d.Id, d.Title, d.FileName, d.DocumentType.ToString(), d.ContentType, d.SizeBytes, d.CreatedAtUtc, d.CreatedBy)).ToListAsync(ct);
    }

    public async Task<CustomerDocumentDto> UploadDocumentAsync(long customerId, CustomerDocumentType type, string? title, FileUpload file, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.CustomersDocuments, ct);
        await EnsureAccessAsync(customerId, ct);
        if (!Enum.IsDefined(type)) throw Invalid("DocumentType", "Invalid document type.");
        var stored = await uploader.StoreAsync(file, $"customers/{customerId}", ct);
        var t = string.IsNullOrWhiteSpace(title) ? stored.SafeFileName : title.Trim();
        var doc = new CustomerDocument
        {
            CustomerId = customerId, DocumentType = type, Title = t[..Math.Min(t.Length, 200)], FileName = stored.SafeFileName, StorageKey = stored.File.Key,
            ContentType = stored.ContentType, SizeBytes = stored.File.SizeBytes, Sha256 = stored.File.Sha256,
        };
        db.CustomerDocuments.Add(doc);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("CustomerDocument.Uploaded", "CustomerDocument", doc.Id.ToString(), NewValues: new { customerId, doc.Title, doc.FileName, doc.SizeBytes, doc.Sha256, Type = type.ToString() }), ct);
        return new CustomerDocumentDto(doc.Id, doc.Title, doc.FileName, doc.DocumentType.ToString(), doc.ContentType, doc.SizeBytes, doc.CreatedAtUtc, doc.CreatedBy);
    }

    public async Task<DownloadedFile> DownloadDocumentAsync(long customerId, long documentId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.CustomersView, ct);
        await EnsureAccessAsync(customerId, ct);
        var d = await db.CustomerDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == documentId && x.CustomerId == customerId && !x.IsDeleted, ct) ?? throw new NotFoundException("Document not found.");
        return new DownloadedFile(await storage.OpenReadAsync(d.StorageKey, ct), d.FileName, d.ContentType);
    }

    public async Task DeleteDocumentAsync(long customerId, long documentId, string reason, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.CustomersDocuments, ct);
        await EnsureAccessAsync(customerId, ct);
        var d = await db.CustomerDocuments.FirstOrDefaultAsync(x => x.Id == documentId && x.CustomerId == customerId && !x.IsDeleted, ct) ?? throw new NotFoundException("Document not found.");
        d.MarkDeleted(clock.GetUtcNow().UtcDateTime, user.UserName ?? "unknown", reason);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("CustomerDocument.Removed", "CustomerDocument", d.Id.ToString(), new { customerId, d.Title, d.FileName }, Reason: reason.Trim()), ct);
    }

    // ---------- helpers ----------
    private static void SetChildren(Customer c, IReadOnlyList<AddressDto>? addresses, IReadOnlyList<ContactDto>? contacts, IReadOnlyList<NomineeDto>? nominees)
    {
        foreach (var a in addresses ?? []) c.Addresses.Add(new CustomerAddress { AddressType = a.AddressType, Line1 = a.Line1.Trim(), City = N(a.City), Country = N(a.Country), IsPrimary = a.IsPrimary });
        foreach (var x in contacts ?? []) c.Contacts.Add(new CustomerContact { Name = x.Name.Trim(), Relation = N(x.Relation), Phone = N(x.Phone), Email = N(x.Email) });
        foreach (var n in nominees ?? []) c.Nominees.Add(new CustomerNominee { FullName = n.FullName.Trim(), Relationship = n.Relationship.Trim(), IdentityNumber = N(n.IdentityNumber), Mobile = N(n.Mobile), SharePercent = n.SharePercent });
    }

    private void ReplaceChildren(Customer c, IReadOnlyList<AddressDto>? addresses, IReadOnlyList<ContactDto>? contacts, IReadOnlyList<NomineeDto>? nominees)
    {
        if (addresses is not null) { db.CustomerAddresses.RemoveRange(c.Addresses); c.Addresses.Clear(); foreach (var a in addresses) c.Addresses.Add(new CustomerAddress { AddressType = a.AddressType, Line1 = a.Line1.Trim(), City = N(a.City), Country = N(a.Country), IsPrimary = a.IsPrimary }); }
        if (contacts is not null) { db.CustomerContacts.RemoveRange(c.Contacts); c.Contacts.Clear(); foreach (var x in contacts) c.Contacts.Add(new CustomerContact { Name = x.Name.Trim(), Relation = N(x.Relation), Phone = N(x.Phone), Email = N(x.Email) }); }
        if (nominees is not null) { db.CustomerNominees.RemoveRange(c.Nominees); c.Nominees.Clear(); foreach (var n in nominees) c.Nominees.Add(new CustomerNominee { FullName = n.FullName.Trim(), Relationship = n.Relationship.Trim(), IdentityNumber = N(n.IdentityNumber), Mobile = N(n.Mobile), SharePercent = n.SharePercent }); }
    }

    private static object Snapshot(Customer c) => new
    {
        c.CustomerCode, c.FullName, IdentityType = c.IdentityType.ToString(), c.IdentityNumber, c.Mobile, c.Email, c.Address, Status = c.Status.ToString(), c.Remarks,
        Addresses = c.Addresses.Select(a => $"{a.AddressType}:{a.Line1}|{a.City}|{a.IsPrimary}").OrderBy(x => x).ToArray(),
        Contacts = c.Contacts.Select(a => $"{a.Name}|{a.Phone}|{a.Email}").OrderBy(x => x).ToArray(),
        Nominees = c.Nominees.Select(a => $"{a.FullName}|{a.Relationship}|{a.SharePercent}").OrderBy(x => x).ToArray(),
    };

    private static string? N(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
