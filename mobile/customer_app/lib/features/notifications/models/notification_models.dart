import '../../../core/util/json.dart';
import '../../../core/util/paged.dart';

class NotificationItem {
  NotificationItem({required this.id, required this.title, required this.message, required this.kind, required this.createdAt, required this.isRead});

  factory NotificationItem.fromJson(Map<String, dynamic> j) => NotificationItem(
        id: J.integer(j['id']),
        title: J.str(j['title']),
        message: J.str(j['message']),
        kind: J.integer(j['kind']),
        createdAt: J.date(j['createdAtUtc']),
        isRead: J.boolean(j['isRead']),
      );

  final int id;
  final String title;
  final String message;

  /// 1 general, 2 payment, 3 support, 4 account.
  final int kind;
  final DateTime createdAt;
  final bool isRead;

  NotificationItem asRead() => NotificationItem(id: id, title: title, message: message, kind: kind, createdAt: createdAt, isRead: true);
}

class NotificationList {
  NotificationList(this.notifications, this.unread);

  factory NotificationList.fromJson(Map<String, dynamic> j) => NotificationList(Paged.from(J.map(j['notifications']), NotificationItem.fromJson), J.integer(j['unread']));

  final Paged<NotificationItem> notifications;
  final int unread;
}

class Announcement {
  Announcement({required this.id, required this.title, required this.body, required this.publishedAt, required this.expiresAt});

  factory Announcement.fromJson(Map<String, dynamic> j) => Announcement(
        id: J.integer(j['id']),
        title: J.str(j['title']),
        body: J.str(j['body']),
        publishedAt: J.date(j['publishedAtUtc']),
        expiresAt: J.dateOrNull(j['expiresAtUtc']),
      );

  final int id;
  final String title;
  final String body;
  final DateTime publishedAt;
  final DateTime? expiresAt;
}
