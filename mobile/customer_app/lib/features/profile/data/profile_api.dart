import '../../../core/network/api_client.dart';
import '../../../core/util/json.dart';
import '../models/profile.dart';

class ProfileApi {
  ProfileApi(this._client);
  final ApiClient _client;

  Future<Profile> profile() async => Profile.fromJson(J.map(await _client.get('/portal/profile')));

  Future<Profile> update(String mobile, String? email, String? address) async =>
      Profile.fromJson(J.map(await _client.put('/portal/profile', body: {'mobile': mobile, 'email': email, 'address': address})));

  Future<void> changePassword(String current, String next) => _client.post('/portal/profile/change-password', body: {'currentPassword': current, 'newPassword': next});
}
