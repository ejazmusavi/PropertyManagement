import 'package:flutter/foundation.dart';

import '../../../core/network/api_exception.dart';
import '../data/auth_repository.dart';

enum AuthState { unknown, signedOut, signedIn }

/// Owns the authentication state of the whole app (the router listens to it).
class AuthViewModel extends ChangeNotifier {
  AuthViewModel(this._repository);

  final AuthRepository _repository;

  AuthState state = AuthState.unknown;
  bool busy = false;
  String? error;
  String? notice;

  Future<void> start() async {
    state = await _repository.restore() ? AuthState.signedIn : AuthState.signedOut;
    notifyListeners();
  }

  /// Called by the API client when the refresh token is rejected.
  void sessionExpired() {
    if (state == AuthState.signedOut) return;
    state = AuthState.signedOut;
    notice = 'Your session has expired. Please sign in again.';
    notifyListeners();
  }

  Future<bool> signIn(String email, String password) async {
    busy = true;
    error = null;
    notice = null;
    notifyListeners();
    try {
      await _repository.signIn(email, password);
      state = AuthState.signedIn;
      return true;
    } on ApiException catch (e) {
      error = e.displayMessage;
      return false;
    } finally {
      busy = false;
      notifyListeners();
    }
  }

  Future<void> signOut() async {
    busy = true;
    notifyListeners();
    await _repository.signOut();
    busy = false;
    state = AuthState.signedOut;
    notice = null;
    error = null;
    notifyListeners();
  }

  /// Requests a reset code. The server answers the same way for unknown e-mails.
  Future<bool> requestReset(String email) => _run(() => _repository.forgotPassword(email));

  Future<bool> resetPassword(String email, String code, String newPassword) => _run(() => _repository.resetPassword(email, code, newPassword));

  Future<bool> _run(Future<void> Function() action) async {
    busy = true;
    error = null;
    notifyListeners();
    try {
      await action();
      return true;
    } on ApiException catch (e) {
      error = e.displayMessage;
      return false;
    } finally {
      busy = false;
      notifyListeners();
    }
  }

  void clearError() {
    error = null;
    notifyListeners();
  }
}
