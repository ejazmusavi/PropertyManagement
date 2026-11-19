# Architecture Decisions

Approved, fixed requirements copied verbatim from the original specification (Part 1 and section 2.2). Do not change without explicit user approval. Record any new decision at the bottom in the same style.

# PART 1 – APPROVED BUSINESS DECISIONS

The following decisions are confirmed and must be treated as fixed architectural requirements.

| Decision | Approved requirement |
|---|---|
| Company structure | One company managing multiple projects |
| Accounting | Complete double-entry accounting |
| Project area | Total area and detailed land distribution |
| Property types | Plots, houses, apartments, shops and commercial units |
| Payments | Office collection and future online payment integration |

## 1.1 Business hierarchy

The application follows this hierarchy:

Company
→ Projects
→ Land and project planning
→ Blocks / Sectors
→ Streets
→ Properties
→ Customers
→ Bookings
→ Agreements
→ Installments
→ Payments
→ Accounting

A single company may operate multiple projects in different cities.

Example:

- Serene Rawalpindi – 200 Kanals.
- Serene Skardu – 30 Kanals.
- Future projects – unlimited.

Each project has independent inventory, sales, expenses, customer agreements and project-level financial reporting.

Company-wide accounting consolidates financial information from all projects.

## 1.2 Important accounting assumption

Implement full double-entry accrual accounting.

However, do not assume that every booking represents immediately recognized accounting revenue.

The accounting treatment of property sales, advances, customer receivables, development charges, taxes, land acquisition and construction costs must be configurable through approved posting rules.

Support both:

- Contractual customer account balances.
- Actual accounting balances.

These are related but must not be treated as interchangeable.

---

## 2.2 Architecture rules

1. Use Clean Architecture.
2. Use a Modular Monolith.
3. Do not introduce microservices.
4. Use EF Core Code First.
5. Use MySQL.
6. Use Application services for business operations.
7. MVC and API must share application services.
8. Do not duplicate business logic in controllers.
9. Use explicit database transactions for financial operations.
10. Use permission-based authorization.
11. Maintain audit trails.
12. Use soft deletion only where business rules allow it.
13. Never physically delete posted financial transactions.
14. Use database constraints and concurrency protection.
15. Use server-side pagination for large datasets.

---

## New decisions log

_None yet. Format: date, phase, decision, reason._

