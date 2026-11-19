using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RealEstate.Infrastructure.Persistence;

/// <summary>Design-time factory for `dotnet ef`. Uses env var REALESTATE_CONNECTION, or a placeholder (migration scaffolding needs no live database).</summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("REALESTATE_CONNECTION")
                 ?? "Server=localhost;Port=3306;Database=realestate_design;User=design;Password=design";
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        DependencyInjection.ConfigureMySql(builder, cs, "8.4.0");
        return new AppDbContext(builder.Options);
    }
}
