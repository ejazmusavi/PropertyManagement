using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RealEstate.Domain.Entities;

namespace RealEstate.Infrastructure.EntityConfigurations;

public class AccountGroupConfiguration : IEntityTypeConfiguration<AccountGroup>
{
    public void Configure(EntityTypeBuilder<AccountGroup> b)
    {
        b.ToTable("AccountGroups");
        b.Audit();
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.Nature).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Classification).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.CashFlowSection).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<AccountGroup>().WithMany().HasForeignKey(x => x.ParentGroupId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => x.Classification);
    }
}

public class ChartOfAccountConfiguration : IEntityTypeConfiguration<ChartOfAccount>
{
    public void Configure(EntityTypeBuilder<ChartOfAccount> b)
    {
        b.ToTable("ChartOfAccounts");
        b.Audit();
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.Nature).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<AccountGroup>().WithMany().HasForeignKey(x => x.AccountGroupId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ChartOfAccount>().WithMany().HasForeignKey(x => x.ParentAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => x.Name);
    }
}

public class FiscalYearConfiguration : IEntityTypeConfiguration<FiscalYear>
{
    public void Configure(EntityTypeBuilder<FiscalYear> b)
    {
        b.ToTable("FiscalYears", t => t.HasCheckConstraint("CK_FiscalYears_Dates", "`EndDate` > `StartDate`"));
        b.Audit();
        b.Property(x => x.Name).HasMaxLength(50).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ClosedBy).HasMaxLength(256);
        b.Property(x => x.ClosedAtUtc).HasPrecision(6);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasMany(x => x.Periods).WithOne().HasForeignKey(p => p.FiscalYearId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Name).IsUnique();
        b.HasIndex(x => x.StartDate).IsUnique();
    }
}

public class AccountingPeriodConfiguration : IEntityTypeConfiguration<AccountingPeriod>
{
    public void Configure(EntityTypeBuilder<AccountingPeriod> b)
    {
        b.ToTable("AccountingPeriods", t => t.HasCheckConstraint("CK_AccountingPeriods_Dates", "`EndDate` >= `StartDate`"));
        b.Property(x => x.Name).HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasIndex(x => new { x.FiscalYearId, x.PeriodNo }).IsUnique();
        b.HasIndex(x => x.StartDate).IsUnique();
        b.HasIndex(x => new { x.StartDate, x.EndDate });
    }
}

public class AccountingPeriodLockConfiguration : IEntityTypeConfiguration<AccountingPeriodLock>
{
    public void Configure(EntityTypeBuilder<AccountingPeriodLock> b)
    {
        b.ToTable("AccountingPeriodLocks");
        b.Property(x => x.Action).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        b.Property(x => x.By).HasMaxLength(256).IsRequired();
        b.Property(x => x.AtUtc).HasPrecision(6);
        b.HasOne<AccountingPeriod>().WithMany().HasForeignKey(x => x.PeriodId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PeriodId, x.AtUtc });
    }
}

