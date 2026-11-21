import '../../../core/network/api_client.dart';
import '../../../core/util/json.dart';
import '../models/property_models.dart';

class PropertiesApi {
  PropertiesApi(this._client);
  final ApiClient _client;

  Future<List<PortalProperty>> properties() async => J.list(await _client.get('/portal/properties')).map(PortalProperty.fromJson).toList();

  Future<AgreementDetail> agreement(int agreementId) async => AgreementDetail.fromJson(J.map(await _client.get('/portal/agreements/$agreementId')));
}
