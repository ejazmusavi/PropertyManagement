using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using RealEstate.Application.Auth;
using RealEstate.Application.Users;

namespace RealEstate.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<LoginRequestValidator>(includeInternalTypes: false);
        services.AddScoped<AuthService>();
        services.AddScoped<UserService>();
        return services;
    }
}
