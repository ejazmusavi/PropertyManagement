import '../../../core/network/api_client.dart';
import '../../../core/util/json.dart';
import '../models/support_models.dart';

class SupportApi {
  SupportApi(this._client);
  final ApiClient _client;

  Future<List<SupportRequest>> requests() async => J.list(await _client.get('/portal/support-requests')).map(SupportRequest.fromJson).toList();

  Future<SupportDetail> detail(int id) async => SupportDetail.fromJson(J.map(await _client.get('/portal/support-requests/$id')));

  Future<SupportDetail> create(SupportCategory category, String subject, String message, int? agreementId) async => SupportDetail.fromJson(J.map(await _client.post(
      '/portal/support-requests', body: {'category': category.code, 'subject': subject, 'message': message, 'agreementId': agreementId})));

  Future<SupportDetail> reply(int id, String message) async =>
      SupportDetail.fromJson(J.map(await _client.post('/portal/support-requests/$id/messages', body: {'message': message})));
}
