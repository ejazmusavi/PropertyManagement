<!-- Phase 11: original requirements are preserved verbatim below the divider (only markdown heading markers were added). -->
> **Phase 11 – Notifications, Automation and GIS Foundation**
> **Status:** see `docs/project-state.md`
> **Depends on:** Phase 5, Phase 6 (reminders, confirmations); Phase 10 (mobile push)
> **Read first:** `CLAUDE.md`, `docs/project-state.md`, then architecture.md, database-design.md. Also apply `docs/global-requirements.md` (authorization, audit, documents, financial precision, concurrency, retention, MVC/API standards, testing, definition of done).
> Dependencies above are derived from the specification's entity and workflow references; the original specification lists only the sequential order.

---

# PHASE 11 – Notifications, Automation and GIS Foundation

### Objective

Prepare advanced business automation and future mapping.

## 11.1 Notifications

Entities:

- Notification
- NotificationTemplate
- NotificationPreference
- NotificationDeliveryLog

Support:

- Upcoming installment reminders.
- Overdue reminders.
- Payment confirmations.
- Booking confirmations.
- Agreement amendments.
- Possession notifications.
- Company announcements.

Channels:

- In-app.
- Email.
- SMS.
- WhatsApp.
- Mobile push notifications.

Integrate channels progressively.

## 11.2 Background processing

Use Hangfire for:

- Scheduled reminders.
- Recurring reports.
- Payment reconciliation tasks.
- Notification dispatch.
- Scheduled maintenance.

Jobs must be idempotent and have retry policies.

## 11.3 GIS foundation

Do not implement complete mapping until the core ERP is stable.

Prepare:

- ProjectBoundary.
- PropertyBoundary.
- MapLayer.
- SpatialReferenceSystem.
- MapDocument.

Future technologies:

- OpenStreetMap.
- MapLibre.
- GeoJSON.
- MySQL Spatial.
- QGIS.

Future features:

- Project boundary.
- Plot polygons.
- Streets.
- Blocks.
- Exact dimensions.
- Property status visualization.
- Plot search.
- Map-based property selection.

Use verified survey data for exact boundaries.
