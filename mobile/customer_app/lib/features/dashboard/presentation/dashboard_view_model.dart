import '../../../core/util/view_model.dart';
import '../data/dashboard_repository.dart';
import '../models/dashboard.dart';

class DashboardViewModel extends ViewModel {
  DashboardViewModel(this._repository);
  final DashboardRepository _repository;

  Dashboard? data;

  Future<void> load({bool refresh = false}) => guard(() async => data = await _repository.load(), showLoading: !refresh || data == null);
}
