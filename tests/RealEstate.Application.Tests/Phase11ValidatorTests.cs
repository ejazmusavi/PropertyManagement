using RealEstate.Application.Gis;
using RealEstate.Application.Jobs;
using RealEstate.Application.Notifications;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Tests;

public class Phase11ValidatorTests
{
    // ----- notifications -----
    [Fact]
    public void Template_NeedsSubjectBody_AndOnlyKnownPlaceholders()
    {
        var v = new SaveTemplateRequestValidator();
        Assert.True(v.Validate(new SaveTemplateRequest(NotificationType.InstallmentUpcoming, NotificationChannel.Email, "en", "Due {{DueDate}}", "Dear {{CustomerName}}", true, null)).IsValid);
        Assert.False(v.Validate(new SaveTemplateRequest(NotificationType.InstallmentUpcoming, NotificationChannel.Email, "en", "", "Body", true, null)).IsValid);
        Assert.False(v.Validate(new SaveTemplateRequest(NotificationType.InstallmentUpcoming, NotificationChannel.Email, "en", "S", new string('x', 2001), true, null)).IsValid);
        var unknown = v.Validate(new SaveTemplateRequest(NotificationType.InstallmentUpcoming, NotificationChannel.Sms, "en", "S", "Hello {{Password}}", true, null));
        Assert.False(unknown.IsValid);
        Assert.Contains(unknown.Errors, e => e.ErrorMessage.Contains("Password"));
        Assert.False(v.Validate(new SaveTemplateRequest(NotificationType.InstallmentUpcoming, NotificationChannel.Sms, "x", "S", "B", true, null)).IsValid);
    }

    [Fact]
    public void ManualSend_OnlyAllowedTypes_NeedsAudience_AndText()
    {
        var v = new SendNotificationRequestValidator();
        Assert.True(v.Validate(new SendNotificationRequest(NotificationType.CompanyAnnouncement, null, null, null, "T", "M", null)).IsValid);        // everyone
        Assert.True(v.Validate(new SendNotificationRequest(NotificationType.PossessionNotice, null, 5, null, "T", "M", "key")).IsValid);
        Assert.False(v.Validate(new SendNotificationRequest(NotificationType.PossessionNotice, null, null, null, "T", "M", null)).IsValid);            // no audience
        Assert.False(v.Validate(new SendNotificationRequest(NotificationType.InstallmentOverdue, 1, null, null, "T", "M", null)).IsValid);             // automatic types are not sent by hand
        Assert.False(v.Validate(new SendNotificationRequest(NotificationType.StaffDigest, 1, null, null, "T", "M", null)).IsValid);
        Assert.False(v.Validate(new SendNotificationRequest(NotificationType.CompanyAnnouncement, 1, null, null, "", "M", null)).IsValid);
        Assert.False(v.Validate(new SendNotificationRequest(NotificationType.CompanyAnnouncement, 1, null, null, "T", new string('m', 2001), null)).IsValid);
    }

    [Fact]
    public void Cancel_NeedsAReason() =>
        Assert.Equal([false, false, true], new[] { "", "no", "customer asked us to stop" }.Select(r => new CancelNotificationRequestValidator().Validate(new CancelNotificationRequest(r, null)).IsValid));

    [Fact]
    public void Preferences_AreBounded_AndTypesMustExist()
    {
        var v = new SavePreferencesRequestValidator();
        Assert.True(v.Validate(new SavePreferencesRequest([new SavePreferenceRequest(NotificationChannel.Sms, null, false), new SavePreferenceRequest(NotificationChannel.Email, NotificationType.InstallmentOverdue, true)])).IsValid);
        Assert.False(v.Validate(new SavePreferencesRequest([new SavePreferenceRequest(NotificationChannel.Sms, (NotificationType)99, false)])).IsValid);
        Assert.False(v.Validate(new SavePreferencesRequest([new SavePreferenceRequest((NotificationChannel)42, null, false)])).IsValid);
        Assert.False(v.Validate(new SavePreferencesRequest([.. Enumerable.Range(0, 101).Select(_ => new SavePreferenceRequest(NotificationChannel.Sms, null, true))])).IsValid);
    }

    [Fact]
    public void DefaultTemplates_CoverEveryTypeAndChannel_AndUseOnlyKnownPlaceholders()
    {
        var all = NotificationTemplateDefaults.All;
        foreach (var t in Enum.GetValues<NotificationType>().Where(t => t != NotificationType.StaffDigest))
            foreach (var c in Enum.GetValues<NotificationChannel>()) Assert.Contains(all, e => e.Type == t && e.Channel == c);
        Assert.Contains(all, e => e.Type == NotificationType.StaffDigest && e.Channel == NotificationChannel.Email);
        Assert.Equal(all.Count, all.Select(e => (e.Type, e.Channel)).Distinct().Count());
        foreach (var e in all)
        {
            Assert.Empty(NotificationRules.UnknownPlaceholders(e.Subject));
            Assert.Empty(NotificationRules.UnknownPlaceholders(e.Body));
            Assert.InRange(e.Subject.Length, 1, 200);
            Assert.InRange(e.Body.Length, 1, 2000);
        }
    }

