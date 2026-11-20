using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Payments;

namespace RealEstate.Domain.Tests;

public class Phase06DomainTests
{
    private static readonly DateOnly D = new(2026, 1, 31);

    private static List<AllocItem> Items() =>
    [
        new(3, 20, 3, D.AddMonths(2), 100m),
        new(1, 10, 1, D, 500m),
        new(2, 20, 2, D.AddMonths(1), 100m),
        new(4, 30, 4, D.AddMonths(12), 50m),
        new(5, 20, 5, D.AddMonths(3), 0m), // settled
    ];

    // ----------------------------------------------------------------------------- allocation engine
    [Fact]
    public void OldestDueFirst_SettlesEarliestLines_AndLeavesRemainderUnallocated()
    {
        var plan = AllocationEngine.Plan(Items(), 650m, AllocationMode.OldestDueFirst);
        Assert.Equal([1L, 2L, 3L], plan.Lines.Select(l => l.ItemId));
        Assert.Equal([500m, 100m, 50m], plan.Lines.Select(l => l.Amount));
        Assert.Equal(0m, plan.Unallocated);
        var over = AllocationEngine.Plan(Items(), 1_000m, AllocationMode.OldestDueFirst);
        Assert.Equal(750m, over.Allocated);
        Assert.Equal(250m, over.Unallocated);
        var toAdvance = AllocationEngine.Plan(Items(), 1_000m, AllocationMode.OldestDueFirst, remainderToAdvance: true);
        Assert.Equal(250m, toAdvance.Advance);
        Assert.Equal(0m, toAdvance.Unallocated);
    }

    [Fact]
    public void Plan_AlwaysAddsUpToTheAmount()
    {
        foreach (var amount in new[] { 0.01m, 99.99m, 500m, 750m, 10_000m })
            foreach (var mode in new[] { AllocationMode.OldestDueFirst, AllocationMode.Advance, AllocationMode.Unallocated })
            {
                var p = AllocationEngine.Plan(Items(), amount, mode);
                Assert.Equal(amount, p.Allocated + p.Advance + p.Unallocated);
                Assert.All(p.Lines, l => Assert.True(l.Amount <= Items().Single(i => i.ItemId == l.ItemId).Outstanding));
            }
    }

    [Fact]
    public void ChargeSpecific_OnlyTouchesThatCharge()
    {
        var plan = AllocationEngine.Plan(Items(), 150m, AllocationMode.ChargeSpecific, chargeId: 20);
        Assert.Equal([2L, 3L], plan.Lines.Select(l => l.ItemId));
        Assert.Equal(0m, plan.Unallocated);
        Assert.Throws<DomainException>(() => AllocationEngine.Plan(Items(), 10m, AllocationMode.ChargeSpecific));
        Assert.Throws<DomainException>(() => AllocationEngine.Plan(Items(), 10m, AllocationMode.ChargeSpecific, chargeId: 99));
    }

    [Fact]
    public void Manual_And_Specific_RefuseOverAllocation()
    {
        var plan = AllocationEngine.Plan(Items(), 300m, AllocationMode.Manual, [new(4, 50m), new(1, 200m)]);
        Assert.Equal(250m, plan.Allocated);
        Assert.Equal(50m, plan.Unallocated);
        Assert.Throws<DomainException>(() => AllocationEngine.Plan(Items(), 300m, AllocationMode.Manual, [new(4, 60m)])); // above the line's outstanding
        Assert.Throws<DomainException>(() => AllocationEngine.Plan(Items(), 100m, AllocationMode.Manual, [new(1, 101m)])); // above the payment
        Assert.Throws<DomainException>(() => AllocationEngine.Plan(Items(), 100m, AllocationMode.Manual, [new(1, 50m), new(1, 10m)])); // duplicate line
        Assert.Throws<DomainException>(() => AllocationEngine.Plan(Items(), 100m, AllocationMode.Manual, [new(99, 10m)])); // unknown line
        Assert.Throws<DomainException>(() => AllocationEngine.Plan(Items(), 100m, AllocationMode.Manual, [new(5, 10m)])); // settled line
        Assert.Throws<DomainException>(() => AllocationEngine.Plan(Items(), 100m, AllocationMode.SpecificInstallment, [new(1, 10m), new(2, 10m)]));
        Assert.Throws<DomainException>(() => AllocationEngine.Plan(Items(), 100m, AllocationMode.Manual, []));
        var specific = AllocationEngine.Plan(Items(), 100m, AllocationMode.SpecificInstallment, [new(2, 100m)]);
        Assert.Single(specific.Lines);
    }

