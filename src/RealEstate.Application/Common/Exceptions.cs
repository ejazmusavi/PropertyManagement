using RealEstate.Domain.Common;

namespace RealEstate.Application.Common;

public class NotFoundException(string message) : DomainException(message);

public class ForbiddenException(string message = "You do not have permission to perform this action.") : DomainException(message);

public class RequestValidationException(IDictionary<string, string[]> errors) : DomainException("One or more validation errors occurred.")
{
    public IDictionary<string, string[]> Errors { get; } = errors;
}
