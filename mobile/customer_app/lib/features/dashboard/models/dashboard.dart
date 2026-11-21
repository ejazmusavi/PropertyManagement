import '../../../core/util/json.dart';

class DueItem {
  DueItem({required this.agreementId, required this.agreementNumber, required this.lineNo, required this.description, required this.dueDate, required this.amount, required this.outstanding, required this.isOverdue});

  factory DueItem.fromJson(Map<String, dynamic> j) => DueItem(
        agreementId: J.integer(j['agreementId']),
        agreementNumber: J.str(j['agreementNumber']),
        lineNo: J.integer(j['lineNo']),
        description: J.str(j['description']),
        dueDate: J.date(j['dueDate']),
        amount: J.money(j['amount']),
        outstanding: J.money(j['outstanding']),
        isOverdue: J.boolean(j['isOverdue']),
      );

  final int agreementId;
  final String agreementNumber;
  final int lineNo;
  final String description;
  final DateTime dueDate;
  final double amount;
  final double outstanding;
  final bool isOverdue;
}

class Dashboard {
  Dashboard({
    required this.customerName,
    required this.customerCode,
    required this.currencySymbol,
    required this.totalProperties,
    required this.activeAgreements,
    required this.totalAgreementValue,
    required this.paidAmount,
    required this.outstandingAmount,
    required this.overdueAmount,
    required this.notYetDueAmount,
    required this.advanceBalance,
    required this.unreadNotifications,
    required this.onlinePaymentsEnabled,
    required this.upcomingDues,
  });

  factory Dashboard.fromJson(Map<String, dynamic> j) => Dashboard(
        customerName: J.str(j['customerName']),
        customerCode: J.str(j['customerCode']),
        currencySymbol: J.str(j['currencySymbol']),
        totalProperties: J.integer(j['totalProperties']),
        activeAgreements: J.integer(j['activeAgreements']),
        totalAgreementValue: J.money(j['totalAgreementValue']),
        paidAmount: J.money(j['paidAmount']),
        outstandingAmount: J.money(j['outstandingAmount']),
        overdueAmount: J.money(j['overdueAmount']),
        notYetDueAmount: J.money(j['notYetDueAmount']),
        advanceBalance: J.money(j['advanceBalance']),
        unreadNotifications: J.integer(j['unreadNotifications']),
        onlinePaymentsEnabled: J.boolean(j['onlinePaymentsEnabled']),
        upcomingDues: J.list(j['upcomingDues']).map(DueItem.fromJson).toList(),
      );

  final String customerName;
  final String customerCode;
  final String currencySymbol;
  final int totalProperties;
  final int activeAgreements;
  final double totalAgreementValue;
  final double paidAmount;
  final double outstandingAmount;
  final double overdueAmount;
  final double notYetDueAmount;
  final double advanceBalance;
  final int unreadNotifications;
  final bool onlinePaymentsEnabled;
  final List<DueItem> upcomingDues;
}
