using System.ComponentModel.DataAnnotations;

namespace RealEstate.Infrastructure.Identity;

public class JwtOptions
{
    public const string Section = "Jwt";
    [Required] public string Issuer { get; set; } = "RealEstate";
    [Required] public string Audience { get; set; } = "RealEstate.Api";
    /// <summary>HMAC signing key (min 32 chars). Supply via user-secrets or environment variable Jwt__SigningKey; never commit it.</summary>
    [Required, MinLength(32)] public string SigningKey { get; set; } = default!;
    [Range(1, 1440)] public int AccessTokenMinutes { get; set; } = 15;
    [Range(1, 365)] public int RefreshTokenDays { get; set; } = 14;
}

public class DatabaseOptions
{
    public const string Section = "Database";
    public string ServerVersion { get; set; } = "8.4.0";
    public bool MigrateOnStartup { get; set; }
}
