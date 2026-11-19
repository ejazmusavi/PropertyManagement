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
