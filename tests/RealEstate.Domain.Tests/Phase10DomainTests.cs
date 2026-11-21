using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Tests;

public class Phase10DomainTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void GatewayTransaction_StartsInitiated_AndFollowsTheTransitionTable()
    {
        var t = new GatewayTransaction();
        Assert.Equal(GatewayTransactionStatus.Initiated, t.Status);
        t.Move(GatewayTransactionStatus.Pending, Now);
        Assert.Null(t.CompletedAtUtc);
        t.Move(GatewayTransactionStatus.Succeeded, Now);
        Assert.Equal(Now, t.CompletedAtUtc);
        t.Move(GatewayTransactionStatus.Succeeded, Now.AddHours(1)); // same status: no change
        Assert.Equal(Now, t.CompletedAtUtc);
        t.Move(GatewayTransactionStatus.Refunded, Now.AddHours(2));
        Assert.Equal(GatewayTransactionStatus.Refunded, t.Status);
    }

    [Theory]
    [InlineData(GatewayTransactionStatus.Failed, GatewayTransactionStatus.Succeeded)]
    [InlineData(GatewayTransactionStatus.Cancelled, GatewayTransactionStatus.Succeeded)]
    [InlineData(GatewayTransactionStatus.Pending, GatewayTransactionStatus.Refunded)]
    [InlineData(GatewayTransactionStatus.Refunded, GatewayTransactionStatus.Succeeded)]
    [InlineData(GatewayTransactionStatus.Succeeded, GatewayTransactionStatus.Failed)]
    [InlineData(GatewayTransactionStatus.Succeeded, GatewayTransactionStatus.Pending)]
    public void GatewayTransaction_RejectsIllegalTransitions(GatewayTransactionStatus from, GatewayTransactionStatus to)
    {
        Assert.False(GatewayTransaction.CanTransition(from, to));
    }

    [Fact]
    public void GatewayTransaction_Move_ThrowsForIllegalTransition()
    {
        var t = new GatewayTransaction();
        t.Move(GatewayTransactionStatus.Failed, Now);
        Assert.Throws<DomainException>(() => t.Move(GatewayTransactionStatus.Succeeded, Now));
    }

    [Fact]
    public void CustomerRole_HoldsOnlyPortalAccess_AndNoStaffRoleHoldsIt()
    {
        var customer = DefaultRoles.All.Single(r => r.Name == RoleNames.Customer);
        Assert.Equal([Permissions.PortalAccess], customer.Permissions);
        Assert.DoesNotContain(DefaultRoles.All.Where(r => r.Name != RoleNames.Customer), r => r.Permissions.Contains(Permissions.PortalAccess));
        Assert.Equal(Permissions.All.Count, Permissions.All.Select(p => p.Code).Distinct().Count());
        Assert.All(DefaultRoles.All.SelectMany(r => r.Permissions), code => Assert.Contains(Permissions.All, p => p.Code == code));
    }
}
