using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;

namespace RealEstate.Domain.Tests;

public class CompanyTests
{
    [Fact]
    public void Constructor_TrimsName() => Assert.Equal("Serene", new Company("  Serene ").Name);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankName(string name) => Assert.Throws<DomainException>(() => new Company(name));

    [Fact]
    public void Rename_RejectsBlankName() => Assert.Throws<DomainException>(() => new Company("A").Rename(" "));
}

public class RefreshTokenTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsActive_TrueUntilExpiry()
    {
        var t = new RefreshToken { ExpiresAtUtc = Now.AddDays(1) };
        Assert.True(t.IsActive(Now));
        Assert.False(t.IsActive(Now.AddDays(1)));
    }

    [Fact]
    public void Revoke_IsIdempotentAndKeepsFirstTimestamp()
    {
        var t = new RefreshToken { ExpiresAtUtc = Now.AddDays(1) };
        t.Revoke(Now, "next");
        t.Revoke(Now.AddHours(1));
        Assert.Equal(Now, t.RevokedAtUtc);
        Assert.Equal("next", t.ReplacedByTokenHash);
        Assert.False(t.IsActive(Now));
    }
}

public class PermissionCatalogTests
{
    [Fact]
    public void Codes_AreUniqueAndLowerCaseDotted()
    {
        var codes = Permissions.All.Select(p => p.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches(@"^[a-z]+(\.[a-z]+)+$", c));
    }

    [Fact]
    public void Constants_AreInCatalog()
    {
        Assert.Contains(Permissions.All, p => p.Code == Permissions.UsersView);
        Assert.Contains(Permissions.All, p => p.Code == Permissions.AuditView);
    }
}
