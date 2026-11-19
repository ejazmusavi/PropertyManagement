using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Tests;

public class ProjectTests
{
    private static Project New(decimal area = 100m) => Project.Create(" ab-1 ", "Serene", null, "Skardu", null, null, null, area, MeasurementUnit.Kanal, null);

    [Fact]
    public void Create_NormalisesCode_StartsInPlanning_AndAreasMatch()
    {
        var p = New(30.5m);
        Assert.Equal("AB-1", p.ProjectCode);
        Assert.Equal(ProjectStatus.Planning, p.Status);
        Assert.Equal(30.5m, p.InitialArea);
        Assert.Equal(p.InitialArea, p.CurrentArea);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1000000000)]
    [InlineData(1.00001)]
    public void Create_RejectsInvalidArea(decimal area) => Assert.Throws<DomainException>(() => New(area));

    [Fact]
    public void Create_RequiresCodeNameAndCity()
    {
        Assert.Throws<DomainException>(() => Project.Create("", "n", null, "c", null, null, null, 1, MeasurementUnit.Acre, null));
        Assert.Throws<DomainException>(() => Project.Create("c", " ", null, "c", null, null, null, 1, MeasurementUnit.Acre, null));
        Assert.Throws<DomainException>(() => Project.Create("c", "n", null, "", null, null, null, 1, MeasurementUnit.Acre, null));
    }

    [Fact]
    public void Dates_CompletionCannotPrecedeStart() =>
        Assert.Throws<DomainException>(() => Project.Create("c", "n", null, "c", null, new DateOnly(2026, 5, 1), new DateOnly(2026, 4, 1), 1, MeasurementUnit.Acre, null));

    [Theory]
    [InlineData(ProjectStatus.Planning, ProjectStatus.Active, true)]
    [InlineData(ProjectStatus.Active, ProjectStatus.OnHold, true)]
    [InlineData(ProjectStatus.OnHold, ProjectStatus.Active, true)]
    [InlineData(ProjectStatus.Active, ProjectStatus.Completed, true)]
    [InlineData(ProjectStatus.Completed, ProjectStatus.Archived, true)]
    [InlineData(ProjectStatus.Planning, ProjectStatus.Completed, false)]
    [InlineData(ProjectStatus.Completed, ProjectStatus.Active, false)]
    [InlineData(ProjectStatus.Archived, ProjectStatus.Active, false)]
    [InlineData(ProjectStatus.Active, ProjectStatus.Active, false)]
    public void StatusTransitions(ProjectStatus from, ProjectStatus to, bool allowed) => Assert.Equal(allowed, Project.CanTransition(from, to));

    [Fact]
    public void ChangeStatus_RequiresReason_AndReturnsHistory()
    {
        var p = New();
        Assert.Throws<DomainException>(() => p.ChangeStatus(ProjectStatus.Active, " ", DateTime.UtcNow, null, null));
        var h = p.ChangeStatus(ProjectStatus.Active, "Approved", DateTime.UtcNow, Guid.NewGuid(), "admin");
        Assert.Equal(ProjectStatus.Planning, h.FromStatus);
        Assert.Equal(ProjectStatus.Active, h.ToStatus);
        Assert.Equal(ProjectStatus.Active, p.Status);
        Assert.Throws<DomainException>(() => p.ChangeStatus(ProjectStatus.Planning, "back", DateTime.UtcNow, null, null));
    }

    [Fact]
    public void ApplyAreaChange_UpdatesCurrentArea_ButNeverBelowZero_OrWhenClosed()
    {
        var p = New(100m);
        p.ApplyAreaChange(25m);
        p.ApplyAreaChange(-10m);
        Assert.Equal(115m, p.CurrentArea);
        Assert.Throws<DomainException>(() => p.ApplyAreaChange(-115m));
        p.ChangeStatus(ProjectStatus.Active, "go", DateTime.UtcNow, null, null);
        p.ChangeStatus(ProjectStatus.Completed, "done", DateTime.UtcNow, null, null);
        Assert.Throws<DomainException>(() => p.ApplyAreaChange(1m));
        Assert.Equal(115m, p.CurrentArea);
    }
}

public class LandRulesTests
{
    [Fact]
    public void Unclassified_IsExplicitRemainder() => Assert.Equal(20m, LandRules.ComputeUnclassified(100m, [50m, 10m, 20m]));

    [Fact]
    public void FullyClassified_HasZeroUnclassified() => Assert.Equal(0m, LandRules.ComputeUnclassified(100m, [60m, 40m]));

    [Fact]
    public void Distribution_CannotExceedProjectArea() => Assert.Throws<DomainException>(() => LandRules.ComputeUnclassified(100m, [60m, 40.0001m]));

    [Fact]
    public void Distribution_RejectsNegativeAndOverPrecise()
    {
        Assert.Throws<DomainException>(() => LandRules.ComputeUnclassified(100m, [-1m]));
        Assert.Throws<DomainException>(() => LandRules.ComputeUnclassified(100m, [1.00001m]));
    }

    [Fact]
    public void ClassifiedCategories_ExcludeUnclassified() => Assert.DoesNotContain(LandCategory.Unclassified, LandRules.ClassifiedCategories);
}

public class StoredDocumentTests
{
    private sealed class Doc : StoredDocument;

    [Fact]
    public void MarkDeleted_RequiresReason_AndOnlyOnce()
    {
        var d = new Doc();
        Assert.Throws<DomainException>(() => d.MarkDeleted(DateTime.UtcNow, "u", " "));
        d.MarkDeleted(DateTime.UtcNow, "u", "Wrong file");
        Assert.True(d.IsDeleted);
        Assert.Equal("Wrong file", d.DeleteReason);
        Assert.Throws<DomainException>(() => d.MarkDeleted(DateTime.UtcNow, "u", "again"));
    }
}

public class Phase1PermissionTests
{
    [Fact]
    public void DefaultRoles_OnlyUseCataloguedPermissions_AndCoverTheSpecifiedRoles()
    {
        var codes = Permissions.All.Select(p => p.Code).ToHashSet();
        Assert.All(DefaultRoles.All.SelectMany(r => r.Permissions), c => Assert.Contains(c, codes));
        var names = DefaultRoles.All.Select(r => r.Name).Append(RoleNames.Administrator).ToHashSet();
        foreach (var n in new[] { "Administrator", "Director", "Project Manager", "Sales Manager", "Accountant", "Cashier", "Recovery Officer", "Inventory Manager", "Customer Support" })
            Assert.Contains(n, names);
    }

    [Fact]
    public void OnlyDirectorsAndAdmins_SeeAllProjects() =>
        Assert.Equal([RoleNames.Director], DefaultRoles.All.Where(r => r.Permissions.Contains(Permissions.ProjectsAccessAll)).Select(r => r.Name));

    [Fact]
    public void CompanyDefaults_AreSensible()
    {
        var c = new Company("X");
        Assert.Equal(3, c.CurrencyCode.Length);
        Assert.InRange(c.FiscalYearStartMonth, 1, 12);
        Assert.Contains("pdf", c.AllowedExtensions());
    }
}
