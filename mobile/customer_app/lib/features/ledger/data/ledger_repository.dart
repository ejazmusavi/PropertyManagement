import '../models/ledger_models.dart';
import 'ledger_api.dart';

class LedgerRepository {
  LedgerRepository(this._api);
  final LedgerApi _api;

  Future<CustomerLedger> customer(int page) => _api.customerLedger(page);
  Future<AgreementLedger> agreement(int agreementId, int page) => _api.agreementLedger(agreementId, page);
}
