# Project State

Single source of truth for progress. Update **only** after verified work. A phase is complete only when the definition of done in `docs/global-requirements.md` (Part 7) is met; existing documentation never implies completion.

Repository evidence: Phase 0 and Phase 1 source, migrations and tests exist (see per-phase records).

Status values: Not Started · In Progress · Blocked · Needs Review · Complete

| Phase | Name | Status | Depends on | Backup | Build | Tests |
|---|---|---|---|---|---|---|
| 0 | Technical Foundation | Complete (Docker unverified, accepted by user) | – | Yes (user-confirmed) | Pass | 84 pass |
| 1 | Company, Projects, Land and Access Control | Complete | 0 | Yes (user-confirmed) | Pass | 158 pass |
| 2 | Property Inventory Management | Not Started | 0, 1 | – | – | – |
| 3 | Dynamic Property Pricing | Not Started | 1, 2 | – | – | – |
| 4 | Customers, Bookings and Agreements | Not Started | 1, 2, 3 | – | – | – |
| 5 | Installment and Payment Agreement Engine | Not Started | 4 | – | – | – |
| 6 | Payments, Allocation and Customer Ledgers | Not Started | 4, 5 | – | – | – |
| 7 | Expenses, Vendors and Government Fees | Not Started | 1 | – | – | – |
| 8 | Complete Double-Entry Accounting | Not Started | 4–7 | – | – | – |
| 9 | Reports and Management Dashboards | Not Started | 8 | – | – | – |
| 10 | Flutter Customer Application | Not Started | 4, 5, 6 | – | – | – |
| 11 | Notifications, Automation and GIS Foundation | Not Started | 5, 6, 10 | – | – | – |

**Next phase:** Phase 2. Open item: verify Docker compose (Phase 0/1 volume `document-storage`) when Docker is available.

## Per-phase records
Add one block per phase when work starts; keep each under ~15 lines.

### Phase 0 – Technical Foundation
Status: Complete, with one user-accepted exception: the "Docker development environment starts" criterion was NOT verified (Docker not installed on the dev machine). User chose to mark Complete on 2026-11-19. Verify `docker compose up --build` when Docker is available.
Implementation summary: Solution (Domain/Application/Infrastructure/Web/Api + 5 test projects), EF Core + MySQL (Pomelo), ASP.NET Identity (lockout, 12-char policy), JWT API auth with rotating hashed refresh tokens, permission catalog + database-evaluated permission policy, audit service + append-only AuditLog + automatic created/modified stamping, optimistic concurrency (RowVersion) on Company/ApplicationSetting, Serilog, OpenAPI + Swagger UI, global exception handling (Problem Details / MVC filter), FluentValidation, health check, `migrate`/`create-admin` CLI, Dockerfiles + compose, MVC login/dashboard/layout/profile/change-password/user list.
Important decisions: see decisions log (EF9+Pomelo9, Identity in Infrastructure, RefreshToken entity, CLI admin bootstrap, DB-evaluated permissions, OpenAPI+SwaggerUI).
Files/modules affected (complete list; replaces git history):
- Root: RealEstate.slnx, Directory.Build.props, Directory.Packages.props, .gitignore, .dockerignore, Dockerfile.api, Dockerfile.web, docker-compose.yml, .env.example
- src/RealEstate.Domain: Common (Entity, IAuditable, DomainException, Permissions, RoleNames), Entities (Company, Permission, RolePermission, UserProject, AuditLog, ApplicationSetting, RefreshToken)
- src/RealEstate.Application: Common (Result, Paging, exceptions, ICurrentUser, IPermissionService, validation ext), Audit, Auth (AuthService, models, validators, abstractions), Users (UserService, models), DependencyInjection
- src/RealEstate.Infrastructure: Identity (ApplicationUser/Role, IdentityService, PermissionService, JWT factory, RefreshTokenService, CurrentUser, permission authorization, options), Persistence (AppDbContext, AuditService, UserQueryService, DatabaseInitializer, DatabaseHealthCheck, AppDbContextFactory, Bootstrap/AdminBootstrapper, Migrations/), EntityConfigurations, DependencyInjection
- src/RealEstate.Api: Program, Controllers (Auth, Health), Middleware/GlobalExceptionHandler, Swagger/BearerSecurityTransformer, appsettings
- src/RealEstate.Web: Program, Cli/CliCommands, Controllers (Account, Home, Profile, Users), Filters, ViewModels, Views (Account, Home, Profile, Users, Shared incl. NavMenu component), wwwroot css/js, appsettings
- tests/: Domain, Application, Infrastructure, Api, Web test projects
- docs: development-setup.md (new), decisions/architecture-decisions.md, project-state.md
Database changes (migrations): Phase00_InitialFoundation (Users, Roles, UserRoles, UserClaims, UserLogins, RoleClaims, UserTokens, Companies, Permissions, RolePermissions, UserProjects, AuditLogs, ApplicationSettings, RefreshTokens).
APIs / MVC screens: POST /api/v1/auth/login|refresh|logout, GET /api/v1/auth/me, GET /api/v1/health, Swagger /swagger; MVC: Login, Dashboard, Navigation layout, Profile, Change password, Users list (search/sort/paging).
Build result: Succeeded, 0 warnings, 0 errors (`dotnet build RealEstate.slnx`).
Test result (`dotnet test RealEstate.slnx`): Domain 8, Application 27, Infrastructure 21, Api 15, Web 13 = 84 passed, 0 failed, 0 skipped (against local MySQL 8.4). UI checked manually in the browser (login, dashboard, users, change password, mobile layout).
Acceptance criteria met / unmet: Met - solution builds; DB connection; migrations execute (also via `migrate` CLI); authentication (cookie + JWT); API docs (/openapi/v1.json, /swagger); initial admin created securely; tests run. Unverified (accepted by user) - Docker development environment starts (Docker not installed here).
Outstanding issues: Docker/compose untested. UserProject.ProjectId has no FK until Project exists (Phase 1). Flutter 3.47.8 verified present. Settings/roles/audit have no UI in Phase 0 by design.
Backup taken before phase (name/date): confirmed by user in chat, 2026-11-19 (name not recorded).
Independent review (2026-11-19): Build 0 warnings/0 errors; 84 tests passed (Domain 8, Application 27, Infrastructure 21, Api 15, Web 13). All acceptance criteria verified except "Docker development environment starts" (Docker not installed; compose/Dockerfiles read only). Strict verdict: Not complete until Docker is verified; user-accepted exception stands. No code defects found. Non-blocking: run `docker compose up --build` when possible; add UserProject.ProjectId FK in Phase 1; compose maps HTTP only while Web calls UseHttpsRedirection (harmless warning); `web` service has no healthcheck.

