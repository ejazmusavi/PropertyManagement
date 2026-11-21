using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Gis;

namespace RealEstate.Domain.Tests;

public class Phase11NotificationRulesTests
{
    [Fact]
    public void Render_ReplacesKnownPlaceholders_AndBlanksMissingOnes()
    {
        var data = new Dictionary<string, string> { ["CustomerName"] = "Ali", ["Amount"] = "Rs 1,000.00" };
        Assert.Equal("Dear Ali, pay Rs 1,000.00 by .", NotificationRules.Render("Dear {{CustomerName}}, pay {{ Amount }} by {{DueDate}}.", data));
        Assert.Equal("plain", NotificationRules.Render("plain", data));
    }

    [Fact]
    public void UnknownPlaceholders_AreReported()
    {
        Assert.Empty(NotificationRules.UnknownPlaceholders("Hi {{CustomerName}} {{Amount}}"));
        Assert.Equal(["Secret", "Other"], NotificationRules.UnknownPlaceholders("{{Secret}} {{CustomerName}} {{Other}} {{Secret}}"));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 60)]
    [InlineData(5, 360)]
    [InlineData(9, 360)]
    public void Backoff_GrowsAndIsCapped(int attempts, int minutes) => Assert.Equal(TimeSpan.FromMinutes(minutes), NotificationRules.Backoff(attempts));

    [Fact]
    public void ResolveChannels_InAppAlwaysOn_OthersOnUnlessSwitchedOff()
    {
        NotificationChannel[] all = [NotificationChannel.InApp, NotificationChannel.Email, NotificationChannel.Sms, NotificationChannel.WhatsApp, NotificationChannel.Push];
        // no preferences: everything on
        Assert.Equal(all, NotificationRules.ResolveChannels(NotificationType.InstallmentUpcoming, all, []));
        // SMS off for all types
        var off = new[] { (NotificationChannel.Sms, 0, false) };
        Assert.DoesNotContain(NotificationChannel.Sms, NotificationRules.ResolveChannels(NotificationType.InstallmentUpcoming, all, off));
        // a specific choice wins over the general one in both directions
        var specificOn = new[] { (NotificationChannel.Sms, 0, false), (NotificationChannel.Sms, (int)NotificationType.InstallmentOverdue, true) };
        Assert.Contains(NotificationChannel.Sms, NotificationRules.ResolveChannels(NotificationType.InstallmentOverdue, all, specificOn));
        Assert.DoesNotContain(NotificationChannel.Sms, NotificationRules.ResolveChannels(NotificationType.InstallmentUpcoming, all, specificOn));
        // the in-app channel cannot be switched off
        var inAppOff = new[] { (NotificationChannel.InApp, 0, false) };
        Assert.Contains(NotificationChannel.InApp, NotificationRules.ResolveChannels(NotificationType.CompanyAnnouncement, all, inAppOff));
    }

    [Fact]
    public void ResolveChannels_SkipsInApp_WhenAnEarlierPhaseAlreadyWritesIt()
    {
        NotificationChannel[] all = [NotificationChannel.InApp, NotificationChannel.Email];
        Assert.Equal([NotificationChannel.Email], NotificationRules.ResolveChannels(NotificationType.PaymentConfirmation, all, []));
    }

    [Fact]
    public void Aggregate_ReflectsDeliveryOutcomes()
    {
        Assert.Equal(NotificationStatus.Completed, NotificationRules.Aggregate([]));
        Assert.Equal(NotificationStatus.Pending, NotificationRules.Aggregate([DeliveryStatus.Sent, DeliveryStatus.Retrying]));
        Assert.Equal(NotificationStatus.Pending, NotificationRules.Aggregate([DeliveryStatus.Pending]));
        Assert.Equal(NotificationStatus.Completed, NotificationRules.Aggregate([DeliveryStatus.Sent, DeliveryStatus.Skipped]));
        Assert.Equal(NotificationStatus.Completed, NotificationRules.Aggregate([DeliveryStatus.Skipped]));
        Assert.Equal(NotificationStatus.PartiallyFailed, NotificationRules.Aggregate([DeliveryStatus.Sent, DeliveryStatus.Failed]));
        Assert.Equal(NotificationStatus.Failed, NotificationRules.Aggregate([DeliveryStatus.Failed, DeliveryStatus.Failed, DeliveryStatus.Skipped]));
        Assert.Equal(NotificationStatus.Cancelled, NotificationRules.Aggregate([DeliveryStatus.Cancelled, DeliveryStatus.Cancelled]));
    }

    [Fact]
    public void OwnerKey_IdentifiesCustomerOrUser_AndRejectsNeither()
    {
        var u = Guid.NewGuid();
        Assert.Equal("C:15", NotificationRules.OwnerKey(15, null));
        Assert.Equal($"U:{u:N}", NotificationRules.OwnerKey(null, u));
        Assert.Throws<DomainException>(() => NotificationRules.OwnerKey(null, null));
    }
}

public class Phase11GeoJsonTests
{
    private const string Square = """{"type":"Polygon","coordinates":[[[73.0,33.0],[73.1,33.0],[73.1,33.1],[73.0,33.1],[73.0,33.0]]]}""";

