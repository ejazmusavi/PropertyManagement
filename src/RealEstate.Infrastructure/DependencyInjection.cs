using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RealEstate.Application.Administration;
using RealEstate.Application.Audit;
using RealEstate.Application.Documents;
using RealEstate.Infrastructure.Storage;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;
using RealEstate.Application.Users;
using RealEstate.Infrastructure.Identity;
using RealEstate.Infrastructure.Persistence;
using RealEstate.Infrastructure.Persistence.Bootstrap;

namespace RealEstate.Infrastructure;

public static class DependencyInjection
{
    public static void ConfigureMySql(DbContextOptionsBuilder builder, string connectionString, string serverVersion) =>
        builder.UseMySql(connectionString, ServerVersion.Create(Version.Parse(serverVersion), Pomelo.EntityFrameworkCore.MySql.Infrastructure.ServerType.MySql),
            o => o.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName));

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connection = config.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured. Set it via user-secrets or the ConnectionStrings__DefaultConnection environment variable.");

        services.AddOptions<DatabaseOptions>().Bind(config.GetSection(DatabaseOptions.Section));
        services.AddOptions<JwtOptions>().Bind(config.GetSection(JwtOptions.Section)).ValidateDataAnnotations().ValidateOnStart();

        var serverVersion = config.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>()?.ServerVersion ?? "8.4.0";
        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddDbContext<AppDbContext>(o => ConfigureMySql(o, connection, serverVersion));

        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.Password.RequiredLength = 12;
                o.Password.RequireDigit = o.Password.RequireLowercase = o.Password.RequireUppercase = o.Password.RequireNonAlphanumeric = true;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.User.RequireUniqueEmail = true;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IAccessTokenFactory, JwtAccessTokenFactory>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IUserQueryService, UserQueryService>();
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<IAdministrationStore, AdministrationStore>();
        services.AddOptions<FileStorageOptions>().Bind(config.GetSection(FileStorageOptions.Section));
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddSingleton<RealEstate.Application.Payments.IReceiptPdfRenderer, RealEstate.Infrastructure.Documents.ReceiptPdfRenderer>();
        services.AddSingleton<RealEstate.Application.Reporting.IReportExporter, RealEstate.Infrastructure.Documents.ReportExporter>();
        services.AddScoped<IPasswordResetNotifier, LoggingPasswordResetNotifier>();
        services.AddSingleton<RealEstate.Application.Gateway.IPaymentGateway, RealEstate.Infrastructure.Gateway.SandboxPaymentGateway>();
        services.AddSingleton<RealEstate.Application.Gateway.IPaymentGatewayRegistry, RealEstate.Infrastructure.Gateway.PaymentGatewayRegistry>();
        services.AddScoped<DatabaseInitializer>();
        services.AddScoped<AdminBootstrapper>();

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        return services;
    }
}
