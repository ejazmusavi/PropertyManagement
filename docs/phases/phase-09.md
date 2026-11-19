<!-- Phase 09: original requirements are preserved verbatim below the divider (only markdown heading markers were added). -->
> **Phase 9 – Reports and Management Dashboards**
> **Status:** see `docs/project-state.md`
> **Depends on:** Phase 8 (authoritative financial source) plus Phases 1-7 data
> **Read first:** `CLAUDE.md`, `docs/project-state.md`, then architecture.md, database-design.md (indexing/pagination). Also apply `docs/global-requirements.md` (authorization, audit, documents, financial precision, concurrency, retention, MVC/API standards, testing, definition of done).
> Dependencies above are derived from the specification's entity and workflow references; the original specification lists only the sequential order.

---

# PHASE 9 – Reports and Management Dashboards

### Objective

Create consolidated management reporting.

## 9.1 Company dashboard

Display:

- Total projects.
- Total land area.
- Total saleable area.
- Total inventory.
- Available properties.
- Sold properties.
- Total booking value.
- Total collections.
- Customer receivables.
- Overdue amount.
- Cash balance.
- Bank balance.
- Expenses.
- Revenue.
- Profit/loss.

## 9.2 Project dashboard

Display:

- Project area.
- Land distribution.
- Property inventory.
- Sales.
- Bookings.
- Collections.
- Outstanding.
- Overdue installments.
- Project expenses.
- Project profitability.

## 9.3 Customer reports

- Customer statement.
- Customer ledger.
- Customer property list.
- Payment history.
- Installment history.
- Outstanding balance.
- Overdue report.

## 9.4 Sales reports

- Daily sales.
- Monthly sales.
- Project-wise sales.
- Block-wise sales.
- Sales by property category.
- Sales by salesperson.
- Agent commissions.
- Cancellation report.

## 9.5 Collection reports

- Daily collection.
- Monthly collection.
- Project collection.
- Payment method summary.
- Cashier collection.
- Bank collection.
- Advance payment report.

## 9.6 Financial reports

Use the accounting engine as the authoritative source for financial statements.

Implement PDF and Excel exports.

All reports must support:

- Date range.
- Project.
- Block.
- Customer.
- Transaction type.
- Payment method.
- Status.

### Acceptance criteria

All dashboard figures must be calculated from actual business data and reconcile with underlying financial records.