public class CostCenterConfiguration : IEntityTypeConfiguration<CostCenter>
{
    public void Configure(EntityTypeBuilder<CostCenter> b)
    {
        b.ToTable("CostCenters");
        b.Audit();
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

public class JournalEntryConfiguration : IEntityTypeConfiguration<JournalEntry>
{
    public void Configure(EntityTypeBuilder<JournalEntry> b)
    {
        b.ToTable("JournalEntries", t =>
        {
            t.HasCheckConstraint("CK_JournalEntries_Totals", "`TotalDebit` >= 0 AND `TotalCredit` >= 0");
            t.HasCheckConstraint("CK_JournalEntries_PostedBalanced", "`Status` <> 'Posted' OR (`TotalDebit` = `TotalCredit` AND `TotalDebit` > 0)");
        });
        b.Audit();
        b.Property(x => x.EntryNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(100);
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Narration).HasMaxLength(500).IsRequired();
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.SourceType).HasMaxLength(40);
        b.Property(x => x.ReversalReason).HasMaxLength(500);
        b.Property(x => x.TotalDebit).HasColumnType("decimal(18,2)");
        b.Property(x => x.TotalCredit).HasColumnType("decimal(18,2)");
        b.Property(x => x.VoucherNumber).HasMaxLength(30);
        foreach (var p in new[] { nameof(JournalEntry.SubmittedBy), nameof(JournalEntry.ApprovedBy), nameof(JournalEntry.RejectedBy), nameof(JournalEntry.CancelledBy) })
            b.Property(p).HasMaxLength(256);
        foreach (var p in new[] { nameof(JournalEntry.SubmittedAtUtc), nameof(JournalEntry.ApprovedAtUtc), nameof(JournalEntry.PostedAtUtc), nameof(JournalEntry.RejectedAtUtc), nameof(JournalEntry.CancelledAtUtc) })
            b.Property(p).HasPrecision(6);
        b.Property(x => x.ApprovalComment).HasMaxLength(500);
        b.Property(x => x.RejectionComment).HasMaxLength(500);
        b.Property(x => x.CancelReason).HasMaxLength(500);
        b.Property(x => x.ClosedPeriodReason).HasMaxLength(500);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FiscalYear>().WithMany().HasForeignKey(x => x.FiscalYearId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AccountingPeriod>().WithMany().HasForeignKey(x => x.PeriodId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PostingRule>().WithMany().HasForeignKey(x => x.PostingRuleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AccountingPostingRequest>().WithMany().HasForeignKey(x => x.PostingRequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.ReversalOfJournalId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.EntryNumber).IsUnique();
        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        b.HasIndex(x => x.PostingRequestId).IsUnique();
        b.HasIndex(x => x.ReversalOfJournalId).IsUnique();
        b.HasIndex(x => x.VoucherNumber).IsUnique();
        b.HasIndex(x => new { x.Status, x.EntryDate });
        b.HasIndex(x => new { x.SourceType, x.SourceId });
    }
}

public class JournalEntryLineConfiguration : IEntityTypeConfiguration<JournalEntryLine>
{
    public void Configure(EntityTypeBuilder<JournalEntryLine> b)
    {
        b.ToTable("JournalEntryLines", t =>
            t.HasCheckConstraint("CK_JournalEntryLines_OneSide", "(`Debit` > 0 AND `Credit` = 0) OR (`Credit` > 0 AND `Debit` = 0)"));
        b.Property(x => x.Debit).HasColumnType("decimal(18,2)");
        b.Property(x => x.Credit).HasColumnType("decimal(18,2)");
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Reference).HasMaxLength(100);
        b.HasOne<ChartOfAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CostCenter>().WithMany().HasForeignKey(x => x.CostCenterId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.JournalEntryId, x.LineNo }).IsUnique();
    }
}

public class GeneralLedgerEntryConfiguration : IEntityTypeConfiguration<GeneralLedgerEntry>
{
    public void Configure(EntityTypeBuilder<GeneralLedgerEntry> b)
    {
        b.ToTable("GeneralLedgerEntries", t =>
            t.HasCheckConstraint("CK_GeneralLedgerEntries_OneSide", "(`Debit` > 0 AND `Credit` = 0) OR (`Credit` > 0 AND `Debit` = 0)"));
        b.Property(x => x.Debit).HasColumnType("decimal(18,2)");
        b.Property(x => x.Credit).HasColumnType("decimal(18,2)");
        b.Property(x => x.VoucherNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Narration).HasMaxLength(500).IsRequired();
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.SourceType).HasMaxLength(40);
        b.Property(x => x.PostedAtUtc).HasPrecision(6);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntryLine>().WithMany().HasForeignKey(x => x.JournalEntryLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ChartOfAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CostCenter>().WithMany().HasForeignKey(x => x.CostCenterId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AccountingPeriod>().WithMany().HasForeignKey(x => x.PeriodId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FiscalYear>().WithMany().HasForeignKey(x => x.FiscalYearId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.JournalEntryLineId).IsUnique();
        b.HasIndex(x => new { x.AccountId, x.EntryDate });
        b.HasIndex(x => new { x.ProjectId, x.AccountId, x.EntryDate });
        b.HasIndex(x => new { x.CostCenterId, x.EntryDate });
        b.HasIndex(x => x.EntryDate);
    }
}

public class AccountingVoucherConfiguration : IEntityTypeConfiguration<AccountingVoucher>
{
    public void Configure(EntityTypeBuilder<AccountingVoucher> b)
    {
        b.ToTable("AccountingVouchers", t => t.HasCheckConstraint("CK_AccountingVouchers_Sequence", "`SequenceNo` > 0"));
        b.Property(x => x.VoucherType).HasConversion<string>().HasMaxLength(5);
        b.Property(x => x.VoucherNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.IssuedBy).HasMaxLength(256).IsRequired();
        b.Property(x => x.IssuedAtUtc).HasPrecision(6);
        b.HasOne<FiscalYear>().WithMany().HasForeignKey(x => x.FiscalYearId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.VoucherType, x.FiscalYearId, x.SequenceNo }).IsUnique();
        b.HasIndex(x => x.VoucherNumber).IsUnique();
        b.HasIndex(x => x.JournalEntryId).IsUnique();
    }
}

public class PostingRuleConfiguration : IEntityTypeConfiguration<PostingRule>
{
    public void Configure(EntityTypeBuilder<PostingRule> b)
    {
        b.ToTable("PostingRules");
        b.Audit();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.EventType).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.ConditionField).HasMaxLength(40);
        b.Property(x => x.ConditionValue).HasMaxLength(100);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.SubmittedBy).HasMaxLength(256);
        b.Property(x => x.ApprovedBy).HasMaxLength(256);
        b.Property(x => x.RetiredBy).HasMaxLength(256);
        b.Property(x => x.DecisionComment).HasMaxLength(500);
        b.Property(x => x.SubmittedAtUtc).HasPrecision(6);
        b.Property(x => x.ApprovedAtUtc).HasPrecision(6);
        b.Property(x => x.RetiredAtUtc).HasPrecision(6);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.PostingRuleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.EventType, x.Status });
    }
}

