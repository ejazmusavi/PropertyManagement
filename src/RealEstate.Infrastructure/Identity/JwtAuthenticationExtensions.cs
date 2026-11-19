using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace RealEstate.Infrastructure.Identity;

public static class JwtAuthenticationExtensions
{
    /// <summary>Adds JWT bearer authentication for the API host.</summary>
    public static AuthenticationBuilder AddRealEstateJwtBearer(this AuthenticationBuilder auth) =>
        auth.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, _ => { });

    public static IServiceCollection ConfigureRealEstateJwtBearer(this IServiceCollection services)
    {
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((o, jwt) =>
            {
                var j = jwt.Value;
                o.MapInboundClaims = false; // claims are issued with ClaimTypes.* names already
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = j.Issuer,
                    ValidateAudience = true,
                    ValidAudience = j.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(j.SigningKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = ClaimTypes.Name,
                    RoleClaimType = ClaimTypes.Role,
                };
            });
        return services;
    }
}