    [Fact]
    public void Polygon_IsSummarized()
    {
        var s = GeoJsonBoundary.Parse(Square, SpatialCoordinateKind.Geographic);
        Assert.Equal("Polygon", s.GeometryType);
        Assert.Equal(5, s.VertexCount);
        Assert.Equal(73.0m, s.MinX); Assert.Equal(33.0m, s.MinY); Assert.Equal(73.1m, s.MaxX); Assert.Equal(33.1m, s.MaxY);
    }

    [Fact]
    public void Feature_AndMultiPolygon_AreAccepted()
    {
        Assert.Equal("Polygon", GeoJsonBoundary.Parse($$"""{"type":"Feature","properties":{},"geometry":{{Square}}}""", SpatialCoordinateKind.Geographic).GeometryType);
        var multi = """{"type":"MultiPolygon","coordinates":[[[[0,0],[1,0],[1,1],[0,0]]],[[[5,5],[6,5],[6,6],[5,5]]]]}""";
        var s = GeoJsonBoundary.Parse(multi, SpatialCoordinateKind.Projected);
        Assert.Equal("MultiPolygon", s.GeometryType);
        Assert.Equal(8, s.VertexCount);
        Assert.Equal(6m, s.MaxX);
    }

    [Fact]
    public void ProjectedCoordinates_MayExceedDegreeRange_ButGeographicMayNot()
    {
        var utm = """{"type":"Polygon","coordinates":[[[500000,3700000],[500100,3700000],[500100,3700100],[500000,3700000]]]}""";
        Assert.Equal(4, GeoJsonBoundary.Parse(utm, SpatialCoordinateKind.Projected).VertexCount);
        var ex = Assert.Throws<DomainException>(() => GeoJsonBoundary.Parse(utm, SpatialCoordinateKind.Geographic));
        Assert.Contains("longitude", ex.Message);
        Assert.Throws<DomainException>(() => GeoJsonBoundary.Parse("""{"type":"Polygon","coordinates":[[[73,95],[73.1,95],[73.1,96],[73,95]]]}""", SpatialCoordinateKind.Geographic));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"type":"Point","coordinates":[1,2]}""")]
    [InlineData("""{"type":"Polygon"}""")]
    [InlineData("""{"type":"Polygon","coordinates":[]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1]]]}""")]               // fewer than four positions
    [InlineData("""{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1]]]}""")]          // not closed
    [InlineData("""{"type":"Polygon","coordinates":[[["a",0],[1,0],[1,1],[0,0]]]}""")]        // not numbers
    [InlineData("""{"type":"Polygon","coordinates":[[[0],[1,0],[1,1],[0,0]]]}""")]            // too few ordinates
    [InlineData("""{"type":"MultiPolygon","coordinates":[]}""")]
    [InlineData("""{"type":"Feature","geometry":null}""")]
    public void InvalidGeometry_IsRejected(string? json) => Assert.Throws<DomainException>(() => GeoJsonBoundary.Parse(json, SpatialCoordinateKind.Projected));

    [Fact]
    public void OversizedGeometry_IsRejected() =>
        Assert.Throws<DomainException>(() => GeoJsonBoundary.Parse(new string(' ', GeoJsonBoundary.MaxLength + 1), SpatialCoordinateKind.Projected));
}

public class Phase11BoundaryAndCatalogTests
{
    [Fact]
    public void BoundaryStatus_MovesOnlyAlongTheWorkflow()
    {
        var b = new ProjectBoundary();
        Assert.Equal(BoundaryStatus.Draft, b.Status);
        Assert.Throws<DomainException>(() => b.Move(BoundaryStatus.Superseded));
        b.Move(BoundaryStatus.Verified);
        Assert.Throws<DomainException>(() => b.Move(BoundaryStatus.Rejected));
        Assert.Throws<DomainException>(() => b.Move(BoundaryStatus.Draft));
        b.Move(BoundaryStatus.Superseded);
        Assert.Throws<DomainException>(() => b.Move(BoundaryStatus.Verified));

        var r = new PropertyBoundary();
        r.Move(BoundaryStatus.Rejected);
        Assert.Throws<DomainException>(() => r.Move(BoundaryStatus.Verified));
    }

    [Fact]
    public void Permissions_AreCataloguedAndGrantedToTheRightRoles()
    {
        string[] codes = [Permissions.NotificationsView, Permissions.NotificationsManage, Permissions.NotificationsSend, Permissions.JobsView, Permissions.JobsManage, Permissions.GisView, Permissions.GisManage, Permissions.GisVerify];
        foreach (var c in codes) Assert.Contains(Permissions.All, p => p.Code == c);
        Assert.Equal(Permissions.All.Count, Permissions.All.Select(p => p.Code).Distinct().Count());
        var director = DefaultRoles.All.First(r => r.Name == RoleNames.Director).Permissions;
        foreach (var c in codes) Assert.Contains(c, director);
        var pm = DefaultRoles.All.First(r => r.Name == RoleNames.ProjectManager).Permissions;
        Assert.Contains(Permissions.GisManage, pm);
        Assert.DoesNotContain(Permissions.GisVerify, pm);
        Assert.DoesNotContain(Permissions.JobsManage, DefaultRoles.All.First(r => r.Name == RoleNames.Accountant).Permissions);
    }
}
