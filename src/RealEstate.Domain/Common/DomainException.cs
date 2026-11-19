namespace RealEstate.Domain.Common;

/// <summary>Business-rule violation. The message is safe to show to users.</summary>
public class DomainException(string message) : Exception(message);
