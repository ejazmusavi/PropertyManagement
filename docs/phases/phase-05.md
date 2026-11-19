<!-- Phase 05: original requirements are preserved verbatim below the divider (only markdown heading markers were added). -->
> **Phase 5 – Installment and Payment Agreement Engine**
> **Status:** see `docs/project-state.md`
> **Depends on:** Phase 4 (customers, bookings, agreements)
> **Read first:** `CLAUDE.md`, `docs/project-state.md`, then architecture.md, database-design.md (financial precision). Also apply `docs/global-requirements.md` (authorization, audit, documents, financial precision, concurrency, retention, MVC/API standards, testing, definition of done).
> Dependencies above are derived from the specification's entity and workflow references; the original specification lists only the sequential order.

---

# PHASE 5 – Installment and Payment Agreement Engine

### Objective

Create a flexible installment engine that can handle any reasonable customer agreement.

## 5.1 Entities

- InstallmentPlan
- InstallmentPlanComponent
- CustomerAgreement
- AgreementCharge
- AgreementVersion
- AgreementAmendment
- PaymentSchedule
- PaymentScheduleItem
- ScheduleAmendment
- LateFeeRule
- Waiver
- ChargeType

## 5.2 Plan types

- Standard project plan.
- Property-category plan.
- Property-specific plan.
- Customer-specific plan.
- Cash agreement.

## 5.3 Charge components

Support:

- Down payment.
- Confirmation payment.
- Monthly installment.
- Quarterly installment.
- Half-yearly installment.
- Annual installment.
- Possession payment.
- Development charges.
- Utility charges.
- Government fees.
- Custom charges.

## 5.4 Component configuration

Every charge must support:

- Amount.
- Frequency.
- Number of installments.
- First due date.
- Subsequent due dates.
- Grace period.
- Late fee rules.
- Waiver rules.
- Applicable charge category.
- Tax treatment.
- Remarks.

## 5.5 Schedule generation

Implement a deterministic ScheduleGenerationService.

Support:

- Monthly schedules.
- Quarterly schedules.
- Half-yearly schedules.
- Annual schedules.
- Custom dates.
- Fixed dates.
- One-time payments.
- Irregular schedules.

The engine must handle:

- Month-end dates.
- Leap years.
- Rounding.
- Partial installments.
- Custom schedules.
- Agreement reconciliation.

Every generated schedule must reconcile with the agreement charge total.

## 5.6 Agreement amendments

Support:

- Rescheduling.
- Installment deferment.
- Installment reduction.
- Additional installments.
- Due-date changes.
- Penalty changes.
- Waivers.
- Additional charges.

All changes require an amendment record and applicable approval.

Never destroy original schedule history.

### MVC screens

- Installment plan templates.
- Plan component configuration.
- Customer agreement.
- Installment schedule builder.
- Schedule preview.
- Agreement amendment.
- Schedule history.
- Late fee configuration.
- Waiver approvals.

### API

- GET /api/v1/installment-plans
- POST /api/v1/installment-plans
- POST /api/v1/agreements/{id}/generate-schedule
- GET /api/v1/agreements/{id}/schedule
- POST /api/v1/agreements/{id}/amendments
- POST /api/v1/agreements/{id}/amendments/{amendmentId}/approve
- GET /api/v1/agreements/{id}/history

### Acceptance criteria

- Multiple installment frequencies work.
- Custom customer plans work.
- Schedule totals reconcile.
- Partial and advance payments can be represented.
- Amendments preserve historical versions.
- All calculations pass automated tests.
