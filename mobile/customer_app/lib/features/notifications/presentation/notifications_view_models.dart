import '../../../core/util/view_model.dart';
import '../data/notifications_repository.dart';
import '../models/notification_models.dart';

class NotificationsViewModel extends ViewModel {
  NotificationsViewModel(this._repository);
  final NotificationsRepository _repository;

  final List<NotificationItem> items = [];
  int unread = 0;
  int _page = 0;
  bool hasMore = false;

  Future<void> load({bool refresh = false}) => guard(() async {
        final result = await _repository.list(1);
        items
          ..clear()
          ..addAll(result.notifications.items);
        unread = result.unread;
        _page = 1;
        hasMore = result.notifications.hasNext;
      }, showLoading: !refresh || items.isEmpty);

  Future<void> loadMore() async {
    if (!hasMore) return;
    final result = await _repository.list(_page + 1);
    items.addAll(result.notifications.items);
    _page++;
    hasMore = result.notifications.hasNext;
    notifyListeners();
  }

  Future<void> markRead(NotificationItem n) async {
    if (n.isRead) return;
    await _repository.markRead(n.id);
    final i = items.indexWhere((x) => x.id == n.id);
    if (i >= 0) items[i] = n.asRead();
    unread = unread > 0 ? unread - 1 : 0;
    notifyListeners();
  }

  Future<void> markAllRead() async {
    await _repository.markAllRead();
    for (var i = 0; i < items.length; i++) {
      items[i] = items[i].asRead();
    }
    unread = 0;
    notifyListeners();
  }
}

class AnnouncementsViewModel extends ViewModel {
  AnnouncementsViewModel(this._repository);
  final NotificationsRepository _repository;

  List<Announcement> items = [];

  Future<void> load() => guard(() async => items = await _repository.announcements());
}
