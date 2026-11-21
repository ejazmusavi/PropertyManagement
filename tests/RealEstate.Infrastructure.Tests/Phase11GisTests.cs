using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RealEstate.Application.Administration;
using RealEstate.Application.Common;
using RealEstate.Application.Documents;
using RealEstate.Application.Gis;
using RealEstate.Application.Inventory;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Persistence;
using RealEstate.Tests.Support;

namespace RealEstate.Infrastructure.Tests;

/// <summary>Phase 11 GIS foundation against MySQL: coordinate systems, versioned boundaries with verification, layers, map documents, export, scoping and database rules.</summary>
[Collection("phase1")]
public class Phase11GisTests(Phase1Fixture fx)
{
    private readonly Phase11Kit k = new(fx);
    private static T Svc<T>(IServiceScope s) where T : notnull => Phase11Kit.Svc<T>(s);

    private const string Square = """{"type":"Polygon","coordinates":[[[73.0,33.0],[73.1,33.0],[73.1,33.1],[73.0,33.1],[73.0,33.0]]]}""";
    private static string Square2(double d) => FormattableString.Invariant($$"""{"type":"Polygon","coordinates":[[[73.0,33.0],[{{73.1 + d}},33.0],[{{73.1 + d}},33.1],[73.0,33.1],[73.0,33.0]]]}""");

    private sealed record Setup(Phase11Kit.World W, Guid Pm, long SrsId, long ProjectId);

    private async Task<Setup> SetupAsync()
    {
        var w = await k.WorldAsync();
        var pm = await k.UserAsync(RoleNames.ProjectManager);
        await k.Do(w.Admin, s => Svc<ProjectAccessAdminService>(s).AssignAsync(w.ProjectId, pm));
        var srs = await k.Run(w.Director, async s => (await Svc<GisService>(s).ListSrsAsync(true)).First(x => x.Code == "EPSG:4326").Id);
        return new Setup(w, pm, srs, w.ProjectId);
    }

    private static SubmitBoundaryRequest Submit(long srs, string geometry = Square, string survey = "SV-2026-01", long? doc = null, DateOnly? date = null) =>
        new(srs, geometry, survey, "Survey Co", date ?? new DateOnly(2026, 3, 1), doc, "field survey");

    private async Task<BoundaryDto> SubmitProjectAsync(Setup t, string? geometry = null, string survey = "SV-2026-01", long? doc = null) =>
        await k.Run(t.Pm, s => Svc<GisService>(s).SubmitProjectBoundaryAsync(t.ProjectId, Submit(t.SrsId, geometry ?? Square, survey, doc)));

    private async Task<BoundaryDto> VerifyProjectAsync(Setup t, BoundaryDto b, Guid? by = null, bool approve = true, string? comment = null) =>
        await k.Run(by ?? t.W.Director, s => Svc<GisService>(s).DecideProjectBoundaryAsync(t.ProjectId, b.Id, new BoundaryDecisionRequest(approve, comment, b.RowVersion)));

    // ------------------------------------------------------------------------------------------------ boundaries
    [MySqlFact]
    public async Task ProjectBoundary_IsVersioned_VerifiedByAnotherUser_AndSupersedesThePreviousOne()
    {
        var t = await SetupAsync();
        var v1 = await SubmitProjectAsync(t);
        Assert.Equal(1, v1.VersionNo);
        Assert.Equal(BoundaryStatus.Draft, v1.Status);
        Assert.Equal("Polygon", v1.GeometryType);
        Assert.Equal(5, v1.VertexCount);
        Assert.Equal(73.0m, v1.MinX); Assert.Equal(33.1m, v1.MaxY);
        Assert.Equal("EPSG:4326", v1.SrsCode);
        Assert.Contains("\"Polygon\"", v1.GeometryJson);

        // the project manager (no gis.verify) and the submitter's own verification are both refused
        await Assert.ThrowsAsync<ForbiddenException>(() => VerifyProjectAsync(t, v1, t.Pm));
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(t.W.Director, async s =>
        {
            var own = await Svc<GisService>(s).SubmitProjectBoundaryAsync(t.ProjectId, Submit(t.SrsId, Square2(0.01), "SV-OWN"));
            return await Svc<GisService>(s).DecideProjectBoundaryAsync(t.ProjectId, own.Id, new BoundaryDecisionRequest(true, null, own.RowVersion));
        }));