| Date | Phase | Decision | Reason |
|---|---|---|---|
| 2026-11-19 | 0 | Target `net10.0` but use EF Core **9.0.23** + **Pomelo.EntityFrameworkCore.MySql 9.0.0** (MySQL 8.4 server). Package `MySqlConnector` pinned to 2.6.2. | Pomelo has no EF Core 10 release; EF 9 runs on the .NET 10 runtime. Pin fixes advisory GHSA-473q-m89c-ghf8 in the transitive 2.4.0. Revisit when Pomelo ships EF 10. |
| 2026-11-19 | 0 | Central package management (`Directory.Packages.props`) and shared `Directory.Build.props` (nullable, warnings as errors). | One place for versions. |
| 2026-11-19 | 0 | `ApplicationUser`/`ApplicationRole` (ASP.NET Identity) live in Infrastructure; Domain stays dependency-free. Identity's join entity is the `UserRole` table (`UserRoles`). `Permission`, `RolePermission`, `UserProject` are Domain entities linked by Guid ids. Identity tables renamed `Users`, `Roles`, `UserRoles`, ... | Architecture rule: Domain depends on nothing. |
| 2026-11-19 | 0 | Added `RefreshToken` entity (not in the phase list). API refresh tokens are opaque, stored as SHA-256 hashes, rotated on every use, with family revocation on reuse. Access tokens are HS256 JWTs (15 min), refresh 14 days. | Needed for `/auth/refresh` and `/auth/logout`; spec leaves token format open (spec-issues #8). |
| 2026-11-19 | 0 | Permission checks are evaluated against the database on every request (`[HasPermission]` policy + service-level `EnsureAsync`), not embedded in tokens. Inactive users hold no permissions. | Immediate revocation; enforcement in backend services as required. |
| 2026-11-19 | 0 | Phase 0 = schema + auth + permission catalog + one seeded `Administrator` role holding every permission (spec-issues #3). Roles list/management UI is Phase 1. | Resolves spec-issues #3 as assumed there. |
| 2026-11-19 | 0 | Initial administrator is created by the CLI `create-admin` (Web project) which refuses to run once an administrator exists; password comes from `REALESTATE_ADMIN_PASSWORD` or a hidden prompt, never an argument or file. | "Created securely" with no credentials in source. |
| 2026-11-19 | 0 | Swagger: built-in `Microsoft.AspNetCore.OpenApi` document at `/openapi/v1.json` plus Swashbuckle SwaggerUI at `/swagger`, enabled by `Swagger:Enabled` (on in Development). | Swashbuckle's generator conflicts with the OpenApi 2.x types used by .NET 10. |
| 2026-11-19 | 0 | Validation with FluentValidation in Application services; `RequestValidationException` -> HTTP 400 ValidationProblemDetails (API) / ModelState (MVC). | Server-side validation shared by MVC and API. |
| 2026-11-19 | 0 | MVC uses cookie authentication; API uses JWT bearer only. Login/refresh endpoints rate-limited per IP; Identity lockout (5 attempts / 15 min). | Per architecture.md. |
| 2026-11-19 | 0 | Tests use a real MySQL server (throw-away `realestate_test_*` databases) configured via env `REALESTATE_TEST_MYSQL` or user-secret `TestMySql`; they report Skipped when not configured. | Provider-faithful database tests. |
| 2026-11-19 | 1 | Application services use an `IAppDbContext` abstraction (EF Core `DbSet`s + `Database` + `Entry`) defined in Application and implemented by `AppDbContext`; Application now references `Microsoft.EntityFrameworkCore`. Identity-specific operations (users/roles) stay behind `IAdministrationStore`/`IIdentityService` in Infrastructure. | Keeps business logic in Application services (shared by MVC and API) without one repository per aggregate; Domain stays EF-free. |
| 2026-11-19 | 1 | The seeded `Administrator` role is the specified "Super Admin". It always holds every permission and cannot be edited, renamed or deleted. The other eight specified roles are seeded as system roles with default permissions granted only on first creation (edits are kept). Only an Administrator may grant/revoke/edit the Administrator role; the last active Administrator cannot be deactivated or demoted; nobody can deactivate themselves. | Avoids renaming the Phase 0 role/CLI; prevents privilege escalation and lock-out. |
| 2026-11-19 | 1 | Project-level access: users with `projects.access.all` (Administrator, Director by default) see every project; everyone else only projects where they have an active `UserProject` row. Unassigned or unknown project ids return 403 (no probing). Project managers and project creators are auto-assigned. `UserProject.ProjectId` now has an FK to `Projects`. | Spec requires project assignment isolation; resolves the Phase 0 placeholder. |
| 2026-11-19 | 1 | Project area is never edited directly. `InitialArea` is fixed at creation; `CurrentArea` changes only through `LandService.AcquireAsync`/`AdjustAsync` inside one transaction that also writes `ProjectAreaHistory` and updates the distribution. Project `RowVersion` guards concurrent changes (`ConcurrencyException`/HTTP 422 on conflict). Completed and Archived projects reject land changes; Archived projects are read-only. | "Do not allow arbitrary project area changes without a history record." |
| 2026-11-19 | 1 | Land distribution (resolves spec-issues #6 for Phase 1): categories Residential, Commercial, Roads, Parks, OpenSpaces, Reserved, OtherFacilities plus an explicit stored **Unclassified** remainder = project area − classified total (never negative). Saleable area = Residential + Commercial. An adjustment/acquisition changes only Unclassified; a reduction cannot cut the area below the classified total. Every line change is recorded in `LandDistributionHistory`. | The permitted unclassified area must be explicit and distributions must reconcile. |
| 2026-11-19 | 1 | A land adjustment requires: date, reason (≥5 chars), a supporting-document reference (mandatory text) with optional uploaded files linked to the adjustment, and an approver who is an active user holding `land.approve`. Acquisitions auto-create an `Acquired` parcel; original parcels may not exceed the project's original area. Optional acquisition `Cost` is informational only (DECIMAL(18,2)); accounting postings are Phase 8. | Spec lists supporting documents and approver as mandatory without defining the form. |
| 2026-11-19 | 1 | Documents (company logo, `ProjectDocument`, `LandDocument`) are stored through `IFileStorage` (`LocalFileStorage`, root `FileStorage:RootPath`, outside wwwroot) with server-generated keys, SHA-256, extension whitelist and size limit from Company document settings; MIME type derived from the extension; downloads only via permission- and project-checked actions; removal is a soft delete with a mandatory reason. | Global requirement 4.3. |
| 2026-11-19 | 1 | Company settings (currency, rounding, fiscal-year start month, time zone, upload limits) are columns on the single `Companies` row guarded by `RowVersion`. Defaults: PKR/Rs, 2 decimals, Nearest, July, Asia/Karachi, 10 MB. | One company only; no multi-tenancy. |
| 2026-11-19 | 1 | Valid project status transitions: Planning→Active/OnHold/Archived; Active→OnHold/Completed; OnHold→Active/Archived; Completed→Archived; Archived is terminal. A reason is mandatory and history is kept. | Spec lists statuses but not transitions. |
