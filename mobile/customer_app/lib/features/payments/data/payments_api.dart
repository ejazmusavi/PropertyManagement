import '../../../core/network/api_client.dart';
import '../../../core/util/format_for_api.dart';
import '../../../core/util/json.dart';
import '../../../core/util/paged.dart';
import '../models/payment_models.dart';

class PaymentsApi {
  PaymentsApi(this._client);
  final ApiClient _client;

  Future<Paged<PortalPayment>> payments(int page) async =>
      Paged.from(J.map(await _client.get('/portal/payments', query: {'page': '$page', 'pageSize': '20'})), PortalPayment.fromJson);

  Future<PaymentDetail> payment(int id) async => PaymentDetail.fromJson(J.map(await _client.get('/portal/payments/$id')));

  Future<PaymentInstructions> instructions() async => PaymentInstructions.fromJson(J.map(await _client.get('/portal/payment-instructions')));

  Future<DownloadedFile> receipt(int receiptId) => _client.download('/portal/receipts/$receiptId/pdf');

  Future<DownloadedFile> proof(int paymentId, int documentId) => _client.download('/portal/payments/$paymentId/proofs/$documentId');

  Future<PaymentDetail> submitProof(ProofSubmission s, UploadFile file) async {
    final fields = <String, String>{
      'AgreementId': '${s.agreementId}',
      'PaymentMethodId': '${s.methodId}',
      'Amount': FormatForApi.amount(s.amount),
      'PaymentDate': FormatForApi.date(s.paymentDate),
      'IdempotencyKey': s.idempotencyKey,
      if (s.reference != null && s.reference!.isNotEmpty) 'Reference': s.reference!,
      if (s.bankName != null && s.bankName!.isNotEmpty) 'BankName': s.bankName!,
      if (s.remarks != null && s.remarks!.isNotEmpty) 'Remarks': s.remarks!,
    };
    return PaymentDetail.fromJson(J.map(await _client.postMultipart('/portal/payments', fields: fields, fileField: 'proof', file: file)));
  }

  Future<OnlinePayment> initiateOnline(int agreementId, double amount, String idempotencyKey) async => OnlinePayment.fromJson(
      J.map(await _client.post('/portal/online-payments', body: {'agreementId': agreementId, 'amount': amount, 'idempotencyKey': idempotencyKey})));

  Future<OnlinePayment> onlinePayment(int id) async => OnlinePayment.fromJson(J.map(await _client.get('/portal/online-payments/$id')));
}