        var ok = await VerifyProjectAsync(t, v1);
        Assert.Equal(BoundaryStatus.Verified, ok.Status);
        Assert.NotNull(ok.VerifiedAtUtc);
        Assert.NotNull(ok.VerifiedBy);
        await Assert.ThrowsAsync<DomainException>(() => VerifyProjectAsync(t, ok));            // already decided

        var v3 = await SubmitProjectAsync(t, Square2(0.02), "SV-2026-02");
        Assert.True(v3.VersionNo > 1);
        var rejected = await Assert.ThrowsAsync<RequestValidationException>(() => VerifyProjectAsync(t, v3, approve: false));
        Assert.Contains("Comment", rejected.Errors.Keys);
        var again = await VerifyProjectAsync(t, v3);
        Assert.Equal(BoundaryStatus.Verified, again.Status);

        var list = await k.Run(t.Pm, s => Svc<GisService>(s).ListProjectBoundariesAsync(t.ProjectId));
        Assert.Equal(1, list.Count(b => b.Status == BoundaryStatus.Verified));
        Assert.Equal(BoundaryStatus.Superseded, list.Single(b => b.Id == v1.Id).Status);
        Assert.NotNull(list.Single(b => b.Id == v1.Id).SupersededAtUtc);
        Assert.All(list, b => Assert.Null(b.GeometryJson));                                       // lists carry no geometry

        // a rejected draft stays visible but never becomes the boundary in force
        var bad = await SubmitProjectAsync(t, Square2(0.5), "SV-BAD");
        var rej = await VerifyProjectAsync(t, bad, approve: false, comment: "does not match survey plan");
        Assert.Equal(BoundaryStatus.Rejected, rej.Status);
        Assert.Equal("does not match survey plan", rej.DecisionComment);
        Assert.Equal(1, (await k.Run(t.Pm, s => Svc<GisService>(s).ListProjectBoundariesAsync(t.ProjectId))).Count(b => b.Status == BoundaryStatus.Verified));

        // stale row version
        var fresh = await SubmitProjectAsync(t, Square2(0.03), "SV-STALE");
        await VerifyProjectAsync(t, fresh);
        await Assert.ThrowsAsync<DomainException>(() => VerifyProjectAsync(t, fresh));

