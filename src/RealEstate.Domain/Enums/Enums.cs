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
