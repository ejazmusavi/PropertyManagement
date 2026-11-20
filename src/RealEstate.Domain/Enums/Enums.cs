namespace RealEstate.Domain.Enums;

public enum ProjectStatus { Planning = 1, Active = 2, OnHold = 3, Completed = 4, Archived = 5 }

public enum MeasurementUnit { Kanal = 1, Marla = 2, SquareFeet = 3, SquareYard = 4, SquareMeter = 5, Acre = 6 }

public enum LandParcelType { Original = 1, Acquired = 2 }

public enum LandAdjustmentType { Addition = 1, Reduction = 2 }

/// <summary>Land distribution categories. Saleable area = Residential + Commercial. Unclassified is the explicit remainder.</summary>
public enum LandCategory
{
    Residential = 1, Commercial = 2, Roads = 3, Parks = 4, OpenSpaces = 5, Reserved = 6, OtherFacilities = 7, Unclassified = 99
}

public enum AreaChangeSource { Initial = 1, Acquisition = 2, Adjustment = 3 }

public enum ProjectDocumentType { Layout = 1, Approval = 2, Legal = 3, Financial = 4, Other = 5 }

public enum LandDocumentType { TitleDeed = 1, Registry = 2, Survey = 3, Mutation = 4, Agreement = 5, AdjustmentSupport = 6, Other = 7 }

public enum RoundingMode { Nearest = 1, Bankers = 2, Up = 3, Down = 4 }

public enum PropertyForm { Plot = 1, House = 2, Apartment = 3, Commercial = 4, Other = 5 }

public enum DevelopmentStatus { Undeveloped = 1, UnderDevelopment = 2, Developed = 3 }

public enum PropertyStatus
{
    Available = 1, Reserved = 2, Booked = 3, Sold = 4, Allocated = 5,
    PossessionReady = 6, PossessionDelivered = 7, Transferred = 8, Cancelled = 9
}

public enum ReservationStatus { Active = 1, Released = 2, Expired = 3, Converted = 4 }

public enum PropertyDocumentType { Plan = 1, Ownership = 2, Allotment = 3, Photo = 4, Legal = 5, Other = 6 }

public enum ZoneKind { Block = 1, Sector = 2, Street = 3 }

// ---------- Phase 3: pricing ----------
/// <summary>Where a pricing rule applies. The numeric order IS the precedence policy: a higher value overrides a lower one.</summary>
public enum PricingScope { Project = 1, Block = 2, Street = 3, PropertyCategory = 4, Property = 5, Customer = 6 }

public enum PricingRuleKind { BaseRate = 1, Premium = 2, AdditionalCharge = 3 }

public enum PricingFactor { Size = 1, Location = 2, Corner = 3, RoadFacing = 4, ParkFacing = 5, Commercial = 6, DevelopmentStatus = 7, Other = 8 }

/// <summary>PerUnit = amount per measurement unit of size; FixedAmount = flat money; Percentage = percent of the size-based price (discounts: of price before discount).</summary>
public enum PricingMethod { PerUnit = 1, FixedAmount = 2, Percentage = 3 }

public enum PricingRuleStatus { PendingApproval = 1, Active = 2, Retired = 3, Rejected = 4 }

public enum PricingConditionField { MinSize = 1, MaxSize = 2, PropertyType = 3, Sector = 4, DevelopmentStatus = 5, Corner = 6, MainRoad = 7, Park = 8, MinFloor = 9, MaxFloor = 10 }

public enum PricingApprovalType { PriceOverride = 1, CustomerDiscount = 2, SpecialPricing = 3, PricingException = 4 }

public enum PricingApprovalStatus { Pending = 1, Approved = 2, Rejected = 3, Cancelled = 4 }

public enum AdjustmentKind { Override = 1, Discount = 2, Surcharge = 3 }

public enum AdjustmentStatus { Active = 1, Revoked = 2 }

public enum PriceChangeSource { Published = 1, ApprovalApplied = 2, AdjustmentRevoked = 3 }

// ---------- Phase 4: customers, bookings and agreements ----------
public enum CustomerStatus { Active = 1, Inactive = 2, Blocked = 3 }

public enum IdentityDocumentType { Cnic = 1, Passport = 2 }

public enum AddressType { Home = 1, Office = 2, Mailing = 3, Permanent = 4 }

public enum CustomerRelationshipType { Spouse = 1, Parent = 2, Child = 3, Sibling = 4, BusinessPartner = 5, Other = 6 }

public enum CustomerDocumentType { Cnic = 1, Passport = 2, Photo = 3, ProofOfAddress = 4, Other = 5 }

public enum PaymentMode { Cash = 1, Installment = 2 }

public enum BookingStatus { Draft = 1, PendingApproval = 2, Confirmed = 3, Rejected = 4, Cancelled = 5 }

public enum BookingChargeType { Registration = 1, Transfer = 2, Development = 3, Tax = 4, Other = 5 }

public enum BookingDecision { Approved = 1, Rejected = 2 }

public enum RefundStatus { Requested = 1, Approved = 2, Rejected = 3 }

public enum AgreementStatus { Active = 1, Cancelled = 2 }

public enum AgreementDocumentType { Signed = 1, Supporting = 2, Other = 3 }

public enum AmendmentType
{
    Transfer = 1, OwnershipChange = 2,
    // Phase 5 schedule amendments (spec 5.6)
    Reschedule = 3, Deferment = 4, InstallmentReduction = 5, AdditionalInstallments = 6, DueDateChange = 7, PenaltyChange = 8, Waiver = 9, AdditionalCharge = 10
}

// ---------- Phase 5: installment and payment agreement engine ----------
public enum AmendmentStatus { Pending = 1, Approved = 2, Rejected = 3, Cancelled = 4 }

