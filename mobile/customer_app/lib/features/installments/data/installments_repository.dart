import '../models/installment_models.dart';
import 'installments_api.dart';

class InstallmentsRepository {
  InstallmentsRepository(this._api);
  final InstallmentsApi _api;

  Future<Schedule> schedule(int agreementId) => _api.schedule(agreementId);
}
