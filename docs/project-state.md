# Project State

Single source of truth for progress. Update **only** after verified work. A phase is complete only when the definition of done in `docs/global-requirements.md` (Part 7) is met; existing documentation never implies completion.

Repository evidence at documentation setup: no application source, solution, migrations or tests exist. All phases Not Started.

Status values: Not Started · In Progress · Blocked · Needs Review · Complete

| Phase | Name | Status | Depends on | Backup | Build | Tests |
|---|---|---|---|---|---|---|
| 0 | Technical Foundation | Not Started | – | – | – | – |
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

**Next phase:** Phase 0 (no dependencies). Phase 0 gate: no further development until the foundation is stable.

## Per-phase records
Add one block per phase when work starts; keep each under ~15 lines.

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
