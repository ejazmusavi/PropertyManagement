import '../../../core/network/api_client.dart';
import '../../../core/util/json.dart';
import '../models/notification_models.dart';

class NotificationsApi {
  NotificationsApi(this._client);
  final ApiClient _client;

  Future<NotificationList> notifications(int page) async =>
      NotificationList.fromJson(J.map(await _client.get('/portal/notifications', query: {'page': '$page', 'pageSize': '30'})));

  Future<void> markRead(int id) => _client.post('/portal/notifications/$id/read');
  Future<void> markAllRead() => _client.post('/portal/notifications/read-all');

  Future<List<Announcement>> announcements() async => J.list(await _client.get('/portal/announcements')).map(Announcement.fromJson).toList();
}
