import '../../../core/network/api_client.dart';
import '../../../core/storage/token_storage.dart';
import '../../../core/util/json.dart';

/// Authentication endpoints (`/auth/*`).
class AuthApi {
  AuthApi(this._client);
  final ApiClient _client;

  Future<StoredSession> login(String email, String password) async {
    final json = await _client.post('/auth/login', body: {'email': email, 'password': password}, auth: false);
    return ApiClient.sessionFromJson(J.map(json));
  }

  Future<void> logout(String refreshToken) => _client.post('/auth/logout', body: {'refreshToken': refreshToken});

  Future<void> forgotPassword(String email) => _client.post('/auth/forgot-password', body: {'email': email}, auth: false);

  Future<void> resetPassword(String email, String token, String newPassword) =>
      _client.post('/auth/reset-password', body: {'email': email, 'token': token, 'newPassword': newPassword}, auth: false);

  Future<Map<String, dynamic>> me() async => J.map(await _client.get('/auth/me'));
}
