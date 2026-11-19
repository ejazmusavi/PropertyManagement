using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Infrastructure.Persistence.Bootstrap;

/// <summary>
/// Creates the first administrator (and the company record). Refuses to run when an administrator already exists,
/// and the password is validated against the Identity password policy. The password is never stored in project files.
/// </summary>
public class AdminBootstrapper(AppDbContext db, UserManager<ApplicationUser> users, DatabaseInitializer initializer, IAuditService audit, TimeProvider clock)
{
    public async Task<Result> CreateInitialAdministratorAsync(string email, string fullName, string password, string companyName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) return Result.Failure("bootstrap.email", "A valid email is required.");
        if (string.IsNullOrWhiteSpace(fullName)) return Result.Failure("bootstrap.name", "Full name is required.");
        if (string.IsNullOrWhiteSpace(companyName)) return Result.Failure("bootstrap.company", "Company name is required.");

        await initializer.SyncSystemDataAsync(ct);

        var adminRoleId = await db.Roles.Where(r => r.Name == RoleNames.Administrator).Select(r => r.Id).SingleAsync(ct);
        if (await db.UserRoles.AnyAsync(ur => ur.RoleId == adminRoleId, ct))
            return Result.Failure("bootstrap.exists", "An administrator already exists. Initial setup can only run once.");

        var user = new ApplicationUser
        {
            UserName = email.Trim(),
            Email = email.Trim(),
            EmailConfirmed = true,
            FullName = fullName.Trim(),
            IsActive = true,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
            return Result.Failure("bootstrap.user", string.Join(" ", created.Errors.Select(e => e.Description)));

        var inRole = await users.AddToRoleAsync(user, RoleNames.Administrator);
        if (!inRole.Succeeded)
        {
            await users.DeleteAsync(user);
            return Result.Failure("bootstrap.role", string.Join(" ", inRole.Errors.Select(e => e.Description)));
        }

        if (!await db.Companies.AnyAsync(ct))
        {
            db.Companies.Add(new Company(companyName));
            await db.SaveChangesAsync(ct);
        }

        await audit.RecordAsync(new AuditEntry("Admin.Bootstrapped", "User", user.Id.ToString(),
            NewValues: new { user.Email, Role = RoleNames.Administrator }, UserId: user.Id, UserName: user.Email), ct);
        return Result.Success();
    }
}
