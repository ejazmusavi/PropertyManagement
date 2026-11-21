using RealEstate.Domain.Common;

namespace RealEstate.Domain.Tests;

public class Phase09DomainTests
{
    [Fact]
    public void ReportPermissions_AreInTheCatalog_WithoutDuplicates()
    {
        var codes = Permissions.All.Select(p => p.Code).ToList();
        Assert.Contains(Permissions.ReportsView, codes);
        Assert.Contains(Permissions.ReportsExport, codes);
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(new[] { Permissions.ReportsView, Permissions.ReportsExport }, c => Assert.Equal("Reports", Permissions.All.First(p => p.Code == c).Module));
    }

    [Theory]
    [InlineData("Director", true, true)]
    [InlineData("Project Manager", true, true)]
    [InlineData("Sales Manager", true, true)]
    [InlineData("Accountant", true, true)]
    [InlineData("Cashier", false, false)]
    [InlineData("Recovery Officer", false, false)]
    [InlineData("Inventory Manager", false, false)]
    [InlineData("Customer Support", false, false)]
    public void DefaultRoles_GetReportPermissionsAsDesigned(string role, bool view, bool export)
    {
        var granted = DefaultRoles.All.First(r => r.Name == role).Permissions;
        Assert.Equal(view, granted.Contains(Permissions.ReportsView));
        Assert.Equal(export, granted.Contains(Permissions.ReportsExport));
    }
}