/// <summary>Plan scope (spec 5.2).</summary>
public enum InstallmentPlanType { StandardProject = 1, PropertyCategory = 2, PropertySpecific = 3, CustomerSpecific = 4, Cash = 5 }

public enum InstallmentPlanStatus { Draft = 1, Active = 2, Retired = 3 }

/// <summary>The charge components of spec 5.3.</summary>
public enum ChargeComponentKind
{
    DownPayment = 1, ConfirmationPayment = 2, MonthlyInstallment = 3, QuarterlyInstallment = 4, HalfYearlyInstallment = 5, AnnualInstallment = 6,
    PossessionPayment = 7, DevelopmentCharges = 8, UtilityCharges = 9, GovernmentFees = 10, CustomCharge = 11
}

/// <summary>What a charge pays for. SaleConsideration components split the booking's total payable; the others are separate charges added to the agreement.</summary>
public enum ChargeCategory { SaleConsideration = 1, DevelopmentCharges = 2, UtilityCharges = 3, GovernmentFees = 4, OtherCharges = 5 }

/// <summary>Exclusive: tax is added on top of the amount; Inclusive: the amount already contains the tax (portion shown for information).</summary>
public enum TaxTreatment { NotApplicable = 1, Exempt = 2, Inclusive = 3, Exclusive = 4 }

/// <summary>OneTime: a single due date. Monthly..Annual: regular intervals. CustomDates: explicit dates (equal split, or explicit amounts = irregular schedule).</summary>
public enum ScheduleFrequency { OneTime = 1, Monthly = 2, Quarterly = 3, HalfYearly = 4, Annual = 5, CustomDates = 6 }

/// <summary>How a plan component's amount is determined when the plan is applied to an agreement.</summary>
public enum AmountBasis { FixedAmount = 1, PercentOfPrice = 2, Remainder = 3 }

public enum AgreementChargeStatus { Active = 1, Cancelled = 2 }

public enum PaymentScheduleStatus { Active = 1, Superseded = 2, Cancelled = 3 }

public enum ScheduleItemStatus { Pending = 1, PartiallyPaid = 2, Paid = 3, Waived = 4 }

public enum LateFeeMethod { FixedOneTime = 1, PercentOneTime = 2, FixedPerDay = 3, PercentPerDay = 4, FixedPerMonth = 5, PercentPerMonth = 6 }

public enum WaiverKind { Principal = 1, LateFee = 2 }

public enum WaiverStatus { Pending = 1, Approved = 2, Rejected = 3, Cancelled = 4 }

/// <summary>Operation recorded for each schedule line changed by an amendment.</summary>
public enum ScheduleChangeOperation { Added = 1, Removed = 2, AmountChanged = 3, DueDateChanged = 4, GraceChanged = 5, Waived = 6, Settled = 7 }

/// <summary>Where the amount removed from an installment goes (the total must still reconcile).</summary>
public enum ReductionRedistribution { SpreadOverRemaining = 1, AddToLast = 2, NewInstallment = 3 }

// ---------- Phase 6: payments, allocation and customer ledgers ----------
/// <summary>The payment methods of spec 6.2.</summary>
public enum PaymentMethodKind { Cash = 1, BankTransfer = 2, Cheque = 3, OnlineTransfer = 4, Card = 5, PaymentGateway = 6 }

/// <summary>Office = recorded by staff from money received; CustomerSubmitted = proof submitted by (or for) the customer, always verified first.</summary>
public enum PaymentSource { Office = 1, CustomerSubmitted = 2 }

/// <summary>PendingVerification -> PendingApproval -> Posted -> Reversed; PendingVerification/PendingApproval -> Rejected.</summary>
public enum PaymentStatus { PendingVerification = 1, PendingApproval = 2, Posted = 3, Rejected = 4, Reversed = 5 }

/// <summary>How a payment is applied to the agreement (spec 6.4).</summary>
public enum AllocationMode { OldestDueFirst = 1, Manual = 2, SpecificInstallment = 3, ChargeSpecific = 4, Advance = 5, Unallocated = 6 }

/// <summary>Installment = applied to a schedule line; Advance = held as advance against the agreement; Refund = paid back to the customer.</summary>
public enum AllocationKind { Installment = 1, Advance = 2, Refund = 3 }

public enum AllocationStatus { Active = 1, Reversed = 2, Released = 3 }

public enum VerificationResult { Verified = 1, Rejected = 2 }

public enum PaymentRefundStatus { Requested = 1, Approved = 2, Rejected = 3, Paid = 4, Cancelled = 5 }

/// <summary>Agreement/customer ledger entry types. Debit increases what the customer owes; credit decreases it.</summary>
public enum LedgerEntryType
{
    InstallmentDue = 1, DueAdjustment = 2, PaymentReceived = 3, PaymentReversed = 4, Waiver = 5, WaiverAdjustment = 6, RefundPaid = 7, BalanceTransferIn = 8, BalanceTransferOut = 9
}

public enum AccountingEventType { PaymentPosted = 1, PaymentReversed = 2, RefundPaid = 3 }

public enum AccountingPostingStatus { Pending = 1, Posted = 2, Failed = 3 }

public enum RecoveryAssignmentStatus { Active = 1, Closed = 2 }

public enum FollowUpMethod { Call = 1, Visit = 2, Sms = 3, Email = 4, Letter = 5, Meeting = 6, Other = 7 }

public enum FollowUpOutcome { Contacted = 1, NoAnswer = 2, PromiseToPay = 3, Disputed = 4, PaymentReceived = 5, WrongContact = 6, Other = 7 }
