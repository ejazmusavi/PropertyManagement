import '../models/dashboard.dart';
import 'dashboard_api.dart';

class DashboardRepository {
  DashboardRepository(this._api);
  final DashboardApi _api;

  Future<Dashboard> load() => _api.dashboard();
}
