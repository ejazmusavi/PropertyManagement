# Project State

Single source of truth for progress. Update **only** after verified work. A phase is complete only when the definition of done in `docs/global-requirements.md` (Part 7) is met; existing documentation never implies completion.

Repository evidence: Phase 0 source, migration and tests exist (see Phase 0 record).

Status values: Not Started · In Progress · Blocked · Needs Review · Complete

| Phase | Name | Status | Depends on | Backup | Build | Tests |
|---|---|---|---|---|---|---|
| 0 | Technical Foundation | Complete (Docker unverified, accepted by user) | – | Yes (user-confirmed) | Pass | 84 pass |
| 1 | Company, Projects, Land and Access Control | Not Started | 0 | – | – | – |
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

**Next phase:** Phase 1. Open item: verify Docker compose for Phase 0 when Docker is available.

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
