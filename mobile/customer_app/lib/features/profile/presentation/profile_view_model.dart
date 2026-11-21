import '../../../core/network/api_exception.dart';
import '../../../core/util/view_model.dart';
import '../data/profile_repository.dart';
import '../models/profile.dart';

class ProfileViewModel extends ViewModel {
  ProfileViewModel(this._repository);
  final ProfileRepository _repository;

  Profile? profile;
  bool saving = false;
  String? saveError;

  Future<void> load() => guard(() async => profile = await _repository.load());

  Future<bool> save(String mobile, String? email, String? address) => _save(() async => profile = await _repository.update(mobile, email, address));

  Future<bool> changePassword(String current, String next) => _save(() => _repository.changePassword(current, next));

  Future<bool> _save(Future<void> Function() action) async {
    saving = true;
    saveError = null;
    notifyListeners();
    try {
      await action();
      return true;
    } on ApiException catch (e) {
      saveError = e.displayMessage;
      return false;
    } finally {
      saving = false;
      notifyListeners();
    }
  }
}