public class PostingRuleLineConfiguration : IEntityTypeConfiguration<PostingRuleLine>
{
    public void Configure(EntityTypeBuilder<PostingRuleLine> b)
    {
        b.ToTable("PostingRuleLines", t => t.HasCheckConstraint("CK_PostingRuleLines_Account", "(`AccountId` IS NULL) <> (`AccountKey` IS NULL)"));
        b.Property(x => x.Side).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.AccountKey).HasMaxLength(40);
        b.Property(x => x.AmountKey).HasMaxLength(40).IsRequired();
        b.Property(x => x.Description).HasMaxLength(200);
        b.HasOne<ChartOfAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CostCenter>().WithMany().HasForeignKey(x => x.CostCenterId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PostingRuleId, x.LineNo }).IsUnique();
    }
}

public class BankAccountConfiguration : IEntityTypeConfiguration<BankAccount>
{
    public void Configure(EntityTypeBuilder<BankAccount> b)
    {
        b.ToTable("BankAccounts");
        b.Audit();
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.BankName).HasMaxLength(150).IsRequired();
        b.Property(x => x.Branch).HasMaxLength(150);
        b.Property(x => x.AccountTitle).HasMaxLength(150).IsRequired();
        b.Property(x => x.AccountNumber).HasMaxLength(50).IsRequired();
        b.Property(x => x.Iban).HasMaxLength(50);
        b.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<ChartOfAccount>().WithMany().HasForeignKey(x => x.GlAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => x.GlAccountId).IsUnique();
    }
}

public class CashAccountConfiguration : IEntityTypeConfiguration<CashAccount>
{
    public void Configure(EntityTypeBuilder<CashAccount> b)
    {
        b.ToTable("CashAccounts");
        b.Audit();
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.Custodian).HasMaxLength(150);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<ChartOfAccount>().WithMany().HasForeignKey(x => x.GlAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => x.GlAccountId).IsUnique();
    }
}

