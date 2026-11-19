using RealEstate.Application.Documents;

namespace RealEstate.Api.Controllers;

internal static class ApiHelpers
{
    public static FileUpload ToUpload(this IFormFile file) => new(file.FileName, file.ContentType, file.Length, file.OpenReadStream());
}
