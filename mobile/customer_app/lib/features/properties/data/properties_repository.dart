import '../models/property_models.dart';
import 'properties_api.dart';

class PropertiesRepository {
  PropertiesRepository(this._api);
  final PropertiesApi _api;

  Future<List<PortalProperty>> list() => _api.properties();
  Future<AgreementDetail> agreement(int agreementId) => _api.agreement(agreementId);
}