public class BankTransactionConfiguration : IEntityTypeConfiguration<BankTransaction>
{
    public void Configure(EntityTypeBuilder<BankTransaction> b)
    {
        b.ToTable("BankTransactions", t =>
            t.HasCheckConstraint("CK_BankTransactions_OneSide", "(`Deposit` > 0 AND `Withdrawal` = 0) OR (`Withdrawal` > 0 AND `Deposit` = 0)"));
        b.Property(x => x.ImportBatch).HasMaxLength(40).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500).IsRequired();
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.Deposit).HasColumnType("decimal(18,2)");
        b.Property(x => x.Withdrawal).HasColumnType("decimal(18,2)");
        b.Property(x => x.RunningBalance).HasColumnType("decimal(18,2)");
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.MatchedBy).HasMaxLength(256);
        b.Property(x => x.MatchedAtUtc).HasPrecision(6);
        b.Property(x => x.IgnoreReason).HasMaxLength(500);
        b.Property(x => x.LineHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.ImportedBy).HasMaxLength(256).IsRequired();
        b.Property(x => x.ImportedAtUtc).HasPrecision(6);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<BankAccount>().WithMany().HasForeignKey(x => x.BankAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<GeneralLedgerEntry>().WithMany().HasForeignKey(x => x.MatchedLedgerEntryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BankReconciliation>().WithMany().HasForeignKey(x => x.ReconciliationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.BankAccountId, x.LineHash }).IsUnique();
        b.HasIndex(x => x.MatchedLedgerEntryId).IsUnique();
        b.HasIndex(x => new { x.BankAccountId, x.Status, x.TransactionDate });
    }
}

public class BankReconciliationConfiguration : IEntityTypeConfiguration<BankReconciliation>
{
    public void Configure(EntityTypeBuilder<BankReconciliation> b)
    {
        b.ToTable("BankReconciliations");
        b.Audit();
        foreach (var p in new[] { nameof(BankReconciliation.StatementBalance), nameof(BankReconciliation.BookBalance), nameof(BankReconciliation.UnclearedBookNet),
                     nameof(BankReconciliation.UnmatchedStatementNet), nameof(BankReconciliation.Difference) })
            b.Property(p).HasColumnType("decimal(18,2)");
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CompletedBy).HasMaxLength(256);
        b.Property(x => x.CompletedAtUtc).HasPrecision(6);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<BankAccount>().WithMany().HasForeignKey(x => x.BankAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.BankAccountId, x.StatementDate });
    }
}

public class ProjectCostAllocationConfiguration : IEntityTypeConfiguration<ProjectCostAllocation>
{
    public void Configure(EntityTypeBuilder<ProjectCostAllocation> b)
    {
        b.ToTable("ProjectCostAllocations", t => t.HasCheckConstraint("CK_ProjectCostAllocations_Amount", "`Amount` > 0"));
        b.Audit();
        b.Property(x => x.AllocationNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Basis).HasMaxLength(500).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.AllocationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ChartOfAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.SourceProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CostCenter>().WithMany().HasForeignKey(x => x.SourceCostCenterId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.AllocationNumber).IsUnique();
        b.HasIndex(x => x.JournalEntryId).IsUnique();
    }
}

public class ProjectCostAllocationLineConfiguration : IEntityTypeConfiguration<ProjectCostAllocationLine>
{
    public void Configure(EntityTypeBuilder<ProjectCostAllocationLine> b)
    {
        b.ToTable("ProjectCostAllocationLines", t =>
        {
            t.HasCheckConstraint("CK_ProjectCostAllocationLines_Amount", "`Amount` > 0");
            t.HasCheckConstraint("CK_ProjectCostAllocationLines_Percentage", "`Percentage` > 0 AND `Percentage` <= 100");
        });
        b.Property(x => x.Percentage).HasColumnType("decimal(9,4)");
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CostCenter>().WithMany().HasForeignKey(x => x.CostCenterId).OnDelete(DeleteBehavior.Restrict);
    }
}
