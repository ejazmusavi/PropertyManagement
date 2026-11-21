import '../../../core/network/api_client.dart';
import '../../../core/util/json.dart';
import '../models/dashboard.dart';

class DashboardApi {
  DashboardApi(this._client);
  final ApiClient _client;

  Future<Dashboard> dashboard() async => Dashboard.fromJson(J.map(await _client.get('/portal/dashboard')));
}
