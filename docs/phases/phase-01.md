<!-- Phase 01: original requirements are preserved verbatim below the divider (only markdown heading markers were added). -->
> **Phase 1 – Company, Projects, Land and Access Control**
> **Status:** see `docs/project-state.md`
> **Depends on:** Phase 0
> **Read first:** `CLAUDE.md`, `docs/project-state.md`, then architecture.md (authorization), database-design.md. Also apply `docs/global-requirements.md` (authorization, audit, documents, financial precision, concurrency, retention, MVC/API standards, testing, definition of done).
> Dependencies above are derived from the specification's entity and workflow references; the original specification lists only the sequential order.

---

# PHASE 1 – Company, Projects, Land and Access Control

### Objective

Implement the company's organizational structure and complete project land management.

## 1.1 Company management

Features:

- Company profile.
- Company logo.
- Contact information.
- Address.
- Currency settings.
- Fiscal year.
- Timezone.
- Financial settings.
- Document settings.

One company only. Do not introduce multi-company tenancy.

## 1.2 Project management

Entities:

- Project
- ProjectAddress
- ProjectDocument
- ProjectManagerAssignment
- ProjectAreaHistory
- ProjectStatusHistory

Project fields:

- ProjectCode
- ProjectName
- Description
- City
- Address
- StartDate
- ExpectedCompletionDate
- InitialArea
- CurrentArea
- MeasurementUnit
- Status
- ProjectManagerId

Project status:

- Planning
- Active
- OnHold
- Completed
- Archived

## 1.3 Land management

Entities:

- LandParcel
- LandAcquisition
- LandAdjustment
- LandDistribution
- LandDistributionHistory
- LandDocument

Track:

- Original land.
- Acquired land.
- Added land.
- Reduced land.
- Total project area.
- Saleable area.
- Roads.
- Parks.
- Open spaces.
- Commercial areas.
- Residential areas.
- Reserved areas.
- Other facilities.

Every land adjustment must contain:

- Date.
- Previous area.
- Adjustment.
- New area.
- Reason.
- Supporting documents.
- Approved by.

Do not allow arbitrary project area changes without a history record.

Land distributions must reconcile to the project land area, with any permitted unclassified area explicitly identified.

## 1.4 User management and permissions

Roles:

- Super Admin.
- Director.
- Project Manager.
- Sales Manager.
- Accountant.
- Cashier.
- Recovery Officer.
- Inventory Manager.
- Customer Support.

Support granular permissions and project assignments.

### MVC screens

- Company profile.
- Project list.
- Create/edit project.
- Project details.
- Land management.
- Land distribution.
- Area adjustment history.
- Land documents.
- User management.
- Role management.
- Permission management.
- Project access assignment.

### API

- GET /api/v1/projects
- POST /api/v1/projects
- GET /api/v1/projects/{id}
- PUT /api/v1/projects/{id}
- GET /api/v1/projects/{id}/land
- POST /api/v1/projects/{id}/land/acquisitions
- POST /api/v1/projects/{id}/land/adjustments
- GET /api/v1/projects/{id}/land/history
- GET /api/v1/users
- POST /api/v1/users
- PUT /api/v1/users/{id}
- GET /api/v1/roles
- GET /api/v1/permissions

### Acceptance criteria

- Multiple projects can be managed.
- Project areas update through controlled transactions.
- Land distribution is tracked.
- Historical records are preserved.
- Project access restrictions work.
- Users cannot access unauthorized projects.
