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
