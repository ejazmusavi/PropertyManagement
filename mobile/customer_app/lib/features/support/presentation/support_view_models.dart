import '../../../core/network/api_exception.dart';
import '../../../core/util/view_model.dart';
import '../data/support_repository.dart';
import '../models/support_models.dart';

class SupportListViewModel extends ViewModel {
  SupportListViewModel(this._repository);
  final SupportRepository _repository;

  List<SupportRequest> items = [];

  Future<void> load({bool refresh = false}) => guard(() async => items = await _repository.list(), showLoading: !refresh || items.isEmpty);
}

class SupportDetailViewModel extends ViewModel {
  SupportDetailViewModel(this._repository, this.id);
  final SupportRepository _repository;
  final int id;

  SupportDetail? detail;
  bool sending = false;
  String? sendError;

  bool get canReply => detail != null && detail!.request.status != SupportStatus.closed;

  Future<void> load() => guard(() async => detail = await _repository.detail(id));

  Future<bool> reply(String message) async {
    sending = true;
    sendError = null;
    notifyListeners();
    try {
      detail = await _repository.reply(id, message);
      return true;
    } on ApiException catch (e) {
      sendError = e.displayMessage;
      return false;
    } finally {
      sending = false;
      notifyListeners();
    }
  }
}

class NewSupportRequestViewModel extends ViewModel {
  NewSupportRequestViewModel(this._repository);
  final SupportRepository _repository;

  bool saving = false;
  String? saveError;

  Future<SupportDetail?> create(SupportCategory category, String subject, String message, int? agreementId) async {
    saving = true;
    saveError = null;
    notifyListeners();
    try {
      return await _repository.create(category, subject, message, agreementId);
    } on ApiException catch (e) {
      saveError = e.displayMessage;
      return null;
    } finally {
      saving = false;
      notifyListeners();
    }
  }
}
