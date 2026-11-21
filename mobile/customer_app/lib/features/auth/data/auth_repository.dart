import '../../../core/network/api_client.dart';
import '../../../core/network/api_exception.dart';
import '../../../core/util/json.dart';
import 'auth_api.dart';

/// Session handling: sign in, restore, secure sign out. Tokens are kept in the platform keystore by the [ApiClient].
class AuthRepository {
  AuthRepository(this._api, this._client);

  final AuthApi _api;
  final ApiClient _client;

  /// Restores a stored session. Returns true when the stored session can still be used (it is refreshed on the first request if needed).
  Future<bool> restore() async {
    await _client.restore();
    return _client.hasSession;
  }

  Future<void> signIn(String email, String password) async {
    final session = await _api.login(email.trim(), password);
    await _client.setSession(session);
    // The customer app is for customer accounts only: make sure the account holds the portal permission.
    final me = await _api.me();
    final permissions = (me['permissions'] as List? ?? const []).map((e) => e.toString()).toList();
    if (!permissions.contains('portal.access')) {
      await _clearQuietly();
      throw ApiException('This account cannot use the customer app. Please use the e-mail the office gave you.', statusCode: 403);
    }
    J.str(me['fullName']);
  }

  /// Ends the session on the server (revoking the refresh token) and always clears the device, even when offline.
  Future<void> signOut() async {
    final token = _client.refreshToken;
    try {
      if (token != null) await _api.logout(token);
    } on ApiException {
      // the device is cleared regardless; an unreachable server cannot keep the tokens alive on this phone
    } finally {
      await _client.clearSession();
    }
  }

  Future<void> forgotPassword(String email) => _api.forgotPassword(email.trim());

  Future<void> resetPassword(String email, String code, String newPassword) => _api.resetPassword(email.trim(), code.trim(), newPassword);

  Future<void> _clearQuietly() async {
    try {
      await _client.clearSession();
    } catch (_) {}
  }
}
