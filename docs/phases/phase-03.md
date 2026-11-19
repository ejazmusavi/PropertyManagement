<!-- Phase 03: original requirements are preserved verbatim below the divider (only markdown heading markers were added). -->
> **Phase 3 – Dynamic Property Pricing**
> **Status:** see `docs/project-state.md`
> **Depends on:** Phase 1, Phase 2 (project, block, street, property attributes)
> **Read first:** `CLAUDE.md`, `docs/project-state.md`, then architecture.md, database-design.md. Also apply `docs/global-requirements.md` (authorization, audit, documents, financial precision, concurrency, retention, MVC/API standards, testing, definition of done).
> Dependencies above are derived from the specification's entity and workflow references; the original specification lists only the sequential order.

---

# PHASE 3 – Dynamic Property Pricing

### Objective

Build a configurable pricing engine.

## 3.1 Pricing entities

- PricingRule
- PricingRuleCondition
- PropertyPrice
- PropertyPriceHistory
- PropertyPriceAdjustment
- DiscountRule
- PricingApproval

## 3.2 Pricing hierarchy

Support:

1. Project pricing.
2. Block pricing.
3. Street pricing.
4. Property category pricing.
5. Property-specific pricing.
6. Customer-negotiated pricing.

More specific pricing rules should override general rules according to an explicit precedence policy.

## 3.3 Pricing factors

- Size.
- Base rate.
- Location.
- Corner.
- Road facing.
- Park facing.
- Commercial category.
- Development status.
- Other configurable premiums.

## 3.4 Pricing calculation

Create a dedicated PricingCalculationService.

Return:

- Base price.
- Size-based price.
- Premiums.
- Discounts.
- Additional charges.
- Final price.
- Applied pricing rules.

Never recalculate historical bookings using current pricing rules.

A booking must store an immutable pricing snapshot.

## 3.5 Approvals

Support approval for:

- Price overrides.
- Customer-specific discounts.
- Special pricing.
- Pricing exceptions.

### MVC screens

- Pricing rules.
- Rate cards.
- Property pricing.
- Price history.
- Discount configuration.
- Pricing approvals.
- Price calculator.

### API

- GET /api/v1/pricing/rules
- POST /api/v1/pricing/rules
- POST /api/v1/pricing/calculate
- GET /api/v1/properties/{id}/price
- GET /api/v1/pricing/history
- POST /api/v1/pricing/approvals

### Acceptance criteria

- Configurable pricing works.
- Customer-specific pricing works.
- Price calculations are reproducible.
- Existing agreements are unaffected by new pricing.
- Approval workflows are enforced.
