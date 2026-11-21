import '../../../core/util/json.dart';

enum PaymentStatus {
  pendingVerification(1, 'Pending verification'),
  pendingApproval(2, 'Pending approval'),
  posted(3, 'Received'),
  rejected(4, 'Rejected'),
  reversed(5, 'Reversed');

  const PaymentStatus(this.code, this.label);
  final int code;
  final String label;
}

enum PaymentMethodKind { cash, bankTransfer, cheque, onlineTransfer, card, paymentGateway }

enum GatewayStatus {
  initiated(1, 'Started'),
  pending(2, 'Waiting for the provider'),
  succeeded(3, 'Confirmed'),
  failed(4, 'Failed'),
  cancelled(5, 'Cancelled'),
  refunded(6, 'Refunded');

  const GatewayStatus(this.code, this.label);
  final int code;
  final String label;

  bool get isFinal => this == succeeded || this == failed || this == cancelled || this == refunded;
}

class PortalPayment {
  PortalPayment({required this.id, required this.paymentNumber, required this.agreementId, required this.agreementNumber, required this.amount, required this.currencyCode, required this.paymentDate, required this.methodName, required this.reference, required this.status, required this.customerSubmitted, required this.submittedAt, required this.receiptId, required this.receiptNumber, required this.rejectReason, required this.proofCount});

  factory PortalPayment.fromJson(Map<String, dynamic> j) => PortalPayment(
        id: J.integer(j['id']),
        paymentNumber: J.str(j['paymentNumber']),
        agreementId: J.integer(j['agreementId']),
        agreementNumber: J.str(j['agreementNumber']),
        amount: J.money(j['amount']),
        currencyCode: J.str(j['currencyCode']),
        paymentDate: J.date(j['paymentDate']),
        methodName: J.str(j['methodName']),
        reference: J.strOrNull(j['reference']),
        status: enumFrom(PaymentStatus.values, (e) => e.code, j['status'], PaymentStatus.pendingVerification),
        customerSubmitted: J.integer(j['source']) == 2,
        submittedAt: J.date(j['submittedAtUtc']),
        receiptId: j['receiptId'] == null ? null : J.integer(j['receiptId']),
        receiptNumber: J.strOrNull(j['receiptNumber']),
        rejectReason: J.strOrNull(j['rejectReason']),
        proofCount: J.integer(j['proofCount']),
      );

  final int id;
  final String paymentNumber;
  final int agreementId;
  final String agreementNumber;
  final double amount;
  final String currencyCode;
  final DateTime paymentDate;
  final String methodName;
  final String? reference;
  final PaymentStatus status;
  final bool customerSubmitted;
  final DateTime submittedAt;
  final int? receiptId;
  final String? receiptNumber;
  final String? rejectReason;
  final int proofCount;

  bool get hasReceipt => receiptId != null && (status == PaymentStatus.posted || status == PaymentStatus.reversed);
}

class PaymentAllocation {
  PaymentAllocation({required this.description, required this.dueDate, required this.amount});

  factory PaymentAllocation.fromJson(Map<String, dynamic> j) => PaymentAllocation(description: J.str(j['description']), dueDate: J.dateOrNull(j['dueDate']), amount: J.money(j['amount']));

  final String description;
  final DateTime? dueDate;
  final double amount;
}

class PaymentProof {
  PaymentProof({required this.id, required this.fileName, required this.sizeBytes, required this.uploadedAt});

  factory PaymentProof.fromJson(Map<String, dynamic> j) =>
      PaymentProof(id: J.integer(j['id']), fileName: J.str(j['fileName']), sizeBytes: J.integer(j['sizeBytes']), uploadedAt: J.date(j['uploadedAtUtc']));

  final int id;
  final String fileName;
  final int sizeBytes;
  final DateTime uploadedAt;
}

class PaymentDetail {
  PaymentDetail({required this.payment, required this.currencySymbol, required this.allocated, required this.advance, required this.unallocated, required this.allocations, required this.proofs, required this.note});

