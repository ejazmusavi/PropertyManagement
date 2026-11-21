import '../../../core/util/json.dart';

/// Status shown for one installment (computed by the server from amounts and the due date).
enum InstallmentStatus {
  paid('Paid'),
  partiallyPaid('Partly paid'),
  upcoming('Upcoming'),
  overdue('Overdue'),
  waived('Waived');

  const InstallmentStatus(this.label);
  final String label;

  static InstallmentStatus parse(String s) => switch (s) {
        'Paid' => paid,
        'PartiallyPaid' => partiallyPaid,
        'Overdue' => overdue,
        'Waived' => waived,
        _ => upcoming,
      };
}

class Installment {
  Installment({required this.itemId, required this.lineNo, required this.installmentNo, required this.installmentCount, required this.description, required this.dueDate, required this.amount, required this.paid, required this.waived, required this.outstanding, required this.lastPaymentDate, required this.status});

  factory Installment.fromJson(Map<String, dynamic> j) => Installment(
        itemId: J.integer(j['itemId']),
        lineNo: J.integer(j['lineNo']),
        installmentNo: J.integer(j['installmentNo']),
        installmentCount: J.integer(j['installmentCount']),
        description: J.str(j['description']),
        dueDate: J.date(j['dueDate']),
        amount: J.money(j['amount']),
        paid: J.money(j['paid']),
        waived: J.money(j['waived']),
        outstanding: J.money(j['outstanding']),
        lastPaymentDate: J.dateOrNull(j['lastPaymentDate']),
        status: InstallmentStatus.parse(J.str(j['status'])),
      );

  final int itemId;
  final int lineNo;
  final int installmentNo;
  final int installmentCount;
  final String description;
  final DateTime dueDate;
  final double amount;
  final double paid;
  final double waived;
  final double outstanding;
  final DateTime? lastPaymentDate;
  final InstallmentStatus status;
}

class ScheduleSummary {
  ScheduleSummary({required this.totalAmount, required this.paid, required this.waived, required this.outstanding, required this.outstandingDue, required this.overdue, required this.notYetDue, required this.paidCount, required this.outstandingCount, required this.overdueCount});

  factory ScheduleSummary.fromJson(Map<String, dynamic> j) => ScheduleSummary(
        totalAmount: J.money(j['totalAmount']),
        paid: J.money(j['paid']),
        waived: J.money(j['waived']),
        outstanding: J.money(j['outstanding']),
        outstandingDue: J.money(j['outstandingDue']),
        overdue: J.money(j['overdue']),
        notYetDue: J.money(j['notYetDue']),
        paidCount: J.integer(j['paidCount']),
        outstandingCount: J.integer(j['outstandingCount']),
        overdueCount: J.integer(j['overdueCount']),
      );

  final double totalAmount;
  final double paid;
  final double waived;
  final double outstanding;
  final double outstandingDue;
  final double overdue;
  final double notYetDue;
  final int paidCount;
  final int outstandingCount;
  final int overdueCount;
}

class Schedule {
  Schedule({required this.agreementId, required this.agreementNumber, required this.currencySymbol, required this.summary, required this.items});

  factory Schedule.fromJson(Map<String, dynamic> j) => Schedule(
        agreementId: J.integer(j['agreementId']),
        agreementNumber: J.str(j['agreementNumber']),
        currencySymbol: J.str(j['currencySymbol']),
        summary: ScheduleSummary.fromJson(J.map(j['summary'])),
        items: J.list(j['items']).map(Installment.fromJson).toList(),
      );

  final int agreementId;
  final String agreementNumber;
  final String currencySymbol;
  final ScheduleSummary summary;
  final List<Installment> items;
}
