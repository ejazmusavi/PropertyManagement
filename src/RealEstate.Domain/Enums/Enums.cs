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
