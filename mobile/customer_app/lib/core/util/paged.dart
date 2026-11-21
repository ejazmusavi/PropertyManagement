import 'json.dart';

/// A page of results (`PagedResult` of the API).
class Paged<T> {
  Paged(this.items, this.page, this.pageSize, this.totalCount);

  final List<T> items;
  final int page;
  final int pageSize;
  final int totalCount;

  bool get hasNext => page * pageSize < totalCount;

  static Paged<T> from<T>(Map<String, dynamic> json, T Function(Map<String, dynamic>) map) =>
      Paged(J.list(json['items']).map(map).toList(), J.integer(json['page']), J.integer(json['pageSize']), J.integer(json['totalCount']));
}
