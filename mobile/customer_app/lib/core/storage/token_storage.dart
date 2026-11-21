import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Tokens of the signed-in customer.
class StoredSession {
  const StoredSession({required this.accessToken, required this.accessExpiresAt, required this.refreshToken, required this.refreshExpiresAt});

  final String accessToken;
  final DateTime accessExpiresAt;
  final String refreshToken;
  final DateTime refreshExpiresAt;

  bool get accessValid => accessExpiresAt.isAfter(DateTime.now().toUtc().add(const Duration(seconds: 30)));
  bool get refreshValid => refreshExpiresAt.isAfter(DateTime.now().toUtc());
}

/// Where the session is persisted. The production implementation uses the platform keystore.
abstract class TokenStorage {
  Future<StoredSession?> read();
  Future<void> write(StoredSession session);
  Future<void> clear();
}

class SecureTokenStorage implements TokenStorage {
  SecureTokenStorage([FlutterSecureStorage? storage]) : _storage = storage ?? const FlutterSecureStorage();

  final FlutterSecureStorage _storage;
  static const _access = 'access_token';
  static const _accessExp = 'access_expires';
  static const _refresh = 'refresh_token';
  static const _refreshExp = 'refresh_expires';

  @override
  Future<StoredSession?> read() async {
    final a = await _storage.read(key: _access);
    final ae = await _storage.read(key: _accessExp);
    final r = await _storage.read(key: _refresh);
    final re = await _storage.read(key: _refreshExp);
    if (a == null || ae == null || r == null || re == null) return null;
    final accessExp = DateTime.tryParse(ae);
    final refreshExp = DateTime.tryParse(re);
    if (accessExp == null || refreshExp == null) return null;
    return StoredSession(accessToken: a, accessExpiresAt: accessExp.toUtc(), refreshToken: r, refreshExpiresAt: refreshExp.toUtc());
  }

  @override
  Future<void> write(StoredSession s) async {
    await _storage.write(key: _access, value: s.accessToken);
    await _storage.write(key: _accessExp, value: s.accessExpiresAt.toUtc().toIso8601String());
    await _storage.write(key: _refresh, value: s.refreshToken);
    await _storage.write(key: _refreshExp, value: s.refreshExpiresAt.toUtc().toIso8601String());
  }

  @override
  Future<void> clear() async {
    for (final k in [_access, _accessExp, _refresh, _refreshExp]) {
      await _storage.delete(key: k);
    }
  }
}

/// In-memory storage for tests.
class MemoryTokenStorage implements TokenStorage {
  StoredSession? value;

  @override
  Future<StoredSession?> read() async => value;

  @override
  Future<void> write(StoredSession session) async => value = session;

  @override
  Future<void> clear() async => value = null;
}
