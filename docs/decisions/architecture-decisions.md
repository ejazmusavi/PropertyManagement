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
