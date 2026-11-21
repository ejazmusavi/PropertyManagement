import '../../../core/network/api_client.dart';
import '../../../core/util/json.dart';
import '../models/installment_models.dart';

class InstallmentsApi {
  InstallmentsApi(this._client);
  final ApiClient _client;

  Future<Schedule> schedule(int agreementId) async => Schedule.fromJson(J.map(await _client.get('/portal/agreements/$agreementId/schedule')));
}
