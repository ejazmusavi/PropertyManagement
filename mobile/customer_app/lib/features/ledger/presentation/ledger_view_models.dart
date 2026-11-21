import '../../../core/util/view_model.dart';
import '../data/ledger_repository.dart';
import '../models/ledger_models.dart';

/// Customer statement. Entries load page by page; the summary comes from the first page.
class LedgerViewModel extends ViewModel {
  LedgerViewModel(this._repository);
  final LedgerRepository _repository;

  CustomerLedger? ledger;
  final List<LedgerEntry> entries = [];
  int _page = 0;
  bool hasMore = false;
  bool loadingMore = false;

  Future<void> load({bool refresh = false}) => guard(() async {
        final result = await _repository.customer(1);
        ledger = result;
        entries
          ..clear()
          ..addAll(result.entries.items);
        _page = 1;
        hasMore = result.entries.hasNext;
      }, showLoading: !refresh || ledger == null);

  Future<void> loadMore() async {
    if (!hasMore || loadingMore) return;
    loadingMore = true;
    notifyListeners();
    try {
      final result = await _repository.customer(_page + 1);
      entries.addAll(result.entries.items);
      _page++;
      hasMore = result.entries.hasNext;
    } finally {
      loadingMore = false;
      notifyListeners();
    }
  }
}

/// Statement of one agreement (transaction history and balances).
class AgreementLedgerViewModel extends ViewModel {
  AgreementLedgerViewModel(this._repository, this.agreementId);
  final LedgerRepository _repository;
  final int agreementId;

  AgreementLedger? ledger;
  final List<LedgerEntry> entries = [];
  int _page = 0;
  bool hasMore = false;
  bool loadingMore = false;

  Future<void> load() => guard(() async {
        final result = await _repository.agreement(agreementId, 1);
        ledger = result;
        entries
          ..clear()
          ..addAll(result.entries.items);
        _page = 1;
        hasMore = result.entries.hasNext;
      });

  Future<void> loadMore() async {
    if (!hasMore || loadingMore) return;
    loadingMore = true;
    notifyListeners();
    try {
      final result = await _repository.agreement(agreementId, _page + 1);
      entries.addAll(result.entries.items);
      _page++;
      hasMore = result.entries.hasNext;
    } finally {
      loadingMore = false;
      notifyListeners();
    }
  }
}
