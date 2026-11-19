using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using RealEstate.Application.Auth;
using RealEstate.Application.Users;
using RealEstate.Application.Administration;
using RealEstate.Application.Common;
using RealEstate.Application.Companies;
using RealEstate.Application.Documents;
using RealEstate.Application.LandManagement;
using RealEstate.Application.Projects;

namespace RealEstate.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<LoginRequestValidator>(includeInternalTypes: false);
        services.AddScoped<AuthService>();
        services.AddScoped<UserService>();
        services.AddScoped<ProjectAccessService>();
        services.AddScoped<DocumentUploader>();
        services.AddScoped<CompanyService>();
        services.AddScoped<ProjectService>();
        services.AddScoped<LandService>();
        services.AddScoped<UserAdministrationService>();
        services.AddScoped<RoleService>();
        services.AddScoped<ProjectAccessAdminService>();
        return services;
    }
}
