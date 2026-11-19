using RealEstate.Application.Common;
using RealEstate.Domain.Common;

namespace RealEstate.Application.Documents;

public sealed record FileUpload(string FileName, string ContentType, long Length, Stream Content);
public sealed record StoredFileInfo(string Key, long SizeBytes, string Sha256);
public sealed record DownloadedFile(Stream Content, string FileName, string ContentType);

/// <summary>Secure file storage outside the web root. Keys are opaque; files are only served through authorized actions.</summary>
public interface IFileStorage
{
    Task<StoredFileInfo> SaveAsync(string folder, string extension, Stream content, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);
}

public sealed record StoredUpload(StoredFileInfo File, string SafeFileName, string ContentType);

/// <summary>Validates uploads against the company document settings and stores them.</summary>
public class DocumentUploader(IFileStorage storage, IAppDbContext db)
{
    private static readonly Dictionary<string, string> Mime = new()
    {
        ["pdf"] = "application/pdf", ["jpg"] = "image/jpeg", ["jpeg"] = "image/jpeg", ["png"] = "image/png",
        ["doc"] = "application/msword", ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["xls"] = "application/vnd.ms-excel", ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ["txt"] = "text/plain", ["csv"] = "text/csv",
    };

    public async Task<StoredUpload> StoreAsync(FileUpload upload, string folder, CancellationToken ct = default)
    {
        var company = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(db.Companies, ct);
        var maxMb = company?.MaxUploadSizeMb ?? 10;
        var allowed = company?.AllowedExtensions() ?? new HashSet<string> { "pdf", "jpg", "jpeg", "png" };

        if (upload.Length <= 0) throw new DomainException("The file is empty.");
        if (upload.Length > maxMb * 1024L * 1024L) throw new DomainException($"The file exceeds the maximum size of {maxMb} MB.");
        var name = SafeName(upload.FileName);
        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0 || !allowed.Contains(ext))
            throw new DomainException($"Files of type '.{ext}' are not allowed. Allowed: {string.Join(", ", allowed.Order())}.");

        var stored = await storage.SaveAsync(folder, "." + ext, upload.Content, ct);
        return new StoredUpload(stored, name, Mime.GetValueOrDefault(ext, "application/octet-stream"));
    }

    public static string SafeName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/'));
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim();
        if (name.Length > 200) name = name[^200..];
        return string.IsNullOrWhiteSpace(name) ? "file" : name;
    }
}
