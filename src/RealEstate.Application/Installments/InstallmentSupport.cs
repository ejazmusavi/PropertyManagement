using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Common;
using RealEstate.Application.Pricing;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Installments;
using RealEstate.Domain.Sales;

namespace RealEstate.Application.Installments;

/// <summary>Shared lookups for the installment services: money policy, company "today", agreement access and JSON mapping of custom schedules.</summary>
public class InstallmentSupport(IAppDbContext db, ProjectAccessService access, PricingSupport pricing)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<MoneyPolicy> PolicyAsync(CancellationToken ct = default)
    {
        var (d, m) = await pricing.RoundingAsync(ct);
        return new MoneyPolicy(d, m);
    }

    public Task<DateOnly> TodayAsync(CancellationToken ct = default) => pricing.TodayAsync(ct);

    public async Task<string> CurrencyAsync(CancellationToken ct = default) =>
        await db.Companies.AsNoTracking().Select(c => c.CurrencySymbol).FirstOrDefaultAsync(ct) ?? "Rs";

    /// <summary>Project-level access to an agreement (through its booking). Unknown ids look like inaccessible ones for non-privileged users.</summary>
    public async Task<long> EnsureAgreementAccessAsync(long agreementId, CancellationToken ct = default)
    {
        var projectId = await (from a in db.CustomerAgreements.AsNoTracking()
                               join b in db.Bookings.AsNoTracking() on a.BookingId equals b.Id
                               where a.Id == agreementId
                               select (long?)b.ProjectId).FirstOrDefaultAsync(ct);
        if (projectId is null)
        {
            if (!await access.HasAllAccessAsync(ct)) throw new ForbiddenException("You do not have access to this agreement.");
            throw new NotFoundException("Agreement not found.");
        }
        await access.EnsureAccessAsync(projectId.Value, ct);
        return projectId.Value;
    }

    public static string? SerializeTemplate(IReadOnlyList<TemplateEntryInput>? e) => e is { Count: > 0 } ? JsonSerializer.Serialize(e, Json) : null;
    public static IReadOnlyList<TemplateEntryInput> ParseTemplate(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<TemplateEntryInput>>(json, Json) ?? [];
    public static string? SerializeEntries(IReadOnlyList<ScheduleEntryInput>? e) => e is { Count: > 0 } ? JsonSerializer.Serialize(e, Json) : null;
    public static IReadOnlyList<ScheduleEntryInput> ParseEntries(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<ScheduleEntryInput>>(json, Json) ?? [];
    public static string Serialize(object o) => JsonSerializer.Serialize(o, Json);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json);

    public static ChargeSpec ToSpec(AgreementCharge c) => new(c.Id, c.SortOrder, c.Description, c.Category, c.Amount, c.Frequency, c.NumberOfInstallments, c.InstallmentAmount,
        c.FirstDueDate, c.DueDayOfMonth, c.Frequency == ScheduleFrequency.CustomDates ? ParseEntries(c.CustomScheduleJson).Select(e => new ScheduleEntry(e.DueDate, e.Amount)).ToList() : null,
        c.GracePeriodDays, c.TaxTreatment, c.TaxRatePercent, c.RoundingUnit);

    public static ChargeSpec ToSpec(AgreementChargeInput c, long key, int sortOrder) => new(key, sortOrder, c.Description.Trim(), c.Category, c.Amount, c.Frequency, c.NumberOfInstallments,
        c.InstallmentAmount, c.Frequency == ScheduleFrequency.CustomDates && c.CustomEntries is { Count: > 0 } e ? e[0].DueDate : c.FirstDueDate, c.DueDayOfMonth,
        c.Frequency == ScheduleFrequency.CustomDates ? c.CustomEntries?.Select(e => new ScheduleEntry(e.DueDate, e.Amount)).ToList() : null,
        c.GracePeriodDays, c.TaxTreatment, c.TaxRatePercent, c.RoundingUnit);

    public static ChargeState ToState(AgreementCharge c) => new(c.Id, c.SortOrder, c.Description, c.Category, c.Amount, c.Frequency, c.FirstDueDate, c.DueDayOfMonth, c.GracePeriodDays,
        c.LateFeeRuleId, c.TaxTreatment, c.TaxRatePercent, c.RoundingUnit, c.WaiverAllowed, c.MaxWaiverPercent);

    public static LateFeeTerms Terms(LateFeeRule r) => new(r.Method, r.Value, r.MaxAmount, r.MaxPercentOfInstallment);

    /// <summary>Validates a charge's references (charge type, late fee rule within the project) and copies the input onto the entity.</summary>
    public async Task ApplyInputAsync(AgreementCharge target, AgreementChargeInput c, long projectId, CancellationToken ct)
    {
        if (!await db.ChargeTypes.AnyAsync(t => t.Id == c.ChargeTypeId && t.IsActive, ct)) throw PricingSupport.Invalid("ChargeTypeId", "The charge type was not found or is inactive.");
        if (c.LateFeeRuleId is { } lr && !await db.LateFeeRules.AnyAsync(r => r.Id == lr && r.IsActive && (r.ProjectId == null || r.ProjectId == projectId), ct))
            throw PricingSupport.Invalid("LateFeeRuleId", "The late fee rule was not found, is inactive or belongs to another project.");
        var entries = c.Frequency == ScheduleFrequency.CustomDates ? c.CustomEntries : null;
        target.ChargeTypeId = c.ChargeTypeId; target.Description = c.Description.Trim(); target.Category = c.Category; target.Amount = c.Amount; target.Frequency = c.Frequency;
        target.NumberOfInstallments = c.Frequency is ScheduleFrequency.OneTime or ScheduleFrequency.CustomDates ? null : c.NumberOfInstallments;
        target.InstallmentAmount = c.Frequency is ScheduleFrequency.OneTime or ScheduleFrequency.CustomDates ? null : c.InstallmentAmount;
        target.FirstDueDate = entries is { Count: > 0 } ? entries[0].DueDate : c.FirstDueDate;
        target.DueDayOfMonth = ScheduleGenerationService.IsRegular(c.Frequency) ? c.DueDayOfMonth : null;
        target.CustomScheduleJson = SerializeEntries(entries); target.GracePeriodDays = c.GracePeriodDays; target.LateFeeRuleId = c.LateFeeRuleId;
        target.WaiverAllowed = c.WaiverAllowed; target.MaxWaiverPercent = c.WaiverAllowed ? c.MaxWaiverPercent : 0m; target.TaxTreatment = c.TaxTreatment;
        target.TaxRatePercent = c.TaxRatePercent; target.RoundingUnit = c.RoundingUnit; target.Remarks = PricingSupport.N(c.Remarks);
        if (c.SortOrder is { } so) target.SortOrder = so;
    }

    /// <summary>Schedule section printed on agreement versions (active schedule lines and agreement charges outside the sale consideration).</summary>
    public static async Task<(int? VersionNo, IReadOnlyList<AgreementScheduleLine>? Lines, IReadOnlyList<AgreementChargeLine>? Extra)> AgreementScheduleTextAsync(
        IAppDbContext db, long agreementId, CancellationToken ct)
    {
        var s = await db.PaymentSchedules.AsNoTracking().Include(x => x.Items).FirstOrDefaultAsync(x => x.ActiveAgreementId == agreementId, ct);
        if (s is null) return (null, null, null);
        var extra = await db.AgreementCharges.AsNoTracking().Where(c => c.AgreementId == agreementId && c.Status == AgreementChargeStatus.Active && c.Category != ChargeCategory.SaleConsideration)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id).Select(c => new AgreementChargeLine($"{c.Category}: {c.Description}", c.Amount)).ToListAsync(ct);
        return (s.VersionNo, s.Items.OrderBy(i => i.LineNo).Select(i => new AgreementScheduleLine(i.LineNo, i.Description, i.DueDate, i.Amount, i.TaxAmount)).ToList(), extra);
    }

    public static ScheduleLineDto Line(PaymentScheduleItem i, IReadOnlyDictionary<long, AgreementCharge> charges, IReadOnlyDictionary<long, LateFeeRule> rules, DateOnly today, MoneyPolicy policy)
    {
        var fee = 0m;
        if (charges.TryGetValue(i.AgreementChargeId, out var c) && c.LateFeeRuleId is { } r && rules.TryGetValue(r, out var rule))
            fee = Math.Max(LateFeeCalculator.Compute(Terms(rule), i.Amount, i.Outstanding, i.GraceEndDate, today, policy) - i.LateFeeWaived, 0m);
        return new ScheduleLineDto(i.Id, i.LineNo, i.AgreementChargeId, i.InstallmentNo, i.InstallmentCount, i.Description, i.DueDate, i.GraceEndDate, i.Amount, i.TaxAmount, i.IsPartial,
            i.AmountPaid, i.AmountWaived, i.LateFeeWaived, i.Outstanding, i.Status, fee, i.Outstanding > 0m && today > i.GraceEndDate, i.IsPaidInAdvance, i.PreviousItemId);
    }

    public static ScheduleSummaryDto Summary(PaymentSchedule s, DateOnly today)
    {
        var next = s.Items.Where(i => i.Outstanding > 0m).OrderBy(i => i.DueDate).ThenBy(i => i.LineNo).FirstOrDefault();
        return new ScheduleSummaryDto(s.Id, s.VersionNo, s.Status, s.Reason, s.ItemCount, s.TotalAmount, s.TotalTax, s.Items.Sum(i => i.Outstanding), next?.DueDate, next?.Outstanding,
            s.GeneratedAtUtc, s.GeneratedBy, s.SupersededAtUtc, s.SourceAmendmentId);
    }
}
