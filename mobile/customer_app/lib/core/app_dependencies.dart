import 'network/api_client.dart';
import 'storage/token_storage.dart';
import 'util/file_service.dart';
import '../features/auth/data/auth_api.dart';
import '../features/auth/data/auth_repository.dart';
import '../features/dashboard/data/dashboard_api.dart';
import '../features/dashboard/data/dashboard_repository.dart';
import '../features/installments/data/installments_api.dart';
import '../features/installments/data/installments_repository.dart';
import '../features/ledger/data/ledger_api.dart';
import '../features/ledger/data/ledger_repository.dart';
import '../features/notifications/data/notifications_api.dart';
import '../features/notifications/data/notifications_repository.dart';
import '../features/payments/data/payments_api.dart';
import '../features/payments/data/payments_repository.dart';
import '../features/profile/data/profile_api.dart';
import '../features/profile/data/profile_repository.dart';
import '../features/properties/data/properties_api.dart';
import '../features/properties/data/properties_repository.dart';
import '../features/support/data/support_api.dart';
import '../features/support/data/support_repository.dart';

/// Composition root: the API client and one repository per feature (each repository wraps that feature's API service).
class AppDependencies {
  AppDependencies({required TokenStorage storage, FileService? files, ApiClient? client})
      : api = client ?? ApiClient(storage: storage),
        files = files ?? DeviceFileService() {
    auth = AuthRepository(AuthApi(api), api);
    dashboard = DashboardRepository(DashboardApi(api));
    properties = PropertiesRepository(PropertiesApi(api));
    installments = InstallmentsRepository(InstallmentsApi(api));
    payments = PaymentsRepository(PaymentsApi(api));
    ledger = LedgerRepository(LedgerApi(api));
    notifications = NotificationsRepository(NotificationsApi(api));
    support = SupportRepository(SupportApi(api));
    profile = ProfileRepository(ProfileApi(api));
  }

  final ApiClient api;
  final FileService files;
  late final AuthRepository auth;
  late final DashboardRepository dashboard;
  late final PropertiesRepository properties;
  late final InstallmentsRepository installments;
  late final PaymentsRepository payments;
  late final LedgerRepository ledger;
  late final NotificationsRepository notifications;
  late final SupportRepository support;
  late final ProfileRepository profile;
}