### Phase 1 – Company, Projects, Land and Access Control
Status: Complete. Phase 0's open Docker item remains unverified (Docker not installed); the Phase 1 compose change (shared `document-storage` volume, `FileStorage__RootPath`) is likewise untested.
Implementation summary: Company profile/settings (logo, contact, address, currency, rounding, fiscal year, time zone, document settings) with RowVersion; Projects (Project, ProjectAddress, ProjectDocument, ProjectManagerAssignment, ProjectAreaHistory, ProjectStatusHistory) with status workflow, manager history and documents; Land (LandParcel, LandAcquisition, LandAdjustment, LandDistribution + history, LandDocument) where project area changes only through transactional acquisition/adjustment with approver, reason, supporting reference and history, and distribution reconciles to the area with an explicit Unclassified remainder; user management (create/edit/roles/active), role management (create/edit/delete, 9 specified roles seeded), editable permission matrix, project access assignment with project-level enforcement in backend services; secure local file storage outside web root; audit for every change.
Important decisions: see decisions log (IAppDbContext in Application, Administrator = Super Admin, project access model, area-change control, distribution/unclassified rule, adjustment requirements, document storage, status transitions). Resolves spec-issues #3 (roles seed + UI) and the Phase 1 part of #6 (unclassified area).
Files/modules affected (complete list; replaces git history):
- Domain: Common (Permissions extended, StoredDocument), Enums/Enums.cs, Entities (Company extended, Project.cs incl. ProjectAddress/Document/ManagerAssignment/StatusHistory/AreaHistory, Land.cs incl. LandParcel/Acquisition/Adjustment/Distribution/DistributionHistory/Document + LandRules)
- Application: Common (IAppDbContext, ProjectAccessService, Validation, ConcurrencyExtensions in Abstractions.cs), Companies, Documents (IFileStorage, DocumentUploader), Projects, LandManagement, Administration (store interface, UserAdministration/Role/ProjectAccessAdmin services), Users (UserModels lookup methods), DependencyInjection, csproj (+EF Core reference)
- Infrastructure: Persistence (AppDbContext, AdministrationStore, UserQueryService, DatabaseInitializer role seeding, Migrations/Phase01_CompanyProjectsLandAccess), EntityConfigurations (Phase01Configurations, Domain/Identity configs), Storage/LocalFileStorage, Identity (ApplicationRole.IsSystem), DependencyInjection
- Api: Controllers (Company, Projects, ProjectLand, Users, Roles+Permissions, ApiHelpers), appsettings
- Web: Controllers (Company, Projects, Land, Users extended, Roles, Permissions), Services/UiPermissions, ViewModels/Phase01ViewModels, Filters (TryAddError), Views (Company, Projects, Land, Users form, Roles, Permissions, _Pager, NavMenu), Program.cs, appsettings
- tests/: Phase01DomainTests, Phase01ValidatorTests (+UserAdministrationRuleTests), Phase01Tests (integration), Phase01ApiTests, Phase01WebTests; DatabaseTests (UserProject test updated for the new FK), ApiFactory/WebFactory (role helpers, shared collections)
- Root/docs: docker-compose.yml (document-storage volume), docs/development-setup.md, decisions/architecture-decisions.md, project-state.md
Database changes (migrations): Phase01_CompanyProjectsLandAccess – Companies (+settings/logo columns, defaults applied to existing row), Roles.IsSystem, Projects, ProjectAddresses, ProjectDocuments, ProjectManagerAssignments, ProjectStatusHistories, ProjectAreaHistories, LandParcels, LandAcquisitions, LandAdjustments, LandDistributions (unique project+category, CHECK area >= 0), LandDistributionHistories, LandDocuments, FK UserProjects.ProjectId -> Projects.
APIs / MVC screens: GET/POST /api/v1/projects, GET/PUT /projects/{id}, POST /projects/{id}/status, /projects/{id}/documents (list/upload/download/delete), /projects/{id}/access (list/grant/revoke), GET /projects/{id}/land, POST land/parcels|acquisitions|adjustments, PUT land/distribution, GET land/history|distribution/history, land/documents (list/upload/download/delete), GET/POST /users, GET/PUT /users/{id}, GET /roles, /roles/{id}, /permissions, GET/PUT /company (+logo). MVC: Company profile, Project list/create/edit/details (+status, documents), Project access assignment, Land overview, Land distribution, Area adjustment history, Land documents, Acquire/Adjust forms, User list/create/edit, Role list/create/edit/delete, Permission matrix.
Build result: `dotnet build RealEstate.slnx` succeeded, 0 warnings, 0 errors.
Test result (`dotnet test RealEstate.slnx`, local MySQL 8.4): Domain 35, Application 36, Infrastructure 42, Api 25, Web 20 = 158 passed, 0 failed, 0 skipped (after the review fix below).
Review fix (2026-11-19): land distribution had no client concurrency token, so a stale form could silently overwrite another user's distribution. `LandOverviewDto.DistributionVersion` (hash of the distribution rows' RowVersions) is now required in `SetLandDistributionRequest` (API: PUT land/distribution body field `distributionVersion`; MVC: hidden field); a stale version returns ConcurrencyException (HTTP 422 / form error with current values reloaded). Files: Application/LandManagement (LandModels.cs, LandService.cs), Web (LandController.cs, ViewModels/Phase01ViewModels.cs, Views/Land/Distribution.cshtml), tests (Phase01ValidatorTests, Phase01Tests +2 tests, Phase01ApiTests, Phase01WebTests). No schema change.
UI verification: all screens exercised through rendered-HTML integration tests; in a real browser (desktop and 375px mobile) only login, project create form, land overview and distribution (incl. live remainder calculation) were inspected manually. Other screens were not looked at visually.
Acceptance criteria met / unmet: Met - multiple projects managed; area changes only via controlled transactions with history; distribution tracked and reconciled; historical records preserved (no delete paths, soft-delete documents with reason); project access restrictions work (service, API and MVC tests); users cannot access unauthorized projects (403). Unmet: none known.
Outstanding issues: Docker compose still unverified. No admin password reset / user self-registration (not in scope). Uploaded files are validated by extension and size, not by content sniffing/antivirus. A throw-away dev database `realestate_ui_check` was left on the local MySQL server by the manual UI check and can be dropped. Project documents have no per-type permission beyond `projects.documents`.
Backup taken before phase (name/date): confirmed by user in chat, 2026-11-19 (name not recorded).


```
### Phase N – Name
Status:
Implementation summary:
Important decisions:
Files/modules affected (complete list; replaces git history):
Database changes (migrations):
APIs / MVC screens:
Build result:
Test result (command, passed/failed counts):
Acceptance criteria met / unmet:
Outstanding issues:
Backup taken before phase (name/date):
```

## Open issues
See `docs/decisions/spec-issues.md` for specification ambiguities; list implementation issues here.
