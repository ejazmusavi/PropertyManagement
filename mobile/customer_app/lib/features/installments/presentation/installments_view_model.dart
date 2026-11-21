import '../../../core/util/view_model.dart';
import '../data/installments_repository.dart';
import '../models/installment_models.dart';

enum InstallmentFilter { all, outstanding, overdue, paid }

class InstallmentsViewModel extends ViewModel {
  InstallmentsViewModel(this._repository, this.agreementId);
  final InstallmentsRepository _repository;
  final int agreementId;

  Schedule? schedule;
  InstallmentFilter filter = InstallmentFilter.all;

  List<Installment> get visible {
    final all = schedule?.items ?? const <Installment>[];
    return switch (filter) {
      InstallmentFilter.all => all,
      InstallmentFilter.outstanding => all.where((i) => i.outstanding > 0).toList(),
      InstallmentFilter.overdue => all.where((i) => i.status == InstallmentStatus.overdue).toList(),
      InstallmentFilter.paid => all.where((i) => i.status == InstallmentStatus.paid || i.status == InstallmentStatus.waived).toList(),
    };
  }

  void setFilter(InstallmentFilter f) {
    filter = f;
    notifyListeners();
  }

  Future<void> load({bool refresh = false}) => guard(() async => schedule = await _repository.schedule(agreementId), showLoading: !refresh || schedule == null);
}
