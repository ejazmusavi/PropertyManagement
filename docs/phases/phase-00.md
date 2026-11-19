<!-- Phase 00: original requirements are preserved verbatim below the divider (only markdown heading markers were added). -->
> **Phase 0 – Technical Foundation**
> **Status:** see `docs/project-state.md`
> **Depends on:** None (first phase)
> **Read first:** `CLAUDE.md`, `docs/project-state.md`, then architecture.md, coding-standards.md, database-design.md (migration conventions). Also apply `docs/global-requirements.md` (authorization, audit, documents, financial precision, concurrency, retention, MVC/API standards, testing, definition of done).
> Dependencies above are derived from the specification's entity and workflow references; the original specification lists only the sequential order.

---

# PHASE 0 – Technical Foundation

### Objective

Create a stable, maintainable development environment.

### Implementation tasks

- Inspect existing repository.
- Verify .NET and Flutter SDKs.
- Verify MySQL availability.
- Create solution and projects.
- Configure project references.
- Configure dependency injection.
- Configure EF Core and MySQL.
- Configure Identity.
- Configure Serilog.
- Configure Swagger.
- Configure global exception handling.
- Configure validation.
- Configure environment settings.
- Configure Docker.
- Configure automated tests.
- Configure database migrations.

### Database entities

Initial:

- Company
- ApplicationUser
- Role
- Permission
- UserRole
- RolePermission
- UserProject
- AuditLog
- ApplicationSetting

### MVC screens

- Login.
- Dashboard shell.
- Navigation layout.
- Profile.
- Change password.
- User management shell.

API endpoints

- POST /api/v1/auth/login
- POST /api/v1/auth/refresh
- POST /api/v1/auth/logout
- GET /api/v1/auth/me
- GET /api/v1/health

### Acceptance criteria

- Solution builds successfully.
- Database connection works.
- Migrations execute successfully.
- Authentication works.
- API documentation works.
- Initial administrator can be created securely.
- Tests run successfully.
- Docker development environment starts.

Phase gate: No further development until the foundation is stable.