    [Fact]
    public void Advance_And_Unallocated_HoldTheWholeAmount_AndAmountsAreValidated()
    {
        Assert.Equal(400m, AllocationEngine.Plan(Items(), 400m, AllocationMode.Advance).Advance);
        Assert.Equal(400m, AllocationEngine.Plan(Items(), 400m, AllocationMode.Unallocated).Unallocated);
        Assert.Throws<DomainException>(() => AllocationEngine.Plan(Items(), 0m, AllocationMode.OldestDueFirst));
        Assert.Throws<DomainException>(() => AllocationEngine.Plan(Items(), 1.005m, AllocationMode.OldestDueFirst));
    }

    // ----------------------------------------------------------------------------- receivables
    private static readonly Dictionary<long, (decimal Due, decimal Waived)> None = new();

    [Fact]
    public void Receivables_ArePostedOnlyForLinesThatHaveFallenDue()
    {
        var today = D.AddMonths(1);
        var lines = new[] { new DueLine(1, 1, D, 500m, 0m, "Down"), new DueLine(2, 2, D.AddMonths(1), 100m, 0m, "M1"), new DueLine(3, 3, D.AddMonths(2), 100m, 0m, "M2") };
        var deltas = ReceivableEngine.Compute(lines, None, today, agreementActive: true);
        Assert.Equal(2, deltas.Count);
        Assert.All(deltas, d => Assert.True(d.FirstPosting));
        Assert.Equal(600m, deltas.Sum(d => d.DueDelta));
        Assert.Equal(D, deltas[0].EntryDate); // dated on the due date
        // already posted: nothing to do (idempotent)
        var posted = new Dictionary<long, (decimal, decimal)> { [1] = (500m, 0m), [2] = (100m, 0m) };
        Assert.Empty(ReceivableEngine.Compute(lines, posted, today, true));
    }

    [Fact]
    public void Receivables_AreReversed_WhenALineMovesToTheFuture_OrTheAgreementIsCancelled_AndWaiversFollowDueLines()
    {
        var today = D.AddMonths(1);
        var posted = new Dictionary<long, (decimal, decimal)> { [1] = (500m, 0m), [2] = (100m, 0m) };
        var deferred = new[] { new DueLine(1, 1, D, 500m, 0m, "Down"), new DueLine(2, 7, D.AddMonths(3), 100m, 0m, "M1") };
        var d = Assert.Single(ReceivableEngine.Compute(deferred, posted, today, true));
        Assert.Equal(2, d.RootId);
        Assert.Equal(-100m, d.DueDelta);
        Assert.Equal(today, d.EntryDate);
        Assert.False(d.FirstPosting);

        var cancelled = ReceivableEngine.Compute(deferred, posted, today, agreementActive: false);
        Assert.Equal(-600m, cancelled.Sum(x => x.DueDelta));

        var waived = new[] { new DueLine(1, 1, D, 500m, 50m, "Down"), new DueLine(3, 3, D.AddMonths(5), 100m, 20m, "Future") };
        var w = ReceivableEngine.Compute(waived, new Dictionary<long, (decimal, decimal)> { [1] = (500m, 0m) }, today, true);
        Assert.Equal(50m, Assert.Single(w).WaiverDelta); // a waiver on a future line is not posted before the line falls due
    }

    [Fact]
    public void Roots_FollowTheVersionChain()
    {
        var roots = ReceivableEngine.Roots(new Dictionary<long, long?> { [1] = null, [5] = 1, [9] = 5, [2] = null, [6] = 2, [7] = null });
        Assert.Equal(1, roots[9]);
        Assert.Equal(1, roots[5]);
        Assert.Equal(2, roots[6]);
        Assert.Equal(7, roots[7]);
    }

    // ----------------------------------------------------------------------------- payment workflow
    [Fact]
    public void PaymentStatus_Transitions_AndReleaseOfTheReferenceKey()
    {
        Assert.True(Payment.CanTransition(PaymentStatus.PendingVerification, PaymentStatus.PendingApproval));
        Assert.True(Payment.CanTransition(PaymentStatus.PendingApproval, PaymentStatus.Posted));
        Assert.True(Payment.CanTransition(PaymentStatus.Posted, PaymentStatus.Reversed));
        Assert.False(Payment.CanTransition(PaymentStatus.PendingVerification, PaymentStatus.Posted)); // unverified proof never posts
        Assert.False(Payment.CanTransition(PaymentStatus.Posted, PaymentStatus.Rejected));
        Assert.False(Payment.CanTransition(PaymentStatus.Reversed, PaymentStatus.Posted));
        Assert.False(Payment.CanTransition(PaymentStatus.Rejected, PaymentStatus.PendingApproval));
        var p = new Payment { Status = PaymentStatus.PendingApproval, ActiveReferenceKey = "2:TRX1" };
        p.Move(PaymentStatus.Posted);
        Assert.Equal("2:TRX1", p.ActiveReferenceKey);
        p.Move(PaymentStatus.Reversed);
        Assert.Null(p.ActiveReferenceKey);
        Assert.Throws<DomainException>(() => p.Move(PaymentStatus.Posted));
    }
}
