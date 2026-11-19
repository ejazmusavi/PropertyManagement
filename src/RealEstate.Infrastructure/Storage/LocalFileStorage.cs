using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using RealEstate.Application.Documents;

namespace RealEstate.Infrastructure.Storage;

public class FileStorageOptions
{
    public const string Section = "FileStorage";
    /// <summary>Directory outside the web root. Relative paths resolve against the application's base directory.</summary>
    public string RootPath { get; set; } = "App_Data/storage";
}

/// <summary>Stores files on local disk under a configured root outside wwwroot. Keys are generated server-side; callers never supply paths.</summary>
public class LocalFileStorage(IOptions<FileStorageOptions> options) : IFileStorage
{
    private readonly string root = Path.GetFullPath(
        Path.IsPathRooted(options.Value.RootPath) ? options.Value.RootPath : Path.Combine(AppContext.BaseDirectory, options.Value.RootPath));

    public async Task<StoredFileInfo> SaveAsync(string folder, string extension, Stream content, CancellationToken ct = default)
    {
        var key = $"{folder.Trim('/').Replace('\\', '/')}/{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var sha = SHA256.Create();
        long size = 0;
        await using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                size += read;
            }
            sha.TransformFinalBlock([], 0, 0);
        }
        return new StoredFileInfo(key, size, Convert.ToHexString(sha.Hash!).ToLowerInvariant());
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var path = Resolve(key);
        if (!File.Exists(path)) throw new FileNotFoundException("Stored file is missing.");
        return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
    }

    private string Resolve(string key)
    {
        var full = Path.GetFullPath(Path.Combine(root, key));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid storage key.");
        return full;
    }
}
