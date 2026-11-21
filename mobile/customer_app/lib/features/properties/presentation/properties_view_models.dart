import '../../../core/util/view_model.dart';
import '../data/properties_repository.dart';
import '../models/property_models.dart';

class PropertiesViewModel extends ViewModel {
  PropertiesViewModel(this._repository);
  final PropertiesRepository _repository;

  List<PortalProperty> items = [];

  Future<void> load({bool refresh = false}) => guard(() async => items = await _repository.list(), showLoading: !refresh || items.isEmpty);
}

class AgreementViewModel extends ViewModel {
  AgreementViewModel(this._repository, this.agreementId);
  final PropertiesRepository _repository;
  final int agreementId;

  AgreementDetail? agreement;

  Future<void> load() => guard(() async => agreement = await _repository.agreement(agreementId));
}
