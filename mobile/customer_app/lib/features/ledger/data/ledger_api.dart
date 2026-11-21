import '../../../core/network/api_client.dart';
import '../../../core/util/json.dart';
import '../models/ledger_models.dart';

class LedgerApi {
  LedgerApi(this._client);
  final ApiClient _client;

  Future<CustomerLedger> customerLedger(int page) async =>
      CustomerLedger.fromJson(J.map(await _client.get('/portal/ledger', query: {'page': '$page', 'pageSize': '30'})));

  Future<AgreementLedger> agreementLedger(int agreementId, int page) async =>
      AgreementLedger.fromJson(J.map(await _client.get('/portal/agreements/$agreementId/ledger', query: {'page': '$page', 'pageSize': '30'})));
}