  factory PaymentDetail.fromJson(Map<String, dynamic> j) => PaymentDetail(
        payment: PortalPayment.fromJson(J.map(j['payment'])),
        currencySymbol: J.str(j['currencySymbol']),
        allocated: J.money(j['allocated']),
        advance: J.money(j['advance']),
        unallocated: J.money(j['unallocated']),
        allocations: J.list(j['allocations']).map(PaymentAllocation.fromJson).toList(),
        proofs: J.list(j['proofs']).map(PaymentProof.fromJson).toList(),
        note: J.strOrNull(j['verificationNote']),
      );

  final PortalPayment payment;
  final String currencySymbol;
  final double allocated;
  final double advance;
  final double unallocated;
  final List<PaymentAllocation> allocations;
  final List<PaymentProof> proofs;
  final String? note;
}

class PaymentMethod {
  PaymentMethod({required this.id, required this.code, required this.name, required this.kind, required this.requiresReference});

  factory PaymentMethod.fromJson(Map<String, dynamic> j) => PaymentMethod(
        id: J.integer(j['id']),
        code: J.str(j['code']),
        name: J.str(j['name']),
        kind: PaymentMethodKind.values[(J.integer(j['kind']) - 1).clamp(0, PaymentMethodKind.values.length - 1)],
        requiresReference: J.boolean(j['requiresReference']),
      );

  final int id;
  final String code;
  final String name;
  final PaymentMethodKind kind;
  final bool requiresReference;
}

class BankAccount {
  BankAccount({required this.bankName, required this.branch, required this.accountTitle, required this.accountNumber, required this.iban});

  factory BankAccount.fromJson(Map<String, dynamic> j) => BankAccount(
        bankName: J.str(j['bankName']),
        branch: J.strOrNull(j['branch']),
        accountTitle: J.str(j['accountTitle']),
        accountNumber: J.str(j['accountNumber']),
        iban: J.strOrNull(j['iban']),
      );

  final String bankName;
  final String? branch;
  final String accountTitle;
  final String accountNumber;
  final String? iban;
}

class PaymentInstructions {
  PaymentInstructions({required this.note, required this.bankAccounts, required this.methods, required this.onlinePaymentsEnabled});

  factory PaymentInstructions.fromJson(Map<String, dynamic> j) => PaymentInstructions(
        note: J.strOrNull(j['note']),
        bankAccounts: J.list(j['bankAccounts']).map(BankAccount.fromJson).toList(),
        methods: J.list(j['methods']).map(PaymentMethod.fromJson).toList(),
        onlinePaymentsEnabled: J.boolean(j['onlinePaymentsEnabled']),
      );

  final String? note;
  final List<BankAccount> bankAccounts;
  final List<PaymentMethod> methods;
  final bool onlinePaymentsEnabled;
}

class OnlinePayment {
  OnlinePayment({required this.id, required this.transactionNumber, required this.agreementId, required this.amount, required this.currencyCode, required this.status, required this.redirectUrl, required this.failureReason, required this.paymentNumber});

  factory OnlinePayment.fromJson(Map<String, dynamic> j) => OnlinePayment(
        id: J.integer(j['id']),
        transactionNumber: J.str(j['transactionNumber']),
        agreementId: J.integer(j['agreementId']),
        amount: J.money(j['amount']),
        currencyCode: J.str(j['currencyCode']),
        status: enumFrom(GatewayStatus.values, (e) => e.code, j['status'], GatewayStatus.pending),
        redirectUrl: J.strOrNull(j['redirectUrl']),
        failureReason: J.strOrNull(j['failureReason']),
        paymentNumber: J.strOrNull(j['paymentNumber']),
      );

  final int id;
  final String transactionNumber;
  final int agreementId;
  final double amount;
  final String currencyCode;
  final GatewayStatus status;
  final String? redirectUrl;
  final String? failureReason;
  final String? paymentNumber;
}

/// Values of the payment proof form (the customer is never part of the request: the server knows who is signed in).
class ProofSubmission {
  ProofSubmission({required this.agreementId, required this.methodId, required this.amount, required this.paymentDate, this.reference, this.bankName, this.remarks, required this.idempotencyKey});

  final int agreementId;
  final int methodId;
  final double amount;
  final DateTime paymentDate;
  final String? reference;
  final String? bankName;
  final String? remarks;
  final String idempotencyKey;
}
