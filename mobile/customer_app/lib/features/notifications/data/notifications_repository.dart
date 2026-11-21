import '../models/notification_models.dart';
import 'notifications_api.dart';

class NotificationsRepository {
  NotificationsRepository(this._api);
  final NotificationsApi _api;

  Future<NotificationList> list(int page) => _api.notifications(page);
  Future<void> markRead(int id) => _api.markRead(id);
  Future<void> markAllRead() => _api.markAllRead();
  Future<List<Announcement>> announcements() => _api.announcements();
}
