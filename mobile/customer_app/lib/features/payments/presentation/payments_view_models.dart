import 'dart:async';

import '../../../core/network/api_client.dart';
import '../../../core/network/api_exception.dart';
import '../../../core/util/file_service.dart';
import '../../../core/util/idempotency.dart';
import '../../../core/util/view_model.dart';
import '../data/payments_repository.dart';
import '../models/payment_models.dart';

class PaymentsViewModel extends ViewModel {
  PaymentsViewModel(this._repository);
  final PaymentsRepository _repository;

  final List<PortalPayment> items = [];
  int _page = 0;
  bool hasMore = false;
  bool loadingMore = false;

  Future<void> load({bool refresh = false}) => guard(() async {
        final result = await _repository.history(1);
        items
          ..clear()
          ..addAll(result.items);
        _page = 1;
        hasMore = result.hasNext;
      }, showLoading: !refresh || items.isEmpty);

  Future<void> loadMore() async {
    if (!hasMore || loadingMore) return;
    loadingMore = true;
    notifyListeners();
    try {
      final result = await _repository.history(_page + 1);
      items.addAll(result.items);
      _page++;
      hasMore = result.hasNext;
    } finally {
      loadingMore = false;
      notifyListeners();
    }
  }
}

class PaymentDetailViewModel extends ViewModel {
  PaymentDetailViewModel(this._repository, this._files, this.paymentId);
  final PaymentsRepository _repository;
  final FileService _files;
  final int paymentId;

  PaymentDetail? detail;
  bool downloading = false;
  String? downloadError;

  Future<void> load() => guard(() async => detail = await _repository.detail(paymentId));

  /// Downloads the receipt PDF from the server (authorized for the signed-in customer) and opens it.
  Future<bool> openReceipt() {
    final id = detail?.payment.receiptId;
    if (id == null) return Future.value(false);
    return _download(() => _repository.receipt(id));
  }

  Future<bool> openProof(PaymentProof proof) => _download(() => _repository.proof(paymentId, proof.id));

  Future<bool> _download(Future<DownloadedFile> Function() fetch) async {
    downloading = true;
    downloadError = null;
    notifyListeners();
    try {
      await _files.open(await fetch());
      return true;
    } on ApiException catch (e) {
      downloadError = e.displayMessage;
      return false;
    } on FileOpenException {
      downloadError = 'No app on this device can open the file.';
      return false;
    } finally {
      downloading = false;
      notifyListeners();
    }
  }
}

class InstructionsViewModel extends ViewModel {
  InstructionsViewModel(this._repository);
  final PaymentsRepository _repository;

  PaymentInstructions? instructions;

  Future<void> load() => guard(() async => instructions = await _repository.instructions());
}

/// Payment proof form. The idempotency key is created once with the form, so a retry after a lost response returns the first submission.
class SubmitProofViewModel extends ViewModel {
  SubmitProofViewModel(this._repository) : _key = newIdempotencyKey();
  final PaymentsRepository _repository;
  final String _key;

  PaymentInstructions? instructions;
  bool submitting = false;
  String? submitError;
  PaymentDetail? result;

  Future<void> load() => guard(() async => instructions = await _repository.instructions());

  Future<bool> submit({required int agreementId, required int methodId, required double amount, required DateTime date, String? reference, String? bankName, String? remarks, required UploadFile file}) async {
    submitting = true;
    submitError = null;
    notifyListeners();
    try {
      result = await _repository.submitProof(
          ProofSubmission(agreementId: agreementId, methodId: methodId, amount: amount, paymentDate: date, reference: reference, bankName: bankName, remarks: remarks, idempotencyKey: _key), file);
      return true;
    } on ApiException catch (e) {
      submitError = e.displayMessage;
      return false;
    } finally {
      submitting = false;
      notifyListeners();
    }
  }
}

/// Online payment (only when the company enabled a provider). The key is stable per screen; the status is polled until it is final.
class OnlinePaymentViewModel extends ViewModel {
  OnlinePaymentViewModel(this._repository) : _key = newIdempotencyKey();
  final PaymentsRepository _repository;
  final String _key;

  OnlinePayment? payment;
  bool starting = false;
  String? startError;
  Timer? _timer;

  Future<bool> start(int agreementId, double amount) async {
    starting = true;
    startError = null;
    notifyListeners();
    try {
      payment = await _repository.startOnline(agreementId, amount, _key);
      return true;
    } on ApiException catch (e) {
      startError = e.displayMessage;
      return false;
    } finally {
      starting = false;
      notifyListeners();
    }
  }

  Future<void> refreshStatus() async {
    final p = payment;
    if (p == null) return;
    try {
      payment = await _repository.onlineStatus(p.id);
      notifyListeners();
    } on ApiException {
      // keep the last status; the next poll tries again
    }
  }

  void startPolling() {
    _timer?.cancel();
    _timer = Timer.periodic(const Duration(seconds: 5), (t) async {
      await refreshStatus();
      if (payment?.status.isFinal ?? false) t.cancel();
    });
  }

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }
}
