import 'package:customer_app/features/dashboard/models/dashboard.dart';
import 'package:customer_app/features/installments/models/installment_models.dart';
import 'package:customer_app/features/ledger/models/ledger_models.dart';
import 'package:customer_app/features/payments/models/payment_models.dart';
import 'package:customer_app/features/properties/models/property_models.dart';
import 'package:customer_app/features/support/models/support_models.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/fake_server.dart';

void main() {
  final server = FakeServer();

  test('dashboard figures are read as sent by the backend', () {
    final d = Dashboard.fromJson(server.dashboard());
    expect(d.totalProperties, 1);
    expect(d.totalAgreementValue, 5000000);
    expect(d.paidAmount, 600000);
    expect(d.outstandingAmount, 162500);
    expect(d.upcomingDues.single.isOverdue, isTrue);
    expect(d.upcomingDues.single.dueDate, DateTime(2026, 10, 1));
  });

  test('numeric enums map to their meaning and unknown codes fall back safely', () {
    final p = PortalProperty.fromJson(server.property());
    expect(p.bookingStatus, BookingStatus.confirmed);
    expect(p.agreementStatus, AgreementStatus.active);
    expect(p.paymentMode, PaymentMode.installment);
    expect(p.unit, 'Marla');
    final odd = PortalProperty.fromJson({...server.property(), 'bookingStatus': 99, 'unit': 77});
    expect(odd.bookingStatus, BookingStatus.confirmed);
    expect(odd.unit, '');
    expect(PortalPayment.fromJson(server.payment(status: 1)).status, PaymentStatus.pendingVerification);
    expect(PortalPayment.fromJson(server.payment(status: 5)).status, PaymentStatus.reversed);
    expect(PortalPayment.fromJson(server.payment(status: 3)).hasReceipt, isTrue);
    expect(PortalPayment.fromJson(server.payment(status: 1)).hasReceipt, isFalse);
  });

  test('installment statuses and totals reconcile with the schedule summary', () {
    final s = Schedule.fromJson(server.schedule());
    expect(s.items.map((i) => i.status), [InstallmentStatus.paid, InstallmentStatus.overdue, InstallmentStatus.upcoming]);
    expect(s.summary.totalAmount, s.summary.paid + s.summary.waived + s.summary.outstanding);
    expect(s.items.fold<double>(0, (a, i) => a + i.outstanding), s.summary.outstanding - 4237500 + 162500); // overdue + one upcoming
    expect(s.summary.overdueCount, s.items.where((i) => i.status == InstallmentStatus.overdue).length);
  });

  test('ledger entries, balances and the reconciliation flag are kept', () {
    final l = CustomerLedger.fromJson(server.ledger());
    expect(l.balance, l.totalDebit - l.totalCredit);
    expect(l.reconciled, isTrue);
    expect(l.entries.items.first.type, LedgerEntryType.paymentReceived);
    expect(l.entries.hasNext, isFalse);
    expect(l.agreements.single.isHolder, isTrue);
    final a = AgreementLedger.fromJson(server.agreementLedger());
    expect(a.summary.paid, 600000);
    expect(a.summary.ledgerBalance, a.summary.outstanding);
  });

  test('payment detail, instructions and support are parsed', () {
    final d = PaymentDetail.fromJson(server.paymentDetail());
    expect(d.proofs.single.fileName, 'slip.png');
    expect(d.allocations.fold<double>(0, (a, x) => a + x.amount), d.allocated);
    final i = PaymentInstructions.fromJson(server.instructions());
    expect(i.methods.map((m) => m.kind), [PaymentMethodKind.bankTransfer, PaymentMethodKind.cash]);
    expect(i.bankAccounts.single.iban, startsWith('PK'));
    final s = SupportDetail.fromJson(server.supportDetail());
    expect(s.messages.map((m) => m.fromOffice), [false, true]);
    expect(s.request.status, SupportStatus.inProgress);
  });
}
