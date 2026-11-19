# Global Requirements (apply to every phase)

Verbatim from the original specification, Parts 4-7. Phase documents reference this file instead of repeating it.

# PART 4 – GLOBAL FUNCTIONAL REQUIREMENTS

These requirements apply to all phases.

## 4.1 Authorization

Implement:

- Role-based access.
- Permission-based access.
- Project-based access.
- Data-level access.
- Customer-level access.

Enforce authorization in backend services and API endpoints.

## 4.2 Audit

Track:

- Who created a record.
- Who modified it.
- Who approved it.
- Who cancelled it.
- Previous values.
- New values.
- Date and time.
- Reason.

Sensitive changes must have explicit audit records.

## 4.3 Documents

Support:

- CNIC.
- Booking agreements.
- Payment receipts.
- Land documents.
- Property documents.
- Expense invoices.
- Government receipts.
- Other attachments.

Store files outside the public web root.

Use secure document access.

## 4.4 Financial precision

- DECIMAL(18,2) for monetary amounts.
- DECIMAL(18,4) for unit rates.
- No floating-point money calculations.
- Configurable rounding.
- Currency-specific formatting.

## 4.5 Concurrency

Protect:

- Property bookings.
- Payment posting.
- Payment allocations.
- Journal posting.
- Agreement amendments.

Use appropriate optimistic concurrency, database transactions, unique constraints and idempotency.

## 4.6 Data retention

Never silently delete:

- Posted payments.
- Posted journal entries.
- Confirmed agreements.
- Approved booking history.
- Payment allocations.
- Reversal history.

Use controlled reversals and cancellation workflows.

---

# PART 5 – STANDARD MVC SCREEN REQUIREMENTS

Every major business module should include:

1. List page.
2. Create page.
3. Edit page.
4. Details page.
5. Search.
6. Filtering.
7. Pagination.
8. Validation.
9. Permission checks.
10. Audit history where relevant.

Use:

- Bootstrap 5.
- Responsive forms.
- Professional tables.
- Consistent navigation.
- Dashboard cards.
- Confirmation dialogs.
- Toast messages.
- Loading states.
- Empty states.

Desktop:

- Use multiple columns where appropriate.
- Three form fields per row when suitable.

Mobile:

- One field per row.
- Full-width controls.
- Responsive tables or card alternatives.

Do not create inactive buttons or placeholder functionality.

---

# PART 6 – API STANDARDS

Use versioned endpoints:

/api/v1/

Standards:

- RESTful resource naming.
- Request and response DTOs.
- Validation.
- Pagination.
- Filtering.
- Sorting.
- Standard error responses.
- Authentication.
- Permission checks.
- API documentation.

Never expose Domain entities directly through APIs.

Use consistent error responses, preferably Problem Details.

Customer APIs must enforce ownership and project access.

---

# PART 7 – TESTING AND ACCEPTANCE

Each phase must have:

- Domain unit tests.
- Application service tests.
- Integration tests.
- API tests.
- Database tests.
- Authorization tests.
- UI verification.

Critical end-to-end scenarios:

1. Create a project.
2. Add land.
3. Create blocks and properties.
4. Configure pricing.
5. Register a customer.
6. Book a property.
7. Generate a customized installment agreement.
8. Generate payment schedules.
9. Collect down payment.
10. Collect monthly installment.
11. Allocate payment.
12. Generate receipt.
13. Verify customer ledger.
14. Verify accounting entries.
15. Record project expense.
16. Generate project financial statement.
17. Access customer mobile application.
18. Verify customer statement.

Test failures, duplicate requests, concurrent operations, refunds, cancellation and amendments.

Definition of done

A phase is complete only when:

- Business functionality is implemented.
- Database changes are complete.
- Migrations work.
- MVC screens work.
- APIs work.
- Authorization works.
- Validation works.
- Tests pass.
- Documentation is updated.
- Build succeeds.

A generated model or empty screen does not qualify as completed functionality.