    // ----- jobs -----
    [Fact]
    public void JobCatalog_HasUniqueKeys_AndFiveFieldUtcCrons()
    {
        Assert.Equal(JobCatalog.All.Count, JobCatalog.All.Select(j => j.Key).Distinct().Count());
        foreach (var j in JobCatalog.All)
        {
            Assert.Equal(5, j.Cron.Split(' ').Length);
            Assert.False(string.IsNullOrWhiteSpace(j.Description));
            Assert.InRange(j.MaxMinutes, 1, 120);
            Assert.Same(j, JobCatalog.Find(j.Key));
        }
        Assert.Null(JobCatalog.Find("nope"));
        // jobs that call permission-checked services declare it
        Assert.True(JobCatalog.Find(JobCatalog.Postings)!.NeedsServiceUser);
        Assert.True(JobCatalog.Find(JobCatalog.Gateway)!.NeedsServiceUser);
        Assert.False(JobCatalog.Find(JobCatalog.Dispatch)!.NeedsServiceUser);
    }

    // ----- GIS -----
    [Fact]
    public void Srs_NeedsCodeSridNameUnit()
    {
        var v = new SaveSrsRequestValidator();
        Assert.True(v.Validate(new SaveSrsRequest("EPSG:32643", 32643, "UTM 43N", SpatialCoordinateKind.Projected, "metre", null, true, null)).IsValid);
        Assert.False(v.Validate(new SaveSrsRequest("bad code!", 1, "N", SpatialCoordinateKind.Projected, "metre", null, true, null)).IsValid);
        Assert.False(v.Validate(new SaveSrsRequest("X", 0, "N", SpatialCoordinateKind.Projected, "metre", null, true, null)).IsValid);
        Assert.False(v.Validate(new SaveSrsRequest("X", 1, "", SpatialCoordinateKind.Projected, "metre", null, true, null)).IsValid);
        Assert.False(v.Validate(new SaveSrsRequest("X", 1, "N", SpatialCoordinateKind.Projected, "", null, true, null)).IsValid);
    }

    [Fact]
    public void BoundarySubmission_NeedsSurveyData()
    {
        var v = new SubmitBoundaryRequestValidator();
        var date = new DateOnly(2026, 5, 1);
        Assert.True(v.Validate(new SubmitBoundaryRequest(1, "{}", "SV-1", "Surveyor", date, null, null)).IsValid);
        Assert.False(v.Validate(new SubmitBoundaryRequest(0, "{}", "SV-1", "Surveyor", date, null, null)).IsValid);
        Assert.False(v.Validate(new SubmitBoundaryRequest(1, "", "SV-1", "Surveyor", date, null, null)).IsValid);
        Assert.False(v.Validate(new SubmitBoundaryRequest(1, "{}", "", "Surveyor", date, null, null)).IsValid);
        Assert.False(v.Validate(new SubmitBoundaryRequest(1, "{}", "SV-1", "", date, null, null)).IsValid);
        Assert.False(v.Validate(new SubmitBoundaryRequest(1, "{}", "SV-1", "Surveyor", default, null, null)).IsValid);
    }

    [Fact]
    public void BoundaryDecision_RejectionNeedsAComment()
    {
        var v = new BoundaryDecisionRequestValidator();
        Assert.True(v.Validate(new BoundaryDecisionRequest(true, null, null)).IsValid);
        Assert.False(v.Validate(new BoundaryDecisionRequest(false, null, null)).IsValid);
        Assert.False(v.Validate(new BoundaryDecisionRequest(false, "no", null)).IsValid);
        Assert.True(v.Validate(new BoundaryDecisionRequest(false, "does not match the plan", null)).IsValid);
    }

    [Fact]
    public void Layer_ValidatesCodeStyleAndOrder()
    {
        var v = new SaveMapLayerRequestValidator();
        SaveMapLayerRequest Make(string code = "PLOTS", string? style = null, int order = 1) => new(code, "Plots", MapLayerKind.Plots, 1, order, true, style, null, null, true, null);
        Assert.True(v.Validate(Make()).IsValid);
        Assert.True(v.Validate(Make(style: """{"fill":"#fff"}""")).IsValid);
        Assert.False(v.Validate(Make(code: "bad code")).IsValid);
        Assert.False(v.Validate(Make(style: "not json")).IsValid);
        Assert.False(v.Validate(Make(style: "[1,2]")).IsValid);
        Assert.False(v.Validate(Make(order: -1)).IsValid);
    }
}
