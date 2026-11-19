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
| 2026-11-19 | 2 | **Property code scope (resolves spec-issues #6 for Phase 2):** a property code is unique within its project (unique index `(ProjectId, PropertyCode)`, stored upper-case, immutable after creation). The same code may exist in different projects. A property *number* must additionally be unique within the same project + block + sector + street combination (service-level check). | "Unique within the appropriate business scope" was undefined; per-project scope matches how plots are numbered and avoids cross-project coupling. |
| 2026-11-19 | 2 | Property categories and types are data, not enums: `PropertyCategory` (Residential, Commercial seeded) and `PropertyType` (Residential Plot, Commercial Plot, House, Apartment, Shop, Office seeded) are system rows created idempotently by code in `DatabaseInitializer`; custom categories/types can be added. System rows cannot be deactivated or re-classified. Each type has a `Form` (Plot/House/Apartment/Commercial/Other) and `RequiresFloor` flag (apartments, shops, offices). | "Custom property categories" requirement; floor applies only where applicable. |
| 2026-11-19 | 2 | Block, sector and street are three tables with one shape (`ProjectZone`: project, code, name, active), unique code and name per project, deactivated rather than deleted. Properties reference them optionally; inactive or other-project zones are rejected for new assignments. | Keeps hierarchy flexible (a project may use only some of the three). |
| 2026-11-19 | 2 | Physical characteristics are flexible name/value rows (`PropertyDimension`: name, DECIMAL(18,4) value, unit; `PropertyFeature`: name, optional text) with unique names per property, instead of fixed columns, because plots, houses and apartments differ. Fixed columns exist only for the spec's standard fields (size, unit, floor, corner, main-road, park, development status). | "Property dimensions must be flexible." |
| 2026-11-19 | 2 | Inventory state machine (`Property.CanTransition`): Available→Reserved/Booked/Cancelled; Reserved→Available/Booked/Cancelled; Booked→Sold/Cancelled; Sold→Allocated/PossessionReady/Transferred/Cancelled; Allocated→PossessionReady/Transferred/Cancelled; PossessionReady→PossessionDelivered/Cancelled; PossessionDelivered→Transferred; Cancelled→Available (re-list); Transferred is terminal. Every change requires a reason and writes `PropertyStatusHistory` (append-only, FK Restrict) plus an audit record. Entering Reserved is only possible via the reserve action and leaving it to Available only via release/expiry. `POST /properties/{id}/status` (`inventory.status`) is the controlled entry point Phases 4-5 will call for booking/sale/possession. | Spec lists the statuses and asks for "controlled state transitions" without defining them. |
| 2026-11-19 | 2 | Reservations are a `PropertyReservation` table (date, expiry, amount DECIMAL(18,2), customer, approver, status Active/Released/Expired/Converted, close reason). **Double booking** is prevented three ways: status check, property `RowVersion`, and a unique index on `ActivePropertyId` (= PropertyId while Active, NULL once closed) so concurrent reservations fail at the database. The customer is stored as an optional `CustomerId` (no FK until Phase 4) plus the name/contact captured at reservation time. The approver must be an active user holding `inventory.reserve`. The reservation amount is informational in Phase 2; receiving and allocating money is Phase 6. | Customers do not exist until Phase 4; booking-integrity is a financial-correctness concern. |
| 2026-11-19 | 2 | Reservation expiry runs lazily and transactionally (`PropertyService.ExpireOverdueAsync`) before every list/available/get/reserve/status/summary/report call and on demand (`POST /api/v1/inventory/expire-reservations`, dashboard button). Expiry is evaluated against "today" in the company time zone. It writes status history (actor `system`), an audit record and closes the reservation as Expired. A background scheduler is deferred to Phase 11 (Hangfire). | Correct without new infrastructure; stock is never shown stale. |
| 2026-11-19 | 2 | Property edit rules: physical fields (number, type, size, unit, zone, floor) are editable only while Available/Reserved/Cancelled; after booking only remarks, flags, development status, location text, dimensions/features can change. Properties cannot be created in Completed/Archived projects; Archived projects reject all property changes. Property records are never deleted. Property documents reuse `StoredDocument` (secure storage, soft delete with reason). | Protects sold inventory; retention rules. |
| 2026-11-19 | 2 | New permissions: `inventory.view`, `.manage`, `.setup`, `.documents`, `.reserve`, `.status`; all property data is project-scoped through `ProjectAccessService` (unknown/unassigned property ids return 403). Default roles are granted these only when the role is first created (existing databases: adjust in the permission matrix; Administrator always has all). | Per global authorization requirements; consistent with Phase 1 decision on role seeding. |
| 2026-11-19 | 2 | `ConcurrencyExtensions.SaveWithConcurrencyAsync` now also converts a MySQL deadlock (loser of two racing writers, rolled back) into `ConcurrencyException`. Adding Phase 2 tables changed EF's statement ordering and exposed this in the Phase 1 test `ConcurrentAdjustments_NeverLoseAnUpdate`. | A deadlock between racing writers has the same meaning as a stale edit. |
| 2026-11-19 | 3 | **Precedence policy:** scope specificity Customer > Property > PropertyCategory > Street > Block > Project (the `PricingScope` enum order, as listed in the spec); within a scope higher `Priority`, then later `EffectiveFrom`, then higher Id. Base rates compete as one group; premiums and additional charges compete per rule `Code` (a more specific rule with the same code replaces the general one, different codes stack). Non-stackable discounts compete (most specific wins); stackable discounts all apply. | Spec requires an "explicit precedence policy". |
| 2026-11-19 | 3 | **Price formula:** size-based price = base rate x size (rate converted between measurement units; Marla = 272.25 sq ft, Kanal = 20 Marla); BasePrice = rate per one unit of the property's own unit; premiums (fixed / per unit / % of size-based price, only for matching factor: corner, road, park, commercial, plus explicit conditions); discounts (% of price before discount or fixed, capped at the price); additional charges (not discounted); approved override replaces the final price. Each line is rounded with the company rounding mode/decimals. Pure engine in `Domain/Pricing/PricingEngine`; `PricingCalculationService` loads data and enforces authorization. | Deterministic, testable, reproducible. |
| 2026-11-19 | 3 | **Immutable pricing:** pricing-defining fields of rules, discounts and adjustments are never edited; rules are retired (effective window closed the day before today, status Retired stays effective inside its window) and replaced. New rules/discounts cannot start in the past. Hence a calculation for a past date cannot change. `PriceSnapshotDto` (canonical JSON + SHA-256) is the immutable snapshot Phase 4 stores with each booking; `PropertyPrice`/`PropertyPriceHistory` store the snapshot of every published price (verified by hash on read). | "Never recalculate historical bookings"; booking entities arrive in Phase 4. |
| 2026-11-19 | 3 | **Approvals (maker-checker):** `PricingApproval` types PriceOverride, CustomerDiscount, SpecialPricing (customer-negotiated price) and PricingException (property-wide discount/surcharge). Approval creates an immutable `PropertyPriceAdjustment`; the approver must hold `pricing.approve` and differ from the requester; rejection needs a comment; one pending request per type/property/customer. Customer-scope pricing rules are created `PendingApproval` with an automatic SpecialPricing approval and apply only after approval. Property-wide adjustments republish the property price; customer-specific ones apply only when calculating for that customer. | Spec 3.5; ownership/financial correctness. |
| 2026-11-19 | 3 | **Frozen prices:** only Available, Reserved or Cancelled properties can be (re)priced, requested or revoked; Booked and later statuses are frozen, so existing bookings/agreements never move. Publishing is idempotent (unchanged content creates no history); project-wide publish reports published/unchanged/frozen/skipped. `PropertyPrice` is unique per property (concurrent first publish yields one row). | Retention and reproducibility. |
| 2026-11-19 | 3 | Customers do not exist until Phase 4: customer ids in customer-scope rules, discounts and approvals are stored as supplied (plus a free-text reference) with no FK or existence check. New permissions `pricing.view/manage/request/approve` (default roles granted on first creation only, as in earlier phases). | Same approach as reservations (Phase 2). |
