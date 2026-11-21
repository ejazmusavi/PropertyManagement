import '../models/profile.dart';
import 'profile_api.dart';

class ProfileRepository {
  ProfileRepository(this._api);
  final ProfileApi _api;

  Future<Profile> load() => _api.profile();
  Future<Profile> update(String mobile, String? email, String? address) => _api.update(mobile, email, address);
  Future<void> changePassword(String current, String next) => _api.changePassword(current, next);
}
