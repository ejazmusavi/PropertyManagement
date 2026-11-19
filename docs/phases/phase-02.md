<!-- Phase 02: original requirements are preserved verbatim below the divider (only markdown heading markers were added). -->
> **Phase 2 – Property Inventory Management**
> **Status:** see `docs/project-state.md`
> **Depends on:** Phase 0, Phase 1 (projects, project access)
> **Read first:** `CLAUDE.md`, `docs/project-state.md`, then architecture.md, database-design.md. Also apply `docs/global-requirements.md` (authorization, audit, documents, financial precision, concurrency, retention, MVC/API standards, testing, definition of done).
> Dependencies above are derived from the specification's entity and workflow references; the original specification lists only the sequential order.

---

# PHASE 2 – Property Inventory Management

### Objective

Manage every individual property within each project.

## 2.1 Property classification

Support:

- Residential plots.
- Commercial plots.
- Houses.
- Apartments.
- Shops.
- Offices.
- Custom property categories.

## 2.2 Property organization

Entities:

- PropertyType
- PropertyCategory
- ProjectBlock
- ProjectSector
- ProjectStreet
- Property
- PropertyDimension
- PropertyFeature
- PropertyDocument
- PropertyStatusHistory

## 2.3 Property details

Each property must support:

- Unique property code.
- Project.
- Block.
- Sector.
- Street.
- Property number.
- Property type.
- Size.
- Measurement unit.
- Dimensions.
- Location description.
- Floor number, where applicable.
- Corner status.
- Main road facing.
- Park facing.
- Development status.
- Availability status.
- Documents.
- Remarks.

Property dimensions must be flexible because plots, apartments and houses have different physical characteristics.

## 2.4 Inventory workflows

Statuses:

- Available.
- Reserved.
- Booked.
- Sold.
- Allocated.
- PossessionReady.
- PossessionDelivered.
- Transferred.
- Cancelled.

Reservation must support:

- Reservation date.
- Expiry date.
- Reservation amount.
- Reserved for customer.
- Approved by.

Implement controlled state transitions.

## 2.5 Inventory reports

- Total inventory.
- Available properties.
- Reserved properties.
- Booked properties.
- Sold properties.
- Properties by project.
- Properties by block.
- Properties by type.
- Properties by size.
- Inventory history.

### MVC screens

- Property dashboard.
- Property types.
- Blocks.
- Sectors.
- Streets.
- Property list.
- Create/edit property.
- Property details.
- Property documents.
- Property status history.
- Inventory reports.

### API

- GET /api/v1/properties
- POST /api/v1/properties
- GET /api/v1/properties/{id}
- PUT /api/v1/properties/{id}
- GET /api/v1/properties/available
- POST /api/v1/properties/{id}/reserve
- POST /api/v1/properties/{id}/release-reservation
- GET /api/v1/projects/{id}/inventory-summary

### Acceptance criteria

- All property categories are supported.
- Properties belong to valid projects.
- Property codes are unique within the appropriate business scope.
- Reservation expiry works.
- Double booking is prevented.
- Property history is retained.
