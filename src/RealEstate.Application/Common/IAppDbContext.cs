using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using RealEstate.Domain.Entities;

namespace RealEstate.Application.Common;

/// <summary>Persistence abstraction used by application services; implemented by the Infrastructure DbContext.</summary>
public interface IAppDbContext
{
    DbSet<Company> Companies { get; }
    DbSet<Permission> Permissions { get; }
    DbSet<RolePermission> RolePermissions { get; }
    DbSet<UserProject> UserProjects { get; }
    DbSet<Project> Projects { get; }
    DbSet<ProjectAddress> ProjectAddresses { get; }
    DbSet<ProjectDocument> ProjectDocuments { get; }
    DbSet<ProjectManagerAssignment> ProjectManagerAssignments { get; }
    DbSet<ProjectAreaHistory> ProjectAreaHistories { get; }
    DbSet<ProjectStatusHistory> ProjectStatusHistories { get; }
    DbSet<LandParcel> LandParcels { get; }
    DbSet<LandAcquisition> LandAcquisitions { get; }
    DbSet<LandAdjustment> LandAdjustments { get; }
    DbSet<LandDistribution> LandDistributions { get; }
    DbSet<LandDistributionHistory> LandDistributionHistories { get; }
    DbSet<LandDocument> LandDocuments { get; }
    DbSet<PropertyCategory> PropertyCategories { get; }
    DbSet<PropertyType> PropertyTypes { get; }
    DbSet<ProjectBlock> ProjectBlocks { get; }
    DbSet<ProjectSector> ProjectSectors { get; }
    DbSet<ProjectStreet> ProjectStreets { get; }
    DbSet<Property> Properties { get; }
    DbSet<PropertyDimension> PropertyDimensions { get; }
    DbSet<PropertyFeature> PropertyFeatures { get; }
    DbSet<PropertyDocument> PropertyDocuments { get; }
    DbSet<PropertyStatusHistory> PropertyStatusHistories { get; }
    DbSet<PropertyReservation> PropertyReservations { get; }
    DatabaseFacade Database { get; }
    EntityEntry<T> Entry<T>(T entity) where T : class;
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Optimistic concurrency conflict (another user changed the record first).</summary>
public class ConcurrencyException() : Domain.Common.DomainException("The record was changed by another user. Reload and try again.");
