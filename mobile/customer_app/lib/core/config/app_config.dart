/// Build-time configuration. Override with `--dart-define=API_BASE_URL=https://erp.example.com`.
/// The default targets the API running on the development machine as seen from the Android emulator.
class AppConfig {
  static const String apiBaseUrl = String.fromEnvironment('API_BASE_URL', defaultValue: 'http://10.0.2.2:5008');
  static const String apiPrefix = '/api/v1';
  static const Duration requestTimeout = Duration(seconds: 30);
}
