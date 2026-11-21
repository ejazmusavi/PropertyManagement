import '../../../core/network/api_client.dart';
import '../../../core/util/paged.dart';
import '../models/payment_models.dart';
import 'payments_api.dart';

class PaymentsRepository {
  PaymentsRepository(this._api);
  final PaymentsApi _api;

  Future<Paged<PortalPayment>> history(int page) => _api.payments(page);
  Future<PaymentDetail> detail(int id) => _api.payment(id);
  Future<PaymentInstructions> instructions() => _api.instructions();
  Future<DownloadedFile> receipt(int receiptId) => _api.receipt(receiptId);
  Future<DownloadedFile> proof(int paymentId, int documentId) => _api.proof(paymentId, documentId);
  Future<PaymentDetail> submitProof(ProofSubmission s, UploadFile file) => _api.submitProof(s, file);
  Future<OnlinePayment> startOnline(int agreementId, double amount, String key) => _api.initiateOnline(agreementId, amount, key);
  Future<OnlinePayment> onlineStatus(int id) => _api.onlinePayment(id);
}
