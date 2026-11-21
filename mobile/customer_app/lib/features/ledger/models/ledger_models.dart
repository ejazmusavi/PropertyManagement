import '../../../core/util/json.dart';
import '../../../core/util/paged.dart';

enum LedgerEntryType {
  installmentDue(1, 'Installment due'),
  dueAdjustment(2, 'Due adjustment'),
  paymentReceived(3, 'Payment received'),
  paymentReversed(4, 'Payment reversed'),
  waiver(5, 'Waiver'),
  waiverAdjustment(6, 'Waiver adjustment'),
  refundPaid(7, 'Refund paid'),
  balanceTransferIn(8, 'Balance transferred in'),
  balanceTransferOut(9, 'Balance transferred out');

  const LedgerEntryType(this.code, this.label);
  final int code;
  final String label;
}

class LedgerEntry {
  LedgerEntry({required this.id, required this.entryDate, required this.type, required this.description, required this.debit, required this.credit, required this.balance, required this.agreementId, required this.agreementNumber, required this.paymentNumber});

  factory LedgerEntry.fromJson(Map<String, dynamic> j) => LedgerEntry(
        id: J.integer(j['id']),
        entryDate: J.date(j['entryDate']),
        type: enumFrom(LedgerEntryType.values, (e) => e.code, j['entryType'], LedgerEntryType.installmentDue),
        description: J.str(j['description']),
        debit: J.money(j['debit']),
        credit: J.money(j['credit']),
        balance: J.money(j['balance']),
        agreementId: J.integer(j['agreementId']),
        agreementNumber: J.strOrNull(j['agreementNumber']),
        paymentNumber: J.strOrNull(j['paymentNumber']),
      );

  final int id;
  final DateTime entryDate;
  final LedgerEntryType type;
  final String description;
  final double debit;
  final double credit;
  final double balance;
  final int agreementId;
  final String? agreementNumber;
  final String? paymentNumber;
}

class AgreementSummary {
  AgreementSummary({required this.agreementValue, required this.discounts, required this.charges, required this.contractTotal, required this.scheduledTotal, required this.paymentsReceived, required this.paymentsReversed, required this.waivers, required this.penaltiesAccrued, required this.refunds, required this.outstanding, required this.overdue, required this.notYetDue, required this.advanceBalance, required this.unallocatedBalance, required this.ledgerBalance, required this.reconciled});

  factory AgreementSummary.fromJson(Map<String, dynamic> j) => AgreementSummary(
        agreementValue: J.money(j['agreementValue']),
        discounts: J.money(j['discounts']),
        charges: J.money(j['charges']),
        contractTotal: J.money(j['contractTotal']),
        scheduledTotal: J.money(j['scheduledTotal']),
        paymentsReceived: J.money(j['paymentsReceived']),
        paymentsReversed: J.money(j['paymentsReversed']),
        waivers: J.money(j['waivers']),
        penaltiesAccrued: J.money(j['penaltiesAccrued']),
        refunds: J.money(j['refunds']),
        outstanding: J.money(j['outstanding']),
        overdue: J.money(j['overdue']),
        notYetDue: J.money(j['notYetDue']),
        advanceBalance: J.money(j['advanceBalance']),
        unallocatedBalance: J.money(j['unallocatedBalance']),
        ledgerBalance: J.money(j['ledgerBalance']),
        reconciled: J.boolean(j['reconciled']),
      );

  final double agreementValue;
  final double discounts;
  final double charges;
  final double contractTotal;
  final double scheduledTotal;
  final double paymentsReceived;
  final double paymentsReversed;
  final double waivers;
  final double penaltiesAccrued;
  final double refunds;
  final double outstanding;
  final double overdue;
  final double notYetDue;
  final double advanceBalance;
  final double unallocatedBalance;
  final double ledgerBalance;
  final bool reconciled;

  double get paid => paymentsReceived - paymentsReversed;
}

class AgreementBalance {
  AgreementBalance({required this.agreementId, required this.agreementNumber, required this.projectCode, required this.isHolder, required this.balance, required this.outstanding, required this.overdue, required this.advanceBalance});

  factory AgreementBalance.fromJson(Map<String, dynamic> j) => AgreementBalance(
        agreementId: J.integer(j['agreementId']),
        agreementNumber: J.str(j['agreementNumber']),
        projectCode: J.str(j['projectCode']),
        isHolder: J.boolean(j['isHolder']),
        balance: J.money(j['balance']),
        outstanding: J.money(j['outstanding']),
        overdue: J.money(j['overdue']),
        advanceBalance: J.money(j['advanceBalance']),
      );

  final int agreementId;
  final String agreementNumber;
  final String projectCode;
  final bool isHolder;
  final double balance;
  final double outstanding;
  final double overdue;
  final double advanceBalance;
}

class CustomerLedger {
  CustomerLedger({required this.customerName, required this.currencySymbol, required this.totalDebit, required this.totalCredit, required this.balance, required this.outstanding, required this.overdue, required this.advanceBalance, required this.reconciled, required this.agreements, required this.entries});

  factory CustomerLedger.fromJson(Map<String, dynamic> j) => CustomerLedger(
        customerName: J.str(j['customerName']),
        currencySymbol: J.str(j['currencySymbol']),
        totalDebit: J.money(j['totalDebit']),
        totalCredit: J.money(j['totalCredit']),
        balance: J.money(j['balance']),
        outstanding: J.money(j['outstanding']),
        overdue: J.money(j['overdue']),
        advanceBalance: J.money(j['advanceBalance']),
        reconciled: J.boolean(j['reconciled']),
        agreements: J.list(j['agreements']).map(AgreementBalance.fromJson).toList(),
        entries: Paged.from(J.map(j['entries']), LedgerEntry.fromJson),
      );

  final String customerName;
  final String currencySymbol;
  final double totalDebit;
  final double totalCredit;
  final double balance;
  final double outstanding;
  final double overdue;
  final double advanceBalance;
  final bool reconciled;
  final List<AgreementBalance> agreements;
  final Paged<LedgerEntry> entries;
}

class AgreementLedger {
  AgreementLedger({required this.agreementNumber, required this.currencySymbol, required this.properties, required this.summary, required this.entries});

  factory AgreementLedger.fromJson(Map<String, dynamic> j) => AgreementLedger(
        agreementNumber: J.str(j['agreementNumber']),
        currencySymbol: J.str(j['currencySymbol']),
        properties: (j['properties'] as List? ?? const []).map((e) => e.toString()).toList(),
        summary: AgreementSummary.fromJson(J.map(j['summary'])),
        entries: Paged.from(J.map(j['entries']), LedgerEntry.fromJson),
      );

  final String agreementNumber;
  final String currencySymbol;
  final List<String> properties;
  final AgreementSummary summary;
  final Paged<LedgerEntry> entries;
}