        using var s2 = fx.Scope();
        var actions = await Svc<AppDbContext>(s2).AuditLogs.AsNoTracking().Where(a => a.EntityType == "ProjectBoundary").Select(a => a.Action).Distinct().ToListAsync();
        Assert.Contains("ProjectBoundary.Submitted", actions);
        Assert.Contains("ProjectBoundary.Verified", actions);
        Assert.Contains("ProjectBoundary.Rejected", actions);
    }

    [MySqlFact]
    public async Task BoundarySubmission_ValidatesGeometrySurveyAndReferences()
    {
        var t = await SetupAsync();
        async Task<RequestValidationException> Fail(SubmitBoundaryRequest r) => await Assert.ThrowsAsync<RequestValidationException>(() => k.Run(t.Pm, s => Svc<GisService>(s).SubmitProjectBoundaryAsync(t.ProjectId, r)));

        Assert.Contains("GeometryJson", (await Fail(Submit(t.SrsId, "not json"))).Errors.Keys);
        Assert.Contains("GeometryJson", (await Fail(Submit(t.SrsId, """{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1]]]}"""))).Errors.Keys);       // open ring
        Assert.Contains("GeometryJson", (await Fail(Submit(t.SrsId, """{"type":"Polygon","coordinates":[[[500000,3700000],[500100,3700000],[500100,3700100],[500000,3700000]]]}"""))).Errors.Keys);   // projected values in a degree system
        Assert.Contains("SurveyReference", (await Fail(Submit(t.SrsId, survey: ""))).Errors.Keys);
        Assert.Contains("SurveyDate", (await Fail(Submit(t.SrsId, date: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30))))).Errors.Keys);
        Assert.Contains("SrsId", (await Fail(Submit(987654))).Errors.Keys);
        Assert.Contains("SourceDocumentId", (await Fail(Submit(t.SrsId, doc: 987654))).Errors.Keys);
        // projected coordinates are fine in a projected system
        var utm = await k.Run(t.W.Director, async s => (await Svc<GisService>(s).ListSrsAsync(true)).First(x => x.Code == "EPSG:32643").Id);
        var ok = await k.Run(t.Pm, s => Svc<GisService>(s).SubmitProjectBoundaryAsync(t.ProjectId, Submit(utm, """{"type":"Polygon","coordinates":[[[500000,3700000],[500100,3700000],[500100,3700100],[500000,3700000]]]}""")));
        Assert.Equal(500100m, ok.MaxX);
        // permission: the accountant has no GIS permissions at all
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(t.W.Accountant, s => Svc<GisService>(s).SubmitProjectBoundaryAsync(t.ProjectId, Submit(t.SrsId))));
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(t.W.Accountant, s => Svc<GisService>(s).ListProjectBoundariesAsync(t.ProjectId)));
    }

    [MySqlFact]
    public async Task ConcurrentVerification_LeavesExactlyOneBoundaryInForce()
    {
        var t = await SetupAsync();
        var a = await SubmitProjectAsync(t, Square2(0.001), "SV-A");
        var b = await SubmitProjectAsync(t, Square2(0.002), "SV-B");
        var outcomes = await Task.WhenAll(
            Task.Run(async () => { try { await VerifyProjectAsync(t, a, t.W.Director); return (Exception?)null; } catch (Exception ex) { return ex; } }),
            Task.Run(async () => { try { await VerifyProjectAsync(t, b, t.W.Admin); return (Exception?)null; } catch (Exception ex) { return ex; } }));
        Assert.All(outcomes.Where(o => o is not null), o => Assert.IsType<ConcurrencyException>(o));
        var list = await k.Run(t.Pm, s => Svc<GisService>(s).ListProjectBoundariesAsync(t.ProjectId));
        Assert.Equal(1, list.Count(x => x.Status == BoundaryStatus.Verified));
        Assert.Equal(2, list.Count);
    }

    [MySqlFact]
    public async Task PropertyBoundary_FollowsTheSameWorkflow_AndProjectScope()
    {
        var t = await SetupAsync();
        var other = await k.WorldAsync();
        var deal = await k.DealAsync(t.W);
        var foreignPm = await k.UserAsync(RoleNames.ProjectManager);
        await k.Do(other.Admin, s => Svc<ProjectAccessAdminService>(s).AssignAsync(other.ProjectId, foreignPm));

        var v1 = await k.Run(t.Pm, s => Svc<GisService>(s).SubmitPropertyBoundaryAsync(deal.PropertyId, Submit(t.SrsId)));
        Assert.Equal("Property", v1.Scope);
        Assert.Equal(t.ProjectId, v1.ProjectId);
        Assert.NotNull(v1.PropertyCode);
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(t.Pm, s => Svc<GisService>(s).DecidePropertyBoundaryAsync(deal.PropertyId, v1.Id, new BoundaryDecisionRequest(true, null, v1.RowVersion))));
        var ok = await k.Run(t.W.Director, s => Svc<GisService>(s).DecidePropertyBoundaryAsync(deal.PropertyId, v1.Id, new BoundaryDecisionRequest(true, null, v1.RowVersion)));
        Assert.Equal(BoundaryStatus.Verified, ok.Status);
        var v2 = await k.Run(t.Pm, s => Svc<GisService>(s).SubmitPropertyBoundaryAsync(deal.PropertyId, Submit(t.SrsId, Square2(0.01), "SV-2")));
        await k.Run(t.W.Director, s => Svc<GisService>(s).DecidePropertyBoundaryAsync(deal.PropertyId, v2.Id, new BoundaryDecisionRequest(true, null, v2.RowVersion)));
        var list = await k.Run(t.Pm, s => Svc<GisService>(s).ListPropertyBoundariesAsync(deal.PropertyId));
        Assert.Equal([2, 1], list.Select(b => b.VersionNo));
        Assert.Equal(BoundaryStatus.Superseded, list.Last().Status);

        // another project's manager cannot see, submit or decide
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(foreignPm, s => Svc<GisService>(s).ListPropertyBoundariesAsync(deal.PropertyId)));
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(foreignPm, s => Svc<GisService>(s).SubmitPropertyBoundaryAsync(deal.PropertyId, Submit(t.SrsId))));
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(foreignPm, s => Svc<GisService>(s).GetPropertyBoundaryAsync(deal.PropertyId, v1.Id)));
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(foreignPm, s => Svc<GisService>(s).ListPropertyBoundariesAsync(99999999)));          // unknown id looks the same
        await Assert.ThrowsAsync<NotFoundException>(() => k.Run(t.W.Director, s => Svc<GisService>(s).ListPropertyBoundariesAsync(99999999)));
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(foreignPm, s => Svc<GisService>(s).ListProjectBoundariesAsync(t.ProjectId)));
    }

    [MySqlFact]
    public async Task GeoJsonExport_HoldsOnlyVerifiedBoundaries()
    {
        var t = await SetupAsync();
        var deal = await k.DealAsync(t.W);
        var empty = JsonDocument.Parse(await k.Run(t.Pm, s => Svc<GisService>(s).ExportGeoJsonAsync(t.ProjectId)));
        Assert.Equal(0, empty.RootElement.GetProperty("features").GetArrayLength());

        var draft = await SubmitProjectAsync(t);
        var pb = await k.Run(t.Pm, s => Svc<GisService>(s).SubmitPropertyBoundaryAsync(deal.PropertyId, Submit(t.SrsId, Square2(0.01), "SV-P")));
        Assert.Equal(0, JsonDocument.Parse(await k.Run(t.Pm, s => Svc<GisService>(s).ExportGeoJsonAsync(t.ProjectId))).RootElement.GetProperty("features").GetArrayLength());

        await VerifyProjectAsync(t, draft);
        await k.Run(t.W.Director, s => Svc<GisService>(s).DecidePropertyBoundaryAsync(deal.PropertyId, pb.Id, new BoundaryDecisionRequest(true, null, pb.RowVersion)));
        var json = JsonDocument.Parse(await k.Run(t.Pm, s => Svc<GisService>(s).ExportGeoJsonAsync(t.ProjectId)));
        var features = json.RootElement.GetProperty("features").EnumerateArray().ToList();
        Assert.Equal("FeatureCollection", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(2, features.Count);
        Assert.Equal("ProjectBoundary", features[0].GetProperty("properties").GetProperty("kind").GetString());
        Assert.Equal(4326, features[0].GetProperty("properties").GetProperty("srid").GetInt32());
        Assert.Equal("Polygon", features[1].GetProperty("geometry").GetProperty("type").GetString());
        Assert.Equal(deal.PropertyId, features[1].GetProperty("properties").GetProperty("propertyId").GetInt64());
        var foreign = await k.WorldAsync();
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(foreign.Support, s => Svc<GisService>(s).ExportGeoJsonAsync(t.ProjectId)));
    }

    // ------------------------------------------------------------------------------------------------ layers
    [MySqlFact]
    public async Task Layers_AreMaintainedPerProject_WithUniqueCodes_AndRowVersions()
    {
        var t = await SetupAsync();
        SaveMapLayerRequest Req(string code = "PLOTS", string name = "Plots", string? rv = null, bool active = true, long? doc = null) =>
            new(code, name, MapLayerKind.Plots, t.SrsId, 10, true, """{"fill":"#cfe8cf"}""", doc, "plot polygons", active, rv);
        var l = await k.Run(t.Pm, s => Svc<GisService>(s).SaveLayerAsync(t.ProjectId, null, Req("plots")));
        Assert.Equal("PLOTS", l.Code);                               // codes are normalized
        Assert.Equal("EPSG:4326", l.SrsCode);
        var dup = await Assert.ThrowsAsync<RequestValidationException>(() => k.Run(t.Pm, s => Svc<GisService>(s).SaveLayerAsync(t.ProjectId, null, Req("PLOTS"))));
        Assert.Contains("Code", dup.Errors.Keys);

        var edited = await k.Run(t.Pm, s => Svc<GisService>(s).SaveLayerAsync(t.ProjectId, l.Id, Req("PLOTS", "All plots", l.RowVersion)));
        Assert.Equal("All plots", edited.Name);
        await Assert.ThrowsAsync<ConcurrencyException>(() => k.Run(t.Pm, s => Svc<GisService>(s).SaveLayerAsync(t.ProjectId, l.Id, Req("PLOTS", "Stale", l.RowVersion))));
        await Assert.ThrowsAsync<DomainException>(() => k.Run(t.Pm, s => Svc<GisService>(s).SaveLayerAsync(t.ProjectId, l.Id, Req("RENAMED", "x", edited.RowVersion))));
        var off = await k.Run(t.Pm, s => Svc<GisService>(s).SaveLayerAsync(t.ProjectId, l.Id, Req("PLOTS", "All plots", edited.RowVersion, active: false)));
        Assert.False(off.IsActive);

        await Assert.ThrowsAsync<RequestValidationException>(() => k.Run(t.Pm, s => Svc<GisService>(s).SaveLayerAsync(t.ProjectId, null, Req("OTHER", doc: 98765))));
        Assert.Single(await k.Run(t.Pm, s => Svc<GisService>(s).ListLayersAsync(t.ProjectId)));
        // layers of another project are not reachable
        var foreign = await k.WorldAsync();
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(foreign.Support, s => Svc<GisService>(s).ListLayersAsync(t.ProjectId)));
        await Assert.ThrowsAsync<NotFoundException>(() => k.Run(t.Pm, s => Svc<GisService>(s).GetLayerAsync(t.ProjectId, 99999)));
        // the same code may exist in another project
        var other = await k.WorldAsync();
        var pm2 = await k.UserAsync(RoleNames.ProjectManager);
        await k.Do(other.Admin, s => Svc<ProjectAccessAdminService>(s).AssignAsync(other.ProjectId, pm2));
        Assert.NotNull(await k.Run(pm2, s => Svc<GisService>(s).SaveLayerAsync(other.ProjectId, null, Req("PLOTS"))));
    }

    // ------------------------------------------------------------------------------------------------ documents
    [MySqlFact]
    public async Task MapDocuments_AreStoredSecurely_DownloadedWithAccess_AndRemovedWithAReason()
    {
        var t = await SetupAsync();
        var bytes = new byte[] { 37, 80, 68, 70, 45, 49, 46, 52 };
        var doc = await k.Run(t.Pm, s => Svc<GisService>(s).UploadDocumentAsync(t.ProjectId, MapDocumentType.SurveyPlan, "Survey plan", t.SrsId, "SV-9", new DateOnly(2026, 2, 1), "signed plan",
            new FileUpload("plan.pdf", "application/pdf", bytes.Length, new MemoryStream(bytes))));
        Assert.Equal("Survey plan", doc.Title);
        Assert.Equal("EPSG:4326", doc.SrsCode);
        Assert.Equal(bytes.Length, doc.SizeBytes);

        var file = await k.Run(t.Pm, s => Svc<GisService>(s).DownloadDocumentAsync(t.ProjectId, doc.Id));
        using (var ms = new MemoryStream()) { await file.Content.CopyToAsync(ms); Assert.Equal(bytes, ms.ToArray()); }
        var foreign = await k.WorldAsync();
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(foreign.Support, s => Svc<GisService>(s).DownloadDocumentAsync(t.ProjectId, doc.Id)));
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(t.W.Accountant, s => Svc<GisService>(s).ListDocumentsAsync(t.ProjectId)));
        await Assert.ThrowsAsync<DomainException>(() => k.Run(t.Pm, s => Svc<GisService>(s).UploadDocumentAsync(t.ProjectId, MapDocumentType.Other, null, null, null, null, null, new FileUpload("run.exe", "application/octet-stream", 3, new MemoryStream([1, 2, 3])))));
        await Assert.ThrowsAsync<RequestValidationException>(() => k.Run(t.Pm, s => Svc<GisService>(s).UploadDocumentAsync(t.ProjectId, MapDocumentType.Other, null, 98765, null, null, null, new FileUpload("a.pdf", "application/pdf", 3, new MemoryStream([1, 2, 3])))));

        // a document that is the source of a verified boundary cannot be removed
        var b = await SubmitProjectAsync(t, doc: doc.Id);
        await VerifyProjectAsync(t, b);
        var blocked = await Assert.ThrowsAsync<DomainException>(() => k.Do(t.Pm, s => Svc<GisService>(s).DeleteDocumentAsync(t.ProjectId, doc.Id, "no longer needed")));
        Assert.Contains("verified boundary", blocked.Message);

        var spare = await k.Run(t.Pm, s => Svc<GisService>(s).UploadDocumentAsync(t.ProjectId, MapDocumentType.Other, null, null, null, null, null, new FileUpload("spare.pdf", "application/pdf", 3, new MemoryStream([1, 2, 3]))));
        await Assert.ThrowsAsync<RequestValidationException>(() => k.Do(t.Pm, s => Svc<GisService>(s).DeleteDocumentAsync(t.ProjectId, spare.Id, "no")));
        await k.Do(t.Pm, s => Svc<GisService>(s).DeleteDocumentAsync(t.ProjectId, spare.Id, "uploaded by mistake"));
        Assert.DoesNotContain(await k.Run(t.Pm, s => Svc<GisService>(s).ListDocumentsAsync(t.ProjectId)), d => d.Id == spare.Id);
        using var s2 = fx.Scope();
        Assert.True((await Svc<AppDbContext>(s2).MapDocuments.AsNoTracking().FirstAsync(d => d.Id == spare.Id)).IsDeleted);      // kept, soft-deleted
    }

    // ------------------------------------------------------------------------------------------------ coordinate systems and overview
    [MySqlFact]
    public async Task CoordinateSystems_AreSeeded_CustomOnesNeedAllProjectAccess_AndKeyFieldsAreImmutable()
    {
        var t = await SetupAsync();
        var all = await k.Run(t.W.Director, s => Svc<GisService>(s).ListSrsAsync());
        foreach (var code in new[] { "EPSG:4326", "EPSG:3857", "EPSG:32642", "EPSG:32643" }) Assert.Contains(all, x => x.Code == code && x.IsSystem && x.IsActive);
        Assert.Equal(SpatialCoordinateKind.Geographic, all.Single(x => x.Code == "EPSG:4326").Kind);

        var req = new SaveSrsRequest("epsg:24378", 24378, "Kalianpur 1975 / India zone I", SpatialCoordinateKind.Projected, "metre", "legacy grid", true, null);
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(t.Pm, s => Svc<GisService>(s).SaveSrsAsync(null, req)));        // project managers hold gis.manage but not all-project access
        var created = await k.Run(t.W.Director, s => Svc<GisService>(s).SaveSrsAsync(null, req));
        Assert.Equal("EPSG:24378", created.Code);
        Assert.False(created.IsSystem);
        await Assert.ThrowsAsync<RequestValidationException>(() => k.Run(t.W.Director, s => Svc<GisService>(s).SaveSrsAsync(null, req)));     // duplicate
        await Assert.ThrowsAsync<DomainException>(() => k.Run(t.W.Director, s => Svc<GisService>(s).SaveSrsAsync(created.Id, req with { Srid = 24379, RowVersion = created.RowVersion })));
        var renamed = await k.Run(t.W.Director, s => Svc<GisService>(s).SaveSrsAsync(created.Id, req with { Name = "Kalianpur zone I", IsActive = false, RowVersion = created.RowVersion }));
        Assert.False(renamed.IsActive);
        Assert.DoesNotContain(await k.Run(t.W.Director, s => Svc<GisService>(s).ListSrsAsync(true)), x => x.Id == created.Id);
        var wgs = all.Single(x => x.Code == "EPSG:4326");
        await Assert.ThrowsAsync<DomainException>(() => k.Run(t.W.Director, s => Svc<GisService>(s).SaveSrsAsync(wgs.Id, new SaveSrsRequest(wgs.Code, wgs.Srid, wgs.Name, wgs.Kind, wgs.Unit, wgs.Description, false, wgs.RowVersion))));
    }

    [MySqlFact]
    public async Task Summary_ShowsOnlyTheUsersProjects_WithCounts()
    {
        var t = await SetupAsync();
        var deal = await k.DealAsync(t.W);
        var b = await SubmitProjectAsync(t);
        await VerifyProjectAsync(t, b);
        await SubmitProjectAsync(t, Square2(0.04), "SV-D");
        var pb = await k.Run(t.Pm, s => Svc<GisService>(s).SubmitPropertyBoundaryAsync(deal.PropertyId, Submit(t.SrsId, Square2(0.05), "SV-PB")));
        await k.Run(t.W.Director, s => Svc<GisService>(s).DecidePropertyBoundaryAsync(deal.PropertyId, pb.Id, new BoundaryDecisionRequest(true, null, pb.RowVersion)));
        await k.Run(t.Pm, s => Svc<GisService>(s).SaveLayerAsync(t.ProjectId, null, new SaveMapLayerRequest("L1", "Layer", MapLayerKind.Blocks, t.SrsId, 1, true, null, null, null, true, null)));

        var mine = await k.Run(t.Pm, s => Svc<GisService>(s).SummaryAsync());
        var row = Assert.Single(mine);
        Assert.Equal(t.ProjectId, row.ProjectId);
        Assert.True(row.HasVerifiedBoundary);
        Assert.Equal(1, row.DraftBoundaries);
        Assert.Equal(1, row.PropertyBoundariesVerified);
        Assert.Equal(1, row.PropertiesTotal);
        Assert.Equal(1, row.Layers);
        Assert.Contains(await k.Run(t.W.Director, s => Svc<GisService>(s).SummaryAsync()), p => p.ProjectId == t.ProjectId);
    }

    // ------------------------------------------------------------------------------------------------ database rules
    [MySqlFact]
    public async Task Database_AllowsOneVerifiedBoundary_AndConsistentActiveKeys()
    {
        var t = await SetupAsync();
        var b = await SubmitProjectAsync(t, Square2(0.06), "SV-DB");
        using var s = fx.Scope();
        var db = Svc<AppDbContext>(s);

        // Verified without the active key is rejected by the CHECK constraint
        var row = await db.ProjectBoundaries.FirstAsync(x => x.Id == b.Id);
        row.Status = BoundaryStatus.Verified;
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // two Verified boundaries of one project are rejected by the unique active key
        var first = await db.ProjectBoundaries.FirstAsync(x => x.Id == b.Id);
        first.Status = BoundaryStatus.Verified; first.ActiveProjectId = t.ProjectId;
        await db.SaveChangesAsync();
        var second = await SubmitProjectAsync(t, Square2(0.07), "SV-DB2");
        var row2 = await db.ProjectBoundaries.FirstAsync(x => x.Id == second.Id);
        row2.Status = BoundaryStatus.Verified; row2.ActiveProjectId = t.ProjectId;
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // an unverified boundary must not carry the key; versions are unique per project
        var draft = await db.ProjectBoundaries.AsNoTracking().FirstAsync(x => x.Id == second.Id);
        db.ProjectBoundaries.Add(new ProjectBoundary
        {
            ProjectId = t.ProjectId, VersionNo = draft.VersionNo, SrsId = t.SrsId, GeometryType = "Polygon", GeometryJson = Square, VertexCount = 5, SurveyReference = "X", SurveyedBy = "Y", SurveyDate = new DateOnly(2026, 1, 1),
            SubmittedByUserId = t.Pm,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
