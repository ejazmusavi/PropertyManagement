import 'package:flutter/foundation.dart';

import '../network/api_exception.dart';

enum LoadStatus { idle, loading, loaded, error }

/// Base of every screen state holder: one consistent loading / error / data pattern (state management: provider + ChangeNotifier).
abstract class ViewModel extends ChangeNotifier {
  LoadStatus status = LoadStatus.idle;
  String? error;
  bool _disposed = false;

  bool get isLoading => status == LoadStatus.loading;
  bool get hasError => status == LoadStatus.error;

  @protected
  Future<void> guard(Future<void> Function() action, {bool showLoading = true}) async {
    if (showLoading) {
      status = LoadStatus.loading;
      error = null;
      notifyListeners();
    }
    try {
      await action();
      status = LoadStatus.loaded;
      error = null;
    } on ApiException catch (e) {
      status = LoadStatus.error;
      error = e.displayMessage;
    } catch (e) {
      status = LoadStatus.error;
      error = 'Something went wrong. Please try again.';
    }
    notifyListeners();
  }

  @override
  void notifyListeners() {
    if (!_disposed) super.notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    super.dispose();
  }
}
