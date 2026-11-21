import '../models/support_models.dart';
import 'support_api.dart';

class SupportRepository {
  SupportRepository(this._api);
  final SupportApi _api;

  Future<List<SupportRequest>> list() => _api.requests();
  Future<SupportDetail> detail(int id) => _api.detail(id);
  Future<SupportDetail> create(SupportCategory c, String subject, String message, int? agreementId) => _api.create(c, subject, message, agreementId);
  Future<SupportDetail> reply(int id, String message) => _api.reply(id, message);
}
